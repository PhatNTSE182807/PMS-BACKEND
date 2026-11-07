using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Helpers;

public static class ParkingLotDataSeed
{
    /// <summary>Map positions of the seeded lots (entrance or building, WGS 84), checked on a map.</summary>
    private static readonly Dictionary<string, (decimal Latitude, decimal Longitude)> KnownCoordinates = new()
    {
        ["LOT-FPTU-01"] = (10.841130m, 106.809886m),     // FPT University HCMC, D1 Street, High-Tech Park
        ["LOT-FSOFT-01"] = (10.850884m, 106.798389m),    // FPT Software F-Town, High-Tech Park
        ["LOT-CONGHOA-01"] = (10.801684m, 106.655154m),  // Republic Plaza, 18E Cong Hoa
        ["LOT-BENTHANH-01"] = (10.772559m, 106.698022m), // Ben Thanh Market, District 1
    };

    /// <summary>
    /// Gives the seeded lots their map position when they have none; a position set by an Admin is kept.
    /// </summary>
    public static async Task FillMissingCoordinatesAsync(PmsDbContext pmsContext)
    {
        var codes = KnownCoordinates.Keys.ToList();
        var lots = await pmsContext.ParkingLots
            .Where(p => codes.Contains(p.Code) && !p.IsDeleted && (p.Latitude == null || p.Longitude == null))
            .ToListAsync();
        if (lots.Count == 0) return;

        foreach (var lot in lots)
            (lot.Latitude, lot.Longitude) = KnownCoordinates[lot.Code];
        await pmsContext.SaveChangesAsync();
    }

    public static async Task SeedParkingLotsAsync(PmsDbContext pmsContext)
    {
        await SeedParkingLotAsync(pmsContext,
            code: "LOT-FPTU-01",
            name: "FPT University Parking Lot",
            address: "Long Thanh My Ward, Thu Duc City, Ho Chi Minh City, Vietnam",
            city: "Ho Chi Minh City",
            district: "Thu Duc City",
            layoutTemplate: "L3S50"); // Wide deck

        await SeedParkingLotAsync(pmsContext,
            code: "LOT-FSOFT-01",
            name: "Fsoft Parking Lot",
            address: "F-Town Building, High-Tech Park, Tan Phu Ward, Thu Duc City, Ho Chi Minh City, Vietnam",
            city: "Ho Chi Minh City",
            district: "Thu Duc City",
            layoutTemplate: "L4S30"); // Tower

        await SeedParkingLotAsync(pmsContext,
            code: "LOT-CONGHOA-01",
            name: "Cong Hoa Parking Lot",
            address: "18E Cong Hoa Street, Ward 4, Tan Binh District, Ho Chi Minh City, Vietnam",
            city: "Ho Chi Minh City",
            district: "Tan Binh District",
            layoutTemplate: "L3S20"); // Compact deck

        await FillMissingCoordinatesAsync(pmsContext);
    }

    private static async Task SeedParkingLotAsync(
        PmsDbContext pmsContext,
        string code,
        string name,
        string address,
        string city,
        string district,
        string layoutTemplate)
    {
        var existing = await pmsContext.ParkingLots
            .FirstOrDefaultAsync(p => p.Code == code && !p.IsDeleted);

        if (existing != null)
        {
            if (!await pmsContext.Zones.AnyAsync(z => z.ParkingLotId == existing.Id))
            {
                await LotTemplates.ApplyAsync(pmsContext, existing, layoutTemplate, userId: null);
            }
            return;
        }

        var lot = new ParkingLot
        {
            Code = code,
            Name = name,
            Address = address,
            City = city,
            District = district,
            Is24Hours = true,
            Status = "Active",
            TimeZoneId = "SE Asia Standard Time",
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow
        };

        pmsContext.ParkingLots.Add(lot);
        await pmsContext.SaveChangesAsync();

        // Generate zones, slots, capacity and the published 3D layout from the chosen model
        await LotTemplates.ApplyAsync(pmsContext, lot, layoutTemplate, userId: null);
    }
}
