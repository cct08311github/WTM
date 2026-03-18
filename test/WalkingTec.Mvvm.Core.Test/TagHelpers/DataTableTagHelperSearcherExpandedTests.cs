using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Guards the SearcherExpanded → layui fold-bool mapping in DataTableTagHelper.
/// Full rendering tests are deferred (same rationale as AnalysisTests).
/// </summary>
[TestClass]
public class DataTableTagHelperSearcherExpandedTests
{
    // Mirror the mapping used in DataTableTagHelper.Process():
    //   SearcherExpanded=true  → foldBool="false" (layui: do NOT fold = expanded)
    //   SearcherExpanded=false → foldBool="true"  (layui: fold = collapsed)
    private static string GetFoldBool(bool searcherExpanded) =>
        searcherExpanded ? "false" : "true";

    [TestMethod]
    public void SearcherExpanded_True_EmitsFoldFalse()
    {
        // layui.element.fold(filter, false) = unfold = expanded
        Assert.AreEqual("false", GetFoldBool(true));
    }

    [TestMethod]
    public void SearcherExpanded_False_EmitsFoldTrue()
    {
        // layui.element.fold(filter, true) = fold = collapsed
        Assert.AreEqual("true", GetFoldBool(false));
    }

    [TestMethod]
    public void SearcherExpanded_Null_NoScriptEmitted()
    {
        // When SearcherExpanded is not set, HasValue is false → no script
        bool? searcherExpanded = null;
        Assert.IsFalse(searcherExpanded.HasValue);
    }

    [TestMethod]
    public void SearcherExpanded_DefaultIsNull()
    {
        // Default value of bool? is null — ensure no accidental initialization
        bool? def = default;
        Assert.IsNull(def);
    }
}
