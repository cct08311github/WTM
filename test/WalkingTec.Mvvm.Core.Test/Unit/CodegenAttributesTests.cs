using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Unit
{
    /// <summary>
    /// Tests for the four codegen feature-attributes and their supporting enums.
    /// Verifies property defaults, AttributeUsage constraints, new enum member values,
    /// and reflectability on a property.
    /// </summary>
    [TestClass]
    public class CodegenAttributesTests
    {
        // -----------------------------------------------------------------
        // Helpers: a sample model used for reflection tests
        // -----------------------------------------------------------------
        private class SampleModel
        {
            [ListColumn(Width = 120, Align = GridColumnAlignEnum.Left, Sort = false,
                        Hide = true, ShowTotal = true, Fixed = GridColumnFixedEnum.Left)]
            public string? Name { get; set; }

            [SearchField(Operator = SearchOperator.Contains, ShowInPanel = false,
                         DateRange = false, Order = 5)]
            public string? Code { get; set; }

            [FormField(ControlType = FormControlType.TextArea, Colspan = 2,
                       Group = "Basic", Order = 3, Placeholder = "Enter...",
                       ReadonlyOnEdit = true)]
            public string? Description { get; set; }

            [ImportConfig(DataType = ColumnDataType.Text, RequiredOnImport = true,
                          ColumnHeader = "MyHeader", DateFormat = "yyyy/MM/dd")]
            public string? ImportedField { get; set; }

            // Plain property — no attributes applied
            public string? Plain { get; set; }
        }

        // =================================================================
        // 1. ListColumnAttribute — defaults
        // =================================================================

        [TestMethod]
        public void ListColumn_defaults_match_spec()
        {
            var attr = new ListColumnAttribute();
            Assert.AreEqual(0, attr.Width);
            Assert.AreEqual(GridColumnAlignEnum.Auto, attr.Align);
            Assert.IsTrue(attr.Sort);
            Assert.IsFalse(attr.Hide);
            Assert.IsFalse(attr.ShowTotal);
            Assert.AreEqual(GridColumnFixedEnum.None, attr.Fixed);
        }

        [TestMethod]
        public void ListColumn_AttributeUsage_is_Property_AllowMultipleFalse()
        {
            var usage = typeof(ListColumnAttribute)
                .GetCustomAttribute<AttributeUsageAttribute>()!;
            Assert.IsNotNull(usage);
            Assert.AreEqual(AttributeTargets.Property, usage.ValidOn);
            Assert.IsFalse(usage.AllowMultiple);
        }

        [TestMethod]
        public void ListColumn_is_reflectable_on_property()
        {
            var prop = typeof(SampleModel).GetProperty(nameof(SampleModel.Name))!;
            var attr = prop.GetCustomAttribute<ListColumnAttribute>();
            Assert.IsNotNull(attr);
            Assert.AreEqual(120, attr.Width);
            Assert.AreEqual(GridColumnAlignEnum.Left, attr.Align);
            Assert.IsFalse(attr.Sort);
            Assert.IsTrue(attr.Hide);
            Assert.IsTrue(attr.ShowTotal);
            Assert.AreEqual(GridColumnFixedEnum.Left, attr.Fixed);
        }

        [TestMethod]
        public void ListColumn_absent_on_plain_property()
        {
            var prop = typeof(SampleModel).GetProperty(nameof(SampleModel.Plain))!;
            Assert.IsNull(prop.GetCustomAttribute<ListColumnAttribute>());
        }

        // =================================================================
        // 2. SearchFieldAttribute — defaults
        // =================================================================

        [TestMethod]
        public void SearchField_defaults_match_spec()
        {
            var attr = new SearchFieldAttribute();
            Assert.AreEqual(SearchOperator.Auto, attr.Operator);
            Assert.IsTrue(attr.ShowInPanel);
            Assert.IsTrue(attr.DateRange);
            Assert.AreEqual(int.MaxValue, attr.Order);
        }

        [TestMethod]
        public void SearchField_AttributeUsage_is_Property_AllowMultipleFalse()
        {
            var usage = typeof(SearchFieldAttribute)
                .GetCustomAttribute<AttributeUsageAttribute>()!;
            Assert.IsNotNull(usage);
            Assert.AreEqual(AttributeTargets.Property, usage.ValidOn);
            Assert.IsFalse(usage.AllowMultiple);
        }

        [TestMethod]
        public void SearchField_is_reflectable_on_property()
        {
            var prop = typeof(SampleModel).GetProperty(nameof(SampleModel.Code))!;
            var attr = prop.GetCustomAttribute<SearchFieldAttribute>();
            Assert.IsNotNull(attr);
            Assert.AreEqual(SearchOperator.Contains, attr.Operator);
            Assert.IsFalse(attr.ShowInPanel);
            Assert.IsFalse(attr.DateRange);
            Assert.AreEqual(5, attr.Order);
        }

        // =================================================================
        // 3. FormFieldAttribute — defaults
        // =================================================================

        [TestMethod]
        public void FormField_defaults_match_spec()
        {
            var attr = new FormFieldAttribute();
            Assert.AreEqual(FormControlType.Auto, attr.ControlType);
            Assert.AreEqual(1, attr.Colspan);
            Assert.IsNull(attr.Group);
            Assert.AreEqual(int.MaxValue, attr.Order);
            Assert.IsNull(attr.Placeholder);
            Assert.IsFalse(attr.ReadonlyOnEdit);
        }

        [TestMethod]
        public void FormField_AttributeUsage_is_Property_AllowMultipleFalse()
        {
            var usage = typeof(FormFieldAttribute)
                .GetCustomAttribute<AttributeUsageAttribute>()!;
            Assert.IsNotNull(usage);
            Assert.AreEqual(AttributeTargets.Property, usage.ValidOn);
            Assert.IsFalse(usage.AllowMultiple);
        }

        [TestMethod]
        public void FormField_is_reflectable_on_property()
        {
            var prop = typeof(SampleModel).GetProperty(nameof(SampleModel.Description))!;
            var attr = prop.GetCustomAttribute<FormFieldAttribute>();
            Assert.IsNotNull(attr);
            Assert.AreEqual(FormControlType.TextArea, attr.ControlType);
            Assert.AreEqual(2, attr.Colspan);
            Assert.AreEqual("Basic", attr.Group);
            Assert.AreEqual(3, attr.Order);
            Assert.AreEqual("Enter...", attr.Placeholder);
            Assert.IsTrue(attr.ReadonlyOnEdit);
        }

        // =================================================================
        // 4. ImportConfigAttribute — defaults
        // =================================================================

        [TestMethod]
        public void ImportConfig_defaults_match_spec()
        {
            var attr = new ImportConfigAttribute();
            Assert.AreEqual(ColumnDataType.Dynamic, attr.DataType);
            Assert.IsFalse(attr.RequiredOnImport);
            Assert.IsNull(attr.ColumnHeader);
            Assert.IsNull(attr.DateFormat);
        }

        [TestMethod]
        public void ImportConfig_AttributeUsage_is_Property_AllowMultipleFalse()
        {
            var usage = typeof(ImportConfigAttribute)
                .GetCustomAttribute<AttributeUsageAttribute>()!;
            Assert.IsNotNull(usage);
            Assert.AreEqual(AttributeTargets.Property, usage.ValidOn);
            Assert.IsFalse(usage.AllowMultiple);
        }

        [TestMethod]
        public void ImportConfig_is_reflectable_on_property()
        {
            var prop = typeof(SampleModel).GetProperty(nameof(SampleModel.ImportedField))!;
            var attr = prop.GetCustomAttribute<ImportConfigAttribute>();
            Assert.IsNotNull(attr);
            Assert.AreEqual(ColumnDataType.Text, attr.DataType);
            Assert.IsTrue(attr.RequiredOnImport);
            Assert.AreEqual("MyHeader", attr.ColumnHeader);
            Assert.AreEqual("yyyy/MM/dd", attr.DateFormat);
        }

        // =================================================================
        // 5. Enum members — GridColumnFixedEnum
        // =================================================================

        [TestMethod]
        public void GridColumnFixedEnum_Left_is_0()
        {
            Assert.AreEqual(0, (int)GridColumnFixedEnum.Left);
        }

        [TestMethod]
        public void GridColumnFixedEnum_Right_is_1()
        {
            Assert.AreEqual(1, (int)GridColumnFixedEnum.Right);
        }

        [TestMethod]
        public void GridColumnFixedEnum_None_is_2()
        {
            Assert.AreEqual(2, (int)GridColumnFixedEnum.None);
        }

        // =================================================================
        // 6. Enum members — GridColumnAlignEnum
        // =================================================================

        [TestMethod]
        public void GridColumnAlignEnum_Center_is_0()
        {
            Assert.AreEqual(0, (int)GridColumnAlignEnum.Center);
        }

        [TestMethod]
        public void GridColumnAlignEnum_Left_is_1()
        {
            Assert.AreEqual(1, (int)GridColumnAlignEnum.Left);
        }

        [TestMethod]
        public void GridColumnAlignEnum_Right_is_2()
        {
            Assert.AreEqual(2, (int)GridColumnAlignEnum.Right);
        }

        [TestMethod]
        public void GridColumnAlignEnum_Auto_is_3()
        {
            Assert.AreEqual(3, (int)GridColumnAlignEnum.Auto);
        }

        // =================================================================
        // 7. Enum members — SearchOperator
        // =================================================================

        [TestMethod]
        public void SearchOperator_Auto_is_0()
        {
            Assert.AreEqual(0, (int)SearchOperator.Auto);
        }

        [TestMethod]
        public void SearchOperator_Contains_is_1()
        {
            Assert.AreEqual(1, (int)SearchOperator.Contains);
        }

        [TestMethod]
        public void SearchOperator_Equal_is_2()
        {
            Assert.AreEqual(2, (int)SearchOperator.Equal);
        }

        [TestMethod]
        public void SearchOperator_Between_is_3()
        {
            Assert.AreEqual(3, (int)SearchOperator.Between);
        }

        [TestMethod]
        public void SearchOperator_does_not_have_StartsWith()
        {
            // StartsWith intentionally omitted (no runtime support)
            Assert.IsFalse(Enum.IsDefined(typeof(SearchOperator), "StartsWith"));
        }

        // =================================================================
        // 8. Enum members — FormControlType
        // =================================================================

        [TestMethod]
        public void FormControlType_Auto_is_0()
        {
            Assert.AreEqual(0, (int)FormControlType.Auto);
        }

        [TestMethod]
        public void FormControlType_Text_is_1()
        {
            Assert.AreEqual(1, (int)FormControlType.Text);
        }

        [TestMethod]
        public void FormControlType_TextArea_is_2()
        {
            Assert.AreEqual(2, (int)FormControlType.TextArea);
        }

        [TestMethod]
        public void FormControlType_Number_is_3()
        {
            Assert.AreEqual(3, (int)FormControlType.Number);
        }

        [TestMethod]
        public void FormControlType_Date_is_4()
        {
            Assert.AreEqual(4, (int)FormControlType.Date);
        }

        [TestMethod]
        public void FormControlType_DateTime_is_5()
        {
            Assert.AreEqual(5, (int)FormControlType.DateTime);
        }

        [TestMethod]
        public void FormControlType_Switch_is_6()
        {
            Assert.AreEqual(6, (int)FormControlType.Switch);
        }

        [TestMethod]
        public void FormControlType_ComboBox_is_7()
        {
            Assert.AreEqual(7, (int)FormControlType.ComboBox);
        }

        [TestMethod]
        public void FormControlType_Radio_is_8()
        {
            Assert.AreEqual(8, (int)FormControlType.Radio);
        }

        [TestMethod]
        public void FormControlType_CheckBox_is_9()
        {
            Assert.AreEqual(9, (int)FormControlType.CheckBox);
        }

        [TestMethod]
        public void FormControlType_Upload_is_10()
        {
            Assert.AreEqual(10, (int)FormControlType.Upload);
        }

        // =================================================================
        // 9. ColumnDataType.Dynamic already exists
        // =================================================================

        [TestMethod]
        public void ColumnDataType_Dynamic_exists()
        {
            Assert.IsTrue(Enum.IsDefined(typeof(ColumnDataType), "Dynamic"));
        }
    }
}
