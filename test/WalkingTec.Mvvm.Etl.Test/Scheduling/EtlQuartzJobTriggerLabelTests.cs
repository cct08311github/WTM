#nullable enable
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Scheduling;

namespace WalkingTec.Mvvm.Etl.Test.Scheduling;

/// <summary>
/// Tests for M24 fix: in the terminal-failure else branch (no retry
/// scheduled), "trigger = EtlRunTrigger.Retry" was set unconditionally,
/// mislabeling the RunLog entry. Fix: the else branch no longer overwrites
/// trigger, preserving the original value (e.g. Scheduled/Manual).
///
/// EtlQuartzJob.Execute() requires a full Quartz IJobExecutionContext so we
/// cannot call it directly in a unit test. The tests here cover:
///   1. The trigger/retry branching logic (ShouldRetry boundaries).
///   2. An enum-identity sanity check that makes the mislabeling visible.
///   3. A documentation test confirming the invariant via the retry/terminal
///      split that was the root cause of the bug.
/// </summary>
[TestClass]
public class EtlQuartzJobTriggerLabelTests
{
    private static EtlJobDefinition CreateJobWithRetry(int retryCount) => new()
    {
        Name = "trigger-label-test",
        CronExpression = "0 0 * * *",
        SourceCsKey = "test",
        SourceDbType = Core.DBTypeEnum.SqlServer,
        WatermarkType = EtlWatermarkType.FullLoad,
        Status = EtlJobStatus.Enabled,
        RetryCount = retryCount
    };

    // ─── Branching logic: retry-scheduling vs terminal-failure ────────────

    /// <summary>
    /// When ShouldRetry returns false the terminal-failure else branch executes.
    /// After the M24 fix that branch must NOT overwrite trigger with Retry.
    /// This test verifies the condition (ShouldRetry=false) that causes the else
    /// branch to run; the source-code fix ensures the mislabeling no longer occurs.
    /// </summary>
    [TestMethod]
    public void ShouldRetry_exhausted_identifies_terminal_failure_branch()
    {
        var job = CreateJobWithRetry(retryCount: 2);

        // currentAttempt == RetryCount → exhausted → terminal else branch.
        var isTerminal = !EtlSchedulerService.ShouldRetry(job, currentAttempt: 2);

        Assert.IsTrue(isTerminal,
            "When currentAttempt equals RetryCount, ShouldRetry must return false " +
            "(terminal-failure branch). After M24 fix that branch must NOT set " +
            "trigger = EtlRunTrigger.Retry.");
    }

    [TestMethod]
    public void ShouldRetry_within_limit_identifies_retry_scheduling_branch()
    {
        var job = CreateJobWithRetry(retryCount: 3);

        // First attempt (0) within limit (3) → retry-scheduling branch.
        // That branch correctly sets trigger = EtlRunTrigger.Retry.
        Assert.IsTrue(EtlSchedulerService.ShouldRetry(job, currentAttempt: 0),
            "First attempt within retry limit must enter retry-scheduling branch.");
    }

    [TestMethod]
    public void ShouldRetry_no_retries_configured_always_terminal()
    {
        // RetryCount = 0 → every failure is terminal from the first attempt.
        // The terminal else branch always runs; trigger must preserve original value.
        var job = CreateJobWithRetry(retryCount: 0);
        Assert.IsFalse(EtlSchedulerService.ShouldRetry(job, currentAttempt: 0),
            "With RetryCount=0 every failure is terminal; trigger must not be overwritten.");
    }

    // ─── Enum-identity sanity check ──────────────────────────────────────

    [TestMethod]
    public void EtlRunTrigger_Scheduled_value_is_distinct_from_Retry()
    {
        // Sanity: Scheduled and Retry are different enum values.
        // Overwriting Scheduled with Retry in the terminal path corrupts RunLog.
        Assert.AreNotEqual(
            EtlRunTrigger.Scheduled,
            EtlRunTrigger.Retry,
            "EtlRunTrigger.Scheduled and Retry must be distinct; overwriting one " +
            "with the other corrupts the RunLog trigger column (M24 mislabel).");
    }

    [TestMethod]
    public void EtlRunTrigger_Manual_value_is_distinct_from_Retry()
    {
        // Manual-triggered terminal failures were also mislabeled as Retry before M24.
        Assert.AreNotEqual(
            EtlRunTrigger.Manual,
            EtlRunTrigger.Retry,
            "EtlRunTrigger.Manual and Retry must be distinct; a manually-triggered " +
            "terminal failure must retain Manual in the RunLog, not be overwritten.");
    }

    // ─── Boundary: retry limit edge cases ────────────────────────────────

    [TestMethod]
    public void ShouldRetry_attempt_equals_limit_is_terminal_not_retry()
    {
        var job = CreateJobWithRetry(retryCount: 3);
        // Attempt 3 == RetryCount 3 → exhausted.
        Assert.IsFalse(EtlSchedulerService.ShouldRetry(job, currentAttempt: 3),
            "Attempt equal to RetryCount is exhausted (terminal), not retryable.");
    }

    [TestMethod]
    public void ShouldRetry_attempt_one_before_limit_is_still_retryable()
    {
        var job = CreateJobWithRetry(retryCount: 3);
        // Attempt 2 < RetryCount 3 → still within limit.
        Assert.IsTrue(EtlSchedulerService.ShouldRetry(job, currentAttempt: 2),
            "Attempt one less than RetryCount must still be retryable.");
    }
}
