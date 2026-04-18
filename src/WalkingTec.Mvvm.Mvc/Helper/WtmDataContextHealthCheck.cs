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
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(_timeout);

            var sw = Stopwatch.StartNew();
            var data = new Dictionary<string, object>
            {
                ["dbType"] = _dc.DBType.ToString(),
            };

            try
            {
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
