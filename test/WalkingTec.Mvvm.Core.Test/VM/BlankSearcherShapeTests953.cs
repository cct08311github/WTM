#nullable enable
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    // ---------------------------------------------------------------------------
    // Fixtures — mirror two in-tree demo shapes an adversarial review of #947/PR #953
    // found: a Where predicate that reads Searcher WITHOUT going through a
    // Check*/guard-then-add helper. BasePagedListVM.GetAuthorizedIdsQuery's blank-Searcher
    // swap (added by #947 to keep row-level DataPrivilege intact in Batch mode) does not
    // reliably suppress either shape. Neither is a security regression (both fail closed),
    // but both are silent compatibility changes from pre-#947 behaviour. These fixtures use
    // an injected in-memory IQueryable (ExportStudentListVM's existing pattern in
    // BasePagedListVMExtendedTests.cs) rather than a DbSet, so no new DbSet needs
    // registering on the shared test DataContext — the deferred-execution/closure-timing
    // behaviour under test is a property of IQueryable itself, identical for
    // EnumerableQuery<T> (used here) and an EF Core provider (used in the real demo VMs).
    // ---------------------------------------------------------------------------

    public class LazyShapeEntity : TopBasePoco
    {
        public string? Category { get; set; }
    }

    public class LazyShapeSearcher : BaseSearcher
    {
        public string? Category { get; set; }
    }

    /// <summary>
    /// Mirrors <c>demo/WalkingTec.Mvvm.Demo/ViewModels/MajorVMs/MajorDetailListVM.
    /// GetSearchQuery()</c>'s shape exactly: <c>.Where(x =&gt; Searcher.SchoolId ==
    /// x.SchoolId)</c>. <c>Searcher</c> here is a property access on the VM instance
    /// (<c>this</c>), read inside a LINQ lambda — evaluated at query EXECUTION
    /// (enumeration) time, not at <c>GetSearchQuery()</c>-call time.
    /// </summary>
    public class LazyShapeListVM : BasePagedListVM<LazyShapeEntity, LazyShapeSearcher>
    {
        private readonly IQueryable<LazyShapeEntity> _data;

        public LazyShapeListVM(IQueryable<LazyShapeEntity> data) => _data = data;

        public override IOrderedQueryable<LazyShapeEntity> GetSearchQuery()
            => _data.Where(x => Searcher.Category == x.Category).OrderBy(x => x.ID);
    }

    public class EagerShapeEntity : TopBasePoco
    {
        public string? ParentKey { get; set; }
    }

    public class EagerShapeSearcher : BaseSearcher
    {
        public string? ParentKey { get; set; }
    }

    /// <summary>
    /// Mirrors <c>demo/WalkingTec.Mvvm.Demo/ViewModels/CityVMs/CityChildrenDetailListVM.
    /// GetSearchQuery()</c>'s shape exactly: a plain C# statement reads
    /// <c>Searcher.ParentId</c> eagerly and returns an empty in-memory list when it is
    /// null — evaluated synchronously while <c>GetSearchQuery()</c> runs, i.e. while the
    /// blank-Searcher swap is in effect.
    /// </summary>
    public class EagerShapeListVM : BasePagedListVM<EagerShapeEntity, EagerShapeSearcher>
    {
        private readonly IQueryable<EagerShapeEntity> _data;

        public EagerShapeListVM(IQueryable<EagerShapeEntity> data) => _data = data;

        public override IOrderedQueryable<EagerShapeEntity> GetSearchQuery()
        {
            if (string.IsNullOrEmpty(Searcher.ParentKey))
                return new List<EagerShapeEntity>().AsQueryable().OrderBy(x => x.ID);
            return _data.Where(x => x.ParentKey == Searcher.ParentKey).OrderBy(x => x.ID);
        }
    }

    /// <summary>
    /// Issue #953 (adversarial review of #947 / PR #953) finding F1 — regression tests
    /// pinning the TRUE (narrower than originally documented) invariant of
    /// <see cref="BasePagedListVM{T,S}"/>'s blank-Searcher swap, verified with a runnable
    /// reproduction before being written up here (see the review's own repro and this PR's
    /// standalone confirmation in scratchpad/f1repro before these tests were added).
    /// </summary>
    [TestClass]
    public class BlankSearcherShapeTests953
    {
        /// <summary>
        /// F1(a) — lazily-evaluated Searcher-reading predicate: the blank-Searcher swap is
        /// a no-op, because <c>Searcher</c> is read at query enumeration time, which
        /// happens after <c>GetAuthorizedIdsQuery</c>'s <c>finally</c> has already restored
        /// the real, request-bound Searcher. A row named directly in <c>Ids</c> can still
        /// silently disappear if it does not match the CURRENT UI search criteria — exactly
        /// the behaviour #947 intended Batch mode to avoid for guard-then-add predicates,
        /// but does not achieve for this shape.
        /// </summary>
        [TestMethod]
        public void GetBatchQuery_LazySearcherClosurePredicate_LiveSearcherValueStillApplied()
        {
            var matching = new LazyShapeEntity { ID = System.Guid.NewGuid(), Category = "A" };
            var nonMatching = new LazyShapeEntity { ID = System.Guid.NewGuid(), Category = "B" };
            var data = new List<LazyShapeEntity> { matching, nonMatching }.AsQueryable();

            var vm = new LazyShapeListVM(data)
            {
                Wtm = MockWtmContext.CreateWtmContext()
            };
            vm.Searcher.Category = "A"; // request-bound, live UI search criteria
            vm.Ids = [matching.ID.ToString(), nonMatching.ID.ToString()]; // BOTH named directly
            vm.SearcherMode = ListVMSearchModeEnum.Batch;
            vm.NeedPage = false;

            vm.DoSearch();

            // If the blank-Searcher swap fully suppressed this Where, both rows named in
            // Ids would come back (Batch mode's whole point). It does not: only the row
            // matching the LIVE Searcher.Category comes back.
            Assert.AreEqual(1, vm.EntityList.Count,
                "#953 F1(a): a lazily-evaluated Searcher-reading predicate still filters " +
                "by the live UI search criteria in Batch mode even when both rows are " +
                "named in Ids. If this assertion ever needs to become 2, the fix is the " +
                "marker-tag redesign tracked separately from #947 (tag DPWhere's Where so " +
                "WhereReplaceModifier can skip exactly that node), not a silent tweak to " +
                "GetAuthorizedIdsQuery's blank-Searcher swap.");
            Assert.AreEqual(matching.ID, vm.EntityList[0].ID);
        }

        /// <summary>
        /// F1(b) — eagerly-evaluated, build-time Searcher-reading branch: with the blank
        /// Searcher, the entity's own <c>GetSearchQuery()</c> unconditionally takes the
        /// "no ParentKey" branch and returns an empty in-memory list; the Ids restriction
        /// is then ANDed onto that empty query. Zero rows, always — regardless of the real
        /// Searcher's value or which rows were named in Ids. Fails closed (not a security
        /// regression), but silently hides rows a caller explicitly selected.
        /// </summary>
        [TestMethod]
        public void GetBatchQuery_EagerSearcherBuildTimeBranch_CollapsesToEmpty()
        {
            var entity = new EagerShapeEntity { ID = System.Guid.NewGuid(), ParentKey = "parent-1" };
            var data = new List<EagerShapeEntity> { entity }.AsQueryable();

            var vm = new EagerShapeListVM(data)
            {
                Wtm = MockWtmContext.CreateWtmContext()
            };
            vm.Searcher.ParentKey = "parent-1"; // request-bound, live -- would normally match
            vm.Ids = [entity.ID.ToString()]; // explicitly named
            vm.SearcherMode = ListVMSearchModeEnum.Batch;
            vm.NeedPage = false;

            vm.DoSearch();

            Assert.AreEqual(0, vm.EntityList.Count,
                "#953 F1(b): a build-time Searcher-reading branch collapses the query to " +
                "an empty in-memory list under the blank-Searcher swap, even though the " +
                "entity was explicitly named in Ids and the real Searcher.ParentKey would " +
                "have matched it. If this assertion ever needs to become 1, the fix is the " +
                "marker-tag redesign tracked separately from #947, not a silent tweak to " +
                "GetAuthorizedIdsQuery's blank-Searcher swap.");
        }
    }
}
