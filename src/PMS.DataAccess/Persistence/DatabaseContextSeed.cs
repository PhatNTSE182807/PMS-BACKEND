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

        // SQL Server gets these from AutomatedMigration; an in-memory database (tests, demo) needs them
        // too, since parking lot models build their slots for these vehicle types
        if (!pmsContext.VehicleTypes.Any())
        {
            pmsContext.VehicleTypes.AddRange(
                new VehicleType { Code = "CAR_4", Name = "4-Seat Car", Description = "Sedan/Hatchback up to 5 seats", SortOrder = 1, IsActive = true },
                new VehicleType { Code = "CAR_7", Name = "7-Seat Car", Description = "SUV/MPV up to 7 seats", SortOrder = 2, IsActive = true },
                // Only 4-seat cars, 7-seat cars and motorbikes are accepted
                new VehicleType { Code = "MOTORBIKE", Name = "Motorbike", Description = "Two-wheeled motorcycles", SortOrder = 3, IsActive = true });
            await pmsContext.SaveChangesAsync();
        }

        await SeedUserAsync(pmsContext, userManager,
            "systemadmin", "System administrator", "admin@gmail.com", "0977888888", "Admin123@", "Admin");

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
