using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PMS.Application.Jobs;
using PMS.Application.Services;
using PMS.Application.Services.Impl;

namespace PMS.Application;

public static class VehicleDocumentsDependencyInjection
{
    /// <summary>
    /// Vehicle paper upload and the Staff review of new vehicles. Files go to private storage under
    /// the content root; Staff compare them by eye (no OCR).
    /// </summary>
    public static IServiceCollection AddVehicleDocuments(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment env)
    {
        var storageOptions = configuration.GetSection(VehicleDocumentStorageOptions.Section).Get<VehicleDocumentStorageOptions>()
                             ?? new VehicleDocumentStorageOptions();

        services.AddSingleton(storageOptions);
        services.AddSingleton<IVehicleDocumentStorage>(_ => new LocalVehicleDocumentStorage(storageOptions, env.ContentRootPath));

        services.AddScoped<IVehicleDocumentService, VehicleDocumentService>();
        services.AddScoped<VehicleDocumentJobs>();

        return services;
    }
}
