#nullable enable
// WorkflowEngine — Join region (parallel/inclusive gateway join-branch advance + orphan check).
//
// #668: partial-class split of WorkflowEngine.cs — pure code motion (see WorkflowEngine.cs
// for the shared design notes, invariants, and race-condition catalogue). Members below were
// cut verbatim (including their original doc comments) from WorkflowEngine.cs; no signature,
// accessibility, or logic changes were made during the move.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine.Routing;
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.Notifications;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

internal sealed partial class WorkflowEngine
{
    /// <summary>
    /// Handle a branch token completing and routing into a Join node (WF-17 §4.3).
    ///
    /// <para>Protocol:
    /// <list type="number">
    ///   <item>Ensure the Join NodeInstance exists (mint if needed — idempotent via unique index).</item>
    ///   <item>Complete this branch token via CAS (<c>CompletedApproved</c>).</item>
    ///   <item>Activate the Join node if still Pending.</item>
    ///   <item>Re-read Join for fresh RowVer.</item>
    ///   <item>Call <c>IncrementJoinArrivedAsync</c> — record this arrival.</item>
    ///   <item>Call <c>FireJoinIfSatisfiedAsync</c> — exactly-once CAS fire.</item>
    ///   <item>If fire won (rows==1): mint the Join's successor node and return <c>NodeCompleted</c>.</item>
    ///   <item>If fire lost (rows==0): another branch already fired the Join OR quorum not yet met.
    ///        Check for the orphan fail-closed backstop (§4.4). Return <c>Advanced</c>.</item>
    /// </list>
    /// </para>
    /// </summary>
    private async Task<WorkflowActionResult> AdvanceBranchIntoJoinAsync(
        NodeInstance branchNode,
        ProcessInstance instance,
        WorkflowGraph graph,
        string joinNodeKey,
        CancellationToken ct)
    {
        // Track whether CheckJoinOrphanAsync should run post-commit (invariant #6: outside tx).
        NodeInstance? postCommitOrphanCheck = null;

        var result = await ExecuteInTransactionAsync(async innerCt =>
        {
            postCommitOrphanCheck = null; // reset on each deadlock retry

            var joinDef = graph.Nodes.First(n => string.Equals(n.NodeKey, joinNodeKey, StringComparison.Ordinal));

            IDbContextTransaction? ownedTx = null;
            if (Db.Database.CurrentTransaction is null)
                ownedTx = await Db.Database.BeginTransactionAsync(innerCt);
            try
            {
                // [1] Mint join node (idempotent, in tx).
                await GuardedTransition.MintNodeInstanceGuardedAsync(
                    Db, instance, joinDef, instance.Generation, innerCt);

                // [2] STATE FLIP: Complete branch token (CAS) — before any appends.
                var branchCompleteRows = await GuardedTransition.CompleteNodeInstanceAsync(
                    Db, branchNode.ID, branchNode.RowVer, NodeState.CompletedApproved,
                    generation: instance.Generation, ct: innerCt);

                if (branchCompleteRows == 0)
                {
                    _logger.LogDebug(
                        "AdvanceBranchIntoJoinAsync: branch {BranchId} already completed by concurrent caller.",
                        branchNode.ID);
                    // Not committed — finally disposes = auto-rollback.
                    return WorkflowActionResult.AlreadyHandled;
                }

                // [3] Re-read join node for current state and RowVer.
                var joinNode = await Db.Set<NodeInstance>()
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        n => n.InstanceId == instance.ID
                          && n.NodeKey == joinNodeKey
                          && n.Generation == instance.Generation,
                        innerCt);

                if (joinNode is null)
                {
                    _logger.LogError(
                        "AdvanceBranchIntoJoinAsync: Join node '{JoinKey}' not found for instance {InstanceId}. " +
                        "Possible mint failure.",
                        joinNodeKey, instance.ID);
                    return WorkflowActionResult.FailClosedRouting;
                }

                if (joinNode.State == NodeState.CompletedApproved)
                {
                    // Another branch already fired. Commit branch completion + log, return Advanced.
                    await WorkflowEventLogWriter.AppendAsync(
                        Db, instance.ID, instance.TenantCode,
                        EventAction.AutoAdvance,
                        nodeKey: branchNode.NodeKey,
                        actorITCode: null,
                        beforeState: NodeState.Activated.ToString(),
                        afterState: NodeState.CompletedApproved.ToString(),
                        ct: innerCt);
                    if (ownedTx is not null) await ownedTx.CommitAsync(innerCt);
                    return WorkflowActionResult.Advanced;
                }

                // Track whether we activated the join (for log ordering).
                bool activatedJoin = false;

                // [5] STATE FLIP: Activate join if Pending.
                if (joinNode.State == NodeState.Pending)
                {
                    var activateJoinRows = await GuardedTransition.ActivateNodeInstanceAsync(
                        Db, joinNode.ID, joinNode.RowVer, DateTime.UtcNow,
                        generation: instance.Generation, ct: innerCt);

                    if (activateJoinRows == 1)
                        activatedJoin = true;

                    // [6] Re-read after activate attempt (another caller may have activated it first).
                    joinNode = await Db.Set<NodeInstance>()
                        .AsNoTracking()
                        .SingleAsync(n => n.ID == joinNode.ID, innerCt);
                }

                if (joinNode.State != NodeState.Activated)
                {
                    // Join already in terminal state (e.g. concurrent branch fired it just now).
                    // Commit branch completion + log as durable.
                    await WorkflowEventLogWriter.AppendAsync(
                        Db, instance.ID, instance.TenantCode,
                        EventAction.AutoAdvance,
                        nodeKey: branchNode.NodeKey,
                        actorITCode: null,
                        beforeState: NodeState.Activated.ToString(),
                        afterState: NodeState.CompletedApproved.ToString(),
                        ct: innerCt);
                    if (ownedTx is not null) await ownedTx.CommitAsync(innerCt);
                    return WorkflowActionResult.Advanced;
                }

                // [7] STATE FLIP: Increment arrival count (CAS).
                await GuardedTransition.IncrementJoinArrivedAsync(
                    Db, joinNode.ID, joinNode.RowVer, instance.Generation, innerCt);

                // Re-read post-increment for fresh RowVer (whether CAS won or lost).
                // Inside ambient tx: read-committed sees own writes on SQL Server; SQLite identical.
                joinNode = await Db.Set<NodeInstance>()
                    .AsNoTracking()
                    .SingleAsync(n => n.ID == joinNode.ID, innerCt);

                // [9] STATE FLIP: Try to fire the Join (exactly-once CAS).
                var fireRows = await GuardedTransition.FireJoinIfSatisfiedAsync(
                    Db, joinNode.ID, joinNode.RowVer, instance.Generation, innerCt);

                // ── ALL STATE FLIPS DONE. Now AppendAsync. ─────────────────────────────────

                // Append: branch complete log.
                await WorkflowEventLogWriter.AppendAsync(
                    Db, instance.ID, instance.TenantCode,
                    EventAction.AutoAdvance,
                    nodeKey: branchNode.NodeKey,
                    actorITCode: null,
                    beforeState: NodeState.Activated.ToString(),
                    afterState: NodeState.CompletedApproved.ToString(),
                    ct: innerCt);

                // Append: join activate log (only if we won the activation CAS).
                if (activatedJoin)
                {
                    await WorkflowEventLogWriter.AppendAsync(
                        Db, instance.ID, instance.TenantCode,
                        EventAction.AutoAdvance,
                        nodeKey: joinNodeKey,
                        actorITCode: null,
                        beforeState: NodeState.Pending.ToString(),
                        afterState: NodeState.Activated.ToString(),
                        ct: innerCt);
                }

                if (fireRows == 0)
                {
                    // Quorum not yet met OR concurrent loser (another branch fired it first).
                    // Commit branch arrival/completion as durable.
                    if (ownedTx is not null) await ownedTx.CommitAsync(innerCt);
                    // Schedule post-commit orphan check (invariant #6: decrement loop OUTSIDE tx).
                    postCommitOrphanCheck = joinNode;
                    return WorkflowActionResult.Advanced;
                }

                // Join fired — append fire log, then mint successor (inside same tx — W5 fix:
                // fire CAS and successor mint are atomic, eliminating the fired-but-no-successor gap).
                await WorkflowEventLogWriter.AppendAsync(
                    Db, instance.ID, instance.TenantCode,
                    EventAction.AutoAdvance,
                    nodeKey: joinNodeKey,
                    actorITCode: null,
                    beforeState: NodeState.Activated.ToString(),
                    afterState: NodeState.CompletedApproved.ToString(),
                    ct: innerCt);

                var joinSuccessorKey = graph.Transitions
                    .FirstOrDefault(t => string.Equals(t.From, joinNodeKey, StringComparison.Ordinal))
                    ?.To;

                if (joinSuccessorKey is null)
                {
                    _logger.LogError(
                        "AdvanceBranchIntoJoinAsync: Join '{JoinKey}' has no outgoing transition. Fail-closed.",
                        joinNodeKey);
                    return WorkflowActionResult.FailClosedRouting;
                }

                var successorDef = graph.Nodes.FirstOrDefault(
                    n => string.Equals(n.NodeKey, joinSuccessorKey, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                           $"Join successor '{joinSuccessorKey}' not found in graph '{graph.Key}'.");

                // WF-19: pass graph.Key so delegation scope filtering works on the minted Join successor.
                // Mint INSIDE tx — atomic with FireJoinIfSatisfiedAsync (W5 fix).
                await MintNodeInstanceAsync(instance, successorDef, innerCt, definitionCode: graph.Key);

                if (ownedTx is not null) await ownedTx.CommitAsync(innerCt);
                return WorkflowActionResult.NodeCompleted;
            }
            catch
            {
                if (ownedTx is not null) await ownedTx.RollbackAsync(CancellationToken.None);
                throw;
            }
            finally
            {
                if (ownedTx is not null) await ownedTx.DisposeAsync();
            }
        }, ct);

        // Post-commit: check for orphaned branches OUTSIDE the tx (design invariant #6).
        if (postCommitOrphanCheck is not null)
            await CheckJoinOrphanAsync(postCommitOrphanCheck, instance, ct);

        return result;
    }

    /// <summary>
    /// Orphan fail-closed backstop (WF-17 §4.4).
    ///
    /// <para>When <c>FireJoinIfSatisfiedAsync</c> returns 0 (quorum not yet met), check
    /// whether all remaining expected arrivals are from dead branches (Superseded or
    /// CompletedRejected but never arrived).  If so, decrement expected and attempt
    /// another fire — eventually making the Join satisfiable with the arrivals that did arrive.</para>
    ///
    /// <para>A branch is "dead non-arriving" when: its NodeInstance is in a terminal
    /// state (CompletedRejected / Superseded) AND it has NOT yet been counted as an
    /// arrival (i.e., the Join's JoinArrivedCount does not include it).  Detecting
    /// this exactly requires the live-cohort query: count Pending/Activated branch tokens
    /// in the same ForkGroup that point to this Join.</para>
    ///
    /// <para>If live branch count + arrived count &gt;= expected, the Join is still satisfiable
    /// and we wait.  If live + arrived &lt; expected, some branches died without arriving →
    /// decrement expected and log a JoinUnsatisfiable warning if it reaches 0.</para>
    /// </summary>
    private async Task CheckJoinOrphanAsync(
        NodeInstance joinNode,
        ProcessInstance instance,
        CancellationToken ct)
    {
        // Re-read for freshest counts.
        joinNode = await Db.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.ID == joinNode.ID, ct);

        if (joinNode.State != NodeState.Activated) return;

        // Count live branch tokens still in flight (Pending or Activated, same generation,
        // JoinNodeKey == this join, not including the join node itself).
        int liveBranches = await Db.Set<NodeInstance>()
            .AsNoTracking()
            .CountAsync(
                n => n.InstanceId == instance.ID
                  && n.Generation == instance.Generation
                  && n.JoinNodeKey == joinNode.NodeKey
                  && (n.State == NodeState.Pending || n.State == NodeState.Activated),
                ct);

        // If liveBranches + arrivedCount >= expectedArrivals, the Join is still satisfiable.
        if (liveBranches + joinNode.JoinArrivedCount >= joinNode.JoinExpectedArrivals)
            return;

        // Dead non-arriving branches detected — decrement expected count.
        int deadNonArriving = joinNode.JoinExpectedArrivals - liveBranches - joinNode.JoinArrivedCount;
        _logger.LogWarning(
            "CheckJoinOrphanAsync: Join '{JoinKey}' (instance {InstanceId}) has {Dead} dead non-arriving " +
            "branch(es). Decrementing JoinExpectedArrivals by {DeadCount} to prevent permanent block.",
            joinNode.NodeKey, instance.ID, deadNonArriving, deadNonArriving);

        for (int i = 0; i < deadNonArriving; i++)
        {
            // Re-read before each decrement to get the latest RowVer.
            joinNode = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .SingleAsync(n => n.ID == joinNode.ID, ct);

            if (joinNode.State != NodeState.Activated) return;

            await GuardedTransition.DecrementJoinExpectedAsync(
                Db, joinNode.ID, joinNode.RowVer, instance.Generation, ct);
        }

        // After decrement(s), re-read and attempt final fire.
        joinNode = await Db.Set<NodeInstance>()
            .AsNoTracking()
            .SingleAsync(n => n.ID == joinNode.ID, ct);

        if (joinNode.State != NodeState.Activated) return;

        if (joinNode.JoinExpectedArrivals <= 0)
        {
            // Join is unsatisfiable — fail-closed.
            _logger.LogError(
                "CheckJoinOrphanAsync: Join '{JoinKey}' for instance {InstanceId} is unsatisfiable " +
                "(JoinExpectedArrivals={Expected}, JoinArrivedCount={Arrived}). Fail-closing Join.",
                joinNode.NodeKey, instance.ID, joinNode.JoinExpectedArrivals, joinNode.JoinArrivedCount);

            await GuardedTransition.CompleteNodeInstanceAsync(
                Db, joinNode.ID, joinNode.RowVer,
                NodeState.CompletedRejected,
                generation: instance.Generation, ct: ct);

            await WorkflowEventLogWriter.AppendAsync(
                Db, instance.ID, instance.TenantCode,
                EventAction.FailClosed,
                nodeKey: joinNode.NodeKey,
                actorITCode: null,
                beforeState: NodeState.Activated.ToString(),
                afterState: NodeState.CompletedRejected.ToString(),
                reason: "Join unsatisfiable: all feeding branches died without arriving.",
                ct: ct);
        }
    }
}
