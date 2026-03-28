#nullable enable
using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace WalkingTec.Mvvm.Core.Services
{
    /// <summary>
    /// Encapsulates ViewModel creation and initialization logic previously in WTMContext.CreateVM.
    /// VMs fundamentally need WTMContext, so it is passed as a parameter.
    /// The value of this extraction is testability and separation of the complex
    /// reflection-based VM wiring from WTMContext itself.
    /// </summary>
    public interface IWtmVmFactory
    {
        /// <summary>Create a ViewModel by type, optionally loading an entity by Id.</summary>
        BaseVM CreateVM(WTMContext wtm, Type? vmType, object? id = null, object[]? ids = null,
            Dictionary<string, object>? values = null, bool passInit = false);

        /// <summary>Create a typed ViewModel with optional property expressions.</summary>
        T CreateVM<T>(WTMContext wtm, Expression<Func<T, object>>? values = null,
            bool passInit = false) where T : BaseVM;

        /// <summary>Create a typed ViewModel and load entity by Id.</summary>
        T CreateVM<T>(WTMContext wtm, object id, Expression<Func<T, object>>? values = null,
            bool passInit = false) where T : BaseVM;

        /// <summary>Create a typed BatchVM with given Ids.</summary>
        T CreateVM<T>(WTMContext wtm, object[] ids, Expression<Func<T, object>>? values = null,
            bool passInit = false) where T : BaseVM;

        /// <summary>Create a ViewModel by fully-qualified type name.</summary>
        BaseVM CreateVM(WTMContext wtm, string? vmFullName, object? id = null,
            object[]? ids = null, bool passInit = false);
    }
}
