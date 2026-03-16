#nullable enable
using System;
using System.Data;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Testing;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// 回歸測試 — 針對已知 bug 修復與邊界場景
///
/// 涵蓋：
/// - WatermarkStrategy.UpdateFromBatchMax：int 值正確轉換為 long（#regression-int-watermark）
/// - WatermarkStrategy.UpdateFromBatchMax：全 null batch 不更新 pending（#regression-null-batch-watermark）
/// - EtlPipelineExecutor：BatchSize=0 快速失敗（#regression-batchsize-zero）
/// - EtlPipelineExecutor：TransformFunc 拋出例外 → Success=false + watermark 丟棄（#regression-transform-exception）
/// - EtlPipelineExecutor：MergeKeyColumn 空字串快速失敗（#regression-empty-merge-key）
/// - EtlPipelineExecutor：Single-batch pipeline watermark 正確更新（#regression-single-batch-watermark）
/// </summary>
[TestClass]
public class RegressionTests
{
    private MockEtlSource _source = null!;
    private MockBulkLoader _loader = null!;

    [TestInitialize]
    public void Setup()
    {
        _source = new MockEtlSource();
        _loader = new MockBulkLoader();
    }

    // ─── Watermark: int Identity column ───

    /// <summary>
    /// 回歸 #regression-int-watermark
    /// SQL Server INT 欄位從 DataReader 讀出為 int（非 long）。
    /// UpdateFromBatchMax 必須接受 int 並正確轉換為 long，不應靜默忽略。
    /// </summary>
    [TestMethod]
    public void Watermark_Identity_int_value_is_promoted_to_long()
    {
        var wm = new WatermarkStrategy(EtlWatermarkType.Identity, "Id", null);

        wm.UpdateFromBatchMax((int)42_000);

        var committed = wm.CommitPendingValue();
        committed.Should().NotBeNullOrEmpty("int value should not be silently discarded");

        var stored = JsonSerializer.Deserialize<long>(committed!);
        stored.Should().Be(42_000L, "int 42000 must be stored as long 42000");
    }

    [TestMethod]
    public void Watermark_Identity_int_value_pending_before_commit()
    {
        var wm = new WatermarkStrategy(EtlWatermarkType.Identity, "Id", null);
        wm.UpdateFromBatchMax((int)99);

        // CurrentValue must not change until CommitPendingValue is called
        wm.CurrentValue.Should().BeNull();

        wm.CommitPendingValue();
        wm.CurrentValue.Should().NotBeNullOrEmpty();
    }

    // ─── Watermark: all-null batch ───

    /// <summary>
    /// 回歸 #regression-null-batch-watermark
    /// 當 batch 內 watermark 欄位全為 DBNull，GetMaxValue 回傳 null，
    /// UpdateFromBatchMax 不應被呼叫（pipeline 層有 null 判斷）。
    /// 即使直接呼叫 UpdateFromBatchMax(null!) 也不應更新 pending value。
    ///
    /// 此測試驗證 WatermarkStrategy 自身：unsupported type → 靜默忽略，pending 保持 null。
    /// </summary>
    [TestMethod]
    public void Watermark_Identity_null_value_does_not_set_pending()
    {
        var wm = new WatermarkStrategy(EtlWatermarkType.Identity, "Id", null);

        // DBNull 是「不支援型別」，應靜默忽略
        wm.UpdateFromBatchMax(DBNull.Value);

        wm.CommitPendingValue().Should().BeNull("all-null batch must not advance watermark");
        wm.CurrentValue.Should().BeNull();
    }

    [TestMethod]
    public async Task Pipeline_all_null_watermark_column_does_not_advance_watermark()
    {
        // 全 DBNull 的 watermark 欄位 batch
        var dt = new DataTable();
        dt.Columns.Add("OrderNo", typeof(string));
        dt.Columns.Add("Amount", typeof(decimal));
        dt.Columns.Add("Id", typeof(int));

        for (int i = 0; i < 20; i++)
        {
            var row = dt.NewRow();
            row["OrderNo"] = $"ORD-{i:D4}";
            row["Amount"] = 100m + i;
            row["Id"] = DBNull.Value;
            dt.Rows.Add(row);
        }

        _source.SetData(dt);
        var config = TestHelpers.CreateTestConfig() with { BatchSize = 50_000 };
        var watermark = new WatermarkStrategy(EtlWatermarkType.Identity, "Id", null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue();
        result.NewWatermarkValue.Should().BeNull("all-null batch must not produce a new watermark");
        watermark.CurrentValue.Should().BeNull();
    }

    // ─── BatchSize = 0 fast-fail ───

    /// <summary>
    /// 回歸 #regression-batchsize-zero
    /// BatchSize=0 在 MockEtlSource 中造成無限迴圈（end == offset 永遠不前進）。
    /// EtlPipelineExecutor.ExecuteAsync 必須在進入 Extract 迴圈前驗證並快速失敗。
    /// </summary>
    [TestMethod]
    public async Task Pipeline_BatchSize_zero_returns_failure_with_descriptive_error()
    {
        _source.SetData(TestHelpers.GenerateOrderData(100));
        var config = TestHelpers.CreateTestConfig() with { BatchSize = 0 };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeFalse("BatchSize=0 is invalid and must fail immediately");
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().Contain("BatchSize", "error must mention the invalid parameter");
    }

    [TestMethod]
    public async Task Pipeline_BatchSize_negative_returns_failure_with_descriptive_error()
    {
        _source.SetData(TestHelpers.GenerateOrderData(100));
        var config = TestHelpers.CreateTestConfig() with { BatchSize = -1 };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("BatchSize");
    }

    // ─── TransformFunc exception ───

    /// <summary>
    /// 回歸 #regression-transform-exception
    /// TransformFunc 拋出例外時，pipeline 應捕獲並回傳 Success=false，
    /// 同時丟棄任何已暫存的 watermark（不可寫入 DB）。
    /// </summary>
    [TestMethod]
    public async Task Pipeline_TransformFunc_exception_returns_failure()
    {
        _source.SetData(TestHelpers.GenerateOrderData(100));
        var config = TestHelpers.CreateTestConfig() with
        {
            TransformFunc = _ => throw new InvalidOperationException("intentional transform error")
        };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeFalse("TransformFunc exception must surface as failure");
        result.Aborted.Should().BeFalse("this is an error, not a cancellation");
        result.ErrorMessage.Should().Contain("intentional transform error");
    }

    [TestMethod]
    public async Task Pipeline_TransformFunc_exception_discards_watermark()
    {
        // 設定兩批資料，第一批 transform 成功並更新 pending，第二批拋出例外
        _source.SetData(TestHelpers.GenerateOrderData(200));
        int callCount = 0;
        var config = TestHelpers.CreateTestConfig() with
        {
            BatchSize = 100,
            TransformFunc = dt =>
            {
                callCount++;
                if (callCount == 2)
                    throw new InvalidOperationException("fail on second batch");
                return dt;
            }
        };

        var original = JsonSerializer.Serialize(new DateTime(2026, 1, 1, 0, 0, 0));
        var watermark = new WatermarkStrategy(EtlWatermarkType.Timestamp, "UpdatedAt", original);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeFalse();
        // watermark 應保持原始值（pending 被丟棄）
        watermark.CurrentValue.Should().Be(original, "failed pipeline must not advance watermark");
    }

    // ─── MergeKeyColumn empty string ───

    /// <summary>
    /// 回歸 #regression-empty-merge-key
    /// 空字串 MergeKeyColumn 會讓 DB MERGE 語句語法錯誤。
    /// 應在 ExecuteAsync 入口快速失敗並給出描述性訊息。
    /// </summary>
    [TestMethod]
    public async Task Pipeline_MergeKeyColumn_empty_returns_failure_with_descriptive_error()
    {
        _source.SetData(TestHelpers.GenerateOrderData(10));
        var config = TestHelpers.CreateTestConfig() with { MergeKeyColumn = "" };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeFalse("empty MergeKeyColumn must fail immediately");
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().Contain("MergeKeyColumn", "error must identify the invalid field");
    }

    [TestMethod]
    public async Task Pipeline_MergeKeyColumn_whitespace_returns_failure()
    {
        _source.SetData(TestHelpers.GenerateOrderData(10));
        var config = TestHelpers.CreateTestConfig() with { MergeKeyColumn = "   " };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeFalse("whitespace-only MergeKeyColumn must fail immediately");
        result.ErrorMessage.Should().Contain("MergeKeyColumn");
    }

    // ─── Single-batch pipeline: watermark correct update ───

    /// <summary>
    /// 回歸 #regression-single-batch-watermark
    /// 當 BatchSize 大於資料筆數（single batch），watermark 必須正確從該唯一 batch
    /// 取得最大值並在成功後 commit。
    /// </summary>
    [TestMethod]
    public async Task Pipeline_single_batch_identity_watermark_updates_correctly()
    {
        // 建立有 Id 欄位的資料（int 型）
        var dt = new DataTable();
        dt.Columns.Add("OrderNo", typeof(string));
        dt.Columns.Add("Amount", typeof(decimal));
        dt.Columns.Add("UpdatedAt", typeof(DateTime));
        dt.Columns.Add("Id", typeof(int));

        var baseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 1; i <= 50; i++)
        {
            var row = dt.NewRow();
            row["OrderNo"] = $"ORD-{i:D8}";
            row["Amount"] = 100m + i;
            row["UpdatedAt"] = baseDate.AddSeconds(i);
            row["Id"] = i;
            dt.Rows.Add(row);
        }

        _source.SetData(dt);
        // BatchSize > data count → single batch
        var config = new EtlPipelineConfig
        {
            JobId = Guid.NewGuid(),
            JobName = "SingleBatchTest",
            SourceConnectionString = "Source=test",
            TargetConnectionString = "Target=test",
            QueryTemplate = "SELECT * FROM Orders WHERE @watermark_clause",
            TargetTableName = "SyncedOrders",
            MergeKeyColumn = "OrderNo",
            BatchSize = 10_000,
            StagingTable = TestHelpers.CreateTestStagingSpec()
        };
        var watermark = new WatermarkStrategy(EtlWatermarkType.Identity, "Id", null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue();
        _loader.BatchCount.Should().Be(1, "all 50 rows fit in one batch");
        result.NewWatermarkValue.Should().NotBeNullOrEmpty("watermark must be committed after success");

        var storedId = JsonSerializer.Deserialize<long>(result.NewWatermarkValue!);
        storedId.Should().Be(50L, "max Id in batch is 50");
    }

    [TestMethod]
    public async Task Pipeline_single_batch_timestamp_watermark_updates_correctly()
    {
        var baseDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        _source.SetData(TestHelpers.GenerateOrderData(30, baseDate));

        var config = TestHelpers.CreateTestConfig() with { BatchSize = 10_000 };
        var watermark = new WatermarkStrategy(EtlWatermarkType.Timestamp, "UpdatedAt", null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue();
        _loader.BatchCount.Should().Be(1);
        result.NewWatermarkValue.Should().NotBeNullOrEmpty();

        // 最大 UpdatedAt = baseDate + 29 seconds
        var maxExpected = baseDate.AddSeconds(29);
        var stored = JsonSerializer.Deserialize<DateTime>(result.NewWatermarkValue!);
        stored.Should().BeCloseTo(maxExpected, TimeSpan.FromSeconds(1));
    }

    // ─── TargetCsKey silent failure regression (pipeline layer) ───

    /// <summary>
    /// 回歸 #regression-targetcskey-silent-failure
    /// 修復前：TargetCsKey 查不到時使用 ?? "" 靜默給空連線字串，
    /// 造成後續 DB 操作拋出不明確的錯誤。
    /// 修復後：EtlQuartzJob 拋出 InvalidOperationException 含 key 名稱。
    ///
    /// 此測試在 pipeline 層驗證：config 含有空字串 TargetConnectionString 時，
    /// pipeline 不會自行修正或靜默跳過 loader 呼叫，EnsureStaging 仍會被呼叫
    /// （pipeline 不知道連線字串是否有效，那是上層責任）。
    /// </summary>
    [TestMethod]
    public async Task Pipeline_empty_TargetConnectionString_still_calls_loader()
    {
        _source.SetData(TestHelpers.GenerateOrderData(5));
        var config = TestHelpers.CreateTestConfig() with { TargetConnectionString = "" };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        // Pipeline 本身不擋空連線字串（那是 EtlQuartzJob 的責任）。
        // 驗證 pipeline 確實把呼叫傳給 loader，而非靜默截斷。
        _loader.EnsureStagingCalled.Should().BeTrue(
            "pipeline must forward calls to loader even when TargetConnectionString is empty — " +
            "the caller (EtlQuartzJob) is responsible for ensuring the key exists");
        result.Success.Should().BeTrue("MockBulkLoader accepts any connection string");
    }
}
