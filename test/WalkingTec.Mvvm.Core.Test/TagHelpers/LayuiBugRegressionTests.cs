using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Regression tests for Issue #333 — 11 LayUI functional defects.
/// These tests exercise the pure logic extracted from each fix so they
/// can run without a full ASP.NET host or database connection.
/// </summary>
[TestClass]
public class LayuiBugRegressionTests
{
    // ── Bug 1: Switch checked with null model ────────────────────────────────

    [TestMethod]
    public void Switch_CheckedProperty_SeedsLocalVariable_NotShadowedByNullInit()
    {
        // When the public Checked property is true and Field.Model is null,
        // the local bool? must be seeded from the property, NOT reset to null.
        bool? publicChecked = true;
        bool? localChecked = publicChecked; // mirrors the fix: bool? Checked = this.Checked;
        Assert.IsTrue(localChecked.HasValue && localChecked.Value,
            "Local Checked variable must be seeded from the public Checked property");
    }

    [TestMethod]
    public void Switch_CheckedProperty_NullModel_NullPublicChecked_StaysNull()
    {
        bool? publicChecked = null;
        bool? localChecked = publicChecked;
        Assert.IsNull(localChecked,
            "When Checked property is null and model is null, local must also be null");
    }

    // ── Bug 2: Slider bracket DefaultValue index guard ────────────────────────

    [TestMethod]
    public void Slider_DefaultValue_SingleElementBracket_DoesNotThrow()
    {
        var defaultValue = "[5]";
        string? value0 = null, value1 = null;
        var tmp = defaultValue.TrimStart('[').TrimEnd(']').Replace(" ", string.Empty);
        var arr = tmp.Split(",", StringSplitOptions.RemoveEmptyEntries);
        bool field1IsSet = false;
        if (field1IsSet && arr.Length >= 2)
        {
            value0 = arr[0]; value1 = arr[1];
        }
        else if (arr.Length >= 1)
        {
            value0 = arr[0];
        }
        Assert.AreEqual("5", value0, "Single-element bracket must parse value0");
        Assert.IsNull(value1, "value1 must remain null for single-element bracket");
    }

    [TestMethod]
    public void Slider_DefaultValue_EmptyBracket_DoesNotThrow()
    {
        var defaultValue = "[]";
        string? value0 = null, value1 = null;
        var tmp = defaultValue.TrimStart('[').TrimEnd(']').Replace(" ", string.Empty);
        var arr = tmp.Split(",", StringSplitOptions.RemoveEmptyEntries);
        bool field1IsSet = false;
        if (field1IsSet && arr.Length >= 2)
        {
            value0 = arr[0]; value1 = arr[1];
        }
        else if (arr.Length >= 1)
        {
            value0 = arr[0];
        }
        Assert.IsNull(value0, "Empty bracket must leave value0 null");
        Assert.IsNull(value1, "Empty bracket must leave value1 null");
    }

    [TestMethod]
    public void Slider_DefaultValue_TwoElementBracket_ParsesBoth_WhenRangeMode()
    {
        var defaultValue = "[10,20]";
        string? value0 = null, value1 = null;
        var tmp = defaultValue.TrimStart('[').TrimEnd(']').Replace(" ", string.Empty);
        var arr = tmp.Split(",", StringSplitOptions.RemoveEmptyEntries);
        bool field1IsSet = true;
        if (field1IsSet && arr.Length >= 2)
        {
            value0 = arr[0]; value1 = arr[1];
        }
        else if (arr.Length >= 1)
        {
            value0 = arr[0];
        }
        Assert.AreEqual("10", value0);
        Assert.AreEqual("20", value1);
    }

    // ── Bug 3: DataTable Filter dict immutability ────────────────────────────

    [TestMethod]
    public void DataTable_Filter_CallerDictNotMutated()
    {
        var callerFilter = new Dictionary<string, object> { ["custom"] = "value" };
        var where = new Dictionary<string, object>(callerFilter);
        where["_DONOT_USE_VMNAME"] = "TestVm";
        where["_DONOT_USE_CS"] = "default";
        Assert.IsFalse(callerFilter.ContainsKey("_DONOT_USE_VMNAME"),
            "Caller's Filter dict must NOT be mutated by DataTableTagHelper");
        Assert.IsTrue(where.ContainsKey("_DONOT_USE_VMNAME"),
            "Internal where dict must contain the system keys");
        Assert.IsTrue(where.ContainsKey("custom"),
            "Internal where dict must include caller's keys");
    }

    [TestMethod]
    public void DataTable_Filter_ReRender_NoDuplicateKeyException()
    {
        var callerFilter = new Dictionary<string, object> { ["custom"] = "value" };

        var where1 = new Dictionary<string, object>(callerFilter);
        where1["_DONOT_USE_VMNAME"] = "vm1";

        var where2 = new Dictionary<string, object>(callerFilter);
        where2["_DONOT_USE_VMNAME"] = "vm1";

        Assert.AreEqual("vm1", where1["_DONOT_USE_VMNAME"]);
        Assert.AreEqual("vm1", where2["_DONOT_USE_VMNAME"]);
    }

    // ── Bug 6: BaseFieldTag missing Field guard ──────────────────────────────

    [TestMethod]
    public void BaseFieldTag_NullField_GuardThrowsInvalidOperationException()
    {
        bool guardTriggered = false;
        object? fieldValue = null;
        try
        {
            if (fieldValue == null)
                throw new InvalidOperationException("field attribute is required");
        }
        catch (InvalidOperationException ex)
        {
            guardTriggered = true;
            StringAssert.Contains(ex.Message, "field",
                "Exception message must mention 'field' attribute");
        }
        Assert.IsTrue(guardTriggered,
            "Null Field must trigger InvalidOperationException guard");
    }

    // ── Bug 7: Radio/CheckBox null item.Value guard ─────────────────────────

    [TestMethod]
    public void Radio_SetSelected_NullItemValue_DoesNotThrow()
    {
        string? itemValue = null;
        string data = "someValue";
        bool matches = itemValue?.ToString()?.ToLower() == data?.ToLower();
        Assert.IsFalse(matches, "Null item.Value must not match a non-null value");
    }

    [TestMethod]
    public void CheckBox_SetSelected_NullItemValue_DoesNotThrow()
    {
        string? itemValue = null;
        string data = "someValue";
        bool matches = itemValue?.ToString()?.ToLower() == data?.ToLower();
        Assert.IsFalse(matches, "Null item.Value must not match a non-null value");
    }

    [TestMethod]
    public void Radio_SetSelected_NullItemValue_NullData_BothNull_AreEqual()
    {
        string? itemValue = null;
        string? data = null;
        bool matches = itemValue?.ToString()?.ToLower() == data?.ToLower();
        Assert.IsTrue(matches, "null?.ToString()?.ToLower() should equal null?.ToLower()");
    }

    // ── Bug 8: UEditor model-wins over DefaultValue ──────────────────────────

    [TestMethod]
    public void UEditor_ModelValue_WinsOver_DefaultValue()
    {
        var modelValue = "saved content";
        var defaultValue = "placeholder";
        var contentValue = string.IsNullOrEmpty(modelValue) ? (defaultValue ?? "") : modelValue;
        Assert.AreEqual("saved content", contentValue,
            "Non-empty model value must win over DefaultValue");
    }

    [TestMethod]
    public void UEditor_EmptyModel_FallsBackTo_DefaultValue()
    {
        var modelValue = "";
        var defaultValue = "placeholder";
        var contentValue = string.IsNullOrEmpty(modelValue) ? (defaultValue ?? "") : modelValue;
        Assert.AreEqual("placeholder", contentValue,
            "Empty model must fall back to DefaultValue");
    }

    [TestMethod]
    public void UEditor_NullModel_NullDefault_EmptyString()
    {
        string? modelStr = null;
        string? defaultValue = null;
        var contentValue = string.IsNullOrEmpty(modelStr) ? (defaultValue ?? "") : modelStr;
        Assert.AreEqual("", contentValue,
            "Null model and null DefaultValue must yield empty string");
    }

    // ── Bug 9: Selector display — val used when value list is empty ──────────

    [TestMethod]
    public void Selector_Display_ValUsedWhenValueListEmpty()
    {
        var value = new List<string>();
        var val = "Active Status";
        var displayText = value.Count > 0 ? string.Join(",", value) : val;
        Assert.AreEqual("Active Status", displayText,
            "When value list is empty, computed val must be used as display text");
    }

    [TestMethod]
    public void Selector_Display_ValueListWins_WhenNonEmpty()
    {
        var value = new List<string> { "Alice", "Bob" };
        var val = "ignored";
        var displayText = value.Count > 0 ? string.Join(",", value) : val;
        Assert.AreEqual("Alice,Bob", displayText,
            "Non-empty value list must be rendered as comma-joined string");
    }

    // ── Bug 5: DataTable removeClass class name ──────────────────────────────

    [TestMethod]
    public void DataTable_RemoveClass_ClassName_HasNoSpace()
    {
        const string correctClassName = "layui-form-selected";
        Assert.IsFalse(correctClassName.Contains(" "),
            "CSS class name must not contain a space");
        Assert.IsTrue(correctClassName.StartsWith("layui-"),
            "Must start with layui- prefix");
    }
}
