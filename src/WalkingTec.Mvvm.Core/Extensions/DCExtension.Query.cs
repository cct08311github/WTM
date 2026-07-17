#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WalkingTec.Mvvm.Core.Extensions
{
    /// <summary>
    /// DC相关扩展函数
    /// </summary>
    public static partial class DCExtension
    {
        #region AddBy YOUKAI 20160310
        /// <summary>
        /// 为查询语句添加关联表的权限过滤
        /// </summary>
        /// <typeparam name="T">源数据类</typeparam>
        /// <param name="baseQuery">源Query</param>
        /// <param name="wtmcontext">wtm context</param>
        /// <param name="IdFields">关联表外键</param>
        /// <returns>修改后的查询语句</returns>
        public static IQueryable<T> DPWhere<T>(this IQueryable<T> baseQuery, WTMContext? wtmcontext, params Expression<Func<T, object>>[] IdFields) //where T : TopBasePoco
        {
            var dps = wtmcontext?.LoginUserInfo?.DataPrivileges;
            bool isInMemory = baseQuery.Provider.GetType().IsGenericType
                && baseQuery.Provider.GetType().GetGenericTypeDefinition() == typeof(EnumerableQuery<>);

            // var dpsSetting = BaseVM.AllDPS;
            ParameterExpression pe = Expression.Parameter(typeof(T));
            Expression left1 = Expression.Constant(1);
            Expression right1 = Expression.Constant(1);
            Expression trueExp = Expression.Equal(left1, right1);
            Expression falseExp = Expression.NotEqual(left1, right1);
            Expression? finalExp = null;
            int tindex = 0;
            //循环所有关联外键
            foreach (var IdField in IdFields)
            {
                Expression exp = trueExp;
                //将外键Id用.分割，循环生成指向最终id的表达式，比如x=> x.a.b.Id
                var fullname = IdField.GetPropertyName();
                string[] splits = fullname.Split('.');

                List<(Expression exp, ParameterExpression pe, bool islist)> data = new System.Collections.Generic.List<(Expression, ParameterExpression, bool)>();
                Expression iexp = pe;
                ParameterExpression ipe = pe;

                //格式化idfeild，保存在data中
                for (int i = 0; i < splits.Length; i++)
                {
                    var item = splits[i];
                    var proname = item;
                    int lindex = proname.IndexOf('[');
                    bool islist = false;
                    if (lindex > 0)
                    {
                        islist = true;
                        proname = proname.Substring(0, lindex);
                    }
                    var pro = iexp.Type.GetSingleProperty(proname);
                    if (pro != null)
                    {
                        iexp = Expression.MakeMemberAccess(iexp, pro);
                    }

                    Type? petype = null;
                    if (islist == true)
                    {
                        petype = iexp.Type.GetGenericArguments()[0];
                    }
                    else
                    {
                        petype = iexp.Type;
                    }
                    if (petype == typeof(TreePoco))
                    {
                        petype = typeof(T);
                    }
                    if (islist == true || i == splits.Length - 1)
                    {
                        data.Add((iexp, ipe, islist));
                        ipe = Expression.Parameter(petype!);
                        iexp = ipe;
                    }
                }

                //确定最终关联的表名
                string tableName = "";
                if (data.Count > 0)
                {
                    var last = data.Last().exp as MemberExpression;
                    string? fieldname = last?.Member?.Name;
                    if (string.IsNullOrEmpty(fieldname) == false)
                    {
                        if (fieldname.ToLower() == "id")
                        {
                            tableName = last!.Member.ReflectedType?.Name ?? "";
                        }
                        else
                        {
                            var pro2 = wtmcontext?.DC?.GetPropertyNameByFk(last!.Member.ReflectedType!, fieldname);
                            if (string.IsNullOrEmpty(pro2) == false)
                            {
                                tableName = last!.Member.ReflectedType!.GetSingleProperty(pro2!)?.PropertyType.Name ?? "";
                            }
                            // Convention-based fallback: strip trailing "Id" to derive navigation property name
                            if (string.IsNullOrEmpty(tableName) && fieldname.Length > 2 && fieldname.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
                            {
                                var navName = fieldname.Substring(0, fieldname.Length - 2);
                                var navProp = last!.Member.ReflectedType?.GetSingleProperty(navName);
                                if (navProp != null)
                                {
                                    tableName = navProp.PropertyType.Name;
                                }
                            }
                        }
                    }
                }

                //如果dps为空，则拼接一个返回假的表达式，这样就查询不出任何数据
                if (dps == null)
                {
                    if (tableName == "")
                    {
                        continue;
                    }
                    exp = falseExp;
                }
                else
                {
                    var dpsSetting = wtmcontext?.DataPrivilegeSettings;
                    //循环系统设定的数据权限，如果没有和关联类一样的表，则跳过
                    if (dpsSetting?.Where(x => x.ModelName == tableName).FirstOrDefault() == null)
                    {
                        continue;
                    }
                    //获取dps中关联到关联类的id列表
                    List<string?> ids = [.. dps.Where(x => x.TableName == tableName).Select(x => x.RelateId)];
                    //如果没有关联的id，则拼接一个返回假的where，是语句查询不到任何数据
                    if (ids == null || ids.Count == 0)
                    {
                        //bool isBasePoco = typeof(IBasePoco).IsAssignableFrom(data.Last().pe.Type);
                        //if (isBasePoco)
                        //{
                        //    exp = Expression.Equal(Expression.Property(data.Last().pe, "CreateBy"), Expression.Constant(wtmcontext.LoginUserInfo?.ITCode));
                        //}
                        //else
                        //{
                        exp = falseExp;
                        //}
                    }
                    //如果有关联 Id
                    else
                    {
                        //如果关联 Id 不包含null，则生成类似 x=> ids.Contains(x.a.b.Id) 这种条件
                        //如果关联 Id 包括null，则代表可以访问所有数据，就不需要再拼接where条件了
                        if (!ids.Contains(null))
                        {
                            for (int i = data.Count - 1; i >= 0; i--)
                            {
                                var d = data[i];
                                if (d.islist == true)
                                {
                                    var lastd = data[i + 1];
                                    if (isInMemory)
                                    {
                                        // In-memory IQueryable: use Enumerable.Any directly
                                        exp = Expression.Call(
                                             typeof(Enumerable),
                                             "Any",
                                             new Type[] { lastd.pe.Type },
                                             d.exp,
                                             Expression.Lambda(typeof(Func<,>).MakeGenericType(lastd.pe.Type, typeof(bool)), exp, new ParameterExpression[] { lastd.pe }));
                                    }
                                    else
                                    {
                                        var queryable = Expression.Call(
                                             typeof(Queryable),
                                             "AsQueryable",
                                             new Type[] { lastd.pe.Type },
                                             d.exp);

                                        exp = Expression.Call(
                                             typeof(Queryable),
                                             "Any",
                                             new Type[] { lastd.pe.Type },
                                             queryable,
                                             Expression.Lambda(typeof(Func<,>).MakeGenericType(lastd.pe.Type, typeof(bool)), exp, new ParameterExpression[] { lastd.pe }));
                                    }

                                }
                                else
                                {
                                    exp = ids.GetContainIdExpression(d.pe.Type, d.pe, d.exp)!.Body;
                                }
                            }
                        }
                    }
                }
                //把所有where里的条件用And拼接在一起
                if (finalExp == null)
                {
                    finalExp = exp;
                }
                else
                {
                    finalExp = Expression.OrElse(finalExp, exp!);
                }
                tindex++;
            }
            //如果没有进行任何修改，则还返回baseQuery
            if (finalExp == null)
            {
                return baseQuery;
            }
            else
            {
                //返回加入了where条件之后的baseQuery
                var query = baseQuery.Where(Expression.Lambda<Func<T, bool>>(finalExp!, pe));
                return query;
            }
        }
        #endregion

        /// <summary>
        /// L14: Returns true when sorting by the given property should be blocked.
        /// Blocks [JsonIgnore] / [NotMapped] properties and a conservative name-based
        /// list of sensitive fields, matching the UpdateModelProperty blocklist concept.
        /// </summary>
        private static bool IsSensitiveSortPropertyDC(PropertyInfo prop)
        {
            if (prop.IsDefined(typeof(JsonIgnoreAttribute), inherit: true))
            {
                return true;
            }
            if (prop.IsDefined(typeof(NotMappedAttribute), inherit: true))
            {
                return true;
            }
            var sensitiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Password", "PasswordHash", "Salt", "Token"
            };
            return sensitiveNames.Contains(prop.Name);
        }

        public static IOrderedQueryable<T> Sort<T>(this IQueryable<T> baseQuery, string sortInfo, params SortInfo[] defaultSorts) where T : TopBasePoco
        {
            List<SortInfo> info = [];
            IOrderedQueryable<T>? rv = null;
            if (string.IsNullOrEmpty(sortInfo))
            {
                if (defaultSorts == null || defaultSorts.Length == 0)
                {
                    ParameterExpression pe = Expression.Parameter(typeof(T));
                    var idproperty = typeof(T).GetSingleProperty("ID");
                    Expression pro = Expression.Property(pe, idproperty!);
                    Type proType = typeof(Guid);
                    Expression final = Expression.Call(
                                                   typeof(Queryable),
                                                   "OrderBy",
                                                   new Type[] { typeof(T), proType },
                                                   baseQuery.Expression,
                                                   Expression.Lambda(pro, new ParameterExpression[] { pe }));
                    rv = baseQuery.Provider.CreateQuery<T>(final) as IOrderedQueryable<T>;
                    return rv!;
                }
                else
                {
                    if (defaultSorts != null)
                    {
                        info.AddRange(defaultSorts);
                    }
                }
            }
            else
            {
                var temp = JsonSerializer.Deserialize<List<SortInfo>>(sortInfo);
                if (temp != null)
                {
                    info.AddRange(temp);
                }
            }
            foreach (var item in info)
            {
                ParameterExpression pe = Expression.Parameter(typeof(T));
                var idproperty = typeof(T).GetSingleProperty(item.Property!);
                // L11: skip sort fields that don't exist on T — avoids NRE from Expression.Property(pe, null!)
                if (idproperty == null)
                {
                    continue;
                }
                // L14: skip sensitive properties (password/token/etc.) to prevent info-leak via sort-order oracle
                if (IsSensitiveSortPropertyDC(idproperty))
                {
                    continue;
                }
                Expression pro = Expression.Property(pe, idproperty);
                Type proType = idproperty.PropertyType;
                if (item.Direction == SortDir.Asc)
                {
                    if (rv == null)
                    {
                        Expression final = Expression.Call(
                               typeof(Queryable),
                               "OrderBy",
                               new Type[] { typeof(T), proType },
                               baseQuery.Expression,
                               Expression.Lambda(pro, new ParameterExpression[] { pe }));
                        rv = baseQuery.Provider.CreateQuery<T>(final) as IOrderedQueryable<T>;
                    }
                    else
                    {
                        Expression final = Expression.Call(
                               typeof(Queryable),
                               "ThenBy",
                               new Type[] { typeof(T), proType },
                               rv.Expression,
                               Expression.Lambda(pro, new ParameterExpression[] { pe }));
                        rv = rv.Provider.CreateQuery<T>(final) as IOrderedQueryable<T>;
                    }
                }
                if (item.Direction == SortDir.Desc)
                {
                    if (rv == null)
                    {
                        Expression final = Expression.Call(
                               typeof(Queryable),
                               "OrderByDescending",
                               new Type[] { typeof(T), proType },
                               baseQuery.Expression,
                               Expression.Lambda(pro, new ParameterExpression[] { pe }));
                        rv = baseQuery.Provider.CreateQuery<T>(final) as IOrderedQueryable<T>;
                    }
                    else
                    {
                        Expression final = Expression.Call(
                               typeof(Queryable),
                               "ThenByDescending",
                               new Type[] { typeof(T), proType },
                               rv.Expression,
                               Expression.Lambda(pro, new ParameterExpression[] { pe }));
                        rv = rv.Provider.CreateQuery<T>(final) as IOrderedQueryable<T>;
                    }
                }
            }
            return rv ?? (baseQuery as IOrderedQueryable<T> ?? baseQuery.OrderBy(x => x.ID));
        }

        public static IQueryable<string> DynamicSelect<T>(this IQueryable<T> baseQuery, string fieldName)
        {
            ParameterExpression pe = Expression.Parameter(typeof(T));
            var idproperty = typeof(T).GetSingleProperty(fieldName);
            // EVM-009: guard against unknown field names the same way Sort() does (L11).
            // Without this guard Expression.Property(pe, null!) throws NRE at runtime.
            if (idproperty == null)
            {
                return Enumerable.Empty<string>().AsQueryable();
            }
            Expression pro = Expression.Property(pe, idproperty);
            Expression tostring = Expression.Call(pro, "ToString", new Type[] { });
            Type proType = typeof(string);
            Expression final = Expression.Call(
                                           typeof(Queryable),
                                           "Select",
                                           new Type[] { typeof(T), proType },
                                           baseQuery.Expression,
                                           Expression.Lambda(tostring, new ParameterExpression[] { pe }));
            var rv = baseQuery.Provider.CreateQuery<string>(final) as IOrderedQueryable<string>;
            return rv!;
        }
    }
}
