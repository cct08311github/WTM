#nullable enable
// WF-5 — DBTypeEnum.Memory startup guard unit tests.
//
// Spec §9 invariant #8: the WorkFlow engine requires a real relational provider.
// DBTypeEnum.Memory (EF InMemory) cannot execute ExecuteUpdateAsync — the same root
// cause that broke Mvc.Tests in #119/#162.
//
// The eager BuildServiceProvider() guard was REMOVED in PR #240 because calling
// BuildServiceProvider() at registration time throws "Cannot resolve scoped service
// from root provider" when IDataContext is registered as Scoped (the production pattern).
// The Memory check is now LAZY — exercised via ValidateDbType() on first engine use (WF-6).
//
// These tests verify:
//   (a) AddWtmWorkFlow does NOT throw, even when IDataContext (Scoped) has DBType == Memory.
//       The host still registers cleanly; the guard fires on first engine use instead.
//   (b) AddWtmWorkFlow works correctly through a REAL scope-validating ServiceProvider
//       (ValidateScopes=true) with IDataContext registered as Scoped — this is the
//       production path and must never crash at AddWtmWorkFlow() call time.
//   (c) ValidateDbType() helper (lazy path) throws for Memory and passes for relational.
//   (d) [#271] Integration: WorkflowEngine.StartAsync respects ValidateDbTypeOnFirstUse=true —
//       throws InvalidOperationException("Memory") when IDataContext.DBType==Memory, and
//       proceeds past the guard (to the next engine check) when DBType==SQLite.

using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Test;

[TestClass]
public class MemoryGuardTests
{
    // ── Helper ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a ServiceCollection that has a SCOPED mock IDataContext with the specified
    /// DBTypeEnum.  This mirrors the real production registration pattern.
    /// </summary>
    private static IServiceCollection BuildServicesWithScopedDbType(DBTypeEnum dbType)
    {
        var services = new ServiceCollection();

        // Register a mock IDataContext with the specified DBTypeEnum — as SCOPED,
        // matching the production pattern.  Previously registered as Singleton, which
        // masked the BuildServiceProvider() crash (Singleton can always be resolved from
        // the root provider; Scoped cannot).
        var mockDc = new Mock<IDataContext>();
        mockDc.SetupProperty(x => x.DBType, dbType);
        services.AddScoped<IDataContext>(_ => mockDc.Object);

        return services;
    }

    // ── AddWtmWorkFlow must NOT throw at registration — even for Memory ────────

    /// <summary>
    /// PR #240 regression guard: AddWtmWorkFlow must NOT throw when IDataContext is
    /// registered as Scoped (production pattern) and DBType is Memory.
    /// The old eager BuildServiceProvider() path crashed every host here.
    /// Guard is now lazy (ValidateDbType on first engine use, WF-6).
    /// </summary>
    [TestMethod]
    public void AddWtmWorkFlow_DoesNotThrow_AtRegistrationTime_EvenWithScopedMemoryContext()
    {
        var services = BuildServicesWithScopedDbType(DBTypeEnum.Memory);

        // Must not throw — the Memory check is deferred to first engine use (WF-6).
        services.AddWtmWorkFlow();
    }

    /// <summary>
    /// Exercises AddWtmWorkFlow through a REAL built ServiceProvider with scope-validation
    /// enabled (ValidateScopes=true) — exactly the environment that crashed production.
    /// IDataContext registered as Scoped to prove the real DI path works cleanly.
    /// </summary>
    [TestMethod]
    public void AddWtmWorkFlow_WorksThroughRealScopeValidatingProvider_WithScopedDataContext()
    {
        var services = BuildServicesWithScopedDbType(DBTypeEnum.SQLite);
        services.AddWtmWorkFlow();

        // Build a real provider WITH scope validation on — this is what ASP.NET Core
        // does by default in the Development environment, and what crashed previously.
        var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });

        // Resolve IDataContext inside a scope — must succeed, no DI exception.
        using var scope = provider.CreateScope();
        var dc = scope.ServiceProvider.GetRequiredService<IDataContext>();
        Assert.AreEqual(DBTypeEnum.SQLite, dc.DBType,
            "IDataContext must be resolvable inside a scope after AddWtmWorkFlow.");
    }

    // ── Relational providers → AddWtmWorkFlow must NOT throw ──────────────────

    [TestMethod]
    public void AddWtmWorkFlow_DoesNotThrow_WhenDbTypeIsSqlServer()
    {
        var services = BuildServicesWithScopedDbType(DBTypeEnum.SqlServer);
        services.AddWtmWorkFlow();
    }

    [TestMethod]
    public void AddWtmWorkFlow_DoesNotThrow_WhenDbTypeIsSQLite()
    {
        var services = BuildServicesWithScopedDbType(DBTypeEnum.SQLite);
        services.AddWtmWorkFlow();
    }

    [TestMethod]
    public void AddWtmWorkFlow_DoesNotThrow_WhenDbTypeIsPgSql()
    {
        var services = BuildServicesWithScopedDbType(DBTypeEnum.PgSql);
        services.AddWtmWorkFlow();
    }

    [TestMethod]
    public void AddWtmWorkFlow_DoesNotThrow_WhenDbTypeIsMySql()
    {
        var services = BuildServicesWithScopedDbType(DBTypeEnum.MySql);
        services.AddWtmWorkFlow();
    }

    [TestMethod]
    public void AddWtmWorkFlow_DoesNotThrow_WhenDbTypeIsOracle()
    {
        var services = BuildServicesWithScopedDbType(DBTypeEnum.Oracle);
        services.AddWtmWorkFlow();
    }

    // ── No IDataContext registered → must NOT throw ────────────────────────────

    /// <summary>
    /// When no IDataContext is registered in the container (e.g. consumer configures
    /// DataContext after AddWtmWorkFlow), AddWtmWorkFlow registers cleanly and the
    /// Memory guard fires lazily on first engine use (WF-6).
    /// </summary>
    [TestMethod]
    public void AddWtmWorkFlow_DoesNotThrow_WhenNoDataContextRegistered()
    {
        var services = new ServiceCollection();
        // No IDataContext registered — registers cleanly, guard fires later.
        services.AddWtmWorkFlow();
    }

    // ── Options registration ──────────────────────────────────────────────────

    /// <summary>
    /// AddWtmWorkFlow must register WorkFlowOptions configure action regardless of
    /// whether IDataContext is registered or what DBType it has.
    /// </summary>
    [TestMethod]
    public void AddWtmWorkFlow_RegistersConfigureOptions_Unconditionally()
    {
        var services = BuildServicesWithScopedDbType(DBTypeEnum.Memory);
        services.AddWtmWorkFlow(o => o.InitiatorAutoApprove = true);

        bool hasConfigureOptions = services.Any(sd =>
            sd.ServiceType.IsGenericType &&
            sd.ServiceType.GetGenericTypeDefinition() == typeof(Microsoft.Extensions.Options.IConfigureOptions<>) &&
            sd.ServiceType.GenericTypeArguments.Length == 1 &&
            sd.ServiceType.GenericTypeArguments[0] == typeof(WorkFlowOptions));

        Assert.IsTrue(hasConfigureOptions,
            "WorkFlowOptions configure descriptor must be registered by AddWtmWorkFlow.");
    }

    // ── ValidateDbTypeOnFirstUse path (lazy guard, called by engine entry points) ──

    /// <summary>
    /// ValidateDbType() helper throws for Memory (used by the lazy guard path in WF-6).
    /// This is the only Memory enforcement that survives PR #240.
    /// </summary>
    [TestMethod]
    public void ValidateDbType_Throws_WhenDbTypeIsMemory()
    {
        var mockDc = new Mock<IDataContext>();
        mockDc.SetupProperty(x => x.DBType, DBTypeEnum.Memory);

        var ex = Assert.ThrowsException<InvalidOperationException>(
            () => ServiceCollectionExtensions.ValidateDbType(mockDc.Object));

        StringAssert.Contains(ex.Message, "Memory");
        StringAssert.Contains(ex.Message, "ExecuteUpdateAsync",
            "Exception message must explain the root cause (EF InMemory cannot translate ExecuteUpdateAsync).");
    }

    /// <summary>
    /// ValidateDbType() helper does NOT throw for SqlServer (relational provider).
    /// </summary>
    [TestMethod]
    public void ValidateDbType_DoesNotThrow_WhenDbTypeIsSqlServer()
    {
        var mockDc = new Mock<IDataContext>();
        mockDc.SetupProperty(x => x.DBType, DBTypeEnum.SqlServer);

        // Should not throw.
        ServiceCollectionExtensions.ValidateDbType(mockDc.Object);
    }

    /// <summary>
    /// ValidateDbType() helper does NOT throw for SQLite (relational provider).
    /// </summary>
    [TestMethod]
    public void ValidateDbType_DoesNotThrow_WhenDbTypeIsSQLite()
    {
        var mockDc = new Mock<IDataContext>();
        mockDc.SetupProperty(x => x.DBType, DBTypeEnum.SQLite);

        ServiceCollectionExtensions.ValidateDbType(mockDc.Object);
    }
}

// ── #271 integration test helpers and test class ──────────────────────────────

/// <summary>
/// Minimal DbContext subclass that ALSO implements IDataContext so it can be passed
/// to the production WorkflowEngine constructor (which casts IDataContext → DbContext).
/// Only DBType is meaningful; all other IDataContext members throw NotImplementedException.
/// Used exclusively by <see cref="ValidateDbTypeOnFirstUseIntegrationTests"/>.
/// </summary>
internal sealed class MinimalIDataContextDbContext : DbContext, IDataContext
{
    private readonly string _connStr;

    public MinimalIDataContextDbContext(string connStr, DBTypeEnum dbType)
    {
        _connStr = connStr;
        DBType = dbType;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder b) =>
        b.UseSqlite($"DataSource={_connStr}?mode=memory&cache=shared");

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<ProcessDefinitionVersion>(e =>
        {
            e.ToTable("Wf_PDV_Guard");
            e.HasKey(x => x.ID);
            e.Property(x => x.GraphJson).IsRequired();
            e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.DefinitionId);
            e.Property(x => x.VersionNo);
            e.Property(x => x.TenantCode).HasMaxLength(50);
            e.Property(x => x.IsValid);
            e.Ignore(x => x.Definition);
        });
    }

    // ── IDataContext ───────────────────────────────────────────────────────────
    public DBTypeEnum DBType { get; set; }
    public bool IsFake { get; set; }
    public bool IsDebug { get; set; }
    public string? CurrentUserCode { get; set; }
    public string? TenantCode { get; } = null;
    public string CSName { get; set; } = string.Empty;

    // All remaining members throw — they are never called because the guard fires first
    // (Memory path) or the test catches the first downstream exception (SQLite path).
    public void AddEntity<T>(T entity) where T : TopBasePoco => throw new NotImplementedException();
    public void UpdateEntity<T>(T entity) where T : TopBasePoco => throw new NotImplementedException();
    public void UpdateProperty<T>(T entity, Expression<Func<T, object>> fieldExp) where T : TopBasePoco => throw new NotImplementedException();
    public void UpdateProperty<T>(T entity, string fieldName) where T : TopBasePoco => throw new NotImplementedException();
    public void DeleteEntity<T>(T entity) where T : TopBasePoco => throw new NotImplementedException();
    public void CascadeDelete<T>(T entity) where T : TreePoco => throw new NotImplementedException();
    public Task<bool> DataInit(object? AllModel, bool IsSpa) => throw new NotImplementedException();
    public void EnsureCreate() { }
    public IDataContext CreateNew() => throw new NotImplementedException();
    public IDataContext ReCreate(ILoggerFactory? _logger = null) => throw new NotImplementedException();
    public DataTable RunSP(string command, params object[] paras) => throw new NotImplementedException();
    public IEnumerable<TElement> RunSP<TElement>(string command, params object[] paras) => throw new NotImplementedException();
    public DataTable RunSQL(string command, params object[] paras) => throw new NotImplementedException();
    public IEnumerable<TElement> RunSQL<TElement>(string sql, params object[] paras) => throw new NotImplementedException();
    public DataTable Run(string sql, CommandType commandType, params object[] paras) => throw new NotImplementedException();
    public IEnumerable<TElement> Run<TElement>(string sql, CommandType commandType, params object[] paras) => throw new NotImplementedException();
    public object CreateCommandParameter(string name, object value, ParameterDirection dir) => throw new NotImplementedException();
    public void SetLoggerFactory(ILoggerFactory factory) { }
    public void SetTenantCode(string? tc) { }
}

/// <summary>
/// #271 integration tests: prove WorkflowEngine.StartAsync actually invokes
/// the lazy ValidateDbTypeOnFirstUse guard when wired (#271 fix).
/// </summary>
[TestClass]
public class ValidateDbTypeOnFirstUseIntegrationTests : IDisposable
{
    private readonly string _dbName = $"WfGuard_{Guid.NewGuid():N}";
    private SqliteConnection _keepAlive = null!;

    [TestInitialize]
    public void Setup()
    {
        _keepAlive = new SqliteConnection($"DataSource={_dbName}?mode=memory&cache=shared");
        _keepAlive.Open();
        // Ensure the minimal table exists (SQLite path only — Memory path throws before any DB access).
        using var ctx = new MinimalIDataContextDbContext(_dbName, DBTypeEnum.SQLite);
        ctx.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _keepAlive?.Close();
        _keepAlive?.Dispose();
    }

    public void Dispose() => Cleanup();

    /// <summary>
    /// #271 — with ValidateDbTypeOnFirstUse=true and DBTypeEnum.Memory, StartAsync must
    /// throw InvalidOperationException containing "Memory" and "ExecuteUpdateAsync"
    /// BEFORE attempting any DB operation.
    /// This proves the lazy guard is actually wired into the engine entry point (not dead code).
    /// </summary>
    [TestMethod]
    public async Task StartAsync_WithMemoryDbType_Throws_WhenValidateDbTypeOnFirstUse()
    {
        // DBTypeEnum.Memory — guard must fire immediately.
        await using var dc = new MinimalIDataContextDbContext(_dbName, DBTypeEnum.Memory);

        var dispatcher = NodeKindDispatcher_Exposed.Create();
        var routingEvaluator = new WalkingTec.Mvvm.WorkFlow.Engine.Routing.WhitelistRoutingEvaluator(
            NullLogger<WalkingTec.Mvvm.WorkFlow.Engine.Routing.WhitelistRoutingEvaluator>.Instance);
        var options = new WorkFlowOptions { ValidateDbTypeOnFirstUse = true };

        // Production constructor — _dc is set, ValidateDbType will be called.
        var engine = new WorkflowEngine(
            dc,
            dispatcher,
            routingEvaluator,
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<WorkflowEngine>.Instance);

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => engine.StartAsync(Guid.NewGuid(), null, "initiator", null));

        StringAssert.Contains(ex.Message, "Memory",
            "Exception message must mention 'Memory'.");
        StringAssert.Contains(ex.Message, "ExecuteUpdateAsync",
            "Exception message must explain the root cause (EF InMemory cannot translate ExecuteUpdateAsync).");
    }

    /// <summary>
    /// #271 — with ValidateDbTypeOnFirstUse=true and a relational provider (SQLite),
    /// StartAsync must NOT throw the Memory guard exception.
    /// It will throw for a different reason (no matching ProcessDefinitionVersion) —
    /// proving the guard passed and the engine proceeded to normal DB logic.
    /// </summary>
    [TestMethod]
    public async Task StartAsync_WithSQLiteDbType_DoesNotThrowMemoryGuard_WhenValidateDbTypeOnFirstUse()
    {
        await using var dc = new MinimalIDataContextDbContext(_dbName, DBTypeEnum.SQLite);

        var dispatcher = NodeKindDispatcher_Exposed.Create();
        var routingEvaluator = new WalkingTec.Mvvm.WorkFlow.Engine.Routing.WhitelistRoutingEvaluator(
            NullLogger<WalkingTec.Mvvm.WorkFlow.Engine.Routing.WhitelistRoutingEvaluator>.Instance);
        var options = new WorkFlowOptions { ValidateDbTypeOnFirstUse = true };

        var engine = new WorkflowEngine(
            dc,
            dispatcher,
            routingEvaluator,
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<WorkflowEngine>.Instance);

        // Must NOT throw the Memory guard exception.
        // Will throw "ProcessDefinitionVersion not found" (expected — no seed data).
        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => engine.StartAsync(Guid.NewGuid(), null, "initiator", null));

        // Explicitly confirm it is NOT the Memory guard message.
        StringAssert.DoesNotMatch(ex.Message,
            new System.Text.RegularExpressions.Regex("Memory"),
            "Exception must NOT be the Memory guard — the guard should have passed for SQLite.");

        // The real exception should mention the missing version.
        StringAssert.Contains(ex.Message, "ProcessDefinitionVersion",
            "Exception must be the 'version not found' engine error, not the Memory guard.");
    }
}
