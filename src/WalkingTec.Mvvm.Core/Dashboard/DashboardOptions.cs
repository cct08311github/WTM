using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Dashboard;
public class DashboardOptions
{
    public string DashboardDirectory { get; set; } = "App_Data/dashboards";
    public bool EnableEditing { get; set; } = true;
    public int DefaultRefreshInterval { get; set; } = 60;
    public bool AllowIframeSameOrigin { get; set; } = false;

    /// <summary>
    /// Role names that are treated as administrators for Dashboard access.
    /// Administrators can view and edit all dashboards regardless of ownership or sharing settings.
    /// Defaults to ["Admin"] for backward compatibility.
    /// Configure this if your deployment uses a different admin role name (e.g. "SystemAdmin", "超級管理員").
    /// </summary>
    public string[] AdminRoles { get; set; } = ["Admin"];
}