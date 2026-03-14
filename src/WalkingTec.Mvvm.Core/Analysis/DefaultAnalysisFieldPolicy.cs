#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 預設的分析欄位可見性策略（根據 AllowedRoles 過濾）。
    /// </summary>
    public class DefaultAnalysisFieldPolicy : IAnalysisFieldPolicy
    {
        public IEnumerable<AnalysisFieldMeta> Filter(
            IEnumerable<AnalysisFieldMeta> fields,
            ClaimsPrincipal user)
        {
            return fields.Where(f =>
            {
                if (string.IsNullOrEmpty(f.AllowedRoles)) return true;
                
                // Admin has full access
                if (user.IsInRole("Admin")) return true;

                var allowed = f.AllowedRoles.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => r.Trim());
                return allowed.Any(role => user.IsInRole(role));
            });
        }
    }
}
