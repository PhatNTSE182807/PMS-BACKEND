# Payroll

Fixed monthly pay, paid by an Admin at the start of the next month (team decision, 2026-11-19; replaced
the earlier salary profiles, attendance periods and payroll sources). Attendance and leave come from
the duty board ([WORK_SCHEDULING.md](WORK_SCHEDULING.md)).

## Amounts (`Payroll` settings, VND)

| Who | Pay |
| --- | --- |
| Full-time Staff | 5,800,000 a month |
| Backup Staff | 30,000 per hour worked |
| Manager | 10,000,000 a month (no shifts, no deductions) |

Full-time Staff:

- minus `monthly / 26` (223,077) per day **absent** (a day with an `Absent` shift) and per day of
  **unpaid leave** (a leave day with a shift beyond the yearly paid allowance);
- plus 30,000 per hour worked on **extra shifts**: shifts beyond the person's weekly pattern on that day
  (a second shift, or a shift on their day off).

Hours worked = check-in to check-out inside the shift's hours, to the minute. Each line is rounded to
the dong. Late arrival has no penalty.

First month: when someone joined after the 1st, the base salary is
`monthly × days from the hire date to the month end / days in the month` (e.g. hired 15/11:
5,800,000 × 16/30). The month someone leaves is paid in full.

## Flow

1. On the 1st (hourly `DutyJobs` run, Vietnam time) last month's payslips are calculated (`Unpaid`)
   and every Admin gets a `PayrollReady` notice.
2. The Admin opens **Payroll**, checks the table (attendance can still be corrected; unpaid payslips are
   recalculated on every read) and presses **Pay all**: every unpaid payslip of the month becomes `Paid`
   with the time, and each person gets a `PayrollPaid` notice. A month can only be paid once it ended.
3. A paid payslip never changes; corrections are `PayAdjustments` (amount ± and reason) added to a month
   that is not paid yet.

Staff and Managers see the payslips they received (**My payslips**), line by line.

**Simulate payday** (demo, `Payroll:AllowSimulation`): a Staff member or Manager creates a paid payslip
for themselves only (the server takes the person from the token) with the base salary and/or a number
of overtime hours (0–200, steps of 0.5). It is marked `IsSimulated`, listed in the Admin report with a
"Simulated" label and its own total, never replaces the real payslip and is ignored by Pay all.

## Data

`PaySlips`: person, lot, month, kind (`FullTime` / `Backup` / `Manager`), base, deductions, extra,
adjustments, net, status, `IsSimulated`, paid time and the lines (JSON). One real payslip per person
and month (filtered unique index). `PayAdjustments`: person, month, amount, reason.

## API

| Method | Route | Who |
| --- | --- | --- |
| GET | `/api/payroll/mine` | Staff, Manager |
| GET | `/api/payroll/simulation` | Staff, Manager (is the button on) |
| POST | `/api/payroll/simulate` | Staff, Manager: `{ includeBase, overtimeHours }` |
| GET | `/api/admin/payroll?month=yyyy-MM-01&lotId&kind&status&includeSimulated` | Admin; a running month shows estimates |
| POST | `/api/admin/payroll/pay-all` | Admin: `{ month }` |
| GET | `/api/admin/payroll/slips/{id}` | Admin |
| POST | `/api/admin/payroll/adjustments` | Admin: `{ employeeId, month, amount, reason }` |
