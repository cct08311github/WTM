using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Mvc.Tests
{
    /// <summary>
    /// Regression tests for Issue #297: embedded JS/CSS assets referenced from
    /// framework views must all be present as manifest resources in the Mvc assembly.
    ///
    /// The <c>UseWtmStaticFiles</c> extension wires an <see cref="Microsoft.Extensions.FileProviders.EmbeddedFileProvider"/>
    /// with base namespace <c>WalkingTec.Mvvm.Mvc</c>, so every <c>/_js/&lt;file&gt;</c>
    /// URL served at runtime resolves to <c>WalkingTec.Mvvm.Mvc.&lt;file&gt;</c> in the
    /// assembly manifest.  A missing entry causes an HTTP 404 at runtime.
    /// </summary>
    [TestClass]
    public class EmbeddedJsAssetTests
    {
        private static readonly Assembly MvcAssembly =
            typeof(WalkingTec.Mvvm.Mvc._FrameworkController).Assembly;

        private static readonly string[] ViewReferencedAssets =
        [
            // Views/_DashboardPage/Index.cshtml, Render.cshtml, Designer.cshtml
            "framework_dashboard.js",
            "framework_dashboard.css",
            // Views/_DashboardPage/Designer.cshtml — #297
            "framework_dashboard_designer.js",
            // Views/_CodeGen/Index.cshtml, SetField.cshtml, Gen.cshtml
            "framework_layui.js",
        ];

        private static string ResourceName(string fileName) =>
            $"WalkingTec.Mvvm.Mvc.{fileName}";

        /// <summary>
        /// Asserts that <c>framework_dashboard_designer.js</c> is embedded in the
        /// Mvc assembly.  Regression guard for Issue #297 where the csproj entry
        /// was missing, causing an HTTP 404 for the no-code dashboard designer page.
        /// </summary>
        [TestMethod]
        public void DashboardDesignerJs_IsEmbeddedInMvcAssembly()
        {
            var manifests = MvcAssembly.GetManifestResourceNames();
            manifests.Should().Contain(
                ResourceName("framework_dashboard_designer.js"),
                because: "Designer.cshtml references /_js/framework_dashboard_designer.js " +
                         "which is served via EmbeddedFileProvider; a missing csproj entry causes HTTP 404");
        }

        /// <summary>
        /// Loop-asserts that every <c>/_js/framework_*</c> asset referenced from a
        /// framework view has a matching manifest resource in the Mvc assembly.
        /// Prevents the whole class of missing-EmbeddedResource regressions.
        /// </summary>
        [TestMethod]
        public void AllViewReferencedFrameworkAssets_AreEmbeddedInMvcAssembly()
        {
            var manifests = MvcAssembly.GetManifestResourceNames();

            var missing = ViewReferencedAssets
                .Select(ResourceName)
                .Where(name => !manifests.Contains(name, StringComparer.Ordinal))
                .ToList();

            missing.Should().BeEmpty(
                because: "every /_js/<file> referenced from a framework view must exist as " +
                         $"WalkingTec.Mvvm.Mvc.<file> in the Mvc assembly manifest; " +
                         $"missing: {string.Join(", ", missing)}");
        }
    }
}
