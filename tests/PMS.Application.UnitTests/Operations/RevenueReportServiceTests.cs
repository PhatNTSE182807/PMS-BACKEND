using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.Operations;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

public class RevenueReportServiceTests : IDisposable
{
    private readonly PmsDbContext _db;
    private readonly RevenueReportService _service;

    public RevenueReportServiceTests()
    {
        var options = new DbContextOptionsBuilder<PmsDbContext>()
            .UseInMemoryDatabase($"RevenueTest_{Guid.NewGuid()}")
            .Options;
        _db = new PmsDbContext(options);
        _service = new RevenueReportService(_db);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private ParkingLot CreateLot(int id, string name, string managerUserId = "mgr-1", bool isDeleted = false, string tz = "SE Asia Standard Time")
    {
        var lot = new ParkingLot
        {
            Id = id,
            Code = $"LOT_{id}",
            Name = name,
            Address = "123 Test Street",
            Status = "Active",
            ManagerUserId = managerUserId,
            IsDeleted = isDeleted,
            TimeZoneId = tz,
            CreatedAt = DateTime.UtcNow
        };
        _db.ParkingLots.Add(lot);
        _db.SaveChanges();
        return lot;
    }

    private ParkingSession CreateSession(
        long id,
        int lotId,
        decimal finalFee,
        decimal depositApplied,
        decimal paidAmount,
        string status,
        DateTime? exitTime,
        DateTime? updatedAt = null,
        long? reservationId = null)
    {
        var session = new ParkingSession
        {
            Id = id,
            SessionCode = $"SS_{id}",
            ParkingLotId = lotId,
            EntryGateId = 1,
            EntryPlateNumber = "51A-12345",
            EntryPlateNormalized = "51A12345",
            RowVersion = new byte[] { 1, 2, 3 },
            EntryTime = exitTime?.AddHours(-2) ?? DateTime.UtcNow.AddHours(-2),
            ExitTime = exitTime,
            OriginalFee = finalFee,
            FinalFee = finalFee,
            DepositApplied = depositApplied,
            PaidAmount = paidAmount,
            Status = status,
            UpdatedAt = updatedAt ?? exitTime ?? DateTime.UtcNow,
            ReservationId = reservationId
        };
        _db.ParkingSessions.Add(session);
        _db.SaveChanges();
        return session;
    }

    private Payment CreatePayment(
        long id,
        long? sessionId,
        long? reservationId,
        int? lotId,
        string purpose,
        string method,
        decimal amount,
        string status = PaymentStatus.Succeeded,
        DateTime? paidAt = null)
    {
        var payment = new Payment
        {
            Id = id,
            PaymentCode = $"PM_{id}",
            InvoiceId = id,
            SessionId = sessionId,
            ReservationId = reservationId,
            ParkingLotId = lotId,
            Purpose = purpose,
            PaymentMethod = method,
            Amount = amount,
            Currency = "VND",
            Status = status,
            PaidAt = paidAt ?? DateTime.UtcNow,
            CreatedAt = paidAt ?? DateTime.UtcNow
        };
        _db.Payments.Add(payment);
        _db.SaveChanges();
        return payment;
    }

    [Fact]
    public async Task FinalFee_IsCountedOnce_ForCompletedSession()
    {
        var lot = CreateLot(1, "Lot A");
        var exitUtc = new DateTime(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc); // 12:00 VN time
        CreateSession(101, lot.Id, finalFee: 30000m, depositApplied: 0m, paidAmount: 30000m,
            status: SessionStatus.Completed, exitTime: exitUtc);
        CreatePayment(1, sessionId: 101, reservationId: null, lotId: lot.Id,
            purpose: PaymentPurpose.ParkingFee, method: PaymentMethod.Cash, amount: 30000m, paidAt: exitUtc);

        var result = await _service.GetAdminRevenueAsync("2026-10-28", "2026-10-28", lot.Id);

        result.SystemTotalRevenue.Should().Be(30000m);
        result.SystemTotalSessions.Should().Be(1);
        result.ByLot.Should().HaveCount(1);
        result.ByLot[0].TotalRevenue.Should().Be(30000m);
        result.PaymentMethods.First(m => m.Method == "CASH").Amount.Should().Be(30000m);
        result.PaymentMethods.First(m => m.Method == "CASH").Percentage.Should().Be(100m);
    }

    [Fact]
    public async Task DepositCoveredSession_ContributesFullFinalFee_WithoutSeparateDepositPayment()
    {
        var lot = CreateLot(1, "Lot A");
        var exitUtc = new DateTime(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc);
        // Fee = 40,000 VND, covered 100% by reservation deposit (which was 50,000 VND; 10,000 was refunded)
        CreateSession(102, lot.Id, finalFee: 40000m, depositApplied: 40000m, paidAmount: 0m,
            status: SessionStatus.Completed, exitTime: exitUtc, reservationId: 501);
        CreatePayment(2, sessionId: null, reservationId: 501, lotId: lot.Id,
            purpose: PaymentPurpose.Deposit, method: PaymentMethod.Online, amount: 50000m, paidAt: exitUtc.AddDays(-1));

        var result = await _service.GetAdminRevenueAsync("2026-10-28", "2026-10-28", lot.Id);

        // Revenue is exactly FinalFee (40,000 VND), not 50,000 VND
        result.SystemTotalRevenue.Should().Be(40000m);
        result.SystemTotalSessions.Should().Be(1);
        result.PaymentMethods.First(m => m.Method == "QR_BANKING").Amount.Should().Be(40000m);
        result.PaymentMethods.First(m => m.Method == "QR_BANKING").Percentage.Should().Be(100m);
    }

    [Fact]
    public async Task SplitSettlement_DepositAndAdditionalPayment_DoesNotDoubleCount()
    {
        var lot = CreateLot(1, "Lot A");
        var exitUtc = new DateTime(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc);
        // Final fee = 80,000 VND: 50,000 deposit + 30,000 paid at exit
        CreateSession(103, lot.Id, finalFee: 80000m, depositApplied: 50000m, paidAmount: 30000m,
            status: SessionStatus.Completed, exitTime: exitUtc, reservationId: 502);

        // Deposit was paid via Online QR yesterday
        CreatePayment(3, sessionId: null, reservationId: 502, lotId: lot.Id,
            purpose: PaymentPurpose.Deposit, method: PaymentMethod.Online, amount: 50000m, paidAt: exitUtc.AddDays(-1));
        // Exit payment was paid via Cash today
        CreatePayment(4, sessionId: 103, reservationId: null, lotId: lot.Id,
            purpose: PaymentPurpose.ParkingFee, method: PaymentMethod.Cash, amount: 30000m, paidAt: exitUtc);

        var result = await _service.GetAdminRevenueAsync("2026-10-28", "2026-10-28", lot.Id);

        // Total revenue must be exactly 80,000 VND (not 30,000 and not 130,000)
        result.SystemTotalRevenue.Should().Be(80000m);
        result.SystemTotalSessions.Should().Be(1);

        var qr = result.PaymentMethods.First(m => m.Method == "QR_BANKING");
        var cash = result.PaymentMethods.First(m => m.Method == "CASH");
        qr.Amount.Should().Be(50000m);
        qr.Percentage.Should().Be(62.5m);
        cash.Amount.Should().Be(30000m);
        cash.Percentage.Should().Be(37.5m);
    }

    [Fact]
    public async Task StandaloneRefund_AndWalletTopUp_AreNotTreatedAsSessionRevenue()
    {
        var lot = CreateLot(1, "Lot A");
        var exitUtc = new DateTime(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc);
        CreateSession(104, lot.Id, finalFee: 20000m, depositApplied: 0m, paidAmount: 20000m,
            status: SessionStatus.Completed, exitTime: exitUtc);
        CreatePayment(5, sessionId: 104, reservationId: null, lotId: lot.Id,
            purpose: PaymentPurpose.ParkingFee, method: PaymentMethod.Wallet, amount: 20000m, paidAt: exitUtc);

        // Standalone refund record and wallet top-up that should be completely ignored
        CreatePayment(6, sessionId: null, reservationId: null, lotId: lot.Id,
            purpose: "Refund", method: PaymentMethod.Online, amount: 15000m, paidAt: exitUtc);
        CreatePayment(7, sessionId: null, reservationId: null, lotId: null,
            purpose: "WalletTopUp", method: PaymentMethod.Online, amount: 500000m, paidAt: exitUtc);

        var result = await _service.GetAdminRevenueAsync("2026-10-28", "2026-10-28", lot.Id);

        result.SystemTotalRevenue.Should().Be(20000m);
        result.SystemTotalSessions.Should().Be(1);
        result.PaymentMethods.First(m => m.Method == "RFID_WALLET").Amount.Should().Be(20000m);
    }

    [Fact]
    public async Task UnpaidOrPendingSessions_AreExcludedFromRevenue()
    {
        var lot = CreateLot(1, "Lot A");
        var exitUtc = new DateTime(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc);

        // Active session in parking lot
        CreateSession(201, lot.Id, finalFee: 0m, depositApplied: 0m, paidAmount: 0m,
            status: SessionStatus.Active, exitTime: null, updatedAt: exitUtc);
        // PendingPayment session at exit barrier
        CreateSession(202, lot.Id, finalFee: 50000m, depositApplied: 0m, paidAmount: 0m,
            status: SessionStatus.PendingPayment, exitTime: null, updatedAt: exitUtc);
        // Cancelled session
        CreateSession(203, lot.Id, finalFee: 0m, depositApplied: 0m, paidAmount: 0m,
            status: SessionStatus.Cancelled, exitTime: exitUtc, updatedAt: exitUtc);

        var result = await _service.GetAdminRevenueAsync("2026-10-28", "2026-10-28", lot.Id);

        result.SystemTotalRevenue.Should().Be(0m);
        result.SystemTotalSessions.Should().Be(0);
    }

    [Fact]
    public async Task PrepaidSession_InsideExitWindow_IsIncludedInRevenue()
    {
        var lot = CreateLot(1, "Lot A");
        var paidUtc = new DateTime(2026, 10, 28, 4, 30, 0, DateTimeKind.Utc); // 11:30 VN time
        // Prepaid via App at kiosk/app: Status = Paid, ExitTime = null
        CreateSession(301, lot.Id, finalFee: 25000m, depositApplied: 0m, paidAmount: 25000m,
            status: SessionStatus.Paid, exitTime: null, updatedAt: paidUtc);
        CreatePayment(8, sessionId: 301, reservationId: null, lotId: lot.Id,
            purpose: PaymentPurpose.ParkingFee, method: PaymentMethod.Online, amount: 25000m, paidAt: paidUtc);

        var result = await _service.GetAdminRevenueAsync("2026-10-28", "2026-10-28", lot.Id);

        result.SystemTotalRevenue.Should().Be(25000m);
        result.SystemTotalSessions.Should().Be(1);
        result.PaymentMethods.First(m => m.Method == "QR_BANKING").Amount.Should().Be(25000m);
    }

    [Fact]
    public async Task AbnormalClosed_And_EmergencyExit_HaveZeroFee()
    {
        var lot = CreateLot(1, "Lot A");
        var exitUtc = new DateTime(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc);
        // Abnormal closed session: FinalFee = 0
        CreateSession(401, lot.Id, finalFee: 0m, depositApplied: 0m, paidAmount: 0m,
            status: SessionStatus.AbnormalClosed, exitTime: exitUtc);
        // Emergency exit: FinalFee = 0
        CreateSession(402, lot.Id, finalFee: 0m, depositApplied: 0m, paidAmount: 0m,
            status: SessionStatus.EmergencyExit, exitTime: exitUtc);

        var result = await _service.GetAdminRevenueAsync("2026-10-28", "2026-10-28", lot.Id);

        result.SystemTotalRevenue.Should().Be(0m);
        result.SystemTotalSessions.Should().Be(0);
    }

    [Fact]
    public async Task UnknownPaymentMethod_IsMappedToOther_NotMisclassifiedAsQr()
    {
        var lot = CreateLot(1, "Lot A");
        var exitUtc = new DateTime(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc);
        CreateSession(501, lot.Id, finalFee: 60000m, depositApplied: 0m, paidAmount: 60000m,
            status: SessionStatus.Completed, exitTime: exitUtc);
        CreatePayment(9, sessionId: 501, reservationId: null, lotId: lot.Id,
            purpose: PaymentPurpose.ParkingFee, method: "CorporateVoucher", amount: 60000m, paidAt: exitUtc);

        var result = await _service.GetAdminRevenueAsync("2026-10-28", "2026-10-28", lot.Id);

        result.SystemTotalRevenue.Should().Be(60000m);
        var other = result.PaymentMethods.FirstOrDefault(m => m.Method == "OTHER");
        other.Should().NotBeNull();
        other!.Amount.Should().Be(60000m);
        other.Percentage.Should().Be(100m);

        // QR_BANKING should remain 0
        result.PaymentMethods.First(m => m.Method == "QR_BANKING").Amount.Should().Be(0m);
    }

    [Fact]
    public async Task ZeroTotalRevenue_HandlesPercentagesSafely()
    {
        var lot = CreateLot(1, "Lot A");

        var result = await _service.GetAdminRevenueAsync("2026-10-28", "2026-10-28", lot.Id);

        result.SystemTotalRevenue.Should().Be(0m);
        result.SystemTotalSessions.Should().Be(0);
        foreach (var m in result.PaymentMethods)
        {
            m.Amount.Should().Be(0m);
            m.Percentage.Should().Be(0m);
        }
    }

    [Fact]
    public async Task ManagerAPI_ReturnsOnlyManagerAssignedLots()
    {
        var lot1 = CreateLot(1, "Lot 1", managerUserId: "mgr-alpha");
        var lot2 = CreateLot(2, "Lot 2", managerUserId: "mgr-beta");

        var exitUtc = new DateTime(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc);
        CreateSession(601, lot1.Id, finalFee: 50000m, depositApplied: 0m, paidAmount: 50000m,
            status: SessionStatus.Completed, exitTime: exitUtc);
        CreateSession(602, lot2.Id, finalFee: 70000m, depositApplied: 0m, paidAmount: 70000m,
            status: SessionStatus.Completed, exitTime: exitUtc);

        // mgr-alpha calls API without lot filter
        var result = await _service.GetManagerRevenueAsync("mgr-alpha", "2026-10-28", "2026-10-28");

        result.ManagerUserId.Should().Be("mgr-alpha");
        result.TotalRevenue.Should().Be(50000m);
        result.TotalSessions.Should().Be(1);
        result.ByLot.Should().HaveCount(1);
        result.ByLot[0].ParkingLotId.Should().Be(lot1.Id);
    }

    [Fact]
    public async Task ManagerAPI_WhenManagerHasNoLots_ReturnsEmptyValidResult()
    {
        var result = await _service.GetManagerRevenueAsync("mgr-unassigned", "2026-10-28", "2026-10-28");

        result.ManagerUserId.Should().Be("mgr-unassigned");
        result.TotalRevenue.Should().Be(0m);
        result.TotalSessions.Should().Be(0);
        result.ByLot.Should().BeEmpty();
        result.PaymentMethods.Should().BeEmpty();
    }

    [Fact]
    public async Task ManagerAPI_RequestingUnmanagedLot_ThrowsForbiddenException()
    {
        var lot = CreateLot(1, "Lot 1", managerUserId: "mgr-other");

        var act = () => _service.GetManagerRevenueAsync("mgr-attacker", "2026-10-28", "2026-10-28", lot.Id);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task AdminAPI_WithoutLotFilter_AggregatesAllActiveLots()
    {
        var lot1 = CreateLot(1, "Lot 1", managerUserId: "mgr-1");
        var lot2 = CreateLot(2, "Lot 2", managerUserId: "mgr-2");
        var lotDeleted = CreateLot(3, "Lot Deleted", managerUserId: "mgr-3", isDeleted: true);

        var exitUtc = new DateTime(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc);
        CreateSession(701, lot1.Id, finalFee: 30000m, depositApplied: 0m, paidAmount: 30000m,
            status: SessionStatus.Completed, exitTime: exitUtc);
        CreateSession(702, lot2.Id, finalFee: 40000m, depositApplied: 0m, paidAmount: 40000m,
            status: SessionStatus.Completed, exitTime: exitUtc);
        CreateSession(703, lotDeleted.Id, finalFee: 99999m, depositApplied: 0m, paidAmount: 99999m,
            status: SessionStatus.Completed, exitTime: exitUtc);

        var result = await _service.GetAdminRevenueAsync("2026-10-28", "2026-10-28");

        result.ActiveLotsCount.Should().Be(2);
        result.SystemTotalRevenue.Should().Be(70000m);
        result.SystemTotalSessions.Should().Be(2);
        result.ByLot.Should().HaveCount(2);
    }

    [Fact]
    public async Task AdminAPI_RequestingNonexistentOrDeletedLot_ThrowsNotFoundException()
    {
        var deletedLot = CreateLot(10, "Deleted Lot", isDeleted: true);

        var act1 = () => _service.GetAdminRevenueAsync("2026-10-28", "2026-10-28", 99999);
        var act2 = () => _service.GetAdminRevenueAsync("2026-10-28", "2026-10-28", deletedLot.Id);

        await act1.Should().ThrowAsync<NotFoundException>();
        await act2.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task InvalidDateFormat_ThrowsBadRequestException()
    {
        var lot = CreateLot(1, "Lot 1");

        var act = () => _service.GetAdminRevenueAsync("not-a-date", "2026-10-28", lot.Id);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task EndDateBeforeStartDate_ThrowsBadRequestException()
    {
        var lot = CreateLot(1, "Lot 1");

        var act = () => _service.GetAdminRevenueAsync("2026-10-29", "2026-10-28", lot.Id);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task AdminAPI_SystemRevenue_EqualsSumOfPerLotRevenue()
    {
        var lot1 = CreateLot(1, "Lot Alpha", managerUserId: "mgr-1");
        var lot2 = CreateLot(2, "Lot Beta", managerUserId: "mgr-2");
        var exitUtc = new DateTime(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc);

        CreateSession(801, lot1.Id, finalFee: 35000m, depositApplied: 0m, paidAmount: 35000m,
            status: SessionStatus.Completed, exitTime: exitUtc);
        CreatePayment(81, sessionId: 801, reservationId: null, lotId: lot1.Id,
            purpose: PaymentPurpose.ParkingFee, method: PaymentMethod.Cash, amount: 35000m, paidAt: exitUtc);

        CreateSession(802, lot2.Id, finalFee: 65000m, depositApplied: 0m, paidAmount: 65000m,
            status: SessionStatus.Completed, exitTime: exitUtc);
        CreatePayment(82, sessionId: 802, reservationId: null, lotId: lot2.Id,
            purpose: PaymentPurpose.ParkingFee, method: PaymentMethod.Online, amount: 65000m, paidAt: exitUtc);

        var result = await _service.GetAdminRevenueAsync("2026-10-28", "2026-10-28");

        result.SystemTotalRevenue.Should().Be(100000m);
        result.SystemTotalSessions.Should().Be(2);
        result.ByLot.Sum(l => l.TotalRevenue).Should().Be(result.SystemTotalRevenue);
        result.ByLot.Sum(l => l.TotalSessions).Should().Be(result.SystemTotalSessions);
    }

    [Fact]
    public async Task ManagerAPI_Totals_EqualSumOfPerLotTotals()
    {
        var lot1 = CreateLot(1, "Lot Alpha", managerUserId: "mgr-multi");
        var lot2 = CreateLot(2, "Lot Beta", managerUserId: "mgr-multi");
        var exitUtc = new DateTime(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc);

        CreateSession(803, lot1.Id, finalFee: 20000m, depositApplied: 0m, paidAmount: 20000m,
            status: SessionStatus.Completed, exitTime: exitUtc);
        CreateSession(804, lot2.Id, finalFee: 80000m, depositApplied: 0m, paidAmount: 80000m,
            status: SessionStatus.Completed, exitTime: exitUtc);

        var result = await _service.GetManagerRevenueAsync("mgr-multi", "2026-10-28", "2026-10-28");

        result.TotalRevenue.Should().Be(100000m);
        result.TotalSessions.Should().Be(2);
        result.ByLot.Sum(l => l.TotalRevenue).Should().Be(result.TotalRevenue);
        result.ByLot.Sum(l => l.TotalSessions).Should().Be(result.TotalSessions);
    }

    [Fact]
    public async Task PaymentMethods_Sum_ExactlyReconcilesToTotalRevenue()
    {
        var lot = CreateLot(1, "Lot Reconcile");
        var exitUtc = new DateTime(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc);

        // Session 1: Walk-in Cash 30,000
        CreateSession(901, lot.Id, finalFee: 30000m, depositApplied: 0m, paidAmount: 30000m,
            status: SessionStatus.Completed, exitTime: exitUtc);
        CreatePayment(91, sessionId: 901, reservationId: null, lotId: lot.Id,
            purpose: PaymentPurpose.ParkingFee, method: PaymentMethod.Cash, amount: 30000m, paidAt: exitUtc);

        // Session 2: Reservation split (50,000 QR deposit + 30,000 Cash checkout = 80,000)
        CreateSession(902, lot.Id, finalFee: 80000m, depositApplied: 50000m, paidAmount: 30000m,
            status: SessionStatus.Completed, exitTime: exitUtc, reservationId: 920);
        CreatePayment(92, sessionId: null, reservationId: 920, lotId: lot.Id,
            purpose: PaymentPurpose.Deposit, method: PaymentMethod.Online, amount: 50000m, paidAt: exitUtc.AddDays(-1));
        CreatePayment(93, sessionId: 902, reservationId: null, lotId: lot.Id,
            purpose: PaymentPurpose.ParkingFee, method: PaymentMethod.Cash, amount: 30000m, paidAt: exitUtc);

        // Session 3: Wallet 40,000
        CreateSession(903, lot.Id, finalFee: 40000m, depositApplied: 0m, paidAmount: 40000m,
            status: SessionStatus.Completed, exitTime: exitUtc);
        CreatePayment(94, sessionId: 903, reservationId: null, lotId: lot.Id,
            purpose: PaymentPurpose.ParkingFee, method: PaymentMethod.Wallet, amount: 40000m, paidAt: exitUtc);

        var result = await _service.GetAdminRevenueAsync("2026-10-28", "2026-10-28", lot.Id);

        result.SystemTotalRevenue.Should().Be(150000m);
        result.PaymentMethods.Sum(m => m.Amount).Should().Be(result.SystemTotalRevenue);
        result.PaymentMethods.First(m => m.Method == "CASH").Amount.Should().Be(60000m);
        result.PaymentMethods.First(m => m.Method == "QR_BANKING").Amount.Should().Be(50000m);
        result.PaymentMethods.First(m => m.Method == "RFID_WALLET").Amount.Should().Be(40000m);
    }

    [Fact]
    public async Task MultipleDepositRecords_AreAllocatedProportionally()
    {
        var lot = CreateLot(1, "Lot MultiDep");
        var exitUtc = new DateTime(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc);

        // Reservation 950 had two deposit payments: 30,000 QR + 20,000 Wallet = 50,000 total deposit
        CreatePayment(95, sessionId: null, reservationId: 950, lotId: lot.Id,
            purpose: PaymentPurpose.Deposit, method: PaymentMethod.Online, amount: 30000m, paidAt: exitUtc.AddDays(-2));
        CreatePayment(96, sessionId: null, reservationId: 950, lotId: lot.Id,
            purpose: PaymentPurpose.Deposit, method: PaymentMethod.Wallet, amount: 20000m, paidAt: exitUtc.AddDays(-2));

        // Session stay = 80,000 VND (50,000 deposit applied + 30,000 cash paid at exit)
        CreateSession(904, lot.Id, finalFee: 80000m, depositApplied: 50000m, paidAmount: 30000m,
            status: SessionStatus.Completed, exitTime: exitUtc, reservationId: 950);
        CreatePayment(97, sessionId: 904, reservationId: null, lotId: lot.Id,
            purpose: PaymentPurpose.ParkingFee, method: PaymentMethod.Cash, amount: 30000m, paidAt: exitUtc);

        var result = await _service.GetAdminRevenueAsync("2026-10-28", "2026-10-28", lot.Id);

        result.SystemTotalRevenue.Should().Be(80000m);
        result.PaymentMethods.Sum(m => m.Amount).Should().Be(80000m);
        result.PaymentMethods.First(m => m.Method == "QR_BANKING").Amount.Should().Be(30000m);
        result.PaymentMethods.First(m => m.Method == "RFID_WALLET").Amount.Should().Be(20000m);
        result.PaymentMethods.First(m => m.Method == "CASH").Amount.Should().Be(30000m);
    }

    /* Vehicle types and time series ------------------------------------- */

    private const int Car4 = 1, Car7 = 2, Motorbike = 3;

    private void SeedVehicleTypes()
    {
        _db.VehicleTypes.AddRange(
            new VehicleType { Id = Car4, Code = "CAR_4", Name = "4-seat car", IsActive = true },
            new VehicleType { Id = Car7, Code = "CAR_7", Name = "7-seat car", IsActive = true },
            new VehicleType { Id = Motorbike, Code = "MOTORBIKE", Name = "Motorbike", IsActive = true });
        _db.SaveChanges();
    }

    private void SetType(long sessionId, int vehicleTypeId)
    {
        var session = _db.ParkingSessions.Find(sessionId);
        session.VehicleTypeId = vehicleTypeId;
        _db.SaveChanges();
    }

    /// <summary>Lot A: a 4-seat and a 7-seat car on 28/10, a motorbike on 29/10 (all at noon VN time).</summary>
    private ParkingLot SeedMixedLot()
    {
        SeedVehicleTypes();
        var lot = CreateLot(1, "Lot A");
        var day1 = new DateTime(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2026, 10, 29, 5, 0, 0, DateTimeKind.Utc);
        CreateSession(201, lot.Id, 40000m, 0m, 40000m, SessionStatus.Completed, day1);
        CreateSession(202, lot.Id, 50000m, 0m, 50000m, SessionStatus.Completed, day1);
        CreateSession(203, lot.Id, 6000m, 0m, 6000m, SessionStatus.Completed, day2);
        SetType(201, Car4);
        SetType(202, Car7);
        SetType(203, Motorbike);
        return lot;
    }

    [Fact]
    public async Task AdminRevenue_SplitsCarsAndMotorbikes()
    {
        SeedMixedLot();

        var result = await _service.GetAdminRevenueAsync("2026-10-28", "2026-10-29");

        result.ByVehicleType.Should().HaveCount(2);
        result.ByVehicleType.Single(v => v.VehicleType == "CAR").TotalRevenue.Should().Be(90000m);
        result.ByVehicleType.Single(v => v.VehicleType == "CAR").TotalSessions.Should().Be(2);
        result.ByVehicleType.Single(v => v.VehicleType == "MOTORBIKE").TotalRevenue.Should().Be(6000m);
    }

    [Fact]
    public async Task AdminRevenue_VehicleTypeFilter_KeepsOnlyThatFamily()
    {
        SeedMixedLot();

        var result = await _service.GetAdminRevenueAsync("2026-10-28", "2026-10-29", vehicleType: "motorbike");

        result.SystemTotalRevenue.Should().Be(6000m);
        result.SystemTotalSessions.Should().Be(1);
        result.ByLot.Single().TotalRevenue.Should().Be(6000m);
        result.ByVehicleType.Single(v => v.VehicleType == "CAR").TotalSessions.Should().Be(0);
    }

    [Fact]
    public async Task AdminRevenue_UnknownVehicleType_IsRejected()
    {
        var act = () => _service.GetAdminRevenueAsync("2026-10-28", "2026-10-29", vehicleType: "BICYCLE");

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task TimeSeries_ReturnsEveryDay_WithCarAndMotorbikeTotals()
    {
        SeedMixedLot();

        var result = await _service.GetAdminRevenueTimeSeriesAsync("2026-10-27", "2026-10-29");

        result.Granularity.Should().Be("DAY");
        result.Points.Select(p => p.StartDate).Should().Equal("2026-10-27", "2026-10-28", "2026-10-29");
        result.Points[0].TotalSessions.Should().Be(0);
        result.Points[1].TotalRevenue.Should().Be(90000m);
        result.Points[1].CarSessions.Should().Be(2);
        result.Points[1].MotorbikeSessions.Should().Be(0);
        result.Points[2].MotorbikeRevenue.Should().Be(6000m);
        result.Points[1].ByLot.Single().ParkingLotName.Should().Be("Lot A");
        result.Points[0].ByLot.Should().BeEmpty();
    }

    [Fact]
    public async Task TimeSeries_CountsSessionsOnTheLocalDay()
    {
        SeedVehicleTypes();
        var lot = CreateLot(1, "Lot A");
        // 18:30 UTC on 27/10 is 01:30 on 28/10 in Vietnam
        CreateSession(301, lot.Id, 20000m, 0m, 20000m, SessionStatus.Completed, new DateTime(2026, 10, 27, 18, 30, 0, DateTimeKind.Utc));

        var result = await _service.GetAdminRevenueTimeSeriesAsync("2026-10-27", "2026-10-28");

        result.Points[0].TotalRevenue.Should().Be(0m);
        result.Points[1].TotalRevenue.Should().Be(20000m);
    }

    [Fact]
    public async Task TimeSeries_LongRange_GoesPerWeek_FromMondayToSunday()
    {
        SeedMixedLot();

        // 01/10/2026 is a Thursday; 35 days -> weeks
        var result = await _service.GetAdminRevenueTimeSeriesAsync("2026-10-01", "2026-11-04");

        result.Granularity.Should().Be("WEEK");
        result.Points[0].StartDate.Should().Be("2026-10-01");
        result.Points[0].EndDate.Should().Be("2026-10-04");
        result.Points[1].StartDate.Should().Be("2026-10-05");
        result.Points[^1].EndDate.Should().Be("2026-11-04");
        result.Points.Sum(p => p.TotalRevenue).Should().Be(96000m);
        // 28 and 29 October fall in the week of Monday 26 October
        result.Points.Single(p => p.StartDate == "2026-10-26").TotalSessions.Should().Be(3);
    }

    [Fact]
    public async Task TimeSeries_ExplicitMonth_AndVehicleFilter()
    {
        SeedMixedLot();

        var result = await _service.GetAdminRevenueTimeSeriesAsync("2026-09-15", "2026-10-31", vehicleType: "CAR", granularity: "month");

        result.Granularity.Should().Be("MONTH");
        result.Points.Select(p => (p.StartDate, p.EndDate)).Should().Equal(("2026-09-15", "2026-09-30"), ("2026-10-01", "2026-10-31"));
        result.Points[1].TotalRevenue.Should().Be(90000m);
        result.Points[1].MotorbikeSessions.Should().Be(0);
    }

    [Fact]
    public async Task TimeSeries_InvalidGranularity_IsRejected()
    {
        var act = () => _service.GetAdminRevenueTimeSeriesAsync("2026-10-01", "2026-10-02", granularity: "HOUR");

        await act.Should().ThrowAsync<BadRequestException>();
    }
}
