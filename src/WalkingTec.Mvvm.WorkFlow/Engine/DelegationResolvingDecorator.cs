#nullable enable
// WF-19 #284.2 — Resolution-time substitution (path A).
//
// Design rationale (§3 path A, FIX-H):
//   • DelegationResolvingDecorator wraps any IApproverResolver (including consumer-registered
//     custom resolvers).  No change to the published IApproverResolver.ResolveAsync signature.
//   • For each base approver A, walks the transitive delegation chain:
//       visited HashSet cycle detection → AdminFallback / FailClose (FIX-H).
//       hop cap from WorkFlowOptions.MaxDelegationHops → stops at last resolved; logs warning.
//       Active rule: IsValid==true AND now ∈ [StartUtc,EndUtc] AND scope matches.
//   • Accumulates into an ordered distinct set (AddIfAbsent).  Delegatee replaces principal;
//     if delegatee is also a direct approver the slot is unified and provenance unioned.
//   • TotalRequired = finalSet.Count, written once in the existing mint transaction.
//     Single-threaded pre-Activation path — no CAS, no torn count.
//   • Provenance is carried via IDelegationContextProvider (secondary interface, no signature
//     change to IApproverResolver).  Mint paths check this interface to stamp DelegatedFromITCode,
//     DelegationRuleId, and DelegationExpiresUtc on each task (AtAssignment semantics).
//
// FIX-H (whole-node FailClose for cycle+no-AdminFallback):
//   When any approver's delegation chain resolves to a cycle with no AdminFallbackITCode,
//   the ENTIRE node is immediately routed through FailClose (ApproverResolution.NoApprover →
//   impossible threshold).  The remaining approvers are NOT processed.  This prevents the
//   silent slot-drop defect where a multi-approver (会签) node could complete with only the
//   non-cycled approvers, causing the cycled approver's authority to vanish undetected.
//
// DI: decorate the innermost IApproverResolver at resolution time (AddWtmWorkFlow wires this).
// Backward-compatible: when no active rule exists for any approver the resolution is identical
// to the inner resolver's result.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

// ── Provenance carrier ────────────────────────────────────────────────────────

/// <summary>
/// Per-approver delegation provenance captured during resolution-time substitution.
/// Stamped onto <see cref="ApprovalTask"/> fields at mint time.
/// </summary>
public sealed record DelegationProvenance(
    /// <summary>Original principal whose authority this slot carries (for DelegatedFromITCode).</summary>
    string OriginalPrincipalITCode,
    /// <summary>Guid of the terminal <see cref="DelegationRule"/> (for DelegationRuleId). Null when not delegated.</summary>
    Guid? RuleId,
    /// <summary>EndUtc snapshot of the terminal rule (for DelegationExpiresUtc). Null when not delegated.</summary>
    DateTime? RuleEndUtc);

/// <summary>
/// Optional secondary interface implemented by <see cref="DelegationResolvingDecorator"/>.
/// Mint paths cast <see cref="IApproverResolver"/> to this interface after calling
/// <see cref="IApproverResolver.ResolveAsync"/> to retrieve per-approver provenance without
/// changing the published <see cref="IApproverResolver"/> contract.
/// </summary>
public interface IDelegationContextProvider
{
    /// <summary>
    /// Returns the provenance map from the most recent <see cref="IApproverResolver.ResolveAsync"/>
    /// call on this instance.  Key = terminal assignee ITCode (case-insensitive).
    /// Null when no delegation took place or <see cref="IApproverResolver.ResolveAsync"/>
    /// has not yet been called.
    /// </summary>
    IReadOnlyDictionary<string, DelegationProvenance>? LastResolutionProvenance { get; }
}

// ── Decorator ─────────────────────────────────────────────────────────────────

/// <summary>
/// <see cref="IApproverResolver"/> decorator that applies resolution-time substitution
/// for standing <see cref="DelegationRule"/> entries (WF-19 §3 path A).
///
/// <para>For each approver A returned by the inner resolver, walks the transitive
/// delegation chain and replaces A with the terminal delegatee.  Human-deduplication
/// is applied: if the terminal delegatee is already a direct approver, one slot is kept
/// and provenance is unioned.  The final ordered distinct set is returned as the
/// resolved approver list.  <see cref="TotalRequired"/> is therefore written once
/// (in the existing mint transaction) with the correct deduped count.</para>
///
/// <para><strong>Cycle detection — whole-node FailClose (FIX-H):</strong> a per-chain
/// <c>visited</c> <see cref="HashSet{T}"/> catches A→B→A and A→A.  When a cycle is detected
/// and <see cref="WorkFlowOptions.AdminFallbackITCode"/> is set, resolution routes to the
/// admin fallback.  When no fallback is configured, <see cref="ResolveAsync"/> immediately
/// returns <see cref="ApproverResolution.NoApprover"/> for the <em>entire node</em> — the
/// remaining approvers are NOT processed.  This prevents the silent slot-drop defect on
/// multi-approver (会签) nodes where only the non-cycled approvers would otherwise be
/// returned, causing the cycled approver's authority to vanish undetected.  The handler's
/// <c>ApplyNoApproverPolicyAsync</c> then fires and sets an impossible threshold so the node
/// blocks for manual admin intervention.</para>
///
/// <para><strong>Hop cap:</strong> when the chain exceeds <see cref="WorkFlowOptions.MaxDelegationHops"/>
/// (default 3), resolution stops at the last resolved delegatee and a Warning is logged.
/// Never throws or loops.</para>
///
/// <para><strong>Determinism guard:</strong> when multiple overlapping active rules exist
/// for the same <c>(Principal, Scope)</c>, the earliest-StartUtc-then-lowest-ID rule is
/// picked and a Warning is logged.  Consumers should enforce uniqueness at the rule-creation
/// layer.</para>
///
/// <para><strong>Provenance:</strong> after <see cref="ResolveAsync"/>, callers can retrieve
/// per-approver provenance via <see cref="IDelegationContextProvider.LastResolutionProvenance"/>
/// (cast to <see cref="IDelegationContextProvider"/>).</para>
/// </summary>
internal sealed class DelegationResolvingDecorator : IApproverResolver, IDelegationContextProvider
{
    private readonly IApproverResolver _inner;
    private readonly WorkFlowOptions _options;
    private readonly ILogger<DelegationResolvingDecorator> _logger;

    // Provenance from the most recent ResolveAsync call (scoped — one instance per request).
    private IReadOnlyDictionary<string, DelegationProvenance>? _lastProvenance;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, DelegationProvenance>? LastResolutionProvenance
        => _lastProvenance;

    public DelegationResolvingDecorator(
        IApproverResolver inner,
        IOptions<WorkFlowOptions> options,
        ILogger<DelegationResolvingDecorator> logger)
    {
        _inner   = inner   ?? throw new ArgumentNullException(nameof(inner));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Calls the inner resolver, then walks transitive delegation chains for each returned
    /// approver.  After this call, provenance is available via
    /// <see cref="IDelegationContextProvider.LastResolutionProvenance"/>.
    /// </remarks>
    public async Task<ApproverResolution> ResolveAsync(
        DbContext db,
        ApproverRuleDef rule,
        NodeInstance nodeInstance,
        string initiatorITCode,
        CancellationToken ct = default)
    {
        _lastProvenance = null; // reset for this call

        if (rule is null) throw new ArgumentNullException(nameof(rule));
        if (nodeInstance is null) throw new ArgumentNullException(nameof(nodeInstance));

        // 1. Inner resolution — may be user/role/managerChain/custom resolver.
        var inner = await _inner.ResolveAsync(db, rule, nodeInstance, initiatorITCode, ct);

        if (inner.Outcome != ResolverOutcome.Resolved)
        {
            // No approvers → pass through unchanged so the handler applies its policy.
            return inner;
        }

        var now = DateTime.UtcNow; // single app-supplied clock; never SQL CURRENT_TIMESTAMP
        var definitionCode = nodeInstance.DefinitionCode; // scope filter
        var tenantCode     = nodeInstance.TenantCode;

        // 2. Ordered distinct set: indexed by lower-cased ITCode for deterministic dedup.
        //    Also track insertion order (List for ordering, HashSet for O(1) membership check).
        var finalList    = new List<string>();
        var finalSet     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var provenanceMap = new Dictionary<string, DelegationProvenance>(StringComparer.OrdinalIgnoreCase);

        foreach (var baseApprover in inner.Approvers)
        {
            if (string.IsNullOrWhiteSpace(baseApprover)) continue;

            // Walk the transitive chain for this base approver.
            var (terminalApprover, ruleId, ruleEndUtc, outcome) =
                await ResolveTransitiveAsync(db, baseApprover, definitionCode, tenantCode, now, ct);

            // Log delegaton cycle / hops-exceeded outcomes as advisory; they do not abort resolution.
            // The FailClose path fires later if terminalApprover is null (AdminFallback not configured).

            if (terminalApprover is null)
            {
                // Cycle with no AdminFallback — FIX-H: route the ENTIRE NODE through FailClose.
                //
                // Design red line: never silently shrink the approver set.  For a multi-approver
                // (会签) node {A, B} where only A's chain cycles, the old `continue` would drop A's
                // slot and return [B], letting the node complete with B alone — A's approval
                // authority would vanish silently.
                //
                // Instead we immediately return NoApprover so the handler's
                // ApplyNoApproverPolicyAsync sets an impossible TotalRequired (int.MaxValue) and
                // the node blocks for manual admin intervention.  We do NOT continue processing
                // the remaining approvers.
                _logger.LogError(
                    "DelegationResolvingDecorator: delegation chain from '{Principal}' on node " +
                    "'{NodeKey}' resolved to a cycle with no AdminFallbackITCode configured. " +
                    "Node fails closed (impossible threshold) — requires manual admin intervention.",
                    baseApprover, nodeInstance.NodeKey);
                _lastProvenance = null;
                return ApproverResolution.NoApprover(
                    $"Delegation chain from '{baseApprover}' resolved to a cycle with no " +
                    "AdminFallbackITCode configured. Node fails closed (impossible threshold) " +
                    "— requires manual admin intervention.");
            }

            // Dedupe: if terminal delegatee already in set (because it's a direct approver or
            // another chain resolved to the same person), keep one slot, union provenance.
            if (!finalSet.Add(terminalApprover))
            {
                // Already present — union provenance (keep first delegation chain's provenance
                // if both are delegated; prefer the direct-approver entry if present).
                if (provenanceMap.TryGetValue(terminalApprover, out var existing) &&
                    existing.RuleId is null && ruleId is not null)
                {
                    // Promote to delegated provenance only if the existing slot had no rule.
                    provenanceMap[terminalApprover] = new DelegationProvenance(baseApprover, ruleId, ruleEndUtc);
                }
                _logger.LogDebug(
                    "DelegationResolvingDecorator: dedupe — '{Terminal}' already in set (base='{Base}'). " +
                    "Keeping one slot.", terminalApprover, baseApprover);
                continue;
            }

            finalList.Add(terminalApprover);

            // Record provenance: ruleId/ruleEndUtc are null when no delegation took place.
            provenanceMap[terminalApprover] = new DelegationProvenance(
                OriginalPrincipalITCode: baseApprover,
                RuleId: ruleId,
                RuleEndUtc: ruleEndUtc);

            if (outcome == ChainOutcome.Delegated)
            {
                _logger.LogDebug(
                    "DelegationResolvingDecorator: '{Base}' delegated to '{Terminal}' on node '{NodeKey}'.",
                    baseApprover, terminalApprover, nodeInstance.NodeKey);
            }
        }

        // 3. Store provenance for the mint path to read via IDelegationContextProvider.
        _lastProvenance = provenanceMap;

        if (finalList.Count == 0)
        {
            // All base approvers were cycled/null → FailClose.
            return ApproverResolution.NoApprover(
                "Delegation resolution produced no valid approvers for node " +
                $"'{nodeInstance.NodeKey}' (cycles with no AdminFallback configured).");
        }

        return ApproverResolution.Success(finalList);
    }

    // ── Transitive chain walker ───────────────────────────────────────────────

    private enum ChainOutcome { Direct, Delegated, Cycle, HopsCapped }

    /// <summary>
    /// Resolve the terminal delegatee for <paramref name="principal"/> by walking the
    /// transitive chain.  Returns (terminalITCode, ruleId, ruleEndUtc, outcome).
    /// Returns (null, null, null, Cycle) when a cycle has no AdminFallback.
    /// </summary>
    private async Task<(string? terminal, Guid? ruleId, DateTime? ruleEndUtc, ChainOutcome outcome)>
        ResolveTransitiveAsync(
            DbContext db,
            string principal,
            string? definitionCode,
            string? tenantCode,
            DateTime now,
            CancellationToken ct)
    {
        var visited  = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { principal };
        var cur      = principal;
        int hops     = 0;
        Guid? terminalRuleId    = null;
        DateTime? terminalEndUtc = null;

        while (true)
        {
            // Find the single active rule for cur in the tenant + scope.
            var rule = await FindActiveRuleAsync(db, cur, definitionCode, tenantCode, now, ct);
            if (rule is null)
            {
                // No active rule — chain ends here.
                return (cur, terminalRuleId, terminalEndUtc,
                    hops > 0 ? ChainOutcome.Delegated : ChainOutcome.Direct);
            }

            var next = rule.DelegateeITCode;

            if (visited.Contains(next))
            {
                // Cycle detected (A→B→A or A→A).
                _logger.LogWarning(
                    "DelegationResolvingDecorator: cycle detected in delegation chain " +
                    "('{Cur}' → '{Next}', chain={Chain}). Routing to AdminFallback.",
                    cur, next, string.Join("→", visited));

                var fallback = _options.AdminFallbackITCode;
                if (string.IsNullOrWhiteSpace(fallback))
                {
                    // No fallback — signal FailClose.
                    return (null, null, null, ChainOutcome.Cycle);
                }

                // Route to admin fallback.
                _logger.LogWarning(
                    "DelegationResolvingDecorator: cycle routing to AdminFallbackITCode='{Fallback}'.",
                    fallback);
                return (fallback, terminalRuleId, terminalEndUtc, ChainOutcome.Cycle);
            }

            if (++hops > _options.MaxDelegationHops)
            {
                // Hop cap exceeded — stop at current resolved delegatee (cur), log warning.
                _logger.LogWarning(
                    "DelegationResolvingDecorator: hop cap ({Max}) exceeded for principal '{Principal}'. " +
                    "Stopping at '{Cur}'.", _options.MaxDelegationHops, principal, cur);
                return (cur, terminalRuleId, terminalEndUtc, ChainOutcome.HopsCapped);
            }

            visited.Add(next);
            // Advance: the terminal rule becomes the one that took us to `next`.
            terminalRuleId   = rule.ID;
            terminalEndUtc   = rule.EndUtc;
            cur = next;
        }
    }

    // ── Active rule lookup ────────────────────────────────────────────────────

    /// <summary>
    /// Query the single active <see cref="DelegationRule"/> for <paramref name="principalITCode"/>.
    /// Active = IsValid AND now ∈ [StartUtc, EndUtc] AND scope matches.
    ///
    /// <para>Determinism guard: when multiple overlapping rules match, picks the earliest
    /// StartUtc then lowest ID.  A Warning is logged so administrators can fix the overlap.</para>
    ///
    /// <para>Tenant-scoped: only rules for <paramref name="tenantCode"/> are considered,
    /// consistent with the global HasQueryFilter on ITenant entities.</para>
    /// </summary>
    private async Task<DelegationRule?> FindActiveRuleAsync(
        DbContext db,
        string principalITCode,
        string? definitionCode,
        string? tenantCode,
        DateTime now,
        CancellationToken ct)
    {
        // Build base query: tenant-scoped, principal match, IsValid, within window.
        var query = db.Set<DelegationRule>()
            .AsNoTracking()
            .Where(r => r.IsValid
                     && r.PrincipalITCode == principalITCode
                     && r.StartUtc <= now
                     && r.EndUtc   >= now);

        // Tenant scope: null tenantCode matches all tenants (single-tenant setups).
        if (tenantCode is not null)
            query = query.Where(r => r.TenantCode == tenantCode);

        // Scope filter: null ScopeDefinitionCode = all workflows; specific code = only that workflow.
        if (!string.IsNullOrWhiteSpace(definitionCode))
            query = query.Where(r => r.ScopeDefinitionCode == null
                                  || r.ScopeDefinitionCode == definitionCode);
        else
            query = query.Where(r => r.ScopeDefinitionCode == null);

        // Ordered for deterministic tie-breaking.
        var candidates = await query
            .OrderBy(r => r.StartUtc)
            .ThenBy(r => r.ID)
            .Take(2) // only need first 2 to detect ambiguity
            .ToListAsync(ct);

        if (candidates.Count == 0) return null;

        if (candidates.Count > 1)
        {
            // Multiple overlapping active rules — non-determinism risk.
            _logger.LogWarning(
                "DelegationResolvingDecorator: multiple active DelegationRules for principal " +
                "'{Principal}' (scope='{Scope}', tenant='{Tenant}'). " +
                "Picking rule {RuleId} (earliest StartUtc={Start}). " +
                "Fix: ensure at most one active rule per (Principal, Scope) at any time.",
                principalITCode, definitionCode ?? "<any>", tenantCode ?? "<any>",
                candidates[0].ID, candidates[0].StartUtc);
        }

        return candidates[0];
    }
}
