#nullable enable
// WF-4: Closed validation-error codes for publish-time graph validation.
//
// Prefer a closed enum + Result<T, E> contract over throwing exceptions for
// user-authored graph validation failures (repo convention: Result-style for
// recoverable user errors; exceptions for programming errors / unexpected state).

namespace WalkingTec.Mvvm.WorkFlow.Definition;

/// <summary>
/// Closed error codes for publish-time workflow graph validation failures.
/// Callers branch on the code; human-readable details are in
/// <see cref="GraphValidationResult.ErrorMessage"/>.
/// </summary>
public enum GraphValidationError
{
    /// <summary>No error.</summary>
    None,

    /// <summary>The graph has no nodes.</summary>
    NoNodes,

    /// <summary>The graph has no exactly-one Start node.</summary>
    MissingStartNode,

    /// <summary>More than one Start node was found.</summary>
    MultipleStartNodes,

    /// <summary>The graph has no End node.</summary>
    MissingEndNode,

    /// <summary>A transition references a <c>from</c> nodeKey that does not exist.</summary>
    DanglingTransitionFrom,

    /// <summary>A transition references a <c>to</c> nodeKey that does not exist.</summary>
    DanglingTransitionTo,

    /// <summary>A branch target nodeKey in a Condition node does not exist.</summary>
    DanglingBranchTarget,

    /// <summary>A Condition node is missing its mandatory <c>default</c> target.</summary>
    ConditionNodeMissingDefault,

    /// <summary>The default target nodeKey in a Condition node does not exist.</summary>
    ConditionNodeDanglingDefault,

    /// <summary>An Approval node has no approverRule.</summary>
    ApprovalNodeMissingApproverRule,

    /// <summary>One or more nodes are unreachable from the Start node.</summary>
    UnreachableNodes,

    /// <summary>The graph key is null or empty.</summary>
    MissingKey,

    // WF-11: Routing-rule validation error codes.

    /// <summary>
    /// A routing rule in a branch references a field that is not in the graph's FieldWhitelist.
    /// Security violation — publish must be rejected.
    /// </summary>
    RoutingFieldNotAllowed,

    /// <summary>An In/NotIn value list in a branch rule exceeds the 100-item cap.</summary>
    RoutingInListTooLarge,

    /// <summary>A branch rule references an unknown or unsupported CLR type in the whitelist.</summary>
    RoutingUnknownClrType,

    /// <summary>A branch rule has an invalid structure (missing field, missing operator, etc.).</summary>
    RoutingInvalidRuleStructure,

    // WF-17: Parallel/Inclusive gateway + Join + Ack validation error codes.

    /// <summary>
    /// A ParallelGateway or InclusiveGateway node is missing its required <c>joinNodeKey</c> field.
    /// </summary>
    GatewayMissingJoinNodeKey,

    /// <summary>
    /// A gateway node's <c>joinNodeKey</c> references a nodeKey that does not exist in the graph.
    /// </summary>
    GatewayDanglingJoinNodeKey,

    /// <summary>
    /// A gateway node's <c>joinNodeKey</c> references a node that is not of kind Join.
    /// </summary>
    GatewayJoinNodeKeyNotJoinKind,

    /// <summary>
    /// An InclusiveGateway has an outgoing transition without a <c>condition</c>.
    /// All InclusiveGateway branches must carry explicit routing conditions.
    /// </summary>
    InclusiveGatewayTransitionMissingCondition,

    /// <summary>
    /// An Ack node is missing its required <c>ackMode</c> (All, Any, or Quorum).
    /// </summary>
    AckNodeMissingAckMode,

    /// <summary>
    /// A ParallelGateway or InclusiveGateway node has no outgoing transitions.
    /// A gateway with zero branches will strand the instance at runtime.
    /// </summary>
    GatewayNoOutgoingTransitions,

    // WF-20: TimeoutDef validation error codes (NEW graph publishes only).

    /// <summary>
    /// A <c>timeout.duration</c> value does not parse as ISO-8601 duration, or is zero/negative.
    /// </summary>
    TimeoutInvalidDuration,

    /// <summary>
    /// A <c>timeout.remindEveryHours</c> value is present but is &lt;= 0.
    /// </summary>
    TimeoutInvalidRemindEveryHours,

    /// <summary>
    /// A <c>timeout.maxReminders</c> value is present but is &lt; 1.
    /// </summary>
    TimeoutInvalidMaxReminders,

    /// <summary>
    /// A <c>timeout.escalateTo</c> value is present but is whitespace.
    /// </summary>
    TimeoutInvalidEscalateTo,

    /// <summary>
    /// A <c>timeout</c> block was found on a node kind that never mints tasks
    /// (Start, End, Condition, Cc, ParallelGateway, InclusiveGateway, Join, Ack).
    /// Timeouts are only meaningful on Approval nodes.
    /// </summary>
    TimeoutOnNonApprovalNode,

    /// <summary>
    /// A <c>timeout</c> block uses <c>businessCalendar: true</c> with
    /// <c>action ∈ {AutoApprove, AutoReject, Escalate}</c>.
    /// Auto-actions combined with the business-calendar flag are rejected at publish
    /// (the fail-closed arming rule from §0 verdict S2).
    /// </summary>
    TimeoutAutoActionWithBusinessCalendar,

    /// <summary>
    /// A <c>timeout</c> block specifies <c>action ∈ {AutoApprove, AutoReject, Escalate}</c>
    /// while <see cref="WorkFlowOptions.AllowTimerAutoAction"/> is <c>false</c> (the default).
    /// Auto-actions must be explicitly opted-in via options before they may be published.
    /// </summary>
    TimeoutAutoActionGateOff,

    // Security (#296): nodeKey shape + uniqueness enforcement.

    /// <summary>
    /// A nodeKey does not match the allowed identifier pattern
    /// <c>^[\p{L}\p{N}_\-\.]{1,64}$</c>.
    ///
    /// <para>NodeKeys must be safe identifiers: Unicode letters/digits, underscore,
    /// hyphen, and dot only; 1–64 characters.  Brackets, parentheses, angle brackets,
    /// whitespace, and other Markdown/HTML-significant characters are not permitted,
    /// preventing injection of Markdown links into notification card bodies.</para>
    ///
    /// <para>CJK characters are permitted because they match <c>\p{L}</c>.</para>
    /// </summary>
    InvalidNodeKey,

    /// <summary>
    /// Two or more nodes share the same <c>nodeKey</c> value.
    ///
    /// <para>The validator previously silently resolved duplicate keys via HashSet
    /// last-wins semantics; duplicate keys are now a publish-time error because they
    /// indicate a graph authoring mistake that can cause unpredictable engine behaviour.</para>
    /// </summary>
    DuplicateNodeKey,

    // #299: Global schemaVersion gate (behavior change for NEW publishes — existing versions are immutable).

    /// <summary>
    /// The graph's <c>schemaVersion</c> is outside the range supported by the current engine.
    ///
    /// <para>Only versions in the range <c>[1, <see cref="WorkflowGraphSchema.CurrentSchemaVersion"/>]</c>
    /// are accepted.  Graphs with <c>schemaVersion &lt; 1</c> or
    /// <c>schemaVersion &gt; <see cref="WorkflowGraphSchema.CurrentSchemaVersion"/></c>
    /// are rejected at publish and validate time so the engine cannot silently
    /// execute an unsupported document under v1 semantics.</para>
    ///
    /// <para>This check applies to NEW publishes only — published versions are immutable
    /// and their stored <c>SchemaVersion</c> column is not re-validated by the engine.</para>
    /// </summary>
    SchemaVersionUnsupported,
}

/// <summary>
/// Result of publish-time graph validation.
/// </summary>
/// <param name="IsValid">True when the graph passed all validation rules.</param>
/// <param name="Error">The specific error code; <see cref="GraphValidationError.None"/> on success.</param>
/// <param name="ErrorMessage">Human-readable description of the failure; null on success.</param>
public sealed record GraphValidationResult(
    bool IsValid,
    GraphValidationError Error,
    string? ErrorMessage)
{
    /// <summary>Success singleton.</summary>
    public static readonly GraphValidationResult Success =
        new(true, GraphValidationError.None, null);

    /// <summary>Create a failure result.</summary>
    public static GraphValidationResult Fail(GraphValidationError error, string message) =>
        new(false, error, message);
}
