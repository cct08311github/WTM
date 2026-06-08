#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Dashboard.Snapshot;

/// <summary>
/// Abstraction for running a single dashboard snapshot on demand.
/// The framework provides a built-in implementation; hosts can replace it to add custom delivery.
/// </summary>
public interface IScheduledDashboardJob
{
    /// <summary>
    /// Executes the snapshot job described by <paramref name="config"/> and returns the result.
    /// </summary>
    Task<DashboardSnapshotResult> RunAsync(ScheduledDashboardJobConfig config, CancellationToken ct = default);
}
