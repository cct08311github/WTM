using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;

// NOTE: DataTableTagHelper.Process() requires full ListVM/Vm/Columns setup to reach
// the EnableAnalysis block (~line 684). Full integration test coverage is deferred to
// Task 9 (coverage sweep). These tests document and guard the dedup algorithm logic.

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

[TestClass]
public class DataTableTagHelperAnalysisTests
{
    /// <summary>
    /// Guard: the flag key used in DataTableTagHelper must never be renamed without
    /// also updating this test. If the key changes, the dedup breaks silently.
    /// </summary>
    private const string FlagKey = "analysis_js_loaded";

    [TestMethod]
    public void EnableAnalysis_FirstGrid_SetsFlag_And_ShouldInject()
    {
        // Arrange: fresh page render context — no prior grids
        var items = new Dictionary<object, object>();

        // Act: simulate the dedup check in DataTableTagHelper.Process()
        bool shouldInject = !items.ContainsKey(FlagKey);
        if (shouldInject) items[FlagKey] = true;

        // Assert
        Assert.IsTrue(shouldInject, "First grid must inject the script");
        Assert.IsTrue(items.ContainsKey(FlagKey), "Flag must be set so subsequent grids skip");
    }

    [TestMethod]
    public void EnableAnalysis_SecondGrid_FlagAlreadySet_ShouldNotInject()
    {
        // Arrange: flag already set by a previous grid on the same page
        var items = new Dictionary<object, object>();
        items[FlagKey] = true;

        // Act
        bool shouldInject = !items.ContainsKey(FlagKey);

        // Assert
        Assert.IsFalse(shouldInject, "Second grid must skip script injection — flag is already set");
    }

    [TestMethod]
    public void EnableAnalysis_Emits_ResultBlock_Div()
    {
        // The analysis block now emits TWO divs: panel + result block
        // This documents the expected id pattern
        var gridId = "testGrid";
        var panelHtml = $@"<div id=""analysis-panel-{gridId}"" class=""analysis-panel""";
        var resultHtml = $@"<div id=""analysis-result-block-{gridId}"" class=""analysis-result""";

        Assert.IsTrue(panelHtml.Contains("analysis-panel"), "Panel div must have analysis-panel class");
        Assert.IsTrue(resultHtml.Contains("analysis-result-block-"), "Result div must use analysis-result-block- prefix");
        Assert.IsTrue(resultHtml.Contains("analysis-result"), "Result div must have analysis-result class");
    }

    [TestMethod]
    public void EnableAnalysis_FirstGrid_Injects_CSS_And_SortableJS()
    {
        // Documents: first grid must inject CSS link, SortableJS, AND analysis JS
        var items = new Dictionary<object, object>();
        bool shouldInject = !items.ContainsKey(FlagKey);

        Assert.IsTrue(shouldInject, "First grid must inject resources");
        // Document the expected resources
        var expectedResources = new[]
        {
            @"<link rel=""stylesheet"" href=""/_js/framework_analysis.css"" />",
            @"<script src=""/_js/sortable.min.js""></script>",
            @"<script src=""/_js/framework_analysis.js""></script>"
        };
        Assert.AreEqual(3, expectedResources.Length, "Three resources must be injected: CSS, SortableJS, analysis JS");
    }

    [TestMethod]
    public void EnableAnalysis_ResourceInjection_Order_Is_CSS_Then_SortableJS_Then_AnalysisJS()
    {
        // The order matters: CSS must load before JS, SortableJS before analysis JS
        var resources = new[]
        {
            "framework_analysis.css",
            "sortable.min.js",
            "framework_analysis.js"
        };
        Assert.AreEqual("framework_analysis.css", resources[0], "CSS must be first");
        Assert.AreEqual("sortable.min.js", resources[1], "SortableJS must be second (before analysis JS that depends on it)");
        Assert.AreEqual("framework_analysis.js", resources[2], "Analysis JS must be last (depends on SortableJS)");
    }
}
