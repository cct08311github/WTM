using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Excel
{
    /// <summary>
    /// Excel offers to "repair" workbooks whose XML breaks the OOXML schema (colour format, child element order).
    /// These tests run the generated export/template through OpenXmlValidator and pin the styling that must survive the fix.
    /// </summary>
    [TestClass]
    public class ExcelSchemaValidityTests
    {
        #region export

        private class StudentExportListVM : BasePagedListVM<Student, BaseSearcher>
        {
            protected override IEnumerable<IGridColumn<Student>> InitGridHeader()
            {
                return new List<GridColumn<Student>>
                {
                    this.MakeGridHeader(x => x.LoginName),
                    this.MakeGridHeader(x => x.Name),
                    this.MakeGridHeader(x => x.Sex),
                    this.MakeGridHeader(x => x.EnRollDate),
                };
            }

            public override IOrderedQueryable<Student> GetSearchQuery()
            {
                return DC.Set<Student>().OrderBy(x => x.LoginName);
            }
        }

        private static StudentExportListVM NewExportVm()
        {
            var vm = new StudentExportListVM
            {
                Wtm = MockWtmContext.CreateWtmContext(new DataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory), "exportuser")
            };
            vm.DC.Set<Student>().Add(new Student { LoginName = "a", Password = "p", Name = "Alice", Sex = GenderEnum.Female, EnRollDate = new DateTime(2020, 1, 2) });
            vm.DC.Set<Student>().Add(new Student { LoginName = "b", Password = "p", Name = "Bob", Sex = GenderEnum.Male });
            vm.DC.SaveChanges();
            return vm;
        }

        [TestMethod]
        public void GenerateExcel_PassesOpenXmlSchemaValidation()
        {
            var bytes = NewExportVm().GenerateExcel();

            var errors = XlsxTestHelper.SchemaErrors(bytes);

            Assert.AreEqual(0, errors.Count, "export has " + errors.Count + " schema error(s):\n" + string.Join("\n", errors));
        }

        [TestMethod]
        public void GenerateExcel_CustomSixDigitTitleColorsAreStillValidArgb()
        {
            var vm = NewExportVm();
            vm.ExportTitleBackColor = "#FFD700";
            vm.ExportTitleFontColor = "0000FF";

            var bytes = vm.GenerateExcel();

            Assert.AreEqual(0, XlsxTestHelper.SchemaErrors(bytes).Count);
            var styles = StylesOf(bytes);
            Assert.AreEqual("FFFFD700", HeaderFill(styles, 2));
            Assert.AreEqual("FF0000FF", styles.Fonts.Elements<Font>().ElementAt(1).Color.Rgb.Value);
        }

        [TestMethod]
        public void GenerateExcel_KeepsHeaderStyling()
        {
            var bytes = NewExportVm().GenerateExcel();

            var styles = StylesOf(bytes);
            // fill 2 = header background (default light steel blue, now with the FF alpha byte)
            Assert.AreEqual("FFB0C4DE", HeaderFill(styles, 2));
            var font = styles.Fonts.Elements<Font>().ElementAt(1);
            Assert.AreEqual(12d, font.FontSize.Val.Value);
            Assert.AreEqual("Calibri", font.FontName.Val.Value);
            Assert.AreEqual("FF000000", font.Color.Rgb.Value);
            // header cells use cell format 1 (font 1 + fill 2), data cells format 2
            var header = styles.CellFormats.Elements<CellFormat>().ElementAt(1);
            Assert.AreEqual(1u, header.FontId.Value);
            Assert.AreEqual(2u, header.FillId.Value);
            Assert.AreEqual(HorizontalAlignmentValues.Center, header.Alignment.Horizontal.Value);
        }

        #endregion

        #region template

        private static ExcelPropety With(ExcelPropety p, Action<ExcelPropety> set)
        {
            set(p);
            return p;
        }

        private class StudentTemplateVM : BaseTemplateVM
        {
            public ExcelPropety LoginName = ExcelPropety.CreateProperty<Student>(x => x.LoginName);
            public ExcelPropety Name = With(ExcelPropety.CreateProperty<Student>(x => x.Name), p => p.BackgroudColor = BackgroudColorEnum.Yellow);
            public ExcelPropety Sex = With(ExcelPropety.CreateProperty<Student>(x => x.Sex), p => p.BackgroudColor = BackgroudColorEnum.Red);
            public ExcelPropety EnRollDate = ExcelPropety.CreateProperty<Student>(x => x.EnRollDate);
            public ExcelPropety OrderNum = ExcelPropety.CreateProperty<GoodsSpecification>(x => x.OrderNum);
            public new ExcelPropety Remark = With(ExcelPropety.CreateProperty<GoodsSpecification>(x => x.Remark), p => p.ReadOnly = true);
        }

        private class StudentImportVM : BaseImportVM<StudentTemplateVM, Student>
        {
        }

        private class PlainTemplateVM : BaseTemplateVM
        {
            public ExcelPropety LoginName = ExcelPropety.CreateProperty<Student>(x => x.LoginName);
        }

        private class PlainImportVM : BaseImportVM<PlainTemplateVM, Student>
        {
        }

        private Microsoft.Extensions.Localization.IStringLocalizer _previousLocalizer;

        [TestInitialize]
        public void InstallLocalizer()
        {
            _previousLocalizer = CoreProgram._localizer;
            CoreProgram._localizer = new TagHelpers.LayUI.KeyEchoLocalizer();
        }

        [TestCleanup]
        public void RestoreLocalizer()
        {
            CoreProgram._localizer = _previousLocalizer;
        }

        [TestMethod]
        public void GenerateTemplate_PassesOpenXmlSchemaValidation()
        {
            var bytes = new StudentImportVM().GenerateTemplate(out _);

            var errors = XlsxTestHelper.SchemaErrors(bytes);

            Assert.AreEqual(0, errors.Count, "template has " + errors.Count + " schema error(s):\n" + string.Join("\n", errors));
        }

        [TestMethod]
        public void GenerateTemplate_WithoutProtectedColumns_PassesOpenXmlSchemaValidation()
        {
            var bytes = new PlainImportVM().GenerateTemplate(out _);

            var errors = XlsxTestHelper.SchemaErrors(bytes);

            Assert.AreEqual(0, errors.Count, "template has " + errors.Count + " schema error(s):\n" + string.Join("\n", errors));
        }

        [TestMethod]
        public void GenerateTemplate_KeepsHeaderColorsAndFont()
        {
            var bytes = new StudentImportVM().GenerateTemplate(out _);

            var styles = StylesOf(bytes);
            Assert.AreEqual("FFD9E2F3", HeaderFill(styles, 2), "grey (default) header");
            Assert.AreEqual("FFFFF2CC", HeaderFill(styles, 3), "yellow header");
            Assert.AreEqual("FFFCE4EC", HeaderFill(styles, 4), "red header");
            var font = styles.Fonts.Elements<Font>().ElementAt(1);
            Assert.AreEqual(11d, font.FontSize.Val.Value);
            Assert.AreEqual("Calibri", font.FontName.Val.Value);
            var headerStyles = styles.CellFormats.Elements<CellFormat>().Skip(1).Take(3).Select(f => f.FillId.Value).ToArray();
            CollectionAssert.AreEqual(new uint[] { 2, 3, 4 }, headerStyles);
        }

        [TestMethod]
        public void GenerateTemplate_FirstRowIsFrozenInSheetView()
        {
            var bytes = new StudentImportVM().GenerateTemplate(out _);

            using var doc = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
            var sheet = FirstWorksheet(doc);
            var pane = sheet.GetFirstChild<SheetViews>()?.Elements<SheetView>().Single().GetFirstChild<Pane>();
            Assert.IsNotNull(pane, "frozen pane must live in SheetViews/SheetView");
            Assert.AreEqual(PaneStateValues.Frozen, pane.State.Value);
            Assert.AreEqual(1d, pane.VerticalSplit.Value);
            Assert.AreEqual("A2", pane.TopLeftCell.Value);
            Assert.AreEqual(PaneValues.BottomLeft, pane.ActivePane.Value);
            // SheetViews has to be the first child of the worksheet (before columns / sheetData)
            Assert.IsInstanceOfType(sheet.FirstChild, typeof(SheetViews));
        }

        [TestMethod]
        public void GenerateTemplate_KeepsSheetProtectionAndHiddenSheets()
        {
            var bytes = new StudentImportVM().GenerateTemplate(out _);

            using var doc = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
            Assert.IsNotNull(FirstWorksheet(doc).GetFirstChild<SheetProtection>(), "read-only column must still protect the sheet");
            var sheets = XlsxTestHelper.SheetNames(bytes);
            Assert.IsFalse(sheets[0].Hidden);
            Assert.IsTrue(sheets[1].Hidden);
            Assert.IsTrue(sheets[2].Hidden);
            Assert.IsNotNull(doc.WorkbookPart.Workbook.GetFirstChild<BookViews>());
        }

        [TestMethod]
        public void LegacyPasswordHash_MatchesKnownExcelValue()
        {
            // "password" hashes to 83AF in the ECMA-376 legacy 16-bit scheme Excel uses for sheetProtection@password
            Assert.AreEqual("83AF", OpenXmlHelper.LegacyPasswordHash("password"));
        }

        [TestMethod]
        public void ToArgb_AddsOpaqueAlphaToSixDigitColorsOnly()
        {
            Assert.AreEqual("FFB0C4DE", OpenXmlHelper.ToArgb("B0C4DE"));
            Assert.AreEqual("FFB0C4DE", OpenXmlHelper.ToArgb("#b0c4de"));
            Assert.AreEqual("80B0C4DE", OpenXmlHelper.ToArgb("80B0C4DE"));
        }

        #endregion

        #region helpers

        private static Stylesheet StylesOf(byte[] bytes)
        {
            using var doc = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
            // detach a copy so it stays usable after the package is closed
            return (Stylesheet)doc.WorkbookPart.WorkbookStylesPart.Stylesheet.CloneNode(true);
        }

        private static string HeaderFill(Stylesheet styles, int fillIndex)
        {
            return styles.Fills.Elements<Fill>().ElementAt(fillIndex).PatternFill.ForegroundColor.Rgb.Value;
        }

        private static Worksheet FirstWorksheet(SpreadsheetDocument doc)
        {
            var workbook = doc.WorkbookPart;
            var sheet = workbook.Workbook.GetFirstChild<Sheets>().Elements<Sheet>().OrderBy(s => s.SheetId.Value).First();
            return ((WorksheetPart)workbook.GetPartById(sheet.Id)).Worksheet;
        }

        #endregion
    }
}
