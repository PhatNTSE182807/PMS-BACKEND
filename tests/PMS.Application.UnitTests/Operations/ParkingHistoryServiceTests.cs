using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using PMS.Application.Exceptions;
using PMS.Application.Models.Operations;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>The customer's parking history and e-receipts (FR-CUS-02).</summary>
public class ParkingHistoryServiceTests
{
    private static async Task<(OperationsTestContext T, ParkingHistoryService Sut)> SetupAsync()
    {
        var t = await new OperationsTestContext().SeedAsync();
        return (t, new ParkingHistoryService(t.Db, t.Clock));
    }

    /// <summary>A session of the test customer's car that entered `hoursAgo` and stayed `minutes`.</summary>
    private static ParkingSession Session(OperationsTestContext t, string code, double hoursAgo, int minutes, string status = SessionStatus.Completed, decimal fee = 20_000, int? customerId = null)
    {
        var entry = t.Now.AddHours(-hoursAgo);
        var session = new ParkingSession
        {
            SessionCode = code,
            ParkingLotId = t.Lot.Id,
            CustomerId = customerId ?? t.Customer.Id,
            VehicleId = t.Car.Id,
            VehicleTypeId = t.Car.VehicleTypeId,
            EntryGateId = 1,
            EntryTime = entry,
            EntryPlateNumber = t.Car.PlateNumber,
            EntryPlateNormalized = t.Car.PlateNormalized,
            RowVersion = [1],
            ExitTime = status == SessionStatus.Completed ? entry.AddMinutes(minutes) : null,
            TotalDurationMinutes = status == SessionStatus.Completed ? minutes : null,
            OriginalFee = fee,
            FinalFee = fee,
            PaidAmount = fee,
            Status = status,
            SessionType = "WalkIn"
        };
        t.Db.ParkingSessions.Add(session);
        return session;
    }

    [Fact]
    public async Task History_Lists_Only_The_Customers_Finished_Sessions_Newest_First()
    {
        var (t, sut) = await SetupAsync();
        Session(t, "S-OLD", hoursAgo: 72, minutes: 60);
        Session(t, "S-NEW", hoursAgo: 5, minutes: 90, fee: 45_000);
        Session(t, "S-INSIDE", hoursAgo: 1, minutes: 0, status: SessionStatus.Active);
        Session(t, "S-OTHER", hoursAgo: 2, minutes: 30, customerId: t.Customer.Id + 1000);
        await t.Db.SaveChangesAsync();

        var page = await sut.GetHistoryAsync(t.CustomerUserId, new ParkingHistoryQuery());

        page.Items.Select(i => i.SessionCode).Should().Equal("S-NEW", "S-OLD");
        page.TotalCount.Should().Be(2);
        page.TotalSpent.Should().Be(65_000);
        var latest = page.Items[0];
        latest.DurationMinutes.Should().Be(90);
        latest.LotName.Should().Be(t.Lot.Name);
        latest.PlateNumber.Should().Be(t.Car.PlateNumber);
        latest.ExitTime!.Value.Kind.Should().Be(DateTimeKind.Utc);
        latest.ReceiptNumber.Should().Be("S-NEW");
    }

    [Fact]
    public async Task History_Pages_And_Filters_By_Exit_Time()
    {
        var (t, sut) = await SetupAsync();
        for (var i = 1; i <= 5; i++)
            Session(t, $"S-{i}", hoursAgo: i * 24, minutes: 60);
        await t.Db.SaveChangesAsync();

        var second = await sut.GetHistoryAsync(t.CustomerUserId, new ParkingHistoryQuery { Page = 2, PageSize = 2 });
        var lastTwoDays = await sut.GetHistoryAsync(t.CustomerUserId, new ParkingHistoryQuery { From = t.Now.AddHours(-49) });

        second.Items.Select(i => i.SessionCode).Should().Equal("S-3", "S-4");
        second.TotalCount.Should().Be(5);
        lastTwoDays.Items.Select(i => i.SessionCode).Should().Equal("S-1", "S-2");
    }

    [Fact]
    public async Task History_Is_Empty_For_A_User_Without_A_Customer_Profile()
    {
        var (t, sut) = await SetupAsync();

        var page = await sut.GetHistoryAsync("nobody", new ParkingHistoryQuery());

        page.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Receipt_Has_The_Fee_Breakdown_The_Deposit_And_The_Invoice_Number()
    {
        var (t, sut) = await SetupAsync();
        var reservation = new Reservation { ReservationCode = "RSV-1", CustomerId = t.Customer.Id, ParkingLotId = t.Lot.Id, VehicleId = t.Car.Id, Status = "Completed" };
        t.Db.Reservations.Add(reservation);
        await t.Db.SaveChangesAsync();

        var session = Session(t, "S-RCPT", hoursAgo: 3, minutes: 120, fee: 50_000);
        session.ReservationId = reservation.Id;
        session.DepositApplied = 30_000;
        session.PaidAmount = 20_000;
        await t.Db.SaveChangesAsync();

        var invoice = new Invoice { InvoiceNumber = "INV-42", SessionId = session.Id, ParkingLotId = t.Lot.Id, Status = "Paid", TotalAmount = 20_000, CreatedAt = t.Now };
        t.Db.Invoices.Add(invoice);
        t.Db.Payments.AddRange(
            new Payment { PaymentCode = "PM-DEP", Invoice = invoice, ReservationId = reservation.Id, Purpose = PaymentPurpose.Deposit, PaymentMethod = PaymentMethod.Online, GatewayProvider = "VNPay", Amount = 30_000, Currency = "VND", Status = PaymentStatus.Succeeded, PaidAt = t.Now.AddHours(-4), CreatedAt = t.Now.AddHours(-4) },
            new Payment { PaymentCode = "PM-FEE", Invoice = invoice, SessionId = session.Id, Purpose = PaymentPurpose.ParkingFee, PaymentMethod = PaymentMethod.Cash, Amount = 20_000, Currency = "VND", Status = PaymentStatus.Succeeded, PaidAt = t.Now.AddHours(-1), CreatedAt = t.Now.AddHours(-1) },
            new Payment { PaymentCode = "PM-FAILED", Invoice = invoice, SessionId = session.Id, Purpose = PaymentPurpose.ParkingFee, PaymentMethod = PaymentMethod.Online, Amount = 20_000, Currency = "VND", Status = PaymentStatus.Failed, CreatedAt = t.Now.AddHours(-2) });
        await t.Db.SaveChangesAsync();

        var receipt = await sut.GetReceiptAsync(t.CustomerUserId, session.Id);

        receipt.ReceiptNumber.Should().Be("INV-42");
        receipt.ReservationCode.Should().Be("RSV-1");
        receipt.CustomerName.Should().Be(t.Customer.FullName);
        receipt.FinalFee.Should().Be(50_000);
        receipt.DepositApplied.Should().Be(30_000);
        receipt.Payments.Select(p => p.PaymentCode).Should().Equal("PM-DEP", "PM-FEE");
    }

    [Fact]
    public async Task Receipt_Is_Refused_For_Open_Or_Foreign_Sessions()
    {
        var (t, sut) = await SetupAsync();
        var inside = Session(t, "S-INSIDE", hoursAgo: 1, minutes: 0, status: SessionStatus.Active);
        var foreign = Session(t, "S-OTHER", hoursAgo: 2, minutes: 30, customerId: t.Customer.Id + 1000);
        await t.Db.SaveChangesAsync();

        await sut.Invoking(s => s.GetReceiptAsync(t.CustomerUserId, inside.Id)).Should().ThrowAsync<BadRequestException>();
        await sut.Invoking(s => s.GetReceiptAsync(t.CustomerUserId, foreign.Id)).Should().ThrowAsync<NotFoundException>();
    }
}
