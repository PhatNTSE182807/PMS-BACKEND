using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.DataAccess.Persistence;
public partial class PmsDbContext
{
    public DbSet<AttendancePeriod> AttendancePeriods { get; set; }
    public DbSet<AttendancePeriodLot> AttendancePeriodLots { get; set; }
    public DbSet<AttendanceOvertimeDecision> AttendanceOvertimeDecisions { get; set; }
    public DbSet<ManagerWorkDay> ManagerWorkDays { get; set; }
    public DbSet<AttendanceCarryAdjustment> AttendanceCarryAdjustments { get; set; }
    public DbSet<AttendancePeriodEvent> AttendancePeriodEvents { get; set; }
    private static void ConfigureAttendancePeriods(ModelBuilder b)
    {
        b.Entity<AttendancePeriod>(e => {
            e.ToTable("AttendancePeriods"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired(); e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.CreatedBy).HasMaxLength(450).IsRequired(); e.Property(x => x.LockedBy).HasMaxLength(450);
        });
        b.Entity<AttendancePeriodLot>(e => {
            e.ToTable("AttendancePeriodLots"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AttendancePeriodId, x.ParkingLotId }).IsUnique();
            e.HasIndex(x => new { x.ParkingLotId, x.StartsAt, x.EndsAt });
            e.Property(x => x.Status).HasMaxLength(20).IsRequired(); e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired(); e.Property(x => x.ConfirmedBy).HasMaxLength(450);
            e.HasOne<AttendancePeriod>().WithMany().HasForeignKey(x => x.AttendancePeriodId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ParkingLot>().WithMany().HasForeignKey(x => x.ParkingLotId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<AttendanceOvertimeDecision>(e => {
            e.ToTable("AttendanceOvertimeDecisions"); e.HasKey(x => x.Id);
            e.Property(x => x.CalculationKey).HasMaxLength(64);
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired(); e.Property(x => x.DecidedBy).HasMaxLength(450).IsRequired();
            e.HasIndex(x => new { x.DutyAssignmentId, x.Id });
            e.HasOne<DutyAssignment>().WithMany().HasForeignKey(x => x.DutyAssignmentId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<ManagerWorkDay>(e => {
            e.ToTable("ManagerWorkDays"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ManagerUserId, x.WorkDate }).IsUnique();
            e.Property(x => x.ManagerUserId).HasMaxLength(450).IsRequired(); e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired(); e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasOne<ParkingLot>().WithMany().HasForeignKey(x => x.ParkingLotId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<AttendanceCarryAdjustment>(e => {
            e.ToTable("AttendanceCarryAdjustments"); e.HasKey(x => x.Id); e.Property(x => x.Hours).HasPrecision(12, 4);
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired(); e.Property(x => x.CreatedBy).HasMaxLength(450).IsRequired();
            e.HasOne<AttendancePeriodLot>().WithMany().HasForeignKey(x => x.SourcePeriodLotId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AttendancePeriodLot>().WithMany().HasForeignKey(x => x.TargetPeriodLotId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DutyAssignment>().WithMany().HasForeignKey(x => x.DutyAssignmentId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<AttendancePeriodEvent>(e => {
            e.ToTable("AttendancePeriodEvents"); e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(40).IsRequired(); e.Property(x => x.ActorId).HasMaxLength(450).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.HasOne<AttendancePeriodLot>().WithMany().HasForeignKey(x => x.AttendancePeriodLotId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
