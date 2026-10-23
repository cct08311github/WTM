using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Issue #344: the id of a row (and the values of the row fields named by a grid action) that the grid action buttons append to the
    /// address are encoded for the place they are written into (a query string). Ordinary ids (GUIDs, numbers) give the same address,
    /// and the ids posted in the request body (Ids) are not touched.
    /// </summary>
    [TestClass]
    public class GridActionIdEncodingTests : LayUITagHelperTestBase
    {
        private sealed class ActionsListVM : LayuiSchoolListVM
        {
            public List<GridAction> Actions { get; set; } = new List<GridAction>();

            protected override List<GridAction> InitGridAction() => Actions;
        }

        private static string RenderGrid(params GridAction[] actions)
        {
            var wtm = MockWtmContext.CreateWtmContext();
            wtm.ConfigInfo.IsQuickDebug = true;
            var listVm = new ActionsListVM { Wtm = wtm, Actions = new List<GridAction>(actions) };
            return Render(new DataTableTagHelper { Vm = Self(listVm, "Vm"), Url = "/School/Search" }, "wt:grid");
        }

        private static GridAction Action(string name, GridActionParameterTypesEnum type, Action<GridAction> configure = null, params string[] whereStr)
        {
            var rv = new GridAction
            {
                Name = name,
                DialogTitle = name,
                ControllerName = "School",
                ActionName = name,
                Area = string.Empty,
                ParameterType = type,
                whereStr = whereStr.Length == 0 ? null : whereStr,
            };
            configure?.Invoke(rv);
            return rv;
        }

        /// <summary>The script of one action button: from its case label to the end of the case.</summary>
        private static string CaseOf(string html, string name)
        {
            var start = html.IndexOf("case 'School" + name + "':{", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "no case for " + name + " in " + html);
            var end = html.IndexOf("};break;", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "case of " + name + " is not closed");
            return html.Substring(start, end - start);
        }

        private const string RowIdOnce = "tempUrl = tempUrl + '&id=' + encodeURIComponent(data.ID);";
        private const string SelectedIdOnce = "tempUrl = tempUrl + '&id=' + encodeURIComponent(ids[0]);";

        #region the id appended to the address

        [TestMethod]
        public void SingleId_AppendsTheRowIdAndTheSelectedIdAsEncodedAddressParts()
        {
            var html = RenderGrid(Action("One", GridActionParameterTypesEnum.SingleId, x => x.ShowDialog = true));

            var script = CaseOf(html, "One");
            AssertContains(script, SelectedIdOnce, RowIdOnce);
            AssertNotContains(script, "'&id=' + ids[0]", "'&id=' + data.ID");
        }

        [TestMethod]
        public void SingleIdWithNull_AppendsTheSelectedIdAsAnEncodedAddressPart()
        {
            var html = RenderGrid(Action("OneOrNone", GridActionParameterTypesEnum.SingleIdWithNull, x => x.ShowDialog = true));

            var script = CaseOf(html, "OneOrNone");
            AssertContains(script, SelectedIdOnce);
            AssertNotContains(script, "'&id=' + ids[0]");
        }

        [TestMethod]
        public void ActionsGroup_SubActions_AppendTheIdAsAnEncodedAddressPart()
        {
            var group = new GridAction
            {
                Name = "More",
                ControllerName = "School",
                ActionName = "ActionsGroup",
                Area = string.Empty,
                ParameterType = GridActionParameterTypesEnum.NoId,
                SubActions = new List<GridAction>
                {
                    Action("SubOne", GridActionParameterTypesEnum.SingleId, x => x.ShowDialog = true),
                    Action("SubOneOrNone", GridActionParameterTypesEnum.SingleIdWithNull),
                },
            };

            var html = RenderGrid(group);

            AssertContains(CaseOf(html, "SubOne"), SelectedIdOnce, RowIdOnce);
            AssertContains(CaseOf(html, "SubOneOrNone"), SelectedIdOnce);
            AssertNotContains(html, "'&id=' + ids[0]", "'&id=' + data.ID");
        }

        [TestMethod]
        public void EveryActionKind_UsesTheEncodedIdForTheAddress()
        {
            var html = RenderGrid(
                Action("Dialog", GridActionParameterTypesEnum.SingleId, x => x.ShowDialog = true),
                Action("Request", GridActionParameterTypesEnum.SingleId),
                Action("ForcedPost", GridActionParameterTypesEnum.SingleIdWithNull, x => x.ForcePost = true),
                Action("Download", GridActionParameterTypesEnum.SingleId, x => x.IsDownload = true),
                Action("Export", GridActionParameterTypesEnum.SingleIdWithNull, x => x.IsExport = true),
                Action("Own", GridActionParameterTypesEnum.SingleId, x => x.OnClickFunc = "ownHandler"));

            foreach (var name in new[] { "Dialog", "Request", "ForcedPost", "Download", "Export", "Own" })
            {
                var script = CaseOf(html, name);
                AssertContains(script, "encodeURIComponent(ids[0])");
                AssertNotContains(script, "'&id=' + ids[0]", "'&id=' + data.ID", "encodeURIComponent(encodeURIComponent(");
            }
            // what each button does with the address is as before
            AssertContains(CaseOf(html, "Dialog"), "ff.OpenDialog(tempUrl,");
            AssertContains(CaseOf(html, "Request"), "ff.BgRequest(tempUrl,");
            AssertContains(CaseOf(html, "Download"), "ff.Download(tempUrl,ids);");
            AssertContains(CaseOf(html, "Export"), "ff.DownloadExcelOrPdf(tempUrl,");
            AssertContains(CaseOf(html, "Own"), "ownHandler(ids,ff.GetSelectionData(");
        }

        [TestMethod]
        public void RedirectActions_AppendTheIdEncodedTwice_BecauseLoadPageDecodesTheAddressOnce()
        {
            // ff.LoadPage decodes the address before it goes into the page hash, so a value that is encoded once would be decoded again
            var html = RenderGrid(
                Action("GoDialog", GridActionParameterTypesEnum.SingleId, x => { x.ShowDialog = true; x.IsRedirect = true; }),
                Action("Go", GridActionParameterTypesEnum.SingleIdWithNull, x => x.IsRedirect = true));

            AssertContains(CaseOf(html, "GoDialog"),
                "tempUrl = tempUrl + '&id=' + encodeURIComponent(encodeURIComponent(ids[0]));",
                "tempUrl = tempUrl + '&id=' + encodeURIComponent(encodeURIComponent(data.ID));",
                "ff.LoadPage(tempUrl,true,");
            AssertContains(CaseOf(html, "Go"),
                "tempUrl = tempUrl + '&id=' + encodeURIComponent(encodeURIComponent(ids[0]));",
                "ff.LoadPage(tempUrl,false,");
            AssertNotContains(html, "'&id=' + ids[0]", "'&id=' + data.ID");
        }

        [TestMethod]
        public void IdsOfMultiRowActions_StayInTheRequestBody()
        {
            var html = RenderGrid(
                Action("Many", GridActionParameterTypesEnum.MultiIds, x => x.ShowDialog = true),
                Action("ManyOrNone", GridActionParameterTypesEnum.MultiIdWithNull));

            var many = CaseOf(html, "Many");
            AssertContains(many, "{'Ids':ids}");
            AssertNotContains(many, "&id=");
            var manyOrNone = CaseOf(html, "ManyOrNone");
            AssertContains(manyOrNone, "{'Ids':ids}");
            AssertNotContains(manyOrNone, "&id=");
        }

        #endregion

        #region the values of the row fields named by the action

        [TestMethod]
        public void RowFieldValues_AreAppendedByConcatWhereStr_OnceEncodedBySelf()
        {
            var html = RenderGrid(
                Action("Dialog", GridActionParameterTypesEnum.SingleId, x => x.ShowDialog = true, "SchoolCode"),
                Action("OneOrNone", GridActionParameterTypesEnum.SingleIdWithNull, null, "SchoolCode"));

            // the call keeps the shape it had: ff.concatWhereStr encodes the values itself
            AssertContains(CaseOf(html, "Dialog"),
                "tempUrl = ff.concatWhereStr(tempUrl,whereStr,objs[0]);",
                "tempUrl = ff.concatWhereStr(tempUrl,whereStr,data);");
            AssertContains(CaseOf(html, "OneOrNone"), "tempUrl = ff.concatWhereStr(tempUrl,whereStr,data);");
            AssertNotContains(html, "whereStr,data,true", "whereStr,objs[0],true");
        }

        [TestMethod]
        public void RowFieldValues_OfRedirectActions_AreEncodedTwice()
        {
            var html = RenderGrid(
                Action("Go", GridActionParameterTypesEnum.SingleId, x => x.IsRedirect = true, "SchoolCode"),
                Action("GoOrNone", GridActionParameterTypesEnum.SingleIdWithNull, x => x.IsRedirect = true, "SchoolCode"));

            AssertContains(CaseOf(html, "Go"),
                "tempUrl = ff.concatWhereStr(tempUrl,whereStr,objs[0],true);",
                "tempUrl = ff.concatWhereStr(tempUrl,whereStr,data,true);");
            AssertContains(CaseOf(html, "GoOrNone"),
                "tempUrl = ff.concatWhereStr(tempUrl,whereStr,objs[0],true);",
                "tempUrl = ff.concatWhereStr(tempUrl,whereStr,data,true);");
        }

        [TestMethod]
        public void ColumnNamesOfTheRowFields_StayAsTheyAre()
        {
            var html = RenderGrid(Action("Dialog", GridActionParameterTypesEnum.SingleId, x => x.ShowDialog = true, "SchoolCode", "Entity.Name"));

            AssertContains(CaseOf(html, "Dialog"), "whereStr=[\"SchoolCode\",\"Entity.Name\"];");
        }

        #endregion

        #region framework_layui.js (node)

        private static string FindUp(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        [TestMethod]
        public void NodeUnitTests_GridActionEncoding_Pass()
        {
            var script = FindUp(Path.Combine("test", "WalkingTec.Mvvm.Core.Test", "JsTests", "grid-action-encoding.test.js"));
            var js = FindUp(Path.Combine("src", "WalkingTec.Mvvm.Mvc", "framework_layui.js"));
            Assert.IsNotNull(script, "grid-action-encoding.test.js not found");
            Assert.IsNotNull(js, "framework_layui.js not found");

            var psi = new ProcessStartInfo("node")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(js);

            Process p;
            try
            {
                p = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Inconclusive("node is not available; skipping JavaScript unit tests.");
                return;
            }

            using (p)
            {
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(60000))
                {
                    p.Kill(true);
                    Assert.Fail("node test timed out");
                }
                Assert.AreEqual(0, p.ExitCode, stdout.Result + Environment.NewLine + stderr.Result);
            }
        }

        #endregion
    }
}
