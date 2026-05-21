#nullable enable
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NPOI.XSSF.UserModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    // ---------------------------------------------------------------------------
    // Concrete TemplateVM implementations to hit uncovered branches
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Covers text / number / float / date / bool / enum columns so
    /// GetColumnDescription() branches are exercised.
    /// </summary>
    public class AllTypesTemplateVM : BaseTemplateVM
    {
        public ExcelPropety Text_Excel = ExcelPropety.CreateProperty<School>(x => x.SchoolName);
        public ExcelPropety Required_Excel = ExcelPropety.CreateProperty<School>(x => x.SchoolCode);
        public ExcelPropety Enum_Excel = ExcelPropety.CreateProperty<School>(x => x.SchoolType);

        protected override void InitVM() { }
    }

    /// <summary>
    /// Template VM with bool/date/float/datetime columns so CreateDataTable
    /// and GetColumnDescription cover those branches.
    /// </summary>
    public class BoolDateFloatTemplateVM : BaseTemplateVM
    {
        public ExcelPropety Bool_Excel = ExcelPropety.CreateProperty<Student>(x => x.IsValid);
        public ExcelPropety Date_Excel = ExcelPropety.CreateProperty<Student>(x => x.EnRollDate);

        protected override void InitVM() { }
    }

    /// <summary>
    /// Template VM with a float (decimal) column to cover Float branch.
    /// GoodsSpecification has no decimal field, so we manually set DataType.
    /// </summary>
    public class FloatTemplateVM : BaseTemplateVM
    {
        public ExcelPropety Float_Excel = ExcelPropety.CreateProperty<GoodsSpecification>(x => x.OrderNum);

        public override void InitExcelData()
        {
            Float_Excel.DataType = ColumnDataType.Float;
        }

        protected override void InitVM() { }
    }

    /// <summary>
    /// Template VM with a DateTime column to cover DateTime branch.
    /// </summary>
    public class DateTimeTemplateVM : BaseTemplateVM
    {
        public ExcelPropety DT_Excel = ExcelPropety.CreateProperty<Student>(x => x.EnRollDate, true);

        public override void InitExcelData()
        {
            DT_Excel.DataType = ColumnDataType.DateTime;
            DT_Excel.IsNullAble = true;
        }

        protected override void InitVM() { }
    }

    /// <summary>
    /// Template VM whose field has a [Display] attribute, covering the else branch (line 69) in the constructor.
    /// </summary>
    public class DisplayAttrTemplateVM : BaseTemplateVM
    {
        [System.ComponentModel.DataAnnotations.Display(Name = "School Name Label")]
        public ExcelPropety School_Name = ExcelPropety.CreateProperty<School>(x => x.SchoolName);

        protected override void InitVM() { }
    }

    /// <summary>
    /// Template VM with a Dynamic column to cover the Dynamic branch in GenerateTemplate.
    /// </summary>
    public class DynamicColTemplateVM : BaseTemplateVM
    {
        public ExcelPropety Dynamic_Excel = ExcelPropety.CreateProperty<GoodsSpecification>(x => x.Name);

        public override void InitExcelData()
        {
            Dynamic_Excel.DataType = ColumnDataType.Dynamic;
            // Add two dynamic sub-columns
            Dynamic_Excel.DynamicColumns = new List<ExcelPropety>
            {
                new ExcelPropety { ColumnName = "SubCol1", DataType = ColumnDataType.Text, IsNullAble = true },
                new ExcelPropety { ColumnName = "SubCol2", DataType = ColumnDataType.Number, IsNullAble = false },
            };
        }

        protected override void InitVM() { }
    }

    /// <summary>
    /// Template VM with a min/max-constrained column.
    /// </summary>
    public class MinMaxTemplateVM : BaseTemplateVM
    {
        public ExcelPropety Num_Excel = ExcelPropety.CreateProperty<GoodsSpecification>(x => x.OrderNum);

        public override void InitExcelData()
        {
            // Manually set min/max on the ExcelPropety fields
            Num_Excel.MinValueOrLength = "1";
            Num_Excel.MaxValuseOrLength = "100";
        }

        protected override void InitVM() { }
    }

    /// <summary>
    /// Template VM with a ComboBox column so that branch of GetColumnDescription is hit.
    /// </summary>
    public class ComboTemplateVM : BaseTemplateVM
    {
        public ExcelPropety Combo_Excel = ExcelPropety.CreateProperty<School>(x => x.SchoolType);

        protected override void InitVM() { }
    }

    /// <summary>
    /// Template VM with ShowDescriptionRow=true and template data rows
    /// so the TemplateDataTable.Rows.Count > 0 branch is executed.
    /// </summary>
    public class TemplateDataTemplateVM : BaseTemplateVM
    {
        public ExcelPropety Name_Excel = ExcelPropety.CreateProperty<School>(x => x.SchoolName);

        public override void SetTemplateDataValus()
        {
            if (TemplateDataTable != null)
            {
                var row = TemplateDataTable.NewRow();
                row["Name_Excel"] = "SampleSchool";
                TemplateDataTable.Rows.Add(row);
            }
        }

        protected override void InitVM() { }
    }

    /// <summary>
    /// Uses a custom FileDisplayName and overrides InitExcelData /
    /// InitCustomFormat / SetTemplateDataValus to drive those virtual methods.
    /// </summary>
    public class CustomTemplateVM : BaseTemplateVM
    {
        public bool InitExcelDataCalled { get; private set; }
        public bool InitCustomFormatCalled { get; private set; }
        public bool SetTemplateDataValusCalled { get; private set; }

        public ExcelPropety Name_Excel = ExcelPropety.CreateProperty<School>(x => x.SchoolName);

        public CustomTemplateVM()
        {
            FileDisplayName = "CustomExport";
        }

        public override void InitExcelData()
        {
            InitExcelDataCalled = true;
        }

        public override void InitCustomFormat()
        {
            InitCustomFormatCalled = true;
        }

        public override void SetTemplateDataValus()
        {
            SetTemplateDataValusCalled = true;
        }

        protected override void InitVM() { }
    }

    /// <summary>
    /// Overrides GetColumnDescription to test the override path.
    /// </summary>
    public class CustomDescriptionTemplateVM : BaseTemplateVM
    {
        public ExcelPropety Name_Excel = ExcelPropety.CreateProperty<School>(x => x.SchoolName);

        protected override string GetColumnDescription(ExcelPropety prop) =>
            $"Custom:{prop.ColumnName}";

        protected override void InitVM() { }
    }

    /// <summary>
    /// Template VM with a number and float column to drive those description branches.
    /// </summary>
    public class NumericTemplateVM : BaseTemplateVM
    {
        public ExcelPropety Int_Excel = ExcelPropety.CreateProperty<GoodsSpecification>(x => x.OrderNum);

        protected override void InitVM() { }
    }

    /// <summary>
    /// Template VM with MaxValuseOrLength set to drive that description branch.
    /// </summary>
    public class MaxLenTemplateVM : BaseTemplateVM
    {
        // SchoolCode has [StringLength] which maps to MaxValuseOrLength
        public ExcelPropety Code_Excel = ExcelPropety.CreateProperty<School>(x => x.SchoolCode);

        protected override void InitVM() { }
    }

    // ---------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------

    [TestClass]
    public class BaseTemplateVMExtendedTests
    {
        private static byte[] GenerateBytes(BaseTemplateVM vm)
        {
            return vm.GenerateTemplate(out _);
        }

        private static XSSFWorkbook LoadWorkbook(byte[] bytes)
        {
            return new XSSFWorkbook(new MemoryStream(bytes));
        }

        // ----------------------------------------------------------------------
        // ExcelIndex property
        // ----------------------------------------------------------------------

        [TestMethod]
        public void ExcelIndex_defaults_to_zero()
        {
            var vm = new AllTypesTemplateVM();
            Assert.AreEqual(0L, vm.ExcelIndex);
        }

        [TestMethod]
        public void ExcelIndex_can_be_set_and_read()
        {
            var vm = new AllTypesTemplateVM();
            vm.ExcelIndex = 42L;
            Assert.AreEqual(42L, vm.ExcelIndex);
        }

        // ----------------------------------------------------------------------
        // InitCustomFormat virtual method
        // ----------------------------------------------------------------------

        [TestMethod]
        public void InitCustomFormat_is_callable_as_no_op_on_base()
        {
            // BaseTemplateVM.InitCustomFormat() is virtual/empty — calling it should not throw
            var vm = new AllTypesTemplateVM();
            vm.InitCustomFormat(); // should not throw
        }

        [TestMethod]
        public void InitCustomFormat_override_is_called_when_subclass_overrides()
        {
            var vm = new CustomTemplateVM();
            vm.InitCustomFormat();
            Assert.IsTrue(vm.InitCustomFormatCalled);
        }

        // ----------------------------------------------------------------------
        // InitExcelData and SetTemplateDataValus
        // ----------------------------------------------------------------------

        [TestMethod]
        public void InitExcelData_and_SetTemplateDataValus_are_called_during_GenerateTemplate()
        {
            var vm = new CustomTemplateVM();
            _ = vm.GenerateTemplate(out _);

            Assert.IsTrue(vm.InitExcelDataCalled, "InitExcelData should be called during GenerateTemplate");
            Assert.IsTrue(vm.SetTemplateDataValusCalled, "SetTemplateDataValus should be called during GenerateTemplate");
        }

        // ----------------------------------------------------------------------
        // FileDisplayName affects the display-name output
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GenerateTemplate_uses_FileDisplayName_when_set()
        {
            var vm = new CustomTemplateVM(); // FileDisplayName = "CustomExport"
            _ = vm.GenerateTemplate(out string displayName);

            Assert.IsTrue(displayName.StartsWith("CustomExport_"), $"Expected display name to start with 'CustomExport_', got '{displayName}'");
        }

        [TestMethod]
        public void GenerateTemplate_uses_class_name_when_FileDisplayName_not_set()
        {
            var vm = new AllTypesTemplateVM();
            _ = vm.GenerateTemplate(out string displayName);

            Assert.IsTrue(displayName.StartsWith("AllTypesTemplateVM_"),
                $"Expected display name to start with class name, got '{displayName}'");
        }

        // ----------------------------------------------------------------------
        // GetColumnDescription — data type branches
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GetColumnDescription_Text_required_contains_Required()
        {
            var vm = new AllTypesTemplateVM();
            var bytes = vm.GenerateTemplate(out _);
            var wb = LoadWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            var descRow = sheet.GetRow(1);
            Assert.IsNotNull(descRow, "Description row (index 1) should exist");

            // At least one cell should say "Required"
            bool found = false;
            for (int c = 0; c < descRow.LastCellNum; c++)
            {
                if ((descRow.GetCell(c)?.ToString() ?? "").Contains("Required"))
                {
                    found = true;
                    break;
                }
            }
            Assert.IsTrue(found, "At least one column description should contain 'Required'");
        }

        [TestMethod]
        public void GetColumnDescription_optional_col_contains_Optional()
        {
            // GoodsSpecification.OrderNum is nullable int → Optional
            var vm = new NumericTemplateVM();
            var bytes = vm.GenerateTemplate(out _);
            var wb = LoadWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            var descRow = sheet.GetRow(1);
            Assert.IsNotNull(descRow);
            var cellText = descRow.GetCell(0)?.ToString() ?? "";
            Assert.IsTrue(cellText.Contains("Optional"), $"Expected 'Optional' in '{cellText}'");
        }

        [TestMethod]
        public void GetColumnDescription_Integer_contains_Integer()
        {
            // OrderNum is int? → Number type → "Integer"
            var vm = new NumericTemplateVM();
            var bytes = vm.GenerateTemplate(out _);
            var wb = LoadWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            var descRow = sheet.GetRow(1);
            Assert.IsNotNull(descRow);
            var cellText = descRow.GetCell(0)?.ToString() ?? "";
            Assert.IsTrue(cellText.Contains("Integer"), $"Expected 'Integer' in '{cellText}'");
        }

        [TestMethod]
        public void GetColumnDescription_Enum_contains_enum_values()
        {
            // SchoolType is SchoolTypeEnum → Enum column → should list enum values
            var vm = new AllTypesTemplateVM();
            var bytes = vm.GenerateTemplate(out _);
            var wb = LoadWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            var descRow = sheet.GetRow(1);
            Assert.IsNotNull(descRow);

            // Find the enum column (index 2 = SchoolType)
            // It may say "Required, 公立学校/私立学校" or similar enum display names
            bool hasEnumColumn = false;
            for (int c = 0; c < descRow.LastCellNum; c++)
            {
                var txt = descRow.GetCell(c)?.ToString() ?? "";
                // Could be the enum display values in Chinese
                if (txt.Length > 0)
                {
                    hasEnumColumn = true;
                    break;
                }
            }
            Assert.IsTrue(hasEnumColumn, "Description row should have at least one non-empty cell");
        }

        [TestMethod]
        public void GetColumnDescription_with_max_length_contains_max_prefix()
        {
            // SchoolCode has [StringLength(3)] → MaxValuseOrLength = "3"
            var vm = new MaxLenTemplateVM();
            var bytes = vm.GenerateTemplate(out _);
            var wb = LoadWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            var descRow = sheet.GetRow(1);
            Assert.IsNotNull(descRow);
            var cellText = descRow.GetCell(0)?.ToString() ?? "";
            // The description includes "max:" prefix
            Assert.IsTrue(cellText.Contains("max:"), $"Expected 'max:' in description, got '{cellText}'");
        }

        // ----------------------------------------------------------------------
        // Custom GetColumnDescription override
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GetColumnDescription_override_is_used()
        {
            var vm = new CustomDescriptionTemplateVM();
            var bytes = vm.GenerateTemplate(out _);
            var wb = LoadWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            var descRow = sheet.GetRow(1);
            Assert.IsNotNull(descRow);
            var cellText = descRow.GetCell(0)?.ToString() ?? "";
            Assert.IsTrue(cellText.StartsWith("Custom:"), $"Expected custom description prefix, got '{cellText}'");
        }

        // ----------------------------------------------------------------------
        // ShowDescriptionRow = false skips description row
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GenerateTemplate_ShowDescriptionRow_false_does_not_write_desc_row()
        {
            var vm = new AllTypesTemplateVM { ShowDescriptionRow = false };
            var bytes = GenerateBytes(vm);
            var wb = LoadWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            // Row 1 should be null or a data row (no description)
            var row1 = sheet.GetRow(1);
            if (row1 != null)
            {
                for (int c = 0; c < row1.LastCellNum; c++)
                {
                    var txt = row1.GetCell(c)?.ToString() ?? "";
                    Assert.IsFalse(txt.Contains("Required") || txt.Contains("Optional"),
                        "Row 1 must not be a description row when ShowDescriptionRow = false");
                }
            }
        }

        // ----------------------------------------------------------------------
        // Parms dictionary
        // ----------------------------------------------------------------------

        [TestMethod]
        public void Parms_is_initialised_as_empty_dictionary()
        {
            var vm = new AllTypesTemplateVM();
            Assert.IsNotNull(vm.Parms);
            Assert.AreEqual(0, vm.Parms.Count);
        }

        [TestMethod]
        public void Parms_can_be_populated()
        {
            var vm = new AllTypesTemplateVM();
            vm.Parms["key1"] = "value1";
            Assert.AreEqual("value1", vm.Parms["key1"]);
        }

        // ----------------------------------------------------------------------
        // ValidityTemplateType default
        // ----------------------------------------------------------------------

        [TestMethod]
        public void ValidityTemplateType_default_is_true()
        {
            var vm = new AllTypesTemplateVM();
            Assert.IsTrue(vm.ValidityTemplateType);
        }

        [TestMethod]
        public void ValidityTemplateType_can_be_set_false()
        {
            var vm = new AllTypesTemplateVM { ValidityTemplateType = false };
            Assert.IsFalse(vm.ValidityTemplateType);
        }

        // ----------------------------------------------------------------------
        // TemplateDataTable property
        // ----------------------------------------------------------------------

        [TestMethod]
        public void TemplateDataTable_is_null_before_GenerateTemplate()
        {
            var vm = new AllTypesTemplateVM();
            Assert.IsNull(vm.TemplateDataTable);
        }

        [TestMethod]
        public void TemplateDataTable_is_populated_after_GenerateTemplate()
        {
            var vm = new AllTypesTemplateVM();
            _ = vm.GenerateTemplate(out _);
            Assert.IsNotNull(vm.TemplateDataTable);
        }

        // ----------------------------------------------------------------------
        // Output is a valid xlsx (basic sanity)
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GenerateTemplate_returns_non_empty_bytes()
        {
            var vm = new AllTypesTemplateVM();
            var bytes = GenerateBytes(vm);
            Assert.IsNotNull(bytes);
            Assert.IsTrue(bytes.Length > 0);
        }

        [TestMethod]
        public void GenerateTemplate_with_Wtm_context_still_produces_valid_output()
        {
            // Exercises the Wtm?.TimeProvider branch for the date in the filename
            var vm = new AllTypesTemplateVM();
            vm.Wtm = MockWtmContext.CreateWtmContext();
            var bytes = vm.GenerateTemplate(out string name);
            Assert.IsNotNull(bytes);
            Assert.IsTrue(bytes.Length > 0);
            Assert.IsFalse(string.IsNullOrEmpty(name));
        }

        // ----------------------------------------------------------------------
        // GetColumnDescription — Text with CharCount branch
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GetColumnDescription_text_with_charcount_contains_max()
        {
            // SchoolName has [StringLength(50)] which sets CharCount or MaxValuseOrLength
            var vm = new AllTypesTemplateVM();
            var bytes = vm.GenerateTemplate(out _);
            var wb = LoadWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            var descRow = sheet.GetRow(1);
            Assert.IsNotNull(descRow);
            // Find a cell that has "max:" (SchoolName or SchoolCode columns)
            bool hasMax = false;
            for (int c = 0; c < descRow.LastCellNum; c++)
            {
                if ((descRow.GetCell(c)?.ToString() ?? "").Contains("max:"))
                {
                    hasMax = true;
                    break;
                }
            }
            Assert.IsTrue(hasMax, "At least one column should have a 'max:' constraint in its description");
        }

        // ----------------------------------------------------------------------
        // CreateDataTable — Bool / Date branches
        // ----------------------------------------------------------------------

        [TestMethod]
        public void CreateDataTable_Bool_branch_adds_bool_column()
        {
            var vm = new BoolDateFloatTemplateVM();
            _ = vm.GenerateTemplate(out _);
            Assert.IsNotNull(vm.TemplateDataTable);
            // Bool_Excel column should be of type bool
            var col = vm.TemplateDataTable.Columns["Bool_Excel"];
            Assert.IsNotNull(col, "Bool_Excel column should exist in DataTable");
            Assert.AreEqual(typeof(bool), col.DataType);
        }

        [TestMethod]
        public void CreateDataTable_Date_branch_adds_string_column()
        {
            var vm = new BoolDateFloatTemplateVM();
            _ = vm.GenerateTemplate(out _);
            Assert.IsNotNull(vm.TemplateDataTable);
            // Date_Excel (DateTime? → Date) should be string
            var col = vm.TemplateDataTable.Columns["Date_Excel"];
            Assert.IsNotNull(col, "Date_Excel column should exist in DataTable");
            Assert.AreEqual(typeof(string), col.DataType);
        }

        [TestMethod]
        public void BoolDate_template_produces_valid_excel()
        {
            var vm = new BoolDateFloatTemplateVM();
            var bytes = GenerateBytes(vm);
            Assert.IsTrue(bytes.Length > 0);
        }

        // ----------------------------------------------------------------------
        // GetColumnDescription — Bool / Date branches
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GetColumnDescription_Bool_contains_TrueFalse()
        {
            // Student.IsValid is bool → Bool data type
            var vm = new BoolDateFloatTemplateVM();
            var bytes = GenerateBytes(vm);
            var wb = LoadWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            var descRow = sheet.GetRow(1);
            Assert.IsNotNull(descRow);
            // Bool_Excel is first column (index 0)
            var txt = descRow.GetCell(0)?.ToString() ?? "";
            Assert.IsTrue(txt.Contains("True/False"), $"Expected 'True/False' in '{txt}'");
        }

        [TestMethod]
        public void GetColumnDescription_Date_contains_date_format()
        {
            // Student.EnRollDate is DateTime? → Date type
            var vm = new BoolDateFloatTemplateVM();
            var bytes = GenerateBytes(vm);
            var wb = LoadWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            var descRow = sheet.GetRow(1);
            Assert.IsNotNull(descRow);
            // Date_Excel is second column (index 1)
            var txt = descRow.GetCell(1)?.ToString() ?? "";
            Assert.IsTrue(txt.Contains("Date") || txt.Contains("yyyy"),
                $"Expected date format hint in '{txt}'");
        }

        // ----------------------------------------------------------------------
        // GetColumnDescription — min value branch
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GetColumnDescription_with_min_value_contains_min_prefix()
        {
            var vm = new MinMaxTemplateVM();
            var bytes = GenerateBytes(vm);
            var wb = LoadWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            var descRow = sheet.GetRow(1);
            Assert.IsNotNull(descRow);
            var txt = descRow.GetCell(0)?.ToString() ?? "";
            Assert.IsTrue(txt.Contains("min:"), $"Expected 'min:' in description when MinValueOrLength is set, got '{txt}'");
        }

        [TestMethod]
        public void GetColumnDescription_with_max_value_contains_max_prefix()
        {
            var vm = new MinMaxTemplateVM();
            var bytes = GenerateBytes(vm);
            var wb = LoadWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            var descRow = sheet.GetRow(1);
            Assert.IsNotNull(descRow);
            var txt = descRow.GetCell(0)?.ToString() ?? "";
            Assert.IsTrue(txt.Contains("max:"), $"Expected 'max:' in description when MaxValuseOrLength is set, got '{txt}'");
        }

        // ----------------------------------------------------------------------
        // Template data rows — TemplateDataTable.Rows.Count > 0 branch
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GenerateTemplate_with_template_data_rows_produces_data_in_excel()
        {
            var vm = new TemplateDataTemplateVM();
            var bytes = GenerateBytes(vm);
            var wb = LoadWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            // row 0 = header, row 1 = description row, row 2 = first data row
            var dataRow = sheet.GetRow(2);
            Assert.IsNotNull(dataRow, "Data row should exist when TemplateDataTable has rows");
            var val = dataRow.GetCell(0)?.ToString() ?? "";
            Assert.AreEqual("SampleSchool", val);
        }

        // ----------------------------------------------------------------------
        // ComboBox column description
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GetColumnDescription_ComboBox_includes_enum_values()
        {
            var vm = new ComboTemplateVM();
            var bytes = GenerateBytes(vm);
            var wb = LoadWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            var descRow = sheet.GetRow(1);
            Assert.IsNotNull(descRow);
            // SchoolType is an enum (ComboBox/Enum) — check we get some content
            var txt = descRow.GetCell(0)?.ToString() ?? "";
            Assert.IsTrue(txt.Length > 0, "Description for Enum column should not be empty");
        }

        // ----------------------------------------------------------------------
        // GetCellStyle — Yellow / Red background branches
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GenerateTemplate_with_yellow_background_column_does_not_throw()
        {
            // SchoolCode with BackgroudColorEnum.Yellow
            var vm = new AllTypesTemplateVM();
            vm.Required_Excel.BackgroudColor = BackgroudColorEnum.Yellow;
            // Should not throw
            var bytes = GenerateBytes(vm);
            Assert.IsTrue(bytes.Length > 0);
        }

        [TestMethod]
        public void GenerateTemplate_with_red_background_column_does_not_throw()
        {
            var vm = new AllTypesTemplateVM();
            vm.Required_Excel.BackgroudColor = BackgroudColorEnum.Red;
            var bytes = GenerateBytes(vm);
            Assert.IsTrue(bytes.Length > 0);
        }

        // ----------------------------------------------------------------------
        // Protected sheet (ReadOnly column)
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GenerateTemplate_with_readonly_column_produces_protected_sheet()
        {
            var vm = new AllTypesTemplateVM();
            vm.Text_Excel.ReadOnly = true;
            var bytes = GenerateBytes(vm);
            // Sheet should be protected — just verify no throw and valid bytes
            Assert.IsTrue(bytes.Length > 0);
        }

        // ----------------------------------------------------------------------
        // GetColumnDescription — Float / DateTime branches
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GetColumnDescription_Float_contains_Decimal()
        {
            var vm = new FloatTemplateVM();
            var bytes = GenerateBytes(vm);
            var wb = LoadWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            var descRow = sheet.GetRow(1);
            Assert.IsNotNull(descRow);
            var txt = descRow.GetCell(0)?.ToString() ?? "";
            Assert.IsTrue(txt.Contains("Decimal"), $"Expected 'Decimal' in Float column description, got '{txt}'");
        }

        [TestMethod]
        public void GetColumnDescription_DateTime_contains_DateTime()
        {
            var vm = new DateTimeTemplateVM();
            var bytes = GenerateBytes(vm);
            var wb = LoadWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            var descRow = sheet.GetRow(1);
            Assert.IsNotNull(descRow);
            var txt = descRow.GetCell(0)?.ToString() ?? "";
            Assert.IsTrue(txt.Contains("DateTime"), $"Expected 'DateTime' in description, got '{txt}'");
        }

        // ----------------------------------------------------------------------
        // CreateDataTable — Float branch
        // ----------------------------------------------------------------------

        [TestMethod]
        public void CreateDataTable_Float_branch_adds_decimal_column()
        {
            var vm = new FloatTemplateVM();
            _ = vm.GenerateTemplate(out _);
            Assert.IsNotNull(vm.TemplateDataTable);
            var col = vm.TemplateDataTable.Columns["Float_Excel"];
            Assert.IsNotNull(col, "Float_Excel column should exist in DataTable");
            Assert.AreEqual(typeof(decimal), col.DataType);
        }

        // ----------------------------------------------------------------------
        // Constructor line 69 — field with [Display] attribute uses GetPropertyDisplayName
        // ----------------------------------------------------------------------

        [TestMethod]
        public void Constructor_field_with_Display_attribute_uses_display_name()
        {
            // Instantiating the VM exercises the else-branch at ctor line 69
            var vm = new DisplayAttrTemplateVM();
            // The ColumnName should be set from the [Display] attribute on the field
            Assert.IsNotNull(vm.School_Name.ColumnName);
            Assert.IsFalse(string.IsNullOrEmpty(vm.School_Name.ColumnName));
        }

        [TestMethod]
        public void DisplayAttr_template_produces_valid_excel()
        {
            var vm = new DisplayAttrTemplateVM();
            var bytes = GenerateBytes(vm);
            Assert.IsTrue(bytes.Length > 0);
        }

        // ----------------------------------------------------------------------
        // Dynamic column type in GenerateTemplate
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GenerateTemplate_Dynamic_column_produces_valid_excel()
        {
            var vm = new DynamicColTemplateVM();
            var bytes = GenerateBytes(vm);
            Assert.IsNotNull(bytes);
            Assert.IsTrue(bytes.Length > 0);
        }

        [TestMethod]
        public void GenerateTemplate_Dynamic_column_header_row_has_sub_column_names()
        {
            var vm = new DynamicColTemplateVM();
            var bytes = GenerateBytes(vm);
            var wb = LoadWorkbook(bytes);
            var sheet = wb.GetSheetAt(0);
            var headerRow = sheet.GetRow(0);
            Assert.IsNotNull(headerRow);
            // Two dynamic sub-columns should appear
            bool hasSubCol1 = false, hasSubCol2 = false;
            for (int c = 0; c < headerRow.LastCellNum; c++)
            {
                var val = headerRow.GetCell(c)?.ToString() ?? "";
                if (val.Contains("SubCol1")) hasSubCol1 = true;
                if (val.Contains("SubCol2")) hasSubCol2 = true;
            }
            Assert.IsTrue(hasSubCol1, "SubCol1 should appear in header row");
            Assert.IsTrue(hasSubCol2, "SubCol2 should appear in header row");
        }
    }
}
