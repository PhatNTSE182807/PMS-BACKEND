using System.Threading.Tasks;
using PMS.Application.Models.Operations;

namespace PMS.Application.Services;

/// <summary>The signed-in customer's finished parking sessions and their e-receipts (FR-CUS-02).</summary>
public interface IParkingHistoryService
{
    /// <summary>Sessions that ended (the vehicle left), newest exit first, one page at a time.</summary>
    Task<ParkingHistoryPageModel> GetHistoryAsync(string userId, ParkingHistoryQuery query);

    /// <summary>The receipt of one of the customer's finished sessions; another customer's session answers 404.</summary>
    Task<ParkingReceiptModel> GetReceiptAsync(string userId, long sessionId);
}
