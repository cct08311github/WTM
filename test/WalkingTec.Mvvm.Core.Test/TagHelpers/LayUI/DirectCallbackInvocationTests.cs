using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Issue #25 (phase 1): wt:tree / wt:combobox call the configured ChangeFunc directly
    /// instead of evaluating a generated string. Ordinary callbacks must keep working; the generated
    /// scripts are parsed with node when it is available.
    /// </summary>
    [TestClass]
    public class DirectCallbackInvocationTests : LayUITagHelperTestBase
    {
        private static LayuiTestVM NewVm() => new LayuiTestVM();

        private static string FormIdOf(BaseVM vm) => FormTagHelper.FORM_ID_PREFIX + vm.UniqueId;

        #region node helpers

        private static string FindNode()
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                foreach (var name in new[] { "node", "node.exe" })
                {
                    try
                    {
                        var candidate = Path.Combine(dir, name);
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch (ArgumentException) { }
                }
            }
            return null;
        }

        private static string RequireNode()
        {
            var node = FindNode();
            if (node == null)
            {
                Assert.Inconclusive("node is not available, the generated script cannot be parsed or executed.");
            }
            return node;
        }

        private static (int ExitCode, string Output) RunNode(string node, string driver, params string[] args)
        {
            var dir = Path.Combine(Path.GetTempPath(), "wtm-callback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var driverPath = Path.Combine(dir, "driver.js");
                File.WriteAllText(driverPath, driver);
                var psi = new ProcessStartInfo(node) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                psi.ArgumentList.Add(driverPath);
                foreach (var a in args)
                {
                    psi.ArgumentList.Add(a);
                }
                using var p = Process.Start(psi);
                var stdout = p.StandardOutput.ReadToEnd();
                var stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                return (p.ExitCode, stdout + stderr);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        private static string ScriptOf(string html)
        {
            var matches = Regex.Matches(html, "<script[^>]*>(.*?)</script>", RegexOptions.Singleline);
            Assert.IsTrue(matches.Count > 0, "no <script> found in: " + html);
            var parts = new List<string>();
            foreach (Match m in matches)
            {
                parts.Add(m.Groups[1].Value);
            }
            return string.Join("\n", parts);
        }

        private const string ParseDriver = @"
const vm = require('vm'), fs = require('fs');
new vm.Script(fs.readFileSync(process.argv[2], 'utf8'));
console.log('ok');";

        private static void AssertParses(string node, string script)
        {
            var file = Path.Combine(Path.GetTempPath(), "wtm-script-" + Guid.NewGuid().ToString("N") + ".js");
            File.WriteAllText(file, script);
            try
            {
                var (code, output) = RunNode(node, ParseDriver, file);
                Assert.AreEqual(0, code, "generated script does not parse:" + Environment.NewLine + output + Environment.NewLine + script);
            }
            finally
            {
                File.Delete(file);
            }
        }

        #endregion

        #region tree

        private static string RenderTree(string changeFunc, string linkId)
        {
            var vm = NewVm();
            vm.Menus = new List<TreeSelectListItem> { new TreeSelectListItem { Text = "root", Value = "1" } };
            var helper = new TreeTagHelper(Options())
            {
                Field = Field(vm, x => x.Names),
                Items = Field(vm, x => x.Menus),
                ChangeFunc = changeFunc,
                LinkId = linkId,
                TriggerUrl = "/api/x",
            };
            return Render(helper, "wt:tree");
        }

        [TestMethod]
        public void Tree_WithLinkAndChangeFunc_CallsFunctionDirectly()
        {
            var html = RenderTree("onPick(a, b)", "other");

            AssertNotContains(html, "eval(");
            AssertContains(html, "if ((onPick(data)) != false) {", "ff.ChainChange(u, $('#");
            AssertParses(RequireNode(), ScriptOf(html));
        }

        [TestMethod]
        public void Tree_WithLinkAndDottedChangeFunc_CallsFunctionDirectly()
        {
            var html = RenderTree("app.handlers.onPick", "other");

            AssertNotContains(html, "eval(");
            AssertContains(html, "if ((app.handlers.onPick(data)) != false) {");
        }

        [TestMethod]
        public void Tree_WithLinkAndNoChangeFunc_LinksWithoutCallback()
        {
            var html = RenderTree(null, "other");

            AssertNotContains(html, "eval(");
            AssertContains(html, "if ((1==1) != false) {", "ff.ChainChange(u, $('#");
            AssertParses(RequireNode(), ScriptOf(html));
        }

        [TestMethod]
        public void Tree_WithLinkAndInvalidChangeFuncName_DoesNotEmitTheName()
        {
            var html = RenderTree("x\"); alert(1); (\"", "other");

            AssertNotContains(html, "eval(", "alert");
            AssertContains(html, "if ((1==1) != false) {");
            AssertParses(RequireNode(), ScriptOf(html));
        }

        [TestMethod]
        public void Tree_WithoutLink_ChangeFuncIsTheOnHandlerAsBefore()
        {
            var html = RenderTree("onPick", null);

            AssertNotContains(html, "eval(", "ff.ChainChange");
            AssertContains(html, "on:function(data){\n        onPick(data)\n   },", "on:function(data){");
        }

        #endregion

        #region combobox

        private static string RenderCombo(string changeFunc, string linkId)
        {
            var helper = new ComboBoxTagHelper(Options(), null)
            {
                Field = Field(NewVm(), x => x.SchoolId),
                ItemUrl = "/api/schools",
                ChangeFunc = changeFunc,
                LinkId = linkId,
                TriggerUrl = "/api/x",
            };
            return Render(helper, "wt:combobox");
        }

        [TestMethod]
        public void ComboBox_WithLinkAndChangeFunc_CallsFunctionDirectly()
        {
            var html = RenderCombo("onPick(a, b)", "other");

            AssertNotContains(html, "eval(");
            AssertContains(html, "if ((onPick(data)) != false) {", "ff.ChainChange(u, $('#");
            AssertParses(RequireNode(), ScriptOf(html));
        }

        [TestMethod]
        public void ComboBox_WithLinkAndDottedChangeFunc_CallsFunctionDirectly()
        {
            var html = RenderCombo("app.handlers.onPick", "other");

            AssertNotContains(html, "eval(");
            AssertContains(html, "if ((app.handlers.onPick(data)) != false) {");
        }

        [TestMethod]
        public void ComboBox_WithLinkAndNoChangeFunc_LinksWithoutCallback()
        {
            var html = RenderCombo(null, "other");

            AssertNotContains(html, "eval(");
            AssertContains(html, "if ((1==1) != false) {", "ff.ChainChange(u, $('#");
            AssertParses(RequireNode(), ScriptOf(html));
        }

        [TestMethod]
        public void ComboBox_WithLinkAndInvalidChangeFuncName_DoesNotEmitTheName()
        {
            var html = RenderCombo("x\"); alert(1); (\"", "other");

            AssertNotContains(html, "eval(", "alert(1)");
            AssertContains(html, "if ((1==1) != false) {");
            AssertParses(RequireNode(), ScriptOf(html));
        }

        [TestMethod]
        public void ComboBox_WithoutLink_ChangeFuncIsTheOnHandlerAsBefore()
        {
            var html = RenderCombo("onPick", null);

            AssertNotContains(html, "eval(", "ff.ChainChange");
            AssertContains(html, "onPick(data)");
        }

        #endregion

    }
}
