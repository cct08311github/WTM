using System;
using System.Threading.Tasks;
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
    /// runs. This class DECORATES whatever <see cref="IControllerActivator"/> is already
    /// registered (see <c>FrameworkServiceExtension.AddWtmContext</c>) and, immediately after
    /// that inner activator constructs a controller, sets its <c>Wtm</c> property — if it
    /// implements <see cref="IBaseController"/> and isn't already set — from the SAME
    /// per-request scoped <see cref="WTMContext"/> instance <c>DataContextFilter</c> would
    /// otherwise have resolved. Those three filters still run afterward and still do their own
    /// work (session/model-state wiring, connection string selection, etc.) —
    /// <c>SetWtmContext()</c>'s <c>if (controller.Wtm == null)</c> check simply becomes a no-op
    /// once this activator has already set it. This fixes the ordering gap for EVERY
    /// controller/assembly (Etl, WorkFlow, Mvc, Demo, and any future third-party assembly)
    /// uniformly, not just the five Etl controllers that happen to expose the symptom today.
    /// </para>
    ///
    /// <para>
    /// <b>#882 review addendum — why this decorates instead of replacing outright</b>: an
    /// earlier version of this fix called <c>services.Replace(ServiceDescriptor.Singleton
    /// &lt;IControllerActivator, WtmControllerActivator&gt;())</c>, unconditionally discarding
    /// whatever was already registered. That is a real compatibility break, confirmed against
    /// the ASP.NET Core 10 source: <c>AddMvcCore()</c> registers
    /// <c>services.TryAddTransient&lt;IControllerActivator, DefaultControllerActivator&gt;()</c>,
    /// and a host that additionally calls <c>.AddControllersAsServices()</c> further replaces
    /// that with <c>ServiceBasedControllerActivator</c>, which resolves each controller
    /// instance — and therefore its DISPOSAL — from the DI container (each controller type was
    /// registered <c>TryAddTransient</c> by that same call) instead of a manual
    /// <c>Dispose()</c> call; its <c>Release</c>/<c>ReleaseAsync</c> are intentionally no-ops
    /// because the owning <see cref="Microsoft.Extensions.DependencyInjection.IServiceScope"/>
    /// already disposes anything it created. A from-scratch reimplementation (the earlier
    /// version's <c>Release()</c> only checked <see cref="IDisposable"/>, never
    /// <see cref="IAsyncDisposable"/>, and never overrode <c>ReleaseAsync</c> at all — relying
    /// on the interface's default-interface-method forwarding to that same incomplete
    /// <c>Release()</c>) would either leak async-only-disposable controllers or, worse,
    /// double-dispose a controller the container also owns. Decorating instead of replacing
    /// means <c>Release</c>/<c>ReleaseAsync</c> simply delegate to whichever inner activator
    /// was already in play, so its disposal contract — whatever it is — is preserved exactly,
    /// and <c>.AddControllersAsServices()</c> keeps working. <b><c>DefaultControllerActivator</c>
    /// is an <c>internal sealed</c> type (verified via <c>ilspycmd</c> against the real installed
    /// 10.0.10 assembly — corrected here from an earlier, wrong version of this comment that also
    /// called <c>ServiceBasedControllerActivator</c> <c>internal sealed</c>: it is actually
    /// <c>public class</c>, not sealed, not internal; the design is unaffected either way, since
    /// this class never needs to name either type). The review's suggested alternative,
    /// implementing <see cref="Microsoft.AspNetCore.Mvc.Controllers.IControllerPropertyActivator"/>
    /// instead, is not viable: that interface is `internal` (also confirmed via <c>ilspycmd</c>)
    /// and is not resolvable or implementable from outside <c>Microsoft.AspNetCore.Mvc.Core</c>.
    /// Wrapping the existing <see cref="IControllerActivator"/> registration is the only
    /// extension point this assembly can actually reach.</b>
    /// </para>
    ///
    /// <para>
    /// <b>#882 review, second round — the inner activator's own disposal ownership.</b> The
    /// decorator paragraph above covers the CONTROLLER's disposal (delegated to <c>_inner</c>
    /// unchanged); it does not by itself cover disposal of the <c>_inner</c> ACTIVATOR INSTANCE.
    /// <c>FrameworkServiceExtension.AddWtmContext</c> builds that instance itself — by invoking
    /// the captured original descriptor's <c>ImplementationFactory</c>, or via
    /// <c>ActivatorUtilities.CreateInstance</c> against its <c>ImplementationType</c> — bypassing
    /// the DI container's own creation path, which is what normally enrolls a newly-created
    /// disposable instance into the owning scope's disposables list. A third-party, disposable
    /// <c>IControllerActivator</c> built that way would previously have been disposed by the
    /// container and now silently would not be, since only the OUTER <c>WtmControllerActivator</c>
    /// gets returned to (and tracked by) the container, and this class implemented neither
    /// <see cref="IDisposable"/> nor <see cref="IAsyncDisposable"/> at all. Neither built-in
    /// activator is itself disposable, which is why the HTTP test suite never caught this.
    /// Fixed: this class now implements both, and the <c>ownsInner</c> constructor flag —
    /// <c>true</c> when <c>AddWtmContext</c> built <c>_inner</c> itself (the
    /// <c>ImplementationFactory</c>/<c>ImplementationType</c> cases), <c>false</c> when
    /// <c>_inner</c> is a pre-built, potentially-shared <c>ImplementationInstance</c> the
    /// container was never going to dispose on its own either — disposing THAT one here, on
    /// every per-resolution wrapper's teardown, would tear down a singleton the next request
    /// still needs — decides whether disposing this wrapper also disposes <c>_inner</c>.
    /// </para>
    ///
    /// <para>
    /// <b>On the construction-time <see cref="WTMContext"/> resolution possibly throwing</b>:
    /// <c>GetRequiredService&lt;WTMContext&gt;()</c> in <see cref="Create"/> can throw if
    /// <c>WTMContext</c> or one of its own dependencies is missing from DI — and, same as the
    /// <c>SetWtmContext</c> call site it replaces, nothing here catches that. This was raised in
    /// the #882 review as a risk that construction now happens "before all
    /// <c>IBaseController</c> filters", so a would-have-been-handled short-circuit might instead
    /// hit this exception first. Verified against the ASP.NET Core 10 source
    /// (<c>ControllerActionInvoker</c>'s <c>State.ActionBegin</c> case) that this does **not**
    /// hold: controller construction happens strictly AFTER the Authorization-filter and
    /// Resource-filter stages, in the same relative position action filters (including the
    /// controller's own <c>OnActionExecuting</c>) already occupied — so any
    /// <c>[Authorize]</c>/resource-filter short-circuit still runs, and still wins, exactly as
    /// before. The only ordering that changed is AMONG action filters, which is the #876 fix
    /// itself. For the five ETL controllers this replaces, that resolution call was previously
    /// unreachable — the very bug being fixed made their own <c>OnActionExecuting</c> short-
    /// circuit with <c>Forbid()</c> before <c>DataContextFilter</c> (the only place
    /// <c>SetWtmContext</c>/this resolution ran) ever got a turn — so this restores the same
    /// per-request DI resolution every other controller in the app already performed on every
    /// request; it does not add a new failure mode to the application as a whole. Deliberately
    /// NOT adding a try/catch here: doing so would silently convert a broken DI registration
    /// (a startup misconfiguration, not a per-request condition) into an unpredictable
    /// null-<c>Wtm</c> state instead of a fail-fast 500, which is a worse outcome.
    /// </para>
    /// </summary>
    public class WtmControllerActivator : IControllerActivator, IDisposable, IAsyncDisposable
    {
        private readonly IControllerActivator _inner;
        private readonly bool _ownsInner;

        /// <param name="inner">The <see cref="IControllerActivator"/> this instance decorates.</param>
        /// <param name="ownsInner">
        /// Whether disposing this wrapper should also dispose <paramref name="inner"/>. Pass
        /// <see langword="true"/> when the caller constructed <paramref name="inner"/> itself
        /// outside the DI container's own tracked creation path (so nothing else will dispose
        /// it); pass <see langword="false"/> when <paramref name="inner"/> is a pre-built,
        /// possibly-shared instance (e.g. a DI <c>ImplementationInstance</c> registration) whose
        /// lifetime this wrapper does not own. Defaults to <see langword="true"/>, matching the
        /// common case of wrapping a freshly-constructed inner activator.
        /// </param>
        public WtmControllerActivator(IControllerActivator inner, bool ownsInner = true)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _ownsInner = ownsInner;
        }

        public object Create(ControllerContext context)
        {
            var controller = _inner.Create(context);
            if (controller is IBaseController baseController && baseController.Wtm == null)
            {
                baseController.Wtm = context.HttpContext.RequestServices.GetRequiredService<WTMContext>();
            }
            return controller;
        }

        public void Release(ControllerContext context, object controller)
        {
            _inner.Release(context, controller);
        }

        public ValueTask ReleaseAsync(ControllerContext context, object controller)
        {
            return _inner.ReleaseAsync(context, controller);
        }

        /// <summary>
        /// Disposes <c>_inner</c> when (and only when) this wrapper owns its lifetime — see the
        /// <c>ownsInner</c> constructor parameter and the #882 review addendum above. This is
        /// about the ACTIVATOR instance, not the controllers it creates (those are released via
        /// <see cref="Release"/>/<see cref="ReleaseAsync"/>, delegated to <c>_inner</c> exactly
        /// as before, unaffected by this).
        /// </summary>
        public void Dispose()
        {
            if (_ownsInner && _inner is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        /// <inheritdoc cref="Dispose"/>
        public async ValueTask DisposeAsync()
        {
            if (!_ownsInner)
            {
                return;
            }

            if (_inner is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
                return;
            }

            if (_inner is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
