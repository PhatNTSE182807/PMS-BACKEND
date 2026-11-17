using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.DataAccess.Persistence;

public partial class PmsDbContext
{
    public DbSet<WorkShiftTemplate> WorkShiftTemplates { get; set; }
    public DbSet<DutyRoster> DutyRosters { get; set; }
    public DbSet<DutyRosterShift> DutyRosterShifts { get; set; }
    public DbSet<DutyAssignment> DutyAssignments { get; set; }
    public DbSet<DutyRosterChangeLog> DutyRosterChangeLogs { get; set; }
    public DbSet<DutyAttendance> DutyAttendances { get; set; }
    public DbSet<DutyAttendanceEvent> DutyAttendanceEvents { get; set; }

    private static void ConfigureWorkScheduling(ModelBuilder b)
    {
        ConfigureWorkRotation(b);
        ConfigureWorkRequests(b);
        ConfigureAttendancePeriods(b);
        ConfigurePayroll(b);
        b.Entity<WorkShiftTemplate>(e =>
        {
            e.ToTable("WorkShiftTemplates");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(100);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.ParkingLotId, x.IsActive });
            e.HasOne<ParkingLot>().WithMany().HasForeignKey(x => x.ParkingLotId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<DutyRoster>(e =>
        {
            e.ToTable("DutyRosters");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(100);
            e.Property(x => x.Status).IsRequired().HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.CreatedBy).IsRequired().HasMaxLength(450);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.CreatedAt).HasPrecision(3);
            e.Property(x => x.PublishedAt).HasPrecision(3);
            e.HasIndex(x => new { x.ParkingLotId, x.FromDate, x.ToDate });
            e.HasOne<ParkingLot>().WithMany().HasForeignKey(x => x.ParkingLotId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<DutyRosterShift>(e =>
        {
            e.ToTable("DutyRosterShifts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(100);
            e.Property(x => x.TimeZoneId).IsRequired().HasMaxLength(100);
            e.Property(x => x.StartsAt).HasPrecision(3);
            e.Property(x => x.EndsAt).HasPrecision(3);
            e.HasIndex(x => new { x.DutyRosterId, x.WorkDate });
            e.HasOne<DutyRoster>().WithMany().HasForeignKey(x => x.DutyRosterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<WorkShiftTemplate>().WithMany().HasForeignKey(x => x.WorkShiftTemplateId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<DutyAssignment>(e =>
        {
            e.ToTable("DutyAssignments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.Source).IsRequired().HasMaxLength(20);
            e.Property(x => x.GenerationKey).HasMaxLength(100);
            e.Property(x => x.AttendanceTrackingFrom).HasPrecision(3).HasDefaultValueSql("sysutcdatetime()");
            e.HasIndex(x => new { x.EmployeeId, x.IsCancelled });
            e.HasOne<DutyRosterShift>().WithMany().HasForeignKey(x => x.DutyRosterShiftId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<DutyRosterChangeLog>(e =>
        {
            e.ToTable("DutyRosterChangeLogs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).IsRequired().HasMaxLength(40).IsUnicode(false);
            e.Property(x => x.ChangedBy).IsRequired().HasMaxLength(450);
            e.Property(x => x.ChangedAt).HasPrecision(3);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasIndex(x => new { x.DutyRosterId, x.Revision }).IsUnique();
            e.HasOne<DutyRoster>().WithMany().HasForeignKey(x => x.DutyRosterId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<DutyAttendance>(e =>
        {
            e.ToTable("DutyAttendances");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DutyAssignmentId).IsUnique();
            e.Property(x => x.CheckInAt).HasPrecision(3);
            e.Property(x => x.CheckOutAt).HasPrecision(3);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasOne<DutyAssignment>().WithMany().HasForeignKey(x => x.DutyAssignmentId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<DutyAttendanceEvent>(e =>
        {
            e.ToTable("DutyAttendanceEvents");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.DutyAssignmentId, x.Revision }).IsUnique();
            e.Property(x => x.Action).IsRequired().HasMaxLength(30).IsUnicode(false);
            e.Property(x => x.ActorId).IsRequired().HasMaxLength(450);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.CreatedAt).HasPrecision(3);
            e.HasOne<DutyAssignment>().WithMany().HasForeignKey(x => x.DutyAssignmentId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
