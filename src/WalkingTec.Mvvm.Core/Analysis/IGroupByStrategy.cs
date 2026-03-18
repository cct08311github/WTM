#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// GroupBy 聚合策略介面。
    /// Phase 1：InProcessGroupByStrategy（記憶體內 GroupBy）
    /// Phase 2：ServerSideGroupByStrategy（SQL 推送 GroupBy）
    /// </summary>
    public interface IGroupByStrategy
    {
        List<Dictionary<string, object?>> Execute<TModel>(
            IQueryable<TModel> query,
            AnalysisQueryRequest req,
            Dictionary<string, AnalysisFieldMeta> whitelist,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 非同步執行聚合查詢。預設實作以同步方式包裝 Execute()，
        /// 具體策略可 override 以獲得真正的非同步資料庫 I/O。
        /// </summary>
        Task<List<Dictionary<string, object?>>> ExecuteAsync<TModel>(
            IQueryable<TModel> query,
            AnalysisQueryRequest req,
            Dictionary<string, AnalysisFieldMeta> whitelist,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Execute(query, req, whitelist, cancellationToken));
    }
}
