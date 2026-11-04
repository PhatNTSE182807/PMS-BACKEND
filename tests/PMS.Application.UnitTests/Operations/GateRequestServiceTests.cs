using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using PMS.Application.Exceptions;
using PMS.Application.Models.Operations;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>Customers ask Staff from the app to open the barrier (check-in with a booking, check-out when paid).</summary>
public class GateRequestServiceTests
{
    private static (GateRequestService Service, IParkingRealtimeNotifier Notifier) Create(OperationsTestContext t)
    {
        var notifier = Substitute.For<IParkingRealtimeNotifier>();
        var service = new GateRequestService(t.Db, t.Gates, new LotScopeService(t.Db, t.Claims), t.Options, t.Clock, notifier);
        return (service, notifier);
    }

    /// <summary>A confirmed booking whose arrival window is open now.</summary>
    private static async Task<(OperationsTestContext T, GateRequestService Service, IParkingRealtimeNotifier Notifier, ReservationResponseModel Reservation)> BookedAsync()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (reservation, _) = await t.ConfirmedReservationAsync(arrivalInMinutes: 5);
        var (service, notifier) = Create(t);
        return (t, service, notifier, reservation);
    }

    /// <summary>The booked car is parked; returns its session id.</summary>
    private static async Task<long> ParkedAsync(OperationsTestContext t, ReservationResponseModel reservation)
    {
        var result = await t.Gates.CheckInAsync(t.Lot.Id, new CheckInModel { PlateNumber = t.Car.PlateNumber, ReservationCode = reservation.Code });
        await t.Gates.ConfirmEntryAsync(result.Session.Id);
        return result.Session.Id;
    }

    // ---- Check-in requests ----

    [Fact]
    public async Task RequestCheckInAsync_Should_Queue_Once_And_Notify_Staff()
    {
        var (t, service, notifier, reservation) = await BookedAsync();

        var first = await service.RequestCheckInAsync(t.CustomerUserId, reservation.Id);
        var again = await service.RequestCheckInAsync(t.CustomerUserId, reservation.Id);

        again.Id.Should().Be(first.Id);
        first.Status.Should().Be(GateRequestStatus.Waiting);
        first.Kind.Should().Be(GateRequestKind.CheckIn);
        first.PlateNumber.Should().Be(t.Car.PlateNumber);
        first.LotName.Should().Be(t.Lot.Name);
        first.ReservationCode.Should().Be(reservation.Code);
        first.SlotCode.Should().NotBeNull();
        first.Problem.Should().BeNull();
        await notifier.Received(1).GateRequestChangedAsync(t.Lot.Id, t.CustomerUserId, first.Id, GateRequestKind.CheckIn, GateRequestStatus.Waiting);
    }

    [Fact]
    public async Task RequestCheckInAsync_Should_Refuse_Unknown_Early_Late_Or_Used_Bookings()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (reservation, _) = await t.ConfirmedReservationAsync(arrivalInMinutes: 60);
        var (service, _) = Create(t);

        await FluentActions.Invoking(() => service.RequestCheckInAsync("someone-else", reservation.Id))
            .Should().ThrowAsync<NotFoundException>();
        await FluentActions.Invoking(() => service.RequestCheckInAsync(t.CustomerUserId, reservation.Id))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Too early*");

        t.Clock.Advance(TimeSpan.FromHours(3));
        await FluentActions.Invoking(() => service.RequestCheckInAsync(t.CustomerUserId, reservation.Id))
            .Should().ThrowAsync<BadRequestException>().WithMessage("The hold has ended*");
    }

    [Fact]
    public async Task RequestCheckInAsync_Should_Refuse_A_Booking_Already_Checked_In()
    {
        var (t, service, _, reservation) = await BookedAsync();
        await ParkedAsync(t, reservation);

        await FluentActions.Invoking(() => service.RequestCheckInAsync(t.CustomerUserId, reservation.Id))
            .Should().ThrowAsync<BadRequestException>().WithMessage("This booking has already checked in.");
    }

    [Fact]
    public async Task RequestCheckInAsync_Should_Refuse_A_Cancelled_Booking_And_A_Parked_Vehicle()
    {
        var (t, service, _, reservation) = await BookedAsync();
        var entity = t.Db.Reservations.Single(r => r.Id == reservation.Id);

        entity.Status = ReservationStatus.Cancelled;
        await t.Db.SaveChangesAsync();
        await FluentActions.Invoking(() => service.RequestCheckInAsync(t.CustomerUserId, reservation.Id))
            .Should().ThrowAsync<BadRequestException>().WithMessage("This booking is Cancelled*");

        entity.Status = ReservationStatus.Confirmed;
        await t.Db.SaveChangesAsync();
        await t.Gates.CheckInAsync(t.Lot.Id, new CheckInModel { PlateNumber = t.Car.PlateNumber, VehicleTypeId = t.Car.VehicleTypeId, IgnoreReservation = true });

        await FluentActions.Invoking(() => service.RequestCheckInAsync(t.CustomerUserId, reservation.Id))
            .Should().ThrowAsync<BadRequestException>().WithMessage("This vehicle is already parked.");
    }

    // ---- Customer cancel and history ----

    [Fact]
    public async Task CancelAsync_Should_Close_A_Waiting_Request_Only()
    {
        var (t, service, _, reservation) = await BookedAsync();
        var request = await service.RequestCheckInAsync(t.CustomerUserId, reservation.Id);

        await FluentActions.Invoking(() => service.CancelAsync("someone-else", request.Id)).Should().ThrowAsync<NotFoundException>();

        var cancelled = await service.CancelAsync(t.CustomerUserId, request.Id);
        cancelled.Status.Should().Be(GateRequestStatus.Cancelled);
        cancelled.Problem.Should().BeNull();

        await FluentActions.Invoking(() => service.CancelAsync(t.CustomerUserId, request.Id))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Staff already handled this request.");

        var mine = await service.GetMineAsync(t.CustomerUserId);
        mine.Should().ContainSingle(r => r.Id == request.Id);
    }

    // ---- Staff queue and decisions ----

    [Fact]
    public async Task GetQueueAsync_Should_Filter_By_Kind_And_Reject_Unknown_Kinds()
    {
        var (t, service, _, reservation) = await BookedAsync();
        await service.RequestCheckInAsync(t.CustomerUserId, reservation.Id);

        (await service.GetQueueAsync(t.Lot.Id, null)).Should().ContainSingle();
        (await service.GetQueueAsync(t.Lot.Id, " checkin ")).Should().ContainSingle();
        (await service.GetQueueAsync(t.Lot.Id, "CheckOut")).Should().BeEmpty();
        await FluentActions.Invoking(() => service.GetQueueAsync(t.Lot.Id, "Teleport"))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task GetQueueAsync_Should_Drop_Requests_Handled_At_The_Lane()
    {
        var (t, service, notifier, reservation) = await BookedAsync();
        var request = await service.RequestCheckInAsync(t.CustomerUserId, reservation.Id);
        await ParkedAsync(t, reservation);

        (await service.GetQueueAsync(t.Lot.Id, null)).Should().BeEmpty();

        var stored = t.Db.GateRequests.Single(g => g.Id == request.Id);
        stored.Status.Should().Be(GateRequestStatus.Cancelled);
        stored.Note.Should().Be("Handled at the lane without the app request.");
        await notifier.Received().GateRequestChangedAsync(t.Lot.Id, t.CustomerUserId, request.Id, GateRequestKind.CheckIn, GateRequestStatus.Cancelled);
    }

    [Fact]
    public async Task GetQueueAsync_Should_Refuse_Staff_Of_Another_Lot()
    {
        var (t, service, _, _) = await BookedAsync();
        t.ActAs("stranger", "Staff");

        await FluentActions.Invoking(() => service.GetQueueAsync(t.Lot.Id, null)).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task ApproveAsync_CheckIn_Should_Open_The_Barrier_And_Complete_The_Request()
    {
        var (t, service, _, reservation) = await BookedAsync();
        var request = await service.RequestCheckInAsync(t.CustomerUserId, reservation.Id);

        var decision = await service.ApproveAsync(request.Id, null);

        decision.BarrierOpen.Should().BeTrue();
        decision.CheckIn.Should().NotBeNull();
        decision.Request.Status.Should().Be(GateRequestStatus.Completed);
        decision.Request.SessionId.Should().NotBeNull();
        decision.Request.HandledAt.Should().NotBeNull();

        await FluentActions.Invoking(() => service.ApproveAsync(request.Id, new ApproveGateRequestModel()))
            .Should().ThrowAsync<BadRequestException>().WithMessage("This request was already handled.");
    }

    [Fact]
    public async Task ApproveAsync_Should_Refuse_A_Request_The_Customer_Cancelled_Or_A_Missing_One()
    {
        var (t, service, _, reservation) = await BookedAsync();
        var request = await service.RequestCheckInAsync(t.CustomerUserId, reservation.Id);
        await service.CancelAsync(t.CustomerUserId, request.Id);

        await FluentActions.Invoking(() => service.ApproveAsync(request.Id, new ApproveGateRequestModel()))
            .Should().ThrowAsync<BadRequestException>().WithMessage("The customer cancelled this request.");
        await FluentActions.Invoking(() => service.ApproveAsync(987654, new ApproveGateRequestModel()))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task RefuseAsync_Should_Need_A_Reason_And_Close_The_Request()
    {
        var (t, service, _, reservation) = await BookedAsync();
        var request = await service.RequestCheckInAsync(t.CustomerUserId, reservation.Id);

        await FluentActions.Invoking(() => service.RefuseAsync(request.Id, new RefuseGateRequestModel { Reason = " " }))
            .Should().ThrowAsync<BadRequestException>();
        await FluentActions.Invoking(() => service.RefuseAsync(request.Id, null))
            .Should().ThrowAsync<BadRequestException>();

        var refused = await service.RefuseAsync(request.Id, new RefuseGateRequestModel { Reason = "  Wrong car  " });

        refused.Status.Should().Be(GateRequestStatus.Refused);
        refused.Note.Should().Be("Wrong car");
    }

    // ---- Check-out requests ----

    [Fact]
    public async Task RequestCheckOutAsync_Should_Need_A_Parked_Vehicle_Of_The_Customer()
    {
        var (t, service, _, reservation) = await BookedAsync();

        await FluentActions.Invoking(() => service.RequestCheckOutAsync("no-profile", 1))
            .Should().ThrowAsync<NotFoundException>().WithMessage("Customer profile not found.");
        await FluentActions.Invoking(() => service.RequestCheckOutAsync(t.CustomerUserId, 999999))
            .Should().ThrowAsync<NotFoundException>().WithMessage("Parking session not found.");

        var sessionId = await ParkedAsync(t, reservation);
        t.Db.ParkingSessions.Single(s => s.Id == sessionId).Status = SessionStatus.Completed;
        await t.Db.SaveChangesAsync();

        await FluentActions.Invoking(() => service.RequestCheckOutAsync(t.CustomerUserId, sessionId))
            .Should().ThrowAsync<BadRequestException>().WithMessage("This vehicle is not parked.");
    }

    [Fact]
    public async Task RequestCheckOutAsync_Should_Warn_Staff_Until_The_Fee_Is_Paid()
    {
        var (t, service, _, reservation) = await BookedAsync();
        var sessionId = await ParkedAsync(t, reservation);
        t.Clock.Advance(TimeSpan.FromHours(2));

        var request = await service.RequestCheckOutAsync(t.CustomerUserId, sessionId);
        var again = await service.RequestCheckOutAsync(t.CustomerUserId, sessionId);

        again.Id.Should().Be(request.Id);
        request.Kind.Should().Be(GateRequestKind.CheckOut);
        request.SessionId.Should().Be(sessionId);
        request.Problem.Should().StartWith("Not paid yet");

        // The gate refuses to open while a fee is due; the request keeps waiting
        await FluentActions.Invoking(() => service.ApproveAsync(request.Id, new ApproveGateRequestModel()))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*still due*");
        t.Db.GateRequests.Single(g => g.Id == request.Id).Status.Should().Be(GateRequestStatus.Waiting);
    }

    [Fact]
    public async Task ApproveAsync_CheckOut_Should_Let_A_Paid_Vehicle_Out()
    {
        var (t, service, _, reservation) = await BookedAsync();
        var sessionId = await ParkedAsync(t, reservation);
        t.Clock.Advance(TimeSpan.FromHours(2));
        var due = await t.Gates.RequestPaymentAsync(sessionId, new CheckoutQuoteRequestModel());
        await t.Gates.PayCashAsync(sessionId, new CashPaymentModel { Amount = due.AmountDue });

        var request = await service.RequestCheckOutAsync(t.CustomerUserId, sessionId);
        request.Problem.Should().BeNull();

        var decision = await service.ApproveAsync(request.Id, new ApproveGateRequestModel());

        decision.BarrierOpen.Should().BeTrue();
        decision.Request.Status.Should().Be(GateRequestStatus.Completed);
    }

    // ---- Paid vehicles go to the Check-out lane on their own ----

    /// <summary>A walk-in (no booking, no account) parked two hours ago and paid in cash at the POS.</summary>
    private static async Task<long> PaidWalkInAsync(OperationsTestContext t, string plate = "30E-555.55")
    {
        var entry = await t.Gates.CheckInAsync(t.Lot.Id, new CheckInModel { PlateNumber = plate, VehicleTypeId = t.TypeId("CAR_4") });
        await t.Gates.ConfirmEntryAsync(entry.Session.Id);
        t.Clock.Advance(TimeSpan.FromHours(2));
        var due = await t.Gates.RequestPaymentAsync(entry.Session.Id, new CheckoutQuoteRequestModel());
        await t.Gates.PayCashAsync(entry.Session.Id, new CashPaymentModel { Amount = due.AmountDue });
        return entry.Session.Id;
    }

    [Fact]
    public async Task GetQueueAsync_Should_Queue_A_Paid_Walk_In_For_The_Check_Out_Lane()
    {
        var (t, service, notifier, _) = await BookedAsync();
        var sessionId = await PaidWalkInAsync(t);

        var queue = await service.GetQueueAsync(t.Lot.Id, GateRequestKind.CheckOut);

        var request = queue.Should().ContainSingle().Subject;
        request.SessionId.Should().Be(sessionId);
        request.PlateNumber.Should().Be("30E-555.55");
        request.CustomerName.Should().BeNull();
        request.VehicleTypeName.Should().NotBeNullOrEmpty();
        request.Problem.Should().BeNull();
        await notifier.Received().GateRequestChangedAsync(t.Lot.Id, null, request.Id, GateRequestKind.CheckOut, GateRequestStatus.Waiting);

        // Reading the queue again does not add a second request
        (await service.GetQueueAsync(t.Lot.Id, GateRequestKind.CheckOut)).Should().ContainSingle();
        (await service.GetQueueAsync(t.Lot.Id, GateRequestKind.CheckIn)).Should().BeEmpty();

        var decision = await service.ApproveAsync(request.Id, new ApproveGateRequestModel());
        decision.BarrierOpen.Should().BeTrue();
        (await service.GetQueueAsync(t.Lot.Id, GateRequestKind.CheckOut)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetQueueAsync_Should_Queue_A_Paid_Customer_With_Their_Account()
    {
        var (t, service, _, reservation) = await BookedAsync();
        var sessionId = await ParkedAsync(t, reservation);
        t.Clock.Advance(TimeSpan.FromHours(2));
        var due = await t.Gates.RequestPaymentAsync(sessionId, new CheckoutQuoteRequestModel());
        await t.Gates.PayCashAsync(sessionId, new CashPaymentModel { Amount = due.AmountDue });

        var request = (await service.GetQueueAsync(t.Lot.Id, GateRequestKind.CheckOut)).Should().ContainSingle().Subject;

        request.SessionId.Should().Be(sessionId);
        request.CustomerName.Should().NotBeNull();
        // The customer follows it in the app, and pressing "Check out" there reuses it
        (await service.GetMineAsync(t.CustomerUserId)).Should().ContainSingle(r => r.Id == request.Id);
        (await service.RequestCheckOutAsync(t.CustomerUserId, sessionId)).Id.Should().Be(request.Id);
    }

    [Fact]
    public async Task GetQueueAsync_Should_Not_Queue_Unpaid_Or_Simulated_Vehicles()
    {
        var (t, service, _, _) = await BookedAsync();
        var unpaid = await t.Gates.CheckInAsync(t.Lot.Id, new CheckInModel { PlateNumber = "30E-111.11", VehicleTypeId = t.TypeId("CAR_4") });
        await t.Gates.ConfirmEntryAsync(unpaid.Session.Id);
        var simulatedId = await PaidWalkInAsync(t, "30E-222.22");
        t.Db.ParkingSessions.Single(s => s.Id == simulatedId).IsSimulated = true;
        await t.Db.SaveChangesAsync();

        (await service.GetQueueAsync(t.Lot.Id, GateRequestKind.CheckOut)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetQueueAsync_Should_Not_Requeue_A_Refused_Vehicle_Until_It_Pays_Again()
    {
        var (t, service, _, _) = await BookedAsync();
        var sessionId = await PaidWalkInAsync(t);
        var request = (await service.GetQueueAsync(t.Lot.Id, GateRequestKind.CheckOut)).Single();

        t.Clock.Advance(TimeSpan.FromMinutes(1));
        await service.RefuseAsync(request.Id, new RefuseGateRequestModel { Reason = "The plate at the lane does not match the parked vehicle." });

        (await service.GetQueueAsync(t.Lot.Id, GateRequestKind.CheckOut)).Should().BeEmpty();
        t.Db.ParkingSessions.Single(s => s.Id == sessionId).Status.Should().Be(SessionStatus.Paid);
    }

    [Fact]
    public async Task Waiting_CheckIn_Request_Should_Explain_Why_The_Barrier_Would_Not_Open()
    {
        var (t, _, _, reservation) = await BookedAsync();
        // No real-time notifier: the service still works
        var service = new GateRequestService(t.Db, t.Gates, new LotScopeService(t.Db, t.Claims), t.Options, t.Clock);
        var request = await service.RequestCheckInAsync(t.CustomerUserId, reservation.Id);
        var entity = t.Db.Reservations.Single(r => r.Id == reservation.Id);
        async Task<string> ProblemAsync() => (await service.GetMineAsync(t.CustomerUserId)).Single(r => r.Id == request.Id).Problem;

        entity.Status = ReservationStatus.Cancelled;
        await t.Db.SaveChangesAsync();
        (await ProblemAsync()).Should().Be("The booking is Cancelled.");

        entity.Status = ReservationStatus.Confirmed;
        entity.StartTime = t.Now.AddHours(2);
        await t.Db.SaveChangesAsync();
        (await ProblemAsync()).Should().StartWith("Too early");

        entity.StartTime = t.Now.AddHours(-2);
        entity.EndTime = t.Now.AddMinutes(-1);
        await t.Db.SaveChangesAsync();
        (await ProblemAsync()).Should().StartWith("The hold has ended");

        t.Db.Reservations.Remove(entity);
        await t.Db.SaveChangesAsync();
        (await ProblemAsync()).Should().Be("The booking no longer exists.");
    }

    [Fact]
    public async Task Waiting_CheckOut_Request_Should_Explain_A_Missing_Session()
    {
        var (t, service, _, reservation) = await BookedAsync();
        var sessionId = await ParkedAsync(t, reservation);
        var request = await service.RequestCheckOutAsync(t.CustomerUserId, sessionId);

        t.Db.ParkingSessions.Remove(t.Db.ParkingSessions.Single(s => s.Id == sessionId));
        await t.Db.SaveChangesAsync();

        (await service.GetMineAsync(t.CustomerUserId)).Single(r => r.Id == request.Id).Problem
            .Should().Be("The parking session no longer exists.");
    }

    [Fact]
    public async Task Waiting_CheckOut_Request_Should_Warn_When_The_Exit_Window_Has_Passed()
    {
        var (t, service, _, reservation) = await BookedAsync();
        var sessionId = await ParkedAsync(t, reservation);
        t.Clock.Advance(TimeSpan.FromHours(2));
        var due = await t.Gates.RequestPaymentAsync(sessionId, new CheckoutQuoteRequestModel());
        await t.Gates.PayCashAsync(sessionId, new CashPaymentModel { Amount = due.AmountDue });
        var request = await service.RequestCheckOutAsync(t.CustomerUserId, sessionId);

        var session = t.Db.ParkingSessions.Single(s => s.Id == sessionId);
        session.ExitWindowEndsAt = t.Now.AddMinutes(-1);
        await t.Db.SaveChangesAsync();

        var queue = await service.GetQueueAsync(t.Lot.Id, GateRequestKind.CheckOut);

        queue.Single(r => r.Id == request.Id).Problem.Should().StartWith("The exit window has passed");

        // Still inside the window: nothing stops the barrier
        session.ExitWindowEndsAt = t.Now.AddMinutes(10);
        await t.Db.SaveChangesAsync();
        (await service.GetQueueAsync(t.Lot.Id, GateRequestKind.CheckOut)).Single(r => r.Id == request.Id).Problem.Should().BeNull();
    }
}
