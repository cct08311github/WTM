#nullable enable
using System.Data;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Testing;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// End-to-end wiring tests for #680: <see cref="EtlColumnNameValidator"/> must
/// run inside <see cref="EtlPipelineExecutor"/> BEFORE the first
/// <see cref="IBulkLoader.BulkLoadAsync"/> call, using the actual extracted
/// batch schema (as CSV/Excel headers or REST JSON keys would surface it when
/// <see cref="EtlPipelineConfig.ColumnMappings"/> is not configured).
/// </summary>
[TestClass]
public class ColumnNameValidationPipelineTests
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
    public async Task Execute_rejects_hostile_column_name_before_first_bulk_load()
    {
        // Simulates a CSV/REST source whose header contains characters that
        // would otherwise be embedded unescaped/under-escaped into
        // loader-generated SQL (MSSQL bracket-close, Oracle unquoted path).
        const string hostileColumn = "Amount]; DROP TABLE X--";

        var hostileData = new DataTable();
        hostileData.Columns.Add("OrderNo", typeof(string));
        hostileData.Columns.Add(hostileColumn, typeof(decimal));
        var row = hostileData.NewRow();
        row["OrderNo"] = "ORD-001";
        row[hostileColumn] = 1m;
        hostileData.Rows.Add(row);

        _source.SetData(hostileData);
        var config = TestHelpers.CreateTestConfig();
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeFalse("a hostile column name must fail the run, not silently strip/rename it");
        result.ErrorMessage.Should().Contain("not allowed");
        _loader.LoadedBatches.Should().BeEmpty(
            "validation must run BEFORE the first BulkLoadAsync call, so no batch ever reaches the loader");
        _loader.MergeCalled.Should().BeFalse("merge must never run after a rejected batch");
    }

    [TestMethod]
    public async Task Execute_succeeds_for_legal_column_names()
    {
        _source.SetData(TestHelpers.GenerateOrderData(10));
        var config = TestHelpers.CreateTestConfig();
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeTrue("OrderNo/Amount/UpdatedAt all conform to the allowlist");
        _loader.LoadedBatches.Should().NotBeEmpty();
        _loader.MergeCalled.Should().BeTrue();
    }

    [TestMethod]
    public async Task Execute_rejects_space_in_column_name()
    {
        var data = new DataTable();
        data.Columns.Add("Order No", typeof(string)); // space — REST/Excel headers commonly have these
        var row = data.NewRow();
        row["Order No"] = "ORD-001";
        data.Rows.Add(row);

        _source.SetData(data);
        var config = TestHelpers.CreateTestConfig();
        var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        var executor = new EtlPipelineExecutor(_source, _loader);

        var result = await executor.ExecuteAsync(config, watermark);

        result.Success.Should().BeFalse();
        _loader.LoadedBatches.Should().BeEmpty();
    }
}
