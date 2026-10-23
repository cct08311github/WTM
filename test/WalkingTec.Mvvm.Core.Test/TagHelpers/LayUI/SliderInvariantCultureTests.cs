using System;
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Issue #322: wt:slider writes the value of its hidden input(s) and the numbers of its script with the invariant culture,
    /// so the posted text does not depend on the regional number format of the server (de-DE writes 30,5, sv-SE a U+2212 minus).
    /// layui itself writes "30.5" into the hidden input when the handle is dragged; the rendered value has to match.
    /// </summary>
    [TestClass]
    public class SliderInvariantCultureTests : LayUITagHelperTestBase
    {
        public class SliderCultureVM : WalkingTec.Mvvm.Core.BaseVM
        {
            public decimal Price { get; set; }
            public decimal? OptionalPrice { get; set; }
            public double Ratio { get; set; }
            public float Weight { get; set; }
            public double RatioMax { get; set; }
            public decimal PriceMax { get; set; }
            public int Level { get; set; }
            public long Big { get; set; }
            public string Text { get; set; }
        }

        private static string RenderWith(string culture, Func<string> render)
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                return render();
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [TestMethod]
        public void Decimal_HiddenInput_UsesTheInvariantCulture()
        {
            var vm = new SliderCultureVM { Price = 30.5m };

            var html = RenderWith("de-DE", () => Render(new SliderTagHelper { Field = Field(vm, x => x.Price) }, "wt:slider"));

            AssertContains(html, "name='Price' value='30.5'", ",value:30.5\n");
            AssertNotContains(html, "value='30,5'", ",value:30,5");
        }

        [TestMethod]
        public void NullableDecimal_HiddenInput_UsesTheInvariantCulture()
        {
            var vm = new SliderCultureVM { OptionalPrice = 7.25m };

            var html = RenderWith("de-DE", () => Render(new SliderTagHelper { Field = Field(vm, x => x.OptionalPrice) }, "wt:slider"));

            AssertContains(html, "name='OptionalPrice' value='7.25'");
            AssertNotContains(html, "value='7,25'");
        }

        [TestMethod]
        public void DoubleAndFloat_HiddenInput_UseTheInvariantCulture()
        {
            var vm = new SliderCultureVM { Ratio = 0.25, Weight = 1.5f };

            var ratio = RenderWith("fr-FR", () => Render(new SliderTagHelper { Field = Field(vm, x => x.Ratio) }, "wt:slider"));
            var weight = RenderWith("fr-FR", () => Render(new SliderTagHelper { Field = Field(vm, x => x.Weight) }, "wt:slider"));

            AssertContains(ratio, "name='Ratio' value='0.25'");
            AssertContains(weight, "name='Weight' value='1.5'");
            AssertNotContains(ratio, "value='0,25'");
            AssertNotContains(weight, "value='1,5'");
        }

        [TestMethod]
        public void NegativeValue_HiddenInputAndScript_UseTheInvariantCulture()
        {
            // sv-SE writes the negative sign as U+2212, which is neither a valid number for the model binder nor for JavaScript
            var vm = new SliderCultureVM { Price = -1.5m, Level = -20 };

            var price = RenderWith("sv-SE", () => Render(new SliderTagHelper { Field = Field(vm, x => x.Price), Min = -10, Max = 10 }, "wt:slider"));
            var level = RenderWith("sv-SE", () => Render(new SliderTagHelper { Field = Field(vm, x => x.Level), Min = -50, Max = 50 }, "wt:slider"));

            AssertContains(price, "name='Price' value='-1.5'", ",value:-1.5\n");
            AssertContains(level, "name='Level' value='-20'", ",value:-20\n");
            AssertNotContains(price, "−");
            AssertNotContains(level, "−");
        }

        [TestMethod]
        public void Range_HiddenInputs_UseTheInvariantCulture()
        {
            var vm = new SliderCultureVM { Price = 10.5m, RatioMax = 80.75 };

            var html = RenderWith("de-DE", () => Render(
                new SliderTagHelper { Field = Field(vm, x => x.Price), Field1 = Field(vm, x => x.RatioMax) }, "wt:slider"));

            AssertContains(html, "name='Price' value='10.5'", "name='RatioMax' value='80.75'", ",range:true", ",value:[10.5,80.75]\n");
            AssertNotContains(html, "value='10,5'", "value='80,75'", "10,5", "80,75");
        }

        [TestMethod]
        public void Range_OneEndBound_MissingEndIsZero()
        {
            var vm = new SliderCultureVM { Price = 10.5m };

            var html = RenderWith("de-DE", () => Render(
                new SliderTagHelper { Field = Field(vm, x => x.Price), Field1 = Field(vm, x => x.OptionalPrice) }, "wt:slider"));

            AssertContains(html, "name='Price' value='10.5'", "name='OptionalPrice' value='0'", ",value:[10.5,0]\n");
        }

        [TestMethod]
        public void MinMaxStepAndHeight_AreWrittenWithTheInvariantCulture()
        {
            var vm = new SliderCultureVM { Level = 5 };

            var html = RenderWith("sv-SE", () => Render(
                new SliderTagHelper { Field = Field(vm, x => x.Level), Min = -100, Max = 2000, Step = 5, SliderType = SliderTypeEnum.Vertical, SliderHeight = 250 }, "wt:slider"));

            AssertContains(html, ",min:-100\n", ",max:2000\n", ",step:5\n", ",height:250\n");
            AssertNotContains(html, "−");
        }

        [TestMethod]
        public void DefaultValueText_IsWrittenAsGiven()
        {
            var vm = new SliderCultureVM { OptionalPrice = null };

            var html = RenderWith("de-DE", () => Render(new SliderTagHelper { Field = Field(vm, x => x.OptionalPrice), DefaultValue = "12.5" }, "wt:slider"));

            AssertContains(html, "name='OptionalPrice' value='12.5'", ",value:12.5\n");
        }

        [TestMethod]
        public void IntegerAndLongValues_AreUnchanged()
        {
            var vm = new SliderCultureVM { Level = 30, Big = 1234567890123 };

            var level = RenderWith("de-DE", () => Render(new SliderTagHelper { Field = Field(vm, x => x.Level) }, "wt:slider"));
            var big = RenderWith("de-DE", () => Render(new SliderTagHelper { Field = Field(vm, x => x.Big) }, "wt:slider"));

            AssertContains(level, "name='Level' value='30'", ",value:30\n");
            AssertContains(big, "name='Big' value='1234567890123'");
            AssertNotContains(big, "1.234.567.890.123");
        }

        [TestMethod]
        public void NonNumericText_IsWrittenAsGiven()
        {
            var vm = new SliderCultureVM { Text = "12,5" };

            var html = RenderWith("de-DE", () => Render(new SliderTagHelper { Field = Field(vm, x => x.Text) }, "wt:slider"));

            AssertContains(html, "name='Text' value='12,5'");
            AssertNotContains(html, ",value:");
        }

        [TestMethod]
        public void OutputDoesNotDependOnTheCurrentCulture()
        {
            // the same slider renders the same text under the default (invariant / en-US) culture and under cultures with other number formats
            var vm = new SliderCultureVM { Price = 30.5m, Ratio = -0.125, Weight = 2.5f, PriceMax = 1234.5m, Level = -3 };
            string RenderAll() =>
                Render(new SliderTagHelper { Field = Field(vm, x => x.Price), Min = -10, Max = 100, Step = 1 }, "wt:slider")
                + Render(new SliderTagHelper { Field = Field(vm, x => x.Ratio), Field1 = Field(vm, x => x.Weight) }, "wt:slider")
                + Render(new SliderTagHelper { Field = Field(vm, x => x.PriceMax), Field1 = Field(vm, x => x.Level), DefaultValue = "[5, 10]" }, "wt:slider");

            var invariant = RenderWith("", RenderAll);
            Assert.AreEqual(invariant, RenderWith("en-US", RenderAll));
            Assert.AreEqual(invariant, RenderWith("de-DE", RenderAll));
            Assert.AreEqual(invariant, RenderWith("fr-FR", RenderAll));
            Assert.AreEqual(invariant, RenderWith("sv-SE", RenderAll));
        }

        [TestMethod]
        public void DefaultCulture_ValuesAreUnchanged()
        {
            var vm = new SliderCultureVM { Price = 30.5m, Level = 30 };

            var html = RenderWith("en-US", () => Render(new SliderTagHelper { Field = Field(vm, x => x.Price), Field1 = Field(vm, x => x.Level) }, "wt:slider"));

            AssertContains(html, "name='Price' value='30.5'", "name='Level' value='30'", ",value:[30.5,30]\n");
        }
    }
}
