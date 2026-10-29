using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MockQueryable;
using NSubstitute;
using PMS.Application.Models.Operations;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

public class AdminDashboardServiceTests : IDisposable
{
    // 2026-10-29 10:00 in Vietnam (UTC+7): today starts 2026-10-28 17:00 UTC, the month starts 2026-09-30 17:00 UTC.
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 3, 0, 0, TimeSpan.Zero);

    private readonly PmsDbContext _db;
    private readonly List<ApplicationUser> _users = [];
    private readonly AdminDashboardService _service;

    public AdminDashboardServiceTests()
    {
        var options = new DbContextOptionsBuilder<PmsDbContext>()
            .UseInMemoryDatabase($"AdminDashboardTests_{Guid.NewGuid()}")
            .Options;
        _db = new PmsDbContext(options);

        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        var userManager = Substitute.For<UserManager<ApplicationUser>>(userStore, null, null, null, null, null, null, null, null);
        userManager.Users.Returns(_ => _users.BuildMock());

        _service = new AdminDashboardService(_db, userManager, new TestClock(Now));
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private ParkingLot AddLot(int id, string name, string address = "1 Test St", string district = "District 1", string city = "Ho Chi Minh City", bool isDeleted = false)
    {
        var lot = new ParkingLot
        {
            Id = id,
            Code = $"LOT_{id}",
            Name = name,
            Address = address,
            District = district,
            City = city,
            Status = LotStatus.Active,
            IsDeleted = isDeleted,
            CreatedAt = DateTime.UtcNow
        };
        _db.ParkingLots.Add(lot);
        _db.SaveChanges();
        return lot;
    }

    private Zone AddZone(int id, int lotId, bool isActive = true, int? counterTotal = null, int counterOccupied = 0)
    {
        var zone = new Zone { Id = id, ParkingLotId = lotId, Code = $"Z{id}", Name = $"Zone {id}", IsActive = isActive, CreatedAt = DateTime.UtcNow };
        _db.Zones.Add(zone);
        if (counterTotal != null)
        {
            _db.ZoneCounters.Add(new ZoneCounter
            {
                Id = id,
                ZoneId = id,
                TotalCapacity = counterTotal.Value,
                OccupiedSpaces = counterOccupied,
                AvailableSpaces = counterTotal.Value - counterOccupied,
                LastUpdatedAt = DateTime.UtcNow
            });
        }
        _db.SaveChanges();
        return zone;
    }

    private void AddSlot(int id, int zoneId, string status, bool isActive = true)
    {
        _db.Slots.Add(new Slot { Id = id, ZoneId = zoneId, VehicleTypeId = 1, Code = $"S{id}", Status = status, IsActive = isActive, RowVersion = [1, 2, 3] });
        _db.SaveChanges();
    }

    private void AddSession(long id, int lotId, decimal fee, string status, DateTime? exitTime, DateTime? updatedAt = null)
    {
        _db.ParkingSessions.Add(new ParkingSession
        {
            Id = id,
            SessionCode = $"SS_{id}",
            ParkingLotId = lotId,
            EntryGateId = 1,
            EntryPlateNumber = "51A-12345",
            EntryPlateNormalized = "51A12345",
            RowVersion = [1, 2, 3],
            EntryTime = (exitTime ?? updatedAt ?? Now.UtcDateTime).AddHours(-2),
            ExitTime = exitTime,
            OriginalFee = fee,
            FinalFee = fee,
            Status = status,
            UpdatedAt = updatedAt ?? exitTime
        });
        _db.SaveChanges();
    }

    private void AddUser(string id, string role, bool isActive = true) =>
        _users.Add(new ApplicationUser { Id = id, UserName = id, Role = role, IsActive = isActive });

    [Fact]
    public async Task Hubs_SplitTodayAndMonthToDate_InTheParkingTimeZone()
    {
        var lot = AddLot(1, "Lot A");
        AddSession(1, lot.Id, 30_000m, SessionStatus.Completed, new DateTime(2026, 10, 29, 1, 0, 0, DateTimeKind.Utc));       // today, 08:00
        AddSession(2, lot.Id, 20_000m, SessionStatus.Completed, new DateTime(2026, 10, 28, 16, 0, 0, DateTimeKind.Utc));      // 23:00 yesterday: this month only
        AddSession(3, lot.Id, 99_999m, SessionStatus.Completed, new DateTime(2026, 9, 30, 16, 59, 0, DateTimeKind.Utc));      // 23:59 on 30 Sep: last month
        AddSession(4, lot.Id, 10_000m, SessionStatus.Paid, null, new DateTime(2026, 10, 29, 2, 0, 0, DateTimeKind.Utc));      // paid, not left yet: today

        var hubs = await _service.GetHubsAsync();

        var hub = hubs.Should().ContainSingle().Subject;
        hub.TodayRevenue.Should().Be(40_000m);
        hub.MTDRevenue.Should().Be(60_000m);
    }

    [Fact]
    public async Task Hubs_IgnoreSessionsThatAreNotSettled()
    {
        var lot = AddLot(1, "Lot A");
        var today = new DateTime(2026, 10, 29, 1, 0, 0, DateTimeKind.Utc);
        AddSession(1, lot.Id, 50_000m, SessionStatus.Active, null, today);
        AddSession(2, lot.Id, 50_000m, SessionStatus.PendingPayment, null, today);
        AddSession(3, lot.Id, 50_000m, SessionStatus.Cancelled, today);
        AddSession(4, lot.Id, 50_000m, SessionStatus.AbnormalClosed, today);

        var hub = (await _service.GetHubsAsync()).Single();

        hub.TodayRevenue.Should().Be(0m);
        hub.MTDRevenue.Should().Be(0m);
    }

    [Fact]
    public async Task Hubs_ListEveryLotWithoutDeletedOnes_BestMonthFirst_AndKeepZeroRevenueLots()
    {
        var quiet = AddLot(1, "Quiet Lot", address: "2 Quiet St", district: "", city: "Ho Chi Minh City");
        var busy = AddLot(2, "Busy Lot");
        AddLot(3, "Gone Lot", isDeleted: true);
        var exit = new DateTime(2026, 10, 20, 5, 0, 0, DateTimeKind.Utc);
        AddSession(1, busy.Id, 70_000m, SessionStatus.Completed, exit);
        AddSession(2, 3, 900_000m, SessionStatus.Completed, exit);

        var hubs = await _service.GetHubsAsync();

        hubs.Select(h => h.LotName).Should().Equal("Busy Lot", "Quiet Lot");
        hubs[0].Location.Should().Be("1 Test St, District 1, Ho Chi Minh City");
        hubs[1].Location.Should().Be("2 Quiet St, Ho Chi Minh City");
        hubs[1].LotId.Should().Be(quiet.Id);
        hubs[1].MTDRevenue.Should().Be(0m);
    }

    [Fact]
    public async Task Bays_UseTheZoneCounter_AndFallBackToSlotsWhenAZoneHasNoCounter()
    {
        var lot = AddLot(1, "Lot A");
        AddZone(1, lot.Id, counterTotal: 100, counterOccupied: 40);
        AddZone(2, lot.Id, isActive: false, counterTotal: 500, counterOccupied: 500); // inactive zone: left out
        AddZone(3, lot.Id);                                                            // no counter: slots decide
        AddSlot(1, 3, SlotStatus.Occupied);
        AddSlot(2, 3, SlotStatus.Available);
        AddSlot(3, 3, SlotStatus.Reserved);
        AddSlot(4, 3, SlotStatus.Occupied, isActive: false);                           // switched off: not configured

        var hub = (await _service.GetHubsAsync()).Single();

        hub.TotalBays.Should().Be(103);
        hub.OccupiedBays.Should().Be(41);
        hub.OccupancyRate.Should().Be(39.8);
    }

    [Fact]
    public async Task Overview_SumsAllSettledRevenue_Bays_AndActiveAccounts()
    {
        var a = AddLot(1, "Lot A");
        var b = AddLot(2, "Lot B");
        var gone = AddLot(3, "Gone Lot", isDeleted: true);
        AddZone(1, a.Id, counterTotal: 60, counterOccupied: 30);
        AddZone(2, b.Id, counterTotal: 40, counterOccupied: 10);
        AddZone(3, gone.Id, counterTotal: 999, counterOccupied: 999);

        AddSession(1, a.Id, 30_000m, SessionStatus.Completed, new DateTime(2026, 10, 29, 1, 0, 0, DateTimeKind.Utc));
        AddSession(2, b.Id, 99_999m, SessionStatus.Completed, new DateTime(2026, 8, 1, 1, 0, 0, DateTimeKind.Utc)); // old, still counts
        AddSession(3, a.Id, 10_000m, SessionStatus.Paid, null, new DateTime(2026, 10, 29, 2, 0, 0, DateTimeKind.Utc));
        AddSession(4, a.Id, 50_000m, SessionStatus.Active, null, new DateTime(2026, 10, 29, 2, 0, 0, DateTimeKind.Utc));
        AddSession(5, gone.Id, 777_000m, SessionStatus.Completed, new DateTime(2026, 10, 29, 1, 0, 0, DateTimeKind.Utc));

        AddUser("c1", "User");
        AddUser("c2", "User");
        AddUser("c3", "User", isActive: false);
        AddUser("m1", "Manager");
        AddUser("m2", "Manager", isActive: false);
        AddUser("s1", "Staff");
        AddUser("s2", "Staff");
        AddUser("s3", "Staff", isActive: false);
        AddUser("admin", "Admin");

        var overview = await _service.GetOverviewAsync();

        overview.TotalGrossRevenue.Should().Be(139_999m);
        overview.TotalBays.Should().Be(100);
        overview.OccupiedBays.Should().Be(40);
        overview.NetworkLoadFactor.Should().Be(40.0);
        overview.ActiveUsers.Should().Be(2);
        overview.ActiveStaff.Should().Be(3);
    }

    [Fact]
    public async Task EmptySystem_ReturnsZeros_WithoutDividingByZero()
    {
        var overview = await _service.GetOverviewAsync();
        var hubs = await _service.GetHubsAsync();

        overview.TotalGrossRevenue.Should().Be(0m);
        overview.TotalBays.Should().Be(0);
        overview.NetworkLoadFactor.Should().Be(0);
        overview.ActiveUsers.Should().Be(0);
        overview.ActiveStaff.Should().Be(0);
        hubs.Should().BeEmpty();
    }

    [Fact]
    public async Task LotWithoutZones_HasZeroBaysAndZeroRate()
    {
        AddLot(1, "Empty Lot");

        var hub = (await _service.GetHubsAsync()).Single();

        hub.TotalBays.Should().Be(0);
        hub.OccupancyRate.Should().Be(0);
    }
}
