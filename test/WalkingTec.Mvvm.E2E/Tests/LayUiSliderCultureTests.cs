using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests
{
    /// <summary>
    /// wt:slider on a server whose request culture has a decimal comma (#322, #342). The test host of this class runs with Languages=de-DE
    /// (see <see cref="SiteTargets.LayUIDecimalComma"/>); the form is bound with that culture. The hidden input keeps the text of the request
    /// culture, so an untouched slider posts "30,5" and the model binder makes 30.5 of it (an invariant "30.5" would be bound as 305).
    /// The bound value is echoed by the Lab Save action as "bound:*" next to the raw form.
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

        private async Task<Dictionary<string, string[]>> SaveAsync()
        {
            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Inputs");
            return await Lab.LastPostAsync("Inputs");
        }

        private static string Describe(Dictionary<string, string[]> posted) =>
            $"posted Rate=[{string.Join("|", posted["Rate"])}] bound Rate=[{posted["bound:Rate"][0]}] error=[{string.Join("|", posted["bound:RateError"])}]";

        [TestMethod]
        public async Task Host_FormatsNumbersWithADecimalComma()
        {
            // guards the setup of this class: wt:display formats with the culture of the request, 1234.5 with "0.00" is 1234,50 in de-DE
            await Expect(Lab.Field("ShownAmount")).ToHaveTextAsync("1234,50");
        }

        [TestMethod]
        public async Task Slider_DecimalValue_IsRenderedInTheRequestCulture()
        {
            await Expect(Body.Locator("input[type=hidden][name=Rate]")).ToHaveValueAsync("30,5");
            await Expect(Body.Locator("input[type=hidden][name=Level]")).ToHaveValueAsync("30");
        }

        [TestMethod]
        public async Task Slider_Untouched_PostsTheRenderedTextAndBindsTheSeededValue()
        {
            var posted = await SaveAsync();

            Assert.AreEqual("de-DE", posted["bound:Culture"][0]);
            CollectionAssert.AreEqual(new[] { "30,5" }, posted["Rate"]);
            Assert.AreEqual("30.5", posted["bound:Rate"][0], Describe(posted));
            Assert.AreEqual(0, posted["bound:RateError"].Length, Describe(posted));
            CollectionAssert.AreEqual(new[] { "30" }, posted["Level"]);
            CollectionAssert.AreEqual(new[] { "20" }, posted["Low"]);
            CollectionAssert.AreEqual(new[] { "70" }, posted["High"]);
        }

        [TestMethod]
        public async Task Slider_Dragged_PostsAndBindsTheInteger()
        {
            // layui snaps the handle to the step (1), so a drag writes an integer
            var track = Body.Locator("#_sliderLabFormVM_Rate .layui-slider");
            var box = await track.BoundingBoxAsync();
            await track.ClickAsync(new() { Position = new Position { X = (float)(box.Width * 0.5), Y = box.Height / 2 } });
            await Expect(Body.Locator("input[type=hidden][name=Rate]")).ToHaveValueAsync("50");

            var posted = await SaveAsync();

            CollectionAssert.AreEqual(new[] { "50" }, posted["Rate"]);
            Assert.AreEqual("50", posted["bound:Rate"][0], Describe(posted));
            Assert.AreEqual(0, posted["bound:RateError"].Length, Describe(posted));
        }

        [TestMethod]
        public async Task Slider_ScriptValueInTheRequestCulture_BindsTheWrittenValue()
        {
            // #343, by design: the hidden input holds text of the request culture and is bound with it, so a script that writes a
            // fractional value into it has to use the decimal separator of that culture (42,5 on this de-DE host). The layui slider
            // itself only produces integers. Writing "42.5" is not supported on a decimal comma host (it is read as 425, see FORK.md).
            await Body.Locator("input[type=hidden][name=Rate]").EvaluateAsync("e => e.value = '42,5'");

            var posted = await SaveAsync();

            CollectionAssert.AreEqual(new[] { "42,5" }, posted["Rate"]);
            Assert.AreEqual("42.5", posted["bound:Rate"][0], Describe(posted));
            Assert.AreEqual(0, posted["bound:RateError"].Length, Describe(posted));
        }
    }
}
