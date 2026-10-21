using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using WalkingTec.Mvvm.E2E.Pages;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests
{
    /// <summary>
    /// The remaining operations of the generated Gadget module: batch edit (BatchEdit / DoBatchEdit), the template download and the
    /// Excel import (Import, GetExcelTemplate, Upload). Each test creates the rows it needs under a unique name.
    /// </summary>
    [TestClass]
    public class GadgetBatchAndImportTests : E2ETest
    {
        protected override bool StartSignedIn => true;

        private static string UniqueName(string prefix) => prefix + " " + Guid.NewGuid().ToString("N").Substring(0, 8);

        private GadgetListPage _list;

        [TestInitialize]
        public async Task OpenList()
        {
            await new HomeFrame(Page).GotoAsync();
            _list = new GadgetListPage(Page);
            await _list.OpenAsync();
        }

        [TestMethod]
        public async Task BatchEdit_ChangesThePriceOfAllSelectedRows()
        {
            var prefix = UniqueName("Edit");
            await _list.CreateAsync(prefix + " one", "E-001", "1");
            await _list.CreateAsync(prefix + " two", "E-002", "2");
            await _list.SearchByNameAsync(prefix);
            await Expect(_list.Rows).ToHaveCountAsync(2);
            await _list.CheckRowAsync(prefix + " one");
            await _list.CheckRowAsync(prefix + " two");

            await _list.ToolbarButton("Batch edit").ClickAsync();
            await Expect(_list.Dialog.Locator("input[name='LinkedVM.Price']")).ToBeVisibleAsync();
            await _list.Dialog.Locator("input[name='LinkedVM.Price']").FillAsync("77");
            await _list.DialogButton("Submit").ClickAsync();

            await _list.AcceptAlertAsync("2 rows have been edited");
            await _list.SearchByNameAsync(prefix);
            await Expect(_list.Row(prefix + " one")).ToContainTextAsync("77");
            await Expect(_list.Row(prefix + " two")).ToContainTextAsync("77");
        }

        [TestMethod]
        public async Task BatchEdit_WithoutASelection_AsksForARow()
        {
            await _list.ToolbarButton("Batch edit").ClickAsync();

            await Expect(Page.Locator(".layui-layer-msg")).ToContainTextAsync("Please select at least one row");
            await Expect(_list.Dialog).ToHaveCountAsync(0);
        }

        [TestMethod]
        public async Task Import_TemplateWithOneFilledRow_AddsTheRow()
        {
            var name = UniqueName("Imported");
            await _list.ToolbarButton("Import").ClickAsync();
            await Expect(_list.Dialog.Locator("a", new() { HasTextString = "Download template" })).ToBeVisibleAsync();

            // the template carries the column headers; add one data row to it
            var download = await Page.RunAndWaitForDownloadAsync(async () => await _list.Dialog.Locator("a", new() { HasTextString = "Download template" }).ClickAsync());
            var path = Path.Combine(E2ERun.OutputDir, "downloads", "import-" + Guid.NewGuid().ToString("N").Substring(0, 6) + "-" + download.SuggestedFilename);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            await download.SaveAsAsync(path);
            XlsxFile.AppendRow(path, name, "I-001", "Tool", 42, "2025-03-04", "Yes");

            var chooser = await Page.RunAndWaitForFileChooserAsync(async () => await _list.Dialog.Locator("button", new() { HasTextString = "Select" }).ClickAsync());
            await chooser.SetFilesAsync(path);
            await Expect(_list.Dialog.Locator("input[type=hidden][name$='UploadFileId']")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("^[0-9a-f-]{36}$"));
            await _list.DialogButton("Submit").ClickAsync();

            await _list.AcceptAlertAsync("1 rows have been imported");
            await _list.SearchByNameAsync(name);
            await Expect(_list.Row(name)).ToContainTextAsync("I-001");
            await Expect(_list.Row(name)).ToContainTextAsync("42");
        }

        [TestMethod]
        public async Task Import_TemplateWithoutRows_ListsNoImportedData()
        {
            await _list.ToolbarButton("Import").ClickAsync();
            var download = await Page.RunAndWaitForDownloadAsync(async () => await _list.Dialog.Locator("a", new() { HasTextString = "Download template" }).ClickAsync());
            var path = Path.Combine(E2ERun.OutputDir, "downloads", "empty-" + Guid.NewGuid().ToString("N").Substring(0, 6) + "-" + download.SuggestedFilename);
            await download.SaveAsAsync(path);
            StringAssert.EndsWith(download.SuggestedFilename, ".xlsx");
            Assert.IsTrue(new FileInfo(path).Length > 1000, "The template is unexpectedly small.");
        }
    }
}
