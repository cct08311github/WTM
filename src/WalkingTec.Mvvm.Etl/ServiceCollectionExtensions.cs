#nullable enable
using System;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WalkingTec.Mvvm.Etl.Alerting;
using WalkingTec.Mvvm.Etl.Dashboard;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Pipeline.Sources;
using WalkingTec.Mvvm.Etl.Scheduling;

namespace WalkingTec.Mvvm.Etl;

/// <summary>
/// ETL 模組 DI 註冊擴充方法
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 註冊 ETL 模組服務（排程、進度追蹤、儀表板）。
    /// 同時以 Singleton 登記 <see cref="IEtlSourceRegistry"/>，
    /// 預先包含 Oracle、SqlServer、CSV、Excel 和 <b>REST/HTTP</b> 五種內建 source。
    /// 如需告警功能，請額外呼叫 <see cref="AddWtmEtlAlerts"/>。
    /// 如需自訂 source 種類，在 <c>AddWtmEtl()</c> 之後取得
    /// <see cref="IEtlSourceRegistry"/> 並呼叫
    /// <see cref="IEtlSourceRegistry.Register"/>。
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="configure">
    /// 選填：設定 <see cref="EtlOptions"/>（例如 <see cref="EtlOptions.RunLogRetentionDays"/>）。
    /// 未傳入時使用預設值（RunLogRetentionDays = 0，保留全部記錄）。
    /// </param>
    public static IServiceCollection AddWtmEtl(
        this IServiceCollection services,
        Action<EtlOptions>? configure = null)
    {
        // Register EtlOptions configuration (#217).
        if (configure != null)
            services.Configure(configure);
        else
            services.Configure<EtlOptions>(_ => { });

        // Register the REST/HTTP ETL source named HttpClient with SSRF-hardening:
        //   AllowAutoRedirect=false prevents redirects bypassing the SSRF guard.
        //   PinnedConnectAsync is the TOCTOU-safe DNS-pinning ConnectCallback.
        services.AddHttpClient(RestEtlSource.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback   = RestEtlSource.PinnedConnectAsync,
            });

        // Register the shared default source registry (#218).
        // Already contains Oracle/SqlServer/CSV/Excel built-in sources.
        // Using the same instance as EtlSourceFactory.DefaultRegistry ensures that
        // registrations made via DI-injected IEtlSourceRegistry are also visible
        // when code calls EtlSourceFactory.CreateSource(string).
        //
        // The RestEtlSource requires IHttpClientFactory, so its factory delegate
        // captures the IServiceProvider and resolves it lazily on first use.
        services.AddSingleton<IEtlSourceRegistry>(sp =>
        {
            var registry = EtlSourceFactory.DefaultRegistry;
            var httpClientFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            registry.Register("rest",  () => new RestEtlSource(httpClientFactory));
            registry.Register("http",  () => new RestEtlSource(httpClientFactory));
            return registry;
        });

        services.AddSingleton<EtlSchedulerService>();
        services.AddSingleton<EtlProgressTracker>();
        services.AddSingleton<EtlDashboardService>();
        services.AddHostedService<EtlHostedService>();

        return services;
    }

    /// <summary>
    /// 註冊 ETL 告警服務（Email + per-job Webhook + 可選 shared webhook sink）。
    /// 可選傳入 <paramref name="configure"/> 設定 SMTP 及 <see cref="EtlAlertOptions.EnableWebhookAlerts"/>。
    /// </summary>
    /// <remarks>
    /// <b>Shared webhook sink 整合（opt-in）：</b>
    /// <list type="number">
    ///   <item>呼叫 <c>AddWtmWebhookSink()</c> 或 <c>AddWtmWebhookSinks()</c> 登記 <c>IWtmWebhookSink</c>。</item>
    ///   <item>在 <paramref name="configure"/> 中設定 <c>opts.EnableWebhookAlerts = true</c>。</item>
    /// </list>
    /// 未設定時行為不變（僅 Email + per-job webhook）。
    /// </remarks>
    /// <example>
    /// // Email only (unchanged behaviour):
    /// builder.Services.AddWtmEtlAlerts(opts =&gt; {
    ///     opts.Smtp = new SmtpAlertOptions { Host = "smtp.example.com", ... };
    /// });
    ///
    /// // Email + shared webhook sink (DingTalk / Slack / Teams / etc.):
    /// builder.Services.AddWtmWebhookSink(o =&gt; {
    ///     o.Provider = WebhookProviderKind.Slack;
    ///     o.Url = "https://hooks.slack.com/services/...";
    /// });
    /// builder.Services.AddWtmEtlAlerts(opts =&gt; {
    ///     opts.EnableWebhookAlerts = true;
    ///     opts.Smtp = new SmtpAlertOptions { ... }; // optional
    /// });
    /// </example>
    public static IServiceCollection AddWtmEtlAlerts(
        this IServiceCollection services,
        Action<EtlAlertOptions>? configure = null)
    {
        // Register the EtlAlert named HttpClient with SSRF-hardening identical to
        // the RestEtlSource client: no auto-redirect (prevents redirect-based SSRF
        // bypasses) and a DNS-pinning ConnectCallback that rejects private/loopback/
        // IMDS addresses at actual TCP connect time (TOCTOU-safe). Issue #484.
        services.AddHttpClient("EtlAlert")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback   = RestEtlSource.PinnedConnectAsync,
            });

        if (configure != null)
            services.Configure(configure);
        else
            services.Configure<EtlAlertOptions>(_ => { });

        // Register EtlAlertService with optional IWtmWebhookSink dependency.
        // When the sink is not registered, it is resolved as null (GetService vs GetRequiredService).
        services.AddTransient<IEtlAlertService>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var options           = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EtlAlertOptions>>();
            var logger            = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<EtlAlertService>>();
            var webhookSink       = sp.GetService<WalkingTec.Mvvm.Core.Notifications.IWtmWebhookSink>();
            return new EtlAlertService(httpClientFactory, options, logger, webhookSink);
        });

        return services;
    }
}

/// <summary>
/// EF Core ModelBuilder 擴充 — 註冊 ETL 資料模型
/// </summary>
public static class EtlDbContextExtensions
{
    /// <summary>
    /// 在 DataContext.OnModelCreating 中呼叫以註冊 ETL 表
    /// </summary>
    public static ModelBuilder ApplyEtlModels(this ModelBuilder builder)
    {
        builder.Entity<EtlJobDefinition>(e =>
        {
            e.ToTable("EtlJobDefinitions");
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.Status);
            // ETL-006: TenantCode index for multi-tenant job isolation queries
            e.HasIndex(x => x.TenantCode);
        });

        builder.Entity<EtlRunLog>(e =>
        {
            e.ToTable("EtlRunLogs");
            e.HasIndex(x => x.JobId);
            e.HasIndex(x => x.StartedAt);
            e.HasOne(x => x.Job).WithMany().HasForeignKey(x => x.JobId);
        });

        // ETL-004: Dead-letter / quarantine store
        builder.Entity<EtlDeadLetterRow>(e =>
        {
            e.ToTable("EtlDeadLetterRows");
            e.HasIndex(x => x.JobId);
            e.HasIndex(x => x.RunId);
            e.HasIndex(x => x.QuarantinedAt);
            e.HasIndex(x => x.TenantCode);
        });

        // ETL-005: Data lineage records
        builder.Entity<EtlLineageRecord>(e =>
        {
            e.ToTable("EtlLineageRecords");
            e.HasIndex(x => x.JobId);
            e.HasIndex(x => x.RunId);
            e.HasIndex(x => x.RecordedAt);
        });

        return builder;
    }
}
