#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core.Notifications;

namespace WalkingTec.Mvvm.Core.Dashboard.Snapshot;

/// <summary>
/// An <see cref="IDashboardSnapshotSink"/> that posts a completion notification
/// to a registered <see cref="IWtmWebhookSink"/> when a snapshot job finishes.
/// </summary>
/// <remarks>
/// Webhooks cannot carry binary attachments, so this sink sends a summary card
/// (job ID, dashboard ID, file name, byte count) — not the file bytes.
/// <para>
/// Silently no-ops when <see cref="IWtmWebhookSink"/> has not been registered.
/// </para>
/// Register via <c>services.AddDashboardWebhookNotificationSnapshotSink()</c> after
/// configuring a webhook sink (e.g. <c>services.AddWtmWebhookSink(...)</c>).
/// </remarks>
public sealed class WebhookNotificationSnapshotSink : IDashboardSnapshotSink
{
    private readonly IWtmWebhookSink? _webhookSink;
    private readonly ILogger<WebhookNotificationSnapshotSink> _logger;

    public WebhookNotificationSnapshotSink(
        IWtmWebhookSink? webhookSink,
        ILogger<WebhookNotificationSnapshotSink> logger)
    {
        // webhookSink may be null when IWtmWebhookSink is not registered.
        _webhookSink = webhookSink;
        _logger      = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task DeliverAsync(
        DashboardSnapshotResult snapshot,
        byte[] content,
        string fileName,
        string contentType,
        CancellationToken ct = default)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        if (content  == null) throw new ArgumentNullException(nameof(content));
        if (fileName == null) throw new ArgumentNullException(nameof(fileName));

        if (_webhookSink == null)
        {
            _logger.LogDebug(
                "WebhookNotificationSnapshotSink: IWtmWebhookSink is not registered — skipping webhook notification for job {JobId}.",
                snapshot.JobId);
            return;
        }

        var message = new WebhookMessage
        {
            Title  = $"Dashboard Snapshot Ready: {snapshot.DashboardId}",
            Body   = $"Scheduled snapshot job **{snapshot.JobId}** completed successfully.",
            Level  = WebhookLevel.Info,
            Fields = new List<KeyValuePair<string, string>>
            {
                new("Job",       snapshot.JobId),
                new("Dashboard", snapshot.DashboardId),
                new("File",      fileName),
                new("Size",      $"{content.Length:N0} bytes"),
                new("Format",    snapshot.Format.ToString())
            }
        };

        await _webhookSink.SendAsync(message, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "WebhookNotificationSnapshotSink: job {JobId} completion notice dispatched.",
            snapshot.JobId);
    }
}
