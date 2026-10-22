using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using WalkingTec.Mvvm.E2E.Pages;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests
{
    /// <summary>
    /// The /_Framework endpoints that the LayUI front end relies on but that have no button of their own in the fixture pages
    /// (they are called by scripts, by the browser, or by pages of downstream applications): called as HTTP requests with the session of
    /// the signed in administrator, and anonymously where the endpoint is open. Also the error page and the language switch.
    /// </summary>
    [TestClass]
    public class FrameworkEndpointTests : LabTest
    {
        private const string Assembly = ", WalkingTec.Mvvm.LayUI.E2EHost";
        private const string LabListVm = "WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.LabVMs.LabItemListVM" + Assembly;
        private const string GadgetVm = "WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.GadgetVMs.GadgetVM" + Assembly;

        // a 1x1 PNG
        private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        private IAPIRequestContext Api => Page.Context.APIRequest;

        private async Task<IAPIRequestContext> AnonymousAsync()
        {
            return await Playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions { BaseURL = Site.BaseUri.ToString() });
        }

        private FilePayload File(string name, byte[] content, string mime = "application/octet-stream") => new FilePayload { Name = name, MimeType = mime, Buffer = content };

        // ---- grid helpers -----------------------------------------------------------------------------------------

        /// <summary>
        /// The reference a page writes for a script of its own that calls the generic data endpoints (the Lab helper action calls
        /// Wtm.CreateGenericEndpointReference for the signed-in user).
        /// </summary>
        private async Task<string> ReferenceAsync(string vm, string purposes, string fields = null)
        {
            var response = await Api.GetAsync("/Lab/GenericReference?vm=" + Uri.EscapeDataString(vm) + "&purposes=" + purposes + (fields == null ? "" : "&fields=" + fields));
            Assert.AreEqual(200, response.Status);
            return await response.TextAsync();
        }

        [TestMethod]
        public async Task GetEmptyData_ReturnsAnEmptyRowOfTheListViewModel()
        {
            var reference = await ReferenceAsync(LabListVm, "List");
            var response = await Api.GetAsync("/_Framework/GetEmptyData?_DONOT_USE_VMNAME=" + Uri.EscapeDataString(LabListVm) + "&_DONOT_USE_VMREF=" + reference);

            Assert.AreEqual(200, response.Status);
            StringAssert.Contains(response.Headers["content-type"], "application/json");
            var row = JsonDocument.Parse(await response.TextAsync()).RootElement;
            Assert.AreEqual(JsonValueKind.Object, row.ValueKind);
            Assert.IsTrue(row.TryGetProperty("Name", out _) && row.TryGetProperty("Score", out _), "The row has the columns of the grid.");
        }

        private async Task<(string Name, string Id)> CreateGadgetAsync(string codePrefix)
        {
            var name = "Prop " + Guid.NewGuid().ToString("N").Substring(0, 8);
            var created = await Api.PostAsync("/Gadget/Create", new APIRequestContextOptions
            {
                Form = Api.CreateFormData().Set("Entity.Name", name).Set("Entity.Code", codePrefix + "-1").Set("Entity.Category", "Tool").Set("Entity.Price", "5")
            });
            Assert.AreEqual(200, created.Status);
            return (name, await FindGadgetAsync(name, "ID"));
        }

        [TestMethod]
        public async Task UpdateModelProperty_AnswersSuccessForAnExistingRecord()
        {
            var gadget = await CreateGadgetAsync("P");
            var reference = await ReferenceAsync(GadgetVm, "Update", "Code");

            var response = await Api.PostAsync("/_Framework/UpdateModelProperty", new APIRequestContextOptions
            {
                Form = Api.CreateFormData().Set("_DONOT_USE_VMNAME", GadgetVm).Set("id", gadget.Id).Set("field", "Code").Set("value", "P-2").Set("_DONOT_USE_VMREF", reference)
            });

            Assert.AreEqual(200, response.Status);
            Assert.AreEqual("Success", JsonDocument.Parse(await response.TextAsync()).RootElement.GetProperty("Data").GetString());
        }

        [TestMethod]
        public async Task UpdateModelProperty_ChangesOneFieldOfARecord()
        {
            var gadget = await CreateGadgetAsync("Q");
            var reference = await ReferenceAsync(GadgetVm, "Update", "Code");

            await Api.PostAsync("/_Framework/UpdateModelProperty", new APIRequestContextOptions
            {
                Form = Api.CreateFormData().Set("_DONOT_USE_VMNAME", GadgetVm).Set("id", gadget.Id).Set("field", "Code").Set("value", "Q-2").Set("_DONOT_USE_VMREF", reference)
            });

            Assert.AreEqual("Q-2", await FindGadgetAsync(gadget.Name, "Code"));
        }

        private async Task<IAPIResponse> UpdateGadgetAsync(string id, string field, string value, string reference)
        {
            var form = Api.CreateFormData().Set("_DONOT_USE_VMNAME", GadgetVm).Set("id", id).Set("field", field).Set("value", value);
            if (reference != null)
            {
                form = form.Set("_DONOT_USE_VMREF", reference);
            }
            return await Api.PostAsync("/_Framework/UpdateModelProperty", new APIRequestContextOptions { Form = form });
        }

        [TestMethod]
        public async Task UpdateModelProperty_AnswersAnErrorAndKeepsTheRecord_WhenTheValueIsNotAccepted()
        {
            var gadget = await CreateGadgetAsync("R");
            var reference = await ReferenceAsync(GadgetVm, "Update", "Code,Name");

            var tooLong = await UpdateGadgetAsync(gadget.Id, "Code", new string('x', 21), reference);
            var missing = await UpdateGadgetAsync(gadget.Id, "Name", "", reference);

            Assert.AreEqual(400, tooLong.Status, "A value the property does not accept is not answered with Success.");
            Assert.AreEqual(400, missing.Status, "A required value cannot be cleared.");
            Assert.AreEqual("R-1", await FindGadgetAsync(gadget.Name, "Code"));
        }

        [TestMethod]
        public async Task UpdateModelProperty_AnswersAnError_ForARecordThatDoesNotExist()
        {
            var reference = await ReferenceAsync(GadgetVm, "Update", "Code");

            var response = await UpdateGadgetAsync(Guid.NewGuid().ToString(), "Code", "S-2", reference);

            Assert.AreEqual(400, response.Status);
            Assert.IsFalse((await response.TextAsync()).Contains("数据不存在"), "The answer is the action's own, not the text of the exception.");
        }

        /// <summary>The columns of a Gadget as stored ("name=value" per line, from the Lab helper).</summary>
        private async Task<Dictionary<string, string>> StoredGadgetAsync(string id)
        {
            var response = await Api.GetAsync("/Lab/GadgetRow?id=" + id);
            Assert.AreEqual(200, response.Status);
            return (await response.TextAsync()).Split('\n').Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => x[1]);
        }

        private static async Task<bool> SavedAsync(IAPIResponse response)
        {
            if (response.Status != 200)
            {
                return false;
            }
            try
            {
                return JsonDocument.Parse(await response.TextAsync()).RootElement.GetProperty("Data").GetString() == "Success";
            }
            catch (JsonException)
            {
                return false;
            }
        }

        [TestMethod]
        public async Task UpdateModelProperty_RefusesAFieldTheReferenceDoesNotList_AndAnotherSpellingOfAListedOne()
        {
            var gadget = await CreateGadgetAsync("U");
            var before = await StoredGadgetAsync(gadget.Id);
            var reference = await ReferenceAsync(GadgetVm, "Update", "Code");

            var notListed = await UpdateGadgetAsync(gadget.Id, "Name", "other", reference);
            var lower = await UpdateGadgetAsync(gadget.Id, "code", "U-2", reference);
            var upper = await UpdateGadgetAsync(gadget.Id, "CODE", "U-2", reference);

            Assert.IsFalse(await SavedAsync(notListed));
            Assert.IsFalse(await SavedAsync(lower));
            Assert.IsFalse(await SavedAsync(upper));
            CollectionAssert.AreEquivalent(before, await StoredGadgetAsync(gadget.Id));
        }

        [TestMethod]
        public async Task UpdateModelProperty_RefusesARequestWithoutAReferenceOfThePurpose()
        {
            var gadget = await CreateGadgetAsync("V");
            var before = await StoredGadgetAsync(gadget.Id);
            var listReference = await ReferenceAsync(GadgetVm, "List", "Code");

            var none = await UpdateGadgetAsync(gadget.Id, "Code", "V-2", null);
            var otherPurpose = await UpdateGadgetAsync(gadget.Id, "Code", "V-2", listReference);

            Assert.IsFalse(await SavedAsync(none));
            Assert.IsFalse(await SavedAsync(otherPurpose));
            CollectionAssert.AreEquivalent(before, await StoredGadgetAsync(gadget.Id));
        }

        [TestMethod]
        public async Task UpdateModelProperty_SetsTheUpdateStamp_AndLeavesOtherColumnsAlone()
        {
            var gadget = await CreateGadgetAsync("W");
            var before = await StoredGadgetAsync(gadget.Id);
            var reference = await ReferenceAsync(GadgetVm, "Update", "Code");

            var response = await UpdateGadgetAsync(gadget.Id, "Code", "W-2", reference);
            var after = await StoredGadgetAsync(gadget.Id);

            Assert.IsTrue(await SavedAsync(response));
            Assert.AreEqual("W-2", after["Code"]);
            Assert.IsFalse(string.IsNullOrEmpty(after["UpdateTime"]), "The update time is set.");
            Assert.IsTrue(Math.Abs((DateTime.Now - DateTime.Parse(after["UpdateTime"], null, System.Globalization.DateTimeStyles.RoundtripKind)).TotalMinutes) < 10, "The update time is current.");
            Assert.AreEqual(before["CreateBy"], after["UpdateBy"], "The update is stamped with the user who made it.");
            foreach (var column in new[] { "Name", "Category", "Price", "LaunchDate", "InStock", "CreateBy", "CreateTime" })
            {
                Assert.AreEqual(before[column], after[column], column + " is not changed.");
            }
        }

        [TestMethod]
        [DataRow("CreateBy", "someone")]
        [DataRow("CreateTime", "2020-01-01T00:00:00")]
        [DataRow("UpdateBy", "someone")]
        [DataRow("UpdateTime", "2020-01-01T00:00:00")]
        [DataRow("ID", "11111111-1111-1111-1111-111111111111")]
        public async Task UpdateModelProperty_NeverWritesTheKeyOrTheStamps_EvenWhenTheReferenceListsThem(string field, string value)
        {
            var gadget = await CreateGadgetAsync("X");
            var before = await StoredGadgetAsync(gadget.Id);
            var reference = await ReferenceAsync(GadgetVm, "Update", field);

            var response = await UpdateGadgetAsync(gadget.Id, field, value, reference);

            Assert.AreEqual(400, response.Status);
            CollectionAssert.AreEquivalent(before, await StoredGadgetAsync(gadget.Id));
        }

        [TestMethod]
        [DataRow("Price", "abc")]
        [DataRow("Price", "")]
        [DataRow("Price", "1e5")]
        [DataRow("LaunchDate", "not a date")]
        [DataRow("InStock", "maybe")]
        [DataRow("Category", "Nothing")]
        [DataRow("Category", "99")]
        public async Task UpdateModelProperty_AnswersAnError_WhenTheTextIsNotAValueOfTheProperty(string field, string value)
        {
            var gadget = await CreateGadgetAsync("Y");
            var before = await StoredGadgetAsync(gadget.Id);
            var reference = await ReferenceAsync(GadgetVm, "Update", field);

            var response = await UpdateGadgetAsync(gadget.Id, field, value, reference);

            Assert.AreEqual(400, response.Status, "The text is not turned into a default value.");
            CollectionAssert.AreEquivalent(before, await StoredGadgetAsync(gadget.Id));
        }

        [TestMethod]
        public async Task UpdateModelProperty_SavesValuesOfOtherTypes_AndEmptyForANullableOne()
        {
            var gadget = await CreateGadgetAsync("Z");
            var reference = await ReferenceAsync(GadgetVm, "Update", "Price,LaunchDate,InStock,Category");

            Assert.IsTrue(await SavedAsync(await UpdateGadgetAsync(gadget.Id, "Price", "12.5", reference)));
            Assert.IsTrue(await SavedAsync(await UpdateGadgetAsync(gadget.Id, "LaunchDate", "2026-03-04T05:06:07", reference)));
            Assert.IsTrue(await SavedAsync(await UpdateGadgetAsync(gadget.Id, "InStock", "true", reference)));
            Assert.IsTrue(await SavedAsync(await UpdateGadgetAsync(gadget.Id, "Category", "Toy", reference)));
            var stored = await StoredGadgetAsync(gadget.Id);
            Assert.AreEqual("12.5", stored["Price"]);
            StringAssert.StartsWith(stored["LaunchDate"], "2026-03-04T05:06:07");
            Assert.AreEqual("True", stored["InStock"]);
            Assert.AreEqual("Toy", stored["Category"]);

            Assert.IsTrue(await SavedAsync(await UpdateGadgetAsync(gadget.Id, "LaunchDate", "", reference)));
            Assert.AreEqual("", (await StoredGadgetAsync(gadget.Id))["LaunchDate"], "Empty text clears a nullable property.");
        }

        [TestMethod]
        public async Task UpdateModelProperty_AnswersAnError_ForAFieldThatCannotBeChanged()
        {
            var gadget = await CreateGadgetAsync("T");
            var reference = await ReferenceAsync(GadgetVm, "Update", "ID,Nothing");

            var key = await UpdateGadgetAsync(gadget.Id, "ID", Guid.NewGuid().ToString(), reference);
            var unknown = await UpdateGadgetAsync(gadget.Id, "Nothing", "x", reference);

            Assert.AreEqual(400, key.Status, "The key of a record is not changed.");
            Assert.AreEqual(400, unknown.Status, "A field the record does not have is not answered with Success.");
            Assert.AreEqual(gadget.Id, await FindGadgetAsync(gadget.Name, "ID"));
        }

        private async Task<string> FindGadgetAsync(string name, string column)
        {
            var search = await Api.PostAsync("/Gadget/Search", new APIRequestContextOptions { Form = Api.CreateFormData().Set("Name", name).Set("Page", "1").Set("Limit", "10") });
            var data = JsonDocument.Parse(await search.TextAsync()).RootElement.GetProperty("Data");
            Assert.AreEqual(1, data.GetArrayLength(), "Expected the created row to be found.");
            return data[0].GetProperty(column).GetString();
        }

        // ---- access, redirects ------------------------------------------------------------------------------------

        [TestMethod]
        public async Task IsAccessable_AnswersForTheSignedInUserAndRefusesAnonymousCallers()
        {
            Assert.AreEqual("true", (await (await Api.GetAsync("/_Framework/IsAccessable?url=%2FGadget%2FIndex")).TextAsync()).Trim().ToLowerInvariant());
            Assert.AreEqual("false", (await (await Api.GetAsync("/_Framework/IsAccessable?url=%2FNowhere%2FIndex")).TextAsync()).Trim().ToLowerInvariant());

            await using var anonymous = await AnonymousAsync();
            Assert.AreEqual(401, (await anonymous.GetAsync("/_Framework/IsAccessable?url=%2FGadget%2FIndex")).Status);
        }

        [TestMethod]
        public async Task Redirect_AndRedirect401_AnswerWithoutBody_AndUnauthorized()
        {
            var redirect = await Api.GetAsync("/_Framework/Redirect");
            Assert.AreEqual(200, redirect.Status);
            Assert.AreEqual("", await redirect.TextAsync());

            await using var anonymous = await AnonymousAsync();
            Assert.AreEqual(401, (await anonymous.GetAsync("/_Framework/Redirect401")).Status);
        }

        [TestMethod]
        public async Task OutSide_WrapsAnAllowedPageInAFrame_AndRefusesOthers()
        {
            var allowed = await Api.GetAsync("/_Framework/OutSide?url=%2FGadget%2FIndex");
            Assert.AreEqual(200, allowed.Status);
            var html = await allowed.TextAsync();
            StringAssert.Contains(html, "<iframe src='/Gadget/Index'");
            StringAssert.Contains(html, "<title>Inventory - Gadgets</title>");

            var refused = await Api.GetAsync("/_Framework/OutSide?url=%2FNowhere%2FIndex");
            Assert.AreEqual(400, refused.Status);
        }

        [TestMethod]
        public async Task OutSide_QuoteCharacterInTheUrl_StaysInsideTheAttribute()
        {
            // a path of an action that every signed in user may call, followed by a quote and more text; the framework's string binder
            // HTML-encodes the parameter, so the quote cannot end the src attribute of the frame
            var response = await Api.GetAsync("/_Framework/OutSide?url=" + Uri.EscapeDataString("/Lab/Pane' data-x='y"));

            Assert.AreEqual(200, response.Status);
            var html = await response.TextAsync();
            Assert.IsFalse(Regex.IsMatch(html, "<iframe[^>]*'\\s+data-x='y'"), "The quote ended the attribute: " + html);
            StringAssert.Contains(html, "&#39;");
        }

        // ---- verify code, language, tenant ------------------------------------------------------------------------

        [TestMethod]
        public async Task GetVerifyCode_ReturnsAPngImage()
        {
            await using var anonymous = await AnonymousAsync();
            var response = await anonymous.GetAsync("/_Framework/GetVerifyCode");

            Assert.AreEqual(200, response.Status);
            StringAssert.StartsWith(response.Headers["content-type"], "image/png");
            var bytes = await response.BodyAsync();
            Assert.AreEqual(0x89, bytes[0]);
            Assert.AreEqual((byte)'P', bytes[1]);
        }

        [TestMethod]
        public async Task SetLanguageForBlazor_SetsTheCultureCookie_AndNavigatesToTheRedirectTarget()
        {
            await using var anonymous = await AnonymousAsync();
            var response = await anonymous.GetAsync("/_Framework/SetLanguageForBlazor?culture=en&redirect=" + Uri.EscapeDataString("/after it's done"));

            Assert.AreEqual(200, response.Status);
            StringAssert.Contains(Uri.UnescapeDataString(response.Headers["set-cookie"]), "c=en|uic=en");
            // the redirect target is written into the script as a JavaScript string: the quote in it is escaped
            var html = await response.TextAsync();
            StringAssert.StartsWith(html, "<script>window.location.href='/after it");
            Assert.IsFalse(html.Contains("it's done"), "The quote must not end the string literal.");
        }

        private async Task<string> SelectorDialogAsync(Func<string, string> value)
        {
            var reference = await ReferenceAsync(LabListVm, "Selector");
            var form = Api.CreateFormData()
                .Set("_DONOT_USE_VMNAME", LabListVm)
                .Set("_DONOT_USE_VMREF", reference)
                .Set("_DONOT_USE_KFIELD", value("Name"))
                .Set("_DONOT_USE_VFIELD", "ID")
                .Set("_DONOT_USE_FIELD", value("Entity.Items"))
                .Set("_DONOT_USE_MULTI_SEL", "true")
                .Set("_DONOT_USE_SEL_ID", value("Entity_Items"))
                .Set("_DONOT_USE_SUBMIT", value("onPick"))
                .Set("_DONOT_USE_LINK_FIELD", "Entity_Other")
                .Set("_DONOT_USE_TRIGGER_URL", value("/Api/Changed"));
            var response = await Api.PostAsync("/_Framework/Selector", new APIRequestContextOptions { Form = form });
            Assert.AreEqual(200, response.Status);
            return await response.TextAsync();
        }

        [TestMethod]
        public async Task Selector_OrdinaryRequestValues_AreWrittenIntoTheDialogScriptAsTheyAre()
        {
            var html = await SelectorDialogAsync(v => v);

            StringAssert.Contains(html, "multiSel = true");
            StringAssert.Contains(html, "displayStrArr.push(selected[i]['Name']);");
            StringAssert.Contains(html, "name='Entity.Items' value='\" + ff.EncodeHtml(selected[i]['ID']) + \"' />");
            StringAssert.Contains(html, "onPick(selected);");
            StringAssert.Contains(html, "$(\"#Entity_Items_Display\").val(");
            StringAssert.Contains(html, "var url = '/Api/Changed?t='");
            StringAssert.Contains(html, "url += '&id=' +encodeURIComponent(selected[i]['ID']);");
            StringAssert.Contains(html, "ff.ChainChange(url, $('#Entity_Items_Display')[0]);");
        }

        [TestMethod]
        public async Task Selector_RequestValuesWithSpecialCharacters_StayInsideTheirStringInTheDialogScript()
        {
            // the model binder already HTML-encodes quotes and angle brackets of the request; a backslash, a line break and a line separator
            // are not touched by it and would end or change a script string literal
            const string probe = "\\\n\u2028Z";
            var html = await SelectorDialogAsync(v => v + probe);

            StringAssert.Contains(html, "displayStrArr.push(selected[i]['Name\\\\\\n\\u2028Z']);");
            StringAssert.Contains(html, "var url = '/Api/Changed\\\\\\n\\u2028Z?t='");
            StringAssert.Contains(html, "$('#Entity_Items\\\\\\n\\u2028Z_Display')");
            StringAssert.Contains(html, "$('#Entity_Items\\\\\\n\\u2028Z_Container input[type=hidden]')");
            StringAssert.Contains(html, "name='Entity.Items\\\\\\n\\u2028Z' value='");
            Assert.IsFalse(html.Contains("\u2028"), "A line separator was written into the page.");
            Assert.IsFalse(html.Contains("Name\\\n"), "A backslash was written in front of a line break inside a string literal.");
            // a submit handler that is not a function name is not written as code at all
            Assert.IsFalse(html.Contains("onPick"), "A submit handler value that is not a function name must not be written as code.");
            Assert.IsFalse(html.Contains("(selected);;"), "No call is written for a submit handler that is not a function name.");
        }

        [TestMethod]
        public async Task SetTenant_WithoutTenants_AnswersWithAReloadScript()
        {
            var response = await Api.GetAsync("/_Framework/SetTenant?tenant=");

            Assert.AreEqual(200, response.Status);
            StringAssert.Contains(await response.TextAsync(), "location.reload();");
        }

        [TestMethod]
        public async Task Login_WhenAlreadySignedIn_StillVerifiesThePassword()
        {
            // the session of the test is the signed-in administrator: posting the login form again must check the credentials it carries
            var wrong = await Api.PostAsync("/Login/Login", new APIRequestContextOptions
            {
                Form = Api.CreateFormData().Set("ITCode", Site.AdminAccount).Set("Password", "not-the-password"),
                MaxRedirects = 0
            });
            Assert.AreEqual(200, wrong.Status, "The login page is shown again.");

            var right = await Api.PostAsync("/Login/Login", new APIRequestContextOptions
            {
                Form = Api.CreateFormData().Set("ITCode", Site.AdminAccount).Set("Password", Site.AdminPassword),
                MaxRedirects = 0
            });
            Assert.AreEqual(302, right.Status);
        }

        [TestMethod]
        public async Task SetTenant_Anonymous_IsSentToTheSignInPage_AndNothingIsSwitched()
        {
            await using var anonymous = await AnonymousAsync();
            var response = await anonymous.GetAsync("/_Framework/SetTenant?tenant=t1");

            Assert.AreEqual(200, response.Status);
            var body = await response.TextAsync();
            StringAssert.Contains(body, "window.location.href=", "The caller is sent to the sign-in page.");
            Assert.IsFalse(body.Contains("location.reload();"), "An anonymous caller must not get the answer of a successful switch.");
            Assert.IsFalse(response.Headers.ContainsKey("set-cookie"), "No cookie is issued.");
        }

        [TestMethod]
        public async Task SetTenant_SignedIn_StaysAGetAndAnswersWithAReloadScript()
        {
            // downstream layouts call it with a GET and the tenant of the signed-in user (empty for the main tenant)
            var response = await Api.GetAsync("/_Framework/SetTenant?tenant=");

            Assert.AreEqual(200, response.Status);
            StringAssert.Contains(await response.TextAsync(), "location.reload();");
            Assert.IsTrue(response.Headers.ContainsKey("set-cookie"), "The sign-in cookie is refreshed.");
        }

        [TestMethod]
        public async Task LanguageMenu_ChoosingALanguage_StoresTheCultureAndReloadsThePage()
        {
            await new HomeFrame(Page).GotoAsync();
            await Page.Locator("#LAY_app .layui-nav-item", new() { HasTextString = "English" }).First.HoverAsync();
            var change = Page.WaitForResponseAsync(r => r.Url.Contains("/_Framework/SetLanguage"));
            await FlushJsCoverageAsync();
            var reload = Page.WaitForLoadStateAsync(LoadState.Load);
            await Page.Locator("a", new() { HasTextString = "English" }).Last.ClickAsync();
            var response = await change;

            Assert.AreEqual(200, response.Status);
            // the script that came back reloads the page; the culture cookie is in the browser
            await Expect(Page.Locator("#frontpage")).ToBeVisibleAsync();
            var cookies = await Context.CookiesAsync();
            Assert.IsTrue(cookies.Any(c => c.Name == ".AspNetCore.Culture" && Uri.UnescapeDataString(c.Value).Contains("uic=en")), "The culture cookie was not stored.");
        }

        [TestMethod]
        public async Task UEditorOptions_ReturnsTheEditorConfiguration()
        {
            await using var anonymous = await AnonymousAsync();
            var response = await anonymous.GetAsync("/_Framework/UEditorOptions");

            Assert.AreEqual(200, response.Status);
            StringAssert.Contains(response.Headers["content-type"], "application/json");
            Assert.AreEqual(JsonValueKind.Object, JsonDocument.Parse(await response.TextAsync()).RootElement.ValueKind);
        }

        // ---- editors and files ------------------------------------------------------------------------------------

        [TestMethod]
        public async Task RichTextBoxUpload_StoresTheImageAndAnswersWithItsAddress()
        {
            var response = await Api.PostAsync("/_Framework/UploadForLayUIRichTextBox", new APIRequestContextOptions
            {
                Multipart = Api.CreateFormData().Set("file", File("inline.png", Png, "image/png"))
            });

            Assert.AreEqual(200, response.Status);
            var json = JsonDocument.Parse(await response.TextAsync()).RootElement;
            Assert.AreEqual(0, json.GetProperty("code").GetInt32());
            var src = json.GetProperty("data").GetProperty("src").GetString();
            StringAssert.StartsWith(src, "/_Framework/GetFile?id=");
            Assert.AreEqual(200, (await Api.GetAsync(src)).Status);
        }

        [TestMethod]
        public async Task UEditorUpload_AcceptsAFileAndABase64Image()
        {
            var file = await Api.PostAsync("/_Framework/UploadForLayUIUEditor", new APIRequestContextOptions
            {
                Multipart = Api.CreateFormData().Set("upfile", File("editor.png", Png, "image/png"))
            });
            Assert.AreEqual(200, file.Status);
            var json = JsonDocument.Parse(await file.TextAsync()).RootElement;
            Assert.AreEqual(200, json.GetProperty("Code").GetInt32());
            StringAssert.StartsWith(json.GetProperty("Data").GetProperty("src").GetString(), "/_Framework/GetFile?id=");

            var scrawl = await Api.PostAsync("/_Framework/UploadForLayUIUEditor", new APIRequestContextOptions
            {
                Form = Api.CreateFormData().Set("FileID", Convert.ToBase64String(Png))
            });
            Assert.AreEqual(200, scrawl.Status);
            Assert.AreEqual(200, JsonDocument.Parse(await scrawl.TextAsync()).RootElement.GetProperty("Code").GetInt32());
        }

        [TestMethod]
        public async Task UEditorUpload_AnswersWithValidJson_ForAFileNameWithQuotesAndBackslashes()
        {
            var response = await Api.PostAsync("/_Framework/UploadForLayUIUEditor", new APIRequestContextOptions
            {
                Multipart = Api.CreateFormData().Set("upfile", File("we'ird,{x}:1.png", Png, "image/png"))
            });

            Assert.AreEqual(200, response.Status);
            var data = JsonDocument.Parse(await response.TextAsync()).RootElement.GetProperty("Data");
            StringAssert.Contains(data.GetProperty("FileName").GetString(), "ird");
            Assert.AreEqual(JsonValueKind.String, data.GetProperty("src").ValueKind);
            Assert.AreEqual(2, data.EnumerateObject().Count(), "No property was added by the file name.");
        }

        [TestMethod]
        public async Task UEditorTag_Renders_AContainerAndAnEscapedInitialContent()
        {
            // the editor scripts are not part of the shared static files, so only the markup the tag helper writes is checked
            var response = await Api.GetAsync("/Lab/Ueditor");

            Assert.AreEqual(200, response.Status);
            var html = await response.TextAsync();
            StringAssert.Contains(html, "isrich=\"1\"");
            StringAssert.Contains(html, "loadEditor('LabFormVM_Rich')");
            // the initial content is a JavaScript string literal: neither a raw quote nor a closing script tag may appear in it
            var literal = Regex.Match(html, "setContent\\('(.*?)'\\)\\}\\)", RegexOptions.Singleline).Groups[1].Value;
            Assert.IsTrue(literal.Length > 0, "The initial content was not found.");
            Assert.IsFalse(literal.Contains("</script>", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(Regex.IsMatch(literal, "(?<!\\\\)'"), "An unescaped quote ends the string.");
        }

        [TestMethod]
        public async Task ViewFile_ChoosesTheViewerByTheFileType()
        {
            var image = await UploadAsync("view.png", Png);
            var pdf = await UploadAsync("view.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 test"));

            var imageHtml = await (await Api.GetAsync(await WithRefAsync("/_Framework/ViewFile/" + image, image))).TextAsync();
            StringAssert.Contains(imageHtml, "<img id='FileObject'");
            var pdfHtml = await (await Api.GetAsync(await WithRefAsync("/_Framework/ViewFile/" + pdf, pdf))).TextAsync();
            StringAssert.Contains(pdfHtml, "<embed");
            StringAssert.Contains(pdfHtml, "application/pdf");
        }

        [TestMethod]
        public async Task GetFile_AsDownload_SetsTheFileNameAndContentType()
        {
            var id = await UploadAsync("download me.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 test"));

            var response = await Api.GetAsync(await WithRefAsync("/_Framework/GetFile/" + id, id));

            Assert.AreEqual(200, response.Status);
            Assert.AreEqual("application/pdf", response.Headers["content-type"]);
            StringAssert.Contains(response.Headers["content-disposition"], "download");
        }

        private async Task<string> UploadAsync(string name, byte[] content)
        {
            var response = await Api.PostAsync("/_Framework/Upload", new APIRequestContextOptions { Multipart = Api.CreateFormData().Set("file", File(name, content)) });
            Assert.AreEqual(200, response.Status);
            return JsonDocument.Parse(await response.TextAsync()).RootElement.GetProperty("Data").GetProperty("Id").GetString();
        }

        // ---- errors -----------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task ActionThatThrows_IsAnsweredByTheFrameworkErrorHandlerWithoutDetails()
        {
            var response = await Page.GotoAsync("/Lab/Fail");

            // /_Framework/Error answers with 400 and the message only: no stack trace of the host (the test host runs without IsQuickDebug)
            Assert.AreEqual(400, response.Status);
            var body = await Page.Locator("body").InnerTextAsync();
            StringAssert.Contains(body, "Lab: simulated failure");
            Assert.IsFalse(body.Contains("LabController"), "The page must not show where the exception came from.");
            Assert.IsFalse(body.Contains(" at "), "The page must not show a stack trace.");
        }

        [TestMethod]
        public async Task ErrorHandler_AnswersAFailedCallWithTheMessageAsPlainText()
        {
            var response = await Api.GetAsync("/Lab/FailWith?text=" + Uri.EscapeDataString("something went wrong"));

            Assert.AreEqual(400, response.Status);
            StringAssert.Contains(response.Headers["content-type"], "text/plain");
            Assert.AreEqual("something went wrong", await response.TextAsync());
        }
    }
}
