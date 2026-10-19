using System;
using Microsoft.Extensions.DependencyInjection;
using PMS.Application.Jobs;
using PMS.Application.Services;

namespace PMS.Api.IntegrationTests.Helpers.Services;

/// <summary>
/// Runs the vehicle paper OCR job right away in its own scope (as Hangfire would, minus the delay),
/// so a test reads the final status straight after submitting.
/// </summary>
public class InlineVehicleVerificationQueue(IServiceScopeFactory scopes) : IVehicleVerificationQueue
{
    public void Enqueue(Guid verificationId)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<VehicleDocumentJobs>().ProcessAsync(verificationId).GetAwaiter().GetResult();
    }
}
