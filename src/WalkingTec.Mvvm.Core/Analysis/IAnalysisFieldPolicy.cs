#nullable enable
using System.Collections.Generic;
using System.Security.Claims;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 分析模式欄位可見性策略。
    /// 負責在回傳 Metadata 或執行查詢前，過濾使用者無權存取的欄位。
    /// </summary>
    public interface IAnalysisFieldPolicy
    {
        /// <summary>
        /// 過濾分析欄位。
        /// </summary>
        /// <param name="fields">掃描出的原始欄位白名單</param>
        /// <param name="user">當前登入使用者</param>
        /// <returns>該使用者有權存取的欄位集合</returns>
        IEnumerable<AnalysisFieldMeta> Filter(
            IEnumerable<AnalysisFieldMeta> fields,
            ClaimsPrincipal user);
    }
}
