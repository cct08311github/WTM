using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.TagHelpers.LayUI.Common;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Server script responses (IsScript) must go through the single ff.ExecServerScript entry point (#25, phase 2).
    /// </summary>
    [TestClass]
    public class ServerScriptChannelTests
    {
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
        public void DialogButton_WithoutDialog_UsesExecServerScript()
        {
            var html = new LayuiUIService().MakeDialogButton(ButtonTypesEnum.Button, "/Test/Action", "Go", null, null, showDialog: false);

            StringAssert.Contains(html, "ff.ExecServerScript(data);");
            Assert.IsFalse(html.Contains("eval("), "the dialog button must not call eval directly");
        }

        [TestMethod]
        public void FrameworkLayuiJs_EvalIsOnlyInsideExecServerScript()
        {
            using var stream = typeof(_CodeGenController).Assembly.GetManifestResourceStream("WalkingTec.Mvvm.Mvc.framework_layui.js");
            Assert.IsNotNull(stream);
            using var reader = new StreamReader(stream);
            var js = reader.ReadToEnd();

            Assert.IsFalse(js.Contains("eval("), "direct eval( calls must not be reintroduced");
            var first = js.IndexOf("(0, eval)(", StringComparison.Ordinal);
            Assert.IsTrue(first >= 0, "ff.ExecServerScript should keep the single indirect eval");
            Assert.AreEqual(-1, js.IndexOf("(0, eval)(", first + 1, StringComparison.Ordinal), "only one eval call site is expected");
        }

        [TestMethod]
        public void NodeUnitTests_ServerScriptChannel_Pass()
        {
            var script = FindUp(Path.Combine("test", "WalkingTec.Mvvm.Core.Test", "JsTests", "script-channel.test.js"));
            var js = FindUp(Path.Combine("src", "WalkingTec.Mvvm.Mvc", "framework_layui.js"));
            Assert.IsNotNull(script, "script-channel.test.js not found");
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
    }
}
