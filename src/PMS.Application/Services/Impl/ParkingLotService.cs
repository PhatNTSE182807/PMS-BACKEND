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

    public async Task<IReadOnlyList<ParkingLotCapacityModel>> GetCapacityOverviewAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        var lots = await pmsContext.ParkingLots.AsNoTracking()
            .Where(p => !p.IsDeleted)
            .OrderBy(p => p.Id)
            .ToListAsync(cancellationToken);

        if (lots.Count == 0)
            return [];

        var lotIds = lots.Select(l => l.Id).ToList();
        var staffedLotIds = await LotStaffing.StaffedLotIdsAsync(pmsContext, lotIds);

        var managerUserIds = lots
            .Where(l => !string.IsNullOrEmpty(l.ManagerUserId))
            .Select(l => l.ManagerUserId)
            .Distinct()
            .ToList();

        var employees = await pmsContext.Employees.AsNoTracking()
            .Where(e => managerUserIds.Contains(e.UserId))
            .ToListAsync(cancellationToken);

        var managerNameByUser = employees
            .GroupBy(e => e.UserId)
            .ToDictionary(g => g.Key, g => g.First().FullName);

        var zones = await pmsContext.Zones.AsNoTracking()
            .Where(z => lotIds.Contains(z.ParkingLotId) && z.IsActive)
            .Include(z => z.ZoneCounter)
            .Include(z => z.Slots)
                .ThenInclude(s => s.VehicleType)
            .ToListAsync(cancellationToken);

        // Hourly flow for the last 6 hours
        var tz = ParkingText.FindTimeZone(null);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var buckets = new List<(DateTime StartUtc, DateTime EndUtc, string HourLabel)>();
        for (var i = 5; i >= 0; i--)
        {
            var bucketTime = localNow.AddHours(-i);
            var startLocal = new DateTime(bucketTime.Year, bucketTime.Month, bucketTime.Day, bucketTime.Hour, 0, 0, DateTimeKind.Unspecified);
            var endLocal = startLocal.AddHours(1);
            var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, tz);
            var endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, tz);
            buckets.Add((startUtc, endUtc, startLocal.ToString("HH:00")));
        }
        var oldestStartUtc = buckets[0].StartUtc;
        var newestEndUtc = buckets[^1].EndUtc;

        var sessions = await pmsContext.ParkingSessions.AsNoTracking()
            .Where(s => lotIds.Contains(s.ParkingLotId) &&
                        ((s.EntryTime >= oldestStartUtc && s.EntryTime < newestEndUtc) ||
                         (s.ExitTime != null && s.ExitTime >= oldestStartUtc && s.ExitTime < newestEndUtc)))
            .Select(s => new { s.ParkingLotId, s.EntryTime, s.ExitTime })
            .ToListAsync(cancellationToken);

        var result = new List<ParkingLotCapacityModel>(lots.Count);

        foreach (var lot in lots)
        {
            var lotZones = zones.Where(z => z.ParkingLotId == lot.Id).OrderBy(z => z.Name).ToList();

            var zoneDetails = new List<(Zone Zone, string VehicleType, int Total, int Occupied, int Available, int Maintenance)>();
            foreach (var z in lotZones)
            {
                var typeCode = z.Slots.Where(s => s.IsActive).Select(s => s.VehicleType?.Code).FirstOrDefault();
                var vType = PublicVehicleFamily(typeCode);
                if (vType == null)
                {
                    vType = z.Name.Contains("Motorbike", StringComparison.OrdinalIgnoreCase) ? "MOTORBIKE" : "CAR";
                }

                var maintenance = z.Slots.Count(s => s.IsActive && s.Status == SlotStatus.Maintenance);
                var total = z.ZoneCounter?.TotalCapacity ?? z.Slots.Count(s => s.IsActive);
                var occupied = z.ZoneCounter?.OccupiedSpaces ?? z.Slots.Count(s => s.IsActive && (s.Status == SlotStatus.Occupied || s.Status == SlotStatus.Reserved));
                var available = Math.Max(0, total - occupied - maintenance);

                zoneDetails.Add((z, vType, total, occupied, available, maintenance));
            }

            var capacityByVehicle = new List<VehicleCapacityDetailModel>();
            foreach (var vType in new[] { "CAR", "MOTORBIKE" })
            {
                var vZones = zoneDetails.Where(z => z.VehicleType == vType).ToList();
                var totalSlots = vZones.Sum(z => z.Total);
                var occupiedSlots = vZones.Sum(z => z.Occupied);
                var maintenanceSlots = vZones.Sum(z => z.Maintenance);
                var availableSlots = Math.Max(0, totalSlots - occupiedSlots - maintenanceSlots);

                capacityByVehicle.Add(new VehicleCapacityDetailModel
                {
                    VehicleType = vType,
                    TotalSlots = totalSlots,
                    OccupiedSlots = occupiedSlots,
                    AvailableSlots = availableSlots,
                    MaintenanceSlots = maintenanceSlots
                });
            }

            var totalAll = capacityByVehicle.Sum(c => c.TotalSlots);
            var occupiedAll = capacityByVehicle.Sum(c => c.OccupiedSlots);
            var overallPercentage = totalAll > 0 ? Math.Round((double)occupiedAll * 100 / totalAll, 1) : 0;

            var isStaffed = staffedLotIds.Contains(lot.Id);
            string lotStatus;
            if (lot.Status == LotStatus.Inactive || lot.Status == LotStatus.TemporarilyClosed)
                lotStatus = "CLOSED";
            else if (lot.Status == LotStatus.Maintenance || !isStaffed)
                lotStatus = "MAINTENANCE";
            else if (overallPercentage >= 100)
                lotStatus = "FULL";
            else
                lotStatus = "OPEN";

            var lastUpdatedDt = lotZones.Select(z => z.ZoneCounter?.LastUpdatedAt)
                .Where(d => d != null)
                .OrderByDescending(d => d)
                .FirstOrDefault() ?? lot.UpdatedAt ?? lot.CreatedAt;

            var address = string.Join(", ", new[] { lot.Address, lot.District, lot.City }.Where(p => !string.IsNullOrWhiteSpace(p)));

            var manager = lot.ManagerUserId != null && managerNameByUser.TryGetValue(lot.ManagerUserId, out var empName)
                ? empName
                : "Unassigned";

            var zoneDtos = zoneDetails.Select(zd =>
            {
                var usable = Math.Max(0, zd.Total - zd.Maintenance);
                var rate = usable > 0 ? (double)zd.Occupied * 100 / usable : 0;
                var zStatus = rate > 90 ? "FULL" : rate >= 70 ? "WARNING" : "AVAILABLE";

                return new ZoneCapacityDetailModel
                {
                    ZoneId = zd.Zone.Id.ToString(),
                    ZoneName = zd.Zone.Name,
                    TotalSlots = zd.Total,
                    OccupiedSlots = zd.Occupied,
                    Status = zStatus
                };
            }).ToList();

            var lotSessions = sessions.Where(s => s.ParkingLotId == lot.Id).ToList();
            var recentFlow = buckets.Select(b => new HourlyFlowModel
            {
                Hour = b.HourLabel,
                CheckIns = lotSessions.Count(s => s.EntryTime >= b.StartUtc && s.EntryTime < b.EndUtc),
                CheckOuts = lotSessions.Count(s => s.ExitTime != null && s.ExitTime >= b.StartUtc && s.ExitTime < b.EndUtc)
            }).ToList();

            result.Add(new ParkingLotCapacityModel
            {
                Id = lot.Id.ToString(),
                Code = lot.Code,
                Name = lot.Name,
                Address = address,
                ManagerOnDuty = manager,
                Status = lotStatus,
                LastUpdated = lastUpdatedDt.ToString("o"),
                OverallOccupancyPercentage = overallPercentage,
                CapacityByVehicle = capacityByVehicle,
                Zones = zoneDtos,
                LayoutTemplate = lot.LayoutTemplate,
                RecentHourlyFlow = recentFlow
            });
        }

        return result;
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
            Latitude = model.Latitude,
            Longitude = model.Longitude,
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
        lot.Latitude = model.Latitude;
        lot.Longitude = model.Longitude;
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
