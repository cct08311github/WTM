using System;
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Issues #322 and #342. The numbers of the slider script (value, min, max, step, height) are JavaScript literals and are written with
    /// the invariant culture whatever the culture of the request is (sv-SE writes a negative sign that is not valid JavaScript).
    /// The hidden inputs keep the text of the request culture: the form is bound with the same culture, so the posted text of an untouched
    /// slider binds to the rendered value (a de-DE host posts 30,5 and binds 30.5; an invariant 30.5 would bind as 305).
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

        private static string ScriptOf(string html)
        {
            var start = html.IndexOf("<script>", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "no script in the output");
            return html.Substring(start);
        }

        [TestMethod]
        public void Decimal_HiddenInputUsesTheRequestCulture_ScriptValueIsInvariant()
        {
            var vm = new SliderCultureVM { Price = 30.5m };

            var html = RenderWith("de-DE", () => Render(new SliderTagHelper { Field = Field(vm, x => x.Price) }, "wt:slider"));

            AssertContains(html, "name='Price' value='30,5'", ",value:30.5\n");
            AssertNotContains(html, ",value:30,5");
        }

        [TestMethod]
        public void DoubleAndFloat_HiddenInputsUseTheRequestCulture_ScriptValuesAreInvariant()
        {
            var vm = new SliderCultureVM { Ratio = 0.25, Weight = 1.5f };

            var ratio = RenderWith("fr-FR", () => Render(new SliderTagHelper { Field = Field(vm, x => x.Ratio) }, "wt:slider"));
            var weight = RenderWith("fr-FR", () => Render(new SliderTagHelper { Field = Field(vm, x => x.Weight) }, "wt:slider"));

            AssertContains(ratio, "name='Ratio' value='0,25'", ",value:0.25\n");
            AssertContains(weight, "name='Weight' value='1,5'", ",value:1.5\n");
        }

        [TestMethod]
        public void NegativeValue_ScriptIsInvariant_HiddenInputUsesTheRequestCulture()
        {
            // sv-SE writes the negative sign as U+2212: valid for the sv-SE model binder, not valid JavaScript
            var vm = new SliderCultureVM { Price = -1.5m, Level = -20 };
            var sv = new CultureInfo("sv-SE");

            var price = RenderWith("sv-SE", () => Render(new SliderTagHelper { Field = Field(vm, x => x.Price), Min = -10, Max = 10 }, "wt:slider"));
            var level = RenderWith("sv-SE", () => Render(new SliderTagHelper { Field = Field(vm, x => x.Level), Min = -50, Max = 50 }, "wt:slider"));

            AssertContains(price, $"name='Price' value='{(-1.5m).ToString(sv)}'", ",value:-1.5\n", ",min:-10\n", ",max:10\n");
            AssertContains(level, $"name='Level' value='{(-20).ToString(sv)}'", ",value:-20\n", ",min:-50\n", ",max:50\n");
            AssertNotContains(ScriptOf(price), "−");
            AssertNotContains(ScriptOf(level), "−");
        }

        [TestMethod]
        public void Range_HiddenInputsUseTheRequestCulture_ScriptValueIsInvariant()
        {
            var vm = new SliderCultureVM { Price = 10.5m, RatioMax = 80.75 };

            var html = RenderWith("de-DE", () => Render(
                new SliderTagHelper { Field = Field(vm, x => x.Price), Field1 = Field(vm, x => x.RatioMax) }, "wt:slider"));

            AssertContains(html, "name='Price' value='10,5'", "name='RatioMax' value='80,75'", ",range:true", ",value:[10.5,80.75]\n");
            AssertNotContains(ScriptOf(html), "10,5", "80,75");
        }

        [TestMethod]
        public void Range_OneEndBound_MissingEndIsZero()
        {
            var vm = new SliderCultureVM { Price = 10.5m };

            var html = RenderWith("de-DE", () => Render(
                new SliderTagHelper { Field = Field(vm, x => x.Price), Field1 = Field(vm, x => x.OptionalPrice) }, "wt:slider"));

            AssertContains(html, "name='Price' value='10,5'", "name='OptionalPrice' value='0'", ",value:[10.5,0]\n");
        }

        [TestMethod]
        public void MinMaxStepAndHeight_AreWrittenWithTheInvariantCulture()
        {
            var vm = new SliderCultureVM { Level = 5 };

            var html = RenderWith("sv-SE", () => Render(
                new SliderTagHelper { Field = Field(vm, x => x.Level), Min = -100, Max = 2000, Step = 5, SliderType = SliderTypeEnum.Vertical, SliderHeight = 250 }, "wt:slider"));

            AssertContains(html, ",min:-100\n", ",max:2000\n", ",step:5\n", ",height:250\n");
            AssertNotContains(ScriptOf(html), "−");
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
        public void ScriptDoesNotDependOnTheCurrentCulture()
        {
            // the script of the same sliders is identical under the default (invariant / en-US) culture and under cultures with other number formats
            var vm = new SliderCultureVM { Price = 30.5m, Ratio = -0.125, Weight = 2.5f, PriceMax = 1234.5m, Level = -3 };
            string RenderAll() =>
                ScriptOf(Render(new SliderTagHelper { Field = Field(vm, x => x.Price), Min = -10, Max = 100, Step = 1 }, "wt:slider"))
                + ScriptOf(Render(new SliderTagHelper { Field = Field(vm, x => x.Ratio), Field1 = Field(vm, x => x.Weight) }, "wt:slider"))
                + ScriptOf(Render(new SliderTagHelper { Field = Field(vm, x => x.PriceMax), Field1 = Field(vm, x => x.Level), DefaultValue = "[5, 10]" }, "wt:slider"));

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
