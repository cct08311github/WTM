#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core.Notifications;

namespace WalkingTec.Mvvm.Core.Dashboard.Alerting;

/// <summary>
/// Background service that periodically fetches widget data and fires threshold alerts
/// via <see cref="IWtmWebhookSink"/> when a breach is detected.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Opt-in: does nothing unless <see cref="DashboardAlertOptions.EvaluationIntervalSeconds"/> &gt; 0
/// and at least one <see cref="IWtmWebhookSink"/> is registered.</item>
/// <item>De-duplication: alert-on-transition. The service tracks the set of currently breached
/// (dashboardId, widgetId, ruleId) keys. An alert fires only when the key transitions from
/// clear → breached. It re-fires after the breach clears and re-occurs.</item>
/// <item>Cooldown: additionally enforces <see cref="DashboardAlertOptions.AlertCooldownSeconds"/>
/// as a minimum gap between consecutive alerts for the same rule key.</item>
/// <item>Tenant-aware: iterates all dashboard summaries visible across all tenants via the
/// unrestricted <see cref="IDashboardService.ListAsync"/> (admin view).</item>
/// </list>
/// </remarks>
public sealed class DashboardAlertHostedService : BackgroundService
{
    private readonly IDashboardService _dashboardService;
    private readonly IWtmWebhookSink? _sink;
    private readonly IOptions<DashboardAlertOptions> _alertOptions;
    private readonly ILogger<DashboardAlertHostedService> _logger;

    // State: tracks currently-breached rule keys and last-fired times.
    // Key: "dashboardId|widgetId|ruleId"
    private readonly ConcurrentDictionary<string, DateTime> _breached = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastFired = new();

    public DashboardAlertHostedService(
        IDashboardService dashboardService,
        IWtmWebhookSink? sink,
        IOptions<DashboardAlertOptions> alertOptions,
        ILogger<DashboardAlertHostedService> logger)
    {
        _dashboardService = dashboardService ?? throw new ArgumentNullException(nameof(dashboardService));
        _sink = sink;
        _alertOptions = alertOptions ?? throw new ArgumentNullException(nameof(alertOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _alertOptions.Value;

        // No-op when disabled or no sink registered.
        if (opts.EvaluationIntervalSeconds <= 0 || _sink == null)
        {
            _logger.LogDebug(
                "DashboardAlertHostedService is inactive: EvaluationIntervalSeconds={Interval}, SinkRegistered={HasSink}",
                opts.EvaluationIntervalSeconds, _sink != null);
            return;
        }

        _logger.LogInformation(
            "DashboardAlertHostedService started: interval={Interval}s, cooldown={Cooldown}s",
            opts.EvaluationIntervalSeconds, opts.AlertCooldownSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown — stop silently.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error during threshold evaluation tick");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(opts.EvaluationIntervalSeconds),
                stoppingToken).ConfigureAwait(false);
        }
    }

    // ── Core evaluation ───────────────────────────────────────────────────────

    internal async Task EvaluateAllAsync(CancellationToken ct)
    {
        // Use "admin" wildcard list — empty roles so only public/all summaries are returned.
        // Implementations that support full admin enumeration (e.g. EfCoreDashboardService)
        // respect the roles array; JsonFileDashboardService also lists all that are accessible.
        var summaries = await _dashboardService.ListAsync("__alert_evaluator__", Array.Empty<string>(), null);

        var nowBreached = new HashSet<string>();

        foreach (var summary in summaries)
        {
            var dashboard = await _dashboardService.GetAsync(summary.Id, summary.TenantId);
            if (dashboard == null) continue;

            foreach (var (widgetId, widget) in dashboard.Widgets)
            {
                if (widget.Thresholds == null || widget.Thresholds.Count == 0)
                    continue;

                WidgetDataResult? dataResult;
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var timeoutSecs = _alertOptions.Value.EvaluationWidgetTimeoutSeconds;
                    if (timeoutSecs > 0)
                        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSecs));

                    dataResult = await _dashboardService.GetWidgetDataAsync(
                        summary.Id, widgetId, null, summary.TenantId, cts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "Widget data fetch timed out during alert evaluation: dashboard={DashboardId}, widget={WidgetId}",
                        summary.Id, widgetId);
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Widget data fetch failed during alert evaluation: dashboard={DashboardId}, widget={WidgetId}",
                        summary.Id, widgetId);
                    continue;
                }

                if (dataResult?.Error != null)
                    continue;

                foreach (var rule in widget.Thresholds)
                {
                    if (string.IsNullOrWhiteSpace(rule.RuleId))
                        continue;

                    var key = $"{summary.Id}|{widgetId}|{rule.RuleId}";
                    var breached = ThresholdEvaluator.IsBreached(dataResult!, rule);

                    if (breached)
                    {
                        nowBreached.Add(key);
                        await MaybeFireAlertAsync(key, summary.Id, widgetId, widget, rule, dataResult!, ct);
                    }
                    else
                    {
                        // Breach cleared — remove from active state so next breach re-fires.
                        _breached.TryRemove(key, out _);
                    }
                }
            }
        }

        // Clean up stale "lastFired" entries for rules that have fully cleared.
        // This prevents unbounded dictionary growth over time.
        var staleCooldownKeys = _lastFired.Keys.Except(nowBreached).ToList();
        foreach (var k in staleCooldownKeys)
        {
            // Only remove if it has been longer than twice the cooldown (safely expired).
            if (_lastFired.TryGetValue(k, out var lastFired) &&
                (DateTime.UtcNow - lastFired).TotalSeconds > _alertOptions.Value.AlertCooldownSeconds * 2)
            {
                _lastFired.TryRemove(k, out _);
            }
        }
    }

    private async Task MaybeFireAlertAsync(
        string key,
        string dashboardId,
        string widgetId,
        WidgetDefinition widget,
        WidgetThreshold rule,
        WidgetDataResult dataResult,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Alert-on-transition: only fire when the key is newly breached.
        var isNewBreach = !_breached.ContainsKey(key);

        // Cooldown guard: even for new breach episodes, enforce the cooldown window.
        if (_lastFired.TryGetValue(key, out var lastFiredAt))
        {
            var elapsed = (now - lastFiredAt).TotalSeconds;
            if (elapsed < _alertOptions.Value.AlertCooldownSeconds)
            {
                // Within cooldown window — record as breached but don't re-fire.
                _breached[key] = now;
                return;
            }
        }

        if (!isNewBreach && !_lastFired.ContainsKey(key))
        {
            // Already tracked as breached and never fired (shouldn't happen, but guard it).
            return;
        }

        // Mark the key as currently breached and record fire time.
        _breached[key] = now;
        _lastFired[key] = now;

        // Build the webhook message.
        var level = rule.Level == ThresholdAlertLevel.Error
            ? WebhookLevel.Error
            : WebhookLevel.Warning;

        var body = string.IsNullOrWhiteSpace(rule.Message)
            ? $"Widget **{widget.Title}** threshold breached: `{rule.Measure}` {OpSymbol(rule.Op)} {rule.Value}."
            : rule.Message;

        var msg = new WebhookMessage
        {
            Title = $"[Dashboard Alert] {widget.Title}",
            Body = body,
            Level = level,
            Fields = new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("Dashboard", dashboardId),
                new System.Collections.Generic.KeyValuePair<string, string>("Widget", widgetId),
                new System.Collections.Generic.KeyValuePair<string, string>("Rule", rule.RuleId),
                new System.Collections.Generic.KeyValuePair<string, string>("Measure", rule.Measure),
                new System.Collections.Generic.KeyValuePair<string, string>("Threshold", $"{OpSymbol(rule.Op)} {rule.Value}"),
                new System.Collections.Generic.KeyValuePair<string, string>("Level", rule.Level.ToString())
            }
        };

        try
        {
            await _sink!.SendAsync(msg, ct);
            _logger.LogInformation(
                "Dashboard threshold alert sent: key={Key}, level={Level}", key, rule.Level);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send threshold alert for key={Key}", key);
        }
    }

    private static string OpSymbol(ThresholdComparisonOp op) => op switch
    {
        ThresholdComparisonOp.Gt => ">",
        ThresholdComparisonOp.Ge => ">=",
        ThresholdComparisonOp.Lt => "<",
        ThresholdComparisonOp.Le => "<=",
        ThresholdComparisonOp.Eq => "==",
        _ => "?"
    };
}
