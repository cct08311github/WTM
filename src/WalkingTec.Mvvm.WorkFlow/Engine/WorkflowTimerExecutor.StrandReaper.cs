#nullable enable
// WorkflowTimerExecutor — StrandReaper region (Wave-5 Returning-lease reclaim + stranded-sequential-node re-drive).
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
    // ── Phase-2: Returning-lease reclaim ─────────────────────────────────────

    private async Task ReclaimExpiredLeasesAsync(DateTime now, CancellationToken ct)
    {
        var db = GetDb();

        // SELECT expired Returning instances — expiry evaluated CLIENT-SIDE on the snapshot
        // so NO DateTime appears in any UPDATE WHERE clause (portable: Oracle/DaMeng safe).
        // Issue #665: bounded per-tick sweep — see WorkFlowOptions.SweepBatchSize.
        // Any remainder beyond the cap is picked up on the next poll tick.
        // #899 follow-up (cross-vendor review of PR #918): IsValid==true added -- a soft-deleted
        // instance stuck in Returning must not be reclaimed back to Running.
        var expiredLeases = await db.Set<ProcessInstance>()
            .IgnoreQueryFilters() // cross-tenant system sweep; every reclaim write is PK+RowVer CAS
            .AsNoTracking()
            .Where(i => i.State == InstanceState.Returning
                         && i.ReturningLeaseUtc != null
                         && i.ReturningLeaseUtc < now
                         && i.IsValid == true)
            .Select(i => new { i.ID, i.RowVer, i.TenantCode })
            .Take(_options.SweepBatchSize)
            .ToListAsync(ct);

        foreach (var inst in expiredLeases)
        {
            ct.ThrowIfCancellationRequested();

            // FIX #325: Set the scoped IDataContext's TenantCode to the current instance's
            // TenantCode before the CAS write so EF's HasQueryFilter (TenantCode == this.TenantCode)
            // matches the correct tenant's row. The candidate SELECT uses IgnoreQueryFilters()
            // (cross-tenant sweep) so all expired leases are found regardless of tenant; but
            // ReclaimReturningLeaseByRowVerAsync's ExecuteUpdateAsync goes through the query filter
            // and therefore needs the tenant set. PK+RowVer CAS ensures no cross-tenant write.
            // Test path: _dc is null (WfTestContext has no ITenant filters) — no action needed.
            string? previousTenantCode = null;
            if (_dc is not null)
            {
                previousTenantCode = _dc.TenantCode;
                _dc.SetTenantCode(inst.TenantCode);
            }

            try
            {
                // ReclaimReturningLeaseByRowVerAsync: NO DateTime in UPDATE WHERE (portable).
                // Expiry was already checked client-side above on the RowVer-pinned snapshot.
                var rows = await GuardedTransition.ReclaimReturningLeaseByRowVerAsync(
                    db, inst.ID, inst.RowVer, ct);

                if (rows == 1)
                {
                    _logger.LogInformation(
                        "Reclaimed expired Returning lease for ProcessInstance {InstanceId}",
                        inst.ID);
                }
                // rows==0: another host beat us — safe no-op.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Lease reclaim failed for ProcessInstance {InstanceId} — will retry next tick",
                    inst.ID);
            }
            finally
            {
                // Restore tenant context so the next row (or any subsequent scope use) does not
                // inherit this instance's tenant. Belt-and-suspenders: PK+RowVer CAS is the real guard.
                if (_dc is not null)
                    _dc.SetTenantCode(previousTenantCode);
            }
        }
    }

    // ── Phase-4: Strand-reaper (Issue #359) ──────────────────────────────────

    /// <summary>
    /// Phase-4 strand-reaper: re-drives Sequential approval nodes where the SequencePointer
    /// was never advanced after a system auto-approve claim committed (crash window between
    /// <see cref="WorkflowEngine.SystemClaimTaskAsync"/> commit and
    /// <see cref="WorkflowEngine.SystemContinueTaskAsync"/> post-commit).
    ///
    /// <para><strong>Strand signature:</strong> ProcessInstance Running + NodeInstance Activated +
    /// ApproveMode==Sequential + task at SequenceOrder==SequencePointer is terminal
    /// (AutoApproved/AutoRejected) + SequencePointer &lt; TotalRequired.</para>
    ///
    /// <para><strong>Re-drive entry:</strong> <see cref="WorkflowEngine.SystemContinueTaskAsync"/>
    /// — the same post-commit continuation the timer normally calls after a successful claim.
    /// SystemContinueTaskAsync calls ExecuteApproveCompletionAsync which re-reads NodeInstance
    /// fresh and atomically advances SequencePointer + activates the next task (or completes
    /// the node) via RowVer-guarded CAS transactions.  Two concurrent timer hosts both calling
    /// this on the same strand produce exactly one advance and one AlreadyHandled no-op (pointer
    /// CAS returns 0 for the loser).</para>
    ///
    /// <para><strong>Concrete engine required:</strong> SystemContinueTaskAsync is on
    /// <see cref="WorkflowEngine"/> (internal); when a custom IWorkflowEngine is registered,
    /// the cast fails → <c>null</c> → reaper gate off for that host (documented limitation).</para>
    ///
    /// <para><strong>Idempotent:</strong> calling this twice on the same strand is safe because
    /// ExecuteApproveCompletionAsync uses RowVer-guarded pointer-advance CAS — the second call
    /// finds pointer already advanced → pointer CAS returns 0 → AlreadyHandled → no-op.
    /// Disabled when <see cref="WorkFlowOptions.StrandReaperBatchSize"/> == 0.</para>
    ///
    /// <para><strong>Lock order:</strong> no explicit locks acquired here; SystemContinueTaskAsync
    /// internally follows Task → Node → Instance order (canonical engine order) via
    /// GuardedTransition.</para>
    /// </summary>
    private async Task ReDriveStrandedSequentialNodesAsync(DateTime now, CancellationToken ct)
    {
        // Gate: disabled when batch size is 0.
        if (_options.StrandReaperBatchSize <= 0)
            return;

        // Gate: concrete WorkflowEngine required for SystemContinueTaskAsync (internal method).
        // Custom IWorkflowEngine registrations that aren't WorkflowEngine → null → gate off.
        var concreteEngine = _engine as WorkflowEngine;
        if (concreteEngine is null)
            return;

        var db = GetDb();

        // ── Step 1: Candidate SELECT (cross-tenant) ───────────────────────────────
        // Activated Sequential nodes with pointer < total (potential strands).
        // IgnoreQueryFilters: cross-tenant system sweep — all re-drives go through
        // SystemContinueTaskAsync which uses RowVer-guarded CAS writes.
        // No cross-tenant write is possible: each write is pinned to the node's own rows.
        // TenantCode projected so we can set _dc.TenantCode before calling the engine.
        // Issue #665: deterministic, starvation-proof ordering — oldest-Activated-first.
        //
        // Without an OrderBy, Take(StrandReaperBatchSize) is a provider-defined, effectively
        // arbitrary window. With more than batch-size candidates, a genuinely stranded node
        // outside that window could go unselected on every tick, silently starving the
        // crash-recovery backstop.
        //
        // NodeInstance.UpdateTime (BasePoco) is NOT usable here: every post-mint write to
        // NodeInstance goes through GuardedTransition's ExecuteUpdateAsync CAS helpers, which
        // bypass DataContext's ChangeTracker-based audit stamping — UpdateTime is never written
        // by the WorkFlow engine and stays permanently null. Ordering on it would collapse to a
        // null/null tie on every row, degrading to OrderBy(ID) alone — deterministic, but NOT
        // starvation-proof: it would return the exact same window forever, permanently starving
        // any node whose ID sorts after the batch cutoff.
        //
        // ActivatedAt IS reliably populated for this candidate set: the only write path that
        // transitions State -> Activated is GuardedTransition.ActivateNodeInstanceAsync, which
        // always stamps ActivatedAt in that same atomic CAS. Ordering oldest-ActivatedAt-first
        // puts the longest-waiting nodes — including genuine strands, which stop making progress
        // the instant they strand — at the front of the window. Because nodes leave the Activated
        // population as they advance/complete, the "oldest surviving" set naturally rotates tick
        // to tick, giving real coverage instead of a fixed, permanently-starved tail.
        //
        // The null-check is defensive belt-and-suspenders (ActivatedAt is not null in practice
        // for State==Activated rows today) and keeps the ordering translatable/null-safe across
        // all 7 DBTypeEnum providers if a future code path ever mints directly into Activated.
        var activatedSeqNodes = await db.Set<NodeInstance>()
            .IgnoreQueryFilters() // justified: cross-tenant system reaper sweep; all re-drives are RowVer CAS-guarded via SystemContinueTaskAsync
            .AsNoTracking()
            .Where(n => n.State == NodeState.Activated
                         && n.ApproveMode == ApproveMode.Sequential
                         && n.SequencePointer < n.TotalRequired)
            .OrderBy(n => n.ActivatedAt == null)
            .ThenBy(n => n.ActivatedAt)
            .ThenBy(n => n.ID)
            .Select(n => new { n.ID, n.RowVer, n.InstanceId, n.TenantCode, n.SequencePointer })
            .Take(_options.StrandReaperBatchSize)
            .ToListAsync(ct);

        if (activatedSeqNodes.Count == 0)
            return;

        // ── Step 2: Filter to Running instances ───────────────────────────────────
        // Only re-drive nodes whose owning instance is still Running.
        // A two-step materialize approach avoids complex correlated subqueries
        // that may not translate cleanly across all supported providers (SQLite/Oracle/DaMeng).
        // #899 follow-up (cross-vendor review of PR #918): IsValid==true added -- a soft-deleted
        // instance must not be pulled into re-drive even if its State column still reads Running.
        var instanceIds = activatedSeqNodes.Select(n => n.InstanceId).Distinct().ToList();
        var runningInstanceIds = await db.Set<ProcessInstance>()
            .IgnoreQueryFilters() // cross-tenant system sweep (same justification)
            .AsNoTracking()
            .Where(i => instanceIds.Contains(i.ID) && i.State == InstanceState.Running && i.IsValid)
            .Select(i => i.ID)
            .ToListAsync(ct);

        if (runningInstanceIds.Count == 0)
            return;

        var runningSet = new HashSet<Guid>(runningInstanceIds);

        // ── Step 3: Per-candidate strand-check and re-drive ───────────────────────
        foreach (var node in activatedSeqNodes)
        {
            ct.ThrowIfCancellationRequested();

            if (!runningSet.Contains(node.InstanceId))
                continue; // instance not Running — skip

            // ── Strand-check: find the terminal task at SequencePointer ───────────
            // Idempotent: if the pointer was already advanced by another host (task at
            // pointer is no longer AutoApproved/AutoRejected), AnyAsync returns false → skip.
            // #899 follow-up (cross-vendor review of PR #918): IsValid==true added -- a
            // soft-deleted task must not be treated as a live strand signature.
            var terminalTask = await db.Set<ApprovalTask>()
                .IgnoreQueryFilters() // cross-tenant system sweep
                .AsNoTracking()
                .Where(t => t.NodeInstanceId == node.ID
                             && t.SequenceOrder == node.SequencePointer
                             && (t.State == TaskState.AutoApproved
                                 || t.State == TaskState.AutoRejected)
                             && t.IsValid == true)
                .Select(t => new { t.ID, t.State, t.AssigneeITCode })
                .FirstOrDefaultAsync(ct);

            if (terminalTask is null)
                continue; // not a strand — task at pointer is still Pending (healthy) or pointer already advanced

            // ── Read full entity rows needed by SystemContinueTaskAsync ───────────
            // SystemContinueTaskAsync takes NodeInstance + ProcessInstance (full entities).
            // Re-reads are fresh; concurrent pointer advance on another host is safe because
            // ExecuteApproveCompletionAsync uses a RowVer-guarded CAS internally.
            var fullNode = await db.Set<NodeInstance>()
                .IgnoreQueryFilters() // cross-tenant system sweep
                .AsNoTracking()
                .SingleOrDefaultAsync(n => n.ID == node.ID, ct);

            // D1: re-validate the strand still holds at the SAME pointer we scanned.
            // A concurrent host (or normal actor) may have advanced the pointer between the
            // batch scan and this fresh read — if so, this is no longer our strand: skip it.
            if (fullNode is null
                || fullNode.State != NodeState.Activated
                || fullNode.SequencePointer != node.SequencePointer
                || fullNode.SequencePointer >= fullNode.TotalRequired)
            {
                continue;
            }

            // #899 follow-up (cross-vendor review of PR #918): re-checked below (not in the query
            // itself, to keep this a SingleOrDefaultAsync like its sibling `fullNode` read) --
            // closes the TOCTOU window between Step 2's batch IsValid check and this per-candidate
            // fresh read.
            var fullInstance = await db.Set<ProcessInstance>()
                .IgnoreQueryFilters() // cross-tenant system sweep
                .AsNoTracking()
                .SingleOrDefaultAsync(i => i.ID == node.InstanceId, ct);

            if (fullInstance is null || fullInstance.State != InstanceState.Running || !fullInstance.IsValid)
                continue; // instance no longer Running (or soft-deleted) — safe skip

            // ── This node matches the strand signature — re-drive via SystemContinueTaskAsync ──
            // Tenant-scoped: set _dc.TenantCode before calling the engine so that
            // any downstream HasQueryFilter scoped writes resolve to the correct tenant.
            // Test path: _dc is null → no tenant setup needed.
            string? previousTenantCode = null;
            if (_dc is not null)
            {
                previousTenantCode = _dc.TenantCode;
                _dc.SetTenantCode(node.TenantCode);
            }

            try
            {
                // Re-drive: SystemContinueTaskAsync calls ExecuteApproveCompletionAsync (Sequential path)
                // which re-reads NodeInstance fresh and atomically advances SequencePointer + activates
                // the next task (or completes the node if pointer+1 >= TotalRequired).
                //
                // CAS-safety: ExecuteApproveCompletionAsync wraps pointer advance in a RowVer-guarded
                // transaction.  If another host or normal flow already advanced the pointer, the pointer
                // CAS returns 0 rows → AlreadyHandled → safe no-op.  No double-advance is possible.
                var ctx = new WorkflowEngine.SystemClaimContext(
                    TaskId:       terminalTask.ID,
                    AssigneeITCode: terminalTask.AssigneeITCode ?? string.Empty,
                    NextState:    terminalTask.State,
                    ApproveMode:  ApproveMode.Sequential,
                    NodeInst:     fullNode,
                    Instance:     fullInstance);

                var result = await concreteEngine.SystemContinueTaskAsync(ctx, ct);

                if (result.Code == WorkflowActionCode.Advanced
                    || result.Code == WorkflowActionCode.InstanceApproved
                    || result.Code == WorkflowActionCode.Rejected)
                {
                    _logger.LogInformation(
                        "Phase-4 strand-reaper: re-drove stranded Sequential node {NodeId} (instance {InstanceId}, " +
                        "pointer {Pointer}) — result={Result}",
                        node.ID, node.InstanceId, node.SequencePointer, result.Code);
                }
                // AlreadyHandled (another host won the CAS), Blocked (next step human), etc. are safe no-ops.
            }
            catch (Exception ex)
            {
                // Per-candidate try/catch: one bad candidate never blocks the rest.
                // The strand stays in place and will be retried on the next tick.
                _logger.LogError(ex,
                    "Phase-4 strand-reaper: re-drive failed for node {NodeId} (instance {InstanceId}) — will retry next tick",
                    node.ID, node.InstanceId);
            }
            finally
            {
                // Restore tenant context for the next candidate. Belt-and-suspenders.
                if (_dc is not null)
                    _dc.SetTenantCode(previousTenantCode);
            }
        }
    }
}
