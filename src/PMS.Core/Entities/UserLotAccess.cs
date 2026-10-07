using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class UserLotAccess
{
    public int Id { get; set; }

    public string UserId { get; set; }

    public int? ParkingLotId { get; set; }

    public string RoleId { get; set; }

    public DateTime GrantedAt { get; set; }

    public string GrantedBy { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public bool IsActive { get; set; }
}
