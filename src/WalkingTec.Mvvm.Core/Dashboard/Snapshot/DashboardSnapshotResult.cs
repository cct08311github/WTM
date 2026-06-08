#nullable enable

namespace WalkingTec.Mvvm.Core.Dashboard.Snapshot;

/// <summary>
/// Holds the output of a completed dashboard snapshot job.
/// </summary>
public sealed class DashboardSnapshotResult
{
    /// <summary>Job identifier from <see cref="ScheduledDashboardJobConfig.JobId"/>.</summary>
    public string JobId { get; set; } = "";

    /// <summary>Dashboard ID that was snapshotted.</summary>
    public string DashboardId { get; set; } = "";

    /// <summary>Format of <see cref="Content"/>.</summary>
    public DashboardExportFormat Format { get; set; }

    /// <summary>
    /// Raw bytes of the exported file (e.g. .xlsx, .pdf, .png).
    /// </summary>
    public byte[] Content { get; set; } = System.Array.Empty<byte>();

    /// <summary>
    /// Suggested file extension (without leading dot), e.g. <c>xlsx</c>, <c>pdf</c>, <c>png</c>.
    /// </summary>
    public string FileExtension => Format switch
    {
        DashboardExportFormat.Excel => "xlsx",
        DashboardExportFormat.Pdf   => "pdf",
        DashboardExportFormat.Png   => "png",
        _                          => "bin"
    };
}
