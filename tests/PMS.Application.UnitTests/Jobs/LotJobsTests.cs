using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using PMS.Application.Jobs;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Jobs;

public class LotJobsTests
{
    private readonly PmsDbContext _context;
    private readonly ILotRealtimeNotifier _notifier = Substitute.For<ILotRealtimeNotifier>();
    private readonly LotJobs _sut;

    public LotJobsTests()
    {
        var options = new DbContextOptionsBuilder<PmsDbContext>()
            .UseInMemoryDatabase(databaseName: $"LotJobsTests_{Guid.NewGuid()}")
            .Options;
        _context = new PmsDbContext(options);
        _sut = new LotJobs(_context, _notifier);
    }

    private ParkingLot AddLot(string code, string status, DateTime? closedUntil)
    {
        var lot = new ParkingLot
        {
            Code = code,
            Name = code,
            Address = "1 Test St",
            Status = status,
            ClosedFrom = closedUntil?.AddHours(-2),
            ClosedUntil = closedUntil,
            ClosureReason = closedUntil == null ? null : "Repainting"
        };
        _context.ParkingLots.Add(lot);
        _context.SaveChanges();
        return lot;
    }

    [Fact]
    public async Task ReopenDueLotsAsync_Should_Reopen_Only_Lots_Whose_Closure_Has_Ended()
    {
        var due = AddLot("DUE", "TemporarilyClosed", DateTime.UtcNow.AddMinutes(-1));
        var later = AddLot("LATER", "TemporarilyClosed", DateTime.UtcNow.AddHours(1));
        var maintenance = AddLot("MAINT", "Maintenance", DateTime.UtcNow.AddMinutes(-1));

        var reopened = await _sut.ReopenDueLotsAsync();

        reopened.Should().Be(1);
        (await _context.ParkingLots.FindAsync(due.Id))!.Should().BeEquivalentTo(new { Status = "Active", ClosedUntil = (DateTime?)null, ClosureReason = (string)null });
        (await _context.ParkingLots.FindAsync(later.Id))!.Status.Should().Be("TemporarilyClosed");
        (await _context.ParkingLots.FindAsync(maintenance.Id))!.Status.Should().Be("Maintenance");
        await _notifier.Received(1).LotStatusChangedAsync(due.Id, "Active");
    }

    [Fact]
    public async Task CleanupExpiredCredentialsAsync_Should_Keep_Recent_Codes_And_Tokens()
    {
        var old = DateTime.UtcNow - LotJobs.CredentialRetention - TimeSpan.FromHours(1);
        _context.OtpCodes.AddRange(
            new OtpCode { UserId = "u1", Destination = "a@test.com", Channel = "Email", Purpose = "Login", CodeHash = "x", ExpiresAt = old, CreatedAt = old },
            new OtpCode { UserId = "u1", Destination = "a@test.com", Channel = "Email", Purpose = "Login", CodeHash = "y", ExpiresAt = DateTime.UtcNow.AddMinutes(5), CreatedAt = DateTime.UtcNow });
        _context.RefreshTokens.AddRange(
            new RefreshToken { UserId = "u1", TokenHash = "old", ExpiresAt = old, CreatedAt = old },
            new RefreshToken { UserId = "u1", TokenHash = "live", ExpiresAt = DateTime.UtcNow.AddDays(1), CreatedAt = DateTime.UtcNow });
        await _context.SaveChangesAsync();

        var removed = await _sut.CleanupExpiredCredentialsAsync();

        removed.Should().Be(2);
        _context.OtpCodes.Select(o => o.CodeHash).Should().Equal("y");
        _context.RefreshTokens.Select(t => t.TokenHash).Should().Equal("live");
    }

    [Fact]
    public async Task SlotService_UpdateStatusAsync_Should_Push_The_New_Status_To_The_Lot()
    {
        var lot = AddLot("PUSH", "Active", null);
        var zone = new Zone { ParkingLotId = lot.Id, Code = "Z1", Name = "Zone 1", IsActive = true };
        var type = new VehicleType { Code = "CAR_4", Name = "Car", IsActive = true };
        _context.AddRange(zone, type);
        await _context.SaveChangesAsync();
        var slot = new Slot { ZoneId = zone.Id, VehicleTypeId = type.Id, Code = "L1-01", Status = "Available", IsActive = true, RowVersion = [1] };
        _context.Slots.Add(slot);
        await _context.SaveChangesAsync();

        await new SlotService(_context, _notifier).UpdateStatusAsync(slot.Id, "maintenance");

        await _notifier.Received(1).SlotStatusChangedAsync(lot.Id, slot.Id, "Maintenance");
    }
}
