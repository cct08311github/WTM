#nullable enable
// WF-6/WF-8: Built-in INodeKindHandler implementations + NodeKindDispatcher registry.
//
// MVP handlers (non-Approval):
//   StartHandler     — pass-through (no tasks, no CC, no wait)
//   EndHandler       — pass-through (engine advances ProcessInstance to Approved)
//   CcHandler        — writes CcRecord rows, never blocks (spec §5.9)
//   ConditionHandler — stub: takes default/first transition (real routing = WF-11)
//
// Approval handler:
//   ApprovalHandler  — dispatches to mode-specific sub-handler:
//                      Sequential (WF-8) → SequentialApprovalHandler
//                      All (WF-9)        → stub (NotImplementedException)
//                      Any (WF-10)       → stub (NotImplementedException)
//
// NodeKindDispatcher is the singleton registry wired by AddWtmWorkFlow.
// SequentialApprovalHandler is injected via DI so it has access to
// IApproverResolver, WorkFlowOptions, and ILogger.

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

// ── Approval handler — dispatches by ApproveMode ─────────────────────────────

/// <summary>
/// Top-level handler for <see cref="NodeKind.Approval"/> nodes.
///
/// <para>Dispatches to the appropriate mode sub-handler based on
/// <see cref="NodeInstance.ApproveMode"/>:
/// <list type="bullet">
///   <item><see cref="ApproveMode.Sequential"/> → <see cref="SequentialApprovalHandler"/> (WF-8)</item>
///   <item><see cref="ApproveMode.All"/> → stub returning Blocked (WF-9)</item>
///   <item><see cref="ApproveMode.Any"/> → stub returning Blocked (WF-10)</item>
/// </list>
/// </para>
/// </summary>
internal sealed class ApprovalHandler : INodeKindHandler
{
    private readonly SequentialApprovalHandler _sequential;

    public ApprovalHandler(SequentialApprovalHandler sequential)
    {
        _sequential = sequential ?? throw new ArgumentNullException(nameof(sequential));
    }

    public Task OnEnterAsync(NodeHandlerContext ctx)
    {
        return ResolveMode(ctx).OnEnterAsync(ctx);
    }

    public Task<bool> CanCompleteAsync(NodeHandlerContext ctx)
    {
        return ResolveMode(ctx).CanCompleteAsync(ctx);
    }

    public Task OnCompleteAsync(NodeHandlerContext ctx)
    {
        return ResolveMode(ctx).OnCompleteAsync(ctx);
    }

    private INodeKindHandler ResolveMode(NodeHandlerContext ctx)
    {
        var mode = ctx.NodeInstance.ApproveMode;
        return mode switch
        {
            ApproveMode.Sequential => _sequential,
            ApproveMode.All        => _allStub,
            ApproveMode.Any        => _anyStub,
            null                   => _sequential, // default to Sequential if not set
            _ => throw new InvalidOperationException(
                     $"Unknown ApproveMode '{mode}' for node '{ctx.NodeInstance.NodeKey}'."),
        };
    }

    // Stubs for WF-9/WF-10 modes — return Blocked; OnCompleteAsync throws.
    private static readonly ApprovalModeStub _allStub = new("All", "WF-9");
    private static readonly ApprovalModeStub _anyStub = new("Any", "WF-10");
}

/// <summary>
/// Stub for a deferred approval mode (WF-9: All, WF-10: Any).
/// Returns Blocked; throws if OnComplete is reached (programming error).
/// </summary>
internal sealed class ApprovalModeStub : INodeKindHandler
{
    private readonly string _modeName;
    private readonly string _waveTag;

    public ApprovalModeStub(string modeName, string waveTag)
    {
        _modeName = modeName;
        _waveTag = waveTag;
    }

    public Task OnEnterAsync(NodeHandlerContext ctx) => Task.CompletedTask;

    public Task<bool> CanCompleteAsync(NodeHandlerContext ctx) => Task.FromResult(false);

    public Task OnCompleteAsync(NodeHandlerContext ctx) =>
        throw new NotImplementedException(
            $"Approval mode '{_modeName}' is not yet implemented. " +
            $"It will be added in {_waveTag}.");
}

// ── Dispatcher registry ───────────────────────────────────────────────────────

/// <summary>
/// Registry mapping <see cref="NodeKind"/> to <see cref="INodeKindHandler"/>.
/// Registered by <see cref="ServiceCollectionExtensions.AddWtmWorkFlow"/>.
///
/// <para>The <see cref="ApprovalHandler"/> is NOT a static singleton because it
/// depends on <see cref="SequentialApprovalHandler"/> which requires scoped services
/// (IApproverResolver, WorkFlowOptions, ILogger).  The dispatcher receives the
/// pre-built <see cref="ApprovalHandler"/> at construction time from DI.</para>
/// </summary>
internal sealed class NodeKindDispatcher : INodeKindDispatcher
{
    private static readonly StartHandler     _start     = new();
    private static readonly EndHandler       _end       = new();
    private static readonly CcHandler        _cc        = new();
    private static readonly ConditionHandler _condition = new();

    private readonly ApprovalHandler _approval;

    public NodeKindDispatcher(ApprovalHandler approval)
    {
        _approval = approval ?? throw new ArgumentNullException(nameof(approval));
    }

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
