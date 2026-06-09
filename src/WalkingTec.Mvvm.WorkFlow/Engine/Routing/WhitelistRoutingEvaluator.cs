#nullable enable
// WF-11: WhitelistRoutingEvaluator — sandboxed conditional routing.
//
// Security model (spec §6, W7):
//   • Field whitelist per version — every referenced field MUST be in the FieldWhitelist.
//   • Closed FilterOperator enum — only the 10 recognized operators are accepted.
//   • In/NotIn capped at 100 items (mirrors AnalysisQueryEngine.Filters.cs:84).
//   • Missing or un-coercible form-data values → fail-closed (NoMatch, not an exception).
//   • No reflection over arbitrary members, no Roslyn, no DynamicLinq, no string-eval.
//   • Injection-proof by construction: positive enumeration over a closed-operator / whitelisted-field pair.
//
// Compiled Expression<Func<IDictionary<string,object?>, bool>> delegates are cached
// by a deterministic content-hash of the rule JSON so repeated evaluation of the
// same rule never recompiles.
//
// Registered as SINGLETON in AddWtmWorkFlow.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.WorkFlow.Definition;

namespace WalkingTec.Mvvm.WorkFlow.Engine.Routing;

/// <summary>
/// Production implementation of <see cref="IRoutingEvaluator"/>.
/// Singleton: caches compiled predicates by deterministic rule content-hash.
/// </summary>
public sealed class WhitelistRoutingEvaluator : IRoutingEvaluator
{
    // Delegate cache: rule content-hash → compiled predicate.
    // ConcurrentDictionary is safe for singleton use from multiple concurrent scoped requests.
    private readonly ConcurrentDictionary<string, Func<IReadOnlyDictionary<string, object?>, bool>>
        _cache = new(StringComparer.Ordinal);

    private readonly ILogger<WhitelistRoutingEvaluator> _logger;

    // Hard limit matching Analysis engine cap (AnalysisQueryEngine.Filters.cs:84).
    internal const int MaxInListSize = 100;

    public WhitelistRoutingEvaluator(ILogger<WhitelistRoutingEvaluator> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public RoutingEvaluationResult Evaluate(
        RoutingRuleDef rule,
        IReadOnlyList<FieldWhitelistEntry> whitelist,
        IReadOnlyDictionary<string, object?> formData)
    {
        if (rule is null) throw new ArgumentNullException(nameof(rule));
        if (whitelist is null) throw new ArgumentNullException(nameof(whitelist));
        if (formData is null) throw new ArgumentNullException(nameof(formData));

        // Validate BEFORE compiling — defense in depth; publish-time is the first gate.
        var validationError = ValidateRule(rule, whitelist);
        if (validationError is not null)
            return validationError;

        // Get or compile the predicate.
        var hash = ComputeRuleHash(rule);
        var predicate = _cache.GetOrAdd(hash, _ => CompileRule(rule, whitelist));

        // Evaluate fail-closed: any exception in the predicate → no-match.
        try
        {
            bool matched = predicate(formData);
            return matched ? RoutingEvaluationResult.Match : RoutingEvaluationResult.NoMatch;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "WhitelistRoutingEvaluator: predicate evaluation threw unexpectedly (hash={Hash}). Fail-closed.",
                hash);
            return RoutingEvaluationResult.Fail(
                RoutingEvaluationCode.TypeCoercionFailed,
                $"Predicate evaluation error: {ex.Message}");
        }
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Validate a rule (and all sub-rules recursively) against the whitelist.
    /// Returns a fail result on the first violation, or null when validation passes.
    /// </summary>
    internal static RoutingEvaluationResult? ValidateRule(
        RoutingRuleDef rule,
        IReadOnlyList<FieldWhitelistEntry> whitelist)
    {
        // Composite AND rule
        if (rule.And is { Count: > 0 })
        {
            foreach (var sub in rule.And)
            {
                var err = ValidateRule(sub, whitelist);
                if (err is not null) return err;
            }
            return null;
        }

        // Composite OR rule
        if (rule.Or is { Count: > 0 })
        {
            foreach (var sub in rule.Or)
            {
                var err = ValidateRule(sub, whitelist);
                if (err is not null) return err;
            }
            return null;
        }

        // Leaf rule — must have field + operator.
        if (string.IsNullOrWhiteSpace(rule.Field))
            return RoutingEvaluationResult.Fail(
                RoutingEvaluationCode.InvalidRuleStructure,
                "Leaf routing rule must specify a 'field'.");

        if (rule.Operator is null)
            return RoutingEvaluationResult.Fail(
                RoutingEvaluationCode.InvalidRuleStructure,
                $"Leaf routing rule for field '{rule.Field}' must specify an 'operator'.");

        // Whitelist check — security gate.
        var entry = FindWhitelistEntry(rule.Field, whitelist);
        if (entry is null)
            return RoutingEvaluationResult.Fail(
                RoutingEvaluationCode.FieldNotAllowed,
                $"Field '{rule.Field}' is not in the graph FieldWhitelist. " +
                "Off-whitelist field access is not permitted (spec §6 whitelist discipline).");

        // In/NotIn cap check.
        if (rule.Operator is FilterOperator.In or FilterOperator.NotIn)
        {
            var listCount = GetInListCount(rule.Value);
            if (listCount > MaxInListSize)
                return RoutingEvaluationResult.Fail(
                    RoutingEvaluationCode.InListTooLarge,
                    $"In/NotIn value list for field '{rule.Field}' has {listCount} items; " +
                    $"maximum is {MaxInListSize} (spec §6 In cap).");
        }

        // CLR type must be resolvable.
        if (ResolveClrType(entry.ClrType) is null)
            return RoutingEvaluationResult.Fail(
                RoutingEvaluationCode.UnknownClrType,
                $"CLR type '{entry.ClrType}' for field '{entry.Field}' could not be resolved.");

        return null; // All checks pass.
    }

    // ── Compilation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Compile a validated <see cref="RoutingRuleDef"/> into a
    /// <c>Func&lt;IReadOnlyDictionary&lt;string,object?&gt;, bool&gt;</c>.
    ///
    /// Only called after <see cref="ValidateRule"/> passes; the whitelist and
    /// entry types are therefore already verified.
    /// </summary>
    private static Func<IReadOnlyDictionary<string, object?>, bool> CompileRule(
        RoutingRuleDef rule,
        IReadOnlyList<FieldWhitelistEntry> whitelist)
    {
        // Parameter: IDictionary<string, object?> formData
        var param = Expression.Parameter(
            typeof(IReadOnlyDictionary<string, object?>), "formData");

        var body = BuildExpression(rule, whitelist, param);
        var lambda = Expression.Lambda<Func<IReadOnlyDictionary<string, object?>, bool>>(body, param);
        return lambda.Compile();
    }

    private static Expression BuildExpression(
        RoutingRuleDef rule,
        IReadOnlyList<FieldWhitelistEntry> whitelist,
        ParameterExpression param)
    {
        // Composite AND: short-circuit all sub-expressions.
        if (rule.And is { Count: > 0 })
        {
            Expression combined = Expression.Constant(true);
            foreach (var sub in rule.And)
                combined = Expression.AndAlso(combined, BuildExpression(sub, whitelist, param));
            return combined;
        }

        // Composite OR: short-circuit any sub-expression.
        if (rule.Or is { Count: > 0 })
        {
            Expression combined = Expression.Constant(false);
            foreach (var sub in rule.Or)
                combined = Expression.OrElse(combined, BuildExpression(sub, whitelist, param));
            return combined;
        }

        // Leaf rule — build lookup + compare expression.
        // ValidateRule already ensured field != null, operator != null, entry != null.
        var entry = FindWhitelistEntry(rule.Field!, whitelist)!;
        var targetType = ResolveClrType(entry.ClrType)!;
        var op = rule.Operator!.Value;

        return BuildLeafExpression(param, rule.Field!, targetType, op, rule.Value);
    }

    /// <summary>
    /// Build: TryGetValue(field, out val) ? Compare(CoerceOrDefault(val, targetType), ruleValue) : false
    ///
    /// Missing field → fail-closed (false). Un-coercible value → fail-closed (false).
    /// Both paths are handled inside the compiled expression via a static helper method
    /// rather than try/catch in the Expression tree (which is not supported in compiled lambdas).
    /// </summary>
    private static Expression BuildLeafExpression(
        ParameterExpression param,
        string field,
        Type targetType,
        FilterOperator op,
        object? ruleValue)
    {
        // We call the static helper: EvaluateLeaf(formData, field, clrTypeName, op, ruleValue)
        // This keeps the Expression tree simple and avoids exception-handling in the tree.
        var method = typeof(WhitelistRoutingEvaluator)
            .GetMethod(nameof(EvaluateLeaf),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

        return Expression.Call(
            method,
            param,
            Expression.Constant(field, typeof(string)),
            Expression.Constant(targetType.FullName ?? targetType.Name, typeof(string)),
            Expression.Constant((int)op, typeof(int)),
            Expression.Constant(ruleValue, typeof(object)));
    }

    /// <summary>
    /// Static leaf evaluator invoked by compiled Expression trees.
    /// Handles: missing key → false; coercion failure → false; all 10 operators.
    /// </summary>
    private static bool EvaluateLeaf(
        IReadOnlyDictionary<string, object?> formData,
        string field,
        string clrTypeName,
        int opInt,
        object? ruleValue)
    {
        // Missing field → fail-closed (spec §6: "missing field → false → default").
        if (!formData.TryGetValue(field, out var rawValue))
            return false;

        var targetType = ResolveClrType(clrTypeName);
        if (targetType is null)
            return false; // Unknown CLR type — fail-closed.

        // Coerce the raw form-data value to the target type.
        object? typedValue = SafeChangeType(rawValue, targetType);
        if (typedValue is null && rawValue is not null)
            return false; // Coercion failed — fail-closed.

        var op = (FilterOperator)opInt;

        return op switch
        {
            FilterOperator.Eq         => AreEqual(typedValue, SafeChangeType(ruleValue, targetType), targetType),
            FilterOperator.NotEq      => !AreEqual(typedValue, SafeChangeType(ruleValue, targetType), targetType),
            FilterOperator.Gt         => Compare(typedValue, SafeChangeType(ruleValue, targetType), targetType) > 0,
            FilterOperator.Gte        => Compare(typedValue, SafeChangeType(ruleValue, targetType), targetType) >= 0,
            FilterOperator.Lt         => Compare(typedValue, SafeChangeType(ruleValue, targetType), targetType) < 0,
            FilterOperator.Lte        => Compare(typedValue, SafeChangeType(ruleValue, targetType), targetType) <= 0,
            FilterOperator.Contains   => ContainsSubstring(typedValue, ruleValue),
            FilterOperator.NotContains => !ContainsSubstring(typedValue, ruleValue),
            FilterOperator.In         => InList(typedValue, ruleValue, targetType),
            FilterOperator.NotIn      => !InList(typedValue, ruleValue, targetType),
            _                         => false, // Unknown operator — fail-closed.
        };
    }

    // ── Operator implementations ──────────────────────────────────────────────

    private static bool AreEqual(object? a, object? b, Type targetType)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        // Use IComparable for value types when available; fall back to Equals.
        if (a is IComparable ca)
            return ca.CompareTo(b) == 0;
        return a.Equals(b);
    }

    private static int Compare(object? a, object? b, Type targetType)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        if (a is IComparable ca)
            return ca.CompareTo(b);
        // Non-comparable — fall back to string comparison as last resort.
        return string.Compare(a.ToString(), b?.ToString(), StringComparison.Ordinal);
    }

    private static bool ContainsSubstring(object? value, object? pattern)
    {
        if (value is null || pattern is null) return false;
        var str = value.ToString() ?? string.Empty;
        var pat = pattern.ToString() ?? string.Empty;
        return str.Contains(pat, StringComparison.OrdinalIgnoreCase);
    }

    private static bool InList(object? value, object? listObj, Type targetType)
    {
        if (value is null || listObj is null) return false;

        // listObj may be a JsonElement (array), IEnumerable<object?>, or object[].
        var items = ExtractListItems(listObj, targetType);
        foreach (var item in items)
        {
            if (AreEqual(value, item, targetType))
                return true;
        }
        return false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IEnumerable<object?> ExtractListItems(object listObj, Type targetType)
    {
        // JsonElement array (from STJ deserialization of RoutingRuleDef.Value).
        if (listObj is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            foreach (var elem in je.EnumerateArray())
            {
                yield return SafeChangeType(JsonElementToObject(elem), targetType);
            }
            yield break;
        }

        // IEnumerable<object?> (e.g. from test construction).
        if (listObj is System.Collections.IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
                yield return SafeChangeType(item, targetType);
            yield break;
        }

        // Single value (degenerate case) — treat as one-element list.
        yield return SafeChangeType(listObj, targetType);
    }

    private static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String  => element.GetString(),
            JsonValueKind.Number  => element.TryGetDecimal(out var d) ? (object)d : element.GetDouble(),
            JsonValueKind.True    => true,
            JsonValueKind.False   => false,
            JsonValueKind.Null    => null,
            _                     => element.GetRawText(),
        };
    }

    /// <summary>
    /// Safe Convert.ChangeType wrapper. Returns null on failure instead of throwing.
    /// </summary>
    internal static object? SafeChangeType(object? value, Type? targetType)
    {
        if (targetType is null) return null;
        if (value is null) return null;

        // Already the right type.
        if (targetType.IsInstanceOfType(value))
            return value;

        // Unwrap JsonElement to a plain CLR value first.
        if (value is JsonElement je)
            value = JsonElementToObject(je);

        if (value is null) return null;

        try
        {
            // Handle nullable types — convert to underlying type.
            var convType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            return Convert.ChangeType(value, convType);
        }
        catch
        {
            // Coercion failure → fail-closed (caller treats null as "no match").
            return null;
        }
    }

    private static FieldWhitelistEntry? FindWhitelistEntry(
        string field,
        IReadOnlyList<FieldWhitelistEntry> whitelist)
    {
        foreach (var entry in whitelist)
        {
            if (string.Equals(entry.Field, field, StringComparison.Ordinal))
                return entry;
        }
        return null;
    }

    /// <summary>
    /// Resolve a CLR type from its full name or assembly-qualified name.
    /// Supports a closed set of commonly-used types to avoid arbitrary type loading.
    /// </summary>
    internal static Type? ResolveClrType(string? clrTypeName)
    {
        if (string.IsNullOrWhiteSpace(clrTypeName)) return null;

        return clrTypeName switch
        {
            "System.String"   or "String"   => typeof(string),
            "System.Boolean"  or "Boolean"  => typeof(bool),
            "System.Byte"     or "Byte"     => typeof(byte),
            "System.Int16"    or "Int16"    => typeof(short),
            "System.Int32"    or "Int32"    => typeof(int),
            "System.Int64"    or "Int64"    => typeof(long),
            "System.Single"   or "Single"   => typeof(float),
            "System.Double"   or "Double"   => typeof(double),
            "System.Decimal"  or "Decimal"  => typeof(decimal),
            "System.DateTime" or "DateTime" => typeof(DateTime),
            "System.Guid"     or "Guid"     => typeof(Guid),
            _                               => null, // Unknown type — fail-closed at validation.
        };
    }

    private static int GetInListCount(object? value)
    {
        if (value is null) return 0;

        if (value is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            int count = 0;
            foreach (var _ in je.EnumerateArray()) count++;
            return count;
        }

        if (value is System.Collections.ICollection col)
            return col.Count;

        if (value is System.Collections.IEnumerable en and not string)
        {
            int count = 0;
            foreach (var _ in en) count++;
            return count;
        }

        return 1; // Single scalar — count as 1.
    }

    // ── Rule content-hash ─────────────────────────────────────────────────────

    /// <summary>
    /// Compute a deterministic SHA-256 hash of the rule's JSON representation.
    /// Used as the cache key for compiled predicates.
    /// </summary>
    private static string ComputeRuleHash(RoutingRuleDef rule)
    {
        // Serialize with sorted keys (same deterministic approach as WorkflowGraphSerializer).
        var json = JsonSerializer.Serialize(rule, new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        });

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }
}
