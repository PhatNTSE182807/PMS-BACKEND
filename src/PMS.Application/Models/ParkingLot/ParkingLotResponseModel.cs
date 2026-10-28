using System;

namespace PMS.Application.Models.ParkingLot;

public class ParkingLotResponseModel
{
    public int Id { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    public string Address { get; set; }

    public string City { get; set; }

    public string District { get; set; }

    public TimeOnly? OpenTime { get; set; }

    public TimeOnly? CloseTime { get; set; }

    public bool Is24Hours { get; set; }

    public int Capacity { get; set; }

    public string LayoutTemplate { get; set; }

    public string ImageUrl { get; set; }

    public string Description { get; set; }

    public string Phone { get; set; }

    /// <summary>
    /// What everyone sees: an Active lot without an active Manager and Staff is reported as Maintenance.
    /// <see cref="StoredStatus"/> keeps the status the Admin set.
    /// </summary>
    public string Status { get; set; }

    /// <summary>The status saved on the lot (Active, Maintenance, TemporarilyClosed, Inactive).</summary>
    public string StoredStatus { get; set; }

    /// <summary>True when the lot has an active Manager and at least one active Staff member.</summary>
    public bool IsStaffed { get; set; }

    /// <summary>True when customers can book it: open and staffed.</summary>
    public bool IsBookable { get; set; }

    public DateTime? ClosedFrom { get; set; }

    public DateTime? ClosedUntil { get; set; }

    public string ClosureReason { get; set; }

    public string MaintenanceNotice { get; set; }

    public int? PartnerId { get; set; }

    public string ManagerUserId { get; set; }

    public string TimeZoneId { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string CreatedBy { get; set; }

    public int TotalZones { get; set; }

    public int TotalSlots { get; set; }
}
