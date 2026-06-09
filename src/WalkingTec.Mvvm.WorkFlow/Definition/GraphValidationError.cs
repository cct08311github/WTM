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
