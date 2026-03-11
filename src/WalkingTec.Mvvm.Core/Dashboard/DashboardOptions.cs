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
}