using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Models;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    /// <summary>Test-only model with an int primary key.</summary>
    public class BatchIntKeyDoc : TopBasePoco
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public new int ID { get; set; }

        public string Title { get; set; }
    }

    /// <summary>Test-only model with a string primary key.</summary>
    public class BatchStringKeyDoc : TopBasePoco
    {
        [Key]
        [StringLength(50)]
        public new string ID { get; set; }

        public string Title { get; set; }
    }

    /// <summary>
    /// BaseBatchVM 批量删除：对每一条要删除的数据，用该数据自身的 Id 调用 CheckIfCanDelete；Ids 去重。
    /// 同步（DoBatchDelete）与异步（DoBatchDeleteAsync）两条路径都要满足。
    /// </summary>
    [TestClass]
    public class BaseBatchVMDeleteCheckTest
    {
        #region view models

        /// <summary>Records every CheckIfCanDelete call and refuses the listed ids (compared as Guids, whatever the spelling).</summary>
        private class RecordingBatchVM<T> : BaseBatchVM<T, SchoolEdit> where T : TopBasePoco, new()
        {
            public List<string> Calls { get; } = new List<string>();
            public HashSet<Guid> Refused { get; } = new HashSet<Guid>();
            /// <summary>Ids refused by their exact text (for models whose key is not a Guid).</summary>
            public HashSet<string> RefusedTexts { get; } = new HashSet<string>();
            /// <summary>Called with the id text of every CheckIfCanDelete call, before the decision.</summary>
            public Action<string> OnCheck { get; set; }

            protected override bool CheckIfCanDelete(object id, out string errorMessage)
            {
                var text = id?.ToString();
                Calls.Add(text);
                OnCheck?.Invoke(text);
                if ((Guid.TryParse(text, out var guid) && Refused.Contains(guid)) || (text != null && RefusedTexts.Contains(text)))
                {
                    errorMessage = "refused:" + text;
                    return false;
                }
                return base.CheckIfCanDelete(id, out errorMessage);
            }
        }

        /// <summary>Counts saves and can fail the workflow lookup that a delete makes for every record (a per-record failure).</summary>
        private sealed class CountingDataContext : DataContext
        {
            public CountingDataContext(string seed) : base(seed, DBTypeEnum.Memory)
            {
            }

            public int Saves { get; private set; }
            public bool FailWorkflowLookup { get; set; }

            public override DbSet<T> Set<T>()
            {
                if (FailWorkflowLookup && typeof(T) == typeof(Elsa_WorkflowInstance))
                {
                    throw new InvalidOperationException("lookup failed");
                }
                return base.Set<T>();
            }

            public override int SaveChanges()
            {
                Saves++;
                return base.SaveChanges();
            }

            public override Task<int> SaveChangesAsync(System.Threading.CancellationToken cancellationToken = default)
            {
                Saves++;
                return base.SaveChangesAsync(cancellationToken);
            }
        }

        #endregion

        #region setup

        private Microsoft.Extensions.Localization.IStringLocalizer _previousLocalizer;

        [TestInitialize]
        public void Initialize()
        {
            _previousLocalizer = CoreProgram._localizer;
            CoreProgram._localizer = new TagHelpers.LayUI.KeyEchoLocalizer();
            WtmFileProvider.Init(new Configs(), new GlobalData { AllAssembly = new List<Assembly> { typeof(WtmFileProvider).Assembly } });
        }

        [TestCleanup]
        public void Cleanup()
        {
            CoreProgram._localizer = _previousLocalizer;
        }

        // hex letters in the ids so that upper / lower case spellings differ
        private static readonly Guid A = Guid.Parse("a0a0a0a0-0000-0000-0000-00000000000a");
        private static readonly Guid B = Guid.Parse("b1b1b1b1-0000-0000-0000-00000000000b");
        private static readonly Guid C = Guid.Parse("c2c2c2c2-0000-0000-0000-00000000000c");
        private static readonly Guid Missing = Guid.Parse("deadbeef-0000-0000-0000-0000000000ff");

        private static WTMContext NewWtm(string seed)
        {
            var wtm = MockWtmContext.CreateWtmContext(new DataContext(seed, DBTypeEnum.Memory), "batchuser");
            Moq.Mock.Get(wtm.HttpContext.Request).Setup(x => x.Headers).Returns(new Microsoft.AspNetCore.Http.HeaderDictionary());
            return wtm;
        }

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

        private sealed class Outcome
        {
            public bool Result;
            public List<string> Calls;
            public Dictionary<string, string> Errors;
            public List<string> ModelErrors;
            public List<Guid> SchoolIds;
            public List<Guid> ValidStudentIds;
            public int StudentRows;
        }

        /// <summary>Runs one batch delete on a fresh database through the synchronous or the asynchronous entry point.</summary>
        private static async Task<Outcome> Run<T>(bool useAsync, Action<string> seeding, string[] ids, params Guid[] refused) where T : TopBasePoco, new()
        {
            var seed = Guid.NewGuid().ToString();
            seeding(seed);
            var wtm = NewWtm(seed);
            var vm = new RecordingBatchVM<T> { Wtm = wtm, DC = new DataContext(seed, DBTypeEnum.Memory), Ids = ids };
            foreach (var r in refused)
            {
                vm.Refused.Add(r);
            }
            var outcome = new Outcome { Result = useAsync ? await vm.DoBatchDeleteAsync() : vm.DoBatchDelete() };
            using var check = new DataContext(seed, DBTypeEnum.Memory);
            outcome.Calls = vm.Calls;
            outcome.Errors = vm.ErrorMessage;
            outcome.ModelErrors = wtm.MSD.Keys.OrderBy(x => x).SelectMany(k => wtm.MSD[k].Select(e => $"{k}={e.ErrorMessage}")).ToList();
            outcome.SchoolIds = check.Set<School>().OrderBy(x => x.SchoolCode).Select(x => x.ID).ToList();
            var students = check.Set<Student>().IgnoreQueryFilters().ToList();
            outcome.StudentRows = students.Count;
            outcome.ValidStudentIds = students.Where(x => x.IsValid).Select(x => x.ID).ToList();
            return outcome;
        }

        private static Task<Outcome> RunSchools(bool useAsync, string[] ids, params Guid[] refused) => Run<School>(useAsync, SeedSchools, ids, refused);

        private sealed class KeyedOutcome
        {
            public bool Result;
            public List<string> Calls;
            public Dictionary<string, string> Errors;
            public List<string> Remaining;
            public int Saves;
        }

        /// <summary>Runs one batch delete for a model whose rows are described by <paramref name="seed"/> / <paramref name="remaining"/>.</summary>
        private static async Task<KeyedOutcome> RunKeyed<T>(bool useAsync, Action<DataContext> seed, Func<DataContext, IEnumerable<string>> remaining, string[] ids,
            Action<RecordingBatchVM<T>, CountingDataContext> configure = null) where T : TopBasePoco, new()
        {
            var name = Guid.NewGuid().ToString();
            using (var dc = new DataContext(name, DBTypeEnum.Memory))
            {
                seed(dc);
                dc.SaveChanges();
            }
            var counting = new CountingDataContext(name);
            var vm = new RecordingBatchVM<T> { Wtm = NewWtm(name), DC = counting, Ids = ids };
            configure?.Invoke(vm, counting);
            var result = useAsync ? await vm.DoBatchDeleteAsync() : vm.DoBatchDelete();
            using var check = new DataContext(name, DBTypeEnum.Memory);
            return new KeyedOutcome { Result = result, Calls = vm.Calls, Errors = vm.ErrorMessage, Remaining = remaining(check).ToList(), Saves = counting.Saves };
        }

        private static void SeedIntDocs(DataContext dc)
        {
            foreach (var n in new[] { 5, 6, 7 })
            {
                dc.Set<BatchIntKeyDoc>().Add(new BatchIntKeyDoc { ID = n, Title = "t" + n });
            }
        }

        private static IEnumerable<string> IntDocIds(DataContext dc) => dc.Set<BatchIntKeyDoc>().OrderBy(x => x.ID).ToList().Select(x => x.ID.ToString());

        private static void SeedStringDocs(DataContext dc)
        {
            foreach (var key in new[] { "abc", "def", A.ToString() })
            {
                dc.Set<BatchStringKeyDoc>().Add(new BatchStringKeyDoc { ID = key, Title = "t" + key });
            }
        }

        private static IEnumerable<string> StringDocIds(DataContext dc) => dc.Set<BatchStringKeyDoc>().OrderBy(x => x.ID).ToList().Select(x => x.ID);

        private static string[] Ids(params Guid[] ids) => ids.Select(x => x.ToString()).ToArray();

        private static IEnumerable<Guid> CalledGuids(Outcome o) => o.Calls.Select(Guid.Parse);

        #endregion

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_HookIsCalledOncePerLoadedEntityWithItsOwnId(bool useAsync)
        {
            var o = await RunSchools(useAsync, Ids(C, A, B));

            Assert.IsTrue(o.Result);
            Assert.AreEqual(3, o.Calls.Count);
            CollectionAssert.AreEquivalent(new[] { A, B, C }, CalledGuids(o).ToList());
            Assert.AreEqual(0, o.SchoolIds.Count, "all deleted");
            Assert.AreEqual(0, o.Errors.Count);
            Assert.AreEqual(0, o.ModelErrors.Count);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_MixedKnownAndUnknownIds_RefusedRecordBlocksTheBatch(bool useAsync)
        {
            var o = await RunSchools(useAsync, Ids(Missing, A), A);

            Assert.IsFalse(o.Result);
            CollectionAssert.AreEqual(new[] { A }, CalledGuids(o).ToList(), "the one loaded record is checked with its own id");
            CollectionAssert.AreEquivalent(new[] { A, B, C }, o.SchoolIds, "nothing deleted");
            Assert.AreEqual(2, o.Errors.Count);
            Assert.AreEqual("refused:" + A, o.Errors[A.ToString()]);
            Assert.AreEqual("Sys.Rollback", o.Errors[Missing.ToString()]);
            CollectionAssert.AreEqual(new[] { "=Sys.DataCannotDelete" }, o.ModelErrors);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_MixedKnownAndUnknownIds_ChecksEachLoadedRecordWithItsOwnId(bool useAsync)
        {
            var o = await RunSchools(useAsync, Ids(Missing, A));

            Assert.IsTrue(o.Result);
            CollectionAssert.AreEqual(new[] { A }, CalledGuids(o).ToList());
            CollectionAssert.AreEquivalent(new[] { B, C }, o.SchoolIds);
            Assert.AreEqual(0, o.Errors.Count);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_DuplicateIdsOfTheSameSpelling_AreCheckedOnce(bool useAsync)
        {
            var o = await RunSchools(useAsync, Ids(A, A, B, A));

            Assert.IsTrue(o.Result);
            CollectionAssert.AreEquivalent(new[] { A, B }, CalledGuids(o).ToList());
            Assert.AreEqual(2, o.Calls.Count);
            CollectionAssert.AreEqual(new[] { C }, o.SchoolIds);
            Assert.AreEqual(0, o.Errors.Count);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_SameGuidInDifferentSpellings_IsCheckedOnceWithTheFirstSpelling(bool useAsync)
        {
            var upper = A.ToString().ToUpperInvariant();
            var braces = "{" + A + "}";
            var o = await RunSchools(useAsync, new[] { upper, A.ToString(), braces, A.ToString("N") });

            Assert.IsTrue(o.Result);
            CollectionAssert.AreEqual(new[] { upper }, o.Calls, "the caller's first spelling is passed to the hook");
            CollectionAssert.AreEquivalent(new[] { B, C }, o.SchoolIds);
            Assert.AreEqual(0, o.Errors.Count);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_DuplicateIdsOfRefusedRecord_HaveNoDuplicateKeys(bool useAsync)
        {
            var upper = A.ToString().ToUpperInvariant();
            var o = await RunSchools(useAsync, new[] { upper, A.ToString(), "{" + A + "}", B.ToString(), B.ToString() }, A);

            Assert.IsFalse(o.Result);
            CollectionAssert.AreEquivalent(new[] { A, B, C }, o.SchoolIds, "nothing deleted");
            Assert.AreEqual(2, o.Errors.Count);
            Assert.AreEqual("refused:" + upper, o.Errors[upper]);
            Assert.AreEqual("Sys.Rollback", o.Errors[B.ToString()]);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_IdsInAnyOrder_EachRecordCheckedWithItsOwnId(bool useAsync)
        {
            // the request lists the ids in a different order than the rows were stored in; the hook refuses B
            var o = await RunSchools(useAsync, Ids(B, C, A), B);

            Assert.IsFalse(o.Result, "refusing one record blocks the whole batch");
            CollectionAssert.AreEquivalent(new[] { A, B, C }, o.SchoolIds, "nothing deleted");
            Assert.AreEqual(B, CalledGuids(o).Last(), "checking stops at the refused record");
            Assert.AreEqual(CalledGuids(o).Count(), CalledGuids(o).Distinct().Count(), "no record is checked twice");
            Assert.AreEqual(3, o.Errors.Count);
            Assert.AreEqual("refused:" + B, o.Errors[B.ToString()]);
            Assert.AreEqual("Sys.Rollback", o.Errors[A.ToString()]);
            Assert.AreEqual("Sys.Rollback", o.Errors[C.ToString()]);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_AnyRecordRefused_DeletesNothing(bool useAsync)
        {
            var o = await RunSchools(useAsync, Ids(A, B, C), B);

            Assert.IsFalse(o.Result);
            CollectionAssert.AreEquivalent(new[] { A, B, C }, o.SchoolIds);
            CollectionAssert.AreEquivalent(new[] { A.ToString(), B.ToString(), C.ToString() }, o.Errors.Keys.ToList());
            Assert.AreEqual("refused:" + B, o.Errors[B.ToString()]);
            Assert.AreEqual("Sys.Rollback", o.Errors[A.ToString()]);
            Assert.AreEqual("Sys.Rollback", o.Errors[C.ToString()]);
            CollectionAssert.AreEqual(new[] { "=Sys.DataCannotDelete" }, o.ModelErrors);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_AllAllowed_HardDeletesTheRequestedRecordsOnly(bool useAsync)
        {
            var o = await RunSchools(useAsync, Ids(C, A));

            Assert.IsTrue(o.Result);
            CollectionAssert.AreEqual(new[] { B }, o.SchoolIds);
            Assert.AreEqual(2, o.Calls.Count);
            Assert.AreEqual(0, o.Errors.Count);
            Assert.AreEqual(0, o.ModelErrors.Count);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_SoftDeleteModel_MarksTheRequestedRecordsInvalid(bool useAsync)
        {
            var o = await Run<Student>(useAsync, SeedStudents, Ids(B, A, A));

            Assert.IsTrue(o.Result);
            Assert.AreEqual(3, o.StudentRows, "soft deleted rows stay");
            CollectionAssert.AreEqual(new[] { C }, o.ValidStudentIds);
            Assert.AreEqual(2, o.Calls.Count);
            CollectionAssert.AreEquivalent(new[] { A, B }, CalledGuids(o).ToList());
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_SoftDeleteModel_RefusedRecordBlocksTheBatch(bool useAsync)
        {
            var o = await Run<Student>(useAsync, SeedStudents, Ids(Missing, A, B), B);

            Assert.IsFalse(o.Result);
            CollectionAssert.AreEquivalent(new[] { A, B, C }, o.ValidStudentIds, "no record is marked invalid");
            Assert.AreEqual("refused:" + B, o.Errors[B.ToString()]);
            Assert.AreEqual("Sys.Rollback", o.Errors[A.ToString()]);
            Assert.AreEqual("Sys.Rollback", o.Errors[Missing.ToString()]);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_EmptyIds_DeletesNothingAndDoesNotThrow(bool useAsync)
        {
            var o = await RunSchools(useAsync, new string[0]);

            Assert.IsTrue(o.Result, "nothing to delete is not a failure");
            Assert.AreEqual(0, o.Calls.Count);
            CollectionAssert.AreEquivalent(new[] { A, B, C }, o.SchoolIds);
            Assert.AreEqual(0, o.Errors.Count);
            Assert.AreEqual(0, o.ModelErrors.Count);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_NullIds_DeletesNothingAndDoesNotThrow(bool useAsync)
        {
            var o = await RunSchools(useAsync, null);

            Assert.IsTrue(o.Result);
            Assert.AreEqual(0, o.Calls.Count);
            CollectionAssert.AreEquivalent(new[] { A, B, C }, o.SchoolIds);
            Assert.AreEqual(0, o.Errors.Count);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_OnlyUnknownIds_DeletesNothing(bool useAsync)
        {
            var o = await RunSchools(useAsync, Ids(Missing));

            Assert.IsTrue(o.Result);
            Assert.AreEqual(0, o.Calls.Count);
            CollectionAssert.AreEquivalent(new[] { A, B, C }, o.SchoolIds);
            Assert.AreEqual(0, o.Errors.Count);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_IntKeyWithDifferentSpelling_EachRecordCheckedOnceWithItsOwnId(bool useAsync)
        {
            // " 5" and "06" are converted to 5 and 6 by the framework's id conversion, but are not the records' own id strings
            var o = await RunKeyed<BatchIntKeyDoc>(useAsync, SeedIntDocs, IntDocIds, new[] { " 5", "06" });

            Assert.IsTrue(o.Result);
            CollectionAssert.AreEquivalent(new[] { "5", "6" }, o.Calls);
            CollectionAssert.AreEqual(new[] { "7" }, o.Remaining);
            Assert.AreEqual(0, o.Errors.Count);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_IntKeyWithDifferentSpelling_RefusedRecordBlocksTheBatch(bool useAsync)
        {
            var o = await RunKeyed<BatchIntKeyDoc>(useAsync, SeedIntDocs, IntDocIds, new[] { " 5" }, (vm, _) => vm.RefusedTexts.Add("5"));

            Assert.IsFalse(o.Result);
            CollectionAssert.AreEqual(new[] { "5" }, o.Calls, "the hook gets the record's own id string");
            CollectionAssert.AreEqual(new[] { "5", "6", "7" }, o.Remaining, "nothing deleted");
            Assert.AreEqual("refused:5", o.Errors["5"]);
            Assert.AreEqual(0, o.Saves);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_StringKeyWithSurroundingWhitespace_RecordCheckedOnceWithItsOwnId(bool useAsync)
        {
            var o = await RunKeyed<BatchStringKeyDoc>(useAsync, SeedStringDocs, StringDocIds, new[] { " abc " });

            Assert.IsTrue(o.Result);
            CollectionAssert.AreEqual(new[] { "abc" }, o.Calls);
            CollectionAssert.AreEquivalent(new[] { "def", A.ToString() }, o.Remaining);
            Assert.AreEqual(0, o.Errors.Count);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_StringKeyWithSurroundingWhitespace_RefusedRecordBlocksTheBatch(bool useAsync)
        {
            var o = await RunKeyed<BatchStringKeyDoc>(useAsync, SeedStringDocs, StringDocIds, new[] { " abc " }, (vm, _) => vm.RefusedTexts.Add("abc"));

            Assert.IsFalse(o.Result);
            CollectionAssert.AreEqual(new[] { "abc" }, o.Calls, "the hook gets the record's own id string");
            CollectionAssert.AreEquivalent(new[] { "abc", "def", A.ToString() }, o.Remaining, "nothing deleted");
            Assert.AreEqual("refused:abc", o.Errors["abc"]);
            Assert.AreEqual(0, o.Saves);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_StringKeysThatLookLikeGuidsAndDifferByCase_AreOneId(bool useAsync)
        {
            // ids that parse as Guid are de-duplicated by Guid value, whatever the key type
            var o = await RunKeyed<BatchStringKeyDoc>(useAsync, SeedStringDocs, StringDocIds, new[] { A.ToString(), A.ToString().ToUpperInvariant() });

            Assert.IsTrue(o.Result);
            CollectionAssert.AreEqual(new[] { A.ToString() }, o.Calls);
            CollectionAssert.AreEquivalent(new[] { "abc", "def" }, o.Remaining);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Delete_PerRecordFailure_ReportsThatRecordsIdAndSavesNothing(bool useAsync)
        {
            // the workflow lookup (made for every record of an IWorkflow model) fails only while B is being deleted
            var o = await RunKeyed<BatchWorkflowDoc>(useAsync,
                dc =>
                {
                    foreach (var id in new[] { A, B, C })
                    {
                        dc.Set<BatchWorkflowDoc>().Add(new BatchWorkflowDoc { ID = id, Title = "t" });
                    }
                },
                dc => dc.Set<BatchWorkflowDoc>().OrderBy(x => x.ID).ToList().Select(x => x.ID.ToString()),
                Ids(A, B, C),
                (vm, dc) => vm.OnCheck = text => dc.FailWorkflowLookup = text == B.ToString());

            Assert.IsFalse(o.Result);
            Assert.AreEqual(0, o.Saves, "nothing is saved");
            CollectionAssert.AreEquivalent(new[] { A.ToString(), B.ToString(), C.ToString() }, o.Remaining);
            Assert.AreEqual(3, o.Errors.Count);
            Assert.AreEqual("lookup failed", o.Errors[B.ToString()], "the error is reported under the failing record's own id");
            Assert.AreEqual("Sys.Rollback", o.Errors[A.ToString()]);
            Assert.AreEqual("Sys.Rollback", o.Errors[C.ToString()]);
        }
    }
}
