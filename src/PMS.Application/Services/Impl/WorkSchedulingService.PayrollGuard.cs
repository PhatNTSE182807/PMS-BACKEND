using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;
public partial class WorkSchedulingService
{
    private async Task EnsurePayrollImmutableChangesAsync()
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State is EntityState.Modified or EntityState.Deleted).ToList()) {
            if (entry.Entity is PayrollAttendanceSource or PayrollAdjustment or PayrollAudit)
                throw new ConflictException("Sealed payroll evidence, adjustments and audit entries are append-only.");
            if (entry.Entity is PayrollSlip) {
                var original = entry.OriginalValues.GetValue<string>("Status");
                if (original == "Paid" || entry.State == EntityState.Deleted)
                    throw new ConflictException("Paid payslips cannot be edited or deleted; use a subsequent-period adjustment.");
                if (original == "Approved" && entry.Properties.Any(p => p.IsModified && p.Metadata.Name is not ("Status" or "PaidAt" or "TransactionReference" or "Revision")))
                    throw new ConflictException("Approved amounts and source explanations are immutable.");
            }
            if (entry.Entity is PayrollLine line) {
                var status = await db.PayrollSlips.Where(s => s.Id == line.PayrollSlipId).Select(s => s.Status).SingleAsync();
                if (status != "Draft") throw new ConflictException("Only Draft payroll lines may be recalculated.");
            }
        }
    }
}
