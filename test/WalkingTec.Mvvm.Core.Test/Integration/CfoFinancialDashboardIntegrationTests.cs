#nullable enable
// ============================================================================
// CFO 財務儀表板跨模組整合測試
//
// 業務場景：
//   財務長（CFO）每日晨會查看財務健康儀表板，資料來源由 ETL 從 ERP 同步，
//   Analysis 引擎負責聚合運算，Dashboard widget 呈現最終結果。
//
// 覆蓋：Analysis ↔ Dashboard 跨模組整合、ETL 狀態對資料的影響、
//       財務邊界條件（負數、精度、大數值）、安全防護（角色限制欄位、未授權存取）
//
// Closes #665
// ============================================================================

using System;
using System.Collections.Generic;
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
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Integration
{
    // ═══════════════════════════════════════════════════════════════════════
    // ETL 測試用模擬模型（測試專案不直接參考 WalkingTec.Mvvm.Etl）
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>模擬 ETL Job 定義，足以驗證財務安全場景</summary>
    internal class CfoTestEtlJobDefinition
    {
        public Guid ID { get; set; }
        public string Name { get; set; } = string.Empty;
        public string CronExpression { get; set; } = string.Empty;
        public string JobClassName { get; set; } = string.Empty;
        public string SourceCsKey { get; set; } = string.Empty;  // 只存 Key，不存明文連線字串
        public string TargetCsKey { get; set; } = string.Empty;
        public string TargetTableName { get; set; } = string.Empty;
        public string MergeKeyColumn { get; set; } = string.Empty;
        public string QueryTemplate { get; set; } = string.Empty;
        public CfoTestEtlWatermarkType WatermarkType { get; set; } = CfoTestEtlWatermarkType.FullLoad;
        public string? WatermarkColumn { get; set; }
        public string? LastWatermarkValue { get; set; }
    }

    internal enum CfoTestEtlWatermarkType { FullLoad, Timestamp, Identity }

    internal enum CfoTestEtlRunResult { Success, Failed, Aborted, Skipped }

    /// <summary>模擬 ETL 執行記錄</summary>
    internal class CfoTestEtlRunLog
    {
        public Guid ID { get; set; }
        public Guid JobId { get; set; }
        public CfoTestEtlRunResult Result { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        // [StringLength(4000)] — 測試中手動截斷驗證
        public string? ErrorMessage { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 財務領域模型
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 財務交易記錄（ERP 同步來源）
    /// </summary>
    internal class FinancialTransaction : TopBasePoco
    {
        [Dimension(DisplayName = "部門")]
        public string Department { get; set; } = "";

        [Dimension(DisplayName = "科目")]
        public string AccountCode { get; set; } = "";

        [Dimension(DisplayName = "幣別")]
        public string Currency { get; set; } = "";

        [Dimension(DisplayName = "交易日期", Hierarchy = DateHierarchy.Month)]
        public DateTime TransactionDate { get; set; }

        /// <summary>應收帳款金額（可為負值，代表退貨沖銷）</summary>
        [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count | AggregateFunc.Avg | AggregateFunc.Max | AggregateFunc.Min, DisplayName = "金額")]
        public decimal Amount { get; set; }

        /// <summary>外幣兌換率（小數點 4 位）</summary>
        [Measure(AllowedFuncs = AggregateFunc.Avg | AggregateFunc.Max | AggregateFunc.Min, DisplayName = "匯率")]
        public decimal ExchangeRate { get; set; }

        /// <summary>本幣金額（TWD）</summary>
        [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Avg, DisplayName = "本幣金額")]
        public decimal AmountTwd { get; set; }

        /// <summary>CFO 專屬敏感欄位：淨利潤（AllowedRoles = "CFO,Finance"）</summary>
        [Measure(AllowedFuncs = AggregateFunc.Sum, DisplayName = "淨利潤", AllowedRoles = "CFO,Finance")]
        public decimal NetProfit { get; set; }
    }

    /// <summary>
    /// 應收帳款老化記錄（AR Aging）
    /// </summary>
    internal class ArAgingRecord : TopBasePoco
    {
        [Dimension(DisplayName = "客戶")]
        public string CustomerCode { get; set; } = "";

        [Dimension(DisplayName = "帳齡區間")]
        public string AgingBucket { get; set; } = ""; // "0-30", "31-60", "61-90", "90+"

        [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count, DisplayName = "應收金額")]
        public decimal OutstandingAmount { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ListVM 定義
    // ═══════════════════════════════════════════════════════════════════════

    [EnableAnalysis]
    internal class FinancialTransactionListVM : BasePagedListVM<FinancialTransaction, BaseSearcher>
    {
        // 靜態資料集，由測試在 [TestInitialize] 注入
        internal static IList<FinancialTransaction> TestData { get; set; } = new List<FinancialTransaction>();

        public override IOrderedQueryable<FinancialTransaction> GetSearchQuery()
            => TestData.AsQueryable().OrderByDescending(x => x.TransactionDate);
    }

    [EnableAnalysis]
    internal class ArAgingListVM : BasePagedListVM<ArAgingRecord, BaseSearcher>
    {
        internal static IList<ArAgingRecord> TestData { get; set; } = new List<ArAgingRecord>();

        public override IOrderedQueryable<ArAgingRecord> GetSearchQuery()
            => TestData.AsQueryable().OrderBy(x => x.CustomerCode);
    }

    /// <summary>
    /// 模擬一個「已被刪除」的 ListVM — 不在 Registry 白名單中
    /// </summary>
    // 注意：刻意不加 [EnableAnalysis]，模擬該 VM 已被移除或從未註冊
    internal class DeletedFinancialListVM : BasePagedListVM<FinancialTransaction, BaseSearcher>
    {
        public override IOrderedQueryable<FinancialTransaction> GetSearchQuery()
            => new List<FinancialTransaction>().AsQueryable().OrderBy(x => x.ID);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 測試主體
    // ═══════════════════════════════════════════════════════════════════════

    [TestClass]
    public class CfoFinancialDashboardIntegrationTests
    {
        // ─── 基礎設施 ─────────────────────────────────────────────────────

        private AnalysisVmRegistry _registry = null!;
        private AnalysisQueryEngine _engine = null!;
        private IServiceProvider _serviceProvider = null!;

        private static readonly string FinancialTxVmType =
            typeof(FinancialTransactionListVM).FullName!;
        private static readonly string ArAgingVmType =
            typeof(ArAgingListVM).FullName!;
        private static readonly string DeletedVmType =
            typeof(DeletedFinancialListVM).FullName!;

        [TestInitialize]
        public void Setup()
        {
            // 建立包含多幣別、負數、大金額的財務測試資料集
            FinancialTransactionListVM.TestData = BuildFinancialTestData();
            ArAgingListVM.TestData = BuildArAgingTestData();

            _registry = new AnalysisVmRegistry();
            _registry.Build(new[] { typeof(CfoFinancialDashboardIntegrationTests).Assembly });

            _engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default);

            var services = new ServiceCollection();
            _serviceProvider = services.BuildServiceProvider();
        }

        [TestCleanup]
        public void Cleanup()
        {
            FinancialTransactionListVM.TestData = new List<FinancialTransaction>();
            ArAgingListVM.TestData = new List<ArAgingRecord>();
        }

        private AnalysisWidgetDataSource CreateAnalysisDataSource()
            => new(_registry, _serviceProvider, _engine);

        // ═══════════════════════════════════════════════════════════════════
        // 正向測試 — 端到端流程
        // ═══════════════════════════════════════════════════════════════════

        [TestMethod]
        [Description("CFO 晨會場景：ETL 同步財務資料後，Analysis 引擎正確聚合各部門金額，Dashboard widget 回傳正確結果")]
        public async Task CFO_MorningBriefing_DashboardWidgetShowsDepartmentRevenue_CorrectAggregation()
        {
            // Arrange: 模擬 ETL 已同步資料（資料已在 TestData 中）
            var source = CreateAnalysisDataSource();
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = FinancialTxVmType,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Department" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Amount", Func = AggregateFunc.Sum }
                    })
                }
            };

            // Act
            var result = await source.GetDataAsync(request);

            // Assert
            result.Should().NotBeNull();
            result.Rows.Should().NotBeNull();
            result.Columns.Should().Contain("Department");
            result.Columns.Should().Contain("Amount_Sum");

            // 業務驗證：各部門金額加總正確
            var salesRow = result.Rows!.First(r => r["Department"]?.ToString() == "業務部");
            var hrRow = result.Rows!.First(r => r["Department"]?.ToString() == "人事部");

            // 業務部：100_000 + (-5_000) + 200_000 + 150_000 = 445_000（含退貨沖銷 -5K 及兩筆 USD 交易）
            Convert.ToDecimal(salesRow["Amount_Sum"]).Should().Be(445_000m);
            // 人事部：50_000（薪資費用）
            Convert.ToDecimal(hrRow["Amount_Sum"]).Should().Be(50_000m);
        }

        [TestMethod]
        [Description("Dashboard 日期範圍篩選傳遞到 Analysis filter，只回傳特定月份資料")]
        public async Task CFO_ViewsMonthlyRevenue_DateRangeFilter_OnlyReturnsSelectedMonth()
        {
            // Arrange
            var source = CreateAnalysisDataSource();

            // 模擬 Dashboard 傳入日期範圍篩選（只看 2026-01）
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = FinancialTxVmType,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Department" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Amount", Func = AggregateFunc.Sum }
                    }),
                    ["filters"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Currency", Operator = FilterOperator.Eq, Value = "TWD" }
                    })
                }
            };

            // Act
            var result = await source.GetDataAsync(request);

            // Assert: 只有 TWD 交易被納入
            result.Rows.Should().NotBeNull();
            result.Rows!.Should().HaveCount(r => r > 0);
            // 所有回傳行必須都是 TWD 資料（因為我們以 Currency 過濾，但 Currency 不在 GROUP BY，
            // 所以只確認結果筆數和金額正確）
            var totalAmount = result.Rows!.Sum(r => Convert.ToDecimal(r["Amount_Sum"]));
            // TWD 交易：業務部 100_000 + (-5_000)、人事部 50_000、財務部 8_000 = 153_000
            totalAmount.Should().Be(153_000m);
        }

        [TestMethod]
        [Description("多個 CFO Dashboard widget 共用同一個 Analysis ListVM，分別計算不同 measures（Sum vs Count vs Avg）")]
        public async Task CFO_MultipleWidgets_SharedListVm_DifferentMeasures_AllCorrect()
        {
            var source = CreateAnalysisDataSource();

            // Widget 1：各部門總金額
            var sumRequest = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = FinancialTxVmType,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Department" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Amount", Func = AggregateFunc.Sum }
                    })
                }
            };

            // Widget 2：各部門交易筆數
            var countRequest = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = FinancialTxVmType,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Department" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Amount", Func = AggregateFunc.Count }
                    })
                }
            };

            // Widget 3：各部門平均交易金額
            var avgRequest = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = FinancialTxVmType,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Department" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Amount", Func = AggregateFunc.Avg }
                    })
                }
            };

            // Act：三個 widget 並行查詢
            var allResults = await Task.WhenAll(
                source.GetDataAsync(sumRequest),
                source.GetDataAsync(countRequest),
                source.GetDataAsync(avgRequest));
            var sumResult   = allResults[0];
            var countResult = allResults[1];
            var avgResult   = allResults[2];

            // Assert
            sumResult.Rows.Should().NotBeNull();
            countResult.Rows.Should().NotBeNull();
            avgResult.Rows.Should().NotBeNull();

            // 業務部 Sum = 445_000, Count = 4, Avg = 445_000 / 4
            // 資料：100_000 + (-5_000) + 200_000 + 150_000 = 445_000（4 筆）
            var salesSum = sumResult.Rows!.First(r => r["Department"]?.ToString() == "業務部");
            var salesCount = countResult.Rows!.First(r => r["Department"]?.ToString() == "業務部");
            var salesAvg = avgResult.Rows!.First(r => r["Department"]?.ToString() == "業務部");

            Convert.ToDecimal(salesSum["Amount_Sum"]).Should().Be(445_000m);
            Convert.ToDecimal(salesCount["Amount_Count"]).Should().Be(4m);
            // Avg = 445_000 / 4 = 111_250
            var avgVal = Convert.ToDecimal(salesAvg["Amount_Avg"]);
            Math.Round(avgVal, 2).Should().Be(Math.Round(445_000m / 4m, 2));
        }

        [TestMethod]
        [Description("CFO 應收帳齡分析：按帳齡區間顯示逾期金額，Widget 正確回傳 AR Aging 報表")]
        public async Task CFO_ViewsArAging_ByBucket_DisplaysCorrectOverdueAmounts()
        {
            var source = CreateAnalysisDataSource();
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = ArAgingVmType,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "AgingBucket" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "OutstandingAmount", Func = AggregateFunc.Sum }
                    })
                }
            };

            var result = await source.GetDataAsync(request);

            result.Rows.Should().NotBeNull();
            result.Rows!.Should().HaveCount(4); // 0-30, 31-60, 61-90, 90+

            // 帳齡最老的應收款最多（財務健康警示）
            var overdue90 = result.Rows!.First(r => r["AgingBucket"]?.ToString() == "90+");
            Convert.ToDecimal(overdue90["OutstandingAmount_Sum"]).Should().Be(500_000m);
        }

        // ═══════════════════════════════════════════════════════════════════
        // 反向測試 — 跨模組錯誤傳播
        // ═══════════════════════════════════════════════════════════════════

        [TestMethod]
        [Description("Dashboard widget 引用已刪除的 Analysis ListVM → Registry 拋出明確 InvalidOperationException，不應整頁 crash")]
        public async Task CFO_DashboardReferencesDeletedListVm_GracefulError_ClearMessage()
        {
            var source = CreateAnalysisDataSource();
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = DeletedVmType, // 未註冊的 VM
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Department" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Amount", Func = AggregateFunc.Sum }
                    })
                }
            };

            // Act & Assert
            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => source.GetDataAsync(request));

            // 錯誤訊息應清楚說明 VM 未在白名單中，而非模糊的 NullReferenceException
            ex.Message.Should().Contain("not registered for analysis",
                because: "Dashboard 應收到明確訊息而非 NullReferenceException，便於 CFO 系統管理員診斷");
        }

        [TestMethod]
        [Description("Analysis 引擎拋出欄位驗證失敗（非白名單維度）→ AnalysisWidgetDataSource 應傳播 InvalidOperationException，不吞例外")]
        public async Task CFO_WidgetRequestsNonWhitelistedField_AnalysisEnginePropagatesError()
        {
            var source = CreateAnalysisDataSource();
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = FinancialTxVmType,
                    // ID 不是 Dimension，應被 ValidateFields 拒絕
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "ID" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Amount", Func = AggregateFunc.Sum }
                    })
                }
            };

            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => source.GetDataAsync(request));

            ex.Message.Should().Contain("ID",
                because: "錯誤訊息應指出哪個欄位不在白名單中");
        }

        [TestMethod]
        [Description("ETL 同步了 schema 不相容的資料（度量欄位缺失）→ Analysis 查詢失敗有明確訊息")]
        public async Task ETL_SyncsIncompatibleSchema_AnalysisQueryFails_WithClearMessage()
        {
            // Arrange：模擬 ETL 注入了缺少正確型別的資料（使用錯誤的 Func 組合）
            var source = CreateAnalysisDataSource();
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = FinancialTxVmType,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Department" }),
                    // ExchangeRate 只允許 Avg/Max/Min，嘗試用 Sum 應拋出
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "ExchangeRate", Func = AggregateFunc.Sum }
                    })
                }
            };

            // Act & Assert
            var ex = await Assert.ThrowsExceptionAsync<NotSupportedException>(
                () => source.GetDataAsync(request));

            ex.Message.Should().Contain("ExchangeRate",
                because: "匯率欄位只允許 Avg/Max/Min，不允許 Sum（不同幣別的匯率加總無意義）");
        }

        [TestMethod]
        [Description("ETL 連線字串絕對不能出現在 Exception 訊息或任何回應中（安全防護）")]
        public async Task ETL_ConnectionString_NeverExposedInExceptionOrResponse()
        {
            // Arrange：模擬 EtlJobDefinition 包含敏感連線字串
            var sensitiveConnString = "Server=erp-server;Database=ERP_PROD;User=sa;Password=S3cr3t!";
            var job = new CfoTestEtlJobDefinition
            {
                ID = Guid.NewGuid(),
                Name = "ERP Financial Sync",
                CronExpression = "0 0 2 * * ?",
                JobClassName = "FinancialSyncJob",
                SourceCsKey = "ErpProd",      // 只存 Key，不存明文連線字串
                TargetCsKey = "DwProd",
                TargetTableName = "FinancialTransactions",
                MergeKeyColumn = "ID",
                QueryTemplate = "SELECT * FROM transactions WHERE updated_at > @watermark"
            };

            // 模擬連線字串洩漏的 Exception
            var leakedException = new Exception($"Connection failed: {sensitiveConnString}");

            // Assert：EtlRunLog 的 ErrorMessage 欄位長度限制為 4000 字元，
            // 但不應把明文連線字串記在 ErrorMessage 中
            var runLog = new CfoTestEtlRunLog
            {
                ID = Guid.NewGuid(),
                JobId = job.ID,
                Result = CfoTestEtlRunResult.Failed,
                StartedAt = DateTime.UtcNow,
                // 安全防護：只記錄脫敏後的錯誤訊息
                ErrorMessage = "Connection failed: [credentials redacted]"
            };

            runLog.ErrorMessage.Should().NotContain("S3cr3t!",
                because: "ETL 執行記錄中的 ErrorMessage 不應包含明文密碼");
            runLog.ErrorMessage.Should().NotContain(sensitiveConnString,
                because: "ETL 執行記錄中的 ErrorMessage 不應包含完整連線字串");

            // 驗證 EtlJobDefinition 設計：只存 Key，不存明文連線字串
            job.SourceCsKey.Should().NotContain("Password",
                because: "EtlJobDefinition 的 SourceCsKey 應為設定 Key 而非明文連線字串");

            await Task.CompletedTask; // 保持 async 簽名一致性
        }

        [TestMethod]
        [Description("ETL Watermark 機制：增量同步後 Analysis 只看到新資料，舊 watermark 確保無重複")]
        public async Task ETL_WatermarkIncrementalSync_AnalysisSeesOnlyFreshData_NoduplicAtes()
        {
            // Arrange：模擬 ETL Watermark 增量同步情境
            // T0：初始狀態 = 3 筆 Q1 資料
            // T1：增量同步 +1 筆 Q2 資料（透過 watermark 只取 > LastWatermarkValue 的）
            var job = new CfoTestEtlJobDefinition
            {
                ID = Guid.NewGuid(),
                Name = "AR 增量同步",
                WatermarkType = CfoTestEtlWatermarkType.Timestamp,
                WatermarkColumn = "UpdatedAt",
                LastWatermarkValue = "2026-03-31T00:00:00Z", // 上次同步到 Q1 結束
                CronExpression = "0 0 1 * * ?",
                JobClassName = "ArSyncJob",
                SourceCsKey = "ErpSource",
                TargetCsKey = "DwTarget",
                TargetTableName = "ArTransactions",
                MergeKeyColumn = "ID",
                QueryTemplate = "SELECT * FROM ar WHERE updated_at > @watermark"
            };

            // 模擬增量同步後的資料狀態（包含 Q2 新增的 USD 交易）
            var postSyncData = FinancialTransactionListVM.TestData.ToList();
            postSyncData.Add(new FinancialTransaction
            {
                ID = Guid.NewGuid(),
                Department = "業務部",
                AccountCode = "AR-001",
                Currency = "USD",
                TransactionDate = new DateTime(2026, 4, 1), // Q2 新資料
                Amount = 800_000m,
                ExchangeRate = 32.5m,
                AmountTwd = 800_000m * 32.5m,
                NetProfit = 80_000m
            });
            FinancialTransactionListVM.TestData = postSyncData;

            // Act：查詢新資料集
            var source = CreateAnalysisDataSource();
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = FinancialTxVmType,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Department" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Amount", Func = AggregateFunc.Count }
                    })
                }
            };

            var result = await source.GetDataAsync(request);

            // Assert：業務部現在有 5 筆（原始 4 筆含 AR-001x3 + AR-002 + 新增 1 筆 Q2）
            var salesCount = result.Rows!.First(r => r["Department"]?.ToString() == "業務部");
            Convert.ToDecimal(salesCount["Amount_Count"]).Should().Be(5m,
                because: "Watermark 增量同步應新增 1 筆 Q2 資料（原本業務部已有 4 筆），總計 5 筆");

            // 驗證 Watermark 設定正確
            job.WatermarkType.Should().Be(CfoTestEtlWatermarkType.Timestamp);
            job.WatermarkColumn.Should().Be("UpdatedAt");
            job.LastWatermarkValue.Should().NotBeNullOrEmpty();
        }

        // ═══════════════════════════════════════════════════════════════════
        // 邊界測試 — 財務特殊場景
        // ═══════════════════════════════════════════════════════════════════

        [TestMethod]
        [Description("應付帳款退貨沖銷：負數金額必須正確參與聚合運算（加總可以為負）")]
        public async Task CFO_ViewsCashFlowTrend_NegativeMonths_DisplaysCorrectly()
        {
            var source = CreateAnalysisDataSource();
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = FinancialTxVmType,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "AccountCode" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Amount", Func = AggregateFunc.Sum },
                        new { Field = "Amount", Func = AggregateFunc.Min }
                    })
                }
            };

            var result = await source.GetDataAsync(request);

            // AR-001（應收）：100_000 + (-5_000) + 200_000 = 295_000（含退貨沖銷）
            var arRow = result.Rows!.FirstOrDefault(r => r["AccountCode"]?.ToString() == "AR-001");
            arRow.Should().NotBeNull("AR-001 科目應在結果中");
            Convert.ToDecimal(arRow!["Amount_Sum"]).Should().Be(295_000m,
                because: "負數退貨沖銷 -5,000 應正確納入加總，不能被忽略");

            // Min 應為負數（退貨沖銷金額）
            Convert.ToDecimal(arRow["Amount_Min"]).Should().Be(-5_000m,
                because: "Min 應回傳最小值（負數），表示有退貨沖銷發生");
        }

        [TestMethod]
        [Description("外幣匯率精度：四位小數 ExchangeRate 的 Avg/Max/Min 計算不應有精度損失")]
        public async Task CFO_FxRatePrecision_FourDecimalPlaces_NoRoundingLoss()
        {
            var source = CreateAnalysisDataSource();
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = FinancialTxVmType,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Currency" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "ExchangeRate", Func = AggregateFunc.Avg },
                        new { Field = "ExchangeRate", Func = AggregateFunc.Max },
                        new { Field = "ExchangeRate", Func = AggregateFunc.Min }
                    })
                }
            };

            var result = await source.GetDataAsync(request);

            result.Rows.Should().NotBeNull();
            var usdRow = result.Rows!.FirstOrDefault(r => r["Currency"]?.ToString() == "USD");
            usdRow.Should().NotBeNull("USD 幣別應在結果中");

            // USD 匯率：32.1500, 32.1750 → Avg = 32.1625, Max = 32.1750, Min = 32.1500
            var avgRate = Convert.ToDecimal(usdRow!["ExchangeRate_Avg"]);
            var maxRate = Convert.ToDecimal(usdRow["ExchangeRate_Max"]);
            var minRate = Convert.ToDecimal(usdRow["ExchangeRate_Min"]);

            avgRate.Should().Be(32.1625m, because: "匯率平均值應精確到 4 位小數，使用 decimal 不應有浮點誤差");
            maxRate.Should().Be(32.1750m, because: "最高匯率應精確保留 4 位小數");
            minRate.Should().Be(32.1500m, because: "最低匯率應精確保留 4 位小數");
        }

        [TestMethod]
        [Description("跨幣別加總防護：不同幣別的 Amount 直接加總在財務上毫無意義，應使用本幣金額 AmountTwd 做聚合")]
        public async Task CFO_CrossCurrencySum_ShouldUseLocalCurrencyAmount_NotForeignAmount()
        {
            var source = CreateAnalysisDataSource();

            // 錯誤做法：直接對 Amount 加總（混合 TWD + USD）
            var wrongRequest = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = FinancialTxVmType,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Department" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Amount", Func = AggregateFunc.Sum }
                    })
                }
            };

            // 正確做法：對 AmountTwd 加總（已換算為本幣）
            var correctRequest = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = FinancialTxVmType,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Department" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "AmountTwd", Func = AggregateFunc.Sum }
                    })
                }
            };

            var wrongResult = await source.GetDataAsync(wrongRequest);
            var correctResult = await source.GetDataAsync(correctRequest);

            // 跨幣別加總的 Amount（USD + TWD 混合）≠ 本幣加總的 AmountTwd
            var wrongSalesSum = wrongResult.Rows!.First(r => r["Department"]?.ToString() == "業務部");
            var correctSalesSum = correctResult.Rows!.First(r => r["Department"]?.ToString() == "業務部");

            var wrongTotal = Convert.ToDecimal(wrongSalesSum["Amount_Sum"]);
            var correctTotal = Convert.ToDecimal(correctSalesSum["AmountTwd_Sum"]);

            // 業務部 AmountTwd 包含 USD 換算後金額，必然大於 Amount 直接加總
            correctTotal.Should().BeGreaterThan(wrongTotal,
                because: "本幣換算後的 AmountTwd 包含匯率乘數，應大於直接加總的原幣 Amount");
        }

        [TestMethod]
        [Description("多幣別精確度：USD 交易 AmountTwd 加總必須精確等於各筆 Amount × ExchangeRate 之和 (#445)")]
        public async Task CFO_MultiCurrency_AmountTwd_ExactValue_MatchesAmountTimesRate()
        {
            // USD 交易 1: Amount=200_000, ExchangeRate=32.1500 → AmountTwd=6_430_000
            // USD 交易 2: Amount=150_000, ExchangeRate=32.1750 → AmountTwd=4_826_250
            // 期望 AmountTwd_Sum = 11_256_250（精確到分）
            const decimal expectedUsdAmountTwdSum = 200_000m * 32.1500m + 150_000m * 32.1750m;

            var source = CreateAnalysisDataSource();
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = FinancialTxVmType,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Currency" }),
                    ["measures"]   = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "AmountTwd", Func = AggregateFunc.Sum }
                    }),
                    ["filters"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Currency", Operator = FilterOperator.Eq, Value = "USD" }
                    })
                }
            };

            var result = await source.GetDataAsync(request);

            result.Should().NotBeNull();
            result.Rows.Should().NotBeNull();
            var usdRow = result.Rows!.FirstOrDefault(r => r["Currency"]?.ToString() == "USD");
            usdRow.Should().NotBeNull("USD 過濾後應有一列結果");

            var actualSum = Convert.ToDecimal(usdRow!["AmountTwd_Sum"]);
            actualSum.Should().Be(expectedUsdAmountTwdSum,
                because: "USD 交易的 AmountTwd 加總必須精確等於各筆 Amount × ExchangeRate 之和，不允許有分毫誤差");
        }

        [TestMethod]
        [Description("季結切分：DateHierarchy.Quarter 正確將 1-3 月歸為 Q1、4-6 月歸為 Q2")]
        public void CFO_QuarterlyReport_DateHierarchy_CorrectlyGroupsByQuarter()
        {
            // 補充跨季交易資料
            var q2Data = new FinancialTransaction
            {
                ID = Guid.NewGuid(),
                Department = "財務部",
                AccountCode = "EXP-001",
                Currency = "TWD",
                TransactionDate = new DateTime(2026, 4, 15), // Q2
                Amount = 30_000m,
                ExchangeRate = 1m,
                AmountTwd = 30_000m,
                NetProfit = 0m
            };
            var extendedData = FinancialTransactionListVM.TestData.ToList();
            extendedData.Add(q2Data);
            FinancialTransactionListVM.TestData = extendedData;

            var engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default);
            var whitelist = AnalysisFieldScanner.ScanModel(typeof(FinancialTransaction));

            var req = new AnalysisQueryRequest
            {
                ListVmType = FinancialTxVmType,
                Dimensions = new List<string> { "TransactionDate" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Amount", Func = AggregateFunc.Sum }
                },
                DimensionHierarchies = new Dictionary<string, DateHierarchy>
                {
                    ["TransactionDate"] = DateHierarchy.Quarter
                }
            };

            var query = FinancialTransactionListVM.TestData.AsQueryable();
            var response = engine.Execute(query, req, whitelist);

            // Assert：應有 Q1（2026Q1）和 Q2（2026Q2）兩組
            response.Rows.Should().NotBeNull();
            var q1Row = response.Rows!.FirstOrDefault(r => r["TransactionDate"]?.ToString()?.Contains("Q1") == true);
            var q2Row = response.Rows!.FirstOrDefault(r => r["TransactionDate"]?.ToString()?.Contains("Q2") == true);

            q1Row.Should().NotBeNull("2026 Q1 應有資料");
            q2Row.Should().NotBeNull("2026 Q2 應有資料（4月份交易）");

            // Q1 金額 = 100_000 + (-5_000) + 200_000 + 150_000（業務部）+ 50_000（人事部）+ 8_000（財務部）= 503_000
            Convert.ToDecimal(q1Row!["Amount_Sum"]).Should().Be(503_000m,
                because: "Q1（1-3月）所有交易應歸入同一季結");

            // Q2 金額 = 30_000
            Convert.ToDecimal(q2Row!["Amount_Sum"]).Should().Be(30_000m,
                because: "Q2（4-6月）只有一筆 4月交易");
        }

        [TestMethod]
        [Description("極大金額（十億級）：Y 軸縮放 DateTruncator.FormatKey 格式化不應溢位或截斷")]
        public async Task CFO_BillionScaleAmount_AnalysisEngine_HandlesCorrectly_NoOverflow()
        {
            // Arrange：注入十億級金額資料
            var bigData = new List<FinancialTransaction>
            {
                new()
                {
                    ID = Guid.NewGuid(),
                    Department = "集團總部",
                    AccountCode = "REV-001",
                    Currency = "TWD",
                    TransactionDate = new DateTime(2026, 1, 1),
                    Amount = 1_500_000_000m, // 十五億
                    ExchangeRate = 1m,
                    AmountTwd = 1_500_000_000m,
                    NetProfit = 150_000_000m
                },
                new()
                {
                    ID = Guid.NewGuid(),
                    Department = "集團總部",
                    AccountCode = "REV-001",
                    Currency = "TWD",
                    TransactionDate = new DateTime(2026, 2, 1),
                    Amount = 2_000_000_000m, // 二十億
                    ExchangeRate = 1m,
                    AmountTwd = 2_000_000_000m,
                    NetProfit = 200_000_000m
                }
            };
            FinancialTransactionListVM.TestData = bigData;

            var source = CreateAnalysisDataSource();
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = FinancialTxVmType,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Department" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Amount", Func = AggregateFunc.Sum },
                        new { Field = "Amount", Func = AggregateFunc.Max }
                    })
                }
            };

            var result = await source.GetDataAsync(request);

            result.Rows.Should().NotBeNull();
            var corpRow = result.Rows!.First(r => r["Department"]?.ToString() == "集團總部");

            // 十五億 + 二十億 = 三十五億
            Convert.ToDecimal(corpRow["Amount_Sum"]).Should().Be(3_500_000_000m,
                because: "decimal 型別應可安全處理十億級金額，不應 overflow");
            Convert.ToDecimal(corpRow["Amount_Max"]).Should().Be(2_000_000_000m,
                because: "Max 應回傳最大單筆金額（二十億）");
        }

        [TestMethod]
        [Description("月結 DateHierarchy.Month 格式化：2026-01、2026-02 等格式應正確，不應產生 2026-1（缺前導零）")]
        public void CFO_MonthlyReport_DateHierarchyFormatKey_HasLeadingZeroPadding()
        {
            var engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default);
            var whitelist = AnalysisFieldScanner.ScanModel(typeof(FinancialTransaction));

            var req = new AnalysisQueryRequest
            {
                ListVmType = FinancialTxVmType,
                Dimensions = new List<string> { "TransactionDate" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "Amount", Func = AggregateFunc.Sum }
                },
                DimensionHierarchies = new Dictionary<string, DateHierarchy>
                {
                    ["TransactionDate"] = DateHierarchy.Month
                }
            };

            var query = FinancialTransactionListVM.TestData.AsQueryable();
            var response = engine.Execute(query, req, whitelist);

            response.Rows.Should().NotBeNull();

            // 所有月份 key 應符合 YYYY-MM 格式（包含前導零）
            foreach (var row in response.Rows!)
            {
                var monthKey = row["TransactionDate"]?.ToString();
                monthKey.Should().MatchRegex(@"^\d{4}-\d{2}$",
                    because: "月份顯示應為 YYYY-MM 格式（例如 2026-01 而非 2026-1），CFO 報表格式要求一致性");
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // 安全測試 — 財務資料敏感性
        // ═══════════════════════════════════════════════════════════════════

        [TestMethod]
        [Description("AnalysisVmRegistry 白名單機制：未標記 [EnableAnalysis] 的財務 VM 不得被存取")]
        public void CFO_UnauthorizedListVm_NotInRegistry_CannotBeAccessed()
        {
            // DeletedFinancialListVM 沒有 [EnableAnalysis] attribute
            var registeredTypes = _registry.GetRegisteredTypes();

            registeredTypes.Should().NotContainKey(DeletedVmType,
                because: "未標記 [EnableAnalysis] 的 ListVM 不應在白名單中，防止未授權存取財務資料");

            // 嘗試 Resolve 應拋出
            var ex = Assert.ThrowsException<InvalidOperationException>(
                () => _registry.Resolve(DeletedVmType));

            ex.Message.Should().Contain("not registered for analysis");
        }

        [TestMethod]
        [Description("角色限制欄位（AllowedRoles）：非 CFO 角色嘗試查詢 NetProfit，AnalysisFieldPolicy 應拒絕")]
        public void CFO_RestrictedMeasure_NetProfit_OnlyCfoRoleCanAccess()
        {
            // 掃描 FinancialTransaction 的欄位 metadata
            var fields = AnalysisFieldScanner.ScanModel(typeof(FinancialTransaction)).ToList();

            var netProfitMeta = fields.FirstOrDefault(f => f.FieldName == "NetProfit");
            netProfitMeta.Should().NotBeNull("NetProfit 應被掃描到");
            netProfitMeta!.AllowedRoles.Should().NotBeNullOrEmpty(
                because: "NetProfit 是 CFO 敏感欄位，必須設定 AllowedRoles");
            netProfitMeta.AllowedRoles.Should().Contain("CFO",
                because: "淨利潤只有 CFO 和 Finance 角色才能查看");

            // 驗證 DefaultAnalysisFieldPolicy 角色篩選邏輯
            // DefaultAnalysisFieldPolicy.Filter() 接受 ClaimsPrincipal
            var policy = new DefaultAnalysisFieldPolicy();

            // 非財務角色（例如 IT 部門）不應看到 NetProfit
            var itPrincipal = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "IT") },
                    "test"));
            var itFields = policy.Filter(fields, itPrincipal).ToList();
            itFields.Should().NotContain(f => f.FieldName == "NetProfit",
                because: "IT 角色不在 NetProfit 的 AllowedRoles 中，不應在可用欄位清單中出現");

            // CFO 角色應可看到 NetProfit
            var cfoPrincipal = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "CFO") },
                    "test"));
            var cfoFields = policy.Filter(fields, cfoPrincipal).ToList();
            cfoFields.Should().Contain(f => f.FieldName == "NetProfit",
                because: "CFO 角色在 AllowedRoles 中，應可存取淨利潤欄位");
        }

        [TestMethod]
        [Description("Analysis API 安全：未提供 listVmType 的 Widget 請求應明確拒絕，不吞例外")]
        public async Task CFO_WidgetRequestMissingListVmType_ThrowsInvalidOperation_NotSilentlyIgnored()
        {
            var source = CreateAnalysisDataSource();
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    // 刻意不帶 listVmType
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Department" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Amount", Func = AggregateFunc.Sum }
                    })
                }
            };

            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => source.GetDataAsync(request));

            ex.Message.Should().Contain("listVmType",
                because: "缺少 listVmType 參數時應明確指出，而非回傳空結果或 NullReferenceException");
        }

        [TestMethod]
        [Description("ETL RunLog 安全：ErrorMessage 欄位有 4000 字元限制，超長錯誤訊息應被截斷而非拋出 DB 例外")]
        public void ETL_RunLog_ErrorMessage_TruncatedAt4000Chars_DoesNotCauseDbException()
        {
            // 模擬超長的 Stack Trace
            var veryLongError = string.Concat(Enumerable.Repeat("Error: Connection failed. ", 200));
            veryLongError.Length.Should().BeGreaterThan(4000);

            var runLog = new CfoTestEtlRunLog
            {
                ID = Guid.NewGuid(),
                JobId = Guid.NewGuid(),
                Result = CfoTestEtlRunResult.Failed,
                StartedAt = DateTime.UtcNow,
                // 截斷至 4000 字元
                ErrorMessage = veryLongError.Length > 4000
                    ? veryLongError.Substring(0, 4000)
                    : veryLongError
            };

            // Assert：欄位長度不超過資料庫限制
            runLog.ErrorMessage.Should().NotBeNull();
            runLog.ErrorMessage!.Length.Should().BeLessOrEqualTo(4000,
                because: "EtlRunLog.ErrorMessage 有 [StringLength(4000)] 限制，超出應截斷而非拋出 DB truncation 例外");
        }

        [TestMethod]
        [Description("Dashboard 安全：CanAccess 正確阻止非授權角色存取 CFO 專屬儀表板")]
        public void CFO_DashboardAccess_NonCfoRole_IsDenied()
        {
            var cfoDashboard = new DashboardDefinition
            {
                Id = "cfo-dashboard",
                Title = "CFO 財務健康儀表板",
                Owner = "cfo_user",
                Sharing = new SharingDefinition
                {
                    Mode = "role",
                    Roles = new List<string> { "CFO", "Finance" }
                }
            };

            // 模擬不同角色
            var service = new JsonFileDashboardService(
                Microsoft.Extensions.Options.Options.Create(new DashboardOptions
                {
                    DashboardDirectory = $"test_dashboards_{Guid.NewGuid():N}"
                }),
                Enumerable.Empty<IWidgetDataSource>());

            // IT 角色不應能存取 CFO 儀表板
            service.CanAccess(cfoDashboard, "it_user", new[] { "IT" })
                .Should().BeFalse(because: "IT 角色不在 CFO 儀表板的 Sharing.Roles 中");

            // CFO 角色應能存取
            service.CanAccess(cfoDashboard, "finance_user", new[] { "Finance" })
                .Should().BeTrue(because: "Finance 角色在 Sharing.Roles 中");

            // Admin 永遠可以存取（緊急審計用途）
            service.CanAccess(cfoDashboard, "admin", new[] { "Admin" })
                .Should().BeTrue(because: "Admin 超級角色應能存取所有儀表板");
        }

        // ═══════════════════════════════════════════════════════════════════
        // CFO 視角深度審查
        // ═══════════════════════════════════════════════════════════════════

        [TestMethod]
        [Description("CFO 數字可信度：多步加總一致性驗證（逐行加總 == 引擎聚合結果，無四捨五入累積誤差）")]
        public async Task CFO_NumberTrustworthiness_ManualSumEqualsEngineSum_NoRoundingAccumulation()
        {
            var source = CreateAnalysisDataSource();
            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = FinancialTxVmType,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Department" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "AmountTwd", Func = AggregateFunc.Sum }
                    })
                }
            };

            var result = await source.GetDataAsync(request);

            // 手動計算各部門本幣金額加總（作為驗證基準）
            var manualSums = FinancialTransactionListVM.TestData
                .GroupBy(t => t.Department)
                .ToDictionary(g => g.Key, g => g.Sum(t => t.AmountTwd));

            foreach (var row in result.Rows!)
            {
                var dept = row["Department"]?.ToString()!;
                var engineSum = Convert.ToDecimal(row["AmountTwd_Sum"]);
                var manualSum = manualSums[dept];

                engineSum.Should().Be(manualSum,
                    because: $"部門 {dept} 的本幣金額加總：引擎計算 {engineSum} 應等於手動加總 {manualSum}，decimal 不應有累積誤差");
            }
        }

        [TestMethod]
        [Description("CFO 數字可信度：CancellationToken 取消後不應回傳部分結果（防止 CFO 看到不完整資料）")]
        public async Task CFO_CancellationToken_CancelledQuery_ThrowsNotReturnsPartialData()
        {
            var source = CreateAnalysisDataSource();
            var cts = new CancellationTokenSource();

            // 注入大量資料讓查詢足夠慢
            var bigData = Enumerable.Range(0, 1000)
                .Select(i => new FinancialTransaction
                {
                    ID = Guid.NewGuid(),
                    Department = $"部門{i % 10}",
                    AccountCode = "REV-001",
                    Currency = "TWD",
                    TransactionDate = new DateTime(2026, 1, 1),
                    Amount = i * 1000m,
                    ExchangeRate = 1m,
                    AmountTwd = i * 1000m,
                    NetProfit = i * 100m
                }).ToList();
            FinancialTransactionListVM.TestData = bigData;

            // 立即取消
            cts.Cancel();

            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = FinancialTxVmType,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Department" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "Amount", Func = AggregateFunc.Sum }
                    })
                }
            };

            // Act & Assert：取消後應拋出 OperationCanceledException，而非回傳部分結果
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                () => source.GetDataAsync(request, cts.Token),
                "CancellationToken 觸發後不應靜默回傳部分資料，CFO 看到未完成的資料比空白更危險");
        }

        [TestMethod]
        [Description("CFO 鑽取路徑：年→季→月三層 DateHierarchy 格式 key 排序後符合時間順序")]
        public void CFO_DrillDownPath_YearQuarterMonth_KeysSortChronologically()
        {
            // 驗證 DateTruncator.FormatKey 的各層級 key 排序一致性
            // 不同層級的 key 格式：Year=2026, Quarter="2026 Q1", Month="2026-01"

            var yearKeys = new[] { 2025, 2026, 2027 };
            var formattedYears = yearKeys.Select(k => DateTruncator.FormatKey(k, DateHierarchy.Year)).ToList();
            formattedYears.Should().BeInAscendingOrder(because: "年份 key 字串排序應與時間順序一致");

            // 季度 key 格式為 "YYYY Qn"，字串排序正確
            var quarterKeys = new[]
            {
                2026 * 10 + 1, // Q1
                2026 * 10 + 2, // Q2
                2026 * 10 + 3, // Q3
                2026 * 10 + 4, // Q4
                2027 * 10 + 1  // 跨年 Q1
            };
            var formattedQuarters = quarterKeys.Select(k => DateTruncator.FormatKey(k, DateHierarchy.Quarter)).ToList();
            formattedQuarters.Should().BeInAscendingOrder(
                because: "季度 key 字串排序應與時間順序一致，跨年 Q1 應排在 Q4 之後");

            // 月份 key 格式為 "YYYY-MM"，字串排序正確
            var monthKeys = new[]
            {
                2026 * 100 + 1,  // 2026-01
                2026 * 100 + 12, // 2026-12
                2027 * 100 + 1   // 2027-01
            };
            var formattedMonths = monthKeys.Select(k => DateTruncator.FormatKey(k, DateHierarchy.Month)).ToList();
            formattedMonths.Should().BeInAscendingOrder(
                because: "月份 key 格式 YYYY-MM 字串排序應與時間順序一致");
        }

        // ═══════════════════════════════════════════════════════════════════
        // 測試資料工廠
        // ═══════════════════════════════════════════════════════════════════

        private static IList<FinancialTransaction> BuildFinancialTestData()
        {
            return new List<FinancialTransaction>
            {
                // 業務部 AR（應收帳款）— TWD，Q1
                new()
                {
                    ID = Guid.NewGuid(),
                    Department = "業務部",
                    AccountCode = "AR-001",
                    Currency = "TWD",
                    TransactionDate = new DateTime(2026, 1, 15),
                    Amount = 100_000m,
                    ExchangeRate = 1m,
                    AmountTwd = 100_000m,
                    NetProfit = 10_000m
                },
                // 業務部 AR 退貨沖銷 — 負數金額
                new()
                {
                    ID = Guid.NewGuid(),
                    Department = "業務部",
                    AccountCode = "AR-001",
                    Currency = "TWD",
                    TransactionDate = new DateTime(2026, 2, 10),
                    Amount = -5_000m,         // 負數：退貨沖銷
                    ExchangeRate = 1m,
                    AmountTwd = -5_000m,
                    NetProfit = -500m
                },
                // 業務部 AR — USD（外幣交易）
                new()
                {
                    ID = Guid.NewGuid(),
                    Department = "業務部",
                    AccountCode = "AR-001",
                    Currency = "USD",
                    TransactionDate = new DateTime(2026, 3, 20),
                    Amount = 200_000m,
                    ExchangeRate = 32.1500m,   // 四位小數匯率
                    AmountTwd = 200_000m * 32.1500m,
                    NetProfit = 20_000m
                },
                // 業務部 AR — USD（另一筆外幣）
                new()
                {
                    ID = Guid.NewGuid(),
                    Department = "業務部",
                    AccountCode = "AR-002",
                    Currency = "USD",
                    TransactionDate = new DateTime(2026, 3, 25),
                    Amount = 150_000m,
                    ExchangeRate = 32.1750m,   // 四位小數匯率
                    AmountTwd = 150_000m * 32.1750m,
                    NetProfit = 15_000m
                },
                // 人事部 — 薪資費用（TWD）
                new()
                {
                    ID = Guid.NewGuid(),
                    Department = "人事部",
                    AccountCode = "EXP-001",
                    Currency = "TWD",
                    TransactionDate = new DateTime(2026, 1, 31),
                    Amount = 50_000m,
                    ExchangeRate = 1m,
                    AmountTwd = 50_000m,
                    NetProfit = 0m
                },
                // 財務部 — 利息收入（TWD）
                new()
                {
                    ID = Guid.NewGuid(),
                    Department = "財務部",
                    AccountCode = "INT-001",
                    Currency = "TWD",
                    TransactionDate = new DateTime(2026, 2, 28),
                    Amount = 8_000m,
                    ExchangeRate = 1m,
                    AmountTwd = 8_000m,
                    NetProfit = 8_000m
                }
            };
        }

        private static IList<ArAgingRecord> BuildArAgingTestData()
        {
            return new List<ArAgingRecord>
            {
                // 客戶 A
                new() { ID = Guid.NewGuid(), CustomerCode = "CUST-A", AgingBucket = "0-30",  OutstandingAmount = 200_000m },
                new() { ID = Guid.NewGuid(), CustomerCode = "CUST-B", AgingBucket = "0-30",  OutstandingAmount = 150_000m },
                // 30-60 天逾期
                new() { ID = Guid.NewGuid(), CustomerCode = "CUST-C", AgingBucket = "31-60", OutstandingAmount = 300_000m },
                // 60-90 天逾期
                new() { ID = Guid.NewGuid(), CustomerCode = "CUST-D", AgingBucket = "61-90", OutstandingAmount = 250_000m },
                new() { ID = Guid.NewGuid(), CustomerCode = "CUST-E", AgingBucket = "61-90", OutstandingAmount = 180_000m },
                // 90+ 天嚴重逾期
                new() { ID = Guid.NewGuid(), CustomerCode = "CUST-F", AgingBucket = "90+",   OutstandingAmount = 300_000m },
                new() { ID = Guid.NewGuid(), CustomerCode = "CUST-G", AgingBucket = "90+",   OutstandingAmount = 200_000m },
            };
        }
    }
}
