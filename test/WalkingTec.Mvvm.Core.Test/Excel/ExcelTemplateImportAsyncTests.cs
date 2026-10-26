using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Excel
{
    /// <summary>
    /// The optional asynchronous import of BaseImportVM (BatchSaveDataAsync, IsDuplicateDataAsync, #382).
    /// The synchronous BatchSaveData is not changed; the async path has to give the same return value, rows, error list and upload file handling,
    /// keep an overridden BatchSaveData, fall back to synchronous enumeration for non-EF queries and let cancellation propagate.
    /// </summary>
    [TestClass]
    public class ExcelTemplateImportAsyncTests
    {
        #region template / import VMs

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

        /// <summary>Rows are unique by SchoolCode (also checked inside the file by SetValidateCheck).</summary>
        private class SchoolDupImportVM : BaseImportVM<SchoolTemplateVM, School>
        {
            public override DuplicatedInfo<School> SetDuplicatedCheck()
            {
                return CreateFieldsInfo(SimpleField(x => x.SchoolCode));
            }

            public School CheckSync(School entity, DuplicatedInfo<School> info) => IsDuplicateData(entity, info);

            public Task<School> CheckAsync(School entity, DuplicatedInfo<School> info, CancellationToken cancellationToken = default) => IsDuplicateDataAsync(entity, info, cancellationToken);

            public static DuplicatedInfo<School> Groups(params DuplicatedField<School>[][] groups)
            {
                var info = new DuplicatedInfo<School>();
                foreach (var g in groups)
                {
                    info.AddGroup(g);
                }
                return info;
            }

            public static DuplicatedField<School> CodeField() => SimpleField(x => x.SchoolCode);

            public static DuplicatedField<School> NameField() => SimpleField(x => x.SchoolName);
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

        /// <summary>Overrides only the synchronous BatchSaveData and records the call.</summary>
        private class SyncOverrideImportVM : SchoolImportVM
        {
            public int SyncCalls { get; private set; }
            public bool SyncResult { get; set; } = true;

            public override bool BatchSaveData()
            {
                SyncCalls++;
                return SyncResult;
            }
        }

        /// <summary>Overrides the synchronous BatchSaveData but still runs the base logic (the typical wrapper override).</summary>
        private class SyncWrapperImportVM : SchoolImportVM
        {
            public int SyncCalls { get; private set; }

            public override bool BatchSaveData()
            {
                SyncCalls++;
                return base.BatchSaveData();
            }
        }

        /// <summary>Overrides only BatchSaveDataAsync.</summary>
        private class AsyncOverrideImportVM : SchoolImportVM
        {
            public int AsyncCalls { get; private set; }

            public override Task<bool> BatchSaveDataAsync(CancellationToken cancellationToken = default)
            {
                AsyncCalls++;
                return Task.FromResult(true);
            }
        }

        /// <summary>Overrides both versions: the async override is what BatchSaveDataAsync runs.</summary>
        private class BothOverrideImportVM : SchoolImportVM
        {
            public int SyncCalls { get; private set; }
            public int AsyncCalls { get; private set; }

            public override bool BatchSaveData()
            {
                SyncCalls++;
                return true;
            }

            public override Task<bool> BatchSaveDataAsync(CancellationToken cancellationToken = default)
            {
                AsyncCalls++;
                return Task.FromResult(false);
            }
        }

        // DataContext variant with the (CS) constructor WTMContext.CreateDC() uses. Private on purpose, so it is not
        // picked up by CS.Cis (which only scans exported types) and cannot affect other tests.
        private sealed class ImportTestDataContext : DataContext
        {
            public ImportTestDataContext(CS cs) : base(cs.Value, cs.DbType ?? DBTypeEnum.Memory)
            {
            }
        }

        /// <summary>Counts how the import saves and can fail the save, so a test sees which path ran and what happens when the database refuses.</summary>
        private sealed class RecordingDataContext : DataContext
        {
            public RecordingDataContext(string seed) : base(seed, DBTypeEnum.Memory)
            {
            }

            public int SyncSaves { get; private set; }
            public int AsyncSaves { get; private set; }
            public List<CancellationToken> Tokens { get; } = new List<CancellationToken>();
            public Func<Exception> SaveFailure { get; set; }

            public override int SaveChanges()
            {
                SyncSaves++;
                if (SaveFailure != null)
                {
                    throw SaveFailure();
                }
                return base.SaveChanges();
            }

            public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            {
                AsyncSaves++;
                Tokens.Add(cancellationToken);
                if (SaveFailure != null)
                {
                    throw SaveFailure();
                }
                return base.SaveChangesAsync(cancellationToken);
            }
        }

        // a tenant model that only exists as a list, to exercise the tenant condition of the duplicate lookup
        public class TenantRow : BasePoco, ITenant
        {
            public string Code { get; set; }
            public string TenantCode { get; set; }
        }

        private class TenantTemplateVM : BaseTemplateVM
        {
        }

        private class TenantImportVM : BaseImportVM<TenantTemplateVM, TenantRow>
        {
            public TenantRow CheckSync(TenantRow entity, DuplicatedInfo<TenantRow> info) => IsDuplicateData(entity, info);

            public Task<TenantRow> CheckAsync(TenantRow entity, DuplicatedInfo<TenantRow> info, CancellationToken cancellationToken = default) => IsDuplicateDataAsync(entity, info, cancellationToken);

            public static DuplicatedInfo<TenantRow> ByCode() => new DuplicatedInfo<TenantRow>().AddGroup(SimpleField(x => x.Code));
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
            // WtmFileProvider keeps its handler registry in static state that the host fills at startup
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

        /// <summary>Appends data rows under the header of the first sheet. A null entry means "blank cell" and the cell is omitted.</summary>
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

        private static byte[] SchoolFile(params string[][] rows) => FillTemplate(new SchoolImportVM().GenerateTemplate(out _), rows);

        private static byte[] MajorFile(params string[][] rows) => FillTemplate(new MajorImportVM().GenerateTemplate(out _), rows);

        private static string Upload(WTMContext wtm, byte[] xlsx)
        {
            var provider = new WtmFileProvider(wtm);
            var file = provider.Upload("import.xlsx", xlsx.Length, new MemoryStream(xlsx), dc: wtm.DC);
            return file.GetID().ToString();
        }

        private static TVm NewImportVm<TVm>(WTMContext wtm, byte[] xlsx) where TVm : BaseVM, new()
        {
            var vm = new TVm { Wtm = wtm };
            vm.GetType().GetProperty("UploadFileId").SetValue(vm, Upload(wtm, xlsx));
            return vm;
        }

        private static School ExistingSchool(string code, string name = "Old name", SchoolTypeEnum type = SchoolTypeEnum.PUB, string remark = "old remark") => new School
        {
            SchoolCode = code,
            SchoolName = name,
            SchoolType = type,
            Remark = remark,
            CreateBy = "seed",
            CreateTime = new DateTime(2020, 1, 1),
        };

        private static void Seed(string seed, params School[] schools)
        {
            using var dc = new DataContext(seed, DBTypeEnum.Memory);
            foreach (var s in schools)
            {
                dc.Set<School>().Add(s);
            }
            dc.SaveChanges();
        }

        /// <summary>What an import left behind, in a form that can be compared between the two paths (ids are random, so they are left out).</summary>
        private sealed class Outcome
        {
            public bool? Result;
            public List<string> Errors;
            public List<string> Rows;
            public bool UploadExists;
        }

        private static List<string> DumpSchools(DataContext dc) => dc.Set<School>().OrderBy(x => x.SchoolCode).ToList()
            .Select(x => $"{x.SchoolCode}|{x.SchoolName}|{x.SchoolType}|{x.Remark}|{x.CreateBy}|{x.UpdateBy}|{x.CreateTime != null}|{x.UpdateTime != null}").ToList();

        private static List<string> DumpMajors(DataContext dc) => dc.Set<Major>().OrderBy(x => x.MajorCode).ToList()
            .Select(x => $"{x.MajorCode}|{x.MajorName}|{x.MajorType}|{x.Remark}|{x.CreateBy}").ToList();

        /// <summary>
        /// Runs one import on a fresh database (optionally pre-filled by <paramref name="seeding"/>) through the synchronous or the asynchronous entry point.
        /// </summary>
        private static async Task<Outcome> Execute<TVm>(bool useAsync, byte[] xlsx, Func<DataContext, List<string>> dump,
            Action<string> seeding = null, Action<TVm> configure = null, Func<string, IDataContext> dataContext = null, CancellationToken cancellationToken = default)
            where TVm : BaseVM, IBaseImport<BaseTemplateVM>, new()
        {
            var seed = Guid.NewGuid().ToString();
            seeding?.Invoke(seed);
            var wtm = NewWtm(seed);
            var vm = NewImportVm<TVm>(wtm, xlsx);
            if (dataContext != null)
            {
                vm.DC = dataContext(seed);
            }
            configure?.Invoke(vm);
            var uploadId = (string)vm.GetType().GetProperty("UploadFileId").GetValue(vm);

            var outcome = new Outcome();
            try
            {
                var save = vm.GetType().GetMethod(useAsync ? "BatchSaveDataAsync" : "BatchSaveData", useAsync ? new[] { typeof(CancellationToken) } : Type.EmptyTypes);
                if (useAsync)
                {
                    outcome.Result = await (Task<bool>)save.Invoke(vm, new object[] { cancellationToken });
                }
                else
                {
                    outcome.Result = (bool)save.Invoke(vm, null);
                }
            }
            catch (System.Reflection.TargetInvocationException e)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            }
            finally
            {
                using var check = new DataContext(seed, DBTypeEnum.Memory);
                outcome.Rows = dump(check);
                outcome.UploadExists = check.Set<FileAttachment>().Any(x => x.ID.ToString() == uploadId);
                outcome.Errors = vm.ErrorListVM.EntityList.Select(e => $"{e.Index}|{e.ExcelIndex}|{e.Message}").ToList();
            }
            return outcome;
        }

        private static async Task<(Outcome Sync, Outcome Async)> ExecuteBoth<TVm>(byte[] xlsx, Func<DataContext, List<string>> dump,
            Action<string> seeding = null, Action<TVm> configure = null, Func<string, IDataContext> dataContext = null)
            where TVm : BaseVM, IBaseImport<BaseTemplateVM>, new()
        {
            var sync = await Execute(false, xlsx, dump, seeding, configure, dataContext);
            var async = await Execute(true, xlsx, dump, seeding, configure, dataContext);
            AssertParity(sync, async);
            return (sync, async);
        }

        private static void AssertParity(Outcome sync, Outcome async)
        {
            Assert.AreEqual(sync.Result, async.Result, "return value");
            CollectionAssert.AreEqual(sync.Errors, async.Errors, "ErrorListVM messages and indexes");
            CollectionAssert.AreEqual(sync.Rows, async.Rows, "persisted rows");
            Assert.AreEqual(sync.UploadExists, async.UploadExists, "upload file deletion");
        }

        #endregion

        #region parity: success

        [TestMethod]
        public async Task BatchSaveDataAsync_ValidRows_PersistsRowsAndRemovesUploadedFile()
        {
            var (sync, async) = await ExecuteBoth<SchoolImportVM>(
                SchoolFile(new[] { "201", "Alpha", "公立学校", "a" }, new[] { "202", "Beta", "私立学校", "b" }),
                DumpSchools);

            Assert.AreEqual(true, async.Result, string.Join("; ", async.Errors));
            CollectionAssert.AreEqual(new[] { "201|Alpha|PUB|a|importuser||True|False", "202|Beta|PRI|b|importuser||True|False" }, async.Rows);
            Assert.IsFalse(async.UploadExists, "uploaded file must be deleted after a successful import");
            Assert.IsFalse(sync.UploadExists);
        }

        [TestMethod]
        public async Task BatchSaveDataAsync_BlankOptionalCell_PersistsCorrectType()
        {
            var (_, async) = await ExecuteBoth<MajorImportVM>(
                MajorFile(new[] { "501", "Law", null, "必修" }, new[] { "502", "Art", "note", "选修" }),
                DumpMajors);

            Assert.AreEqual(true, async.Result, string.Join("; ", async.Errors));
            CollectionAssert.AreEqual(new[] { "501|Law|Required||importuser", "502|Art|Optional|note|importuser" }, async.Rows);
        }

        [TestMethod]
        public async Task BatchSaveDataAsync_NoRows_ReturnsTrueAndSavesNothing()
        {
            var (sync, async) = await ExecuteBoth<SchoolImportVM>(SchoolFile(), DumpSchools);

            Assert.AreEqual(true, async.Result);
            Assert.AreEqual(0, async.Rows.Count);
            Assert.AreEqual(sync.UploadExists, async.UploadExists);
        }

        #endregion

        #region parity: validation errors (nothing saved, upload file kept)

        [TestMethod]
        public async Task BatchSaveDataAsync_InvalidValue_ReportsErrorAndSavesNothing()
        {
            // "9" violates the 3-digit regular expression of School.SchoolCode; the valid row before it must not be saved either
            var (_, async) = await ExecuteBoth<SchoolImportVM>(
                SchoolFile(new[] { "101", "Good", "公立学校", "ok" }, new[] { "9", "Bad", "公立学校", "x" }),
                DumpSchools);

            Assert.AreEqual(false, async.Result);
            Assert.IsTrue(async.Errors.Count > 0);
            Assert.IsTrue(async.Errors.All(e => e.StartsWith("3|3|")), "only the third sheet row is invalid: " + string.Join("; ", async.Errors));
            Assert.AreEqual(0, async.Rows.Count);
            Assert.IsTrue(async.UploadExists, "the uploaded file is kept so the user can fix it");
        }

        [TestMethod]
        public async Task BatchSaveDataAsync_SeveralInvalidRows_ReportsEachRow()
        {
            var (_, async) = await ExecuteBoth<SchoolImportVM>(
                SchoolFile(new[] { "8", "Bad one", "公立学校", "x" }, new[] { "102", "Good", "私立学校", "ok" }, new[] { "7", "Bad two", "公立学校", "x" }),
                DumpSchools);

            Assert.AreEqual(false, async.Result);
            CollectionAssert.AreEquivalent(new[] { 2L, 4L }, async.Errors.Select(e => long.Parse(e.Split('|')[0])).Distinct().ToArray());
            Assert.AreEqual(0, async.Rows.Count);
        }

        [TestMethod]
        public async Task BatchSaveDataAsync_TemplateOfAnotherType_ReportsWholeFileError()
        {
            var (_, async) = await ExecuteBoth<SchoolImportVM>(MajorFile(new[] { "101", "Name", null, "必修" }), DumpSchools);

            Assert.AreEqual(false, async.Result);
            CollectionAssert.AreEqual(new[] { "0|0|Sys.WrongTemplate" }, async.Errors);
            Assert.AreEqual(0, async.Rows.Count);
        }

        [TestMethod]
        public async Task BatchSaveDataAsync_UnknownEnumText_ReportsFormatError()
        {
            var (_, async) = await ExecuteBoth<SchoolImportVM>(SchoolFile(new[] { "301", "Name", "not-a-school-type", "x" }), DumpSchools);

            Assert.AreEqual(false, async.Result);
            Assert.AreEqual(1, async.Errors.Count);
            StringAssert.Contains(async.Errors[0], "ValueNotExist");
            Assert.AreEqual(0, async.Rows.Count);
        }

        [TestMethod]
        public async Task BatchSaveDataAsync_DuplicateInsideTheFile_ReportsErrorFromTheSyncValidation()
        {
            // SetValidateCheck (kept synchronous) compares the rows of the file with each other
            var (_, async) = await ExecuteBoth<SchoolDupImportVM>(
                SchoolFile(new[] { "401", "One", "公立学校", "x" }, new[] { "401", "Two", "公立学校", "y" }),
                DumpSchools);

            Assert.AreEqual(false, async.Result);
            Assert.AreEqual(2, async.Errors.Count, string.Join("; ", async.Errors));
            Assert.IsTrue(async.Errors.All(e => e.Contains("Sys.DuplicateError")));
            Assert.AreEqual(0, async.Rows.Count);
            Assert.IsTrue(async.UploadExists);
        }

        #endregion

        #region parity: rows that already exist in the database

        private static readonly string[][] OverwriteRows =
        {
            new[] { "601", "New name", "私立学校", "new remark" },
            new[] { "602", "Brand new", "公立学校", "fresh" },
        };

        [TestMethod]
        public async Task BatchSaveDataAsync_ExistingRowWithOverwrite_UpdatesTheExistingRowAndAddsTheNewOne()
        {
            var (_, async) = await ExecuteBoth<SchoolDupImportVM>(
                SchoolFile(OverwriteRows),
                DumpSchools,
                seeding: seed => Seed(seed, ExistingSchool("601")),
                configure: vm => vm.IsOverWriteExistData = true);

            Assert.AreEqual(true, async.Result, string.Join("; ", async.Errors));
            // 601 keeps its creator, gets the imported values and UpdateBy/UpdateTime; no second 601 row is added
            CollectionAssert.AreEqual(new[]
            {
                "601|New name|PRI|new remark|seed|importuser|True|True",
                "602|Brand new|PUB|fresh|importuser||True|False",
            }, async.Rows);
            Assert.IsFalse(async.UploadExists);
        }

        [TestMethod]
        public async Task BatchSaveDataAsync_ExistingRowWithoutOverwrite_ReportsDuplicateAndSavesNothing()
        {
            var (_, async) = await ExecuteBoth<SchoolDupImportVM>(
                SchoolFile(OverwriteRows),
                DumpSchools,
                seeding: seed => Seed(seed, ExistingSchool("601")),
                configure: vm => vm.IsOverWriteExistData = false);

            Assert.AreEqual(false, async.Result);
            Assert.AreEqual(1, async.Errors.Count, string.Join("; ", async.Errors));
            StringAssert.StartsWith(async.Errors[0], "2|", "the error points at Excel row 2");
            StringAssert.Contains(async.Errors[0], "Sys.DuplicateError");
            CollectionAssert.AreEqual(new[] { "601|Old name|PUB|old remark|seed||True|False" }, async.Rows, "nothing is saved, not even the new row");
            Assert.IsTrue(async.UploadExists);
        }

        [TestMethod]
        public async Task BatchSaveDataAsync_ExistingRowWithoutOverwrite_NewRowsStillImport()
        {
            var (_, async) = await ExecuteBoth<SchoolDupImportVM>(
                SchoolFile(new[] { "701", "Fresh one", "公立学校", "a" }, new[] { "702", "Fresh two", "私立学校", "b" }),
                DumpSchools,
                seeding: seed => Seed(seed, ExistingSchool("601")),
                configure: vm => vm.IsOverWriteExistData = false);

            Assert.AreEqual(true, async.Result, string.Join("; ", async.Errors));
            Assert.AreEqual(3, async.Rows.Count);
        }

        #endregion

        #region parity: the database refuses the save

        private static IEnumerable<object[]> SaveFailures()
        {
            yield return new object[] { "generic", (Func<Exception>)(() => new InvalidOperationException("save failed")) };
            yield return new object[] { "dbupdate", (Func<Exception>)(() => new DbUpdateException("update failed")) };
        }

        [TestMethod]
        [DynamicData(nameof(SaveFailures), DynamicDataSourceType.Method)]
        public async Task BatchSaveDataAsync_SaveChangesThrows_ReturnsFalseWithErrorMessage(string kind, Func<Exception> failure)
        {
            var (sync, async) = await ExecuteBoth<SchoolImportVM>(
                SchoolFile(new[] { "801", "Alpha", "公立学校", "a" }),
                DumpSchools,
                dataContext: seed => new RecordingDataContext(seed) { SaveFailure = failure });

            Assert.AreEqual(false, async.Result, kind);
            Assert.AreEqual(1, async.Errors.Count, string.Join("; ", async.Errors));
            StringAssert.Contains(async.Errors[0], failure().Message);
            Assert.AreEqual(0, async.Rows.Count);
            Assert.IsTrue(async.UploadExists, "a failed import keeps the uploaded file");
            Assert.AreEqual(sync.Errors[0], async.Errors[0]);
        }

        [TestMethod]
        public async Task BatchSaveDataAsync_SaveChangesThrowsWithoutCancellation_OperationCanceledIsAnImportError()
        {
            // an OperationCanceledException the caller did not ask for (for example a database timeout) is still reported like any other save error
            var outcome = await Execute<SchoolImportVM>(true,
                SchoolFile(new[] { "811", "Alpha", "公立学校", "a" }),
                DumpSchools,
                dataContext: seed => new RecordingDataContext(seed) { SaveFailure = () => new OperationCanceledException("timed out") });

            Assert.AreEqual(false, outcome.Result);
            Assert.AreEqual(1, outcome.Errors.Count);
            StringAssert.Contains(outcome.Errors[0], "timed out");
        }

        #endregion

        #region override preservation

        [TestMethod]
        public async Task BatchSaveDataAsync_OnlyBatchSaveDataOverridden_CallsTheOverrideAndReturnsItsResult()
        {
            var seed = Guid.NewGuid().ToString();
            var dc = new RecordingDataContext(seed);
            var vm = NewImportVm<SyncOverrideImportVM>(NewWtm(seed), SchoolFile(new[] { "901", "Alpha", "公立学校", "a" }));
            vm.DC = dc;

            vm.SyncResult = true;
            Assert.IsTrue(await vm.BatchSaveDataAsync());
            vm.SyncResult = false;
            Assert.IsFalse(await vm.BatchSaveDataAsync());

            Assert.AreEqual(2, vm.SyncCalls);
            Assert.AreEqual(0, dc.AsyncSaves + dc.SyncSaves, "the override replaces the base logic, nothing is saved");
            using var check = new DataContext(seed, DBTypeEnum.Memory);
            Assert.AreEqual(0, check.Set<School>().Count());
        }

        [TestMethod]
        public async Task BatchSaveDataAsync_OverrideCallsBase_RunsTheFullSynchronousLogic()
        {
            var sync = await Execute<SyncWrapperImportVM>(false, SchoolFile(new[] { "902", "Alpha", "公立学校", "a" }), DumpSchools);
            var async = await Execute<SyncWrapperImportVM>(true, SchoolFile(new[] { "902", "Alpha", "公立学校", "a" }), DumpSchools);

            Assert.AreEqual(true, async.Result);
            AssertParity(sync, async);
            Assert.AreEqual(1, async.Rows.Count);
        }

        [TestMethod]
        public async Task BatchSaveDataAsync_OverriddenAsync_RunsTheAsyncOverrideOnly()
        {
            var vm = new AsyncOverrideImportVM { Wtm = NewWtm(Guid.NewGuid().ToString()) };

            Assert.IsTrue(await vm.BatchSaveDataAsync());
            Assert.AreEqual(1, vm.AsyncCalls);
        }

        [TestMethod]
        public async Task BatchSaveDataAsync_BothOverridden_RunsTheAsyncOverride()
        {
            var vm = new BothOverrideImportVM { Wtm = NewWtm(Guid.NewGuid().ToString()) };

            Assert.IsFalse(await vm.BatchSaveDataAsync());
            Assert.AreEqual(1, vm.AsyncCalls);
            Assert.AreEqual(0, vm.SyncCalls);
            Assert.IsTrue(vm.BatchSaveData(), "the synchronous override is untouched");
        }

        [TestMethod]
        public async Task BatchSaveDataAsync_NothingOverridden_SavesThroughSaveChangesAsyncWithTheToken()
        {
            var seed = Guid.NewGuid().ToString();
            var dc = new RecordingDataContext(seed);
            var vm = NewImportVm<SchoolImportVM>(NewWtm(seed), SchoolFile(new[] { "903", "Alpha", "公立学校", "a" }));
            vm.DC = dc;
            using var cts = new CancellationTokenSource();

            Assert.IsTrue(await vm.BatchSaveDataAsync(cts.Token));

            Assert.AreEqual(0, dc.SyncSaves, "no synchronous SaveChanges");
            Assert.AreEqual(1, dc.AsyncSaves);
            Assert.AreEqual(cts.Token, dc.Tokens.Single());
            using var check = new DataContext(seed, DBTypeEnum.Memory);
            Assert.AreEqual(1, check.Set<School>().Count());
        }

        [TestMethod]
        public void BatchSaveData_NothingOverridden_StillUsesSynchronousSaveChanges()
        {
            var seed = Guid.NewGuid().ToString();
            var dc = new RecordingDataContext(seed);
            var vm = NewImportVm<SchoolImportVM>(NewWtm(seed), SchoolFile(new[] { "904", "Alpha", "公立学校", "a" }));
            vm.DC = dc;

            Assert.IsTrue(vm.BatchSaveData());

            Assert.AreEqual(1, dc.SyncSaves);
            Assert.AreEqual(0, dc.AsyncSaves);
        }

        #endregion

        #region cancellation

        [TestMethod]
        public async Task BatchSaveDataAsync_CancelledBeforeStart_ThrowsAndNothingHappens()
        {
            var seed = Guid.NewGuid().ToString();
            var dc = new RecordingDataContext(seed);
            var vm = NewImportVm<SchoolImportVM>(NewWtm(seed), SchoolFile(new[] { "911", "Alpha", "公立学校", "a" }));
            vm.DC = dc;
            var uploadId = vm.UploadFileId;
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => vm.BatchSaveDataAsync(cts.Token));

            Assert.AreEqual(0, dc.AsyncSaves + dc.SyncSaves);
            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count, "cancellation is not an import error");
            using var check = new DataContext(seed, DBTypeEnum.Memory);
            Assert.AreEqual(0, check.Set<School>().Count());
            Assert.IsTrue(check.Set<FileAttachment>().Any(x => x.ID.ToString() == uploadId), "the upload file is not deleted");
        }

        [TestMethod]
        public async Task BatchSaveDataAsync_CancelledDuringSave_PropagatesAndKeepsTheUploadFile()
        {
            using var cts = new CancellationTokenSource();
            var seed = Guid.NewGuid().ToString();
            var dc = new RecordingDataContext(seed) { SaveFailure = () => { cts.Cancel(); return new OperationCanceledException(cts.Token); } };
            var vm = NewImportVm<SchoolImportVM>(NewWtm(seed), SchoolFile(new[] { "912", "Alpha", "公立学校", "a" }));
            vm.DC = dc;
            var uploadId = vm.UploadFileId;

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => vm.BatchSaveDataAsync(cts.Token));

            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count, "cancellation is not an import error");
            using var check = new DataContext(seed, DBTypeEnum.Memory);
            Assert.AreEqual(0, check.Set<School>().Count());
            Assert.IsTrue(check.Set<FileAttachment>().Any(x => x.ID.ToString() == uploadId));
        }

        #endregion

        #region IsDuplicateDataAsync

        private static SchoolDupImportVM NewDupVm(string seed)
        {
            return new SchoolDupImportVM { Wtm = NewWtm(seed) };
        }

        [TestMethod]
        public async Task IsDuplicateDataAsync_ReturnsTheSameEntityAsTheSyncVersion()
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, ExistingSchool("101", "Existing"), ExistingSchool("102", "Other"));
            var vm = NewDupVm(seed);
            var info = SchoolDupImportVM.Groups(new[] { SchoolDupImportVM.CodeField() });

            var matchSync = vm.CheckSync(new School { SchoolCode = "101" }, info);
            var matchAsync = await vm.CheckAsync(new School { SchoolCode = "101" }, info);
            var noneSync = vm.CheckSync(new School { SchoolCode = "199" }, info);
            var noneAsync = await vm.CheckAsync(new School { SchoolCode = "199" }, info);

            Assert.IsNotNull(matchSync);
            Assert.AreEqual("Existing", matchSync.SchoolName);
            Assert.AreEqual(matchSync.ID, matchAsync.ID);
            Assert.IsNull(noneSync);
            Assert.IsNull(noneAsync);
        }

        [TestMethod]
        public async Task IsDuplicateDataAsync_CancelledToken_Throws()
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, ExistingSchool("920"));
            var vm = NewDupVm(seed);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => vm.CheckAsync(new School { SchoolCode = "920" }, SchoolDupImportVM.Groups(new[] { SchoolDupImportVM.CodeField() }), cts.Token));
            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count);
        }

        [TestMethod]
        public async Task IsDuplicateDataAsync_NoConditions_ReturnsNull()
        {
            var vm = NewDupVm(Guid.NewGuid().ToString());

            Assert.IsNull(vm.CheckSync(new School { SchoolCode = "101" }, null));
            Assert.IsNull(await vm.CheckAsync(new School { SchoolCode = "101" }, null));
            Assert.IsNull(await vm.CheckAsync(new School { SchoolCode = "101" }, new DuplicatedInfo<School>()));
        }

        [TestMethod]
        public async Task IsDuplicateDataAsync_SecondGroupMatches_ReportsTheGroupFieldsLikeTheSyncVersion()
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, ExistingSchool("101", "Existing"));
            // group 1 (code) finds nothing, group 2 (name) finds the row; without overwrite the message names the matching group
            var info = SchoolDupImportVM.Groups(new[] { SchoolDupImportVM.CodeField() }, new[] { SchoolDupImportVM.NameField() });
            var syncVm = NewDupVm(seed);
            syncVm.IsOverWriteExistData = false;
            var asyncVm = NewDupVm(seed);
            asyncVm.IsOverWriteExistData = false;
            var entity = new Func<School>(() => new School { SchoolCode = "150", SchoolName = "Existing", ExcelIndex = 5 });

            var sync = syncVm.CheckSync(entity(), info);
            var async = await asyncVm.CheckAsync(entity(), info);

            Assert.AreEqual(sync.ID, async.ID);
            CollectionAssert.AreEqual(
                syncVm.ErrorListVM.EntityList.Select(e => $"{e.Index}|{e.Message}").ToList(),
                asyncVm.ErrorListVM.EntityList.Select(e => $"{e.Index}|{e.Message}").ToList());
            Assert.AreEqual(1, asyncVm.ErrorListVM.EntityList.Count);
            Assert.AreEqual(5, asyncVm.ErrorListVM.EntityList[0].Index);
        }

        [TestMethod]
        public async Task IsDuplicateDataAsync_OverwriteOn_ReportsNoError()
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, ExistingSchool("101"));
            var vm = NewDupVm(seed);
            vm.IsOverWriteExistData = true;

            var found = await vm.CheckAsync(new School { SchoolCode = "101" }, SchoolDupImportVM.Groups(new[] { SchoolDupImportVM.CodeField() }));

            Assert.IsNotNull(found);
            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count);
        }

        // a DbSet whose queries come from the given source (the usual way to put a list or a spied query behind DC.Set<T>())
        private static Mock<IDataContext> DcWithSet<T>(IQueryable<T> source) where T : TopBasePoco
        {
            var set = new Mock<DbSet<T>>();
            set.Setup(m => m.AsQueryable()).Returns(source);
            var queryable = set.As<IQueryable<T>>();
            queryable.Setup(m => m.Provider).Returns(source.Provider);
            queryable.Setup(m => m.Expression).Returns(source.Expression);
            queryable.Setup(m => m.ElementType).Returns(source.ElementType);
            queryable.Setup(m => m.GetEnumerator()).Returns(() => source.GetEnumerator());
            var dc = new Mock<IDataContext>();
            dc.Setup(x => x.Set<T>()).Returns(set.Object);
            return dc;
        }

        [TestMethod]
        public async Task IsDuplicateDataAsync_NonEfProvider_FallsBackToSynchronousEnumeration()
        {
            var list = new List<School> { ExistingSchool("101", "From list"), ExistingSchool("102") };
            var vm = NewDupVm(Guid.NewGuid().ToString());
            vm.DC = DcWithSet(list.AsQueryable()).Object;
            var info = SchoolDupImportVM.Groups(new[] { SchoolDupImportVM.CodeField() });

            var sync = vm.CheckSync(new School { SchoolCode = "101" }, info);
            var async = await vm.CheckAsync(new School { SchoolCode = "101" }, info);
            var none = await vm.CheckAsync(new School { SchoolCode = "199" }, info);

            Assert.AreSame(list[0], sync);
            Assert.AreSame(list[0], async, "no InvalidOperationException from the async EF operators");
            Assert.IsNull(none);
        }

        [TestMethod]
        public async Task IsDuplicateDataAsync_TenantCondition_MatchesTheSyncVersion()
        {
            var rows = new List<TenantRow>
            {
                new TenantRow { Code = "A", TenantCode = "t1" },
                new TenantRow { Code = "A", TenantCode = "t2" },
            };
            var wtm = NewWtm(Guid.NewGuid().ToString());
            wtm.ConfigInfo.EnableTenant = true;
            wtm.LoginUserInfo = new LoginUserInfo { ITCode = "importuser", CurrentTenant = "t2" };
            var vm = new TenantImportVM { Wtm = wtm };
            vm.DC = DcWithSet(rows.AsQueryable()).Object;

            var sync = vm.CheckSync(new TenantRow { Code = "A" }, TenantImportVM.ByCode());
            var async = await vm.CheckAsync(new TenantRow { Code = "A" }, TenantImportVM.ByCode());

            Assert.AreSame(rows[1], sync, "only the row of the current tenant matches");
            Assert.AreSame(rows[1], async);

            wtm.LoginUserInfo = new LoginUserInfo { ITCode = "importuser", CurrentTenant = "t3" };
            Assert.IsNull(vm.CheckSync(new TenantRow { Code = "A" }, TenantImportVM.ByCode()));
            Assert.IsNull(await vm.CheckAsync(new TenantRow { Code = "A" }, TenantImportVM.ByCode()));
        }

        // ---- a provider that counts how the duplicate lookup runs (the same idea as the spy of the async list search test) ----

        private class Spy
        {
            public int SyncEnumerations;
            public int AsyncEnumerations;
            public List<CancellationToken> Tokens = new List<CancellationToken>();
        }

        private class SpyProvider : IAsyncQueryProvider
        {
            private readonly IQueryProvider _inner;
            private readonly Spy _spy;

            public SpyProvider(IQueryProvider inner, Spy spy)
            {
                _inner = inner;
                _spy = spy;
            }

            public IQueryable CreateQuery(Expression expression) => throw new NotSupportedException();

            public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new SpyQuery<TElement>(_inner.CreateQuery<TElement>(expression), _spy);

            public object Execute(Expression expression) => _inner.Execute(expression);

            public TResult Execute<TResult>(Expression expression) => _inner.Execute<TResult>(expression);

            public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default) => ((IAsyncQueryProvider)_inner).ExecuteAsync<TResult>(expression, cancellationToken);
        }

        private class SpyQuery<T> : IOrderedQueryable<T>, IAsyncEnumerable<T>
        {
            private readonly IQueryable<T> _inner;
            private readonly Spy _spy;

            public SpyQuery(IQueryable<T> inner, Spy spy)
            {
                _inner = inner;
                _spy = spy;
                Provider = new SpyProvider(inner.Provider, spy);
            }

            public Type ElementType => _inner.ElementType;
            public Expression Expression => _inner.Expression;
            public IQueryProvider Provider { get; }

            public IEnumerator<T> GetEnumerator()
            {
                _spy.SyncEnumerations++;
                return _inner.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                _spy.AsyncEnumerations++;
                _spy.Tokens.Add(cancellationToken);
                return ((IAsyncEnumerable<T>)_inner).GetAsyncEnumerator(cancellationToken);
            }
        }

        [TestMethod]
        public async Task IsDuplicateDataAsync_AsyncQueryProvider_EnumeratesAsynchronouslyWithTheToken()
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, ExistingSchool("101", "Existing"));
            var spy = new Spy();
            using var realDc = new DataContext(seed, DBTypeEnum.Memory);
            var vm = NewDupVm(seed);
            vm.DC = DcWithSet<School>(new SpyQuery<School>(realDc.Set<School>(), spy)).Object;
            using var cts = new CancellationTokenSource();

            var found = await vm.CheckAsync(new School { SchoolCode = "101" }, SchoolDupImportVM.Groups(new[] { SchoolDupImportVM.CodeField() }), cts.Token);

            Assert.IsNotNull(found);
            Assert.AreEqual(1, spy.AsyncEnumerations);
            Assert.AreEqual(0, spy.SyncEnumerations);
            Assert.AreEqual(cts.Token, spy.Tokens.Single());

            // the synchronous version of the same lookup still enumerates synchronously
            vm.CheckSync(new School { SchoolCode = "101" }, SchoolDupImportVM.Groups(new[] { SchoolDupImportVM.CodeField() }));
            Assert.AreEqual(1, spy.SyncEnumerations);
            Assert.AreEqual(1, spy.AsyncEnumerations);
        }

        #endregion
    }
}
