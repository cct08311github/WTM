using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests
{
    /// <summary>
    /// The grid action buttons of /Lab/Keys, a grid whose row ids are text (the entities of the host have GUID ids): one row with an
    /// ordinary id, one with a GUID, one whose id (and Code field) holds the characters that cut an address. Every button that puts the id
    /// of a row, or a field of it, into the address of its request must deliver the value whole to the server, and the address of an
    /// ordinary id must stay the one it always was (#344).
    /// </summary>
    [TestClass]
    public class LayUiGridActionIdTests : LabTest
    {
        // the Code field of the special row (the fixture host writes the same text)
        private const string SpecialCode = "c&d#2 +%'\"]\\";
        private const string Guid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

        [TestInitialize]
        public async Task OpenPage()
        {
            await Page.Context.APIRequest.GetAsync("/Lab/KeyProbe?reset=true");
            await Lab.OpenAsync("Keys");
            await Expect(Rows).ToHaveCountAsync(3);
        }

        private ILocator Rows => Body.Locator(".layui-table-main tr");
        private ILocator Toolbar(string text) => Body.Locator(".layui-table-tool a.layui-btn", new() { HasTextString = text });

        // The buttons of a row exist twice: in the main table and, when the action column is fixed at the right, in an overlay table that
        // covers the main one. Which copy can be clicked depends on the layout, which is settled a moment after the rows appear (and
        // differs between machines), so the click looks for a visible copy again whenever the one it found is covered or gone.
        private async Task ClickRowButtonAsync(string row, string text)
        {
            var index = IndexOf(row);
            var buttons = Body.Locator($".layui-table-main tr[data-index='{index}'] a.layui-btn, .layui-table-fixed-r tr[data-index='{index}'] a.layui-btn", new() { HasTextString = text });
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    // the overlay table comes last in the page: when both copies are visible it is the one on top
                    await buttons.Filter(new() { Visible = true }).Last.ClickAsync(new() { Timeout = 2000 });
                    return;
                }
                catch (TimeoutException) when (attempt < 15)
                {
                    await Task.Delay(300);
                }
                catch (PlaywrightException) when (attempt < 15)
                {
                    await Task.Delay(300);
                }
            }
        }

        private static int IndexOf(string row) => row switch { "Key plain" => 0, "Key guid" => 1, "Key special" => 2, _ => throw new ArgumentException(row) };

        /// <summary>Checks the check box of a row (the check boxes of the grid sit in a fixed overlay table).</summary>
        private async Task SelectAsync(string row)
        {
            var index = await Rows.Filter(new() { HasTextString = row }).GetAttributeAsync("data-index");
            await Body.Locator($".layui-table-fixed-l tr[data-index='{index}'] .layui-form-checkbox").ClickAsync();
        }

        /// <summary>What the last request of a key action sent: values as decoded by the server, "_raw" the query string, "_Ids" the posted ids.</summary>
        private async Task<Dictionary<string, string[]>> ProbeAsync(string action)
        {
            for (var i = 0; i < 100; i++)
            {
                var response = await Page.Context.APIRequest.GetAsync("/Lab/KeyProbe?name=" + action);
                var seen = await response.JsonAsync<Dictionary<string, string[]>>() ?? new Dictionary<string, string[]>();
                if (seen.Count > 0)
                {
                    return seen;
                }
                await Task.Delay(100);
            }
            Assert.Fail("The server did not receive a request of " + action);
            return null;
        }

        private async Task CloseAlertAsync(string action)
        {
            await AcceptAlertAsync("Key probe " + action);
        }

        private static void AssertRaw(string expected, Dictionary<string, string[]> seen)
        {
            Assert.IsTrue(seen.ContainsKey("_raw"), "no query recorded: " + System.Text.Json.JsonSerializer.Serialize(seen));
            // the framework adds a cache buster ("_" or "v") to the address of some requests
            var raw = System.Text.RegularExpressions.Regex.Replace(seen["_raw"].Single(), "&(_|v)=\\d+$", "");
            Assert.AreEqual(expected, raw, System.Text.Json.JsonSerializer.Serialize(seen));
        }

        private static string Id(Dictionary<string, string[]> seen) => seen["id"].Single();
        private static string Code(Dictionary<string, string[]> seen) => seen["Code"].Single();

        // ---- row buttons ---------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task RowButton_Dialog_OrdinaryIds_KeepTheAddressTheyAlwaysHad()
        {
            await ClickRowButtonAsync("Key plain", "Key dialog");
            var seen = await ProbeAsync("KeyDialog");
            await CloseAlertAsync("KeyDialog");
            AssertRaw("?1=1&id=plain-1&Code=code-1", seen);

            await ClickRowButtonAsync("Key guid", "Key dialog");
            seen = await ProbeAsync("KeyDialog");
            await CloseAlertAsync("KeyDialog");
            AssertRaw("?1=1&id=" + Guid + "&Code=code-2", seen);
        }

        [TestMethod]
        public async Task RowButton_Dialog_IdAndFieldWithAddressCharacters_ReachTheServerWhole()
        {
            await ClickRowButtonAsync("Key special", "Key dialog");

            var seen = await ProbeAsync("KeyDialog");
            Assert.AreEqual(LabData.SpecialId, Id(seen));
            Assert.AreEqual(SpecialCode, Code(seen));
            await CloseAlertAsync("KeyDialog");
            await ExpectNoProbeElementsAsync();
        }

        [TestMethod]
        public async Task RowButton_Request_IdAndFieldWithAddressCharacters_ReachTheServerWhole()
        {
            // SingleIdWithNull without a dialog: a background request
            await ClickRowButtonAsync("Key special", "Key request");

            var seen = await ProbeAsync("KeyRequest");
            Assert.AreEqual(LabData.SpecialId, Id(seen));
            Assert.AreEqual(SpecialCode, Code(seen));
            await CloseAlertAsync("KeyRequest");
        }

        [TestMethod]
        public async Task RowButton_Request_OrdinaryIds_KeepTheAddressTheyAlwaysHad()
        {
            await ClickRowButtonAsync("Key guid", "Key request");

            var seen = await ProbeAsync("KeyRequest");
            await CloseAlertAsync("KeyRequest");
            // the fields named by the action come before the id for this kind of button
            AssertRaw("?1=1&Code=code-2&id=" + Guid, seen);
        }

        [TestMethod]
        public async Task RowButton_Redirect_IdAndFieldWithAddressCharacters_ReachTheServerWhole()
        {
            // ff.LoadPage puts the address into the page hash, from where the frame loads the page
            await ClickRowButtonAsync("Key special", "Key page");

            var seen = await ProbeAsync("KeyGo");
            Assert.AreEqual(LabData.SpecialId, Id(seen));
            Assert.AreEqual(SpecialCode, Code(seen));
            await Expect(Body.Locator("#lab-keygo")).ToHaveTextAsync("key page " + LabData.SpecialId);
            await ExpectNoProbeElementsAsync();
        }

        [TestMethod]
        public async Task RowButton_Redirect_OrdinaryIds_KeepTheAddressTheyAlwaysHad()
        {
            await ClickRowButtonAsync("Key guid", "Key page");

            var seen = await ProbeAsync("KeyGo");
            AssertRaw("?1=1&id=" + Guid + "&Code=code-2", seen);
            await Expect(Body.Locator("#lab-keygo")).ToHaveTextAsync("key page " + Guid);
        }

        // ---- tool bar buttons (the id comes from the checked row) ------------------------------------------------------

        [TestMethod]
        public async Task Toolbar_Dialog_SelectedRowWithAddressCharacters_ReachesTheServerWhole()
        {
            await SelectAsync("Key special");

            await Toolbar("Key dialog").ClickAsync();

            var seen = await ProbeAsync("KeyDialog");
            Assert.AreEqual(LabData.SpecialId, Id(seen));
            Assert.AreEqual(SpecialCode, Code(seen));
            await CloseAlertAsync("KeyDialog");
        }

        [TestMethod]
        public async Task Toolbar_Dialog_SelectedOrdinaryRow_KeepsTheAddressItAlwaysHad()
        {
            await SelectAsync("Key plain");

            await Toolbar("Key dialog").ClickAsync();

            var seen = await ProbeAsync("KeyDialog");
            await CloseAlertAsync("KeyDialog");
            AssertRaw("?1=1&id=plain-1&Code=code-1", seen);
        }

        [TestMethod]
        public async Task Toolbar_Request_SelectedRowWithAddressCharacters_ReachesTheServerWhole()
        {
            await SelectAsync("Key special");

            await Toolbar("Key request").ClickAsync();

            var seen = await ProbeAsync("KeyRequest");
            Assert.AreEqual(LabData.SpecialId, Id(seen));
            Assert.AreEqual(SpecialCode, Code(seen));
            await CloseAlertAsync("KeyRequest");
        }

        [TestMethod]
        public async Task Toolbar_Request_NothingSelected_SendsNoId()
        {
            await Toolbar("Key request").ClickAsync();

            var seen = await ProbeAsync("KeyRequest");
            await CloseAlertAsync("KeyRequest");
            AssertRaw("?1=1", seen);
        }

        [TestMethod]
        public async Task Toolbar_Redirect_SelectedRowWithAddressCharacters_ReachesTheServerWhole()
        {
            await SelectAsync("Key special");

            await Toolbar("Key page").ClickAsync();

            var seen = await ProbeAsync("KeyGo");
            Assert.AreEqual(LabData.SpecialId, Id(seen));
            Assert.AreEqual(SpecialCode, Code(seen));
            await Expect(Body.Locator("#lab-keygo")).ToHaveTextAsync("key page " + LabData.SpecialId);
        }

        // ---- the buttons inside a group -----------------------------------------------------------------------------------

        [TestMethod]
        public async Task ActionsGroup_SubAction_SelectedRowWithAddressCharacters_ReachesTheServerWhole()
        {
            await SelectAsync("Key special");

            await ClickSubActionAsync();

            var seen = await ProbeAsync("KeyGroup");
            Assert.AreEqual(LabData.SpecialId, Id(seen));
            Assert.AreEqual(SpecialCode, Code(seen));
            await CloseAlertAsync("KeyGroup");
        }

        [TestMethod]
        public async Task ActionsGroup_SubAction_SelectedOrdinaryRow_KeepsTheAddressItAlwaysHad()
        {
            await SelectAsync("Key guid");

            await ClickSubActionAsync();

            var seen = await ProbeAsync("KeyGroup");
            await CloseAlertAsync("KeyGroup");
            AssertRaw("?1=1&id=" + Guid + "&Code=code-2", seen);
        }

        /// <summary>Opens the drop-down of the group button and clicks the sub action (the drop-down handler is attached half a second after the grid is shown).</summary>
        private async Task ClickSubActionAsync()
        {
            var sub = Body.Locator(".layui-table-tool button.downpanel dd a", new() { HasTextString = "Key sub" });
            for (var i = 0; i < 10 && await sub.IsVisibleAsync() == false; i++)
            {
                await Task.Delay(600);
                await Body.Locator(".layui-table-tool button.downpanel .layui-select-title").ClickAsync();
            }
            await sub.ClickAsync();
        }

        // ---- download buttons whose address holds the id -------------------------------------------------------------------

        [TestMethod]
        public async Task RowButton_Download_IdAndFieldWithAddressCharacters_ReachTheServerWhole()
        {
            var download = await Page.RunAndWaitForDownloadAsync(async () => await ClickRowButtonAsync("Key special", "Key file"));
            Assert.AreEqual("key.txt", download.SuggestedFilename);

            var seen = await ProbeAsync("KeyFile");
            Assert.AreEqual(LabData.SpecialId, Id(seen));
            Assert.AreEqual(SpecialCode, Code(seen));
        }

        [TestMethod]
        public async Task RowButton_Download_OrdinaryIds_KeepTheAddressTheyAlwaysHad()
        {
            var download = await Page.RunAndWaitForDownloadAsync(async () => await ClickRowButtonAsync("Key guid", "Key file"));
            Assert.AreEqual("key.txt", download.SuggestedFilename);

            var seen = await ProbeAsync("KeyFile");
            AssertRaw("?1=1&id=" + Guid + "&Code=code-2", seen);
        }

        // ---- the ids that travel in the body of the request --------------------------------------------------------------

        [TestMethod]
        public async Task ToolbarDownload_PostsTheSelectedIdsUnchanged()
        {
            await SelectAsync("Key plain");
            await SelectAsync("Key special");

            var download = await Page.RunAndWaitForDownloadAsync(async () => await Toolbar("Key download").ClickAsync());
            Assert.AreEqual("keys.txt", download.SuggestedFilename);

            var seen = await ProbeAsync("KeyDownload");
            CollectionAssert.AreEquivalent(new[] { "plain-1", LabData.SpecialId }, seen["_Ids"]);
            // the ids are not part of the address
            AssertRaw("?1=1", seen);
        }
    }
}
