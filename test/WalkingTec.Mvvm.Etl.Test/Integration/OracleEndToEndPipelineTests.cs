#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Oracle.ManagedDataAccess.Client;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;
using WalkingTec.Mvvm.Etl.Pipeline.Sources;

namespace WalkingTec.Mvvm.Etl.Test.Integration;

/// <summary>
/// Oracle 端到端 Pipeline 整合測試 — 驗證 Oracle-to-Oracle 完整 ETL 流程。
/// 需要 Docker: docker compose -f test/docker-compose.etl-test.yml up -d
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class OracleEndToEndPipelineTests : IntegrationTestBase
{
    private const string SourceTable = "ETL_E2E_ORA_SOURCE";
    private const string StagingTable = "ETL_E2E_ORA_STAGING";
    private const string TargetTable = "ETL_E2E_ORA_TARGET";

    private static readonly StagingTableSpec StagingSpec = new(
        StagingTable,
        new StagingColumn("ORDERNO", "VARCHAR2(50)"),
        new StagingColumn("AMOUNT", "NUMBER(18,2)"),
        new StagingColumn("UPDATEDAT", "TIMESTAMP")
    );

    [TestInitialize]
    public async Task Setup()
    {
        await DropOracleTableSafeAsync(SourceTable);
        await DropOracleTableSafeAsync(StagingTable);
        await DropOracleTableSafeAsync(TargetTable);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await DropOracleTableSafeAsync(SourceTable);
        await DropOracleTableSafeAsync(StagingTable);
        await DropOracleTableSafeAsync(TargetTable);
    }

    [TestMethod]
    public async Task Full_pipeline_oracle_to_oracle()
    {
        // Arrange — 來源 2000 筆
        await CreateOracleSourceTableAsync(SourceTable, 2000);
        await CreateOracleTargetTableAsync(TargetTable);

        using var source = new OracleSource();
        var loader = new OracleBulkLoader();
        var executor = new EtlPipelineExecutor(source, loader);

        var config = new EtlPipelineConfig
        {
            JobId = Guid.NewGuid(),
            JobName = "Oracle-E2E-FullLoad",
            SourceConnectionString = OracleConnectionString,
            TargetConnectionString = OracleConnectionString,
            QueryTemplate = $"SELECT ORDERNO, AMOUNT, UPDATEDAT FROM {SourceTable}",
            TargetTableName = TargetTable,
            MergeKeyColumn = "ORDERNO",
            BatchSize = 500,
            StagingTable = StagingSpec
        };

        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);

        // Act
        var result = await executor.ExecuteAsync(config, watermark);

        // Assert
        Assert.IsTrue(result.Success, $"Pipeline failed: {result.ErrorMessage}");
        Assert.AreEqual(2000, result.ExtractedRows);
        Assert.AreEqual(2000, result.LoadedRows);

        var targetCount = await CountOracleRowsAsync(TargetTable);
        Assert.AreEqual(2000, targetCount);
    }

    [TestMethod]
    public async Task Incremental_pipeline_oracle_only_loads_new_rows()
    {
        // Arrange — 來源先 500 筆
        await CreateOracleSourceTableAsync(SourceTable, 500);
        await CreateOracleTargetTableAsync(TargetTable);

        // 第一跑
        using var source1 = new OracleSource();
        var loader = new OracleBulkLoader();
        var executor1 = new EtlPipelineExecutor(source1, loader);

        var config = new EtlPipelineConfig
        {
            JobId = Guid.NewGuid(),
            JobName = "Oracle-E2E-Incremental",
            SourceConnectionString = OracleConnectionString,
            TargetConnectionString = OracleConnectionString,
            QueryTemplate = $"SELECT ORDERNO, AMOUNT, UPDATEDAT FROM {SourceTable} WHERE UPDATEDAT > :watermark ORDER BY UPDATEDAT",
            TargetTableName = TargetTable,
            MergeKeyColumn = "ORDERNO",
            BatchSize = 200,
            StagingTable = StagingSpec
        };

        var watermark1 = new WatermarkStrategy(
            EtlWatermarkType.Timestamp, "UPDATEDAT",
            "\"2025-12-31T00:00:00Z\"");

        var result1 = await executor1.ExecuteAsync(config, watermark1);
        Assert.IsTrue(result1.Success, $"First run failed: {result1.ErrorMessage}");
        Assert.AreEqual(500, result1.ExtractedRows);

        // 新增 200 筆
        await InsertOracleRowsAsync(SourceTable, 200, 500);

        // 第二跑
        using var source2 = new OracleSource();
        var executor2 = new EtlPipelineExecutor(source2, loader);
        var watermark2 = new WatermarkStrategy(
            EtlWatermarkType.Timestamp, "UPDATEDAT",
            result1.NewWatermarkValue);

        var result2 = await executor2.ExecuteAsync(config, watermark2);

        // Assert
        Assert.IsTrue(result2.Success, $"Second run failed: {result2.ErrorMessage}");
        Assert.AreEqual(200, result2.ExtractedRows);

        var targetCount = await CountOracleRowsAsync(TargetTable);
        Assert.AreEqual(700, targetCount);
    }

    #region Oracle Helpers

    private static async Task CreateOracleSourceTableAsync(string tableName, int rowCount)
    {
        await DropOracleTableSafeAsync(tableName);
        await using var conn = new OracleConnection(OracleConnectionString);
        await conn.OpenAsync();

        await using var createCmd = conn.CreateCommand();
        createCmd.CommandText = $@"
            CREATE TABLE {tableName} (
                ORDERNO   VARCHAR2(50) NOT NULL,
                AMOUNT    NUMBER(18,2) NOT NULL,
                UPDATEDAT TIMESTAMP NOT NULL,
                CONSTRAINT PK_{tableName} PRIMARY KEY (ORDERNO)
            )";
        await createCmd.ExecuteNonQueryAsync();

        var baseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < rowCount; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                INSERT INTO {tableName} (ORDERNO, AMOUNT, UPDATEDAT)
                VALUES (:no, :amt, :dt)";
            cmd.Parameters.Add(new OracleParameter("no", $"ORD-{i:D8}"));
            cmd.Parameters.Add(new OracleParameter("amt", 100m + (i % 1000)));
            cmd.Parameters.Add(new OracleParameter("dt", baseDate.AddSeconds(i)));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task CreateOracleTargetTableAsync(string tableName)
    {
        await DropOracleTableSafeAsync(tableName);
        await using var conn = new OracleConnection(OracleConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            CREATE TABLE {tableName} (
                ORDERNO   VARCHAR2(50) NOT NULL,
                AMOUNT    NUMBER(18,2) NOT NULL,
                UPDATEDAT TIMESTAMP NOT NULL,
                CONSTRAINT PK_{tableName} PRIMARY KEY (ORDERNO)
            )";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertOracleRowsAsync(string tableName, int count, int startIndex)
    {
        await using var conn = new OracleConnection(OracleConnectionString);
        await conn.OpenAsync();

        var baseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = startIndex; i < startIndex + count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                INSERT INTO {tableName} (ORDERNO, AMOUNT, UPDATEDAT)
                VALUES (:no, :amt, :dt)";
            cmd.Parameters.Add(new OracleParameter("no", $"ORD-{i:D8}"));
            cmd.Parameters.Add(new OracleParameter("amt", 100m + (i % 1000)));
            cmd.Parameters.Add(new OracleParameter("dt", baseDate.AddSeconds(i)));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task DropOracleTableSafeAsync(string tableName)
    {
        await using var conn = new OracleConnection(OracleConnectionString);
        await conn.OpenAsync();

        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = :t";
        checkCmd.Parameters.Add(new OracleParameter("t", tableName.ToUpperInvariant()));
        var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0;

        if (exists)
        {
            await using var dropCmd = conn.CreateCommand();
            dropCmd.CommandText = $"DROP TABLE {tableName} PURGE";
            await dropCmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<int> CountOracleRowsAsync(string tableName)
    {
        await using var conn = new OracleConnection(OracleConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    #endregion
}
