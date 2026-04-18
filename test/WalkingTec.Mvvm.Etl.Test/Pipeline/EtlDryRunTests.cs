#nullable enable
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
using WalkingTec.Mvvm.Etl.Testing;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// Tests for issue #834: ETL dry-run mode. Verifies that when
/// <see cref="EtlPipelineConfig.IsDryRun"/> is true, the executor
/// extracts + transforms only, skips every write step, emits a
/// preview sample + validation warnings, and never commits watermark.
/// </summary>
[TestClass]
public class EtlDryRunTests
{
    private MockEtlSource _source = null!;
    private MockBulkLoader _loader = null!;

    [TestInitialize]
    public void Setup()
    {
        _source = new MockEtlSource();
        _loader = new MockBulkLoader();
    }

    [TestMethod]
    public async Task DryRun_skips_all_write_steps()
    {
        _source.SetData(TestHelpers.GenerateOrderData(100));
        var config = TestHelpers.CreateTestConfig() with { IsDryRun = true };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue();
        result.IsDryRun.Should().BeTrue();
        _loader.EnsureStagingCalled.Should().BeFalse("dry-run must not create staging table");
        _loader.TruncateCalled.Should().BeFalse("dry-run must not truncate staging");
        _loader.BatchCount.Should().Be(0, "dry-run must not bulk-load");
        _loader.MergeCalled.Should().BeFalse("dry-run must not merge to target");
        result.LoadedRows.Should().Be(0);
    }

    [TestMethod]
    public async Task DryRun_returns_preview_sample_of_configured_size()
    {
        _source.SetData(TestHelpers.GenerateOrderData(50));
        var config = TestHelpers.CreateTestConfig() with
        {
            IsDryRun = true,
            DryRunPreviewSampleSize = 5,
        };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.PreviewRows.Should().HaveCount(5);
        result.PreviewRows[0].Should().ContainKey("OrderNo");
        result.PreviewRows[0].Should().ContainKey("Amount");
        result.PreviewRows[0].Should().ContainKey("UpdatedAt");
    }

    [TestMethod]
    public async Task DryRun_preview_defaults_to_10_rows()
    {
        _source.SetData(TestHelpers.GenerateOrderData(100));
        var config = TestHelpers.CreateTestConfig() with { IsDryRun = true };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.PreviewRows.Should().HaveCount(10);
    }

    [TestMethod]
    public async Task DryRun_stops_after_first_batch_regardless_of_source_size()
    {
        _source.SetData(TestHelpers.GenerateOrderData(120_000));
        var config = TestHelpers.CreateTestConfig() with
        {
            IsDryRun = true,
            BatchSize = 50_000,
        };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.ExtractedRows.Should().Be(50_000,
            "dry-run must only pull one batch to keep cost bounded");
    }

    [TestMethod]
    public async Task DryRun_warns_when_MergeKeyColumn_not_in_source()
    {
        _source.SetData(TestHelpers.GenerateOrderData(10));
        var config = TestHelpers.CreateTestConfig() with
        {
            IsDryRun = true,
            MergeKeyColumn = "NonExistentColumn",
        };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue("warnings are informational, not fatal");
        result.ValidationWarnings.Should().Contain(w =>
            w.Contains("NonExistentColumn") && w.Contains("not found"));
    }

    [TestMethod]
    public async Task DryRun_warns_on_empty_source()
    {
        _source.SetData(TestHelpers.GenerateOrderData(0));
        var config = TestHelpers.CreateTestConfig() with { IsDryRun = true };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue();
        result.ExtractedRows.Should().Be(0);
        result.ValidationWarnings.Should().Contain(w => w.Contains("zero rows"));
    }

    [TestMethod]
    public async Task DryRun_warns_when_MergeKeyColumn_empty()
    {
        _source.SetData(TestHelpers.GenerateOrderData(10));
        var config = TestHelpers.CreateTestConfig() with
        {
            IsDryRun = true,
            MergeKeyColumn = "",
        };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.ValidationWarnings.Should().Contain(w =>
            w.Contains("MergeKeyColumn") && w.Contains("null or empty"));
    }

    [TestMethod]
    public async Task DryRun_calculates_pending_watermark_without_committing()
    {
        _source.SetData(TestHelpers.GenerateOrderData(20,
            baseDate: new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)));
        var config = TestHelpers.CreateTestConfig() with { IsDryRun = true };
        var watermark = new WatermarkStrategy(
            EtlWatermarkType.Timestamp, "UpdatedAt",
            System.Text.Json.JsonSerializer.Serialize(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        var initialCurrent = watermark.CurrentValue;

        var executor = new EtlPipelineExecutor(_source, _loader);
        var result = await executor.ExecuteAsync(config, watermark);

        result.NewWatermarkValue.Should().NotBeNullOrEmpty(
            "dry-run must report the pending watermark value");
        watermark.CurrentValue.Should().Be(initialCurrent,
            "dry-run must NOT commit the watermark — CurrentValue unchanged");
    }

    [TestMethod]
    public async Task DryRun_runs_transform_function()
    {
        _source.SetData(TestHelpers.GenerateOrderData(5));
        var transformCalls = 0;
        var config = TestHelpers.CreateTestConfig() with
        {
            IsDryRun = true,
            TransformFunc = batch =>
            {
                transformCalls++;
                return batch;
            },
        };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        await executor.ExecuteAsync(config, watermark);

        transformCalls.Should().Be(1,
            "dry-run must exercise the transform so operators can validate it");
    }

    [TestMethod]
    public async Task DryRun_surfaces_extract_exception_as_error_not_crash()
    {
        var throwingSource = new ThrowingSource(new InvalidOperationException("simulated SQL syntax error"));
        var config = TestHelpers.CreateTestConfig() with { IsDryRun = true };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(throwingSource, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeFalse();
        result.IsDryRun.Should().BeTrue();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    /// <summary>Ad-hoc source that throws on first iteration of ExtractBatchesAsync.</summary>
    private sealed class ThrowingSource : IEtlSource
    {
        private readonly Exception _toThrow;
        public ThrowingSource(Exception toThrow) { _toThrow = toThrow; }

        public async IAsyncEnumerable<DataTable> ExtractBatchesAsync(
            string connectionString, string queryTemplate, object? watermarkValue,
            int batchSize,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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

    [TestMethod]
    public async Task DryRun_zero_sample_size_returns_empty_preview_but_still_validates()
    {
        _source.SetData(TestHelpers.GenerateOrderData(20));
        var config = TestHelpers.CreateTestConfig() with
        {
            IsDryRun = true,
            DryRunPreviewSampleSize = 0,
            MergeKeyColumn = "BogusKey",
        };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.PreviewRows.Should().BeEmpty();
        result.ValidationWarnings.Should().Contain(w => w.Contains("BogusKey"));
    }

    [TestMethod]
    public async Task DryRun_preview_rows_convert_DBNull_to_null()
    {
        var dt = new DataTable();
        dt.Columns.Add("OrderNo", typeof(string));
        dt.Columns.Add("Notes", typeof(string));
        var row = dt.NewRow();
        row["OrderNo"] = "A1";
        row["Notes"] = DBNull.Value;
        dt.Rows.Add(row);

        _source.SetData(dt);
        var config = TestHelpers.CreateTestConfig() with
        {
            IsDryRun = true,
            MergeKeyColumn = "OrderNo",
        };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.PreviewRows.Should().HaveCount(1);
        result.PreviewRows[0]["Notes"].Should().BeNull(
            "DBNull must be projected as C# null for JSON friendliness");
    }
}
