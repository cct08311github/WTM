#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace WalkingTec.Mvvm.Core.Cache
{
    /// <summary>
    /// 靜態/參數表快取服務。
    /// 由框架在 DI 中注冊為 Singleton；應用透過 <see cref="WTMContext.GetLookup{T}"/> 存取。
    /// </summary>
    public interface ILookupCacheService
    {
        /// <summary>
        /// 取得指定型別的全表快取資料（cache miss 時自動從 DB 補充）。
        /// </summary>
        IReadOnlyList<T> GetAll<T>(DbContext dc, string? tenantId = null) where T : TopBasePoco;

        /// <summary>
        /// 非同步取得指定型別的全表快取資料（cache miss 時自動從 DB 補充）。
        /// </summary>
        Task<IReadOnlyList<T>> GetAllAsync<T>(DbContext dc, string? tenantId = null, CancellationToken ct = default)
            where T : TopBasePoco;

        /// <summary>使指定型別、指定租戶的快取失效。</summary>
        void Invalidate<T>(string? tenantId = null) where T : TopBasePoco;

        /// <summary>使指定型別的所有租戶快取失效（跨租戶寫入時呼叫）。</summary>
        void InvalidateType(Type entityType);

        /// <summary>此型別是否標記了 <see cref="CacheLookupAttribute"/>。</summary>
        bool IsCacheable(Type entityType);

        /// <summary>取得所有標記 WarmOnStartup=true 的型別清單（供 warmup service 使用）。</summary>
        IReadOnlyList<Type> GetWarmupTypes();

        /// <summary>取得指定型別的 <see cref="CacheLookupAttribute"/>，未標記時回傳 null。</summary>
        CacheLookupAttribute? GetAttribute(Type entityType);

        /// <summary>全域預設 TenantIsolation 值。</summary>
        bool DefaultTenantIsolation { get; }

        /// <summary>
        /// 強制重新載入：先失效指定型別的所有租戶快取，再立即從 DB 重新查詢填入快取。
        /// 適用於 admin 批次匯入後，避免失效後首次請求的冷查詢。
        /// </summary>
        Task RefreshAsync<T>(DbContext dc, string? tenantId = null, CancellationToken ct = default)
            where T : TopBasePoco;
    }
}
