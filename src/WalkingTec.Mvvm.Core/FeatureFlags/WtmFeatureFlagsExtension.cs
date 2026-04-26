#nullable enable
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace WalkingTec.Mvvm.Core.FeatureFlags
{
    /// <summary>
    /// Service-collection extensions that register the WTM feature-flag
    /// service. Opt-in — apps must explicitly call one of the overloads.
    /// </summary>
    public static class WtmFeatureFlagsExtension
    {
        /// <summary>
        /// Registers <see cref="IWtmFeatureFlags"/> backed by the
        /// configuration section <c>"FeatureFlags"</c> with no static
        /// defaults and no dynamic resolver. Flags not declared in
        /// configuration resolve to <c>false</c>.
        /// </summary>
        public static IServiceCollection AddWtmFeatureFlags(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);
            services.AddOptions<WtmFeatureFlagsOptions>();
            RegisterCoreServices(services);
            return services;
        }

        /// <summary>
        /// Registers <see cref="IWtmFeatureFlags"/>, applying
        /// <paramref name="configure"/> to the options instance. Use this
        /// overload to seed static defaults, change the configuration
        /// section, or attach a dynamic resolver.
        /// </summary>
        public static IServiceCollection AddWtmFeatureFlags(
            this IServiceCollection services,
            Action<WtmFeatureFlagsOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);
            services.AddOptions<WtmFeatureFlagsOptions>().Configure(configure);
            RegisterCoreServices(services);
            return services;
        }

        private static void RegisterCoreServices(IServiceCollection services)
        {
            // TryAdd so repeated calls are idempotent — if an app wires
            // AddWtmFeatureFlags() twice (once by an extension, once by
            // the host), the second registration shouldn't duplicate.
            services.TryAddSingleton<IWtmFeatureFlags, WtmFeatureFlags>();

            // IHttpContextAccessor is commonly present in web apps but
            // background-service-only hosts may not register it. TryAdd
            // ensures we don't override a host-provided registration.
            services.TryAddSingleton<
                Microsoft.AspNetCore.Http.IHttpContextAccessor,
                Microsoft.AspNetCore.Http.HttpContextAccessor>();
        }
    }
}
