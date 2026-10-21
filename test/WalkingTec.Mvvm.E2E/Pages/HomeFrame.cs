using System.Threading.Tasks;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Pages
{
    /// <summary>The layuiadmin frame: header, side menu, page tabs and the body of the active tab.</summary>
    public sealed class HomeFrame
    {
        private readonly IPage _page;

        public HomeFrame(IPage page)
        {
            _page = page;
        }

        public ILocator UserName => _page.Locator("#layout-user-name");
        public ILocator Menu => _page.Locator("#LAY-system-side-menu");
        public ILocator Body => _page.Locator("#LAY_app_body .layadmin-tabsbody-item.layui-show");
        public ILocator Tabs => _page.Locator("#LAY_app_tabsheader li");

        // exact text: "Lab" must not also match "Lab Inputs"
        public ILocator MenuLink(string text) => Menu.Locator("a").Filter(new() { Has = _page.Locator("cite", new() { HasTextRegex = new System.Text.RegularExpressions.Regex("^\\s*" + System.Text.RegularExpressions.Regex.Escape(text) + "\\s*$") }) });

        /// <summary>Opens the home page (the session comes from the cookies of the context) and waits for the front page tab to be rendered.</summary>
        public async Task GotoAsync()
        {
            await _page.GotoAsync("/");
            await Expect(_page.Locator("#frontpage")).ToBeVisibleAsync();
        }

        /// <summary>Expands the folder menu entry and opens the page entry below it.</summary>
        public async Task OpenMenuPageAsync(string folder, string page)
        {
            await MenuLink(folder).ClickAsync();
            await MenuLink(page).ClickAsync();
        }

        public async Task LogoutAsync()
        {
            await _page.Locator("#layout-logout").ClickAsync();
            await Expect(_page.Locator("#login-submit")).ToBeVisibleAsync();
        }
    }
}
