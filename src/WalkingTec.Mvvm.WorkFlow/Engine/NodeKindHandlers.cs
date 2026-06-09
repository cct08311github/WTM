#nullable enable
// WF-6/WF-8/WF-9/WF-10/WF-13: Built-in INodeKindHandler implementations + NodeKindDispatcher registry.
//
// MVP handlers (non-Approval):
//   StartHandler     — pass-through (no tasks, no CC, no wait)
//   EndHandler       — pass-through (engine advances ProcessInstance to Approved)
//   CcHandler        — writes CcRecord rows, never blocks (spec §5.9); WF-13: full tenant+permission check
//   ConditionHandler — stub: takes default/first transition (real routing = WF-11)
//
// Approval handler:
//   ApprovalHandler  — dispatches to mode-specific sub-handler:
//                      Sequential (WF-8) → SequentialApprovalHandler
//                      All (WF-9)        → AllApprovalHandler
//                      Any (WF-10)       → AnyApprovalHandler
//
// NodeKindDispatcher is the singleton registry wired by AddWtmWorkFlow.
// All approval sub-handlers are injected via DI so they have access to
// IApproverResolver, WorkFlowOptions, and ILogger.

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
/// Handler for <see cref="NodeKind.Cc"/> nodes (WF-13 + WF-14 full implementation).
///
/// <para>Structurally cannot block (spec §5.9): resolves recipients via
/// <see cref="IApproverResolver"/>, applies tenant-isolation checks via
/// <see cref="ICcTenantValidator"/> (WF-14: FrameworkUser lookup), writes
/// <see cref="CcRecord"/> rows, then immediately completes.
/// Recipients may mark-read or comment but CANNOT approve or reject.
/// No <see cref="ApprovalTask"/> is ever created by this handler.</para>
///
/// <para>Tenant isolation (WF-14): a CC recipient whose ITCode is not a valid,
/// active user in the instance's tenant is rejected/skipped (logged) and NEVER
/// written as a CcRecord.  This prevents cross-tenant CC leaks at the data level.
/// The CcRecord always carries the INSTANCE's TenantCode regardless.</para>
///
/// <para>Ack distinction: a Cc node is always non-blocking. A future Ack node
/// (NodeKind.Ack — WF-16) blocks until the recipient acknowledges.
/// CC and Ack are two separate NodeKind values, not the same handler.</para>
/// </summary>
internal sealed class CcHandler : INodeKindHandler
{
    private readonly IApproverResolver _resolver;
    private readonly ICcTenantValidator _tenantValidator;
    private readonly ILogger<CcHandler> _logger;

    public CcHandler(
        IApproverResolver resolver,
        ICcTenantValidator tenantValidator,
        ILogger<CcHandler> logger)
    {
        _resolver        = resolver        ?? throw new ArgumentNullException(nameof(resolver));
        _tenantValidator = tenantValidator ?? throw new ArgumentNullException(nameof(tenantValidator));
        _logger          = logger          ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task OnEnterAsync(NodeHandlerContext ctx)
    {
        var nodeDef  = ctx.NodeDef;
        var instance = ctx.ProcessInstance;
        var nodeInst = ctx.NodeInstance;
        var now      = DateTime.UtcNow;

        // Collect candidate ITCodes from two sources (in priority order):
        // 1. The node's primary approverRule (resolves User/Role/ManagerChain).
        // 2. Inline cc[] array on the node (each entry has its own Rule).
        var allRecipients = new System.Collections.Generic.List<string>();

        // 1. Primary approverRule on the CC node.
        if (nodeDef.ApproverRule is { } primaryRule)
        {
            var resolution = await _resolver.ResolveAsync(
                ctx.Db, primaryRule, nodeInst, instance.InitiatorITCode, ctx.CancellationToken);

            if (resolution.Outcome == ResolverOutcome.Resolved)
            {
                allRecipients.AddRange(resolution.Approvers);
            }
            else
            {
                _logger.LogDebug(
                    "CcHandler: primary approverRule resolution on node '{NodeKey}' returned {Outcome}: {Detail}.",
                    nodeDef.NodeKey, resolution.Outcome, resolution.Detail);
            }
        }

        // 2. Inline cc[] entries (each has its own Rule).
        if (nodeDef.Cc is { Count: > 0 } ccRules)
        {
            foreach (var ccEntry in ccRules)
            {
                var resolution = await _resolver.ResolveAsync(
                    ctx.Db, ccEntry.Rule, nodeInst, instance.InitiatorITCode, ctx.CancellationToken);

                if (resolution.Outcome == ResolverOutcome.Resolved)
                {
                    allRecipients.AddRange(resolution.Approvers);
                }
                else
                {
                    _logger.LogDebug(
                        "CcHandler: inline cc rule on node '{NodeKey}' returned {Outcome}: {Detail}.",
                        nodeDef.NodeKey, resolution.Outcome, resolution.Detail);
                }
            }
        }

        // Tenant isolation: all CcRecords carry the instance's TenantCode.
        // WF-14: each recipient ITCode is validated via ICcTenantValidator (FrameworkUser
        // lookup) before writing. Invalid/cross-tenant recipients are skipped, never written.
        var instanceTenantCode = instance.TenantCode;

        // Dedup and write one CcRecord per unique, validated recipient.
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int written = 0;

        foreach (var itCode in allRecipients)
        {
            if (!seen.Add(itCode))
            {
                // Duplicate — skip (human-dedupe per spec §5.1 discipline).
                continue;
            }

            // WF-14: validate recipient ITCode against FrameworkUser for same-tenant.
            // Recipients that are not valid active users in this tenant are skipped.
            // The validator logs a warning for each rejected recipient.
            var isValid = await _tenantValidator.IsValidTenantUserAsync(
                ctx.Db, itCode, instanceTenantCode, ctx.CancellationToken);

            if (!isValid)
            {
                // Logged by the validator; skip — do NOT write a cross-tenant CcRecord.
                continue;
            }

            var record = new CcRecord
            {
                ID               = Guid.NewGuid(),
                TenantCode       = instanceTenantCode,   // always the INSTANCE's tenant
                InstanceId       = instance.ID,
                NodeKey          = nodeDef.NodeKey,
                RecipientITCode  = itCode,
                Trigger          = CcTrigger.OnNode,
                SentAtUtc        = now,
            };
            ctx.Db.Set<CcRecord>().Add(record);
            written++;
        }

        if (written > 0)
        {
            await ctx.Db.SaveChangesAsync(ctx.CancellationToken);
            _logger.LogDebug(
                "CcHandler: wrote {Count} CcRecord(s) for node '{NodeKey}' on instance {InstanceId}.",
                written, nodeDef.NodeKey, instance.ID);
        }
    }

    public Task<bool> CanCompleteAsync(NodeHandlerContext ctx) => Task.FromResult(true);

    public Task OnCompleteAsync(NodeHandlerContext ctx) => Task.CompletedTask;
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
///   <item><see cref="ApproveMode.All"/> → <see cref="AllApprovalHandler"/> (WF-9)</item>
///   <item><see cref="ApproveMode.Any"/> → <see cref="AnyApprovalHandler"/> (WF-10)</item>
/// </list>
/// </para>
/// </summary>
internal sealed class ApprovalHandler : INodeKindHandler
{
    private readonly SequentialApprovalHandler _sequential;
    private readonly AllApprovalHandler _all;
    private readonly AnyApprovalHandler _any;

    public ApprovalHandler(
        SequentialApprovalHandler sequential,
        AllApprovalHandler all,
        AnyApprovalHandler any)
    {
        _sequential = sequential ?? throw new ArgumentNullException(nameof(sequential));
        _all        = all        ?? throw new ArgumentNullException(nameof(all));
        _any        = any        ?? throw new ArgumentNullException(nameof(any));
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
            ApproveMode.All        => _all,
            ApproveMode.Any        => _any,
            null                   => _sequential, // default to Sequential if not set
            _ => throw new InvalidOperationException(
                     $"Unknown ApproveMode '{mode}' for node '{ctx.NodeInstance.NodeKey}'."),
        };
    }
}

// ── Dispatcher registry ───────────────────────────────────────────────────────

/// <summary>
/// Registry mapping <see cref="NodeKind"/> to <see cref="INodeKindHandler"/>.
/// Registered by <see cref="ServiceCollectionExtensions.AddWtmWorkFlow"/>.
///
/// <para>The <see cref="ApprovalHandler"/> and <see cref="CcHandler"/> are NOT static
/// singletons because they depend on scoped services (IApproverResolver, WorkFlowOptions,
/// ILogger).  The dispatcher receives the pre-built handlers at construction time from DI.</para>
/// </summary>
internal sealed class NodeKindDispatcher : INodeKindDispatcher
{
    private static readonly StartHandler     _start     = new();
    private static readonly EndHandler       _end       = new();
    private static readonly ConditionHandler _condition = new();

    private readonly CcHandler       _cc;
    private readonly ApprovalHandler _approval;

    public NodeKindDispatcher(CcHandler cc, ApprovalHandler approval)
    {
        _cc       = cc       ?? throw new ArgumentNullException(nameof(cc));
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
