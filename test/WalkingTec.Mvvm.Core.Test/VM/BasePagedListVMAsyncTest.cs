using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    /// <summary>
    /// The optional asynchronous search of a list view model (BasePagedListVM.DoSearchAsync, ListVMExtension.GetJsonAsync, #382).
    /// The synchronous DoSearch / GetJson are not changed; the async path has to give the same result, keep an overridden DoSearch and
    /// fall back to synchronous materialization for queries whose provider is not async.
    /// </summary>
    [TestClass]
    public class BasePagedListVMAsyncTest
    {
        private readonly string _seed = Guid.NewGuid().ToString();

        public BasePagedListVMAsyncTest()
        {
            using var dc = new DataContext(_seed, DBTypeEnum.Memory);
            for (int i = 1; i <= 20; i++)
            {
                dc.Set<Student>().Add(new Student
                {
                    LoginName = "s" + i.ToString("00"),
                    Password = "p",
                    Name = "n" + (int)(i / 10),
                    IsValid = true
                });
            }
            dc.SaveChanges();
        }

        // ---- test view models ------------------------------------------------------------------------------------------------------------

        private class StudentListVM : BasePagedListVM<Student, BaseSearcher>
        {
            public int AfterCalls { get; private set; }
            public Func<StudentListVM, IOrderedQueryable<Student>> Query { get; set; }

            public override IOrderedQueryable<Student> GetSearchQuery()
            {
                if (Query != null)
                {
                    return Query(this);
                }
                return DC.Set<Student>().Where(x => x.IsValid).OrderBy(x => x.LoginName);
            }

            public override void AfterDoSearcher()
            {
                AfterCalls++;
                base.AfterDoSearcher();
            }

            protected override IEnumerable<IGridColumn<Student>> InitGridHeader()
            {
                return new List<GridColumn<Student>>
                {
                    this.MakeGridHeader(x => x.LoginName),
                    this.MakeGridHeader(x => x.Name),
                };
            }
        }

        /// <summary>Overrides only the synchronous DoSearch, with its own logic that never reaches the database.</summary>
        private class SyncOverrideListVM : StudentListVM
        {
            public int DoSearchCalls { get; private set; }

            public override void DoSearch()
            {
                DoSearchCalls++;
                EntityList = new List<Student> { new Student { ID = Guid.Parse("00000000-0000-0000-0000-000000000001"), LoginName = "from-override" } };
                Searcher.Count = 1;
                IsSearched = true;
            }
        }

        /// <summary>Overrides the synchronous DoSearch but calls the base implementation, as most real overrides do.</summary>
        private class SyncOverrideWithBaseListVM : StudentListVM
        {
            public int DoSearchCalls { get; private set; }

            public override void DoSearch()
            {
                DoSearchCalls++;
                base.DoSearch();
            }
        }

        /// <summary>Overrides only DoSearchAsync.</summary>
        private class AsyncOverrideListVM : StudentListVM
        {
            public int DoSearchCalls { get; private set; }
            public int DoSearchAsyncCalls { get; private set; }

            public override void DoSearch()
            {
                DoSearchCalls++;
                base.DoSearch();
            }

            public override Task DoSearchAsync(CancellationToken cancellationToken = default)
            {
                DoSearchAsyncCalls++;
                EntityList = new List<Student> { new Student { LoginName = "from-async-override" } };
                IsSearched = true;
                return Task.CompletedTask;
            }
        }

        // ---- a query provider that counts how it is executed -------------------------------------------------------------------------------

        private class Spy
        {
            public int SyncExecutes;
            public int SyncEnumerations;
            public int AsyncExecutes;
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

            public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
            {
                return new SpyQuery<TElement>(_inner.CreateQuery<TElement>(expression), _spy);
            }

            public object Execute(Expression expression)
            {
                _spy.SyncExecutes++;
                return _inner.Execute(expression);
            }

            public TResult Execute<TResult>(Expression expression)
            {
                _spy.SyncExecutes++;
                return _inner.Execute<TResult>(expression);
            }

            public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
            {
                _spy.AsyncExecutes++;
                _spy.Tokens.Add(cancellationToken);
                return ((IAsyncQueryProvider)_inner).ExecuteAsync<TResult>(expression, cancellationToken);
            }
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

        // ---- helpers ---------------------------------------------------------------------------------------------------------------------

        private T NewVm<T>() where T : StudentListVM, new()
        {
            var vm = new T();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            return vm;
        }

        private StudentListVM NewVm() => NewVm<StudentListVM>();

        private StudentListVM NewSpyVm(Spy spy)
        {
            var vm = NewVm();
            vm.Query = self => new SpyQuery<Student>(self.DC.Set<Student>().Where(x => x.IsValid).OrderBy(x => x.LoginName), spy);
            return vm;
        }

        private List<string> IdsOf(params string[] loginNames)
        {
            using var dc = new DataContext(_seed, DBTypeEnum.Memory);
            return dc.Set<Student>().Where(x => loginNames.Contains(x.LoginName)).Select(x => x.ID.ToString()).ToList();
        }

        private static List<string> Names(StudentListVM vm) => vm.EntityList.Select(x => x.LoginName).ToList();

        /// <summary>
        /// Runs the same configuration through DoSearch and DoSearchAsync on two fresh view models: rows (in order), Count, Page, PageCount, Limit,
        /// IsSearched and the number of AfterDoSearcher calls have to be the same. Returns the synchronous one for further assertions.
        /// </summary>
        private async Task<StudentListVM> AssertParity(Action<StudentListVM> configure, string scenario)
        {
            var sync = NewVm();
            configure(sync);
            sync.DoSearch();

            var async = NewVm();
            configure(async);
            await async.DoSearchAsync();

            CollectionAssert.AreEqual(Names(sync), Names(async), $"{scenario}: rows");
            Assert.AreEqual(sync.Searcher.Count, async.Searcher.Count, $"{scenario}: Count");
            Assert.AreEqual(sync.Searcher.Page, async.Searcher.Page, $"{scenario}: Page");
            Assert.AreEqual(sync.Searcher.PageCount, async.Searcher.PageCount, $"{scenario}: PageCount");
            Assert.AreEqual(sync.Searcher.Limit, async.Searcher.Limit, $"{scenario}: Limit");
            Assert.IsTrue(sync.IsSearched, $"{scenario}: sync IsSearched");
            Assert.IsTrue(async.IsSearched, $"{scenario}: async IsSearched");
            Assert.AreEqual(1, sync.AfterCalls, $"{scenario}: sync AfterDoSearcher");
            Assert.AreEqual(1, async.AfterCalls, $"{scenario}: async AfterDoSearcher");
            return sync;
        }

        // ---- the async path gives what the sync path gives ---------------------------------------------------------------------------------

        [TestMethod]
        public async Task Paged_SameAsSync()
        {
            var vm = await AssertParity(x => { x.Searcher.Limit = 10; x.Searcher.Page = 2; }, "paged");
            Assert.AreEqual(20, vm.Searcher.Count);
            Assert.AreEqual(2, vm.Searcher.PageCount);
            Assert.AreEqual(2, vm.Searcher.Page);
            CollectionAssert.AreEqual(Enumerable.Range(11, 10).Select(i => "s" + i.ToString("00")).ToList(), Names(vm));
        }

        [TestMethod]
        public async Task PageBeyondPageCount_IsClampedToTheLastPage()
        {
            var vm = await AssertParity(x => { x.Searcher.Limit = 5; x.Searcher.Page = 99; }, "page beyond");
            Assert.AreEqual(4, vm.Searcher.PageCount);
            Assert.AreEqual(4, vm.Searcher.Page);
            Assert.AreEqual(5, vm.EntityList.Count);
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(-1)]
        [DataRow(-100)]
        public async Task PageZeroOrNegative_IsRaisedToTheFirstPage(int page)
        {
            var vm = await AssertParity(x => { x.Searcher.Limit = 3; x.Searcher.Page = page; }, "page " + page);
            Assert.AreEqual(1, vm.Searcher.Page);
            Assert.AreEqual(7, vm.Searcher.PageCount);
            Assert.AreEqual(3, vm.EntityList.Count);
        }

        [TestMethod]
        public async Task LimitZero_UsesTheDefaultRowsPerPage()
        {
            var vm = await AssertParity(x => x.Searcher.Limit = 0, "limit 0");
            Assert.AreEqual(20, vm.Searcher.Limit);
            Assert.AreEqual(1, vm.Searcher.PageCount);
            Assert.AreEqual(20, vm.EntityList.Count);
        }

        [TestMethod]
        public async Task LimitMinusOne_ReturnsEverythingWithoutPaging()
        {
            var vm = await AssertParity(x => { x.Searcher.Limit = -1; x.Searcher.Page = 5; }, "limit -1");
            Assert.AreEqual(20, vm.Searcher.Count);
            Assert.AreEqual(20, vm.Searcher.Limit);
            Assert.AreEqual(1, vm.Searcher.PageCount);
            Assert.AreEqual(1, vm.Searcher.Page);
            Assert.AreEqual(20, vm.EntityList.Count);
        }

        [TestMethod]
        public async Task NeedPageFalse_ReturnsEverythingWithoutPaging()
        {
            var vm = await AssertParity(x => { x.NeedPage = false; x.Searcher.Limit = 5; }, "NeedPage false");
            Assert.AreEqual(20, vm.Searcher.Count);
            Assert.AreEqual(20, vm.Searcher.Limit);
            Assert.AreEqual(1, vm.Searcher.PageCount);
            Assert.AreEqual(20, vm.EntityList.Count);
        }

        [TestMethod]
        public async Task SortInfo_ReplacesTheOrder()
        {
            var vm = await AssertParity(x =>
            {
                x.Searcher.Limit = 5;
                x.Searcher.SortInfo = new SortInfo { Direction = SortDir.Desc, Property = "LoginName" };
            }, "sort");
            CollectionAssert.AreEqual(new List<string> { "s20", "s19", "s18", "s17", "s16" }, Names(vm));
        }

        [TestMethod]
        public async Task ReplaceWhere_ReplacesTheWhere()
        {
            var vm = await AssertParity(x =>
            {
                x.Searcher.Limit = 4;
                x.ReplaceWhere = (Expression<Func<Student, bool>>)(s => s.Name == "n1");
            }, "replace where");
            Assert.AreEqual(10, vm.Searcher.Count);
            Assert.AreEqual(3, vm.Searcher.PageCount);
        }

        [TestMethod]
        public async Task NoRows_PageCountIsZero()
        {
            var vm = await AssertParity(x =>
            {
                x.Searcher.Limit = 5;
                x.Searcher.Page = 3;
                x.ReplaceWhere = (Expression<Func<Student, bool>>)(s => s.Name == "nothing");
            }, "no rows");
            Assert.AreEqual(0, vm.Searcher.Count);
            Assert.AreEqual(0, vm.Searcher.PageCount);
            Assert.AreEqual(0, vm.EntityList.Count);
        }

        [TestMethod]
        public async Task PassSearch_ReturnsEverythingAndLeavesTheSearcherAlone()
        {
            var vm = await AssertParity(x => { x.PassSearch = true; x.Searcher.Limit = 5; }, "pass search");
            Assert.AreEqual(20, vm.EntityList.Count);
            Assert.AreEqual(0, vm.Searcher.Count);
            Assert.AreEqual(0, vm.Searcher.PageCount);
            Assert.AreEqual(5, vm.Searcher.Limit);
        }

        [TestMethod]
        public async Task EverySearcherMode_SameAsSync()
        {
            foreach (ListVMSearchModeEnum mode in Enum.GetValues(typeof(ListVMSearchModeEnum)))
            {
                var ids = IdsOf("s03", "s07", "s11");
                var vm = await AssertParity(x => { x.SearcherMode = mode; x.Ids = ids; x.Searcher.Limit = 10; }, mode.ToString());
                if (mode == ListVMSearchModeEnum.Batch || mode == ListVMSearchModeEnum.CheckExport)
                {
                    CollectionAssert.AreEqual(new List<string> { "s03", "s07", "s11" }, Names(vm), mode.ToString());
                }
                else
                {
                    Assert.AreEqual(20, vm.Searcher.Count, mode.ToString());
                }
            }
        }

        [TestMethod]
        public async Task BatchWithIds_ReturnsOnlyTheSelectedRows()
        {
            var ids = IdsOf("s02", "s05");
            var vm = await AssertParity(x => { x.SearcherMode = ListVMSearchModeEnum.Batch; x.Ids = ids; }, "batch");
            CollectionAssert.AreEqual(new List<string> { "s02", "s05" }, Names(vm));
            Assert.AreEqual(2, vm.Searcher.Count);
        }

        [TestMethod]
        public async Task DoSearchAsync_MarksSearchedAndCallsAfterDoSearcherOnce()
        {
            var vm = NewVm();
            Assert.IsFalse(vm.IsSearched);
            await vm.DoSearchAsync();
            Assert.IsTrue(vm.IsSearched);
            Assert.AreEqual(1, vm.AfterCalls);
        }

        // ---- override preservation -------------------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task SubclassOverridingOnlyDoSearch_DoSearchAsyncRunsTheOverride()
        {
            var vm = NewVm<SyncOverrideListVM>();
            await vm.DoSearchAsync();
            Assert.AreEqual(1, vm.DoSearchCalls);
            CollectionAssert.AreEqual(new List<string> { "from-override" }, Names(vm));
            Assert.IsTrue(vm.IsSearched);
        }

        [TestMethod]
        public async Task SubclassOverridingDoSearchAndCallingBase_DoSearchAsyncRunsTheOverrideAndTheBaseLogic()
        {
            var vm = NewVm<SyncOverrideWithBaseListVM>();
            vm.Searcher.Limit = 10;
            await vm.DoSearchAsync();
            Assert.AreEqual(1, vm.DoSearchCalls);
            Assert.AreEqual(10, vm.EntityList.Count);
            Assert.AreEqual(20, vm.Searcher.Count);
            Assert.AreEqual(1, vm.AfterCalls);
        }

        [TestMethod]
        public async Task SubclassOverridingDoSearchAsync_RunsItsOwnAndNotDoSearch()
        {
            var vm = NewVm<AsyncOverrideListVM>();
            await vm.DoSearchAsync();
            Assert.AreEqual(1, vm.DoSearchAsyncCalls);
            Assert.AreEqual(0, vm.DoSearchCalls);
            CollectionAssert.AreEqual(new List<string> { "from-async-override" }, Names(vm));
        }

        [TestMethod]
        public async Task SubclassOverridingNeither_RunsTheAsyncPath()
        {
            var spy = new Spy();
            var vm = NewSpyVm(spy);
            vm.Searcher.Limit = 10;
            await vm.DoSearchAsync();
            // the count and the page are both executed asynchronously, nothing synchronously
            Assert.AreEqual(1, spy.AsyncExecutes);
            Assert.AreEqual(1, spy.AsyncEnumerations);
            Assert.AreEqual(0, spy.SyncExecutes);
            Assert.AreEqual(0, spy.SyncEnumerations);
            Assert.AreEqual(10, vm.EntityList.Count);
            Assert.AreEqual(20, vm.Searcher.Count);
        }

        [TestMethod]
        public void SyncDoSearch_IsStillSynchronous()
        {
            var spy = new Spy();
            var vm = NewSpyVm(spy);
            vm.Searcher.Limit = 10;
            vm.DoSearch();
            Assert.AreEqual(1, spy.SyncExecutes);
            Assert.AreEqual(1, spy.SyncEnumerations);
            Assert.AreEqual(0, spy.AsyncExecutes);
            Assert.AreEqual(0, spy.AsyncEnumerations);
        }

        [TestMethod]
        public async Task TheAsyncPath_PassesTheTokenToEveryAsyncCall()
        {
            var spy = new Spy();
            var vm = NewSpyVm(spy);
            vm.Searcher.Limit = 10;
            using var cts = new CancellationTokenSource();
            await vm.DoSearchAsync(cts.Token);
            Assert.AreEqual(2, spy.Tokens.Count);
            Assert.IsTrue(spy.Tokens.All(t => t == cts.Token));
        }

        // ---- queries whose provider is not async -------------------------------------------------------------------------------------------

        private static IOrderedQueryable<Student> InMemoryList()
        {
            return Enumerable.Range(1, 12).Select(i => new Student { LoginName = "m" + i.ToString("00"), Name = "x" }).AsQueryable().OrderBy(x => x.LoginName);
        }

        [TestMethod]
        public async Task QueryWithoutAsyncProvider_FallsBackToSynchronousMaterialization()
        {
            var vm = NewVm();
            vm.Query = _ => InMemoryList();
            vm.Searcher.Limit = 5;
            vm.Searcher.Page = 3;
            await vm.DoSearchAsync();
            Assert.AreEqual(12, vm.Searcher.Count);
            Assert.AreEqual(3, vm.Searcher.PageCount);
            CollectionAssert.AreEqual(new List<string> { "m11", "m12" }, Names(vm));
            Assert.IsTrue(vm.IsSearched);
        }

        [TestMethod]
        public async Task QueryWithoutAsyncProvider_NoPagingAndSortAndPassSearch()
        {
            var vm = NewVm();
            vm.Query = _ => InMemoryList();
            vm.NeedPage = false;
            vm.Searcher.SortInfo = new SortInfo { Direction = SortDir.Desc, Property = "LoginName" };
            await vm.DoSearchAsync();
            Assert.AreEqual(12, vm.Searcher.Count);
            Assert.AreEqual(12, vm.Searcher.Limit);
            Assert.AreEqual("m12", vm.EntityList[0].LoginName);

            var pass = NewVm();
            pass.Query = _ => InMemoryList();
            pass.PassSearch = true;
            await pass.DoSearchAsync();
            Assert.AreEqual(12, pass.EntityList.Count);
        }

        // ---- cancellation ------------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task CancelledToken_ThrowsOperationCanceledAndDoesNotSearch()
        {
            var vm = NewVm();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await vm.DoSearchAsync(cts.Token));
            Assert.IsFalse(vm.IsSearched);
            Assert.AreEqual(0, vm.AfterCalls);
        }

        [TestMethod]
        public async Task CancelledToken_GetJsonAsyncThrowsOperationCanceled()
        {
            var vm = NewVm();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await vm.GetJsonAsync(cancellationToken: cts.Token));
        }

        // ---- the interface ------------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task InterfaceDefault_CallsDoSearch()
        {
            // an implementation of IBasePagedListVM that knows nothing about DoSearchAsync (the default interface method is used)
            var calls = 0;
            var fake = new Mock<IBasePagedListVM<Student, BaseSearcher>> { CallBase = true };
            fake.Setup(x => x.DoSearch()).Callback(() => calls++);
            await fake.Object.DoSearchAsync();
            Assert.AreEqual(1, calls);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await fake.Object.DoSearchAsync(cts.Token));
            Assert.AreEqual(1, calls);
        }

        [TestMethod]
        public async Task BasePagedListVM_ImplementsTheInterfaceMemberItself()
        {
            // called through the interface, the real implementation (the async path) is used and not the default one
            var spy = new Spy();
            IBasePagedListVM<Student, BaseSearcher> vm = NewSpyVm(spy);
            await vm.DoSearchAsync();
            Assert.AreEqual(1, spy.AsyncExecutes);
            Assert.AreEqual(0, spy.SyncExecutes);
        }

        // ---- GetJsonAsync -------------------------------------------------------------------------------------------------------------------

        [TestMethod]
        [DataRow(true, true)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(false, false)]
        public async Task GetJsonAsync_SameStringAsGetJson(bool plainText, bool enumToString)
        {
            var sync = NewVm();
            sync.Searcher.Limit = 7;
            sync.Searcher.Page = 2;
            var async = NewVm();
            async.Searcher.Limit = 7;
            async.Searcher.Page = 2;

            var expected = sync.GetJson(plainText, enumToString);
            var actual = await async.GetJsonAsync(plainText, enumToString);
            Assert.AreEqual(expected, actual);
            StringAssert.Contains(actual, "\"Count\":20,");
            StringAssert.Contains(actual, "\"PageCount\":3");
        }

        [TestMethod]
        public async Task GetJsonAsync_SameStringAsGetJson_WithExtraValues()
        {
            Func<Dictionary<string, object>> extra = () => new Dictionary<string, object> { { "Tag", "a" }, { "N", 3 } };
            var sync = NewVm();
            var async = NewVm();
            var expected = sync.GetJson(true, true, extra);
            var actual = await async.GetJsonAsync(true, true, extra);
            Assert.AreEqual(expected, actual);
            StringAssert.StartsWith(actual, "{\"Tag\":\"a\",\"N\":\"3\",\"Code\":200,");
        }

        [TestMethod]
        public async Task GetJsonAsync_SearcherFlagsOverrideTheArguments_LikeGetJson()
        {
            var sync = NewVm();
            sync.Searcher.IsPlainText = false;
            sync.Searcher.IsEnumToString = false;
            var async = NewVm();
            async.Searcher.IsPlainText = false;
            async.Searcher.IsEnumToString = false;
            Assert.AreEqual(sync.GetJson(true, true), await async.GetJsonAsync(true, true));
        }

        [TestMethod]
        public async Task GetJsonAsync_DoesNotSearchAgainWhenAlreadySearched()
        {
            var vm = NewVm();
            vm.Searcher.Limit = 5;
            vm.DoSearch();
            Assert.AreEqual(1, vm.AfterCalls);
            var before = Names(vm);

            var json = await vm.GetJsonAsync();
            Assert.AreEqual(1, vm.AfterCalls);
            CollectionAssert.AreEqual(before, Names(vm));
            Assert.AreEqual(vm.GetJson(), json);
        }

        [TestMethod]
        public async Task GetJsonAsync_SearchesThroughTheAsyncPath()
        {
            var spy = new Spy();
            var vm = NewSpyVm(spy);
            vm.Searcher.Limit = 5;
            var json = await vm.GetJsonAsync();
            Assert.AreEqual(1, spy.AsyncExecutes);
            Assert.AreEqual(1, spy.AsyncEnumerations);
            Assert.AreEqual(0, spy.SyncExecutes);
            Assert.AreEqual(0, spy.SyncEnumerations);
            StringAssert.Contains(json, "\"Count\":20,");
        }

        [TestMethod]
        public async Task GetJsonAsync_KeepsAnOverriddenDoSearch()
        {
            var vm = NewVm<SyncOverrideListVM>();
            var json = await vm.GetJsonAsync();
            Assert.AreEqual(1, vm.DoSearchCalls);
            StringAssert.Contains(json, "from-override");
            Assert.AreEqual(NewVm<SyncOverrideListVM>().GetJson(), json);
        }
    }
}
