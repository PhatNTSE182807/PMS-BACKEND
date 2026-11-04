using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Helpers;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Layout;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

public class LayoutService(PmsDbContext pmsContext) : ILayoutService
{
    private const string Published = "Published";

    private const string Available = "Available";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<LotTemplateResponseModel> GetTemplates() => LotTemplates.All;

    public async Task<LayoutResponseModel> GetPublishedAsync(int lotId)
    {
        var lot = await FindLotAsync(lotId);
        var layout = await FindPublishedAsync(lotId);
        var slots = await PlacedSlotsAsync(layout);
        var zones = await pmsContext.Zones
            .Where(z => z.ParkingLotId == lotId && z.IsActive)
            .OrderBy(z => z.FloorNo).ThenBy(z => z.Code)
            .ToListAsync();

        return new LayoutResponseModel
        {
            ParkingLotId = lot.Id,
            ParkingLotName = lot.Name,
            LotStatus = LotStaffing.EffectiveStatus(lot, await LotStaffing.IsStaffedAsync(pmsContext, lot.Id)),
            Version = layout.Version,
            TemplateId = layout.TemplateId,
            PublishedAt = layout.PublishedAt,
            UpdatedAt = layout.UpdatedAt ?? layout.CreatedAt,
            Floors = ReadFloors(layout),
            Zones = zones.Select(z => new LayoutZoneModel
            {
                ZoneId = z.Id,
                Code = z.Code,
                Name = z.Name,
                ZoneType = z.ZoneType,
                FloorNo = z.FloorNo
            }).ToList(),
            Slots = slots.Select(s => new LayoutSlotModel
            {
                SlotId = s.Slot.Id,
                Code = s.Slot.Code,
                ZoneId = s.Slot.ZoneId,
                VehicleTypeCode = s.Slot.VehicleType?.Code,
                FloorNo = s.Placement.FloorNo,
                Row = s.Placement.Row,
                Col = s.Placement.Col,
                Rotation = s.Placement.Rotation,
                ModelUrl = s.Placement.ModelUrl
            }).OrderBy(s => s.FloorNo).ThenBy(s => s.Row).ThenBy(s => s.Col).ToList()
        };
    }

    public async Task<LayoutStatusResponseModel> GetStatusAsync(int lotId)
    {
        await FindLotAsync(lotId);
        var layout = await FindPublishedAsync(lotId);
        var slots = await PlacedSlotsAsync(layout);

        return new LayoutStatusResponseModel
        {
            ParkingLotId = lotId,
            Version = layout.Version,
            GeneratedAt = DateTime.UtcNow,
            Slots = slots.Select(s => new SlotStatusModel { SlotId = s.Slot.Id, Status = s.Slot.Status }).ToList(),
            Zones = await ZoneAvailabilityAsync(lotId)
        };
    }

    public async Task<Layout2DResponseModel> Get2DAsync(int lotId, string detail)
    {
        var level = string.Equals(detail, LayoutDetail.Zones, StringComparison.OrdinalIgnoreCase) ? LayoutDetail.Zones : LayoutDetail.Slots;
        var lot = await FindLotAsync(lotId);
        var layout = await FindPublishedAsync(lotId);
        var slots = await PlacedSlotsAsync(layout);
        var zones = await pmsContext.Zones.Where(z => z.ParkingLotId == lotId).ToDictionaryAsync(z => z.Id);
        var parked = level == LayoutDetail.Slots ? await ParkedColorsAsync(lotId) : [];
        var colorOfSlot = parked.Where(p => p.SlotId != null)
            .GroupBy(p => p.SlotId.Value)
            .ToDictionary(g => g.Key, g => g.First().Color);

        var floors = ReadFloors(layout).Select(floor =>
        {
            var onFloor = slots.Where(s => s.Placement.FloorNo == floor.FloorNo).ToList();
            var result = new Layout2DFloorModel
            {
                FloorNo = floor.FloorNo,
                Name = floor.Name,
                Kind = floor.Kind,
                Rows = floor.Rows,
                Cols = floor.Cols,
                Total = onFloor.Count,
                Free = onFloor.Count(s => s.Slot.Status == Available)
            };

            if (level == LayoutDetail.Slots)
            {
                result.Cells = onFloor.Select(s => new Layout2DCellModel
                {
                    SlotId = s.Slot.Id,
                    Code = s.Slot.Code,
                    ZoneId = s.Slot.ZoneId,
                    VehicleTypeCode = s.Slot.VehicleType?.Code,
                    Row = s.Placement.Row,
                    Col = s.Placement.Col,
                    Rotation = s.Placement.Rotation,
                    Status = s.Slot.Status,
                    VehicleColor = colorOfSlot.GetValueOrDefault(s.Slot.Id)
                }).OrderBy(c => c.Row).ThenBy(c => c.Col).ToList();
            }
            else
            {
                result.ZoneBlocks = onFloor.GroupBy(s => s.Slot.ZoneId).Select(g =>
                {
                    zones.TryGetValue(g.Key, out var zone);
                    return new Layout2DZoneBlockModel
                    {
                        ZoneId = g.Key,
                        Code = zone?.Code,
                        Name = zone?.Name,
                        MinRow = g.Min(s => s.Placement.Row),
                        MaxRow = g.Max(s => s.Placement.Row),
                        MinCol = g.Min(s => s.Placement.Col),
                        MaxCol = g.Max(s => s.Placement.Col),
                        Total = g.Count(),
                        Free = g.Count(s => s.Slot.Status == Available)
                    };
                }).OrderBy(b => b.Code).ToList();
            }

            return result;
        }).ToList();

        var counters = await ZoneAvailabilityAsync(lotId);
        foreach (var counter in counters)
            counter.WalkInColors = parked.Where(p => p.SlotId == null && p.ZoneId == counter.ZoneId).Select(p => p.Color).ToList();

        return new Layout2DResponseModel
        {
            ParkingLotId = lot.Id,
            ParkingLotName = lot.Name,
            LotStatus = LotStaffing.EffectiveStatus(lot, await LotStaffing.IsStaffedAsync(pmsContext, lot.Id)),
            Version = layout.Version,
            Detail = level,
            Floors = floors,
            // Live free spaces per zone (walk-ins only move the counter), so the map can show walk-ins too
            ZoneCounters = counters
        };
    }

    /* Helpers -------------------------------------------------------- */

    /// <summary>Colours of the registered vehicles with an open session in the lot, oldest entry first.</summary>
    private async Task<List<(int? SlotId, int? ZoneId, string Color)>> ParkedColorsAsync(int lotId)
    {
        var parked = await (
                from session in pmsContext.ParkingSessions
                where session.ParkingLotId == lotId && SessionStatus.Open.Contains(session.Status) && session.VehicleId != null
                join vehicle in pmsContext.Vehicles on session.VehicleId equals (Guid?)vehicle.Id
                where vehicle.Color != null && vehicle.Color != ""
                orderby session.EntryTime, session.Id
                select new { session.SlotId, session.ZoneId, vehicle.Color })
            .ToListAsync();

        return parked.Select(p => (p.SlotId, p.ZoneId, p.Color)).ToList();
    }

    private async Task<ParkingLot> FindLotAsync(int lotId) =>
        await pmsContext.ParkingLots.FirstOrDefaultAsync(p => p.Id == lotId && !p.IsDeleted)
        ?? throw new NotFoundException($"Parking lot with ID {lotId} was not found.");

    private async Task<LotLayout> FindPublishedAsync(int lotId) =>
        await pmsContext.LotLayouts
            .Include(l => l.Slots)
            .Where(l => l.ParkingLotId == lotId && l.Status == Published)
            .OrderByDescending(l => l.Version)
            .FirstOrDefaultAsync()
        ?? throw new NotFoundException("This parking lot has no 3D layout. An Admin picks its model first.");

    private static List<LayoutFloorModel> ReadFloors(LotLayout layout) =>
        JsonSerializer.Deserialize<List<LayoutFloorModel>>(layout.FloorsJson, JsonOptions) ?? [];

    /// <summary>Placements joined to their slots; slots deactivated since are left out.</summary>
    private async Task<List<(LotLayoutSlot Placement, Slot Slot)>> PlacedSlotsAsync(LotLayout layout)
    {
        var slotIds = layout.Slots.Select(s => s.SlotId).ToList();
        var slots = await pmsContext.Slots
            .Include(s => s.VehicleType)
            .Where(s => slotIds.Contains(s.Id) && s.IsActive)
            .ToDictionaryAsync(s => s.Id);

        return layout.Slots
            .Where(p => slots.ContainsKey(p.SlotId))
            .Select(p => (p, slots[p.SlotId]))
            .ToList();
    }

    /// <summary>Free / total per zone: the Zone Counter when there is one, the slots otherwise.</summary>
    private async Task<List<ZoneAvailabilityModel>> ZoneAvailabilityAsync(int lotId)
    {
        var zones = await pmsContext.Zones
            .Include(z => z.ZoneCounter)
            .Include(z => z.Slots)
            .Where(z => z.ParkingLotId == lotId && z.IsActive)
            .ToListAsync();

        return zones.Select(z => z.ZoneCounter != null
            ? new ZoneAvailabilityModel { ZoneId = z.Id, Total = z.ZoneCounter.TotalCapacity, Free = z.ZoneCounter.AvailableSpaces }
            : new ZoneAvailabilityModel
            {
                ZoneId = z.Id,
                Total = z.Slots.Count(s => s.IsActive),
                Free = z.Slots.Count(s => s.IsActive && s.Status == Available)
            }).ToList();
    }
}
