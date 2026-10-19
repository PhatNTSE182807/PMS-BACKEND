using PMS.Application.Exceptions;
using PMS.Application.Models.Operations;

namespace PMS.Application.Helpers.Parking;

/// <summary>
/// The arrows of the SRS state machines (section 4.3: "không được chuyển trạng thái ngoài các mũi tên đã vẽ").
/// Services call <c>EnsureCan…</c> before every status change.
/// </summary>
public static class ParkingStateMachines
{
    private static readonly Dictionary<string, string[]> ReservationMoves = new()
    {
        // Chờ cọc → Đã xác nhận (deposit paid in time) | Hết hạn (deadline passed or the customer gave up)
        [ReservationStatus.PendingDeposit] = [ReservationStatus.Confirmed, ReservationStatus.Expired],
        // Đã xác nhận → itself (change arrival/slot) | Đã check-in | Hủy | Bãi hủy | No-show
        [ReservationStatus.Confirmed] =
        [
            ReservationStatus.Confirmed, ReservationStatus.CheckedIn, ReservationStatus.Cancelled,
            ReservationStatus.LotCancelled, ReservationStatus.NoShow
        ],
        // Đã check-in → Hoàn tất (vehicle left, deposit deducted)
        [ReservationStatus.CheckedIn] = [ReservationStatus.Completed]
    };

    private static readonly Dictionary<string, string[]> SessionMoves = new()
    {
        // Before the sensor: valid once the vehicle passed, cancelled if it turned around
        [SessionStatus.PendingEntry] = [SessionStatus.Active, SessionStatus.Cancelled],
        [SessionStatus.Active] =
        [
            SessionStatus.PendingPayment, SessionStatus.LostTicketPending,
            SessionStatus.AbnormalClosed, SessionStatus.EmergencyExit
        ],
        [SessionStatus.LostTicketPending] = [SessionStatus.Active, SessionStatus.PendingPayment],
        // Paid (or 0 VND), or back to parked when the customer cancels / the wait times out
        [SessionStatus.PendingPayment] = [SessionStatus.Paid, SessionStatus.Active],
        // Leaves through the barrier, or misses the Exit Window (prepaid only) and must pay again
        [SessionStatus.Paid] = [SessionStatus.Completed, SessionStatus.PendingPayment],
        // The Manager waives or bills the fee
        [SessionStatus.EmergencyExit] = [SessionStatus.Completed]
    };

    private static readonly Dictionary<string, string[]> SlotMoves = new()
    {
        [SlotStatus.Available] = [SlotStatus.Reserved, SlotStatus.Maintenance, SlotStatus.Occupied],
        // Held → free (expired, cancelled, no-show, lot cancelled) | parked (check-in)
        [SlotStatus.Reserved] = [SlotStatus.Available, SlotStatus.Occupied],
        [SlotStatus.Occupied] = [SlotStatus.Available],
        [SlotStatus.Maintenance] = [SlotStatus.Available]
    };

    public static bool CanMoveReservation(string from, string to) => Allowed(ReservationMoves, from, to);

    public static bool CanMoveSession(string from, string to) => Allowed(SessionMoves, from, to);

    public static bool CanMoveSlot(string from, string to) => Allowed(SlotMoves, from, to);

    public static void EnsureReservation(string from, string to)
    {
        if (!CanMoveReservation(from, to))
            throw new BadRequestException($"A reservation that is {from} cannot become {to}.");
    }

    public static void EnsureSession(string from, string to)
    {
        if (!CanMoveSession(from, to))
            throw new BadRequestException($"A parking session that is {from} cannot become {to}.");
    }

    private static bool Allowed(Dictionary<string, string[]> moves, string from, string to) =>
        from != null && moves.TryGetValue(from, out var targets) && targets.Contains(to);
}
