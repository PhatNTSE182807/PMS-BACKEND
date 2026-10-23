using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PMS.Application.Helpers.Parking;
using PMS.Application.Jobs;
using PMS.Application.Models.Operations;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.Application.Services.Payments;

namespace PMS.Application;

/// <summary>Reservations, gate sessions and VNPay / MoMo payments.</summary>
public static class OperationsDependencyInjection
{
    public static IServiceCollection AddParkingOperations(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(configuration.GetSection(ParkingOptions.Section).Get<ParkingOptions>() ?? new ParkingOptions());
        services.AddSingleton(configuration.GetSection(PaymentGatewayOptions.Section).Get<PaymentGatewayOptions>() ?? new PaymentGatewayOptions());
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new ParkingTokenSigner(sp.GetRequiredService<ParkingOptions>(), sp.GetService<ILogger<ParkingTokenSigner>>()));

        services.AddHttpClient(VnPayGateway.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddHttpClient(MoMoGateway.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<IPaymentGateway, VnPayGateway>();
        services.AddScoped<IPaymentGateway, MoMoGateway>();

        services.AddScoped<ParkingInventory>();
        services.AddScoped<LotScopeService>();
        services.AddScoped<TariffProvider>();
        services.AddScoped<PlateListService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<ReservationService>();
        services.AddScoped<IReservationService>(sp => sp.GetRequiredService<ReservationService>());
        services.AddScoped<GateService>();
        services.AddScoped<IGateService>(sp => sp.GetRequiredService<GateService>());
        services.AddScoped<IParkingHistoryService, ParkingHistoryService>();
        services.AddScoped<ParkingJobs>();

        return services;
    }
}
