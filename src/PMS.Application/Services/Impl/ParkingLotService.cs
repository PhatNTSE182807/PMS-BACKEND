using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Helpers;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.Application.Models.ParkingLot;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

public class ParkingLotService(PmsDbContext pmsContext, ILotRealtimeNotifier notifier = null) : IParkingLotService
{
    public async Task<IEnumerable<ParkingLotResponseModel>> GetAllAsync(string searchTerm = null, string status = null, bool includeDeleted = false)
    {
        var query = pmsContext.ParkingLots.AsQueryable();

        if (!includeDeleted)
        {
            query = query.Where(p => !p.IsDeleted);
        }

        // The status filter matches what the list shows: an open lot without people on duty counts as Maintenance
        var statusFilter = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
        if (statusFilter == LotStatus.Active || statusFilter == LotStatus.Maintenance)
        {
            query = query.Where(p => p.Status == LotStatus.Active || p.Status == statusFilter);
        }
        else if (statusFilter != null)
        {
            query = query.Where(p => p.Status == statusFilter);
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.Trim();
            query = query.Where(p =>
                p.Code.Contains(term) ||
                p.Name.Contains(term) ||
                p.Address.Contains(term) ||
                (p.City != null && p.City.Contains(term)) ||
                (p.District != null && p.District.Contains(term)));
        }

        var lots = await query
            .Include(p => p.Zones)
                .ThenInclude(z => z.Slots)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

        var staffed = await LotStaffing.StaffedLotIdsAsync(pmsContext, lots.Select(p => p.Id));
        var models = lots.Select(p => MapToResponseModel(p, staffed.Contains(p.Id)));
        return statusFilter == null ? models.ToList() : models.Where(m => m.Status == statusFilter).ToList();
    }

    public async Task<IEnumerable<PublicParkingLotResponseModel>> GetPublicLotsAsync(string searchTerm = null)
    {
        var query = pmsContext.ParkingLots
            // Lots under maintenance stay visible so customers can look at them; they just cannot be booked
            .Where(p => !p.IsDeleted && (p.Status == LotStatus.Active || p.Status == LotStatus.Maintenance));

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.Trim();
            query = query.Where(p =>
                p.Code.Contains(term) ||
                p.Name.Contains(term) ||
                p.Address.Contains(term) ||
                (p.City != null && p.City.Contains(term)) ||
                (p.District != null && p.District.Contains(term)));
        }

        var lots = await query
            .OrderBy(p => p.Name)
            .ToListAsync();
        var staffed = await LotStaffing.StaffedLotIdsAsync(pmsContext, lots.Select(p => p.Id));
        var bays = await PublicBaysAsync(lots.Select(p => p.Id).ToList());

        return lots.Select(p =>
        {
            var isStaffed = staffed.Contains(p.Id);
            var effective = LotStaffing.EffectiveStatus(p, isStaffed);
            var bookable = effective == LotStatus.Active;
            var lotBays = bays.Where(b => b.LotId == p.Id).ToList();
            return new PublicParkingLotResponseModel
            {
                TotalBays = lotBays.Sum(b => b.Total),
                FreeBays = lotBays.Sum(b => b.Free),
                BaysByVehicle = lotBays
                    .Where(b => b.VehicleType != null)
                    .GroupBy(b => b.VehicleType)
                    .OrderBy(g => g.Key)
                    .Select(g => new PublicLotBaysModel
                    {
                        VehicleType = g.Key,
                        TotalBays = g.Sum(b => b.Total),
                        FreeBays = g.Sum(b => b.Free)
                    })
                    .ToList(),
                Id = p.Id,
                Code = p.Code,
                Name = p.Name,
                Address = p.Address,
                City = p.City,
                District = p.District,
                Latitude = p.Latitude,
                Longitude = p.Longitude,
                OpenTime = p.OpenTime,
                CloseTime = p.CloseTime,
                Is24Hours = p.Is24Hours,
                Capacity = p.Capacity,
                LayoutTemplate = p.LayoutTemplate,
                ImageUrl = p.ImageUrl,
                Description = p.Description,
                Phone = p.Phone,
                Status = effective,
                IsBookable = bookable,
                MaintenanceNotice = bookable
                    ? null
                    : !isStaffed && p.Status == LotStatus.Active
                        ? LotStaffing.UnstaffedNotice
                        : p.MaintenanceNotice ?? "This lot is under maintenance and cannot take bookings right now."
            };
        }).ToList();
    }

    /// <summary>
    /// Free / total bays per active zone of the lots, with the zone's vehicle family (CAR or MOTORBIKE,
    /// from its bays; null when it has none). The zone counter is used when there is one, the slots otherwise.
    /// </summary>
    private async Task<List<(int LotId, int ZoneId, string ZoneName, string VehicleType, int Total, int Free)>> PublicBaysAsync(List<int> lotIds)
    {
        var zones = await pmsContext.Zones.AsNoTracking()
            .Where(z => lotIds.Contains(z.ParkingLotId) && z.IsActive)
            .Select(z => new
            {
                z.ParkingLotId,
                z.Id,
                z.Name,
                Counter = z.ZoneCounter,
                TypeCode = z.Slots.Where(s => s.IsActive).Select(s => s.VehicleType.Code).FirstOrDefault(),
                SlotTotal = z.Slots.Count(s => s.IsActive),
                SlotFree = z.Slots.Count(s => s.IsActive && s.Status == SlotStatus.Available)
            })
            .ToListAsync();

        return zones.Select(z => (
            z.ParkingLotId,
            z.Id,
            z.Name,
            PublicVehicleFamily(z.TypeCode),
            z.Counter?.TotalCapacity ?? z.SlotTotal,
            z.Counter?.AvailableSpaces ?? z.SlotFree)).ToList();
    }

    /// <summary>Public name of a vehicle family: all cars share bays; anything else is left out of the breakdown.</summary>
    private static string PublicVehicleFamily(string vehicleTypeCode) =>
        ParkingInventory.Family(vehicleTypeCode) switch
        {
            "car" => "CAR",
            "two-wheel" => "MOTORBIKE",
            _ => null
        };

    public async Task<LotOccupancyModel> GetOccupancyAsync(int lotId)
    {
        if (!await pmsContext.ParkingLots.AnyAsync(p => p.Id == lotId && !p.IsDeleted))
            throw new NotFoundException($"Parking lot with ID {lotId} was not found.");

        var zones = await PublicBaysAsync([lotId]);
        var total = zones.Sum(z => z.Total);
        var free = zones.Sum(z => z.Free);
        return new LotOccupancyModel
        {
            ParkingLotId = lotId,
            TotalBays = total,
            FreeBays = free,
            OccupiedBays = total - free,
            OccupancyRate = total == 0 ? 0 : Math.Round((total - free) * 100m / total, 1),
            BaysByVehicle = zones
                .Where(z => z.VehicleType != null)
                .GroupBy(z => z.VehicleType)
                .OrderBy(g => g.Key)
                .Select(g => new PublicLotBaysModel { VehicleType = g.Key, TotalBays = g.Sum(z => z.Total), FreeBays = g.Sum(z => z.Free) })
                .ToList(),
            Zones = zones
                .OrderBy(z => z.ZoneName)
                .Select(z => new LotZoneOccupancyModel
                {
                    ZoneId = z.ZoneId,
                    ZoneName = z.ZoneName,
                    VehicleType = z.VehicleType,
                    TotalBays = z.Total,
                    FreeBays = z.Free,
                    OccupiedBays = z.Total - z.Free
                })
                .ToList()
        };
    }

    public async Task<ParkingLotResponseModel> GetByIdAsync(int id)
    {
        var lot = await pmsContext.ParkingLots
            .Include(p => p.Zones)
                .ThenInclude(z => z.Slots)
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted);

        if (lot == null)
            throw new NotFoundException($"Parking lot with ID {id} was not found.");

        return await ToResponseAsync(lot);
    }

    public async Task<ParkingLotResponseModel> CreateAsync(CreateParkingLotModel model, string createdBy = null)
    {
        var codeNormalized = !string.IsNullOrWhiteSpace(model.Code)
            ? model.Code.Trim().ToUpperInvariant()
            : $"LOT-{System.Security.Cryptography.RandomNumberGenerator.GetInt32(100000, 999999)}";

        var codeExists = await pmsContext.ParkingLots
            .AnyAsync(p => p.Code == codeNormalized && !p.IsDeleted);

        if (codeExists)
            throw new BadRequestException($"Parking lot with code '{codeNormalized}' already exists.");

        var managerUserId = string.IsNullOrWhiteSpace(model.ManagerUserId) ? null : model.ManagerUserId.Trim();
        if (managerUserId != null)
            await EnsureActiveManagerAsync(managerUserId);

        var lot = new ParkingLot
        {
            Code = codeNormalized,
            Name = model.Name.Trim(),
            Address = model.Address.Trim(),
            City = model.City?.Trim(),
            District = model.District?.Trim(),
            OpenTime = model.OpenTime,
            CloseTime = model.CloseTime,
            Is24Hours = model.Is24Hours,
            ImageUrl = model.ImageUrl?.Trim(),
            Description = model.Description?.Trim(),
            Phone = model.Phone?.Trim(),
            Status = "Active",
            ManagerUserId = managerUserId,
            TimeZoneId = !string.IsNullOrWhiteSpace(model.TimeZoneId) ? model.TimeZoneId : "SE Asia Standard Time",
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy
        };

        pmsContext.ParkingLots.Add(lot);
        await pmsContext.SaveChangesAsync();

        // Zones, slots, capacity and the published 3D layout all come from the chosen model
        await LotTemplates.ApplyAsync(pmsContext, lot, model.LayoutTemplate, createdBy);

        // A new lot has no Staff yet, so it is shown as Maintenance until its Manager adds some
        return await ToResponseAsync(lot);
    }

    public async Task<ParkingLotResponseModel> ApplyTemplateAsync(int id, string templateId, string userId)
    {
        var lot = await pmsContext.ParkingLots.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted)
            ?? throw new NotFoundException($"Parking lot with ID {id} was not found.");

        await LotTemplates.ApplyAsync(pmsContext, lot, templateId, userId);
        if (notifier != null)
            await notifier.LayoutChangedAsync(id);

        return await GetByIdAsync(id);
    }

    public async Task<ParkingLotResponseModel> UpdateAsync(int id, UpdateParkingLotModel model)
    {
        var lot = await pmsContext.ParkingLots
            .Include(p => p.Zones)
                .ThenInclude(z => z.Slots)
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted);

        if (lot == null)
            throw new NotFoundException($"Parking lot with ID {id} was not found.");

        lot.Name = model.Name.Trim();
        lot.Address = model.Address.Trim();
        lot.City = model.City?.Trim();
        lot.District = model.District?.Trim();
        lot.OpenTime = model.OpenTime;
        lot.CloseTime = model.CloseTime;
        lot.Is24Hours = model.Is24Hours;
        lot.ImageUrl = model.ImageUrl?.Trim();
        lot.Description = model.Description?.Trim();
        lot.Phone = model.Phone?.Trim();

        // One Manager per lot: picking a Manager here replaces the current one; an empty value removes it
        if (model.ManagerUserId != null)
        {
            var managerUserId = string.IsNullOrWhiteSpace(model.ManagerUserId) ? null : model.ManagerUserId.Trim();
            if (managerUserId != null && managerUserId != lot.ManagerUserId)
                await EnsureActiveManagerAsync(managerUserId);
            lot.ManagerUserId = managerUserId;
        }

        if (!string.IsNullOrWhiteSpace(model.TimeZoneId))
            lot.TimeZoneId = model.TimeZoneId;

        lot.UpdatedAt = DateTime.UtcNow;

        await pmsContext.SaveChangesAsync();

        return await ToResponseAsync(lot);
    }

    public async Task<bool> UpdateStatusAsync(int id, UpdateParkingLotStatusModel model)
    {
        var lot = await pmsContext.ParkingLots
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted);

        if (lot == null)
            throw new NotFoundException($"Parking lot with ID {id} was not found.");

        lot.Status = model.Status;
        lot.ClosureReason = model.ClosureReason;
        lot.MaintenanceNotice = model.MaintenanceNotice;
        lot.ClosedFrom = model.ClosedFrom;
        lot.ClosedUntil = model.ClosedUntil;
        lot.UpdatedAt = DateTime.UtcNow;

        await pmsContext.SaveChangesAsync();
        // A temporary closure ends on its own: LotJobs.ReopenDueLotsAsync reopens the lot at ClosedUntil.
        if (notifier != null)
            await notifier.LotStatusChangedAsync(id, lot.Status);
        return true;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var lot = await pmsContext.ParkingLots
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted);

        if (lot == null)
            throw new NotFoundException($"Parking lot with ID {id} was not found.");

        var hasActiveSessions = await pmsContext.ParkingSessions
            .AnyAsync(s => s.ParkingLotId == id && s.Status == "Active");

        if (hasActiveSessions)
            throw new BadRequestException("Cannot delete parking lot with active parking sessions.");

        lot.IsDeleted = true;
        lot.Status = "Inactive";
        lot.UpdatedAt = DateTime.UtcNow;

        await pmsContext.SaveChangesAsync();
        return true;
    }

    private async Task EnsureActiveManagerAsync(string managerUserId)
    {
        var isManager = await pmsContext.Employees.AnyAsync(e =>
            e.UserId == managerUserId && e.Position == "Manager" && e.Status == "Active");
        if (!isManager)
            throw new BadRequestException("The selected Manager does not exist or is deactivated.");
    }

    private async Task<ParkingLotResponseModel> ToResponseAsync(ParkingLot lot) =>
        MapToResponseModel(lot, await LotStaffing.IsStaffedAsync(pmsContext, lot.Id));

    private static ParkingLotResponseModel MapToResponseModel(ParkingLot p, bool isStaffed)
    {
        var status = LotStaffing.EffectiveStatus(p, isStaffed);
        return new ParkingLotResponseModel
        {
            Id = p.Id,
            Code = p.Code,
            Name = p.Name,
            Address = p.Address,
            City = p.City,
            District = p.District,
            OpenTime = p.OpenTime,
            CloseTime = p.CloseTime,
            Is24Hours = p.Is24Hours,
            Capacity = p.Capacity,
            LayoutTemplate = p.LayoutTemplate,
            ImageUrl = p.ImageUrl,
            Description = p.Description,
            Phone = p.Phone,
            Status = status,
            StoredStatus = p.Status,
            IsStaffed = isStaffed,
            IsBookable = status == LotStatus.Active,
            ClosedFrom = p.ClosedFrom,
            ClosedUntil = p.ClosedUntil,
            ClosureReason = p.ClosureReason,
            MaintenanceNotice = p.MaintenanceNotice,
            PartnerId = p.PartnerId,
            ManagerUserId = p.ManagerUserId,
            TimeZoneId = p.TimeZoneId,
            IsDeleted = p.IsDeleted,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            CreatedBy = p.CreatedBy,
            TotalZones = p.Zones?.Count ?? 0,
            TotalSlots = p.Zones?.Sum(z => z.Slots?.Count ?? 0) ?? 0
        };
    }
}
