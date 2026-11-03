using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.DataAccess.Persistence;

/// <summary>
/// Deferred price updates: the lot's occupancy counter and concurrency token, the price copied onto a
/// session at entry, and the PendingPriceChanges table. Created or altered by
/// AutomatedMigration.EnsureOperationsTablesAsync.
/// </summary>
public partial class PmsDbContext
{
    public virtual DbSet<PendingPriceChange> PendingPriceChanges { get; set; }

    private static void ConfigurePriceUpdates(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ParkingLot>(entity =>
        {
            // Every writer of the lot's occupancy or price state saves this row, so SQL Server's rowversion
            // turns a race (last vehicle leaving while another enters, price change while a vehicle leaves)
            // into a DbUpdateConcurrencyException for the loser, which re-reads and decides again.
            entity.Property(e => e.OccupiedBays).HasDefaultValue(0);
            entity.Property(e => e.RowVersion)
                .IsRequired()
                .IsRowVersion()
                .IsConcurrencyToken();
        });

        modelBuilder.Entity<ParkingSession>(entity =>
        {
            entity.Property(e => e.PriceSnapshotJson).HasColumnType("nvarchar(max)");
        });

        // The price a customer was shown when booking: the check-in uses it instead of the lot's live price
        modelBuilder.Entity<Reservation>(entity =>
        {
            entity.Property(e => e.PriceSnapshotJson).HasColumnType("nvarchar(max)");
        });

        modelBuilder.Entity<PendingPriceChange>(entity =>
        {
            entity.ToTable("PendingPriceChanges");

            // One pending change per rule: a second request updates this row instead of adding another
            entity.HasIndex(e => e.PricingRuleId, "IX_PendingPriceChanges_PricingRuleId").IsUnique();
            // "Does this lot have a price waiting?" is asked on every last exit
            entity.HasIndex(e => e.ParkingLotId, "IX_PendingPriceChanges_ParkingLotId");

            entity.Property(e => e.ProposedValuesJson)
                .IsRequired()
                .HasColumnType("nvarchar(max)");
            entity.Property(e => e.RevisionNo).HasDefaultValue(1);
            entity.Property(e => e.RequestedBy).HasMaxLength(450);
            entity.Property(e => e.RequestedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.UpdatedAt).HasPrecision(3);

            entity.HasOne(d => d.ParkingLot).WithMany(p => p.PendingPriceChanges)
                .HasForeignKey(d => d.ParkingLotId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_PendingPriceChanges_ParkingLots");

            entity.HasOne(d => d.PricingRule).WithOne()
                .HasForeignKey<PendingPriceChange>(d => d.PricingRuleId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_PendingPriceChanges_PricingRules");
        });
    }
}
