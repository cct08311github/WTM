#nullable enable
using System;

namespace WalkingTec.Mvvm.Core.Dashboard.Snapshot;

/// <summary>
/// Shared delivery options used by built-in <see cref="IDashboardSnapshotSink"/> implementations.
/// Set when calling the sink-specific registration helpers
/// (<c>AddDashboardFileSnapshotSink</c> / <c>AddDashboardEmailSnapshotSink</c>).
/// </summary>
public sealed class DashboardSnapshotDeliveryOptions
{
    // ── FileSystemSnapshotSink ────────────────────────────────────────────────

    /// <summary>
    /// Absolute path to the directory where snapshot files are written.
    /// Required when <see cref="FileSystemSnapshotSink"/> is registered.
    /// The directory is created automatically if it does not exist.
    /// </summary>
    public string? OutputDirectory { get; set; }

    // ── EmailSnapshotSink ─────────────────────────────────────────────────────

    /// <summary>
    /// One or more recipient e-mail addresses for snapshot delivery.
    /// Required when <see cref="EmailSnapshotSink"/> is registered.
    /// If this array is empty the sink is silently skipped.
    /// </summary>
    public string[] EmailRecipients { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Optional e-mail subject template.
    /// Supported placeholders: <c>{DashboardId}</c>, <c>{JobId}</c>, <c>{FileName}</c>.
    /// Defaults to <c>"Dashboard Snapshot: {DashboardId}"</c> when null or empty.
    /// </summary>
    public string? EmailSubjectTemplate { get; set; }
}
