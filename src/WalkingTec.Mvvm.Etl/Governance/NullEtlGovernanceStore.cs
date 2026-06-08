#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Etl.Models;

namespace WalkingTec.Mvvm.Etl.Governance;

/// <summary>
/// No-op implementation of <see cref="IEtlGovernanceStore"/> — used when
/// dead-letter / lineage is not configured or no store has been registered.
/// </summary>
public sealed class NullEtlGovernanceStore : IEtlGovernanceStore
{
    public static readonly NullEtlGovernanceStore Instance = new();

    private NullEtlGovernanceStore() { }

    public Task AddDeadLetterRowsAsync(
        Guid jobId, Guid runId,
        IEnumerable<EtlDeadLetterEntry> entries,
        string? tenantCode,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task AddLineageRecordAsync(
        EtlLineageRecord record,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<EtlDeadLetterRow>> QueryDeadLetterAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<EtlDeadLetterRow>>(Array.Empty<EtlDeadLetterRow>());
}
