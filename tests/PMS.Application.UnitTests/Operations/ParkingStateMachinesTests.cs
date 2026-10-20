using FluentAssertions;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>Only the arrows drawn in SRS 4.3 are allowed.</summary>
public class ParkingStateMachinesTests
{
    [Theory]
    [InlineData(ReservationStatus.PendingDeposit, ReservationStatus.Confirmed)]
    [InlineData(ReservationStatus.PendingDeposit, ReservationStatus.Expired)]
    [InlineData(ReservationStatus.Confirmed, ReservationStatus.Confirmed)]
    [InlineData(ReservationStatus.Confirmed, ReservationStatus.CheckedIn)]
    [InlineData(ReservationStatus.Confirmed, ReservationStatus.Cancelled)]
    [InlineData(ReservationStatus.Confirmed, ReservationStatus.LotCancelled)]
    [InlineData(ReservationStatus.Confirmed, ReservationStatus.NoShow)]
    [InlineData(ReservationStatus.CheckedIn, ReservationStatus.Completed)]
    public void Drawn_Reservation_Moves_Are_Allowed(string from, string to)
    {
        ParkingStateMachines.CanMoveReservation(from, to).Should().BeTrue();
    }

    [Theory]
    [InlineData(ReservationStatus.PendingDeposit, ReservationStatus.CheckedIn)]
    [InlineData(ReservationStatus.PendingDeposit, ReservationStatus.LotCancelled)]
    [InlineData(ReservationStatus.Expired, ReservationStatus.Confirmed)]
    [InlineData(ReservationStatus.NoShow, ReservationStatus.CheckedIn)]
    [InlineData(ReservationStatus.Cancelled, ReservationStatus.Confirmed)]
    [InlineData(ReservationStatus.Completed, ReservationStatus.Cancelled)]
    [InlineData(ReservationStatus.CheckedIn, ReservationStatus.Cancelled)]
    public void Other_Reservation_Moves_Are_Refused(string from, string to)
    {
        ParkingStateMachines.CanMoveReservation(from, to).Should().BeFalse();
        var act = () => ParkingStateMachines.EnsureReservation(from, to);
        act.Should().Throw<BadRequestException>();
    }

    [Theory]
    [InlineData(SessionStatus.PendingEntry, SessionStatus.Active)]
    [InlineData(SessionStatus.PendingEntry, SessionStatus.Cancelled)]
    [InlineData(SessionStatus.Active, SessionStatus.PendingPayment)]
    [InlineData(SessionStatus.Active, SessionStatus.LostTicketPending)]
    [InlineData(SessionStatus.Active, SessionStatus.AbnormalClosed)]
    [InlineData(SessionStatus.Active, SessionStatus.EmergencyExit)]
    [InlineData(SessionStatus.LostTicketPending, SessionStatus.Active)]
    [InlineData(SessionStatus.LostTicketPending, SessionStatus.PendingPayment)]
    [InlineData(SessionStatus.PendingPayment, SessionStatus.Paid)]
    [InlineData(SessionStatus.PendingPayment, SessionStatus.Active)]
    [InlineData(SessionStatus.Paid, SessionStatus.Completed)]
    [InlineData(SessionStatus.Paid, SessionStatus.PendingPayment)]
    [InlineData(SessionStatus.EmergencyExit, SessionStatus.Completed)]
    public void Drawn_Session_Moves_Are_Allowed(string from, string to)
    {
        ParkingStateMachines.CanMoveSession(from, to).Should().BeTrue();
    }

    [Theory]
    [InlineData(SessionStatus.Active, SessionStatus.Completed)]
    [InlineData(SessionStatus.Active, SessionStatus.Paid)]
    [InlineData(SessionStatus.PendingEntry, SessionStatus.PendingPayment)]
    [InlineData(SessionStatus.Completed, SessionStatus.Active)]
    [InlineData(SessionStatus.Cancelled, SessionStatus.Active)]
    [InlineData(SessionStatus.AbnormalClosed, SessionStatus.Active)]
    public void Other_Session_Moves_Are_Refused(string from, string to)
    {
        ParkingStateMachines.CanMoveSession(from, to).Should().BeFalse();
    }

    [Theory]
    [InlineData(SlotStatus.Available, SlotStatus.Reserved, true)]
    [InlineData(SlotStatus.Reserved, SlotStatus.Available, true)]
    [InlineData(SlotStatus.Reserved, SlotStatus.Occupied, true)]
    [InlineData(SlotStatus.Occupied, SlotStatus.Available, true)]
    [InlineData(SlotStatus.Reserved, SlotStatus.Maintenance, false)]
    [InlineData(SlotStatus.Maintenance, SlotStatus.Reserved, false)]
    public void Slot_Moves_Follow_The_Slot_Machine(string from, string to, bool allowed)
    {
        ParkingStateMachines.CanMoveSlot(from, to).Should().Be(allowed);
    }
}
