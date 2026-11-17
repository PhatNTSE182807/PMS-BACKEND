using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.DataAccess.Persistence;

/// <summary>Customers' account deletion requests. Created by AutomatedMigration.EnsureAccountDeletionRequestsTableAsync.</summary>
public partial class PmsDbContext
{
    public virtual DbSet<AccountDeletionRequest> AccountDeletionRequests { get; set; }

    private static void ConfigureAccountDeletionRequests(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountDeletionRequest>(entity =>
        {
            entity.ToTable("AccountDeletionRequests");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.UserId, e.Status }, "IX_AccountDeletionRequests_User_Status");

            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.Reason).HasMaxLength(500);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.ClosedBy).HasMaxLength(450);
            entity.Property(e => e.RequestedAt).HasPrecision(3);
            entity.Property(e => e.ClosedAt).HasPrecision(3);
        });
    }
}
