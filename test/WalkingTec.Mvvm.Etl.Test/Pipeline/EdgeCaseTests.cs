#nullable enable
using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Testing;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// Pipeline 邊界場景測試 — 補充 #216
/// </summary>
[TestClass]
public class EdgeCaseTests
{
    private MockEtlSource _source = null!;
    private MockBulkLoader _loader = null!;

    [TestInitialize]
    public void Setup()
    {
        _source = new MockEtlSource();
        _loader = new MockBulkLoader();
    }

    // ─── Batch splitting ───

    [TestMethod]
    public async Task Batch_splitting_10K_by_3K_yields_4_batches()
    {
        _source.SetData(TestHelpers.GenerateOrderData(10_000));
        var config = TestHelpers.CreateTestConfig() with { BatchSize = 3_000 };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue();
        _loader.BatchCount.Should().Be(4); // 3K + 3K + 3K + 1K
        _loader.TotalRows.Should().Be(10_000);
    }

    [TestMethod]
    public async Task Batch_splitting_exact_multiple_yields_correct_count()
    {
        _source.SetData(TestHelpers.GenerateOrderData(9_000));
        var config = TestHelpers.CreateTestConfig() with { BatchSize = 3_000 };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue();
        _loader.BatchCount.Should().Be(3); // 3K + 3K + 3K exact
        _loader.TotalRows.Should().Be(9_000);
    }

    [TestMethod]
    public async Task Batch_size_larger_than_data_yields_single_batch()
    {
        _source.SetData(TestHelpers.GenerateOrderData(100));
        var config = TestHelpers.CreateTestConfig() with { BatchSize = 50_000 };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue();
        _loader.BatchCount.Should().Be(1);
        _loader.TotalRows.Should().Be(100);
    }

    // ─── Transform: filtering ───

    [TestMethod]
    public async Task Transform_filtering_reduces_loaded_rows()
    {
        // 1000 筆，Amount 在 100~1099 之間，過濾只留 Amount > 500
        _source.SetData(TestHelpers.GenerateOrderData(1_000));
        var config = TestHelpers.CreateTestConfig() with
        {
            BatchSize = 500,
            TransformFunc = dt =>
            {
                var filtered = dt.Clone();
                foreach (DataRow row in dt.Rows)
                {
                    if ((decimal)row["Amount"] > 500m)
                        filtered.ImportRow(row);
                }
                return filtered;
            }
        };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue();
        // Amount = 100 + (i % 1000), so values > 500 are i%1000 > 400 → 599 out of 1000
        result.LoadedRows.Should().BeLessThan(1_000);
        result.LoadedRows.Should().BeGreaterThan(0);
        _loader.MergeCalled.Should().BeTrue();
    }

    [TestMethod]
    public async Task Transform_filtering_all_rows_results_in_zero_loaded()
    {
        _source.SetData(TestHelpers.GenerateOrderData(100));
        var config = TestHelpers.CreateTestConfig() with
        {
            TransformFunc = _ =>
            {
                // Return empty DataTable — all rows filtered out
                var empty = new DataTable();
                empty.Columns.Add("OrderNo", typeof(string));
                empty.Columns.Add("Amount", typeof(decimal));
                empty.Columns.Add("UpdatedAt", typeof(DateTime));
                return empty;
            }
        };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue();
        result.LoadedRows.Should().Be(0);
    }

    // ─── Watermark edge case ───

    [TestMethod]
    public async Task All_null_watermark_column_does_not_crash()
    {
        // All UpdatedAt values are DBNull — GetMaxValue should return null, not throw
        var dt = new DataTable();
        dt.Columns.Add("OrderNo", typeof(string));
        dt.Columns.Add("Amount", typeof(decimal));
        dt.Columns.Add("UpdatedAt", typeof(DateTime));
        for (int i = 0; i < 10; i++)
        {
            var row = dt.NewRow();
            row["OrderNo"] = $"ORD-{i:D4}";
            row["Amount"] = 100m + i;
            row["UpdatedAt"] = DBNull.Value;
            dt.Rows.Add(row);
        }
        _source.SetData(dt);
        var config = TestHelpers.CreateTestConfig();
        var watermark = new WatermarkStrategy(EtlWatermarkType.Timestamp, "UpdatedAt", null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue();
        result.ExtractedRows.Should().Be(10);
    }

    // ─── Transform: computed column ───

    [TestMethod]
    public async Task Transform_adds_computed_column()
    {
        _source.SetData(TestHelpers.GenerateOrderData(50));
        DataTable? capturedBatch = null;
        _loader.OnBatchLoaded += (_, _) =>
        {
            capturedBatch ??= _loader.LoadedBatches.First();
        };

        var config = TestHelpers.CreateTestConfig() with
        {
            TransformFunc = dt =>
            {
                if (!dt.Columns.Contains("DoubleAmount"))
                    dt.Columns.Add("DoubleAmount", typeof(decimal));
                foreach (DataRow row in dt.Rows)
                    row["DoubleAmount"] = (decimal)row["Amount"] * 2;
                return dt;
            }
        };
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        await executor.ExecuteAsync(config, watermark);

        capturedBatch.Should().NotBeNull();
        capturedBatch!.Columns.Contains("DoubleAmount").Should().BeTrue();
        var firstRow = capturedBatch.Rows[0];
        ((decimal)firstRow["DoubleAmount"]).Should().Be((decimal)firstRow["Amount"] * 2);
    }
}
