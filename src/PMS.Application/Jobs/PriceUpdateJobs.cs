using PMS.Application.Services.Impl;

namespace PMS.Application.Jobs;

/// <summary>
/// Safety net for deferred price updates, run by Hangfire (registered in PMS.API). The price normally goes live
/// in the exit that empties the lot; this job covers the exits that could not finish that step.
/// </summary>
public class PriceUpdateJobs(LotOccupancy occupancy)
{
    public const string IdleLotsJobId = "price-update-idle-lots";

    /// <summary>Gives every lot that has a price waiting and no vehicle inside its new price. Returns the number of lots changed.</summary>
    public Task<int> PromoteIdleLotsAsync() => occupancy.SettleIdleLotsAsync();
}
