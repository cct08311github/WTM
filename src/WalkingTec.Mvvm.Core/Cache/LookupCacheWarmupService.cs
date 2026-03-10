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
    ///   <item>僅預熱 main tenant（TenantCode = null），多租戶各自在首次請求時暖機</item>
    /// </list>
    /// </para>
    /// </summary>
    public class LookupCacheWarmupService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILookupCacheService _cacheService;
        private readonly ILogger<LookupCacheWarmupService> _logger;

        public LookupCacheWarmupService(
            IServiceProvider serviceProvider,
            ILookupCacheService cacheService,
            ILogger<LookupCacheWarmupService> logger)
        {
            _serviceProvider = serviceProvider;
            _cacheService = cacheService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 讓主應用完成 startup 後再預熱
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);

            var types = _cacheService.GetWarmupTypes();
            if (types.Count == 0) return;

            _logger.LogInformation("[WTM] Starting lookup cache warm-up for {Count} type(s)...", types.Count);

            // 每個型別透過 reflection 呼叫泛型 GetAllAsync<T>
            var method = typeof(ILookupCacheService).GetMethod(nameof(ILookupCacheService.GetAllAsync))
                         ?? throw new InvalidOperationException("GetAllAsync method not found on ILookupCacheService.");

            using var scope = _serviceProvider.CreateScope();
            var dc = scope.ServiceProvider.GetService<IDataContext>() as DbContext;

            if (dc == null)
            {
                _logger.LogWarning("[WTM] Lookup cache warm-up skipped: EF Core DbContext not available.");
                return;
            }

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

            _logger.LogInformation("[WTM] Lookup cache warm-up completed.");
        }
    }
}
