#nullable enable
// WF-10: AnyApprovalHandler — 或签 (Any-one) completion policy.
//
// Design:
//   • OnEnterAsync: resolve all approvers via IApproverResolver,
//     mint ALL ApprovalTask rows as Pending (parallel inboxes, all active at once).
//     Sets TotalRequired on NodeInstance.
//
//   • Approve path (spec §5.3 + §7.1(a)):
//     - First approver to claim the TASK wins via GuardedTransition.ClaimApprovalTaskAsync.
//     - Then immediately attempt the node-completion CAS:
//         UPDATE NodeInstance SET State=CompletedApproved, DecidedBy=@me
//           WHERE Id=@id AND State==Activated
//     - If CAS rows==1 → winner; cancel all sibling Pending tasks.
//     - If CAS rows==0 → another approver already won; this is AlreadyHandled (not an error).
//
//   • Reject path (spec §5.3):
//     - A single reject does NOT fail the node; others can still approve.
//     - Node fails only when the LAST pending approver rejects:
//         PendingCount after this reject == 0 AND ApprovedCount == 0
//       In that case: attempt CAS → CompletedRejected.
//     - "Last reject" is computed as: no remaining Pending tasks and no approved task.
//
//   • Concurrent approve race (T-CONC-1):
//     - Two actors both ClaimApprovalTask their own task (each wins their own CAS row=1 for the task).
//     - Both then attempt the node-completion CAS: first wins, second gets rows=0 → AlreadyHandled.
//     - Siblings cancelled exactly once by the winner.

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
/// Completion policy handler for <see cref="ApproveMode.Any"/> (或签) nodes.
///
/// <para><strong>任意审批：</strong> all approvers get a <see cref="TaskState.Pending"/>
/// task simultaneously.  The <em>first</em> approver to approve wins — the node completes
/// via a guarded CAS on <c>NodeInstance.State == Activated</c>; sibling tasks are
/// cancelled.  A concurrent second approver who also completes their task CAS gets
/// <see cref="WorkflowActionResult.AlreadyHandled"/> — never an error.</para>
///
/// <para><strong>Reject:</strong> a single reject does <em>not</em> fail the node;
/// the remaining pending approvers can still approve.  The node fails only when the
/// last pending approver rejects (no approvers left to approve).</para>
/// </summary>
internal sealed class AnyApprovalHandler : INodeKindHandler
{
    private readonly IApproverResolver _resolver;
    private readonly WorkFlowOptions _options;
    private readonly ILogger<AnyApprovalHandler> _logger;
    // #676: clock seam — see NodeKindHandlers.CcHandler for the rationale/pattern.
    private readonly TimeProvider _timeProvider;

    public AnyApprovalHandler(
        IApproverResolver resolver,
        IOptions<WorkFlowOptions> options,
        ILogger<AnyApprovalHandler> logger,
        TimeProvider? timeProvider = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _options  = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger   = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // ── OnEnterAsync ──────────────────────────────────────────────────────────

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
                "AnyApprovalHandler.OnEnterAsync: tasks already minted for node {NodeId}. Skipping.",
                nodeInst.ID);
            return;
        }

        if (nodeDef.ApproverRule is null)
        {
            _logger.LogWarning(
                "AnyApprovalHandler: node '{NodeKey}' has no approverRule. Applying missing-handler policy.",
                nodeDef.NodeKey);
            await ApplyNoApproverPolicyAsync(ctx, "No approverRule defined on node.");
            return;
        }

        var resolution = await _resolver.ResolveAsync(
            db, nodeDef.ApproverRule, nodeInst, instance.InitiatorITCode, ct);

        if (resolution.Outcome != ResolverOutcome.Resolved)
        {
            _logger.LogWarning(
                "AnyApprovalHandler: resolver returned {Outcome} for node '{NodeKey}'. Detail: {Detail}",
                resolution.Outcome, nodeDef.NodeKey, resolution.Detail);
            await ApplyNoApproverPolicyAsync(ctx, resolution.Detail);
            return;
        }

        var approvers = resolution.Approvers;
        var total     = approvers.Count;

        // Write TotalRequired.
        await db.Set<NodeInstance>()
            .Where(n => n.ID == nodeInst.ID)
            .ExecuteUpdateAsync(
                s => s.SetProperty(n => n.TotalRequired, total),
                ct);

        // FIX-4: read delegation provenance from the decorator (if active).
        // Cast is safe — when no decorator is registered the cast returns null and provenance
        // fields stay null (backward-compatible: pre-Wave-4 tasks have no delegation info).
        var delegationCtx = _resolver as IDelegationContextProvider;
        var provenance    = delegationCtx?.LastResolutionProvenance;

        var now   = _timeProvider.GetUtcNow().UtcDateTime;
        var tasks = new List<ApprovalTask>(total);

        for (int i = 0; i < total; i++)
        {
            var assignee = approvers[i];

            // FIX-4 AtAssignment provenance: stamp if this slot was produced by a delegation rule.
            DelegationProvenance? prov = null;
            provenance?.TryGetValue(assignee, out prov);

            tasks.Add(new ApprovalTask
            {
                ID                   = Guid.NewGuid(),
                TenantCode           = instance.TenantCode,
                NodeInstanceId       = nodeInst.ID,
                AssigneeITCode       = assignee,
                State                = TaskState.Pending,
                SequenceOrder        = i,
                RowVer               = 0,
                IsValid              = true,
                // FIX-4: Generation must match nodeInst.Generation so that post-回退 stale CAS
                // (Generation < gNew) is correctly blocked by IX_Wf_ApprovalTask_Node_Assignee_Gen.
                Generation           = nodeInst.Generation,
                // Delegation provenance (null when task is not delegated).
                DelegatedFromITCode  = prov?.OriginalPrincipalITCode != assignee
                                           ? prov?.OriginalPrincipalITCode
                                           : null,
                DelegationRuleId     = prov?.RuleId,
                DelegationExpiresUtc = prov?.RuleEndUtc,
            });
        }

        db.Set<ApprovalTask>().AddRange(tasks);
        await db.SaveChangesAsync(ct);

        _logger.LogDebug(
            "AnyApprovalHandler: minted {Count} parallel tasks for node '{NodeKey}'.",
            total, nodeDef.NodeKey);
    }

    // ── CanCompleteAsync ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when at least one approver has approved
    /// (advisory <see cref="NodeInstance.ApprovedCount"/> &gt;= 1, spec §5.3).
    ///
    /// <para><c>AdvanceCoreAsync</c> performs the authoritative guarded CAS on
    /// <c>NodeInstance.State == Activated</c> after this returns <c>true</c>.
    /// Concurrent winners both see count &gt;= 1 and attempt the node CAS; only one
    /// wins (rows == 1); the loser gets <see cref="WorkflowActionCode.AlreadyHandled"/>.</para>
    /// </summary>
    public async Task<bool> CanCompleteAsync(NodeHandlerContext ctx)
    {
        var db = ctx.Db;
        var ct = ctx.CancellationToken;

        // Re-read for latest advisory count.
        var fresh = await db.Set<NodeInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(n => n.ID == ctx.NodeInstance.ID, ct);

        if (fresh is null) return false;

        return fresh.ApprovedCount >= 1;
    }

    // ── OnCompleteAsync ───────────────────────────────────────────────────────

    /// <summary>
    /// Cancels all remaining <see cref="TaskState.Pending"/> sibling tasks once
    /// the winner has been declared by <c>AdvanceCoreAsync</c>'s CAS.
    /// </summary>
    public async Task OnCompleteAsync(NodeHandlerContext ctx)
    {
        var nodeInst = ctx.NodeInstance;
        var db       = ctx.Db;
        var ct       = ctx.CancellationToken;

        // Cancel remaining Pending tasks (the winner's task was already claimed as Approved).
        await db.Set<ApprovalTask>()
            .Where(t => t.NodeInstanceId == nodeInst.ID && t.State == TaskState.Pending)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.State, TaskState.Cancelled),
                ct);

        _logger.LogDebug(
            "AnyApprovalHandler.OnCompleteAsync: cancelled remaining Pending tasks for node '{NodeKey}' (id={NodeId}).",
            nodeInst.NodeKey, nodeInst.ID);
    }

    // ── Internal: attempt first-approve CAS ──────────────────────────────────

    /// <summary>
    /// Called by <see cref="WorkflowEngine.ApproveTaskAsync"/> for Any-mode nodes after
    /// the task CAS has been won.  Attempts the node-completion CAS.
    ///
    /// <para>Returns <c>true</c> if this caller is the winner (node now CompletedApproved
    /// and sibling tasks cancelled).  Returns <c>false</c> if another concurrent
    /// caller already won (AlreadyHandled).</para>
    /// </summary>
    internal static async Task<bool> TryCompleteApprovedAsync(
        DbContext db,
        NodeInstance freshNode,
        string actorITCode,
        ILogger logger,
        System.Threading.CancellationToken ct)
    {
        var rows = await GuardedTransition.CompleteNodeInstanceAsync(
            db,
            freshNode.ID,
            freshNode.RowVer,
            NodeState.CompletedApproved,
            decidedBy: actorITCode,
            ct: ct);

        if (rows == 1)
        {
            // Cancel all remaining sibling Pending tasks.
            await db.Set<ApprovalTask>()
                .Where(t => t.NodeInstanceId == freshNode.ID && t.State == TaskState.Pending)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.State, TaskState.Cancelled),
                    ct);

            logger.LogInformation(
                "AnyApprovalHandler: node '{NodeKey}' (id={NodeId}) completed as Approved by '{Actor}'.",
                freshNode.NodeKey, freshNode.ID, actorITCode);
            return true;
        }

        // Another concurrent approver already won the CAS — this is the expected loser path.
        logger.LogDebug(
            "AnyApprovalHandler.TryCompleteApproved: node {NodeId} CAS returned 0 — already completed by concurrent actor (AlreadyHandled).",
            freshNode.ID);
        return false;
    }

    /// <summary>
    /// Called by <see cref="WorkflowEngine.RejectTaskAsync"/> for Any-mode nodes after
    /// the task CAS has been won.  Fails the node only when the last pending approver rejects.
    ///
    /// <para>Returns <c>true</c> if this caller is the last-reject winner (node now
    /// CompletedRejected).  Returns <c>false</c> if other approvers are still pending
    /// (node continues).</para>
    /// </summary>
    internal static async Task<bool> TryCompleteRejectedAsync(
        DbContext db,
        NodeInstance freshNode,
        string actorITCode,
        ILogger logger,
        System.Threading.CancellationToken ct)
    {
        // Spec §5.3: node fails only when LAST pending approver rejects.
        // After this task was claimed as Rejected, check remaining Pending tasks.
        int pendingCount = await db.Set<ApprovalTask>()
            .AsNoTracking()
            .CountAsync(t => t.NodeInstanceId == freshNode.ID && t.State == TaskState.Pending, ct);

        // Also check that nobody has approved (approvedCount == 0).
        // Re-read fresh ApprovedCount from the node.
        var currentNode = await db.Set<NodeInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(n => n.ID == freshNode.ID, ct);

        if (currentNode is null) return false;

        // Only fail if no more Pending tasks AND nobody has approved.
        bool isLastReject = pendingCount == 0 && currentNode.ApprovedCount == 0;

        if (!isLastReject)
        {
            logger.LogDebug(
                "AnyApprovalHandler: node {NodeId} reject by '{Actor}' — {Pending} tasks still pending / " +
                "{Approved} already approved. Node continues.",
                freshNode.ID, actorITCode, pendingCount, currentNode.ApprovedCount);
            return false;
        }

        // Last reject: attempt the node-fail CAS.
        var rows = await GuardedTransition.CompleteNodeInstanceAsync(
            db,
            currentNode.ID,
            currentNode.RowVer,
            NodeState.CompletedRejected,
            decidedBy: actorITCode,
            ct: ct);

        if (rows == 1)
        {
            logger.LogInformation(
                "AnyApprovalHandler: node '{NodeKey}' (id={NodeId}) completed as Rejected " +
                "(last pending approver '{Actor}' rejected).",
                freshNode.NodeKey, freshNode.ID, actorITCode);
            return true;
        }

        // Another concurrent actor (another late reject or an approve) already completed the node.
        logger.LogDebug(
            "AnyApprovalHandler.TryCompleteRejected: node {NodeId} last-reject CAS returned 0 — concurrent actor already completed.",
            freshNode.ID);
        return false;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task ApplyNoApproverPolicyAsync(NodeHandlerContext ctx, string? detail)
    {
        var policy   = _options.AutoApproveOnMissingHandler;
        var nodeDef  = ctx.NodeDef;
        var nodeInst = ctx.NodeInstance;
        var db       = ctx.Db;
        var ct       = ctx.CancellationToken;

        switch (policy)
        {
            case AutoApproveOnMissingHandlerPolicy.AutoApprove:
                _logger.LogWarning(
                    "AnyApprovalHandler: AutoApprove on missing handler for node '{NodeKey}'. Detail: {Detail}",
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
                        "AnyApprovalHandler: EscalateToAdmin requested but AdminFallbackITCode is not configured. " +
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
                    // FIX-4: Generation must match nodeInst.Generation so that post-回退 stale CAS
                    // (Generation < gNew) is correctly blocked by IX_Wf_ApprovalTask_Node_Assignee_Gen.
                    Generation     = nodeInst.Generation,
                    // Admin-fallback tasks are never delegated; leave provenance fields null.
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
                    "AnyApprovalHandler: FailClose — no approver for node '{NodeKey}'. Detail: {Detail}",
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
