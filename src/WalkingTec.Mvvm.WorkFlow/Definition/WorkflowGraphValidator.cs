#nullable enable
// WF-4: Publish-time structural validation for WorkflowGraph documents.
// WF-11: Extended with routing-rule whitelist validation.
// WF-17: Extended with fork↔Join pairing validation (ParallelGateway / InclusiveGateway).
//
// Validation is fail-closed: any structural problem returns a descriptive
// GraphValidationResult with a closed error code.  Exceptions are NOT used
// for user-authored validation failures (repo Result-style convention).
//
// Checks performed (§4 / §6 spec requirements):
//   1. Graph key present.
//   2. Nodes list non-empty.
//   3. Exactly one Start node.
//   4. At least one End node.
//   5. Transitions reference existing nodeKeys (dangling-edge check).
//   6. Condition nodes have mandatory default target, and it exists.
//   7. Condition branch targets exist.
//   8. Approval nodes have approverRule.
//   9. All non-Start nodes are reachable from Start (via transitions + branch targets).
//  10. (WF-11) Every branch RoutingRuleDef: field in whitelist, closed operator, In cap ≤ 100.
//  11. (WF-17) ParallelGateway/InclusiveGateway must have joinNodeKey pointing to a Join node.
//  12. (WF-17) InclusiveGateway transitions must each carry a condition (routing rule).
//  13. (WF-17) Ack nodes must have AckMode specified.

using System;
using System.Collections.Generic;
using WalkingTec.Mvvm.WorkFlow.Engine.Routing;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Definition;

/// <summary>
/// Validates a <see cref="WorkflowGraph"/> at publish time.
/// All checks are structural; routing-rule whitelist validation is deferred to
/// <c>RoutingValidator</c> (WF-11).
/// </summary>
public static class WorkflowGraphValidator
{
    /// <summary>
    /// Validate <paramref name="graph"/> and return the first detected error.
    /// Returns <see cref="GraphValidationResult.Success"/> when all checks pass.
    /// </summary>
    public static GraphValidationResult Validate(WorkflowGraph graph)
    {
        if (graph is null) throw new ArgumentNullException(nameof(graph));

        // 1. Graph key must be present.
        if (string.IsNullOrWhiteSpace(graph.Key))
            return GraphValidationResult.Fail(GraphValidationError.MissingKey,
                "The graph 'key' field is required and must not be empty.");

        // 2. Nodes list non-empty.
        if (graph.Nodes == null || graph.Nodes.Count == 0)
            return GraphValidationResult.Fail(GraphValidationError.NoNodes,
                "The graph must contain at least one node.");

        // Build a nodeKey set for O(1) existence checks.
        var nodeKeys = new HashSet<string>(StringComparer.Ordinal);
        int startCount = 0;
        int endCount = 0;

        foreach (var node in graph.Nodes)
        {
            nodeKeys.Add(node.NodeKey);
            if (node.Kind == NodeKind.Start) startCount++;
            if (node.Kind == NodeKind.End)   endCount++;
        }

        // 3. Exactly one Start node.
        if (startCount == 0)
            return GraphValidationResult.Fail(GraphValidationError.MissingStartNode,
                "The graph must contain exactly one Start node.");
        if (startCount > 1)
            return GraphValidationResult.Fail(GraphValidationError.MultipleStartNodes,
                $"The graph must contain exactly one Start node, but {startCount} were found.");

        // 4. At least one End node.
        if (endCount == 0)
            return GraphValidationResult.Fail(GraphValidationError.MissingEndNode,
                "The graph must contain at least one End node.");

        // 5. Transition nodeKey existence checks.
        if (graph.Transitions != null)
        {
            foreach (var t in graph.Transitions)
            {
                if (!nodeKeys.Contains(t.From))
                    return GraphValidationResult.Fail(GraphValidationError.DanglingTransitionFrom,
                        $"Transition 'from' nodeKey '{t.From}' does not reference an existing node.");
                if (!nodeKeys.Contains(t.To))
                    return GraphValidationResult.Fail(GraphValidationError.DanglingTransitionTo,
                        $"Transition 'to' nodeKey '{t.To}' does not reference an existing node.");
            }
        }

        // 6–8. Per-node checks.
        foreach (var node in graph.Nodes)
        {
            if (node.Kind == NodeKind.Condition)
            {
                // Condition nodes must have a mandatory default target.
                if (string.IsNullOrWhiteSpace(node.Default))
                    return GraphValidationResult.Fail(GraphValidationError.ConditionNodeMissingDefault,
                        $"Condition node '{node.NodeKey}' must specify a 'default' target nodeKey.");

                if (!nodeKeys.Contains(node.Default))
                    return GraphValidationResult.Fail(GraphValidationError.ConditionNodeDanglingDefault,
                        $"Condition node '{node.NodeKey}' default target '{node.Default}' does not reference an existing node.");

                // Branch targets must exist.
                if (node.Branches != null)
                {
                    foreach (var branch in node.Branches)
                    {
                        if (!nodeKeys.Contains(branch.Target))
                            return GraphValidationResult.Fail(GraphValidationError.DanglingBranchTarget,
                                $"Condition node '{node.NodeKey}' branch target '{branch.Target}' does not reference an existing node.");

                        // WF-11: Validate the branch routing rule against the whitelist.
                        var routingError = ValidateRoutingRule(
                            branch.Rule, node.NodeKey, graph.FieldWhitelist);
                        if (routingError is not null)
                            return routingError;
                    }
                }
            }

            if (node.Kind == NodeKind.Approval && node.ApproverRule == null)
                return GraphValidationResult.Fail(GraphValidationError.ApprovalNodeMissingApproverRule,
                    $"Approval node '{node.NodeKey}' must have an 'approverRule'.");

            // WF-17 check 11: gateway nodes must reference a valid Join node.
            if (node.Kind is NodeKind.ParallelGateway or NodeKind.InclusiveGateway)
            {
                if (string.IsNullOrWhiteSpace(node.JoinNodeKey))
                    return GraphValidationResult.Fail(GraphValidationError.GatewayMissingJoinNodeKey,
                        $"Gateway node '{node.NodeKey}' must specify a 'joinNodeKey' pointing to a Join node.");

                if (!nodeKeys.Contains(node.JoinNodeKey))
                    return GraphValidationResult.Fail(GraphValidationError.GatewayDanglingJoinNodeKey,
                        $"Gateway node '{node.NodeKey}' joinNodeKey '{node.JoinNodeKey}' does not reference an existing node.");

                // Verify the referenced node is actually a Join node.
                bool isJoin = false;
                foreach (var n in graph.Nodes)
                {
                    if (string.Equals(n.NodeKey, node.JoinNodeKey, StringComparison.Ordinal))
                    {
                        if (n.Kind != NodeKind.Join)
                            return GraphValidationResult.Fail(GraphValidationError.GatewayJoinNodeKeyNotJoinKind,
                                $"Gateway node '{node.NodeKey}' joinNodeKey '{node.JoinNodeKey}' references a node of kind '{n.Kind}', not Join.");
                        isJoin = true;
                        break;
                    }
                }
                if (!isJoin)
                    return GraphValidationResult.Fail(GraphValidationError.GatewayDanglingJoinNodeKey,
                        $"Gateway node '{node.NodeKey}' joinNodeKey '{node.JoinNodeKey}' does not reference an existing node.");
            }

            // WF-17 check 12: InclusiveGateway outgoing transitions must carry conditions.
            if (node.Kind == NodeKind.InclusiveGateway && graph.Transitions != null)
            {
                foreach (var t in graph.Transitions)
                {
                    if (string.Equals(t.From, node.NodeKey, StringComparison.Ordinal)
                        && t.Condition is null)
                    {
                        return GraphValidationResult.Fail(GraphValidationError.InclusiveGatewayTransitionMissingCondition,
                            $"InclusiveGateway node '{node.NodeKey}': outgoing transition to '{t.To}' must have a 'condition'. " +
                            "All InclusiveGateway branches require an explicit routing condition.");
                    }
                }
            }

            // WF-17 check 13: Ack nodes must specify AckMode.
            if (node.Kind == NodeKind.Ack && node.AckMode is null)
                return GraphValidationResult.Fail(GraphValidationError.AckNodeMissingAckMode,
                    $"Ack node '{node.NodeKey}' must specify an 'ackMode' (All, Any, or Quorum).");
        }

        // 9. Reachability from Start (BFS over transitions + Condition branch targets + default).
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        // Find the start node key.
        string startKey = string.Empty;
        foreach (var node in graph.Nodes)
        {
            if (node.Kind == NodeKind.Start)
            {
                startKey = node.NodeKey;
                break;
            }
        }
        reachable.Add(startKey);
        queue.Enqueue(startKey);

        // Build a forward adjacency map from transitions.
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (graph.Transitions != null)
        {
            foreach (var t in graph.Transitions)
            {
                if (!adjacency.TryGetValue(t.From, out var list))
                    adjacency[t.From] = list = new List<string>();
                list.Add(t.To);
            }
        }

        // Also add Condition branch targets / default into the adjacency.
        foreach (var node in graph.Nodes)
        {
            if (node.Kind == NodeKind.Condition)
            {
                if (!adjacency.TryGetValue(node.NodeKey, out var list))
                    adjacency[node.NodeKey] = list = new List<string>();

                if (!string.IsNullOrWhiteSpace(node.Default))
                    list.Add(node.Default!);

                if (node.Branches != null)
                    foreach (var b in node.Branches)
                        list.Add(b.Target);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out var neighbors)) continue;
            foreach (var neighbor in neighbors)
            {
                if (reachable.Add(neighbor))
                    queue.Enqueue(neighbor);
            }
        }

        if (reachable.Count < nodeKeys.Count)
        {
            var unreachable = new List<string>();
            foreach (var k in nodeKeys)
                if (!reachable.Contains(k))
                    unreachable.Add(k);

            return GraphValidationResult.Fail(GraphValidationError.UnreachableNodes,
                $"The following node(s) are unreachable from the Start node: {string.Join(", ", unreachable)}.");
        }

        return GraphValidationResult.Success;
    }

    // ── WF-11: Routing-rule whitelist validation ───────────────────────────────

    /// <summary>
    /// Validate a <see cref="RoutingRuleDef"/> (and all sub-rules recursively) at
    /// publish time. Returns a <see cref="GraphValidationResult"/> failure on the
    /// first violation, or null when the rule passes all checks.
    ///
    /// Mirrors the runtime <see cref="Engine.Routing.WhitelistRoutingEvaluator.ValidateRule"/>
    /// check (defense-in-depth: validate at publish AND at runtime evaluation).
    /// </summary>
    private static GraphValidationResult? ValidateRoutingRule(
        RoutingRuleDef rule,
        string nodeKey,
        IReadOnlyList<FieldWhitelistEntry> whitelist)
    {
        // Composite AND
        if (rule.And is { Count: > 0 })
        {
            foreach (var sub in rule.And)
            {
                var err = ValidateRoutingRule(sub, nodeKey, whitelist);
                if (err is not null) return err;
            }
            return null;
        }

        // Composite OR
        if (rule.Or is { Count: > 0 })
        {
            foreach (var sub in rule.Or)
            {
                var err = ValidateRoutingRule(sub, nodeKey, whitelist);
                if (err is not null) return err;
            }
            return null;
        }

        // Leaf rule — must have field + operator.
        if (string.IsNullOrWhiteSpace(rule.Field))
            return GraphValidationResult.Fail(
                GraphValidationError.RoutingInvalidRuleStructure,
                $"Condition node '{nodeKey}': branch routing rule must specify a 'field'.");

        if (rule.Operator is null)
            return GraphValidationResult.Fail(
                GraphValidationError.RoutingInvalidRuleStructure,
                $"Condition node '{nodeKey}': branch routing rule for field '{rule.Field}' must specify an 'operator'.");

        // Whitelist check — security gate.
        bool inWhitelist = false;
        FieldWhitelistEntry? entry = null;
        foreach (var e in whitelist)
        {
            if (string.Equals(e.Field, rule.Field, StringComparison.Ordinal))
            {
                inWhitelist = true;
                entry = e;
                break;
            }
        }

        if (!inWhitelist)
            return GraphValidationResult.Fail(
                GraphValidationError.RoutingFieldNotAllowed,
                $"Condition node '{nodeKey}': branch rule references field '{rule.Field}' which is " +
                "not in the graph FieldWhitelist. Add the field to 'fieldWhitelist' before publishing.");

        // CLR type must be resolvable.
        if (Engine.Routing.WhitelistRoutingEvaluator.ResolveClrType(entry!.ClrType) is null)
            return GraphValidationResult.Fail(
                GraphValidationError.RoutingUnknownClrType,
                $"Condition node '{nodeKey}': whitelist entry for field '{entry.Field}' has " +
                $"unknown CLR type '{entry.ClrType}'.");

        // In/NotIn cap check (mirrors AnalysisQueryEngine.Filters.cs:84).
        if (rule.Operator is FilterOperator.In or FilterOperator.NotIn)
        {
            int count = CountInListItems(rule.Value);
            if (count > Engine.Routing.WhitelistRoutingEvaluator.MaxInListSize)
                return GraphValidationResult.Fail(
                    GraphValidationError.RoutingInListTooLarge,
                    $"Condition node '{nodeKey}': In/NotIn list for field '{rule.Field}' " +
                    $"has {count} items; maximum is {Engine.Routing.WhitelistRoutingEvaluator.MaxInListSize}.");
        }

        return null; // All checks pass.
    }

    private static int CountInListItems(object? value)
    {
        if (value is null) return 0;
        if (value is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            int c = 0;
            foreach (var _ in je.EnumerateArray()) c++;
            return c;
        }
        if (value is System.Collections.ICollection col) return col.Count;
        if (value is System.Collections.IEnumerable en and not string)
        {
            int c = 0;
            foreach (var _ in en) c++;
            return c;
        }
        return 1;
    }

    // ── WF-16: Dominator computation ──────────────────────────────────────────

    /// <summary>
    /// Compute the dominator set for every node in <paramref name="graph"/>.
    ///
    /// <para>Algorithm: classic iterative data-flow analysis (Cooper et al.).
    /// The Start node dominates only itself.  For every other node N:
    /// dom(N) = {N} ∪ (∩ dom(P) for all predecessors P of N).</para>
    ///
    /// <para>Convergence is guaranteed for acyclic graphs (workflows are DAGs)
    /// and for graphs with simple back-edges (safe conservative approximation).</para>
    /// </summary>
    /// <returns>
    /// Dictionary mapping each nodeKey to the set of nodeKeys that dominate it.
    /// A node X dominates node Y iff every path from Start to Y passes through X.
    /// </returns>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> ComputeDominators(
        WorkflowGraph graph)
    {
        if (graph is null) throw new ArgumentNullException(nameof(graph));

        var allKeys = new HashSet<string>(StringComparer.Ordinal);
        string? startKey = null;

        foreach (var n in graph.Nodes)
        {
            allKeys.Add(n.NodeKey);
            if (n.Kind == NodeKind.Start)
                startKey = n.NodeKey;
        }

        if (startKey is null)
            throw new InvalidOperationException("Graph has no Start node; cannot compute dominators.");

        // Build predecessor map (reverse adjacency).
        var predecessors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var k in allKeys)
            predecessors[k] = new List<string>();

        // Transitions contribute forward edges → populate predecessors.
        if (graph.Transitions != null)
        {
            foreach (var t in graph.Transitions)
            {
                if (!predecessors.TryGetValue(t.To, out var preds))
                    predecessors[t.To] = preds = new List<string>();
                preds.Add(t.From);
            }
        }

        // Condition branch targets + default also count as forward edges.
        foreach (var node in graph.Nodes)
        {
            if (node.Kind == NodeKind.Condition)
            {
                if (!string.IsNullOrWhiteSpace(node.Default))
                {
                    if (!predecessors.TryGetValue(node.Default!, out var dp))
                        predecessors[node.Default!] = dp = new List<string>();
                    if (!dp.Contains(node.NodeKey))
                        dp.Add(node.NodeKey);
                }
                if (node.Branches != null)
                {
                    foreach (var b in node.Branches)
                    {
                        if (!predecessors.TryGetValue(b.Target, out var bp))
                            predecessors[b.Target] = bp = new List<string>();
                        if (!bp.Contains(node.NodeKey))
                            bp.Add(node.NodeKey);
                    }
                }
            }
        }

        // Initialize dominator sets.
        // dom(Start) = {Start}
        // dom(N) = all nodes (universal set — intersection will narrow it)
        var dom = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var k in allKeys)
        {
            dom[k] = string.Equals(k, startKey, StringComparison.Ordinal)
                ? new HashSet<string>(StringComparer.Ordinal) { startKey }
                : new HashSet<string>(allKeys, StringComparer.Ordinal);
        }

        // Iterative fixed-point.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var k in allKeys)
            {
                if (string.Equals(k, startKey, StringComparison.Ordinal)) continue;

                // new dom(k) = {k} ∪ (∩ dom(p) for all p in predecessors(k))
                var preds = predecessors[k];
                if (preds.Count == 0) continue; // unreachable node; dom stays universal

                // Start with intersection of all predecessor dom sets.
                HashSet<string>? intersection = null;
                foreach (var p in preds)
                {
                    if (intersection is null)
                    {
                        intersection = new HashSet<string>(dom[p], StringComparer.Ordinal);
                    }
                    else
                    {
                        intersection.IntersectWith(dom[p]);
                    }
                }

                var newDom = intersection ?? new HashSet<string>(StringComparer.Ordinal);
                newDom.Add(k);

                if (!newDom.SetEquals(dom[k]))
                {
                    dom[k] = newDom;
                    changed = true;
                }
            }
        }

        // Convert to readonly for the return type.
        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var kv in dom)
            result[kv.Key] = kv.Value;
        return result;
    }

    /// <summary>
    /// Returns the set of upstream Approval node keys that are strict dominators of
    /// <paramref name="triggerNodeKey"/> — i.e., valid return targets for WF-16.
    ///
    /// <para>A strict dominator of N is any dominator of N except N itself.</para>
    ///
    /// <para>Only <see cref="NodeKind.Approval"/> nodes are considered valid return targets —
    /// returning to Start/End/Condition nodes is not meaningful.</para>
    /// </summary>
    public static IReadOnlySet<string> GetValidReturnTargets(
        WorkflowGraph graph,
        string triggerNodeKey)
    {
        var dominators = ComputeDominators(graph);

        if (!dominators.TryGetValue(triggerNodeKey, out var domSet))
            return new HashSet<string>(StringComparer.Ordinal);

        // Build a lookup of Approval nodeKeys.
        var approvalKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
        {
            if (n.Kind == NodeKind.Approval)
                approvalKeys.Add(n.NodeKey);
        }

        // Valid targets = strict dominators that are Approval nodes.
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in domSet)
        {
            if (!string.Equals(k, triggerNodeKey, StringComparison.Ordinal)
                && approvalKeys.Contains(k))
            {
                result.Add(k);
            }
        }
        return result;
    }

    /// <summary>
    /// Returns the "previous" Approval node — the last Approval node visited on the
    /// canonical topological path from Start to <paramref name="triggerNodeKey"/>,
    /// exclusive of the trigger itself.
    ///
    /// <para>Used by <c>ReturnToPrevAsync</c> to resolve the implicit target.</para>
    ///
    /// <para>When multiple Approval nodes strictly dominate the trigger node, the one
    /// closest to the trigger in topological order is returned (maximum BFS depth from Start
    /// among all dominating Approval nodes).</para>
    /// </summary>
    /// <returns>The nodeKey of the closest dominating Approval node, or null if none exists.</returns>
    public static string? GetPrevApprovalNode(WorkflowGraph graph, string triggerNodeKey)
    {
        var validTargets = GetValidReturnTargets(graph, triggerNodeKey);
        if (validTargets.Count == 0)
            return null;

        // BFS from Start to compute topological depth (distance from Start) for each node.
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        var adjacency = BuildAdjacency(graph);
        var queue = new Queue<string>();

        foreach (var n in graph.Nodes)
            if (n.Kind == NodeKind.Start)
            {
                depth[n.NodeKey] = 0;
                queue.Enqueue(n.NodeKey);
                break;
            }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            int d = depth[current];
            if (!adjacency.TryGetValue(current, out var neighbors)) continue;
            foreach (var nb in neighbors)
            {
                if (!depth.ContainsKey(nb))
                {
                    depth[nb] = d + 1;
                    queue.Enqueue(nb);
                }
            }
        }

        // Among the valid return targets, pick the one with the highest depth (closest to trigger).
        string? best = null;
        int bestDepth = -1;
        foreach (var k in validTargets)
        {
            int d = depth.TryGetValue(k, out var v) ? v : 0;
            if (d > bestDepth)
            {
                bestDepth = d;
                best = k;
            }
        }
        return best;
    }

    /// <summary>
    /// Compute the set of node keys in the "return span" — every node strictly between
    /// <paramref name="targetNodeKey"/> and <paramref name="triggerNodeKey"/> in topological
    /// order (exclusive of the target, inclusive of the trigger node and all nodes reachable
    /// from the target that are dominated by the trigger or reachable before it).
    ///
    /// <para>The span is the set of NodeInstances that must be superseded + tasks discarded
    /// when executing STEP-4 of the return path (spec §5.7 Wave-3).</para>
    ///
    /// <para>Implementation: BFS forward from <paramref name="targetNodeKey"/> stopping at
    /// nodes that are NOT reachable from <paramref name="targetNodeKey"/> before the
    /// <paramref name="triggerNodeKey"/>.  The trigger node itself is included
    /// in the span (its tasks are discarded; the node is superseded).</para>
    /// </summary>
    public static IReadOnlySet<string> ComputeReturnSpan(
        WorkflowGraph graph,
        string targetNodeKey,
        string triggerNodeKey)
    {
        // BFS forward from targetNodeKey, collect all nodes reachable before we "exit"
        // through the target again — simple forward reachability from target, stopping
        // when we exit the subgraph that lies between target and the rest of the graph.
        //
        // For well-structured DAG workflows the span is: all nodes reachable from target
        // that are NOT dominators of every path from Start AND that the trigger dominates
        // (i.e., lie "after" target and "before or at" trigger).
        //
        // Simpler conservative bound used here: BFS forward from target; collect every
        // node reachable from target that lies strictly before or at the trigger.
        // Nodes that lie "past" the trigger (only reachable after trigger) are excluded.

        var adjacency = BuildAdjacency(graph);

        // Forward-reachable from target (inclusive).
        var fromTarget = new HashSet<string>(StringComparer.Ordinal);
        var q1 = new Queue<string>();
        q1.Enqueue(targetNodeKey);
        fromTarget.Add(targetNodeKey);
        while (q1.Count > 0)
        {
            var c = q1.Dequeue();
            if (!adjacency.TryGetValue(c, out var ns)) continue;
            foreach (var n in ns)
                if (fromTarget.Add(n))
                    q1.Enqueue(n);
        }

        // Forward-reachable from nodes in fromTarget excluding targetNodeKey itself
        // that are still in the "span" — i.e., NOT reachable from Start without
        // going through target again.
        //
        // For the purposes of STEP-4, the span = fromTarget minus the target itself,
        // bounded to nodes reachable from target up-to-and-including the trigger.
        //
        // Nodes downstream of the trigger (only reachable after/past trigger) are excluded.
        // Forward-reachable from triggerNodeKey's successors = nodes past the trigger.
        var pastTrigger = new HashSet<string>(StringComparer.Ordinal);
        if (adjacency.TryGetValue(triggerNodeKey, out var triggerSuccessors))
        {
            foreach (var s in triggerSuccessors)
            {
                var q2 = new Queue<string>();
                q2.Enqueue(s);
                while (q2.Count > 0)
                {
                    var c = q2.Dequeue();
                    if (!pastTrigger.Add(c)) continue;
                    if (!adjacency.TryGetValue(c, out var ns2)) continue;
                    foreach (var n2 in ns2)
                        q2.Enqueue(n2);
                }
            }
        }

        // Span = (fromTarget − targetNodeKey) − pastTrigger
        var span = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in fromTarget)
        {
            if (!string.Equals(k, targetNodeKey, StringComparison.Ordinal)
                && !pastTrigger.Contains(k))
            {
                span.Add(k);
            }
        }
        // Always include the trigger itself.
        span.Add(triggerNodeKey);
        return span;
    }

    /// <summary>
    /// Build a forward adjacency map (nodeKey → list of successor nodeKeys) for <paramref name="graph"/>.
    /// Handles Condition branches/default, and WF-17 ParallelGateway/InclusiveGateway transitions.
    /// </summary>
    private static Dictionary<string, List<string>> BuildAdjacency(WorkflowGraph graph)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
            adjacency[n.NodeKey] = new List<string>();

        if (graph.Transitions != null)
        {
            foreach (var t in graph.Transitions)
            {
                if (!adjacency.TryGetValue(t.From, out var list))
                    adjacency[t.From] = list = new List<string>();
                if (!list.Contains(t.To))
                    list.Add(t.To);
            }
        }

        foreach (var node in graph.Nodes)
        {
            if (node.Kind == NodeKind.Condition)
            {
                if (!adjacency.TryGetValue(node.NodeKey, out var list))
                    adjacency[node.NodeKey] = list = new List<string>();

                if (!string.IsNullOrWhiteSpace(node.Default) && !list.Contains(node.Default!))
                    list.Add(node.Default!);

                if (node.Branches != null)
                    foreach (var b in node.Branches)
                        if (!list.Contains(b.Target))
                            list.Add(b.Target);
            }
            // WF-17: ParallelGateway / InclusiveGateway are structurally connected via
            // transitions (already added above), but the JoinNodeKey is also a structural
            // successor for dominator / reachability purposes.
            else if (node.Kind is NodeKind.ParallelGateway or NodeKind.InclusiveGateway)
            {
                if (!string.IsNullOrWhiteSpace(node.JoinNodeKey))
                {
                    if (!adjacency.TryGetValue(node.NodeKey, out var glist))
                        adjacency[node.NodeKey] = glist = new List<string>();
                    // The join itself is reachable from the gateway (indirectly via branches).
                    // Add to ensure the Join is reachable in BFS (it may not have an explicit
                    // transition from the gateway itself — branches connect to it via their paths).
                    if (!glist.Contains(node.JoinNodeKey))
                        glist.Add(node.JoinNodeKey);
                }
            }
        }

        return adjacency;
    }
}
