using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class ParkingSession
{
    public long Id { get; set; }

    public string SessionCode { get; set; }

    public int ParkingLotId { get; set; }

    public int? CustomerId { get; set; }

    public Guid? VehicleId { get; set; }

    public int? AccessCardId { get; set; }

    public long? SubscriptionId { get; set; }

    public long? ReservationId { get; set; }

    public int EntryGateId { get; set; }

    public DateTime EntryTime { get; set; }

    public string EntryPlateNumber { get; set; }

    public string EntryPlateNormalized { get; set; }

    public string EntryImageFrontUrl { get; set; }

    public string EntryImageRearUrl { get; set; }

    public string EntryImageOverviewUrl { get; set; }

    public string EntryOperatorUserId { get; set; }

    public decimal? EntryAnprConfidence { get; set; }

    public int? ZoneId { get; set; }

    public int? SlotId { get; set; }

    public int? ExitGateId { get; set; }

    public DateTime? ExitTime { get; set; }

    public string ExitPlateNumber { get; set; }

    public string ExitPlateNormalized { get; set; }

    public string ExitImageFrontUrl { get; set; }

    public string ExitImageRearUrl { get; set; }

    public string ExitImageOverviewUrl { get; set; }

    public string ExitOperatorUserId { get; set; }

    public decimal? ExitAnprConfidence { get; set; }

    public int? PricingRuleId { get; set; }

    public int? TotalDurationMinutes { get; set; }

    public decimal OriginalFee { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal FinalFee { get; set; }

    public string Status { get; set; }

    public bool IsLostCard { get; set; }

    public byte[] RowVersion { get; set; }

    public virtual Customer Customer { get; set; }

    public virtual Gate EntryGate { get; set; }

    public virtual Gate ExitGate { get; set; }

    public virtual ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();

    public virtual ParkingLot ParkingLot { get; set; }

    public virtual Vehicle Vehicle { get; set; }
}
