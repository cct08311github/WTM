#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;

namespace WalkingTec.Mvvm.Core.FeatureFlags
{
    /// <summary>
    /// Configuration for <see cref="IWtmFeatureFlags"/>. Controls which
    /// configuration section is consulted, the static defaults used as
    /// a fallback, and an optional dynamic resolver for per-request
    /// overrides (tenant, user, canary, etc.).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolution order is deterministic and testable:
    /// </para>
    /// <list type="number">
    /// <item><see cref="Resolver"/> — if supplied and returns non-null.</item>
    /// <item>Configuration binding at <c>{ConfigurationSection}:{flagName}</c>.</item>
    /// <item><see cref="Defaults"/> static dictionary.</item>
    /// <item><c>false</c> (fail-closed — unknown flags are off).</item>
    /// </list>
    /// <para>
    /// Configuration-bound values are read on every call so
    /// <c>IConfigurationRoot.Reload()</c> / <c>appsettings.json</c>
    /// watcher changes propagate without an app restart — a deliberate
    /// choice so ops can toggle a flag in production by updating the
    /// file / config store.
    /// </para>
    /// </remarks>
    public class WtmFeatureFlagsOptions
    {
        /// <summary>
        /// Root <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
        /// section consulted for flag values. Default <c>"FeatureFlags"</c>.
        /// A flag <c>"new-checkout"</c> is read from
        /// <c>FeatureFlags:new-checkout</c>.
        /// </summary>
        public string ConfigurationSection { get; set; } = "FeatureFlags";

        /// <summary>
        /// Static defaults consulted when the configuration section does
        /// not contain an entry for the flag. Case-insensitive by default
        /// to make <c>"new-checkout"</c> and <c>"New-Checkout"</c>
        /// interchangeable — flag naming is high-churn and operators
        /// should not be punished for Title-Case.
        /// </summary>
        public IDictionary<string, bool> Defaults { get; set; } =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Optional dynamic resolver consulted <b>before</b> configuration
        /// and defaults. Returning <c>null</c> means "I don't have an
        /// opinion — fall through to config / defaults." Returning
        /// <c>true</c> or <c>false</c> short-circuits the chain. A
        /// throwing resolver is caught and treated as <c>null</c> so a
        /// faulty flag service never takes down the app.
        /// </summary>
        /// <remarks>
        /// Typical uses: per-tenant override, canary ring evaluation,
        /// authenticated-user-id-based rollout, LaunchDarkly / Unleash
        /// adapter, time-windowed flags. The <see cref="HttpContext"/>
        /// argument is <c>null</c> for calls from background services /
        /// jobs that have no request scope.
        /// </remarks>
        public Func<HttpContext?, string, bool?>? Resolver { get; set; }
    }
}
