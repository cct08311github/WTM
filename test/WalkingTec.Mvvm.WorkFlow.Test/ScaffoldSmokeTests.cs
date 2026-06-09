#nullable enable
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.WorkFlow;

namespace WalkingTec.Mvvm.WorkFlow.Test;

/// <summary>
/// WF-1 scaffold smoke tests: verify that the DI registration and ModelBuilder
/// extension compile and function correctly before any engine logic is added.
/// </summary>
[TestClass]
public class ScaffoldSmokeTests
{
    /// <summary>
    /// AddWtmWorkFlow must register WorkFlowOptions with InitiatorAutoApprove == false
    /// (the safe, opt-in default per spec §9).
    /// </summary>
    [TestMethod]
    public void AddWtmWorkFlow_Registers_WorkFlowOptions_WithDefaultInitiatorAutoApprove_False()
    {
        var services = new ServiceCollection();
        services.AddWtmWorkFlow();
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<WorkFlowOptions>>().Value;

        Assert.IsNotNull(options, "WorkFlowOptions should be resolvable after AddWtmWorkFlow.");
        Assert.IsFalse(
            options.InitiatorAutoApprove,
            "InitiatorAutoApprove must default to false (opt-in only, spec §9).");
    }

    /// <summary>
    /// AddWtmWorkFlow with an explicit configure action must honour the supplied value.
    /// </summary>
    [TestMethod]
    public void AddWtmWorkFlow_Configure_Action_Overrides_Default()
    {
        var services = new ServiceCollection();
        services.AddWtmWorkFlow(o => o.InitiatorAutoApprove = true);
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<WorkFlowOptions>>().Value;

        Assert.IsTrue(
            options.InitiatorAutoApprove,
            "InitiatorAutoApprove should reflect the configure action override.");
    }

    /// <summary>
    /// ApplyWorkFlowModels must be callable on a ModelBuilder and must return
    /// the same builder instance (fluent chain contract).
    /// Real entity registrations will be added in WF-2.
    /// </summary>
    [TestMethod]
    public void ApplyWorkFlowModels_IsCallable_And_ReturnsBuilder()
    {
        // Use an in-memory DbContext solely to obtain a ModelBuilder.
        var optionsBuilder = new DbContextOptionsBuilder<TestOnlyDbContext>()
            .UseInMemoryDatabase("WF1_Smoke_" + Guid.NewGuid());
        using var ctx = new TestOnlyDbContext(optionsBuilder.Options);

        // Obtain a ModelBuilder via the protected OnModelCreating override.
        var builder = new ModelBuilder();
        var result = builder.ApplyWorkFlowModels();

        Assert.IsNotNull(result, "ApplyWorkFlowModels must return a non-null ModelBuilder.");
        Assert.AreSame(builder, result, "ApplyWorkFlowModels must return the same builder instance (fluent chain).");
    }

    // Minimal DbContext just to satisfy the API; not used for data access.
    private sealed class TestOnlyDbContext : DbContext
    {
        public TestOnlyDbContext(DbContextOptions<TestOnlyDbContext> options)
            : base(options) { }
    }
}
