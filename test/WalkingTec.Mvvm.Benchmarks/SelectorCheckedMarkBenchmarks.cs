#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Helper;

namespace WalkingTec.Mvvm.Benchmarks
{
    public class SelectorBenchRow
    {
        public Guid ID { get; set; }
        public bool Checked { get; set; }
    }

    /// <summary>
    /// Perf(#705): <c>BasePagedListVM.AfterDoSearcher</c>'s Selector-mode "mark checked rows"
    /// loop previously ran <c>Ids.Contains(id)</c> (a linear scan of a <see cref="List{T}"/>,
    /// so O(rows × ids)) PLUS a fresh reflection property lookup
    /// (<c>PropertyInfo</c>.GetValue via <c>GetSingleProperty(...).GetValue(...)</c>, not
    /// cached) for EVERY row. <c>Uncached_ListContainsPlusReflection</c> reproduces that
    /// pre-#705 shape; <c>Cached_HashSetPlusCompiledGetter</c> reproduces the fix — a
    /// <see cref="HashSet{T}"/> id lookup (same equality semantics as
    /// <c>List&lt;string&gt;.Contains</c>'s default <c>EqualityComparer&lt;string&gt;.Default</c>)
    /// plus a compiled getter obtained once via the REAL
    /// <see cref="PropertyHelper.GetPropertyExpression"/> API (cached internally by
    /// <c>ReflectionCache.PropertyAccessors</c>) and reused for every row.
    /// </summary>
    [ShortRunJob]
    [MemoryDiagnoser]
    public class SelectorCheckedMarkBenchmarks
    {
        private const int RowCount = 5000;
        private const int IdCount = 500;

        private List<SelectorBenchRow> _rows = null!;
        private List<string> _idsAsList = null!;

        [GlobalSetup]
        public void Setup()
        {
            _rows = new List<SelectorBenchRow>(RowCount);
            for (int i = 0; i < RowCount; i++)
            {
                _rows.Add(new SelectorBenchRow { ID = Guid.NewGuid() });
            }

            // Every 10th row's id is one of the "selected" ids, so both benchmarks do a
            // realistic mix of hits and misses.
            _idsAsList = new List<string>(IdCount);
            for (int i = 0; i < RowCount; i += RowCount / IdCount)
            {
                _idsAsList.Add(_rows[i].ID.ToString());
            }

            // Pre-warm the compiled-getter cache used by the Cached_* benchmark.
            PropertyHelper.GetPropertyExpression(typeof(SelectorBenchRow), "ID");
        }

        /// <summary>Pre-#705 shape: List&lt;string&gt;.Contains scan + fresh reflection GetValue, per row.</summary>
        [Benchmark(Baseline = true)]
        public int Uncached_ListContainsPlusReflection()
        {
            int checkedCount = 0;
            foreach (var item in _rows)
            {
                // Reproduces item.GetID() → GetSingleProperty("ID").GetValue(item): a fresh
                // PropertyInfo lookup + reflection invoke on every row.
                var idProp = item.GetType().GetProperty("ID")!;
                var id = idProp.GetValue(item)!;
                if (_idsAsList.Contains(id.ToString()!))
                {
                    item.Checked = true;
                    checkedCount++;
                }
                else
                {
                    item.Checked = false;
                }
            }
            return checkedCount;
        }

        /// <summary>#705 fix: HashSet id lookup + compiled getter cached once, reused per row.</summary>
        [Benchmark]
        public int Cached_HashSetPlusCompiledGetter()
        {
            var idSet = new HashSet<string>(_idsAsList);
            var getter = PropertyHelper.GetPropertyExpression(typeof(SelectorBenchRow), "ID");

            int checkedCount = 0;
            foreach (var item in _rows)
            {
                var v = getter(item);
                if (v != null && idSet.Contains(v.ToString()!))
                {
                    item.Checked = true;
                    checkedCount++;
                }
                else
                {
                    item.Checked = false;
                }
            }
            return checkedCount;
        }
    }
}
