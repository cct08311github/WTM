#nullable enable
using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace WalkingTec.Mvvm.Core
{
    public partial class WTMContext
    {
        #region LookupCache

        /// <summary>
        /// 取得靜態/參數表的快取資料（cache miss 時自動從 DB 補充）。
        /// Model 必須標記 <see cref="WalkingTec.Mvvm.Core.Cache.CacheLookupAttribute"/>。
        /// </summary>
        /// <param name="predicate">可選的記憶體過濾條件（Func，在記憶體中執行），不觸發額外 DB 查詢。</param>
        /// <returns>IReadOnlyList 防止呼叫端意外修改快取內容。</returns>
        public System.Collections.Generic.IReadOnlyList<T> GetLookup<T>(
            System.Func<T, bool>? predicate = null) where T : TopBasePoco
        {
            var svc = ServiceProvider?.GetService(typeof(WalkingTec.Mvvm.Core.Cache.ILookupCacheService))
                      as WalkingTec.Mvvm.Core.Cache.ILookupCacheService;
            if (svc == null)
                throw new InvalidOperationException(
                    "ILookupCacheService is not registered. Call services.AddWtmContext() first.");

            var attr = svc.GetAttribute(typeof(T));
            Microsoft.EntityFrameworkCore.DbContext dbCtx;
            IDataContext? altDc = null;
            try
            {
                if (!string.IsNullOrEmpty(attr?.ConnectionKey))
                {
                    altDc = CreateDC(cskey: attr.ConnectionKey);
                    dbCtx = altDc as Microsoft.EntityFrameworkCore.DbContext
                        ?? throw new InvalidOperationException(
                            $"ConnectionKey '{attr.ConnectionKey}' did not produce an EF Core DbContext.");
                }
                else
                {
                    dbCtx = DC as Microsoft.EntityFrameworkCore.DbContext
                        ?? throw new InvalidOperationException(
                            "GetLookup requires an EF Core DbContext. Ensure IDataContext is configured.");
                }

                // 解析 tenant isolation：Attribute 明確設定優先，否則使用全域預設
                bool useTenant = attr?.TenantIsolationOrNull ?? svc.DefaultTenantIsolation;
                var tenantId = useTenant ? LoginUserInfo?.TenantCode : null;

                var all = svc.GetAll<T>(dbCtx, tenantId);
                return predicate == null ? all : (System.Collections.Generic.IReadOnlyList<T>)[.. all.Where(predicate)];
            }
            finally
            {
                (altDc as IDisposable)?.Dispose();
            }
        }

        /// <summary>
        /// 非同步取得靜態/參數表的快取資料（cache miss 時自動從 DB 補充）。
        /// Model 必須標記 <see cref="WalkingTec.Mvvm.Core.Cache.CacheLookupAttribute"/>。
        /// </summary>
        /// <param name="predicate">可選的記憶體過濾條件（Func，在記憶體中執行），不觸發額外 DB 查詢。</param>
        /// <param name="ct">取消 token。</param>
        /// <returns>IReadOnlyList 防止呼叫端意外修改快取內容。</returns>
        public async System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<T>> GetLookupAsync<T>(
            System.Func<T, bool>? predicate = null,
            System.Threading.CancellationToken ct = default) where T : TopBasePoco
        {
            var svc = ServiceProvider?.GetService(typeof(WalkingTec.Mvvm.Core.Cache.ILookupCacheService))
                      as WalkingTec.Mvvm.Core.Cache.ILookupCacheService;
            if (svc == null)
                throw new InvalidOperationException(
                    "ILookupCacheService is not registered. Call services.AddWtmContext() first.");

            var attr = svc.GetAttribute(typeof(T));
            Microsoft.EntityFrameworkCore.DbContext dbCtx;
            IDataContext? altDc = null;
            try
            {
                if (!string.IsNullOrEmpty(attr?.ConnectionKey))
                {
                    altDc = CreateDC(cskey: attr.ConnectionKey);
                    dbCtx = altDc as Microsoft.EntityFrameworkCore.DbContext
                        ?? throw new InvalidOperationException(
                            $"ConnectionKey '{attr.ConnectionKey}' did not produce an EF Core DbContext.");
                }
                else
                {
                    dbCtx = DC as Microsoft.EntityFrameworkCore.DbContext
                        ?? throw new InvalidOperationException(
                            "GetLookupAsync requires an EF Core DbContext. Ensure IDataContext is configured.");
                }

                bool useTenant = attr?.TenantIsolationOrNull ?? svc.DefaultTenantIsolation;
                var tenantId = useTenant ? LoginUserInfo?.TenantCode : null;

                var all = await svc.GetAllAsync<T>(dbCtx, tenantId, ct).ConfigureAwait(false);
                return predicate == null ? all : (System.Collections.Generic.IReadOnlyList<T>)[.. all.Where(predicate)];
            }
            finally
            {
                (altDc as IDisposable)?.Dispose();
            }
        }

        /// <summary>
        /// 從快取中查找單筆資料。
        /// </summary>
        public T? GetLookupItem<T>(System.Func<T, bool> predicate) where T : TopBasePoco
        {
            return GetLookup<T>().FirstOrDefault(predicate);
        }

        /// <summary>
        /// 從快取生成下拉選單項目（ComboSelectListItem）。
        /// </summary>
        public System.Collections.Generic.List<ComboSelectListItem> GetLookupSelectList<T>(
            System.Func<T, object> valueField,
            System.Func<T, string> textField,
            System.Func<T, bool>? filter = null) where T : TopBasePoco
        {
            var items = filter == null
                ? (System.Collections.Generic.IEnumerable<T>)GetLookup<T>()
                : GetLookup<T>().Where(filter);
            return [.. items.Select(x => new ComboSelectListItem
            {
                Value = valueField(x)?.ToString(),
                Text = textField(x)
            })];
        }

        /// <summary>
        /// 強制重新載入快取：先失效所有租戶的快取，再立即從 DB 重新查詢填入。
        /// 適用於 admin 批次匯入後，避免失效後首次請求的冷查詢。
        /// </summary>
        public async System.Threading.Tasks.Task RefreshLookupAsync<T>(
            System.Threading.CancellationToken ct = default) where T : TopBasePoco
        {
            var svc = ServiceProvider?.GetService(typeof(WalkingTec.Mvvm.Core.Cache.ILookupCacheService))
                      as WalkingTec.Mvvm.Core.Cache.ILookupCacheService;
            if (svc == null)
                throw new InvalidOperationException(
                    "ILookupCacheService is not registered. Call services.AddWtmContext() first.");

            var attr = svc.GetAttribute(typeof(T));
            Microsoft.EntityFrameworkCore.DbContext dbCtx;
            IDataContext? altDc = null;
            try
            {
                if (!string.IsNullOrEmpty(attr?.ConnectionKey))
                {
                    altDc = CreateDC(cskey: attr.ConnectionKey);
                    dbCtx = altDc as Microsoft.EntityFrameworkCore.DbContext
                        ?? throw new InvalidOperationException(
                            $"ConnectionKey '{attr.ConnectionKey}' did not produce an EF Core DbContext.");
                }
                else
                {
                    dbCtx = DC as Microsoft.EntityFrameworkCore.DbContext
                        ?? throw new InvalidOperationException(
                            "RefreshLookupAsync requires an EF Core DbContext.");
                }

                bool useTenant = attr?.TenantIsolationOrNull ?? svc.DefaultTenantIsolation;
                var tenantId = useTenant ? LoginUserInfo?.TenantCode : null;
                await svc.RefreshAsync<T>(dbCtx, tenantId, ct).ConfigureAwait(false);
            }
            finally
            {
                (altDc as IDisposable)?.Dispose();
            }
        }

        #endregion
    }
}
