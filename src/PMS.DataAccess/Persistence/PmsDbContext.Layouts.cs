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
        // Vehicle paper checks live in PmsDbContext.VehicleDocuments.cs
        ConfigureVehicleDocuments(modelBuilder);

        // Reservations, gate sessions and payments live in PmsDbContext.Operations.cs
        ConfigureOperations(modelBuilder);

        // Check-in / check-out requests from the customer app live in PmsDbContext.GateRequests.cs
        ConfigureGateRequests(modelBuilder);

        // Customer notifications (vehicle approved / rejected, check-in, check-out) live in PmsDbContext.Notifications.cs
        ConfigureNotifications(modelBuilder);

        // Deferred price updates (lot occupancy, price copied at entry, pending changes) live in PmsDbContext.PriceUpdates.cs
        ConfigurePriceUpdates(modelBuilder);

        // Banknotes a Staff asks the Manager to add to their cash drawer live in PmsDbContext.CashTopUps.cs
        ConfigureCashTopUps(modelBuilder);

        // Booked bays taken by a walk-in and the compensation of a pricing rule live in PmsDbContext.BookedBayCases.cs
        ConfigureBookedBayCases(modelBuilder);

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
