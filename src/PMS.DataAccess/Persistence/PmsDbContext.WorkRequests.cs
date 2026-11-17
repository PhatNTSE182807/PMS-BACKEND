using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.DataAccess.Persistence;
public partial class PmsDbContext
{
    public DbSet<WorkLeaveRequest> WorkLeaveRequests { get; set; }
    public DbSet<WorkLeaveBalance> WorkLeaveBalances { get; set; }
    public DbSet<WorkLeaveDay> WorkLeaveDays { get; set; }
    public DbSet<WorkSwapRequest> WorkSwapRequests { get; set; }
    public DbSet<WorkOpenShift> WorkOpenShifts { get; set; }
    public DbSet<WorkOpenShiftClaim> WorkOpenShiftClaims { get; set; }
    public DbSet<WorkManagerCoverage> WorkManagerCoverages { get; set; }
    public DbSet<WorkScheduleWorkflowEvent> WorkScheduleWorkflowEvents { get; set; }
    private static void ConfigureWorkRequests(ModelBuilder b)
    {
        b.Entity<DutyAssignment>(e => {
            e.Property(x => x.ReplacementReason).HasMaxLength(1000);
            e.Property(x => x.CancellationKind).HasMaxLength(30);
            e.HasOne<DutyAssignment>().WithMany().HasForeignKey(x => x.OriginalAssignmentId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_DutyAssignments_Original");
        });
        b.Entity<WorkLeaveRequest>(e => {
            e.ToTable("WorkLeaveRequests"); e.HasKey(x => x.Id); e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.Kind).IsRequired().HasMaxLength(20); e.Property(x => x.Status).IsRequired().HasMaxLength(30);
            e.Property(x => x.Reason).IsRequired().HasMaxLength(1000); e.Property(x => x.DecisionReason).HasMaxLength(1000);
            e.HasIndex(x => new { x.ParkingLotId, x.Status });
            e.HasOne<DutyAssignment>().WithMany().HasForeignKey(x => x.DutyAssignmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<WorkLeaveBalance>(e => {
            e.ToTable("WorkLeaveBalances"); e.HasKey(x => x.Id); e.Property(x => x.UserId).IsRequired().HasMaxLength(450);
            e.HasIndex(x => new { x.UserId, x.Year }).IsUnique(); e.Property(x => x.Revision).IsConcurrencyToken();
        });
        b.Entity<WorkLeaveDay>(e => {
            e.ToTable("WorkLeaveDays"); e.HasKey(x => x.Id); e.Property(x => x.UserId).IsRequired().HasMaxLength(450);
            e.HasIndex(x => new { x.UserId, x.LeaveDate }).IsUnique();
            e.HasOne<WorkLeaveRequest>().WithMany().HasForeignKey(x => x.FirstLeaveRequestId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<WorkSwapRequest>(e => {
            e.ToTable("WorkSwapRequests"); e.HasKey(x => x.Id); e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.Status).IsRequired().HasMaxLength(30); e.Property(x => x.Reason).IsRequired().HasMaxLength(1000); e.Property(x => x.DecisionReason).HasMaxLength(1000);
            e.HasOne<DutyAssignment>().WithMany().HasForeignKey(x => x.AssignmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DutyAssignment>().WithMany().HasForeignKey(x => x.OtherAssignmentId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<WorkOpenShift>(e => {
            e.ToTable("WorkOpenShifts"); e.HasKey(x => x.Id); e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.Status).IsRequired().HasMaxLength(30);
            e.Property(x => x.Reason).IsRequired().HasMaxLength(1000);
            e.Property(x => x.ExpiresAt).HasColumnName("DeadlineAt");
            e.HasIndex(x => new { x.ParkingLotId, x.Status });
            e.HasOne<DutyRosterShift>().WithMany().HasForeignKey(x => x.DutyRosterShiftId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DutyAssignment>().WithMany().HasForeignKey(x => x.OriginalAssignmentId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<WorkOpenShiftClaim>(e => {
            e.ToTable("WorkOpenShiftClaims"); e.HasKey(x => x.Id); e.Property(x => x.Status).IsRequired().HasMaxLength(30);
            e.HasIndex(x => new { x.WorkOpenShiftId, x.EmployeeId }).IsUnique();
            e.HasOne<WorkOpenShift>().WithMany().HasForeignKey(x => x.WorkOpenShiftId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<WorkManagerCoverage>(e => {
            e.ToTable("WorkManagerCoverages"); e.HasKey(x => x.Id); e.Property(x => x.ManagerUserId).IsRequired().HasMaxLength(450);
            e.Property(x => x.Reason).IsRequired().HasMaxLength(1000);
            e.HasIndex(x => x.WorkOpenShiftId).IsUnique();
            e.HasOne<WorkOpenShift>().WithMany().HasForeignKey(x => x.WorkOpenShiftId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DutyRosterShift>().WithMany().HasForeignKey(x => x.DutyRosterShiftId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<WorkScheduleWorkflowEvent>(e => {
            e.ToTable("WorkScheduleWorkflowEvents"); e.HasKey(x => x.Id); e.Property(x => x.ActorId).IsRequired().HasMaxLength(450);
            e.Property(x => x.Kind).IsRequired().HasMaxLength(30); e.Property(x => x.Action).IsRequired().HasMaxLength(50); e.Property(x => x.Reason).HasMaxLength(1000);
            e.HasIndex(x => new { x.ParkingLotId, x.Kind, x.EntityId });
        });
    }
}
