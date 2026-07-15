#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;

namespace WalkingTec.Mvvm.Etl.Governance;

/// <summary>
/// ETL-004/005 governance persistence contract.
/// Implementations write dead-letter rows and lineage records.
/// A no-op implementation is provided for pipelines that run outside a full DI host.
/// </summary>
public interface IEtlGovernanceStore
{
    /// <summary>
    /// Persists a batch of dead-letter rows.
    /// Called per-batch during the quality-rule Drop path when
    /// <see cref="EtlPipelineConfig.EnableDeadLetter"/> is true.
    /// </summary>
    Task AddDeadLetterRowsAsync(
        Guid jobId,
        Guid runId,
        IEnumerable<EtlDeadLetterEntry> entries,
        string? tenantCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a lineage record for a completed run.
    /// Called once on success when <see cref="EtlPipelineConfig.EnableLineage"/> is true.
    /// </summary>
    Task AddLineageRecordAsync(
        EtlLineageRecord record,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all quarantined rows for the specified job, ordered by quarantine time.
    /// Useful for operator inspection and replay tooling.
    /// </summary>
    Task<IReadOnlyList<EtlDeadLetterRow>> QueryDeadLetterAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// #673(d) — run-scoped dedupe: marks the dead-letter rows written for
    /// <paramref name="jobId"/>/<paramref name="runId"/> as <see cref="EtlDeadLetterRow.RunSucceeded"/>
    /// = true. Called by <see cref="Pipeline.EtlPipelineExecutor"/> once, right after a
    /// successful run flushes its buffered dead-letter entries — see
    /// <see cref="EtlDeadLetterRow.RunSucceeded"/> for the full state machine.
    /// <para>
    /// Default implementation is a no-op so existing implementers of this interface
    /// keep compiling and behaving exactly as before (source- and binary-compatible via
    /// C# default interface members) — override only if your store persists
    /// <see cref="EtlDeadLetterRow.RunSucceeded"/> and wants run-scoped dedupe.
    /// </para>
    /// </summary>
    Task MarkDeadLetterRunSucceededAsync(
        Guid jobId, Guid runId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// #673(d) — run-scoped dedupe: deletes dead-letter rows for <paramref name="jobId"/>
    /// whose owning run did NOT complete successfully
    /// (<see cref="EtlDeadLetterRow.RunSucceeded"/> == <c>false</c>). Called by
    /// <see cref="Pipeline.EtlPipelineExecutor"/> once, at the start of every run when
    /// dead-letter capture is enabled — a retry re-extracts the same (watermark
    /// unchanged) window and produces its own up-to-date diagnostics, so the prior
    /// failed attempt's rows are stale and would otherwise duplicate on every retry.
    /// <para>
    /// Rows with <see cref="EtlDeadLetterRow.RunSucceeded"/> == <c>null</c> (legacy rows
    /// written before this column existed) or == <c>true</c> (a successful run's
    /// permanent Drop-path record) are never touched.
    /// </para>
    /// <para>
    /// Default implementation is a no-op — see <see cref="MarkDeadLetterRunSucceededAsync"/>.
    /// </para>
    /// </summary>
    Task ClearDeadLetterFromFailedRunsAsync(
        Guid jobId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>
/// One quarantined row entry (in-memory transfer object from the executor to the store).
/// </summary>
public sealed record EtlDeadLetterEntry(
    string RowJson,
    string Reason,
    string Source);
