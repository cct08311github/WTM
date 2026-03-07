#nullable enable
using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// GridActionExtension
    /// </summary>
    public static class GridActionExtensions
    {
        #region MakeStandardAction  创建标准动作

        /// <summary>
        /// 创建标准动作
        /// </summary>
        /// <typeparam name="T">继承自TopBasePoco的类</typeparam>
        /// <typeparam name="V">继承自ISearcher的类</typeparam>
        /// <param name="self">self</param>
        /// <param name="controllerName">动作的Controller</param>
        /// <param name="standardType">标准动作类型</param>
        /// <param name="dialogTitle">弹出窗口的标题，可为空，代表使用默认文字</param>
        /// <param name="areaName">域名</param>
        /// <param name="dialogWidth">弹出窗口的宽度</param>
        /// <param name="dialogHeight">弹出窗口的高度</param>
        /// <param name="name">按钮显示的文字</param>
        /// <param name="buttonId">Button的id  默认自动生成</param>
        /// <param name="whereStr">whereStr</param>
        /// <returns>列表动作</returns>
        /// <remarks>
        /// 根据标准动作类型，创建默认属性的标准动作
        /// </remarks>
        public static GridAction MakeStandardAction<T, V>(this IBasePagedListVM<T, V> self
            , string controllerName
            , GridActionStandardTypesEnum standardType
            , string dialogTitle
            , string? areaName = null
            , int? dialogWidth = null
            , int? dialogHeight = null
            , string? name = null
            , string? buttonId = null
            , params Expression<Func<T, object>>[] whereStr)
            where T : TopBasePoco
            where V : ISearcher
        {
            var iconcls = string.Empty;
            var actionName = standardType.ToString();
            var gridname = string.Empty;
            var paraType = GridActionParameterTypesEnum.NoId;
            var showInRow = false;
            var hideOnToolBar = false;
            var showDialog = true;
            var isexport = false;
            string? msg = null;
            var ispost = false;
            string? qs = null;
            var loc = CoreProgram._localizer;
            switch (standardType)
            {
                case GridActionStandardTypesEnum.Approve:
                    iconcls = "layui-icon layui-icon-form";
                    gridname = loc != null ? (string?)loc["Sys.Approve"] ?? string.Empty : string.Empty;
                    paraType = GridActionParameterTypesEnum.SingleId;
                    showInRow = true;
                    hideOnToolBar = true;
                    break;
                case GridActionStandardTypesEnum.Create:
                    iconcls = "layui-icon layui-icon-add-1";
                    gridname = loc != null ? (string?)loc["Sys.Create"] ?? string.Empty : string.Empty;
                    paraType = GridActionParameterTypesEnum.NoId;
                    break;
                case GridActionStandardTypesEnum.AddRow:
                    iconcls = "layui-icon layui-icon-add-1";
                    gridname = loc != null ? (string?)loc["Sys.Create"] ?? string.Empty : string.Empty;
                    paraType = GridActionParameterTypesEnum.AddRow;
                    break;
                case GridActionStandardTypesEnum.Edit:
                    iconcls = "layui-icon layui-icon-edit";
                    gridname = loc != null ? (string?)loc["Sys.Edit"] ?? string.Empty : string.Empty;
                    paraType = GridActionParameterTypesEnum.SingleId;
                    showInRow = true;
                    hideOnToolBar = true;
                    break;
                case GridActionStandardTypesEnum.Delete:
                    iconcls = "layui-icon layui-icon-delete";
                    gridname = loc != null ? (string?)loc["Sys.Delete"] ?? string.Empty : string.Empty;
                    paraType = GridActionParameterTypesEnum.SingleId;
                    showInRow = true;
                    hideOnToolBar = true;
                    break;
                case GridActionStandardTypesEnum.SimpleDelete:
                    iconcls = "layui-icon layui-icon-delete";
                    gridname = loc != null ? (string?)loc["Sys.Delete"] ?? string.Empty : string.Empty;
                    paraType = GridActionParameterTypesEnum.SingleIdWithNull;
                    showInRow = true;
                    hideOnToolBar = true;
                    showDialog = false;
                    actionName = "BatchDelete";
                    qs = "_donotuse_sd=1";
                    ispost = true;
                    msg = loc != null ? (string?)loc["Sys.DeleteConfirm"] : null;
                    break;

                case GridActionStandardTypesEnum.RemoveRow:
                    iconcls = "layui-icon layui-icon-delete";
                    gridname = loc != null ? (string?)loc["Sys.Delete"] ?? string.Empty : string.Empty;
                    paraType = GridActionParameterTypesEnum.RemoveRow;
                    showInRow = true;
                    hideOnToolBar = true;
                    break;
                case GridActionStandardTypesEnum.Details:
                    iconcls = "layui-icon layui-icon-form";
                    gridname = loc != null ? (string?)loc["Sys.Details"] ?? string.Empty : string.Empty;
                    paraType = GridActionParameterTypesEnum.SingleId;
                    showInRow = true;
                    hideOnToolBar = true;
                    break;
                case GridActionStandardTypesEnum.BatchEdit:
                    iconcls = "layui-icon layui-icon-edit";
                    gridname = loc != null ? (string?)loc["Sys.BatchEdit"] ?? string.Empty : string.Empty;
                    paraType = GridActionParameterTypesEnum.MultiIds;
                    break;
                case GridActionStandardTypesEnum.BatchDelete:
                    iconcls = "layui-icon layui-icon-delete";
                    gridname = loc != null ? (string?)loc["Sys.BatchDelete"] ?? string.Empty : string.Empty;
                    paraType = GridActionParameterTypesEnum.MultiIds;
                    break;
                case GridActionStandardTypesEnum.SimpleBatchDelete:
                    iconcls = "layui-icon layui-icon-delete";
                    gridname = loc != null ? (string?)loc["Sys.BatchDelete"] ?? string.Empty : string.Empty;
                    paraType = GridActionParameterTypesEnum.MultiIds;
                    showDialog = false;
                    msg = loc != null ? (string?)loc["Sys.BatchDeleteConfirm"] : null;
                    actionName = "BatchDelete";
                    ispost = true;
                    break;
                case GridActionStandardTypesEnum.Import:
                    iconcls = "layui-icon layui-icon-templeate-1";
                    gridname = loc != null ? (string?)loc["Sys.Import"] ?? string.Empty : string.Empty;
                    paraType = GridActionParameterTypesEnum.NoId;
                    break;
                case GridActionStandardTypesEnum.ExportExcel:
                    iconcls = "layui-icon layui-icon-download-circle";
                    gridname = loc != null ? (string?)loc["Sys.Export"] ?? string.Empty : string.Empty;
                    paraType = GridActionParameterTypesEnum.MultiIdWithNull;
                    name = loc != null ? (string?)loc["Sys.ExportExcel"] : null;
                    showInRow = false;
                    showDialog = false;
                    hideOnToolBar = false;
                    isexport = true;
                   break;
                default:
                    break;
            }

            if (string.IsNullOrEmpty(dialogTitle))
            {
                dialogTitle = gridname;
            }

            var list = new List<string>();
            foreach (var item in whereStr)
            {
                list.Add(PropertyHelper.GetPropertyName(item));
            }

            return new GridAction
            {
                ButtonId = buttonId,
                Name = (name ?? gridname),
                DialogTitle = dialogTitle,
                Area = areaName,
                ControllerName = controllerName,
                ActionName = actionName,
                ParameterType = paraType,
                IconCls = iconcls,
                DialogWidth = dialogWidth ?? 800,
                DialogHeight = dialogHeight,
                ShowInRow = showInRow,
                ShowDialog = showDialog,
                HideOnToolBar = hideOnToolBar,
                PromptMessage = msg,
                ForcePost = ispost,
                QueryString = qs,
                IsExport = isexport,
                whereStr = list.ToArray()
            };
        }

        #endregion

        #region MakeAction 创建按钮
        /// <summary>
        /// 创建标准动作
        /// </summary>
        /// <typeparam name="T">继承自TopBasePoco的类</typeparam>
        /// <typeparam name="V">继承自ISearcher的类</typeparam>
        /// <param name="self">self</param>
        /// <param name="controllerName">动作的Controller</param>
        /// <param name="actionName">actionName</param>
        /// <param name="name">动作名，默认为‘新建’</param>
        /// <param name="dialogTitle">弹出窗口的标题</param>
        /// <param name="paraType">paraType</param>
        /// <param name="areaName">域名</param>
        /// <param name="dialogWidth">弹出窗口的宽度</param>
        /// <param name="dialogHeight">弹出窗口的高度</param>
        /// <param name="buttonId">Button的id  默认自动生成</param>
        /// <param name="whereStr">whereStr</param>
        /// <returns>列表动作</returns>
        /// <remarks>
        /// 根据标准动作类型，创建默认属性的标准动作
        /// </remarks>
        public static GridAction MakeAction<T, V>(this IBasePagedListVM<T, V> self
            , string controllerName
            , string actionName
            , string name
            , string dialogTitle
            , GridActionParameterTypesEnum paraType
            , string? areaName = null
            , int? dialogWidth = null
            , int? dialogHeight = null
            , string? buttonId = null
            , params Expression<Func<T, object>>[] whereStr)
            where T : TopBasePoco
            where V : ISearcher
        {
            var iconcls = string.Empty;

            var list = new List<string>();
            foreach (var item in whereStr)
            {
                list.Add(PropertyHelper.GetPropertyName(item));
            }

            return new GridAction
            {
                ButtonId = buttonId,
                Name = name,
                DialogTitle = dialogTitle,
                Area = areaName,
                ControllerName = controllerName,
                ActionName = actionName,
                ParameterType = paraType, 
                IconCls = iconcls,
                DialogWidth = dialogWidth ?? 800,
                DialogHeight = dialogHeight,
                ShowDialog = true,
                whereStr = list.ToArray()
            };
        }

        public static GridAction MakeActionsGroup<T, V>(this IBasePagedListVM<T, V> self
            , string name
            , List<GridAction> subActions
            , params Expression<Func<T, object>>[] whereStr)
            where T : TopBasePoco
            where V : ISearcher
        {
            var iconcls = string.Empty;

            var list = new List<string>();
            foreach (var item in whereStr)
            {
                list.Add(PropertyHelper.GetPropertyName(item));
            }

            return new GridAction
            {
                ButtonId = Guid.NewGuid().ToString(),
                Name = name,
                DialogTitle = "",
                Area = "",
                ControllerName = "",
                ActionName = "ActionsGroup",
                ParameterType =  GridActionParameterTypesEnum.NoId, 
                IconCls = iconcls,
                DialogWidth = 0,
                DialogHeight = 0,
                ShowDialog = false,
                whereStr = list.ToArray(),
                 SubActions= subActions
            };
        }

        #endregion

        #region MakeStandardExportAction  创建标准导出按钮

        /// <summary>
        /// 创建标准导出按钮
        /// </summary>
        /// <typeparam name="T">继承自TopBasePoco的类</typeparam>
        /// <typeparam name="V">继承自ISearcher的类</typeparam>
        /// <param name="self">self</param>
        /// <param name="gridid">vmGuid</param>
        /// <param name="MustSelect"></param>
        /// <param name="exportType">导出类型  默认null，支持所有导出</param>
        /// <param name="param">参数</param>
        /// <returns></returns>
        [Obsolete("Will be removed in future, use MakeStandardAction with GridActionStandardTypesEnum.ExportExcel instead")]
        public static GridAction MakeStandardExportAction<T, V>(this IBasePagedListVM<T, V> self
            , string? gridid = null
            , bool MustSelect = false
            , ExportEnum? exportType = null
            , params KeyValuePair<string, string>[] param)
            where T : TopBasePoco
            where V : ISearcher
        {
            var loc = CoreProgram._localizer;
            exportType = ExportEnum.Excel;

            var action = new GridAction
            {
                Name = loc != null ? (string?)loc["Sys.ExportExcel"] : null,
                DialogTitle = loc != null ? (string?)loc["Sys.ExportExcel"] : null,
                Area = string.Empty,
                ControllerName = "_Framework",
                ActionName = "GetExportExcel",
                ParameterType = GridActionParameterTypesEnum.MultiIdWithNull,

                IconCls = "layui-icon layui-icon-download-circle",
                ShowInRow = false,
                ShowDialog = false,
                HideOnToolBar = false
            };
            return action;
        }

        #endregion

        #region Set Property

        /// <summary>
        /// Set the dialog to be maximized
        /// </summary>
        /// <param name="self"></param>
        /// <param name="Max"></param>
        /// <returns></returns>
        public static GridAction SetMax(this GridAction self, bool Max = true)
        {
            self.Max = Max;
            return self;
        }


        /// <summary>
        /// Set the dialog to be maximized
        /// </summary>
        /// <param name="self"></param>
        /// <param name="buttonclass">button class.
        /// Some of the layui defined class to control color:
        /// layui-btn-primary
        /// layui-btn-normal
        /// layui-btn-warm
        /// layui-btn-danger
        /// </param>
        /// <returns></returns>
        public static GridAction SetButtonClass(this GridAction self, string buttonclass)
        {
            self.ButtonClass = buttonclass;
            return self;
        }



        /// <summary>
        /// Set the dialog to be maximized
        /// </summary>
        /// <param name="self"></param>
        /// <param name="isDownload"></param>
        /// <returns></returns>
        public static GridAction SetIsDownload(this GridAction self, bool isDownload = true)
        {
            self.IsDownload = isDownload;
            return self;
        }

        public static GridAction SetIsExport(this GridAction self, bool isExport= true)
        {
            self.IsExport = isExport;
            return self;
        }


        /// <summary>
        /// Set prompt message
        /// </summary>
        /// <param name="self"></param>
        /// <param name="msg"></param>
        /// <returns></returns>
        public static GridAction SetPromptMessage(this GridAction self, string msg)
        {
            self.PromptMessage = msg;
            return self;
        }


        /// <summary>
        /// 是否在每行都显示
        /// </summary>
        /// <param name="self"></param>
        /// <param name="showInRow"></param>
        /// <returns></returns>
        public static GridAction SetShowInRow(this GridAction self, bool showInRow = true)
        {
            self.ShowInRow = showInRow;
            return self;
        }
        /// <summary>
        /// 是否在工具栏上隐藏按钮
        /// </summary>
        /// <param name="self"></param>
        /// <param name="hideOnToolBar"></param>
        /// <returns></returns>
        public static GridAction SetHideOnToolBar(this GridAction self, bool hideOnToolBar = true)
        {
            self.HideOnToolBar = hideOnToolBar;
            return self;
        }
        /// <summary>
        /// 把按钮当作容器,添加按钮的子按钮
        /// </summary>
        /// <param name="self"></param>
        /// <param name="subActions">子按钮</param>
        /// <returns></returns>
        public static GridAction SetSubActions(this GridAction self, List<GridAction> subActions)
        {
            self.SubActions = subActions;
            return self;
        }

        /// <summary>
        /// 如果按钮方式是SingleId或SingleIdWithNull，WhereStr里的字段也会被带入到url中传递过去
        /// </summary>
        /// <param name="self"></param>
        /// <param name="str"></param>
        /// <returns></returns>
        public static GridAction SetWhereStr(this GridAction self, params string[] str)
        {
            self.whereStr = str;
            return self;
        }
        #endregion
    }
}
