using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Pages
{
    /// <summary>
    /// The Lab fixture pages of the test host (/Lab/*): one page per group of LayUI tag helpers, opened through the side menu
    /// like any other module. The page content is rendered into the active tab of the layuiadmin frame.
    /// </summary>
    public sealed class LabPage
    {
        private readonly IPage _page;
        private readonly HomeFrame _frame;

        public LabPage(IPage page)
        {
            _page = page;
            _frame = new HomeFrame(page);
        }

        public IPage Page => _page;
        public HomeFrame Frame => _frame;
        public ILocator Body => _frame.Body;

        /// <summary>Opens the home frame (the session comes from the test's storage state) and then the named Lab page.</summary>
        public async Task OpenAsync(string name)
        {
            await _frame.GotoAsync();
            await _frame.OpenMenuPageAsync("Lab", "Lab " + name);
            await Expect(Body.Locator("#lab-" + name.ToLowerInvariant())).ToBeAttachedAsync();
        }

        /// <summary>The element ids the tag helpers derive from a view model property: {ViewModelType}_{Property}.</summary>
        public ILocator Field(string property) => Body.Locator("#LabFormVM_" + property);

        /// <summary>Raw form of the last post of a page, as the server received it (name to values).</summary>
        public async Task<Dictionary<string, string[]>> LastPostAsync(string page)
        {
            var response = await _page.Context.APIRequest.GetAsync("/Lab/LastPost?page=" + page);
            return await response.JsonAsync<Dictionary<string, string[]>>() ?? new Dictionary<string, string[]>();
        }

        /// <summary>Clicks the form button with the given text and waits for the post to the server to come back.</summary>
        public async Task SubmitAsync(string buttonText, string urlPart)
        {
            var post = _page.WaitForResponseAsync(r => r.Url.Contains(urlPart) && r.Request.Method == "POST");
            await Body.Locator("button[type=submit]", new() { HasTextString = buttonText }).ClickAsync();
            await post;
        }
    }
}
