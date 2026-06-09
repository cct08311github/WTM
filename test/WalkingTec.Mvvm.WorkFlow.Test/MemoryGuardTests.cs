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

using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;

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
