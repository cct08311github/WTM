#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Testing;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

[TestClass]
public class CancellationTests
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
    public async Task Cancellation_stops_after_current_batch()
    {
        _source.SetData(TestHelpers.GenerateOrderData(200_000));
        var cts = new CancellationTokenSource();
        _loader.OnBatchLoaded += (_, _) => cts.Cancel(); // 第一批後取消

        var config = TestHelpers.CreateTestConfig() with { BatchSize = 50_000 };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark, cts.Token);

        _loader.TotalRows.Should().Be(50_000); // 只完成一批
    }

    [TestMethod]
    public async Task Cancelled_result_is_aborted()
    {
        _source.SetData(TestHelpers.GenerateOrderData(200_000));
        var cts = new CancellationTokenSource();
        _loader.OnBatchLoaded += (_, _) => cts.Cancel();

        var config = TestHelpers.CreateTestConfig() with { BatchSize = 50_000 };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark, cts.Token);

        result.Success.Should().BeFalse();
        result.Aborted.Should().BeTrue();
        result.ErrorMessage.Should().Contain("aborted");
    }

    [TestMethod]
    public async Task Cancelled_discards_watermark()
    {
        _source.SetData(TestHelpers.GenerateOrderData(200_000));
        var cts = new CancellationTokenSource();
        _loader.OnBatchLoaded += (_, _) => cts.Cancel();

        var config = TestHelpers.CreateTestConfig() with { BatchSize = 50_000 };
        var watermark = new WatermarkStrategy(
            EtlWatermarkType.Timestamp, "UpdatedAt", "\"2026-01-01T00:00:00\"");
        var executor = new EtlPipelineExecutor(_source, _loader);

        await executor.ExecuteAsync(config, watermark, cts.Token);

        // watermark 不應更新
        watermark.CurrentValue.Should().Be("\"2026-01-01T00:00:00\"");
    }
}
