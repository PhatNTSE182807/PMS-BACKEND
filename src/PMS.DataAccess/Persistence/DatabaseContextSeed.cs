using Microsoft.AspNetCore.Identity;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.DataAccess.Persistence;

public static class DatabaseContextSeed
{
    public static async Task SeedDatabaseAsync(DatabaseContext context, PmsDbContext pmsContext, UserManager<ApplicationUser> userManager)
    {
        if (!userManager.Users.Any())
        {
            var user = new ApplicationUser
            {
                UserName = "admin",
                Email = "admin@admin.com",
                EmailConfirmed = true,
                PhoneNumber = "0123456789",
                Role = "Admin"
            };

            await userManager.CreateAsync(user, "Admin123.?");
        }

        await SeedUserAsync(pmsContext, userManager,
            "systemadmin", "System administrator", "admin@gmail.com", "0977888888", "Admin 123@", "Admin");

        await SeedUserAsync(pmsContext, userManager,
            "nguyentanphat", "Nguyễn Tấn Phát", "phat@gmail.com", "0977654654", "Phat123@");

        await SeedUserAsync(pmsContext, userManager,
            "buihaminhnhat", "Bùi Hà Minh Nhật", "nhat@gmail.com", "0986456456", "Nhat123@");

        await context.SaveChangesAsync();
    }

    private static async Task SeedUserAsync(
        PmsDbContext pmsContext,
        UserManager<ApplicationUser> userManager,
        string userName,
        string fullName,
        string email,
        string phoneNumber,
        string password,
        string role = "User")
    {
        if (await userManager.FindByEmailAsync(email) != null)
            return;

        var user = new ApplicationUser
        {
            UserName = userName,
            Email = email,
            EmailConfirmed = true,
            PhoneNumber = phoneNumber,
            Role = role
        };

        var result = await userManager.CreateAsync(user, password);

        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Failed to seed user '{email}': {string.Join("; ", result.Errors.Select(e => e.Description))}");

        pmsContext.Customers.Add(new Customer
        {
            UserId = user.Id,
            CustomerCode = $"CUS-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            FullName = fullName,
            Phone = phoneNumber,
            Email = email,
            CustomerType = "Individual",
            MembershipTier = "Standard",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });

        await pmsContext.SaveChangesAsync();
    }
}
