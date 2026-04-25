#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Testing;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// EtlPipelineConfig.ColumnMappings: rename + drop columns at the
/// staging-load boundary so source schema (cust_id) can land in
/// target schema (CustomerID). Verifies the pure ApplyColumnMappings
/// helper and the executor wiring (with retry-budget compatibility).
/// </summary>
[TestClass]
public class ColumnMappingsTests
{
    // ── Pure ApplyColumnMappings ───────────────────────────────────────

    [TestMethod]
    public void ApplyColumnMappings_renames_columns_and_keeps_data()
    {
        var src = new DataTable();
        src.Columns.Add("cust_id", typeof(int));
        src.Columns.Add("order_no", typeof(string));
        src.Rows.Add(7, "ORD-001");

        var mapped = EtlPipelineExecutor.ApplyColumnMappings(src, new Dictionary<string, string>
        {
            ["cust_id"] = "CustomerID",
            ["order_no"] = "OrderNumber",
        });

        Assert.AreEqual(2, mapped.Columns.Count);
        Assert.AreEqual("CustomerID", mapped.Columns[0].ColumnName);
        Assert.AreEqual("OrderNumber", mapped.Columns[1].ColumnName);
        Assert.AreEqual(7, mapped.Rows[0]["CustomerID"]);
        Assert.AreEqual("ORD-001", mapped.Rows[0]["OrderNumber"]);
        Assert.AreEqual(typeof(int), mapped.Columns["CustomerID"]!.DataType);
    }

    [TestMethod]
    public void ApplyColumnMappings_drops_unmapped_source_columns()
    {
        var src = new DataTable();
        src.Columns.Add("keep_me", typeof(string));
        src.Columns.Add("drop_me_secret", typeof(string));
        src.Rows.Add("visible", "internal");

        var mapped = EtlPipelineExecutor.ApplyColumnMappings(src, new Dictionary<string, string>
        {
            ["keep_me"] = "Visible",
            // drop_me_secret intentionally omitted from mapping
        });

        Assert.AreEqual(1, mapped.Columns.Count);
        Assert.AreEqual("Visible", mapped.Columns[0].ColumnName);
        Assert.IsFalse(mapped.Columns.Contains("drop_me_secret"));
    }

    [TestMethod]
    public void ApplyColumnMappings_unknown_source_column_emits_DBNull()
    {
        // Operator declared a target column but the source batch
        // doesn't carry it (older API version, conditional column).
        // Output should still have the column, filled with DBNull.
        var src = new DataTable();
        src.Columns.Add("cust_id", typeof(int));
        src.Rows.Add(42);

        var mapped = EtlPipelineExecutor.ApplyColumnMappings(src, new Dictionary<string, string>
        {
            ["cust_id"] = "CustomerID",
            ["maybe_present"] = "OptionalField",
        });

        Assert.AreEqual(2, mapped.Columns.Count);
        Assert.AreEqual(42, mapped.Rows[0]["CustomerID"]);
        Assert.AreEqual(DBNull.Value, mapped.Rows[0]["OptionalField"]);
    }

    [TestMethod]
    public void ApplyColumnMappings_preserves_mapping_order()
    {
        // Column order in output reflects mapping insertion order so
        // operator gets predictable layout in staging table.
        var src = new DataTable();
        src.Columns.Add("a", typeof(int));
        src.Columns.Add("b", typeof(int));
        src.Columns.Add("c", typeof(int));
        src.Rows.Add(1, 2, 3);

        var mapped = EtlPipelineExecutor.ApplyColumnMappings(src, new Dictionary<string, string>
        {
            ["c"] = "Z",
            ["a"] = "X",
            ["b"] = "Y",
        });

        CollectionAssert.AreEqual(
            new[] { "Z", "X", "Y" },
            mapped.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray());
    }

    [TestMethod]
    public void ApplyColumnMappings_empty_value_throws_ArgumentException()
    {
        var src = new DataTable();
        Assert.ThrowsException<System.ArgumentException>(() =>
            EtlPipelineExecutor.ApplyColumnMappings(src, new Dictionary<string, string>
            {
                [""] = "Tgt",
            }));
        Assert.ThrowsException<System.ArgumentException>(() =>
            EtlPipelineExecutor.ApplyColumnMappings(src, new Dictionary<string, string>
            {
                ["src"] = "",
            }));
    }

    // ── Pipeline wiring ────────────────────────────────────────────────

    [TestMethod]
    public async Task Null_ColumnMappings_keeps_pre_10_5_behavior()
    {
        // Default null → executor sends batch verbatim to BulkLoad,
        // identical wire shape to 10.4.x.
        var src = new MockEtlSource(); src.SetData(TestHelpers.GenerateOrderData(10));
        var loader = new MockBulkLoader();
        var executor = new EtlPipelineExecutor(src, loader);

        var result = await executor.ExecuteAsync(
            TestHelpers.CreateTestConfig() with { BatchSize = 100 },
            new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null));

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, loader.LoadedBatches.Count);
        // Source columns: OrderNo, Amount, UpdatedAt — all preserved
        // exactly when no mapping is set.
        var batch = loader.LoadedBatches[0];
        Assert.IsTrue(batch.Columns.Contains("OrderNo"));
        Assert.IsTrue(batch.Columns.Contains("Amount"));
        Assert.IsTrue(batch.Columns.Contains("UpdatedAt"));
    }

    [TestMethod]
    public async Task ColumnMappings_renames_and_drops_at_load_step()
    {
        var src = new MockEtlSource(); src.SetData(TestHelpers.GenerateOrderData(5));
        var loader = new MockBulkLoader();
        var executor = new EtlPipelineExecutor(src, loader);

        var config = TestHelpers.CreateTestConfig() with
        {
            BatchSize = 100,
            ColumnMappings = new Dictionary<string, string>
            {
                ["OrderNo"] = "OrderNumber",
                ["Amount"] = "TotalAmount",
                // UpdatedAt intentionally dropped
            },
        };

        var result = await executor.ExecuteAsync(
            config, new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null));

        Assert.IsTrue(result.Success);
        var batch = loader.LoadedBatches[0];
        Assert.IsTrue(batch.Columns.Contains("OrderNumber"));
        Assert.IsTrue(batch.Columns.Contains("TotalAmount"));
        Assert.IsFalse(batch.Columns.Contains("UpdatedAt"),
            "Unmapped source column must be dropped at the load boundary.");
        Assert.IsFalse(batch.Columns.Contains("OrderNo"),
            "Source name should be replaced by target name (not both).");
    }

    [TestMethod]
    public async Task ColumnMappings_run_after_TransformFunc()
    {
        // TransformFunc adds a synthetic column; ColumnMappings then
        // forwards it under a different name. Ordering matters: if
        // mapping ran first the synth column wouldn't exist yet.
        var src = new MockEtlSource(); src.SetData(TestHelpers.GenerateOrderData(3));
        var loader = new MockBulkLoader();
        var executor = new EtlPipelineExecutor(src, loader);

        var config = TestHelpers.CreateTestConfig() with
        {
            BatchSize = 100,
            TransformFunc = dt =>
            {
                dt.Columns.Add("synth_col", typeof(string));
                foreach (DataRow row in dt.Rows) { row["synth_col"] = "synthetic"; }
                return dt;
            },
            ColumnMappings = new Dictionary<string, string>
            {
                ["synth_col"] = "Synthesized",
            },
        };

        var result = await executor.ExecuteAsync(
            config, new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null));

        Assert.IsTrue(result.Success);
        var batch = loader.LoadedBatches[0];
        Assert.AreEqual(1, batch.Columns.Count);
        Assert.AreEqual("Synthesized", batch.Columns[0].ColumnName);
        Assert.AreEqual("synthetic", batch.Rows[0]["Synthesized"]);
    }
}
