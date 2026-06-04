#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    /// <summary>
    /// Regression tests for the low-priority analysis fix batch:
    ///   L1  — FilterCondition.Values (List&lt;string&gt;) is honoured before comma-split of Value
    ///   L2  — MemoryAnalysisCache.Set stores entry under same lock as token capture
    ///   L20 — SaveQuery enforces per-user cap; AnalysisSavedQuery.ConfigJson has [StringLength(65536)]
    ///   L21 — Export / PivotExport reject invalid format values (allowlist: xlsx, csv)
    /// </summary>
    [TestClass]
    public class AnalysisLowFixTests
    {
        // ─── Shared test model ────────────────────────────────────────────────

        private class SaleRecord : TopBasePoco
        {
            [Dimension(DisplayName = "地區")]
            public string Region { get; set; } = string.Empty;

            [Dimension(DisplayName = "類別")]
            public string Category { get; set; } = string.Empty;

            [Measure(AllowedFuncs =
                AggregateFunc.Sum | AggregateFunc.Count | AggregateFunc.Avg |
                AggregateFunc.Max | AggregateFunc.Min,
                DisplayName = "金額")]
            public decimal Amount { get; set; }
        }

        private static IList<SaleRecord> _testData = new List<SaleRecord>();

        [EnableAnalysis]
        private class SaleRecordListVM : BasePagedListVM<SaleRecord, BaseSearcher>
        {
            public override IOrderedQueryable<SaleRecord> GetSearchQuery()
                => _testData.AsQueryable().OrderByDescending(x => x.ID);
        }

        private AnalysisVmRegistry _registry = new();

        [TestInitialize]
        public void Setup()
        {
            _testData = new List<SaleRecord>();
            _registry = new AnalysisVmRegistry();
            _registry.Build(new[] { typeof(AnalysisLowFixTests).Assembly });
        }

        private static AnalysisQueryEngine CreateEngine() =>
            new AnalysisQueryEngine(GroupByStrategyResolver.Default);

        private _AnalysisController CreateController()
        {
            var ctrl = new _AnalysisController(
                _registry,
                CreateEngine(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<_AnalysisController>.Instance);
            ctrl.Wtm = MockWtmContext.CreateWtmContext();
            return ctrl;
        }

        private static AnalysisQueryRequest MakeReq(
            string[]? dims = null,
            (string field, AggregateFunc func)[]? msrs = null) =>
            new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName!,
                Dimensions = dims?.ToList() ?? new List<string> { "Region" },
                Measures   = msrs?.Select(m => new MeasureRequest { Field = m.field, Func = m.func })
                                  .ToList()
                             ?? new List<MeasureRequest>
                                {
                                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                                },
                Filters = new List<FilterCondition>()
            };

        // ─── L1: Values list form is honoured ────────────────────────────────

        /// <summary>
        /// A FilterCondition with Values=["A","B"] and Value="" must filter correctly
        /// using the list form, not throw or return wrong results.
        /// </summary>
        [TestMethod]
        public void L1_Values_list_filters_correctly_ignoring_empty_Value()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "A", Category = "X", Amount = 10m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "B", Category = "Y", Amount = 20m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "C", Category = "Z", Amount = 30m },
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SaleRecord));
            var engine = CreateEngine();

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new FilterCondition
                    {
                        Field    = "Region",
                        Operator = FilterOperator.In,
                        Value    = "",            // deliberately empty — Values should be used
                        Values   = new List<string> { "A", "B" }
                    }
                }
            };

            var result = engine.Execute(_testData.AsQueryable(), req, whitelist);

            // Only regions A and B should survive the filter
            Assert.AreEqual(2, result.Rows.Count,
                "Expected 2 rows (A, B); C must be excluded by the Values list filter.");

            var regions = result.Rows.Select(r => r["Region"]?.ToString()).OrderBy(x => x).ToList();
            CollectionAssert.AreEqual(new[] { "A", "B" }, regions,
                "Values list [A, B] should determine the In-filter, not the empty Value string.");
        }

        /// <summary>
        /// A FilterCondition with Values=["A"] and Value="B,C" must use Values, not Value.
        /// Values takes precedence over the comma-split of Value.
        /// </summary>
        [TestMethod]
        public void L1_Values_list_takes_precedence_over_Value_string()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "A", Category = "X", Amount = 1m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "B", Category = "Y", Amount = 2m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "C", Category = "Z", Amount = 3m },
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SaleRecord));
            var engine = CreateEngine();

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new FilterCondition
                    {
                        Field    = "Region",
                        Operator = FilterOperator.In,
                        Value    = "B,C",         // should be ignored when Values is non-empty
                        Values   = new List<string> { "A" }
                    }
                }
            };

            var result = engine.Execute(_testData.AsQueryable(), req, whitelist);

            Assert.AreEqual(1, result.Rows.Count,
                "Values=[A] should take precedence; only region A expected.");
            Assert.AreEqual("A", result.Rows[0]["Region"]?.ToString());
        }

        // ─── L2: MemoryAnalysisCache Set stores entry reliably ───────────────

        /// <summary>
        /// After Set, TryGet must return the stored entry (basic correctness of the lock'd path).
        /// This exercises the moved _cache.Set being inside lock(_lock).
        /// </summary>
        [TestMethod]
        public void L2_Set_stores_entry_and_TryGet_returns_it()
        {
            var mc = new MemoryCache(new MemoryCacheOptions());
            var cache = new MemoryAnalysisCache(mc);

            var response = new AnalysisQueryResponse
            {
                QueryHash  = "LOCK_TEST_HASH",
                Columns    = new List<string> { "Region" },
                Rows       = new List<Dictionary<string, object?>>
                {
                    new Dictionary<string, object?> { ["Region"] = "North" }
                },
                TotalCount = 1,
                Truncated  = false
            };

            cache.Set("LOCK_TEST_HASH", response);

            var hit = cache.TryGet("LOCK_TEST_HASH", out var cached);

            Assert.IsTrue(hit, "TryGet must return true after Set.");
            Assert.IsNotNull(cached, "cached must not be null after a successful Set.");
            Assert.AreEqual("LOCK_TEST_HASH", cached!.QueryHash);
        }

        /// <summary>
        /// After InvalidateAll, an entry stored before the invalidation must not be retrievable.
        /// This validates that the expiration token is correctly wired (lock'd path).
        /// </summary>
        [TestMethod]
        public void L2_InvalidateAll_evicts_entry_stored_before_invalidation()
        {
            var mc = new MemoryCache(new MemoryCacheOptions());
            var cache = new MemoryAnalysisCache(mc);

            var response = new AnalysisQueryResponse
            {
                QueryHash  = "PRE_INVALIDATE",
                Columns    = new List<string> { "X" },
                Rows       = new List<Dictionary<string, object?>>(),
                TotalCount = 0,
                Truncated  = false
            };

            cache.Set("PRE_INVALIDATE", response);
            Assert.IsTrue(cache.TryGet("PRE_INVALIDATE", out _), "Entry should exist before InvalidateAll.");

            cache.InvalidateAll();

            Assert.IsFalse(cache.TryGet("PRE_INVALIDATE", out _),
                "Entry must be evicted after InvalidateAll.");
        }

        // ─── L20: SaveQuery per-user cap + ConfigJson StringLength ───────────

        /// <summary>
        /// ConfigJson must carry a [StringLength(65536)] attribute.
        /// </summary>
        [TestMethod]
        public void L20_ConfigJson_has_StringLength_65536_attribute()
        {
            var prop = typeof(AnalysisSavedQuery).GetProperty(nameof(AnalysisSavedQuery.ConfigJson));
            Assert.IsNotNull(prop, "ConfigJson property must exist.");

            var attr = prop!.GetCustomAttribute<StringLengthAttribute>();
            Assert.IsNotNull(attr, "ConfigJson must have [StringLength] attribute.");
            Assert.AreEqual(65536, attr!.MaximumLength, "ConfigJson StringLength must be 65536.");
        }

        /// <summary>
        /// SaveQuery beyond the 100-row per-user cap must return BadRequest.
        /// MockWtmContext.CreateWtmContext() provides a real SQLite in-memory DB
        /// (EnsureCreated) so the EF Count query executes against real tables.
        /// </summary>
        [TestMethod]
        public void L20_SaveQuery_beyond_per_user_cap_returns_BadRequest()
        {
            // MockWtmContext already creates a per-test SQLite in-memory DB with EnsureCreated.
            var wtm = MockWtmContext.CreateWtmContext();
            var userCode = wtm.LoginUserInfo?.ITCode ?? "user";

            // Seed exactly MaxSavedQueriesPerUser (100) rows for this user.
            for (int i = 0; i < 100; i++)
            {
                wtm.DC.Set<AnalysisSavedQuery>().Add(new AnalysisSavedQuery
                {
                    ID         = Guid.NewGuid(),
                    Name       = $"Query{i}",
                    ListVmType = typeof(SaleRecordListVM).FullName!,
                    ConfigJson = "{}",
                    OwnerCode  = userCode,
                    IsPublic   = false
                });
            }
            wtm.DC.SaveChanges();

            var ctrl = new _AnalysisController(
                _registry,
                CreateEngine(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<_AnalysisController>.Instance);
            ctrl.Wtm = wtm;

            var saveReq = new SaveQueryRequest
            {
                Name     = "QueryThatShouldBeRejected",
                Config   = new AnalysisQueryRequest
                {
                    ListVmType = typeof(SaleRecordListVM).FullName!,
                    Dimensions = new List<string> { "Region" },
                    Measures   = new List<MeasureRequest>
                    {
                        new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                    }
                },
                IsPublic = false
            };

            var result = ctrl.SaveQuery(saveReq);

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "SaveQuery must return 400 BadRequest when the per-user cap (100) is reached.");
        }

        // ─── L21: Export / PivotExport reject invalid format ─────────────────

        /// <summary>
        /// Export with an invalid format query-string value must return 400 BadRequest.
        /// </summary>
        [TestMethod]
        public async Task L21_Export_invalid_format_returns_BadRequest()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "North", Category = "A", Amount = 100m }
            };

            var ctrl = CreateController();

            var req = MakeReq();

            // Pass an invalid format value (log injection attempt).
            var result = await ctrl.Export(req, format: "xlsx\nContent-Type: text/plain");

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "Export must return 400 for an invalid format value.");
        }

        /// <summary>
        /// Export with a valid format (xlsx) must not return BadRequest due to the allowlist check.
        /// </summary>
        [TestMethod]
        public async Task L21_Export_valid_format_xlsx_passes_allowlist()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "North", Category = "A", Amount = 100m }
            };

            var ctrl = CreateController();
            var req = MakeReq();

            var result = await ctrl.Export(req, format: "xlsx");

            // Should NOT be a BadRequestObjectResult due to the format check
            Assert.IsNotInstanceOfType(result, typeof(BadRequestObjectResult),
                "Export with format=xlsx must not be rejected by the allowlist guard.");
        }

        /// <summary>
        /// Export with a valid format (csv) must not return BadRequest due to the allowlist check.
        /// </summary>
        [TestMethod]
        public async Task L21_Export_valid_format_csv_passes_allowlist()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "North", Category = "A", Amount = 100m }
            };

            var ctrl = CreateController();
            var req = MakeReq();

            var result = await ctrl.Export(req, format: "CSV"); // case-insensitive

            Assert.IsNotInstanceOfType(result, typeof(BadRequestObjectResult),
                "Export with format=CSV (case-insensitive) must not be rejected by the allowlist guard.");
        }

        /// <summary>
        /// PivotExport with an invalid format query-string value must return 400 BadRequest.
        /// </summary>
        [TestMethod]
        public async Task L21_PivotExport_invalid_format_returns_BadRequest()
        {
            var ctrl = CreateController();

            // A minimal pivot request — the format check fires before body validation in the new code.
            var pivotReq = new AnalysisPivotRequest
            {
                ListVmType   = typeof(SaleRecordListVM).FullName!,
                Dimensions   = new List<string> { "Region" },
                PivotDimension = "Category",
                Measures     = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                }
            };

            var result = await ctrl.PivotExport(pivotReq, format: "xml");

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "PivotExport must return 400 for an invalid format value.");
        }
    }
}
