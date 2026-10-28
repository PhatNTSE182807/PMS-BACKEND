using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using PMS.Application.Exceptions;
using PMS.Application.Jobs;
using PMS.Application.Models.Notifications;
using PMS.Application.Models.Operations;
using PMS.Application.Models.VehicleDocuments;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.Application.UnitTests.Operations;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Services;

/// <summary>The customer's notification bell and the background jobs that fill it.</summary>
public class NotificationTests : IDisposable
{
    private const string UserId = "user-1";

    private readonly PmsDbContext _context;
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 23, 3, 32, 0, TimeSpan.Zero));
    private readonly IUserNotificationNotifier _notifier = Substitute.For<IUserNotificationNotifier>();
    private readonly NotificationService _service;
    private readonly NotificationJobs _jobs;

    private Customer _customer;
    private ParkingLot _lot;
    private Vehicle _vehicle;

    public NotificationTests()
    {
        _context = new PmsDbContext(new DbContextOptionsBuilder<PmsDbContext>()
            .UseInMemoryDatabase($"NotificationTests_{Guid.NewGuid()}")
            .Options);
        _service = new NotificationService(_context, _clock);
        _jobs = new NotificationJobs(_context, _clock, _notifier);
        Seed();
    }

    public void Dispose() => _context.Dispose();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private void Seed()
    {
        _customer = new Customer { UserId = UserId, CustomerCode = "C1", FullName = "Customer", IsActive = true, CreatedAt = Now };
        _lot = new ParkingLot { Code = "LOT-1", Name = "Central", Address = "1 Main St", Status = "Active", TimeZoneId = "SE Asia Standard Time", CreatedAt = Now };
        _vehicle = new Vehicle { Customer = _customer, VehicleTypeId = 1, PlateNumber = "51A-123.45", PlateNormalized = "51A12345", Brand = "Toyota", Model = "Vios", IsActive = true };
        _context.AddRange(_customer, _lot, _vehicle);
        _context.SaveChanges();
    }

    private UserNotification AddNotification(string userId = UserId, DateTime? createdAt = null, DateTime? readAt = null, string dataJson = null)
    {
        var notification = new UserNotification
        {
            UserId = userId,
            Type = NotificationTypes.CheckIn,
            Title = "Checked in",
            Message = "Hello",
            RefId = Guid.NewGuid().ToString(),
            DataJson = dataJson,
            CreatedAt = createdAt ?? Now,
            ReadAt = readAt,
            EventAt = Now
        };
        _context.UserNotifications.Add(notification);
        _context.SaveChanges();
        return notification;
    }

    private ParkingSession AddSession(string status, DateTime? exitTime = null, int? customerId = -1)
    {
        var session = new ParkingSession
        {
            SessionCode = $"S-{Guid.NewGuid():N}"[..12],
            ParkingLotId = _lot.Id,
            CustomerId = customerId == -1 ? _customer.Id : customerId,
            EntryGateId = 1,
            EntryTime = Now.AddMinutes(-90),
            EntryPlateNumber = "51A-123.45",
            EntryPlateNormalized = "51A12345",
            Status = status,
            SessionType = "WalkIn",
            ExitTime = exitTime,
            RowVersion = [1]
        };
        _context.ParkingSessions.Add(session);
        _context.SaveChanges();
        return session;
    }

    // ---- Notification types ----

    [Fact]
    public void NotificationTypes_All_Should_List_Every_Type_Once()
    {
        NotificationTypes.All.Should().Equal(
            NotificationTypes.VehicleApproved,
            NotificationTypes.VehicleRejected,
            NotificationTypes.CheckIn,
            NotificationTypes.CheckOut);
        NotificationTypes.All.Should().OnlyHaveUniqueItems();
    }

    // ---- NotificationService ----

    [Fact]
    public async Task GetMineAsync_Should_List_Own_Notifications_Newest_First()
    {
        var older = AddNotification(createdAt: Now.AddHours(-2), dataJson: "{\"plateNumber\":\"51A\"}");
        var newer = AddNotification(readAt: Now);
        AddNotification(userId: "other");

        var all = await _service.GetMineAsync(UserId, unreadOnly: false, take: 0);

        all.Items.Select(n => n.Id).Should().Equal(newer.Id, older.Id);
        all.UnreadCount.Should().Be(1);
        all.Items[1].Data.Should().ContainKey("plateNumber");
        all.Items[0].IsRead.Should().BeTrue();
        all.Items[0].CreatedAt.Kind.Should().Be(DateTimeKind.Utc);

        var unread = await _service.GetMineAsync(UserId, unreadOnly: true, take: 500);
        unread.Items.Should().ContainSingle(n => n.Id == older.Id);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("  ")]
    public async Task GetMineAsync_Should_Ignore_Unreadable_Data(string json)
    {
        AddNotification(dataJson: json);

        var list = await _service.GetMineAsync(UserId, false, 10);

        list.Items[0].Data.Should().BeEmpty();
    }

    [Fact]
    public async Task MarkReadAsync_Should_Set_The_Read_Time_Once()
    {
        var notification = AddNotification();

        var first = await _service.MarkReadAsync(UserId, notification.Id);
        _clock.Advance(TimeSpan.FromHours(1));
        var second = await _service.MarkReadAsync(UserId, notification.Id);

        first.ReadAt.Should().Be(second.ReadAt);
        (await _service.GetUnreadCountAsync(UserId)).Should().Be(0);
    }

    [Fact]
    public async Task MarkReadAsync_And_DeleteAsync_Should_Hide_Other_Users_Notifications()
    {
        var other = AddNotification(userId: "other");

        await FluentActions.Invoking(() => _service.MarkReadAsync(UserId, other.Id)).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Invoking(() => _service.DeleteAsync(UserId, other.Id)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task MarkAllReadAsync_Should_Mark_Every_Unread_One()
    {
        AddNotification();
        AddNotification();
        AddNotification(readAt: Now);

        (await _service.MarkAllReadAsync(UserId)).Should().Be(2);
        (await _service.GetUnreadCountAsync(UserId)).Should().Be(0);
    }

    [Fact]
    public async Task DeleteAsync_Should_Remove_The_Notification()
    {
        var notification = AddNotification();

        await _service.DeleteAsync(UserId, notification.Id);

        _context.UserNotifications.Should().BeEmpty();
    }

    // ---- NotificationJobs: vehicle review ----

    private VehicleVerification AddVerification(string status, string note = null, string userId = UserId)
    {
        var verification = new VehicleVerification
        {
            Vehicle = _vehicle, CustomerId = _customer.Id, UserId = userId, Status = status, ReviewNote = note,
            CreatedAt = Now, DecidedAt = Now
        };
        _context.VehicleVerifications.Add(verification);
        _context.SaveChanges();
        return verification;
    }

    [Fact]
    public async Task VehicleReviewedAsync_Should_Notify_An_Approval_Once()
    {
        var verification = AddVerification(VehicleVerificationStatus.Approved);

        (await _jobs.VehicleReviewedAsync(verification.Id)).Should().BeTrue();
        (await _jobs.VehicleReviewedAsync(verification.Id)).Should().BeFalse();

        var saved = _context.UserNotifications.Single();
        saved.Type.Should().Be(NotificationTypes.VehicleApproved);
        saved.Message.Should().Contain("51A-123.45 (Toyota Vios)");
        await _notifier.Received(1).NotificationCreatedAsync(UserId, Arg.Any<NotificationModel>(), 1);
    }

    [Theory]
    [InlineData("Blurry photo", ": \"Blurry photo\".")]
    [InlineData(null, "51A-123.45 (Toyota Vios).")]
    public async Task VehicleReviewedAsync_Should_Quote_The_Rejection_Reason(string note, string expected)
    {
        var verification = AddVerification(VehicleVerificationStatus.Rejected, note);

        (await _jobs.VehicleReviewedAsync(verification.Id)).Should().BeTrue();

        var saved = _context.UserNotifications.Single();
        saved.Type.Should().Be(NotificationTypes.VehicleRejected);
        saved.Message.Should().Contain(expected);
    }

    [Fact]
    public async Task VehicleReviewedAsync_Should_Skip_Undecided_Unknown_Or_Ownerless_Checks()
    {
        (await _jobs.VehicleReviewedAsync(Guid.NewGuid())).Should().BeFalse();
        (await _jobs.VehicleReviewedAsync(AddVerification(VehicleVerificationStatus.NeedsReview).Id)).Should().BeFalse();
        (await _jobs.VehicleReviewedAsync(AddVerification(VehicleVerificationStatus.Approved, userId: "").Id)).Should().BeFalse();
        _context.UserNotifications.Should().BeEmpty();
    }

    [Fact]
    public async Task VehicleReviewedAsync_Should_Use_The_Plate_Alone_Without_Brand()
    {
        _vehicle.Brand = null;
        _vehicle.Model = " ";
        await _context.SaveChangesAsync();
        var verification = AddVerification(VehicleVerificationStatus.Approved);
        var jobs = new NotificationJobs(_context, _clock);

        (await jobs.VehicleReviewedAsync(verification.Id)).Should().BeTrue();

        _context.UserNotifications.Single().Message.Should().Contain("papers of 51A-123.45. ");
    }

    // ---- NotificationJobs: check-in / check-out ----

    [Fact]
    public async Task SessionCheckedInAsync_Should_Tell_The_Owner_The_Local_Time()
    {
        var session = AddSession(SessionStatus.Active);

        (await _jobs.SessionCheckedInAsync(session.Id)).Should().BeTrue();

        var saved = _context.UserNotifications.Single();
        saved.Title.Should().Be("Checked in at Central");
        saved.Message.Should().Contain("09:02, 23/10/2026");
        saved.Message.Should().Contain(session.SessionCode);
    }

    [Fact]
    public async Task SessionCheckedInAsync_Should_Skip_Walk_Ins_Without_Account_And_Pending_Entries()
    {
        (await _jobs.SessionCheckedInAsync(9999)).Should().BeFalse();
        (await _jobs.SessionCheckedInAsync(AddSession(SessionStatus.Active, customerId: null).Id)).Should().BeFalse();
        (await _jobs.SessionCheckedInAsync(AddSession(SessionStatus.PendingEntry).Id)).Should().BeFalse();
    }

    [Fact]
    public async Task SessionCheckedInAsync_Should_Fall_Back_To_Vietnam_Time_For_An_Unknown_Zone()
    {
        _lot.TimeZoneId = "Nowhere/Unknown";
        await _context.SaveChangesAsync();
        var session = AddSession(SessionStatus.Completed);

        (await _jobs.SessionCheckedInAsync(session.Id)).Should().BeTrue();

        _context.UserNotifications.Single().Message.Should().Contain("09:02, 23/10/2026");
    }

    [Fact]
    public async Task SessionCheckedOutAsync_Should_Give_Duration_And_Fee()
    {
        var session = AddSession(SessionStatus.Completed, exitTime: Now);
        session.FinalFee = 25000;
        await _context.SaveChangesAsync();

        (await _jobs.SessionCheckedOutAsync(session.Id)).Should().BeTrue();

        var saved = _context.UserNotifications.Single();
        saved.Type.Should().Be(NotificationTypes.CheckOut);
        saved.Message.Should().Contain("after 1 h 30 min").And.Contain("Fee: 25,000 VND.");
        saved.DataJson.Should().Contain("\"durationMinutes\":\"90\"");
    }

    [Theory]
    [InlineData(45, null, "after 45 min.")]
    [InlineData(120, "Car towed", "after 2 h. The session was closed by the lot Manager: Car towed.")]
    [InlineData(120, null, "after 2 h. The session was closed by the lot Manager.")]
    public async Task SessionCheckedOutAsync_Should_Explain_Free_And_Manager_Closed_Sessions(int minutes, string note, string expected)
    {
        var session = AddSession(note == null && minutes == 45 ? SessionStatus.Completed : SessionStatus.AbnormalClosed, exitTime: Now);
        session.TotalDurationMinutes = minutes;
        session.ClosedNote = note;
        await _context.SaveChangesAsync();

        (await _jobs.SessionCheckedOutAsync(session.Id)).Should().BeTrue();

        _context.UserNotifications.Single().Message.Should().EndWith(expected);
    }

    [Fact]
    public async Task SessionCheckedOutAsync_Should_Use_The_Paid_Amount_And_Skip_Sessions_Still_Inside()
    {
        (await _jobs.SessionCheckedOutAsync(AddSession(SessionStatus.Active).Id)).Should().BeFalse();

        var session = AddSession(SessionStatus.Completed, exitTime: Now);
        session.PaidAmount = 10000;
        await _context.SaveChangesAsync();

        (await _jobs.SessionCheckedOutAsync(session.Id)).Should().BeTrue();
        _context.UserNotifications.Single().Message.Should().Contain("Fee: 10,000 VND.");
    }

    [Fact]
    public async Task SessionCheckedOutAsync_Should_Name_A_Missing_Lot_Generically()
    {
        var session = AddSession(SessionStatus.Completed, exitTime: Now);
        session.ParkingLotId = 12345;
        await _context.SaveChangesAsync();

        (await _jobs.SessionCheckedOutAsync(session.Id)).Should().BeTrue();

        _context.UserNotifications.Single().Title.Should().Be("Checked out of the parking lot");
    }

    // ---- NotificationJobs: purge ----

    [Fact]
    public async Task PurgeOldNotificationsAsync_Should_Drop_Old_Read_And_Very_Old_Unread()
    {
        AddNotification(createdAt: Now.AddDays(-61), readAt: Now.AddDays(-61));
        AddNotification(createdAt: Now.AddDays(-181));
        var keptUnread = AddNotification(createdAt: Now.AddDays(-61));
        var keptRead = AddNotification(createdAt: Now.AddDays(-10), readAt: Now);

        (await _jobs.PurgeOldNotificationsAsync()).Should().Be(2);

        _context.UserNotifications.Select(n => n.Id).Should().BeEquivalentTo([keptUnread.Id, keptRead.Id]);
    }

    // ---- Hangfire publisher ----

    [Fact]
    public void HangfireUserNotificationPublisher_Should_Enqueue_One_Job_Per_Event()
    {
        var client = Substitute.For<IBackgroundJobClient>();
        var publisher = new HangfireUserNotificationPublisher(client);

        publisher.VehicleReviewed(Guid.NewGuid());
        publisher.SessionCheckedIn(1);
        publisher.SessionCheckedOut(2);

        client.Received(3).Create(Arg.Is<Job>(j => j.Type == typeof(NotificationJobs)), Arg.Any<IState>());
    }
}
