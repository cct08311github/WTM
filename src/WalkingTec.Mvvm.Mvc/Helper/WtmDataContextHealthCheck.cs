#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Health check that probes the registered <see cref="IDataContext"/> via
    /// <c>Database.CanConnectAsync</c> under a tight timeout. Returns
    /// <c>Unhealthy</c> with the exception message when the underlying provider
    /// rejects the connection. Introduced by issue #836 so apps don't
    /// reinvent the same boilerplate check in every Program.cs.
    /// </summary>
    /// <remarks>
    /// WTM's DI default registers <see cref="NullContext"/> as <see cref="IDataContext"/>
    /// when no real DataContext is wired up. All <c>NullContext</c> members throw
    /// <see cref="NotImplementedException"/>, so this check must detect the sentinel
    /// and short-circuit rather than letting the exception escape the health framework
    /// and permanently mark <c>/ready</c> as Unhealthy (fix: issue #441).
    /// </remarks>
    public sealed class WtmDataContextHealthCheck : IHealthCheck
    {
        private readonly IDataContext _dc;
        private readonly TimeSpan _timeout;

        public WtmDataContextHealthCheck(IDataContext dc, TimeSpan timeout)
        {
            _dc = dc ?? throw new ArgumentNullException(nameof(dc));
            _timeout = timeout > TimeSpan.Zero
                ? timeout
                : TimeSpan.FromSeconds(2);
        }

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            // NullContext is WTM's DI sentinel (registered via TryAddScoped) for apps
            // that have not wired up a real IDataContext.  Every member on NullContext
            // throws NotImplementedException, so we must short-circuit here instead of
            // letting the exception escape the health framework and cause a false 503.
            if (_dc is NullContext)
            {
                return HealthCheckResult.Healthy(
                    "No DataContext configured; health check skipped.");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(_timeout);

            var sw = Stopwatch.StartNew();

            // Build the data dict inside the try so that _dc.DBType (which can throw on
            // unusual IDataContext implementations) is also covered by the catch (fix: #441).
            var data = new Dictionary<string, object>();

            try
            {
                data["dbType"] = _dc.DBType.ToString();

                var ok = await _dc.Database.CanConnectAsync(linked.Token).ConfigureAwait(false);
                sw.Stop();
                data["durationMs"] = sw.ElapsedMilliseconds;

                return ok
                    ? HealthCheckResult.Healthy("DataContext connection OK.", data)
                    : HealthCheckResult.Unhealthy("DataContext.CanConnectAsync returned false.", data: data);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                sw.Stop();
                data["durationMs"] = sw.ElapsedMilliseconds;
                data["timeoutMs"] = (long)_timeout.TotalMilliseconds;
                return HealthCheckResult.Unhealthy(
                    $"DataContext probe exceeded timeout ({_timeout.TotalMilliseconds} ms).",
                    data: data);
            }
            catch (Exception ex)
            {
                sw.Stop();
                data["durationMs"] = sw.ElapsedMilliseconds;
                return HealthCheckResult.Unhealthy(
                    "DataContext probe threw.", ex, data);
            }
        }
    }
}
