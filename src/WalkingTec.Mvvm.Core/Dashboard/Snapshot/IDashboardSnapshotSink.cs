#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Dashboard.Snapshot;

/// <summary>
/// Delivery seam for scheduled dashboard snapshots.
/// Each registered sink receives the completed snapshot bytes after a
/// <see cref="IScheduledDashboardJob"/> run finishes successfully.
/// </summary>
/// <remarks>
/// Sinks are opt-in: register at least one via the
/// <c>AddDashboard*SnapshotSink</c> extension methods on
/// <see cref="DashboardServiceCollectionExtensions"/>.
/// When no sink is registered the framework falls back to log-only behaviour
/// (default, backward-compatible).
/// </remarks>
public interface IDashboardSnapshotSink
{
    /// <summary>
    /// Delivers the snapshot to its destination (file system, e-mail, external system, etc.).
    /// </summary>
    /// <param name="snapshot">Metadata produced by the snapshot job.</param>
    /// <param name="content">Raw bytes of the exported file.</param>
    /// <param name="fileName">Suggested file name including extension (e.g. <c>"sales-2026-06-20.xlsx"</c>).</param>
    /// <param name="contentType">MIME content type (e.g. <c>"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when delivery is finished or has been irrecoverably rejected.</returns>
    Task DeliverAsync(
        DashboardSnapshotResult snapshot,
        byte[] content,
        string fileName,
        string contentType,
        CancellationToken ct = default);
}
