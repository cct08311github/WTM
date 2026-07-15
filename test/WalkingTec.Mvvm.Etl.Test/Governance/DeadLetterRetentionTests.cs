#nullable enable
// #673(e): dead-letter retention pruning tests.
// Uses SQLite shared in-memory so ExecuteDeleteAsync works
// (EF InMemory provider does not support bulk-delete operations).
using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Scheduling;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Etl.Test.Governance;

/// <summary>
/// #673(e): Retention-pruning logic for <see cref="EtlDeadLetterRow"/>, mirroring
/// <c>EtlRunLogRetentionTests</c>. <see cref="EtlSchedulerService.PruneDeadLetterAsync"/> must:
///   - delete only rows older than <see cref="EtlOptions.DeadLetterRetentionDays"/>
///   - keep rows within the window untouched
///   - be a no-op when DeadLetterRetentionDays == 0 (default — never silently deletes data)
/// </summary>
[TestClass]
public class DeadLetterRetentionTests : IDisposable
{
    private GovernanceTestDataContext _dc = null!;
    private IServiceProvider _sp = null!;
    private SqliteConnection _keepAlive = null!;

    [TestInitialize]
    public void Setup()
    {
        var dbName = $"DeadLetterRetention_{Guid.NewGuid():N}";
        _keepAlive = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
        _keepAlive.Open();
        _dc = new GovernanceTestDataContext(
            $"DataSource={dbName}?mode=memory&cache=shared", DBTypeEnum.SQLite);
        _dc.Database.EnsureCreated();

        var wtm = MockWtmContext.CreateWtmContext(_dc);
        var services = new ServiceCollection();
        services.AddSingleton(wtm);
        _sp = services.BuildServiceProvider();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dc?.Dispose();
        _keepAlive?.Close();
        _keepAlive?.Dispose();
    }

    public void Dispose() => Cleanup();

    // ─── helpers ────────────────────────────────────────────────────────────

    private EtlDeadLetterRow AddRow(Guid jobId, DateTime quarantinedAt)
    {
        var row = new EtlDeadLetterRow
        {
            JobId = jobId,
            RunId = Guid.NewGuid(),
            RowJson = "{}",
            Reason = "test reason",
            QuarantinedAt = quarantinedAt,
            Source = EtlDeadLetterSource.QualityRule,
        };
        _dc.Set<EtlDeadLetterRow>().Add(row);
        _dc.SaveChanges();
        return row;
    }

    private IServiceProvider BuildSpWithRetention(int retentionDays)
    {
        var wtm = MockWtmContext.CreateWtmContext(_dc);
        var services = new ServiceCollection();
        services.AddSingleton(wtm);
        services.Configure<EtlOptions>(o => o.DeadLetterRetentionDays = retentionDays);
        return services.BuildServiceProvider();
    }

    // ─── tests ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task PruneDeadLetterAsync_RetentionDays_zero_keeps_all_rows()
    {
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        AddRow(jobId, now.AddDays(-100));
        AddRow(jobId, now.AddDays(-500));

        // Default: RetentionDays = 0 (keep forever — never silently deletes data)
        var sp = BuildSpWithRetention(0);
        var svc = new EtlSchedulerService(sp);
        await svc.PruneDeadLetterAsync();

        _dc.Set<EtlDeadLetterRow>().Count().Should()
            .Be(2, "DeadLetterRetentionDays=0 is no-op; all rows must survive");
    }

    [TestMethod]
    public async Task PruneDeadLetterAsync_deletes_rows_older_than_retention_window()
    {
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        AddRow(jobId, now.AddDays(-91));
        AddRow(jobId, now.AddDays(-180));

        var sp = BuildSpWithRetention(90);
        var svc = new EtlSchedulerService(sp);
        await svc.PruneDeadLetterAsync();

        _dc.Set<EtlDeadLetterRow>().Count().Should()
            .Be(0, "both rows are older than 90 days and should be pruned");
    }

    [TestMethod]
    public async Task PruneDeadLetterAsync_keeps_rows_within_retention_window()
    {
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        AddRow(jobId, now.AddDays(-10));
        AddRow(jobId, now.AddDays(-50));

        var sp = BuildSpWithRetention(90);
        var svc = new EtlSchedulerService(sp);
        await svc.PruneDeadLetterAsync();

        _dc.Set<EtlDeadLetterRow>().Count().Should()
            .Be(2, "both rows are within 90 days and must not be pruned");
    }

    [TestMethod]
    public async Task PruneDeadLetterAsync_deletes_only_old_rows_keeps_recent()
    {
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var oldRow = AddRow(jobId, now.AddDays(-200));
        var recentRow = AddRow(jobId, now.AddDays(-30));

        var sp = BuildSpWithRetention(90);
        var svc = new EtlSchedulerService(sp);
        await svc.PruneDeadLetterAsync();

        var ids = _dc.Set<EtlDeadLetterRow>().Select(r => r.ID).ToList();
        ids.Should().NotContain(oldRow.ID, "row older than 90 days must be pruned");
        ids.Should().Contain(recentRow.ID, "row within 90 days must survive");
    }

    [TestMethod]
    public async Task PruneDeadLetterAsync_negative_retention_is_treated_as_no_op()
    {
        var jobId = Guid.NewGuid();
        AddRow(jobId, DateTime.UtcNow.AddDays(-500));

        // Negative retention = misconfiguration → treat as 0 (no-op)
        var sp = BuildSpWithRetention(-10);
        var svc = new EtlSchedulerService(sp);
        await svc.PruneDeadLetterAsync();

        _dc.Set<EtlDeadLetterRow>().Count().Should().Be(1);
    }
}
