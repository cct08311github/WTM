#nullable enable
// WF-5 — DBTypeEnum.Memory startup guard unit tests.
//
// Spec §9 invariant #8: the WorkFlow engine requires a real relational provider.
// DBTypeEnum.Memory (EF InMemory) cannot execute ExecuteUpdateAsync — the same root
// cause that broke Mvc.Tests in #119/#162.
//
// These tests verify:
//   (a) AddWtmWorkFlow throws InvalidOperationException when IDataContext.DBType == Memory.
//   (b) AddWtmWorkFlow does NOT throw for each supported relational provider.

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
    /// Builds a ServiceCollection that has a mock IDataContext with the specified DBTypeEnum.
    /// Then calls AddWtmWorkFlow() and verifies the guard behaves correctly.
    /// </summary>
    private static IServiceCollection BuildServicesWithDbType(DBTypeEnum dbType)
    {
        var services = new ServiceCollection();

        // Register a mock IDataContext with the specified DBTypeEnum.
        var mockDc = new Mock<IDataContext>();
        mockDc.SetupProperty(x => x.DBType, dbType);
        services.AddSingleton(mockDc.Object);

        return services;
    }

    // ── Memory → must throw ────────────────────────────────────────────────────

    /// <summary>
    /// WF-5: AddWtmWorkFlow must throw InvalidOperationException when DBTypeEnum is Memory.
    /// </summary>
    [TestMethod]
    public void AddWtmWorkFlow_Throws_WhenDbTypeIsMemory()
    {
        var services = BuildServicesWithDbType(DBTypeEnum.Memory);

        var ex = Assert.ThrowsException<InvalidOperationException>(
            () => services.AddWtmWorkFlow(),
            "AddWtmWorkFlow must throw InvalidOperationException when DBTypeEnum.Memory is configured.");

        StringAssert.Contains(ex.Message, "Memory",
            "Exception message should mention 'Memory' so the error is actionable.");
        StringAssert.Contains(ex.Message, "ExecuteUpdateAsync",
            "Exception message should mention 'ExecuteUpdateAsync' to explain the root cause.");
    }

    /// <summary>
    /// WF-5: The error message must be actionable — tell the developer what to do next.
    /// </summary>
    [TestMethod]
    public void AddWtmWorkFlow_MemoryThrow_MessageIsActionable()
    {
        var services = BuildServicesWithDbType(DBTypeEnum.Memory);

        var ex = Assert.ThrowsException<InvalidOperationException>(
            () => services.AddWtmWorkFlow());

        // Message must name at least one valid alternative.
        bool mentionsRelational =
            ex.Message.Contains("SQLite") ||
            ex.Message.Contains("SqlServer") ||
            ex.Message.Contains("relational");

        Assert.IsTrue(mentionsRelational,
            "Exception message must guide the developer toward a valid provider.");
    }

    // ── Relational providers → must NOT throw ──────────────────────────────────

    [TestMethod]
    public void AddWtmWorkFlow_DoesNotThrow_WhenDbTypeIsSqlServer()
    {
        var services = BuildServicesWithDbType(DBTypeEnum.SqlServer);
        // No exception expected.
        services.AddWtmWorkFlow();
    }

    [TestMethod]
    public void AddWtmWorkFlow_DoesNotThrow_WhenDbTypeIsSQLite()
    {
        var services = BuildServicesWithDbType(DBTypeEnum.SQLite);
        services.AddWtmWorkFlow();
    }

    [TestMethod]
    public void AddWtmWorkFlow_DoesNotThrow_WhenDbTypeIsPgSql()
    {
        var services = BuildServicesWithDbType(DBTypeEnum.PgSql);
        services.AddWtmWorkFlow();
    }

    [TestMethod]
    public void AddWtmWorkFlow_DoesNotThrow_WhenDbTypeIsMySql()
    {
        var services = BuildServicesWithDbType(DBTypeEnum.MySql);
        services.AddWtmWorkFlow();
    }

    [TestMethod]
    public void AddWtmWorkFlow_DoesNotThrow_WhenDbTypeIsOracle()
    {
        var services = BuildServicesWithDbType(DBTypeEnum.Oracle);
        services.AddWtmWorkFlow();
    }

    // ── No IDataContext registered → must NOT throw ────────────────────────────

    /// <summary>
    /// When no IDataContext is registered in the container (e.g. consumer configures
    /// DataContext after AddWtmWorkFlow), the eager guard silently skips — it cannot
    /// inspect a non-existent registration.  The lazy guard (WF-6) fires on first use.
    /// </summary>
    [TestMethod]
    public void AddWtmWorkFlow_DoesNotThrow_WhenNoDataContextRegistered()
    {
        var services = new ServiceCollection();
        // No IDataContext registered — guard skips gracefully.
        services.AddWtmWorkFlow();
    }

    // ── Configure action registration order when Memory throws ───────────────

    /// <summary>
    /// When Memory triggers the guard, the exception is thrown AFTER the configure
    /// action is registered (so options configuration is preserved in the container even
    /// though AddWtmWorkFlow throws).  The configure action itself is lazily invoked
    /// only when IOptions&lt;WorkFlowOptions&gt; is first resolved — not at registration time —
    /// so we verify the action is registered (an IConfigureOptions service is present)
    /// rather than checking a synchronous callback flag.
    /// </summary>
    [TestMethod]
    public void AddWtmWorkFlow_Memory_ThrowsAfterOptionsAreRegistered()
    {
        var services = BuildServicesWithDbType(DBTypeEnum.Memory);

        // The guard throws, but the options configure action should have been
        // added to the service collection before the check runs.
        Assert.ThrowsException<InvalidOperationException>(() =>
            services.AddWtmWorkFlow(o => o.InitiatorAutoApprove = true));

        // Verify that the IConfigureOptions<WorkFlowOptions> descriptor was added
        // (options registration happened before the guard check).
        bool hasConfigureOptions = services.Any(sd =>
            sd.ServiceType.IsGenericType &&
            sd.ServiceType.GetGenericTypeDefinition() == typeof(Microsoft.Extensions.Options.IConfigureOptions<>) &&
            sd.ServiceType.GenericTypeArguments.Length == 1 &&
            sd.ServiceType.GenericTypeArguments[0] == typeof(WorkFlowOptions));

        Assert.IsTrue(hasConfigureOptions,
            "WorkFlowOptions configure descriptor must be registered before the Memory guard throws.");
    }

    // ── ValidateDbTypeOnFirstUse path ─────────────────────────────────────────

    /// <summary>
    /// ValidateDbType() helper throws for Memory (used by the lazy guard path in WF-6).
    /// </summary>
    [TestMethod]
    public void ValidateDbType_Throws_WhenDbTypeIsMemory()
    {
        var mockDc = new Mock<IDataContext>();
        mockDc.SetupProperty(x => x.DBType, DBTypeEnum.Memory);

        var ex = Assert.ThrowsException<InvalidOperationException>(
            () => ServiceCollectionExtensions.ValidateDbType(mockDc.Object));

        StringAssert.Contains(ex.Message, "Memory");
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
}
