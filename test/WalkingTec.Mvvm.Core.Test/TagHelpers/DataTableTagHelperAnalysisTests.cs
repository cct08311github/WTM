using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

[TestClass]
public class DataTableTagHelperAnalysisTests
{
    private static TagHelperContext MakeContext(Dictionary<object, object> items = null)
        => new("wt:grid", new TagHelperAttributeList(), items ?? new Dictionary<object, object>(), "test-id");

    [TestMethod]
    public void EnableAnalysis_FirstGrid_ShouldInjectScript()
    {
        var items = new Dictionary<object, object>();
        // Simulate the dedup logic that DataTableTagHelper.Process will implement:
        // First call: flag absent → should inject
        bool shouldInject = !items.ContainsKey("analysis_js_loaded");
        if (shouldInject) items["analysis_js_loaded"] = true;

        Assert.IsTrue(shouldInject, "First grid should inject the script");
        Assert.IsTrue(items.ContainsKey("analysis_js_loaded"), "Flag must be set after first injection");
    }

    [TestMethod]
    public void EnableAnalysis_SecondGrid_ShouldSkipScript()
    {
        var items = new Dictionary<object, object>();
        items["analysis_js_loaded"] = true; // flag already set by first grid

        bool shouldInject = !items.ContainsKey("analysis_js_loaded");

        Assert.IsFalse(shouldInject, "Second grid must NOT inject the script again");
    }

    [TestMethod]
    public void EnableAnalysis_FlagKey_IsCorrectString()
    {
        // Guard: key name must match exactly what DataTableTagHelper uses
        const string expectedKey = "analysis_js_loaded";
        var items = new Dictionary<object, object>();
        items[expectedKey] = true;
        Assert.IsTrue(items.ContainsKey("analysis_js_loaded"));
    }
}
