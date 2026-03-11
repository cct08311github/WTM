#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;

namespace WalkingTec.Mvvm.Etl.Pipeline;

/// <summary>
/// ETL Extract 介面 — 從來源 DB 串流讀取資料
/// </summary>
public interface IEtlSource : IDisposable
{
    /// <summary>
    /// 以 DbDataReader 方式串流讀取資料，分批 yield return DataTable
    /// </summary>
    /// <param name="connectionString">來源 DB 連線字串</param>
    /// <param name="queryTemplate">SQL 查詢模板，包含 @watermark 參數佔位</param>
    /// <param name="watermarkValue">watermark 參數值（null = 不帶 watermark 條件）</param>
    /// <param name="batchSize">每批筆數</param>
    /// <param name="cancellationToken">取消 token</param>
    /// <returns>每個 DataTable 是一個 batch</returns>
    IAsyncEnumerable<DataTable> ExtractBatchesAsync(
        string connectionString,
        string queryTemplate,
        object? watermarkValue,
        int batchSize,
        CancellationToken cancellationToken = default);
}
