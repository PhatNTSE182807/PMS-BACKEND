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

    public virtual DbSet<DailyCashClose> DailyCashCloses { get; set; }

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

        modelBuilder.Entity<DailyCashClose>(entity =>
        {
            entity.ToTable("DailyCashCloses");
            entity.HasIndex(e => new { e.ParkingLotId, e.StaffUserId, e.BusinessDate, e.CloseNo }, "IX_DailyCashCloses_Drawer").IsUnique();
            entity.Property(e => e.StaffUserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.FloatJson).HasMaxLength(400);
            entity.Property(e => e.CountedJson).HasMaxLength(400);
            entity.Property(e => e.FloatTotal).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.CountedTotal).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.ExpectedTotal).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Difference).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.DifferenceReason).HasMaxLength(500);
            entity.Property(e => e.ConfirmedBy).HasMaxLength(450);
            entity.Property(e => e.ConfirmNote).HasMaxLength(500);
            entity.Property(e => e.OpenedAt).HasPrecision(3);
            entity.Property(e => e.ClosedAt).HasPrecision(3);
            entity.Property(e => e.ConfirmedAt).HasPrecision(3);
            entity.Property(e => e.CloseDueAt).HasPrecision(3);
            entity.Property(e => e.OverdueAt).HasPrecision(3);
        });

        modelBuilder.Entity<SessionViolation>(entity =>
        {
            entity.Property(e => e.CitizenIdPhotoPath).HasMaxLength(500);
            entity.Property(e => e.RegistrationPhotoPath).HasMaxLength(500);
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.Property(e => e.CashReceivedJson).HasMaxLength(400);
            entity.Property(e => e.CashChangeJson).HasMaxLength(400);
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
