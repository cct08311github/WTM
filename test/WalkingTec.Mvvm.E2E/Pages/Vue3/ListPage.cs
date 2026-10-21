using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Pages.Vue3
{
    /// <summary>
    /// A list page of the Vue3Demo: collapsible search panel, toolbar, Element Plus table and the dialogs the page opens.
    /// All list pages are built from the same components, so one page object serves them all.
    /// </summary>
    public sealed class ListPage
    {
        private readonly IPage _page;

        public ListPage(IPage page, string route)
        {
            _page = page;
            Route = route;
        }

        public string Route { get; }

        public ILocator Rows => _page.Locator(".el-table__body-wrapper tr.el-table__row");
        public ILocator Row(string text) => Rows.Filter(new() { HasTextString = text });
        public ILocator Toolbar => _page.Locator(".layout-padding-auto > .el-card__body > div[style*='right']");
        public ILocator Dialog => _page.Locator(".el-dialog:visible");
        /// <summary>The newest message toast (earlier ones fade out on their own).</summary>
        public ILocator Toast => _page.Locator(".el-message").Last;

        /// <summary>The form item with this label, for controls that are not labelled inputs (select, switch, cascader).</summary>
        public ILocator DialogItem(string label) => Dialog.Locator(".el-form-item").Filter(new() { Has = _page.Locator("label", new() { HasTextString = label }) });

        public async Task OpenAsync(Vue3App app)
        {
            await app.GoToAsync(Route);
            await Expect(_page.Locator(".el-table__body-wrapper")).ToBeVisibleAsync();
            // the first search of the page is still running while the loading mask is shown
            await Expect(_page.Locator(".el-table .el-loading-mask")).ToHaveCountAsync(0);
        }

        /// <summary>
        /// A button by its visible text. The icon font adds a generated character to the accessible name of buttons that have
        /// an icon, so the name is not usable here; the text content is.
        /// </summary>
        public static ILocator ButtonIn(ILocator scope, string text) =>
            scope.Locator("button").Filter(new() { HasTextRegex = new Regex("^\\s*" + Regex.Escape(text) + "\\s*$") });

        public ILocator ToolbarButton(string name) => ButtonIn(Toolbar, name);
        public ILocator RowButton(string rowText, string name) => ButtonIn(Row(rowText), name);

        /// <summary>Expands the search panel when it is collapsed and returns the field with this label.</summary>
        public async Task<ILocator> SearchFieldAsync(string label)
        {
            var field = _page.Locator(".search-form").GetByLabel(label, new() { Exact = true });
            if (await field.First.IsVisibleAsync() == false)
            {
                await _page.Locator(".card-header").First.ClickAsync();
            }
            return field.First;
        }

        public async Task SearchAsync(string label, string value)
        {
            var field = await SearchFieldAsync(label);
            await field.FillAsync(value);
            await ClickSearchAsync();
        }

        /// <summary>Clicks Search and waits for the answer of the search request, so that the rows that follow are the filtered ones.</summary>
        public async Task ClickSearchAsync()
        {
            await _page.RunAndWaitForResponseAsync(
                async () => await ButtonIn(_page.Locator(".card-header"), "Search").ClickAsync(),
                response => response.Request.Method == "POST" && response.Url.EndsWith("/search", System.StringComparison.OrdinalIgnoreCase));
            await Expect(_page.Locator(".el-table .el-loading-mask")).ToHaveCountAsync(0);
        }

        public async Task ResetSearchAsync()
        {
            await ButtonIn(_page.Locator(".card-header"), "Reset").ClickAsync();
            await ClickSearchAsync();
        }

        /// <summary>A form item of the search panel by its label.</summary>
        public ILocator SearchItem(string label) => _page.Locator(".search-form .el-form-item").Filter(new() { Has = _page.Locator("label", new() { HasTextString = label }) });

        public ILocator DialogField(string label) => Dialog.GetByLabel(label, new() { Exact = true });
        public ILocator DialogButton(string name) => ButtonIn(Dialog, name);

        public async Task SubmitDialogAsync()
        {
            await DialogButton("Submit").ClickAsync();
            await Expect(Dialog).ToBeHiddenAsync();
        }

        public async Task CheckRowAsync(string rowText)
        {
            await Row(rowText).Locator(".el-checkbox").ClickAsync();
            await Expect(Row(rowText).Locator("input.el-checkbox__original")).ToBeCheckedAsync();
        }

        /// <summary>Row level delete: the button opens a confirmation popover.</summary>
        public async Task DeleteRowAsync(string rowText)
        {
            await RowButton(rowText, "Delete").ClickAsync();
            await _page.Locator(".el-popconfirm:visible .el-button--primary").ClickAsync();
            await Expect(Row(rowText)).ToHaveCountAsync(0);
        }

        public async Task WaitForEmptyAsync() => await Expect(Rows).ToHaveCountAsync(0);

        /// <summary>
        /// The import round trip every list has: open the import dialog, download the template with the dialog button, add the given
        /// rows to it, upload the file and submit. Returns the template that was downloaded.
        /// </summary>
        public async Task<byte[]> ImportAsync(string importButtonText, params string[][] rows)
        {
            await ToolbarButton(importButtonText).ClickAsync();
            var download = await _page.RunAndWaitForDownloadAsync(async () => await ButtonIn(Dialog, "Download template").ClickAsync());
            var template = await ReadDownloadAsync(download);
            await Dialog.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
            {
                Name = "import.xlsx",
                MimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                Buffer = Support.ExcelTemplate.WithRows(template, rows)
            });
            await Expect(Dialog.Locator(".el-upload-list__item.is-success")).ToBeVisibleAsync();
            await DialogButton("Submit").ClickAsync();
            await Expect(Dialog).ToBeHiddenAsync();
            return template;
        }

        public static async Task<byte[]> ReadDownloadAsync(IDownload download)
        {
            var path = await download.PathAsync();
            return System.IO.File.ReadAllBytes(path);
        }
    }
}
