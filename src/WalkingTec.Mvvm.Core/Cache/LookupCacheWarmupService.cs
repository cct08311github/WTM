#nullable enable
using System;
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

            using var scope = _serviceProvider.CreateScope();
            var (dc, owned) = ResolveDataContext(scope.ServiceProvider);

            if (dc == null)
            {
                _logger.LogWarning("[WTM] Lookup cache warm-up skipped: EF Core DbContext not available.");
                return;
            }

            try
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
            finally
            {
                // Only dispose when we created this DbContext ourselves (see
                // ResolveDataContext) — the DI-fallback instance's lifetime is owned by `scope`.
                if (owned) { dc.Dispose(); }
            }

            _logger.LogInformation("[WTM] Lookup cache warm-up completed.");
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
