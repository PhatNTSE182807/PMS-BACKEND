using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.DataAccess.Persistence;

/// <summary>Price changes saved as drafts. Created by AutomatedMigration.EnsurePricingRuleDraftsTableAsync.</summary>
public partial class PmsDbContext
{
    public virtual DbSet<PricingRuleDraft> PricingRuleDrafts { get; set; }

    private static void ConfigurePricingRuleDrafts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PricingRuleDraft>(entity =>
        {
            entity.ToTable("PricingRuleDrafts");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ParkingLotId, e.Status }, "IX_PricingRuleDrafts_Lot_Status");

            entity.Property(e => e.ValuesJson).IsRequired();
            entity.Property(e => e.Note).HasMaxLength(500);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.CreatedBy).HasMaxLength(450);
            entity.Property(e => e.UpdatedBy).HasMaxLength(450);
            entity.Property(e => e.ClosedBy).HasMaxLength(450);
            entity.Property(e => e.CreatedAt).HasPrecision(3);
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
            entity.Property(e => e.ClosedAt).HasPrecision(3);
        });
    }
}
