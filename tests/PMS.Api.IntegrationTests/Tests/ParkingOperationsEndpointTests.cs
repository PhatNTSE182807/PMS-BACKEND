using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using PMS.API;
using PMS.Api.IntegrationTests.Common;
using PMS.Api.IntegrationTests.Helpers;
using PMS.Application.Helpers;
using PMS.Application.Jobs;
using PMS.Application.Models.Operations;
using PMS.Application.Services.Payments;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

/// <summary>
/// End-to-end flows of reservations, the gates and VNPay payments (SRS UC-01, UC-02, UC-04) over the
/// HTTP API, with a hand-moved clock and gateway API calls answered locally (no network).
/// </summary>
public class ParkingOperationsEndpointTests : IDisposable
{
    private const string HashSecret = "INTEGRATIONSECRET";

    private readonly ApiApplicationFactory<Program> _root = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero));

    public ParkingOperationsEndpointTests()
    {
        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(_clock);
            services.AddSingleton(new ParkingOptions { TicketSigningKey = "integration-key" });
            services.AddSingleton(new PaymentGatewayOptions
            {
                VnPay = new VnPayOptions { TmnCode = "ITESTCODE", HashSecret = HashSecret },
                MoMo = new MoMoOptions { PartnerCode = "MOMO", AccessKey = "AK", SecretKey = "SK" }
            });
            // Refund / status calls get an "accepted" answer instead of reaching the sandbox
            services.AddHttpClient(VnPayGateway.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new AcceptingHandler());
            services.AddHttpClient(MoMoGateway.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new AcceptingHandler());
        }));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _root.Dispose();
    }

    [Fact]
    public async Task Booking_Deposit_Checkin_And_Checkout_Run_End_To_End()
    {
        var world = await SeedAsync();
        var slot = world.FreeCarSlotId();

        // 1. Customer holds a slot; a second customer on the same slot loses (first holder wins)
        var created = await world.Customer.PostAsync("/api/reservations", Json(new { parkingLotId = world.LotId, slotId = slot, vehicleId = world.CarId, arrivalTime = _clock.GetUtcNow().AddMinutes(30) }));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var reservation = (await Body(created))["result"]!;
        reservation["status"]!.Value<string>().Should().Be(ReservationStatus.PendingDeposit);
        var reservationId = reservation["id"]!.Value<long>();

        var other = await world.NewCustomerAsync("51G-555.55");
        var lost = await other.Client.PostAsync("/api/reservations", Json(new { parkingLotId = world.LotId, slotId = slot, vehicleId = other.CarId, arrivalTime = _clock.GetUtcNow().AddMinutes(30) }));
        lost.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // 2. Deposit through VNPay: the payment URL is signed; the IPN confirms once
        var pay = await Body(await world.Customer.PostAsync($"/api/reservations/{reservationId}/payments", Json(new { provider = "VNPay" })));
        var payment = pay["result"]!;
        payment["payUrl"]!.Value<string>().Should().StartWith("https://sandbox.vnpayment.vn/paymentv2/vpcpay.html?").And.Contain("vnp_SecureHash=");

        var ipnQuery = SignedVnPay(payment["code"]!.Value<string>(), 30_000, "00");
        var ipn = await Body(await _factory.CreateClient().GetAsync($"/api/payments/vnpay/ipn?{ipnQuery}"));
        ipn["RspCode"]!.Value<string>().Should().Be("00");
        var again = await Body(await _factory.CreateClient().GetAsync($"/api/payments/vnpay/ipn?{ipnQuery}"));
        again["RspCode"]!.Value<string>().Should().Be("02");

        // The return page call after the IPN reports the same payment as already processed
        var returned = await Body(await _factory.CreateClient().GetAsync($"/api/payments/vnpay/return?{ipnQuery}"));
        returned["result"]!["outcome"]!.Value<string>().Should().Be("AlreadyProcessed");
        returned["result"]!["payment"]!["status"]!.Value<string>().Should().Be(PaymentStatus.Succeeded);

        var qr = (await Body(await world.Customer.GetAsync($"/api/reservations/{reservationId}/qr")))["result"]!["qrPayload"]!.Value<string>();

        // 3. Too early for the check-in window (opens 15 min before arrival)
        var early = await CheckInAsync(world, new { reservationQr = qr, plateNumber = "51A-123.45" });
        early["outcome"]!.Value<string>().Should().Be("Blocked");
        early["alerts"]!.Select(a => a["code"]!.Value<string>()).Should().Contain("RESERVATION_OUTSIDE_WINDOW");

        // 4. In the window: barrier opens, the sensor confirms, the held slot becomes Occupied
        _clock.Advance(TimeSpan.FromMinutes(20));
        var entry = await CheckInAsync(world, new { reservationQr = qr, plateNumber = "51A-123.45" });
        entry["outcome"]!.Value<string>().Should().Be("Created");
        entry["ticket"]!.Value<string>().Should().StartWith("tkt.");
        var sessionId = entry["session"]!["id"]!.Value<long>();

        var confirmed = await Body(await world.Staff.PostAsync($"/api/staff/sessions/{sessionId}/entry-confirm", Json(new { })));
        confirmed["result"]!["status"]!.Value<string>().Should().Be(SessionStatus.Active);
        world.SlotStatus(slot).Should().Be(SlotStatus.Occupied);

        // 5. Leaves 40 minutes later: fee 20,000 is covered by the 30,000 deposit, 10,000 comes back (SRS example 4)
        _clock.Advance(TimeSpan.FromMinutes(40));
        var quote = (await Body(await world.Staff.PostAsync($"/api/staff/sessions/{sessionId}/quote", Json(new { plateNumber = "51A12345" }))))["result"]!;
        quote["fee"]!.Value<decimal>().Should().Be(20_000);
        quote["amountDue"]!.Value<decimal>().Should().Be(0);
        quote["depositRefund"]!.Value<decimal>().Should().Be(10_000);
        quote["canOpenBarrier"]!.Value<bool>().Should().BeTrue();

        var checkout = (await Body(await world.Staff.PostAsync($"/api/staff/sessions/{sessionId}/check-out", Json(new { plateNumber = "51A12345" }))))["result"]!;
        checkout["barrierOpen"]!.Value<bool>().Should().BeTrue();
        checkout["depositRefund"]!.Value<decimal>().Should().Be(10_000);
        checkout["session"]!["status"]!.Value<string>().Should().Be(SessionStatus.Completed);
        world.SlotStatus(slot).Should().Be(SlotStatus.Available);

        var final = (await Body(await world.Customer.GetAsync($"/api/reservations/{reservationId}")))["result"]!;
        final["status"]!.Value<string>().Should().Be(ReservationStatus.Completed);
        final["refundAmount"]!.Value<decimal>().Should().Be(10_000);
    }

    [Fact]
    public async Task Walk_In_Pays_Cash_And_The_Zone_Counter_Comes_Back()
    {
        var world = await SeedAsync();
        var before = world.CarCounterAvailable();

        var entry = await CheckInAsync(world, new { plateNumber = "30A-111.22", vehicleTypeId = world.CarTypeId, anprConfidence = 0.97 });
        entry["outcome"]!.Value<string>().Should().Be("Created");
        var sessionId = entry["session"]!["id"]!.Value<long>();
        await world.Staff.PostAsync($"/api/staff/sessions/{sessionId}/entry-confirm", Json(new { }));
        world.CarCounterAvailable().Should().Be(before - 1);

        // The same plate cannot enter twice (FR-OPS-11)
        var duplicate = await CheckInAsync(world, new { plateNumber = "30A11122", vehicleTypeId = world.CarTypeId });
        duplicate["alerts"]!.Select(a => a["code"]!.Value<string>()).Should().Contain("DUPLICATE_SESSION");

        // 08:00 → 10:20 = 3 blocks = 60,000 (SRS example 2)
        _clock.Advance(TimeSpan.FromMinutes(140));
        var quote = (await Body(await world.Staff.PostAsync($"/api/staff/sessions/{sessionId}/quote", Json(new { }))))["result"]!;
        quote["amountDue"]!.Value<decimal>().Should().Be(60_000);
        quote["session"]!["status"]!.Value<string>().Should().Be(SessionStatus.PendingPayment);

        // Not paid yet: the barrier stays closed
        (await world.Staff.PostAsync($"/api/staff/sessions/{sessionId}/check-out", Json(new { }))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var cash = (await Body(await world.Staff.PostAsync($"/api/staff/sessions/{sessionId}/payments/cash", Json(new { amount = 100_000 }))))["result"]!;
        cash["amount"]!.Value<decimal>().Should().Be(60_000);
        cash["status"]!.Value<string>().Should().Be(PaymentStatus.Succeeded);

        var checkout = (await Body(await world.Staff.PostAsync($"/api/staff/sessions/{sessionId}/check-out", Json(new { plateNumber = "30A-111.22" }))))["result"]!;
        checkout["session"]!["status"]!.Value<string>().Should().Be(SessionStatus.Completed);
        world.CarCounterAvailable().Should().Be(before);
    }

    [Fact]
    public async Task Online_Payment_That_Lands_After_Cash_Is_Refunded_Automatically()
    {
        var world = await SeedAsync();
        var entry = await CheckInAsync(world, new { plateNumber = "30A-222.33", vehicleTypeId = world.CarTypeId });
        var sessionId = entry["session"]!["id"]!.Value<long>();
        await world.Staff.PostAsync($"/api/staff/sessions/{sessionId}/entry-confirm", Json(new { }));
        _clock.Advance(TimeSpan.FromMinutes(50));

        // Dynamic QR shown at the lane, the guest scans it but the callback is slow (FR-PAY-08)...
        var qr = (await Body(await world.Staff.PostAsync($"/api/staff/sessions/{sessionId}/payments/online", Json(new { provider = "VNPay" }))))["result"]!;
        qr["amount"]!.Value<decimal>().Should().Be(20_000);
        // ...so the Staff takes cash
        await world.Staff.PostAsync($"/api/staff/sessions/{sessionId}/payments/cash", Json(new { amount = 20_000 }));

        var late = await Body(await _factory.CreateClient().GetAsync($"/api/payments/vnpay/ipn?{SignedVnPay(qr["code"]!.Value<string>(), 20_000, "00")}"));
        late["RspCode"]!.Value<string>().Should().Be("00");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
        var refund = await db.Payments.SingleAsync(p => p.Purpose == PaymentPurpose.Refund && p.SessionId == sessionId);
        refund.Amount.Should().Be(20_000);
        refund.Status.Should().Be(PaymentStatus.Refunded);
        (await db.ParkingSessions.SingleAsync(s => s.Id == sessionId)).PaidAmount.Should().Be(20_000);
    }

    [Fact]
    public async Task Blacklisted_And_Low_Confidence_Plates_Wait_For_The_Staff()
    {
        var world = await SeedAsync();
        var add = await world.Manager.PostAsync($"/api/manager/lots/{world.LotId}/plate-lists",
            Json(new { listType = "Blacklist", plateNumber = "29A-666.66", reason = "Nợ phí", expiresAt = _clock.GetUtcNow().AddDays(30) }));
        add.StatusCode.Should().Be(HttpStatusCode.OK);

        var blocked = await CheckInAsync(world, new { plateNumber = "29A-666.66", vehicleTypeId = world.CarTypeId, anprConfidence = 0.6 });
        blocked["outcome"]!.Value<string>().Should().Be("Blocked");
        blocked["alerts"]!.Select(a => a["code"]!.Value<string>()).Should().Contain(["BLACKLISTED", "LOW_CONFIDENCE"]);

        // Override without a reason is refused; with a reason the barrier opens and the note is kept
        (await world.Staff.PostAsync($"/api/staff/lots/{world.LotId}/check-in",
            Json(new { plateNumber = "29A-666.66", vehicleTypeId = world.CarTypeId, anprConfidence = 0.6, plateConfirmed = true, allowBlacklisted = true })))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var allowed = await CheckInAsync(world, new
        {
            plateNumber = "29A-666.66", vehicleTypeId = world.CarTypeId, anprConfidence = 0.6,
            plateConfirmed = true, allowBlacklisted = true, overrideReason = "Đã thu nợ cũ"
        });
        allowed["outcome"]!.Value<string>().Should().Be("Created");
        allowed["session"]!["overrideNote"]!.Value<string>().Should().Contain("Đã thu nợ cũ");
    }

    [Fact]
    public async Task Staff_Of_Another_Lot_Is_Refused_And_Deposit_Deadline_Expires_The_Booking()
    {
        var world = await SeedAsync();
        var outsider = await world.NewStaffAsync(lotId: null);
        (await outsider.GetAsync($"/api/staff/lots/{world.LotId}/sessions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var slot = world.FreeCarSlotId();
        var created = await Body(await world.Customer.PostAsync("/api/reservations", Json(new { parkingLotId = world.LotId, slotId = slot, vehicleId = world.CarId, arrivalTime = _clock.GetUtcNow().AddMinutes(60) })));
        var id = created["result"]!["id"]!.Value<long>();

        _clock.Advance(TimeSpan.FromMinutes(11));
        using (var scope = _factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<ParkingJobs>().RunMinutelyAsync()).ExpiredReservations.Should().Be(1);

        (await Body(await world.Customer.GetAsync($"/api/reservations/{id}")))["result"]!["status"]!.Value<string>().Should().Be(ReservationStatus.Expired);
        world.SlotStatus(slot).Should().Be(SlotStatus.Available);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private async Task<JToken> CheckInAsync(World world, object body)
    {
        var response = await world.Staff.PostAsync($"/api/staff/lots/{world.LotId}/check-in", Json(body));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await Body(response))["result"]!;
    }

    private static JsonContent Json(object body) => new(body);

    private static async Task<JObject> Body(HttpResponseMessage response) => JObject.Parse(await response.Content.ReadAsStringAsync());

    /// <summary>The query string VNPay would send, signed with the test secret.</summary>
    private static string SignedVnPay(string txnRef, decimal amount, string code)
    {
        var fields = new Dictionary<string, string>
        {
            ["vnp_Amount"] = ((long)(amount * 100)).ToString(),
            ["vnp_BankCode"] = "NCB",
            ["vnp_OrderInfo"] = "Thanh toan",
            ["vnp_PayDate"] = "20261020080500",
            ["vnp_ResponseCode"] = code,
            ["vnp_TmnCode"] = "ITESTCODE",
            ["vnp_TransactionNo"] = "14999999",
            ["vnp_TransactionStatus"] = code,
            ["vnp_TxnRef"] = txnRef
        };
        var query = VnPayGateway.BuildQuery(fields);
        return $"{query}&vnp_SecureHash={VnPayGateway.Sign(HashSecret, query)}";
    }

    private async Task<World> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var tag = Guid.NewGuid().ToString("N")[..8];
        var manager = await NewUserAsync(users, "Manager", tag);
        var lot = new ParkingLot { Code = $"OPS-{tag}", Name = "Ops lot", Address = "1 Ops St", Status = "Active", TimeZoneId = "SE Asia Standard Time", ManagerUserId = manager.Id, CreatedAt = DateTime.UtcNow };
        db.ParkingLots.Add(lot);
        await db.SaveChangesAsync();
        await LotTemplates.ApplyAsync(db, lot, "L3S20", manager.Id);

        var world = new World(this, lot.Id)
        {
            CarTypeId = db.VehicleTypes.Single(v => v.Code == "CAR_4").Id,
            Manager = ClientFor(manager)
        };
        world.Staff = await world.NewStaffAsync(lot.Id);
        var customer = await world.NewCustomerAsync("51A-123.45");
        world.Customer = customer.Client;
        world.CarId = customer.CarId;
        return world;
    }

    private static async Task<ApplicationUser> NewUserAsync(UserManager<ApplicationUser> users, string role, string tag)
    {
        var user = new ApplicationUser { Email = $"{role.ToLowerInvariant()}_{tag}@test.com", UserName = $"{role.ToLowerInvariant()}_{tag}", Role = role, EmailConfirmed = true, IsActive = true };
        (await users.CreateAsync(user, "Password.1!")).Succeeded.Should().BeTrue();
        return user;
    }

    private HttpClient ClientFor(ApplicationUser user)
    {
        var client = _factory.CreateClient();
        var token = JwtHelper.GenerateToken(user, _factory.Services.GetRequiredService<IConfiguration>());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>One lot (L3S20 model) with its Manager, a Staff and a customer with a car.</summary>
    private sealed class World(ParkingOperationsEndpointTests test, int lotId)
    {
        public int LotId { get; } = lotId;
        public int CarTypeId { get; init; }
        public HttpClient Manager { get; init; }
        public HttpClient Staff { get; set; }
        public HttpClient Customer { get; set; }
        public Guid CarId { get; set; }

        public async Task<HttpClient> NewStaffAsync(int? lotId)
        {
            using var scope = test._factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
            var user = await NewUserAsync(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(), "Staff", Guid.NewGuid().ToString("N")[..8]);
            db.Employees.Add(new Employee { UserId = user.Id, EmployeeCode = $"E-{user.Id[..8]}", FullName = "Staff", ParkingLotId = lotId, Status = "Active", Position = "Attendant", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            return test.ClientFor(user);
        }

        public async Task<(HttpClient Client, Guid CarId)> NewCustomerAsync(string plate)
        {
            using var scope = test._factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
            var user = await NewUserAsync(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(), "User", Guid.NewGuid().ToString("N")[..8]);
            var customer = new Customer { UserId = user.Id, CustomerCode = $"C-{user.Id[..8]}", FullName = "Customer", IsActive = true, CreatedAt = DateTime.UtcNow };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            var car = new Vehicle { CustomerId = customer.Id, VehicleTypeId = CarTypeId, PlateNumber = plate, PlateNormalized = new string(plate.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant(), IsActive = true, CreatedAt = DateTime.UtcNow };
            db.Vehicles.Add(car);
            await db.SaveChangesAsync();
            return (test.ClientFor(user), car.Id);
        }

        public int FreeCarSlotId()
        {
            using var scope = test._factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
            return db.Slots.Where(s => s.Zone.ParkingLotId == LotId && s.VehicleTypeId == CarTypeId && s.Status == "Available").OrderBy(s => s.Id).Select(s => s.Id).First();
        }

        public string SlotStatus(int slotId)
        {
            using var scope = test._factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<PmsDbContext>().Slots.Single(s => s.Id == slotId).Status;
        }

        /// <summary>Free spaces of the first car level (where walk-in cars are counted first when levels tie).</summary>
        public int CarCounterAvailable()
        {
            using var scope = test._factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
            return db.ZoneCounters.Where(c => c.Zone.ParkingLotId == LotId && c.Zone.Slots.Any(s => s.VehicleTypeId == CarTypeId)).Sum(c => c.AvailableSpaces);
        }
    }

    /// <summary>Clock the tests move by hand.</summary>
    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Gateway API answer: refund / query accepted.</summary>
    private sealed class AcceptingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.Host.Contains("momo")
                    ? "{\"resultCode\":0,\"message\":\"ok\",\"transId\":1}"
                    : "{\"vnp_ResponseCode\":\"00\",\"vnp_Message\":\"ok\",\"vnp_TransactionNo\":\"1\"}", Encoding.UTF8, "application/json")
            });
    }
}
