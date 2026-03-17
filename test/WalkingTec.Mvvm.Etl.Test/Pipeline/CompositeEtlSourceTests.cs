#nullable enable
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Pipeline.Sources;
using WalkingTec.Mvvm.Etl.Testing;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

[TestClass]
public class CompositeEtlSourceTests
{
    private static MockEtlSource MakeSource(params (int id, string name)[] rows)
    {
        var dt = new DataTable();
        dt.Columns.Add("Id",   typeof(int));
        dt.Columns.Add("Name", typeof(string));
        foreach (var (id, name) in rows)
            dt.Rows.Add(id, name);

        var src = new MockEtlSource();
        src.SetData(dt);
        return src;
    }

    [TestMethod]
    public async Task Composite_unions_batches_from_two_sources_in_order()
    {
        var src1 = MakeSource((1, "Alice"), (2, "Bob"));
        var src2 = MakeSource((3, "Charlie"));

        using var composite = new CompositeEtlSource(new[]
        {
            ("cs1", "SELECT * FROM T1", (WalkingTec.Mvvm.Etl.Pipeline.IEtlSource)src1),
            ("cs2", "SELECT * FROM T2", (WalkingTec.Mvvm.Etl.Pipeline.IEtlSource)src2),
        });

        var all = new List<DataRow>();
        await foreach (var batch in composite.ExtractBatchesAsync("", "", null, 100))
            foreach (DataRow row in batch.Rows)
                all.Add(row);

        Assert.AreEqual(3, all.Count, "composite should yield all rows from both sources");
        Assert.AreEqual(1, all[0]["Id"], "first row from source 1");
        Assert.AreEqual(2, all[1]["Id"], "second row from source 1");
        Assert.AreEqual(3, all[2]["Id"], "row from source 2 appended last");
    }

    [TestMethod]
    public async Task Composite_with_single_source_behaves_as_passthrough()
    {
        var src = MakeSource((10, "X"), (20, "Y"));

        using var composite = new CompositeEtlSource(new[]
        {
            ("cs", "SELECT * FROM T", (WalkingTec.Mvvm.Etl.Pipeline.IEtlSource)src),
        });

        var count = 0;
        await foreach (var batch in composite.ExtractBatchesAsync("", "", null, 100))
            count += batch.Rows.Count;

        Assert.AreEqual(2, count);
    }

    [TestMethod]
    public async Task Composite_with_empty_source_list_yields_nothing()
    {
        using var composite = new CompositeEtlSource(
            System.Array.Empty<(string, string, WalkingTec.Mvvm.Etl.Pipeline.IEtlSource)>());

        var count = 0;
        await foreach (var batch in composite.ExtractBatchesAsync("", "", null, 100))
            count += batch.Rows.Count;

        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public async Task Composite_top_level_connection_and_query_are_ignored()
    {
        // The inner source uses its own cs/query; top-level params should be irrelevant
        var src = MakeSource((99, "Z"));

        using var composite = new CompositeEtlSource(new[]
        {
            ("real-cs", "SELECT * FROM T", (WalkingTec.Mvvm.Etl.Pipeline.IEtlSource)src),
        });

        var count = 0;
        await foreach (var batch in composite.ExtractBatchesAsync(
            "IGNORED-CS", "IGNORED-QUERY", null, 100))
            count += batch.Rows.Count;

        Assert.AreEqual(1, count, "inner source should be used regardless of top-level params");
    }

    [TestMethod]
    public async Task Composite_respects_batch_size_from_inner_sources()
    {
        var src = MakeSource((1, "A"), (2, "B"), (3, "C"), (4, "D"), (5, "E"));

        using var composite = new CompositeEtlSource(new[]
        {
            ("cs", "SELECT * FROM T", (WalkingTec.Mvvm.Etl.Pipeline.IEtlSource)src),
        });

        var batches = new List<DataTable>();
        await foreach (var batch in composite.ExtractBatchesAsync("", "", null, batchSize: 2))
            batches.Add(batch);

        Assert.AreEqual(3, batches.Count, "5 rows / batchSize 2 → 3 batches");
        Assert.AreEqual(2, batches[0].Rows.Count);
        Assert.AreEqual(2, batches[1].Rows.Count);
        Assert.AreEqual(1, batches[2].Rows.Count);
    }
}
