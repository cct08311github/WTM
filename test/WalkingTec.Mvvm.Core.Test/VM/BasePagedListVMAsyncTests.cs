#nullable enable
// Tests for #412 — DoSearchAsync async list-query path on BasePagedListVM.
//
// Uses SQLite shared-memory (NOT EF InMemory) because EF Core's CountAsync /
// ToListAsync are not supported by the InMemory provider in all query shapes.
//
// A minimal flat entity (AsyncListItem) with no navigation properties is used to
// avoid the "duplicate column name: MajorId" EnsureCreated collision that occurs
// when the base FrameworkContext.OnModelCreating scans the test assembly and finds
// conflicting School/Major FK relationships.

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // Minimal flat entity — no navigation properties, no FK deps
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// Flat entity for async-list tests.
    /// No navigation properties so EnsureCreated does not collide with
    /// School/Major FK relationships in the test project.
    /// </summary>
    public class AsyncListItem : BasePoco
    {
        [StringLength(50)]
        public string Label { get; set; } = "";

        [StringLength(20)]
        public string Category { get; set; } = "";
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // SQLite shared-memory DataContext — only AsyncListItem, no FK collision
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// EF DataContext for #412 async tests.
    /// Overrides OnModelCreating to register ONLY AsyncListItem — does NOT call
    /// base.OnModelCreating() so the assembly scan in FrameworkContext is skipped,
    /// preventing the "duplicate column name: MajorId" schema collision.
    /// </summary>
    internal sealed class AsyncListContext : EmptyContext
    {
        public DbSet<AsyncListItem> AsyncListItems { get; set; } = null!;

        public AsyncListContext(string connStr) : base(connStr, DBTypeEnum.SQLite) { }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseSqlite(CSName);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Register ONLY AsyncListItem — skip base call to avoid assembly-scan collision.
            modelBuilder.Entity<AsyncListItem>(b =>
            {
                b.HasKey(e => e.ID);
                b.Property(e => e.Label).HasMaxLength(50);
                b.Property(e => e.Category).HasMaxLength(20);
                b.Property(e => e.CreateTime);
                b.Property(e => e.CreateBy).HasMaxLength(50);
                b.Property(e => e.UpdateTime);
                b.Property(e => e.UpdateBy).HasMaxLength(50);
            });
            // Suppress AnalysisSavedQuery inherited from EmptyContext.
            modelBuilder.Ignore<AnalysisSavedQuery>();
        }
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // Concrete ListVM used by #412 tests
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    internal sealed class AsyncListVM : BasePagedListVM<AsyncListItem, BaseSearcher>
    {
        public override IOrderedQueryable<AsyncListItem> GetSearchQuery()
            => DC!.Set<AsyncListItem>().OrderBy(x => x.Label);
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // DoSearchAsync tests — #412
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    [TestClass]
    public class BasePagedListVMAsyncTests
    {
        // Keep-alive connection so the shared-memory database survives across
        // multiple DbContext lifetimes within a single test.
        private string _connStr = null!;
        private SqliteConnection _keepAlive = null!;

        [TestInitialize]
        public void Init()
        {
            var dbName = $"AsyncListTest_{Guid.NewGuid():N}";
            _connStr = $"DataSource={dbName}?mode=memory&cache=shared";
            _keepAlive = new SqliteConnection(_connStr);
            _keepAlive.Open();

            using var ctx = new AsyncListContext(_connStr);
            ctx.Database.EnsureCreated();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _keepAlive?.Close();
            _keepAlive?.Dispose();
        }

        // Create a new context instance (re-uses the same named in-memory database).
        private AsyncListContext NewCtx() => new AsyncListContext(_connStr);

        private WTMContext CreateWtm() => MockWtmContext.CreateWtmContext(NewCtx());

        private void Seed(int count)
        {
            using var ctx = NewCtx();
            for (int i = 0; i < count; i++)
            {
                ctx.Set<AsyncListItem>().Add(new AsyncListItem
                {
                    Label = $"item{i:D3}",
                    Category = (i % 2 == 0) ? "even" : "odd"
                });
            }
            ctx.SaveChanges();
        }

        // ─── Parity: DoSearchAsync produces identical results to DoSearch ────────

        [TestMethod]
        public async Task DoSearchAsync_WithPaging_MatchesDoSearch()
        {
            Seed(20);

            var syncVm = new AsyncListVM { Wtm = CreateWtm() };
            syncVm.Searcher.Limit = 7;
            syncVm.DoSearch();

            var asyncVm = new AsyncListVM { Wtm = CreateWtm() };
            asyncVm.Searcher.Limit = 7;
            await asyncVm.DoSearchAsync().ConfigureAwait(false);

            Assert.AreEqual(syncVm.Searcher.Count, asyncVm.Searcher.Count,
                "Count must match between sync and async");
            Assert.AreEqual(syncVm.Searcher.PageCount, asyncVm.Searcher.PageCount,
                "PageCount must match");
            Assert.AreEqual(syncVm.EntityList.Count, asyncVm.EntityList.Count,
                "EntityList.Count must match");
            Assert.AreEqual(syncVm.IsSearched, asyncVm.IsSearched);
        }

        [TestMethod]
        public async Task DoSearchAsync_WithNoPaging_MatchesDoSearch()
        {
            Seed(15);

            var syncVm = new AsyncListVM { Wtm = CreateWtm(), NeedPage = false };
            syncVm.DoSearch();

            var asyncVm = new AsyncListVM { Wtm = CreateWtm(), NeedPage = false };
            await asyncVm.DoSearchAsync().ConfigureAwait(false);

            Assert.AreEqual(syncVm.Searcher.Count, asyncVm.Searcher.Count);
            Assert.AreEqual(syncVm.EntityList.Count, asyncVm.EntityList.Count);
            Assert.AreEqual(syncVm.Searcher.PageCount, asyncVm.Searcher.PageCount);
            Assert.AreEqual(syncVm.Searcher.Page, asyncVm.Searcher.Page);
        }

        [TestMethod]
        public async Task DoSearchAsync_Page2_MatchesDoSearch()
        {
            Seed(20);

            var syncVm = new AsyncListVM { Wtm = CreateWtm() };
            syncVm.Searcher.Limit = 5;
            syncVm.Searcher.Page = 2;
            syncVm.DoSearch();

            var asyncVm = new AsyncListVM { Wtm = CreateWtm() };
            asyncVm.Searcher.Limit = 5;
            asyncVm.Searcher.Page = 2;
            await asyncVm.DoSearchAsync().ConfigureAwait(false);

            Assert.AreEqual(syncVm.Searcher.Count, asyncVm.Searcher.Count);
            Assert.AreEqual(syncVm.Searcher.Page, asyncVm.Searcher.Page);
            Assert.AreEqual(syncVm.EntityList.Count, asyncVm.EntityList.Count);
        }

        [TestMethod]
        public async Task DoSearchAsync_WithSortInfo_MatchesDoSearch()
        {
            Seed(10);

            var sortInfo = new SortInfo { Direction = SortDir.Asc, Property = "Label" };

            var syncVm = new AsyncListVM { Wtm = CreateWtm(), NeedPage = false };
            syncVm.Searcher.SortInfo = sortInfo;
            syncVm.DoSearch();

            var asyncVm = new AsyncListVM { Wtm = CreateWtm(), NeedPage = false };
            asyncVm.Searcher.SortInfo = sortInfo;
            await asyncVm.DoSearchAsync().ConfigureAwait(false);

            Assert.AreEqual(syncVm.EntityList.Count, asyncVm.EntityList.Count);
            // Both should have the same first element when sorted asc by Label.
            Assert.AreEqual(syncVm.EntityList[0].Label, asyncVm.EntityList[0].Label);
        }

        [TestMethod]
        public async Task DoSearchAsync_WithReplaceWhere_MatchesDoSearch()
        {
            Seed(10);

            Expression<Func<AsyncListItem, bool>> filter = s => s.Category == "even";

            var syncVm = new AsyncListVM { Wtm = CreateWtm(), NeedPage = false };
            syncVm.ReplaceWhere = filter;
            syncVm.DoSearch();

            var asyncVm = new AsyncListVM { Wtm = CreateWtm(), NeedPage = false };
            asyncVm.ReplaceWhere = filter;
            await asyncVm.DoSearchAsync().ConfigureAwait(false);

            Assert.AreEqual(syncVm.EntityList.Count, asyncVm.EntityList.Count,
                "ReplaceWhere must filter identically in async path");
        }

        [TestMethod]
        public async Task DoSearchAsync_SearcherMode_Export_MatchesDoSearch()
        {
            Seed(8);

            var syncVm = new AsyncListVM { Wtm = CreateWtm(), NeedPage = false };
            syncVm.SearcherMode = ListVMSearchModeEnum.Export;
            syncVm.DoSearch();

            var asyncVm = new AsyncListVM { Wtm = CreateWtm(), NeedPage = false };
            asyncVm.SearcherMode = ListVMSearchModeEnum.Export;
            await asyncVm.DoSearchAsync().ConfigureAwait(false);

            Assert.AreEqual(syncVm.EntityList.Count, asyncVm.EntityList.Count);
        }

        [TestMethod]
        public async Task DoSearchAsync_PassSearch_True_MatchesDoSearch()
        {
            Seed(12);

            var syncVm = new AsyncListVM { Wtm = CreateWtm(), PassSearch = true };
            syncVm.DoSearch();

            var asyncVm = new AsyncListVM { Wtm = CreateWtm(), PassSearch = true };
            await asyncVm.DoSearchAsync().ConfigureAwait(false);

            Assert.AreEqual(syncVm.EntityList.Count, asyncVm.EntityList.Count);
        }

        [TestMethod]
        public async Task DoSearchAsync_SetsIsSearched_True()
        {
            Seed(3);
            var vm = new AsyncListVM { Wtm = CreateWtm() };
            Assert.IsFalse(vm.IsSearched);
            await vm.DoSearchAsync().ConfigureAwait(false);
            Assert.IsTrue(vm.IsSearched);
        }

        [TestMethod]
        public async Task DoSearchAsync_EmptyTable_MatchesDoSearch()
        {
            // No data — both must return empty lists.
            var syncVm = new AsyncListVM { Wtm = CreateWtm(), NeedPage = false };
            syncVm.DoSearch();

            var asyncVm = new AsyncListVM { Wtm = CreateWtm(), NeedPage = false };
            await asyncVm.DoSearchAsync().ConfigureAwait(false);

            Assert.AreEqual(0, syncVm.EntityList.Count);
            Assert.AreEqual(0, asyncVm.EntityList.Count);
        }

        [TestMethod]
        public async Task DoSearchAsync_PageBeyondCount_ClampedToLastPage()
        {
            Seed(5);

            var syncVm = new AsyncListVM { Wtm = CreateWtm() };
            syncVm.Searcher.Limit = 10;
            syncVm.Searcher.Page = 999;
            syncVm.DoSearch();

            var asyncVm = new AsyncListVM { Wtm = CreateWtm() };
            asyncVm.Searcher.Limit = 10;
            asyncVm.Searcher.Page = 999;
            await asyncVm.DoSearchAsync().ConfigureAwait(false);

            Assert.AreEqual(syncVm.Searcher.Page, asyncVm.Searcher.Page,
                "Page beyond count should be clamped identically");
        }

        [TestMethod]
        public async Task DoSearchAsync_NegativePage_FixedToFirstPage()
        {
            Seed(5);

            var syncVm = new AsyncListVM { Wtm = CreateWtm() };
            syncVm.Searcher.Limit = 3;
            syncVm.Searcher.Page = -5;
            syncVm.DoSearch();

            var asyncVm = new AsyncListVM { Wtm = CreateWtm() };
            asyncVm.Searcher.Limit = 3;
            asyncVm.Searcher.Page = -5;
            await asyncVm.DoSearchAsync().ConfigureAwait(false);

            Assert.AreEqual(1, syncVm.Searcher.Page);
            Assert.AreEqual(1, asyncVm.Searcher.Page);
            Assert.AreEqual(syncVm.EntityList.Count, asyncVm.EntityList.Count);
        }

        // ─── Cancellation test ───────────────────────────────────────────────────

        [TestMethod]
        public async Task DoSearchAsync_Cancelled_ThrowsCancellationException()
        {
            Seed(5);

            using var cts = new CancellationTokenSource();
            // Cancel before the call so the async path honours it immediately.
            cts.Cancel();

            var vm = new AsyncListVM { Wtm = CreateWtm(), NeedPage = false };

            // TaskCanceledException (thrown by EF Core async operators) derives from
            // OperationCanceledException; we catch either to verify cancellation propagation.
            bool cancelled = false;
            try
            {
                await vm.DoSearchAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            Assert.IsTrue(cancelled, "DoSearchAsync must propagate cancellation as OperationCanceledException or TaskCanceledException");
        }

        // ─── Interface: DoSearchAsync is reachable through IBasePagedListVM<T, S> ─

        [TestMethod]
        public async Task Interface_DoSearchAsync_IsCallable()
        {
            Seed(4);

            IBasePagedListVM<AsyncListItem, BaseSearcher> vm = new AsyncListVM { Wtm = CreateWtm() };
            vm.NeedPage = false;
            await vm.DoSearchAsync().ConfigureAwait(false);

            Assert.IsTrue(vm.IsSearched);
            Assert.AreEqual(4, vm.GetEntityList().Count());
        }
    }
}
