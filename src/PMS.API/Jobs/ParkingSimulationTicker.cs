using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PMS.Application.Jobs;
using PMS.Application.Models.Operations;

namespace PMS.API.Jobs;

/// <summary>
/// While the Admin's parking simulation is on, checks booking deadlines every second (Hangfire's recurring
/// jobs run at most once a minute, too slow for holds of a few seconds). Idle when the simulation is off.
/// </summary>
public class ParkingSimulationTicker(IServiceScopeFactory scopes, ParkingOptions options, ILogger<ParkingSimulationTicker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!options.IsSimulating)
                continue;
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ParkingJobs>().RunSimulationTickAsync();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Parking simulation tick failed");
            }
        }
    }
}
