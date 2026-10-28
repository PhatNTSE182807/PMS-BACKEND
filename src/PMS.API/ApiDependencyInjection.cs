using System.Text;
using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using PMS.API.Filters;
using PMS.API.Hubs;
using PMS.Application.Jobs;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.DataAccess;

namespace PMS.API;

public static class ApiDependencyInjection
{
    /// <summary>Rate limit for the public 3D layout channel (SRS FR-3D-08).</summary>
    public const string PublicLayoutRateLimit = "public-layout";

    /// <summary>Rate limit for the forgot / reset password endpoints, which send emails and check tokens.</summary>
    public const string PasswordResetRateLimit = "password-reset";

    /// <summary>
    /// Public layout: 120 requests a minute per client IP, enough to poll statuses every second.
    /// Password reset: 5 requests per 15 minutes per client IP, so nobody can flood a mailbox.
    /// </summary>
    public static void AddPublicRateLimits(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(PasswordResetRateLimit, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(15),
                        QueueLimit = 0
                    }));
            options.AddPolicy(PublicLayoutRateLimit, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 120,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
        });
    }

    /// <summary>
    /// SignalR pushes lot changes to the 3D views; Hangfire runs timed work (SQL Server storage, or
    /// in memory when the database is). Dashboard: /hangfire, local requests only.
    /// </summary>
    public static void AddRealtimeAndJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSignalR();
        services.AddScoped<ILotRealtimeNotifier, SignalRLotRealtimeNotifier>();
        services.AddScoped<IVehicleVerificationNotifier, SignalRVehicleVerificationNotifier>();
        services.AddScoped<IParkingRealtimeNotifier, SignalRParkingRealtimeNotifier>();

        // Customer notification bell
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IUserNotificationPublisher, HangfireUserNotificationPublisher>();
        services.AddScoped<IUserNotificationNotifier, SignalRUserNotificationNotifier>();

        var database = configuration.GetSection("Database").Get<DatabaseConfiguration>() ?? new DatabaseConfiguration();
        var hangfireConn = database.GetResolvedConnectionString();
        services.AddHangfire(config =>
        {
            config.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings();
            if (database.UseInMemoryDatabase)
                config.UseInMemoryStorage();
            else
                config.UseSqlServerStorage(hangfireConn);
        });
        services.AddHangfireServer();
    }

    public static void UseRealtimeAndJobs(this WebApplication app)
    {
        app.MapHub<LotHub>(LotHub.Path);
        app.MapHub<VehicleVerificationHub>(VehicleVerificationHub.Path);
        app.MapHub<ParkingOpsHub>(ParkingOpsHub.Path);
        app.UseHangfireDashboard("/hangfire");

        var jobs = app.Services.GetRequiredService<IRecurringJobManager>();
        jobs.AddOrUpdate<LotJobs>(LotJobs.ReopenDueLotsJobId, j => j.ReopenDueLotsAsync(), Cron.Minutely());
        jobs.AddOrUpdate<LotJobs>(LotJobs.CleanupCredentialsJobId, j => j.CleanupExpiredCredentialsAsync(), Cron.Daily(3));
        // BR-26: vehicle paper photos are deleted 30 days after the final decision
        jobs.AddOrUpdate<VehicleDocumentJobs>(VehicleDocumentJobs.PurgeDocumentsJobId, j => j.PurgeExpiredDocumentsAsync(), Cron.Daily(2));
        // Deposit deadlines, no-shows, reminders, turn-arounds, pending payments and refund retries
        jobs.AddOrUpdate<ParkingJobs>(ParkingJobs.MinutelyJobId, j => j.RunMinutelyAsync(), Cron.Minutely());
        // BR-20: overdue and abandoned vehicles
        jobs.AddOrUpdate<ParkingJobs>(ParkingJobs.OverdueJobId, j => j.FlagOverdueSessionsAsync(), "*/15 * * * *");
        // Customer notifications: daily purge of old read/unread items
        jobs.AddOrUpdate<NotificationJobs>(NotificationJobs.PurgeJobId, j => j.PurgeOldNotificationsAsync(), Cron.Daily(4));
    }

    public static void AddJwt(this IServiceCollection services, IConfiguration configuration)
    {
        var secretKey = configuration.GetValue<string>("JwtConfiguration:SecretKey");

        var key = Encoding.ASCII.GetBytes(secretKey);

        services.AddAuthentication(x =>
            {
                x.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                x.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(x =>
            {
                x.RequireHttpsMetadata = false;
                x.SaveToken = true;
                x.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ValidateIssuer = false,
                    ValidateAudience = false
                };
                x.Events = new JwtBearerEvents
                {
                    // Browsers cannot set headers on WebSockets: the private hubs take the JWT from ?access_token=
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken) && (context.HttpContext.Request.Path.StartsWithSegments(VehicleVerificationHub.Path)
                                || context.HttpContext.Request.Path.StartsWithSegments(ParkingOpsHub.Path)))
                            context.Token = accessToken;
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        if (context.Principal?.HasClaim(c => c.Type == "purpose" && c.Value == "2FA_Challenge") == true)
                        {
                            context.Fail("Two-factor challenge token cannot be used as an API access token.");
                        }
                        return Task.CompletedTask;
                    }
                };
            });
    }

    public static void AddSwagger(this IServiceCollection services)
    {
        services.AddSwaggerGen(s =>
        {
            s.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "XXIV Parking",
                Version = "v1",
                Description = "API for XXIV Parking"
            });

            s.DocumentFilter<SwaggerTagsDocumentFilter>();

            s.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "JWT Authorization header using the Bearer scheme (Example: 'Bearer YOUR_TOKEN')",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.ApiKey,
                Scheme = "Bearer"
            });

            s.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    },
                    Array.Empty<string>()
                }
            });
        });
    }
}
