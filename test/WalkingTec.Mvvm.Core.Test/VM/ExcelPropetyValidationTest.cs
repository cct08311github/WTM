using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    /// <summary>
    /// Test entity with diverse property types for ExcelPropety validation testing.
    /// </summary>
    public class ExcelTestEntity : BasePoco
    {
        [Required]
        [StringLength(50)]
        public string Name { get; set; } = "";

        public int Count { get; set; }

        public decimal Price { get; set; }

        public bool IsActive { get; set; }

        public DateTime CreatedDate { get; set; }

        public DateTime? OptionalDate { get; set; }

        public GenderEnum Gender { get; set; }

        public int? OptionalCount { get; set; }
    }

    /// <summary>
    /// Tests for ExcelPropety.ValueValidity() — field-level type validation
    /// during Excel import. Verifies error detection for invalid numbers,
    /// dates, enums, booleans, and nullable handling.
    /// </summary>
    [TestClass]
    public class ExcelPropetyValidationTest
    {
        // ─── CreateProperty Type Detection ────────────────────────────

        [TestMethod]
        public void CreateProperty_StringField_DetectsTextType()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.Name);
            Assert.AreEqual(ColumnDataType.Text, prop.DataType);
        }

        [TestMethod]
        public void CreateProperty_IntField_DetectsNumberType()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.Count);
            Assert.AreEqual(ColumnDataType.Number, prop.DataType);
        }

        [TestMethod]
        public void CreateProperty_DecimalField_DetectsFloatType()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.Price);
            Assert.AreEqual(ColumnDataType.Float, prop.DataType);
        }

        [TestMethod]
        public void CreateProperty_BoolField_DetectsBoolType()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.IsActive);
            Assert.AreEqual(ColumnDataType.Bool, prop.DataType);
        }

        [TestMethod]
        public void CreateProperty_DateTimeField_DetectsDateType()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.CreatedDate);
            Assert.AreEqual(ColumnDataType.Date, prop.DataType);
        }

        [TestMethod]
        public void CreateProperty_DateTimeWithFlag_DetectsDateTimeType()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.CreatedDate, true);
            Assert.AreEqual(ColumnDataType.DateTime, prop.DataType);
        }

        [TestMethod]
        public void CreateProperty_EnumField_DetectsEnumType()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.Gender);
            Assert.AreEqual(ColumnDataType.Enum, prop.DataType);
        }

        [TestMethod]
        public void CreateProperty_NullableInt_IsNullable()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.OptionalCount);
            Assert.IsTrue(prop.IsNullAble, "Nullable<int> without [Required] should be nullable");
        }

        [TestMethod]
        public void CreateProperty_NullableDateTime_IsNullable()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.OptionalDate);
            Assert.IsTrue(prop.IsNullAble, "Nullable<DateTime> should be nullable");
        }

        [TestMethod]
        public void CreateProperty_RequiredString_IsNotNullable()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.Name);
            Assert.IsFalse(prop.IsNullAble, "[Required] string should not be nullable");
        }

        // ─── Number Validation ────────────────────────────────────────

        [TestMethod]
        public void ValueValidity_ValidNumber_NoError()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.Count);
            var errors = new List<ErrorMessage>();

            prop.ValueValidity("42", errors, 1);

            Assert.AreEqual(0, errors.Count);
            Assert.AreEqual(42, prop.Value);
        }

        [TestMethod]
        public void ValueValidity_InvalidNumber_AddsError()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.Count);
            var errors = new List<ErrorMessage>();

            prop.ValueValidity("not_a_number", errors, 3);

            Assert.AreEqual(1, errors.Count);
            Assert.AreEqual(3, errors[0].Index, "Error should reference the correct row index");
        }

        [TestMethod]
        public void ValueValidity_FloatInNumberField_AddsError()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.Count);
            var errors = new List<ErrorMessage>();

            prop.ValueValidity("3.14", errors, 2);

            Assert.AreEqual(1, errors.Count, "Decimal value in integer field should fail");
        }

        [TestMethod]
        public void ValueValidity_EmptyNumber_AddsError()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.Count);
            var errors = new List<ErrorMessage>();

            prop.ValueValidity("", errors, 1);

            Assert.AreEqual(1, errors.Count, "Empty string in non-nullable int field should fail");
        }

        // ─── Float/Decimal Validation ─────────────────────────────────

        [TestMethod]
        public void ValueValidity_ValidDecimal_NoError()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.Price);
            var errors = new List<ErrorMessage>();

            prop.ValueValidity("19.99", errors, 1);

            Assert.AreEqual(0, errors.Count);
            Assert.AreEqual(19.99m, prop.Value);
        }

        [TestMethod]
        public void ValueValidity_InvalidDecimal_AddsError()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.Price);
            var errors = new List<ErrorMessage>();

            prop.ValueValidity("abc", errors, 5);

            Assert.AreEqual(1, errors.Count);
            Assert.AreEqual(5, errors[0].Index);
        }

        // ─── Date Validation ──────────────────────────────────────────

        [TestMethod]
        public void ValueValidity_ValidDate_NoError()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.CreatedDate);
            var errors = new List<ErrorMessage>();

            prop.ValueValidity("2026-03-12", errors, 1);

            Assert.AreEqual(0, errors.Count);
            Assert.IsInstanceOfType(prop.Value, typeof(DateTime));
        }

        [TestMethod]
        public void ValueValidity_InvalidDate_AddsError()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.CreatedDate);
            var errors = new List<ErrorMessage>();

            prop.ValueValidity("not-a-date", errors, 4);

            Assert.AreEqual(1, errors.Count);
            Assert.AreEqual(4, errors[0].Index);
        }

        [TestMethod]
        public void ValueValidity_InvalidDateFormat_AddsError()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.CreatedDate);
            var errors = new List<ErrorMessage>();

            prop.ValueValidity("32/13/2026", errors, 1);

            Assert.AreEqual(1, errors.Count, "Invalid month/day should fail");
        }

        // ─── Bool Validation ──────────────────────────────────────────

        [TestMethod]
        public void ValueValidity_InvalidBool_AddsError()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.IsActive);
            var errors = new List<ErrorMessage>();

            prop.ValueValidity("maybe", errors, 2);

            Assert.AreEqual(1, errors.Count, "Non-Yes/No value should fail");
        }

        // ─── Nullable Handling ────────────────────────────────────────

        [TestMethod]
        public void ValueValidity_NullableField_EmptyString_NoError()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.OptionalCount);
            var errors = new List<ErrorMessage>();

            prop.ValueValidity("", errors, 1);

            Assert.AreEqual(0, errors.Count, "Empty value in nullable field should not error");
        }

        [TestMethod]
        public void ValueValidity_NullableField_Null_NoError()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.OptionalCount);
            var errors = new List<ErrorMessage>();

            prop.ValueValidity(null, errors, 1);

            Assert.AreEqual(0, errors.Count, "Null value in nullable field should not error");
        }

        [TestMethod]
        public void ValueValidity_NullableField_ValidValue_NoError()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.OptionalCount);
            var errors = new List<ErrorMessage>();

            prop.ValueValidity("7", errors, 1);

            Assert.AreEqual(0, errors.Count);
            Assert.AreEqual(7, prop.Value);
        }

        [TestMethod]
        public void ValueValidity_NullableField_InvalidValue_AddsError()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.OptionalCount);
            var errors = new List<ErrorMessage>();

            prop.ValueValidity("xyz", errors, 6);

            Assert.AreEqual(1, errors.Count, "Invalid value in nullable int field should still fail");
        }

        // ─── Text Validation ──────────────────────────────────────────

        [TestMethod]
        public void ValueValidity_Text_AlwaysAccepted()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.Name);
            var errors = new List<ErrorMessage>();

            prop.ValueValidity("any text value", errors, 1);

            Assert.AreEqual(0, errors.Count);
            Assert.AreEqual("any text value", prop.Value);
        }

        // ─── Multiple Errors Accumulation ─────────────────────────────

        [TestMethod]
        public void ValueValidity_MultipleInvalidRows_AccumulatesErrors()
        {
            var prop = ExcelPropety.CreateProperty<ExcelTestEntity>(x => x.Count);
            var errors = new List<ErrorMessage>();

            prop.ValueValidity("bad1", errors, 1);
            prop.ValueValidity("bad2", errors, 2);
            prop.ValueValidity("bad3", errors, 3);

            Assert.AreEqual(3, errors.Count);
            Assert.AreEqual(1, errors[0].Index);
            Assert.AreEqual(2, errors[1].Index);
            Assert.AreEqual(3, errors[2].Index);
        }
    }
}
