#nullable enable
// ScaffoldSmokeTests.cs — WF-1 scaffold smoke tests.
// PR #240: removed unused EF InMemory DbContext scaffolding (violates spec invariant #6
// / repo rule #119/#162 — EF InMemory cannot translate ExecuteUpdateAsync and must not
// be used in reference tests).  ApplyWorkFlowModels test now uses a bare ModelBuilder
// without any DbContext, which is the correct contract for a pure builder extension.
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
    /// ApplyWorkFlowModels must be callable on a bare ModelBuilder and must return
    /// the same builder instance (fluent chain contract).
    ///
    /// Note: the previous test used a <c>UseInMemoryDatabase</c> DbContext here — that was
    /// dead scaffolding (the context was created but never used) and violated spec invariant
    /// #6 / repo rule #119/#162 by normalizing EF InMemory in a reference test.  Removed
    /// in PR #240; a bare ModelBuilder is sufficient and correct for this contract test.
    /// </summary>
    [TestMethod]
    public void ApplyWorkFlowModels_IsCallable_And_ReturnsBuilder()
    {
        var builder = new ModelBuilder();
        var result = builder.ApplyWorkFlowModels();

        Assert.IsNotNull(result, "ApplyWorkFlowModels must return a non-null ModelBuilder.");
        Assert.AreSame(builder, result, "ApplyWorkFlowModels must return the same builder instance (fluent chain).");
    }
}
