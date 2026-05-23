#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Helper;

namespace WalkingTec.Mvvm.Benchmarks
{
    public enum SaleRegion
    {
        [System.ComponentModel.DataAnnotations.Display(Name = "北区")]
        North,
        [System.ComponentModel.DataAnnotations.Display(Name = "南区")]
        South,
        East,
        West
    }

    public class SaleBenchmarkModel
    {
        [Dimension(DisplayName = "地区")]
        public SaleRegion Region { get; set; }

        [Dimension(DisplayName = "日期")]
        public DateTime OrderDate { get; set; }

        [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count, DisplayName = "金额")]
        public decimal Amount { get; set; }

        [Measure(AllowedFuncs = AggregateFunc.Avg, DisplayName = "单价")]
        public double UnitPrice { get; set; }
    }

    [ShortRunJob]
    [MemoryDiagnoser]
    public class AnalysisFieldScannerBenchmarks
    {
        [GlobalSetup]
        public void Setup()
        {
            // Pre-warm the cache for Cached_* benchmarks
            AnalysisFieldScanner.ScanModel(typeof(SaleBenchmarkModel)).ToList();
        }

        [Benchmark(Baseline = true)]
        public List<AnalysisFieldMeta> Uncached_Scan()
        {
            // Inline original logic without caching
            var results = new List<AnalysisFieldMeta>();
            var modelType = typeof(SaleBenchmarkModel);
            foreach (var prop in modelType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var dim = prop.GetCustomAttribute<DimensionAttribute>();
                if (dim != null)
                {
                    var clrType = prop.PropertyType;
                    var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;
                    var isDate = underlying == typeof(DateTime);

                    IReadOnlyList<string>? allowedValues = null;
                    if (underlying.IsEnum)
                    {
                        allowedValues = [.. Enum.GetValues(underlying)
                            .Cast<Enum>()
                            .Select(e => {
                                var f = underlying.GetField(e.ToString());
                                if (f != null)
                                {
                                    var a = f.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.DisplayAttribute), true)
                                             .Cast<System.ComponentModel.DataAnnotations.DisplayAttribute>()
                                             .FirstOrDefault();
                                    return a?.GetName() ?? e.ToString();
                                }
                                return e.ToString();
                            })];
                    }

                    results.Add(new AnalysisFieldMeta
                    {
                        FieldName = prop.Name,
                        DisplayName = dim.DisplayName ?? prop.Name,
                        Kind = AnalysisFieldKind.Dimension,
                        ClrType = clrType,
                        IsDate = isDate,
                        Hierarchy = isDate ? dim.Hierarchy : DateHierarchy.None,
                        AllowedRoles = dim.AllowedRoles,
                        AllowedValues = allowedValues
                    });
                    continue;
                }

                var msr = prop.GetCustomAttribute<MeasureAttribute>();
                if (msr != null)
                {
                    results.Add(new AnalysisFieldMeta
                    {
                        FieldName = prop.Name,
                        DisplayName = msr.DisplayName ?? prop.Name,
                        Kind = AnalysisFieldKind.Measure,
                        AllowedFuncs = msr.AllowedFuncs,
                        ClrType = prop.PropertyType,
                        AllowedRoles = msr.AllowedRoles,
                        Format = msr.Format
                    });
                }
            }
            return results;
        }

        [Benchmark]
        public List<AnalysisFieldMeta> Cached_Scan()
        {
            return AnalysisFieldScanner.ScanModel(typeof(SaleBenchmarkModel)).ToList();
        }
    }
}
