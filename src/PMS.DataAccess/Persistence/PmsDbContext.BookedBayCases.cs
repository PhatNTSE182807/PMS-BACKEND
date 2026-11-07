using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.DataAccess.Persistence;

/// <summary>
/// Booked bays taken by a walk-in, and the compensation a pricing rule pays for them. Created by
/// AutomatedMigration.EnsureOperationsTablesAsync.
/// </summary>
public partial class PmsDbContext
{
    public virtual DbSet<BookedBayCase> BookedBayCases { get; set; }

    private static void ConfigureBookedBayCases(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BookedBayCase>(entity =>
        {
            entity.ToTable("BookedBayCases");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ParkingLotId, e.Status, e.CreatedAt }, "IX_BookedBayCases_Lot");
            entity.HasIndex(e => e.ReservationId, "IX_BookedBayCases_Reservation");

            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.DepositRefunded).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.CompensationPaid).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.ResolutionNote).HasMaxLength(500);
            entity.Property(e => e.ResolvedBy).HasMaxLength(450);
            entity.Property(e => e.ResolvedAt).HasPrecision(3);
            entity.Property(e => e.CreatedAt).HasPrecision(3);
        });

        modelBuilder.Entity<PricingRule>().Property(e => e.BookedBayCompensation).HasColumnType("decimal(18, 2)");
    }
}
