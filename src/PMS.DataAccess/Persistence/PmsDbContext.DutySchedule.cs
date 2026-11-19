using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.DataAccess.Persistence;

/// <summary>Duty board (weekly pattern + one-day changes), shift attendance, leave and payslips.
/// Created by AutomatedMigration.EnsureDutyScheduleTablesAsync.</summary>
public partial class PmsDbContext
{
    public virtual DbSet<DutyPattern> DutyPatterns { get; set; }
    public virtual DbSet<DutyPatternCell> DutyPatternCells { get; set; }
    public virtual DbSet<DutyOverride> DutyOverrides { get; set; }
    public virtual DbSet<DutyShiftAttendance> DutyShiftAttendances { get; set; }
    public virtual DbSet<DutyLeaveRequest> DutyLeaveRequests { get; set; }
    public virtual DbSet<DutyLeaveDay> DutyLeaveDays { get; set; }
    public virtual DbSet<WorkLeaveBalance> WorkLeaveBalances { get; set; }
    public virtual DbSet<PaySlip> PaySlips { get; set; }
    public virtual DbSet<PayAdjustment> PayAdjustments { get; set; }

    private static void ConfigureDutySchedule(ModelBuilder b)
    {
        b.Entity<Employee>().Property(e => e.EmploymentType).IsRequired().HasMaxLength(20).IsUnicode(false);

        b.Entity<DutyPattern>(e =>
        {
            e.ToTable("DutyPatterns");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ParkingLotId, x.EffectiveFrom }).IsUnique();
            e.Property(x => x.CreatedByUserId).HasMaxLength(450);
            e.Property(x => x.CreatedAt).HasPrecision(3);
        });
        b.Entity<DutyPatternCell>(e =>
        {
            e.ToTable("DutyPatternCells");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.DutyPatternId, x.DayOfWeek, x.Slot }).IsUnique();
        });
        b.Entity<DutyOverride>(e =>
        {
            e.ToTable("DutyOverrides");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ParkingLotId, x.WorkDate, x.Slot }).IsUnique();
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.CreatedByUserId).HasMaxLength(450);
            e.Property(x => x.CreatedAt).HasPrecision(3);
        });
        b.Entity<DutyShiftAttendance>(e =>
        {
            e.ToTable("DutyShiftAttendances");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ParkingLotId, x.WorkDate, x.Slot, x.EmployeeId }).IsUnique();
            e.HasIndex(x => new { x.EmployeeId, x.WorkDate });
            e.Property(x => x.Status).IsRequired().HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.AdjustedByUserId).HasMaxLength(450);
            e.Property(x => x.AdjustReason).HasMaxLength(500);
            e.Property(x => x.CheckInAt).HasPrecision(3);
            e.Property(x => x.CheckOutAt).HasPrecision(3);
            e.Property(x => x.CreatedAt).HasPrecision(3);
            e.Property(x => x.UpdatedAt).HasPrecision(3);
        });
        b.Entity<DutyLeaveRequest>(e =>
        {
            e.ToTable("DutyLeaveRequests");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ParkingLotId, x.Status });
            e.HasIndex(x => new { x.EmployeeId, x.FromDate });
            e.Property(x => x.Reason).IsRequired().HasMaxLength(500);
            e.Property(x => x.Status).IsRequired().HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.DecisionReason).HasMaxLength(500);
            e.Property(x => x.DecidedByUserId).HasMaxLength(450);
            e.Property(x => x.DecidedAt).HasPrecision(3);
            e.Property(x => x.CreatedAt).HasPrecision(3);
        });
        b.Entity<DutyLeaveDay>(e =>
        {
            e.ToTable("DutyLeaveDays");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.EmployeeId, x.LeaveDate });
        });
        b.Entity<WorkLeaveBalance>(e =>
        {
            e.ToTable("WorkLeaveBalances");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).IsRequired().HasMaxLength(450);
            e.HasIndex(x => new { x.UserId, x.Year }).IsUnique();
            e.Property(x => x.Revision).IsConcurrencyToken();
        });
        b.Entity<PaySlip>(e =>
        {
            e.ToTable("PaySlips");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Month, x.Status });
            e.HasIndex(x => new { x.EmployeeId, x.Month });
            e.Property(x => x.UserId).IsRequired().HasMaxLength(450);
            e.Property(x => x.Kind).IsRequired().HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.Status).IsRequired().HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.PaidByUserId).HasMaxLength(450);
            e.Property(x => x.BaseAmount).HasPrecision(18, 0);
            e.Property(x => x.Deductions).HasPrecision(18, 0);
            e.Property(x => x.Overtime).HasPrecision(18, 0);
            e.Property(x => x.Adjustments).HasPrecision(18, 0);
            e.Property(x => x.NetAmount).HasPrecision(18, 0);
            e.Property(x => x.PaidAt).HasPrecision(3);
            e.Property(x => x.CreatedAt).HasPrecision(3);
            e.Property(x => x.UpdatedAt).HasPrecision(3);
        });
        b.Entity<PayAdjustment>(e =>
        {
            e.ToTable("PayAdjustments");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.EmployeeId, x.Month });
            e.Property(x => x.Amount).HasPrecision(18, 0);
            e.Property(x => x.Reason).IsRequired().HasMaxLength(500);
            e.Property(x => x.CreatedByUserId).HasMaxLength(450);
            e.Property(x => x.CreatedAt).HasPrecision(3);
        });
    }
}
