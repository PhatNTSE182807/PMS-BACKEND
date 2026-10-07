using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class SystemSetting
{
    public int Id { get; set; }

    public string SettingKey { get; set; }

    public string SettingValue { get; set; }

    public string ValueType { get; set; }

    public string Category { get; set; }

    public string Description { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string UpdatedBy { get; set; }
}
