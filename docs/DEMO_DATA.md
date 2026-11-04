# Demo data

Ready-made accounts and parking activity for testing every flow by hand: Admin → Manager → Staff →
customer, from registration papers to booking, gate entry, payment, lost tickets and exit.

Code: `src/PMS.Application/Helpers/DemoData/` (`DemoDataSeed`). Checks:
`tests/PMS.Api.IntegrationTests/Tests/DemoDataSeedTests.cs`.

## Turning it on

The API seeds the demo data at start-up when `Seed:DemoData` is `true`.

| Environment | Value |
| --- | --- |
| Development (`appsettings.Development.json`) | `true` |
| Everything else (`appsettings.json`), Testing | `false` |

- Accounts, lot staffing, lanes and prices are added when missing, on every start.
- The parking story (vehicles, sessions, bookings, payments, wallets, notifications) is written **once
  per database**, in one transaction. An audit log row with `Action = 'DemoDataSeeded'` marks it.
- It never takes a lot away from a Manager already assigned to it, and it skips the story (with a
  warning in the log) when one of its plates is already used in the database.
- Times are relative to the moment it runs. The time-bound cases below (a booking arriving in 10
  minutes, a 15-minute exit window) are only fresh right after seeding.
- To turn it on for a hosted test environment, set the variable `Seed__DemoData=true`. Never turn it on
  for a database real customers use.

### Starting again from fresh data

Drop your local database and start the API: migrations recreate it and the seed runs again. The API
prints the database it uses at start-up, e.g.
`[Database] Local database resolved: Server='.', Database='parking_management_system_<you>'`.

```bash
sqlcmd -S . -E -Q "ALTER DATABASE [parking_management_system_<you>] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [parking_management_system_<you>];"
```

Sample paper photos are stored under `src/PMS.API/App_Data/vehicle-documents/` (ignored by git); you
can empty that folder at the same time.

## Accounts

Demo passwords: Managers **`Manager123@`**, Staff **`Staff123@`**, customers **`Demo@123`**. Demo
Managers and Staff that still had `Demo@123` from an older seed are moved to their new password on the
next start. Internal accounts sign in with the username,
customers with the email (either works). Emails use `example.com`, so no mail is ever delivered.

The Admin comes from the base seed: `systemadmin` / `admin@gmail.com`, password `Admin123@`.

### Managers and Staff

| Username | Name | Role | Lot | Note |
| --- | --- | --- | --- | --- |
| `mgr.fptu` | Lê Minh Quân | Manager | FPT University | |
| `mgr.fsoft` | Phạm Ngọc Hân | Manager | Fsoft | |
| `mgr.conghoa` | Đặng Quốc Bảo | Manager | Cong Hoa | |
| `mgr.spare` | Trương Mỹ Linh | Manager | — | no lot yet: give her Ben Thanh |
| `mgr.former` | Huỳnh Văn Tài | Manager | Ben Thanh | **deactivated** |
| `staff.fptu1` | Nguyễn Thị Hoa | Staff | FPT University | |
| `staff.fptu2` | Trần Văn Long | Staff | FPT University | |
| `staff.fsoft1` | Lý Thanh Tâm | Staff | Fsoft | |
| `staff.fsoft2` | Võ Hoàng Nam | Staff | Fsoft | |
| `staff.conghoa1` | Phan Thị Mai | Staff | Cong Hoa | |
| `staff.conghoa2` | Bùi Đức Anh | Staff | Cong Hoa | **deactivated**: sign-in is refused |

### Customers

| Email | Name | Wallet | Vehicles (papers) | Use it to test |
| --- | --- | --- | --- | --- |
| `an.nguyen@example.com` | Nguyễn Văn An | ≥ 200,000 | 51K-123.45 car, 51H-678.90 7-seat, 59X1-234.56 motorbike (all approved) | parked car paid in the app; booking arriving now; the 2-booking limit |
| `binh.tran@example.com` | Trần Thị Bình | ≥ 200,000 | 30G-555.88 car (approved) | check-out of a booking: deposit deducted |
| `cuong.le@example.com` | Lê Hoàng Cường | none | 51F-246.80 car (waiting for Staff), 59T2-135.79 motorbike (draft: Citizen ID photo missing) | finishing an upload; Staff approving papers; first wallet top-up |
| `dung.pham@example.com` | Phạm Thu Dung | 100,000 | 43A-246.13 car (approved), 43A-777.99 7-seat (waiting: owner on the paper differs), 29A-111.22 (rejected, removed) | booking with a deposit from the wallet; Staff rejecting papers; the rejection notice |
| `em.vo@example.com` | Võ Minh Em | 50,000 | 51G-999.01 car (approved) | lost ticket; wallet too low for the fine |
| `giang.hoang@example.com` | Hoàng Gia Giang | ≥ 200,000 | 51L-202.02 car (approved) | exit window after paying in the app |
| `hai.do@example.com` | Đỗ Thanh Hải | ≥ 200,000 | 59C1-468.02 motorbike, 51A-864.20 car (approved) | check-out request from the app; long history |
| `khoa.mai@example.com` | Mai Đăng Khoa | none | none, no Citizen ID | a brand-new account: profile first, then a vehicle |

## Lots

| Lot | Model | People on duty | Lanes | Prices (active rule per vehicle type) |
| --- | --- | --- | --- | --- |
| FPT University (`LOT-FPTU-01`) | Wide deck, 3 × 50 | `mgr.fptu`, 2 Staff | IN-1, IN-2, OUT-1, OUT-2 | hourly blocks: car 20,000/h (cap 150,000), 7-seat 25,000/h, motorbike 5,000 per 4 h; car rule is version 2 (price change in its history) |
| Fsoft (`LOT-FSOFT-01`) | Tower, 4 × 30 | `mgr.fsoft`, 2 Staff | IN-1, OUT-1 | day 06:00-18:00 / night: car 25,000 / 15,000 per hour, 7-seat 30,000 / 20,000, motorbike 6,000 per 4 h |
| Cong Hoa (`LOT-CONGHOA-01`) | Compact deck, 3 × 20 | `mgr.conghoa`, 1 active Staff | IN-1, OUT-1 | first 2 h car 30,000 then 15,000/h, 7-seat 40,000 then 20,000/h, motorbike 5,000 then 2,000/h (at most 20,000/day) |
| Ben Thanh (`LOT-BENTHANH-01`) | Compact deck, 3 × 20 | none (its Manager is deactivated) | added on first use | none: the default tariff applies |

Ben Thanh is shown as **under maintenance** and refuses bookings until it has an active Manager and
Staff.

Plate lists: whitelist `51A-000.01` (FPT shuttle), `80A-000.99` (all lots), `51C-999.99` (Cong Hoa,
expired 2 days ago); blacklist `51K-666.66` (all lots), `30E-404.04` (Fsoft).

## What is going on right after seeding

| Lot | Plate | Situation |
| --- | --- | --- |
| FPT | 51K-123.45 | An's car, walk-in, about 3 h, on a bay |
| FPT | 51G-999.01 | Em's car, **lost ticket waiting for the Staff's papers check** (reported by `staff.fptu1`) |
| FPT | 51D-357.91 | guest car, about 1 h |
| FPT | 51B-468.24 | guest 7-seat, **over 24 h** (overdue alert) |
| FPT | 59P1-111.11 | guest motorbike |
| FPT | (no plate) | motorbike without a plate: the exit asks for a photo check |
| FPT | 51A-000.01 | whitelisted shuttle: free at the exit |
| Fsoft | 30G-555.88 | Bình, **parked on a booking** with a 30,000 deposit |
| Fsoft | 59C1-468.02 | Hải's motorbike, **check-out request waiting** in the lane queue |
| Fsoft | 61A-135.24 | guest car, **8 days** (abandoned incident; a ghost session to close) |
| Fsoft | 51E-246.80 | guest 7-seat, **lost-ticket case rejected**: no payment point takes the fee |
| Cong Hoa | 51L-202.02 | Giang, **paid in the app 5 minutes ago**: 10 minutes of exit window left |
| Cong Hoa | 59T1-777.77 | guest motorbike |

Bookings: An's 7-seat **arrives at Fsoft 10 minutes after seeding** (deposit paid, bay held, check-in
request already in the queue; the hold ends 40 minutes after seeding). An also has a scheduled booking
at Cong Hoa tomorrow 08:00-11:00 without a deposit, so he is at the limit of two active bookings.

History: about 30 days of finished visits in the three lots (cash and online at the lane, wallet and
online in the app), past bookings ending each way (completed, cancelled with refund, cancelled by the
lot, no-show, deposit expired), an approved lost ticket, one approved on the spot, a ghost session
closed by the Manager, a car that turned around at the barrier, a missed exit window paid with the
late fee, wallet top-ups and refunds, and the customers' notifications.

## Flows to try

**Admin** (`systemadmin`)
1. Lots list: Ben Thanh is under maintenance.
2. Managers: give Ben Thanh to `mgr.spare` (allowed because `mgr.former` is deactivated); try giving
   FPT University to her (refused: it has an active Manager).

**Manager**
1. `mgr.spare`: add a Staff account at Ben Thanh; the lot becomes bookable.
2. `staff.fptu1` (Exceptions & Alerts): check Em's papers and approve or disapprove the lost ticket. Managers no longer decide lost tickets.
3. `mgr.fsoft`: close the 8-day-old 61A-135.24 session abnormally; cancel a booking of the lot.
4. Pricing page: see the rules, simulate a fee, read the FPT car rule's history.
5. Plate lists: add or remove a whitelist / blacklist entry.

**Staff**
1. `staff.fptu1`, exit lane: 51D-357.91 (cash), 51A-000.01 (free, whitelisted), the plate-less
   motorbike (photo check), 51B-468.24 (more than one day, capped per day).
2. `staff.fsoft1`: check out Bình's 30G-555.88 (deposit deducted); handle Hải's check-out request and
   An's check-in request; enter 30E-404.04 (blacklisted) or 51D-357.91 at another lot (already parked).
3. `staff.fsoft2`: 51E-246.80 cannot be paid; report the lost ticket again and decide it in Exceptions & Alerts.
4. Vehicle verification: approve Cường's 51F-246.80; reject Dung's 43A-777.99 (the owner on the paper
   is someone else).
5. `staff.conghoa1`: Giang's 51L-202.02 leaves free inside the exit window; after it, a late fee is due.

**Customer**
1. An: current session and fee so far, pay from the wallet, exit pass; booking QR; try a third booking
   (refused).
2. Dung: book a bay at Cong Hoa with a deposit paid from the wallet; read the rejection notice.
3. Cường: add the Citizen ID photo to the motorbike and submit; top up the wallet (VNPay/MoMo are
   simulated in Development).
4. Em: try paying the lost-ticket fine from a 50,000 wallet.
5. Khoa: save a Citizen ID in the profile, then register a vehicle.
6. Hải: parking history and receipts; notifications.
