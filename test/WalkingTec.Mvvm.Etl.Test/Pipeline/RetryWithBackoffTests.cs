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
        // #970 negative control, paired with Cancellation_already_requested_when_transient_thrown_aborts_immediately
        // below (same MaxBatchRetries=0 shape, but with no cancellation at all): a
        // genuine data failure must not report Aborted=true.
        Assert.IsFalse(result.Aborted,
            "A genuine, non-cancelled data failure must not report Aborted=true.");
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
        // #970 negative control: a genuine data failure — retries exhausted, no
        // cancellation ever requested — must NOT report Aborted=true. Without this
        // assertion, a #970 fix that set Aborted unconditionally (instead of only on
        // cancellation) would pass both cancellation-interleaving tests below and
        // still be wrong.
        Assert.IsFalse(result.Aborted,
            "A genuine, non-cancelled data failure must not report Aborted=true.");
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

    // ── Cancellation during backoff (#970) ──────────────────────────────
    //
    // #970: the retry helper's cancellation handling has two distinct
    // interleavings relative to when the loader throws its transient
    // failure, and each is tested SEPARATELY here rather than relying on a
    // wall-clock race (the original version of this test used
    // `cts.CancelAfter(100)` against a 1000ms base delay — timing that
    // *looked* generous but wasn't: full-jitter backoff draws
    // `Random.Shared.NextInt64(0, ceilingMs + 1)`, so an unlucky low roll on
    // an early attempt could let many retries fire inside that 100ms
    // window, occasionally landing the cancel exactly at a throw instant —
    // interleaving 2 below — instead of during a delay. That is precisely
    // the production defect, not flakiness in the test's premise.):
    //
    //   1. Cancellation observed WHILE genuinely suspended in the backoff
    //      Task.Delay (this test) — already correctly surfaces as Aborted
    //      before the #970 fix, via the existing
    //      `catch (OperationCanceledException)` immediately above the
    //      retry catch.
    //   2. Cancellation already requested at the exact instant the loader
    //      throws the transient exception (next test) — this was the
    //      defect: the old `catch when (... && !IsCancellationRequested)`
    //      filter did not match, so the transient exception's own type
    //      propagated to the general `catch (Exception ex)` with
    //      Aborted=false.

    [TestMethod]
    public async Task Cancellation_during_retry_backoff_aborts_immediately()
    {
        var source = new MockEtlSource(); source.SetData(TestHelpers.GenerateOrderData(50));
        var loader = new MockBulkLoader { TransientFailuresBeforeSuccess = 1 };

        // Deterministic signal: fires synchronously the instant BEFORE the loader's
        // (only) simulated transient failure is thrown. RunContinuationsAsynchronously
        // is required — without it, TrySetResult could run this test's continuation
        // (which cancels) INLINE on the loader's own call stack, i.e. before the
        // exception even leaves BulkLoadAsync, which would instead reproduce
        // interleaving 2 (the sibling test), not this one.
        var firstFailureThrown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loader.OnBeforeTransientFailureThrown += (_, _) => firstFailureThrown.TrySetResult();

        var executor = new EtlPipelineExecutor(source, loader);

        var cts = new System.Threading.CancellationTokenSource();
        var config = TestHelpers.CreateTestConfig() with
        {
            BatchSize = 100,
            MaxBatchRetries = 50,
            // Large enough that the full-jitter roll (0..ceilingMs, inclusive of 0)
            // landing exactly on 0 — which would skip Task.Delay entirely and defeat
            // this test's premise of cancelling WHILE suspended in the delay — has
            // negligible probability (~1 in 2,000,001) instead of the ~1-in-2001
            // chance the original 1000/10_000ms values gave it.
            BatchRetryBaseDelayMs = 1_000_000,
            BatchRetryMaxDelayMs = 2_000_000,
        };

        var task = executor.ExecuteAsync(
            config,
            new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null),
            cts.Token);

        // Wait until the (only) transient failure has actually been thrown before
        // cancelling. Because BulkLoadWithRetryAsync's cancellation check (both
        // before and after #970) runs synchronously, on the same call stack,
        // immediately after the throw — strictly before this test's continuation can
        // resume on a different thread-pool callback — cancelling here always lands
        // after that check has already found "not yet cancelled" and the loop has
        // moved on to compute and await the backoff delay.
        await firstFailureThrown.Task;
        cts.Cancel();
        var result = await task;

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Aborted, "Cancellation during backoff must surface as Aborted.");
    }

    [TestMethod]
    public async Task Cancellation_already_requested_when_transient_thrown_aborts_immediately()
    {
        // #970 — the defect: cancellation already requested at the exact instant the
        // loader throws its transient failure. Before the fix, the retry catch's
        // `when (attempt < maxRetries && !cancellationToken.IsCancellationRequested)`
        // filter evaluates false here, so that catch does not match; the transient
        // exception's own type propagates untouched past the
        // `catch (OperationCanceledException)` above it (which can't match a
        // non-cancellation exception) to the caller's general `catch (Exception ex)`,
        // reporting an operator-cancelled run as an ordinary data failure
        // (Aborted=false).
        var source = new MockEtlSource(); source.SetData(TestHelpers.GenerateOrderData(50));
        var loader = new MockBulkLoader { TransientFailuresBeforeSuccess = 1 };

        var cts = new System.Threading.CancellationTokenSource();
        // Cancel SYNCHRONOUSLY, on the same call stack, the instant before the mock
        // throws — deterministically reproducing "cancellation already requested at
        // the instant a transient exception is thrown" with no timing dependency at
        // all.
        loader.OnBeforeTransientFailureThrown += (_, _) => cts.Cancel();

        var executor = new EtlPipelineExecutor(source, loader);
        // MaxBatchRetries = 0 is deliberate, not incidental: it forces the fixed code's
        // `cancellationToken.ThrowIfCancellationRequested()` check to be the ONLY thing
        // that can distinguish this case from an ordinary retries-exhausted data
        // failure. With retry budget available (e.g. MaxBatchRetries = 3), the mutant
        // that neutralizes this fix (removes just that one ThrowIfCancellationRequested
        // call, see test/mutants/entries/etl970-*) does not reliably fail this
        // assertion: the loop falls through to `attempt++`/`Task.Delay`, and Task.Delay
        // itself independently short-circuits to a canceled task when called with an
        // ALREADY-cancelled token — so the mutant can still accidentally converge on
        // Aborted=true through the pre-existing delay-cancellation path, or (if the
        // random jitter happens to roll exactly 0, skipping Task.Delay entirely) even
        // let the retry silently succeed. MaxBatchRetries = 0 makes `attempt >=
        // maxRetries` true immediately, so the ONLY route to Aborted=true is the
        // ThrowIfCancellationRequested() check itself -- a deterministic kill for the
        // mutant, not a probabilistic one.
        var config = TestHelpers.CreateTestConfig() with
        {
            BatchSize = 100,
            MaxBatchRetries = 0,
        };

        var result = await executor.ExecuteAsync(
            config,
            new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null),
            cts.Token);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Aborted,
            "Cancellation already requested when the transient exception is thrown must still surface as Aborted.");
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
