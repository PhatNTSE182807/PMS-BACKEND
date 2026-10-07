using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Shift
{
    public long Id { get; set; }

    public string ShiftCode { get; set; }

    public int ParkingLotId { get; set; }

    public int? GateId { get; set; }

    public int EmployeeId { get; set; }

    public int? ScheduleId { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public decimal OpeningCash { get; set; }

    public decimal? ExpectedCash { get; set; }

    public decimal? CountedCash { get; set; }

    public decimal? CashDifference { get; set; }

    public int TotalSessions { get; set; }

    public string Status { get; set; }

    public int? HandoverToEmployeeId { get; set; }

    public string HandoverNote { get; set; }

    public string ApprovedBy { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public string Note { get; set; }

    public byte[] RowVersion { get; set; }
}
