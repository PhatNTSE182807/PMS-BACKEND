using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class CustomerFeedback
{
    public int Id { get; set; }

    public int? CustomerId { get; set; }

    public int? ParkingLotId { get; set; }

    public long? SessionId { get; set; }

    public string FeedbackType { get; set; }

    public string Subject { get; set; }

    public string Content { get; set; }

    public string Status { get; set; }

    public string AssignedTo { get; set; }

    public string Response { get; set; }

    public DateTime? RespondedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
