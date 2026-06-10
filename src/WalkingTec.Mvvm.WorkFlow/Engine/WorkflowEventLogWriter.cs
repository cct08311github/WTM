#nullable enable
// WF-7: Append-only WorkflowEventLog writer.
//
// Wave-3 (WF-16) redesign — AllocateSeqAsync replaces MAX(Seq)+1 + Serializable:
//   • #269 used a Serializable transaction wrapping MAX(Seq)+1 + INSERT to prevent
//     duplicate Seq values under concurrent transitions.  That approach is NOT portable
//     across all 7 DBTypeEnum providers (Oracle / DaMeng have SERIALIZABLE quirks; the
//     no-SERIALIZABLE rule is stated in the Wave-3 design addendum §3 Race-B note).
//   • Wave-3 replaces it with ProcessInstance.NextSeq — a per-instance counter bumped
//     atomically via GuardedTransition.AllocateSeqAsync (single-row CAS, no isolation
//     dependency).  The counter guarantees Seq is unique without any transaction-level
//     serialization requirement.
//   • AppendAsync still participates in an ambient transaction when one is open (e.g.
//     the return-path engine wraps all STEP 0-6 writes in a single transaction).
//
// Design (post-Wave-3):
//   • AppendAsync allocates the next Seq via AllocateSeqAsync (retry loop, max 5 attempts).
//   • If the DbContext has an ambient transaction the INSERT commits with it; otherwise a
//     new default-isolation transaction wraps just the INSERT.
//   • OccurredUtc sourced from DateTime.UtcNow (proper clock injection deferred to WF-14).

using System;
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
/// <para><strong>Monotonic Seq — Wave-3 portable design (#276):</strong>
/// Seq is allocated from <see cref="ProcessInstance.NextSeq"/> via a CAS loop
/// in <see cref="GuardedTransition.AllocateSeqAsync"/>.  This replaces the
/// previous MAX(Seq)+1 + Serializable-transaction approach (#269) which is not
/// portable across all 7 DBTypeEnum providers (spec §3 Race-B addendum).</para>
/// </summary>
public static class WorkflowEventLogWriter
{
    // Maximum Seq-allocation retry attempts (each attempt is a cheap single-row CAS).
    // 5 retries handles practical contention; if still failing the caller gets an exception.
    private const int MaxSeqRetries = 5;

    /// <summary>
    /// Append an immutable event row for the given instance.
    ///
    /// <para>Seq is allocated atomically from the per-instance NextSeq counter via a
    /// portable CAS loop — no Serializable transaction required.</para>
    ///
    /// <para>If the <paramref name="db"/> already has an active ambient transaction the
    /// INSERT participates in it (the outer engine transaction covers the whole return-path
    /// STEP 0-6 atomically).  Otherwise a default-isolation transaction wraps the INSERT.</para>
    /// </summary>
    /// <param name="db">The DbContext owning the current (or ambient) transaction.</param>
    /// <param name="instanceId">The FK to <see cref="ProcessInstance"/>.</param>
    /// <param name="tenantCode">Tenant isolation code (copied from the instance).</param>
    /// <param name="action">The action being recorded.</param>
    /// <param name="nodeKey">The node this event relates to (null for instance-level events).</param>
    /// <param name="actorITCode">The user who triggered this event (null for system/auto actions).</param>
    /// <param name="beforeState">State before the transition (human-readable enum name).</param>
    /// <param name="afterState">State after the transition (human-readable enum name).</param>
    /// <param name="reason">Optional comment or rejection reason.</param>
    /// <param name="generation">
    /// Generation epoch of the <see cref="ProcessInstance"/> at event-append time (Wave-3 §8.4).
    /// Null for pre-Wave-3 callers and instance-level events not tied to a generation.
    /// Stored for audit grouping only — never enters Seq math or control-flow branching.
    /// </param>
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
        int? generation = null,
        CancellationToken ct = default)
    {
        // Allocate the next Seq value from the per-instance counter.
        // The CAS loop in AllocateSeqAsync retries when the RowVer changes under contention —
        // this is cheaper and more portable than opening a Serializable transaction.
        int seq = await AllocateSeqWithRetryAsync(db, instanceId, ct);

        // Participate in an ambient transaction when one is already open (e.g. the return-path
        // engine wraps STEP 0-6 in one transaction).  Otherwise, wrap just the INSERT in a
        // default-isolation transaction so the row is never left half-written.
        bool ownsTransaction = db.Database.CurrentTransaction is null;
        IDbContextTransaction? tx = ownsTransaction
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        try
        {
            var logEntry = new WorkflowEventLog
            {
                ID = Guid.NewGuid(),
                TenantCode = tenantCode,
                InstanceId = instanceId,
                Seq = seq,
                ActorITCode = actorITCode,
                Action = action,
                NodeKey = nodeKey,
                BeforeState = beforeState,
                AfterState = afterState,
                Reason = reason,
                Generation = generation,
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

    // ── Private: Seq allocation ────────────────────────────────────────────────

    /// <summary>
    /// Allocate the next Seq from <see cref="ProcessInstance.NextSeq"/> via a CAS retry loop.
    ///
    /// <para>Each attempt:
    /// <list type="number">
    ///   <item>Read current <c>NextSeq</c> + <c>RowVer</c> from the instance row.</item>
    ///   <item>Call <see cref="GuardedTransition.AllocateSeqAsync"/> — a
    ///         single-row <c>ExecuteUpdateAsync</c> with <c>WHERE RowVer == expected</c>.</item>
    ///   <item>rows == 1 → allocation succeeded; rows == 0 → RowVer changed under concurrent
    ///         transition → retry.</item>
    /// </list>
    /// Up to <see cref="MaxSeqRetries"/> retries; then throws.
    /// </para>
    /// </summary>
    private static async Task<int> AllocateSeqWithRetryAsync(
        DbContext db,
        Guid instanceId,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt < MaxSeqRetries; attempt++)
        {
            // Re-read the instance to get the current RowVer (may have changed between attempts).
            var inst = await db.Set<ProcessInstance>()
                .AsNoTracking()
                .Where(p => p.ID == instanceId)
                .Select(p => new { p.NextSeq, p.RowVer })
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException(
                       $"ProcessInstance {instanceId} not found during Seq allocation.");

            var (rows, seq) = await GuardedTransition.AllocateSeqAsync(
                db, instanceId, inst.RowVer, ct);

            if (rows == 1)
                return seq;

            // rows == 0: RowVer changed — another transition updated the instance concurrently.
            // Back off briefly (yield once; no sleep — stay async-friendly) and retry.
            await Task.Yield();
        }

        throw new InvalidOperationException(
            $"Could not allocate event log Seq for ProcessInstance {instanceId} " +
            $"after {MaxSeqRetries} attempts. Possible extreme contention.");
    }
}
