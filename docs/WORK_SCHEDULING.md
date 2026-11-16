# Work scheduling, rotation and attendance

Payroll built on locked attendance is documented in [PAYROLL.md](PAYROLL.md).

This module implements shift templates and manual duty rosters. Cash drawers, the legacy
`Shift`, `ShiftTemplate`, `ShiftSchedule` and `ShiftCashCount` tables remain independent and unchanged.
Attendance and deterministic rotation generation are integrated into these same assignments
and calendar. Payroll and restriction of POS operations by shift remain outside this module.

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
Generated rosters additionally validate minimum rest and maximum weekly hours, including manual
changes before publication; see the rotation section below.

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
Approved leave and swapped originals have explicit retained calendar trails. Empty cells are
never labelled leave or absence. Leave/swap/replacement workflows share this module; payroll
remains outside this phase.

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
assignment; a notes-only edit does not reset it. Approved leave cancels the original assignment
without deleting it, clears any recorded absence with an attendance event, and provides a real
leave interval to scheduling validation. Historical attendance remains readable by its owner
and scoped Manager; cancelled leave/swap originals cannot start or end a shift.

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

## Rotation generation

The Manager's existing Work schedules screen has a Rotation setup tab and a Sinh lịch action
on an existing Draft roster. Month is only a display mode: generation never creates a monthly
roster implicitly or changes the roster's date range or publication workflow. Preview replaces
the data shown by the existing shared `WorkScheduleCalendar`; there is no second calendar module.
Preview cells are read-only and explicitly distinguish kept rows and proposed additions. Save
creates/updates the same Draft; use the existing manual assignment/dated-shift controls to adjust
it before publication. Published rosters cannot be generation targets.

`WorkRotationGroups` contains lot scope, active flag, revision and offset in days. Normalized
`WorkRotationMembers` stores Employee and direct-work position; a Staff member belongs to at most
one active group in a lot. `WorkRotationCycles` stores revisioned ordered blocks (template ID or
Rest, number of days). The UI starts with block lengths 2/2/2/1; the Manager explicitly chooses
Morning/Afternoon/Night templates from that lot. Neither names nor group offsets are assumed to
provide sufficient coverage. Cycle indices use date-only day numbers and positive modulo:
`(date - cycleStart + groupOffset) mod cycleLength`, also for dates before the anchor.

Generation is deterministic by date, group ID and employee ID; it is not an optimizer. Every
template referenced in the cycle contributes an actual dated occurrence/minimum demand on each
selected day, including days where all selected groups are resting. Group members keep their
configured Entry/Exit/Counter position. Ineligible/overlapping/leave-conflicting candidates are
excluded and explained; unmet template minima remain visible shortages. Shortages still allow
publication. Preview also reports Staff without direct assignments in the selected range;
this includes unselected Staff and people assigned only to standby.

Confirmed parameters: **minimum 60 minutes rest**, **maximum 48 planned hours per week**.
Configuration keys `WorkRotation:MinimumRestHours=1` and `MaximumWeeklyHours=48` can override
the agreed defaults. Limits are checked against all relevant users' assignments, across rosters
and lots, including drafts and adjacent dates outside the generation selection. Weeks are
Monday 00:00 through the next Monday 00:00 in the lot's saved time zone; actual UTC interval
intersections split overnight hours across week boundaries. Exactly 60 minutes rest and exactly
48 hours are allowed. A generated roster is revalidated when read and before publication, so
subsequent manual changes cannot publish unresolved known overlap/rest/hour/leave violations.
This does not retroactively enforce new limits on legacy manually-created rosters.

`IApprovedWorkLeaveSource` is a read-only integration contract for approved UTC leave intervals.
Any intersection, including the following morning of a night shift, excludes the candidate.
Production registers `ApprovedWorkLeaveSource`, reading only real Approved `WorkLeaveRequests`.
Pending/rejected/cancelled requests do not create exclusions. Leave approvals and rotation writes
share the scheduling transaction/application lock. Preview fingerprints include approved intervals;
changed approvals require a fresh preview. Pending-request assignments are protected from
regeneration. The unavailable-source implementation remains only as an optional integration/test
fallback, never a source of fabricated leave.

`DutyAssignment.Source` distinguishes Manual and Rotation. `IsManuallyEdited` and stable
`GenerationKey` retain manual edits, moves and cancellation tombstones when regenerating;
existing rows migrate as Manual. Append is the default. Explicit replacement only cancels
untouched Rotation rows in the selected groups, selected start dates and target Draft.
Attendance rows/history are always protected. Other Drafts and every Published roster are kept
and used for conflicts. Nothing is physically deleted from old scheduling or attendance data.
Replacement never refreshes an existing occurrence's saved template snapshot silently.

Preview returns separate Keep/Add/Replace and standby changes. Saving requires its fingerprint
and roster revision; the server recalculates the proposal from authoritative inputs inside the
existing serializable scheduling transaction and application lock. Changes to groups, cycle,
templates, Staff eligibility, leave inputs or relevant scheduling data require a new preview.
Repeated/stale save requests return 409 and cannot duplicate assignments. Rotation generation
uses the existing roster revision/history pipeline. Configuration, rule values and group/cycle
snapshots are recorded in the roster history; Staff projections omit these management details.

`DutyStandbyAssignments` is separate from direct DutyAssignments. When enabled, generation
rotates deterministically through the explicitly selected active group pool, choosing one person
who has no direct work or approved leave anywhere in that whole local day. Overnight work ending
that morning makes the person unavailable for daily standby. Existing standby is respected across
rosters; missing eligible people are reported as standby shortages. Standby has no direct staffing
count, planned work-hour sum or attendance/Absent record. The shared calendar renders a separate
Standby group. Dated shift controls include add/edit/remove standby; manual edits/cancellations are
protected on regeneration. Published standby changes use the existing reason/history/affected-user
notification flow. Staff can see their own Published standby even when they have no direct shift.
Confirmed replacement work releases conflicting standby with roster history and creates a new
direct-work assignment. Standby allowances and payroll remain future work.

## Leave, swaps and replacement workflow

Staff submit requests from their own Published assignment details on the existing calendar/POS.
The Manager's existing Work schedules page and Staff's personal schedule include a scoped request
board and leave balances. Pending requests have text markers on active cells and do not remove
the original work. Approved leave and original swapped assignments remain visible as historical
rows in the same shift/date group. Open positions are rendered in the shift/date staffing cell,
including shifts with no employee rows. Manager can open missing positions, inspect eligible
candidates, daily standby, claims and offer history. Staff see only their own requests/trails,
targeted offers or public offers in their current lot; personal leave reasons are not disclosed
through public vacancies or lot-wide realtime signals.

New additive tables: `WorkLeaveRequests`, `WorkLeaveBalances`, `WorkLeaveDays`, `WorkSwapRequests`,
`WorkOpenShifts`, `WorkOpenShiftClaims`, `WorkManagerCoverages`, `WorkScheduleWorkflowEvents`.
Assignment metadata links a new replacement/swap assignment to its original and records the
reason/weekly-hours exception. All existing scheduling, legacy shifts, cash drawers and
attendance data are retained. Request history is append-only; roster changes also use the
existing before/after roster revision log. No salary is calculated.

Confirmed `WorkRequests` defaults:

| Key | Value | Meaning |
| --- | --- | --- |
| PlannedNoticeHours | 24 | Planned leave at least 24 hours before start; otherwise Emergency |
| AnnualPaidDays | 12 | Annual paid-day entitlement per Staff user |
| TargetResponseMinutes | 120 | Proposed person / normal standby response window |
| EmergencyStandbyMinutes | 60 | Standby response for work starting in less than 24 hours |
| PublicClaimMinutes | 1440 | Maximum public offer duration: 24 hours |
| CutoffHoursBeforeStart | 24 | Public offers stop 24 hours before planned start |

The user selected the emergency policy: no direct proposed replacement when less than 24 hours
remain; notify Manager and use the standby/replacement process. A public response window ends at
the earliest of posted-at +24 hours, start -24 hours, or shift end. When already past cut-off,
the process enters Manual after any applicable priority/standby response; no fictitious public
waiting period is created. Exact deadline equality is expired. A targeted accepted claim stops
its response timer while awaiting Manager confirmation; the shift end still expires it. Public
claims remain confirmable by Manager after cut-off while the slot is still open; new public
claims are blocked. The minutely job advances offers and expires undecided swaps at the first
related shift start, idempotently. Pending emergency leave is not automatically expired, since
the Manager may confirm it after the missed shift.

Leave transitions: `Pending → Approved / Rejected / Cancelled`. Staff can cancel their own Pending
request. Manager approval rechecks active membership, assignment state and attendance. Actual
check-in/out requires attendance resolution before full-assignment leave; this phase does not
infer partial-day leave. Approved leave records the original UTC interval and local start date,
soft-cancels the original, and clears Absent with retained history. If direct staffing still
meets the position minimum, no replacement search is required. Otherwise the vacancy links the
original assignment and follows priority offers. A no-show also opens an uncovered position;
Absent assignments are retained but excluded from effective coverage counts.

Paid leave is charged once per user/local start date, even for multiple assignments on that date;
overnight work uses its start date/year. No balance is consumed while Pending. Allocation happens
under the shared write lock at approval: remaining entitlement → Paid; exhausted → Unpaid without
an absence penalty. Later entitlement edits do not rewrite approved classifications. Admin can
view/edit yearly Staff entitlements with a reason in the existing Staff accounts page; revisions
prevent stale edits, and entitlement cannot be reduced below paid days used. Staff see their own
balances; Managers see Staff balances in managed lots. There is no carry-over, proration or
payroll calculation introduced by this workflow.

Swap transitions: `AwaitingColleague → PendingManager → Approved / Rejected`, with colleague
`Declined`, requester `Cancelled` before final decision, or `Expired` at the first start.
Manager cannot bypass colleague consent. Both original assignments must still be Published,
active and not started/clocked in. Eligibility is rechecked against other work, leave, rest and
weekly hours with both originals excluded from the hypothetical swap. Approval soft-cancels
both originals and creates two linked assignments at the respective original positions in one
transaction, even across two rosters. Failure changes neither side. Conflicting standby is
released in the same transaction with its own roster history.

Open-position lifecycle: `Open → Filled / Closed / Expired`; priority stage is separate:
`Proposed → Standby → Public → Manual`. Ineligible or unavailable priority candidates are skipped.
Decline, withdrawal, Manager rejection or timeout advances a targeted offer. Public offers can
have multiple claims; a claim is `Claimed → Confirmed / Rejected / Withdrawn / Expired` and never
counts as coverage until Manager confirmation. Every confirmation checks current eligibility
and remaining demand under the shared SQL lock. One winner creates one separate replacement;
other pending claims are rejected and notified. Manual assignment follows cut-off/timeout.
Only this Manual step can record an explicit weekly-hours exception with Manager reason and an
overtime-review flag; overlap, approved leave, active membership and minimum rest remain required.
Manager self-coverage has a separate record and contributes to its position's coverage without
creating Staff attendance for the Manager. Closing an offer retains history and notifies pending
claimants. Manual calendar assignment cannot bypass an existing open-position confirmation flow.

For replacement work confirmed after the planned shift has begun, the Staff start window opens
at confirmation/tracking activation; Late and the missed-start deadline use +15/+30 minutes from
that activation. Planned shift end and grace remain unchanged. This avoids inheriting the old
Staff member's absence while preserving actual check-in times. Reopening an original absence
closes outstanding offers and claims; a confirmed replacement must be resolved first. Recorded
coverage/pending requests protect affected schedule edits from silently invalidating their history.
Dated occurrences with approved leave or swap originals cannot be rewritten or removed: the
shared original shift snapshot is retained, as are its assignment positions and request links.

Notifications are committed with the request/assignment changes, then pushed through existing
SignalR/Hangfire infrastructure. Calendar/POS/request queries refresh on the same scoped signal,
network/SignalR reconnect and 10-second polling. Only invalidation is broadcast to the lot;
personal request reasons remain in authorized responses. Existing filters, dates and calendar
scroll preservation apply. Request-board lists prioritize pending work and show up to 300 recent
items per type; assignment details retain the complete request history for that assignment.

Routes under `/api/work-scheduling` (Staff/Manager with backend owner/lot checks):

| Method | Suffix | Purpose |
| --- | --- | --- |
| GET | `/lots/{lotId}/requests?year=YYYY` | Scoped request board, vacancies and balances |
| GET | `/assignments/{id}/requests` | Own/scoped assignment requests and swap choices |
| POST | `/leave-requests` | Submit Planned/Emergency leave |
| POST | `/leave-requests/{id}/actions` | Cancel / approve / reject with revision |
| POST | `/swap-requests` | Propose a reciprocal swap |
| POST | `/swap-requests/{id}/actions` | Accept / decline / cancel / approve / reject |
| POST | `/lots/{lotId}/open-shifts` | Manager opens a missing Published position |
| GET | `/open-shifts/{id}` | Authorized offer detail, claims and Manager candidates/history |
| POST | `/open-shifts/{id}/actions` | Claim / decline / withdraw / confirm / reject claim / assign / ManagerCover / close |

Admin balances use `GET /api/admin/work-scheduling/leave-balances?year=YYYY` and
`PUT /api/admin/work-scheduling/leave-balances/{employeeId}/{year}` with revision, entitlement and
reason. No request action requires the Staff member to be currently clocked in. Cash drawer
opening, collection, handover and closure keep their existing lifecycle.

Additional Manager routes under `/api/manager/lots/{lotId}/work-scheduling`:

| Method | Suffix | Purpose |
| --- | --- | --- |
| GET / POST | `/rotation-groups` | List / create scoped groups |
| PUT | `/rotation-groups/{id}` | Edit membership, position, offset or deactivate with revision |
| GET / POST | `/rotation-cycles` | List / create cycles |
| PUT | `/rotation-cycles/{id}` | Edit named blocks with revision |
| POST | `/rosters/{id}/rotation-preview` | Read-only preview and input fingerprint |
| POST | `/rosters/{id}/rotation-apply` | Revalidate and apply to Draft atomically |
| POST | `/rosters/{id}/standby` | Add manual daily standby |
| PUT | `/rosters/{id}/standby/{standbyId}` | Edit standby; null employee cancels with retained history |

The additive repeatable migration creates the four rotation/standby tables and metadata columns;
it does not reuse legacy Shift tables. Tests cover the default cycle, offsets/negative modulo,
93-day/year-crossing ranges, weekly overnight boundaries, 60-minute rest, external Published
conflicts, approved leave/provider availability, manual preservation, protected attendance,
Staff privacy, standby-only notification/projection, stale previews and repeated requests.
The isolated LocalDB test also saves group/cycle data, races two generation applications and
repeats migration while preserving attendance/history and legacy data.

## Attendance review, confirmation and period locking

Requirements implemented from the business flow: Manager reviews Staff attendance in their lot,
decides overtime and confirms the lot; Admin records Manager workdays/leave and closes a period.
Closing warns about unconfirmed lots. Locked evidence is retained; corrections are separate
entries in a subsequent period. This module does not create payroll, salary amounts or payment states.

Implementation choices for these requirements:

- Admin creates a named, explicit inclusive date range and selects the included lots. Periods
  cannot overlap for the same lot. Each lot stores its timezone and the corresponding half-open
  UTC interval. The calendar's Day/Week/Month selector only controls display.
- Lot states are `Open → Confirmed → Locked`. Edits to related schedule, attendance, approvals,
  Manager days or carry adjustments invalidate a confirmation and retain its old snapshot in
  the audit trail. Confirming again requires the current lot revision. A period displays Confirmed
  when all included lots are confirmed; Admin alone locks all included lots atomically.
- An Admin may close unconfirmed lots only after explicit acknowledgment and a required reason.
  This does not bypass unresolved attendance: unfinished/future shifts, unreviewed automatic
  checkouts/untracked attendance, undecided overtime, pending leave/swaps and open replacement
  offers prevent confirmation/locking. Late and recorded Absent remain visible and are acknowledged
  through the lot confirmation; they do not require fabricated times.
- Original actual in/out comes from immutable Staff Started/Ended events. Corrections never replace
  those events. Missing historical clock events are **unknown**, including automatic checkout;
  an inferred or corrected end is not presented as an original Staff checkout.
- Current/adjusted hours use the stored interval. Ordinary hours are the actual interval within
  the planned shift, subject to the agreed 48-hour local Monday–Sunday limit. Actual ordinary
  hours are allocated chronologically; excess and early/late outside-shift hours are overtime,
  without double counting. Late has no separate penalty. No rounding or wage coefficient is applied.
- Within-shift hours become approved on individual review or lot confirmation. Overtime requires
  an explicit approve/reject decision with a reason. A scheduling `AllowWeeklyOvertime` exception
  does not itself approve actual overtime. Decisions reference attendance revision, planned times
  and the preceding weekly work evidence. Corrections make outdated decisions pending again.
- Changes earlier in a week also invalidate dependent later period confirmations. A locked affected
  period rejects direct changes, even if the request moves the shift or its corrected times outside
  that period. Later assignments at another lot are included when they share weekly work evidence.
  Period closing uses the same serializable transaction/application lock as every
  schedule writer, so a concurrent edit cannot slip between lock validation and persistence.
- The shared `SaveWorkChangesAsync` guard is used at every scheduling, rotation, attendance,
  request and replacement persistence point; it checks old and new intervals before saving. Read
  permissions and cash drawer operations are unchanged. Timer jobs skip locked attendance and
  preserve idempotence. Individual review materializes an already-due automatic end if its timer
  has not run, preserving the automatic flag and recording the review event.
- Overnight shifts retain the original assignment and start date. Hours are intersected separately
  with each lot period, including weekly/monthly boundaries. Leave days retain the agreed start-date
  accounting; an overnight absence is counted once in the start-date period. Locked review rows,
  totals, decisions and Manager days are returned from the snapshot. Decision histories include
  their reasons and distinguish the current decision from earlier/stale decisions.
- Manager can approve a signed-hours carry entry from a locked source to a later open period in
  the same lot, with source employee/optional assignment, target revision and reason. Entries are
  append-only; correcting one requires a compensating entry. They prepare the attendance evidence
  for later payroll integration and never modify the locked source.

Frontend: Manager opens **Work schedules → Review attendance by period**, selects period/lot,
filters anomalies on the same calendar and opens the existing assignment details. Summary totals
cover all Published rosters in the explicit period, even when the selected roster/calendar view
shows only part of it. A matching-assignment list opens records outside the current visible range.
Admin opens **Facility Managers → Attendance periods and Manager workdays** to create periods,
record Manager days and review the explicit lot list before locking. Queries use the existing
realtime invalidation, reconnect refetch and polling; updates retain calendar filters and scroll.

API base `/api/work-scheduling/attendance-periods`:

| Method | Suffix | Role / operation |
| --- | --- | --- |
| GET | `?lotId=...` | Manager scoped list; Admin all or scoped |
| GET | `/directory` | Admin's existing lots and current Manager IDs |
| POST | root | Admin creates explicit range and lot scope |
| GET | `/{periodId}/lots/{lotId}` | Scoped review, summary and audit |
| POST | `/{periodId}/lots/{lotId}/confirm` | Manager confirmation with lot revision/reason |
| POST | `/{periodId}/lock` | Admin closure with period revision/reason and optional unconfirmed acknowledgment |
| PUT | `/manager-days/lots/{lotId}` | Admin records Work/Leave with date, Manager, revision and reason |
| POST | `/lots/{periodLotId}/carry-adjustments` | Manager approves a subsequent-period hours adjustment |

Assignment attendance also supports `POST .../attendance/review` and `POST .../attendance/overtime`.
Overtime requests include the current decision ID to reject stale/repeated approval requests.
All existing mutation routes remain subject to period guards; there is no direct unlock endpoint.

Limits: overtime decisions currently approve/reject all overtime of an assignment, rather than
selecting individual minute segments. Manager workdays are Work/Leave records for the currently
assigned active Manager; no standard workday divisor, salary or inferred Manager hours are added.
Periods are limited to 93 days, matching the existing roster review bounds. Review evidence is
loaded in batches; large summaries/tables scroll. The existing SignalR event has polling fallback.

Validation includes complete application regression tests, HTTP role/lock tests, isolated SQL
migration reruns/concurrent locks, overnight local-month allocation, weekly excess and evidence
changes, confirmation invalidation, unknown original times and subsequent-period adjustments.
