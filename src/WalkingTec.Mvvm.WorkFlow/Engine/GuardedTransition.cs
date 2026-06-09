#nullable enable
// WF-7: GuardedTransition — the ONE shared CAS helper all engine state changes route through.
//
// Design:
//   • Every state-changing transition in the engine MUST use this helper (spec §7.1 invariant #1).
//   • The guard-in-WHERE ExecuteUpdateAsync pattern: update only fires when the entity is in
//     the expected state AND has the expected RowVer.  Rows-affected == 1 → winner; 0 → loser.
//   • "Loser" is not an error — it's the idempotent no-op result of a concurrent race (spec §7.1).
//   • Generalizes the proven WF-0 / ConcurrencySpikeTests.cs / ConcurrencyConformanceTests.cs
//     patterns from per-entity helpers to a central, reusable service method.
//   • Callers must never call ExecuteUpdateAsync directly for state transitions — always go
//     through GuardedTransition so the invariant is auditable in one place.
//
// Concurrency races covered (spec §7.1):
//   (a) 或签 approver-vs-approver  → NodeInstance level
//   (b) 会签 double-completion (W1) → NodeInstance level (completion CAS)
//   (c) 撤回-vs-final-approve      → ProcessInstance level
//   (d) 超时-vs-human              → ApprovalTask level (Wave 5)

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

/// <summary>
/// Central guarded-CAS helper.  Every engine state transition routes through one of
/// these static methods.  The pattern mirrors <c>TokenService.cs:95-116</c> (the proven
/// WTM CAS primitive) and is validated by the WF-0/WF-3 conformance suite.
///
/// <para><strong>Invariant:</strong> rows-affected == 1 means this caller won the race
/// and must proceed.  rows-affected == 0 means another concurrent actor already completed
/// this transition — the caller must treat it as <see cref="WorkflowActionResult.AlreadyHandled"/>
/// and return without side-effects.  This is never an exception.</para>
///
/// <para><strong>RowVer semantics (spec §7.2):</strong> all WorkFlow entities carry a
/// plain <c>uint RowVer</c> managed inside the WHERE clause of
/// <c>ExecuteUpdateAsync</c> — not as an EF concurrency token.  The winner atomically
/// increments RowVer as part of the same SET clause, making the next update automatically
/// stale for late arrivals.</para>
/// </summary>
public static class GuardedTransition
{
    // ── ProcessInstance transitions ────────────────────────────────────────────

    /// <summary>
    /// Attempt to advance a <see cref="ProcessInstance"/> from <paramref name="expectedState"/>
    /// to <paramref name="nextState"/>.
    ///
    /// Performs a guarded <c>ExecuteUpdateAsync</c> that only fires when
    /// <c>State == expectedState AND RowVer == expectedRowVer</c>.
    /// Returns <c>1</c> for the single winner; <c>0</c> for every loser.
    /// </summary>
    public static Task<int> AdvanceProcessInstanceAsync(
        DbContext db,
        Guid instanceId,
        InstanceState expectedState,
        uint expectedRowVer,
        InstanceState nextState,
        CancellationToken ct = default)
    {
        return db.Set<ProcessInstance>()
            .Where(x => x.ID == instanceId
                         && x.State == expectedState
                         && x.RowVer == expectedRowVer)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.State, nextState)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                ct);
    }

    // ── NodeInstance transitions ───────────────────────────────────────────────

    /// <summary>
    /// Attempt to activate a <see cref="NodeInstance"/> from
    /// <see cref="NodeState.Pending"/> → <see cref="NodeState.Activated"/>.
    ///
    /// Also records <paramref name="activatedAt"/> in the same atomic SET.
    /// </summary>
    public static Task<int> ActivateNodeInstanceAsync(
        DbContext db,
        Guid nodeInstanceId,
        uint expectedRowVer,
        DateTime activatedAt,
        CancellationToken ct = default)
    {
        return db.Set<NodeInstance>()
            .Where(x => x.ID == nodeInstanceId
                         && x.State == NodeState.Pending
                         && x.RowVer == expectedRowVer)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.State, NodeState.Activated)
                       .SetProperty(x => x.ActivatedAt, activatedAt)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                ct);
    }

    /// <summary>
    /// Attempt to complete a <see cref="NodeInstance"/> from
    /// <see cref="NodeState.Activated"/> → <paramref name="completedState"/>.
    ///
    /// This is the core completion-as-CAS (spec §7.4): in 会签 / 或签 scenarios,
    /// multiple concurrent actors may attempt this simultaneously; exactly one wins.
    /// The optional <paramref name="decidedBy"/> captures the winning approver's ITCode.
    /// </summary>
    public static Task<int> CompleteNodeInstanceAsync(
        DbContext db,
        Guid nodeInstanceId,
        uint expectedRowVer,
        NodeState completedState,
        string? decidedBy = null,
        CancellationToken ct = default)
    {
        if (decidedBy is not null)
        {
            return db.Set<NodeInstance>()
                .Where(x => x.ID == nodeInstanceId
                             && x.State == NodeState.Activated
                             && x.RowVer == expectedRowVer)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.State, completedState)
                           .SetProperty(x => x.DecidedBy, decidedBy)
                           .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                    ct);
        }

        return db.Set<NodeInstance>()
            .Where(x => x.ID == nodeInstanceId
                         && x.State == NodeState.Activated
                         && x.RowVer == expectedRowVer)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.State, completedState)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                ct);
    }

    /// <summary>
    /// Atomically increment <see cref="NodeInstance.ApprovedCount"/> or
    /// <see cref="NodeInstance.RejectedCount"/> for an already-<see cref="NodeState.Activated"/>
    /// node (会签 advisory count).
    ///
    /// This does NOT use RowVer because count increments are advisory — the authoritative
    /// completion decision is a separate <see cref="CompleteNodeInstanceAsync"/> CAS (spec §7.4).
    /// The increment is conditional on the node still being Activated so it no-ops after
    /// completion (rows == 0 is safe here).
    /// </summary>
    public static Task<int> IncrementNodeApprovedCountAsync(
        DbContext db,
        Guid nodeInstanceId,
        CancellationToken ct = default)
    {
        return db.Set<NodeInstance>()
            .Where(x => x.ID == nodeInstanceId && x.State == NodeState.Activated)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.ApprovedCount, x => x.ApprovedCount + 1),
                ct);
    }

    /// <summary>
    /// Atomically increment <see cref="NodeInstance.RejectedCount"/> for an
    /// <see cref="NodeState.Activated"/> node (会签 advisory count).
    /// </summary>
    public static Task<int> IncrementNodeRejectedCountAsync(
        DbContext db,
        Guid nodeInstanceId,
        CancellationToken ct = default)
    {
        return db.Set<NodeInstance>()
            .Where(x => x.ID == nodeInstanceId && x.State == NodeState.Activated)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.RejectedCount, x => x.RejectedCount + 1),
                ct);
    }

    // ── ApprovalTask transitions ───────────────────────────────────────────────

    /// <summary>
    /// Attempt to claim an <see cref="ApprovalTask"/> from <see cref="TaskState.Pending"/>
    /// → <paramref name="nextState"/> (typically <see cref="TaskState.Approved"/> or
    /// <see cref="TaskState.Rejected"/>).
    ///
    /// Records <paramref name="actedAtUtc"/> and <paramref name="comment"/> in the same SET.
    /// Winner gets rows == 1; late concurrent claim gets rows == 0.
    /// </summary>
    public static Task<int> ClaimApprovalTaskAsync(
        DbContext db,
        Guid taskId,
        uint expectedRowVer,
        TaskState nextState,
        DateTime actedAtUtc,
        string? comment = null,
        CancellationToken ct = default)
    {
        return db.Set<ApprovalTask>()
            .Where(x => x.ID == taskId
                         && x.State == TaskState.Pending
                         && x.RowVer == expectedRowVer)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.State, nextState)
                       .SetProperty(x => x.ActedAtUtc, actedAtUtc)
                       .SetProperty(x => x.Comment, comment)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                ct);
    }

    // ── Helper: map rows-affected to WorkflowActionResult ─────────────────────

    /// <summary>
    /// Map a raw rows-affected count from <c>ExecuteUpdateAsync</c> to the
    /// standard workflow result codes.
    ///
    /// <para>rows == 1 → <paramref name="successResult"/> (caller won the CAS)</para>
    /// <para>rows == 0 → <see cref="WorkflowActionResult.AlreadyHandled"/> (idempotent loser)</para>
    /// <para>rows &gt; 1 → should never happen with PK predicates; treated as unexpected.</para>
    /// </summary>
    public static WorkflowActionResult MapRows(int rowsAffected, WorkflowActionResult successResult)
    {
        return rowsAffected switch
        {
            1 => successResult,
            0 => WorkflowActionResult.AlreadyHandled,
            _ => WorkflowActionResult.WithDetail(
                     WorkflowActionCode.AlreadyHandled,
                     $"Unexpected rows-affected={rowsAffected}; treated as AlreadyHandled."),
        };
    }
}
