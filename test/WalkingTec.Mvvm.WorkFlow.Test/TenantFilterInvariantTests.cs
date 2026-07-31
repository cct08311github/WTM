#nullable enable
// TenantFilterInvariantTests.cs — Issue #899 rewrite.
//
// ── Why the pre-#899 version of this file was a false-assurance test ──────────────────────────
//
// The old `WfTenantTestDataContext` (a) declared nine explicit `DbSet<T>` properties for
// WorkFlow entities -- demo/WalkingTec.Mvvm.Demo/DataContext.cs declares ZERO (verified:
// `grep -nE 'DbSet<' demo/WalkingTec.Mvvm.Demo/DataContext.cs` has no WorkFlow hits) -- and
// (b) manually re-applied the ITenant query filter BY HAND in its own `OnModelCreating`, via the
// exact same `Expression.Lambda` idiom production code was supposed to use but didn't. Both are
// departures from what production code actually does. The result: this test proved "the filter
// EXPRESSION is written correctly", not "production wiring APPLIES it" -- when production wiring
// broke (the root defect #899 exists to fix), this test kept passing, because it was testing its
// own parallel hand-built implementation, never the real `ApplyWorkFlowModels()` call chain.
//
// ── The two-test structure (do not collapse these back into one) ──────────────────────────────
//
// #899 adds a NEW overload, `ApplyWorkFlowModels(this ModelBuilder, EmptyContext)`. A test using
// that new wiring literally does not COMPILE against the pre-#899 source tree (the overload does
// not exist yet) -- and per this repo's standing rule, a compile failure does not count as RED.
// So there are two independent fixtures below, each proving a different thing:
//
//   1. WorkFlowObsoleteOverloadCharacterizationTests (PERMANENT) -- demo-shaped context, calls
//      the ZERO-ARG (now [Obsolete]) ApplyWorkFlowModels() after base.OnModelCreating(), asserts
//      NO WorkFlow entity gets a query filter. Green BEFORE #899 and green AFTER #899 -- it pins
//      the [Obsolete] message's own promise ("行為與升級前完全一致") and stops someone later
//      "helpfully" wiring a filter into the old overload and causing a silent behaviour change
//      for callers who have not migrated.
//
//   2. TenantFilterInvariantTests (NEW) -- same demo shape, but calls the NEW
//      ApplyWorkFlowModels(this) overload, and asserts the filter both EXISTS and BEHAVES.
//
// Confirming (2) cannot be RED before #899 for a structural (not effort) reason: verified
// directly, not assumed -- `git stash` the ServiceCollectionExtensions.cs / demo DataContext.cs
// changes with THIS test file still in place and running `dotnet build` on this test project
// reproduces CS1501 ("no overload for method 'ApplyWorkFlowModels' takes 1 arguments") for
// `WfDemoShapedContext.OnModelCreating`'s `modelBuilder.ApplyWorkFlowModels(this)` call -- a
// compile failure, not a red test run.
//
// IMPORTANT CORRECTION (cross-vendor review of PR #918): an earlier version of this comment
// claimed WorkFlowObsoleteOverloadCharacterizationTests "compiles and runs unchanged before and
// after #899" as if that made it usable as pre-fix RED evidence. That claim does not survive
// scrutiny: MSTest test discovery requires the whole ASSEMBLY (this test PROJECT) to build
// successfully -- a single compile error anywhere in the project fails the entire build,
// regardless of which .cs file the error is in. Because `TenantFilterInvariantTests` (fixture 2,
// same project) references the new overload, the pre-#899 tree fails to compile this project as
// a whole, and NEITHER fixture can literally be *run* against it -- not even fixture 1, whose own
// code never touches the new overload. Splitting fixture 1 into a separate .cs file would not
// change this: file boundaries do not create assembly boundaries.
//
// So: there is NO executable pre-fix RED for the acceptance criterion (tenant B can still create
// tenant A's code) itself -- only the git-stash-confirmed CS1501 above, which proves *why* one is
// structurally unavailable, not a run result. What DOES exist, and IS runnable both before and
// after #899 without needing the new overload, is
// WorkFlowObsoleteOverloadCharacterizationTests.ZeroArgOverload_ReproducesTheDuplicateCodeAcrossTenantsBug
// below -- but be precise about what it proves: it is a GREEN characterization (it exercises the
// real `WorkflowDefinitionStore.CreateDefinitionAsync` production code under the permanently-
// unfiltered old overload and asserts the BUGGY outcome as the expected one), not a RED test. It
// documents that the underlying defect is real and reproducible; it does not substitute for a
// failing pre-fix assertion of the FIXED behaviour, which is structurally unobtainable per above.
// The binding, CI-enforced evidence that the fix itself works is the mutant
// (test/mutants/entries/wf899-applyworkflowmodels-processdefinition-tenantfilter-neutralize.json).
//
// ── Hardening applied throughout (do not remove without equivalent replacement) ────────────────
//   - Every behavioural assertion below is a BEHAVIOUR assertion (rows visible/hidden), not an
//     existence assertion (`GetQueryFilter() != null` is satisfied by `x => true`).
//   - Every assertion's failure message states which line's deletion turns it red.
//   - DB: SQLite shared-in-memory (spec §9 invariant #8 / #119/#162 -- NEVER EF InMemory; EF
//     InMemory does not enforce the composite-unique-index/tenant-scoping semantics under test).
//   - A CI-enforced mutant (test/mutants/entries/wf899-applyworkflowmodels-processdefinition-
//     tenantfilter-neutralize.json) targets ProcessDefinition_TenantB_CannotSeeTenantA_Row /
//     ProcessDefinition_TenantA_CanSeeOwnRow below, mirroring the ETL #862 precedent
//     (test/mutants/entries/etl862-applyetlmodels-tenantfilter-neutralize.json).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Engine.Routing;
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.ViewModels;

namespace WalkingTec.Mvvm.WorkFlow.Test;

// ─── Demo-shaped test contexts ──────────────────────────────────────────────────────────────
// Neither context below declares a single DbSet<T> for any WorkFlow entity -- this mirrors
// demo/WalkingTec.Mvvm.Demo/DataContext.cs's ACTUAL shape, which relies entirely on
// ApplyWorkFlowModels[Core]'s own `builder.Entity<T>(...)` calls to register the entity types
// (EF Core registers a type into the model via ModelBuilder.Entity<T>() regardless of whether a
// DbSet<T> CLR property exists -- Set<T>() works against any model-registered type either way).

/// <summary>
/// Characterization fixture (#899): demo shape, OnModelCreating calls the ZERO-ARG (now
/// [Obsolete]) ApplyWorkFlowModels() overload after base.OnModelCreating(). Used only by
/// <see cref="WorkFlowObsoleteOverloadCharacterizationTests"/>.
/// </summary>
internal sealed class WfDemoShapedObsoleteContext : EmptyContext
{
    public WfDemoShapedObsoleteContext(string cs, DBTypeEnum dbtype) : base(cs, dbtype) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
#pragma warning disable CS0618 // intentional: this fixture pins the OBSOLETE overload's own unchanged behaviour.
        modelBuilder.ApplyWorkFlowModels();
#pragma warning restore CS0618
    }
}

/// <summary>
/// Invariant fixture (#899): same demo shape, but OnModelCreating calls the NEW
/// <c>ApplyWorkFlowModels(this)</c> overload -- the actual production wiring path
/// (demo/WalkingTec.Mvvm.Demo/DataContext.cs uses this exact call as of #899). Used only by
/// <see cref="TenantFilterInvariantTests"/>.
/// </summary>
internal sealed class WfDemoShapedContext : EmptyContext
{
    public WfDemoShapedContext(string cs, DBTypeEnum dbtype) : base(cs, dbtype) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyWorkFlowModels(this);
    }
}

/// <summary>
/// Consumer-style DataContext with explicit <c>DbSet&lt;T&gt;</c> declarations for all 10
/// WorkFlow entities, used by <c>TimerReaperTests.cs</c>'s broader engine tests (T-325 series,
/// T-TMO-21/22a) that need typed sets and production table names for scenarios well beyond this
/// file's own tenant-filter-wiring concern (e.g. <c>WorkflowTimerExecutor</c> crash-recovery,
/// idempotency-index pinning). Deliberately KEPT (same class name, same DbSet properties, same
/// constructor signature) rather than folded into <see cref="WfDemoShapedContext"/> so those
/// tests are unaffected by this file's #899 rewrite.
///
/// <para><strong>What changed for #899:</strong> <c>OnModelCreating</c> now calls the real
/// <c>ApplyWorkFlowModels(this)</c> production overload instead of the pre-#899 version's
/// zero-arg call plus a hand-rolled, by-hand-reapplied filter. Net effect for every existing
/// consumer is unchanged (zero-arg registration + hand-applied filter was already equivalent to
/// the combined filter) -- but consumers now exercise the SAME code path production does,
/// closing the exact "tests its own parallel implementation" gap #899 was filed over for THIS
/// file's other two fixtures.</para>
///
/// <para><strong>Do NOT use this context for a false-assurance-prevention test.</strong> It
/// declares DbSet&lt;T&gt; for every entity, which is NOT the shape
/// demo/WalkingTec.Mvvm.Demo/DataContext.cs actually uses -- that is exactly why
/// <see cref="WfDemoShapedContext"/> and <see cref="WfDemoShapedObsoleteContext"/> exist as
/// separate, deliberately DbSet-less fixtures above.</para>
/// </summary>
internal sealed class WfTenantTestDataContext : EmptyContext
{
    public WfTenantTestDataContext(string cs, DBTypeEnum dbtype) : base(cs, dbtype) { }

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
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyWorkFlowModels(this);
    }
}

// ─── Reflection-driven entity inventory (shared by both fixtures) ──────────────────────────────

internal static class WfTenantEntityInventory
{
    /// <summary>
    /// Returns all concrete, non-abstract types in the WorkFlow assembly that implement
    /// <c>ITenant</c> and derive from <c>TopBasePoco</c> -- the same set that should have
    /// HasQueryFilter applied by the new overload. Reflection-driven so a future new entity
    /// auto-covers without editing this file.
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

    /// <summary>The subset of <see cref="GetWorkFlowITenantTypes"/> that is also IPersistPoco --
    /// the 6 entities that must ALSO get the soft-delete (IsValid == true) half of the filter.
    /// </summary>
    internal static IReadOnlyList<Type> GetWorkFlowPersistPocoTypes() =>
        GetWorkFlowITenantTypes().Where(t => typeof(IPersistPoco).IsAssignableFrom(t)).ToList();
}

// ─── Minimal valid entity factories ─────────────────────────────────────────────────────────
// Every factory below assigns fresh Guid.NewGuid() values to any FK/unique-participating field
// so that seeding multiple rows of the SAME entity type (different tenant, different IsValid)
// never collides with this schema's own unique indexes (e.g. ProcessDefinition's composite
// unique (TenantCode, Code) -- each row gets its own random Code). FK values are never resolved
// against a real parent row (these fixtures do not enable SQLite FK enforcement, matching the
// existing GovernanceTestDataContext / pre-#899 WfTenantTestDataContext precedent), so an
// unresolved FK guid is fine for these tests' purposes.

internal static class WfTenantEntityFactories
{
    internal static ProcessDefinition NewProcessDefinition(string? tenantCode, bool isValid) => new()
    {
        ID = Guid.NewGuid(),
        TenantCode = tenantCode,
        Code = $"CODE-{Guid.NewGuid():N}",
        Name = "Test Definition",
        IsEnabled = true,
        IsValid = isValid,
    };

    internal static ProcessDefinitionVersion NewProcessDefinitionVersion(string? tenantCode, bool isValid, Guid definitionId) => new()
    {
        ID = Guid.NewGuid(),
        TenantCode = tenantCode,
        DefinitionId = definitionId,
        VersionNo = 1,
        GraphJson = "{}",
        ContentHash = $"HASH-{Guid.NewGuid():N}",
        IsValid = isValid,
    };

    internal static ProcessDefinitionDraft NewProcessDefinitionDraft(string? tenantCode, bool isValid, Guid definitionId) => new()
    {
        ID = Guid.NewGuid(),
        TenantCode = tenantCode,
        DefinitionId = definitionId,
        GraphJson = "{}",
        IsValid = isValid,
    };

    internal static ProcessInstance NewProcessInstance(string? tenantCode, bool isValid, Guid definitionVersionId) => new()
    {
        ID = Guid.NewGuid(),
        TenantCode = tenantCode,
        DefinitionVersionId = definitionVersionId,
        InitiatorITCode = "tester",
        State = InstanceState.Draft,
        IsValid = isValid,
    };

    internal static ApprovalTask NewApprovalTask(string? tenantCode, bool isValid, Guid nodeInstanceId) => new()
    {
        ID = Guid.NewGuid(),
        TenantCode = tenantCode,
        NodeInstanceId = nodeInstanceId,
        AssigneeITCode = "approver",
        State = TaskState.Pending,
        IsValid = isValid,
    };

    internal static DelegationRule NewDelegationRule(string? tenantCode, bool isValid) => new()
    {
        ID = Guid.NewGuid(),
        TenantCode = tenantCode,
        PrincipalITCode = "principal",
        DelegateeITCode = "delegatee",
        StartUtc = DateTime.UtcNow,
        EndUtc = DateTime.UtcNow.AddDays(1),
        IsValid = isValid,
    };

    internal static NodeInstance NewNodeInstance(string? tenantCode, Guid instanceId) => new()
    {
        ID = Guid.NewGuid(),
        TenantCode = tenantCode,
        InstanceId = instanceId,
        NodeKey = $"node-{Guid.NewGuid():N}",
        NodeKind = NodeKind.Approval,
        State = NodeState.Pending,
    };

    internal static WorkflowTimer NewWorkflowTimer(string? tenantCode, Guid nodeInstanceId) => new()
    {
        ID = Guid.NewGuid(),
        TenantCode = tenantCode,
        NodeInstanceId = nodeInstanceId,
        FireAtUtc = DateTime.UtcNow.AddHours(1),
        Action = TimerAction.Remind,
        IdempotencyKey = Guid.NewGuid().ToString("N"), // globally unique, not just per-tenant
        Status = TimerStatus.Armed,
    };

    internal static CcRecord NewCcRecord(string? tenantCode, Guid instanceId) => new()
    {
        ID = Guid.NewGuid(),
        TenantCode = tenantCode,
        InstanceId = instanceId,
        RecipientITCode = "recipient",
        Trigger = CcTrigger.OnNode,
    };

    internal static WorkflowEventLog NewWorkflowEventLog(string? tenantCode, Guid instanceId) => new()
    {
        ID = Guid.NewGuid(),
        TenantCode = tenantCode,
        InstanceId = instanceId,
        Seq = 1,
        Action = EventAction.Submit,
        OccurredUtc = DateTime.UtcNow,
    };
}

// ─── Characterization tests (PERMANENT — green before AND after #899) ──────────────────────────

/// <summary>
/// #899: pins the [Obsolete] zero-arg <c>ApplyWorkFlowModels()</c> overload's existing
/// behaviour. Green before AND after #899 -- this is the Obsolete message's promise
/// ("行為與升級前完全一致", mirroring #862's identical ETL promise) made runnable, and it
/// stops someone later "helpfully" wiring a filter into the old overload and causing a silent
/// behaviour change for callers who have not migrated to <c>ApplyWorkFlowModels(this)</c>.
/// </summary>
[TestClass]
public class WorkFlowObsoleteOverloadCharacterizationTests : IDisposable
{
    private WfDemoShapedObsoleteContext _dc = null!;
    private SqliteConnection _keepAlive = null!;

    [TestInitialize]
    public void Setup()
    {
        var dbName = $"WfObsoleteCharacterization_{Guid.NewGuid():N}";
        var connStr = $"DataSource={dbName}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connStr);
        _keepAlive.Open();
        _dc = new WfDemoShapedObsoleteContext(connStr, DBTypeEnum.SQLite);
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
    /// Which line's deletion turns this red: adding ANY
    /// <c>ApplyWorkFlowTenantFilter&lt;T&gt;(builder, context)</c>-equivalent call into the
    /// zero-arg <c>ApplyWorkFlowModels(this ModelBuilder builder)</c> overload's body
    /// (ServiceCollectionExtensions.cs) turns this red -- that overload has no context
    /// instance to bind a filter to, and must keep applying none.
    /// </summary>
    [TestMethod]
    public void ZeroArgOverload_AppliesNoQueryFilter_ToAnyWorkFlowEntity()
    {
        var entityTypes = WfTenantEntityInventory.GetWorkFlowITenantTypes();
        Assert.IsTrue(entityTypes.Count > 0, "Reflection guard: must find at least one WorkFlow ITenant entity.");

        var unexpectedlyFiltered = new List<string>();
        foreach (var t in entityTypes)
        {
            var efEntityType = _dc.Model.FindEntityType(t);
            Assert.IsNotNull(efEntityType,
                $"{t.Name}: must still be registered in the model -- table/column/index " +
                "registration is unchanged by #899, only HasQueryFilter wiring is.");

            var filters = efEntityType!.GetDeclaredQueryFilters();
            if (filters != null && filters.Any())
            {
                unexpectedlyFiltered.Add(t.Name);
            }
        }

        Assert.AreEqual(0, unexpectedlyFiltered.Count,
            "#899 characterization violated: the OBSOLETE zero-arg ApplyWorkFlowModels() " +
            $"overload must NEVER apply a query filter, but it did for: {string.Join(", ", unexpectedlyFiltered)}. " +
            "This overload has no DbContext instance to bind TenantCode to -- if this now " +
            "fails, someone added filter logic to the wrong overload.");
    }

    /// <summary>
    /// Documents (and runnably reproduces) the pre-#899 write-side defect #899's own acceptance
    /// criterion targets, using the REAL <see cref="WorkflowDefinitionStore.CreateDefinitionAsync"/>
    /// production code path -- not a reimplementation. This test is permanent and its assertions
    /// do NOT change after #899 lands: the obsolete zero-arg overload never gets a filter, so
    /// this bug remains reproducible under it forever (by design -- that IS the Obsolete
    /// contract). See <see cref="TenantFilterInvariantTests.NewWiring_TenantA_CreatesCodeX_ThenTenantB_CanStillCreateCodeX"/>
    /// for the corresponding proof that the NEW wiring fixes this.
    /// </summary>
    [TestMethod]
    public async Task ZeroArgOverload_ReproducesTheDuplicateCodeAcrossTenantsBug()
    {
        const string code = "PurchaseApproval-ObsoleteWiring";
        using var store = new WorkflowDefinitionStore(_dc);

        _dc.SetTenantCode("TenantA");
        var resultA = await store.CreateDefinitionAsync(
            new CreateDefinitionRequest { Code = code, Name = "Tenant A's definition" },
            tenantCode: "TenantA", createdBy: "tester");
        Assert.AreEqual(CreateDefinitionOutcome.Created, resultA.Outcome,
            "Setup precondition failed: tenant A's own create must succeed.");

        // #899 root-cause reproduction: with NO query filter applied at all (the obsolete
        // overload's permanent behaviour), CreateDefinitionAsync's duplicate check
        // (`_dc.Set<ProcessDefinition>().AnyAsync(d => d.Code == request.Code)`,
        // WorkflowDefinitionStore.cs) sees EVERY tenant's rows, not just TenantB's -- so it
        // (incorrectly) treats TenantA's code as already taken. Which line's deletion turns
        // this red: none needed -- this reproduces the pre-#899 production bug as-is, using
        // the context shape that never gets a filter by design. If this assertion ever starts
        // passing (Outcome == Created), the obsolete overload silently started filtering,
        // which is the characterization violation the OTHER test in this class exists to catch.
        _dc.SetTenantCode("TenantB");
        var resultB = await store.CreateDefinitionAsync(
            new CreateDefinitionRequest { Code = code, Name = "Tenant B's definition" },
            tenantCode: "TenantB", createdBy: "tester");
        Assert.AreEqual(CreateDefinitionOutcome.DuplicateCode, resultB.Outcome,
            "Pre-#899 bug reproduction failed: under the (permanently) unfiltered obsolete " +
            "overload, tenant B's create of the SAME code tenant A already used should be " +
            "(incorrectly) rejected as a duplicate, because the duplicate check has no " +
            "TenantCode predicate of its own and depends entirely on the (here: absent) " +
            "global filter.");
    }
}

// ─── Invariant tests (NEW production wiring) ────────────────────────────────────────────────

/// <summary>
/// #899: asserts the NEW <c>ApplyWorkFlowModels(this)</c> overload's query filter both EXISTS
/// and BEHAVES, using the same demo shape (no DbSet&lt;T&gt; declared) production code actually
/// uses. Cannot be confirmed RED against pre-#899 source for a structural reason, not an effort
/// one -- see the file header comment.
/// </summary>
[TestClass]
public class TenantFilterInvariantTests : IDisposable
{
    private const string TenantA = "TenantA";
    private const string TenantB = "TenantB";

    private WfDemoShapedContext _dcA = null!;
    private WfDemoShapedContext _dcB = null!;
    private SqliteConnection _keepAlive = null!;

    [TestInitialize]
    public void Setup()
    {
        var dbName = $"WfTenantInvariant_{Guid.NewGuid():N}";
        var connStr = $"DataSource={dbName}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connStr);
        _keepAlive.Open();

        _dcA = new WfDemoShapedContext(connStr, DBTypeEnum.SQLite);
        _dcA.SetTenantCode(TenantA);
        _dcA.Database.EnsureCreated();

        // Same shared-cache in-memory db, second context scoped to a different tenant -- this
        // is what proves "TenantB cannot see TenantA's row" is a QUERY FILTER decision, not
        // simply "the row was never written" (both contexts see the same physical rows unless
        // the filter intervenes).
        _dcB = new WfDemoShapedContext(connStr, DBTypeEnum.SQLite);
        _dcB.SetTenantCode(TenantB);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dcA?.Dispose();
        _dcB?.Dispose();
        _keepAlive?.Close();
        _keepAlive?.Dispose();
    }

    public void Dispose() => Cleanup();

    // ── FK parent-chain seed helpers ─────────────────────────────────────────────────────────
    // SQLite enforces declared FK constraints for these fixtures (verified empirically -- an
    // earlier version of this file used bare Guid.NewGuid() FK values and every test touching an
    // FK-bearing entity failed with SqliteException "FOREIGN KEY constraint failed"). Every
    // helper below seeds a minimal, valid, TENANT-MATCHED parent chain via _dcA (inserts are
    // never filtered -- only queries are -- so which context performs the insert does not
    // affect what this file's assertions are about) and returns the leaf id needed by the
    // caller's own entity under test.

    private Guid SeedProcessDefinition(string? tenantCode)
    {
        var def = WfTenantEntityFactories.NewProcessDefinition(tenantCode, true);
        _dcA.Add(def);
        _dcA.SaveChanges();
        return def.ID;
    }

    private Guid SeedProcessDefinitionVersion(string? tenantCode)
    {
        var ver = WfTenantEntityFactories.NewProcessDefinitionVersion(tenantCode, true, SeedProcessDefinition(tenantCode));
        _dcA.Add(ver);
        _dcA.SaveChanges();
        return ver.ID;
    }

    private Guid SeedProcessInstance(string? tenantCode)
    {
        var inst = WfTenantEntityFactories.NewProcessInstance(tenantCode, true, SeedProcessDefinitionVersion(tenantCode));
        _dcA.Add(inst);
        _dcA.SaveChanges();
        return inst.ID;
    }

    private Guid SeedNodeInstance(string? tenantCode)
    {
        var node = WfTenantEntityFactories.NewNodeInstance(tenantCode, SeedProcessInstance(tenantCode));
        _dcA.Add(node);
        _dcA.SaveChanges();
        return node.ID;
    }

    // ── Sanity / structural checks (kept from the pre-#899 file) ────────────────────────────

    /// <summary>
    /// Sanity check: reflection finds exactly the expected count of ITenant entities in the
    /// WorkFlow assembly. If this number changes, update this assertion and document the reason.
    /// Current expected: 10 (ProcessDefinition, ProcessDefinitionVersion, ProcessDefinitionDraft,
    /// ProcessInstance, ApprovalTask, DelegationRule, NodeInstance, WorkflowTimer, CcRecord,
    /// WorkflowEventLog) -- 6 PersistPoco + 4 BasePoco.
    /// </summary>
    [TestMethod]
    public void WorkFlowITenantEntityCount_MatchesExpected()
    {
        var entityTypes = WfTenantEntityInventory.GetWorkFlowITenantTypes();
        Assert.AreEqual(10, entityTypes.Count,
            $"Expected 10 ITenant WorkFlow entities; found {entityTypes.Count}. " +
            $"Types: [{string.Join(", ", entityTypes.Select(t => t.Name))}]. " +
            "If a new entity was added, update this assertion, this file's factories/loops, " +
            "and confirm ApplyWorkFlowModels(ModelBuilder, EmptyContext) calls " +
            "ApplyWorkFlowTenantFilter<T> for it.");

        var persistPocoTypes = WfTenantEntityInventory.GetWorkFlowPersistPocoTypes();
        Assert.AreEqual(6, persistPocoTypes.Count,
            $"Expected 6 PersistPoco/ITenant WorkFlow entities (the ones that ALSO need the " +
            $"soft-delete filter half); found {persistPocoTypes.Count}: " +
            $"[{string.Join(", ", persistPocoTypes.Select(t => t.Name))}].");
    }

    /// <summary>
    /// Spec §10: ProcessDefinitionVersion.IsValid is shadowed with [BindNever] to block
    /// model-binding from flipping it. Invariant: the property exists and defaults to true.
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

    // ── Existence sweep (coverage net -- NOT sufficient alone, see behavioural tests below) ──

    /// <summary>
    /// Existence-only coverage net across all 10 entities. Deliberately NOT the primary proof --
    /// `GetQueryFilter() != null` is satisfied by `x => true`, so this alone would not catch a
    /// filter that exists but is wrong (e.g. tenant-only on a PersistPoco entity, missing the
    /// soft-delete half). The behavioural tests below are the real proof; this test exists so a
    /// REGISTRATION gap (an entity silently dropped from the loop in
    /// ApplyWorkFlowModels(ModelBuilder, EmptyContext)) is caught even for entities the
    /// behavioural tests don't separately name.
    /// </summary>
    [TestMethod]
    public void AllWorkFlowITenantEntities_HaveQueryFilter()
    {
        var entityTypes = WfTenantEntityInventory.GetWorkFlowITenantTypes();
        var missing = new List<string>();
        foreach (var t in entityTypes)
        {
            var efEntityType = _dcA.Model.FindEntityType(t);
            if (efEntityType == null)
            {
                missing.Add($"{t.Name}: not found in model");
                continue;
            }
            var filters = efEntityType.GetDeclaredQueryFilters();
            if (filters == null || !filters.Any())
            {
                missing.Add($"{t.Name}: GetDeclaredQueryFilters() returned empty");
            }
        }

        Assert.AreEqual(0, missing.Count,
            "#899 registration gap: which line's deletion turns this red -- removing the " +
            "corresponding ApplyWorkFlowTenantFilter<T>(builder, context) call from " +
            "ApplyWorkFlowModels(ModelBuilder, EmptyContext) (ServiceCollectionExtensions.cs) " +
            $"for any of: {string.Join("; ", missing)}");
    }

    // ── Behavioural tests: tenant isolation, across all 10 entities ─────────────────────────

    /// <summary>
    /// Behavioural proof (not existence) for all 10 ITenant entities: a row seeded under
    /// TenantA is visible to a context scoped to TenantA, invisible to a context scoped to
    /// TenantB, and reappears under TenantB's context via IgnoreQueryFilters() (proving the
    /// invisibility above is a QUERY FILTER decision, not the row failing to exist).
    /// </summary>
    [TestMethod]
    public void AllTenantEntities_TenantIsolation_AcrossAllTenTypes()
    {
        var failures = new List<string>();

        CheckTenantIsolation<ProcessDefinition>(t => WfTenantEntityFactories.NewProcessDefinition(t, true), failures);
        CheckTenantIsolation<ProcessDefinitionVersion>(t => WfTenantEntityFactories.NewProcessDefinitionVersion(t, true, SeedProcessDefinition(t)), failures);
        CheckTenantIsolation<ProcessDefinitionDraft>(t => WfTenantEntityFactories.NewProcessDefinitionDraft(t, true, SeedProcessDefinition(t)), failures);
        CheckTenantIsolation<ProcessInstance>(t => WfTenantEntityFactories.NewProcessInstance(t, true, SeedProcessDefinitionVersion(t)), failures);
        CheckTenantIsolation<ApprovalTask>(t => WfTenantEntityFactories.NewApprovalTask(t, true, SeedNodeInstance(t)), failures);
        CheckTenantIsolation<DelegationRule>(t => WfTenantEntityFactories.NewDelegationRule(t, true), failures);
        CheckTenantIsolation<NodeInstance>(t => WfTenantEntityFactories.NewNodeInstance(t, SeedProcessInstance(t)), failures);
        CheckTenantIsolation<WorkflowTimer>(t => WfTenantEntityFactories.NewWorkflowTimer(t, SeedNodeInstance(t)), failures);
        CheckTenantIsolation<CcRecord>(t => WfTenantEntityFactories.NewCcRecord(t, SeedProcessInstance(t)), failures);
        CheckTenantIsolation<WorkflowEventLog>(t => WfTenantEntityFactories.NewWorkflowEventLog(t, SeedProcessInstance(t)), failures);

        Assert.AreEqual(0, failures.Count,
            "#899 tenant-isolation regression -- which line's deletion turns this red: the " +
            "corresponding ApplyWorkFlowTenantFilter<T> call in " +
            "ApplyWorkFlowModels(ModelBuilder, EmptyContext) (ServiceCollectionExtensions.cs), " +
            $"or the TenantCode Expression.Equal inside ApplyWorkFlowTenantFilter<T> itself:\n" +
            string.Join("\n", failures));
    }

    private void CheckTenantIsolation<T>(Func<string, T> factory, List<string> failures)
        where T : TopBasePoco, ITenant
    {
        var typeName = typeof(T).Name;
        var row = factory(TenantA);
        _dcA.Add(row);
        _dcA.SaveChanges();
        var id = row.ID;

        if (!_dcA.Set<T>().Any(e => e.ID == id))
        {
            failures.Add($"{typeName}: TenantA's own context could not see its own row (filter too strict).");
            return;
        }
        if (_dcB.Set<T>().Any(e => e.ID == id))
        {
            failures.Add($"{typeName}: TenantB's context can see TenantA's row (filter missing/wrong).");
            return;
        }
        if (!_dcB.Set<T>().IgnoreQueryFilters().Any(e => e.ID == id))
        {
            failures.Add($"{typeName}: row not reachable via IgnoreQueryFilters() -- the row itself is " +
                          "missing, so the invisibility above proves nothing about the filter.");
        }
    }

    // ── Behavioural tests: soft-delete filter, across the 6 PersistPoco entities ────────────

    /// <summary>
    /// Behavioural proof for the 6 PersistPoco/ITenant entities: a row seeded with
    /// IsValid=false is invisible through the normal (same-tenant) query, and reappears via
    /// IgnoreQueryFilters() -- proving the invisibility is the soft-delete half of the combined
    /// filter, not missing data. This dimension is the one ETL's tenant-only
    /// ApplyEtlTenantFilter&lt;T&gt; would have silently dropped if copied as-is (issue #899's
    /// central design correction) -- ETL has no PersistPoco entities to get this wrong.
    /// </summary>
    [TestMethod]
    public void AllPersistPocoEntities_SoftDeleteFilter_AcrossAllSixTypes()
    {
        var failures = new List<string>();

        CheckSoftDeleteHidden<ProcessDefinition>(
            (t, v) => WfTenantEntityFactories.NewProcessDefinition(t, v), failures);
        CheckSoftDeleteHidden<ProcessDefinitionVersion>(
            (t, v) => WfTenantEntityFactories.NewProcessDefinitionVersion(t, v, SeedProcessDefinition(t)), failures);
        CheckSoftDeleteHidden<ProcessDefinitionDraft>(
            (t, v) => WfTenantEntityFactories.NewProcessDefinitionDraft(t, v, SeedProcessDefinition(t)), failures);
        CheckSoftDeleteHidden<ProcessInstance>(
            (t, v) => WfTenantEntityFactories.NewProcessInstance(t, v, SeedProcessDefinitionVersion(t)), failures);
        CheckSoftDeleteHidden<ApprovalTask>(
            (t, v) => WfTenantEntityFactories.NewApprovalTask(t, v, SeedNodeInstance(t)), failures);
        CheckSoftDeleteHidden<DelegationRule>(
            (t, v) => WfTenantEntityFactories.NewDelegationRule(t, v), failures);

        Assert.AreEqual(0, failures.Count,
            "#899 soft-delete filter regression (the central design correction vs. copying " +
            "ETL's tenant-only helper as-is) -- which line's deletion turns this red: the " +
            "`if (typeof(IPersistPoco).IsAssignableFrom(typeof(T))) { conditions.Add(... IsValid " +
            "...) }` block inside ApplyWorkFlowTenantFilter<T> (ServiceCollectionExtensions.cs):\n" +
            string.Join("\n", failures));
    }

    private void CheckSoftDeleteHidden<T>(Func<string?, bool, T> factory, List<string> failures)
        where T : TopBasePoco, ITenant, IPersistPoco
    {
        var typeName = typeof(T).Name;
        var row = factory(TenantA, false); // IsValid = false
        _dcA.Add(row);
        _dcA.SaveChanges();
        var id = row.ID;

        if (_dcA.Set<T>().Any(e => e.ID == id))
        {
            failures.Add($"{typeName}: soft-deleted (IsValid=false) row is visible through the normal, same-tenant query.");
            return;
        }
        if (!_dcA.Set<T>().IgnoreQueryFilters().Any(e => e.ID == id))
        {
            failures.Add($"{typeName}: row not reachable via IgnoreQueryFilters() -- the row itself is " +
                          "missing, so the invisibility above proves nothing about the filter.");
        }
    }

    // ── Dedicated single-entity tests (mutant CI target -- see test/mutants/entries/) ───────

    /// <summary>
    /// Mutant CI target (RED half): deleting the
    /// <c>ApplyWorkFlowTenantFilter&lt;ProcessDefinition&gt;(builder, context)</c> call from
    /// <c>ApplyWorkFlowModels(ModelBuilder, EmptyContext)</c> (ServiceCollectionExtensions.cs)
    /// turns this red. Kept as its own dedicated, individually-addressable test (mirrors #862's
    /// <c>EtlJobDefinition_TenantA_CannotReadTenantB_Job</c>) because the aggregate loop tests
    /// above cannot be targeted by a single MSTest fully-qualified-name filter.
    /// </summary>
    [TestMethod]
    public void ProcessDefinition_TenantB_CannotSeeTenantA_Row()
    {
        var row = WfTenantEntityFactories.NewProcessDefinition(TenantA, true);
        _dcA.Add(row);
        _dcA.SaveChanges();

        var visibleToOtherTenant = _dcB.Set<ProcessDefinition>().Any(d => d.ID == row.ID);
        Assert.IsFalse(visibleToOtherTenant,
            "#899: a context scoped to TenantB must NOT resolve TenantA's ProcessDefinition row. " +
            "If this fails, ApplyWorkFlowModels(ModelBuilder, EmptyContext) is no longer applying " +
            "the ITenant query filter for ProcessDefinition.");
    }

    /// <summary>
    /// Mutant CI positive control (GREEN half, mirrors #862's
    /// <c>EtlJobDefinition_TenantA_CanReadOwnJob</c>): proves the mutation above does not have
    /// broader collateral effects, e.g. breaking the Wf_ProcessDefinition table registration
    /// itself. Stays green regardless of whether the tenant filter is present, by design -- its
    /// purpose is to bound the blast radius of the red test's mutant, not to detect the
    /// mutation on its own.
    /// </summary>
    [TestMethod]
    public void ProcessDefinition_TenantA_CanSeeOwnRow()
    {
        var row = WfTenantEntityFactories.NewProcessDefinition(TenantA, true);
        _dcA.Add(row);
        _dcA.SaveChanges();

        var visibleToOwnTenant = _dcA.Set<ProcessDefinition>().Any(d => d.ID == row.ID);
        Assert.IsTrue(visibleToOwnTenant,
            "Sanity/positive-control failure: TenantA's own context must see its own " +
            "ProcessDefinition row regardless of the tenant filter's presence.");
    }

    /// <summary>
    /// Combines BOTH dimensions on a single row (wrong-tenant AND soft-deleted) to demonstrate
    /// IgnoreQueryFilters() restores full visibility -- a data-presence/bypass proof that the row
    /// really exists and IgnoreQueryFilters() really can reach it.
    ///
    /// <para><strong>Not binding on its own (cross-vendor review of PR #918):</strong> because the
    /// row is hidden by BOTH dimensions at once, deleting EITHER half of the combined
    /// (IsValid &amp;&amp; TenantCode) filter inside <c>ApplyWorkFlowTenantFilter&lt;T&gt;</c>
    /// leaves this test green -- the OTHER half still hides the row from
    /// <c>hiddenFromOtherTenant</c> for an unrelated reason, and <c>IgnoreQueryFilters()</c> still
    /// reveals it regardless of which half (if any) is broken. Verified directly, not assumed: a
    /// local single-half mutation on each side leaves both assertions in this test passing. The
    /// tests that DO bind to each half individually are
    /// <see cref="AllTenantEntities_TenantIsolation_AcrossAllTenTypes"/> (tenant half, all 10
    /// types) and <see cref="AllPersistPocoEntities_SoftDeleteFilter_AcrossAllSixTypes"/>
    /// (IsValid half, the 6 PersistPoco types) -- this test exists only to show the two dimensions
    /// composing correctly on ONE row, not as its own mutation-detection evidence.</para>
    /// </summary>
    [TestMethod]
    public void IgnoreQueryFilters_RestoresVisibility_ForBothTenantAndSoftDeleteDimensions()
    {
        var row = WfTenantEntityFactories.NewProcessDefinition(TenantA, false); // wrong-tenant (from dcB's POV) AND soft-deleted
        _dcA.Add(row);
        _dcA.SaveChanges();

        var hiddenFromOtherTenant = _dcB.Set<ProcessDefinition>().Any(d => d.ID == row.ID);
        Assert.IsFalse(hiddenFromOtherTenant,
            "Setup precondition failed: the row must be hidden from TenantB's normal query.");

        var visibleIgnoringFilters = _dcB.Set<ProcessDefinition>().IgnoreQueryFilters().Any(d => d.ID == row.ID);
        Assert.IsTrue(visibleIgnoringFilters,
            "#899: IgnoreQueryFilters() must restore visibility of a row hidden by BOTH the " +
            "tenant and soft-delete dimensions -- proving the combined filter is a real EF " +
            "query filter (droppable via IgnoreQueryFilters), matching the same pattern the " +
            "engine's 30+ IgnoreQueryFilters() sweep call sites rely on.");
    }

    // ── Acceptance test (issue #899 comment): the write-side symptom ────────────────────────

    /// <summary>
    /// #899 acceptance criterion (from the issue's own comment thread, PR #896's 58-member
    /// enumeration): the schema's own unique index on ProcessDefinition is COMPOSITE
    /// (TenantCode, Code) -- two tenants sharing a Code is BY DESIGN
    /// (ServiceCollectionExtensions.cs, <c>e.HasIndex(x => new { x.TenantCode, x.Code }).IsUnique()</c>).
    /// Before #899, <see cref="WorkflowDefinitionStore.CreateDefinitionAsync"/>'s duplicate
    /// check had no <c>TenantCode</c> predicate of its own and relied entirely on the
    /// (nonexistent) global filter, so in production a Code used by ANY tenant was rejected for
    /// every other tenant -- see
    /// <see cref="WorkFlowObsoleteOverloadCharacterizationTests.ZeroArgOverload_ReproducesTheDuplicateCodeAcrossTenantsBug"/>
    /// for the runnable reproduction of that defect under the (permanently unfixed) old wiring.
    /// This test exercises the SAME real production code path
    /// (<c>WorkflowDefinitionStore.CreateDefinitionAsync</c>, not a reimplementation) under the
    /// NEW wiring and proves the defect is fixed.
    /// </summary>
    [TestMethod]
    public async Task NewWiring_TenantA_CreatesCodeX_ThenTenantB_CanStillCreateCodeX()
    {
        const string code = "PurchaseApproval-SharedCode";
        using var storeA = new WorkflowDefinitionStore(_dcA);
        using var storeB = new WorkflowDefinitionStore(_dcB);

        var resultA = await storeA.CreateDefinitionAsync(
            new CreateDefinitionRequest { Code = code, Name = "Tenant A's definition" },
            tenantCode: TenantA, createdBy: "tester");
        Assert.AreEqual(CreateDefinitionOutcome.Created, resultA.Outcome,
            "Setup precondition failed: tenant A's own create must succeed.");

        // Which line's deletion turns this red: the
        // ApplyWorkFlowTenantFilter<ProcessDefinition>(builder, context) call in
        // ApplyWorkFlowModels(ModelBuilder, EmptyContext) (ServiceCollectionExtensions.cs) --
        // delete it and CreateDefinitionAsync's `AnyAsync(d => d.Code == request.Code)` query
        // (WorkflowDefinitionStore.cs) sees TenantA's row too, and this assertion fails with
        // Outcome == DuplicateCode instead of Created.
        var resultB = await storeB.CreateDefinitionAsync(
            new CreateDefinitionRequest { Code = code, Name = "Tenant B's definition" },
            tenantCode: TenantB, createdBy: "tester");
        Assert.AreEqual(CreateDefinitionOutcome.Created, resultB.Outcome,
            "#899: tenant B must be able to create the SAME Code tenant A already used -- the " +
            "unique index is composite (TenantCode, Code), sharing a Code across tenants is by " +
            "design, and the duplicate check must be scoped by the (now-active) tenant filter.");
    }

    // ── Write-provenance guards (cross-vendor review of PR #918, Blocking 1) ────────────────
    //
    // The filter alone does not protect a WRITE whose TenantCode came from a caller-supplied
    // parameter never checked against anything: WorkflowEngine.StartAsync's `tenantCode`
    // parameter used to be written onto the new ProcessInstance verbatim, and
    // WorkflowDefinitionStore.CreateDefinitionAsync's `tenantCode` parameter used to be written
    // onto the new ProcessDefinition verbatim -- in both cases with NO check that the value
    // agreed with the already-authorized source (the loaded ProcessDefinitionVersion / the
    // calling IDataContext's own TenantCode). A caller passing a mismatched value would silently
    // write a row under a tenant nobody's context can ever query again (or, by coincidence,
    // under some OTHER tenant's value). Both methods now fail closed BEFORE any write on a
    // mismatch. These tests prove the fail-closed contract two ways: (a) a wrong-but-non-null
    // value, and (b) a null value against a non-null authorized tenant -- and prove NO row was
    // written at all (via IgnoreQueryFilters(), which distinguishes "genuinely absent" from
    // "written but merely invisible to my own tenant-scoped query").

    private static WorkflowEngine MakeMinimalEngine(WfDemoShapedContext dc) => new(
        dc,
        NodeKindDispatcher_Exposed.Create(),
        new WhitelistRoutingEvaluator(NullLogger<WhitelistRoutingEvaluator>.Instance),
        Options.Create(new WorkFlowOptions()),
        NullLogger<WorkflowEngine>.Instance);

    /// <summary>
    /// Which line's deletion turns this red: the
    /// `if (!string.Equals(tenantCode, version.TenantCode, StringComparison.Ordinal))` guard in
    /// <c>WorkflowEngine.StartAsync</c> (WorkflowEngine.cs). Delete it and this test's
    /// <c>ThrowsExceptionAsync</c> assertion fails (no exception), and the follow-up
    /// IgnoreQueryFilters() assertion fails too (a ProcessInstance row WAS written, under the
    /// wrong tenant).
    /// </summary>
    [TestMethod]
    public async Task StartAsync_TenantCodeMismatch_ThrowsAndWritesNoRow()
    {
        var definitionId = SeedProcessDefinition(TenantA);
        var version = WfTenantEntityFactories.NewProcessDefinitionVersion(TenantA, true, definitionId);
        // GraphJson is never parsed -- the provenance guard runs BEFORE the graph is
        // deserialized, so an intentionally-invalid placeholder is sufficient here.
        version.GraphJson = "{}";
        _dcA.Add(version);
        _dcA.SaveChanges();

        var engine = MakeMinimalEngine(_dcA);

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            engine.StartAsync(version.ID, null, "initiator", tenantCode: "SomeOtherTenant"));
        StringAssert.Contains(ex.Message, "does not match ProcessDefinitionVersion",
            "#899: the exception must name the actual mismatch, not just any InvalidOperationException.");

        var anyInstanceWritten = _dcA.Set<ProcessInstance>()
            .IgnoreQueryFilters()
            .Any(i => i.DefinitionVersionId == version.ID);
        Assert.IsFalse(anyInstanceWritten,
            "#899: a tenantCode/version mismatch must throw BEFORE any ProcessInstance row is " +
            "written -- not write one under the mismatched tenant and only fail visibly later.");
    }

    /// <summary>
    /// Same guard, the NULL-vs-non-null shape specifically (reviewer's explicit ask): a caller
    /// passing no tenant at all against a definition version that DOES belong to a tenant must
    /// also fail closed -- `string.Equals(null, "TenantA")` is false, same as any other mismatch,
    /// but null is worth its own test because it is the value every un-migrated/background
    /// caller is most likely to pass by omission.
    /// </summary>
    [TestMethod]
    public async Task StartAsync_NullTenantCodeParameter_AgainstNonNullVersionTenant_ThrowsAndWritesNoRow()
    {
        var definitionId = SeedProcessDefinition(TenantA);
        var version = WfTenantEntityFactories.NewProcessDefinitionVersion(TenantA, true, definitionId);
        version.GraphJson = "{}";
        _dcA.Add(version);
        _dcA.SaveChanges();

        var engine = MakeMinimalEngine(_dcA);

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            engine.StartAsync(version.ID, null, "initiator", tenantCode: null));
        StringAssert.Contains(ex.Message, "does not match ProcessDefinitionVersion");

        var anyInstanceWritten = _dcA.Set<ProcessInstance>()
            .IgnoreQueryFilters()
            .Any(i => i.DefinitionVersionId == version.ID);
        Assert.IsFalse(anyInstanceWritten,
            "#899: a null tenantCode against a tenant-owned version must throw BEFORE any " +
            "ProcessInstance row is written.");
    }

    /// <summary>
    /// Which line's deletion turns this red: the
    /// `if (!string.Equals(tenantCode, _dc.TenantCode, StringComparison.Ordinal))` guard in
    /// <c>WorkflowDefinitionStore.CreateDefinitionAsync</c> (WorkflowDefinitionStore.cs). Delete
    /// it and this test's exception assertion fails, and the IgnoreQueryFilters() follow-up
    /// fails too (a ProcessDefinition row WAS written, under the wrong tenant).
    /// </summary>
    [TestMethod]
    public async Task CreateDefinitionAsync_TenantCodeMismatch_ThrowsAndWritesNoRow()
    {
        using var store = new WorkflowDefinitionStore(_dcA); // _dcA.TenantCode == TenantA

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.CreateDefinitionAsync(
                new CreateDefinitionRequest { Code = "MismatchCode", Name = "n" },
                tenantCode: "SomeOtherTenant", createdBy: "tester"));
        StringAssert.Contains(ex.Message, "does not match the calling context's own TenantCode");

        var anyRowWritten = _dcA.Set<ProcessDefinition>()
            .IgnoreQueryFilters()
            .Any(d => d.Code == "MismatchCode");
        Assert.IsFalse(anyRowWritten,
            "#899: a tenantCode/context mismatch must throw BEFORE any ProcessDefinition row is " +
            "written -- not write one under the mismatched tenant and only fail visibly later.");
    }

    /// <summary>
    /// Same guard, NULL-vs-non-null shape (reviewer's explicit ask): a caller passing no tenant
    /// at all against a context that DOES have one must also fail closed.
    /// </summary>
    [TestMethod]
    public async Task CreateDefinitionAsync_NullTenantCodeParameter_AgainstNonNullContextTenant_ThrowsAndWritesNoRow()
    {
        using var store = new WorkflowDefinitionStore(_dcA); // _dcA.TenantCode == TenantA (non-null)

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.CreateDefinitionAsync(
                new CreateDefinitionRequest { Code = "NullMismatchCode", Name = "n" },
                tenantCode: null, createdBy: "tester"));
        StringAssert.Contains(ex.Message, "does not match the calling context's own TenantCode");

        var anyRowWritten = _dcA.Set<ProcessDefinition>()
            .IgnoreQueryFilters()
            .Any(d => d.Code == "NullMismatchCode");
        Assert.IsFalse(anyRowWritten,
            "#899: a null tenantCode against a tenant-owned context must throw BEFORE any " +
            "ProcessDefinition row is written.");
    }
}
