using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Helpers.DemoData;

/// <summary>Demo accounts, the lot each one works at, and the lanes and prices of the demo lots.</summary>
public sealed partial class DemoDataSeed
{
    private const string ManagerRole = "Manager";

    private const string StaffRole = "Staff";

    private sealed record LotDef(string Key, string Code, string Name, string Address, string District, string Template);

    /// <summary>The three lots of ParkingLotDataSeed, plus one the demo adds without people on duty.</summary>
    private static readonly LotDef[] LotDefs =
    [
        new("fptu", "LOT-FPTU-01", null, null, null, null),
        new("fsoft", "LOT-FSOFT-01", null, null, null, null),
        new("conghoa", "LOT-CONGHOA-01", null, null, null, null),
        new("benthanh", "LOT-BENTHANH-01", "Ben Thanh Parking Lot",
            "1 Pham Ngu Lao Street, Ben Thanh Ward, District 1, Ho Chi Minh City, Vietnam", "District 1", "L3S20")
    ];

    private static readonly string[] OperatingLots = ["fptu", "fsoft", "conghoa"];

    private sealed record EmployeeDef(string UserName, string FullName, string Phone, string Role, string LotKey, bool IsActive);

    private static readonly EmployeeDef[] EmployeeDefs =
    [
        new("mgr.fptu", "Lê Minh Quân", "0901000101", ManagerRole, "fptu", true),
        new("mgr.fsoft", "Phạm Ngọc Hân", "0901000102", ManagerRole, "fsoft", true),
        new("mgr.conghoa", "Đặng Quốc Bảo", "0901000103", ManagerRole, "conghoa", true),
        // No lot yet: the Admin can hand Ben Thanh over to this Manager
        new("mgr.spare", "Trương Mỹ Linh", "0901000104", ManagerRole, null, true),
        // Deactivated: Ben Thanh still points at this Manager, so it shows as under maintenance
        new("mgr.former", "Huỳnh Văn Tài", "0901000105", ManagerRole, "benthanh", false),
        new("staff.fptu1", "Nguyễn Thị Hoa", "0901000201", StaffRole, "fptu", true),
        new("staff.fptu2", "Trần Văn Long", "0901000202", StaffRole, "fptu", true),
        new("staff.fsoft1", "Lý Thanh Tâm", "0901000203", StaffRole, "fsoft", true),
        new("staff.fsoft2", "Võ Hoàng Nam", "0901000204", StaffRole, "fsoft", true),
        new("staff.conghoa1", "Phan Thị Mai", "0901000205", StaffRole, "conghoa", true),
        new("staff.conghoa2", "Bùi Đức Anh", "0901000206", StaffRole, "conghoa", false)
    ];

    private sealed record PriceDef(
        string LotKey, string TypeCode, string Name, string ChargeModel, int BlockMinutes, decimal BlockPrice,
        decimal? MaxDailyFee, decimal LostTicketFee,
        int? FirstBlockMinutes = null, decimal? FirstBlockPrice = null,
        decimal? DayPrice = null, decimal? NightPrice = null, decimal EntryFee = 0);

    /// <summary>One active rule per lot and vehicle type, each lot with another charge model.</summary>
    private static readonly PriceDef[] PriceDefs =
    [
        // FPT University: plain hourly blocks
        new("fptu", Car4, "4-seat cars", "Block", 60, 20_000, 150_000, 100_000),
        new("fptu", Car7, "7-seat cars", "Block", 60, 25_000, 180_000, 120_000),
        new("fptu", Motorbike, "Motorbikes", "Block", 240, 5_000, 20_000, 50_000),
        // Fsoft: price by the hour, day 06:00-18:00 and night
        new("fsoft", Car4, "4-seat cars, day / night", "DayNight", 60, 25_000, 200_000, 120_000, DayPrice: 25_000, NightPrice: 15_000),
        new("fsoft", Car7, "7-seat cars, day / night", "DayNight", 60, 30_000, 250_000, 150_000, DayPrice: 30_000, NightPrice: 20_000),
        new("fsoft", Motorbike, "Motorbikes", "Block", 240, 6_000, 24_000, 60_000),
        // Cong Hoa: a 2-hour first block, then hourly, for cars and motorbikes alike
        new("conghoa", Car4, "4-seat cars, first 2 hours", "Block", 60, 15_000, 160_000, 100_000, FirstBlockMinutes: 120, FirstBlockPrice: 30_000),
        new("conghoa", Car7, "7-seat cars, first 2 hours", "Block", 60, 20_000, 200_000, 120_000, FirstBlockMinutes: 120, FirstBlockPrice: 40_000),
        new("conghoa", Motorbike, "Motorbikes, first 2 hours", "Block", 60, 2_000, 20_000, 50_000, FirstBlockMinutes: 120, FirstBlockPrice: 5_000)
    ];

    /// <summary>The once-per-day motorbike rule that older demo databases still have at Cong Hoa.</summary>
    private const string LegacyMotorbikePerDayName = "Motorbikes, per day";

    private static string EmailOf(string userName) => $"{userName}@{EmailDomain}";

    /// <summary>Finds the lots; adds Ben Thanh (built from its template) when it is missing.</summary>
    private async Task<bool> LoadLotsAsync()
    {
        adminId = (await userManager.FindByEmailAsync("admin@gmail.com"))?.Id
                  ?? await userManager.Users.Where(u => u.Role == "Admin").OrderBy(u => u.UserName).Select(u => u.Id).FirstOrDefaultAsync();

        foreach (var def in LotDefs)
        {
            var lot = await db.ParkingLots.FirstOrDefaultAsync(p => p.Code == def.Code && !p.IsDeleted);
            if (lot == null && def.Template != null)
            {
                lot = new ParkingLot
                {
                    Code = def.Code,
                    Name = def.Name,
                    Address = def.Address,
                    City = "Ho Chi Minh City",
                    District = def.District,
                    Is24Hours = true,
                    Status = "Active",
                    TimeZoneId = "SE Asia Standard Time",
                    CreatedAt = now.AddDays(-20),
                    CreatedBy = adminId
                };
                db.ParkingLots.Add(lot);
                await db.SaveChangesAsync();
                await LotTemplates.ApplyAsync(db, lot, def.Template, adminId);
            }

            if (lot == null)
            {
                logger?.LogWarning("Demo data skipped: parking lot {Code} does not exist.", def.Code);
                return false;
            }

            lots[def.Key] = new LotInfo
            {
                Key = def.Key,
                Lot = lot,
                Zones = await db.Zones.Include(z => z.Slots).Include(z => z.ZoneCounter)
                    .Where(z => z.ParkingLotId == lot.Id && z.IsActive)
                    .OrderBy(z => z.FloorNo).ThenBy(z => z.Code)
                    .ToListAsync()
            };
        }

        return true;
    }

    /// <summary>Managers, Staff and customers: created when missing, with their Employee / Customer rows.</summary>
    private async Task EnsureAccountsAsync()
    {
        for (var i = 0; i < EmployeeDefs.Length; i++)
        {
            var def = EmployeeDefs[i];
            var user = await EnsureUserAsync(def.UserName, def.Phone, def.Role, def.IsActive);
            employees[def.UserName] = user;

            var employee = await db.Employees.FirstOrDefaultAsync(e => e.UserId == user.Id);
            if (employee == null)
            {
                var code = $"{(def.Role == ManagerRole ? "QL" : "NV")}{900_100 + i * 7:D6}";
                if (await db.Employees.AnyAsync(e => e.EmployeeCode == code))
                    code = $"{(def.Role == ManagerRole ? "QL" : "NV")}{random.Next(0, 1_000_000):D6}";
                employee = new Employee
                {
                    UserId = user.Id,
                    EmployeeCode = code,
                    FullName = def.FullName,
                    Email = user.Email,
                    Phone = def.Phone,
                    Position = def.Role,
                    HireDate = DateOnly.FromDateTime(now.AddDays(-120 + i * 3)),
                    Status = def.IsActive ? "Active" : "Inactive",
                    CreatedAt = now.AddDays(-120 + i * 3)
                };
                db.Employees.Add(employee);
            }

            // Staff work at one lot; a Manager's lots are set on the lot itself (ParkingLots.ManagerUserId)
            if (def.Role == StaffRole && employee.ParkingLotId == null && def.LotKey != null)
                employee.ParkingLotId = lots[def.LotKey].Id;
            await db.SaveChangesAsync();
        }

        foreach (var def in CustomerDefs)
        {
            var user = await EnsureUserAsync(def.UserName, def.Phone, "User", true);
            var customer = await db.Customers.FirstOrDefaultAsync(c => c.UserId == user.Id);
            if (customer == null)
            {
                var citizenTaken = def.CitizenId != null && await db.Customers.AnyAsync(c => c.CitizenId == def.CitizenId);
                customer = new Customer
                {
                    UserId = user.Id,
                    CustomerCode = $"CUS-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                    FullName = def.FullName,
                    Phone = def.Phone,
                    Email = user.Email,
                    CitizenId = citizenTaken ? null : def.CitizenId,
                    DateOfBirth = def.BirthDate,
                    Address = def.Address,
                    CustomerType = "Individual",
                    MembershipTier = "Standard",
                    IsActive = true,
                    CreatedAt = now.AddDays(-def.JoinedDaysAgo)
                };
                db.Customers.Add(customer);
                await db.SaveChangesAsync();
            }

            customers[def.Key] = new CustomerInfo { Key = def.Key, User = user, Customer = customer, PaysWithWallet = def.PaysWithWallet };
        }
    }

    private async Task<ApplicationUser> EnsureUserAsync(string userName, string phone, string role, bool isActive)
    {
        var email = EmailOf(userName);
        var password = PasswordFor(role);
        var user = await userManager.FindByNameAsync(userName) ?? await userManager.FindByEmailAsync(email);
        if (user != null)
        {
            // Managers and Staff created before they got their own passwords still use the customers' one
            if (password != Password && await userManager.CheckPasswordAsync(user, Password))
            {
                var token = await userManager.GeneratePasswordResetTokenAsync(user);
                var reset = await userManager.ResetPasswordAsync(user, token, password);
                if (!reset.Succeeded)
                    logger?.LogWarning("Demo data: could not change the password of '{UserName}': {Errors}",
                        userName, string.Join("; ", reset.Errors.Select(e => e.Description)));
            }
            return user;
        }

        user = new ApplicationUser
        {
            UserName = userName,
            Email = email,
            EmailConfirmed = true,
            PhoneNumber = phone,
            Role = role,
            IsActive = isActive
        };
        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Demo data: could not create '{userName}': {string.Join("; ", result.Errors.Select(e => e.Description))}");
        return user;
    }

    /// <summary>
    /// Gives each demo lot its Manager (only when it has none: a lot is never taken from a Manager), adds
    /// lanes to lots without any, and one active price rule per vehicle type to lots without rules.
    /// </summary>
    private async Task EnsureLotSetupAsync()
    {
        foreach (var def in EmployeeDefs.Where(e => e.Role == ManagerRole && e.LotKey != null))
        {
            var lot = lots[def.LotKey].Lot;
            if (lot.ManagerUserId == null)
            {
                lot.ManagerUserId = employees[def.UserName].Id;
                lot.UpdatedAt = now;
            }
        }
        await db.SaveChangesAsync();

        foreach (var info in lots.Values)
        {
            info.ManagerId = info.Lot.ManagerUserId;
            var staff = await db.Employees
                .Where(e => e.ParkingLotId == info.Id && e.Position != ManagerRole && e.Status != "Terminated")
                .Select(e => new { e.UserId, e.Status })
                .ToListAsync();
            info.StaffIds.AddRange(staff.Where(s => s.Status == "Active").Select(s => s.UserId).OrderBy(id => id));
            info.FormerStaffIds.AddRange(staff.Where(s => s.Status != "Active").Select(s => s.UserId).OrderBy(id => id));
        }

        foreach (var key in OperatingLots)
        {
            var info = lots[key];
            var gates = await db.Gates.Where(g => g.ParkingLotId == info.Id && g.IsActive).OrderBy(g => g.Code).ToListAsync();
            if (gates.Count == 0)
            {
                gates =
                [
                    new Gate { ParkingLotId = info.Id, Code = "IN-1", Name = "Entry lane 1", Direction = "In", IsActive = true },
                    new Gate { ParkingLotId = info.Id, Code = "OUT-1", Name = "Exit lane 1", Direction = "Out", IsActive = true }
                ];
                if (key == "fptu")
                {
                    gates.Add(new Gate { ParkingLotId = info.Id, Code = "IN-2", Name = "Entry lane 2", Direction = "In", IsActive = true });
                    gates.Add(new Gate { ParkingLotId = info.Id, Code = "OUT-2", Name = "Exit lane 2", Direction = "Out", IsActive = true });
                }
                db.Gates.AddRange(gates);
                await db.SaveChangesAsync();
            }

            info.Entry = gates.FirstOrDefault(g => g.Direction == "In") ?? gates.FirstOrDefault(g => g.Direction == "Both") ?? gates[0];
            info.Exit = gates.FirstOrDefault(g => g.Direction == "Out") ?? gates.FirstOrDefault(g => g.Direction == "Both") ?? gates[0];

            if (!await db.PricingRules.AnyAsync(r => r.ParkingLotId == info.Id))
                await AddPricingRulesAsync(info);
            else
                await MoveMotorbikesToBlocksAsync(info);
        }

        // Staff lists of a lot run by another Manager (assigned before the demo) still work; log it for clarity
        foreach (var key in OperatingLots.Where(k => lots[k].StaffIds.Count == 0))
            logger?.LogWarning("Demo lot {Code} has no active Staff; it will show as under maintenance.", lots[key].Lot.Code);
    }

    /// <summary>
    /// Team decision (2026-11-04): motorbikes are charged by block like cars, never once per day. Older demo databases
    /// still have Cong Hoa's "Motorbikes, per day" rule: it is moved to the block prices above, once (a new version with
    /// its history). A rule a Manager has renamed or re-priced since is left alone. Motorbikes already parked keep the
    /// price they entered with.
    /// </summary>
    private async Task MoveMotorbikesToBlocksAsync(LotInfo lot)
    {
        var def = PriceDefs.FirstOrDefault(p => p.LotKey == lot.Key && p.TypeCode == Motorbike);
        if (def == null || def.ChargeModel != "Block") return;
        var type = types[Motorbike];
        var rule = await db.PricingRules.FirstOrDefaultAsync(r => r.ParkingLotId == lot.Id && r.VehicleTypeId == type.Id
                                                                 && r.ChargeModel == "PerEntry" && r.Name == LegacyMotorbikePerDayName);
        if (rule == null) return;

        var old = new { rule.Name, rule.ChargeModel, rule.EntryFee, rule.BlockMinutes, rule.BlockPrice, rule.MaxDailyFee, rule.VersionNo };
        rule.Name = def.Name;
        rule.ChargeModel = def.ChargeModel;
        rule.EntryFee = def.EntryFee;
        rule.FirstBlockMinutes = def.FirstBlockMinutes;
        rule.FirstBlockPrice = def.FirstBlockPrice;
        rule.BlockMinutes = def.BlockMinutes;
        rule.BlockPrice = def.BlockPrice;
        rule.MaxDailyFee = def.MaxDailyFee;
        rule.VersionNo += 1;
        rule.UpdatedAt = now;
        db.PricingRuleHistories.Add(new PricingRuleHistory
        {
            PricingRuleId = rule.Id,
            ChangeType = "Update",
            OldValuesJson = JsonSerializer.Serialize(old),
            NewValuesJson = JsonSerializer.Serialize(new { rule.Name, rule.ChargeModel, rule.FirstBlockMinutes, rule.FirstBlockPrice, rule.BlockMinutes, rule.BlockPrice, rule.MaxDailyFee, rule.VersionNo }),
            ChangedBy = lot.ManagerId,
            ChangedAt = now,
            Reason = "Motorbikes are charged by block like cars"
        });
        await db.SaveChangesAsync();
        logger?.LogInformation("Demo lot {Code}: motorbikes now pay by block instead of once per day.", lot.Lot.Code);
    }

    private async Task AddPricingRulesAsync(LotInfo lot)
    {
        var createdAt = now.AddDays(-60);
        foreach (var def in PriceDefs.Where(p => p.LotKey == lot.Key))
        {
            var type = types[def.TypeCode];
            // FPT cars had a price change 45 days ago: version 2, old price in the history
            var changed = def.LotKey == "fptu" && def.TypeCode == Car4;
            var rule = new PricingRule
            {
                Code = $"RULE-{lot.Id}-{type.Code}-{createdAt:yyyyMMddHHmmss}",
                Name = def.Name,
                ParkingLotId = lot.Id,
                VehicleTypeId = type.Id,
                ChargeModel = def.ChargeModel,
                EntryFee = def.EntryFee,
                FirstBlockMinutes = def.FirstBlockMinutes,
                FirstBlockPrice = def.FirstBlockPrice,
                BlockMinutes = def.BlockMinutes,
                BlockPrice = def.BlockPrice,
                DayStartTime = def.ChargeModel == "DayNight" ? new TimeOnly(6, 0) : null,
                NightStartTime = def.ChargeModel == "DayNight" ? new TimeOnly(18, 0) : null,
                DayPrice = def.DayPrice,
                NightPrice = def.NightPrice,
                MaxDailyFee = def.MaxDailyFee,
                LostTicketFee = def.LostTicketFee,
                Currency = "VND",
                EffectiveFrom = createdAt,
                Status = "Active",
                VersionNo = changed ? 2 : 1,
                CreatedBy = lot.ManagerId,
                ApprovedBy = lot.ManagerId,
                ApprovedAt = createdAt,
                CreatedAt = createdAt,
                UpdatedAt = changed ? now.AddDays(-45) : null
            };
            db.PricingRules.Add(rule);
            await db.SaveChangesAsync();

            db.PricingRuleHistories.Add(new PricingRuleHistory
            {
                PricingRuleId = rule.Id,
                ChangeType = "Create",
                NewValuesJson = JsonSerializer.Serialize(new { rule.Code, rule.Name, VehicleTypeCode = type.Code, rule.ChargeModel, BlockPrice = changed ? 15_000m : rule.BlockPrice, rule.MaxDailyFee }),
                ChangedBy = lot.ManagerId,
                ChangedAt = createdAt,
                Reason = "Created initial pricing rule"
            });
            if (changed)
                db.PricingRuleHistories.Add(new PricingRuleHistory
                {
                    PricingRuleId = rule.Id,
                    ChangeType = "Update",
                    OldValuesJson = JsonSerializer.Serialize(new { BlockPrice = 15_000m, VersionNo = 1 }),
                    NewValuesJson = JsonSerializer.Serialize(new { rule.BlockPrice, rule.VersionNo }),
                    ChangedBy = lot.ManagerId,
                    ChangedAt = now.AddDays(-45),
                    Reason = "New semester price"
                });
        }

        await db.SaveChangesAsync();
    }
}
