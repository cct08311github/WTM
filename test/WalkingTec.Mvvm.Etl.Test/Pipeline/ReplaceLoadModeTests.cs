#nullable enable
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;
using WalkingTec.Mvvm.Etl.Testing;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// EtlLoadMode.Replace path: dispatcher chooses ReplaceAsync over
/// MergeAsync, ReplaceWhereClause is forwarded, MergeKeyColumn is
/// optional in Replace mode, and the SQL-injection guard rejects
/// dangerous WHERE clauses.
/// </summary>
[TestClass]
public class ReplaceLoadModeTests
{
    [TestMethod]
    public async Task Default_LoadMode_Merge_calls_MergeAsync_not_ReplaceAsync()
    {
        var src = new MockEtlSource(); src.SetData(TestHelpers.GenerateOrderData(50));
        var loader = new MockBulkLoader();
        var executor = new EtlPipelineExecutor(src, loader);

        var result = await executor.ExecuteAsync(
            TestHelpers.CreateTestConfig() with { BatchSize = 100 },
            new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null));

        Assert.IsTrue(result.Success);
        Assert.IsTrue(loader.MergeCalled, "Default LoadMode is Merge.");
        Assert.IsFalse(loader.ReplaceCalled);
    }

    [TestMethod]
    public async Task Replace_LoadMode_calls_ReplaceAsync_with_where_clause()
    {
        var src = new MockEtlSource(); src.SetData(TestHelpers.GenerateOrderData(50));
        var loader = new MockBulkLoader();
        var executor = new EtlPipelineExecutor(src, loader);

        var config = TestHelpers.CreateTestConfig() with
        {
            BatchSize = 100,
            LoadMode = EtlLoadMode.Replace,
            ReplaceWhereClause = "OrderDate >= '2026-01-01'",
        };

        var result = await executor.ExecuteAsync(
            config,
            new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null));

        Assert.IsTrue(result.Success);
        Assert.IsTrue(loader.ReplaceCalled);
        Assert.IsFalse(loader.MergeCalled, "Replace mode must NOT also call Merge.");
        Assert.AreEqual("OrderDate >= '2026-01-01'", loader.ReplaceWhereClauseCaptured);
    }

    [TestMethod]
    public async Task Replace_LoadMode_with_null_where_means_truncate_target()
    {
        // null whereClause → DELETE FROM target (no WHERE) — equivalent
        // to "wipe everything and re-insert from source".
        var src = new MockEtlSource(); src.SetData(TestHelpers.GenerateOrderData(50));
        var loader = new MockBulkLoader();
        var executor = new EtlPipelineExecutor(src, loader);

        var config = TestHelpers.CreateTestConfig() with
        {
            BatchSize = 100,
            LoadMode = EtlLoadMode.Replace,
            ReplaceWhereClause = null,
        };

        var result = await executor.ExecuteAsync(
            config, new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null));

        Assert.IsTrue(result.Success);
        Assert.IsNull(loader.ReplaceWhereClauseCaptured);
    }

    [TestMethod]
    public async Task Replace_LoadMode_does_not_require_MergeKeyColumn()
    {
        // Merge mode validates MergeKeyColumn != null; Replace mode
        // shouldn't because it doesn't use it. Confirm Replace works
        // with empty MergeKeyColumn.
        var src = new MockEtlSource(); src.SetData(TestHelpers.GenerateOrderData(50));
        var loader = new MockBulkLoader();
        var executor = new EtlPipelineExecutor(src, loader);

        var config = TestHelpers.CreateTestConfig() with
        {
            BatchSize = 100,
            LoadMode = EtlLoadMode.Replace,
            MergeKeyColumn = "", // intentionally empty
        };

        var result = await executor.ExecuteAsync(
            config, new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null));

        Assert.IsTrue(result.Success, $"Got error '{result.ErrorMessage}'.");
    }

    [TestMethod]
    public async Task Merge_LoadMode_still_requires_MergeKeyColumn()
    {
        // Pre-10.5 contract preserved: Merge with empty key fails fast.
        var src = new MockEtlSource(); src.SetData(TestHelpers.GenerateOrderData(50));
        var loader = new MockBulkLoader();
        var executor = new EtlPipelineExecutor(src, loader);

        var config = TestHelpers.CreateTestConfig() with
        {
            BatchSize = 100,
            LoadMode = EtlLoadMode.Merge,
            MergeKeyColumn = "",
        };

        var result = await executor.ExecuteAsync(
            config, new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.ErrorMessage ?? "", "MergeKeyColumn");
    }

    // ── SQL-injection guard ────────────────────────────────────────────

    [TestMethod]
    public void IsSafeWhereClause_allows_normal_predicates()
    {
        Assert.IsTrue(MssqlBulkLoader.IsSafeWhereClause("OrderDate >= '2026-01-01'"));
        Assert.IsTrue(MssqlBulkLoader.IsSafeWhereClause("Region = 'North' AND Amount > 100"));
        Assert.IsTrue(MssqlBulkLoader.IsSafeWhereClause("Code IN ('A', 'B', 'C')"));
        Assert.IsTrue(MssqlBulkLoader.IsSafeWhereClause(null), "null = delete entire table is allowed.");
        Assert.IsTrue(MssqlBulkLoader.IsSafeWhereClause(""));
    }

    [TestMethod]
    public void IsSafeWhereClause_rejects_statement_separators_and_comments()
    {
        Assert.IsFalse(MssqlBulkLoader.IsSafeWhereClause("1=1; DROP TABLE Users"));
        Assert.IsFalse(MssqlBulkLoader.IsSafeWhereClause("1=1 --"));
        Assert.IsFalse(MssqlBulkLoader.IsSafeWhereClause("1=1 /* sneaky */"));
    }

    [TestMethod]
    public void IsSafeWhereClause_rejects_system_proc_prefixes()
    {
        Assert.IsFalse(MssqlBulkLoader.IsSafeWhereClause("xp_cmdshell('whoami') = ''"));
        Assert.IsFalse(MssqlBulkLoader.IsSafeWhereClause("sp_executesql N'evil'"));
        Assert.IsFalse(MssqlBulkLoader.IsSafeWhereClause("XP_CMDSHELL('') = ''"),
            "Prefix check must be case-insensitive.");
    }
}
