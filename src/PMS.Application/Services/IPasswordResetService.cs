using PMS.Application.Models.User;

namespace PMS.Application.Services;

/// <summary>Self-service "forgot password" by email link (FR-AUTH-12), for every role.</summary>
public interface IPasswordResetService
{
    /// <summary>
    /// Emails a reset link when an active account uses this address. Does nothing otherwise,
    /// so the caller always gets the same answer and cannot tell which emails have an account.
    /// </summary>
    Task RequestResetAsync(ForgotPasswordModel model);

    /// <summary>Sets the new password; the link stops working afterwards.</summary>
    Task ResetPasswordAsync(ResetPasswordModel model);
}
