using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.DataAccess.Persistence;

/// <summary>
/// Price updates: the lot's occupancy counter and concurrency token, and the price copied onto a session at
/// entry. Created or altered by AutomatedMigration.EnsureOperationsTablesAsync.
/// </summary>
public partial class PmsDbContext
{
    public virtual DbSet<DailyCashClose> DailyCashCloses { get; set; }

    private static void ConfigurePriceUpdates(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ParkingLot>(entity =>
        {
            // Every writer of the lot's occupancy or price state saves this row, so SQL Server's rowversion
            // turns a race (a vehicle entering while a price changes or another vehicle enters)
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

        // Legacy: the price older bookings were made at, no longer read
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

    }
}
