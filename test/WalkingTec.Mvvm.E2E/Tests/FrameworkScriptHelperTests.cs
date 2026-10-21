using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests
{
    /// <summary>
    /// The helper functions of framework_layui.js (the global ff object) that pages of downstream applications call directly: value
    /// encoding, conversion of server item lists for the combo, tree and transfer components, cookies, form and grid selection helpers.
    /// They run in the real page of the LayUI host.
    /// </summary>
    [TestClass]
    public class FrameworkScriptHelperTests : LabTest
    {
        private async Task<T> RunAsync<T>(string script, object arg = null)
        {
            return await Page.EvaluateAsync<T>(script, arg);
        }

        [TestInitialize]
        public async Task OpenHome()
        {
            await Lab.Frame.GotoAsync();
        }

        [TestMethod]
        public async Task EncodeHtml_EncodesTheFiveMarkupCharacters_AndLeavesOtherTextAlone()
        {
            Assert.AreEqual("&amp;&lt;&gt;&quot;&#39; 測試 100%", await RunAsync<string>("() => ff.EncodeHtml('&<>\"\\' 測試 100%')"));
            Assert.AreEqual("", await RunAsync<string>("() => ff.EncodeHtml(null)"));
            Assert.AreEqual("", await RunAsync<string>("() => ff.EncodeHtml(undefined)"));
            Assert.AreEqual("12", await RunAsync<string>("() => ff.EncodeHtml(12)"));
        }

        [TestMethod]
        public async Task Guid_ReturnsADifferentIdentifierEachTime()
        {
            var ids = await RunAsync<string[]>("() => [ff.guid(), ff.guid(), ff.guid()]");

            Assert.AreEqual(3, ids.Distinct().Count());
            foreach (var id in ids)
            {
                StringAssert.Matches(id, new System.Text.RegularExpressions.Regex("^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$"));
            }
        }

        [TestMethod]
        public async Task Cookies_CanBeStoredAndReadBack()
        {
            await Page.EvaluateAsync("() => ff.SetCookie('e2e-helper', 'value one', false)");

            Assert.AreEqual("value one", await RunAsync<string>("() => ff.GetCookie('e2e-helper', false)"));
            Assert.IsNull(await RunAsync<string>("() => ff.GetCookie('e2e-not-there', false)"));
        }

        [TestMethod]
        public async Task ConcatWhereStr_AppendsTheNamedValuesOfTheRowToTheUrl()
        {
            Assert.AreEqual("/x?1=1&a=1&b=two", await RunAsync<string>("() => ff.concatWhereStr('/x?1=1', ['a', 'b'], { a: 1, b: 'two', c: 3 })"));
            Assert.AreEqual("/x?1=1", await RunAsync<string>("() => ff.concatWhereStr('/x?1=1', ['a'], null)"));
            Assert.AreEqual("", await RunAsync<string>("() => ff.concatWhereStr(null, null, null)"));
        }

        [TestMethod]
        public async Task ItemConverters_MapServerListsToTheShapesOfTheComponents()
        {
            var json = await RunAsync<string>(@"() => {
                const data = [
                    { Value: 'a', Text: 'A & B', Disabled: false, Selected: false, Icon: 'i1' },
                    { Value: 'b', Text: 'B', Disabled: true, Selected: true, Children: [{ Value: 'c', Text: 'C', Selected: false }] }
                ];
                return JSON.stringify({
                    tree: ff.getTreeItems(data, ['c']),
                    combo: ff.getComboItems(data, ['a'], false, undefined),
                    comboDefaults: ff.getComboItems(data, ['a'], true, undefined),
                    comboDisabled: ff.getComboItems(data, [], false, true),
                    transfer: ff.getTransferItems(data, ['a']),
                    checkedLeaves: ff.getTreeChecked([{ id: 'x', children: [{ id: 'y' }, { id: 'z', children: [{ id: 'w' }] }] }, { id: 'v' }])
                });
            }");
            var r = JsonDocument.Parse(json).RootElement;

            // tree: names and values come from Text / Value, a selected value marks the matching node, children are converted too
            Assert.AreEqual("A & B", r.GetProperty("tree")[0].GetProperty("name").GetString());
            Assert.IsTrue(r.GetProperty("tree")[1].GetProperty("selected").GetBoolean());
            Assert.IsTrue(r.GetProperty("tree")[1].GetProperty("children")[0].GetProperty("selected").GetBoolean());
            // combo: Selected of the item or a selected value; with useDefaultValue only the values count
            Assert.IsTrue(r.GetProperty("combo")[0].GetProperty("selected").GetBoolean());
            Assert.IsTrue(r.GetProperty("combo")[1].GetProperty("selected").GetBoolean());
            Assert.IsFalse(r.GetProperty("comboDefaults")[1].GetProperty("selected").GetBoolean());
            Assert.IsTrue(r.GetProperty("comboDisabled")[0].GetProperty("disabled").GetBoolean());
            // transfer: value and title
            Assert.AreEqual("a", r.GetProperty("transfer")[0].GetProperty("value").GetString());
            Assert.AreEqual("A & B", r.GetProperty("transfer")[0].GetProperty("title").GetString());
            Assert.IsTrue(r.GetProperty("transfer")[1].GetProperty("disabled").GetBoolean());
            // the leaves of a tree
            CollectionAssert.AreEqual(new[] { "y", "w", "v" }, r.GetProperty("checkedLeaves").EnumerateArray().Select(x => x.GetString()).ToArray());
        }

        [TestMethod]
        public async Task FormHelpers_ReadTheValuesOfAForm()
        {
            await Lab.OpenAsync("Inputs");
            var formId = await Body.Locator("form").GetAttributeAsync("id");
            await Lab.Field("Title").FillAsync(LabData.Typed);

            var json = await RunAsync<string>("id => JSON.stringify({ data: ff.GetFormData(id), array: ff.GetFormArray(id), post: ff.GetPostData(id) })", formId);
            var r = JsonDocument.Parse(json).RootElement;

            Assert.AreEqual(LabData.Typed, r.GetProperty("data").GetProperty("Title").GetString());
            Assert.AreEqual("30", r.GetProperty("data").GetProperty("Level").GetString());
            // the array form lists name / value pairs of the fields that have a value
            Assert.IsTrue(r.GetProperty("array").EnumerateArray().Any(x => x.GetProperty("name").GetString() == "Title" && x.GetProperty("value").GetString() == LabData.Typed));
            // the post data is a url encoded string: decoding the value of Title gives the text back
            var segment = r.GetProperty("post").GetString().Split('&').First(x => x.StartsWith("Title="));
            Assert.AreEqual(LabData.Typed, System.Uri.UnescapeDataString(segment.Substring("Title=".Length).Replace("+", " ")));
        }

        [TestMethod]
        public async Task ResetForm_ClearsTheTextFields()
        {
            await Lab.OpenAsync("Inputs");
            var formId = await Body.Locator("form").GetAttributeAsync("id");
            await Expect(Lab.Field("Title")).ToHaveValueAsync(LabData.Special);

            await Page.EvaluateAsync("id => ff.resetForm(id)", formId);

            await Expect(Lab.Field("Title")).ToHaveValueAsync("");
            await Expect(Lab.Field("Memo")).ToHaveValueAsync(LabData.Special);
        }

        [TestMethod]
        public async Task SelectorParameters_SetByScript_AreSentWhenThePopupOpens()
        {
            await Lab.OpenAsync("Pickers");
            await Page.EvaluateAsync("() => ff.setSelectorPara('LabFormVM_GadgetId', { 'Searcher.Name': 'Alpha' })");

            await Body.Locator("#LabFormVM_GadgetId_Select").ClickAsync();

            var dialog = Page.Locator(".layui-layer-page");
            await Expect(dialog.Locator(".layui-table-main tr", new() { HasTextString = "Alpha Wrench" })).ToHaveCountAsync(1);
            await Expect(dialog.Locator(".layui-table-main tr", new() { HasTextString = "Beta Spinner" })).ToHaveCountAsync(0);
        }

        private async Task PickAlphaAsync()
        {
            await Body.Locator("#LabFormVM_GadgetId_Select").ClickAsync();
            var dialog = Page.Locator(".layui-layer-page");
            var index = await dialog.Locator(".layui-table-main tr", new() { HasTextString = "Alpha Wrench" }).GetAttributeAsync("data-index");
            await dialog.Locator($".layui-table-fixed-l tr[data-index='{index}'] .layui-form-checkbox").ClickAsync();
            await dialog.Locator("button", new() { HasTextString = "OK" }).ClickAsync();
            await Expect(Body.Locator("#LabFormVM_GadgetId_Display")).ToHaveValueAsync("Alpha Wrench");
        }

        [TestMethod]
        public async Task ClearSelector_EmptiesTheDisplayedText()
        {
            await Lab.OpenAsync("Pickers");
            await PickAlphaAsync();

            await Page.EvaluateAsync("() => ff.clearSelector('LabFormVM_GadgetId')");

            await Expect(Body.Locator("#LabFormVM_GadgetId_Display")).ToHaveValueAsync("");
        }

        [TestMethod]
        [Ignore("Pending issue: clearSelector looks for the hidden id fields in a container named literally id_Container instead of the container of the selector, so the ids stay in the form and are posted.")]
        public async Task ClearSelector_RemovesTheStoredIds()
        {
            await Lab.OpenAsync("Pickers");
            await PickAlphaAsync();

            await Page.EvaluateAsync("() => ff.clearSelector('LabFormVM_GadgetId')");

            await Expect(Body.Locator("#LabFormVM_GadgetId_Container input[type=hidden]")).ToHaveCountAsync(0);
        }
    }
}
