#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Dashboard;
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

        [TestMethod]
        public void Properties_return_expected_values()
        {
            var source = CreateSource();
            Assert.AreEqual("analysis", source.Name);
            Assert.AreEqual(WidgetDataSourceKind.Analysis, source.Kind);
        }
    }
}
