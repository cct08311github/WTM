#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core.Cache
{
    /// <summary>
    /// 背景服務：在應用啟動後預熱所有 <see cref="CacheLookupAttribute.WarmOnStartup"/> = true 的靜態表快取。
    /// <para>
    /// 設計原則：
    /// <list type="bullet">
    ///   <item>後台非同步執行，不阻擋應用啟動</item>
    ///   <item>失敗只記 warning log，首次查詢會自動走 cache-miss 路徑補充</item>
    ///   <item>
    ///     <strong>Single-tenant (null-tenant) only.</strong>
    ///     This service performs a single best-effort warmup pass using
    ///     <c>TenantCode = null</c> (the main/default tenant). Each additional
    ///     tenant in a multi-tenant deployment is <em>not</em> pre-warmed here;
    ///     its cache is populated lazily on the first request that carries that
    ///     tenant's code. Multi-tenant deployments that require startup-time
    ///     warmup across all tenants should provide their own
    ///     <see cref="Microsoft.Extensions.Hosting.IHostedService"/> implementation
    ///     that iterates the tenant list and calls
    ///     <see cref="ILookupCacheService.GetAllAsync{T}"/> once per tenant.
    ///   </item>
    ///   <item>
    ///     <strong>Per-<see cref="CacheLookupAttribute.ConnectionKey"/> routing (#756).</strong>
    ///     Warm-up types are grouped by <see cref="CacheLookupAttribute.ConnectionKey"/> and each
    ///     group is warmed against the connection it actually serves from at runtime — mirroring
    ///     how <see cref="WTMContext.GetLookup{T}"/> / <see cref="WTMContext.GetLookupAsync{T}"/> /
    ///     <see cref="WTMContext.RefreshLookupAsync{T}"/> resolve their DbContext via
    ///     <c>WTMContext.CreateDC(cskey: attr.ConnectionKey)</c>. Types with no
    ///     <see cref="CacheLookupAttribute.ConnectionKey"/> keep warming against the default
    ///     connection (see <see cref="ResolveDataContext"/>). A group whose connection cannot be
    ///     resolved (unknown key, disabled connection, or no <see cref="WTMContext"/> available)
    ///     is skipped with a log — it is never warmed against the wrong connection, which would
    ///     otherwise poison the shared <c>wtm:lookup:{FullName}:{tenant}</c> cache key for the
    ///     full TTL with data read from the wrong database.
    ///   </item>
    /// </list>
    /// </para>
    /// </summary>
    public class LookupCacheWarmupService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILookupCacheService _cacheService;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly ILogger<LookupCacheWarmupService> _logger;

        public LookupCacheWarmupService(
            IServiceProvider serviceProvider,
            ILookupCacheService cacheService,
            IHostApplicationLifetime lifetime,
            ILogger<LookupCacheWarmupService> logger)
        {
            _serviceProvider = serviceProvider;
            _cacheService = cacheService;
            _lifetime = lifetime;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 等待應用完全啟動後才開始預熱，取代硬編碼的 3 秒延遲
            var tcs = new TaskCompletionSource();
            using var reg = _lifetime.ApplicationStarted.Register(() => tcs.TrySetResult());

            // 如果應用已停止或 stoppingToken 被取消，提前退出
            using var stopReg = stoppingToken.Register(() => tcs.TrySetCanceled());

            try
            {
                await tcs.Task.ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            var types = _cacheService.GetWarmupTypes();
            if (types.Count == 0) return;

            _logger.LogInformation("[WTM] Starting lookup cache warm-up for {Count} type(s)...", types.Count);

            // 每個型別透過 reflection 呼叫泛型 GetAllAsync<T>
            var method = typeof(ILookupCacheService).GetMethod(nameof(ILookupCacheService.GetAllAsync))
                         ?? throw new InvalidOperationException("GetAllAsync method not found on ILookupCacheService.");

            // #756: group warm-up types by CacheLookupAttribute.ConnectionKey — the runtime paths
            // (WTMContext.GetLookup/GetLookupAsync/RefreshLookupAsync) route each type through
            // CreateDC(cskey: attr.ConnectionKey). Warming every type against a single default
            // connection regardless of ConnectionKey (a) always fails for non-default types and
            // (b) can silently populate the shared cache key with data read from the WRONG
            // database when a same-named table happens to exist on the default connection too.
            var defaultGroupTypes = new List<Type>();
            var namedGroups = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);
            foreach (var type in types)
            {
                var connectionKey = _cacheService.GetAttribute(type)?.ConnectionKey;
                if (string.IsNullOrEmpty(connectionKey))
                {
                    defaultGroupTypes.Add(type);
                }
                else
                {
                    if (!namedGroups.TryGetValue(connectionKey, out var group))
                    {
                        group = [];
                        namedGroups[connectionKey] = group;
                    }
                    group.Add(type);
                }
            }

            using var scope = _serviceProvider.CreateScope();

            if (defaultGroupTypes.Count > 0)
            {
                await WarmDefaultGroupAsync(scope.ServiceProvider, defaultGroupTypes, method, stoppingToken)
                    .ConfigureAwait(false);
            }

            if (namedGroups.Count > 0 && !stoppingToken.IsCancellationRequested)
            {
                await WarmNamedGroupsAsync(scope.ServiceProvider, namedGroups, method, stoppingToken)
                    .ConfigureAwait(false);
            }

            _logger.LogInformation("[WTM] Lookup cache warm-up completed.");
        }

        /// <summary>
        /// Warms the types that have no <see cref="CacheLookupAttribute.ConnectionKey"/> against
        /// the default connection, resolved via <see cref="ResolveDataContext"/> — unchanged from
        /// the pre-#756 behaviour, except the resolution call is now guarded so a known-but-disabled
        /// default connection (which makes <c>WTMContext.CreateDC</c> throw
        /// <see cref="InvalidOperationException"/>) cannot escape <see cref="ExecuteAsync"/> and
        /// stop the host under <c>BackgroundServiceExceptionBehavior.StopHost</c>.
        /// </summary>
        private async Task WarmDefaultGroupAsync(
            IServiceProvider scopedProvider,
            List<Type> defaultGroupTypes,
            MethodInfo method,
            CancellationToken stoppingToken)
        {
            DbContext? dc = null;
            bool owned = false;
            try
            {
                (dc, owned) = ResolveDataContext(scopedProvider);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[WTM] Lookup cache warm-up: resolving the default connection failed: {Message}. " +
                    "{Count} default-connection type(s) will not be warmed; first request will populate the cache.",
                    ex.Message, defaultGroupTypes.Count);
                return;
            }

            if (dc == null)
            {
                _logger.LogWarning(
                    "[WTM] Lookup cache warm-up skipped for {Count} default-connection type(s): EF Core DbContext not available.",
                    defaultGroupTypes.Count);
                return;
            }

            try
            {
                await WarmTypesAsync(defaultGroupTypes, dc, method, stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                // Only dispose when we created this DbContext ourselves (see
                // ResolveDataContext) — the DI-fallback instance's lifetime is owned by the scope.
                if (owned) { dc.Dispose(); }
            }
        }

        /// <summary>
        /// Warms each <see cref="CacheLookupAttribute.ConnectionKey"/> group against its own
        /// connection, one <see cref="DbContext"/> per key (not per type). Requires a
        /// <see cref="WTMContext"/> in the scope — DI-fallback-only hosts cannot resolve a named
        /// connection key safely, so those types are skipped (never warmed against the wrong DB)
        /// and left for the documented lazy cache-miss fallback.
        /// <para>
        /// The <c>GetService&lt;WTMContext&gt;()</c> lookup is guarded the same way
        /// <see cref="ResolveDataContext"/> is guarded in <see cref="WarmDefaultGroupAsync"/> — a
        /// throwing scoped-service factory (e.g. a consumer-supplied <see cref="WTMContext"/>
        /// subclass whose constructor graph fails to resolve) must not escape and stop the host.
        /// </para>
        /// </summary>
        private async Task WarmNamedGroupsAsync(
            IServiceProvider scopedProvider,
            Dictionary<string, List<Type>> namedGroups,
            MethodInfo method,
            CancellationToken stoppingToken)
        {
            WTMContext? wtm;
            try
            {
                wtm = scopedProvider.GetService<WTMContext>();
            }
            catch (Exception ex)
            {
                var failedTypeNames = string.Join(", ", namedGroups.Values.SelectMany(g => g).Select(t => t.Name));
                _logger.LogWarning(
                    ex,
                    "[WTM] Lookup cache warm-up: resolving WTMContext for ConnectionKey routing failed: {Message}. " +
                    "ConnectionKey type(s) will not be warmed; first request will populate the cache. Type(s): {Types}",
                    ex.Message, failedTypeNames);
                return;
            }

            if (wtm == null)
            {
                var allNamedTypeNames = string.Join(", ", namedGroups.Values.SelectMany(g => g).Select(t => t.Name));
                _logger.LogInformation(
                    "[WTM] Lookup cache warm-up: ConnectionKey types cannot be warmed without WTMContext; " +
                    "first request will populate the cache. Type(s): {Types}",
                    allNamedTypeNames);
                return;
            }

            foreach (var (connectionKey, groupTypes) in namedGroups)
            {
                if (stoppingToken.IsCancellationRequested) break;

                if (!wtm.IsKnownConnectionKey(connectionKey))
                {
                    _logger.LogWarning(
                        "[WTM] Lookup cache warm-up skipped for ConnectionKey '{ConnectionKey}': not a configured " +
                        "connection. Type(s): {Types}",
                        connectionKey, string.Join(", ", groupTypes.Select(t => t.Name)));
                    continue;
                }

                DbContext? dc;
                try
                {
                    dc = wtm.CreateDC(isLog: false, cskey: connectionKey, logerror: true) as DbContext;
                }
                catch (Exception ex)
                {
                    // A known-but-disabled connection makes CreateDC throw InvalidOperationException
                    // (WTMContext.CreateDC.cs) — never let it escape and stop the host.
                    _logger.LogWarning(
                        ex,
                        "[WTM] Lookup cache warm-up failed to resolve ConnectionKey '{ConnectionKey}': {Message}. " +
                        "Type(s) will not be warmed; first request will populate the cache. Type(s): {Types}",
                        connectionKey, ex.Message, string.Join(", ", groupTypes.Select(t => t.Name)));
                    continue;
                }

                if (dc == null)
                {
                    _logger.LogWarning(
                        "[WTM] Lookup cache warm-up skipped for ConnectionKey '{ConnectionKey}': EF Core DbContext " +
                        "not available. Type(s): {Types}",
                        connectionKey, string.Join(", ", groupTypes.Select(t => t.Name)));
                    continue;
                }

                try
                {
                    await WarmTypesAsync(groupTypes, dc, method, stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    dc.Dispose();
                }
            }
        }

        /// <summary>
        /// Warms a batch of types (all sharing one already-resolved <see cref="DbContext"/>) via
        /// reflection over <see cref="ILookupCacheService.GetAllAsync{T}"/>. Per-type failures are
        /// logged as warnings and never propagate — a single broken type must not abort the rest
        /// of the batch or the host's startup.
        /// </summary>
        private async Task WarmTypesAsync(
            IReadOnlyList<Type> types,
            DbContext dc,
            MethodInfo method,
            CancellationToken stoppingToken)
        {
            foreach (var type in types)
            {
                if (stoppingToken.IsCancellationRequested) break;
                try
                {
                    var generic = method.MakeGenericMethod(type);
                    var task = (Task?)generic.Invoke(_cacheService, new object?[] { dc, null, stoppingToken });
                    if (task != null)
                        await task.ConfigureAwait(false);

                    _logger.LogInformation("[WTM] Lookup cache warmed: {TypeName}", type.Name);
                }
                catch (Exception ex)
                {
                    // method.Invoke wraps exceptions in TargetInvocationException; unwrap for readable logs
                    var inner = ex is TargetInvocationException tie ? tie.InnerException ?? tie : ex;
                    _logger.LogWarning(
                        inner,
                        "[WTM] Lookup cache warm-up failed for {TypeName}: {Message}. First request will populate the cache.",
                        type.Name, inner.Message);
                }
            }
        }

        /// <summary>
        /// #727: resolves the app's real, connection-string/tenant-routed DataContext for the
        /// startup warm-up pass.
        /// <para>
        /// <c>AddWtmContext</c> only ever registers
        /// <c>services.TryAddScoped&lt;IDataContext, NullContext&gt;()</c> as a safe placeholder
        /// default — apps obtain their real DataContext through <see cref="WTMContext.CreateDC"/>,
        /// never through generic DI. Before this fix,
        /// <c>scope.ServiceProvider.GetService&lt;IDataContext&gt;() as DbContext</c> always
        /// resolved <see cref="NullContext"/> (which does not extend <see cref="DbContext"/>, so
        /// the cast silently produced <c>null</c>) in every real deployment — this hosted
        /// service is registered UNCONDITIONALLY by <c>AddWtmContext</c> (not opt-in), so every
        /// WTM application with any <c>[CacheLookup(WarmOnStartup = true)]</c> type silently
        /// skipped startup warm-up on every boot, degrading (without crashing or being visibly
        /// broken) to the documented lazy cache-miss fallback — masked because no prior test
        /// exercised this hosted service through a real ASP.NET Core DI container built by
        /// <c>AddWtmContext</c> (mirrors the #721 <c>TokenTestFixture</c> masking pattern).
        /// Mirrors the WTMContext-first / DI-fallback resolution
        /// <see cref="WalkingTec.Mvvm.Core.Support.Quartz.WtmJob"/> and the Etl
        /// <c>EtlSchedulerService</c> already use for their own hosted-service DB access.
        /// </para>
        /// </summary>
        private static (DbContext? Dc, bool Owned) ResolveDataContext(IServiceProvider scopedProvider)
        {
            // Primary path (real deployments): WTMContext.CreateDC() is the framework's
            // connection-string/tenant-aware factory — the same one every other part of WTM
            // (controllers, VMs, WtmJob, EtlSchedulerService) actually uses. This instance is
            // NOT DI-tracked, so the caller must dispose it (Owned = true).
            var wtm = scopedProvider.GetService<WTMContext>();
            var dc = wtm?.CreateDC(isLog: false, logerror: true) as DbContext;
            if (dc != null)
            {
                return (dc, true);
            }

            // Fallback: hosts/tests that explicitly re-register IDataContext against a real
            // DbContext in DI without registering WTMContext itself. Lifetime is owned by the
            // DI scope, not by us.
            return (scopedProvider.GetService<IDataContext>() as DbContext, false);
        }
    }
}
