using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using WalkingTec.Mvvm.E2E.Security;

namespace WalkingTec.Mvvm.E2E.Tests.Security
{
    /// <summary>
    /// The generic data endpoints of the framework (/_Framework/GetPagingData, Selector, GetExportExcel, GetExcelTemplate, GetEmptyData,
    /// UpdateModelProperty) serve a view model to a caller who holds the privilege of the page that shows it, with the reference that page
    /// wrote, and to nobody else. Plain HTTP with the four identities of the permission matrix: anonymous, an account without role, an
    /// account with a limited role (Gadget list and read-only actions) and an administrator.
    /// </summary>
    [TestClass]
    [TestCategory("generic-endpoint")]
    public class GenericEndpointAuthorizationTests
    {
        private const string Assembly = ", WalkingTec.Mvvm.LayUI.E2EHost";
        private const string GadgetListVm = "WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.GadgetVMs.GadgetListVM" + Assembly;
        private const string GadgetImportVm = "WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.GadgetVMs.GadgetImportVM" + Assembly;
        private const string GadgetVmName = "WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.GadgetVMs.GadgetVM" + Assembly;
        private const string LabListVm = "WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.LabVMs.LabItemListVM" + Assembly;

        private static HttpClient _http;
        private static string _noPrivilegeText;

        private static async Task<MatrixIdentity> IdentityAsync(string name)
        {
            var site = await E2ERun.GetSiteAsync(SiteTargets.LayUIMatrix);
            if (_http == null)
            {
                _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = site.BaseUri, Timeout = TimeSpan.FromSeconds(60) };
                _noPrivilegeText = RefusalTexts.Load(site.Target.RepoRoot).NoPrivilege;
            }
            return (await MatrixAccounts.LayUiAsync(site)).Single(x => x.Name == name);
        }

        private static async Task<(HttpStatusCode Status, string Body, string ContentType)> SendAsync(string identity, HttpMethod method, string url, IDictionary<string, string> form = null, bool gridRequest = false)
        {
            var who = await IdentityAsync(identity);
            using var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("Accept-Language", "en");
            if (gridRequest)
            {
                request.Headers.TryAddWithoutValidation("layuisearch", "true");
            }
            who.Authorize?.Invoke(request);
            if (form != null)
            {
                request.Content = new FormUrlEncodedContent(form);
            }
            using var response = await _http.SendAsync(request);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            var contentType = response.Content.Headers.ContentType?.MediaType;
            var isText = contentType == null || contentType.StartsWith("text/") || contentType.Contains("json");
            return (response.StatusCode, isText ? Encoding.UTF8.GetString(bytes) : $"[{bytes.Length} bytes]", contentType);
        }

        private static Dictionary<string, string> GridForm(string vm, string reference, string cs = "")
        {
            var form = new Dictionary<string, string> { ["_DONOT_USE_VMNAME"] = vm, ["_DONOT_USE_CS"] = cs, ["Page"] = "1", ["Limit"] = "10", ["SearcherMode"] = "Search" };
            if (reference != null)
            {
                form["_DONOT_USE_VMREF"] = reference;
            }
            return form;
        }

        private static int CodeOf(string json) => JsonDocument.Parse(json).RootElement.GetProperty("Code").GetInt32();

        /// <summary>The settings object (JSON) the grid writes into the page as its "where" filter.</summary>
        private static Dictionary<string, string> FilterIn(string html)
        {
            var match = Regex.Match(html, "\\{[^{}]*\"_DONOT_USE_VMREF\"[^{}]*\\}");
            Assert.IsTrue(match.Success, "The page does not contain a view model reference.");
            var filter = new Dictionary<string, string>();
            foreach (var property in JsonDocument.Parse(match.Value).RootElement.EnumerateObject())
            {
                filter[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
            }
            return filter;
        }

        private static async Task<Dictionary<string, string>> ReportFilterAsync()
        {
            var page = await SendAsync("admin", HttpMethod.Get, "/Report/Index");
            Assert.AreEqual(HttpStatusCode.OK, page.Status);
            return FilterIn(page.Body);
        }

        private static async Task<string> LabReferenceAsync(string vm, string purposes, string fields = null, string cs = null)
        {
            var url = "/Lab/GenericReference?vm=" + Uri.EscapeDataString(vm) + "&purposes=" + purposes + (fields == null ? "" : "&fields=" + fields) + (cs == null ? "" : "&cs=" + cs);
            var response = await SendAsync("admin", HttpMethod.Get, url);
            Assert.IsTrue(response.Body.Length > 20, "No reference was issued.");
            return response.Body;
        }

        // ---- the page itself ---------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task FeaturePage_IsOnlyOpenToTheRoleThatHasItsPrivilege()
        {
            foreach (var identity in new[] { "norole", "limited" })
            {
                var page = await SendAsync(identity, HttpMethod.Get, "/Report/Index");
                Assert.AreEqual(_noPrivilegeText, page.Body.Trim(), identity);
            }
            Assert.IsTrue((await SendAsync("admin", HttpMethod.Get, "/Report/Index")).Body.Contains("_DONOT_USE_VMREF"));
        }

        // ---- GetPagingData -----------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task GetPagingData_ServesTheRowsToTheUserWhoHasThePage()
        {
            var filter = await ReportFilterAsync();

            var response = await SendAsync("admin", HttpMethod.Post, "/_Framework/GetPagingData", GridForm(filter["_DONOT_USE_VMNAME"], filter["_DONOT_USE_VMREF"], filter["_DONOT_USE_CS"]), gridRequest: true);

            Assert.AreEqual(200, CodeOf(response.Body));
            var data = JsonDocument.Parse(response.Body).RootElement;
            Assert.IsTrue(data.GetProperty("Count").GetInt32() >= 5, "The seeded gadgets are listed.");
            StringAssert.Contains(response.Body, "Alpha Wrench");
        }

        [TestMethod]
        [DataRow("norole")]
        [DataRow("limited")]
        [DataRow("admin")]
        public async Task GetPagingData_WithoutTheReferenceOfAPage_IsRefused_ForEveryUser(string identity)
        {
            var response = await SendAsync(identity, HttpMethod.Post, "/_Framework/GetPagingData", GridForm(GadgetListVm, null), gridRequest: true);

            Assert.AreEqual(403, CodeOf(response.Body));
            Assert.IsFalse(response.Body.Contains("Alpha Wrench"));
        }

        [TestMethod]
        [DataRow("norole")]
        [DataRow("limited")]
        public async Task GetPagingData_ReferenceOfAnotherUser_IsRefused(string identity)
        {
            var filter = await ReportFilterAsync();

            var response = await SendAsync(identity, HttpMethod.Post, "/_Framework/GetPagingData", GridForm(filter["_DONOT_USE_VMNAME"], filter["_DONOT_USE_VMREF"], filter["_DONOT_USE_CS"]), gridRequest: true);

            Assert.AreEqual(403, CodeOf(response.Body));
            Assert.IsFalse(response.Body.Contains("Alpha Wrench"));
        }

        [TestMethod]
        public async Task GetPagingData_AnonymousCaller_IsTurnedAwayWhateverItSends()
        {
            var filter = await ReportFilterAsync();

            var response = await SendAsync("anonymous", HttpMethod.Post, "/_Framework/GetPagingData", GridForm(filter["_DONOT_USE_VMNAME"], filter["_DONOT_USE_VMREF"], filter["_DONOT_USE_CS"]), gridRequest: true);

            Assert.AreEqual(401, CodeOf(response.Body));
        }

        [TestMethod]
        public async Task GetPagingData_ReferenceOfOneViewModel_DoesNotServeAnother()
        {
            var filter = await ReportFilterAsync();

            var response = await SendAsync("admin", HttpMethod.Post, "/_Framework/GetPagingData", GridForm(LabListVm, filter["_DONOT_USE_VMREF"], filter["_DONOT_USE_CS"]), gridRequest: true);

            Assert.AreEqual(403, CodeOf(response.Body));
        }

        [TestMethod]
        public async Task GetPagingData_ConnectionKeyOfTheRequest_MustBeTheOneOfThePage()
        {
            var filter = await ReportFilterAsync();

            var unknown = await SendAsync("admin", HttpMethod.Post, "/_Framework/GetPagingData", GridForm(filter["_DONOT_USE_VMNAME"], filter["_DONOT_USE_VMREF"], "no-such-connection"), gridRequest: true);
            var other = await SendAsync("admin", HttpMethod.Post, "/_Framework/GetPagingData", GridForm(filter["_DONOT_USE_VMNAME"], filter["_DONOT_USE_VMREF"], "default"), gridRequest: true);

            Assert.AreEqual(403, CodeOf(unknown.Body));
            // the page used the default connection (an empty key), so naming it explicitly is a different request
            Assert.AreEqual(string.IsNullOrEmpty(filter["_DONOT_USE_CS"]) ? 403 : 200, CodeOf(other.Body));
        }

        [TestMethod]
        public async Task GetPagingData_PageReferenceForTheTemplate_IsNotAReferenceForRows()
        {
            var importPage = await SendAsync("admin", HttpMethod.Get, "/Gadget/Import");
            var template = Regex.Match(importPage.Body, "_DONOT_USE_VMREF=([A-Za-z0-9_%\\-]+)").Groups[1].Value;
            Assert.IsTrue(template.Length > 20);

            var response = await SendAsync("admin", HttpMethod.Post, "/_Framework/GetPagingData", GridForm(GadgetImportVm, Uri.UnescapeDataString(template)), gridRequest: true);

            Assert.AreEqual(403, CodeOf(response.Body));
        }

        // ---- GetExportExcel ----------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task GetExportExcel_ExportsForTheUserWhoHasThePage_AndRefusesTheOthers()
        {
            var filter = await ReportFilterAsync();
            var form = GridForm(filter["_DONOT_USE_VMNAME"], filter["_DONOT_USE_VMREF"], filter["_DONOT_USE_CS"]);

            var admin = await SendAsync("admin", HttpMethod.Post, "/_Framework/GetExportExcel", form);
            Assert.AreEqual(HttpStatusCode.OK, admin.Status);
            StringAssert.Contains(admin.ContentType, "ms-excel");

            foreach (var identity in new[] { "norole", "limited" })
            {
                var refused = await SendAsync(identity, HttpMethod.Post, "/_Framework/GetExportExcel", form);
                Assert.AreEqual(_noPrivilegeText, refused.Body.Trim(), identity);
            }
            var anonymous = await SendAsync("anonymous", HttpMethod.Post, "/_Framework/GetExportExcel", form);
            Assert.AreNotEqual("application/vnd.ms-excel", anonymous.ContentType);
        }

        [TestMethod]
        [DataRow("norole")]
        [DataRow("limited")]
        [DataRow("admin")]
        public async Task GetExportExcel_WithoutTheReferenceOfAPage_IsRefused_ForEveryUser(string identity)
        {
            var response = await SendAsync(identity, HttpMethod.Post, "/_Framework/GetExportExcel", GridForm(GadgetListVm, null));

            Assert.AreEqual(_noPrivilegeText, response.Body.Trim());
        }

        // ---- GetExcelTemplate --------------------------------------------------------------------------------------------

        private static async Task<string> TemplateLinkAsync()
        {
            var page = await SendAsync("admin", HttpMethod.Get, "/Gadget/Import");
            var link = Regex.Match(page.Body, "href=\"(/_Framework/GetExcelTemplate\\?[^\"]+)\"").Groups[1].Value;
            Assert.IsTrue(link.Contains("_DONOT_USE_VMREF="), "The template link carries a reference.");
            return System.Net.WebUtility.HtmlDecode(link);
        }

        [TestMethod]
        public async Task GetExcelTemplate_DownloadsForTheUserWhoHasTheImportPage()
        {
            var link = await TemplateLinkAsync();

            var response = await SendAsync("admin", HttpMethod.Get, link);

            Assert.AreEqual(HttpStatusCode.OK, response.Status);
            StringAssert.Contains(response.ContentType, "ms-excel");
        }

        [TestMethod]
        [DataRow("norole")]
        [DataRow("limited")]
        public async Task ImportPage_AndItsTemplate_AreRefusedToUsersWithoutThePrivilege(string identity)
        {
            var link = await TemplateLinkAsync();

            var page = await SendAsync(identity, HttpMethod.Get, "/Gadget/Import");
            var template = await SendAsync(identity, HttpMethod.Get, link);
            var bare = await SendAsync(identity, HttpMethod.Get, "/_Framework/GetExcelTemplate?_DONOT_USE_VMNAME=" + Uri.EscapeDataString(GadgetImportVm));

            Assert.AreEqual(_noPrivilegeText, page.Body.Trim());
            Assert.AreEqual(_noPrivilegeText, template.Body.Trim());
            Assert.AreEqual(_noPrivilegeText, bare.Body.Trim());
        }

        [TestMethod]
        public async Task GetExcelTemplate_WithoutAReference_IsRefused_EvenForTheAdministrator()
        {
            var response = await SendAsync("admin", HttpMethod.Get, "/_Framework/GetExcelTemplate?_DONOT_USE_VMNAME=" + Uri.EscapeDataString(GadgetImportVm));

            Assert.AreEqual(_noPrivilegeText, response.Body.Trim());
        }

        // ---- Selector ----------------------------------------------------------------------------------------------------

        private static async Task<Dictionary<string, string>> SelectorFilterAsync()
        {
            var page = await SendAsync("admin", HttpMethod.Get, "/Lab/Pickers");
            return FilterIn(page.Body);
        }

        [TestMethod]
        public async Task Selector_OpensForTheUserWhoseFormShowsIt_AndWritesTheReferenceOfItsGrid()
        {
            var filter = await SelectorFilterAsync();

            var response = await SendAsync("admin", HttpMethod.Post, "/_Framework/Selector", filter);

            Assert.AreEqual(HttpStatusCode.OK, response.Status);
            StringAssert.Contains(response.Body, "/_Framework/GetPagingData");
            StringAssert.Contains(response.Body, "_DONOT_USE_VMREF");
        }

        [TestMethod]
        [DataRow("anonymous")]
        [DataRow("norole")]
        [DataRow("limited")]
        [DataRow("admin")]
        public async Task Selector_WithoutAReference_IsRefused_EvenThoughTheActionIsPublic(string identity)
        {
            var filter = await SelectorFilterAsync();
            filter.Remove("_DONOT_USE_VMREF");

            var response = await SendAsync(identity, HttpMethod.Post, "/_Framework/Selector", filter);

            Assert.AreEqual(_noPrivilegeText, response.Body.Trim());
        }

        [TestMethod]
        [DataRow("anonymous")]
        [DataRow("norole")]
        [DataRow("limited")]
        public async Task Selector_ReferenceOfAnotherUser_IsRefused(string identity)
        {
            var filter = await SelectorFilterAsync();

            var response = await SendAsync(identity, HttpMethod.Post, "/_Framework/Selector", filter);

            Assert.AreEqual(_noPrivilegeText, response.Body.Trim());
        }

        [TestMethod]
        public async Task Selector_ReferenceDoesNotServeAnotherViewModel_OrAnotherConnection()
        {
            var filter = await SelectorFilterAsync();

            var otherVm = new Dictionary<string, string>(filter) { ["_DONOT_USE_VMNAME"] = LabListVm };
            var otherConnection = new Dictionary<string, string>(filter) { ["_DONOT_USE_CURRENTCS"] = "no-such-connection" };

            Assert.AreEqual(_noPrivilegeText, (await SendAsync("admin", HttpMethod.Post, "/_Framework/Selector", otherVm)).Body.Trim());
            Assert.AreEqual(_noPrivilegeText, (await SendAsync("admin", HttpMethod.Post, "/_Framework/Selector", otherConnection)).Body.Trim());
        }

        [TestMethod]
        public async Task SelectorReference_IsNotAReferenceForTheGridOfTheListViewModel()
        {
            var filter = await SelectorFilterAsync();

            var response = await SendAsync("admin", HttpMethod.Post, "/_Framework/GetPagingData", GridForm(filter["_DONOT_USE_VMNAME"], filter["_DONOT_USE_VMREF"], filter["_DONOT_USE_CURRENTCS"]), gridRequest: true);

            Assert.AreEqual(403, CodeOf(response.Body));
        }

        // ---- GetEmptyData, UpdateModelProperty (pages that call them from their own script ask for a reference) --------------------

        [TestMethod]
        public async Task GetEmptyData_NeedsAReferenceOfThePage()
        {
            var reference = await LabReferenceAsync(LabListVm, "List");
            var url = "/_Framework/GetEmptyData?_DONOT_USE_VMNAME=" + Uri.EscapeDataString(LabListVm);

            var served = await SendAsync("admin", HttpMethod.Get, url + "&_DONOT_USE_VMREF=" + reference);
            var without = await SendAsync("admin", HttpMethod.Get, url);
            var other = await SendAsync("limited", HttpMethod.Get, url + "&_DONOT_USE_VMREF=" + reference);
            var anonymous = await SendAsync("anonymous", HttpMethod.Get, url + "&_DONOT_USE_VMREF=" + reference);

            Assert.AreEqual("application/json", served.ContentType);
            Assert.AreEqual(_noPrivilegeText, without.Body.Trim());
            Assert.AreEqual(_noPrivilegeText, other.Body.Trim());
            Assert.AreNotEqual("application/json", anonymous.ContentType);
        }

        [TestMethod]
        public async Task UpdateModelProperty_OnlyChangesTheFieldsThePageListed()
        {
            var reference = await LabReferenceAsync(GadgetVmName, "Update", "Name");
            var id = Guid.NewGuid().ToString();
            Dictionary<string, string> Form(string field, string reference) => new()
            {
                ["_DONOT_USE_VMNAME"] = GadgetVmName, ["id"] = id, ["field"] = field, ["value"] = "x", ["_DONOT_USE_VMREF"] = reference
            };

            var listed = await SendAsync("admin", HttpMethod.Post, "/_Framework/UpdateModelProperty", Form("Name", reference));
            var notListed = await SendAsync("admin", HttpMethod.Post, "/_Framework/UpdateModelProperty", Form("Code", reference));
            var withoutReference = await SendAsync("admin", HttpMethod.Post, "/_Framework/UpdateModelProperty", Form("Name", null));
            var otherUser = await SendAsync("limited", HttpMethod.Post, "/_Framework/UpdateModelProperty", Form("Name", reference));
            var listReference = await SendAsync("admin", HttpMethod.Post, "/_Framework/UpdateModelProperty", Form("Name", await LabReferenceAsync(GadgetVmName, "List")));

            Assert.AreNotEqual(_noPrivilegeText, listed.Body.Trim(), "The listed field passes the check.");
            Assert.AreEqual(_noPrivilegeText, notListed.Body.Trim());
            Assert.AreEqual(_noPrivilegeText, withoutReference.Body.Trim());
            Assert.AreEqual(_noPrivilegeText, otherUser.Body.Trim());
            Assert.AreEqual(_noPrivilegeText, listReference.Body.Trim());
        }
        // ---- the connection of a page is decided by the server ------------------------------------------------------------

        [TestMethod]
        [DataRow("default")]
        [DataRow("no-such-connection")]
        public async Task ConnectionKeyInTheQueryString_DoesNotChangeTheConnectionOfThePage(string requested)
        {
            var plain = await ReportFilterAsync();

            var page = await SendAsync("admin", HttpMethod.Get, "/Report/Index?DONOTUSECSName=" + requested);
            var asked = FilterIn(page.Body);

            Assert.AreEqual(plain["_DONOT_USE_CS"], asked["_DONOT_USE_CS"], "The page runs on the connection the server decides.");
            var response = await SendAsync("admin", HttpMethod.Post, "/_Framework/GetPagingData", GridForm(asked["_DONOT_USE_VMNAME"], asked["_DONOT_USE_VMREF"], requested), gridRequest: true);
            Assert.AreEqual(403, CodeOf(response.Body), "The reference of the page does not serve another connection.");
        }

        [TestMethod]
        public async Task GetEmptyData_UpdateModelProperty_AndTheTemplate_AreBoundToTheConnectionTheServerUses()
        {
            // a reference pinned to a connection other than the one the request runs on (the default, an empty key) is not honoured
            var pinnedList = await LabReferenceAsync(LabListVm, "List", cs: "default");
            var pinnedUpdate = await LabReferenceAsync(GadgetVmName, "Update", "Name", "default");
            var pinnedTemplate = await LabReferenceAsync(GadgetImportVm, "Template", cs: "default");
            var okTemplate = await LabReferenceAsync(GadgetImportVm, "Template");

            var emptyUrl = "/_Framework/GetEmptyData?_DONOT_USE_VMNAME=" + Uri.EscapeDataString(LabListVm) + "&_DONOT_USE_VMREF=";
            var templateUrl = "/_Framework/GetExcelTemplate?_DONOT_USE_VMNAME=" + Uri.EscapeDataString(GadgetImportVm) + "&_DONOT_USE_VMREF=";

            Assert.AreEqual(_noPrivilegeText, (await SendAsync("admin", HttpMethod.Get, emptyUrl + pinnedList)).Body.Trim());
            Assert.AreEqual(_noPrivilegeText, (await SendAsync("admin", HttpMethod.Get, templateUrl + pinnedTemplate)).Body.Trim());
            var update = await SendAsync("admin", HttpMethod.Post, "/_Framework/UpdateModelProperty", new Dictionary<string, string>
            {
                ["_DONOT_USE_VMNAME"] = GadgetVmName, ["id"] = Guid.NewGuid().ToString(), ["field"] = "Name", ["value"] = "x", ["_DONOT_USE_VMREF"] = pinnedUpdate
            });
            Assert.AreEqual(_noPrivilegeText, update.Body.Trim());
            StringAssert.Contains((await SendAsync("admin", HttpMethod.Get, templateUrl + okTemplate)).ContentType, "ms-excel");
        }

        // ---- a reference for the selector does not give the right to export ---------------------------------------------------------

        [TestMethod]
        public async Task SelectorPopup_GridReference_AdmitsPaging_ButNotExport()
        {
            var filter = await SelectorFilterAsync();
            var popup = await SendAsync("admin", HttpMethod.Post, "/_Framework/Selector", filter);
            var grid = FilterIn(popup.Body);

            var paging = await SendAsync("admin", HttpMethod.Post, "/_Framework/GetPagingData", GridForm(grid["_DONOT_USE_VMNAME"], grid["_DONOT_USE_VMREF"], grid["_DONOT_USE_CS"]), gridRequest: true);
            var export = await SendAsync("admin", HttpMethod.Post, "/_Framework/GetExportExcel", GridForm(grid["_DONOT_USE_VMNAME"], grid["_DONOT_USE_VMREF"], grid["_DONOT_USE_CS"]));
            var viaSelectorReference = await SendAsync("admin", HttpMethod.Post, "/_Framework/GetExportExcel", GridForm(filter["_DONOT_USE_VMNAME"], filter["_DONOT_USE_VMREF"], filter["_DONOT_USE_CURRENTCS"]));

            Assert.AreEqual(200, CodeOf(paging.Body));
            Assert.AreEqual(_noPrivilegeText, export.Body.Trim());
            Assert.AreEqual(_noPrivilegeText, viaSelectorReference.Body.Trim());
        }

        // ---- users with ordinary rights keep working ------------------------------------------------------------------------------

        [TestMethod]
        public async Task LimitedRole_OpensItsPage_AndItsGridLoadsRows()
        {
            var page = await SendAsync("limited", HttpMethod.Get, "/Gadget/Index");
            Assert.AreEqual(HttpStatusCode.OK, page.Status);
            Assert.AreNotEqual(_noPrivilegeText, page.Body.Trim());
            var filter = FilterIn(page.Body);

            var response = await SendAsync("limited", HttpMethod.Post, "/_Framework/GetPagingData", GridForm(filter["_DONOT_USE_VMNAME"], filter["_DONOT_USE_VMREF"], filter["_DONOT_USE_CS"]), gridRequest: true);

            Assert.AreEqual(200, CodeOf(response.Body));
            StringAssert.Contains(response.Body, "Alpha Wrench");
        }

        [TestMethod]
        [DataRow("norole")]
        [DataRow("limited")]
        public async Task OrdinaryUser_UsesTheSelectorOfAPageOpenToEveryone(string identity)
        {
            var page = await SendAsync(identity, HttpMethod.Get, "/Lab/Pickers");
            var filter = FilterIn(page.Body);

            var popup = await SendAsync(identity, HttpMethod.Post, "/_Framework/Selector", filter);
            Assert.AreEqual(HttpStatusCode.OK, popup.Status);
            StringAssert.Contains(popup.Body, "/_Framework/GetPagingData");

            var grid = FilterIn(popup.Body);
            var rows = await SendAsync(identity, HttpMethod.Post, "/_Framework/GetPagingData", GridForm(grid["_DONOT_USE_VMNAME"], grid["_DONOT_USE_VMREF"], grid["_DONOT_USE_CS"]), gridRequest: true);
            Assert.AreEqual(200, CodeOf(rows.Body));
        }
    }
}
