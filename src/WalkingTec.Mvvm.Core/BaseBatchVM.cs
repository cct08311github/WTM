using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Models;
using WalkingTec.Mvvm.Core.Support.FileHandlers;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// 批量操作VM接口
    /// </summary>
    /// <typeparam name="TEditModel">批量修改的VM</typeparam>
    public interface IBaseBatchVM<out TEditModel>
        where TEditModel : BaseVM
    {
        /// <summary>
        /// 批量修改的VM
        /// </summary>
        TEditModel LinkedVM { get; }

        /// <summary>
        /// 批量列表VM
        /// </summary>
        IBasePagedListVM<TopBasePoco, ISearcher> ListVM { get; }

        /// <summary>
        /// 列表数据的Id数组
        /// </summary>
        //IEnumerable<Guid> Ids { get; set; }
        string[] Ids { get; set; }

        /// <summary>
        /// 批量操作的错误
        /// </summary>
        Dictionary<string, string> ErrorMessage { get; set; }
    }

    /// <summary>
    /// 批量操作的基础VM，所有批量操作的VM应继承本类
    /// </summary>
    /// <typeparam name="TModel">批量修改的VM</typeparam>
    /// <typeparam name="TLinkModel">批量列表VM</typeparam>
    public class BaseBatchVM<TModel, TLinkModel> : BaseVM, IBaseBatchVM<TLinkModel> where TModel : TopBasePoco,new() where TLinkModel : BaseVM
    {
        /// <summary>
        /// 批量修改的VM
        /// </summary>
        
        public TLinkModel LinkedVM { get; set; }

        /// <summary>
        /// 批量列表VM
        /// </summary>
        [JsonIgnore]
        public IBasePagedListVM<TopBasePoco, ISearcher> ListVM { get; set; }

        /// <summary>
        /// 批量操作的错误
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, string> ErrorMessage { get; set; }

        /// <summary>
        /// 列表数据的Id数组
        /// </summary>
        public string[] Ids { get; set; }

        /// <summary>
        /// 构造函数
        /// </summary>
        public BaseBatchVM()
        {
            //this.Ids = new List<Guid>();
            ErrorMessage = new Dictionary<string, string>();
        }

        /// <summary>
        /// 添加错误信息
        /// </summary>
        /// <param name="e">错误</param>
        /// <param name="id">数据Id</param>
        protected void SetExceptionMessage(Exception e, string id)
        {
            if (id != null)
            {
                ErrorMessage.Add(id, e.Message);
            }
        }

        /// <summary>
        /// 检查是否可以删除，当进行批量删除操作时会调用本函数。子类如果有特殊判断应重载本函数。
        /// 批量删除时，对每一条要删除的数据调用一次，传入 Ids 中与该数据对应的写法；没有完全对应的写法时传入该数据自身 Id 的字符串。Ids 中重复的 Id 只检查一次
        /// </summary>
        /// <param name="id">数据Id</param>
        /// <param name="errorMessage">错误信息</param>
        /// <returns>true代表可以删除，false代表不能删除</returns>
        protected virtual bool CheckIfCanDelete(object id, out string errorMessage)
        {
            errorMessage = null;
            return true;
        }

        /// <summary>
        /// 批量删除，默认对Ids中包含的主键的数据进行删除。子类如果有特殊判断应重载本函数
        /// </summary>
        /// <returns>true代表成功，false代表失败</returns>
        public virtual bool DoBatchDelete()
        {
            bool rv = true;
            //Ids 去重后作为本次要删除的数据Id；没有任何 Id 时不删除任何数据
            var state = new BatchDeleteState(Ids);
            var entityList = state.HasIds ? BuildDeleteQuery(state).ToList() : new List<TModel>();
            List<string> idsData = state.IdsData;
            //循环所有要删除的数据
            for (int i = 0; i < entityList.Count; i++)
            {
                var Entity = entityList[i];
                //按每条数据调用 CheckIfCanDelete（传入 Ids 中与该数据对应的写法；没有完全对应的写法时传入该数据自身 Id 的字符串）
                string entityId = state.GetRequestId(Entity);
                string checkErro = null;
                //检查是否可以删除，如不能删除则直接跳过
                if (CheckIfCanDelete(entityId, out checkErro) == false)
                {
                    ErrorMessage.Add(entityId, checkErro);
                    rv = false;
                    break;
                }
                //进行删除
                try
                {
                    if (PrepareEntityForDelete(state, Entity))
                    {
                        var wi = BuildWorkflowInstanceQuery(Entity).ToList();
                        if (wi.Count > 0)
                        {
                            DC.Set<Elsa_WorkflowInstance>().RemoveRange(wi);
                            var wl = BuildWorkflowLogQuery(wi).ToList();
                            DC.Set<Elsa_WorkflowExecutionLogRecord>().RemoveRange(wl);
                            var ww = BuildFrameworkWorkflowQuery(wi).ToList();
                            DC.Set<FrameworkWorkflow>().RemoveRange(ww);
                        }
                    }
                }
                catch (Exception e)
                {
                    SetExceptionMessage(e, entityId);
                    rv = false;
                }
            }
            //进行数据库的删除操作
            if (rv == true)
            {
                try
                {
                    DC.SaveChanges();
                    DeleteAttachedFiles(state.FileIds);
                }
                catch (Exception e)
                {
                    SetExceptionMessage(e, null);
                    rv = false;
                }
            }
            //如果失败，添加错误信息
            if (rv == false)
            {
                FillRollbackMessages(idsData);
                ListVM?.DoSearch();
                FinishDeleteFailure();
            }
            return rv;
        }

        /// <summary>
        /// 批量删除的异步版本（可选），删除逻辑（逐条检查 <see cref="CheckIfCanDelete"/>、软删除或物理删除、附件、流程数据、错误信息与 <see cref="ListVM"/> 的 BatchError）与 <see cref="DoBatchDelete"/> 一致，
        /// 只是以异步方式访问数据库（查询使用 <c>ToListAsync</c>，保存使用 <c>SaveChangesAsync</c>，失败后刷新列表使用 <c>ListVM.DoSearchAsync</c>）。
        /// 如果子类覆盖了同步的 <see cref="DoBatchDelete"/> 而没有覆盖本方法，会直接调用 <see cref="DoBatchDelete"/> 并返回其结果，让既有的覆盖逻辑不被略过。
        /// 本方法中以下步骤仍然是同步的：<see cref="CheckIfCanDelete"/>、删除附件文件（<c>WtmFileProvider</c> 没有异步 API）。
        /// 如果查询所用的 provider 不支持异步，该次查询改用同步枚举，不会抛例外。
        /// 与同步版本不同的是取消：<paramref name="cancellationToken"/> 已取消时（调用前、查询或保存期间）会抛出 <see cref="OperationCanceledException"/>，
        /// 不会像其他例外一样被转成错误信息，也不会删除附件文件；本方法不会写入已被追踪但尚未保存的变更，但它们仍留在 DC 的追踪中，取消后应丢弃该 DC（或不要再对同一个 DC 调用 SaveChanges）。
        /// 失败后重新查询列表（<c>ListVM.DoSearchAsync</c>）期间也可能发生取消，此时同样会抛出 <see cref="OperationCanceledException"/> 而不是返回 false，且 <see cref="ErrorMessage"/> 可能已被填入（模型错误尚未加入）。
        /// 覆盖同步的 <see cref="DoBatchDelete"/> 时不要在其中调用本方法，否则本方法会再调用该覆盖而形成递归；同步覆盖应调用 base.DoBatchDelete()。
        /// 异步路径使用 <c>SaveChangesAsync</c>，如果自定义的 DataContext 只覆盖了同步的 <c>SaveChanges</c>（例如审计逻辑），需同时覆盖 <c>SaveChangesAsync</c>。
        /// 框架内部仍然调用同步的 <see cref="DoBatchDelete"/>，只有消费端明确调用本方法时才会走异步路径
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>true代表成功，false代表失败</returns>
        public virtual async Task<bool> DoBatchDeleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OverridesDoBatchDelete(GetType()))
            {
                return DoBatchDelete();
            }
            bool rv = true;
            //Ids 去重后作为本次要删除的数据Id；没有任何 Id 时不删除任何数据
            var state = new BatchDeleteState(Ids);
            var entityList = state.HasIds ? await ToListQueryAsync(BuildDeleteQuery(state), cancellationToken) : new List<TModel>();
            List<string> idsData = state.IdsData;
            //循环所有要删除的数据
            for (int i = 0; i < entityList.Count; i++)
            {
                var Entity = entityList[i];
                //按每条数据调用 CheckIfCanDelete（传入 Ids 中与该数据对应的写法；没有完全对应的写法时传入该数据自身 Id 的字符串）
                string entityId = state.GetRequestId(Entity);
                string checkErro = null;
                //检查是否可以删除，如不能删除则直接跳过
                if (CheckIfCanDelete(entityId, out checkErro) == false)
                {
                    ErrorMessage.Add(entityId, checkErro);
                    rv = false;
                    break;
                }
                //进行删除
                try
                {
                    if (PrepareEntityForDelete(state, Entity))
                    {
                        var wi = await ToListQueryAsync(BuildWorkflowInstanceQuery(Entity), cancellationToken);
                        if (wi.Count > 0)
                        {
                            DC.Set<Elsa_WorkflowInstance>().RemoveRange(wi);
                            var wl = await ToListQueryAsync(BuildWorkflowLogQuery(wi), cancellationToken);
                            DC.Set<Elsa_WorkflowExecutionLogRecord>().RemoveRange(wl);
                            var ww = await ToListQueryAsync(BuildFrameworkWorkflowQuery(wi), cancellationToken);
                            DC.Set<FrameworkWorkflow>().RemoveRange(ww);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    //取消不是删除错误，交给调用方处理
                    throw;
                }
                catch (Exception e)
                {
                    SetExceptionMessage(e, entityId);
                    rv = false;
                }
            }
            //进行数据库的删除操作
            if (rv == true)
            {
                try
                {
                    await DC.SaveChangesAsync(cancellationToken);
                    DeleteAttachedFiles(state.FileIds);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    //取消不是删除错误，交给调用方处理
                    throw;
                }
                catch (Exception e)
                {
                    SetExceptionMessage(e, null);
                    rv = false;
                }
            }
            //如果失败，添加错误信息
            if (rv == false)
            {
                FillRollbackMessages(idsData);
                if (ListVM != null)
                {
                    await ListVM.DoSearchAsync(cancellationToken);
                }
                FinishDeleteFailure();
            }
            return rv;
        }

        private static readonly ConcurrentDictionary<Type, bool> _overridesDoBatchDelete = new ConcurrentDictionary<Type, bool>();

        /// <summary>
        /// 判断实际类型是否覆盖了同步的 DoBatchDelete（DoBatchDelete 的声明类型不是 BaseBatchVM 本身），结果按类型缓存
        /// </summary>
        private static bool OverridesDoBatchDelete(Type type)
        {
            return _overridesDoBatchDelete.GetOrAdd(type, static t =>
            {
                var declaring = t.GetMethod(nameof(DoBatchDelete), BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.DeclaringType;
                return declaring == null || !(declaring.IsGenericType && declaring.GetGenericTypeDefinition() == typeof(BaseBatchVM<,>));
            });
        }

        /// <summary>
        /// 批量删除过程中用到的、只由 TModel 与 Ids 决定的信息，以及收集到的附件 Id；同步与异步的批量删除共用
        /// </summary>
        private sealed class BatchDeleteState
        {
            private readonly Dictionary<Guid, string> _guidIds = new Dictionary<Guid, string>();
            private readonly Dictionary<string, string> _textIds = new Dictionary<string, string>(StringComparer.Ordinal);

            /// <param name="ids">请求中的 Ids，为 null 时按没有任何 Id 处理</param>
            public BatchDeleteState(IEnumerable<string> ids)
            {
                IdsData = DistinctIds(ids);
                foreach (var id in IdsData)
                {
                    if (id != null)
                    {
                        _textIds.TryAdd(id, id);
                        if (Guid.TryParse(id, out var guid))
                        {
                            _guidIds.TryAdd(guid, id);
                        }
                    }
                }
                ModelType = typeof(TModel);
                Pros = ModelType.GetAllProperties();
                Fa = Pros.Where(x => x.PropertyType == typeof(FileAttachment) || typeof(TopBasePoco).IsAssignableFrom(x.PropertyType)).ToList();
                IsPersist = typeof(IPersistPoco).IsAssignableFrom(ModelType);
                IsBasePoco = typeof(IBasePoco).IsAssignableFrom(ModelType);
                Fas = Pros.Where(x => typeof(IEnumerable<ISubFile>).IsAssignableFrom(x.PropertyType)).ToList();
            }

            /// <summary>
            /// Ids 去重后的结果：能解析为 Guid 的按 Guid 比较（大小写、花括号等写法不同视为同一个，即使主键是字符串类型，两个只有大小写不同且形如 Guid 的字符串主键也会被视为同一个 Id），否则按 ordinal 字符串比较；保留首次出现的顺序与写法
            /// </summary>
            public List<string> IdsData { get; }
            public bool HasIds => IdsData.Count > 0;

            /// <summary>
            /// 找到 Ids 中与该数据对应的写法（Guid 写法不同视为同一个，否则 ordinal 字符串比较），用于 CheckIfCanDelete 与错误信息；
            /// 没有完全对应的写法时用该数据自身 Id 的字符串。例如字符串主键在请求中的大小写或前后空白不同、int／long 主键写成 "05"、
            /// 无法解析的 Id 被转换成 Guid.Empty 或 0 时，框架转换 Id 查询仍然能找到数据，但 Ids 中没有完全对应的写法
            /// </summary>
            public string GetRequestId(TModel entity)
            {
                var entityId = entity.GetID()?.ToString();
                if (entityId != null)
                {
                    if (Guid.TryParse(entityId, out var guid) && _guidIds.TryGetValue(guid, out var byGuid))
                    {
                        return byGuid;
                    }
                    if (_textIds.TryGetValue(entityId, out var byText))
                    {
                        return byText;
                    }
                }
                return entityId ?? string.Empty;
            }

            private static List<string> DistinctIds(IEnumerable<string> ids)
            {
                var rv = new List<string>();
                if (ids == null)
                {
                    return rv;
                }
                var seenGuids = new HashSet<Guid>();
                var seenTexts = new HashSet<string>(StringComparer.Ordinal);
                var seenNull = false;
                foreach (var id in ids)
                {
                    if (id == null)
                    {
                        if (!seenNull)
                        {
                            seenNull = true;
                            rv.Add(id);
                        }
                    }
                    else if (Guid.TryParse(id, out var guid) ? seenGuids.Add(guid) : seenTexts.Add(id))
                    {
                        rv.Add(id);
                    }
                }
                return rv;
            }

            public Type ModelType { get; }
            public List<PropertyInfo> Pros { get; }
            public List<PropertyInfo> Fa { get; }
            public List<PropertyInfo> Fas { get; }
            public bool IsPersist { get; }
            public bool IsBasePoco { get; }
            //如果包含附件，则先删除附件
            public List<Guid> FileIds { get; } = new List<Guid>();
        }

        /// <summary>
        /// 组装取出要删除的数据的查询（包含子文件列表的 Include，不跟踪，按 Ids 过滤）；同步与异步的批量删除共用
        /// </summary>
        private IQueryable<TModel> BuildDeleteQuery(BatchDeleteState state)
        {
            var query = DC.Set<TModel>().AsQueryable();
            foreach (var f in state.Fas)
            {
                query = query.Include(f.Name);
            }
            return query.AsNoTracking().CheckIDs(state.IdsData);
        }

        /// <summary>
        /// 准备删除一条数据：软删除的数据设置 IsValid／UpdateTime／UpdateBy，否则收集附件 Id、清空导航属性并加入 DC 的删除；
        /// 同步与异步的批量删除共用。返回 true 代表还需要清理该数据对应的流程数据（<c>IWorkflow</c> 且为物理删除）
        /// </summary>
        private bool PrepareEntityForDelete(BatchDeleteState state, TModel Entity)
        {
            var modelType = state.ModelType;
            var pros = state.Pros;
            if (state.IsPersist)
            {
                (Entity as IPersistPoco).IsValid = false;
                DC.UpdateProperty(Entity, "IsValid");
                if (state.IsBasePoco)
                {
                    (Entity as IBasePoco).UpdateTime = DateTime.Now;
                    (Entity as IBasePoco).UpdateBy = LoginUserInfo.ITCode;
                    DC.UpdateProperty(Entity, "UpdateTime");
                    DC.UpdateProperty(Entity, "UpdateBy");
                }
                return false;
            }
            else
            {

                foreach (var f in state.Fa)
                {
                    if (f.PropertyType == typeof(FileAttachment))
                    {
                        string fidfield = DC.GetFKName2(modelType, f.Name);
                        var fidpro = pros.Where(x => x.Name == fidfield).FirstOrDefault();
                        var idresult = fidpro.GetValue(Entity);
                        if (idresult != null)
                        {
                            Guid fid = Guid.Empty;
                            if (Guid.TryParse(idresult.ToString(), out fid) == true)
                            {
                                state.FileIds.Add(fid);
                            }
                        }
                    }
                    f.SetValue(Entity, null);
                }

                foreach (var f in state.Fas)
                {
                    var subs = f.GetValue(Entity) as IEnumerable<ISubFile>;
                    if (subs != null)
                    {
                        foreach (var sub in subs)
                        {
                            state.FileIds.Add(sub.FileId);
                        }
                        f.SetValue(Entity, null);
                    }
                    else
                    {

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
                DC.DeleteEntity(Entity);

                return typeof(IWorkflow).IsAssignableFrom(typeof(TModel));
            }
        }

        /// <summary>
        /// 组装查询该数据对应的流程实例；同步与异步的批量删除共用
        /// </summary>
        private IQueryable<Elsa_WorkflowInstance> BuildWorkflowInstanceQuery(TModel Entity)
        {
            return DC.Set<Elsa_WorkflowInstance>().CheckEqual(typeof(TModel).FullName, x => x.ContextType).CheckEqual(Entity.GetID().ToString(), x => x.ContextId);
        }

        /// <summary>
        /// 组装查询流程实例的执行日志；同步与异步的批量删除共用
        /// </summary>
        private IQueryable<Elsa_WorkflowExecutionLogRecord> BuildWorkflowLogQuery(List<Elsa_WorkflowInstance> instances)
        {
            return DC.Set<Elsa_WorkflowExecutionLogRecord>().CheckContain(instances.Select(x => x.ID).ToList(), x => x.WorkflowInstanceId);
        }

        /// <summary>
        /// 组装查询流程实例对应的 FrameworkWorkflow；同步与异步的批量删除共用
        /// </summary>
        private IQueryable<FrameworkWorkflow> BuildFrameworkWorkflowQuery(List<Elsa_WorkflowInstance> instances)
        {
            return DC.Set<FrameworkWorkflow>().CheckContain(instances.Select(x => x.ID).ToList(), x => x.WorkflowId);
        }

        /// <summary>
        /// 数据库删除成功后删除收集到的附件文件；同步与异步的批量删除共用（<c>WtmFileProvider</c> 没有异步 API，所以仍为同步）
        /// </summary>
        private void DeleteAttachedFiles(List<Guid> fileids)
        {
            var fp = Wtm.ServiceProvider.GetRequiredService<WtmFileProvider>();
            foreach (var item in fileids)
            {
                fp.DeleteFile(item.ToString(), DC.ReCreate());
            }
        }

        /// <summary>
        /// 失败时，已有错误信息的情况下，给其余没有错误信息的数据 Id 补上回滚信息；同步与异步的批量删除、批量修改共用
        /// </summary>
        private void FillRollbackMessages(List<string> idsData)
        {
            if (ErrorMessage.Count > 0)
            {
                foreach (var id in idsData)
                {
                    if (id != null)
                    {
                        //已有的错误信息保留，同一个 Id 不会重复添加
                        ErrorMessage.TryAdd(id, CoreProgram._localizer?["Sys.Rollback"]);
                    }
                }
            }
        }

        /// <summary>
        /// 批量删除失败时，把错误信息写到列表的每一行（BatchError）并添加模型错误；同步与异步的批量删除共用，调用前列表已重新搜索
        /// </summary>
        private void FinishDeleteFailure()
        {
            if (ListVM != null)
            {
                ApplyBatchErrors();
            }
            MSD.AddModelError("", CoreProgram._localizer?["Sys.DataCannotDelete"]);
        }

        /// <summary>
        /// 把 <see cref="ErrorMessage"/> 写到列表每一行的 BatchError；同步与异步共用
        /// </summary>
        private void ApplyBatchErrors()
        {
            foreach (var item in ListVM.GetEntityList())
            {
                item.BatchError = ErrorMessage.Where(x => x.Key == item.GetID().ToString()).Select(x => x.Value).FirstOrDefault();
            }
        }

        //只有查询的 provider 支持异步（EF Core）时才以异步方式执行，否则退回同步，避免 ToListAsync 抛出 InvalidOperationException
        private static async Task<List<T>> ToListQueryAsync<T>(IQueryable<T> query, CancellationToken cancellationToken)
        {
            if (query.Provider is IAsyncQueryProvider)
            {
                return await query.ToListAsync(cancellationToken);
            }
            return query.ToList();
        }

        /// <summary>
        /// 批量修改，默认对Ids中包含的数据进行修改，子类如果有特殊判断应重载本函数
        /// </summary>
        /// <returns>true代表成功，false代表失败</returns>
        public virtual bool DoBatchEdit()
        {
            bool rv = PrepareBatchEdit(out List<string> idsData, default);
            //进行数据库的修改操作
            if (rv == true)
            {
                try
                {
                    DC.SaveChanges();
                }
                catch (Exception e)
                {
                    SetExceptionMessage(e, null);
                    rv = false;
                }
            }

            //如果有错误，输出错误信息
            if (rv == false)
            {
                FillRollbackMessages(idsData);
                RefreshErrorList();
            }
            return rv;
        }

        /// <summary>
        /// 批量修改的异步版本（可选），修改逻辑（逐条给同名属性赋值、使用对应的 BaseCRUDVM 验证、UpdateTime／UpdateBy、错误信息与 <see cref="ListVM"/> 的 BatchError）与 <see cref="DoBatchEdit"/> 一致，
        /// 只是以异步方式访问数据库（保存使用 <c>SaveChangesAsync</c>，失败后刷新列表使用 <see cref="RefreshErrorListAsync"/>）。
        /// 如果子类覆盖了同步的 <see cref="DoBatchEdit"/> 而没有覆盖本方法，会直接调用 <see cref="DoBatchEdit"/> 并返回其结果，让既有的覆盖逻辑不被略过。
        /// 本方法中逐条赋值与验证（<c>vm.Validate()</c>）的步骤仍然是同步的。
        /// 与同步版本不同的是取消：<paramref name="cancellationToken"/> 已取消时（调用前或保存期间）会抛出 <see cref="OperationCanceledException"/>，
        /// 不会像其他保存例外一样被转成错误信息；本方法不会写入已被追踪但尚未保存的变更，但它们仍留在 DC 的追踪中，取消后应丢弃该 DC（或不要再对同一个 DC 调用 SaveChanges）。
        /// 失败后重新查询列表（<see cref="RefreshErrorListAsync"/>）期间也可能发生取消，此时同样会抛出 <see cref="OperationCanceledException"/> 而不是返回 false，且 <see cref="ErrorMessage"/> 可能已被填入。
        /// 覆盖同步的 <see cref="DoBatchEdit"/> 时不要在其中调用本方法，否则本方法会再调用该覆盖而形成递归；同步覆盖应调用 base.DoBatchEdit()。
        /// 异步路径使用 <c>SaveChangesAsync</c>，如果自定义的 DataContext 只覆盖了同步的 <c>SaveChanges</c>（例如审计逻辑），需同时覆盖 <c>SaveChangesAsync</c>。
        /// 框架内部仍然调用同步的 <see cref="DoBatchEdit"/>，只有消费端明确调用本方法时才会走异步路径
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>true代表成功，false代表失败</returns>
        public virtual async Task<bool> DoBatchEditAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OverridesDoBatchEdit(GetType()))
            {
                return DoBatchEdit();
            }
            bool rv = PrepareBatchEdit(out List<string> idsData, cancellationToken);
            //进行数据库的修改操作
            if (rv == true)
            {
                try
                {
                    await DC.SaveChangesAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    //取消不是修改错误，交给调用方处理
                    throw;
                }
                catch (Exception e)
                {
                    SetExceptionMessage(e, null);
                    rv = false;
                }
            }

            //如果有错误，输出错误信息
            if (rv == false)
            {
                FillRollbackMessages(idsData);
                await RefreshErrorListAsync(cancellationToken);
            }
            return rv;
        }

        private static readonly ConcurrentDictionary<Type, bool> _overridesDoBatchEdit = new ConcurrentDictionary<Type, bool>();

        /// <summary>
        /// 判断实际类型是否覆盖了同步的 DoBatchEdit（DoBatchEdit 的声明类型不是 BaseBatchVM 本身），结果按类型缓存
        /// </summary>
        private static bool OverridesDoBatchEdit(Type type)
        {
            return _overridesDoBatchEdit.GetOrAdd(type, static t =>
            {
                var declaring = t.GetMethod(nameof(DoBatchEdit), BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.DeclaringType;
                return declaring == null || !(declaring.IsGenericType && declaring.GetGenericTypeDefinition() == typeof(BaseBatchVM<,>));
            });
        }

        /// <summary>
        /// 批量修改保存前的准备：找到对应的 BaseCRUDVM，逐条给实体的同名属性赋值并验证；有错误时返回 false。同步与异步的批量修改共用
        /// </summary>
        /// <param name="idsData">Ids 中的数据 Id 列表</param>
        /// <param name="cancellationToken">异步版本传入的取消令牌；同步版本传入 default（永远不会取消，行为不变）</param>
        private bool PrepareBatchEdit(out List<string> idsData, CancellationToken cancellationToken)
        {
            //获取批量修改VM的所有属性
            var pros = LinkedVM.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
            bool rv = true;
            idsData = Ids.ToList();
            string currentvmname = this.GetType().Name;
            Type vmtype = null;
            //找到对应的BaseCRUDVM，并初始化
            if (currentvmname.ToLower().Contains("apibatchvm"))
            {
                vmtype = this.GetType().Assembly.GetExportedTypes().Where(x => x.IsSubclassOf(typeof(BaseCRUDVM<TModel>)) && x.Name.ToLower().Contains("apivm") == true).FirstOrDefault();
            }
            else
            {
                vmtype = this.GetType().Assembly.GetExportedTypes().Where(x => x.IsSubclassOf(typeof(BaseCRUDVM<TModel>)) && x.Name.ToLower().Contains("apivm") == false).FirstOrDefault();
            }
            IBaseCRUDVM<TModel> vm = null;
            if (vmtype != null)
            {
                vm = vmtype.GetConstructor(System.Type.EmptyTypes).Invoke(null) as IBaseCRUDVM<TModel>;
                vm.CopyContext(this);
            }
            //循环所有数据
            for (int i = 0; i < idsData.Count; i++)
            {
                try
                {
                    //如果找不到对应数据，则输出错误
                    TModel entity = new TModel();
                    entity.SetID(idsData[i]);
                    //循环LinkedVM中的属性，给entity中同名属性赋值
                    foreach (var pro in pros)
                    {
                        var proToSet = entity.GetType().GetSingleProperty(pro.Name);
                        var val = FC.ContainsKey("LinkedVM." + pro.Name) ? FC["LinkedVM." + pro.Name] : null;
                        if(val == null && FC.ContainsKey("LinkedVM." + pro.Name + "[]"))
                        {
                            val = FC["LinkedVM." + pro.Name + "[]"];
                        }
                        var valuetoset = pro.GetValue(LinkedVM);
                        if (proToSet != null && val != null && valuetoset != null)
                        {
                            var hasvalue = true;
                            if ( val is StringValues sv && StringValues.IsNullOrEmpty(sv) == true)
                            {
                                hasvalue = false;
                            }
                            if (hasvalue)
                            {
                                proToSet.SetValue(entity, valuetoset);
                                DC.UpdateProperty(entity, proToSet.Name);
                            }
                        }
                    }

                    //调用controller方法验证model
                    //try
                    //{
                    //    Controller.GetType().GetMethod("RedoValidation").Invoke(Controller, new object[] { entity });
                    //}
                    //catch { }
                    //如果有对应的BaseCRUDVM则使用其进行数据验证
                    if (vm != null)
                    {
                        vm.Validate();
                        var errors = vm.MSD;
                        if (errors != null && errors.Count > 0)
                        {
                            var error = "";
                            foreach (var key in errors.Keys)
                            {
                                if (errors[key].Count > 0)
                                {
                                    error += errors[key].Select(x => x.ErrorMessage).ToSepratedString();
                                }
                            }
                            if (error != "")
                            {
                                ErrorMessage.Add(idsData[i], error);
                                rv = false;
                                break;
                            }
                        }
                    }
                    if (typeof(IBasePoco).IsAssignableFrom( typeof(TModel)))
                    {
                        IBasePoco ent = entity as IBasePoco;
                        if (ent.UpdateTime == null)
                        {
                            ent.UpdateTime = DateTime.Now;
                            DC.UpdateProperty(entity, nameof(ent.UpdateTime));
                        }
                        if (string.IsNullOrEmpty(ent.UpdateBy))
                        {
                            ent.UpdateBy = LoginUserInfo?.ITCode;
                            DC.UpdateProperty(entity, nameof(ent.UpdateBy));
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    //取消不是修改错误，交给调用方处理
                    throw;
                }
                catch (Exception e)
                {
                    SetExceptionMessage(e, idsData[i]);
                    rv = false;
                }
            }
            return rv;
        }

        protected void RefreshErrorList()
        {
            ListVM.DoSearch();
            ApplyBatchErrors();

        }

        /// <summary>
        /// <see cref="RefreshErrorList"/> 的异步版本：用 <c>ListVM.DoSearchAsync</c> 重新搜索列表，再把 <see cref="ErrorMessage"/> 写到每一行的 BatchError。
        /// 子类覆盖异步的批量操作时可以调用本方法；框架内部的同步批量操作仍然调用 <see cref="RefreshErrorList"/>
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        protected async Task RefreshErrorListAsync(CancellationToken cancellationToken = default)
        {
            await ListVM.DoSearchAsync(cancellationToken);
            ApplyBatchErrors();
        }
    }
}
