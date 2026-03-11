#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Etl.Pipeline;

namespace WalkingTec.Mvvm.Etl.Testing;

/// <summary>
/// 測試用 BulkLoader — 收集資料到 List，不做真正 BulkCopy。
/// 隨框架發布，供使用者測試自己的 ETL Job。
/// </summary>
public class MockBulkLoader : IBulkLoader
{
    public List<DataTable> LoadedBatches { get; } = new();
    public int BatchCount => LoadedBatches.Count;
    public int TotalRows => LoadedBatches.Sum(b => b.Rows.Count);
    public bool MergeCalled { get; private set; }
    public bool TruncateCalled { get; private set; }
    public bool EnsureStagingCalled { get; private set; }

    /// <summary>設定此值讓指定 batch 拋出異常（從 1 開始計數）</summary>
    public int? FailOnBatch { get; set; }

    /// <summary>每次 BulkLoad 完成後觸發</summary>
    public event EventHandler? OnBatchLoaded;

    public Task BulkLoadAsync(string connectionString, string stagingTableName,
        DataTable batch, CancellationToken cancellationToken = default)
    {
        LoadedBatches.Add(batch.Copy());

        if (FailOnBatch.HasValue && LoadedBatches.Count == FailOnBatch.Value)
            throw new InvalidOperationException($"MockBulkLoader: simulated failure on batch {FailOnBatch.Value}");

        OnBatchLoaded?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task MergeAsync(string connectionString, string stagingTableName,
        string targetTableName, string mergeKeyColumn,
        CancellationToken cancellationToken = default)
    {
        MergeCalled = true;
        return Task.CompletedTask;
    }

    public Task TruncateStagingAsync(string connectionString, string stagingTableName,
        CancellationToken cancellationToken = default)
    {
        TruncateCalled = true;
        return Task.CompletedTask;
    }

    public Task EnsureStagingTableAsync(string connectionString, string stagingTableName,
        StagingTableSpec spec, CancellationToken cancellationToken = default)
    {
        EnsureStagingCalled = true;
        return Task.CompletedTask;
    }
}
