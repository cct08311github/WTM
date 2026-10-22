using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Issue #281: wt:slider draws its handle from the bound value of the field (the value that is also written to
    /// the hidden input); default-value only applies when the field has no value.
    /// </summary>
    [TestClass]
    public class SliderBoundValueTests : LayUITagHelperTestBase
    {
        public class SliderVM : WalkingTec.Mvvm.Core.BaseVM
        {
            public int? Level { get; set; }
            public int Score { get; set; }
            public int Low { get; set; }
            public int High { get; set; }
            public int? High2 { get; set; }
            public decimal Price { get; set; }
            public double Ratio { get; set; }
            public string Text { get; set; }
        }

        [TestMethod]
        public void BoundValue_IsUsedForTheHandle()
        {
            var vm = new SliderVM { Level = 30 };

            var html = Render(new SliderTagHelper { Field = Field(vm, x => x.Level) }, "wt:slider");

            AssertContains(html, "name='Level' value='30'", ",value:30\n");
        }

        [TestMethod]
        public void BoundValue_WinsOverDefaultValue()
        {
            var vm = new SliderVM { Level = 30 };

            var html = Render(new SliderTagHelper { Field = Field(vm, x => x.Level), DefaultValue = "50" }, "wt:slider");

            AssertContains(html, "name='Level' value='30'", ",value:30\n");
            AssertNotContains(html, ",value:50");
        }

        [TestMethod]
        public void NoBoundValue_DefaultValueStillApplies()
        {
            var vm = new SliderVM { Level = null };

            var html = Render(new SliderTagHelper { Field = Field(vm, x => x.Level), DefaultValue = "50" }, "wt:slider");

            AssertContains(html, "name='Level' value='50'", ",value:50\n");
        }

        [TestMethod]
        public void NoBoundValue_NoDefaultValue_OmitsValue()
        {
            var vm = new SliderVM { Level = null };

            AssertNotContains(Render(new SliderTagHelper { Field = Field(vm, x => x.Level) }, "wt:slider"), ",value:");
        }

        [TestMethod]
        public void ValueTypeZero_DoesNotOverrideDefaultValue()
        {
            // an int property cannot tell "not set" from 0: default-value keeps applying as it always did
            var vm = new SliderVM { Score = 0 };

            var html = Render(new SliderTagHelper { Field = Field(vm, x => x.Score), DefaultValue = "30" }, "wt:slider");

            AssertContains(html, ",value:30\n");
        }

        [TestMethod]
        public void ValueTypeZero_WithoutDefaultValue_OnAScaleStartingAtZero_OmitsValue()
        {
            var vm = new SliderVM { Score = 0 };

            AssertNotContains(Render(new SliderTagHelper { Field = Field(vm, x => x.Score) }, "wt:slider"), ",value:");
        }

        [TestMethod]
        public void ValueTypeZero_WithoutDefaultValue_IsTheHandlePosition()
        {
            var vm = new SliderVM { Score = 0 };

            var html = Render(new SliderTagHelper { Field = Field(vm, x => x.Score), Min = -50, Max = 50 }, "wt:slider");

            AssertContains(html, "name='Score' value='0'", ",value:0\n");
        }

        [TestMethod]
        public void NonZeroValueType_WinsOverDefaultValue()
        {
            var vm = new SliderVM { Score = 70 };

            var html = Render(new SliderTagHelper { Field = Field(vm, x => x.Score), DefaultValue = "30" }, "wt:slider");

            AssertContains(html, ",value:70\n");
            AssertNotContains(html, ",value:30");
        }

        [TestMethod]
        public void Range_BoundValues_AreUsedForBothEnds()
        {
            var vm = new SliderVM { Low = 20, High = 70 };

            var html = Render(new SliderTagHelper { Field = Field(vm, x => x.Low), Field1 = Field(vm, x => x.High), DefaultValue = "[5, 95]" }, "wt:slider");

            AssertContains(html, "name='Low' value='20'", "name='High' value='70'", ",range:true", ",value:[20,70]\n");
            AssertNotContains(html, "[5, 95]");
        }

        [TestMethod]
        public void Range_OneEndBound_MissingEndIsZeroLikeTheHiddenInput()
        {
            var vm = new SliderVM { Low = 20, High2 = null };

            var html = Render(new SliderTagHelper { Field = Field(vm, x => x.Low), Field1 = Field(vm, x => x.High2) }, "wt:slider");

            AssertContains(html, "name='Low' value='20'", "name='High2' value='0'", ",value:[20,0]\n");
        }

        [TestMethod]
        public void Range_BothZero_KeepsTheDefaultValue()
        {
            var vm = new SliderVM { Low = 0, High = 0 };

            var html = Render(new SliderTagHelper { Field = Field(vm, x => x.Low), Field1 = Field(vm, x => x.High), DefaultValue = "[10, 20]" }, "wt:slider");

            AssertContains(html, ",value:[10, 20]\n");
        }

        [TestMethod]
        public void Range_NoBoundValue_DefaultValueStillApplies()
        {
            var vm = new SliderVM { Level = null, High2 = null };

            var html = Render(new SliderTagHelper { Field = Field(vm, x => x.Level), Field1 = Field(vm, x => x.High2), DefaultValue = "[10, 20]" }, "wt:slider");

            AssertContains(html, "name='Level' value='10'", "name='High2' value='20'", ",value:[10, 20]\n");
        }

        [TestMethod]
        public void DecimalAndDoubleBoundValues_AreEmittedWithTheInvariantCulture()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                var vm = new SliderVM { Price = 12.5m, Ratio = 0.25 };

                var price = Render(new SliderTagHelper { Field = Field(vm, x => x.Price) }, "wt:slider");
                var ratio = Render(new SliderTagHelper { Field = Field(vm, x => x.Ratio) }, "wt:slider");

                AssertContains(price, ",value:12.5\n");
                AssertContains(ratio, ",value:0.25\n");
                AssertNotContains(price, ",value:12,5");
                AssertNotContains(ratio, ",value:0,25");
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [TestMethod]
        public void NumericTextBoundValue_IsEmittedAsANumber()
        {
            var vm = new SliderVM { Text = " 42 " };

            AssertContains(Render(new SliderTagHelper { Field = Field(vm, x => x.Text) }, "wt:slider"), ",value:42\n");
        }

        [TestMethod]
        public void NonNumericBoundValue_NeverReachesTheScript_AndFallsBackToTheDefault()
        {
            const string hostile = "1});alert(1);//";
            var vm = new SliderVM { Text = hostile };

            var withoutDefault = Render(new SliderTagHelper { Field = Field(vm, x => x.Text) }, "wt:slider");
            var withDefault = Render(new SliderTagHelper { Field = Field(vm, x => x.Text), DefaultValue = "30" }, "wt:slider");

            // the hidden input carries the text attribute-encoded; the script must not carry it at all
            AssertNotContains(withoutDefault, ",value:");
            AssertNotContains(withDefault, ",value:1", "value:1})");
            AssertContains(withDefault, ",value:30\n");
        }

        [TestMethod]
        public void NonFiniteBoundValue_IsDropped()
        {
            var vm = new SliderVM { Text = "Infinity" };

            AssertNotContains(Render(new SliderTagHelper { Field = Field(vm, x => x.Text) }, "wt:slider"), ",value:", "Infinity\n");
        }
    }
}
