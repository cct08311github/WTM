#nullable enable
using System.Collections.Generic;
using System.Linq;

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
            Dictionary<string, AnalysisFieldMeta> whitelist);
    }
}
