#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core.Support.Email;

namespace WalkingTec.Mvvm.Core.Dashboard.Snapshot;

/// <summary>
/// An <see cref="IDashboardSnapshotSink"/> that sends each snapshot as an e-mail attachment
/// via <see cref="IWtmEmailService"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Silently no-ops when <see cref="DashboardSnapshotDeliveryOptions.EmailRecipients"/> is empty.</item>
/// <item>Silently no-ops when <see cref="IWtmEmailService"/> has not been registered (resolved as null).</item>
/// <item>Register via <c>services.AddDashboardEmailSnapshotSink(recipients)</c> after calling
///   <c>services.AddWtmEmail(...)</c> or <c>services.AddWtmNullEmail()</c>.</item>
/// </list>
/// </remarks>
public sealed class EmailSnapshotSink : IDashboardSnapshotSink
{
    private readonly IWtmEmailService? _emailService;
    private readonly IOptions<DashboardSnapshotDeliveryOptions> _options;
    private readonly ILogger<EmailSnapshotSink> _logger;

    private const string DefaultSubjectTemplate = "Dashboard Snapshot: {DashboardId}";

    public EmailSnapshotSink(
        IWtmEmailService? emailService,
        IOptions<DashboardSnapshotDeliveryOptions> options,
        ILogger<EmailSnapshotSink> logger)
    {
        // emailService may be null when IWtmEmailService is not registered — graceful no-op.
        _emailService = emailService;
        _options      = options ?? throw new ArgumentNullException(nameof(options));
        _logger       = logger  ?? throw new ArgumentNullException(nameof(logger));
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

        if (_emailService == null)
        {
            _logger.LogDebug(
                "EmailSnapshotSink: IWtmEmailService is not registered — skipping e-mail delivery for job {JobId}.",
                snapshot.JobId);
            return;
        }

        if (opts.EmailRecipients == null || opts.EmailRecipients.Length == 0)
        {
            _logger.LogDebug(
                "EmailSnapshotSink: No recipients configured — skipping e-mail delivery for job {JobId}.",
                snapshot.JobId);
            return;
        }

        var subjectTemplate = string.IsNullOrWhiteSpace(opts.EmailSubjectTemplate)
            ? DefaultSubjectTemplate
            : opts.EmailSubjectTemplate;

        var subject = subjectTemplate
            .Replace("{DashboardId}", snapshot.DashboardId, StringComparison.OrdinalIgnoreCase)
            .Replace("{JobId}",       snapshot.JobId,       StringComparison.OrdinalIgnoreCase)
            .Replace("{FileName}",    fileName,             StringComparison.OrdinalIgnoreCase);

        var message = new EmailMessage
        {
            To      = opts.EmailRecipients,
            Subject = subject,
            Body    = $"Dashboard snapshot for '{snapshot.DashboardId}' (job: {snapshot.JobId}) is attached.",
            IsHtml  = false,
            Attachments = new[]
            {
                new EmailAttachment
                {
                    FileName    = fileName,
                    Data        = content,
                    ContentType = contentType
                }
            }
        };

        await _emailService.SendAsync(message, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "EmailSnapshotSink: job {JobId} snapshot e-mailed to {RecipientCount} recipient(s) ({Bytes} bytes).",
            snapshot.JobId, opts.EmailRecipients.Length, content.Length);
    }
}
