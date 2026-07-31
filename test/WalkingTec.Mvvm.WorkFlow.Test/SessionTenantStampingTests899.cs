#nullable enable
// #899 session-half -- SessionTenantStampingTests899.cs
//
// TenantFilterInvariantTests.cs (the #899 model-half fixture) proved the ITenant/soft-delete
// query filter EXISTS and BEHAVES correctly when bound to a context whose TenantCode was already
// set by hand (`_dcA.SetTenantCode(TenantA)`). It never proved that a REAL request-path service,
// resolved through the REAL DI container the way `AddWtmWorkFlow`/`AddWtmWorkFlowDesigner` wire
// it, ever sets that TenantCode in the first place -- and in production it did not: every one of
// `WorkflowEngine`/`WorkflowTimerExecutor` (via `ScopedWorkflowDataContextHolder.Resolve()` ->
// `ResolveDataContext`), `WorkflowDefinitionStore`, and `ProcessDefinitionPublisher` built their
// own module DataContext via `IWtmDataContextFactory.CreateDC()` called with NO arguments, so
// `TenantCode` was unconditionally `null` regardless of who was actually asking. In a migrated
// multi-tenant deployment the (correctly-wired) filter then matched ZERO rows for every module
// read -- worse than the pre-#899 unfiltered state, not better.
//
// ALL 605 pre-#899-session-half WorkFlow tests (including every test in
// TenantFilterInvariantTests.cs and ProdDiReproTests.cs) use either the internal
// direct-DbContext test constructor or a hand-stamped `SetTenantCode` call, so none of them can
// structurally observe this defect -- this is precisely why production was broken while CI
// stayed green. The tests below are the first in this suite to build a REAL DI container
// (mirroring ProdDiReproTests.cs's own `BuildProdLikeProvider` pattern) with a REAL, tenant-bearing
// `WTMContext` scope and resolve `IWorkflowEngine`/`IWorkflowDefinitionStore`/
// `IProcessDefinitionPublisher` from it -- never hand-stamping the DataContext directly.
//
// Fix under test: `ResolveAmbientTenant(IServiceProvider)` (new) reads
// `WTMContext.LoginUserInfo?.CurrentTenant`; `ResolveDataContext` (ServiceCollectionExtensions.cs)
// now calls `dc.SetTenantCode(ResolveAmbientTenant(sp))` immediately after the factory creates a
// context, and `WorkflowDefinitionStore`/`ProcessDefinitionPublisher` gained a
// `(IWtmDataContextFactory, string? tenantCode)` constructor overload that does the same, wired
// from their own DI factory lambdas.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Services;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.ViewModels;

namespace WalkingTec.Mvvm.WorkFlow.Test;

/// <summary>
/// Minimal spy <see cref="ILoggerProvider"/> that records every log entry emitted through ANY
/// category, for the two self-check log tests below. Deliberately does not filter by category at
/// creation time -- tests filter <see cref="Entries"/> themselves so a wrong/renamed category
/// string in production would show up as "0 entries found" rather than being silently masked by
/// a matching filter here.
/// </summary>
internal sealed class SpyLoggerProvider899 : ILoggerProvider
{
    public List<(LogLevel Level, string Category, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new SpyLogger(categoryName, Entries);

    public void Dispose()
    {
    }

    private sealed class SpyLogger : ILogger
    {
        private readonly string _category;
        private readonly List<(LogLevel, string, string)> _entries;

        public SpyLogger(string category, List<(LogLevel, string, string)> entries)
        {
            _category = category;
            _entries = entries;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, _category, formatter(state, exception)));
            }
        }
    }
}

/// <summary>
/// #899 session-half: fake <see cref="IWtmDataContextFactory"/> that hands back a fresh, real,
/// DbContext-backed <see cref="IDataContext"/> using the OBSOLETE, zero-argument
/// <c>ApplyWorkFlowModels()</c> overload -- i.e. simulates a consumer who has NOT migrated their
/// <c>DataContext.OnModelCreating</c> to pass <c>this</c>. Used only by the "unmigrated model"
/// self-check test below. Reuses <c>WfDemoShapedObsoleteContext</c>
/// (<c>TenantFilterInvariantTests.cs</c>, same assembly/namespace) rather than redeclaring it.
/// </summary>
internal sealed class UnmigratedModelDataContextFactory899 : IWtmDataContextFactory
{
    private readonly string _connectionString;

    public UnmigratedModelDataContextFactory899(string connectionString) => _connectionString = connectionString;

    public IDataContext? CreateDC(
        string? currentCs = null,
        string? currentTenant = null,
        string? refererDomain = null,
        string? userCode = null,
        bool isLog = false,
        string? cskey = null,
        bool logerror = true)
        => new WfDemoShapedObsoleteContext(_connectionString, DBTypeEnum.SQLite);
}

[TestClass]
public class SessionTenantStampingTests899
{
    // ── Shared helpers ───────────────────────────────────────────────────────────────────────

    private static IServiceCollection NewProdLikeServices(string dbName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        // Mirrors AddWtmContext (Core IServiceExtension.cs / Mvc FrameworkServiceExtension.cs).
        services.TryAddScoped<IDataContext, NullContext>();
        services.AddScoped<IWtmDataContextFactory>(_ => new ProdDiReproDataContextFactory(dbName));
        // The piece ProdDiReproTests.cs never needed: a real, scoped WTMContext so
        // ResolveAmbientTenant(sp) has something to read. No IHttpContextAccessor is registered,
        // so WTMContext.HttpContext is null -- LoginUserInfo's getter short-circuits to null
        // unless a test explicitly sets it via the public setter (mirrors the real background
        // timer scope, which also has no HttpContext).
        services.AddScoped<WTMContext>();
        return services;
    }

    /// <summary>
    /// Start -> Approval(Sequential) -> End. Proven shape (mirrors
    /// <c>PublishFlowTests.MinimalGraph</c>) -- only used to get a valid, publishable
    /// <c>ProcessDefinitionVersion</c> for <see cref="IWorkflowEngine.StartAsync"/> to load; these
    /// tests never need the approval task to actually be actioned.
    /// </summary>
    private static WorkflowGraph MinimalGraph(string key) => new()
    {
        SchemaVersion = 1,
        Key = key,
        Name = "SessionStampingTestGraph",
        Nodes = new()
        {
            new NodeDef { NodeKey = "start", Kind = NodeKind.Start },
            new NodeDef
            {
                NodeKey = "mgr",
                Kind = NodeKind.Approval,
                ApproveMode = ApproveMode.Sequential,
                RejectGate = RejectGate.Immediate,
                RejectPolicy = RejectPolicy.ReturnToInitiator,
                ApproverRule = new ApproverRuleDef { Type = "Role", Value = "MANAGER" },
            },
            new NodeDef { NodeKey = "end", Kind = NodeKind.End },
        },
        Transitions = new()
        {
            new TransitionDef { From = "start", To = "mgr" },
            new TransitionDef { From = "mgr", To = "end" },
        },
    };

    // ── (a) DI stamping: a scope with CurrentTenant='A' yields a holder DC whose TenantCode=='A' ──

    /// <summary>
    /// Which line's deletion turns this red: the <c>dc.SetTenantCode(ResolveAmbientTenant(sp))</c>
    /// call inside <c>ResolveDataContext</c> (<c>ServiceCollectionExtensions.cs</c>). This is the
    /// SAME resolution path <c>IWorkflowEngine</c>/<c>WorkflowTimerExecutor</c> use via
    /// <c>ScopedWorkflowDataContextHolder.Resolve()</c>.
    /// </summary>
    [TestMethod]
    public void ResolveDataContext_ScopeWithCurrentTenantA_StampsHolderDataContext()
    {
        var dbName = $"WfSess899A_{Guid.NewGuid():N}";
        using var keepAlive = SqliteSharedMemoryFixture.OpenKeepAliveWithBusyTimeout(dbName);
        using (var setup = new WfProdDiTestContext(dbName))
        {
            setup.Database.EnsureCreated();
        }

        var services = NewProdLikeServices(dbName);
        services.AddWtmWorkFlow();
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var wtm = scope.ServiceProvider.GetRequiredService<WTMContext>();
        wtm.LoginUserInfo = new LoginUserInfo { CurrentTenant = "A" };

        var holder = scope.ServiceProvider.GetRequiredService<ScopedWorkflowDataContextHolder>();
        var dc = holder.Resolve();

        Assert.AreEqual("A", dc.TenantCode,
            "#899 session-half: which line's deletion turns this red -- the " +
            "`dc.SetTenantCode(ResolveAmbientTenant(sp))` call in ResolveDataContext " +
            "(ServiceCollectionExtensions.cs). Without it this holder DataContext's TenantCode " +
            "stays null regardless of the ambient WTMContext's CurrentTenant.");
    }

    // ── (c) Background scope: no HttpContext -> DC TenantCode == null ──────────────────────────

    /// <summary>
    /// Which line's deletion turns this red: any non-null fallback added to
    /// <c>ResolveAmbientTenant</c> (<c>ServiceCollectionExtensions.cs</c>) -- this test exists
    /// specifically to stop a future "helpful" change that stamps the background timer scope with
    /// something other than null. Mirrors the real <c>WorkflowTimerHostedService.TickAsync</c>
    /// scope: a bare DI scope with no <c>HttpContext</c> and no <c>LoginUserInfo</c> ever set.
    /// </summary>
    [TestMethod]
    public void ResolveDataContext_BackgroundScopeWithNoLoginUserInfo_TenantCodeStaysNull()
    {
        var dbName = $"WfSess899C_{Guid.NewGuid():N}";
        using var keepAlive = SqliteSharedMemoryFixture.OpenKeepAliveWithBusyTimeout(dbName);
        using (var setup = new WfProdDiTestContext(dbName))
        {
            setup.Database.EnsureCreated();
        }

        var services = NewProdLikeServices(dbName);
        services.AddWtmWorkFlow();
        var sp = services.BuildServiceProvider();

        // No IHttpContextAccessor registered, LoginUserInfo never touched -- WTMContext.HttpContext
        // is null, so the LoginUserInfo getter short-circuits to null (WTMContext.User.cs).
        using var scope = sp.CreateScope();
        var holder = scope.ServiceProvider.GetRequiredService<ScopedWorkflowDataContextHolder>();
        var dc = holder.Resolve();

        Assert.IsNull(dc.TenantCode,
            "#899 session-half: a scope with no HttpContext/LoginUserInfo (the background timer " +
            "shape) must produce a DataContext with TenantCode==null, byte-identical to this " +
            "path's behaviour before #899's session-half fix -- the pre-existing per-candidate " +
            "SetTenantCode calls in WorkflowTimerExecutor.*.cs remain the ONLY tenant source for " +
            "that path.");
    }

    // ── (b) Two-tenant behaviour through DI-resolved store/engine (not hand-stamped contexts) ──

    /// <summary>
    /// Which line's deletion turns this red: the same
    /// <c>dc.SetTenantCode(ResolveAmbientTenant(sp))</c> stamp as (a) above -- it is what the
    /// #899 model-half query filter binds to. Exercises the REAL production wiring end to end:
    /// tenant A creates a definition via the DI-resolved <see cref="IWorkflowDefinitionStore"/>,
    /// lists it, publishes it via the DI-resolved <see cref="IProcessDefinitionPublisher"/>; then
    /// tenant B (a fresh scope) cannot see it in its own list, and the DI-resolved
    /// <see cref="IWorkflowEngine"/>'s <c>StartAsync</c> against tenant A's versionId reports
    /// not-found -- never a hand-stamped <c>SetTenantCode</c> call anywhere in this test.
    /// </summary>
    [TestMethod]
    public async Task TwoTenantBehaviour_ViaDIResolvedStoreAndEngine_CreateListPublishStartAsync()
    {
        var dbName = $"WfSess899B_{Guid.NewGuid():N}";
        using var keepAlive = SqliteSharedMemoryFixture.OpenKeepAliveWithBusyTimeout(dbName);
        using (var setup = new WfProdDiTestContext(dbName))
        {
            setup.Database.EnsureCreated();
        }

        var services = NewProdLikeServices(dbName);
        services.AddWtmWorkFlow();
        services.AddWtmWorkFlowDesigner();
        var sp = services.BuildServiceProvider();

        var code = $"SESS899B-{Guid.NewGuid():N}";
        Guid versionId;

        // ── Tenant A: create -> list -> publish, all through DI-resolved services ──────────────
        using (var scopeA = sp.CreateScope())
        {
            var wtmA = scopeA.ServiceProvider.GetRequiredService<WTMContext>();
            wtmA.LoginUserInfo = new LoginUserInfo { CurrentTenant = "A" };

            var storeA = scopeA.ServiceProvider.GetRequiredService<IWorkflowDefinitionStore>();
            var createResult = await storeA.CreateDefinitionAsync(
                new CreateDefinitionRequest { Code = code, Name = "Session stamping test" },
                tenantCode: "A", createdBy: "tester");
            Assert.AreEqual(CreateDefinitionOutcome.Created, createResult.Outcome,
                "Setup precondition failed: tenant A's own create through DI must succeed.");

            var listA = await storeA.ListDefinitionsAsync(1, 50);
            Assert.IsTrue(listA.Items.Any(d => d.Code == code),
                "Setup precondition failed: tenant A must see its own newly-created definition.");

            var publisherA = scopeA.ServiceProvider.GetRequiredService<IProcessDefinitionPublisher>();
            var publishResult = await publisherA.PublishAsync(code, MinimalGraph(code), "tester");
            Assert.AreEqual(PublishOutcome.Published, publishResult.Outcome,
                "Setup precondition failed: tenant A's own publish through DI must succeed.");
            versionId = publishResult.VersionId!.Value;
        }

        // ── Tenant B: fresh scope, cannot see A's definition or start A's version ───────────────
        using (var scopeB = sp.CreateScope())
        {
            var wtmB = scopeB.ServiceProvider.GetRequiredService<WTMContext>();
            wtmB.LoginUserInfo = new LoginUserInfo { CurrentTenant = "B" };

            var storeB = scopeB.ServiceProvider.GetRequiredService<IWorkflowDefinitionStore>();
            var listB = await storeB.ListDefinitionsAsync(1, 50);
            Assert.IsFalse(listB.Items.Any(d => d.Code == code),
                "#899 session-half: tenant B's DI-resolved store must NOT see tenant A's " +
                "definition. Which line's deletion turns this red: the " +
                "`dc.SetTenantCode(ResolveAmbientTenant(sp))` stamp in ResolveDataContext, or the " +
                "`_dc.SetTenantCode(tenantCode)` call in the WorkflowDefinitionStore(factory, " +
                "tenantCode) constructor -- without either, both scopes' module DataContexts have " +
                "TenantCode==null and the #899 filter matches nothing for anyone.");

            var engineB = scopeB.ServiceProvider.GetRequiredService<IWorkflowEngine>();
            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                engineB.StartAsync(versionId, null, "initiator", tenantCode: "B"));
            StringAssert.Contains(ex.Message, "not found or not valid",
                "#899 session-half: tenant B's DI-resolved engine must report tenant A's " +
                "versionId as not found -- same stamping requirement as above, via the " +
                "ScopedWorkflowDataContextHolder/ResolveDataContext path this time.");
        }
    }

    // ── (d) Guard same-source: DI-resolved CreateDefinitionAsync 'A' succeeds; after manually ──
    // ── re-stamping the DC to 'B', the same 'A' throws ──────────────────────────────────────────

    private static readonly FieldInfo StoreDcField = typeof(WorkflowDefinitionStore)
        .GetField("_dc", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("WorkflowDefinitionStore._dc field not found — signature changed?");

    /// <summary>
    /// Which line's deletion turns this red: the
    /// <c>if (!string.Equals(tenantCode, _dc.TenantCode, StringComparison.Ordinal))</c> guard's
    /// <c>throw</c> in <c>CreateDefinitionAsync</c> (<c>WorkflowDefinitionStore.cs</c>). Proves the
    /// #899 session-half stamp and the pre-existing write-provenance guard (PR #918 review) now
    /// compare genuinely SAME-SOURCED values through DI, not two independently-derived ones that
    /// happen to usually agree -- and that the guard still catches real drift (something else
    /// re-stamping the same DataContext after construction) rather than being a tautological
    /// reject-everything check the way it was before the session-half fix (when `_dc.TenantCode`
    /// was unconditionally null and any non-null `tenantCode` a real controller passed would
    /// always throw).
    /// </summary>
    [TestMethod]
    public async Task CreateDefinitionAsync_ViaDI_GuardRejectsAfterDcManuallyReStamped()
    {
        var dbName = $"WfSess899D_{Guid.NewGuid():N}";
        using var keepAlive = SqliteSharedMemoryFixture.OpenKeepAliveWithBusyTimeout(dbName);
        using (var setup = new WfProdDiTestContext(dbName))
        {
            setup.Database.EnsureCreated();
        }

        var services = NewProdLikeServices(dbName);
        services.AddWtmWorkFlowDesigner();
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var wtm = scope.ServiceProvider.GetRequiredService<WTMContext>();
        wtm.LoginUserInfo = new LoginUserInfo { CurrentTenant = "A" };

        var store = scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionStore>();

        var okResult = await store.CreateDefinitionAsync(
            new CreateDefinitionRequest { Code = $"SESS899D1-{Guid.NewGuid():N}", Name = "n" },
            tenantCode: "A", createdBy: "tester");
        Assert.AreEqual(CreateDefinitionOutcome.Created, okResult.Outcome,
            "Setup precondition failed: same-tenant create through DI must succeed.");

        // Simulate drift: something re-stamps the SAME underlying DataContext to a different
        // tenant after construction (a non-WTM caller misusing the store directly, or a future
        // regression reverting the controller's LoginUserInfo?.CurrentTenant alignment).
        var dc = (IDataContext)StoreDcField.GetValue(store)!;
        dc.SetTenantCode("B");

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.CreateDefinitionAsync(
                new CreateDefinitionRequest { Code = $"SESS899D2-{Guid.NewGuid():N}", Name = "n" },
                tenantCode: "A", createdBy: "tester"));
        StringAssert.Contains(ex.Message, "does not match the calling context's own TenantCode",
            "#899 session-half: after the DC drifts to 'B', a caller still claiming 'A' must be " +
            "rejected by the guard's same-source invariant, not silently written under either " +
            "tenant.");
    }

    // ── (e) Self-check log, both directions ─────────────────────────────────────────────────────

    private static void ResetTenantFilterCheckFlag()
    {
        // `_tenantFilterCheckLogged` is deliberately process-lifetime (see its own doc comment in
        // ServiceCollectionExtensions.cs) -- reset it via reflection before each of these two
        // tests so each observes a clean slate regardless of what any other test in this
        // (sequentially-executed, no [assembly: Parallelize]) assembly already triggered.
        typeof(ServiceCollectionExtensions)
            .GetField("_tenantFilterCheckLogged", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, 0);
    }

    /// <summary>
    /// Which line's deletion turns this red: the <c>logger?.LogWarning(message)</c> /
    /// <c>logger?.LogError(message)</c> call inside <c>WarnIfTenantFilterMissing</c>
    /// (<c>ServiceCollectionExtensions.cs</c>), or the <c>Interlocked.Exchange</c> guard being
    /// removed (which would make this fire on EVERY resolution instead of exactly once).
    /// </summary>
    [TestMethod]
    public void WarnIfTenantFilterMissing_UnmigratedModel_LogsExactlyOnce()
    {
        ResetTenantFilterCheckFlag();

        var dbName = $"WfSess899EUnmig_{Guid.NewGuid():N}";
        var connStr = $"DataSource={dbName}?mode=memory&cache=shared";
        using var keepAlive = new SqliteConnection(connStr);
        keepAlive.Open();
        using (var setup = new WfDemoShapedObsoleteContext(connStr, DBTypeEnum.SQLite))
        {
            setup.Database.EnsureCreated();
        }

        var spy = new SpyLoggerProvider899();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(spy));
        services.AddOptions();
        services.AddScoped<IWtmDataContextFactory>(_ => new UnmigratedModelDataContextFactory899(connStr));
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();

        // Two calls in the SAME process -- only the FIRST may log.
        var (_, owned1) = ServiceCollectionExtensions.ResolveDataContext(scope.ServiceProvider);
        var (_, owned2) = ServiceCollectionExtensions.ResolveDataContext(scope.ServiceProvider);
        Assert.IsTrue(owned1 && owned2, "Setup precondition failed: both resolutions must go through the factory path.");

        var relevant = spy.Entries.Where(e => e.Category == "WalkingTec.Mvvm.WorkFlow.TenantFilterCheck").ToList();
        Assert.AreEqual(1, relevant.Count,
            "#899 session-half: an un-migrated model (obsolete zero-arg ApplyWorkFlowModels()) " +
            $"must log the self-check exactly once per process, not {relevant.Count} times.");
        StringAssert.Contains(relevant[0].Message, "#899");
        Assert.AreEqual(LogLevel.Warning, relevant[0].Level,
            "No GlobalData is registered in this test container (AllTenant empty/absent), so the " +
            "single-tenant branch (LogWarning, not LogError) is expected.");
    }

    /// <summary>
    /// Which line's deletion turns this red: the
    /// <c>if (hasFilter) { return; }</c> early-return inside <c>WarnIfTenantFilterMissing</c>
    /// (<c>ServiceCollectionExtensions.cs</c>) -- delete it and a migrated consumer would start
    /// logging a false "not migrated" warning on every process start.
    /// </summary>
    [TestMethod]
    public void WarnIfTenantFilterMissing_MigratedModel_NeverLogs()
    {
        ResetTenantFilterCheckFlag();

        var dbName = $"WfSess899EMig_{Guid.NewGuid():N}";
        using var keepAlive = SqliteSharedMemoryFixture.OpenKeepAliveWithBusyTimeout(dbName);
        using (var setup = new WfProdDiTestContext(dbName))
        {
            setup.Database.EnsureCreated();
        }

        var spy = new SpyLoggerProvider899();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(spy));
        services.AddOptions();
        services.AddScoped<IWtmDataContextFactory>(_ => new ProdDiReproDataContextFactory(dbName));
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var (_, owned) = ServiceCollectionExtensions.ResolveDataContext(scope.ServiceProvider);
        Assert.IsTrue(owned, "Setup precondition failed: resolution must go through the factory path.");

        var relevant = spy.Entries.Where(e => e.Category == "WalkingTec.Mvvm.WorkFlow.TenantFilterCheck").ToList();
        Assert.AreEqual(0, relevant.Count,
            "#899 session-half: a migrated consumer (ApplyWorkFlowModels(this)) must never trigger " +
            $"the missing-filter self-check log; found {relevant.Count} entries.");
    }
}
