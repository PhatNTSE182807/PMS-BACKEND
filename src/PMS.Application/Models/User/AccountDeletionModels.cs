using System;

namespace PMS.Application.Models.User;

/// <summary>States of an <c>AccountDeletionRequest</c>.</summary>
public static class AccountDeletionRequestStatuses
{
    /// <summary>Waiting for an Admin to lock the account.</summary>
    public const string Pending = "Pending";
    /// <summary>An Admin locked the account.</summary>
    public const string Completed = "Completed";
    /// <summary>The customer withdrew the request.</summary>
    public const string Cancelled = "Cancelled";
}

/// <summary>Body of POST /api/users/profile/deletion-request.</summary>
public class CreateAccountDeletionRequestModel
{
    /// <summary>Optional reason, at most 500 characters.</summary>
    public string Reason { get; set; }
}

/// <summary>A customer's account deletion request.</summary>
public class AccountDeletionRequestModel
{
    public long Id { get; set; }

    public string Reason { get; set; }

    public string Status { get; set; }

    public DateTime RequestedAt { get; set; }

    public DateTime? ClosedAt { get; set; }
}

/// <summary>One customer account in the Admin's User Accounts list.</summary>
public class CustomerAccountModel
{
    public string Id { get; set; }

    public string UserName { get; set; }

    public string FullName { get; set; }

    public string Email { get; set; }

    public string PhoneNumber { get; set; }

    public string Role { get; set; }

    public bool IsActive { get; set; }

    /// <summary>The customer's pending deletion request, or null.</summary>
    public AccountDeletionRequestModel DeletionRequest { get; set; }
}
