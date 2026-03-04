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
            public string Region { get; set; }

            [Dimension(DisplayName = "業務員")]
            public string SalesRep { get; set; }

            [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count, DisplayName = "金額")]
            public decimal Amount { get; set; }

            public string Ignored { get; set; }
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
    }
}
