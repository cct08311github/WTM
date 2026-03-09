#nullable enable

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 根據資料庫類型和查詢特徵選擇 GroupBy 策略。
    /// Phase 1：一律回傳 InProcessGroupByStrategy（保持現有行為）。
    /// Phase 2：SqlServer/Oracle 等可回傳 ServerSideGroupByStrategy。
    /// </summary>
    public class GroupByStrategyResolver
    {
        private static readonly InProcessGroupByStrategy InProcess = new();

        /// <summary>預設 Resolver 實例（Phase 1 一律 InProcess）。</summary>
        public static GroupByStrategyResolver Default { get; } = new();

        /// <summary>
        /// 根據 DB 類型和查詢請求解析 GroupBy 策略。
        /// </summary>
        public virtual IGroupByStrategy Resolve(DBTypeEnum dbType, AnalysisQueryRequest req)
        {
            // Phase 2 will add ServerSideGroupByStrategy for SqlServer/Oracle
            // For now, always return InProcess (preserves Phase 1 behavior)
            return InProcess;
        }
    }
}
