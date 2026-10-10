using System;
using System.Collections.Generic;

namespace PMS.Application.Models.Profile;

public class UserProfileResponseModel
{
    public string UserId { get; set; }

    public string FullName { get; set; }

    public string PhoneNumber { get; set; }

    public string Email { get; set; }

    public string AvatarUrl { get; set; }

    public bool IsActive { get; set; }

    public string Status { get; set; }

    public bool HasPassword { get; set; }

    public string Role { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public List<VehicleResponseModel> Vehicles { get; set; } = new();
}
