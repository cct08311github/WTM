using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Excel
{
    /// <summary>
    /// Template download (/_Framework/GetExcelTemplate) and import (BaseImportVM) of the LayUI pages.
    /// Both were rewritten from NPOI to DocumentFormat.OpenXml in v10; the tests exercise the real
    /// round trip: generate template -> fill rows -> store as uploaded file -> import.
    /// </summary>
    [TestClass]
    public class ExcelTemplateImportTests
    {
        #region template / import VMs (what the code generator emits for a model)

        private class SchoolTemplateVM : BaseTemplateVM
        {
            public ExcelPropety SchoolCode = ExcelPropety.CreateProperty<School>(x => x.SchoolCode);
            public ExcelPropety SchoolName = ExcelPropety.CreateProperty<School>(x => x.SchoolName);
            public ExcelPropety SchoolType = ExcelPropety.CreateProperty<School>(x => x.SchoolType);
            public new ExcelPropety Remark = ExcelPropety.CreateProperty<School>(x => x.Remark);
        }

        private class SchoolImportVM : BaseImportVM<SchoolTemplateVM, School>
        {
        }

        // Remark is optional here, so Excel omits the blank cell when the user leaves it empty.
        private class MajorTemplateVM : BaseTemplateVM
        {
            public ExcelPropety MajorCode = ExcelPropety.CreateProperty<Major>(x => x.MajorCode);
            public ExcelPropety MajorName = ExcelPropety.CreateProperty<Major>(x => x.MajorName);
            public new ExcelPropety Remark = ExcelPropety.CreateProperty<Major>(x => x.Remark);
            public ExcelPropety MajorType = ExcelPropety.CreateProperty<Major>(x => x.MajorType);
        }

        private class MajorImportVM : BaseImportVM<MajorTemplateVM, Major>
        {
        }

        // Optional column is the last one, so a blank Remark leaves the row one cell short.
        private class MajorRemarkLastTemplateVM : BaseTemplateVM
        {
            public ExcelPropety MajorCode = ExcelPropety.CreateProperty<Major>(x => x.MajorCode);
            public ExcelPropety MajorName = ExcelPropety.CreateProperty<Major>(x => x.MajorName);
            public ExcelPropety MajorType = ExcelPropety.CreateProperty<Major>(x => x.MajorType);
            public new ExcelPropety Remark = ExcelPropety.CreateProperty<Major>(x => x.Remark);
        }

        private class MajorRemarkLastImportVM : BaseImportVM<MajorRemarkLastTemplateVM, Major>
        {
        }

        // DataContext variant with the (CS) constructor WTMContext.CreateDC() uses. Internal on purpose, so it is not
        // picked up by CS.Cis (which only scans exported types) and cannot affect other tests.
        internal sealed class ImportTestDataContext : DataContext
        {
            public ImportTestDataContext(CS cs) : base(cs.Value, cs.DbType ?? DBTypeEnum.Memory)
            {
            }
        }

        #endregion

        #region helpers

        private Microsoft.Extensions.Localization.IStringLocalizer _previousLocalizer;

        [TestInitialize]
        public void InstallLocalizer()
        {
            // import/template code reads error texts and "Yes/No" from the static CoreProgram._localizer
            _previousLocalizer = CoreProgram._localizer;
            CoreProgram._localizer = new TagHelpers.LayUI.KeyEchoLocalizer();
            // WtmFileProvider keeps its handler registry in static state that the host fills at startup;
            // without it GetFile() cannot resolve the "database" handler and the import sees no file.
            WtmFileProvider.Init(new Configs(), new GlobalData { AllAssembly = new List<System.Reflection.Assembly> { typeof(WtmFileProvider).Assembly } });
        }

        [TestCleanup]
        public void RestoreLocalizer()
        {
            CoreProgram._localizer = _previousLocalizer;
        }

        private static WTMContext NewWtm(string seed)
        {
            var wtm = MockWtmContext.CreateWtmContext(new DataContext(seed, DBTypeEnum.Memory), "importuser");
            // WTMContext.CreateDC() inspects the Referer header; the shared mock leaves Request.Headers unset (null)
            Moq.Mock.Get(wtm.HttpContext.Request).Setup(x => x.Headers).Returns(new Microsoft.AspNetCore.Http.HeaderDictionary());
            // the import reads/deletes the uploaded file through WtmFileProvider using Wtm.CreateDC(), which goes through Configs.Connections
            wtm.ConfigInfo.Connections = new List<CS>
            {
                new CS
                {
                    Key = "default",
                    Value = seed,
                    DbType = DBTypeEnum.Memory,
                    DcConstructor = typeof(ImportTestDataContext).GetConstructor(new[] { typeof(CS) }),
                },
            };
            return wtm;
        }

        private static Cell TextCell(int col, int row, string text) => new Cell
        {
            CellReference = OpenXmlHelper.GetCellReference(col, row),
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(text)),
        };

        /// <summary>
        /// Appends data rows under the header of the first sheet. A null entry means "blank cell" and the cell is
        /// omitted, which is how Excel stores empty cells.
        /// </summary>
        private static byte[] FillTemplate(byte[] template, params string[][] rows)
        {
            var ms = new MemoryStream();
            ms.Write(template, 0, template.Length);
            ms.Position = 0;
            using (var doc = SpreadsheetDocument.Open(ms, true))
            {
                var workbook = doc.WorkbookPart;
                var sheet = workbook.Workbook.GetFirstChild<Sheets>().Elements<Sheet>().OrderBy(s => s.SheetId.Value).First();
                var sheetData = ((WorksheetPart)workbook.GetPartById(sheet.Id)).Worksheet.GetFirstChild<SheetData>();
                for (int r = 0; r < rows.Length; r++)
                {
                    var row = new Row { RowIndex = (uint)(r + 2) };
                    for (int c = 0; c < rows[r].Length; c++)
                    {
                        if (rows[r][c] != null)
                        {
                            row.AppendChild(TextCell(c, r + 1, rows[r][c]));
                        }
                    }
                    sheetData.AppendChild(row);
                }
                workbook.Workbook.Save();
            }
            return ms.ToArray();
        }

        /// <summary>Removes the r="A2" attribute from every data row cell, like some non-Excel writers do. The header row is kept.</summary>
        private static byte[] StripDataCellReferences(byte[] xlsx)
        {
            var ms = new MemoryStream();
            ms.Write(xlsx, 0, xlsx.Length);
            ms.Position = 0;
            using (var doc = SpreadsheetDocument.Open(ms, true))
            {
                var workbook = doc.WorkbookPart;
                var sheet = workbook.Workbook.GetFirstChild<Sheets>().Elements<Sheet>().OrderBy(s => s.SheetId.Value).First();
                var sheetData = ((WorksheetPart)workbook.GetPartById(sheet.Id)).Worksheet.GetFirstChild<SheetData>();
                foreach (var row in sheetData.Elements<Row>().Skip(1))
                {
                    foreach (var cell in row.Elements<Cell>())
                    {
                        cell.CellReference = null;
                    }
                }
                workbook.Workbook.Save();
            }
            return ms.ToArray();
        }

        /// <summary>Stores bytes the way /_Framework/Upload does, and returns the file id for BaseImportVM.UploadFileId.</summary>
        private static string Upload(WTMContext wtm, byte[] xlsx)
        {
            var provider = new WtmFileProvider(wtm);
            var file = provider.Upload("import.xlsx", xlsx.Length, new MemoryStream(xlsx), dc: wtm.DC);
            return file.GetID().ToString();
        }

        private static T NewImportVm<T>(WTMContext wtm, byte[] xlsx) where T : BaseVM, new()
        {
            var vm = new T { Wtm = wtm };
            var id = Upload(wtm, xlsx);
            vm.GetType().GetProperty("UploadFileId").SetValue(vm, id);
            return vm;
        }

        #endregion

        #region template generation

        [TestMethod]
        public void GenerateTemplate_ProducesValidXlsxWithColumnHeaders()
        {
            var vm = new SchoolImportVM();

            var bytes = vm.GenerateTemplate(out var displayName);

            Assert.IsTrue(XlsxTestHelper.LooksLikeZip(bytes));
            StringAssert.StartsWith(displayName, nameof(SchoolTemplateVM) + "_");
            StringAssert.EndsWith(displayName, ".xlsx");
            var rows = XlsxTestHelper.ReadSheet(bytes);
            Assert.AreEqual(1, rows.Count, "template contains only the header row");
            // required columns are marked with '*'
            CollectionAssert.AreEqual(new[] { "学校编码*", "学校名称*", "学校类型*", "备注*" }, rows[0]);
        }

        [TestMethod]
        public void GenerateTemplate_OptionalColumnHasNoStar()
        {
            var bytes = new MajorImportVM().GenerateTemplate(out _);

            var header = XlsxTestHelper.ReadSheet(bytes)[0];

            CollectionAssert.AreEqual(new[] { "专业编码*", "专业名称*", "备注", "专业类别*" }, header);
        }

        [TestMethod]
        public void GenerateTemplate_HasHiddenReferenceSheetsWithTemplateTypeMarker()
        {
            var bytes = new SchoolImportVM().GenerateTemplate(out _);

            var sheets = XlsxTestHelper.SheetNames(bytes);
            Assert.AreEqual(3, sheets.Count);
            Assert.AreEqual(nameof(SchoolTemplateVM), sheets[0].Name);
            Assert.IsFalse(sheets[0].Hidden);
            Assert.IsTrue(sheets[1].Hidden, "Sheet2 (yes/no + type marker) must be hidden");
            Assert.IsTrue(sheets[2].Hidden, "Sheet3 (dropdown data) must be hidden");

            var marker = XlsxTestHelper.ReadSheet(bytes, 1);
            Assert.AreEqual(nameof(SchoolTemplateVM), marker[0][2], "C1 holds the template type name checked on import");
        }

        [TestMethod]
        public void GenerateTemplate_EnumColumnListsDisplayNamesInDropdownSheet()
        {
            var bytes = new SchoolImportVM().GenerateTemplate(out _);

            var dropdown = XlsxTestHelper.ReadSheet(bytes, 2);

            // SchoolType is the 3rd column => C1:C2 hold the enum display names used by the data validation list
            var values = dropdown.Select(r => r.Length > 2 ? r[2] : string.Empty).ToList();
            CollectionAssert.AreEquivalent(new[] { "公立学校", "私立学校" }, values);
        }

        [TestMethod]
        public void GenerateTemplate_HasDataValidationForEveryColumn()
        {
            var bytes = new SchoolImportVM().GenerateTemplate(out _);

            using var doc = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
            var workbook = doc.WorkbookPart;
            var sheet = workbook.Workbook.GetFirstChild<Sheets>().Elements<Sheet>().OrderBy(s => s.SheetId.Value).First();
            var validations = ((WorksheetPart)workbook.GetPartById(sheet.Id)).Worksheet.GetFirstChild<DataValidations>();

            Assert.IsNotNull(validations);
            Assert.AreEqual(4, validations.Elements<DataValidation>().Count());
            var enumList = validations.Elements<DataValidation>().Single(v => v.Type?.Value == DataValidationValues.List);
            StringAssert.Contains(enumList.SequenceOfReferences.InnerText, "C2:C1048576");
        }

        #endregion

        #region import

        [TestMethod]
        public void Import_RoundTripOfFilledTemplate_BuildsEntityList()
        {
            var wtm = NewWtm(Guid.NewGuid().ToString());
            var template = new SchoolImportVM().GenerateTemplate(out _);
            var filled = FillTemplate(template,
                new[] { "101", "First school", "公立学校", "remark one" },
                new[] { "102", "Second school", "私立学校", "remark two" });
            var vm = NewImportVm<SchoolImportVM>(wtm, filled);

            vm.SetEntityList();

            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count, string.Join("; ", vm.ErrorListVM.EntityList.Select(x => x.Message)));
            Assert.AreEqual(2, vm.EntityList.Count);
            Assert.AreEqual("101", vm.EntityList[0].SchoolCode);
            Assert.AreEqual("First school", vm.EntityList[0].SchoolName);
            Assert.AreEqual(SchoolTypeEnum.PUB, vm.EntityList[0].SchoolType);
            Assert.AreEqual("remark one", vm.EntityList[0].Remark);
            Assert.AreEqual("102", vm.EntityList[1].SchoolCode);
            Assert.AreEqual(SchoolTypeEnum.PRI, vm.EntityList[1].SchoolType);
        }

        [TestMethod]
        public void Import_BatchSaveData_PersistsRowsAndRemovesUploadedFile()
        {
            var seed = Guid.NewGuid().ToString();
            var wtm = NewWtm(seed);
            var template = new SchoolImportVM().GenerateTemplate(out _);
            var filled = FillTemplate(template,
                new[] { "201", "Alpha", "公立学校", "a" },
                new[] { "202", "Beta", "私立学校", "b" });
            var vm = NewImportVm<SchoolImportVM>(wtm, filled);
            var uploadId = vm.UploadFileId;

            var ok = vm.BatchSaveData();

            Assert.IsTrue(ok, string.Join("; ", vm.ErrorListVM.EntityList.Select(x => x.Message)));
            using var check = new DataContext(seed, DBTypeEnum.Memory);
            var saved = check.Set<School>().OrderBy(x => x.SchoolCode).ToList();
            Assert.AreEqual(2, saved.Count);
            Assert.AreEqual("Alpha", saved[0].SchoolName);
            Assert.AreEqual(SchoolTypeEnum.PRI, saved[1].SchoolType);
            Assert.AreEqual("importuser", saved[0].CreateBy);
            Assert.IsNotNull(saved[0].CreateTime);
            Assert.IsFalse(check.Set<FileAttachment>().Any(x => x.ID.ToString() == uploadId), "uploaded template must be deleted after a successful import");
        }

        [TestMethod]
        public void Import_RowWithInvalidValue_ReportsErrorAndSavesNothing()
        {
            var seed = Guid.NewGuid().ToString();
            var wtm = NewWtm(seed);
            var template = new SchoolImportVM().GenerateTemplate(out _);
            // "9" violates the 3-digit regular expression of School.SchoolCode
            var filled = FillTemplate(template, new[] { "9", "Bad", "公立学校", "x" });
            var vm = NewImportVm<SchoolImportVM>(wtm, filled);

            var ok = vm.BatchSaveData();

            Assert.IsFalse(ok);
            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0);
            Assert.IsFalse(vm.ErrorListVM.EntityList.Any(x => x.Message == "Sys.WrongTemplate"), "the template itself is valid, only the value is wrong");
            using var check = new DataContext(seed, DBTypeEnum.Memory);
            Assert.AreEqual(0, check.Set<School>().Count());
        }

        // #288: GetErrorJson opened the uploaded bytes through a fixed-size MemoryStream for writing and failed with
        // "Memory stream is not expandable." as soon as the import had an invalid row.
        [TestMethod]
        public void Import_RowWithInvalidValue_GetErrorJsonReturnsErrorFileWithRowMessage()
        {
            var wtm = NewWtm(Guid.NewGuid().ToString());
            var template = new SchoolImportVM().GenerateTemplate(out _);
            // row 2 is valid, row 3 violates the 3-digit regular expression of School.SchoolCode
            var filled = FillTemplate(template,
                new[] { "101", "Good", "公立学校", "ok" },
                new[] { "9", "Bad", "公立学校", "x" });
            var vm = NewImportVm<SchoolImportVM>(wtm, filled);

            Assert.IsFalse(vm.BatchSaveData());
            var rowErrors = vm.ErrorListVM.EntityList.Where(x => x.Index > 0).ToList();
            Assert.IsTrue(rowErrors.Count > 0);
            Assert.IsTrue(rowErrors.All(x => x.Index == 3), "only the third sheet row is invalid");

            ErrorObj error = null;
            try
            {
                error = vm.GetErrorJson();
            }
            catch (NotSupportedException ex)
            {
                Assert.Fail("GetErrorJson must not throw: " + ex.Message);
            }

            Assert.IsTrue(error.Form.ContainsKey("Entity.Import"));
            Assert.IsTrue(error.Form.ContainsKey("Entity.ErrorFileId"), "the error file is offered to the user");

            // the error file is the uploaded workbook plus one message cell after the last column of each invalid row
            var errorFile = new WtmFileProvider(wtm).GetFile(error.Form["Entity.ErrorFileId"], true, wtm.DC);
            byte[] errorBytes;
            using (var ms = new MemoryStream())
            {
                errorFile.DataStream.CopyTo(ms);
                errorBytes = ms.ToArray();
            }
            var rows = XlsxTestHelper.ReadSheet(errorBytes);
            Assert.AreEqual(3, rows.Count);
            Assert.AreEqual(4, rows[0].Length, "header row is untouched");
            Assert.AreEqual(4, rows[1].Length, "valid row is untouched");
            Assert.AreEqual(5, rows[2].Length, "invalid row gets the message cell");
            Assert.AreEqual(rowErrors[0].Message, rows[2][4]);
            Assert.AreEqual("9", rows[2][0], "the original values are kept");
        }

        [TestMethod]
        public void Import_SeveralInvalidRows_GetErrorJsonMarksEachRow()
        {
            var wtm = NewWtm(Guid.NewGuid().ToString());
            var template = new SchoolImportVM().GenerateTemplate(out _);
            var filled = FillTemplate(template,
                new[] { "8", "Bad one", "公立学校", "x" },
                new[] { "102", "Good", "私立学校", "ok" },
                new[] { "7", "Bad two", "公立学校", "x" });
            var vm = NewImportVm<SchoolImportVM>(wtm, filled);

            Assert.IsFalse(vm.BatchSaveData());

            var error = vm.GetErrorJson();

            var errorFile = new WtmFileProvider(wtm).GetFile(error.Form["Entity.ErrorFileId"], true, wtm.DC);
            byte[] errorBytes;
            using (var ms = new MemoryStream())
            {
                errorFile.DataStream.CopyTo(ms);
                errorBytes = ms.ToArray();
            }
            var rows = XlsxTestHelper.ReadSheet(errorBytes);
            Assert.AreEqual(4, rows.Count);
            Assert.AreEqual(4, rows[0].Length);
            Assert.AreEqual(5, rows[1].Length, "row 2 is invalid");
            Assert.AreEqual(4, rows[2].Length, "row 3 is valid");
            Assert.AreEqual(5, rows[3].Length, "row 4 is invalid");
        }

        [TestMethod]
        public void Import_NonRowError_GetErrorJsonReturnsMessageWithoutErrorFile()
        {
            var wtm = NewWtm(Guid.NewGuid().ToString());
            var template = new MajorImportVM().GenerateTemplate(out _);
            // a template of another type is a whole-file error (row index 0)
            var vm = NewImportVm<SchoolImportVM>(wtm, FillTemplate(template, new[] { "101", "Name", null, "必修" }));

            Assert.IsFalse(vm.BatchSaveData());

            var error = vm.GetErrorJson();

            Assert.IsTrue(error.Form.ContainsKey("Entity.Import"));
            Assert.IsFalse(error.Form.ContainsKey("Entity.ErrorFileId"));
        }

        [TestMethod]
        public void Import_UnknownEnumText_ReportsFormatError()
        {
            var wtm = NewWtm(Guid.NewGuid().ToString());
            var template = new SchoolImportVM().GenerateTemplate(out _);
            var filled = FillTemplate(template, new[] { "301", "Name", "not-a-school-type", "x" });
            var vm = NewImportVm<SchoolImportVM>(wtm, filled);

            vm.SetEntityList();

            Assert.AreEqual(1, vm.ErrorListVM.EntityList.Count);
            StringAssert.Contains(vm.ErrorListVM.EntityList[0].Message, "ValueNotExist");
            Assert.AreEqual(2, vm.ErrorListVM.EntityList[0].Index, "error points at Excel row 2");
        }

        [TestMethod]
        public void Import_TemplateOfAnotherType_IsRejected()
        {
            var wtm = NewWtm(Guid.NewGuid().ToString());
            var otherTemplate = new MajorImportVM().GenerateTemplate(out _);
            var vm = NewImportVm<SchoolImportVM>(wtm, otherTemplate);

            vm.SetEntityList();

            Assert.AreEqual(1, vm.ErrorListVM.EntityList.Count, "the hidden type marker must not match");
            Assert.AreEqual("Sys.WrongTemplate", vm.ErrorListVM.EntityList[0].Message);
            Assert.AreEqual(0, vm.EntityList.Count);
        }

        [TestMethod]
        public void Import_WithoutUploadedFile_ReportsError()
        {
            var vm = new SchoolImportVM { Wtm = NewWtm(Guid.NewGuid().ToString()) };

            vm.SetEntityList();

            Assert.AreEqual(1, vm.ErrorListVM.EntityList.Count);
            Assert.AreEqual("Sys.PleaseUploadTemplate", vm.ErrorListVM.EntityList[0].Message);
            Assert.AreEqual(0, vm.EntityList.Count);
        }

        [TestMethod]
        public void Import_StopsAtFirstEmptyRow()
        {
            var wtm = NewWtm(Guid.NewGuid().ToString());
            var template = new SchoolImportVM().GenerateTemplate(out _);
            var filled = FillTemplate(template,
                new[] { "401", "One", "公立学校", "x" },
                new string[] { null, null, null, null },
                new[] { "402", "AfterGap", "公立学校", "x" });
            var vm = NewImportVm<SchoolImportVM>(wtm, filled);

            vm.SetEntityList();

            Assert.AreEqual(1, vm.EntityList.Count);
        }

        // Regression for #86: Excel omits blank cells, the import used to read cells by list position.
        [TestMethod]
        public void Import_BlankOptionalCellInTheMiddle_KeepsLaterColumnsAligned()
        {
            var wtm = NewWtm(Guid.NewGuid().ToString());
            var template = new MajorImportVM().GenerateTemplate(out _);
            // Remark (column C) is blank => the C cell is not in the XML, MajorType (D) is the third cell of the row
            var filled = FillTemplate(template,
                new[] { "101", "Math", null, "必修" },
                new[] { "102", "Physics", "has remark", "选修" });
            var vm = NewImportVm<MajorImportVM>(wtm, filled);

            vm.SetEntityList();

            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count, string.Join("; ", vm.ErrorListVM.EntityList.Select(x => x.Message)));
            Assert.AreEqual(2, vm.EntityList.Count);
            Assert.AreEqual("101", vm.EntityList[0].MajorCode);
            Assert.AreEqual("Math", vm.EntityList[0].MajorName);
            Assert.IsTrue(string.IsNullOrEmpty(vm.EntityList[0].Remark));
            Assert.AreEqual(MajorTypeEnum.Required, vm.EntityList[0].MajorType);
            Assert.AreEqual("has remark", vm.EntityList[1].Remark);
            Assert.AreEqual(MajorTypeEnum.Optional, vm.EntityList[1].MajorType);
        }

        [TestMethod]
        public void Import_BlankLeadingOptionalCells_KeepLaterColumnsAligned()
        {
            var wtm = NewWtm(Guid.NewGuid().ToString());
            var template = new MajorImportVM().GenerateTemplate(out _);
            // code and name left empty: only C and D are in the XML (required-ness of text is checked later, on save)
            var filled = FillTemplate(template, new[] { null, null, "x", "必修" });
            var vm = NewImportVm<MajorImportVM>(wtm, filled);

            vm.SetEntityList();

            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count, string.Join("; ", vm.ErrorListVM.EntityList.Select(x => x.Message)));
            Assert.AreEqual(1, vm.EntityList.Count);
            Assert.IsTrue(string.IsNullOrEmpty(vm.EntityList[0].MajorCode));
            Assert.IsTrue(string.IsNullOrEmpty(vm.EntityList[0].MajorName));
            Assert.AreEqual("x", vm.EntityList[0].Remark);
            Assert.AreEqual(MajorTypeEnum.Required, vm.EntityList[0].MajorType);
        }

        [TestMethod]
        public void Import_BlankLastOptionalCell_ImportsRow()
        {
            var wtm = NewWtm(Guid.NewGuid().ToString());
            var template = new MajorRemarkLastImportVM().GenerateTemplate(out _);
            var filled = FillTemplate(template,
                new[] { "201", "Chemistry", "选修", null },
                new[] { "202", "Biology", "必修", "bio remark" });
            var vm = NewImportVm<MajorRemarkLastImportVM>(wtm, filled);

            vm.SetEntityList();

            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count, string.Join("; ", vm.ErrorListVM.EntityList.Select(x => x.Message)));
            Assert.AreEqual(2, vm.EntityList.Count);
            Assert.AreEqual(MajorTypeEnum.Optional, vm.EntityList[0].MajorType);
            Assert.IsTrue(string.IsNullOrEmpty(vm.EntityList[0].Remark));
            Assert.AreEqual("bio remark", vm.EntityList[1].Remark);
        }

        [TestMethod]
        public void Import_RowWithOnlyBlankCells_IsTreatedAsEmptyAndStopsImport()
        {
            var wtm = NewWtm(Guid.NewGuid().ToString());
            var template = new MajorImportVM().GenerateTemplate(out _);
            // second row has a cell for every column but all of them hold only whitespace/empty text
            var filled = FillTemplate(template,
                new[] { "301", "First", null, "必修" },
                new[] { "", " ", "", "" },
                new[] { "302", "AfterGap", null, "必修" });
            var vm = NewImportVm<MajorImportVM>(wtm, filled);

            vm.SetEntityList();

            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count, string.Join("; ", vm.ErrorListVM.EntityList.Select(x => x.Message)));
            Assert.AreEqual(1, vm.EntityList.Count);
        }

        [TestMethod]
        public void Import_RowWithOnlyLastColumnFilled_IsNotTreatedAsEmpty()
        {
            var wtm = NewWtm(Guid.NewGuid().ToString());
            var template = new MajorImportVM().GenerateTemplate(out _);
            var filled = FillTemplate(template, new[] { null, null, null, "必修" });
            var vm = NewImportVm<MajorImportVM>(wtm, filled);

            vm.SetEntityList();

            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count, string.Join("; ", vm.ErrorListVM.EntityList.Select(x => x.Message)));
            Assert.AreEqual(1, vm.EntityList.Count, "the row has data in column D, so it is not an empty row");
            Assert.AreEqual(MajorTypeEnum.Required, vm.EntityList[0].MajorType);
        }

        [TestMethod]
        public void Import_CellsWithoutReference_AreReadInOrder()
        {
            var wtm = NewWtm(Guid.NewGuid().ToString());
            var template = new MajorImportVM().GenerateTemplate(out _);
            var filled = StripDataCellReferences(FillTemplate(template,
                new[] { "401", "Art", "note", "必修" },
                new[] { "402", "Music", "note 2", "选修" }));
            var vm = NewImportVm<MajorImportVM>(wtm, filled);

            vm.SetEntityList();

            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count, string.Join("; ", vm.ErrorListVM.EntityList.Select(x => x.Message)));
            Assert.AreEqual(2, vm.EntityList.Count);
            Assert.AreEqual("401", vm.EntityList[0].MajorCode);
            Assert.AreEqual("note", vm.EntityList[0].Remark);
            Assert.AreEqual(MajorTypeEnum.Required, vm.EntityList[0].MajorType);
            Assert.AreEqual("Music", vm.EntityList[1].MajorName);
            Assert.AreEqual(MajorTypeEnum.Optional, vm.EntityList[1].MajorType);
        }

        [TestMethod]
        public void Import_BlankOptionalCell_BatchSaveDataPersistsCorrectType()
        {
            var seed = Guid.NewGuid().ToString();
            var wtm = NewWtm(seed);
            var template = new MajorImportVM().GenerateTemplate(out _);
            var filled = FillTemplate(template, new[] { "501", "Law", null, "必修" });
            var vm = NewImportVm<MajorImportVM>(wtm, filled);

            var ok = vm.BatchSaveData();

            Assert.IsTrue(ok, string.Join("; ", vm.ErrorListVM.EntityList.Select(x => x.Message)));
            using var check = new DataContext(seed, DBTypeEnum.Memory);
            var saved = check.Set<Major>().Single();
            Assert.AreEqual("Law", saved.MajorName);
            Assert.AreEqual(MajorTypeEnum.Required, saved.MajorType);
        }

        #endregion

        #region date cells (#295)

        // Excel stores a date as a number plus a number format, so a date cell reaches the reader as text like "46317".
        private class DateRow : BasePoco
        {
            [Display(Name = "Qty")]
            public int Qty { get; set; }

            [Display(Name = "Day")]
            public DateTime? Day { get; set; }

            [Display(Name = "Stamp")]
            public DateTime? Stamp { get; set; }
        }

        private class DateTemplateVM : BaseTemplateVM
        {
            public ExcelPropety Qty = ExcelPropety.CreateProperty<DateRow>(x => x.Qty);
            public ExcelPropety Day = ExcelPropety.CreateProperty<DateRow>(x => x.Day);
            public ExcelPropety Stamp = ExcelPropety.CreateProperty<DateRow>(x => x.Stamp, true);
        }

        private class DateImportVM : BaseImportVM<DateTemplateVM, DateRow>
        {
            public List<DateTemplateVM> Rows => TemplateData;
        }

        /// <summary>Style index 7 of the WTM template: number format 165, yyyy-MM-dd HH:mm:ss.</summary>
        private const uint WtmTemplateDateStyle = 7;

        private static readonly DateTime SampleDay = new DateTime(2026, 10, 22);
        private static readonly DateTime SampleStamp = new DateTime(2026, 10, 22, 13, 45, 30);

        /// <summary>Adds a cell format (and its custom number format code, when given) and returns the style index.</summary>
        private static uint AddStyle(WorkbookPart workbook, uint numberFormatId, string formatCode = null)
        {
            var stylesheet = workbook.WorkbookStylesPart.Stylesheet;
            if (formatCode != null)
            {
                var formats = stylesheet.NumberingFormats;
                if (formats == null)
                {
                    formats = new NumberingFormats();
                    stylesheet.InsertAt(formats, 0);
                }
                formats.Append(new NumberingFormat { NumberFormatId = numberFormatId, FormatCode = formatCode });
                formats.Count = (uint)formats.ChildElements.Count;
            }
            var cellFormats = stylesheet.CellFormats;
            cellFormats.Append(new CellFormat { NumberFormatId = numberFormatId, ApplyNumberFormat = true });
            cellFormats.Count = (uint)cellFormats.ChildElements.Count;
            return (uint)cellFormats.ChildElements.Count - 1;
        }

        // new MemoryStream(byte[]) cannot grow, which the package needs when it is disposed after a change
        private static MemoryStream ExpandableStream(byte[] bytes)
        {
            var ms = new MemoryStream();
            ms.Write(bytes, 0, bytes.Length);
            ms.Position = 0;
            return ms;
        }

        private static string Serial(DateTime value) => value.ToOADate().ToString("R", CultureInfo.InvariantCulture);

        /// <summary>A numeric cell: no t attribute, like Excel writes numbers and dates.</summary>
        private static Cell NumberCell(int col, string value, uint? style, CellValues? type = null) => new Cell
        {
            CellReference = OpenXmlHelper.GetCellReference(col, 1),
            CellValue = new CellValue(value),
            StyleIndex = style,
            DataType = type,
        };

        /// <summary>Appends row 2 to the template. <paramref name="cells"/> may add styles to the workbook first.</summary>
        private static byte[] FillRowWithCells(byte[] template, Func<WorkbookPart, Cell[]> cells, bool date1904 = false)
        {
            var ms = new MemoryStream();
            ms.Write(template, 0, template.Length);
            ms.Position = 0;
            using (var doc = SpreadsheetDocument.Open(ms, true))
            {
                var workbook = doc.WorkbookPart;
                var sheet = workbook.Workbook.GetFirstChild<Sheets>().Elements<Sheet>().OrderBy(s => s.SheetId.Value).First();
                var sheetData = ((WorksheetPart)workbook.GetPartById(sheet.Id)).Worksheet.GetFirstChild<SheetData>();
                var row = new Row { RowIndex = 2 };
                foreach (var cell in cells(workbook))
                {
                    row.AppendChild(cell);
                }
                sheetData.AppendChild(row);
                if (date1904)
                {
                    workbook.Workbook.WorkbookProperties = new WorkbookProperties { Date1904 = true };
                }
                workbook.Workbook.Save();
                workbook.WorkbookStylesPart.Stylesheet.Save();
            }
            return ms.ToArray();
        }

        private static DateImportVM ImportRow(Func<WorkbookPart, Cell[]> cells, bool date1904 = false)
            => ImportRow<DateImportVM>(cells, date1904);

        private static T ImportRow<T>(Func<WorkbookPart, Cell[]> cells, bool date1904 = false) where T : BaseVM, new()
        {
            var wtm = NewWtm(Guid.NewGuid().ToString());
            dynamic generator = new T();
            byte[] template = generator.GenerateTemplate(out string _);
            var vm = NewImportVm<T>(wtm, FillRowWithCells(template, cells, date1904));
            ((dynamic)vm).SetTemplateData();
            return vm;
        }

        private static void AssertValue(DateImportVM vm, object qty, object day, object stamp)
        {
            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count, string.Join("; ", vm.ErrorListVM.EntityList.Select(e => e.Message)));
            Assert.AreEqual(1, vm.Rows.Count);
            Assert.AreEqual(qty, vm.Rows[0].Qty.Value);
            Assert.AreEqual(day, vm.Rows[0].Day.Value);
            Assert.AreEqual(stamp, vm.Rows[0].Stamp.Value);
        }

        [TestMethod]
        public void Import_DateColumn_BuiltInDateFormatCell_IsReadAsDate()
        {
            var vm = ImportRow(wb => new[]
            {
                NumberCell(0, "7", null),
                NumberCell(1, Serial(SampleDay), AddStyle(wb, 14)),
                NumberCell(2, Serial(SampleStamp), AddStyle(wb, 22)),
            });

            AssertValue(vm, 7, SampleDay, SampleStamp);
        }

        [TestMethod]
        public void Import_DateColumn_EastAsianBuiltInFormatCell_IsReadAsDate()
        {
            var vm = ImportRow(wb => new[]
            {
                NumberCell(0, "7", null),
                NumberCell(1, Serial(SampleDay), AddStyle(wb, 31)),
                NumberCell(2, Serial(SampleStamp), AddStyle(wb, 57)),
            });

            AssertValue(vm, 7, SampleDay, SampleStamp);
        }

        [TestMethod]
        public void Import_DateTimeColumn_WtmTemplateDateStyle_IsReadAsDateTime()
        {
            // the format the downloaded template gives the date columns: custom number format 165, yyyy-MM-dd HH:mm:ss
            var vm = ImportRow(wb => new[]
            {
                NumberCell(0, "7", null),
                NumberCell(1, Serial(SampleDay), WtmTemplateDateStyle),
                NumberCell(2, Serial(SampleStamp), WtmTemplateDateStyle, CellValues.Number),
            });

            AssertValue(vm, 7, SampleDay, SampleStamp);
        }

        [TestMethod]
        public void Import_DateColumn_CustomFormatWithSingleLetterParts_IsReadAsDate()
        {
            var vm = ImportRow(wb => new[]
            {
                NumberCell(0, "7", null),
                NumberCell(1, Serial(SampleDay), AddStyle(wb, 166, "yyyy/m/d")),
                NumberCell(2, Serial(SampleStamp), AddStyle(wb, 167, "[$-F800]dddd, mmmm dd, yyyy h:mm")),
            });

            AssertValue(vm, 7, SampleDay, SampleStamp);
        }

        [TestMethod]
        public void Import_NumberColumn_WithDecimalFormat_KeepsTheNumber()
        {
            var vm = ImportRow(wb => new[]
            {
                NumberCell(0, "5", AddStyle(wb, 168, "0.00")),
                NumberCell(1, Serial(SampleDay), AddStyle(wb, 14)),
                NumberCell(2, Serial(SampleStamp), WtmTemplateDateStyle),
            });

            AssertValue(vm, 5, SampleDay, SampleStamp);
        }

        [TestMethod]
        public void Import_NumberColumn_WithDateFormat_IsNotConverted()
        {
            // only Date/DateTime columns read the serial as a date
            var vm = ImportRow(wb => new[]
            {
                NumberCell(0, "45953", AddStyle(wb, 14)),
                NumberCell(1, Serial(SampleDay), AddStyle(wb, 14)),
                NumberCell(2, Serial(SampleStamp), WtmTemplateDateStyle),
            });

            AssertValue(vm, 45953, SampleDay, SampleStamp);
        }

        [TestMethod]
        public void Import_DateColumn_ElapsedTimeBuiltInFormat_IsNotConverted()
        {
            // built-in 46 is [h]:mm:ss, a duration rather than a point in time
            var vm = ImportRow(wb => new[]
            {
                NumberCell(0, "7", null),
                NumberCell(1, "45953.5", AddStyle(wb, 46)),
                NumberCell(2, "45953.5", AddStyle(wb, 46)),
            });

            Assert.AreEqual(2, vm.ErrorListVM.EntityList.Count, "same format error as before #295");
            Assert.IsTrue(vm.ErrorListVM.EntityList.All(e => e.Index == 2));
        }

        [TestMethod]
        public void Import_DateColumn_ElapsedTimeCustomFormat_IsNotConverted()
        {
            var vm = ImportRow(wb => new[]
            {
                NumberCell(0, "7", null),
                NumberCell(1, "45953.5", AddStyle(wb, 170, "[h]:mm:ss")),
                NumberCell(2, "45953.5", AddStyle(wb, 171, "[mm]:ss")),
            });

            Assert.AreEqual(2, vm.ErrorListVM.EntityList.Count, "same format error as before #295");
            Assert.IsTrue(vm.ErrorListVM.EntityList.All(e => e.Index == 2));
        }

        private class TextRow : BasePoco
        {
            [Display(Name = "Qty")]
            public int Qty { get; set; }

            [Display(Name = "Label")]
            public string Label { get; set; }
        }

        private class TextTemplateVM : BaseTemplateVM
        {
            public ExcelPropety Qty = ExcelPropety.CreateProperty<TextRow>(x => x.Qty);
            public ExcelPropety Label = ExcelPropety.CreateProperty<TextRow>(x => x.Label);
        }

        private class TextImportVM : BaseImportVM<TextTemplateVM, TextRow>
        {
            public List<TextTemplateVM> Rows => TemplateData;
        }

        [TestMethod]
        public void Import_TextColumn_WithDateFormat_KeepsTheSerialString()
        {
            var vm = ImportRow<TextImportVM>(wb => new[]
            {
                NumberCell(0, "7", null),
                NumberCell(1, "45953", AddStyle(wb, 14)),
            });

            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count, string.Join("; ", vm.ErrorListVM.EntityList.Select(e => e.Message)));
            Assert.AreEqual(1, vm.Rows.Count);
            Assert.AreEqual(7, vm.Rows[0].Qty.Value);
            Assert.AreEqual("45953", vm.Rows[0].Label.Value);
        }

        [TestMethod]
        public void Import_DateColumn_TextCells_AreParsedAsBefore()
        {
            var vm = ImportRow(wb => new[]
            {
                NumberCell(0, "7", null),
                TextCell(1, 1, "2026-10-22"),
                // a text cell that carries a date style is still text
                new Cell
                {
                    CellReference = OpenXmlHelper.GetCellReference(2, 1),
                    DataType = CellValues.InlineString,
                    InlineString = new InlineString(new Text("2026-10-22 13:45:30")),
                    StyleIndex = WtmTemplateDateStyle,
                },
            });

            AssertValue(vm, 7, SampleDay, SampleStamp);
        }

        [TestMethod]
        public void Import_DateColumn_NumberWithoutDateFormat_IsStillAFormatError()
        {
            var vm = ImportRow(wb => new[]
            {
                NumberCell(0, "7", null),
                NumberCell(1, Serial(SampleDay), null),
                NumberCell(2, Serial(SampleStamp), AddStyle(wb, 2)),
            });

            Assert.AreEqual(2, vm.ErrorListVM.EntityList.Count, "both date columns hold a bare number");
            Assert.IsTrue(vm.ErrorListVM.EntityList.All(e => e.Index == 2));
        }

        [TestMethod]
        public void Import_DateColumn_1904DateSystem_UsesTheShiftedEpoch()
        {
            // the same serial means a date 1462 days later in the 1900 system
            var serial1904 = (SampleDay.ToOADate() - 1462).ToString("R", CultureInfo.InvariantCulture);
            var vm = ImportRow(wb => new[]
            {
                NumberCell(0, "7", null),
                NumberCell(1, serial1904, AddStyle(wb, 14)),
                NumberCell(2, serial1904, AddStyle(wb, 14)),
            }, date1904: true);

            AssertValue(vm, 7, SampleDay, SampleDay);
        }

        [TestMethod]
        public void Import_DateColumn_SerialOutOfRange_IsAFormatError()
        {
            var vm = ImportRow(wb => new[]
            {
                NumberCell(0, "7", null),
                NumberCell(1, "99999999", AddStyle(wb, 14)),
                NumberCell(2, "-5", AddStyle(wb, 14)),
            });

            Assert.AreEqual(2, vm.ErrorListVM.EntityList.Count);
        }

        [DataTestMethod]
        [DataRow(14u, null, true)]
        [DataRow(15u, null, true)]
        [DataRow(16u, null, true)]
        [DataRow(17u, null, true)]
        [DataRow(18u, null, true)]
        [DataRow(19u, null, true)]
        [DataRow(20u, null, true)]
        [DataRow(21u, null, true)]
        [DataRow(22u, null, true)]
        [DataRow(27u, null, true)]
        [DataRow(28u, null, true)]
        [DataRow(29u, null, true)]
        [DataRow(30u, null, true)]
        [DataRow(31u, null, true)]
        [DataRow(32u, null, true)]
        [DataRow(33u, null, true)]
        [DataRow(34u, null, true)]
        [DataRow(35u, null, true)]
        [DataRow(36u, null, true)]
        [DataRow(45u, null, true)]
        [DataRow(47u, null, true)]
        [DataRow(50u, null, true)]
        [DataRow(51u, null, true)]
        [DataRow(52u, null, true)]
        [DataRow(53u, null, true)]
        [DataRow(54u, null, true)]
        [DataRow(55u, null, true)]
        [DataRow(56u, null, true)]
        [DataRow(57u, null, true)]
        [DataRow(58u, null, true)]
        [DataRow(46u, null, false)]
        [DataRow(0u, null, false)]
        [DataRow(2u, null, false)]
        [DataRow(10u, null, false)]
        [DataRow(13u, null, false)]
        [DataRow(23u, null, false)]
        [DataRow(26u, null, false)]
        [DataRow(37u, null, false)]
        [DataRow(44u, null, false)]
        [DataRow(48u, null, false)]
        [DataRow(49u, null, false)]
        [DataRow(59u, null, false)]
        [DataRow(163u, null, false)]
        [DataRow(170u, "yyyy-mm-dd", true)]
        [DataRow(170u, "yyyy/m/d", true)]
        [DataRow(170u, "yyyy-MM-dd HH:mm:ss", true)]
        [DataRow(170u, "m/d/yy;@", true)]
        [DataRow(170u, "[$-404]e/m/d", true)]
        [DataRow(170u, "[$-F800]dddd, mmmm dd, yyyy", true)]
        [DataRow(170u, "h:mm AM/PM", true)]
        [DataRow(170u, "mm:ss", true)]
        [DataRow(170u, "\"年\"yyyy", true)]
        [DataRow(170u, "0.00", false)]
        [DataRow(170u, "#,##0", false)]
        [DataRow(170u, "General", false)]
        [DataRow(170u, "\"days\"0", false)]
        [DataRow(170u, "[Red]0.00", false)]
        [DataRow(170u, "[h]:mm:ss", false)]
        [DataRow(170u, "[mm]:ss", false)]
        [DataRow(170u, "[ss]", false)]
        [DataRow(170u, "[hh]:mm", false)]
        [DataRow(170u, "#,##0.00_);[Red](#,##0.00)", false)]
        [DataRow(170u, "0.00E+00", false)]
        [DataRow(170u, "0.0%", false)]
        [DataRow(170u, "@", false)]
        [DataRow(170u, "#,##0 \"m\"", false)]
        [DataRow(170u, "0\\d", false)]
        [DataRow(171u, null, false)]
        public void IsDateFormattedCell_ClassifiesNumberFormats(uint formatId, string formatCode, bool expected)
        {
            var template = new DateImportVM().GenerateTemplate(out _);
            using var doc = SpreadsheetDocument.Open(ExpandableStream(template), true);
            var workbook = doc.WorkbookPart;
            var style = AddStyle(workbook, formatId, formatCode);

            Assert.AreEqual(expected, OpenXmlHelper.IsDateFormattedCell(NumberCell(0, "1", style), workbook));
        }

        [TestMethod]
        public void IsDateFormattedCell_NoStyleInformation_IsFalse()
        {
            var template = new DateImportVM().GenerateTemplate(out _);
            using var doc = SpreadsheetDocument.Open(ExpandableStream(template), true);
            var workbook = doc.WorkbookPart;

            Assert.IsFalse(OpenXmlHelper.IsDateFormattedCell(null, workbook));
            Assert.IsFalse(OpenXmlHelper.IsDateFormattedCell(NumberCell(0, "1", null), workbook), "style 0 is General");
            Assert.IsFalse(OpenXmlHelper.IsDateFormattedCell(NumberCell(0, "1", 999), workbook), "style index beyond the table");
            Assert.IsFalse(OpenXmlHelper.IsDateFormattedCell(NumberCell(0, "1", WtmTemplateDateStyle), null));

            workbook.DeletePart(workbook.WorkbookStylesPart);
            Assert.IsFalse(OpenXmlHelper.IsDateFormattedCell(NumberCell(0, "1", WtmTemplateDateStyle), workbook), "no stylesheet");
        }

        #endregion
    }
}
