#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Testing;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// Per-batch retry-with-backoff: a transiently failing BulkLoad
/// recovers automatically when MaxBatchRetries > 0; permanent
/// failures still surface as job failure; backoff timing & stats
/// are reported back via EtlExecutionResult.RetryAttemptsTotal.
/// </summary>
[TestClass]
public class RetryWithBackoffTests
{
    private static EtlPipelineConfig ConfigWithRetries(int maxRetries) =>
        TestHelpers.CreateTestConfig() with
        {
            BatchSize = 100,
            MaxBatchRetries = maxRetries,
            // Tight delays so the test suite stays fast — production uses
            // 200ms base, 30s ceiling. With base=1ms ceiling=10ms the
            // worst-case test runtime is dominated by jitter (~tens of ms).
            BatchRetryBaseDelayMs = 1,
            BatchRetryMaxDelayMs = 10,
        };

    // ── Default: MaxBatchRetries = 0 keeps pre-10.5 behavior ────────────

    [TestMethod]
    public async Task Default_no_retry_first_failure_aborts_job()
    {
        var source = new MockEtlSource(); source.SetData(TestHelpers.GenerateOrderData(100));
        var loader = new MockBulkLoader { TransientFailuresBeforeSuccess = 1 };
        var executor = new EtlPipelineExecutor(source, loader);

        var result = await executor.ExecuteAsync(
            TestHelpers.CreateTestConfig() with { BatchSize = 100 },
            new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null));

        Assert.IsFalse(result.Success, "MaxBatchRetries default = 0; first failure must fail the job.");
        Assert.AreEqual(0, result.RetryAttemptsTotal);
        StringAssert.Contains(result.ErrorMessage ?? "", "transient failure");
    }

    // ── MaxBatchRetries > 0 recovers from transient failures ────────────

    [TestMethod]
    public async Task Single_transient_failure_recovers_with_one_retry()
    {
        var data = TestHelpers.GenerateOrderData(100);
        var source = new MockEtlSource(); source.SetData(data);
        var loader = new MockBulkLoader { TransientFailuresBeforeSuccess = 1 };
        var executor = new EtlPipelineExecutor(source, loader);

        var result = await executor.ExecuteAsync(
            ConfigWithRetries(maxRetries: 3),
            new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null));

        Assert.IsTrue(result.Success, $"Should recover after 1 retry; got error '{result.ErrorMessage}'.");
        Assert.AreEqual(1, result.RetryAttemptsTotal);
        Assert.AreEqual(1, loader.TransientFailuresObserved);
        Assert.AreEqual(100, result.LoadedRows);
        Assert.IsTrue(loader.MergeCalled, "Merge must still run on recovered job.");
    }

    [TestMethod]
    public async Task Multiple_transient_failures_recover_within_budget()
    {
        var source = new MockEtlSource(); source.SetData(TestHelpers.GenerateOrderData(100));
        var loader = new MockBulkLoader { TransientFailuresBeforeSuccess = 3 };
        var executor = new EtlPipelineExecutor(source, loader);

        var result = await executor.ExecuteAsync(
            ConfigWithRetries(maxRetries: 5),
            new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null));

        Assert.IsTrue(result.Success);
        Assert.AreEqual(3, result.RetryAttemptsTotal);
    }

    // ── Permanent failure exhausts retries and aborts cleanly ───────────

    [TestMethod]
    public async Task Failures_beyond_budget_abort_job_with_retry_count()
    {
        var source = new MockEtlSource(); source.SetData(TestHelpers.GenerateOrderData(100));
        // 5 transient failures but only 2 retries allowed → 3rd attempt fails permanently.
        var loader = new MockBulkLoader { TransientFailuresBeforeSuccess = 5 };
        var executor = new EtlPipelineExecutor(source, loader);

        var result = await executor.ExecuteAsync(
            ConfigWithRetries(maxRetries: 2),
            new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null));

        Assert.IsFalse(result.Success);
        Assert.AreEqual(2, result.RetryAttemptsTotal,
            "Retries observed must equal the budget when failures exceed budget.");
        Assert.AreEqual(3, loader.TransientFailuresObserved,
            "First try (1) + 2 retries = 3 attempts, all failing.");
        StringAssert.Contains(result.ErrorMessage ?? "", "transient failure");
    }

    // ── Watermark / Merge behavior under retry ──────────────────────────

    [TestMethod]
    public async Task Watermark_committed_when_retry_succeeds()
    {
        var data = TestHelpers.GenerateOrderData(50);
        var source = new MockEtlSource(); source.SetData(data);
        var loader = new MockBulkLoader { TransientFailuresBeforeSuccess = 1 };
        var executor = new EtlPipelineExecutor(source, loader);

        var watermark = new WatermarkStrategy(EtlWatermarkType.Timestamp, "UpdatedAt", null);

        var result = await executor.ExecuteAsync(ConfigWithRetries(3), watermark);

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.NewWatermarkValue,
            "Watermark must commit on a retry-recovered run, same as a clean run.");
    }

    [TestMethod]
    public async Task Watermark_discarded_when_retry_budget_exhausted()
    {
        var source = new MockEtlSource(); source.SetData(TestHelpers.GenerateOrderData(50));
        var loader = new MockBulkLoader { TransientFailuresBeforeSuccess = 99 };
        var executor = new EtlPipelineExecutor(source, loader);

        var watermark = new WatermarkStrategy(EtlWatermarkType.Timestamp, "UpdatedAt", null);

        var result = await executor.ExecuteAsync(ConfigWithRetries(2), watermark);

        Assert.IsFalse(result.Success);
        Assert.IsNull(result.NewWatermarkValue,
            "Failed run must NOT commit the watermark — same contract as no-retry pre-10.5.");
    }

    // ── Cancellation during backoff ─────────────────────────────────────

    [TestMethod]
    public async Task Cancellation_during_retry_backoff_aborts_immediately()
    {
        var source = new MockEtlSource(); source.SetData(TestHelpers.GenerateOrderData(50));
        // 99 failures but slow base delay so we have time to cancel.
        var loader = new MockBulkLoader { TransientFailuresBeforeSuccess = 99 };
        var executor = new EtlPipelineExecutor(source, loader);

        var cts = new System.Threading.CancellationTokenSource();
        var config = TestHelpers.CreateTestConfig() with
        {
            BatchSize = 100,
            MaxBatchRetries = 50,
            BatchRetryBaseDelayMs = 1000,
            BatchRetryMaxDelayMs = 10_000,
        };

        var task = executor.ExecuteAsync(
            config,
            new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null),
            cts.Token);

        // Cancel after a short moment — must be observed promptly.
        cts.CancelAfter(100);
        var result = await task;

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Aborted, "Cancellation during backoff must surface as Aborted.");
    }

    // ── Clamp ranges ────────────────────────────────────────────────────

    [TestMethod]
    public async Task Negative_MaxBatchRetries_treated_as_zero()
    {
        var source = new MockEtlSource(); source.SetData(TestHelpers.GenerateOrderData(50));
        var loader = new MockBulkLoader { TransientFailuresBeforeSuccess = 1 };
        var executor = new EtlPipelineExecutor(source, loader);

        var result = await executor.ExecuteAsync(
            TestHelpers.CreateTestConfig() with { BatchSize = 100, MaxBatchRetries = -5 },
            new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null));

        // Negative is clamped to 0; first failure aborts, no retry.
        Assert.IsFalse(result.Success);
        Assert.AreEqual(0, result.RetryAttemptsTotal);
    }
}
