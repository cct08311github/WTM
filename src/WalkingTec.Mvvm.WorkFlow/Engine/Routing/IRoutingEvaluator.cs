#nullable enable
// WF-11: Routing evaluator interface.
//
// The IRoutingEvaluator takes a RoutingRuleDef, validates it against the
// per-version FieldWhitelist, compiles a cached Expression<Func<IDictionary<string,object?>,bool>>,
// and evaluates it in-memory against the deserialized FormDataJson dictionary.
//
// Registered as SINGLETON (compiles-and-caches by ContentHash); engine receives it
// via DI constructor injection.

using System.Collections.Generic;
using WalkingTec.Mvvm.WorkFlow.Definition;

namespace WalkingTec.Mvvm.WorkFlow.Engine.Routing;

/// <summary>
/// Evaluates a <see cref="RoutingRuleDef"/> in-memory against a deserialized
/// <see cref="Models.ProcessInstance.FormDataJson"/> dictionary.
///
/// <para><strong>Security guarantees (spec §6, W7):</strong>
/// <list type="bullet">
///   <item>Every referenced field MUST be present in <paramref name="whitelist"/>; off-list → fail-closed.</item>
///   <item>Only the closed <see cref="FilterOperator"/> enum is accepted.</item>
///   <item>In/NotIn value lists are capped at 100 items.</item>
///   <item>Missing or un-coercible form-data values → fail-closed (no match, no exception to caller).</item>
///   <item>No reflection, no Roslyn, no DynamicLinq, no string eval — positive enumeration only.</item>
/// </list>
/// </para>
///
/// <para><strong>Caching:</strong> compiled delegates are cached by a deterministic content hash
/// derived from the rule JSON; the same rule always reuses the same compiled delegate.
/// </para>
/// </summary>
public interface IRoutingEvaluator
{
    /// <summary>
    /// Validate and evaluate <paramref name="rule"/> against <paramref name="formData"/>.
    ///
    /// <para>Validation (whitelist + closed operator + In cap) is performed before
    /// any expression is compiled or evaluated (defense-in-depth; publish-time
    /// validation is the first gate).</para>
    ///
    /// <para>Returns <see cref="RoutingEvaluationResult.Fail"/> (IsMatch=false) for
    /// any security or structural violation — never throws to the caller for
    /// user-authored rule problems.</para>
    /// </summary>
    /// <param name="rule">The routing rule predicate to evaluate.</param>
    /// <param name="whitelist">
    ///   The per-version field whitelist from
    ///   <see cref="WorkflowGraph.FieldWhitelist"/>. Used for both validation and CLR-type coercion.
    /// </param>
    /// <param name="formData">
    ///   Deserialized form-data dictionary from <see cref="Models.ProcessInstance.FormDataJson"/>.
    ///   Null values and missing keys are treated as fail-closed (no match).
    /// </param>
    /// <returns>Closed <see cref="RoutingEvaluationResult"/>.</returns>
    RoutingEvaluationResult Evaluate(
        RoutingRuleDef rule,
        IReadOnlyList<FieldWhitelistEntry> whitelist,
        IReadOnlyDictionary<string, object?> formData);
}
