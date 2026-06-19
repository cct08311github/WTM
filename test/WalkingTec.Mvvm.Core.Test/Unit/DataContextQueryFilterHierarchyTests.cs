#nullable enable
using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Unit
{
    // ── Test entities ──────────────────────────────────────────────────────────

    /// <summary>
    /// A concrete (non-abstract) intermediate entity that implements ITenant.
    /// Child : ConcreteHierarchyParent : BasePoco represents the hierarchy that
    /// the old CLR-BaseType check missed when deciding where to apply the EF
    /// global query filter.
    /// </summary>
    internal class ConcreteHierarchyParent : BasePoco, ITenant
    {
        public string? TenantCode { get; set; }
        public string Label { get; set; } = "";
    }

    /// <summary>
    /// Leaf entity in the hierarchy; inherits TenantCode from ConcreteHierarchyParent.
    /// </summary>
    internal class ConcreteHierarchyChild : ConcreteHierarchyParent
    {
        public string Extra { get; set; } = "";
    }

    /// <summary>
    /// DataContext sub-class that exposes both DbSets so Utils.GetAllModels()
    /// picks them up and DataContext.OnModelCreating can build the full EF
    /// hierarchy before applying the global query filter on the root type.
    /// </summary>
    internal class ConcreteHierarchyChildContext : WalkingTec.Mvvm.Core.Test.DataContext
    {
        public DbSet<ConcreteHierarchyParent> ConcreteHierarchyParents { get; set; } = null!;
        public DbSet<ConcreteHierarchyChild> ConcreteHierarchyChildren { get; set; } = null!;
        public ConcreteHierarchyChildContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the two-pass EF-metadata query filter registration correctly
    /// applies the tenant filter on the EF root type of a concrete-intermediate
    /// hierarchy (Child : ConcreteParent : BasePoco), covering the case the old
    /// CLR BaseType check missed.
    /// </summary>
    [TestClass]
    public class DataContextQueryFilterHierarchyTests
    {
        [TestMethod]
        [Description("EF root query filter must exclude entities from a different tenant (concrete hierarchy)")]
        public void QueryFilter_ConcreteHierarchy_TenantIsolation_ChildSeenOnlyByOwnTenant()
        {
            var seed = Guid.NewGuid().ToString("N");

            // Seed: add a child entity for TENANT_B, bypassing the query filter
            using (var seedCtx = new ConcreteHierarchyChildContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                seedCtx.ConcreteHierarchyChildren.Add(new ConcreteHierarchyChild
                {
                    TenantCode = "TENANT_B",
                    Label = "from-b",
                    Extra = "extra-b"
                });
                seedCtx.SaveChanges();
            }

            // Query as TENANT_A — should see zero results
            using (var queryCtx = new ConcreteHierarchyChildContext(seed, DBTypeEnum.Memory))
            {
                queryCtx.SetTenantCode("TENANT_A");
                var results = queryCtx.Set<ConcreteHierarchyChild>().ToList();
                Assert.AreEqual(0, results.Count,
                    "TENANT_A must not see entities seeded for TENANT_B " +
                    "(concrete-hierarchy EF root query filter must be active)");
            }
        }

        [TestMethod]
        [Description("EF root query filter must allow entities belonging to the same tenant (concrete hierarchy)")]
        public void QueryFilter_ConcreteHierarchy_TenantIsolation_SameTenantCanSeeOwnEntities()
        {
            var seed = Guid.NewGuid().ToString("N");

            // Seed: add a child entity for TENANT_A
            using (var seedCtx = new ConcreteHierarchyChildContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                seedCtx.ConcreteHierarchyChildren.Add(new ConcreteHierarchyChild
                {
                    TenantCode = "TENANT_A",
                    Label = "from-a",
                    Extra = "extra-a"
                });
                seedCtx.SaveChanges();
            }

            // Query as TENANT_A — should see exactly 1 result
            using (var queryCtx = new ConcreteHierarchyChildContext(seed, DBTypeEnum.Memory))
            {
                queryCtx.SetTenantCode("TENANT_A");
                var results = queryCtx.Set<ConcreteHierarchyChild>().ToList();
                Assert.AreEqual(1, results.Count,
                    "TENANT_A must see exactly one entity seeded for TENANT_A " +
                    "(concrete-hierarchy EF root query filter must allow same-tenant rows)");
            }
        }
    }

    // ── Combined-filter test entities ──────────────────────────────────────────

    /// <summary>
    /// Entity that implements BOTH <see cref="IPersistPoco"/> (soft-delete via IsValid)
    /// AND <see cref="ITenant"/> (tenant isolation via TenantCode).
    /// Tests that both EF global query filters apply simultaneously (#382 / #383).
    /// </summary>
    internal class CombinedFilterParent : BasePoco, IPersistPoco, ITenant
    {
        public bool IsValid { get; set; } = true;
        public string? TenantCode { get; set; }
        public string Label { get; set; } = "";
    }

    /// <summary>
    /// Leaf entity inheriting from <see cref="CombinedFilterParent"/>.
    /// Both IsValid and TenantCode are inherited from the parent.
    /// </summary>
    internal class CombinedFilterChild : CombinedFilterParent
    {
        public string Tag { get; set; } = "";
    }

    /// <summary>
    /// DataContext exposing both DbSets so EF OnModelCreating processes the full
    /// hierarchy and applies the combined global query filters at the EF root type.
    /// </summary>
    internal class CombinedFilterChildContext : WalkingTec.Mvvm.Core.Test.DataContext
    {
        public DbSet<CombinedFilterParent> CombinedFilterParents { get; set; } = null!;
        public DbSet<CombinedFilterChild> CombinedFilterChildren { get; set; } = null!;
        public CombinedFilterChildContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
    }

    // ── Combined-filter tests ──────────────────────────────────────────────────

    /// <summary>
    /// Verifies that entities implementing both <see cref="IPersistPoco"/> and
    /// <see cref="ITenant"/> have both the soft-delete (IsValid) and tenant isolation
    /// (TenantCode) global query filters applied simultaneously.
    ///
    /// This covers the case where Issue #382 added the tenant-only filter test;
    /// these tests guard the combined-filter scenario.
    /// </summary>
    [TestClass]
    public class DataContextCombinedFilterHierarchyTests
    {
        [TestMethod]
        [Description(
            "Combined filter: a soft-deleted entity (IsValid=false) must be excluded " +
            "even when queried by the entity's own tenant (#382 combined-filter coverage).")]
        public void CombinedFilter_SoftDeleted_ExcludedFromBothTenantsQuery()
        {
            var seed = Guid.NewGuid().ToString("N");

            // Seed: add a soft-deleted child for TENANT_A (bypassing filters via seed ctx)
            using (var seedCtx = new CombinedFilterChildContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                seedCtx.CombinedFilterChildren.Add(new CombinedFilterChild
                {
                    IsValid = false,      // soft-deleted
                    TenantCode = "TENANT_A",
                    Label = "deleted-a",
                    Tag = "tag-deleted"
                });
                seedCtx.SaveChanges();
            }

            // Query as TENANT_A — soft-delete filter must exclude this entity
            using (var queryCtx = new CombinedFilterChildContext(seed, DBTypeEnum.Memory))
            {
                queryCtx.SetTenantCode("TENANT_A");
                var results = queryCtx.Set<CombinedFilterChild>().ToList();
                Assert.AreEqual(0, results.Count,
                    "TENANT_A must not see soft-deleted entities (IsValid=false) even in " +
                    "its own tenant — both IPersistPoco and ITenant filters must be active.");
            }
        }

        [TestMethod]
        [Description(
            "Combined filter: an active entity (IsValid=true) for the querying tenant " +
            "must be visible (#382 combined-filter coverage).")]
        public void CombinedFilter_ActiveAndCorrectTenant_VisibleToTenant()
        {
            var seed = Guid.NewGuid().ToString("N");

            // Seed: add an active child for TENANT_A
            using (var seedCtx = new CombinedFilterChildContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                seedCtx.CombinedFilterChildren.Add(new CombinedFilterChild
                {
                    IsValid = true,
                    TenantCode = "TENANT_A",
                    Label = "active-a",
                    Tag = "tag-active"
                });
                seedCtx.SaveChanges();
            }

            // Query as TENANT_A — should see exactly 1 active entity
            using (var queryCtx = new CombinedFilterChildContext(seed, DBTypeEnum.Memory))
            {
                queryCtx.SetTenantCode("TENANT_A");
                var results = queryCtx.Set<CombinedFilterChild>().ToList();
                Assert.AreEqual(1, results.Count,
                    "TENANT_A must see exactly one active entity seeded for TENANT_A " +
                    "(combined IPersistPoco + ITenant filter must allow active same-tenant rows).");
            }
        }

        [TestMethod]
        [Description(
            "Combined filter: an active entity for a different tenant must be excluded " +
            "by the tenant filter even when IsValid=true (#382 combined-filter coverage).")]
        public void CombinedFilter_ActiveButWrongTenant_Excluded()
        {
            var seed = Guid.NewGuid().ToString("N");

            // Seed: add an active child for TENANT_B
            using (var seedCtx = new CombinedFilterChildContext(seed, DBTypeEnum.Memory))
            {
                seedCtx.Database.EnsureCreated();
                seedCtx.CombinedFilterChildren.Add(new CombinedFilterChild
                {
                    IsValid = true,
                    TenantCode = "TENANT_B",
                    Label = "active-b",
                    Tag = "tag-b"
                });
                seedCtx.SaveChanges();
            }

            // Query as TENANT_A — tenant filter must exclude TENANT_B entity
            using (var queryCtx = new CombinedFilterChildContext(seed, DBTypeEnum.Memory))
            {
                queryCtx.SetTenantCode("TENANT_A");
                var results = queryCtx.Set<CombinedFilterChild>().ToList();
                Assert.AreEqual(0, results.Count,
                    "TENANT_A must not see active entities belonging to TENANT_B " +
                    "(ITenant query filter must apply even when IsValid=true).");
            }
        }
    }
}
