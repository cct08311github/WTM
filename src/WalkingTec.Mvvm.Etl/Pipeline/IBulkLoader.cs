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
