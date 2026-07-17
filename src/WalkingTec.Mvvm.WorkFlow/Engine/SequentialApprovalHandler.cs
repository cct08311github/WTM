#nullable enable
// WF-8: SequentialApprovalHandler — 串签 (Sequential) completion policy.
//
// Design:
//   • OnEnterAsync: resolve approvers via IApproverResolver; mint ALL ApprovalTask rows
//     upfront (tasks for steps > 0 get State=NotYetActive).  This avoids a second resolver
//     call per step and makes the full approver list visible for audit.
//     Apply InitiatorAutoApprove: if an upfront task's AssigneeITCode == initiator AND the
//     option is true, set that task to AutoApproved.  If such tasks form the LEADING
//     contiguous run starting at the pointer (e.g. approvers[0] is the initiator), the
//     pointer is advanced past that run and the first non-auto-approved step is promoted
//     to Pending here (#529) — otherwise the node would have zero Pending tasks and be
//     stranded forever.  Mid-chain auto-approved steps (not part of the leading run) keep
//     their AutoApproved state and are skipped later by WorkflowEngine's #361 loop once a
//     human approves the pointer task ahead of them.
//   • CanCompleteAsync: returns true only when ALL tasks are terminal
//     (Approved/AutoApproved/Rejected/Cancelled) i.e. the pointer has passed the last step.
//     ALSO returns true immediately when the node is already in a terminal state (re-entry guard).
//   • OnCompleteAsync: no-op for Sequential — the engine's AdvanceCoreAsync handles the
//     NodeInstance CAS completion.
//   • Sequential pointer advancement on Approve/Reject is handled externally via
//     WorkflowEngine.ApproveTaskAsync / RejectTaskAsync, not inside these handler methods.
//     The handler's role is: OnEnter (mint tasks), CanComplete (gate), OnComplete (cleanup).
//
// InitiatorAutoApprove (spec §9 / WorkFlowOptions):
//   Default false.  When true, if the initiator resolves as an approver on any step,
//   that step is auto-approved at OnEnterAsync time.  Consecutive same-person steps
//   are still deduped by IApproverResolver before reaching here.
//
// No-approver handling: when IApproverResolver returns NoApprover, apply
//   AutoApproveOnMissingHandlerPolicy from WorkFlowOptions.

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
/// Completion policy handler for <see cref="ApproveMode.Sequential"/> (串签) nodes.
///
/// <para><strong>逐级审批：</strong> approvers act one at a time in authored order.
/// The <see cref="NodeInstance.SequencePointer"/> tracks which step is currently
/// active.  Only the task at the current pointer is <see cref="TaskState.Pending"/>;
/// all others are <see cref="TaskState.NotYetActive"/>.</para>
///
/// <para>Approver list is resolved via <see cref="IApproverResolver"/> at
/// <see cref="OnEnterAsync"/> time (lazy per node activation, per spec §5.1).</para>
/// </summary>
internal sealed class SequentialApprovalHandler : INodeKindHandler
{
    private readonly IApproverResolver _resolver;
    private readonly WorkFlowOptions _options;
    private readonly ILogger<SequentialApprovalHandler> _logger;
    // #676: clock seam — see NodeKindHandlers.CcHandler for the rationale/pattern.
    private readonly TimeProvider _timeProvider;

    public SequentialApprovalHandler(
        IApproverResolver resolver,
        IOptions<WorkFlowOptions> options,
        ILogger<SequentialApprovalHandler> logger,
        TimeProvider? timeProvider = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // ── OnEnterAsync ──────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve approvers and mint <see cref="ApprovalTask"/> rows.
    ///
    /// <list type="bullet">
    ///   <item>Task at SequenceOrder == SequencePointer (0) → <see cref="TaskState.Pending"/>.</item>
    ///   <item>All subsequent tasks → <see cref="TaskState.NotYetActive"/>.</item>
    ///   <item>If no approver resolved → apply <see cref="AutoApproveOnMissingHandlerPolicy"/>.</item>
    ///   <item>InitiatorAutoApprove: auto-approve any step whose assignee == initiator
    ///         when <see cref="WorkFlowOptions.InitiatorAutoApprove"/> is true.</item>
    /// </list>
    /// </summary>
    public async Task OnEnterAsync(NodeHandlerContext ctx)
    {
        var nodeDef = ctx.NodeDef;
        var nodeInst = ctx.NodeInstance;
        var instance = ctx.ProcessInstance;
        var db = ctx.Db;
        var ct = ctx.CancellationToken;

        // Guard: if tasks already exist for this node (idempotent re-entry), skip minting.
        var existingCount = await db.Set<ApprovalTask>()
            .AsNoTracking()
            .CountAsync(t => t.NodeInstanceId == nodeInst.ID, ct);

        if (existingCount > 0)
        {
            _logger.LogDebug(
                "SequentialApprovalHandler.OnEnterAsync: tasks already minted for node {NodeId}. Skipping.",
                nodeInst.ID);
            return;
        }

        // Rule must exist — validated at publish time.
        if (nodeDef.ApproverRule is null)
        {
            _logger.LogWarning(
                "SequentialApprovalHandler: node '{NodeKey}' has no approverRule. Applying missing-handler policy.",
                nodeDef.NodeKey);
            await ApplyNoApproverPolicyAsync(ctx, "No approverRule defined on node.");
            return;
        }

        // Resolve approvers lazily.
        var resolution = await _resolver.ResolveAsync(
            db, nodeDef.ApproverRule, nodeInst, instance.InitiatorITCode, ct);

        if (resolution.Outcome != ResolverOutcome.Resolved)
        {
            _logger.LogWarning(
                "SequentialApprovalHandler: resolver returned {Outcome} for node '{NodeKey}'. Detail: {Detail}",
                resolution.Outcome, nodeDef.NodeKey, resolution.Detail);
            await ApplyNoApproverPolicyAsync(ctx, resolution.Detail);
            return;
        }

        var approvers = resolution.Approvers;
        var pointer = nodeInst.SequencePointer; // typically 0 on fresh activation
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // WF-19: read delegation provenance from the decorator (if active).
        var delegationCtx = _resolver as IDelegationContextProvider;
        var provenance    = delegationCtx?.LastResolutionProvenance;

        // #529: pre-compute InitiatorAutoApprove flags for every step, then advance the
        // pointer past the leading CONTIGUOUS run of auto-approved steps starting at
        // `pointer`. Without this, a leading auto-approved step (e.g. approvers =
        // [initiator, human2, human3]) mints as a terminal AutoApproved task while the
        // pointer stays at 0 — there is never a Pending task and no timeout timer gets
        // armed, permanently stranding the node. Mid-chain auto-approved steps (i.e. not
        // part of the leading run) are unaffected here; those are skipped by the #361
        // loop in WorkflowEngine once a human approves the pointer task ahead of them.
        var autoApproveFlags = new bool[approvers.Count];
        for (int i = 0; i < approvers.Count; i++)
        {
            autoApproveFlags[i] = string.Equals(approvers[i], instance.InitiatorITCode,
                                                 StringComparison.OrdinalIgnoreCase)
                                   && _options.InitiatorAutoApprove;
        }

        var advancedPointer = pointer;
        while (advancedPointer < approvers.Count && autoApproveFlags[advancedPointer])
        {
            advancedPointer++;
        }

        var tasks = new List<ApprovalTask>(approvers.Count);

        for (int i = 0; i < approvers.Count; i++)
        {
            var assignee = approvers[i];

            // InitiatorAutoApprove: skip the step by marking it AutoApproved immediately.
            bool autoApprove = autoApproveFlags[i];

            var state = autoApprove
                ? TaskState.AutoApproved
                : (i == advancedPointer ? TaskState.Pending : TaskState.NotYetActive);

            // WF-19 AtAssignment provenance: stamp if this slot was produced by a delegation rule.
            DelegationProvenance? prov = null;
            provenance?.TryGetValue(assignee, out prov);

            var task = new ApprovalTask
            {
                ID                   = Guid.NewGuid(),
                TenantCode           = instance.TenantCode,
                NodeInstanceId       = nodeInst.ID,
                AssigneeITCode       = assignee,
                State                = state,
                SequenceOrder        = i,
                ActedAtUtc           = autoApprove ? now : null,
                Comment              = autoApprove ? "Auto-approved: initiator is the approver." : null,
                RowVer               = 0,
                IsValid              = true,
                Generation           = nodeInst.Generation,
                // Delegation provenance (null when task is not delegated).
                DelegatedFromITCode  = prov?.OriginalPrincipalITCode != assignee
                                           ? prov?.OriginalPrincipalITCode
                                           : null,
                DelegationRuleId     = prov?.RuleId,
                DelegationExpiresUtc = prov?.RuleEndUtc,
            };
            tasks.Add(task);

            if (autoApprove)
            {
                _logger.LogInformation(
                    "SequentialApprovalHandler: step {Order} auto-approved (InitiatorAutoApprove=true) for node '{NodeKey}', assignee '{ITCode}'.",
                    i, nodeDef.NodeKey, assignee);
            }
        }

        db.Set<ApprovalTask>().AddRange(tasks);

        // Write TotalRequired and the advanced SequencePointer to the NodeInstance in one
        // CAS-style update to avoid re-reading. When `advancedPointer == pointer` (no
        // leading auto-approved run, the common case) this is a harmless no-op write.
        // When the leading run consumes ALL steps, advancedPointer == approvers.Count,
        // which is exactly the "beyond the last step" value CanCompleteAsync expects.
        await db.Set<NodeInstance>()
            .Where(n => n.ID == nodeInst.ID)
            .ExecuteUpdateAsync(
                s => s.SetProperty(n => n.TotalRequired, approvers.Count)
                       .SetProperty(n => n.SequencePointer, advancedPointer),
                ct);

        await db.SaveChangesAsync(ct);

        if (advancedPointer >= approvers.Count)
        {
            _logger.LogInformation(
                "SequentialApprovalHandler: all {Count} steps auto-approved for node '{NodeKey}'.",
                tasks.Count, nodeDef.NodeKey);
        }
        else if (advancedPointer > pointer)
        {
            _logger.LogInformation(
                "SequentialApprovalHandler: advanced pointer from {OldPointer} to {NewPointer} past leading " +
                "auto-approved steps for node '{NodeKey}'; step {NewPointer} is now Pending.",
                pointer, advancedPointer, nodeDef.NodeKey, advancedPointer);
        }
    }

    // ── CanCompleteAsync ──────────────────────────────────────────────────────

    /// <summary>
    /// The node can complete when the Sequential pointer has passed the last step
    /// (i.e., all approvers have acted) AND the node is not rejected.
    /// </summary>
    public async Task<bool> CanCompleteAsync(NodeHandlerContext ctx)
    {
        var nodeInst = ctx.NodeInstance;
        var db = ctx.Db;
        var ct = ctx.CancellationToken;

        // Re-read for fresh state.
        var freshNode = await db.Set<NodeInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(n => n.ID == nodeInst.ID, ct);

        if (freshNode is null) return false;

        // Already rejected → completion will be handled as CompletedRejected by caller.
        if (freshNode.State == NodeState.CompletedRejected)
            return true;

        // Already approved (e.g. via concurrent call).
        if (freshNode.State == NodeState.CompletedApproved)
            return true;

        // Check pointer vs total required.
        var totalRequired = freshNode.TotalRequired;
        if (totalRequired == 0)
        {
            // No tasks were minted — shouldn't happen after OnEnter, but treat as completable.
            return true;
        }

        var pointer = freshNode.SequencePointer;

        // Can complete when pointer has moved past the last index.
        return pointer >= totalRequired;
    }

    // ── OnCompleteAsync ───────────────────────────────────────────────────────

    /// <summary>
    /// No-op for Sequential — the engine's AdvanceCoreAsync handles the NodeInstance
    /// CAS completion transition.  Any cleanup needed before routing onward is handled
    /// in <see cref="WorkflowEngine.ApproveTaskAsync"/> / <see cref="WorkflowEngine.RejectTaskAsync"/>.
    /// </summary>
    public Task OnCompleteAsync(NodeHandlerContext ctx) => Task.CompletedTask;

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Apply the configured <see cref="AutoApproveOnMissingHandlerPolicy"/> when no approver
    /// could be resolved.  This prevents a silent deadlock.
    /// </summary>
    private async Task ApplyNoApproverPolicyAsync(NodeHandlerContext ctx, string? detail)
    {
        var policy = _options.AutoApproveOnMissingHandler;
        var nodeDef = ctx.NodeDef;
        var nodeInst = ctx.NodeInstance;
        var db = ctx.Db;
        var ct = ctx.CancellationToken;

        switch (policy)
        {
            case AutoApproveOnMissingHandlerPolicy.AutoApprove:
                _logger.LogWarning(
                    "SequentialApprovalHandler: AutoApprove on missing handler for node '{NodeKey}'. Detail: {Detail}",
                    nodeDef.NodeKey, detail);

                // Advance pointer to totalRequired=0 so CanCompleteAsync returns true.
                // No tasks minted; the node completes as if all approved.
                await db.Set<NodeInstance>()
                    .Where(n => n.ID == nodeInst.ID)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(n => n.TotalRequired, 0)
                               .SetProperty(n => n.SequencePointer, 0),
                        ct);
                break;

            case AutoApproveOnMissingHandlerPolicy.EscalateToAdmin:
                var adminCode = _options.AdminFallbackITCode;
                if (string.IsNullOrWhiteSpace(adminCode))
                {
                    // EscalateToAdmin was requested but no AdminFallbackITCode is configured.
                    // Safety red-line: NEVER fall through to AutoApprove — fail closed instead.
                    _logger.LogWarning(
                        "SequentialApprovalHandler: EscalateToAdmin policy requested but AdminFallbackITCode " +
                        "is not configured. Failing closed (not auto-approving) for node '{NodeKey}'. " +
                        "Detail: {Detail}. Set WorkFlowOptions.AdminFallbackITCode to enable escalation.",
                        nodeDef.NodeKey, detail);
                    goto case AutoApproveOnMissingHandlerPolicy.FailClose;
                }

                _logger.LogWarning(
                    "SequentialApprovalHandler: Escalating to admin '{Admin}' for node '{NodeKey}'. Detail: {Detail}",
                    adminCode, nodeDef.NodeKey, detail);

                var adminTask = new ApprovalTask
                {
                    ID = Guid.NewGuid(),
                    TenantCode = ctx.ProcessInstance.TenantCode,
                    NodeInstanceId = nodeInst.ID,
                    AssigneeITCode = adminCode,
                    State = TaskState.Pending,
                    SequenceOrder = 0,
                    Comment = $"Admin fallback: {detail}",
                    RowVer = 0,
                    IsValid = true,
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
                _logger.LogError(
                    "SequentialApprovalHandler: FailClose policy — no approver for node '{NodeKey}'. " +
                    "Instance will be left in a fail-closed state. Detail: {Detail}",
                    nodeDef.NodeKey, detail);

                // Leave TotalRequired=0 but SequencePointer=-1 as sentinel to signal fail-closed.
                // WorkflowEngine.ApproveTaskAsync / AdvanceCoreAsync will detect this.
                // CanCompleteAsync will NOT return true for this case (pointer=-1 != totalRequired=0
                // because we check pointer >= totalRequired, and -1 < 0 is true for uint but we
                // use int — set to a sentinel value the caller checks).
                // Simpler: set TotalRequired to a very high sentinel so the node never completes.
                // The engine will need to detect this from an AdminFallback result.
                // For the MVP: treat FailClose as setting TotalRequired=int.MaxValue (never completes).
                await db.Set<NodeInstance>()
                    .Where(n => n.ID == nodeInst.ID)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(n => n.TotalRequired, int.MaxValue),
                        ct);
                break;
        }
    }
}
