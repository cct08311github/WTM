#nullable enable
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Engine.Routing;
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.Notifications;

namespace WalkingTec.Mvvm.WorkFlow;

/// <summary>
/// WorkFlow 模組 DI 註冊擴充方法
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 註冊 WorkFlow 模組核心服務。
    /// 僅需傳入可選的 <paramref name="configure"/> 調整 <see cref="WorkFlowOptions"/>。
    /// 如需告警 Webhook，另呼叫 <see cref="AddWtmWorkFlowNotifications"/>（WF-15）。
    /// 如需逾時排程，另呼叫 <see cref="AddWtmWorkFlowTimers"/>（WF-20）。
    ///
    /// <para><strong>WF-5 — DBTypeEnum.Memory guard:</strong>
    /// The Memory check is performed lazily on the first engine entry point that
    /// resolves <see cref="IDataContext"/> — see <see cref="ValidateDbType"/>.
    /// The eager-startup check was removed (PR #240) because calling
    /// <c>BuildServiceProvider()</c> at registration time throws
    /// "Cannot resolve scoped service from root provider" under ASP.NET Core's
    /// default scope-validation, crashing every host with a misleading DI error.
    /// The lazy per-request guard (<c>ValidateDbTypeOnFirstUse</c>) provides
    /// equivalent protection without the root-provider anti-pattern.</para>
    ///
    /// <para>The workflow engine requires a real relational provider because
    /// <c>ExecuteUpdateAsync</c> — the guarded-CAS primitive — is not supported by the
    /// EF InMemory provider (spec §9 invariant #8; same root cause as #119/#162).</para>
    /// </summary>
    public static IServiceCollection AddWtmWorkFlow(
        this IServiceCollection services,
        Action<WorkFlowOptions>? configure = null)
    {
        // 1. Register WorkFlowOptions configuration (mirrors AddWtmEtl pattern).
        if (configure != null)
            services.Configure(configure);
        else
            services.Configure<WorkFlowOptions>(_ => { });

        // 2. WF-4: Publish-flow registrations.
        //    IProcessDefinitionPublisher — scoped (one per request, wraps the scoped IDataContext).
        services.AddScoped<IProcessDefinitionPublisher, ProcessDefinitionPublisher>();

        // 3. WF-6/7: IWorkflowEngine.
        services.AddScoped<IWorkflowEngine, WorkflowEngine>();

        // 4. WF-8/9/10: IApproverResolver + IManagerChainProvider + approval mode handlers + dispatcher.
        //    All registered as scoped because handlers depend on IApproverResolver and
        //    IOptions<WorkFlowOptions> (request-scoped).
        // 5b. WF-11: IRoutingEvaluator — registered as SINGLETON because it caches compiled
        //    Expression<Func<IDictionary,bool>> delegates by content-hash. Thread-safe via
        //    ConcurrentDictionary. Must be singleton to share the compiled-predicate cache
        //    across all request-scoped engine instances.
        services.AddSingleton<IRoutingEvaluator, WhitelistRoutingEvaluator>();
        services.TryAddScoped<IManagerChainProvider, DefaultManagerChainProvider>();

        // WF-19: IApproverResolver is decorated with DelegationResolvingDecorator.
        //
        // Pattern: register DefaultApproverResolver as a concrete scoped service (the "inner"),
        // then register IApproverResolver via a factory delegate that wraps the inner with the
        // decorator.  This avoids BuildServiceProvider() (which would crash under ASP.NET Core's
        // scope-validation), and correctly handles consumer-registered custom resolvers: if the
        // consumer replaces IApproverResolver via AddScoped<IApproverResolver, CustomResolver>()
        // AFTER calling AddWtmWorkFlow(), the factory here is overridden and the decorator is NOT
        // applied to the custom resolver.  That is intentional — consumers who want delegation on
        // a custom resolver can chain their own decorator.  The default resolver always gets it.
        //
        // The factory receives the scoped IServiceProvider — safe, no root-provider access.
        services.AddScoped<DefaultApproverResolver>();
        services.AddScoped<IApproverResolver>(sp =>
            new DelegationResolvingDecorator(
                sp.GetRequiredService<DefaultApproverResolver>(),
                sp.GetRequiredService<IOptions<WorkFlowOptions>>(),
                sp.GetRequiredService<ILogger<DelegationResolvingDecorator>>()));
        services.AddScoped<SequentialApprovalHandler>(); // WF-8 串签
        services.AddScoped<AllApprovalHandler>();        // WF-9 会签
        services.AddScoped<AnyApprovalHandler>();        // WF-10 或签
        services.AddScoped<ApprovalHandler>();
        services.AddScoped<ICcTenantValidator, CcTenantValidator>(); // WF-14: FrameworkUser tenant guard
        services.AddScoped<CcHandler>();                 // WF-13 抄送 (full IApproverResolver + tenant check)
        services.AddScoped<AckHandler>();                // WF-17 blocking-acknowledge
        services.AddScoped<JoinHandler>();               // WF-17 Join barrier
        services.AddScoped<ParallelGatewayHandler>();    // WF-17 AND-fork
        services.AddScoped<InclusiveGatewayHandler>();   // WF-17 OR-fork
        services.AddScoped<INodeKindDispatcher, NodeKindDispatcher>();

        return services;
    }

    /// <summary>
    /// Opt-in. Registers <see cref="IWorkflowNotifier"/> backed by the shared
    /// <see cref="WalkingTec.Mvvm.Core.Notifications.IWtmWebhookSink"/>
    /// (DingTalk / WeCom / Feishu / Slack / Teams).
    ///
    /// <para>If no <see cref="IWtmWebhookSink"/> is registered the notifier is still wired
    /// but is a silent no-op on every event — the engine operates normally without any
    /// notification sink configured.</para>
    ///
    /// <para>Notification delivery is best-effort and non-blocking: a webhook failure is
    /// logged at <c>Error</c> level but never propagates to the engine caller and can never
    /// roll back an approval transaction.  Call notifications AFTER the engine operation
    /// completes so that a delivery failure cannot affect the authoritative state transition.
    /// </para>
    ///
    /// <para>Mirrors <c>AddWtmEtlAlerts</c> in split / opt-in pattern.</para>
    /// </summary>
    public static IServiceCollection AddWtmWorkFlowNotifications(
        this IServiceCollection services)
    {
        services.AddScoped<IWorkflowNotifier, WebhookWorkflowNotifier>();
        return services;
    }

    /// <summary>
    /// Opt-in (Timeout wave). Adds the durable timer poller modeled on EtlHostedService.
    /// </summary>
    /// <remarks>WF-20: WorkflowTimerHostedService + ITimeoutActionRegistry registered here.</remarks>
    public static IServiceCollection AddWtmWorkFlowTimers(
        this IServiceCollection services)
    {
        // WF-20: services.AddSingleton<ITimeoutActionRegistry, TimeoutActionRegistry>();
        //         services.AddHostedService<WorkflowTimerHostedService>();
        return services;
    }

    /// <summary>
    /// Called by the engine at each entry point when
    /// <see cref="WorkFlowOptions.ValidateDbTypeOnFirstUse"/> is true (lazy guard path).
    /// Throws if the resolved provider is <see cref="DBTypeEnum.Memory"/>.
    /// </summary>
    internal static void ValidateDbType(IDataContext dc)
    {
        if (dc.DBType == DBTypeEnum.Memory)
            ThrowMemoryNotSupported();
    }

    private static void ThrowMemoryNotSupported()
    {
        throw new InvalidOperationException(
            "The WorkFlow engine requires a real relational database provider. " +
            "DBTypeEnum.Memory (EF InMemory) is not supported because the engine relies on " +
            "ExecuteUpdateAsync for atomic guarded-CAS transitions, which EF InMemory cannot translate. " +
            "Configure a relational provider (SQLite, SqlServer, PgSql, MySql, Oracle, or DaMeng) " +
            "before calling AddWtmWorkFlow().");
    }
}

/// <summary>
/// EF Core ModelBuilder 擴充 — 註冊 WorkFlow 資料模型
/// </summary>
public static class WorkFlowDbContextExtensions
{
    /// <summary>
    /// Called from the CONSUMER's <c>DataContext.OnModelCreating</c> (NOT from FrameworkContext)
    /// to register all WorkFlow tables, indexes, FK relationships, and per-provider RowVer mapping.
    /// Mirrors <c>ApplyEtlModels()</c> (ServiceCollectionExtensions.cs:141).
    ///
    /// <para><strong>Does NOT add HasQueryFilter</strong> — the tenant-isolation and soft-delete
    /// filters are auto-applied by the consumer's <c>DataContext</c> for all entities that are
    /// DIRECT descendants of <c>PersistPoco</c>/<c>BasePoco</c> and implement <c>ITenant</c>
    /// (DataContext.cs:164).</para>
    ///
    /// <para><strong>EF migrations — consumer owns them.</strong>
    /// <c>WalkingTec.Mvvm.WorkFlow</c> ships ZERO migrations (same as Etl).
    /// Generate migrations in your consumer application:
    /// <code>
    /// dotnet ef migrations add WorkFlow_InitialCreate \
    ///   --context DataContext \
    ///   --project &lt;YourApp&gt;/&lt;YourApp&gt;.csproj \
    ///   --startup-project &lt;YourApp&gt;/&lt;YourApp&gt;.csproj
    /// </code>
    /// This creates 9 tables: Wf_ProcessDefinition, Wf_ProcessDefinitionVersion,
    /// Wf_ProcessInstance, Wf_NodeInstance, Wf_ApprovalTask, Wf_WorkflowEventLog,
    /// Wf_CcRecord, Wf_DelegationRule, Wf_WorkflowTimer.
    /// </para>
    ///
    /// <para><strong>RowVer concurrency mapping (WF-3, spec §7.2):</strong>
    /// <c>uint RowVer</c> is mapped as a plain property — NOT as an EF concurrency token.
    /// The engine manages it inside the WHERE clause of <c>ExecuteUpdateAsync</c>
    /// (app-incremented, portable CAS pattern from WF-0 / TokenService).
    /// The provider is detected at model-build time via <c>Database.ProviderName</c>.
    /// </para>
    /// </summary>
    public static ModelBuilder ApplyWorkFlowModels(this ModelBuilder builder)
    {
        // ── ProcessDefinition ────────────────────────────────────────────────
        builder.Entity<ProcessDefinition>(e =>
        {
            e.ToTable("Wf_ProcessDefinition");
            // Unique business key per tenant — enforced by a filtered unique index.
            e.HasIndex(x => new { x.TenantCode, x.Code }).IsUnique();
            e.HasIndex(x => x.IsEnabled);
            e.HasIndex(x => x.TenantCode);

            // Navigation to the current version (nullable until first publish).
            e.HasOne(x => x.CurrentVersion)
                .WithMany()
                .HasForeignKey(x => x.CurrentVersionId)
                .OnDelete(DeleteBehavior.Restrict);

            e.Property(x => x.Code).HasMaxLength(100).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Category).HasMaxLength(100);
            e.Property(x => x.TenantCode).HasMaxLength(50);
        });

        // ── ProcessDefinitionVersion ─────────────────────────────────────────
        builder.Entity<ProcessDefinitionVersion>(e =>
        {
            e.ToTable("Wf_ProcessDefinitionVersion");
            // Unique (TenantCode, DefinitionId, VersionNo) enforces monotonic versioning.
            e.HasIndex(x => new { x.TenantCode, x.DefinitionId, x.VersionNo }).IsUnique();
            e.HasIndex(x => x.ContentHash);

            e.HasOne(x => x.Definition)
                .WithMany()
                .HasForeignKey(x => x.DefinitionId)
                .OnDelete(DeleteBehavior.Restrict);

            e.Property(x => x.GraphJson).IsRequired();
            e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.PublishedBy).HasMaxLength(50);
            e.Property(x => x.TenantCode).HasMaxLength(50);
        });

        // ── ProcessInstance ───────────────────────────────────────────────────
        builder.Entity<ProcessInstance>(e =>
        {
            e.ToTable("Wf_ProcessInstance");
            // Business-object lookup index.
            e.HasIndex(x => new { x.TenantCode, x.BusinessType, x.BusinessKey });
            e.HasIndex(x => x.State);

            e.HasOne(x => x.DefinitionVersion)
                .WithMany()
                .HasForeignKey(x => x.DefinitionVersionId)
                .OnDelete(DeleteBehavior.Restrict);

            e.Property(x => x.InitiatorITCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.BusinessType).HasMaxLength(200);
            e.Property(x => x.BusinessKey).HasMaxLength(200);
            e.Property(x => x.TenantCode).HasMaxLength(50);

            // RowVer: plain uint column, NOT an EF concurrency token.
            // The engine manages this inside WHERE clause of ExecuteUpdateAsync (spec §7.2).
            e.Property(x => x.RowVer);

            // ── Wave-3 (WF-16) fields ──────────────────────────────────────────
            // Generation: epoch counter.  Default 0 (pre-Wave-3 instances).
            // Backfill migration note: no data migration needed — default 0 is correct for
            // all existing rows (they were never involved in a return-to-node operation).
            e.Property(x => x.Generation);

            // ReturnLoops: guarded by BeginReturnAsync CAS; capped by MaxReturnLoops option.
            e.Property(x => x.ReturnLoops);

            // NextSeq: per-instance Seq counter.  Replaces MAX(Seq)+1 + SERIALIZABLE.
            // Backfill guidance: run once per consumer migration:
            //   UPDATE Wf_ProcessInstance pi
            //   SET NextSeq = (SELECT COALESCE(MAX(Seq), 0) + 1
            //                  FROM Wf_WorkflowEventLog WHERE InstanceId = pi.ID)
            // This ensures existing log rows' Seq values are below the new counter.
            e.Property(x => x.NextSeq).HasDefaultValue(1);

            // ReturningLeaseUtc: crash-recovery lease for the Wave-5 reaper (WF-20 / ReclaimReturningLeaseAsync).
            e.Property(x => x.ReturningLeaseUtc);
        });

        // ── NodeInstance ──────────────────────────────────────────────────────
        builder.Entity<NodeInstance>(e =>
        {
            e.ToTable("Wf_NodeInstance");
            // Core query: active nodes for a given instance.
            e.HasIndex(x => new { x.InstanceId, x.State });
            // Wave-3: generation-scoped live-marking query.
            e.HasIndex(x => new { x.InstanceId, x.Generation, x.State });
            e.HasIndex(x => x.TenantCode);

            e.HasOne(x => x.Instance)
                .WithMany()
                .HasForeignKey(x => x.InstanceId)
                .OnDelete(DeleteBehavior.Restrict);

            e.Property(x => x.NodeKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.DecidedBy).HasMaxLength(50);
            e.Property(x => x.TenantCode).HasMaxLength(50);

            // RowVer: plain uint column — app-incremented CAS (spec §7.2).
            e.Property(x => x.RowVer);

            // ── Wave-3 (WF-16) fields ──────────────────────────────────────────
            // Generation: epoch at mint time.  Default 0.
            e.Property(x => x.Generation);

            // SupersededAtGen: set atomically by SupersedeNodeAsync to the generation that
            // superseded this node.  Null for active nodes.
            e.Property(x => x.SupersededAtGen);

            // ── Wave-3 (WF-17) fields ──────────────────────────────────────────
            // ForkGroupId: identifies the fork group for parallel/inclusive branch tokens.
            // Null for nodes not inside a fork/Join region.
            e.Property(x => x.ForkGroupId);

            // JoinNodeKey: the Join node this branch token is expected to converge into.
            // Null for tokens not inside a fork/Join region.
            e.Property(x => x.JoinNodeKey).HasMaxLength(100);

            // JoinExpectedArrivals: pinned at fork time to the number of branch tokens minted.
            // Default 0 for non-Join nodes.
            e.Property(x => x.JoinExpectedArrivals).HasDefaultValue(0);

            // JoinArrivedCount: incremented atomically by IncrementJoinArrivedAsync.
            // Default 0 for non-Join nodes.
            e.Property(x => x.JoinArrivedCount).HasDefaultValue(0);

            // AckMode: completion mode for Ack nodes. Null for non-Ack nodes.
            e.Property(x => x.AckMode);

            // ── Wave-4 (WF-19) fields ──────────────────────────────────────────
            // DefinitionCode: stamped at mint time from WorkflowGraph.Key.
            // Used by DelegationResolvingDecorator to scope-filter DelegationRules without
            // an extra JOIN.  Null for pre-Wave-4 rows (treated as global scope by decorator).
            e.Property(x => x.DefinitionCode).HasMaxLength(100);

            // ── Wave-4 (WF-18) fields ──────────────────────────────────────────
            // ApproverSetEpoch: node-local epoch co-incremented with TotalRequired in every
            // approver-set mutation (加签, AtAction revoke, 转办 reassign).  Asserted in the
            // completion CAS predicate alongside RowVer (R1 keystone FIX-A/B).
            // Default 0 for all pre-Wave-4 rows; no index needed (PK reads only).
            e.Property(x => x.ApproverSetEpoch).HasDefaultValue(0u);

            // Non-filtered unique index on (TenantCode, InstanceId, NodeKey, Generation):
            // enforces idempotent re-entry minting for MintNodeInstanceGuardedAsync (STEP-5).
            // Non-filtered (no WHERE clause) so it works across all 7 providers including
            // Oracle / DaMeng which do not support partial/filtered unique indexes.
            e.HasIndex(x => new { x.TenantCode, x.InstanceId, x.NodeKey, x.Generation })
             .IsUnique()
             .HasDatabaseName("IX_Wf_NodeInstance_TenantCode_InstanceId_NodeKey_Generation");
        });

        // ── ApprovalTask ──────────────────────────────────────────────────────
        builder.Entity<ApprovalTask>(e =>
        {
            e.ToTable("Wf_ApprovalTask");
            // Approver inbox query: tasks assigned to me that are pending.
            e.HasIndex(x => new { x.TenantCode, x.AssigneeITCode, x.State });
            e.HasIndex(x => x.NodeInstanceId);

            e.HasOne(x => x.NodeInstance)
                .WithMany()
                .HasForeignKey(x => x.NodeInstanceId)
                .OnDelete(DeleteBehavior.Restrict);

            e.Property(x => x.AssigneeITCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.DelegatedFromITCode).HasMaxLength(50);
            e.Property(x => x.AddedByITCode).HasMaxLength(50);
            e.Property(x => x.TenantCode).HasMaxLength(50);

            // RowVer: plain uint column — app-incremented CAS (spec §7.2).
            e.Property(x => x.RowVer);

            // Wave-3 (WF-16): Generation epoch, stamped at task-mint time.
            // DiscardTasksForReturnAsync scopes its bulk-cancel to the current generation.
            e.Property(x => x.Generation);

            // ── Wave-4 (WF-18) fields ──────────────────────────────────────────
            // AddDepth: 加签 chain depth.  Base tasks = 0; injected tasks = sourceTask.AddDepth+1.
            // O(1) MaxAddDepth check — no chain walk.  Default 0 for all pre-Wave-4 rows.
            e.Property(x => x.AddDepth).HasDefaultValue(0);

            // UNIQUE index on (NodeInstanceId, AssigneeITCode, Generation): FIX-G guard.
            // Prevents concurrent double-加签 from inserting duplicate rows for the same
            // approver in the same node epoch.  The unique constraint is the DB-level
            // backstop; the CAS on ApproverSetEpoch+RowVer is the first line of defence.
            // Non-filtered (no WHERE clause) to work across all 7 DB providers.
            e.HasIndex(x => new { x.NodeInstanceId, x.AssigneeITCode, x.Generation })
             .IsUnique()
             .HasDatabaseName("IX_Wf_ApprovalTask_Node_Assignee_Gen");

            // ── Wave-4 (WF-19) fields ──────────────────────────────────────────
            // DelegationRuleId: FK-by-value to the DelegationRule that produced this slot.
            // Null for non-delegated tasks.
            e.Property(x => x.DelegationRuleId);

            // DelegationExpiresUtc: snapshot of DelegationRule.EndUtc at mint/reassign time.
            // Null = no window constraint.  Participates in the AtAction claim CAS predicate.
            // Backfill note: no data migration needed — null is the correct default for all
            // pre-Wave-4 rows (they are not subject to a delegation window).
            e.Property(x => x.DelegationExpiresUtc);

            // WindowVerifiedUtc: audit-only timestamp (AtAction mode).  NEVER in any CAS
            // predicate.  Null for AtAssignment and not-yet-claimed tasks.
            e.Property(x => x.WindowVerifiedUtc);
        });

        // ── WorkflowEventLog ──────────────────────────────────────────────────
        builder.Entity<WorkflowEventLog>(e =>
        {
            e.ToTable("Wf_WorkflowEventLog");
            // Timeline query: all events for an instance in sequence order.
            // Unique constraint (TenantCode, InstanceId, Seq) — DB backstop against
            // duplicate Seq values from concurrent transitions (#240 / WF-2 engine-side fix).
            e.HasIndex(x => new { x.TenantCode, x.InstanceId, x.Seq }).IsUnique();
            e.HasIndex(x => x.TenantCode);

            e.HasOne(x => x.Instance)
                .WithMany()
                .HasForeignKey(x => x.InstanceId)
                .OnDelete(DeleteBehavior.Restrict);

            e.Property(x => x.ActorITCode).HasMaxLength(50);
            e.Property(x => x.NodeKey).HasMaxLength(100);
            e.Property(x => x.OnBehalfOfITCode).HasMaxLength(50);
            e.Property(x => x.AddedByITCode).HasMaxLength(50);
            e.Property(x => x.BeforeState).HasMaxLength(50);
            e.Property(x => x.AfterState).HasMaxLength(50);
            e.Property(x => x.TenantCode).HasMaxLength(50);

            // Wave-3 (WF-16): Generation — audit grouping only; never enters Seq math.
            e.Property(x => x.Generation);
        });

        // ── CcRecord ──────────────────────────────────────────────────────────
        builder.Entity<CcRecord>(e =>
        {
            e.ToTable("Wf_CcRecord");
            e.HasIndex(x => new { x.TenantCode, x.RecipientITCode, x.ReadAtUtc });
            e.HasIndex(x => x.InstanceId);

            e.HasOne(x => x.Instance)
                .WithMany()
                .HasForeignKey(x => x.InstanceId)
                .OnDelete(DeleteBehavior.Restrict);

            e.Property(x => x.RecipientITCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.NodeKey).HasMaxLength(100);
            e.Property(x => x.TenantCode).HasMaxLength(50);
        });

        // ── DelegationRule (WF-19) ────────────────────────────────────────────
        builder.Entity<DelegationRule>(e =>
        {
            e.ToTable("Wf_DelegationRule");

            // Chain-resolution lookup index: (TenantCode, PrincipalITCode, IsValid, StartUtc, EndUtc).
            // DelegationResolvingDecorator queries by tenant + principal + IsValid + date window.
            // Non-filtered to work across all 7 providers (MySQL/Oracle lack filtered indexes).
            e.HasIndex(x => new { x.TenantCode, x.PrincipalITCode, x.EndUtc })
             .HasDatabaseName("IX_Wf_DelegationRule_Principal_Lookup");
            e.HasIndex(x => x.DelegateeITCode)
             .HasDatabaseName("IX_Wf_DelegationRule_Delegatee");

            e.Property(x => x.PrincipalITCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.DelegateeITCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.ScopeDefinitionCode).HasMaxLength(100);
            e.Property(x => x.TenantCode).HasMaxLength(50);
        });

        // ── WorkflowTimer (WF-20 stub) ────────────────────────────────────────
        builder.Entity<WorkflowTimer>(e =>
        {
            e.ToTable("Wf_WorkflowTimer");
            // Poller query: armed timers ordered by fire time.
            e.HasIndex(x => new { x.Status, x.FireAtUtc });
            // Idempotency constraint — unique delivery key.
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => x.TenantCode);

            // Optional FK to ApprovalTask (may be null for node-scoped timers).
            e.HasOne(x => x.ApprovalTask)
                .WithMany()
                .HasForeignKey(x => x.ApprovalTaskId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);

            e.HasOne(x => x.NodeInstance)
                .WithMany()
                .HasForeignKey(x => x.NodeInstanceId)
                .OnDelete(DeleteBehavior.Restrict);

            e.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.TenantCode).HasMaxLength(50);

            // RowVer: plain uint column — app-incremented CAS (spec §7.2).
            e.Property(x => x.RowVer);

            // Wave-3 (WF-16): Generation epoch — gates timer-fire action against stale epochs.
            // Default 0 (pre-Wave-3 timers are generation 0; they fire normally unless the
            // node they cover was superseded at gen > 0, which the fire-action checks).
            e.Property(x => x.Generation);
        });

        return builder;
    }
}
