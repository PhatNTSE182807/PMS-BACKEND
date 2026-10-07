using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Permission
{
    public int Id { get; set; }

    public string Code { get; set; }

    public string Module { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }
}
