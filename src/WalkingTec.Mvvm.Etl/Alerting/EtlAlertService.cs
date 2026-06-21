#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core.Notifications;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline.Sources;

namespace WalkingTec.Mvvm.Etl.Alerting;

/// <summary>
/// 預設 ETL 告警實作。
/// 支援 Email（SMTP）、per-job Webhook（HTTP POST JSON）、以及 shared
/// <see cref="IWtmWebhookSink"/>（DingTalk / WeCom / Feishu / Slack / Teams）三種通道。
/// </summary>
public class EtlAlertService : IEtlAlertService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<EtlAlertOptions> _options;
    private readonly ILogger<EtlAlertService> _logger;

    // Optional — injected only when IWtmWebhookSink is registered in the DI container.
    // Null when the shared webhook sink subsystem is not configured.
    private readonly IWtmWebhookSink? _webhookSink;

    public EtlAlertService(
        IHttpClientFactory httpClientFactory,
        IOptions<EtlAlertOptions> options,
        ILogger<EtlAlertService> logger,
        IWtmWebhookSink? webhookSink = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        _webhookSink = webhookSink;
    }

    public async Task SendAlertAsync(
        EtlJobDefinition jobDef,
        EtlRunLog runLog,
        CancellationToken ct = default)
    {
        await SendCoreAsync(jobDef, runLog, isSla: false, actualElapsedMs: null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SendSlaBreachAlertAsync(
        EtlJobDefinition jobDef,
        EtlRunLog runLog,
        long actualElapsedMs,
        CancellationToken ct = default)
    {
        await SendCoreAsync(jobDef, runLog, isSla: true, actualElapsedMs: actualElapsedMs, ct).ConfigureAwait(false);
    }

    private async Task SendCoreAsync(
        EtlJobDefinition jobDef,
        EtlRunLog runLog,
        bool isSla,
        long? actualElapsedMs,
        CancellationToken ct)
    {
        var tasks = new List<Task>();

        // Legacy per-job raw JSON webhook (unchanged behaviour).
        if (!string.IsNullOrWhiteSpace(jobDef.AlertWebhookUrl))
            tasks.Add(SendLegacyWebhookAsync(jobDef, runLog, isSla, actualElapsedMs, ct));

        // Legacy SMTP email (unchanged behaviour).
        if (!string.IsNullOrWhiteSpace(jobDef.AlertEmail))
            tasks.Add(SendEmailAsync(jobDef, runLog, isSla, actualElapsedMs, ct));

        // Shared webhook sink (DingTalk / WeCom / Feishu / Slack / Teams) — opt-in.
        if (_options.Value.EnableWebhookAlerts && _webhookSink != null)
            tasks.Add(SendWebhookCardAsync(jobDef, runLog, isSla, actualElapsedMs, ct));

        if (tasks.Count > 0)
            await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    // ── Shared webhook sink (DingTalk/WeCom/Feishu/Slack/Teams) ──────────

    /// <summary>
    /// Formats an ETL failure or SLA-breach alert as a structured <see cref="WebhookMessage"/>
    /// card and sends it via the registered <see cref="IWtmWebhookSink"/>.
    /// Error messages are sanitized by <c>EtlErrorSanitizer</c> before inclusion to prevent
    /// connection strings and secrets from leaking into webhook payloads.
    /// </summary>
    private async Task SendWebhookCardAsync(
        EtlJobDefinition jobDef,
        EtlRunLog runLog,
        bool isSla,
        long? actualElapsedMs,
        CancellationToken ct)
    {
        // _webhookSink is guaranteed non-null by the caller guard; but satisfy the compiler.
        if (_webhookSink is null) return;

        var durationSec = runLog.ElapsedMs > 0
            ? (runLog.ElapsedMs / 1000.0).ToString("F1") + "s"
            : (runLog.FinishedAt.HasValue
                ? (runLog.FinishedAt.Value - runLog.StartedAt).TotalSeconds.ToString("F1") + "s"
                : "—");

        string title;
        string body;
        WebhookLevel level;

        if (isSla && actualElapsedMs.HasValue)
        {
            var actualSec = (actualElapsedMs.Value / 1000.0).ToString("F1");
            level = WebhookLevel.Warning;
            title = $"[SLA Breach] ETL Job '{jobDef.Name}'";
            body  = $"Job **{jobDef.Name}** exceeded the expected duration.  \n" +
                    $"Expected: ≤ {jobDef.ExpectedDurationSeconds}s | Actual: {actualSec}s";
        }
        else
        {
            level = WebhookLevel.Error;
            title = $"[ETL Failure] Job '{jobDef.Name}'";
            body  = $"Job **{jobDef.Name}** has failed " +
                    $"{jobDef.ConsecutiveFailureCount} consecutive time(s).";
        }

        // Sanitize error excerpt — strip connection strings / secrets.
        var sanitizedError = string.IsNullOrWhiteSpace(runLog.ErrorMessage)
            ? string.Empty
            : SanitizeErrorMessage(runLog.ErrorMessage);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("Job",      jobDef.Name),
            new("Status",   isSla ? "SLA Breach" : "Failed"),
            new("Rows Extracted", runLog.ExtractedRows.ToString()),
            new("Rows Loaded",    runLog.LoadedRows.ToString()),
            new("Duration",       durationSec),
            new("Started",        runLog.StartedAt.ToString("u")),
            new("Finished",       runLog.FinishedAt.HasValue
                                    ? runLog.FinishedAt.Value.ToString("u")
                                    : "—"),
        };

        if (!string.IsNullOrEmpty(sanitizedError))
            fields.Add(new("Error", sanitizedError));

        var message = new WebhookMessage
        {
            Title  = title,
            Body   = body,
            Level  = level,
            Fields = fields,
        };

        try
        {
            await _webhookSink.SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to send ETL webhook card for job '{JobName}'", jobDef.Name);
        }
    }

    // ── Legacy per-job raw JSON webhook ──────────────────────────────────

    private async Task SendLegacyWebhookAsync(
        EtlJobDefinition jobDef,
        EtlRunLog runLog,
        bool isSla,
        long? actualElapsedMs,
        CancellationToken ct)
    {
        // Dispatch-time SSRF guard (#484): require absolute https:// and reject blocked IP
        // ranges. This mirrors the boundary validation in EtlJobDefinitionVM.Validate() but
        // runs defensively at POST time to cover jobs imported via API or migration scripts
        // that bypass the VM layer. Uses RestEtlSource.IsBlockedIp (the shared SSRF helper).
        var webhookUrl = jobDef.AlertWebhookUrl;
        if (!string.IsNullOrWhiteSpace(webhookUrl))
        {
            if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var webhookUri)
                || webhookUri.Scheme != Uri.UriSchemeHttps)
            {
                _logger.LogWarning(
                    "ETL alert webhook for job '{JobName}' skipped: URL is not an absolute https:// URI.",
                    jobDef.Name);
                return;
            }

            if (IPAddress.TryParse(webhookUri.Host, out var literalIp)
                && RestEtlSource.IsBlockedIp(literalIp))
            {
                _logger.LogWarning(
                    "ETL alert webhook for job '{JobName}' skipped: target host is in a blocked IP range (SSRF guard).",
                    jobDef.Name);
                return;
            }
        }

        string? messageOverride = null;
        if (isSla && actualElapsedMs.HasValue)
        {
            var actualSec = actualElapsedMs.Value / 1000.0;
            messageOverride =
                $"[SLA Breach] ETL Job '{jobDef.Name}' exceeded the expected duration.\n" +
                $"Expected: ≤ {jobDef.ExpectedDurationSeconds}s  " +
                $"Actual: {actualSec:F1}s\n" +
                $"Started: {runLog.StartedAt:u}  Finished: {runLog.FinishedAt:u}";
        }

        // L3 defence-in-depth: sanitize error message before it leaves the system (#484).
        // Mirrors the sanitization already applied in SendWebhookCardAsync — strips
        // connection strings / secrets from the stored ErrorMessage before egress.
        var sanitizedError = string.IsNullOrWhiteSpace(runLog.ErrorMessage)
            ? null
            : Pipeline.EtlErrorSanitizer.SanitizeRaw(runLog.ErrorMessage);

        var payload = new
        {
            text = messageOverride ?? BuildAlertMessage(jobDef, runLog),
            jobName = jobDef.Name,
            jobId = jobDef.ID,
            consecutiveFailures = jobDef.ConsecutiveFailureCount,
            errorMessage = sanitizedError,
            startedAt = runLog.StartedAt,
            finishedAt = runLog.FinishedAt,
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        using var client = _httpClientFactory.CreateClient("EtlAlert");
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await client
                .PostAsync(jobDef.AlertWebhookUrl, content, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "ETL alert webhook for job '{JobName}' returned {StatusCode}",
                    jobDef.Name, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to send ETL alert webhook for job '{JobName}'", jobDef.Name);
        }
    }

    // ── Email ─────────────────────────────────────────────────────────────

    private async Task SendEmailAsync(
        EtlJobDefinition jobDef,
        EtlRunLog runLog,
        bool isSla,
        long? actualElapsedMs,
        CancellationToken ct)
    {
        var smtp = _options.Value.Smtp;
        if (smtp == null || string.IsNullOrWhiteSpace(smtp.Host))
        {
            _logger.LogWarning(
                "ETL alert Email is configured on job '{JobName}' but SmtpAlertOptions.Host is empty",
                jobDef.Name);
            return;
        }

        string? messageOverride = null;
        if (isSla && actualElapsedMs.HasValue)
        {
            var actualSec = actualElapsedMs.Value / 1000.0;
            messageOverride =
                $"[SLA Breach] ETL Job '{jobDef.Name}' exceeded the expected duration.\n" +
                $"Expected: ≤ {jobDef.ExpectedDurationSeconds}s  " +
                $"Actual: {actualSec:F1}s\n" +
                $"Started: {runLog.StartedAt:u}  Finished: {runLog.FinishedAt:u}";
        }

        var subject = messageOverride != null
            ? $"[ETL Alert] Job '{jobDef.Name}' SLA breach"
            : $"[ETL Alert] Job '{jobDef.Name}' failed ({jobDef.ConsecutiveFailureCount} consecutive)";

        // L3 defence-in-depth: sanitize before email egress (#484).
        // Builds the alert body using a sanitized RunLog so connection strings / secrets
        // in ErrorMessage are stripped before they leave the system via SMTP.
        var sanitizedRunLog = string.IsNullOrWhiteSpace(runLog.ErrorMessage)
            ? runLog
            : new EtlRunLog
            {
                JobId            = runLog.JobId,
                Trigger          = runLog.Trigger,
                Result           = runLog.Result,
                ErrorMessage     = Pipeline.EtlErrorSanitizer.SanitizeRaw(runLog.ErrorMessage),
                StartedAt        = runLog.StartedAt,
                FinishedAt       = runLog.FinishedAt,
                ExtractedRows    = runLog.ExtractedRows,
                LoadedRows       = runLog.LoadedRows,
                ErrorRows        = runLog.ErrorRows,
                ElapsedMs        = runLog.ElapsedMs,
                WatermarkSnapshot = runLog.WatermarkSnapshot,
            };
        var body = messageOverride ?? BuildAlertMessage(jobDef, sanitizedRunLog);

        var from = string.IsNullOrWhiteSpace(smtp.FromName)
            ? new MailAddress(smtp.FromAddress)
            : new MailAddress(smtp.FromAddress, smtp.FromName);

        using var mail = new MailMessage { From = from, Subject = subject, Body = body };
        foreach (var addr in jobDef.AlertEmail!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            mail.To.Add(addr);
        }

        using var client = new SmtpClient(smtp.Host, smtp.Port)
        {
            EnableSsl = smtp.EnableSsl,
            Credentials = (smtp.UserName != null && smtp.Password != null)
                ? new NetworkCredential(smtp.UserName, smtp.Password)
                : null,
        };

        try
        {
            await client.SendMailAsync(mail, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to send ETL alert email for job '{JobName}'", jobDef.Name);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string BuildAlertMessage(EtlJobDefinition jobDef, EtlRunLog runLog)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ETL Job '{jobDef.Name}' has failed {jobDef.ConsecutiveFailureCount} consecutive time(s).");
        sb.AppendLine($"Started: {runLog.StartedAt:u}  Finished: {runLog.FinishedAt:u}");
        if (!string.IsNullOrWhiteSpace(runLog.ErrorMessage))
            sb.AppendLine($"Error: {runLog.ErrorMessage}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Strips connection string fragments and limits length.
    /// Mirrors EtlErrorSanitizer logic but works on already-sanitized RunLog strings
    /// (defence-in-depth: the sanitizer runs at RunLog write time; this runs at alert
    /// dispatch time, preventing leaks if a raw exception message ends up in ErrorMessage).
    /// </summary>
    private static string SanitizeErrorMessage(string raw)
    {
        return Pipeline.EtlErrorSanitizer.SanitizeRaw(raw);
    }
}
