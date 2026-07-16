#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using BenchmarkDotNet.Attributes;
using WalkingTec.Mvvm.Etl.Pipeline;

namespace WalkingTec.Mvvm.Benchmarks
{
    /// <summary>
    /// Perf(#674): <c>EtlPipelineExecutor.ApplyColumnMappings</c> previously did a
    /// <c>source.Columns.Contains(key)</c> + string-indexed <c>srcRow[key]</c> lookup PER
    /// ROW PER MAPPING — O(rows * mappings) string lookups over the whole batch.
    /// <c>Uncached_StringIndexedLookup</c> reproduces that pre-#674 shape.
    /// <c>Cached_PreResolvedOrdinals</c> calls the actual current
    /// <see cref="EtlPipelineExecutor.ApplyColumnMappings"/> (the #674 fix — source column
    /// ordinals pre-resolved once before the row loop, then indexed by ordinal). Both
    /// produce the identical output <see cref="DataTable"/> (same values, same column
    /// order, same DBNull-for-missing-column semantics), so the fix is
    /// behaviour-identical, only faster.
    /// </summary>
    [ShortRunJob]
    [MemoryDiagnoser]
    public class EtlColumnMappingBenchmarks
    {
        private const int RowCount = 100_000;
        private const int ColumnCount = 20;

        private DataTable _source = null!;
        private Dictionary<string, string> _mappings = null!;

        [GlobalSetup]
        public void Setup()
        {
            _source = new DataTable();
            for (int c = 0; c < ColumnCount; c++)
            {
                _source.Columns.Add($"Src{c}", typeof(string));
            }
            for (int r = 0; r < RowCount; r++)
            {
                var row = _source.NewRow();
                for (int c = 0; c < ColumnCount; c++)
                {
                    row[c] = $"v{r}_{c}";
                }
                _source.Rows.Add(row);
            }

            // Map all but the last source column (which is intentionally unmapped) plus
            // one mapping whose source column does NOT exist, to exercise the
            // DBNull-for-missing-column path exactly like the pre-#674 implementation did.
            _mappings = new Dictionary<string, string>();
            for (int c = 0; c < ColumnCount - 1; c++)
            {
                _mappings[$"Src{c}"] = $"Tgt{c}";
            }
            _mappings["DoesNotExist"] = "TgtMissing";
        }

        /// <summary>Pre-#674 shape: Columns.Contains(key) + string-indexed srcRow[key] PER ROW PER MAPPING.</summary>
        [Benchmark(Baseline = true)]
        public DataTable Uncached_StringIndexedLookup()
        {
            var source = _source;
            var mappings = _mappings;
            var output = new DataTable();
            foreach (var kv in mappings)
            {
                var srcType = source.Columns.Contains(kv.Key)
                    ? source.Columns[kv.Key]!.DataType
                    : typeof(object);
                output.Columns.Add(kv.Value, srcType);
            }

            foreach (DataRow srcRow in source.Rows)
            {
                var newRow = output.NewRow();
                foreach (var kv in mappings)
                {
                    if (source.Columns.Contains(kv.Key))
                    {
                        newRow[kv.Value] = srcRow[kv.Key];
                    }
                    else
                    {
                        newRow[kv.Value] = DBNull.Value;
                    }
                }
                output.Rows.Add(newRow);
            }
            return output;
        }

        /// <summary>#674 fix: source ordinals pre-resolved once, then indexed by ordinal.</summary>
        [Benchmark]
        public DataTable Cached_PreResolvedOrdinals()
        {
            return EtlPipelineExecutor.ApplyColumnMappings(_source, _mappings);
        }
    }
}
