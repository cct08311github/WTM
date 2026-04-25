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

    /// <summary>
    /// 模擬「轉瞬故障」：前 N 次 BulkLoad 拋例外，第 N+1 次以後成功。
    /// 用來驗證 per-batch retry-with-backoff 的恢復行為（測試用）。
    /// 0 = 不模擬轉瞬故障。每成功一次自動歸零。
    /// </summary>
    public int TransientFailuresBeforeSuccess { get; set; }

    /// <summary>計數已發生的轉瞬故障（測試斷言用）。</summary>
    public int TransientFailuresObserved { get; private set; }

    /// <summary>每次 BulkLoad 完成後觸發</summary>
    public event EventHandler? OnBatchLoaded;

    public Task BulkLoadAsync(string connectionString, string stagingTableName,
        DataTable batch, CancellationToken cancellationToken = default)
    {
        // Transient-failure simulation runs BEFORE the success-path
        // bookkeeping so the executor's retry loop sees a clean
        // exception-then-success sequence.
        if (TransientFailuresBeforeSuccess > 0)
        {
            TransientFailuresBeforeSuccess--;
            TransientFailuresObserved++;
            throw new InvalidOperationException(
                $"MockBulkLoader: simulated transient failure (#{TransientFailuresObserved})");
        }

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

    public Task<bool> IsUniqueColumnAsync(string connectionString, string tableName, string columnName,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }
}
