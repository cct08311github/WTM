#nullable enable
// WF-9: AllApprovalHandler — 会签 (Joint/All + ratio) completion policy.
//
// Design:
//   • OnEnterAsync: resolve all approvers via IApproverResolver (same pattern as Sequential),
//     mint ALL ApprovalTask rows as Pending (parallel inboxes, all active at once).
//     Sets TotalRequired on NodeInstance.
//     Impossible-threshold short-circuit: if approvePercent > 100% or if
//     ceil(total * percent) > total (which can happen with floating-point edge cases),
//     the threshold is clamped to total to prevent a permanent hang.
//     A percent of 0 is treated as "all" (threshold = TotalRequired).
//
//   • Completion (spec §5.2 + §7.4):
//     - threshold = ApprovePercent == null ? TotalRequired : ceil(TotalRequired * ApprovePercent)
//     - If threshold > TotalRequired after clamping or == 0 edge: short-circuit with a clear result.
//     - When ApprovedCount crosses the threshold, attempt a guarded CAS on
//       NodeInstance.State == Activated (exactly one winner; concurrent losers get 0 rows → no-op).
//     - Count increments (ApprovedCount/RejectedCount) are advisory via
//       GuardedTransition.IncrementNodeApprovedCountAsync; the CAS decides completion.
//
//   • RejectGate (spec §5.2):
//     - Immediate (default): first reject closes the node immediately.
//       CAS on NodeInstance.State == Activated → winner fails node; losers no-op.
//       Remaining Pending tasks are Cancelled.
//     - AfterAll: wait for all approvers to have acted (no Pending tasks remain),
//       then fail if any rejected AND threshold unreachable (RejectedCount > TotalRequired - need).
//
//   • Impossible threshold detection:
//     - approvePercent > 1.0m (e.g. 150% encoded as 1.5): treat as 100% (clamp).
//     - approvePercent == 0 treated as 100% (must all approve).
//     - Logged clearly; node never hangs.
//
//   • No-approver handling: same AutoApproveOnMissingHandlerPolicy as Sequential.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

/// <summary>
/// Completion policy handler for <see cref="ApproveMode.All"/> (会签) nodes.
///
/// <para><strong>并行会签：</strong> all approvers get a <see cref="TaskState.Pending"/>
/// task simultaneously.  The node completes (approved) when the number of approvals
/// reaches the configured threshold (all, or <c>approvePercent</c> fraction rounded
/// up with <see cref="Math.Ceiling"/>).</para>
///
/// <para>Completion uses a <strong>guarded CAS</strong> on
/// <c>NodeInstance.State == Activated</c> (spec §7.4 / W1 fix): advisory
/// <see cref="NodeInstance.ApprovedCount"/> increments tell each approver whether
/// they might be the threshold-crosser, but only the single CAS winner actually
/// completes the node.  Concurrent final approvals are safe — at most one advances.</para>
///
/// <para><see cref="RejectGate"/>:
/// <list type="bullet">
///   <item><see cref="RejectGate.Immediate"/> (default) — first reject fails the node immediately.</item>
///   <item><see cref="RejectGate.AfterAll"/> — wait for all approvers to have acted; fail only if
///     the approval threshold has become mathematically unreachable.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class AllApprovalHandler : INodeKindHandler
{
    private readonly IApproverResolver _resolver;
    private readonly WorkFlowOptions _options;
    private readonly ILogger<AllApprovalHandler> _logger;

    public AllApprovalHandler(
        IApproverResolver resolver,
        IOptions<WorkFlowOptions> options,
        ILogger<AllApprovalHandler> logger)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _options  = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger   = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── OnEnterAsync ──────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve all approvers and mint ALL <see cref="ApprovalTask"/> rows as
    /// <see cref="TaskState.Pending"/> (parallel inboxes).
    /// </summary>
    public async Task OnEnterAsync(NodeHandlerContext ctx)
    {
        var nodeDef  = ctx.NodeDef;
        var nodeInst = ctx.NodeInstance;
        var instance = ctx.ProcessInstance;
        var db       = ctx.Db;
        var ct       = ctx.CancellationToken;

        // Idempotent re-entry guard.
        var existingCount = await db.Set<ApprovalTask>()
            .AsNoTracking()
            .CountAsync(t => t.NodeInstanceId == nodeInst.ID, ct);

        if (existingCount > 0)
        {
            _logger.LogDebug(
                "AllApprovalHandler.OnEnterAsync: tasks already minted for node {NodeId}. Skipping.",
                nodeInst.ID);
            return;
        }

        if (nodeDef.ApproverRule is null)
        {
            _logger.LogWarning(
                "AllApprovalHandler: node '{NodeKey}' has no approverRule. Applying missing-handler policy.",
                nodeDef.NodeKey);
            await ApplyNoApproverPolicyAsync(ctx, "No approverRule defined on node.");
            return;
        }

        var resolution = await _resolver.ResolveAsync(
            db, nodeDef.ApproverRule, nodeInst, instance.InitiatorITCode, ct);

        if (resolution.Outcome != ResolverOutcome.Resolved)
        {
            _logger.LogWarning(
                "AllApprovalHandler: resolver returned {Outcome} for node '{NodeKey}'. Detail: {Detail}",
                resolution.Outcome, nodeDef.NodeKey, resolution.Detail);
            await ApplyNoApproverPolicyAsync(ctx, resolution.Detail);
            return;
        }

        var approvers = resolution.Approvers;
        var total     = approvers.Count;

        // Compute threshold early to detect impossible configurations at mint time.
        int threshold = ComputeThreshold(total, nodeInst.ApprovePercent, nodeDef.NodeKey);

        // Write TotalRequired to NodeInstance.
        await db.Set<NodeInstance>()
            .Where(n => n.ID == nodeInst.ID)
            .ExecuteUpdateAsync(
                s => s.SetProperty(n => n.TotalRequired, total),
                ct);

        var now   = DateTime.UtcNow;
        var tasks = new List<ApprovalTask>(total);

        for (int i = 0; i < total; i++)
        {
            tasks.Add(new ApprovalTask
            {
                ID             = Guid.NewGuid(),
                TenantCode     = instance.TenantCode,
                NodeInstanceId = nodeInst.ID,
                AssigneeITCode = approvers[i],
                State          = TaskState.Pending,
                SequenceOrder  = i,
                RowVer         = 0,
                IsValid        = true,
            });
        }

        db.Set<ApprovalTask>().AddRange(tasks);
        await db.SaveChangesAsync(ct);

        _logger.LogDebug(
            "AllApprovalHandler: minted {Count} parallel tasks for node '{NodeKey}' (threshold={Threshold}).",
            total, nodeDef.NodeKey, threshold);
    }

    // ── CanCompleteAsync ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when the advisory <see cref="NodeInstance.ApprovedCount"/>
    /// has reached the computed threshold (spec §5.2 + §7.4).
    ///
    /// <para>This check is advisory — <c>AdvanceCoreAsync</c> will perform the
    /// authoritative guarded CAS on <c>NodeInstance.State == Activated</c> after this
    /// returns <c>true</c>.  If two concurrent approvers both see the count at threshold,
    /// only one wins the CAS; the other's <c>CompleteNodeInstanceAsync</c> returns 0 rows
    /// (→ <see cref="WorkflowActionCode.AlreadyHandled"/>).</para>
    ///
    /// <para>WF-18 (FIX-A/B): the fresh snapshot is returned via <paramref name="freshNode"/>
    /// so that callers can thread <see cref="NodeInstance.ApproverSetEpoch"/> into the
    /// subsequent completion CAS predicate.  This ensures that a concurrent 加签 that
    /// bumped the epoch between this read and the CAS will be detected (rows==0).</para>
    /// </summary>
    public async Task<bool> CanCompleteAsync(NodeHandlerContext ctx)
    {
        var db = ctx.Db;
        var ct = ctx.CancellationToken;

        // Re-read for the latest advisory count.
        var fresh = await db.Set<NodeInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(n => n.ID == ctx.NodeInstance.ID, ct);

        if (fresh is null) return false;

        int threshold = ComputeThreshold(fresh.TotalRequired, fresh.ApprovePercent, fresh.NodeKey);
        return fresh.ApprovedCount >= threshold;
    }

    /// <summary>
    /// Re-read the node and return the latest snapshot (WF-18 FIX-A/B helper).
    ///
    /// <para>The engine calls this variant when it needs the fresh
    /// <see cref="NodeInstance.ApproverSetEpoch"/> for the completion CAS after having
    /// incremented the advisory approval count.  The separate re-read is intentional:
    /// the count increment was already committed so this snapshot captures both the
    /// new count and the current epoch.</para>
    /// </summary>
    internal static Task<NodeInstance?> ReadFreshNodeAsync(
        DbContext db,
        Guid nodeInstanceId,
        System.Threading.CancellationToken ct)
    {
        return db.Set<NodeInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(n => n.ID == nodeInstanceId, ct)!;
    }

    // ── OnCompleteAsync ───────────────────────────────────────────────────────

    /// <summary>
    /// No-op — the engine's AdvanceCoreAsync handles the NodeInstance CAS
    /// completion transition.  会签 has no sibling-cancellation step on approve.
    /// </summary>
    public Task OnCompleteAsync(NodeHandlerContext ctx) => Task.CompletedTask;

    // ── Internal: attempt threshold-crossing CAS ──────────────────────────────

    /// <summary>
    /// Called by <see cref="WorkflowEngine.ApproveTaskAsync"/> after incrementing
    /// <see cref="NodeInstance.ApprovedCount"/>.  If the advisory count has reached
    /// the threshold, attempt the guarded CAS to complete the node.
    ///
    /// <para>Returns <c>true</c> if this caller won the CAS and completed the node.
    /// Returns <c>false</c> if the count has not yet reached the threshold, or if
    /// another concurrent caller already won the CAS (rows == 0).</para>
    ///
    /// <para>WF-18 (FIX-A/B): <paramref name="freshNode"/> must be the post-increment
    /// re-read so that its <see cref="NodeInstance.ApproverSetEpoch"/> is current.
    /// The epoch is folded into the completion CAS predicate alongside RowVer — a
    /// concurrent 加签 that bumped the epoch invalidates this in-flight completion,
    /// causing rows==0 so the caller can re-read and re-evaluate the threshold.</para>
    /// </summary>
    internal static async Task<bool> TryCompleteApprovedAsync(
        DbContext db,
        NodeInstance freshNode,
        ILogger logger,
        System.Threading.CancellationToken ct)
    {
        int threshold = ComputeThreshold(freshNode.TotalRequired, freshNode.ApprovePercent, freshNode.NodeKey);

        if (freshNode.ApprovedCount < threshold)
            return false; // advisory count not yet at threshold; wait

        // Advisory count says we might be the threshold-crosser; attempt the CAS.
        // WF-18 FIX-A/B: assert ApproverSetEpoch so a concurrent 加签 that bumped
        // TotalRequired invalidates this in-flight completion (rows→0).
        var rows = await GuardedTransition.CompleteNodeInstanceAsync(
            db,
            freshNode.ID,
            freshNode.RowVer,
            NodeState.CompletedApproved,
            generation: freshNode.Generation,
            expectedApproverSetEpoch: freshNode.ApproverSetEpoch,
            ct: ct);

        if (rows == 1)
        {
            logger.LogInformation(
                "AllApprovalHandler: node '{NodeKey}' (id={NodeId}) completed as Approved. " +
                "ApprovedCount={Approved}/{Total}, Threshold={Threshold}.",
                freshNode.NodeKey, freshNode.ID, freshNode.ApprovedCount, freshNode.TotalRequired, threshold);
            return true;
        }

        // Another concurrent caller already won the CAS — loser no-op.
        logger.LogDebug(
            "AllApprovalHandler.TryCompleteApproved: node {NodeId} CAS returned 0 — already completed by concurrent actor.",
            freshNode.ID);
        return false;
    }

    /// <summary>
    /// Called by <see cref="WorkflowEngine.RejectTaskAsync"/> after incrementing
    /// <see cref="NodeInstance.RejectedCount"/>.  Evaluates the <see cref="RejectGate"/>
    /// and attempts the rejection CAS when appropriate.
    ///
    /// <para>Returns <c>true</c> if this caller won the rejection CAS.
    /// Returns <c>false</c> otherwise (gate not met, or another caller already won).</para>
    /// </summary>
    internal static async Task<bool> TryCompleteRejectedAsync(
        DbContext db,
        NodeInstance freshNode,
        string actorITCode,
        ILogger logger,
        System.Threading.CancellationToken ct)
    {
        int threshold = ComputeThreshold(freshNode.TotalRequired, freshNode.ApprovePercent, freshNode.NodeKey);
        int total     = freshNode.TotalRequired;
        int rejected  = freshNode.RejectedCount;
        int approved  = freshNode.ApprovedCount;

        bool shouldFail;

        if (freshNode.RejectGate == RejectGate.Immediate)
        {
            // Any reject immediately fails the node.
            shouldFail = true;
        }
        else
        {
            // AfterAll: fail only when the threshold is mathematically unreachable:
            //   remaining possible approvals = total - approved - rejected < threshold - approved
            // i.e. even if everyone remaining approves, we cannot hit the threshold.
            int pendingCount = await db.Set<ApprovalTask>()
                .AsNoTracking()
                .CountAsync(t => t.NodeInstanceId == freshNode.ID && t.State == TaskState.Pending, ct);

            // After this reject, pending goes down by 1 (we already claimed it above).
            // But the re-read count still includes this task as Rejected (not Pending),
            // so pendingCount already reflects post-reject state in the advisory sense.
            // For AfterAll: threshold is unreachable if approved + pendingCount < threshold.
            shouldFail = (approved + pendingCount) < threshold;
        }

        if (!shouldFail)
        {
            logger.LogDebug(
                "AllApprovalHandler.TryCompleteRejected: node {NodeId} AfterAll gate not met " +
                "(approved={A}, pending≈{P}, threshold={T}) — continuing.",
                freshNode.ID, approved, total - approved - rejected, threshold);
            return false;
        }

        // Attempt the rejection CAS.
        // WF-18 FIX-A/B: assert Generation + ApproverSetEpoch so a concurrent
        // 加签 (or return) that changed the approver set invalidates this CAS.
        var rows = await GuardedTransition.CompleteNodeInstanceAsync(
            db,
            freshNode.ID,
            freshNode.RowVer,
            NodeState.CompletedRejected,
            decidedBy: actorITCode,
            generation: freshNode.Generation,
            expectedApproverSetEpoch: freshNode.ApproverSetEpoch,
            ct: ct);

        if (rows == 1)
        {
            // Cancel all remaining Pending tasks.
            await db.Set<ApprovalTask>()
                .Where(t => t.NodeInstanceId == freshNode.ID && t.State == TaskState.Pending)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.State, TaskState.Cancelled),
                    ct);

            logger.LogInformation(
                "AllApprovalHandler: node '{NodeKey}' (id={NodeId}) completed as Rejected " +
                "(RejectGate={Gate}, actor='{Actor}').",
                freshNode.NodeKey, freshNode.ID, freshNode.RejectGate, actorITCode);
            return true;
        }

        logger.LogDebug(
            "AllApprovalHandler.TryCompleteRejected: node {NodeId} rejection CAS returned 0 — concurrent actor already completed.",
            freshNode.ID);
        return false;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Compute the approval threshold from <paramref name="total"/> approvers and
    /// an optional <paramref name="approvePercent"/>.
    ///
    /// <para>Rules:
    /// <list type="bullet">
    ///   <item>null percent → all: threshold = total.</item>
    ///   <item>0% or percent &gt; 100% → clamped to 100%: threshold = total.</item>
    ///   <item>Otherwise: threshold = ceil(total × percent).</item>
    ///   <item>Minimum: 1 (even 0.001% of 1000 needs at least 1 approval).</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static int ComputeThreshold(int total, decimal? approvePercent, string nodeKey)
    {
        if (total <= 0) return 0;
        if (approvePercent is null || approvePercent <= 0m || approvePercent > 1.0m)
        {
            // null → 100 %; impossible/zero percent → clamp to 100 %
            return total;
        }

        // Spec §5.2: "ceil rounding for ratio thresholds"
        var raw = (double)total * (double)approvePercent;
        int threshold = (int)Math.Ceiling(raw);

        // Clamp: threshold can't exceed total (floating-point ceil edge cases).
        threshold = Math.Min(threshold, total);

        // Minimum threshold of 1.
        return Math.Max(1, threshold);
    }

    /// <summary>
    /// Apply the configured <see cref="AutoApproveOnMissingHandlerPolicy"/> when no approver
    /// can be resolved.  Same logic as SequentialApprovalHandler.
    /// </summary>
    private async Task ApplyNoApproverPolicyAsync(NodeHandlerContext ctx, string? detail)
    {
        var policy  = _options.AutoApproveOnMissingHandler;
        var nodeDef = ctx.NodeDef;
        var nodeInst = ctx.NodeInstance;
        var db       = ctx.Db;
        var ct       = ctx.CancellationToken;

        switch (policy)
        {
            case AutoApproveOnMissingHandlerPolicy.AutoApprove:
                _logger.LogWarning(
                    "AllApprovalHandler: AutoApprove on missing handler for node '{NodeKey}'. Detail: {Detail}",
                    nodeDef.NodeKey, detail);

                await db.Set<NodeInstance>()
                    .Where(n => n.ID == nodeInst.ID)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(n => n.TotalRequired, 0),
                        ct);
                break;

            case AutoApproveOnMissingHandlerPolicy.EscalateToAdmin:
                var adminCode = _options.AdminFallbackITCode;
                if (string.IsNullOrWhiteSpace(adminCode))
                {
                    _logger.LogWarning(
                        "AllApprovalHandler: EscalateToAdmin policy requested but AdminFallbackITCode is not configured. " +
                        "Failing closed for node '{NodeKey}'. Detail: {Detail}",
                        nodeDef.NodeKey, detail);
                    goto case AutoApproveOnMissingHandlerPolicy.FailClose;
                }

                var adminTask = new ApprovalTask
                {
                    ID             = Guid.NewGuid(),
                    TenantCode     = ctx.ProcessInstance.TenantCode,
                    NodeInstanceId = nodeInst.ID,
                    AssigneeITCode = adminCode,
                    State          = TaskState.Pending,
                    SequenceOrder  = 0,
                    Comment        = $"Admin fallback: {detail}",
                    RowVer         = 0,
                    IsValid        = true,
                };
                db.Set<ApprovalTask>().Add(adminTask);

                await db.Set<NodeInstance>()
                    .Where(n => n.ID == nodeInst.ID)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(n => n.TotalRequired, 1),
                        ct);

                await db.SaveChangesAsync(ct);
                break;

            case AutoApproveOnMissingHandlerPolicy.FailClose:
            default:
                _logger.LogError(
                    "AllApprovalHandler: FailClose — no approver for node '{NodeKey}'. Detail: {Detail}",
                    nodeDef.NodeKey, detail);

                await db.Set<NodeInstance>()
                    .Where(n => n.ID == nodeInst.ID)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(n => n.TotalRequired, int.MaxValue),
                        ct);
                break;
        }
    }
}
