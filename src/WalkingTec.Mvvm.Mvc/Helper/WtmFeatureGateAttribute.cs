#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using WalkingTec.Mvvm.Core.FeatureFlags;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Gates a controller class or action method behind a feature flag.
    /// When the flag is disabled, the action short-circuits with
    /// <see cref="FallbackStatusCode"/> (default <c>404</c> — the
    /// "feature doesn't exist for you" semantics commonly used on
    /// pre-release flags) without invoking the action body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Usage:
    /// <code>
    /// [WtmFeatureGate("new-checkout")]
    /// [HttpPost("/api/v2/orders")]
    /// public IActionResult PlaceOrderV2() { ... }
    /// </code>
    /// </para>
    /// <para>
    /// Requires <c>services.AddWtmFeatureFlags()</c> to have been called
    /// during startup; when the service is not registered, the attribute
    /// treats the flag as disabled (fail-closed — unknown = off).
    /// </para>
    /// <para>
    /// Returning 404 by default is intentional: it gives attackers /
    /// curious users zero signal that a gated endpoint even exists. Set
    /// <see cref="FallbackStatusCode"/> to 403 / 503 / etc. when the
    /// intended semantic is "feature exists but disabled for you right
    /// now" (e.g. maintenance-window gates).
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class WtmFeatureGateAttribute : Attribute, IAsyncActionFilter
    {
        /// <summary>
        /// Name of the flag consulted via
        /// <see cref="IWtmFeatureFlags.IsEnabled(string)"/>.
        /// </summary>
        public string FlagName { get; }

        /// <summary>
        /// HTTP status code returned when the flag is disabled.
        /// Default <see cref="StatusCodes.Status404NotFound"/>.
        /// </summary>
        public int FallbackStatusCode { get; set; } = StatusCodes.Status404NotFound;

        public WtmFeatureGateAttribute(string flagName)
        {
            if (string.IsNullOrWhiteSpace(flagName))
            {
                throw new ArgumentException(
                    "[WtmFeatureGate] FlagName must not be null or whitespace.",
                    nameof(flagName));
            }
            FlagName = flagName;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(next);

            var flags = context.HttpContext.RequestServices.GetService<IWtmFeatureFlags>();
            if (flags == null || !flags.IsEnabled(FlagName))
            {
                context.Result = new StatusCodeResult(FallbackStatusCode);
                return;
            }

            await next();
        }
    }
}
