using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class BackupJob
{
    public int Id { get; set; }

    public string JobType { get; set; }

    public string Status { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public string FilePath { get; set; }

    public long? SizeBytes { get; set; }

    public string TriggeredBy { get; set; }

    public string ErrorMessage { get; set; }
}
