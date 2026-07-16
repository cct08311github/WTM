#nullable enable
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using BenchmarkDotNet.Attributes;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Helper;

namespace WalkingTec.Mvvm.Benchmarks
{
    /// <summary>~15-property import row model, mirroring a typical Excel-import target.</summary>
    public class ImportValidationSampleModel
    {
        [Required] public string P1 { get; set; } = "";
        [StringLength(50)] public string P2 { get; set; } = "";
        [Range(0, 100)] public int P3 { get; set; }
        [Required] public string P4 { get; set; } = "";
        public string P5 { get; set; } = "";
        [StringLength(20)] public string P6 { get; set; } = "";
        public int P7 { get; set; }
        [Range(0, 1000)] public decimal P8 { get; set; }
        public string P9 { get; set; } = "";
        [Required] public string P10 { get; set; } = "";
        public string P11 { get; set; } = "";
        public int P12 { get; set; }
        [StringLength(100)] public string P13 { get; set; } = "";
        public bool P14 { get; set; }
        public string P15 { get; set; } = "";
    }

    /// <summary>
    /// Perf(#674): <c>BaseImportVM.TryValidateObject</c>/<c>TryValidateProperty</c> called
    /// <c>modelType.GetProperties()</c> plus a per-property <c>GetCustomAttributes(true)</c>
    /// scan (which re-instantiates attribute objects) on EVERY imported row.
    /// <c>Uncached_PerRowReflection</c> reproduces that pre-#674 shape (including the exact
    /// <c>i.GetType().BaseType == typeof(ValidationAttribute)</c> filter).
    /// <c>Cached_PerTypeReflectionCache</c> reproduces the fix — a per-model-type cache of
    /// (PropertyInfo, ValidationAttribute[]) built once via
    /// <see cref="ReflectionCache.ImportValidationInfos"/> and reused across rows. Both
    /// benchmarks evaluate the exact same rules against the exact same values, so the fix
    /// is behaviour-identical (same validation results), only faster.
    /// </summary>
    [ShortRunJob]
    [MemoryDiagnoser]
    public class BaseImportValidationBenchmarks
    {
        private const int RowCount = 10_000;

        private List<ImportValidationSampleModel> _rows = null!;

        [GlobalSetup]
        public void Setup()
        {
            _rows = new List<ImportValidationSampleModel>(RowCount);
            for (int i = 0; i < RowCount; i++)
            {
                _rows.Add(new ImportValidationSampleModel
                {
                    P1 = "x",
                    P2 = "abc",
                    P3 = 5,
                    P4 = "y",
                    P8 = 12.5m,
                    P10 = "z",
                    P13 = "abc",
                });
            }
        }

        /// <summary>Pre-#674 shape: GetProperties() + GetCustomAttributes(true) on EVERY row.</summary>
        [Benchmark(Baseline = true)]
        public int Uncached_PerRowReflection()
        {
            int total = 0;
            var modelType = typeof(ImportValidationSampleModel);
            foreach (var row in _rows)
            {
                foreach (var p in modelType.GetProperties())
                {
                    var rules = p.GetCustomAttributes(true)
                        .Where(i => i.GetType().BaseType == typeof(ValidationAttribute))
                        .Cast<ValidationAttribute>();
                    var displayName = p.GetPropertyDisplayName();
                    var value = p.GetValue(row);
                    foreach (var rule in rules)
                    {
                        if (!rule.IsValid(value))
                        {
                            total++;
                        }
                    }
                }
            }
            return total;
        }

        /// <summary>#674 fix: per-type cache built once, reused across all rows.</summary>
        [Benchmark]
        public int Cached_PerTypeReflectionCache()
        {
            int total = 0;
            var modelType = typeof(ImportValidationSampleModel);
            var infos = ReflectionCache.ImportValidationInfos.GetOrAdd(modelType, static t =>
                [.. t.GetProperties().Select(p => new ImportPropertyValidationInfo(
                    p,
                    [.. p.GetCustomAttributes(true).Where(i => i.GetType().BaseType == typeof(ValidationAttribute)).Cast<ValidationAttribute>()]))]);

            foreach (var row in _rows)
            {
                foreach (var info in infos)
                {
                    var displayName = info.Property.GetPropertyDisplayName();
                    var value = info.Property.GetValue(row);
                    foreach (var rule in info.Rules)
                    {
                        if (!rule.IsValid(value))
                        {
                            total++;
                        }
                    }
                }
            }
            return total;
        }
    }
}
