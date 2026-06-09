#nullable enable
// WF-7: Append-only WorkflowEventLog writer.
//
// Design:
//   • Every engine transition calls AppendAsync inside the same transaction as the
//     ExecuteUpdateAsync CAS — this IS the authoritative audit because ExecuteUpdateAsync
//     bypasses the EF change-tracker and [AuditChanges] does not see those writes (spec §8.4).
//   • Monotonic Seq per instance: computed as MAX(Seq)+1 within the same transaction.
//     Using MAX ensures monotonicity even after concurrent appends (two concurrent appends
//     in the same millisecond each read the pre-insert MAX and then insert; the UNIQUE index
//     on (InstanceId, Seq) catches duplicates if they collide — in practice a single
//     DbContext transaction serializes appends within one AdvanceAsync call).
//   • Seq starts at 1 (no events before Submit → Seq 1 = Submit).
//   • OccurredUtc is always UTC, sourced from DateTime.UtcNow (WF-6 uses UtcNow directly;
//     proper Wtm.TimeProvider injection is WF-14 when the engine gets IWtm* services).

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
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
/// <para><strong>Monotonic Seq:</strong>
/// Computed as <c>MAX(Seq) + 1</c> over all existing events for the same instance.
/// Starting value is 1 (no log rows → next = 1).  Within a single
/// <c>DbContext</c> transaction the appends are serialized, so Seq is guaranteed
/// to be unique and monotonically increasing per instance.</para>
/// </summary>
public static class WorkflowEventLogWriter
{
    /// <summary>
    /// Append an immutable event row for the given instance.
    ///
    /// Must be called within the same transaction as the corresponding
    /// <c>GuardedTransition</c> CAS to guarantee the audit is atomic with the
    /// state change.
    /// </summary>
    /// <param name="db">The DbContext owning the current transaction.</param>
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
        // Compute the next monotonic Seq in the same transaction context.
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
    }
}
