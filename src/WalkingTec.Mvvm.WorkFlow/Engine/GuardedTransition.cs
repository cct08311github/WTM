#nullable enable
// WF-7: GuardedTransition — the ONE shared CAS helper all engine state changes route through.
// WF-16: Wave-3 — added 回退-to-node methods + Generation epoch guard on existing predicates.
// WF-17: Wave-3 — added Join counting CAS (IncrementJoinArrivedAsync / DecrementJoinExpectedAsync /
//                  FireJoinIfSatisfiedAsync) for parallel/inclusive gateway multi-token marking.
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
//   (e) Race A: span-discard vs in-flight approve (WF-16) → NodeInstance SupersedeNodeAsync
//   (f) Race B: Seq allocation without MAX(Seq)+1 (WF-16) → ProcessInstance AllocateSeqAsync
//   (g) Race C: timer cancel vs timer fire during return (WF-16) → WorkflowTimer
//   (h) Race D: concurrent returns + MaxReturnLoops cap (WF-16) → ProcessInstance BeginReturnAsync
//   (i) T-JOIN: Join arrival counting + single-statement fire (WF-17) → NodeInstance Join CAS
//   (j) R1: 加签-onto-in-flight-会签 threshold-recompute (WF-18) → ApproverSetEpoch co-increment + completion CAS binding

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.WorkFlow.Definition;
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
///
/// <para><strong>Generation epoch (WF-16 Wave-3):</strong> existing
/// Activate/Complete/IncrementApproved/ClaimTask predicates gained an optional
/// <c>generation</c> parameter.  When non-null the guard adds <c>AND Generation == @g</c>
/// so that operations from a stale epoch (gOld) no-op after a 回退 bump.  Callers that
/// do not participate in the epoch (pre-WF-16 paths) pass null and preserve the previous
/// predicate shape byte-for-byte.</para>
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

    // ── WF-16 Wave-3: ProcessInstance return-operation methods ────────────────

    /// <summary>
    /// STEP-1 linearization point for 回退-to-node (WF-16 Race D).
    ///
    /// <para>In a single-row CAS: transitions the instance from <see cref="InstanceState.Running"/>
    /// to <see cref="InstanceState.Returning"/>, increments both <c>Generation</c> and
    /// <c>ReturnLoops</c> atomically, and stamps <c>ReturningLeaseUtc</c>.
    /// Guard includes <c>ReturnLoops &lt; maxReturnLoops</c> so the cap is enforced inside
    /// the same statement — no double-count, no isolation dependency (Race D closed).</para>
    ///
    /// <para>rows == 1 → this caller owns the return epoch (gNew = gOld + 1).</para>
    /// <para>rows == 0 → re-read to disambiguate: ReturnLoops &gt;= cap → fail-closed;
    ///   else another concurrent return already won → AlreadyHandled.</para>
    /// </summary>
    /// <param name="db">DbContext with an active transaction (caller-owned).</param>
    /// <param name="instanceId">PK of the instance.</param>
    /// <param name="expectedRowVer">RowVer read in STEP-0 (stale → CAS fails).</param>
    /// <param name="expectedGeneration">Generation read in STEP-0 (gOld).</param>
    /// <param name="maxReturnLoops">Cap from <c>WorkFlowOptions.MaxReturnLoops</c>.</param>
    /// <param name="leaseExpiry">UTC lease expiry for the Returning sub-state (Wave-5 reaper).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<(int rows, uint newGeneration)> BeginReturnAsync(
        DbContext db,
        Guid instanceId,
        uint expectedRowVer,
        uint expectedGeneration,
        int maxReturnLoops,
        DateTime leaseExpiry,
        CancellationToken ct = default)
    {
        var rows = await db.Set<ProcessInstance>()
            .Where(x => x.ID == instanceId
                         && x.State == InstanceState.Running
                         && x.RowVer == expectedRowVer
                         && x.Generation == expectedGeneration
                         && x.ReturnLoops < (uint)maxReturnLoops)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.State, InstanceState.Returning)
                       .SetProperty(x => x.Generation, x => x.Generation + 1)
                       .SetProperty(x => x.ReturnLoops, x => x.ReturnLoops + 1)
                       .SetProperty(x => x.ReturningLeaseUtc, leaseExpiry)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                ct);

        // Return the new generation inline so callers can avoid an extra re-read.
        // rows==1: winner, newGeneration = expectedGeneration + 1.
        // rows==0: loser, newGeneration = 0 (irrelevant — caller checks rows first).
        return (rows, rows == 1 ? expectedGeneration + 1 : 0u);
    }

    /// <summary>
    /// STEP-4: Supersede a single span <see cref="NodeInstance"/> (WF-16 Race A).
    ///
    /// <para>Flips <c>State → Superseded</c> using the same RowVer an approver uses,
    /// so supersede-vs-approve collapses into the proven single-row CAS contest
    /// (<c>T_PROV_0_SQLite_ConcurrentCAS_ExactlyOneWinner</c>).
    /// State is flipped to terminal <c>Superseded</c> (not a bare marker) so that the
    /// advisory <c>IncrementNodeApprovedCountAsync</c> (<c>WHERE State==Activated</c>)
    /// auto-no-ops after supersession (Race A closed).</para>
    ///
    /// <para>rows == 1 → this node is superseded.</para>
    /// <para>rows == 0 → approver already completed the node; caller detects via
    ///   re-read (CompletedApproved, gen-stale) and handles gracefully.</para>
    /// </summary>
    public static Task<int> SupersedeNodeAsync(
        DbContext db,
        Guid nodeInstanceId,
        uint expectedRowVer,
        uint supersededAtGen,
        CancellationToken ct = default)
    {
        return db.Set<NodeInstance>()
            .Where(x => x.ID == nodeInstanceId
                         && (x.State == NodeState.Activated || x.State == NodeState.Pending)
                         && x.RowVer == expectedRowVer)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.State, NodeState.Superseded)
                       .SetProperty(x => x.SupersededAtGen, supersededAtGen)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                ct);
    }

    /// <summary>
    /// STEP-3: Cancel span <see cref="ApprovalTask"/>s during a 回退-to-node (WF-16).
    ///
    /// <para>Bulk cancel of all tasks on the given node instances that are still in a
    /// cancellable state.  Uses a batched per-row CAS approach — since tasks within a
    /// node-instance span do not contend with each other, a simple bulk update per
    /// node instance is sufficient.  Already-decided tasks are left intact for audit.</para>
    ///
    /// <para>The trigger task is handled separately (Pending → Rejected) by the caller.</para>
    /// </summary>
    /// <param name="db">DbContext with an active transaction.</param>
    /// <param name="spanNodeInstanceIds">IDs of the node instances in the span.</param>
    /// <param name="excludeTaskId">The trigger task — caller handles it separately.</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task<int> DiscardTasksForReturnAsync(
        DbContext db,
        IReadOnlyList<Guid> spanNodeInstanceIds,
        Guid? excludeTaskId,
        CancellationToken ct = default)
    {
        var cancellable = new[]
        {
            TaskState.NotYetActive,
            TaskState.Pending,
            TaskState.Suspended,
            TaskState.AddedPending,
        };

        if (excludeTaskId.HasValue)
        {
            return db.Set<ApprovalTask>()
                .Where(t => spanNodeInstanceIds.Contains(t.NodeInstanceId)
                             && cancellable.Contains(t.State)
                             && t.ID != excludeTaskId.Value)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.State, TaskState.Cancelled)
                           .SetProperty(t => t.RowVer, t => t.RowVer + 1),
                    ct);
        }

        return db.Set<ApprovalTask>()
            .Where(t => spanNodeInstanceIds.Contains(t.NodeInstanceId)
                         && cancellable.Contains(t.State))
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.State, TaskState.Cancelled)
                       .SetProperty(t => t.RowVer, t => t.RowVer + 1),
                ct);
    }

    /// <summary>
    /// STEP-2: Cancel armed <see cref="WorkflowTimer"/>s for span nodes during a 回退-to-node
    /// (WF-16 Race C).
    ///
    /// <para>Per-row CAS on Status.  A poller that already fired a timer gets CAS rows==0
    /// (no-op); its subsequent node action is gen-gated and no-ops (Race C closed).
    /// Supersede-not-delete removes the FK hazard entirely — the NodeInstance row
    /// still exists as Superseded so the FK is never exercised.</para>
    /// </summary>
    /// <param name="db">DbContext with an active transaction.</param>
    /// <param name="spanNodeInstanceIds">IDs of the node instances in the span.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Total rows cancelled (0 is valid when no timers are armed).</returns>
    public static Task<int> CancelTimersForReturnAsync(
        DbContext db,
        IReadOnlyList<Guid> spanNodeInstanceIds,
        CancellationToken ct = default)
    {
        return db.Set<WorkflowTimer>()
            .Where(t => spanNodeInstanceIds.Contains(t.NodeInstanceId)
                         && t.Status == TimerStatus.Armed)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.Status, TimerStatus.Cancelled)
                       .SetProperty(t => t.RowVer, t => t.RowVer + 1),
                ct);
    }

    /// <summary>
    /// STEP-6 counterpart: allocate the next monotonic Seq value for
    /// <see cref="WorkflowEventLog"/> (WF-16 Race B).
    ///
    /// <para>Replaces the old <c>MAX(Seq)+1</c> pattern that required SERIALIZABLE isolation
    /// to avoid duplicates.  The counter rides the instance row's own CAS — single-row,
    /// single-statement, no isolation dependency, exactly the proven spike shape.
    /// Two concurrent callers contend on the instance RowVer; one wins and takes Seq=k,
    /// the other retries and takes Seq=k+1 (contiguous, monotonic, gap-free).</para>
    ///
    /// <para>Returns the <strong>pre-increment</strong> value (the Seq to use for the current
    /// append).  The DB column advances to <c>NextSeq+1</c>.</para>
    ///
    /// <para>Callers MUST be inside the engine-owned transaction (or an ambient one).
    /// If the CAS fails (rows==0), re-read and retry (up to MaxRetries — handled by the
    /// engine's retry envelope).</para>
    /// </summary>
    /// <param name="db">DbContext (caller owns the ambient transaction).</param>
    /// <param name="instanceId">PK of the <see cref="ProcessInstance"/>.</param>
    /// <param name="expectedRowVer">Current RowVer (stale → CAS fails).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// (rows, seq): rows==1 + seq = the allocated Seq; rows==0 means RowVer changed
    /// (concurrent writer) — caller retries with refreshed RowVer.
    /// </returns>
    public static async Task<(int rows, int seq)> AllocateSeqAsync(
        DbContext db,
        Guid instanceId,
        uint expectedRowVer,
        CancellationToken ct = default)
    {
        // Read the current NextSeq BEFORE incrementing — this is the value we'll assign to the log.
        var current = await db.Set<ProcessInstance>()
            .AsNoTracking()
            .Where(x => x.ID == instanceId && x.RowVer == expectedRowVer)
            .Select(x => x.NextSeq)
            .FirstOrDefaultAsync(ct);

        if (current == 0)
        {
            // RowVer mismatch — current==0 means no row matched the predicate.
            return (0, 0);
        }

        // CAS: increment NextSeq, bump RowVer. Guard on same RowVer so two concurrent
        // appends can't both read the same NextSeq.
        var rows = await db.Set<ProcessInstance>()
            .Where(x => x.ID == instanceId && x.RowVer == expectedRowVer)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.NextSeq, x => x.NextSeq + 1)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                ct);

        return (rows, rows == 1 ? current : 0);
    }

    /// <summary>
    /// STEP-5: Mint a new <see cref="NodeInstance"/> for the return target, guarded so that
    /// the insert only proceeds if the instance is still in the <c>Returning</c> state at
    /// the new generation (WF-16 successor-TOCTOU, Race A §3).
    ///
    /// <para>Idempotency is provided by the <c>UNIQUE (TenantCode, InstanceId, NodeKey, Generation)</c>
    /// index — a second attempt for the same (instanceId, nodeKey, generation) tuple silently
    /// no-ops (SQLite/PgSQL: insert-or-ignore; others: PK collision caught before commit).</para>
    ///
    /// <para>The caller MUST re-read the instance after STEP-1 and gate the mint on
    /// <c>State==Returning AND Generation==gNew</c> to close the TOCTOU window.</para>
    /// </summary>
    /// <param name="db">DbContext with an active transaction.</param>
    /// <param name="node">Fully-populated new NodeInstance (ID pre-set by caller, Generation=gNew).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<bool> MintNodeInstanceGuardedAsync(
        DbContext db,
        NodeInstance node,
        CancellationToken ct = default)
    {
        // Check-before-insert: avoid relying solely on the unique-index constraint for
        // idempotency because SQLite treats NULLs as distinct in unique indexes
        // (NULL != NULL), so (null, id, key, gen) does not constrain duplicates on SQLite
        // when TenantCode is null.  The check uses (InstanceId, NodeKey, Generation) which
        // are all non-nullable and sufficient to detect an already-minted node.
        //
        // This is safe within a single-threaded drain loop (the engine processes one
        // operation at a time within a single DbContext scope).  Concurrent callers use
        // separate DbContext instances and continue to rely on the unique-index CAS.
        bool alreadyMinted = await db.Set<NodeInstance>()
            .AsNoTracking()
            .AnyAsync(
                n => n.InstanceId == node.InstanceId
                  && n.NodeKey    == node.NodeKey
                  && n.Generation == node.Generation,
                ct);

        if (alreadyMinted)
            return false;

        // The unique index (TenantCode, InstanceId, NodeKey, Generation) still provides
        // a safety net for concurrent callers on providers where NULL is treated as equal.
        try
        {
            db.Set<NodeInstance>().Add(node);
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message
            .Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message
            .Contains("unique", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message
            .Contains("constraint", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Idempotent: another concurrent caller already minted this node.
            // Detach the conflicting entity so the context stays clean.
            var entry = db.Entry(node);
            if (entry.State != Microsoft.EntityFrameworkCore.EntityState.Detached)
                entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            return false;
        }
    }

    /// <summary>
    /// Convenience overload: builds the <see cref="NodeInstance"/> from a
    /// <see cref="ProcessInstance"/> + <see cref="NodeDef"/> + explicit generation,
    /// then delegates to <see cref="MintNodeInstanceGuardedAsync(DbContext,NodeInstance,CancellationToken)"/>.
    ///
    /// <para>Used by tests and internal helpers that hold a <see cref="ProcessInstance"/>
    /// reference rather than a pre-built <see cref="NodeInstance"/>.</para>
    /// </summary>
    public static Task<bool> MintNodeInstanceGuardedAsync(
        DbContext db,
        ProcessInstance instance,
        NodeDef nodeDef,
        uint generation,
        CancellationToken ct = default)
    {
        var node = new NodeInstance
        {
            ID = Guid.NewGuid(),
            TenantCode = instance.TenantCode,
            InstanceId = instance.ID,
            NodeKey = nodeDef.NodeKey,
            NodeKind = nodeDef.Kind,
            State = NodeState.Pending,
            ApproveMode = nodeDef.ApproveMode,
            ApprovePercent = nodeDef.ApprovePercent,
            RejectGate = nodeDef.RejectGate ?? RejectGate.Immediate,
            RejectPolicy = nodeDef.RejectPolicy ?? RejectPolicy.ReturnToInitiator,
            RowVer = 0,
            Generation = generation,
        };
        return MintNodeInstanceGuardedAsync(db, node, ct);
    }

    /// <summary>
    /// Wave-5 reaper: reclaim an expired <c>Returning</c> lease after a crash
    /// (WF-16 Race D crash addendum).
    ///
    /// <para>Single-row CAS: <c>WHERE State==Returning AND ReturningLeaseUtc &lt; @now AND RowVer==@v</c>
    /// → <c>State=Running, ReturningLeaseUtc=NULL, RowVer+1</c>.  The original (dead)
    /// engine owner's later commit fails its own RowVer guard.</para>
    /// </summary>
    public static Task<int> ReclaimReturningLeaseAsync(
        DbContext db,
        Guid instanceId,
        uint expectedRowVer,
        DateTime now,
        CancellationToken ct = default)
    {
        return db.Set<ProcessInstance>()
            .Where(x => x.ID == instanceId
                         && x.State == InstanceState.Returning
                         && x.RowVer == expectedRowVer
                         && x.ReturningLeaseUtc != null
                         && x.ReturningLeaseUtc < now)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.State, InstanceState.Running)
                       .SetProperty(x => x.ReturningLeaseUtc, (DateTime?)null)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                ct);
    }

    // ── NodeInstance transitions ───────────────────────────────────────────────

    /// <summary>
    /// Attempt to activate a <see cref="NodeInstance"/> from
    /// <see cref="NodeState.Pending"/> → <see cref="NodeState.Activated"/>.
    ///
    /// Also records <paramref name="activatedAt"/> in the same atomic SET.
    ///
    /// <para>WF-16: when <paramref name="generation"/> is non-null, the predicate gains
    /// <c>AND Generation == @g</c> so a stale-epoch pending node auto-no-ops (returns 0)
    /// instead of being incorrectly activated after a 回退 bump.</para>
    /// </summary>
    public static Task<int> ActivateNodeInstanceAsync(
        DbContext db,
        Guid nodeInstanceId,
        uint expectedRowVer,
        DateTime activatedAt,
        uint? generation = null,
        CancellationToken ct = default)
    {
        if (generation.HasValue)
        {
            var gen = generation.Value;
            return db.Set<NodeInstance>()
                .Where(x => x.ID == nodeInstanceId
                             && x.State == NodeState.Pending
                             && x.RowVer == expectedRowVer
                             && x.Generation == gen)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.State, NodeState.Activated)
                           .SetProperty(x => x.ActivatedAt, activatedAt)
                           .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                    ct);
        }

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
    ///
    /// <para>WF-16: when <paramref name="generation"/> is non-null, the predicate gains
    /// <c>AND Generation == @g</c> so a stale-epoch approve no-ops after a 回退 bump.</para>
    ///
    /// <para>WF-18 (FIX-A/B): when <paramref name="expectedApproverSetEpoch"/> is non-null,
    /// the predicate gains <c>AND ApproverSetEpoch == @e</c> so that a concurrent 加签
    /// that bumped the epoch (and therefore changed <c>TotalRequired</c>) invalidates any
    /// in-flight completion that evaluated the threshold against the pre-加签 snapshot.
    /// The deciding approver re-reads the updated threshold and only completes if still met.
    /// Passing null preserves the byte-identical pre-Wave-4 predicate for callers that
    /// do not participate in the epoch (same discipline as the optional <paramref name="generation"/>
    /// parameter introduced in Wave-3).</para>
    /// </summary>
    public static Task<int> CompleteNodeInstanceAsync(
        DbContext db,
        Guid nodeInstanceId,
        uint expectedRowVer,
        NodeState completedState,
        string? decidedBy = null,
        uint? generation = null,
        uint? expectedApproverSetEpoch = null,
        CancellationToken ct = default)
    {
        if (generation.HasValue && expectedApproverSetEpoch.HasValue)
        {
            var gen = generation.Value;
            var epoch = expectedApproverSetEpoch.Value;
            if (decidedBy is not null)
            {
                return db.Set<NodeInstance>()
                    .Where(x => x.ID == nodeInstanceId
                                 && x.State == NodeState.Activated
                                 && x.RowVer == expectedRowVer
                                 && x.Generation == gen
                                 && x.ApproverSetEpoch == epoch)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(x => x.State, completedState)
                               .SetProperty(x => x.DecidedBy, decidedBy)
                               .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                        ct);
            }

            return db.Set<NodeInstance>()
                .Where(x => x.ID == nodeInstanceId
                             && x.State == NodeState.Activated
                             && x.RowVer == expectedRowVer
                             && x.Generation == gen
                             && x.ApproverSetEpoch == epoch)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.State, completedState)
                           .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                    ct);
        }

        if (generation.HasValue)
        {
            var gen = generation.Value;
            if (decidedBy is not null)
            {
                return db.Set<NodeInstance>()
                    .Where(x => x.ID == nodeInstanceId
                                 && x.State == NodeState.Activated
                                 && x.RowVer == expectedRowVer
                                 && x.Generation == gen)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(x => x.State, completedState)
                               .SetProperty(x => x.DecidedBy, decidedBy)
                               .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                        ct);
            }

            return db.Set<NodeInstance>()
                .Where(x => x.ID == nodeInstanceId
                             && x.State == NodeState.Activated
                             && x.RowVer == expectedRowVer
                             && x.Generation == gen)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.State, completedState)
                           .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                    ct);
        }

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
    ///
    /// <para>WF-16: the WHERE clause already guards <c>State == Activated</c>, which means it
    /// auto-no-ops on a <c>Superseded</c> node — the Race A terminal-state flip closes this
    /// without needing an explicit generation guard here.</para>
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

    // ── WF-17: Join counting CAS ──────────────────────────────────────────────

    /// <summary>
    /// Atomically increment <see cref="NodeInstance.JoinArrivedCount"/> on the Join node
    /// to record that one more branch has arrived (WF-17 T-JOIN race).
    ///
    /// <para><strong>Guard predicate:</strong>
    /// <c>WHERE ID==@id AND State==Activated AND Generation==@g AND RowVer==@v</c>.
    /// rows == 1 → this branch arrival is recorded; caller must then call
    /// <see cref="FireJoinIfSatisfiedAsync"/> in the same logical step.
    /// rows == 0 → Join is already closed (CompletedApproved/Superseded) or epoch stale —
    /// treat as <see cref="WorkflowActionResult.AlreadyHandled"/>.</para>
    /// </summary>
    /// <param name="db">DbContext (caller owns the ambient transaction).</param>
    /// <param name="joinNodeInstanceId">PK of the Join <see cref="NodeInstance"/>.</param>
    /// <param name="expectedRowVer">RowVer read before this call; stale → CAS fails (rows==0).</param>
    /// <param name="generation">Current process generation (epoch guard).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>rows-affected: 1 = winner, 0 = loser.</returns>
    public static Task<int> IncrementJoinArrivedAsync(
        DbContext db,
        Guid joinNodeInstanceId,
        uint expectedRowVer,
        uint generation,
        CancellationToken ct = default)
    {
        return db.Set<NodeInstance>()
            .Where(x => x.ID == joinNodeInstanceId
                         && x.State == NodeState.Activated
                         && x.Generation == generation
                         && x.RowVer == expectedRowVer)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.JoinArrivedCount, x => x.JoinArrivedCount + 1)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                ct);
    }

    /// <summary>
    /// Decrement <see cref="NodeInstance.JoinExpectedArrivals"/> when a branch dies without
    /// arriving at its paired Join (orphan fail-closed backstop, WF-17 §4.4).
    ///
    /// <para><strong>Guard predicate:</strong>
    /// <c>WHERE ID==@id AND State==Activated AND Generation==@g
    ///    AND JoinExpectedArrivals &gt; JoinArrivedCount AND RowVer==@v</c>.
    /// The underflow guard (<c>ExpectedArrivals &gt; ArrivedCount</c>) ensures the expected
    /// count never drops below the already-arrived count, keeping the fire condition sound.</para>
    ///
    /// <para>rows == 1 → expected decremented; caller must re-read and check whether
    /// <see cref="FireJoinIfSatisfiedAsync"/> can now complete the Join.
    /// rows == 0 → Join already closed, epoch stale, or underflow would have occurred —
    /// all safe to treat as no-op.</para>
    /// </summary>
    /// <param name="db">DbContext (caller owns the ambient transaction).</param>
    /// <param name="joinNodeInstanceId">PK of the Join <see cref="NodeInstance"/>.</param>
    /// <param name="expectedRowVer">RowVer read before this call; stale → CAS fails (rows==0).</param>
    /// <param name="generation">Current process generation (epoch guard).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>rows-affected: 1 = decremented, 0 = guard rejected (safe no-op).</returns>
    public static Task<int> DecrementJoinExpectedAsync(
        DbContext db,
        Guid joinNodeInstanceId,
        uint expectedRowVer,
        uint generation,
        CancellationToken ct = default)
    {
        return db.Set<NodeInstance>()
            .Where(x => x.ID == joinNodeInstanceId
                         && x.State == NodeState.Activated
                         && x.Generation == generation
                         && x.JoinExpectedArrivals > x.JoinArrivedCount
                         && x.RowVer == expectedRowVer)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.JoinExpectedArrivals, x => x.JoinExpectedArrivals - 1)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                ct);
    }

    /// <summary>
    /// Single-statement conditional CAS to fire the Join node (WF-17 §4.3).
    ///
    /// <para><strong>This is the key double-fire guard.</strong>  The entire "check
    /// <c>ArrivedCount >= ExpectedArrivals</c>" and "flip State" happens inside one
    /// <c>ExecuteUpdateAsync</c> statement.  Exactly one caller wins (rows==1);
    /// every subsequent concurrent caller finds <c>State != Activated</c> and gets rows==0
    /// (<see cref="WorkflowActionResult.AlreadyHandled"/>).</para>
    ///
    /// <para><strong>Guard predicate:</strong>
    /// <c>WHERE ID==@id AND State==Activated AND Generation==@g
    ///    AND JoinArrivedCount &gt;= JoinExpectedArrivals AND RowVer==@v</c>.</para>
    ///
    /// <para>rows == 1 → Join fired; caller mints the successor node(s) and continues the drain loop.
    /// rows == 0 → quorum not yet met, epoch stale, or already fired — caller waits (no-op).</para>
    /// </summary>
    /// <param name="db">DbContext (caller owns the ambient transaction).</param>
    /// <param name="joinNodeInstanceId">PK of the Join <see cref="NodeInstance"/>.</param>
    /// <param name="expectedRowVer">RowVer read before this call; stale → CAS fails (rows==0).</param>
    /// <param name="generation">Current process generation (epoch guard).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>rows-affected: 1 = Join fired (exactly-once), 0 = quorum not met or already handled.</returns>
    public static Task<int> FireJoinIfSatisfiedAsync(
        DbContext db,
        Guid joinNodeInstanceId,
        uint expectedRowVer,
        uint generation,
        CancellationToken ct = default)
    {
        return db.Set<NodeInstance>()
            .Where(x => x.ID == joinNodeInstanceId
                         && x.State == NodeState.Activated
                         && x.Generation == generation
                         && x.JoinExpectedArrivals > 0
                         && x.JoinArrivedCount >= x.JoinExpectedArrivals
                         && x.RowVer == expectedRowVer)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.State, NodeState.CompletedApproved)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                ct);
    }

    // ── WF-18: 加签 ApproverSet mutations ────────────────────────────────────────

    /// <summary>
    /// Atomically expand the approver set for an <see cref="NodeState.Activated"/> node
    /// by <paramref name="delta"/> tasks (WF-18 R1 keystone — FIX-A/B).
    ///
    /// <para><strong>Single-statement CAS:</strong>
    /// <c>WHERE ID==@id AND State==Activated AND Generation==@g
    ///    AND ApproverSetEpoch==@expectedEpoch AND RowVer==@v</c><br/>
    /// <c>SET TotalRequired+=@delta, ApproverSetEpoch+=1, RowVer+=1</c>.</para>
    ///
    /// <para>The TotalRequired bump and the epoch bump are co-atomic: if a concurrent
    /// completion CAS asserts the old epoch it will no-op (rows==0) and the deciding
    /// approver re-reads the updated TotalRequired before retrying.  The epoch also
    /// prevents double-加签 from inflating TotalRequired twice for the same logical
    /// operation (FIX-G: combined with UNIQUE index on NodeInstanceId+AssigneeITCode+Generation).</para>
    ///
    /// <para>rows == 1 → this caller won; proceed to INSERT the k new tasks.
    /// rows == 0 → node already closed or stale epoch — return
    /// <see cref="WorkflowActionResult.NodeAlreadyDecided"/> or
    /// <see cref="WorkflowActionResult.AlreadyHandled"/> accordingly.</para>
    /// </summary>
    /// <param name="db">DbContext (caller owns the explicit transaction).</param>
    /// <param name="nodeInstanceId">PK of the target <see cref="NodeInstance"/>.</param>
    /// <param name="expectedRowVer">RowVer read before this call; stale → CAS fails.</param>
    /// <param name="generation">Current process generation (epoch guard — must match node.Generation).</param>
    /// <param name="expectedApproverSetEpoch">ApproverSetEpoch read before this call; stale → CAS fails.</param>
    /// <param name="delta">Number of new tasks being injected (must be ≥ 1).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>rows-affected: 1 = CAS won, 0 = node closed or epoch stale.</returns>
    public static Task<int> AddApproversToNodeAsync(
        DbContext db,
        Guid nodeInstanceId,
        uint expectedRowVer,
        uint generation,
        uint expectedApproverSetEpoch,
        int delta,
        CancellationToken ct = default)
    {
        return db.Set<NodeInstance>()
            .Where(x => x.ID == nodeInstanceId
                         && x.State == NodeState.Activated
                         && x.Generation == generation
                         && x.ApproverSetEpoch == expectedApproverSetEpoch
                         && x.RowVer == expectedRowVer)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.TotalRequired, x => x.TotalRequired + delta)
                       .SetProperty(x => x.ApproverSetEpoch, x => x.ApproverSetEpoch + 1)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                ct);
    }

    /// <summary>
    /// Bump <see cref="NodeInstance.ApproverSetEpoch"/> (and <see cref="NodeInstance.RowVer"/>)
    /// without changing <c>TotalRequired</c>.
    ///
    /// <para>Used for non-count approver-set mutations that still need to invalidate
    /// in-flight completion CAS — e.g. AtAction revoke, 转办 mid-flight reassign.
    /// The guard predicate is <c>State==Activated AND RowVer==@v</c> so the bump
    /// no-ops if the node is already closed.</para>
    ///
    /// <para>rows == 1 → epoch bumped, any in-flight completion CAS with the old epoch
    /// will now return rows==0.  rows == 0 → node already closed (safe no-op).</para>
    /// </summary>
    /// <param name="db">DbContext (caller owns the explicit transaction).</param>
    /// <param name="nodeInstanceId">PK of the target <see cref="NodeInstance"/>.</param>
    /// <param name="expectedRowVer">RowVer read before this call; stale → CAS fails.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>rows-affected: 1 = bumped, 0 = node closed or RowVer stale.</returns>
    public static Task<int> AdvanceNodeApproverSetEpochAsync(
        DbContext db,
        Guid nodeInstanceId,
        uint expectedRowVer,
        CancellationToken ct = default)
    {
        return db.Set<NodeInstance>()
            .Where(x => x.ID == nodeInstanceId
                         && x.State == NodeState.Activated
                         && x.RowVer == expectedRowVer)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.ApproverSetEpoch, x => x.ApproverSetEpoch + 1)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                ct);
    }

    // ── WF-19: 委托 mid-flight task reassignment ─────────────────────────────────

    /// <summary>
    /// Atomically reassign an existing <see cref="ApprovalTask"/> from the current holder
    /// (<paramref name="principalITCode"/>) to a new delegatee (mid-flight 转办/委托-now;
    /// design §3 path B, FIX-C).
    ///
    /// <para><strong>Single-statement CAS:</strong>
    /// <c>WHERE ID==@taskId AND State==Pending AND RowVer==@v AND Generation==@g</c><br/>
    /// <c>SET AssigneeITCode=@delegatee, DelegatedFromITCode=@principal,
    ///    DelegationRuleId=@ruleId, DelegationExpiresUtc=@ruleEndUtc, RowVer+=1</c>.</para>
    ///
    /// <para><strong>TotalRequired invariant:</strong> this CAS never touches TotalRequired.
    /// It is a 1-for-1 slot transfer — the vote count is structurally unchanged (FIX-C).</para>
    ///
    /// <para>rows == 1 → <paramref name="delegateeITCode"/> now owns the slot; the original
    /// holder (<paramref name="principalITCode"/>) can no longer act on this task.  The caller
    /// must then bump the node's <c>ApproverSetEpoch</c> via
    /// <see cref="AdvanceNodeApproverSetEpochAsync"/> so any in-flight completion CAS
    /// re-evaluates against the updated eligible-actor set.</para>
    ///
    /// <para>rows == 0 → the task is no longer Pending (principal already acted) or the
    /// Generation guard rejected a stale-epoch reassign after a 回退 span-discard.
    /// Treat as <see cref="WorkflowActionResult.AlreadyHandled"/> — the delegation
    /// harmlessly did not apply.</para>
    /// </summary>
    /// <param name="db">DbContext (caller owns the explicit transaction).</param>
    /// <param name="taskId">PK of the <see cref="ApprovalTask"/> being reassigned.</param>
    /// <param name="expectedRowVer">RowVer read before this call; stale → CAS fails.</param>
    /// <param name="generation">Generation of the task's node; stale after 回退 → CAS fails (T-DEL-12).</param>
    /// <param name="delegateeITCode">ITCode of the new assignee.</param>
    /// <param name="principalITCode">ITCode of the original holder; stamped as DelegatedFromITCode.</param>
    /// <param name="delegationRuleId">FK-by-value to the triggering DelegationRule (provenance).</param>
    /// <param name="delegationExpiresUtc">Snapshot of rule EndUtc for AtAction window checks; null = no window.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>rows-affected: 1 = CAS won (slot reassigned), 0 = task not Pending or epoch stale.</returns>
    public static Task<int> ReassignTaskAssigneeAsync(
        DbContext db,
        Guid taskId,
        uint expectedRowVer,
        uint generation,
        string delegateeITCode,
        string principalITCode,
        Guid? delegationRuleId,
        DateTime? delegationExpiresUtc,
        CancellationToken ct = default)
    {
        // FIX-1: add AssigneeITCode==principalITCode to the predicate to close the TOCTOU
        // hijack window: if a concurrent actor already reassigned the slot (bumping RowVer),
        // the fresh RowVer re-read inside the engine tx would otherwise pass the CAS even though
        // the slot now belongs to someone else.  Binding the current owner in the WHERE clause
        // ensures the CAS is a no-op (rows==0) when the assignee has changed.
        return db.Set<ApprovalTask>()
            .Where(x => x.ID == taskId
                         && x.State == TaskState.Pending
                         && x.RowVer == expectedRowVer
                         && x.Generation == generation
                         && x.AssigneeITCode == principalITCode)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.AssigneeITCode, delegateeITCode)
                       .SetProperty(x => x.DelegatedFromITCode, principalITCode)
                       .SetProperty(x => x.DelegationRuleId, delegationRuleId)
                       .SetProperty(x => x.DelegationExpiresUtc, delegationExpiresUtc)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
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
    ///
    /// <para>WF-16: when <paramref name="generation"/> is non-null, adds <c>AND Generation == @g</c>
    /// so a task from a superseded span no-ops instead of being claimed by a late approver.</para>
    /// </summary>
    public static Task<int> ClaimApprovalTaskAsync(
        DbContext db,
        Guid taskId,
        uint expectedRowVer,
        TaskState nextState,
        DateTime actedAtUtc,
        string? comment = null,
        uint? generation = null,
        CancellationToken ct = default)
    {
        if (generation.HasValue)
        {
            var gen = generation.Value;
            return db.Set<ApprovalTask>()
                .Where(x => x.ID == taskId
                             && x.State == TaskState.Pending
                             && x.RowVer == expectedRowVer
                             && x.Generation == gen)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.State, nextState)
                           .SetProperty(x => x.ActedAtUtc, actedAtUtc)
                           .SetProperty(x => x.Comment, comment)
                           .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                    ct);
        }

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

    // ── WF-19 #284.4: ClaimDelegatedTaskAsync (AtAction window check folded into CAS) ──────

    /// <summary>
    /// AtAction variant of <see cref="ClaimApprovalTaskAsync"/> that additionally enforces the
    /// delegation window in the same atomic UPDATE predicate (FIX-D, design §4 R3).
    ///
    /// <para><strong>Predicate:</strong>
    /// <c>WHERE ID==@id AND State==Pending AND RowVer==@v AND Generation==@g
    ///    AND (DelegationExpiresUtc IS NULL OR @now &lt;= DelegationExpiresUtc)</c></para>
    ///
    /// <para><c>@now</c> is app-supplied and bound once (never SQL <c>CURRENT_TIMESTAMP</c>).</para>
    ///
    /// <para>rows==0 is ambiguous: either the task was already handled by a concurrent actor,
    /// or the delegation window expired.  The caller must disambiguate with a follow-up
    /// no-side-effect read:
    /// <list type="bullet">
    ///   <item>State==Pending AND @now &gt; DelegationExpiresUtc → <see cref="WorkflowActionCode.DelegationExpired"/>
    ///     (task intentionally stays Pending; audit shows real reason).</item>
    ///   <item>Otherwise → <see cref="WorkflowActionCode.AlreadyHandled"/> (concurrently acted).</item>
    /// </list></para>
    ///
    /// <para><strong>DO NOT modify the existing <see cref="ClaimApprovalTaskAsync"/> predicate</strong>
    /// — that would break the byte-identical pre-Wave-4 hot path for AtAssignment tasks.</para>
    /// </summary>
    /// <param name="db">DbContext; no explicit transaction required (single-statement CAS).</param>
    /// <param name="taskId">PK of the task to claim.</param>
    /// <param name="expectedRowVer">RowVer read before this call; stale → rows==0.</param>
    /// <param name="nextState">Approved or Rejected.</param>
    /// <param name="actedAtUtc">App-supplied timestamp (bound once; never SQL CURRENT_TIMESTAMP).</param>
    /// <param name="comment">Optional approver comment.</param>
    /// <param name="generation">Process generation (Wave-3 stale-span guard).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>rows-affected: 1 = claimed, 0 = already handled or window expired.</returns>
    public static Task<int> ClaimDelegatedTaskAsync(
        DbContext db,
        Guid taskId,
        uint expectedRowVer,
        TaskState nextState,
        DateTime actedAtUtc,
        string? comment = null,
        uint? generation = null,
        CancellationToken ct = default)
    {
        // FIX-D: window check AND state flip in one statement — no TOCTOU gap.
        // The @now bound is actedAtUtc (caller supplies it once).
        var now = actedAtUtc;

        if (generation.HasValue)
        {
            var gen = generation.Value;
            return db.Set<ApprovalTask>()
                .Where(x => x.ID == taskId
                             && x.State == TaskState.Pending
                             && x.RowVer == expectedRowVer
                             && x.Generation == gen
                             && (x.DelegationExpiresUtc == null || now <= x.DelegationExpiresUtc))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.State, nextState)
                           .SetProperty(x => x.ActedAtUtc, actedAtUtc)
                           .SetProperty(x => x.Comment, comment)
                           .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                    ct);
        }

        return db.Set<ApprovalTask>()
            .Where(x => x.ID == taskId
                         && x.State == TaskState.Pending
                         && x.RowVer == expectedRowVer
                         && (x.DelegationExpiresUtc == null || now <= x.DelegationExpiresUtc))
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.State, nextState)
                       .SetProperty(x => x.ActedAtUtc, actedAtUtc)
                       .SetProperty(x => x.Comment, comment)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                ct);
    }

    // ── WF-19 #284.5: RevokeDelegatedTasksAsync (admin revocation sweep) ─────────

    /// <summary>
    /// Sweep result for a single task row during revocation.
    /// </summary>
    public enum RevokeSingleTaskResult
    {
        /// <summary>CAS succeeded — task reverted to principal.</summary>
        Revoked,
        /// <summary>CAS returned rows==0 — task was no longer Pending or Generation changed.</summary>
        NotPending,
        /// <summary>The task had no DelegationRuleId matching the requested rule (not applicable).</summary>
        NotDelegated,
    }

    /// <summary>
    /// Per-task outcome reported by <see cref="RevokeDelegatedTasksAsync"/>.
    /// </summary>
    public sealed record RevokeDelegatedTaskOutcome(
        Guid TaskId,
        RevokeSingleTaskResult Result);

    /// <summary>
    /// Admin revocation sweep: for every open Pending <see cref="ApprovalTask"/> produced by
    /// <paramref name="delegationRuleId"/>, atomically reverts the slot to the original principal
    /// (1-for-1 slot reassignment — <c>TotalRequired</c> NEVER changes).
    ///
    /// <para><strong>Per-row CAS:</strong>
    /// <c>WHERE ID==@taskId AND State==Pending AND RowVer==@v AND Generation==@g</c>
    /// <c>SET AssigneeITCode=DelegatedFromITCode, DelegatedFromITCode=NULL,
    ///    DelegationRuleId=NULL, DelegationExpiresUtc=NULL, RowVer+=1</c>.
    /// Each row uses its own <c>(RowVer, Generation)</c> snapshot — partial success is valid
    /// and reported per task.</para>
    ///
    /// <para><strong>Idempotent:</strong> rows==0 (already acted or generation changed) is
    /// treated as <see cref="RevokeSingleTaskResult.NotPending"/> — not an error.</para>
    ///
    /// <para><strong>Epoch bump:</strong> every node whose task was successfully revoked has its
    /// <c>ApproverSetEpoch</c> bumped via <see cref="AdvanceNodeApproverSetEpochAsync"/> so any
    /// in-flight completion CAS re-reads the updated eligible-actor set.  The epoch bump is
    /// best-effort: rows==0 there (node completed concurrently) is safe and logged at Debug.</para>
    ///
    /// <para><strong>RBAC:</strong> caller (engine method) is responsible for admin-level
    /// authorization before calling this method.</para>
    ///
    /// <para><strong>Event log:</strong> the engine method appends a
    /// <see cref="Models.EventAction.Delegate"/> row per task inside the same DB session;
    /// this method does NOT write event log rows (single-responsibility).</para>
    /// </summary>
    /// <param name="db">DbContext; caller does NOT need an explicit transaction (per-row CAS).</param>
    /// <param name="delegationRuleId">FK-by-value of the rule whose delegations are being revoked.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Sequence of per-task outcomes; callers should collect and report partial success.</returns>
    public static async IAsyncEnumerable<RevokeDelegatedTaskOutcome> RevokeDelegatedTasksAsync(
        DbContext db,
        Guid delegationRuleId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // 1. Read all active tasks (Pending | NotYetActive | AddedPending) for this rule in one
        //    query (snapshot; no explicit transaction).  NotYetActive = Sequential tasks minted
        //    but not yet reached; AddedPending = tasks added by 加签.  All must be reverted so
        //    that no stale delegated slot fires when the workflow advances.
        //    Each row is then CAS'd individually — partial success is valid.
        var candidates = await db.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.DelegationRuleId == delegationRuleId
                         && (t.State == TaskState.Pending
                             || t.State == TaskState.NotYetActive
                             || t.State == TaskState.AddedPending)
                         && t.IsValid == true)
            .Select(t => new { t.ID, t.RowVer, t.Generation, t.DelegatedFromITCode, t.NodeInstanceId, t.State })
            .ToListAsync(ct);

        foreach (var row in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // Guard: task must have a principal to revert to.
            if (string.IsNullOrWhiteSpace(row.DelegatedFromITCode))
            {
                yield return new RevokeDelegatedTaskOutcome(row.ID, RevokeSingleTaskResult.NotDelegated);
                continue;
            }

            var principal = row.DelegatedFromITCode!;

            // 2. Per-row single-statement CAS: revert slot to principal.
            //    Clears DelegationRuleId + DelegationExpiresUtc (no longer delegated).
            //    DelegatedFromITCode cleared (task is back to a native slot).
            //    State predicate uses the snapshot state (Pending | NotYetActive | AddedPending)
            //    so concurrent activations or completes that changed the state cause rows==0
            //    which is a safe no-op (the task already moved out of scope).
            int rows = await db.Set<ApprovalTask>()
                .Where(t => t.ID == row.ID
                             && t.State == row.State
                             && t.RowVer == row.RowVer
                             && t.Generation == row.Generation)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.AssigneeITCode, principal)
                           .SetProperty(t => t.DelegatedFromITCode, (string?)null)
                           .SetProperty(t => t.DelegationRuleId, (Guid?)null)
                           .SetProperty(t => t.DelegationExpiresUtc, (DateTime?)null)
                           .SetProperty(t => t.RowVer, t => t.RowVer + 1),
                    ct);

            if (rows == 0)
            {
                // Concurrent claim or supersede beat us — task already moved; not an error.
                yield return new RevokeDelegatedTaskOutcome(row.ID, RevokeSingleTaskResult.NotPending);
                continue;
            }

            // 3. Bump the owning node's ApproverSetEpoch so any in-flight completion re-reads.
            //    Re-read node RowVer fresh after the task CAS.
            var nodeSnap = await db.Set<NodeInstance>()
                .AsNoTracking()
                .Where(n => n.ID == row.NodeInstanceId && n.State == NodeState.Activated)
                .Select(n => new { n.ID, n.RowVer })
                .FirstOrDefaultAsync(ct);

            if (nodeSnap is not null)
            {
                // Best-effort epoch bump — rows==0 (node completed concurrently) is safe.
                await AdvanceNodeApproverSetEpochAsync(db, nodeSnap.ID, nodeSnap.RowVer, ct);
            }

            yield return new RevokeDelegatedTaskOutcome(row.ID, RevokeSingleTaskResult.Revoked);
        }
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
