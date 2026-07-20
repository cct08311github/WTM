#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Background service that periodically deletes <see cref="RefreshTokenEntity"/>
    /// rows from <c>FrameworkRefreshTokens</c> per the TTLs configured in
    /// <see cref="RefreshTokenRetentionOptions"/>. Introduced by issue #757.
    /// Structurally mirrors <see cref="ActionLogRetentionService"/> 1:1 —
    /// intentionally NOT shared/generalized with it, so the two sweeps can
    /// evolve independently.
    /// </summary>
    /// <remarks>
    /// Schedule: runs once per calendar day at
    /// <see cref="RefreshTokenRetentionOptions.RunAtLocalHour"/>.
    /// <para>
    /// Deletion is batched via <c>ExecuteDeleteAsync</c> (EF Core 7+) with
    /// <see cref="RefreshTokenRetentionOptions.BatchSize"/>, looping until
    /// the current sweep's candidates are drained — avoids transaction-log
    /// bloat and lock contention from a single massive <c>DELETE</c>.
    /// </para>
    /// <para>
    /// <b>Reuse-attack forensics trade-off:</b> #721's reuse-attack chain
    /// detection (see <c>TokenService.RefreshTokenAsync</c>, lines ~72-90)
    /// treats presentation of an already-revoked token as a signal to
    /// revoke its entire descendant chain — but that detection only works
    /// while the revoked row still exists. Purging revoked rows downgrades
    /// containment from "detect and revoke the whole chain" to plain
    /// fail-closed rejection (a deleted token simply doesn't match any row,
    /// so <c>RefreshTokenAsync</c> returns <c>null</c> — still safe, just
    /// without the chain-revocation forensic signal). The default
    /// <see cref="RefreshTokenRetentionOptions.RevokedDays"/> (30 days) is
    /// deliberately far larger than the 7-day refresh-token lifetime
    /// (<c>TokenService.RefreshTokenExpiryDays</c>), so recently-active
    /// attack chains stay queryable well past any legitimate token's
    /// natural expiry.
    /// </para>
    /// <para>
    /// <b>Hard safety invariant:</b> the revoked-row sweep additionally
    /// requires <c>ExpiresUtc &lt; nowUtc</c> — a revoked-but-still-unexpired
    /// row is never deleted, regardless of <see cref="RefreshTokenRetentionOptions.RevokedDays"/>.
    /// Such a row is the reuse-attack tripwire's live evidence; deleting it
    /// early would erase the record before the token could even naturally
    /// expire. This conjunct is not configurable.
    /// </para>
    /// <para>
    /// Multi-replica sweeps are concurrent-safe and idempotent — each
    /// <c>ExecuteDeleteAsync</c> only affects rows still matching its
    /// predicate, so overlapping sweeps from multiple replicas simply
    /// converge on the same end state. No distributed lock is taken, same
    /// as <see cref="ActionLogRetentionService"/>.
    /// </para>
    /// <para>
    /// <see cref="WTMContext.CreateDC"/> resolves the default connection
    /// only — apps using tenant-per-database routing only have their
    /// default-tenant rows purged by this service, same limitation as
    /// <see cref="ActionLogRetentionService"/>.
    /// </para>
    /// <para>
    /// Fault-tolerant: any per-iteration exception is logged as a
    /// warning; the service never crashes the host.
    /// </para>
    /// </remarks>
    public class RefreshTokenRetentionService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly IOptionsMonitor<RefreshTokenRetentionOptions> _options;
        private readonly ILogger<RefreshTokenRetentionService> _logger;
        private readonly TimeProvider _timeProvider;

        public RefreshTokenRetentionService(
            IServiceProvider services,
            IOptionsMonitor<RefreshTokenRetentionOptions> options,
            ILogger<RefreshTokenRetentionService> logger,
            TimeProvider? timeProvider = null)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Main loop: wake up at the configured local hour each day and
            // run a sweep. First iteration sleeps to the *next* scheduled
            // run — it does not run immediately on startup (avoids stampede
            // when many replicas start at once). Operators who want
            // an immediate one-off sweep can expose
            // RunRetentionOnceAsync via an admin endpoint.
            while (!stoppingToken.IsCancellationRequested)
            {
                var options = _options.CurrentValue;
                if (!options.Enabled)
                {
                    // Poll Enabled once an hour — lets operators flip the
                    // switch via appsettings reload without app restart.
                    await DelaySafely(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var now = _timeProvider.GetLocalNow();
                var configuredHour = options.RunAtLocalHour;
                if (configuredHour < 0 || configuredHour > 23)
                {
                    // RunAtLocalHour is operator-supplied config (appsettings, possibly hot-reloaded
                    // via IOptionsMonitor on every loop iteration) and is never validated at the
                    // options-binding layer. An out-of-range value must never reach
                    // ComputeNextRun's DateTimeOffset construction unguarded — that would throw
                    // ArgumentOutOfRangeException outside any try/catch and, under the default
                    // BackgroundServiceExceptionBehavior.StopHost (.NET 6+), take down the entire
                    // host. Clamp defensively and tell the operator why the schedule doesn't match
                    // what they configured.
                    _logger.LogWarning(
                        "RefreshTokenRetention: RunAtLocalHour={ConfiguredHour} is outside the valid 0-23 range; clamping to {ClampedHour}.",
                        configuredHour, Math.Clamp(configuredHour, 0, 23));
                }
                var next = ComputeNextRun(now, configuredHour);
                var wait = next - now;
                _logger.LogInformation(
                    "RefreshTokenRetention: next sweep scheduled for {NextRun} (in {Wait})",
                    next, wait);

                await DelaySafely(wait, stoppingToken).ConfigureAwait(false);
                if (stoppingToken.IsCancellationRequested) { return; }

                try
                {
                    await RunRetentionOnceAsync(options, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "RefreshTokenRetention: sweep failed; will retry next cycle.");
                }
            }
        }

        /// <summary>
        /// Compute the next scheduled run time. If <paramref name="hour"/>
        /// has not yet passed today, schedule for today; otherwise tomorrow.
        /// Exposed internal for testing.
        /// </summary>
        /// <remarks>
        /// <paramref name="hour"/> is clamped to the valid <c>DateTimeOffset</c>
        /// hour range (0-23) before the schedule is built. <see cref="RefreshTokenRetentionOptions.RunAtLocalHour"/>
        /// is operator-supplied config with no upstream validation (and is
        /// re-read from <c>IOptionsMonitor.CurrentValue</c> every loop
        /// iteration, so a live appsettings reload can introduce a bad value
        /// mid-flight) — an out-of-range hour must never reach the
        /// <see cref="DateTimeOffset"/> constructor unguarded, or it throws
        /// <see cref="ArgumentOutOfRangeException"/> and, since this method
        /// is called from <see cref="ExecuteAsync"/> outside the sweep's
        /// try/catch, stops the whole host under the default
        /// <c>BackgroundServiceExceptionBehavior.StopHost</c> (.NET 6+).
        /// Clamping keeps the "the service never crashes the host" guarantee
        /// true for every input, not just the documented 0-23 range.
        /// </remarks>
        internal static DateTimeOffset ComputeNextRun(DateTimeOffset now, int hour)
        {
            var clampedHour = Math.Clamp(hour, 0, 23);
            var target = new DateTimeOffset(now.Year, now.Month, now.Day, clampedHour, 0, 0, now.Offset);
            if (target <= now) { target = target.AddDays(1); }
            return target;
        }

        private async Task DelaySafely(TimeSpan delay, CancellationToken ct)
        {
            if (delay <= TimeSpan.Zero) { return; }
            try
            {
                await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // expected on shutdown
            }
        }

        /// <summary>
        /// Execute the retention sweep once. Exposed public so tests
        /// (and admin-triggered endpoints) can invoke synchronously
        /// without waiting for the daily schedule.
        /// </summary>
        public async Task<RetentionRunResult> RunRetentionOnceAsync(
            RefreshTokenRetentionOptions options,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(options);
            var now = _timeProvider.GetUtcNow();
            var result = new RetentionRunResult();

            using var scope = _services.CreateScope();
            var (dc, owned) = ResolveDataContext(scope.ServiceProvider);
            try
            {
                result.ExpiredDeleted = await DeleteExpired(dc, options.ExpiredDays, now, options.BatchSize, ct)
                    .ConfigureAwait(false);
                result.RevokedDeleted = await DeleteRevoked(dc, options.RevokedDays, now, options.BatchSize, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                // Only dispose when we created this DataContext ourselves (see
                // ResolveDataContext) — the DI-fallback instance's lifetime is owned by `scope`.
                if (owned) { dc.Dispose(); }
            }

            _logger.LogInformation(
                "RefreshTokenRetention: sweep complete. Expired={Expired} Revoked={Revoked} total={Total}",
                result.ExpiredDeleted, result.RevokedDeleted, result.TotalDeleted);

            return result;
        }

        /// <summary>
        /// Mirrors <c>ActionLogRetentionService.ResolveDataContext</c> (added by #727 as a
        /// follow-up to #721): resolves the app's real, connection-string/tenant-routed
        /// DataContext for this hosted service's daily sweep.
        /// <para>
        /// <c>AddWtmContext</c> only ever registers
        /// <c>services.TryAddScoped&lt;IDataContext, NullContext&gt;()</c> as a safe placeholder
        /// default — apps obtain their real DataContext through <see cref="WTMContext.CreateDC"/>,
        /// never through generic DI. Resolving <c>IDataContext</c> directly from DI would
        /// silently resolve <see cref="NullContext"/> in every real deployment (its members throw
        /// <see cref="NotImplementedException"/>), which — swallowed by <see cref="ExecuteAsync"/>'s
        /// per-iteration <c>catch (Exception ex)</c> — would silently delete zero rows forever.
        /// </para>
        /// </summary>
        private static (IDataContext Dc, bool Owned) ResolveDataContext(IServiceProvider scopedProvider)
        {
            // Primary path (real deployments): WTMContext.CreateDC() is the framework's
            // connection-string/tenant-aware factory — the same one every other part of WTM
            // (controllers, VMs, WtmJob, EtlSchedulerService, ActionLogRetentionService) actually
            // uses. This instance is NOT DI-tracked, so the caller must dispose it (Owned = true).
            var wtm = scopedProvider.GetService<WTMContext>();
            var dc = wtm?.CreateDC(isLog: false, logerror: true);
            if (dc != null)
            {
                return (dc, true);
            }

            // Fallback: hosts/tests that explicitly re-register IDataContext against a real
            // DataContext in DI without registering WTMContext itself. Lifetime is owned by
            // the DI scope, not by us.
            return (scopedProvider.GetRequiredService<IDataContext>(), false);
        }

        private async Task<long> DeleteExpired(
            IDataContext dc,
            int expiredDays,
            DateTimeOffset now,
            int batchSize,
            CancellationToken ct)
        {
            if (expiredDays <= 0) { return 0; } // disabled
            if (batchSize <= 0) { batchSize = 5000; }

            var expiredCutoff = now.UtcDateTime.AddDays(-expiredDays);
            long deletedTotal = 0;

            // Loop until no more matching rows. Each iteration deletes at
            // most `batchSize` rows via OrderBy+Take+ExecuteDelete. Using
            // OrderBy(ExpiresUtc) ensures stable cutoff across iterations.
            while (!ct.IsCancellationRequested)
            {
                int batch;
                try
                {
                    batch = await dc.Set<RefreshTokenEntity>()
                        .AsNoTracking()
                        .Where(x => x.ExpiresUtc < expiredCutoff)
                        .OrderBy(x => x.ExpiresUtc)
                        .Take(batchSize)
                        .ExecuteDeleteAsync(ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return deletedTotal; }

                deletedTotal += batch;
                if (batch < batchSize) { break; } // drained
            }

            return deletedTotal;
        }

        private async Task<long> DeleteRevoked(
            IDataContext dc,
            int revokedDays,
            DateTimeOffset now,
            int batchSize,
            CancellationToken ct)
        {
            if (revokedDays <= 0) { return 0; } // disabled
            if (batchSize <= 0) { batchSize = 5000; }

            var revokedCutoff = now.UtcDateTime.AddDays(-revokedDays);
            var nowUtc = now.UtcDateTime;
            long deletedTotal = 0;

            while (!ct.IsCancellationRequested)
            {
                int batch;
                try
                {
                    // HARD SAFETY INVARIANT: `x.ExpiresUtc < nowUtc` must stay in this predicate
                    // regardless of configuration. A revoked-but-still-unexpired row is the
                    // reuse-attack chain's live tripwire (TokenService.cs RefreshTokenAsync,
                    // ~lines 72-90) — deleting it before it has also naturally expired would
                    // erase evidence the reuse-attack detection still depends on.
                    batch = await dc.Set<RefreshTokenEntity>()
                        .AsNoTracking()
                        .Where(x => x.RevokedUtc != null && x.RevokedUtc < revokedCutoff && x.ExpiresUtc < nowUtc)
                        .OrderBy(x => x.RevokedUtc)
                        .Take(batchSize)
                        .ExecuteDeleteAsync(ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return deletedTotal; }

                deletedTotal += batch;
                if (batch < batchSize) { break; } // drained
            }

            return deletedTotal;
        }

        /// <summary>
        /// Per-run counters returned by
        /// <see cref="RunRetentionOnceAsync"/>.
        /// </summary>
        public sealed class RetentionRunResult
        {
            public long ExpiredDeleted { get; set; }
            public long RevokedDeleted { get; set; }
            public long TotalDeleted => ExpiredDeleted + RevokedDeleted;
        }
    }
}
