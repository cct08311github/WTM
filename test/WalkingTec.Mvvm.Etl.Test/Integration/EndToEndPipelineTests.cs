#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;
using WalkingTec.Mvvm.Etl.Pipeline.Sources;

namespace WalkingTec.Mvvm.Etl.Test.Integration;

/// <summary>
/// 端到端 Pipeline 整合測試 — 驗證完整 Extract → Load → Merge 流程。
/// MSSQL-to-MSSQL（來源與目標都在同一個 MSSQL 實例）。
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class EndToEndPipelineTests : IntegrationTestBase
{
    private const string SourceTable = "_etl_e2e_source";
    private const string StagingTable = "_etl_e2e_staging";
    private const string TargetTable = "_etl_e2e_target";

    private static readonly StagingTableSpec StagingSpec = new(
        StagingTable,
        new StagingColumn("OrderNo", "NVARCHAR(50)"),
        new StagingColumn("Amount", "DECIMAL(18,2)"),
        new StagingColumn("UpdatedAt", "DATETIME2")
    );

    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        if (!IsMssqlAvailable())
            Assert.Inconclusive("MSSQL not available — skipping integration tests. Run: docker compose -f test/docker-compose.etl-test.yml up -d");
        await EnsureMssqlDatabaseAsync();
    }

    [TestInitialize]
    public async Task Setup()
    {
        await DropMssqlTableAsync(SourceTable);
        await DropMssqlTableAsync(StagingTable);
        await DropMssqlTableAsync(TargetTable);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await DropMssqlTableAsync(SourceTable);
        await DropMssqlTableAsync(StagingTable);
        await DropMssqlTableAsync(TargetTable);
    }

    [TestMethod]
    public async Task Full_pipeline_mssql_to_mssql()
    {
        // Arrange — 來源 5000 筆
        await CreateMssqlSourceTableAsync(SourceTable, 5000);
        await CreateMssqlTargetTableAsync(TargetTable);

        using var source = new MssqlSource();
        var loader = new MssqlBulkLoader();
        var executor = new EtlPipelineExecutor(source, loader);

        var config = new EtlPipelineConfig
        {
            JobId = Guid.NewGuid(),
            JobName = "E2E-FullLoad",
            SourceConnectionString = MssqlConnectionString,
            TargetConnectionString = MssqlConnectionString,
            QueryTemplate = $"SELECT [OrderNo], [Amount], [UpdatedAt] FROM [{SourceTable}]",
            TargetTableName = TargetTable,
            MergeKeyColumn = "OrderNo",
            BatchSize = 1000,
            StagingTable = StagingSpec
        };

        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);

        // Act
        var result = await executor.ExecuteAsync(config, watermark);

        // Assert
        Assert.IsTrue(result.Success, $"Pipeline failed: {result.ErrorMessage}");
        Assert.AreEqual(5000, result.ExtractedRows);
        Assert.AreEqual(5000, result.LoadedRows);
        Assert.IsTrue(result.ElapsedMs > 0);

        var targetCount = await CountMssqlRowsAsync(TargetTable);
        Assert.AreEqual(5000, targetCount);
    }

    [TestMethod]
    public async Task Incremental_pipeline_only_loads_new_rows()
    {
        // Arrange — 來源先 1000 筆
        await CreateMssqlSourceTableAsync(SourceTable, 1000);
        await CreateMssqlTargetTableAsync(TargetTable);

        // 第一跑：全量（watermark 從 null 開始 = WHERE 1=1 等效）
        using var source1 = new MssqlSource();
        var loader = new MssqlBulkLoader();
        var executor1 = new EtlPipelineExecutor(source1, loader);

        var config = new EtlPipelineConfig
        {
            JobId = Guid.NewGuid(),
            JobName = "E2E-Incremental",
            SourceConnectionString = MssqlConnectionString,
            TargetConnectionString = MssqlConnectionString,
            QueryTemplate = $"SELECT [OrderNo], [Amount], [UpdatedAt] FROM [{SourceTable}] WHERE [UpdatedAt] > @watermark ORDER BY [UpdatedAt]",
            TargetTableName = TargetTable,
            MergeKeyColumn = "OrderNo",
            BatchSize = 500,
            StagingTable = StagingSpec
        };

        // 首跑：watermark = null → GetParameterValue() = null → 不加 @watermark 參數
        // 但我們的 QueryTemplate 要求 @watermark，所以用 Timestamp 模式 + 初始值
        var watermark1 = new WatermarkStrategy(
            EtlWatermarkType.Timestamp, "UpdatedAt",
            "\"2025-12-31T00:00:00Z\"");

        var result1 = await executor1.ExecuteAsync(config, watermark1);
        Assert.IsTrue(result1.Success, $"First run failed: {result1.ErrorMessage}");
        Assert.AreEqual(1000, result1.ExtractedRows);

        // 新增 500 筆到來源（index 1000~1499）
        await InsertMoreRowsAsync(SourceTable, 500, 1000);

        // 第二跑：用第一跑的 watermark
        using var source2 = new MssqlSource();
        var executor2 = new EtlPipelineExecutor(source2, loader);
        var watermark2 = new WatermarkStrategy(
            EtlWatermarkType.Timestamp, "UpdatedAt",
            result1.NewWatermarkValue);

        var result2 = await executor2.ExecuteAsync(config, watermark2);

        // Assert — 第二跑只載入 500 筆新資料
        Assert.IsTrue(result2.Success, $"Second run failed: {result2.ErrorMessage}");
        Assert.AreEqual(500, result2.ExtractedRows);

        var targetCount = await CountMssqlRowsAsync(TargetTable);
        Assert.AreEqual(1500, targetCount);
    }

    [TestMethod]
    public async Task Failed_pipeline_does_not_corrupt_target()
    {
        // Arrange — 來源 500 筆，target 預先放 200 筆
        await CreateMssqlSourceTableAsync(SourceTable, 500);
        await CreateMssqlSourceTableAsync(TargetTable, 200); // 用 source helper 建含 PK 的表

        using var source = new MssqlSource();
        // 用一個會在 Merge 階段失敗的 config — staging 與 target 的 merge key 不存在
        var loader = new MssqlBulkLoader();
        var executor = new EtlPipelineExecutor(source, loader);

        // 故意設錯 MergeKeyColumn 讓 MERGE 失敗
        var config = new EtlPipelineConfig
        {
            JobId = Guid.NewGuid(),
            JobName = "E2E-FailTest",
            SourceConnectionString = MssqlConnectionString,
            TargetConnectionString = MssqlConnectionString,
            QueryTemplate = $"SELECT [OrderNo], [Amount], [UpdatedAt] FROM [{SourceTable}]",
            TargetTableName = TargetTable,
            MergeKeyColumn = "NonExistentColumn",
            BatchSize = 1000,
            StagingTable = StagingSpec
        };

        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);

        // Act
        var result = await executor.ExecuteAsync(config, watermark);

        // Assert — pipeline 失敗，但 target 資料不受影響
        Assert.IsFalse(result.Success);
        Assert.IsFalse(string.IsNullOrEmpty(result.ErrorMessage));

        var targetCount = await CountMssqlRowsAsync(TargetTable);
        Assert.AreEqual(200, targetCount, "Target table should not be corrupted by failed pipeline");
    }

    #region Helpers

    private static async Task InsertMoreRowsAsync(string tableName, int count, int startIndex)
    {
        await using var conn = new SqlConnection(MssqlConnectionString);
        await conn.OpenAsync();

        var baseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = startIndex; i < startIndex + count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                INSERT INTO [{tableName}] ([OrderNo], [Amount], [UpdatedAt])
                VALUES (@no, @amt, @dt)";
            cmd.Parameters.AddWithValue("@no", $"ORD-{i:D8}");
            cmd.Parameters.AddWithValue("@amt", 100m + (i % 1000));
            cmd.Parameters.AddWithValue("@dt", baseDate.AddSeconds(i));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    #endregion
}
