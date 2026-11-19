using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Identity;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Default <see cref="ICustomerAudience"/>: reads the accounts, not the Customers table, whose row only appears with
/// a customer's first payment.
/// </summary>
public class IdentityCustomerAudience(UserManager<ApplicationUser> userManager) : ICustomerAudience
{
    private const string CustomerRole = "User";
    private const string AdminRole = "Admin";

    public async Task<IReadOnlyList<string>> ActiveCustomerIdsAsync() =>
        await userManager.Users.Where(u => u.Role == CustomerRole && u.IsActive).Select(u => u.Id).ToListAsync();

    public async Task<IReadOnlyList<string>> ActiveAdminIdsAsync() =>
        await userManager.Users.Where(u => u.Role == AdminRole && u.IsActive).Select(u => u.Id).ToListAsync();

    public async Task<(string UserName, string Email)> AccountAsync(string userId)
    {
        var account = await userManager.Users.Where(u => u.Id == userId)
            .Select(u => new { u.UserName, u.Email })
            .FirstOrDefaultAsync();
        return (account?.UserName, account?.Email);
    }
}
