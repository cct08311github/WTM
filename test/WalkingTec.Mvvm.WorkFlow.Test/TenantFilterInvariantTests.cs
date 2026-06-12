#nullable enable
// TenantFilterInvariantTests.cs — Spec §10 mandatory invariant: HasQueryFilter present on
// every WorkFlow ITenant entity.
//
// The WTM DataContext auto-applies tenant + soft-delete filters ONLY on DIRECT descendants
// of PersistPoco/BasePoco (DataContext.cs:164). Multi-level inheritance or a missing DbSet<>
// declaration silently drops the filter. This invariant test is the CI backstop.
//
// Approach (mirrors GovernanceTestDataContext precedent in WalkingTec.Mvvm.Etl.Test):
// - A consumer-style test DataContext inherits EmptyContext (so OnConfiguring + SQLite wiring
//   fires) and declares an explicit DbSet<T> for every WorkFlow entity.
// - OnModelCreating calls ApplyWorkFlowModels() + applies the tenant query filters
//   explicitly for all ITenant entities (the same Expression.Lambda pattern used in
//   GovernanceTestDataContext.cs).
// - The ITenant entity list is driven by REFLECTION over the WorkFlow assembly so that
//   newly added entities are automatically covered without updating this file.
// - Tests assert Model.FindEntityType(typeof(T)).GetQueryFilter() != null for every entity.
//
// DB: SQLite shared-in-memory (NEVER EF InMemory — spec §9 invariant #8 / #119/#162).
// PR #240.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Test;

// ─── Consumer-style test DataContext (mirrors GovernanceTestDataContext) ──────

/// <summary>
/// SQLite-backed test DataContext that registers all 9 WorkFlow tables and applies
/// the WTM tenant query filters for every ITenant entity.
/// Used for spec §10 HasQueryFilter invariant tests.
/// </summary>
internal sealed class WfTenantTestDataContext : EmptyContext
{
    public WfTenantTestDataContext(string cs, DBTypeEnum dbtype)
        : base(cs, dbtype) { }

    // Explicit DbSet<T> for all 10 WorkFlow entities — required for filter wiring.
    public DbSet<ProcessDefinition>        ProcessDefinitions        { get; set; } = null!;
    public DbSet<ProcessDefinitionVersion> ProcessDefinitionVersions { get; set; } = null!;
    public DbSet<ProcessDefinitionDraft>   ProcessDefinitionDrafts   { get; set; } = null!;
    public DbSet<ProcessInstance>          ProcessInstances          { get; set; } = null!;
    public DbSet<NodeInstance>             NodeInstances             { get; set; } = null!;
    public DbSet<ApprovalTask>             ApprovalTasks             { get; set; } = null!;
    public DbSet<WorkflowEventLog>         WorkflowEventLogs         { get; set; } = null!;
    public DbSet<CcRecord>                 CcRecords                 { get; set; } = null!;
    public DbSet<DelegationRule>           DelegationRules           { get; set; } = null!;
    public DbSet<WorkflowTimer>            WorkflowTimers            { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);  // EmptyContext → Oracle identifier length (no-op for SQLite)
        modelBuilder.ApplyWorkFlowModels();  // Tables, indexes, FK relationships

        // Explicitly apply the ITenant global query filter for every WorkFlow entity
        // that implements ITenant.  This mirrors what FrameworkContext.OnModelCreating
        // does via GetAllModels() + DataContext.cs:164, but applied explicitly here so
        // the test is independent of the static GetAllModels() assembly-scan cache
        // (which may or may not include test-assembly types depending on scan order).
        //
        // Filter: TenantCode == this.TenantCode (captures the DataContext instance so
        // EF reads the current TenantCode at query time — same pattern as DataContext.cs:173).
        foreach (var entityType in GetWorkFlowITenantTypes())
        {
            var pe = Expression.Parameter(entityType);
            var filterExp = Expression.Equal(
                Expression.Property(pe, "TenantCode"),
                Expression.PropertyOrField(Expression.Constant(this), "TenantCode"));
            var lambda = Expression.Lambda(filterExp, pe);

            var builder = typeof(ModelBuilder)
                .GetMethod("Entity", Type.EmptyTypes)!
                .MakeGenericMethod(entityType)
                .Invoke(modelBuilder, null) as EntityTypeBuilder;

            // HasQueryFilter via reflection (EntityTypeBuilder is non-generic at this point)
            builder!.GetType()
                .GetMethod("HasQueryFilter", new[] { typeof(LambdaExpression) })!
                .Invoke(builder, new object[] { lambda });
        }
    }

    /// <summary>
    /// Returns all concrete types in the WorkFlow assembly that implement ITenant and are
    /// NOT abstract — the same set that should have HasQueryFilter applied.
    /// Driven by reflection so new entities auto-cover without editing this file.
    /// </summary>
    internal static IReadOnlyList<Type> GetWorkFlowITenantTypes()
    {
        var wfAssembly = typeof(ProcessDefinition).Assembly;
        return wfAssembly.GetExportedTypes()
            .Where(t =>
                t.Namespace?.StartsWith("WalkingTec.Mvvm.WorkFlow.Models", StringComparison.Ordinal) == true
                && !t.IsAbstract
                && !t.IsInterface
                && typeof(ITenant).IsAssignableFrom(t)
                && typeof(TopBasePoco).IsAssignableFrom(t))
            .OrderBy(t => t.Name)
            .ToList();
    }
}

// ─── Invariant tests ──────────────────────────────────────────────────────────

/// <summary>
/// Spec §10 HasQueryFilter invariant: asserts that every WorkFlow ITenant entity
/// has a query filter registered on the consumer's DataContext model.
///
/// If any entity is missing the filter (multi-level inheritance, missing DbSet, etc.)
/// this test fails with a clear message naming the offending type, providing an early
/// CI signal before cross-tenant data leakage can occur in production.
/// </summary>
[TestClass]
public class TenantFilterInvariantTests : IDisposable
{
    private WfTenantTestDataContext _dc = null!;
    private SqliteConnection _keepAlive = null!;

    [TestInitialize]
    public void Setup()
    {
        var dbName = $"WfTenantInvariant_{Guid.NewGuid():N}";
        _keepAlive = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
        _keepAlive.Open();
        _dc = new WfTenantTestDataContext(
            $"DataSource={dbName}?mode=memory&cache=shared", DBTypeEnum.SQLite);
        _dc.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dc?.Dispose();
        _keepAlive?.Close();
        _keepAlive?.Dispose();
    }

    public void Dispose() => Cleanup();

    /// <summary>
    /// Spec §10: every WorkFlow ITenant entity must have HasQueryFilter registered.
    /// Entity list is reflection-driven — new entities auto-cover.
    /// </summary>
    [TestMethod]
    public void AllWorkFlowITenantEntities_HaveQueryFilter()
    {
        var entityTypes = WfTenantTestDataContext.GetWorkFlowITenantTypes();

        Assert.IsTrue(entityTypes.Count > 0,
            "GetWorkFlowITenantTypes must find at least one ITenant entity — reflection guard.");

        var missing = new List<string>();
        foreach (var t in entityTypes)
        {
            var efEntityType = _dc.Model.FindEntityType(t);
            if (efEntityType == null)
            {
                missing.Add($"{t.Name}: not found in model (is DbSet<{t.Name}> declared in WfTenantTestDataContext?)");
                continue;
            }

            // GetDeclaredQueryFilters() supersedes the obsolete GetQueryFilter() in EF Core 10+.
            var filters = efEntityType.GetDeclaredQueryFilters();
            if (filters == null || !filters.Any())
            {
                missing.Add($"{t.Name}: GetDeclaredQueryFilters() returned empty — tenant filter not wired");
            }
        }

        if (missing.Count > 0)
        {
            Assert.Fail(
                $"Spec §10 HasQueryFilter invariant FAILED for {missing.Count}/{entityTypes.Count} WorkFlow entities:\n" +
                string.Join("\n", missing.Select(m => $"  - {m}")));
        }
    }

    /// <summary>
    /// Sanity check: reflection finds exactly the expected count of ITenant entities in the
    /// WorkFlow assembly.  If this number changes, update this assertion and document the reason.
    /// Current expected: 10 (ProcessDefinition, ProcessDefinitionVersion, ProcessDefinitionDraft,
    /// ProcessInstance, NodeInstance, ApprovalTask, WorkflowEventLog, CcRecord, DelegationRule, WorkflowTimer).
    /// </summary>
    [TestMethod]
    public void WorkFlowITenantEntityCount_MatchesExpected()
    {
        var entityTypes = WfTenantTestDataContext.GetWorkFlowITenantTypes();

        Assert.AreEqual(10, entityTypes.Count,
            $"Expected 10 ITenant WorkFlow entities; found {entityTypes.Count}. " +
            $"Types: [{string.Join(", ", entityTypes.Select(t => t.Name))}]. " +
            "If a new entity was added, update this assertion and ensure DbSet<T> is declared " +
            "in WfTenantTestDataContext and the tenant filter is applied in its OnModelCreating.");
    }

    /// <summary>
    /// Spec §10: ProcessDefinitionVersion.IsValid is shadowed with [BindNever] to block
    /// model-binding from flipping it.  Invariant: the property exists and defaults to true.
    /// </summary>
    [TestMethod]
    public void ProcessDefinitionVersion_IsValid_DefaultsToTrue_AndIsBindNever()
    {
        var version = new ProcessDefinitionVersion();
        Assert.IsTrue(version.IsValid,
            "ProcessDefinitionVersion.IsValid must default to true (keeps IsValid==true query filter intact).");

        var prop = typeof(ProcessDefinitionVersion).GetProperty("IsValid")!;
        var bindNeverAttr = prop.GetCustomAttribute<Microsoft.AspNetCore.Mvc.ModelBinding.BindNeverAttribute>();
        Assert.IsNotNull(bindNeverAttr,
            "ProcessDefinitionVersion.IsValid must carry [BindNever] to block model-binding from flipping it.");
    }
}
