#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;

namespace WalkingTec.Mvvm.Etl.Governance;

/// <summary>
/// EF Core–backed implementation of <see cref="IEtlGovernanceStore"/>.
/// Persists <see cref="EtlDeadLetterRow"/> and <see cref="EtlLineageRecord"/> to the
/// application DataContext.
/// </summary>
/// <remarks>
/// <b>Row-data sanitization</b>: before persistence each row's JSON is passed through
/// <see cref="EtlErrorSanitizer.SanitizeRaw"/> to redact connection-string credentials
/// that could appear in values originating from configuration columns.
/// </remarks>
/// <remarks>
/// #862: the only real construction site (<c>EtlQuartzJob.cs</c>) passes the Quartz-triggered
/// background <c>Wtm.DC</c>, which has no HTTP identity and therefore always resolves
/// <c>TenantCode == null</c> (see <c>EtlSchedulerService</c>'s class-level remarks for the
/// full rationale). <see cref="MarkDeadLetterRunSucceededAsync"/> and
/// <see cref="ClearDeadLetterFromFailedRunsAsync"/> key their updates by
/// <c>(JobId, RunId)</c>/<c>(JobId, RunSucceeded)</c>, not by an ambient tenant scope, so both
/// call <c>IgnoreQueryFilters()</c> explicitly -- without it, #862's <see cref="EtlDeadLetterRow"/>
/// ITenant fix would silently stop these updates from matching any tenant-scoped row.
/// </remarks>
public sealed class DbEtlGovernanceStore : IEtlGovernanceStore
{
    private readonly IDataContext _dc;

    public DbEtlGovernanceStore(IDataContext dc)
    {
        _dc = dc ?? throw new ArgumentNullException(nameof(dc));
    }

    /// <inheritdoc/>
    public async Task AddDeadLetterRowsAsync(
        Guid jobId,
        Guid runId,
        IEnumerable<EtlDeadLetterEntry> entries,
        string? tenantCode,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        foreach (var entry in entries)
        {
            // Sanitize row JSON before persistence to guard against credential leakage.
            var sanitizedJson = EtlErrorSanitizer.SanitizeRaw(entry.RowJson);
            var sanitizedReason = EtlErrorSanitizer.SanitizeRaw(entry.Reason);

            _dc.Set<EtlDeadLetterRow>().Add(new EtlDeadLetterRow
            {
                JobId          = jobId,
                RunId          = runId,
                RowJson        = sanitizedJson,
                Reason         = sanitizedReason.Length > 2000 ? sanitizedReason[..2000] : sanitizedReason,
                Source         = entry.Source,
                QuarantinedAt  = now,
                TenantCode     = tenantCode,
                // #673(d): starts "not yet confirmed successful" — MarkDeadLetterRunSucceededAsync
                // flips this to true after a successful run's flush. See
                // EtlDeadLetterRow.RunSucceeded for the full state machine.
                RunSucceeded   = false,
            });
        }

        await _dc.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task MarkDeadLetterRunSucceededAsync(
        Guid jobId, Guid runId, CancellationToken cancellationToken = default)
    {
        // #862: IgnoreQueryFilters() -- see class remarks.
        await _dc.Set<EtlDeadLetterRow>()
            .IgnoreQueryFilters()
            .Where(r => r.JobId == jobId && r.RunId == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RunSucceeded, true), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ClearDeadLetterFromFailedRunsAsync(
        Guid jobId, CancellationToken cancellationToken = default)
    {
        // RunSucceeded == false only — never null (legacy rows) or true (permanent
        // successful-run history). See EtlDeadLetterRow.RunSucceeded for the state
        // machine this enforces.
        // #862: IgnoreQueryFilters() -- see class remarks.
        await _dc.Set<EtlDeadLetterRow>()
            .IgnoreQueryFilters()
            .Where(r => r.JobId == jobId && r.RunSucceeded == false)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task AddLineageRecordAsync(
        EtlLineageRecord record,
        CancellationToken cancellationToken = default)
    {
        _dc.Set<EtlLineageRecord>().Add(record);
        await _dc.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EtlDeadLetterRow>> QueryDeadLetterAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        // #673(d): AsNoTracking is required for correctness, not just perf. This is a
        // read-only reporting query, and MarkDeadLetterRunSucceededAsync /
        // ClearDeadLetterFromFailedRunsAsync are set-based ExecuteUpdate/ExecuteDelete
        // calls that bypass the change tracker by design. A caller that reuses the same
        // IDataContext instance across a run (as EtlQuartzJob and EtlPipelineExecutor
        // do) would otherwise see the STALE pre-update tracked entity here — e.g.
        // RunSucceeded still false immediately after MarkDeadLetterRunSucceededAsync set
        // it true in the database — because a tracking query returns the already-tracked
        // identity-mapped instance instead of re-materializing from the query results.
        return await _dc.Set<EtlDeadLetterRow>()
            .AsNoTracking()
            .Where(r => r.JobId == jobId)
            .OrderBy(r => r.QuarantinedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
