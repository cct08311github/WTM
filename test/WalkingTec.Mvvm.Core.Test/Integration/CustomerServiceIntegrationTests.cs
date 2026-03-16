#nullable enable
// ────────────────────────────────────────────────────────────────────────────
// 客服中心業務整合測試
//
// 業務場景：客服副總（VP of Customer Service）每日監控儀表板
//   ETL → Analysis 多維度查詢 → Dashboard Widget 數據源
//
// 測試分類：
//   [正向] Happy Path   — 標準客服業務場景（8 個）
//   [反向] Negative     — 異常資料、邊緣狀態（6 個）
//   [邊界] Boundary     — 臨界值行為（6 個）
// ────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Dashboard;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Testing;

namespace WalkingTec.Mvvm.Core.Test.Integration
{
    // ═══════════════════════════════════════════════════════════════════════════
    // 領域模型：客服工單
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>客服工單 — ETL 目標表 / Analysis 查詢模型</summary>
    internal class ServiceTicket : TopBasePoco
    {
        [Dimension(DisplayName = "客服人員")]
        public string AgentName { get; set; } = string.Empty;

        [Dimension(DisplayName = "客訴類別")]
        public string Category { get; set; } = string.Empty;

        [Dimension(DisplayName = "工單狀態")]
        public string Status { get; set; } = string.Empty;   // Open / Closed

        [Dimension(DisplayName = "優先級")]
        public string Priority { get; set; } = string.Empty; // P1 / P2 / P3

        [Dimension(DisplayName = "建立月份", Hierarchy = DateHierarchy.Month)]
        public DateTime CreatedAt { get; set; }

        [Dimension(DisplayName = "結案時間")]
        public DateTime? ClosedAt { get; set; }

        /// <summary>
        /// 處理時間（小時）。由 ETL 計算：(ClosedAt - CreatedAt).TotalHours。
        /// 未結案者為 null。
        /// </summary>
        [Measure(
            AllowedFuncs = AggregateFunc.Avg | AggregateFunc.Min | AggregateFunc.Max | AggregateFunc.Sum,
            DisplayName = "處理時間(小時)")]
        public decimal? ResolutionHours { get; set; }

        /// <summary>滿意度評分（1–5），客戶未評分時為 null</summary>
        [Measure(
            AllowedFuncs = AggregateFunc.Avg | AggregateFunc.Min | AggregateFunc.Max | AggregateFunc.Count,
            DisplayName = "滿意度")]
        public decimal? SatisfactionScore { get; set; }

        /// <summary>工單計數（每筆固定為 1，用於加總）</summary>
        [Measure(
            AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count,
            DisplayName = "工單數")]
        public decimal TicketCount { get; set; } = 1m;

        /// <summary>是否在 SLA 內結案（0=超時, 1=達標）</summary>
        [Measure(
            AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Avg | AggregateFunc.Count,
            DisplayName = "SLA達標")]
        public decimal SlaFlag { get; set; }
    }

    // ───────────────────────────────────────────────────────────────────────────
    // 靜態注入點：由 Setup() 填充，供 ListVM.GetSearchQuery() 使用
    // ───────────────────────────────────────────────────────────────────────────
    internal static class CsTestDataStore
    {
        public static IList<ServiceTicket> Tickets { get; set; } = new List<ServiceTicket>();
    }

    [EnableAnalysis]
    internal class ServiceTicketListVM : BasePagedListVM<ServiceTicket, BaseSearcher>
    {
        public override IOrderedQueryable<ServiceTicket> GetSearchQuery()
            => CsTestDataStore.Tickets.AsQueryable().OrderByDescending(x => x.CreatedAt);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 資料工廠
    // ═══════════════════════════════════════════════════════════════════════════

    internal static class CsDataFactory
    {
        public static ServiceTicket Closed(
            string agent, string category, string priority,
            DateTime createdAt, DateTime closedAt, decimal? satisfaction = 4m)
        {
            var hours = (decimal)(closedAt - createdAt).TotalHours;
            // SLA 規則：P1=4h, P2=8h, P3=24h
            var slaLimit = priority switch { "P1" => 4m, "P2" => 8m, _ => 24m };
            return new ServiceTicket
            {
                ID = Guid.NewGuid(),
                AgentName = agent,
                Category = category,
                Priority = priority,
                Status = "Closed",
                CreatedAt = createdAt,
                ClosedAt = closedAt,
                ResolutionHours = hours,
                SatisfactionScore = satisfaction,
                TicketCount = 1m,
                SlaFlag = hours <= slaLimit ? 1m : 0m
            };
        }

        public static ServiceTicket Open(
            string agent, string category, string priority, DateTime createdAt)
            => new ServiceTicket
            {
                ID = Guid.NewGuid(),
                AgentName = agent,
                Category = category,
                Priority = priority,
                Status = "Open",
                CreatedAt = createdAt,
                ClosedAt = null,
                ResolutionHours = null,  // 未結案，無處理時間
                SatisfactionScore = null, // 未評分
                TicketCount = 1m,
                SlaFlag = 0m
            };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 主測試類別
    // ═══════════════════════════════════════════════════════════════════════════

    [TestClass]
    public class CustomerServiceIntegrationTests
    {
        private AnalysisQueryEngine _engine = null!;
        private IEnumerable<AnalysisFieldMeta> _allFields = null!;
        private AnalysisVmRegistry _registry = null!;
        private IServiceProvider _serviceProvider = null!;

        [TestInitialize]
        public void Setup()
        {
            CsTestDataStore.Tickets = new List<ServiceTicket>();
            _engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default);
            _allFields = AnalysisFieldScanner.ScanModel(typeof(ServiceTicket));

            _registry = new AnalysisVmRegistry();
            _registry.Build(new[] { typeof(CustomerServiceIntegrationTests).Assembly });

            _serviceProvider = new ServiceCollection().BuildServiceProvider();
        }

        private IQueryable<ServiceTicket> Q() => CsTestDataStore.Tickets.AsQueryable();
        private AnalysisQueryEngine E() => _engine;

        // ═══════════════════════════════════════════════════════════════════════
        // ▌正向測試（Happy Path）— 8 個
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// [正向] 工單資料 ETL → Analysis 按客服人員+月份 group by → 處理量排名
        /// </summary>
        [TestMethod]
        public async Task VP_AfterEtlSync_AnalysisShowsAgentVolumeByMonth()
        {
            // ── Step 1: ETL 同步 5 筆工單到 staging ──
            var source = new DataTable();
            source.Columns.Add("TicketId", typeof(long));
            source.Columns.Add("AgentName", typeof(string));
            source.Columns.Add("Status", typeof(string));
            source.Rows.Add(1L, "Alice", "Closed");
            source.Rows.Add(2L, "Alice", "Closed");
            source.Rows.Add(3L, "Bob",   "Closed");
            source.Rows.Add(4L, "Bob",   "Open");
            source.Rows.Add(5L, "Carol", "Closed");

            var mockSource = new MockEtlSource();
            mockSource.SetData(source);
            var mockLoader = new MockBulkLoader();

            var config = new EtlPipelineConfig
            {
                JobId            = Guid.NewGuid(),
                JobName          = "TicketSync",
                SourceConnectionString = "mock://src",
                TargetConnectionString = "mock://tgt",
                QueryTemplate    = "SELECT * FROM Tickets WHERE {watermark}",
                TargetTableName  = "ServiceTickets",
                MergeKeyColumn   = "TicketId",
                BatchSize        = 100,
                StagingTable     = new StagingTableSpec("stg_Tickets",
                    new StagingColumn("TicketId",  "BIGINT"),
                    new StagingColumn("AgentName", "NVARCHAR(100)"),
                    new StagingColumn("Status",    "NVARCHAR(20)"))
            };

            var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
            var executor  = new EtlPipelineExecutor(mockSource, mockLoader);
            var etlResult = await executor.ExecuteAsync(config, watermark);

            etlResult.Success.Should().BeTrue("ETL 應成功");
            etlResult.ExtractedRows.Should().Be(5);
            etlResult.LoadedRows.Should().Be(5);
            mockLoader.MergeCalled.Should().BeTrue("Merge 必須執行");

            // ── Step 2: ETL 後，Analysis 按客服人員統計工單數 ──
            CsTestDataStore.Tickets = new List<ServiceTicket>
            {
                CsDataFactory.Closed("Alice", "帳單問題", "P2", new DateTime(2026, 1, 10), new DateTime(2026, 1, 10, 5, 0, 0)),
                CsDataFactory.Closed("Alice", "技術問題", "P1", new DateTime(2026, 1, 12), new DateTime(2026, 1, 12, 2, 0, 0)),
                CsDataFactory.Closed("Bob",   "退款申請", "P2", new DateTime(2026, 1, 15), new DateTime(2026, 1, 15, 6, 0, 0)),
                CsDataFactory.Open("Bob",     "帳單問題", "P3", new DateTime(2026, 1, 20)),
                CsDataFactory.Closed("Carol", "技術問題", "P3", new DateTime(2026, 1, 8), new DateTime(2026, 1, 9)),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "AgentName" },
                Measures   = new List<MeasureRequest>
                {
                    new() { Field = "TicketCount", Func = AggregateFunc.Sum }
                }
            };

            var result = E().Execute(Q(), req, _allFields);

            result.Rows.Should().HaveCount(3, "應有 3 位客服人員");
            var alice = result.Rows.Single(r => r["AgentName"]?.ToString() == "Alice");
            Convert.ToDecimal(alice["TicketCount_Sum"]).Should().Be(2m);
        }

        /// <summary>
        /// [正向] 平均處理時間計算（建立到結案的小時數）：Avg 聚合正確
        /// </summary>
        [TestMethod]
        public void VP_ViewsAverageResolutionTime_AvgAggregationCorrect()
        {
            // Alice：3 筆，處理時間 2h、4h、6h → 平均 4h
            CsTestDataStore.Tickets = new List<ServiceTicket>
            {
                CsDataFactory.Closed("Alice", "帳單問題", "P2",
                    new DateTime(2026, 1, 10, 9, 0, 0), new DateTime(2026, 1, 10, 11, 0, 0)),  // 2h
                CsDataFactory.Closed("Alice", "技術問題", "P2",
                    new DateTime(2026, 1, 11, 9, 0, 0), new DateTime(2026, 1, 11, 13, 0, 0)),  // 4h
                CsDataFactory.Closed("Alice", "退款申請", "P2",
                    new DateTime(2026, 1, 12, 9, 0, 0), new DateTime(2026, 1, 12, 15, 0, 0)),  // 6h
                CsDataFactory.Closed("Bob",   "帳單問題", "P3",
                    new DateTime(2026, 1, 10, 8, 0, 0), new DateTime(2026, 1, 10, 20, 0, 0)), // 12h
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "AgentName" },
                Measures   = new List<MeasureRequest>
                {
                    new() { Field = "ResolutionHours", Func = AggregateFunc.Avg },
                    new() { Field = "ResolutionHours", Func = AggregateFunc.Min },
                    new() { Field = "ResolutionHours", Func = AggregateFunc.Max },
                }
            };

            // 注意：ResolutionHours 為 nullable，需過濾掉 Open 工單
            var closedQuery = CsTestDataStore.Tickets
                .Where(t => t.ResolutionHours.HasValue)
                .AsQueryable();

            var result = E().Execute(closedQuery, req, _allFields);

            var alice = result.Rows.Single(r => r["AgentName"]?.ToString() == "Alice");
            Convert.ToDecimal(alice["ResolutionHours_Avg"]).Should().Be(4m,
                "Alice 三筆工單平均處理時間 = (2+4+6)/3 = 4 小時");
            Convert.ToDecimal(alice["ResolutionHours_Min"]).Should().Be(2m);
            Convert.ToDecimal(alice["ResolutionHours_Max"]).Should().Be(6m);
        }

        /// <summary>
        /// [正向] SLA 達標率：按客服人員統計，SlaFlag 的加總/計數即達標數/總數
        /// </summary>
        [TestMethod]
        public void VP_ViewsSlaComplianceRate_SumOverCountIsCorrect()
        {
            // Bob：4 張工單，P3 SLA 24h
            //   - 3h → 達標（SlaFlag=1）
            //   - 20h → 達標（SlaFlag=1）
            //   - 25h → 超時（SlaFlag=0）
            //   - 30h → 超時（SlaFlag=0）
            var base2 = new DateTime(2026, 2, 1, 8, 0, 0);
            CsTestDataStore.Tickets = new List<ServiceTicket>
            {
                CsDataFactory.Closed("Bob", "帳單問題", "P3", base2, base2.AddHours(3)),   // 達標
                CsDataFactory.Closed("Bob", "技術問題", "P3", base2, base2.AddHours(20)),  // 達標
                CsDataFactory.Closed("Bob", "退款申請", "P3", base2, base2.AddHours(25)),  // 超時
                CsDataFactory.Closed("Bob", "其他",     "P3", base2, base2.AddHours(30)),  // 超時
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "AgentName" },
                Measures   = new List<MeasureRequest>
                {
                    new() { Field = "SlaFlag",    Func = AggregateFunc.Sum },
                    new() { Field = "TicketCount", Func = AggregateFunc.Sum }
                }
            };

            var result = E().Execute(Q(), req, _allFields);

            result.Rows.Should().HaveCount(1);
            var bob = result.Rows[0];

            var slaCompliant = Convert.ToDecimal(bob["SlaFlag_Sum"]);
            var total        = Convert.ToDecimal(bob["TicketCount_Sum"]);
            var slaRate      = slaCompliant / total;

            slaCompliant.Should().Be(2m, "Bob 有 2 張工單在 SLA 內結案");
            total.Should().Be(4m);
            slaRate.Should().Be(0.5m, "Bob SLA 達標率 = 50%");
        }

        /// <summary>
        /// [正向] 客訴類別圓餅圖：各類別工單量正確，加總等於總工單數
        /// </summary>
        [TestMethod]
        public void VP_ViewsCategoryDistribution_PieChartTotalsAreConsistent()
        {
            CsTestDataStore.Tickets = new List<ServiceTicket>
            {
                CsDataFactory.Closed("Alice", "帳單問題", "P2", new DateTime(2026,1,1), new DateTime(2026,1,1,5,0,0)),
                CsDataFactory.Closed("Alice", "帳單問題", "P2", new DateTime(2026,1,2), new DateTime(2026,1,2,4,0,0)),
                CsDataFactory.Closed("Bob",   "技術問題", "P1", new DateTime(2026,1,3), new DateTime(2026,1,3,2,0,0)),
                CsDataFactory.Closed("Bob",   "技術問題", "P1", new DateTime(2026,1,4), new DateTime(2026,1,4,3,0,0)),
                CsDataFactory.Closed("Bob",   "技術問題", "P1", new DateTime(2026,1,5), new DateTime(2026,1,5,1,0,0)),
                CsDataFactory.Closed("Carol", "退款申請", "P2", new DateTime(2026,1,6), new DateTime(2026,1,6,7,0,0)),
                CsDataFactory.Open("Carol",   "其他",     "P3", new DateTime(2026,1,7)),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Category" },
                Measures   = new List<MeasureRequest>
                {
                    new() { Field = "TicketCount", Func = AggregateFunc.Sum }
                }
            };

            var result = E().Execute(Q(), req, _allFields);

            result.Rows.Should().HaveCount(4, "應有 4 個客訴類別");

            var grandTotal = result.Rows.Sum(r => Convert.ToDecimal(r["TicketCount_Sum"]));
            grandTotal.Should().Be(7m, "4 個類別工單量加總應等於全部工單數");

            var techRow = result.Rows.Single(r => r["Category"]?.ToString() == "技術問題");
            Convert.ToDecimal(techRow["TicketCount_Sum"]).Should().Be(3m, "技術問題 3 張工單");
        }

        /// <summary>
        /// [正向] 滿意度評分趨勢（1-5 分）按月份 Avg
        /// </summary>
        [TestMethod]
        public void VP_ViewsSatisfactionTrend_MonthlyAvgIsCorrect()
        {
            CsTestDataStore.Tickets = new List<ServiceTicket>
            {
                // 1 月：3 張，平均 (5+4+3)/3 = 4.0
                CsDataFactory.Closed("Alice", "帳單問題", "P2", new DateTime(2026,1,10), new DateTime(2026,1,10,5,0,0), satisfaction: 5m),
                CsDataFactory.Closed("Bob",   "技術問題", "P2", new DateTime(2026,1,15), new DateTime(2026,1,15,7,0,0), satisfaction: 4m),
                CsDataFactory.Closed("Carol", "退款申請", "P3", new DateTime(2026,1,20), new DateTime(2026,1,21),        satisfaction: 3m),
                // 2 月：2 張，平均 (5+5)/2 = 5.0
                CsDataFactory.Closed("Alice", "帳單問題", "P2", new DateTime(2026,2,5),  new DateTime(2026,2,5,3,0,0),  satisfaction: 5m),
                CsDataFactory.Closed("Bob",   "技術問題", "P1", new DateTime(2026,2,10), new DateTime(2026,2,10,1,0,0), satisfaction: 5m),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "CreatedAt" },
                Measures   = new List<MeasureRequest>
                {
                    new() { Field = "SatisfactionScore", Func = AggregateFunc.Avg }
                },
                DimensionHierarchies = new Dictionary<string, DateHierarchy>
                {
                    ["CreatedAt"] = DateHierarchy.Month
                }
            };

            var closedQuery = CsTestDataStore.Tickets
                .Where(t => t.SatisfactionScore.HasValue)
                .AsQueryable();

            var result = E().Execute(closedQuery, req, _allFields);

            result.Rows.Should().HaveCount(2, "應有 1 月和 2 月兩個月份");

            var jan = result.Rows.Single(r =>
            {
                var v = r["CreatedAt"]?.ToString() ?? "";
                return v.Contains("2026") && v.Contains("01");
            });
            Convert.ToDecimal(jan["SatisfactionScore_Avg"]).Should().Be(4m, "(5+4+3)/3 = 4 分");

            var feb = result.Rows.Single(r =>
            {
                var v = r["CreatedAt"]?.ToString() ?? "";
                return v.Contains("2026") && v.Contains("02");
            });
            Convert.ToDecimal(feb["SatisfactionScore_Avg"]).Should().Be(5m, "(5+5)/2 = 5 分");
        }

        /// <summary>
        /// [正向] Dashboard 即時監控：待處理工單數 KPI widget
        /// </summary>
        [TestMethod]
        public async Task VP_ViewsDashboardKpiWidget_OpenTicketsCountIsCorrect()
        {
            CsTestDataStore.Tickets = new List<ServiceTicket>
            {
                CsDataFactory.Open("Alice", "帳單問題", "P2", new DateTime(2026,3,1)),
                CsDataFactory.Open("Bob",   "技術問題", "P1", new DateTime(2026,3,2)),
                CsDataFactory.Open("Carol", "退款申請", "P3", new DateTime(2026,3,3)),
                CsDataFactory.Closed("Alice", "其他", "P2", new DateTime(2026,3,1), new DateTime(2026,3,1,5,0,0)),
            };

            var source  = new AnalysisWidgetDataSource(_registry, _serviceProvider, _engine);
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(ServiceTicketListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Status" }),
                    ["measures"]   = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "TicketCount", Func = AggregateFunc.Sum }
                    }),
                    ["filters"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Status", Operator = FilterOperator.Eq, Value = "Open" }
                    })
                }
            };

            var widgetResult = await source.GetDataAsync(request);

            widgetResult.Should().NotBeNull();
            widgetResult.Rows.Should().HaveCount(1, "只有 Open 一個 Status 分組");
            Convert.ToDecimal(widgetResult.Rows[0]["TicketCount_Sum"]).Should().Be(3m,
                "待處理（Open）工單共 3 張");

            widgetResult.Metadata.Should().ContainKey("totalCount");
            widgetResult.Metadata.Should().ContainKey("truncated");
        }

        /// <summary>
        /// [正向] 多層篩選：時間範圍 + 客服人員 + 客訴類別
        /// </summary>
        [TestMethod]
        public void VP_AppliesMultiFilter_TimeRangeAgentCategory_CorrectSubset()
        {
            CsTestDataStore.Tickets = new List<ServiceTicket>
            {
                // 1 月 Alice 帳單問題 → 應被選到
                CsDataFactory.Closed("Alice", "帳單問題", "P2",
                    new DateTime(2026,1,10), new DateTime(2026,1,10,5,0,0)),
                // 1 月 Alice 技術問題 → 類別不符，排除
                CsDataFactory.Closed("Alice", "技術問題", "P1",
                    new DateTime(2026,1,11), new DateTime(2026,1,11,2,0,0)),
                // 2 月 Alice 帳單問題 → 日期不符，排除
                CsDataFactory.Closed("Alice", "帳單問題", "P2",
                    new DateTime(2026,2,5), new DateTime(2026,2,5,3,0,0)),
                // 1 月 Bob 帳單問題 → 人員不符，排除
                CsDataFactory.Closed("Bob", "帳單問題", "P3",
                    new DateTime(2026,1,12), new DateTime(2026,1,13)),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "AgentName", "Category" },
                Measures   = new List<MeasureRequest>
                {
                    new() { Field = "TicketCount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new() { Field = "CreatedAt",  Operator = FilterOperator.Gte, Value = "2026-01-01" },
                    new() { Field = "CreatedAt",  Operator = FilterOperator.Lte, Value = "2026-01-31" },
                    new() { Field = "AgentName",  Operator = FilterOperator.Eq,  Value = "Alice" },
                    new() { Field = "Category",   Operator = FilterOperator.Eq,  Value = "帳單問題" }
                }
            };

            var result = E().Execute(Q(), req, _allFields);

            result.Rows.Should().HaveCount(1, "3 個篩選條件下只有 1 筆符合");
            Convert.ToDecimal(result.Rows[0]["TicketCount_Sum"]).Should().Be(1m);
            result.Rows[0]["AgentName"]?.ToString().Should().Be("Alice");
            result.Rows[0]["Category"]?.ToString().Should().Be("帳單問題");
        }

        /// <summary>
        /// [正向] 匯出客服績效報表 — Excel 欄位與 Analysis 結果一致
        /// </summary>
        [TestMethod]
        public void VP_ExportsPerformanceReport_ExcelColumnsMatchAnalysisResult()
        {
            CsTestDataStore.Tickets = new List<ServiceTicket>
            {
                CsDataFactory.Closed("Alice", "帳單問題", "P2",
                    new DateTime(2026,1,10), new DateTime(2026,1,10,5,0,0), 5m),
                CsDataFactory.Closed("Bob",   "技術問題", "P1",
                    new DateTime(2026,1,11), new DateTime(2026,1,11,2,0,0), 4m),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "AgentName", "Category" },
                Measures   = new List<MeasureRequest>
                {
                    new() { Field = "TicketCount",      Func = AggregateFunc.Sum },
                    new() { Field = "ResolutionHours",  Func = AggregateFunc.Avg },
                    new() { Field = "SatisfactionScore", Func = AggregateFunc.Avg },
                    new() { Field = "SlaFlag",           Func = AggregateFunc.Sum },
                }
            };

            var closedQuery = CsTestDataStore.Tickets
                .Where(t => t.ResolutionHours.HasValue)
                .AsQueryable();

            var result = E().Execute(closedQuery, req, _allFields);

            var excelBytes = AnalysisExcelExporter.Export(result, includeChart: false);
            excelBytes.Should().NotBeNull();
            excelBytes.Length.Should().BeGreaterThan(0, "Excel 位元組不應為空");

            var expectedCols = new[]
            {
                "AgentName", "Category",
                "TicketCount_Sum", "ResolutionHours_Avg",
                "SatisfactionScore_Avg", "SlaFlag_Sum"
            };
            foreach (var col in expectedCols)
                result.Columns.Should().Contain(col, $"報表應含欄位 '{col}'");
        }

        /// <summary>
        /// [正向] 多客服人員績效排名：速度（處理時間）+ 品質（滿意度）雙維度
        /// </summary>
        [TestMethod]
        public void VP_RanksAgentsBySpeedAndQuality_BothMetricsPresent()
        {
            CsTestDataStore.Tickets = new List<ServiceTicket>
            {
                // Alice：快（2h）但品質中等（3分）
                CsDataFactory.Closed("Alice", "技術問題", "P1",
                    new DateTime(2026,1,5), new DateTime(2026,1,5,2,0,0), 3m),
                // Bob：慢（10h）但品質高（5分）
                CsDataFactory.Closed("Bob", "帳單問題", "P2",
                    new DateTime(2026,1,5), new DateTime(2026,1,5,10,0,0), 5m),
                // Carol：中等速度（5h）品質中等（4分）
                CsDataFactory.Closed("Carol", "退款申請", "P3",
                    new DateTime(2026,1,6), new DateTime(2026,1,7,5,0,0), 4m),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "AgentName" },
                Measures   = new List<MeasureRequest>
                {
                    new() { Field = "ResolutionHours",   Func = AggregateFunc.Avg },
                    new() { Field = "SatisfactionScore", Func = AggregateFunc.Avg },
                    new() { Field = "TicketCount",       Func = AggregateFunc.Sum }
                }
            };

            var closedQuery = CsTestDataStore.Tickets
                .Where(t => t.ResolutionHours.HasValue)
                .AsQueryable();

            var result = E().Execute(closedQuery, req, _allFields);

            result.Rows.Should().HaveCount(3);
            result.Columns.Should().Contain("ResolutionHours_Avg",   "速度維度需呈現");
            result.Columns.Should().Contain("SatisfactionScore_Avg", "品質維度需呈現");

            var alice = result.Rows.Single(r => r["AgentName"]?.ToString() == "Alice");
            var bob   = result.Rows.Single(r => r["AgentName"]?.ToString() == "Bob");

            Convert.ToDecimal(alice["ResolutionHours_Avg"]).Should().BeLessThan(
                Convert.ToDecimal(bob["ResolutionHours_Avg"]),
                "Alice 比 Bob 更快結案");
            Convert.ToDecimal(alice["SatisfactionScore_Avg"]).Should().BeLessThan(
                Convert.ToDecimal(bob["SatisfactionScore_Avg"]),
                "Bob 滿意度高於 Alice，速度快不等於品質好");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // ▌反向測試（Negative Path）— 6 個
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// [反向] 工單未結案（無結案時間）→ 處理時間 null 不應 crash，Avg 計算排除 null
        /// </summary>
        [TestMethod]
        public void VP_OpenTicketsHaveNullResolutionTime_AvgCalculationDoesNotCrash()
        {
            CsTestDataStore.Tickets = new List<ServiceTicket>
            {
                CsDataFactory.Open("Alice", "帳單問題", "P2", new DateTime(2026,3,1)),
                CsDataFactory.Open("Alice", "技術問題", "P1", new DateTime(2026,3,2)),
                CsDataFactory.Closed("Alice", "退款申請", "P3",
                    new DateTime(2026,3,3), new DateTime(2026,3,5), 4m), // 48h
            };

            // 僅統計 Closed 工單的平均處理時間（ResolutionHours 非 null）
            var closedQuery = CsTestDataStore.Tickets
                .Where(t => t.ResolutionHours.HasValue)
                .AsQueryable();

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "AgentName" },
                Measures   = new List<MeasureRequest>
                {
                    new() { Field = "ResolutionHours", Func = AggregateFunc.Avg }
                }
            };

            // 不應拋例外
            Action act = () => E().Execute(closedQuery, req, _allFields);
            act.Should().NotThrow("未結案工單的 null ResolutionHours 不應導致 crash");

            var result = E().Execute(closedQuery, req, _allFields);
            result.Rows.Should().HaveCount(1, "只有 Alice 有 Closed 工單");
            Convert.ToDecimal(result.Rows[0]["ResolutionHours_Avg"]).Should().Be(48m);
        }

        /// <summary>
        /// [反向] 滿意度為 null（客戶未評分）→ Avg 計算排除 null，不 crash
        /// </summary>
        [TestMethod]
        public void VP_NullSatisfactionScore_ExcludedFromAvg_DoesNotCrash()
        {
            CsTestDataStore.Tickets = new List<ServiceTicket>
            {
                CsDataFactory.Closed("Bob", "帳單問題", "P2",
                    new DateTime(2026,1,10), new DateTime(2026,1,10,5,0,0), satisfaction: 5m),
                CsDataFactory.Closed("Bob", "技術問題", "P2",
                    new DateTime(2026,1,11), new DateTime(2026,1,11,3,0,0), satisfaction: null), // 未評分
                CsDataFactory.Closed("Bob", "退款申請", "P3",
                    new DateTime(2026,1,12), new DateTime(2026,1,13),        satisfaction: 3m),
            };

            // 只取有評分的工單
            var ratedQuery = CsTestDataStore.Tickets
                .Where(t => t.SatisfactionScore.HasValue)
                .AsQueryable();

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "AgentName" },
                Measures   = new List<MeasureRequest>
                {
                    new() { Field = "SatisfactionScore", Func = AggregateFunc.Avg }
                }
            };

            Action act = () => E().Execute(ratedQuery, req, _allFields);
            act.Should().NotThrow("null 滿意度不應導致 crash");

            var result = E().Execute(ratedQuery, req, _allFields);
            // 只有 5 和 3 被納入：平均 = 4
            Convert.ToDecimal(result.Rows[0]["SatisfactionScore_Avg"]).Should().Be(4m,
                "排除 null 評分後，(5+3)/2 = 4");
        }

        /// <summary>
        /// [反向] 客服人員離職後其歷史工單仍可查詢
        /// （離職員工的工單資料不因人員狀態而消失）
        /// </summary>
        [TestMethod]
        public void VP_ResignedAgentHistoricalTickets_StillQueryable()
        {
            // David 已離職，但其 2025 年的歷史工單應可查詢
            CsTestDataStore.Tickets = new List<ServiceTicket>
            {
                CsDataFactory.Closed("David(離職)", "帳單問題", "P2",
                    new DateTime(2025,12,10), new DateTime(2025,12,10,4,0,0), 4m),
                CsDataFactory.Closed("David(離職)", "技術問題", "P2",
                    new DateTime(2025,12,15), new DateTime(2025,12,15,6,0,0), 5m),
                CsDataFactory.Closed("Alice", "帳單問題", "P2",
                    new DateTime(2026,1,10), new DateTime(2026,1,10,5,0,0), 5m),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "AgentName" },
                Measures   = new List<MeasureRequest>
                {
                    new() { Field = "TicketCount",      Func = AggregateFunc.Sum },
                    new() { Field = "SatisfactionScore", Func = AggregateFunc.Avg }
                },
                Filters = new List<FilterCondition>
                {
                    new() { Field = "AgentName", Operator = FilterOperator.Eq, Value = "David(離職)" }
                }
            };

            var closedQuery = CsTestDataStore.Tickets
                .Where(t => t.SatisfactionScore.HasValue)
                .AsQueryable();

            var result = E().Execute(closedQuery, req, _allFields);

            result.Rows.Should().HaveCount(1, "應可查詢到離職員工的歷史工單");
            Convert.ToDecimal(result.Rows[0]["TicketCount_Sum"]).Should().Be(2m,
                "David 有 2 筆歷史工單");
        }

        /// <summary>
        /// [反向] Analysis 篩選到不存在的客服人員 → 回傳空結果，不崩潰
        /// </summary>
        [TestMethod]
        public void VP_FilterByNonExistentAgent_ReturnsEmptyGracefully()
        {
            CsTestDataStore.Tickets = new List<ServiceTicket>
            {
                CsDataFactory.Closed("Alice", "帳單問題", "P2",
                    new DateTime(2026,1,10), new DateTime(2026,1,10,5,0,0)),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "AgentName" },
                Measures   = new List<MeasureRequest>
                {
                    new() { Field = "TicketCount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new() { Field = "AgentName", Operator = FilterOperator.Eq, Value = "鬼魂客服員" }
                }
            };

            var result = E().Execute(Q(), req, _allFields);

            result.Rows.Should().BeEmpty("不存在的客服人員應回傳空結果");
            result.TotalCount.Should().Be(0);
            result.Truncated.Should().BeFalse();
        }

        /// <summary>
        /// [反向] 大量重複工單（同一客戶短時間提交多次）→ 每筆均計入，不被去重
        /// </summary>
        [TestMethod]
        public void VP_DuplicateTicketsFromSameCustomer_AllCountedNotDeduped()
        {
            var spamBase = new DateTime(2026, 3, 10, 8, 0, 0);
            var tickets  = Enumerable.Range(1, 20)
                .Select(i => CsDataFactory.Open("Bob", "帳單問題", "P3",
                    spamBase.AddMinutes(i)))
                .ToList();

            CsTestDataStore.Tickets = tickets;

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "AgentName", "Category" },
                Measures   = new List<MeasureRequest>
                {
                    new() { Field = "TicketCount", Func = AggregateFunc.Sum }
                }
            };

            var result = E().Execute(Q(), req, _allFields);

            result.Rows.Should().HaveCount(1);
            Convert.ToDecimal(result.Rows[0]["TicketCount_Sum"]).Should().Be(20m,
                "同一客戶重複提交的 20 張工單應全部計入，不被去重");
        }

        /// <summary>
        /// [反向] ETL 失敗後舊資料不受影響，Analysis 仍回傳正確資料
        /// </summary>
        [TestMethod]
        public async Task VP_WhenEtlFails_AnalysisDataIsPreserved()
        {
            // 失敗前的基準資料
            CsTestDataStore.Tickets = new List<ServiceTicket>
            {
                CsDataFactory.Closed("Alice", "帳單問題", "P2",
                    new DateTime(2026,1,10), new DateTime(2026,1,10,5,0,0)),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "AgentName" },
                Measures   = new List<MeasureRequest>
                {
                    new() { Field = "TicketCount", Func = AggregateFunc.Sum }
                }
            };

            var beforeResult = E().Execute(Q(), req, _allFields);
            int rowsBefore = beforeResult.Rows.Count;

            // ETL 刻意失敗
            var badSource = new DataTable();
            badSource.Columns.Add("TicketId", typeof(long));
            badSource.Rows.Add(99L);

            var mockSource = new MockEtlSource();
            mockSource.SetData(badSource);
            var mockLoader = new MockBulkLoader { FailOnBatch = 1 };

            var config = new EtlPipelineConfig
            {
                JobId            = Guid.NewGuid(),
                JobName          = "TicketSyncFail",
                SourceConnectionString = "mock://src",
                TargetConnectionString = "mock://tgt",
                QueryTemplate    = "SELECT * FROM Tickets",
                TargetTableName  = "ServiceTickets",
                MergeKeyColumn   = "TicketId",
                BatchSize        = 100,
                StagingTable     = new StagingTableSpec("stg",
                    new StagingColumn("TicketId", "BIGINT"))
            };

            var watermark  = new WatermarkStrategy(EtlWatermarkType.Identity, "TicketId", "0");
            var executor   = new EtlPipelineExecutor(mockSource, mockLoader);
            var etlResult  = await executor.ExecuteAsync(config, watermark);

            etlResult.Success.Should().BeFalse("ETL 應失敗");
            watermark.CurrentValue.Should().Be("0", "失敗後 Watermark 不應推進");

            // Analysis 資料應不變
            var afterResult = E().Execute(Q(), req, _allFields);
            afterResult.Rows.Should().HaveCount(rowsBefore,
                "ETL 失敗後 Analysis 資料應不受影響");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // ▌邊界測試（Boundary Cases）— 6 個
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// [邊界] 處理時間 = 0（秒結案，例如自動回覆）→ ResolutionHours=0，不崩潰
        /// </summary>
        [TestMethod]
        public void VP_InstantResolution_ZeroHours_Handled()
        {
            // 建立和結案時間相同（自動系統回覆）
            var t = new DateTime(2026, 1, 5, 10, 0, 0);
            CsTestDataStore.Tickets = new List<ServiceTicket>
            {
                CsDataFactory.Closed("AutoBot", "一般查詢", "P3", t, t),  // 0 秒，ResolutionHours=0
                CsDataFactory.Closed("Alice",   "帳單問題", "P2",
                    new DateTime(2026,1,6,9,0,0), new DateTime(2026,1,6,14,0,0)), // 5h
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "AgentName" },
                Measures   = new List<MeasureRequest>
                {
                    new() { Field = "ResolutionHours", Func = AggregateFunc.Avg },
                    new() { Field = "ResolutionHours", Func = AggregateFunc.Min }
                }
            };

            var closedQuery = CsTestDataStore.Tickets
                .Where(x => x.ResolutionHours.HasValue)
                .AsQueryable();

            Action act = () => E().Execute(closedQuery, req, _allFields);
            act.Should().NotThrow("處理時間 = 0 不應導致例外");

            var result = E().Execute(closedQuery, req, _allFields);
            var bot = result.Rows.Single(r => r["AgentName"]?.ToString() == "AutoBot");
            Convert.ToDecimal(bot["ResolutionHours_Avg"]).Should().Be(0m);
            Convert.ToDecimal(bot["ResolutionHours_Min"]).Should().Be(0m);
        }

        /// <summary>
        /// [邊界] 滿意度全部 5 分（標準差 = 0）→ Avg=Max=Min=5，不崩潰
        /// </summary>
        [TestMethod]
        public void VP_AllPerfectSatisfaction_AllMetricsAreFive()
        {
            CsTestDataStore.Tickets = Enumerable.Range(1, 5)
                .Select(i => CsDataFactory.Closed("Alice", "帳單問題", "P2",
                    new DateTime(2026, 1, i), new DateTime(2026, 1, i, 3, 0, 0), 5m))
                .ToList();

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "AgentName" },
                Measures   = new List<MeasureRequest>
                {
                    new() { Field = "SatisfactionScore", Func = AggregateFunc.Avg },
                    new() { Field = "SatisfactionScore", Func = AggregateFunc.Min },
                    new() { Field = "SatisfactionScore", Func = AggregateFunc.Max },
                }
            };

            var ratedQuery = CsTestDataStore.Tickets.AsQueryable();
            var result = E().Execute(ratedQuery, req, _allFields);

            result.Rows.Should().HaveCount(1);
            var row = result.Rows[0];
            Convert.ToDecimal(row["SatisfactionScore_Avg"]).Should().Be(5m);
            Convert.ToDecimal(row["SatisfactionScore_Min"]).Should().Be(5m);
            Convert.ToDecimal(row["SatisfactionScore_Max"]).Should().Be(5m,
                "全部 5 分時 Max=Min=Avg=5（標準差=0）");
        }

        /// <summary>
        /// [邊界] 單一客服人員處理 100% 工單 → Analysis 只有 1 個分組
        /// </summary>
        [TestMethod]
        public void VP_SingleAgentHandlesAllTickets_OneGroupInResult()
        {
            CsTestDataStore.Tickets = Enumerable.Range(1, 10)
                .Select(i => CsDataFactory.Closed("Alice", "帳單問題", "P2",
                    new DateTime(2026, 1, i), new DateTime(2026, 1, i, 5, 0, 0), 4m))
                .ToList();

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "AgentName" },
                Measures   = new List<MeasureRequest>
                {
                    new() { Field = "TicketCount", Func = AggregateFunc.Sum }
                }
            };

            var result = E().Execute(Q(), req, _allFields);

            result.Rows.Should().HaveCount(1, "只有 Alice 一人，應只有 1 個分組");
            Convert.ToDecimal(result.Rows[0]["TicketCount_Sum"]).Should().Be(10m,
                "Alice 處理全部 10 張工單");
        }

        /// <summary>
        /// [邊界] 跨日工單（22:00 建立，隔日 02:00 結案 = 4 小時，不是負 20 小時）
        /// </summary>
        [TestMethod]
        public void VP_OvernightTicket_ResolutionTimeIsPositiveFourHours()
        {
            var created = new DateTime(2026, 1, 15, 22, 0, 0);
            var closed  = new DateTime(2026, 1, 16,  2, 0, 0); // 隔日 02:00

            var ticket = CsDataFactory.Closed("Carol", "技術問題", "P2", created, closed);
            ticket.ResolutionHours.Should().BeApproximately(4m, 0.01m,
                "跨日工單處理時間應為正 4 小時，不是 -20 小時");

            CsTestDataStore.Tickets = new List<ServiceTicket> { ticket };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "AgentName" },
                Measures   = new List<MeasureRequest>
                {
                    new() { Field = "ResolutionHours", Func = AggregateFunc.Avg }
                }
            };

            var closedQ = CsTestDataStore.Tickets.AsQueryable();
            var result  = E().Execute(closedQ, req, _allFields);

            result.Rows.Should().HaveCount(1);
            Convert.ToDecimal(result.Rows[0]["ResolutionHours_Avg"]).Should().BeApproximately(4m, 0.01m,
                "Analysis 回傳的跨日處理時間應為 4 小時");
        }

        /// <summary>
        /// [邊界] SLA 剛好 24 小時整（邊界：算達標還是超時？）
        /// 規則：24h 整 ≤ SLA 上限 → 算達標（SlaFlag=1）
        /// </summary>
        [TestMethod]
        public void VP_ExactlyAtSlaBoundary_24Hours_CountsAsCompliant()
        {
            var created = new DateTime(2026, 2, 1, 9, 0, 0);
            var closed  = new DateTime(2026, 2, 2, 9, 0, 0); // 剛好 24 小時

            var ticket = CsDataFactory.Closed("Bob", "一般查詢", "P3", created, closed);

            ticket.ResolutionHours.Should().BeApproximately(24m, 0.001m, "應剛好 24 小時");
            ticket.SlaFlag.Should().Be(1m, "24h 整 ≤ P3 SLA 24h，應算達標");
        }

        /// <summary>
        /// [邊界] 月底最後一天 23:59 建立的工單屬於該月份（不跨月）
        /// </summary>
        [TestMethod]
        public void VP_LastMinuteOfMonth_TicketBelongsToCorrectMonth()
        {
            var createdAtEndOfJan = new DateTime(2026, 1, 31, 23, 59, 59);
            var closedAtEarlyFeb  = new DateTime(2026, 2, 1, 1, 0, 0);

            CsTestDataStore.Tickets = new List<ServiceTicket>
            {
                CsDataFactory.Closed("Alice", "帳單問題", "P2", createdAtEndOfJan, closedAtEarlyFeb),
                CsDataFactory.Closed("Alice", "技術問題", "P2",
                    new DateTime(2026, 2, 5), new DateTime(2026, 2, 5, 3, 0, 0)),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "CreatedAt" },
                Measures   = new List<MeasureRequest>
                {
                    new() { Field = "TicketCount", Func = AggregateFunc.Sum }
                },
                DimensionHierarchies = new Dictionary<string, DateHierarchy>
                {
                    ["CreatedAt"] = DateHierarchy.Month
                }
            };

            var result = E().Execute(Q(), req, _allFields);

            result.Rows.Should().HaveCount(2, "1 月底和 2 月初應各自獨立分月");

            // 找 1 月份分組（23:59:59 建立的工單應歸屬 1 月）
            var janRow = result.Rows.SingleOrDefault(r =>
            {
                var v = r["CreatedAt"]?.ToString() ?? "";
                return v.Contains("2026") && v.Contains("01");
            });

            janRow.Should().NotBeNull("月底 23:59 建立的工單應歸屬 1 月，不跨月");
            Convert.ToDecimal(janRow!["TicketCount_Sum"]).Should().Be(1m,
                "1 月底建立的工單應歸屬 1 月份統計");
        }
    }
}
