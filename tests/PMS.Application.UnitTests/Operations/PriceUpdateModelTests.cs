using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>The EF model behind deferred price updates: what the database must enforce.</summary>
public sealed class PriceUpdateModelTests : IDisposable
{
    private readonly PmsDbContext _db = new(
        new DbContextOptionsBuilder<PmsDbContext>().UseInMemoryDatabase($"PriceUpdateModel_{Guid.NewGuid()}").Options);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void ParkingLot_row_version_is_a_concurrency_token()
    {
        var property = _db.Model.FindEntityType(typeof(ParkingLot))!.FindProperty(nameof(ParkingLot.RowVersion))!;

        property.IsConcurrencyToken.Should().BeTrue();
        property.IsNullable.Should().BeFalse();
        property.ValueGenerated.Should().Be(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAddOrUpdate);
    }

    [Fact]
    public void A_pricing_rule_has_at_most_one_pending_change()
    {
        var index = _db.Model.FindEntityType(typeof(PendingPriceChange))!
            .GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(PendingPriceChange.PricingRuleId)]));

        index.IsUnique.Should().BeTrue();
    }

    [Fact]
    public void A_session_keeps_the_price_it_entered_with()
    {
        var property = _db.Model.FindEntityType(typeof(ParkingSession))!.FindProperty(nameof(ParkingSession.PriceSnapshotJson))!;

        property.IsNullable.Should().BeTrue("sessions created before the column existed have no copy");
        // The in-memory provider has no relational type mapping, so read what the model asked for
        property.FindAnnotation("Relational:ColumnType")!.Value.Should().Be("nvarchar(max)");
    }

    [Fact]
    public async Task A_new_lot_starts_empty_and_can_be_saved()
    {
        var lot = new ParkingLot { Code = "T1", Name = "Test lot", Address = "1 Test Street", Status = "Active", CreatedAt = DateTime.UtcNow };
        _db.ParkingLots.Add(lot);

        await _db.SaveChangesAsync();

        (await _db.ParkingLots.AsNoTracking().SingleAsync()).OccupiedBays.Should().Be(0);
    }
}
