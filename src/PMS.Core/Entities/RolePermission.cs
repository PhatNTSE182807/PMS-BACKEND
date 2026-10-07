using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class RolePermission
{
    public string RoleId { get; set; }

    public int PermissionId { get; set; }

    public DateTime GrantedAt { get; set; }
}
