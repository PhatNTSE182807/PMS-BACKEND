namespace PMS.Application.Models.Dashboard;

/// <summary>Performance of one parking lot (hub) on the Admin dashboard.</summary>
public class HubPerformanceDto
{
    public int LotId { get; set; }

    public string LotName { get; set; }

    public string Location { get; set; }

    public int TotalBays { get; set; }

    public int OccupiedBays { get; set; }

    /// <summary>Share of occupied bays in this lot, as a percentage (0-100).</summary>
    public double OccupancyRate { get; set; }

    /// <summary>Revenue in VND collected today.</summary>
    public decimal TodayRevenue { get; set; }

    /// <summary>Revenue in VND collected since the first day of the current month (month-to-date).</summary>
    public decimal MTDRevenue { get; set; }
}
