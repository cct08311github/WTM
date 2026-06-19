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
}
