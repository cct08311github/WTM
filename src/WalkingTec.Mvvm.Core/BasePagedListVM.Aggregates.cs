#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace WalkingTec.Mvvm.Core
{
    public partial class BasePagedListVM<TModel, TSearcher> : BaseVM, IBasePagedListVM<TModel, TSearcher>
        where TModel : TopBasePoco
        where TSearcher : BaseSearcher
    {
        #region #431 Server-side aggregate footers

        /// <summary>
        /// Computes server-side column aggregates (Sum/Avg/Count/Min/Max) over the
        /// <em>full filtered query</em> (i.e. before paging) for every column that has
        /// <see cref="IGridColumn{T}.AggregateType"/> set to a value other than
        /// <see cref="GridAggregateTypeEnum.None"/>.
        /// <para>
        /// Returns a dictionary keyed by the column field name (e.g. <c>"Price"</c>)
        /// whose value is the aggregate result formatted as a string. Returns an empty
        /// dictionary when no column has an aggregate configured, so callers can skip
        /// serialization cheaply.
        /// </para>
        /// <para>
        /// The aggregate is computed over <see cref="GetSearchQuery()"/> — the same
        /// filtered, ordered query used by <see cref="DoSearch()"/> — but without any
        /// <c>Skip</c>/<c>Take</c> paging so the result spans the full data set.
        /// </para>
        /// </summary>
        public virtual Dictionary<string, string> ComputeAggregates()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var columns = GetHeaders()
                .SelectMany(h => h.BottomChildren)
                .Where(c => c.AggregateType != GridAggregateTypeEnum.None
                            && c.ColumnType == GridColumnTypeEnum.Normal
                            && !string.IsNullOrEmpty(c.FieldName))
                .ToList();

            if (columns.Count == 0)
            {
                return result;
            }

            // Build the base query (full filtered set, no paging).
            IQueryable<TModel> query = GetSearchQuery();
            if (ReplaceWhere != null)
            {
                var mod = new WhereReplaceModifier<TModel>((ReplaceWhere as Expression<Func<TModel, bool>>)!);
                query = query.Provider.CreateQuery<TModel>(mod.Modify(query.Expression));
            }

            foreach (var col in columns)
            {
                try
                {
                    // Build a member-access expression for the column property.
                    var param = Expression.Parameter(typeof(TModel), "x");
                    var propInfo = typeof(TModel).GetProperty(col.FieldName!);
                    if (propInfo == null)
                    {
                        continue;
                    }
                    var body = Expression.Property(param, propInfo);

                    // Determine whether the property type is numeric/convertible.
                    var propType = propInfo.PropertyType;
                    var underlying = Nullable.GetUnderlyingType(propType) ?? propType;

                    string aggregated;
                    switch (col.AggregateType)
                    {
                        case GridAggregateTypeEnum.Count:
                        {
                            // Non-nullable value types (int, decimal, bool, DateTime...) cannot be compared
                            // with null via Expression.NotEqual — that throws InvalidOperationException at
                            // expression-tree compilation time (and is silently swallowed by the outer catch,
                            // producing a blank footer cell).  All value-type rows are by definition non-null,
                            // so the count is simply the total row count (#431 bug fix).
                            if (propType.IsValueType && Nullable.GetUnderlyingType(propType) == null)
                            {
                                aggregated = query.Count().ToString();
                            }
                            else
                            {
                                // Reference type or Nullable<T> — predicate filters out DB nulls.
                                var notNull = Expression.NotEqual(body, Expression.Constant(null, propType));
                                var lambda = Expression.Lambda<Func<TModel, bool>>(notNull, param);
                                aggregated = query.Count(lambda).ToString();
                            }
                            break;
                        }
                        case GridAggregateTypeEnum.Sum:
                        {
                            aggregated = ComputeNumericAggregate(query, param, body, propType, underlying, GridAggregateTypeEnum.Sum);
                            break;
                        }
                        case GridAggregateTypeEnum.Avg:
                        {
                            aggregated = ComputeNumericAggregate(query, param, body, propType, underlying, GridAggregateTypeEnum.Avg);
                            break;
                        }
                        case GridAggregateTypeEnum.Min:
                        {
                            aggregated = ComputeMinMax(query, param, body, propType, underlying, isMin: true);
                            break;
                        }
                        case GridAggregateTypeEnum.Max:
                        {
                            aggregated = ComputeMinMax(query, param, body, propType, underlying, isMin: false);
                            break;
                        }
                        default:
                            continue;
                    }
                    result[col.FieldName!] = aggregated;
                }
                catch (Exception)
                {
                    // Aggregate failed for this column (e.g. non-numeric type for Sum).
                    // Skip silently — other columns are still computed.
                }
            }

            return result;
        }

        private static string ComputeNumericAggregate(
            IQueryable<TModel> query,
            ParameterExpression param,
            MemberExpression body,
            Type propType,
            Type underlying,
            GridAggregateTypeEnum aggType)
        {
            // Convert to decimal? so EF Core can aggregate any numeric type.
            Expression converted;
            if (underlying == typeof(decimal) || underlying == typeof(decimal?))
            {
                converted = propType.IsGenericType ? body : Expression.Convert(body, typeof(decimal?));
            }
            else
            {
                // Convert non-nullable numeric types to decimal?; nullable ones cast then convert.
                var toNullDecimal = typeof(decimal?);
                converted = Expression.Convert(Expression.Convert(body, underlying), toNullDecimal);
            }

            var lambda = Expression.Lambda<Func<TModel, decimal?>>(converted, param);
            if (aggType == GridAggregateTypeEnum.Sum)
            {
                var sum = query.Sum(lambda);
                return sum?.ToString("G") ?? "0";
            }
            else // Avg
            {
                var avg = query.Average(lambda);
                return avg.HasValue ? avg.Value.ToString("G") : "0";
            }
        }

        private static string ComputeMinMax(
            IQueryable<TModel> query,
            ParameterExpression param,
            MemberExpression body,
            Type propType,
            Type underlying,
            bool isMin)
        {
            // For Min/Max we use a generic Select + Min()/Max() via reflection.
            // Cast the property to its nullable form so nulls are handled correctly.
            var selectLambda = Expression.Lambda(body, param);
            var selectMethod = typeof(Queryable)
                .GetMethods()
                .First(m => m.Name == "Select" && m.GetParameters().Length == 2)
                .MakeGenericMethod(typeof(TModel), propType);
            var projected = selectMethod.Invoke(null, new object[] { query, selectLambda }) as IQueryable;

            if (projected == null)
            {
                return string.Empty;
            }

            var aggregateMethodName = isMin ? "Min" : "Max";
            var aggregateMethod = typeof(Queryable)
                .GetMethods()
                .First(m => m.Name == aggregateMethodName && m.GetParameters().Length == 1)
                .MakeGenericMethod(propType);

            var result = aggregateMethod.Invoke(null, new object[] { projected });
            return result?.ToString() ?? string.Empty;
        }

        #endregion
    }
}
