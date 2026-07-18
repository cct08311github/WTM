#nullable enable
using System;
using System.Collections.Generic;
using WalkingTec.Mvvm.Core.Analysis;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Data.Common;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using MySql.Data.MySqlClient;
using Npgsql;
using NpgsqlTypes;
using System.Text;
using NPOI.HSSF.Util;
using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.Streaming;
using NPOI.XSSF.UserModel;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core
{
    public delegate object ColumnFormatCallBack<in T>(T entity, object fieldValue) where T : TopBasePoco;

    /// <summary>
    /// ListVM的搜索模式枚举
    /// </summary>
    public enum ListVMSearchModeEnum
    {
        Search, //搜索
        Export, //导出
        Batch, //批量
        Selector,//选择器
        MasterDetail, //
        CheckExport,
        Custom1, Custom2, Custom3, Custom4, Custom5
    };

    /// <summary>
    /// ListVM的基类，所有ListVM应该继承这个类， 基类提供了搜索，导出等列表常用功能
    /// </summary>
    /// <typeparam name="TModel">ListVM中的Model类</typeparam>
    /// <typeparam name="TSearcher">ListVM使用的Searcher类</typeparam>
    public partial class BasePagedListVM<TModel, TSearcher> : BaseVM, IBasePagedListVM<TModel, TSearcher>
        where TModel : TopBasePoco
        where TSearcher : BaseSearcher
    {


        [JsonIgnore]
        public string? TotalText { get; set; } = CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.Total"] : null;

        public virtual DbCommand? GetSearchCommand()
        {
            return null;
        }

        private int? _childrenDepth;

        /// <summary>
        /// 多级表头深度  默认 1级
        /// </summary>
        public int GetChildrenDepth()
        {
            if (_childrenDepth == null)
            {
                _childrenDepth = _getHeaderDepth();
            }
            return _childrenDepth.Value;
        }

        /// <summary>
        /// GridHeaders
        /// </summary>
        [JsonIgnore]
        private IEnumerable<IGridColumn<TModel>>? GridHeaders { get; set; }

        /// <summary>
        /// GetHeaders
        /// </summary>
        /// <returns></returns>
        public IEnumerable<IGridColumn<TModel>> GetHeaders()
        {
            if (GridHeaders == null)
            {
                GridHeaders = InitGridHeader();
            }
            return GridHeaders;
        }

        /// <summary>
        /// 计算多级表头深度
        /// </summary>
        /// <returns></returns>
        private int _getHeaderDepth()
        {
            IEnumerable<IGridColumn<TModel>> headers = GetHeaders();
            return headers.Max(x => x.MaxDepth);
        }

        private List<GridAction>? _gridActions;

        /// <summary>
        /// 页面动作
        /// </summary>
        public List<GridAction> GetGridActions()
        {
            if (_gridActions == null)
            {
                _gridActions = InitGridAction();
            }
            return _gridActions;
        }

        /// <summary>
        /// 初始化 InitGridHeader，继承的类应该重载这个函数来设定数据的列和动作
        /// </summary>
        protected virtual IEnumerable<IGridColumn<TModel>> InitGridHeader()
        {
            return [];
        }

        protected virtual List<GridAction> InitGridAction()
        {
            return [];
        }

        /// <summary>
        /// 回傳此 ListVM 可供分析的欄位 Metadata（掃描 TModel 上的 [Dimension]/[Measure] attribute）。
        /// 子類別可覆寫以自訂欄位清單。
        /// </summary>
        public virtual IEnumerable<AnalysisFieldMeta> GetAnalysisFields()
            => AnalysisFieldScanner.ScanModel(typeof(TModel));

        #region Core
        public SortInfo CreateSortInfo(Expression<Func<TModel, object>> pro, SortDir dir)
        {
            SortInfo rv = new SortInfo
            {
                Property = PropertyHelper.GetPropertyName(pro),
                Direction = dir
            };
            return rv;
        }

        /// <summary>
        /// InitList后触发的事件
        /// </summary>
        public event Action<IBasePagedListVM<TModel, TSearcher>>? OnAfterInitList;

        /// <summary>
        ///记录批量操作时列表中选择的Id
        /// </summary>
        public List<string> Ids { get; set; } = [];
        public string? SelectorValueField { get; set; }
        /// <summary>
        /// 是否已经搜索过
        /// </summary>
        [JsonIgnore]
        public bool IsSearched { get; set; }

        [JsonIgnore]
        public bool PassSearch { get; set; }
        /// <summary>
        /// 查询模式
        /// </summary>
        [JsonIgnore]
        public ListVMSearchModeEnum SearcherMode { get; set; }

        /// <summary>
        /// 是否需要分页
        /// </summary>
        [JsonIgnore]
        public bool NeedPage { get; set; }

        /// <summary>
        /// 允许导出Excel的最大行数，超过行数会分成多个文件，最多不能超过100万
        /// </summary>
        [JsonIgnore]
        public int ExportMaxCount { get; set; }

        /// <summary>
        /// 根据允许导出的Excel最大行数，算出最终导出的Excel个数
        /// </summary>
        [JsonIgnore]
        public int ExportExcelCount { get; set; }

        /// <summary>
        /// 最後一次呼叫 GenerateExcel() 實際匯出的資料列數（不含表頭）。
        /// </summary>
        [JsonIgnore]
        public int ExportRowCount { get; private set; }

        /// <summary>
        /// 导出文件第一行背景颜色，使用HSSFColor，例如：HSSFColor.Red.Index
        /// </summary>
        [JsonIgnore]
        public short? ExportTitleBackColor { get; set; }

        /// <summary>
        /// 导出文件第一行文字颜色，使用HSSFColor，例如：HSSFColor.Red.Index
        /// </summary>
        [JsonIgnore]
        public short? ExportTitleFontColor { get; set; }

        /// <summary>
        /// 数据列表
        /// </summary>
        [JsonIgnore]
        public List<TModel> EntityList { get; set; }

        /// <summary>
        /// 搜索条件
        /// </summary>
        [JsonIgnore]
        public TSearcher Searcher { get; set; }

        /// <summary>
        /// 使用 VM 的 Id 来生成 SearcherDiv 的 Id
        /// </summary>
        [JsonIgnore]
        public string SearcherDivId
        {
            get { return this.UniqueId + "Searcher"; }
        }


        /// <summary>
        /// 替换查询条件，如果被赋值，则列表会使用里面的Lambda来替换原有Query里面的Where条件
        /// </summary>
        [JsonIgnore()]
        public Expression? ReplaceWhere { get; set; }

        /// <summary>
        /// 构造函数
        /// </summary>
        public BasePagedListVM()
        {
            //默认需要分页
            NeedPage = true;
            //初始化数据列表
            EntityList = [];
            //初始化搜索条件
            Searcher = (TSearcher)typeof(TSearcher).GetConstructor(Type.EmptyTypes)!.Invoke(null);
        }

        /// <summary>
        /// 获取数据列表
        /// </summary>
        /// <returns>数据列表</returns>
        public IEnumerable<TModel> GetEntityList()
        {
            if (IsSearched == false && (EntityList == null || EntityList.Count == 0))
            {
                DoSearch();
            }
            return EntityList?.AsEnumerable() ?? Enumerable.Empty<TModel>();
        }


        /// <summary>
        /// 调用InitListVM并触发OnAfterInitList事件
        /// </summary>
        public void DoInitListVM()
        {
            InitListVM();
            OnAfterInitList?.Invoke(this);
        }


        /// <summary>
        /// 初始化ListVM，继承的类应该重载这个函数来设定数据的列和动作
        /// </summary>
        protected virtual void InitListVM()
        {
        }

        public virtual bool GetIsSelected(object item)
        {
            return false;
        }

        public override void Validate()
        {
            Searcher?.Validate();
            base.Validate();
        }

        /// <summary>
        /// 设定行前景色，继承的类应重载这个函数来根据每行的数据显示不同的前景色
        /// </summary>
        /// <param name="entity">数据</param>
        /// <returns>前景颜色</returns>
        public virtual string SetFullRowColor(object entity)
        {
            return "";
        }

        /// <summary>
        /// 设定行背景色，继承的类应重载这个函数来根据每行的数据显示不同的背景色
        /// </summary>
        /// <param name="entity">数据</param>
        /// <returns>背景颜色</returns>
        public virtual string SetFullRowBgColor(object entity)
        {
            return "";
        }

        /// <summary>
        /// 设定搜索语句，继承的类应该重载这个函数来指定自己的搜索语句
        /// </summary>
        /// <returns>搜索语句</returns>
        public virtual IOrderedQueryable<TModel> GetSearchQuery()
        {
            return DC!.Set<TModel>().OrderByDescending(x => x.ID);
        }

        /// <summary>
        /// 设定导出时搜索语句，继承的类应该重载这个函数来指定自己导出时的搜索语句，如不指定则默认和搜索用的搜索语句相同
        /// </summary>
        /// <returns>搜索语句</returns>
        public virtual IOrderedQueryable<TModel> GetExportQuery()
        {
            return GetSearchQuery();
        }

        /// <summary>
        /// 设定搜索语句，继承的类应该重载这个函数来指定自己导出时的搜索语句，如不指定则默认和搜索用的搜索语句相同
        /// </summary>
        /// <returns>搜索语句</returns>
        public virtual IOrderedQueryable<TModel> GetSelectorQuery()
        {
            return GetSearchQuery();
        }

        /// <summary>
        /// 设定勾选后导出的搜索语句，继承的类应该重载这个函数来指定自己导出时的搜索语句，如不指定则默认和搜索用的搜索语句相同
        /// </summary>
        /// <returns>搜索语句</returns>
        public virtual IOrderedQueryable<TModel> GetCheckedExportQuery()
        {
            var baseQuery = GetBatchQuery();
            return baseQuery;
        }

        /// <summary>
        /// 设定批量模式下的搜索语句，继承的类应重载这个函数来指定自己批量模式的搜索语句，如果不指定则默认使用Ids.Contains(x.Id)来代替搜索语句中的Where条件
        /// </summary>
        /// <returns>搜索语句</returns>
        public virtual IOrderedQueryable<TModel> GetBatchQuery()
        {
            var baseQuery = GetSearchQuery();
            if (ReplaceWhere == null)
            {
                Expression? peid = null;
                if (string.IsNullOrEmpty(SelectorValueField) == false && SelectorValueField.ToLower() != "id")
                {
                    // Guard: a form-bound SelectorValueField that names a non-existent
                    // property would make GetSingleProperty return null, causing the
                    // Expression.Property call to throw ArgumentNullException at runtime
                    // (issue #106). When the property is not found, leave peid as null so
                    // the query falls back to the default id-based Contains predicate.
                    var selectorProp = typeof(TModel).GetSingleProperty(SelectorValueField);
                    if (selectorProp != null)
                    {
                        var pe = Expression.Parameter(typeof(TModel));
                        peid = Expression.Property(pe, selectorProp);
                    }
                }
                List<string?> tmpIds = [.. Ids.Cast<string?>()];
                var mod = new WhereReplaceModifier<TModel>(tmpIds.GetContainIdExpression<TModel>(peid));
                var newExp = mod.Modify(baseQuery.Expression);
                var newQuery = baseQuery.Provider.CreateQuery<TModel>(newExp) as IOrderedQueryable<TModel>;
                return newQuery!;
            }
            else
            {
                return baseQuery;
            }
        }

        /// <summary>
        /// 设定主从模式的搜索语句，继承的类应该重载这个函数来指定自己主从模式的搜索语句，如不指定则默认和搜索用的搜索语句相同
        /// </summary>
        /// <returns>搜索语句</returns>
        public virtual IOrderedQueryable<TModel> GetMasterDetailsQuery()
        {
            return GetSearchQuery();
        }

        /// <summary>
        /// 进行搜索
        /// </summary>
        public virtual void DoSearch()
        {
            var cmd = GetSearchCommand();
            if (cmd == null)
            {
                IOrderedQueryable<TModel>? query = null;
                //根据搜索模式调用不同的函数
                switch (SearcherMode)
                {
                    case ListVMSearchModeEnum.Search:
                        query = GetSearchQuery();
                        break;
                    case ListVMSearchModeEnum.Export:
                        query = GetExportQuery();
                        break;
                    case ListVMSearchModeEnum.Batch:
                        query = GetBatchQuery();
                        break;
                    case ListVMSearchModeEnum.MasterDetail:
                        query = GetMasterDetailsQuery();
                        break;
                    case ListVMSearchModeEnum.CheckExport:
                        query = GetCheckedExportQuery();
                        break;
                    case ListVMSearchModeEnum.Selector:
                        query = GetSelectorQuery();
                        break;
                    default:
                        query = GetSearchQuery();
                        break;
                }
                if (query != null)
                {
                    //如果设定了替换条件，则使用替换条件替换Query中的Where语句
                    if (ReplaceWhere != null)
                    {
                        var mod = new WhereReplaceModifier<TModel>((ReplaceWhere as Expression<Func<TModel, bool>>)!);
                        var newExp = mod.Modify(query!.Expression);
                        query = query.Provider.CreateQuery<TModel>(newExp) as IOrderedQueryable<TModel>;
                    }
                    if (Searcher.SortInfo != null)
                    {
                        var mod = new OrderReplaceModifier(Searcher.SortInfo);
                        var newExp = mod.Modify(query!.Expression);
                        query = query.Provider.CreateQuery<TModel>(newExp) as IOrderedQueryable<TModel>;
                    }
                    //if (typeof(IPersistPoco).IsAssignableFrom( typeof(TModel)))
                    //{
                    //    var mod = new IsValidModifier();
                    //    var newExp = mod.Modify(query.Expression);
                    //    query = query.Provider.CreateQuery<TModel>(newExp) as IOrderedQueryable<TModel>;
                    //}
                    if (PassSearch == false)
                    {
                        //如果需要分页，则添加分页语句
                        if (NeedPage && Searcher.Limit != -1)
                        {
                            //获取返回数据的数量
                            var count = query!.Count();
                            if (count < 0)
                            {
                                count = 0;
                            }
                            if (Searcher.Limit == 0)
                            {
                                Searcher.Limit = ConfigInfo?.UIOptions.DataTable.RPP ?? 20;
                            }
                            //根据返回数据的数量，以及预先设定的每页行数来设定数据量和总页数
                            Searcher.Count = count;
                            Searcher.PageCount = (int)Math.Ceiling((1.0 * Searcher.Count / Searcher.Limit));
                            if (Searcher.Page <= 0)
                            {
                                Searcher.Page = 1;
                            }
                            if (Searcher.PageCount > 0 && Searcher.Page > Searcher.PageCount)
                            {
                                Searcher.Page = Searcher.PageCount;
                            }
                            EntityList = [.. query!.Skip((Searcher.Page - 1) * Searcher.Limit).Take(Searcher.Limit).AsNoTracking()];
                        }
                        else //如果不需要分页则直接获取数据
                        {
                            EntityList = [.. query!.AsNoTracking()];
                            Searcher.Count = EntityList.Count;
                            Searcher.Limit = EntityList.Count;
                            Searcher.PageCount = 1;
                            Searcher.Page = 1;
                        }
                    }
                    else
                    {
                        EntityList = [.. query!.AsNoTracking()];
                    }
                }
            }
            else
            {
                ProcessCommand(cmd);
            }
            IsSearched = true;
            //调用AfterDoSearch函数来处理自定义的后续操作
            AfterDoSearcher();
        }


        /// <summary>
        /// 进行搜索（异步版本）。与 <see cref="DoSearch"/> 产生完全相同的结果，
        /// 但使用 EF Core 的 <c>CountAsync</c> / <c>ToListAsync</c> 在数据库层执行异步 I/O。
        /// 查询构建逻辑（GetSearchQuery、ReplaceWhere、SortInfo、分页）与同步版本完全共享，
        /// 仅数据库物化操作变为异步。
        /// </summary>
        /// <remarks>
        /// 当 <see cref="GetSearchCommand"/> 返回非 null 的 <see cref="DbCommand"/> 时，
        /// 此方法回退到同步的 <see cref="ProcessCommand"/> 路径（存储过程暂不支持异步路径）。
        /// </remarks>
        /// <param name="ct">取消令牌。</param>
        public virtual async Task DoSearchAsync(CancellationToken ct = default)
        {
            var cmd = GetSearchCommand();
            if (cmd == null)
            {
                IOrderedQueryable<TModel>? query = null;
                // Mirror the SearcherMode switch from DoSearch exactly.
                switch (SearcherMode)
                {
                    case ListVMSearchModeEnum.Search:
                        query = GetSearchQuery();
                        break;
                    case ListVMSearchModeEnum.Export:
                        query = GetExportQuery();
                        break;
                    case ListVMSearchModeEnum.Batch:
                        query = GetBatchQuery();
                        break;
                    case ListVMSearchModeEnum.MasterDetail:
                        query = GetMasterDetailsQuery();
                        break;
                    case ListVMSearchModeEnum.CheckExport:
                        query = GetCheckedExportQuery();
                        break;
                    case ListVMSearchModeEnum.Selector:
                        query = GetSelectorQuery();
                        break;
                    default:
                        query = GetSearchQuery();
                        break;
                }
                if (query != null)
                {
                    // Apply ReplaceWhere if set.
                    if (ReplaceWhere != null)
                    {
                        var mod = new WhereReplaceModifier<TModel>((ReplaceWhere as Expression<Func<TModel, bool>>)!);
                        var newExp = mod.Modify(query.Expression);
                        query = query.Provider.CreateQuery<TModel>(newExp) as IOrderedQueryable<TModel>;
                    }
                    // Apply custom sort if set.
                    if (Searcher.SortInfo != null)
                    {
                        var mod = new OrderReplaceModifier(Searcher.SortInfo);
                        var newExp = mod.Modify(query!.Expression);
                        query = query.Provider.CreateQuery<TModel>(newExp) as IOrderedQueryable<TModel>;
                    }
                    if (PassSearch == false)
                    {
                        if (NeedPage && Searcher.Limit != -1)
                        {
                            // Async count — keeps the thread free during I/O.
                            var count = await query!.CountAsync(ct).ConfigureAwait(false);
                            if (count < 0)
                            {
                                count = 0;
                            }
                            if (Searcher.Limit == 0)
                            {
                                Searcher.Limit = ConfigInfo?.UIOptions.DataTable.RPP ?? 20;
                            }
                            Searcher.Count = count;
                            Searcher.PageCount = (int)Math.Ceiling((1.0 * Searcher.Count / Searcher.Limit));
                            if (Searcher.Page <= 0)
                            {
                                Searcher.Page = 1;
                            }
                            if (Searcher.PageCount > 0 && Searcher.Page > Searcher.PageCount)
                            {
                                Searcher.Page = Searcher.PageCount;
                            }
                            // Async materialization.
                            EntityList = await query!
                                .Skip((Searcher.Page - 1) * Searcher.Limit)
                                .Take(Searcher.Limit)
                                .AsNoTracking()
                                .ToListAsync(ct)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            EntityList = await query!.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
                            Searcher.Count = EntityList.Count;
                            Searcher.Limit = EntityList.Count;
                            Searcher.PageCount = 1;
                            Searcher.Page = 1;
                        }
                    }
                    else
                    {
                        EntityList = await query!.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                // DbCommand path (stored procedures) does not have an async variant here;
                // fall back to the synchronous processor.
                ProcessCommand(cmd);
            }
            IsSearched = true;
            AfterDoSearcher();
        }

        private void ProcessCommand(DbCommand cmd)
        {
            object? total;

            if (Searcher.Page <= 0)
            {
                Searcher.Page = 1;
            }
            if (DC!.DBType == DBTypeEnum.MySql)
            {
                List<MySqlParameter> parms = [];
                foreach (MySqlParameter item in cmd.Parameters)
                {
                    parms.Add(new MySqlParameter($"@{item.ParameterName}", item.Value));
                }
                if (cmd.CommandType == CommandType.StoredProcedure)
                {
                    parms.Add(new MySqlParameter("@SearchMode", Enum.GetName(typeof(ListVMSearchModeEnum), SearcherMode)));
                    parms.Add(new MySqlParameter("@NeedPage", (NeedPage && Searcher.Limit != -1)));
                    parms.Add(new MySqlParameter("@CurrentPage", Searcher.Page));
                    parms.Add(new MySqlParameter("@RecordsPerPage", Searcher.Limit));
                    parms.Add(new MySqlParameter("@Sort", Searcher.SortInfo?.Property));
                    parms.Add(new MySqlParameter("@SortDir", Searcher.SortInfo?.Direction));
                    parms.Add(new MySqlParameter("@IDs", Ids == null ? "" : Ids.ToSepratedString()));

                    MySqlParameter outp = new MySqlParameter("@TotalRecords", MySqlDbType.Int64)
                    {
                        Value = 0,
                        Direction = ParameterDirection.Output
                    };
                    parms.Add(outp);
                }
                MySqlParameter[] pa = [.. parms];

                EntityList = [.. DC.Run<TModel>(cmd.CommandText, cmd.CommandType, pa)];
                if (cmd.CommandType == CommandType.StoredProcedure)
                {
                    total = pa.Last().Value;
                }
                else
                {
                    total = EntityList.Count;
                }
            }
            else if (DC.Database.IsNpgsql())
            {
                List<NpgsqlParameter> parms = [];
                foreach (NpgsqlParameter item in cmd.Parameters)
                {
                    parms.Add(new NpgsqlParameter($"@{item.ParameterName}", item.Value));
                }

                if (cmd.CommandType == CommandType.StoredProcedure)
                {
                    parms.Add(new NpgsqlParameter("@SearchMode", Enum.GetName(typeof(ListVMSearchModeEnum), SearcherMode)));
                    parms.Add(new NpgsqlParameter("@NeedPage", (NeedPage && Searcher.Limit != -1)));
                    parms.Add(new NpgsqlParameter("@CurrentPage", Searcher.Page));
                    parms.Add(new NpgsqlParameter("@RecordsPerPage", Searcher.Limit));
                    parms.Add(new NpgsqlParameter("@Sort", Searcher.SortInfo?.Property));
                    parms.Add(new NpgsqlParameter("@SortDir", Searcher.SortInfo?.Direction));
                    parms.Add(new NpgsqlParameter("@IDs", Ids == null ? "" : Ids.ToSepratedString()));

                    NpgsqlParameter outp = new NpgsqlParameter("@TotalRecords", NpgsqlDbType.Bigint)
                    {
                        Value = 0,
                        Direction = ParameterDirection.Output
                    };
                    parms.Add(outp);
                }
                NpgsqlParameter[] pa = [.. parms];

                EntityList = [.. DC.Run<TModel>(cmd.CommandText, cmd.CommandType, pa)];
                if (cmd.CommandType == CommandType.StoredProcedure)
                {
                    total = pa.Last().Value;
                }
                else
                {
                    total = EntityList.Count;
                }
            }
            else
            {
                List<SqlParameter> parms = [];
                foreach (SqlParameter item in cmd.Parameters)
                {
                    parms.Add(new SqlParameter($"@{item.ParameterName}", item.Value));
                }
                if (cmd.CommandType == CommandType.StoredProcedure)
                {

                    parms.Add(new SqlParameter("@SearchMode", Enum.GetName(typeof(ListVMSearchModeEnum), SearcherMode)));
                    parms.Add(new SqlParameter("@NeedPage", (NeedPage && Searcher.Limit != -1)));
                    parms.Add(new SqlParameter("@CurrentPage", Searcher.Page));
                    parms.Add(new SqlParameter("@RecordsPerPage", Searcher.Limit));
                    parms.Add(new SqlParameter("@Sort", Searcher.SortInfo?.Property));
                    parms.Add(new SqlParameter("@SortDir", Searcher.SortInfo?.Direction));
                    parms.Add(new SqlParameter("@IDs", Ids == null ? "" : Ids.ToSepratedString()));

                    SqlParameter outp = new SqlParameter("@TotalRecords", 0)
                    {
                        Direction = ParameterDirection.Output
                    };
                    parms.Add(outp);
                }
                SqlParameter[] pa = [.. parms];

                EntityList = [.. DC.Run<TModel>(cmd.CommandText, cmd.CommandType, pa)];
                if (cmd.CommandType == CommandType.StoredProcedure)
                {
                    total = pa.Last().Value;
                }
                else
                {
                    total = EntityList.Count;
                }

            }
            if (NeedPage && Searcher.Limit != -1)
            {
                // L5: guard divide-by-zero if Searcher.Limit was not set before calling ProcessCommand
                if (Searcher.Limit <= 0)
                {
                    Searcher.Limit = ConfigInfo?.UIOptions.DataTable.RPP ?? 20;
                }
                if (total != null)
                {
                    try
                    {
                        Searcher.Count = long.Parse(total.ToString()!);
                        Searcher.PageCount = (int)((Searcher.Count - 1) / Searcher.Limit + 1);
                    }
                    catch (Exception ex)
                    {
                        Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BasePagedListVM")?.LogDebug(ex, "Paging count parse failed for total='{Total}'", total);
                    }
                }
            }
            else
            {
                Searcher.PageCount = EntityList.Count;
            }

        }

        public DateTime AddTime(DateTime dt, string type, int size)
        {
            switch (type)
            {
                case "year":
                    return dt.AddYears(size);
                case "month":
                    return dt.AddMonths(size);
                case "day":
                    return dt.AddDays(size);
                case "hour":
                    return dt.AddHours(size);
                case "minute":
                    return dt.AddMinutes(size);
                case "second":
                    return dt.AddSeconds(size);
                default:
                    return dt;
            }
        }

        /// <summary>
        /// 搜索后运行的函数，继承的类如果需要在搜索结束后进行其他操作，可重载这个函数
        /// </summary>
        public virtual void AfterDoSearcher()
        {
            if (SearcherMode == ListVMSearchModeEnum.Selector && Ids != null && Ids.Count > 0 && EntityList != null && EntityList.Count > 0)
            {
                // #705: this loop was O(rows * ids) — Ids is a List<string>, so
                // Ids.Contains(...) did a linear scan per row — plus a per-row reflection
                // getter (item.GetID() / GetPropertyValue(string) both call
                // PropertyInfo.GetValue via reflection). Build the id lookup once as a
                // HashSet (same equality semantics as List<string>.Contains's default
                // EqualityComparer<string>.Default) and reuse a compiled getter per distinct
                // runtime type instead of reflecting on every row.
                var idSet = new HashSet<string>(Ids);
                bool useIdField = string.IsNullOrEmpty(SelectorValueField) || SelectorValueField.ToLower() == "id";
                string propName = useIdField ? "ID" : SelectorValueField!;
                var getterCache = new Dictionary<Type, Func<object, object?>?>();

                foreach (var item in EntityList)
                {
                    var itemType = item.GetType();
                    if (!getterCache.TryGetValue(itemType, out var getter))
                    {
                        // Preserve the original GetPropertyValue(string) fallback: a
                        // property that doesn't exist on the runtime type (e.g. a
                        // form-tampered SelectorValueField) silently contributes nothing
                        // instead of throwing.
                        getter = itemType.GetSingleProperty(propName) != null
                            ? PropertyHelper.GetPropertyExpression(itemType, propName)
                            : null;
                        getterCache[itemType] = getter;
                    }

                    // Preserve GetPropertyValue's original try/catch: a property getter
                    // that throws must not abort the whole loop — the original non-ID
                    // path (GetPropertyValue) swallowed the exception and effectively
                    // skipped the row (returned "" instead of a matching id).
                    object? v;
                    try
                    {
                        v = getter?.Invoke(item);
                    }
                    catch
                    {
                        v = null;
                    }

                    if (v != null && idSet.Contains(v.ToString()!))
                    {
                        item.Checked = true;
                    }
                }
            }
        }

        /// <summary>
        /// 删除所有ActionGridColumn的列
        /// </summary>
        public void RemoveActionColumn(object? root = null)
        {
            if (root == null)
            {
                if (GridHeaders == null)
                {
                    GetHeaders();
                }
                root = GridHeaders;
            }
            if (root != null)
            {
                //IEnumerable<IGridColumn<TModel>>
                var aroot = root as List<GridColumn<TModel>>;
                if (aroot != null)
                {
                    var toRemove = aroot.Where(x => x.ColumnType == GridColumnTypeEnum.Action).FirstOrDefault();
                    if (toRemove != null)
                    {
                        aroot.Remove(toRemove);
                    }
                    foreach (var child in aroot)
                    {
                        if (child.Children != null && child.Children.Count() > 0)
                        {
                            RemoveActionColumn(child.Children);
                        }
                    }
                }
            }
        }

        public void RemoveAction()
        {
            _gridActions = [];
        }

        public void RemoveActionAndIdColumn(IEnumerable<IGridColumn<TModel>>? root = null)
        {
            if (root == null)
            {
                if (GridHeaders == null)
                {
                    GetHeaders();
                }
                root = GridHeaders;
            }
            if (root != null)
            {
                var aroot = root as List<GridColumn<TModel>>;
                List<GridColumn<TModel>>? remove = null;
                var idpro = typeof(TModel).GetSingleProperty("ID")?.PropertyType;
                if (aroot != null)
                {
                    if (idpro == typeof(string))
                    {
                        remove = [.. aroot.Where(x => x.ColumnType == GridColumnTypeEnum.Action || x.Hide == true || x.DisableExport)];
                    }
                    else
                    {
                        remove = [.. aroot.Where(x => x.ColumnType == GridColumnTypeEnum.Action || x.Hide == true || x.DisableExport || x.FieldName?.ToLower() == "id")];
                    }
                    foreach (var item in remove)
                    {
                        aroot.Remove(item);
                    }
                    foreach (var child in root)
                    {
                        if (child.Children != null && child.Children.Count() > 0)
                        {
                            RemoveActionAndIdColumn(child.Children);
                        }
                    }
                }
            }
        }


        /// <summary>
        /// 添加Error列，主要为批量模式使用
        /// </summary>
        public void AddErrorColumn()
        {
            GetHeaders();
            //寻找所有Header为错误信息的列，如果没有则添加
            if (GridHeaders!.Where(x => x.Field == "BatchError").FirstOrDefault() == null)
            {
                var temp = GridHeaders as List<GridColumn<TModel>>;
                if (temp != null)
                {
                    string? errorHeader = CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.Error"] : null;
                    if (temp.Where(x => x.ColumnType == GridColumnTypeEnum.Action).FirstOrDefault() == null)
                    {
                        temp.Add(this.MakeGridColumn(x => x.BatchError!, Width: 200, Header: errorHeader).SetForeGroundFunc(x => "ff0000").SetFixed(GridColumnFixedEnum.Right));
                    }
                    else
                    {
                        temp.Insert(temp.Count - 1, this.MakeGridColumn(x => x.BatchError!, Width: 200, Header: errorHeader).SetForeGroundFunc(x => "ff0000").SetFixed(GridColumnFixedEnum.Right));
                    }
                }
            }
        }

        public void ProcessListError(List<TModel>? Entities)
        {
            if(Entities == null)
            {
                return;
            }
            EntityList = Entities;
            IsSearched = true;
            bool haserror = false;
            List<string> keys = [];
            if (string.IsNullOrEmpty(DetailGridPrix) == false)
            {
                if (EntityList.Any(x => x.BatchError != null))
                {
                    haserror = true;
                }
                else
                {
                    foreach (var item in MSD!.Keys)
                    {
                        if (item.StartsWith(DetailGridPrix+"["))
                        {
                            var errors = MSD[item];
                            if (errors.Count > 0)
                            {
                                Regex r = WalkingTec.Mvvm.Core.Helper.CoreRegexes.GetDetailGridIndexRegex(DetailGridPrix!);
                                try
                                {
                                    if (int.TryParse(r.Match(item).Groups[1].Value, out int index))
                                    {
                                        EntityList[index].BatchError = errors.Select(x => x.ErrorMessage).ToSepratedString();
                                        keys.Add(item);
                                        haserror = true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BasePagedListVM")?.LogDebug(ex, "BatchError detail-grid index parse failed for key '{Key}'", item);
                                }
                            }
                        }
                    }
                    foreach (var item in keys)
                    {
                        MSD.RemoveModelError(item);
                    }
                }
                if (haserror)
                {
                    AddErrorColumn();
                }
            }
        }

        public TModel CreateEmptyEntity()
        {
            return (TModel)typeof(TModel).GetConstructor(Type.EmptyTypes)!.Invoke(null);
        }

        public void ClearEntityList()
        {
            EntityList?.Clear();
        }

        public string? DetailGridPrix { get; set; }

        public Type ModelType => typeof(TModel);

        #endregion


    }
}