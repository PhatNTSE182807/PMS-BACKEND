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

    public async Task<IReadOnlyList<string>> ActiveCustomerIdsAsync() =>
        await userManager.Users.Where(u => u.Role == CustomerRole && u.IsActive).Select(u => u.Id).ToListAsync();
}
