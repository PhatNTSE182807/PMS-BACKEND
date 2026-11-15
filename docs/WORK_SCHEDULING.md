# Manual work scheduling

This module implements shift templates and manual duty rosters. Cash drawers, the legacy
`Shift`, `ShiftTemplate`, `ShiftSchedule` and `ShiftCashCount` tables remain independent and unchanged.
Attendance is integrated into these same assignments and calendar. There is no automatic
rotation, payroll or restriction of POS operations by shift.

## Storage and migration

`AutomatedMigration.EnsureWorkSchedulingTablesAsync` runs at startup after the existing PMS and
notification tables. Its guarded SQL adds five new tables: `WorkShiftTemplates`, `DutyRosters`,
`DutyRosterShifts`, `DutyAssignments`, `DutyRosterChangeLogs`. Repeating it preserves existing rows.
Foreign keys restrict deletion; removing a dated shift or assignment marks its rows cancelled.

A dated shift snapshots the template name, start/end times, staffing minima and lot time zone.
Changing or deactivating a template never changes existing dated shifts, including drafts.
Editing a dated shift is an explicit action that applies the selected template's current values
and revalidates all existing assignments. A date denotes the shift's start day; an earlier end
time means the following day. Equal times are rejected. Instants are stored in UTC and displayed
in the saved lot time zone. Ambiguous or invalid daylight-saving boundaries are rejected.

## Permissions and validation

Only a Manager can create/read/edit templates and rosters in lots whose `ManagerUserId` matches
the authenticated user. Admin is not implicitly a scheduler. Staff can only read their own
assignments in published rosters, while still assigned to that lot. Backend checks every route,
including nested roster, shift and assignment identifiers.

Assignment requires both an active Employee in the lot and an active Identity account with role
Staff. Publication checks these conditions again. The current `Employees.ParkingLotId` membership
model is preserved; this phase does not introduce multiple-lot membership.

Overlap checks use half-open actual time intervals `[start, end)`, across positions, adjacent days
and all rosters, including drafts. Draft assignments therefore reserve a Staff member's time;
remove them to release it. Consecutive shifts sharing only an endpoint are allowed in this phase.
Minimum rest and maximum weekly hours are not implemented because their parameters remain unagreed.

Rosters cover at most 93 start dates; each staffing minimum is between 0 and 100. A roster can
publish with zero assigned Staff as long as it contains at least one dated shift; all unfilled
positions are returned as warnings. Dates without dated shifts have no staffing requirement.

Every mutation requires the latest revision. Stale revisions return HTTP 409. On SQL Server,
mutations use a PmsDbContext transaction and a transaction-owned application lock shared by API
processes. This serializes scheduling writers and prevents concurrent overlap checks from both
succeeding. The identity middleware's separate transaction is not relied on for schedule writes.

## Publication and notifications

Publication is one-way from Draft to Published. Subsequent edits retain Published status, require
a reason, record before/after snapshots, revision, actor and timestamp, and notify only Staff
whose assignments changed, including removed/replaced Staff. Initial publication notifies all
assigned Staff. A template edit alone does not generate roster notifications.

Notifications and history are saved in the same transaction as the roster change. Hangfire pushes
committed messages through the existing SignalR notification channel. If enqueue or delivery fails,
the durable notification is still available; the bell refreshes every 30 seconds and schedules
every 10 seconds while their pages are active. Notifications do not send email or SMS.

## Routes and screens

Manager screen: `/management/work-schedules`; Staff screen: `/staff/work-schedule`.
The feature uses the live API rather than adding a second mock scheduling implementation.

Manager API prefix: `/api/manager/lots/{lotId}/work-scheduling`.

| Method | Suffix | Purpose |
| --- | --- | --- |
| GET / POST | `/templates` | List / create templates |
| PUT | `/templates/{id}` | Edit / deactivate a template with its revision |
| GET | `/staff` | Active eligible Staff and Employee IDs |
| GET / POST | `/rosters` | List the latest 100 rosters / create a draft |
| POST | `/rosters/{id}/shifts` | Add a dated shift, including an unfilled one |
| PUT | `/rosters/{id}/shifts/{shiftId}` | Explicitly update a dated shift |
| POST | `/rosters/{id}/shifts/{shiftId}/cancel` | Cancel a shift and its assignments |
| POST | `/rosters/{id}/shifts/{shiftId}/assignments` | Assign Staff |
| PUT | `/rosters/{id}/shifts/{shiftId}/assignments/{assignmentId}` | Replace Staff / position |
| POST | `/rosters/{id}/shifts/{shiftId}/assignments/{assignmentId}/cancel` | Remove an assignment |
| POST | `/rosters/{id}/publish` | Publish and return shortage warnings |
| GET | `/rosters/{id}/history` | Read retained before/after history |

Staff API: `GET /api/staff/work-schedule?from=YYYY-MM-DD&to=YYYY-MM-DD` (at most 93 days).
It includes a preceding overnight shift only when it actually continues into the requested range;
a shift ending exactly at midnight is excluded from the following day.

## Verification

From the backend root:

```powershell
dotnet build src/PMS.API/PMS.API.csproj --no-restore -m:1 /p:UseSharedCompilation=false
dotnet test tests/PMS.Application.UnitTests --no-restore -m:1 /p:UseSharedCompilation=false
dotnet test tests/PMS.Api.IntegrationTests --no-restore -m:1 /p:UseSharedCompilation=false --filter FullyQualifiedName~WorkSchedulingEndpointTests
```

For the opt-in SQL Server test on Windows with LocalDB installed:

```powershell
$env:PMS_WORK_SCHEDULING_SQL_TESTS = '1'
dotnet test tests/PMS.Application.UnitTests --no-restore -m:1 /p:UseSharedCompilation=false --filter FullyQualifiedName~WorkScheduling
```

The SQL test creates a uniquely named `PMS_WorkScheduling_Test_*` database and removes it in
`finally`; it never uses an application database. It checks repeated migration, legacy preservation,
real relational mappings, publication, durable notifications and concurrent overlapping assignments.

Frontend checks from the frontend root: `npm run build` and ESLint on changed TypeScript files.
Manual browser flow: create overnight template; create draft and dated shift; assign Staff;
publish despite shortages; edit a published assignment with a reason; inspect its history;
sign in as the affected Staff and verify their schedule and publication/change notifications.

## Calendar view

The existing Manager and Staff screens share `WorkScheduleCalendar`; there is no second calendar
store, scheduling service or module. The toolbar provides the authorized lot selector, accent-insensitive
employee-name search, an anchor date/month, previous/next, Today, and Day/Week/Month modes.
Weeks start on Monday. Dates are computed as date-only values, avoiding browser DST conversions.
The horizontal viewport contains up to 31 day columns at a time, even for a 93-day roster. The header,
shift column and employee column stay fixed while scrolling. Changing the visible date range resets
the scroll position. Today and weekends have text labels in addition to different backgrounds.

Groups use the template ID plus the saved shift name and hours. Old snapshots with different hours
stay separate. Employee rows are built from assignments in the visible range, not a fabricated full
team roster. Cells retain arrays of references including roster, dated-shift and assignment IDs, so
multiple assignments on a day cannot overwrite one another. Overnight shifts remain on their start
date and show the explicit end date; preceding overnight work is called out separately.

Manager clicks use the same roster revision/history/notification pipeline. Empty cells on existing
dated shifts open an eligibility-checked assignment dialog. An empty date for an active unchanged
template can create its dated shift and first assignment atomically via
`POST /rosters/{id}/calendar-assignments`. No shift is persisted when that assignment fails validation.
`GET /rosters/{id}/eligible-staff` accepts an existing shift ID or a template/start date and checks
active membership plus overlaps; updates are revalidated on save. It can exclude the assignment
being edited, but only when that assignment belongs to the same roster.

Existing assignment updates accept an optional `targetShiftId` to move the same assignment to
another existing dated shift in the same roster. Notes are stored on `DutyAssignments.Notes`
(maximum 1000 characters); the migration adds this nullable column to databases from the previous
phase without rebuilding tables. Notes are included in snapshots and affected-person notifications.
The dialog keeps the revision present when it opened and asks the user to reopen if polling finds
a newer revision. Published changes require a reason, including edits to notes.

Whole-shift edits/removals remain in the existing "Dated shift controls" section; they affect all
assignments of that occurrence. Staff have no mutation controls and keep the existing server-side
filter to their own published assignments. Aggregate staffing totals are captured before this
personal filter, so other Staff's identities are not disclosed and the viewer's rows do not
incorrectly represent total coverage. A day without a visible personal assignment is not described
as a missing lot-wide shift.

The legend separates roster visibility (Draft/Published), active assignment status (Assigned),
attendance and staffing shortages (assigned/minimum by position). An empty cell is not an absence.
There is no approved-leave source in the current system: empty cells are never labelled approved
leave. Minimum rest/weekly hours, leave, payroll and automatic rotation remain outside this phase.

## Integrated attendance

`DutyAttendances` stores one revisioned record per assignment; `DutyAttendanceEvents` retains
immutable before/after state, actor, action, timestamp and Manager reason. The guarded startup
migration adds these tables plus `DutyAssignments.AttendanceTrackingFrom`; it preserves legacy
and existing scheduling data. No cash-drawer or legacy shift table is used for attendance.

The defaults from the business flow are configurable in `WorkAttendance`: `EarlyStartMinutes=30`,
`LateAfterMinutes=15`, `AbsentAfterMinutes=30`, `EndGraceMinutes=15`. Options are validated on resolution.
The start window includes both start minus 30 minutes and start plus 30 minutes. Late is strictly
after start plus 15 minutes. Absent applies strictly after start plus 30 minutes, only to a real,
active, noncancelled, published assignment with an active Staff account still in the lot and no
check-in. A very short shift's start window closes at its scheduled end. Empty cells, shortages,
Draft assignments, cancelled assignments and unavailable Staff are not newly marked absent.

Upcoming, StartWindow, Working, Completed and Absent are calculated against server UTC time and
the occurrence's saved start/end instants, including overnight work. Draft is NotPublished.
After the end plus 15-minute grace (strictly greater), an unfinished working assignment is
Completed with an explicit AutoEnded/review flag and a recorded out time at scheduled end
(or check-in if a Manager entered a later check-in). It is never presented as a Staff check-out.
The minutely Hangfire job persists deadline events and durable notifications idempotently;
reads already reflect elapsed deadlines, without waiting for the next timer tick.

Existing assignments are backfilled with tracking activation time. Assignments whose tracking
activation or roster publication is after their start deadline are Untracked/review, not
retroactively Absent. Managers may record their actual times with a reason. This cutover policy
also covers newly created historical assignments, rescheduling into the past and late publication.
Changing the assigned employee, dated shift or planned times resets tracking activation for that
assignment; a notes-only edit does not reset it. There is currently no
approved-leave relationship to consult; approved leave must eventually cancel/exclude an
assignment through the leave integration before its deadline. That integration is not invented
by this attendance implementation.

Staff may start/end only their own published assignment in their active lot, using server time.
They can act from the existing calendar details or the Current shift card on POS. The card shows
recent/active work and the next 48 hours. A Staff member cannot start a second still-working shift,
including during the previous shift's grace period. Manager corrections are limited to managed
lots and require a nonblank reason of at most 500 characters. Actual times cannot be future times;
end cannot precede start; correcting past the end grace requires an actual end. Reopening applies
to Absent work before scheduled end, records a start at now and clears absence; older completed
work is corrected using actual times. Admin has no implicit attendance-editing permission.

Attendance commands require both attendance and roster revisions. The scheduling transaction/
application lock is shared with attendance writers. Existing attendance history, or an elapsed
tracked published start deadline, blocks cancellation, employee replacement, moving an assignment
or changing its planned times. Notes and position remain editable through the published-history
pipeline. Correcting attendance never silently rewrites the planned shift.

| Method | Route | Permission |
| --- | --- | --- |
| GET | `/api/work-scheduling/assignments/{id}/attendance` | Scoped Manager / owning active Staff |
| POST | `/api/work-scheduling/assignments/{id}/attendance/start` | Owning active Staff |
| POST | `/api/work-scheduling/assignments/{id}/attendance/end` | Owning active Staff |
| POST | `/api/work-scheduling/assignments/{id}/attendance/reopen` | Scoped Manager; reason required |
| PUT | `/api/work-scheduling/assignments/{id}/attendance` | Scoped Manager; actual times and reason |
| GET | `/api/staff/current-shifts` | Active Staff; own published work |

The shared calendar cell displays actual attendance text/icons and separate Late/AutoEnded flags.
Details show planned and recorded times, saved lot time zone and adjustment history. Correction
inputs explicitly use the device time zone and preserve seconds. There is no second calendar.

After commit, Hangfire uses existing SignalR notifications and a lot-scoped `WorkAttendanceChanged`
invalidation signal with no individual attendance payload. Durable owner notifications remain
available if live delivery fails; late starts, absence and auto-end also notify the lot Manager.
Active calendar/details/POS queries poll every 10 seconds, refetch on network reconnect and after
SignalR reconnect has rejoined lot subscriptions. Refetches retain the selected lot, search, date
range and calendar scroll offsets. Cached content remains mounted during temporary connection
errors; stale correction forms require reopening if the record or roster revision changed.

Attendance verification includes exact boundary/overnight tests, role/ownership/lot rejection,
correction/reopening reasons and audit, stale revisions, repeated jobs, historical cutover and
late publication. SQL tests cover repeated migration and concurrent starts. API tests confirm
that a Staff action updates the same roster projection returned to the calendar.

Additional frontend regression checks:

```powershell
node --test tests/work-scheduling/calendar.test.mjs tests/work-scheduling/attendance.test.mjs
```

These cover month/year boundaries, leap months, start-date placement of overnight work, distinct
snapshot groups, exact assignment references, multiple same-day entries, search without changing
staffing totals, unfilled shifts and a 93-day roster with 150 employee rows.
