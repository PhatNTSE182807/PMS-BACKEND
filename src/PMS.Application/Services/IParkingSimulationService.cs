using PMS.Application.Models.Operations;

namespace PMS.Application.Services;

/// <summary>The Admin's parking simulation (demo mode in seconds), saved in the system settings.</summary>
public interface IParkingSimulationService
{
    /// <summary>The current simulation and the normal values it replaces.</summary>
    Task<ParkingSimulationResponseModel> GetAsync();

    /// <summary>Saves the simulation and applies it at once to every lot.</summary>
    Task<ParkingSimulationResponseModel> UpdateAsync(UpdateParkingSimulationModel model, string userId);

    /// <summary>Applies the saved simulation when the API starts (off when nothing was saved).</summary>
    Task LoadAsync();
}
