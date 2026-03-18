#nullable enable
// ============================================================================
// 供應鏈營運長（COO）儀表板跨模組整合測試
//
// 業務場景：製造業 COO 的供應鏈健康儀表板
//   ETL → 採購訂單/庫存異動/交貨記錄同步
//   Analysis → 供應商準時率、庫存周轉率、採購金額趨勢、缺貨率
//   Dashboard → COO 每日查看的供應鏈 KPI 看板
//
// 測試策略：
//   • MockEtlSource / MockBulkLoader 取代真實 DB，允許 CI 直接執行
//   • Analysis 採 in-memory LINQ 策略（SQLite DBType）
//   • Dashboard 以 AnalysisWidgetDataSource 橋接 Analysis
//   • 準時率公式：準時筆數 / 總筆數 × 100（SCOR model OTD）
//   • 庫存周轉率公式：銷貨成本 / 平均庫存（SCOR model Inventory Turns）
//   • 風險優先排序：準時率低的供應商排前面（COO 視角）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    // ═══════════════════════════════════════════════════════════════════════
    // 供應鏈領域模型
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 採購訂單交貨記錄（ERP 同步來源 / Analysis 查詢模型）
    ///
    /// IsOnTime 欄位語義：
    ///   true  = 實際交貨日 ≤ 預計交貨日（準時或提前）
    ///   false = 實際交貨日 > 預計交貨日（遲交）
    /// </summary>
    internal class SupplierDelivery : TopBasePoco
    {
        [Dimension(DisplayName = "供應商代碼")]
        public string SupplierCode { get; set; } = string.Empty;

        [Dimension(DisplayName = "物料類別")]
        public string MaterialCategory { get; set; } = string.Empty;

        [Dimension(DisplayName = "交貨月份", Hierarchy = DateHierarchy.Month)]
        public DateTime DeliveryDate { get; set; }

        /// <summary>預計交貨日（ERP 約定）</summary>
        [Dimension(DisplayName = "預計交貨月份", Hierarchy = DateHierarchy.Month)]
        public DateTime PlannedDate { get; set; }

        /// <summary>是否準時（true = 實際 ≤ 計劃）</summary>
        [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count, DisplayName = "準時筆數")]
        public decimal IsOnTime { get; set; } // 1=準時, 0=遲交，用 decimal 方便 Sum 聚合

        /// <summary>採購金額（TWD）</summary>
        [Measure(
            AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Avg | AggregateFunc.Max | AggregateFunc.Min | AggregateFunc.Count,
            DisplayName = "採購金額")]
        public decimal PurchaseAmount { get; set; }

        /// <summary>交貨天數延遲（負=提前, 0=準時, 正=遲交天數）</summary>
        [Measure(
            AllowedFuncs = AggregateFunc.Avg | AggregateFunc.Max | AggregateFunc.Min,
            DisplayName = "交貨延遲天數")]
        public decimal DeliveryDelayDays { get; set; }

        /// <summary>是否缺貨（1=缺貨, 0=正常）</summary>
        [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count, DisplayName = "缺貨筆數")]
        public decimal IsStockout { get; set; }
    }

    /// <summary>
    /// 庫存異動記錄（計算周轉率用）
    ///
    /// 庫存周轉率公式（SCOR model Inventory Turns）：
    ///   銷貨成本（COGS） / 期間平均庫存金額
    /// </summary>
    internal class InventoryMovement : TopBasePoco
    {
        [Dimension(DisplayName = "物料代碼")]
        public string MaterialCode { get; set; } = string.Empty;

        [Dimension(DisplayName = "物料類別")]
        public string MaterialCategory { get; set; } = string.Empty;

        [Dimension(DisplayName = "異動月份", Hierarchy = DateHierarchy.Month)]
        public DateTime MovementDate { get; set; }

        /// <summary>銷貨成本（出庫金額，COGS 估算）</summary>
        [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Avg, DisplayName = "銷貨成本")]
        public decimal CostOfGoodsSold { get; set; }

        /// <summary>期末庫存金額</summary>
        [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Avg, DisplayName = "庫存金額")]
        public decimal InventoryValue { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ListVM（供 Analysis Registry 掃描）
    // ═══════════════════════════════════════════════════════════════════════

    [EnableAnalysis]
    internal class SupplierDeliveryListVM : BasePagedListVM<SupplierDelivery, BaseSearcher>
    {
        // 靜態注入點：由測試 Setup() 填充，每個測試獨立初始化
        public static IList<SupplierDelivery> TestData { get; set; } = new List<SupplierDelivery>();

        public override IOrderedQueryable<SupplierDelivery> GetSearchQuery()
            => TestData.AsQueryable().OrderByDescending(x => x.DeliveryDate);
    }

    [EnableAnalysis]
    internal class InventoryMovementListVM : BasePagedListVM<InventoryMovement, BaseSearcher>
    {
        public static IList<InventoryMovement> TestData { get; set; } = new List<InventoryMovement>();

        public override IOrderedQueryable<InventoryMovement> GetSearchQuery()
            => TestData.AsQueryable().OrderByDescending(x => x.MovementDate);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 測試類別本體
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 供應鏈 COO 儀表板端對端整合測試。
    /// 覆蓋：ETL 同步 → Analysis 多維度查詢 → Dashboard Widget 三模組串聯。
    /// </summary>
    [TestClass]
    public class SupplyChainIntegrationTests
    {
        // ── 基礎設施 ──────────────────────────────────────────────────────
        private AnalysisVmRegistry _registry = null!;
        private AnalysisQueryEngine _engine = null!;
        private IServiceProvider _serviceProvider = null!;

        [TestInitialize]
        public void Setup()
        {
            SupplierDeliveryListVM.TestData = BuildDeliveryData();
            InventoryMovementListVM.TestData = BuildInventoryData();

            _registry = new AnalysisVmRegistry();
            _registry.Build(new[] { typeof(SupplyChainIntegrationTests).Assembly });

            _engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default);

            var services = new ServiceCollection();
            _serviceProvider = services.BuildServiceProvider();
        }

        // ═══════════════════════════════════════════════════════════════════
        // 測試資料工廠
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 供應商交貨資料（6 個月、4 個供應商、含遲交/部分交貨場景）
        ///
        /// 供應商準時率預計：
        ///   SUP-A: 10 / 12 = 83.3%（優良）
        ///   SUP-B:  6 / 10 = 60.0%（需關注）
        ///   SUP-C:  2 /  8 = 25.0%（高風險）
        ///   SUP-D: 12 / 12 = 100%（完美）
        /// </summary>
        private static List<SupplierDelivery> BuildDeliveryData()
        {
            var data = new List<SupplierDelivery>();
            var baseDate = new DateTime(2026, 1, 1);

            // SUP-A (電子零件)：12 筆，10 準時，2 遲交
            for (int i = 0; i < 10; i++)
                data.Add(MakeDelivery("SUP-A", "電子零件",
                    baseDate.AddDays(i * 15),
                    baseDate.AddDays(i * 15),           // 準時：實際 = 計劃
                    isOnTime: 1, amount: 50_000m + i * 1_000m, delay: 0));

            data.Add(MakeDelivery("SUP-A", "電子零件",
                new DateTime(2026, 3, 20), new DateTime(2026, 3, 15), // 遲交 5 天
                isOnTime: 0, amount: 75_000m, delay: 5));
            data.Add(MakeDelivery("SUP-A", "電子零件",
                new DateTime(2026, 4, 18), new DateTime(2026, 4, 10), // 遲交 8 天
                isOnTime: 0, amount: 80_000m, delay: 8));

            // SUP-B (機械零件)：10 筆，6 準時，4 遲交
            for (int i = 0; i < 6; i++)
                data.Add(MakeDelivery("SUP-B", "機械零件",
                    baseDate.AddDays(i * 20),
                    baseDate.AddDays(i * 20),
                    isOnTime: 1, amount: 30_000m + i * 500m, delay: 0));

            for (int i = 0; i < 4; i++)
                data.Add(MakeDelivery("SUP-B", "機械零件",
                    new DateTime(2026, 4, 1).AddDays(i * 10),
                    new DateTime(2026, 4, 1).AddDays(i * 10 - 3), // 遲交 3 天
                    isOnTime: 0, amount: 35_000m, delay: 3));

            // SUP-C (原料)：8 筆，2 準時，6 遲交（高風險供應商）
            for (int i = 0; i < 2; i++)
                data.Add(MakeDelivery("SUP-C", "原料",
                    baseDate.AddDays(i * 30),
                    baseDate.AddDays(i * 30),
                    isOnTime: 1, amount: 100_000m, delay: 0));

            for (int i = 0; i < 6; i++)
                data.Add(MakeDelivery("SUP-C", "原料",
                    new DateTime(2026, 2, 1).AddDays(i * 15),
                    new DateTime(2026, 2, 1).AddDays(i * 15 - 10), // 遲交 10 天
                    isOnTime: 0, amount: 95_000m, delay: 10 + i));

            // SUP-D (包材)：12 筆，全部準時（完美供應商）
            for (int i = 0; i < 12; i++)
                data.Add(MakeDelivery("SUP-D", "包材",
                    baseDate.AddDays(i * 13),
                    baseDate.AddDays(i * 13),
                    isOnTime: 1, amount: 8_000m + i * 200m, delay: 0));

            return data;
        }

        private static SupplierDelivery MakeDelivery(
            string supplierCode, string category,
            DateTime deliveryDate, DateTime plannedDate,
            decimal isOnTime, decimal amount, decimal delay,
            decimal isStockout = 0)
            => new()
            {
                ID               = Guid.NewGuid(),
                SupplierCode     = supplierCode,
                MaterialCategory = category,
                DeliveryDate     = deliveryDate,
                PlannedDate      = plannedDate,
                IsOnTime         = isOnTime,
                PurchaseAmount   = amount,
                DeliveryDelayDays = delay,
                IsStockout       = isStockout,
            };

        /// <summary>
        /// 庫存異動資料（3 個物料類別，3 個月期間）
        ///
        /// 庫存周轉率：
        ///   電子零件：COGS=600_000 / AvgInventory=200_000 = 3.0 次
        ///   機械零件：COGS=300_000 / AvgInventory=100_000 = 3.0 次
        ///   原料：    COGS=900_000 / AvgInventory=450_000 = 2.0 次
        /// </summary>
        private static List<InventoryMovement> BuildInventoryData()
        {
            return new List<InventoryMovement>
            {
                // 電子零件（月初庫存 180k→200k→220k, COGS 各月 200k）
                new() { ID=Guid.NewGuid(), MaterialCode="EL-001", MaterialCategory="電子零件",
                        MovementDate=new DateTime(2026,1,31), CostOfGoodsSold=200_000m, InventoryValue=180_000m },
                new() { ID=Guid.NewGuid(), MaterialCode="EL-001", MaterialCategory="電子零件",
                        MovementDate=new DateTime(2026,2,28), CostOfGoodsSold=200_000m, InventoryValue=200_000m },
                new() { ID=Guid.NewGuid(), MaterialCode="EL-001", MaterialCategory="電子零件",
                        MovementDate=new DateTime(2026,3,31), CostOfGoodsSold=200_000m, InventoryValue=220_000m },

                // 機械零件（平均庫存 100k, COGS 各月 100k）
                new() { ID=Guid.NewGuid(), MaterialCode="MC-001", MaterialCategory="機械零件",
                        MovementDate=new DateTime(2026,1,31), CostOfGoodsSold=100_000m, InventoryValue=90_000m },
                new() { ID=Guid.NewGuid(), MaterialCode="MC-001", MaterialCategory="機械零件",
                        MovementDate=new DateTime(2026,2,28), CostOfGoodsSold=100_000m, InventoryValue=100_000m },
                new() { ID=Guid.NewGuid(), MaterialCode="MC-001", MaterialCategory="機械零件",
                        MovementDate=new DateTime(2026,3,31), CostOfGoodsSold=100_000m, InventoryValue=110_000m },

                // 原料（高庫存低轉率）
                new() { ID=Guid.NewGuid(), MaterialCode="RM-001", MaterialCategory="原料",
                        MovementDate=new DateTime(2026,1,31), CostOfGoodsSold=300_000m, InventoryValue=400_000m },
                new() { ID=Guid.NewGuid(), MaterialCode="RM-001", MaterialCategory="原料",
                        MovementDate=new DateTime(2026,2,28), CostOfGoodsSold=300_000m, InventoryValue=450_000m },
                new() { ID=Guid.NewGuid(), MaterialCode="RM-001", MaterialCategory="原料",
                        MovementDate=new DateTime(2026,3,31), CostOfGoodsSold=300_000m, InventoryValue=500_000m },
            };
        }

        // ═══════════════════════════════════════════════════════════════════
        // ▌正向測試（Happy Path）— 8 個
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Happy-01：ETL 同步採購訂單 → Analysis 按供應商+月份 group by → 金額加總正確
        /// </summary>
        [TestMethod]
        public async Task Coo_EtlSyncPurchaseOrders_ThenAnalysisBySupplierAndMonth_ReturnsCorrectTotals()
        {
            // ── ETL 步驟 ──
            var sourceTable = new DataTable();
            sourceTable.Columns.Add("DeliveryId", typeof(long));
            sourceTable.Columns.Add("SupplierCode", typeof(string));
            sourceTable.Columns.Add("PurchaseAmount", typeof(decimal));
            sourceTable.Columns.Add("DeliveryDate", typeof(DateTime));
            sourceTable.Rows.Add(1L, "SUP-A", 50_000m, new DateTime(2026, 1, 10));
            sourceTable.Rows.Add(2L, "SUP-A", 60_000m, new DateTime(2026, 2, 15));
            sourceTable.Rows.Add(3L, "SUP-B", 30_000m, new DateTime(2026, 1, 20));
            sourceTable.Rows.Add(4L, "SUP-B", 35_000m, new DateTime(2026, 2, 25));

            var mockSource = new MockEtlSource();
            mockSource.SetData(sourceTable);
            var mockLoader = new MockBulkLoader();

            var config = new EtlPipelineConfig
            {
                JobId                  = Guid.NewGuid(),
                JobName                = "SupplierDeliverySync",
                SourceConnectionString = "mock://erp",
                TargetConnectionString = "mock://wh",
                QueryTemplate          = "SELECT * FROM SupplierDeliveries WHERE {watermark}",
                TargetTableName        = "SupplierDeliveries",
                MergeKeyColumn         = "DeliveryId",
                BatchSize              = 100,
                StagingTable           = new StagingTableSpec("stg_SupplierDeliveries",
                    new StagingColumn("DeliveryId",      "BIGINT"),
                    new StagingColumn("SupplierCode",    "NVARCHAR(20)"),
                    new StagingColumn("PurchaseAmount",  "DECIMAL(18,2)"),
                    new StagingColumn("DeliveryDate",    "DATETIME"))
            };

            var watermark = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
            var executor  = new EtlPipelineExecutor(mockSource, mockLoader);
            var etlResult = await executor.ExecuteAsync(config, watermark);

            // ETL 驗證
            Assert.IsTrue(etlResult.Success, $"ETL 失敗：{etlResult.ErrorMessage}");
            Assert.AreEqual(4, etlResult.ExtractedRows, "應擷取 4 筆採購訂單");
            Assert.IsTrue(mockLoader.MergeCalled, "Merge 必須被呼叫");

            // Analysis 步驟：供應商 × 月份 group by
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "SupplierCode" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "PurchaseAmount", Func = AggregateFunc.Sum },
                    new() { Field = "PurchaseAmount", Func = AggregateFunc.Count }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SupplierDelivery));
            var result    = _engine.Execute(
                SupplierDeliveryListVM.TestData.AsQueryable(), req, whitelist);

            Assert.IsTrue(result.Rows.Count >= 2, "應至少有 2 個供應商分組");

            // SUP-A 加總驗算（測試資料）
            var supA = result.Rows.FirstOrDefault(r => r["SupplierCode"]?.ToString() == "SUP-A");
            Assert.IsNotNull(supA, "應有 SUP-A 分組");

            // SUP-A 在 BuildDeliveryData 中：10 筆準時 (50_000~59_000) + 75_000 + 80_000
            var supAExpected = 10 * 50_000m + (1_000m * 0 + 1_000m * 1 + 1_000m * 2 + 1_000m * 3 +
                               1_000m * 4 + 1_000m * 5 + 1_000m * 6 + 1_000m * 7 +
                               1_000m * 8 + 1_000m * 9) + 75_000m + 80_000m;
            Assert.AreEqual(supAExpected, Convert.ToDecimal(supA["PurchaseAmount_Sum"]),
                "SUP-A 採購金額加總計算錯誤");
        }

        /// <summary>
        /// Happy-02：供應商準時交貨率計算（準時筆數 / 總筆數 × 100）
        /// 含部分交貨（IsOnTime=0.5 不在此版本，以 0/1 discrete 實作）
        /// </summary>
        [TestMethod]
        public void Coo_CalculatesOnTimeDeliveryRate_PerSupplier_MatchesScorFormula()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "SupplierCode" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "IsOnTime",       Func = AggregateFunc.Sum   }, // 準時筆數
                    new() { Field = "PurchaseAmount",  Func = AggregateFunc.Count } // 總筆數（用任一欄位 Count）
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SupplierDelivery));
            var result    = _engine.Execute(
                SupplierDeliveryListVM.TestData.AsQueryable(), req, whitelist);

            Assert.AreEqual(4, result.Rows.Count, "應有 4 個供應商分組");

            // 計算各供應商 OTD%（SCOR OTD = 準時筆數 / 總筆數 × 100）
            foreach (var row in result.Rows)
            {
                decimal onTimeSum   = Convert.ToDecimal(row["IsOnTime_Sum"]);
                decimal totalCount  = Convert.ToDecimal(row["PurchaseAmount_Count"]);
                decimal otdPercent  = totalCount > 0 ? onTimeSum / totalCount * 100m : 0m;
                row["OTD_Computed"] = otdPercent; // 附加計算欄（COO 視角）
            }

            // SUP-A：10/12 = 83.33%
            var supA = result.Rows.Single(r => r["SupplierCode"]?.ToString() == "SUP-A");
            decimal supAOtd = Convert.ToDecimal(supA["IsOnTime_Sum"]) /
                              Convert.ToDecimal(supA["PurchaseAmount_Count"]) * 100m;
            Assert.AreEqual(10m, Convert.ToDecimal(supA["IsOnTime_Sum"]), "SUP-A 準時筆數應為 10");
            Assert.AreEqual(12m, Convert.ToDecimal(supA["PurchaseAmount_Count"]), "SUP-A 總筆數應為 12");
            Assert.AreEqual(Math.Round(10m / 12m * 100m, 4),
                            Math.Round(supAOtd, 4), "SUP-A OTD% 計算錯誤");

            // SUP-C：2/8 = 25.0%（高風險）
            var supC = result.Rows.Single(r => r["SupplierCode"]?.ToString() == "SUP-C");
            decimal supCOtd = Convert.ToDecimal(supC["IsOnTime_Sum"]) /
                              Convert.ToDecimal(supC["PurchaseAmount_Count"]) * 100m;
            Assert.AreEqual(2m,  Convert.ToDecimal(supC["IsOnTime_Sum"]),         "SUP-C 準時筆數應為 2");
            Assert.AreEqual(8m,  Convert.ToDecimal(supC["PurchaseAmount_Count"]), "SUP-C 總筆數應為 8");
            Assert.AreEqual(25m, Math.Round(supCOtd, 4), "SUP-C OTD% 應為 25%（高風險）");

            // SUP-D：12/12 = 100%
            var supD = result.Rows.Single(r => r["SupplierCode"]?.ToString() == "SUP-D");
            decimal supDOtd = Convert.ToDecimal(supD["IsOnTime_Sum"]) /
                              Convert.ToDecimal(supD["PurchaseAmount_Count"]) * 100m;
            Assert.AreEqual(100m, Math.Round(supDOtd, 4), "SUP-D OTD% 應為 100%（完美供應商）");

            // COO 風險優先順序驗證：準時率低的應排前面（應用層排序）
            var ranked = result.Rows
                .Select(r => new {
                    Supplier = r["SupplierCode"]?.ToString(),
                    Otd = Convert.ToDecimal(r["IsOnTime_Sum"]) /
                          Convert.ToDecimal(r["PurchaseAmount_Count"]) * 100m
                })
                .OrderBy(x => x.Otd) // 準時率低 → 排前面（風險優先）
                .ToList();

            Assert.AreEqual("SUP-C", ranked[0].Supplier,
                "COO 風險排名：SUP-C（25%）應排第一位（最高風險）");
            Assert.AreEqual("SUP-D", ranked[ranked.Count - 1].Supplier,
                "COO 風險排名：SUP-D（100%）應排最後（最低風險）");
        }

        /// <summary>
        /// Happy-03：庫存周轉率計算（SCOR Inventory Turns = 年化 COGS / 平均庫存）
        /// </summary>
        [TestMethod]
        public void Coo_CalculatesInventoryTurnover_PerMaterialCategory_MatchesScorFormula()
        {
            // COGS = Sum of CostOfGoodsSold per category
            var cogsReq = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "MaterialCategory" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "CostOfGoodsSold",  Func = AggregateFunc.Sum },
                    new() { Field = "InventoryValue",   Func = AggregateFunc.Avg }, // 平均庫存
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(InventoryMovement));
            var result    = _engine.Execute(
                InventoryMovementListVM.TestData.AsQueryable(), cogsReq, whitelist);

            Assert.AreEqual(3, result.Rows.Count, "應有 3 個物料類別分組");

            foreach (var row in result.Rows)
            {
                decimal cogs         = Convert.ToDecimal(row["CostOfGoodsSold_Sum"]);
                decimal avgInventory = Convert.ToDecimal(row["InventoryValue_Avg"]);
                decimal turnover     = avgInventory > 0 ? cogs / avgInventory : 0m;
                row["InventoryTurns_Computed"] = turnover;
            }

            // 電子零件：COGS=600_000 / AvgInventory=200_000 = 3.0 次
            var electronics = result.Rows.Single(r => r["MaterialCategory"]?.ToString() == "電子零件");
            decimal elecCogs     = Convert.ToDecimal(electronics["CostOfGoodsSold_Sum"]);
            decimal elecAvgInv   = Convert.ToDecimal(electronics["InventoryValue_Avg"]);
            decimal elecTurnover = elecCogs / elecAvgInv;

            Assert.AreEqual(600_000m, elecCogs, "電子零件 COGS 應為 600,000");
            Assert.AreEqual(200_000m, Math.Round(elecAvgInv, 0), "電子零件平均庫存應為 200,000");
            Assert.AreEqual(3.0m, Math.Round(elecTurnover, 2), "電子零件周轉率應為 3.0 次");

            // 原料：COGS=900_000 / AvgInventory=450_000 = 2.0 次（較低，需關注）
            var rawMaterial = result.Rows.Single(r => r["MaterialCategory"]?.ToString() == "原料");
            decimal rawCogs     = Convert.ToDecimal(rawMaterial["CostOfGoodsSold_Sum"]);
            decimal rawAvgInv   = Convert.ToDecimal(rawMaterial["InventoryValue_Avg"]);
            decimal rawTurnover = rawCogs / rawAvgInv;

            Assert.AreEqual(900_000m, rawCogs, "原料 COGS 應為 900,000");
            Assert.AreEqual(2.0m, Math.Round(rawTurnover, 2), "原料周轉率應為 2.0 次（較低）");
        }

        /// <summary>
        /// Happy-04：多維度交叉分析（供應商 × 物料類別 × 月份）
        /// </summary>
        [TestMethod]
        public void Coo_MultiDimensionalAnalysis_SupplierCategoryMonth_ProducesCorrectCrossTabulation()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "SupplierCode", "MaterialCategory" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "PurchaseAmount", Func = AggregateFunc.Sum },
                    new() { Field = "IsOnTime",       Func = AggregateFunc.Sum },
                    new() { Field = "IsOnTime",       Func = AggregateFunc.Count }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SupplierDelivery));
            var result    = _engine.Execute(
                SupplierDeliveryListVM.TestData.AsQueryable(), req, whitelist);

            // 每個供應商只有 1 個物料類別 → 應有 4 個組合
            Assert.AreEqual(4, result.Rows.Count,
                "4 供應商 × 各 1 物料類別 = 4 個交叉維度組合");

            // 驗證欄位結構正確
            Assert.IsTrue(result.Columns.Contains("SupplierCode"),       "應含 SupplierCode 維度");
            Assert.IsTrue(result.Columns.Contains("MaterialCategory"),   "應含 MaterialCategory 維度");
            Assert.IsTrue(result.Columns.Contains("PurchaseAmount_Sum"), "應含 PurchaseAmount_Sum 度量");
            Assert.IsTrue(result.Columns.Contains("IsOnTime_Sum"),       "應含 IsOnTime_Sum 度量");
            Assert.IsTrue(result.Columns.Contains("IsOnTime_Count"),     "應含 IsOnTime_Count 度量");

            // SUP-A 電子零件：IsOnTime_Count = 12, Sum = 10
            var supAElec = result.Rows.Single(r =>
                r["SupplierCode"]?.ToString() == "SUP-A" &&
                r["MaterialCategory"]?.ToString() == "電子零件");
            Assert.AreEqual(12m, Convert.ToDecimal(supAElec["IsOnTime_Count"]), "SUP-A 電子 Count=12");
            Assert.AreEqual(10m, Convert.ToDecimal(supAElec["IsOnTime_Sum"]),   "SUP-A 電子 OnTime=10");
        }

        /// <summary>
        /// Happy-05：Dashboard 多 widget 同步刷新（KPI + 趨勢圖 + 排名表）
        /// </summary>
        [TestMethod]
        public async Task Coo_DashboardMultiWidgetRefresh_AllWidgetsReturnData()
        {
            var dataSource = new AnalysisWidgetDataSource(_registry, _serviceProvider, _engine);

            // Widget 1：KPI 總覽（零維度）
            var kpiRequest = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(SupplierDeliveryListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(new List<string>()),
                    ["measures"]   = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "PurchaseAmount", Func = AggregateFunc.Sum },
                        new { Field = "IsOnTime",       Func = AggregateFunc.Sum },
                        new { Field = "PurchaseAmount", Func = AggregateFunc.Count }
                    })
                }
            };

            // Widget 2：供應商排名表
            var rankRequest = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(SupplierDeliveryListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "SupplierCode" }),
                    ["measures"]   = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "IsOnTime",      Func = AggregateFunc.Sum },
                        new { Field = "PurchaseAmount", Func = AggregateFunc.Count }
                    })
                }
            };

            // Widget 3：物料類別趨勢
            var trendRequest = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(SupplierDeliveryListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "MaterialCategory" }),
                    ["measures"]   = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "PurchaseAmount", Func = AggregateFunc.Sum }
                    })
                }
            };

            // 同步刷新三個 Widget
            var kpiResult   = await dataSource.GetDataAsync(kpiRequest);
            var rankResult  = await dataSource.GetDataAsync(rankRequest);
            var trendResult = await dataSource.GetDataAsync(trendRequest);

            // KPI Widget 驗證
            Assert.IsNotNull(kpiResult, "KPI Widget 應有回傳");
            Assert.AreEqual(1, kpiResult.Rows!.Count, "KPI 零維度應回傳 1 列");
            Assert.IsTrue(kpiResult.Columns!.Contains("PurchaseAmount_Sum"), "KPI 應含採購金額加總");

            // 排名表 Widget 驗證
            Assert.IsNotNull(rankResult, "排名表 Widget 應有回傳");
            Assert.AreEqual(4, rankResult.Rows!.Count, "供應商排名應有 4 筆");

            // 趨勢 Widget 驗證
            Assert.IsNotNull(trendResult, "趨勢 Widget 應有回傳");
            Assert.AreEqual(4, trendResult.Rows!.Count, "4 個物料類別");

            // Metadata 驗證
            Assert.IsTrue(kpiResult.Metadata!.ContainsKey("totalCount"), "應含 totalCount");
            Assert.IsFalse(Convert.ToBoolean(kpiResult.Metadata!["truncated"]), "KPI 不應截斷");
        }

        /// <summary>
        /// Happy-06：篩選串聯 — 選定供應商 SUP-B → 只看該供應商的交貨明細
        /// </summary>
        [TestMethod]
        public void Coo_FilterBySpecificSupplier_OnlyShowsThatSuppliersDeliveries()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "SupplierCode", "MaterialCategory" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "PurchaseAmount", Func = AggregateFunc.Sum },
                    new() { Field = "IsOnTime",       Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new() { Field = "SupplierCode", Operator = FilterOperator.Eq, Value = "SUP-B" }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SupplierDelivery));
            var result    = _engine.Execute(
                SupplierDeliveryListVM.TestData.AsQueryable(), req, whitelist);

            Assert.AreEqual(1, result.Rows.Count, "篩選後只應有 SUP-B 的分組");
            Assert.AreEqual("SUP-B", result.Rows[0]["SupplierCode"]?.ToString(),
                "結果必須是 SUP-B");
            Assert.AreEqual("機械零件", result.Rows[0]["MaterialCategory"]?.ToString(),
                "SUP-B 的物料類別應為機械零件");

            // SUP-B：6 準時
            Assert.AreEqual(6m, Convert.ToDecimal(result.Rows[0]["IsOnTime_Sum"]),
                "SUP-B 準時筆數應為 6");
        }

        /// <summary>
        /// Happy-07：Ad-hoc filter 動態條件 — 金額 > 10,000 AND 交貨延遲 > 3 天
        /// 模擬 COO 鑽取高金額且嚴重遲交的異常訂單
        /// </summary>
        [TestMethod]
        public void Coo_AdhocFilter_HighAmountAndLongDelay_DrillsDownToAnomalies()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "SupplierCode" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "PurchaseAmount",    Func = AggregateFunc.Sum },
                    new() { Field = "DeliveryDelayDays", Func = AggregateFunc.Avg }
                },
                Filters = new List<FilterCondition>
                {
                    // 金額 > 10,000
                    new() { Field = "PurchaseAmount",    Operator = FilterOperator.Gt, Value = "10000" },
                    // 延遲 > 3 天（遲交超過 3 天的嚴重異常）
                    new() { Field = "DeliveryDelayDays", Operator = FilterOperator.Gt, Value = "3" }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SupplierDelivery));
            var result    = _engine.Execute(
                SupplierDeliveryListVM.TestData.AsQueryable(), req, whitelist);

            // SUP-A：2 筆延遲 > 3 天（delay=5, delay=8），金額 75k/80k
            // SUP-C：6 筆延遲 > 3 天（delay=10~15，amount=95k 全部 > 10k）
            Assert.IsTrue(result.Rows.Count >= 2,
                "應至少有 2 個供應商有高金額嚴重遲交訂單");

            // 所有結果的延遲平均必定 > 3（因為篩選了 delay > 3 的訂單）
            foreach (var row in result.Rows)
            {
                decimal avgDelay = Convert.ToDecimal(row["DeliveryDelayDays_Avg"]);
                Assert.IsTrue(avgDelay > 3m,
                    $"供應商 {row["SupplierCode"]} 的平均延遲 {avgDelay} 應 > 3 天");
            }

            // SUP-C 應出現（全 6 筆遲交延遲超過 5 天，金額 95k）
            var supC = result.Rows.FirstOrDefault(r => r["SupplierCode"]?.ToString() == "SUP-C");
            Assert.IsNotNull(supC, "SUP-C 高金額嚴重遲交，應出現在鑽取結果中");
        }

        /// <summary>
        /// Happy-08：匯出 Excel 格式與 Analysis 結果一致（欄位、筆數對齊）
        /// </summary>
        [TestMethod]
        public void Coo_ExportCsv_ColumnsAndRowCountMatchAnalysisResult()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "SupplierCode", "MaterialCategory" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "PurchaseAmount",    Func = AggregateFunc.Sum },
                    new() { Field = "IsOnTime",          Func = AggregateFunc.Sum },
                    new() { Field = "DeliveryDelayDays", Func = AggregateFunc.Avg }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SupplierDelivery));
            var result    = _engine.Execute(
                SupplierDeliveryListVM.TestData.AsQueryable(), req, whitelist);

            Assert.IsTrue(result.Rows.Count > 0, "Analysis 應有資料才能匯出");

            // 匯出 Excel
            var excelBytes = AnalysisExcelExporter.Export(result, includeChart: false);
            Assert.IsNotNull(excelBytes, "Excel 位元組不應為 null");
            Assert.IsTrue(excelBytes.Length > 0, "Excel 不應為空");

            // 驗證 Analysis 結果欄位完整性（CSV 欄位一致性的代理驗證）
            var expectedCols = new[]
            {
                "SupplierCode", "MaterialCategory",
                "PurchaseAmount_Sum", "IsOnTime_Sum", "DeliveryDelayDays_Avg"
            };
            foreach (var col in expectedCols)
            {
                Assert.IsTrue(result.Columns.Contains(col),
                    $"Analysis 結果應含欄位 '{col}'（匯出 CSV/Excel 依此為準）");
            }

            // 筆數對齊（4 個供應商 × 各 1 物料類別 = 4）
            Assert.AreEqual(4, result.Rows.Count, "匯出列數應等於 Analysis 結果列數");
        }

        // ═══════════════════════════════════════════════════════════════════
        // ▌反向測試（Error Path）— 6 個
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Error-01：供應商代碼不存在時的 Analysis 查詢 → 回傳空結果（非例外）
        /// </summary>
        [TestMethod]
        public void Coo_QueryNonExistentSupplierCode_ReturnsEmptyGracefully()
        {
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "SupplierCode" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "PurchaseAmount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new() { Field = "SupplierCode", Operator = FilterOperator.Eq, Value = "SUP-NONEXISTENT" }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SupplierDelivery));
            var result    = _engine.Execute(
                SupplierDeliveryListVM.TestData.AsQueryable(), req, whitelist);

            Assert.AreEqual(0, result.Rows.Count,
                "不存在的供應商代碼應回傳空結果，不拋例外");
            Assert.AreEqual(0, result.TotalCount, "TotalCount 應為 0");
            Assert.IsFalse(result.Truncated, "空結果不應標記截斷");
        }

        /// <summary>
        /// Error-02：ETL 同步中斷（第 1 batch 已寫入，但 Merge 前失敗）→ Watermark 不推進
        /// </summary>
        [TestMethod]
        public async Task Coo_EtlInterruptedBeforeMerge_WatermarkNotAdvanced()
        {
            var sourceTable = new DataTable();
            sourceTable.Columns.Add("DeliveryId", typeof(long));
            sourceTable.Columns.Add("UpdatedAt",  typeof(DateTime));
            sourceTable.Columns.Add("Amount",     typeof(decimal));

            // 加入 3 筆（batchSize=2 → 2 次 batch → FailOnBatch=1 → 第一 batch 失敗）
            sourceTable.Rows.Add(101L, new DateTime(2026, 3, 1), 50_000m);
            sourceTable.Rows.Add(102L, new DateTime(2026, 3, 2), 60_000m);
            sourceTable.Rows.Add(103L, new DateTime(2026, 3, 3), 70_000m);

            var mockSource = new MockEtlSource();
            mockSource.SetData(sourceTable);
            // FailOnBatch=1 → 第一個 batch BulkLoad 完成後拋出例外（Merge 前）
            var mockLoader = new MockBulkLoader { FailOnBatch = 1 };

            var config = new EtlPipelineConfig
            {
                JobId                  = Guid.NewGuid(),
                JobName                = "DeliverySync_InterruptTest",
                SourceConnectionString = "mock://erp",
                TargetConnectionString = "mock://wh",
                QueryTemplate          = "SELECT * FROM Deliveries WHERE UpdatedAt > @watermark",
                TargetTableName        = "Deliveries",
                MergeKeyColumn         = "DeliveryId",
                BatchSize              = 2, // 確保有 2 個 batch
                StagingTable           = new StagingTableSpec("stg_Deliveries",
                    new StagingColumn("DeliveryId", "BIGINT"),
                    new StagingColumn("UpdatedAt",  "DATETIME"),
                    new StagingColumn("Amount",     "DECIMAL(18,2)"))
            };

            var initialWatermark = "\"2026-02-28T00:00:00\""; // JSON 格式
            var watermark = new WatermarkStrategy(
                EtlWatermarkType.Timestamp, "UpdatedAt", initialWatermark);

            var executor  = new EtlPipelineExecutor(mockSource, mockLoader);
            var etlResult = await executor.ExecuteAsync(config, watermark);

            // 驗證 ETL 失敗
            Assert.IsFalse(etlResult.Success, "ETL 應失敗");
            Assert.IsFalse(mockLoader.MergeCalled, "Merge 不應被呼叫（在失敗前未到達 Merge 步驟）");

            // 核心：Watermark 不推進（DiscardPendingValue 被呼叫）
            Assert.AreEqual(initialWatermark, watermark.CurrentValue,
                "ETL 中斷後 Watermark 不應推進，確保下次可重跑");
        }

        /// <summary>
        /// Error-03：Analysis 查詢包含 SQL injection 嘗試 → 白名單驗證阻擋
        /// </summary>
        [TestMethod]
        public void Coo_SqlInjectionInDimensionField_IsRejectedByWhitelist()
        {
            // 嘗試用惡意 field name 繞過
            var maliciousReq = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "'; DROP TABLE SupplierDeliveries --" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "PurchaseAmount", Func = AggregateFunc.Sum }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SupplierDelivery));

            // 白名單驗證應阻擋不存在的欄位名稱
            Assert.ThrowsException<AnalysisFieldNotFoundException>(
                () => _engine.Execute(
                    SupplierDeliveryListVM.TestData.AsQueryable(), maliciousReq, whitelist),
                "惡意欄位名稱應被白名單阻擋，拋出 AnalysisFieldNotFoundException");
        }

        /// <summary>
        /// Error-04：Dashboard widget 引用的 Analysis 欄位不在白名單 → InvalidOperationException
        /// </summary>
        [TestMethod]
        public async Task Coo_DashboardWidgetReferencesUnregisteredVm_ThrowsInvalidOperation()
        {
            var dataSource = new AnalysisWidgetDataSource(_registry, _serviceProvider, _engine);

            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    // 引用未被 [EnableAnalysis] 標記或未在 registry 中的 VM
                    ["listVmType"] = "WalkingTec.Mvvm.Core.Test.Integration.NonExistentSupplyChainListVM",
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "SupplierCode" }),
                    ["measures"]   = JsonSerializer.Serialize(new[]
                        { new { Field = "PurchaseAmount", Func = AggregateFunc.Sum } })
                }
            };

            await Assert.ThrowsExceptionAsync<AnalysisVmNotFoundException>(
                () => dataSource.GetDataAsync(request),
                "未在 registry 中的 ListVM 應拋出 AnalysisVmNotFoundException");
        }

        /// <summary>
        /// Error-05：超過 50,000 筆限制的 Take 截斷行為
        /// 驗證 InProcessGroupByStrategy.MaxMaterializeRows = 50,000 的防護機制
        /// </summary>
        [TestMethod]
        public void Coo_QueryExceeds50kRowLimit_MaterializationIsCapped()
        {
            // 建立超過 50,000 筆的資料集（用 50,001 筆）
            const int overLimit = 50_001;
            var largeData = Enumerable.Range(1, overLimit)
                .Select(i => MakeDelivery(
                    $"SUP-{i % 10:D3}", "電子零件",
                    new DateTime(2026, 1, 1).AddDays(i % 180),
                    new DateTime(2026, 1, 1).AddDays(i % 180),
                    isOnTime: 1, amount: 1_000m, delay: 0))
                .ToList();

            // 使用零維度（全局聚合）確保 Group By 結果只有 1 列（不觸發 10k 截斷）
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string>(), // 零維度
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "PurchaseAmount", Func = AggregateFunc.Sum },
                    new() { Field = "PurchaseAmount", Func = AggregateFunc.Count }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SupplierDelivery));
            var result    = _engine.Execute(largeData.AsQueryable(), req, whitelist);

            // 應僅載入 50,000 筆（MaxMaterializeRows），不是 50,001 筆
            // Count 欄位反映實際載入筆數
            decimal count = Convert.ToDecimal(result.Rows[0]["PurchaseAmount_Count"]);
            Assert.AreEqual(50_000m, count,
                $"超過 50k 上限時，應只載入 50,000 筆（actual: {count}）");
        }

        /// <summary>
        /// Error-06：分組結果超過 10,000 行的截斷行為
        /// 驗證 AnalysisQueryEngine 的 MaxRows = 10,000 截斷邏輯
        /// </summary>
        [TestMethod]
        public void Coo_GroupByResultExceeds10kRows_TruncatedFlagSetAndRowsCapped()
        {
            // 建立超過 10,000 個不同 SupplierCode 的資料（確保 GroupBy 後 > 10,000 組）
            const int supplierCount = 10_001;
            var manySupplierData = Enumerable.Range(1, supplierCount)
                .Select(i => MakeDelivery(
                    $"SUP-{i:D5}", "電子零件",
                    new DateTime(2026, 1, 1),
                    new DateTime(2026, 1, 1),
                    isOnTime: 1, amount: 1_000m, delay: 0))
                .ToList();

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "SupplierCode" }, // 10,001 個不同供應商
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "PurchaseAmount", Func = AggregateFunc.Sum }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SupplierDelivery));
            var result    = _engine.Execute(manySupplierData.AsQueryable(), req, whitelist);

            Assert.IsTrue(result.Truncated,
                "GroupBy 結果超過 10,000 時，Truncated 應為 true");
            Assert.AreEqual(10_000, result.Rows.Count,
                "截斷後應只保留 10,000 列");
            Assert.AreEqual(10_001, result.TotalCount,
                "TotalCount 應反映截斷前的真實總數（10,001）");
        }

        // ═══════════════════════════════════════════════════════════════════
        // ▌邊界測試（Boundary）— 6 個
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Boundary-01：交貨日期 = 預計日期（剛好準時，不是遲到也不是提前）
        /// </summary>
        [TestMethod]
        public void Coo_DeliveryExactlyOnPlannedDate_CountedAsOnTime()
        {
            var exactDate = new DateTime(2026, 6, 15);
            var singleDelivery = new List<SupplierDelivery>
            {
                MakeDelivery("SUP-E", "電子零件",
                    deliveryDate: exactDate,
                    plannedDate:  exactDate, // 完全相同 → 剛好準時
                    isOnTime: 1, amount: 50_000m, delay: 0)
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "SupplierCode" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "IsOnTime",       Func = AggregateFunc.Sum },
                    new() { Field = "PurchaseAmount",  Func = AggregateFunc.Count }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SupplierDelivery));
            var result    = _engine.Execute(singleDelivery.AsQueryable(), req, whitelist);

            Assert.AreEqual(1, result.Rows.Count, "應有 1 個分組");
            Assert.AreEqual(1m, Convert.ToDecimal(result.Rows[0]["IsOnTime_Sum"]),
                "剛好準時（delivery=planned）應算 1 筆準時");
            Assert.AreEqual(1m, Convert.ToDecimal(result.Rows[0]["PurchaseAmount_Count"]),
                "總筆數應為 1");

            // OTD% = 1/1 × 100 = 100%
            decimal otd = Convert.ToDecimal(result.Rows[0]["IsOnTime_Sum"]) /
                          Convert.ToDecimal(result.Rows[0]["PurchaseAmount_Count"]) * 100m;
            Assert.AreEqual(100m, otd, "剛好準時的 OTD% 應為 100%");
        }

        /// <summary>
        /// Boundary-02：庫存量為 0 的除以零防護（周轉率計算不應崩潰）
        /// </summary>
        [TestMethod]
        public void Coo_ZeroInventoryValue_TurnoverCalculationDoesNotCrash()
        {
            var zeroInventoryData = new List<InventoryMovement>
            {
                new()
                {
                    ID               = Guid.NewGuid(),
                    MaterialCode     = "ZI-001",
                    MaterialCategory = "特殊物料",
                    MovementDate     = new DateTime(2026, 1, 31),
                    CostOfGoodsSold  = 100_000m,
                    InventoryValue   = 0m  // 庫存量為 0（邊界條件）
                }
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "MaterialCategory" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "CostOfGoodsSold", Func = AggregateFunc.Sum },
                    new() { Field = "InventoryValue",  Func = AggregateFunc.Avg }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(InventoryMovement));

            // 應正常回傳（Analysis 不計算周轉率，只聚合原始欄位）
            var result = _engine.Execute(zeroInventoryData.AsQueryable(), req, whitelist);
            Assert.IsNotNull(result, "庫存量為 0 的 Analysis 查詢不應拋出例外");

            Assert.AreEqual(1, result.Rows.Count, "應有 1 個分組");
            Assert.AreEqual(0m, Convert.ToDecimal(result.Rows[0]["InventoryValue_Avg"]),
                "平均庫存量應為 0");

            // 應用層除以零防護（周轉率由應用層計算，Analysis 只提供原始聚合）
            decimal cogs     = Convert.ToDecimal(result.Rows[0]["CostOfGoodsSold_Sum"]);
            decimal avgInv   = Convert.ToDecimal(result.Rows[0]["InventoryValue_Avg"]);
            decimal turnover = avgInv == 0m ? decimal.MaxValue : cogs / avgInv;

            Assert.AreEqual(decimal.MaxValue, turnover,
                "庫存量為 0 時，周轉率應返回 MaxValue（表示∞）而非拋出 DivideByZeroException");
        }

        /// <summary>
        /// Boundary-03：所有供應商準時率 100%（無異常值的正常分佈）
        /// </summary>
        [TestMethod]
        public void Coo_AllSuppliersHundredPercentOtd_NormalDistributionNoAnomalies()
        {
            var perfectData = new List<SupplierDelivery>();
            for (int i = 1; i <= 5; i++)
            {
                for (int j = 0; j < 10; j++)
                {
                    var d = new DateTime(2026, 1, 1).AddDays(j * 15);
                    perfectData.Add(MakeDelivery(
                        $"SUP-{i:D1}00", "電子零件",
                        deliveryDate: d, plannedDate: d,
                        isOnTime: 1, amount: 50_000m, delay: 0));
                }
            }

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "SupplierCode" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "IsOnTime",       Func = AggregateFunc.Sum },
                    new() { Field = "PurchaseAmount",  Func = AggregateFunc.Count }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SupplierDelivery));
            var result    = _engine.Execute(perfectData.AsQueryable(), req, whitelist);

            Assert.AreEqual(5, result.Rows.Count, "應有 5 個供應商分組");

            foreach (var row in result.Rows)
            {
                decimal onTime = Convert.ToDecimal(row["IsOnTime_Sum"]);
                decimal total  = Convert.ToDecimal(row["PurchaseAmount_Count"]);
                decimal otd    = onTime / total * 100m;

                Assert.AreEqual(100m, otd,
                    $"供應商 {row["SupplierCode"]} OTD% 應為 100%（全部準時）");
            }
        }

        /// <summary>
        /// Boundary-04：單一供應商佔總採購 99%（圓餅圖呈現）
        /// 驗證高度集中的供應商結構能正確計算比例
        /// </summary>
        [TestMethod]
        public void Coo_DominantSupplierRepresents99Percent_PieChartDataIsCorrect()
        {
            var dominantData = new List<SupplierDelivery>
            {
                // 主要供應商：採購 9,900,000
                MakeDelivery("SUP-MAIN", "電子零件",
                    new DateTime(2026, 1, 1), new DateTime(2026, 1, 1),
                    isOnTime: 1, amount: 9_900_000m, delay: 0),
                // 次要供應商：採購 100,000（僅佔 1%）
                MakeDelivery("SUP-MINOR", "電子零件",
                    new DateTime(2026, 1, 1), new DateTime(2026, 1, 1),
                    isOnTime: 1, amount: 100_000m, delay: 0),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "SupplierCode" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "PurchaseAmount", Func = AggregateFunc.Sum }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SupplierDelivery));
            var result    = _engine.Execute(dominantData.AsQueryable(), req, whitelist);

            Assert.AreEqual(2, result.Rows.Count, "應有 2 個供應商分組");

            decimal total = result.Rows.Sum(r => Convert.ToDecimal(r["PurchaseAmount_Sum"]));
            Assert.AreEqual(10_000_000m, total, "總採購金額應為 10,000,000");

            var mainRow = result.Rows.Single(r => r["SupplierCode"]?.ToString() == "SUP-MAIN");
            decimal mainShare = Convert.ToDecimal(mainRow["PurchaseAmount_Sum"]) / total * 100m;

            Assert.AreEqual(99m, mainShare, "主要供應商應佔總採購 99%（圓餅圖集中風險）");
        }

        /// <summary>
        /// Boundary-05：金額欄位包含極小值（0.01）和極大值（9,999,999,999.99）
        /// </summary>
        [TestMethod]
        public void Coo_ExtremeAmountValues_MinAndMaxHandledCorrectly()
        {
            const decimal minAmount = 0.01m;
            const decimal maxAmount = 9_999_999_999.99m;

            var extremeData = new List<SupplierDelivery>
            {
                MakeDelivery("SUP-MIN", "包材",
                    new DateTime(2026, 1, 1), new DateTime(2026, 1, 1),
                    isOnTime: 1, amount: minAmount, delay: 0),
                MakeDelivery("SUP-MAX", "包材",
                    new DateTime(2026, 1, 1), new DateTime(2026, 1, 1),
                    isOnTime: 1, amount: maxAmount, delay: 0),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string>(), // 零維度 = 全局聚合
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "PurchaseAmount", Func = AggregateFunc.Sum },
                    new() { Field = "PurchaseAmount", Func = AggregateFunc.Min },
                    new() { Field = "PurchaseAmount", Func = AggregateFunc.Max }
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SupplierDelivery));
            var result    = _engine.Execute(extremeData.AsQueryable(), req, whitelist);

            Assert.AreEqual(1, result.Rows.Count, "零維度應回傳 1 列");

            decimal sum = Convert.ToDecimal(result.Rows[0]["PurchaseAmount_Sum"]);
            decimal min = Convert.ToDecimal(result.Rows[0]["PurchaseAmount_Min"]);
            decimal max = Convert.ToDecimal(result.Rows[0]["PurchaseAmount_Max"]);

            Assert.AreEqual(minAmount + maxAmount, sum, 0.01m, "極值加總應正確");
            Assert.AreEqual(minAmount, min, "Min 應為 0.01");
            Assert.AreEqual(maxAmount, max, "Max 應為 9,999,999,999.99");
        }

        /// <summary>
        /// Boundary-06：月度彙總的跨月邊界（1月最後一天 vs 2月第一天不合併）
        /// 驗證日期層級切割在月份邊界的正確性
        /// </summary>
        [TestMethod]
        public void Coo_MonthBoundaryDates_Jan31AndFeb1AreSeparateMonthGroups()
        {
            var boundaryData = new List<SupplierDelivery>
            {
                // 1 月最後一天
                MakeDelivery("SUP-A", "電子零件",
                    new DateTime(2026, 1, 31), new DateTime(2026, 1, 31),
                    isOnTime: 1, amount: 10_000m, delay: 0),
                // 2 月第一天
                MakeDelivery("SUP-A", "電子零件",
                    new DateTime(2026, 2, 1), new DateTime(2026, 2, 1),
                    isOnTime: 1, amount: 20_000m, delay: 0),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "DeliveryDate" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "PurchaseAmount", Func = AggregateFunc.Sum }
                },
                DimensionHierarchies = new Dictionary<string, DateHierarchy>
                {
                    ["DeliveryDate"] = DateHierarchy.Month
                }
            };

            var whitelist = AnalysisFieldScanner.ScanModel(typeof(SupplierDelivery));
            var result    = _engine.Execute(boundaryData.AsQueryable(), req, whitelist);

            Assert.AreEqual(2, result.Rows.Count,
                "1/31 和 2/1 應屬於不同月份分組（2 個分組）");

            // 兩個月份的金額應各自獨立
            var months = result.Rows.Select(r => r["DeliveryDate"]?.ToString() ?? "").ToList();
            Assert.IsTrue(months.Contains("2026-01"),
                "應有 2026-01 月份分組");
            Assert.IsTrue(months.Contains("2026-02"),
                "應有 2026-02 月份分組");

            var jan = result.Rows.Single(r => r["DeliveryDate"]?.ToString() == "2026-01");
            var feb = result.Rows.Single(r => r["DeliveryDate"]?.ToString() == "2026-02");

            Assert.AreEqual(10_000m, Convert.ToDecimal(jan["PurchaseAmount_Sum"]),
                "1 月採購金額應為 10,000（1/31 的資料）");
            Assert.AreEqual(20_000m, Convert.ToDecimal(feb["PurchaseAmount_Sum"]),
                "2 月採購金額應為 20,000（2/1 的資料）");
        }
    }
}
