using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using PMS.Application.Exceptions;
using PMS.Application.Models.WorkScheduling;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Operations;
public class PayrollCalculatorTests
{
    // Explicit arithmetic fixtures only. These figures are not production defaults.
    public static PayrollProfile StaffProfile(DateOnly? effective = null) => new() {
        Id = 1, UserId = "staff", Role = "Staff", EffectiveFrom = effective ?? new(2026, 1, 1), HourlyRate = 100,
        NightStart = new(22, 0), NightEnd = new(6, 0), NightFactor = 1.5m, HolidayFactor = 2, OvertimeFactor = 1.25m,
        Currency = "VND", MoneyDecimals = 2, RoundingMode = "AwayFromZero", Revision = 1
    };
    public static PayrollPerson Staff = new("staff", 1, "Staff arithmetic fixture", "Staff");
    public static PayrollAssignmentEvidence Assignment(DateTime start, DateTime end, string kind = "Regular", int id = 10) =>
        new(id, 1, "staff", Staff.Name, "Frozen night", DateOnly.FromDateTime(start.AddHours(7)), "SE Asia Standard Time", start, end, 2, start, end,
            false, false, "Completed", [new(start, end, kind)], null, false, "[]", "[]");
    public static PayrollSourceBundle Source(IReadOnlyList<PayrollAssignmentEvidence> assignments,
        IReadOnlyList<PayrollHoliday> holidays = null, IReadOnlyList<ManagerWorkDayModel> days = null) => new(1, "frozen-hash",
        new PayrollEvidence(1, 1, 1, "Arithmetic lot", "SE Asia Standard Time", new(2026, 10, 1), new(2026, 10, 31),
            new DateTime(2026, 9, 30, 17, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 31, 17, 0, 0, DateTimeKind.Utc),
            new[] { Staff }.Concat((days ?? []).Select(d => new PayrollPerson(d.ManagerUserId, null, d.ManagerName, "Manager"))).DistinctBy(p => p.UserId).ToList(), assignments, days ?? [], holidays ?? [], []));
    static DateTime U(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, DateTimeKind.Utc);
    static PayrollCalculation Calc(PayrollSourceBundle source, params PayrollProfile[] profiles) => PayrollCalculator.Calculate(new(2026, 10, 1), Staff, [source], profiles.Length == 0 ? [StaffProfile()] : profiles, []);

    [Fact] public void Ordinary_Day_Eight_Hours_Times_100_Is_800()
    {
        var result = Calc(Source([Assignment(U(2, 1), U(2, 9))]));
        result.Total.Should().Be(800); result.Lines.Sum(x => x.Quantity).Should().Be(8);
    }
    [Fact] public void Night_And_Holiday_Use_Maximum_Not_Product()
    {
        // Local 22:00–24:00 on a holiday: 2 × 100 × max(1.5,2) = 400, not 600.
        var result = Calc(Source([Assignment(U(2, 15), U(2, 17))], holidays: [new(new(2026, 10, 2), "Fixture holiday")]));
        result.Total.Should().Be(400); result.Lines.Single().Factor.Should().Be(2);
    }
    [Fact] public void Overtime_Night_Holiday_Uses_Max_Premium_Then_Overtime()
    {
        // 2 × 100 × max(1.5,2) × 1.25 = 500.
        var result = Calc(Source([Assignment(U(2, 15), U(2, 17), "Overtime")], holidays: [new(new(2026, 10, 2), "Fixture holiday")]));
        result.Total.Should().Be(500); result.Lines.Single().Factor.Should().Be(2.5m);
    }
    [Fact] public void Midnight_Splits_Holiday_And_Night_Hours_Exactly()
    {
        // Local Oct2 22:00–Oct3 06:00: holiday 2h×100×2 + night 6h×100×1.5 = 1300.
        var result = Calc(Source([Assignment(U(2, 15), U(2, 23))], holidays: [new(new(2026, 10, 2), "Fixture holiday")]));
        result.Total.Should().Be(1300); result.Lines.Should().HaveCount(2);
    }
    [Fact] public void Effective_Rate_Changes_At_Midnight_Without_Changing_Source_Shift()
    {
        var first = StaffProfile(); var second = StaffProfile(new(2026, 10, 3)); second.Id = 2; second.HourlyRate = 200;
        // 2h×100×1.5 + 6h×200×1.5 = 2100.
        var result = Calc(Source([Assignment(U(2, 15), U(2, 23))]), first, second);
        result.Total.Should().Be(2100); result.Lines.Select(l => l.ProfileId).Should().Equal(1, 2);
    }
    [Fact] public void Paid_Leave_Uses_Planned_Hours_And_Ordinary_Rate_Without_Holiday_Premium()
    {
        var row = Assignment(U(2, 15), U(2, 23)) with { Intervals = [], IsPaidLeave = true, LeaveRequestId = 42, AttendanceStatus = "ApprovedLeave", ActualStart = null, ActualEnd = null };
        var result = Calc(Source([row], holidays: [new(new(2026, 10, 2), "Fixture holiday")]));
        result.Total.Should().Be(800); result.Lines.Should().OnlyContain(l => l.Factor == 1 && l.Kind == "PaidLeave" && l.LeaveRequestId == 42);
    }
    [Fact] public void Late_Has_No_Penalty_And_Absent_Has_No_Extra_Debit()
    {
        var row = Assignment(U(2, 1, 20), U(2, 9)) with { Late = true };
        var absent = Assignment(U(3, 1), U(3, 9), id: 11) with { Intervals = [], AttendanceStatus = "Absent", ActualStart = null, ActualEnd = null };
        var result = Calc(Source([row, absent]));
        result.Total.Should().Be(766.67m); result.Lines.Should().ContainSingle(l => l.Kind == "Absent" && l.Amount == 0);
        result.Lines.Should().NotContain(l => l.Quantity < 0);
    }
    [Fact] public void Manager_Monthly_Base_Absent_Allowance_Bonus_And_Adjustment_Are_Explainable()
    {
        var person = new PayrollPerson("manager", 2, "Manager fixture", "Manager");
        var p = new PayrollProfile { Id = 2, UserId = "manager", Role = "Manager", EffectiveFrom = new(2026, 1, 1), MonthlyRate = 2600, StandardWorkDays = 26,
            Currency = "VND", MoneyDecimals = 2, RoundingMode = "AwayFromZero" };
        var source = Source([], days: [new(1, 1, "manager", person.Name, new(2026, 10, 2), "Absent", "Verified", 1), new(2, 1, "manager", person.Name, new(2026, 10, 3), "PaidLeave", "Approved", 1)]);
        var adjustment = new PayrollAdjustment { Id = 1, UserId = "manager", Role = "Manager", PayMonth = new(2026, 10, 1), Kind = "Bonus", Quantity = 1, UnitRate = 50, Reason = "Fixture bonus" };
        // 2600 − 2600/26 × 1 + 50 = 2550. Approved leave has no deduction.
        var result = PayrollCalculator.Calculate(new(2026, 10, 1), person, [source], [p], [adjustment]);
        result.Total.Should().Be(2550); result.Lines.Should().ContainSingle(l => l.Kind == "ManagerAbsence" && l.Amount == -100);
    }
    [Fact] public void Missing_Factors_Are_Reported_And_Not_Defaulted()
    {
        var p = StaffProfile(); p.NightFactor = null;
        FluentActions.Invoking(() => Calc(Source([Assignment(U(2, 15), U(2, 17))]), p)).Should().Throw<BadRequestException>().WithMessage("*night factor*");
    }
    [Fact] public void Fractional_Line_Rounding_Uses_The_Explicit_Mode()
    {
        var p = StaffProfile(); p.HourlyRate = 1.005m;
        p.RoundingMode = "ToEven"; Calc(Source([Assignment(U(2, 1), U(2, 2))]), p).Total.Should().Be(1.00m);
        p.RoundingMode = "AwayFromZero"; Calc(Source([Assignment(U(2, 1), U(2, 2))]), p).Total.Should().Be(1.01m);
    }
    [Fact] public void Source_Hours_Are_Clipped_To_The_Payroll_Month()
    {
        // Local Oct31 22:00–Nov1 06:00: October pays 2h×100×1.5 = 300.
        var row = Assignment(U(31, 15), U(31, 23));
        Calc(Source([row])).Total.Should().Be(300);
    }
    [Fact] public void Manager_Salary_Uses_The_Profile_On_The_First_Day_And_Deducts_Absent_And_Unpaid_Days()
    {
        var manager = new PayrollPerson("manager", 2, "Manager fixture", "Manager");
        var first = new PayrollProfile { Id = 1, UserId = "manager", Role = "Manager", EffectiveFrom = new(2026, 1, 1), MonthlyRate = 2600, StandardWorkDays = 26, Currency = "VND", MoneyDecimals = 2, RoundingMode = "AwayFromZero" };
        var second = new PayrollProfile { Id = 2, UserId = "manager", Role = "Manager", EffectiveFrom = new(2026, 10, 16), MonthlyRate = 5200, StandardWorkDays = 26, Currency = "VND", MoneyDecimals = 2, RoundingMode = "AwayFromZero" };
        var source = Source([], days: [new(1, 1, "manager", manager.Name, new(2026, 10, 2), "Work", "Verified", 1),
            new(2, 1, "manager", manager.Name, new(2026, 10, 20), "Absent", "Verified", 1), new(3, 1, "manager", manager.Name, new(2026, 10, 21), "UnpaidLeave", "Verified", 1)]);
        var result = PayrollCalculator.Calculate(new(2026, 10, 1), manager, [source], [first, second], []);
        // The October 1 profile applies to the whole month: 2600 − 2 × 2600/26. The October 16 raise starts next month.
        result.Total.Should().Be(2400);
        result.Lines.Should().ContainSingle(l => l.Kind == "MonthlyBase" && l.Quantity == 1 && l.ProfileId == 1);
        result.Lines.Count(l => l.Kind == "ManagerAbsence").Should().Be(2);
    }
    [Fact] public void Former_Manager_Receiving_Only_A_Correction_Does_Not_Get_Another_Monthly_Base()
    {
        var manager = new PayrollPerson("former-manager", 2, "Former Manager", "Manager");
        var p = new PayrollProfile { Id = 1, UserId = manager.UserId, Role = "Manager", EffectiveFrom = new(2026, 1, 1), MonthlyRate = 2600, Currency = "VND", MoneyDecimals = 2, RoundingMode = "AwayFromZero" };
        var a = new PayrollAdjustment { Id = 1, UserId = manager.UserId, Role = "Manager", PayMonth = new(2026, 10, 1), Kind = "Adjustment", Quantity = 1, UnitRate = 50, SourceSlipId = 1, Reason = "Owed correction" };
        var result = PayrollCalculator.Calculate(new(2026, 10, 1), manager, [Source([])], [p], [a]);
        result.Total.Should().Be(50); result.Lines.Should().NotContain(l => l.Kind == "MonthlyBase");
    }
}
