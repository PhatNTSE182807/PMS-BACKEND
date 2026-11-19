using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Filters;
using PMS.Application.Exceptions;
using PMS.Application.Models.DutySchedule;
using PMS.Application.Services.Impl;

namespace PMS.API.Filters;

/// <summary>
/// POS actions that change vehicles or cash: a Staff member must be checked in to a shift.
/// Reading is still allowed (the screens show a locked state), and Managers / Admins are not checked.
/// Turned off with WorkSchedule:RequireShiftForPos = false.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireOnShiftAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var allowed = context.ActionDescriptor.EndpointMetadata.OfType<AllowOffShiftAttribute>().Any();
        if (!allowed && !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && http.User.IsInRole("Staff"))
        {
            var options = http.RequestServices.GetRequiredService<DutyOptions>();
            var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (options.RequireShiftForPos && !await http.RequestServices.GetRequiredService<DutyScheduleService>().IsOnShiftAsync(userId))
                throw new ForbiddenException("You are not on shift. Start your shift to use the POS.");
        }
        await next();
    }
}

/// <summary>An action a Staff member may still do after their shift (handing in the cash drawer).</summary>
[AttributeUsage(AttributeTargets.Method)]
public class AllowOffShiftAttribute : Attribute;
