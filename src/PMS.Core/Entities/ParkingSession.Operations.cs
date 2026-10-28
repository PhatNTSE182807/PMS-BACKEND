using System;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// Gate and payment fields of a parking session (SRS FR-OPS-01/02, FR-PAY-07, section 4.3.2 and
/// section 8 "ParkingSession"), kept apart from the scaffolded columns.
/// </summary>
public partial class ParkingSession
{
    /// <summary>"WalkIn", "Reservation" or "Whitelist".</summary>
    public string SessionType { get; set; }

    public int? VehicleTypeId { get; set; }

    /// <summary>False for vehicles without a plate (FR-OPS-10): the ticket and photos identify them.</summary>
    public bool HasPlate { get; set; } = true;

    /// <summary>Entry recorded by hand on the POS (FR-OPS-03), with the reason.</summary>
    public bool IsManualEntry { get; set; }

    public string ManualEntryReason { get; set; }

    /// <summary>Why a vehicle entered without a plate (unreadable or missing); null when it has a plate. Not a device failure, so it is not a manual entry.</summary>
    public string NoPlateReason { get; set; }

    public bool IsManualExit { get; set; }

    public string ManualExitReason { get; set; }

    /// <summary>The loop sensor confirmed the vehicle passed the barrier; only then the session counts.</summary>
    public DateTime? EntryConfirmedAt { get; set; }

    /// <summary>Version of the pricing rule in force at entry (BR-22).</summary>
    public int? PricingRuleVersion { get; set; }

    /// <summary>Reservation deposit deducted from the fee at check-out (FR-RSV-07).</summary>
    public decimal DepositApplied { get; set; }

    /// <summary>Money collected for this session so far (cash and online, refunds excluded).</summary>
    public decimal PaidAmount { get; set; }

    /// <summary>Prepaid at a counter, kiosk or the app: the vehicle must leave before this (FR-PAY-07).</summary>
    public DateTime? ExitWindowEndsAt { get; set; }

    /// <summary>Why no payment was needed (BR-09): grace period, whitelist.</summary>
    public string ZeroFeeReason { get; set; }

    /// <summary>Entry and exit plates differ (FR-OPS-04).</summary>
    public bool PlateMismatch { get; set; }

    /// <summary>Staff decisions taken at the gate (blacklist, zone full, low ANPR confidence...).</summary>
    public string OverrideNote { get; set; }

    /// <summary>Note on an abnormal close or emergency exit (section 4.3.2).</summary>
    public string ClosedNote { get; set; }

    /// <summary>BR-20: overdue alert sent (24 h by default).</summary>
    public DateTime? OverdueNotifiedAt { get; set; }

    /// <summary>BR-20: flagged as abandoned (7 days by default) and an incident was opened.</summary>
    public DateTime? AbandonedFlaggedAt { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
