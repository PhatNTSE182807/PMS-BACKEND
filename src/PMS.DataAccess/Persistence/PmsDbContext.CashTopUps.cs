using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.DataAccess.Persistence;

/// <summary>
/// Banknotes a Staff asks the Manager to add to their cash drawer. Created by
/// AutomatedMigration.EnsureOperationsTablesAsync.
/// </summary>
public partial class PmsDbContext
{
    public virtual DbSet<CashTopUpRequest> CashTopUpRequests { get; set; }

    private static void ConfigureCashTopUps(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CashTopUpRequest>(entity =>
        {
            entity.ToTable("CashTopUpRequests");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ParkingLotId, e.Status, e.CreatedAt }, "IX_CashTopUpRequests_Lot");
            entity.HasIndex(e => e.DailyCashCloseId, "IX_CashTopUpRequests_Drawer");

            entity.Property(e => e.StaffUserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.NotesJson).IsRequired().HasMaxLength(400);
            entity.Property(e => e.Total).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Reason).HasMaxLength(500);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.DecidedBy).HasMaxLength(450);
            entity.Property(e => e.DecisionNote).HasMaxLength(500);
            entity.Property(e => e.CreatedAt).HasPrecision(3);
            entity.Property(e => e.DecidedAt).HasPrecision(3);
        });
    }
}
