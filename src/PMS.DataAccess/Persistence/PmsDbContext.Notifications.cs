using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.DataAccess.Persistence;

/// <summary>Customer notifications (bell on the dashboard). Created by AutomatedMigration.EnsureNotificationsTableAsync.</summary>
public partial class PmsDbContext
{
    public virtual DbSet<UserNotification> UserNotifications { get; set; }

    private static void ConfigureNotifications(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserNotification>(entity =>
        {
            entity.ToTable("UserNotifications");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.UserId, e.CreatedAt }, "IX_UserNotifications_UserId_CreatedAt");
            entity.HasIndex(e => new { e.Type, e.RefId }, "IX_UserNotifications_Type_RefId");

            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.Type).IsRequired().HasMaxLength(30).IsUnicode(false);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Message).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.RefType).HasMaxLength(30).IsUnicode(false);
            entity.Property(e => e.RefId).HasMaxLength(100);
            entity.Property(e => e.EventAt).HasPrecision(3);
            entity.Property(e => e.CreatedAt).HasPrecision(3);
            entity.Property(e => e.ReadAt).HasPrecision(3);
        });
    }
}
