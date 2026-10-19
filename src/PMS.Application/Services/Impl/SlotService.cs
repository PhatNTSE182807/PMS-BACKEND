using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.Slot;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

public class SlotService(PmsDbContext pmsContext, ILotRealtimeNotifier notifier = null) : ISlotService
{
    private static readonly string[] AllowedStatuses = ["Available", "Occupied", "Reserved", "Maintenance"];

    public async Task<IEnumerable<SlotResponseModel>> GetByZoneIdAsync(int zoneId)
    {
        var zoneExists = await pmsContext.Zones.AnyAsync(z => z.Id == zoneId);
        if (!zoneExists)
            throw new NotFoundException($"Zone with ID {zoneId} was not found.");

        return await GetAllAsync(zoneId: zoneId);
    }

    public async Task<IEnumerable<SlotResponseModel>> GetAllAsync(int? zoneId = null, int? vehicleTypeId = null, string status = null, bool? isActive = null)
    {
        var query = pmsContext.Slots
            .Include(s => s.Zone)
                .ThenInclude(z => z.ParkingLot)
            .Include(s => s.VehicleType)
            .AsQueryable();

        if (zoneId.HasValue)
        {
            query = query.Where(s => s.ZoneId == zoneId.Value);
        }

        if (vehicleTypeId.HasValue)
        {
            query = query.Where(s => s.VehicleTypeId == vehicleTypeId.Value);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(s => s.Status == status);
        }

        if (isActive.HasValue)
        {
            query = query.Where(s => s.IsActive == isActive.Value);
        }

        var slots = await query
            .OrderBy(s => s.Code)
            .ToListAsync();

        return slots.Select(MapToResponseModel);
    }

    public async Task<SlotResponseModel> GetByIdAsync(int id)
    {
        var slot = await pmsContext.Slots
            .Include(s => s.Zone)
                .ThenInclude(z => z.ParkingLot)
            .Include(s => s.VehicleType)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (slot == null)
            throw new NotFoundException($"Slot with ID {id} was not found.");

        return MapToResponseModel(slot);
    }

    public async Task<SlotResponseModel> CreateAsync(CreateSlotModel model)
    {
        var zone = await pmsContext.Zones
            .Include(z => z.ParkingLot)
            .FirstOrDefaultAsync(z => z.Id == model.ZoneId && z.IsActive);

        if (zone == null)
            throw new NotFoundException($"Active zone with ID {model.ZoneId} was not found.");

        var vehicleType = await pmsContext.VehicleTypes
            .FirstOrDefaultAsync(vt => vt.Id == model.VehicleTypeId && vt.IsActive);

        if (vehicleType == null)
            throw new NotFoundException($"Active vehicle type with ID {model.VehicleTypeId} was not found.");

        var codeNormalized = model.Code.Trim().ToUpperInvariant();

        var codeExists = await pmsContext.Slots
            .AnyAsync(s => s.ZoneId == model.ZoneId && s.Code == codeNormalized);

        if (codeExists)
            throw new BadRequestException($"Slot with code '{codeNormalized}' already exists in this zone.");

        var initialStatus = !string.IsNullOrWhiteSpace(model.Status) ? model.Status.Trim() : "Available";
        if (!AllowedStatuses.Any(a => string.Equals(a, initialStatus, StringComparison.OrdinalIgnoreCase)))
            throw new BadRequestException("Invalid status. Allowed values: Available, Occupied, Reserved, Maintenance.");

        var slot = new Slot
        {
            ZoneId = model.ZoneId,
            VehicleTypeId = model.VehicleTypeId,
            Code = codeNormalized,
            Status = initialStatus,
            DedicatedVehicleId = model.DedicatedVehicleId,
            DedicatedNote = model.DedicatedNote?.Trim(),
            IsActive = true,
            RowVersion = Array.Empty<byte>()
        };

        pmsContext.Slots.Add(slot);
        await pmsContext.SaveChangesAsync();

        slot.Zone = zone;
        slot.VehicleType = vehicleType;
        return MapToResponseModel(slot);
    }

    public async Task<SlotResponseModel> UpdateAsync(int id, UpdateSlotModel model)
    {
        var slot = await pmsContext.Slots
            .Include(s => s.Zone)
                .ThenInclude(z => z.ParkingLot)
            .Include(s => s.VehicleType)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (slot == null)
            throw new NotFoundException($"Slot with ID {id} was not found.");

        if (model.VehicleTypeId.HasValue && model.VehicleTypeId.Value != slot.VehicleTypeId)
        {
            var vehicleType = await pmsContext.VehicleTypes
                .FirstOrDefaultAsync(vt => vt.Id == model.VehicleTypeId.Value && vt.IsActive);

            if (vehicleType == null)
                throw new NotFoundException($"Active vehicle type with ID {model.VehicleTypeId.Value} was not found.");

            slot.VehicleTypeId = model.VehicleTypeId.Value;
            slot.VehicleType = vehicleType;
        }

        if (!string.IsNullOrWhiteSpace(model.Code))
        {
            var codeNormalized = model.Code.Trim().ToUpperInvariant();
            if (codeNormalized != slot.Code)
            {
                var codeExists = await pmsContext.Slots
                    .AnyAsync(s => s.ZoneId == slot.ZoneId && s.Code == codeNormalized && s.Id != id);

                if (codeExists)
                    throw new BadRequestException($"Slot with code '{codeNormalized}' already exists in this zone.");

                slot.Code = codeNormalized;
            }
        }

        if (model.DedicatedVehicleId.HasValue)
            slot.DedicatedVehicleId = model.DedicatedVehicleId;

        if (model.DedicatedNote != null)
            slot.DedicatedNote = model.DedicatedNote.Trim();


        if (model.IsActive.HasValue)
            slot.IsActive = model.IsActive.Value;

        await pmsContext.SaveChangesAsync();

        return MapToResponseModel(slot);
    }

    public async Task<bool> UpdateStatusAsync(int id, string status)
    {
        var slot = await pmsContext.Slots.FirstOrDefaultAsync(s => s.Id == id);
        if (slot == null)
            throw new NotFoundException($"Slot with ID {id} was not found.");

        var matchedStatus = AllowedStatuses.FirstOrDefault(a => string.Equals(a, status?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (matchedStatus == null)
            throw new BadRequestException("Invalid status. Allowed values: Available, Occupied, Reserved, Maintenance.");

        if (slot.Status == "Occupied" && matchedStatus != "Occupied")
        {
            var hasActiveSession = await pmsContext.ParkingSessions.AnyAsync(s => s.SlotId == id && s.Status == "Active");
            if (hasActiveSession)
                throw new BadRequestException("Cannot change status of a slot with an active parking session.");
        }

        slot.Status = matchedStatus;
        await pmsContext.SaveChangesAsync();

        if (notifier != null)
        {
            var lotId = await pmsContext.Zones.Where(z => z.Id == slot.ZoneId).Select(z => z.ParkingLotId).FirstAsync();
            await notifier.SlotStatusChangedAsync(lotId, slot.Id, slot.Status);
        }

        return true;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var slot = await pmsContext.Slots.FirstOrDefaultAsync(s => s.Id == id);
        if (slot == null)
            throw new NotFoundException($"Slot with ID {id} was not found.");

        if (slot.Status == "Occupied" || slot.Status == "Reserved")
            throw new BadRequestException($"Cannot deactivate or delete a slot that is currently {slot.Status.ToLowerInvariant()}.");

        slot.IsActive = false;
        await pmsContext.SaveChangesAsync();
        return true;
    }

    private static SlotResponseModel MapToResponseModel(Slot s)
    {
        return new SlotResponseModel
        {
            Id = s.Id,
            ZoneId = s.ZoneId,
            ZoneCode = s.Zone?.Code,
            ZoneName = s.Zone?.Name,
            ParkingLotId = s.Zone?.ParkingLotId ?? 0,
            ParkingLotCode = s.Zone?.ParkingLot?.Code,
            VehicleTypeId = s.VehicleTypeId,
            VehicleTypeCode = s.VehicleType?.Code,
            VehicleTypeName = s.VehicleType?.Name,
            Code = s.Code,
            Status = s.Status,
            DedicatedVehicleId = s.DedicatedVehicleId,
            DedicatedNote = s.DedicatedNote,
            IsActive = s.IsActive
        };
    }
}
