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

    // ─── #680 (P3): word-boundary fix for the sp_/xp_ prefix check ────────
    // The previous plain Contains("sp_")/Contains("xp_") check false-positived
    // on legitimate column names that merely contain "sp_"/"xp_" mid-word
    // (e.g. "resp_code"). The guard must still reject a real proc-prefix
    // invocation appearing anywhere at a word boundary within the clause.

    [TestMethod]
    public void IsSafeWhereClause_allows_columns_with_sp_or_xp_substring_not_at_word_boundary()
    {
        Assert.IsTrue(MssqlBulkLoader.IsSafeWhereClause("resp_code = 'X'"),
            "'resp_code' merely contains 'sp_' mid-word and must not be rejected.");
        Assert.IsTrue(MssqlBulkLoader.IsSafeWhereClause("expat_code = 'Y'"));
        Assert.IsTrue(MssqlBulkLoader.IsSafeWhereClause("TransportCost > 100 AND resp_code IS NOT NULL"));
    }

    [TestMethod]
    public void IsSafeWhereClause_still_rejects_proc_prefix_at_word_boundary_mid_clause()
    {
        // A genuine sp_/xp_ invocation preceded by a space (a real word
        // boundary) inside a longer clause must still be rejected — the fix
        // only stops matching MID-WORD substrings, not real occurrences
        // anywhere in the string.
        Assert.IsFalse(MssqlBulkLoader.IsSafeWhereClause("1=1 AND xp_cmdshell('x')=''"));
        Assert.IsFalse(MssqlBulkLoader.IsSafeWhereClause("1=1 AND sp_executesql(N'x')"));
    }
}
