#nullable enable

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 根據資料庫類型和查詢特徵選擇 GroupBy 策略。
    /// SqlServer/Oracle 使用 ServerSideGroupByStrategy（SQL 推送 GroupBy）；
    /// 其餘（SQLite、MySQL、PgSql、Memory、DaMeng）使用 InProcessGroupByStrategy。
    /// </summary>
    public class GroupByStrategyResolver
    {
        private static readonly InProcessGroupByStrategy InProcess = new();
        private static readonly ServerSideGroupByStrategy ServerSide = new();

        /// <summary>預設 Resolver 實例。</summary>
        public static GroupByStrategyResolver Default { get; } = new();

        /// <summary>
        /// 根據 DB 類型和查詢請求解析 GroupBy 策略。
        /// </summary>
        public virtual IGroupByStrategy Resolve(DBTypeEnum dbType, AnalysisQueryRequest req)
        {
            return dbType switch
            {
                DBTypeEnum.SqlServer => ServerSide,
                DBTypeEnum.Oracle => ServerSide,
                _ => InProcess
            };
        }
    }
}
