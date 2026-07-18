#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Helper;

namespace WalkingTec.Mvvm.Benchmarks
{
    public class GroupByBenchRow
    {
        public string Region { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    /// <summary>
    /// Perf(#705): <c>InProcessGroupByStrategy.GroupAndAggregate</c>/<c>BuildGroupKey</c>
    /// already pre-resolved dimension/measure <see cref="PropertyInfo"/> ONCE outside the
    /// row loop (a prior optimization), but still called <c>PropertyInfo.GetValue(row)</c> —
    /// a reflection invoke — for every dimension/measure, on EVERY row.
    /// <c>Uncached_PropertyInfoGetValue</c> reproduces that pre-#705 shape;
    /// <c>Cached_CompiledGetter</c> reproduces the fix — a compiled
    /// <see cref="Func{T,TResult}"/> obtained once via the REAL
    /// <see cref="PropertyHelper.GetPropertyExpression"/> API (cached internally by
    /// <c>ReflectionCache.PropertyAccessors</c>) and invoked per row instead.
    /// </summary>
    [ShortRunJob]
    [MemoryDiagnoser]
    public class GroupByGetterBenchmarks
    {
        private const int RowCount = 5000;

        private List<GroupByBenchRow> _rows = null!;
        private PropertyInfo _dimProp = null!;
        private PropertyInfo _measureProp = null!;

        [GlobalSetup]
        public void Setup()
        {
            var regions = new[] { "North", "South", "East", "West" };
            _rows = new List<GroupByBenchRow>(RowCount);
            for (int i = 0; i < RowCount; i++)
            {
                _rows.Add(new GroupByBenchRow { Region = regions[i % regions.Length], Amount = i });
            }

            _dimProp = typeof(GroupByBenchRow).GetProperty("Region")!;
            _measureProp = typeof(GroupByBenchRow).GetProperty("Amount")!;

            // Pre-warm the compiled-getter cache used by the Cached_* benchmark.
            PropertyHelper.GetPropertyExpression(typeof(GroupByBenchRow), "Region");
            PropertyHelper.GetPropertyExpression(typeof(GroupByBenchRow), "Amount");
        }

        /// <summary>Pre-#705 shape: PropertyInfo pre-resolved once, but .GetValue(row) reflection invoke per row.</summary>
        [Benchmark(Baseline = true)]
        public string Uncached_PropertyInfoGetValue()
        {
            string last = string.Empty;
            foreach (var row in _rows)
            {
                var dim = _dimProp.GetValue(row);
                var measure = _measureProp.GetValue(row);
                last = $"{dim}\0{measure}";
            }
            return last;
        }

        /// <summary>#705 fix: compiled getter obtained once, invoked per row instead of PropertyInfo.GetValue.</summary>
        [Benchmark]
        public string Cached_CompiledGetter()
        {
            var dimGetter = PropertyHelper.GetPropertyExpression(typeof(GroupByBenchRow), "Region");
            var measureGetter = PropertyHelper.GetPropertyExpression(typeof(GroupByBenchRow), "Amount");

            string last = string.Empty;
            foreach (var row in _rows)
            {
                var dim = dimGetter(row);
                var measure = measureGetter(row);
                last = $"{dim}\0{measure}";
            }
            return last;
        }
    }
}
