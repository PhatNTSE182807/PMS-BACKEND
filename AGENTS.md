# AGENTS.md — XXIV Parking backend (`Parking system`)

Instructions for AI coding agents (Claude Code, Codex, Copilot…) and for people new to the repo.
Read this file first; it is written so you can get oriented in a couple of minutes instead of
re-reading the whole codebase and the SRS.

This repo is one of two. Keep both in mind on every task:

| Repo | Folder | Remote |
| --- | --- | --- |
| Backend (this repo) | `D:\OJT Project\Parking system` | `git.fsoft-academy.edu.vn/hcm26-cpl-net-07/team-03/parking-management-system` |
| Frontend | `D:\OJT Project\PMS Frontend` | `git.fsoft-academy.edu.vn/hcm26-cpl-net-07/team-03/parking-management-system-frontend` |

---

## 1. Non-negotiable rules

1. **Everything written into the repo is in English.** Identifiers, comments, XML docs, log
   messages, exception and validation messages returned by the API, seed data labels, test names,
   commit messages, branch names, PR titles and descriptions, and Markdown docs other than the SRS
   copy. Vietnamese domain words get an English name from the glossary in section 6
   (e.g. "cà vẹt" → `RegistrationCertificate`, never `CaVet`). Talk to the user in Vietnamese
   when they write in Vietnamese; only the chat is Vietnamese.
2. **Work on a feature branch, in both repos, and merge into `develop`.** See section 8.
   Never commit straight to `develop`, `main`, `master` or `production` (`production` auto-deploys
   to Azure).
3. **The SRS v1.4 is the source of truth** for behaviour: `docs/SRS_v1.4.md`. When the user asks
   for something that contradicts it, do what the user asked and add a line to section 10
   ("Known differences from the SRS").
4. **Never hard-delete business data** (sessions, payments, vehicles, staff accounts): lock,
   detach or anonymise instead (BR-21). Financial records are immutable; corrections are new
   `PaymentAdjustment` rows (NFR-08).
5. **Maker-Checker:** the person who proposes an approval can never approve it (BR-14). Enforce
   it in the backend, never trust role or scope sent by the client (FR-AUTH-07).
6. **Privacy:** public endpoints and public real-time channels never expose licence plates
   (FR-3D-08); vehicle registration photos are only readable with the "verify vehicles"
   permission and every read is audited (FR-VER-06).
7. **No requirement codes in the UI.** Nothing a user can see may show SRS or spec codes such as
   `FR-AUTH-01`, `FR-AD…`, `BR-10`, `NFR-05`, `UC-03` or `SEC-01`: not in labels, buttons,
   tooltips, toasts, dialogs, empty states, page titles, demo/mock data, nor in API error and
   validation messages (the frontend shows those to users). Say the rule in plain words instead
   ("The exit window has passed: 10,000 VND more is due."). Codes are fine in code comments,
   tests, commit messages and docs. Before merging, run
   `grep -rnE "\b(FR|BR|NFR|UC|SEC|Sec)-[A-Z0-9]" src` and remove every hit that is not a comment.
8. **No AI co-author or attribution in git.** Commit messages, merge messages, PR titles and
   descriptions never contain `Co-Authored-By: Claude …`, `noreply@anthropic.com`,
   "Generated with Claude Code" or any other AI-assistant credit. Turn Claude Code's attribution
   off in your own `~/.claude/settings.json` (`"attribution": { "commit": "", "pr": "" }`).
   The `.claude/` and `.githooks/` folders are local and never committed. Pushed history is
   not rewritten.

## 2. The system in one screen (SRS v1.4)

Multi-lot parking management: vehicles enter and leave, fees are charged, bays are booked and
shown on a 3D map, cash and online payments are reconciled, managers get reports.

- **Actors / roles:** Admin, Manager, Staff, User (4 roles in ASP.NET Identity) + Guest (no
  account, not a role). Manager and Staff only act inside the lots assigned to them
  (`UserLotAccess`); the "verify vehicles" permission is chain-wide.
- **Who creates whom:** the first Admin is seeded; Admin creates Managers only; a Manager creates
  Staff only in their own lots. Users self-register (form or Google). No OTP / 2FA in v1.4.
- **Apps:** Web Admin/Dashboard (Admin, Manager), POS Staff (gate/counter), customer web/app
  (User, Guest). 3D is rendered only in the frontend; the backend serves the layout JSON and slot
  status over REST + SignalR.
- **Two kinds of bays, physically separate:** *reservation slots* (fixed, signposted, tracked one
  by one with a state machine) and *walk-in capacity* (not assigned a slot; only the **zone
  counter** of a counting zone goes up and down). Walk-in capacity of a zone = active slots −
  reservation slots − maintenance slots. A counting zone is (lot, vehicle type) by default.
- **Bookings are by arrival time**, at most N hours ahead (BR-01, 2 h), with a deposit that is
  deducted from the fee at check-out (BR-03). Arrival window = [arrival − early, arrival + hold]
  (BR-02, 15 min / 15 min).
- **Three payment points:** exit lane (barrier opens at once), counter/kiosk and app. The **exit
  window** (BR-10, 15 min) applies only to the last two (pre-payment).
- **Cash is closed per Staff per day** (no shifts at all — the old `Shift*` entities are legacy).
- **Offline is mandatory:** a lot-level Edge Service keeps gates working; QR tickets and exit
  passes are signed (HMAC) so they can be checked offline (`ParkingTokenSigner`).

Out of scope in v1.4 (do not build unless asked): OTP / 2FA login, monthly passes and
subscriptions, e-invoices, loyalty points / reviews, multi-tenant, shift scheduling,
per-slot sensors for walk-ins, AI forecasting, OCR of registration papers (SRS 2.5).

## 3. Find a requirement fast

`docs/SRS_v1.4.md` is the full SRS converted from the team's .docx, with every table kept.
Search by ID instead of reading it top to bottom:

```bash
grep -n "FR-RSV-02" docs/SRS_v1.4.md        # one requirement
grep -n "^| BR-" docs/SRS_v1.4.md           # all 28 business rules with defaults
grep -n "^| UC-" docs/SRS_v1.4.md           # the 15 use cases
grep -n "^#### 4.2" docs/SRS_v1.4.md        # exception flows per area
```

| SRS module | IDs | Backend (this repo) | Frontend |
| --- | --- | --- | --- |
| Accounts & RBAC | FR-AUTH-01…12 | `UsersController`, `AdminController` (Managers), `ManagerController` (Staff), `IEmployeeAccountService`, `IGoogleTokenValidator` | `LoginPage`, `RegisterPage`, `pages/admin/StaffAccountsPage`, `pages/management/ManagerStaffPage`, `api/identity.service.ts` |
| Lots, zones, slots | FR-LOT-01…08 | `LotsController`, `ZonesController`, `SlotsController`, `IParkingLotService`, `IZoneService`, `ISlotService`, `Jobs/LotJobs` | `pages/admin/ParkingLotsPage`, `api/lot-management.*` |
| 3D map | FR-3D-01…09 | `LayoutsController` (`api/public/lots/{id}/layout`, rate-limited), `ILayoutService`, `Hubs/LotHub` | `components/customer/garage/*`, `api/layout.service.ts`, `hooks/useLotLayout.ts` |
| Gate operations | FR-OPS-01…19 | `StaffGateController`, `GateRequestsController`, `ParkingSessionsController`, `ManagerOperationsController` (plate lists), `Hubs/ParkingOpsHub`, `Jobs/ParkingJobs` | `pages/staff/StaffGatePage`, `StaffCheckoutPage`, `StaffExceptionsPage` |
| Pricing | FR-PRC-01…06 | `ManagerPricingController` (`/api/manager/lots/{id}/pricing-rules`, simulate), `LotsController` public prices, `IPricingRuleService`, `TariffProvider`, `FeeCalculator` | `pages/management/ManagerPricingPage`, `api/pricing.service.ts`, `hooks/usePricing.ts` |
| Reservations | FR-RSV-01…10 | `ReservationsController`, `IReservationService` | `pages/customer/CustomerBookingsPage`, `pages/management/ManagerReservationsPage` |
| Payments & receipts | FR-PAY-01…11 | `PaymentsController`, `WalletController`, `Services/Payments` (VNPay, MoMo) | `PaymentReturnPage`, `pages/staff/StaffCashTallyPage` |
| Customer app | FR-CUS-01…06 | `UsersController` (profile), `VehicleDocumentsController`, `IParkingHistoryService` | `pages/customer/*` |
| Vehicle verification | FR-VER-01…06 | `VehicleVerificationsController`, `Hubs/VehicleVerificationHub`, `Jobs/VehicleDocumentJobs` | `pages/staff/StaffVehicleVerificationPage` |
| Reports | FR-RPT-01…07 | no report controller yet | `pages/admin/RevenueReportPage`, `AdminDashboardPage` |
| Staff, devices, settings | FR-SYS-01…08 | `NotificationsController`, `SystemSetting`, `Device`, `Gate` entities | `api/system.service.ts`, `api/notification.service.ts` |

## 4. Business rules you will hit most

Defaults are configurable; the full table is SRS section 5.

| Rule | Default |
| --- | --- |
| BR-01 book at most N hours ahead; only vehicle types with "bookable" on | 2 h; cars only |
| BR-02 arrival window | 15 min early, 15 min hold |
| BR-04 / BR-05 cancel ≥ H min before arrival → 100 % refund; later or no-show → deposit kept | H = 30 min |
| BR-06 deposit, payment deadline | 30,000 VND per car; 10 min |
| BR-07 active bookings | 2 per account, 1 per vehicle |
| BR-09 fee 0 → no payment row, record the 0 reason | — |
| BR-10 after the exit window: re-price from entry, subtract paid, add late-exit fee | 15 min |
| BR-11 wait for a dynamic-QR callback | 60 s |
| BR-12 daily cash close: difference that needs a reason; latest close time | 20,000 VND; 23:00 |
| BR-14 approval deadlines, escalate to Admin | lost ticket 10 min; refund / waiver 24 h |
| BR-18 ANPR confidence threshold | 85 % |
| BR-20 overdue / abandoned vehicle | 24 h / 7 days |
| BR-22 price version = the one valid at entry; daily cap per calendar day; round up to 1,000 VND; no free period: the first block is owed from entry (team rule, section 10) | — |
| BR-25 / BR-26 review deadline; keep registration photos / gate photos | 24 h; 30 / 90 days |
| BR-28 account form (5 fields), Vietnamese phone `0` + 9 digits, unique email and phone | password: 6+ characters, 1 upper-case letter, 1 special character (team rule, section 10) |

The six fee examples at the end of SRS section 5 are the acceptance tests for pricing; keep a
unit test for each.

## 5. State machines (do not add transitions)

String constants live in `src/PMS.Application/Models/Operations/ParkingStatuses.cs`.

- **Reservation slot:** `Available → Reserved` (user picks, locked until the deposit deadline) →
  `Occupied` (check-in) → `Available` (exit or abnormal close). `Reserved → Available` on deposit
  timeout / cancel / no-show / lot cancel. `Available ⇄ Maintenance` by a Manager, only with no
  active booking. Walk-in bays have no state, only the zone counter.
- **Session:** `PendingEntry` (barrier open, waiting for the loop sensor) → `Active` →
  `PendingPayment` (fee requested at lane, counter or app; back to `Active` on cancel/timeout) →
  `Paid` (paid after deposit, or fee 0; back to `PendingPayment` when a pre-payment exceeds the
  exit window) → `Completed` (vehicle passed the barrier). Side paths: `Active → LostTicketPending
  → Active` (approved: the fine is added at check-out; rejected: the vehicle stays until new papers
  are approved); `Active → AbnormalClosed`
  (Maker-Checker); `Active → EmergencyExit` (Manager then waives or records debt). A turned-back
  car cancels the session (`Cancelled`) and the counter does not change. "Device failure" is not a
  state: it is the *manual* flag with a reason.
- **Reservation:** `PendingDeposit → Confirmed` (deposit in time) or `Expired` (late callbacks are
  refunded automatically). `Confirmed → CheckedIn → Completed`, or `Cancelled` (user),
  `LotCancelled` (100 % refund), `NoShow` (hold time passed). Changing arrival time or slot keeps
  it `Confirmed` and writes `ReservationChangeLog`.
- **Vehicle verification (SRS):** Pending review → Verified / Needs more info (user resubmits,
  max 3, BR-24) / Rejected (appeal → a *different* Manager reviews) / Disputed (plate already
  verified by another owner; Manager decides). Verified → Revoked (Manager, with reason) or
  Removed (user). In code (`VehicleVerificationStatus` in
  `src/PMS.Application/Models/VehicleDocuments/VehicleDocumentModels.cs`) this is `Draft →
  NeedsReview → Approved | Rejected`; `Pending`, `Processing`, `AutoVerified` are legacy OCR states.
- **Daily cash close:** NotClosed → Closed (Staff enters counted cash) → Confirmed (Manager), or
  Closed → HasDifference (over BR-12) → Confirmed after the Manager writes the reason. Cash taken
  after closing becomes an extra close for the same day.

## 6. Glossary (Vietnamese → English name to use in code)

| Vietnamese | English / code name |
| --- | --- |
| bãi xe / khu vực / slot | parking lot / zone / slot |
| zone đếm | counting zone (`ZoneCounter`) |
| slot đặt chỗ | reservation slot |
| xe vãng lai | walk-in vehicle (`WalkIn`) |
| làn / cổng | lane / gate |
| lượt gửi | parking session |
| đặt chỗ, cọc, cấn trừ cọc | reservation, deposit, deposit offset |
| cửa sổ đến / thời gian giữ | arrival window / hold time |
| vé, thẻ, mất vé | ticket, card, lost ticket (`LostTicketCase`) |
| barie | barrier |
| chốt tiền cuối ngày | daily cash close (`DailyCashClose`) |
| kiểm kê tồn bãi | inventory check (`InventoryCheck`) |
| điều chỉnh counter | zone counter adjustment |
| session "ma" | ghost session |
| đóng bất thường / đóng cưỡng bức | abnormal close / forced close |
| ra khẩn cấp, chế độ khẩn cấp | emergency exit, emergency mode |
| duyệt theo điều kiện | conditional approval |
| người đề xuất / người duyệt | maker / checker |
| hoàn tiền, miễn phí, hủy bill | refund, fee waiver, bill void (all `PaymentAdjustment` types) |
| cà vẹt | vehicle registration certificate (`RegistrationCertificate`) |
| CCCD | citizen ID (`CitizenId`) |
| xác minh phương tiện, tranh chấp, thu hồi | vehicle verification, dispute, revocation |
| chủ quản | primary owner |
| sự cố | incident |
| hàng chờ xung đột | conflict queue |
| bảng giá, block, trần/ngày | pricing rule, time block, daily cap |

## 7. Get oriented in two minutes

Run these before planning anything, in **both** repos:

```bash
git status --short && git branch --show-current
git log --oneline -15 develop                  # what landed recently
git branch -a --list "*features/*"             # work in progress per feature / screen
git log develop..HEAD --oneline                 # what this branch adds
```

Then, in this repo:

```bash
ls src/PMS.API/Controllers                      # the API surface
grep -rn "\[Http\(Get\|Post\|Put\|Patch\|Delete\)" src/PMS.API/Controllers --include=*.cs | wc -l
dotnet build PMS.sln
dotnet test tests/PMS.Application.UnitTests
```

`docs/API_DOCUMENTATION.md` lists the endpoints; Swagger runs at `/swagger` in Development.
In Development the API also seeds demo accounts (passwords: Managers `Manager123@`, Staff `Staff123@`, customers `Demo@123`) and a month of parking
activity for manual testing: see `docs/DEMO_DATA.md`.
If a requirement has no controller in the table in section 3 it is not built yet: check the
frontend, which may be running on mock data (`src/mocks`, `useDemo*` hooks).

## 8. Git workflow (both repos)

Branch name: `features/<feature-or-screen-name>` in lower-case kebab case, in English, e.g.
`features/daily-cash-close`, `features/staff-gate-screen`, `features/reservation-deposit`.
Use the **same branch name in both repos** when one feature touches the API and the UI, even if
one side has only a small change.

```bash
# 1. start: in each repo that the task touches
git checkout develop && git pull origin develop
git checkout -b features/<name>            # or: git checkout features/<name> if it exists

# 2. work, then commit in English (Conventional Commits)
git add <files> && git commit -m "feat(reservations): deduct the deposit at check-out"

# 3. finish every coding task by merging into develop
dotnet build PMS.sln && dotnet test tests/PMS.Application.UnitTests   # frontend: npm run lint && npm run build
git checkout develop && git pull origin develop
git merge --no-ff features/<name>
git push origin develop
git checkout features/<name>               # keep working on the branch afterwards
```

- Check the branch before the first edit of a task; if you are on `develop`, create or switch to
  the feature branch first. Carry uncommitted work along (`git checkout -b` keeps it).
- Commit types: `feat`, `fix`, `refactor`, `test`, `docs`, `chore`, `ci`, `style`; the scope is
  the module (`auth`, `lots`, `gate`, `reservations`, `payments`, `verification`, `layout`…).
- Merge only after build and tests pass. On a merge conflict, resolve it on the feature branch
  (`git merge develop` there), test again, then merge.
- Do not rewrite shared history (`push --force`, rebase of pushed branches) and do not touch
  `main`, `master` or `production`; releases to `production` are done by the team.
- The API and the UI must agree: when an endpoint or DTO changes, update the frontend types
  (`src/types/*.dto.ts`) and service on the same-named branch, and merge both repos.

## 9. This repo

- .NET 9, ASP.NET Core, EF Core (SQL Server), ASP.NET Identity (`dbo.Users`, `AspNetRoles`…;
  never recreate Identity tables), SignalR hubs under `/api/hubs/*`, Hangfire jobs in
  `src/PMS.Application/Jobs`, xUnit + NSubstitute tests, Coverlet coverage.
- Layers: `PMS.API` (controllers, hubs, filters, middleware) → `PMS.Application` (services,
  validators, models/DTOs, jobs) → `PMS.DataAccess` (DbContext partials, configurations,
  migrations, repositories) → `PMS.Core` (entities) ; `PMS.Shared` for cross-cutting services.
  Controllers stay thin; rules live in services and validators.
- Status values are string constants in `ParkingStatuses.cs` — reuse them, do not inline strings.
- Migrations: add with EF Core in `PMS.DataAccess`; never edit an applied migration.
- Demo data (`Helpers/DemoData/DemoDataSeed`, `"Seed:DemoData"`): when a feature changes how sessions,
  bookings, payments or wallets are written, update the seed the same way so the demo stays consistent
  (`DemoDataSeedTests` checks the counters, ledgers and payments).
- Tests: `tests/PMS.Application.UnitTests` (services, validators) and
  `tests/PMS.Api.IntegrationTests` (`WebApplicationFactory`). Coverage:
  `dotnet test tests/PMS.Application.UnitTests --settings coverlet.runsettings --collect:"XPlat Code Coverage" --results-directory TestResults`
  (see `tests/README.md`). Add or update a unit test with every rule you implement.
- CI: `.gitlab-ci.yml` builds and tests every push; pushing `production` deploys to Azure
  (`docs/CI-CD.md`).
- Secrets: never commit real keys or connection strings; `appsettings.Development.json` is for
  local values only.

## 10. Known differences from the SRS

Keep this list current; it is the first place to look when code and SRS disagree.

- Parking simulation (team addition, 2026-10-29, for demos): the Admin screen `/admin/simulation`
  (`AdminSimulationController`, `api/admin/simulation`, saved in `SystemSettings` key `Parking.Simulation`)
  runs every lot in seconds. While it is on, one pricing block of every rule lasts N seconds at the same
  block price with **no daily cap** (`FeeCalculator.CalculateSimulated`), and the deposit / no-deposit
  holds, deposit deadline, early check-in, free-cancellation deadline and exit window are seconds
  (`ParkingOptions.HoldTime`, `DepositPaymentTime`, `EarlyArrivalTime`, `CancelRefundTime`,
  `ExitWindowTime`). `ParkingSimulationTicker` runs the deadline checks every second. Turning it off
  restores the normal rules and the Manager's pricing; bookings made while it was on keep their short times.
- Walk-in simulation (team addition, 2026-11-03, for demos): the same Admin screen sends walk-ins with random
  plates (real format, never a registered, parked or listed plate) through a lot's real entry lane
  (`WalkInSimulationService`, `api/admin/simulation/walk-ins`) and parks each in a random free bay of its type
  (as when Staff picks a bay at the lane), so lists and the 3D map show its bay. Each stays a random time (one hour = one pricing
  block while the simulation is on), then pays cash at the exit lane and leaves by itself (`ParkingJobs` minutely
  run and simulation tick). Their sessions carry `IsSimulated`; fees, payments and counters are real and nothing
  is deleted. The screen can also report a lost ticket for a simulated vehicle and park a walk-in in a booked bay.
- Daily cash close (BR-12, built 2026-11-03): one `DailyCashClose` row per Staff drawer of a lot and business day
  (`CashDrawerService`, `/api/staff/lots/{id}/cash-drawer`, `/api/manager/…/cash-closes`). The Staff opens it with a
  counted float (or it opens on its own, empty, with their first cash payment; cash after a close opens drawer 2, 3…).
  At the lane the Staff can tap the banknotes handed over; the payment then stores the notes received and the change
  (`Payment.CashReceivedJson` / `CashChangeJson`) and the screen suggests change from the drawer. The close is a blind
  count; a difference over 20,000 VND needs a reason (`HasDifference`) and the lot's Manager confirms every close
  (never their own) in the Approval Queue, with a note when it is off. Each drawer must close by 23:00 lot time
  (`CloseDueAt`; a drawer opened later gets an hour): Staff see a countdown banner, and a drawer still open after it
  is flagged (`OverdueAt`) and the lot's Manager is alerted (`CASH_*` OpsAlerts on the lot channel, checked by the
  minutely job and the simulation tick). The Admin simulation can end the business day now with a short grace
  period (`POST /api/admin/simulation/end-business-day`). Not built: escalation to the Admin when a lot has no Manager.
- Cash for a drawer (team addition, 2026-11-04): a Staff short of banknotes (usually change) asks the lot's Manager
  for a list of notes from the Cash Tally page or, when the drawer cannot make change, from the POS
  (`CashTopUpRequests` table, `CashDrawerService.RequestTopUpAsync`, `POST /api/staff/lots/{id}/cash-drawer/top-ups`;
  one waiting request per drawer, the Staff can cancel it). The Manager approves or rejects it (with a reason) in
  the Approval Queue (`/api/manager/lots/{id}/cash-top-ups`, `.../cash-top-ups/{id}/approve|reject`; never their own
  request, only while the drawer is open). Approved notes count as cash put into that drawer: they are added to
  its expected notes and expected total (`TopUpTotal`), so the close balances when the Staff counts them.
- Booked bay taken (BR-08): when a booking checks in and another vehicle is in its bay, the entry lane moves the
  booking to a free bay of its type and waives the first hour (`ParkingSession.FirstHourWaived`; one block while
  simulating), or, with no bay left, lets it park as a walk-in and refunds the whole deposit.
- The 3D / 2D map draws walk-ins: the 2D plan carries each zone's live counter (`ZoneCounters`) and the frontend
  fills free bays of the zone in a fixed order (`walkInBays` in `src/lib/lot-layout.ts`).
- No free period (team decision, 2026-10-25): the first block (the first hour by default, or the rule's
  first block) is owed as soon as the vehicle enters, so a stay is never charged 0 VND. This replaces
  BR-09 / BR-22's grace period: `FeeCalculator` ignores `GraceMinutes` and the pricing page no longer
  offers it. Only whitelisted plates, waivers and turned-back vehicles (cancelled sessions) pay nothing.
- Only three vehicle types (team decision, 2026-10-25): `CAR_4` (4-seat car), `CAR_7` (7-seat car)
  and `MOTORBIKE`. No bicycles, e-bikes or EVs: the `BICYCLE` row is kept but inactive (old data may
  point to it), there are no EV chargers, and pricing has one rule per type. A vehicle without a
  readable plate still needs the lane's vehicle type at check-in.
- Pricing rules store an overnight surcharge and an overstay surcharge, but the gate's `FeeCalculator`
  does not apply them yet, so the Manager pricing page does not offer them. "Price by the hour" is the
  `DayNight` charge model: a block costs the day or night price of the hour it starts in.
- Saving a pricing rule edits it in place (new `VersionNo`, old values in `PricingRuleHistory`), and the
  price a stay is charged is frozen at entry (BR-22): `ParkingSession.PriceSnapshotJson` is copied from the live rule
  at check-in. Sessions from before the snapshot existed fall back to the live rule.
- Price changes (user request, 2026-11-04; replaces the earlier "wait for an empty lot" team decision): a new price
  is live as soon as the Manager saves it. Vehicles already inside keep the price they entered with; every vehicle
  entering afterwards pays the new one, including one booked before the change (`Reservation.PriceSnapshotJson` is
  legacy, no longer written or read). Saving a new price on an Active rule tells every active customer account
  (`ICustomerAudience`) in the notification bell through `NotificationJobs` (`PriceChanged`). Rules shared by the
  whole system (`ParkingLotId == null`) cannot be changed by a Manager. The old `PendingPriceChanges` table is left
  in existing databases and no code reads it. Every entry/exit/close path must go through `LotOccupancy`;
  `ParkingLot.RowVersion` serialises a price change with vehicles entering.
- Deposit is optional when booking (team decision, 2026-10-24). With a deposit (BR-06, paid by
  wallet, VNPay or MoMo) the booking waits in `PendingDeposit` and the slot is held **30 minutes**
  after arrival (`Parking:Reservations:DepositHoldMinutes`). Without a deposit
  (`CreateReservationModel.PayDeposit = false`) it is `Confirmed` at once, `DepositAmount` is 0 and
  the slot is held only **15 minutes** (`HoldMinutes`, the SRS BR-02 value). BR-02's single hold time
  no longer applies to deposit bookings.
- Online wallet top-ups (VNPay / MoMo) are capped at **10,000,000 VND per customer per calendar day**
  (Vietnam time, `Parking:Wallet:DailyTopUpLimit`); paid and still-payable top-up links of the day
  both count. The wallet itself is a team addition (see below).
- Password rule (confirmed by the team on 2026-10-24, keep it): at least 6 characters with at
  least one upper-case letter and at least one special character, for every account. It replaces
  BR-28's rule (8+ characters with upper, lower and digit); do not switch to the SRS version.
  The single source is `UserValidatorConfiguration` (`MinimumPasswordLength`, `PasswordRegex`);
  the frontend uses the same rule in `src/lib/password.ts`.
- Legacy entities from earlier SRS versions still exist (`Shift*`, `Subscription*`, `Voucher`,
  `Einvoice`, `OtpCode`…). They are out of scope in v1.4: do not extend them for new features.
- The user wallet (`WalletController`, `Wallet*` entities) is a team addition: the SRS lists an
  internal wallet as out of scope (2.5), but it is in active use for payments.
- Lot staffing (team decision, 2026-10-26): a lot has exactly one Manager (`ParkingLots.ManagerUserId`)
  and any number of Staff. Assigning a lot that another *active* Manager runs is refused; a deactivated
  Manager's lot can be handed over. An `Active` lot without an active Manager **and** at least one active
  Staff is reported as `Maintenance` (`LotStaffing`, worked out on every read; `StoredStatus` keeps the
  Admin's value): customers can still see it on the public list and 3D map, but bookings are refused. A
  new lot therefore starts in maintenance until its Manager adds Staff.
- Motorbikes park at once (team decision, 2026-10-30): BR-01 allows cars only, but `MOTORBIKE` is now
  in `ReservableVehicleTypeCodes` and in `InstantVehicleTypeCodes`. For these types `CreateAsync` ignores
  the times and the deposit: the arrival is now, pay-as-you-go, no deposit, `Confirmed` at once and the bay
  is held now for `HoldMinutes`. The customer app then sends a check-in request, so Staff sees the plate
  at the entry lane; the fee is paid at check-out. Cars still book ahead as before.
- Bookings have two modes (`BookingMode`): `PayAsYouGo` follows the SRS (arrival time only);
  `Scheduled` also takes a departure time, which the SRS does not have.
- Vehicle verification has no "needs more info", "disputed" or "revoked" state yet (section 5).
- Lost tickets (FR-OPS-05) have no `LostTicketCase` table: each report is a `SessionViolations` row
  (`ViolationType = LostTicket`) with `Status` (Pending / Approved / Rejected), `ApprovalMode`
  (Conditional / Manager / Staff), `DecidedBy`, `DecidedAt` and `DecisionNote`. The reporter can never decide
  the case, and a rejected case blocks every payment point and the exit until a new case is approved.
  Only walk-ins can report a lost ticket (team decision, 2026-11-03): a booked vehicle has no paper ticket
  (booking QR, verified plate or exit pass), so the API refuses it. Staff start the case from Vehicles in the lot
  (Lost ticket) or the Exceptions desk.
  Not built yet: the 10-minute approval deadline with escalation to Admin (BR-14), the Manager's 24-hour
  review of conditional approvals (BR-13; those rows keep `IsResolved = false`), a stored BR-13 checklist
  (only the `ConditionsMet` flag), and locking the lost ticket QR.
- Exit barrier only at the Check-out lane (team decision, 2026-11-04): the Staff POS (`/staff/checkout`) only
  takes the fee; it has no barrier button. Every paid vehicle of the lot (paid at the POS, in the app or 0 VND,
  walk-ins included) gets a waiting check-out `GateRequest` (`GateRequestService.QueuePaidVehiclesAsync`, run when
  the queue is read), shown in the Check-out tab of the gate dashboard, where the Staff opens the barrier (the
  normal exit checks run). Walk-in requests have no `UserId` / `CustomerId`; a customer's request carries their
  account, so the app shows it. Simulated walk-ins are skipped, and a request refused or cancelled after the last
  payment is not opened again.
- Lost ticket papers (team decision, 2026-11-04): Lost ticket (Vehicles in the lot on the gate dashboard, or the
  Exceptions desk after a plate lookup) opens a popup where the Staff uploads photos of the driver's Citizen ID and
  vehicle registration certificate (JPEG / PNG / PDF, 8 MB each; `POST /api/staff/sessions/{id}/lost-ticket/papers`,
  stored under `lost-tickets/{sessionId}`, paths on the `SessionViolations` row). The case waits with
  `ApprovalMode = Staff` (no Manager alert) and the Exceptions screen shows the papers for the Staff to check and
  approve or disapprove (`POST .../lost-ticket/review`; disapproving needs a note). The Staff at the counter decides
  this case, unlike the Maker-Checker rule for the other lost-ticket paths. Every decision leaves the vehicle parked
  (`Active`): an approval adds the lost-ticket fine to the bill at check-out; a disapproval keeps the vehicle in
  until new papers are uploaded and approved.
- Some older code comments quote the Vietnamese SRS terms (e.g. in `ParkingStatuses.cs`).
  Translate them to English when you touch those lines; do not add new ones.
- Not built yet in the API (as of 2026-10-24): pricing management, reports, the approval queue
  (`ApprovalRequest`), inventory check, incidents, emergency mode, audit log
  viewer. Re-check with the commands in section 7 before relying on this line.
