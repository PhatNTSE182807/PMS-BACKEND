using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using NSubstitute;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Account;
using PMS.Application.Models.Notifications;
using PMS.Application.Models.Operations;
using PMS.Application.Models.Pricing;
using PMS.Application.Models.Validators.Account;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.Application.Services.Payments;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>
/// Internal steps of the payment, gate and booking services that the end-to-end flows only reach in one
/// direction: exit passes before and after prepayment, the exit window per payment point, return-URL
/// checks, gateway lookup, confidence scaling and other small rules.
/// </summary>
public class InternalBranchTests
{
    private static object Invoke(object target, string method, params object[] args)
    {
        var type = target as Type ?? target.GetType();
        var flags = BindingFlags.NonPublic | (target is Type ? BindingFlags.Static : BindingFlags.Instance | BindingFlags.Static);
        var info = type.GetMethods(flags).Single(m => m.Name == method && m.GetParameters().Length == args.Length);
        try
        {
            return info.Invoke(target is Type ? null : target, args);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            throw e.InnerException;
        }
    }

    private static ParkingSession Session(OperationsTestContext t, string status, DateTime? exitWindowEndsAt = null) => new()
    {
        Id = 77,
        SessionCode = "S-77",
        ParkingLotId = t.Lot.Id,
        EntryTime = t.Now.AddHours(-1),
        Status = status,
        ExitWindowEndsAt = exitWindowEndsAt
    };

    // ---- Exit pass ----

    [Fact]
    public async Task ExitPass_Should_Carry_The_Window_Only_While_A_Prepayment_Is_Valid()
    {
        var t = await new OperationsTestContext().SeedAsync();

        var prepaid = (ExitPassModel)Invoke(t.Gates, "ExitPassFor", Session(t, SessionStatus.Paid, t.Now.AddMinutes(10)), 50_000m);
        prepaid.Prepaid.Should().BeTrue();
        prepaid.AmountDue.Should().Be(0);
        prepaid.ValidUntil.Should().NotBeNull();
        t.Signer.Verify(prepaid.Token, ParkingTokenSigner.ExitPassKind).Should().ContainKey("until");

        var expired = (ExitPassModel)Invoke(t.Gates, "ExitPassFor", Session(t, SessionStatus.Paid, t.Now.AddMinutes(-1)), 10_000m);
        expired.Prepaid.Should().BeFalse();
        expired.AmountDue.Should().Be(10_000);
        expired.ValidUntil.Should().BeNull();

        var counterPaid = (ExitPassModel)Invoke(t.Gates, "ExitPassFor", Session(t, SessionStatus.Paid), 0m);
        counterPaid.Prepaid.Should().BeFalse();

        var active = (ExitPassModel)Invoke(t.Gates, "ExitPassFor", Session(t, SessionStatus.Active, t.Now.AddMinutes(10)), 20_000m);
        active.Prepaid.Should().BeFalse();
        active.AmountDue.Should().Be(20_000);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(92.5, 0.925)]
    [InlineData(0.8, 0.8)]
    [InlineData(1.0, 1.0)]
    public void Anpr_Confidence_Should_Accept_Percentages(double? value, double expected)
    {
        var result = (decimal)Invoke(typeof(GateService), "NormalizeConfidence", value.HasValue ? (decimal?)value.Value : null);

        result.Should().Be((decimal)expected);
    }

    // ---- Payment service ----

    [Theory]
    [InlineData(PaymentChannel.App, true)]
    [InlineData(PaymentChannel.Kiosk, true)]
    [InlineData(PaymentChannel.Lane, false)]
    public async Task Settling_A_Session_Should_Open_An_Exit_Window_Only_For_Prepayments(string channel, bool hasWindow)
    {
        var t = await new OperationsTestContext().SeedAsync();
        var session = Session(t, SessionStatus.PendingPayment);

        Invoke(t.Payments, "SettleSession", session, new Payment { Amount = 20_000, Channel = channel });

        session.Status.Should().Be(SessionStatus.Paid);
        session.PaidAmount.Should().Be(20_000);
        (session.ExitWindowEndsAt != null).Should().Be(hasWindow);
    }

    [Fact]
    public async Task Payment_Model_Should_Add_An_Exit_Pass_Only_For_A_Paid_Prepayment()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var succeeded = new Payment { PaymentCode = "P1", Status = PaymentStatus.Succeeded, CreatedAt = t.Now };

        var withPass = (PaymentResponseModel)Invoke(t.Payments, "ToModel", succeeded, Session(t, SessionStatus.Paid, t.Now.AddMinutes(10)));
        withPass.ExitPass.Should().NotBeNull();
        withPass.ExitWindowEndsAt.Should().NotBeNull();

        ((PaymentResponseModel)Invoke(t.Payments, "ToModel", succeeded, Session(t, SessionStatus.Paid))).ExitPass.Should().BeNull();
        ((PaymentResponseModel)Invoke(t.Payments, "ToModel", succeeded, Session(t, SessionStatus.Active, t.Now))).ExitPass.Should().BeNull();
        ((PaymentResponseModel)Invoke(t.Payments, "ToModel", succeeded, null)).ExitPass.Should().BeNull();

        var pending = new Payment { PaymentCode = "P2", Status = PaymentStatus.Pending, PayUrl = "https://pay", CreatedAt = t.Now };
        var pendingModel = (PaymentResponseModel)Invoke(t.Payments, "ToModel", pending, Session(t, SessionStatus.Paid, t.Now.AddMinutes(10)));
        pendingModel.ExitPass.Should().BeNull();
        pendingModel.PayUrl.Should().Be("https://pay");
        pendingModel.CallbackDeadline.Should().NotBeNull();
    }

    [Fact]
    public async Task Return_Url_Should_Only_Follow_Allowed_Origins()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var fallback = t.GatewayOptions.FrontendReturnUrl.TrimEnd('/');

        Invoke(t.Payments, "ReturnBase", "http://localhost:5173/payment/return/").Should().Be("http://localhost:5173/payment/return");
        Invoke(t.Payments, "ReturnBase", "https://evil.example.com/steal").Should().Be(fallback);
        Invoke(t.Payments, "ReturnBase", "not a url").Should().Be(fallback);
        Invoke(t.Payments, "ReturnBase", (string)null).Should().Be(fallback);

        t.GatewayOptions.AllowedReturnOrigins = null;
        Invoke(t.Payments, "ReturnBase", "http://localhost:5173/payment/return").Should().Be(fallback);
    }

    [Fact]
    public async Task Gateway_Lookup_Should_Need_A_Known_Configured_Provider()
    {
        var t = await new OperationsTestContext().SeedAsync();

        ((IPaymentGateway)Invoke(t.Payments, "Gateway", " vnpay ")).Provider.Should().Be(PaymentProviders.VnPay);
        FluentActions.Invoking(() => Invoke(t.Payments, "Gateway", "PayPal")).Should().Throw<BadRequestException>();
        FluentActions.Invoking(() => Invoke(t.Payments, "Gateway", (string)null)).Should().Throw<BadRequestException>();

        t.GatewayOptions.VnPay.TmnCode = null;
        FluentActions.Invoking(() => Invoke(t.Payments, "Gateway", "VNPay")).Should().Throw<UnprocessableRequestException>();

        t.GatewayOptions.AllowSimulatedPayments = true;
        ((IPaymentGateway)Invoke(t.Payments, "Gateway", "VNPay")).IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task Payment_Changes_Should_Be_Pushed_When_A_Notifier_Is_Set()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var notifier = Substitute.For<IParkingRealtimeNotifier>();
        var payments = new PaymentService(t.Db, [], t.GatewayOptions, t.Options, t.Signer, new LotScopeService(t.Db, t.Claims), t.Claims, t.Clock, t.Inventory, notifier);

        Invoke(payments, "QueuePaymentPush", new Payment { Id = 5, CustomerUserId = "u1", Status = PaymentStatus.Succeeded });
        await t.Inventory.FlushAsync();

        await notifier.Received(1).PaymentChangedAsync("u1", null, 5, PaymentStatus.Succeeded, Arg.Any<string>(), null, null);
    }

    [Fact]
    public void Min_Should_Return_The_Earlier_Time()
    {
        var early = new DateTime(2026, 1, 1);
        var late = early.AddHours(1);

        Invoke(typeof(PaymentService), "Min", early, late).Should().Be(early);
        Invoke(typeof(PaymentService), "Min", late, early).Should().Be(early);
    }

    // ---- Reservation service ----

    [Fact]
    public async Task Booking_Mode_Should_Default_To_Pay_As_You_Go()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var arrival = t.Clock.GetUtcNow().AddHours(1);

        var (_, departure, mode) = ((DateTime, DateTime?, string))Invoke(t.Reservations, "ValidateTimes", null, arrival, null);

        mode.Should().Be(BookingMode.PayAsYouGo);
        departure.Should().BeNull();
        FluentActions.Invoking(() => Invoke(t.Reservations, "ValidateTimes", "Monthly", arrival, null)).Should().Throw<BadRequestException>();
    }

    [Fact]
    public async Task Deposit_Should_Fall_Back_To_The_Default_For_Unlisted_Types()
    {
        var t = await new OperationsTestContext().SeedAsync();

        Invoke(t.Reservations, "DepositFor", "SPACESHIP").Should().Be(t.Options.Reservations.DefaultDeposit);
    }

    [Fact]
    public void Truncate_Should_Cut_Long_Notes_Only()
    {
        Invoke(typeof(ReservationService), "Truncate", (string)null).Should().BeNull();
        Invoke(typeof(ReservationService), "Truncate", "short").Should().Be("short");
        ((string)Invoke(typeof(ReservationService), "Truncate", new string('a', 250))).Should().HaveLength(200);
    }

    // ---- Small mappings and rules ----

    [Fact]
    public void Tariff_From_Rule_Should_Treat_Hourly_As_Block()
    {
        TariffProvider.FromRule(new PricingRule { ChargeModel = "Hourly" }).ChargeModel.Should().Be("Block");
        TariffProvider.FromRule(new PricingRule { ChargeModel = "DayNight" }).ChargeModel.Should().Be("DayNight");
    }

    [Fact]
    public void Pricing_Rule_Model_Should_Default_Currency_And_Mark_Dates_Utc()
    {
        var model = (PricingRuleModel)Invoke(typeof(PricingRuleService), "ToModel",
            new PricingRule { Name = "R", Currency = null, EffectiveTo = new DateTime(2026, 12, 31) }, null, null);

        model.Currency.Should().Be("VND");
        model.EffectiveTo!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Notification_Model_Should_Leave_A_Missing_Event_Time_Empty()
    {
        var model = (NotificationModel)Invoke(typeof(NotificationService), "ToModel",
            new UserNotification { Title = "t", Message = "m", CreatedAt = DateTime.UtcNow, EventAt = null });

        model.EventAt.Should().BeNull();
        model.IsRead.Should().BeFalse();
    }

    [Fact]
    public void Layout_Floors_Should_Be_Empty_For_A_Null_Document()
    {
        var floors = (System.Collections.IList)Invoke(typeof(LayoutService), "ReadFloors", new LotLayout { FloorsJson = "null" });

        floors.Count.Should().Be(0);
    }

    [Fact]
    public async Task Slot_Changes_Should_Be_Pushed_When_A_Lot_Notifier_Is_Set()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var lotNotifier = Substitute.For<ILotRealtimeNotifier>();
        var inventory = new ParkingInventory(t.Db, lotNotifier);

        Invoke(inventory, "QueueSlot", t.Lot.Id, new Slot { Id = 9, Status = SlotStatus.Reserved });
        await inventory.FlushAsync();

        await lotNotifier.Received(1).SlotStatusChangedAsync(t.Lot.Id, 9, SlotStatus.Reserved);
    }

    [Fact]
    public void Staff_Email_Uniqueness_Should_Skip_A_Blank_Email()
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        var validator = new CreateStaffAccountModelValidator(users);

        Invoke(validator, "EmailAddressIsUnique", " ").Should().Be(true);
    }

    [Fact]
    public void FeeCalculator_DayNight_Should_Charge_Night_Before_The_Day_Starts()
    {
        var vietnam = ParkingText.FindTimeZone("SE Asia Standard Time");
        var tariff = new Tariff
        {
            ChargeModel = "DayNight", BlockMinutes = 60, BlockPrice = 1_000,
            DayStart = new TimeOnly(6, 0), NightStart = new TimeOnly(18, 0), DayPrice = 20_000, NightPrice = 30_000
        };
        var entry = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 20, 5, 0, 0), vietnam);

        FeeCalculator.Calculate(tariff, entry, entry.AddMinutes(30), vietnam).Total.Should().Be(30_000);
    }

    // ---- VNPay status query ----

    private sealed class UnsignedAnswerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"vnp_ResponseCode\":\"00\",\"vnp_Amount\":2000000}", Encoding.UTF8, "application/json")
            });
    }

    [Fact]
    public async Task VnPay_Query_Should_Distrust_An_Unsigned_Answer()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new UnsignedAnswerHandler()));
        var gateway = new VnPayGateway(new PaymentGatewayOptions { VnPay = new VnPayOptions { TmnCode = "T", HashSecret = "S" } }, factory);

        var status = await gateway.QueryAsync(new GatewayQueryRequest("PAY-1", DateTime.UtcNow, null));

        status.State.Should().Be(GatewayPaymentState.Unknown);
        status.Message.Should().Contain("invalid signature");
    }
}
