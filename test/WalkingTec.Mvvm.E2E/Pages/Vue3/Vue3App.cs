using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Pages.Vue3
{
    /// <summary>
    /// The Vue3Demo single page app: login page and the application frame (header, side menu, tabs).
    /// The app starts in Chinese. Tests run in English so that labels are readable; <see cref="OpenLoginAsync"/> switches the
    /// stored setting before the first screen is used. The real language switch through the header is tested separately.
    /// </summary>
    public sealed class Vue3App
    {
        private const string ThemeConfigKey = "wtm-vue:themeConfig";
        private readonly IPage _page;

        public Vue3App(IPage page)
        {
            _page = page;
        }

        // ---- login page ----
        public ILocator AccountInput => _page.Locator(".login-content-form input[type=text]").First;
        public ILocator PasswordInput => _page.Locator(".login-content-form input[type=password]");
        public ILocator TenantInput => _page.Locator(".login-content-form input[type=text]").Nth(1);
        public ILocator LoginButton => _page.Locator(".login-content-submit");

        // ---- frame ----
        public ILocator UserMenu => _page.Locator(".layout-navbars-breadcrumb-user-link");
        public ILocator LanguageMenu => _page.Locator(".layout-navbars-breadcrumb-user-icon .fa-globe");
        public ILocator SideMenu => _page.Locator(".layout-container .el-menu").First;
        public ILocator Toast => _page.Locator(".el-message");

        /// <summary>
        /// Opens the app at the login page in English. The first load stores the theme settings (language included) in
        /// localStorage; they are switched to English and the page is reloaded, so that every later screen is in English.
        /// </summary>
        public async Task OpenLoginAsync()
        {
            // Runs before the app on every load of this tab, but only switches the stored language once (the marker lives in
            // sessionStorage, which survives reloads), so that a language chosen later in the test is kept.
            await _page.AddInitScriptAsync($@"(() => {{
                try {{
                    const key = '{ThemeConfigKey}';
                    const raw = localStorage.getItem(key);
                    if (raw && sessionStorage.getItem('e2e-language-set') === null) {{
                        const cfg = JSON.parse(raw);
                        cfg.globalI18n = 'en';
                        localStorage.setItem(key, JSON.stringify(cfg));
                        sessionStorage.setItem('e2e-language-set', '1');
                    }}
                }} catch (e) {{ }}
            }})();");
            await _page.GotoAsync("/");
            await Expect(LoginButton).ToBeVisibleAsync();
            await _page.WaitForFunctionAsync($"() => localStorage.getItem('{ThemeConfigKey}') !== null");
            await _page.ReloadAsync();
            await Expect(LoginButton).ToBeVisibleAsync();
            await Expect(PasswordInput).ToHaveAttributeAsync("placeholder", new Regex("^[A-Za-z]"));
        }

        public async Task SubmitLoginAsync(string account, string password, string tenant = null)
        {
            await AccountInput.FillAsync(account);
            await PasswordInput.FillAsync(password);
            if (tenant != null)
            {
                await TenantInput.FillAsync(tenant);
            }
            await LoginButton.ClickAsync();
        }

        /// <summary>Signs in and waits until the frame shows the signed in user.</summary>
        public async Task LoginAsync(string account, string password, string expectedUserName = "Admin")
        {
            await OpenLoginAsync();
            await SubmitLoginAsync(account, password);
            await Expect(UserMenu).ToContainTextAsync(expectedUserName);
        }

        /// <summary>
        /// Chooses an entry of the user menu. The menu opens on hover and closes when the pointer leaves it, and the page
        /// underneath animates, so the click is sent to the entry directly once the menu is shown.
        /// </summary>
        public async Task ChooseUserMenuAsync(string entry)
        {
            await UserMenu.HoverAsync();
            var item = _page.Locator(".el-dropdown-menu__item").Filter(new() { HasTextString = entry });
            await Expect(item).ToBeVisibleAsync();
            await item.DispatchEventAsync("click");
        }

        public async Task LogoutAsync()
        {
            await ChooseUserMenuAsync("Log out");
            await _page.Locator(".el-message-box__btns .el-button--primary").ClickAsync();
            await Expect(LoginButton).ToBeVisibleAsync();
        }

        /// <summary>Routes inside the running app (no page load), for example "/_admin/frameworkuser".</summary>
        public async Task GoToAsync(string route)
        {
            await _page.EvaluateAsync("route => { location.hash = '#' + route; }", route);
            await Expect(_page).ToHaveURLAsync(new Regex(Regex.Escape("#" + route) + "($|\\?)"));
        }
    }
}
