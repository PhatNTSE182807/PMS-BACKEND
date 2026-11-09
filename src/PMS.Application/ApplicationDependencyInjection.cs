using Mapster;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PMS.Application.Common.Email;
using PMS.Application.Jobs;
using PMS.Application.MappingProfiles;
using PMS.Application.Services;
using PMS.Application.Services.DevImpl;
using PMS.Application.Services.Impl;
using PMS.Shared.Services;
using PMS.Shared.Services.Impl;

namespace PMS.Application;

public static class ApplicationDependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IWebHostEnvironment env)
    {
        services.AddServices(env);

        services.RegisterMapper();

        return services;
    }

    private static void AddServices(this IServiceCollection services, IWebHostEnvironment env)
    {
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IUserProfileService, UserProfileService>();
        services.AddScoped<IPasswordResetService, PasswordResetService>();
        services.AddScoped<IEmployeeAccountService, EmployeeAccountService>();
        services.AddScoped<ILayoutService, LayoutService>();
        services.AddScoped<IParkingLotService, ParkingLotService>();
        services.AddScoped<IZoneService, ZoneService>();
        services.AddScoped<ISlotService, SlotService>();
        services.AddScoped<IClaimService, ClaimService>();
        services.AddScoped<ITemplateService, TemplateService>();
        services.AddScoped<IGoogleTokenValidator, GoogleTokenValidator>();
        services.AddScoped<LotJobs>();

        // Brevo's HTTPS API when a key is set (the host may block SMTP ports), otherwise SMTP.
        // Development only logs emails until real credentials are set (e.g. in user-secrets).
        services.AddHttpClient(BrevoEmailService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddScoped<IEmailService>(sp =>
        {
            var smtpSettings = sp.GetRequiredService<SmtpSettings>();
            if (smtpSettings.UsesBrevo)
                return ActivatorUtilities.CreateInstance<BrevoEmailService>(sp);
            return env.IsDevelopment() && !smtpSettings.IsConfigured
                ? ActivatorUtilities.CreateInstance<DevEmailService>(sp)
                : new EmailService(smtpSettings);
        });
    }

    private static void RegisterMapper(this IServiceCollection services)
    {
        services.AddMapster();
    }

    public static void AddEmailConfiguration(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(configuration.GetSection("SmtpSettings").Get<SmtpSettings>());
    }
}
