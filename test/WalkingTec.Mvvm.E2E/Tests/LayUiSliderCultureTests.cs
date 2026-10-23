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
    }
}
