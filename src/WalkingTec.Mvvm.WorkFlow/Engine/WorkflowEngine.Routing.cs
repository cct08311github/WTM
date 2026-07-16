#nullable enable
// WorkflowEngine — Routing region (node minting, next-node/condition resolution, pending-task inbox, first-pending notify).
//
// #668: partial-class split of WorkflowEngine.cs — pure code motion (see WorkflowEngine.cs
// for the shared design notes, invariants, and race-condition catalogue). Members below were
// cut verbatim (including their original doc comments) from WorkflowEngine.cs; no signature,
// accessibility, or logic changes were made during the move.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Mint a new <see cref="NodeInstance"/> in <see cref="NodeState.Pending"/> for the
    /// given node definition, stamped with <paramref name="generation"/>.
    /// </summary>
    private async Task<NodeInstance> MintNodeInstanceAsync(
        ProcessInstance instance,
        NodeDef nodeDef,
        CancellationToken ct,
        uint? generation = null,
        string? definitionCode = null)
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
            // Wave-3: stamp generation epoch at mint time.
            Generation = generation ?? instance.Generation,
            // WF-17: stamp AckMode for Ack nodes.
            AckMode = nodeDef.AckMode,
            // WF-19: stamp DefinitionCode for delegation scope filtering.
            DefinitionCode = definitionCode,
        };
        Db.Set<NodeInstance>().Add(node);
        await Db.SaveChangesAsync(ct);
        return node;
    }

    /// <summary>
    /// Determine the next node key to route to from the completed <paramref name="nodeDef"/>.
    ///
    /// WF-11 strategy for Condition nodes (Exclusive gateway):
    ///   1. Deserialize FormDataJson into a dictionary.
    ///   2. Evaluate branches IN ORDER via IRoutingEvaluator — first match wins.
    ///   3. If no branch matches, use the Default target (always present after publish validation).
    ///   4. If no match AND no default → fail-closed (return null; caller logs + returns FailClosedRouting).
    ///
    /// For all other node kinds: follow the first outgoing transition in the transitions list.
    /// </summary>
    private string? ResolveNextNodeKey(
        WorkflowGraph graph,
        NodeDef nodeDef,
        ProcessInstance instance)
    {
        if (nodeDef.Kind == NodeKind.Condition)
        {
            return ResolveConditionNodeKey(graph, nodeDef, instance);
        }

        // For all other node kinds: follow the first matching outgoing transition.
        return graph.Transitions
            .FirstOrDefault(t => t.From == nodeDef.NodeKey)
            ?.To;
    }

    /// <summary>
    /// Exclusive gateway routing (WF-11): evaluate branches in order against FormDataJson;
    /// take the first matching branch; fall back to Default if none match.
    /// Fail-closed when no match and no default.
    /// </summary>
    private string? ResolveConditionNodeKey(
        WorkflowGraph graph,
        NodeDef nodeDef,
        ProcessInstance instance)
    {
        // Deserialize FormDataJson to IReadOnlyDictionary<string, object?>.
        // An empty/null FormDataJson is treated as an empty dictionary (all fields missing → fail-closed → default).
        IReadOnlyDictionary<string, object?> formData = DeserializeFormData(instance.FormDataJson);

        // Evaluate branches in defined array order (Exclusive first-match).
        if (nodeDef.Branches is { Count: > 0 })
        {
            foreach (var branch in nodeDef.Branches)
            {
                // Runtime re-validation (defense in depth against publish-time bypass).
                var evalResult = _routingEvaluator.Evaluate(
                    branch.Rule,
                    graph.FieldWhitelist,
                    formData);

                if (evalResult.Code != RoutingEvaluationCode.Ok)
                {
                    // Log the security/structural violation but continue to next branch
                    // rather than short-circuiting the whole node — fail-closed at branch level.
                    _logger.LogWarning(
                        "ResolveConditionNodeKey: branch evaluation error for node '{NodeKey}', " +
                        "target '{Target}': {Code} — {Message}. Branch treated as non-matching.",
                        nodeDef.NodeKey, branch.Target, evalResult.Code, evalResult.ErrorMessage);
                    continue;
                }

                if (evalResult.IsMatch)
                {
                    _logger.LogDebug(
                        "ResolveConditionNodeKey: node '{NodeKey}' branch matched, routing to '{Target}'.",
                        nodeDef.NodeKey, branch.Target);
                    return branch.Target;
                }
            }
        }

        // No branch matched — use the Default target.
        if (nodeDef.Default is not null)
        {
            _logger.LogDebug(
                "ResolveConditionNodeKey: node '{NodeKey}' no branch matched; routing to default '{Default}'.",
                nodeDef.NodeKey, nodeDef.Default);
            return nodeDef.Default;
        }

        // No match and no default — fail-closed (spec §5.8: "no match + no default impossible
        // at runtime given publish validation; if somehow reached → FAIL CLOSED").
        _logger.LogError(
            "ResolveConditionNodeKey: node '{NodeKey}' in graph '{GraphKey}' has no matching branch " +
            "and no default target. Fail-closed.",
            nodeDef.NodeKey, graph.Key);
        return null;
    }

    // ── WF-14: Inbox query ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ApprovalTask>> GetPendingTasksAsync(
        string actorITCode,
        string? tenantCode,
        CancellationToken ct = default)
    {
        // The DataContext query filter (ITenant + IsValid) auto-scopes to tenantCode
        // because it is registered against the correct context.
        // We additionally filter by AssigneeITCode and State server-side.
        var tasks = await Db.Set<ApprovalTask>()
            .AsNoTracking()
            .Where(t => t.AssigneeITCode == actorITCode
                     && t.State == TaskState.Pending
                     && t.IsValid == true)
            .Include(t => t.NodeInstance)
            .OrderBy(t => t.DueUtc == null)
            .ThenBy(t => t.DueUtc)
            .ToListAsync(ct);

        return tasks.AsReadOnly();
    }

    // ── WF-15: Notify helper ─────────────────────────────────────────────────

    /// <summary>
    /// Fire <see cref="IWorkflowNotifier.NotifyTaskAssignedAsync"/> for every <see cref="TaskState.Pending"/>
    /// <see cref="ApprovalTask"/> that currently belongs to <paramref name="instanceId"/>.
    /// Called AFTER the engine advances (post-commit) when a new approval node becomes active.
    /// Best-effort — all exceptions are caught and logged, never propagated.
    /// </summary>
    private async Task NotifyFirstPendingTasksAsync(
        Guid instanceId,
        ProcessInstance instance,
        CancellationToken ct)
    {
        if (_notifier is null) return;

        try
        {
            // Read the currently active node and its pending tasks.
            var activeNodeInst = await Db.Set<NodeInstance>()
                .AsNoTracking()
                .Where(n => n.InstanceId == instanceId
                             && (n.State == NodeState.Pending || n.State == NodeState.Activated))
                .OrderBy(n => n.ID)
                .FirstOrDefaultAsync(ct);

            if (activeNodeInst is null) return;

            var pendingTasks = await Db.Set<ApprovalTask>()
                .AsNoTracking()
                .Where(t => t.NodeInstanceId == activeNodeInst.ID && t.State == TaskState.Pending)
                .ToListAsync(ct);

            foreach (var pendingTask in pendingTasks)
            {
                try { await _notifier.NotifyTaskAssignedAsync(instance, activeNodeInst, pendingTask, ct); }
                catch (Exception ex) { _logger.LogError(ex, "WF-15 NotifyTaskAssignedAsync failed for task {TaskId}.", pendingTask.ID); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WF-15 NotifyFirstPendingTasksAsync failed for instance {InstanceId}.", instanceId);
        }
    }

    /// <summary>
    /// Deserialize <paramref name="formDataJson"/> into a flat string-keyed dictionary.
    /// Returns an empty dictionary for null/empty input (missing fields → fail-closed in evaluator).
    /// Only the top-level flat object is supported for the MVP routing evaluator.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> DeserializeFormData(string? formDataJson)
    {
        if (string.IsNullOrWhiteSpace(formDataJson))
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        try
        {
            using var doc = JsonDocument.Parse(formDataJson);
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Clone the value so the dictionary outlives the JsonDocument.
                result[prop.Name] = prop.Value.Clone();
            }
            return result;
        }
        catch (JsonException ex)
        {
            // Malformed FormDataJson — return empty dict so all field lookups fail-closed.
            // The engine will fall back to the Default branch (which publish validation guarantees exists).
            _ = ex; // Suppress unused warning — intentional swallow here; caller handles default.
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }
}
