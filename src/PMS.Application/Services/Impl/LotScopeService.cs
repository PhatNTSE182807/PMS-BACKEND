using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.DataAccess.Persistence;
using PMS.Shared.Services;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Lot scope of internal accounts (SRS section 8, 11): Admins act on every lot, a Manager on the lots
/// they manage (ParkingLots.ManagerUserId), a Staff on the lot they are assigned to (Employees.ParkingLotId).
/// </summary>
public class LotScopeService(PmsDbContext pmsContext, IClaimService claimService)
{
    private (string UserId, string Role)? _actor;

    public string UserId => _actor?.UserId ?? claimService.GetUserId();

    public string Role => _actor?.Role ?? claimService.GetClaim(ClaimTypes.Role);

    /// <summary>
    /// Background work with no signed-in user (the walk-in simulation's automatic exits) acts as this
    /// account for the rest of the scope. The account's own role still decides what it may do.
    /// </summary>
    public void ActAs(string userId, string role) => _actor = (userId, role);

    public async Task EnsureCanOperateAsync(int lotId)
    {
        if (!await CanOperateAsync(UserId, Role, lotId))
            throw new ForbiddenException("You are not assigned to this parking lot.");
    }

    public async Task<bool> CanOperateAsync(string userId, string role, int lotId)
    {
        if (string.IsNullOrEmpty(userId)) return false;

        return role switch
        {
            "Admin" => await pmsContext.ParkingLots.AnyAsync(p => p.Id == lotId && !p.IsDeleted),
            "Manager" => await pmsContext.ParkingLots.AnyAsync(p => p.Id == lotId && !p.IsDeleted && p.ManagerUserId == userId),
            "Staff" => await pmsContext.Employees.AnyAsync(e => e.UserId == userId && e.ParkingLotId == lotId && e.Status != "Terminated"),
            _ => false
        };
    }

    /// <summary>Managers only (lot-side cancellation, approvals); Admins pass too.</summary>
    public async Task EnsureCanManageAsync(int lotId)
    {
        if (Role is not ("Manager" or "Admin") || !await CanOperateAsync(UserId, Role, lotId))
            throw new ForbiddenException("You do not manage this parking lot.");
    }
}
