#nullable enable
// WF-7: Append-only WorkflowEventLog writer.
//
// Design:
//   • Every engine transition calls AppendAsync inside the same transaction as the
//     ExecuteUpdateAsync CAS — this IS the authoritative audit because ExecuteUpdateAsync
//     bypasses the EF change-tracker and [AuditChanges] does not see those writes (spec §8.4).
//   • Monotonic Seq per instance: computed as MAX(Seq)+1 within an explicit serializable
//     transaction that wraps the MAX read and the INSERT atomically.  This prevents two
//     concurrent transitions on the same instance from reading the same MAX before either
//     has inserted — which would produce duplicate Seq values and violate the UNIQUE index
//     on (TenantCode, InstanceId, Seq) added in #240 (#269 fix).
//   • If the DbContext already has an active ambient transaction (e.g. the caller wrapped
//     the CAS + AppendAsync together), we participate in it and do NOT start a new one,
//     so the serialisation guarantee is inherited from the outer transaction.
//   • Seq starts at 1 (no events before Submit → Seq 1 = Submit).
//   • OccurredUtc is always UTC, sourced from DateTime.UtcNow (WF-6 uses UtcNow directly;
//     proper Wtm.TimeProvider injection is WF-14 when the engine gets IWtm* services).

using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

/// <summary>
/// Appends immutable <see cref="WorkflowEventLog"/> rows with a monotonic
/// per-instance <see cref="WorkflowEventLog.Seq"/>.
///
/// <para>This is the authoritative engine audit trail.  Because engine transitions
/// use <c>ExecuteUpdateAsync</c> they bypass the EF change-tracker, so
/// <c>[AuditChanges]</c> does NOT capture them (spec §8.4).  Every transition MUST
/// call <see cref="AppendAsync"/> inside the same transaction.</para>
///
/// <para><strong>Monotonic Seq — race-safe (#269):</strong>
/// The MAX(Seq)+1 read and the INSERT are wrapped in an explicit serializable
/// transaction (or participate in an existing ambient transaction).  Under
/// serializable isolation two concurrent callers cannot both read the same MAX
/// before either has inserted — the DB serializes them, so each caller sees the
/// other's newly inserted row and computes a distinct next value.
/// Starting value is 1 (no log rows → next = 1).</para>
/// </summary>
public static class WorkflowEventLogWriter
{
    /// <summary>
    /// Append an immutable event row for the given instance.
    ///
    /// Safe under concurrent transitions on the same instance: the MAX(Seq)+1
    /// computation and the INSERT are wrapped in a serializable transaction to
    /// prevent duplicate Seq values (#269).
    /// </summary>
    /// <param name="db">The DbContext owning the current (or ambient) transaction.</param>
    /// <param name="instanceId">The FK to <see cref="ProcessInstance"/>.</param>
    /// <param name="tenantCode">Tenant isolation code (copied from the instance).</param>
    /// <param name="action">The action being recorded.</param>
    /// <param name="nodeKey">The node this event relates to (may be null for instance-level events).</param>
    /// <param name="actorITCode">The user who triggered this event (null for system/auto actions).</param>
    /// <param name="beforeState">State before the transition (human-readable enum name).</param>
    /// <param name="afterState">State after the transition (human-readable enum name).</param>
    /// <param name="reason">Optional comment or rejection reason.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task AppendAsync(
        DbContext db,
        Guid instanceId,
        string? tenantCode,
        EventAction action,
        string? nodeKey,
        string? actorITCode,
        string? beforeState,
        string? afterState,
        string? reason = null,
        CancellationToken ct = default)
    {
        // Use an ambient transaction if one is already open (caller wrapped both CAS + AppendAsync
        // together), otherwise begin a new serializable transaction so that the MAX read and the
        // INSERT are atomic — preventing duplicate Seq when two concurrent transitions race on the
        // same instance (#269 / spec §8.4 race-safe audit requirement).
        bool ownsTransaction = db.Database.CurrentTransaction is null;
        IDbContextTransaction? tx = ownsTransaction
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;

        try
        {
            // Compute the next monotonic Seq within the serialized transaction window.
            // MAX returns null when there are no rows (first event); default to 0 so next = 1.
            var maxSeq = await db.Set<WorkflowEventLog>()
                .Where(e => e.InstanceId == instanceId)
                .MaxAsync(e => (int?)e.Seq, ct)
                ?? 0;

            var logEntry = new WorkflowEventLog
            {
                ID = Guid.NewGuid(),
                TenantCode = tenantCode,
                InstanceId = instanceId,
                Seq = maxSeq + 1,
                ActorITCode = actorITCode,
                Action = action,
                NodeKey = nodeKey,
                BeforeState = beforeState,
                AfterState = afterState,
                Reason = reason,
                OccurredUtc = DateTime.UtcNow,
            };

            db.Set<WorkflowEventLog>().Add(logEntry);
            await db.SaveChangesAsync(ct);

            if (ownsTransaction)
                await tx!.CommitAsync(ct);
        }
        catch when (ownsTransaction && tx is not null)
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (ownsTransaction)
                tx?.Dispose();
        }
    }
}
