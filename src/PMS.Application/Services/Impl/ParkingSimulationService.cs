using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Keeps the parking simulation in one SystemSettings row and in <see cref="ParkingOptions.Simulation"/>, which
/// every reservation, gate and payment service reads. Saving replaces the settings object as a whole.
/// </summary>
public class ParkingSimulationService(PmsDbContext pmsContext, ParkingOptions options, TimeProvider clock) : IParkingSimulationService
{
    public const string SettingKey = "Parking.Simulation";
    public const string SettingCategory = "Parking";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<ParkingSimulationResponseModel> GetAsync()
    {
        var row = await pmsContext.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.SettingKey == SettingKey);
        return ToResponse(options.Simulation ?? new ParkingSimulationSettings(), row);
    }

    public async Task<ParkingSimulationResponseModel> UpdateAsync(UpdateParkingSimulationModel model, string userId)
    {
        ArgumentNullException.ThrowIfNull(model);
        int[] seconds =
        [
            model.BlockSeconds, model.DepositHoldSeconds, model.HoldSeconds, model.DepositPaymentSeconds,
            model.EarlyArrivalSeconds, model.CancelRefundSeconds, model.ExitWindowSeconds
        ];
        if (seconds.Any(s => s is < ParkingSimulationSettings.MinSeconds or > ParkingSimulationSettings.MaxSeconds))
            throw new BadRequestException(
                $"Every simulated time must be between {ParkingSimulationSettings.MinSeconds} and {ParkingSimulationSettings.MaxSeconds} seconds.");

        var settings = new ParkingSimulationSettings
        {
            Enabled = model.Enabled,
            BlockSeconds = model.BlockSeconds,
            DepositHoldSeconds = model.DepositHoldSeconds,
            HoldSeconds = model.HoldSeconds,
            DepositPaymentSeconds = model.DepositPaymentSeconds,
            EarlyArrivalSeconds = model.EarlyArrivalSeconds,
            CancelRefundSeconds = model.CancelRefundSeconds,
            ExitWindowSeconds = model.ExitWindowSeconds
        };

        var row = await pmsContext.SystemSettings.FirstOrDefaultAsync(s => s.SettingKey == SettingKey);
        if (row == null)
        {
            row = new SystemSetting
            {
                SettingKey = SettingKey,
                ValueType = "json",
                Category = SettingCategory,
                Description = "Parking simulation: fees and booking times in seconds for demos."
            };
            pmsContext.SystemSettings.Add(row);
        }

        row.SettingValue = JsonSerializer.Serialize(settings);
        row.UpdatedAt = Now;
        row.UpdatedBy = userId;
        await pmsContext.SaveChangesAsync();

        // Applied only once saved, so a failed save never leaves the API simulating
        options.Simulation = settings;
        return ToResponse(settings, row);
    }

    public async Task LoadAsync()
    {
        var row = await pmsContext.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.SettingKey == SettingKey);
        options.Simulation = Parse(row?.SettingValue) ?? new ParkingSimulationSettings();
    }

    /// <summary>The saved settings, or null when there are none or they cannot be read.</summary>
    private static ParkingSimulationSettings Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ParkingSimulationSettings>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private ParkingSimulationResponseModel ToResponse(ParkingSimulationSettings settings, SystemSetting row) => new()
    {
        Enabled = settings.Enabled,
        BlockSeconds = settings.BlockSeconds,
        DepositHoldSeconds = settings.DepositHoldSeconds,
        HoldSeconds = settings.HoldSeconds,
        DepositPaymentSeconds = settings.DepositPaymentSeconds,
        EarlyArrivalSeconds = settings.EarlyArrivalSeconds,
        CancelRefundSeconds = settings.CancelRefundSeconds,
        ExitWindowSeconds = settings.ExitWindowSeconds,
        Normal = new ParkingSimulationDefaultsModel
        {
            DepositHoldMinutes = options.Reservations.DepositHoldMinutes,
            HoldMinutes = options.Reservations.HoldMinutes,
            DepositPaymentMinutes = options.Reservations.DepositMinutes,
            EarlyArrivalMinutes = options.Reservations.EarlyArrivalMinutes,
            CancelRefundMinutes = options.Reservations.CancelRefundMinutes,
            ExitWindowMinutes = options.Gate.ExitWindowMinutes
        },
        UpdatedAt = row?.UpdatedAt,
        UpdatedBy = row?.UpdatedBy
    };
}
