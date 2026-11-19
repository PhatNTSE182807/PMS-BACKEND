using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Helpers.DemoData;

/// <summary>A weekly duty pattern for every operating lot that has none yet.</summary>
public sealed partial class DemoDataSeed
{
    /// <summary>
    /// The lot's active full-time Staff (five in the demo) share the 4 shifts × 7 days: each works one
    /// shift a day and has a day off, which moves along the week. Starts on this week's Monday.
    /// </summary>
    private async Task EnsureDutyPatternsAsync()
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, timeZone));
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        foreach (var key in OperatingLots)
        {
            var lotId = lots[key].Lot.Id;
            if (await db.DutyPatterns.AnyAsync(p => p.ParkingLotId == lotId)) continue;

            var userIds = EmployeeDefs.Where(d => d.LotKey == key && d.Role == StaffRole && d.IsActive && d.EmploymentType == "FullTime")
                .Select(d => employees[d.UserName].Id).ToList();
            var staff = await db.Employees.Where(e => userIds.Contains(e.UserId) && e.Status == "Active" && e.ParkingLotId == lotId)
                .OrderBy(e => e.EmployeeCode).Select(e => e.Id).ToListAsync();
            if (staff.Count < 5) continue;

            var pattern = new DutyPattern { ParkingLotId = lotId, EffectiveFrom = monday, CreatedAt = now };
            db.DutyPatterns.Add(pattern);
            await db.SaveChangesAsync();
            for (var day = 0; day < 7; day++)
            {
                var off = day % staff.Count;
                var working = staff.Where((_, i) => i != off).Take(4).ToList();
                for (var slot = 0; slot < 4; slot++)
                    db.DutyPatternCells.Add(new DutyPatternCell
                    {
                        DutyPatternId = pattern.Id, DayOfWeek = day + 1, Slot = slot, EmployeeId = working[(slot + day) % 4],
                    });
            }
            await db.SaveChangesAsync();
        }
    }
}
