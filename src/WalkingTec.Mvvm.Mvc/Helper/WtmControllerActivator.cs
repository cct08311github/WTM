using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc.Helper
{
    /// <summary>
    /// Issue #876 root cause: <see cref="WalkingTec.Mvvm.Mvc.Filters.DataContextFilter"/>,
    /// <see cref="WalkingTec.Mvvm.Mvc.Filters.PrivilegeFilter"/> and
    /// <see cref="WalkingTec.Mvvm.Mvc.Filters.FrameworkFilter"/> are the only place
    /// <see cref="IBaseController.Wtm"/> was populated (via
    /// <c>ActionExecutingContextExtension.SetWtmContext</c>), and they are registered as
    /// GLOBAL <c>MvcOptions.Filters</c> — but ASP.NET Core's own
    /// <c>ControllerActionFilter</c> (the internal wrapper that invokes a controller's own
    /// <c>OnActionExecuting</c>/<c>OnActionExecuted</c> overrides when the concrete type
    /// implements <see cref="Microsoft.AspNetCore.Mvc.Filters.IActionFilter"/>, which every
    /// <see cref="Controller"/>-derived class does) is hard-coded by the framework to
    /// <c>Order = int.MinValue</c> — it ALWAYS runs before any custom filter, no matter what
    /// <c>Order</c> that filter is given. Confirmed empirically (see issue #876): a global
    /// filter's <c>Order</c> can never beat it.
    ///
    /// <para>
    /// This is invisible for every controller elsewhere in WTM because none of them read
    /// <c>Wtm</c> from inside their own <c>OnActionExecuting</c> override — WTM's convention is
    /// declarative, URL-based authorization via <c>PrivilegeFilter</c>
    /// (<c>[AllRights]</c>/menu privileges). The five <c>WalkingTec.Mvvm.Etl</c> controllers
    /// (<c>_EtlJobController</c>, <c>_EtlDashboardController</c>, and the three #841 added —
    /// <c>_EtlRunLogController</c>/<c>_EtlMonitorController</c>/<c>_EtlSchemaController</c>) are
    /// the only controllers in the codebase that override <c>OnActionExecuting</c> and read
    /// <c>Wtm</c> directly to implement a role gate — so they are the only ones exposed to this
    /// ordering gap: <c>Wtm</c> was always null at the point their gate read it, the
    /// null-conditional operators made the role check silently evaluate to "no roles", and
    /// <c>context.Result = Forbid()</c> short-circuited the rest of the pipeline before
    /// <c>DataContextFilter</c> et al. ever got a turn — denying every caller, including a
    /// genuine Admin, with a clean 403/redirect (not a <see cref="NullReferenceException"/>,
    /// since the gate itself never dereferences a null <c>Wtm</c>).
    /// </para>
    ///
    /// <para>
    /// <b>Fix</b>: since no custom filter can ever run before the framework's own
    /// <c>ControllerActionFilter</c>, <c>Wtm</c> must be populated even earlier — at controller
    /// CONSTRUCTION time, which happens before any filter (including the hard-coded-first one)
    /// runs. This class replaces the default <see cref="IControllerActivator"/> (the same
    /// <c>ActivatorUtilities.CreateInstance</c>-based construction ASP.NET Core's own
    /// <c>DefaultControllerActivator</c> performs — no change to constructor-injection
    /// semantics) and, immediately after constructing an <see cref="IBaseController"/>, sets
    /// its <c>Wtm</c> property from the SAME per-request scoped <see cref="WTMContext"/>
    /// instance <c>DataContextFilter</c> would otherwise have resolved. Those three filters
    /// still run afterward and still do their own work (session/model-state wiring, connection
    /// string selection, etc.) — <c>SetWtmContext()</c>'s <c>if (controller.Wtm == null)</c>
    /// check simply becomes a no-op once this activator has already set it. This fixes the
    /// ordering gap for EVERY controller/assembly (Etl, WorkFlow, Mvc, Demo, and any future
    /// third-party assembly) uniformly, not just the five Etl controllers that happen to expose
    /// the symptom today.
    /// </para>
    /// </summary>
    public class WtmControllerActivator : IControllerActivator
    {
        public object Create(ControllerContext context)
        {
            var controllerType = context.ActionDescriptor.ControllerTypeInfo.AsType();
            var controller = ActivatorUtilities.CreateInstance(context.HttpContext.RequestServices, controllerType);
            if (controller is IBaseController baseController && baseController.Wtm == null)
            {
                baseController.Wtm = context.HttpContext.RequestServices.GetRequiredService<WTMContext>();
            }
            return controller;
        }

        public void Release(ControllerContext context, object controller)
        {
            if (controller is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
