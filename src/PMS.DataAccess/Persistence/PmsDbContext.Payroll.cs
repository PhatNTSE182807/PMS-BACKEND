using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.DataAccess.Persistence;
public partial class PmsDbContext
{
    public DbSet<PayrollProfile> PayrollProfiles { get; set; }
    public DbSet<PayrollAttendanceSource> PayrollAttendanceSources { get; set; }
    public DbSet<PayrollSlip> PayrollSlips { get; set; }
    public DbSet<PayrollLine> PayrollLines { get; set; }
    public DbSet<PayrollAdjustment> PayrollAdjustments { get; set; }
    public DbSet<PayrollComplaint> PayrollComplaints { get; set; }
    public DbSet<PayrollAudit> PayrollAudits { get; set; }

    private static void ConfigurePayroll(ModelBuilder b)
    {
        b.Entity<PayrollProfile>(e => {
            e.ToTable("PayrollProfiles"); e.HasKey(x => x.Id); e.Property(x => x.UserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.Role).HasMaxLength(20).IsRequired(); e.Property(x => x.CreatedBy).HasMaxLength(450).IsRequired();
            e.Property(x => x.Revision).IsConcurrencyToken(); e.HasIndex(x => new { x.UserId, x.EffectiveFrom }).IsUnique();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired(); e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.OvertimeCombination).HasMaxLength(40); e.Property(x => x.PaidLeaveMethod).HasMaxLength(30);
            e.Property(x => x.ManagerProration).HasMaxLength(30); e.Property(x => x.ManagerUnpaidLeavePolicy).HasMaxLength(30);
            e.Property(x => x.RoundingMode).HasMaxLength(30);
            foreach (var name in new[] { "HourlyRate", "MonthlyRate", "StandbyDailyAllowance", "StandardWorkDays", "PaidLeaveHoursPerDay", "NightFactor", "HolidayFactor", "OvertimeFactor" })
                e.Property<decimal?>(name).HasPrecision(18, 6);
        });
        b.Entity<PayrollAttendanceSource>(e => {
            e.ToTable("PayrollAttendanceSources"); e.HasKey(x => x.Id); e.HasIndex(x => x.AttendancePeriodLotId).IsUnique();
            e.Property(x => x.SnapshotJson).IsRequired(); e.Property(x => x.SnapshotHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(450).IsRequired();
            e.HasOne<AttendancePeriodLot>().WithMany().HasForeignKey(x => x.AttendancePeriodLotId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<PayrollSlip>(e => {
            e.ToTable("PayrollSlips"); e.HasKey(x => x.Id); e.Property(x => x.UserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.EmployeeName).HasMaxLength(200).IsRequired(); e.Property(x => x.Role).HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired(); e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.Total).HasPrecision(18, 4); e.Property(x => x.BasisHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.TransactionReference).HasMaxLength(200); e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.UserId, x.ParkingLotId, x.PayMonth }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.PayMonth, x.Role }).IsUnique().HasFilter("[Role] = N'Manager'");
            e.HasOne<ParkingLot>().WithMany().HasForeignKey(x => x.ParkingLotId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<PayrollLine>(e => {
            e.ToTable("PayrollLines"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.PayrollSlipId, x.Ordinal }).IsUnique();
            e.Property(x => x.Kind).HasMaxLength(40).IsRequired(); e.Property(x => x.Description).HasMaxLength(500).IsRequired();
            e.Property(x => x.Unit).HasMaxLength(20).IsRequired();
            e.Property(x => x.Quantity).HasPrecision(28, 16); e.Property(x => x.UnitRate).HasPrecision(18, 6);
            e.Property(x => x.Factor).HasPrecision(18, 6); e.Property(x => x.Amount).HasPrecision(18, 4);
            e.HasOne<PayrollSlip>().WithMany().HasForeignKey(x => x.PayrollSlipId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PayrollProfile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DutyAssignment>().WithMany().HasForeignKey(x => x.AssignmentId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<PayrollAdjustment>(e => {
            e.ToTable("PayrollAdjustments"); e.HasKey(x => x.Id); e.Property(x => x.UserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.Role).HasMaxLength(20).IsRequired(); e.Property(x => x.Kind).HasMaxLength(30).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired(); e.Property(x => x.CreatedBy).HasMaxLength(450).IsRequired();
            e.Property(x => x.Quantity).HasPrecision(18, 8); e.Property(x => x.UnitRate).HasPrecision(18, 6);
            e.HasIndex(x => x.SourceComplaintId).IsUnique().HasFilter("[SourceComplaintId] IS NOT NULL");
            e.HasIndex(x => x.SourceCarryId).IsUnique().HasFilter("[SourceCarryId] IS NOT NULL");
            e.HasOne<PayrollSlip>().WithMany().HasForeignKey(x => x.SourceSlipId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ParkingLot>().WithMany().HasForeignKey(x => x.ParkingLotId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<PayrollComplaint>(e => {
            e.ToTable("PayrollComplaints"); e.HasKey(x => x.Id); e.Property(x => x.UserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired(); e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ResolutionReason).HasMaxLength(1000); e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasOne<PayrollSlip>().WithMany().HasForeignKey(x => x.PayrollSlipId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<PayrollAudit>(e => {
            e.ToTable("PayrollAudits"); e.HasKey(x => x.Id); e.Property(x => x.ActorId).HasMaxLength(450).IsRequired();
            e.Property(x => x.Action).HasMaxLength(50).IsRequired(); e.Property(x => x.Reason).HasMaxLength(1000);
            e.HasIndex(x => new { x.PayrollSlipId, x.Id });
        });
        b.Entity<DutyStandbyAssignment>().Property(x => x.WasCalled).HasDefaultValue(false);
    }
}
