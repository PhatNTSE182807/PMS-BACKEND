using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using PMS.Application.Models.Profile;

namespace PMS.Application.Services;

public interface IUserProfileService
{
    Task<UserProfileResponseModel> GetProfileAsync(string userId);

    Task<UserProfileResponseModel> UpdateProfileAsync(string userId, UpdateUserProfileModel model);

    Task<ProfileFieldAvailabilityModel> CheckProfileUniquenessAsync(string userId, string? phone, string? citizenId);

    /// <summary>Uploads and saves a new avatar image for the user. Returns the updated profile.</summary>
    Task<UserProfileResponseModel> UploadAvatarAsync(string userId, IFormFile file);

    Task<List<VehicleResponseModel>> GetVehiclesAsync(string userId);

    Task<PlateAvailabilityResponseModel> CheckPlateAvailabilityAsync(string userId, string plate, Guid? excludeVehicleId = null);

    Task<VehicleResponseModel> AddVehicleAsync(string userId, CreateVehicleModel model);

    Task<VehicleResponseModel> UpdateVehicleAsync(string userId, Guid vehicleId, UpdateVehicleModel model);

    Task<bool> DeleteVehicleAsync(string userId, Guid vehicleId);

    Task<VehicleResponseModel> SetDefaultVehicleAsync(string userId, Guid vehicleId);

    Task<List<VehicleTypeResponseModel>> GetVehicleTypesAsync();

    Task<bool> ChangePasswordAsync(string userId, ChangePasswordModel model);
}
