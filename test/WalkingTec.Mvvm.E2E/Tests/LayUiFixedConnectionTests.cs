using System;
using System.IO;
using System.Linq;
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
    /// The report page and the pages with a fixed connection of the test host (/Report/Index, /OtherDb/*), on the smoke site so that the
    /// route coverage report counts them. The permission matrix reaches the same routes on a site of its own (GenericEndpointAuthorizationTests),
    /// whose hits are not part of the report. The connection <c>other</c> has a read replica <c>other_1</c>, each a database of its own whose
    /// Gadget names start with "Base " and "Replica ": the prefix tells which database a page or an endpoint ran on.
    /// </summary>
    [TestClass]
    public class LayUiFixedConnectionTests : E2ETest
    {
        private const string Assembly = ", WalkingTec.Mvvm.LayUI.E2EHost";
        private const string GadgetVm = "WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.GadgetVMs.GadgetVM" + Assembly;

        protected override bool StartSignedIn => true;

        private HomeFrame Frame => new HomeFrame(Page);
        private ILocator Body => Frame.Body;
        private ILocator Rows => Body.Locator(".layui-table-main tr");
        private ILocator Cell(ILocator row, string field) => row.Locator($"td[data-field={field}]");

        private static bool IsPagingData(IResponse r) => r.Url.Contains("/_Framework/GetPagingData") && r.Request.Method == "POST";

        /// <summary>Loads a page that has no menu entry into the active tab of the home frame, the way a link of the framework does (location hash).</summary>
        private async Task OpenFrameworkPageAsync(string url)
        {
            await Page.EvaluateAsync("url => ff.LoadPage(url)", url);
        }

        [TestMethod]
        public async Task ReportPage_Opens_ListsTheRowsFromTheGenericDataEndpoint()
        {
            await Frame.GotoAsync();
            var data = Page.WaitForResponseAsync(IsPagingData);
            await Frame.OpenMenuPageAsync("Reports", "Gadget report");

            var response = await data;
            Assert.AreEqual(200, response.Status);
            // the default connection, shared with the other tests of the run: at least the five seeded rows
            var json = (await response.JsonAsync()).Value;
            Assert.IsTrue(json.GetProperty("Count").GetInt32() >= 5, "The seeded gadgets are listed.");
            await Expect(Rows.First).ToBeVisibleAsync();
            await Expect(Cell(Rows.First, "Name")).Not.ToBeEmptyAsync();
            await Site.AssertRouteHitAsync("report/index");
        }

        [TestMethod]
        public async Task FixedConnectionGrid_Opens_ListsTheRowsOfTheReplicaOfItsConnection()
        {
            await Frame.GotoAsync();
            var data = Page.WaitForResponseAsync(IsPagingData);
            await OpenFrameworkPageAsync("/OtherDb/Grid");

            Assert.AreEqual(200, (await data).Status);
            // a read of the connection "other" runs on its replica: five rows, all of them "Replica ..."; not the default connection ("Alpha Wrench") nor the base ("Base ...")
            var names = Body.Locator(".layui-table-main td[data-field=Name]");
            await Expect(names).ToHaveCountAsync(5);
            await Expect(names.Filter(new() { HasTextRegex = new Regex("^Replica ") })).ToHaveCountAsync(5);
            await Expect(names.Filter(new() { HasTextString = "Replica Alpha Wrench" })).ToHaveCountAsync(1);
            await Site.AssertRouteHitAsync("otherdb/grid");
        }

        [TestMethod]
        public async Task FixedConnectionImport_Opens_ShowsTheTemplateDownload()
        {
            await Frame.GotoAsync();
            await Page.EvaluateAsync("() => ff.OpenDialog('/OtherDb/Import', 'otherdb-import', 'Import', 800)");
            var dialog = Page.Locator(".layui-layer-page");
            var template = dialog.Locator("a", new() { HasTextString = "Download template" });
            await Expect(template).ToBeVisibleAsync();

            var download = await Page.RunAndWaitForDownloadAsync(async () => await template.ClickAsync());

            StringAssert.EndsWith(download.SuggestedFilename, ".xlsx");
            var path = Path.Combine(E2ERun.OutputDir, "downloads", "other-" + Guid.NewGuid().ToString("N").Substring(0, 6) + "-" + download.SuggestedFilename);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            await download.SaveAsAsync(path);
            Assert.IsTrue(new FileInfo(path).Length > 1000, "The template is unexpectedly small.");
            await Site.AssertRouteHitAsync("otherdb/import");
        }

        /// <summary>The Gadget rows ("id|name" per line) one of the helper actions of the page reads: BaseGadgets or ReplicaGadgets.</summary>
        private async Task<(string Id, string Name)[]> RowsAsync(string action)
        {
            var response = await Context.APIRequest.GetAsync("/OtherDb/" + action);
            Assert.AreEqual(200, response.Status);
            return (await response.TextAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => (x.Split('|')[0], x.Split('|')[1])).ToArray();
        }

        /// <summary>The reference a page of this connection writes for the framework's generic endpoints (what /OtherDb/Reference issues).</summary>
        private async Task<string> ReferenceAsync(string vm, string purposes, string fields)
        {
            var response = await Context.APIRequest.GetAsync($"/OtherDb/Reference?vm={Uri.EscapeDataString(vm)}&purposes={purposes}&fields={fields}");
            Assert.AreEqual(200, response.Status);
            var reference = await response.TextAsync();
            Assert.IsTrue(reference.Length > 20, "No reference was issued.");
            return reference;
        }

        /// <summary>Sets the Name of a Gadget to <paramref name="value"/> through the generic endpoint, on the connection the reference is bound to.</summary>
        private async Task<IAPIResponse> UpdateNameAsync(string reference, string id, string value)
        {
            return await Context.APIRequest.PostAsync("/_Framework/UpdateModelProperty", new()
            {
                Form = Context.APIRequest.CreateFormData()
                    .Set("_DONOT_USE_VMNAME", GadgetVm).Set("_DONOT_USE_VMREF", reference).Set("id", id).Set("field", "Name").Set("value", value)
            });
        }

        [TestMethod]
        public async Task BaseAndReplicaGadgets_ListTheRowsOfTheirOwnDatabase()
        {
            var baseRows = await RowsAsync("BaseGadgets");
            var replicaRows = await RowsAsync("ReplicaGadgets");

            Assert.AreEqual(5, baseRows.Length);
            Assert.AreEqual(5, replicaRows.Length);
            Assert.IsTrue(baseRows.All(x => x.Name.StartsWith("Base ")), "BaseGadgets reads the base connection.");
            Assert.IsTrue(replicaRows.All(x => x.Name.StartsWith("Replica ")), "ReplicaGadgets reads the read replica.");
            Assert.IsFalse(baseRows.Select(x => x.Id).Intersect(replicaRows.Select(x => x.Id)).Any(), "Each database has rows (and ids) of its own.");
            await Site.AssertRouteHitAsync("otherdb/basegadgets", "otherdb/replicagadgets");
        }

        [TestMethod]
        public async Task Reference_OfAFixedConnectionPage_WritesFindOnlyTheRowsOfTheBase()
        {
            var baseRow = (await RowsAsync("BaseGadgets")).First();
            var replicaRow = (await RowsAsync("ReplicaGadgets")).First();
            var reference = await ReferenceAsync(GadgetVm, "Update", "Name");

            // the name is set to the value it has: the test checks which database the write is looked up in, and leaves the rows as they are
            var onBase = await UpdateNameAsync(reference, baseRow.Id, baseRow.Name);
            var onReplicaRow = await UpdateNameAsync(reference, replicaRow.Id, replicaRow.Name);

            Assert.AreEqual(200, onBase.Status, "The row of the base connection is found: a write does not go to the replica.");
            StringAssert.Contains(await onBase.TextAsync(), "\"Code\":\"200\"");
            Assert.AreEqual(400, onReplicaRow.Status, "The row that exists only on the replica is not found by a write.");
            await Site.AssertRouteHitAsync("otherdb/reference");
        }
    }
}
