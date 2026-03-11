#nullable enable
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Testing;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

[TestClass]
public class FailureTests
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
    public async Task Failure_on_batch_returns_error_result()
    {
        _source.SetData(TestHelpers.GenerateOrderData(100_000));
        _loader.FailOnBatch = 2;

        var config = TestHelpers.CreateTestConfig() with { BatchSize = 50_000 };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeFalse();
        result.Aborted.Should().BeFalse();
    }

    [TestMethod]
    public async Task Failure_discards_watermark()
    {
        _source.SetData(TestHelpers.GenerateOrderData(100_000));
        _loader.FailOnBatch = 2;

        var original = JsonSerializer.Serialize(new System.DateTime(2026, 3, 10, 0, 0, 0));
        var config = TestHelpers.CreateTestConfig() with { BatchSize = 50_000 };
        var watermark = new WatermarkStrategy(
            EtlWatermarkType.Timestamp, "UpdatedAt", original);
        var executor = new EtlPipelineExecutor(_source, _loader);

        await executor.ExecuteAsync(config, watermark);

        watermark.CurrentValue.Should().Be(original);
    }

    [TestMethod]
    public async Task Failure_includes_error_message()
    {
        _source.SetData(TestHelpers.GenerateOrderData(100_000));
        _loader.FailOnBatch = 1;

        var config = TestHelpers.CreateTestConfig() with { BatchSize = 50_000 };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("simulated failure");
    }
}
