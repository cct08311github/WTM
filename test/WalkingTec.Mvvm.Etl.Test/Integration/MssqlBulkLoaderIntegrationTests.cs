#nullable enable
using System.Data;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;

namespace WalkingTec.Mvvm.Etl.Test.Integration;

/// <summary>
/// MSSQL BulkLoader 整合測試 — 驗證 SqlBulkCopy + MERGE INTO 在真實 MSSQL 上的行為。
/// 需要 Docker: docker compose -f test/docker-compose.etl-test.yml up -d
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class MssqlBulkLoaderIntegrationTests : IntegrationTestBase
{
    private const string StagingTable = "_etl_test_stg_orders";
    private const string TargetTable = "_etl_test_target_orders";

    private readonly MssqlBulkLoader _loader = new();

    private static readonly StagingTableSpec StagingSpec = new(
        StagingTable,
        new StagingColumn("OrderNo", "NVARCHAR(50)"),
        new StagingColumn("Amount", "DECIMAL(18,2)"),
        new StagingColumn("UpdatedAt", "DATETIME2")
    );

    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        await EnsureMssqlDatabaseAsync();
    }

    [TestInitialize]
    public async Task Setup()
    {
        await DropMssqlTableAsync(StagingTable);
        await DropMssqlTableAsync(TargetTable);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await DropMssqlTableAsync(StagingTable);
        await DropMssqlTableAsync(TargetTable);
    }

    [TestMethod]
    public async Task BulkLoad_inserts_rows_to_staging()
    {
        // Arrange
        await _loader.EnsureStagingTableAsync(MssqlConnectionString, StagingTable, StagingSpec);
        var data = GenerateTestData(1000);

        // Act
        await _loader.BulkLoadAsync(MssqlConnectionString, StagingTable, data);

        // Assert
        var count = await CountMssqlRowsAsync(StagingTable);
        Assert.AreEqual(1000, count);
    }

    [TestMethod]
    public async Task Merge_upserts_to_target()
    {
        // Arrange — staging 有 500 筆，target 有 300 筆（其中 200 筆與 staging 重疊）
        await _loader.EnsureStagingTableAsync(MssqlConnectionString, StagingTable, StagingSpec);
        await CreateMssqlTargetTableAsync(TargetTable);

        // target: ORD-00000000 ~ ORD-00000299
        await CreateMssqlSourceTableAsync(TargetTable + "_tmp", 300);
        // 用 INSERT...SELECT 搬到 target（因為 CreateMssqlSourceTableAsync 直接建到指定表）
        // 改用直接建 target 並插入
        await DropMssqlTableAsync(TargetTable);
        await CreateMssqlSourceTableAsync(TargetTable, 300);

        // staging: ORD-00000100 ~ ORD-00000599（與 target 重疊 100~299）
        var stagingData = GenerateTestData(500, startIndex: 100);
        await _loader.BulkLoadAsync(MssqlConnectionString, StagingTable, stagingData);

        // Act
        await _loader.MergeAsync(MssqlConnectionString, StagingTable, TargetTable, "OrderNo");

        // Assert — target 應有 600 筆（0~99 原有 + 100~599 merge）
        var count = await CountMssqlRowsAsync(TargetTable);
        Assert.AreEqual(600, count);
    }

    [TestMethod]
    public async Task Merge_is_idempotent()
    {
        // Arrange
        await _loader.EnsureStagingTableAsync(MssqlConnectionString, StagingTable, StagingSpec);
        await CreateMssqlTargetTableAsync(TargetTable);

        var data = GenerateTestData(200);
        await _loader.BulkLoadAsync(MssqlConnectionString, StagingTable, data);

        // Act — merge 兩次
        await _loader.MergeAsync(MssqlConnectionString, StagingTable, TargetTable, "OrderNo");
        await _loader.MergeAsync(MssqlConnectionString, StagingTable, TargetTable, "OrderNo");

        // Assert — 筆數不變
        var count = await CountMssqlRowsAsync(TargetTable);
        Assert.AreEqual(200, count);
    }

    [TestMethod]
    public async Task TruncateStaging_clears_all()
    {
        // Arrange
        await _loader.EnsureStagingTableAsync(MssqlConnectionString, StagingTable, StagingSpec);
        await _loader.BulkLoadAsync(MssqlConnectionString, StagingTable, GenerateTestData(500));

        // Act
        await _loader.TruncateStagingAsync(MssqlConnectionString, StagingTable);

        // Assert
        var count = await CountMssqlRowsAsync(StagingTable);
        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public async Task EnsureStagingTable_creates_if_not_exists()
    {
        // Act
        await _loader.EnsureStagingTableAsync(MssqlConnectionString, StagingTable, StagingSpec);

        // Assert
        var exists = await MssqlTableExistsAsync(StagingTable);
        Assert.IsTrue(exists);
    }

    [TestMethod]
    public async Task EnsureStagingTable_noop_if_exists()
    {
        // Arrange — 建立一次
        await _loader.EnsureStagingTableAsync(MssqlConnectionString, StagingTable, StagingSpec);

        // Act — 再呼叫一次不應報錯
        await _loader.EnsureStagingTableAsync(MssqlConnectionString, StagingTable, StagingSpec);

        // Assert
        var exists = await MssqlTableExistsAsync(StagingTable);
        Assert.IsTrue(exists);
    }
}
