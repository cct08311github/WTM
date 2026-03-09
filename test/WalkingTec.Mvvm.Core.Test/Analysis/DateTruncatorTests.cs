#nullable enable
using System;
using System.Linq.Expressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    [TestClass]
    public class DateTruncatorTests
    {
        private class TestModel
        {
            public DateTime OrderDate { get; set; }
            public DateTime? ShipDate { get; set; }
        }

        // ── Year ───────────────────────────────────────────────

        [TestMethod]
        public void Year_truncation_returns_year_int()
        {
            var func = CompileDateTime(DateHierarchy.Year);
            var result = func(new TestModel { OrderDate = new DateTime(2026, 3, 9) });
            Assert.AreEqual(2026, result);
        }

        // ── Quarter ────────────────────────────────────────────

        [DataTestMethod]
        [DataRow(1, 20261)]  // Jan -> Q1
        [DataRow(3, 20261)]  // Mar -> Q1
        [DataRow(4, 20262)]  // Apr -> Q2
        [DataRow(6, 20262)]  // Jun -> Q2
        [DataRow(7, 20263)]  // Jul -> Q3
        [DataRow(9, 20263)]  // Sep -> Q3
        [DataRow(10, 20264)] // Oct -> Q4
        [DataRow(12, 20264)] // Dec -> Q4
        public void Quarter_truncation_maps_month_to_correct_quarter(int month, int expectedKey)
        {
            var func = CompileDateTime(DateHierarchy.Quarter);
            var result = func(new TestModel { OrderDate = new DateTime(2026, month, 15) });
            Assert.AreEqual(expectedKey, result);
        }

        // ── Month ──────────────────────────────────────────────

        [TestMethod]
        public void Month_truncation_returns_yearmonth_int()
        {
            var func = CompileDateTime(DateHierarchy.Month);
            var result = func(new TestModel { OrderDate = new DateTime(2026, 3, 9) });
            Assert.AreEqual(202603, result);
        }

        // ── Day ────────────────────────────────────────────────

        [TestMethod]
        public void Day_truncation_returns_yearmonthday_int()
        {
            var func = CompileDateTime(DateHierarchy.Day);
            var result = func(new TestModel { OrderDate = new DateTime(2026, 3, 9) });
            Assert.AreEqual(20260309, result);
        }

        // ── Nullable DateTime ──────────────────────────────────

        [TestMethod]
        public void Nullable_datetime_with_value_works_same_as_nonnullable()
        {
            var param = Expression.Parameter(typeof(TestModel), "x");
            var dateProp = Expression.Property(param, nameof(TestModel.ShipDate));
            var truncExpr = DateTruncator.BuildTruncExpression(dateProp, DateHierarchy.Month);
            var lambda = Expression.Lambda<Func<TestModel, int>>(truncExpr, param).Compile();

            var result = lambda(new TestModel { ShipDate = new DateTime(2026, 7, 20) });
            Assert.AreEqual(202607, result);
        }

        // ── FormatKey ──────────────────────────────────────────

        [TestMethod]
        public void FormatKey_Year_returns_plain_year()
        {
            Assert.AreEqual("2026", DateTruncator.FormatKey(2026, DateHierarchy.Year));
        }

        [TestMethod]
        public void FormatKey_Quarter_returns_year_space_quarter()
        {
            Assert.AreEqual("2026 Q1", DateTruncator.FormatKey(20261, DateHierarchy.Quarter));
            Assert.AreEqual("2026 Q4", DateTruncator.FormatKey(20264, DateHierarchy.Quarter));
        }

        [TestMethod]
        public void FormatKey_Month_returns_dash_separated()
        {
            Assert.AreEqual("2026-03", DateTruncator.FormatKey(202603, DateHierarchy.Month));
            Assert.AreEqual("2026-12", DateTruncator.FormatKey(202612, DateHierarchy.Month));
        }

        [TestMethod]
        public void FormatKey_Day_returns_iso_date()
        {
            Assert.AreEqual("2026-03-09", DateTruncator.FormatKey(20260309, DateHierarchy.Day));
        }

        // ── Invalid hierarchy ──────────────────────────────────

        [TestMethod]
        public void None_hierarchy_throws_ArgumentException()
        {
            var param = Expression.Parameter(typeof(TestModel), "x");
            var dateProp = Expression.Property(param, nameof(TestModel.OrderDate));
            Assert.ThrowsException<ArgumentException>(
                () => DateTruncator.BuildTruncExpression(dateProp, DateHierarchy.None));
        }

        // ── Helpers ────────────────────────────────────────────

        private static Func<TestModel, int> CompileDateTime(DateHierarchy hierarchy)
        {
            var param = Expression.Parameter(typeof(TestModel), "x");
            var dateProp = Expression.Property(param, nameof(TestModel.OrderDate));
            var truncExpr = DateTruncator.BuildTruncExpression(dateProp, hierarchy);
            return Expression.Lambda<Func<TestModel, int>>(truncExpr, param).Compile();
        }
    }
}
