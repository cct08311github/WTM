using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests
{
    /// <summary>
    /// Layout and display tag helpers on /Lab/Layout: wt:container, wt:row, wt:card, wt:panel, wt:quote, wt:code, wt:image, wt:tab (with
    /// tabheaders / tabheader / tabcontents / tabcontent), wt:fieldset, wt:button, wt:linkbutton and wt:flowinfo.
    /// </summary>
    [TestClass]
    public class LayUiLayoutTests : LabTest
    {
        [TestInitialize]
        public async Task OpenPage()
        {
            await Lab.OpenAsync("Layout");
        }

        private ILocator Dialog => Page.Locator(".layui-layer-page");

        // ---- container, row, card -------------------------------------------------------------------------------

        [TestMethod]
        public async Task ContainerAndRow_RenderTheirLayoutClasses()
        {
            await Expect(Body.Locator("#lab-container")).ToHaveAttributeAsync("style", new Regex("text-align:left"));
            await Expect(Body.Locator("#lab-row")).ToHaveClassAsync(new Regex("layui-row"));
            await Expect(Body.Locator("#lab-row")).ToHaveClassAsync(new Regex("layui-col-space10"));
            // two items per row: every child is wrapped in a half width column
            await Expect(Body.Locator("#lab-row > .layui-col-md6")).ToHaveCountAsync(2);
        }

        [TestMethod]
        public async Task Card_BothThemes_ShowTheirTitleAndBody()
        {
            await Expect(Body.Locator("#lab-card-dark .layui-card-header")).ToHaveTextAsync("Dark card");
            await Expect(Body.Locator("#lab-card-dark .layui-card-body")).ToHaveTextAsync("dark card body");
            await Expect(Body.Locator("#lab-row")).ToContainTextAsync("white card body");
        }

        [TestMethod]
        public async Task Card_TitleWithSpecialCharacters_IsShownAsText()
        {
            var header = Body.Locator("#lab-card-white").Locator("xpath=preceding::div[contains(@class,'layui-card-header')][1]");
            await Expect(header).ToHaveTextAsync(LabData.Special);
            await ExpectNoProbeElementsAsync(header);
        }

        // ---- panel ----------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task Panel_Collapse_TogglesItsBody()
        {
            var open = Body.Locator("#lab-panel-collapse .layui-colla-content");
            var closed = Body.Locator("#lab-panel-closed .layui-colla-content");
            await Expect(open).ToBeVisibleAsync();
            await Expect(closed).ToBeHiddenAsync();

            await Body.Locator("#lab-panel-closed .layui-colla-title").ClickAsync();
            await Expect(closed).ToBeVisibleAsync();
            await Expect(closed).ToHaveTextAsync("closed panel body");

            await Body.Locator("#lab-panel-collapse .layui-colla-title").ClickAsync();
            await Expect(open).ToBeHiddenAsync();
        }

        [TestMethod]
        public async Task Panel_CardType_ShowsTitleAndBody()
        {
            await Expect(Body.Locator("#lab-panel-card .layui-card-header")).ToHaveTextAsync("Card panel");
            await Expect(Body.Locator("#lab-panel-card .layui-card-body")).ToContainTextAsync("card panel body");
        }

        [TestMethod]
        public async Task Panel_TitleWithSpecialCharacters_IsShownAsText()
        {
            // layui adds its collapse icon, an icon-font character, to the title element
            await Expect(Body.Locator("#lab-panel-collapse .layui-colla-title")).ToHaveTextAsync(new Regex("^" + Regex.Escape(LabData.Special) + "[\\ue000-\\uf8ff]?$"));
            await ExpectNoProbeElementsAsync(Body.Locator("#lab-panel-collapse"));
        }

        // ---- quote, code, fieldset, image -----------------------------------------------------------------------

        [TestMethod]
        public async Task QuoteAndCode_ContentIsShownAsText()
        {
            await Expect(Body.Locator("#lab-quote")).ToHaveTextAsync(LabData.Special);
            await Expect(Body.Locator("#lab-quote")).ToHaveClassAsync(new Regex("layui-elem-quote"));
            await Expect(Body.Locator("#lab-quote-simple")).ToHaveClassAsync(new Regex("layui-quote-nm"));
            // layui turns a pre.layui-code into a titled code block
            await Expect(Body.Locator("#lab-code")).ToContainTextAsync(LabData.Special);
            await ExpectNoProbeElementsAsync(Body.Locator("#lab-quote, #lab-code"));
        }

        [TestMethod]
        public async Task Image_Renders_WithTheGivenUrlAndWidth()
        {
            var image = Body.Locator("#lab-imageimg");
            await Expect(image).ToHaveAttributeAsync("src", "/images/logo.png");
            await Expect(image).ToBeVisibleAsync();
            Assert.IsTrue(await image.EvaluateAsync<bool>("img => img.complete && img.naturalWidth > 0"), "The image did not load.");
            Assert.AreEqual(64, (int)(await image.BoundingBoxAsync()).Width);
        }

        // ---- tab ------------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task Tab_Switch_ShowsTheContentOfTheClickedHeader()
        {
            var tab = Body.Locator(".layui-tab");
            var headers = tab.Locator(".layui-tab-title li");
            await Expect(headers).ToHaveCountAsync(3);
            await Expect(headers.Nth(0)).ToHaveClassAsync(new Regex("layui-this"));
            await Expect(Body.Locator("#lab-tab-1")).ToBeVisibleAsync();
            await Expect(Body.Locator("#lab-tab-2")).ToBeHiddenAsync();

            await headers.Nth(2).ClickAsync();
            await Expect(headers.Nth(2)).ToHaveClassAsync(new Regex("layui-this"));
            await Expect(Body.Locator("#lab-tab-3")).ToBeVisibleAsync();
            await Expect(Body.Locator("#lab-tab-3")).ToHaveTextAsync("third content");
            await Expect(Body.Locator("#lab-tab-1")).ToBeHiddenAsync();
            await Expect(headers.Nth(0)).Not.ToHaveClassAsync(new Regex("layui-this"));
        }

        [TestMethod]
        public async Task Tab_HeaderWithSpecialCharacters_IsShownAsText()
        {
            var header = Body.Locator(".layui-tab-title li").Nth(1);
            await Expect(header).ToHaveTextAsync(LabData.Special);

            await header.ClickAsync();
            await Expect(Body.Locator("#lab-tab-2")).ToHaveTextAsync("second content");
            await ExpectNoProbeElementsAsync(Body.Locator(".layui-tab-title"));
        }

        // ---- buttons --------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task Button_Click_RunsTheHandlerOnce_AndADisabledButtonDoesNot()
        {
            await Body.Locator("#lab-btn-plain").ClickAsync();
            await ExpectScriptAsync("() => window.labClicks === 1");

            await Expect(Body.Locator("#lab-btn-disabled")).ToBeDisabledAsync();
            await Body.Locator("#lab-btn-disabled").ClickAsync(new() { Force = true });
            Assert.AreEqual(1, await Page.EvaluateAsync<int>("() => window.labClicks"));
        }

        [TestMethod]
        public async Task Button_Styles_AreAppliedFromTheAttributes()
        {
            await Expect(Body.Locator("#lab-btn-special")).ToHaveClassAsync(new Regex("layui-btn-warm"));
            await Expect(Body.Locator("#lab-btn-special")).ToHaveClassAsync(new Regex("layui-btn-sm"));
            await Expect(Body.Locator("#lab-btn-special i.layui-icon-ok")).ToHaveCountAsync(1);
            await Expect(Body.Locator("#lab-btn-round")).ToHaveClassAsync(new Regex("layui-btn-xs"));
            await Expect(Body.Locator("#lab-btn-round")).ToHaveClassAsync(new Regex("layui-btn-danger"));
        }

        [TestMethod]
        public async Task Button_TextWithSpecialCharacters_IsShownAsText()
        {
            await Expect(Body.Locator("#lab-btn-special")).ToHaveTextAsync(" " + LabData.Special);
            await ExpectNoProbeElementsAsync(Body.Locator("#lab-btn-special"));
        }

        [TestMethod]
        public async Task Button_WithConfirmText_RunsTheHandlerOnlyAfterTheUserConfirms()
        {
            await Body.Locator("#lab-btn-confirm").ClickAsync();
            var confirm = Page.Locator(".layui-layer-dialog");
            await Expect(confirm).ToContainTextAsync("Run it?");
            // cancel first: nothing runs
            await confirm.Locator(".layui-layer-btn1").ClickAsync();
            await Expect(confirm).ToHaveCountAsync(0);
            Assert.IsNull(await Page.EvaluateAsync<object>("() => window.labConfirmed === undefined ? null : window.labConfirmed"));

            await Body.Locator("#lab-btn-confirm").ClickAsync();
            await confirm.Locator(".layui-layer-btn0").ClickAsync();
            await ExpectScriptAsync("() => window.labConfirmed === true");
        }

        [TestMethod]
        public async Task LinkButton_Dialog_OpensAPopupWithTheTitleAndClosesAgain()
        {
            await Body.Locator("#lab-link-dialog").ClickAsync();

            await Expect(Dialog).ToContainTextAsync("Popup title");
            await Expect(Dialog).ToContainTextAsync("popup content");
            await Dialog.Locator("button", new() { HasTextString = "Close" }).ClickAsync();
            await Expect(Dialog).ToHaveCountAsync(0);
        }

        [TestMethod]
        public async Task LinkButton_Self_CallsTheUrlInTheBackgroundAndShowsTheAnswer()
        {
            var call = Page.WaitForResponseAsync(r => r.Url.Contains("/Lab/Ping"));
            await Body.Locator("#lab-link-self").ClickAsync();
            await call;

            await Expect(Page.Locator(".layui-layer-dialog")).ToContainTextAsync("pong");
        }

        [TestMethod]
        public async Task LinkButton_CurrentWindow_OpensTheUrlAsAnotherTabOfTheFrame()
        {
            await Expect(Lab.Frame.Tabs).ToHaveCountAsync(2);

            await Body.Locator("#lab-link-page").ClickAsync();

            await Expect(Lab.Frame.Tabs).ToHaveCountAsync(3);
            await Expect(Lab.Frame.Body).ToContainTextAsync("popup content");
        }

        // ---- flow info ------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task FlowInfo_WithoutWorkflowData_RendersAnEmptyTimeline()
        {
            // the host has no workflow; the tag helper renders the timeline element without items
            await Expect(Body.Locator("ul#lab-flow")).ToHaveClassAsync(new Regex("layui-timeline"));
            await Expect(Body.Locator("ul#lab-flow li")).ToHaveCountAsync(0);
        }
    }
}
