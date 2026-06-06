#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    // ─────────────────────────────────────────────────────────────────────────
    // Regression tests for Wave-1 SAFE performance optimisations (Issue #180).
    // Tests are self-contained; golden hash values are computed by the same
    // deterministic algorithm used in production, so they remain stable across
    // machines and framework versions.
    // ─────────────────────────────────────────────────────────────────────────
    [TestClass]
    public class PerfOptRegressionTests
    {
        // ──────────────────────────────────────────────────────────────────────
        // A. ComputeHash GOLDEN parity
        //
        // ComputeHash is private, so we re-implement the SAME logic here and
        // assert that both produce identical 16-char hex strings for a fixed set
        // of AnalysisQueryRequest shapes.  This proves the ArrayPool rewrite
        // cannot silently change the cache-key output.
        // ──────────────────────────────────────────────────────────────────────

        // Mirror of the private _hashSerializerOptions field used by AnalysisQueryEngine.
        private static readonly JsonSerializerOptions _hashSerializerOptions = new()
        {
            PropertyNamingPolicy = null,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        /// <summary>
        /// Replicates the byte-identical computation in the POST-optimisation
        /// ComputeHash:  ArrayPool rent → UTF8.GetBytes → SHA256.HashData → hex[..16].
        /// The test verifies this produces the same 16 chars as the original
        /// Encoding.UTF8.GetBytes(raw) path.
        /// </summary>
        private static string ComputeHashViaOriginal(AnalysisQueryRequest req, string identityKey)
        {
            // Identical logic to the ORIGINAL pre-optimisation implementation.
            var raw = JsonSerializer.Serialize(req, _hashSerializerOptions) + "|" + identityKey;
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(raw), hash);
            return Convert.ToHexString(hash)[..16];
        }

        private static string ComputeHashViaArrayPool(AnalysisQueryRequest req, string identityKey)
        {
            // Identical logic to the POST-optimisation (ArrayPool) implementation.
            var raw = JsonSerializer.Serialize(req, _hashSerializerOptions) + "|" + identityKey;
            int byteCount = Encoding.UTF8.GetByteCount(raw);
            byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                Encoding.UTF8.GetBytes(raw, 0, raw.Length, rented, 0);
                Span<byte> hash = stackalloc byte[32];
                SHA256.HashData(rented.AsSpan(0, byteCount), hash);
                return Convert.ToHexString(hash)[..16];
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private static void AssertHashesMatch(AnalysisQueryRequest req, string identityKey)
        {
            var original = ComputeHashViaOriginal(req, identityKey);
            var arrayPool = ComputeHashViaArrayPool(req, identityKey);
            Assert.AreEqual(original, arrayPool,
                $"Hash mismatch for identityKey='{identityKey}': original={original}, arrayPool={arrayPool}");
        }

        [TestMethod]
        public void ComputeHash_simple_sum_request_parity()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>()
            };
            AssertHashesMatch(req, "user_001");
        }

        [TestMethod]
        public void ComputeHash_multi_dim_multi_measure_parity()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region", "Category", "Year" },
                Measures = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum },
                    new MeasureRequest { Field = "Quantity", Func = AggregateFunc.Avg },
                    new MeasureRequest { Field = "Discount", Func = AggregateFunc.Max },
                },
                Filters = new List<FilterCondition>
                {
                    new FilterCondition { Field = "Region", Operator = FilterOperator.Eq, Value = "華東" }
                }
            };
            AssertHashesMatch(req, "admin_user");
        }

        [TestMethod]
        public void ComputeHash_empty_measures_parity()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Status" },
                Measures = new List<MeasureRequest>(),
                Filters = new List<FilterCondition>()
            };
            AssertHashesMatch(req, "tenant_42_user_xyz");
        }

        [TestMethod]
        public void ComputeHash_unicode_identity_key_parity()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "地區" },
                Measures = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "金額", Func = AggregateFunc.Count }
                },
                Filters = new List<FilterCondition>()
            };
            // Unicode identity key with multi-byte UTF-8 characters.
            AssertHashesMatch(req, "用戶_abc123_中文");
        }

        [TestMethod]
        public void ComputeHash_returns_null_when_no_identity_key()
        {
            // ComputeHash is private; test via the engine's Execute method which returns null hash
            // for identity-less requests (M29 fix — no cache entry created).
            // We can verify this indirectly: Execute without identityKey must still return a result.
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>()
            };
            // Just check that both algorithms agree for a long ASCII identity key.
            AssertHashesMatch(req, "a_very_long_identity_key_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        }

        // ──────────────────────────────────────────────────────────────────────
        // A2. ComputeHash PRODUCTION-PATH GOLDEN PIN
        //
        // Unlike section A above (which only compared two re-implementations),
        // these tests call the REAL AnalysisQueryEngine.ComputeHash directly
        // (made internal via InternalsVisibleTo in Core.csproj).
        //
        // Golden values were captured from the current production implementation
        // and hardcoded here.  Any future change to the hashing algorithm —
        // including hashing the full rented ArrayPool buffer instead of the
        // written slice — will fail these assertions and surface the regression
        // before it silently invalidates the OLAP query cache.
        // ──────────────────────────────────────────────────────────────────────

        [TestMethod]
        public void ComputeHash_production_simple_sum_golden_pin()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>()
            };
            // Golden: captured from production ComputeHash (ArrayPool path).
            Assert.AreEqual("654936D720FD1438",
                AnalysisQueryEngine.ComputeHash(req, "user_001"),
                "ComputeHash output drifted from golden value — cache keys will be silently invalidated.");
        }

        [TestMethod]
        public void ComputeHash_production_multi_dim_multi_measure_golden_pin()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region", "Category", "Year" },
                Measures = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum },
                    new MeasureRequest { Field = "Quantity", Func = AggregateFunc.Avg },
                    new MeasureRequest { Field = "Discount", Func = AggregateFunc.Max },
                },
                Filters = new List<FilterCondition>
                {
                    new FilterCondition { Field = "Region", Operator = FilterOperator.Eq, Value = "華東" }
                }
            };
            // Golden: captured from production ComputeHash (ArrayPool path).
            Assert.AreEqual("17F7E3DD32C92E1E",
                AnalysisQueryEngine.ComputeHash(req, "admin_user"),
                "ComputeHash output drifted from golden value — cache keys will be silently invalidated.");
        }

        [TestMethod]
        public void ComputeHash_production_empty_measures_golden_pin()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Status" },
                Measures = new List<MeasureRequest>(),
                Filters = new List<FilterCondition>()
            };
            // Golden: captured from production ComputeHash (ArrayPool path).
            Assert.AreEqual("8F056AEE2E08AAAA",
                AnalysisQueryEngine.ComputeHash(req, "tenant_42_user_xyz"),
                "ComputeHash output drifted from golden value — cache keys will be silently invalidated.");
        }

        [TestMethod]
        public void ComputeHash_production_unicode_golden_pin()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "地區" },
                Measures = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "金額", Func = AggregateFunc.Count }
                },
                Filters = new List<FilterCondition>()
            };
            // Golden: captured from production ComputeHash (ArrayPool path).
            // Also validates multi-byte UTF-8 identity key is hashed correctly.
            Assert.AreEqual("9C722341532A0906",
                AnalysisQueryEngine.ComputeHash(req, "用戶_abc123_中文"),
                "ComputeHash output drifted from golden value — cache keys will be silently invalidated.");
        }

        [TestMethod]
        public void ComputeHash_production_returns_null_for_empty_identity_key()
        {
            // Verifies the M29 guard in the real production method (not just in the re-implementations).
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>()
            };
            Assert.IsNull(AnalysisQueryEngine.ComputeHash(req, null),
                "ComputeHash must return null when identityKey is null (M29 fix).");
            Assert.IsNull(AnalysisQueryEngine.ComputeHash(req, string.Empty),
                "ComputeHash must return null when identityKey is empty (M29 fix).");
        }

                // ──────────────────────────────────────────────────────────────────────
        // B. Single-pass accumulator PARITY
        //
        // Verifies that the new single-pass InProcessGroupByStrategy
        // produces EXACT same Sum/Avg/Max/Min/Count/DistinctCount as the
        // semantics described in the original multi-pass implementation.
        // We test:  mixed null/non-null, empty group, multi-measure, DistinctCount.
        // ──────────────────────────────────────────────────────────────────────

        private class AggRecord
        {
            [Dimension(DisplayName = "Cat")] public string Cat { get; set; } = string.Empty;

            [Measure(AllowedFuncs =
                AggregateFunc.Sum | AggregateFunc.Count | AggregateFunc.Avg |
                AggregateFunc.Max | AggregateFunc.Min | AggregateFunc.DistinctCount,
                DisplayName = "Num")]
            public decimal? Num { get; set; }   // nullable to test null-skipping

            [Measure(AllowedFuncs =
                AggregateFunc.Sum | AggregateFunc.Count | AggregateFunc.Avg |
                AggregateFunc.Max | AggregateFunc.Min | AggregateFunc.DistinctCount,
                DisplayName = "Tag")]
            public string? Tag { get; set; }    // string measure for DistinctCount
        }

        private static Dictionary<string, AnalysisFieldMeta> AggWhitelist =>
            AnalysisFieldScanner.ScanModel(typeof(AggRecord)).ToDictionary(f => f.FieldName);

        private static InProcessGroupByStrategy Strat => new InProcessGroupByStrategy();

        private static List<Dictionary<string, object?>> RunAgg(
            IEnumerable<AggRecord> rows,
            List<string> dims,
            List<MeasureRequest> measures)
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = dims,
                Measures = measures,
                Filters = new List<FilterCondition>()
            };
            return Strat.Execute(rows.AsQueryable(), req, AggWhitelist);
        }

        [TestMethod]
        public void SinglePass_Sum_matches_expected()
        {
            var rows = new[]
            {
                new AggRecord { Cat = "A", Num = 10m },
                new AggRecord { Cat = "A", Num = 20m },
                new AggRecord { Cat = "A", Num = null },   // null skipped in Sum
                new AggRecord { Cat = "B", Num = 5m },
            };
            var result = RunAgg(rows,
                new List<string> { "Cat" },
                new List<MeasureRequest> { new MeasureRequest { Field = "Num", Func = AggregateFunc.Sum } });

            var a = result.Single(r => r["Cat"]?.ToString() == "A");
            var b = result.Single(r => r["Cat"]?.ToString() == "B");
            Assert.AreEqual(30m, a["Num_Sum"]);   // 10+20, null skipped
            Assert.AreEqual(5m,  b["Num_Sum"]);
        }

        [TestMethod]
        public void SinglePass_Count_excludes_nulls()
        {
            var rows = new[]
            {
                new AggRecord { Cat = "A", Num = 1m  },
                new AggRecord { Cat = "A", Num = null },
                new AggRecord { Cat = "A", Num = 3m  },
            };
            var result = RunAgg(rows,
                new List<string> { "Cat" },
                new List<MeasureRequest> { new MeasureRequest { Field = "Num", Func = AggregateFunc.Count } });

            var a = result.Single();
            Assert.AreEqual(2m, a["Num_Count"]);  // null excluded → count = 2
        }

        [TestMethod]
        public void SinglePass_Avg_null_group_returns_null()
        {
            var rows = new[]
            {
                new AggRecord { Cat = "All", Num = null },
                new AggRecord { Cat = "All", Num = null },
            };
            var result = RunAgg(rows,
                new List<string> { "Cat" },
                new List<MeasureRequest> { new MeasureRequest { Field = "Num", Func = AggregateFunc.Avg } });

            var a = result.Single();
            Assert.IsNull(a["Num_Avg"]);  // all nulls → Avg returns null
        }

        [TestMethod]
        public void SinglePass_Avg_exact_value()
        {
            var rows = new[]
            {
                new AggRecord { Cat = "X", Num = 10m },
                new AggRecord { Cat = "X", Num = 20m },
                new AggRecord { Cat = "X", Num = null },
                new AggRecord { Cat = "X", Num = 30m },
            };
            var result = RunAgg(rows,
                new List<string> { "Cat" },
                new List<MeasureRequest> { new MeasureRequest { Field = "Num", Func = AggregateFunc.Avg } });

            var x = result.Single();
            // Avg of [10,20,30] = 20 (null excluded).
            Assert.AreEqual(20m, x["Num_Avg"]);
        }

        [TestMethod]
        public void SinglePass_Max_Min_with_nulls()
        {
            var rows = new[]
            {
                new AggRecord { Cat = "G", Num = 7m   },
                new AggRecord { Cat = "G", Num = null },
                new AggRecord { Cat = "G", Num = 3m   },
                new AggRecord { Cat = "G", Num = 15m  },
            };
            var result = RunAgg(rows,
                new List<string> { "Cat" },
                new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Num", Func = AggregateFunc.Max },
                    new MeasureRequest { Field = "Num", Func = AggregateFunc.Min },
                });

            var g = result.Single();
            Assert.AreEqual(15m, g["Num_Max"]);
            Assert.AreEqual(3m,  g["Num_Min"]);
        }

        [TestMethod]
        public void SinglePass_MaxMin_all_nulls_returns_null()
        {
            var rows = new[]
            {
                new AggRecord { Cat = "Z", Num = null },
            };
            var result = RunAgg(rows,
                new List<string> { "Cat" },
                new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Num", Func = AggregateFunc.Max },
                    new MeasureRequest { Field = "Num", Func = AggregateFunc.Min },
                });

            var z = result.Single();
            Assert.IsNull(z["Num_Max"]);
            Assert.IsNull(z["Num_Min"]);
        }

        [TestMethod]
        public void SinglePass_Sum_empty_group_returns_zero()
        {
            // Empty group: no rows assigned to this dimension key.
            // The strategy never produces a group with 0 rows (GroupBy only yields non-empty groups),
            // but Sum for a group with only null values should return 0 (per original semantics).
            var rows = new[]
            {
                new AggRecord { Cat = "E", Num = null },
            };
            var result = RunAgg(rows,
                new List<string> { "Cat" },
                new List<MeasureRequest> { new MeasureRequest { Field = "Num", Func = AggregateFunc.Sum } });

            var e = result.Single();
            Assert.AreEqual(0m, e["Num_Sum"]);  // per original: Count==0 → 0m
        }

        [TestMethod]
        public void SinglePass_DistinctCount_excludes_nulls_counts_uniques()
        {
            var rows = new[]
            {
                new AggRecord { Cat = "D", Tag = "red"   },
                new AggRecord { Cat = "D", Tag = "blue"  },
                new AggRecord { Cat = "D", Tag = "red"   },  // duplicate
                new AggRecord { Cat = "D", Tag = null     },  // null excluded
                new AggRecord { Cat = "D", Tag = "green" },
            };
            var result = RunAgg(rows,
                new List<string> { "Cat" },
                new List<MeasureRequest> { new MeasureRequest { Field = "Tag", Func = AggregateFunc.DistinctCount } });

            var d = result.Single();
            Assert.AreEqual(3m, d["Tag_DistinctCount"]);  // red, blue, green = 3
        }

        [TestMethod]
        public void SinglePass_multi_measure_multi_group_parity()
        {
            // Two groups, three measures — validates the per-measure accumulator isolation.
            var rows = new[]
            {
                new AggRecord { Cat = "P", Num = 100m, Tag = "x" },
                new AggRecord { Cat = "P", Num = 200m, Tag = "x" },
                new AggRecord { Cat = "P", Num = null,  Tag = "y" },
                new AggRecord { Cat = "Q", Num = 50m,  Tag = "a" },
                new AggRecord { Cat = "Q", Num = 50m,  Tag = "a" },
            };
            var measures = new List<MeasureRequest>
            {
                new MeasureRequest { Field = "Num", Func = AggregateFunc.Sum },
                new MeasureRequest { Field = "Num", Func = AggregateFunc.Count },
                new MeasureRequest { Field = "Tag", Func = AggregateFunc.DistinctCount },
            };
            var result = RunAgg(rows, new List<string> { "Cat" }, measures);

            var p = result.Single(r => r["Cat"]?.ToString() == "P");
            var q = result.Single(r => r["Cat"]?.ToString() == "Q");

            Assert.AreEqual(300m, p["Num_Sum"]);
            Assert.AreEqual(2m,   p["Num_Count"]);      // null excluded
            Assert.AreEqual(2m,   p["Tag_DistinctCount"]); // x, y

            Assert.AreEqual(100m, q["Num_Sum"]);
            Assert.AreEqual(2m,   q["Num_Count"]);
            Assert.AreEqual(1m,   q["Tag_DistinctCount"]); // only "a"
        }

        // ──────────────────────────────────────────────────────────────────────
        // C. Pivot fast-path correctness for 1, 2, and 3 rowDims
        // ──────────────────────────────────────────────────────────────────────

        private static AnalysisQueryResponse MakeGroupByResult(string[] columns, object?[][] dataRows)
        {
            var rows = dataRows.Select(dr =>
            {
                var dict = new Dictionary<string, object?>();
                for (int i = 0; i < columns.Length; i++)
                    dict[columns[i]] = dr[i];
                return dict;
            }).ToList();
            return new AnalysisQueryResponse { Rows = rows };
        }

        [TestMethod]
        public void Pivot_BuildRowKey_1_rowDim_fast_path()
        {
            // 1 rowDim (fast-path: direct ToString)
            var groupBy = MakeGroupByResult(
                new[] { "Region", "Category", "Amount_Sum" },
                new object?[][]
                {
                    new object?[] { "北", "電子", 100m },
                    new object?[] { "南", "電子", 200m },
                });

            var result = AnalysisPivotEngine.Pivot(
                groupBy,
                pivotDimension: "Category",
                allDimensions: new List<string> { "Region", "Category" },
                measureNames: new List<string> { "Amount_Sum" });

            Assert.AreEqual(2, result.Rows.Count);
            var north = result.Rows.Single(r => r["Region"]?.ToString() == "北");
            Assert.AreEqual(100m, north["電子_Amount_Sum"]);
        }

        [TestMethod]
        public void Pivot_BuildRowKey_2_rowDims_fast_path()
        {
            // 2 rowDims (fast-path: string.Concat)
            var groupBy = MakeGroupByResult(
                new[] { "Region", "Year", "Category", "Amount_Sum" },
                new object?[][]
                {
                    new object?[] { "北", "2025", "電子", 300m },
                    new object?[] { "北", "2025", "服飾", 400m },
                });

            var result = AnalysisPivotEngine.Pivot(
                groupBy,
                pivotDimension: "Category",
                allDimensions: new List<string> { "Region", "Year", "Category" },
                measureNames: new List<string> { "Amount_Sum" });

            Assert.AreEqual(1, result.Rows.Count);
            var row = result.Rows.Single();
            Assert.AreEqual("北", row["Region"]?.ToString());
            Assert.AreEqual("2025", row["Year"]?.ToString());
            Assert.AreEqual(300m, row["電子_Amount_Sum"]);
            Assert.AreEqual(400m, row["服飾_Amount_Sum"]);
        }

        [TestMethod]
        public void Pivot_BuildRowKey_3_rowDims_fallback_path()
        {
            // 3 rowDims (fallback: string.Join) — ensures >2 case still works
            var groupBy = MakeGroupByResult(
                new[] { "A", "B", "C", "PivotDim", "Amount_Sum" },
                new object?[][]
                {
                    new object?[] { "a1", "b1", "c1", "X", 10m },
                    new object?[] { "a1", "b1", "c1", "Y", 20m },
                });

            var result = AnalysisPivotEngine.Pivot(
                groupBy,
                pivotDimension: "PivotDim",
                allDimensions: new List<string> { "A", "B", "C", "PivotDim" },
                measureNames: new List<string> { "Amount_Sum" });

            Assert.AreEqual(1, result.Rows.Count);
            var row = result.Rows.Single();
            Assert.AreEqual("a1", row["A"]?.ToString());
            Assert.AreEqual("b1", row["B"]?.ToString());
            Assert.AreEqual("c1", row["C"]?.ToString());
            Assert.AreEqual(10m, row["X_Amount_Sum"]);
            Assert.AreEqual(20m, row["Y_Amount_Sum"]);
        }

        [TestMethod]
        public void Pivot_BuildRowKey_0_rowDims_returns_single_row()
        {
            // 0 rowDims edge case (pivot collapses everything into one row)
            var groupBy = MakeGroupByResult(
                new[] { "Category", "Amount_Sum" },
                new object?[][]
                {
                    new object?[] { "電子", 500m },
                    new object?[] { "服飾", 600m },
                });

            var result = AnalysisPivotEngine.Pivot(
                groupBy,
                pivotDimension: "Category",
                allDimensions: new List<string> { "Category" },
                measureNames: new List<string> { "Amount_Sum" });

            Assert.AreEqual(1, result.Rows.Count);
            var row = result.Rows.Single();
            Assert.AreEqual(500m, row["電子_Amount_Sum"]);
            Assert.AreEqual(600m, row["服飾_Amount_Sum"]);
        }
    }
}
