using Microsoft.EntityFrameworkCore;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Helpers.Parking;

/// <summary>
/// A lot is staffed when it has an active Manager and at least one active Staff member. A lot that is
/// open (Active) but not staffed is shown as Maintenance: customers can see it but cannot book it.
/// Worked out on every read, so assigning, locking or removing people updates the lot at once.
/// </summary>
public static class LotStaffing
{
    private const string ActiveEmployee = "Active";
    private const string ManagerPosition = "Manager";

    public const string UnstaffedNotice =
        "This lot has no Manager and Staff on duty yet. You can view it, but it cannot take bookings.";

    /// <summary>The ids, among <paramref name="lotIds"/>, of the lots that have an active Manager and active Staff.</summary>
    public static async Task<HashSet<int>> StaffedLotIdsAsync(PmsDbContext db, IEnumerable<int> lotIds)
    {
        var ids = lotIds.Distinct().ToList();
        if (ids.Count == 0) return [];

        var withManager = await db.ParkingLots
            .Where(p => ids.Contains(p.Id) && p.ManagerUserId != null)
            .Where(p => db.Employees.Any(e => e.UserId == p.ManagerUserId && e.Status == ActiveEmployee))
            .Select(p => p.Id)
            .ToListAsync();
        if (withManager.Count == 0) return [];

        var withStaff = await db.Employees
            .Where(e => e.ParkingLotId != null && withManager.Contains(e.ParkingLotId.Value)
                        && e.Status == ActiveEmployee && e.Position != ManagerPosition)
            .Select(e => e.ParkingLotId.Value)
            .Distinct()
            .ToListAsync();

        return withStaff.ToHashSet();
    }

    public static async Task<bool> IsStaffedAsync(PmsDbContext db, int lotId) =>
        (await StaffedLotIdsAsync(db, [lotId])).Contains(lotId);

    /// <summary>The status customers and staff see: an open lot without people on duty is under maintenance.</summary>
    public static string EffectiveStatus(string storedStatus, bool staffed) =>
        storedStatus == LotStatus.Active && !staffed ? LotStatus.Maintenance : storedStatus;

    public static string EffectiveStatus(ParkingLot lot, bool staffed) => EffectiveStatus(lot.Status, staffed);
}
