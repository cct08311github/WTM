using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests
{
    /// <summary>
    /// wt:searchpanel and wt:grid (data table) on /Lab/Grid: 35 synthetic rows, 10 per page, sortable columns, a dialog link in a cell,
    /// a row button and the Excel export. Every fifth name contains characters that need HTML encoding.
    /// </summary>
    [TestClass]
    public class LayUiGridTests : LabTest
    {
        [TestInitialize]
        public async Task OpenPage()
        {
            await Lab.OpenAsync("Grid");
            await Expect(Rows.First).ToBeVisibleAsync();
        }

        private ILocator Rows => Body.Locator(".layui-table-main tr");
        private ILocator Cell(ILocator row, string field) => row.Locator($"td[data-field={field}]");
        private ILocator Pager => Body.Locator(".layui-table-page");
        private ILocator Dialog => Page.Locator(".layui-layer-page");
        private ILocator Name => Body.Locator("#LabItemListVM_Searcher_Name");

        private async Task SearchAsync()
        {
            var search = Page.WaitForResponseAsync(r => r.Url.Contains("/Lab/Search") && r.Request.Method == "POST");
            await Body.Locator("a[issearchbutton]").ClickAsync();
            await search;
        }

        [TestMethod]
        public async Task Grid_FirstLoad_ShowsOnePageWithTheTotalAndPager()
        {
            await Expect(Rows).ToHaveCountAsync(10);
            await Expect(Pager).ToContainTextAsync("Total: 35");
            await Expect(Pager.Locator(".layui-laypage-curr")).ToHaveTextAsync("1");
            // 35 rows, 10 per page: four pages
            await Expect(Pager.Locator("a[data-page]:not(.layui-laypage-prev):not(.layui-laypage-next)")).ToHaveCountAsync(3 + 0);
            await Expect(Pager.Locator("a.layui-laypage-last")).ToHaveAttributeAsync("data-page", "4");
        }

        [TestMethod]
        public async Task Paging_NextPageAndPageSize_ChangeTheRows()
        {
            await Expect(Cell(Rows.First, "Name")).ToContainTextAsync("Lab item 01");

            await Pager.Locator("a[data-page='2']").First.ClickAsync();
            await Expect(Pager.Locator(".layui-laypage-curr")).ToHaveTextAsync("2");
            await Expect(Cell(Rows.First, "Name")).ToContainTextAsync("Lab item 11");
            await Expect(Rows).ToHaveCountAsync(10);

            await Pager.Locator("a.layui-laypage-last").ClickAsync();
            await Expect(Rows).ToHaveCountAsync(5);

            // 35 rows at 20 per page: the grid moves to the last page, which holds the remaining 15
            await Pager.Locator(".layui-laypage-limits select").SelectOptionAsync("20");
            await Expect(Pager.Locator(".layui-laypage-curr")).ToHaveTextAsync("2");
            await Expect(Rows).ToHaveCountAsync(15);
            await Pager.Locator("a[data-page='1']").First.ClickAsync();
            await Expect(Rows).ToHaveCountAsync(20);
        }

        [TestMethod]
        public async Task Sorting_ByScore_OrdersAllPagesNotOnlyTheVisibleOnes()
        {
            var header = Body.Locator(".layui-table-header th[data-field=Score]");
            var sorted = Page.WaitForResponseAsync(r => r.Url.Contains("/Lab/Search") && r.Request.Method == "POST");
            await header.Locator(".layui-table-sort-asc").ClickAsync();
            await sorted;
            // scores are (7 * n) mod 31 for n = 1..35; the smallest is 0
            await Expect(Cell(Rows.First, "Score")).ToHaveTextAsync("0");

            sorted = Page.WaitForResponseAsync(r => r.Url.Contains("/Lab/Search") && r.Request.Method == "POST");
            await header.Locator(".layui-table-sort-desc").ClickAsync();
            await sorted;
            await Expect(Cell(Rows.First, "Score")).ToHaveTextAsync("30");
        }

        [TestMethod]
        public async Task SearchPanel_ByNameAndCategory_FiltersTheRows()
        {
            await Name.FillAsync("item 07");
            await SearchAsync();
            await Expect(Rows).ToHaveCountAsync(1);
            await Expect(Cell(Rows.First, "Name")).ToContainTextAsync("Lab item 07");

            await Body.Locator("button[id^=wtResetBtn_]").ClickAsync();
            await Expect(Name).ToHaveValueAsync("");
            await PickSearchComboAsync("Category", "Toy");
            await SearchAsync();
            // categories cycle Toy, Appliance, Tool over n mod 3 = 1, 2, 0 for n = 1..35: Toy has 12 rows
            await Expect(Pager).ToContainTextAsync("Total: 12");
            await Expect(Cell(Rows.First, "Category")).ToHaveTextAsync("Toy");
        }

        // the search value is bound as HTML-encoded text by the framework, so the rows with special characters are found by their plain words
        private async Task SearchSpecialRowsAsync()
        {
            await Name.FillAsync("Special");
            await SearchAsync();
            await Expect(Rows).ToHaveCountAsync(7);
        }

        private async Task PickSearchComboAsync(string property, string option)
        {
            var combo = Body.Locator($"#LabItemListVM_Searcher_{property} xm-select");
            await combo.ClickAsync();
            await combo.Locator(".xm-option", new() { HasTextString = option }).First.ClickAsync();
            await Body.Locator($"label[for=LabItemListVM_Searcher_{property}]").ClickAsync();
        }

        [TestMethod]
        public async Task SearchPanel_Collapse_HidesTheConditions()
        {
            await Expect(Name).ToBeVisibleAsync();
            await Body.Locator(".layui-colla-title").First.ClickAsync(new() { Position = new Position { X = 20, Y = 15 } });
            await Expect(Name).ToBeHiddenAsync();
            await Body.Locator(".layui-colla-title").First.ClickAsync(new() { Position = new Position { X = 20, Y = 15 } });
            await Expect(Name).ToBeVisibleAsync();
        }

        [TestMethod]
        public async Task Grid_NamesWithMarkupCharacters_AreShownAsText()
        {
            await SearchSpecialRowsAsync();

            // rows 5, 10, ... 35 have the special name
            await Expect(Rows).ToHaveCountAsync(7);
            await Expect(Cell(Rows.First, "Name")).ToHaveTextAsync("Special 05 " + LabData.SpecialItem);
            await Expect(Cell(Rows.First, "Note")).ToHaveTextAsync("Special 05 " + LabData.SpecialItem);
            await ExpectNoProbeElementsAsync();
        }

        [TestMethod]
        public async Task RowButton_Details_OpensADialogWithTheRow()
        {
            await Rows.Filter(new() { HasTextString = "Lab item 03" }).Locator("a.layui-btn", new() { HasTextString = "Details" }).ClickAsync();

            await Expect(Dialog).ToContainTextAsync("Lab item 03");
            await Expect(Dialog).ToContainTextAsync("Tool");
            await Dialog.Locator("button", new() { HasTextString = "Close" }).ClickAsync();
            await Expect(Dialog).ToHaveCountAsync(0);
        }

        [TestMethod]
        public async Task CellLink_OpensTheDetailsDialog_AndShowsSpecialNamesAsText()
        {
            await SearchSpecialRowsAsync();

            await Cell(Rows.First, "Note").Locator("a").ClickAsync();

            await Expect(Dialog.Locator("#LabItemVM_Entity_Name")).ToHaveTextAsync("Special 05 " + LabData.SpecialItem);
            await ExpectNoProbeElementsAsync();
        }

        [TestMethod]
        public async Task Export_DownloadsAWorkbookWithTheUnchangedValues()
        {
            await SearchSpecialRowsAsync();

            var download = await Page.RunAndWaitForDownloadAsync(async () => await Body.Locator(".layui-table-tool a.layui-btn", new() { HasTextString = "Export Excel" }).ClickAsync());

            var path = Path.Combine(E2ERun.OutputDir, "downloads", download.SuggestedFilename);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            await download.SaveAsAsync(path);
            var texts = ReadWorkbookTexts(path);
            // the export applies the search conditions: only the seven rows with the special name, one header row
            Assert.AreEqual(7, texts.Count(x => x.StartsWith("Special ")), "Rows that do not match the search must not be exported.");
            Assert.IsFalse(texts.Contains("Lab item 01"));
            CollectionAssert.Contains(texts, "Item Name");
            // the characters that are not tag-like survive (the "Details" column holds markup and is not exported)
            Assert.IsTrue(texts.Any(x => x.StartsWith("Special 05 A&B ") && x.Contains("\"q\" 'q'")));
        }

        [TestMethod]
        public async Task Export_ValueThatLooksLikeATag_IsExportedUnchanged()
        {
            await SearchSpecialRowsAsync();
            var download = await Page.RunAndWaitForDownloadAsync(async () => await Body.Locator(".layui-table-tool a.layui-btn", new() { HasTextString = "Export Excel" }).ClickAsync());
            var path = Path.Combine(E2ERun.OutputDir, "downloads", "tag-like-" + download.SuggestedFilename);
            await download.SaveAsAsync(path);

            CollectionAssert.Contains(ReadWorkbookTexts(path), "Special 05 " + LabData.SpecialItem);
        }

        private static System.Collections.Generic.List<string> ReadWorkbookTexts(string path)
        {
            // the export writes inline strings into the first sheet
            using var zip = ZipFile.OpenRead(path);
            var sheet = zip.Entries.First(e => e.FullName.EndsWith("sheet1.xml"));
            using var reader = new StreamReader(sheet.Open());
            return XDocument.Parse(reader.ReadToEnd()).Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value).ToList();
        }

        [TestMethod]
        public async Task ToolbarDownload_PostsTheSelectedIds_AfterTheUserConfirms()
        {
            // check two rows (the check boxes of the grid sit in a fixed overlay table)
            foreach (var name in new[] { "Lab item 01", "Lab item 02" })
            {
                var index = await Rows.Filter(new() { HasTextString = name }).GetAttributeAsync("data-index");
                await Body.Locator($".layui-table-fixed-l tr[data-index='{index}'] .layui-form-checkbox").ClickAsync();
            }

            await Body.Locator(".layui-table-tool a.layui-btn", new() { HasTextString = "Download selected" }).ClickAsync();
            var confirm = Page.Locator(".layui-layer-dialog");
            await Expect(confirm).ToContainTextAsync("Download the selected rows?");
            var download = await Page.RunAndWaitForDownloadAsync(async () => await confirm.Locator(".layui-layer-btn0").ClickAsync());

            Assert.AreEqual("sample.txt", download.SuggestedFilename);
            var path = Path.Combine(E2ERun.OutputDir, "downloads", "grid-" + download.SuggestedFilename);
            await download.SaveAsAsync(path);
            Assert.AreEqual("ids=2", File.ReadAllText(path));
        }

        [TestMethod]
        public async Task ToolbarDownload_CancelledConfirmation_SendsNothing()
        {
            var posts = 0;
            Page.Request += (_, r) => { if (r.Url.Contains("/Lab/Sample")) posts++; };

            await Body.Locator(".layui-table-tool a.layui-btn", new() { HasTextString = "Download selected" }).ClickAsync();
            var confirm = Page.Locator(".layui-layer-dialog");
            await confirm.Locator(".layui-layer-btn1").ClickAsync();
            await Expect(confirm).ToHaveCountAsync(0);

            Assert.AreEqual(0, posts);
        }

        [TestMethod]
        public async Task SelectionHelpers_ReportTheCheckedAndTheUncheckedRows()
        {
            var gridId = await Body.Locator("table[id^=wtTable_]").GetAttributeAsync("id");
            var index = await Rows.Filter(new() { HasTextString = "Lab item 02" }).GetAttributeAsync("data-index");
            await Body.Locator($".layui-table-fixed-l tr[data-index='{index}'] .layui-form-checkbox").ClickAsync();

            Assert.AreEqual(1, await Page.EvaluateAsync<int>("id => ff.GetSelections(id).length", gridId));
            Assert.AreEqual("Lab item 02", await Page.EvaluateAsync<string>("id => ff.GetSelectionData(id)[0].Name", gridId));
            Assert.AreEqual(9, await Page.EvaluateAsync<int>("id => ff.GetNonSelections(id).length", gridId));
            Assert.AreEqual(9, await Page.EvaluateAsync<int>("id => ff.GetNonSelectionData(id).length", gridId));
            Assert.IsFalse(await Page.EvaluateAsync<bool>("id => ff.GetIsSelectAll(id)", gridId));

            await Body.Locator(".layui-table-fixed-l .layui-table-header .layui-form-checkbox").First.ClickAsync();
            await ExpectScriptAsync($"() => ff.GetIsSelectAll('{gridId}') === true");
            Assert.AreEqual(10, await Page.EvaluateAsync<int>("id => ff.GetSelections(id).length", gridId));
        }

        [TestMethod]
        public async Task ColumnFilter_HidesAColumn()
        {
            var header = Body.Locator(".layui-table-header th[data-field=Category]");
            await Expect(header).ToBeVisibleAsync();

            await Body.Locator(".layui-table-tool-self [lay-event=LAYTABLE_COLS]").ClickAsync();
            await Body.Locator(".layui-table-tool-panel li", new() { HasTextString = "Category" }).Locator(".layui-form-checkbox").ClickAsync();

            await Expect(header).ToBeHiddenAsync();
        }
    }
}
