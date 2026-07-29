#nullable enable
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core.Extensions
{
    /// <summary>
    /// DC相关扩展函数
    /// </summary>
    public static partial class DCExtension
    {
        #region 树形下拉
        /// <summary>
        /// 查询数据源，并转化成TreeSelectListItem列表
        /// </summary>
        /// <typeparam name="T">数据源类型</typeparam>
        /// <param name="baseQuery">基础查询</param>
        /// <param name="wtmcontext">wtm context</param>
        /// <param name="textField">表达式用来获取Text字段对应的值</param>
        /// <param name="valueField">表达式用来获取Value字段对应的值，不指定则默认使用Id字段</param>
        /// <param name="iconField">表达式用来获取icon字段对应的值</param>
        /// <param name="urlField">表达式用来获取Url字段对应的值</param>
        /// <param name="tagField">表达式用来获取Tag字段对应的值</param>
        /// <param name="expandField">表达式用来获取Expanded字段对应的值，指示节点是否展开</param>
        /// <param name="ignorDataPrivilege">忽略数据权限判断</param>
        /// <param name="SortByName">是否根据Text字段排序，默认为是</param>
        /// <returns>SelectListItem列表</returns>
        public static List<TreeSelectListItem> GetTreeSelectListItems<T>(this IQueryable<T> baseQuery
            , WTMContext? wtmcontext
            , Expression<Func<T, string>> textField
            , Expression<Func<T, object>>? valueField = null
            , Expression<Func<T, string>>? iconField = null
            , Expression<Func<T, string>>? urlField = null
            , Expression<Func<T, string>>? tagField = null
            , Expression<Func<T, bool>>? expandField = null
            , bool ignorDataPrivilege = false
            , bool SortByName = true)
            where T : TreePoco
        {
            var dps = wtmcontext?.LoginUserInfo?.DataPrivileges;
            var query = baseQuery.AsNoTracking();

            //如果没有指定忽略权限，则拼接权限过滤的where条件
            if (ignorDataPrivilege == false)
            {
                query = AppendSelfDPWhere(query, wtmcontext, dps);
            }

            //if (typeof(IPersistPoco).IsAssignableFrom(typeof(T)))
            //{
            //    var mod = new IsValidModifier();
            //    var newExp = mod.Modify(query.Expression);
            //    query = query.Provider.CreateQuery<T>(newExp) as IOrderedQueryable<T>;
            //}

            //处理后面要使用的expression
            if (valueField == null)
            {
                valueField = x => x.GetID()!;
            }
            Expression<Func<T, string>> idfield = x => x.GetID()!.ToString()!;
            Expression<Func<T, string>> parentField = x => x.GetParentID()!.ToString()!;

            //定义PE
            ParameterExpression pe = Expression.Parameter(typeof(T));
            ChangePara cp = new ChangePara();

            //创建新类，形成类似 new SimpleTreeTextAndValue() 的表达式
            NewExpression newItem = Expression.New(typeof(TreeSelectListItem));

            //绑定Text字段，形成类似 Text = textField 的表达式
            var textMI = typeof(TreeSelectListItem).GetMember("Text")[0]!;
            MemberBinding textBind = Expression.Bind(textMI, cp.Change(textField.Body, pe)!);

            //绑定Value字段，形成类似 Value = valueField 的表达式
            var valueMI = typeof(TreeSelectListItem).GetMember("Value")[0]!;
            var temp = cp.Change(valueField.Body, pe)!;
            var tempp = Expression.Call(temp, "ToString", new Type[] { });
            MemberBinding valueBind = Expression.Bind(valueMI, tempp);

            //绑定ParentId字段，形成类似 Value = valueField 的表达式
            var parentMI = typeof(TreeSelectListItem).GetMember("ParentId")[0]!;
            MemberBinding parentBind = Expression.Bind(parentMI, cp.Change(parentField.Body, pe)!);
            //绑定d字段，形成类似 Value = valueField 的表达式
            var IdMI = typeof(TreeSelectListItem).GetMember("Id")[0]!;
            MemberBinding idBind = Expression.Bind(IdMI, cp.Change(idfield.Body, pe)!);

            //绑定Url字段，形成类似 Value = valueField 的表达式
            MemberBinding? urlBind = null;
            var urlMI = typeof(TreeSelectListItem).GetMember("Url")[0]!;
            if (urlField != null)
            {
                urlBind = Expression.Bind(urlMI, cp.Change(urlField.Body, pe)!);
            }
            else
            {
                urlBind = Expression.Bind(urlMI, Expression.Constant(string.Empty));
            }

            //绑定icon字段，形成类似 Icon = iconField 的表达式
            MemberBinding? iconBind = null;
            var iconMI = typeof(TreeSelectListItem).GetMember("Icon")[0]!;
            if (iconField != null)
            {
                iconBind = Expression.Bind(iconMI, cp.Change(iconField.Body, pe)!);
            }
            else
            {
                iconBind = Expression.Bind(iconMI, Expression.Constant(string.Empty));
            }

            //绑定Tag字段，形成类似 Value = valueField 的表达式
            MemberBinding? tagBind = null;
            var tagMI = typeof(TreeSelectListItem).GetMember("Tag")[0]!;
            if (tagField != null)
            {
                tagBind = Expression.Bind(tagMI, cp.Change(tagField.Body, pe)!);
            }
            else
            {
                tagBind = Expression.Bind(tagMI, Expression.Constant(""));
            }

            //绑定Tag字段，形成类似 Value = valueField 的表达式
            MemberBinding? expandBind = null;
            var expandMI = typeof(TreeSelectListItem).GetMember("Expended")[0]!;
            if (expandField != null)
            {
                expandBind = Expression.Bind(expandMI, cp.Change(expandField.Body, pe)!);
            }
            else
            {
                expandBind = Expression.Bind(expandMI, Expression.Constant(false));
            }

            //合并创建新类和绑定字段的表达式，形成类似 new SimpleTextAndValue{ Text = textField, Value = valueField} 的表达式
            MemberInitExpression init = Expression.MemberInit(newItem, textBind, valueBind, iconBind, parentBind, urlBind, tagBind, expandBind, idBind);

            //将最终形成的表达式转化为Lambda，形成类似 x=> new SimpleTextAndValue { Text = x.textField, Value = x.valueField} 的表达式
            var lambda = Expression.Lambda<Func<T, TreeSelectListItem>>(init, pe);

            List<TreeSelectListItem>? rv = null;

            //根据Text对下拉菜单数据排序
            if (SortByName == true)
            {
                rv = [.. query.Select(lambda).OrderBy(x => x.Text)];
            }
            else
            {
                rv = [.. query.Select(lambda)];
            }

            List<TreeSelectListItem> toDel = [];

            rv!.ForEach(x =>
            {
                List<TreeSelectListItem> c = [.. rv.Where(y => y.ParentId == x.Id?.ToString())];
                x.Children = c;
                toDel.AddRange(c);
            });
            toDel.ForEach(x => rv.Remove(x));
            return rv;
        }

        #endregion

        #region 下拉
        /// <summary>
        /// 查询数据源，并转化成SelectListItem列表
        /// </summary>
        /// <typeparam name="T">数据源类型</typeparam>
        /// <param name="baseQuery">基础查询</param>
        /// <param name="wtmcontext">Wtm Context</param>
        /// <param name="textField">SelectListItem中Text字段对应的值</param>
        /// <param name="valueField">SelectListItem中Value字段对应的值，默认为Id列</param>
        /// <param name="ignorDataPrivilege">忽略数据权限判断</param>
        /// <param name="SortByName">是否根据Text字段排序，默认为是</param>
        /// <returns>SelectListItem列表</returns>
        public static List<ComboSelectListItem> GetSelectListItems<T>(this IQueryable<T> baseQuery
            , WTMContext? wtmcontext
            , Expression<Func<T, string>> textField
            , Expression<Func<T, object>>? valueField = null
            , bool ignorDataPrivilege = false
            , bool SortByName = true)
            where T : TopBasePoco
        {
            var dps = wtmcontext?.LoginUserInfo?.DataPrivileges;
            var query = baseQuery.AsNoTracking();

            //如果value字段为空，则默认使用Id字段作为value值
            if (valueField == null)
            {
                valueField = x => x.GetID()!;
            }

            //如果没有指定忽略权限，则拼接权限过滤的where条件
            if (ignorDataPrivilege == false)
            {
                query = AppendSelfDPWhere(query, wtmcontext, dps);
            }

            //if (typeof(IPersistPoco).IsAssignableFrom(typeof(T)))
            //{
            //    var mod = new IsValidModifier();
            //    var newExp = mod.Modify(query.Expression);
            //    query = query.Provider.CreateQuery<T>(newExp) as IOrderedQueryable<T>;
            //}


            //定义PE
            ParameterExpression pe = Expression.Parameter(typeof(T));
            ChangePara cp = new ChangePara();
            //创建新类，形成类似 new SimpleTextAndValue() 的表达式
            NewExpression newItem = Expression.New(typeof(ComboSelectListItem));

            //绑定Text字段，形成类似 Text = textField 的表达式
            var textMI = typeof(ComboSelectListItem).GetMember("Text")[0]!;
            MemberBinding textBind = Expression.Bind(textMI, cp.Change(textField.Body, pe)!);


            //绑定Value字段，形成类似 Value = valueField 的表达式
            var valueMI = typeof(ComboSelectListItem).GetMember("Value")[0]!;
            var temp = cp.Change(valueField.Body, pe)!;
            var tempp = Expression.Call(temp, "ToString", new Type[] { });
            MemberBinding valueBind = Expression.Bind(valueMI, tempp);

            //如果是树形结构，给ParentId赋值
            MemberBinding? parentBind = null;
            var parentMI = typeof(ComboSelectListItem).GetMember("ParentId")[0]!;
            if (typeof(TreePoco).IsAssignableFrom(typeof(T)))
            {
                var parentMember = Expression.MakeMemberAccess(pe, typeof(T).GetSingleProperty("ParentId")!);
                var p = Expression.Call(parentMember, "ToString", new Type[] { });
                //var p1 = Expression.Call(p, "ToLower", new Type[] { });
                parentBind = Expression.Bind(parentMI, p);
            }
            else
            {
                parentBind = Expression.Bind(parentMI, Expression.Constant(string.Empty));
            }

            //合并创建新类和绑定字段的表达式，形成类似 new SimpleTextAndValue{ Text = textField, Value = valueField} 的表达式
            MemberInitExpression init = Expression.MemberInit(newItem, textBind, valueBind, parentBind);

            //将最终形成的表达式转化为Lambda，形成类似 x=> new SimpleTextAndValue { Text = x.textField, Value = x.valueField} 的表达式
            var lambda = Expression.Lambda<Func<T, ComboSelectListItem>>(init, pe);


            List<ComboSelectListItem> rv = [];
            //根据Text对下拉菜单数据排序
            if (SortByName == true)
            {
                rv = [.. query.Select(lambda).OrderBy(x => x.Text)];
            }
            else
            {
                rv = [.. query.Select(lambda)];
            }

            return rv;
        }

        #endregion

        /// <summary>
        /// 拼接本表的数据权限过滤
        /// </summary>
        /// <typeparam name="T">数据类</typeparam>
        /// <param name="query">源query</param>
        /// <param name="wtmcontext">Wtm context</param>
        /// <param name="dps">数据权限列表</param>
        /// <returns>拼接好where条件的query</returns>
        private static readonly MethodInfo _appendSelfDPWhereMethod =
            typeof(DCExtension).GetMethod("AppendSelfDPWhere", BindingFlags.Static | BindingFlags.NonPublic)!;

        /// <summary>
        /// #843 observability: element-type names (via <c>Type.FullName</c>) that have already
        /// logged a fail-closed denial in <see cref="ApplyDataPrivilegeForAnalysis"/> during this
        /// process's lifetime. Backs the "log once per element type per process" throttle — see
        /// that method's doc comment for why this cadence was chosen over per-call or per-job-run.
        /// </summary>
        private static readonly ConcurrentDictionary<string, byte> _loggedNoIdentityDenials = new();

        /// <summary>
        /// Analysis Mode 防禦性 DataPrivilege 過濾（#554）。
        /// 對非泛型 IQueryable 套用 AppendSelfDPWhere，確保 Analysis 查詢受同一行級權限保護。
        /// </summary>
        /// <param name="baseQuery">未過濾的查詢來源。</param>
        /// <param name="wtmcontext">
        /// 呼叫端的 <see cref="WTMContext"/>。<see cref="WTMContext.LoginUserInfo"/> 為 <c>null</c>
        /// 代表沒有可信身分 — 典型情況是背景執行：
        /// <see cref="WalkingTec.Mvvm.Core.Dashboard.Snapshot.DashboardSnapshotJob"/>、
        /// <see cref="WalkingTec.Mvvm.Core.Dashboard.Alerting.DashboardAlertHostedService"/> 透過
        /// <c>IServiceProvider.CreateScope()</c> 解析出的 <see cref="WTMContext"/> 沒有
        /// <c>HttpContext</c> 可讀，因此永遠不帶 <c>LoginUserInfo</c>。
        /// </param>
        /// <param name="declaredSystemQuery">
        /// #843：顯式宣告「這是不受列級 DataPrivilege 限制的系統查詢」。預設 <c>false</c>。
        /// <para>
        /// 修法之前（#554 之後、#843 之前），<c>LoginUserInfo == null</c> 會直接跳過過濾、回傳
        /// 未過濾的查詢 — 等同把「沒有身分」當成「不過濾」，而不是「不放行」。背景 job 因此能看到
        /// 使用者自己透過 HTTP 建立、且受 DataPrivilege 限制的 widget/查詢設定所指向的資料，即使
        /// 那個使用者自己在互動路徑上受同一組規則限制、根本看不到那些列。
        /// </para>
        /// <para>
        /// 現在 <c>LoginUserInfo == null</c> 預設會改為呼叫 <see cref="AppendSelfDPWhere"/>（傳入
        /// <c>dps = null</c>）——沿用它既有處理「已認證但沒被指派任何 RelateId」的邏輯（該 model
        /// 有設定 DataPrivilege 規則時，<c>dps == null</c> 產生 <c>1 != 1</c> 全拒），只是把觸發
        /// 條件從「已認證但沒有 RelateId」擴大到「完全沒有身分」。沒有為該 model 設定 DataPrivilege
        /// 規則的查詢不受影響（沿用原本「無規則 = 不過濾」的語意，與是否有身分無關）。
        /// </para>
        /// <para>
        /// 只有呼叫端明確傳入 <c>declaredSystemQuery: true</c>——一個 review 時看得到的具名參數，
        /// 不是設定檔旗標——才會回到「略過 DataPrivilege」的舊行為，且僅在
        /// <c>LoginUserInfo == null</c> 時才生效；已認證的呼叫者（<c>LoginUserInfo != null</c>）
        /// 傳 <c>true</c> 沒有作用，無法用這個參數繞過自己的列級限制。
        /// <see cref="WalkingTec.Mvvm.Core.Dashboard.AnalysisWidgetDataSource"/>
        /// 目前兩個消費者（互動式 Dashboard 檢視與背景 job）都不傳 <c>true</c>：互動路徑一律有
        /// <c>LoginUserInfo</c>，這個分支根本不會被觸發；背景路徑刻意留在預設的 fail-closed，
        /// 因為目前沒有機制能重建「這個 widget 的建立者當初的 DataPrivilege 範圍」（issue #843
        /// 討論過「job 建立者身分」與「租戶系統身分」兩個替代方案，均因需要不存在的新基礎設施而
        /// 否決 — 見 issue 討論與 CHANGELOG）。
        /// </para>
        /// <para>
        /// <b>Observability</b>：fail-closed 預設是**靜默**的隱患——背景 job 因此回傳空結果，
        /// 但沒有任何訊號連到這個原因，operator 只會看到一張空圖表或全零的 snapshot 列。因此，
        /// 每當這個方法因為「無身分 + 該 model 有設定 DataPrivilege 規則」而實際拒絕（不是
        /// no-op）時，會透過 <see cref="CoreProgram.GetLogger(string)"/>（category
        /// <c>"DCExtension"</c>，與 <c>WtmFileProvider</c> 等既有 static helper 相同模式，
        /// 因為此類無法走 DI 注入 <c>ILogger</c>）記一筆 <c>LogWarning</c>，內容含 element type
        /// 名稱與補救方式（<c>declaredSystemQuery: true</c>）。**節流**：每個 element type
        /// 在單一 process 生命週期內只記一次——這是查詢路徑上的 helper，排程 job 可能每幾分鐘
        /// 重跑同一個 widget，逐次呼叫都記會洗版；沒有現成的「job run」邊界可用（那個邊界在
        /// 呼叫端好幾層之上，把它往下傳會擴大這個與 <c>_AnalysisController</c> 共用的 static
        /// helper 的介面，超出這次 observability 修補的範圍），故選擇「每 element type 每
        /// process 一次」，並隨 process 重啟自然重置。
        /// </para>
        /// </param>
        public static IQueryable ApplyDataPrivilegeForAnalysis(
            IQueryable baseQuery,
            WTMContext? wtmcontext,
            bool declaredSystemQuery = false)
        {
            if (wtmcontext?.DataPrivilegeSettings == null) return baseQuery;
            var elementType = baseQuery.ElementType;
            if (!typeof(TopBasePoco).IsAssignableFrom(elementType)) return baseQuery;
            if (wtmcontext.LoginUserInfo == null && declaredSystemQuery)
            {
                // #843: caller explicitly declared this is an intentionally-unfiltered system
                // query — the only sanctioned way back to the old fail-open behaviour.
                return baseQuery;
            }
            if (wtmcontext.LoginUserInfo == null)
            {
                // #843 observability: only warn when this denial is actually consequential —
                // i.e. the model has a DataPrivilege rule configured, so AppendSelfDPWhere below
                // will deny rows rather than no-op. Without this check, every background query
                // against every model (including ones with nothing to protect) would log.
                var elementTypeName = elementType.FullName ?? elementType.Name;
                var isGated = wtmcontext.DataPrivilegeSettings.Any(x => x.ModelName == elementType.Name);
                if (isGated && _loggedNoIdentityDenials.TryAdd(elementTypeName, 0))
                {
                    CoreProgram.GetLogger("DCExtension")?.LogWarning(
                        "ApplyDataPrivilegeForAnalysis denied all rows for '{ElementType}': no " +
                        "identity (WTMContext.LoginUserInfo is null) and a DataPrivilege rule is " +
                        "configured for this model, so the query fails closed by default (#843). " +
                        "This is expected during background execution (DashboardSnapshotJob, " +
                        "DashboardAlertHostedService) and means the resulting widget data or " +
                        "snapshot will be empty for this model. If this specific query is genuinely " +
                        "meant to run unfiltered, pass declaredSystemQuery: true at the call site. " +
                        "Logged once per element type per process.",
                        elementTypeName);
                }
            }
            var method = _appendSelfDPWhereMethod.MakeGenericMethod(elementType);
            var dps = wtmcontext.LoginUserInfo?.DataPrivileges;
            return (IQueryable)method.Invoke(null, new object?[] { baseQuery, wtmcontext, dps })!;
        }

        private static IQueryable<T> AppendSelfDPWhere<T>(IQueryable<T> query, WTMContext? wtmcontext, List<SimpleDataPri>? dps) where T : TopBasePoco
        {
            var dpsSetting = wtmcontext?.DataPrivilegeSettings;
            Type modelTye = typeof(T);
            bool isBasePoco = typeof(IBasePoco).IsAssignableFrom(modelTye);
            ParameterExpression pe = Expression.Parameter(typeof(T));
            Expression peid = Expression.Property(pe, typeof(T).GetSingleProperty("ID")!);
            //循环数据权限，加入到where条件中，达到自动过滤的效果
            if (dpsSetting?.Where(x => x.ModelName == query.ElementType.Name).SingleOrDefault() != null)
            {
                //如果dps参数是空，则生成 1!=1 这种错误的表达式，这样就查不到任何数据了
                if (dps == null)
                {
                    query = query.Where(Expression.Lambda<Func<T, bool>>(Expression.NotEqual(Expression.Constant(1), Expression.Constant(1)), pe));
                }
                else
                {
                    //在dps中找到和baseQuery源数据表名一样的关联id
                    List<string?> ids = [.. dps.Where(x => x.TableName == query.ElementType.Name).Select(x => x.RelateId)];

                    if (ids == null || ids.Count == 0)
                    {
                        //if (isBasePoco == true)
                        //{
                        //    var selfexp = Expression.Equal(Expression.Property(pe, "CreateBy"), Expression.Constant(wtmcontext.LoginUserInfo?.ITCode));
                        //    query = query.Where(Expression.Lambda<Func<T, bool>>(selfexp, pe));
                        //}
                        //else
                        //{
                        query = query.Where(Expression.Lambda<Func<T, bool>>(Expression.NotEqual(Expression.Constant(1), Expression.Constant(1)), pe));
                        //}
                    }
                    else
                    {
                        if (!ids.Contains(null))
                        {
                            query = query.Where(ids.GetContainIdExpression<T>());
                        }
                    }
                }
            }
            return query;
        }

        /// <summary>
        /// 为查询语句添加关联表的权限过滤
        /// </summary>
        /// <typeparam name="T">源数据类</typeparam>
        /// <param name="baseQuery">源Query</param>
        /// <param name="wtmcontext"></param>
        /// <param name="IdFields">关联表外键</param>
        /// <returns>修改后的查询语句</returns>
        //public static IQueryable<T> DPWhere<T>(this IQueryable<T> baseQuery, WTMContext wtmcontext, params Expression<Func<T, object>>[] IdFields) where T : TopBasePoco
        //{
        //    var dps = wtmcontext?.LoginUserInfo?.DataPrivileges;
        //    //循环所有关联外键
        //    List<string> tableNameList = new List<string>();
        //    foreach (var IdField in IdFields)
        //    {

        //        //将外键 Id 用.分割，循环生成指向最终id的表达式，比如x=> x.a.b.Id
        //        var fieldName = IdField.GetPropertyName(false);
        //        //获取关联的类
        //        string typename = "";
        //        //如果外键名称不是‘id’，则根据model层的命名规则，它应该是xxxId，所以抹掉最后的 Id 应该是关联的类名
        //        if (fieldName.ToLower() != "id")
        //        {
        //            fieldName = fieldName.Remove(fieldName.Length - 2);
        //            var dtype = IdField.GetPropertyInfo().DeclaringType;
        //            if (dtype == typeof(TreePoco) && fieldName == "Parent")
        //            {
        //                typename = typeof(T).Name;
        //            }
        //            else
        //            {
        //                typename = dtype.GetSingleProperty(fieldName).PropertyType.Name;
        //            }
        //        }
        //        //如果是 Id，则本身就是关联的类
        //        else
        //        {
        //            typename = typeof(T).Name;
        //        }
        //        tableNameList.Add(typename);

        //    }
        //    //var test = DPWhere(baseQuery, dps, tableNameList, IdFields);
        //    return DPWhere(baseQuery, wtmcontext, tableNameList, IdFields);
        //}

        public static Expression<Func<TModel, bool>> GetContainIdExpression<TModel>(this List<string?> Ids, Expression? peid = null)
        {
            ParameterExpression pe = Expression.Parameter(typeof(TModel));
            var rv = Ids.GetContainIdExpression(typeof(TModel), pe, peid) as Expression<Func<TModel, bool>>;
            return rv!;
        }

        public static LambdaExpression GetContainIdExpression(this List<string?> Ids, Type modeltype, ParameterExpression pe, Expression? peid = null)
        {
            if (Ids == null)
            {
                Ids = [];
            }
            if (peid == null)
            {
                peid = Expression.Property(pe, modeltype.GetSingleProperty("ID")!);
            }
            else
            {
                ChangePara cp = new ChangePara();
                peid = cp.Change(peid, pe);
                if (peid is LambdaExpression expression)
                {
                    peid = expression.Body;
                }
            }
            var propertype = peid!.GetPropertyInfo()!.PropertyType;
            var listtype = typeof(List<>).MakeGenericType(propertype);
            var list = listtype.GetConstructor(Type.EmptyTypes)!.Invoke(null)!;
            var add = listtype.GetMethod("Add");
            foreach (var item in Ids)
            {
                object? vv = PropertyHelper.ConvertValue(item, peid.Type);
                add?.Invoke(list, new object?[] { vv });
            }
            Expression dpleft = Expression.Constant(list);
            Expression dpcondition = Expression.Call(dpleft, listtype.GetMethod("Contains")!, peid);
            var rv = Expression.Lambda(typeof(Func<,>).MakeGenericType(modeltype, typeof(bool)), dpcondition, pe);
            return rv;
        }

        /// <summary>
        /// 开始一个事务，当使用同一IDataContext时，嵌套的两个事务不会引起冲突，当嵌套的事务执行时引起的异常会通过回滚方法向上层抛出异常
        /// </summary>
        /// <param name="self">DataContext</param>
        /// <returns>可用的事务实例</returns>
        public static Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction BeginTransaction(this IDataContext self)
        {
            if (self == null)
                throw new ArgumentNullException(nameof(self));
            if (self.Database == null)
                throw new ArgumentNullException(nameof(self.Database));
            if (@"Microsoft.EntityFrameworkCore.InMemory".Equals(self.Database.ProviderName, StringComparison.OrdinalIgnoreCase))
                return FakeNestedTransaction.DefaultTransaction;
            return self.Database.CurrentTransaction == null ? self.Database.BeginTransaction() : FakeNestedTransaction.DefaultTransaction;
        }

        /// <summary>
        /// 开始一个事务，当使用同一IDataContext时，嵌套的两个事务不会引起冲突，当嵌套的事务执行时引起的异常会通过回滚方法向上层抛出异常
        /// </summary>
        /// <param name="self">DataContext</param>
        /// <param name="isolationLevel"></param>
        /// <returns>可用的事务实例</returns>
        public static Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction BeginTransaction(this IDataContext self, System.Data.IsolationLevel isolationLevel)
        {
            if (self == null)
                throw new ArgumentNullException(nameof(self));
            if (self.Database == null)
                throw new ArgumentNullException(nameof(self.Database));
            if (@"Microsoft.EntityFrameworkCore.InMemory".Equals(self.Database.ProviderName, StringComparison.OrdinalIgnoreCase))
                return FakeNestedTransaction.DefaultTransaction;
            return self.Database.CurrentTransaction == null ? self.Database.BeginTransaction(isolationLevel) : FakeNestedTransaction.DefaultTransaction;
        }

        /// <summary>
        /// 开始一个异步事务，当使用同一IDataContext时，嵌套的两个事务不会引起冲突。
        /// 对于InMemory provider返回FakeNestedTransaction（no-op事务）；
        /// 否则若已有活跃事务则返回FakeNestedTransaction，否则开启新事务。
        /// </summary>
        /// <param name="self">DataContext</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>可用的事务实例</returns>
        public static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginTransactionAsync(
            this IDataContext self,
            CancellationToken cancellationToken = default)
        {
            if (self == null)
                throw new ArgumentNullException(nameof(self));
            if (self.Database == null)
                throw new ArgumentNullException(nameof(self.Database));
            if (@"Microsoft.EntityFrameworkCore.InMemory".Equals(self.Database.ProviderName, StringComparison.OrdinalIgnoreCase))
                return FakeNestedTransaction.DefaultTransaction;
            if (self.Database.CurrentTransaction != null)
                return FakeNestedTransaction.DefaultTransaction;
            return await self.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal class FakeNestedTransaction : Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction
    {
        internal static readonly FakeNestedTransaction DefaultTransaction = new FakeNestedTransaction();

        private FakeNestedTransaction() { }

        public void Dispose()
        {
        }

        public void Commit()
        {
        }

        public void Rollback()
        {
            throw new TransactionInDoubtException("an exception occurs while executing the nested transaction or processing the results");
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            Rollback(); // throws TransactionInDoubtException for nested-tx, consistent with sync path
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;

        public Guid TransactionId => Guid.Empty;
    }
}
