using System;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Checkbox/radio option markup built in framework_layui.js must HTML-encode Value and Text (#262).
    /// </summary>
    [TestClass]
    public class OptionEncodingTests
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
        public void NodeUnitTests_OptionEncoding_Pass()
        {
            var script = FindUp(Path.Combine("test", "WalkingTec.Mvvm.Core.Test", "JsTests", "option-encoding.test.js"));
            var js = FindUp(Path.Combine("src", "WalkingTec.Mvvm.Mvc", "framework_layui.js"));
            Assert.IsNotNull(script, "option-encoding.test.js not found");
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
