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
    public string UserId => claimService.GetUserId();

    public string Role => claimService.GetClaim(ClaimTypes.Role);

    public async Task EnsureCanOperateAsync(int lotId)
    {
        if (!await CanOperateAsync(UserId, Role, lotId))
            throw new ForbiddenException("Bạn không được phân công ở bãi này. You are not assigned to this parking lot.");
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
            throw new ForbiddenException("Bạn không quản lý bãi này. You do not manage this parking lot.");
    }
}
