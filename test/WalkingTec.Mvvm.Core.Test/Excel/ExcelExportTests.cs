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

        /// <summary>Name is a plain data column; Remark is formatted to HTML; OrderNum is formatted to a plain string.</summary>
        private class TagLikeExportListVM : BasePagedListVM<GoodsSpecification, BaseSearcher>
        {
            protected override IEnumerable<IGridColumn<GoodsSpecification>> InitGridHeader()
            {
                return new List<GridColumn<GoodsSpecification>>
                {
                    this.MakeGridHeader(x => x.Name),
                    this.MakeGridHeader(x => x.Remark).SetFormat((entity, val) => "<span class=\"c\">" + val + "</span>"),
                    this.MakeGridHeader(x => x.OrderNum).SetFormat((entity, val) => "No." + val),
                };
            }

            public override IOrderedQueryable<GoodsSpecification> GetSearchQuery()
            {
                return DC.Set<GoodsSpecification>().OrderBy(x => x.Name);
            }
        }

        /// <summary>
        /// Remark is formatted to what the grid shows as buttons or links (a ColumnFormatInfo or a list of them), chosen per row by the Name.
        /// </summary>
        private class FormatInfoExportListVM : BasePagedListVM<GoodsSpecification, BaseSearcher>
        {
            public static readonly Guid FileId = Guid.NewGuid();

            protected override IEnumerable<IGridColumn<GoodsSpecification>> InitGridHeader()
            {
                return new List<GridColumn<GoodsSpecification>>
                {
                    this.MakeGridHeader(x => x.Name),
                    this.MakeGridHeader(x => x.Remark).SetFormat((entity, val) => Format(entity)),
                    this.MakeGridHeader(x => x.OrderNum),
                };
            }

            private static object Format(GoodsSpecification entity)
            {
                switch (entity.Name)
                {
                    case "download":
                        return ColumnFormatInfo.MakeDownloadButton(ButtonTypesEnum.Button, FileId, "Download");
                    case "view":
                        return ColumnFormatInfo.MakeViewButton(ButtonTypesEnum.Link, FileId, buttonText: "Photo");
                    case "nofile":
                        return ColumnFormatInfo.MakeDownloadButton(ButtonTypesEnum.Button, null, "Download");
                    case "dialog":
                        return ColumnFormatInfo.MakeDialogButton(ButtonTypesEnum.Link, "/Goods/Details/1", "Details", 600, null, "Title");
                    case "script":
                        return ColumnFormatInfo.MakeScriptButton(ButtonTypesEnum.Button, "", "Run", script: "alert(1)");
                    case "html":
                        return ColumnFormatInfo.MakeHtml("<b>bold</b> and <i>italic</i>");
                    case "list":
                        return new List<ColumnFormatInfo>
                        {
                            ColumnFormatInfo.MakeDialogButton(ButtonTypesEnum.Link, "/Goods/Details/1", "Details", 600, null, "Title"),
                            ColumnFormatInfo.MakeDownloadButton(ButtonTypesEnum.Button, FileId, "Download"),
                        };
                    case "listsome":
                        return new List<ColumnFormatInfo>
                        {
                            null,
                            ColumnFormatInfo.MakeDownloadButton(ButtonTypesEnum.Button, null, "Download"),
                            ColumnFormatInfo.MakeDialogButton(ButtonTypesEnum.Link, "/Goods/Details/1", "Details", 600, null, "Title"),
                            ColumnFormatInfo.MakeHtml(""),
                        };
                    case "emptylist":
                        return new List<ColumnFormatInfo>();
                    case "null":
                        return null;
                    case "string":
                        return "plain " + entity.Remark;
                    case "htmlstring":
                        return "<a href=\"/x\" class=\"b\">Open</a>";
                    default:
                        return entity.Remark;
                }
            }

            public override IOrderedQueryable<GoodsSpecification> GetSearchQuery()
            {
                return DC.Set<GoodsSpecification>().OrderBy(x => x.OrderNum);
            }
        }

        private static string[] ExportRow(string name, string remark = "r")
        {
            var vm = NewListVm<FormatInfoExportListVM>(NewSeed());
            vm.DC.Set<GoodsSpecification>().Add(new GoodsSpecification { Name = name, OrderNum = 7, Remark = remark });
            vm.DC.SaveChanges();
            var rows = XlsxTestHelper.ReadSheet(vm.GenerateExcel());
            Assert.AreEqual(2, rows.Count);
            return rows[1];
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

        // Issue #285: only columns with a Format callback produce HTML; plain data is exported as it is.
        [TestMethod]
        public void GenerateExcel_PlainColumn_KeepsTagLikeText()
        {
            var vm = NewListVm<GoodsExportListVM>(NewSeed());
            vm.DC.Set<GoodsSpecification>().Add(new GoodsSpecification { Name = "a<b>c", OrderNum = 1, Remark = "<tag>" });
            vm.DC.Set<GoodsSpecification>().Add(new GoodsSpecification { Name = "x < y and y > z", OrderNum = 2, Remark = "<script>alert(1)</script>" });
            vm.DC.SaveChanges();

            var rows = XlsxTestHelper.ReadSheet(vm.GenerateExcel());

            Assert.AreEqual(3, rows.Count);
            CollectionAssert.AreEqual(new[] { "a<b>c", "1", "<tag>" }, rows[1]);
            CollectionAssert.AreEqual(new[] { "x < y and y > z", "2", "<script>alert(1)</script>" }, rows[2]);
        }

        [TestMethod]
        public void GenerateExcel_FormattedColumn_StillStripsTags()
        {
            var vm = NewListVm<TagLikeExportListVM>(NewSeed());
            vm.DC.Set<GoodsSpecification>().Add(new GoodsSpecification { Name = "<tag>", OrderNum = 5, Remark = "r" });
            vm.DC.SaveChanges();

            var rows = XlsxTestHelper.ReadSheet(vm.GenerateExcel());

            Assert.AreEqual(2, rows.Count);
            // Name is plain data: unchanged. Remark has a Format that wraps the value in a span: the tags are removed.
            // OrderNum has a Format that returns a plain string: nothing to remove.
            CollectionAssert.AreEqual(new[] { "<tag>", "r", "No.5" }, rows[1]);
        }

        [TestMethod]
        public void GenerateExcel_FormattedColumn_RemovesTagLikeTextInTheValueToo()
        {
            // the Format callback decides what the cell holds; its whole output is treated as HTML (the behavior before #285)
            var vm = NewListVm<TagLikeExportListVM>(NewSeed());
            vm.DC.Set<GoodsSpecification>().Add(new GoodsSpecification { Name = "n", OrderNum = 1, Remark = "a<b>c" });
            vm.DC.SaveChanges();

            var rows = XlsxTestHelper.ReadSheet(vm.GenerateExcel());

            CollectionAssert.AreEqual(new[] { "n", "ac", "No.1" }, rows[1]);
        }

        // Issue #289: a Format callback that returns a ColumnFormatInfo (button/link) used to export the type name.
        [TestMethod]
        public void GenerateExcel_FormatReturnsColumnFormatInfo_ExportsTheVisibleText()
        {
            // download, preview, dialog and script buttons all show their Text in the grid
            CollectionAssert.AreEqual(new[] { "download", "Download", "7" }, ExportRow("download"));
            CollectionAssert.AreEqual(new[] { "view", "Photo", "7" }, ExportRow("view"));
            CollectionAssert.AreEqual(new[] { "dialog", "Details", "7" }, ExportRow("dialog"));
            CollectionAssert.AreEqual(new[] { "script", "Run", "7" }, ExportRow("script"));
        }

        [TestMethod]
        public void GenerateExcel_FormatReturnsColumnFormatInfo_NeverWritesATypeName()
        {
            foreach (var name in new[] { "download", "view", "nofile", "dialog", "script", "html", "list", "listsome", "emptylist", "null" })
            {
                foreach (var cell in ExportRow(name))
                {
                    Assert.IsFalse(cell.Contains("ColumnFormatInfo") || cell.Contains("System.Collections"), name + ": " + cell);
                }
            }
        }

        [TestMethod]
        public void GenerateExcel_FormatReturnsListOfColumnFormatInfo_ExportsTheTextsJoined()
        {
            CollectionAssert.AreEqual(new[] { "list", "Details, Download", "7" }, ExportRow("list"));
        }

        [TestMethod]
        public void GenerateExcel_FormatReturnsListOfColumnFormatInfo_SkipsItemsThatShowNothing()
        {
            // a null item, a download button without a file and an empty html item render nothing in the grid
            CollectionAssert.AreEqual(new[] { "listsome", "Details", "7" }, ExportRow("listsome"));
            CollectionAssert.AreEqual(new[] { "emptylist", "", "7" }, ExportRow("emptylist"));
        }

        [TestMethod]
        public void GenerateExcel_FormatReturnsDownloadButtonWithoutFile_ExportsAnEmptyCell()
        {
            // the grid renders no button when there is no file
            CollectionAssert.AreEqual(new[] { "nofile", "", "7" }, ExportRow("nofile"));
        }

        [TestMethod]
        public void GenerateExcel_FormatReturnsHtmlColumnFormatInfo_StripsTags()
        {
            CollectionAssert.AreEqual(new[] { "html", "bold and italic", "7" }, ExportRow("html"));
        }

        [TestMethod]
        public void GenerateExcel_FormatReturnsNull_ExportsAnEmptyCell()
        {
            CollectionAssert.AreEqual(new[] { "null", "", "7" }, ExportRow("null"));
        }

        // Unchanged by #289: a string result (HTML or not) is exported as before, tags removed (#285).
        [TestMethod]
        public void GenerateExcel_FormatReturnsString_IsExportedAsBefore()
        {
            CollectionAssert.AreEqual(new[] { "string", "plain r", "7" }, ExportRow("string"));
            CollectionAssert.AreEqual(new[] { "string", "plain x", "7" }, ExportRow("string", "<b>x</b>"));
        }

        [TestMethod]
        public void GenerateExcel_FormatReturnsHtmlString_StillStripsTags()
        {
            CollectionAssert.AreEqual(new[] { "htmlstring", "Open", "7" }, ExportRow("htmlstring"));
        }

        [TestMethod]
        public void GenerateExcel_FormatReturnsTheRawValue_IsExportedAsBefore()
        {
            // a column with a Format callback is treated as HTML as a whole (the behavior before #285 and still today)
            CollectionAssert.AreEqual(new[] { "other", "x", "7" }, ExportRow("other", "<tag>x"));
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
            var tempDir = System.IO.Path.GetDirectoryName(root);
            var rootName = System.IO.Path.GetFileName(root);
            string[] strayDirs() => System.IO.Directory.GetDirectories(tempDir, rootName + "*").Where(d => System.IO.Path.GetFileName(d).Contains('\\')).ToArray();

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

                // Issue #89: a hard-coded "\\export" made a sibling directory with a backslash in its name on Linux/macOS.
                Assert.IsTrue(System.IO.Directory.Exists(root), "temp files must be created under HostRoot");
                Assert.AreEqual(0, strayDirs().Length, "no directory with a backslash in its name may be created");
                // the temp export folder (and the zip inside it) is removed after the bytes are read
                Assert.AreEqual(0, System.IO.Directory.GetFileSystemEntries(root).Length, "temp export folder must be cleaned up");
            }
            finally
            {
                if (System.IO.Directory.Exists(root))
                {
                    System.IO.Directory.Delete(root, true);
                }
                foreach (var d in strayDirs())
                {
                    System.IO.Directory.Delete(d, true);
                }
            }
        }
    }
}
