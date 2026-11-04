using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using PMS.Application.Helpers;
using PMS.Application.Helpers.Parking;
using PMS.Application.Jobs;
using PMS.Application.Models.Operations;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.Application.Services.Payments;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using PMS.Shared.Services;

namespace PMS.Application.UnitTests.Operations;

/// <summary>A clock the tests move by hand.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Answers every gateway API call (refund / querydr) with "accepted"; records the requests.</summary>
public sealed class AcceptingGatewayHandler : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add($"{request.RequestUri} {(request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken))}");
        var body = request.RequestUri!.Host.Contains("momo")
            ? "{\"resultCode\":0,\"message\":\"Thành công.\",\"transId\":999}"
            : "{\"vnp_ResponseCode\":\"00\",\"vnp_Message\":\"Refund success\",\"vnp_TransactionNo\":\"999\"}";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

/// <summary>
/// Real reservation, gate and payment services over an in-memory database, a test clock and gateways
/// with test keys (no network: API calls go to <see cref="AcceptingGatewayHandler"/>).
/// </summary>
public sealed class OperationsTestContext
{
    public const string HashSecret = "SECRETKEY123";

    public PmsDbContext Db { get; }
    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero));
    public ParkingOptions Options { get; } = new() { TicketSigningKey = "unit-test-key" };
    public PaymentGatewayOptions GatewayOptions { get; } = new()
    {
        VnPay = new VnPayOptions { TmnCode = "TESTCODE", HashSecret = HashSecret },
        MoMo = new MoMoOptions { PartnerCode = "MOMO", AccessKey = "AK", SecretKey = "SK" }
    };
    public AcceptingGatewayHandler Handler { get; } = new();
    public IClaimService Claims { get; } = Substitute.For<IClaimService>();
    public ParkingTokenSigner Signer { get; }
    public ParkingInventory Inventory { get; }
    public LotOccupancy Occupancy { get; }
    public PaymentService Payments { get; }
    public ReservationService Reservations { get; }
    public GateService Gates { get; }
    public ParkingJobs Jobs { get; }

    public ParkingLot Lot { get; private set; }
    public string CustomerUserId { get; } = "customer-1";
    public string StaffUserId { get; } = "staff-1";

    /// <summary>The lot's Manager; a lot needs an active Manager and Staff to take bookings.</summary>
    public string ManagerUserId { get; } = "manager-1";
    public Customer Customer { get; private set; }
    public Vehicle Car { get; private set; }

    public OperationsTestContext()
    {
        Db = new PmsDbContext(new DbContextOptionsBuilder<PmsDbContext>().UseInMemoryDatabase($"Operations_{Guid.NewGuid()}").Options);
        Signer = new ParkingTokenSigner(Options);

        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(Handler, disposeHandler: false));
        IPaymentGateway[] gateways = [new VnPayGateway(GatewayOptions, httpFactory), new MoMoGateway(GatewayOptions, httpFactory)];

        ActAs(StaffUserId, "Staff");
        var scope = new LotScopeService(Db, Claims);
        Inventory = new ParkingInventory(Db);
        var tariffs = new TariffProvider(Db, Options);
        Payments = new PaymentService(Db, gateways, GatewayOptions, Options, Signer, scope, Claims, Clock, Inventory);
        Occupancy = new LotOccupancy(Db);
        Reservations = new ReservationService(Db, Options, Inventory, Payments, Signer, scope, Clock);
        Gates = new GateService(Db, Options, Inventory, tariffs, Occupancy, Payments, Signer, scope, Clock, paperStorage: new InMemoryPaperStorage());
        Jobs = new ParkingJobs(Db, Options, Reservations, Gates, Occupancy, Payments, Inventory, Clock);
    }

    public DateTime Now => Clock.GetUtcNow().UtcDateTime;

    public void ActAs(string userId, string role)
    {
        Claims.GetUserId().Returns(userId);
        Claims.GetClaim(ClaimTypes.Role).Returns(role);
    }

    /// <summary>A lot built from the L3S20 model (motorbike floor + two car floors), a Staff, a customer and a car.</summary>
    public async Task<OperationsTestContext> SeedAsync()
    {
        Db.VehicleTypes.AddRange(
            new VehicleType { Code = "CAR_4", Name = "4-Seat Car", IsActive = true, SortOrder = 1 },
            new VehicleType { Code = "CAR_7", Name = "7-Seat Car", IsActive = true, SortOrder = 2 },
            new VehicleType { Code = "MOTORBIKE", Name = "Motorbike", IsActive = true, SortOrder = 3 });
        // A lot only takes bookings with an active Manager and Staff on duty
        Lot = new ParkingLot { Code = "LOT-T", Name = "Test lot", Address = "1 Test St", Status = "Active", ManagerUserId = ManagerUserId, TimeZoneId = "SE Asia Standard Time", CreatedAt = Now };
        Db.ParkingLots.Add(Lot);
        await Db.SaveChangesAsync();
        await LotTemplates.ApplyAsync(Db, Lot, "L3S20", "admin");

        Db.Employees.Add(new Employee { UserId = StaffUserId, EmployeeCode = "EMP-1", FullName = "Staff", ParkingLotId = Lot.Id, Status = "Active", Position = "Attendant", CreatedAt = Now });
        Db.Employees.Add(new Employee { UserId = ManagerUserId, EmployeeCode = "QL-1", FullName = "Manager", Status = "Active", Position = "Manager", CreatedAt = Now });
        Customer = new Customer { UserId = CustomerUserId, CustomerCode = "CUS-1", FullName = "Customer", IsActive = true, CreatedAt = Now };
        Db.Customers.Add(Customer);
        await Db.SaveChangesAsync();
        Car = new Vehicle { CustomerId = Customer.Id, VehicleTypeId = TypeId("CAR_4"), PlateNumber = "51A-123.45", PlateNormalized = "51A12345", IsActive = true, CreatedAt = Now };
        Db.Vehicles.Add(Car);
        // Only vehicles whose papers Staff approved can be booked
        ApprovePapers(Car);
        await Db.SaveChangesAsync();
        return this;
    }

    /// <summary>Adds a Staff-approved paper check, so the vehicle can be booked.</summary>
    public void ApprovePapers(Vehicle vehicle) =>
        Db.VehicleVerifications.Add(new VehicleVerification { VehicleId = vehicle.Id, CustomerId = Customer.Id, UserId = CustomerUserId, Status = "Approved", CreatedAt = Now, DecidedAt = Now });

    public int TypeId(string code) => Db.VehicleTypes.Single(v => v.Code == code).Id;

    public Slot FreeCarSlot(int skip = 0) =>
        Db.Slots.Where(s => s.VehicleTypeId == TypeId("CAR_4") && s.Status == SlotStatus.Available).OrderBy(s => s.Id).Skip(skip).First();

    public ZoneCounter CounterOf(int zoneId) => Db.ZoneCounters.Single(c => c.ZoneId == zoneId);

    /// <summary>Books a free car slot one hour ahead and pays the deposit through a signed VNPay IPN.</summary>
    public async Task<(ReservationResponseModel Reservation, PaymentResponseModel Payment)> ConfirmedReservationAsync(int arrivalInMinutes = 60)
    {
        var slot = FreeCarSlot();
        var reservation = await Reservations.CreateAsync(CustomerUserId, new CreateReservationModel
        {
            ParkingLotId = Lot.Id,
            SlotId = slot.Id,
            VehicleId = Car.Id,
            ArrivalTime = Clock.GetUtcNow().AddMinutes(arrivalInMinutes)
        });
        var payment = await Payments.CreateDepositPaymentAsync(reservation.Id, CustomerUserId, new CreatePaymentModel { Provider = "VNPay" }, new PaymentRequestContext("127.0.0.1", "http://api"));
        await Payments.HandleCallbackAsync("VNPay", PaymentSignatureTests.SignedVnPayReturn(payment.Code, (long)(payment.Amount * 100), "00"));
        return (await Reservations.GetAsync(CustomerUserId, reservation.Id), payment);
    }
}
