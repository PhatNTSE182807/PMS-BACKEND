namespace PMS.Application.Models.Dashboard;

/// <summary>Network-wide headline numbers shown on the top cards of the Admin dashboard.</summary>
public class DashboardOverviewDto
{
    /// <summary>Gross revenue in VND across all lots.</summary>
    public decimal TotalGrossRevenue { get; set; }

    /// <summary>Share of occupied bays across the whole network, as a percentage (0-100).</summary>
    public double NetworkLoadFactor { get; set; }

    public int TotalBays { get; set; }

    public int OccupiedBays { get; set; }

    /// <summary>Number of active customer accounts.</summary>
    public int ActiveUsers { get; set; }

    /// <summary>Number of active Manager and Staff accounts.</summary>
    public int ActiveStaff { get; set; }
}
