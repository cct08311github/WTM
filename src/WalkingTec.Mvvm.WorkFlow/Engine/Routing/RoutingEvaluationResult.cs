#nullable enable
// WF-11: Closed result type for whitelist routing evaluation.
//
// Fail-closed by design: every error path produces a closed RoutingEvaluationResult
// rather than throwing exceptions to callers (repo Result-style convention for
// recoverable user-authored configuration failures).

namespace WalkingTec.Mvvm.WorkFlow.Engine.Routing;

/// <summary>
/// Closed outcome codes for routing rule evaluation.
/// </summary>
public enum RoutingEvaluationCode
{
    /// <summary>Rule evaluated successfully (may be matched or not-matched).</summary>
    Ok,

    /// <summary>A field referenced in the rule is not in the graph's FieldWhitelist (security error).</summary>
    FieldNotAllowed,

    /// <summary>The operator is not a recognized closed FilterOperator value.</summary>
    UnknownOperator,

    /// <summary>An In/NotIn value list exceeded the 100-item cap.</summary>
    InListTooLarge,

    /// <summary>The form-data value could not be coerced to the whitelisted CLR type (fail-closed).</summary>
    TypeCoercionFailed,

    /// <summary>The CLR type named in the whitelist entry could not be resolved.</summary>
    UnknownClrType,

    /// <summary>Rule structure is invalid (e.g. leaf rule has no field/operator).</summary>
    InvalidRuleStructure,
}

/// <summary>
/// Result of evaluating a single <see cref="Definition.RoutingRuleDef"/> against
/// a deserialized form-data dictionary.
///
/// <para><strong>Fail-closed contract:</strong>
/// Any non-<see cref="RoutingEvaluationCode.Ok"/> code means the rule did NOT match;
/// the engine must treat the branch as unselected and log the code for diagnostics.
/// </para>
/// </summary>
public sealed record RoutingEvaluationResult(
    bool IsMatch,
    RoutingEvaluationCode Code,
    string? ErrorMessage)
{
    /// <summary>Successful match.</summary>
    public static readonly RoutingEvaluationResult Match =
        new(true, RoutingEvaluationCode.Ok, null);

    /// <summary>Successful non-match (no error — rule was evaluated and returned false).</summary>
    public static readonly RoutingEvaluationResult NoMatch =
        new(false, RoutingEvaluationCode.Ok, null);

    /// <summary>Create a fail-closed error result (IsMatch=false, non-Ok code).</summary>
    public static RoutingEvaluationResult Fail(RoutingEvaluationCode code, string message) =>
        new(false, code, message);
}
