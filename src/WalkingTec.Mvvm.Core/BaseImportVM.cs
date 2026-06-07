#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NPOI.HSSF.Util;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Support.FileHandlers;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Progress snapshot reported by <see cref="BaseImportVM{T,P}.BatchSaveData"/> via
    /// <see cref="IProgress{T}"/> when importing large Excel files (#607).
    /// </summary>
    public readonly struct ImportProgress
    {
        /// <summary>Number of rows that have been processed so far.</summary>
        public int Processed { get; init; }
        /// <summary>Total number of rows to process.</summary>
        public int Total { get; init; }
        /// <summary>Human-readable description of the current phase (e.g. "Validating", "Saving").</summary>
        public string Phase { get; init; }
    }

    /// <summary>
    /// 导入接口
    /// </summary>
    /// <typeparam name="T">导入模版类</typeparam>
    public interface IBaseImport<out T> where T : BaseTemplateVM
    {
        T Template { get; }
        byte[] GenerateTemplate(out string displayName);
        void SetParms(Dictionary<string, string> parms);
        TemplateErrorListVM ErrorListVM { get; set; }
    }

    /// <summary>
    /// 导入基类，Excel导入的类应继承本类
    /// </summary>
    /// <typeparam name="T">导入模版类</typeparam>
    /// <typeparam name="P">导入的Model类</typeparam>
    public class BaseImportVM<T, P> : BaseVM, IBaseImport<T>, IDisposable
        where T : BaseTemplateVM, new()
        where P : TopBasePoco, new()
    {
        private bool _disposed;

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            xssfworkbook?.Close();
            xssfworkbook = null;
        }

        #region 字段、属性
        /// <summary>
        /// 上传文件的Id，方便导入等操作中进行绑定，这类操作需要上传文件但不需要记录在数据库中，所以Model层中没有文件Id的字段
        /// </summary>
        [Display(Name = "UploadFile")]
        public string? UploadFileId { get; set; }

        /// <summary>
        /// 下载模板显示名称
        /// </summary>
        [JsonIgnore]
        public string? FileDisplayName { get; set; }

        /// <summary>
        /// 错误列表
        /// </summary>
        [JsonIgnore]
        public TemplateErrorListVM ErrorListVM { get; set; }

        /// <summary>
        /// Maximum number of errors surfaced via <see cref="InlineErrors"/> (#615).
        /// Defaults to 50. Set to 0 to disable inline errors.
        /// </summary>
        [JsonIgnore]
        public int InlineErrorLimit { get; set; } = 50;

        /// <summary>
        /// Returns up to <see cref="InlineErrorLimit"/> validation errors so the UI can
        /// display them inline (without requiring the user to download an error file).
        /// Returns an empty list when there are no errors or <see cref="InlineErrorLimit"/>
        /// is 0 (#615).
        /// </summary>
        [JsonIgnore]
        public IReadOnlyList<ErrorMessage> InlineErrors =>
            InlineErrorLimit <= 0
                ? Array.Empty<ErrorMessage>()
                : [.. ErrorListVM.EntityList.Take(InlineErrorLimit)];

        /// <summary>
        /// 是否验证模板类型（当其他系统模板导入到某模块时可设置为False）
        /// </summary>
        [JsonIgnore]
        public bool ValidityTemplateType { get; set; }

        /// <summary>
        /// When <c>true</c>, runs all validation and business-rule checks but skips
        /// persisting data. <see cref="ErrorListVM"/> is populated as usual; the caller
        /// can inspect <see cref="EntityList"/> to see which rows would be imported.
        /// The uploaded file is NOT deleted so the user can re-submit for the real import.
        /// Default: <c>false</c>.
        /// </summary>
        [JsonIgnore]
        public bool ValidateOnly { get; set; }

        /// <summary>
        /// 下载模版页面的参数
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, string>? Parms { get; set; }

        protected List<T>? TemplateData;

        /// <summary>
        /// 要导入的Model列表
        /// </summary>
        [JsonIgnore]
        public List<P> EntityList { get; set; }

        /// <summary>
        /// 模版
        /// </summary>
        [JsonIgnore]
        public T Template { get; set; }

        /// <summary>
        /// Model数据是否已被赋值
        /// </summary>
        protected bool isEntityListSet = false;

        /// <summary>
        /// 声明XSSF
        /// </summary>
        protected XSSFWorkbook? xssfworkbook;

        /// <summary>
        /// Maximum allowed XLSX upload size in bytes (default 10 MiB).
        /// Override in a subclass to raise or lower the limit for a specific import.
        /// </summary>
        protected virtual long MaxImportFileBytes => 10 * 1024 * 1024;

        /// <summary>
        /// 唯一性验证
        /// </summary>
        protected DuplicatedInfo<P>? finalInfo;

        /// <summary>
        /// 是否存在主子表
        /// </summary>
        protected bool HasSubTable { get; set; }

        /// <summary>
        /// 是否在sqlserver时使用bulk导入
        /// </summary>
        public bool UseBulkSave { get; set; }

        /// <summary>
        /// 是否覆盖已有数据
        /// </summary>
        public bool IsOverWriteExistData { get; set; } = true;
        #endregion

        #region 构造函数
        public BaseImportVM()
        {
            ErrorListVM = new TemplateErrorListVM();
            ValidityTemplateType = true;
            Template = new T();
            EntityList = [];
        }
        #endregion

        #region  生成excel
        /// <summary>
        /// 生成模版
        /// </summary>
        /// <param name="displayName">模版文件名</param>
        /// <returns>生成的模版</returns>
        public virtual byte[] GenerateTemplate(out string displayName)
        {
            return Template.GenerateTemplate(out displayName);
        }
        #endregion

        #region 设置参数值
        /// <summary>
        /// 设置模版参数
        /// </summary>
        /// <param name="parms">参数</param>
        public void SetParms(Dictionary<string, string> parms)
        {
            Template.Parms = parms;
        }
        #endregion

        #region 可重写方法

        /// <summary>
        /// 设置数据唯一性验证，子类中如果需要数据唯一性验证，应重写此方法
        /// </summary>
        /// <returns>唯一性属性</returns>
        public virtual DuplicatedInfo<P>? SetDuplicatedCheck()
        {
            return null;
        }

        /// <summary>
        /// 获取上传的结果值
        /// </summary>
        public virtual void SetEntityList()
        {
            if (!isEntityListSet)
            {
                EntityList = [];

                //初始化上传的模板数据
                SetTemplateData();

                //如果模板中有错误，直接返回
                if (ErrorListVM.EntityList.Count > 0)
                {
                    return;
                }

                //对EntityList赋值
                SetEntityData();

                //设置标识为初始化
                isEntityListSet = true;
            }
        }

        /// <summary>
        /// 获取上传模板中填写的数据，包含了对模板正确性的验证
        /// </summary>
        public virtual void SetTemplateData()
        {
            if (TemplateData != null && TemplateData.Count > 0)
            {
                return;
            }

            try
            {
                TemplateData = [];

                //【CHECK】上传附件的ID为空
                if (UploadFileId == null)
                {
                    ErrorListVM.EntityList.Add(new ErrorMessage { Message = (CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.PleaseUploadTemplate"] : null) });
                    return;
                }
                Models.IWtmFile? file = null;
                if (Wtm!.ServiceProvider != null)
                {
                    var fp = Wtm!.ServiceProvider.GetRequiredService<WtmFileProvider>();
                   // var tempdc = Wtm.DC;
                    file = fp.GetFile(UploadFileId, true,Wtm!.CreateDC(false));
                    //Wtm.DC = tempdc;
                }
                if (file == null)
                {
                    ErrorListVM.EntityList.Add(new ErrorMessage { Message = (CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.WrongTemplate"] : null) });
                    return;
                }
                // M8: reject oversized uploads before NPOI allocates memory for the full file.
                // Without this guard an attacker can upload a multi-hundred-MB XLSX and
                // exhaust heap; the bare catch below would swallow the OOM as "WrongTemplate".
                if (file.DataStream != null && file.DataStream.CanSeek && file.DataStream.Length > MaxImportFileBytes)
                {
                    ErrorListVM.EntityList.Add(new ErrorMessage
                    {
                        Message = $"Import file exceeds the maximum allowed size ({MaxImportFileBytes / 1024 / 1024} MiB)."
                    });
                    return;
                }
                xssfworkbook = new XSSFWorkbook(file.DataStream);
                file.DataStream?.Dispose();
                Template.InitExcelData();
                Template.InitCustomFormat();

                //【CHECK】判断是否上传的是正确的模板数据
                string? TemplateHiddenName = xssfworkbook.GetSheetAt(1)?.GetRow(0)?.Cells[2]?.ToString();
                if (ValidityTemplateType && !string.Equals(TemplateHiddenName, typeof(T).Name))
                {
                    ErrorListVM.EntityList.Add(new ErrorMessage { Message = (CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.WrongTemplate"] : null) });
                    return;
                }

                //获取数据的Sheet页信息
                ISheet sheet = xssfworkbook.GetSheetAt(0);
                sheet.ForceFormulaRecalculation = true;
                XSSFFormulaEvaluator XE = new XSSFFormulaEvaluator(xssfworkbook);
                IEnumerator rows = sheet.GetEnumerator();
                var cells = sheet.GetRow(0).Cells;

                //获取模板中所有字段的属性
                List<ExcelPropety> ListTemplateProptetys = [];
                List<FieldInfo> ListPropetys = [.. Template.GetType().GetFields().Where(x => x.FieldType == typeof(ExcelPropety))];
                for (int i = 0; i < ListPropetys.Count; i++)
                {
                    ExcelPropety ep = (ExcelPropety)ListPropetys[i].GetValue(Template)!;
                    ListTemplateProptetys.Add(ep);
                }

                //【CHECK】验证模板的列数是否正确
                ExcelPropety? dynamicColumn = ListTemplateProptetys.Where(x => x.DataType == ColumnDataType.Dynamic).FirstOrDefault();
                int columnCount = dynamicColumn == null ? ListTemplateProptetys.Count : (ListTemplateProptetys.Count + dynamicColumn.DynamicColumns.Count - 1);
                if (columnCount != cells.Count)
                {
                    ErrorListVM.EntityList.Add(new ErrorMessage { Message = (CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.WrongTemplate"] : null) });
                    return;
                }

                //【CHECK】判断字段是否根据顺序能一对一相对应。  //是否可以去除？
                // pIndex tracks position in ListTemplateProptetys (one per template property).
                // i tracks position in the actual Excel column headers (may be more for Dynamic).
                // Dynamic columns expand to DynamicColumns.Count cells, so i must skip forward
                // by that many steps while pIndex only advances once (Issue #104, Bug 4).
                int pIndex = 0;
                HasSubTable = false;
                for (int i = 0; i < cells.Count; i++)
                {
                    //是否有子表
                    HasSubTable = ListTemplateProptetys[pIndex].SubTableType != null ? true : HasSubTable;
                    if (ListTemplateProptetys[pIndex].DataType == ColumnDataType.Dynamic)
                    {
                        // Skip forward by the number of dynamic sub-columns minus 1
                        // (the outer for-loop will add 1 more on the next iteration).
                        int dcCount = ListTemplateProptetys[pIndex].DynamicColumns.Count;
                        i += dcCount - 1;
                        pIndex++;
                    }
                    else
                    {
                        pIndex++;
                    }
                }

                //如果有子表，则设置主表字段非必填
                if (HasSubTable)
                {
                    for (int i = 0; i < cells.Count; i++)
                    {
                        ListTemplateProptetys[i].IsNullAble = ListTemplateProptetys[i].SubTableType == null ? true : ListTemplateProptetys[i].IsNullAble;
                    }
                }

                // If the template was generated with a description row (v2), skip it (#615).
                bool hasDescriptionRow = xssfworkbook.GetSheetAt(1)?.GetRow(0)?.GetCell(3)?.ToString() == "v2";

                //向TemplateData中赋值
                int rowIndex = 2;
                rows.MoveNext(); // skip header row
                if (hasDescriptionRow)
                {
                    rows.MoveNext(); // skip description row
                    rowIndex = 3;
                }
                while (rows.MoveNext())
                {
                    XSSFRow row = (XSSFRow)rows.Current;
                    if (IsEmptyRow(row, columnCount))
                    {
                        // M7: skip blank rows mid-file instead of terminating the whole import.
                        // A bare `return` here caused every blank separator row to silently
                        // truncate all subsequent data and report success.
                        continue;
                    }

                    T result = new T();
                    pIndex = 0;
                    for (int i = 0; i < columnCount; i++)
                    {
                        //获取列的值
                        string value = row.GetCell(i, MissingCellPolicy.CREATE_NULL_AS_BLANK).ToString() ?? string.Empty;
                        ExcelPropety excelPropety = CopyExcelPropety(ListTemplateProptetys[pIndex]);

                        if (excelPropety.DataType == ColumnDataType.Text)
                        {
                            ICell cell = row.GetCell(i);
                            value = GetCellFormulaValue(XE, cell, value);
                        }

                        if (excelPropety.DataType == ColumnDataType.Dynamic)
                        {
                            int dynamicColCount = excelPropety.DynamicColumns.Count();
                            for (int dynamicColIndex = 0; dynamicColIndex < dynamicColCount; dynamicColIndex++)
                            {
                                excelPropety.DynamicColumns[dynamicColIndex].ValueValidity(row.GetCell(i + dynamicColIndex, MissingCellPolicy.CREATE_NULL_AS_BLANK).ToString(), ErrorListVM.EntityList, rowIndex);
                            }
                            i = i + dynamicColCount - 1;
                        }
                        else
                        {
                            excelPropety.ValueValidity(value, ErrorListVM.EntityList, rowIndex);
                        }

                        //如果没有错误，进行赋值
                        if (ErrorListVM.EntityList.Count == 0)
                        {
                            var pts = ListPropetys[pIndex];
                            pts.SetValue(result, excelPropety);
                        }

                        pIndex++;
                    }
                    result.ExcelIndex = rowIndex;
                    TemplateData.Add(result);
                    rowIndex++;
                }

                return;
            }
            catch (OutOfMemoryException)
            {
                // M8: OOM must not be swallowed as "WrongTemplate" — it indicates a resource
                // exhaustion that the process cannot safely recover from.  Rethrow so the
                // runtime crash-handler / ASP.NET middleware can recycle or log appropriately.
                throw;
            }
            catch
            {
                ErrorListVM.EntityList.Add(new ErrorMessage { Message = (CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.WrongTemplate"] : null) });
            }
        }

        #region 进行公式计算
        public string GetCellFormulaValue(XSSFFormulaEvaluator XE, ICell? cell, string Value)
        {
            if (!string.IsNullOrEmpty(Value) && Value.IndexOf("=") == 0)
            {
                // Block dangerous external-reference formulas to prevent formula injection attacks
                string formulaUpper = Value.Substring(1).TrimStart().ToUpperInvariant();
                if (formulaUpper.Contains('|') || formulaUpper.StartsWith("CMD") ||
                    formulaUpper.StartsWith("WEBSERVICE") || formulaUpper.StartsWith("HYPERLINK") ||
                    formulaUpper.StartsWith("IMPORTXML") || formulaUpper.StartsWith("IMPORTDATA") ||
                    formulaUpper.StartsWith("IMPORTFEED") || formulaUpper.StartsWith("IMPORTRANGE"))
                {
                    return Value;
                }

                try
                {
                    string Formula = Value.Substring(1);
                    cell!.SetCellFormula(Formula);
                    XE.EvaluateFormulaCell(cell!);
                    Value = cell!.NumericCellValue.ToString();
                }
                catch (Exception ex)
                {
                    Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseImportVM")?.LogDebug(ex, "Excel formula evaluation failed for '{Value}'; falling back to raw string", Value);
                }
            }
            return Value;
        }
        #endregion

        /// <summary>
        /// 根据模板中的数据，填写导入类的集合中
        /// </summary>
        public virtual void SetEntityData()
        {
            //反射出类中所有属性字段 P是Model层定义的类
            var pros = typeof(P).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            //反射出模板类中的所有属性字段 T是模板类，ExcelProperty 是自定义的Excel属性类
            List<FieldInfo> ListExcelFields = [.. typeof(T).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Where(x => x.FieldType == typeof(ExcelPropety))];

            //循环Excel中的数据
            foreach (var item in TemplateData!)
            {
                // Derive the real Excel row number from the template item so that error
                // messages in FormatData/FormatSingleData report the correct row (#150 L4).
                int rowIndex = (int)item.ExcelIndex;
                bool isMainData = false;

                //主表信息
                Dictionary<string, ExcelPropety> ParentEntity = new Dictionary<string, ExcelPropety>();
                string ParentEntityValues = string.Empty;

                //子表信息
                Dictionary<Type, List<FieldInfo>> ChildrenEntity = new Dictionary<Type, List<FieldInfo>>();
                Dictionary<Type, string> ChildrenEntityDic = new Dictionary<Type, string>();

                //循环TemplateVM中定义的所有的列，区分出主子表
                foreach (var ExcelField in ListExcelFields)
                {
                    //获取本列的ExcelProperty的值
                    if (typeof(T).GetField(ExcelField.Name)?.GetValue(item) is ExcelPropety ep)
                    {
                        //如果是子表的字段
                        if (ep.SubTableType != null)
                        {
                            //保存子表字段信息稍后处理
                            if (!ChildrenEntity.ContainsKey(ep.SubTableType))
                            {
                                ChildrenEntity[ep.SubTableType] = [];
                            }
                            ChildrenEntity[ep.SubTableType].Add(ExcelField);
                        }
                        else
                        {
                            //保存子表字段信息稍后处理
                            ParentEntity.Add(ep.FieldName, ep);
                            ParentEntityValues += ep.Value;
                        }
                    }
                }

                //子表信息是否为空
                foreach (var sub in ChildrenEntity)
                {
                    string subVal = string.Empty;
                    foreach (var field in sub.Value)
                    {
                        ExcelPropety? ep = typeof(T).GetField(field.Name)?.GetValue(item) as ExcelPropety;
                        subVal += ep?.Value;
                    }
                    ChildrenEntityDic.Add(sub.Key, subVal);
                }

                P? entity = null;

                //说明主表信息为空
                if (string.IsNullOrEmpty(ParentEntityValues))
                {
                    entity = EntityList.LastOrDefault();
                    // M9: if EntityList is empty the very first row already has empty parent
                    // columns (sub-table row before any parent row).  Dereferencing null below
                    // (GetType, GetID, SetPropertyValue, ExcelIndex) would produce an NRE → 500.
                    // Emit a clear diagnostic and skip this orphaned row.
                    if (entity == null)
                    {
                        ErrorListVM.EntityList.Add(new ErrorMessage
                        {
                            Message = "Sub-table row appears before any parent row.",
                            ExcelIndex = item.ExcelIndex
                        });
                        continue;
                    }
                }
                else
                {
                    //初始化一个新的Entity
                    entity = new P();
                    isMainData = true;

                    //给主表赋值
                    foreach (var mep in ParentEntity)
                    {
                        SetEntityFieldValue(entity, mep.Value, rowIndex, mep.Key, item);
                    }
                    if (typeof(ITenant).IsAssignableFrom(entity!.GetType()))
                    {
                        ITenant? ent = entity as ITenant;
                        if (ent != null) ent.TenantCode = LoginUserInfo?.CurrentTenant;
                    }

                }

                //给子表赋值
                foreach (var sub in ChildrenEntity)
                {
                    //循环Entity的所有属性，找到List<SubTableType>类型的字段
                    foreach (var pro in pros)
                    {
                        if (pro.PropertyType.IsGenericType)
                        {
                            var gtype = pro.PropertyType.GetGenericArguments()[0];
                            if (gtype == sub.Key)
                            {
                                //子表
                                var subList = entity!.GetType().GetSingleProperty(pro.Name)?.GetValue(entity);
                                string fk = DC!.GetFKName<P>(pro.Name);

                                //如果子表不为空
                                if (!string.IsNullOrEmpty(ChildrenEntityDic.Where(x => x.Key == sub.Key).FirstOrDefault().Value))
                                {
                                    IList? list = null;
                                    if (subList == null)
                                    {
                                        //初始化List<SubTableType>
                                        list = typeof(List<>).MakeGenericType(gtype).GetConstructor(Type.EmptyTypes)?.Invoke(null) as IList;
                                    }
                                    else
                                    {
                                        list = subList as IList;
                                    }

                                    //初始化一个SubTableType
                                    var SubTypeEntity = gtype.GetConstructor(System.Type.EmptyTypes)!.Invoke(null);

                                    //给SubTableType中和本ExcelProperty同名的字段赋值
                                    foreach (var field in sub.Value)
                                    {
                                        ExcelPropety? ep = typeof(T).GetField(field.Name)?.GetValue(item) as ExcelPropety;
                                        SetEntityFieldValue(SubTypeEntity, ep!, rowIndex, ep!.FieldName, item);
                                    }

                                    if (string.IsNullOrEmpty(fk) == false)
                                    {
                                        PropertyHelper.SetPropertyValue(SubTypeEntity, fk, entity.GetID());
                                    }

                                    if (typeof(IBasePoco).IsAssignableFrom(SubTypeEntity.GetType()))
                                    {
                                        (SubTypeEntity as IBasePoco)!.CreateTime = Wtm!.TimeProvider.GetLocalNow().DateTime;
                                        (SubTypeEntity as IBasePoco)!.CreateBy = LoginUserInfo?.ITCode;
                                    }
                                    if (typeof(ITenant).IsAssignableFrom(SubTypeEntity.GetType()))
                                    {
                                        ITenant? ent = SubTypeEntity as ITenant;
                                        if (ent != null) ent.TenantCode = LoginUserInfo?.CurrentTenant;
                                    }

                                    //var context = new ValidationContext(SubTypeEntity);
                                    //var validationResults = new List<ValidationResult>();
                                    //TryValidateObject(SubTypeEntity, context, validationResults);
                                    //if (validationResults.Count == 0)
                                    //{
                                    //将付好值得SubTableType实例添加到List中
                                    list!.Add(SubTypeEntity);

                                    PropertyHelper.SetPropertyValue(entity!, pro.Name, list);
                                    //}
                                    //else
                                    //{
                                    //    ErrorListVM.EntityList.Add(new ErrorMessage { Message = validationResults.FirstOrDefault()?.ErrorMessage ?? "Error", ExcelIndex = item.ExcelIndex });
                                    //    break;
                                    //}

                                }
                                break;
                            }
                        }
                    }

                }
                entity!.ExcelIndex = item.ExcelIndex;
                if (isMainData)
                {
                    EntityList.Add(entity!);
                }
            }
        }

        /// <summary>
        /// 进行上传中的错误验证
        /// </summary>
        public virtual void SetValidateCheck()
        {
            //找到对应的BaseCRUDVM，并初始化
            List<Type> vms = [.. this.GetType().Assembly.GetExportedTypes().Where(x => x.IsSubclassOf(typeof(BaseCRUDVM<P>)))];
            var vmtype = vms.Where(x => x.Name.ToLower() == typeof(P).Name.ToLower() + "vm").FirstOrDefault();
            if (vmtype == null)
            {
                vmtype = vms.FirstOrDefault();
            }

            IBaseCRUDVM<P>? vm = null;
            DuplicatedInfo<P>? dinfo = null;
            if (vmtype != null)
            {
                vm = vmtype.GetConstructor(System.Type.EmptyTypes)?.Invoke(null) as IBaseCRUDVM<P>;
                vm?.CopyContext(this);
                dinfo = (vm as dynamic)?.SetDuplicatedCheck();
            }
            var cinfo = this.SetDuplicatedCheck();
            finalInfo = new DuplicatedInfo<P>
            {
                Groups = []
            };
            if (cinfo != null)
            {
                foreach (var item in cinfo.Groups)
                {
                    finalInfo.Groups.Add(item);
                }
            }
            else if (dinfo != null)
            {
                foreach (var item in dinfo.Groups)
                {
                    finalInfo.Groups.Add(item);
                }
            }
            //调用controller方法验证model
            //var vmethod = Controller?.GetType().GetMethod("RedoValidation");
            foreach (var entity in EntityList)
            {
                //try
                //{
                //    vmethod.Invoke(Controller, new object[] { entity });
                //}
                //catch { }

                if (vm != null)
                {
                    vm.SetEntity(entity);
                    vm.ByPassBaseValidation = true;
                    vm.Validate();
                    var basevm = vm as BaseVM;
                    if (basevm?.MSD?.Count > 0)
                    {
                        foreach (var key in basevm.MSD.Keys)
                        {
                            foreach (var error in basevm.MSD[key])
                            {
                                ErrorListVM.EntityList.Add(new ErrorMessage { Message = error.ErrorMessage, Index = entity.ExcelIndex });
                            }
                        }
                    }
                }
                (vm as BaseVM)?.MSD?.Clear();

                //在本地EntityList中验证是否有重复
                ValidateDuplicateData(finalInfo, entity);
            }
        }

        protected void SetEntityFieldValue(object entity, ExcelPropety ep, int rowIndex, string fieldName, T templateVM)
        {
            if (ep.FormatData != null)
            {
                ProcessResult processResult = ep.FormatData(ep.Value, templateVM);
                if (processResult != null)
                {
                    //未添加任何处理结果
                    if (processResult.EntityValues.Count == 0)
                    {
                        PropertyHelper.SetPropertyValue(entity, fieldName, ep.Value, stringBasedValue: true);
                    }
                    //字段为一对一
                    if (processResult.EntityValues.Count == 1)
                    {
                        ep.Value = processResult.EntityValues[0].FieldValue;
                        if (!string.IsNullOrEmpty(processResult.EntityValues[0].ErrorMsg))
                        {
                            ErrorListVM.EntityList.Add(new ErrorMessage { Message = processResult.EntityValues[0].ErrorMsg, ExcelIndex = rowIndex, Index = rowIndex });
                        }
                        PropertyHelper.SetPropertyValue(entity, fieldName, ep.Value, stringBasedValue: true);
                    }
                    //字段为一对多
                    if (processResult.EntityValues.Count > 1)
                    {
                        foreach (var entityValue in processResult.EntityValues)
                        {
                            if (!string.IsNullOrEmpty(entityValue.ErrorMsg))
                            {
                                ErrorListVM.EntityList.Add(new ErrorMessage { Message = entityValue.ErrorMsg, ExcelIndex = rowIndex, Index = rowIndex });
                            }
                            PropertyHelper.SetPropertyValue(entity, entityValue.FieldName, entityValue.FieldValue, stringBasedValue: true);
                        }
                    }
                }
            }
            else if (ep.FormatSingleData != null)
            {
                ep.FormatSingleData(ep.Value, templateVM, out string singleEntityValue, out string errorMsg);
                if (!string.IsNullOrEmpty(errorMsg))
                {
                    ErrorListVM.EntityList.Add(new ErrorMessage { Message = errorMsg, ExcelIndex = rowIndex, Index = rowIndex });
                }
                PropertyHelper.SetPropertyValue(entity, fieldName, singleEntityValue, stringBasedValue: true);
            }
            else
            {
                PropertyHelper.SetPropertyValue(entity, fieldName, ep.Value, stringBasedValue: true);
            }
        }

        protected bool IsUpdateRecordDuplicated(DuplicatedInfo<P> checkCondition, P entity)
        {
            if (checkCondition != null && checkCondition.Groups.Count > 0)
            {
                //生成基础Query
                var baseExp = EntityList.AsQueryable();
                var modelType = typeof(P);
                ParameterExpression para = Expression.Parameter(modelType, "tm");
                //循环所有重复字段组
                foreach (var group in checkCondition.Groups)
                {
                    List<Expression> conditions = [];
                    //生成一个表达式，类似于 x=>x.Id != id，这是为了当修改数据时验证重复性的时候，排除当前正在修改的数据
                    var idproperty = modelType.GetSingleProperty("ID")!;
                    MemberExpression idLeft = Expression.Property(para, idproperty);
                    ConstantExpression idRight = Expression.Constant(entity.GetID());
                    BinaryExpression idNotEqual = Expression.NotEqual(idLeft, idRight);
                    conditions.Add(idNotEqual);
                    List<PropertyInfo> props = [];
                    //在每个组中循环所有字段
                    foreach (var field in group.Fields)
                    {
                        Expression? exp = field.GetExpression(entity, para);
                        if (exp != null)
                        {
                            conditions.Add(exp);
                        }
                        //将字段名保存，为后面生成错误信息作准备
                        props.AddRange(field.GetProperties());
                    }
                    int count = 0;
                    if (conditions.Count > 1)
                    {
                        //循环添加条件并生成Where语句
                        Expression conExp = conditions[0];
                        for (int i = 1; i < conditions.Count; i++)
                        {
                            conExp = Expression.AndAlso(conExp, conditions[i]);
                        }

                        MethodCallExpression whereCallExpression = Expression.Call(
                             typeof(Queryable),
                             "Where",
                             new Type[] { modelType },
                             baseExp.Expression,
                             Expression.Lambda<Func<P, bool>>(conExp, new ParameterExpression[] { para }));
                        var result = baseExp.Provider.CreateQuery(whereCallExpression);

                        foreach (var res in result)
                        {
                            count++;
                        }
                    }
                    if (count > 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        protected void ValidateDuplicateData(DuplicatedInfo<P> checkCondition, P entity)
        {
            if (checkCondition != null && checkCondition.Groups.Count > 0)
            {
                //生成基础Query
                var baseExp = EntityList.AsQueryable();
                var modelType = typeof(P);
                ParameterExpression para = Expression.Parameter(modelType, "tm");
                //循环所有重复字段组
                foreach (var group in checkCondition.Groups)
                {
                    List<Expression> conditions = [];
                    //生成一个表达式，类似于 x=>x.Id != id，这是为了当修改数据时验证重复性的时候，排除当前正在修改的数据
                    var idproperty = modelType.GetSingleProperty("ExcelIndex")!;
                    MemberExpression idLeft = Expression.Property(para, idproperty);
                    ConstantExpression idRight = Expression.Constant(entity.ExcelIndex);
                    BinaryExpression idNotEqual = Expression.NotEqual(idLeft, idRight);
                    conditions.Add(idNotEqual);
                    List<PropertyInfo> props = [];
                    //在每个组中循环所有字段
                    foreach (var field in group.Fields)
                    {
                        Expression? exp = field.GetExpression(entity, para);
                        if (exp != null)
                        {
                            conditions.Add(exp);
                        }
                        //将字段名保存，为后面生成错误信息作准备
                        props.AddRange(field.GetProperties());
                    }
                    int count = 0;
                    if (conditions.Count > 1)
                    {
                        //循环添加条件并生成Where语句
                        Expression whereCallExpression = baseExp.Expression;
                        for (int i = 0; i < conditions.Count; i++)
                        {
                            whereCallExpression = Expression.Call(
                                 typeof(Queryable),
                                 "Where",
                                 new Type[] { modelType },
                                 whereCallExpression,
                                 Expression.Lambda<Func<P, bool>>(conditions[i], new ParameterExpression[] { para }));
                        }
                        var result = baseExp.Provider.CreateQuery(whereCallExpression);

                        foreach (var res in result)
                        {
                            count++;
                        }
                    }
                    if (count > 0)
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
                            ErrorListVM.EntityList.Add(new ErrorMessage { Message = (CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.DuplicateError", AllName] : null), Index = entity.ExcelIndex });
                        }
                        //如果多个字段重复，则拼接形成 xx，yy，zz组合字段重复 这种提示
                        else if (props.Count > 1)
                        {
                            ErrorListVM.EntityList.Add(new ErrorMessage { Message = (CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.DuplicateGroupError", AllName] : null), Index = entity.ExcelIndex });
                        }
                    }
                }
            }
        }


        private void TryValidateObject(object model, ValidationContext context, ICollection<ValidationResult> results)
        {
            var modelType = model.GetType();
            foreach (var p in modelType.GetProperties())
            {
                var propertyValue = p.GetValue(model);
                TryValidateProperty(propertyValue, context, results, p);
            }
        }

        private void TryValidateProperty(object? value, ValidationContext context, ICollection<ValidationResult> results, PropertyInfo? propertyInfo = null)
        {
            var modelType = context.ObjectType;
            if (propertyInfo == null)
            {
                propertyInfo = modelType.GetProperty(context.MemberName!);
            }

            if (propertyInfo != null)
            {
                var rules = propertyInfo.GetCustomAttributes(true).Where(i => i.GetType().BaseType == typeof(ValidationAttribute)).Cast<ValidationAttribute>();
                var displayName = propertyInfo.GetPropertyDisplayName();
                var memberName = propertyInfo.Name;
                foreach (var rule in rules)
                {
                    if (!rule.IsValid(value))
                    {
                        string errorMessage = "Error";
                        if (!string.IsNullOrEmpty(rule.ErrorMessage))
                        {
                            var loc = Wtm?.Localizer;
                            if (rule is RangeAttribute range)
                            {
                                if (range.Minimum != null && range.Maximum != null)
                                {
                                    errorMessage = loc != null ? (string)loc[rule.ErrorMessage, displayName, range.Minimum, range.Maximum] : rule.ErrorMessage;
                                }
                                else if (range.Minimum != null)
                                {
                                    errorMessage = loc != null ? (string)loc[rule.ErrorMessage, displayName, range.Minimum] : rule.ErrorMessage;
                                }
                                else if (range.Maximum != null)
                                {
                                    errorMessage = loc != null ? (string)loc[rule.ErrorMessage, displayName, range.Maximum] : rule.ErrorMessage;
                                }
                            }
                            else if (rule is StringLengthAttribute sl)
                            {
                                if (sl.MaximumLength > 0 && sl.MinimumLength > 0)
                                {
                                    errorMessage = loc != null ? (string)loc[rule.ErrorMessage, displayName, sl.MinimumLength, sl.MaximumLength] : rule.ErrorMessage;
                                }
                                else if (sl.MinimumLength > 0)
                                {
                                    errorMessage = loc != null ? (string)loc[rule.ErrorMessage, displayName, sl.MinimumLength] : rule.ErrorMessage;
                                }
                                else if (sl.MaximumLength > 0)
                                {
                                    errorMessage = loc != null ? (string)loc[rule.ErrorMessage, displayName, sl.MaximumLength] : rule.ErrorMessage;
                                }
                            }
                            else
                            {
                                errorMessage = loc != null ? (string)loc[rule.ErrorMessage, displayName] : rule.ErrorMessage;
                            }
                        }
                        results.Add(new ValidationResult(errorMessage, new string[] { memberName }));
                    }
                }
            }
        }


        /// <summary>
        /// 保存指定表中的数据
        /// </summary>
        /// <param name="progress">
        /// Optional progress sink. Reports <see cref="ImportProgress"/> after every processed
        /// row during the validation and save phases so callers can display a progress bar.
        /// </param>
        /// <returns>成功返回True，失败返回False</returns>
        public virtual bool BatchSaveData(IProgress<ImportProgress>? progress = null)
        {
            //删除不必要的附件
            if (DeletedFileIds != null && DeletedFileIds.Count > 0 && Wtm!.ServiceProvider != null)
            {
                var fp = Wtm!.ServiceProvider.GetRequiredService<WtmFileProvider>();

                foreach (var item in DeletedFileIds)
                {
                    fp.DeleteFile(item.ToString(), Wtm!.CreateDC(false));
                }
            }

            //进行赋值
            SetEntityList();
            int total = EntityList.Count;
            int processed = 0;

            // EVM-008: wrap per-row data-annotation validation in try/catch so a single
            // bad row records its own error instead of aborting the entire file with
            // "WrongTemplate".  We collect ALL per-row errors before returning, then
            // report them via ErrorListVM (the framework's existing import-error channel).
            foreach (var entity in EntityList)
            {
                try
                {
                    var context = new ValidationContext(entity);
                    List<ValidationResult> validationResults = [];
                    TryValidateObject(entity, context, validationResults);
                    if (validationResults.Count > 0)
                    {
                        ErrorListVM.EntityList.Add(new ErrorMessage { Message = validationResults.FirstOrDefault()?.ErrorMessage ?? "Error", ExcelIndex = entity.ExcelIndex, Index = entity.ExcelIndex });
                    }
                }
                catch (Exception ex)
                {
                    // Surface which row failed and why — do not swallow silently.
                    ErrorListVM.EntityList.Add(new ErrorMessage
                    {
                        Message = ex.Message,
                        ExcelIndex = entity.ExcelIndex,
                        Index = entity.ExcelIndex
                    });
                }
                progress?.Report(new ImportProgress { Processed = ++processed, Total = total, Phase = "Validating" });
            }
            if (ErrorListVM.EntityList.Count > 0)
            {
                DoReInit();
                return false;
            }

            //执行验证
            SetValidateCheck();
            if (ErrorListVM.EntityList.Count > 0)
            {
                DoReInit();
                return false;
            }
            var ModelType = typeof(P);
            //循环数据列表
            List<P> ListAdd = [];
            processed = 0;

            // EVM-002 + EVM-008 (import contract): validate-all-then-commit.
            // Open a transaction that covers both the per-row DC staging and the final
            // SaveChanges so a mid-row exception or SaveChanges failure rolls back ALL
            // rows atomically, preventing partial imports.
            // BeginTransaction() is a no-op for the InMemory provider so the single-DB
            // happy path is completely unaffected.
            using var tx = DC!.BeginTransaction();
            foreach (var item in EntityList)
            {
                // EVM-008: isolate per-row staging exceptions — record against the row
                // and continue so ALL bad rows are reported before stopping.
                try
                {
                    //根据唯一性的设定查找数据库中是否有同样的数据
                    P? exist = IsDuplicateData(item, finalInfo);
                    //如果设置了覆盖功能
                    if (IsOverWriteExistData)
                    {
                        if (exist != null)
                        {
                            //如果有重复数据，则进行修改
                            var tempPros = typeof(T).GetFields();
                            foreach (var pro in tempPros)
                            {
                                var excelProp = Template.GetType().GetField(pro.Name)?.GetValue(Template) as ExcelPropety;
                                var proToSet = excelProp != null ? typeof(P).GetSingleProperty(excelProp.FieldName) : null;
                                if (proToSet != null)
                                {
                                    var val = proToSet.GetValue(item);
                                    PropertyHelper.SetPropertyValue(exist, excelProp!.FieldName, val, stringBasedValue: true);
                                    try
                                    {
                                        DC!.UpdateProperty(exist, proToSet.Name);
                                    }
                                    catch (Exception ex)
                                    {
                                        Wtm?.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("BaseImportVM")?.LogWarning(ex, "Import update: UpdateProperty failed for '{Property}' on duplicate row", proToSet.Name);
                                    }
                                }
                            }

                            if (tempPros.Where(x => x.Name == "UpdateTime").SingleOrDefault() == null)
                            {
                                if (typeof(IBasePoco).IsAssignableFrom(exist.GetType()))
                                {
                                    (exist as IBasePoco)!.UpdateTime = Wtm!.TimeProvider.GetLocalNow().DateTime;
                                    DC!.UpdateProperty(exist, "UpdateTime");
                                }
                            }

                            if (tempPros.Where(x => x.Name == "UpdateBy").SingleOrDefault() == null)
                            {
                                if (typeof(IBasePoco).IsAssignableFrom(exist.GetType()))
                                {
                                    (exist as IBasePoco)!.UpdateBy = LoginUserInfo?.ITCode;
                                    DC!.UpdateProperty(exist, "UpdateBy");
                                }
                            }
                            exist.ExcelIndex = item.ExcelIndex;
                            //DC.UpdateEntity(exist);

                            continue;
                        }
                        else
                        {
                            if (typeof(IPersistPoco).IsAssignableFrom(item.GetType()))
                            {
                                (item as IPersistPoco)!.IsValid = true;
                            }
                        }
                    }
                    else
                    {
                        if (exist == null)
                        {
                            if (typeof(IPersistPoco).IsAssignableFrom(ModelType))
                            {
                                (item as IPersistPoco)!.IsValid = true;
                            }
                        }
                    }
                    //进行添加操作
                    if (typeof(IBasePoco).IsAssignableFrom(item.GetType()))
                    {
                        (item as IBasePoco)!.CreateTime = Wtm!.TimeProvider.GetLocalNow().DateTime;
                        (item as IBasePoco)!.CreateBy = LoginUserInfo?.ITCode;
                    }
                    if (typeof(ITenant).IsAssignableFrom(ModelType))
                    {
                        ITenant? ent = item as ITenant;
                        if (ent != null) ent.TenantCode = LoginUserInfo?.CurrentTenant;
                    }

                    //如果是SqlServer数据库，而且没有主子表功能，进行Bulk插入
                    var connInfo = ConfigInfo?.Connections.Where(x => x.Key == (CurrentCS ?? "default")).FirstOrDefault();
                    if (connInfo != null && connInfo.DbType == DBTypeEnum.SqlServer && !HasSubTable && UseBulkSave == true)
                    {
                        //ListAdd.Add(item);
                    }
                    else
                    {
                        DC!.Set<P>().Add(item);
                    }
                    progress?.Report(new ImportProgress { Processed = ++processed, Total = total, Phase = "Saving" });
                }
                catch (Exception ex)
                {
                    // EVM-008: record the per-row error and continue to collect remaining
                    // row errors — do NOT silently swallow; surface row index + reason.
                    ErrorListVM.EntityList.Add(new ErrorMessage
                    {
                        Message = ex.Message,
                        ExcelIndex = item.ExcelIndex,
                        Index = item.ExcelIndex
                    });
                }
            }

            // If any row-staging errors occurred, roll back the transaction and report.
            if (ErrorListVM.EntityList.Count > 0)
            {
                try { tx.Rollback(); } catch { /* swallow nested-tx rethrow */ }
                DoReInit();
                return false;
            }

            //如果没有错误，更新数据库
            if (EntityList.Count > 0 && !ValidateOnly)
            {
                try
                {
                    DC!.SaveChanges();
                    tx.Commit();

                    if (ListAdd.Count > 0)
                    {
                        BulkInsert<P>(DC!, DC!.GetTableName<P>(), ListAdd);
                    }
                }
                catch (Exception e)
                {
                    try { tx.Rollback(); } catch { /* swallow nested-tx rethrow */ }
                    SetExceptionMessage(e, null);
                    DoReInit();
                    return false;
                }
            }
            else
            {
                // ValidateOnly mode or empty list — nothing to commit; dispose cleanly.
                try { tx.Rollback(); } catch { /* swallow nested-tx rethrow */ }
            }

            if (!ValidateOnly && string.IsNullOrEmpty(UploadFileId) == false && Wtm!.ServiceProvider != null)
            {
                var fp = Wtm!.ServiceProvider.GetRequiredService<WtmFileProvider>();
                fp.DeleteFile(UploadFileId, Wtm!.CreateDC(false, "default"));
            }

            return true;
        }

        /// <summary>
        /// 批量插入数据库操作，支持SqlServer
        /// </summary>
        /// <typeparam name="K"></typeparam>
        /// <param name="dc">data context</param>
        /// <param name="tableName"></param>
        /// <param name="list"></param>
        protected static void BulkInsert<K>(IDataContext dc, string tableName, IList<K> list)
        {
            //using (var bulkCopy = new SqlBulkCopy(dc.CSName))
            //{
            //    bulkCopy.BatchSize = list.Count;
            //    bulkCopy.DestinationTableName = tableName;

            //    var table = new DataTable();
            //    var props = typeof(K).GetAllProperties().Distinct(x => x.Name);

            //    //生成Table的列
            //    foreach (var propertyInfo in props)
            //    {
            //        var notmapped = propertyInfo.GetCustomAttribute<NotMappedAttribute>();
            //        var notobject = propertyInfo.PropertyType.Namespace.Equals("System") || propertyInfo.PropertyType.IsEnumOrNullableEnum();
            //        if (notmapped == null && notobject)
            //        {
            //            string Name = dc.GetFieldName<K>(propertyInfo.Name);
            //            bulkCopy.ColumnMappings.Add(Name, Name);
            //            table.Columns.Add(Name, Nullable.GetUnderlyingType(propertyInfo.PropertyType) ?? propertyInfo.PropertyType);
            //        }
            //    }

            //    //给Table赋值
            //    var values = new object[table.Columns.Count];
            //    foreach (var item in list)
            //    {
            //        var Index = 0;
            //        foreach (var propertyInfo in props)
            //        {
            //            var notmapped = propertyInfo.GetCustomAttribute<NotMappedAttribute>();
            //            var notobject = propertyInfo.PropertyType.Namespace.Equals("System") || propertyInfo.PropertyType.IsEnumOrNullableEnum();
            //            if (notmapped == null && notobject)
            //            {
            //                values[Index] = propertyInfo.GetValue(item);
            //                Index++;
            //            }
            //        }
            //        table.Rows.Add(values);
            //    }
            //    //检测是否有继承字段，如果存在，进行赋值
            //    string Discriminator = dc.GetFieldName<K>("Discriminator");
            //    if (!string.IsNullOrEmpty(Discriminator))
            //    {
            //        bulkCopy.ColumnMappings.Add("Discriminator", "Discriminator");
            //        table.Columns.Add("Discriminator", typeof(string));
            //        for (int i = 0; i < table.Rows.Count; i++)
            //        {
            //            table.Rows[i]["Discriminator"] = typeof(K).Name;
            //        }
            //    }
            //    bulkCopy.WriteToServer(table);
            //}
        }
        #endregion

        #region 验证是否空行
        /// <summary>
        /// 验证Excel中某行是否为空行
        /// </summary>
        /// <param name="row">行数</param>
        /// <param name="colCount">列数</param>
        /// <returns>True代表空行，False代表非空行</returns>
        private bool IsEmptyRow(XSSFRow row, int colCount)
        {
            bool result = true;
            for (int i = 0; i < colCount; i++)
            {
                string? value = row.GetCell(i, MissingCellPolicy.CREATE_NULL_AS_BLANK).ToString();
                if (!string.IsNullOrEmpty(value))
                {
                    result = false;
                    break;
                }
            }
            return result;
        }
        #endregion

        #region 复制Excel属性
        /// <summary>
        /// 复制Excel属性
        /// </summary>
        /// <param name="excelPropety">单元格属性</param>
        /// <returns>复制后的单元格</returns>
        private ExcelPropety CopyExcelPropety(ExcelPropety excelPropety)
        {
            ExcelPropety ep = new ExcelPropety
            {
                BackgroudColor = excelPropety.BackgroudColor,
                ColumnName = excelPropety.ColumnName,
                DataType = excelPropety.DataType,
                ResourceType = excelPropety.ResourceType,
                IsNullAble = excelPropety.IsNullAble,
                ListItems = excelPropety.ListItems,
                MaxValuseOrLength = excelPropety.MaxValuseOrLength,
                MinValueOrLength = excelPropety.MinValueOrLength,
                Value = excelPropety.Value,
                SubTableType = excelPropety.SubTableType,
                CharCount = excelPropety.CharCount,
                ReadOnly = excelPropety.ReadOnly,
                FormatData = excelPropety.FormatData,
                FormatSingleData = excelPropety.FormatSingleData,
                FieldName = excelPropety.FieldName
            };
            List<ExcelPropety> li = [];
            foreach (var item in excelPropety.DynamicColumns)
            {
                li.Add(CopyExcelPropety(item));
            }
            ep.DynamicColumns = li;
            return ep;
        }
        #endregion

        #region 设置异常信息
        /// <summary>
        /// 设置错误信息
        /// </summary>
        /// <param name="e">异常</param>
        /// <param name="id">数据Id</param>
        protected void SetExceptionMessage(Exception e, long? id)
        {
            //检查是否为数据库操作错误
            if (e is DbUpdateException)
            {
                var de = (DbUpdateException)e;
                if (de.Entries != null)
                {
                    if (de.Entries.Count == 0)
                    {
                        ErrorListVM.EntityList.Add(new ErrorMessage { Index = 0, Message = e.Message + e.InnerException?.Message });
                    }
                    //循环此错误相关的数据
                    foreach (var ent in de.Entries)
                    {
                        //获取错误数据Id
                        var errorId = (long)((ent.Entity as TopBasePoco)!.ExcelIndex);
                        //根据State判断修改或删除操作，输出不同的错误信息
                        if (ent.State == EntityState.Deleted)
                        {
                            ErrorListVM.EntityList.Add(new ErrorMessage { Index = errorId, Message = (CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.DataCannotDelete"] : null) });
                        }
                        else if (ent.State == EntityState.Modified)
                        {
                            ErrorListVM.EntityList.Add(new ErrorMessage { Index = errorId, Message = (CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.EditFailed"] : null) });
                        }
                        else
                        {
                            ErrorListVM.EntityList.Add(new ErrorMessage { Index = errorId, Message = de.Message });
                        }
                    }
                }
            }
            //对于其他类型的错误，直接添加错误信息
            else
            {
                if (id != null)
                {
                    ErrorListVM.EntityList.Add(new ErrorMessage { Index = id.Value, Message = e.Message });
                }
                else
                {
                    ErrorListVM.EntityList.Add(new ErrorMessage { Index = 0, Message = e.Message });
                }
            }
        }
        #endregion

        #region 验证数据重复

        /// <summary>
        /// 判断数据是否在库中存在重复数据
        /// </summary>
        /// <param name="Entity">要验证的数据</param>
        /// <param name="checkCondition">验证表达式</param>
        /// <returns>null代表没有重复</returns>
        protected P? IsDuplicateData(P Entity, DuplicatedInfo<P>? checkCondition)
        {
            //获取设定的重复字段信息
            if (checkCondition != null && checkCondition.Groups.Count > 0)
            {
                //生成基础Query
                var baseExp = DC!.Set<P>().AsQueryable();
                var modelType = typeof(P);
                ParameterExpression para = Expression.Parameter(modelType, "tm");
                //循环所有重复字段组
                foreach (var group in checkCondition.Groups)
                {
                    List<Expression> conditions = [];
                    //生成一个表达式，类似于 x=>x.Id != id，这是为了当修改数据时验证重复性的时候，排除当前正在修改的数据
                    //在每个组中循环所有字段
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
                    if (typeof(ITenant).IsAssignableFrom(modelType) && props.Any(x => x.Name.ToLower() == "tenantcode") == false && Wtm?.ConfigInfo?.EnableTenant == true && group.UseTenant == true)
                    {
                        ITenant? ent = Entity as ITenant;
                        if (ent != null) ent.TenantCode = LoginUserInfo?.CurrentTenant;
                        var f = new DuplicatedField<P>(x => (x as ITenant)!.TenantCode!);
                        Expression? exp = f.GetExpression(Entity, para);
                        if (exp != null) conditions.Add(exp);
                    }
                    if (conditions.Count > 0)
                    {
                        //循环添加条件并生成Where语句
                        Expression whereCallExpression = baseExp.Expression;
                        for (int i = 0; i < conditions.Count; i++)
                        {
                            whereCallExpression = Expression.Call(
                                 typeof(Queryable),
                                 "Where",
                                 new Type[] { modelType },
                                 whereCallExpression,
                                 Expression.Lambda<Func<P, bool>>(conditions[i], new ParameterExpression[] { para }));
                        }
                        var result = baseExp.Provider.CreateQuery(whereCallExpression);


                        foreach (var res in result)
                        {
                            if (IsOverWriteExistData == false)
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
                                    ErrorListVM.EntityList.Add(new ErrorMessage { Message = (CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.DuplicateError", AllName] : null), Index = Entity.ExcelIndex });
                                }
                                //如果多个字段重复，则拼接形成 xx，yy，zz组合字段重复 这种提示
                                else if (props.Count > 1)
                                {
                                    ErrorListVM.EntityList.Add(new ErrorMessage { Message = (CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.DuplicateGroupError", AllName] : null), Index = Entity.ExcelIndex });
                                }

                            }
                            return res as P;
                        }
                    }
                }
            }
            return null;
        }
        #endregion

        protected DuplicatedInfo<P> CreateFieldsInfo(params DuplicatedField<P>[] FieldExps)
        {
            DuplicatedInfo<P> d = new DuplicatedInfo<P>();
            d.AddGroup(FieldExps);
            return d;
        }

        /// <summary>
        /// 创建一个简单重复数据信息
        /// </summary>
        /// <param name="FieldExp">重复数据的字段</param>
        /// <returns>重复数据信息</returns>
        public static DuplicatedField<P> SimpleField(Expression<Func<P, object>> FieldExp)
        {
            return new DuplicatedField<P>(FieldExp);
        }

        /// <summary>
        /// 创建一个关联到其他表数组中数据的重复信息
        /// </summary>
        /// <typeparam name="V">关联表类</typeparam>
        /// <param name="MiddleExp">指向关联表类数组的Lambda</param>
        /// <param name="FieldExps">指向最终字段的Lambda</param>
        /// <returns>重复数据信息</returns>
        public static DuplicatedField<P> SubField<V>(Expression<Func<P, List<V>>> MiddleExp, params Expression<Func<V, object>>[] FieldExps)
        {
            return new ComplexDuplicatedField<P, V>(MiddleExp, FieldExps);
        }

        public ErrorObj GetErrorJson()
        {
            var mse = new ErrorObj();
            mse.Form = new Dictionary<string, string>();
            var err = ErrorListVM?.EntityList?.Where(x => x.Index == 0).FirstOrDefault()?.Message;
            if (string.IsNullOrEmpty(err))
            {
                Models.IWtmFile? fa = null;
                if(Wtm!.ServiceProvider == null) {
                    return mse;
                }
                var fp = Wtm!.ServiceProvider.GetRequiredService<WtmFileProvider>();
                fa = fp.GetFile(UploadFileId!, true, DC!);
                // Use a local workbook so GetErrorJson does not overwrite (and leak) the
                // field-level xssfworkbook that SetTemplateData already opened. (#150 L3)
                using var localWb = new XSSFWorkbook(fa!.DataStream);
                fa!.DataStream?.Dispose();
                List<FieldInfo> propetys = [.. Template.GetType().GetFields().Where(x => x.FieldType == typeof(ExcelPropety))];
                List<ExcelPropety> excelPropetys = [];
                for (int porpetyIndex = 0; porpetyIndex < propetys.Count; porpetyIndex++)
                {
                    ExcelPropety ep = (ExcelPropety)propetys[porpetyIndex].GetValue(Template)!;
                    excelPropetys.Add(ep);
                }
                int columnCount = excelPropetys.Count;
                //int excelPropetyCount = excelPropetys.Count;
                var dynamicColumn = excelPropetys.Where(x => x.DataType == ColumnDataType.Dynamic).FirstOrDefault();
                if (dynamicColumn != null)
                {
                    columnCount = columnCount + dynamicColumn.DynamicColumns.Count - 1;
                }
                ISheet sheet = localWb.GetSheetAt(0);
                var errorStyle = localWb.CreateCellStyle();
                IFont f = localWb.CreateFont();
                f.Color = HSSFColor.Red.Index;
                errorStyle.SetFont(f);
                errorStyle.IsLocked = true;
                foreach (var e in ErrorListVM!.EntityList)
                {
                    if (e.Index > 0)
                    {
                        var c = sheet.GetRow((int)(e.Index - 1)).CreateCell(columnCount);
                        c.CellStyle = errorStyle;
                        c.SetCellValue(e.Message ?? string.Empty);
                    }
                }
                MemoryStream ms = new MemoryStream();
                localWb.Write(ms);
                ms.Position = 0;

                var newfile = fp.Upload("Error-" + fa!.FileName, ms.Length, ms);
                ms.Close();
                ms.Dispose();
                err = CoreProgram._localizer != null ? (string?)CoreProgram._localizer["Sys.ImportError"] : null;
                mse.Form.Add("Entity.Import", err ?? string.Empty);
                mse.Form.Add("Entity.ErrorFileId", newfile?.GetID() ?? "");
            }
            else
            {
                mse.Form.Add("Entity.Import", err ?? string.Empty);
            }
            return mse;
        }
    }

    #region 辅助类
    public class ErrorMessage : TopBasePoco
    {
        [Display(Name = "Sys.RowIndex")]
        public long Index { get; set; }

        [Display(Name = "Sys.CellIndex")]
        public long Cell { get; set; }
        [Display(Name = "Sys.ErrorMsg")]
        public string? Message { get; set; }
    }

    /// <summary>
    /// 错误数据列表
    /// </summary>
    public class TemplateErrorListVM : BasePagedListVM<ErrorMessage, BaseSearcher>
    {

        public TemplateErrorListVM()
        {
            EntityList = [];
            NeedPage = false;
        }

        protected override IEnumerable<IGridColumn<ErrorMessage>> InitGridHeader()
        {
            return new List<GridColumn<ErrorMessage>>{
                this.MakeGridHeader(x => x.Index, 60),
                this.MakeGridHeader(x => x.Message!)
            };
        }

        public override IOrderedQueryable<ErrorMessage> GetSearchQuery()
        {
            return EntityList!.AsQueryable().OrderBy(x => x.Index);
        }
    }

    #endregion

}
