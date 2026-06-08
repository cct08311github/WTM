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
            });
        }

        await _dc.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
        return await _dc.Set<EtlDeadLetterRow>()
            .Where(r => r.JobId == jobId)
            .OrderBy(r => r.QuarantinedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
