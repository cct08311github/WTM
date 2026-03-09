using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    [TestClass]
    public class AnalysisFieldScannerTests
    {
        private class OrderModel
        {
            [Dimension(DisplayName = "地區")]
            public string Region { get; set; } = string.Empty;

            [Dimension(DisplayName = "業務員")]
            public string SalesRep { get; set; } = string.Empty;

            [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count, DisplayName = "金額")]
            public decimal Amount { get; set; }

            public string Ignored { get; set; } = string.Empty;
        }

        [TestMethod]
        public void Scan_returns_all_annotated_fields()
        {
            var fields = AnalysisFieldScanner.ScanModel(typeof(OrderModel)).ToList();
            Assert.AreEqual(3, fields.Count);
        }

        [TestMethod]
        public void Dimension_fields_have_correct_kind()
        {
            var dims = AnalysisFieldScanner.ScanModel(typeof(OrderModel))
                           .Where(f => f.Kind == AnalysisFieldKind.Dimension).ToList();
            Assert.AreEqual(2, dims.Count);
            Assert.IsTrue(dims.Any(f => f.FieldName == "Region" && f.DisplayName == "地區"));
        }

        [TestMethod]
        public void Measure_fields_have_allowed_funcs()
        {
            var m = AnalysisFieldScanner.ScanModel(typeof(OrderModel))
                        .Single(f => f.Kind == AnalysisFieldKind.Measure);
            Assert.AreEqual("Amount", m.FieldName);
            Assert.IsTrue(m.AllowedFuncs.HasFlag(AggregateFunc.Sum));
            Assert.IsFalse(m.AllowedFuncs.HasFlag(AggregateFunc.Avg));
        }

        [TestMethod]
        public void Non_annotated_properties_excluded()
        {
            var fields = AnalysisFieldScanner.ScanModel(typeof(OrderModel)).ToList();
            Assert.IsFalse(fields.Any(f => f.FieldName == "Ignored"));
        }

        [TestMethod]
        public void ScanModel_null_throws_ArgumentNullException()
        {
            Assert.ThrowsException<ArgumentNullException>(
                () => AnalysisFieldScanner.ScanModel(null!).ToList());
        }

        private class NoDisplayNameModel
        {
            [Dimension]
            public string Category { get; set; } = string.Empty;
        }

        [TestMethod]
        public void DisplayName_falls_back_to_property_name_when_null()
        {
            var fields = AnalysisFieldScanner.ScanModel(typeof(NoDisplayNameModel)).ToList();
            Assert.AreEqual(1, fields.Count);
            Assert.AreEqual("Category", fields[0].DisplayName);
        }

        // --- DateHierarchy tests (#91, #92, #93) ---

        private class DateModel
        {
            [Dimension(DisplayName = "訂單日期", Hierarchy = DateHierarchy.Month)]
            public DateTime OrderDate { get; set; }

            [Dimension(DisplayName = "取消日期")]
            public DateTime? CancelDate { get; set; }

            [Dimension(DisplayName = "地區")]
            public string Region { get; set; } = string.Empty;

            [Dimension(Hierarchy = DateHierarchy.Year)]
            public DateTime CreatedAt { get; set; }
        }

        [TestMethod]
        public void DateTime_property_sets_IsDate_true_and_Hierarchy()
        {
            var field = AnalysisFieldScanner.ScanModel(typeof(DateModel))
                            .Single(f => f.FieldName == "OrderDate");
            Assert.IsTrue(field.IsDate);
            Assert.AreEqual(DateHierarchy.Month, field.Hierarchy);
        }

        [TestMethod]
        public void Nullable_DateTime_property_sets_IsDate_true()
        {
            var field = AnalysisFieldScanner.ScanModel(typeof(DateModel))
                            .Single(f => f.FieldName == "CancelDate");
            Assert.IsTrue(field.IsDate);
            Assert.AreEqual(DateHierarchy.None, field.Hierarchy);
        }

        [TestMethod]
        public void String_dimension_sets_IsDate_false()
        {
            var field = AnalysisFieldScanner.ScanModel(typeof(DateModel))
                            .Single(f => f.FieldName == "Region");
            Assert.IsFalse(field.IsDate);
            Assert.AreEqual(DateHierarchy.None, field.Hierarchy);
        }

        [TestMethod]
        public void Non_date_dimension_has_Hierarchy_None()
        {
            var field = AnalysisFieldScanner.ScanModel(typeof(OrderModel))
                            .Single(f => f.FieldName == "Region");
            Assert.IsFalse(field.IsDate);
            Assert.AreEqual(DateHierarchy.None, field.Hierarchy);
        }

        [TestMethod]
        public void DateTime_with_Year_hierarchy()
        {
            var field = AnalysisFieldScanner.ScanModel(typeof(DateModel))
                            .Single(f => f.FieldName == "CreatedAt");
            Assert.IsTrue(field.IsDate);
            Assert.AreEqual(DateHierarchy.Year, field.Hierarchy);
        }
    }
}
