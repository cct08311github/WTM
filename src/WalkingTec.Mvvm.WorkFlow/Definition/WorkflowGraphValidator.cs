#nullable enable
// WF-4: Publish-time structural validation for WorkflowGraph documents.
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

using System;
using System.Collections.Generic;
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
                    }
                }
            }

            if (node.Kind == NodeKind.Approval && node.ApproverRule == null)
                return GraphValidationResult.Fail(GraphValidationError.ApprovalNodeMissingApproverRule,
                    $"Approval node '{node.NodeKey}' must have an 'approverRule'.");
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
}
