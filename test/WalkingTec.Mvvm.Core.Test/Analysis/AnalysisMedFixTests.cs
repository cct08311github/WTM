#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    /// <summary>
    /// Focused regression tests for the med-analysis fix batch:
    ///   M1  — FilterCondition.Value=null NRE
    ///   M2  — filter/having/sort cap (controller BadRequest)
    ///   M3  — pivot row-key collision when dimension value contains '|'
    ///   M4  — DataPrivilege fingerprint included in identityKey
    ///   M5  — ServerSideGroupByStrategy 4-measure guard
    ///   M29 — ComputeHash returns null when identityKey null; no anonymous cache sharing
    /// </summary>
    [TestClass]
    public class AnalysisMedFixTests
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
            _registry.Build(new[] { typeof(AnalysisMedFixTests).Assembly });
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

        // ─── M1: null FilterCondition.Value does not throw NRE ────────────────

        [TestMethod]
        public void M1_RelativeDate_null_Value_does_not_throw()
        {
            // A FilterCondition with null Value must not cause NRE in ResolveRelativeDates.
            var filters = new List<FilterCondition>
            {
                new FilterCondition { Field = "Region", Operator = FilterOperator.Eq, Value = null },
                new FilterCondition { Field = "Region", Operator = FilterOperator.Eq, Value = "@today" }
            };

            // Should NOT throw; the null-Value condition is passed through as-is,
            // and the @today token is expanded.
            var result = AnalysisQueryEngine.ResolveRelativeDates(filters);

            // null-Value condition preserved + 2 conditions for @today (Gte + Lte)
            Assert.AreEqual(3, result.Count, "null-Value condition should pass through, @today expands to 2");
            Assert.IsNull(result[0].Value, "null-Value condition should be unchanged");
        }

        [TestMethod]
        public void M1_Engine_Execute_with_null_filter_value_does_not_throw()
        {
            // In-process strategy should survive a null filter value (ChangeType(null, ...) → null constant)
            var data = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "North", Category = "A", Amount = 100m }
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
                    // Value = null: should not NRE — ChangeType(null, targetType) returns null
                    new FilterCondition { Field = "Region", Operator = FilterOperator.Eq, Value = null }
                }
            };

            // Execute must not throw NRE
            var result = engine.Execute(data.AsQueryable(), req, whitelist);
            Assert.IsNotNull(result);
        }

        // ─── M2: filter/having/sort caps return BadRequest ────────────────────

        [TestMethod]
        public async Task M2_Query_over_50_filters_returns_400()
        {
            var ctrl = CreateController();
            var req  = MakeReq();
            req.Filters = Enumerable.Range(0, 51)
                .Select(i => new FilterCondition { Field = "Region", Operator = FilterOperator.Eq, Value = $"R{i}" })
                .ToList();

            var result = await ctrl.Query(req);
            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "51 filter clauses should return 400");
        }

        [TestMethod]
        public async Task M2_Query_over_50_havingFilters_returns_400()
        {
            var ctrl = CreateController();
            var req  = MakeReq();
            req.HavingFilters = Enumerable.Range(0, 51)
                .Select(i => new HavingFilter { Field = "Amount_Sum", Operator = FilterOperator.Gt, Value = i.ToString() })
                .ToList();

            var result = await ctrl.Query(req);
            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "51 having-filter clauses should return 400");
        }

        [TestMethod]
        public async Task M2_Query_over_50_sort_returns_400()
        {
            var ctrl = CreateController();
            var req  = MakeReq();
            req.Sort = Enumerable.Range(0, 51)
                .Select(_ => new SortSpec { Field = "Amount_Sum", Descending = false })
                .ToList();

            var result = await ctrl.Query(req);
            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "51 sort clauses should return 400");
        }

        [TestMethod]
        public async Task M2_Query_exactly_50_filters_does_not_reject()
        {
            // Add enough test data so the query can actually execute
            for (int i = 0; i < 5; i++)
                _testData.Add(new SaleRecord { ID = Guid.NewGuid(), Region = "North", Category = "A", Amount = 100m });

            var ctrl = CreateController();
            var req  = MakeReq();
            // 50 filter clauses → should not be rejected as over the limit
            req.Filters = Enumerable.Range(0, 50)
                .Select(i => new FilterCondition { Field = "Region", Operator = FilterOperator.Eq, Value = $"R{i}" })
                .ToList();

            var result = await ctrl.Query(req);
            // Should not be a 400 from our cap check (may be other 400 from engine, but not cap check)
            if (result is BadRequestObjectResult bad)
            {
                var pd = bad.Value as Microsoft.AspNetCore.Mvc.ProblemDetails;
                Assert.IsFalse(pd?.Title?.Contains("must not exceed"), "50 clauses exactly should not be rejected by cap");
            }
        }

        // ─── M3: pivot row key collision when dimension value contains '|' ────

        [TestMethod]
        public void M3_Pivot_dimension_value_with_pipe_does_not_collide()
        {
            // Two rows whose Region values each contain '|' must produce two distinct pivot groups.
            // Without M3 fix, "A|B" and "C" would produce the same key as "A" and "B|C"
            // when the row key is naively joined with '|'.
            var data = new List<SaleRecord>
            {
                // Row 1: Region = "A|B" (contains the '|' delimiter), Category = "X"
                new SaleRecord { ID = Guid.NewGuid(), Region = "A|B", Category = "X", Amount = 10m },
                // Row 2: Region = "A",   Category = "B|X" (pipe in the pivot dimension — different row)
                new SaleRecord { ID = Guid.NewGuid(), Region = "A",   Category = "B|X", Amount = 20m },
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SaleRecord));
            var engine    = CreateEngine();

            var pivotReq = new AnalysisPivotRequest
            {
                ListVmType     = typeof(SaleRecordListVM).FullName!,
                Dimensions     = new List<string> { "Region", "Category" },
                PivotDimension = "Category",
                Measures       = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>()
            };

            var result = engine.ExecutePivot(data.AsQueryable(), pivotReq, whitelist);

            // The two rows must produce two distinct row groups (one per Region).
            // "A|B" and "A" are different regions; they must not be merged.
            Assert.AreEqual(2, result.Rows.Count,
                "Rows with pipe-containing dimension values must produce distinct pivot groups");

            // Verify each region has its own row
            var regions = result.Rows.Select(r => r["Region"]?.ToString()).OrderBy(r => r).ToList();
            CollectionAssert.Contains(regions, "A|B");
            CollectionAssert.Contains(regions, "A");
        }

        // ─── M4: DataPrivilege fingerprint → different identity key ───────────

        [TestMethod]
        public void M4_Different_DataPrivileges_produce_different_identityKey()
        {
            // Access BuildDataPrivilegeFingerprint via reflection (private static method on controller)
            var method = typeof(_AnalysisController).GetMethod(
                "BuildDataPrivilegeFingerprint",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "BuildDataPrivilegeFingerprint must exist as a private static method");

            var privA = new List<SimpleDataPri>
            {
                new SimpleDataPri { ID = Guid.NewGuid(), TableName = "Order", RelateId = "1", UserCode = "alice" }
            };

            var privB = new List<SimpleDataPri>
            {
                new SimpleDataPri { ID = Guid.NewGuid(), TableName = "Order", RelateId = "2", UserCode = "alice" }
            };

            var fpA = (string?)method!.Invoke(null, new object?[] { privA });
            var fpB = (string?)method!.Invoke(null, new object?[] { privB });

            Assert.IsNotNull(fpA);
            Assert.IsNotNull(fpB);
            Assert.AreNotEqual(fpA, fpB,
                "Different RelateId must produce a different fingerprint");
        }

        [TestMethod]
        public void M4_Empty_DataPrivileges_produce_stable_empty_fingerprint()
        {
            var method = typeof(_AnalysisController).GetMethod(
                "BuildDataPrivilegeFingerprint",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "BuildDataPrivilegeFingerprint must exist");

            var fpNull  = (string?)method!.Invoke(null, new object?[] { null });
            var fpEmpty = (string?)method!.Invoke(null, new object?[] { new List<SimpleDataPri>() });

            Assert.AreEqual(string.Empty, fpNull,  "null privileges → empty fingerprint");
            Assert.AreEqual(string.Empty, fpEmpty, "empty list → empty fingerprint");
            // Both null and empty produce the same stable empty string
            Assert.AreEqual(fpNull, fpEmpty);
        }

        [TestMethod]
        public void M4_Same_DataPrivileges_different_order_produce_same_fingerprint()
        {
            var method = typeof(_AnalysisController).GetMethod(
                "BuildDataPrivilegeFingerprint",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "BuildDataPrivilegeFingerprint must exist");

            var pri1 = new SimpleDataPri { ID = Guid.NewGuid(), TableName = "Order",   RelateId = "1", UserCode = "alice" };
            var pri2 = new SimpleDataPri { ID = Guid.NewGuid(), TableName = "Product", RelateId = "2", UserCode = "alice" };

            var fpForward  = (string?)method!.Invoke(null, new object?[] { new List<SimpleDataPri> { pri1, pri2 } });
            var fpReversed = (string?)method!.Invoke(null, new object?[] { new List<SimpleDataPri> { pri2, pri1 } });

            Assert.AreEqual(fpForward, fpReversed,
                "Privilege order must not affect the fingerprint — sort ensures stability");
        }

        // ─── M5: ServerSideGroupByStrategy 4-measure guard ───────────────────

        [TestMethod]
        public void M5_ServerSide_Execute_with_4_measures_throws()
        {
            var strategy = new ServerSideGroupByStrategy();
            var data     = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "North", Category = "A", Amount = 1m }
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum },
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Count },
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Max },
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Min }, // 4th — over cap
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SaleRecord)).ToDictionary(f => f.FieldName);

            var ex = Assert.ThrowsException<InvalidOperationException>(() =>
                strategy.Execute(data.AsQueryable(), req, whitelist));

            StringAssert.Contains(ex.Message, "3",
                "Exception message should mention the 3-measure limit");
        }

        [TestMethod]
        public async Task M5_ServerSide_ExecuteAsync_with_4_measures_throws()
        {
            var strategy = new ServerSideGroupByStrategy();
            var data     = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "North", Category = "A", Amount = 1m }
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum },
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Count },
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Max },
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Min },
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SaleRecord)).ToDictionary(f => f.FieldName);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
                await strategy.ExecuteAsync(data.AsQueryable(), req, whitelist));
        }

        [TestMethod]
        public void M5_ServerSide_Execute_with_3_measures_does_not_throw()
        {
            // Confirm the normal controller path (max 3 measures) does not trip the guard.
            var strategy = new ServerSideGroupByStrategy();
            var data     = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "North", Category = "A", Amount = 10m }
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum },
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Count },
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Max },
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SaleRecord)).ToDictionary(f => f.FieldName);

            // Should not throw
            var result = strategy.Execute(data.AsQueryable(), req, whitelist);
            Assert.IsNotNull(result);
        }

        // ─── M29: ComputeHash null when identityKey null; no anonymous cache share ──

        [TestMethod]
        public void M29_ComputeHash_returns_null_when_identityKey_null()
        {
            // ComputeHash is private static — access via reflection.
            var method = typeof(AnalysisQueryEngine).GetMethod(
                "ComputeHash",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "ComputeHash must exist as a private static method");

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                }
            };

            // Null identityKey → null hash (M29)
            var hashNull = (string?)method!.Invoke(null, new object?[] { req, null });
            Assert.IsNull(hashNull, "ComputeHash must return null when identityKey is null");

            // Empty string identityKey → also null (M29)
            var hashEmpty = (string?)method!.Invoke(null, new object?[] { req, string.Empty });
            Assert.IsNull(hashEmpty, "ComputeHash must return null when identityKey is empty");

            // Non-null identityKey → non-null hash
            var hashPresent = (string?)method!.Invoke(null, new object?[] { req, "tenant_user" });
            Assert.IsNotNull(hashPresent, "ComputeHash must return a hash when identityKey is present");
        }

        [TestMethod]
        public void M29_Identity_less_requests_do_not_share_cache()
        {
            // Two calls with no identityKey must not share a cache entry.
            // We verify by using a cache-backed engine and confirming that the second
            // call actually re-executes (uses the in-memory data state) rather than
            // returning a stale cached result.
            using var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var opts = new DbContextOptionsBuilder<M29SaleContext>().UseSqlite(conn).Options;
            using var ctx = new M29SaleContext(opts);
            ctx.Database.EnsureCreated();

            ctx.SaleRecords.Add(new M29SaleRecord { ID = Guid.NewGuid(), Region = "North", Amount = 100m });
            ctx.SaveChanges();

            var mc     = new MemoryCache(new MemoryCacheOptions());
            var cache  = new MemoryAnalysisCache(mc);
            var engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default, cache);

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(M29SaleRecord));

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>()
            };

            // First call — no identityKey (anonymous)
            var r1 = engine.Execute(ctx.SaleRecords.AsQueryable(), req, whitelist, identityKey: null);
            Assert.AreEqual(1, r1.Rows.Count);

            // Mutate DB
            ctx.SaleRecords.Add(new M29SaleRecord { ID = Guid.NewGuid(), Region = "South", Amount = 200m });
            ctx.SaveChanges();

            // Second call — also no identityKey. Because M29 skips the cache for identity-less
            // requests, the engine must see the new data.
            var r2 = engine.Execute(ctx.SaleRecords.AsQueryable(), req, whitelist, identityKey: null);
            Assert.AreEqual(2, r2.Rows.Count,
                "Identity-less requests must not share a cache entry; second call must see new data");
            Assert.AreNotSame(r1, r2, "Different object instances expected (no cache hit)");
        }

        // ─── #380 Fix 6: PivotExport filter caps ─────────────────────────────

        private static AnalysisPivotRequest MakePivotReq() =>
            new AnalysisPivotRequest
            {
                ListVmType     = typeof(SaleRecordListVM).FullName!,
                Dimensions     = new List<string> { "Region" },
                Measures       = new List<MeasureRequest> { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                PivotDimension = "Category"
            };

        [TestMethod]
        public async Task Fix6_PivotExport_over_50_filters_returns_400()
        {
            var ctrl = CreateController();
            var req  = MakePivotReq();
            req.Filters = Enumerable.Range(0, 51)
                .Select(i => new FilterCondition { Field = "Region", Operator = FilterOperator.Eq, Value = $"R{i}" })
                .ToList();

            var result = await ctrl.PivotExport(req);
            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "PivotExport: 51 filter clauses should return 400");
        }

        [TestMethod]
        public async Task Fix6_PivotExport_over_50_havingFilters_returns_400()
        {
            var ctrl = CreateController();
            var req  = MakePivotReq();
            req.HavingFilters = Enumerable.Range(0, 51)
                .Select(i => new HavingFilter { Field = "Amount_Sum", Operator = FilterOperator.Gt, Value = i.ToString() })
                .ToList();

            var result = await ctrl.PivotExport(req);
            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "PivotExport: 51 having-filter clauses should return 400");
        }

        [TestMethod]
        public async Task Fix6_PivotExport_over_50_sort_returns_400()
        {
            var ctrl = CreateController();
            var req  = MakePivotReq();
            req.Sort = Enumerable.Range(0, 51)
                .Select(_ => new SortSpec { Field = "Amount_Sum", Descending = false })
                .ToList();

            var result = await ctrl.PivotExport(req);
            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "PivotExport: 51 sort clauses should return 400");
        }
    }

    // ─── Supporting types for M29 test (must be at namespace scope for EF) ────────

    internal class M29SaleRecord : TopBasePoco
    {
        [Dimension(DisplayName = "地區")]
        public string Region { get; set; } = string.Empty;

        [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count, DisplayName = "金額")]
        public decimal Amount { get; set; }
    }

    internal class M29SaleContext : DbContext
    {
        public M29SaleContext(DbContextOptions opts) : base(opts) { }
        public DbSet<M29SaleRecord> SaleRecords { get; set; } = null!;
    }
}
