using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

        private static bool IsDevelopmentName(string name)
        {
            return string.Equals(name?.Trim(), Environments.Development, StringComparison.OrdinalIgnoreCase);
        }
    }
}
