using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PMS.Application.Jobs;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.Application.Services.Ocr;

namespace PMS.Application;

public static class VehicleDocumentsDependencyInjection
{
    /// <summary>
    /// Vehicle paper upload, OCR and verification. "Ocr:Engine" picks the engine: Tesseract (default)
    /// or Fake (deterministic, for tests and demos). Files go to private storage under the content root.
    /// </summary>
    public static IServiceCollection AddVehicleDocuments(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment env)
    {
        var ocrOptions = configuration.GetSection(OcrOptions.Section).Get<OcrOptions>() ?? new OcrOptions();
        var storageOptions = configuration.GetSection(VehicleDocumentStorageOptions.Section).Get<VehicleDocumentStorageOptions>()
                             ?? new VehicleDocumentStorageOptions();

        services.AddSingleton(ocrOptions);
        services.AddSingleton(storageOptions);
        services.AddSingleton<IVehicleDocumentStorage>(_ => new LocalVehicleDocumentStorage(storageOptions, env.ContentRootPath));

        if (string.Equals(ocrOptions.Engine, "Fake", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IDocumentOcrService, FakeDocumentOcrService>();
        else
            services.AddSingleton<IDocumentOcrService>(sp => new TesseractDocumentOcrService(
                ocrOptions, env.ContentRootPath, sp.GetRequiredService<ILogger<TesseractDocumentOcrService>>()));

        services.AddScoped<IVehicleVerificationQueue, HangfireVehicleVerificationQueue>();
        services.AddScoped<IVehicleDocumentService, VehicleDocumentService>();
        services.AddScoped<VehicleDocumentJobs>();

        return services;
    }
}
