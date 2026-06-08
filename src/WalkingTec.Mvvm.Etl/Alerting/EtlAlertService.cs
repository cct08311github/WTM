#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Etl.Models;

namespace WalkingTec.Mvvm.Etl.Alerting;

/// <summary>
/// 預設 ETL 告警實作。
/// 支援 Email（SMTP）與 Webhook（HTTP POST JSON）兩種通道。
/// </summary>
public class EtlAlertService : IEtlAlertService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<EtlAlertOptions> _options;
    private readonly ILogger<EtlAlertService> _logger;

    public EtlAlertService(
        IHttpClientFactory httpClientFactory,
        IOptions<EtlAlertOptions> options,
        ILogger<EtlAlertService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task SendAlertAsync(
        EtlJobDefinition jobDef,
        EtlRunLog runLog,
        CancellationToken ct = default)
    {
        await SendCoreAsync(jobDef, runLog, messageOverride: null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SendSlaBreachAlertAsync(
        EtlJobDefinition jobDef,
        EtlRunLog runLog,
        long actualElapsedMs,
        CancellationToken ct = default)
    {
        var actualSec = actualElapsedMs / 1000.0;
        var message =
            $"[SLA Breach] ETL Job '{jobDef.Name}' exceeded the expected duration.\n" +
            $"Expected: ≤ {jobDef.ExpectedDurationSeconds}s  " +
            $"Actual: {actualSec:F1}s\n" +
            $"Started: {runLog.StartedAt:u}  Finished: {runLog.FinishedAt:u}";
        await SendCoreAsync(jobDef, runLog, messageOverride: message, ct).ConfigureAwait(false);
    }

    private async Task SendCoreAsync(
        EtlJobDefinition jobDef,
        EtlRunLog runLog,
        string? messageOverride,
        CancellationToken ct)
    {
        var tasks = new System.Collections.Generic.List<Task>();

        if (!string.IsNullOrWhiteSpace(jobDef.AlertWebhookUrl))
            tasks.Add(SendWebhookAsync(jobDef, runLog, messageOverride, ct));

        if (!string.IsNullOrWhiteSpace(jobDef.AlertEmail))
            tasks.Add(SendEmailAsync(jobDef, runLog, messageOverride, ct));

        if (tasks.Count > 0)
            await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    // ── Webhook ───────────────────────────────────────────────────────────

    private async Task SendWebhookAsync(
        EtlJobDefinition jobDef,
        EtlRunLog runLog,
        string? messageOverride,
        CancellationToken ct)
    {
        var payload = new
        {
            text = messageOverride ?? BuildAlertMessage(jobDef, runLog),
            jobName = jobDef.Name,
            jobId = jobDef.ID,
            consecutiveFailures = jobDef.ConsecutiveFailureCount,
            errorMessage = runLog.ErrorMessage,
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
        string? messageOverride,
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

        var subject = messageOverride != null
            ? $"[ETL Alert] Job '{jobDef.Name}' SLA breach"
            : $"[ETL Alert] Job '{jobDef.Name}' failed ({jobDef.ConsecutiveFailureCount} consecutive)";
        var body = messageOverride ?? BuildAlertMessage(jobDef, runLog);

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
}
