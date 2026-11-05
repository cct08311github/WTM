using System;
using System.Linq;
using System.Linq.Expressions;

namespace WalkingTec.Mvvm.Core.Extensions
{
    /// <summary>
    /// Marks a <c>Where</c> condition as protected: a condition that restricts which records a user may see (a data privilege condition or
    /// a hand-written authorization condition) and that must stay in a query when the query is narrowed to selected records.
    /// The mark is the name of the predicate lambda. It does not change the generated SQL and does not take part in query caching.
    /// </summary>
    public static class ProtectedCondition
    {
        /// <summary>The name given to the predicate lambda of a protected condition.</summary>
        public const string LambdaName = "WTM.ProtectedCondition";

        /// <summary>
        /// Adds a protected condition. It filters like <c>Where</c>; when a list query is narrowed to selected records
        /// (<see cref="BasePagedListVM{TModel, TSearcher}"/> in batch or export mode), the condition is kept while the other
        /// conditions in the same run are replaced. Variables captured by the predicate stay query parameters.
        /// </summary>
        /// <remarks>
        /// Do not use a subquery over the same entity type inside the predicate; this form is not supported. Narrowing the query
        /// visits the expression a second time, which may affect the inside of the lambda. Keep the predicate to the columns of the
        /// entity itself and to captured variables.
        /// </remarks>
        /// <param name="source">the query</param>
        /// <param name="predicate">the condition</param>
        /// <returns>the query with the condition added</returns>
        public static IQueryable<T> WhereProtected<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            if (predicate == null)
            {
                throw new ArgumentNullException(nameof(predicate));
            }
            return source.Where(Mark(predicate));
        }

        /// <summary>Returns the same condition with the protected mark.</summary>
        internal static Expression<Func<T, bool>> Mark<T>(Expression<Func<T, bool>> predicate)
        {
            return Expression.Lambda<Func<T, bool>>(predicate.Body, LambdaName, predicate.Parameters);
        }

        /// <summary>Whether the call is a <c>Where</c> whose predicate carries the protected mark.</summary>
        internal static bool IsMarkedWhere(MethodCallExpression call)
        {
            return call.Method.Name == nameof(Queryable.Where)
                && call.Arguments.Count == 2
                && call.Arguments[1] is UnaryExpression quote
                && quote.Operand is LambdaExpression lambda
                && lambda.Name == LambdaName;
        }
    }
}
