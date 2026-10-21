using System.Threading.Tasks;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Pages
{
    public sealed class LoginPage
    {
        private readonly IPage _page;

        public LoginPage(IPage page)
        {
            _page = page;
        }

        public ILocator Error => _page.Locator("#login-error");

        public async Task GotoAsync()
        {
            await _page.GotoAsync("/Login/Login");
            await Expect(_page.Locator("#login-submit")).ToBeVisibleAsync();
        }

        public async Task SubmitAsync(string account, string password)
        {
            await _page.Locator("#login-account").FillAsync(account);
            await _page.Locator("#login-password").FillAsync(password);
            await _page.Locator("#login-submit").ClickAsync();
        }

        /// <summary>
        /// Logs in and waits until the LayUI home frame is fully rendered: user name, menu and the front page of the first tab.
        /// Opening a menu page before the front page is rendered races with layuiadmin's initial routing.
        /// </summary>
        public async Task LoginAsync(string account, string password)
        {
            await GotoAsync();
            await SubmitAsync(account, password);
            await Expect(_page.Locator("#layout-user-name")).ToBeVisibleAsync();
            await Expect(_page.Locator("#frontpage")).ToBeVisibleAsync();
        }
    }
}
