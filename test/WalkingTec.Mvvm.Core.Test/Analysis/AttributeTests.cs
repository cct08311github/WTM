using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    [TestClass]
    public class AttributeTests
    {
        private class SampleModel
        {
            [Dimension(DisplayName = "地區")]
            public string Region { get; set; }

            [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count, DisplayName = "金額")]
            public decimal Amount { get; set; }

            public string NotAnnotated { get; set; }
        }

        [TestMethod]
        public void Dimension_attribute_applied_correctly()
        {
            var prop = typeof(SampleModel).GetProperty(nameof(SampleModel.Region));
            var attr = prop.GetCustomAttributes(typeof(DimensionAttribute), false)
                           .Cast<DimensionAttribute>().Single();
            Assert.AreEqual("地區", attr.DisplayName);
        }

        [TestMethod]
        public void Measure_allows_flag_combination()
        {
            var prop = typeof(SampleModel).GetProperty(nameof(SampleModel.Amount));
            var attr = prop.GetCustomAttributes(typeof(MeasureAttribute), false)
                           .Cast<MeasureAttribute>().Single();
            Assert.IsTrue(attr.AllowedFuncs.HasFlag(AggregateFunc.Sum));
            Assert.IsTrue(attr.AllowedFuncs.HasFlag(AggregateFunc.Count));
            Assert.IsFalse(attr.AllowedFuncs.HasFlag(AggregateFunc.Avg));
        }

        [TestMethod]
        public void Non_annotated_property_has_no_analysis_attributes()
        {
            var prop = typeof(SampleModel).GetProperty(nameof(SampleModel.NotAnnotated));
            Assert.AreEqual(0, prop.GetCustomAttributes(typeof(DimensionAttribute), false).Length);
            Assert.AreEqual(0, prop.GetCustomAttributes(typeof(MeasureAttribute), false).Length);
        }
    }
}
