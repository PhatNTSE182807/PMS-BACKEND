using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.DataAccess.Persistence;
public partial class PmsDbContext
{
    public DbSet<WorkRotationGroup> WorkRotationGroups { get; set; }
    public DbSet<WorkRotationMember> WorkRotationMembers { get; set; }
    public DbSet<WorkRotationCycle> WorkRotationCycles { get; set; }
    public DbSet<DutyStandbyAssignment> DutyStandbyAssignments { get; set; }
    private static void ConfigureWorkRotation(ModelBuilder b)
    {
        b.Entity<WorkRotationGroup>(e => {
            e.ToTable("WorkRotationGroups"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(100); e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasOne<ParkingLot>().WithMany().HasForeignKey(x => x.ParkingLotId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<WorkRotationMember>(e => {
            e.ToTable("WorkRotationMembers"); e.HasKey(x => new { x.WorkRotationGroupId, x.EmployeeId });
            e.Property(x => x.Position).IsRequired().HasMaxLength(20).IsUnicode(false);
            e.HasOne<WorkRotationGroup>().WithMany().HasForeignKey(x => x.WorkRotationGroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<WorkRotationCycle>(e => {
            e.ToTable("WorkRotationCycles"); e.HasKey(x => x.Id); e.Property(x => x.Name).IsRequired().HasMaxLength(100);
            e.Property(x => x.StepsJson).IsRequired(); e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasOne<ParkingLot>().WithMany().HasForeignKey(x => x.ParkingLotId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<DutyStandbyAssignment>(e => {
            e.ToTable("DutyStandbyAssignments"); e.HasKey(x => x.Id); e.Property(x => x.Source).IsRequired().HasMaxLength(20);
            e.HasIndex(x => new { x.DutyRosterId, x.WorkDate });
            e.HasOne<DutyRoster>().WithMany().HasForeignKey(x => x.DutyRosterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
