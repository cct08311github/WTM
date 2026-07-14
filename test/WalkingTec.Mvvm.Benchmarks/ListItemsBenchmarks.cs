#nullable enable
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Benchmarks
{
    public class ListItemsSampleRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// Perf(#663): ListExtension.ToListItems used to call textField/valueField/
    /// selectedCondition.Compile() once PER ROW inside the foreach instead of once per
    /// call. Uncached_* below inlines that original per-row-Compile() shape; Cached_*
    /// calls the fixed extension method (Compile() hoisted above the loop).
    /// </summary>
    [ShortRunJob]
    [MemoryDiagnoser]
    public class ListItemsBenchmarks
    {
        private List<ListItemsSampleRow> _rows = null!;

        private static readonly Expression<Func<ListItemsSampleRow, object>> _textField = x => x.Name;
        private static readonly Expression<Func<ListItemsSampleRow, object>> _valueField = x => x.Id;
        private static readonly Expression<Func<ListItemsSampleRow, bool>> _selectedCondition = x => x.Id == 1;

        [Params(10, 200)]
        public int RowCount { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _rows = [];
            for (int i = 0; i < RowCount; i++)
            {
                _rows.Add(new ListItemsSampleRow { Id = i, Name = $"Row {i}" });
            }
        }

        [Benchmark(Baseline = true)]
        public List<ComboSelectListItem> Uncached_ToListItems()
        {
            // Inline original logic: Compile() called once per row (pre-#663 fix).
            List<ComboSelectListItem> rv = [];
            foreach (var item in _rows)
            {
                string text = _textField.Compile().Invoke(item)?.ToString() ?? "";
                string value = _valueField.Compile().Invoke(item)?.ToString() ?? "";
                ComboSelectListItem li = new() { Text = text, Value = value };
                if (_selectedCondition.Compile().Invoke(item))
                {
                    li.Selected = true;
                }
                rv.Add(li);
            }
            return rv;
        }

        [Benchmark]
        public List<ComboSelectListItem> Cached_ToListItems()
        {
            return _rows.ToListItems(_textField, _valueField, _selectedCondition);
        }
    }
}
