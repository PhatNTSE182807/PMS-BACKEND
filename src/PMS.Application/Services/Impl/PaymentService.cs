using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.Application.Services.Payments;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using PMS.Shared.Services;

namespace PMS.Application.Services.Impl;

/// <inheritdoc />
public class PaymentService(
    PmsDbContext pmsContext,
    IEnumerable<IPaymentGateway> gateways,
    PaymentGatewayOptions gatewayOptions,
    ParkingOptions parkingOptions,
    ParkingTokenSigner signer,
    LotScopeService lotScope,
    IClaimService claimService,
    TimeProvider clock,
    ParkingInventory inventory,
    IParkingRealtimeNotifier notifier = null,
    ILogger<PaymentService> logger = null) : IPaymentService
{
    /// <summary>A callback and its IPN twin (or two IPN retries) never apply the same payment at once.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PaymentGates = new();

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<PaymentResponseModel> CreateDepositPaymentAsync(long reservationId, string userId, CreatePaymentModel model, PaymentRequestContext context)
    {
        var reservation = await pmsContext.Reservations.FirstOrDefaultAsync(r => r.Id == reservationId && r.UserId == userId)
                          ?? throw new NotFoundException("Reservation not found.");

        if (reservation.Status != ReservationStatus.PendingDeposit || reservation.DepositDueAt <= Now)
            throw new BadRequestException("This reservation is not waiting for a deposit.");

        var isWallet = string.Equals(model?.Provider?.Trim(), PaymentProviders.Wallet, StringComparison.OrdinalIgnoreCase);

        if (isWallet)
        {
            var customer = await GetOrCreateCustomerAsync(userId);
            var wallet = await GetOrCreateWalletAsync(customer.Id);

            if (wallet.Balance < reservation.DepositAmount)
            {
                throw new BadRequestException(
                    $"Insufficient XXIV Wallet balance ({wallet.Balance:N0} VND). Deposit required: {reservation.DepositAmount:N0} VND. Please top up your XXIV Wallet.");
            }

            var before = wallet.Balance;
            wallet.Balance -= reservation.DepositAmount;
            wallet.UpdatedAt = Now;

            var walletInvoice = NewInvoice(reservation.ParkingLotId, reservation.CustomerId, reservation.DepositAmount, Now);
            walletInvoice.ReservationId = reservation.Id;
            walletInvoice.Status = "Paid";
            walletInvoice.PaidAt = Now;

            var payment = NewPayment(walletInvoice, PaymentMethod.Wallet, PaymentProviders.Wallet, reservation.DepositAmount, PaymentPurpose.Deposit, PaymentChannel.App);
            payment.ReservationId = reservation.Id;
            payment.ParkingLotId = reservation.ParkingLotId;
            payment.CustomerUserId = userId;
            payment.Status = PaymentStatus.Succeeded;
            payment.PaidAt = Now;
            payment.PayUrl = null;

            pmsContext.Invoices.Add(walletInvoice);
            pmsContext.Payments.Add(payment);
            await pmsContext.SaveChangesAsync();

            pmsContext.WalletTransactions.Add(new WalletTransaction
            {
                WalletId = wallet.Id,
                TxnType = "Payment",
                Amount = -reservation.DepositAmount,
                BalanceBefore = before,
                BalanceAfter = wallet.Balance,
                PaymentId = payment.Id,
                ReferenceType = "Reservation",
                ReferenceId = reservation.ReservationCode,
                Description = $"Deposit for reservation {reservation.ReservationCode}",
                CreatedAt = Now,
                CreatedBy = userId
            });
            await pmsContext.SaveChangesAsync();

            await ApplyDepositAsync(payment);
            await pmsContext.SaveChangesAsync();
            QueuePaymentPush(payment);
            await inventory.FlushAsync();
            return ToModel(payment);
        }

        var gateway = Gateway(model?.Provider);

        // One open link per provider: paying twice for the same deposit is what FR-PAY-09 refunds
        var open = await pmsContext.Payments
            .Where(p => p.ReservationId == reservationId && p.Purpose == PaymentPurpose.Deposit && p.Status == PaymentStatus.Pending
                        && p.GatewayProvider == gateway.Provider && p.ExpiresAt > Now && p.PayUrl != null)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync();
        if (open != null)
            return ToModel(open);

        var expiresAt = Min(Now.AddMinutes(parkingOptions.Gate.PaymentLinkMinutes), reservation.DepositDueAt ?? Now.AddMinutes(10));
        var depositInvoice = NewInvoice(reservation.ParkingLotId, reservation.CustomerId, reservation.DepositAmount, expiresAt);
        depositInvoice.ReservationId = reservation.Id;
        var paymentOnline = NewPayment(depositInvoice, PaymentMethod.Online, gateway.Provider, reservation.DepositAmount, PaymentPurpose.Deposit, PaymentChannel.App);
        paymentOnline.ReservationId = reservation.Id;
        paymentOnline.ParkingLotId = reservation.ParkingLotId;
        paymentOnline.CustomerUserId = userId;
        paymentOnline.ExpiresAt = expiresAt;

        await StartCheckoutAsync(gateway, paymentOnline, $"Coc dat cho {reservation.ReservationCode}", model.ReturnUrl, context);
        return ToModel(paymentOnline);
    }

    public async Task<PaymentResponseModel> CreateSessionPaymentAsync(ParkingSession session, decimal amountDue, string provider, string channel,
        string returnUrl, string customerUserId, string staffUserId, PaymentRequestContext context)
    {
        if (amountDue <= 0)
            throw new BadRequestException("Nothing is due for this session.");

        var isWallet = string.Equals(provider?.Trim(), PaymentProviders.Wallet, StringComparison.OrdinalIgnoreCase);

        if (isWallet)
        {
            if (string.IsNullOrWhiteSpace(customerUserId))
                throw new BadRequestException("This parking session is not linked to a registered customer wallet.");

            var customer = await GetOrCreateCustomerAsync(customerUserId);
            var wallet = await GetOrCreateWalletAsync(customer.Id);

            if (wallet.Balance < amountDue)
            {
                throw new BadRequestException(
                    $"Insufficient XXIV Wallet balance ({wallet.Balance:N0} VND). Parking fee required: {amountDue:N0} VND. Please top up your XXIV Wallet.");
            }

            var before = wallet.Balance;
            wallet.Balance -= amountDue;
            wallet.UpdatedAt = Now;

            var walletSessionInvoice = NewInvoice(session.ParkingLotId, session.CustomerId, amountDue, Now);
            walletSessionInvoice.SessionId = session.Id;
            walletSessionInvoice.Status = "Paid";
            walletSessionInvoice.PaidAt = Now;

            var payment = NewPayment(walletSessionInvoice, PaymentMethod.Wallet, PaymentProviders.Wallet, amountDue, PaymentPurpose.ParkingFee, channel);
            payment.SessionId = session.Id;
            payment.ParkingLotId = session.ParkingLotId;
            payment.CustomerUserId = customerUserId;
            payment.CollectedByUserId = staffUserId;
            payment.Status = PaymentStatus.Succeeded;
            payment.PaidAt = Now;
            payment.PayUrl = null;

            pmsContext.Invoices.Add(walletSessionInvoice);
            pmsContext.Payments.Add(payment);
            await pmsContext.SaveChangesAsync();

            pmsContext.WalletTransactions.Add(new WalletTransaction
            {
                WalletId = wallet.Id,
                TxnType = "Payment",
                Amount = -amountDue,
                BalanceBefore = before,
                BalanceAfter = wallet.Balance,
                PaymentId = payment.Id,
                ReferenceType = "ParkingSession",
                ReferenceId = session.SessionCode,
                Description = $"Parking fee for session {session.SessionCode} ({session.EntryPlateNumber})",
                CreatedAt = Now,
                CreatedBy = customerUserId
            });
            await pmsContext.SaveChangesAsync();

            await ApplyFeeAsync(payment);
            await pmsContext.SaveChangesAsync();
            QueuePaymentPush(payment);
            await inventory.FlushAsync();
            return ToModel(payment, session);
        }

        var gateway = Gateway(provider);

        var open = await pmsContext.Payments
            .Where(p => p.SessionId == session.Id && p.Purpose == PaymentPurpose.ParkingFee && p.Status == PaymentStatus.Pending
                        && p.GatewayProvider == gateway.Provider && p.Amount == amountDue && p.ExpiresAt > Now && p.PayUrl != null)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync();
        if (open != null)
            return ToModel(open);

        var expiresAt = Now.AddMinutes(parkingOptions.Gate.PaymentLinkMinutes);
        var invoiceOnline = NewInvoice(session.ParkingLotId, session.CustomerId, amountDue, expiresAt);
        invoiceOnline.SessionId = session.Id;
        var paymentOnline = NewPayment(invoiceOnline, PaymentMethod.Online, gateway.Provider, amountDue, PaymentPurpose.ParkingFee, channel);
        paymentOnline.SessionId = session.Id;
        paymentOnline.ParkingLotId = session.ParkingLotId;
        paymentOnline.CustomerUserId = customerUserId;
        paymentOnline.CollectedByUserId = staffUserId;
        paymentOnline.ExpiresAt = expiresAt;

        await StartCheckoutAsync(gateway, paymentOnline, $"Phi gui xe {session.SessionCode}", returnUrl, context);
        return ToModel(paymentOnline);
    }

    public async Task<PaymentResponseModel> RecordCashAsync(ParkingSession session, decimal amountDue, decimal received, bool prepaid, string staffUserId)
    {
        if (session.Status != SessionStatus.PendingPayment)
            throw new BadRequestException("The session is not waiting for payment.");
        if (amountDue <= 0)
            throw new BadRequestException("Nothing is due for this session.");
        if (received < amountDue)
            throw new BadRequestException($"Received {received:N0} VND, less than the {amountDue:N0} VND due.");

        var invoice = NewInvoice(session.ParkingLotId, session.CustomerId, amountDue, Now);
        invoice.SessionId = session.Id;
        var payment = NewPayment(invoice, PaymentMethod.Cash, null, amountDue, PaymentPurpose.ParkingFee, prepaid ? PaymentChannel.Kiosk : PaymentChannel.Lane);
        payment.SessionId = session.Id;
        payment.ParkingLotId = session.ParkingLotId;
        payment.CollectedByUserId = staffUserId;
        payment.Status = PaymentStatus.Succeeded;
        payment.PaidAt = Now;
        invoice.Status = "Paid";
        invoice.PaidAt = Now;

        pmsContext.Invoices.Add(invoice);
        pmsContext.Payments.Add(payment);
        SettleSession(session, payment);
        await pmsContext.SaveChangesAsync();

        QueuePaymentPush(payment);
        inventory.Queue(() => notifier?.SessionChangedAsync(session.ParkingLotId, session.Id, session.Status, session.EntryPlateNumber) ?? Task.CompletedTask);
        await inventory.FlushAsync();

        return ToModel(payment, session);
    }

    public async Task<PaymentCallbackResultModel> HandleCallbackAsync(string provider, IReadOnlyDictionary<string, string> fields)
    {
        var gateway = Gateway(provider);
        var callback = gateway.ParseCallback(fields);
        if (!callback.SignatureValid)
        {
            logger?.LogWarning("{Provider} callback with an invalid signature for {Code}.", gateway.Provider, callback.PaymentCode);
            return new PaymentCallbackResultModel { SignatureValid = false, Outcome = "InvalidSignature", Message = "Invalid signature." };
        }

        return await ApplyAsync(callback.PaymentCode, callback.Success, callback.Amount, callback.TransactionNo,
            callback.Message, JsonSerializer.Serialize(fields));
    }

    public async Task<PaymentCallbackResultModel> CheckStatusAsync(long paymentId, string clientIp)
    {
        var payment = await pmsContext.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId)
                      ?? throw new NotFoundException("Payment not found.");
        await EnsureCanSeeAsync(payment);

        if (payment.Status != PaymentStatus.Pending || payment.GatewayProvider == null)
            return new PaymentCallbackResultModel { SignatureValid = true, Outcome = "AlreadyProcessed", Payment = ToModel(payment) };

        return await QueryAndApplyAsync(payment, clientIp);
    }

    public async Task<PaymentResponseModel> GetAsync(long paymentId)
    {
        var payment = await pmsContext.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId)
                      ?? throw new NotFoundException("Payment not found.");
        await EnsureCanSeeAsync(payment);
        var session = payment.SessionId == null ? null : await pmsContext.ParkingSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == payment.SessionId);
        return ToModel(payment, session);
    }

    public async Task<PaymentResponseModel> GetPendingForReservationAsync(long reservationId)
    {
        var payment = await pmsContext.Payments.AsNoTracking()
            .Where(p => p.ReservationId == reservationId && p.Purpose == PaymentPurpose.Deposit && p.Status == PaymentStatus.Pending && p.ExpiresAt > Now)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync();
        return payment == null ? null : ToModel(payment);
    }

    public async Task<PaymentResponseModel> GetPendingForSessionAsync(long sessionId)
    {
        var payment = await pmsContext.Payments.AsNoTracking()
            .Where(p => p.SessionId == sessionId && p.Purpose == PaymentPurpose.ParkingFee && p.Status == PaymentStatus.Pending
                        && p.ExpiresAt > Now && p.PayUrl != null)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync();
        return payment == null ? null : ToModel(payment);
    }

    public async Task<decimal> DepositHeldAsync(long reservationId)
    {
        var paid = await pmsContext.Payments
            .Where(p => p.ReservationId == reservationId && p.Purpose == PaymentPurpose.Deposit && p.Status == PaymentStatus.Succeeded)
            .Select(p => new { p.Amount, p.RefundedAmount })
            .ToListAsync();
        return paid.Sum(p => p.Amount - p.RefundedAmount);
    }

    public async Task<decimal> RefundDepositAsync(Reservation reservation, decimal amount, string reason, string requestedBy)
    {
        if (amount <= 0) return 0;

        var deposits = await pmsContext.Payments
            .Where(p => p.ReservationId == reservation.Id && p.Purpose == PaymentPurpose.Deposit && p.Status == PaymentStatus.Succeeded)
            .OrderBy(p => p.PaidAt)
            .ToListAsync();

        var left = amount;
        foreach (var deposit in deposits)
        {
            var refundable = deposit.Amount - deposit.RefundedAmount;
            if (refundable <= 0 || left <= 0) continue;
            var part = Math.Min(refundable, left);
            await RefundAsync(deposit, part, reason, requestedBy);
            left -= part;
        }

        var refunded = amount - left;
        reservation.RefundAmount += refunded;
        return refunded;
    }

    public async Task<int> CheckPendingPaymentsAsync()
    {
        var waitedSince = Now.AddSeconds(-parkingOptions.Gate.PendingPaymentCallbackSeconds);
        var askedSince = Now.AddMinutes(-1);
        var pending = await pmsContext.Payments.AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Pending && p.GatewayProvider != null && p.CreatedAt <= waitedSince
                        && (p.LastCheckedAt == null || p.LastCheckedAt <= askedSince))
            .OrderBy(p => p.CreatedAt)
            .Take(50)
            .ToListAsync();

        var changed = 0;
        foreach (var payment in pending)
        {
            var gateway = gateways.FirstOrDefault(g => g.Provider == payment.GatewayProvider);
            if (gateway is not { IsConfigured: true })
            {
                // Nothing to ask: stale links simply expire
                if (payment.ExpiresAt <= Now && await ExpireAsync(payment.Id)) changed++;
                continue;
            }

            var result = await QueryAndApplyAsync(payment, null);
            if (result.Outcome is "Applied" or "Failed" or "Refunded" or "Expired") changed++;
        }

        return changed;
    }

    public async Task<int> RetryPendingRefundsAsync()
    {
        var refunds = await pmsContext.Payments
            .Where(p => p.Purpose == PaymentPurpose.Refund && p.Status == PaymentStatus.RefundPending && p.GatewayProvider != null
                        && (p.LastCheckedAt == null || p.LastCheckedAt <= Now.AddMinutes(-5)))
            .Take(20)
            .ToListAsync();

        var done = 0;
        foreach (var refund in refunds)
        {
            var original = await pmsContext.Payments.FirstOrDefaultAsync(p => p.Id == refund.RefundOfPaymentId);
            if (original == null) continue;
            if (await SendRefundAsync(original, refund)) done++;
        }

        await pmsContext.SaveChangesAsync();
        await inventory.FlushAsync();
        return done;
    }

    // ---- applying results -------------------------------------------------------------------------

    private async Task<PaymentCallbackResultModel> QueryAndApplyAsync(Payment payment, string clientIp)
    {
        var gateway = Gateway(payment.GatewayProvider);
        var status = await gateway.QueryAsync(new GatewayQueryRequest(payment.PaymentCode, payment.CreatedAt, clientIp));

        await pmsContext.Payments.Where(p => p.Id == payment.Id).LoadAsync();
        var tracked = pmsContext.Payments.Local.First(p => p.Id == payment.Id);
        tracked.LastCheckedAt = Now;
        await pmsContext.SaveChangesAsync();

        switch (status.State)
        {
            case GatewayPaymentState.Succeeded:
                return await ApplyAsync(payment.PaymentCode, true, status.Amount ?? payment.Amount, status.TransactionNo, status.Message, null);
            case GatewayPaymentState.Failed:
                return await ApplyAsync(payment.PaymentCode, false, payment.Amount, status.TransactionNo, status.Message, null);
            default:
                if (payment.ExpiresAt <= Now.AddMinutes(-5) && await ExpireAsync(payment.Id))
                    return new PaymentCallbackResultModel { SignatureValid = true, Outcome = "Expired", Payment = ToModel(tracked) };
                return new PaymentCallbackResultModel
                {
                    SignatureValid = true,
                    Outcome = "Pending",
                    Message = status.Message ?? "The payment is still pending.",
                    Payment = ToModel(tracked)
                };
        }
    }

    /// <summary>
    /// Applies a gateway result once. A success that arrives after the reservation expired or after the
    /// session was already paid (cash, another QR) is refunded at once (FR-RSV-08, FR-PAY-08/09).
    /// </summary>
    private async Task<PaymentCallbackResultModel> ApplyAsync(string paymentCode, bool success, decimal amount, string transactionNo, string message, string payload)
    {
        if (string.IsNullOrWhiteSpace(paymentCode))
            return new PaymentCallbackResultModel { SignatureValid = true, Outcome = "NotFound", Message = "Missing payment code." };

        var gate = PaymentGates.GetOrAdd(paymentCode, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var payment = await pmsContext.Payments.Include(p => p.Invoice).FirstOrDefaultAsync(p => p.PaymentCode == paymentCode);
            if (payment == null)
                return new PaymentCallbackResultModel { SignatureValid = true, Outcome = "NotFound", Message = "Payment not found." };

            if (payment.Status != PaymentStatus.Pending && payment.Status != PaymentStatus.Expired && payment.Status != PaymentStatus.Failed)
                return new PaymentCallbackResultModel { SignatureValid = true, Outcome = "AlreadyProcessed", Payment = ToModel(payment) };

            if (success && amount != payment.Amount)
                return new PaymentCallbackResultModel { SignatureValid = true, Outcome = "InvalidAmount", Message = "Amount mismatch.", Payment = ToModel(payment) };

            payment.UpdatedAt = Now;
            if (payload != null) payment.PayloadResponse = payload;
            if (!string.IsNullOrWhiteSpace(transactionNo) && transactionNo != "0") payment.GatewayTransactionNo = transactionNo;

            if (!success)
            {
                // A failure after an earlier failure/expiry changes nothing
                if (payment.Status == PaymentStatus.Pending)
                {
                    payment.Status = PaymentStatus.Failed;
                    payment.FailureReason = message;
                    await pmsContext.SaveChangesAsync();
                    QueuePaymentPush(payment);
                    await inventory.FlushAsync();
                }
                return new PaymentCallbackResultModel { SignatureValid = true, Outcome = "Failed", Message = message, Payment = ToModel(payment) };
            }

            // Money was taken, even if our link had already expired or failed
            payment.Status = PaymentStatus.Succeeded;
            payment.PaidAt = Now;
            payment.FailureReason = null;
            if (payment.Invoice != null)
            {
                payment.Invoice.Status = "Paid";
                payment.Invoice.PaidAt = Now;
            }

            var outcome = payment.Purpose switch
            {
                PaymentPurpose.Deposit => await ApplyDepositAsync(payment),
                PaymentPurpose.ParkingFee => await ApplyFeeAsync(payment),
                PaymentPurpose.WalletTopUp => await ApplyWalletTopUpAsync(payment),
                _ => "Applied"
            };

            await pmsContext.SaveChangesAsync();
            QueuePaymentPush(payment);
            await inventory.FlushAsync();

            var session = payment.SessionId == null ? null : await pmsContext.ParkingSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == payment.SessionId);
            return new PaymentCallbackResultModel
            {
                SignatureValid = true,
                Outcome = outcome,
                Message = outcome == "Refunded"
                    ? "The payment came late or twice and was refunded."
                    : message,
                Payment = ToModel(payment, session)
            };
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> ApplyDepositAsync(Payment payment)
    {
        var reservation = await pmsContext.Reservations.FirstOrDefaultAsync(r => r.Id == payment.ReservationId);
        if (reservation == null)
            return "Applied";

        if (reservation.Status == ReservationStatus.PendingDeposit && payment.CreatedAt <= (reservation.DepositDueAt ?? DateTime.MaxValue))
        {
            ParkingStateMachines.EnsureReservation(reservation.Status, ReservationStatus.Confirmed);
            reservation.Status = ReservationStatus.Confirmed;
            reservation.ConfirmedAt = Now;
            reservation.UpdatedAt = Now;
            pmsContext.ReservationChangeLogs.Add(new ReservationChangeLog
            {
                ReservationId = reservation.Id,
                Action = "Confirmed",
                OldValue = ReservationStatus.PendingDeposit,
                NewValue = payment.PaymentCode,
                ChangedBy = "System",
                ChangedAt = Now
            });

            // Other links of the same deposit are closed so they cannot be paid a second time
            var others = await pmsContext.Payments
                .Where(p => p.ReservationId == reservation.Id && p.Id != payment.Id && p.Status == PaymentStatus.Pending)
                .ToListAsync();
            foreach (var other in others)
                other.Status = PaymentStatus.Expired;

            var (userId, id, lotId, slotId) = (reservation.UserId, reservation.Id, reservation.ParkingLotId, reservation.SlotId);
            inventory.Queue(() => notifier?.ReservationChangedAsync(userId, id, lotId, ReservationStatus.Confirmed, slotId) ?? Task.CompletedTask);
            return "Applied";
        }

        // FR-RSV-08 (callback after Expired / Cancelled) or FR-PAY-09 (second deposit): give it back
        await RefundAsync(payment, payment.Amount, reservation.Status == ReservationStatus.Confirmed
            ? $"Duplicate deposit refund {reservation.ReservationCode}"
            : $"Late deposit refund {reservation.ReservationCode}", "System");
        reservation.RefundAmount += payment.Amount;
        return "Refunded";
    }

    private async Task<string> ApplyFeeAsync(Payment payment)
    {
        var session = await pmsContext.ParkingSessions.FirstOrDefaultAsync(s => s.Id == payment.SessionId);
        if (session == null)
            return "Applied";

        if (session.Status == SessionStatus.PendingPayment)
        {
            SettleSession(session, payment);
            var (lotId, id, status, plate) = (session.ParkingLotId, session.Id, session.Status, session.EntryPlateNumber);
            inventory.Queue(() => notifier?.SessionChangedAsync(lotId, id, status, plate) ?? Task.CompletedTask);
            return "Applied";
        }

        // Paid already (cash or another QR), or the vehicle has gone: keep the first payment, refund this one
        await RefundAsync(payment, payment.Amount, $"Duplicate payment refund {session.SessionCode}", "System");
        var (lot, sessionId, code) = (session.ParkingLotId, session.Id, session.SessionCode);
        inventory.Queue(() => notifier?.OpsAlertAsync(lot, "DUPLICATE_PAYMENT_REFUNDED", $"The duplicate payment for {code} was refunded automatically.", sessionId) ?? Task.CompletedTask);
        return "Refunded";
    }

    /// <summary>The session is paid: Paid; prepaid at the app or a kiosk also gets an Exit Window (FR-PAY-07).</summary>
    private void SettleSession(ParkingSession session, Payment payment)
    {
        ParkingStateMachines.EnsureSession(session.Status, SessionStatus.Paid);
        session.PaidAmount += payment.Amount;
        session.Status = SessionStatus.Paid;
        session.UpdatedAt = Now;
        session.ExitWindowEndsAt = payment.Channel is PaymentChannel.App or PaymentChannel.Kiosk
            ? Now.Add(parkingOptions.ExitWindowTime)
            : null;
    }

    /// <summary>Creates a refund row and sends it (cash is handed back on the spot, FR-PAY-09).</summary>
    private async Task RefundAsync(Payment original, decimal amount, string reason, string requestedBy)
    {
        original.RefundedAmount += amount;
        original.UpdatedAt = Now;

        var refund = new Payment
        {
            PaymentCode = ParkingText.NewCode("RF", Now),
            Invoice = original.Invoice,
            InvoiceId = original.InvoiceId,
            PaymentMethod = original.PaymentMethod,
            GatewayProvider = original.GatewayProvider,
            Amount = amount,
            Currency = "VND",
            Status = PaymentStatus.RefundPending,
            Purpose = PaymentPurpose.Refund,
            ReservationId = original.ReservationId,
            SessionId = original.SessionId,
            ParkingLotId = original.ParkingLotId,
            CustomerUserId = original.CustomerUserId,
            CollectedByUserId = original.PaymentMethod == PaymentMethod.Cash ? original.CollectedByUserId : null,
            Channel = original.Channel,
            RefundOfPaymentId = original.Id,
            FailureReason = reason,
            CreatedAt = Now
        };
        pmsContext.Payments.Add(refund);

        if (original.PaymentMethod == PaymentMethod.Cash)
        {
            refund.Status = PaymentStatus.Refunded;
            refund.PaidAt = Now;
        }
        else if (original.PaymentMethod == PaymentMethod.Wallet)
        {
            refund.Status = PaymentStatus.Refunded;
            refund.PaidAt = Now;

            if (!string.IsNullOrWhiteSpace(original.CustomerUserId))
            {
                var customer = await GetOrCreateCustomerAsync(original.CustomerUserId);
                var wallet = await GetOrCreateWalletAsync(customer.Id);
                var before = wallet.Balance;
                wallet.Balance += amount;
                wallet.UpdatedAt = Now;

                pmsContext.WalletTransactions.Add(new WalletTransaction
                {
                    WalletId = wallet.Id,
                    TxnType = "Refund",
                    Amount = amount,
                    BalanceBefore = before,
                    BalanceAfter = wallet.Balance,
                    PaymentId = refund.Id,
                    ReferenceType = original.Purpose ?? "Refund",
                    ReferenceId = original.PaymentCode,
                    Description = $"Refund: {reason}",
                    CreatedAt = Now,
                    CreatedBy = requestedBy ?? "System"
                });
            }
        }
        else
        {
            await SendRefundAsync(original, refund);
        }

        QueuePaymentPush(refund);
    }

    /// <summary>Asks the gateway to refund; a refusal keeps the row RefundPending for the retry job / Manager queue.</summary>
    private async Task<bool> SendRefundAsync(Payment original, Payment refund)
    {
        refund.LastCheckedAt = Now;
        var gateway = gateways.FirstOrDefault(g => g.Provider == original.GatewayProvider);
        if (gateway is not { IsConfigured: true })
        {
            refund.FailureReason = "Gateway not configured.";
            return false;
        }

        GatewayRefundResult result;
        try
        {
            result = await gateway.RefundAsync(new GatewayRefundRequest(
                refund.PaymentCode, original.PaymentCode, original.GatewayTransactionNo, original.CreatedAt, refund.Amount,
                refund.Amount >= original.Amount, refund.FailureReason, "system", null));
        }
        catch (Exception e)
        {
            logger?.LogWarning(e, "Refund {Code} failed.", refund.PaymentCode);
            result = new GatewayRefundResult(false, null, e.Message);
        }

        if (result.Accepted)
        {
            refund.Status = PaymentStatus.Refunded;
            refund.PaidAt = Now;
            refund.GatewayTransactionNo = result.TransactionNo;
            refund.FailureReason = null;
            return true;
        }

        refund.FailureReason = result.Message;
        if (original.ParkingLotId is { } lotId)
        {
            var code = refund.PaymentCode;
            inventory.Queue(() => notifier?.OpsAlertAsync(lotId, "REFUND_FAILED", $"Refund for {code} failed: {result.Message}", original.SessionId) ?? Task.CompletedTask);
        }
        return false;
    }

    private async Task<bool> ExpireAsync(long paymentId)
    {
        var payment = await pmsContext.Payments.FirstOrDefaultAsync(p => p.Id == paymentId && p.Status == PaymentStatus.Pending);
        if (payment == null) return false;
        payment.Status = PaymentStatus.Expired;
        payment.UpdatedAt = Now;
        await pmsContext.SaveChangesAsync();
        QueuePaymentPush(payment);
        await inventory.FlushAsync();
        return true;
    }

    // ---- building payments ------------------------------------------------------------------------

    private async Task StartCheckoutAsync(IPaymentGateway gateway, Payment payment, string orderInfo, string returnUrl, PaymentRequestContext context)
    {
        pmsContext.Invoices.Add(payment.Invoice);
        pmsContext.Payments.Add(payment);
        await pmsContext.SaveChangesAsync();

        // Simulation without gateway keys: the payment exists but has no pay page; it is completed
        // with SimulateSuccessAsync instead of the gateway
        if (!gateway.IsConfigured && gatewayOptions.AllowSimulatedPayments)
            return;

        try
        {
            var provider = gateway.Provider.ToLowerInvariant();
            var apiBase = (string.IsNullOrWhiteSpace(gatewayOptions.PublicApiBaseUrl) ? context?.ApiBaseUrl : gatewayOptions.PublicApiBaseUrl)?.TrimEnd('/');
            var checkout = await gateway.CreateCheckoutAsync(new GatewayCheckoutRequest(
                payment.PaymentCode,
                payment.Amount,
                orderInfo,
                $"{ReturnBase(returnUrl)}/{provider}",
                $"{apiBase}/api/payments/{provider}/ipn",
                context?.ClientIp,
                payment.CreatedAt,
                payment.ExpiresAt ?? payment.CreatedAt.AddMinutes(15)));

            payment.PayUrl = checkout.PayUrl;
            if (checkout.RawResponse != null) payment.PayloadResponse = checkout.RawResponse;
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException)
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = e.Message;
            await pmsContext.SaveChangesAsync();
            throw new UnprocessableRequestException($"Could not create the {gateway.Provider} payment: {e.Message}");
        }

        await pmsContext.SaveChangesAsync();
    }

    /// <summary>The client's return page if its origin is allowed (no open redirects), else the configured page.</summary>
    private string ReturnBase(string requested)
    {
        if (!string.IsNullOrWhiteSpace(requested) && Uri.TryCreate(requested, UriKind.Absolute, out var uri))
        {
            var origin = uri.GetLeftPart(UriPartial.Authority);
            if (gatewayOptions.AllowedReturnOrigins?.Any(o => string.Equals(o.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase)) == true)
                return requested.TrimEnd('/');
        }

        return gatewayOptions.FrontendReturnUrl.TrimEnd('/');
    }

    private IPaymentGateway Gateway(string provider)
    {
        var gateway = gateways.FirstOrDefault(g => string.Equals(g.Provider, provider?.Trim(), StringComparison.OrdinalIgnoreCase))
                      ?? throw new BadRequestException("Provider must be VNPay or MoMo.");
        if (!gateway.IsConfigured && !gatewayOptions.AllowSimulatedPayments)
            throw new UnprocessableRequestException($"{gateway.Provider} is not configured on the server.");
        return gateway;
    }

    public bool SimulationEnabled => gatewayOptions.AllowSimulatedPayments;

    public async Task<PaymentCallbackResultModel> SimulateSuccessAsync(long paymentId)
    {
        if (!gatewayOptions.AllowSimulatedPayments)
            throw new ForbiddenException("Simulated payments are turned off on this server.");

        var payment = await pmsContext.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId)
                      ?? throw new NotFoundException("Payment not found.");
        // Only the customer who owns the payment or Staff of its lot, like reading it
        await EnsureCanSeeAsync(payment);
        if (payment.GatewayProvider == null)
            throw new BadRequestException("Only VNPay and MoMo payments can be simulated.");
        if (payment.Status != PaymentStatus.Pending)
            return new PaymentCallbackResultModel { SignatureValid = true, Outcome = "AlreadyProcessed", Payment = ToModel(payment) };

        logger?.LogWarning("Simulated {Provider} success for payment {Code} ({Amount} VND).", payment.GatewayProvider, payment.PaymentCode, payment.Amount);
        // Same path as a verified gateway callback: deposit confirmed, wallet credited, session paid…
        return await ApplyAsync(payment.PaymentCode, true, payment.Amount, $"SIM{Now:yyyyMMddHHmmss}",
            "Simulated success (testing only)", JsonSerializer.Serialize(new { simulated = true, payment.PaymentCode }));
    }

    private Invoice NewInvoice(int lotId, int? customerId, decimal amount, DateTime dueDate) => new()
    {
        InvoiceNumber = ParkingText.NewCode("INV", Now),
        CustomerId = customerId,
        ParkingLotId = lotId,
        SubTotal = amount,
        TotalAmount = amount,
        Status = "Unpaid",
        DueDate = dueDate,
        CreatedAt = Now
    };

    private Payment NewPayment(Invoice invoice, string method, string provider, decimal amount, string purpose, string channel) => new()
    {
        PaymentCode = ParkingText.NewCode("PM", Now),
        Invoice = invoice,
        PaymentMethod = method,
        GatewayProvider = provider,
        Amount = amount,
        Currency = "VND",
        Status = PaymentStatus.Pending,
        Purpose = purpose,
        Channel = channel,
        CreatedAt = Now
    };

    private async Task EnsureCanSeeAsync(Payment payment)
    {
        var userId = claimService.GetUserId();
        var role = claimService.GetClaim(ClaimTypes.Role);
        if (payment.CustomerUserId != null && payment.CustomerUserId == userId)
            return;
        if (payment.ParkingLotId is { } lotId && await lotScope.CanOperateAsync(userId, role, lotId))
            return;
        throw new NotFoundException("Payment not found.");
    }

    private void QueuePaymentPush(Payment payment)
    {
        if (notifier == null) return;
        var (userId, lotId, id, status, purpose, reservationId, sessionId) =
            (payment.CustomerUserId, payment.ParkingLotId, payment.Id, payment.Status, payment.Purpose, payment.ReservationId, payment.SessionId);
        inventory.Queue(() => notifier.PaymentChangedAsync(userId, lotId, id, status, purpose, reservationId, sessionId));
    }

    private PaymentResponseModel ToModel(Payment p, ParkingSession session = null)
    {
        var model = new PaymentResponseModel
        {
            Id = p.Id,
            Code = p.PaymentCode,
            Provider = p.GatewayProvider,
            Method = p.PaymentMethod,
            Purpose = p.Purpose,
            Channel = p.Channel,
            Amount = p.Amount,
            Status = p.Status,
            PayUrl = p.Status == PaymentStatus.Pending ? p.PayUrl : null,
            ExpiresAt = p.ExpiresAt,
            CreatedAt = p.CreatedAt,
            PaidAt = p.PaidAt,
            ReservationId = p.ReservationId,
            SessionId = p.SessionId,
            RefundedAmount = p.RefundedAmount,
            RefundOfPaymentId = p.RefundOfPaymentId,
            Message = p.FailureReason,
            CallbackDeadline = p.Status == PaymentStatus.Pending ? p.CreatedAt.AddSeconds(parkingOptions.Gate.PendingPaymentCallbackSeconds) : null
        };

        // Exit Pass: a signed proof of payment that an offline gate accepts until the window ends
        if (session is { Status: SessionStatus.Paid, ExitWindowEndsAt: { } until } && p.Status == PaymentStatus.Succeeded)
        {
            model.ExitWindowEndsAt = until;
            model.ExitPass = signer.Sign(ParkingTokenSigner.ExitPassKind, session.ParkingLotId, new Dictionary<string, string>
            {
                ["sc"] = session.SessionCode,
                ["until"] = until.ToString("O")
            });
        }

        return model;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    public async Task<PaymentResponseModel> CreateWalletTopUpPaymentAsync(string userId, TopUpWalletModel model, PaymentRequestContext context)
    {
        if (model == null || model.Amount <= 0)
            throw new BadRequestException("Please enter a valid top-up amount.");

        var walletRules = parkingOptions.Wallet;
        if (model.Amount < walletRules.MinTopUp)
            throw new BadRequestException($"Minimum top-up amount is {Vnd(walletRules.MinTopUp)}.");

        // Daily cap per customer: paid top-ups and still-payable links of today both count
        var remaining = walletRules.DailyTopUpLimit - await TopUpsTodayAsync(userId);
        if (model.Amount > remaining)
            throw new BadRequestException(remaining > 0
                ? $"You can top up at most {Vnd(walletRules.DailyTopUpLimit)} a day. You can still add {Vnd(remaining)} today."
                : $"You have reached today's top-up limit of {Vnd(walletRules.DailyTopUpLimit)}. Try again tomorrow.");

        var gateway = Gateway(model.Provider);
        var customer = await GetOrCreateCustomerAsync(userId);
        await GetOrCreateWalletAsync(customer.Id);

        var lotId = await pmsContext.ParkingLots.Select(l => l.Id).FirstOrDefaultAsync();
        var expiresAt = Now.AddMinutes(parkingOptions.Gate.PaymentLinkMinutes);
        var invoice = NewInvoice(lotId, customer.Id, model.Amount, expiresAt);
        var payment = NewPayment(invoice, PaymentMethod.Online, gateway.Provider, model.Amount, PaymentPurpose.WalletTopUp, PaymentChannel.App);
        payment.CustomerUserId = userId;
        payment.ParkingLotId = lotId;
        payment.ExpiresAt = expiresAt;

        var effectiveReturn = string.IsNullOrWhiteSpace(model.ReturnUrl)
            ? $"{gatewayOptions.FrontendReturnUrl.TrimEnd('/')}/{gateway.Provider.ToLowerInvariant()}"
            : model.ReturnUrl;

        await StartCheckoutAsync(gateway, payment, $"Nap vi XXIV Wallet {payment.PaymentCode}", effectiveReturn, context);
        return ToModel(payment);
    }

    private async Task<string> ApplyWalletTopUpAsync(Payment payment)
    {
        if (string.IsNullOrWhiteSpace(payment.CustomerUserId))
            return "Applied";

        var customer = await GetOrCreateCustomerAsync(payment.CustomerUserId);
        var wallet = await GetOrCreateWalletAsync(customer.Id);

        var before = wallet.Balance;
        wallet.Balance += payment.Amount;
        wallet.UpdatedAt = Now;

        pmsContext.WalletTransactions.Add(new WalletTransaction
        {
            WalletId = wallet.Id,
            TxnType = "Deposit",
            Amount = payment.Amount,
            BalanceBefore = before,
            BalanceAfter = wallet.Balance,
            PaymentId = payment.Id,
            ReferenceType = "Payment",
            ReferenceId = payment.PaymentCode,
            Description = $"Wallet Top-up ({payment.GatewayProvider})",
            CreatedAt = Now,
            CreatedBy = payment.CustomerUserId
        });

        await pmsContext.SaveChangesAsync();
        return "Applied";
    }

    public async Task<WalletResponseModel> GetWalletAsync(string userId)
    {
        var customer = await GetOrCreateCustomerAsync(userId);
        var wallet = await GetOrCreateWalletAsync(customer.Id);

        var holdAmount = await pmsContext.ParkingSessions
            .Where(s => s.CustomerId == customer.Id && SessionStatus.Inside.Contains(s.Status))
            .Select(s => (decimal?)s.FinalFee)
            .SumAsync() ?? 0m;

        var txs = await pmsContext.WalletTransactions
            .Where(t => t.WalletId == wallet.Id)
            .OrderByDescending(t => t.CreatedAt)
            .Take(10)
            .ToListAsync();

        var recent = txs.Select(t => new WalletTransactionResponseModel
        {
            Id = t.ReferenceId ?? $"TX-{t.Id}",
            Title = t.Description ?? (t.Amount >= 0 ? "Wallet Deposit" : "Wallet Withdrawal"),
            Subtitle = $"{t.CreatedAt:MMM dd, HH:mm} • Ref: {t.ReferenceId ?? t.Id.ToString()}",
            Amount = t.Amount,
            Status = "Completed",
            Type = t.Amount >= 0 ? "deposit" : "withdraw",
            Method = t.ReferenceType ?? "Wallet",
            CreatedAt = t.CreatedAt
        }).ToList();

        return new WalletResponseModel
        {
            Balance = wallet.Balance,
            HoldAmount = holdAmount,
            Currency = wallet.Currency ?? "VND",
            Status = wallet.Status ?? "Active",
            DailyTopUpLimit = parkingOptions.Wallet.DailyTopUpLimit,
            TopUpRemainingToday = Math.Max(0, parkingOptions.Wallet.DailyTopUpLimit - await TopUpsTodayAsync(userId)),
            MinTopUp = parkingOptions.Wallet.MinTopUp,
            RecentTransactions = recent
        };
    }

    public async Task<WalletResponseModel> WithdrawWalletAsync(string userId, WithdrawWalletModel model)
    {
        if (model == null || model.Amount <= 0)
            throw new BadRequestException("Please enter a valid withdrawal amount.");

        var customer = await GetOrCreateCustomerAsync(userId);
        var wallet = await GetOrCreateWalletAsync(customer.Id);

        if (wallet.Balance < model.Amount)
            throw new BadRequestException("Available balance is insufficient for withdrawal.");

        var before = wallet.Balance;
        wallet.Balance -= model.Amount;
        wallet.UpdatedAt = Now;

        var methodLabel = string.Equals(model.Method, "momo", StringComparison.OrdinalIgnoreCase) ? "MoMo Wallet" : "Bank Account";
        var dest = string.IsNullOrWhiteSpace(model.BankName) ? methodLabel : $"{model.BankName} ({model.BankAccount})";

        pmsContext.WalletTransactions.Add(new WalletTransaction
        {
            WalletId = wallet.Id,
            TxnType = "Withdraw",
            Amount = -model.Amount,
            BalanceBefore = before,
            BalanceAfter = wallet.Balance,
            ReferenceType = model.Method ?? "Bank",
            ReferenceId = ParkingText.NewCode("WD", Now),
            Description = $"Withdrawal to {dest}",
            CreatedAt = Now,
            CreatedBy = userId
        });

        await pmsContext.SaveChangesAsync();
        return await GetWalletAsync(userId);
    }

    private async Task<Customer> GetOrCreateCustomerAsync(string userId)
    {
        var customer = await pmsContext.Customers.FirstOrDefaultAsync(c => c.UserId == userId);
        if (customer != null)
            return customer;

        customer = new Customer
        {
            UserId = userId,
            CustomerCode = $"CUS-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            FullName = "Customer",
            CustomerType = "Individual",
            MembershipTier = "Standard",
            IsActive = true,
            CreatedAt = Now
        };
        pmsContext.Customers.Add(customer);
        await pmsContext.SaveChangesAsync();
        return customer;
    }

    /// <summary>"10,000,000 VND", the same in every server culture.</summary>
    private static string Vnd(decimal amount) => $"{amount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} VND";

    /// <summary>Midnight of today in the business time zone (Vietnam by default), as UTC.</summary>
    private DateTime TodayStartUtc()
    {
        var zone = ParkingText.FindTimeZone(parkingOptions.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(Now, DateTimeKind.Utc), zone);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localNow.Date, DateTimeKind.Unspecified), zone);
    }

    /// <summary>
    /// What the customer topped up today for the daily limit: succeeded top-ups plus payment links
    /// that can still be paid (expired, failed and refunded ones do not count).
    /// </summary>
    private async Task<decimal> TopUpsTodayAsync(string userId)
    {
        var dayStart = TodayStartUtc();
        var now = Now;
        return await pmsContext.Payments
            .Where(p => p.Purpose == PaymentPurpose.WalletTopUp && p.CustomerUserId == userId && p.CreatedAt >= dayStart
                        && (p.Status == PaymentStatus.Succeeded || (p.Status == PaymentStatus.Pending && (p.ExpiresAt == null || p.ExpiresAt > now))))
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;
    }

    private async Task<Wallet> GetOrCreateWalletAsync(int customerId)
    {
        var wallet = await pmsContext.Wallets.FirstOrDefaultAsync(w => w.CustomerId == customerId);
        if (wallet != null)
            return wallet;

        wallet = new Wallet
        {
            CustomerId = customerId,
            Balance = 0,
            Currency = "VND",
            Status = "Active",
            UpdatedAt = Now,
            // SQL Server generates the rowversion; the in-memory test database needs a value
            RowVersion = []
        };
        pmsContext.Wallets.Add(wallet);
        await pmsContext.SaveChangesAsync();
        return wallet;
    }
}
