using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// framework_layui.js is the client half of every LayUI TagHelper (ff.RenderForm, ff.OpenDialog, ...).
    /// It ships as an embedded resource of WalkingTec.Mvvm.Mvc and is served under /_js; losing it breaks every page.
    /// </summary>
    [TestClass]
    public class FrameworkLayuiResourceTests
    {
        private const string ResourceName = "WalkingTec.Mvvm.Mvc.framework_layui.js";

        private static Assembly MvcAssembly => typeof(_CodeGenController).Assembly;

        private static string ReadResource(string name)
        {
            using var stream = MvcAssembly.GetManifestResourceStream(name);
            Assert.IsNotNull(stream, $"Embedded resource {name} was not found in {MvcAssembly.GetName().Name}");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        [TestMethod]
        public void FrameworkLayuiJs_IsEmbeddedInMvcAssembly()
        {
            var names = MvcAssembly.GetManifestResourceNames();

            CollectionAssert.Contains(names, ResourceName);
        }

        [TestMethod]
        public void FrameworkLayuiJs_IsNotEmptyAndDefinesCoreFunctions()
        {
            var js = ReadResource(ResourceName);

            Assert.IsTrue(js.Length > 10_000, "framework_layui.js looks truncated, length=" + js.Length);
            // functions emitted TagHelper scripts call
            foreach (var fn in new[] { "RenderForm", "OpenDialog", "ChainChange", "PostForm", "GetSearchFormData", "LoadComboItems", "CloseDialog", "BgRequest", "RefreshChart" })
            {
                StringAssert.Contains(js, fn + ":", $"framework_layui.js no longer defines ff.{fn}");
            }
        }

        [TestMethod]
        public void FrameworkLayuiJs_IsServedByTheEmbeddedFileProviderUsedForSlashJs()
        {
            // UseWtmStaticFiles maps /_js to EmbeddedFileProvider(Mvc assembly, "WalkingTec.Mvvm.Mvc")
            var provider = new EmbeddedFileProvider(MvcAssembly, "WalkingTec.Mvvm.Mvc");

            var file = provider.GetFileInfo("framework_layui.js");

            Assert.IsTrue(file.Exists, "/_js/framework_layui.js would 404");
            Assert.IsTrue(file.Length > 0);
        }

        [TestMethod]
        public void LayuiViewsAndCodeGeneratorTemplates_AreEmbedded()
        {
            var names = MvcAssembly.GetManifestResourceNames().ToHashSet(StringComparer.Ordinal);

            foreach (var expected in new[]
            {
                "WalkingTec.Mvvm.Mvc.Views._Framework.Selector.cshtml",
                "WalkingTec.Mvvm.Mvc.Views._CodeGen.Index.cshtml",
                "WalkingTec.Mvvm.Mvc.GeneratorFiles.Mvc.Controller.txt",
                "WalkingTec.Mvvm.Mvc.GeneratorFiles.Mvc.ListView.txt",
                "WalkingTec.Mvvm.Mvc.GeneratorFiles.Mvc.EditView.txt",
                "WalkingTec.Mvvm.Mvc.GeneratorFiles.Mvc.ImportView.txt",
            })
            {
                Assert.IsTrue(names.Contains(expected), $"Embedded resource {expected} is missing");
            }
        }
    }
}
