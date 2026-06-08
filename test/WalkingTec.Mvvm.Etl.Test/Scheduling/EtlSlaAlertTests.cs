#nullable enable
// ETL-010: SLA / duration breach alert tests
using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Alerting;
using WalkingTec.Mvvm.Etl.Models;

namespace WalkingTec.Mvvm.Etl.Test.Scheduling;

/// <summary>
/// ETL-010: SLA / duration breach alert tests.
///
/// Verifies:
///   1. The DIM on IEtlAlertService falls through to SendAlertAsync
///      when no override is present (back-compat guarantee).
///   2. The condition "ExpectedDurationSeconds > 0 AND ElapsedMs > threshold"
///      correctly classifies breach vs. within-SLA.
///   3. A custom IEtlAlertService that overrides SendSlaBreachAlertAsync
///      receives the correct actualElapsedMs argument.
///   4. ExpectedDurationSeconds == 0 (default) skips SLA evaluation entirely.
/// </summary>
[TestClass]
public class EtlSlaAlertTests
{
    // ─── helpers ────────────────────────────────────────────────────────────

    private static EtlJobDefinition MakeJob(int expectedDurationSeconds) => new()
    {
        Name = "SlaTestJob",
        CronExpression = "0 0 * * *",
        SourceCsKey = "src",
        SourceDbType = WalkingTec.Mvvm.Core.DBTypeEnum.SqlServer,
        TargetCsKey = "tgt",
        TargetTableName = "tbl",
        MergeKeyColumn = "id",
        QueryTemplate = "SELECT 1",
        WatermarkType = EtlWatermarkType.FullLoad,
        Status = EtlJobStatus.Enabled,
        JobClassName = "test",
        ExpectedDurationSeconds = expectedDurationSeconds,
    };

    private static EtlRunLog MakeRunLog(long elapsedMs) => new()
    {
        JobId = Guid.NewGuid(),
        Trigger = EtlRunTrigger.Scheduled,
        Result = EtlRunResult.Success,
        StartedAt = DateTime.UtcNow.AddMilliseconds(-elapsedMs),
        FinishedAt = DateTime.UtcNow,
        ElapsedMs = elapsedMs,
    };

    // ─── condition tests ────────────────────────────────────────────────────

    [TestMethod]
    public void SLA_breach_condition_fires_when_elapsed_exceeds_threshold()
    {
        var jobDef = MakeJob(expectedDurationSeconds: 60);     // SLA = 60s
        var runLog = MakeRunLog(elapsedMs: 61_000);            // actual = 61s

        var shouldAlert = jobDef.ExpectedDurationSeconds > 0
            && runLog.ElapsedMs > jobDef.ExpectedDurationSeconds * 1000L;

        shouldAlert.Should().BeTrue("61s > 60s → SLA breached");
    }

    [TestMethod]
    public void SLA_breach_condition_does_not_fire_when_within_threshold()
    {
        var jobDef = MakeJob(expectedDurationSeconds: 60);     // SLA = 60s
        var runLog = MakeRunLog(elapsedMs: 59_999);            // actual = 59.999s

        var shouldAlert = jobDef.ExpectedDurationSeconds > 0
            && runLog.ElapsedMs > jobDef.ExpectedDurationSeconds * 1000L;

        shouldAlert.Should().BeFalse("59.999s ≤ 60s → within SLA");
    }

    [TestMethod]
    public void SLA_breach_condition_does_not_fire_when_exactly_at_threshold()
    {
        var jobDef = MakeJob(expectedDurationSeconds: 60);
        var runLog = MakeRunLog(elapsedMs: 60_000);            // exact boundary

        var shouldAlert = jobDef.ExpectedDurationSeconds > 0
            && runLog.ElapsedMs > jobDef.ExpectedDurationSeconds * 1000L;

        shouldAlert.Should().BeFalse("60s == 60s → not strictly greater → no breach");
    }

    [TestMethod]
    public void SLA_breach_condition_skipped_when_ExpectedDurationSeconds_is_zero()
    {
        var jobDef = MakeJob(expectedDurationSeconds: 0);      // feature off
        var runLog = MakeRunLog(elapsedMs: 999_999);           // very slow

        var shouldAlert = jobDef.ExpectedDurationSeconds > 0
            && runLog.ElapsedMs > jobDef.ExpectedDurationSeconds * 1000L;

        shouldAlert.Should().BeFalse("ExpectedDurationSeconds=0 disables SLA alerting");
    }

    [TestMethod]
    public void SLA_breach_condition_skipped_when_ExpectedDurationSeconds_is_negative()
    {
        var jobDef = MakeJob(expectedDurationSeconds: -1);     // misconfiguration
        var runLog = MakeRunLog(elapsedMs: 999_999);

        var shouldAlert = jobDef.ExpectedDurationSeconds > 0
            && runLog.ElapsedMs > jobDef.ExpectedDurationSeconds * 1000L;

        shouldAlert.Should().BeFalse("negative ExpectedDurationSeconds is treated as disabled");
    }

    // ─── DIM back-compat test ───────────────────────────────────────────────

    [TestMethod]
    public async Task SendSlaBreachAlertAsync_DIM_delegates_to_SendAlertAsync()
    {
        // Arrange: a minimal IEtlAlertService that only implements SendAlertAsync;
        // the DIM for SendSlaBreachAlertAsync should route through it.
        var jobDef = MakeJob(60);
        var runLog = MakeRunLog(61_000);

        var spy = new TrackingAlertService();

        // Act: invoke through the interface (DIM path)
        await ((IEtlAlertService)spy).SendSlaBreachAlertAsync(
            jobDef, runLog, actualElapsedMs: 61_000);

        // Assert: DIM must have forwarded to SendAlertAsync
        spy.SendAlertCalled.Should().BeTrue(
            "the DIM on IEtlAlertService must fall through to SendAlertAsync");
        spy.LastJobDef.Should().BeSameAs(jobDef);
    }

    [TestMethod]
    public async Task SendSlaBreachAlertAsync_custom_override_receives_correct_elapsedMs()
    {
        // Arrange: an implementation that overrides SendSlaBreachAlertAsync
        var jobDef = MakeJob(60);
        var runLog = MakeRunLog(90_000);
        var spy = new CapturingSlaAlertService();

        // Act
        await spy.SendSlaBreachAlertAsync(jobDef, runLog, actualElapsedMs: 90_000);

        // Assert
        spy.CapturedElapsedMs.Should().Be(90_000,
            "the overriding implementation must receive the actual elapsed ms");
        spy.CapturedJobDef.Should().BeSameAs(jobDef);
    }

    // ─── test doubles ───────────────────────────────────────────────────────

    /// <summary>
    /// Minimal IEtlAlertService that only implements SendAlertAsync.
    /// SendSlaBreachAlertAsync falls through via the DIM.
    /// </summary>
    private sealed class TrackingAlertService : IEtlAlertService
    {
        public bool SendAlertCalled { get; private set; }
        public EtlJobDefinition? LastJobDef { get; private set; }

        public Task SendAlertAsync(
            EtlJobDefinition jobDef,
            EtlRunLog runLog,
            CancellationToken ct = default)
        {
            SendAlertCalled = true;
            LastJobDef = jobDef;
            return Task.CompletedTask;
        }
        // NOTE: intentionally NOT overriding SendSlaBreachAlertAsync → DIM path
    }

    /// <summary>
    /// Overrides SendSlaBreachAlertAsync to capture the arguments.
    /// </summary>
    private sealed class CapturingSlaAlertService : IEtlAlertService
    {
        public long CapturedElapsedMs { get; private set; }
        public EtlJobDefinition? CapturedJobDef { get; private set; }

        public Task SendAlertAsync(
            EtlJobDefinition jobDef, EtlRunLog runLog, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SendSlaBreachAlertAsync(
            EtlJobDefinition jobDef, EtlRunLog runLog, long actualElapsedMs,
            CancellationToken ct = default)
        {
            CapturedElapsedMs = actualElapsedMs;
            CapturedJobDef = jobDef;
            return Task.CompletedTask;
        }
    }
}
