#nullable enable
// WF-8: IApproverResolver — lazy per-step approver resolution contract.
//
// Design:
//   • Resolution is LAZY: called once per step as the Sequential pointer advances,
//     not all upfront.  This ensures ManagerChain resolution uses live org data
//     at the time each step becomes active.
//   • Closed outcome type: ResolverOutcome / ApproverResolution — never free-form strings
//     for control flow (spec §9 discipline).
//   • Three resolution kinds in MVP:
//       "User"         — one explicit ITCode from ApproverRuleDef.Value
//       "Role"         — all members of the named role (FrameworkUserRole table)
//       "ManagerChain" — initiator's manager chain, capped by MaxLevel
//   • Mandatory caps: MaxLevel for ManagerChain (from rule or WorkFlowOptions.MaxLevel).
//   • Human-dedupe: same ITCode appearing twice in a resolved list is skipped + logged.
//   • Admin-fallback result code when resolution yields nobody — never a silent deadlock.
//
// FrameworkUser does NOT have a ManagerCode column in the base schema; ManagerChain
// resolution uses a pluggable IManagerChainProvider interface so consumers can inject
// their own org-chart source.  The default (DefaultManagerChainProvider) returns an
// empty chain, which triggers the admin-fallback path.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

// ── Outcome types ─────────────────────────────────────────────────────────────

/// <summary>
/// Closed outcome code for approver resolution.
/// Callers branch on this — never on free-form strings.
/// </summary>
public enum ResolverOutcome
{
    /// <summary>One or more approvers were successfully resolved.</summary>
    Resolved,

    /// <summary>
    /// Resolution yielded no approvers.  The caller must apply the
    /// <see cref="AutoApproveOnMissingHandlerPolicy"/> from <see cref="WorkFlowOptions"/>
    /// (auto-approve, escalate to admin, or fail-close) — never a silent deadlock.
    /// </summary>
    NoApprover,

    /// <summary>
    /// The resolution rule type is unknown or not supported in this wave.
    /// Treated as NoApprover by the engine.
    /// </summary>
    UnsupportedRuleType,
}

/// <summary>
/// Result of a single approver resolution call.
/// <see cref="Outcome"/> drives control flow; <see cref="Approvers"/> is the list
/// when <see cref="Outcome"/> == <see cref="ResolverOutcome.Resolved"/>.
/// </summary>
public sealed record ApproverResolution(
    ResolverOutcome Outcome,
    IReadOnlyList<string> Approvers,
    string? Detail = null)
{
    /// <summary>Shorthand for a resolved list.</summary>
    public static ApproverResolution Success(IReadOnlyList<string> approvers)
        => new(ResolverOutcome.Resolved, approvers);

    /// <summary>Shorthand for no-approver outcome.</summary>
    public static ApproverResolution NoApprover(string? detail = null)
        => new(ResolverOutcome.NoApprover, System.Array.Empty<string>(), detail);

    /// <summary>Shorthand for unsupported rule type.</summary>
    public static ApproverResolution Unsupported(string ruleType)
        => new(ResolverOutcome.UnsupportedRuleType, System.Array.Empty<string>(),
               $"Unsupported approver rule type: '{ruleType}'.");
}

// ── IApproverResolver ─────────────────────────────────────────────────────────

/// <summary>
/// Resolves the ordered list of approvers for a given <see cref="ApproverRuleDef"/>
/// lazily (called once per Sequential step, not all upfront).
///
/// <para><strong>Mandatory invariants (spec §5.1 / §5.5):</strong>
/// <list type="bullet">
///   <item>Resolution is lazy — called per step as the Sequential pointer advances.</item>
///   <item>ManagerChain is capped by <see cref="WorkFlowOptions.MaxLevel"/> (or rule-level override).</item>
///   <item>Human-dedupe: the same ITCode appearing more than once is silently removed after the first occurrence.</item>
///   <item>Empty result → <see cref="ResolverOutcome.NoApprover"/> (never a silent deadlock).</item>
/// </list>
/// </para>
///
/// <para><strong>Supported rule types (MVP):</strong> "User", "Role", "ManagerChain".
/// Unknown types return <see cref="ResolverOutcome.UnsupportedRuleType"/>.</para>
/// </summary>
public interface IApproverResolver
{
    /// <summary>
    /// Resolve approvers for the given <paramref name="rule"/> in the context of
    /// <paramref name="nodeInstance"/> (provides tenant, mode, etc.) and
    /// <paramref name="initiatorITCode"/> (needed for ManagerChain traversal).
    /// </summary>
    /// <param name="db">DbContext for RBAC queries (FrameworkUserRole).</param>
    /// <param name="rule">The rule definition from the graph node.</param>
    /// <param name="nodeInstance">The runtime node instance being processed.</param>
    /// <param name="initiatorITCode">ITCode of the process initiator (for ManagerChain).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ApproverResolution> ResolveAsync(
        DbContext db,
        ApproverRuleDef rule,
        NodeInstance nodeInstance,
        string initiatorITCode,
        CancellationToken ct = default);
}

// ── IManagerChainProvider ─────────────────────────────────────────────────────

/// <summary>
/// Pluggable org-chart provider for ManagerChain resolution.
///
/// <para>The base WTM <c>FrameworkUser</c> does not have a ManagerCode column.
/// Consumers who have extended <c>FrameworkUserBase</c> with a manager field should
/// implement this interface and register it in DI to enable real ManagerChain resolution.
/// The default registration (<see cref="DefaultManagerChainProvider"/>) returns an empty
/// chain, which triggers the admin-fallback path and logs a warning.</para>
/// </summary>
public interface IManagerChainProvider
{
    /// <summary>
    /// Return the ordered list of manager ITCodes for <paramref name="employeeITCode"/>,
    /// starting from the immediate manager, up to <paramref name="maxLevel"/> levels.
    ///
    /// Returns an empty list if the employee has no manager or if the org chart is not available.
    /// Never throws — return empty list on any lookup failure and log internally.
    /// </summary>
    Task<IReadOnlyList<string>> GetManagerChainAsync(
        DbContext db,
        string employeeITCode,
        int maxLevel,
        string? tenantCode,
        CancellationToken ct = default);
}
