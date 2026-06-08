#nullable enable
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Core.Dashboard.Snapshot;

/// <summary>
/// Configuration for the scheduled dashboard snapshot/export subsystem.
/// All settings are opt-in; the default state leaves the hosted service dormant.
/// </summary>
public sealed class DashboardSnapshotOptions
{
    /// <summary>
    /// Scheduled snapshot jobs to run.
    /// When empty (default), the hosted service does nothing.
    /// </summary>
    public List<ScheduledDashboardJobConfig> Jobs { get; set; } = new();
}
