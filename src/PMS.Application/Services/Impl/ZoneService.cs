using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.Zone;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

public class ZoneService(PmsDbContext pmsContext) : IZoneService
{
    public async Task<IEnumerable<ZoneResponseModel>> GetByLotIdAsync(int lotId)
    {
        var lotExists = await pmsContext.ParkingLots
            .AnyAsync(p => p.Id == lotId && !p.IsDeleted);

        if (!lotExists)
            throw new NotFoundException($"Parking lot with ID {lotId} was not found.");

        return await GetAllAsync(lotId);
    }

    public async Task<IEnumerable<ZoneResponseModel>> GetAllAsync(int? lotId = null)
    {
        var query = pmsContext.Zones
            .Include(z => z.ParkingLot)
            .Include(z => z.Slots)
            .AsQueryable();

        if (lotId.HasValue)
        {
            query = query.Where(z => z.ParkingLotId == lotId.Value);
        }

        var zones = await query
            .OrderBy(z => z.Code)
            .ToListAsync();

        return zones.Select(MapToResponseModel);
    }

    public async Task<ZoneResponseModel> GetByIdAsync(int id)
    {
        var zone = await pmsContext.Zones
            .Include(z => z.ParkingLot)
            .Include(z => z.Slots)
            .FirstOrDefaultAsync(z => z.Id == id);

        if (zone == null)
            throw new NotFoundException($"Zone with ID {id} was not found.");

        return MapToResponseModel(zone);
    }

    public async Task<ZoneResponseModel> CreateAsync(CreateZoneModel model)
    {
        var lot = await pmsContext.ParkingLots
            .FirstOrDefaultAsync(p => p.Id == model.ParkingLotId && !p.IsDeleted);

        if (lot == null)
            throw new NotFoundException($"Parking lot with ID {model.ParkingLotId} was not found.");

        var codeNormalized = !string.IsNullOrWhiteSpace(model.Code)
            ? model.Code.Trim().ToUpperInvariant()
            : $"ZN-{System.Security.Cryptography.RandomNumberGenerator.GetInt32(100000, 999999)}";

        var codeExists = await pmsContext.Zones
            .AnyAsync(z => z.ParkingLotId == model.ParkingLotId && z.Code == codeNormalized);

        if (codeExists)
            throw new BadRequestException($"Zone with code '{codeNormalized}' already exists in this parking lot.");

        var zone = new Zone
        {
            ParkingLotId = model.ParkingLotId,
            Code = codeNormalized,
            Name = model.Name.Trim(),
            ZoneType = !string.IsNullOrWhiteSpace(model.ZoneType) ? model.ZoneType.Trim() : "Area",
            FloorNo = model.FloorNo,
            Capacity = model.Capacity > 0 ? model.Capacity : (lot.Capacity > 0 ? lot.Capacity : 50),
            MaxReservationSlots = model.MaxReservationSlots,
            LayoutJson = model.LayoutJson,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        pmsContext.Zones.Add(zone);
        await pmsContext.SaveChangesAsync();

        var counter = new ZoneCounter
        {
            ZoneId = zone.Id,
            AvailableSpaces = zone.Capacity,
            OccupiedSpaces = 0,
            TotalCapacity = zone.Capacity,
            LastUpdatedAt = DateTime.UtcNow
        };
        pmsContext.ZoneCounters.Add(counter);
        await pmsContext.SaveChangesAsync();

        zone.ParkingLot = lot;
        return MapToResponseModel(zone);
    }

    public async Task<ZoneResponseModel> UpdateAsync(int id, UpdateZoneModel model)
    {
        var zone = await pmsContext.Zones
            .Include(z => z.ParkingLot)
            .Include(z => z.Slots)
            .FirstOrDefaultAsync(z => z.Id == id);

        if (zone == null)
            throw new NotFoundException($"Zone with ID {id} was not found.");

        zone.Name = model.Name.Trim();

        if (!string.IsNullOrWhiteSpace(model.ZoneType))
            zone.ZoneType = model.ZoneType.Trim();

        if (model.FloorNo.HasValue)
            zone.FloorNo = model.FloorNo;

        zone.Capacity = model.Capacity;
        zone.LayoutJson = model.LayoutJson;

        if (model.MaxReservationSlots.HasValue)
            zone.MaxReservationSlots = model.MaxReservationSlots.Value;

        if (model.IsActive.HasValue)
            zone.IsActive = model.IsActive.Value;

        zone.UpdatedAt = DateTime.UtcNow;

        var counter = await pmsContext.ZoneCounters.FirstOrDefaultAsync(zc => zc.ZoneId == zone.Id);
        if (counter != null)
        {
            counter.TotalCapacity = zone.Capacity;
            counter.AvailableSpaces = Math.Max(0, zone.Capacity - counter.OccupiedSpaces);
            counter.LastUpdatedAt = DateTime.UtcNow;
        }

        await pmsContext.SaveChangesAsync();

        return MapToResponseModel(zone);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var zone = await pmsContext.Zones
            .Include(z => z.Slots)
            .FirstOrDefaultAsync(z => z.Id == id);

        if (zone == null)
            throw new NotFoundException($"Zone with ID {id} was not found.");

        var hasOccupiedOrReservedSlots = zone.Slots.Any(s => s.IsActive && (s.Status == "Occupied" || s.Status == "Reserved"));
        if (hasOccupiedOrReservedSlots)
            throw new BadRequestException("Cannot delete or deactivate a zone that contains occupied slots or reserved slots.");

        zone.IsActive = false;
        zone.UpdatedAt = DateTime.UtcNow;

        await pmsContext.SaveChangesAsync();
        return true;
    }

    private static ZoneResponseModel MapToResponseModel(Zone z)
    {
        return new ZoneResponseModel
        {
            Id = z.Id,
            ParkingLotId = z.ParkingLotId,
            ParkingLotCode = z.ParkingLot?.Code,
            ParkingLotName = z.ParkingLot?.Name,
            Code = z.Code,
            Name = z.Name,
            ZoneType = z.ZoneType,
            FloorNo = z.FloorNo,
            Capacity = z.Capacity,
            MaxReservationSlots = z.MaxReservationSlots,
            LayoutJson = z.LayoutJson,
            IsActive = z.IsActive,
            CreatedAt = z.CreatedAt,
            UpdatedAt = z.UpdatedAt,
            TotalSlots = z.Slots?.Count ?? 0
        };
    }
}
