#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Analysis
{
    public partial class AnalysisQueryEngine
    {
        // ── ApplyFilters Contains MethodInfo caches ────────────────────────────
        // Open generic methods resolved once; closed specialisations cached per CLR type.
        private static readonly MethodInfo _enumerableContainsOpenMethod =
            typeof(Enumerable)
                .GetMethods()
                .First(m => m.Name == "Contains" && m.GetParameters().Length == 2);

        private static readonly MethodInfo _stringContainsMethod =
            typeof(string).GetMethod("Contains", new[] { typeof(string) })!;

        private static readonly ConcurrentDictionary<Type, MethodInfo> _enumerableContainsClosedCache = new();

        /// <summary>
        /// Public for the <see cref="BuildDrillThroughQuery"/> entry point —
        /// drill-through reuses the same expression-tree filter pipeline
        /// (whitelist validation, type conversion, relative-date tokens)
        /// instead of duplicating it.
        /// </summary>
        public static IQueryable<TModel> ApplyFilters<TModel>(
            IQueryable<TModel> query,
            List<FilterCondition>? filters,
            Dictionary<string, AnalysisFieldMeta> whitelist)
        {
            if (filters == null || filters.Count == 0) return query;
            filters = ResolveRelativeDates(filters);

            var param = Expression.Parameter(typeof(TModel), "x");
            Expression? body = null;

            foreach (var f in filters)
            {
                if (!whitelist.TryGetValue(f.Field, out var meta))
                    throw new AnalysisFieldNotFoundException(f.Field, "Filter");

                var prop = Expression.Property(param, f.Field);
                var targetType = prop.Type;
                var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

                Expression? filterExpr = null;
                try
                {
                    if (f.Operator == FilterOperator.In || f.Operator == FilterOperator.NotIn)
                    {
                        // L1: honour the strongly-typed list form (f.Values) first.
                        // f.Values is populated by callers that already hold a List<string>
                        // (e.g. AnalysisQueryRequest.FilterCondition.Values).  Fall back to
                        // comma-splitting f.Value for callers that use the legacy string form.
                        System.Collections.IEnumerable? values = null;
                        if (f.Values is { Count: > 0 } valuesList)
                        {
                            values = valuesList;
                        }
                        else if (f.Value is string s && s.Length > 0)
                        {
                            values = s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim());
                        }
                        else
                        {
                            values = f.Value as System.Collections.IEnumerable;
                        }

                        if (values == null) throw new InvalidOperationException("Value for 'In'/'NotIn' operator must be an array, list or comma-separated string.");

                        var list = new System.Collections.ArrayList();
                        foreach (var v in values)
                        {
                            list.Add(ChangeType(v, underlyingType));
                        }
                        if (list.Count == 0) throw new InvalidOperationException("'In'/'NotIn' operator requires at least one value.");
                        if (list.Count > 100) throw new InvalidOperationException("'In'/'NotIn' operator supports up to 100 values.");

                        var typedList = Array.CreateInstance(underlyingType, list.Count);
                        list.CopyTo(typedList);
                        var listConst = Expression.Constant(typedList);

                        var containsMethod = _enumerableContainsClosedCache.GetOrAdd(
                            underlyingType,
                            t => _enumerableContainsOpenMethod.MakeGenericMethod(t));

                        Expression propForIn = prop;
                        if (Nullable.GetUnderlyingType(targetType) != null)
                        {
                            propForIn = Expression.Property(prop, "Value");
                            var nullGuard = Expression.NotEqual(prop, Expression.Constant(null, targetType));
                            var containsExpr = Expression.Call(containsMethod, listConst, propForIn);
                            if (f.Operator == FilterOperator.NotIn)
                            {
                                // NOT_NULL AND NOT_CONTAINS — keeps null rows excluded, consistent with
                                // In / NotEq / Gt etc. and SQL semantics (NULL NOT IN … → excluded). (#481)
                                filterExpr = Expression.AndAlso(nullGuard, Expression.Not(containsExpr));
                            }
                            else
                            {
                                filterExpr = Expression.AndAlso(nullGuard, containsExpr);
                            }
                        }
                        else
                        {
                            var inExpr = Expression.Call(containsMethod, listConst, propForIn);
                            filterExpr = f.Operator == FilterOperator.NotIn ? Expression.Not(inExpr) : inExpr;
                        }
                    }
                    else if (f.Operator == FilterOperator.Contains || f.Operator == FilterOperator.NotContains)
                    {
                        if (targetType != typeof(string))
                            throw new InvalidOperationException($"'Contains'/'NotContains' operator is only supported for string fields, not '{targetType.Name}'.");
                        var val = Expression.Constant(f.Value?.ToString() ?? "");
                        Expression containsExpr = Expression.Call(prop, _stringContainsMethod, val);
                        filterExpr = f.Operator == FilterOperator.NotContains ? Expression.Not(containsExpr) : containsExpr;
                    }
                    else
                    {
                        var val = Expression.Constant(ChangeType(f.Value, underlyingType), underlyingType);
                        var propForCmp = prop;
                        if (Nullable.GetUnderlyingType(targetType) != null)
                        {
                            propForCmp = Expression.Property(prop, "Value");
                        }

                        Expression cmp = f.Operator switch
                        {
                            FilterOperator.Eq => Expression.Equal(propForCmp, val),
                            FilterOperator.NotEq => Expression.NotEqual(propForCmp, val),
                            FilterOperator.Gt => Expression.GreaterThan(propForCmp, val),
                            FilterOperator.Gte => Expression.GreaterThanOrEqual(propForCmp, val),
                            FilterOperator.Lt => Expression.LessThan(propForCmp, val),
                            FilterOperator.Lte => Expression.LessThanOrEqual(propForCmp, val),
                            _ => throw new InvalidOperationException($"Operator {f.Operator} not supported in current analysis engine version.")
                        };

                        // Nullable properties need a NOT-NULL guard: SQL silently excludes NULL
                        // rows from comparisons, but LINQ-to-Objects would throw on .Value access.
                        if (Nullable.GetUnderlyingType(targetType) != null)
                        {
                            filterExpr = Expression.AndAlso(
                                Expression.NotEqual(prop, Expression.Constant(null, targetType)),
                                cmp
                            );
                        }
                        else
                        {
                            filterExpr = cmp;
                        }
                    }
                }
                catch (Exception ex) when (!(ex is InvalidOperationException))
                {
                    throw new InvalidOperationException($"欄位 '{f.Field}' 的篩選條件無效：{ex.Message}", ex);
                }

                body = body == null ? filterExpr : Expression.AndAlso(body, filterExpr);
            }

            if (body == null) return query;
            return query.Where(Expression.Lambda<Func<TModel, bool>>(body, param));
        }

        /// <summary>
        /// 將篩選條件中的相對日期 token（@today、@thisWeek 等）展開為 Gte/Lte 對。
        /// 不含 token 的條件直接原樣返回，不建立新物件。
        /// </summary>
        internal static List<FilterCondition> ResolveRelativeDates(List<FilterCondition> filters)
        {
            bool hasTokens = false;
            foreach (var f in filters)
                // M1 fix: f.Value may be null (STJ overrides the = string.Empty default with null)
                if ((f.Value ?? string.Empty).StartsWith("@", StringComparison.Ordinal)) { hasTokens = true; break; }
            if (!hasTokens) return filters;

            List<FilterCondition> result = [];
            var today = DateTime.Today;
            // ISO week offset: (dow + 6) % 7 maps Sun=0..Sat=6 → Mon=0..Sun=6.
            var mondayOffset = ((int)today.DayOfWeek + 6) % 7;

            foreach (var f in filters)
            {
                // M1 fix: guard against null Value — a null Value is not a relative-date token
                if (!(f.Value ?? string.Empty).StartsWith("@", StringComparison.Ordinal))
                {
                    result.Add(f);
                    continue;
                }

                DateTime start, end;
                switch ((f.Value ?? string.Empty).ToLowerInvariant())
                {
                    case "@today":
                        start = today; end = today;
                        break;
                    case "@yesterday":
                        start = today.AddDays(-1); end = today.AddDays(-1);
                        break;
                    case "@thisweek":
                        start = today.AddDays(-mondayOffset);
                        end = start.AddDays(6);
                        break;
                    case "@lastweek":
                        start = today.AddDays(-mondayOffset - 7);
                        end = start.AddDays(6);
                        break;
                    case "@nextweek":
                        start = today.AddDays(-mondayOffset + 7);
                        end = start.AddDays(6);
                        break;
                    case "@thismonth":
                        start = new DateTime(today.Year, today.Month, 1);
                        end = start.AddMonths(1).AddDays(-1);
                        break;
                    case "@lastmonth":
                        start = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
                        end = new DateTime(today.Year, today.Month, 1).AddDays(-1);
                        break;
                    case "@nextmonth":
                        start = new DateTime(today.Year, today.Month, 1).AddMonths(1);
                        end = start.AddMonths(1).AddDays(-1);
                        break;
                    case "@last7days":
                        start = today.AddDays(-7);
                        end = today;
                        break;
                    case "@last30days":
                        start = today.AddDays(-30);
                        end = today;
                        break;
                    case "@last90days":
                        start = today.AddDays(-90);
                        end = today;
                        break;
                    case "@last365days":
                        start = today.AddDays(-365);
                        end = today;
                        break;
                    case "@thisquarter":
                        // Quarter-start through today (not quarter-end) —
                        // matches @ytd semantics: "this period to date".
                        start = new DateTime(today.Year, ((today.Month - 1) / 3) * 3 + 1, 1);
                        end = today;
                        break;
                    case "@lastquarter":
                    {
                        // Full previous calendar quarter, regardless of today.
                        var thisQStart = new DateTime(today.Year, ((today.Month - 1) / 3) * 3 + 1, 1);
                        start = thisQStart.AddMonths(-3);
                        end = thisQStart.AddDays(-1);
                        break;
                    }
                    case "@ytd":
                        // Year-to-date: Jan 1 of this year through today.
                        start = new DateTime(today.Year, 1, 1);
                        end = today;
                        break;
                    case "@thisyear":
                        // Full current calendar year (Jan 1 through Dec 31),
                        // distinct from @ytd which clamps to today.
                        start = new DateTime(today.Year, 1, 1);
                        end = new DateTime(today.Year, 12, 31);
                        break;
                    case "@lastyear":
                        // Full previous calendar year.
                        start = new DateTime(today.Year - 1, 1, 1);
                        end = new DateTime(today.Year - 1, 12, 31);
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown relative date token '{f.Value}'.");
                }

                result.Add(new FilterCondition { Field = f.Field, Operator = FilterOperator.Gte, Value = start.ToString("yyyy-MM-dd") });
                result.Add(new FilterCondition { Field = f.Field, Operator = FilterOperator.Lte, Value = end.ToString("yyyy-MM-dd 23:59:59") });
            }
            return result;
        }

        private static object? ChangeType(object? value, Type targetType)
        {
            if (value == null) return null;
            if (targetType.IsEnum)
            {
                if (value is string s)
                {
                    // Fast path: member name ("Regular") or numeric string ("1")
                    if (Enum.TryParse(targetType, s, ignoreCase: true, out var parsed)) return parsed;

                    // Slow path: chart clicks send [Display(Name)] values (e.g. "一般") after
                    // ResolveEnumDisplayNames has translated them. Reverse-lookup. (#473)
                    foreach (var member in Enum.GetValues(targetType))
                    {
                        if (((Enum)member).GetEnumDisplayName() == s)
                            return member;
                    }
                    var validNames = Enum.GetValues(targetType)
                        .Cast<Enum>()
                        .Select(m => m.GetEnumDisplayName() ?? m.ToString())
                        .Distinct();
                    throw new ArgumentException($"'{s}' 不是有效的列舉值。有效值：{string.Join("、", validNames)}");
                }
                return Enum.ToObject(targetType, value);
            }
            return Convert.ChangeType(value, targetType, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
