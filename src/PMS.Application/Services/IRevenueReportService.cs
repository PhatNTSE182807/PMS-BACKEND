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

    Task<AdminRevenueReportResponse> GetAdminRevenueAsync(
        string startDate, string endDate, int? parkingLotId = null);
}
