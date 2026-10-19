using System.Collections.Generic;
using System.Threading.Tasks;
using PMS.Application.Models.ParkingLot;

namespace PMS.Application.Services;

public interface IParkingLotService
{
    Task<IEnumerable<ParkingLotResponseModel>> GetAllAsync(string searchTerm = null, string status = null, bool includeDeleted = false);

    Task<IEnumerable<PublicParkingLotResponseModel>> GetPublicLotsAsync(string searchTerm = null);

    Task<ParkingLotResponseModel> GetByIdAsync(int id);

    Task<ParkingLotResponseModel> CreateAsync(CreateParkingLotModel model, string createdBy = null);

    Task<ParkingLotResponseModel> UpdateAsync(int id, UpdateParkingLotModel model);

    /// <summary>Builds zones, slots and the 3D layout from a model, for a lot that has no zones yet.</summary>
    Task<ParkingLotResponseModel> ApplyTemplateAsync(int id, string templateId, string userId);

    Task<bool> UpdateStatusAsync(int id, UpdateParkingLotStatusModel model);

    Task<bool> DeleteAsync(int id);
}
