#nullable enable
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Core.FeatureFlags
{
    /// <summary>
    /// Lightweight feature-flag service for WTM apps. Lets callers gate
    /// new code paths behind a named flag without an external dependency
    /// (Microsoft.FeatureManagement, LaunchDarkly, Unleash, etc.) while
    /// staying compatible with one via <see cref="WtmFeatureFlagsOptions.Resolver"/>.
    /// </summary>
    /// <remarks>
    /// Register with <c>services.AddWtmFeatureFlags()</c> (or the
    /// callback overload). Resolution order is: resolver → configuration
    /// → defaults → <c>false</c>.
    /// </remarks>
    public interface IWtmFeatureFlags
    {
        /// <summary>
        /// Returns <c>true</c> when the named flag is enabled; <c>false</c>
        /// otherwise. A <c>null</c>, empty, or whitespace-only
        /// <paramref name="flagName"/> always returns <c>false</c> — the
        /// safe default for a mis-keyed call site.
        /// </summary>
        bool IsEnabled(string? flagName);

        /// <summary>
        /// Snapshot of all flag names present in the configuration
        /// section or the static defaults, with their currently-resolved
        /// values. Intended for an admin diagnostics endpoint — do not
        /// call on the hot path (evaluates every flag).
        /// </summary>
        IReadOnlyDictionary<string, bool> Snapshot();
    }
}
