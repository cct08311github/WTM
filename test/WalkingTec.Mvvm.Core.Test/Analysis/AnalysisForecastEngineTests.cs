#nullable enable
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    /// <summary>
    /// 純記憶體測試 — 驗證 <see cref="AnalysisForecastEngine"/> 的線性回歸 /
    /// 移動平均預測、邊界條件 (時間維度遺失、實際列不足、無法 parse 期別)
    /// 都靜默 no-op 而非 throw。
    /// </summary>
    [TestClass]
    public class AnalysisForecastEngineTests
    {
        private static AnalysisQueryResponse BuildLinearMonthly()
        {
            return new AnalysisQueryResponse
            {
                Rows = new List<Dictionary<string, object?>>
                {
                    new() { ["Month"] = "2026-01", ["Amount_Sum"] = 100m },
                    new() { ["Month"] = "2026-02", ["Amount_Sum"] = 200m },
                    new() { ["Month"] = "2026-03", ["Amount_Sum"] = 300m },
                    new() { ["Month"] = "2026-04", ["Amount_Sum"] = 400m },
                },
            };
        }

        private static Dictionary<string, DateHierarchy> MonthlyHierarchy() =>
            new() { ["Month"] = DateHierarchy.Month };

        [TestMethod]
        public void Linear_appends_3_forecast_rows_with_correct_labels()
        {
            var resp = BuildLinearMonthly();
            AnalysisForecastEngine.ApplyForecast(
                resp,
                new ForecastSpec { Periods = 3, Method = ForecastMethod.Linear },
                new List<string> { "Month" },
                new List<string> { "Amount_Sum" },
                MonthlyHierarchy());

            Assert.IsTrue(resp.HasForecast);
            Assert.AreEqual(7, resp.Rows.Count, "4 actual + 3 forecast");
            var forecasted = resp.Rows.Where(r =>
                r.TryGetValue(AnalysisForecastEngine.ForecastFlagKey, out var f) && f is true).ToList();
            Assert.AreEqual(3, forecasted.Count);
            Assert.AreEqual("2026-05", forecasted[0]["Month"]);
            Assert.AreEqual("2026-06", forecasted[1]["Month"]);
            Assert.AreEqual("2026-07", forecasted[2]["Month"]);
        }

        [TestMethod]
        public void Linear_extrapolates_perfect_line_correctly()
        {
            var resp = BuildLinearMonthly();
            AnalysisForecastEngine.ApplyForecast(
                resp,
                new ForecastSpec { Periods = 2, Method = ForecastMethod.Linear },
                new List<string> { "Month" },
                new List<string> { "Amount_Sum" },
                MonthlyHierarchy());

            // y = 100x + 100；實際 indices 0..3，下兩期應為 500、600
            Assert.AreEqual(500m, resp.Rows[4]["Amount_Sum"]);
            Assert.AreEqual(600m, resp.Rows[5]["Amount_Sum"]);
        }

        [TestMethod]
        public void MovingAverage_averages_last_3_periods_constant()
        {
            var resp = new AnalysisQueryResponse
            {
                Rows = new List<Dictionary<string, object?>>
                {
                    new() { ["Month"] = "2026-01", ["Amount_Sum"] = 100m },
                    new() { ["Month"] = "2026-02", ["Amount_Sum"] = 200m },
                    new() { ["Month"] = "2026-03", ["Amount_Sum"] = 300m }, // last 3: 100,200,300 → avg=200
                },
            };
            AnalysisForecastEngine.ApplyForecast(
                resp,
                new ForecastSpec { Periods = 2, Method = ForecastMethod.MovingAverage },
                new List<string> { "Month" },
                new List<string> { "Amount_Sum" },
                MonthlyHierarchy());

            Assert.IsTrue(resp.HasForecast);
            Assert.AreEqual(200m, resp.Rows[3]["Amount_Sum"]);
            Assert.AreEqual(200m, resp.Rows[4]["Amount_Sum"], "MA = constant");
        }

        [TestMethod]
        public void MovingAverage_uses_all_when_fewer_than_window()
        {
            var resp = new AnalysisQueryResponse
            {
                Rows = new List<Dictionary<string, object?>>
                {
                    new() { ["Month"] = "2026-01", ["Amount_Sum"] = 100m },
                    new() { ["Month"] = "2026-02", ["Amount_Sum"] = 200m },
                },
            };
            AnalysisForecastEngine.ApplyForecast(
                resp,
                new ForecastSpec { Periods = 1, Method = ForecastMethod.MovingAverage },
                new List<string> { "Month" },
                new List<string> { "Amount_Sum" },
                MonthlyHierarchy());

            Assert.AreEqual(150m, resp.Rows[2]["Amount_Sum"]);
        }

        [TestMethod]
        public void NoOp_when_no_hierarchies_provided()
        {
            var resp = BuildLinearMonthly();
            AnalysisForecastEngine.ApplyForecast(
                resp,
                new ForecastSpec { Periods = 1 },
                new List<string> { "Month" },
                new List<string> { "Amount_Sum" },
                hierarchies: null);
            Assert.IsFalse(resp.HasForecast);
            Assert.AreEqual(4, resp.Rows.Count);
        }

        [TestMethod]
        public void NoOp_when_first_dim_not_in_hierarchies()
        {
            var resp = new AnalysisQueryResponse
            {
                Rows = new List<Dictionary<string, object?>>
                {
                    new() { ["Region"] = "北", ["Amount_Sum"] = 100m },
                    new() { ["Region"] = "南", ["Amount_Sum"] = 200m },
                },
            };
            AnalysisForecastEngine.ApplyForecast(
                resp,
                new ForecastSpec { Periods = 1 },
                new List<string> { "Region" },
                new List<string> { "Amount_Sum" },
                MonthlyHierarchy()); // hierarchy 對 "Month" 但維度是 "Region"

            Assert.IsFalse(resp.HasForecast);
            Assert.AreEqual(2, resp.Rows.Count);
        }

        [TestMethod]
        public void Linear_returns_null_when_fewer_than_2_points()
        {
            var resp = new AnalysisQueryResponse
            {
                Rows = new List<Dictionary<string, object?>>
                {
                    new() { ["Month"] = "2026-01", ["Amount_Sum"] = 100m },
                },
            };
            AnalysisForecastEngine.ApplyForecast(
                resp,
                new ForecastSpec { Periods = 1, Method = ForecastMethod.Linear },
                new List<string> { "Month" },
                new List<string> { "Amount_Sum" },
                MonthlyHierarchy());

            // 仍 append 預測列（label 可推得），但 measure 為 null
            Assert.IsTrue(resp.HasForecast);
            Assert.IsNull(resp.Rows[1]["Amount_Sum"]);
            Assert.AreEqual("2026-02", resp.Rows[1]["Month"]);
        }

        [TestMethod]
        public void Periods_clamped_to_max()
        {
            var resp = BuildLinearMonthly();
            AnalysisForecastEngine.ApplyForecast(
                resp,
                new ForecastSpec { Periods = 99, Method = ForecastMethod.Linear },
                new List<string> { "Month" },
                new List<string> { "Amount_Sum" },
                MonthlyHierarchy());

            var forecasted = resp.Rows.Skip(4).ToList();
            Assert.AreEqual(AnalysisForecastEngine.MaxPeriods, forecasted.Count);
        }

        [TestMethod]
        public void Empty_rows_noop()
        {
            var resp = new AnalysisQueryResponse();
            AnalysisForecastEngine.ApplyForecast(
                resp,
                new ForecastSpec { Periods = 1 },
                new List<string> { "Month" },
                new List<string> { "Amount_Sum" },
                MonthlyHierarchy());
            Assert.IsFalse(resp.HasForecast);
            Assert.AreEqual(0, resp.Rows.Count);
        }

        [TestMethod]
        public void Quarter_hierarchy_advances_correctly()
        {
            var resp = new AnalysisQueryResponse
            {
                Rows = new List<Dictionary<string, object?>>
                {
                    new() { ["Quarter"] = "2026 Q1", ["Amount_Sum"] = 100m },
                    new() { ["Quarter"] = "2026 Q2", ["Amount_Sum"] = 200m },
                    new() { ["Quarter"] = "2026 Q3", ["Amount_Sum"] = 300m },
                },
            };
            AnalysisForecastEngine.ApplyForecast(
                resp,
                new ForecastSpec { Periods = 2, Method = ForecastMethod.Linear },
                new List<string> { "Quarter" },
                new List<string> { "Amount_Sum" },
                new Dictionary<string, DateHierarchy> { ["Quarter"] = DateHierarchy.Quarter });

            Assert.AreEqual("2026 Q4", resp.Rows[3]["Quarter"]);
            Assert.AreEqual("2027 Q1", resp.Rows[4]["Quarter"]);
        }

        [TestMethod]
        public void NextLabel_returns_null_for_unparseable()
        {
            Assert.IsNull(DateTruncator.NextLabel("not-a-date", DateHierarchy.Month));
            Assert.IsNull(DateTruncator.NextLabel(null, DateHierarchy.Year));
            Assert.IsNull(DateTruncator.NextLabel("2026-99", DateHierarchy.Month));
        }

        [TestMethod]
        public void NextLabel_year_quarter_month_day_round_trip()
        {
            Assert.AreEqual("2027", DateTruncator.NextLabel("2026", DateHierarchy.Year));
            Assert.AreEqual("2026 Q2", DateTruncator.NextLabel("2026 Q1", DateHierarchy.Quarter));
            Assert.AreEqual("2027 Q1", DateTruncator.NextLabel("2026 Q4", DateHierarchy.Quarter));
            Assert.AreEqual("2026-04", DateTruncator.NextLabel("2026-03", DateHierarchy.Month));
            Assert.AreEqual("2027-01", DateTruncator.NextLabel("2026-12", DateHierarchy.Month));
            Assert.AreEqual("2026-03-16", DateTruncator.NextLabel("2026-03-15", DateHierarchy.Day));
        }
    }
}
