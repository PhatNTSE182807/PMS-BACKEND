using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Notifications;
using PMS.Application.Services;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

/// <summary>
/// The signed-in customer's notification bell: newest items, unread count, mark read and delete.
/// Notifications are created by Hangfire jobs (vehicle approved/rejected, check-in, check-out)
/// and pushed in real time through the parking-ops hub as "NotificationCreated".
/// </summary>
[Authorize(Roles = "User")]
public class NotificationsController(INotificationService notifications, IClaimService claimService) : ApiController
{
    /// <summary>Newest notifications (default 30, max 100) and the unread count.</summary>
    [HttpGet("~/api/notifications")]
    public async Task<IActionResult> GetMineAsync([FromQuery] bool unreadOnly = false, [FromQuery] int take = 30) =>
        Ok(ApiResult<NotificationListModel>.Success(await notifications.GetMineAsync(claimService.GetUserId(), unreadOnly, take)));

    /// <summary>How many notifications the user has not read yet (for the badge).</summary>
    [HttpGet("~/api/notifications/unread-count")]
    public async Task<IActionResult> GetUnreadCountAsync() =>
        Ok(ApiResult<int>.Success(await notifications.GetUnreadCountAsync(claimService.GetUserId())));

    /// <summary>Marks a single notification as read.</summary>
    [HttpPost("~/api/notifications/{id:long}/read")]
    public async Task<IActionResult> MarkReadAsync(long id) =>
        Ok(ApiResult<NotificationModel>.Success(await notifications.MarkReadAsync(claimService.GetUserId(), id)));

    /// <summary>Marks every unread notification as read; returns the count that changed.</summary>
    [HttpPost("~/api/notifications/read-all")]
    public async Task<IActionResult> MarkAllReadAsync() =>
        Ok(ApiResult<int>.Success(await notifications.MarkAllReadAsync(claimService.GetUserId())));

    /// <summary>Deletes a single notification.</summary>
    [HttpDelete("~/api/notifications/{id:long}")]
    public async Task<IActionResult> DeleteAsync(long id)
    {
        await notifications.DeleteAsync(claimService.GetUserId(), id);
        return Ok(ApiResult<object>.Success(null));
    }
}
