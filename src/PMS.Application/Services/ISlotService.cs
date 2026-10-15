using System.Collections.Generic;
using System.Threading.Tasks;
using PMS.Application.Models.Slot;

namespace PMS.Application.Services;

public interface ISlotService
{
    Task<IEnumerable<SlotResponseModel>> GetByZoneIdAsync(int zoneId);

    Task<IEnumerable<SlotResponseModel>> GetAllAsync(int? zoneId = null, int? vehicleTypeId = null, string status = null, bool? isActive = null);

    Task<SlotResponseModel> GetByIdAsync(int id);

    Task<SlotResponseModel> CreateAsync(CreateSlotModel model);

    Task<SlotResponseModel> UpdateAsync(int id, UpdateSlotModel model);

    Task<bool> UpdateStatusAsync(int id, string status);

    Task<bool> DeleteAsync(int id);
}
