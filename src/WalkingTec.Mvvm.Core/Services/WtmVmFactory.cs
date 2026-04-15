#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core.Services
{
    /// <summary>
    /// Default implementation of <see cref="IWtmVmFactory"/>.
    /// Logic copied from WTMContext.CreateVM and SetSubVm (WTMContext.cs lines 1127-1414).
    /// </summary>
    public class WtmVmFactory : IWtmVmFactory
    {
        public BaseVM CreateVM(WTMContext wtm, Type? vmType, object? id = null,
            object[]? ids = null, Dictionary<string, object>? values = null,
            bool passInit = false)
        {
            var ctor = vmType?.GetConstructor(Type.EmptyTypes);
            BaseVM rv = ctor?.Invoke(null) as BaseVM
                ?? throw new InvalidOperationException($"Cannot create instance of {vmType?.FullName}. Ensure it has a parameterless constructor.");
            rv.Wtm = wtm;
            rv.FC = new Dictionary<string, object>();
            rv.CreatorAssembly = wtm.GetType().AssemblyQualifiedName;
            rv.ControllerName = wtm.HttpContext?.Request?.Path;

            PopulateFormCollection(rv, wtm.HttpContext);

            if (values != null)
            {
                foreach (var v in values)
                {
                    PropertyHelper.SetPropertyValue(rv, v.Key, v.Value, null, false);
                }
            }

            if (id != null && rv is IBaseCRUDVM<TopBasePoco> cvm)
            {
                cvm.SetEntityById(id);
            }

            SetSubVm(rv, passInit);

            if (rv is IBaseBatchVM<BaseVM> temp)
            {
                InitBatchVM(temp, rv, ids, passInit);
            }

            if (rv is IBasePagedListVM<TopBasePoco, ISearcher> lvm)
            {
                var searcher = lvm.Searcher;
                searcher.CopyContext(rv);
                if (!passInit) searcher.DoInit();
                lvm.DoInitListVM();
            }

            if (rv is IBaseImport<BaseTemplateVM> tvm)
            {
                tvm.Template.CopyContext(rv);
                tvm.Template.DoInit();
                tvm.ErrorListVM.CopyContext(rv);
            }

            if (!passInit)
            {
                rv.DoInit();
            }

            return rv;
        }

        public T CreateVM<T>(WTMContext wtm, Expression<Func<T, object>>? values = null,
            bool passInit = false) where T : BaseVM
        {
            var dir = new SetValuesParser().Parse(values);
            return (T)CreateVM(wtm, typeof(T), null, Array.Empty<object>(), dir, passInit);
        }

        public T CreateVM<T>(WTMContext wtm, object id,
            Expression<Func<T, object>>? values = null,
            bool passInit = false) where T : BaseVM
        {
            var dir = new SetValuesParser().Parse(values);
            return (T)CreateVM(wtm, typeof(T), id, Array.Empty<object>(), dir, passInit);
        }

        public T CreateVM<T>(WTMContext wtm, object[] ids,
            Expression<Func<T, object>>? values = null,
            bool passInit = false) where T : BaseVM
        {
            var dir = new SetValuesParser().Parse(values);
            return (T)CreateVM(wtm, typeof(T), null, ids, dir, passInit);
        }

        public BaseVM CreateVM(WTMContext wtm, string? vmFullName, object? id = null,
            object[]? ids = null, bool passInit = false)
        {
            return CreateVM(wtm, Type.GetType(vmFullName ?? ""), id, ids, null, passInit);
        }

        #region Private helpers

        private static void PopulateFormCollection(BaseVM rv, HttpContext? httpContext)
        {
            if (httpContext?.Request == null) return;
            try
            {
                if (httpContext.Request.QueryString != QueryString.Empty)
                {
                    foreach (var key in httpContext.Request.Query.Keys)
                    {
                        if (!rv.FC.ContainsKey(key))
                        {
                            rv.FC.Add(key, httpContext.Request.Query[key]);
                        }
                    }
                }
                if (httpContext.Request.HasFormContentType)
                {
                    var f = httpContext.Request.Form;
                    foreach (var key in f.Keys)
                    {
                        if (!rv.FC.ContainsKey(key))
                        {
                            rv.FC.Add(key, f[key]);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                httpContext.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger("WtmVmFactory")?.LogDebug(ex, "PopulateFormCollection: reading query/form for ViewModel '{VmType}' failed", rv.GetType().Name);
            }
        }

        private static void InitBatchVM(IBaseBatchVM<BaseVM> temp, BaseVM rv, object[]? ids, bool passInit)
        {
            temp.Ids = [];
            if (ids != null)
            {
                temp.Ids = [.. ids.Select(iid => iid?.ToString() ?? "")];
            }
            if (temp.ListVM != null)
            {
                temp.ListVM.CopyContext(rv);
                temp.ListVM.Ids = ids == null ? [] : [.. temp.Ids];
                temp.ListVM.SearcherMode = ListVMSearchModeEnum.Batch;
                temp.ListVM.NeedPage = false;
            }
            if (temp.LinkedVM != null)
            {
                temp.LinkedVM.CopyContext(rv);
            }
            if (temp.ListVM != null)
            {
                temp.ListVM.OnAfterInitList += (self) =>
                {
                    self.RemoveActionColumn();
                    self.RemoveAction();
                    if (temp.ErrorMessage.Count > 0)
                    {
                        self.AddErrorColumn();
                    }
                };
                temp.ListVM.DoInitListVM();
                if (temp.ListVM.Searcher != null)
                {
                    var searcher = temp.ListVM.Searcher;
                    searcher.CopyContext(rv);
                    if (!passInit) searcher.DoInit();
                }
            }
            temp.LinkedVM?.DoInit();
        }

        private void SetSubVm(BaseVM? vm, bool passInit)
        {
            var sub = vm?.GetType()?.GetAllProperties()
                .Where(x => typeof(BaseVM).IsAssignableFrom(x.PropertyType) && x.Name != "ParentVM");
            if (sub == null) return;

            foreach (var prop in sub)
            {
                var subins = prop.GetValue(vm) as BaseVM;
                bool exist = subins != null;
                if (subins == null)
                {
                    subins = prop.PropertyType?.GetConstructor(Type.EmptyTypes)?.Invoke(null) as BaseVM;
                }
                if (subins != null)
                {
                    subins.CopyContext(vm);
                    subins.ParentVM = vm;
                    subins.PropertyNameInParent = prop.Name;
                    if (!passInit) subins.DoInit();
                    if (!exist) vm!.SetPropertyValue(prop.Name, subins);
                    SetSubVm(subins, passInit);
                }
            }
        }

        #endregion
    }
}
