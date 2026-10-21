using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Pages
{
    /// <summary>
    /// The Gadget list page (generated from the LayUI code generator templates): search panel, layui table and the
    /// create / edit / delete / details dialogs that open as layer pages above the table.
    /// </summary>
    public sealed class GadgetListPage
    {
        private readonly IPage _page;
        private readonly HomeFrame _frame;

        public GadgetListPage(IPage page)
        {
            _page = page;
            _frame = new HomeFrame(page);
        }

        private ILocator Body => _frame.Body;
        public ILocator Rows => Body.Locator(".layui-table-main tr");
        public ILocator Dialog => _page.Locator(".layui-layer-page");

        public ILocator Row(string name) => Rows.Filter(new() { HasTextString = name });

        public async Task OpenAsync()
        {
            await _frame.OpenMenuPageAsync("Inventory", "Gadgets");
            await Expect(Body.Locator("#GadgetListVM_Searcher_Name")).ToBeVisibleAsync();
            await Expect(Rows.First).ToBeVisibleAsync();
        }

        public async Task SearchByNameAsync(string name)
        {
            await Body.Locator("#GadgetListVM_Searcher_Name").FillAsync(name);
            var search = _page.WaitForResponseAsync(r => r.Url.Contains("/Gadget/Search") && r.Request.Method == "POST");
            await Body.Locator("a[issearchbutton]").ClickAsync();
            await search;
        }

        /// <summary>Every button of the grid's toolbar.</summary>
        public ILocator ToolbarButtons => Body.Locator(".layui-table-tool a.layui-btn");

        public ILocator ToolbarButton(string text) => Body.Locator(".layui-table-tool a.layui-btn").Filter(new() { HasTextString = text });

        public ILocator RowButton(string name, string text) => Row(name).Locator("a.layui-btn", new() { HasTextString = text });

        public async Task CheckRowAsync(string name)
        {
            // layui renders the checkbox column in a fixed-left overlay table; the row there has the same data-index
            var index = await Row(name).GetAttributeAsync("data-index");
            await Body.Locator($".layui-table-fixed-l tr[data-index='{index}'] .layui-form-checkbox").ClickAsync();
        }

        // ---- dialog helpers -------------------------------------------------------------------------------------

        public ILocator DialogField(string property) => Dialog.Locator($"input[name='Entity.{property}']:not(.xm-select-default)");

        public ILocator DialogButton(string text) => Dialog.Locator("button", new() { HasTextString = text }).First;

        public async Task SubmitDialogAsync()
        {
            await DialogButton("Submit").ClickAsync();
            await Expect(Dialog).ToHaveCountAsync(0);
        }

        /// <summary>Waits for the framework's alert ("N rows have been deleted") and closes it.</summary>
        public async Task AcceptAlertAsync(string expectedText)
        {
            var alert = _page.Locator(".layui-layer-dialog");
            await Expect(alert).ToContainTextAsync(expectedText);
            await alert.Locator(".layui-layer-btn0").ClickAsync();
            await Expect(alert).ToHaveCountAsync(0);
        }

        public async Task CreateAsync(string name, string code, string price)
        {
            await ToolbarButton("Create").ClickAsync();
            await Expect(DialogField("Name")).ToBeVisibleAsync();
            await DialogField("Name").FillAsync(name);
            await DialogField("Code").FillAsync(code);
            await DialogField("Price").FillAsync(price);
            await SubmitDialogAsync();
        }

        public static Regex Text(string text) => new Regex(Regex.Escape(text));
    }
}
