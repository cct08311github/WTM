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
using WalkingTec.Mvvm.Core.Support.Email;

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

        /// <summary>
        /// Issue #948: registers a host-owned <see cref="IDashboardEgressPolicy"/> that
        /// <see cref="RestWidgetDataSource"/> consults whenever a REST widget's resolved
        /// destination would otherwise be blocked by the SSRF-safe default (private
        /// network, or plain HTTP). Nothing is registered by default — without calling
        /// this, every such destination is rejected, for every widget, regardless of what
        /// its <c>RestOptions.AllowPrivateNetwork</c>/<c>AllowHttp</c> ask for. See
        /// <see cref="IDashboardEgressPolicy"/> for the full rationale.
        /// </summary>
        /// <remarks>
        /// <b>Registered as a Singleton — this is required, not a stylistic choice.</b>
        /// <see cref="IDashboardService"/> (the only consumer, via its
        /// <c>IEnumerable&lt;IWidgetDataSource&gt;</c> constructor dependency, which pulls
        /// in <see cref="RestWidgetDataSource"/>) is itself registered Singleton by
        /// <see cref="AddWtmDashboard"/>, and that <c>IEnumerable&lt;IWidgetDataSource&gt;</c>
        /// is resolved exactly once, at the moment the singleton <c>IDashboardService</c> is
        /// first constructed — so <see cref="RestWidgetDataSource"/>'s own <c>Transient</c>
        /// registration is effectively moot here; it is built once, from the root container.
        /// An earlier version of this method registered the policy <c>Scoped</c>, which is a
        /// captive-dependency error: with ASP.NET Core's default DI validation
        /// (<c>ValidateScopes</c>/<c>ValidateOnBuild</c>, on by default in
        /// <c>Host.CreateDefaultBuilder</c> under the Development environment) the app fails
        /// to start outright (<c>"Cannot consume scoped service ... from singleton ..."</c>);
        /// with validation off (typically Production) it silently becomes exactly the captive
        /// dependency the validator would have caught — one <typeparamref name="T"/> instance
        /// resolved once from the root provider and held for the process lifetime, including
        /// by the background <c>DashboardAlertHostedService</c>/<c>DashboardSnapshotJob</c>
        /// singletons that share the same <c>_dataSources</c>. Implementations MUST therefore
        /// be thread-safe and must not depend on scoped services (a <c>DbContext</c> resolved
        /// through DI, <c>IHttpContextAccessor</c>-derived per-request state, etc.) — a policy
        /// that needs a database should hold an <c>IDbContextFactory&lt;T&gt;</c> (itself
        /// singleton-safe) and create a short-lived context per <see cref="IDashboardEgressPolicy.IsAllowedAsync"/>
        /// call, or use <c>IServiceScopeFactory.CreateScope()</c> internally.
        /// </remarks>
        /// <example>
        /// <code>
        /// services.AddWtmDashboard();
        /// services.AddWtmDashboardEgressPolicy&lt;MyInternalHostAllowlistPolicy&gt;();
        /// </code>
        /// </example>
        public static IServiceCollection AddWtmDashboardEgressPolicy<T>(this IServiceCollection services)
            where T : class, IDashboardEgressPolicy
        {
            services.AddSingleton<IDashboardEgressPolicy, T>();
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

        // ── Snapshot delivery sinks ───────────────────────────────────────────

        /// <summary>
        /// Registers <see cref="FileSystemSnapshotSink"/> as an <see cref="IDashboardSnapshotSink"/>
        /// that writes completed snapshots to <paramref name="outputDirectory"/>.
        /// </summary>
        /// <remarks>
        /// Call this <strong>after</strong> <see cref="AddWtmDashboardSnapshots"/>.
        /// </remarks>
        /// <param name="services">Service collection.</param>
        /// <param name="outputDirectory">
        /// Absolute path to the directory where snapshot files are written.
        /// The directory is created automatically if it does not exist.
        /// </param>
        /// <example>
        /// <code>
        /// services.AddWtmDashboardSnapshots(opt => { ... });
        /// services.AddDashboardFileSnapshotSink("/var/snapshots/dashboard");
        /// </code>
        /// </example>
        public static IServiceCollection AddDashboardFileSnapshotSink(
            this IServiceCollection services,
            string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("outputDirectory must not be null or whitespace.", nameof(outputDirectory));

            EnsureDeliveryOptions(services);
            services.Configure<DashboardSnapshotDeliveryOptions>(o => o.OutputDirectory = outputDirectory);
            services.AddSingleton<IDashboardSnapshotSink, FileSystemSnapshotSink>();

            return services;
        }

        /// <summary>
        /// Registers <see cref="EmailSnapshotSink"/> as an <see cref="IDashboardSnapshotSink"/>
        /// that sends completed snapshots as e-mail attachments to <paramref name="recipients"/>.
        /// </summary>
        /// <remarks>
        /// Requires <see cref="EmailServiceCollectionExtensions.AddWtmEmail"/> (or
        /// <see cref="EmailServiceCollectionExtensions.AddWtmNullEmail"/>) to be called
        /// so that <see cref="IWtmEmailService"/> is resolvable.
        /// The sink silently no-ops when no recipients are supplied or when
        /// <see cref="IWtmEmailService"/> is not registered.
        /// Call this <strong>after</strong> <see cref="AddWtmDashboardSnapshots"/>.
        /// </remarks>
        /// <param name="services">Service collection.</param>
        /// <param name="recipients">One or more recipient e-mail addresses.</param>
        /// <param name="subjectTemplate">
        /// Optional subject template. Supports <c>{DashboardId}</c>, <c>{JobId}</c>, <c>{FileName}</c>.
        /// Defaults to <c>"Dashboard Snapshot: {DashboardId}"</c>.
        /// </param>
        /// <example>
        /// <code>
        /// services.AddWtmEmail(o => { o.Host = "smtp.example.com"; ... });
        /// services.AddWtmDashboardSnapshots(opt => { ... });
        /// services.AddDashboardEmailSnapshotSink("ops@example.com", "finance@example.com");
        /// </code>
        /// </example>
        public static IServiceCollection AddDashboardEmailSnapshotSink(
            this IServiceCollection services,
            string[] recipients,
            string? subjectTemplate = null)
        {
            if (recipients == null) throw new ArgumentNullException(nameof(recipients));

            EnsureDeliveryOptions(services);
            services.Configure<DashboardSnapshotDeliveryOptions>(o =>
            {
                o.EmailRecipients      = recipients;
                o.EmailSubjectTemplate = subjectTemplate;
            });

            // EmailSnapshotSink takes IWtmEmailService? (nullable) so it gracefully no-ops
            // when the email service has not been registered.
            services.TryAddSingleton<IWtmEmailService>(_ => null!);
            services.AddSingleton<IDashboardSnapshotSink, EmailSnapshotSink>();

            return services;
        }

        /// <summary>
        /// Registers <see cref="WebhookNotificationSnapshotSink"/> as an <see cref="IDashboardSnapshotSink"/>
        /// that posts a completion summary card to the registered <see cref="IWtmWebhookSink"/>
        /// when a snapshot job finishes.
        /// </summary>
        /// <remarks>
        /// Webhooks cannot carry binary attachments; this sink sends a summary only (job ID,
        /// dashboard ID, file name, byte count). The actual file bytes are not transmitted.
        /// The sink silently no-ops when <see cref="IWtmWebhookSink"/> is not registered.
        /// Call this <strong>after</strong> <see cref="AddWtmDashboardSnapshots"/>.
        /// </remarks>
        /// <example>
        /// <code>
        /// services.AddWtmWebhookSink(opt => opt.AddDingTalk("https://..."));
        /// services.AddWtmDashboardSnapshots(opt => { ... });
        /// services.AddDashboardWebhookNotificationSnapshotSink();
        /// </code>
        /// </example>
        public static IServiceCollection AddDashboardWebhookNotificationSnapshotSink(
            this IServiceCollection services)
        {
            // WebhookNotificationSnapshotSink takes IWtmWebhookSink? (nullable) so it gracefully
            // no-ops when no webhook sink has been registered.
            services.TryAddSingleton<IWtmWebhookSink>(_ => null!);
            services.AddSingleton<IDashboardSnapshotSink, WebhookNotificationSnapshotSink>();

            return services;
        }

        // ── Internal helpers ──────────────────────────────────────────────────

        private static void EnsureDeliveryOptions(IServiceCollection services)
        {
            // Idempotent — AddOptions<T> is safe to call multiple times.
            services.AddOptions<DashboardSnapshotDeliveryOptions>();
        }
    }
}