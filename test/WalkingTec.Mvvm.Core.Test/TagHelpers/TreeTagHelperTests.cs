#nullable enable
using System.Net;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// TreeTagHelper requires <c>IOptionsMonitor&lt;Configs&gt;</c> in its constructor,
/// making full Process() integration tests impractical without a live DI container.
/// These tests cover structural property existence and pure HTML-encoding logic.
/// </summary>
[TestClass]
public class TreeTagHelperTests
{
    // ── Structural property-existence tests ──────────────────────────────────

    [TestMethod]
    public void TreeTagHelper_HasEmptyTextProperty()
    {
        var prop = typeof(TreeTagHelper).GetProperty("EmptyText", BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must have public EmptyText property");
        Assert.AreEqual(typeof(string), prop.PropertyType);
    }

    [TestMethod]
    public void TreeTagHelper_ShowLine_PropertyExistsAndIsBool()
    {
        var prop = typeof(TreeTagHelper).GetProperty("ShowLine", BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must have public ShowLine property");
        Assert.AreEqual(typeof(bool), prop.PropertyType);
    }

    [TestMethod]
    public void TreeTagHelper_HasEnableSearchProperty()
    {
        var prop = typeof(TreeTagHelper).GetProperty("EnableSearch", BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must have public EnableSearch property");
        Assert.AreEqual(typeof(bool?), prop.PropertyType);
    }

    [TestMethod]
    public void TreeTagHelper_HasLazyUrlProperty()
    {
        var prop = typeof(TreeTagHelper).GetProperty("LazyUrl", BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must have public LazyUrl property");
        Assert.AreEqual(typeof(string), prop.PropertyType);
    }

    [TestMethod]
    public void TreeTagHelper_HasChangeFuncProperty()
    {
        var prop = typeof(TreeTagHelper).GetProperty("ChangeFunc", BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must have public ChangeFunc property");
        Assert.AreEqual(typeof(string), prop.PropertyType);
    }

    [TestMethod]
    public void TreeTagHelper_HasItemsProperty()
    {
        var prop = typeof(TreeTagHelper).GetProperty("Items", BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must have public Items property");
    }

    // ── Pure HTML-encoding logic ──────────────────────────────────────────────

    [TestMethod]
    public void PureLogic_HtmlEncode_XssPayload_DoesNotContainRawAngleBrackets()
    {
        var raw = "'><script>alert(1)</script>";
        var encoded = WebUtility.HtmlEncode(raw);
        Assert.IsFalse(encoded.Contains("<script>"),
            "HtmlEncoded value must not contain raw <script>");
        Assert.IsFalse(encoded.Contains("'>"),
            "HtmlEncoded value must not contain attribute-breaking '>");
    }

    [TestMethod]
    public void PureLogic_HtmlEncode_PreservesNonSpecialText()
    {
        var raw = "Hello World";
        var encoded = WebUtility.HtmlEncode(raw);
        Assert.AreEqual("Hello World", encoded,
            "HtmlEncode must not alter plain text without special HTML chars");
    }
}
