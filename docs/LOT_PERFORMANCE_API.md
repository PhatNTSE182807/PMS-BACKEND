# Lot Performance & Revenue API

Endpoints that report how the parking lots perform: occupancy, revenue and sessions. Admin endpoints
cover every lot in the network; the Manager endpoint covers only the lots the signed-in Manager runs.

| Method | Route | Role | Purpose |
| --- | --- | --- | --- |
| GET | [`/api/admin/dashboard/overview`](#1-get-apiadmindashboardoverview) | Admin | Network headline numbers |
| GET | [`/api/admin/dashboard/hubs`](#2-get-apiadmindashboardhubs) | Admin | Per-lot performance (occupancy, today and month-to-date revenue) |
| GET | [`/api/admin/reports/revenue`](#3-get-apiadminreportsrevenue) | Admin | Revenue report for a date range, all lots or one lot |
| GET | [`/api/admin/reports/revenue/timeseries`](#3b-get-apiadminreportsrevenuetimeseries) | Admin | The same revenue per day, week, month or year, by vehicle type and lot |
| GET | [`/api/manager/reports/revenue`](#4-get-apimanagerreportsrevenue) | Manager | Same report, limited to the Manager's own lots |

Source code:

| Endpoint | Controller | Service |
| --- | --- | --- |
| Dashboard | `src/PMS.API/Controllers/AdminDashboardController.cs` | `AdminDashboardService`, `AdminDashboardQueries` |
| Revenue | `AdminRevenueController.cs`, `ManagerRevenueController.cs` | `RevenueReportService` |

---

## Common rules

### Authentication

Every endpoint needs a JWT bearer token:

```http
Authorization: Bearer <access-token>
```

| Situation | Status |
| --- | --- |
| No or invalid token | `401 Unauthorized` |
| Signed in with the wrong role | `403 Forbidden` |

### Response envelope

Every response is wrapped in `ApiResult<T>` and uses camelCase property names.

```json
{
  "succeeded": true,
  "result": { },
  "errors": null
}
```

Errors use the same envelope with `succeeded: false`, `result: null` and a list of messages:

```json
{
  "succeeded": false,
  "result": null,
  "errors": ["'endDate' cannot be before 'startDate'."]
}
```

### What counts as revenue

All four endpoints use the same rule:

- **Revenue** is `ParkingSession.FinalFee` (VND) of **settled** sessions only:
  - `Completed` sessions (the vehicle passed the exit barrier), dated by `ExitTime`;
  - `Paid` sessions not yet exited (pre-paid at the counter or in the app), dated by `UpdatedAt`.
- Sessions in `PendingEntry`, `Active`, `PendingPayment`, `LostTicketPending` and `Cancelled` are excluded.
- `FinalFee` already includes the deposit offset (`FinalFee = DepositApplied + PaidAmount`).
- Payment adjustments (refunds, waivers, bill voids) are **not** subtracted.
- Lots that are soft-deleted (`IsDeleted`) are left out.

### Time zone

Day boundaries are local parking time (the default parking time zone, Vietnam time), not UTC. "Today" is
00:00 to 24:00 local time.

---

## 1. GET `/api/admin/dashboard/overview`

Headline numbers for the whole network, for the top cards of the Admin dashboard.

**Role:** Admin
**Query parameters:** none

### Response `200 OK` — `DashboardOverviewDto`

| Field | Type | Description |
| --- | --- | --- |
| `totalGrossRevenue` | decimal | **All-time** settled revenue (VND) of every lot that is not deleted |
| `networkLoadFactor` | double | `occupiedBays / totalBays × 100`, rounded to 1 decimal; `0` when there are no bays |
| `totalBays` | int | Bays of all lots (see [How bays are counted](#how-bays-are-counted)) |
| `occupiedBays` | int | Bays in use right now |
| `activeUsers` | int | Active customer accounts (role `User`) |
| `activeStaff` | int | Active Manager **and** Staff accounts |

### Example

```http
GET /api/admin/dashboard/overview
Authorization: Bearer <admin-token>
```

```json
{
  "succeeded": true,
  "result": {
    "totalGrossRevenue": 184520000,
    "networkLoadFactor": 63.4,
    "totalBays": 820,
    "occupiedBays": 520,
    "activeUsers": 1240,
    "activeStaff": 37
  },
  "errors": null
}
```

---

## 2. GET `/api/admin/dashboard/hubs`

Performance of each parking lot ("hub"): live occupancy plus today's and month-to-date revenue.

**Role:** Admin
**Query parameters:** none

### Response `200 OK` — `HubPerformanceDto[]`

One item per lot that is not deleted, including lots with no bays or no revenue (their numbers are `0`).
Sorted by `mtdRevenue` (highest first), then by `lotName`.

| Field | Type | Description |
| --- | --- | --- |
| `lotId` | int | Parking lot ID |
| `lotName` | string | Lot name |
| `location` | string | `Address, District, City` (empty parts skipped) |
| `totalBays` | int | Bays of the lot |
| `occupiedBays` | int | Bays in use right now |
| `occupancyRate` | double | `occupiedBays / totalBays × 100`, rounded to 1 decimal; `0` when there are no bays |
| `todayRevenue` | decimal | Settled revenue (VND) since 00:00 today, local time |
| `mtdRevenue` | decimal | Settled revenue (VND) since the 1st of the current month, local time, up to the end of today |

### Example

```http
GET /api/admin/dashboard/hubs
Authorization: Bearer <admin-token>
```

```json
{
  "succeeded": true,
  "result": [
    {
      "lotId": 1,
      "lotName": "XXIV Central",
      "location": "12 Nguyen Hue, District 1, Ho Chi Minh City",
      "totalBays": 300,
      "occupiedBays": 241,
      "occupancyRate": 80.3,
      "todayRevenue": 4250000,
      "mtdRevenue": 98700000
    },
    {
      "lotId": 3,
      "lotName": "XXIV Riverside",
      "location": "88 Ton Duc Thang, District 1, Ho Chi Minh City",
      "totalBays": 120,
      "occupiedBays": 0,
      "occupancyRate": 0,
      "todayRevenue": 0,
      "mtdRevenue": 0
    }
  ],
  "errors": null
}
```

### How bays are counted

Used by both dashboard endpoints. Only **active zones** of lots that are not deleted are counted.

- A zone **with a zone counter** counts `ZoneCounter.TotalCapacity` as its bays and
  `ZoneCounter.OccupiedSpaces` as occupied. This is the live number and includes walk-in vehicles.
- A zone **without a counter** falls back to its slots: every active slot is a bay, and a slot with status
  `Occupied` is a used bay.

Bays in maintenance count toward the total if the zone counter's `TotalCapacity` includes them.

---

## 3. GET `/api/admin/reports/revenue`

Revenue and session count for a date range, by lot and by payment method.

**Role:** Admin

### Query parameters

| Name | Type | Required | Description |
| --- | --- | --- | --- |
| `startDate` | string `yyyy-MM-dd` | No* | First day, inclusive |
| `endDate` | string `yyyy-MM-dd` | No* | Last day, inclusive |
| `parkingLotId` | int | No | Report a single lot; omit to report all lots |
| `vehicleType` | string | No | `ALL` (default), `CAR` (CAR_4 and CAR_7) or `MOTORBIKE`; anything else is `400` |

\* Send **both** dates or **neither**. With neither, the report covers **today**.
The range is `[startDate 00:00, endDate + 1 day 00:00)` in local time. When `parkingLotId` is given, the lot's
own time zone is used.

### Response `200 OK` — `AdminRevenueReportResponse`

| Field | Type | Description |
| --- | --- | --- |
| `startDate` | string | Resolved start date (`yyyy-MM-dd`) |
| `endDate` | string | Resolved end date (`yyyy-MM-dd`) |
| `systemTotalRevenue` | decimal | Settled revenue (VND) in the range |
| `systemTotalSessions` | int | Settled sessions in the range (sessions with a 0 fee count too) |
| `activeLotsCount` | int | Number of lots in the report (lots that are not deleted, whatever their status) |
| `byLot` | `ParkingLotRevenueModel[]` | One row per lot, sorted by name; lots with no revenue appear with `0` |
| `paymentMethods` | `PaymentMethodBreakdownModel[]` | Revenue split by payment method |
| `byVehicleType` | `{ vehicleType, totalRevenue, totalSessions }[]` | `CAR` and `MOTORBIKE` always; `OTHER` (session without a vehicle type) only when above 0 |

#### `ParkingLotRevenueModel`

| Field | Type | Description |
| --- | --- | --- |
| `parkingLotId` | int | Lot ID |
| `parkingLotName` | string | Lot name |
| `totalRevenue` | decimal | Settled revenue (VND) of the lot in the range |
| `totalSessions` | int | Settled sessions of the lot in the range |
| `managerUserId` | string \| null | The lot's Manager, `null` when it has none |

#### `PaymentMethodBreakdownModel`

| Field | Type | Description |
| --- | --- | --- |
| `method` | string | `QR_BANKING`, `RFID_WALLET`, `CASH` or `OTHER` |
| `amount` | decimal | Revenue (VND) collected by this method |
| `percentage` | decimal | Share of the total revenue, rounded to 1 decimal; `0` when the total is 0 |

`QR_BANKING`, `RFID_WALLET` and `CASH` are always returned, in that order. `OTHER` is added only when it is
greater than 0.

| `method` | Comes from (`Payment.PaymentMethod`) |
| --- | --- |
| `QR_BANKING` | `Online` (VNPay, MoMo) |
| `RFID_WALLET` | `Wallet` |
| `CASH` | `Cash` |
| `OTHER` | Any other method, or revenue with no matching successful payment |

How each session's `FinalFee` is split:

1. `PaidAmount` goes to the method(s) of the session's successful `ParkingFee` payments. When there are several
   payments, it is split in proportion to their amounts.
2. `DepositApplied` goes to the method(s) of the reservation's successful `Deposit` payments, split the same way.
3. If `FinalFee` is larger than `DepositApplied + PaidAmount`, the rest goes to `OTHER`.

### Errors

| Status | When | Message |
| --- | --- | --- |
| `400` | Only one of the two dates was sent | `Both 'startDate' and 'endDate' must be provided, or both omitted.` |
| `400` | A date is not `yyyy-MM-dd` | `Invalid 'startDate' format. Expected yyyy-MM-dd.` (or `'endDate'`) |
| `400` | `endDate` is before `startDate` | `'endDate' cannot be before 'startDate'.` |
| `404` | `parkingLotId` does not exist or is deleted | `Parking lot not found.` |

### Example

```http
GET /api/admin/reports/revenue?startDate=2026-10-01&endDate=2026-10-29
Authorization: Bearer <admin-token>
```

```json
{
  "succeeded": true,
  "result": {
    "startDate": "2026-10-01",
    "endDate": "2026-10-29",
    "systemTotalRevenue": 152300000,
    "systemTotalSessions": 6120,
    "activeLotsCount": 2,
    "byLot": [
      {
        "parkingLotId": 1,
        "parkingLotName": "XXIV Central",
        "totalRevenue": 98700000,
        "totalSessions": 3950,
        "managerUserId": "8f1c2d3e-0000-0000-0000-000000000001"
      },
      {
        "parkingLotId": 2,
        "parkingLotName": "XXIV Riverside",
        "totalRevenue": 53600000,
        "totalSessions": 2170,
        "managerUserId": null
      }
    ],
    "paymentMethods": [
      { "method": "QR_BANKING", "amount": 70100000, "percentage": 46.0 },
      { "method": "RFID_WALLET", "amount": 31200000, "percentage": 20.5 },
      { "method": "CASH", "amount": 51000000, "percentage": 33.5 }
    ]
  },
  "errors": null
}
```

---

## 3b. GET `/api/admin/reports/revenue/timeseries`

The settled revenue of section 3, cut into periods, for the trend chart and the per-period table.

**Role:** Admin

| Name | Type | Required | Description |
| --- | --- | --- | --- |
| `startDate`, `endDate`, `parkingLotId`, `vehicleType` | | | Same as section 3 |
| `granularity` | string | No | `DAY`, `WEEK` (Monday to Sunday), `MONTH` or `YEAR`. Omitted: `DAY` up to 31 days, `WEEK` up to 16 weeks, `MONTH` up to 24 months, else `YEAR` |

Response `AdminRevenueTimeSeriesResponse`: `startDate`, `endDate`, `granularity` and `points[]`. Every period of the range is
returned, oldest first, empty ones included; the first and last are clipped to the range. A session counts on the local day of
its exit (Completed) or payment (Paid, not yet out).

| Point field | Description |
| --- | --- |
| `startDate`, `endDate` | First and last day of the period (inclusive) |
| `totalRevenue`, `totalSessions` | Settled revenue and sessions of the period |
| `carRevenue`, `carSessions`, `motorbikeRevenue`, `motorbikeSessions` | The same, per vehicle family |
| `byLot[]` | `parkingLotId`, `parkingLotName`, `totalRevenue`, `totalSessions`, `carSessions`, `motorbikeSessions` of lots with sessions in the period |

Errors: the `400` / `404` of section 3, plus `400 Invalid 'granularity'. Use DAY, WEEK, MONTH or YEAR.`

---

## 4. GET `/api/manager/reports/revenue`

The same report as [section 3](#3-get-apiadminreportsrevenue), limited to the lots whose
`ParkingLots.ManagerUserId` is the signed-in Manager. The Manager ID is read from the token, never from the
request.

**Role:** Manager

### Query parameters

Same as [section 3](#query-parameters): `startDate`, `endDate`, `parkingLotId`.

### Response `200 OK` — `ManagerRevenueReportResponse`

| Field | Type | Description |
| --- | --- | --- |
| `managerUserId` | string | The signed-in Manager |
| `startDate` | string | Resolved start date (`yyyy-MM-dd`) |
| `endDate` | string | Resolved end date (`yyyy-MM-dd`) |
| `totalRevenue` | decimal | Settled revenue (VND) of the Manager's lots in the range |
| `totalSessions` | int | Settled sessions of the Manager's lots in the range |
| `byLot` | `ParkingLotRevenueModel[]` | One row per managed lot |
| `paymentMethods` | `PaymentMethodBreakdownModel[]` | Revenue split by payment method |

A Manager with no lots gets `0` totals and empty `byLot` and `paymentMethods` lists, not an error.

### Errors

Same `400` errors as section 3, plus:

| Status | When | Message |
| --- | --- | --- |
| `403` | `parkingLotId` is not one of the Manager's lots | `You do not manage this parking lot.` |

---

## Frontend status (as of 2026-10-30)

| Endpoint | Frontend |
| --- | --- |
| `/api/admin/dashboard/overview`, `/hubs` | Called by `src/api/admin.service.ts` on `features/admin-dashboard` only; not merged into frontend `develop` |
| `/api/admin/reports/revenue`, `/timeseries` | Revenue Report page (`src/api/report.service.ts`, `pages/admin/RevenueReportPage`) |
| `/api/manager/reports/revenue` | Called by `src/api/report.service.ts` on frontend `develop` |

`VITE_MOCK_SERVICES` defaults to `capacity,reports`, so the Admin dashboard shows mock data unless that
variable is changed.

## Not built yet

The SRS reports module asks for more than these endpoints provide:

- Vehicles in and out per lot, average stay, peak hours.
- Daily cash close report per Staff per day.
- Exception, booking (no-show, cancellations, deposits kept and refunded) and vehicle verification reports.
- Excel / PDF export.
