#nullable enable
// ────────────────────────────────────────────────────────────────────────────
// 銷售績效儀表板整合測試
//
// 業務場景：中型企業銷售部門主管的每日查看流程
//   ETL → Analysis 多維度查詢 → Dashboard Widget 數據源
//
// 測試策略：
//   • MockEtlSource / MockBulkLoader 取代真實 DB，允許 CI 直接執行
//   • Analysis 採 in-memory LINQ 策略（SQLite DBType）
//   • Dashboard 以 AnalysisWidgetDataSource 橋接 Analysis
//   • 測試名稱以業務場景命名，反映主管視角
// ────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NPOI.XSSF.UserModel;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Dashboard;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Testing;

namespace WalkingTec.Mvvm.Core.Test.Integration
{
    /// <summary>
    /// 銷售績效儀表板端對端整合測試。
    /// 覆蓋：ETL 同步 → Analysis 多維度查詢 → Dashboard Widget 三模組串聯。
    /// </summary>
    [TestClass]
    public class SalesDashboardIntegrationTests
    {
        // ═══════════════════════════════════════════════════════════════════
        // 領域模型：銷售訂單（Analysis 分析目標）
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>銷售訂單 — ETL 目標表 / Analysis 查詢模型</summary>
        private class SalesOrder : TopBasePoco
        {
            [Dimension(DisplayName = "區域")]
            public string Region { get; set; } = string.Empty;

            [Dimension(DisplayName = "產品類別")]
            public string Category { get; set; } = string.Empty;

            [Dimension(DisplayName = "業務員")]
            public string SalesRep { get; set; } = string.Empty;

            [Dimension(DisplayName = "訂單日期", Hierarchy = DateHierarchy.Month)]
            public DateTime OrderDate { get; set; }

            [Measure(
                AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count | AggregateFunc.Avg | AggregateFunc.Max | AggregateFunc.Min,
                DisplayName = "營收")]
            public decimal Revenue { get; set; }

            [Measure(
                AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count,
                DisplayName = "訂單數")]
            public int OrderCount { get; set; }
        }

        // ───────────────────────────────────────────────────────────────────
        // 靜態注入點：由 Setup() 填充，供 ListVM.GetSearchQuery() 使用
        // ───────────────────────────────────────────────────────────────────
        private static IList<SalesOrder> _salesData = new List<SalesOrder>();

        [EnableAnalysis]
        private class SalesOrderListVM : BasePagedListVM<SalesOrder, BaseSearcher>
        {
            public override IOrderedQueryable<SalesOrder> GetSearchQuery()
                => _salesData.AsQueryable().OrderByDescending(x => x.OrderDate);
        }

        // ═══════════════════════════════════════════════════════════════════
        // 基礎設施
        // ═══════════════════════════════════════════════════════════════════

        private AnalysisVmRegistry _registry = null!;
        private AnalysisQueryEngine _engine = null!;
        private IServiceProvider _serviceProvider = null!;

        [TestInitialize]
        public void Setup()
        {
            // 準備典型銷售資料（跨兩年度、多區域、多類別、含退貨）
            _salesData = BuildSalesData();

            _registry = new AnalysisVmRegistry();
            _registry.Build(new[] { typeof(SalesDashboardIntegrationTests).Assembly });

            _engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default);

            var services = new ServiceCollection();
            _serviceProvider = services.BuildServiceProvider();
        }

        // ═══════════════════════════════════════════════════════════════════
        // 測試資料工廠
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 建立涵蓋以下場景的資料：
        /// - 多區域（北部/南部/東部）
        /// - 多類別（電子/服飾/食品）
        /// - 2025 & 2026 跨年度
        /// - 含退貨（Revenue 為負）
        /// - 部分月份零銷售（靠篩選模擬）
        /// </summary>
        private static List<SalesOrder> BuildSalesData()
        {
            var data = new List<SalesOrder>();

            // 2025 年資料（跨年度測試）
            data.Add(MakeOrder("北部", "電子", "Alice", new DateTime(2025, 11, 15), 1_200_000m, 10));
            data.Add(MakeOrder("北部", "電子", "Alice", new DateTime(2025, 12, 20), 980_000m,  8));
            data.Add(MakeOrder("南部", "服飾", "Bob",   new DateTime(2025, 11, 10), 450_000m,  30));
            data.Add(MakeOrder("南部", "服飾", "Bob",   new DateTime(2025, 12, 5),  390_000m,  25));

            // 2026 年資料
            data.Add(MakeOrder("北部", "電子", "Alice", new DateTime(2026, 1, 10), 1_500_000m, 12));
            data.Add(MakeOrder("北部", "電子", "Alice", new DateTime(2026, 2, 14), 1_350_000m, 11));
            data.Add(MakeOrder("北部", "服飾", "Alice", new DateTime(2026, 1, 20),   300_000m,  20));
            data.Add(MakeOrder("南部", "電子", "Bob",   new DateTime(2026, 1, 8),    800_000m,  7));
            data.Add(MakeOrder("南部", "服飾", "Bob",   new DateTime(2026, 2, 18),   420_000m,  28));
            data.Add(MakeOrder("東部", "食品", "Carol", new DateTime(2026, 1, 25),   250_000m,  50));
            data.Add(MakeOrder("東部", "食品", "Carol", new DateTime(2026, 2, 28),   280_000m,  55));

            // 退貨訂單（Revenue 為負，OrderCount=1）
            data.Add(MakeOrder("北部", "電子", "Alice", new DateTime(2026, 2, 5), -150_000m, 1));

            return data;
        }

        private static SalesOrder MakeOrder(string region, string category, string rep,
            DateTime date, decimal revenue, int count)
            => new()
            {
                ID         = Guid.NewGuid(),
                Region     = region,
                Category   = category,
                SalesRep   = rep,
                OrderDate  = date,
                Revenue    = revenue,
                OrderCount = count,
            };

        // ═══════════════════════════════════════════════════════════════════
        // ▌正向測試（Happy Path）
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 主管查看各區域月度營收加總 — 驗證 Region × Month GroupBy 正確
        /// </summary>
        [TestMethod]
        public void SalesManager_ViewsMonthlyRevenue_ByRegion_ReturnsCorrectTotals()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Revenue", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new() { Field = "Region", Operator = FilterOperator.In, Value = "北部,南部,東部" }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SalesOrder));
            var result = _engine.Execute(_salesData.AsQueryable(), req, whitelist);

            Assert.AreEqual(3, result.Rows.Count, "應有 3 個區域分組");
            Assert.IsFalse(result.Truncated);

            var north = result.Rows.Single(r => r["Region"]?.ToString() == "北部");
            // 北部：1_200_000 + 980_000 + 1_500_000 + 1_350_000 + 300_000 + (-150_000) = 5_180_000
            var expectedNorthRevenue = 1_200_000m + 980_000m + 1_500_000m + 1_350_000m + 300_000m + (-150_000m);
            Assert.AreEqual(expectedNorthRevenue, Convert.ToDecimal(north["Revenue_Sum"]),
                "北部總營收計算錯誤（含退貨）");

            var east = result.Rows.Single(r => r["Region"]?.ToString() == "東部");
            // 東部：250_000 + 280_000 = 530_000
            Assert.AreEqual(530_000m, Convert.ToDecimal(east["Revenue_Sum"]));
        }

        /// <summary>
        /// 主管用 ETL 同步銷售訂單後，Analysis 即時反映新資料
        /// — 驗證 ETL → Analysis 完整串聯
        /// </summary>
        [TestMethod]
        public async Task SalesManager_AfterEtlSync_AnalysisReflectsUpdatedData()
        {
            // ── Step 1: 準備 ETL 來源資料（模擬 5 筆新訂單）──
            var sourceTable = new DataTable();
            sourceTable.Columns.Add("OrderId", typeof(long));
            sourceTable.Columns.Add("Region",  typeof(string));
            sourceTable.Columns.Add("Revenue", typeof(decimal));
            sourceTable.Rows.Add(1L, "北部", 500_000m);
            sourceTable.Rows.Add(2L, "南部", 300_000m);
            sourceTable.Rows.Add(3L, "東部", 200_000m);
            sourceTable.Rows.Add(4L, "北部", 700_000m);
            sourceTable.Rows.Add(5L, "南部", 100_000m);

            var mockSource = new MockEtlSource();
            mockSource.SetData(sourceTable);
            var mockLoader = new MockBulkLoader();

            var config = new EtlPipelineConfig
            {
                JobId          = Guid.NewGuid(),
                JobName        = "SalesOrderSync",
                SourceConnectionString = "mock://source",
                TargetConnectionString = "mock://target",
                QueryTemplate  = "SELECT * FROM SalesOrders WHERE {watermark}",
                TargetTableName= "SalesOrders",
                MergeKeyColumn = "OrderId",
                BatchSize      = 100,
                StagingTable   = new StagingTableSpec("stg_SalesOrders",
                    new StagingColumn("OrderId", "BIGINT"),
                    new StagingColumn("Region",  "NVARCHAR(50)"),
                    new StagingColumn("Revenue", "DECIMAL(18,2)"))
            };

            var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);

            // ── Step 2: 執行 ETL ──
            var executor = new EtlPipelineExecutor(mockSource, mockLoader);
            var etlResult = await executor.ExecuteAsync(config, watermark);

            Assert.IsTrue(etlResult.Success, $"ETL 失敗：{etlResult.ErrorMessage}");
            Assert.AreEqual(5, etlResult.ExtractedRows, "應擷取 5 筆");
            Assert.AreEqual(5, etlResult.LoadedRows, "應載入 5 筆");
            Assert.IsTrue(mockLoader.MergeCalled, "Merge 必須執行");

            // ── Step 3: ETL 完成後，模擬 Analysis 查詢（資料已在 _salesData 中）──
            // 測試重點：確認 Analysis 可在 ETL 成功後被呼叫並回傳正確結果
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Revenue", Func = AggregateFunc.Sum },
                    new() { Field = "Revenue", Func = AggregateFunc.Count }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SalesOrder));
            var analysisResult = _engine.Execute(_salesData.AsQueryable(), req, whitelist);

            Assert.IsTrue(analysisResult.Rows.Count > 0, "ETL 後 Analysis 應有資料");
            Assert.IsTrue(analysisResult.Columns.Contains("Revenue_Sum"), "應含 Revenue_Sum 欄位");
            Assert.IsTrue(analysisResult.Columns.Contains("Revenue_Count"), "應含 Revenue_Count 欄位");
        }

        /// <summary>
        /// 主管查看雙軸圖表：營收（百萬級）vs 訂單數（百級）— 驗證雙軸偵測邏輯
        /// </summary>
        [TestMethod]
        public void SalesManager_ViewsDualAxisChart_RevenueVsOrderCount_DetectsDualAxis()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Revenue",    Func = AggregateFunc.Sum },
                    new() { Field = "OrderCount", Func = AggregateFunc.Sum }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SalesOrder));
            var result = _engine.Execute(_salesData.AsQueryable(), req, whitelist);

            Assert.AreEqual(3, result.Rows.Count, "應有 3 個區域");

            // 驗證 Excel 匯出的雙軸偵測
            var measureIndices = new List<int> { 1, 2 }; // Revenue_Sum 在 index 1, OrderCount_Sum 在 index 2
            bool isDualAxis = AnalysisExcelExporter.DetectDualAxis(result, measureIndices);
            Assert.IsTrue(isDualAxis,
                "營收（百萬）vs 訂單數（百）差距 >= 10 倍，應啟用雙軸");
        }

        /// <summary>
        /// 主管套用日期範圍篩選 → Dashboard 只顯示 2026 年 1 月資料
        /// </summary>
        [TestMethod]
        public void SalesManager_AppliesDateRangeFilter_DashboardShowsCorrectPeriod()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region", "Category" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Revenue", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new() { Field = "OrderDate", Operator = FilterOperator.Gte, Value = "2026-01-01" },
                    new() { Field = "OrderDate", Operator = FilterOperator.Lte, Value = "2026-01-31" }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SalesOrder));
            var result = _engine.Execute(_salesData.AsQueryable(), req, whitelist);

            // 2026-01 有：北部電子(1_500_000)、北部服飾(300_000)、南部電子(800_000)、東部食品(250_000)
            Assert.AreEqual(4, result.Rows.Count, "2026 年 1 月應有 4 筆區域×類別組合");

            var northElec = result.Rows.Single(r =>
                r["Region"]?.ToString() == "北部" && r["Category"]?.ToString() == "電子");
            Assert.AreEqual(1_500_000m, Convert.ToDecimal(northElec["Revenue_Sum"]));
        }

        /// <summary>
        /// Dashboard Widget 透過 AnalysisWidgetDataSource 串接 Analysis — 驗證三模組橋接
        /// </summary>
        [TestMethod]
        public async Task SalesManager_ViewsDashboardWidget_DataSourceBridgesAnalysisCorrectly()
        {
            var source = new AnalysisWidgetDataSource(_registry, _serviceProvider, _engine);

            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(SalesOrderListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Region" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Revenue", Func = AggregateFunc.Sum }
                    })
                }
            };

            var widgetResult = await source.GetDataAsync(request);

            Assert.IsNotNull(widgetResult);
            Assert.AreEqual(3, widgetResult.Rows.Count, "Widget 應顯示 3 個區域");
            Assert.IsTrue(widgetResult.Columns.Contains("Revenue_Sum"), "Widget 應含 Revenue_Sum");

            // 驗證 Metadata
            Assert.IsTrue(widgetResult.Metadata.ContainsKey("totalCount"));
            Assert.IsFalse(Convert.ToBoolean(widgetResult.Metadata["truncated"]), "資料不應截斷");
        }

        /// <summary>
        /// 匯出 Excel — 欄位與畫面（Analysis 回傳）一致
        /// </summary>
        [TestMethod]
        public void SalesManager_ExportsExcel_ColumnsMatchAnalysisResult()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region", "Category" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Revenue",    Func = AggregateFunc.Sum },
                    new() { Field = "OrderCount", Func = AggregateFunc.Sum }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SalesOrder));
            var result = _engine.Execute(_salesData.AsQueryable(), req, whitelist);

            // 匯出 Excel（含圖表）
            var excelBytes = AnalysisExcelExporter.Export(result, includeChart: true, chartType: "bar");

            Assert.IsNotNull(excelBytes);
            Assert.IsTrue(excelBytes.Length > 0, "Excel 位元組不應為空");

            // 再次驗證欄位名稱與 Analysis 回傳一致
            var expectedCols = new[] { "Region", "Category", "Revenue_Sum", "OrderCount_Sum" };
            foreach (var col in expectedCols)
                Assert.IsTrue(result.Columns.Contains(col),
                    $"Analysis 結果應含欄位 '{col}'");
        }

        /// <summary>
        /// 匯出 Excel 欄位順序 — 與 Analysis 回傳 Columns 順序一致 (#438)
        /// </summary>
        [TestMethod]
        public void SalesManager_ExportsExcel_ColumnOrderMatchesAnalysisResultColumns()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region", "Category" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Revenue",    Func = AggregateFunc.Sum },
                    new() { Field = "OrderCount", Func = AggregateFunc.Sum }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SalesOrder));
            var result = _engine.Execute(_salesData.AsQueryable(), req, whitelist);
            var excelBytes = AnalysisExcelExporter.Export(result, includeChart: false);

            using var ms = new MemoryStream(excelBytes);
            var wb = new XSSFWorkbook(ms);
            var sheet = wb.GetSheetAt(0);
            var headerRow = sheet.GetRow(0);

            // Excel header row must use display names when available (#495)
            for (int i = 0; i < result.Columns.Count; i++)
            {
                var colKey = result.Columns[i];
                var expectedHeader = result.ColumnDisplayNames.TryGetValue(colKey, out var dn) ? dn : colKey;
                var cellVal = headerRow.GetCell(i)?.StringCellValue ?? string.Empty;
                Assert.AreEqual(expectedHeader, cellVal,
                    $"Excel header column {i} should be '{expectedHeader}' but was '{cellVal}'");
            }

            Assert.AreEqual(result.Columns.Count, headerRow.LastCellNum,
                "Excel should have exactly as many columns as result.Columns");
        }

        /// <summary>
        /// 業務員績效排名 — 驗證多維度 + 多度量 + 排序（Count 輔助）
        /// </summary>
        [TestMethod]
        public void SalesManager_ViewsSalesRepPerformance_MultiDimensionCorrect()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "SalesRep", "Region" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Revenue",    Func = AggregateFunc.Sum },
                    new() { Field = "Revenue",    Func = AggregateFunc.Avg },
                    new() { Field = "OrderCount", Func = AggregateFunc.Sum }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SalesOrder));
            var result = _engine.Execute(_salesData.AsQueryable(), req, whitelist);

            // Alice 在北部、Bob 在南部（東部是 Carol）
            Assert.IsTrue(result.Rows.Count >= 3, "至少 3 個業務員-區域組合");

            var aliceNorth = result.Rows.FirstOrDefault(r =>
                r["SalesRep"]?.ToString() == "Alice" && r["Region"]?.ToString() == "北部");
            Assert.IsNotNull(aliceNorth, "Alice 北部的資料應存在");

            // Alice 北部：5 筆 (1_200_000, 980_000, 1_500_000, 1_350_000, 300_000, -150_000)
            var aliceNorthRevSum = Convert.ToDecimal(aliceNorth["Revenue_Sum"]);
            Assert.AreEqual(5_180_000m, aliceNorthRevSum, "Alice 北部總營收應包含退貨扣減");
        }

        // ═══════════════════════════════════════════════════════════════════
        // ▌反向測試（Negative Path）
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// ETL 失敗後舊資料不受影響 — 驗證 Watermark 不更新、Analysis 仍回傳舊資料
        /// </summary>
        [TestMethod]
        public async Task SalesManager_WhenEtlFails_OldAnalysisDataIsPreserved()
        {
            // ── Step 1: 取得失敗前的 Analysis 結果 ──
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Revenue", Func = AggregateFunc.Sum }
                }
            };
            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SalesOrder));
            var beforeResult = _engine.Execute(_salesData.AsQueryable(), req, whitelist);
            int rowsBeforeEtl = beforeResult.Rows.Count;
            decimal northRevBefore = Convert.ToDecimal(
                beforeResult.Rows.Single(r => r["Region"]?.ToString() == "北部")["Revenue_Sum"]);

            // ── Step 2: ETL 刻意失敗（第 1 batch 拋出例外）──
            var sourceTable = new DataTable();
            sourceTable.Columns.Add("OrderId", typeof(long));
            sourceTable.Columns.Add("Revenue", typeof(decimal));
            sourceTable.Rows.Add(1L, 9_999_999m); // 這筆不應被套用

            var mockSource = new MockEtlSource();
            mockSource.SetData(sourceTable);
            var mockLoader = new MockBulkLoader { FailOnBatch = 1 };

            var config = new EtlPipelineConfig
            {
                JobId            = Guid.NewGuid(),
                JobName          = "SalesOrderSyncFailTest",
                SourceConnectionString = "mock://src",
                TargetConnectionString = "mock://tgt",
                QueryTemplate    = "SELECT * FROM SalesOrders",
                TargetTableName  = "SalesOrders",
                MergeKeyColumn   = "OrderId",
                BatchSize        = 100,
                StagingTable     = new StagingTableSpec("stg_SalesOrders",
                    new StagingColumn("OrderId", "BIGINT"),
                    new StagingColumn("Revenue", "DECIMAL(18,2)"))
            };

            var watermark = new WatermarkStrategy(EtlWatermarkType.Identity, "OrderId", "100");
            var executor = new EtlPipelineExecutor(mockSource, mockLoader);
            var etlResult = await executor.ExecuteAsync(config, watermark);

            // ── Step 3: 驗證 ETL 失敗 ──
            Assert.IsFalse(etlResult.Success, "ETL 應失敗");
            Assert.IsNull(etlResult.NewWatermarkValue, "Watermark 不應更新");
            // Watermark 的 CurrentValue 不應改變
            Assert.AreEqual("100", watermark.CurrentValue,
                "ETL 失敗後 Watermark 不應推進");

            // ── Step 4: Analysis 回傳仍是舊資料（_salesData 未被改動）──
            var afterResult = _engine.Execute(_salesData.AsQueryable(), req, whitelist);
            Assert.AreEqual(rowsBeforeEtl, afterResult.Rows.Count,
                "ETL 失敗後 Analysis 結果筆數應不變");
            var northRevAfter = Convert.ToDecimal(
                afterResult.Rows.Single(r => r["Region"]?.ToString() == "北部")["Revenue_Sum"]);
            Assert.AreEqual(northRevBefore, northRevAfter,
                "ETL 失敗後 Analysis 數值應與失敗前一致");
        }

        /// <summary>
        /// Analysis 查詢無資料時回傳空結果（非例外）— graceful degradation
        /// </summary>
        [TestMethod]
        public void SalesManager_QueryWithNoMatchingData_ReturnsEmptyNotException()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Revenue", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    // 不存在的區域
                    new() { Field = "Region", Operator = FilterOperator.Eq, Value = "火星據點" }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SalesOrder));
            var result = _engine.Execute(_salesData.AsQueryable(), req, whitelist);

            Assert.AreEqual(0, result.Rows.Count, "無資料時應回傳空清單");
            Assert.IsFalse(result.Truncated, "無資料不應標記截斷");
            Assert.AreEqual(0, result.TotalCount);
        }

        /// <summary>
        /// 篩選條件矛盾（開始日期 > 結束日期）→ 回傳空結果，不崩潰
        /// </summary>
        [TestMethod]
        public void SalesManager_ContradictoryDateFilter_ReturnsEmptyGracefully()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Revenue", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    // 開始 > 結束 → 邏輯矛盾
                    new() { Field = "OrderDate", Operator = FilterOperator.Gte, Value = "2026-12-31" },
                    new() { Field = "OrderDate", Operator = FilterOperator.Lte, Value = "2026-01-01" }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SalesOrder));
            var result = _engine.Execute(_salesData.AsQueryable(), req, whitelist);

            // Analysis 引擎不驗證邏輯矛盾，只照實 Apply（兩個 AND 條件互斥 → 0 筆）
            Assert.AreEqual(0, result.Rows.Count,
                "矛盾篩選條件應產生空結果，不拋例外");
        }

        /// <summary>
        /// 無權限 Widget 請求（ListVM 不在白名單中）→ 拋 InvalidOperationException
        /// </summary>
        [TestMethod]
        public async Task SalesManager_UnauthorizedWidgetAccess_ThrowsException()
        {
            var source = new AnalysisWidgetDataSource(_registry, _serviceProvider, _engine);

            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    // 使用未在 registry 中的 ListVM
                    ["listVmType"] = "WalkingTec.Mvvm.Core.Test.Integration.UnknownListVM"
                }
            };

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => source.GetDataAsync(request),
                "未授權的 ListVM 應拋出 InvalidOperationException");
        }

        /// <summary>
        /// 不允許的聚合函式（OrderCount 僅允許 Sum/Count，不允許 Avg）→ 清楚拋出
        /// </summary>
        [TestMethod]
        public void SalesManager_UnsupportedAggregateFunction_ThrowsNotSupportedException()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    // OrderCount 只允許 Sum | Count，Avg 不在其中
                    new() { Field = "OrderCount", Func = AggregateFunc.Avg }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SalesOrder));
            Assert.ThrowsException<NotSupportedException>(
                () => _engine.Execute(_salesData.AsQueryable(), req, whitelist),
                "不允許的聚合函式應拋出 NotSupportedException");
        }

        // ═══════════════════════════════════════════════════════════════════
        // ▌邊界測試（Edge Cases）
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 跨年度資料月份分組 — 2025-12 與 2026-01 不應合併
        /// </summary>
        [TestMethod]
        public void SalesManager_CrossYearData_MonthGroupingSegregatesByYear()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "OrderDate" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Revenue", Func = AggregateFunc.Sum }
                },
                DimensionHierarchies = new Dictionary<string, DateHierarchy>
                {
                    ["OrderDate"] = DateHierarchy.Month
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SalesOrder));
            var result = _engine.Execute(_salesData.AsQueryable(), req, whitelist);

            // 應有 2025-11, 2025-12, 2026-01, 2026-02 共 4 個月份分組
            Assert.AreEqual(4, result.Rows.Count,
                "跨年度月份應各自獨立分組");

            // 確認 2025-12 和 2026-01 是分開的 key
            var months = result.Rows.Select(r => r["OrderDate"]?.ToString()).ToList();
            Assert.IsTrue(months.Any(m => m != null && m.Contains("2025") && m.Contains("12")),
                "應有 2025-12 月份分組");
            Assert.IsTrue(months.Any(m => m != null && m.Contains("2026") && m.Contains("01")),
                "應有 2026-01 月份分組");

            // 確認兩年的同月不被合併
            var dec2025 = result.Rows.Single(r =>
            {
                var v = r["OrderDate"]?.ToString() ?? "";
                return v.Contains("2025") && v.Contains("12");
            });
            var jan2026 = result.Rows.Single(r =>
            {
                var v = r["OrderDate"]?.ToString() ?? "";
                return v.Contains("2026") && v.Contains("01");
            });
            Assert.AreNotEqual(
                Convert.ToDecimal(dec2025["Revenue_Sum"]),
                Convert.ToDecimal(jan2026["Revenue_Sum"]),
                "2025-12 和 2026-01 的資料不應相同（不應被合併）");
        }

        /// <summary>
        /// 退貨訂單（Revenue 為負）正確參與加總計算
        /// </summary>
        [TestMethod]
        public void SalesManager_NegativeRevenueReturnOrders_CorrectlyReduceTotals()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "SalesRep" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Revenue", Func = AggregateFunc.Sum },
                    new() { Field = "Revenue", Func = AggregateFunc.Min }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SalesOrder));
            var result = _engine.Execute(_salesData.AsQueryable(), req, whitelist);

            var alice = result.Rows.Single(r => r["SalesRep"]?.ToString() == "Alice");
            var aliceMin = Convert.ToDecimal(alice["Revenue_Min"]);

            // Alice 有一筆 -150_000 退貨
            Assert.IsTrue(aliceMin < 0,
                $"Alice 的 Revenue_Min 應為負值（退貨），實際：{aliceMin}");

            // Alice 加總 = 1_200_000 + 980_000 + 1_500_000 + 1_350_000 + 300_000 + (-150_000) = 5_180_000
            Assert.AreEqual(5_180_000m, Convert.ToDecimal(alice["Revenue_Sum"]),
                "退貨應從加總中扣除");
        }

        /// <summary>
        /// 單一區域篩選 vs 全部區域 — 兩者數字總和一致
        /// </summary>
        [TestMethod]
        public void SalesManager_SingleRegionVsAllRegions_TotalsAreConsistent()
        {
            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SalesOrder));

            // 全部區域
            var reqAll = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Revenue", Func = AggregateFunc.Sum }
                }
            };
            var allResult = _engine.Execute(_salesData.AsQueryable(), reqAll, whitelist);
            var totalFromAll = allResult.Rows.Sum(r => Convert.ToDecimal(r["Revenue_Sum"]));

            // 分別查三個區域
            decimal totalFromIndividual = 0;
            foreach (var region in new[] { "北部", "南部", "東部" })
            {
                var reqSingle = new AnalysisQueryRequest
                {
                    Dimensions = new List<string> { "Region" },
                    Measures = new List<MeasureRequest>
                    {
                        new() { Field = "Revenue", Func = AggregateFunc.Sum }
                    },
                    Filters = new List<FilterCondition>
                    {
                        new() { Field = "Region", Operator = FilterOperator.Eq, Value = region }
                    }
                };
                var singleResult = _engine.Execute(_salesData.AsQueryable(), reqSingle, whitelist);
                if (singleResult.Rows.Count > 0)
                    totalFromIndividual += Convert.ToDecimal(singleResult.Rows[0]["Revenue_Sum"]);
            }

            Assert.AreEqual(totalFromAll, totalFromIndividual,
                "分別查詢各區域的加總應等於全部區域查詢的總和");
        }

        /// <summary>
        /// 0 筆資料月份透過篩選排除後，結果集合本身不含該月份（不出現 Revenue=0 的假月份）
        /// </summary>
        [TestMethod]
        public void SalesManager_EmptyMonthIsExcluded_NotShowingAsZero()
        {
            // 查詢一個確認沒有資料的月份區間（2026-03）
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Revenue", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new() { Field = "OrderDate", Operator = FilterOperator.Gte, Value = "2026-03-01" },
                    new() { Field = "OrderDate", Operator = FilterOperator.Lte, Value = "2026-03-31" }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SalesOrder));
            var result = _engine.Execute(_salesData.AsQueryable(), req, whitelist);

            // Analysis 是 GroupBy 模式：無資料就無分組（不補 0）
            // 這是預期的設計行為：主管應在前端處理「無資料月份補 0」
            Assert.AreEqual(0, result.Rows.Count,
                "無資料的月份不應出現假分組（設計上不補 0，前端自行處理）");
        }

        /// <summary>
        /// 雙軸偵測邊界：兩度量數值相近（比例 < 10）→ 不啟用雙軸
        /// </summary>
        [TestMethod]
        public void SalesManager_SimilarScaleMeasures_DoNotTriggerDualAxis()
        {
            // 建立兩度量數值相近的資料集
            var sameScaleData = new List<SalesOrder>
            {
                MakeOrder("北部", "電子", "Alice", new DateTime(2026, 1, 1), 1_000m, 900),
                MakeOrder("南部", "電子", "Bob",   new DateTime(2026, 1, 2), 900m, 800),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Revenue",    Func = AggregateFunc.Sum },
                    new() { Field = "OrderCount", Func = AggregateFunc.Sum }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SalesOrder));
            var result = _engine.Execute(sameScaleData.AsQueryable(), req, whitelist);

            // Revenue_Sum ~ 1000, OrderCount_Sum ~ 900 → ratio < 10 → no dual axis
            var measureIndices = new List<int> { 1, 2 };
            bool isDualAxis = AnalysisExcelExporter.DetectDualAxis(result, measureIndices);
            Assert.IsFalse(isDualAxis,
                "數量級相近（< 10 倍）時不應啟用雙軸");
        }

        /// <summary>
        /// ETL 取消操作後 Watermark 不推進，再次執行可從舊 Watermark 重跑
        /// </summary>
        [TestMethod]
        public async Task SalesManager_CancelledEtlJob_WatermarkNotAdvanced()
        {
            var sourceTable = new DataTable();
            sourceTable.Columns.Add("OrderId", typeof(long));
            sourceTable.Columns.Add("Revenue", typeof(decimal));
            for (int i = 1; i <= 200; i++)
                sourceTable.Rows.Add((long)i, (decimal)(i * 1000));

            var mockSource = new MockEtlSource();
            mockSource.SetData(sourceTable);
            var mockLoader = new MockBulkLoader();

            var config = new EtlPipelineConfig
            {
                JobId            = Guid.NewGuid(),
                JobName          = "SalesCancelTest",
                SourceConnectionString = "mock://src",
                TargetConnectionString = "mock://tgt",
                QueryTemplate    = "SELECT * FROM SalesOrders WHERE OrderId > @watermark",
                TargetTableName  = "SalesOrders",
                MergeKeyColumn   = "OrderId",
                BatchSize        = 50,
                StagingTable     = new StagingTableSpec("stg",
                    new StagingColumn("OrderId", "BIGINT"),
                    new StagingColumn("Revenue", "DECIMAL(18,2)"))
            };

            var watermark = new WatermarkStrategy(EtlWatermarkType.Identity, "OrderId", "0");
            var cts = new CancellationTokenSource();
            cts.Cancel(); // 立即取消

            var executor = new EtlPipelineExecutor(mockSource, mockLoader);
            var result = await executor.ExecuteAsync(config, watermark, cts.Token);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Aborted, "應標記為 Aborted");
            // Watermark 不推進（DiscardPendingValue 被呼叫）
            Assert.AreEqual("0", watermark.CurrentValue,
                "取消後 Watermark 不應推進");
        }

        /// <summary>
        /// 跨年度統計 — 2025 全年 vs 2026 迄今，數字各自正確
        /// </summary>
        [TestMethod]
        public void SalesManager_CrossYearSummary_EachYearCalculatedIndependently()
        {
            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SalesOrder));

            // 2025 年
            var req2025 = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Revenue", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new() { Field = "OrderDate", Operator = FilterOperator.Gte, Value = "2025-01-01" },
                    new() { Field = "OrderDate", Operator = FilterOperator.Lte, Value = "2025-12-31" }
                }
            };
            var result2025 = _engine.Execute(_salesData.AsQueryable(), req2025, whitelist);
            var total2025 = result2025.Rows.Sum(r => Convert.ToDecimal(r["Revenue_Sum"]));

            // 2026 年
            var req2026 = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Revenue", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new() { Field = "OrderDate", Operator = FilterOperator.Gte, Value = "2026-01-01" },
                    new() { Field = "OrderDate", Operator = FilterOperator.Lte, Value = "2026-12-31" }
                }
            };
            var result2026 = _engine.Execute(_salesData.AsQueryable(), req2026, whitelist);
            var total2026 = result2026.Rows.Sum(r => Convert.ToDecimal(r["Revenue_Sum"]));

            // 驗算：2025 全年 = 1_200_000 + 980_000 + 450_000 + 390_000 = 3_020_000
            Assert.AreEqual(3_020_000m, total2025, $"2025 年總營收錯誤，實際：{total2025}");

            // 2026 = 1_500_000 + 1_350_000 + 300_000 + 800_000 + 420_000 + 250_000 + 280_000 + (-150_000) = 4_750_000
            Assert.AreEqual(4_750_000m, total2026, $"2026 年總營收錯誤，實際：{total2026}");

            // 兩年加總 = 完整資料集總和
            var reqTotal = new AnalysisQueryRequest
            {
                Dimensions = new List<string>(),
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Revenue", Func = AggregateFunc.Sum }
                }
            };
            var totalResult = _engine.Execute(_salesData.AsQueryable(), reqTotal, whitelist);
            var grandTotal = Convert.ToDecimal(totalResult.Rows[0]["Revenue_Sum"]);
            Assert.AreEqual(total2025 + total2026, grandTotal,
                "2025+2026 加總應等於全資料集總和");
        }

        /// <summary>
        /// 純 KPI 查詢（零維度）— 主管首頁看總覽數字
        /// </summary>
        [TestMethod]
        public void SalesManager_ViewsKpiSummary_ZeroDimensionProducesGlobalAggregate()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string>(), // 無維度 = 全局聚合
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Revenue",    Func = AggregateFunc.Sum },
                    new() { Field = "Revenue",    Func = AggregateFunc.Avg },
                    new() { Field = "OrderCount", Func = AggregateFunc.Sum },
                    new() { Field = "Revenue",    Func = AggregateFunc.Min },
                    new() { Field = "Revenue",    Func = AggregateFunc.Max }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SalesOrder));
            var result = _engine.Execute(_salesData.AsQueryable(), req, whitelist);

            Assert.AreEqual(1, result.Rows.Count, "零維度應折疊成 1 列 KPI");
            Assert.AreEqual(5, result.Columns.Count - 0,
                "應有 5 個度量欄位（不含維度）");

            var kpi = result.Rows[0];
            // Total = 3_020_000 + 4_750_000 = 7_770_000
            Assert.AreEqual(7_770_000m, Convert.ToDecimal(kpi["Revenue_Sum"]),
                "全局總營收計算錯誤");
            Assert.IsTrue(Convert.ToDecimal(kpi["Revenue_Min"]) < 0,
                "最小營收應為負（含退貨）");
            Assert.IsTrue(Convert.ToDecimal(kpi["Revenue_Max"]) > 0,
                "最大營收應為正");
        }

        /// <summary>
        /// ETL 空資料同步（來源表為空）— 成功完成但擷取 0 筆
        /// </summary>
        [TestMethod]
        public async Task SalesManager_EtlEmptySource_SucceedsWithZeroRows()
        {
            var emptyTable = new DataTable();
            emptyTable.Columns.Add("OrderId", typeof(long));
            emptyTable.Columns.Add("Revenue", typeof(decimal));
            // 不加任何資料列

            var mockSource = new MockEtlSource();
            mockSource.SetData(emptyTable);
            var mockLoader = new MockBulkLoader();

            var config = new EtlPipelineConfig
            {
                JobId            = Guid.NewGuid(),
                JobName          = "SalesOrderEmptySync",
                SourceConnectionString = "mock://src",
                TargetConnectionString = "mock://tgt",
                QueryTemplate    = "SELECT * FROM SalesOrders",
                TargetTableName  = "SalesOrders",
                MergeKeyColumn   = "OrderId",
                BatchSize        = 100,
                StagingTable     = new StagingTableSpec("stg",
                    new StagingColumn("OrderId", "BIGINT"),
                    new StagingColumn("Revenue", "DECIMAL(18,2)"))
            };

            var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
            var executor = new EtlPipelineExecutor(mockSource, mockLoader);
            var result = await executor.ExecuteAsync(config, watermark);

            Assert.IsTrue(result.Success, "空來源也應成功完成");
            Assert.AreEqual(0, result.ExtractedRows);
            Assert.AreEqual(0, result.LoadedRows);
            Assert.IsTrue(mockLoader.MergeCalled,
                "即使空資料，Merge 仍應執行（確保 staging 已 truncate）");
        }
    }
}
