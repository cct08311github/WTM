using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Dashboard
{
    public static class DashboardServiceCollectionExtensions
    {
        public static IServiceCollection AddWtmDashboard(this IServiceCollection services, Action<DashboardOptions>? setupAction = null)
        {
            services.AddOptions<DashboardOptions>();
            if (setupAction != null)
            {
                services.Configure(setupAction);
            }

            services.AddSingleton<IDashboardService, JsonFileDashboardService>();

            if (services.All(d => d.ServiceType != typeof(GroupByStrategyResolver)))
            {
                services.AddSingleton(GroupByStrategyResolver.Default);
            }

            // Auto-register AnalysisWidgetDataSource when Analysis Mode dependencies are available.
            // IMPORTANT: AddWtmContext() must be called BEFORE AddWtmDashboard() for this check to
            // succeed. If called in the wrong order, AnalysisWidgetDataSource is silently skipped and
            // widget data requests for the "analysis" source will fail with "Data source not found".
            var hasRegistry = services.Any(d => d.ServiceType == typeof(AnalysisVmRegistry));
            var hasEngine = services.Any(d => d.ServiceType == typeof(AnalysisQueryEngine));
            if (hasRegistry && hasEngine)
            {
                services.AddTransient<IWidgetDataSource, AnalysisWidgetDataSource>();
            }
            else if (hasRegistry || hasEngine)
            {
                // Partial registration — one dep present without the other is unexpected.
                // Surface this at startup rather than silently producing a misconfigured container.
                throw new InvalidOperationException(
                    "Partial Analysis Mode registration detected. " +
                    "Both AnalysisVmRegistry and AnalysisQueryEngine must be registered. " +
                    "Ensure services.AddWtmContext() is called before services.AddWtmDashboard().");
            }

            return services;
        }

        public static IServiceCollection AddWidgetDataSource<T>(this IServiceCollection services) where T : class, IWidgetDataSource
        {
            services.AddTransient<IWidgetDataSource, T>();
            return services;
        }

        public static IServiceCollection AddWidgetDataSourcesFromAssembly(this IServiceCollection services, Assembly assembly)
        {
            var types = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(IWidgetDataSource).IsAssignableFrom(t));

            foreach (var type in types)
            {
                services.AddTransient(typeof(IWidgetDataSource), type);
            }

            return services;
        }
    }
}