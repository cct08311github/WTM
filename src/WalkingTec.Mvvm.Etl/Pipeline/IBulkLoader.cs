#nullable enable
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Etl.Pipeline;

/// <summary>
/// ETL Load 介面 — 批量寫入目標 DB
/// </summary>
public interface IBulkLoader
{
    /// <summary>
    /// 將一批資料 BulkCopy 到 staging table
    /// </summary>
    Task BulkLoadAsync(
        string connectionString,
        string stagingTableName,
        DataTable batch,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 執行 MERGE INTO 從 staging table 合併到目標 table
    /// </summary>
    Task MergeAsync(
        string connectionString,
        string stagingTableName,
        string targetTableName,
        string mergeKeyColumn,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 「刪除重建」模式（10.5+）：在單一 transaction 內先
    /// <c>DELETE FROM target [WHERE whereClause]</c>，再
    /// <c>INSERT INTO target (cols) SELECT cols FROM staging</c>。
    /// <paramref name="whereClause"/> 為 null/空白 → 刪整張 target；
    /// 否則必須是合法 SQL 條件（不含 "WHERE" 字面）— 由 caller 負責
    /// 安全性，loader 做基本 prefix 守護後直接拼接。
    /// </summary>
    /// <remarks>
    /// 預設實作丟 <see cref="System.NotSupportedException"/>，框架內
    /// 提供的 MssqlBulkLoader / OracleBulkLoader 會 override。第三方
    /// 自製 IBulkLoader 不需 implement 即可保持相容（Replace 模式
    /// 啟動時才需要）。
    /// </remarks>
    Task ReplaceAsync(
        string connectionString,
        string stagingTableName,
        string targetTableName,
        string? whereClause,
        CancellationToken cancellationToken = default)
        => throw new System.NotSupportedException(
            $"{GetType().Name} does not support EtlLoadMode.Replace. " +
            "Use the framework-provided MssqlBulkLoader / OracleBulkLoader, " +
            "or implement ReplaceAsync on your own loader.");

    /// <summary>
    /// 清空 staging table（TRUNCATE）
    /// </summary>
    Task TruncateStagingAsync(
        string connectionString,
        string stagingTableName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 確認 staging table 存在，不存在則自動建立
    /// </summary>
    Task EnsureStagingTableAsync(
        string connectionString,
        string stagingTableName,
        StagingTableSpec spec,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 檢查指定資料表的欄位是否具有唯一限制 (PK 或 Unique Index)
    /// </summary>
    Task<bool> IsUniqueColumnAsync(
        string connectionString,
        string tableName,
        string columnName,
        CancellationToken cancellationToken = default);
}
