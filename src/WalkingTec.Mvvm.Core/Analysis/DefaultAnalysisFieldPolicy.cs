#nullable enable
using System.Collections.Generic;
using System.Security.Claims;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 預設的分析欄位可見性策略（全部允許）。
    /// 作為 Phase 2 的基礎實作，後續可由使用者透過 DI 覆寫以結合 RBAC。
    /// </summary>
    public class DefaultAnalysisFieldPolicy : IAnalysisFieldPolicy
    {
        public IEnumerable<AnalysisFieldMeta> Filter(
            IEnumerable<AnalysisFieldMeta> fields,
            ClaimsPrincipal user)
        {
            // 預設不阻擋任何欄位，維持 Phase 1 的行為
            return fields;
        }
    }
}
