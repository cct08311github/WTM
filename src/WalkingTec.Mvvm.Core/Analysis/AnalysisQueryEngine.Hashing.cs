#nullable enable
using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Analysis
{
    public partial class AnalysisQueryEngine
    {
        /// <summary>
        /// 固定序列化選項：確保 ComputeHash() 在所有環境、STJ 版本下產生相同的 JSON 字串。
        /// </summary>
        private static readonly System.Text.Json.JsonSerializerOptions _hashSerializerOptions =
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = null,
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
            };

        /// <summary>
        /// 聚合函式名稱的中文對照（用於匯出標頭）。
        /// </summary>
        private static readonly FrozenDictionary<AggregateFunc, string> _funcDisplayNames = new Dictionary<AggregateFunc, string>
        {
            { AggregateFunc.Sum,           "合計" },
            { AggregateFunc.Count,         "計數" },
            { AggregateFunc.Avg,           "平均" },
            { AggregateFunc.Max,           "最大" },
            { AggregateFunc.Min,           "最小" },
            { AggregateFunc.DistinctCount, "不重複計數" },
        }.ToFrozenDictionary();

        /// <summary>
        /// 計算查詢快取 key。
        /// 當 <paramref name="identityKey"/> 為 null 或空字串時回傳 null，
        /// 表示「此請求不應寫入或讀取共用快取」（M29 修復：防止匿名請求共用快取）。
        /// </summary>
        // internal (not public) so Core.Test can call it directly via InternalsVisibleTo;
        // kept out of the public surface to preserve encapsulation.
        internal static string? ComputeHash(AnalysisQueryRequest req, string? identityKey = null)
        {
            // M29 fix: identity-less requests must never share a cache entry.
            // Returning null signals callers to skip both get and set.
            if (string.IsNullOrEmpty(identityKey))
                return null;

            var raw = System.Text.Json.JsonSerializer.Serialize(req, _hashSerializerOptions);
            raw += "|" + identityKey;

            // Use ArrayPool to avoid a heap allocation for the UTF-8 byte array.
            // GetByteCount + GetBytes produces the EXACT same byte sequence as
            // Encoding.UTF8.GetBytes(raw) — byte-identical hash guaranteed.
            int byteCount = System.Text.Encoding.UTF8.GetByteCount(raw);
            byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                System.Text.Encoding.UTF8.GetBytes(raw, 0, raw.Length, rented, 0);
                Span<byte> hash = stackalloc byte[32]; // SHA256 = 32 bytes
                System.Security.Cryptography.SHA256.HashData(rented.AsSpan(0, byteCount), hash);
                return Convert.ToHexString(hash)[..16];
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        /// <summary>
        /// 建立欄位 key → 使用者友善顯示名稱的對照表。
        /// </summary>
        private static Dictionary<string, MeasureFormat> BuildColumnFormats(
            AnalysisQueryRequest req,
            Dictionary<string, AnalysisFieldMeta> wl)
        {
            var map = new Dictionary<string, MeasureFormat>();
            foreach (var m in req.Measures)
            {
                var key = $"{m.Field}_{m.Func}";
                map[key] = wl.TryGetValue(m.Field, out var meta) ? meta.Format : MeasureFormat.Auto;
            }
            return map;
        }

        private static Dictionary<string, string> BuildColumnDisplayNames(
            AnalysisQueryRequest req,
            Dictionary<string, AnalysisFieldMeta> wl)
        {
            var map = new Dictionary<string, string>();

            // 維度欄位
            foreach (var dim in req.Dimensions)
            {
                var displayName = wl.TryGetValue(dim, out var meta) && !string.IsNullOrEmpty(meta.DisplayName)
                    ? meta.DisplayName
                    : dim;
                map[dim] = displayName;
            }

            // 量值欄位
            var compareLabel = req.CompareWith?.Label ?? "Compare";
            foreach (var m in req.Measures)
            {
                var key = $"{m.Field}_{m.Func}";
                var fieldDisplay = wl.TryGetValue(m.Field, out var meta) && !string.IsNullOrEmpty(meta.DisplayName)
                    ? meta.DisplayName
                    : m.Field;
                var funcDisplay = _funcDisplayNames.TryGetValue(m.Func, out var fd) ? fd : m.Func.ToString();
                var baseLabel = $"{fieldDisplay} {funcDisplay}";
                map[key] = baseLabel;

                // Period-over-period — surface the comparison columns
                // with human-readable headers so the front-end picker
                // can render "金額 合計 (上月)" / "差值" / "變化%" out
                // of the box without per-app i18n plumbing.
                if (req.CompareWith != null)
                {
                    map[$"{key}_Compare"]    = $"{baseLabel} ({compareLabel})";
                    map[$"{key}_Delta"]      = $"{baseLabel} 差值";
                    map[$"{key}_ChangePct"]  = $"{baseLabel} 變化%";
                }
            }

            return map;
        }

        /// <summary>
        /// Replace enum string values in dimension columns with their [Display(Name)] if available.
        /// </summary>
        private static void ResolveEnumDisplayNames(
            List<Dictionary<string, object?>> rows,
            IList<string> dimensions,
            Dictionary<string, AnalysisFieldMeta> wl)
        {
            // Build a map of dimension columns whose CLR type is an enum
            var enumDims = new Dictionary<string, Type>();
            foreach (var dim in dimensions)
            {
                if (wl.TryGetValue(dim, out var meta))
                {
                    var clr = Nullable.GetUnderlyingType(meta.ClrType) ?? meta.ClrType;
                    if (clr.IsEnum) enumDims[dim] = clr;
                }
            }
            if (enumDims.Count == 0) return;

            // Cache resolved names per enum type
            var displayCache = new Dictionary<string, Dictionary<string, string>>();
            foreach (var kvp in enumDims)
            {
                var cache = new Dictionary<string, string>();
                foreach (var val in Enum.GetValues(kvp.Value))
                {
                    var name = val.ToString()!;
                    var display = ((Enum)val).GetEnumDisplayName();
                    if (!string.IsNullOrEmpty(display) && display != name)
                        cache[name] = display;
                }
                if (cache.Count > 0) displayCache[kvp.Key] = cache;
            }
            if (displayCache.Count == 0) return;

            // Replace values in rows
            foreach (var row in rows)
            {
                foreach (var kvp in displayCache)
                {
                    if (row.TryGetValue(kvp.Key, out var val) && val is string s && kvp.Value.TryGetValue(s, out var display))
                        row[kvp.Key] = display;
                }
            }
        }

        /// <summary>
        /// Escape a single pivot row-key segment so that a literal '|' or '\' in a
        /// dimension value cannot collide with the '|' join delimiter used in
        /// <see cref="ExecutePivot{TModel}"/> / <see cref="ExecutePivotAsync{TModel}"/>.
        /// Encoding: '\' → '\\', '|' → '\|'.  Decoding is not needed because the
        /// pivot map uses the full escaped key only as a dictionary key (no split).
        /// </summary>
        private static string EscapeKeySeg(string? seg)
        {
            if (seg == null) return string.Empty;
            return seg.Replace("\\", "\\\\").Replace("|", "\\|");
        }

        /// <summary>
        /// Validate <see cref="AnalysisQueryRequest.HavingFilters"/>: each
        /// filter's <c>Field</c> must reference a requested
        /// <c>{measure.Field}_{measure.Func}</c> result column;
        /// <c>Operator</c> must be one of the numeric-comparison set
        /// (Eq / NotEq / Gt / Gte / Lt / Lte) — Contains/In/etc. don't
        /// apply to scalar aggregate values.
        /// </summary>
        internal static void ValidateHavingFilters(AnalysisQueryRequest req)
        {
            if (req.HavingFilters == null || req.HavingFilters.Count == 0) { return; }

            var allowed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in req.Measures) { allowed.Add($"{m.Field}_{m.Func}"); }

            foreach (var h in req.HavingFilters)
            {
                if (string.IsNullOrWhiteSpace(h.Field))
                {
                    throw new AnalysisException("HavingFilter.Field must not be empty.");
                }
                if (!allowed.Contains(h.Field))
                {
                    throw new AnalysisException(
                        $"HavingFilter field '{h.Field}' is not in the requested Measures. " +
                        $"Use the '{{Field}}_{{Func}}' name (e.g. 'Amount_Sum').");
                }
                switch (h.Operator)
                {
                    case FilterOperator.Eq:
                    case FilterOperator.NotEq:
                    case FilterOperator.Gt:
                    case FilterOperator.Gte:
                    case FilterOperator.Lt:
                    case FilterOperator.Lte:
                        break;
                    default:
                        throw new AnalysisException(
                            $"HavingFilter operator '{h.Operator}' is not supported. " +
                            "HAVING applies to scalar aggregate values; allowed operators are Eq, NotEq, Gt, Gte, Lt, Lte.");
                }
            }
        }
    }
}
