#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace WalkingTec.Mvvm.Core.Cache
{
    /// <summary>
    /// EF Core SaveChanges 攔截器：偵測 <see cref="CacheLookupAttribute"/> 實體的寫入並自動失效快取。
    /// <para>
    /// 應用端需在 DbContextOptions 主動加入此 Interceptor 才能啟用自動失效：
    /// <code>
    /// optionsBuilder.AddInterceptors(
    ///     app.ApplicationServices.GetRequiredService&lt;LookupInvalidationInterceptor&gt;()
    /// );
    /// </code>
    /// 若未加入，快取仍會在 TTL 到期後自然失效（最終一致性）。
    /// </para>
    /// </summary>
    public class LookupInvalidationInterceptor : SaveChangesInterceptor
    {
        private readonly ILookupCacheService _cacheService;

        public LookupInvalidationInterceptor(ILookupCacheService cacheService)
        {
            _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            InvalidateDirtyLookups(eventData.Context);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            InvalidateDirtyLookups(eventData.Context);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private void InvalidateDirtyLookups(DbContext? context)
        {
            if (context == null) return;

            foreach (var entry in context.ChangeTracker.Entries())
            {
                if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                    continue;

                var entityType = entry.Entity.GetType();
                if (_cacheService.IsCacheable(entityType))
                {
                    _cacheService.InvalidateType(entityType);
                }
            }
        }
    }
}
