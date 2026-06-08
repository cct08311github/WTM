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
}

/// <summary>
/// One quarantined row entry (in-memory transfer object from the executor to the store).
/// </summary>
public sealed record EtlDeadLetterEntry(
    string RowJson,
    string Reason,
    string Source);
