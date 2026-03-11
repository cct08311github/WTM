#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Testing;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

[TestClass]
public class EtlPipelineTests
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
    public async Task Execute_splits_data_into_correct_batches()
    {
        _source.SetData(TestHelpers.GenerateOrderData(120_000));
        var config = TestHelpers.CreateTestConfig() with { BatchSize = 50_000 };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue();
        _loader.BatchCount.Should().Be(3); // 50K + 50K + 20K
        _loader.TotalRows.Should().Be(120_000);
    }

    [TestMethod]
    public async Task Execute_calls_merge_after_all_batches()
    {
        _source.SetData(TestHelpers.GenerateOrderData(100));
        var config = TestHelpers.CreateTestConfig();
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        await executor.ExecuteAsync(config, watermark);

        _loader.MergeCalled.Should().BeTrue();
    }

    [TestMethod]
    public async Task Execute_calls_truncate_before_loading()
    {
        _source.SetData(TestHelpers.GenerateOrderData(100));
        var config = TestHelpers.CreateTestConfig();
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        await executor.ExecuteAsync(config, watermark);

        _loader.TruncateCalled.Should().BeTrue();
    }

    [TestMethod]
    public async Task Execute_calls_ensure_staging()
    {
        _source.SetData(TestHelpers.GenerateOrderData(100));
        var config = TestHelpers.CreateTestConfig();
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        await executor.ExecuteAsync(config, watermark);

        _loader.EnsureStagingCalled.Should().BeTrue();
    }

    [TestMethod]
    public async Task Execute_reports_progress_per_batch()
    {
        _source.SetData(TestHelpers.GenerateOrderData(120_000));
        var config = TestHelpers.CreateTestConfig() with { BatchSize = 50_000 };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);

        var progressReports = new List<EtlProgress>();
        var progress = new SynchronousProgress<EtlProgress>(p => progressReports.Add(p));
        var executor = new EtlPipelineExecutor(_source, _loader, progress);

        await executor.ExecuteAsync(config, watermark);

        // 3 Loading reports + 1 Merging report = at least 4
        progressReports.Count.Should().BeGreaterThanOrEqualTo(4);
    }

    [TestMethod]
    public async Task Execute_returns_correct_row_counts()
    {
        _source.SetData(TestHelpers.GenerateOrderData(5_000));
        var config = TestHelpers.CreateTestConfig() with { BatchSize = 2_000 };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.ExtractedRows.Should().Be(5_000);
        result.LoadedRows.Should().Be(5_000);
        result.ElapsedMs.Should().BeGreaterOrEqualTo(0);
    }

    [TestMethod]
    public async Task Execute_with_transform_applies_function()
    {
        _source.SetData(TestHelpers.GenerateOrderData(100));
        bool transformCalled = false;
        var config = TestHelpers.CreateTestConfig() with
        {
            TransformFunc = dt =>
            {
                transformCalled = true;
                return dt; // pass-through
            }
        };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        await executor.ExecuteAsync(config, watermark);

        transformCalled.Should().BeTrue();
    }

    [TestMethod]
    public async Task Execute_empty_source_returns_success_zero_rows()
    {
        _source.SetData(new DataTable());
        var config = TestHelpers.CreateTestConfig();
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue();
        result.ExtractedRows.Should().Be(0);
        result.LoadedRows.Should().Be(0);
        _loader.MergeCalled.Should().BeTrue();
    }
}
