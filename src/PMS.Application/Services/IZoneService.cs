using System.Collections.Generic;
using System.Threading.Tasks;
using PMS.Application.Models.Zone;

namespace PMS.Application.Services;

public interface IZoneService
{
    Task<IEnumerable<ZoneResponseModel>> GetByLotIdAsync(int lotId);

    Task<IEnumerable<ZoneResponseModel>> GetAllAsync(int? lotId = null);

    Task<ZoneResponseModel> GetByIdAsync(int id);

    Task<ZoneResponseModel> CreateAsync(CreateZoneModel model);

    Task<ZoneResponseModel> UpdateAsync(int id, UpdateZoneModel model);

    Task<bool> DeleteAsync(int id);
}
