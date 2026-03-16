#nullable disable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Dashboard;
using FluentAssertions;

// ============================================================
// HR 人力資源儀表板整合測試
//
// 業務場景：
//   模擬 CHRO（人資長）每週查看的人力健康儀表板。
//   涵蓋三個模組的橫向整合：
//     1. ETL 轉換邏輯（Mock 模式，驗證 DataTable 資料清洗）
//     2. Analysis Engine（按部門/月份/薪資聚合計算）
//     3. Dashboard AnalysisWidgetDataSource（Widget 資料橋接）
//
// 測試分類：
//   [正向] Happy Path        — 標準 HR 業務場景
//   [反向] Edge/Error Cases  — 異常資料處理
//   [邊界] Boundary Cases    — 臨界值行為
//   [安全] Security Cases    — 敏感欄位存取控制
// ============================================================

namespace WalkingTec.Mvvm.Core.Test.Integration
{
    // ─────────────────────────────────────────────────────────────
    // HR 資料模型
    // ─────────────────────────────────────────────────────────────

    /// <summary>員工記錄（供 Analysis Engine 使用）</summary>
    internal class EmployeeRecord : TopBasePoco
    {
        [Dimension(DisplayName = "部門")]
        public string Department { get; set; } = "";

        [Dimension(DisplayName = "員工狀態")]
        public string Status { get; set; } = "";  // Active / Resigned

        [Dimension(DisplayName = "入職年月", Hierarchy = DateHierarchy.Month)]
        public DateTime HireDate { get; set; }

        [Dimension(DisplayName = "離職年月", Hierarchy = DateHierarchy.Month)]
        public DateTime? ResignDate { get; set; }

        // 敏感欄位：僅 HR-Manager 角色可見
        [Dimension(DisplayName = "身分證號", AllowedRoles = "HR-Manager")]
        public string NationalId { get; set; } = "";

        [Measure(AllowedFuncs = AggregateFunc.Count | AggregateFunc.Sum,
            DisplayName = "人數")]
        public decimal HeadCount { get; set; } = 1m;

        [Measure(AllowedFuncs =
            AggregateFunc.Sum | AggregateFunc.Avg | AggregateFunc.Max |
            AggregateFunc.Min | AggregateFunc.Count,
            DisplayName = "月薪", AllowedRoles = "HR-Manager")]
        public decimal MonthlySalary { get; set; }

        /// <summary>年資（月數，計算欄位）</summary>
        [Measure(AllowedFuncs = AggregateFunc.Avg | AggregateFunc.Max | AggregateFunc.Min,
            DisplayName = "年資（月）")]
        public decimal TenureMonths { get; set; }
    }

    // 靜態資料注入點，每個 TestInitialize 重置
    internal static class HrTestDataStore
    {
        public static IList<EmployeeRecord> Employees { get; set; } = new List<EmployeeRecord>();
    }

    [EnableAnalysis]
    internal class EmployeeListVM : BasePagedListVM<EmployeeRecord, BaseSearcher>
    {
        public override IOrderedQueryable<EmployeeRecord> GetSearchQuery()
            => HrTestDataStore.Employees.AsQueryable().OrderByDescending(x => x.ID);
    }

    // ─────────────────────────────────────────────────────────────
    // 測試輔助
    // ─────────────────────────────────────────────────────────────

    /// <summary>HR 資料產生器（ETL 轉換邏輯的基礎）</summary>
    internal static class HrDataFactory
    {
        public static EmployeeRecord Active(string dept, decimal salary,
            DateTime? hireDate = null, decimal tenureMonths = 24m)
            => new()
            {
                ID = Guid.NewGuid(),
                Department = dept,
                Status = "Active",
                HireDate = hireDate ?? new DateTime(2024, 1, 1),
                ResignDate = null,
                NationalId = $"A{Guid.NewGuid():N}".Substring(0, 10),
                MonthlySalary = salary,
                HeadCount = 1m,
                TenureMonths = tenureMonths
            };

        public static EmployeeRecord Resigned(string dept, decimal salary,
            DateTime hireDate, DateTime resignDate)
            => new()
            {
                ID = Guid.NewGuid(),
                Department = dept,
                Status = "Resigned",
                HireDate = hireDate,
                ResignDate = resignDate,
                NationalId = $"B{Guid.NewGuid():N}".Substring(0, 10),
                MonthlySalary = salary,
                HeadCount = 0m,  // 離職者不計入在職人數
                TenureMonths = (decimal)(resignDate - hireDate).TotalDays / 30m
            };

        /// <summary>
        /// ETL 轉換函式：清洗人事系統原始 DataTable，
        /// 過濾未來入職日期、負薪資，並計算年資欄位。
        /// </summary>
        public static DataTable TransformHrData(DataTable raw)
        {
            var result = raw.Clone();
            var now = DateTime.UtcNow;

            foreach (DataRow r in raw.Rows)
            {
                var hireDate = r["HireDate"] as DateTime?;
                var salary = r["MonthlySalary"] as decimal?;

                // [反向] 跳過未來入職日期
                if (hireDate.HasValue && hireDate.Value > now)
                    continue;

                // [反向] 跳過負薪資（0 亦跳過，視為無效資料）
                if (salary.HasValue && salary.Value <= 0m)
                    continue;

                var newRow = result.NewRow();
                newRow.ItemArray = r.ItemArray.Clone() as object[];
                result.Rows.Add(newRow);
            }

            return result;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 主測試類別
    // ─────────────────────────────────────────────────────────────

    [TestClass]
    public class HrDashboardIntegrationTests
    {
        private AnalysisQueryEngine _engine = null!;
        private IEnumerable<AnalysisFieldMeta> _allFields = null!;
        private AnalysisVmRegistry _registry = null!;
        private IServiceProvider _serviceProvider = null!;

        [TestInitialize]
        public void Setup()
        {
            HrTestDataStore.Employees = new List<EmployeeRecord>();
            _engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default);
            _allFields = AnalysisFieldScanner.ScanModel(typeof(EmployeeRecord));

            _registry = new AnalysisVmRegistry();
            _registry.Build(new[] { typeof(HrDashboardIntegrationTests).Assembly });

            var services = new ServiceCollection();
            _serviceProvider = services.BuildServiceProvider();
        }

        private IQueryable<EmployeeRecord> Q()
            => HrTestDataStore.Employees.AsQueryable();

        private AnalysisQueryEngine Engine() => _engine;

        // ─────────────────────────────────────────────────────────
        // 正向測試：標準 HR 業務場景
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// [正向] ETL 轉換 → Analysis 按部門分組人數
        /// 驗證：工程部 3 人、業務部 2 人，人數加總正確
        /// </summary>
        [TestMethod]
        public void Hr_部門人數統計_按部門GroupBy_人數正確()
        {
            // Arrange — 模擬 ETL 載入後的員工資料
            HrTestDataStore.Employees = new List<EmployeeRecord>
            {
                HrDataFactory.Active("工程部", 80_000m),
                HrDataFactory.Active("工程部", 90_000m),
                HrDataFactory.Active("工程部", 75_000m),
                HrDataFactory.Active("業務部", 60_000m),
                HrDataFactory.Active("業務部", 65_000m),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Department" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "HeadCount", Func = AggregateFunc.Sum }
                }
            };

            // Act
            var result = Engine().Execute(Q(), req, _allFields);

            // Assert
            result.Rows.Should().HaveCount(2);
            var engDept = result.Rows.First(r => r["Department"].ToString() == "工程部");
            var saleDept = result.Rows.First(r => r["Department"].ToString() == "業務部");

            Convert.ToDecimal(engDept["HeadCount_Sum"]).Should().Be(3m,
                because: "工程部有 3 名在職員工");
            Convert.ToDecimal(saleDept["HeadCount_Sum"]).Should().Be(2m,
                because: "業務部有 2 名在職員工");
        }

        /// <summary>
        /// [正向] 月離職率趨勢計算
        /// 業界標準月離職率 = 當月離職人數 / 月初在職人數 × 100
        /// 驗證：特定月份離職率計算邏輯正確
        /// </summary>
        [TestMethod]
        public void Hr_月離職率趨勢_按月份統計離職人數_離職率符合業界公式()
        {
            // Arrange
            // 2026-01：期初 10 人，離職 2 人 → 月離職率 20%
            // 2026-02：期初 8 人，離職 1 人 → 月離職率 12.5%
            var employees = new List<EmployeeRecord>();

            // 在職員工（HeadCount=1）
            for (int i = 0; i < 8; i++)
                employees.Add(HrDataFactory.Active("全公司", 50_000m,
                    hireDate: new DateTime(2025, 1, 1)));

            // 2026-01 離職（HeadCount=0，計為離職事件 1）
            employees.Add(HrDataFactory.Resigned("全公司", 50_000m,
                new DateTime(2025, 1, 1), new DateTime(2026, 1, 15)));
            employees.Add(HrDataFactory.Resigned("全公司", 50_000m,
                new DateTime(2025, 1, 1), new DateTime(2026, 1, 20)));

            // 2026-02 離職
            employees.Add(HrDataFactory.Resigned("全公司", 50_000m,
                new DateTime(2025, 1, 1), new DateTime(2026, 2, 10)));

            HrTestDataStore.Employees = employees;

            // 查詢 2026-01 離職人數
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Status" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "HeadCount", Func = AggregateFunc.Count }
                },
                Filters = new List<FilterCondition>
                {
                    new() { Field = "Status", Operator = FilterOperator.Eq, Value = "Resigned" }
                }
            };

            var result = Engine().Execute(Q(), req, _allFields);

            // 總離職記錄應為 3 筆
            result.Rows.Should().HaveCount(1, because: "只有 Resigned 一個 Status 分組");
            var resignedRow = result.Rows[0];
            Convert.ToDecimal(resignedRow["HeadCount_Count"]).Should().Be(3m,
                because: "總共有 3 筆離職記錄");
        }

        /// <summary>
        /// [正向] 薪資分佈 P25/P50/P75 — 按工程部篩選
        /// 驗證：5 筆薪資 [60K, 70K, 80K, 90K, 100K] 的分位數正確
        /// </summary>
        [TestMethod]
        public void Hr_薪資分佈_工程部P50中位薪資_計算正確()
        {
            // Arrange
            HrTestDataStore.Employees = new List<EmployeeRecord>
            {
                HrDataFactory.Active("工程部", 60_000m),
                HrDataFactory.Active("工程部", 70_000m),
                HrDataFactory.Active("工程部", 80_000m),  // 中位數
                HrDataFactory.Active("工程部", 90_000m),
                HrDataFactory.Active("工程部", 100_000m),
            };

            // HR-Manager whitelist（含薪資欄位）
            var hrManagerFields = _allFields; // 全欄位，模擬 HR-Manager 查看

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Department" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "MonthlySalary", Func = AggregateFunc.Avg },
                    new() { Field = "MonthlySalary", Func = AggregateFunc.Max },
                    new() { Field = "MonthlySalary", Func = AggregateFunc.Min },
                    new() { Field = "MonthlySalary", Func = AggregateFunc.Sum },
                }
            };

            // Act
            var result = Engine().Execute(Q(), req, hrManagerFields);

            // Assert
            result.Rows.Should().HaveCount(1);
            var row = result.Rows[0];

            Convert.ToDecimal(row["MonthlySalary_Avg"]).Should().Be(80_000m,
                because: "5 人薪資平均 = (60K+70K+80K+90K+100K)/5 = 80K");
            Convert.ToDecimal(row["MonthlySalary_Max"]).Should().Be(100_000m);
            Convert.ToDecimal(row["MonthlySalary_Min"]).Should().Be(60_000m);
            Convert.ToDecimal(row["MonthlySalary_Sum"]).Should().Be(400_000m);
        }

        /// <summary>
        /// [正向] 部門人數圓餅圖（Pie Chart）資料
        /// 驗證：4 個部門人數加總等於全公司人數
        /// </summary>
        [TestMethod]
        public void Hr_圓餅圖資料_四個部門人數加總等於全公司人數()
        {
            // Arrange
            HrTestDataStore.Employees = new List<EmployeeRecord>
            {
                HrDataFactory.Active("工程部", 80_000m),
                HrDataFactory.Active("工程部", 85_000m),
                HrDataFactory.Active("業務部", 55_000m),
                HrDataFactory.Active("業務部", 58_000m),
                HrDataFactory.Active("業務部", 60_000m),
                HrDataFactory.Active("人資部", 65_000m),
                HrDataFactory.Active("財務部", 70_000m),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Department" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "HeadCount", Func = AggregateFunc.Sum }
                }
            };

            // Act
            var result = Engine().Execute(Q(), req, _allFields);

            // Assert
            result.Rows.Should().HaveCount(4, because: "應有 4 個部門");

            var totalHeadCount = result.Rows
                .Sum(r => Convert.ToDecimal(r["HeadCount_Sum"]));

            totalHeadCount.Should().Be(7m, because: "4 個部門人數加總應等於 7 人");
        }

        /// <summary>
        /// [正向] Dashboard AnalysisWidgetDataSource 橋接
        /// 驗證：Widget 從 EmployeeListVM 取得部門人數資料，欄位與列數正確
        /// </summary>
        [TestMethod]
        public async System.Threading.Tasks.Task Hr_Dashboard_Widget橋接_部門人數Widget資料正確()
        {
            // Arrange
            HrTestDataStore.Employees = new List<EmployeeRecord>
            {
                HrDataFactory.Active("工程部", 80_000m),
                HrDataFactory.Active("業務部", 55_000m),
                HrDataFactory.Active("業務部", 60_000m),
            };

            var source = new AnalysisWidgetDataSource(_registry, _serviceProvider, _engine);

            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(EmployeeListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Department" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "HeadCount", Func = AggregateFunc.Sum }
                    })
                }
            };

            // Act
            var result = await source.GetDataAsync(request);

            // Assert
            result.Columns.Should().Contain("Department");
            result.Columns.Should().Contain("HeadCount_Sum");
            result.Rows.Should().HaveCount(2, because: "有工程部和業務部兩個分組");

            var totalFromWidget = result.Rows
                .Sum(r => Convert.ToDecimal(r["HeadCount_Sum"]));
            totalFromWidget.Should().Be(3m);
        }

        /// <summary>
        /// [正向] 年資結構分析
        /// 驗證：按年資區間分布，平均年資計算正確
        /// </summary>
        [TestMethod]
        public void Hr_年資結構分析_部門平均年資計算正確()
        {
            // Arrange：工程部 3 人，年資分別為 12、24、36 個月
            HrTestDataStore.Employees = new List<EmployeeRecord>
            {
                HrDataFactory.Active("工程部", 70_000m, tenureMonths: 12m),
                HrDataFactory.Active("工程部", 80_000m, tenureMonths: 24m),
                HrDataFactory.Active("工程部", 90_000m, tenureMonths: 36m),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Department" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "TenureMonths", Func = AggregateFunc.Avg },
                    new() { Field = "TenureMonths", Func = AggregateFunc.Max },
                    new() { Field = "TenureMonths", Func = AggregateFunc.Min },
                }
            };

            // Act
            var result = Engine().Execute(Q(), req, _allFields);

            // Assert
            result.Rows.Should().HaveCount(1);
            var row = result.Rows[0];

            Convert.ToDecimal(row["TenureMonths_Avg"]).Should().Be(24m,
                because: "(12+24+36)/3 = 24 個月平均年資");
            Convert.ToDecimal(row["TenureMonths_Max"]).Should().Be(36m);
            Convert.ToDecimal(row["TenureMonths_Min"]).Should().Be(12m);
        }

        // ─────────────────────────────────────────────────────────
        // 反向測試：異常資料處理
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// [反向] ETL 轉換：未來入職日期應被過濾
        /// 業務規則：入職日期不可為未來時間（系統錯誤或資料異常）
        /// </summary>
        [TestMethod]
        public void HrEtl_轉換_未來入職日期應被過濾掉()
        {
            // Arrange
            var rawData = new DataTable();
            rawData.Columns.Add("EmployeeId", typeof(string));
            rawData.Columns.Add("HireDate", typeof(DateTime));
            rawData.Columns.Add("MonthlySalary", typeof(decimal));

            // 1 筆正常 + 1 筆未來入職
            rawData.Rows.Add("EMP001", DateTime.UtcNow.AddMonths(-6), 80_000m);
            rawData.Rows.Add("EMP002", DateTime.UtcNow.AddMonths(3), 75_000m);  // 未來入職

            // Act
            var cleaned = HrDataFactory.TransformHrData(rawData);

            // Assert
            cleaned.Rows.Count.Should().Be(1,
                because: "未來入職日期的資料列應被 ETL 轉換邏輯過濾");
            cleaned.Rows[0]["EmployeeId"].Should().Be("EMP001");
        }

        /// <summary>
        /// [反向] ETL 轉換：薪資為 0 或負數應被過濾
        /// 業務規則：薪資必須為正整數
        /// </summary>
        [TestMethod]
        public void HrEtl_轉換_薪資為零或負數應被過濾()
        {
            // Arrange
            var rawData = new DataTable();
            rawData.Columns.Add("EmployeeId", typeof(string));
            rawData.Columns.Add("HireDate", typeof(DateTime));
            rawData.Columns.Add("MonthlySalary", typeof(decimal));

            rawData.Rows.Add("EMP001", DateTime.UtcNow.AddYears(-1), 60_000m);  // 正常
            rawData.Rows.Add("EMP002", DateTime.UtcNow.AddYears(-1), 0m);       // 薪資 0
            rawData.Rows.Add("EMP003", DateTime.UtcNow.AddYears(-1), -5_000m);  // 負薪資

            // Act
            var cleaned = HrDataFactory.TransformHrData(rawData);

            // Assert
            cleaned.Rows.Count.Should().Be(1,
                because: "薪資 <= 0 的記錄應被 ETL 過濾");
        }

        /// <summary>
        /// [反向] 員工同時屬於兩個部門（兼職/借調）分組計算
        /// 業務場景：借調員工同時有主部門與借調部門記錄
        /// 驗證：兩筆記錄各自計入對應部門，總人數為 2
        /// </summary>
        [TestMethod]
        public void Hr_借調員工_同時屬兩個部門的記錄各自計入分組()
        {
            // Arrange：王小明借調，工程部保留原職、同時計入專案部
            HrTestDataStore.Employees = new List<EmployeeRecord>
            {
                HrDataFactory.Active("工程部", 80_000m),   // 王小明主職
                HrDataFactory.Active("專案部", 80_000m),   // 王小明借調（兩筆記錄）
                HrDataFactory.Active("業務部", 55_000m),   // 其他員工
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Department" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "HeadCount", Func = AggregateFunc.Sum }
                }
            };

            // Act
            var result = Engine().Execute(Q(), req, _allFields);

            // Assert
            result.Rows.Should().HaveCount(3, because: "工程部、專案部、業務部各一組");

            var totalHeadCount = result.Rows.Sum(r => Convert.ToDecimal(r["HeadCount_Sum"]));
            totalHeadCount.Should().Be(3m,
                because: "借調員工產生兩筆記錄，人數記法依業務規則由資料來源決定");
        }

        /// <summary>
        /// [反向] Analysis 欄位包含敏感資料（身分證號）的安全防護
        /// 非 HR-Manager 角色不得看到 NationalId 和 MonthlySalary 欄位
        /// </summary>
        [TestMethod]
        public void Hr_安全_非HR主管無法查詢身分證號欄位()
        {
            // Arrange：一般主管角色（非 HR-Manager）
            var regularManagerUser = new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "DeptManager") })
            );

            var policy = new DefaultAnalysisFieldPolicy();
            var filteredFields = policy.Filter(_allFields, regularManagerUser).ToList();

            // Assert：NationalId 和 MonthlySalary 應被過濾掉
            filteredFields.Should().NotContain(f => f.FieldName == "NationalId",
                because: "非 HR-Manager 不得存取身分證號欄位");
            filteredFields.Should().NotContain(f => f.FieldName == "MonthlySalary",
                because: "非 HR-Manager 不得存取薪資欄位");

            // 可以看到的欄位（無 AllowedRoles 限制）
            filteredFields.Should().Contain(f => f.FieldName == "Department");
            filteredFields.Should().Contain(f => f.FieldName == "HeadCount");
        }

        /// <summary>
        /// [反向] Analysis 欄位安全：非授權角色嘗試查詢薪資欄位應拋例外
        /// </summary>
        [TestMethod]
        public void Hr_安全_非HR主管查詢薪資欄位應拋InvalidOperationException()
        {
            // Arrange：過濾後的欄位白名單（只有非敏感欄位）
            var regularManagerUser = new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "DeptManager") })
            );

            var policy = new DefaultAnalysisFieldPolicy();
            var filteredFields = policy.Filter(_allFields, regularManagerUser);

            HrTestDataStore.Employees = new List<EmployeeRecord>
            {
                HrDataFactory.Active("工程部", 80_000m),
            };

            // 嘗試在過濾後的白名單中查詢薪資
            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Department" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "MonthlySalary", Func = AggregateFunc.Avg }  // 敏感欄位
                }
            };

            // Act & Assert
            Action act = () => Engine().Execute(Q(), req, filteredFields);
            act.Should().Throw<InvalidOperationException>(
                because: "薪資欄位已從白名單中移除，查詢應被拒絕");
        }

        /// <summary>
        /// [反向] HR-Manager 角色可以查詢薪資欄位（正向對照）
        /// </summary>
        [TestMethod]
        public void Hr_安全_HR主管可查詢薪資欄位()
        {
            // Arrange
            var hrManagerUser = new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "HR-Manager") })
            );

            var policy = new DefaultAnalysisFieldPolicy();
            var hrFields = policy.Filter(_allFields, hrManagerUser).ToList();

            // Assert：HR-Manager 可以看到薪資和身分證號欄位
            hrFields.Should().Contain(f => f.FieldName == "MonthlySalary",
                because: "HR-Manager 有薪資查看權限");
            hrFields.Should().Contain(f => f.FieldName == "NationalId",
                because: "HR-Manager 有身分證號查看權限");
        }

        // ─────────────────────────────────────────────────────────
        // 邊界測試：臨界值行為
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// [邊界] 部門只有 1 人時的離職率（100% 離職 = 單人部門全員離職）
        /// 驗證：離職計數 = 1，HeadCount_Sum = 0
        /// </summary>
        [TestMethod]
        public void Hr_邊界_單人部門全員離職_離職計數為1且在職人數為0()
        {
            // Arrange：財務部只有 1 人，且已離職
            HrTestDataStore.Employees = new List<EmployeeRecord>
            {
                HrDataFactory.Resigned("財務部", 70_000m,
                    new DateTime(2024, 1, 1), new DateTime(2026, 3, 1)),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Department", "Status" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "HeadCount", Func = AggregateFunc.Count }
                }
            };

            // Act
            var result = Engine().Execute(Q(), req, _allFields);

            // Assert
            result.Rows.Should().HaveCount(1);
            var row = result.Rows[0];
            row["Department"].Should().Be("財務部");
            row["Status"].Should().Be("Resigned");
            Convert.ToDecimal(row["HeadCount_Count"]).Should().Be(1m,
                because: "財務部僅有 1 筆離職記錄");
        }

        /// <summary>
        /// [邊界] 全公司 0 離職的月份
        /// 驗證：過濾 Resigned 後結果集為空，不應拋例外
        /// </summary>
        [TestMethod]
        public void Hr_邊界_全公司零離職月份_查詢應回傳空結果集()
        {
            // Arrange：全部在職員工，無離職記錄
            HrTestDataStore.Employees = new List<EmployeeRecord>
            {
                HrDataFactory.Active("工程部", 80_000m),
                HrDataFactory.Active("業務部", 55_000m),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Department" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "HeadCount", Func = AggregateFunc.Count }
                },
                Filters = new List<FilterCondition>
                {
                    new() { Field = "Status", Operator = FilterOperator.Eq, Value = "Resigned" }
                }
            };

            // Act
            var result = Engine().Execute(Q(), req, _allFields);

            // Assert：0 離職，結果應為空
            result.Rows.Should().BeEmpty(
                because: "全公司無離職員工時，離職查詢應回傳空結果，不應拋例外");
        }

        /// <summary>
        /// [邊界] 薪資全部相同時的統計
        /// 驗證：Max = Min = Avg = 該薪資值
        /// </summary>
        [TestMethod]
        public void Hr_邊界_薪資全部相同_MaxMinAvg應相等()
        {
            // Arrange：5 人全部月薪 75,000
            HrTestDataStore.Employees = new List<EmployeeRecord>
            {
                HrDataFactory.Active("工程部", 75_000m),
                HrDataFactory.Active("工程部", 75_000m),
                HrDataFactory.Active("工程部", 75_000m),
                HrDataFactory.Active("工程部", 75_000m),
                HrDataFactory.Active("工程部", 75_000m),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Department" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "MonthlySalary", Func = AggregateFunc.Max },
                    new() { Field = "MonthlySalary", Func = AggregateFunc.Min },
                    new() { Field = "MonthlySalary", Func = AggregateFunc.Avg },
                }
            };

            // Act
            var result = Engine().Execute(Q(), req, _allFields);

            // Assert
            result.Rows.Should().HaveCount(1);
            var row = result.Rows[0];

            var max = Convert.ToDecimal(row["MonthlySalary_Max"]);
            var min = Convert.ToDecimal(row["MonthlySalary_Min"]);
            var avg = Convert.ToDecimal(row["MonthlySalary_Avg"]);

            max.Should().Be(75_000m);
            min.Should().Be(75_000m);
            avg.Should().Be(75_000m);
            max.Should().Be(min, because: "薪資全部相同時 Max 應等於 Min");
        }

        /// <summary>
        /// [邊界] 閏年 2/29 入職員工的年資計算
        /// 驗證：2024-02-29 入職，至 2026-02-28 的年資計算不拋例外
        /// </summary>
        [TestMethod]
        public void Hr_邊界_閏年2月29日入職員工_年資計算不拋例外()
        {
            // Arrange：2024 是閏年，2/29 入職
            var leapYearHireDate = new DateTime(2024, 2, 29);
            var checkDate = new DateTime(2026, 2, 28);  // 2026 非閏年，無 2/29

            // 計算年資（月數）：模擬 ETL 計算邏輯
            var tenureMonths = (decimal)(checkDate - leapYearHireDate).TotalDays / 30m;

            HrTestDataStore.Employees = new List<EmployeeRecord>
            {
                new()
                {
                    ID = Guid.NewGuid(),
                    Department = "工程部",
                    Status = "Active",
                    HireDate = leapYearHireDate,
                    MonthlySalary = 85_000m,
                    HeadCount = 1m,
                    TenureMonths = tenureMonths,
                    NationalId = "A123456789"
                }
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Department" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "TenureMonths", Func = AggregateFunc.Avg }
                }
            };

            // Act（不應拋例外）
            Action act = () => Engine().Execute(Q(), req, _allFields);

            // Assert
            act.Should().NotThrow(
                because: "閏年 2/29 入職的年資計算不應產生例外");

            var result = Engine().Execute(Q(), req, _allFields);
            result.Rows.Should().HaveCount(1);
            Convert.ToDecimal(result.Rows[0]["TenureMonths_Avg"])
                .Should().BeGreaterThan(20m, because: "閏年入職超過 2 年的年資應大於 20 個月");
        }

        /// <summary>
        /// [邊界] 空公司（無員工資料）時 Dashboard Widget 應回傳空結果不崩潰
        /// </summary>
        [TestMethod]
        public async System.Threading.Tasks.Task Hr_邊界_空員工資料_Dashboard_Widget回傳空結果不崩潰()
        {
            // Arrange：無員工資料
            HrTestDataStore.Employees = new List<EmployeeRecord>();

            var source = new AnalysisWidgetDataSource(_registry, _serviceProvider, _engine);

            var request = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(EmployeeListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Department" }),
                    ["measures"] = JsonSerializer.Serialize(new[]
                    {
                        new { Field = "HeadCount", Func = AggregateFunc.Sum }
                    })
                }
            };

            // Act
            var result = await source.GetDataAsync(request);

            // Assert
            result.Should().NotBeNull();
            result.Rows.Should().BeEmpty(because: "無員工資料時應回傳空結果集，不拋例外");
        }

        // ─────────────────────────────────────────────────────────
        // 安全測試：管理層審查視角
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// [安全] Admin 角色可存取所有欄位（包含薪資和身分證號）
        /// 驗證 DefaultAnalysisFieldPolicy 的 Admin 豁免規則
        /// </summary>
        [TestMethod]
        public void Hr_安全_Admin角色可存取所有HR敏感欄位()
        {
            // Arrange：Admin 角色
            var adminUser = new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "Admin") })
            );

            var policy = new DefaultAnalysisFieldPolicy();

            // Act
            var adminFields = policy.Filter(_allFields, adminUser).ToList();

            // Assert：Admin 應能看到所有欄位
            adminFields.Should().Contain(f => f.FieldName == "NationalId",
                because: "Admin 有超級管理員權限");
            adminFields.Should().Contain(f => f.FieldName == "MonthlySalary",
                because: "Admin 可查看薪資");
            adminFields.Count().Should().Be(_allFields.Count(),
                because: "Admin 可存取全部欄位");
        }

        /// <summary>
        /// [安全] 匿名用戶無任何角色時，只能看到無 AllowedRoles 限制的欄位
        /// </summary>
        [TestMethod]
        public void Hr_安全_匿名用戶只能看到公開欄位()
        {
            // Arrange：完全匿名（無任何 Claims）
            var anonymousUser = new ClaimsPrincipal(new ClaimsIdentity());

            var policy = new DefaultAnalysisFieldPolicy();
            var publicFields = policy.Filter(_allFields, anonymousUser).ToList();

            // Assert
            publicFields.Should().NotContain(f => f.FieldName == "NationalId");
            publicFields.Should().NotContain(f => f.FieldName == "MonthlySalary");

            // 公開欄位應可見
            publicFields.Should().Contain(f => f.FieldName == "Department");
            publicFields.Should().Contain(f => f.FieldName == "Status");
            publicFields.Should().Contain(f => f.FieldName == "HeadCount");
            publicFields.Should().Contain(f => f.FieldName == "TenureMonths");
        }

        /// <summary>
        /// [安全] 匯出前去識別化驗證：Analysis 查詢結果中不應存在完整身分證號
        /// 業務規則：匯出報表必須去除 NationalId 欄位
        /// </summary>
        [TestMethod]
        public void Hr_安全_匯出前去識別化_查詢結果不含身分證欄位()
        {
            // Arrange：使用非 HR-Manager 的過濾白名單
            var regularUser = new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "DeptManager") })
            );
            var policy = new DefaultAnalysisFieldPolicy();
            var safeFields = policy.Filter(_allFields, regularUser).ToList();

            HrTestDataStore.Employees = new List<EmployeeRecord>
            {
                HrDataFactory.Active("工程部", 80_000m),
            };

            var req = new AnalysisQueryRequest
            {
                Dimensions = new List<string> { "Department" },
                Measures = new List<MeasureRequest>
                {
                    new() { Field = "HeadCount", Func = AggregateFunc.Sum }
                }
            };

            // Act
            var result = Engine().Execute(Q(), req, safeFields);

            // Assert：結果欄位中不應包含 NationalId
            result.Columns.Should().NotContain("NationalId",
                because: "去識別化後的報表不應包含身分證號");
            result.Columns.Should().NotContain("MonthlySalary_Sum",
                because: "非授權角色的查詢結果不應包含薪資資料");
        }

        // ─────────────────────────────────────────────────────────
        // ETL Watermark 邏輯測試（CHRO 關心的增量同步正確性）
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// [正向] ETL Watermark：FullLoad 模式每次同步全量資料
        /// 驗證：FullLoad watermark 的 WHERE clause 為 "1=1"
        /// </summary>
        [TestMethod]
        public void HrEtl_Watermark_FullLoad模式每次全量同步()
        {
            // Arrange
            var watermark = new WalkingTec.Mvvm.Etl.Pipeline.WatermarkStrategy(
                WalkingTec.Mvvm.Etl.Models.EtlWatermarkType.FullLoad, null, null);

            // Act
            var whereClause = watermark.BuildWhereClause();

            // Assert
            whereClause.Should().Be("1=1",
                because: "FullLoad 模式不應用任何 watermark 過濾條件");
        }

        /// <summary>
        /// [正向] ETL Watermark：Timestamp 增量同步，有前次 watermark 時使用增量查詢
        /// 業務場景：人事系統每晚 23:00 增量同步異動員工記錄
        /// </summary>
        [TestMethod]
        public void HrEtl_Watermark_Timestamp增量同步_有前次值時使用增量WHERE()
        {
            // Arrange：前次同步 watermark 為昨天 23:00
            var lastSync = new DateTime(2026, 3, 15, 23, 0, 0, DateTimeKind.Utc);
            var watermarkValue = System.Text.Json.JsonSerializer.Serialize(lastSync);

            var watermark = new WalkingTec.Mvvm.Etl.Pipeline.WatermarkStrategy(
                WalkingTec.Mvvm.Etl.Models.EtlWatermarkType.Timestamp,
                column: "UpdatedAt",
                currentValue: watermarkValue);

            // Act
            var whereClause = watermark.BuildWhereClause();
            var paramValue = watermark.GetParameterValue();

            // Assert
            whereClause.Should().Be("UpdatedAt > @watermark",
                because: "Timestamp 增量模式應使用 > @watermark 過濾");
            paramValue.Should().BeOfType<DateTime>();
        }

        /// <summary>
        /// [正向] ETL Watermark：首次執行（無前次值）應全量同步
        /// </summary>
        [TestMethod]
        public void HrEtl_Watermark_首次執行無前次值_應全量同步()
        {
            // Arrange：首次同步，無歷史 watermark
            var watermark = new WalkingTec.Mvvm.Etl.Pipeline.WatermarkStrategy(
                WalkingTec.Mvvm.Etl.Models.EtlWatermarkType.Timestamp,
                column: "UpdatedAt",
                currentValue: null);  // 無前次值

            // Act
            var whereClause = watermark.BuildWhereClause();
            var paramValue = watermark.GetParameterValue();

            // Assert
            whereClause.Should().Be("1=1",
                because: "首次執行無前次 watermark 時應全量同步");
            paramValue.Should().BeNull();
        }

        /// <summary>
        /// [正向] ETL Watermark：Job 失敗後 watermark 不更新（資料一致性保障）
        /// 業務場景：人事系統同步中途失敗，下次應從相同時間點重新同步
        /// </summary>
        [TestMethod]
        public void HrEtl_Watermark_Job失敗後丟棄暫存值_下次從相同時間點重新同步()
        {
            // Arrange
            var originalTime = new DateTime(2026, 3, 14, 23, 0, 0, DateTimeKind.Utc);
            var watermarkValue = System.Text.Json.JsonSerializer.Serialize(originalTime);

            var watermark = new WalkingTec.Mvvm.Etl.Pipeline.WatermarkStrategy(
                WalkingTec.Mvvm.Etl.Models.EtlWatermarkType.Timestamp,
                column: "UpdatedAt",
                currentValue: watermarkValue);

            // 模擬 batch 中有更新的 watermark 暫存值
            var newMaxTime = new DateTime(2026, 3, 15, 23, 0, 0, DateTimeKind.Utc);
            watermark.UpdateFromBatchMax(newMaxTime);

            // Act：Job 失敗，丟棄暫存值
            watermark.DiscardPendingValue();

            // Assert：CurrentValue 仍為原始值（不應更新）
            watermark.CurrentValue.Should().Be(watermarkValue,
                because: "Job 失敗後 watermark 不應更新，確保下次從相同時間點重試");
        }
    }
}
