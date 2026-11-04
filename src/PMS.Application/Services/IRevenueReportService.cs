using System.Threading.Tasks;
using PMS.Application.Models.Reports;

namespace PMS.Application.Services;

/// <summary>
/// Revenue reporting service providing role-separated aggregation for Manager and Admin.
/// Revenue is derived from ParkingSession.FinalFee of settled (Paid/Completed) sessions.
/// </summary>
public interface IRevenueReportService
{
    Task<ManagerRevenueReportResponse> GetManagerRevenueAsync(
        string managerUserId, string startDate, string endDate, int? parkingLotId = null);

    /// <summary>
    /// Everything the Manager's lots (or one of them) have earned, from their first parking session to today, with the
    /// same rules as the dated report (Completed or Paid sessions, split by payment method).
    /// </summary>
    Task<ManagerRevenueReportResponse> GetManagerAllTimeRevenueAsync(string managerUserId, int? parkingLotId = null);

    /// <param name="vehicleType">"ALL" (or null), "CAR" or "MOTORBIKE".</param>
    Task<AdminRevenueReportResponse> GetAdminRevenueAsync(
        string startDate, string endDate, int? parkingLotId = null, string vehicleType = null);

    /// <summary>
    /// The same settled revenue per day, week, month or year of the range, split into cars and motorbikes and by lot.
    /// </summary>
    /// <param name="granularity">"DAY", "WEEK", "MONTH", "YEAR", or null to pick the finest that keeps about a month of points.</param>
    Task<AdminRevenueTimeSeriesResponse> GetAdminRevenueTimeSeriesAsync(
        string startDate, string endDate, int? parkingLotId = null, string vehicleType = null, string granularity = null);
}
