#nullable enable
// WorkflowTimerExecutor — Sweep region (SweepExpiredAtActionDelegationsAsync).
//
// #668: partial-class split of WorkflowTimerExecutor.cs — pure code motion (see
// WorkflowTimerExecutor.cs for the shared design notes and invariants). Members below were
// cut verbatim (including their original doc comments) from WorkflowTimerExecutor.cs; no
// signature, accessibility, or logic changes were made during the move.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.Notifications;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

internal sealed partial class WorkflowTimerExecutor
{
    // ── Phase-3: AtAction expired-delegation sweep (WF-20.5) ─────────────────

    /// <summary>
    /// AtAction expired-delegation sweep (R5): doubly opt-in —
    /// (a) <see cref="WorkFlowOptions.DelegationWindowMode"/> must be
    ///     <see cref="DelegationWindowMode.AtAction"/> AND
    /// (b) <see cref="WorkFlowOptions.DelegationExpiredSweep"/> must be
    ///     <see cref="DelegationExpiredSweep.RevertToPrincipal"/>.
    ///
    /// <para>AtAction mode is ctor-blocked on Oracle/DaMeng; this sweep is therefore
    /// unreachable on providers that don't support nullable-DateTime query patterns.
    /// Expiry is evaluated CLIENT-SIDE (no DateTime in UPDATE WHERE — portability preserved).</para>
    ///
    /// <para>Per-row revert shape (mirrors <see cref="GuardedTransition.RevokeDelegatedTasksAsync"/>):
    /// <c>State==Pending AND RowVer AND Generation</c> CAS;
    /// <c>AssigneeITCode=DelegatedFromITCode</c>, clear delegation fields, bump RowVer.
    /// Concurrent human claim wins cleanly (rows==0 → safe no-op; partial success valid).</para>
    ///
    /// <para>Epoch bump + <c>DelegationExpiredReverted</c> event (actor NULL) + notify principal
    /// follow each successful revert.</para>
    /// </summary>
    private async Task SweepExpiredAtActionDelegationsAsync(DateTime now, CancellationToken ct)
    {
        // Gate (a): only when DelegationWindowMode==AtAction.
        if (_options.DelegationWindowMode != DelegationWindowMode.AtAction)
            return;

        // Gate (b): only when DelegationExpiredSweep==RevertToPrincipal.
        if (_options.DelegationExpiredSweep != DelegationExpiredSweep.RevertToPrincipal)
            return;

        var db = GetDb();

        // SELECT expired delegated Pending tasks — expiry evaluated CLIENT-SIDE on the snapshot
        // (nullable-DateTime SELECT-side only; no DateTime in UPDATE WHERE — portability preserved).
        // IgnoreQueryFilters: cross-tenant system sweep; every write is PK+RowVer CAS.
        // Issue #665: bounded per-tick sweep — see WorkFlowOptions.SweepBatchSize.
        // Any remainder beyond the cap is picked up on the next poll tick.
        var expiredDelegated = await db.Set<ApprovalTask>()
            .IgnoreQueryFilters() // justified: cross-tenant system reaper sweep; all writes are PK+RowVer CAS
            .AsNoTracking()
            .Where(t => t.State == TaskState.Pending
                         && t.DelegationRuleId != null
                         && t.DelegatedFromITCode != null
                         && t.DelegationExpiresUtc != null
                         && t.DelegationExpiresUtc < now)
            .Select(t => new
            {
                t.ID,
                t.RowVer,
                t.Generation,
                t.NodeInstanceId,
                t.AssigneeITCode,
                t.DelegatedFromITCode,
                t.TenantCode,
            })
            .Take(_options.SweepBatchSize)
            .ToListAsync(ct);

        foreach (var row in expiredDelegated)
        {
            ct.ThrowIfCancellationRequested();

            // FIX #325: Set the scoped IDataContext's TenantCode to the current task's
            // TenantCode before the CAS write so EF's HasQueryFilter (TenantCode == this.TenantCode)
            // matches the correct tenant's row. The candidate SELECT uses IgnoreQueryFilters()
            // (cross-tenant sweep) so all expired delegations are found regardless of tenant; but
            // the ExecuteUpdateAsync CAS writes go through the query filter and therefore need the
            // tenant set on the context. PK+RowVer CAS ensures no cross-tenant write is possible.
            // Test path: _dc is null (WfTestContext has no ITenant filters) — no action needed.
            string? previousTenantCode = null;
            if (_dc is not null)
            {
                previousTenantCode = _dc.TenantCode;
                _dc.SetTenantCode(row.TenantCode);
            }

            try
            {
                if (string.IsNullOrWhiteSpace(row.DelegatedFromITCode))
                {
                    // Defensive: DelegatedFromITCode is null/empty — skip (principal unknown).
                    // continue inside try still executes the finally block (C# spec §13.11).
                    _logger.LogWarning(
                        "AtAction sweep: task {TaskId} has expired delegation but null DelegatedFromITCode — skipping",
                        row.ID);
                    continue;
                }

                string principal = row.DelegatedFromITCode!;

                // C13 (#327): collision pre-check — mirrors escalate-path (lines ~1308-1346) to kill infinite
                // per-tick retry when the principal was added (加签'd) onto the same node. If principal already
                // holds ANY task row on (NodeInstanceId, Generation), skip the flip and emit a FailClosed audit
                // event for operator visibility. Do NOT revert — leave the expired delegated slot as-is.
                bool hasCollision = await db.Set<ApprovalTask>()
                    .IgnoreQueryFilters() // justified: cross-tenant system sweep; all writes are PK+RowVer CAS
                    .AsNoTracking()
                    .AnyAsync(t => t.NodeInstanceId == row.NodeInstanceId
                                    && t.Generation == row.Generation
                                    && t.AssigneeITCode == principal, ct);

                if (hasCollision)
                {
                    _logger.LogInformation(
                        "AtAction sweep: principal {Principal} already has a task row on node {NodeId} gen {Gen} " +
                        "— collision detected, leaving expired delegated slot, notify-only",
                        principal, row.NodeInstanceId, (uint)row.Generation);

                    var nodeForInstColl = await db.Set<NodeInstance>()
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .Where(n => n.ID == row.NodeInstanceId)
                        .Select(n => new { n.InstanceId })
                        .FirstOrDefaultAsync(ct);

                    if (nodeForInstColl is not null)
                    {
                        var instSnapColl = await db.Set<ProcessInstance>()
                            .IgnoreQueryFilters()
                            .AsNoTracking()
                            .Where(i => i.ID == nodeForInstColl.InstanceId)
                            .Select(i => new { i.ID, i.RowVer, i.TenantCode })
                            .FirstOrDefaultAsync(ct);

                        if (instSnapColl is not null)
                        {
                            var seqColl = await GuardedTransition.AllocateSeqWithRetryAsync(db, instSnapColl.ID, instSnapColl.RowVer, ct);
                            if (seqColl.rows == 1)
                            {
                                db.Set<WorkflowEventLog>().Add(new WorkflowEventLog
                                {
                                    ID          = Guid.NewGuid(),
                                    TenantCode  = instSnapColl.TenantCode,
                                    InstanceId  = instSnapColl.ID,
                                    Seq         = seqColl.seq,
                                    Action      = EventAction.FailClosed,
                                    NodeKey     = null,
                                    ActorITCode = null,
                                    Generation  = (int?)(uint)row.Generation,
                                    Reason      = "delegation-expiry revert: principal already participant — collision notify-only",
                                    OccurredUtc = now,
                                });
                                await db.SaveChangesAsync(ct);
                            }
                        }
                    }

                    continue;
                }

                // Per-row CAS: revert to principal, clear delegation fields, bump RowVer.
                // Concurrent human claim → rows==0 → safe no-op (partial success valid).
                int revertRows = await db.Set<ApprovalTask>()
                    .Where(t => t.ID == row.ID
                                 && t.State == TaskState.Pending
                                 && t.RowVer == row.RowVer
                                 && t.Generation == row.Generation)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(t => t.AssigneeITCode, principal)
                               .SetProperty(t => t.DelegatedFromITCode, (string?)null)
                               .SetProperty(t => t.DelegationRuleId, (Guid?)null)
                               .SetProperty(t => t.DelegationExpiresUtc, (DateTime?)null)
                               .SetProperty(t => t.WindowVerifiedUtc, (DateTime?)null)
                               .SetProperty(t => t.RowVer, t => t.RowVer + 1),
                        ct);

                if (revertRows == 0)
                {
                    // Concurrent human claim or supersede won — not an error.
                    _logger.LogDebug(
                        "AtAction sweep: task {TaskId} — revert CAS lost (human claimed or superseded) — no-op",
                        row.ID);
                    continue;
                }

                // Epoch bump: re-read node RowVer fresh after task CAS.
                var nodeSnapForEpoch = await db.Set<NodeInstance>()
                    .IgnoreQueryFilters() // cross-tenant system sweep
                    .AsNoTracking()
                    .Where(n => n.ID == row.NodeInstanceId && n.State == NodeState.Activated)
                    .Select(n => new { n.ID, n.RowVer })
                    .FirstOrDefaultAsync(ct);

                if (nodeSnapForEpoch is not null)
                {
                    // Best-effort epoch bump; rows==0 (node completed concurrently) is safe.
                    await GuardedTransition.AdvanceNodeApproverSetEpochAsync(
                        db, nodeSnapForEpoch.ID, nodeSnapForEpoch.RowVer, ct);
                }

                // Append DelegationExpiredReverted event (actor NULL = system sweep).
                // Re-read instance RowVer to get a fresh Seq allocation target.
                // We look up the instance through the node — may return null if node was removed.
                var nodeForInst = await db.Set<NodeInstance>()
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(n => n.ID == row.NodeInstanceId)
                    .Select(n => new { n.InstanceId })
                    .FirstOrDefaultAsync(ct);

                if (nodeForInst is not null)
                {
                    var instSnap = await db.Set<ProcessInstance>()
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .Where(i => i.ID == nodeForInst.InstanceId)
                        .Select(i => new { i.ID, i.RowVer, i.TenantCode })
                        .FirstOrDefaultAsync(ct);

                    if (instSnap is not null)
                    {
                        var seqResult = await GuardedTransition.AllocateSeqWithRetryAsync(
                            db, instSnap.ID, instSnap.RowVer, ct);

                        if (seqResult.rows == 1)
                        {
                            db.Set<WorkflowEventLog>().Add(new WorkflowEventLog
                            {
                                ID          = Guid.NewGuid(),
                                TenantCode  = instSnap.TenantCode,
                                InstanceId  = instSnap.ID,
                                Seq         = seqResult.seq,
                                Action      = EventAction.DelegationExpiredReverted,
                                NodeKey     = null,
                                ActorITCode = null, // system sweep
                                Generation  = (int?)(uint)row.Generation,
                                OccurredUtc = now,
                            });
                            await db.SaveChangesAsync(ct);
                        }

                        // Post-commit notify: alert principal that their task was reverted back to them.
                        if (_notifier is not null)
                        {
                            try
                            {
                                var freshInstance = await db.Set<ProcessInstance>()
                                    .IgnoreQueryFilters()
                                    .AsNoTracking()
                                    .Where(i => i.ID == instSnap.ID)
                                    .FirstOrDefaultAsync(ct);

                                if (freshInstance is not null)
                                {
                                    var taskForNotifier = new ApprovalTask
                                    {
                                        ID             = row.ID,
                                        AssigneeITCode = principal,
                                        NodeInstanceId = row.NodeInstanceId,
                                        State          = TaskState.Pending,
                                    };

                                    // Reuse NotifyTaskAssignedAsync to alert the reverted principal.
                                    // This is structurally equivalent to a new assignment notification.
                                    await _notifier.NotifyTaskAssignedAsync(
                                        freshInstance,
                                        new NodeInstance { ID = row.NodeInstanceId, InstanceId = instSnap.ID },
                                        taskForNotifier, ct)
                                        .ConfigureAwait(false);
                                }
                            }
                            catch (Exception notifyEx)
                            {
                                _logger.LogError(notifyEx,
                                    "AtAction sweep: notification failed for task {TaskId} principal {Principal} — ignored",
                                    row.ID, principal);
                            }
                        }
                    }
                }

                _logger.LogInformation(
                    "AtAction sweep: reverted task {TaskId} from delegate {Delegate} to principal {Principal}",
                    row.ID, row.AssigneeITCode, principal);
            }
            catch (Exception ex)
            {
                // Per-row try/catch: one bad row never blocks the rest.
                _logger.LogError(ex,
                    "AtAction sweep: failed for task {TaskId} — will retry next tick",
                    row.ID);
            }
            finally
            {
                // Restore tenant context for next iteration.
                // Belt-and-suspenders: PK+RowVer CAS is the real cross-tenant guard.
                if (_dc is not null)
                    _dc.SetTenantCode(previousTenantCode);
            }
        }
    }
}
