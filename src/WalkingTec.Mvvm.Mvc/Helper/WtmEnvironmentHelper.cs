using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Resolves whether the application runs in the Development environment while the service
    /// collection is still being built (before the host, and therefore IHostEnvironment, exists).
    /// </summary>
    public static class WtmEnvironmentHelper
    {
        /// <summary>
        /// Returns true only when the environment is positively identified as Development (case-insensitive).
        /// Resolution order: an IHostEnvironment registered as an instance; then the configuration keys
        /// "environment", "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT"; then the process environment variables
        /// ASPNETCORE_ENVIRONMENT and DOTNET_ENVIRONMENT. If nothing is found the result is false
        /// (treated as Production, i.e. fail closed).
        /// </summary>
        public static bool ResolveIsDevelopment(IServiceCollection services, IConfiguration config)
        {
            var hostEnv = services?.Where(x => x.ServiceType == typeof(IHostEnvironment))
                .Select(x => x.ImplementationInstance).OfType<IHostEnvironment>().LastOrDefault();
            if (hostEnv != null)
            {
                return IsDevelopmentName(hostEnv.EnvironmentName);
            }

            var name = config?["environment"];
            if (string.IsNullOrWhiteSpace(name))
            {
                name = config?["ASPNETCORE_ENVIRONMENT"];
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                name = config?["DOTNET_ENVIRONMENT"];
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
            }
            return IsDevelopmentName(name);
        }

        /// <summary>
        /// Returns true when IsQuickDebug is enabled while the environment is not Development.
        /// </summary>
        public static bool ShouldWarnQuickDebug(Configs configs, bool isDevelopment)
        {
            return configs != null && configs.IsQuickDebug == true && isDevelopment == false;
        }

        /// <summary>
        /// Logs a critical message when <see cref="ShouldWarnQuickDebug"/> is true. Never throws.
        /// </summary>
        public static void LogQuickDebugWarning(Configs configs, bool isDevelopment, ILogger logger)
        {
            if (ShouldWarnQuickDebug(configs, isDevelopment))
            {
                logger?.LogCritical("IsQuickDebug is enabled outside the Development environment. IsQuickDebug disables permission checks and exposes error details in responses, and it must not be enabled in production. Set IsQuickDebug to false (and optionally set ExposeExceptionDetails to false).");
            }
        }

        /// <summary>
        /// Maps an Oracle Connections[].Version value to the compatibility level that is applied and whether the
        /// value is explicitly supported. 19, 21 and 23 are supported; any other non-empty value falls back to 19
        /// compatibility (unchanged behaviour) and is reported as unsupported. An empty value means "not specified"
        /// and also uses 19 compatibility without being reported.
        /// </summary>
        public static (int Compatibility, bool IsSupported) ResolveOracleCompatibility(string version)
        {
            switch (version)
            {
                case "19": return (19, true);
                case "21": return (21, true);
                case "23": return (23, true);
                default: return (19, string.IsNullOrWhiteSpace(version));
            }
        }

        /// <summary>
        /// Logs one Warning per Oracle connection whose Version is set to a value other than 19, 21 or 23.
        /// Behaviour is unchanged (19 compatibility is applied); never throws.
        /// </summary>
        public static void LogOracleVersionWarnings(Configs configs, ILogger logger)
        {
            if (configs?.Connections == null || logger == null)
            {
                return;
            }
            foreach (var cs in configs.Connections)
            {
                if (cs?.DbType == DBTypeEnum.Oracle && ResolveOracleCompatibility(cs.Version).IsSupported == false)
                {
                    logger.LogWarning("Connection '{Key}' uses Oracle with unsupported Version '{Version}'. Oracle 19c compatibility was applied. Oracle Database below 19c is not supported by this provider (ODP.NET 23); supported Version values are 19, 21 and 23.", cs.Key, cs.Version);
                }
            }
        }

        private static bool IsDevelopmentName(string name)
        {
            return string.Equals(name?.Trim(), Environments.Development, StringComparison.OrdinalIgnoreCase);
        }
    }
}
