#nullable enable
// ─────────────────────────────────────────────────────────────────────────────
// 客戶分群分析整合測試 (#444)
//
// 業務場景：BA / CRM 人員分析 VIP、一般、低活躍客戶的收入貢獻比例
//
// 覆蓋：多維度 GroupBy、Count/Sum/Avg 聚合、In 過濾、佔比計算
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Dashboard;

namespace WalkingTec.Mvvm.Core.Test.Integration
{
    // ─── 領域模型 ────────────────────────────────────────────────────────────

    internal class CustomerOrder : TopBasePoco
    {
        [Dimension(DisplayName = "客戶分群")]
        public string CustomerTier { get; set; } = "";  // VIP / Regular / LowActivity

        [Dimension(DisplayName = "地區")]
        public string Region { get; set; } = "";

        [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Avg | AggregateFunc.Count,
                 DisplayName = "收入")]
        public decimal Revenue { get; set; }
    }

    internal static class SegTestDataStore
    {
        public static IList<CustomerOrder> Orders { get; set; } = new List<CustomerOrder>();
    }

    [EnableAnalysis]
    internal class CustomerOrderListVM : BasePagedListVM<CustomerOrder, BaseSearcher>
    {
        public override IOrderedQueryable<CustomerOrder> GetSearchQuery()
            => SegTestDataStore.Orders.AsQueryable().OrderByDescending(x => x.ID);
    }

    // ─── 測試主體 ─────────────────────────────────────────────────────────────

    [TestClass]
    public class CustomerSegmentationIntegrationTests
    {
        private AnalysisQueryEngine _engine = null!;
        private AnalysisVmRegistry _registry = null!;
        private IServiceProvider _serviceProvider = null!;

        private static readonly string VmType = typeof(CustomerOrderListVM).FullName!;

        [TestInitialize]
        public void Setup()
        {
            _engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default);
            _registry = new AnalysisVmRegistry();
            _registry.Build(new[] { typeof(CustomerSegmentationIntegrationTests).Assembly });
            _serviceProvider = new ServiceCollection().BuildServiceProvider();
        }

        [TestCleanup]
        public void Cleanup() => SegTestDataStore.Orders = new List<CustomerOrder>();

        private AnalysisWidgetDataSource CreateSource()
            => new(_registry, _serviceProvider, _engine);

        // ─── 測試資料工廠 ─────────────────────────────────────────────────────

        private static List<CustomerOrder> BuildSegmentationData()
        {
            var orders = new List<CustomerOrder>();
            // VIP: 3 筆 × 10_000 = 30_000
            for (int i = 0; i < 3; i++)
                orders.Add(new CustomerOrder { ID = Guid.NewGuid(), CustomerTier = "VIP", Region = "北部", Revenue = 10_000m });
            // Regular: 5 筆 × 2_000 = 10_000
            for (int i = 0; i < 5; i++)
                orders.Add(new CustomerOrder { ID = Guid.NewGuid(), CustomerTier = "Regular", Region = "南部", Revenue = 2_000m });
            // LowActivity: 2 筆 × 500 = 1_000
            for (int i = 0; i < 2; i++)
                orders.Add(new CustomerOrder { ID = Guid.NewGuid(), CustomerTier = "LowActivity", Region = "南部", Revenue = 500m });
            return orders;
        }

        // ─── 正向測試 ─────────────────────────────────────────────────────────

        [TestMethod]
        [Description("客戶分群：各群 Revenue Sum/Count/Avg 聚合正確")]
        public async Task Segment_RevenueAggregation_ByTier_CorrectTotals()
        {
            SegTestDataStore.Orders = BuildSegmentationData();
            var source = CreateSource();

            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = VmType,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "CustomerTier" }),
                    ["measures"]   = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Revenue", Func = AggregateFunc.Sum },
                        new { Field = "Revenue", Func = AggregateFunc.Count },
                        new { Field = "Revenue", Func = AggregateFunc.Avg },
                    })
                }
            };

            var result = await source.GetDataAsync(request);

            result.Should().NotBeNull();
            result.Rows.Should().NotBeNull();
            result.Rows!.Should().HaveCount(3, "三個分群");

            var vip  = result.Rows.First(r => r["CustomerTier"]?.ToString() == "VIP");
            var reg  = result.Rows.First(r => r["CustomerTier"]?.ToString() == "Regular");
            var low  = result.Rows.First(r => r["CustomerTier"]?.ToString() == "LowActivity");

            Convert.ToDecimal(vip["Revenue_Sum"]).Should().Be(30_000m);
            Convert.ToDecimal(vip["Revenue_Count"]).Should().Be(3m);
            Convert.ToDecimal(vip["Revenue_Avg"]).Should().Be(10_000m);

            Convert.ToDecimal(reg["Revenue_Sum"]).Should().Be(10_000m);
            Convert.ToDecimal(reg["Revenue_Count"]).Should().Be(5m);
            Convert.ToDecimal(reg["Revenue_Avg"]).Should().Be(2_000m);

            Convert.ToDecimal(low["Revenue_Sum"]).Should().Be(1_000m);
        }

        [TestMethod]
        [Description("客戶分群：VIP 佔總收入 30/41 ≈ 73.17%")]
        public async Task Segment_VIP_RevenueContribution_Percentage_Correct()
        {
            SegTestDataStore.Orders = BuildSegmentationData();
            var source = CreateSource();

            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = VmType,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "CustomerTier" }),
                    ["measures"]   = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Revenue", Func = AggregateFunc.Sum }
                    })
                }
            };

            var result = await source.GetDataAsync(request);
            var total = result.Rows!.Sum(r => Convert.ToDecimal(r["Revenue_Sum"]));
            var vipSum = Convert.ToDecimal(result.Rows!.First(r => r["CustomerTier"]?.ToString() == "VIP")["Revenue_Sum"]);

            total.Should().Be(41_000m, "VIP 30K + Regular 10K + LowActivity 1K = 41K");
            var pct = vipSum / total * 100;
            pct.Should().BeApproximately(73.17m, 0.1m, "VIP 佔總收入約 73.17%");
        }

        [TestMethod]
        [Description("多維度分群 × 地區 — GroupBy 兩維度結果正確")]
        public async Task Segment_MultiDimension_TierAndRegion_CorrectGrouping()
        {
            SegTestDataStore.Orders = BuildSegmentationData();
            var source = CreateSource();

            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = VmType,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "CustomerTier", "Region" }),
                    ["measures"]   = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Revenue", Func = AggregateFunc.Sum }
                    })
                }
            };

            var result = await source.GetDataAsync(request);

            result.Rows.Should().NotBeNull();
            // VIP × 北部, Regular × 南部, LowActivity × 南部 = 3 combinations
            result.Rows!.Should().HaveCount(3);

            var vipNorth = result.Rows.FirstOrDefault(r =>
                r["CustomerTier"]?.ToString() == "VIP" && r["Region"]?.ToString() == "北部");
            vipNorth.Should().NotBeNull("VIP × 北部 應有一列");
            Convert.ToDecimal(vipNorth!["Revenue_Sum"]).Should().Be(30_000m);
        }
    }
}
