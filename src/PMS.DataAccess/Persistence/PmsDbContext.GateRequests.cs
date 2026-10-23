using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.DataAccess.Persistence;

/// <summary>
/// Check-in / check-out requests sent from the customer app to the lot's Staff. Created by
/// AutomatedMigration.EnsureGateRequestsTableAsync.
/// </summary>
public partial class PmsDbContext
{
    public virtual DbSet<GateRequest> GateRequests { get; set; }

    private static void ConfigureGateRequests(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GateRequest>(entity =>
        {
            entity.ToTable("GateRequests");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ParkingLotId, e.Kind, e.Status, e.CreatedAt }, "IX_GateRequests_Queue");
            entity.HasIndex(e => new { e.UserId, e.CreatedAt }, "IX_GateRequests_UserId");

            entity.Property(e => e.Kind).HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.Status).HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.UserId).HasMaxLength(450);
            entity.Property(e => e.PlateNumber).HasMaxLength(20);
            entity.Property(e => e.HandledBy).HasMaxLength(450);
            entity.Property(e => e.Note).HasMaxLength(500);
            entity.Property(e => e.CreatedAt).HasPrecision(3);
            entity.Property(e => e.HandledAt).HasPrecision(3);
        });
    }
}
