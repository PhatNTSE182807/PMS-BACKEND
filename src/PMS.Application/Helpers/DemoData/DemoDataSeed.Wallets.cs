using Microsoft.EntityFrameworkCore;
using PMS.Application.Jobs;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Helpers.DemoData;

/// <summary>Wallet ledgers built from every wallet movement of the story, and the customers' notifications.</summary>
public sealed partial class DemoDataSeed
{
    /// <summary>Top-ups each customer made; more are added before any payment the balance could not cover.</summary>
    private static readonly (string Customer, int DaysAgo, decimal Amount, string Provider)[] TopUps =
    [
        ("an", 29, 800_000, PaymentProviders.VnPay),
        ("binh", 26, 500_000, PaymentProviders.MoMo),
        // Exactly 100,000 left: enough for a deposit, not for much more
        ("dung", 2, 100_000, PaymentProviders.MoMo),
        // Only 50,000: too little for a lost-ticket fine, so the wallet refuses it
        ("em", 21, 50_000, PaymentProviders.VnPay),
        ("giang", 16, 300_000, PaymentProviders.VnPay),
        ("hai", 28, 500_000, PaymentProviders.MoMo),
        ("hai", 12, 500_000, PaymentProviders.VnPay)
    ];

    private const decimal MinimumWalletBalance = 200_000;

    private int topUpLotId;

    private async Task SeedWalletsAsync()
    {
        // PaymentService files top-ups under the first lot of the table
        topUpLotId = await db.ParkingLots.OrderBy(l => l.Id).Select(l => l.Id).FirstAsync();
        foreach (var (key, daysAgo, amount, provider) in TopUps)
            AddTopUp(customers[key], HoursAgo(daysAgo * 24 + random.Next(1, 10)), amount, provider);

        // A wallet never goes below zero: top up before a payment it could not cover
        var ledgers = walletOps.GroupBy(o => o.Customer).Select(g => (Customer: g.Key, Ops: g.OrderBy(o => o.At).ToList())).ToList();
        foreach (var (customer, ops) in ledgers)
        {
            decimal balance = 0;
            foreach (var op in ops)
            {
                if (balance + op.Amount < 0)
                {
                    var needed = Math.Max(500_000m, Math.Ceiling(-(balance + op.Amount) / 100_000m) * 100_000m);
                    AddTopUp(customer, op.At.AddHours(-2), needed, PaymentProviders.VnPay);
                    balance += needed;
                }

                balance += op.Amount;
            }

            // Customers who pay from the wallet keep enough to try it now (the story's spending varies by date)
            if (customer.PaysWithWallet && balance < MinimumWalletBalance)
                AddTopUp(customer, HoursAgo(20), Math.Ceiling((2 * MinimumWalletBalance - balance) / 100_000m) * 100_000m, PaymentProviders.MoMo);
        }

        await db.SaveChangesAsync();

        foreach (var group in walletOps.GroupBy(o => o.Customer).ToList())
        {
            var customer = group.Key;
            var wallet = await db.Wallets.FirstOrDefaultAsync(w => w.CustomerId == customer.Customer.Id);
            if (wallet == null)
            {
                wallet = new Wallet { CustomerId = customer.Customer.Id, Balance = 0, Currency = "VND", Status = "Active", UpdatedAt = now, RowVersion = [] };
                db.Wallets.Add(wallet);
                await db.SaveChangesAsync();
            }

            var balance = wallet.Balance;
            // Money in before money out at the same moment
            foreach (var op in group.OrderBy(o => o.At).ThenByDescending(o => o.Amount))
            {
                var before = balance;
                balance += op.Amount;
                db.WalletTransactions.Add(new WalletTransaction
                {
                    WalletId = wallet.Id,
                    TxnType = op.TxnType,
                    Amount = op.Amount,
                    BalanceBefore = before,
                    BalanceAfter = balance,
                    PaymentId = op.Payment?.Id,
                    ReferenceType = op.ReferenceType,
                    ReferenceId = op.ReferenceId,
                    Description = op.Description,
                    CreatedAt = op.At,
                    CreatedBy = op.CreatedBy
                });
            }

            wallet.Balance = balance;
            wallet.UpdatedAt = group.Max(o => o.At);
        }

        await db.SaveChangesAsync();
    }

    /// <summary>A wallet top-up paid through VNPay or MoMo, as PaymentService.ApplyWalletTopUpAsync records it.</summary>
    private void AddTopUp(CustomerInfo customer, DateTime paidAt, decimal amount, string provider)
    {
        var payment = NewPayment(topUpLotId, customer, customer.Customer.Id, amount, paidAt,
            PaymentMethod.Online, provider, PaymentPurpose.WalletTopUp, PaymentChannel.App, null);
        payment.Invoice.DueDate = payment.ExpiresAt;
        walletOps.Add(new WalletOp(customer, paidAt, amount, "Deposit", payment, "Payment", payment.PaymentCode,
            $"Wallet Top-up ({provider})", customer.UserId));
    }

    /// <summary>
    /// The notifications the bell would have received, written by the real notification job with its clock
    /// set to each event: paper decisions, the vehicles inside now, and the last three days of visits.
    /// Anything older than half a day is marked read.
    /// </summary>
    private async Task SeedNotificationsAsync()
    {
        var clock = new SettableClock();
        var jobs = new NotificationJobs(db, clock);

        foreach (var verification in decidedVerifications.OrderBy(v => v.DecidedAt))
        {
            clock.UtcNow = verification.DecidedAt!.Value;
            await jobs.VehicleReviewedAsync(verification.Id);
        }

        var recent = customerSessions
            .Where(s => SessionStatus.Inside.Contains(s.Status) || (s.Status == SessionStatus.Completed && s.ExitTime >= now.AddDays(-3)))
            .OrderBy(s => s.EntryTime);
        foreach (var session in recent)
        {
            clock.UtcNow = session.EntryConfirmedAt ?? session.EntryTime;
            await jobs.SessionCheckedInAsync(session.Id);
            if (session.Status == SessionStatus.Completed)
            {
                clock.UtcNow = session.ExitTime!.Value;
                await jobs.SessionCheckedOutAsync(session.Id);
            }
        }

        var userIds = customers.Values.Select(c => c.UserId).ToList();
        var readBefore = now.AddHours(-12);
        foreach (var notification in await db.UserNotifications.Where(n => userIds.Contains(n.UserId) && n.ReadAt == null && n.CreatedAt < readBefore).ToListAsync())
            notification.ReadAt = notification.CreatedAt.AddMinutes(random.Next(5, 180));
        await db.SaveChangesAsync();
    }
}
