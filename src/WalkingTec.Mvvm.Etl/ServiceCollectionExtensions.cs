#nullable enable
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WalkingTec.Mvvm.Etl.Alerting;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Scheduling;

namespace WalkingTec.Mvvm.Etl;

/// <summary>
/// ETL 模組 DI 註冊擴充方法
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 註冊 ETL 模組服務（排程、進度追蹤）。
    /// 如需告警功能，請額外呼叫 <see cref="AddWtmEtlAlerts"/>。
    /// </summary>
    public static IServiceCollection AddWtmEtl(this IServiceCollection services)
    {
        services.AddSingleton<EtlSchedulerService>();
        services.AddSingleton<EtlProgressTracker>();
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
