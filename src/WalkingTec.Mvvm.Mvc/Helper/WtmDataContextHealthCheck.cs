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
    /// Health check that probes a real <see cref="IDataContext"/> via
    /// <c>Database.CanConnectAsync</c> under a tight timeout. Returns
    /// <c>Unhealthy</c> with the exception message when the underlying provider
    /// rejects the connection. Introduced by issue #836 so apps don't
    /// reinvent the same boilerplate check in every Program.cs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>#741 (residual of #727):</b> this check is instantiated per-invocation via
    /// <c>ActivatorUtilities</c> (<c>AddTypeActivatedCheck&lt;WtmDataContextHealthCheck&gt;</c>),
    /// inside a fresh DI scope the health-check framework creates for each run. <c>AddWtmContext</c>
    /// only ever registers <c>services.TryAddScoped&lt;IDataContext, NullContext&gt;()</c> as a safe
    /// placeholder — real apps get their DataContext through <see cref="WTMContext.CreateDC"/>, never
    /// through generic DI. A constructor that only took <see cref="IDataContext"/> therefore ALWAYS
    /// resolved <see cref="NullContext"/> in real deployments, and the #441 guard below made that
    /// look like an intentional "Healthy (skipped)" — so <c>/ready</c> silently never probed the real
    /// database in prod, masking genuine DB outages behind a green health check.
    /// </para>
    /// <para>
    /// Fixed by resolving through the optional, DI-injected <see cref="WTMContext"/> first
    /// (<see cref="WTMContext.CreateDC"/> — the same connection-string/tenant-aware factory every
    /// other part of WTM uses), mirroring the WTMContext-first / DI-fallback pattern
    /// <c>ActionLogRetentionService</c> and <c>LookupCacheWarmupService</c> already use for their own
    /// hosted-service DB access (#727). The DI-injected <see cref="IDataContext"/> remains a fallback
    /// for hosts/tests that register a real <see cref="IDataContext"/> directly without registering
    /// <see cref="WTMContext"/> (e.g. <c>AddWtmHealthChecks</c> used standalone, without
    /// <c>AddWtmContext</c>).
    /// </para>
    /// WTM's DI default registers <see cref="NullContext"/> as <see cref="IDataContext"/>
    /// when no real DataContext is wired up. All <c>NullContext</c> members throw
    /// <see cref="NotImplementedException"/>, so this check must detect the sentinel
    /// and short-circuit rather than letting the exception escape the health framework
    /// and permanently mark <c>/ready</c> as Unhealthy (fix: issue #441).
    /// </remarks>
    public sealed class WtmDataContextHealthCheck : IHealthCheck
    {
        private readonly IDataContext _diDc;
        private readonly WTMContext? _wtm;
        private readonly TimeSpan _timeout;

        public WtmDataContextHealthCheck(IDataContext dc, WTMContext? wtm = null, TimeSpan timeout = default)
        {
            _diDc = dc ?? throw new ArgumentNullException(nameof(dc));
            _wtm = wtm;
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

            // Build the data dict inside the try so that dc.DBType (which can throw on
            // unusual IDataContext implementations) is also covered by the catch (fix: #441),
            // and so that a WTMContext.CreateDC() resolution failure (#741) is reported the
            // same way as any other probe failure rather than escaping uncaught.
            var data = new Dictionary<string, object>();
            IDataContext? dc = null;
            var owned = false;

            try
            {
                (dc, owned) = ResolveDataContext();

                // NullContext is WTM's DI sentinel (registered via TryAddScoped) for apps
                // that have not wired up a real IDataContext.  Every member on NullContext
                // throws NotImplementedException, so we must short-circuit here instead of
                // letting the exception escape the health framework and cause a false 503.
                if (dc is null || dc is NullContext)
                {
                    return HealthCheckResult.Healthy(
                        "No DataContext configured; health check skipped.");
                }

                data["dbType"] = dc.DBType.ToString();

                var ok = await dc.Database.CanConnectAsync(linked.Token).ConfigureAwait(false);
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
            finally
            {
                // Only dispose when we created this DataContext ourselves via
                // WTMContext.CreateDC() (see ResolveDataContext) — the DI-fallback instance's
                // lifetime is owned by the health-check's own DI scope, not by us.
                if (owned)
                {
                    dc?.Dispose();
                }
            }
        }

        /// <summary>
        /// #741 follow-up to #727: resolves the app's real, connection-string/tenant-routed
        /// DataContext for this probe rather than the DI placeholder.
        /// </summary>
        private (IDataContext? Dc, bool Owned) ResolveDataContext()
        {
            // Primary path (real deployments): WTMContext.CreateDC() is the framework's
            // connection-string/tenant-aware factory — the same one every other part of WTM
            // (controllers, VMs, WtmJob, EtlSchedulerService, ActionLogRetentionService,
            // LookupCacheWarmupService) actually uses to obtain a real DataContext. This
            // instance is NOT DI-tracked, so the caller must dispose it (Owned = true).
            var real = _wtm?.CreateDC(isLog: false, logerror: true);
            if (real != null)
            {
                return (real, true);
            }

            // Fallback: hosts/tests that register a real IDataContext directly in DI
            // (e.g. AddWtmHealthChecks used standalone, without AddWtmContext/WTMContext) or
            // that have not wired up a real DataContext at all (NullContext placeholder).
            // Lifetime is owned by the DI scope, not by us.
            return (_diDc, false);
        }
    }
}
