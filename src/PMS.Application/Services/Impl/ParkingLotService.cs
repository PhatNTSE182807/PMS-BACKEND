using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Helpers;
using PMS.Application.Models.ParkingLot;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

public class ParkingLotService(PmsDbContext pmsContext) : IParkingLotService
{
    public async Task<IEnumerable<ParkingLotResponseModel>> GetAllAsync(string searchTerm = null, string status = null, bool includeDeleted = false)
    {
        var query = pmsContext.ParkingLots.AsQueryable();

        if (!includeDeleted)
        {
            query = query.Where(p => !p.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(p => p.Status == status);
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

        return lots.Select(MapToResponseModel);
    }

    public async Task<IEnumerable<PublicParkingLotResponseModel>> GetPublicLotsAsync(string searchTerm = null)
    {
        var query = pmsContext.ParkingLots
            .Where(p => !p.IsDeleted && p.Status == "Active");

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

        return lots.Select(p => new PublicParkingLotResponseModel
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
            Status = p.Status
        });
    }

    public async Task<ParkingLotResponseModel> GetByIdAsync(int id)
    {
        var lot = await pmsContext.ParkingLots
            .Include(p => p.Zones)
                .ThenInclude(z => z.Slots)
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted);

        if (lot == null)
            throw new NotFoundException($"Parking lot with ID {id} was not found.");

        return MapToResponseModel(lot);
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
            ManagerUserId = model.ManagerUserId,
            TimeZoneId = !string.IsNullOrWhiteSpace(model.TimeZoneId) ? model.TimeZoneId : "SE Asia Standard Time",
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy
        };

        pmsContext.ParkingLots.Add(lot);
        await pmsContext.SaveChangesAsync();

        // Zones, slots, capacity and the published 3D layout all come from the chosen model
        await LotTemplates.ApplyAsync(pmsContext, lot, model.LayoutTemplate, createdBy);

        return MapToResponseModel(lot);
    }

    public async Task<ParkingLotResponseModel> ApplyTemplateAsync(int id, string templateId, string userId)
    {
        var lot = await pmsContext.ParkingLots.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted)
            ?? throw new NotFoundException($"Parking lot with ID {id} was not found.");

        await LotTemplates.ApplyAsync(pmsContext, lot, templateId, userId);

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

        if (model.ManagerUserId != null)
            lot.ManagerUserId = model.ManagerUserId;

        if (!string.IsNullOrWhiteSpace(model.TimeZoneId))
            lot.TimeZoneId = model.TimeZoneId;

        lot.UpdatedAt = DateTime.UtcNow;

        await pmsContext.SaveChangesAsync();

        return MapToResponseModel(lot);
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

    private static ParkingLotResponseModel MapToResponseModel(ParkingLot p)
    {
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
            Status = p.Status,
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
