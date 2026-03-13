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

            // Auto-register AnalysisWidgetDataSource when Analysis Mode dependencies are available
            var hasRegistry = services.Any(d => d.ServiceType == typeof(AnalysisVmRegistry));
            var hasEngine = services.Any(d => d.ServiceType == typeof(AnalysisQueryEngine));
            if (hasRegistry && hasEngine)
            {
                services.AddTransient<IWidgetDataSource, AnalysisWidgetDataSource>();
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