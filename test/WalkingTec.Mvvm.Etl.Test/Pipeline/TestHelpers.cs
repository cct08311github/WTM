#nullable enable
using System;
using System.Data;
using WalkingTec.Mvvm.Etl.Pipeline;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// 測試用輔助方法
/// </summary>
public static class TestHelpers
{
    /// <summary>
    /// 產生含 OrderNo (string), Amount (decimal), UpdatedAt (DateTime) 的測試資料
    /// </summary>
    public static DataTable GenerateOrderData(int rowCount, DateTime? baseDate = null)
    {
        var dt = new DataTable();
        dt.Columns.Add("OrderNo", typeof(string));
        dt.Columns.Add("Amount", typeof(decimal));
        dt.Columns.Add("UpdatedAt", typeof(DateTime));

        var start = baseDate ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < rowCount; i++)
        {
            var row = dt.NewRow();
            row["OrderNo"] = $"ORD-{i:D8}";
            row["Amount"] = 100m + (i % 1000);
            row["UpdatedAt"] = start.AddSeconds(i);
            dt.Rows.Add(row);
        }

        return dt;
    }

    public static StagingTableSpec CreateTestStagingSpec() => new(
        "stg_orders",
        new StagingColumn("OrderNo", "NVARCHAR(50)"),
        new StagingColumn("Amount", "DECIMAL(18,2)"),
        new StagingColumn("UpdatedAt", "DATETIME2")
    );

    public static EtlPipelineConfig CreateTestConfig(Guid? jobId = null) => new()
    {
        JobId = jobId ?? Guid.NewGuid(),
        JobName = "TestJob",
        SourceConnectionString = "Source=test",
        TargetConnectionString = "Target=test",
        QueryTemplate = "SELECT * FROM Orders WHERE @watermark_clause",
        TargetTableName = "SyncedOrders",
        MergeKeyColumn = "OrderNo",
        BatchSize = 50_000,
        StagingTable = CreateTestStagingSpec()
    };
}

/// <summary>
/// 同步版 IProgress — 避免 Progress&lt;T&gt; 的 SynchronizationContext 非同步回調問題
/// </summary>
public class SynchronousProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;
    public SynchronousProgress(Action<T> handler) => _handler = handler;
    public void Report(T value) => _handler(value);
}
