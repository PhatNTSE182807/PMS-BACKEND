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

    Task<bool> UpdateStatusAsync(int id, UpdateParkingLotStatusModel model);

    Task<bool> DeleteAsync(int id);
}
