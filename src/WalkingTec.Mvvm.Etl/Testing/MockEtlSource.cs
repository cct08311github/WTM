#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Etl.Pipeline;

namespace WalkingTec.Mvvm.Etl.Testing;

/// <summary>
/// 測試用 EtlSource — 回傳 in-memory DataTable，不連真實 DB。
/// 隨框架發布，供使用者測試自己的 ETL Job。
/// </summary>
public class MockEtlSource : IEtlSource
{
    private DataTable _data = new();

    /// <summary>設定測試資料</summary>
    public void SetData(DataTable data) => _data = data;

    public async IAsyncEnumerable<DataTable> ExtractBatchesAsync(
        string connectionString, string queryTemplate, object? watermarkValue,
        int batchSize, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int offset = 0;
        while (offset < _data.Rows.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = _data.Clone(); // schema only
            int end = Math.Min(offset + batchSize, _data.Rows.Count);
            for (int i = offset; i < end; i++)
            {
                batch.ImportRow(_data.Rows[i]);
            }
            offset = end;
            yield return batch;
            await Task.CompletedTask;
        }
    }

    public void Dispose() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
