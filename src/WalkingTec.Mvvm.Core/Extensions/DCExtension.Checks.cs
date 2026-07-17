#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace WalkingTec.Mvvm.Core.Extensions
{
    /// <summary>
    /// DC相关扩展函数
    /// </summary>
    public static partial class DCExtension
    {
        public static IQueryable<T> CheckID<T>(this IQueryable<T> baseQuery, object? val, Expression<Func<T, object>>? member = null)
        {
            ParameterExpression pe = Expression.Parameter(typeof(T));
            PropertyInfo? idproperty = null;
            if (member == null)
            {
                idproperty = typeof(T).GetSingleProperty("ID")!;
            }
            else
            {
                idproperty = member.GetPropertyInfo()!;
            }
            Expression peid = Expression.Property(pe, idproperty!);
            var convertid = PropertyHelper.ConvertValue(val, idproperty!.PropertyType);
            if (idproperty!.PropertyType.IsNullable())
            {
                peid = Expression.Property(peid, "Value");
            }
            return baseQuery.Where(Expression.Lambda<Func<T, bool>>(Expression.Equal(peid, Expression.Constant(convertid)), pe));
        }

        public static IQueryable<T> CheckParentID<T>(this IQueryable<T> baseQuery, string? val)
        {
            ParameterExpression pe = Expression.Parameter(typeof(T));
            PropertyInfo? idproperty = null;
            idproperty = typeof(T).GetSingleProperty("ParentId")!;
            Expression peid = Expression.Property(pe, idproperty!);
            var p = Expression.Call(peid, "ToString", new Type[] { });
            if (val == null)
            {
                return baseQuery.Where(Expression.Lambda<Func<T, bool>>(Expression.Equal(peid, Expression.Constant(null)), pe));
            }
            else
            {
                return baseQuery.Where(Expression.Lambda<Func<T, bool>>(Expression.Equal(p, Expression.Constant(val)), pe));
            }
        }


        public static IQueryable<T> CheckIDs<T>(this IQueryable<T> baseQuery, List<string?>? val, Expression<Func<T, object>>? member = null)
        {
            if (val == null || val.Count == 0)
            {
                return baseQuery;
            }
            ParameterExpression pe = Expression.Parameter(typeof(T));
            PropertyInfo? idproperty = null;
            if (member == null)
            {
                idproperty = typeof(T).GetSingleProperty("ID")!;
            }
            else
            {
                idproperty = member.GetPropertyInfo()!;
            }
            Expression peid = Expression.Property(pe, idproperty!);
            var exp = val.GetContainIdExpression(typeof(T), pe, peid)!.Body;
            return baseQuery.Where(Expression.Lambda<Func<T, bool>>(exp, pe));
        }


        public static IQueryable<T> CheckNotNull<T>(this IQueryable<T> baseQuery, Expression<Func<T, object>> member)
        {
            return baseQuery.CheckNotNull<T>(member.GetPropertyName());
        }

        public static IQueryable<T> CheckNotNull<T>(this IQueryable<T> baseQuery, string member)
        {
            ParameterExpression pe = Expression.Parameter(typeof(T));
            PropertyInfo? idproperty = typeof(T).GetSingleProperty(member);
            Expression peid = Expression.Property(pe, idproperty!);
            return baseQuery.Where(Expression.Lambda<Func<T, bool>>(Expression.NotEqual(peid, Expression.Constant(null)), pe));
        }


        public static IQueryable<T> CheckNull<T>(this IQueryable<T> baseQuery, Expression<Func<T, object>> member)
        {
            ParameterExpression pe = Expression.Parameter(typeof(T));
            PropertyInfo? idproperty = typeof(T).GetSingleProperty(member.GetPropertyName());
            Expression peid = Expression.Property(pe, idproperty!);
            return baseQuery.Where(Expression.Lambda<Func<T, bool>>(Expression.Equal(peid, Expression.Constant(null)), pe));
        }

        /// <summary>
        /// val不为空时，附加查询条件
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <typeparam name="S"></typeparam>
        /// <param name="baseQuery"></param>
        /// <param name="val"></param>
        /// <param name="where"></param>
        /// <returns></returns>
        public static IQueryable<T> CheckWhere<T, S>(this IQueryable<T> baseQuery, S? val, Expression<Func<T, bool>> where)
        {
            if (val == null)
            {
                return baseQuery;
            }
            else if (val is string s && string.IsNullOrEmpty(s))
            {
                return baseQuery;
            }
            else
            {
                if (val != null && typeof(IList).IsAssignableFrom(val.GetType()))
                {
                    if (((IList)val).Count == 0)
                    {
                        return baseQuery;
                    }
                }
                return baseQuery.Where(where);
            }
        }

        /// <summary>
        /// 条件为true时，附加查询条件
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="baseQuery"></param>
        /// <param name="val">bool?</param>
        /// <param name="where"></param>
        /// <returns></returns>
        public static IQueryable<T> WhereIf<T>(this IQueryable<T> baseQuery, bool? val, Expression<Func<T, bool>> where)
        {
            if (val == null || val == false)
            {
                return baseQuery;
            }
            return baseQuery.Where(where);
        }

        public static IQueryable<T> CheckEqual<T>(this IQueryable<T> baseQuery, string val, Expression<Func<T, string>> field)
        {
            if (val == null || val == "")
            {
                return baseQuery;
            }
            else
            {
                val = val.Trim();
                var equal = Expression.Equal(field.Body, Expression.Constant(val));
                var where = Expression.Lambda<Func<T, bool>>(equal, field.Parameters[0]);
                return baseQuery.Where(where);
            }
        }

        public static IQueryable<T> CheckEqual<T, S>(this IQueryable<T> baseQuery, S? val, Expression<Func<T, S?>> field)
            where S : struct
        {
            if (val == null)
            {
                return baseQuery;
            }
            else
            {
                var equal = Expression.Equal(Expression.PropertyOrField(field.Body, "Value"), Expression.Constant(val));
                var where = Expression.Lambda<Func<T, bool>>(equal, field.Parameters[0]);
                return baseQuery.Where(where);
            }
        }

        public static IQueryable<T> CheckEqual<T, S>(this IQueryable<T> baseQuery, S val, Expression<Func<T, S?>> field)
    where S : struct
        {
            S? a = val;
            return baseQuery.CheckEqual(a, field);
        }


        public static IQueryable<T> CheckBetween<T, S>(this IQueryable<T> baseQuery, S? valMin, S? valMax, Expression<Func<T, S?>> field, bool includeMin = true, bool includeMax = true)
    where S : struct
        {
            if (valMin == null && valMax == null)
            {
                return baseQuery;
            }
            else
            {
                IQueryable<T> rv = baseQuery;
                if (valMin != null)
                {
                    BinaryExpression exp1 = !includeMin ? Expression.GreaterThan(Expression.PropertyOrField(field.Body, "Value"), Expression.Constant(valMin)) : Expression.GreaterThanOrEqual(Expression.PropertyOrField(field.Body, "Value"), Expression.Constant(valMin));
                    rv = rv.Where(Expression.Lambda<Func<T, bool>>(exp1, field.Parameters[0]));
                }
                if (valMax != null)
                {
                    BinaryExpression exp2 = !includeMax ? Expression.LessThan(Expression.PropertyOrField(field.Body, "Value"), Expression.Constant(valMax)) : Expression.LessThanOrEqual(Expression.PropertyOrField(field.Body, "Value"), Expression.Constant(valMax));
                    rv = rv.Where(Expression.Lambda<Func<T, bool>>(exp2, field.Parameters[0]));
                }
                return rv;
            }
        }

        public static IQueryable<T> CheckBetween<T, S>(this IQueryable<T> baseQuery, S valMin, S valMax, Expression<Func<T, S?>> field, bool includeMin = true, bool includeMax = true)
where S : struct
        {
            S? a = valMin;
            S? b = valMax;
            return CheckBetween(baseQuery, a, b, field, includeMin, includeMax);
        }

        public static IQueryable<T> CheckBetween<T, S>(this IQueryable<T> baseQuery, S? valMin, S valMax, Expression<Func<T, S?>> field, bool includeMin = true, bool includeMax = true)
where S : struct
        {
            S? a = valMin;
            S? b = valMax;
            return CheckBetween(baseQuery, a, b, field, includeMin, includeMax);
        }

        public static IQueryable<T> CheckBetween<T, S>(this IQueryable<T> baseQuery, S valMin, S? valMax, Expression<Func<T, S?>> field, bool includeMin = true, bool includeMax = true)
where S : struct
        {
            S? a = valMin;
            S? b = valMax;
            return CheckBetween(baseQuery, a, b, field, includeMin, includeMax);
        }

        public static IQueryable<T> CheckContain<T>(this IQueryable<T> baseQuery, string? val, Expression<Func<T, string>> field, bool ignoreCase = true)
        {
            if (string.IsNullOrEmpty(val))
            {
                return baseQuery;
            }
            else
            {
                val = val.Trim();
                Expression? exp = null;
                if (ignoreCase == true)
                {
                    var tolower = Expression.Call(field.Body, "ToLower", null);
                    exp = Expression.Call(tolower, "Contains", null, Expression.Constant(val.ToLower()));
                }
                else
                {
                    exp = Expression.Call(field.Body, "Contains", null, Expression.Constant(val));

                }
                var where = Expression.Lambda<Func<T, bool>>(exp, field.Parameters[0]);
                return baseQuery.Where(where);
            }
        }

        public static IQueryable<T> CheckContain<T, S>(this IQueryable<T> baseQuery, List<S?>? val, Expression<Func<T, S>> field)
        {
            if (val == null || val.Count == 0 || (val.Count == 1 && val[0] == null))
            {
                return baseQuery;
            }
            else
            {
                Expression? exp = null;
                exp = Expression.Call(Expression.Constant(val), "Contains", null, field.Body);

                var where = Expression.Lambda<Func<T, bool>>(exp, field.Parameters[0]);
                return baseQuery.Where(where);
            }
        }

        public static IQueryable<T> CheckContain<T, S>(this IQueryable<T> baseQuery, List<string?>? val, Expression<Func<T, S>> field)
        {
            if (val == null || val.Count == 0 || (val.Count == 1 && val[0] == null))
            {
                return baseQuery;
            }
            else
            {
                ParameterExpression pe = Expression.Parameter(typeof(T));
                var rv = val.GetContainIdExpression<T>(field);
                return baseQuery.Where(rv);
            }
        }
    }
}
