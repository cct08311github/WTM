using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Dashboard;
public class WidgetDataRequest
{
    public Dictionary<string, string> Parameters { get; set; } = new();
    public string? TenantId { get; set; }
}