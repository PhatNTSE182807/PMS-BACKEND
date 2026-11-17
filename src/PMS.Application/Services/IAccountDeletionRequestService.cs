using System.Collections.Generic;
using System.Threading.Tasks;
using PMS.Application.Models.User;

namespace PMS.Application.Services;

/// <summary>
/// Customers ask for their account to be deleted; every Admin is told and locks the account from the customer list
/// (locking completes the request). Nothing is hard-deleted.
/// </summary>
public interface IAccountDeletionRequestService
{
    /// <summary>The customer's pending request, or null.</summary>
    Task<AccountDeletionRequestModel> GetMineAsync(string userId);

    /// <summary>Files a request (one pending request per account) and tells the Admins.</summary>
    Task<AccountDeletionRequestModel> RequestAsync(string userId, CreateAccountDeletionRequestModel model);

    /// <summary>Withdraws the customer's pending request.</summary>
    Task CancelMineAsync(string userId);

    /// <summary>Customer accounts for the Admin, matched on name, user name, email or phone, with their pending request.</summary>
    Task<IReadOnlyList<CustomerAccountModel>> GetCustomersAsync(string searchTerm);

    /// <summary>Closes the account's pending request once an Admin locked it.</summary>
    Task CompletePendingAsync(string userId, string adminId);
}
