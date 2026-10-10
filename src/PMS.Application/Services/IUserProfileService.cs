using System.Collections.Generic;
using System.Threading.Tasks;
using PMS.Application.Models.Profile;

namespace PMS.Application.Services;

public interface IUserProfileService
{
    Task<UserProfileResponseModel> GetProfileAsync(string userId);

    Task<UserProfileResponseModel> UpdateProfileAsync(string userId, UpdateUserProfileModel model);

    Task<List<VehicleResponseModel>> GetVehiclesAsync(string userId);

    Task<VehicleResponseModel> AddVehicleAsync(string userId, CreateVehicleModel model);

    Task<VehicleResponseModel> UpdateVehicleAsync(string userId, Guid vehicleId, UpdateVehicleModel model);

    Task<bool> DeleteVehicleAsync(string userId, Guid vehicleId);

    Task<VehicleResponseModel> SetDefaultVehicleAsync(string userId, Guid vehicleId);

    Task<List<VehicleTypeResponseModel>> GetVehicleTypesAsync();

    Task<bool> ChangePasswordAsync(string userId, ChangePasswordModel model);
}
