using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class DataDeletionRequest
{
    public int Id { get; set; }

    public string UserId { get; set; }

    public DateTime RequestedAt { get; set; }

    public string Status { get; set; }

    public string ProcessedBy { get; set; }

    public DateTime? ProcessedAt { get; set; }

    public string Note { get; set; }
}
