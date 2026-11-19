using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PMS.Application.Exceptions;
using PMS.Application.Models.User;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <inheritdoc cref="IAccountDeletionRequestService"/>
public class AccountDeletionRequestService(
    PmsDbContext pmsContext,
    UserManager<ApplicationUser> userManager,
    TimeProvider clock,
    IUserNotificationPublisher notifications = null,
    ILogger<AccountDeletionRequestService> logger = null) : IAccountDeletionRequestService
{
    private const string CustomerRole = "User";
    private const int MaxReasonLength = 500;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<AccountDeletionRequestModel> GetMineAsync(string userId)
    {
        var request = await PendingQuery(userId).AsNoTracking().FirstOrDefaultAsync();
        return request == null ? null : ToModel(request);
    }

    public async Task<AccountDeletionRequestModel> RequestAsync(string userId, CreateAccountDeletionRequestModel model)
    {
        var user = await userManager.FindByIdAsync(userId ?? "")
                   ?? throw new NotFoundException("Account not found.");
        if (!string.Equals(user.Role, CustomerRole, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException("Only customer accounts can ask to be deleted. Staff accounts are closed by their Manager or an Administrator.");
        if (!user.IsActive)
            throw new BadRequestException("This account is already locked.");

        var reason = model?.Reason?.Trim();
        if (reason?.Length > MaxReasonLength)
            throw new BadRequestException($"The reason can be at most {MaxReasonLength} characters.");

        // One pending request per account: asking again just returns it
        var existing = await PendingQuery(userId).AsNoTracking().FirstOrDefaultAsync();
        if (existing != null)
            return ToModel(existing);

        var request = new AccountDeletionRequest
        {
            UserId = userId,
            Reason = string.IsNullOrEmpty(reason) ? null : reason,
            Status = AccountDeletionRequestStatuses.Pending,
            RequestedAt = Now
        };
        pmsContext.AccountDeletionRequests.Add(request);
        await pmsContext.SaveChangesAsync();

        try
        {
            notifications?.AccountDeletionRequested(request.Id);
        }
        catch (Exception ex)
        {
            // The request is saved and shows in the Admin's customer list even without the bell notice
            logger?.LogWarning(ex, "Could not queue the Admin notice of deletion request {RequestId}", request.Id);
        }

        return ToModel(request);
    }

    public async Task CancelMineAsync(string userId)
    {
        var request = await PendingQuery(userId).FirstOrDefaultAsync()
                      ?? throw new NotFoundException("You have no pending account deletion request.");
        request.Status = AccountDeletionRequestStatuses.Cancelled;
        request.ClosedBy = userId;
        request.ClosedAt = Now;
        await pmsContext.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<CustomerAccountModel>> GetCustomersAsync(string searchTerm)
    {
        var term = searchTerm?.Trim();
        var users = userManager.Users.Where(u => u.Role == CustomerRole);
        List<string> nameMatches = [];
        if (!string.IsNullOrEmpty(term))
        {
            // Full names live on the Customers row, not on the account
            nameMatches = await pmsContext.Customers.AsNoTracking()
                .Where(c => c.UserId != null && c.FullName != null && c.FullName.Contains(term))
                .Select(c => c.UserId)
                .ToListAsync();
            users = users.Where(u =>
                (u.UserName != null && u.UserName.Contains(term)) ||
                (u.Email != null && u.Email.Contains(term)) ||
                (u.PhoneNumber != null && u.PhoneNumber.Contains(term)) ||
                nameMatches.Contains(u.Id));
        }

        var accounts = await users
            .Select(u => new { u.Id, u.UserName, u.Email, u.PhoneNumber, u.Role, u.IsActive })
            .ToListAsync();
        var ids = accounts.Select(a => a.Id).ToList();
        var names = await pmsContext.Customers.AsNoTracking()
            .Where(c => c.UserId != null && ids.Contains(c.UserId))
            .Select(c => new { c.UserId, c.FullName })
            .ToListAsync();
        var nameById = names.GroupBy(c => c.UserId).ToDictionary(g => g.Key, g => g.First().FullName);
        var pending = await pmsContext.AccountDeletionRequests.AsNoTracking()
            .Where(r => r.Status == AccountDeletionRequestStatuses.Pending && ids.Contains(r.UserId))
            .ToListAsync();
        var pendingById = pending.GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.RequestedAt).First());

        // Pending requests first (oldest first), then every other account by name
        return accounts
            .Select(a => new CustomerAccountModel
            {
                Id = a.Id,
                UserName = a.UserName,
                FullName = nameById.GetValueOrDefault(a.Id),
                Email = a.Email,
                PhoneNumber = a.PhoneNumber,
                Role = a.Role,
                IsActive = a.IsActive,
                DeletionRequest = pendingById.TryGetValue(a.Id, out var request) ? ToModel(request) : null
            })
            .OrderBy(c => c.DeletionRequest == null)
            .ThenBy(c => c.DeletionRequest?.RequestedAt)
            .ThenBy(c => c.FullName ?? c.UserName)
            .ToList();
    }

    public async Task CompletePendingAsync(string userId, string adminId)
    {
        var requests = await pmsContext.AccountDeletionRequests
            .Where(r => r.UserId == userId && r.Status == AccountDeletionRequestStatuses.Pending)
            .ToListAsync();
        if (requests.Count == 0)
            return;
        foreach (var request in requests)
        {
            request.Status = AccountDeletionRequestStatuses.Completed;
            request.ClosedBy = adminId;
            request.ClosedAt = Now;
        }
        await pmsContext.SaveChangesAsync();
    }

    private IQueryable<AccountDeletionRequest> PendingQuery(string userId) =>
        pmsContext.AccountDeletionRequests
            .Where(r => r.UserId == userId && r.Status == AccountDeletionRequestStatuses.Pending)
            .OrderByDescending(r => r.RequestedAt);

    private static AccountDeletionRequestModel ToModel(AccountDeletionRequest request) => new()
    {
        Id = request.Id,
        Reason = request.Reason,
        Status = request.Status,
        RequestedAt = request.RequestedAt,
        ClosedAt = request.ClosedAt
    };
}
