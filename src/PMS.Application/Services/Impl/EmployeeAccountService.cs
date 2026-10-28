using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.Account;
using PMS.Application.Models.Admin;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

public class EmployeeAccountService(
    UserManager<ApplicationUser> userManager,
    IUserService userService,
    PmsDbContext pmsContext)
    : IEmployeeAccountService
{
    private const string ManagerRole = "Manager";

    private const string StaffRole = "Staff";

    /* Admin -> Manager ---------------------------------------------- */

    public async Task<IEnumerable<EmployeeAccountResponseModel>> GetManagersAsync(bool? isActive = null, string searchTerm = null)
    {
        var query = userManager.Users.Where(u => u.Role == ManagerRole);
        if (isActive.HasValue) query = query.Where(u => u.IsActive == isActive.Value);

        var users = await query.ToListAsync();
        var userIds = users.Select(u => u.Id).ToList();
        var employees = await pmsContext.Employees.Where(e => userIds.Contains(e.UserId)).ToDictionaryAsync(e => e.UserId);
        var lots = await pmsContext.ParkingLots
            .Where(p => !p.IsDeleted && p.ManagerUserId != null && userIds.Contains(p.ManagerUserId))
            .ToListAsync();

        return users
            .Select(u => ToResponse(u, employees.GetValueOrDefault(u.Id), lots.Where(p => p.ManagerUserId == u.Id)))
            .Where(m => Matches(m, searchTerm))
            .OrderBy(m => m.FullName)
            .ToList();
    }

    public async Task<EmployeeAccountResponseModel> GetManagerAsync(string managerId)
    {
        var user = await FindAccountAsync(managerId, ManagerRole);
        return await ManagerResponseAsync(user);
    }

    public async Task<EmployeeAccountResponseModel> UpdateManagerAsync(string managerId, UpdateEmployeeAccountModel model)
    {
        var user = await FindAccountAsync(managerId, ManagerRole);
        await UpdateDetailsAsync(user, model);
        return await ManagerResponseAsync(user);
    }

    public async Task<EmployeeAccountResponseModel> SetManagerStatusAsync(string managerId, bool isActive)
    {
        var user = await FindAccountAsync(managerId, ManagerRole);
        await SetActiveAsync(user, isActive, isActive ? "Active" : "Inactive");
        return await ManagerResponseAsync(user);
    }

    public async Task<EmployeeAccountResponseModel> AssignManagerLotsAsync(string managerId, AssignParkingLotsModel model)
    {
        var user = await FindAccountAsync(managerId, ManagerRole);
        if (!user.IsActive)
            throw new BadRequestException("Cannot assign parking lots to a deactivated Manager.");

        var lotIds = (model.ParkingLotIds ?? []).Distinct().ToList();
        var lots = await pmsContext.ParkingLots.Where(p => lotIds.Contains(p.Id) && !p.IsDeleted).ToListAsync();
        var missing = lotIds.Except(lots.Select(p => p.Id)).ToList();
        if (missing.Count > 0)
            throw new NotFoundException($"Parking lot {missing[0]} does not exist.");

        // One Manager per lot: a lot run by another active Manager must be released by that Manager first.
        // A lot whose Manager was deactivated can be handed over; its Staff stay and report to the new Manager.
        var otherManagerIds = lots
            .Where(p => p.ManagerUserId != null && p.ManagerUserId != user.Id)
            .Select(p => p.ManagerUserId)
            .Distinct()
            .ToList();
        if (otherManagerIds.Count > 0)
        {
            var activeOthers = await pmsContext.Employees
                .Where(e => otherManagerIds.Contains(e.UserId) && e.Status == "Active")
                .ToDictionaryAsync(e => e.UserId, e => e.FullName);
            var taken = lots.FirstOrDefault(p => p.ManagerUserId != null && activeOthers.ContainsKey(p.ManagerUserId));
            if (taken != null)
                throw new BadRequestException(
                    $"{taken.Name} already has a Manager ({activeOthers[taken.ManagerUserId]}). A lot can only have one Manager: remove it from that Manager first.");
        }

        var now = DateTime.UtcNow;
        foreach (var lot in lots.Where(p => p.ManagerUserId != user.Id))
        {
            lot.ManagerUserId = user.Id;
            lot.UpdatedAt = now;
        }

        var released = await pmsContext.ParkingLots
            .Where(p => p.ManagerUserId == user.Id && !lotIds.Contains(p.Id))
            .ToListAsync();
        foreach (var lot in released)
        {
            lot.ManagerUserId = null;
            lot.UpdatedAt = now;
        }

        await pmsContext.SaveChangesAsync();
        return await ManagerResponseAsync(user);
    }

    /* Manager -> Staff ---------------------------------------------- */

    public async Task<IEnumerable<AssignedParkingLotModel>> GetManagedLotsAsync(string managerId)
    {
        var lots = await ManagedLotsQuery(managerId).OrderBy(p => p.Name).ToListAsync();
        return lots.Select(ToLotModel).ToList();
    }

    public async Task<IEnumerable<AssignedParkingLotModel>> GetStaffLotsAsync(string staffId)
    {
        var lotIds = pmsContext.Employees
            .Where(e => e.UserId == staffId && e.ParkingLotId != null && e.Status != "Terminated")
            .Select(e => e.ParkingLotId.Value);
        var lots = await pmsContext.ParkingLots.Where(p => lotIds.Contains(p.Id) && !p.IsDeleted).OrderBy(p => p.Name).ToListAsync();
        return lots.Select(ToLotModel).ToList();
    }

    public async Task<IEnumerable<EmployeeAccountResponseModel>> GetStaffAsync(string managerId, int? parkingLotId = null, bool? isActive = null, string searchTerm = null)
    {
        var lots = await ManagedLotsQuery(managerId).ToDictionaryAsync(p => p.Id);
        if (parkingLotId.HasValue && !lots.ContainsKey(parkingLotId.Value))
            throw new ForbiddenException("You do not manage this parking lot.");

        List<int> lotIds = parkingLotId.HasValue ? [parkingLotId.Value] : lots.Keys.ToList();
        var employees = await pmsContext.Employees
            .Where(e => e.ParkingLotId != null && lotIds.Contains(e.ParkingLotId.Value) && e.Status != "Terminated")
            .ToDictionaryAsync(e => e.UserId);
        var userIds = employees.Keys.ToList();

        var query = userManager.Users.Where(u => u.Role == StaffRole && userIds.Contains(u.Id));
        if (isActive.HasValue) query = query.Where(u => u.IsActive == isActive.Value);
        var users = await query.ToListAsync();

        return users
            .Select(u =>
            {
                var employee = employees[u.Id];
                return ToResponse(u, employee, lots.Values.Where(p => p.Id == employee.ParkingLotId));
            })
            .Where(s => Matches(s, searchTerm))
            .OrderBy(s => s.FullName)
            .ToList();
    }

    public async Task<EmployeeAccountResponseModel> GetStaffByIdAsync(string managerId, string staffId)
    {
        var (user, employee, lot) = await FindManagedStaffAsync(managerId, staffId);
        return ToResponse(user, employee, [lot]);
    }

    public async Task<EmployeeAccountResponseModel> CreateStaffAsync(string managerId, CreateStaffAccountModel model)
    {
        // A lot without a Manager cannot get Staff; a Manager only creates Staff in their own lots
        var lot = await FindManagedLotAsync(managerId, model.ParkingLotId);

        var userName = string.IsNullOrWhiteSpace(model.UserName)
            ? await UniqueUserNameAsync(model.Email)
            : model.UserName.Trim();
        if (await userManager.FindByNameAsync(userName) != null)
            throw new BadRequestException("Username is not available");
        await EnsurePhoneIsUniqueAsync(model.PhoneNumber, null);

        // The role is fixed here: a Manager can never create an account above Staff
        var created = await userService.CreateStaffAsync(new CreateEmployeeAccountModel
        {
            UserName = userName,
            FullName = model.FullName,
            Email = model.Email,
            Password = model.Password,
            PhoneNumber = model.PhoneNumber,
            AssignedParkingLot = lot.Id.ToString()
        });

        var staffId = created.Id.ToString();
        var employee = await pmsContext.Employees.FirstOrDefaultAsync(e => e.UserId == staffId)
            ?? throw new NotFoundException("Employee record was not created.");
        employee.ParkingLotId = lot.Id;
        employee.HireDate = DateOnly.FromDateTime(DateTime.UtcNow);
        await pmsContext.SaveChangesAsync();

        var user = await userManager.FindByIdAsync(staffId)
            ?? throw new NotFoundException("Staff account was not created.");
        return ToResponse(user, employee, [lot]);
    }

    public async Task<EmployeeAccountResponseModel> UpdateStaffAsync(string managerId, string staffId, UpdateEmployeeAccountModel model)
    {
        var (user, employee, lot) = await FindManagedStaffAsync(managerId, staffId);
        await UpdateDetailsAsync(user, model);
        return ToResponse(user, employee, [lot]);
    }

    public async Task<EmployeeAccountResponseModel> AssignStaffLotAsync(string managerId, string staffId, AssignStaffParkingLotModel model)
    {
        var (user, employee, _) = await FindManagedStaffAsync(managerId, staffId);
        var lot = await FindManagedLotAsync(managerId, model.ParkingLotId);

        employee.ParkingLotId = lot.Id;
        employee.UpdatedAt = DateTime.UtcNow;
        await pmsContext.SaveChangesAsync();

        return ToResponse(user, employee, [lot]);
    }

    public async Task<EmployeeAccountResponseModel> SetStaffStatusAsync(string managerId, string staffId, bool isActive)
    {
        var (user, employee, lot) = await FindManagedStaffAsync(managerId, staffId);
        await SetActiveAsync(user, isActive, isActive ? "Active" : "Inactive");
        return ToResponse(user, employee, [lot]);
    }

    public async Task<bool> DeleteStaffAsync(string managerId, string staffId)
    {
        var (user, employee, _) = await FindManagedStaffAsync(managerId, staffId);
        employee.TerminationDate = DateOnly.FromDateTime(DateTime.UtcNow);
        await SetActiveAsync(user, false, "Terminated");
        return true;
    }

    /* Helpers -------------------------------------------------------- */

    private IQueryable<ParkingLot> ManagedLotsQuery(string managerId) =>
        pmsContext.ParkingLots.Where(p => p.ManagerUserId == managerId && !p.IsDeleted);

    private async Task<ParkingLot> FindManagedLotAsync(string managerId, int parkingLotId)
    {
        var lot = await pmsContext.ParkingLots.FirstOrDefaultAsync(p => p.Id == parkingLotId && !p.IsDeleted)
            ?? throw new NotFoundException("Parking lot does not exist.");
        if (lot.ManagerUserId != managerId)
            throw new ForbiddenException("You do not manage this parking lot.");
        return lot;
    }

    private async Task<ApplicationUser> FindAccountAsync(string userId, string role)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user == null || user.Role != role)
            throw new NotFoundException($"{role} does not exist.");
        return user;
    }

    /// <summary>Loads a Staff account and checks it works at a lot the Manager manages.</summary>
    private async Task<(ApplicationUser User, Employee Employee, ParkingLot Lot)> FindManagedStaffAsync(string managerId, string staffId)
    {
        var user = await FindAccountAsync(staffId, StaffRole);
        var employee = await pmsContext.Employees.FirstOrDefaultAsync(e => e.UserId == staffId && e.Status != "Terminated")
            ?? throw new NotFoundException("Staff does not exist.");

        var lot = employee.ParkingLotId.HasValue
            ? await pmsContext.ParkingLots.FirstOrDefaultAsync(p => p.Id == employee.ParkingLotId.Value && !p.IsDeleted)
            : null;
        if (lot == null || lot.ManagerUserId != managerId)
            throw new ForbiddenException("This staff member does not work at a parking lot you manage.");

        return (user, employee, lot);
    }

    private async Task<EmployeeAccountResponseModel> ManagerResponseAsync(ApplicationUser user)
    {
        var employee = await pmsContext.Employees.FirstOrDefaultAsync(e => e.UserId == user.Id);
        var lots = await ManagedLotsQuery(user.Id).OrderBy(p => p.Name).ToListAsync();
        return ToResponse(user, employee, lots);
    }

    private async Task UpdateDetailsAsync(ApplicationUser user, UpdateEmployeeAccountModel model)
    {
        var email = model.Email.Trim();
        var phone = model.PhoneNumber.Trim();

        var emailOwner = await userManager.FindByEmailAsync(email);
        if (emailOwner != null && emailOwner.Id != user.Id)
            throw new BadRequestException("Email address is already in use");
        await EnsurePhoneIsUniqueAsync(phone, user.Id);

        user.Email = email;
        user.PhoneNumber = phone;
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
            throw new BadRequestException(result.Errors.FirstOrDefault()?.Description ?? "Failed to update the account.");

        var employee = await pmsContext.Employees.FirstOrDefaultAsync(e => e.UserId == user.Id);
        if (employee != null)
        {
            employee.FullName = model.FullName.Trim();
            employee.Email = email;
            employee.Phone = phone;
            employee.UpdatedAt = DateTime.UtcNow;
            await pmsContext.SaveChangesAsync();
        }
    }

    private async Task SetActiveAsync(ApplicationUser user, bool isActive, string employeeStatus)
    {
        user.IsActive = isActive;
        if (isActive) user.EmailConfirmed = true;
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
            throw new BadRequestException(result.Errors.FirstOrDefault()?.Description ?? "Failed to update the account status.");

        var employee = await pmsContext.Employees.FirstOrDefaultAsync(e => e.UserId == user.Id);
        if (employee != null)
        {
            employee.Status = employeeStatus;
            employee.UpdatedAt = DateTime.UtcNow;
            await pmsContext.SaveChangesAsync();
        }
    }

    private async Task EnsurePhoneIsUniqueAsync(string phoneNumber, string exceptUserId)
    {
        var phone = phoneNumber?.Trim();
        if (string.IsNullOrEmpty(phone)) return;

        var taken = await userManager.Users.AnyAsync(u => u.PhoneNumber == phone && u.Id != exceptUserId);
        if (taken)
            throw new BadRequestException("Phone number is already in use");
    }

    /// <summary>Staff are created from an email; the username is its local part, made unique.</summary>
    private async Task<string> UniqueUserNameAsync(string email)
    {
        var local = new string((email ?? string.Empty).Split('@')[0].Where(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-').ToArray());
        var baseName = local.Length >= 5 ? local[..Math.Min(local.Length, 16)] : $"staff{local}";

        var candidate = baseName;
        for (var i = 1; await userManager.FindByNameAsync(candidate) != null; i++)
            candidate = $"{baseName}{i}";
        return candidate;
    }

    private static bool Matches(EmployeeAccountResponseModel account, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return true;
        var term = searchTerm.Trim();
        return new[] { account.FullName, account.UserName, account.Email, account.PhoneNumber, account.EmployeeCode }
            .Any(value => value != null && value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static AssignedParkingLotModel ToLotModel(ParkingLot lot) => new()
    {
        Id = lot.Id,
        Code = lot.Code,
        Name = lot.Name
    };

    private static EmployeeAccountResponseModel ToResponse(ApplicationUser user, Employee employee, IEnumerable<ParkingLot> lots) => new()
    {
        Id = user.Id,
        UserName = user.UserName,
        FullName = employee?.FullName ?? user.UserName,
        Email = user.Email,
        PhoneNumber = user.PhoneNumber,
        Role = user.Role,
        EmployeeCode = employee?.EmployeeCode,
        IsActive = user.IsActive,
        Status = employee?.Status ?? (user.IsActive ? "Active" : "Inactive"),
        CreatedAt = employee?.CreatedAt,
        ParkingLots = lots.Where(p => p != null).Select(ToLotModel).ToList()
    };
}
