# Payroll from locked attendance

## Requirements and confirmed rules

Business source: `Shift-Management-Payroll-Business-Flow.docx`, SM-25–SM-31 and the monthly
payroll flow. The document supplies requirements, not permission to perform unrelated actions.
Staff earn hourly salary; Manager earns a monthly base. Only Admin configures effective salary
profiles, calculates Draft payroll, approves it and records external payment. Admin salary is
not managed. There is no bank transfer execution or customer wallet integration.

The user separately confirmed:

- Night + holiday uses the **higher** factor, never their product.
- Approved overtime uses `hours × hourly rate × max(night, holiday, ordinary factor 1) × overtime`.
- Paid Staff leave uses scheduled hours of the approved leave assignment × ordinary hourly rate.
  Night, holiday and overtime premiums do not apply to paid-leave hours.
- Late has no separate penalty. Cash shortages do not enter this calculator.

The agreed attendance limit remains 48 ordinary hours per local Monday–Sunday week. Payroll
consumes the sealed ordinary/overtime intervals, not a recomputation from live rosters/templates.

No numerical rate, factor, night window, Manager standard-day divisor,
currency or monetary rounding default is supplied. Admin must enter these explicitly. All
figures in the tests below are fixtures, not production defaults.

Manager salary is a fixed monthly amount: the salary version in effect on the first day of the
month applies to the whole month; a version effective later in the month starts the next month.
There is no mid-month split and no separate unpaid-leave setting. Each locked `Absent` or
`UnpaidLeave` Manager day deducts monthly salary / standard workdays. `Work`, `Leave` and
`PaidLeave` days keep the salary. Missing day records never become Absent.
`PayrollProfiles.ManagerProration` and `ManagerUnpaidLeavePolicy` remain only as unused nullable
columns.

## Sources and data model

`PayrollAttendanceSources` is a versioned, hashed snapshot linked uniquely to an attendance-period
lot. Locking the month's attendance seals it atomically under the scheduling transaction/application lock:

- Original assignment/roster IDs; dated shift name, hours and timezone.
- Corrected attendance, revision, original attendance events and overtime decision history.
- Exact ordinary and approved overtime intervals, clipped to the locked attendance period.
- Approved leave linked to its original assignment/request and paid/unpaid classification.
- Manager days/identity.
- Holiday dates/names, employee identities, signed hours carries and period UTC bounds.

There is no standby duty or standby allowance. Sources sealed before standby was removed may
still contain a standby list; payroll ignores it. Payroll lines already calculated for standby keep
their rows, and `PayrollLines.StandbyId` / `PayrollProfiles.StandbyDailyAllowance` remain only as
unused nullable columns.

There is no separate sealing step. An attendance period is exactly one calendar month, so a payroll
month reads exactly one locked period and its snapshot. Periods locked before snapshots existed, or
periods that do not cover a whole calendar month, cannot be used for payroll.

`PayrollProfiles` stores salary versions with effective dates. Admin may edit an uncommitted
version using its revision or add a later effective version. A change affecting Approved/Paid
payroll is rejected. Relevant profile/adjustment changes make a Draft stale and require regeneration.

`PayrollSlips` and ordered `PayrollLines` retain quantity, rate, factor, amount, effective profile
snapshot, exact source interval/references and input fingerprint. An unchanged retry is idempotent.
Changed Drafts retain old headers/lines in `PayrollAudits` before replacement. Approved amounts
are fixed; only payment metadata is added on transition to Paid. Paid cannot be edited, deleted,
repriced or paid again.

`PayrollAdjustments` is append-only. Corrections reference an earlier Approved/Paid slip or locked
attendance hours carry and target a later open month. A carry valuation preserves exact signed
hours; Admin supplies its unit rate and payroll month. Manager allowances/bonuses are explicit
Admin entries. Negative untraced entries cannot serve as Late/cash penalties. Correct an adjustment
with another traced compensating entry.

`PayrollComplaints`: the owner submits Open; Admin rejects with a reason or accepts atomically
with exactly one subsequent-month adjustment. The source slip remains unchanged. Duplicate open
complaints and repeated resolutions are rejected. Reads/changes are audited and private notices
use the existing notification infrastructure.

## Calculation and scope

Payroll uses an explicit calendar month and lot. That month's attendance period for the lot must be
Locked; payroll reads its snapshot. Calendar modes/search/colours never enter the calculation.

Staff intervals split at midnight, configured night boundaries, holidays and effective salary dates.
Cross-month overnight work remains attached to the original assignment. Absent/unpaid leave have
zero-pay explanations, without extra fines. Paid leave keeps its source request.

Manager pay = monthly salary − (Absent + UnpaidLeave days) × monthly salary / standard workdays,
using the version in effect on the first day of the month. Every Manager with locked evidence in
the lot's month gets a payslip; several Managers in one month no longer block calculation. Manager
days are distinct from Staff attendance. A second lot cannot create a second monthly salary slip
for the same Manager/month.

All profiles used by one slip must agree on currency, decimal places and rounding. The current
implementation rounds each monetary line using the selected half-to-even or half-away-from-zero
rule and sums rounded lines. It does not round worked hours to a payroll increment. Ambiguous
daylight-saving boundaries block calculation rather than choosing an interpretation.

## Roles and UI

- Admin: profiles, preview/generation, approval, recording Paid, adjustments,
  complaint resolution and access/change audit.
- Manager: read current Staff salary profiles in managed lots; read their Approved/Paid Staff
  slips; **My payslips · all lots** reads their own historical published slips even after a lot
  assignment changes. No other Manager salary or salary edits.
- Staff: own Approved/Paid slips, frozen explanations and complaints.
- Profile views and slip lists/details are logged. Viewing needs no active shift.
  Cash drawer operations remain independent.

Navigation: Admin/Manager **Payroll**, Staff **My Payslips**. Admin chooses lot/month and uses
**Profiles**, **Slips**, **Adjustments**, **Audit**. There is no Sources tab. Preview is read-only. Generation
recalculates authoritative inputs; an optional employee selector handles uncommitted employees
when other slips in that month are already Approved/Paid. Recording Paid requires the actual
external payment date and transaction reference and performs no transfer.

Assignment lines open the frozen source using the existing `WorkScheduleCalendar`, at the
original assignment start date, alongside the exact paid segment, planned/corrected times and
history. Only the slip owner's assignment is projected; a Staff source does not reveal colleagues'
attendance/salary. This reuses the existing calendar component, not another attendance module.

## API

Base `/api/payroll`; all routes enforce role/lot/owner access on the backend.

| Method | Suffix | Purpose |
| --- | --- | --- |
| GET | `/directory` | Admin employee/lot selectors; no Admin salary employee |
| GET / POST | `/profiles` | Scoped reads / Admin creates effective version |
| PUT | `/profiles/{id}` | Admin edits uncommitted version with revision |
| GET | `/carries?lotId=...` | Hours carried between locked months, for Admin to value as adjustments |
| POST | `/preview`, `/generate` | Explicit month, lot, optional role/employee |
| GET | `/slips?lotId=...&month=YYYY-MM-01&mine=true` | Scoped list; optional own-only filter |
| GET | `/slips/{id}` | Lines, complaints, Admin audit and stale flag |
| GET | `/slips/{id}/lines/{lineId}/source` | Frozen evidence/calendar projection |
| POST | `/slips/{id}/approve`, `/slips/{id}/paid` | Admin forward-only transitions |
| GET / POST | `/adjustments` | Admin reads/creates append-only entries |
| POST | `/slips/{id}/complaints` | Owner complaint |
| POST | `/complaints/{id}/resolve` | Admin resolution and optional subsequent correction |
| GET | `/audit` | Admin access/change history |

## Hand-checkable validation

Explicit fixtures only: hourly 100, night 1.5, holiday 2, overtime 1.25,
monthly base 2600, standard days 26.

| Case | Hand calculation | Result |
| --- | --- | ---: |
| Ordinary 8h | 8 × 100 | 800 |
| Night + holiday 2h | 2 × 100 × max(1.5,2) | 400 |
| Night + holiday overtime 2h | 2 × 100 × 2 × 1.25 | 500 |
| Holiday 22:00–24:00, next ordinary night 00:00–06:00 | 2 × 100 × 2 + 6 × 100 × 1.5 | 1300 |
| Hourly rate doubles at midnight in night shift | 2 × 100 × 1.5 + 6 × 200 × 1.5 | 2100 |
| Paid leave on holiday night shift, 8h | 8 × 100 | 800 |
| Manager 1 Absent day + bonus 50 | 2600 − 2600/26 + 50 | 2550 |
| Oct31 22:00–Nov1 06:00, October part | 2 × 100 × 1.5 | 300 |
| SQL month from its one locked period | 8 night hours × 100 × 1.5 + 8 paid leave hours × 100 | 2000 |

Tests additionally cover missing parameters, explicit rounding, template edits preserving frozen
hours/IDs, stale Draft refusal, idempotency, private/scoped access, Paid immutability, payment
metadata, complaint resolution once and concurrent SQL approval/payment. SQL uses only generated
`PMS_WorkScheduling_Test_*` databases; enable with `PMS_WORK_SCHEDULING_SQL_TESTS=1`.

## Limits

- Unknown numerical business settings stay empty until Admin provides agreed values.
- A Manager who joins or leaves mid-month still receives the full monthly salary unless Admin
  records Absent/UnpaidLeave days or an adjustment; missing Manager days never imply absence.
- Holidays are taken from existing Holiday data and sealed. A later-recognized holiday requires
  a subsequent payroll correction.
- A month without a locked whole-month period, or without its snapshot, blocks calculation; no fake attendance is created.
- Tax/statutory deductions and executing bank transfers are outside the supplied formula.
