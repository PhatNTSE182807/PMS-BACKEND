using System;

namespace PMS.Application.Models.Profile;

public class VehicleResponseModel
{
    public Guid VehicleId { get; set; }

    public string LicensePlate { get; set; }

    public int VehicleTypeId { get; set; }

    public string VehicleType { get; set; }

    public string Brand { get; set; }

    public string Model { get; set; }

    public string Color { get; set; }

    /// <summary>Citizen ID (CCCD) of the registered owner.</summary>
    public string CitizenId { get; set; }

    public bool IsDefault { get; set; }

    public bool IsActive { get; set; }

    /// <summary>
    /// Registration status from the latest paper check: null (no papers submitted yet), Draft,
    /// NeedsReview (waiting for Staff), Approved or Rejected. Only an approved vehicle can be booked.
    /// </summary>
    public string VerificationStatus { get; set; }

    /// <summary>Staff note on the latest decision (the reason when rejected).</summary>
    public string VerificationNote { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
