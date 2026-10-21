using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests
{
    /// <summary>
    /// UIOptions:EncodeHtmlContent. The test host starts with it on (all other LayUI tests rely on that); the framework default is off, where the
    /// text of titles, labels and button texts is emitted as it is so that pages which pass HTML on purpose keep working.
    /// </summary>
    [TestClass]
    public class LayUiHtmlEncodingOptionTests : LabTest
    {
        [TestMethod]
        public async Task Off_TextOfTagHelpersIsEmittedAsIs_SoMarkupInItIsKept()
        {
            await SetHtmlContentEncodingAsync(false);

            await Lab.OpenAsync("Layout");

            // the card title and the tab header are HTML now: the element that was in the text exists
            await Expect(Body.Locator("#lab-card-white").Locator("xpath=preceding::div[contains(@class,'layui-card-header')][1]").Locator("#e2e-probe")).ToHaveCountAsync(1);
            await Expect(Body.Locator(".layui-tab-title li").Nth(1).Locator("#e2e-probe")).ToHaveCountAsync(1);
        }

        [TestMethod]
        public async Task On_TheSameTextIsShownAsText()
        {
            await Lab.OpenAsync("Layout");

            await Expect(Body.Locator(".layui-tab-title li").Nth(1)).ToHaveTextAsync(LabData.Special);
            await Expect(Body.Locator(".layui-tab-title li").Nth(1).Locator("#e2e-probe")).ToHaveCountAsync(0);
        }

        [TestMethod]
        public async Task Off_GridCellsOfRowsWithRawMarkupAreInterpreted_WhileOnShowsThemAsText()
        {
            await SetHtmlContentEncodingAsync(false);
            await Lab.OpenAsync("Grid");
            await Expect(Body.Locator(".layui-table-main tr").First).ToBeVisibleAsync();
            // rows 5 and 10 of the first page hold the special name; with the option off its markup becomes an element
            await Expect(Body.Locator(".layui-table-main td[data-field=Name] #e2e-probe-item")).ToHaveCountAsync(2);

            await SetHtmlContentEncodingAsync(true);
            await Lab.OpenAsync("Grid");
            await Expect(Body.Locator(".layui-table-main tr").First).ToBeVisibleAsync();
            await Expect(Body.Locator(".layui-table-main td[data-field=Name] #e2e-probe-item")).ToHaveCountAsync(0);
        }
    }
}
