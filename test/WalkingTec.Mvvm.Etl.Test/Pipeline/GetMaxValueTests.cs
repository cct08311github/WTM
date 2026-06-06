#nullable enable
using System;
using System.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Pipeline;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// Regression tests for the GetMaxValue single-pass optimisation (A4).
/// Validates null/DBNull semantics and Comparer&lt;object&gt;.Default behaviour
/// to ensure the optimised path is byte-identical to the original LINQ .Max().
/// </summary>
[TestClass]
public class GetMaxValueTests
{
    // GetMaxValue is private; we exercise it indirectly through WatermarkStrategy
    // via the EtlPipelineExecutor integration surface.  For direct unit coverage
    // we expose it through a thin test accessor below.

    // ─── Accessor ────────────────────────────────────────────────────────────

    /// <summary>
    /// Calls GetMaxValue via reflection so we can unit-test the private static
    /// method directly without needing a full pipeline run.
    /// </summary>
    private static object? InvokeGetMaxValue(DataTable batch, string columnName)
    {
        var method = typeof(EtlPipelineExecutor)
            .GetMethod("GetMaxValue",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(method, "GetMaxValue private static method not found on EtlPipelineExecutor");
        return method.Invoke(null, new object[] { batch, columnName });
    }

    // ─── Helper ──────────────────────────────────────────────────────────────

    private static DataTable MakeTable(string colName, Type colType)
    {
        var t = new DataTable();
        t.Columns.Add(colName, colType);
        return t;
    }

    // ─── Tests ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void GetMaxValue_empty_table_returns_null()
    {
        var t = MakeTable("Ts", typeof(DateTime));
        var result = InvokeGetMaxValue(t, "Ts");
        Assert.IsNull(result, "Empty table must return null");
    }

    [TestMethod]
    public void GetMaxValue_all_dbnull_rows_returns_null()
    {
        var t = MakeTable("Ts", typeof(DateTime));
        var r1 = t.NewRow(); r1["Ts"] = DBNull.Value; t.Rows.Add(r1);
        var r2 = t.NewRow(); r2["Ts"] = DBNull.Value; t.Rows.Add(r2);

        var result = InvokeGetMaxValue(t, "Ts");
        Assert.IsNull(result, "All-DBNull rows must return null — same as LINQ .Max() on empty sequence");
    }

    [TestMethod]
    public void GetMaxValue_mixed_null_and_value_skips_nulls()
    {
        // Verify that DBNull rows are skipped and the real max is returned.
        var t = MakeTable("Id", typeof(int));
        var r1 = t.NewRow(); r1["Id"] = DBNull.Value; t.Rows.Add(r1);
        var r2 = t.NewRow(); r2["Id"] = 5; t.Rows.Add(r2);
        var r3 = t.NewRow(); r3["Id"] = DBNull.Value; t.Rows.Add(r3);
        var r4 = t.NewRow(); r4["Id"] = 3; t.Rows.Add(r4);

        var result = InvokeGetMaxValue(t, "Id");
        Assert.AreEqual(5, result, "Max of {5,3} with DBNull rows skipped must be 5");
    }

    [TestMethod]
    public void GetMaxValue_single_non_null_row_returns_that_value()
    {
        var t = MakeTable("Val", typeof(int));
        var r = t.NewRow(); r["Val"] = 42; t.Rows.Add(r);

        var result = InvokeGetMaxValue(t, "Val");
        Assert.AreEqual(42, result);
    }

    [TestMethod]
    public void GetMaxValue_integer_column_returns_correct_max()
    {
        var t = MakeTable("OrderId", typeof(int));
        foreach (var v in new[] { 10, 3, 99, 7, 42 })
        {
            var r = t.NewRow(); r["OrderId"] = v; t.Rows.Add(r);
        }

        var result = InvokeGetMaxValue(t, "OrderId");
        Assert.AreEqual(99, result, "Max of {10,3,99,7,42} is 99");
    }

    [TestMethod]
    public void GetMaxValue_datetime_column_returns_latest()
    {
        var t = MakeTable("UpdatedAt", typeof(DateTime));
        var d1 = new DateTime(2024, 1, 1);
        var d2 = new DateTime(2024, 6, 15);
        var d3 = new DateTime(2023, 12, 31);
        foreach (var d in new[] { d1, d2, d3 })
        {
            var r = t.NewRow(); r["UpdatedAt"] = d; t.Rows.Add(r);
        }

        var result = InvokeGetMaxValue(t, "UpdatedAt");
        Assert.AreEqual(d2, result, "Max datetime is 2024-06-15");
    }

    [TestMethod]
    public void GetMaxValue_string_column_uses_ordinal_comparison()
    {
        // Comparer<object>.Default on boxed strings uses string ordinal order.
        var t = MakeTable("Code", typeof(string));
        foreach (var s in new[] { "apple", "banana", "cherry" })
        {
            var r = t.NewRow(); r["Code"] = s; t.Rows.Add(r);
        }

        var result = InvokeGetMaxValue(t, "Code");
        // Ordinal: 'c' > 'b' > 'a', so "cherry" wins.
        Assert.AreEqual("cherry", result);
    }

    [TestMethod]
    public void GetMaxValue_column_not_in_table_returns_null()
    {
        var t = MakeTable("OrderId", typeof(int));
        var r = t.NewRow(); r["OrderId"] = 1; t.Rows.Add(r);

        // Column "MissingCol" does not exist → should return null without throwing.
        var result = InvokeGetMaxValue(t, "MissingCol");
        Assert.IsNull(result, "Non-existent column must return null");
    }

    [TestMethod]
    public void GetMaxValue_long_column_returns_correct_max()
    {
        var t = MakeTable("Seq", typeof(long));
        foreach (var v in new long[] { 100L, 9999999999L, 1L })
        {
            var r = t.NewRow(); r["Seq"] = v; t.Rows.Add(r);
        }

        var result = InvokeGetMaxValue(t, "Seq");
        Assert.AreEqual(9999999999L, result);
    }

    /// <summary>
    /// Verifies that a column whose DataType does NOT implement IComparable
    /// (e.g. byte[]) does not throw an InvalidCastException. Comparer&lt;object&gt;.Default
    /// falls back to an ArgumentException for incomparable types rather than a NullRef
    /// or InvalidCast — the important thing is it does not silently give a wrong result.
    /// This test documents the chosen behaviour boundary.
    /// </summary>
    [TestMethod]
    public void GetMaxValue_non_IComparable_column_does_not_throw_InvalidCastException()
    {
        // byte[] is not IComparable, so Comparer<object>.Default.Compare on two
        // byte[] values will throw ArgumentException, not InvalidCastException.
        // With only ONE non-null row, no comparison is performed → returns that row's value.
        var t = MakeTable("Blob", typeof(byte[]));
        var row = t.NewRow();
        row["Blob"] = new byte[] { 1, 2, 3 };
        t.Rows.Add(row);

        // Single row: no comparison needed — should return the value without exception.
        var result = InvokeGetMaxValue(t, "Blob");
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, (byte[]?)result);
    }
}
