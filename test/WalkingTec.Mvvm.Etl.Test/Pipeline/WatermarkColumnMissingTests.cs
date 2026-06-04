#nullable enable
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
/// Tests for L16/#153: when the configured WatermarkColumn is absent from the
/// batch schema the executor must record a warning in
/// <see cref="EtlExecutionResult.ValidationWarnings"/> and complete with
/// <see cref="EtlExecutionResult.Success"/> = true (warn, do not crash).
/// Without this fix the watermark silently freezes and the next run
/// re-processes already-ingested rows forever while still reporting Success.
/// </summary>
[TestClass]
public class WatermarkColumnMissingTests
{
    private MockEtlSource _source = null!;
    private MockBulkLoader _loader = null!;

    [TestInitialize]
    public void Setup()
    {
        _source = new MockEtlSource();
        _loader = new MockBulkLoader();
    }

    // ─── Core regression: missing watermark column emits a warning ─────────

    [TestMethod]
    public async Task Missing_watermark_column_records_warning_in_result()
    {
        // Batch has OrderNo + Amount but NOT the configured watermark column "UpdatedAt".
        var dt = new DataTable();
        dt.Columns.Add("OrderNo", typeof(string));
        dt.Columns.Add("Amount", typeof(decimal));
        for (int i = 0; i < 10; i++)
        {
            var row = dt.NewRow();
            row["OrderNo"] = $"ORD-{i:D4}";
            row["Amount"] = 100m + i;
            dt.Rows.Add(row);
        }

        _source.SetData(dt);
        // WatermarkColumn is "UpdatedAt" but the batch has no such column.
        var config = TestHelpers.CreateTestConfig();
        var watermark = new WatermarkStrategy(EtlWatermarkType.Timestamp, "UpdatedAt", null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue("run should succeed — missing watermark column is a warning, not a fatal error");
        result.ValidationWarnings.Should().NotBeEmpty("at least one warning must be recorded for the missing column");
        result.ValidationWarnings.Any(w => w.Contains("UpdatedAt"))
            .Should().BeTrue("the warning must name the missing column 'UpdatedAt'");
    }

    [TestMethod]
    public async Task Missing_watermark_column_run_still_loads_data()
    {
        // Even with a missing watermark column, loaded rows should still be counted.
        var dt = new DataTable();
        dt.Columns.Add("OrderNo", typeof(string));
        dt.Columns.Add("Amount", typeof(decimal));
        for (int i = 0; i < 5; i++)
        {
            var row = dt.NewRow();
            row["OrderNo"] = $"ORD-{i:D4}";
            row["Amount"] = 50m + i;
            dt.Rows.Add(row);
        }

        _source.SetData(dt);
        var config = TestHelpers.CreateTestConfig();
        var watermark = new WatermarkStrategy(EtlWatermarkType.Timestamp, "UpdatedAt", null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue();
        result.LoadedRows.Should().Be(5, "data must still be loaded even when watermark column is absent");
        _loader.TotalRows.Should().Be(5);
    }

    [TestMethod]
    public async Task Present_watermark_column_produces_no_warning()
    {
        // Sanity-check: happy path must not emit any watermark-column warning.
        _source.SetData(TestHelpers.GenerateOrderData(10));
        var config = TestHelpers.CreateTestConfig();
        // "UpdatedAt" IS present in GenerateOrderData schema.
        var watermark = new WatermarkStrategy(EtlWatermarkType.Timestamp, "UpdatedAt", null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue();
        result.ValidationWarnings
            .Where(w => w.Contains("WatermarkColumn") || w.Contains("UpdatedAt"))
            .Should().BeEmpty("no warning expected when the column exists in the batch");
    }

    [TestMethod]
    public async Task FullLoad_watermark_type_does_not_produce_warning_even_if_column_absent()
    {
        // FullLoad never updates watermark so the column-presence check must be skipped.
        var dt = new DataTable();
        dt.Columns.Add("OrderNo", typeof(string));
        for (int i = 0; i < 5; i++)
        {
            var row = dt.NewRow();
            row["OrderNo"] = $"ORD-{i:D4}";
            dt.Rows.Add(row);
        }

        _source.SetData(dt);
        var config = TestHelpers.CreateTestConfig();
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, "UpdatedAt", null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue();
        result.ValidationWarnings
            .Where(w => w.Contains("WatermarkColumn") || w.Contains("UpdatedAt"))
            .Should().BeEmpty("FullLoad does not use a watermark column; no warning should be raised");
    }
}
