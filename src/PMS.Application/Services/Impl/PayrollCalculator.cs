using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.WorkScheduling;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>Pure calculation over sealed attendance and effective salary profiles. No live schedule queries.</summary>
public static class PayrollCalculator
{
    public static PayrollCalculation Calculate(DateOnly month, PayrollPerson person, IReadOnlyList<PayrollSourceBundle> sources,
        IReadOnlyList<PayrollProfile> profileRows, IReadOnlyList<PayrollAdjustment> adjustments)
    {
        if (month.Day != 1 || month.Year < 1900 || month.Year >= 9999 || person.Role is not ("Staff" or "Manager"))
            throw new BadRequestException("Choose a calendar month and a Staff or Manager employee.");
        var next = month.AddMonths(1); var last = next.AddDays(-1);
        var profiles = profileRows.Where(x => x.UserId == person.UserId && x.Role == person.Role && x.EffectiveFrom <= last).OrderBy(x => x.EffectiveFrom).ToList();
        var lines = new List<PayrollLine>(); var used = new HashSet<int>(); string currency = null; int? decimals = null; string rounding = null;
        PayrollProfile Profile(DateOnly date)
        {
            var p = profiles.LastOrDefault(p => p.EffectiveFrom <= date) ?? throw new BadRequestException($"{person.Name}: no salary profile effective on {date:yyyy-MM-dd}.");
            if (p.Currency?.Length != 3 || p.MoneyDecimals is null or < 0 or > 4 || p.RoundingMode is not ("ToEven" or "AwayFromZero"))
                throw new BadRequestException($"{person.Name}, profile #{p.Id}: configure currency, decimal places and rounding mode.");
            if (currency != null && (currency != p.Currency || decimals != p.MoneyDecimals || rounding != p.RoundingMode))
                throw new BadRequestException("Profiles within one payslip must use the same currency and rounding rules.");
            currency = p.Currency; decimals = p.MoneyDecimals; rounding = p.RoundingMode; used.Add(p.Id); return p;
        }
        void Add(PayrollLine line, PayrollProfile p, object source)
        {
            var amount = line.Quantity * line.UnitRate * line.Factor;
            if (Math.Abs(amount) > 99999999999999m) throw new BadRequestException("A payroll line exceeds the supported monetary range.");
            line.Quantity = decimal.Round(line.Quantity, 16, MidpointRounding.ToEven);
            line.ProfileId = p.Id; line.Ordinal = lines.Count + 1;
            line.Amount = decimal.Round(amount, decimals.Value, rounding == "ToEven" ? MidpointRounding.ToEven : MidpointRounding.AwayFromZero);
            line.SourceJson = JsonSerializer.Serialize(new { Evidence = source, Profile = p, CalculationPolicy = "SM-30:max(night,holiday); overtime=max(night,holiday)*overtime; paid-leave=planned-hours*base-rate; v1" });
            lines.Add(line);
        }
        static decimal Need(decimal? value, string field, PayrollProfile p, bool positive = false)
        {
            if (!value.HasValue || (positive ? value.Value <= 0 : value.Value < 0)) throw new BadRequestException($"Profile #{p.Id}: configure {field} explicitly.");
            return value.Value;
        }
        static DateTime Boundary(DateOnly date, TimeOnly time, TimeZoneInfo zone)
        {
            var local = date.ToDateTime(time);
            if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local)) throw new BadRequestException("Ambiguous daylight-saving boundary requires an explicit payroll correction; no time is guessed.");
            return TimeZoneInfo.ConvertTimeToUtc(local, zone);
        }
        foreach (var source in sources.OrderBy(x => x.Evidence.StartsAt)) {
            var evidence = source.Evidence; var zone = ParkingText.FindTimeZone(evidence.TimeZoneId);
            var monthStart = Boundary(month, TimeOnly.MinValue, zone); var monthEnd = Boundary(next, TimeOnly.MinValue, zone);
            var holidays = evidence.Holidays.Select(h => h.Date).ToHashSet();
            foreach (var a in evidence.Assignments.Where(a => a.UserId == person.UserId && person.Role == "Staff")) {
                var intervals = a.Intervals.ToList();
                if (a.IsPaidLeave) intervals.Add(new(a.PlannedStart, a.PlannedEnd, "PaidLeave"));
                foreach (var interval in intervals) {
                    var start = new[] { interval.Start, evidence.StartsAt, monthStart }.Max();
                    var end = new[] { interval.End, evidence.EndsAt, monthEnd }.Min();
                    while (start < end) {
                        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(start, DateTimeKind.Utc), zone);
                        var date = DateOnly.FromDateTime(local); var p = Profile(date); var rate = Need(p.HourlyRate, "hourly rate", p, true);
                        var cut = new[] { end, Boundary(date.AddDays(1), TimeOnly.MinValue, zone) }.Min();
                        var night = false; var holiday = holidays.Contains(date); var factor = 1m;
                        if (interval.Kind != "PaidLeave") {
                            if (!p.NightStart.HasValue || !p.NightEnd.HasValue || p.NightStart == p.NightEnd)
                                throw new BadRequestException($"Profile #{p.Id}: configure a distinct night start/end window.");
                            var nf = Need(p.NightFactor, "night factor", p, true); var hf = Need(p.HolidayFactor, "holiday factor", p, true);
                            if (nf < 1 || hf < 1) throw new BadRequestException("Night and holiday factors cannot reduce ordinary pay.");
                            var time = TimeOnly.FromDateTime(local);
                            night = p.NightStart < p.NightEnd ? time >= p.NightStart && time < p.NightEnd : time >= p.NightStart || time < p.NightEnd;
                            foreach (var boundary in new[] { Boundary(date, p.NightStart.Value, zone), Boundary(date, p.NightEnd.Value, zone) })
                                if (boundary > start && boundary < cut) cut = boundary;
                            factor = Math.Max(night ? nf : 1m, holiday ? hf : 1m);
                            if (interval.Kind == "Overtime") {
                                var ot = Need(p.OvertimeFactor, "overtime factor", p, true);
                                if (ot < 1) throw new BadRequestException("An overtime factor cannot reduce ordinary pay.");
                                factor *= ot;
                            }
                        }
                        Add(new PayrollLine { Kind = interval.Kind, Description = $"{a.ShiftName}: {interval.Kind}{(night ? " · night" : "")}{(holiday && interval.Kind != "PaidLeave" ? " · holiday" : "")}",
                            SourcePeriodLotId = evidence.PeriodLotId, AssignmentId = a.AssignmentId, AttendanceRevision = a.AttendanceRevision,
                            LeaveRequestId = interval.Kind == "PaidLeave" ? a.LeaveRequestId : null, WorkDate = date, StartsAt = start, EndsAt = cut,
                            Unit = "Hour", Quantity = (decimal)(cut - start).Ticks / TimeSpan.TicksPerHour, UnitRate = rate, Factor = factor }, p,
                            new { Assignment = a, SourceId = source.Id, SourceHash = source.Hash, Night = night, Holiday = holiday, interval.Kind });
                        start = cut;
                    }
                }
                if (a.AttendanceStatus == "Absent" && a.WorkDate >= month && a.WorkDate < next) {
                    var p = Profile(a.WorkDate);
                    Add(new PayrollLine { Kind = "Absent", Description = $"{a.ShiftName}: no approved worked hours; no additional penalty", SourcePeriodLotId = evidence.PeriodLotId,
                        AssignmentId = a.AssignmentId, AttendanceRevision = a.AttendanceRevision, WorkDate = a.WorkDate, Unit = "Hour", Quantity = 0, UnitRate = Need(p.HourlyRate, "hourly rate", p, true), Factor = 1 }, p,
                        new { Assignment = a, SourceId = source.Id, SourceHash = source.Hash });
                }
                if (a.AttendanceStatus == "ApprovedLeave" && !a.IsPaidLeave && a.WorkDate >= month && a.WorkDate < next) {
                    var p = Profile(a.WorkDate);
                    Add(new PayrollLine { Kind = "UnpaidLeave", Description = $"{a.ShiftName}: approved unpaid leave; no additional penalty", SourcePeriodLotId = evidence.PeriodLotId,
                        AssignmentId = a.AssignmentId, AttendanceRevision = a.AttendanceRevision, LeaveRequestId = a.LeaveRequestId, WorkDate = a.WorkDate,
                        Unit = "Hour", Quantity = 0, UnitRate = Need(p.HourlyRate, "hourly rate", p, true), Factor = 1 }, p,
                        new { Assignment = a, SourceId = source.Id, SourceHash = source.Hash });
                }
            }
        }
        if (person.Role == "Manager" && sources.Any(s => s.Evidence.People.Any(p => p.UserId == person.UserId && p.Role == "Manager"))) {
            // Fixed monthly salary: the profile effective on the first day of the month applies to the whole month.
            var p = Profile(month);
            var monthly = Need(p.MonthlyRate, "monthly salary", p, true);
            Add(new PayrollLine { Kind = "MonthlyBase", Description = "Manager monthly salary",
                SourcePeriodLotId = sources.First().Evidence.PeriodLotId, WorkDate = month, Unit = "Month", Quantity = 1,
                UnitRate = monthly, Factor = 1 }, p,
                new { Manager = person, FromDate = month, ToDate = last, Sources = sources.Select(x => new { x.Id, x.Hash }).ToList() });
            foreach (var group in sources.SelectMany(s => s.Evidence.ManagerDays.Select(d => (Source: s, Day: d)))
                .Where(x => x.Day.ManagerUserId == person.UserId && x.Day.WorkDate >= month && x.Day.WorkDate < next).GroupBy(x => x.Day.WorkDate)) {
                if (group.Select(x => x.Day.Kind).Distinct().Count() > 1) throw new BadRequestException("Conflicting locked Manager workday evidence.");
                var item = group.First(); var day = item.Day;
                // Each locked Absent or unpaid-leave day deducts monthly salary / standard workdays; other days keep the salary.
                var deduct = day.Kind is "Absent" or "UnpaidLeave";
                var rate = deduct ? monthly / Need(p.StandardWorkDays, "standard workdays", p, true) : 0;
                Add(new PayrollLine { Kind = deduct ? "ManagerAbsence" : "ManagerDay", Description = $"Admin recorded {day.Kind}" + (deduct ? " · daily salary deduction, no additional penalty" : " · monthly salary retained"),
                    SourcePeriodLotId = item.Source.Evidence.PeriodLotId, ManagerDayId = day.Id, WorkDate = day.WorkDate, Unit = "Day", Quantity = deduct ? -1 : 0,
                    UnitRate = rate, Factor = 1 }, p, new { ManagerDay = day, SourceId = item.Source.Id, SourceHash = item.Source.Hash });
            }
        }
        foreach (var adjustment in adjustments.Where(a => a.UserId == person.UserId && a.Role == person.Role && a.PayMonth == month).OrderBy(a => a.Id)) {
            var p = Profile(month);
            Add(new PayrollLine { Kind = adjustment.Kind, Description = adjustment.Reason, SourcePeriodLotId = sources.First().Evidence.PeriodLotId,
                AdjustmentId = adjustment.Id, WorkDate = month, Unit = adjustment.SourceCarryId.HasValue ? "Hour" : "Entry",
                Quantity = adjustment.Quantity, UnitRate = adjustment.UnitRate, Factor = 1 }, p, new { Adjustment = adjustment });
        }
        if (lines.Count == 0) { _ = Profile(month); }
        var basis = JsonSerializer.Serialize(new { Policy = "payroll-v1", Month = month, Person = person,
            Sources = sources.OrderBy(s => s.Id).Select(s => new { s.Id, s.Hash }).ToList(),
            Profiles = profiles.Where(p => used.Contains(p.Id)).ToList(), Adjustments = adjustments.Where(a => a.UserId == person.UserId && a.Role == person.Role && a.PayMonth == month).OrderBy(a => a.Id).ToList() });
        return new(person.UserId, person.EmployeeId, person.Name, person.Role, currency, lines.Sum(l => l.Amount), lines, basis,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basis))), sources.Select(x => x.Id).ToList());
    }
}
