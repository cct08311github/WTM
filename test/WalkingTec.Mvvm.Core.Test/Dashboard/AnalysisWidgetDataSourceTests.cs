#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Dashboard;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Core.Test.Extensions;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    [TestClass]
    public class AnalysisWidgetDataSourceTests
    {
        // ─── Test Model ──────────────────────────────────────────────────────

        private class DashSaleRecord : TopBasePoco
        {
            [Dimension(DisplayName = "Region")] public string Region { get; set; } = "";
            [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count, DisplayName = "Amount")]
            public decimal Amount { get; set; }
        }

        private static IList<DashSaleRecord> _testData = new List<DashSaleRecord>();

        [EnableAnalysis]
        private class DashSaleRecordListVM : BasePagedListVM<DashSaleRecord, BaseSearcher>
        {
            public override IOrderedQueryable<DashSaleRecord> GetSearchQuery()
                => _testData.AsQueryable().OrderByDescending(x => x.ID);
        }

        // ─── Infrastructure ──────────────────────────────────────────────────

        private AnalysisVmRegistry _registry = null!;
        private AnalysisQueryEngine _engine = null!;
        private IServiceProvider _serviceProvider = null!;

        [TestInitialize]
        public void Setup()
        {
            _testData = new List<DashSaleRecord>
            {
                new() { ID = Guid.NewGuid(), Region = "North", Amount = 100m },
                new() { ID = Guid.NewGuid(), Region = "North", Amount = 200m },
                new() { ID = Guid.NewGuid(), Region = "South", Amount = 150m },
            };

            _registry = new AnalysisVmRegistry();
            _registry.Build(new[] { typeof(AnalysisWidgetDataSourceTests).Assembly });

            _engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default);

            var services = new ServiceCollection();
            _serviceProvider = services.BuildServiceProvider();
        }

        private AnalysisWidgetDataSource CreateSource()
            => new(_registry, _serviceProvider, _engine);

        // ─── Tests ───────────────────────────────────────────────────────────

        [TestMethod]
        public async Task GetDataAsync_returns_columns_and_rows()
        {
            var source = CreateSource();
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(DashSaleRecordListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Region" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Amount", Func = AggregateFunc.Sum }
                    })
                }
            };

            var result = await source.GetDataAsync(request);

            Assert.IsNotNull(result);
            Assert.IsNotNull(result.Columns);
            Assert.IsNotNull(result.Rows);
            Assert.AreEqual(2, result.Columns.Count); // Region, Amount_Sum
            Assert.IsTrue(result.Columns.Contains("Region"));
            Assert.IsTrue(result.Columns.Contains("Amount_Sum"));
            Assert.AreEqual(2, result.Rows.Count); // North, South

            var northRow = result.Rows.First(r => r["Region"]?.ToString() == "North");
            Assert.AreEqual(300m, Convert.ToDecimal(northRow["Amount_Sum"]));

            var southRow = result.Rows.First(r => r["Region"]?.ToString() == "South");
            Assert.AreEqual(150m, Convert.ToDecimal(southRow["Amount_Sum"]));
        }

        [TestMethod]
        public async Task GetDataAsync_throws_for_unregistered_listvm()
        {
            var source = CreateSource();
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = "Some.Nonexistent.ListVM"
                }
            };

            await Assert.ThrowsExceptionAsync<AnalysisVmNotFoundException>(
                () => source.GetDataAsync(request));
        }

        [TestMethod]
        public async Task GetDataAsync_throws_when_listVmType_missing()
        {
            var source = CreateSource();
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>()
            };

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => source.GetDataAsync(request));
        }

        /// <summary>
        /// #795: MaxFilterClauses used to be enforced only by _AnalysisController's own
        /// pre-checks. AnalysisWidgetDataSource — a second HTTP path into the same
        /// _engine.ExecuteDynamicAsync — had no cap at all, so an oversized Filters list would
        /// reach ApplyFilters and build an unbounded left-deep Expression.AndAlso tree (a
        /// StackOverflowException risk that .NET cannot catch). The guard now lives in the
        /// engine's ValidateFields, so this second caller must inherit it without any
        /// widget-specific code change.
        /// </summary>
        [TestMethod]
        public async Task GetDataAsync_throws_when_filters_exceed_MaxFilterClauses()
        {
            var oversized = Enumerable.Range(0, AnalysisLimits.MaxFilterClauses + 1)
                .Select(i => new FilterCondition { Field = "Region", Operator = FilterOperator.Eq, Value = $"R{i}" })
                .ToList();

            var source = CreateSource();
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(DashSaleRecordListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Region" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Amount", Func = AggregateFunc.Sum }
                    }),
                    ["filters"] = JsonSerializer.Serialize(oversized)
                }
            };

            await Assert.ThrowsExceptionAsync<AnalysisException>(
                () => source.GetDataAsync(request));
        }

        /// <summary>
        /// #795 HIGH: <c>Dimensions</c>/<c>Measures</c> were still capped only in
        /// <c>_AnalysisController</c>'s own pre-checks, so the widget path — whose caller can
        /// override the stored widget's <c>dimensions</c>/<c>measures</c> via the request body
        /// (<c>EfCoreDashboardService.GetWidgetDataAsync</c> merges saved values only when the
        /// key is absent) — remained unbounded on exactly these two lists. Proves
        /// <see cref="AnalysisLimits.MaxGroupByFields"/> is now enforced at the engine boundary
        /// for this second caller too.
        /// </summary>
        [TestMethod]
        public async Task GetDataAsync_throws_when_dimensions_exceed_MaxGroupByFields()
        {
            // Count-only cap: ValidateClauseCounts runs before the per-field whitelist check,
            // so repeating a single valid field name is enough to exercise the cap in isolation.
            var oversizedDimensions = Enumerable.Repeat("Region", AnalysisLimits.MaxGroupByFields + 1).ToList();

            var source = CreateSource();
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(DashSaleRecordListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(oversizedDimensions),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Amount", Func = AggregateFunc.Sum }
                    })
                }
            };

            await Assert.ThrowsExceptionAsync<AnalysisException>(
                () => source.GetDataAsync(request));
        }

        /// <summary>
        /// #795 MEDIUM: the widget path used to build its aggregation over the
        /// unfiltered <c>GetSearchQuery()</c> result regardless of the caller's row-level
        /// <c>DataPrivilege</c> — <c>_AnalysisController</c> applied
        /// <c>DCExtension.ApplyDataPrivilegeForAnalysis</c> as defense-in-depth but
        /// <c>AnalysisWidgetDataSource</c> did not, so a dashboard widget returned fully
        /// unscoped data to a user whose row-level privilege only grants access to a
        /// single record. This proves the widget path's <c>GetDataAsync</c> now aggregates
        /// only the row the caller's <c>DataPrivileges</c> grant access to.
        /// </summary>
        [TestMethod]
        public async Task GetDataAsync_applies_DataPrivilege_row_filtering()
        {
            var allowedRow = _testData.Single(x => x.Region == "North" && x.Amount == 100m);

            var dpSettings = new List<IDataPrivilege>
            {
                new DPTestPrivilegeInfo
                {
                    ModelName = nameof(DashSaleRecord),
                    PrivillegeName = nameof(DashSaleRecord),
                    ModelType = typeof(TopBasePoco)
                }
            };
            var wtm = new WTMContext(null, new GlobalData(), null, null, dpSettings);
            wtm.MSD = new BasicMSD();
            wtm.DC = new EmptyContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
            wtm.LoginUserInfo = new LoginUserInfo
            {
                ITCode = "scopeduser",
                DataPrivileges = new List<SimpleDataPri>
                {
                    new SimpleDataPri
                    {
                        ID = Guid.NewGuid(),
                        TableName = nameof(DashSaleRecord),
                        RelateId = allowedRow.ID.ToString(),
                        UserCode = "scopeduser"
                    }
                }
            };

            var source = CreateSourceWithWtm(wtm);
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(DashSaleRecordListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Region" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Amount", Func = AggregateFunc.Sum }
                    })
                }
            };

            var result = await source.GetDataAsync(request);

            Assert.AreEqual(1, result.Rows.Count,
                "only the single row this user's DataPrivilege grants access to should be aggregated");
            var row = result.Rows.Single();
            Assert.AreEqual("North", row["Region"]?.ToString());
            Assert.AreEqual(100m, Convert.ToDecimal(row["Amount_Sum"]),
                "the North/200 row must be excluded — it is not covered by the granted RelateId");
        }

        [TestMethod]
        public void Properties_return_expected_values()
        {
            var source = CreateSource();
            Assert.AreEqual("analysis", source.Name);
            Assert.AreEqual(WidgetDataSourceKind.Analysis, source.Kind);
        }

        // ─── RBAC tests (#529) ───────────────────────────────────────────────

        /// <summary>VM that requires the "Analyst" role via EnableAnalysisAttribute.</summary>
        [EnableAnalysis(AllowedRoles = "Analyst")]
        private class RestrictedSaleListVM : BasePagedListVM<DashSaleRecord, BaseSearcher>
        {
            public override IOrderedQueryable<DashSaleRecord> GetSearchQuery()
                => _testData.AsQueryable().OrderByDescending(x => x.ID);
        }

        private AnalysisWidgetDataSource CreateSourceWithWtm(WTMContext wtm, IAnalysisFieldPolicy? policy = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton(wtm);
            var sp = services.BuildServiceProvider();
            return new AnalysisWidgetDataSource(_registry, sp, _engine, policy);
        }

        /// <summary>Creates a WTMContext with the given role codes set on LoginUserInfo.</summary>
        private static WTMContext CreateWtmWithRoles(params string[] roleCodes)
        {
            var wtm = MockWtmContext.CreateWtmContext();
            wtm.LoginUserInfo = new LoginUserInfo
            {
                ITCode = "testuser",
                Roles = roleCodes.Select(c => new SimpleRole { RoleCode = c, RoleName = $"Display_{c}" }).ToList()
            };
            return wtm;
        }

        [TestMethod]
        public async Task CheckAccess_no_AllowedRoles_allows_any_user()
        {
            // DashSaleRecordListVM has [EnableAnalysis] without AllowedRoles → open to all
            var wtm = CreateWtmWithRoles(/* no roles */);
            var source = CreateSourceWithWtm(wtm);

            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(DashSaleRecordListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Region" }),
                    ["measures"] = JsonSerializer.Serialize(new[] { new { Field = "Amount", Func = AggregateFunc.Sum } })
                }
            };

            // Should NOT throw
            var result = await source.GetDataAsync(request);
            Assert.IsNotNull(result);
        }

        [TestMethod]
        public async Task CheckAccess_required_role_present_allows_access()
        {
            var wtm = CreateWtmWithRoles("Analyst");
            var source = CreateSourceWithWtm(wtm);

            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(RestrictedSaleListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Region" }),
                    ["measures"] = JsonSerializer.Serialize(new[] { new { Field = "Amount", Func = AggregateFunc.Sum } })
                }
            };

            var result = await source.GetDataAsync(request);
            Assert.IsNotNull(result);
        }

        [TestMethod]
        public async Task CheckAccess_missing_role_throws_Unauthorized()
        {
            var wtm = CreateWtmWithRoles("Viewer"); // does NOT have "Analyst"
            var source = CreateSourceWithWtm(wtm);

            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(RestrictedSaleListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Region" }),
                    ["measures"] = JsonSerializer.Serialize(new[] { new { Field = "Amount", Func = AggregateFunc.Sum } })
                }
            };

            await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(
                () => source.GetDataAsync(request));
        }

        [TestMethod]
        public async Task CheckAccess_Admin_bypasses_AllowedRoles()
        {
            var wtm = CreateWtmWithRoles("Admin");
            var source = CreateSourceWithWtm(wtm);

            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(RestrictedSaleListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Region" }),
                    ["measures"] = JsonSerializer.Serialize(new[] { new { Field = "Amount", Func = AggregateFunc.Sum } })
                }
            };

            var result = await source.GetDataAsync(request);
            Assert.IsNotNull(result);
        }

        [TestMethod]
        public async Task CheckAccess_no_login_info_is_denied_for_restricted_vm()
        {
            var wtm = MockWtmContext.CreateWtmContext();
            wtm.LoginUserInfo = new LoginUserInfo { ITCode = "anonymous", Roles = null };
            var source = CreateSourceWithWtm(wtm);

            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(RestrictedSaleListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Region" }),
                    ["measures"] = JsonSerializer.Serialize(new[] { new { Field = "Amount", Func = AggregateFunc.Sum } })
                }
            };

            await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(
                () => source.GetDataAsync(request));
        }

        [TestMethod]
        public async Task FieldPolicy_filters_columns_before_query()
        {
            // Policy that removes Amount field
            var policy = new BlockAmountFieldPolicy();
            var wtm = CreateWtmWithRoles(); // open VM — no AllowedRoles restriction
            var source = CreateSourceWithWtm(wtm, policy);

            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(DashSaleRecordListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Region" }),
                    ["measures"] = JsonSerializer.Serialize(new[] { new { Field = "Amount", Func = AggregateFunc.Sum } })
                }
            };

            // The engine rejects the request because "Amount" was stripped from the whitelist
            // by the field policy before reaching the engine — it raises AnalysisFieldNotFoundException.
            await Assert.ThrowsExceptionAsync<AnalysisFieldNotFoundException>(
                () => source.GetDataAsync(request));
        }

        [TestMethod]
        public async Task CheckAccess_uses_RoleCode_not_RoleName()
        {
            // RoleCode = "Analyst" (matches AllowedRoles), RoleName = "分析師" (different)
            // → should be ALLOWED (comparing on RoleCode)
            var wtm = MockWtmContext.CreateWtmContext();
            wtm.LoginUserInfo = new LoginUserInfo
            {
                ITCode = "testuser",
                Roles = new List<SimpleRole>
                {
                    new SimpleRole { RoleCode = "Analyst", RoleName = "分析師" }
                }
            };
            var source = CreateSourceWithWtm(wtm);

            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(RestrictedSaleListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Region" }),
                    ["measures"] = JsonSerializer.Serialize(new[] { new { Field = "Amount", Func = AggregateFunc.Sum } })
                }
            };

            var result = await source.GetDataAsync(request);
            Assert.IsNotNull(result, "RoleCode matches AllowedRoles → access should be granted");
        }

        [TestMethod]
        public async Task CheckAccess_denied_when_only_RoleName_matches_not_RoleCode()
        {
            // RoleCode = "viewer", RoleName = "Analyst" — only RoleName matches AllowedRoles
            // → should be DENIED (we compare RoleCode, not RoleName)
            var wtm = MockWtmContext.CreateWtmContext();
            wtm.LoginUserInfo = new LoginUserInfo
            {
                ITCode = "testuser",
                Roles = new List<SimpleRole>
                {
                    new SimpleRole { RoleCode = "viewer", RoleName = "Analyst" }
                }
            };
            var source = CreateSourceWithWtm(wtm);

            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(RestrictedSaleListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Region" }),
                    ["measures"] = JsonSerializer.Serialize(new[] { new { Field = "Amount", Func = AggregateFunc.Sum } })
                }
            };

            await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(
                () => source.GetDataAsync(request),
                "RoleName matches but RoleCode does not → access must be denied");
        }

        private class BlockAmountFieldPolicy : IAnalysisFieldPolicy
        {
            public IEnumerable<AnalysisFieldMeta> Filter(IEnumerable<AnalysisFieldMeta> fields, ClaimsPrincipal user)
                => fields.Where(f => f.FieldName != "Amount");
        }
    }
}