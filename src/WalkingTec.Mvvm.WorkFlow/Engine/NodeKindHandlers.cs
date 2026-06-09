#nullable enable
// WF-6: Built-in INodeKindHandler implementations + NodeKindDispatcher registry.
//
// MVP handlers (non-Approval):
//   StartHandler   — pass-through (no tasks, no CC, no wait)
//   EndHandler     — pass-through (engine advances ProcessInstance to Approved)
//   CcHandler      — writes CcRecord rows, never blocks (spec §5.9)
//   ConditionHandler — stub: takes default/first transition (real routing = WF-11)
//
// Approval stub:
//   ApprovalHandler — returns Blocked; throws NotImplementedException("WF-8/9/10")
//   from OnCompleteAsync since that path should not be reached until the mode handlers land.
//
// NodeKindDispatcher is the singleton registry wired by AddWtmWorkFlow.

using System;
using System.Linq;
using System.Threading.Tasks;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

// ── Start handler ─────────────────────────────────────────────────────────────

/// <summary>
/// Handler for <see cref="NodeKind.Start"/> nodes.
/// Pass-through: no human tasks, no CC, no wait.  Always completable immediately.
/// </summary>
internal sealed class StartHandler : INodeKindHandler
{
    public Task OnEnterAsync(NodeHandlerContext ctx) => Task.CompletedTask;
    public Task<bool> CanCompleteAsync(NodeHandlerContext ctx) => Task.FromResult(true);
    public Task OnCompleteAsync(NodeHandlerContext ctx) => Task.CompletedTask;
}

// ── End handler ───────────────────────────────────────────────────────────────

/// <summary>
/// Handler for <see cref="NodeKind.End"/> nodes.
/// Pass-through: reaching End means the process has been approved.
/// The engine advances the <see cref="ProcessInstance"/> to Approved after OnCompleteAsync.
/// </summary>
internal sealed class EndHandler : INodeKindHandler
{
    public Task OnEnterAsync(NodeHandlerContext ctx) => Task.CompletedTask;
    public Task<bool> CanCompleteAsync(NodeHandlerContext ctx) => Task.FromResult(true);
    public Task OnCompleteAsync(NodeHandlerContext ctx) => Task.CompletedTask;
}

// ── CC handler ────────────────────────────────────────────────────────────────

/// <summary>
/// Handler for <see cref="NodeKind.Cc"/> nodes.
///
/// <para>Structurally cannot block (spec §5.9): writes <see cref="CcRecord"/> rows
/// for each recipient derived from the node's CC rules, then immediately completes.
/// Recipients may mark-read or comment but CANNOT approve or reject.</para>
///
/// <para>MVP: CC recipient resolution from approverRule is stubbed — recipients are
/// inferred from the node's <see cref="Definition.NodeDef.ApproverRule"/> field (type User/Role).
/// Full <c>IApproverResolver</c> integration is WF-8.</para>
/// </summary>
internal sealed class CcHandler : INodeKindHandler
{
    public async Task OnEnterAsync(NodeHandlerContext ctx)
    {
        var nodeDef = ctx.NodeDef;
        var now = DateTime.UtcNow;

        // Resolve recipients from the node's approverRule (MVP stub).
        // Full IApproverResolver integration in WF-8.
        var recipients = ResolveMvpRecipients(nodeDef);

        foreach (var recipientITCode in recipients)
        {
            var record = new CcRecord
            {
                ID = Guid.NewGuid(),
                TenantCode = ctx.TenantCode,
                InstanceId = ctx.ProcessInstance.ID,
                NodeKey = nodeDef.NodeKey,
                RecipientITCode = recipientITCode,
                Trigger = CcTrigger.OnNode,
                SentAtUtc = now,
            };
            ctx.Db.Set<CcRecord>().Add(record);
        }

        if (recipients.Length > 0)
            await ctx.Db.SaveChangesAsync(ctx.CancellationToken);
    }

    public Task<bool> CanCompleteAsync(NodeHandlerContext ctx) => Task.FromResult(true);

    public Task OnCompleteAsync(NodeHandlerContext ctx) => Task.CompletedTask;

    private static string[] ResolveMvpRecipients(Definition.NodeDef nodeDef)
    {
        // MVP: if the node has an approverRule with type=User and a value, use that.
        // Real IApproverResolver integration lands in WF-8. // WF-8
        if (nodeDef.ApproverRule is { Type: "User", Value: { } itCode })
            return new[] { itCode };

        // Inline CC rules on the node (type=User)
        if (nodeDef.Cc is { Count: > 0 } ccRules)
        {
            return ccRules
                .Where(c => c.Rule.Type == "User" && c.Rule.Value is not null)
                .Select(c => c.Rule.Value!)
                .Distinct()
                .ToArray();
        }

        return Array.Empty<string>();
    }
}

// ── Condition handler ─────────────────────────────────────────────────────────

/// <summary>
/// Handler for <see cref="NodeKind.Condition"/> nodes.
///
/// <para>Pass-through: transitions instantly, no human wait.  For MVP, always takes
/// the <c>default</c> transition (real routing evaluator = WF-11). // WF-11</para>
///
/// <para>The engine's <see cref="WorkflowEngine.AdvanceAsync"/> routes via outgoing
/// transitions after OnCompleteAsync; the Condition handler's job is just to record
/// the Skip on non-taken branches and never block.</para>
/// </summary>
internal sealed class ConditionHandler : INodeKindHandler
{
    public Task OnEnterAsync(NodeHandlerContext ctx) => Task.CompletedTask;

    public Task<bool> CanCompleteAsync(NodeHandlerContext ctx) => Task.FromResult(true);

    public Task OnCompleteAsync(NodeHandlerContext ctx) => Task.CompletedTask;
    // WF-11: real WhitelistRoutingEvaluator selects the branch target here.
}

// ── Approval handler (stub — WF-8/9/10) ──────────────────────────────────────

/// <summary>
/// Stub handler for <see cref="NodeKind.Approval"/> nodes.
///
/// <para>Returns <see cref="WorkflowActionResult.Blocked"/> from
/// <see cref="CanCompleteAsync"/> because no human has acted yet.</para>
///
/// <para>The full completion policies (串签 = Sequential, 会签 = All, 或签 = Any) are
/// implemented in WF-8, WF-9, and WF-10.  <see cref="OnCompleteAsync"/> throws
/// <see cref="NotImplementedException"/> to make the seam visible:
/// the engine should never call <c>OnComplete</c> on an Approval node until a real
/// handler replaces this stub.</para>
/// </summary>
internal sealed class ApprovalHandlerStub : INodeKindHandler
{
    public Task OnEnterAsync(NodeHandlerContext ctx)
    {
        // WF-8/9/10: real handler mints ApprovalTask rows here per the ApproveMode policy.
        // Stub: no tasks created — the node remains Activated with no pending tasks.
        return Task.CompletedTask;
    }

    public Task<bool> CanCompleteAsync(NodeHandlerContext ctx)
    {
        // Approval nodes block until human action; the real policy (WF-8/9/10) decides.
        // Stub always returns false → engine leaves the node Activated.
        return Task.FromResult(false);
    }

    public Task OnCompleteAsync(NodeHandlerContext ctx)
    {
        // This path must not be reached while the stub is in place.
        // If it is reached, it means the engine called OnComplete on an Approval node
        // without a real handler — that is a programming error.
        throw new NotImplementedException(
            "ApprovalHandler.OnCompleteAsync is not implemented in this wave. " +
            "Approval node completion policies (串签/会签/或签) are implemented in WF-8/9/10.");
    }
}

// ── Dispatcher registry ───────────────────────────────────────────────────────

/// <summary>
/// Singleton registry mapping <see cref="NodeKind"/> to <see cref="INodeKindHandler"/>.
/// Registered by <see cref="ServiceCollectionExtensions.AddWtmWorkFlow"/>.
/// </summary>
internal sealed class NodeKindDispatcher : INodeKindDispatcher
{
    private static readonly StartHandler     _start     = new();
    private static readonly EndHandler       _end       = new();
    private static readonly CcHandler        _cc        = new();
    private static readonly ConditionHandler _condition = new();
    private static readonly ApprovalHandlerStub _approval = new();

    public INodeKindHandler Resolve(NodeKind kind) => kind switch
    {
        NodeKind.Start     => _start,
        NodeKind.End       => _end,
        NodeKind.Cc        => _cc,
        NodeKind.Condition => _condition,
        NodeKind.Approval  => _approval,
        // WF-16: Ack handler
        // WF-17: Join handler
        _ => throw new InvalidOperationException(
                 $"No INodeKindHandler registered for NodeKind.{kind}. " +
                 $"Ensure the handler is registered in AddWtmWorkFlow."),
    };
}
