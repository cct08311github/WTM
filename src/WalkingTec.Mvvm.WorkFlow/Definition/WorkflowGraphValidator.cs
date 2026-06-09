#nullable enable
// WF-4: Publish-time structural validation for WorkflowGraph documents.
// WF-11: Extended with routing-rule whitelist validation.
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
}
