using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Repositories;
using PMS.DataAccess.Repositories.Impl;

namespace PMS.DataAccess;

public static class DataAccessDependencyInjection
{
    public static IServiceCollection AddDataAccess(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDatabase(configuration);

        services.AddIdentity();

        services.AddRepositories();

        return services;
    }

    private static void AddRepositories(this IServiceCollection services)
    {
        // Register repositories here
    }

    private static void AddDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var databaseConfig = configuration.GetSection("Database").Get<DatabaseConfiguration>() ?? new DatabaseConfiguration();
        var connectionString = databaseConfig.GetResolvedConnectionString();

        // Propagate resolved connection string back to configuration for downstream consumers (e.g. Hangfire)
        if (!string.IsNullOrEmpty(connectionString))
        {
            configuration["Database:ConnectionString"] = connectionString;
        }

        if (databaseConfig.UseInMemoryDatabase)
        {
            services.AddDbContext<DatabaseContext>(options =>
            {
                options.UseInMemoryDatabase("NTierDatabase");
                options.ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning));
            });

            services.AddDbContext<PmsDbContext>(options =>
            {
                options.UseInMemoryDatabase("NTierDatabase");
                options.ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning));
            });
        }
        else
        {
            services.AddDbContext<DatabaseContext>(options =>
            {
                options.UseSqlServer(connectionString,
                    opt => opt.MigrationsAssembly(typeof(DatabaseContext).Assembly.FullName));
                options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
            });

            services.AddDbContext<PmsDbContext>(options =>
                options.UseSqlServer(connectionString));
        }
    }

    private static void AddIdentity(this IServiceCollection services)
    {
        services.AddDefaultIdentity<ApplicationUser>(options => options.SignIn.RequireConfirmedAccount = false)
            .AddEntityFrameworkStores<DatabaseContext>();

        services.Configure<IdentityOptions>(options =>
        {
            // Same rule as UserValidatorConfiguration: 6+ characters, one upper case letter, one special character
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireNonAlphanumeric = true;
            options.Password.RequireUppercase = true;
            options.Password.RequiredLength = 6;
            options.Password.RequiredUniqueChars = 1;

            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.AllowedForNewUsers = true;

            options.User.AllowedUserNameCharacters =
                "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";
            options.User.RequireUniqueEmail = true;
        });
    }
}

public class DatabaseConfiguration
{
    public bool UseInMemoryDatabase { get; set; }

    /// <summary>
    /// When true (default), appends the developer's sanitized Windows/OS username
    /// (e.g. parking_management_system_{UserName}) so each developer automatically
    /// works on their own isolated database without colliding or editing config.
    /// </summary>
    public bool UsePerUserDatabase { get; set; } = true;

    /// <summary>
    /// When true (default), tests connectivity and automatically discovers local SQL Server
    /// instances ((localdb)\mssqllocaldb, ., localhost, .\SQLEXPRESS, (local)) if the configured
    /// server is not reachable.
    /// </summary>
    public bool AutoDetectLocalServer { get; set; } = true;

    public string ConnectionString { get; set; }

    /// <summary>
    /// Returns the resolved connection string (with per-user DB and auto-server detection).
    /// </summary>
    public string GetResolvedConnectionString() => DatabaseConnectionStringResolver.Resolve(this);
}
