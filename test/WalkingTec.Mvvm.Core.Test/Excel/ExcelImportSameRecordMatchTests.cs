using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Localization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Excel
{
    /// <summary>
    /// #397: with IsOverWriteExistData = true and two or more effective duplicate groups, two file rows could match the same existing record
    /// through different groups; the later row silently overwrote the earlier one. Now the later row gets an error, the remaining rows are still
    /// checked, and nothing is saved.
    /// </summary>
    [TestClass]
    public class ExcelImportSameRecordMatchTests
    {
        private const string ConflictKey = "Sys.ImportRecordMatchedByMultipleRows";

        #region template / import VMs

        private class DocTemplateVM : BaseTemplateVM
        {
            public ExcelPropety Code = ExcelPropety.CreateProperty<ImportSameRecordDoc>(x => x.Code);
            public ExcelPropety Title = ExcelPropety.CreateProperty<ImportSameRecordDoc>(x => x.Title);
            public ExcelPropety Ref = ExcelPropety.CreateProperty<ImportSameRecordDoc>(x => x.Ref);
        }

        // three effective groups: Code, Title, Ref
        private class ThreeGroupImportVM : BaseImportVM<DocTemplateVM, ImportSameRecordDoc>
        {
            public override DuplicatedInfo<ImportSameRecordDoc> SetDuplicatedCheck()
                => CreateFieldsInfo(SimpleField(x => x.Code)).AddGroup(SimpleField(x => x.Title)).AddGroup(SimpleField(x => x.Ref));
        }

        private class TwoGroupImportVM : BaseImportVM<DocTemplateVM, ImportSameRecordDoc>
        {
            public override DuplicatedInfo<ImportSameRecordDoc> SetDuplicatedCheck()
                => CreateFieldsInfo(SimpleField(x => x.Code)).AddGroup(SimpleField(x => x.Title));
        }

        private class OneGroupImportVM : BaseImportVM<DocTemplateVM, ImportSameRecordDoc>
        {
            public override DuplicatedInfo<ImportSameRecordDoc> SetDuplicatedCheck() => CreateFieldsInfo(SimpleField(x => x.Code));
        }

        private class IntTemplateVM : BaseTemplateVM
        {
            public ExcelPropety Code = ExcelPropety.CreateProperty<ImportSameRecordIntDoc>(x => x.Code);
            public ExcelPropety Title = ExcelPropety.CreateProperty<ImportSameRecordIntDoc>(x => x.Title);
        }

        private class IntImportVM : BaseImportVM<IntTemplateVM, ImportSameRecordIntDoc>
        {
            public override DuplicatedInfo<ImportSameRecordIntDoc> SetDuplicatedCheck()
                => CreateFieldsInfo(SimpleField(x => x.Code)).AddGroup(SimpleField(x => x.Title));
        }

        private class StringTemplateVM : BaseTemplateVM
        {
            public ExcelPropety Code = ExcelPropety.CreateProperty<ImportSameRecordStringDoc>(x => x.Code);
            public ExcelPropety Title = ExcelPropety.CreateProperty<ImportSameRecordStringDoc>(x => x.Title);
        }

        private class StringImportVM : BaseImportVM<StringTemplateVM, ImportSameRecordStringDoc>
        {
            public override DuplicatedInfo<ImportSameRecordStringDoc> SetDuplicatedCheck()
                => CreateFieldsInfo(SimpleField(x => x.Code)).AddGroup(SimpleField(x => x.Title));
        }

        private sealed class ImportTestDataContext : DataContext
        {
            public ImportTestDataContext(CS cs) : base(cs.Value, DBTypeEnum.Memory)
            {
            }
        }

        // global NoTracking: rows found by the duplicate check are separate, detached instances
        private sealed class NoTrackingImportTestDataContext : DataContext
        {
            public NoTrackingImportTestDataContext(CS cs) : base(cs.Value, DBTypeEnum.Memory)
            {
                ChangeTracker.QueryTrackingBehavior = Microsoft.EntityFrameworkCore.QueryTrackingBehavior.NoTracking;
            }
        }

        /// <summary>Returns "key|arg0|arg1" so a test sees which key was used and which row number was passed.</summary>
        private sealed class ArgEchoLocalizer : IStringLocalizer
        {
            public LocalizedString this[string name] => new LocalizedString(name, name);

            public LocalizedString this[string name, params object[] arguments] =>
                new LocalizedString(name, arguments == null || arguments.Length == 0 ? name : name + "|" + string.Join("|", arguments));

            public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
        }

        /// <summary>Like a resource file that does not have the key: the value is the key name and ResourceNotFound is true.</summary>
        private sealed class MissingKeyLocalizer : IStringLocalizer
        {
            public LocalizedString this[string name] => new LocalizedString(name, name, true);

            public LocalizedString this[string name, params object[] arguments] => new LocalizedString(name, name, true);

            public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
        }

        #endregion

        #region helpers

        private IStringLocalizer _previousLocalizer;

        [TestInitialize]
        public void InstallLocalizer()
        {
            _previousLocalizer = CoreProgram._localizer;
            CoreProgram._localizer = new ArgEchoLocalizer();
            WtmFileProvider.Init(new Configs(), new GlobalData { AllAssembly = new List<System.Reflection.Assembly> { typeof(WtmFileProvider).Assembly } });
        }

        [TestCleanup]
        public void RestoreLocalizer()
        {
            CoreProgram._localizer = _previousLocalizer;
        }

        private static WTMContext NewWtm(string seed, bool noTracking = false)
        {
            IDataContext initialDc = noTracking ? new NoTrackingImportTestDataContext(new CS { Value = seed }) : new DataContext(seed, DBTypeEnum.Memory);
            var wtm = MockWtmContext.CreateWtmContext(initialDc, "importuser", dataProtection: ImportUploadRef.Provider);
            Moq.Mock.Get(wtm.HttpContext.Request).Setup(x => x.Headers).Returns(new Microsoft.AspNetCore.Http.HeaderDictionary());
            wtm.ConfigInfo.Connections = new List<CS>
            {
                new CS
                {
                    Key = "default",
                    Value = seed,
                    DbType = DBTypeEnum.Memory,
                    DcConstructor = (noTracking ? typeof(NoTrackingImportTestDataContext) : typeof(ImportTestDataContext)).GetConstructor(new[] { typeof(CS) }),
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

        private static string Upload(WTMContext wtm, byte[] xlsx)
        {
            var provider = new WtmFileProvider(wtm);
            var file = provider.Upload("import.xlsx", xlsx.Length, new MemoryStream(xlsx), dc: wtm.DC);
            return file.GetID().ToString();
        }

        private static TVm NewImportVm<TVm>(WTMContext wtm, byte[] xlsx) where TVm : BaseVM, new()
        {
            var vm = new TVm { Wtm = wtm };
            ImportUploadRef.Apply(wtm, vm, Upload(wtm, xlsx));
            return vm;
        }

        // existing rows: (code, title, ref)
        private static void Seed(string seed, params (string Code, string Title, string Ref)[] rows)
        {
            using var dc = new DataContext(seed, DBTypeEnum.Memory);
            foreach (var r in rows)
            {
                dc.Set<ImportSameRecordDoc>().Add(new ImportSameRecordDoc { Code = r.Code, Title = r.Title, Ref = r.Ref, CreateBy = "seed", CreateTime = new DateTime(2020, 1, 1) });
            }
            dc.SaveChanges();
        }

        private static List<string> Dump(string seed)
        {
            using var dc = new DataContext(seed, DBTypeEnum.Memory);
            return dc.Set<ImportSameRecordDoc>().OrderBy(x => x.Code).ToList().Select(x => $"{x.Code}|{x.Title}|{x.Ref}|{x.UpdateBy}").ToList();
        }

        private static List<ErrorMessage> Errors(BaseVM vm) => ((IBaseImport<BaseTemplateVM>)vm).ErrorListVM.EntityList.ToList();

        private static List<string> Messages(BaseVM vm) => Errors(vm).Select(x => x.Message).ToList();

        private static List<long> ErrorRows(BaseVM vm) => Errors(vm).Select(x => x.Index).ToList();

        private static async Task<bool> Save<TTemplate, TModel>(BaseImportVM<TTemplate, TModel> vm, bool useAsync)
            where TTemplate : BaseTemplateVM, new() where TModel : TopBasePoco, new()
            => useAsync ? await vm.BatchSaveDataAsync() : vm.BatchSaveData();

        private static readonly (string, string, string)[] TwoExisting =
        {
            ("001", "t1", "r1"),
            ("002", "t2", "r2"),
        };

        #endregion

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task TwoGroups_TwoRowsMatchOneRecord_LaterRowIsReportedAndNothingIsSaved(bool useAsync)
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, TwoExisting);
            var before = Dump(seed);
            var wtm = NewWtm(seed);
            var vm = NewImportVm<TwoGroupImportVM>(wtm, FillTemplate(new TwoGroupImportVM().GenerateTemplate(out _),
                new[] { "001", "ta", "ra" },     // row 2: matches 001 by Code
                new[] { "zzz", "t1", "rb" }));   // row 3: matches 001 by Title
            vm.IsOverWriteExistData = true;

            var ok = await Save(vm, useAsync);

            Assert.IsFalse(ok);
            CollectionAssert.AreEqual(new[] { ConflictKey + "|2" }, Messages(vm), "one error, which names the earlier row");
            CollectionAssert.AreEqual(new long[] { 3 }, ErrorRows(vm), "the error is on the later row");
            CollectionAssert.AreEqual(before, Dump(seed), "the database is unchanged, including the first row");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ThreeGroups_ThreeRowsMatchOneRecord_TwoErrors(bool useAsync)
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, TwoExisting);
            var before = Dump(seed);
            var wtm = NewWtm(seed);
            var vm = NewImportVm<ThreeGroupImportVM>(wtm, FillTemplate(new ThreeGroupImportVM().GenerateTemplate(out _),
                new[] { "001", "ta", "ra" },     // row 2: 001 by Code
                new[] { "yyy", "t1", "rb" },     // row 3: 001 by Title
                new[] { "xxx", "tc", "r1" }));   // row 4: 001 by Ref
            vm.IsOverWriteExistData = true;

            var ok = await Save(vm, useAsync);

            Assert.IsFalse(ok);
            CollectionAssert.AreEqual(new[] { ConflictKey + "|2", ConflictKey + "|2" }, Messages(vm), "both later rows name the first row that matched");
            CollectionAssert.AreEqual(new long[] { 3, 4 }, ErrorRows(vm));
            CollectionAssert.AreEqual(before, Dump(seed));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Conflict_WithValidNewRowAndValidOverwriteRow_NothingIsSaved(bool useAsync)
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, TwoExisting);
            var before = Dump(seed);
            var wtm = NewWtm(seed);
            var vm = NewImportVm<TwoGroupImportVM>(wtm, FillTemplate(new TwoGroupImportVM().GenerateTemplate(out _),
                new[] { "001", "ta", "ra" },       // row 2: matches 001 by Code
                new[] { "zzz", "t1", "rb" },       // row 3: conflict (001 by Title)
                new[] { "003", "t3", "rc" },       // row 4: brand new
                new[] { "002", "t2new", "rd" }));  // row 5: valid overwrite of 002
            vm.IsOverWriteExistData = true;

            var ok = await Save(vm, useAsync);

            Assert.IsFalse(ok);
            CollectionAssert.AreEqual(new long[] { 3 }, ErrorRows(vm), "only the conflicting row is reported");
            CollectionAssert.AreEqual(before, Dump(seed), "neither the new row nor the valid overwrite nor the row before the conflict is saved");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task TwoRowsMatchTwoDifferentRecordsThroughDifferentGroups_BothAreUpdated(bool useAsync)
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, TwoExisting);
            var wtm = NewWtm(seed);
            var vm = NewImportVm<TwoGroupImportVM>(wtm, FillTemplate(new TwoGroupImportVM().GenerateTemplate(out _),
                new[] { "001", "ta", "ra" },     // 001 by Code
                new[] { "zzz", "t2", "rb" }));   // 002 by Title
            vm.IsOverWriteExistData = true;

            var ok = await Save(vm, useAsync);

            Assert.IsTrue(ok, string.Join("; ", Messages(vm)));
            Assert.AreEqual(0, Messages(vm).Count);
            CollectionAssert.AreEqual(new[] { "001|ta|ra|importuser", "zzz|t2|rb|importuser" }, Dump(seed));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task SingleGroup_Overwrite_IsUnchanged(bool useAsync)
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, TwoExisting);
            var wtm = NewWtm(seed);
            var vm = NewImportVm<OneGroupImportVM>(wtm, FillTemplate(new OneGroupImportVM().GenerateTemplate(out _),
                new[] { "001", "ta", "ra" },
                new[] { "003", "tb", "rb" }));
            vm.IsOverWriteExistData = true;

            var ok = await Save(vm, useAsync);

            Assert.IsTrue(ok, string.Join("; ", Messages(vm)));
            CollectionAssert.AreEqual(new[] { "001|ta|ra|importuser", "002|t2|r2|", "003|tb|rb|" }, Dump(seed));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task OverwriteOff_TwoRowsMatchingOneRecord_IsUnchanged(bool useAsync)
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, TwoExisting);
            var wtm = NewWtm(seed);
            var vm = NewImportVm<TwoGroupImportVM>(wtm, FillTemplate(new TwoGroupImportVM().GenerateTemplate(out _),
                new[] { "001", "ta", "ra" },
                new[] { "zzz", "t1", "rb" }));
            vm.IsOverWriteExistData = false;

            var ok = await Save(vm, useAsync);

            // with overwrite off every row that matches an existing record is a duplicate error (the existing behaviour); no new message appears
            Assert.IsFalse(ok);
            Assert.IsFalse(Messages(vm).Any(x => x.StartsWith(ConflictKey)), string.Join("; ", Messages(vm)));
            CollectionAssert.AreEqual(new long[] { 2, 3 }, ErrorRows(vm));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task NoTracking_ConflictIsStillDetected(bool useAsync)
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, TwoExisting);
            var before = Dump(seed);
            var wtm = NewWtm(seed, noTracking: true);
            var vm = NewImportVm<TwoGroupImportVM>(wtm, FillTemplate(new TwoGroupImportVM().GenerateTemplate(out _),
                new[] { "001", "ta", "ra" },
                new[] { "zzz", "t1", "rb" }));
            vm.IsOverWriteExistData = true;

            var ok = await Save(vm, useAsync);

            Assert.IsFalse(ok);
            CollectionAssert.AreEqual(new[] { ConflictKey + "|2" }, Messages(vm));
            CollectionAssert.AreEqual(before, Dump(seed));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task NoTracking_DifferentRecords_StillUpdated(bool useAsync)
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, TwoExisting);
            var wtm = NewWtm(seed, noTracking: true);
            var vm = NewImportVm<TwoGroupImportVM>(wtm, FillTemplate(new TwoGroupImportVM().GenerateTemplate(out _),
                new[] { "001", "ta", "ra" },
                new[] { "zzz", "t2", "rb" }));
            vm.IsOverWriteExistData = true;

            var ok = await Save(vm, useAsync);

            Assert.IsTrue(ok, string.Join("; ", Messages(vm)));
            CollectionAssert.AreEqual(new[] { "001|ta|ra|importuser", "zzz|t2|rb|importuser" }, Dump(seed));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task IntKey_ConflictIsDetected_AndDifferentRecordsAreNot(bool useAsync)
        {
            var seed = Guid.NewGuid().ToString();
            using (var dc = new DataContext(seed, DBTypeEnum.Memory))
            {
                dc.Set<ImportSameRecordIntDoc>().Add(new ImportSameRecordIntDoc { ID = 1, Code = "001", Title = "t1" });
                dc.Set<ImportSameRecordIntDoc>().Add(new ImportSameRecordIntDoc { ID = 2, Code = "002", Title = "t2" });
                dc.SaveChanges();
            }

            var conflict = NewImportVm<IntImportVM>(NewWtm(seed, noTracking: true), FillTemplate(new IntImportVM().GenerateTemplate(out _),
                new[] { "001", "ta" }, new[] { "zzz", "t1" }));
            conflict.IsOverWriteExistData = true;
            Assert.IsFalse(await Save(conflict, useAsync));
            CollectionAssert.AreEqual(new[] { ConflictKey + "|2" }, Messages(conflict));

            var fine = NewImportVm<IntImportVM>(NewWtm(seed, noTracking: true), FillTemplate(new IntImportVM().GenerateTemplate(out _),
                new[] { "001", "ta" }, new[] { "zzz", "t2" }));
            fine.IsOverWriteExistData = true;
            Assert.IsTrue(await Save(fine, useAsync), string.Join("; ", Messages(fine)));
            using var check = new DataContext(seed, DBTypeEnum.Memory);
            CollectionAssert.AreEqual(new[] { "001|ta", "zzz|t2" }, check.Set<ImportSameRecordIntDoc>().OrderBy(x => x.ID).ToList().Select(x => $"{x.Code}|{x.Title}").ToList());
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task StringKey_ConflictIsDetected_AndDifferentRecordsAreNot(bool useAsync)
        {
            var seed = Guid.NewGuid().ToString();
            using (var dc = new DataContext(seed, DBTypeEnum.Memory))
            {
                dc.Set<ImportSameRecordStringDoc>().Add(new ImportSameRecordStringDoc { ID = "k-1", Code = "001", Title = "t1" });
                dc.Set<ImportSameRecordStringDoc>().Add(new ImportSameRecordStringDoc { ID = "k-2", Code = "002", Title = "t2" });
                dc.SaveChanges();
            }

            var conflict = NewImportVm<StringImportVM>(NewWtm(seed, noTracking: true), FillTemplate(new StringImportVM().GenerateTemplate(out _),
                new[] { "001", "ta" }, new[] { "zzz", "t1" }));
            conflict.IsOverWriteExistData = true;
            Assert.IsFalse(await Save(conflict, useAsync));
            CollectionAssert.AreEqual(new[] { ConflictKey + "|2" }, Messages(conflict));

            var fine = NewImportVm<StringImportVM>(NewWtm(seed, noTracking: true), FillTemplate(new StringImportVM().GenerateTemplate(out _),
                new[] { "001", "ta" }, new[] { "zzz", "t2" }));
            fine.IsOverWriteExistData = true;
            Assert.IsTrue(await Save(fine, useAsync), string.Join("; ", Messages(fine)));
            using var check = new DataContext(seed, DBTypeEnum.Memory);
            CollectionAssert.AreEqual(new[] { "001|ta", "zzz|t2" }, check.Set<ImportSameRecordStringDoc>().OrderBy(x => x.ID).ToList().Select(x => $"{x.Code}|{x.Title}").ToList());
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ErrorMessage_NeverContainsCellValues(bool useAsync)
        {
            const string secretCode = "CODE-SECRET-A";
            const string secretTitle = "t1";
            var seed = Guid.NewGuid().ToString();
            Seed(seed, ("001", secretTitle, "r1"));
            var wtm = NewWtm(seed);
            var vm = NewImportVm<TwoGroupImportVM>(wtm, FillTemplate(new TwoGroupImportVM().GenerateTemplate(out _),
                new[] { "001", "TITLE-SECRET-B", "REF-SECRET-C" },
                new[] { secretCode, secretTitle, "REF-SECRET-D" }));
            vm.IsOverWriteExistData = true;

            Assert.IsFalse(await Save(vm, useAsync));

            var message = Messages(vm).Single();
            Assert.AreEqual(ConflictKey + "|2", message);
            foreach (var secret in new[] { secretCode, "TITLE-SECRET-B", "REF-SECRET-C", "REF-SECRET-D", "001" })
            {
                Assert.IsFalse(message.Contains(secret), "the message must not contain cell values");
            }
        }

        // The VM keeps its parsed rows (isEntityListSet), so a second call walks the same rows again. If the matched-record state of the
        // first call were kept, every row of the second call would be reported against itself.
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task SecondSaveOnSameVm_HasNoStaleConflict(bool useAsync)
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, TwoExisting);
            var wtm = NewWtm(seed);
            var vm = NewImportVm<TwoGroupImportVM>(wtm, FillTemplate(new TwoGroupImportVM().GenerateTemplate(out _),
                new[] { "001", "ta", "ra" },
                new[] { "zzz", "t2", "rb" }));
            vm.IsOverWriteExistData = true;

            Assert.IsTrue(await Save(vm, useAsync), string.Join("; ", Messages(vm)));
            Assert.IsTrue(await Save(vm, useAsync), "the second call must not report the rows of the first call: " + string.Join("; ", Messages(vm)));

            Assert.AreEqual(0, Messages(vm).Count);
        }

        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public async Task MissingResourceOrNullLocalizer_UsesBuiltInTextWithEarlierRow(bool useAsync, bool nullLocalizer)
        {
            CoreProgram._localizer = nullLocalizer ? null : new MissingKeyLocalizer();
            var seed = Guid.NewGuid().ToString();
            Seed(seed, ("001", "t1", "r1"));
            var vm = NewImportVm<TwoGroupImportVM>(NewWtm(seed), FillTemplate(new TwoGroupImportVM().GenerateTemplate(out _),
                new[] { "001", "SECRET-A", "SECRET-B" },
                new[] { "SECRET-C", "t1", "SECRET-D" }));
            vm.IsOverWriteExistData = true;

            Assert.IsFalse(await Save(vm, useAsync));

            var message = Messages(vm).Single();
            Assert.IsFalse(string.IsNullOrEmpty(message));
            StringAssert.Contains(message, "row 2");
            Assert.IsFalse(message.Contains(ConflictKey), "the bare key name must not be shown");
            Assert.IsFalse(message.Contains("SECRET") || message.Contains("001") || message.Contains("t1"), "no cell values");
            CollectionAssert.AreEqual(new long[] { 3 }, ErrorRows(vm));
        }
    }
}
