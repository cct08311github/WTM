using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests
{
    /// <summary>
    /// wt:selector (popup with a data table, single and multiple selection) on /Lab/Pickers.
    /// The pop up asks /_Framework/Selector for the dialog and /_Framework/GetPagingData for the rows.
    /// </summary>
    [TestClass]
    public class LayUiSelectorTests : LabTest
    {
        private static readonly Regex Guid = new Regex("^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$");

        [TestInitialize]
        public async Task OpenPage()
        {
            await Lab.OpenAsync("Pickers");
        }

        private ILocator Dialog => Page.Locator(".layui-layer-page");

        private async Task OpenSelectorAsync(string property)
        {
            await Body.Locator($"#LabFormVM_{property}_Select").ClickAsync();
            await Expect(Dialog.Locator(".layui-table-main tr").First).ToBeVisibleAsync();
        }

        private ILocator DialogRow(string name) => Dialog.Locator(".layui-table-main tr", new() { HasTextString = name });

        private async Task CheckDialogRowAsync(string name)
        {
            // the check box column is a fixed overlay table; the row there has the same data-index
            var index = await DialogRow(name).GetAttributeAsync("data-index");
            await Dialog.Locator($".layui-table-fixed-l tr[data-index='{index}'] .layui-form-checkbox").ClickAsync();
        }

        private async Task ConfirmDialogAsync()
        {
            await Dialog.Locator("button", new() { HasTextString = "OK" }).ClickAsync();
            await Expect(Dialog).ToHaveCountAsync(0);
        }

        [TestMethod]
        public async Task Selector_Single_ShowsTheRowsOfTheListViewModelInAPopup()
        {
            await OpenSelectorAsync("GadgetId");

            await Expect(Dialog).ToContainTextAsync("Pick a gadget");
            await Expect(DialogRow("Alpha Wrench")).ToContainTextAsync("G-100");
            await Expect(DialogRow("Epsilon Kite")).ToBeVisibleAsync();
        }

        [TestMethod]
        public async Task Selector_Single_PickingARowFillsTheTextBoxAndPostsItsId()
        {
            await OpenSelectorAsync("GadgetId");
            await CheckDialogRowAsync("Beta Spinner");
            await ConfirmDialogAsync();

            await Expect(Body.Locator("#LabFormVM_GadgetId_Display")).ToHaveValueAsync("Beta Spinner");
            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Pickers");

            var posted = (await Lab.LastPostAsync("Pickers"))["GadgetId"].Single();
            StringAssert.Matches(posted, Guid);
        }

        [TestMethod]
        public async Task Selector_Single_RefusesASecondRow()
        {
            await OpenSelectorAsync("GadgetId");
            await CheckDialogRowAsync("Alpha Wrench");
            await CheckDialogRowAsync("Beta Spinner");
            await Dialog.Locator("button", new() { HasTextString = "OK" }).ClickAsync();

            // the single selector keeps the pop up open and tells the user to pick one row
            await Expect(Page.Locator(".layui-layer-msg")).ToBeVisibleAsync();
            await Expect(Dialog).ToHaveCountAsync(1);
            await Expect(Body.Locator("#LabFormVM_GadgetId_Display")).ToHaveValueAsync("");
        }

        [TestMethod]
        public async Task Selector_Multiple_PostsEverySelectedId()
        {
            await OpenSelectorAsync("GadgetIds");
            await CheckDialogRowAsync("Alpha Wrench");
            await CheckDialogRowAsync("Gamma Blender");
            await ConfirmDialogAsync();

            await Expect(Body.Locator("#LabFormVM_GadgetIds_Display")).ToHaveValueAsync("Alpha Wrench,Gamma Blender");
            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Pickers");

            var posted = (await Lab.LastPostAsync("Pickers"))["GadgetIds"];
            Assert.AreEqual(2, posted.Distinct().Count());
            foreach (var id in posted)
            {
                StringAssert.Matches(id, Guid);
            }
        }

        [TestMethod]
        public async Task Selector_SelectAllInTheHeader_PicksEveryRowOfThePage_AndAgainClearsThem()
        {
            await OpenSelectorAsync("GadgetIds");
            var rows = await Dialog.Locator(".layui-table-main tr").CountAsync();
            await Dialog.Locator(".layui-table-fixed-l .layui-table-header .layui-form-checkbox").First.ClickAsync();
            await ConfirmDialogAsync();

            var names = (await Body.Locator("#LabFormVM_GadgetIds_Display").InputValueAsync()).Split(',');
            Assert.AreEqual(rows, names.Length, "Every row of the page is selected.");
            CollectionAssert.Contains(names, "Alpha Wrench");

            // open again (the rows are checked) and use the header check box to clear them
            await OpenSelectorAsync("GadgetIds");
            await Dialog.Locator(".layui-table-fixed-l .layui-table-header .layui-form-checkbox").First.ClickAsync();
            await ConfirmDialogAsync();
            await Expect(Body.Locator("#LabFormVM_GadgetIds_Display")).ToHaveValueAsync("");
        }

        [TestMethod]
        public async Task Selector_UncheckingARow_RemovesItFromTheSelection()
        {
            await OpenSelectorAsync("GadgetIds");
            await CheckDialogRowAsync("Alpha Wrench");
            await CheckDialogRowAsync("Beta Spinner");
            // check again = uncheck
            await CheckDialogRowAsync("Alpha Wrench");
            await ConfirmDialogAsync();

            await Expect(Body.Locator("#LabFormVM_GadgetIds_Display")).ToHaveValueAsync("Beta Spinner");
        }

        [TestMethod]
        public async Task Selector_OpenedAgain_KeepsThePreviousSelectionChecked()
        {
            await OpenSelectorAsync("GadgetIds");
            await CheckDialogRowAsync("Delta Level");
            await ConfirmDialogAsync();

            await OpenSelectorAsync("GadgetIds");

            var index = await DialogRow("Delta Level").GetAttributeAsync("data-index");
            await Expect(Dialog.Locator($".layui-table-fixed-l tr[data-index='{index}'] .layui-form-checked")).ToHaveCountAsync(1);
        }
    }
}
