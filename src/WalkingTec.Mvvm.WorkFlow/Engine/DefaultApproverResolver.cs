#nullable enable
// WF-8: DefaultApproverResolver — concrete IApproverResolver.
//
// Resolution kinds (MVP):
//   "User"         — single ITCode from ApproverRuleDef.Value (or comma-separated list in Value).
//   "Role"         — query FrameworkUserRoles WHERE RoleCode == rule.Value (+ tenant filter).
//   "ManagerChain" — delegate to IManagerChainProvider, cap at MaxLevel.
//
// Caps / dedupe:
//   • MaxLevel: taken from rule.MaxLevel if set, else WorkFlowOptions.MaxLevel.
//   • Human-dedupe: same ITCode appearing more than once in the resolved list is dropped
//     after the first occurrence (order-stable; logged via ILogger).
//   • Empty result after dedup → ResolverOutcome.NoApprover (never silent deadlock).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

/// <summary>
/// Default <see cref="IApproverResolver"/> implementation.
/// Registered as scoped by <see cref="ServiceCollectionExtensions.AddWtmWorkFlow"/>.
///
/// <para>Supports rule types "User", "Role", and "ManagerChain" for the MVP wave.
/// Unknown types return <see cref="ResolverOutcome.UnsupportedRuleType"/>.</para>
/// </summary>
internal sealed class DefaultApproverResolver : IApproverResolver
{
    private readonly WorkFlowOptions _options;
    private readonly IManagerChainProvider _managerChainProvider;
    private readonly ILogger<DefaultApproverResolver> _logger;

    public DefaultApproverResolver(
        IOptions<WorkFlowOptions> options,
        IManagerChainProvider managerChainProvider,
        ILogger<DefaultApproverResolver> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _managerChainProvider = managerChainProvider ?? throw new ArgumentNullException(nameof(managerChainProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<ApproverResolution> ResolveAsync(
        DbContext db,
        ApproverRuleDef rule,
        NodeInstance nodeInstance,
        string initiatorITCode,
        CancellationToken ct = default)
    {
        if (rule is null) throw new ArgumentNullException(nameof(rule));
        if (nodeInstance is null) throw new ArgumentNullException(nameof(nodeInstance));
        if (string.IsNullOrWhiteSpace(initiatorITCode))
            throw new ArgumentException("initiatorITCode must not be empty.", nameof(initiatorITCode));

        var tenantCode = nodeInstance.TenantCode;

        IReadOnlyList<string> raw = rule.Type switch
        {
            "User" => ResolveUser(rule),
            "Role" => await ResolveRoleAsync(db, rule, tenantCode, ct),
            "ManagerChain" => await ResolveManagerChainAsync(db, rule, initiatorITCode, tenantCode, ct),
            _ => Array.Empty<string>(),
        };

        if (rule.Type is not ("User" or "Role" or "ManagerChain"))
        {
            _logger.LogWarning(
                "ApproverResolver: unsupported rule type '{RuleType}' for node '{NodeKey}'.",
                rule.Type, nodeInstance.NodeKey);
            return ApproverResolution.Unsupported(rule.Type);
        }

        // Human-dedupe: stable order, remove duplicate ITCodes.
        var deduped = Deduplicate(raw, nodeInstance.NodeKey);

        if (deduped.Count == 0)
        {
            _logger.LogWarning(
                "ApproverResolver: resolved 0 approvers for node '{NodeKey}' (rule type '{RuleType}').",
                nodeInstance.NodeKey, rule.Type);
            return ApproverResolution.NoApprover(
                $"Rule type '{rule.Type}' resolved no approvers for node '{nodeInstance.NodeKey}'.");
        }

        return ApproverResolution.Success(deduped);
    }

    // ── Private resolution helpers ─────────────────────────────────────────────

    private static IReadOnlyList<string> ResolveUser(ApproverRuleDef rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Value))
            return Array.Empty<string>();

        // Support comma-separated ITCode list in Value (convenience for tests / simple graphs).
        return rule.Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static async Task<IReadOnlyList<string>> ResolveRoleAsync(
        DbContext db,
        ApproverRuleDef rule,
        string? tenantCode,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rule.Value))
            return Array.Empty<string>();

        // Query FrameworkUserRole: all users in the named role for this tenant.
        // Apply tenant filter manually (engine's DbContext may not have HasQueryFilter on RBAC tables).
        var query = db.Set<FrameworkUserRole>()
            .AsNoTracking()
            .Where(r => r.RoleCode == rule.Value);

        if (tenantCode is not null)
            query = query.Where(r => r.TenantCode == tenantCode);

        var codes = await query
            .Select(r => r.UserCode)
            .Distinct()
            .OrderBy(c => c) // deterministic ordering (spec §4 prompt-cache discipline)
            .ToListAsync(ct);

        return codes;
    }

    private async Task<IReadOnlyList<string>> ResolveManagerChainAsync(
        DbContext db,
        ApproverRuleDef rule,
        string initiatorITCode,
        string? tenantCode,
        CancellationToken ct)
    {
        // MaxLevel: rule-level override takes precedence; fall back to global option.
        var maxLevel = rule.MaxLevel ?? _options.MaxLevel;
        if (maxLevel <= 0) maxLevel = 1;

        var chain = await _managerChainProvider.GetManagerChainAsync(
            db, initiatorITCode, maxLevel, tenantCode, ct);

        return chain;
    }

    // ── Deduplication ─────────────────────────────────────────────────────────

    private List<string> Deduplicate(IReadOnlyList<string> input, string nodeKey)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(input.Count);

        foreach (var code in input)
        {
            if (string.IsNullOrWhiteSpace(code)) continue;

            if (!seen.Add(code))
            {
                _logger.LogDebug(
                    "ApproverResolver: duplicate ITCode '{ITCode}' skipped in node '{NodeKey}' approver list.",
                    code, nodeKey);
                continue;
            }

            result.Add(code);
        }

        return result;
    }
}

// ── DefaultManagerChainProvider ───────────────────────────────────────────────

/// <summary>
/// Default no-op implementation of <see cref="IManagerChainProvider"/>.
///
/// <para><see cref="FrameworkUserBase"/> does not define a ManagerCode property.
/// This provider returns an empty chain, which causes the ManagerChain resolution path
/// to yield <see cref="ResolverOutcome.NoApprover"/>.  Consumers who have extended
/// <c>FrameworkUserBase</c> with an org-chart column should replace this registration
/// with their own implementation.</para>
/// </summary>
public sealed class DefaultManagerChainProvider : IManagerChainProvider
{
    private readonly ILogger<DefaultManagerChainProvider> _logger;

    public DefaultManagerChainProvider(ILogger<DefaultManagerChainProvider> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> GetManagerChainAsync(
        DbContext db,
        string employeeITCode,
        int maxLevel,
        string? tenantCode,
        CancellationToken ct = default)
    {
        // FrameworkUserBase has no ManagerCode — consumers must register a real provider.
        // Returning empty causes the NoApprover admin-fallback path to fire.
        _logger.LogWarning(
            "DefaultManagerChainProvider: no org-chart source configured. " +
            "ManagerChain resolution for '{ITCode}' returns empty. " +
            "Register a real IManagerChainProvider implementation to enable manager-chain approvals.",
            employeeITCode);

        IReadOnlyList<string> empty = Array.Empty<string>();
        return Task.FromResult(empty);
    }
}
