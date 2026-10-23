using System;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// A message shown in the customer's notification bell: a vehicle registration approved or rejected,
/// a vehicle checked in or out. Written by a Hangfire job (NotificationJobs) after the change is saved.
/// </summary>
public class UserNotification
{
    public long Id { get; set; }

    /// <summary>Identity user the notification belongs to (dbo.Users.Id).</summary>
    public string UserId { get; set; }

    /// <summary>"VehicleApproved", "VehicleRejected", "CheckIn" or "CheckOut".</summary>
    public string Type { get; set; }

    public string Title { get; set; }

    public string Message { get; set; }

    /// <summary>What the notification is about: "VehicleVerification" or "ParkingSession".</summary>
    public string RefType { get; set; }

    /// <summary>Id of that record; with Type it keeps a retried job from writing the notification twice.</summary>
    public string RefId { get; set; }

    /// <summary>When the event happened (check-in / check-out time, decision time), UTC.</summary>
    public DateTime? EventAt { get; set; }

    /// <summary>Optional JSON with extra values for the UI (plate, lot, fee...).</summary>
    public string DataJson { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ReadAt { get; set; }
}
