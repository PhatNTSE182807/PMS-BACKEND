using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Services;
using PMS.DataAccess.Persistence;

namespace PMS.Application.Jobs;

/// <summary>Background jobs for lots and sign-in data, run by Hangfire (see PMS.API).</summary>
public class LotJobs(PmsDbContext pmsContext, ILotRealtimeNotifier notifier = null)
{
    public const string ReopenDueLotsJobId = "reopen-due-lots";
    public const string CleanupCredentialsJobId = "cleanup-expired-credentials";

    /// <summary>Expired login codes and refresh tokens are kept this long for troubleshooting.</summary>
    public static readonly TimeSpan CredentialRetention = TimeSpan.FromDays(7);

    /// <summary>Temporarily closed lots reopen on their own once the closing period ends (FR-LOT-06).</summary>
    public async Task<int> ReopenDueLotsAsync()
    {
        var now = DateTime.UtcNow;
        var lots = await pmsContext.ParkingLots
            .Where(p => !p.IsDeleted && p.Status == "TemporarilyClosed" && p.ClosedUntil != null && p.ClosedUntil <= now)
            .ToListAsync();

        foreach (var lot in lots)
        {
            lot.Status = "Active";
            lot.ClosedFrom = null;
            lot.ClosedUntil = null;
            lot.ClosureReason = null;
            lot.UpdatedAt = now;
        }

        await pmsContext.SaveChangesAsync();

        if (notifier != null)
            foreach (var lot in lots)
                await notifier.LotStatusChangedAsync(lot.Id, lot.Status);

        return lots.Count;
    }

    /// <summary>Deletes login codes and refresh tokens that expired more than <see cref="CredentialRetention"/> ago.</summary>
    public async Task<int> CleanupExpiredCredentialsAsync()
    {
        var cutoff = DateTime.UtcNow - CredentialRetention;
        var otps = await pmsContext.OtpCodes.Where(o => o.ExpiresAt < cutoff).ToListAsync();
        var tokens = await pmsContext.RefreshTokens.Where(t => t.ExpiresAt < cutoff).ToListAsync();

        pmsContext.OtpCodes.RemoveRange(otps);
        pmsContext.RefreshTokens.RemoveRange(tokens);
        await pmsContext.SaveChangesAsync();

        return otps.Count + tokens.Count;
    }
}
