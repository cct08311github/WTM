#nullable enable
// WF-4: Process-definition JSON schema POCOs.
//
// These are pure data-bag records that represent the canonical form of a
// WorkflowGraph document.  They are serialized to / deserialized from the
// ProcessDefinitionVersion.GraphJson column.
//
// Design decisions:
//   • schemaVersion (int) is the published contract.  Evolution is additive-only.
//   • NodeDef / TransitionDef / sub-objects use camelCase JSON names (spec §4 example).
//   • Wave-deferred fields are marked with // Wave-N comments and kept nullable/optional
//     so the MVP serializer does not need to know about them.
//   • Routing / approver specs use closed enums + structured objects — no free-form
//     strings in any branching position (spec §6 injection-proof discipline).
//   • Enum values that map directly to Models.NodeKind / ApproveMode / … are kept
//     string-typed in JSON for readability, parsed via STJ enum converter.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Definition;

// ── Top-level document ────────────────────────────────────────────────────────

/// <summary>
/// Top-level workflow graph document stored in
/// <see cref="Models.ProcessDefinitionVersion.GraphJson"/>.
/// Serialized to canonical JSON (deterministic key order, no indentation)
/// and hashed to produce <see cref="Models.ProcessDefinitionVersion.ContentHash"/>.
///
/// schemaVersion = 1 is the published contract for Sprint-1.
/// Evolution is additive-only.
/// </summary>
public sealed class WorkflowGraph
{
    /// <summary>
    /// Published contract version.  Sprint-1 = 1.
    /// Bump ONLY for backward-incompatible changes (additive fields do not bump).
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Unique business key that matches <see cref="Models.ProcessDefinition.Code"/>.
    /// </summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>Human-readable display name for the process.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Field whitelist: each entry names a FormDataJson field that is
    /// allowed to be referenced in routing rules.  Enforced at publish-time
    /// and re-validated at routing-evaluation time (spec §6 whitelist discipline).
    /// </summary>
    [JsonPropertyName("fieldWhitelist")]
    public List<FieldWhitelistEntry> FieldWhitelist { get; set; } = new();

    /// <summary>Ordered list of node definitions.</summary>
    [JsonPropertyName("nodes")]
    public List<NodeDef> Nodes { get; set; } = new();

    /// <summary>Ordered list of transition definitions.</summary>
    [JsonPropertyName("transitions")]
    public List<TransitionDef> Transitions { get; set; } = new();
}

// ── Field whitelist ───────────────────────────────────────────────────────────

/// <summary>
/// One entry in the per-version field whitelist.
/// Only fields listed here may appear in routing rule predicates.
/// </summary>
public sealed class FieldWhitelistEntry
{
    /// <summary>Field name as it appears in FormDataJson.</summary>
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    /// <summary>
    /// CLR type name used to coerce the JSON value before evaluation.
    /// Examples: "System.Decimal", "System.String", "System.Int32".
    /// </summary>
    [JsonPropertyName("clrType")]
    public string ClrType { get; set; } = string.Empty;

    /// <summary>
    /// Optional: restrict access to this field to specific roles.
    /// Null = all authenticated roles may reference the field in rules.
    /// // Wave-4: role-scoped field visibility enforcement in the routing evaluator.
    /// </summary>
    [JsonPropertyName("allowedRoles")]
    public List<string>? AllowedRoles { get; set; }
}

// ── Node definition ───────────────────────────────────────────────────────────

/// <summary>
/// Definition of a single node embedded in the graph.
/// Uses a discriminated shape: mandatory <see cref="NodeKey"/> + <see cref="Kind"/>,
/// then kind-specific properties that are nullable/absent for other kinds.
/// </summary>
public sealed class NodeDef
{
    /// <summary>Unique stable identifier for this node within the graph.</summary>
    [JsonPropertyName("nodeKey")]
    public string NodeKey { get; set; } = string.Empty;

    /// <summary>Node type (maps to <see cref="NodeKind"/>).</summary>
    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public NodeKind Kind { get; set; }

    // ── Approval-node fields ──────────────────────────────────────────────────

    /// <summary>
    /// Approval completion mode: Serial (串签) / All (会签) / Any (或签).
    /// Required when <see cref="Kind"/> == Approval; ignored for other kinds.
    /// </summary>
    [JsonPropertyName("approveMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ApproveMode? ApproveMode { get; set; }

    /// <summary>
    /// Optional ratio for 比例会签: approve when at least this fraction (0.0–1.0]
    /// of approvers have approved.  Null = all required (i.e., 100%).
    /// Only meaningful when ApproveMode == All.
    /// </summary>
    [JsonPropertyName("approvePercent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? ApprovePercent { get; set; }

    /// <summary>
    /// When rejection closes the node in 会签 mode.
    /// Default: Immediate.
    /// </summary>
    [JsonPropertyName("rejectGate")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public RejectGate? RejectGate { get; set; }

    /// <summary>
    /// What happens to the instance when this node is rejected.
    /// Default: ReturnToInitiator.
    /// </summary>
    [JsonPropertyName("rejectPolicy")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public RejectPolicy? RejectPolicy { get; set; }

    /// <summary>
    /// Structured approver resolution rule.
    /// Required for Approval nodes; absent for Start/End/Condition/Cc/Ack/Join.
    /// </summary>
    [JsonPropertyName("approverRule")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApproverRuleDef? ApproverRule { get; set; }

    /// <summary>
    /// Zero or more CC rules on this Approval node (inline 抄送).
    /// Triggers are evaluated per-entry.
    /// </summary>
    [JsonPropertyName("cc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CcRuleDef>? Cc { get; set; }

    /// <summary>
    /// Timeout configuration for this node.
    /// // Wave-5: engine consumption deferred.  Schema present from Sprint-1.
    /// </summary>
    [JsonPropertyName("timeout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimeoutDef? Timeout { get; set; }

    // ── Condition-node fields ─────────────────────────────────────────────────

    /// <summary>
    /// Ordered list of conditional branches evaluated left-to-right (exclusive).
    /// Required when <see cref="Kind"/> == Condition.
    /// </summary>
    [JsonPropertyName("branches")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<BranchDef>? Branches { get; set; }

    /// <summary>
    /// Default target nodeKey when no branch matches.
    /// MANDATORY for Condition nodes (enforced at publish-time validation).
    /// </summary>
    [JsonPropertyName("default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Default { get; set; }

    // ── WF-17: Parallel/Inclusive gateway + Join + Ack fields ────────────────

    /// <summary>
    /// The nodeKey of the Join node that this gateway's branch tokens will converge into.
    /// Required for <see cref="NodeKind.ParallelGateway"/> and
    /// <see cref="NodeKind.InclusiveGateway"/> nodes.
    /// Must reference an existing <see cref="NodeKind.Join"/> node in the graph.
    /// </summary>
    [JsonPropertyName("joinNodeKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JoinNodeKey { get; set; }

    /// <summary>
    /// Completion mode for <see cref="NodeKind.Ack"/> (blocking-acknowledge) nodes.
    /// Mirrors <see cref="ApproveMode"/> semantics for Ack nodes.
    /// Null for non-Ack node kinds.
    /// </summary>
    [JsonPropertyName("ackMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Models.AckMode? AckMode { get; set; }
}

// ── Approver rule ─────────────────────────────────────────────────────────────

/// <summary>
/// Describes how approvers are resolved for an Approval node.
/// type discriminates the resolution strategy.
/// </summary>
public sealed class ApproverRuleDef
{
    /// <summary>
    /// Resolution type:
    ///   "Role"         — members of a named role.
    ///   "User"         — explicit user ITCode list.
    ///   "ManagerChain" — initiator's manager chain, capped by <see cref="MaxLevel"/>.
    ///   "Initiator"    — // Wave-4: delegate back to initiator.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// For "Role": role code; for "User": ITCode.
    /// Null for ManagerChain and other dynamic types.
    /// </summary>
    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; set; }

    /// <summary>
    /// Maximum chain depth for ManagerChain resolution.
    /// Ignored for other types.
    /// Default: 2 (spec example).
    /// </summary>
    [JsonPropertyName("maxLevel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxLevel { get; set; }
}

// ── CC rule ───────────────────────────────────────────────────────────────────

/// <summary>Inline CC configuration attached to an Approval node.</summary>
public sealed class CcRuleDef
{
    /// <summary>When this CC fires.</summary>
    [JsonPropertyName("trigger")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Models.CcTrigger Trigger { get; set; }

    /// <summary>Approver-style rule describing CC recipients.</summary>
    [JsonPropertyName("rule")]
    public ApproverRuleDef Rule { get; set; } = new();
}

// ── Timeout definition ────────────────────────────────────────────────────────

/// <summary>
/// Timeout configuration for an approval node.
/// // Wave-5: engine will arm a WorkflowTimer from this at node-activation time.
/// Schema present from Sprint-1 so definitions can be authored before Wave-5 ships.
/// </summary>
public sealed class TimeoutDef
{
    /// <summary>ISO 8601 duration string, e.g. "P2D" (2 days), "PT8H" (8 hours).</summary>
    [JsonPropertyName("duration")]
    public string Duration { get; set; } = string.Empty;

    /// <summary>
    /// When true, the timer ticks only during business hours as defined by
    /// <see cref="WorkFlowOptions.BusinessCalendarId"/>.
    /// // Wave-5: calendar integration.
    /// </summary>
    [JsonPropertyName("businessCalendar")]
    public bool BusinessCalendar { get; set; }

    /// <summary>Action to take when the timer fires.</summary>
    [JsonPropertyName("action")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Models.TimerAction Action { get; set; }

    /// <summary>
    /// For Remind: re-send reminder every N hours.
    /// // Wave-5.
    /// </summary>
    [JsonPropertyName("remindEveryHours")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RemindEveryHours { get; set; }

    /// <summary>
    /// For Remind: stop after this many reminders.
    /// // Wave-5.
    /// </summary>
    [JsonPropertyName("maxReminders")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxReminders { get; set; }
}

// ── Condition branch ──────────────────────────────────────────────────────────

/// <summary>One conditional branch in a Condition node.</summary>
public sealed class BranchDef
{
    /// <summary>
    /// Routing rule predicate evaluated against FormDataJson.
    /// Uses the whitelisted field + closed operator enum (spec §6).
    /// </summary>
    [JsonPropertyName("rule")]
    public RoutingRuleDef Rule { get; set; } = new();

    /// <summary>nodeKey to activate when this branch matches.</summary>
    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;
}

/// <summary>
/// A single routing predicate: field op value, AND/OR composable via
/// <see cref="And"/> / <see cref="Or"/> (recursive; depth-limited at publish-time).
///
/// Injection-proof by construction: field must be in the per-version whitelist;
/// operator is a closed enum; value is a JSON literal — no Roslyn, no DynamicLinq,
/// no string eval (spec §6).
/// </summary>
public sealed class RoutingRuleDef
{
    /// <summary>
    /// Leaf-node: field name.
    /// Must appear in <see cref="WorkflowGraph.FieldWhitelist"/>.
    /// Null for composite (And/Or) rules that have no own predicate.
    /// </summary>
    [JsonPropertyName("field")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Field { get; set; }

    /// <summary>
    /// Leaf-node: comparison operator.
    /// Closed enum — no arbitrary string injection.
    /// </summary>
    [JsonPropertyName("operator")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public FilterOperator? Operator { get; set; }

    /// <summary>
    /// Leaf-node: comparison value (JSON primitive or array for In/NotIn).
    /// Deserialized as object? and coerced via ChangeType to the clrType at eval-time.
    /// </summary>
    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Value { get; set; }

    /// <summary>
    /// AND-composition: all sub-rules must hold.
    /// // Wave-3: deep AND/OR nesting; MVP accepts flat single-rule branches only.
    /// </summary>
    [JsonPropertyName("and")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RoutingRuleDef>? And { get; set; }

    /// <summary>
    /// OR-composition: any sub-rule may hold.
    /// // Wave-3.
    /// </summary>
    [JsonPropertyName("or")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RoutingRuleDef>? Or { get; set; }
}

/// <summary>
/// Closed routing filter operators — mirrors AnalysisQueryEngine.Filters.cs FilterOperator
/// (verified: AnalysisQueryRequest.cs:219) so the field whitelist discipline stays consistent
/// across Analysis and WorkFlow routing surfaces.
/// </summary>
public enum FilterOperator
{
    Eq,
    NotEq,
    Gt,
    Gte,
    Lt,
    Lte,
    Contains,
    NotContains,
    In,
    NotIn,
}

// ── Transition definition ─────────────────────────────────────────────────────

/// <summary>
/// A directed edge in the workflow graph from one node to another.
/// Condition nodes use their Branches/Default rather than transitions
/// to express conditional routing — but the transition list documents
/// the full structural connectivity for validation purposes.
/// </summary>
public sealed class TransitionDef
{
    /// <summary>Source nodeKey.</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Target nodeKey.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    /// <summary>
    /// Optional condition (for explicit conditional transitions).
    /// Wave-3: Inclusive gateway transitions carry conditions here.
    /// MVP: all explicit transitions are unconditional.
    /// </summary>
    [JsonPropertyName("condition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RoutingRuleDef? Condition { get; set; }
}
