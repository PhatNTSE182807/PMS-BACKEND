using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.Layout;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Helpers;

/// <summary>
/// The ready-made parking buildings (the frontend 3D models). A lot is never configured by hand:
/// picking a template creates one zone per level, its slots, and the published layout that the
/// 3D view and the 2D fallback draw.
/// </summary>
public static class LotTemplates
{
    private const string Car = "car";

    private const string Motorbike = "motorbike";

    private sealed record Definition(string Id, string Name, string Summary, string[] Floors, int SlotsPerFloor);

    // Keep in sync with GARAGE_TEMPLATES in the frontend (src/components/customer/garage/garageModel.ts)
    private static readonly Definition[] Definitions =
    [
        new("L3S20", "Compact deck", "3 levels × 20 bays · 2 car levels, motorbikes on the ground", [Motorbike, Car, Car], 20),
        new("L3S50", "Wide deck", "3 levels × 50 bays · 2 car levels, motorbikes on the ground", [Motorbike, Car, Car], 50),
        new("L4S30", "Tower", "4 levels × 30 bays · 3 car levels, motorbikes on the ground", [Motorbike, Car, Car, Car], 30)
    ];

    /// <summary>Vehicle type each floor kind is built for (codes seeded by AutomatedMigration).</summary>
    private static readonly Dictionary<string, string> VehicleTypeCodes = new()
    {
        [Car] = "CAR_4",
        [Motorbike] = "MOTORBIKE"
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<LotTemplateResponseModel> All { get; } = Definitions.Select(ToModel).ToList();

    public static LotTemplateResponseModel Find(string id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool Exists(string id) => Find(id) != null;

    /// <summary>
    /// Adds the zones, slots, zone counters and the published layout of the template to a lot that
    /// has none yet, and sets the lot's capacity and template. Saves the changes.
    /// </summary>
    public static async Task ApplyAsync(PmsDbContext pmsContext, ParkingLot lot, string templateId, string userId)
    {
        var template = Find(templateId)
            ?? throw new BadRequestException($"Unknown parking lot model '{templateId}'.");

        if (await pmsContext.Zones.AnyAsync(z => z.ParkingLotId == lot.Id))
            throw new BadRequestException("This parking lot already has zones; its model cannot be changed.");

        var typeCodes = VehicleTypeCodes.Values.ToList();
        var vehicleTypes = (await pmsContext.VehicleTypes
                .Where(v => typeCodes.Contains(v.Code) && v.IsActive)
                .OrderBy(v => v.Id)
                .Select(v => new { v.Code, v.Id })
                .ToListAsync())
            .GroupBy(v => v.Code)
            .ToDictionary(g => g.Key, g => g.First().Id);
        var missing = typeCodes.Except(vehicleTypes.Keys).ToList();
        if (missing.Count > 0)
            throw new BadRequestException($"Vehicle type '{missing[0]}' is not configured.");

        var now = DateTime.UtcNow;
        var floorsWithZones = template.Floors.Select(floor =>
        {
            var zone = new Zone
            {
                ParkingLotId = lot.Id,
                Code = floor.Name,
                Name = $"Level {floor.FloorNo + 1} · {(floor.Kind == Motorbike ? "Motorbikes" : "Cars")}",
                ZoneType = "Floor",
                FloorNo = floor.FloorNo,
                Capacity = template.SlotsPerFloor,
                MaxReservationSlots = template.SlotsPerFloor,
                IsActive = true,
                CreatedAt = now,
                ZoneCounter = new ZoneCounter
                {
                    AvailableSpaces = template.SlotsPerFloor,
                    OccupiedSpaces = 0,
                    TotalCapacity = template.SlotsPerFloor,
                    LastUpdatedAt = now
                }
            };

            for (var i = 0; i < template.SlotsPerFloor; i++)
            {
                zone.Slots.Add(new Slot
                {
                    VehicleTypeId = vehicleTypes[VehicleTypeCodes[floor.Kind]],
                    Code = $"{floor.Name}-{i + 1:D2}",
                    Status = "Available",
                    IsActive = true,
                    RowVersion = Array.Empty<byte>()
                });
            }

            return (Floor: floor, Zone: zone);
        }).ToList();

        pmsContext.Zones.AddRange(floorsWithZones.Select(f => f.Zone));
        lot.Capacity = template.TotalSlots;
        lot.LayoutTemplate = template.Id;
        lot.UpdatedAt = now;
        await pmsContext.SaveChangesAsync();

        // Slots fill each level row by row; bays in odd rows face the other way across the lane
        var layout = new LotLayout
        {
            ParkingLotId = lot.Id,
            Version = 1,
            Status = "Published",
            TemplateId = template.Id,
            FloorsJson = JsonSerializer.Serialize(template.Floors, JsonOptions),
            CreatedAt = now,
            CreatedBy = userId,
            PublishedAt = now,
            PublishedBy = userId
        };
        foreach (var (floor, zone) in floorsWithZones)
        {
            var index = 0;
            foreach (var slot in zone.Slots)
            {
                var row = index / floor.Cols;
                layout.Slots.Add(new LotLayoutSlot
                {
                    SlotId = slot.Id,
                    FloorNo = floor.FloorNo,
                    Row = row,
                    Col = index % floor.Cols,
                    Rotation = row % 2 == 0 ? 0 : 180
                });
                index++;
            }
        }

        pmsContext.LotLayouts.Add(layout);
        await pmsContext.SaveChangesAsync();
    }

    private static LotTemplateResponseModel ToModel(Definition d)
    {
        // Up to 30 bays a level fits in two rows around one lane; bigger levels use four rows
        var rows = d.SlotsPerFloor > 30 ? 4 : 2;
        var cols = (int)Math.Ceiling(d.SlotsPerFloor / (double)rows);
        var floors = d.Floors.Select((kind, i) => new LayoutFloorModel
        {
            FloorNo = i,
            Name = $"L{i + 1}",
            Kind = kind,
            Rows = rows,
            Cols = cols
        }).ToList();
        var carFloors = d.Floors.Count(k => k == Car);
        var bikeFloors = d.Floors.Count(k => k == Motorbike);

        return new LotTemplateResponseModel
        {
            Id = d.Id,
            Name = d.Name,
            Summary = d.Summary,
            SlotsPerFloor = d.SlotsPerFloor,
            CarSlots = carFloors * d.SlotsPerFloor,
            MotorbikeSlots = bikeFloors * d.SlotsPerFloor,
            TotalSlots = d.Floors.Length * d.SlotsPerFloor,
            Floors = floors
        };
    }
}
