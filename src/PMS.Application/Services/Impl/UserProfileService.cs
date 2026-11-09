using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.Profile;
using PMS.Application.Models.VehicleDocuments;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

public class UserProfileService : IUserProfileService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly PmsDbContext _pmsContext;
    private readonly IWebHostEnvironment? _env;

    /// <summary>Allowed MIME types for avatar images.</summary>
    private static readonly HashSet<string> AllowedImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp"
    };

    /// <summary>Maximum avatar file size: 10 MB.</summary>
    private const long MaxAvatarBytes = 10 * 1024 * 1024;

    public UserProfileService(UserManager<ApplicationUser> userManager, PmsDbContext pmsContext, IWebHostEnvironment? env = null)
    {
        _userManager = userManager;
        _pmsContext = pmsContext;
        _env = env;
    }

    public async Task<UserProfileResponseModel> GetProfileAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            throw new NotFoundException("User not found.");

        var employee = await _pmsContext.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.UserId == user.Id);
        var customer = await GetOrCreateCustomerAsync(user);

        var vehicles = await _pmsContext.Vehicles
            .AsNoTracking()
            .Include(v => v.VehicleType)
            .Where(v => v.CustomerId == customer.Id && v.IsActive)
            .OrderByDescending(v => v.IsDefault)
            .ThenByDescending(v => v.CreatedAt)
            .Select(v => MapToVehicleResponse(v))
            .ToListAsync();
        await WithPapersAsync(vehicles);

        var isUserActive = user.IsActive && (employee != null ? employee.Status == "Active" : customer.IsActive);
        var status = "Active";
        if (user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTimeOffset.UtcNow)
        {
            status = "Locked";
        }
        else if (!isUserActive)
        {
            status = "Inactive";
        }

        var fullName = employee?.FullName;
        if (string.IsNullOrWhiteSpace(fullName))
            fullName = customer.FullName;
        if (string.IsNullOrWhiteSpace(fullName))
            fullName = user.UserName;

        return new UserProfileResponseModel
        {
            UserId = user.Id,
            FullName = fullName,
            PhoneNumber = !string.IsNullOrWhiteSpace(employee?.Phone) ? employee.Phone : (!string.IsNullOrWhiteSpace(customer.Phone) ? customer.Phone : user.PhoneNumber),
            Email = !string.IsNullOrWhiteSpace(employee?.Email) ? employee.Email : (!string.IsNullOrWhiteSpace(customer.Email) ? customer.Email : user.Email),
            AvatarUrl = customer.AvatarUrl,
            CitizenId = customer.CitizenId,
            IsActive = isUserActive,
            Status = status,
            HasPassword = !string.IsNullOrEmpty(user.PasswordHash),
            Role = user.Role,
            EmployeeCode = employee?.EmployeeCode,
            CreatedAt = employee?.CreatedAt ?? customer.CreatedAt,
            UpdatedAt = employee?.UpdatedAt ?? customer.UpdatedAt,
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

        // Primary email cannot be changed through profile update


        if (!string.IsNullOrWhiteSpace(model.PhoneNumber))
        {
            var trimmedPhone = model.PhoneNumber.Trim();
            if (await IsPhoneTakenAsync(trimmedPhone, userId))
                throw new BadRequestException("Phone number is already registered to another account.");

            customer.Phone = trimmedPhone;
            user.PhoneNumber = trimmedPhone;
        }

        if (model.AvatarUrl != null)
        {
            customer.AvatarUrl = string.IsNullOrWhiteSpace(model.AvatarUrl) ? null : model.AvatarUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(model.CitizenId))
        {
            var citizenId = model.CitizenId.Trim();
            if (await IsCitizenIdTakenAsync(citizenId, userId))
                throw new BadRequestException("Citizen ID is already registered to another account.");
            customer.CitizenId = citizenId;
        }

        customer.UpdatedAt = DateTime.UtcNow;

        await _pmsContext.SaveChangesAsync();
        await _userManager.UpdateAsync(user);

        return await GetProfileAsync(userId);
    }

    public async Task<ProfileFieldAvailabilityModel> CheckProfileUniquenessAsync(string userId, string? phone, string? citizenId)
    {
        var result = new ProfileFieldAvailabilityModel();

        if (!string.IsNullOrWhiteSpace(phone))
        {
            var trimmedPhone = phone.Trim();
            if (await IsPhoneTakenAsync(trimmedPhone, userId))
            {
                result.IsPhoneAvailable = false;
                result.PhoneReason = "Phone number is already registered to another account.";
            }
        }

        if (!string.IsNullOrWhiteSpace(citizenId))
        {
            var trimmedCitizenId = citizenId.Trim();
            if (await IsCitizenIdTakenAsync(trimmedCitizenId, userId))
            {
                result.IsCitizenIdAvailable = false;
                result.CitizenIdReason = "Citizen ID is already registered to another account.";
            }
        }

        return result;
    }

    private async Task<bool> IsPhoneTakenAsync(string phone, string userId)
    {
        var takenInCustomers = await _pmsContext.Customers
            .AnyAsync(c => c.Phone == phone && c.UserId != userId);
        if (takenInCustomers) return true;

        try
        {
            return await _userManager.Users.AnyAsync(u => u.PhoneNumber == phone && u.Id != userId);
        }
        catch (InvalidOperationException)
        {
            return _userManager.Users.Any(u => u.PhoneNumber == phone && u.Id != userId);
        }
    }

    private async Task<bool> IsCitizenIdTakenAsync(string citizenId, string userId)
    {
        return await _pmsContext.Customers
            .AnyAsync(c => c.CitizenId == citizenId && c.UserId != userId);
    }

    public async Task<UserProfileResponseModel> UploadAvatarAsync(string userId, IFormFile file)
    {
        if (file == null || file.Length == 0)
            throw new BadRequestException("No file was uploaded.");

        if (file.Length > MaxAvatarBytes)
            throw new BadRequestException("Avatar image must be smaller than 10 MB.");

        if (!AllowedImageTypes.Contains(file.ContentType))
            throw new BadRequestException("Only JPEG, PNG, GIF and WebP images are allowed.");

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            throw new NotFoundException("User not found.");

        var customer = await GetOrCreateCustomerAsync(user);

        // Build a unique file name: userId + timestamp + original extension
        var extension = Path.GetExtension(file.FileName)?.ToLowerInvariant() ?? ".jpg";
        var fileName = $"{userId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{extension}";

        var uploadsDir = Path.Combine(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"), "uploads", "avatars");
        Directory.CreateDirectory(uploadsDir);

        var filePath = Path.Combine(uploadsDir, fileName);
        await using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        // Delete the old avatar file when it was served from local storage
        if (!string.IsNullOrWhiteSpace(customer.AvatarUrl) && customer.AvatarUrl.StartsWith("/uploads/"))
        {
            var oldPath = Path.Combine(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"),
                customer.AvatarUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(oldPath))
            {
                try { File.Delete(oldPath); } catch { /* best-effort cleanup */ }
            }
        }

        customer.AvatarUrl = $"/uploads/avatars/{fileName}";
        customer.UpdatedAt = DateTime.UtcNow;
        await _pmsContext.SaveChangesAsync();

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

        var vehicles = await _pmsContext.Vehicles
            .AsNoTracking()
            .Include(v => v.VehicleType)
            .Where(v => v.CustomerId == customer.Id && v.IsActive)
            .OrderByDescending(v => v.IsDefault)
            .ThenByDescending(v => v.CreatedAt)
            .Select(v => MapToVehicleResponse(v))
            .ToListAsync();
        return await WithPapersAsync(vehicles);
    }

    public async Task<PlateAvailabilityResponseModel> CheckPlateAvailabilityAsync(string userId, string plate, Guid? excludeVehicleId = null)
    {
        if (string.IsNullOrWhiteSpace(plate))
        {
            return new PlateAvailabilityResponseModel
            {
                LicensePlate = string.Empty,
                IsAvailable = false,
                Reason = "License plate is required."
            };
        }

        var plateNormalized = NormalizePlate(plate);
        var user = await _userManager.FindByIdAsync(userId);
        var customer = user != null ? await _pmsContext.Customers.FirstOrDefaultAsync(c => c.UserId == userId) : null;

        var query = _pmsContext.Vehicles
            .Where(v => v.PlateNormalized == plateNormalized && v.IsActive);

        if (excludeVehicleId.HasValue)
        {
            query = query.Where(v => v.Id != excludeVehicleId.Value);
        }

        var existingVehicle = await query.FirstOrDefaultAsync();

        if (existingVehicle != null)
        {
            var isOwn = customer != null && existingVehicle.CustomerId == customer.Id;
            return new PlateAvailabilityResponseModel
            {
                LicensePlate = plate.Trim().ToUpperInvariant(),
                IsAvailable = false,
                Reason = isOwn
                    ? "Vehicle with this license plate is already registered to your account."
                    : "This license plate is already registered by another account."
            };
        }

        return new PlateAvailabilityResponseModel
        {
            LicensePlate = plate.Trim().ToUpperInvariant(),
            IsAvailable = true
        };
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
            .FirstOrDefaultAsync(v => v.PlateNormalized == plateNormalized && v.IsActive);

        if (existingVehicle != null)
        {
            if (existingVehicle.CustomerId == customer.Id)
                throw new BadRequestException("Vehicle with this license plate is already registered to your account.");

            throw new BadRequestException("This license plate is already registered by another account.");
        }

        var hasExistingVehicles = await _pmsContext.Vehicles
            .AnyAsync(v => v.CustomerId == customer.Id && v.IsActive);

        // Citizen ID must be saved in user profile before registering a vehicle
        if (string.IsNullOrWhiteSpace(customer.CitizenId))
            throw new BadRequestException("Your Citizen ID must be saved in your profile before registering a vehicle.");

        // Automatically use the Citizen ID from the user's profile
        var citizenId = customer.CitizenId.Trim();

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
            CitizenId = citizenId,
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
            .FirstOrDefaultAsync(v => v.Id != vehicleId && v.PlateNormalized == plateNormalized && v.IsActive);

        if (duplicatePlate != null)
        {
            if (duplicatePlate.CustomerId == customer.Id)
                throw new BadRequestException("Another vehicle with this license plate is already registered to your account.");

            throw new BadRequestException("This license plate is already registered by another account.");
        }

        // Submitted papers were checked against this plate and owner; changing them needs a new registration
        var newCitizenId = string.IsNullOrWhiteSpace(model.CitizenId) ? vehicle.CitizenId : model.CitizenId.Trim();
        if (plateNormalized != vehicle.PlateNormalized || newCitizenId != vehicle.CitizenId)
        {
            var papers = await LatestPapersAsync([vehicle.Id]);
            var status = papers.TryGetValue(vehicle.Id, out var check) ? check.Status : null;
            if (status != null && status != VehicleVerificationStatus.Draft && status != VehicleVerificationStatus.Rejected)
                throw new BadRequestException("The papers of this vehicle were already submitted. Remove it and register it again to change the plate or owner.");
        }

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
        if (!string.IsNullOrWhiteSpace(model.CitizenId))
            vehicle.CitizenId = model.CitizenId.Trim();
        vehicle.IsActive = model.IsActive;
        vehicle.UpdatedAt = DateTime.UtcNow;

        await _pmsContext.SaveChangesAsync();

        vehicle.VehicleType = vehicleType;
        return (await WithPapersAsync([MapToVehicleResponse(vehicle)]))[0];
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
        return (await WithPapersAsync([MapToVehicleResponse(vehicle)]))[0];
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

        IdentityResult result;
        if (await _userManager.HasPasswordAsync(user))
        {
            if (string.IsNullOrWhiteSpace(model.CurrentPassword))
                throw new BadRequestException("Current password is required.");
            result = await _userManager.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);
        }
        else
        {
            // Google-only account (FR-AUTH-08): the first password needs no current one
            result = await _userManager.AddPasswordAsync(user, model.NewPassword);
        }

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

    /// <summary>The latest paper check of each vehicle (status and Staff note), keyed by vehicle id.</summary>
    private async Task<Dictionary<Guid, (string Status, string Note)>> LatestPapersAsync(List<Guid> vehicleIds)
    {
        var checks = await _pmsContext.VehicleVerifications
            .AsNoTracking()
            .Where(v => vehicleIds.Contains(v.VehicleId))
            .Select(v => new { v.VehicleId, v.Status, v.ReviewNote, v.CreatedAt })
            .ToListAsync();

        return checks
            .GroupBy(c => c.VehicleId)
            .ToDictionary(g => g.Key, g =>
            {
                var latest = g.OrderByDescending(c => c.CreatedAt).First();
                return (latest.Status, latest.ReviewNote);
            });
    }

    /// <summary>Fills each vehicle's registration status from its latest paper check.</summary>
    private async Task<List<VehicleResponseModel>> WithPapersAsync(List<VehicleResponseModel> vehicles)
    {
        if (vehicles.Count == 0)
            return vehicles;

        var papers = await LatestPapersAsync(vehicles.Select(v => v.VehicleId).ToList());
        foreach (var vehicle in vehicles)
        {
            if (!papers.TryGetValue(vehicle.VehicleId, out var check))
                continue;
            // Checks left over from the OCR era read like the current statuses
            vehicle.VerificationStatus = VehicleVerificationStatus.Waiting.Contains(check.Status) ? VehicleVerificationStatus.NeedsReview
                : VehicleVerificationStatus.Verified.Contains(check.Status) ? VehicleVerificationStatus.Approved
                : check.Status;
            vehicle.VerificationNote = check.Note;
        }
        return vehicles;
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
            CitizenId = v.CitizenId,
            IsDefault = v.IsDefault,
            IsActive = v.IsActive,
            CreatedAt = v.CreatedAt,
            UpdatedAt = v.UpdatedAt
        };
    }
}
