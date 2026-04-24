#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WalkingTec.Mvvm.Core.FeatureFlags
{
    /// <summary>
    /// Default <see cref="IWtmFeatureFlags"/> implementation: resolves
    /// each lookup against (in order) the configured
    /// <see cref="WtmFeatureFlagsOptions.Resolver"/>, the
    /// <see cref="IConfiguration"/> section, and the static
    /// <see cref="WtmFeatureFlagsOptions.Defaults"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Thread-safe: <see cref="IOptionsMonitor{TOptions}"/> publishes
    /// atomic option snapshots, and <see cref="IConfiguration"/> is
    /// itself thread-safe. No internal caching — every call re-resolves
    /// so file-watcher / <c>IConfigurationRoot.Reload()</c> changes
    /// propagate immediately. Flag lookups are string-keyed dictionary
    /// reads and a single config probe; cost is negligible.
    /// </para>
    /// <para>
    /// <see cref="IHttpContextAccessor"/> is optional — background
    /// services / hosted jobs can resolve <see cref="IWtmFeatureFlags"/>
    /// and ask for a flag without a request in scope.
    /// </para>
    /// </remarks>
    public class WtmFeatureFlags : IWtmFeatureFlags
    {
        private readonly IOptionsMonitor<WtmFeatureFlagsOptions> _options;
        private readonly IConfiguration _configuration;
        private readonly IHttpContextAccessor? _httpContextAccessor;
        private readonly ILogger<WtmFeatureFlags>? _logger;

        public WtmFeatureFlags(
            IOptionsMonitor<WtmFeatureFlagsOptions> options,
            IConfiguration configuration,
            IHttpContextAccessor? httpContextAccessor = null,
            ILogger<WtmFeatureFlags>? logger = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
        }

        public bool IsEnabled(string? flagName)
        {
            if (string.IsNullOrWhiteSpace(flagName)) { return false; }

            var opts = _options.CurrentValue;

            // Priority 1: dynamic resolver.
            if (opts.Resolver != null)
            {
                try
                {
                    var http = _httpContextAccessor?.HttpContext;
                    var resolved = opts.Resolver(http, flagName!);
                    if (resolved.HasValue) { return resolved.Value; }
                }
                catch (Exception ex)
                {
                    // A faulty resolver must not take the app down —
                    // log and fall through to config / defaults.
                    _logger?.LogWarning(ex,
                        "WtmFeatureFlags: Resolver threw for flag {Flag}; falling back to config/defaults",
                        LogSanitizer.Sanitize(flagName!));
                }
            }

            // Priority 2: configuration binding.
            var configKey = BuildConfigKey(opts.ConfigurationSection, flagName!);
            var raw = _configuration[configKey];
            if (!string.IsNullOrEmpty(raw) && bool.TryParse(raw, out var parsed))
            {
                return parsed;
            }

            // Priority 3: static defaults.
            return opts.Defaults.TryGetValue(flagName!, out var def) && def;
        }

        public IReadOnlyDictionary<string, bool> Snapshot()
        {
            var opts = _options.CurrentValue;
            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            // Start from defaults so statically-declared flags appear
            // even when the operator hasn't overridden them.
            foreach (var kv in opts.Defaults)
            {
                result[kv.Key] = IsEnabled(kv.Key);
            }

            // Overlay configuration-declared flags (may add new names
            // not present in Defaults).
            var section = _configuration.GetSection(opts.ConfigurationSection);
            if (section.Exists())
            {
                foreach (var child in section.GetChildren())
                {
                    if (string.IsNullOrEmpty(child.Key)) { continue; }
                    result[child.Key] = IsEnabled(child.Key);
                }
            }

            return result;
        }

        internal static string BuildConfigKey(string section, string flag)
        {
            if (string.IsNullOrEmpty(section)) { return flag; }
            // IConfiguration uses ':' (or '__' in env vars) as separator.
            return section + ConfigurationPath.KeyDelimiter + flag;
        }
    }
}
