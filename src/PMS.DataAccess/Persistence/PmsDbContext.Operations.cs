using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.DataAccess.Persistence;

/// <summary>
/// Reservations, gate sessions and payments: the columns added to the scaffolded tables and the new
/// PlateLists / ReservationChangeLogs tables. Created or altered by AutomatedMigration.EnsureOperationsTablesAsync.
/// </summary>
public partial class PmsDbContext
{
    public virtual DbSet<PlateListEntry> PlateLists { get; set; }

    public virtual DbSet<ReservationChangeLog> ReservationChangeLogs { get; set; }

    private static void ConfigureOperations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Reservation>(entity =>
        {
            entity.HasIndex(e => e.ReservationCode, "IX_Reservations_ReservationCode").IsUnique();
            entity.HasIndex(e => new { e.SlotId, e.Status }, "IX_Reservations_SlotId_Status");
            entity.HasIndex(e => new { e.UserId, e.Status }, "IX_Reservations_UserId_Status");

            entity.Property(e => e.UserId).HasMaxLength(450);
            entity.Property(e => e.DepositDueAt).HasPrecision(3);
            entity.Property(e => e.ConfirmedAt).HasPrecision(3);
            entity.Property(e => e.CheckedInAt).HasPrecision(3);
            entity.Property(e => e.CompletedAt).HasPrecision(3);
            entity.Property(e => e.CancelReason).HasMaxLength(500);
            entity.Property(e => e.CancelledBy).HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.RefundAmount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.ReminderSentAt).HasPrecision(3);
            entity.Property(e => e.HoldReminderSentAt).HasPrecision(3);
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
            entity.Property(e => e.BookingMode).HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.DepartureTime).HasPrecision(3);
            entity.Property(e => e.SlotHeldAt).HasPrecision(3);
        });

        modelBuilder.Entity<ParkingSession>(entity =>
        {
            entity.HasIndex(e => e.SessionCode, "IX_ParkingSessions_SessionCode").IsUnique();

            entity.Property(e => e.SessionType).HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.ManualEntryReason).HasMaxLength(500);
            entity.Property(e => e.NoPlateReason).HasMaxLength(500);
            entity.Property(e => e.ManualExitReason).HasMaxLength(500);
            entity.Property(e => e.EntryConfirmedAt).HasPrecision(3);
            entity.Property(e => e.DepositApplied).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.PaidAmount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.ExitWindowEndsAt).HasPrecision(3);
            entity.Property(e => e.ZeroFeeReason).HasMaxLength(100);
            entity.Property(e => e.OverrideNote).HasMaxLength(1000);
            entity.Property(e => e.ClosedNote).HasMaxLength(500);
            entity.Property(e => e.OverdueNotifiedAt).HasPrecision(3);
            entity.Property(e => e.AbandonedFlaggedAt).HasPrecision(3);
            entity.Property(e => e.CreatedAt).HasPrecision(3);
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
        });

        modelBuilder.Entity<Invoice>(entity =>
        {
            entity.HasIndex(e => e.InvoiceNumber, "IX_Invoices_InvoiceNumber").IsUnique();
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.HasIndex(e => e.PaymentCode, "IX_Payments_PaymentCode").IsUnique();
            entity.HasIndex(e => new { e.Status, e.CreatedAt }, "IX_Payments_Status_CreatedAt");

            entity.Property(e => e.Purpose).HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.CustomerUserId).HasMaxLength(450);
            entity.Property(e => e.CollectedByUserId).HasMaxLength(450);
            entity.Property(e => e.Channel).HasMaxLength(10).IsUnicode(false);
            entity.Property(e => e.ExpiresAt).HasPrecision(3);
            entity.Property(e => e.RefundedAmount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.GatewayTransactionNo).HasMaxLength(100);
            entity.Property(e => e.FailureReason).HasMaxLength(500);
            entity.Property(e => e.PayUrl).HasMaxLength(2000);
            entity.Property(e => e.LastCheckedAt).HasPrecision(3);
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
        });

        modelBuilder.Entity<PlateListEntry>(entity =>
        {
            entity.ToTable("PlateLists");
            entity.HasIndex(e => new { e.PlateNormalized, e.ListType }, "IX_PlateLists_PlateNormalized_ListType");

            entity.Property(e => e.ListType).IsRequired().HasMaxLength(10).IsUnicode(false);
            entity.Property(e => e.PlateNumber).IsRequired().HasMaxLength(20);
            entity.Property(e => e.PlateNormalized).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ExpiresAt).HasPrecision(0);
            entity.Property(e => e.CreatedBy).HasMaxLength(450);
            entity.Property(e => e.CreatedAt).HasPrecision(3).HasDefaultValueSql("(sysutcdatetime())");
        });

        modelBuilder.Entity<ReservationChangeLog>(entity =>
        {
            entity.HasIndex(e => e.ReservationId, "IX_ReservationChangeLogs_ReservationId");

            entity.Property(e => e.Action).IsRequired().HasMaxLength(30).IsUnicode(false);
            entity.Property(e => e.OldValue).HasMaxLength(200);
            entity.Property(e => e.NewValue).HasMaxLength(200);
            entity.Property(e => e.ChangedBy).HasMaxLength(450);
            entity.Property(e => e.ChangedAt).HasPrecision(3).HasDefaultValueSql("(sysutcdatetime())");
        });
    }
}
