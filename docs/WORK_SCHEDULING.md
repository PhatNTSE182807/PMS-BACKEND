# Duty board, shift attendance and leave

Pay is documented in [PAYROLL.md](PAYROLL.md). This module replaced the earlier rosters, shift
templates, rotation, open shifts / swaps / standby and attendance periods (team decision,
2026-11-19). Their tables are left in the database and no longer used; the legacy `Shift*` tables
and the cash drawer are unrelated and unchanged.

## Shifts

Every lot has **4 fixed shifts a day**, one Staff member each:

| Slot | Name | Hours (lot time zone) |
| --- | --- | --- |
| 0 | Night | 00:00–06:00 |
| 1 | Morning | 06:00–12:00 |
| 2 | Afternoon | 12:00–18:00 |
| 3 | Evening | 18:00–24:00 |

Hours are constants (`DutySlots`); there is no shift template.

## Weekly pattern and one-day changes

- `DutyPatterns` (lot, `EffectiveFrom`) + `DutyPatternCells` (weekday 1–7, slot, employee): who works
  each shift of the week. A pattern repeats every week until a later version starts.
- `DutyOverrides` (lot, date, slot, employee or null): a change for one date only. An override made by
  an approved leave also stores `LeaveRequestId` (the person on leave).
- The board of a date = the pattern in force on that date, then its override.

The Manager clicks a shift on the board and picks a person (or leaves it empty):

- **Emergency replacement** (this day only): backup Staff only (or back to the pattern person), also
  for a shift in progress: someone working is taken off and their shift ends at that moment. Writes an
  override (removed again if it equals the pattern).
- **Change the weekly pattern** (this and following weeks): full-time Staff only, shifts that have not
  begun. Starts a new pattern version on that date (copying the one in force) and applies the same change
  to later versions; drops a hand-made override on that date.

Rules when putting someone on a shift (`DutyScheduleService.Check`):

- active Staff of the lot; not on pending or approved leave that day;
- at most 2 shifts (12 hours) on one day, and never 3 shifts in a row across midnight;
- a shift next to another is allowed but flagged "Back-to-back shift";
- backup Staff only on one-day changes (never in the pattern);
- a shift that is over cannot change; a shift in progress only takes an emergency replacement.

A Manager cannot deactivate (or remove) a Staff member who is on shift.

## Employment

`Employees.EmploymentType`: `FullTime` (monthly salary, in the weekly pattern) or `Backup` (paid by the
hour, put on single shifts). The Manager chooses it when creating or editing a Staff account.

## Attendance and the POS lock

`DutyShiftAttendances` holds one row per person and shift: `Working`, `Completed` or `Absent`.

- A Staff member starts their shift from 30 minutes before it begins until it ends
  (`POST /api/staff/duty/shifts/start`). Starting after the start time (or after being put on a shift
  that had begun) shows **Late**; it is only a label, nothing is deducted.
- A started shift cannot be ended early: there is no end action. The minutely `DutyJobs` run ends it at
  the scheduled end, and marks a shift **absent** at its end when nobody started it (skipped while the
  person has leave waiting, and when the board was set up after the shift began). The Staff and the
  Manager are told.
- The Manager cannot edit attendance (removed 2026-11-20).
- **POS lock**: `[RequireOnShift]` refuses non-GET Staff actions of the gate, checkout, cash, booked-bay
  cases, gate requests and vehicle reviews (403 "You are not on shift") unless the Staff is checked in
  to a shift that has not ended. Handing in the cash drawer is still allowed after the shift
  (`[AllowOffShift]`). `WorkSchedule:RequireShiftForPos = false` turns the lock off (integration tests do).

The frontend sends Staff to `/staff` after sign-in: the POS when on shift (or the shift is open to
start), otherwise **Off duty** with the next shift. POS pages show a locked screen off shift.

## Leave

`DutyLeaveRequests` (Pending → Approved / Rejected, or Cancelled by the Staff) and `DutyLeaveDays`.

- A Staff member asks for a date range with a reason. At most **7 calendar days a month** over all their
  pending and approved requests (a request across two months counts in each month); no overlapping
  requests; no leave in the month they joined unless they joined on the 1st.
- **12 paid days a year** by default (`WorkLeaveBalances`, set by an Admin on the Staff accounts page);
  only days on which the person has a shift use them, then the days are unpaid.
- The Manager sees the shifts the leave covers and, when approving, picks who takes each one (eligible
  Staff, full-time first, then backup) or leaves it empty (red on the board). Rejecting needs a reason.

## API

Manager, `/api/manager/lots/{lotId}/duty`:

| Method | Suffix | Purpose |
| --- | --- | --- |
| GET | `/week?start=yyyy-MM-dd` | One week of the board and the lot's Staff |
| GET | `/candidates?date&slot&excludeEmployeeId` | Who can take a shift, with the reason someone cannot |
| PUT | `/cells` | `{ date, slot, employeeId, scope: Day / Following, reason }` |
| GET | `/leave-requests` | Waiting requests first, with their shifts |
| POST | `/leave-requests/{id}/approve` | `{ replacements: [{ date, slot, employeeId }] }` |
| POST | `/leave-requests/{id}/reject` | `{ reason }` |

Staff, `/api/staff/duty`: `GET status`, `GET week`, `POST shifts/start`, `GET leave`,
`GET leave/preview?from&to`, `POST leave`, `POST leave/{id}/cancel`.

Admin: `GET /api/admin/duty/leave-balances?year`, `PUT /api/admin/duty/leave-balances/{employeeId}/{year}`.

The lot's ops channel gets a `DutyScheduleChanged` refresh signal (no names or times); people get bell
notices (`DutyShiftChanged`, `DutyAbsent`, `DutyAttendanceChanged`, `DutyLeaveRequested`, `DutyLeaveDecided`).

## Settings (`WorkSchedule`)

`RequireShiftForPos` (true), `EarlyStartMinutes` (30),
`MaxLeaveDaysPerMonth` (7), `AnnualPaidLeaveDays` (12).
