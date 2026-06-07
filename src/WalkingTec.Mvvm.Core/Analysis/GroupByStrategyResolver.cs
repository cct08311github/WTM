#nullable enable

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 根據資料庫類型和查詢特徵選擇 GroupBy 策略。
    /// SqlServer、Oracle、MySql、PgSql 使用 ServerSideGroupByStrategy（SQL 推送 GroupBy）；
    /// 其餘（SQLite、Memory、DaMeng）使用 InProcessGroupByStrategy。
    /// </summary>
    public class GroupByStrategyResolver
    {
        // ServerSideGroupByStrategy is stateless — safe to share as a process-wide singleton.
        private static readonly ServerSideGroupByStrategy ServerSide = new();

        /// <summary>預設 Resolver 實例。</summary>
        public static GroupByStrategyResolver Default { get; } = new();

        /// <summary>
        /// 根據 DB 類型和查詢請求解析 GroupBy 策略。
        /// SqlServer、Oracle、MySql、PgSql 均支援標準 SQL GROUP BY，使用 ServerSideGroupByStrategy；
        /// SQLite 的 GroupBy Expression Tree 翻譯有限制，使用 InProcessGroupByStrategy。
        /// </summary>
        /// <remarks>
        /// <see cref="InProcessGroupByStrategy"/> carries mutable per-call state
        /// (<c>LastMaterializeCount</c>) and MUST NOT be shared across concurrent
        /// requests. A fresh instance is returned for every Resolve() call for the
        /// in-process path so that two simultaneous OLAP requests cannot race on
        /// that field. <see cref="ServerSideGroupByStrategy"/> is stateless and
        /// remains a shared singleton.
        /// </remarks>
        public virtual IGroupByStrategy Resolve(DBTypeEnum dbType, AnalysisQueryRequest req)
        {
            return dbType switch
            {
                DBTypeEnum.SqlServer => ServerSide,
                DBTypeEnum.Oracle    => ServerSide,
                DBTypeEnum.MySql     => ServerSide,
                DBTypeEnum.PgSql     => ServerSide,
                _                    => new InProcessGroupByStrategy()
            };
        }
    }
}
