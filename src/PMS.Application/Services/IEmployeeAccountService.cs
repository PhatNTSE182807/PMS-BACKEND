using PMS.Application.Models.Account;

namespace PMS.Application.Services;

/// <summary>
/// Internal account management (SRS FR-AUTH-04/07, UC-09). A lot has one Manager (ParkingLots.ManagerUserId)
/// and many Staff (Employees.ParkingLotId). Staff are never hard-deleted (BR-21).
/// </summary>
public interface IEmployeeAccountService
{
    /* Admin -> Manager */

    Task<IEnumerable<EmployeeAccountResponseModel>> GetManagersAsync(bool? isActive = null, string searchTerm = null);

    Task<EmployeeAccountResponseModel> GetManagerAsync(string managerId);

    Task<EmployeeAccountResponseModel> UpdateManagerAsync(string managerId, UpdateEmployeeAccountModel model);

    Task<EmployeeAccountResponseModel> SetManagerStatusAsync(string managerId, bool isActive);

    /// <summary>
    /// Replaces the Manager's lots. A lot has one Manager: a lot run by another active Manager is refused;
    /// a lot whose Manager is deactivated is handed over.
    /// </summary>
    Task<EmployeeAccountResponseModel> AssignManagerLotsAsync(string managerId, AssignParkingLotsModel model);

    /* Manager -> Staff (always scoped to the lots the calling Manager manages) */

    Task<IEnumerable<AssignedParkingLotModel>> GetManagedLotsAsync(string managerId);

    /// <summary>The lot the signed-in Staff works at (Employees.ParkingLotId, set by their Manager); empty when unassigned.</summary>
    Task<IEnumerable<AssignedParkingLotModel>> GetStaffLotsAsync(string staffId);

    Task<IEnumerable<EmployeeAccountResponseModel>> GetStaffAsync(string managerId, int? parkingLotId = null, bool? isActive = null, string searchTerm = null);

    Task<EmployeeAccountResponseModel> GetStaffByIdAsync(string managerId, string staffId);

    Task<EmployeeAccountResponseModel> CreateStaffAsync(string managerId, CreateStaffAccountModel model);

    Task<EmployeeAccountResponseModel> UpdateStaffAsync(string managerId, string staffId, UpdateEmployeeAccountModel model);

    Task<EmployeeAccountResponseModel> AssignStaffLotAsync(string managerId, string staffId, AssignStaffParkingLotModel model);

    Task<EmployeeAccountResponseModel> SetStaffStatusAsync(string managerId, string staffId, bool isActive);

    /// <summary>Soft delete: the account is locked and the employee marked Terminated; nothing is removed.</summary>
    Task<bool> DeleteStaffAsync(string managerId, string staffId);
}
