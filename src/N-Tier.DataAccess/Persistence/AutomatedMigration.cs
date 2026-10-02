using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using N_Tier.DataAccess.Identity;

namespace N_Tier.DataAccess.Persistence;

public static class AutomatedMigration
{
    public static async Task MigrateAsync(IServiceProvider services)
    {
        var logger = services.GetService<ILogger<DatabaseContext>>();

        try
        {
            var context = services.GetRequiredService<DatabaseContext>();

            if (context.Database.IsSqlServer())
            {
                logger?.LogInformation("Applying database migrations...");
                await context.Database.MigrateAsync();
                logger?.LogInformation("Database migration completed successfully.");
            }

            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

            await DatabaseContextSeed.SeedDatabaseAsync(context, userManager);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "An error occurred while migrating or seeding the database. Please check your SQL Server connection string.");
            throw;
        }
    }
}
