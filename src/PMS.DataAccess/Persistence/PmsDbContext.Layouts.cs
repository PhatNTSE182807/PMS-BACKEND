using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.DataAccess.Persistence;

/// <summary>Layout 3D tables (and the lot's template), kept apart from the scaffolded model. Created by AutomatedMigration.</summary>
public partial class PmsDbContext
{
    public virtual DbSet<LotLayout> LotLayouts { get; set; }

    public virtual DbSet<LotLayoutSlot> LotLayoutSlots { get; set; }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ParkingLot>()
            .Property(e => e.LayoutTemplate)
            .HasMaxLength(30)
            .IsUnicode(false);

        modelBuilder.Entity<LotLayout>(entity =>
        {
            entity.HasIndex(e => new { e.ParkingLotId, e.Status }, "IX_LotLayouts_ParkingLotId_Status");

            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Draft");
            entity.Property(e => e.TemplateId).HasMaxLength(30).IsUnicode(false);
            entity.Property(e => e.FloorsJson).IsRequired();
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CreatedBy).HasMaxLength(450);
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
            entity.Property(e => e.PublishedAt).HasPrecision(3);
            entity.Property(e => e.PublishedBy).HasMaxLength(450);
        });

        modelBuilder.Entity<LotLayoutSlot>(entity =>
        {
            entity.HasIndex(e => new { e.LayoutId, e.SlotId }, "IX_LotLayoutSlots_LayoutId_SlotId").IsUnique();

            entity.Property(e => e.ModelUrl).HasMaxLength(500);

            entity.HasOne(d => d.Layout).WithMany(p => p.Slots)
                .HasForeignKey(d => d.LayoutId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_LotLayoutSlots_LotLayouts");
        });
    }
}
