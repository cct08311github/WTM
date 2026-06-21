#nullable enable
// WF-6/WF-8/WF-9/WF-10/WF-13: Built-in INodeKindHandler implementations + NodeKindDispatcher registry.
// WF-17: ParallelGatewayHandler, InclusiveGatewayHandler, JoinHandler, AckHandler.
//
// MVP handlers (non-Approval):
//   StartHandler            — pass-through (no tasks, no CC, no wait)
//   EndHandler              — pass-through (engine advances ProcessInstance to Approved)
//   CcHandler               — writes CcRecord rows, never blocks (spec §5.9); WF-13: full tenant+permission check
//   ConditionHandler        — stub: takes default/first transition (real routing = WF-11)
//
// Approval handler:
//   ApprovalHandler         — dispatches to mode-specific sub-handler:
//                             Sequential (WF-8) → SequentialApprovalHandler
//                             All (WF-9)        → AllApprovalHandler
//                             Any (WF-10)       → AnyApprovalHandler
//
// WF-17 handlers (parallel/inclusive gateways + Join + Ack):
//   ParallelGatewayHandler  — AND-fork: mints ALL outgoing branch tokens, pins JoinExpectedArrivals.
//   InclusiveGatewayHandler — OR-fork: mints matching branch tokens, pins JoinExpectedArrivals; fail-closed if 0.
//   JoinHandler             — Join barrier: NOT a blocking node in the traditional sense.
//                             CanCompleteAsync returns false until the Join fires via CAS.
//   AckHandler              — Blocking-acknowledge: holds until ackMode quorum is met.
//
// NodeKindDispatcher is the singleton registry wired by AddWtmWorkFlow.
// All approval sub-handlers are injected via DI so they have access to
// IApproverResolver, WorkFlowOptions, and ILogger.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine.Routing;
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

// ── WF-17: ParallelGatewayHandler ─────────────────────────────────────────────

/// <summary>
/// Handler for <see cref="NodeKind.ParallelGateway"/> (AND-fork) nodes.
///
/// <para><strong>OnEnterAsync:</strong> mints ALL outgoing branch tokens in one SaveChanges,
/// stamping each with a shared <c>ForkGroupId</c> and <c>JoinNodeKey</c>.
/// Also pins <see cref="NodeInstance.JoinExpectedArrivals"/> on the Join node to
/// the number of branches minted (requires the Join NodeInstance to already exist).</para>
///
/// <para><strong>CanCompleteAsync:</strong> always returns true — gateway itself passes through;
/// the engine drain loop will then process each branch token.</para>
///
/// <para>The fork mint is idempotent via the
/// <c>UNIQUE (TenantCode, InstanceId, NodeKey, Generation)</c> index —
/// a concurrent second mint attempt silently no-ops.</para>
/// </summary>
internal sealed class ParallelGatewayHandler : INodeKindHandler
{
    private readonly ILogger<ParallelGatewayHandler> _logger;

    public ParallelGatewayHandler(ILogger<ParallelGatewayHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task OnEnterAsync(NodeHandlerContext ctx)
    {
        var node     = ctx.NodeDef;
        var instance = ctx.ProcessInstance;
        var db       = ctx.Db;
        var ct       = ctx.CancellationToken;
        var now      = DateTime.UtcNow;

        // Collect all outgoing transitions from this gateway.
        var outgoing = ctx.Graph.Transitions
            .Where(t => string.Equals(t.From, node.NodeKey, StringComparison.Ordinal))
            .ToList();

        if (outgoing.Count == 0)
        {
            _logger.LogWarning(
                "ParallelGatewayHandler: gateway '{NodeKey}' has no outgoing transitions — " +
                "no branches minted for instance {InstanceId}.",
                node.NodeKey, instance.ID);
            return;
        }

        // All branches in this fork share a ForkGroupId.
        var forkGroupId = Guid.NewGuid();
        var joinNodeKey = node.JoinNodeKey;

        // Mint all branch tokens in one batch (AND-fork: ALL branches activated).
        var branchNodeDefs = new List<NodeDef>();
        foreach (var t in outgoing)
        {
            var branchDef = ctx.Graph.Nodes.FirstOrDefault(
                n => string.Equals(n.NodeKey, t.To, StringComparison.Ordinal));
            if (branchDef is null)
            {
                _logger.LogError(
                    "ParallelGatewayHandler: transition target '{Target}' not found in graph '{GraphKey}'. Skipping.",
                    t.To, ctx.Graph.Key);
                continue;
            }
            branchNodeDefs.Add(branchDef);
        }

        foreach (var branchDef in branchNodeDefs)
        {
            var branchNode = new NodeInstance
            {
                ID                 = Guid.NewGuid(),
                TenantCode         = instance.TenantCode,
                InstanceId         = instance.ID,
                NodeKey            = branchDef.NodeKey,
                NodeKind           = branchDef.Kind,
                State              = NodeState.Pending,
                ApproveMode        = branchDef.ApproveMode,
                ApprovePercent     = branchDef.ApprovePercent,
                RejectGate         = branchDef.RejectGate ?? RejectGate.Immediate,
                RejectPolicy       = branchDef.RejectPolicy ?? RejectPolicy.ReturnToInitiator,
                ForkGroupId        = forkGroupId,
                JoinNodeKey        = joinNodeKey,
                Generation         = instance.Generation,
                ActivatedAt        = now,
                RowVer             = 0,
            };
            db.Set<NodeInstance>().Add(branchNode);
        }

        if (branchNodeDefs.Count > 0)
        {
            // Pre-mint the Join NodeInstance eagerly so that PinJoinExpectedArrivalsAsync
            // (which uses ExecuteUpdateAsync) finds an existing row to update.
            // Without this, the Join would be minted lazily by the first arriving branch
            // AFTER the gateway OnEnter completes, and PinJoinExpectedArrivalsAsync would
            // silently update 0 rows (Join row doesn't exist yet → JoinExpectedArrivals
            // stays at 0 → FireJoinIfSatisfiedAsync fires on the very first arrival because
            // 1 >= 0 is true).
            if (!string.IsNullOrWhiteSpace(joinNodeKey))
            {
                var joinDef = ctx.Graph.Nodes.FirstOrDefault(
                    n => string.Equals(n.NodeKey, joinNodeKey, StringComparison.Ordinal));
                if (joinDef is not null)
                {
                    var alreadyExists = await db.Set<NodeInstance>()
                        .AsNoTracking()
                        .AnyAsync(
                            n => n.InstanceId == instance.ID
                              && n.NodeKey    == joinNodeKey
                              && n.Generation == instance.Generation,
                            ct);
                    if (!alreadyExists)
                    {
                        db.Set<NodeInstance>().Add(new NodeInstance
                        {
                            ID             = Guid.NewGuid(),
                            TenantCode     = instance.TenantCode,
                            InstanceId     = instance.ID,
                            NodeKey        = joinDef.NodeKey,
                            NodeKind       = joinDef.Kind,
                            State          = NodeState.Pending,
                            ApproveMode    = joinDef.ApproveMode,
                            ApprovePercent = joinDef.ApprovePercent,
                            RejectGate     = joinDef.RejectGate ?? RejectGate.Immediate,
                            RejectPolicy   = joinDef.RejectPolicy ?? RejectPolicy.ReturnToInitiator,
                            Generation     = instance.Generation,
                            RowVer         = 0,
                        });
                    }
                }
            }

            // #483 Bug #8: pin JoinExpectedArrivals in-memory so it's committed atomically
            // with the Join NodeInstance row — eliminates the two-commit stranding window.
            if (!string.IsNullOrWhiteSpace(joinNodeKey))
            {
                var joinEntry = db.ChangeTracker.Entries<NodeInstance>()
                    .FirstOrDefault(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Added
                                      && string.Equals(e.Entity.NodeKey, joinNodeKey, StringComparison.Ordinal));
                if (joinEntry is not null)
                    joinEntry.Entity.JoinExpectedArrivals = branchNodeDefs.Count;
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException dbEx) when (
                dbEx.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true
                || dbEx.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true)
            {
                // #483 Bug #4: idempotent no-op — a concurrent winner already minted these branches.
                // Detach all Added NodeInstance entities so the context stays usable.
                var addedEntries = db.ChangeTracker.Entries<NodeInstance>()
                    .Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Added)
                    .ToList();
                foreach (var e in addedEntries)
                    e.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
                _logger.LogDebug(
                    "ParallelGatewayHandler: unique-constraint collision for instance {InstanceId} node '{NodeKey}' — " +
                    "concurrent winner already minted branches. No-op.",
                    instance.ID, node.NodeKey);
                return;
            }

            // Pin JoinExpectedArrivals on the Join NodeInstance (now guaranteed to exist).
            // This is a best-effort defensive call — the atomic in-memory pin above already
            // committed the value in the same SaveChanges, so this is a no-op under normal flow.
            if (!string.IsNullOrWhiteSpace(joinNodeKey))
            {
                await PinJoinExpectedArrivalsAsync(db, instance, joinNodeKey!, branchNodeDefs.Count, ct);
            }

            _logger.LogDebug(
                "ParallelGatewayHandler: minted {Count} branch token(s) (forkGroup={ForkGroupId}) for instance {InstanceId}.",
                branchNodeDefs.Count, forkGroupId, instance.ID);
        }
    }

    public Task<bool> CanCompleteAsync(NodeHandlerContext ctx) => Task.FromResult(true);
    public Task OnCompleteAsync(NodeHandlerContext ctx) => Task.CompletedTask;

    /// <summary>
    /// Sets <see cref="NodeInstance.JoinExpectedArrivals"/> on the Join node to <paramref name="count"/>.
    /// The Join node must already exist as Pending. Uses a targeted update that is idempotent
    /// (sets the value absolutely — safe because the fork is the only writer at mint time).
    /// </summary>
    internal static async Task PinJoinExpectedArrivalsAsync(
        DbContext db,
        ProcessInstance instance,
        string joinNodeKey,
        int count,
        System.Threading.CancellationToken ct)
    {
        await db.Set<NodeInstance>()
            .Where(n => n.InstanceId == instance.ID
                         && n.NodeKey == joinNodeKey
                         && n.Generation == instance.Generation
                         && n.State == NodeState.Pending)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.JoinExpectedArrivals, count)
                       .SetProperty(x => x.RowVer, x => x.RowVer + 1),
                ct);
    }
}

// ── WF-17: InclusiveGatewayHandler ────────────────────────────────────────────

/// <summary>
/// Handler for <see cref="NodeKind.InclusiveGateway"/> (OR-fork) nodes.
///
/// <para><strong>OnEnterAsync:</strong> evaluates each outgoing transition's <c>Condition</c>
/// via <see cref="IRoutingEvaluator"/>.  Mints only the matching branch tokens.
/// Pins <see cref="NodeInstance.JoinExpectedArrivals"/> to the number actually minted.
/// Fail-closed: if no branches match, logs an error (the node stays Activated; the engine
/// will return <see cref="WorkflowActionResult.FailClosedRouting"/> on the next drain step).</para>
///
/// <para><strong>CanCompleteAsync:</strong> returns true only if at least one branch was minted.
/// If no branches matched (fail-closed state), returns false so the engine loops back and
/// returns <see cref="WorkflowActionResult.FailClosedRouting"/>.</para>
/// </summary>
internal sealed class InclusiveGatewayHandler : INodeKindHandler
{
    private readonly IRoutingEvaluator _routingEvaluator;
    private readonly ILogger<InclusiveGatewayHandler> _logger;

    public InclusiveGatewayHandler(
        IRoutingEvaluator routingEvaluator,
        ILogger<InclusiveGatewayHandler> logger)
    {
        _routingEvaluator = routingEvaluator ?? throw new ArgumentNullException(nameof(routingEvaluator));
        _logger           = logger           ?? throw new ArgumentNullException(nameof(logger));
    }

    // Tracks the number of branches minted (set in OnEnterAsync; read in CanCompleteAsync).
    // NodeHandlerContext is re-created per call so state set in OnEnterAsync is available
    // in CanCompleteAsync via the Db + NodeInstance queries.
    public async Task OnEnterAsync(NodeHandlerContext ctx)
    {
        var node     = ctx.NodeDef;
        var instance = ctx.ProcessInstance;
        var db       = ctx.Db;
        var ct       = ctx.CancellationToken;
        var now      = DateTime.UtcNow;

        // Deserialize FormDataJson for routing evaluation.
        IReadOnlyDictionary<string, object?> formData =
            new Dictionary<string, object?>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(instance.FormDataJson))
        {
            try
            {
                var raw = System.Text.Json.JsonSerializer.Deserialize<
                    Dictionary<string, System.Text.Json.JsonElement>>(instance.FormDataJson);
                if (raw is not null)
                    formData = raw.ToDictionary(
                        kv => kv.Key,
                        kv => (object?)kv.Value,
                        StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "InclusiveGatewayHandler: failed to parse FormDataJson for instance {InstanceId}. Using empty dict.",
                    instance.ID);
            }
        }

        // Evaluate each outgoing transition's condition.
        var forkGroupId    = Guid.NewGuid();
        var joinNodeKey    = node.JoinNodeKey;
        var mintedBranches = new List<NodeDef>();

        foreach (var t in ctx.Graph.Transitions
                     .Where(t => string.Equals(t.From, node.NodeKey, StringComparison.Ordinal)))
        {
            if (t.Condition is null)
            {
                // Should have been caught at publish-time validation; skip defensively.
                _logger.LogWarning(
                    "InclusiveGatewayHandler: transition to '{To}' from '{From}' has no condition. Skipping.",
                    t.To, node.NodeKey);
                continue;
            }

            var evalResult = _routingEvaluator.Evaluate(
                t.Condition, ctx.Graph.FieldWhitelist, formData);
            bool matches = evalResult.Code == Engine.Routing.RoutingEvaluationCode.Ok
                           && evalResult.IsMatch;

            if (!matches) continue;

            var branchDef = ctx.Graph.Nodes.FirstOrDefault(
                n => string.Equals(n.NodeKey, t.To, StringComparison.Ordinal));
            if (branchDef is null) continue;

            mintedBranches.Add(branchDef);
        }

        if (mintedBranches.Count == 0)
        {
            // Fail-closed: no matching branches.
            _logger.LogError(
                "InclusiveGatewayHandler: no branches matched for gateway '{NodeKey}' in graph '{GraphKey}'. " +
                "Instance {InstanceId} will be fail-closed.",
                node.NodeKey, ctx.Graph.Key, instance.ID);
            // OnEnterAsync returns without minting anything.  CanCompleteAsync will return false
            // and the engine will return FailClosedRouting.
            return;
        }

        foreach (var branchDef in mintedBranches)
        {
            var branchNode = new NodeInstance
            {
                ID             = Guid.NewGuid(),
                TenantCode     = instance.TenantCode,
                InstanceId     = instance.ID,
                NodeKey        = branchDef.NodeKey,
                NodeKind       = branchDef.Kind,
                State          = NodeState.Pending,
                ApproveMode    = branchDef.ApproveMode,
                ApprovePercent = branchDef.ApprovePercent,
                RejectGate     = branchDef.RejectGate ?? RejectGate.Immediate,
                RejectPolicy   = branchDef.RejectPolicy ?? RejectPolicy.ReturnToInitiator,
                ForkGroupId    = forkGroupId,
                JoinNodeKey    = joinNodeKey,
                Generation     = instance.Generation,
                ActivatedAt    = now,
                RowVer         = 0,
            };
            db.Set<NodeInstance>().Add(branchNode);
        }

        // Pre-mint the Join NodeInstance eagerly (same reason as ParallelGatewayHandler —
        // PinJoinExpectedArrivalsAsync needs an existing Pending row to update).
        if (!string.IsNullOrWhiteSpace(joinNodeKey))
        {
            var joinDef = ctx.Graph.Nodes.FirstOrDefault(
                n => string.Equals(n.NodeKey, joinNodeKey, StringComparison.Ordinal));
            if (joinDef is not null)
            {
                var alreadyExists = await db.Set<NodeInstance>()
                    .AsNoTracking()
                    .AnyAsync(
                        n => n.InstanceId == instance.ID
                          && n.NodeKey    == joinNodeKey
                          && n.Generation == instance.Generation,
                        ct);
                if (!alreadyExists)
                {
                    db.Set<NodeInstance>().Add(new NodeInstance
                    {
                        ID             = Guid.NewGuid(),
                        TenantCode     = instance.TenantCode,
                        InstanceId     = instance.ID,
                        NodeKey        = joinDef.NodeKey,
                        NodeKind       = joinDef.Kind,
                        State          = NodeState.Pending,
                        ApproveMode    = joinDef.ApproveMode,
                        ApprovePercent = joinDef.ApprovePercent,
                        RejectGate     = joinDef.RejectGate ?? RejectGate.Immediate,
                        RejectPolicy   = joinDef.RejectPolicy ?? RejectPolicy.ReturnToInitiator,
                        Generation     = instance.Generation,
                        RowVer         = 0,
                    });
                }
            }
        }

        // #483 Bug #8: pin JoinExpectedArrivals atomically with the Join row.
        if (!string.IsNullOrWhiteSpace(joinNodeKey))
        {
            var joinEntry = db.ChangeTracker.Entries<NodeInstance>()
                .FirstOrDefault(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Added
                                  && string.Equals(e.Entity.NodeKey, joinNodeKey, StringComparison.Ordinal));
            if (joinEntry is not null)
                joinEntry.Entity.JoinExpectedArrivals = mintedBranches.Count;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException dbEx) when (
            dbEx.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true
            || dbEx.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true)
        {
            // #483 Bug #4: idempotent no-op — a concurrent winner already minted these branches.
            var addedEntries = db.ChangeTracker.Entries<NodeInstance>()
                .Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Added)
                .ToList();
            foreach (var e in addedEntries)
                e.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            _logger.LogDebug(
                "InclusiveGatewayHandler: unique-constraint collision for instance {InstanceId} node '{NodeKey}' — " +
                "concurrent winner already minted branches. No-op.",
                instance.ID, node.NodeKey);
            return;
        }

        if (!string.IsNullOrWhiteSpace(joinNodeKey))
        {
            await ParallelGatewayHandler.PinJoinExpectedArrivalsAsync(
                db, instance, joinNodeKey!, mintedBranches.Count, ct);
        }

        _logger.LogDebug(
            "InclusiveGatewayHandler: minted {Count} branch token(s) (forkGroup={ForkGroupId}) for instance {InstanceId}.",
            mintedBranches.Count, forkGroupId, instance.ID);
    }

    public async Task<bool> CanCompleteAsync(NodeHandlerContext ctx)
    {
        // Check if any branch tokens were minted for this gateway's forkGroup.
        // If none exist (fail-closed), return false so the engine returns FailClosedRouting.
        var nodeInst = ctx.NodeInstance;
        var hasBranches = await ctx.Db.Set<NodeInstance>()
            .AsNoTracking()
            .AnyAsync(
                n => n.InstanceId == nodeInst.InstanceId
                  && n.Generation == nodeInst.Generation
                  && n.State != NodeState.Superseded
                  && n.NodeKey != nodeInst.NodeKey
                  && n.JoinNodeKey == ctx.NodeDef.JoinNodeKey,
                ctx.CancellationToken);
        return hasBranches;
    }

    public Task OnCompleteAsync(NodeHandlerContext ctx) => Task.CompletedTask;
}

// ── WF-17: JoinHandler ────────────────────────────────────────────────────────

/// <summary>
/// Handler for <see cref="NodeKind.Join"/> nodes.
///
/// <para>A Join node is a barrier: it waits until all its expected branch arrivals
/// have been recorded by <c>IncrementJoinArrivedAsync</c> (called by the engine
/// drain loop when a branch node completes and routes to the Join).</para>
///
/// <para><strong>CanCompleteAsync:</strong> the Join node can complete only when
/// <c>JoinArrivedCount &gt;= JoinExpectedArrivals</c>.  The actual state flip
/// (<see cref="NodeState.CompletedApproved"/>) is performed atomically by
/// <c>FireJoinIfSatisfiedAsync</c> in the engine drain loop — not here.</para>
///
/// <para><strong>OnEnterAsync:</strong> no-op — the Join is activated by the engine;
/// it does not mint tasks or CC records.</para>
/// </summary>
internal sealed class JoinHandler : INodeKindHandler
{
    public Task OnEnterAsync(NodeHandlerContext ctx) => Task.CompletedTask;

    public async Task<bool> CanCompleteAsync(NodeHandlerContext ctx)
    {
        // Re-read the Join NodeInstance to get the freshest counters.
        var ni = await ctx.Db.Set<NodeInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                n => n.ID == ctx.NodeInstance.ID,
                ctx.CancellationToken);

        if (ni is null) return false;

        // Already fired (by a concurrent caller via FireJoinIfSatisfiedAsync CAS)?
        if (ni.State == NodeState.CompletedApproved) return true;

        // Quorum check: all expected branch arrivals must have arrived.
        return ni.JoinArrivedCount >= ni.JoinExpectedArrivals
            && ni.JoinExpectedArrivals > 0;
    }

    public Task OnCompleteAsync(NodeHandlerContext ctx) => Task.CompletedTask;
}

// ── WF-17: AckHandler ─────────────────────────────────────────────────────────

/// <summary>
/// Handler for <see cref="NodeKind.Ack"/> (blocking-acknowledge) nodes.
///
/// <para>An Ack node holds the token until the configured quorum of acknowledgers
/// have acted.  This is structurally identical to an Approval node in blocking semantics,
/// but acknowledgers CANNOT reject — they can only acknowledge.</para>
///
/// <para><strong>CanCompleteAsync:</strong> checks whether the ack quorum is met
/// based on <see cref="NodeInstance.AckMode"/>:
/// <list type="bullet">
///   <item><see cref="AckMode.Any"/> — true as soon as <c>ApprovedCount &gt;= 1</c>.</item>
///   <item><see cref="AckMode.All"/> — true when <c>ApprovedCount &gt;= TotalRequired</c>.</item>
///   <item><see cref="AckMode.Quorum"/> — true when <c>ApprovedCount &gt;= ApprovePercent * TotalRequired</c>.</item>
/// </list>
/// </para>
///
/// <para><strong>OnEnterAsync:</strong> no-op for now (acknowledger tasks are minted by
/// the engine in a future wave; currently the Ack node is a structured barrier that can
/// be completed via <c>ApproveTaskAsync</c> with acknowledge semantics).</para>
/// </summary>
internal sealed class AckHandler : INodeKindHandler
{
    private readonly ILogger<AckHandler> _logger;

    public AckHandler(ILogger<AckHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task OnEnterAsync(NodeHandlerContext ctx) => Task.CompletedTask;

    public Task<bool> CanCompleteAsync(NodeHandlerContext ctx)
    {
        var ni      = ctx.NodeInstance;
        var ackMode = ni.AckMode ?? AckMode.All;

        bool complete = ackMode switch
        {
            AckMode.Any    => ni.ApprovedCount >= 1,
            AckMode.All    => ni.TotalRequired > 0 && ni.ApprovedCount >= ni.TotalRequired,
            AckMode.Quorum => ni.TotalRequired > 0
                              && ni.ApprovePercent.HasValue
                              && ni.ApprovedCount >= (int)Math.Ceiling(
                                     (double)(ni.ApprovePercent.Value * ni.TotalRequired)),
            _ => false,
        };

        if (!complete)
        {
            _logger.LogDebug(
                "AckHandler: Ack node '{NodeKey}' not yet complete " +
                "(mode={AckMode}, approved={Approved}, total={Total}).",
                ni.NodeKey, ackMode, ni.ApprovedCount, ni.TotalRequired);
        }

        return Task.FromResult(complete);
    }

    public Task OnCompleteAsync(NodeHandlerContext ctx) => Task.CompletedTask;
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

    private readonly CcHandler               _cc;
    private readonly ApprovalHandler         _approval;
    private readonly AckHandler              _ack;
    private readonly JoinHandler             _join;
    private readonly ParallelGatewayHandler  _parallelGateway;
    private readonly InclusiveGatewayHandler _inclusiveGateway;

    public NodeKindDispatcher(
        CcHandler cc,
        ApprovalHandler approval,
        AckHandler ack,
        JoinHandler join,
        ParallelGatewayHandler parallelGateway,
        InclusiveGatewayHandler inclusiveGateway)
    {
        _cc               = cc               ?? throw new ArgumentNullException(nameof(cc));
        _approval         = approval         ?? throw new ArgumentNullException(nameof(approval));
        _ack              = ack              ?? throw new ArgumentNullException(nameof(ack));
        _join             = join             ?? throw new ArgumentNullException(nameof(join));
        _parallelGateway  = parallelGateway  ?? throw new ArgumentNullException(nameof(parallelGateway));
        _inclusiveGateway = inclusiveGateway ?? throw new ArgumentNullException(nameof(inclusiveGateway));
    }

    public INodeKindHandler Resolve(NodeKind kind) => kind switch
    {
        NodeKind.Start              => _start,
        NodeKind.End                => _end,
        NodeKind.Cc                 => _cc,
        NodeKind.Condition          => _condition,
        NodeKind.Approval           => _approval,
        NodeKind.Ack                => _ack,              // WF-17: blocking-acknowledge
        NodeKind.Join               => _join,             // WF-17: Join barrier
        NodeKind.ParallelGateway    => _parallelGateway,  // WF-17: AND-fork
        NodeKind.InclusiveGateway   => _inclusiveGateway, // WF-17: OR-fork
        _ => throw new InvalidOperationException(
                 $"No INodeKindHandler registered for NodeKind.{kind}. " +
                 $"Ensure the handler is registered in AddWtmWorkFlow."),
    };
}
