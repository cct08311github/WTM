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
    /// Background service that periodically deletes <see cref="ActionLog"/>
    /// rows older than per-type TTL configured in
    /// <see cref="ActionLogRetentionOptions"/>. Introduced by issue #832.
    /// </summary>
    /// <remarks>
    /// Schedule: runs once per calendar day at
    /// <see cref="ActionLogRetentionOptions.RunAtLocalHour"/>.
    /// <para>
    /// Deletion is batched via <c>ExecuteDeleteAsync</c> (EF Core 7+) with
    /// <see cref="ActionLogRetentionOptions.BatchSize"/>, looping until
    /// the current sweep's per-type candidates are drained — avoids
    /// transaction-log bloat and lock contention from a single massive
    /// <c>DELETE</c>.
    /// </para>
    /// <para>
    /// Fault-tolerant: any per-iteration exception is logged as a
    /// warning; the service never crashes the host.
    /// </para>
    /// </remarks>
    public class ActionLogRetentionService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly IOptionsMonitor<ActionLogRetentionOptions> _options;
        private readonly ILogger<ActionLogRetentionService> _logger;
        private readonly TimeProvider _timeProvider;

        public ActionLogRetentionService(
            IServiceProvider services,
            IOptionsMonitor<ActionLogRetentionOptions> options,
            ILogger<ActionLogRetentionService> logger,
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
                var next = ComputeNextRun(now, options.RunAtLocalHour);
                var wait = next - now;
                _logger.LogInformation(
                    "ActionLogRetention: next sweep scheduled for {NextRun} (in {Wait})",
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
                        "ActionLogRetention: sweep failed; will retry next cycle.");
                }
            }
        }

        /// <summary>
        /// Compute the next scheduled run time. If <paramref name="hour"/>
        /// has not yet passed today, schedule for today; otherwise tomorrow.
        /// Exposed internal for testing.
        /// </summary>
        internal static DateTimeOffset ComputeNextRun(DateTimeOffset now, int hour)
        {
            var target = new DateTimeOffset(now.Year, now.Month, now.Day, hour, 0, 0, now.Offset);
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
            ActionLogRetentionOptions options,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(options);
            var now = _timeProvider.GetUtcNow();
            var result = new RetentionRunResult();

            using var scope = _services.CreateScope();
            var dc = scope.ServiceProvider.GetService<IDataContext>()
                      ?? throw new InvalidOperationException(
                          "ActionLogRetentionService requires IDataContext in DI.");

            // Per-LogType sweep; 0 or negative days disables retention for
            // that type (keeps rows forever).
            await DeleteOneType(dc, ActionLogTypesEnum.Normal, options.NormalDays, now, options.BatchSize, result, ct)
                .ConfigureAwait(false);
            await DeleteOneType(dc, ActionLogTypesEnum.Exception, options.ExceptionDays, now, options.BatchSize, result, ct)
                .ConfigureAwait(false);
            await DeleteOneType(dc, ActionLogTypesEnum.Debug, options.DebugDays, now, options.BatchSize, result, ct)
                .ConfigureAwait(false);
            await DeleteOneType(dc, ActionLogTypesEnum.Job, options.JobDays, now, options.BatchSize, result, ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "ActionLogRetention: sweep complete. Normal={Normal} Exception={Exception} Debug={Debug} Job={Job} total={Total}",
                result.NormalDeleted, result.ExceptionDeleted, result.DebugDeleted, result.JobDeleted,
                result.TotalDeleted);

            return result;
        }

        private async Task DeleteOneType(
            IDataContext dc,
            ActionLogTypesEnum logType,
            int days,
            DateTimeOffset now,
            int batchSize,
            RetentionRunResult result,
            CancellationToken ct)
        {
            if (days <= 0) { return; } // disabled for this type
            if (batchSize <= 0) { batchSize = 5000; }

            var cutoff = now.UtcDateTime.AddDays(-days);
            long deletedTotal = 0;

            // Loop until no more matching rows. Each iteration deletes at
            // most `batchSize` rows via OrderBy+Take+ExecuteDelete. Using
            // OrderBy(ActionTime) ensures stable cutoff across iterations.
            while (!ct.IsCancellationRequested)
            {
                int batch;
                try
                {
                    batch = await dc.Set<ActionLog>()
                        .AsNoTracking()
                        .Where(l => l.ActionTime < cutoff && l.LogType == logType)
                        .OrderBy(l => l.ActionTime)
                        .Take(batchSize)
                        .ExecuteDeleteAsync(ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }

                deletedTotal += batch;
                if (batch < batchSize) { break; } // drained
            }

            switch (logType)
            {
                case ActionLogTypesEnum.Normal:    result.NormalDeleted    += deletedTotal; break;
                case ActionLogTypesEnum.Exception: result.ExceptionDeleted += deletedTotal; break;
                case ActionLogTypesEnum.Debug:     result.DebugDeleted     += deletedTotal; break;
                case ActionLogTypesEnum.Job:       result.JobDeleted       += deletedTotal; break;
            }
        }

        /// <summary>
        /// Per-run counters returned by
        /// <see cref="RunRetentionOnceAsync"/>.
        /// </summary>
        public sealed class RetentionRunResult
        {
            public long NormalDeleted { get; set; }
            public long ExceptionDeleted { get; set; }
            public long DebugDeleted { get; set; }
            public long JobDeleted { get; set; }
            public long TotalDeleted => NormalDeleted + ExceptionDeleted + DebugDeleted + JobDeleted;
        }
    }
}
