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
    /// 註冊 ETL 告警服務（Email + Webhook）。
    /// 可選傳入 <paramref name="configure"/> 設定 SMTP；Webhook 不需額外設定。
    /// </summary>
    /// <example>
    /// builder.Services.AddWtmEtlAlerts(opts =&gt; {
    ///     opts.Smtp = new SmtpAlertOptions {
    ///         Host = "smtp.example.com", Port = 587,
    ///         EnableSsl = true,
    ///         UserName = "...", Password = "...",
    ///         FromAddress = "etl-alert@example.com"
    ///     };
    /// });
    /// </example>
    public static IServiceCollection AddWtmEtlAlerts(
        this IServiceCollection services,
        Action<EtlAlertOptions>? configure = null)
    {
        services.AddHttpClient("EtlAlert");

        if (configure != null)
            services.Configure(configure);
        else
            services.Configure<EtlAlertOptions>(_ => { });

        services.AddTransient<IEtlAlertService, EtlAlertService>();

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
        });

        builder.Entity<EtlRunLog>(e =>
        {
            e.ToTable("EtlRunLogs");
            e.HasIndex(x => x.JobId);
            e.HasIndex(x => x.StartedAt);
            e.HasOne(x => x.Job).WithMany().HasForeignKey(x => x.JobId);
        });

        return builder;
    }
}
