#nullable enable
// WF-20.1: WorkflowTimerHostedService — BackgroundService (Etl envelope pattern).
//
// Design §1 specification:
//   • Singleton over IServiceProvider; per-tick CreateScope().
//   • @now bound ONCE per tick via (TimeProvider.System fallback).GetUtcNow().UtcDateTime
//     (Etl EtlSchedulerService.cs:49 precedent — never SQL CURRENT_TIMESTAMP).
//   • Per-timer try/catch (poisoned timer never blocks batch) — in WorkflowTimerExecutor.
//   • Whole-tick try/catch — in ExecuteAsync.
//   • 3-attempt startup retry.
//   • UNCONDITIONAL ValidateDbType on first scope — exception escapes ExecuteAsync
//     → .NET default BackgroundServiceExceptionBehavior.StopHost (Memory fail-fast).
//
// Design §0 Memory verdict: throw out of ExecuteAsync → StopHost.
// The engine ctor would fail the first workflow op anyway; loud > limping.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

/// <summary>
/// Durable timer reaper hosted service (WF-20 Wave-5).
///
/// <para>Modeled on <c>EtlHostedService</c>: long-running <see cref="BackgroundService"/>
/// that creates a fresh DI scope each tick to pull candidates from
/// <c>WorkflowTimerExecutor</c>.</para>
///
/// <para>Registration: call <c>AddWtmWorkFlowTimers()</c> — opt-in, no-op without it.</para>
///
/// <para><strong>Memory fail-fast:</strong> on the very first scope,
/// <c>ValidateDbType</c> is called UNCONDITIONALLY.  If <c>DBTypeEnum.Memory</c> is detected,
/// the exception escapes <c>ExecuteAsync</c> and .NET's default
/// <c>BackgroundServiceExceptionBehavior.StopHost</c> terminates the host immediately.
/// Loud is better than silently limping — the engine cannot operate without a relational
/// provider.</para>
///
/// <para><strong>Timestamps:</strong> <c>@now</c> is bound once per tick via
/// <c>(_sp.GetService&lt;TimeProvider&gt;() ?? TimeProvider.System).GetUtcNow().UtcDateTime</c>
/// (Etl precedent: <c>EtlSchedulerService.cs:49</c>). Never <c>DateTime.UtcNow</c> inside a loop;
/// never SQL <c>CURRENT_TIMESTAMP</c>.</para>
/// </summary>
public sealed class WorkflowTimerHostedService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<WorkflowTimerHostedService> _logger;

    public WorkflowTimerHostedService(
        IServiceProvider sp,
        ILogger<WorkflowTimerHostedService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    // FIX-B5b: Startup retry backoff schedule.
    // Previous: 3 attempts with 2s/4s delays then host stopped — too aggressive for transient
    // DB-not-ready scenarios (e.g. container startup order, cloud DB cold-start).
    // New: only Memory InvalidOperationException escapes ExecuteAsync (StopHost);
    // all other transient failures use 5s/15s/60s exponential back-off then keep retrying
    // indefinitely at TimerPollInterval rather than stopping the host.
    private static readonly TimeSpan[] StartupBackoff = [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(60),
    ];

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup retry: keep retrying until validation passes or host stops.
        // Only Memory InvalidOperationException escapes (StopHost per §1 design).
        int attempt = 0;
        while (true)
        {
            try
            {
                await RunStartupValidationAsync(stoppingToken);
                break; // validation passed — proceed to the main poll loop
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return; // host is stopping — exit cleanly
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("Memory", StringComparison.Ordinal))
            {
                // Memory fail-fast: DO NOT retry — escape ExecuteAsync immediately so
                // BackgroundServiceExceptionBehavior.StopHost terminates the host.
                _logger.LogCritical(ex,
                    "WorkflowTimerHostedService: DBTypeEnum.Memory detected — " +
                    "the timer reaper requires a relational provider. " +
                    "Propagating exception to trigger StopHost.");
                throw;
            }
            catch (Exception ex)
            {
                // Transient failure (DB not ready, network blip, etc.) — keep retrying.
                // FIX-B5b: use 5s/15s/60s back-off; after the third attempt stay at 60s.
                var delay = attempt < StartupBackoff.Length
                    ? StartupBackoff[attempt]
                    : StartupBackoff[^1];
                attempt++;
                _logger.LogWarning(ex,
                    "WorkflowTimerHostedService startup attempt {Attempt} failed — " +
                    "retrying in {Delay}s (DB-not-ready or transient fault; only Memory exception stops host)",
                    attempt, (int)delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);
            }
        }

        // Main poll loop.
        while (!stoppingToken.IsCancellationRequested)
        {
            // Whole-tick try/catch: any unhandled exception in the tick is logged
            // but does NOT terminate the hosted service (resilient poller).
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WorkflowTimerHostedService tick failed — will retry after poll interval");
            }

            var options = _sp.GetService<IOptions<WorkFlowOptions>>()?.Value;
            var interval = options?.TimerPollInterval ?? TimeSpan.FromMinutes(1);
            await Task.Delay(interval, stoppingToken);
        }
    }

    // ── Startup validation (first scope, UNCONDITIONAL ValidateDbType) ────────

    private async Task RunStartupValidationAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var dc = scope.ServiceProvider.GetRequiredService<IDataContext>();

        // UNCONDITIONAL — always check on first scope; exception escapes ExecuteAsync
        // for Memory provider (StopHost). This is the design §1 requirement.
        ServiceCollectionExtensions.ValidateDbType(dc);

        // One dummy await so async context is established (mirrors Etl pattern).
        await Task.CompletedTask;
    }

    // ── Per-tick execution ────────────────────────────────────────────────────

    private async Task TickAsync(CancellationToken ct)
    {
        // Bind @now ONCE per tick — Etl precedent (EtlSchedulerService.cs:49).
        var now = (_sp.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime;

        using var scope = _sp.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<WorkflowTimerExecutor>();
        await executor.RunTickAsync(now, ct);
    }
}
