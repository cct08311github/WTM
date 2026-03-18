using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Unit tests for DataTableTagHelper column-fixed and resize features (issue #612).
/// Tests target the internal ParseFieldSet helper directly, keeping the suite fast
/// (no full TagHelper stack required).
/// </summary>
[TestClass]
public class DataTableTagHelperColumnFixedTests
{
    // ── ParseFieldSet ─────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseFieldSet_Null_ReturnsNull()
    {
        var result = DataTableTagHelper.ParseFieldSet(null);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void ParseFieldSet_Empty_ReturnsNull()
    {
        var result = DataTableTagHelper.ParseFieldSet("   ");
        Assert.IsNull(result);
    }

    [TestMethod]
    public void ParseFieldSet_SingleField_ReturnsSingletonSet()
    {
        var result = DataTableTagHelper.ParseFieldSet("Name");
        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Count);
        Assert.IsTrue(result.Contains("Name"));
    }

    [TestMethod]
    public void ParseFieldSet_MultipleFields_ReturnsAllFields()
    {
        var result = DataTableTagHelper.ParseFieldSet("Name, Code, Status");
        Assert.IsNotNull(result);
        Assert.AreEqual(3, result.Count);
        Assert.IsTrue(result.Contains("Name"));
        Assert.IsTrue(result.Contains("Code"));
        Assert.IsTrue(result.Contains("Status"));
    }

    [TestMethod]
    public void ParseFieldSet_IsCaseInsensitive()
    {
        var result = DataTableTagHelper.ParseFieldSet("name,CODE");
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Contains("NAME"), "Should match uppercase");
        Assert.IsTrue(result.Contains("code"), "Should match lowercase");
        Assert.IsTrue(result.Contains("Name"), "Should match mixed case");
    }

    [TestMethod]
    public void ParseFieldSet_TrimsWhitespace()
    {
        var result = DataTableTagHelper.ParseFieldSet("  Name  ,  Code  ");
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Contains("Name"));
        Assert.IsTrue(result.Contains("Code"));
    }

    [TestMethod]
    public void ParseFieldSet_EmptySegmentsIgnored()
    {
        // Leading/trailing commas or double-commas produce empty segments.
        var result = DataTableTagHelper.ParseFieldSet(",Name,,Code,");
        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.Contains("Name"));
        Assert.IsTrue(result.Contains("Code"));
    }

    [TestMethod]
    public void ParseFieldSet_AllEmptySegments_ReturnsNull()
    {
        var result = DataTableTagHelper.ParseFieldSet(",,,");
        Assert.IsNull(result, "All-empty segments should yield null, not an empty set");
    }

    // ── Fixed-override resolution logic ───────────────────────────────────────

    [TestMethod]
    public void FixedLeftSet_ContainsField_ShouldOverrideToLeft()
    {
        var leftSet = DataTableTagHelper.ParseFieldSet("Name");
        var rightSet = DataTableTagHelper.ParseFieldSet(null);

        // Simulate the resolution logic in generateColHeaderCore.
        string? field = "Name";
        var resolved = ResolveFixed(field, leftSet, rightSet);

        Assert.AreEqual(WalkingTec.Mvvm.Core.GridColumnFixedEnum.Left, resolved);
    }

    [TestMethod]
    public void FixedRightSet_ContainsField_ShouldOverrideToRight()
    {
        var leftSet = DataTableTagHelper.ParseFieldSet(null);
        var rightSet = DataTableTagHelper.ParseFieldSet("Status");

        string field = "Status";
        var resolved = ResolveFixed(field, leftSet, rightSet);

        Assert.AreEqual(WalkingTec.Mvvm.Core.GridColumnFixedEnum.Right, resolved);
    }

    [TestMethod]
    public void FieldNotInEitherSet_ShouldKeepOriginalFixed()
    {
        var leftSet = DataTableTagHelper.ParseFieldSet("Name");
        var rightSet = DataTableTagHelper.ParseFieldSet("Status");

        // Column "Code" not in either set — original (null) should be preserved.
        string field = "Code";
        WalkingTec.Mvvm.Core.GridColumnFixedEnum? original = null;
        var resolved = ResolveFixed(field, leftSet, rightSet, original);

        Assert.IsNull(resolved);
    }

    [TestMethod]
    public void NullField_NoOverrideApplied()
    {
        var leftSet = DataTableTagHelper.ParseFieldSet("Name");
        string field = null;
        var resolved = ResolveFixed(field, leftSet, null);

        Assert.IsNull(resolved);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors the resolution logic in DataTableTagHelper.generateColHeaderCore.
    /// </summary>
    private static WalkingTec.Mvvm.Core.GridColumnFixedEnum? ResolveFixed(
        string field,
        System.Collections.Generic.HashSet<string> leftSet,
        System.Collections.Generic.HashSet<string> rightSet,
        WalkingTec.Mvvm.Core.GridColumnFixedEnum? original = null)
    {
        var resolved = original;
        if (field != null && leftSet?.Contains(field) == true)
            resolved = WalkingTec.Mvvm.Core.GridColumnFixedEnum.Left;
        else if (field != null && rightSet?.Contains(field) == true)
            resolved = WalkingTec.Mvvm.Core.GridColumnFixedEnum.Right;
        return resolved;
    }
}
