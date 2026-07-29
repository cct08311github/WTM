#nullable enable
using System;
using System.Linq.Expressions;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WalkingTec.Mvvm.Core;
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
    /// 在 DataContext.OnModelCreating 中呼叫以註冊 ETL 表，並套用 <see cref="ITenant"/>
    /// 全域查詢過濾器（多租戶隔離）。
    /// </summary>
    /// <remarks>
    /// <b>Issue #862 root cause, fixed here:</b> the (now obsolete)
    /// <see cref="ApplyEtlModels(ModelBuilder)"/> zero-argument overload registers ETL entity
    /// types via <c>modelBuilder.Entity&lt;T&gt;()</c>, but every documented call site (see
    /// <c>docs/etl-module.md</c>, <c>demo/WalkingTec.Mvvm.Demo/DataContext.cs</c>) invokes it
    /// from the consumer's own <c>DataContext.OnModelCreating</c> AFTER
    /// <c>base.OnModelCreating(modelBuilder)</c> returns. By the time that base call returns,
    /// <c>FrameworkContext.OnModelCreating</c>'s own Pass 2 loop
    /// (<c>src/WalkingTec.Mvvm.Core/DataContext.cs</c>) has ALREADY finished iterating
    /// <c>modelBuilder.Model.GetEntityTypes()</c> and applying the <see cref="ITenant"/> /
    /// <c>IPersistPoco</c> global query filters for every entity type known to the model AT
    /// THAT POINT — it can never retroactively see an entity type registered afterward. This
    /// means <see cref="EtlJobDefinition"/>'s <see cref="ITenant"/> implementation (added for
    /// ETL-006) has never actually been enforced through a standard
    /// <c>FrameworkContext</c>-derived app: a context scoped to tenant A could read tenant B's
    /// <see cref="EtlJobDefinition"/> row by id with no filter applied at all (confirmed
    /// empirically — the generated SQL carried no <c>TenantCode</c> predicate whatsoever, not
    /// merely a wrong one). Same root cause as #841/#862's headline claim about
    /// <see cref="EtlRunLog"/> lacking <see cref="ITenant"/> entirely, but one layer deeper: even
    /// the ETL entity that DOES implement the interface was never actually isolated.
    /// This overload closes that gap by re-applying the identical query-filter pattern
    /// <c>DataContext.cs</c>'s Pass 2 uses (<c>TenantCode == this.TenantCode</c>, EF Core's
    /// documented "reference the current DbContext instance in a query filter" idiom), scoped
    /// to exactly the ETL entity types that implement <see cref="ITenant"/>, immediately after
    /// registering them in THIS method — so ordering can no longer defeat it.
    /// </remarks>
    /// <param name="builder">ModelBuilder from your DataContext.OnModelCreating.</param>
    /// <param name="context">
    /// Pass <c>this</c> from your DataContext.OnModelCreating override. Required so the
    /// TenantCode filter binds to the CURRENT context instance's TenantCode at query time
    /// (EF Core's supported "DbContext instance access in query filters" idiom) instead of a
    /// value that would otherwise be impossible to obtain from a ModelBuilder-only extension
    /// method.
    /// </param>
    public static ModelBuilder ApplyEtlModels(this ModelBuilder builder, EmptyContext context)
    {
        ApplyEtlModelsCore(builder);

        // #862: apply the ITenant filter for every ETL entity that implements it, right here,
        // instead of relying on FrameworkContext.OnModelCreating's Pass 2 (which never sees
        // these types -- see remarks above).
        ApplyEtlTenantFilter<EtlJobDefinition>(builder, context);
        ApplyEtlTenantFilter<EtlDeadLetterRow>(builder, context);
        ApplyEtlTenantFilter<EtlLineageRecord>(builder, context);
        ApplyEtlTenantFilter<EtlRunLog>(builder, context);

        return builder;
    }

    /// <summary>
    /// 舊版多載（無 <see cref="EmptyContext"/> 參數）— 僅為向下相容保留，行為與升級前完全
    /// 一致：只註冊資料表結構，<b>不會</b>套用 <see cref="ITenant"/> 全域查詢過濾器。
    /// 這正是 #862 描述的問題本身——本多載沒有管道可以取得目前的 context 執行個體，因此無法
    /// 修正。請改用 <see cref="ApplyEtlModels(ModelBuilder, EmptyContext)"/>。
    /// </summary>
    [Obsolete("ApplyEtlModels() without a DbContext instance can never apply the ITenant global " +
              "query filter to EtlJobDefinition/EtlRunLog/EtlDeadLetterRow/EtlLineageRecord " +
              "(issue #862) -- table/column/index registration is unchanged and still runs, but " +
              "tenant isolation for these tables does not. Call " +
              "ApplyEtlModels(this) from your DataContext.OnModelCreating instead.")]
    public static ModelBuilder ApplyEtlModels(this ModelBuilder builder)
    {
        return ApplyEtlModelsCore(builder);
    }

    private static ModelBuilder ApplyEtlModelsCore(ModelBuilder builder)
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
            // #841/#862: TenantCode index for multi-tenant isolation queries (new column --
            // see this type's own doc comment and the #862 migration notes in CHANGELOG.md).
            e.HasIndex(x => x.TenantCode);
        });

        // ETL-004: Dead-letter / quarantine store
        builder.Entity<EtlDeadLetterRow>(e =>
        {
            e.ToTable("EtlDeadLetterRows");
            e.HasIndex(x => x.JobId);
            e.HasIndex(x => x.RunId);
            e.HasIndex(x => x.QuarantinedAt);
            e.HasIndex(x => x.TenantCode);
            // #673(d): composite index for ClearDeadLetterFromFailedRunsAsync's
            // (JobId, RunSucceeded == false) lookup, run at the start of every job
            // execution when EnableDeadLetter is on.
            e.HasIndex(x => new { x.JobId, x.RunSucceeded });
        });

        // ETL-005: Data lineage records
        builder.Entity<EtlLineageRecord>(e =>
        {
            e.ToTable("EtlLineageRecords");
            e.HasIndex(x => x.JobId);
            e.HasIndex(x => x.RunId);
            e.HasIndex(x => x.RecordedAt);
            // #862: TenantCode index for multi-tenant isolation queries (new column -- see
            // this type's own doc comment and the #862 migration notes in CHANGELOG.md).
            e.HasIndex(x => x.TenantCode);
        });

        return builder;
    }

    /// <summary>
    /// Applies the same <c>TenantCode == this.TenantCode</c> global query filter
    /// <c>DataContext.cs</c>'s Pass 2 loop uses, scoped to a single entity type known (at
    /// compile time, via the <c>where T : ITenant</c> constraint) to implement
    /// <see cref="ITenant"/>. See the remarks on
    /// <see cref="ApplyEtlModels(ModelBuilder, EmptyContext)"/> for why this needs to live here
    /// rather than relying on the base context's own filter application.
    /// </summary>
    private static void ApplyEtlTenantFilter<T>(ModelBuilder builder, EmptyContext context)
        where T : class, ITenant
    {
        var pe = Expression.Parameter(typeof(T));
        var exp = Expression.Equal(
            Expression.Property(pe, nameof(ITenant.TenantCode)),
            Expression.PropertyOrField(Expression.Constant(context), nameof(EmptyContext.TenantCode)));
        var lambda = Expression.Lambda<Func<T, bool>>(exp, pe);
        builder.Entity<T>().HasQueryFilter(lambda);
    }
}
