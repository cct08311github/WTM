#nullable enable
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System.IO;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.VM;

/// <summary>
/// Guards issue #615 — ShowDescriptionRow in BaseTemplateVM:
/// when enabled (default), the template must contain a description row below the header.
/// </summary>
[TestClass]
public class TemplateDescriptionRowTests
{
    private class SimpleTemplateVM : BaseTemplateVM
    {
        public ExcelPropety Name_Excel = ExcelPropety.CreateProperty<School>(x => x.SchoolName);
        public ExcelPropety Code_Excel = ExcelPropety.CreateProperty<School>(x => x.SchoolCode);
        protected override void InitVM() { }
    }

    private static IWorkbook GenerateWorkbook(bool showDescriptionRow)
    {
        var vm = new SimpleTemplateVM { ShowDescriptionRow = showDescriptionRow };
        var bytes = vm.GenerateTemplate(out _);
        return new XSSFWorkbook(new MemoryStream(bytes));
    }

    [TestMethod]
    public void ShowDescriptionRow_True_generates_v2_marker_in_hidden_sheet()
    {
        var wb = GenerateWorkbook(showDescriptionRow: true);
        var enumSheet = wb.GetSheetAt(1);
        var marker = enumSheet.GetRow(0)?.GetCell(3)?.ToString();
        Assert.AreEqual("v2", marker,
            "Hidden enum sheet cell[3] must contain 'v2' when ShowDescriptionRow = true");
    }

    [TestMethod]
    public void ShowDescriptionRow_False_generates_empty_marker_in_hidden_sheet()
    {
        var wb = GenerateWorkbook(showDescriptionRow: false);
        var enumSheet = wb.GetSheetAt(1);
        var marker = enumSheet.GetRow(0)?.GetCell(3)?.ToString() ?? string.Empty;
        Assert.AreEqual(string.Empty, marker,
            "Hidden enum sheet cell[3] must be empty when ShowDescriptionRow = false");
    }

    [TestMethod]
    public void ShowDescriptionRow_True_row2_is_not_null()
    {
        var wb = GenerateWorkbook(showDescriptionRow: true);
        var sheet = wb.GetSheetAt(0);
        var descRow = sheet.GetRow(1);
        Assert.IsNotNull(descRow, "Row 2 (index 1) must exist when ShowDescriptionRow = true");
    }

    [TestMethod]
    public void ShowDescriptionRow_True_row2_contains_RequiredOrOptional_text()
    {
        var wb = GenerateWorkbook(showDescriptionRow: true);
        var sheet = wb.GetSheetAt(0);
        var descRow = sheet.GetRow(1);
        Assert.IsNotNull(descRow);

        bool anyHintFound = false;
        for (int c = 0; c < descRow.LastCellNum; c++)
        {
            var text = descRow.GetCell(c)?.ToString() ?? "";
            if (text.Contains("Required") || text.Contains("Optional"))
            {
                anyHintFound = true;
                break;
            }
        }
        Assert.IsTrue(anyHintFound, "Description row must contain 'Required' or 'Optional'");
    }

    [TestMethod]
    public void ShowDescriptionRow_False_row2_is_data_or_null()
    {
        var wb = GenerateWorkbook(showDescriptionRow: false);
        var sheet = wb.GetSheetAt(0);
        // With ShowDescriptionRow = false, the first data row starts at index 1
        // (header is row 0, data is row 1). Row 1 should be null since no template data.
        var row1 = sheet.GetRow(1);
        // Either null (no data) or a data row — but NOT the green description row
        if (row1 != null)
        {
            // If row exists, it should not contain "Required" or "Optional" hint text
            for (int c = 0; c < row1.LastCellNum; c++)
            {
                var text = row1.GetCell(c)?.ToString() ?? "";
                Assert.IsFalse(text.Contains("Required") || text.Contains("Optional"),
                    "Row 2 must not be a description row when ShowDescriptionRow = false");
            }
        }
    }

    [TestMethod]
    public void ShowDescriptionRow_default_is_true()
    {
        var vm = new SimpleTemplateVM();
        Assert.IsTrue(vm.ShowDescriptionRow);
    }
}
