using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.Profile;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

public class UserProfileService : IUserProfileService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly PmsDbContext _pmsContext;

    public UserProfileService(UserManager<ApplicationUser> userManager, PmsDbContext pmsContext)
    {
        _userManager = userManager;
        _pmsContext = pmsContext;
    }

    public async Task<UserProfileResponseModel> GetProfileAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            throw new NotFoundException("User not found.");

        var customer = await GetOrCreateCustomerAsync(user);

        var vehicles = await _pmsContext.Vehicles
            .AsNoTracking()
            .Include(v => v.VehicleType)
            .Where(v => v.CustomerId == customer.Id && v.IsActive)
            .OrderByDescending(v => v.IsDefault)
            .ThenByDescending(v => v.CreatedAt)
            .Select(v => MapToVehicleResponse(v))
            .ToListAsync();

        var status = "Active";
        if (user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTimeOffset.UtcNow)
        {
            status = "Locked";
        }
        else if (!customer.IsActive)
        {
            status = "Inactive";
        }

        return new UserProfileResponseModel
        {
            UserId = user.Id,
            FullName = customer.FullName,
            PhoneNumber = !string.IsNullOrWhiteSpace(customer.Phone) ? customer.Phone : user.PhoneNumber,
            Email = !string.IsNullOrWhiteSpace(customer.Email) ? customer.Email : user.Email,
            AvatarUrl = customer.AvatarUrl,
            IsActive = customer.IsActive,
            Status = status,
            HasPassword = !string.IsNullOrEmpty(user.PasswordHash),
            Role = user.Role,
            CreatedAt = customer.CreatedAt,
            UpdatedAt = customer.UpdatedAt,
            Vehicles = vehicles
        };
    }

    public async Task<UserProfileResponseModel> UpdateProfileAsync(string userId, UpdateUserProfileModel model)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            throw new NotFoundException("User not found.");

        var customer = await GetOrCreateCustomerAsync(user);

        if (!string.IsNullOrWhiteSpace(model.FullName))
        {
            customer.FullName = model.FullName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(model.Email))
        {
            var trimmedEmail = model.Email.Trim();
            customer.Email = trimmedEmail;
            user.Email = trimmedEmail;
            user.NormalizedEmail = trimmedEmail.ToUpperInvariant();
        }

        if (!string.IsNullOrWhiteSpace(model.PhoneNumber))
        {
            var trimmedPhone = model.PhoneNumber.Trim();
            customer.Phone = trimmedPhone;
            user.PhoneNumber = trimmedPhone;
        }

        if (model.AvatarUrl != null)
        {
            customer.AvatarUrl = string.IsNullOrWhiteSpace(model.AvatarUrl) ? null : model.AvatarUrl.Trim();
        }

        customer.UpdatedAt = DateTime.UtcNow;

        await _pmsContext.SaveChangesAsync();
        await _userManager.UpdateAsync(user);

        return await GetProfileAsync(userId);
    }

    public async Task<List<VehicleResponseModel>> GetVehiclesAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            throw new NotFoundException("User not found.");

        var customer = await _pmsContext.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.UserId == userId);

        if (customer == null)
            return new List<VehicleResponseModel>();

        return await _pmsContext.Vehicles
            .AsNoTracking()
            .Include(v => v.VehicleType)
            .Where(v => v.CustomerId == customer.Id && v.IsActive)
            .OrderByDescending(v => v.IsDefault)
            .ThenByDescending(v => v.CreatedAt)
            .Select(v => MapToVehicleResponse(v))
            .ToListAsync();
    }

    public async Task<VehicleResponseModel> AddVehicleAsync(string userId, CreateVehicleModel model)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            throw new NotFoundException("User not found.");

        var customer = await GetOrCreateCustomerAsync(user);

        var vehicleType = await _pmsContext.VehicleTypes
            .FirstOrDefaultAsync(vt => vt.Id == model.VehicleTypeId && vt.IsActive);

        if (vehicleType == null)
            throw new BadRequestException("Invalid or inactive vehicle type.");

        var plateNormalized = NormalizePlate(model.LicensePlate);

        var existingVehicle = await _pmsContext.Vehicles
            .FirstOrDefaultAsync(v => v.CustomerId == customer.Id && v.PlateNormalized == plateNormalized && v.IsActive);

        if (existingVehicle != null)
            throw new BadRequestException("Vehicle with this license plate is already registered to your account.");

        var hasExistingVehicles = await _pmsContext.Vehicles
            .AnyAsync(v => v.CustomerId == customer.Id && v.IsActive);

        var isDefault = model.IsDefault || !hasExistingVehicles;

        if (isDefault)
        {
            await UnsetOtherDefaultVehiclesAsync(customer.Id);
        }

        var vehicle = new Vehicle
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            VehicleTypeId = model.VehicleTypeId,
            PlateNumber = model.LicensePlate.Trim().ToUpperInvariant(),
            PlateNormalized = plateNormalized,
            Brand = model.Brand?.Trim(),
            Model = model.Model?.Trim(),
            Color = model.Color?.Trim(),
            IsDefault = isDefault,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _pmsContext.Vehicles.Add(vehicle);
        await _pmsContext.SaveChangesAsync();

        vehicle.VehicleType = vehicleType;
        return MapToVehicleResponse(vehicle);
    }

    public async Task<VehicleResponseModel> UpdateVehicleAsync(string userId, Guid vehicleId, UpdateVehicleModel model)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            throw new NotFoundException("User not found.");

        var customer = await _pmsContext.Customers.FirstOrDefaultAsync(c => c.UserId == userId);
        if (customer == null)
            throw new NotFoundException("Customer profile not found.");

        var vehicle = await _pmsContext.Vehicles
            .Include(v => v.VehicleType)
            .FirstOrDefaultAsync(v => v.Id == vehicleId && v.CustomerId == customer.Id && v.IsActive);

        if (vehicle == null)
            throw new NotFoundException("Vehicle not found.");

        var vehicleType = await _pmsContext.VehicleTypes
            .FirstOrDefaultAsync(vt => vt.Id == model.VehicleTypeId && vt.IsActive);

        if (vehicleType == null)
            throw new BadRequestException("Invalid or inactive vehicle type.");

        var plateNormalized = NormalizePlate(model.LicensePlate);

        var duplicatePlate = await _pmsContext.Vehicles
            .FirstOrDefaultAsync(v => v.CustomerId == customer.Id && v.Id != vehicleId && v.PlateNormalized == plateNormalized && v.IsActive);

        if (duplicatePlate != null)
            throw new BadRequestException("Another vehicle with this license plate is already registered.");

        if (model.IsDefault && !vehicle.IsDefault)
        {
            await UnsetOtherDefaultVehiclesAsync(customer.Id, vehicleId);
            vehicle.IsDefault = true;
        }
        else if (!model.IsDefault && vehicle.IsDefault)
        {
            vehicle.IsDefault = false;
        }

        vehicle.PlateNumber = model.LicensePlate.Trim().ToUpperInvariant();
        vehicle.PlateNormalized = plateNormalized;
        vehicle.VehicleTypeId = model.VehicleTypeId;
        vehicle.Brand = model.Brand?.Trim();
        vehicle.Model = model.Model?.Trim();
        vehicle.Color = model.Color?.Trim();
        vehicle.IsActive = model.IsActive;
        vehicle.UpdatedAt = DateTime.UtcNow;

        await _pmsContext.SaveChangesAsync();

        vehicle.VehicleType = vehicleType;
        return MapToVehicleResponse(vehicle);
    }

    public async Task<bool> DeleteVehicleAsync(string userId, Guid vehicleId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            throw new NotFoundException("User not found.");

        var customer = await _pmsContext.Customers.FirstOrDefaultAsync(c => c.UserId == userId);
        if (customer == null)
            throw new NotFoundException("Customer profile not found.");

        var vehicle = await _pmsContext.Vehicles
            .FirstOrDefaultAsync(v => v.Id == vehicleId && v.CustomerId == customer.Id && v.IsActive);

        if (vehicle == null)
            throw new NotFoundException("Vehicle not found.");

        var wasDefault = vehicle.IsDefault;
        vehicle.IsActive = false;
        vehicle.IsDefault = false;
        vehicle.UpdatedAt = DateTime.UtcNow;

        if (wasDefault)
        {
            var nextDefault = await _pmsContext.Vehicles
                .Where(v => v.CustomerId == customer.Id && v.Id != vehicleId && v.IsActive)
                .OrderByDescending(v => v.CreatedAt)
                .FirstOrDefaultAsync();

            if (nextDefault != null)
            {
                nextDefault.IsDefault = true;
                nextDefault.UpdatedAt = DateTime.UtcNow;
            }
        }

        await _pmsContext.SaveChangesAsync();
        return true;
    }

    public async Task<VehicleResponseModel> SetDefaultVehicleAsync(string userId, Guid vehicleId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            throw new NotFoundException("User not found.");

        var customer = await _pmsContext.Customers.FirstOrDefaultAsync(c => c.UserId == userId);
        if (customer == null)
            throw new NotFoundException("Customer profile not found.");

        var vehicle = await _pmsContext.Vehicles
            .Include(v => v.VehicleType)
            .FirstOrDefaultAsync(v => v.Id == vehicleId && v.CustomerId == customer.Id && v.IsActive);

        if (vehicle == null)
            throw new NotFoundException("Vehicle not found.");

        await UnsetOtherDefaultVehiclesAsync(customer.Id, vehicleId);

        vehicle.IsDefault = true;
        vehicle.UpdatedAt = DateTime.UtcNow;

        await _pmsContext.SaveChangesAsync();
        return MapToVehicleResponse(vehicle);
    }

    public async Task<List<VehicleTypeResponseModel>> GetVehicleTypesAsync()
    {
        return await _pmsContext.VehicleTypes
            .AsNoTracking()
            .Where(vt => vt.IsActive)
            .OrderBy(vt => vt.SortOrder)
            .Select(vt => new VehicleTypeResponseModel
            {
                Id = vt.Id,
                Code = vt.Code,
                Name = vt.Name,
                Description = vt.Description,
                SortOrder = vt.SortOrder
            })
            .ToListAsync();
    }

    public async Task<bool> ChangePasswordAsync(string userId, ChangePasswordModel model)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            throw new NotFoundException("User not found.");

        var result = await _userManager.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);

        if (!result.Succeeded)
            throw new BadRequestException(result.Errors.FirstOrDefault()?.Description ?? "Failed to change password.");

        return true;
    }

    private async Task<Customer> GetOrCreateCustomerAsync(ApplicationUser user)
    {
        var customer = await _pmsContext.Customers.FirstOrDefaultAsync(c => c.UserId == user.Id);
        if (customer != null)
            return customer;

        customer = new Customer
        {
            UserId = user.Id,
            CustomerCode = $"CUS-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            FullName = user.UserName ?? "User",
            Phone = user.PhoneNumber,
            Email = user.Email,
            CustomerType = "Individual",
            MembershipTier = "Standard",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _pmsContext.Customers.Add(customer);
        await _pmsContext.SaveChangesAsync();
        return customer;
    }

    private async Task UnsetOtherDefaultVehiclesAsync(int customerId, Guid? excludeVehicleId = null)
    {
        var query = _pmsContext.Vehicles
            .Where(v => v.CustomerId == customerId && v.IsDefault && v.IsActive);

        if (excludeVehicleId.HasValue)
        {
            query = query.Where(v => v.Id != excludeVehicleId.Value);
        }

        var defaultVehicles = await query.ToListAsync();
        foreach (var v in defaultVehicles)
        {
            v.IsDefault = false;
            v.UpdatedAt = DateTime.UtcNow;
        }
    }

    private static string NormalizePlate(string plate)
    {
        if (string.IsNullOrWhiteSpace(plate))
            return string.Empty;

        return Regex.Replace(plate, @"[^a-zA-Z0-9]", string.Empty).ToUpperInvariant();
    }

    private static VehicleResponseModel MapToVehicleResponse(Vehicle v)
    {
        return new VehicleResponseModel
        {
            VehicleId = v.Id,
            LicensePlate = v.PlateNumber,
            VehicleTypeId = v.VehicleTypeId,
            VehicleType = v.VehicleType?.Name ?? "Unknown",
            Brand = v.Brand,
            Model = v.Model,
            Color = v.Color,
            IsDefault = v.IsDefault,
            IsActive = v.IsActive,
            CreatedAt = v.CreatedAt,
            UpdatedAt = v.UpdatedAt
        };
    }
}
