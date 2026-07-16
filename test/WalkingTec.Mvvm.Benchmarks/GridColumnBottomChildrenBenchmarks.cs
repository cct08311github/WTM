#nullable enable
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Benchmarks
{
    public class GridColumnBenchEntity : TopBasePoco
    {
    }

    /// <summary>
    /// Perf(#674): <see cref="IGridColumn{T}.BottomChildren"/> recursively rebuilds a NEW
    /// <see cref="List{T}"/> on EVERY access. Before the fix, the grid-JSON row loop
    /// (<c>ListVMExtension.GetSingleDataJson</c>/<c>GetDataJson</c>) and all three Excel/CSV
    /// export row loops in <c>BasePagedListVM</c> re-flattened the header tree once PER ROW
    /// instead of once for the whole operation. <c>Uncached_PerRowReflatten</c> reproduces
    /// that pre-#674 shape; <c>Cached_HoistedFlatten</c> reproduces the fix — flatten once,
    /// reuse the flattened list across all rows. Both benchmarks touch the SAME columns in
    /// the SAME order, so the fix is behaviour-identical, only faster.
    /// </summary>
    [ShortRunJob]
    [MemoryDiagnoser]
    public class GridColumnBottomChildrenBenchmarks
    {
        private const int ColumnCount = 1000;
        private const int RowCount = 1000;

        private List<IGridColumn<GridColumnBenchEntity>> _headers = null!;

        [GlobalSetup]
        public void Setup()
        {
            // 1,000 leaf columns directly under the root — the worst-case shape for
            // BottomChildren's per-node List allocation (each leaf's BottomChildren
            // access allocates a fresh single-element List).
            _headers = new List<IGridColumn<GridColumnBenchEntity>>(ColumnCount);
            for (int i = 0; i < ColumnCount; i++)
            {
                _headers.Add(new GridColumn<GridColumnBenchEntity>(x => x.ID, null) { Field = $"F{i}" });
            }
        }

        /// <summary>Pre-#674 shape: BottomChildren re-walked/re-allocated once PER ROW.</summary>
        [Benchmark(Baseline = true)]
        public int Uncached_PerRowReflatten()
        {
            int total = 0;
            for (int row = 0; row < RowCount; row++)
            {
                foreach (var baseCol in _headers)
                {
                    foreach (var col in baseCol.BottomChildren)
                    {
                        total += col.Field!.Length;
                    }
                }
            }
            return total;
        }

        /// <summary>#674 fix: flatten ONCE, reuse the flattened list across all rows.</summary>
        [Benchmark]
        public int Cached_HoistedFlatten()
        {
            int total = 0;
            var flat = _headers.SelectMany(h => h.BottomChildren).ToList();
            for (int row = 0; row < RowCount; row++)
            {
                foreach (var col in flat)
                {
                    total += col.Field!.Length;
                }
            }
            return total;
        }
    }
}
