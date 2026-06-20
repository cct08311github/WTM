#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WalkingTec.Mvvm.Core.Dashboard.Snapshot;

/// <summary>
/// An <see cref="IDashboardSnapshotSink"/> that writes each snapshot to the local file system.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Directory is created on first write if it does not already exist.</item>
/// <item>File name format: <c>{DashboardId}-{JobId}-{UtcNow:yyyyMMddHHmmss}.{ext}</c></item>
/// <item>Path traversal is guarded: the resolved path must remain inside <see cref="DashboardSnapshotDeliveryOptions.OutputDirectory"/>.</item>
/// </list>
/// Register via <c>services.AddDashboardFileSnapshotSink(dir)</c>.
/// </remarks>
public sealed class FileSystemSnapshotSink : IDashboardSnapshotSink
{
    private readonly IOptions<DashboardSnapshotDeliveryOptions> _options;
    private readonly ILogger<FileSystemSnapshotSink> _logger;

    public FileSystemSnapshotSink(
        IOptions<DashboardSnapshotDeliveryOptions> options,
        ILogger<FileSystemSnapshotSink> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task DeliverAsync(
        DashboardSnapshotResult snapshot,
        byte[] content,
        string fileName,
        string contentType,
        CancellationToken ct = default)
    {
        if (snapshot  == null) throw new ArgumentNullException(nameof(snapshot));
        if (content   == null) throw new ArgumentNullException(nameof(content));
        if (fileName  == null) throw new ArgumentNullException(nameof(fileName));

        var opts = _options.Value;
        var dir  = opts.OutputDirectory;

        if (string.IsNullOrWhiteSpace(dir))
        {
            _logger.LogWarning(
                "FileSystemSnapshotSink: OutputDirectory is not configured — skipping delivery for job {JobId}.",
                snapshot.JobId);
            return;
        }

        // Normalise to absolute path so path-traversal guard is reliable.
        dir = Path.GetFullPath(dir);

        // Guard: file name must not escape the output directory.
        var safeName   = Path.GetFileName(fileName); // strip any path component
        var targetPath = Path.GetFullPath(Path.Combine(dir, safeName));
        if (!targetPath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(targetPath, dir, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "FileSystemSnapshotSink: Resolved path '{TargetPath}' escapes output directory '{Dir}' — delivery aborted for job {JobId}.",
                targetPath, dir, snapshot.JobId);
            return;
        }

        Directory.CreateDirectory(dir);

        await File.WriteAllBytesAsync(targetPath, content, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "FileSystemSnapshotSink: job {JobId} snapshot written to '{TargetPath}' ({Bytes} bytes).",
            snapshot.JobId, targetPath, content.Length);
    }
}
