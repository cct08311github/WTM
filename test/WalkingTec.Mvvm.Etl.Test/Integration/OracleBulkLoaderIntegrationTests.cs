#nullable enable
using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Oracle.ManagedDataAccess.Client;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;

namespace WalkingTec.Mvvm.Etl.Test.Integration;

/// <summary>
/// Oracle BulkLoader 整合測試 — 驗證 Array Binding + MERGE INTO 在真實 Oracle 上的行為。
/// 需要 Docker: docker compose -f test/docker-compose.etl-test.yml up -d
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class OracleBulkLoaderIntegrationTests : IntegrationTestBase
{
    private const string StagingTable = "ETL_TEST_STG_ORDERS";
    private const string TargetTable = "ETL_TEST_TGT_ORDERS";

    private readonly OracleBulkLoader _loader = new();

    private static readonly StagingTableSpec StagingSpec = new(
        StagingTable,
        new StagingColumn("ORDERNO", "VARCHAR2(50)"),
        new StagingColumn("AMOUNT", "NUMBER(18,2)"),
        new StagingColumn("UPDATEDAT", "TIMESTAMP")
    );

    [TestInitialize]
    public async Task Setup()
    {
        await DropOracleTableAsync(StagingTable);
        await DropOracleTableAsync(TargetTable);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await DropOracleTableAsync(StagingTable);
        await DropOracleTableAsync(TargetTable);
    }

    [TestMethod]
    public async Task BulkLoad_inserts_rows_to_staging()
    {
        // Arrange
        await _loader.EnsureStagingTableAsync(OracleConnectionString, StagingTable, StagingSpec);
        var data = GenerateOracleTestData(1000);

        // Act
        await _loader.BulkLoadAsync(OracleConnectionString, StagingTable, data);

        // Assert
        var count = await CountOracleRowsAsync(StagingTable);
        Assert.AreEqual(1000, count);
    }

    [TestMethod]
    public async Task Merge_upserts_to_target()
    {
        // Arrange
        await _loader.EnsureStagingTableAsync(OracleConnectionString, StagingTable, StagingSpec);
        await CreateOracleTargetTableAsync(TargetTable);

        // 先在 target 放 300 筆
        var initialData = GenerateOracleTestData(300);
        // 用 staging 做中轉
        await _loader.BulkLoadAsync(OracleConnectionString, StagingTable, initialData);
        await _loader.MergeAsync(OracleConnectionString, StagingTable, TargetTable, "ORDERNO");
        await _loader.TruncateStagingAsync(OracleConnectionString, StagingTable);

        // staging 放 500 筆（100~599，與 target 重疊 100~299）
        var stagingData = GenerateOracleTestData(500, startIndex: 100);
        await _loader.BulkLoadAsync(OracleConnectionString, StagingTable, stagingData);

        // Act
        await _loader.MergeAsync(OracleConnectionString, StagingTable, TargetTable, "ORDERNO");

        // Assert — 0~599 = 600 筆
        var count = await CountOracleRowsAsync(TargetTable);
        Assert.AreEqual(600, count);
    }

    [TestMethod]
    public async Task Merge_is_idempotent()
    {
        // Arrange
        await _loader.EnsureStagingTableAsync(OracleConnectionString, StagingTable, StagingSpec);
        await CreateOracleTargetTableAsync(TargetTable);

        var data = GenerateOracleTestData(200);
        await _loader.BulkLoadAsync(OracleConnectionString, StagingTable, data);

        // Act — merge 兩次
        await _loader.MergeAsync(OracleConnectionString, StagingTable, TargetTable, "ORDERNO");
        await _loader.MergeAsync(OracleConnectionString, StagingTable, TargetTable, "ORDERNO");

        // Assert
        var count = await CountOracleRowsAsync(TargetTable);
        Assert.AreEqual(200, count);
    }

    [TestMethod]
    public async Task TruncateStaging_clears_all()
    {
        // Arrange
        await _loader.EnsureStagingTableAsync(OracleConnectionString, StagingTable, StagingSpec);
        await _loader.BulkLoadAsync(OracleConnectionString, StagingTable, GenerateOracleTestData(500));

        // Act
        await _loader.TruncateStagingAsync(OracleConnectionString, StagingTable);

        // Assert
        var count = await CountOracleRowsAsync(StagingTable);
        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public async Task EnsureStagingTable_creates_if_not_exists()
    {
        // Act
        await _loader.EnsureStagingTableAsync(OracleConnectionString, StagingTable, StagingSpec);

        // Assert
        var exists = await OracleTableExistsAsync(StagingTable);
        Assert.IsTrue(exists);
    }

    [TestMethod]
    public async Task EnsureStagingTable_noop_if_exists()
    {
        // Arrange
        await _loader.EnsureStagingTableAsync(OracleConnectionString, StagingTable, StagingSpec);

        // Act — 再呼叫一次
        await _loader.EnsureStagingTableAsync(OracleConnectionString, StagingTable, StagingSpec);

        // Assert
        var exists = await OracleTableExistsAsync(StagingTable);
        Assert.IsTrue(exists);
    }

    #region Oracle Helpers

    private static DataTable GenerateOracleTestData(int rowCount, DateTime? baseDate = null, int startIndex = 0)
    {
        var dt = new DataTable();
        // Oracle 慣用大寫欄名
        dt.Columns.Add("ORDERNO", typeof(string));
        dt.Columns.Add("AMOUNT", typeof(decimal));
        dt.Columns.Add("UPDATEDAT", typeof(DateTime));

        var start = baseDate ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (int i = startIndex; i < startIndex + rowCount; i++)
        {
            var row = dt.NewRow();
            row["ORDERNO"] = $"ORD-{i:D8}";
            row["AMOUNT"] = 100m + (i % 1000);
            row["UPDATEDAT"] = start.AddSeconds(i);
            dt.Rows.Add(row);
        }

        return dt;
    }

    private static async Task CreateOracleTargetTableAsync(string tableName)
    {
        await DropOracleTableAsync(tableName);
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

    private static async Task DropOracleTableAsync(string tableName)
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

    private static async Task<bool> OracleTableExistsAsync(string tableName)
    {
        await using var conn = new OracleConnection(OracleConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = :t";
        cmd.Parameters.Add(new OracleParameter("t", tableName.ToUpperInvariant()));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }

    #endregion
}
