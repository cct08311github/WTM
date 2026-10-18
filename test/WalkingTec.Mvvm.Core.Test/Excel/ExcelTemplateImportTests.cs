using System;
using System.Collections.Generic;
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

        #endregion
    }
}
