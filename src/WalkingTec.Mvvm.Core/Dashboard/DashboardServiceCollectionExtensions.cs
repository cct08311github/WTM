using System;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Dashboard.Alerting;
using WalkingTec.Mvvm.Core.Dashboard.Snapshot;
using WalkingTec.Mvvm.Core.Notifications;

namespace WalkingTec.Mvvm.Core.Dashboard
{
    public static class DashboardServiceCollectionExtensions
    {
        public static IServiceCollection AddWtmDashboard(this IServiceCollection services, Action<DashboardOptions>? setupAction = null)
        {
            services.AddLogging();
            services.AddOptions<DashboardOptions>();
            if (setupAction != null)
            {
                services.Configure(setupAction);
            }

            services.AddSingleton<IDashboardService, JsonFileDashboardService>();

            // Issue #824: register built-in REST widget data source. Requires
            // IHttpClientFactory + IMemoryCache — register them here if the
            // host hasn't already (both are idempotent via TryAdd-style).
            services.AddHttpClient();
            services.AddMemoryCache();

            // Issue #101 (SSRF hardening): register the named HttpClient used by RestWidgetDataSource
            // with AllowAutoRedirect=false and a ConnectCallback that enforces the SSRF block at
            // actual TCP connect time (TOCTOU-safe DNS pinning).
            //
            // ConnectCallback = RestWidgetDataSource.PinnedConnectAsync:
            //   - Resolves the hostname to IPs at connection time (not request-build time).
            //   - Selects only IPs that pass the SSRF block list (via SelectConnectableIp).
            //   - The outgoing HttpRequestMessage.RequestUri keeps the original hostname URL,
            //     so TLS SNI and server-certificate validation use the correct hostname — HTTPS works.
            //   - AllowPrivateNetwork policy is threaded per-request via HttpRequestMessage.Options.
            //
            // AllowAutoRedirect=false: defence-in-depth — 302 redirects cannot bypass the guard.
            services.AddHttpClient(RestWidgetDataSource.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    ConnectCallback = RestWidgetDataSource.PinnedConnectAsync
                });

            services.AddTransient<IWidgetDataSource, RestWidgetDataSource>();

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

        /// <summary>
        /// Enables KPI threshold alerting for dashboards.
        /// <para>
        /// Registers <see cref="DashboardAlertHostedService"/> as a hosted service.
        /// The service is dormant unless <see cref="DashboardAlertOptions.EvaluationIntervalSeconds"/>
        /// is set to a positive value <strong>and</strong> at least one
        /// <see cref="IWtmWebhookSink"/> is registered.
        /// </para>
        /// <example>
        /// <code>
        /// services.AddWtmDashboard();
        /// services.AddWtmWebhookSink(opt => opt.AddDingTalk("https://..."));  // or any sink
        /// services.AddWtmDashboardAlerts(opt =>
        /// {
        ///     opt.EvaluationIntervalSeconds = 60;
        ///     opt.AlertCooldownSeconds = 3600;
        /// });
        /// </code>
        /// </example>
        /// </summary>
        public static IServiceCollection AddWtmDashboardAlerts(
            this IServiceCollection services,
            Action<DashboardAlertOptions>? setupAction = null)
        {
            services.AddOptions<DashboardAlertOptions>();
            if (setupAction != null)
                services.Configure(setupAction);

            // IWtmWebhookSink is optional — if none registered, we inject null via TryAddSingleton.
            // The hosted service checks for null and no-ops.
            services.TryAddSingleton<IWtmWebhookSink>(_ => null!);

            services.AddSingleton<DashboardAlertHostedService>();
            services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DashboardAlertHostedService>());

            return services;
        }

        /// <summary>
        /// Enables scheduled dashboard snapshot/export jobs.
        /// <para>
        /// Registers <see cref="DashboardSnapshotHostedService"/> and the default
        /// <see cref="DashboardSnapshotJob"/> implementation.
        /// The service is dormant unless at least one <see cref="ScheduledDashboardJobConfig"/>
        /// with a valid <see cref="ScheduledDashboardJobConfig.CronExpression"/> is provided.
        /// </para>
        /// <para>
        /// For PDF/PNG formats, also register an <see cref="IDashboardRenderer"/>:
        /// <code>services.AddSingleton&lt;IDashboardRenderer, MyPlaywrightRenderer&gt;();</code>
        /// Without one, the framework registers <see cref="NotConfiguredDashboardRenderer"/>
        /// which throws a clear error when PDF/PNG is requested.
        /// </para>
        /// <example>
        /// <code>
        /// services.AddWtmDashboard();
        /// services.AddWtmDashboardSnapshots(opt =>
        /// {
        ///     opt.Jobs.Add(new ScheduledDashboardJobConfig
        ///     {
        ///         JobId      = "weekly-sales",
        ///         DashboardId = "sales-overview",
        ///         Format     = DashboardExportFormat.Excel,
        ///         CronExpression = "0 8 * * 1"   // every Monday 08:00 UTC
        ///     });
        /// });
        /// </code>
        /// </example>
        /// </summary>
        public static IServiceCollection AddWtmDashboardSnapshots(
            this IServiceCollection services,
            Action<DashboardSnapshotOptions>? setupAction = null)
        {
            services.AddOptions<DashboardSnapshotOptions>();
            if (setupAction != null)
                services.Configure(setupAction);

            // Register the no-op renderer as fallback; host can replace with a real one.
            services.TryAddSingleton<IDashboardRenderer, NotConfiguredDashboardRenderer>();

            services.TryAddSingleton<IScheduledDashboardJob, DashboardSnapshotJob>();

            services.AddSingleton<DashboardSnapshotHostedService>();
            services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DashboardSnapshotHostedService>());

            return services;
        }

        /// <summary>
        /// Replaces the default <see cref="JsonFileDashboardService"/> with <see cref="EfCoreDashboardService"/>
        /// backed by <see cref="DashboardDbContext"/>.
        /// Call this <strong>after</strong> <see cref="AddWtmDashboard"/> to swap the store.
        /// <para>
        /// The caller must supply an EF Core provider by configuring <paramref name="configureDb"/>
        /// (e.g. <c>opt =&gt; opt.UseSqlite("...")</c>).
        /// </para>
        /// <example>
        /// <code>
        /// services.AddWtmDashboard();
        /// services.AddWtmEfDashboardStore(opt =&gt; opt.UseSqlServer(connectionString));
        /// </code>
        /// </example>
        /// </summary>
        public static IServiceCollection AddWtmEfDashboardStore(
            this IServiceCollection services,
            Action<DbContextOptionsBuilder> configureDb)
        {
            if (configureDb == null) throw new ArgumentNullException(nameof(configureDb));

            // Remove the JsonFile singleton registered by AddWtmDashboard.
            var existing = services.FirstOrDefault(
                d => d.ServiceType == typeof(IDashboardService) &&
                     d.ImplementationType == typeof(JsonFileDashboardService));
            if (existing != null)
                services.Remove(existing);

            services.AddDbContextFactory<DashboardDbContext>(configureDb);
            services.AddSingleton<IDashboardService, EfCoreDashboardService>();

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