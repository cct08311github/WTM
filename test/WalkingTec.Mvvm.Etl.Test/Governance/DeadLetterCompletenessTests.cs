#nullable enable
// #673: dead-letter completeness tests.
// Uses SQLite shared in-memory (not EF InMemory) so ExecuteUpdateAsync /
// ExecuteDeleteAsync work for the run-scoped dedupe path.
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Governance;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Testing;

namespace WalkingTec.Mvvm.Etl.Test.Governance;

/// <summary>
/// #673 — dead-letter completeness:
/// (1) Abort captures the offending row before throwing.
/// (2) Batch load / transform failures capture a documented batch-level marker.
/// (3) Dead-letter entries flush once per run (not per-batch) and a rerun after a
///     failed run does not duplicate the failed attempt's rows.
/// (4) Retention pruning is covered separately in <c>DeadLetterRetentionTests</c>.
/// </summary>
[TestClass]
public class DeadLetterCompletenessTests : IDisposable
{
    private GovernanceTestDataContext _dc = null!;
    private SqliteConnection _keepAlive = null!;

    [TestInitialize]
    public void Setup()
    {
        var dbName = $"DeadLetterCompleteness_{Guid.NewGuid():N}";
        _keepAlive = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
        _keepAlive.Open();
        _dc = new GovernanceTestDataContext(
            $"DataSource={dbName}?mode=memory&cache=shared", DBTypeEnum.SQLite);
        _dc.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dc?.Dispose();
        _keepAlive?.Dispose();
    }

    // ────────────────────────────────────────────────────────────────────────
    // #673(c) — Abort path capture
    // ────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Abort_with_EnableDeadLetter_captures_offending_row_before_throwing()
    {
        var dt = new DataTable();
        dt.Columns.Add("OrderNo", typeof(string));
        dt.Columns.Add("Age", typeof(int));
        var good = dt.NewRow(); good["OrderNo"] = "ORD-1"; good["Age"] = 30;
        var bad = dt.NewRow(); bad["OrderNo"] = "ORD-2"; bad["Age"] = 999; // out of [0,120]
        dt.Rows.Add(good);
        dt.Rows.Add(bad);

        var source = new MockEtlSource(); source.SetData(dt);
        var loader = new MockBulkLoader();
        var governance = new DbEtlGovernanceStore(_dc);
        var executor = new EtlPipelineExecutor(source, loader, governanceStore: governance);

        var jobId = Guid.NewGuid();
        var config = new EtlPipelineConfig
        {
            JobId = jobId,
            JobName = "AbortCapture",
            SourceConnectionString = "src=test",
            TargetConnectionString = "tgt=test",
            QueryTemplate = "SELECT * FROM t",
            TargetTableName = "T",
            MergeKeyColumn = "OrderNo",
            BatchSize = 50_000,
            StagingTable = new StagingTableSpec("stg_t",
                new StagingColumn("OrderNo", "NVARCHAR(50)"),
                new StagingColumn("Age", "INT")),
            QualityRules = new List<EtlQualityRule>
            {
                new() { Column = "Age", RuleType = EtlQualityRuleType.Range, Min = 0, Max = 120 }
            },
            QualityRuleAction = EtlQualityRuleAction.Abort,
            EnableDeadLetter = true,
        };

        var result = await executor.ExecuteAsync(config, FullLoadWatermark());

        result.Success.Should().BeFalse("Abort still fails the run — capture is additive diagnostics only");
        result.Aborted.Should().BeFalse("this is a quality-rule Abort, not an operator cancellation");

        var dlRows = await governance.QueryDeadLetterAsync(jobId);
        dlRows.Should().HaveCount(1, "the offending row must be captured before the Abort exception propagates");
        dlRows[0].Source.Should().Be(EtlDeadLetterSource.QualityRuleAbort);
        dlRows[0].Reason.Should().Contain("Age");
        dlRows[0].RowJson.Should().Contain("ORD-2");
        dlRows[0].RunId.Should().Be(result.RunId);
    }

    [TestMethod]
    public async Task Abort_without_EnableDeadLetter_captures_nothing()
    {
        var dt = new DataTable();
        dt.Columns.Add("Age", typeof(int));
        var bad = dt.NewRow(); bad["Age"] = 999;
        dt.Rows.Add(bad);

        var source = new MockEtlSource(); source.SetData(dt);
        var loader = new MockBulkLoader();
        var governance = new DbEtlGovernanceStore(_dc);
        var executor = new EtlPipelineExecutor(source, loader, governanceStore: governance);

        var jobId = Guid.NewGuid();
        var config = new EtlPipelineConfig
        {
            JobId = jobId,
            JobName = "AbortNoCapture",
            SourceConnectionString = "src=test",
            TargetConnectionString = "tgt=test",
            QueryTemplate = "SELECT * FROM t",
            TargetTableName = "T",
            MergeKeyColumn = "Age",
            BatchSize = 50_000,
            StagingTable = new StagingTableSpec("stg_t", new StagingColumn("Age", "INT")),
            QualityRules = new List<EtlQualityRule>
            {
                new() { Column = "Age", RuleType = EtlQualityRuleType.Range, Min = 0, Max = 120 }
            },
            QualityRuleAction = EtlQualityRuleAction.Abort,
            EnableDeadLetter = false,
        };

        var result = await executor.ExecuteAsync(config, FullLoadWatermark());

        result.Success.Should().BeFalse();
        var dlRows = await governance.QueryDeadLetterAsync(jobId);
        dlRows.Should().BeEmpty("dead-letter is disabled — capture must be a strict no-op");
    }

    // ────────────────────────────────────────────────────────────────────────
    // #673(a)/(b) — batch load / transform failure capture
    // ────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task LoadFailure_with_EnableDeadLetter_captures_batch_marker_with_sanitized_error()
    {
        var source = new MockEtlSource();
        source.SetData(TestData());
        var loader = new MockBulkLoader { FailOnBatch = 1 };
        var governance = new DbEtlGovernanceStore(_dc);
        var executor = new EtlPipelineExecutor(source, loader, governanceStore: governance);

        var jobId = Guid.NewGuid();
        var config = TestConfig(jobId) with { EnableDeadLetter = true };

        var result = await executor.ExecuteAsync(config, FullLoadWatermark());

        result.Success.Should().BeFalse("bulk load fails on the only batch");
        result.ErrorMessage.Should().Contain("simulated failure", "rollback/rethrow semantics are unchanged");

        var dlRows = await governance.QueryDeadLetterAsync(jobId);
        dlRows.Should().HaveCount(1, "per-row attribution isn't available for bulk-load failures");
        dlRows[0].Source.Should().Be(EtlDeadLetterSource.LoadError);
        dlRows[0].RowJson.Should().Contain("batch-level-capture");
        dlRows[0].RowJson.Should().Contain("batchRowCount");
        dlRows[0].Reason.Should().Contain("simulated failure");
    }

    [TestMethod]
    public async Task LoadFailure_without_EnableDeadLetter_preserves_exact_rethrow_semantics()
    {
        var source = new MockEtlSource();
        source.SetData(TestData());
        var loader = new MockBulkLoader { FailOnBatch = 1 };
        var governance = new DbEtlGovernanceStore(_dc);
        var executor = new EtlPipelineExecutor(source, loader, governanceStore: governance);

        var jobId = Guid.NewGuid();
        var config = TestConfig(jobId) with { EnableDeadLetter = false };

        var result = await executor.ExecuteAsync(config, FullLoadWatermark());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("simulated failure");
        (await governance.QueryDeadLetterAsync(jobId)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task TransformFailure_with_EnableDeadLetter_captures_batch_marker()
    {
        var source = new MockEtlSource();
        source.SetData(TestData());
        var loader = new MockBulkLoader();
        var governance = new DbEtlGovernanceStore(_dc);
        var executor = new EtlPipelineExecutor(source, loader, governanceStore: governance);

        var jobId = Guid.NewGuid();
        var config = TestConfig(jobId) with
        {
            EnableDeadLetter = true,
            TransformFunc = _ => throw new InvalidOperationException("simulated transform bug"),
        };

        var result = await executor.ExecuteAsync(config, FullLoadWatermark());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("simulated transform bug");

        var dlRows = await governance.QueryDeadLetterAsync(jobId);
        dlRows.Should().HaveCount(1);
        dlRows[0].Source.Should().Be(EtlDeadLetterSource.TransformError);
        dlRows[0].RowJson.Should().Contain("batch-level-capture");
        dlRows[0].Reason.Should().Contain("simulated transform bug");
    }

    // ────────────────────────────────────────────────────────────────────────
    // #673(d) — flush-once-per-run + run-scoped dedupe on rerun
    // ────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task DeadLetter_flushed_once_per_run_not_per_batch()
    {
        // 6 rows / batch size 2 → 3 batches; every 2nd row violates NotNull.
        var dt = new DataTable();
        dt.Columns.Add("OrderNo", typeof(string));
        for (int i = 0; i < 6; i++)
        {
            var r = dt.NewRow();
            r["OrderNo"] = i % 2 == 1 ? (object)DBNull.Value : $"ORD-{i}";
            dt.Rows.Add(r);
        }

        var source = new MockEtlSource(); source.SetData(dt);
        var loader = new MockBulkLoader();
        var inner = new DbEtlGovernanceStore(_dc);
        var spy = new SpyGovernanceStore(inner);
        var executor = new EtlPipelineExecutor(source, loader, governanceStore: spy);

        var jobId = Guid.NewGuid();
        var config = new EtlPipelineConfig
        {
            JobId = jobId,
            JobName = "FlushOnce",
            SourceConnectionString = "src=test",
            TargetConnectionString = "tgt=test",
            QueryTemplate = "SELECT * FROM t",
            TargetTableName = "T",
            MergeKeyColumn = "OrderNo",
            BatchSize = 2,
            StagingTable = new StagingTableSpec("stg_t", new StagingColumn("OrderNo", "NVARCHAR(50)")),
            QualityRules = new List<EtlQualityRule>
            {
                new() { Column = "OrderNo", RuleType = EtlQualityRuleType.NotNull }
            },
            QualityRuleAction = EtlQualityRuleAction.Drop,
            EnableDeadLetter = true,
        };

        var result = await executor.ExecuteAsync(config, FullLoadWatermark());

        result.Success.Should().BeTrue("Drop path never fails the run");
        result.QualityFailedRows.Should().Be(3, "one violation per batch, 3 batches");
        spy.AddDeadLetterCallCount.Should().Be(1,
            "entries must be buffered for the whole run and flushed exactly once, not per-batch");

        var dlRows = await inner.QueryDeadLetterAsync(jobId);
        dlRows.Should().HaveCount(3);
        dlRows.Should().OnlyContain(r => r.RunSucceeded == true);
    }

    [TestMethod]
    public async Task Rerun_after_failed_run_does_not_duplicate_dead_letter_rows()
    {
        var jobId = Guid.NewGuid();
        var governance = new DbEtlGovernanceStore(_dc);

        // Run 1: bulk load fails on the only batch — the whole run fails, but the
        // NotNull violation was already captured before the load ran.
        var source1 = new MockEtlSource(); source1.SetData(RerunTestData());
        var loader1 = new MockBulkLoader { FailOnBatch = 1 };
        var executor1 = new EtlPipelineExecutor(source1, loader1, governanceStore: governance);
        var result1 = await executor1.ExecuteAsync(RerunConfig(jobId), FullLoadWatermark());

        result1.Success.Should().BeFalse("bulk load fails on the only batch");

        var afterRun1 = await governance.QueryDeadLetterAsync(jobId);
        afterRun1.Should().HaveCount(2,
            "quality-rule Drop capture + load-error batch marker, both from the failed run");
        afterRun1.Should().OnlyContain(r => r.RunSucceeded == false);

        // Run 2: fresh loader (no injected failure), same source data — load succeeds
        // this time (watermark was discarded on failure, so the same window is
        // reprocessed from scratch).
        var source2 = new MockEtlSource(); source2.SetData(RerunTestData());
        var loader2 = new MockBulkLoader();
        var executor2 = new EtlPipelineExecutor(source2, loader2, governanceStore: governance);
        var result2 = await executor2.ExecuteAsync(RerunConfig(jobId), FullLoadWatermark());

        result2.Success.Should().BeTrue("load succeeds this time");

        // Run 2's start-of-run cleanup must remove run 1's (RunSucceeded=false) rows
        // before writing its own — the final set is ONLY run 2's Drop-path capture
        // (1 row), not the 2 (failed) + 1 (succeeded) = 3 that naive per-run
        // accumulation would leave behind.
        var afterRun2 = await governance.QueryDeadLetterAsync(jobId);
        afterRun2.Should().HaveCount(1,
            "run 2's start-of-run cleanup must remove run 1's failed-run rows before writing its own");
        afterRun2[0].RunId.Should().Be(result2.RunId);
        afterRun2[0].RunSucceeded.Should().BeTrue("a successfully-completed run's captures are permanent history");
    }

    [TestMethod]
    public async Task Rerun_after_two_consecutive_failures_still_keeps_only_latest_failed_runs_rows()
    {
        // Guards against unbounded accumulation across repeated failures: each new
        // run's start-of-run cleanup removes the PRIOR failed run's rows, so at most
        // one failed attempt's worth of diagnostics ever accumulates.
        var jobId = Guid.NewGuid();
        var governance = new DbEtlGovernanceStore(_dc);

        var source1 = new MockEtlSource(); source1.SetData(RerunTestData());
        var loader1 = new MockBulkLoader { FailOnBatch = 1 };
        var executor1 = new EtlPipelineExecutor(source1, loader1, governanceStore: governance);
        var result1 = await executor1.ExecuteAsync(RerunConfig(jobId), FullLoadWatermark());
        result1.Success.Should().BeFalse();

        var source2 = new MockEtlSource(); source2.SetData(RerunTestData());
        var loader2 = new MockBulkLoader { FailOnBatch = 1 }; // fails again
        var executor2 = new EtlPipelineExecutor(source2, loader2, governanceStore: governance);
        var result2 = await executor2.ExecuteAsync(RerunConfig(jobId), FullLoadWatermark());
        result2.Success.Should().BeFalse();

        var afterTwoFailures = await governance.QueryDeadLetterAsync(jobId);
        afterTwoFailures.Should().HaveCount(2,
            "run 2's start-of-run cleanup removed run 1's rows before run 2 wrote its own — " +
            "never 4 (2+2) from naive accumulation across repeated failures");
        afterTwoFailures.Should().OnlyContain(r => r.RunId == result2.RunId);
    }

    // ────────────────────────────────────────────────────────────────────────
    // #700 — configurable buffered-flush cap (EtlOptions.MaxDeadLetterRowsPerRun)
    // ────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task DeadLetter_truncates_at_the_configured_cap_not_the_hardcoded_default()
    {
        // 5 rows / batch size 1 → 5 batches, every row violates NotNull → 5 raw
        // violations. The executor is constructed with a cap of 2 — far below
        // EtlPipelineExecutor.DefaultMaxDeadLetterRowsPerRun (10000, mirroring
        // EtlOptions.MaxDeadLetterRowsPerRun's default) — to prove the *configured*
        // value gates capture, not the hardcoded default.
        var dt = new DataTable();
        dt.Columns.Add("OrderNo", typeof(string));
        for (int i = 0; i < 5; i++)
        {
            var r = dt.NewRow();
            r["OrderNo"] = DBNull.Value; // every row violates NotNull
            dt.Rows.Add(r);
        }

        var source = new MockEtlSource(); source.SetData(dt);
        var loader = new MockBulkLoader();
        var governance = new DbEtlGovernanceStore(_dc);
        const int cap = 2;
        var executor = new EtlPipelineExecutor(
            source, loader, governanceStore: governance, maxDeadLetterRowsPerRun: cap);

        var jobId = Guid.NewGuid();
        var config = new EtlPipelineConfig
        {
            JobId = jobId,
            JobName = "TruncationCap",
            SourceConnectionString = "src=test",
            TargetConnectionString = "tgt=test",
            QueryTemplate = "SELECT * FROM t",
            TargetTableName = "T",
            MergeKeyColumn = "OrderNo",
            BatchSize = 1,
            StagingTable = new StagingTableSpec("stg_t", new StagingColumn("OrderNo", "NVARCHAR(50)")),
            QualityRules = new List<EtlQualityRule>
            {
                new() { Column = "OrderNo", RuleType = EtlQualityRuleType.NotNull }
            },
            QualityRuleAction = EtlQualityRuleAction.Drop,
            EnableDeadLetter = true,
        };

        var result = await executor.ExecuteAsync(config, FullLoadWatermark());

        result.Success.Should().BeTrue("Drop path never fails the run");
        result.QualityFailedRows.Should().Be(5, "all 5 rows violate NotNull regardless of the dead-letter cap");

        var dlRows = await governance.QueryDeadLetterAsync(jobId);
        dlRows.Should().HaveCount(cap + 1,
            $"only the first {cap} real violations are captured before the configured cap trips, " +
            "plus exactly one truncation-marker entry — proving the constructor value (not the " +
            "10000 default) governs truncation");

        // All rows are flushed together in one SaveChangesAsync call and share the same
        // QuarantinedAt timestamp, so relative order among them is not guaranteed —
        // find the marker by content instead of assuming array position.
        var markerRows = dlRows.Where(r => r.RowJson.Contains("dead-letter-capture-truncated")).ToList();
        markerRows.Should().ContainSingle("exactly one truncation marker is appended once the cap trips");
        markerRows[0].Reason.Should().Contain($"capped at {cap} entries");

        var realRows = dlRows.Except(markerRows).ToList();
        realRows.Should().HaveCount(cap, $"only the first {cap} real violations are buffered before truncation");
        realRows.Should().OnlyContain(r => r.Source == EtlDeadLetterSource.QualityRule);
    }

    // ────────────────────────────────────────────────────────────────────────
    // #700(a)/(b) — Periodic dead-letter flush: crash-durability tradeoff (Issue #700)
    // ────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Periodic_mode_flushes_buffer_before_run_end_so_an_interrupted_run_does_not_lose_everything()
    {
        // 6 rows / batch size 1 → 6 batches, every row violates NotNull → 1 dead-letter
        // entry per batch. flushThreshold = 2 → a partial flush fires once 2 entries
        // have accumulated (after batch 2's violation is captured). The run is
        // interrupted (token cancelled from MockBulkLoader.OnBatchLoaded, the same
        // pattern CancellationTests.cs uses) right after batch 3's BulkLoad completes,
        // but BEFORE batch 3's own violation reaches the threshold.
        //
        // This is the closest deterministic in-process approximation of a hard process
        // crash: whatever already reached the DB via a COMPLETED partial flush
        // survives; whatever was still only in memory does not.
        // OperationCanceledException (not a thrown-from-inside exception) is used
        // deliberately — EtlPipelineExecutor.ExecuteAsync's own success/failure catch
        // blocks always get a chance to run their own end-of-run flush first, but the
        // OperationCanceledException path is the one branch that, by #673 design,
        // never flushes the remaining in-memory buffer — the correct stand-in for "the
        // run never reached ANY of its own completion/failure flush logic".
        var dt = SixRowsAllNullOrderNo();
        var source = new MockEtlSource(); source.SetData(dt);
        var loader = new MockBulkLoader();
        var inner = new DbEtlGovernanceStore(_dc);
        var spy = new SpyGovernanceStore(inner);
        var cts = new CancellationTokenSource();
        loader.OnBatchLoaded += (_, _) =>
        {
            if (loader.BatchCount == 3) { cts.Cancel(); }
        };
        var executor = new EtlPipelineExecutor(
            source, loader, governanceStore: spy,
            deadLetterFlushMode: EtlDeadLetterFlushMode.Periodic,
            deadLetterFlushThreshold: 2);

        var jobId = Guid.NewGuid();
        var result = await executor.ExecuteAsync(PeriodicCrashConfig(jobId), FullLoadWatermark(), cts.Token);

        result.Aborted.Should().BeTrue("the run was interrupted before it could reach batch 4");
        spy.AddDeadLetterCallCount.Should().Be(1,
            "only ONE partial flush (triggered after batch 2 crossed the threshold) had a " +
            "chance to run before the interruption — the run never reached its own " +
            "end-of-run flush logic");

        var dlRows = await inner.QueryDeadLetterAsync(jobId);
        dlRows.Should().HaveCount(2,
            "batch 1 and batch 2's violations were already flushed to the DB before the " +
            "interruption; batch 3's violation was still only in memory and is lost — " +
            "bounded by the configured threshold, not the whole run");
        dlRows.Should().OnlyContain(r => r.RunSucceeded == false,
            "the run never completed successfully, so MarkDeadLetterRunSucceededAsync never ran");
    }

    [TestMethod]
    public async Task OncePerRun_default_loses_all_dead_letter_rows_for_the_same_interrupted_run()
    {
        // Same interruption scenario as the Periodic test above, but constructed
        // WITHOUT deadLetterFlushMode/deadLetterFlushThreshold args — proving the
        // DEFAULT executor behaviour is byte-for-byte unchanged from pre-#700.
        // Demonstrates the exact tradeoff Issue #700 exists to document: OncePerRun
        // never writes anything until the run's own end-of-run flush logic runs, and
        // interruption (cancellation) skips that logic entirely — so an interrupted
        // run loses ALL of its buffered diagnostics, not just the un-flushed tail.
        var dt = SixRowsAllNullOrderNo();
        var source = new MockEtlSource(); source.SetData(dt);
        var loader = new MockBulkLoader();
        var inner = new DbEtlGovernanceStore(_dc);
        var spy = new SpyGovernanceStore(inner);
        var cts = new CancellationTokenSource();
        loader.OnBatchLoaded += (_, _) =>
        {
            if (loader.BatchCount == 3) { cts.Cancel(); }
        };
        var executor = new EtlPipelineExecutor(source, loader, governanceStore: spy);

        var jobId = Guid.NewGuid();
        var result = await executor.ExecuteAsync(PeriodicCrashConfig(jobId), FullLoadWatermark(), cts.Token);

        result.Aborted.Should().BeTrue();
        spy.AddDeadLetterCallCount.Should().Be(0,
            "OncePerRun never flushes until the run's own end-of-run logic — which the " +
            "interruption skips entirely");
        (await inner.QueryDeadLetterAsync(jobId)).Should().BeEmpty(
            "the tradeoff #700 documents: a hard interruption in the default OncePerRun " +
            "mode loses ALL of the run's buffered diagnostics, not just the tail");
    }

    [TestMethod]
    public async Task Rerun_after_periodic_flush_interruption_still_dedupes_no_duplicate_rows()
    {
        // Continuation of the interruption scenario: run 1 (Periodic mode) is
        // interrupted and leaves 2 partially-flushed rows behind (RunSucceeded=false).
        // Run 2 is a normal, uninterrupted rerun of the SAME job — its start-of-run
        // ClearDeadLetterFromFailedRunsAsync must remove those 2 stale rows before
        // writing its own, exactly as it already does for a fully-buffered OncePerRun
        // failure (see Rerun_after_failed_run_does_not_duplicate_dead_letter_rows
        // above). This is what makes Periodic partial-flush safe: the existing
        // RunSucceeded/RunId cleanup doesn't care HOW MANY separate flush calls wrote
        // the stale rows, only that they belong to a run that never succeeded.
        var jobId = Guid.NewGuid();
        var governance = new DbEtlGovernanceStore(_dc);

        var source1 = new MockEtlSource(); source1.SetData(SixRowsAllNullOrderNo());
        var loader1 = new MockBulkLoader();
        var cts1 = new CancellationTokenSource();
        loader1.OnBatchLoaded += (_, _) =>
        {
            if (loader1.BatchCount == 3) { cts1.Cancel(); }
        };
        var executor1 = new EtlPipelineExecutor(
            source1, loader1, governanceStore: governance,
            deadLetterFlushMode: EtlDeadLetterFlushMode.Periodic,
            deadLetterFlushThreshold: 2);
        var result1 = await executor1.ExecuteAsync(PeriodicCrashConfig(jobId), FullLoadWatermark(), cts1.Token);
        result1.Aborted.Should().BeTrue();

        var afterRun1 = await governance.QueryDeadLetterAsync(jobId);
        afterRun1.Should().HaveCount(2, "run 1's partial flush persisted 2 rows before being interrupted");
        afterRun1.Should().OnlyContain(r => r.RunSucceeded == false);

        // Run 2: fresh source/loader, no interruption, same job — start-of-run cleanup
        // must remove run 1's stale rows before run 2 writes its own.
        var source2 = new MockEtlSource(); source2.SetData(RerunTestData());
        var loader2 = new MockBulkLoader();
        var executor2 = new EtlPipelineExecutor(
            source2, loader2, governanceStore: governance,
            deadLetterFlushMode: EtlDeadLetterFlushMode.Periodic,
            deadLetterFlushThreshold: 2);
        var result2 = await executor2.ExecuteAsync(RerunConfig(jobId), FullLoadWatermark());

        result2.Success.Should().BeTrue("load succeeds this time, no interruption");

        var afterRun2 = await governance.QueryDeadLetterAsync(jobId);
        afterRun2.Should().HaveCount(1,
            "run 2's start-of-run cleanup removed run 1's 2 stale (partially-flushed, " +
            "RunSucceeded=false) rows before writing its own single Drop-path capture — " +
            "never 3 (2 stale + 1 new)");
        afterRun2[0].RunId.Should().Be(result2.RunId);
        afterRun2[0].RunSucceeded.Should().BeTrue("a successfully-completed run's captures are permanent history");
    }

    [TestMethod]
    public void EtlOptions_DeadLetterFlushMode_defaults_to_OncePerRun_and_threshold_to_500()
    {
        var options = new EtlOptions();

        options.DeadLetterFlushMode.Should().Be(EtlDeadLetterFlushMode.OncePerRun,
            "the default must preserve #673's exact flush-once-per-run behaviour — " +
            "crash-durability is opt-in, never silently on for existing deployments");
        options.DeadLetterFlushThreshold.Should().Be(500);
    }

    [TestMethod]
    public async Task Periodic_mode_with_no_interruption_still_flushes_once_per_run_worth_of_data_and_marks_succeeded()
    {
        // Sanity check that Periodic mode's mid-run partial flushes don't change the
        // FINAL outcome for a run that completes normally — same row count and
        // RunSucceeded=true end-state as OncePerRun, just delivered via more than one
        // AddDeadLetterRowsAsync call.
        var dt = SixRowsAllNullOrderNo();
        var source = new MockEtlSource(); source.SetData(dt);
        var loader = new MockBulkLoader();
        var inner = new DbEtlGovernanceStore(_dc);
        var spy = new SpyGovernanceStore(inner);
        var executor = new EtlPipelineExecutor(
            source, loader, governanceStore: spy,
            deadLetterFlushMode: EtlDeadLetterFlushMode.Periodic,
            deadLetterFlushThreshold: 2);

        var jobId = Guid.NewGuid();
        var result = await executor.ExecuteAsync(PeriodicCrashConfig(jobId), FullLoadWatermark());

        result.Success.Should().BeTrue("Drop path never fails the run");
        spy.AddDeadLetterCallCount.Should().BeGreaterThan(1,
            "6 violations at threshold 2 must trigger multiple partial flushes plus the final one");

        var dlRows = await inner.QueryDeadLetterAsync(jobId);
        dlRows.Should().HaveCount(6, "all 6 violations across the whole run, regardless of how many flush calls wrote them");
        dlRows.Should().OnlyContain(r => r.RunSucceeded == true,
            "MarkDeadLetterRunSucceededAsync flips ALL rows for this RunId, not just the final flush's");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static DataTable TestData()
    {
        var dt = new DataTable();
        dt.Columns.Add("OrderNo", typeof(string));
        dt.Columns.Add("Amount", typeof(decimal));
        var r1 = dt.NewRow(); r1["OrderNo"] = "ORD-1"; r1["Amount"] = 100m;
        var r2 = dt.NewRow(); r2["OrderNo"] = "ORD-2"; r2["Amount"] = 200m;
        dt.Rows.Add(r1);
        dt.Rows.Add(r2);
        return dt;
    }

    /// <summary>6 rows, single "OrderNo" column, every value DBNull — used by the
    /// #700 periodic-flush tests to produce exactly one NotNull dead-letter violation
    /// per batch at BatchSize = 1.</summary>
    private static DataTable SixRowsAllNullOrderNo()
    {
        var dt = new DataTable();
        dt.Columns.Add("OrderNo", typeof(string));
        for (int i = 0; i < 6; i++)
        {
            var r = dt.NewRow();
            r["OrderNo"] = DBNull.Value;
            dt.Rows.Add(r);
        }
        return dt;
    }

    /// <summary>Config for the #700 periodic-flush tests: BatchSize = 1 (one row per
    /// batch, matching <see cref="SixRowsAllNullOrderNo"/>) and a NotNull/Drop quality
    /// rule on OrderNo so every batch produces exactly one dead-letter entry without
    /// ever failing the run.</summary>
    private static EtlPipelineConfig PeriodicCrashConfig(Guid jobId) => new()
    {
        JobId = jobId,
        JobName = "PeriodicCrash",
        SourceConnectionString = "src=test",
        TargetConnectionString = "tgt=test",
        QueryTemplate = "SELECT * FROM t",
        TargetTableName = "T",
        MergeKeyColumn = "OrderNo",
        BatchSize = 1,
        StagingTable = new StagingTableSpec("stg_t", new StagingColumn("OrderNo", "NVARCHAR(50)")),
        QualityRules = new List<EtlQualityRule>
        {
            new() { Column = "OrderNo", RuleType = EtlQualityRuleType.NotNull }
        },
        QualityRuleAction = EtlQualityRuleAction.Drop,
        EnableDeadLetter = true,
    };

    private static EtlPipelineConfig TestConfig(Guid jobId) => new()
    {
        JobId = jobId,
        JobName = "DeadLetterCompleteness",
        SourceConnectionString = "src=test",
        TargetConnectionString = "tgt=test",
        QueryTemplate = "SELECT * FROM t",
        TargetTableName = "T",
        MergeKeyColumn = "OrderNo",
        BatchSize = 50_000,
        StagingTable = new StagingTableSpec("stg_t",
            new StagingColumn("OrderNo", "NVARCHAR(50)"),
            new StagingColumn("Amount", "DECIMAL(18,2)")),
    };

    private static DataTable RerunTestData()
    {
        var dt = new DataTable();
        dt.Columns.Add("OrderNo", typeof(string));
        dt.Columns.Add("Amount", typeof(decimal));
        var good = dt.NewRow(); good["OrderNo"] = "ORD-1"; good["Amount"] = 100m;
        var bad = dt.NewRow(); bad["OrderNo"] = DBNull.Value; bad["Amount"] = 50m; // violates NotNull
        dt.Rows.Add(good);
        dt.Rows.Add(bad);
        return dt;
    }

    private static EtlPipelineConfig RerunConfig(Guid jobId) => new()
    {
        JobId = jobId,
        JobName = "Rerun-Dedup",
        SourceConnectionString = "src=test",
        TargetConnectionString = "tgt=test",
        QueryTemplate = "SELECT * FROM t",
        TargetTableName = "T",
        MergeKeyColumn = "OrderNo",
        BatchSize = 50_000,
        StagingTable = new StagingTableSpec("stg_t",
            new StagingColumn("OrderNo", "NVARCHAR(50)"),
            new StagingColumn("Amount", "DECIMAL(18,2)")),
        QualityRules = new List<EtlQualityRule>
        {
            new() { Column = "OrderNo", RuleType = EtlQualityRuleType.NotNull }
        },
        QualityRuleAction = EtlQualityRuleAction.Drop,
        EnableDeadLetter = true,
    };

    private static WatermarkStrategy FullLoadWatermark()
        => new(EtlWatermarkType.FullLoad, null, null);

    /// <summary>Counts <see cref="IEtlGovernanceStore.AddDeadLetterRowsAsync"/> invocations, delegating everything to <paramref name="inner"/>.</summary>
    private sealed class SpyGovernanceStore : IEtlGovernanceStore
    {
        private readonly IEtlGovernanceStore _inner;
        public int AddDeadLetterCallCount { get; private set; }

        public SpyGovernanceStore(IEtlGovernanceStore inner) => _inner = inner;

        public Task AddDeadLetterRowsAsync(
            Guid jobId, Guid runId, IEnumerable<EtlDeadLetterEntry> entries, string? tenantCode,
            CancellationToken cancellationToken = default)
        {
            AddDeadLetterCallCount++;
            return _inner.AddDeadLetterRowsAsync(jobId, runId, entries, tenantCode, cancellationToken);
        }

        public Task AddLineageRecordAsync(EtlLineageRecord record, CancellationToken cancellationToken = default)
            => _inner.AddLineageRecordAsync(record, cancellationToken);

        public Task<IReadOnlyList<EtlDeadLetterRow>> QueryDeadLetterAsync(
            Guid jobId, CancellationToken cancellationToken = default)
            => _inner.QueryDeadLetterAsync(jobId, cancellationToken);

        public Task MarkDeadLetterRunSucceededAsync(
            Guid jobId, Guid runId, CancellationToken cancellationToken = default)
            => _inner.MarkDeadLetterRunSucceededAsync(jobId, runId, cancellationToken);

        public Task ClearDeadLetterFromFailedRunsAsync(
            Guid jobId, CancellationToken cancellationToken = default)
            => _inner.ClearDeadLetterFromFailedRunsAsync(jobId, cancellationToken);
    }
}
