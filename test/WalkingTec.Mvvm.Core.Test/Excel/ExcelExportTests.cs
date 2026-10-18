using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Excel
{
    /// <summary>
    /// BasePagedListVM.GenerateExcel is what the LayUI grid "export" button calls (/_Framework/ExportExcel).
    /// v10 rewrote it from NPOI to DocumentFormat.OpenXml, so these tests read the produced bytes back.
    /// </summary>
    [TestClass]
    public class ExcelExportTests
    {
        private class GoodsExportListVM : BasePagedListVM<GoodsSpecification, BaseSearcher>
        {
            protected override IEnumerable<IGridColumn<GoodsSpecification>> InitGridHeader()
            {
                return new List<GridColumn<GoodsSpecification>>
                {
                    this.MakeGridHeader(x => x.ID),
                    this.MakeGridHeader(x => x.Name),
                    this.MakeGridHeader(x => x.OrderNum),
                    this.MakeGridHeader(x => x.Remark),
                    this.MakeGridHeaderAction(width: 100),
                };
            }

            public override IOrderedQueryable<GoodsSpecification> GetSearchQuery()
            {
                return DC.Set<GoodsSpecification>().OrderBy(x => x.OrderNum);
            }
        }

        private class SchoolExportListVM : BasePagedListVM<School, BaseSearcher>
        {
            protected override IEnumerable<IGridColumn<School>> InitGridHeader()
            {
                return new List<GridColumn<School>>
                {
                    this.MakeGridHeader(x => x.SchoolCode),
                    this.MakeGridHeader(x => x.SchoolName),
                    this.MakeGridHeader(x => x.SchoolType),
                };
            }

            public override IOrderedQueryable<School> GetSearchQuery()
            {
                return DC.Set<School>().OrderBy(x => x.SchoolCode);
            }
        }

        private static string NewSeed() => Guid.NewGuid().ToString();

        private static T NewListVm<T>(string seed) where T : BaseVM, new()
        {
            return new T { Wtm = MockWtmContext.CreateWtmContext(new DataContext(seed, DBTypeEnum.Memory), "exportuser") };
        }

        [TestMethod]
        public void GenerateExcel_ProducesValidXlsxWithHeaderAndDataRows()
        {
            var seed = NewSeed();
            var vm = NewListVm<GoodsExportListVM>(seed);
            vm.DC.Set<GoodsSpecification>().Add(new GoodsSpecification { Name = "bolt", OrderNum = 2, Remark = "r2" });
            vm.DC.Set<GoodsSpecification>().Add(new GoodsSpecification { Name = "nut", OrderNum = 1, Remark = "r1" });
            vm.DC.SaveChanges();

            var bytes = vm.GenerateExcel();

            Assert.IsTrue(XlsxTestHelper.LooksLikeZip(bytes), "export output is not an OOXML zip package");
            var rows = XlsxTestHelper.ReadSheet(bytes);
            Assert.AreEqual(3, rows.Count, "header + 2 data rows");
            // header row: ID and the action column are not exported
            CollectionAssert.AreEqual(new[] { "品种名称", "序号", "备注" }, rows[0]);
            // data rows follow the query order (OrderNum asc)
            CollectionAssert.AreEqual(new[] { "nut", "1", "r1" }, rows[1]);
            CollectionAssert.AreEqual(new[] { "bolt", "2", "r2" }, rows[2]);
        }

        [TestMethod]
        public void GenerateExcel_SheetIsNamedAfterModelType()
        {
            var vm = NewListVm<GoodsExportListVM>(NewSeed());

            var bytes = vm.GenerateExcel();

            var sheets = XlsxTestHelper.SheetNames(bytes);
            Assert.AreEqual(1, sheets.Count);
            Assert.AreEqual(nameof(GoodsSpecification), sheets[0].Name);
        }

        [TestMethod]
        public void GenerateExcel_NumericColumnIsStoredAsNumber_TextAsInlineString()
        {
            var seed = NewSeed();
            var vm = NewListVm<GoodsExportListVM>(seed);
            vm.DC.Set<GoodsSpecification>().Add(new GoodsSpecification { Name = "bolt", OrderNum = 7, Remark = "r" });
            vm.DC.SaveChanges();

            var bytes = vm.GenerateExcel();

            using var doc = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(new System.IO.MemoryStream(bytes), false);
            var sheet = (DocumentFormat.OpenXml.Packaging.WorksheetPart)doc.WorkbookPart.GetPartById(
                doc.WorkbookPart.Workbook.GetFirstChild<DocumentFormat.OpenXml.Spreadsheet.Sheets>().Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().First().Id);
            var dataRow = sheet.Worksheet.GetFirstChild<DocumentFormat.OpenXml.Spreadsheet.SheetData>().Elements<DocumentFormat.OpenXml.Spreadsheet.Row>().Last();
            var cells = dataRow.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>().ToList();
            Assert.AreEqual(DocumentFormat.OpenXml.Spreadsheet.CellValues.InlineString, cells[0].DataType.Value);
            Assert.AreEqual(DocumentFormat.OpenXml.Spreadsheet.CellValues.Number, cells[1].DataType.Value);
            Assert.AreEqual("7", cells[1].CellValue.Text);
        }

        [TestMethod]
        public void GenerateExcel_EnumColumnUsesDisplayName()
        {
            var seed = NewSeed();
            var vm = NewListVm<SchoolExportListVM>(seed);
            vm.DC.Set<School>().Add(new School { SchoolCode = "001", SchoolName = "First", SchoolType = SchoolTypeEnum.PRI, Remark = "x" });
            vm.DC.Set<School>().Add(new School { SchoolCode = "002", SchoolName = "Second", SchoolType = SchoolTypeEnum.PUB, Remark = "x" });
            vm.DC.SaveChanges();

            var rows = XlsxTestHelper.ReadSheet(vm.GenerateExcel());

            Assert.AreEqual(3, rows.Count);
            CollectionAssert.AreEqual(new[] { "学校编码", "学校名称", "学校类型" }, rows[0]);
            CollectionAssert.AreEqual(new[] { "001", "First", "私立学校" }, rows[1]);
            CollectionAssert.AreEqual(new[] { "002", "Second", "公立学校" }, rows[2]);
        }

        [TestMethod]
        public void GenerateExcel_EmptyResult_StillProducesHeaderOnlyWorkbook()
        {
            var vm = NewListVm<SchoolExportListVM>(NewSeed());

            var rows = XlsxTestHelper.ReadSheet(vm.GenerateExcel());

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(3, rows[0].Length);
        }

        [TestMethod]
        public void GenerateExcel_MoreRowsThanExportMaxCount_ReturnsZipOfWorkbooks()
        {
            var seed = NewSeed();
            var vm = NewListVm<SchoolExportListVM>(seed);
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wtm-export-" + Guid.NewGuid().ToString("N"));
            vm.Wtm.ConfigInfo.HostRoot = root;
            vm.ExportMaxCount = 2;
            for (int i = 1; i <= 3; i++)
            {
                vm.DC.Set<School>().Add(new School { SchoolCode = i.ToString("000"), SchoolName = "s" + i, SchoolType = SchoolTypeEnum.PUB, Remark = "x" });
            }
            vm.DC.SaveChanges();

            try
            {
                var bytes = vm.GenerateExcel();

                Assert.AreEqual(2, vm.ExportExcelCount);
                using var zip = new System.IO.Compression.ZipArchive(new System.IO.MemoryStream(bytes));
                var xlsxEntries = zip.Entries.Where(e => e.Name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)).ToList();
                Assert.AreEqual(2, xlsxEntries.Count);
                foreach (var entry in xlsxEntries)
                {
                    using var ms = new System.IO.MemoryStream();
                    using (var s = entry.Open()) { s.CopyTo(ms); }
                    Assert.IsTrue(XlsxTestHelper.LooksLikeZip(ms.ToArray()));
                }
            }
            finally
            {
                if (System.IO.Directory.Exists(root))
                {
                    System.IO.Directory.Delete(root, true);
                }
            }
        }
    }
}
