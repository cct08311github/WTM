#nullable enable
// #667 completion: extract the execution-strategy + classifier-retry envelope out of
// WorkflowEngine into a shared static helper so non-engine callers (WorkflowTimerExecutor,
// and — via their own thin local wrappers — ProcessDefinitionPublisher / WorkflowEventLogWriter)
// can reuse the SAME legality-wrapping + idempotency logic instead of duplicating it.
//
// TWO SEPARATE CONCERNS (do not conflate — this was the root defect in the first #667 attempt):
//
//   (A) EXECUTION-STRATEGY WRAPPING (retryOnDeadlock: irrelevant — ALWAYS applied).
//       EF Core throws InvalidOperationException ("...does not support user-initiated
//       transactions...") when Database.BeginTransactionAsync() is called directly while the
//       DbContext's configured execution strategy has RetriesOnFailure == true (i.e. the host
//       called options.EnableRetryOnFailure() — a commonly-recommended cloud SQL Server/Postgres
//       setting). Routing body's own BeginTransactionAsync call through
//       Db.Database.CreateExecutionStrategy().ExecuteAsync(...) makes that call legal on every
//       host, retrying or not. This concern applies to EVERY transactional call site with
//       NO exceptions — it is not something callers can opt out of.
//
//   (B) DEADLOCK-RETRY (retryOnDeadlock parameter — opt-out for lease-reaper-covered paths).
//       The WorkflowDeadlockClassifier-driven outer retry loop that re-runs the whole unit on a
//       transient/deadlock failure. The Return-path (txReturn/txA/txB) and any other unit
//       explicitly covered by a self-healing background sweep (e.g. the Wave-5 Returning-lease
//       reaper) intentionally sets retryOnDeadlock: false — a single attempt; on failure the
//       reaper reclaims the stranded state on its own schedule instead of this method retrying
//       inline. retryOnDeadlock: false still fully applies concern (A) — legality is never
//       optional; only the extra retry attempts are skipped (maxAttempts collapses to 1).
namespace WalkingTec.Mvvm.WorkFlow.Engine;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.WorkFlow;

/// <summary>
/// Shared execution-strategy + optional classifier-retry envelope for every transactional unit
/// in the WorkFlow engine assembly. See the file-level comment for the (A)/(B) concern split.
/// Internal — this is a cross-file implementation-sharing seam, not a public extension point.
///
/// <para><strong>Why the primary overloads take <see cref="DatabaseFacade"/> +
/// <c>Action? clearChangeTracker</c> instead of a bare <see cref="DbContext"/>:</strong>
/// <see cref="Definition.ProcessDefinitionPublisher"/> operates over the <c>IDataContext</c>
/// abstraction (WTM's Clean Architecture boundary), not a concrete <c>DbContext</c> — and some
/// <c>IDataContext</c> implementations (e.g. hand-written test adapters wrapping an inner
/// DbContext, mirrored across several test files) are NOT themselves <c>DbContext</c> subclasses.
/// <c>IDataContext.Database</c> (a <see cref="DatabaseFacade"/>) is available on every
/// implementation and is sufficient for the execution-strategy legality wrap (concern A); the
/// <c>ChangeTracker.Clear()</c> idempotency step (ties to a concrete <c>DbContext</c>) is
/// therefore threaded through as an optional callback instead of a hard dependency, so callers
/// that cannot obtain a <c>DbContext</c> still get full legality-wrap + retry coverage — they
/// just lose the (best-effort, additive) idempotency guard, which is no worse than the pre-#667
/// status quo (zero wrap, zero guard) for that narrow case. <see cref="WorkflowEngine"/> and
/// <see cref="WorkflowTimerExecutor"/> always hold a real <c>DbContext</c> and use the
/// convenience overloads below, which supply <c>ChangeTracker.Clear</c> automatically.</para>
/// </summary>
internal static class WorkflowTransactionExecutor
{
    /// <summary>
    /// Run <paramref name="body"/> inside <paramref name="database"/>'s execution strategy.
    /// </summary>
    /// <param name="database">The <see cref="DatabaseFacade"/> whose execution strategy wraps
    /// <paramref name="body"/> (via <c>CreateExecutionStrategy()</c>).</param>
    /// <param name="clearChangeTracker">
    /// Invoked immediately before every invocation of <paramref name="body"/> (see the
    /// <c>DbContext</c> overloads for why) — pass <c>null</c> only when no concrete
    /// <c>DbContext</c> is reachable from the caller's abstraction (see the type-level remarks).
    /// </param>
    /// <param name="options">Supplies <see cref="WorkFlowOptions.DeadlockRetryAttempts"/> and
    /// <see cref="WorkFlowOptions.DeadlockRetryBaseDelay"/>.</param>
    /// <param name="logger">Destination for retry/exhaustion diagnostics.</param>
    /// <param name="body">The transactional unit to run — responsible for its own
    /// begin/commit/rollback (own-or-enlist) inside the strategy-wrapped delegate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="retryOnDeadlock">
    /// <c>true</c> (default-equivalent at call sites that pass it): classify failures via
    /// <see cref="WorkflowDeadlockClassifier"/> and retry the whole unit up to
    /// <see cref="WorkFlowOptions.DeadlockRetryAttempts"/> times, returning
    /// <see cref="WorkflowActionResult.DeadlockRetryExhausted"/> if every attempt fails.
    /// <c>false</c>: single attempt only (maxAttempts collapses to 1) — the caller has a
    /// self-healing backstop (e.g. the lease reaper) so an inline retry loop is unnecessary risk.
    /// Concern (A), the execution-strategy legality wrap, applies identically either way.
    /// </param>
    internal static async Task<WorkflowActionResult> ExecuteInTransactionAsync(
        DatabaseFacade database,
        Action? clearChangeTracker,
        WorkFlowOptions options,
        ILogger logger,
        Func<CancellationToken, Task<WorkflowActionResult>> body,
        CancellationToken ct,
        bool retryOnDeadlock = true)
    {
        int maxAttempts = retryOnDeadlock ? Math.Max(1, options.DeadlockRetryAttempts) : 1;
        var baseDelay = options.DeadlockRetryBaseDelay;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var strategy = database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(ct, async token =>
                {
                    // #290 FIX-1 (extended by #667): clear on every invocation of body — this
                    // outer attempt AND any inner retry the host-configured strategy performs
                    // (relevant even when retryOnDeadlock is false, since the HOST's own
                    // EnableRetryOnFailure strategy can still retry internally).
                    clearChangeTracker?.Invoke();
                    return await body(token);
                });
            }
            catch (Exception ex) when (WorkflowDeadlockClassifier.IsDeadlockVictim(ex)
                                        && attempt < maxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(
                    baseDelay.TotalMilliseconds * attempt
                    + Random.Shared.NextDouble() * baseDelay.TotalMilliseconds);

                logger.LogWarning(
                    "ExecuteInTransactionAsync: deadlock victim on attempt {Attempt}/{Max}. " +
                    "Retrying after {DelayMs:F0} ms. Exception: {ExMessage}",
                    attempt, maxAttempts, delay.TotalMilliseconds, ex.Message);

                await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (WorkflowDeadlockClassifier.IsDeadlockVictim(ex)
                                        && attempt >= maxAttempts)
            {
                // Exhausted all retry attempts (or retryOnDeadlock:false — single-attempt
                // fail-closed) — return a closed result code instead of propagating the raw
                // provider exception. When retryOnDeadlock is false the caller's self-healing
                // backstop (e.g. the lease reaper) is responsible for eventual recovery.
                logger.LogError(ex,
                    "ExecuteInTransactionAsync: deadlock victim on attempt {Attempt}/{Max} " +
                    "(retryOnDeadlock={RetryOnDeadlock}). Returning DeadlockRetryExhausted.",
                    attempt, maxAttempts, retryOnDeadlock);
                return WorkflowActionResult.DeadlockRetryExhausted;
            }
        }

        // Unreachable — the loop always returns or throws.
        throw new InvalidOperationException("ExecuteInTransactionAsync: unexpected fall-through.");
    }

    /// <summary>Convenience overload for callers holding a concrete <see cref="DbContext"/> —
    /// supplies <c>db.Database</c> and <c>db.ChangeTracker.Clear</c> automatically. Used by
    /// <see cref="WorkflowEngine"/> and <see cref="WorkflowTimerExecutor"/>.</summary>
    internal static Task<WorkflowActionResult> ExecuteInTransactionAsync(
        DbContext db,
        WorkFlowOptions options,
        ILogger logger,
        Func<CancellationToken, Task<WorkflowActionResult>> body,
        CancellationToken ct,
        bool retryOnDeadlock = true)
        => ExecuteInTransactionAsync(
            db.Database, db.ChangeTracker.Clear, options, logger, body, ct, retryOnDeadlock);

    /// <summary>
    /// Generic overload that allows <paramref name="body"/> to return an arbitrary result
    /// alongside the <see cref="WorkflowActionResult"/> (e.g. to carry a post-commit signal back
    /// to the caller without a post-commit re-read). See the scalar overload for parameter docs.
    /// </summary>
    internal static async Task<(WorkflowActionResult result, T extra)> ExecuteInTransactionAsync<T>(
        DatabaseFacade database,
        Action? clearChangeTracker,
        WorkFlowOptions options,
        ILogger logger,
        Func<CancellationToken, Task<(WorkflowActionResult result, T extra)>> body,
        T defaultExtra,
        CancellationToken ct,
        bool retryOnDeadlock = true)
    {
        int maxAttempts = retryOnDeadlock ? Math.Max(1, options.DeadlockRetryAttempts) : 1;
        var baseDelay = options.DeadlockRetryBaseDelay;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var strategy = database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(ct, async token =>
                {
                    // See the scalar overload for why ChangeTracker.Clear() is required
                    // before every invocation of body.
                    clearChangeTracker?.Invoke();
                    return await body(token);
                });
            }
            catch (Exception ex) when (WorkflowDeadlockClassifier.IsDeadlockVictim(ex)
                                        && attempt < maxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(
                    baseDelay.TotalMilliseconds * attempt
                    + Random.Shared.NextDouble() * baseDelay.TotalMilliseconds);

                logger.LogWarning(
                    "ExecuteInTransactionAsync<T>: deadlock victim on attempt {Attempt}/{Max}. " +
                    "Retrying after {DelayMs:F0} ms. Exception: {ExMessage}",
                    attempt, maxAttempts, delay.TotalMilliseconds, ex.Message);

                await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (WorkflowDeadlockClassifier.IsDeadlockVictim(ex)
                                        && attempt >= maxAttempts)
            {
                logger.LogError(ex,
                    "ExecuteInTransactionAsync<T>: deadlock victim on attempt {Attempt}/{Max} " +
                    "(retryOnDeadlock={RetryOnDeadlock}). Returning DeadlockRetryExhausted.",
                    attempt, maxAttempts, retryOnDeadlock);
                return (WorkflowActionResult.DeadlockRetryExhausted, defaultExtra);
            }
        }

        throw new InvalidOperationException("ExecuteInTransactionAsync<T>: unexpected fall-through.");
    }

    /// <summary>Convenience overload for callers holding a concrete <see cref="DbContext"/>.
    /// See the scalar <c>DbContext</c> overload.</summary>
    internal static Task<(WorkflowActionResult result, T extra)> ExecuteInTransactionAsync<T>(
        DbContext db,
        WorkFlowOptions options,
        ILogger logger,
        Func<CancellationToken, Task<(WorkflowActionResult result, T extra)>> body,
        T defaultExtra,
        CancellationToken ct,
        bool retryOnDeadlock = true)
        => ExecuteInTransactionAsync(
            db.Database, db.ChangeTracker.Clear, options, logger, body, defaultExtra, ct, retryOnDeadlock);
}
