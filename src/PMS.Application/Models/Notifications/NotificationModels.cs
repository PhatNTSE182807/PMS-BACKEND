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

    public static readonly string[] All = [VehicleApproved, VehicleRejected, CheckIn, CheckOut, PriceChanged];
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
