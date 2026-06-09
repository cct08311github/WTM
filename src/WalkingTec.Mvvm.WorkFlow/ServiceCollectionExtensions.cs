#nullable enable
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace WalkingTec.Mvvm.WorkFlow;

/// <summary>
/// WorkFlow 模組 DI 註冊擴充方法
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 註冊 WorkFlow 模組核心服務。
    /// 僅需傳入可選的 <paramref name="configure"/> 調整 <see cref="WorkFlowOptions"/>。
    /// 如需告警 Webhook，另呼叫 AddWtmWorkFlowNotifications()（WF-5）。
    /// 如需逾時排程，另呼叫 AddWtmWorkFlowTimers()（WF-6）。
    /// </summary>
    /// <remarks>
    /// WF-2: engine service registrations (IWorkflowEngine, IApproverResolver,
    ///        IRoutingEvaluator, INodeKindDispatcher) will be wired here.
    /// </remarks>
    public static IServiceCollection AddWtmWorkFlow(
        this IServiceCollection services,
        Action<WorkFlowOptions>? configure = null)
    {
        // Register WorkFlowOptions configuration (mirrors AddWtmEtl pattern).
        if (configure != null)
            services.Configure(configure);
        else
            services.Configure<WorkFlowOptions>(_ => { });

        // WF-2: Register engine seams here (IWorkflowEngine, IApproverResolver,
        //        IRoutingEvaluator, INodeKindDispatcher).

        return services;
    }

    /// <summary>
    /// Opt-in. Wires an already-registered IWtmWebhookSink into the engine notifier.
    /// Mirrors AddWtmEtlAlerts. No-op when no sink is registered.
    /// </summary>
    /// <remarks>WF-5: IWorkflowNotifier / WebhookWorkflowNotifier implemented here.</remarks>
    public static IServiceCollection AddWtmWorkFlowNotifications(
        this IServiceCollection services)
    {
        // WF-5: services.AddScoped<IWorkflowNotifier, WebhookWorkflowNotifier>();
        return services;
    }

    /// <summary>
    /// Opt-in (Timeout wave). Adds the durable timer poller modeled on EtlHostedService.
    /// </summary>
    /// <remarks>WF-6: WorkflowTimerHostedService + ITimeoutActionRegistry registered here.</remarks>
    public static IServiceCollection AddWtmWorkFlowTimers(
        this IServiceCollection services)
    {
        // WF-6: services.AddSingleton<ITimeoutActionRegistry, TimeoutActionRegistry>();
        //        services.AddHostedService<WorkflowTimerHostedService>();
        return services;
    }
}

/// <summary>
/// EF Core ModelBuilder 擴充 — 註冊 WorkFlow 資料模型
/// </summary>
public static class WorkFlowDbContextExtensions
{
    /// <summary>
    /// 在 DataContext.OnModelCreating 中呼叫以註冊 WorkFlow 表。
    /// Mirrors <c>ApplyEtlModels()</c>: called from the CONSUMER's DataContext,
    /// NOT from FrameworkContext.
    /// Does NOT add HasQueryFilter — auto-applied by DataContext for ITenant/IPersistPoco.
    /// </summary>
    /// <remarks>
    /// WF-2: Entity mappings (ProcessDefinition, ProcessInstance, NodeInstance,
    ///        ApprovalTask, WorkflowEventLog, etc.) will be registered here.
    /// </remarks>
    public static ModelBuilder ApplyWorkFlowModels(this ModelBuilder builder)
    {
        // WF-2: builder.Entity<ProcessDefinition>(...) + related entities.
        return builder;
    }
}
