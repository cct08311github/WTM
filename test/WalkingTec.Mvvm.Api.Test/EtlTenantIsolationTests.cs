#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Scheduling;

namespace WalkingTec.Mvvm.Api.Test;

/// <summary>
/// Issues #841/#862 — "ITenant coverage": <c>DataContext.cs</c>'s global query filter
/// (<c>src/WalkingTec.Mvvm.Core/DataContext.cs:243-246</c>) only generates a
/// <c>TenantCode</c> predicate for entity types implementing <see cref="WalkingTec.Mvvm.Core.ITenant"/>.
/// Several ETL entities carry tenant-scoped data without implementing it, so no amount of
/// endpoint-level authorization makes them tenant-safe.
///
/// <para>
/// <b>Deeper than the issue text describes (#862 root cause):</b> <see cref="EtlJobDefinition"/>
/// already implemented <see cref="WalkingTec.Mvvm.Core.ITenant"/> before this PR (ETL-006), but
/// its filter was NEVER actually applied through a real <c>FrameworkContext</c>-derived app —
/// <c>ApplyEtlModels()</c> registers ETL entity types into the EF model AFTER
/// <c>base.OnModelCreating()</c> (which applies the <c>ITenant</c> filter loop) has already
/// finished running, so the loop never sees them. The <c>EtlJobDefinition_*</c> tests below
/// prove the fix: before it, the generated SQL for a cross-tenant id lookup carried NO
/// <c>TenantCode</c> predicate at all (confirmed empirically, not assumed).
/// </para>
///
/// <para>
/// Each entity gets a matching pair of tests — a negative control (tenant A cannot read tenant
/// B's row; this is the actual security property) and a positive control (tenant A can still
/// read its own row) — kept as SEPARATE test methods rather than combined assertions, so each
/// can be wired to its own mutant as a clean red/green pair (test/mutants/entries/).
/// </para>
///
/// <para>
/// Uses the <c>MultiTenantSeedFixtureTests</c>/<c>DbTestHelpers</c> pattern: EnableTenant is set
/// via <c>ConfigureAppConfiguration</c> + <c>AddInMemoryCollection</c> (never
/// <c>services.Configure&lt;Configs&gt;</c> — see that class's doc comment for why), and no
/// class-level factory is shared across tests (a shared factory was found to race demo.db's
/// schema sync — see <c>DbTestHelpers</c>'s "Isolation" doc comment).
/// </para>
/// </summary>
[TestClass]
public class EtlTenantIsolationTests
{
    private static WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> NewTenantEnabledFactory(
        DemoWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["EnableTenant"] = "true",
                });
            });
        });

    private static EtlJobDefinition NewJob(string name, string? tenantCode) => new()
    {
        ID = Guid.NewGuid(),
        Name = name,
        CronExpression = "0 0 2 * * ?",
        JobClassName = "Diag",
        SourceCsKey = "default",
        TargetCsKey = "default",
        TargetTableName = "DiagTarget",
        QueryTemplate = "SELECT 1",
        TenantCode = tenantCode,
    };

    private sealed record JobPair(EtlJobDefinition JobA, EtlJobDefinition JobB, string TenantA, string TenantB);

    private static JobPair SeedJobPair(WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> tenantFactory)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenantA = $"etlA{suffix}";
        var tenantB = $"etlB{suffix}";
        var jobA = DbTestHelpers.Seed(tenantFactory, NewJob($"job-A-{suffix}", tenantA));
        var jobB = DbTestHelpers.Seed(tenantFactory, NewJob($"job-B-{suffix}", tenantB));
        return new JobPair(jobA, jobB, tenantA, tenantB);
    }

    // ─── EtlJobDefinition — #862 root-cause wiring fix ─────────────────────────────────────

    /// <summary>
    /// Negative control / the actual fix. <b>Mutant target</b>: deleting the
    /// <c>ApplyEtlTenantFilter&lt;EtlJobDefinition&gt;(builder, context);</c> line in
    /// <c>EtlDbContextExtensions.ApplyEtlModels(ModelBuilder, EmptyContext)</c>
    /// (<c>src/WalkingTec.Mvvm.Etl/ServiceCollectionExtensions.cs</c>) turns this assertion red.
    /// Before that fix, this returned non-null — the generated SQL carried no TenantCode
    /// predicate whatsoever.
    /// </summary>
    [TestMethod]
    public void EtlJobDefinition_TenantA_CannotReadTenantB_Job()
    {
        using var factory = new DemoWebApplicationFactory();
        using var tenantFactory = NewTenantEnabledFactory(factory);

        var pair = SeedJobPair(tenantFactory);

        var (scopeA, dcA) = DbTestHelpers.OpenScopedContext(tenantFactory, pair.TenantA);
        using (scopeA)
        {
            var crossJob = dcA.Set<EtlJobDefinition>().FirstOrDefault(x => x.ID == pair.JobB.ID);
            Assert.IsNull(crossJob,
                "#862: a context scoped to tenant A must NOT resolve tenant B's " +
                "EtlJobDefinition by GUID. If this fails, ApplyEtlModels(ModelBuilder, " +
                "EmptyContext) is no longer applying the ITenant query filter for " +
                "EtlJobDefinition.");
        }
    }

    /// <summary>Positive control paired with the test above — own-tenant reads must still work.</summary>
    [TestMethod]
    public void EtlJobDefinition_TenantA_CanReadOwnJob()
    {
        using var factory = new DemoWebApplicationFactory();
        using var tenantFactory = NewTenantEnabledFactory(factory);

        var pair = SeedJobPair(tenantFactory);

        var (scopeA, dcA) = DbTestHelpers.OpenScopedContext(tenantFactory, pair.TenantA);
        using (scopeA)
        {
            var ownJob = dcA.Set<EtlJobDefinition>().FirstOrDefault(x => x.ID == pair.JobA.ID);
            Assert.IsNotNull(ownJob,
                "#862: a context scoped to tenant A must see tenant A's own EtlJobDefinition.");
        }
    }

    // ─── EtlDeadLetterRow — #862: TenantCode already populated, interface was missing ──────

    private static EtlDeadLetterRow NewDeadLetterRow(string? tenantCode) => new()
    {
        ID = Guid.NewGuid(),
        JobId = Guid.NewGuid(),
        RunId = Guid.NewGuid(),
        RowJson = "{}",
        Reason = "diagnostic",
        TenantCode = tenantCode,
    };

    private sealed record DeadLetterPair(EtlDeadLetterRow RowA, EtlDeadLetterRow RowB, string TenantA, string TenantB);

    private static DeadLetterPair SeedDeadLetterPair(WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> tenantFactory)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenantA = $"dlrA{suffix}";
        var tenantB = $"dlrB{suffix}";
        var rowA = DbTestHelpers.Seed(tenantFactory, NewDeadLetterRow(tenantA));
        var rowB = DbTestHelpers.Seed(tenantFactory, NewDeadLetterRow(tenantB));
        return new DeadLetterPair(rowA, rowB, tenantA, tenantB);
    }

    /// <summary>
    /// Negative control / the actual fix. Before this PR, <see cref="EtlDeadLetterRow"/> had a
    /// populated <c>TenantCode</c> column but did not implement <see cref="WalkingTec.Mvvm.Core.ITenant"/>
    /// at all -- the value was written and never used for filtering.
    /// <b>Mutant target</b>: deleting the
    /// <c>ApplyEtlTenantFilter&lt;EtlDeadLetterRow&gt;(builder, context);</c> line in
    /// <c>EtlDbContextExtensions.ApplyEtlModels(ModelBuilder, EmptyContext)</c>
    /// (<c>src/WalkingTec.Mvvm.Etl/ServiceCollectionExtensions.cs</c>) turns this assertion red
    /// (a patch reverting the <c>: BasePoco, ITenant</c> declaration itself would not compile,
    /// since <c>ApplyEtlTenantFilter&lt;T&gt;</c>'s <c>where T : ITenant</c> constraint would
    /// then fail at the call site in the same file -- reported as an invalid mutant, not a kill).
    /// </summary>
    [TestMethod]
    public void EtlDeadLetterRow_TenantA_CannotReadTenantB_Row()
    {
        using var factory = new DemoWebApplicationFactory();
        using var tenantFactory = NewTenantEnabledFactory(factory);

        var pair = SeedDeadLetterPair(tenantFactory);

        var (scopeA, dcA) = DbTestHelpers.OpenScopedContext(tenantFactory, pair.TenantA);
        using (scopeA)
        {
            var crossRow = dcA.Set<EtlDeadLetterRow>().FirstOrDefault(x => x.ID == pair.RowB.ID);
            Assert.IsNull(crossRow,
                "#862: a context scoped to tenant A must NOT resolve tenant B's " +
                "EtlDeadLetterRow by GUID -- RowJson holds the actual source rows that " +
                "failed, so this is a real cross-tenant data-content read if it succeeds.");
        }
    }

    /// <summary>Positive control paired with the test above — own-tenant reads must still work.</summary>
    [TestMethod]
    public void EtlDeadLetterRow_TenantA_CanReadOwnRow()
    {
        using var factory = new DemoWebApplicationFactory();
        using var tenantFactory = NewTenantEnabledFactory(factory);

        var pair = SeedDeadLetterPair(tenantFactory);

        var (scopeA, dcA) = DbTestHelpers.OpenScopedContext(tenantFactory, pair.TenantA);
        using (scopeA)
        {
            var ownRow = dcA.Set<EtlDeadLetterRow>().FirstOrDefault(x => x.ID == pair.RowA.ID);
            Assert.IsNotNull(ownRow,
                "#862: a context scoped to tenant A must see tenant A's own EtlDeadLetterRow.");
        }
    }

    // ─── EtlLineageRecord — #862: new TenantCode column, needs migration + backfill ────────

    private static EtlLineageRecord NewLineageRecord(string? tenantCode) => new()
    {
        ID = Guid.NewGuid(),
        JobId = Guid.NewGuid(),
        RunId = Guid.NewGuid(),
        SourceKind = "Diag",
        TargetTable = "DiagTarget",
        TenantCode = tenantCode,
    };

    private sealed record LineagePair(EtlLineageRecord RecordA, EtlLineageRecord RecordB, string TenantA, string TenantB);

    private static LineagePair SeedLineagePair(WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> tenantFactory)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenantA = $"linA{suffix}";
        var tenantB = $"linB{suffix}";
        var recordA = DbTestHelpers.Seed(tenantFactory, NewLineageRecord(tenantA));
        var recordB = DbTestHelpers.Seed(tenantFactory, NewLineageRecord(tenantB));
        return new LineagePair(recordA, recordB, tenantA, tenantB);
    }

    /// <summary>
    /// Negative control / the actual fix. Before this PR, <see cref="EtlLineageRecord"/> had no
    /// <c>TenantCode</c> column at all and did not implement <see cref="WalkingTec.Mvvm.Core.ITenant"/>.
    /// <b>Mutant target</b>: deleting the
    /// <c>ApplyEtlTenantFilter&lt;EtlLineageRecord&gt;(builder, context);</c> line in
    /// <c>EtlDbContextExtensions.ApplyEtlModels(ModelBuilder, EmptyContext)</c>
    /// (<c>src/WalkingTec.Mvvm.Etl/ServiceCollectionExtensions.cs</c>) turns this assertion red.
    /// </summary>
    [TestMethod]
    public void EtlLineageRecord_TenantA_CannotReadTenantB_Record()
    {
        using var factory = new DemoWebApplicationFactory();
        using var tenantFactory = NewTenantEnabledFactory(factory);

        var pair = SeedLineagePair(tenantFactory);

        var (scopeA, dcA) = DbTestHelpers.OpenScopedContext(tenantFactory, pair.TenantA);
        using (scopeA)
        {
            var crossRecord = dcA.Set<EtlLineageRecord>().FirstOrDefault(x => x.ID == pair.RecordB.ID);
            Assert.IsNull(crossRecord,
                "#862: a context scoped to tenant A must NOT resolve tenant B's " +
                "EtlLineageRecord by GUID.");
        }
    }

    /// <summary>Positive control paired with the test above — own-tenant reads must still work.</summary>
    [TestMethod]
    public void EtlLineageRecord_TenantA_CanReadOwnRecord()
    {
        using var factory = new DemoWebApplicationFactory();
        using var tenantFactory = NewTenantEnabledFactory(factory);

        var pair = SeedLineagePair(tenantFactory);

        var (scopeA, dcA) = DbTestHelpers.OpenScopedContext(tenantFactory, pair.TenantA);
        using (scopeA)
        {
            var ownRecord = dcA.Set<EtlLineageRecord>().FirstOrDefault(x => x.ID == pair.RecordA.ID);
            Assert.IsNotNull(ownRecord,
                "#862: a context scoped to tenant A must see tenant A's own EtlLineageRecord.");
        }
    }

    // ─── #862 regression: background scheduler must still see EVERY tenant's jobs ─────────
    //
    // Enabling the ITenant filter for EtlJobDefinition has a real side effect the tests above
    // do not cover: EtlSchedulerService/EtlQuartzJob/DbEtlGovernanceStore resolve their OWN
    // WTMContext from a fresh DI scope with no HTTP identity, so its TenantCode is always
    // null. Confirmed empirically while building this fix: before adding IgnoreQueryFilters()
    // throughout EtlSchedulerService.cs/EtlQuartzJob.cs/DbEtlGovernanceStore.cs, a
    // tenant-scoped job became permanently invisible to the scheduler the moment the
    // EtlJobDefinition ITenant filter started being enforced -- the scheduler could no longer
    // find it to execute it, reset it after a crash, or prune its old records. This test locks
    // in that the fix works: a stuck-Running job belonging to a REAL (non-null) tenant is still
    // found and reset by the background ghost-job sweep.

    /// <summary>
    /// <b>Mutant target</b>: removing <c>.IgnoreQueryFilters()</c> from the
    /// <c>ghostJobs</c> query in <c>EtlSchedulerService.ResetGhostRunningJobsAsync</c>
    /// (<c>src/WalkingTec.Mvvm.Etl/Scheduling/EtlSchedulerService.cs</c>) turns this
    /// assertion red -- the tenant-scoped job would no longer be found at all, so its
    /// Status would stay Running instead of being reset to Failed.
    /// </summary>
    [TestMethod]
    public async Task EtlScheduler_ResetGhostRunningJobs_FindsTenantScopedJob_DespiteNoAmbientTenantIdentity()
    {
        using var factory = new DemoWebApplicationFactory();
        using var tenantFactory = NewTenantEnabledFactory(factory);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenantCode = $"schedT{suffix}";
        var job = DbTestHelpers.Seed(tenantFactory, new EtlJobDefinition
        {
            ID = Guid.NewGuid(),
            Name = $"ghost-job-{suffix}",
            CronExpression = "0 0 2 * * ?",
            JobClassName = "Diag",
            SourceCsKey = "default",
            TargetCsKey = "default",
            TargetTableName = "DiagTarget",
            QueryTemplate = "SELECT 1",
            TenantCode = tenantCode,
            // Simulates a job whose executing process crashed mid-run -- exactly the state
            // ResetGhostRunningJobsAsync exists to clean up on the next startup.
            Status = EtlJobStatus.Running,
        });

        var scheduler = tenantFactory.Services.GetRequiredService<EtlSchedulerService>();
        await scheduler.ResetGhostRunningJobsAsync();

        var updated = DbTestHelpers.ReadBack<EtlJobDefinition>(tenantFactory, job.ID, ignoreQueryFilters: true);
        Assert.IsNotNull(updated, "#862: the seeded job must still physically exist.");
        Assert.AreEqual(EtlJobStatus.Failed, updated!.Status,
            "#862: the background ghost-job-reset sweep must still find and reset a " +
            "tenant-scoped job stuck in Running, despite the sweep's own background context " +
            "having no ambient tenant identity (TenantCode == null there).");
    }

    // ─── EtlRunLog — #841's original subject: new TenantCode column, needs migration ───────

    private static EtlRunLog NewRunLog(Guid jobId, string? tenantCode) => new()
    {
        ID = Guid.NewGuid(),
        JobId = jobId,
        Trigger = EtlRunTrigger.Manual,
        Result = EtlRunResult.Success,
        StartedAt = DateTime.UtcNow,
        TenantCode = tenantCode,
    };

    private sealed record RunLogPair(EtlRunLog LogA, EtlRunLog LogB, string TenantA, string TenantB);

    private static RunLogPair SeedRunLogPair(WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> tenantFactory)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenantA = $"rlA{suffix}";
        var tenantB = $"rlB{suffix}";
        var jobA = DbTestHelpers.Seed(tenantFactory, NewJob($"rl-job-A-{suffix}", tenantA));
        var jobB = DbTestHelpers.Seed(tenantFactory, NewJob($"rl-job-B-{suffix}", tenantB));
        var logA = DbTestHelpers.Seed(tenantFactory, NewRunLog(jobA.ID, tenantA));
        var logB = DbTestHelpers.Seed(tenantFactory, NewRunLog(jobB.ID, tenantB));
        return new RunLogPair(logA, logB, tenantA, tenantB);
    }

    /// <summary>
    /// Negative control / the actual fix -- #841's original subject. Before this PR,
    /// <see cref="EtlRunLog"/> had no <c>TenantCode</c> column at all and did not implement
    /// <see cref="WalkingTec.Mvvm.Core.ITenant"/>. <b>Mutant target</b>: deleting the
    /// <c>ApplyEtlTenantFilter&lt;EtlRunLog&gt;(builder, context);</c> line in
    /// <c>EtlDbContextExtensions.ApplyEtlModels(ModelBuilder, EmptyContext)</c>
    /// (<c>src/WalkingTec.Mvvm.Etl/ServiceCollectionExtensions.cs</c>) turns this assertion red.
    /// </summary>
    [TestMethod]
    public void EtlRunLog_TenantA_CannotReadTenantB_Log()
    {
        using var factory = new DemoWebApplicationFactory();
        using var tenantFactory = NewTenantEnabledFactory(factory);

        var pair = SeedRunLogPair(tenantFactory);

        var (scopeA, dcA) = DbTestHelpers.OpenScopedContext(tenantFactory, pair.TenantA);
        using (scopeA)
        {
            var crossLog = dcA.Set<EtlRunLog>().FirstOrDefault(x => x.ID == pair.LogB.ID);
            Assert.IsNull(crossLog,
                "#841/#862: a context scoped to tenant A must NOT resolve tenant B's " +
                "EtlRunLog by GUID.");
        }
    }

    /// <summary>Positive control paired with the test above — own-tenant reads must still work.</summary>
    [TestMethod]
    public void EtlRunLog_TenantA_CanReadOwnLog()
    {
        using var factory = new DemoWebApplicationFactory();
        using var tenantFactory = NewTenantEnabledFactory(factory);

        var pair = SeedRunLogPair(tenantFactory);

        var (scopeA, dcA) = DbTestHelpers.OpenScopedContext(tenantFactory, pair.TenantA);
        using (scopeA)
        {
            var ownLog = dcA.Set<EtlRunLog>().FirstOrDefault(x => x.ID == pair.LogA.ID);
            Assert.IsNotNull(ownLog,
                "#841/#862: a context scoped to tenant A must see tenant A's own EtlRunLog.");
        }
    }

    /// <summary>
    /// #862 regression, mirroring <see cref="EtlScheduler_ResetGhostRunningJobs_FindsTenantScopedJob_DespiteNoAmbientTenantIdentity"/>
    /// for the write path: <c>EtlSchedulerService.RerunFromSnapshotAsync</c> must still find a
    /// tenant-scoped <see cref="EtlRunLog"/> by id from its own background context (TenantCode
    /// always null there). <b>Mutant target</b>: removing <c>.IgnoreQueryFilters()</c> from the
    /// <c>runLog</c> lookup in <c>RerunFromSnapshotAsync</c>
    /// (<c>src/WalkingTec.Mvvm.Etl/Scheduling/EtlSchedulerService.cs</c>) turns this assertion
    /// red -- the tenant-scoped run log would no longer be found, so <c>RerunFromSnapshotAsync</c>
    /// would silently no-op (its <c>if (runLog == null) return;</c> guard) instead of updating
    /// the job's watermark.
    /// </summary>
    [TestMethod]
    public async Task EtlScheduler_RerunFromSnapshot_FindsTenantScopedRunLog_DespiteNoAmbientTenantIdentity()
    {
        using var factory = new DemoWebApplicationFactory();
        using var tenantFactory = NewTenantEnabledFactory(factory);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenantCode = $"rerunT{suffix}";
        var job = DbTestHelpers.Seed(tenantFactory, NewJob($"rerun-job-{suffix}", tenantCode));
        var snapshotWatermark = $"WM-{suffix}";
        var runLog = DbTestHelpers.Seed(tenantFactory, new EtlRunLog
        {
            ID = Guid.NewGuid(),
            JobId = job.ID,
            Trigger = EtlRunTrigger.Manual,
            Result = EtlRunResult.Success,
            StartedAt = DateTime.UtcNow,
            WatermarkSnapshot = snapshotWatermark,
            TenantCode = tenantCode,
        });

        var scheduler = tenantFactory.Services.GetRequiredService<EtlSchedulerService>();
        // TriggerNowAsync inside RerunFromSnapshotAsync requires a live Quartz scheduler
        // (EnsureScheduler()), which this lightweight test does not wire up -- that call
        // throws AFTER the part under test (finding the run log/job and writing the
        // watermark back) has already completed, so it is expected and ignored here.
        try
        {
            await scheduler.RerunFromSnapshotAsync(runLog.ID);
        }
        catch (InvalidOperationException)
        {
            // Expected: "ETL Scheduler not initialized" from EnsureScheduler() inside the
            // TriggerNowAsync call RerunFromSnapshotAsync makes at its very end.
        }

        var updated = DbTestHelpers.ReadBack<EtlJobDefinition>(tenantFactory, job.ID, ignoreQueryFilters: true);
        Assert.IsNotNull(updated, "#862: the seeded job must still physically exist.");
        Assert.AreEqual(snapshotWatermark, updated!.LastWatermarkValue,
            "#862: RerunFromSnapshotAsync must still find a tenant-scoped EtlRunLog by id and " +
            "write its WatermarkSnapshot back onto the job, despite the scheduler's own " +
            "background context having no ambient tenant identity (TenantCode == null there).");
    }
}
