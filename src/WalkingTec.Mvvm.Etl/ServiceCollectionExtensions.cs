#nullable enable
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Scheduling;

namespace WalkingTec.Mvvm.Etl;

/// <summary>
/// ETL 模組 DI 註冊擴充方法
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 註冊 ETL 模組服務（排程、進度追蹤）
    /// </summary>
    public static IServiceCollection AddWtmEtl(this IServiceCollection services)
    {
        services.AddSingleton<EtlSchedulerService>();
        services.AddSingleton<EtlProgressTracker>();
        services.AddHostedService<EtlHostedService>();

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
