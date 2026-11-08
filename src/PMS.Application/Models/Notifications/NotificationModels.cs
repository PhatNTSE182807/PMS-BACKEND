using System;
using System.Collections.Generic;

namespace PMS.Application.Models.Notifications;

/// <summary>Kinds of customer notifications.</summary>
public static class NotificationTypes
{
    /// <summary>Staff approved the papers of a newly registered vehicle.</summary>
    public const string VehicleApproved = "VehicleApproved";
    /// <summary>Staff rejected the papers; the vehicle was removed from the account.</summary>
    public const string VehicleRejected = "VehicleRejected";
    /// <summary>The vehicle entered a lot (the entry was confirmed).</summary>
    public const string CheckIn = "CheckIn";
    /// <summary>The vehicle left a lot (or the session was closed by a Manager).</summary>
    public const string CheckOut = "CheckOut";
    /// <summary>A Manager changed a lot's price.</summary>
    public const string PriceChanged = "PriceChanged";
    /// <summary>A walk-in parked in the customer's booked bay; Staff will move the booking.</summary>
    public const string BookedBayTaken = "BookedBayTaken";
    /// <summary>Staff moved the booking to another bay.</summary>
    public const string BookedBayReassigned = "BookedBayReassigned";
    /// <summary>The walk-in left before Staff had to move the booking: the booked bay is free again.</summary>
    public const string BookedBayFreed = "BookedBayFreed";
    /// <summary>No bay was left: the lot cancelled the booking.</summary>
    public const string BookingCancelledByLot = "BookingCancelledByLot";
    /// <summary>The deposit went back to the XXIV wallet.</summary>
    public const string DepositRefunded = "DepositRefunded";
    /// <summary>A compensation was paid into the XXIV wallet.</summary>
    public const string CompensationPaid = "CompensationPaid";
    /// <summary>The planned departure of a parked vehicle's scheduled booking is near.</summary>
    public const string DepartureSoon = "DepartureSoon";
    /// <summary>A scheduled booking's planned departure has passed and the vehicle is still parked (customer and Staff).</summary>
    public const string DepartureOverdue = "DepartureOverdue";
    /// <summary>The booked bay was still in use when the hold started, so the booking was moved to another bay.</summary>
    public const string BookingBayMoved = "BookingBayMoved";

    public static readonly string[] All =
    [
        VehicleApproved, VehicleRejected, CheckIn, CheckOut, PriceChanged,
        BookedBayTaken, BookedBayReassigned, BookedBayFreed, BookingCancelledByLot, DepositRefunded, CompensationPaid
    ];
}

/// <summary>One notification as the customer sees it.</summary>
public class NotificationModel
{
    public long Id { get; set; }

    public string Type { get; set; }

    public string Title { get; set; }

    public string Message { get; set; }

    public string RefType { get; set; }

    public string RefId { get; set; }

    /// <summary>When the event happened (check-in / check-out time, decision time), UTC.</summary>
    public DateTime? EventAt { get; set; }

    /// <summary>Extra values for the UI: plateNumber, lotName, entryTime, exitTime, durationMinutes, fee, note.</summary>
    public Dictionary<string, string> Data { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    public DateTime? ReadAt { get; set; }

    public bool IsRead => ReadAt != null;
}

/// <summary>The newest notifications and how many are still unread.</summary>
public class NotificationListModel
{
    public List<NotificationModel> Items { get; set; } = [];

    public int UnreadCount { get; set; }
}
