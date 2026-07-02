#nullable enable
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// Tests for #537: <see cref="MySqlBulkLoader"/> must default to a non-zero
/// <see cref="MySqlBulkLoader.InternalBatchSize"/> so that the shipped
/// <c>EtlPipelineConfig.BatchSize</c> default of 50,000 rows does not produce a single
/// multi-row INSERT statement large enough to exceed MySQL's <c>max_allowed_packet</c>.
/// <para>
/// Previously <c>InternalBatchSize</c> defaulted to 0, which <see cref="MySqlBulkLoader.BulkLoadAsync"/>
/// treated as "insert the whole batch in one statement". These tests verify the new
/// default (1000) and the pure chunking math extracted into
/// <see cref="MySqlBulkLoader.ResolveSubBatchSize"/> / <see cref="MySqlBulkLoader.ComputeChunkCount"/>,
/// which drives <c>BulkLoadAsync</c>'s Skip/Take loop. No live MySQL connection is required —
/// these are pure-logic seams that mirror the actual chunking behaviour without hitting the DB.
/// </para>
/// </summary>
[TestClass]
public class MySqlBulkLoaderChunkingTests
{
    // ═══════════════════════════════════════════════════════════════
    // Default ctor behaviour (#537 fix)
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    public void Default_constructor_sets_InternalBatchSize_to_1000()
    {
        var loader = new MySqlBulkLoader();

        loader.InternalBatchSize.Should().Be(1000,
            "#537: a non-zero default keeps each INSERT well within MySQL's max_allowed_packet " +
            "when the framework-default 50,000-row EtlPipelineConfig.BatchSize is used");
    }

    [TestMethod]
    public void Parameterless_construction_compiles_and_uses_new_default()
    {
        // Existing callers using `new MySqlBulkLoader()` (e.g. EtlSourceFactory) must
        // still compile unchanged and now receive the chunked-by-default behaviour.
        MySqlBulkLoader loader = new();

        loader.InternalBatchSize.Should().Be(1000);
        loader.TimeoutSeconds.Should().Be(300, "TimeoutSeconds default must be unaffected by this fix");
    }

    [TestMethod]
    public void Default_ctor_TimeoutSeconds_unchanged_by_this_fix()
    {
        var loader = new MySqlBulkLoader();

        loader.TimeoutSeconds.Should().Be(300);
    }

    // ═══════════════════════════════════════════════════════════════
    // InternalBatchSize remains configurable (opt-out / opt-in preserved)
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    public void Explicit_zero_still_opts_back_into_single_statement_behaviour()
    {
        // Callers that relied on pre-fix single-INSERT behaviour can still get it explicitly.
        var loader = new MySqlBulkLoader(internalBatchSize: 0);

        loader.InternalBatchSize.Should().Be(0);
    }

    [TestMethod]
    public void Custom_positive_InternalBatchSize_is_honoured()
    {
        var loader = new MySqlBulkLoader(internalBatchSize: 250);

        loader.InternalBatchSize.Should().Be(250);
    }

    [TestMethod]
    public void TimeoutSeconds_and_InternalBatchSize_are_independently_configurable()
    {
        var loader = new MySqlBulkLoader(timeoutSeconds: 60, internalBatchSize: 5000);

        loader.TimeoutSeconds.Should().Be(60);
        loader.InternalBatchSize.Should().Be(5000);
    }

    // ═══════════════════════════════════════════════════════════════
    // Pure chunking math (seam mirroring BulkLoadAsync's Skip/Take loop)
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    public void ResolveSubBatchSize_uses_InternalBatchSize_when_positive()
    {
        var subBatchSize = MySqlBulkLoader.ResolveSubBatchSize(internalBatchSize: 1000, rowCount: 50_000);

        subBatchSize.Should().Be(1000);
    }

    [TestMethod]
    public void ResolveSubBatchSize_falls_back_to_full_row_count_when_zero()
    {
        // Legacy/opt-out behaviour: 0 means "whole batch in one statement".
        var subBatchSize = MySqlBulkLoader.ResolveSubBatchSize(internalBatchSize: 0, rowCount: 50_000);

        subBatchSize.Should().Be(50_000);
    }

    [TestMethod]
    public void ComputeChunkCount_default_50000_row_batch_splits_into_50_statements()
    {
        // The exact scenario from #537: default BatchSize=50_000, default InternalBatchSize=1000.
        var subBatchSize = MySqlBulkLoader.ResolveSubBatchSize(internalBatchSize: 1000, rowCount: 50_000);
        var chunkCount = MySqlBulkLoader.ComputeChunkCount(rowCount: 50_000, subBatchSize: subBatchSize);

        chunkCount.Should().Be(50,
            "50,000 rows chunked at 1000 rows/statement must issue exactly 50 INSERT statements, " +
            "each well within max_allowed_packet, instead of one 50,000-row statement");
    }

    [TestMethod]
    public void ComputeChunkCount_with_legacy_zero_override_issues_a_single_statement()
    {
        var subBatchSize = MySqlBulkLoader.ResolveSubBatchSize(internalBatchSize: 0, rowCount: 50_000);
        var chunkCount = MySqlBulkLoader.ComputeChunkCount(rowCount: 50_000, subBatchSize: subBatchSize);

        chunkCount.Should().Be(1,
            "explicit opt-out (internalBatchSize=0) must preserve the pre-#537 single-statement behaviour");
    }

    [TestMethod]
    public void ComputeChunkCount_rounds_up_for_a_remainder_row()
    {
        // 1001 rows at 1000/chunk → 2 statements (1000 + 1), not truncated to 1.
        var chunkCount = MySqlBulkLoader.ComputeChunkCount(rowCount: 1001, subBatchSize: 1000);

        chunkCount.Should().Be(2, "a partial trailing chunk must still be issued as its own statement");
    }

    [TestMethod]
    public void ComputeChunkCount_exact_multiple_does_not_add_an_empty_trailing_chunk()
    {
        var chunkCount = MySqlBulkLoader.ComputeChunkCount(rowCount: 3000, subBatchSize: 1000);

        chunkCount.Should().Be(3);
    }

    [TestMethod]
    public void ComputeChunkCount_zero_rows_issues_no_statements()
    {
        var chunkCount = MySqlBulkLoader.ComputeChunkCount(rowCount: 0, subBatchSize: 1000);

        chunkCount.Should().Be(0);
    }

    [TestMethod]
    public void ComputeChunkCount_single_row_batch_issues_one_statement()
    {
        var chunkCount = MySqlBulkLoader.ComputeChunkCount(rowCount: 1, subBatchSize: 1000);

        chunkCount.Should().Be(1);
    }

    // ═══════════════════════════════════════════════════════════════
    // EtlSourceFactory wiring: `new MySqlBulkLoader()` picks up the new default
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    public void Factory_style_construction_yields_chunked_default_for_framework_default_BatchSize()
    {
        // Mirrors EtlSourceFactory.CreateBulkLoader's `DBTypeEnum.MySql => new MySqlBulkLoader()`
        // and EtlPipelineConfig.BatchSize's default of 50_000 — confirms the two defaults
        // now compose safely without any caller-side configuration.
        var loader = new MySqlBulkLoader();
        var subBatchSize = MySqlBulkLoader.ResolveSubBatchSize(loader.InternalBatchSize, rowCount: 50_000);
        var chunkCount = MySqlBulkLoader.ComputeChunkCount(50_000, subBatchSize);

        chunkCount.Should().BeGreaterThan(1,
            "with framework defaults alone (no caller configuration), the batch must now be " +
            "chunked into multiple statements rather than one 50,000-row INSERT");
    }
}
