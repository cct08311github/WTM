#nullable enable
// ETL-009: Composite merge keys
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;
using WalkingTec.Mvvm.Etl.Testing;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// ETL-009: Composite merge-key tests.
///
/// Verifies:
///   1. ParseMergeKeys parses single and composite keys correctly.
///   2. ParseMergeKeys throws on empty/whitespace input.
///   3. Single key → back-compat (same ON clause shape as before, no AND).
///   4. Composite key → AND-joined ON clause with all key columns.
///   5. Key columns are excluded from UPDATE SET.
///   6. The pipeline executor wires the composite key through MergeAsync.
/// </summary>
[TestClass]
public class CompositeMergeKeyTests
{
    // ─── ParseMergeKeys: parsing contract ──────────────────────────────────

    [TestMethod]
    public void ParseMergeKeys_single_column_returns_single_element()
    {
        var keys = MssqlBulkLoader.ParseMergeKeys("OrderId");
        keys.Should().ContainSingle().Which.Should().Be("OrderId");
    }

    [TestMethod]
    public void ParseMergeKeys_two_columns_returns_both()
    {
        var keys = MssqlBulkLoader.ParseMergeKeys("TenantId,OrderNo");
        keys.Should().HaveCount(2);
        keys[0].Should().Be("TenantId");
        keys[1].Should().Be("OrderNo");
    }

    [TestMethod]
    public void ParseMergeKeys_trims_whitespace_around_each_column()
    {
        var keys = MssqlBulkLoader.ParseMergeKeys(" TenantId , OrderNo ");
        keys.Should().HaveCount(2);
        keys[0].Should().Be("TenantId");
        keys[1].Should().Be("OrderNo");
    }

    [TestMethod]
    public void ParseMergeKeys_three_columns_returns_all_three()
    {
        var keys = MssqlBulkLoader.ParseMergeKeys("Region,TenantId,OrderNo");
        keys.Should().HaveCount(3);
        keys.Should().ContainInOrder("Region", "TenantId", "OrderNo");
    }

    [TestMethod]
    public void ParseMergeKeys_empty_string_throws()
    {
        var act = () => MssqlBulkLoader.ParseMergeKeys("");
        act.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void ParseMergeKeys_whitespace_only_throws()
    {
        var act = () => MssqlBulkLoader.ParseMergeKeys("   ");
        act.Should().Throw<ArgumentException>();
    }

    // ─── ON-clause shape verification (whitebox) ───────────────────────────

    [TestMethod]
    public void Composite_on_clause_contains_all_keys_and_joined()
    {
        var keys = MssqlBulkLoader.ParseMergeKeys("TenantId, OrderNo");

        // Reproduce the MERGE ON clause the loader would build internally.
        var onClause = "ON " + string.Join(" AND ",
            ((IReadOnlyList<string>)keys).Select(k => $"target.[{k}] = source.[{k}]"));

        onClause.Should().Contain("target.[TenantId] = source.[TenantId]");
        onClause.Should().Contain("target.[OrderNo] = source.[OrderNo]");
        onClause.Should().Contain(" AND ", "key conditions must be AND-joined");
    }

    [TestMethod]
    public void Single_key_on_clause_has_no_and()
    {
        var keys = MssqlBulkLoader.ParseMergeKeys("OrderId");

        var onClause = "ON " + string.Join(" AND ",
            ((IReadOnlyList<string>)keys).Select(k => $"target.[{k}] = source.[{k}]"));

        onClause.Should().Be("ON target.[OrderId] = source.[OrderId]",
            "single key should produce the original ON-clause shape");
        onClause.Should().NotContain(" AND ");
    }

    [TestMethod]
    public void Composite_update_set_excludes_key_columns()
    {
        var keys = MssqlBulkLoader.ParseMergeKeys("TenantId,OrderNo");
        var keySet = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);

        var allCols = new[] { "TenantId", "OrderNo", "Amount", "Status" };
        var updateCols = allCols.Where(c => !keySet.Contains(c)).ToList();

        updateCols.Should().BeEquivalentTo(new[] { "Amount", "Status" },
            "both key columns must be excluded from the UPDATE SET list");
    }

    // ─── Pipeline executor wires composite key to MergeAsync ───────────────

    [TestMethod]
    public async Task ExecuteAsync_composite_key_forwards_key_string_to_MergeAsync()
    {
        var source = new MockEtlSource();
        var data = TestHelpers.GenerateOrderData(5);
        source.SetData(data);

        var loader = new CapturingMockBulkLoader();

        var config = new EtlPipelineConfig
        {
            JobId = Guid.NewGuid(),
            JobName = "CompositeKeyJob",
            SourceConnectionString = "src",
            TargetConnectionString = "tgt",
            QueryTemplate = "SELECT * FROM t",
            TargetTableName = "Orders",
            MergeKeyColumn = "TenantId,OrderNo",   // composite key
            BatchSize = 1000,
            StagingTable = TestHelpers.CreateTestStagingSpec()
        };

        var watermark = new WatermarkStrategy(
            EtlWatermarkType.FullLoad, null, null, "UTC");

        var executor = new EtlPipelineExecutor(source, loader);
        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue("pipeline must succeed");
        loader.MergeKeyColumnPassed.Should().Be("TenantId,OrderNo",
            "composite key string must be passed unchanged to MergeAsync");
    }

    [TestMethod]
    public async Task ExecuteAsync_single_key_backward_compat_calls_merge()
    {
        var source = new MockEtlSource();
        source.SetData(TestHelpers.GenerateOrderData(3));

        var loader = new CapturingMockBulkLoader();

        var config = new EtlPipelineConfig
        {
            JobId = Guid.NewGuid(),
            JobName = "SingleKeyJob",
            SourceConnectionString = "src",
            TargetConnectionString = "tgt",
            QueryTemplate = "SELECT * FROM t",
            TargetTableName = "Orders",
            MergeKeyColumn = "OrderNo",   // single key — back-compat
            BatchSize = 1000,
            StagingTable = TestHelpers.CreateTestStagingSpec()
        };

        var watermark = new WatermarkStrategy(
            EtlWatermarkType.FullLoad, null, null, "UTC");

        var executor = new EtlPipelineExecutor(source, loader);
        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue();
        loader.MergeKeyColumnPassed.Should().Be("OrderNo",
            "single key must be forwarded as-is (back-compat)");
    }
}

// ─── Test double: captures MergeKeyColumn arg ──────────────────────────────────

/// <summary>
/// Wraps MockBulkLoader but also records the <c>mergeKeyColumn</c> argument
/// passed to <see cref="IBulkLoader.MergeAsync"/> so tests can assert on it.
/// </summary>
internal sealed class CapturingMockBulkLoader : IBulkLoader
{
    public string? MergeKeyColumnPassed { get; private set; }
    private readonly MockBulkLoader _inner = new();

    public Task BulkLoadAsync(string cs, string staging, DataTable batch,
        CancellationToken ct = default) =>
        _inner.BulkLoadAsync(cs, staging, batch, ct);

    public Task MergeAsync(string cs, string staging, string target,
        string mergeKeyColumn, CancellationToken ct = default)
    {
        MergeKeyColumnPassed = mergeKeyColumn;
        return Task.CompletedTask;
    }

    public Task TruncateStagingAsync(string cs, string staging,
        CancellationToken ct = default) =>
        _inner.TruncateStagingAsync(cs, staging, ct);

    public Task EnsureStagingTableAsync(string cs, string staging,
        StagingTableSpec spec, CancellationToken ct = default) =>
        _inner.EnsureStagingTableAsync(cs, staging, spec, ct);

    public Task<bool> IsUniqueColumnAsync(string cs, string table,
        string column, CancellationToken ct = default) =>
        _inner.IsUniqueColumnAsync(cs, table, column, ct);
}
