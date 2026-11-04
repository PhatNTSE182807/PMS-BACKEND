using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Daily cash close per Staff and lot (BR-12, SRS 4.3 "Daily cash close"). A drawer opens with a counted float,
/// every cash payment the Staff records adds the notes received and removes the change given, and at the close
/// the Staff counts the banknotes blind. A difference over the threshold needs a reason and goes to the lot's
/// Manager, who confirms it (Maker-Checker: never the Staff who closed it). Cash taken after a close opens the
/// day's next drawer. Drawers are never deleted.
/// </summary>
public class CashDrawerService(
    PmsDbContext pmsContext,
    LotScopeService lotScope,
    ParkingOptions options,
    TimeProvider clock,
    IParkingRealtimeNotifier notifier = null)
{
    /// <summary>BR-12: the latest close time, lot local time.</summary>
    public static readonly TimeOnly CloseDeadline = new(23, 0);

    /// <summary>BR-12: a larger difference needs a reason and a Manager's confirmation.</summary>
    public const decimal DifferenceThreshold = 20_000;

    /// <summary>Vietnamese banknotes the drawer counts, largest first.</summary>
    public static readonly IReadOnlyList<int> Denominations = [500_000, 200_000, 100_000, 50_000, 20_000, 10_000, 5_000, 2_000, 1_000];

    private const int MaxNotesPerValue = 100_000;
    private const string NotOpened = "NotOpened";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>The signed-in Staff's latest drawer of today at the lot, with the live expected cash.</summary>
    public async Task<CashDrawerModel> GetCurrentAsync(int lotId)
    {
        await lotScope.EnsureCanOperateAsync(lotId);
        var today = await BusinessDateAsync(lotId);
        var drawer = await LatestAsync(lotId, lotScope.UserId, today);
        if (drawer == null)
            return new CashDrawerModel
            {
                ParkingLotId = lotId,
                StaffUserId = lotScope.UserId,
                BusinessDate = today,
                Status = NotOpened,
                DifferenceThreshold = DifferenceThreshold,
                Denominations = Denominations
            };
        return await ToModelAsync(drawer);
    }

    /// <summary>Opens today's drawer with the float the Staff counted in.</summary>
    public async Task<CashDrawerModel> OpenAsync(int lotId, OpenCashDrawerModel model)
    {
        await lotScope.EnsureCanOperateAsync(lotId);
        var notes = CleanNotes(model?.Float, "float");
        var drawer = await OpenDrawerAsync(lotId, lotScope.UserId, notes)
                     ?? throw new BadRequestException("Your cash drawer is already open.");
        return await ToModelAsync(drawer);
    }

    /// <summary>
    /// Before a Staff records cash: makes sure a drawer is open (an extra close of the day when the first one is
    /// already closed, starting with no float).
    /// </summary>
    public async Task EnsureOpenAsync(int lotId, string staffUserId)
    {
        if (string.IsNullOrEmpty(staffUserId)) return;
        var latest = await LatestAsync(lotId, staffUserId, await BusinessDateAsync(lotId));
        if (latest is { Status: CashCloseStatus.Open }) return;
        await OpenDrawerAsync(lotId, staffUserId, []);
    }

    /// <summary>Closes the drawer with the blind count; a difference over the threshold needs a reason.</summary>
    public async Task<CashDrawerModel> CloseAsync(int lotId, CloseCashDrawerModel model)
    {
        await lotScope.EnsureCanOperateAsync(lotId);
        var drawer = await LatestAsync(lotId, lotScope.UserId, await BusinessDateAsync(lotId), tracked: true);
        if (drawer is not { Status: CashCloseStatus.Open })
            throw new BadRequestException("Open your cash drawer before closing it.");

        var counted = CleanNotes(model?.Counted, "count");
        var countedTotal = Total(counted);
        var (collected, _, _, _) = await CashTakenAsync(drawer, Now);
        var (topUps, _) = await ApprovedTopUpsAsync(drawer.Id);
        var expected = drawer.FloatTotal + collected + topUps;
        var difference = countedTotal - expected;
        var reason = model?.Reason?.Trim();
        var overThreshold = Math.Abs(difference) > DifferenceThreshold;
        if (overThreshold && string.IsNullOrEmpty(reason))
            throw new BadRequestException(
                $"The count is {difference:+#,0;-#,0} VND off the expected cash, more than {DifferenceThreshold:#,0} VND: give the reason to close the drawer.");
        if (reason?.Length > 500)
            throw new BadRequestException("The reason is too long (500 characters at most).");

        drawer.CountedJson = Write(counted);
        drawer.CountedTotal = countedTotal;
        drawer.ExpectedTotal = expected;
        drawer.Difference = difference;
        drawer.DifferenceReason = string.IsNullOrEmpty(reason) ? null : reason;
        drawer.ClosedAt = Now;
        drawer.Status = overThreshold ? CashCloseStatus.HasDifference : CashCloseStatus.Closed;
        await pmsContext.SaveChangesAsync();

        // The Manager hears about every close at once, with the difference when there is one
        var who = await StaffNameAsync(drawer.StaffUserId);
        var late = drawer.CloseDueAt != null && drawer.ClosedAt > drawer.CloseDueAt ? " late" : string.Empty;
        await AlertAsync(drawer.ParkingLotId, "CASH_CLOSED", difference == 0
            ? $"{who} closed cash drawer {drawer.CloseNo}{late}: it balances. Confirm it in the Approval Queue."
            : $"{who} closed cash drawer {drawer.CloseNo}{late}: {difference:+#,0;-#,0} VND off. Check it in the Approval Queue.");
        return await ToModelAsync(drawer);
    }

    /// <summary>A lot's drawers for its Manager: closed ones waiting for confirmation by default, newest first.</summary>
    public async Task<IReadOnlyList<CashDrawerModel>> GetForLotAsync(int lotId, string status)
    {
        await lotScope.EnsureCanManageAsync(lotId);
        var query = pmsContext.DailyCashCloses.AsNoTracking().Where(d => d.ParkingLotId == lotId);
        query = string.IsNullOrWhiteSpace(status)
            ? query.Where(d => d.Status == CashCloseStatus.Closed || d.Status == CashCloseStatus.HasDifference || (d.Status == CashCloseStatus.Open && d.OverdueAt != null))
            : query.Where(d => d.Status == status.Trim());
        var drawers = await query.OrderByDescending(d => d.BusinessDate).ThenByDescending(d => d.ClosedAt).Take(100).ToListAsync();

        var models = new List<CashDrawerModel>();
        foreach (var drawer in drawers)
            models.Add(await ToModelAsync(drawer));
        return models;
    }

    /// <summary>The Manager confirms a closed drawer; a difference over the threshold needs a note.</summary>
    public async Task<CashDrawerModel> ConfirmAsync(long id, ConfirmCashCloseModel model)
    {
        var drawer = await pmsContext.DailyCashCloses.FirstOrDefaultAsync(d => d.Id == id)
                     ?? throw new NotFoundException("Cash close not found.");
        await lotScope.EnsureCanManageAsync(drawer.ParkingLotId);
        if (drawer.Status is not (CashCloseStatus.Closed or CashCloseStatus.HasDifference))
            throw new BadRequestException(drawer.Status == CashCloseStatus.Open ? "This drawer is still open." : "This cash close is already confirmed.");
        // Maker-Checker: whoever counted the drawer never confirms it
        if (drawer.StaffUserId == lotScope.UserId)
            throw new ForbiddenException("You closed this drawer, so another Manager has to confirm it.");

        var note = model?.Note?.Trim();
        if (drawer.Status == CashCloseStatus.HasDifference && string.IsNullOrEmpty(note))
            throw new BadRequestException("This close has a difference: write what you checked before confirming it.");
        if (note?.Length > 500)
            throw new BadRequestException("The note is too long (500 characters at most).");

        drawer.Status = CashCloseStatus.Confirmed;
        drawer.ConfirmedBy = lotScope.UserId;
        drawer.ConfirmedAt = Now;
        drawer.ConfirmNote = string.IsNullOrEmpty(note) ? null : note;
        await pmsContext.SaveChangesAsync();
        return await ToModelAsync(drawer);
    }

    /// <summary>
    /// Admin simulation: the business day of one lot (or of every lot) ends now. Every open drawer must be closed
    /// within the grace period; its Staff are told at once, and the Manager when the deadline passes.
    /// </summary>
    public async Task<EndBusinessDayResultModel> EndBusinessDayAsync(EndBusinessDayModel model)
    {
        var grace = model?.GraceSeconds ?? 30;
        if (grace is < 5 or > 3600)
            throw new BadRequestException("Give the Staff between 5 and 3,600 seconds to close.");
        var lotId = model?.LotId;
        if (lotId != null && !await pmsContext.ParkingLots.AnyAsync(p => p.Id == lotId && !p.IsDeleted))
            throw new NotFoundException("Parking lot not found.");

        var due = Now.AddSeconds(grace);
        var open = await pmsContext.DailyCashCloses
            .Where(d => d.Status == CashCloseStatus.Open && (lotId == null || d.ParkingLotId == lotId))
            .ToListAsync();
        foreach (var drawer in open)
        {
            drawer.CloseDueAt = due;
            drawer.OverdueAt = null;
        }
        await pmsContext.SaveChangesAsync();

        foreach (var lot in open.Select(d => d.ParkingLotId).Distinct())
            await AlertAsync(lot, "CASH_DAY_END", $"The business day is over: count and close your cash drawer within {grace} seconds.");
        return new EndBusinessDayResultModel { DrawersToClose = open.Count, CloseDueAt = Utc(due) };
    }

    /// <summary>
    /// Background run (every minute, and every second while simulating): open drawers past their deadline are
    /// marked overdue and the lot's Manager is told (BR-12 escalation). They can still be closed, marked late.
    /// </summary>
    public async Task<int> FlagOverdueAsync()
    {
        var overdue = await pmsContext.DailyCashCloses
            .Where(d => d.Status == CashCloseStatus.Open && d.OverdueAt == null && d.CloseDueAt != null && d.CloseDueAt <= Now)
            .ToListAsync();
        foreach (var drawer in overdue)
            drawer.OverdueAt = Now;
        await pmsContext.SaveChangesAsync();

        foreach (var drawer in overdue)
        {
            var (collected, _, _, _) = await CashTakenAsync(drawer, Now);
            var (topUps, _) = await ApprovedTopUpsAsync(drawer.Id);
            var who = await StaffNameAsync(drawer.StaffUserId);
            await AlertAsync(drawer.ParkingLotId, "CASH_OVERDUE",
                $"{who} has not closed cash drawer {drawer.CloseNo} by the deadline ({drawer.FloatTotal + collected + topUps:#,0} VND expected).");
        }
        return overdue.Count;
    }

    // ---- more banknotes for a drawer (team addition, 2026-11-04) ----------------------------------

    /// <summary>
    /// The Staff's drawer is short of banknotes (usually change): they list the notes they need for the lot's
    /// Manager to approve. A drawer has at most one request waiting at a time.
    /// </summary>
    public async Task<CashTopUpModel> RequestTopUpAsync(int lotId, RequestCashTopUpModel model)
    {
        await lotScope.EnsureCanOperateAsync(lotId);
        var notes = CleanNotes(model?.Notes, "request");
        if (notes.Count == 0)
            throw new BadRequestException("List the banknotes you need.");
        var reason = model?.Reason?.Trim();
        if (reason?.Length > 500)
            throw new BadRequestException("The reason is too long (500 characters at most).");

        // The notes go into the open drawer; with none open, today's next drawer opens empty to receive them
        await EnsureOpenAsync(lotId, lotScope.UserId);
        var drawer = await LatestAsync(lotId, lotScope.UserId, await BusinessDateAsync(lotId));
        if (await pmsContext.CashTopUpRequests.AnyAsync(r => r.DailyCashCloseId == drawer.Id && r.Status == CashTopUpStatus.Pending))
            throw new BadRequestException("You already have a request waiting for the Manager: cancel it to ask for different notes.");

        var request = new CashTopUpRequest
        {
            ParkingLotId = lotId,
            DailyCashCloseId = drawer.Id,
            StaffUserId = lotScope.UserId,
            NotesJson = Write(notes),
            Total = Total(notes),
            Reason = string.IsNullOrEmpty(reason) ? null : reason,
            Status = CashTopUpStatus.Pending,
            CreatedAt = Now
        };
        pmsContext.CashTopUpRequests.Add(request);
        await pmsContext.SaveChangesAsync();

        var who = await StaffNameAsync(request.StaffUserId);
        await AlertAsync(lotId, "CASH_TOPUP_REQUESTED",
            $"{who} needs {request.Total:#,0} VND in banknotes for cash drawer {drawer.CloseNo}: approve it in the Approval Queue.");
        return (await ToTopUpModelsAsync([request]))[0];
    }

    /// <summary>The Staff withdraws their own request before the Manager decides.</summary>
    public async Task<CashTopUpModel> CancelTopUpAsync(int lotId, long id)
    {
        await lotScope.EnsureCanOperateAsync(lotId);
        var request = await pmsContext.CashTopUpRequests.FirstOrDefaultAsync(r => r.Id == id && r.ParkingLotId == lotId && r.StaffUserId == lotScope.UserId)
                      ?? throw new NotFoundException("Cash request not found.");
        if (request.Status != CashTopUpStatus.Pending)
            throw new BadRequestException("The Manager already decided on this request.");

        Decide(request, CashTopUpStatus.Cancelled, null);
        await pmsContext.SaveChangesAsync();
        return (await ToTopUpModelsAsync([request]))[0];
    }

    /// <summary>A lot's requests for its Manager: waiting ones by default, oldest first; or one status, newest first.</summary>
    public async Task<IReadOnlyList<CashTopUpModel>> GetTopUpsForLotAsync(int lotId, string status)
    {
        await lotScope.EnsureCanManageAsync(lotId);
        var query = pmsContext.CashTopUpRequests.AsNoTracking().Where(r => r.ParkingLotId == lotId);
        var requests = string.IsNullOrWhiteSpace(status)
            ? await query.Where(r => r.Status == CashTopUpStatus.Pending).OrderBy(r => r.CreatedAt).ToListAsync()
            : await query.Where(r => r.Status == status.Trim()).OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync();
        return await ToTopUpModelsAsync(requests);
    }

    /// <summary>The Manager approves: the notes count as cash put into the Staff's drawer (never one's own request).</summary>
    public async Task<CashTopUpModel> ApproveTopUpAsync(long id, DecideCashTopUpModel model)
    {
        var request = await PendingTopUpAsync(id);
        var drawer = await pmsContext.DailyCashCloses.AsNoTracking().FirstAsync(d => d.Id == request.DailyCashCloseId);
        if (drawer.Status != CashCloseStatus.Open)
            throw new BadRequestException("The Staff already closed this drawer: reject the request and let them ask again from their new drawer.");

        Decide(request, CashTopUpStatus.Approved, Note(model, required: false));
        await pmsContext.SaveChangesAsync();
        await AlertAsync(request.ParkingLotId, "CASH_TOPUP_APPROVED",
            $"{request.Total:#,0} VND in banknotes approved for {await StaffNameAsync(request.StaffUserId)}'s cash drawer {drawer.CloseNo}: hand the notes over.");
        return (await ToTopUpModelsAsync([request]))[0];
    }

    /// <summary>The Manager rejects with a reason the Staff sees.</summary>
    public async Task<CashTopUpModel> RejectTopUpAsync(long id, DecideCashTopUpModel model)
    {
        var request = await PendingTopUpAsync(id);
        Decide(request, CashTopUpStatus.Rejected, Note(model, required: true));
        await pmsContext.SaveChangesAsync();
        await AlertAsync(request.ParkingLotId, "CASH_TOPUP_REJECTED",
            $"The cash request of {await StaffNameAsync(request.StaffUserId)} was rejected: {request.DecisionNote}");
        return (await ToTopUpModelsAsync([request]))[0];
    }

    private async Task<CashTopUpRequest> PendingTopUpAsync(long id)
    {
        var request = await pmsContext.CashTopUpRequests.FirstOrDefaultAsync(r => r.Id == id)
                      ?? throw new NotFoundException("Cash request not found.");
        await lotScope.EnsureCanManageAsync(request.ParkingLotId);
        if (request.Status != CashTopUpStatus.Pending)
            throw new BadRequestException(request.Status == CashTopUpStatus.Cancelled
                ? "The Staff cancelled this request."
                : "This request was already decided.");
        // Maker-Checker: whoever asked for the cash never approves it
        if (request.StaffUserId == lotScope.UserId)
            throw new ForbiddenException("You made this request, so another Manager has to decide on it.");
        return request;
    }

    private void Decide(CashTopUpRequest request, string status, string note)
    {
        request.Status = status;
        request.DecidedBy = lotScope.UserId;
        request.DecidedAt = Now;
        request.DecisionNote = note;
    }

    private static string Note(DecideCashTopUpModel model, bool required)
    {
        var note = model?.Note?.Trim();
        if (required && string.IsNullOrEmpty(note))
            throw new BadRequestException("Give the Staff a reason for rejecting the request.");
        if (note?.Length > 500)
            throw new BadRequestException("The note is too long (500 characters at most).");
        return string.IsNullOrEmpty(note) ? null : note;
    }

    /// <summary>Cash and banknotes the Manager approved into a drawer.</summary>
    private async Task<(decimal Total, Dictionary<int, int> Notes)> ApprovedTopUpsAsync(long drawerId)
    {
        var approved = await pmsContext.CashTopUpRequests.AsNoTracking()
            .Where(r => r.DailyCashCloseId == drawerId && r.Status == CashTopUpStatus.Approved)
            .Select(r => new { r.Total, r.NotesJson })
            .ToListAsync();
        var notes = new Dictionary<int, int>();
        foreach (var request in approved)
            foreach (var (value, count) in Read(request.NotesJson))
                notes[value] = notes.GetValueOrDefault(value) + count;
        return (approved.Sum(r => r.Total), notes);
    }

    private async Task<List<CashTopUpModel>> ToTopUpModelsAsync(IReadOnlyCollection<CashTopUpRequest> requests)
    {
        if (requests.Count == 0)
            return [];
        var userIds = requests.Select(r => r.StaffUserId).Concat(requests.Where(r => r.DecidedBy != null).Select(r => r.DecidedBy)).Distinct().ToList();
        var names = await pmsContext.Employees.AsNoTracking()
            .Where(e => userIds.Contains(e.UserId))
            .ToDictionaryAsync(e => e.UserId, e => e.FullName);
        var drawerIds = requests.Select(r => r.DailyCashCloseId).Distinct().ToList();
        var drawers = await pmsContext.DailyCashCloses.AsNoTracking()
            .Where(d => drawerIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => new { d.CloseNo, d.Status });

        return requests.Select(r =>
        {
            var drawer = drawers.GetValueOrDefault(r.DailyCashCloseId);
            return new CashTopUpModel
            {
                Id = r.Id,
                ParkingLotId = r.ParkingLotId,
                DrawerId = r.DailyCashCloseId,
                CloseNo = drawer?.CloseNo ?? 0,
                StaffUserId = r.StaffUserId,
                StaffName = names.GetValueOrDefault(r.StaffUserId),
                Notes = Read(r.NotesJson),
                Total = r.Total,
                Reason = r.Reason,
                Status = r.Status,
                CreatedAt = Utc(r.CreatedAt),
                DecidedBy = r.DecidedBy,
                DecidedByName = r.DecidedBy != null ? names.GetValueOrDefault(r.DecidedBy) : null,
                DecidedAt = r.DecidedAt is { } decided ? Utc(decided) : null,
                DecisionNote = r.DecisionNote,
                DrawerOpen = drawer?.Status == CashCloseStatus.Open
            };
        }).ToList();
    }

    // ---- helpers ----------------------------------------------------------------------------------

    /// <summary>23:00 of the business day in the lot's time zone, in UTC.</summary>
    private async Task<DateTime> DeadlineAsync(int lotId, DateOnly day)
    {
        var timeZoneId = await pmsContext.ParkingLots.AsNoTracking().Where(p => p.Id == lotId).Select(p => p.TimeZoneId).FirstOrDefaultAsync();
        var local = DateTime.SpecifyKind(day.ToDateTime(CloseDeadline), DateTimeKind.Unspecified);
        var deadline = TimeZoneInfo.ConvertTimeToUtc(local, ParkingText.FindTimeZone(timeZoneId ?? options.TimeZoneId));
        // A drawer opened after 23:00 (an extra close) gets an hour to close
        return deadline > Now ? deadline : Now.AddHours(1);
    }

    private async Task<string> StaffNameAsync(string userId) =>
        await pmsContext.Employees.AsNoTracking().Where(e => e.UserId == userId).Select(e => e.FullName).FirstOrDefaultAsync() ?? "A Staff member";

    /// <summary>Real-time alert to everyone working the lot (Staff consoles and the Manager); never breaks the request.</summary>
    private async Task AlertAsync(int lotId, string code, string message)
    {
        if (notifier == null) return;
        try
        {
            await notifier.OpsAlertAsync(lotId, code, message, null);
        }
        catch
        {
            // Real-time is best effort; the screens also poll
        }
    }

    private async Task<DailyCashClose> OpenDrawerAsync(int lotId, string staffUserId, Dictionary<int, int> notes)
    {
        var today = await BusinessDateAsync(lotId);
        var latest = await LatestAsync(lotId, staffUserId, today);
        if (latest is { Status: CashCloseStatus.Open }) return null;

        var drawer = new DailyCashClose
        {
            ParkingLotId = lotId,
            StaffUserId = staffUserId,
            BusinessDate = today,
            CloseNo = (latest?.CloseNo ?? 0) + 1,
            Status = CashCloseStatus.Open,
            OpenedAt = Now,
            CloseDueAt = await DeadlineAsync(lotId, today),
            FloatJson = Write(notes),
            FloatTotal = Total(notes)
        };
        pmsContext.DailyCashCloses.Add(drawer);
        await pmsContext.SaveChangesAsync();
        return drawer;
    }

    private Task<DailyCashClose> LatestAsync(int lotId, string staffUserId, DateOnly day, bool tracked = false)
    {
        var query = pmsContext.DailyCashCloses.Where(d => d.ParkingLotId == lotId && d.StaffUserId == staffUserId && d.BusinessDate == day);
        if (!tracked) query = query.AsNoTracking();
        return query.OrderByDescending(d => d.CloseNo).FirstOrDefaultAsync();
    }

    private async Task<DateOnly> BusinessDateAsync(int lotId)
    {
        var timeZoneId = await pmsContext.ParkingLots.AsNoTracking().Where(p => p.Id == lotId).Select(p => p.TimeZoneId).FirstOrDefaultAsync();
        var local = TimeZoneInfo.ConvertTimeFromUtc(Now, ParkingText.FindTimeZone(timeZoneId ?? options.TimeZoneId));
        return DateOnly.FromDateTime(local);
    }

    /// <summary>Cash fees the drawer's Staff recorded at its lot between the opening and <paramref name="until"/>.</summary>
    private async Task<(decimal Collected, int Count, Dictionary<int, int> NetNotes, decimal Untracked)> CashTakenAsync(DailyCashClose drawer, DateTime until)
    {
        var payments = await pmsContext.Payments.AsNoTracking()
            .Where(p => p.PaymentMethod == PaymentMethod.Cash && p.Status == PaymentStatus.Succeeded
                        && p.CollectedByUserId == drawer.StaffUserId && p.ParkingLotId == drawer.ParkingLotId
                        && p.PaidAt >= drawer.OpenedAt && p.PaidAt <= until)
            .Select(p => new { p.Amount, p.CashReceivedJson, p.CashChangeJson })
            .ToListAsync();

        var net = new Dictionary<int, int>();
        decimal untracked = 0;
        foreach (var payment in payments)
        {
            if (payment.CashReceivedJson == null)
            {
                untracked += payment.Amount;
                continue;
            }
            foreach (var (value, count) in Read(payment.CashReceivedJson)) net[value] = net.GetValueOrDefault(value) + count;
            foreach (var (value, count) in Read(payment.CashChangeJson)) net[value] = net.GetValueOrDefault(value) - count;
        }
        return (payments.Sum(p => p.Amount), payments.Count, net, untracked);
    }

    private async Task<CashDrawerModel> ToModelAsync(DailyCashClose drawer)
    {
        var until = drawer.ClosedAt ?? Now;
        var (collected, count, net, untracked) = await CashTakenAsync(drawer, until);
        var (topUpTotal, topUpNotes) = await ApprovedTopUpsAsync(drawer.Id);
        var floatNotes = Read(drawer.FloatJson);
        var expectedNotes = Denominations
            .Select(value => (value, count: floatNotes.GetValueOrDefault(value) + net.GetValueOrDefault(value) + topUpNotes.GetValueOrDefault(value)))
            .Where(n => n.count != 0)
            .ToDictionary(n => n.value, n => n.count);
        var requests = await pmsContext.CashTopUpRequests.AsNoTracking()
            .Where(r => r.DailyCashCloseId == drawer.Id)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        var names = await pmsContext.Employees.AsNoTracking()
            .Where(e => e.UserId == drawer.StaffUserId || e.UserId == drawer.ConfirmedBy)
            .Select(e => new { e.UserId, e.FullName })
            .ToListAsync();

        return new CashDrawerModel
        {
            Id = drawer.Id,
            ParkingLotId = drawer.ParkingLotId,
            StaffUserId = drawer.StaffUserId,
            StaffName = names.FirstOrDefault(n => n.UserId == drawer.StaffUserId)?.FullName,
            BusinessDate = drawer.BusinessDate,
            CloseNo = drawer.CloseNo,
            Status = drawer.Status,
            OpenedAt = Utc(drawer.OpenedAt),
            CloseDueAt = drawer.CloseDueAt is { } due ? Utc(due) : null,
            IsOverdue = drawer.Status == CashCloseStatus.Open && drawer.CloseDueAt != null && drawer.CloseDueAt <= Now,
            ClosedLate = drawer.ClosedAt != null && drawer.CloseDueAt != null && drawer.ClosedAt > drawer.CloseDueAt,
            Float = floatNotes,
            FloatTotal = drawer.FloatTotal,
            CashCollected = collected,
            CashPayments = count,
            ExpectedNotes = expectedNotes,
            // Frozen at the close; live while the drawer is open
            ExpectedTotal = drawer.Status == CashCloseStatus.Open
                ? drawer.FloatTotal + collected + topUpTotal
                : drawer.ExpectedTotal ?? drawer.FloatTotal + collected + topUpTotal,
            TopUpTotal = topUpTotal,
            TopUps = await ToTopUpModelsAsync(requests),
            UntrackedCash = untracked,
            Counted = drawer.CountedJson == null ? null : Read(drawer.CountedJson),
            CountedTotal = drawer.CountedTotal,
            Difference = drawer.Difference,
            DifferenceReason = drawer.DifferenceReason,
            ClosedAt = drawer.ClosedAt is { } closed ? Utc(closed) : null,
            ConfirmedBy = drawer.ConfirmedBy,
            ConfirmedByName = names.FirstOrDefault(n => n.UserId == drawer.ConfirmedBy)?.FullName,
            ConfirmedAt = drawer.ConfirmedAt is { } confirmed ? Utc(confirmed) : null,
            ConfirmNote = drawer.ConfirmNote,
            DifferenceThreshold = DifferenceThreshold,
            Denominations = Denominations
        };
    }

    /// <summary>Keeps known banknote values with a count above zero; refuses unknown values and impossible counts.</summary>
    public static Dictionary<int, int> CleanNotes(IReadOnlyDictionary<int, int> notes, string what)
    {
        var clean = new Dictionary<int, int>();
        foreach (var (value, count) in notes ?? new Dictionary<int, int>())
        {
            if (!Denominations.Contains(value))
                throw new BadRequestException($"{value:#,0} VND is not a banknote the drawer counts.");
            if (count is < 0 or > MaxNotesPerValue)
                throw new BadRequestException($"The {what} has an impossible number of {value:#,0} VND notes.");
            if (count > 0) clean[value] = count;
        }
        return clean;
    }

    public static decimal Total(IReadOnlyDictionary<int, int> notes) => notes.Sum(n => (decimal)n.Key * n.Value);

    public static string Write(IReadOnlyDictionary<int, int> notes) => JsonSerializer.Serialize(notes);

    private static Dictionary<int, int> Read(string json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<Dictionary<int, int>>(json) ?? [];

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
