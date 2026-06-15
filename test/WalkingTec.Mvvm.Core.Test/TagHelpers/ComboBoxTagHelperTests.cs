#nullable enable
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// ComboBoxTagHelper requires <c>IOptionsMonitor&lt;Configs&gt;</c> and <c>WTMContext</c>
/// in its constructor, making full Process() integration tests impractical without a live
/// DI container. These tests cover structural property existence and pure serialization logic.
/// </summary>
[TestClass]
public class ComboBoxTagHelperTests
{
    // ── Structural property-existence tests ──────────────────────────────────

    [TestMethod]
    public void ComboBoxTagHelper_HasEmptyTextProperty()
    {
        var prop = typeof(ComboBoxTagHelper).GetProperty("EmptyText", BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must have public EmptyText property");
        Assert.AreEqual(typeof(string), prop.PropertyType);
    }

    [TestMethod]
    public void ComboBoxTagHelper_HasEnableSearchProperty()
    {
        var prop = typeof(ComboBoxTagHelper).GetProperty("EnableSearch", BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must have public EnableSearch property");
        Assert.AreEqual(typeof(bool?), prop.PropertyType);
    }

    [TestMethod]
    public void ComboBoxTagHelper_HasMultiSelectProperty()
    {
        var prop = typeof(ComboBoxTagHelper).GetProperty("MultiSelect", BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must have public MultiSelect property");
        Assert.AreEqual(typeof(bool?), prop.PropertyType);
    }

    [TestMethod]
    public void ComboBoxTagHelper_HasRemoteUrlProperty()
    {
        var prop = typeof(ComboBoxTagHelper).GetProperty("RemoteUrl", BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must have public RemoteUrl property");
        Assert.AreEqual(typeof(string), prop.PropertyType);
    }

    [TestMethod]
    public void ComboBoxTagHelper_HasChangeFuncProperty()
    {
        var prop = typeof(ComboBoxTagHelper).GetProperty("ChangeFunc", BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must have public ChangeFunc property");
        Assert.AreEqual(typeof(string), prop.PropertyType);
    }

    [TestMethod]
    public void ComboBoxTagHelper_HasItemsProperty()
    {
        var prop = typeof(ComboBoxTagHelper).GetProperty("Items", BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(prop, "Must have public Items property");
    }

    // ── Pure serialization logic ──────────────────────────────────────────────

    [TestMethod]
    public void PureLogic_JsonSerializeStringList_ProducesValidJsonArray()
    {
        var items = new List<string> { "alpha", "beta", "gamma" };
        var json = JsonSerializer.Serialize(items);
        Assert.IsTrue(json.StartsWith("["), "JsonSerializer must produce JSON array starting with [");
        StringAssert.Contains(json, "\"alpha\"");
        StringAssert.Contains(json, "\"beta\"");
        StringAssert.Contains(json, "\"gamma\"");
    }

    [TestMethod]
    public void PureLogic_SelectValWithDoubleQuote_JsonSerializeEscapesCorrectly()
    {
        var values = new List<string> { "val\"ue", "normal" };
        var json = JsonSerializer.Serialize(values);
        // Serialized output must escape the inner double-quote
        Assert.IsTrue(json.Contains("val\\\"ue") || json.Contains("val\\u0022ue"),
            "Double quotes within string values must be escaped in JSON output");
        StringAssert.Contains(json, "normal");
    }
}
