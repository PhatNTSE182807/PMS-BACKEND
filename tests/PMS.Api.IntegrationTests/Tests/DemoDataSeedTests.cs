using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PMS.API;
using PMS.Api.IntegrationTests.Common;
using PMS.Application.Helpers.DemoData;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.Application.Models.VehicleDocuments;
using PMS.DataAccess.Persistence;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

/// <summary>One API with the demo data, shared by the tests of the class (seeding takes a few seconds).</summary>
public sealed class DemoDataFixture : IDisposable
{
    private readonly ApiApplicationFactory<Program> _root = new();

    public DemoDataFixture()
    {
        Factory = _root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string> { [DemoDataSeed.ConfigKey] = "true" })));
        // Starting the API runs the seed
        Factory.CreateClient();
    }

    public WebApplicationFactory<Program> Factory { get; }

    public void Dispose()
    {
        Factory.Dispose();
        _root.Dispose();
    }
}

/// <summary>
/// The demo data ("Seed:DemoData": true) on the in-memory database: every role can sign in, the numbers add
/// up (zone counters, wallets, payments), a second run adds nothing, and the real flows work on it. The
/// flows that change data go through the real services, so the checks still hold whatever order they run in.
/// </summary>
public class DemoDataSeedTests(DemoDataFixture fixture) : IClassFixture<DemoDataFixture>
{
    private static readonly string[] OperatingLotCodes = ["LOT-FPTU-01", "LOT-FSOFT-01", "LOT-CONGHOA-01"];

    private readonly WebApplicationFactory<Program> _factory = fixture.Factory;

    [Theory]
    [InlineData("mgr.fptu", "Manager")]
    [InlineData("staff.fsoft1", "Staff")]
    [InlineData("an.nguyen@example.com", "User")]
    public async Task Demo_Accounts_Sign_In_With_Their_Role(string login, string role)
    {
        var result = await AuthenticateAsync(login);

        result["role"]!.Value<string>().Should().Be(role);
    }

    [Fact]
    public async Task Deactivated_Demo_Staff_Cannot_Sign_In()
    {
        var response = await _factory.CreateClient().PostAsync("/api/users/authenticate",
            Json(new { username = "staff.conghoa2", password = DemoDataSeed.StaffPassword }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_Demo_Manager_Still_On_The_Old_Shared_Password_Gets_The_Manager_Password()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<PMS.DataAccess.Identity.ApplicationUser>>();
            var manager = await users.FindByNameAsync("mgr.spare");
            await users.ResetPasswordAsync(manager!, await users.GeneratePasswordResetTokenAsync(manager!), DemoDataSeed.Password);
        }

        using (var scope = _factory.Services.CreateScope())
            await DemoDataSeed.SeedAsync(scope.ServiceProvider);

        var client = _factory.CreateClient();
        (await client.PostAsync("/api/users/authenticate", Json(new { username = "mgr.spare", password = DemoDataSeed.Password })))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsync("/api/users/authenticate", Json(new { username = "mgr.spare", password = DemoDataSeed.ManagerPassword })))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Demo_Lots_Have_People_On_Duty_Except_Ben_Thanh()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
        var lots = await db.ParkingLots.ToDictionaryAsync(l => l.Code, l => l.Id);

        foreach (var code in OperatingLotCodes)
            (await LotStaffing.IsStaffedAsync(db, lots[code])).Should().BeTrue(code);
        (await LotStaffing.IsStaffedAsync(db, lots["LOT-BENTHANH-01"])).Should().BeFalse();
        (await db.PricingRules.CountAsync(r => r.Status == "Active")).Should().Be(9);
    }

    [Fact]
    public async Task Zone_Counters_Match_The_Vehicles_Inside_And_The_Held_Bays()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
        var zones = await db.Zones.Include(z => z.ZoneCounter).Include(z => z.Slots).ToListAsync();
        var inside = await db.ParkingSessions.Where(s => SessionStatus.Inside.Contains(s.Status)).ToListAsync();

        inside.Should().HaveCountGreaterThan(10);
        foreach (var zone in zones)
        {
            var occupied = inside.Count(s => s.ZoneId == zone.Id);
            var held = zone.Slots.Count(s => s.Status == SlotStatus.Reserved);
            zone.ZoneCounter.OccupiedSpaces.Should().Be(occupied, zone.Code);
            zone.ZoneCounter.AvailableSpaces.Should().Be(zone.ZoneCounter.TotalCapacity - occupied - held, zone.Code);
        }

        // Every bay marked Occupied has its vehicle inside
        var occupiedSlots = zones.SelectMany(z => z.Slots).Where(s => s.Status == SlotStatus.Occupied).Select(s => s.Id).ToList();
        occupiedSlots.Should().BeEquivalentTo(inside.Where(s => s.SlotId != null).Select(s => s.SlotId!.Value));
    }

    [Fact]
    public async Task Wallets_Match_Their_Ledgers()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
        var wallets = await db.Wallets.ToListAsync();

        wallets.Should().NotBeEmpty();
        foreach (var wallet in wallets)
        {
            var ledger = await db.WalletTransactions.Where(t => t.WalletId == wallet.Id).OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).ToListAsync();
            ledger.Sum(t => t.Amount).Should().Be(wallet.Balance);
            ledger.Should().OnlyContain(t => t.BalanceAfter >= 0 && t.BalanceAfter == t.BalanceBefore + t.Amount);
            ledger.Last().BalanceAfter.Should().Be(wallet.Balance);
        }

        // Wallet payers can pay now; Em and Dung keep the exact amounts their test cases rely on
        var balances = await db.Wallets.Join(db.Customers, w => w.CustomerId, c => c.Id, (w, c) => new { c.Email, w.Balance })
            .ToDictionaryAsync(x => x.Email, x => x.Balance);
        foreach (var payer in new[] { "an.nguyen", "binh.tran", "giang.hoang", "hai.do" })
            balances[$"{payer}@example.com"].Should().BeGreaterThanOrEqualTo(200_000, payer);
        balances["em.vo@example.com"].Should().Be(50_000);
        balances["dung.pham@example.com"].Should().Be(100_000);
    }

    [Fact]
    public async Task Finished_Visits_Are_Paid_In_Full()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
        var completed = await db.ParkingSessions.Where(s => s.Status == SessionStatus.Completed).ToListAsync();
        var paid = (await db.Payments.Where(p => p.Purpose == PaymentPurpose.ParkingFee && p.Status == PaymentStatus.Succeeded).ToListAsync())
            .GroupBy(p => p.SessionId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));

        completed.Should().HaveCountGreaterThan(200);
        foreach (var session in completed)
        {
            (session.PaidAmount + session.DepositApplied).Should().Be(session.FinalFee, session.SessionCode);
            paid.GetValueOrDefault(session.Id).Should().Be(session.PaidAmount, session.SessionCode);
            if (session.SessionType != SessionType.Whitelist)
                session.FinalFee.Should().BePositive("there is no free period");
        }
    }

    [Fact]
    public async Task Papers_Waiting_For_Review_Have_Their_Files()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
        var waiting = await db.VehicleVerifications.Include(v => v.Documents)
            .Where(v => v.Status == VehicleVerificationStatus.NeedsReview).ToListAsync();

        waiting.Should().HaveCount(2);
        waiting.SelectMany(v => v.Documents).Should().HaveCount(4)
            .And.OnlyContain(d => d.StoragePath != null && d.ContentType == "application/pdf");
    }

    [Fact]
    public async Task Seeding_Again_Adds_Nothing()
    {
        int Count(PmsDbContext db) => db.ParkingSessions.Count() + db.Payments.Count() + db.Reservations.Count() + db.Employees.Count() + db.Vehicles.Count();

        int before;
        await using (var scope = _factory.Services.CreateAsyncScope())
            before = Count(scope.ServiceProvider.GetRequiredService<PmsDbContext>());

        await using (var scope = _factory.Services.CreateAsyncScope())
            await DemoDataSeed.SeedAsync(scope.ServiceProvider);

        await using (var scope = _factory.Services.CreateAsyncScope())
            Count(scope.ServiceProvider.GetRequiredService<PmsDbContext>()).Should().Be(before);
    }

    [Fact]
    public async Task An_Old_Once_Per_Day_Motorbike_Price_Moves_To_Blocks_Like_Cars()
    {
        // An older demo database: Cong Hoa motorbikes paid 5,000 VND once per day
        int ruleId, version;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
            var lotId = await db.ParkingLots.Where(p => p.Code == "LOT-CONGHOA-01").Select(p => p.Id).SingleAsync();
            var typeId = await db.VehicleTypes.Where(v => v.Code == "MOTORBIKE").Select(v => v.Id).SingleAsync();
            var rule = await db.PricingRules.SingleAsync(r => r.ParkingLotId == lotId && r.VehicleTypeId == typeId && r.Status == "Active");
            rule.Name = "Motorbikes, per day";
            rule.ChargeModel = "PerEntry";
            rule.EntryFee = 5_000;
            rule.FirstBlockMinutes = null;
            rule.FirstBlockPrice = null;
            rule.BlockPrice = 0;
            rule.MaxDailyFee = null;
            await db.SaveChangesAsync();
            (ruleId, version) = (rule.Id, rule.VersionNo);
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
            await DemoDataSeed.SeedAsync(scope.ServiceProvider);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
            var rule = await db.PricingRules.AsNoTracking().SingleAsync(r => r.Id == ruleId);
            rule.ChargeModel.Should().Be("Block");
            rule.EntryFee.Should().Be(0);
            rule.FirstBlockMinutes.Should().Be(120);
            rule.FirstBlockPrice.Should().Be(5_000);
            rule.BlockMinutes.Should().Be(60);
            rule.BlockPrice.Should().Be(2_000);
            rule.MaxDailyFee.Should().Be(20_000);
            rule.VersionNo.Should().Be(version + 1);
            (await db.PricingRuleHistories.AnyAsync(h => h.PricingRuleId == ruleId && h.Reason == "Motorbikes are charged by block like cars"))
                .Should().BeTrue();
        }
    }

    [Fact]
    public async Task Staff_Takes_Cash_And_Lets_A_Seeded_Walk_In_Out()
    {
        var staff = await SignInAsync("staff.fptu1");
        var lotId = await LotIdAsync("LOT-FPTU-01");

        var session = (await Body(await staff.GetAsync($"/api/staff/lots/{lotId}/sessions/lookup?plate=51D-357.91")))["result"]!;
        var sessionId = session["id"]!.Value<long>();
        var quote = (await Body(await staff.PostAsync($"/api/staff/sessions/{sessionId}/quote", Json(new { plateNumber = "51D-357.91" }))))["result"]!;
        var due = quote["amountDue"]!.Value<decimal>();
        due.Should().BePositive();

        (await staff.PostAsync($"/api/staff/sessions/{sessionId}/payments/cash", Json(new { amount = due }))).StatusCode.Should().Be(HttpStatusCode.OK);
        var checkout = (await Body(await staff.PostAsync($"/api/staff/sessions/{sessionId}/check-out", Json(new { plateNumber = "51D-357.91" }))))["result"]!;

        checkout["barrierOpen"]!.Value<bool>().Should().BeTrue();
        checkout["session"]!["status"]!.Value<string>().Should().Be(SessionStatus.Completed);
    }

    [Fact]
    public async Task Manager_Approves_The_Seeded_Lost_Ticket()
    {
        var manager = await SignInAsync("mgr.fptu");
        long sessionId;
        await using (var scope = _factory.Services.CreateAsyncScope())
            sessionId = await scope.ServiceProvider.GetRequiredService<PmsDbContext>().ParkingSessions
                .Where(s => s.Status == SessionStatus.LostTicketPending).Select(s => s.Id).SingleAsync();

        var approved = await manager.PostAsync($"/api/manager/sessions/{sessionId}/lost-ticket/approve", Json(new { reason = "Papers checked" }));

        approved.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Body(approved))["result"]!["status"]!.Value<string>().Should().Be(SessionStatus.PendingPayment);
    }

    [Fact]
    public async Task Customer_Sees_The_Parked_Car_And_The_Wallet()
    {
        var customer = await SignInAsync("an.nguyen@example.com");

        var current = (await Body(await customer.GetAsync("/api/parking-sessions/current")))["result"]!;
        current.Select(c => c["session"]!["plateNumber"]!.Value<string>()).Should().Contain("51K-123.45");

        var wallet = (await Body(await customer.GetAsync("/api/wallet")))["result"]!;
        wallet["balance"]!.Value<decimal>().Should().BePositive();
    }

    private async Task<int> LotIdAsync(string code)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PmsDbContext>().ParkingLots.Where(l => l.Code == code).Select(l => l.Id).SingleAsync();
    }

    /// <summary>Managers (mgr.*) use the Manager password; other internal accounts are Staff.</summary>
    private static string InternalPassword(string userName) =>
        DemoDataSeed.PasswordFor(userName.StartsWith("mgr.") ? "Manager" : "Staff");

    /// <summary>Signs in through the API with the demo password of the account (email for customers, username for internal accounts).</summary>
    private async Task<JToken> AuthenticateAsync(string login)
    {
        var response = await _factory.CreateClient().PostAsync("/api/users/authenticate",
            Json(login.Contains('@') ? new { email = login, password = DemoDataSeed.Password } : (object)new { username = login, password = InternalPassword(login) }));
        response.StatusCode.Should().Be(HttpStatusCode.OK, login);
        return (await Body(response))["result"]!;
    }

    /// <summary>A client carrying the token of the demo account.</summary>
    private async Task<HttpClient> SignInAsync(string login)
    {
        var token = (await AuthenticateAsync(login))["token"]!.Value<string>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent Json(object value) => new(JsonConvert.SerializeObject(value), Encoding.UTF8, "application/json");

    private static async Task<JObject> Body(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
    }
}
