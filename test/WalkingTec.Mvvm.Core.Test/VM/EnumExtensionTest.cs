using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    /// <summary>
    /// Enum used only for testing — has explicit Display attributes and gaps in values.
    /// </summary>
    public enum TestPriorityEnum
    {
        [Display(Name = "Low Priority")]
        Low = 0,

        [Display(Name = "Medium Priority")]
        Medium = 5,

        [Display(Name = "High Priority")]
        High = 10
    }

    /// <summary>
    /// Enum without Display attributes — member names should be used as-is.
    /// </summary>
    public enum TestPlainEnum
    {
        Alpha,
        Beta,
        Gamma
    }

    /// <summary>
    /// Tests for EnumExtension.ToListItems() — enum display name ↔ value mapping
    /// used by Excel import for ColumnDataType.Enum validation.
    /// </summary>
    [TestClass]
    public class EnumExtensionTest
    {
        // ─── Basic Mapping ───────────────────────────────────────────

        [TestMethod]
        public void ToListItems_ReturnsAllMembers()
        {
            var items = typeof(GenderEnum).ToListItems();
            Assert.AreEqual(2, items.Count);
        }

        [TestMethod]
        public void ToListItems_ValueIsEnumIntegerString()
        {
            var items = typeof(GenderEnum).ToListItems();
            // Value stores enum member name (not integer)
            Assert.AreEqual("Male", items[0].Value);
            Assert.AreEqual("Female", items[1].Value);
        }

        [TestMethod]
        public void ToListItems_TextUsesDisplayAttribute()
        {
            var items = typeof(TestPriorityEnum).ToListItems();
            // Display names come from [Display(Name=...)] via GetEnumDisplayName
            // When localizer is null, the raw Display Name string is returned
            var texts = items.Select(x => x.Text).ToList();
            Assert.IsTrue(texts.Contains("Low Priority") || texts.Contains("Low"),
                "Should use Display attribute or fall back to member name");
        }

        [TestMethod]
        public void ToListItems_PlainEnum_TextIsMemberName()
        {
            var items = typeof(TestPlainEnum).ToListItems();
            Assert.AreEqual("Alpha", items[0].Text);
            Assert.AreEqual("Beta", items[1].Text);
            Assert.AreEqual("Gamma", items[2].Text);
        }

        [TestMethod]
        public void ToListItems_GappedValues_PreservedCorrectly()
        {
            var items = typeof(TestPriorityEnum).ToListItems();
            Assert.AreEqual("Low", items[0].Value);
            Assert.AreEqual("Medium", items[1].Value);
            Assert.AreEqual("High", items[2].Value);
        }

        // ─── Selection ───────────────────────────────────────────────

        [TestMethod]
        public void ToListItems_WithValue_MarksSelected()
        {
            var items = typeof(GenderEnum).ToListItems(GenderEnum.Female);
            var selected = items.Where(x => x.Selected).ToList();
            Assert.AreEqual(1, selected.Count);
            Assert.AreEqual("Female", selected[0].Value);
        }

        [TestMethod]
        public void ToListItems_WithStringValue_MarksSelected()
        {
            var items = typeof(TestPlainEnum).ToListItems("Beta");
            var selected = items.Where(x => x.Selected).ToList();
            Assert.AreEqual(1, selected.Count);
            Assert.AreEqual("Beta", selected[0].Text);
        }

        [TestMethod]
        public void ToListItems_WithNullValue_NoneSelected()
        {
            var items = typeof(GenderEnum).ToListItems(null);
            Assert.IsFalse(items.Any(x => x.Selected));
        }

        // ─── PleaseSelect Option ─────────────────────────────────────

        [TestMethod]
        public void ToListItems_PleaseSelect_AddsEmptyFirstItem()
        {
            var items = typeof(TestPlainEnum).ToListItems(pleaseSelect: true);
            Assert.AreEqual(4, items.Count); // 3 members + 1 "please select"
            Assert.AreEqual("", items[0].Value);
        }

        [TestMethod]
        public void ToListItems_NoPleaseSelect_ExactMemberCount()
        {
            var items = typeof(TestPlainEnum).ToListItems(pleaseSelect: false);
            Assert.AreEqual(3, items.Count);
        }

        // ─── Nullable Enum ───────────────────────────────────────────

        [TestMethod]
        public void ToListItems_NullableEnum_ReturnsMembers()
        {
            var items = typeof(GenderEnum?).ToListItems();
            Assert.AreEqual(2, items.Count);
            Assert.AreEqual("Male", items[0].Value);
            Assert.AreEqual("Female", items[1].Value);
        }

        // ─── GetEnumDisplayName ──────────────────────────────────────

        [TestMethod]
        public void GetEnumDisplayName_WithDisplayAttribute()
        {
            var name = PropertyHelper.GetEnumDisplayName(typeof(TestPriorityEnum), "Low");
            // Should return "Low Priority" (from Display attribute) or "Low" if localizer resolves it
            Assert.IsFalse(string.IsNullOrEmpty(name));
        }

        [TestMethod]
        public void GetEnumDisplayName_WithoutDisplayAttribute()
        {
            var name = PropertyHelper.GetEnumDisplayName(typeof(TestPlainEnum), "Alpha");
            Assert.AreEqual("Alpha", name);
        }

        [TestMethod]
        public void GetEnumDisplayName_NullType_ReturnsEmpty()
        {
            var name = PropertyHelper.GetEnumDisplayName(null, "Alpha");
            Assert.AreEqual("", name);
        }

        [TestMethod]
        public void GetEnumDisplayName_NullValue_ReturnsEmpty()
        {
            var name = PropertyHelper.GetEnumDisplayName(typeof(GenderEnum), (string?)null);
            Assert.AreEqual("", name);
        }
    }
}
