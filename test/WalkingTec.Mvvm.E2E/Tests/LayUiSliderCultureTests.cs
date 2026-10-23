using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests
{
    /// <summary>
    /// wt:slider on a server whose request culture has a decimal comma (#322): the hidden input carries the value with a decimal point,
    /// the same text layui writes when the handle is dragged, so the posted value does not depend on the regional number format.
    /// The test host of this class runs with Languages=de-DE (see <see cref="SiteTargets.LayUIDecimalComma"/>).
    /// </summary>
    [TestClass]
    public class LayUiSliderCultureTests : LabTest
    {
        protected override SiteTarget Target => SiteTargets.LayUIDecimalComma;

        [TestInitialize]
        public async Task OpenPage()
        {
            await Lab.OpenAsync("Inputs");
        }

        [TestMethod]
        public async Task Host_FormatsNumbersWithADecimalComma()
        {
            // guards the setup of this class: wt:display formats with the culture of the request, 1234.5 with "0.00" is 1234,50 in de-DE
            await Expect(Lab.Field("ShownAmount")).ToHaveTextAsync("1234,50");
        }

        [TestMethod]
        public async Task Slider_DecimalValue_IsRenderedWithADecimalPoint()
        {
            await Expect(Body.Locator("input[type=hidden][name=Rate]")).ToHaveValueAsync("30.5");
            await Expect(Body.Locator("input[type=hidden][name=Level]")).ToHaveValueAsync("30");
        }

        [TestMethod]
        public async Task Slider_DecimalValue_IsPostedWithADecimalPoint()
        {
            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Inputs");

            var posted = await Lab.LastPostAsync("Inputs");
            CollectionAssert.AreEqual(new[] { "30.5" }, posted["Rate"]);
            CollectionAssert.AreEqual(new[] { "30" }, posted["Level"]);
            CollectionAssert.AreEqual(new[] { "20" }, posted["Low"]);
            CollectionAssert.AreEqual(new[] { "70" }, posted["High"]);
        }

        // ---- what the model binder makes of the posted text (the request culture of the host is de-DE) -----------------------

        private async Task<System.Collections.Generic.Dictionary<string, string[]>> SaveAsync()
        {
            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Inputs");
            return await Lab.LastPostAsync("Inputs");
        }

        private static string Describe(System.Collections.Generic.Dictionary<string, string[]> posted) =>
            $"posted Rate=[{string.Join("|", posted["Rate"])}] bound Rate=[{posted["bound:Rate"][0]}] error=[{string.Join("|", posted["bound:RateError"])}] culture=[{posted["bound:Culture"][0]}]";

        [TestMethod]
        public async Task Bound_Untouched_RateIsTheSeededValue()
        {
            var posted = await SaveAsync();

            Console.WriteLine("UNTOUCHED: " + Describe(posted));
            Assert.AreEqual("de-DE", posted["bound:Culture"][0]);
            Assert.AreEqual("30.5", posted["bound:Rate"][0], Describe(posted));
        }

        [TestMethod]
        public async Task Bound_Dragged_RateIsTheDraggedValue()
        {
            var track = Body.Locator("#_sliderLabFormVM_Rate .layui-slider");
            var box = await track.BoundingBoxAsync();
            await track.ClickAsync(new() { Position = new Microsoft.Playwright.Position { X = (float)(box.Width * 0.5), Y = box.Height / 2 } });
            var hidden = await Body.Locator("input[type=hidden][name=Rate]").InputValueAsync();

            var posted = await SaveAsync();

            Console.WriteLine("DRAGGED (hidden=" + hidden + "): " + Describe(posted));
            Assert.AreEqual(hidden, posted["Rate"][0]);
            Assert.AreEqual(hidden, posted["bound:Rate"][0], Describe(posted));
        }

        [TestMethod]
        public async Task Bound_FractionalScriptValue_RateIsTheWrittenValue()
        {
            // what layui writes for a fractional value: JavaScript number text, always with a decimal point
            await Body.Locator("input[type=hidden][name=Rate]").EvaluateAsync("e => e.value = String(42.5)");

            var posted = await SaveAsync();

            Console.WriteLine("SCRIPT 42.5: " + Describe(posted));
            Assert.AreEqual("42.5", posted["Rate"][0]);
            Assert.AreEqual("42.5", posted["bound:Rate"][0], Describe(posted));
        }
    }
}
