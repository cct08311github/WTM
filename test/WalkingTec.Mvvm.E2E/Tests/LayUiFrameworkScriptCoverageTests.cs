using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests
{
    /// <summary>
    /// The parts of framework_layui.js the other LayUI tests do not reach (#317): the startsWith polyfill, ff.LoadPage for an outside
    /// address and for a new window, the chart callbacks of a dialog (maximize, restore, drag resize), the load failure of the selector
    /// dialog (ff.OpenDialog2) and the two public combo box helpers without a caller in the framework (ff.changeComboIcon,
    /// ff.refreshcombobox). Every test checks what the user or the page can observe, not only that the function ran.
    /// </summary>
    [TestClass]
    public class LayUiFrameworkScriptCoverageTests : LabTest
    {
        private ILocator Dialog => Page.Locator(".layui-layer-page");

        // ---- String.prototype.startsWith polyfill ---------------------------------------------------------------

        [TestMethod]
        public async Task StartsWithPolyfill_WhenTheBrowserHasNoStartsWith_TheFrameworkInstallsItAndTheGridSearchWorks()
        {
            // runs before the scripts of every page (and frame) of the context: the browser looks like one without the method
            await Context.AddInitScriptAsync("delete String.prototype.startsWith;");
            var errors = new List<string>();
            Page.PageError += (_, e) => errors.Add(e);

            await Lab.OpenAsync("Grid");

            // the framework script found no function and put its own in place
            Assert.IsFalse(await Page.EvaluateAsync<bool>("() => String.prototype.startsWith.toString().includes('[native code]')"), "The native method was not removed.");
            Assert.IsTrue(await Page.EvaluateAsync<bool>("() => 'Searcher.Name'.startsWith('Searcher.') && !'Searcher.Name'.startsWith('Name') && 'abc'.startsWith('')"));

            // ff.GetSearchFormData strips the prefix of the search form with startsWith: a search by name still finds the one row
            var rows = Body.Locator(".layui-table-main tr");
            await Expect(rows.First).ToBeVisibleAsync();
            await Body.Locator("#LabItemListVM_Searcher_Name").FillAsync("item 07");
            var search = Page.WaitForResponseAsync(r => r.Url.Contains("/Lab/Search") && r.Request.Method == "POST");
            await Body.Locator("a[issearchbutton]").ClickAsync();
            await search;
            await Expect(rows).ToHaveCountAsync(1);
            await Expect(rows.First.Locator("td[data-field=Name]")).ToContainTextAsync("Lab item 07");
            CollectionAssert.AreEqual(new string[0], errors, "The page reported script errors: " + string.Join(" | ", errors));
        }

        // ---- ff.LoadPage ----------------------------------------------------------------------------------------

        [TestMethod]
        public async Task LoadPage_OutsideUrl_EncodesTheInnerUrlBeforeItGoesIntoTheHash()
        {
            await Lab.OpenAsync("Layout");

            // the menu writes outside entries this way; the inner address (a page of this site) has its own query string
            await Page.EvaluateAsync("() => ff.LoadPage('/_framework/outside?url=/Lab/Pane?id=a&b=c%20d')");

            // '&' and '?' of the inner address must not become part of the outer one: the whole inner address is one encoded value
            var expected = "#/_framework/outside?url=" + Uri.EscapeDataString("/Lab/Pane?id=a&b=c d");
            await ExpectScriptAsync($"() => location.hash === {System.Text.Json.JsonSerializer.Serialize(expected)}");
            await Expect(Page.Locator(".layui-layer-loading")).ToHaveCountAsync(0);
        }

        [TestMethod]
        public async Task LoadPage_OutsideUrl_ShowsThePageOfTheInnerUrl()
        {
            await Lab.OpenAsync("Layout");

            await Page.EvaluateAsync("() => ff.LoadPage('/_framework/outside?url=/Lab/Pane?id=outside-e2e')");

            // /_Framework/OutSide wraps the page in a frame in the page tab (it refuses addresses that are not an action of the site)
            var frame = Page.FrameLocator("iframe[src*='/Lab/Pane']");
            await Expect(frame.Locator("#lab-pane")).ToHaveTextAsync("pane outside-e2e");
        }

        [TestMethod]
        public async Task LoadPage_NewWindow_OpensTheHostPageWithTheUrlInTheHashAndSetsTheTitle()
        {
            await ServeHostPageAsync();
            await Lab.OpenAsync("Layout");
            var hashBefore = await Page.EvaluateAsync<string>("() => location.hash");

            var popup = await Page.RunAndWaitForPopupAsync(async () =>
                await Page.EvaluateAsync("() => ff.LoadPage('/Lab/Pane?id=popup', true, 'E2E popup title')"));

            // the new window gets the page host with the address after the hash; the title is set by the opener after a short delay
            await popup.WaitForLoadStateAsync();
            Assert.AreEqual(new Uri(Page.Url).GetLeftPart(UriPartial.Authority) + "/Home/PIndex/#/Lab/Pane?id=popup", popup.Url);
            await Expect(popup).ToHaveTitleAsync("E2E popup title");
            // the opener stays where it is and has no loading mask left
            Assert.AreEqual(hashBefore, await Page.EvaluateAsync<string>("() => location.hash"));
            await Expect(Page.Locator(".layui-layer-loading")).ToHaveCountAsync(0);
        }

        [TestMethod]
        public async Task LoadPage_WithPostData_AlsoOpensANewWindow()
        {
            await ServeHostPageAsync();
            await Lab.OpenAsync("Layout");

            // a page that is given post data (para) always opens in a new window, whatever the second argument says
            var popup = await Page.RunAndWaitForPopupAsync(async () =>
                await Page.EvaluateAsync("() => ff.LoadPage('/Lab/Pane?id=para', false, 'Window with data', { Title: 'x' })"));

            await popup.WaitForLoadStateAsync();
            StringAssert.EndsWith(popup.Url, "/Home/PIndex/#/Lab/Pane?id=para");
            await Expect(popup).ToHaveTitleAsync("Window with data");
        }

        /// <summary>
        /// /Home/PIndex is the page of an application that shows one address in its own window; the framework does not ship it and the
        /// test host has none. The context answers for it, so the new window has a document of the site whose title the opener can set.
        /// </summary>
        private async Task ServeHostPageAsync()
        {
            await Context.RouteAsync(new Regex("/Home/PIndex", RegexOptions.IgnoreCase), route => route.FulfillAsync(new()
            {
                Status = 200,
                ContentType = "text/html; charset=utf-8",
                Body = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>E2E page host</title></head><body><div id=\"e2e-pindex\">page host</div></body></html>"
            }));
        }

        // ---- charts in a dialog: maximize, restore, drag resize -------------------------------------------------

        private static readonly string[] ChartIds = { "labchartbar", "labchartline", "labchartpie", "labchartscatter" };

        /// <summary>
        /// Opens /Lab/Charts in a dialog that has the maximize icon. The tag helper gives the charts a fixed width, which a resize cannot
        /// change, so a style rule makes them follow the width of their container (the way a chart in a fluid layout does).
        /// </summary>
        private async Task OpenChartDialogAsync()
        {
            await Lab.OpenAsync("Layout");
            // the chart tag helper uses the global echarts, which the host loads as a layuiadmin extension after the home page is up
            await ExpectScriptAsync("() => typeof window.echarts !== 'undefined'");
            await Page.AddStyleTagAsync(new() { Content = "div[ischart='1'] { width: auto !important; }" });

            await Page.EvaluateAsync("() => ff.OpenDialog('/Lab/Charts', 'e2e-chart-dialog', 'Charts', 700, 420)");

            await Expect(Dialog).ToBeVisibleAsync();
            // the window opens with a scale animation: wait until it has its real size before aiming at its icons and handle
            await ExpectScriptAsync("() => { const t = getComputedStyle(document.querySelector('.layui-layer-page')).transform; return t === 'none' || t === 'matrix(1, 0, 0, 1, 0, 0)'; }");
            foreach (var id in ChartIds)
            {
                await ExpectScriptAsync($"() => {{ const c = window.{id}Chart; return !!c && c.getWidth() > 0 && !!document.querySelector('#{id} canvas'); }}");
            }
        }

        /// <summary>
        /// Script that is true when every chart of the dialog is exactly as wide as its container (echarts only follows its container
        /// when it is told to resize) and the width <c>w</c> satisfies the condition.
        /// </summary>
        private static string ChartsFit(string widthCondition)
        {
            var ids = System.Text.Json.JsonSerializer.Serialize(ChartIds);
            return $"() => {{ const ids = {ids}; return ids.every(id => {{ const w = document.getElementById(id).clientWidth; return window[id + 'Chart'].getWidth() === w && ({widthCondition}); }}); }}";
        }

        /// <summary>The width the charts have in the dialog as it was opened.</summary>
        private async Task<int> InitialChartWidthAsync()
        {
            await ExpectScriptAsync(ChartsFit("w > 300"));
            return await Page.EvaluateAsync<int>("() => document.getElementById('labchartbar').clientWidth");
        }

        [TestMethod]
        public async Task Dialog_WithCharts_Maximize_ResizesTheCharts()
        {
            await OpenChartDialogAsync();
            var before = await InitialChartWidthAsync();

            await Page.Locator(".layui-layer-max").ClickAsync();

            // nothing else redraws a chart: the full callback of the dialog must resize every chart to the larger window
            await ExpectScriptAsync(ChartsFit($"w > {before + 300}"));
        }

        [TestMethod]
        public async Task Dialog_WithCharts_Restore_ResizesTheChartsBack()
        {
            await OpenChartDialogAsync();
            var before = await InitialChartWidthAsync();
            await Page.Locator(".layui-layer-max").ClickAsync();
            await ExpectScriptAsync(ChartsFit($"w > {before + 300}"));

            // after maximizing, the same icon restores the window
            await Page.Locator(".layui-layer-maxmin").ClickAsync();

            await ExpectScriptAsync(ChartsFit($"w === {before}"));
        }

        [TestMethod]
        public async Task Dialog_WithCharts_DragResize_ResizesTheChartsWithTheWindow()
        {
            await OpenChartDialogAsync();
            var before = await InitialChartWidthAsync();

            var handle = await Page.Locator(".layui-layer-resize").BoundingBoxAsync();
            var x = handle.X + handle.Width / 2;
            var y = handle.Y + handle.Height / 2;
            await Page.Mouse.MoveAsync(x, y);
            await Page.Mouse.DownAsync();
            await Page.Mouse.MoveAsync(x + 150, y + 60, new() { Steps = 5 });
            await Page.Mouse.UpAsync();

            await ExpectScriptAsync(ChartsFit($"w > {before + 100}"));
        }

        // ---- ff.OpenDialog2: the selector dialog cannot be loaded -----------------------------------------------

        [TestMethod]
        public async Task SelectorDialog_WhenTheServerAnswersWithAnError_ShowsTheAnswerAndRestoresTheWindowIds()
        {
            await Lab.OpenAsync("Pickers");
            await Page.EvaluateAsync("() => ff.SetCookie('windowids', 'e2e-outer')");
            string idsWhileLoading = null;
            await Page.RouteAsync(new Regex("/_Framework/Selector(\\?|$)", RegexOptions.IgnoreCase), async route =>
            {
                // the dialog has registered itself in the cookie while its content is on the way
                idsWhileLoading = await Page.EvaluateAsync<string>("() => ff.GetCookie('windowids')");
                await route.FulfillAsync(new() { Status = 500, ContentType = "text/plain; charset=utf-8", Body = "Selector is not available today" });
            });

            await Body.Locator("#LabFormVM_GadgetId_Select").ClickAsync();

            await AcceptAlertAsync("Selector is not available today");
            Assert.IsNotNull(idsWhileLoading);
            StringAssert.StartsWith(idsWhileLoading, "e2e-outer,");
            // the failed window is taken out of the list of open windows again; no window was opened and the loading mask is gone
            Assert.AreEqual("e2e-outer", await Page.EvaluateAsync<string>("() => ff.GetCookie('windowids')"));
            await Expect(Dialog).ToHaveCountAsync(0);
            await Expect(Page.Locator(".layui-layer-loading")).ToHaveCountAsync(0);
        }

        [TestMethod]
        public async Task SelectorDialog_WhenTheAnswerHasNoText_ShowsTheFrameworkLoadFailedMessage()
        {
            await Lab.OpenAsync("Pickers");
            await Page.RouteAsync(new Regex("/_Framework/Selector(\\?|$)", RegexOptions.IgnoreCase),
                route => route.FulfillAsync(new() { Status = 503, ContentType = "text/plain; charset=utf-8", Body = "" }));
            var expected = await Page.EvaluateAsync<string>("() => ff.DONOTUSE_Text_LoadFailed");
            Assert.IsFalse(string.IsNullOrEmpty(expected), "The framework has no load failed text.");

            await Body.Locator("#LabFormVM_GadgetId_Select").ClickAsync();

            await AcceptAlertAsync(expected);
            await Expect(Dialog).ToHaveCountAsync(0);
        }

        // ---- combo box helpers without a caller in the framework ------------------------------------------------

        [TestMethod]
        public async Task ChangeComboIcon_AsTheSelectHandler_ShowsTheIconOfTheChosenOption()
        {
            await Lab.OpenAsync("Layout");
            // a layui select whose options carry an icon; the helper is the handler of the select event, the way a page of an application wires it
            await Page.EvaluateAsync(@"async () => {
                await new Promise(resolve => layui.use('form', resolve));
                const host = document.createElement('div');
                host.id = 'e2e-icon-host';
                host.innerHTML = '<form class=""layui-form"" style=""width:300px;padding:10px""><select id=""e2e-icon-select"" lay-filter=""e2eIcon"">' +
                    '<option value="""">choose</option>' +
                    '<option value=""star"" icon=""layui-icon layui-icon-star"">Star</option>' +
                    '<option value=""plain"">Plain</option></select></form>';
                document.querySelector('#LAY_app_body .layadmin-tabsbody-item.layui-show').appendChild(host);
                layui.form.on('select(e2eIcon)', ff.changeComboIcon);
                layui.form.render('select');
            }");
            var combo = Body.Locator("#e2e-icon-host .layui-form-select");
            var title = combo.Locator(".layui-select-title");
            var icon = title.Locator("._wtm-combo-icon");
            await Expect(icon).ToHaveCountAsync(0);

            await title.ClickAsync();
            await combo.Locator("dd[lay-value=star]").ClickAsync();

            // the icon of the option is put in front of the input, which makes room for it
            await Expect(icon).ToHaveCountAsync(1);
            await Expect(icon).ToHaveClassAsync(new Regex("layui-icon-star"));
            await Expect(title.Locator("input")).ToHaveCSSAsync("padding-left", "30px");

            // an option without an icon takes it away again and gives the input its own padding back
            await title.ClickAsync();
            await combo.Locator("dd[lay-value=plain]").ClickAsync();

            await Expect(icon).ToHaveCountAsync(0);
            await Expect(title.Locator("input")).Not.ToHaveCSSAsync("padding-left", "30px");
        }

        [TestMethod]
        public async Task ChangeComboIcon_AnIconValueWithQuotes_StaysInTheClassAttribute()
        {
            await Lab.OpenAsync("Layout");
            await Page.EvaluateAsync(@"async () => {
                await new Promise(resolve => layui.use('form', resolve));
                const host = document.createElement('div');
                host.id = 'e2e-icon-host';
                host.innerHTML = '<form class=""layui-form"" style=""width:300px;padding:10px""><select id=""e2e-icon-select"" lay-filter=""e2eIcon"">' +
                    '<option value="""">choose</option>' +
                    '<option value=""odd"" icon=""layui-icon-star&quot; data-e2e=&quot;1"">Odd</option></select></form>';
                document.querySelector('#LAY_app_body .layadmin-tabsbody-item.layui-show').appendChild(host);
                layui.form.on('select(e2eIcon)', ff.changeComboIcon);
                layui.form.render('select');
            }");
            var combo = Body.Locator("#e2e-icon-host .layui-form-select");
            var title = combo.Locator(".layui-select-title");

            await title.ClickAsync();
            await combo.Locator("dd[lay-value=odd]").ClickAsync();

            // one icon element whose class holds the whole text of the attribute, and no attribute made from it
            var icon = title.Locator("._wtm-combo-icon");
            await Expect(icon).ToHaveCountAsync(1);
            await Expect(icon).ToHaveAttributeAsync("class", "_wtm-combo-icon layui-icon-star\" data-e2e=\"1");
            await Expect(icon).Not.ToHaveAttributeAsync("data-e2e", new Regex(".*"));
        }

        [TestMethod]
        public async Task Refreshcombobox_PassesTheSettingsAndTheItemsOnToFormSelects()
        {
            await Lab.OpenAsync("Layout");
            // layui.formSelects is an add-on the framework only calls into: the test host does not load it, so a stand in records the call
            await Page.EvaluateAsync("() => { window.e2eFormSelectsCalls = []; layui.formSelects = { on: function (options) { window.e2eFormSelectsCalls.push(options); } }; }");

            await Page.EvaluateAsync(@"() => ff.refreshcombobox(
                { layFilter: 'e2eCombo', url: '/Lab/Cities', self: 'self-e2e', targetname: 'City', linkto: 'LabFormVM_City', cf: 'cf-e2e', selectFunc: 'pick' },
                [{ name: 'North', value: 'n1' }])");

            var call = await Page.EvaluateAsync<System.Text.Json.JsonElement>("() => JSON.parse(JSON.stringify(window.e2eFormSelectsCalls))");
            Assert.AreEqual(1, call.GetArrayLength(), "formSelects.on must be called once.");
            var options = call[0];
            Assert.AreEqual("e2eCombo", options.GetProperty("layFilter").GetString());
            Assert.AreEqual("/Lab/Cities", options.GetProperty("url").GetString());
            Assert.AreEqual("self-e2e", options.GetProperty("self").GetString());
            Assert.AreEqual("City", options.GetProperty("targetname").GetString());
            Assert.AreEqual("LabFormVM_City", options.GetProperty("linkto").GetString());
            Assert.AreEqual("cf-e2e", options.GetProperty("cf").GetString());
            Assert.AreEqual("pick", options.GetProperty("selectFunc").GetString());
            // the items are handed over as they are, the separators are fixed
            Assert.AreEqual(1, options.GetProperty("arr").GetArrayLength());
            Assert.AreEqual("n1", options.GetProperty("arr")[0].GetProperty("value").GetString());
            Assert.AreEqual(",", options.GetProperty("separator").GetString());
            Assert.AreEqual("", options.GetProperty("left").GetString());
            Assert.AreEqual("", options.GetProperty("right").GetString());
        }
    }
}
