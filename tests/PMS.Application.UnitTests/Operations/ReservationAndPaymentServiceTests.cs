using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.Operations;
using PMS.Application.Services;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>Reservation life cycle (SRS 4.3.3) and idempotent gateway callbacks (FR-PAY-08..10, FR-RSV-08).</summary>
public class ReservationAndPaymentServiceTests
{
    private static readonly PaymentRequestContext Context = new("127.0.0.1", "http://api");

    [Fact]
    public async Task Booking_Holds_The_Slot_And_Takes_A_Space_Off_The_Zone_Counter()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var slot = t.FreeCarSlot();
        var before = t.CounterOf(slot.ZoneId).AvailableSpaces;

        var reservation = await t.Reservations.CreateAsync(t.CustomerUserId, new CreateReservationModel
        {
            ParkingLotId = t.Lot.Id, SlotId = slot.Id, VehicleId = t.Car.Id, ArrivalTime = t.Clock.GetUtcNow().AddMinutes(30)
        });

        reservation.Status.Should().Be(ReservationStatus.PendingDeposit);
        reservation.DepositAmount.Should().Be(30_000);
        reservation.DepositDueAt.Should().Be(t.Now.AddMinutes(10));
        (await t.Db.Slots.FindAsync(slot.Id))!.Status.Should().Be(SlotStatus.Reserved);
        t.CounterOf(slot.ZoneId).AvailableSpaces.Should().Be(before - 1);
    }

    [Fact]
    public async Task Second_Holder_Of_The_Same_Slot_Loses()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var slot = t.FreeCarSlot();
        await t.Reservations.CreateAsync(t.CustomerUserId, new CreateReservationModel
        {
            ParkingLotId = t.Lot.Id, SlotId = slot.Id, VehicleId = t.Car.Id, ArrivalTime = t.Clock.GetUtcNow().AddMinutes(30)
        });

        // Another car of the same account trying the same slot
        var second = new PMS.DataAccess.Persistence.Entities.Vehicle
        {
            CustomerId = t.Customer.Id, VehicleTypeId = t.TypeId("CAR_7"), PlateNumber = "51B-999.99", PlateNormalized = "51B99999", IsActive = true
        };
        t.Db.Vehicles.Add(second);
        t.ApprovePapers(second);
        await t.Db.SaveChangesAsync();

        var act = () => t.Reservations.CreateAsync(t.CustomerUserId, new CreateReservationModel
        {
            ParkingLotId = t.Lot.Id, SlotId = slot.Id, VehicleId = second.Id, ArrivalTime = t.Clock.GetUtcNow().AddMinutes(30)
        });

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(31 * 24 * 60)]
    public async Task Arrival_Must_Be_Within_The_Lead_Time(int minutesAhead)
    {
        var t = await new OperationsTestContext().SeedAsync();

        var act = () => t.Reservations.CreateAsync(t.CustomerUserId, new CreateReservationModel
        {
            ParkingLotId = t.Lot.Id, SlotId = t.FreeCarSlot().Id, VehicleId = t.Car.Id, ArrivalTime = t.Clock.GetUtcNow().AddMinutes(minutesAhead)
        });

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Ipn_Confirms_Once_And_Repeats_Change_Nothing()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var slot = t.FreeCarSlot();
        var reservation = await t.Reservations.CreateAsync(t.CustomerUserId, new CreateReservationModel
        {
            ParkingLotId = t.Lot.Id, SlotId = slot.Id, VehicleId = t.Car.Id, ArrivalTime = t.Clock.GetUtcNow().AddMinutes(60)
        });
        var payment = await t.Payments.CreateDepositPaymentAsync(reservation.Id, t.CustomerUserId, new CreatePaymentModel { Provider = "VNPay" }, Context);
        payment.PayUrl.Should().StartWith("https://sandbox.vnpayment.vn/paymentv2/vpcpay.html?");

        var ipn = PaymentSignatureTests.SignedVnPayReturn(payment.Code, 3_000_000, "00");
        var first = await t.Payments.HandleCallbackAsync("VNPay", ipn);
        var second = await t.Payments.HandleCallbackAsync("VNPay", ipn);

        first.Outcome.Should().Be("Applied");
        second.Outcome.Should().Be("AlreadyProcessed");
        (await t.Reservations.GetAsync(t.CustomerUserId, reservation.Id)).Status.Should().Be(ReservationStatus.Confirmed);
        t.Db.Payments.Count(p => p.Purpose == PaymentPurpose.Refund).Should().Be(0);
        t.Db.ReservationChangeLogs.Count(l => l.ReservationId == reservation.Id && l.Action == "Confirmed").Should().Be(1);
    }

    [Fact]
    public async Task Callback_With_A_Bad_Signature_Or_Amount_Is_Ignored()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var reservation = await t.Reservations.CreateAsync(t.CustomerUserId, new CreateReservationModel
        {
            ParkingLotId = t.Lot.Id, SlotId = t.FreeCarSlot().Id, VehicleId = t.Car.Id, ArrivalTime = t.Clock.GetUtcNow().AddMinutes(60)
        });
        var payment = await t.Payments.CreateDepositPaymentAsync(reservation.Id, t.CustomerUserId, new CreatePaymentModel { Provider = "VNPay" }, Context);

        var forged = PaymentSignatureTests.SignedVnPayReturn(payment.Code, 3_000_000, "00", secret: "WRONG");
        (await t.Payments.HandleCallbackAsync("VNPay", forged)).Outcome.Should().Be("InvalidSignature");

        var cheaper = PaymentSignatureTests.SignedVnPayReturn(payment.Code, 100_000, "00");
        (await t.Payments.HandleCallbackAsync("VNPay", cheaper)).Outcome.Should().Be("InvalidAmount");

        (await t.Reservations.GetAsync(t.CustomerUserId, reservation.Id)).Status.Should().Be(ReservationStatus.PendingDeposit);
    }

    [Fact]
    public async Task Unpaid_Deposit_Expires_And_A_Late_Callback_Is_Refunded()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var slot = t.FreeCarSlot();
        var counterBefore = t.CounterOf(slot.ZoneId).AvailableSpaces;
        var reservation = await t.Reservations.CreateAsync(t.CustomerUserId, new CreateReservationModel
        {
            ParkingLotId = t.Lot.Id, SlotId = slot.Id, VehicleId = t.Car.Id, ArrivalTime = t.Clock.GetUtcNow().AddMinutes(60)
        });
        var payment = await t.Payments.CreateDepositPaymentAsync(reservation.Id, t.CustomerUserId, new CreatePaymentModel { Provider = "MoMo" }, Context)
            .ContinueWith(task => task.IsFaulted ? null : task.Result);
        // MoMo create needs the MoMo API (answered by the fake handler without a payUrl): fall back to VNPay
        payment ??= await t.Payments.CreateDepositPaymentAsync(reservation.Id, t.CustomerUserId, new CreatePaymentModel { Provider = "VNPay" }, Context);

        t.Clock.Advance(TimeSpan.FromMinutes(11));
        var run = await t.Jobs.RunMinutelyAsync();

        run.ExpiredReservations.Should().Be(1);
        (await t.Reservations.GetAsync(t.CustomerUserId, reservation.Id)).Status.Should().Be(ReservationStatus.Expired);
        (await t.Db.Slots.FindAsync(slot.Id))!.Status.Should().Be(SlotStatus.Available);
        t.CounterOf(slot.ZoneId).AvailableSpaces.Should().Be(counterBefore);

        // FR-RSV-08: the money arrives after all → refunded through the gateway
        var late = await t.Payments.HandleCallbackAsync("VNPay", PaymentSignatureTests.SignedVnPayReturn(payment.Code, 3_000_000, "00"));
        late.Outcome.Should().Be("Refunded");
        var refund = await t.Db.Payments.SingleAsync(p => p.Purpose == PaymentPurpose.Refund);
        refund.Status.Should().Be(PaymentStatus.Refunded);
        refund.Amount.Should().Be(30_000);
        t.Handler.Requests.Should().ContainSingle(r => r.Contains("\"vnp_Command\":\"refund\""));
    }

    [Fact]
    public async Task Cancelling_Early_Refunds_The_Whole_Deposit_And_Late_Refunds_Nothing()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (early, _) = await t.ConfirmedReservationAsync(arrivalInMinutes: 90);

        var cancelled = await t.Reservations.CancelAsync(t.CustomerUserId, early.Id, "Đổi kế hoạch");

        cancelled.Status.Should().Be(ReservationStatus.Cancelled);
        cancelled.RefundAmount.Should().Be(30_000);
        (await t.Db.Slots.FindAsync(early.SlotId))!.Status.Should().Be(SlotStatus.Available);

        var (late, _) = await t.ConfirmedReservationAsync(arrivalInMinutes: 20);
        (await t.Reservations.CancelAsync(t.CustomerUserId, late.Id, null)).RefundAmount.Should().Be(0);
    }

    [Fact]
    public async Task Lot_Cancellation_Refunds_100_Percent()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (reservation, _) = await t.ConfirmedReservationAsync(arrivalInMinutes: 20);
        t.Lot.ManagerUserId = "manager-1";
        await t.Db.SaveChangesAsync();
        t.ActAs("manager-1", "Manager");

        var cancelled = await t.Reservations.CancelByLotAsync(reservation.Id, "Bảo trì slot");

        cancelled.Status.Should().Be(ReservationStatus.LotCancelled);
        cancelled.RefundAmount.Should().Be(30_000);
    }

    [Fact]
    public async Task No_Show_Keeps_The_Deposit_And_Frees_The_Slot()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (reservation, _) = await t.ConfirmedReservationAsync(arrivalInMinutes: 30);

        t.Clock.Advance(TimeSpan.FromMinutes(46));
        (await t.Jobs.RunMinutelyAsync()).NoShows.Should().Be(1);

        var after = await t.Reservations.GetAsync(t.CustomerUserId, reservation.Id);
        after.Status.Should().Be(ReservationStatus.NoShow);
        after.RefundAmount.Should().Be(0);
        (await t.Db.Slots.FindAsync(reservation.SlotId))!.Status.Should().Be(SlotStatus.Available);
    }

    [Fact]
    public async Task Changing_The_Slot_Moves_The_Hold()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (reservation, _) = await t.ConfirmedReservationAsync(arrivalInMinutes: 90);
        var other = t.FreeCarSlot();

        var changed = await t.Reservations.UpdateAsync(t.CustomerUserId, reservation.Id, new UpdateReservationModel { SlotId = other.Id });

        changed.SlotId.Should().Be(other.Id);
        (await t.Db.Slots.FindAsync(other.Id))!.Status.Should().Be(SlotStatus.Reserved);
        (await t.Db.Slots.FindAsync(reservation.SlotId))!.Status.Should().Be(SlotStatus.Available);
        changed.Status.Should().Be(ReservationStatus.Confirmed);
    }
}
