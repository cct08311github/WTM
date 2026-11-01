using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Excel
{
    /// <summary>
    /// #396: the overwrite path of BaseImportVM used to wrap DC.UpdateProperty in an empty catch. Properties that are not mapped
    /// ([NotMapped], lists, navigations) are now skipped explicitly; any other failure is still ignored (the import outcome is unchanged)
    /// but is written once per model+property to the ILogger of the WTMContext.
    /// </summary>
    [TestClass]
    public class ExcelImportOverwriteUpdatePropertyTests
    {
        #region template / import VMs

        private class DocMemoTemplateVM : BaseTemplateVM
        {
            public ExcelPropety Code = ExcelPropety.CreateProperty<ImportOverwriteDoc>(x => x.Code);
            public ExcelPropety Title = ExcelPropety.CreateProperty<ImportOverwriteDoc>(x => x.Title);
            public ExcelPropety Memo = ExcelPropety.CreateProperty<ImportOverwriteDoc>(x => x.Memo);
        }

        private class DocMemoImportVM : BaseImportVM<DocMemoTemplateVM, ImportOverwriteDoc>
        {
            public override DuplicatedInfo<ImportOverwriteDoc> SetDuplicatedCheck() => CreateFieldsInfo(SimpleField(x => x.Code));
        }

        private class DocScalarTemplateVM : BaseTemplateVM
        {
            public ExcelPropety Code = ExcelPropety.CreateProperty<ImportOverwriteDoc>(x => x.Code);
            public ExcelPropety Title = ExcelPropety.CreateProperty<ImportOverwriteDoc>(x => x.Title);
        }

        private class DocScalarImportVM : BaseImportVM<DocScalarTemplateVM, ImportOverwriteDoc>
        {
            public override DuplicatedInfo<ImportOverwriteDoc> SetDuplicatedCheck() => CreateFieldsInfo(SimpleField(x => x.Code));
        }

        // FluentIgnored is excluded from the model by the fluent configuration of the test DataContext only
        private class DocIgnoredTemplateVM : BaseTemplateVM
        {
            public ExcelPropety Code = ExcelPropety.CreateProperty<ImportOverwriteDoc>(x => x.Code);
            public ExcelPropety Title = ExcelPropety.CreateProperty<ImportOverwriteDoc>(x => x.Title);
            public ExcelPropety FluentIgnored = ExcelPropety.CreateProperty<ImportOverwriteDoc>(x => x.FluentIgnored);
        }

        private class DocTagsTemplateVM : BaseTemplateVM
        {
            // Tags comes first: with a detached row the first UpdateProperty call attaches it, and a value assigned before that is not seen as a change
            public ExcelPropety Tags = ExcelPropety.CreateProperty<ImportOverwriteDoc>(x => x.Tags);
            public ExcelPropety Code = ExcelPropety.CreateProperty<ImportOverwriteDoc>(x => x.Code);
        }

        private class DocTagsImportVM : BaseImportVM<DocTagsTemplateVM, ImportOverwriteDoc>
        {
            public override DuplicatedInfo<ImportOverwriteDoc> SetDuplicatedCheck() => CreateFieldsInfo(SimpleField(x => x.Code));
        }

        private class DocIgnoredTagsTemplateVM : BaseTemplateVM
        {
            public ExcelPropety Code = ExcelPropety.CreateProperty<ImportOverwriteDoc>(x => x.Code);
            public ExcelPropety FluentIgnoredTags = ExcelPropety.CreateProperty<ImportOverwriteDoc>(x => x.FluentIgnoredTags);
        }

        private class DocIgnoredTagsImportVM : BaseImportVM<DocIgnoredTagsTemplateVM, ImportOverwriteDoc>
        {
            public override DuplicatedInfo<ImportOverwriteDoc> SetDuplicatedCheck() => CreateFieldsInfo(SimpleField(x => x.Code));
        }

        private class DocIgnoredImportVM : BaseImportVM<DocIgnoredTemplateVM, ImportOverwriteDoc>
        {
            public override DuplicatedInfo<ImportOverwriteDoc> SetDuplicatedCheck() => CreateFieldsInfo(SimpleField(x => x.Code));
        }

        // DataContext variant with the (CS) constructor WTMContext.CreateDC() uses. Private on purpose, so it is not
        // picked up by CS.Cis (which only scans exported types) and cannot affect other tests.
        private sealed class ImportTestDataContext : DataContext
        {
            public ImportTestDataContext(CS cs) : base(cs.Value, DBTypeEnum.Memory)
            {
            }
        }

        // global NoTracking, like a site that configures it on its DataContext: rows found by the duplicate check are detached
        private sealed class NoTrackingImportTestDataContext : DataContext
        {
            public NoTrackingImportTestDataContext(CS cs) : base(cs.Value, DBTypeEnum.Memory)
            {
                ChangeTracker.QueryTrackingBehavior = Microsoft.EntityFrameworkCore.QueryTrackingBehavior.NoTracking;
            }
        }

        /// <summary>Collects every log entry so a test sees exactly what the import wrote.</summary>
        private sealed class CapturingLoggerProvider : ILoggerProvider
        {
            public List<(LogLevel Level, string Category, string Message)> Entries { get; } = new List<(LogLevel, string, string)>();
            public List<Exception> Exceptions { get; } = new List<Exception>();

            public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

            public void Dispose()
            {
            }

            private sealed class CapturingLogger : ILogger
            {
                private readonly CapturingLoggerProvider _owner;
                private readonly string _category;

                public CapturingLogger(CapturingLoggerProvider owner, string category)
                {
                    _owner = owner;
                    _category = category;
                }

                public IDisposable BeginScope<TState>(TState state) => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                {
                    lock (_owner.Entries)
                    {
                        _owner.Entries.Add((logLevel, _category, formatter(state, exception)));
                        if (exception != null)
                        {
                            _owner.Exceptions.Add(exception);
                        }
                    }
                }
            }
        }

        #endregion

        #region helpers

        private Microsoft.Extensions.Localization.IStringLocalizer _previousLocalizer;

        [TestInitialize]
        public void InstallLocalizer()
        {
            _previousLocalizer = CoreProgram._localizer;
            CoreProgram._localizer = new TagHelpers.LayUI.KeyEchoLocalizer();
            WtmFileProvider.Init(new Configs(), new GlobalData { AllAssembly = new List<System.Reflection.Assembly> { typeof(WtmFileProvider).Assembly } });
        }

        [TestCleanup]
        public void RestoreLocalizer()
        {
            CoreProgram._localizer = _previousLocalizer;
        }

        private static WTMContext NewWtm(string seed, ILoggerFactory loggerFactory, bool noTracking = false)
        {
            IDataContext initialDc = noTracking ? new NoTrackingImportTestDataContext(new CS { Value = seed }) : new DataContext(seed, DBTypeEnum.Memory);
            var wtm = MockWtmContext.CreateWtmContext(initialDc, "importuser", loggerFactory: loggerFactory);
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
            vm.GetType().GetProperty("UploadFileId").SetValue(vm, Upload(wtm, xlsx));
            return vm;
        }

        private static void Seed(string seed, params string[] codes)
        {
            using var dc = new DataContext(seed, DBTypeEnum.Memory);
            foreach (var code in codes)
            {
                dc.Set<ImportOverwriteDoc>().Add(new ImportOverwriteDoc { Code = code, Title = "old title", Tags = new List<string> { "x", "y" }, CreateBy = "seed", CreateTime = new DateTime(2020, 1, 1) });
            }
            dc.SaveChanges();
        }

        private static List<string> Dump(string seed)
        {
            using var dc = new DataContext(seed, DBTypeEnum.Memory);
            return dc.Set<ImportOverwriteDoc>().OrderBy(x => x.Code).ToList().Select(x => $"{x.Code}|{x.Title}|{x.CreateBy}|{x.UpdateBy}").ToList();
        }

        /// <summary>Runs the synchronous or the asynchronous save of the same import.</summary>
        private static async Task<bool> Save<TTemplate>(BaseImportVM<TTemplate, ImportOverwriteDoc> vm, bool useAsync) where TTemplate : BaseTemplateVM, new()
            => useAsync ? await vm.BatchSaveDataAsync() : vm.BatchSaveData();

        private static List<string> Errors<TTemplate>(BaseImportVM<TTemplate, ImportOverwriteDoc> vm) where TTemplate : BaseTemplateVM, new()
            => vm.ErrorListVM.EntityList.Select(x => x.Message).ToList();

        private static List<(LogLevel Level, string Category, string Message)> Warnings(CapturingLoggerProvider logs)
        {
            lock (logs.Entries)
            {
                return logs.Entries.Where(x => x.Level >= LogLevel.Warning).ToList();
            }
        }

        private static ILoggerFactory NewLoggerFactory(CapturingLoggerProvider provider) => LoggerFactory.Create(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));

        #endregion

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Overwrite_NotMappedProperty_IsSkippedWithoutWarning(bool useAsync)
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, "001", "002");
            var logs = new CapturingLoggerProvider();
            var wtm = NewWtm(seed, NewLoggerFactory(logs));
            var template = new DocMemoImportVM().GenerateTemplate(out _);
            var vm = NewImportVm<DocMemoImportVM>(wtm, FillTemplate(template,
                new[] { "001", "new one", "memo one" },
                new[] { "002", "new two", "memo two" }));
            vm.IsOverWriteExistData = true;

            var ok = await Save(vm, useAsync);

            Assert.IsTrue(ok, string.Join("; ", Errors(vm)));
            Assert.AreEqual(0, Errors(vm).Count);
            CollectionAssert.AreEqual(new[] { "001|new one|seed|importuser", "002|new two|seed|importuser" }, Dump(seed));
            Assert.AreEqual(0, Warnings(logs).Count, "a [NotMapped] property is skipped explicitly, nothing is thrown and nothing is logged");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Overwrite_MappedScalar_IsUpdated(bool useAsync)
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, "001");
            var logs = new CapturingLoggerProvider();
            var wtm = NewWtm(seed, NewLoggerFactory(logs));
            var vm = NewImportVm<DocScalarImportVM>(wtm, FillTemplate(new DocScalarImportVM().GenerateTemplate(out _),
                new[] { "001", "changed" },
                new[] { "003", "brand new" }));
            vm.IsOverWriteExistData = true;

            var ok = await Save(vm, useAsync);

            Assert.IsTrue(ok, string.Join("; ", Errors(vm)));
            CollectionAssert.AreEqual(new[] { "001|changed|seed|importuser", "003|brand new|importuser|" }, Dump(seed));
            Assert.AreEqual(0, Warnings(logs).Count);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Overwrite_UpdatePropertyThrows_ImportStillSucceedsAndWarnsOncePerProperty(bool useAsync)
        {
            const string secret1 = "CELL-VALUE-ONE";
            const string secret2 = "CELL-VALUE-TWO";
            var seed = Guid.NewGuid().ToString();
            Seed(seed, "001", "002");
            var logs = new CapturingLoggerProvider();
            var wtm = NewWtm(seed, NewLoggerFactory(logs));
            var vm = NewImportVm<DocIgnoredImportVM>(wtm, FillTemplate(new DocIgnoredImportVM().GenerateTemplate(out _),
                new[] { "001", "new one", secret1 },
                new[] { "002", "new two", secret2 }));
            vm.IsOverWriteExistData = true;

            var ok = await Save(vm, useAsync);

            // same outcome as before the change: success, no error entries, the mapped columns are updated
            Assert.IsTrue(ok, string.Join("; ", Errors(vm)));
            Assert.AreEqual(0, Errors(vm).Count);
            CollectionAssert.AreEqual(new[] { "001|new one|seed|importuser", "002|new two|seed|importuser" }, Dump(seed));
            // two rows fail for the same property, one warning
            var warnings = Warnings(logs);
            Assert.AreEqual(1, warnings.Count, string.Join(" || ", warnings.Select(x => x.Message)));
            Assert.AreEqual(LogLevel.Warning, warnings[0].Level);
            StringAssert.Contains(warnings[0].Message, "FluentIgnored");
            StringAssert.Contains(warnings[0].Message, nameof(ImportOverwriteDoc));
            StringAssert.Contains(warnings[0].Message, "InvalidOperationException");
            Assert.IsFalse(warnings[0].Message.Contains(secret1) || warnings[0].Message.Contains(secret2), "cell values must never be logged");
            // the exception text is not logged either (a custom IDataContext could put anything into it), and the exception object is not passed on
            Assert.IsFalse(warnings[0].Message.Contains("could not be found"), "the exception message must not be logged");
            Assert.AreEqual(0, logs.Exceptions.Count, "the exception object must not be handed to the logger");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Overwrite_UpdatePropertyThrows_AnotherImportWarnsAgain(bool useAsync)
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, "001");
            var logs = new CapturingLoggerProvider();
            for (int i = 0; i < 2; i++)
            {
                var wtm = NewWtm(seed, NewLoggerFactory(logs));
                var vm = NewImportVm<DocIgnoredImportVM>(wtm, FillTemplate(new DocIgnoredImportVM().GenerateTemplate(out _), new[] { "001", "round " + i, "x" }));
                vm.IsOverWriteExistData = true;
                Assert.IsTrue(await Save(vm, useAsync), string.Join("; ", Errors(vm)));
            }

            Assert.AreEqual(2, Warnings(logs).Count, "the throttle is per import, not per process");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Overwrite_UpdatePropertyThrows_WithoutLoggerFactory_ImportStillSucceeds(bool useAsync)
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, "001");
            var wtm = NewWtm(seed, null);
            Assert.IsNull(wtm.LoggerFactory);
            var vm = NewImportVm<DocIgnoredImportVM>(wtm, FillTemplate(new DocIgnoredImportVM().GenerateTemplate(out _), new[] { "001", "no logger", "x" }));
            vm.IsOverWriteExistData = true;

            var ok = await Save(vm, useAsync);

            Assert.IsTrue(ok, string.Join("; ", Errors(vm)));
            CollectionAssert.AreEqual(new[] { "001|no logger|seed|importuser" }, Dump(seed));
        }

        // A mapped primitive collection (List<string>) is not a navigation: it keeps getting UpdateProperty. This matters when the
        // existing row is not tracked (global NoTracking): then the explicit IsModified is the only thing that makes EF write the new value.
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Overwrite_MappedPrimitiveCollection_StillGetsUpdatePropertyWhenRowIsNotTracked(bool useAsync)
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, "001");
            var logs = new CapturingLoggerProvider();
            var wtm = NewWtm(seed, NewLoggerFactory(logs), noTracking: true);
            var vm = NewImportVm<DocTagsImportVM>(wtm, FillTemplate(new DocTagsImportVM().GenerateTemplate(out _), new[] { "a", "001" }));
            vm.IsOverWriteExistData = true;

            var ok = await Save(vm, useAsync);

            Assert.IsTrue(ok, string.Join("; ", Errors(vm)));
            using var dc = new DataContext(seed, DBTypeEnum.Memory);
            var saved = dc.Set<ImportOverwriteDoc>().Single();
            // the cell cannot be converted to a list, so the imported item has no tags; the seeded ["x","y"] is replaced
            CollectionAssert.AreNotEqual(new[] { "x", "y" }, saved.Tags?.ToArray(), "UpdateProperty must have run for the mapped collection");
            Assert.AreEqual(0, Warnings(logs).Count);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Overwrite_UnmappedListWithoutNotMapped_WarnsOnce(bool useAsync)
        {
            var seed = Guid.NewGuid().ToString();
            Seed(seed, "001", "002");
            var logs = new CapturingLoggerProvider();
            var wtm = NewWtm(seed, NewLoggerFactory(logs));
            var vm = NewImportVm<DocIgnoredTagsImportVM>(wtm, FillTemplate(new DocIgnoredTagsImportVM().GenerateTemplate(out _),
                new[] { "001", "a" }, new[] { "002", "b" }));
            vm.IsOverWriteExistData = true;

            var ok = await Save(vm, useAsync);

            Assert.IsTrue(ok, string.Join("; ", Errors(vm)));
            var warnings = Warnings(logs);
            Assert.AreEqual(1, warnings.Count, string.Join(" || ", warnings.Select(x => x.Message)));
            StringAssert.Contains(warnings[0].Message, "FluentIgnoredTags");
        }
    }
}
