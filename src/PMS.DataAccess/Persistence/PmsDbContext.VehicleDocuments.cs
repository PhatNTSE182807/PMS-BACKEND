using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.DataAccess.Persistence;

/// <summary>Vehicle paper checks (cà vẹt + CCCD photos and their OCR result). Created by AutomatedMigration.</summary>
public partial class PmsDbContext
{
    public virtual DbSet<VehicleVerification> VehicleVerifications { get; set; }

    public virtual DbSet<VehicleDocument> VehicleDocuments { get; set; }

    private static void ConfigureVehicleDocuments(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VehicleVerification>(entity =>
        {
            entity.HasIndex(e => new { e.VehicleId, e.CreatedAt }, "IX_VehicleVerifications_VehicleId_CreatedAt");
            entity.HasIndex(e => e.Status, "IX_VehicleVerifications_Status");

            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.Confidence).HasPrecision(5, 4);
            entity.Property(e => e.StatusReason).HasMaxLength(500);
            entity.Property(e => e.ReviewedBy).HasMaxLength(450);
            entity.Property(e => e.ReviewNote).HasMaxLength(500);
            entity.Property(e => e.CreatedAt).HasPrecision(3).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.SubmittedAt).HasPrecision(3);
            entity.Property(e => e.ProcessedAt).HasPrecision(3);
            entity.Property(e => e.DecidedAt).HasPrecision(3);
            entity.Property(e => e.FilesDeletedAt).HasPrecision(3);

            entity.HasOne(d => d.Vehicle).WithMany()
                .HasForeignKey(d => d.VehicleId)
                .HasConstraintName("FK_VehicleVerifications_Vehicles");
        });

        modelBuilder.Entity<VehicleDocument>(entity =>
        {
            entity.HasIndex(e => e.VerificationId, "IX_VehicleDocuments_VerificationId");

            entity.Property(e => e.DocumentType).IsRequired().HasMaxLength(30).IsUnicode(false);
            entity.Property(e => e.Side).IsRequired().HasMaxLength(10).IsUnicode(false);
            entity.Property(e => e.StoragePath).HasMaxLength(500);
            entity.Property(e => e.OriginalFileName).HasMaxLength(260);
            entity.Property(e => e.ContentType).IsRequired().HasMaxLength(50).IsUnicode(false);
            entity.Property(e => e.UploadedAt).HasPrecision(3).HasDefaultValueSql("(sysutcdatetime())");

            entity.HasOne(d => d.Verification).WithMany(p => p.Documents)
                .HasForeignKey(d => d.VerificationId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_VehicleDocuments_VehicleVerifications");
        });
    }
}
