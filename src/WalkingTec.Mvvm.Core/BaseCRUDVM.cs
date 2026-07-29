#nullable enable
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Models;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory;
using Microsoft.EntityFrameworkCore.Metadata.Internal;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// 单表增删改查VM的接口
    /// </summary>
    /// <typeparam name="T">继承TopBasePoco的类</typeparam>
    public interface IBaseCRUDVM<out T> where T : TopBasePoco, new()
    {
        T Entity { get; }
        /// <summary>
        /// 根据主键Id获取Entity
        /// </summary>
        /// <param name="id">主键Id</param>
        void SetEntityById(object id);

        /// <summary>
        /// 设置Entity
        /// </summary>
        /// <param name="entity">要设定的TopBasePoco</param>
        void SetEntity(object entity);

        /// <summary>
        /// 添加
        /// </summary>
        void DoAdd();

        Task DoAddAsync();

        /// <summary>
        /// 修改
        /// </summary>
        void DoEdit(bool updateAllFields);
        Task DoEditAsync(bool updateAllFields);

        /// <summary>
        /// 删除，对于TopBasePoco进行物理删除，对于PersistPoco把IsValid修改为false
        /// </summary>
        void DoDelete();
        Task DoDeleteAsync();

        /// <summary>
        /// 彻底删除，对PersistPoco进行物理删除
        /// </summary>
        void DoRealDelete();
        Task DoRealDeleteAsync();

        /// <summary>
        /// 将源VM的上数据库上下文，Session，登录用户信息，模型状态信息，缓存信息等内容复制到本VM中
        /// </summary>
        /// <param name="vm">复制的源</param>
        void CopyContext(BaseVM vm);

        /// <summary>
        /// 是否跳过基类的唯一性验证，批量导入的时候唯一性验证会由存储过程完成，不需要单独调用本类的验证方法
        /// </summary>
        bool ByPassBaseValidation { get; set; }

        /// <summary>
        /// True if the last DoEdit / DoEditAsync call failed due to an optimistic concurrency conflict.
        /// </summary>
        bool IsConcurrencyConflict { get; }

        /// <summary>Returns a one-line human-readable label for the entity; used by bulk-delete preview (#619).</summary>
        string GetDeletePreviewString();

        void Validate();
        IModelStateService? MSD { get; }

        /// <summary>
        /// #809: Runs only <see cref="BaseCRUDVM{TModel}.ValidateDuplicateData"/> — the
        /// duplicate-key check — without invoking the rest of the user-overridable
        /// <see cref="Validate"/> chain. See the implementation for the full rationale.
        /// </summary>
        List<object> ValidateDuplicateDataOnly();

        /// <summary>
        /// #809: Appends an "Edit" audit ChangeLog row for callers that persist a change
        /// without going through <c>DoEdit</c>/<c>DoEditPrepare</c>. See the implementation
        /// for the required call ordering.
        /// </summary>
        void AppendEditChangeLog();
    }

    /// <summary>
    /// #705: DoEdit/DoAdd's soft-relation resolution and <see cref="IncludeInfo.SoftSelect"/>
    /// previously called <c>DC.GetType().GetMethod("Set", Type.EmptyTypes)!.MakeGenericMethod(entityType)</c>
    /// on every save — a full reflection method lookup plus a closed-generic build, per
    /// sub-collection property, per request. Cache the open <c>Set&lt;T&gt;()</c> MethodInfo
    /// once per concrete <see cref="IDataContext"/> type, and each closed-generic MethodInfo
    /// per (dcType, entityType) pair, instead of reflecting on every call — mirrors the
    /// _FrameworkController.cs UpdateProperty cache pattern from #34/#663. Shared by both
    /// <see cref="BaseCRUDVM{TModel}"/> (generic) and the non-generic <see cref="IncludeInfo"/>.
    /// </summary>
    internal static class EfSetMethodCache
    {
        private static readonly ConcurrentDictionary<Type, MethodInfo> s_openMethodCache = new();
        private static readonly ConcurrentDictionary<(Type dcType, Type entityType), MethodInfo> s_closedMethodCache = new();

        public static MethodInfo GetClosedSetMethod(Type dcType, Type entityType)
        {
            return s_closedMethodCache.GetOrAdd((dcType, entityType), key =>
            {
                var openMethod = s_openMethodCache.GetOrAdd(key.dcType, t => t.GetMethod("Set", Type.EmptyTypes)!);
                return openMethod.MakeGenericMethod(key.entityType);
            });
        }
    }

    /// <summary>
    /// 单表增删改查基类，所有单表操作的VM应该继承这个基类
    /// </summary>
    /// <typeparam name="TModel">继承TopBasePoco的类</typeparam>
    public class BaseCRUDVM<TModel> : BaseVM, IBaseCRUDVM<TModel> where TModel : TopBasePoco, new()
    {
        internal static readonly MethodInfo IncludeMethodInfo = typeof(EntityFrameworkQueryableExtensions).GetTypeInfo().GetDeclaredMethods("Include").Single((MethodInfo mi) => mi.GetGenericArguments().Count() == 2 && mi.GetParameters().Any((ParameterInfo pi) => pi.Name == "navigationPropertyPath" && pi.ParameterType != typeof(string)));
        internal static readonly MethodInfo ThenIncludeAfterEnumerableMethodInfo = (from mi in typeof(EntityFrameworkQueryableExtensions).GetTypeInfo().GetDeclaredMethods("ThenInclude")
                                                                                    where mi.GetGenericArguments().Count() == 3
                                                                                    select mi).Single(delegate (MethodInfo mi)
                                                                                    {
                                                                                        Type type = mi.GetParameters()[0].ParameterType.GenericTypeArguments[1];
                                                                                        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>);
                                                                                    });

        internal static readonly MethodInfo ThenIncludeAfterReferenceMethodInfo = typeof(EntityFrameworkQueryableExtensions).GetTypeInfo().GetDeclaredMethods("ThenInclude").Single((MethodInfo mi) => mi.GetGenericArguments().Count() == 3 && mi.GetParameters()[0].ParameterType.GenericTypeArguments[1].IsGenericParameter);
        public TModel Entity { get; set; }
        [JsonIgnore]
        public bool ByPassBaseValidation { get; set; }

        /// <summary>
        /// Set to true by DoEdit / DoEditAsync when EF throws DbUpdateConcurrencyException.
        /// </summary>
        [JsonIgnore]
        public bool IsConcurrencyConflict { get; private set; }

        //保存读取时Include的内容
        private List<Expression<Func<TModel, object>>>? _toInclude { get; set; }

        /// <summary>
        /// 构造函数
        /// </summary>
        public BaseCRUDVM()
        {
            //初始化Entity
            var ctor = typeof(TModel).GetConstructor(Type.EmptyTypes);
            Entity = (TModel)ctor!.Invoke(null)!;
            //初始化VM中所有List<>的类
            //var lists = typeof(TModel).GetAllProperties().Where(x => x.PropertyType.IsGeneric(typeof(List<>)));
            //foreach (var li in lists)
            //{
            //    var gs = li.PropertyType.GetGenericArguments();
            //    var newObj = Activator.CreateInstance(typeof(List<>).MakeGenericType(gs[0]));
            //    li.SetValue(Entity, newObj, null);
            //}
        }

        public IQueryable<TModel> GetBaseQuery()
        {
            return DC!.Set<TModel>();
        }

        /// <summary>
        /// Returns a one-line human-readable summary of <see cref="Entity"/> used by the
        /// bulk-delete preview dialog (#619).  Override to provide a richer label.
        /// The default implementation looks for Name / Title / Code / ITCode properties
        /// in that order; falls back to the primary key value.
        /// </summary>
        public virtual string GetDeletePreviewString()
        {
            var searchNames = new[] { "Name", "Title", "Code", "ITCode", "SchoolName", "RoleName" };
            foreach (var n in searchNames)
            {
                var prop = typeof(TModel).GetProperty(n);
                if (prop != null)
                {
                    var v = prop.GetValue(Entity)?.ToString();
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }
            return Entity.GetID()?.ToString() ?? string.Empty;
        }

        /// <summary>
        /// 设定添加和修改时对于重复数据的判断，子类进行相关操作时应重载这个函数
        /// </summary>
        /// <returns>唯一性属性</returns>
        public virtual DuplicatedInfo<TModel>? SetDuplicatedCheck()
        {
            return null;
        }

        /// <summary>
        /// 设定读取是Include的内容
        /// </summary>
        /// <param name="exps">需要关联的类</param>
        public void SetInclude(params Expression<Func<TModel, object>>[] exps)
        {
            _toInclude = _toInclude ?? [];
            _toInclude.AddRange(exps);
        }

        /// <summary>
        /// 根据主键Id设定Entity
        /// </summary>
        /// <param name="id">主键Id</param>
        public void SetEntityById(object id)
        {
            this.Entity = GetById(id);
        }

        /// <summary>
        /// 设置Entity
        /// </summary>
        /// <param name="entity">要设定的TopBasePoco</param>
        public void SetEntity(object entity)
        {
            this.Entity = (entity as TModel)!;
        }

        /// <summary>
        /// 根据主键获取Entity
        /// </summary>
        /// <param name="Id">主键Id</param>
        /// <returns>Entity</returns>
        protected virtual TModel GetById(object Id)
        {
            TModel? rv = null;
            var ModelType = typeof(TModel);
            //建立基础查询
            var query = DC!.Set<TModel>().AsQueryable();
            List<IncludeInfo> includeInfo = [];
            //循环添加其他设定的Include
            if (_toInclude != null)
            {
                foreach (var item in _toInclude)
                {
                    List<IncludeInfo> exps = [];
                    Expression current = item.Body;
                    while (current != null && current.NodeType != ExpressionType.Parameter)
                    {
                        if (current.NodeType == ExpressionType.MemberAccess)
                        {
                            MemberExpression me = (current as MemberExpression)!;
                            Type? mt = me.Member.GetMemberType();
                            Type? testTypt = mt;
                            if (testTypt != null && testTypt.IsList())
                            {
                                testTypt = testTypt.GetGenericArguments()[0];
                            }
                            if (testTypt != null && typeof(TopBasePoco).IsAssignableFrom(testTypt))
                            {
                                IncludeInfo newinfo = new IncludeInfo
                                {
                                    mi = me.Member,
                                    t = mt!
                                };
                                var top = exps.FirstOrDefault();
                                if (top != null)
                                {
                                    newinfo.Next = top;
                                    top.Pre = newinfo;
                                }
                                exps.Insert(0, newinfo);
                            }
                            current = me.Expression!;
                        }
                        else if (current.NodeType == ExpressionType.Call)
                        {
                            MethodCallExpression mc = (current as MethodCallExpression)!;
                            current = mc.Object!;
                        }
                        else if (current.NodeType == ExpressionType.Convert)
                        {
                            UnaryExpression ue = (current as UnaryExpression)!;
                            current = ue.Operand;
                        }
                    }
                    if (exps.Count == 0)
                    {
                        continue;
                    }
                    includeInfo.Add(exps[0]);
                    Expression includeExpression = query.Expression;
                    ParameterExpression para = Expression.Parameter(ModelType, "x");
                    MemberExpression newme = Expression.MakeMemberAccess(para, exps[0].mi);

                    if (exps[0].IsNotMapped)
                    {
                        continue;
                    }
                    includeExpression = Expression.Call(
                             null,
                             IncludeMethodInfo.MakeGenericMethod(ModelType, exps[0].t),
                             includeExpression,
                             Expression.Lambda(newme, new ParameterExpression[] { para }));

                    for (int i = 1; i < exps.Count; i++)
                    {
                        if (exps[i].IsNotMapped)
                        {
                            break; ;
                        }
                        Type stype = exps[i - 1].t;
                        if (stype.IsList())
                        {
                            stype = stype.GetGenericArguments()[0];
                        }
                        para = Expression.Parameter(stype, "s");
                        newme = Expression.MakeMemberAccess(para, exps[i].mi);
                        if (exps[i - 1].t.IsList())
                        {
                            includeExpression = Expression.Call(
                             null,
                             ThenIncludeAfterEnumerableMethodInfo.MakeGenericMethod(ModelType, stype, exps[i].t),
                             includeExpression,
                             Expression.Lambda(newme, new ParameterExpression[] { para }));
                        }
                        else
                        {
                            includeExpression = Expression.Call(
                             null,
                             ThenIncludeAfterReferenceMethodInfo.MakeGenericMethod(ModelType, stype, exps[i].t),
                             includeExpression,
                             Expression.Lambda(newme, new ParameterExpression[] { para }));
                        }
                    }
                    query = query.Provider.CreateQuery<TModel>(includeExpression);
                }
            }

            List<IncludeInfo> softincludes = [.. includeInfo.Where(x => x.HasNotMapped == true)];
            if (softincludes.Count > 0)
            {
                ParameterExpression pe = Expression.Parameter(ModelType);
                NewExpression newItem = Expression.New(ModelType);

                var pp = ModelType.GetAllProperties();
                List<MemberBinding> bindExps = [];
                List<string> existname = [];
                foreach (var pro in pp)
                {
                    if (existname.Contains(pro.Name))
                    {
                        continue;
                    }
                    if (pro.GetCustomAttribute<NotMappedAttribute>() == null)
                    {
                        if ((pro.PropertyType.IsList() == false && typeof(TopBasePoco).IsAssignableFrom(pro.PropertyType) == false) || includeInfo.Any(x=>x.t == pro.PropertyType))
                        {
                            var right = Expression.MakeMemberAccess(pe, pro);
                            if (right != null)
                            {
                                MemberBinding bind = Expression.Bind(pro, right);
                                bindExps.Add(bind);
                                existname.Add(pro.Name);
                            }
                        }
                    }
                    else
                    {
                        var soft = softincludes.Where(x => x.mi.Name == pro.Name).FirstOrDefault();
                        if (soft != null)
                        {
                            var right = soft.SoftSelect(pe, DC!);
                            if (right != null)
                            {
                                MemberBinding bind = Expression.Bind(pro, right);
                                bindExps.Add(bind);
                            }
                        }
                    }
                }
                MemberInitExpression init = Expression.MemberInit(newItem, bindExps);
                var lambda = Expression.Lambda<Func<TModel, TModel>>(init, pe);
                query = query.Select(lambda);
            }
            //获取数据
            rv = query.CheckID(Id).AsNoTracking().FirstOrDefault();
            if (rv == null)
            {
                throw new Exception("数据不存在");
            }
            //如果TopBasePoco有关联的附件，则自动Include 附件名称
            var pros = typeof(TModel).GetAllProperties();
            List<PropertyInfo> fa = [.. pros.Where(x => x.PropertyType == typeof(FileAttachment))];
            foreach (var f in fa)
            {
                var fname = DC!.GetFKName2<TModel>(f.Name);
                var fid = typeof(TModel).GetSingleProperty(fname)?.GetValue(rv);
                if (fid != null && Wtm?.ServiceProvider != null)
                {
                    var fp = Wtm.ServiceProvider.GetRequiredService<WtmFileProvider>();
                    var file = fp.GetFile(fid!.ToString()!, false, DC!);
                    rv.SetPropertyValue(f.Name, file);
                }
            }
            return rv;
        }

        /// <summary>
        /// 添加，进行默认的添加操作。子类如有自定义操作应重载本函数
        /// </summary>
        public virtual void DoAdd()
        {
            if (!DoAddPrepare())
            {
                // #815 sixth/seventh round: a FileAttachment FK whose column EF Core's own model
                // says is required (e.g. ISubFile's Guid FileId used as TModel's own scalar, or a
                // [Required] Guid? FK) with no legitimate prior value to revert to was rejected at
                // the request level — see the doc comment on ApplyFileAttachmentResolution. MSD
                // already carries the model error; nothing was staged for insertion, so there is
                // nothing to save.
                return;
            }
            AppendChangeLog("Add", null, SerializeScalarProps(Entity));
            // Persist to DB first; only delete orphaned files after a successful save
            // so that a failed insert does not leave files permanently deleted (Issue #104, Bug 3).
            DC!.SaveChanges();
            if (DeletedFileIds != null && DeletedFileIds.Count > 0 && Wtm?.ServiceProvider != null)
            {
                var fp = Wtm.ServiceProvider.GetRequiredService<WtmFileProvider>();

                // #815: Add creates a brand-new row, so there is no pre-existing DB state it
                // could have legitimately referenced yet — FilterLegitimateDeletedFileIds(null)
                // always yields nothing. See the method doc comment for the full rationale.
                // Accurately: this loop is PROVABLY UNREACHABLE, not an active defence layer —
                // it is kept only for structural symmetry with the other three DeletedFileIds
                // call sites (so all four look identical and a future refactor cannot
                // accidentally drop the guard from just this one). DeleteFileTenantScoped never
                // actually executes here.
                // Known consequence: "upload then cancel before saving" now leaves a permanent
                // orphan FileAttachment row + blob on this path (pre-#815 cleanup here was
                // insecure — any id could be named). Documented in
                // docs/production-readiness.md; reaper tracked as Issue #822.
                foreach (var item in FilterLegitimateDeletedFileIds(null))
                {
                    fp.DeleteFileTenantScoped(item, DC!);
                }
            }
        }

        public virtual async Task DoAddAsync()
        {
            // #815 fifth round: the async variant of DoAddPrepare — uses the awaited batched
            // file-reference resolution instead of the sync one, so this async request path
            // never blocks a ThreadPool thread on it.
            if (!await DoAddPrepareAsync())
            {
                // #815 sixth round: see the sync DoAdd's matching guard above.
                return;
            }
            AppendChangeLog("Add", null, SerializeScalarProps(Entity));
            // Persist to DB first; only delete orphaned files after a successful save
            // so that a failed insert does not leave files permanently deleted (Issue #104, Bug 3).
            await DC!.SaveChangesAsync();
            if (DeletedFileIds != null && DeletedFileIds.Count > 0 && Wtm?.ServiceProvider != null)
            {
                var fp = Wtm.ServiceProvider.GetRequiredService<WtmFileProvider>();

                // #815: see DoAdd — a brand-new row has no pre-existing file reference to
                // validate DeletedFileIds against. This loop is likewise provably unreachable,
                // kept only for structural symmetry with the other three call sites.
                foreach (var item in FilterLegitimateDeletedFileIds(null))
                {
                    fp.DeleteFileTenantScoped(item, DC!.ReCreate());
                }
            }
        }

        /// <summary>
        /// Sync entry point used by <see cref="DoAdd"/>. Runs <see cref="DoAddPrepareCore"/>,
        /// then the SYNC file-reference gate (<see cref="RejectUnresolvableFileAttachmentReferences"/>),
        /// then stages <see cref="Entity"/> for insertion. See
        /// <see cref="DoAddPrepareAsync"/> for the async counterpart used by
        /// <see cref="DoAddAsync"/> (Issue #815 fifth round — batching + async).
        /// </summary>
        /// <returns>
        /// <see langword="false"/> when the file-reference gate rejected the whole request
        /// (Issue #815 sixth/seventh round — the FK's column does not accept NULL, per EF
        /// Core's own model, and there is no legitimate prior value to revert to) — in that case
        /// <see cref="Entity"/> is deliberately NOT staged for insertion, and the caller
        /// (<see cref="DoAdd"/>/<see cref="DoAddAsync"/>) must not call <c>SaveChanges</c>.
        /// <see langword="true"/> otherwise.
        /// </returns>
        private bool DoAddPrepare()
        {
            DoAddPrepareCore();

            // #815 rework: reject a posted FileAttachment FK the caller cannot resolve for
            // their own tenant BEFORE it is ever written — see the method doc comment on
            // RejectUnresolvableFileAttachmentReferences. Add has no pre-existing DB state
            // (preSaveSnapshot: null), so an unresolvable reference reverts to null, UNLESS EF
            // Core's own model says the FK's column is required (#815 sixth/seventh round), in
            // which case there is no safe "null" to revert to and the whole request is rejected
            // instead.
            if (RejectUnresolvableFileAttachmentReferences(null))
            {
                return false;
            }

            //添加数据
            DC!.Set<TModel>().Add(Entity);
            return true;
        }

        /// <summary>
        /// Async counterpart of <see cref="DoAddPrepare"/> used by <see cref="DoAddAsync"/> —
        /// same <see cref="DoAddPrepareCore"/> call, but awaits the ASYNC batched file-reference
        /// gate (<see cref="RejectUnresolvableFileAttachmentReferencesAsync"/>) instead of
        /// running it synchronously, so the async request path never blocks a ThreadPool thread
        /// on it (Issue #815 fifth round). See <see cref="DoAddPrepare"/> for the meaning of the
        /// returned <see cref="bool"/> (Issue #815 sixth round).
        /// </summary>
        private async Task<bool> DoAddPrepareAsync()
        {
            DoAddPrepareCore();
            if (await RejectUnresolvableFileAttachmentReferencesAsync(null))
            {
                return false;
            }
            DC!.Set<TModel>().Add(Entity);
            return true;
        }

        /// <summary>
        /// Shared preparation logic for both <see cref="DoAddPrepare"/> and
        /// <see cref="DoAddPrepareAsync"/> — everything EXCEPT the file-reference gate and the
        /// final <c>Add</c>, which the two callers run themselves (sync vs. async) so the
        /// file-reference resolution query can be sync or awaited without duplicating this whole
        /// method.
        /// </summary>
        private void DoAddPrepareCore()
        {
            var pros = typeof(TModel).GetAllProperties();
            //将所有TopBasePoco的属性赋空值，防止添加关联的重复内容
            if (typeof(TModel) != typeof(FileAttachment))
            {
                foreach (var pro in pros)
                {
                    if (pro.PropertyType.GetTypeInfo().IsSubclassOf(typeof(TopBasePoco)))
                    {
                        pro.SetValue(Entity, null);
                        string fkname = DC!.GetFKName2<TModel>(pro.Name);
                        var fkpro = pros.Where(x => x.Name == fkname).FirstOrDefault();
                        if (fkpro != null)
                        {
                            if (fkpro.PropertyType == typeof(string) && fkpro.GetValue(Entity)?.ToString() == "")
                            {
                                fkpro.SetValue(Entity, null);
                            }
                        }
                    }
                }
            }
            //自动设定添加日期和添加人
            if (typeof(IBasePoco).IsAssignableFrom(typeof(TModel)))
            {
                IBasePoco ent = (Entity as IBasePoco)!;
                if (ent.CreateTime == null)
                {
                    ent.CreateTime = Wtm!.TimeProvider.GetLocalNow().DateTime;
                }
                if (string.IsNullOrEmpty(ent.CreateBy))
                {
                    ent.CreateBy = LoginUserInfo?.ITCode;
                }
            }
            if (typeof(ITenant).IsAssignableFrom(typeof(TModel)))
            {
                ITenant ent = (Entity as ITenant)!;
                ent.TenantCode = LoginUserInfo?.CurrentTenant;
            }
            if (typeof(IPersistPoco).IsAssignableFrom(typeof(TModel)))
            {
                (Entity as IPersistPoco)!.IsValid = true;
            }

            #region 更新子表
            foreach (var pro in pros)
            {
                //找到类型为List<xxx>的字段
                if (pro.PropertyType.GenericTypeArguments.Count() > 0)
                {
                    //获取xxx的类型
                    var ftype = pro.PropertyType.GenericTypeArguments.First();
                    //如果xxx继承自TopBasePoco
                    if (ftype.IsSubclassOf(typeof(TopBasePoco)))
                    {
                        string softkey = "";
                        //界面传过来的子表数据
                        IEnumerable<TopBasePoco>? list = pro.GetValue(Entity) as IEnumerable<TopBasePoco>;
                        if (list != null && list.Count() > 0)
                        {
                            string fkname = DC!.GetFKName<TModel>(pro.Name);
                            if (string.IsNullOrEmpty(fkname))
                            {
                                if (pro.GetCustomAttribute<NotMappedAttribute>() != null)
                                {
                                    fkname = pro.GetCustomAttribute<SoftFKAttribute>()?.PropertyName ?? "";
                                    softkey = typeof(TModel).GetCustomAttribute<SoftKeyAttribute>()?.PropertyName ?? "";
                                }
                            }
                            var itemPros = ftype.GetAllProperties();

                            bool found = false;
                            foreach (var newitem in list)
                            {
                                foreach (var itempro in itemPros)
                                {
                                    if (itempro.PropertyType.IsSubclassOf(typeof(TopBasePoco)))
                                    {
                                        itempro.SetValue(newitem, null);
                                    }
                                    if (!string.IsNullOrEmpty(fkname))
                                    {
                                        if (itempro.Name.ToLower() == fkname.ToLower())
                                        {
                                            try
                                            {
                                                itempro.SetValue(newitem, string.IsNullOrEmpty(softkey) ? Entity.GetID() : Entity.GetPropertyValue(softkey));
                                            }
                                            catch (Exception ex)
                                            {
                                                Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(ex, "Failed to set FK property '{Property}' on sub-entity during DoAdd", itempro.Name);
                                            }
                                            found = true;
                                        }
                                    }
                                }
                                if (string.IsNullOrEmpty(softkey) == false)
                                {
                                    DC!.AddEntity(newitem);
                                }
                            }
                            //如果没有找到相应的外建字段，则可能是多对多的关系，或者做了特殊的设定，这种情况框架无法支持，直接退出本次循环
                            if (found == false)
                            {
                                continue;
                            }
                            //循环页面传过来的子表数据,自动设定添加日期和添加人
                            foreach (var newitem in list)
                            {
                                var subtype = newitem.GetType();
                                if (typeof(IBasePoco).IsAssignableFrom(subtype))
                                {
                                    IBasePoco ent = (newitem as IBasePoco)!;
                                    if (ent.CreateTime == null)
                                    {
                                        ent.CreateTime = Wtm!.TimeProvider.GetLocalNow().DateTime;
                                    }
                                    if (string.IsNullOrEmpty(ent.CreateBy))
                                    {
                                        ent.CreateBy = LoginUserInfo?.ITCode;
                                    }
                                }
                                if (typeof(ITenant).IsAssignableFrom(subtype))
                                {
                                    ITenant ent = (newitem as ITenant)!;
                                    ent.TenantCode = LoginUserInfo?.CurrentTenant;
                                }
                            }
                        }
                    }
                }
            }
            #endregion
        }

        /// <summary>
        /// 修改，进行默认的修改操作。子类如有自定义操作应重载本函数
        /// </summary>
        /// <param name="updateAllFields">为true时，框架会更新当前Entity的全部值，为false时，框架会检查Request.Form里的key，只更新表单提交的字段</param>
        public virtual void DoEdit(bool updateAllFields = false)
        {
            var _snapshotResult = LoadEntitySnapshot();
            if (!_snapshotResult.Succeeded)
            {
                // #875 (third site): a snapshot-load failure must never be treated as "no prior
                // snapshot exists" — RejectUnresolvableFileAttachmentReferences/
                // ApplyFileAttachmentResolution below reads preSaveSnapshot == null as "this is an
                // Add, there is no prior state" and, on that reading, skips
                // LoadExistingSubItemFileIds' restore-vs-drop check entirely for every rejected
                // sub-item. A caller re-posting an EXISTING child with an unresolvable FileId would
                // then be silently DROPPED instead of restored — the same emptying-the-collection
                // → DoEditPreparePart2's Count()==0 branch → delete-every-existing-child chain
                // Issue #828/#875 already closed for the other two sites, reached here through a
                // spurious Add-shape misclassification of a real Edit instead of a resolution-query
                // failure. See LoadEntitySnapshot's doc comment.
                MSD?.AddModelError(" ", Localizer?["Sys.EditFailed"] ?? "Edit failed");
                return;
            }
            var _auditSnapshot = _snapshotResult.Snapshot;
            if (!DoEditPrepare(updateAllFields, _auditSnapshot))
            {
                // #815 sixth/seventh round: the file-reference gate rejected the whole request (a
                // required FK column, per EF Core's own model, with no legitimate prior value to
                // revert to) — MSD already carries the model error; the sub-table diff/update in
                // DoEditPreparePart2 never ran, so nothing was staged and there is nothing to
                // save. See ApplyFileAttachmentResolution's doc comment.
                return;
            }
            AppendChangeLog("Edit", SerializeScalarProps(_auditSnapshot), SerializeScalarProps(Entity));

            // Track whether SaveChanges succeeded so we only delete files on success
            // (Issue #104, Bug 2).
            bool saved = false;
            try
            {
                DC!.SaveChanges();
                saved = true;
            }
            catch (DbUpdateConcurrencyException)
            {
                IsConcurrencyConflict = true;
                MSD?.AddModelError(" ", Localizer?["Sys.ConcurrencyConflict"] ?? "The record was modified by another user. Please reload and try again.");
            }
            catch
            {
                MSD?.AddModelError(" ", Localizer?["Sys.EditFailed"] ?? "Edit failed");
            }
            //删除不需要的附件 — only when the DB save succeeded (Issue #104, Bug 2)
            if (saved && DeletedFileIds != null && DeletedFileIds.Count > 0 && Wtm?.ServiceProvider != null)
            {
                var fp = Wtm.ServiceProvider.GetRequiredService<WtmFileProvider>();

                // #815: validated against _auditSnapshot — the entity's DB state as it existed
                // BEFORE this edit — never against Entity's own posted (attacker-controlled)
                // properties. See FilterLegitimateDeletedFileIds' doc comment.
                // #815 rework: FilterLegitimateDeletedFileIds alone is not sufficient — a caller
                // can forge the entity's own FK to a foreign file in a first request, then name
                // that same id in DeletedFileIds in a second request, and the pre-edit snapshot
                // used above legitimately (but wrongly) contains it. DeleteFileTenantScoped is the
                // primary control that actually closes the cross-tenant version of that bypass by
                // refusing to resolve a FileAttachment outside the caller's own tenant, regardless
                // of FileUploadOptions.EnforceTenantFileScope.
                foreach (var item in FilterLegitimateDeletedFileIds(_auditSnapshot))
                {
                    fp.DeleteFileTenantScoped(item, DC!.ReCreate());
                }
            }

        }

        public virtual async Task DoEditAsync(bool updateAllFields = false)
        {
            var _snapshotResult = await LoadEntitySnapshotAsync();
            if (!_snapshotResult.Succeeded)
            {
                // #875 (third site): see the sync DoEdit's matching guard above.
                MSD?.AddModelError(" ", Localizer?["Sys.EditFailed"] ?? "Edit failed");
                return;
            }
            var _auditSnapshot = _snapshotResult.Snapshot;
            // #815 fifth round: the async variant of DoEditPrepare — uses the awaited batched
            // file-reference resolution instead of the sync one, so this async request path
            // never blocks a ThreadPool thread on it.
            if (!await DoEditPrepareAsync(updateAllFields, _auditSnapshot))
            {
                // #815 sixth round: see the sync DoEdit's matching guard above.
                return;
            }
            AppendChangeLog("Edit", SerializeScalarProps(_auditSnapshot), SerializeScalarProps(Entity));

            // Track whether SaveChangesAsync succeeded so we only delete files on success
            // (Issue #104, Bug 2).
            bool saved = false;
            try
            {
                await DC!.SaveChangesAsync();
                saved = true;
            }
            catch (DbUpdateConcurrencyException)
            {
                IsConcurrencyConflict = true;
                MSD?.AddModelError(" ", Localizer?["Sys.ConcurrencyConflict"] ?? "The record was modified by another user. Please reload and try again.");
            }
            catch
            {
                MSD?.AddModelError(" ", Localizer?["Sys.EditFailed"] ?? "Edit failed");
            }
            //删除不需要的附件 — only when the DB save succeeded (Issue #104, Bug 2)
            if (saved && DeletedFileIds != null && DeletedFileIds.Count > 0 && Wtm?.ServiceProvider != null)
            {
                var fp = Wtm.ServiceProvider.GetRequiredService<WtmFileProvider>();

                // #815: same pre-edit-snapshot validation as the sync DoEdit above.
                // #815 rework: same DeleteFileTenantScoped primary control as the sync DoEdit
                // above — see its comment for why FilterLegitimateDeletedFileIds alone is not
                // sufficient.
                foreach (var item in FilterLegitimateDeletedFileIds(_auditSnapshot))
                {
                    fp.DeleteFileTenantScoped(item, DC!);
                }
            }
        }

        /// <summary>
        /// Sync entry point used by <see cref="DoEdit"/> (and <see cref="DoDelete"/>'s
        /// soft-delete path). Runs <see cref="DoEditPreparePart1"/>, then the SYNC
        /// file-reference gate (<see cref="RejectUnresolvableFileAttachmentReferences"/>), then
        /// <see cref="DoEditPreparePart2"/>. See <see cref="DoEditPrepareAsync"/> for the async
        /// counterpart used by <see cref="DoEditAsync"/>/<see cref="DoDeleteAsync"/> (Issue #815
        /// fifth round — batching + async).
        /// </summary>
        /// <returns>
        /// <see langword="false"/> when the file-reference gate rejected the whole request
        /// (Issue #815 sixth round) — <see cref="DoEditPreparePart2"/> deliberately does NOT
        /// run, so no sub-table diff/update or scalar <c>UpdateProperty</c> is staged, and the
        /// caller must not call <c>SaveChanges</c>. <see langword="true"/> otherwise.
        /// </returns>
        private bool DoEditPrepare(bool updateAllFields, TModel? preSaveSnapshot)
        {
            var pros = DoEditPreparePart1();

            // #815 rework: reject a posted FileAttachment FK the caller cannot resolve for
            // their own tenant BEFORE it is written by the UpdateProperty/UpdateEntity calls
            // below — see the method doc comment on RejectUnresolvableFileAttachmentReferences.
            // Must run after the navigation-property nulling above (Entity's posted FK scalars
            // are already in their final pre-save form by this point) and before every write
            // path further down in this method.
            if (RejectUnresolvableFileAttachmentReferences(preSaveSnapshot))
            {
                return false;
            }

            DoEditPreparePart2(updateAllFields, pros);
            return true;
        }

        /// <summary>
        /// Async counterpart of <see cref="DoEditPrepare"/> used by
        /// <see cref="DoEditAsync"/>/<see cref="DoDeleteAsync"/> — same
        /// <see cref="DoEditPreparePart1"/>/<see cref="DoEditPreparePart2"/> calls, but awaits
        /// the ASYNC batched file-reference gate
        /// (<see cref="RejectUnresolvableFileAttachmentReferencesAsync"/>) instead of running it
        /// synchronously, so the async request path never blocks a ThreadPool thread on it
        /// (Issue #815 fifth round). See <see cref="DoEditPrepare"/> for the meaning of the
        /// returned <see cref="bool"/> (Issue #815 sixth round).
        /// </summary>
        private async Task<bool> DoEditPrepareAsync(bool updateAllFields, TModel? preSaveSnapshot)
        {
            var pros = DoEditPreparePart1();
            if (await RejectUnresolvableFileAttachmentReferencesAsync(preSaveSnapshot))
            {
                return false;
            }
            DoEditPreparePart2(updateAllFields, pros);
            return true;
        }

        /// <summary>
        /// First half of the shared <c>DoEditPrepare</c> logic — everything BEFORE the
        /// file-reference gate: <c>UpdateTime</c>/<c>UpdateBy</c> stamping and nulling
        /// <see cref="TopBasePoco"/> navigation properties (so their FK scalars are in final
        /// pre-save form before <see cref="RejectUnresolvableFileAttachmentReferences"/> reads
        /// them). Returns the cached property list so
        /// <see cref="DoEditPreparePart2"/> does not need to look it up again.
        /// </summary>
        private List<PropertyInfo> DoEditPreparePart1()
        {
            if (typeof(IBasePoco).IsAssignableFrom(typeof(TModel)))
            {
                IBasePoco ent = (Entity as IBasePoco)!;
                //if (ent.UpdateTime == null)
                //{
                ent.UpdateTime = Wtm!.TimeProvider.GetLocalNow().DateTime;
                //}
                //if (string.IsNullOrEmpty(ent.UpdateBy))
                //{
                ent.UpdateBy = LoginUserInfo?.ITCode;
                //}
            }
            var pros = typeof(TModel).GetAllProperties();
            //pros = pros.Where(x => x.CustomAttributes.Any(y => y.AttributeType == typeof(NotMappedAttribute)) == false).ToList();
            if (typeof(TModel) != typeof(FileAttachment))
            {
                foreach (var pro in pros)
                {
                    if (pro.PropertyType.GetTypeInfo().IsSubclassOf(typeof(TopBasePoco)))
                    {
                        pro.SetValue(Entity, null);
                        string fkname = DC!.GetFKName2<TModel>(pro.Name);
                        var fkpro = pros.Where(x => x.Name == fkname).FirstOrDefault();
                        if (fkpro != null)
                        {
                            if (fkpro.PropertyType == typeof(string) && fkpro.GetValue(Entity)?.ToString() == "")
                            {
                                fkpro.SetValue(Entity, null);
                            }
                        }
                    }
                }
            }
            return pros;
        }

        /// <summary>
        /// Second half of the shared <c>DoEditPrepare</c> logic — everything AFTER the
        /// file-reference gate: the 更新子表 (sub-table diff/update) block and the final
        /// scalar-field <c>UpdateProperty</c>/<c>UpdateEntity</c> calls. Takes
        /// <paramref name="pros"/> from <see cref="DoEditPreparePart1"/> instead of
        /// recomputing it (the lookup is cached either way, but this keeps the two halves using
        /// literally the same list).
        /// </summary>
        private void DoEditPreparePart2(bool updateAllFields, List<PropertyInfo> pros)
        {
            #region 更新子表
            foreach (var pro in pros)
            {
                //找到类型为List<xxx>的字段
                if (pro.PropertyType.GenericTypeArguments.Count() > 0)
                {
                    //获取xxx的类型
                    var ftype = pro.PropertyType.GenericTypeArguments.First();
                    //如果xxx继承自TopBasePoco
                    if (ftype.IsSubclassOf(typeof(TopBasePoco)))
                    {
                        //界面传过来的子表数据
                        //获取外键字段名称
                        string fkname = DC!.GetFKName<TModel>(pro.Name);
                        string softkey = "";
                        if (string.IsNullOrEmpty(fkname))
                        {
                            if (pro.GetCustomAttribute<NotMappedAttribute>() != null)
                            {
                                fkname = pro.GetCustomAttribute<SoftFKAttribute>()?.PropertyName ?? "";
                                softkey = typeof(TModel).GetCustomAttribute<SoftKeyAttribute>()?.PropertyName ?? "";
                            }
                        }
                        if (pro.GetValue(Entity) is IEnumerable<TopBasePoco> list && list.Count() > 0)
                        {
                            var itemPros = ftype.GetAllProperties();
                            bool found = false;
                            foreach (var newitem in list)
                            {
                                var subtype = newitem.GetType();
                                if (typeof(IBasePoco).IsAssignableFrom(subtype))
                                {
                                    IBasePoco ent = (newitem as IBasePoco)!;
                                    if (ent.UpdateTime == null)
                                    {
                                        ent.UpdateTime = Wtm!.TimeProvider.GetLocalNow().DateTime;
                                    }
                                    if (string.IsNullOrEmpty(ent.UpdateBy))
                                    {
                                        ent.UpdateBy = LoginUserInfo?.ITCode;
                                    }
                                }
                                //循环页面传过来的子表数据,将关联到TopBasePoco的字段设为null,并且把外键字段的值设定为主表ID
                                foreach (var itempro in itemPros)
                                {
                                    if (itempro.PropertyType.IsSubclassOf(typeof(TopBasePoco)))
                                    {
                                        itempro.SetValue(newitem, null);
                                    }
                                    if (!string.IsNullOrEmpty(fkname))
                                    {
                                        if (itempro.Name.ToLower() == fkname.ToLower())
                                        {
                                            try
                                            {
                                                itempro.SetValue(newitem, string.IsNullOrEmpty(softkey) ? Entity.GetID() : Entity.GetPropertyValue(softkey));
                                            }
                                            catch (Exception ex)
                                            {
                                                Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(ex, "Failed to set FK property '{Property}' on sub-entity during DoEdit", itempro.Name);
                                            }
                                            found = true;
                                        }
                                    }
                                }
                            }
                            //如果没有找到相应的外建字段，则可能是多对多的关系，或者做了特殊的设定，这种情况框架无法支持，直接退出本次循环
                            if (found == false)
                            {
                                continue;
                            }

                            var set = EfSetMethodCache.GetClosedSetMethod(DC!.GetType(), ftype);
                            var dataquery = set.Invoke(DC!, null) as IQueryable<TopBasePoco>;
                            ParameterExpression pe = Expression.Parameter(ftype);
                            Expression member = Expression.MakeMemberAccess(pe, ftype.GetSingleProperty(fkname)!);
                            //member = Expression.Call(member, "ToString", new Type[] { });
                            Expression right = Expression.Constant(string.IsNullOrEmpty(softkey) ? Entity.GetID() : Entity.GetPropertyValue(softkey), member.Type);
                            Expression condition = Expression.Equal(member, right);
                            var exp = Expression.Call(
                                  typeof(Queryable),
                                  "Where",
                                  new Type[] { ftype },
                                  dataquery!.Expression,
                                  Expression.Lambda(condition, new ParameterExpression[] { pe }));
                            var q = dataquery.Provider.CreateQuery(exp) as IQueryable<TopBasePoco>;
                            IEnumerable<TopBasePoco> data = [.. q!.AsNoTracking()];
                            //比较子表原数据和新数据的区别
                            IEnumerable<TopBasePoco>? toadd = null;
                            IEnumerable<TopBasePoco>? toremove = null;
                            Utils.CheckDifference(data, list, out toremove, out toadd);
                            //设定子表应该更新的字段
                            List<string> setnames = [];
                            foreach (var field in FC.Keys)
                            {
                                var f = field.ToLower();

                                if (f.StartsWith($"{this.GetParentStr().ToLower()}entity." + pro.Name.ToLower() + "[0]."))
                                {
                                    string name = f.Replace($"{this.GetParentStr().ToLower()}entity." + pro.Name.ToLower() + "[0].", "");
                                    setnames.Add(name);
                                }
                            }

                            //前台传过来的数据
                            foreach (var newitem in list)
                            {
                                //数据库中的数据
                                foreach (var item in data)
                                {
                                    //需要更新的数据
                                    if (newitem.GetID().ToString() == item.GetID().ToString())
                                    {
                                        dynamic i = newitem;
                                        var newitemType = item.GetType();
                                        foreach (var itempro in itemPros)
                                        {
                                            if (!itempro.PropertyType.IsSubclassOf(typeof(TopBasePoco)) && (updateAllFields == true || setnames.Contains(itempro.Name.ToLower())))
                                            {
                                                var notmapped = itempro.GetCustomAttribute<NotMappedAttribute>();
                                                var cannotedit = itempro.GetCustomAttribute<CanNotEditAttribute>();
                                                if (itempro.Name != "ID" && notmapped == null && itempro.PropertyType.IsList() == false && cannotedit == null)
                                                {
                                                    DC!.UpdateProperty(i, itempro.Name);
                                                }
                                            }
                                        }
                                        if (typeof(IBasePoco).IsAssignableFrom(item.GetType()))
                                        {
                                            DC!.UpdateProperty(i, "UpdateTime");
                                            DC!.UpdateProperty(i, "UpdateBy");
                                        }
                                    }
                                }
                            }
                            //需要删除的数据
                            foreach (var item in toremove!)
                            {
                                //如果是PersistPoco，则把IsValid设为false，并不进行物理删除
                                if (typeof(IPersistPoco).IsAssignableFrom(ftype))
                                {
                                    (item as IPersistPoco)!.IsValid = false;
                                    if (typeof(IBasePoco).IsAssignableFrom(ftype))
                                    {
                                        (item as IBasePoco)!.UpdateTime = Wtm!.TimeProvider.GetLocalNow().DateTime;
                                        (item as IBasePoco)!.UpdateBy = LoginUserInfo?.ITCode;
                                    }
                                    dynamic i = item;
                                    DC!.UpdateEntity(i);
                                }
                                else
                                {
                                    foreach (var itempro in itemPros)
                                    {
                                        if (itempro.PropertyType.IsSubclassOf(typeof(TopBasePoco)))
                                        {
                                            itempro.SetValue(item, null);
                                        }
                                    }
                                    dynamic i = item;
                                    DC!.DeleteEntity(i);
                                }
                            }
                            //需要添加的数据
                            foreach (var item in toadd!)
                            {
                                if (typeof(IBasePoco).IsAssignableFrom(item.GetType()))
                                {
                                    IBasePoco ent = (item as IBasePoco)!;
                                    if (ent.CreateTime == null)
                                    {
                                        ent.CreateTime = Wtm!.TimeProvider.GetLocalNow().DateTime;
                                    }
                                    if (string.IsNullOrEmpty(ent.CreateBy))
                                    {
                                        ent.CreateBy = LoginUserInfo?.ITCode;
                                    }
                                }
                                if (typeof(ITenant).IsAssignableFrom(item.GetType()))
                                {
                                    ITenant ent = (item as ITenant)!;
                                    ent.TenantCode = LoginUserInfo?.CurrentTenant;
                                }
                                DC!.AddEntity(item);
                            }
                        }
                        else if ((pro.GetValue(Entity) is IEnumerable<TopBasePoco> list2 && list2?.Count() == 0))
                        {
                            if (string.IsNullOrEmpty(fkname))
                            {
                                continue;
                            }
                            var itemPros = ftype.GetAllProperties();
                            // #815 eighth round: fkname (resolved above via DC.GetFKName) can name an
                            // EF shadow property that has no backing CLR property on ftype — the same
                            // shape LoadExistingSubItemFileIds already fails closed on. Round 8's own
                            // parent-scoped drop in ApplyFileAttachmentResolution can turn a
                            // previously non-empty posted collection into an empty one for exactly
                            // this shape, routing here for the first time. Expression.MakeMemberAccess
                            // below throws ArgumentNullException when handed a null MemberInfo, so
                            // guard it: there is no CLR property to build a parent-scoped delete query
                            // against, so skip the cascade-delete instead of crashing. This leaves any
                            // existing children of this parent untouched rather than guessing at a
                            // query this method cannot safely construct.
                            var fkProperty = ftype.GetSingleProperty(fkname);
                            if (fkProperty == null)
                            {
                                Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(
                                    "DoEditPreparePart2: could not resolve FK property '{FkName}' on {ItemType} (EF shadow property) while clearing sub-table {Property}; skipping cascade-delete for this parent to avoid an unhandled exception (Issue #815)",
                                    fkname, ftype.Name, pro.Name);
                                continue;
                            }
                            var set = EfSetMethodCache.GetClosedSetMethod(DC!.GetType(), ftype);
                            var dataquery = set.Invoke(DC!, null) as IQueryable<TopBasePoco>;
                            ParameterExpression pe = Expression.Parameter(ftype);
                            Expression member = Expression.MakeMemberAccess(pe, fkProperty);
                            //member = Expression.Call(member, "ToString", new Type[] { });
                            Expression right = Expression.Constant(string.IsNullOrEmpty(softkey) ? Entity.GetID() : Entity.GetPropertyValue(softkey), member.Type);
                            Expression condition = Expression.Equal(member, right);
                            var exp = Expression.Call(
                                  typeof(Queryable),
                                  "Where",
                                  new Type[] { ftype },
                                  dataquery!.Expression,
                                  Expression.Lambda(condition, new ParameterExpression[] { pe }));
                            var q = dataquery.Provider.CreateQuery(exp) as IQueryable<TopBasePoco>;
                            IEnumerable<TopBasePoco> removeData = [.. q!.AsNoTracking()];

                            foreach (var item in removeData)
                            {
                                //如果是PersistPoco，则把IsValid设为false，并不进行物理删除
                                if (typeof(IPersistPoco).IsAssignableFrom(ftype))
                                {
                                    (item as IPersistPoco)!.IsValid = false;
                                    if (typeof(IBasePoco).IsAssignableFrom(ftype))
                                    {
                                        (item as IBasePoco)!.UpdateTime = Wtm!.TimeProvider.GetLocalNow().DateTime;
                                        (item as IBasePoco)!.UpdateBy = LoginUserInfo?.ITCode;
                                    }
                                    dynamic i = item;
                                    DC!.UpdateEntity(i);
                                }
                                else
                                {
                                    foreach (var itempro in itemPros)
                                    {
                                        if (itempro.PropertyType.IsSubclassOf(typeof(TopBasePoco)))
                                        {
                                            itempro.SetValue(item, null);
                                        }
                                    }
                                    dynamic i = item;
                                    DC!.DeleteEntity(i);
                                }
                            }
                        }
                    }
                }
            }
            #endregion


            if (updateAllFields == false)
            {
                if (typeof(TreePoco).IsAssignableFrom(typeof(TModel)))
                {
                    var cid = Entity.GetID();
                    var pid = Entity.GetParentID();
                    if (cid != null && pid != null && cid.ToString() == pid.ToString())
                    {
                        var pkey = FC.Keys.Where(x => x.ToLower() == "entity.parentid").FirstOrDefault();
                        if (string.IsNullOrEmpty(pkey) == false)
                        {
                            FC.Remove(pkey);
                        }
                    }
                }
                foreach (var field in FC.Keys)
                {
                    var f = field.ToLower();
                    if (f.StartsWith($"{this.GetParentStr().ToLower()}entity.") && !f.Contains("["))
                    {
                        string name = f.Replace($"{this.GetParentStr().ToLower()}entity.", "");
                        try
                        {
                            var itempro = pros.Where(x => x.Name.ToLower() == name).FirstOrDefault();
                            var notmapped = itempro?.GetCustomAttribute<NotMappedAttribute>();
                            var cannotedit = itempro?.GetCustomAttribute<CanNotEditAttribute>();
                            if (itempro != null && itempro.Name != "ID" && notmapped == null && itempro.PropertyType.IsList() == false && cannotedit == null)
                            {
                                DC!.UpdateProperty(Entity, itempro.Name);
                            }
                        }
                        catch (Exception ex)
                        {
                            Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(ex, "DoEdit: UpdateProperty failed for FC field '{Field}'", name);
                        }
                    }
                }
                if (typeof(IBasePoco).IsAssignableFrom(typeof(TModel)))
                {
                    try
                    {
                        DC!.UpdateProperty(Entity, "UpdateTime");
                        DC!.UpdateProperty(Entity, "UpdateBy");
                    }
                    catch (Exception ex)
                    {
                        Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(ex, "DoEdit: UpdateProperty failed for UpdateTime/UpdateBy on '{EntityType}'", typeof(TModel).Name);
                    }
                }
            }
            else
            {
                if (typeof(TreePoco).IsAssignableFrom(typeof(TModel)))
                {
                    var cid = Entity.GetID();
                    var pid = Entity.GetParentID();
                    if (cid != null && pid != null && cid.ToString() == pid.ToString())
                    {
                        var parentid = Entity.GetType().GetSingleProperty("ParentId");
                        if (parentid != null)
                        {
                            try
                            {
                                parentid.SetValue(Entity, null);
                            }
                            catch (Exception ex)
                            {
                                Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(ex, "Failed to clear ParentId on entity '{EntityType}'", Entity.GetType().Name);
                            }
                        }
                    }
                }
                DC!.UpdateEntity(Entity);
            }
        }

        /// <summary>
        /// 删除，进行默认的删除操作。子类如有自定义操作应重载本函数
        /// </summary>
        public virtual void DoDelete()
        {
            //如果是PersistPoco，则把IsValid设为false，并不进行物理删除
            if (typeof(IPersistPoco).IsAssignableFrom(typeof(TModel)))
            {
                var _snapshotResult = LoadEntitySnapshot();
                if (!_snapshotResult.Succeeded)
                {
                    // #875 (third site): see DoEdit's matching guard — a snapshot-load failure
                    // must not be treated as "no prior snapshot exists" by the file-reference gate
                    // below.
                    MSD?.AddModelError("", CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.DeleteFailed"] ?? "" : "");
                    return;
                }
                var _auditSnapshot = _snapshotResult.Snapshot;
                FC["Entity.IsValid"] = 0;
                (Entity as IPersistPoco)!.IsValid = false;

                var pros = typeof(TModel).GetAllProperties();
                //如果包含List<PersistPoco>，将子表IsValid也设置为false
                List<PropertyInfo> fas = [.. pros.Where(x => typeof(IEnumerable<IPersistPoco>).IsAssignableFrom(x.PropertyType))];
                foreach (var f in fas)
                {
                    f.SetValue(Entity, f.PropertyType.GetConstructor(Type.EmptyTypes)!.Invoke(null));
                }

                // #815 sixth/seventh round: DoEditPrepare can reject the whole request (a
                // required FileAttachment FK column, per EF Core's own model, with no legitimate
                // prior value) — when it does, DoEditPreparePart2 never ran, so there is nothing
                // staged to save. MSD already carries the model error from the gate.
                if (DoEditPrepare(false, _auditSnapshot))
                {
                    AppendChangeLog("Delete", SerializeScalarProps(_auditSnapshot), null);
                    try
                    {
                        DC!.SaveChanges();
                    }
                    catch (DbUpdateException)
                    {
                        MSD?.AddModelError("", CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.DeleteFailed"] ?? "" : "");
                    }
                }
            }
            //如果是普通的TopBasePoco，则进行物理删除
            else if (typeof(TModel).GetTypeInfo().IsSubclassOf(typeof(TopBasePoco)))
            {
                DoRealDelete();
            }
        }

        public virtual async Task DoDeleteAsync()
        {
            //如果是PersistPoco，则把IsValid设为false，并不进行物理删除
            if (typeof(IPersistPoco).IsAssignableFrom(typeof(TModel)))
            {
                var _snapshotResult = await LoadEntitySnapshotAsync();
                if (!_snapshotResult.Succeeded)
                {
                    // #875 (third site): see the sync DoDelete's matching guard above.
                    MSD?.AddModelError("", CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.DeleteFailed"] ?? "" : "");
                    return;
                }
                var _auditSnapshot = _snapshotResult.Snapshot;
                FC["Entity.IsValid"] = 0;
                (Entity as IPersistPoco)!.IsValid = false;
                var pros = typeof(TModel).GetAllProperties();
                //如果包含List<PersistPoco>，将子表IsValid也设置为false
                List<PropertyInfo> fas = [.. pros.Where(x => typeof(IEnumerable<IPersistPoco>).IsAssignableFrom(x.PropertyType))];
                foreach (var f in fas)
                {
                    f.SetValue(Entity, f.PropertyType.GetConstructor(Type.EmptyTypes)!.Invoke(null));
                }
                fas = [.. pros.Where(x => typeof(TopBasePoco).IsAssignableFrom(x.PropertyType))];
                foreach (var f in fas)
                {
                    f.SetValue(Entity, null);
                }
                // #815 fifth round: DoDeleteAsync is itself an async request-path method — use
                // the awaited batched file-reference gate here too, not the sync one.
                // #815 sixth round: see the sync DoDelete's matching guard above for why the
                // return value is checked before saving.
                if (await DoEditPrepareAsync(false, _auditSnapshot))
                {
                    AppendChangeLog("Delete", SerializeScalarProps(_auditSnapshot), null);
                    try
                    {
                        await DC!.SaveChangesAsync();
                    }
                    catch (DbUpdateException)
                    {
                        MSD?.AddModelError("", CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.DeleteFailed"] ?? "" : "");
                    }
                }
            }
            //如果是普通的TopBasePoco，则进行物理删除
            else if (typeof(TModel).GetTypeInfo().IsSubclassOf(typeof(TopBasePoco)))
            {
                await DoRealDeleteAsync();
            }
        }

        /// <summary>
        /// 物理删除，对于普通的TopBasePoco和Delete操作相同，对于PersistPoco则进行真正的删除。子类如有自定义操作应重载本函数
        /// </summary>
        public virtual void DoRealDelete()
        {
            try
            {
                List<Guid> fileids = [];
                var pros = typeof(TModel).GetAllProperties();

                //如果包含附件，则先删除附件
                List<PropertyInfo> fa = [.. pros.Where(x => x.PropertyType == typeof(FileAttachment) || typeof(TopBasePoco).IsAssignableFrom(x.PropertyType))];
                foreach (var f in fa)
                {
                    if (f.GetValue(Entity) is FileAttachment file)
                    {
                        fileids.Add(file.ID);
                    }
                    f.SetValue(Entity, null);
                }

                List<PropertyInfo> fas = [.. pros.Where(x => typeof(IEnumerable<ISubFile>).IsAssignableFrom(x.PropertyType))];
                foreach (var f in fas)
                {
                    var subs = f.GetValue(Entity) as IEnumerable<ISubFile>;
                    if (subs == null)
                    {
                        var fullEntity = DC!.Set<TModel>().AsQueryable().Include(f.Name).AsNoTracking().CheckID(Entity.ID).FirstOrDefault();
                        subs = fullEntity != null ? f.GetValue(fullEntity) as IEnumerable<ISubFile> : null;
                    }
                    if (subs != null)
                    {
                        foreach (var sub in subs)
                        {
                            fileids.Add(sub.FileId);
                        }
                        f.SetValue(Entity, null);
                    }
                }
                if (typeof(TModel) != typeof(FileAttachment))
                {
                    foreach (var pro in pros)
                    {
                        if (pro.PropertyType.GetTypeInfo().IsSubclassOf(typeof(TopBasePoco)))
                        {
                            pro.SetValue(Entity, null);
                        }
                    }
                }
                AppendChangeLog("Delete", SerializeScalarProps(Entity), null);
                DC!.DeleteEntity(Entity);
                DC!.SaveChanges();
                if (Wtm?.ServiceProvider != null)
                {
                    var fp = Wtm.ServiceProvider.GetRequiredService<WtmFileProvider>();
                    foreach (var item in fileids)
                    {
                        // #815 rework: fileids above is read off Entity's own FileAttachment /
                        // ISubFile properties, which RejectUnresolvableFileAttachmentReferences
                        // now keeps free of foreign-tenant references on write — but this call
                        // site is defence in depth, same as every other DeleteFile sink: an id
                        // must still resolve for the caller's own tenant scope, unconditionally,
                        // regardless of FileUploadOptions.EnforceTenantFileScope. See
                        // WtmFileProvider.DeleteFileTenantScoped's doc comment.
                        fp.DeleteFileTenantScoped(item.ToString(), DC!.ReCreate());
                    }
                }
            }
            catch (Exception ex)
            {
                CoreProgram.GetLogger("BaseCRUDVM")?.LogError(ex, "DoRealDelete failed for {Entity} id={Id}", typeof(TModel).Name, Entity?.GetID());
                MSD?.AddModelError("", CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.DeleteFailed"] ?? "" : "");
            }
        }


        public virtual async Task DoRealDeleteAsync()
        {
            try
            {
                List<Guid> fileids = [];
                var pros = typeof(TModel).GetAllProperties();

                //如果包含附件，则先删除附件
                List<PropertyInfo> fa = [.. pros.Where(x => x.PropertyType == typeof(FileAttachment) || typeof(TopBasePoco).IsAssignableFrom(x.PropertyType))];
                foreach (var f in fa)
                {
                    if (f.GetValue(Entity) is FileAttachment file)
                    {
                        fileids.Add(file.ID);
                    }
                    f.SetValue(Entity, null);
                }

                List<PropertyInfo> fas = [.. pros.Where(x => typeof(IEnumerable<ISubFile>).IsAssignableFrom(x.PropertyType))];
                foreach (var f in fas)
                {
                    var subs = f.GetValue(Entity) as IEnumerable<ISubFile>;
                    if (subs == null)
                    {
                        var fullEntity = await DC!.Set<TModel>().AsQueryable()
                            .Include(f.Name).AsNoTracking().CheckID(Entity.ID).FirstOrDefaultAsync();
                        subs = fullEntity != null ? f.GetValue(fullEntity) as IEnumerable<ISubFile> : null;
                    }
                    if (subs != null)
                    {
                        foreach (var sub in subs)
                        {
                            fileids.Add(sub.FileId);
                        }
                        f.SetValue(Entity, null);
                    }
                }
                if (typeof(TModel) != typeof(FileAttachment))
                {
                    foreach (var pro in pros)
                    {
                        if (pro.PropertyType.GetTypeInfo().IsSubclassOf(typeof(TopBasePoco)))
                        {
                            pro.SetValue(Entity, null);
                        }
                    }
                }
                AppendChangeLog("Delete", SerializeScalarProps(Entity), null);
                DC!.DeleteEntity(Entity);
                await DC!.SaveChangesAsync();
                if (Wtm?.ServiceProvider != null)
                {
                    var fp = Wtm.ServiceProvider.GetRequiredService<WtmFileProvider>();
                    foreach (var item in fileids)
                    {
                        // #815 rework: fileids above is read off Entity's own FileAttachment /
                        // ISubFile properties, which RejectUnresolvableFileAttachmentReferences
                        // now keeps free of foreign-tenant references on write — but this call
                        // site is defence in depth, same as every other DeleteFile sink: an id
                        // must still resolve for the caller's own tenant scope, unconditionally,
                        // regardless of FileUploadOptions.EnforceTenantFileScope. See
                        // WtmFileProvider.DeleteFileTenantScoped's doc comment.
                        fp.DeleteFileTenantScoped(item.ToString(), DC!.ReCreate());
                    }
                }
            }
            catch (Exception ex)
            {
                CoreProgram.GetLogger("BaseCRUDVM")?.LogError(ex, "DoRealDeleteAsync failed for {Entity} id={Id}", typeof(TModel).Name, Entity?.GetID());
                MSD?.AddModelError("", CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.DeleteFailed"] ?? "" : "");
            }
        }

        #region AuditChanges helpers

        private static readonly System.Text.Json.JsonSerializerOptions _changeLogJsonOptions =
            new System.Text.Json.JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            };

        /// <summary>
        /// 若 TModel 標注了 <see cref="AuditChangesAttribute"/>，將一筆 ChangeLog 加入 DC（由呼叫方的 SaveChanges 一起提交）。
        /// </summary>
        private void AppendChangeLog(string action, string? oldJson, string? newJson)
        {
            if (!typeof(TModel).IsDefined(typeof(AuditChangesAttribute), inherit: true)) return;
            DC!.Set<ChangeLog>().Add(new ChangeLog
            {
                Action     = action,
                EntityType = typeof(TModel).FullName ?? typeof(TModel).Name,
                EntityId   = Entity?.GetID()?.ToString(),
                ChangedBy  = LoginUserInfo?.ITCode,
                ChangedAt  = Wtm!.TimeProvider.GetUtcNow().DateTime,
                OldValues  = oldJson,
                NewValues  = newJson,
            });
        }

        /// <summary>
        /// 從資料庫以 AsNoTracking 讀取 Entity 快照，用於記錄 OldValues。找不到時 <see
        /// cref="EntitySnapshotResult.Snapshot"/> 回傳 null，<see
        /// cref="EntitySnapshotResult.Succeeded"/> 仍為 true。
        /// <para>
        /// <b>Issue #875 (third site) — a query failure is not the same fact as "no such row".</b>
        /// <c>DoEdit</c>/<c>DoEditAsync</c>/<c>DoDelete</c>/<c>DoDeleteAsync</c> pass this method's
        /// result on as <c>preSaveSnapshot</c> into
        /// <see cref="RejectUnresolvableFileAttachmentReferences"/>/its async twin, which treats
        /// <c>preSaveSnapshot == null</c> as "this is Add, there is no prior DB state" — on the
        /// Edit/Delete path that call site is the ONLY caller, it always means a real prior row
        /// exists — and, on that reading,
        /// <see cref="ApplyFileAttachmentResolution"/> skips the <see
        /// cref="LoadExistingSubItemFileIds"/> restore-vs-drop check entirely for every rejected
        /// sub-item (its own <c>preSaveSnapshot != null ? ... : []</c> guard). Before this fix, a
        /// caught exception here (a transient connection drop, a timeout, ...) collapsed into the
        /// exact same <see langword="null"/> a legitimate "id not set" or "row genuinely deleted
        /// concurrently" case already returns, silently making a real Edit request look like a
        /// brand-new Add to that downstream logic — a caller re-posting an EXISTING child with an
        /// unresolvable FileId would then be DROPPED instead of restored, which, when it empties
        /// the whole posted collection, routes into <see cref="DoEditPreparePart2"/>'s
        /// <c>Count()==0</c> branch and physically deletes EVERY existing child row for the
        /// parent — the identical Issue #828/#875 data-loss chain, reached through this THIRD
        /// swallowing site instead of the two already fixed. <see
        /// cref="EntitySnapshotResult.Succeeded"/> is <see langword="false"/> ONLY when the query
        /// itself threw; the four Edit/Delete callers reject the whole request (<c>MSD</c> error,
        /// no <c>SaveChanges</c>) exactly the way Issue #828 already does for a resolution-query
        /// failure, instead of ever passing an ambiguous <see langword="null"/> onward.
        /// </para>
        /// <para>
        /// <see cref="AppendEditChangeLog"/> is the one remaining caller that does NOT reject on
        /// failure — it is used by <c>_FrameworkController.UpdateModelProperty</c>'s narrow
        /// single-property save path, which never calls <see cref="DoEditPrepare"/>/
        /// <see cref="ApplyFileAttachmentResolution"/> and so cannot reach the deletion branch
        /// above; a failed snapshot there only degrades that path's <c>ChangeLog.OldValues</c>
        /// audit field, so it stays best-effort (logged by this method either way).
        /// </para>
        /// </summary>
        private readonly record struct EntitySnapshotResult(bool Succeeded, TModel? Snapshot);

        private EntitySnapshotResult LoadEntitySnapshot()
        {
            var id = Entity?.GetID();
            if (id == null) return new EntitySnapshotResult(true, null);
            try
            {
                return new EntitySnapshotResult(true, DC!.Set<TModel>().AsNoTracking().CheckID(id).FirstOrDefault());
            }
            catch (Exception ex)
            {
                Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(ex,
                    "LoadEntitySnapshot: failed loading the pre-edit snapshot for {Model} id={Id}; snapshot load FAILED, not treated as Add (Issue #875)",
                    typeof(TModel).Name, id);
                return new EntitySnapshotResult(false, null);
            }
        }

        private async Task<EntitySnapshotResult> LoadEntitySnapshotAsync()
        {
            var id = Entity?.GetID();
            if (id == null) return new EntitySnapshotResult(true, null);
            try
            {
                return new EntitySnapshotResult(true, await DC!.Set<TModel>().AsNoTracking().CheckID(id).FirstOrDefaultAsync());
            }
            catch (Exception ex)
            {
                Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(ex,
                    "LoadEntitySnapshotAsync: failed loading the pre-edit snapshot for {Model} id={Id}; snapshot load FAILED, not treated as Add (Issue #875)",
                    typeof(TModel).Name, id);
                return new EntitySnapshotResult(false, null);
            }
        }

        /// <summary>
        /// #815: <see cref="BaseVM.DeletedFileIds"/> is model-bound — it arrives straight from
        /// the posted form. Its most common producer is <c>UploadTagHelper.cs</c> (~247 emits the
        /// hidden input the browser posts) but it is not the ONLY one: the Blazor equivalents
        /// <c>WTUploadFile.razor</c>/<c>WTUploadImage.razor</c> (<c>OnAvatarDelete</c> →
        /// <c>SetDeletedIds</c>, ~161/~199 and ~172/~213) add to the same list, and
        /// <c>BasePage.cs</c> (~109, <c>PostsForm</c>) copies it onto the posted <c>BaseVM</c>
        /// before every submit — see the sub-table paragraph below for why their multi-file branch
        /// does not widen what this method allows. Without validation a caller could name ANY GUID
        /// for <c>WtmFileProvider.DeleteFile</c> to remove. <c>WtmFileProvider</c> resolved by id
        /// with <c>IgnoreQueryFilters()</c> whenever <c>FileUploadOptions.EnforceTenantFileScope</c>
        /// is false (the default), so an unvalidated id could belong to any tenant's
        /// <see cref="FileAttachment"/> row — arbitrary cross-tenant file deletion.
        /// <para>
        /// <see cref="FileAttachment"/> carries no owner column, but there is a narrower invariant
        /// that needs no schema change: a caller should only be able to delete a file THIS record
        /// actually referenced. This filters <see cref="BaseVM.DeletedFileIds"/> down to the ids
        /// that <paramref name="preSaveSnapshot"/> — a fresh, untracked copy of <typeparamref
        /// name="TModel"/> read from the database BEFORE this save touched anything (both callers
        /// capture it ahead of <c>DoAddPrepare</c>/<c>DoEditPrepare</c> and <c>SaveChanges</c>; the
        /// Add path passes <c>null</c>, since a not-yet-existing row has never legitimately
        /// referenced any file) — actually held in its own <see cref="FileAttachment"/>-typed
        /// properties.
        /// </para>
        /// <para>
        /// <b>This is a second, defence-in-depth layer only — it is NOT sufficient on its own.</b>
        /// A caller can forge the entity's own FK to point at a victim file in one request (saved
        /// with no validation), then name that same id in <see cref="BaseVM.DeletedFileIds"/> in a
        /// follow-up request: the pre-save snapshot legitimately (but wrongly) now contains it, so
        /// this filter alone would pass it through (Issue #815 rework — this is why checking
        /// against <c>Entity</c>'s own in-memory posted properties in the SAME request would be
        /// even weaker: an attacker would not even need two requests). The PRIMARY control against
        /// arbitrary/cross-tenant deletion is <c>WtmFileProvider.DeleteFileTenantScoped</c>, called
        /// at every site that iterates this method's result — it refuses to resolve a
        /// <see cref="FileAttachment"/> outside the caller's own tenant, unconditionally,
        /// regardless of <c>FileUploadOptions.EnforceTenantFileScope</c>. The residual gap this
        /// filter does not close — the two-request forge-then-delete bypass WITHIN the same tenant
        /// — is a smaller, accepted risk (see Issue #815's PR discussion): it requires an
        /// authenticated user already inside the victim's tenant.
        /// </para>
        /// <para>
        /// An id that fails this check is silently skipped, not reported as an error —
        /// <c>WtmFileProvider.DeleteFile</c>/<c>DeleteFileTenantScoped</c> already no-op for an id
        /// that does not resolve to a row they are willing to remove, so the caller-visible
        /// behaviour is unchanged: the file named simply is not deleted. The one legitimate flow
        /// this narrows is <c>UploadTagHelper</c>'s "cancel a just-selected replacement before
        /// submitting" affordance, which posts the id of the file JUST uploaded in this same
        /// session (never any persisted entity's FK, before or after this save) rather than the
        /// pre-edit <c>Field.Model</c> value — that id was never a legitimate reference of ANY
        /// entity either, so skipping it only leaves an already-orphaned
        /// <see cref="FileAttachment"/> row uncleaned; it does not block or alter the save itself.
        /// </para>
        /// <para>
        /// Sub-table (list) entities' own <see cref="FileAttachment"/>-typed properties are
        /// intentionally NOT covered by <see cref="GetOwnFileIds"/> (it only inspects TModel's own
        /// scalar <see cref="FileAttachment"/>-typed properties, never a collection). This DOES
        /// matter for the Blazor multi-file producers named above: <c>WTUploadFile.razor</c>'s
        /// <c>IsMultiple</c> branch (bound to a <c>List&lt;ISubFile&gt;</c> property) also calls
        /// <c>SetDeletedIds</c>, so ids from that branch DO reach
        /// <see cref="BaseVM.DeletedFileIds"/> — but because <see cref="GetOwnFileIds"/> never
        /// matches them, they are unconditionally filtered out here, same as any other
        /// non-matching id: no security regression (they were never a legitimate match), but also
        /// no cleanup for that flow — a pre-existing gap, not introduced by this fix. See Issue
        /// #815.
        /// </para>
        /// </summary>
        private IEnumerable<string> FilterLegitimateDeletedFileIds(TModel? preSaveSnapshot)
        {
            if (DeletedFileIds == null || DeletedFileIds.Count == 0)
            {
                yield break;
            }
            var legitimateIds = GetOwnFileIds(preSaveSnapshot);
            if (legitimateIds.Count == 0)
            {
                yield break;
            }
            foreach (var item in DeletedFileIds)
            {
                if (item != null && Guid.TryParse(item, out var parsed) && legitimateIds.Contains(parsed))
                {
                    yield return item;
                }
            }
        }

        /// <summary>
        /// Collects the <see cref="FileAttachment"/> ids <paramref name="snapshot"/> actually
        /// holds via its own <see cref="FileAttachment"/>-typed properties (resolved to their
        /// backing FK scalar column the same way the auto-Include block above does). See
        /// <see cref="FilterLegitimateDeletedFileIds"/> for why this is read from a pre-save
        /// snapshot rather than the live <c>Entity</c>.
        /// <para>
        /// Known narrowing: if <c>DC.GetFKName2</c> resolves the FK to an EF shadow property (no
        /// corresponding CLR property — e.g. a model that declares only <c>public FileAttachment
        /// Photo</c> with no explicit <c>PhotoId</c> scalar), <c>GetSingleProperty</c> below
        /// cannot read its value off a plain (untracked, reflection-only) snapshot instance, so
        /// that property is skipped and its file is excluded from the legitimate-ids set. This
        /// only narrows what <see cref="FilterLegitimateDeletedFileIds"/> allows to be deleted (a
        /// legitimately-orphaned file for that property becomes uncleanable via
        /// <see cref="BaseVM.DeletedFileIds"/>), never widens it — logged below so the narrowing
        /// is observable rather than a silent surprise. See Issue #815.
        /// </para>
        /// </summary>
        private HashSet<Guid> GetOwnFileIds(TModel? snapshot)
        {
            var result = new HashSet<Guid>();
            if (snapshot == null || DC == null)
            {
                return result;
            }
            var pros = typeof(TModel).GetAllProperties();
            foreach (var f in pros.Where(x => x.PropertyType == typeof(FileAttachment)))
            {
                var fkname = DC.GetFKName2<TModel>(f.Name);
                if (string.IsNullOrEmpty(fkname))
                {
                    continue;
                }
                var fidpro = typeof(TModel).GetSingleProperty(fkname);
                if (fidpro == null)
                {
                    Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(
                        "GetOwnFileIds: FK '{FkName}' for {Model}.{Property} is an EF shadow property with no matching CLR property; excluded from DeletedFileIds validation (Issue #815)",
                        fkname, typeof(TModel).Name, f.Name);
                    continue;
                }
                var fid = fidpro.GetValue(snapshot);
                if (fid != null && Guid.TryParse(fid.ToString(), out var parsed) && parsed != Guid.Empty)
                {
                    result.Add(parsed);
                }
            }
            return result;
        }

        /// <summary>
        /// #815 rework — Option A, kill the primitive at its source. Round 1/2 patched
        /// individual sinks that DELETE a <see cref="FileAttachment"/> named in
        /// <see cref="BaseVM.DeletedFileIds"/>; a reviewer bypassed both by never touching
        /// <c>DeletedFileIds</c> at all: POST 1 edits the caller's OWN row and sets a
        /// <see cref="FileAttachment"/>-typed property's backing FK scalar (e.g. <c>PhotoId</c>)
        /// to a file the caller has no relationship to — <see cref="DoEditPrepare"/> only ever
        /// nulled the NAVIGATION property, never the posted FK scalar, so it landed in the DB
        /// unvalidated. POST 2 then reached the victim's file through ANY path that derives file
        /// ids from the entity — <see cref="DoRealDelete"/>/<see cref="DoRealDeleteAsync"/>,
        /// <c>BaseBatchVM.DoBatchDelete(Async)</c>, or simply a plain read: <c>GetById</c>
        /// resolves the navigation via <c>WtmFileProvider.GetFile</c>, which uses
        /// <c>IgnoreQueryFilters()</c> whenever <c>FileUploadOptions.EnforceTenantFileScope</c> is
        /// false (the default) — so a forged FK is a READ primitive, not only a delete one.
        /// <para>
        /// This closes BOTH at the one place they share: the FK scalar is never allowed to point
        /// at a <see cref="FileAttachment"/> the caller cannot resolve for their OWN tenant — the
        /// <c>ITenant</c> global query filter is kept ON here UNCONDITIONALLY, regardless of
        /// <c>EnforceTenantFileScope</c>, mirroring
        /// <see cref="WtmFileProvider.DeleteFileTenantScoped"/>'s resolution rule. A caller who
        /// posts a foreign id through <see cref="DoAddPrepare"/>/<see cref="DoEditPrepare"/> (this
        /// method's only two callers — their scalar loop above and the <see cref="ISubFile"/>
        /// collection loop below, both walked here) simply never gets it written, so POST 2 has
        /// nothing left to reach through <see cref="DoRealDelete"/>/<see cref="DoRealDeleteAsync"/>,
        /// <c>BaseBatchVM.DoBatchDelete(Async)</c>, or a plain read via that SAME two-call-site
        /// gate. <b>This claim is scoped to those two call sites, not "any sink whatsoever"</b> —
        /// #815's own first-round review already proved <c>BasePagedListVM.UpdateEntityList</c>,
        /// <c>BaseBatchVM.DoBatchEdit</c>/<c>Async</c>, <c>BaseImportVM.BatchSaveData</c>'s Excel
        /// mapping, and direct <c>DbSet</c> saves never call <see cref="DoAddPrepare"/>/
        /// <see cref="DoEditPrepare"/> at all, so this gate does not cover them — those write
        /// paths remain open and are tracked as <b>Issue #824</b>, which enforces the invariant at
        /// the <c>EmptyContext.SaveChanges</c> boundary instead of adding yet another per-path
        /// gate call site here.
        /// </para>
        /// <para>
        /// Round 2's sink-level tenant scoping (<see cref="WtmFileProvider.DeleteFileTenantScoped"/>)
        /// stays as defence in depth — this does not replace it, it removes the primitive both
        /// rounds were chasing downstream.
        /// </para>
        /// <para>
        /// An id that fails resolution is silently reverted to <paramref name="preSaveSnapshot"/>'s
        /// value for that same FK (the entity's own legitimate pre-edit reference, possibly
        /// <see langword="null"/>), or to <see langword="null"/> when <paramref
        /// name="preSaveSnapshot"/> is itself <see langword="null"/> (the Add path — a brand-new
        /// row has no prior legitimate value to revert to). This mirrors how an unresolvable id
        /// already no-ops at the <c>WtmFileProvider</c> delete sinks rather than surfacing as a
        /// user-visible error. Ordinary usage is unaffected: a just-uploaded file always resolves
        /// for its uploader, because <c>WtmFileProvider.Upload</c> stamps its
        /// <c>TenantCode</c> from that same caller's <c>LoginUserInfo.CurrentTenant</c>.
        /// </para>
        /// <para>
        /// <b>Known narrowing, same as <see cref="GetOwnFileIds"/></b>: when <c>DC.GetFKName2</c>
        /// resolves to an EF shadow property with no matching CLR property, there is no scalar to
        /// read the posted value from (or write a safe value back to), so that property is
        /// skipped. This is not a gap in practice — a shadow FK has no CLR property for model
        /// binding to target either, so a caller cannot post to it through this path at all.
        /// </para>
        /// <para>
        /// <b>Caveat — single-tenant deployments</b>: the <c>ITenant</c> global filter compares
        /// <c>FileAttachment.TenantCode == DC.TenantCode</c>. Where multi-tenancy is not in use,
        /// every row (including one belonging to a different USER of the same, often
        /// <see langword="null"/>, tenant) shares that same TenantCode, so this check resolves
        /// every file and provides no isolation between two users. This closes the CROSS-TENANT
        /// version of the primitive; the narrower "any authenticated user vs. any other user's
        /// file, same tenant" threat is not addressed by tenant scoping and remains open — see
        /// Issue #815's PR discussion.
        /// </para>
        /// <para>
        /// <b>#815 third round — <see cref="ISubFile"/> collections covered too.</b> The
        /// scalar-only version of this method left an identical, unvalidated write reachable
        /// through any <c>List&lt;T&gt;</c>-typed property whose item type implements
        /// <see cref="ISubFile"/> (e.g. a <c>Product.Attachments</c> collection): posting
        /// <c>Attachments = [ new ProductAttachment { FileId = &lt;victim file GUID&gt; } ]</c>
        /// through <c>DoAddPrepare</c>/<c>DoEditPrepare</c> wrote <c>FileId</c> with no check at
        /// all — worse than the scalar case, since <see cref="GetOwnFileIds"/>/<see
        /// cref="FilterLegitimateDeletedFileIds"/> never covered sub-item collections either
        /// (see that method's doc comment), so this had neither layer. The loop below walks the
        /// same <c>IEnumerable&lt;ISubFile&gt;</c> properties <see cref="DoRealDelete"/>/<see
        /// cref="DoRealDeleteAsync"/> already walk on the delete side, and applies the identical
        /// tenant-scoped resolution check shared with the scalar loop above (originally a
        /// per-id helper, now the batched <see cref="ResolveFileAttachmentIdsForCaller"/> — see
        /// the "batched resolution, sync and async" doc paragraph below).
        /// </para>
        /// <para>
        /// <b>#815 fourth round — drop the item, never write <see cref="Guid.Empty"/> into
        /// <c>ISubFile.FileId</c>.</b> <see cref="ISubFile"/> mandates a non-nullable <c>Guid
        /// FileId</c> plus a <c>FileAttachment?</c> navigation, and
        /// <c>DataContext.OnModelCreating</c> explicitly leaves <see cref="ISubFile"/> types on
        /// EF's default convention (it <c>continue</c>s past them), so a real FK constraint to
        /// <see cref="FileAttachment"/> always exists on any FK-enforcing provider.
        /// <see cref="Guid.Empty"/> matches no row, so clearing to it — the earlier round's
        /// behaviour — did not "controlledly reject" anything: on <c>DoEdit</c> it rolled back
        /// the ENTIRE save (losing the legitimate parent edit too) and on <c>DoAdd</c> it threw
        /// an unhandled <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>. Instead,
        /// an unresolvable sub-item is removed from the very <c>List&lt;T&gt;</c> instance
        /// <paramref name="preSaveSnapshot"/>-independent <c>Entity</c> already holds (mutated in
        /// place via the non-generic <see cref="IList"/> the model-bound collection is backed by
        /// — every in-repo <see cref="ISubFile"/> collection property is a concrete
        /// <c>List&lt;T&gt;</c>), so the later 更新子表 (<c>DoAddPrepare</c>'s cascade-add /
        /// <c>DoEditPrepare</c>'s <c>Utils.CheckDifference</c>+<c>toadd</c> loop) never sees the
        /// forged item at all — it is not added. <b>Correction (#815 seventh round):</b> "any
        /// existing DB row for a different sub-item id is left completely untouched" only holds
        /// when at least one item remains in the posted collection after the drop. When the drop
        /// empties the collection entirely, <c>DoEditPreparePart2</c>'s <c>else if (... .Count()
        /// == 0)</c> branch runs instead of the <c>Utils.CheckDifference</c> path and physically
        /// deletes EVERY existing child row for this parent — the same behaviour an omitted
        /// child collection already gets from base <c>DoEdit</c> semantics with no gate involved
        /// at all, so this is not a NEW data-loss mode the gate introduces, just a premise this
        /// paragraph originally overstated. A collection property that is not a mutable
        /// <see cref="IList"/> (e.g. a fixed-size
        /// array) cannot have a single element removed from it in place; that narrow case fails
        /// closed by clearing the WHOLE property instead of leaving the unresolved reference
        /// behind — over-broad relative to the common <c>List&lt;T&gt;</c> path, but never a
        /// silent invalid-FK write, and never observed in this codebase's own models.
        /// </para>
        /// <para>
        /// <b>#815 fifth round — batched resolution, sync and async.</b> Each candidate id used
        /// to cost its own synchronous <c>.Any()</c> round-trip, issued from inside this method —
        /// an N+1 query pattern when a posted <see cref="ISubFile"/> collection has more than one
        /// item, and a sync-over-async hazard on the <c>DoAddAsync</c>/<c>DoEditAsync</c> request
        /// path (the #128 ThreadPool-starvation lesson —
        /// <c>.claude/rules/dotnet-conventions.md</c>). <see cref="CollectFileAttachmentCandidates"/>
        /// now gathers every distinct candidate id up front (no DB access), a single query
        /// resolves all of them at once (<see cref="ResolveFileAttachmentIdsForCaller"/> /
        /// <see cref="ResolveFileAttachmentIdsForCallerAsync"/>), and
        /// <see cref="ApplyFileAttachmentResolution"/> applies the same rejection rules as before
        /// against the resolved id set. The async overload
        /// (<see cref="RejectUnresolvableFileAttachmentReferencesAsync"/>) is used by
        /// <c>DoAddPrepareAsync</c>/<c>DoEditPrepareAsync</c>, called from
        /// <c>DoAddAsync</c>/<c>DoEditAsync</c>/<c>DoDeleteAsync</c>.
        /// </para>
        /// <para>
        /// <b>#815 sixth round — the scalar branch had the identical <see cref="Guid.Empty"/>
        /// defect the fourth round fixed for <see cref="ISubFile"/> collections, one branch
        /// over.</b> The "revert to <paramref name="preSaveSnapshot"/>'s value, or
        /// <see langword="null"/> on Add" rule above assumes the FK property can actually HOLD
        /// <see langword="null"/>. When the posted scalar navigation's backing FK is a
        /// non-nullable value type (e.g. a plain <c>Guid FileId</c> — reachable whenever
        /// <typeparamref name="TModel"/> is itself an <see cref="ISubFile"/> shape, such as
        /// <c>ProductAttachment</c> used directly as a CRUD VM's model, or any code-generator-
        /// produced sub-table VM), <c>PropertyInfo.SetValue(Entity, null)</c> does not throw and
        /// does not leave the property alone — reflection silently coerces the <see
        /// langword="null"/> to <c>default(T)</c>, i.e. <see cref="Guid.Empty"/>. That is exactly
        /// the unresolvable-FK write this whole method exists to prevent: on <c>DoEdit</c> it
        /// throws inside <c>SaveChanges</c> (only caught there, not prevented), and on
        /// <c>DoAdd</c> — which has no such try/catch — it surfaces as an unhandled
        /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> straight out of
        /// <see cref="DoAdd"/>. <see cref="ApplyFileAttachmentResolution"/> now checks whether
        /// the FK property is a non-nullable value type AND whether <paramref
        /// name="preSaveSnapshot"/> actually carries a non-default prior value for it; only when
        /// both a safe revert target exists does it write <c>safeValue</c> as before. Otherwise —
        /// Add (<paramref name="preSaveSnapshot"/> is <see langword="null"/>), or an Edit whose
        /// snapshot's own FK is already at its type's default — there is nothing safe to write,
        /// so the WHOLE request is rejected via <c>MSD.AddModelError</c> instead: this method now
        /// returns <see langword="true"/> to its two callers
        /// (<see cref="DoAddPrepare"/>/<see cref="DoAddPrepareAsync"/> and
        /// <see cref="DoEditPrepare"/>/<see cref="DoEditPrepareAsync"/>), which skip staging
        /// <see cref="Entity"/> / running <see cref="DoEditPreparePart2"/> and never call
        /// <c>SaveChanges</c> at all — a controlled, caller-visible failure rather than either an
        /// unhandled exception or a silent success with a mangled FK.
        /// </para>
        /// <para>
        /// <b>#815 seventh round — the rejection predicate above was still CLR-type-only.</b>
        /// "the FK property is a non-nullable value type" (previous paragraph) infers whether
        /// writing <see langword="null"/> is safe from the FK property's own CLR type. But
        /// <c>[Required] Guid? PhotoId</c> (or an equivalent fluent <c>IsRequired()</c>) makes a
        /// CLR-<em>nullable</em> FK a NOT NULL column at the database while still being a
        /// CLR-nullable type — that shape's CLR type check said "safe to write null", so it fell
        /// through to <c>SetValue(Entity, null)</c> and threw the same unhandled
        /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> the previous paragraph
        /// describes, out of <c>DoAdd</c>/<c>DoAddAsync</c>. <see
        /// cref="ApplyFileAttachmentResolution"/> now asks EF Core's own model instead —
        /// <c>DC.Model.FindEntityType(typeof(TModel))?.FindProperty(fkProperty.Name)?.IsNullable</c>
        /// — whether the COLUMN accepts NULL, and rejects the whole request under the same rule
        /// as before when it does not, regardless of what the CLR type alone would have allowed.
        /// A property this method cannot find in the EF model at all (e.g. reached through a
        /// NotMapped/soft-key FK) fails closed: treated as required, same as if EF had reported
        /// it non-nullable. The FK's CLR type is still used, unchanged, to decide what "no
        /// reference" looks like for the <em>legitimate-prior-value</em> check (default(fkType)
        /// for a non-nullable value type, <see langword="null"/> for anything else) — only the
        /// "is writing null acceptable at all" question moved from the CLR type to the EF model.
        /// </para>
        /// <para>
        /// <b>#815 sixth round — Edit-path sub-item drop could delete an untouched sibling row.</b>
        /// The fourth round's "drop the forged item out of the posted collection" fix is correct
        /// for a BRAND-NEW forged sub-item, but not when the rejected item's id matches an
        /// EXISTING child row: dropping it removes that id from the list <see
        /// cref="DoEditPreparePart2"/>'s <c>Utils.CheckDifference</c> diffs against the DB state,
        /// so the untouched row is classified <c>toremove</c> and <c>DC.DeleteEntity</c>
        /// physically deletes it — a re-post that forges ONE sub-item's <c>FileId</c> silently
        /// deletes a completely unrelated, legitimate child row, with <c>DoEdit</c> reporting
        /// success. <see cref="ApplyFileAttachmentResolution"/> now looks up, in a single
        /// per-property batched query (<see cref="LoadExistingSubItemFileIds"/>), which of the
        /// rejected items' ids already exist as a DB row for that same sub-table; a match gets
        /// its <see cref="ISubFile.FileId"/> restored in place from that row's actual stored
        /// value (kept in the posted list, so <c>Utils.CheckDifference</c> still sees it as
        /// present and unrelated to removal) instead of being dropped. The drop behaviour is kept
        /// only for items with no existing DB counterpart — a genuinely brand-new forged item,
        /// where dropping cannot lose any pre-existing row.
        /// </para>
        /// <para>
        /// <b>#815 seventh round — the restore above was wrong on TWO axes.</b> (1) The
        /// justification for restoring-instead-of-dropping is that <c>Utils.CheckDifference</c>
        /// will see the restored id and treat it as untouched — but <c>CheckDifference</c> only
        /// ever runs on the EDIT path (<see cref="DoEditPreparePart2"/>, never
        /// <c>DoAddPreparePart2</c>'s cascade-add). On ADD, restoring a rejected item that
        /// happens to share an id with an existing DB row keeps that item in the posted list,
        /// which the cascade-add then inserts — a PK violation, since a row with that id already
        /// exists. <see cref="ApplyFileAttachmentResolution"/> now only calls
        /// <see cref="LoadExistingSubItemFileIds"/> when <paramref name="preSaveSnapshot"/> is
        /// non-<see langword="null"/> (the Edit path); Add always falls through to the drop
        /// path, same as the fourth round's original behaviour. (2) The lookup itself was not
        /// scoped to the parent being edited, so a rejected item whose id belonged to a
        /// DIFFERENT parent's child row was also restored and kept — invisible to
        /// <c>DoEditPreparePart2</c>'s own PARENT-SCOPED <c>CheckDifference</c> query, so it was
        /// classified <c>toadd</c> and PK-violated there too, aborting the whole edit (including
        /// the request's own legitimate scalar change). <see
        /// cref="LoadExistingSubItemFileIds"/>'s own doc comment has the fix and the fail-closed
        /// fallback when parent-scoping isn't resolvable for a given relationship shape.
        /// </para>
        /// <para>
        /// <b>Issue #828 — a resolution QUERY failure is not the same fact as "these ids don't
        /// exist".</b> Every round above assumes <see cref="ResolveFileAttachmentIdsForCaller"/>
        /// reliably tells apart "the caller may reference these ids" from "the caller may NOT
        /// reference these ids". Before this fix it did not: a thrown exception from the batched
        /// resolution query (SQL Server's hard 2100-parameter-per-query cap — real again under
        /// EF Core 10's default <c>Contains()</c> translation, which reverted from EF8/9's single
        /// JSON/<c>OPENJSON</c> parameter back to one scalar parameter PER candidate id, see
        /// <see cref="ResolveFileAttachmentIdsForCaller"/>'s own doc comment — a query timeout, a
        /// transient connection drop, ...) was caught and treated identically to "the query
        /// succeeded and none of these ids exist": an EMPTY resolved set, fed straight into
        /// <see cref="ApplyFileAttachmentResolution"/>'s per-item narrowing. For a posted <see
        /// cref="ISubFile"/> collection whose every item happens to be a candidate — the ORDINARY
        /// case, since a caller re-posting an entity normally re-posts its own unchanged children
        /// too — every item was dropped, emptying the WHOLE posted collection, which routes
        /// straight into the <c>else if (... .Count() == 0)</c> branch documented on the "fourth
        /// round" paragraph above: <c>DoEditPreparePart2</c> then physically deletes EVERY
        /// existing child row for that parent, and <c>DoEdit</c> reports success. A dependency
        /// failure that has nothing to do with any candidate id's legitimacy must never be able to
        /// reach that branch. <see cref="ResolveFileAttachmentIdsForCaller"/> and its async twin
        /// now return a <c>Succeeded</c> flag alongside the resolved set; when resolution itself
        /// failed, <see cref="RejectUnresolvableFileAttachmentReferences"/> rejects the WHOLE
        /// request the same way the required-FK-with-no-legitimate-prior-value case already does
        /// (sixth/seventh round above) — <c>MSD.AddModelError</c>, <see langword="true"/> to the
        /// caller, nothing staged, <c>SaveChanges</c> never called — instead of ever handing an
        /// ambiguous empty set to <see cref="ApplyFileAttachmentResolution"/>'s per-item rules.
        /// This closes the actual data-loss hole regardless of what turns out to trigger a
        /// resolution failure in practice. Separately, and only to remove the SPECIFIC
        /// 2100-parameter trigger as a routine failure mode for a large but entirely legitimate
        /// form (<c>FormOptions.ValueCountLimit</c> is 5000 — see
        /// <c>FrameworkServiceExtension.cs</c>), the resolution query is now also split into
        /// <see cref="FileAttachmentResolutionBatchSize"/>-sized batches well under the hard cap;
        /// this is defense in depth, not the fix for the data-loss hole itself — a batch failure
        /// still goes through the same whole-request rejection above rather than being narrowed.
        /// </para>
        /// </summary>
        /// <returns>
        /// <see langword="true"/> when a posted FileAttachment reference had to be rejected at
        /// the REQUEST level — either the batched resolution QUERY itself failed (Issue #828: the
        /// caller must not have any candidate narrowed on an ambiguous empty result), or a scalar
        /// FK whose column EF Core's own model says is required had no legitimate prior value to
        /// revert to (Issue #815 sixth/seventh round). Either way the caller must not persist
        /// anything from this request. <see langword="false"/> otherwise (nothing was posted,
        /// everything resolved, or every rejection could be handled by
        /// reverting/dropping/restoring in place).
        /// </returns>
        private bool RejectUnresolvableFileAttachmentReferences(TModel? preSaveSnapshot)
        {
            if (DC == null || typeof(TModel) == typeof(FileAttachment))
            {
                return false;
            }
            var candidates = CollectFileAttachmentCandidates();
            var resolution = ResolveFileAttachmentIdsForCaller(candidates.CandidateIds);
            if (!resolution.Succeeded)
            {
                return RejectWholeRequestForResolutionFailure(candidates.CandidateIds.Count);
            }
            return ApplyFileAttachmentResolution(preSaveSnapshot, candidates.ScalarRefs, candidates.SubFileProperties, resolution.ResolvedIds);
        }

        /// <summary>
        /// Async counterpart of <see cref="RejectUnresolvableFileAttachmentReferences"/> — same
        /// candidate collection and application logic, but the batched resolution query is
        /// awaited instead of run synchronously. See the "batched resolution, sync and async"
        /// doc paragraph above. See <see cref="RejectUnresolvableFileAttachmentReferences"/> for
        /// the meaning of the returned <see cref="bool"/> (Issue #815 sixth round; Issue #828).
        /// </summary>
        private async Task<bool> RejectUnresolvableFileAttachmentReferencesAsync(TModel? preSaveSnapshot)
        {
            if (DC == null || typeof(TModel) == typeof(FileAttachment))
            {
                return false;
            }
            var candidates = CollectFileAttachmentCandidates();
            var resolution = await ResolveFileAttachmentIdsForCallerAsync(candidates.CandidateIds);
            if (!resolution.Succeeded)
            {
                return RejectWholeRequestForResolutionFailure(candidates.CandidateIds.Count);
            }
            return ApplyFileAttachmentResolution(preSaveSnapshot, candidates.ScalarRefs, candidates.SubFileProperties, resolution.ResolvedIds);
        }

        /// <summary>
        /// Issue #828: a resolution QUERY failure (thrown exception — provider parameter cap,
        /// timeout, transient connection loss, ...) is not the same fact as "none of these
        /// candidate ids exist for this caller". Before this method existed,
        /// <see cref="ResolveFileAttachmentIdsForCaller"/> collapsed both into an empty resolved
        /// set, and <see cref="ApplyFileAttachmentResolution"/> applied its per-item narrowing
        /// rules (revert scalar to prior value / drop-or-restore sub-item) to EVERY candidate as
        /// if each one had genuinely failed to resolve — which, for a posted <see cref="ISubFile"/>
        /// collection where every item's id happened to be a candidate (the ordinary case), could
        /// drop the WHOLE posted collection and route into <c>DoEditPreparePart2</c>'s
        /// empty-collection branch, physically deleting every EXISTING child row for the parent.
        /// Rejecting the whole request here — the same "nothing persisted, MSD carries the error"
        /// contract <see cref="ApplyFileAttachmentResolution"/>'s required-FK path already uses —
        /// is a strict superset of what per-item narrowing could safely do anyway: without a
        /// trustworthy resolved set there is no safe per-item decision left to make.
        /// </summary>
        private bool RejectWholeRequestForResolutionFailure(int candidateCount)
        {
            MSD?.AddModelError(" ", Localizer?["Sys.FileResolutionFailed"] ?? "Could not verify one or more file references; please try again.");
            Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(
                "RejectUnresolvableFileAttachmentReferences: batched resolution query failed for {Count} candidate FileAttachment id(s) on {Model}; rejecting the whole request instead of narrowing (Issue #828)",
                candidateCount, typeof(TModel).Name);
            return true;
        }

        /// <summary>
        /// A posted scalar <see cref="FileAttachment"/>-typed property's backing FK scalar,
        /// captured before resolution so <see cref="ApplyFileAttachmentResolution"/> does not
        /// need to repeat the <c>DC.GetFKName2</c>/reflection lookup.
        /// </summary>
        private readonly record struct ScalarFileAttachmentRef(PropertyInfo FkProperty, string FkName, Guid PostedId);

        /// <summary>
        /// Pure, DB-free first pass shared by <see cref="RejectUnresolvableFileAttachmentReferences"/>
        /// and its async counterpart: walks <typeparamref name="TModel"/>'s scalar
        /// <see cref="FileAttachment"/> properties and its <see cref="ISubFile"/> collection
        /// properties, and returns every distinct non-empty posted <see cref="FileAttachment"/>
        /// id found, alongside enough context to apply the rejection afterwards without
        /// re-walking the entity.
        /// </summary>
        private (List<ScalarFileAttachmentRef> ScalarRefs, List<PropertyInfo> SubFileProperties, HashSet<Guid> CandidateIds) CollectFileAttachmentCandidates()
        {
            var scalarRefs = new List<ScalarFileAttachmentRef>();
            var subFileProperties = new List<PropertyInfo>();
            var candidateIds = new HashSet<Guid>();
            var pros = typeof(TModel).GetAllProperties();

            foreach (var f in pros.Where(x => x.PropertyType == typeof(FileAttachment)))
            {
                var fkname = DC!.GetFKName2<TModel>(f.Name);
                if (string.IsNullOrEmpty(fkname))
                {
                    continue;
                }
                var fidpro = typeof(TModel).GetSingleProperty(fkname);
                if (fidpro == null)
                {
                    continue; // EF shadow property — see doc comment above.
                }
                var posted = fidpro.GetValue(Entity);
                if (posted == null || Guid.TryParse(posted.ToString(), out var postedGuid) == false || postedGuid == Guid.Empty)
                {
                    continue; // clearing/leaving the reference empty is always allowed.
                }
                scalarRefs.Add(new ScalarFileAttachmentRef(fidpro, fkname, postedGuid));
                candidateIds.Add(postedGuid);
            }

            foreach (var f in pros.Where(x => typeof(IEnumerable<ISubFile>).IsAssignableFrom(x.PropertyType)))
            {
                if (f.GetValue(Entity) is not IEnumerable<ISubFile> subs)
                {
                    continue;
                }
                var hasCandidate = false;
                foreach (var sub in subs)
                {
                    if (sub.FileId == Guid.Empty)
                    {
                        continue; // clearing/leaving the reference empty is always allowed.
                    }
                    candidateIds.Add(sub.FileId);
                    hasCandidate = true;
                }
                if (hasCandidate)
                {
                    subFileProperties.Add(f);
                }
            }

            return (scalarRefs, subFileProperties, candidateIds);
        }

        /// <summary>
        /// Issue #828: SQL Server's hard per-query parameter cap is 2100 (see
        /// <see href="https://learn.microsoft.com/sql/sql-server/maximum-capacity-specifications-for-sql-server"/>).
        /// EF Core 8/9 sidestepped that entirely by translating a parameterized <c>Contains()</c>
        /// collection into a SINGLE JSON-array parameter unpacked with <c>OPENJSON</c> — but EF
        /// Core 10 reverted the DEFAULT translation back to one scalar SQL parameter PER element
        /// (padded to reduce plan-cache churn) specifically because the OPENJSON form produced
        /// bad query plans for a minority of real workloads (see
        /// <see href="https://learn.microsoft.com/ef/core/what-is-new/ef-core-10.0/breaking-changes#parameterized-collections-now-use-multiple-parameters-by-default"/>).
        /// This repo is pinned to EF Core 10.0.9 (<c>Directory.Packages.props</c>), so a
        /// <see cref="FileAttachment"/> resolution query built from more candidate ids than this
        /// can throw a provider exception purely from its own size, on a request that posted
        /// nothing malicious — the trigger Issue #828 describes really does apply here, though it
        /// would NOT have applied on EF Core 8 or 9. Splitting into batches well under the hard
        /// cap removes that as a routine failure mode for a large but legitimate form; it is
        /// defense in depth ONLY — it does not by itself make an unrelated resolution failure
        /// (timeout, connection drop, a future EF/provider change, ...) safe. That safety comes
        /// from <see cref="RejectUnresolvableFileAttachmentReferences"/> rejecting the whole
        /// request when resolution does not succeed, regardless of why.
        /// </summary>
        private const int FileAttachmentResolutionBatchSize = 500;

        /// <summary>
        /// Issue #828: the result of a batched <see cref="FileAttachment"/> resolution attempt.
        /// <see cref="Succeeded"/> is <see langword="false"/> only when the resolution QUERY
        /// itself threw — never when it ran fine and simply found fewer rows than candidates.
        /// Callers must treat a failed resolution as "unknown", not "unresolvable": see
        /// <see cref="RejectUnresolvableFileAttachmentReferences"/>'s doc comment for why
        /// conflating the two used to let a dependency failure delete data.
        /// </summary>
        private readonly record struct FileAttachmentResolutionResult(bool Succeeded, HashSet<Guid> ResolvedIds);

        /// <summary>
        /// Resolves every id in <paramref name="candidateIds"/> to the set of ids that exist as a
        /// <see cref="FileAttachment"/> row under the CALLER's own tenant scope — the
        /// <c>ITenant</c> global query filter kept ON unconditionally (no
        /// <c>IgnoreQueryFilters()</c>), regardless of
        /// <c>FileUploadOptions.EnforceTenantFileScope</c> — batched into
        /// <see cref="FileAttachmentResolutionBatchSize"/>-sized queries (Issue #828; previously a
        /// single unbounded query — Issue #815 fifth round: before that, one query per candidate
        /// id). On the FIRST batch that throws, resolution stops immediately and reports failure
        /// — see <see cref="FileAttachmentResolutionResult"/>'s doc comment: a partially-resolved
        /// set from the batches that happened to succeed before the failure is deliberately
        /// discarded rather than returned, because <see
        /// cref="RejectUnresolvableFileAttachmentReferences"/> never uses it when
        /// <c>Succeeded</c> is <see langword="false"/> anyway, and keeping it around risks a
        /// future caller mistakenly treating "resolved so far" as "resolved, full stop".
        /// </summary>
        private FileAttachmentResolutionResult ResolveFileAttachmentIdsForCaller(ICollection<Guid> candidateIds)
        {
            if (candidateIds.Count == 0)
            {
                return new FileAttachmentResolutionResult(true, []);
            }
            var resolved = new HashSet<Guid>();
            try
            {
                foreach (var batch in candidateIds.Chunk(FileAttachmentResolutionBatchSize))
                {
                    foreach (var id in DC!.Set<FileAttachment>().Where(x => batch.Contains(x.ID)).Select(x => x.ID))
                    {
                        resolved.Add(id);
                    }
                }
                return new FileAttachmentResolutionResult(true, resolved);
            }
            catch (Exception ex)
            {
                Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(ex,
                    "RejectUnresolvableFileAttachmentReferences: batched resolution query failed for {Count} candidate FileAttachment id(s); resolution FAILED, not narrowed (Issue #828)",
                    candidateIds.Count);
                return new FileAttachmentResolutionResult(false, []);
            }
        }

        /// <summary>
        /// Async counterpart of <see cref="ResolveFileAttachmentIdsForCaller"/> — same batched
        /// queries, awaited instead of run synchronously so
        /// <c>DoAddAsync</c>/<c>DoEditAsync</c>/<c>DoDeleteAsync</c> never block a ThreadPool
        /// thread on it.
        /// </summary>
        private async Task<FileAttachmentResolutionResult> ResolveFileAttachmentIdsForCallerAsync(ICollection<Guid> candidateIds)
        {
            if (candidateIds.Count == 0)
            {
                return new FileAttachmentResolutionResult(true, []);
            }
            var resolved = new HashSet<Guid>();
            try
            {
                foreach (var batch in candidateIds.Chunk(FileAttachmentResolutionBatchSize))
                {
                    foreach (var id in await DC!.Set<FileAttachment>().Where(x => batch.Contains(x.ID)).Select(x => x.ID).ToListAsync())
                    {
                        resolved.Add(id);
                    }
                }
                return new FileAttachmentResolutionResult(true, resolved);
            }
            catch (Exception ex)
            {
                Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(ex,
                    "RejectUnresolvableFileAttachmentReferences: batched resolution query failed for {Count} candidate FileAttachment id(s); resolution FAILED, not narrowed (Issue #828)",
                    candidateIds.Count);
                return new FileAttachmentResolutionResult(false, []);
            }
        }

        /// <summary>
        /// Second, DB-free-EXCEPT-for-<see cref="LoadExistingSubItemFileIds"/> pass shared by
        /// <see cref="RejectUnresolvableFileAttachmentReferences"/> and its async counterpart:
        /// given the resolved set of ids the caller may legitimately reference, reverts/rejects
        /// every scalar candidate that did NOT resolve, and restores/drops every rejected
        /// <see cref="ISubFile"/> item. See the "drop the item, never write Guid.Empty" and
        /// "sixth round" doc paragraphs on <see cref="RejectUnresolvableFileAttachmentReferences"/>.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> when a scalar FK had to be rejected at the request level
        /// (Issue #815 sixth/seventh round — the column is required per EF Core's own model, and
        /// there is no legitimate prior value); <see langword="false"/> otherwise.
        /// </returns>
        private bool ApplyFileAttachmentResolution(TModel? preSaveSnapshot, List<ScalarFileAttachmentRef> scalarRefs, List<PropertyInfo> subFileProperties, HashSet<Guid> resolvedIds)
        {
            var requestRejected = false;

            foreach (var scalar in scalarRefs)
            {
                if (resolvedIds.Contains(scalar.PostedId))
                {
                    continue;
                }

                var fkType = scalar.FkProperty.PropertyType;
                // CLR-nullability of fkType is used ONLY to know what "no reference" looks like
                // for this property's own type — default(fkType) (e.g. Guid.Empty) for a
                // non-nullable value type, vs null for anything else (Nullable<T> or a reference
                // type). It is NOT used to decide whether writing null is acceptable — see
                // isColumnRequired below (#815 seventh round).
                var fkTypeHasNoNullRepresentation = fkType.IsValueType && Nullable.GetUnderlyingType(fkType) == null;

                object? safeValue = null;
                var hasLegitimatePriorValue = false;
                if (preSaveSnapshot != null)
                {
                    safeValue = scalar.FkProperty.GetValue(preSaveSnapshot);
                    hasLegitimatePriorValue = fkTypeHasNoNullRepresentation
                        // A non-nullable value type has no way to represent "no reference" — its
                        // own type default (e.g. Guid.Empty) means the snapshot never had a real
                        // one either, so there is nothing legitimate to revert to.
                        ? !Equals(safeValue, Activator.CreateInstance(fkType))
                        : safeValue != null;
                }

                // #815 seventh round: ask EF Core's own model whether the COLUMN tolerates NULL,
                // instead of inferring it from the FK property's CLR type. `[Required] Guid?
                // PhotoId` (or an equivalent fluent `IsRequired()`) makes a CLR-nullable FK a NOT
                // NULL column at the database — the old `fkType`-only check let that shape fall
                // through to `SetValue(Entity, null)` below, producing an unhandled
                // DbUpdateException out of DoAdd/DoAddAsync instead of the same request-level
                // rejection a non-nullable-value-type FK already gets. This also covers a
                // hypothetical non-Nullable reference-type FK marked required — same hazard, no
                // such shape lives in this repo today.
                var efFkProperty = DC?.Model.FindEntityType(typeof(TModel))?.FindProperty(scalar.FkProperty.Name);
                var isColumnRequired = efFkProperty != null
                    ? !efFkProperty.IsNullable
                    // The property isn't in the EF model at all (e.g. reached through a
                    // NotMapped/soft-key FK — see DC.GetFKName2's NotMappedAttribute branch). EF
                    // gives no authoritative answer about NULL-ability there. Fail closed: treat
                    // it as required rather than risk a silent null write into a column this
                    // method cannot verify.
                    : true;

                if (isColumnRequired && !hasLegitimatePriorValue)
                {
                    // #815 sixth/seventh round: writing safeValue here (null on Add, or the
                    // snapshot's own already-default/null value on Edit) into a column that does
                    // not accept NULL would either reflection-coerce to default(fkType) (a
                    // non-nullable value type) or throw a DbUpdateException out of SaveChanges (a
                    // required CLR-nullable FK) — see the doc comments above. Reject the whole
                    // request instead of writing anything.
                    requestRejected = true;
                    MSD?.AddModelError(GetValidationFieldName(scalar.FkProperty)[0], Localizer?["Sys.FileNotFound"] ?? "File is not found");
                    Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(
                        "RejectUnresolvableFileAttachmentReferences: rejected FK '{FkName}'={PostedId} on {Model} — not resolvable under the caller's own tenant scope and no legitimate prior value to revert to on a required FK column; rejecting the whole request (Issue #815)",
                        scalar.FkName, scalar.PostedId, typeof(TModel).Name);
                    continue;
                }

                scalar.FkProperty.SetValue(Entity, safeValue);
                Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(
                    "RejectUnresolvableFileAttachmentReferences: rejected FK '{FkName}'={PostedId} on {Model} — not resolvable under the caller's own tenant scope; reverted to the prior value (Issue #815)",
                    scalar.FkName, scalar.PostedId, typeof(TModel).Name);
            }

            foreach (var f in subFileProperties)
            {
                if (f.GetValue(Entity) is not IEnumerable<ISubFile> subs)
                {
                    continue;
                }
                List<ISubFile>? rejected = null;
                foreach (var sub in subs)
                {
                    if (sub.FileId != Guid.Empty && !resolvedIds.Contains(sub.FileId))
                    {
                        (rejected ??= []).Add(sub);
                    }
                }
                if (rejected == null || rejected.Count == 0)
                {
                    continue;
                }

                // #815 sixth round: a rejected item whose id matches an EXISTING DB child must be
                // restored in place, not dropped — dropping it makes Utils.CheckDifference (run
                // later by DoEditPreparePart2) treat the untouched row as removed. See the "Edit-
                // path sub-item drop" doc paragraph above.
                // #815 seventh round: this restore is only correct on the EDIT path — Edit is the
                // only caller where Utils.CheckDifference (DoEditPreparePart2) runs at all and can
                // recognize the restored id as "still present, unrelated to removal". On Add
                // (preSaveSnapshot == null) there is no CheckDifference diff — a kept item is
                // cascade-inserted by DoAddPreparePart2's straight Add loop even though a DB row
                // with that same id may already exist, PK-violating. Skip the lookup entirely on
                // Add so every rejected item falls through to the drop path below, same as before
                // this round.
                //
                // #875: a query that THREW while looking up existing sub-item rows is not the
                // same fact as "none of these rejected items exist as an existing child row" —
                // exactly the Issue #828 distinction, one call site over. LoadExistingSubItemFileIds
                // now reports whether its lookup actually ran to completion; when it did not, the
                // old code silently fell through to the drop path below for every rejected item,
                // which — when it empties the whole posted collection — routes into
                // DoEditPreparePart2's Count()==0 branch and physically deletes EVERY existing
                // child row for this parent while DoEdit still reports success. Reject the whole
                // request instead, the same contract Issue #828 already established for the
                // resolution-query failure above: MSD carries the error, nothing gets staged, and
                // the caller never calls SaveChanges.
                Dictionary<string, Guid> existingFileIdsById;
                if (preSaveSnapshot != null)
                {
                    var lookup = LoadExistingSubItemFileIds(f, rejected);
                    if (!lookup.Succeeded)
                    {
                        requestRejected = true;
                        MSD?.AddModelError(" ", Localizer?["Sys.FileResolutionFailed"] ?? "Could not verify one or more file references; please try again.");
                        Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(
                            "RejectUnresolvableFileAttachmentReferences: existing sub-item lookup query failed for {Model}.{Property}; rejecting the whole request instead of dropping rejected items (Issue #875)",
                            typeof(TModel).Name, f.Name);
                        continue;
                    }
                    existingFileIdsById = lookup.ExistingFileIdsById;
                }
                else
                {
                    existingFileIdsById = [];
                }
                List<ISubFile>? toDrop = null;
                foreach (var sub in rejected)
                {
                    var subId = (sub as TopBasePoco)?.GetID()?.ToString();
                    if (subId != null && existingFileIdsById.TryGetValue(subId, out var priorFileId))
                    {
                        sub.FileId = priorFileId;
                        Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(
                            "RejectUnresolvableFileAttachmentReferences: sub-item {SubItemId} on {Model}.{Property} posted an unresolvable FileId — restored its existing prior FileId instead of dropping the item (Issue #815)",
                            subId, typeof(TModel).Name, f.Name);
                        continue;
                    }
                    (toDrop ??= []).Add(sub);
                }
                if (toDrop == null || toDrop.Count == 0)
                {
                    continue;
                }

                if (f.GetValue(Entity) is IList mutableList && !mutableList.IsFixedSize && !mutableList.IsReadOnly)
                {
                    foreach (var sub in toDrop)
                    {
                        mutableList.Remove(sub);
                        Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(
                            "RejectUnresolvableFileAttachmentReferences: dropped sub-item FileId={PostedId} on {Model}.{Property} from the posted collection — not resolvable under the caller's own tenant scope and no existing DB row to restore (Issue #815)",
                            sub.FileId, typeof(TModel).Name, f.Name);
                    }
                }
                else
                {
                    // Not a mutable IList (e.g. a fixed-size array) — cannot drop a single
                    // element in place. Fail closed by clearing the whole property rather than
                    // leaving an unresolved FK reference behind. See the doc comment above.
                    f.SetValue(Entity, null);
                    Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(
                        "RejectUnresolvableFileAttachmentReferences: {Model}.{Property} holds an unresolvable sub-item FileId but its runtime collection type is not a mutable IList; cleared the whole property (Issue #815)",
                        typeof(TModel).Name, f.Name);
                }
            }

            return requestRejected;
        }

        /// <summary>
        /// Issue #875: the result of <see cref="LoadExistingSubItemFileIds"/> — <see
        /// cref="Succeeded"/> is <see langword="false"/> only when the lookup QUERY itself threw,
        /// never when it ran fine and simply found no existing row for a given rejected item. See
        /// <see cref="LoadExistingSubItemFileIds"/>'s doc comment for why conflating the two used
        /// to let a dependency failure delete data, the same shape Issue #828 already fixed for
        /// <see cref="ResolveFileAttachmentIdsForCaller"/>.
        /// </summary>
        private readonly record struct ExistingSubItemLookupResult(bool Succeeded, Dictionary<string, Guid> ExistingFileIdsById);

        /// <summary>
        /// #815 sixth round: given a <see cref="ISubFile"/> collection property and the items
        /// within it that <see cref="ApplyFileAttachmentResolution"/> is about to reject, looks
        /// up which of those items' ids already exist as a row in that same sub-table — an
        /// EXISTING child of some parent that a re-post is trying to overwrite with a forged
        /// <see cref="ISubFile.FileId"/>, as opposed to a brand-new item with no DB counterpart
        /// at all. A single batched query per <paramref name="subFileProperty"/> (never one per
        /// rejected item — the #815 fifth round N+1 lesson applies here too), built the same way
        /// <see cref="DoEditPreparePart2"/> already builds its own sub-table query: resolve the
        /// concrete item CLR type from the property's generic argument, get a closed <c>Set&lt;T&gt;</c>
        /// via <see cref="EfSetMethodCache"/>, and apply <see cref="Extensions.DCExtension.CheckIDs"/>'s
        /// id-membership expression against it — <c>AsNoTracking()</c> so this lookup can never
        /// collide with the change tracker over the SAME rows <c>DoEditPreparePart2</c> loads
        /// moments later.
        /// <para>
        /// This does NOT read through <paramref name="rejectedItems"/>' own DB-loaded snapshot
        /// (i.e. through a hypothetical <c>preSaveSnapshot.SomeCollection</c>) because that
        /// collection is never eager-loaded in the first place — <c>LoadEntitySnapshot</c>'s
        /// plain <c>Set&lt;TModel&gt;().AsNoTracking().CheckID(id).FirstOrDefault()</c> carries no
        /// <c>Include</c>, exactly why <see cref="DoEditPreparePart2"/> and
        /// <see cref="DoRealDelete"/> each run their own separate sub-table query instead of
        /// reading a snapshot's navigation property. A direct query is the only way to see the
        /// row's actual current <see cref="ISubFile.FileId"/>.
        /// </para>
        /// <para>
        /// <b>#815 seventh round — scoped to the parent being edited.</b> An earlier round left
        /// this lookup deliberately unscoped, reasoning that <see cref="Utils.CheckDifference"/>
        /// (run afterwards by <see cref="DoEditPreparePart2"/>) matches purely by
        /// <see cref="TopBasePoco.GetID"/>, so a match here already means "there IS a real DB
        /// row with this id, restore ITS actual FileId". That reasoning missed that
        /// <c>CheckDifference</c> only ever diffs against <c>DoEditPreparePart2</c>'s OWN
        /// sub-table query, which IS parent-scoped (filtered by the same FK this method now
        /// filters by). A rejected item whose id belongs to a DIFFERENT parent's child row was
        /// kept (restored) here but invisible to that parent-scoped diff, so
        /// <c>Utils.CheckDifference</c> classified it <c>toadd</c> — a cascade-insert of a row
        /// whose primary key already exists, PK-violating and rolling back the WHOLE edit
        /// (including the request's own legitimate scalar change). Filtering by the same FK
        /// <see cref="DoEditPreparePart2"/> uses (resolved the identical way:
        /// <see cref="Extensions.DCExtension.GetFKName{T}"/> against <paramref
        /// name="subFileProperty"/>'s name) keeps the match set consistent with what
        /// <c>CheckDifference</c> will actually see, so a same-parent match still restores in
        /// place and a cross-parent id falls through to the caller's drop path instead of
        /// PK-violating.
        /// </para>
        /// <para>
        /// When the FK cannot be resolved this way (empty <c>fkname</c> — e.g. a many-to-many or
        /// soft-keyed relationship <see cref="Extensions.DCExtension.GetFKName{T}"/> cannot
        /// express as a single EF-navigated FK property — or the resolved name has no matching
        /// CLR property on <paramref name="subFileProperty"/>'s item type, e.g. an EF shadow
        /// property), parent-scoping is not possible at all: this method reports success with an
        /// empty result rather than fall back to the old unscoped lookup, so every rejected item
        /// in that shape falls through to the caller's drop path. An aborted edit that PK-violates
        /// and loses a legitimate same-request change is worse than dropping a forged item that
        /// ends up being a false negative for "restore". This is a deterministic fact about
        /// <typeparamref name="TModel"/>'s own shape, true on every call regardless of the
        /// database's health — unlike the QUERY failure below, it is safe to treat as "ran fine,
        /// found nothing to restore against", never as "unknown, reject the whole request".
        /// </para>
        /// <para>
        /// <b>Issue #875 — a lookup QUERY failure is not the same fact as "these rejected items
        /// don't exist as a DB row either".</b> Same defect as Issue #828, one call site over:
        /// before this fix, an exception from the query below (provider parameter cap, a timeout,
        /// a transient connection drop, ...) was caught, logged, and this method fell through to
        /// its final <c>return result</c> with whatever partial results happened to accumulate
        /// before the failure — indistinguishable from "the query ran fine and confirmed none of
        /// these rejected items have an existing DB row". <see cref="ApplyFileAttachmentResolution"/>
        /// treated that identically to a confirmed empty result and dropped every rejected item —
        /// which, when it emptied the whole posted collection, routed into
        /// <see cref="DoEditPreparePart2"/>'s <c>Count()==0</c> branch and physically deleted
        /// EVERY existing child row for the parent, while <c>DoEdit</c> still reported success.
        /// This method now reports <see cref="ExistingSubItemLookupResult.Succeeded"/> alongside
        /// its result, discarding any partially-accumulated entries when the query throws (same
        /// as <see cref="ResolveFileAttachmentIdsForCaller"/> discards a partially-resolved set on
        /// failure — see its doc comment for why). <see cref="ApplyFileAttachmentResolution"/>
        /// rejects the whole request when <c>Succeeded</c> is <see langword="false"/>, the same
        /// contract Issue #828 already established for the earlier resolution-query failure.
        /// </para>
        /// <para>
        /// <b>Issue #875 — batched the same way Issue #828 batched FileAttachment resolution.</b>
        /// <paramref name="rejectedItems"/> can be as large as the whole posted sub-item
        /// collection (every candidate that failed the earlier resolution step), so a single
        /// <c>Contains()</c> built from every id in <paramref name="rejectedItems"/> is subject to
        /// the identical EF Core 10 / SQL Server 2100-parameter-per-query cap
        /// <see cref="FileAttachmentResolutionBatchSize"/>'s doc comment describes for the
        /// resolution query — this is defense in depth only, same caveat as there: it removes the
        /// SPECIFIC parameter-cap trigger as a routine failure mode for a large but legitimate
        /// form, it does not by itself make an unrelated failure (timeout, connection drop, ...)
        /// safe. A failure on any one batch discards the whole call's results and reports failure,
        /// same as <see cref="ResolveFileAttachmentIdsForCaller"/>.
        /// </para>
        /// </summary>
        /// <param name="subFileProperty">The <see cref="ISubFile"/> collection property being resolved.</param>
        /// <param name="rejectedItems">
        /// The items <see cref="ApplyFileAttachmentResolution"/> is about to reject for this
        /// property; can be as large as the whole posted sub-item collection (see the "batched"
        /// doc paragraph above).
        /// </param>
        private ExistingSubItemLookupResult LoadExistingSubItemFileIds(PropertyInfo subFileProperty, List<ISubFile> rejectedItems)
        {
            var result = new Dictionary<string, Guid>();
            if (DC == null || Entity == null || rejectedItems.Count == 0)
            {
                return new ExistingSubItemLookupResult(true, result);
            }

            var itemType = subFileProperty.PropertyType.GenericTypeArguments.FirstOrDefault();
            if (itemType == null || !typeof(TopBasePoco).IsAssignableFrom(itemType))
            {
                // Not a shape Utils.CheckDifference can match by id either — nothing to restore
                // against; the caller falls back to dropping these items, same as before.
                return new ExistingSubItemLookupResult(true, result);
            }

            // #815 seventh round: resolve the same parent FK DoEditPreparePart2's own sub-table
            // query filters by, so this lookup's match set stays consistent with what
            // Utils.CheckDifference will actually see — see the "scoped to the parent being
            // edited" doc paragraph above.
            var fkname = DC.GetFKName<TModel>(subFileProperty.Name);
            var fkProperty = string.IsNullOrEmpty(fkname) ? null : itemType.GetSingleProperty(fkname);
            if (fkProperty == null)
            {
                // Parent-scoping is not possible for this relationship shape — fail closed by
                // restoring nothing (never fall back to the unscoped lookup that could restore a
                // different parent's row); every rejected item falls through to the drop path.
                // Deterministic shape fact, not a query failure — Succeeded stays true.
                Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(
                    "RejectUnresolvableFileAttachmentReferences: could not resolve a parent-scoping FK for {Model}.{Property}; rejected items with no resolved match fall back to being dropped (Issue #815)",
                    typeof(TModel).Name, subFileProperty.Name);
                return new ExistingSubItemLookupResult(true, result);
            }

            var ids = rejectedItems
                .Select(x => (x as TopBasePoco)?.GetID()?.ToString())
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct()
                .ToList();
            if (ids.Count == 0)
            {
                return new ExistingSubItemLookupResult(true, result);
            }

            try
            {
                var setMethod = EfSetMethodCache.GetClosedSetMethod(DC.GetType(), itemType);
                var dataquery = setMethod.Invoke(DC, null) as IQueryable<TopBasePoco>;

                // Issue #875: batched into FileAttachmentResolutionBatchSize-sized queries — see
                // the "batched the same way" doc paragraph above.
                foreach (var idBatch in ids.Chunk(FileAttachmentResolutionBatchSize))
                {
                    ParameterExpression pe = Expression.Parameter(itemType);
                    var idsLambda = idBatch.ToList().GetContainIdExpression(itemType, pe);
                    Expression fkMember = Expression.MakeMemberAccess(pe, fkProperty);
                    Expression fkRight = Expression.Constant(Entity.GetID(), fkMember.Type);
                    Expression fkCondition = Expression.Equal(fkMember, fkRight);
                    Expression body = Expression.AndAlso(idsLambda.Body, fkCondition);
                    var lambda = Expression.Lambda(body, pe);
                    var exp = Expression.Call(
                          typeof(Queryable),
                          "Where",
                          new Type[] { itemType },
                          dataquery!.Expression,
                          lambda);
                    var q = dataquery.Provider.CreateQuery(exp) as IQueryable<TopBasePoco>;
                    foreach (var row in q!.AsNoTracking())
                    {
                        if (row is ISubFile subRow)
                        {
                            result[row.GetID().ToString()!] = subRow.FileId;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // #875: discard whatever partial results the batches that succeeded before this
                // one accumulated — same reasoning as ResolveFileAttachmentIdsForCaller's doc
                // comment: the caller never uses this result when Succeeded is false anyway, and
                // keeping it around risks a future caller mistakenly treating "found so far" as
                // "found, full stop".
                Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseCRUDVM")?.LogWarning(ex,
                    "RejectUnresolvableFileAttachmentReferences: failed looking up existing sub-item rows for {Model}.{Property}; lookup FAILED, not narrowed (Issue #875)",
                    typeof(TModel).Name, subFileProperty.Name);
                return new ExistingSubItemLookupResult(false, []);
            }
            return new ExistingSubItemLookupResult(true, result);
        }

        /// <summary>
        /// 將實體的純量屬性（排除導覽屬性與集合）序列化為 JSON 字串。
        /// </summary>
        private static string? SerializeScalarProps(TModel? entity)
        {
            if (entity == null) return null;
            var dict = new Dictionary<string, object?>();
            foreach (var p in typeof(TModel).GetAllProperties())
            {
                if (p.PropertyType.IsSubclassOf(typeof(TopBasePoco))) continue;
                if (p.PropertyType != typeof(string)
                    && typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType)) continue;
                try { dict[p.Name] = p.GetValue(entity); } catch (Exception) { /* Intentionally ignored: property getter may throw for computed/virtual properties */ }
            }
            try
            {
                return System.Text.Json.JsonSerializer.Serialize(dict, _changeLogJsonOptions);
            }
            catch
            {
                return null;
            }
        }

        #endregion

        /// <summary>
        /// 创建重复数据信息
        /// </summary>
        /// <param name="FieldExps">重复数据信息</param>
        /// <returns>重复数据信息</returns>
        protected DuplicatedInfo<TModel> CreateFieldsInfo(params DuplicatedField<TModel>[] FieldExps)
        {
            DuplicatedInfo<TModel> d = new DuplicatedInfo<TModel>();
            d.AddGroup(FieldExps);
            return d;
        }

        /// <summary>
        /// 创建一个简单重复数据信息
        /// </summary>
        /// <param name="FieldExp">重复数据的字段</param>
        /// <returns>重复数据信息</returns>
        public static DuplicatedField<TModel> SimpleField(Expression<Func<TModel, object>> FieldExp)
        {
            return new DuplicatedField<TModel>(FieldExp);
        }

        /// <summary>
        /// 创建一个关联到其他表数组中数据的重复信息
        /// </summary>
        /// <typeparam name="V">关联表类</typeparam>
        /// <param name="MiddleExp">指向关联表类数组的Lambda</param>
        /// <param name="FieldExps">指向最终字段的Lambda</param>
        /// <returns>重复数据信息</returns>
        public static DuplicatedField<TModel> SubField<V>(Expression<Func<TModel, List<V>>> MiddleExp, params Expression<Func<V, object>>[] FieldExps)
        {
            return new ComplexDuplicatedField<TModel, V>(MiddleExp, FieldExps);
        }

        /// <summary>
        /// 验证数据，默认验证重复数据。子类如需要其他自定义验证，则重载这个函数
        /// </summary>
        /// <returns>验证结果</returns>
        public override void Validate()
        {
            if (ByPassBaseValidation == false)
            {
                base.Validate();
                ////如果msd是BasicMSD，则认为他是手动创建的，也就是说并没有走asp.net core默认的模型验证
                ////那么手动验证模型
                //if (Wtm?.MSD is BasicMSD)
                //{
                //    var valContext = new ValidationContext(this.Entity);
                //    List<ValidationResult> error = new List<ValidationResult>();
                //    if (!Validator.TryValidateObject(Entity, valContext, error, true))
                //    {
                //        foreach (var item in error)
                //        {
                //            string key = item.MemberNames.FirstOrDefault();
                //            if (MSD.Keys.Contains(key) == false)
                //            {
                //                MSD.AddModelError($"Entity.{key}", item.ErrorMessage);
                //            }
                //        }
                //    }
                //    var list = typeof(TModel).GetAllProperties().Where(x => x.PropertyType.IsListOf<TopBasePoco>());
                //    foreach (var item in list)
                //    {
                //        var it = item.GetValue(Entity) as IEnumerable;
                //        if(it == null)
                //        {
                //            continue;
                //        }
                //        var contextset = false;
                //        foreach (var e in it)
                //        {
                //            if(contextset == false)
                //            {
                //                valContext = new ValidationContext(e);
                //                contextset = true;
                //            }

                //            if (!Validator.TryValidateObject(e, valContext, error, true))
                //            {
                //                foreach (var err in error)
                //                {
                //                    string key = err.MemberNames.FirstOrDefault();
                //                    if (MSD.Keys.Contains(key) == false)
                //                    {
                //                        MSD.AddModelError($"Entity.{item.Name}.{key}", err.ErrorMessage);
                //                    }
                //                }
                //            }

                //        }
                //    }
                //}

                //验证重复数据
                ValidateDuplicateData();
            }
        }

        /// <summary>
        /// 验证重复数据
        /// 如果存在重复的数据，则返回已存在数据的id列表
        /// 如果不存在重复数据，则返回一个空列表
        /// </summary>
        protected List<object> ValidateDuplicateData()
        {
            //定义一个对象列表用于存放重复数据的id
            List<object> count = [];
            //获取设定的重复字段信息
            var checkCondition = SetDuplicatedCheck();
            if (checkCondition != null && checkCondition.Groups.Count > 0)
            {
                // WTM-SEC-004: IgnoreQueryFilters is intentional here.
                //
                // We MUST keep IgnoreQueryFilters() so the duplicate check can see
                // soft-deleted rows (IPersistPoco.IsValid == false).  Without it, a key
                // held by a soft-deleted record would look "free" and a new record would
                // be allowed to reuse it — causing a restore conflict.
                //
                // However, IgnoreQueryFilters() also bypasses the ITenant global filter,
                // which means TenantA could silently see TenantB's records and produce a
                // false "duplicate" error.  We fix this by re-applying the tenant predicate
                // explicitly (below) after calling IgnoreQueryFilters(), so soft-delete
                // visibility is preserved while cross-tenant leakage is closed.
                //
                // The explicit tenant WHERE clause is added further down in the loop via
                // the same DuplicatedField<TModel> mechanism used for all other conditions.
                var baseExp = DC!.Set<TModel>().IgnoreQueryFilters().AsQueryable();
                var modelType = typeof(TModel);
                ParameterExpression para = Expression.Parameter(modelType, "tm");
                //循环所有重复字段组
                foreach (var group in checkCondition.Groups)
                {
                    List<object> innercount = [];
                    List<Expression> conditions = [];
                    //生成一个表达式，类似于 x=>x.Id != id，这是为了当修改数据时验证重复性的时候，排除当前正在修改的数据
                    var idproperty = typeof(TModel).GetSingleProperty("ID");
                    MemberExpression idLeft = Expression.Property(para, idproperty!);
                    ConstantExpression idRight = Expression.Constant(Entity.GetID());
                    BinaryExpression idNotEqual = Expression.NotEqual(idLeft, idRight);
                    conditions.Add(idNotEqual);
                    List<PropertyInfo> props = [];
                    //在每个组中循环所有字段
                    foreach (var field in group.Fields)
                    {
                        Expression? exp = field.GetExpression(Entity, para);
                        if (exp != null)
                        {
                            conditions.Add(exp);
                        }
                        //将字段名保存，为后面生成错误信息作准备
                        props.AddRange(field.GetProperties());
                    }
                    if (typeof(ITenant).IsAssignableFrom(typeof(TModel)) && props.Any(x => x.Name.ToLower() == "tenantcode") == false && Wtm?.ConfigInfo?.EnableTenant == true && group.UseTenant == true)
                    {
                        ITenant ent = (Entity as ITenant)!;
                        ent.TenantCode = LoginUserInfo?.CurrentTenant;
                        var f = new DuplicatedField<TModel>(x => (x as ITenant)!.TenantCode!);
                        Expression? exp = f.GetExpression(Entity, para);
                        if (exp != null)
                        {
                            conditions.Add(exp);
                        }
                    }
                    // WTM-SEC-004 (continued): Re-apply tenant isolation unconditionally when
                    // group.UseTenant == false (tenant is not part of the uniqueness key) but
                    // multi-tenancy is enabled.  IgnoreQueryFilters() above stripped the global
                    // ITenant filter to preserve soft-delete visibility; without this block,
                    // TenantA's duplicate check would silently scan TenantB's rows.
                    //
                    // Predicate mirrors the global filter in DataContext.OnModelCreating:
                    //   Expression.Equal(Property(pe, "TenantCode"),
                    //                    PropertyOrField(Constant(this), "TenantCode"))
                    // We replicate it here via the DuplicatedField<TModel> mechanism so it
                    // is combined with the other AND conditions and evaluated by the same
                    // LINQ provider path.
                    //
                    // This block is skipped when:
                    //   - tenancy is disabled (EnableTenant == false), or
                    //   - TModel does not implement ITenant, or
                    //   - the caller already included TenantCode in the uniqueness fields (handled
                    //     by the group.UseTenant == true block above), or
                    //   - group.UseTenant == true (already handled in the block above).
                    if (typeof(ITenant).IsAssignableFrom(typeof(TModel))
                        && props.Any(x => x.Name.ToLower() == "tenantcode") == false
                        && Wtm?.ConfigInfo?.EnableTenant == true
                        && group.UseTenant == false)
                    {
                        // Ensure Entity.TenantCode reflects the current user's tenant.
                        ITenant ent = (Entity as ITenant)!;
                        ent.TenantCode = LoginUserInfo?.CurrentTenant;
                        // Build: x => (x as ITenant).TenantCode == Entity.TenantCode
                        var f = new DuplicatedField<TModel>(x => (x as ITenant)!.TenantCode!);
                        Expression? exp = f.GetExpression(Entity, para);
                        if (exp != null)
                        {
                            conditions.Add(exp);
                        }
                    }
                    if (typeof(IPersistPoco).IsAssignableFrom(typeof(TModel)) && props.Any(x => x.Name.ToLower() == "isvalid") == false)
                    {
                        IPersistPoco ent = (Entity as IPersistPoco)!;
                        ent.IsValid = true;
                        var f = new DuplicatedField<TModel>(x => (x as IPersistPoco)!.IsValid);
                        Expression? exp = f.GetExpression(Entity, para);
                        if (exp != null)
                        {
                            conditions.Add(exp);
                        }
                    }

                    //如果要求判断id不重复，则去掉id不相等的判断，加入id相等的判断
                    //if (props.Any(x => x.Name.ToLower() == "id"))
                    //{
                    //    conditions.RemoveAt(0);
                    //    BinaryExpression idEqual = Expression.Equal(idLeft, idRight);
                    //    conditions.Insert(0, idEqual);
                    //}
                    //int count = 0;
                    if (conditions.Count > 1)
                    {
                        //循环添加条件并生成Where语句
                        //Expression conExp = conditions[0];
                        Expression whereCallExpression = baseExp.Expression;
                        for (int i = 0; i < conditions.Count; i++)
                        {
                            whereCallExpression = Expression.Call(
                                 typeof(Queryable),
                                 "Where",
                                 new Type[] { modelType },
                                 whereCallExpression,
                                 Expression.Lambda<Func<TModel, bool>>(conditions[i], new ParameterExpression[] { para }));
                        }
                        var result = baseExp.Provider.CreateQuery(whereCallExpression);

                        foreach (TopBasePoco res in result)
                        {
                            var id = res.GetID();
                            count.Add(id);
                            innercount.Add(id);
                        }
                    }
                    if (innercount.Count > 0)
                    {
                        //循环拼接所有字段名
                        string AllName = "";
                        foreach (var prop in props)
                        {
                            string name = PropertyHelper.GetPropertyDisplayName(prop);
                            AllName += name + ",";
                        }
                        if (AllName.EndsWith(","))
                        {
                            AllName = AllName.Remove(AllName.Length - 1);
                        }
                        //如果只有一个字段重复，则拼接形成 xxx字段重复 这种提示
                        if (props.Count == 1)
                        {
                            MSD!.AddModelError(GetValidationFieldName(props[0])[0], CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.DuplicateError", AllName] ?? "" : "");
                        }
                        //如果多个字段重复，则拼接形成 xx，yy，zz组合字段重复 这种提示
                        else if (props.Count > 1)
                        {
                            MSD!.AddModelError(GetValidationFieldName(props.First())[0], CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.DuplicateGroupError", AllName] ?? "" : "");
                        }
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// #797/#809: Runs only the duplicate-key check (<see cref="ValidateDuplicateData"/>),
        /// without the rest of the <see cref="Validate"/> override chain.
        ///
        /// <see cref="Validate"/> is user-overridable, and several in-tree generated CRUD VMs
        /// override it to require state that only <c>InitVM()</c> populates — e.g.
        /// <c>FrameworkMenuVM.Validate()</c> adds a "Module required" error whenever
        /// <c>SelectedModule</c> is empty, and <c>SelectedModule</c> is only ever set by
        /// <c>InitVM()</c>. <c>_FrameworkController.UpdateModelProperty</c> builds its VM with
        /// <c>passInit: true</c> (it only needs <see cref="Entity"/> loaded, not the
        /// VM's full init pipeline), so calling the full <see cref="Validate"/> there would
        /// reject requests a bound-VM Edit action would have accepted. MVC-004 only ever
        /// promised the duplicate check — this exposes exactly that piece, respecting
        /// <see cref="ByPassBaseValidation"/> the same way <see cref="Validate"/> does.
        ///
        /// #797 (round 3): <see cref="ValidateDuplicateData"/> is not side-effect-free. To
        /// build its uniqueness query it unconditionally overwrites <c>Entity.TenantCode</c>
        /// (to the caller's current tenant) and/or <c>Entity.IsValid</c> (to <c>true</c>)
        /// whenever <typeparamref name="TModel"/> is <see cref="ITenant"/> / <see
        /// cref="IPersistPoco"/> and the duplicate-check group does not already cover that
        /// field. That mutation is harmless inside the full <c>Validate()</c> →
        /// <c>DoAdd()</c>/<c>DoEdit()</c> pipeline — <c>DoAddPrepare()</c> unconditionally
        /// re-forces both fields for Add, and <c>DoEdit()</c>'s Attach()-based save only
        /// persists properties <c>DoEditPrepare</c> explicitly marks Modified, so the scratch
        /// value never reaches <c>SaveChanges()</c> for Edit either. But this method is also
        /// called directly by <c>_FrameworkController.UpdateModelProperty</c>, which persists
        /// exactly one client-chosen property via a narrow <c>UpdateProperty()</c> call and
        /// logs an audit ChangeLog row from Entity's live state (<see
        /// cref="AppendEditChangeLog"/>) — for that caller, letting the mutation leak into
        /// <see cref="Entity"/> would either silently discard the client's own edit (if
        /// <c>TenantCode</c>/<c>IsValid</c> is the field being edited) or record a ChangeLog
        /// transition that never happened (if some other field is being edited; see #797).
        /// Snapshot both fields before the check and restore them after, so this method is
        /// side-effect-free from the caller's point of view — it only ever returns the
        /// duplicate-id list, exactly as its contract promises.
        /// </summary>
        public List<object> ValidateDuplicateDataOnly()
        {
            if (ByPassBaseValidation)
            {
                return [];
            }

            var tenantEntity = Entity as ITenant;
            var originalTenantCode = tenantEntity?.TenantCode;
            var persistEntity = Entity as IPersistPoco;
            var originalIsValid = persistEntity?.IsValid;

            try
            {
                return ValidateDuplicateData();
            }
            finally
            {
                if (tenantEntity != null)
                {
                    tenantEntity.TenantCode = originalTenantCode;
                }
                if (persistEntity != null)
                {
                    persistEntity.IsValid = originalIsValid!.Value;
                }
            }
        }

        /// <summary>
        /// #797/#809: Appends an "Edit" audit <see cref="ChangeLog"/> row (only when
        /// <typeparamref name="TModel"/> carries <see cref="AuditChangesAttribute"/>) for
        /// callers that persist a change without going through
        /// <see cref="DoEdit"/>/<see cref="DoEditPrepare"/> — namely
        /// <c>_FrameworkController.UpdateModelProperty</c>'s single-property save path, which
        /// deliberately bypasses DoEdit to avoid its collateral-write side effects (#797) but
        /// must not silently drop the RBAC audit trail as a result.
        ///
        /// Must be called after <see cref="Entity"/> has been mutated with the new
        /// value(s) but BEFORE the caller's <c>SaveChanges()</c> commits them, so the DB
        /// snapshot loaded here still reflects the pre-edit values — mirroring
        /// <see cref="DoEdit"/>'s own snapshot-before-SaveChanges ordering. This method only
        /// adds the ChangeLog row to <see cref="BaseVM.DC"/>'s change tracker; the caller's own
        /// SaveChanges() call is what actually persists it (atomically with the property edit).
        /// </summary>
        public void AppendEditChangeLog()
        {
            // #875: this call site intentionally does not reject on a failed snapshot load — see
            // LoadEntitySnapshot's doc comment for why it is safe here specifically (this path
            // never reaches ApplyFileAttachmentResolution's deletion branch). Best-effort, same as
            // before; LoadEntitySnapshot itself already logs the failure.
            var auditSnapshot = LoadEntitySnapshot().Snapshot;
            AppendChangeLog("Edit", SerializeScalarProps(auditSnapshot), SerializeScalarProps(Entity));
        }


        /// <summary>
        /// 根据属性信息获取验证字段名
        /// </summary>
        /// <param name="pi">属性信息</param>
        /// <returns>验证字段名称数组，用于ValidationResult</returns>
        private string[] GetValidationFieldName(PropertyInfo pi)
        {
            return new[] { "Entity." + pi.Name };
        }

    }

    class IncludeInfo
    {
        public MemberInfo mi { get; set; } = null!;
        public Type t { get; set; } = null!;
        public IncludeInfo? Next { get; set; }
        public IncludeInfo? Pre { get; set; }

        private bool? _isnotmapped;
        public bool IsNotMapped
        {
            get
            {
                if (_isnotmapped == null)
                {
                    _isnotmapped = mi.GetCustomAttribute<NotMappedAttribute>() != null;
                }
                return _isnotmapped.Value;
            }
        }

        public bool HasNotMapped
        {
            get
            {
                var cur = (IncludeInfo?)this;
                while (cur != null)
                {
                    if (cur.IsNotMapped == true)
                    {
                        return true;
                    }
                    cur = cur.Next;
                }
                return false;
            }
        }

        private string? _softkey;
        public string SoftKey
        {
            get
            {
                if (_softkey == null)
                {
                    _softkey = InnerType.GetCustomAttribute<SoftKeyAttribute>()?.PropertyName ?? "";

                }
                return _softkey;
            }
        }

        private string? _softfk;
        public string SoftFK
        {
            get
            {
                if (_softfk == null)
                {
                    _softfk = mi.GetCustomAttribute<SoftFKAttribute>()?.PropertyName ?? "";

                }
                return _softfk;
            }
        }

        private Type? _innerType;
        public Type InnerType
        {
            get
            {
                if (_innerType == null)
                {
                    Type stype = this.t;
                    if (stype.IsList())
                    {
                        stype = stype.GetGenericArguments()[0];
                    }
                    _innerType = stype;
                }
                return _innerType;
            }
        }

        public Expression? SoftSelect(ParameterExpression parentpe, IDataContext DC)
        {
            Expression? rv = null;

            if (this.IsNotMapped == false)
            {
                rv = Expression.MakeMemberAccess(parentpe, parentpe.Type.GetSingleProperty(this.mi.Name)!);
            }
            else
            {
                if (this.t.IsList() == false)
                {
                    if (string.IsNullOrEmpty(this.SoftKey))
                    {
                        return rv;
                    }
                    var set = EfSetMethodCache.GetClosedSetMethod(DC.GetType(), InnerType);
                    rv = Expression.Call(Expression.Constant(DC), set);
                    ParameterExpression pe = Expression.Parameter(InnerType);
                    Expression member = Expression.MakeMemberAccess(pe, InnerType.GetSingleProperty(this.SoftKey)!);
                    Expression right = Expression.MakeMemberAccess(parentpe, parentpe.Type.GetSingleProperty(this.mi.Name + "Id")!);
                    Expression condition = Expression.Equal(member, right);
                    rv = Expression.Call(
                         typeof(Queryable),
                         "Where",
                         new Type[] { InnerType },
                         rv,
                         Expression.Lambda(condition, new ParameterExpression[] { pe }));
                }
                else
                {
                    if (string.IsNullOrEmpty(this.SoftFK))
                    {
                        return rv;
                    }
                    var parentkey = parentpe.Type.GetCustomAttribute<SoftKeyAttribute>()?.PropertyName;
                    if (string.IsNullOrEmpty(parentkey))
                    {
                        return rv;
                    }
                    var set = EfSetMethodCache.GetClosedSetMethod(DC.GetType(), InnerType);
                    rv = Expression.Call(Expression.Constant(DC), set);
                    ParameterExpression pe = Expression.Parameter(InnerType);
                    Expression member = Expression.MakeMemberAccess(pe, InnerType.GetSingleProperty(this.SoftFK)!);
                    Expression right = Expression.MakeMemberAccess(parentpe, parentpe.Type.GetSingleProperty(parentkey)!);
                    Expression condition = Expression.Equal(member, right);
                    rv = Expression.Call(
                         typeof(Queryable),
                         "Where",
                         new Type[] { InnerType },
                         rv,
                         Expression.Lambda(condition, new ParameterExpression[] { pe }));
                }
            }
            if (this.Next != null)
            {
                ParameterExpression pe = Expression.Parameter(this.InnerType);
                NewExpression newItem = Expression.New(this.InnerType);
                var pros = this.InnerType.GetAllProperties();
                List<MemberBinding> bindExps = [];
                foreach (var pro in pros)
                {
                    if (pro.GetCustomAttribute<NotMappedAttribute>() == null)
                    {
                        var right = Expression.MakeMemberAccess(pe, pro);
                        MemberBinding bind = Expression.Bind(pro, right);
                        bindExps.Add(bind);
                    }
                    else if (this.Next.mi.Name == pro.Name)
                    {
                        var right = this.Next.SoftSelect(pe, DC);
                        if (right != null)
                        {
                            MemberBinding bind = Expression.Bind(pro, right);
                            bindExps.Add(bind);
                        }
                    }
                }

                MemberInitExpression init = Expression.MemberInit(newItem, bindExps);
                var lambda = Expression.Lambda(init, pe);

                rv = Expression.Call(
               typeof(Queryable),
               "Select",
               new Type[] { InnerType, InnerType },
               rv!,
               lambda);
            }

            if (this.t.IsList() == false)
            {
                rv = Expression.Call(
                   typeof(Enumerable),
                   "FirstOrDefault",
                   new Type[] { InnerType },
                   rv!);
            }
            else
            {
                rv = Expression.Call(
              typeof(Enumerable),
              "ToList",
              new Type[] { InnerType },
              rv!);
            }
            return rv;
        }
    }
}
