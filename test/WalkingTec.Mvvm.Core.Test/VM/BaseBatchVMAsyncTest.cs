using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Models;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    /// <summary>A model that is a workflow target (IWorkflow), so the batch delete also cleans up its workflow data.</summary>
    public class BatchWorkflowDoc : TopBasePoco, IWorkflow
    {
        public string Title { get; set; }
    }

    /// <summary>
    /// The optional asynchronous batch operations of BaseBatchVM (DoBatchDeleteAsync, DoBatchEditAsync, RefreshErrorListAsync, #382).
    /// The synchronous DoBatchDelete / DoBatchEdit are not changed; the async path has to give the same return value, rows, error messages,
    /// model state errors, list row errors and file deletions, keep overridden synchronous methods and let cancellation propagate.
    /// </summary>
    [TestClass]
    public class BaseBatchVMAsyncTest
    {
        #region view models

        private class SchoolBatchVM : BaseBatchVM<School, SchoolEdit>
        {
        }

        private class StudentBatchVM : BaseBatchVM<Student, SchoolEdit>
        {
        }

        private class SchoolTopBatchVM : BaseBatchVM<SchoolTop, SchoolEdit>
        {
        }

        private class AlbumBatchVM : BaseBatchVM<Album, SchoolEdit>
        {
        }

        private class WorkflowDocBatchVM : BaseBatchVM<BatchWorkflowDoc, SchoolEdit>
        {
        }

        /// <summary>Refuses to delete the listed ids.</summary>
        private class GuardedSchoolBatchVM : BaseBatchVM<School, SchoolEdit>
        {
            public HashSet<string> Blocked { get; } = new HashSet<string>();

            protected override bool CheckIfCanDelete(object id, out string errorMessage)
            {
                if (Blocked.Contains(id.ToString()))
                {
                    errorMessage = "blocked";
                    return false;
                }
                return base.CheckIfCanDelete(id, out errorMessage);
            }
        }

        /// <summary>The CRUD view model DoBatchEdit finds for SchoolTop (by reflection); it rejects every record.</summary>
        public class SchoolTopRejectingCrudVM : BaseCRUDVM<SchoolTop>
        {
            /// <summary>Replaces the default rejection (a test sets and resets it).</summary>
            public static Action ValidateHook { get; set; }

            public override void Validate()
            {
                if (ValidateHook != null)
                {
                    ValidateHook();
                    return;
                }
                MSD.AddModelError("Entity.SchoolName", "name rejected");
            }
        }

        /// <summary>Overrides only the synchronous methods, with their own logic that never reaches the database.</summary>
        private class SyncOverrideBatchVM : BaseBatchVM<School, SchoolEdit>
        {
            public int DeleteCalls { get; private set; }
            public int EditCalls { get; private set; }

            public override bool DoBatchDelete()
            {
                DeleteCalls++;
                return false;
            }

            public override bool DoBatchEdit()
            {
                EditCalls++;
                return false;
            }
        }

        /// <summary>Overrides the synchronous methods but still runs the base logic (the typical wrapper override).</summary>
        private class SyncWrapperBatchVM : BaseBatchVM<School, SchoolEdit>
        {
            public int DeleteCalls { get; private set; }
            public int EditCalls { get; private set; }

            public override bool DoBatchDelete()
            {
                DeleteCalls++;
                return base.DoBatchDelete();
            }

            public override bool DoBatchEdit()
            {
                EditCalls++;
                return base.DoBatchEdit();
            }
        }

        /// <summary>Overrides only the asynchronous methods.</summary>
        private class AsyncOverrideBatchVM : BaseBatchVM<School, SchoolEdit>
        {
            public int DeleteCalls { get; private set; }
            public int EditCalls { get; private set; }
            public int AsyncDeleteCalls { get; private set; }
            public int AsyncEditCalls { get; private set; }

            public override bool DoBatchDelete()
            {
                DeleteCalls++;
                return base.DoBatchDelete();
            }

            public override bool DoBatchEdit()
            {
                EditCalls++;
                return base.DoBatchEdit();
            }

            public override Task<bool> DoBatchDeleteAsync(CancellationToken cancellationToken = default)
            {
                AsyncDeleteCalls++;
                return Task.FromResult(true);
            }

            public override Task<bool> DoBatchEditAsync(CancellationToken cancellationToken = default)
            {
                AsyncEditCalls++;
                return Task.FromResult(true);
            }
        }

        /// <summary>Overrides both versions: the async override is what the async method runs.</summary>
        private class BothOverrideBatchVM : BaseBatchVM<School, SchoolEdit>
        {
            public int DeleteCalls { get; private set; }
            public int EditCalls { get; private set; }
            public int AsyncDeleteCalls { get; private set; }
            public int AsyncEditCalls { get; private set; }

            public override bool DoBatchDelete()
            {
                DeleteCalls++;
                return true;
            }

            public override bool DoBatchEdit()
            {
                EditCalls++;
                return true;
            }

            public override Task<bool> DoBatchDeleteAsync(CancellationToken cancellationToken = default)
            {
                AsyncDeleteCalls++;
                return Task.FromResult(false);
            }

            public override Task<bool> DoBatchEditAsync(CancellationToken cancellationToken = default)
            {
                AsyncEditCalls++;
                return Task.FromResult(false);
            }
        }

        /// <summary>A subclass of a subclass: the override is inherited, not declared by the concrete type.</summary>
        private class InheritedSyncOverrideBatchVM : SyncOverrideBatchVM
        {
        }

        /// <summary>The list a batch operation refreshes after a failure; counts how it was searched.</summary>
        private interface ICountingList
        {
            int AsyncSearches { get; }
            int Searches { get; }
        }

        private class ListVM<T> : BasePagedListVM<T, BaseSearcher>, ICountingList where T : TopBasePoco, new()
        {
            public int AsyncSearches { get; private set; }
            public int Searches { get; private set; }

            public override IOrderedQueryable<T> GetSearchQuery()
            {
                return DC.Set<T>().OrderBy(x => x.ID);
            }

            public override Task DoSearchAsync(CancellationToken cancellationToken = default)
            {
                AsyncSearches++;
                return base.DoSearchAsync(cancellationToken);
            }

            public override void AfterDoSearcher()
            {
                Searches++;
                base.AfterDoSearcher();
            }

            protected override IEnumerable<IGridColumn<T>> InitGridHeader()
            {
                return new List<GridColumn<T>>
                {
                    this.MakeGridHeader(x => x.ID),
                };
            }
        }

        #endregion

        #region data contexts

        // DataContext variant that ReCreate() (used to delete files) and WTMContext.CreateDC() can construct: the shared test DataContext has neither of the
        // constructors they look for. Private on purpose, so it is not picked up by CS.Cis (which only scans exported types) and cannot affect other tests.
        private class BatchTestDataContext : DataContext
        {
            public BatchTestDataContext(string seed, DBTypeEnum dbType) : base(seed, dbType)
            {
            }

            public BatchTestDataContext(CS cs) : base(cs.Value, cs.DbType ?? DBTypeEnum.Memory)
            {
            }
        }

        /// <summary>Counts how the batch operation saves and can fail the save, so a test sees which path ran and what happens when the database refuses.</summary>
        private sealed class RecordingDataContext : BatchTestDataContext
        {
            public RecordingDataContext(string seed) : base(seed, DBTypeEnum.Memory)
            {
            }

            public RecordingDataContext(string seed, DBTypeEnum dbType) : base(seed, dbType)
            {
            }

            public int SyncSaves { get; private set; }
            public int AsyncSaves { get; private set; }
            public List<CancellationToken> Tokens { get; } = new List<CancellationToken>();
            public Func<Exception> SaveFailure { get; set; }

            /// <summary>Called whenever a set of the given type is asked for (the workflow lookups of a delete are made through it).</summary>
            public Action<Type> OnSet { get; set; }

            public override DbSet<T> Set<T>()
            {
                OnSet?.Invoke(typeof(T));
                return base.Set<T>();
            }

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

        #endregion

        #region setup

        private Microsoft.Extensions.Localization.IStringLocalizer _previousLocalizer;

        [TestInitialize]
        public void Initialize()
        {
            // the batch operations read their error texts from the static CoreProgram._localizer
            _previousLocalizer = CoreProgram._localizer;
            CoreProgram._localizer = new TagHelpers.LayUI.KeyEchoLocalizer();
            // WtmFileProvider keeps its handler registry in static state that the host fills at startup
            WtmFileProvider.Init(new Configs(), new GlobalData { AllAssembly = new List<Assembly> { typeof(WtmFileProvider).Assembly } });
        }

        [TestCleanup]
        public void Cleanup()
        {
            CoreProgram._localizer = _previousLocalizer;
        }

        private static WTMContext NewWtm(string seed)
        {
            var wtm = MockWtmContext.CreateWtmContext(new DataContext(seed, DBTypeEnum.Memory), "batchuser");
            // WTMContext.CreateDC() inspects the Referer header; the shared mock leaves Request.Headers unset (null)
            Moq.Mock.Get(wtm.HttpContext.Request).Setup(x => x.Headers).Returns(new Microsoft.AspNetCore.Http.HeaderDictionary());
            wtm.ConfigInfo.Connections = new List<CS>
            {
                new CS
                {
                    Key = "default",
                    Value = seed,
                    DbType = DBTypeEnum.Memory,
                    DcConstructor = typeof(BatchTestDataContext).GetConstructor(new[] { typeof(CS) }),
                },
            };
            return wtm;
        }

        private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
        private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");

        private static string[] Ids(params Guid[] ids) => ids.Select(x => x.ToString()).ToArray();

        private static void SeedSchools(string seed)
        {
            using var dc = new DataContext(seed, DBTypeEnum.Memory);
            foreach (var (id, code) in new[] { (A, "101"), (B, "102"), (C, "103") })
            {
                dc.Set<School>().Add(new School { ID = id, SchoolCode = code, SchoolName = "name" + code, SchoolType = SchoolTypeEnum.PUB, Remark = "r" + code, CreateBy = "seed" });
            }
            dc.SaveChanges();
        }

        private static void SeedStudents(string seed)
        {
            using var dc = new DataContext(seed, DBTypeEnum.Memory);
            foreach (var (id, n) in new[] { (A, "1"), (B, "2"), (C, "3") })
            {
                dc.Set<Student>().Add(new Student { ID = id, LoginName = "s" + n, Password = "p", Name = "n" + n, IsValid = true, CreateBy = "seed" });
            }
            dc.SaveChanges();
        }

        private static void SeedSchoolTops(string seed)
        {
            using var dc = new DataContext(seed, DBTypeEnum.Memory);
            foreach (var (id, code) in new[] { (A, "101"), (B, "102"), (C, "103") })
            {
                dc.Set<SchoolTop>().Add(new SchoolTop { ID = id, SchoolCode = code, SchoolName = "name" + code, SchoolType = SchoolTypeEnum.PUB, Remark = "r" + code });
            }
            dc.SaveChanges();
        }

        /// <summary>Three albums (A, B, C), each with a cover, a scan and one photo, every one of them an uploaded file.</summary>
        private static void SeedAlbums(string seed)
        {
            var wtm = NewWtm(seed);
            var provider = new WtmFileProvider(wtm);
            Guid Upload(string name) => Guid.Parse(provider.Upload(name + ".txt", 3, new System.IO.MemoryStream(new byte[] { 1, 2, 3 }), dc: wtm.DC).GetID().ToString());
            using var dc = new DataContext(seed, DBTypeEnum.Memory);
            foreach (var id in new[] { A, B, C })
            {
                dc.Set<Album>().Add(new Album { ID = id, Title = "album " + id.ToString().Substring(35), CoverId = Upload("cover"), ScanId = Upload("scan") });
                dc.Set<AlbumPhoto>().Add(new AlbumPhoto { ID = Guid.NewGuid(), AlbumId = id, FileId = Upload("photo"), Order = 1 });
            }
            dc.SaveChanges();
        }

        /// <summary>Documents A, B and C; every one has a workflow instance with a log record and a FrameworkWorkflow.</summary>
        private static void SeedWorkflowDocs(string seed)
        {
            using var dc = new DataContext(seed, DBTypeEnum.Memory);
            foreach (var id in new[] { A, B, C })
            {
                var instanceId = "wi-" + id;
                dc.Set<BatchWorkflowDoc>().Add(new BatchWorkflowDoc { ID = id, Title = "doc" });
                dc.Set<Elsa_WorkflowInstance>().Add(new Elsa_WorkflowInstance
                {
                    ID = instanceId,
                    DefinitionId = "def",
                    CorrelationId = "corr",
                    DefinitionVersionId = "defver",
                    ContextType = typeof(BatchWorkflowDoc).FullName,
                    ContextId = id.ToString(),
                    CreatedAt = DateTimeOffset.Now,
                });
                dc.Set<Elsa_WorkflowExecutionLogRecord>().Add(new Elsa_WorkflowExecutionLogRecord
                {
                    ID = "log-" + id,
                    WorkflowInstanceId = instanceId,
                    ActivityType = "t",
                    ActivityId = "a",
                    Timestamp = DateTimeOffset.Now,
                });
                dc.Set<FrameworkWorkflow>().Add(new FrameworkWorkflow { ID = Guid.NewGuid(), UserCode = "u", WorkflowId = instanceId, ActivityId = "a" });
            }
            dc.SaveChanges();
        }

        private static string SchoolLine(School x) => $"{x.SchoolCode}|{x.SchoolName}|{x.SchoolType}|{x.Remark}|{x.CreateBy}|{x.UpdateBy}|{x.UpdateTime != null}";

        private static List<string> DumpSchools(DataContext dc) => dc.Set<School>().OrderBy(x => x.SchoolCode).ToList().Select(SchoolLine).ToList();

        private static List<string> DumpStudents(DataContext dc) => dc.Set<Student>().IgnoreQueryFilters().OrderBy(x => x.LoginName).ToList()
            .Select(x => $"{x.LoginName}|{x.Name}|{x.IsValid}|{x.CreateBy}|{x.UpdateBy}|{x.UpdateTime != null}").ToList();

        private static List<string> DumpSchoolTops(DataContext dc) => dc.Set<SchoolTop>().OrderBy(x => x.SchoolCode).ToList().Select(x => $"{x.SchoolCode}|{x.SchoolName}|{x.SchoolType}|{x.Remark}").ToList();

        private static List<string> DumpAlbums(DataContext dc) => dc.Set<Album>().OrderBy(x => x.ID).ToList().Select(x => $"{x.ID}|{x.Title}").ToList();

        private static List<string> DumpWorkflowDocs(DataContext dc)
        {
            var rows = dc.Set<BatchWorkflowDoc>().OrderBy(x => x.ID).ToList().Select(x => "doc|" + x.ID).ToList();
            rows.AddRange(dc.Set<Elsa_WorkflowInstance>().OrderBy(x => x.ID).ToList().Select(x => "instance|" + x.ID));
            rows.AddRange(dc.Set<Elsa_WorkflowExecutionLogRecord>().OrderBy(x => x.ID).ToList().Select(x => "log|" + x.ID));
            rows.AddRange(dc.Set<FrameworkWorkflow>().OrderBy(x => x.WorkflowId).ToList().Select(x => "framework|" + x.WorkflowId));
            return rows;
        }

        /// <summary>What a batch operation left behind, in a form that can be compared between the two paths.</summary>
        private sealed class Outcome
        {
            public bool Result;
            public List<string> Errors;
            public List<string> ModelErrors;
            public List<string> Rows;
            public List<string> ListErrors;
            public int FilesLeft;
            public int AsyncSearches;
            public int Searches;
        }

        private enum Op
        {
            Delete,
            Edit,
        }

        /// <summary>
        /// Runs one batch operation on a fresh database (filled by <paramref name="seeding"/>) through the synchronous or the asynchronous entry point.
        /// </summary>
        private static async Task<Outcome> Run<TModel, TVm>(Op op, bool useAsync, Action<string> seeding, string[] ids, Func<DataContext, List<string>> dump,
            Func<string, DataContext> dataContext = null, Action<TVm> configure = null, CancellationToken cancellationToken = default)
            where TModel : TopBasePoco, new()
            where TVm : BaseBatchVM<TModel, SchoolEdit>, new()
        {
            var seed = Guid.NewGuid().ToString();
            seeding(seed);
            var wtm = NewWtm(seed);
            var vm = new TVm { Wtm = wtm };
            vm.DC = dataContext != null ? dataContext(seed) : new BatchTestDataContext(seed, DBTypeEnum.Memory);
            vm.Ids = ids;
            configure?.Invoke(vm);

            var outcome = new Outcome();
            try
            {
                if (op == Op.Delete)
                {
                    outcome.Result = useAsync ? await vm.DoBatchDeleteAsync(cancellationToken) : vm.DoBatchDelete();
                }
                else
                {
                    outcome.Result = useAsync ? await vm.DoBatchEditAsync(cancellationToken) : vm.DoBatchEdit();
                }
            }
            finally
            {
                using var check = new DataContext(seed, DBTypeEnum.Memory);
                outcome.Rows = dump(check);
                outcome.FilesLeft = check.Set<FileAttachment>().Count();
                outcome.Errors = vm.ErrorMessage.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}").ToList();
                outcome.ModelErrors = wtm.MSD.Keys.OrderBy(x => x).SelectMany(k => wtm.MSD[k].Select(e => $"{k}={e.ErrorMessage}")).ToList();
                // the counts come first: reading the rows of a list that was not searched searches it
                outcome.AsyncSearches = (vm.ListVM as ICountingList)?.AsyncSearches ?? 0;
                outcome.Searches = (vm.ListVM as ICountingList)?.Searches ?? 0;
                outcome.ListErrors = vm.ListVM?.GetEntityList().OrderBy(x => x.ID).Select(x => $"{x.ID}|{x.BatchError}").ToList() ?? new List<string>();
            }
            return outcome;
        }

        /// <summary>Runs the same scenario through the sync and the async entry point (each on its own fresh database), checks they agree and returns both.</summary>
        private static async Task<(Outcome Sync, Outcome Async)> Both<TModel, TVm>(Op op, Action<string> seeding, string[] ids, Func<DataContext, List<string>> dump,
            Func<string, DataContext> dataContext = null, Action<TVm> configure = null)
            where TModel : TopBasePoco, new()
            where TVm : BaseBatchVM<TModel, SchoolEdit>, new()
        {
            var sync = await Run<TModel, TVm>(op, false, seeding, ids, dump, dataContext, configure);
            var async = await Run<TModel, TVm>(op, true, seeding, ids, dump, dataContext, configure);
            AssertParity(sync, async);
            return (sync, async);
        }

        private static void AssertParity(Outcome sync, Outcome async)
        {
            Assert.AreEqual(sync.Result, async.Result, "return value");
            CollectionAssert.AreEqual(sync.Errors, async.Errors, "ErrorMessage");
            CollectionAssert.AreEqual(sync.ModelErrors, async.ModelErrors, "model state errors");
            CollectionAssert.AreEqual(sync.Rows, async.Rows, "persisted rows");
            CollectionAssert.AreEqual(sync.ListErrors, async.ListErrors, "BatchError of the list rows");
            Assert.AreEqual(sync.FilesLeft, async.FilesLeft, "file deletion");
            Assert.AreEqual(sync.Searches, async.Searches, "number of list searches");
        }

        #endregion

        #region parity: batch delete

        [TestMethod]
        public async Task DeleteAsync_NonPersistModel_HardDeletesLikeSync()
        {
            var (sync, async) = await Both<SchoolTop, SchoolTopBatchVM>(Op.Delete, SeedSchoolTops, Ids(A, C), DumpSchoolTops);

            Assert.IsTrue(async.Result);
            CollectionAssert.AreEqual(new[] { "102|name102|PUB|r102" }, async.Rows);
            Assert.AreEqual(0, async.Errors.Count);
            Assert.AreEqual(0, async.ModelErrors.Count);
            Assert.IsTrue(sync.Result);
        }

        [TestMethod]
        public async Task DeleteAsync_NonPersistBasePocoModel_HardDeletesLikeSync()
        {
            var (sync, async) = await Both<School, SchoolBatchVM>(Op.Delete, SeedSchools, Ids(A, C), DumpSchools);

            Assert.IsTrue(async.Result);
            CollectionAssert.AreEqual(new[] { "102|name102|PUB|r102|seed||False" }, async.Rows);
            Assert.IsTrue(sync.Result);
        }

        [TestMethod]
        public async Task DeleteAsync_PersistModel_SoftDeletesLikeSync()
        {
            var (sync, async) = await Both<Student, StudentBatchVM>(Op.Delete, SeedStudents, Ids(A, C), DumpStudents);

            Assert.IsTrue(async.Result);
            CollectionAssert.AreEqual(new[]
            {
                "s1|n1|False|seed|batchuser|True",
                "s2|n2|True|seed||False",
                "s3|n3|False|seed|batchuser|True",
            }, async.Rows);
            Assert.IsTrue(sync.Result);
        }

        [TestMethod]
        public async Task DeleteAsync_ModelWithFiles_DeletesRowsAndTheirFilesLikeSync()
        {
            var (sync, async) = await Both<Album, AlbumBatchVM>(Op.Delete, SeedAlbums, Ids(A, C), DumpAlbums);

            Assert.IsTrue(async.Result);
            CollectionAssert.AreEqual(new[] { $"{B}|album b" }, async.Rows);
            // 3 albums x (cover, scan, photo) = 9 files; the files of the two deleted albums are gone
            Assert.AreEqual(3, async.FilesLeft);
            Assert.AreEqual(3, sync.FilesLeft);
        }

        [TestMethod]
        public async Task DeleteAsync_WorkflowModel_RemovesTheWorkflowDataLikeSync()
        {
            var (sync, async) = await Both<BatchWorkflowDoc, WorkflowDocBatchVM>(Op.Delete, SeedWorkflowDocs, Ids(A, C), DumpWorkflowDocs);

            Assert.IsTrue(async.Result);
            CollectionAssert.AreEqual(new[] { "doc|" + B, "instance|wi-" + B, "log|log-" + B, "framework|wi-" + B }, async.Rows);
            Assert.IsTrue(sync.Result);
        }

        [TestMethod]
        public async Task DeleteAsync_CheckIfCanDeleteRefuses_ReturnsFalseAndDeletesNothingLikeSync()
        {
            var (sync, async) = await Both<School, GuardedSchoolBatchVM>(Op.Delete, SeedSchools, Ids(A, B, C), DumpSchools,
                configure: vm => vm.Blocked.Add(B.ToString()));

            Assert.IsFalse(async.Result);
            CollectionAssert.AreEqual(new[] { $"{A}=Sys.Rollback", $"{B}=blocked", $"{C}=Sys.Rollback" }, async.Errors);
            CollectionAssert.AreEqual(new[] { "=Sys.DataCannotDelete" }, async.ModelErrors);
            Assert.AreEqual(3, async.Rows.Count, "nothing is deleted: " + string.Join("; ", async.Rows));
            Assert.IsFalse(sync.Result);
        }

        [TestMethod]
        public async Task DeleteAsync_SaveChangesThrows_ReturnsFalseWithModelErrorAndKeepsFilesLikeSync()
        {
            var (sync, async) = await Both<Album, AlbumBatchVM>(Op.Delete, SeedAlbums, Ids(A, C), DumpAlbums,
                dataContext: seed => new RecordingDataContext(seed) { SaveFailure = () => new InvalidOperationException("storage refused") });

            Assert.IsFalse(async.Result);
            Assert.AreEqual(0, async.Errors.Count, "a failed save has no data id to report");
            CollectionAssert.AreEqual(new[] { "=Sys.DataCannotDelete" }, async.ModelErrors);
            Assert.AreEqual(3, async.Rows.Count, "no album is deleted");
            Assert.AreEqual(9, async.FilesLeft, "no file is deleted");
            Assert.IsFalse(sync.Result);
        }

        [TestMethod]
        public async Task DeleteAsync_FailureWithListVM_AssignsBatchErrorAndRefreshesTheListAsynchronously()
        {
            var (sync, async) = await Both<School, GuardedSchoolBatchVM>(Op.Delete, SeedSchools, Ids(A, B, C), DumpSchools,
                configure: vm =>
                {
                    vm.Blocked.Add(B.ToString());
                    vm.ListVM = new ListVM<School> { Wtm = vm.Wtm };
                });

            Assert.IsFalse(async.Result);
            CollectionAssert.AreEqual(new[] { $"{A}|Sys.Rollback", $"{B}|blocked", $"{C}|Sys.Rollback" }, async.ListErrors);
            Assert.AreEqual(1, async.AsyncSearches, "the async path searches with DoSearchAsync");
            Assert.AreEqual(0, sync.AsyncSearches, "the sync path searches with DoSearch");
            Assert.AreEqual(1, async.Searches);
            Assert.AreEqual(1, sync.Searches);
        }

        [TestMethod]
        public async Task DeleteAsync_SuccessWithListVM_DoesNotSearchTheList()
        {
            var (sync, async) = await Both<School, SchoolBatchVM>(Op.Delete, SeedSchools, Ids(A), DumpSchools,
                configure: vm => vm.ListVM = new ListVM<School> { Wtm = vm.Wtm });

            Assert.IsTrue(async.Result);
            Assert.AreEqual(0, async.Searches);
            Assert.AreEqual(0, sync.Searches);
        }

        #endregion

        #region parity: batch edit

        private static void ConfigureEdit(BaseBatchVM<School, SchoolEdit> vm)
        {
            vm.LinkedVM = new SchoolEdit { SchoolName = "renamed", Remark = "newremark" };
            vm.FC.Add("LinkedVM.SchoolName", 0);
            vm.FC.Add("LinkedVM.Remark", 0);
        }

        // a failed batch edit refreshes the list it was started from, so a test that makes one fail has to give it a list
        private static void ConfigureEditWithList(BaseBatchVM<School, SchoolEdit> vm)
        {
            ConfigureEdit(vm);
            vm.ListVM = new ListVM<School> { Wtm = vm.Wtm };
        }

        private static void ConfigureTopEdit(BaseBatchVM<SchoolTop, SchoolEdit> vm)
        {
            vm.LinkedVM = new SchoolEdit { SchoolName = "renamed" };
            vm.FC.Add("LinkedVM.SchoolName", 0);
        }

        private static void ConfigureTopEditWithList(BaseBatchVM<SchoolTop, SchoolEdit> vm)
        {
            ConfigureTopEdit(vm);
            vm.ListVM = new ListVM<SchoolTop> { Wtm = vm.Wtm };
        }

        [TestMethod]
        public async Task EditAsync_UpdatesTheFieldsAndAuditColumnsLikeSync()
        {
            var (sync, async) = await Both<School, SchoolBatchVM>(Op.Edit, SeedSchools, Ids(A, B), DumpSchools, configure: ConfigureEdit);

            Assert.IsTrue(async.Result);
            CollectionAssert.AreEqual(new[]
            {
                "101|renamed|PUB|newremark|seed|batchuser|True",
                "102|renamed|PUB|newremark|seed|batchuser|True",
                "103|name103|PUB|r103|seed||False",
            }, async.Rows);
            Assert.AreEqual(0, async.Errors.Count);
            Assert.IsTrue(sync.Result);
        }

        [TestMethod]
        public async Task EditAsync_CrudViewModelRejects_ReturnsFalseWithItsMessageLikeSync()
        {
            var (sync, async) = await Both<SchoolTop, SchoolTopBatchVM>(Op.Edit, SeedSchoolTops, Ids(A, B), DumpSchoolTops, configure: ConfigureTopEditWithList);

            Assert.IsFalse(async.Result);
            CollectionAssert.AreEqual(new[] { $"{A}=name rejected", $"{B}=Sys.Rollback" }, async.Errors);
            Assert.IsTrue(async.Rows.All(x => !x.Contains("renamed")), "nothing is saved");
            Assert.IsFalse(sync.Result);
        }

        [TestMethod]
        public async Task EditAsync_SaveChangesThrows_ReturnsFalseLikeSync()
        {
            var (sync, async) = await Both<School, SchoolBatchVM>(Op.Edit, SeedSchools, Ids(A, B), DumpSchools,
                dataContext: seed => new RecordingDataContext(seed) { SaveFailure = () => new InvalidOperationException("storage refused") },
                configure: ConfigureEditWithList);

            Assert.IsFalse(async.Result);
            Assert.AreEqual(0, async.Errors.Count, "a failed save has no data id to report");
            Assert.IsTrue(async.Rows.All(x => !x.Contains("renamed")), "nothing is saved");
            Assert.IsFalse(sync.Result);
        }

        [TestMethod]
        public async Task EditAsync_StorageRejectsTheUpdate_ReturnsFalseLikeSync()
        {
            // an id that does not exist: the update itself fails when it is saved (no test double involved)
            var (sync, async) = await Both<School, SchoolBatchVM>(Op.Edit, SeedSchools, new[] { A.ToString(), Guid.Parse("00000000-0000-0000-0000-0000000000ff").ToString() }, DumpSchools,
                configure: ConfigureEditWithList);

            Assert.IsFalse(async.Result);
            Assert.IsFalse(sync.Result);
        }

        [TestMethod]
        public async Task EditAsync_FailureWithListVM_AssignsBatchErrorAndRefreshesTheListAsynchronously()
        {
            var (sync, async) = await Both<SchoolTop, SchoolTopBatchVM>(Op.Edit, SeedSchoolTops, Ids(A, B), DumpSchoolTops,
                configure: ConfigureTopEditWithList);

            Assert.IsFalse(async.Result);
            CollectionAssert.AreEqual(new[] { $"{A}|name rejected", $"{B}|Sys.Rollback", $"{C}|" }, async.ListErrors);
            Assert.AreEqual(1, async.AsyncSearches, "the async path searches with DoSearchAsync");
            Assert.AreEqual(0, sync.AsyncSearches, "the sync path searches with DoSearch");
            Assert.AreEqual(1, async.Searches);
            Assert.AreEqual(1, sync.Searches);
        }

        #endregion

        #region override preservation

        private static (RecordingDataContext Dc, WTMContext Wtm, string Seed) Recording(Action<string> seeding)
        {
            var seed = Guid.NewGuid().ToString();
            seeding(seed);
            return (new RecordingDataContext(seed), NewWtm(seed), seed);
        }

        private static T NewVm<T>(RecordingDataContext dc, WTMContext wtm, params Guid[] ids) where T : BaseBatchVM<School, SchoolEdit>, new()
        {
            var vm = new T { Wtm = wtm, DC = dc, Ids = Ids(ids) };
            ConfigureEdit(vm);
            return vm;
        }

        [TestMethod]
        public async Task SubclassOverridingOnlyTheSyncMethods_AsyncMethodsRunTheOverrides()
        {
            var (dc, wtm, _) = Recording(SeedSchools);
            var vm = NewVm<SyncOverrideBatchVM>(dc, wtm, A);

            Assert.IsFalse(await vm.DoBatchDeleteAsync());
            Assert.IsFalse(await vm.DoBatchEditAsync());

            Assert.AreEqual(1, vm.DeleteCalls);
            Assert.AreEqual(1, vm.EditCalls);
            Assert.AreEqual(0, dc.AsyncSaves + dc.SyncSaves, "the override never reached the database");
        }

        [TestMethod]
        public async Task SubclassInheritingASyncOverride_AsyncMethodsRunIt()
        {
            var (dc, wtm, _) = Recording(SeedSchools);
            var vm = NewVm<InheritedSyncOverrideBatchVM>(dc, wtm, A);

            Assert.IsFalse(await vm.DoBatchDeleteAsync());
            Assert.IsFalse(await vm.DoBatchEditAsync());

            Assert.AreEqual(1, vm.DeleteCalls);
            Assert.AreEqual(1, vm.EditCalls);
        }

        [TestMethod]
        public async Task SubclassOverridingTheSyncMethodsAndCallingBase_AsyncMethodsRunTheOverrideAndTheBaseLogic()
        {
            var (dc, wtm, seed) = Recording(SeedSchools);
            var vm = NewVm<SyncWrapperBatchVM>(dc, wtm, A);

            Assert.IsTrue(await vm.DoBatchDeleteAsync());
            vm.Ids = Ids(B);
            Assert.IsTrue(await vm.DoBatchEditAsync());

            Assert.AreEqual(1, vm.DeleteCalls);
            Assert.AreEqual(1, vm.EditCalls);
            Assert.AreEqual(2, dc.SyncSaves, "the base logic of the override ran synchronously");
            Assert.AreEqual(0, dc.AsyncSaves);
            using var check = new DataContext(seed, DBTypeEnum.Memory);
            Assert.AreEqual(2, check.Set<School>().Count(), "one school is deleted");
        }

        [TestMethod]
        public async Task SubclassOverridingTheAsyncMethods_RunsItsOwnAndNotTheSyncOnes()
        {
            var (dc, wtm, _) = Recording(SeedSchools);
            var vm = NewVm<AsyncOverrideBatchVM>(dc, wtm, A);

            Assert.IsTrue(await vm.DoBatchDeleteAsync());
            Assert.IsTrue(await vm.DoBatchEditAsync());

            Assert.AreEqual(1, vm.AsyncDeleteCalls);
            Assert.AreEqual(1, vm.AsyncEditCalls);
            Assert.AreEqual(0, vm.DeleteCalls);
            Assert.AreEqual(0, vm.EditCalls);
        }

        [TestMethod]
        public async Task SubclassOverridingBothVersions_AsyncMethodsRunTheAsyncOverrides()
        {
            var vm = new BothOverrideBatchVM { Wtm = NewWtm(Guid.NewGuid().ToString()) };

            Assert.IsFalse(await vm.DoBatchDeleteAsync());
            Assert.IsFalse(await vm.DoBatchEditAsync());
            Assert.AreEqual(1, vm.AsyncDeleteCalls);
            Assert.AreEqual(1, vm.AsyncEditCalls);
            Assert.AreEqual(0, vm.DeleteCalls);
            Assert.AreEqual(0, vm.EditCalls);
            Assert.IsTrue(vm.DoBatchDelete(), "the synchronous overrides are untouched");
            Assert.IsTrue(vm.DoBatchEdit());
        }

        [TestMethod]
        public async Task NothingOverridden_AsyncMethodsSaveThroughSaveChangesAsyncWithTheToken()
        {
            var (dc, wtm, seed) = Recording(SeedSchools);
            var vm = NewVm<SchoolBatchVM>(dc, wtm, A);
            using var cts = new CancellationTokenSource();

            Assert.IsTrue(await vm.DoBatchDeleteAsync(cts.Token));
            vm.Ids = Ids(B);
            Assert.IsTrue(await vm.DoBatchEditAsync(cts.Token));

            Assert.AreEqual(0, dc.SyncSaves, "no synchronous SaveChanges");
            Assert.AreEqual(2, dc.AsyncSaves);
            Assert.IsTrue(dc.Tokens.All(x => x == cts.Token));
            using var check = new DataContext(seed, DBTypeEnum.Memory);
            Assert.AreEqual(2, check.Set<School>().Count());
        }

        [TestMethod]
        public void NothingOverridden_SyncMethodsStillUseSynchronousSaveChanges()
        {
            var (dc, wtm, _) = Recording(SeedSchools);
            var vm = NewVm<SchoolBatchVM>(dc, wtm, A);

            Assert.IsTrue(vm.DoBatchDelete());
            vm.Ids = Ids(B);
            Assert.IsTrue(vm.DoBatchEdit());

            Assert.AreEqual(2, dc.SyncSaves);
            Assert.AreEqual(0, dc.AsyncSaves);
        }

        #endregion

        #region cancellation

        [TestMethod]
        public async Task DeleteAsync_CancelledBeforeStart_ThrowsAndNothingHappens()
        {
            var seed = Guid.NewGuid().ToString();
            SeedAlbums(seed);
            var dc = new RecordingDataContext(seed);
            var vm = new AlbumBatchVM { Wtm = NewWtm(seed), DC = dc, Ids = Ids(A, C) };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => vm.DoBatchDeleteAsync(cts.Token));

            Assert.AreEqual(0, dc.AsyncSaves + dc.SyncSaves);
            Assert.AreEqual(0, vm.ErrorMessage.Count, "cancellation is not a delete error");
            Assert.AreEqual(0, vm.Wtm.MSD.Count);
            using var check = new DataContext(seed, DBTypeEnum.Memory);
            Assert.AreEqual(3, check.Set<Album>().Count());
            Assert.AreEqual(9, check.Set<FileAttachment>().Count(), "no file is deleted");
        }

        [TestMethod]
        public async Task EditAsync_CancelledBeforeStart_ThrowsAndNothingHappens()
        {
            var (dc, wtm, seed) = Recording(SeedSchools);
            var vm = NewVm<SchoolBatchVM>(dc, wtm, A, B);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => vm.DoBatchEditAsync(cts.Token));

            Assert.AreEqual(0, dc.AsyncSaves + dc.SyncSaves);
            Assert.AreEqual(0, vm.ErrorMessage.Count, "cancellation is not an edit error");
            using var check = new DataContext(seed, DBTypeEnum.Memory);
            CollectionAssert.AreEqual(new[] { "101|name101|PUB|r101|seed||False", "102|name102|PUB|r102|seed||False", "103|name103|PUB|r103|seed||False" }, DumpSchools(check));
        }

        [TestMethod]
        public async Task DeleteAsync_CancelledDuringSave_PropagatesAndKeepsTheFiles()
        {
            using var cts = new CancellationTokenSource();
            var seed = Guid.NewGuid().ToString();
            SeedAlbums(seed);
            var dc = new RecordingDataContext(seed) { SaveFailure = () => { cts.Cancel(); return new OperationCanceledException(cts.Token); } };
            var vm = new AlbumBatchVM { Wtm = NewWtm(seed), DC = dc, Ids = Ids(A, C) };

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => vm.DoBatchDeleteAsync(cts.Token));

            Assert.AreEqual(0, vm.ErrorMessage.Count, "cancellation is not a delete error");
            Assert.AreEqual(0, vm.Wtm.MSD.Count, "no delete error is reported");
            using var check = new DataContext(seed, DBTypeEnum.Memory);
            Assert.AreEqual(3, check.Set<Album>().Count());
            Assert.AreEqual(9, check.Set<FileAttachment>().Count(), "no file is deleted");
        }

        [TestMethod]
        public async Task EditAsync_CancelledDuringSave_Propagates()
        {
            using var cts = new CancellationTokenSource();
            var (dc, wtm, seed) = Recording(SeedSchools);
            dc.SaveFailure = () => { cts.Cancel(); return new OperationCanceledException(cts.Token); };
            var vm = NewVm<SchoolBatchVM>(dc, wtm, A, B);

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => vm.DoBatchEditAsync(cts.Token));

            Assert.AreEqual(0, vm.ErrorMessage.Count, "cancellation is not an edit error");
            using var check = new DataContext(seed, DBTypeEnum.Memory);
            Assert.IsTrue(DumpSchools(check).All(x => !x.Contains("renamed")));
        }

        [TestMethod]
        public async Task DeleteAsync_CancelledWhileTheWorkflowDataIsLookedUp_Propagates()
        {
            using var cts = new CancellationTokenSource();
            var seed = Guid.NewGuid().ToString();
            SeedWorkflowDocs(seed);
            var dc = new RecordingDataContext(seed)
            {
                OnSet = type =>
                {
                    if (type == typeof(Elsa_WorkflowInstance))
                    {
                        cts.Cancel();
                        throw new OperationCanceledException(cts.Token);
                    }
                },
            };
            var vm = new WorkflowDocBatchVM { Wtm = NewWtm(seed), DC = dc, Ids = Ids(A, C) };

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => vm.DoBatchDeleteAsync(cts.Token));

            Assert.AreEqual(0, vm.ErrorMessage.Count, "the per-item handler does not turn cancellation into a delete error");
            Assert.AreEqual(0, vm.Wtm.MSD.Count);
            Assert.AreEqual(0, dc.AsyncSaves + dc.SyncSaves);
            using var check = new DataContext(seed, DBTypeEnum.Memory);
            Assert.AreEqual(12, DumpWorkflowDocs(check).Count);
        }

        [TestMethod]
        public async Task EditAsync_CancelledWhileAnItemIsValidated_Propagates()
        {
            using var cts = new CancellationTokenSource();
            var seed = Guid.NewGuid().ToString();
            SeedSchoolTops(seed);
            var dc = new RecordingDataContext(seed);
            var vm = new SchoolTopBatchVM { Wtm = NewWtm(seed), DC = dc, Ids = Ids(A, B) };
            ConfigureTopEdit(vm);
            SchoolTopRejectingCrudVM.ValidateHook = () => { cts.Cancel(); throw new OperationCanceledException(cts.Token); };
            try
            {
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => vm.DoBatchEditAsync(cts.Token));
            }
            finally
            {
                SchoolTopRejectingCrudVM.ValidateHook = null;
            }

            Assert.AreEqual(0, vm.ErrorMessage.Count, "the per-item handler does not turn cancellation into an edit error");
            Assert.AreEqual(0, dc.AsyncSaves + dc.SyncSaves);
        }

        [TestMethod]
        public async Task DeleteAsync_OperationCanceledWithoutCancellation_IsAnErrorLikeAnyOtherException()
        {
            // an OperationCanceledException the caller did not ask for (for example a database timeout) is still reported like any other error
            var (sync, async) = await Both<Album, AlbumBatchVM>(Op.Delete, SeedAlbums, Ids(A, C), DumpAlbums,
                dataContext: seed => new RecordingDataContext(seed) { SaveFailure = () => new OperationCanceledException("timed out") });

            Assert.IsFalse(async.Result);
            CollectionAssert.AreEqual(new[] { "=Sys.DataCannotDelete" }, async.ModelErrors);
            Assert.AreEqual(9, async.FilesLeft);
            Assert.IsFalse(sync.Result);
        }

        [TestMethod]
        public async Task DeleteAsync_PerItemOperationCanceledWithoutCancellation_IsAnErrorLikeAnyOtherException()
        {
            var (sync, async) = await Both<BatchWorkflowDoc, WorkflowDocBatchVM>(Op.Delete, SeedWorkflowDocs, Ids(A, C), DumpWorkflowDocs,
                dataContext: seed => new RecordingDataContext(seed) { OnSet = type => { if (type == typeof(Elsa_WorkflowInstance)) throw new OperationCanceledException("timed out"); } });

            Assert.IsFalse(async.Result);
            CollectionAssert.AreEqual(new[] { $"{A}=timed out", $"{C}=timed out" }, async.Errors);
            Assert.AreEqual(12, async.Rows.Count);
            Assert.IsFalse(sync.Result);
        }

        [TestMethod]
        public async Task EditAsync_OperationCanceledWithoutCancellation_IsAnErrorLikeAnyOtherException()
        {
            var (sync, async) = await Both<School, SchoolBatchVM>(Op.Edit, SeedSchools, Ids(A, B), DumpSchools,
                dataContext: seed => new RecordingDataContext(seed) { SaveFailure = () => new OperationCanceledException("timed out") },
                configure: ConfigureEditWithList);

            Assert.IsFalse(async.Result);
            Assert.IsTrue(async.Rows.All(x => !x.Contains("renamed")));
            Assert.IsFalse(sync.Result);
        }

        [TestMethod]
        public async Task EditAsync_ValidationOperationCanceledWithoutCancellation_IsAnErrorLikeAnyOtherException()
        {
            SchoolTopRejectingCrudVM.ValidateHook = () => throw new OperationCanceledException("timed out");
            try
            {
                var (sync, async) = await Both<SchoolTop, SchoolTopBatchVM>(Op.Edit, SeedSchoolTops, Ids(A, B), DumpSchoolTops, configure: ConfigureTopEditWithList);

                Assert.IsFalse(async.Result);
                CollectionAssert.AreEqual(new[] { $"{A}=timed out", $"{B}=timed out" }, async.Errors);
                Assert.IsFalse(sync.Result);
            }
            finally
            {
                SchoolTopRejectingCrudVM.ValidateHook = null;
            }
        }

        #endregion

        #region query provider fallback

        // the entity and workflow lookups go through DC.Set<T>(), which is always EF Core, so the fallback for a provider without async support is checked on the helper itself
        private static Task<List<int>> ToListQueryAsync(IQueryable<int> query, CancellationToken cancellationToken)
        {
            var method = typeof(BaseBatchVM<School, SchoolEdit>).GetMethod("ToListQueryAsync", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(typeof(int));
            return (Task<List<int>>)method.Invoke(null, new object[] { query, cancellationToken });
        }

        [TestMethod]
        public async Task ToListQueryAsync_QueryWithoutAsyncProvider_FallsBackToSynchronousEnumeration()
        {
            var rows = await ToListQueryAsync(new List<int> { 3, 1, 2 }.AsQueryable().OrderBy(x => x), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, rows);
        }

        #endregion
    }
}
