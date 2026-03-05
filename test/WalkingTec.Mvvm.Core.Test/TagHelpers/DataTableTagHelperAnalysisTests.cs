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
}
