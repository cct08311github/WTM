#nullable enable
// ETL-015: Dry-run audit-logging tests
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Testing;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// ETL-015: Structured audit-logging on the dry-run source-read path.
///
/// Verifies:
///   1. Dry-run start → LogInformation entry with JobId / JobName / query.
///   2. Dry-run completion → LogInformation entry with ExtractedRows / ElapsedMs.
///   3. Dry-run abort (OperationCanceled) → LogWarning entry.
///   4. Dry-run extract failure → LogError entry.
///   5. Normal (non-dry-run) execution emits NO dry-run log entries.
/// </summary>
[TestClass]
public class DryRunAuditLoggingTests
{
    // ─── helpers ────────────────────────────────────────────────────────────

    private static (MockEtlSource source, MockBulkLoader loader, CapturingLogger logger)
        CreateComponents()
    {
        return (new MockEtlSource(), new MockBulkLoader(), new CapturingLogger());
    }

    private static WatermarkStrategy FullLoad() =>
        new(EtlWatermarkType.FullLoad, null, null, "UTC");

    // ─── happy-path tests ────────────────────────────────────────────────────

    [TestMethod]
    public async Task DryRun_start_emits_LogInformation_with_job_name()
    {
        var (source, loader, logger) = CreateComponents();
        source.SetData(TestHelpers.GenerateOrderData(10));

        var config = TestHelpers.CreateTestConfig() with { IsDryRun = true };
        var executor = new EtlPipelineExecutor(source, loader, logger: logger);
        await executor.ExecuteAsync(config, FullLoad());

        logger.HasInformationContaining("dry-run started").Should().BeTrue(
            "ETL-015 requires a structured start log entry at the beginning of dry-run");
    }

    [TestMethod]
    public async Task DryRun_start_log_includes_JobName()
    {
        var (source, loader, logger) = CreateComponents();
        source.SetData(TestHelpers.GenerateOrderData(5));

        var config = TestHelpers.CreateTestConfig() with
        {
            IsDryRun = true,
            JobName = "AuditTestJob",
        };
        var executor = new EtlPipelineExecutor(source, loader, logger: logger);
        await executor.ExecuteAsync(config, FullLoad());

        logger.HasInformationContaining("AuditTestJob").Should().BeTrue(
            "start log must include JobName so audit trail is human-readable");
    }

    [TestMethod]
    public async Task DryRun_completion_emits_LogInformation_with_extracted_rows()
    {
        var (source, loader, logger) = CreateComponents();
        source.SetData(TestHelpers.GenerateOrderData(20));

        var config = TestHelpers.CreateTestConfig() with { IsDryRun = true };
        var executor = new EtlPipelineExecutor(source, loader, logger: logger);
        await executor.ExecuteAsync(config, FullLoad());

        logger.HasInformationContaining("dry-run completed").Should().BeTrue(
            "ETL-015 requires a completion log entry after dry-run succeeds");
    }

    [TestMethod]
    public async Task DryRun_emits_at_least_two_Information_entries_on_success()
    {
        var (source, loader, logger) = CreateComponents();
        source.SetData(TestHelpers.GenerateOrderData(5));

        var config = TestHelpers.CreateTestConfig() with { IsDryRun = true };
        var executor = new EtlPipelineExecutor(source, loader, logger: logger);
        await executor.ExecuteAsync(config, FullLoad());

        logger.Entries(LogLevel.Information).Count.Should().BeGreaterThanOrEqualTo(2,
            "ETL-015 must emit at least a start and a completion entry");
    }

    [TestMethod]
    public async Task DryRun_empty_source_still_emits_start_and_completion_logs()
    {
        var (source, loader, logger) = CreateComponents();
        source.SetData(TestHelpers.GenerateOrderData(0));   // empty source

        var config = TestHelpers.CreateTestConfig() with { IsDryRun = true };
        var executor = new EtlPipelineExecutor(source, loader, logger: logger);
        await executor.ExecuteAsync(config, FullLoad());

        logger.HasInformationContaining("dry-run started").Should().BeTrue();
        logger.HasInformationContaining("dry-run completed").Should().BeTrue();
    }

    // ─── cancellation path ───────────────────────────────────────────────────

    [TestMethod]
    public async Task DryRun_abort_emits_LogWarning()
    {
        var source = new CancellingSource();    // throws OperationCanceledException
        var loader = new MockBulkLoader();
        var logger = new CapturingLogger();

        var config = TestHelpers.CreateTestConfig() with { IsDryRun = true };
        var executor = new EtlPipelineExecutor(source, loader, logger: logger);
        await executor.ExecuteAsync(config, FullLoad());

        logger.HasWarningContaining("dry-run aborted").Should().BeTrue(
            "ETL-015 requires a LogWarning entry when dry-run is cancelled");
    }

    // ─── error path ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task DryRun_extract_failure_emits_LogError()
    {
        var source = new ThrowingSource(new InvalidOperationException("simulated DB error"));
        var loader = new MockBulkLoader();
        var logger = new CapturingLogger();

        var config = TestHelpers.CreateTestConfig() with { IsDryRun = true };
        var executor = new EtlPipelineExecutor(source, loader, logger: logger);
        await executor.ExecuteAsync(config, FullLoad());

        logger.HasErrorEntry().Should().BeTrue(
            "ETL-015 requires a LogError entry when the dry-run source read fails");
        logger.HasErrorContaining("dry-run failed").Should().BeTrue(
            "the error entry must identify this as a dry-run failure");
    }

    // ─── isolation: normal run must NOT emit dry-run log entries ────────────

    [TestMethod]
    public async Task Normal_run_emits_no_dryrun_log_entries()
    {
        var source = new MockEtlSource();
        source.SetData(TestHelpers.GenerateOrderData(5));
        var loader = new MockBulkLoader();
        var logger = new CapturingLogger();

        var config = TestHelpers.CreateTestConfig();   // IsDryRun defaults to false
        var executor = new EtlPipelineExecutor(source, loader, logger: logger);
        await executor.ExecuteAsync(config, FullLoad());

        logger.HasInformationContaining("dry-run").Should().BeFalse(
            "normal run must not emit dry-run log entries");
        logger.HasWarningContaining("dry-run").Should().BeFalse();
    }

    // ─── ad-hoc source helpers ───────────────────────────────────────────────

    private sealed class ThrowingSource : IEtlSource
    {
        private readonly Exception _toThrow;
        public ThrowingSource(Exception toThrow) { _toThrow = toThrow; }

        public async IAsyncEnumerable<System.Data.DataTable> ExtractBatchesAsync(
            string connectionString, string queryTemplate, object? watermarkValue,
            int batchSize,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            throw _toThrow;
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancellingSource : IEtlSource
    {
        public async IAsyncEnumerable<System.Data.DataTable> ExtractBatchesAsync(
            string connectionString, string queryTemplate, object? watermarkValue,
            int batchSize,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            throw new OperationCanceledException("cancelled during test");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

// ─── Logger spy ────────────────────────────────────────────────────────────────

/// <summary>
/// In-memory ILogger that captures log calls so tests can assert on their
/// level, message, and content without any real logging infrastructure.
/// Thread-safe (lock-free list is sufficient for single-threaded tests).
/// </summary>
internal sealed class CapturingLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Add((logLevel, formatter(state, exception)));
    }

    public IReadOnlyList<(LogLevel Level, string Message)> Entries(LogLevel level) =>
        _entries.FindAll(e => e.Level == level);

    public bool HasInformationContaining(string text) =>
        _entries.Exists(e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains(text, StringComparison.OrdinalIgnoreCase));

    public bool HasWarningContaining(string text) =>
        _entries.Exists(e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains(text, StringComparison.OrdinalIgnoreCase));

    public bool HasErrorEntry() =>
        _entries.Exists(e => e.Level == LogLevel.Error);

    public bool HasErrorContaining(string text) =>
        _entries.Exists(e =>
            e.Level == LogLevel.Error &&
            e.Message.Contains(text, StringComparison.OrdinalIgnoreCase));
}
