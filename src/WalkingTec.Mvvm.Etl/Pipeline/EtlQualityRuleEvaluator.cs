#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace WalkingTec.Mvvm.Etl.Pipeline;

/// <summary>
/// 純記憶體規則評估器 — 對 <see cref="DataTable"/> 套用
/// <see cref="EtlQualityRule"/> 集合，依 <see cref="EtlQualityRuleAction"/>
/// 決定要剔除違規列、繼續、還是 throw。
/// </summary>
public static class EtlQualityRuleEvaluator
{
    /// <summary>違規範例累計上限，避免 OOM 與爆量 log。</summary>
    public const int MaxFailureSamples = 20;

    private static readonly ConcurrentDictionary<string, Regex> _regexCache = new();

    /// <summary>
    /// 依規則套用至 <paramref name="table"/>，回傳新的 DataTable（或原表，
    /// 若 Action = Continue 則就地不改）。違規列數量寫到
    /// <paramref name="failedRows"/>，違規範例（最多 <see cref="MaxFailureSamples"/>
    /// 筆）寫到 <paramref name="failureSamples"/>。
    /// </summary>
    /// <exception cref="EtlQualityRuleViolationException">
    /// <paramref name="action"/> = <see cref="EtlQualityRuleAction.Abort"/>
    /// 且至少一筆違規時拋出。
    /// </exception>
    public static DataTable Apply(
        DataTable table,
        IList<EtlQualityRule> rules,
        EtlQualityRuleAction action,
        out int failedRows,
        out List<string> failureSamples)
    {
        if (table == null) { throw new ArgumentNullException(nameof(table)); }
        if (rules == null) { throw new ArgumentNullException(nameof(rules)); }

        failedRows = 0;
        failureSamples = new List<string>();
        if (rules.Count == 0 || table.Rows.Count == 0)
        {
            return table;
        }

        // 預先決定每個規則的欄位 index — 規則欄不存在於表中就丟例外
        // （表示 mapping / config 對不上，不該悄悄忽略）
        var ruleColIdx = new int[rules.Count];
        // Pre-build HashSet<string> for In-rules so per-row Contains is O(1) instead of O(n).
        // StringComparer.Ordinal matches the case-sensitivity of the previous IList.Contains
        // (which used the default object.Equals → string ordinal comparison).
        // Allowed property stays IList<string> — no public API change.
        var inRuleSets = new HashSet<string>?[rules.Count];
        for (int i = 0; i < rules.Count; i++)
        {
            var col = table.Columns[rules[i].Column];
            if (col == null)
            {
                throw new ArgumentException(
                    $"Quality rule references column '{rules[i].Column}' which is not in the transformed table.",
                    nameof(rules));
            }
            ruleColIdx[i] = col.Ordinal;
            if (rules[i].RuleType == EtlQualityRuleType.In
                && rules[i].Allowed != null && rules[i].Allowed!.Count > 0)
            {
                inRuleSets[i] = new HashSet<string>(rules[i].Allowed!, StringComparer.Ordinal);
            }
        }

        var keepRows = new List<DataRow>(table.Rows.Count);
        for (int rowIdx = 0; rowIdx < table.Rows.Count; rowIdx++)
        {
            var row = table.Rows[rowIdx];
            string? violation = null;
            for (int i = 0; i < rules.Count; i++)
            {
                // Fast-path for In-rules: use pre-built HashSet when available.
                if (inRuleSets[i] != null)
                {
                    var val = row[ruleColIdx[i]];
                    if (val != null && val != DBNull.Value)
                    {
                        var sv = val.ToString() ?? string.Empty;
                        if (!inRuleSets[i]!.Contains(sv))
                            violation = $"{rules[i].Column}={Truncate(sv)} not in allow-list (rule: In)";
                    }
                    // else: null/DBNull → same pass-through as Evaluate
                }
                else
                {
                    violation = Evaluate(rules[i], row[ruleColIdx[i]]);
                }
                if (violation != null) { break; }
            }
            if (violation == null)
            {
                keepRows.Add(row);
                continue;
            }

            failedRows++;
            if (failureSamples.Count < MaxFailureSamples)
            {
                failureSamples.Add($"row#{rowIdx}: {violation}");
            }

            if (action == EtlQualityRuleAction.Abort)
            {
                throw new EtlQualityRuleViolationException(
                    $"Quality rule violated: {violation}");
            }
            if (action == EtlQualityRuleAction.Continue)
            {
                keepRows.Add(row); // 違規列照樣保留（audit-only）
            }
            // Drop → 不加入 keepRows
        }

        // 沒違規或全保留 → 原表回傳（避免不必要的 Clone）
        if (failedRows == 0 || action == EtlQualityRuleAction.Continue)
        {
            return table;
        }

        // Drop 模式：建一張只含合規列的新表
        var clone = table.Clone();
        foreach (var r in keepRows)
        {
            clone.ImportRow(r);
        }
        return clone;
    }

    /// <summary>
    /// 對單一格子套用規則。回傳違規描述字串，或 <c>null</c> = 通過。
    /// </summary>
    internal static string? Evaluate(EtlQualityRule rule, object? value)
    {
        switch (rule.RuleType)
        {
            case EtlQualityRuleType.NotNull:
                if (value == null || value == DBNull.Value)
                {
                    return $"{rule.Column} is null (rule: NotNull)";
                }
                return null;

            case EtlQualityRuleType.Range:
                if (value == null || value == DBNull.Value) { return null; } // null 不在 Range 規則範圍
                if (!TryToDecimal(value, out var dec))
                {
                    return $"{rule.Column}={Truncate(value)} not numeric (rule: Range)";
                }
                if (rule.Min.HasValue && dec < rule.Min.Value)
                {
                    return $"{rule.Column}={dec} below Min={rule.Min.Value} (rule: Range)";
                }
                if (rule.Max.HasValue && dec > rule.Max.Value)
                {
                    return $"{rule.Column}={dec} above Max={rule.Max.Value} (rule: Range)";
                }
                return null;

            case EtlQualityRuleType.Regex:
                if (string.IsNullOrEmpty(rule.Pattern))
                {
                    return $"{rule.Column} rule misconfigured: Regex Pattern is empty";
                }
                if (value == null || value == DBNull.Value) { return null; }
                var regex = _regexCache.GetOrAdd(rule.Pattern,
                    p => new Regex(p, RegexOptions.Compiled, TimeSpan.FromSeconds(1)));
                var s = value.ToString() ?? string.Empty;
                if (!regex.IsMatch(s))
                {
                    return $"{rule.Column}={Truncate(s)} fails Regex {Truncate(rule.Pattern)}";
                }
                return null;

            case EtlQualityRuleType.In:
                if (rule.Allowed == null || rule.Allowed.Count == 0)
                {
                    return $"{rule.Column} rule misconfigured: In Allowed list is empty";
                }
                if (value == null || value == DBNull.Value) { return null; }
                var sv = value.ToString() ?? string.Empty;
                if (!rule.Allowed.Contains(sv))
                {
                    return $"{rule.Column}={Truncate(sv)} not in allow-list (rule: In)";
                }
                return null;

            default:
                return $"{rule.Column} rule misconfigured: unknown RuleType {rule.RuleType}";
        }
    }

    private static bool TryToDecimal(object value, out decimal result)
    {
        if (value is decimal d) { result = d; return true; }
        if (value is int i) { result = i; return true; }
        if (value is long l) { result = l; return true; }
        if (value is double db) { result = (decimal)db; return true; }
        if (value is float f) { result = (decimal)f; return true; }
        return decimal.TryParse(value.ToString(), NumberStyles.Any,
            CultureInfo.InvariantCulture, out result);
    }

    private static string Truncate(object? value, int max = 64)
    {
        var s = value?.ToString() ?? "";
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}

/// <summary>
/// <see cref="EtlQualityRuleAction.Abort"/> 模式下，遇到第一個違規列即拋出。
/// </summary>
public class EtlQualityRuleViolationException : Exception
{
    public EtlQualityRuleViolationException(string message) : base(message) { }
}
