#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Services;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Engine.Routing;
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.Controllers;
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
        //    IProcessDefinitionPublisher — scoped (one per request).
        //    WTM's IDataContext in DI is always NullContext (the real DC is created transiently via
        //    WTMContext.DC).  Use IWtmDataContextFactory when available (production + AddWtmContext
        //    called); fall back to IDataContext for test setups that register a real context directly.
        services.AddScoped<IProcessDefinitionPublisher>(sp =>
        {
            var factory = sp.GetService<IWtmDataContextFactory>();
            var options = sp.GetService<IOptions<WorkFlowOptions>>();
            // #667 completion: wire the publisher's optional logger so the strategy-wrap's
            // deadlock-retry diagnostics are captured in production; falls back to NullLogger
            // (unchanged behaviour) when no logging provider is registered.
            var logger = sp.GetService<ILogger<ProcessDefinitionPublisher>>();
            if (factory != null)
                // #899 session-half: stamp the caller's ambient tenant (see ResolveAmbientTenant)
                // onto the DataContext this ctor creates via the factory -- the DI-fallback branch
                // below is untouched, that IDataContext's lifetime/tenant belongs to its own scope.
                return new ProcessDefinitionPublisher(factory, ResolveAmbientTenant(sp), options, logger);
            var dc = sp.GetRequiredService<IDataContext>();
            return new ProcessDefinitionPublisher(dc, options, logger);
        });

        // #727-followup (review of #727): register the per-scope DataContext holder BEFORE any
        // consumer factory needs it. TryAdd so AddWtmWorkFlowTimers (which also TryAdds it, in
        // case a host calls it without AddWtmWorkFlow — not the documented order, but harmless)
        // does not double-register. See ScopedWorkflowDataContextHolder for the full rationale.
        services.TryAddScoped<ScopedWorkflowDataContextHolder>();

        // 3. WF-6/7: IWorkflowEngine.
        //    #727: WorkflowEngine's production constructor takes IDataContext directly, which
        //    plain `AddScoped<IWorkflowEngine, WorkflowEngine>()` would let ASP.NET Core's
        //    constructor-injection resolve straight off the DI container — i.e. NullContext in
        //    every real deployment (see the IProcessDefinitionPublisher comment above for why).
        //    Route through the same IWtmDataContextFactory-first / IDataContext-fallback
        //    resolution as every other DC consumer in this file.
        //
        //    #727-followup: resolve via ScopedWorkflowDataContextHolder — NOT a direct
        //    ResolveDataContext(sp) call — so that when WorkflowTimerExecutor's factory ALSO
        //    resolves an engine from the same scope (WorkflowTimerHostedService.TickAsync), both
        //    consumers share ONE DbContext/DB connection. ownsDc is always false here: the holder
        //    (a scoped, IDisposable service) is the sole owner and is disposed by the DI container
        //    at scope-end, whether or not this factory's dc was the one that created it.
        services.AddScoped<IWorkflowEngine>(sp =>
        {
            var dc = sp.GetRequiredService<ScopedWorkflowDataContextHolder>().Resolve();
            var dispatcher = sp.GetRequiredService<INodeKindDispatcher>();
            var routingEvaluator = sp.GetRequiredService<IRoutingEvaluator>();
            var options = sp.GetRequiredService<IOptions<WorkFlowOptions>>();
            var logger = sp.GetRequiredService<ILogger<WorkflowEngine>>();
            var notifier = sp.GetService<IWorkflowNotifier>();
            var businessCalendar = sp.GetService<IBusinessCalendar>();
            var graphProvider = sp.GetService<IWorkflowGraphProvider>();
            var timeProvider = sp.GetService<TimeProvider>();
            return new WorkflowEngine(
                dc, dispatcher, routingEvaluator, options, logger,
                notifier, businessCalendar, graphProvider, timeProvider, ownsDc: false);
        });

        // 4. WF-8/9/10: IApproverResolver + IManagerChainProvider + approval mode handlers + dispatcher.
        //    All registered as scoped because handlers depend on IApproverResolver and
        //    IOptions<WorkFlowOptions> (request-scoped).
        // 5b. WF-11: IRoutingEvaluator — registered as SINGLETON because it caches compiled
        //    Expression<Func<IDictionary,bool>> delegates by content-hash. Thread-safe via
        //    ConcurrentDictionary. Must be singleton to share the compiled-predicate cache
        //    across all request-scoped engine instances.
        services.AddSingleton<IRoutingEvaluator, WhitelistRoutingEvaluator>();

        // #666: IWorkflowGraphProvider — singleton cache of deserialized WorkflowGraph documents,
        // keyed by DefinitionVersionId. Same rationale as IRoutingEvaluator above: ProcessDefinitionVersion
        // is immutable once published, so the deserialized graph is safe to share across every
        // scoped engine/timer-executor instance for the process lifetime. See IWorkflowGraphProvider.cs.
        services.AddSingleton<IWorkflowGraphProvider, WorkflowGraphProvider>();

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
                sp.GetRequiredService<ILogger<DelegationResolvingDecorator>>(),
                // #676: explicit resolve — this is a factory delegate, not auto constructor-injection,
                // so TimeProvider must be pulled from the container by hand to share the same clock
                // as WorkflowEngine and the other DI-constructed handlers (GetService, not
                // GetRequiredService — null is a valid "no host TimeProvider registered" case and the
                // decorator's own constructor already defaults to TimeProvider.System).
                sp.GetService<TimeProvider>()));
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
    /// Opt-in (Timeout wave). Adds the durable timer poller modeled on <c>EtlHostedService</c>.
    ///
    /// <para>Registers:</para>
    /// <list type="bullet">
    ///   <item><see cref="IBusinessCalendar"/> → <see cref="PassThroughBusinessCalendar"/> (default;
    ///     consumer overrides by calling <c>services.AddSingleton&lt;IBusinessCalendar, CustomImpl&gt;()</c>
    ///     AFTER this call — TryAdd means consumer registration wins).</item>
    ///   <item>Internal <c>WorkflowTimerExecutor</c> (scoped — one per tick scope).</item>
    ///   <item><see cref="WorkflowTimerHostedService"/> (BackgroundService singleton).</item>
    /// </list>
    ///
    /// <para><strong>Memory fail-fast:</strong> <c>WorkflowTimerHostedService</c> calls
    /// <see cref="ValidateDbType"/> unconditionally on first startup scope; Memory throws and
    /// .NET's default <c>BackgroundServiceExceptionBehavior.StopHost</c> terminates the host
    /// immediately (spec §9 invariant #8).</para>
    ///
    /// <para><strong>ITimeoutActionRegistry design-doc sketch was dropped</strong> (Wave-5 §0 verdict A):
    /// a closed <c>switch</c> on <c>TimerAction</c> inside the internal executor is used instead.
    /// An open extension point over engine-internal race surfaces is a correctness liability;
    /// it can be added additively later.</para>
    /// </summary>
    public static IServiceCollection AddWtmWorkFlowTimers(
        this IServiceCollection services)
    {
        // IBusinessCalendar: TryAdd so consumer override after this call wins.
        services.TryAddSingleton<IBusinessCalendar, PassThroughBusinessCalendar>();

        // #727-followup: see the matching registration + rationale in AddWtmWorkFlow. TryAdd here
        // too so AddWtmWorkFlowTimers() called without AddWtmWorkFlow() still gets a holder.
        services.TryAddScoped<ScopedWorkflowDataContextHolder>();

        // WorkflowTimerExecutor: scoped — one per tick scope.
        //    #727: same NullContext trap as IWorkflowEngine above — WorkflowTimerExecutor's
        //    production constructor also takes IDataContext directly. Route through the
        //    IWtmDataContextFactory-first / IDataContext-fallback resolution.
        //
        //    #727-followup (CRITICAL fix): the naive #727 fix called ResolveDataContext(sp) here
        //    AND let `sp.GetService<IWorkflowEngine>()` independently call ResolveDataContext(sp)
        //    again inside its own factory — IWtmDataContextFactory.CreateDC() has no per-scope
        //    caching, so that minted TWO separate DbContext/DB-connection instances within one
        //    scope. WorkflowTimerExecutor.Fire.cs opens a transaction on `db` (this executor's
        //    context) and, for AutoApprove/AutoReject, calls into the engine's
        //    SystemClaimTaskAsync — which without this fix ran on the ENGINE's own, different,
        //    NON-transactional connection (autocommit), breaking the documented
        //    IN-TXN-claim / POST-COMMIT-continuation atomicity contract (see Fire.cs's FIX-C
        //    comment and WorkflowEngine.System.cs's SystemClaimTaskAsync doc comment) and the
        //    deadlock-retry idempotency contract in WorkflowTransactionExecutor. Resolving the dc
        //    via the shared ScopedWorkflowDataContextHolder BEFORE resolving IWorkflowEngine
        //    guarantees both this executor and any engine resolved later in the same scope share
        //    the SAME DbContext — `sp.GetService<IWorkflowEngine>()` below hits the holder's cache
        //    (set by the Resolve() call on the line above it), not a fresh CreateDC() call.
        //    ownsDc is always false: the holder owns disposal (scope-end, container-driven).
        services.AddScoped<WorkflowTimerExecutor>(sp =>
        {
            var dc = sp.GetRequiredService<ScopedWorkflowDataContextHolder>().Resolve();
            var options = sp.GetRequiredService<IOptions<WorkFlowOptions>>();
            var logger = sp.GetRequiredService<ILogger<WorkflowTimerExecutor>>();
            var engine = sp.GetService<IWorkflowEngine>();
            var notifier = sp.GetService<IWorkflowNotifier>();
            var graphProvider = sp.GetService<IWorkflowGraphProvider>();
            return new WorkflowTimerExecutor(
                dc, options, logger, engine, notifier, graphProvider, ownsDc: false);
        });

        // WorkflowTimerHostedService: BackgroundService; unconditionally validates DBType on first scope.
        services.AddHostedService<WorkflowTimerHostedService>();

        return services;
    }

    // ── WF-21.2/3: Designer catalog service + antiforgery ────────────────────

    /// <summary>
    /// Opt-in. Registers designer catalog services: <see cref="IWorkflowDefinitionStore"/>,
    /// the ASP.NET Core <c>IAntiforgery</c> service (header: <c>X-WTM-WF-XSRF</c>), and
    /// <see cref="WorkFlowOptions.Designer"/> sub-options (WF-21.3).
    ///
    /// <para>Follows the <c>AddWtmWorkFlowNotifications</c> / <c>AddWtmWorkFlowTimers</c>
    /// opt-in family pattern — calling <see cref="AddWtmWorkFlow"/> alone changes nothing
    /// for hosts that do not call this method.</para>
    ///
    /// <para><strong>Antiforgery:</strong> calls <c>services.AddAntiforgery()</c> with
    /// <c>HeaderName = "X-WTM-WF-XSRF"</c>.  If the host has already called
    /// <c>AddAntiforgery()</c>, the <c>Configure</c> callback here simply amends the options.
    /// The global WTM antiforgery configuration is NOT broken — this is the first,
    /// designer-scoped registration (spec §4 / T-DSN-8).
    /// The designer bootstrap endpoint (<c>GET /_workflow/designer/bootstrap</c>)
    /// issues the token cookie; JS reads it and sends the header on mutating calls.</para>
    ///
    /// <para>No <c>BuildServiceProvider()</c> anywhere (10.9.0 startup-crash lesson).</para>
    /// </summary>
    public static IServiceCollection AddWtmWorkFlowDesigner(
        this IServiceCollection services,
        Action<DesignerOptions>? configureDesigner = null)
    {
        // IWorkflowDefinitionStore: scoped (one per request).
        //    WTM's IDataContext in DI is always NullContext (the real DC is created transiently via
        //    WTMContext.DC).  Use IWtmDataContextFactory when available (production + AddWtmContext
        //    called); fall back to IDataContext for test setups that register a real context directly.
        services.AddScoped<IWorkflowDefinitionStore>(sp =>
        {
            var factory = sp.GetService<IWtmDataContextFactory>();
            if (factory != null)
                // #899 session-half: same stamping as IProcessDefinitionPublisher above.
                return new WorkflowDefinitionStore(factory, ResolveAmbientTenant(sp));
            var dc = sp.GetRequiredService<IDataContext>();
            return new WorkflowDefinitionStore(dc);
        });

        // WF-21.3 / FIX-B3c: Register ASP.NET Core antiforgery configured to read the request token
        // from the designer's own header name (X-WTM-WF-XSRF).
        //
        // FIX-B3c root cause: the previous approach (FIX-B3b) injected the designer token into
        // "X-XSRF-TOKEN" before calling IAntiforgery.ValidateRequestAsync(), assuming "X-XSRF-TOKEN"
        // is the ASP.NET Core default HeaderName.  It is NOT — the framework default is
        // "RequestVerificationToken".  ValidateRequestAsync() reads from HeaderName, not from
        // "X-XSRF-TOKEN", so the injected token was never found → 400 on every mutating call.
        //
        // Correct approach: configure AntiforgeryOptions.HeaderName = "X-WTM-WF-XSRF" so
        // ValidateRequestAsync() reads the token directly from the designer header.
        // AddAntiforgery() is idempotent; Configure<AntiforgeryOptions>() merges into whatever
        // the host already registered.  If a host explicitly configured a different HeaderName
        // before calling AddWtmWorkFlowDesigner(), this Configure() call will override it — that
        // trade-off is intentional and acceptable for the designer feature.  Any host that needs
        // to keep a custom HeaderName for its own pipeline should configure antiforgery AFTER
        // calling AddWtmWorkFlowDesigner().
        services.AddAntiforgery();
        services.Configure<Microsoft.AspNetCore.Antiforgery.AntiforgeryOptions>(o =>
            o.HeaderName = Controllers.DesignerHeaderNames.Xsrf);

        // WF-21.3: Apply designer sub-options if provided.
        if (configureDesigner != null)
            services.Configure<WorkFlowOptions>(o => configureDesigner(o.Designer));

        return services;
    }

    // ── WF-21.4: Designer static-file seam ───────────────────────────────────

    /// <summary>
    /// Opt-in. Adds the static-file middleware seam that serves the embedded designer
    /// assets (CSS + JS modules) from the <c>WalkingTec.Mvvm.WorkFlow</c> assembly.
    ///
    /// <para>Registers a second <see cref="StaticFileOptions"/> instance at
    /// <c>/_workflow_designer/assets</c> backed by an
    /// <see cref="EmbeddedFileProvider"/> pointing at the WorkFlow assembly.
    /// This does NOT modify the existing <c>UseWtmStaticFiles()</c> provider at
    /// <c>/_js</c> — all existing Mvc framework assets are unaffected.</para>
    ///
    /// <para><strong>Asset URLs served by this seam (spec §8):</strong>
    /// <list type="bullet">
    ///   <item><c>/_workflow_designer/assets/framework_workflow_designer.css</c></item>
    ///   <item><c>/_workflow_designer/assets/framework_workflow_designer_core.js</c></item>
    ///   <item><c>/_workflow_designer/assets/framework_workflow_designer_forms.js</c></item>
    ///   <item><c>/_workflow_designer/assets/framework_workflow_designer_view.js</c></item>
    /// </list>
    /// Note: the request path uses an underscore (<c>/_workflow_designer</c>), not a hyphen,
    /// because <see cref="EmbeddedFileProvider"/> mangles hyphens in resource paths.</para>
    ///
    /// <para><strong>ETag / caching:</strong> the <see cref="StaticFileOptions"/> here uses
    /// framework defaults (ETag based on last-write, conditional-get).  Consumers who need
    /// aggressive caching should add <c>ResponseCachingMiddleware</c> or a CDN layer on top
    /// — this method does NOT mutate <c>WtmETagOptions</c> or any global static-file default.</para>
    ///
    /// <para>Call after <c>UseWtmStaticFiles()</c> and before <c>UseEndpoints()</c>.</para>
    /// </summary>
    public static IApplicationBuilder UseWtmWorkFlowDesigner(this IApplicationBuilder app)
    {
        // EmbeddedFileProvider with the WorkFlow assembly + base namespace.
        // Files under designer\ are accessible as if served from /_workflow_designer/assets/.
        // The base namespace matches the assembly name so the provider resolves:
        //   designer\framework_workflow_designer.css
        //   → WalkingTec.Mvvm.WorkFlow.designer.framework_workflow_designer.css
        var assembly = typeof(WorkflowDesignerPageController).Assembly;

        // Content-type provider with explicit JS module MIME type so browsers do not
        // reject type="module" scripts with a wrong MIME type.
        var contentTypeProvider = new FileExtensionContentTypeProvider();
        contentTypeProvider.Mappings[".js"] = "application/javascript";
        contentTypeProvider.Mappings[".css"] = "text/css";
        contentTypeProvider.Mappings[".html"] = "text/html; charset=utf-8";

        app.UseStaticFiles(new StaticFileOptions
        {
            RequestPath = new PathString("/_workflow_designer/assets"),
            FileProvider = new EmbeddedFileProvider(
                assembly,
                "WalkingTec.Mvvm.WorkFlow.designer"),
            ContentTypeProvider = contentTypeProvider
        });

        return app;
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

    /// <summary>
    /// #727 (audit of the #721 IDataContext→NullContext DI-resolution gap): the single
    /// resolution helper every WorkFlow-module DC consumer in this file should route through.
    /// <para>
    /// WTM's <c>IDataContext</c> in DI is always <see cref="NullContext"/> — <c>AddWtmContext</c>
    /// only ever registers <c>services.TryAddScoped&lt;IDataContext, NullContext&gt;()</c> as a
    /// safe placeholder default; the real, connection-string/tenant-routed DataContext is
    /// created transiently via <see cref="IWtmDataContextFactory.CreateDC"/> (the same mechanism
    /// <c>WTMContext.DC</c> uses). Before #727, <see cref="WorkflowEngine"/> and
    /// <see cref="WorkflowTimerExecutor"/> were registered with plain
    /// <c>services.AddScoped&lt;T&gt;()</c>, which let ASP.NET Core's automatic
    /// constructor-injection resolve their <c>IDataContext</c> parameter straight off the DI
    /// container — i.e. <see cref="NullContext"/> in every real deployment, immediately throwing
    /// <see cref="InvalidCastException"/> ("Unable to cast object of type 'NullContext' to type
    /// 'DbContext'") the first time either type was constructed. This was masked because no
    /// existing test constructed either type through a real ASP.NET Core DI container built by
    /// <c>AddWtmWorkFlow()</c> — engine tests use the internal direct-<c>DbContext</c>
    /// constructor and controller tests mock <see cref="IWorkflowEngine"/> entirely, so the
    /// broken production registration itself was never exercised (see
    /// <c>ProdDiReproTests</c>).
    /// </para>
    /// <para>
    /// Returns <c>Owned = true</c> when this call created a fresh DataContext via the factory —
    /// the caller must dispose it. Returns <c>Owned = false</c> for the DI-fallback path (hosts
    /// or tests that explicitly re-register a real <c>IDataContext</c> without registering
    /// <see cref="IWtmDataContextFactory"/>) — that instance's lifetime is owned by the caller's
    /// DI scope, not by us.
    /// </para>
    /// <para>
    /// <strong>#899 session-half:</strong> <c>IWtmDataContextFactory.CreateDC()</c> is called here
    /// with NO arguments, so the DataContext it returns always writes to the module's default
    /// connection with <c>TenantCode == null</c> at creation time (unlike <c>WTMContext.DC</c>,
    /// whose OWN <c>CreateDC()</c> instance method resolves <c>_loginUserInfo?.CurrentTenant</c>
    /// and stamps it before returning — see <c>WTMContext.CreateDC.cs</c>). The #899 fix wired an
    /// <see cref="ITenant"/> query filter into <c>ApplyWorkFlowModels(this)</c> that binds to
    /// THIS context instance's own <c>TenantCode</c>; without a stamp here that filter is
    /// permanently bound to <c>null</c>, so a migrated multi-tenant consumer's module reads
    /// return zero rows regardless of who is asking. The factory-path branch below now stamps
    /// the caller's ambient tenant (<see cref="ResolveAmbientTenant"/>) onto the freshly-created
    /// <paramref name="sp"/>-scoped DataContext — deliberately via <c>SetTenantCode</c> AFTER
    /// <c>CreateDC()</c>, never via <c>CreateDC(currentTenant: ...)</c>, because that parameter
    /// re-routes a tenant with <c>IsUsingDB == true</c> to a DIFFERENT physical database
    /// (<c>WtmDataContextFactory.CreateDC</c>'s <c>CreateTenantDC</c> branch) — this module
    /// always writes <c>Wf_*</c> tables to the default connection today, and smuggling a
    /// connection re-route into a filter fix would be its own compatibility break. When no
    /// ambient <see cref="WTMContext"/>/<c>LoginUserInfo</c> is available (the background
    /// timer scope has no <c>HttpContext</c>), <see cref="ResolveAmbientTenant"/> resolves to
    /// <c>null</c> — the exact same value this path always produced before, so the background
    /// path is byte-identical.
    /// </para>
    /// </summary>
    internal static (IDataContext Dc, bool Owned) ResolveDataContext(IServiceProvider sp)
    {
        var factory = sp.GetService<IWtmDataContextFactory>();
        var dc = factory?.CreateDC();
        if (dc != null)
        {
            dc.SetTenantCode(ResolveAmbientTenant(sp));
            WarnIfTenantFilterMissing(dc, sp);
            return (dc, true);
        }

        return (sp.GetRequiredService<IDataContext>(), false);
    }

    /// <summary>
    /// #899 session-half: resolves the tenant the CURRENT DI scope is acting on behalf of, the
    /// same way every other WTM-framework DataContext (<c>WTMContext.DC</c>,
    /// <c>WtmDataContextFactory.CreateDC</c>'s own callers) does — by reading the scoped
    /// <see cref="WTMContext"/>'s <c>LoginUserInfo.CurrentTenant</c>, never by re-deriving tenant
    /// resolution independently (that would drift from <c>WTMContext</c>'s own logic, including
    /// its #116 Referer-based-resolution security fix, the moment either one changes).
    /// <para>
    /// <see cref="WTMContext"/> is registered <c>AddScoped</c> (<c>IServiceExtension.cs</c> /
    /// <c>FrameworkServiceExtension.cs</c>), so <c>sp.GetService&lt;WTMContext&gt;()</c> resolves
    /// the SAME instance every other consumer in this DI scope sees. On a real HTTP request,
    /// WTM's middleware pipeline has already called <c>EnsureLoginUserInfoAsync()</c> before any
    /// controller (and therefore before any WorkFlow service) runs, so <c>LoginUserInfo</c> is a
    /// plain field read here, never a blocking reload.
    /// </para>
    /// <para>
    /// On a scope with no <see cref="Microsoft.AspNetCore.Http.HttpContext"/> (the background
    /// timer tick scope — <c>WorkflowTimerHostedService.TickAsync</c> creates a bare DI scope,
    /// not an HTTP request scope), <see cref="WTMContext.LoginUserInfo"/>'s getter short-circuits
    /// to <c>null</c> (<c>WTMContext.User.cs</c>) — so this method returns <c>null</c> there,
    /// identical to what <see cref="ResolveDataContext"/> always produced before this fix. The
    /// background reaper/sweep code paths remain per-candidate <c>SetTenantCode</c>-driven
    /// (<c>WorkflowTimerExecutor.*.cs</c>) exactly as before; this method is never called a
    /// second time to "helpfully" give the background scope itself a tenant.
    /// </para>
    /// </summary>
    internal static string? ResolveAmbientTenant(IServiceProvider sp)
        => sp.GetService<WTMContext>()?.LoginUserInfo?.CurrentTenant;

    // #899 session-half: process-wide, fire-at-most-once flag for WarnIfTenantFilterMissing.
    // 0 = not yet checked; any non-zero Interlocked.Exchange result means another thread/request
    // already ran (or is running) the check — this call returns immediately without logging
    // again. Deliberately process-lifetime, not per-scope/per-request: the model shape (whether
    // ApplyWorkFlowModels(this) was migrated) cannot change at runtime, so re-checking on every
    // request would be pure overhead for a fact that is fixed at process startup.
    private static int _tenantFilterCheckLogged;

    /// <summary>
    /// #899 session-half self-check: the first time a factory-created module DataContext is
    /// resolved in this process, inspect whether the consumer's <c>DataContext.OnModelCreating</c>
    /// has actually migrated to <c>ApplyWorkFlowModels(this)</c> — i.e. whether
    /// <see cref="ProcessDefinition"/> has an active EF Core query filter — and log once if not.
    /// Un-migrated consumers keep working unfiltered (Compatibility red line — see the
    /// <c>[Obsolete]</c> overload's own doc comment); this makes that state loud instead of
    /// silent, without changing behaviour.
    /// <para>
    /// <c>LogError</c> when <see cref="GlobalData.AllTenant"/> is non-empty — a real multi-tenant
    /// deployment running genuinely unprotected, the worst case this can detect. <c>LogWarning</c>
    /// otherwise (single-tenant deployments are only missing the soft-delete half; still worth
    /// flagging, not an emergency).
    /// </para>
    /// </summary>
    internal static void WarnIfTenantFilterMissing(IDataContext dc, IServiceProvider sp)
    {
        if (Interlocked.Exchange(ref _tenantFilterCheckLogged, 1) != 0)
        {
            return;
        }

        // Only a real DbContext exposes .Model; NullContext/other IDataContext test doubles
        // cannot be inspected this way -- nothing to warn about if we cannot even ask.
        if (dc is not DbContext dbContext)
        {
            return;
        }

        // GetDeclaredQueryFilters() (not the obsolete single-filter GetQueryFilter()) -- matches
        // the idiom TenantFilterInvariantTests.cs already uses to inspect the same model metadata.
        var filters = dbContext.Model.FindEntityType(typeof(ProcessDefinition))?.GetDeclaredQueryFilters();
        var hasFilter = filters != null && filters.Any();
        if (hasFilter)
        {
            return;
        }

        var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("WalkingTec.Mvvm.WorkFlow.TenantFilterCheck");
        var isMultiTenantDeployment = (sp.GetService<GlobalData>()?.AllTenant?.Count ?? 0) > 0;

        const string message =
            "#899: this WorkFlow module DataContext has no ITenant/soft-delete query filter " +
            "active -- your DataContext.OnModelCreating is still calling the obsolete zero-argument " +
            "modelBuilder.ApplyWorkFlowModels() overload. Change it to " +
            "modelBuilder.ApplyWorkFlowModels(this) to enable tenant isolation and soft-delete " +
            "filtering for the 10 WorkFlow entities. This message is logged once per process.";

        if (isMultiTenantDeployment)
        {
            logger?.LogError(message);
        }
        else
        {
            logger?.LogWarning(message);
        }
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
/// #727-followup (review of #727's CRITICAL/HIGH regression): a scoped, per-DI-scope cache
/// wrapping <see cref="ServiceCollectionExtensions.ResolveDataContext"/> so that every
/// WorkFlow-module consumer resolved from the SAME DI scope that calls
/// <see cref="Resolve"/> gets the exact same <see cref="IDataContext"/>/<see cref="DbContext"/>
/// instance — i.e. the same DB connection — instead of a fresh one per call.
///
/// <para>
/// <strong>Why this exists:</strong> <c>IWtmDataContextFactory.CreateDC()</c> is unconditionally
/// non-caching — every call mints a brand-new <c>DbContext</c>. Before this fix,
/// <c>IWorkflowEngine</c>'s and <c>WorkflowTimerExecutor</c>'s DI factories each called
/// <c>ResolveDataContext(sp)</c> independently, so within one scope (the production
/// <c>WorkflowTimerHostedService.TickAsync</c> path) they ended up on two separate DbContext
/// instances / DB connections. <c>WorkflowTimerExecutor.Fire.cs</c> opens a transaction on its
/// own DbContext and, for AutoApprove/AutoReject, calls into the engine's
/// <c>SystemClaimTaskAsync</c> — which, on a different connection with no open transaction,
/// autocommitted outside the executor's transaction, breaking the documented
/// IN-TXN-claim / POST-COMMIT-continuation atomicity contract (see
/// <c>WorkflowEngine.System.cs</c>'s <c>SystemClaimTaskAsync</c> doc comment: "MUST be called
/// inside an open transaction") and the deadlock-retry idempotency contract that
/// <c>WorkflowTransactionExecutor.ExecuteInTransactionAsync</c> depends on. This mirrors
/// (and was caught by) the same cross-connection-atomicity failure mode the pre-existing
/// <c>WorkflowTimerExecutor.Fire.cs</c> comment already assumed did NOT happen ("The engine
/// resolved from the same DI scope shares this DbContext instance").
/// </para>
///
/// <para>
/// <strong>Lifetime/disposal:</strong> registered scoped (<c>TryAddScoped</c>) in both
/// <see cref="ServiceCollectionExtensions.AddWtmWorkFlow"/> and
/// <see cref="ServiceCollectionExtensions.AddWtmWorkFlowTimers"/>. Because this holder itself
/// implements <see cref="IDisposable"/> and is resolved through the DI container, the container
/// tracks and disposes it automatically at scope-end — which disposes the DataContext it created
/// (factory path) exactly once. Consumers (<c>WorkflowEngine</c>, <c>WorkflowTimerExecutor</c>)
/// are constructed with <c>ownsDc: false</c> — this holder is now the sole owner for the
/// factory-created case; the DI-fallback case (a host/test that registered a real
/// <c>IDataContext</c> directly, no <c>IWtmDataContextFactory</c>) is never disposed here either
/// way, matching pre-existing behavior (that instance's lifetime belongs to its own
/// <c>AddScoped&lt;IDataContext,...&gt;</c> registration).
/// </para>
/// </summary>
internal sealed class ScopedWorkflowDataContextHolder : IDisposable
{
    private readonly IServiceProvider _sp;
    private (IDataContext Dc, bool Owned)? _resolved;

    public ScopedWorkflowDataContextHolder(IServiceProvider sp)
    {
        _sp = sp;
    }

    /// <summary>
    /// Returns the shared <see cref="IDataContext"/> for the current DI scope, creating it via
    /// <see cref="ServiceCollectionExtensions.ResolveDataContext"/> on the first call and caching
    /// it for every subsequent call within the same scope (and therefore the same
    /// <see cref="ScopedWorkflowDataContextHolder"/> instance).
    /// </summary>
    public IDataContext Resolve()
    {
        _resolved ??= ServiceCollectionExtensions.ResolveDataContext(_sp);
        return _resolved.Value.Dc;
    }

    /// <summary>
    /// Disposes the DataContext this holder created via the factory path
    /// (<c>Owned == true</c>). No-op when <see cref="Resolve"/> was never called, or when it
    /// returned the DI-fallback instance (<c>Owned == false</c> — that instance's lifetime is
    /// owned by its own DI registration, not by us).
    /// </summary>
    public void Dispose()
    {
        if (_resolved is { Owned: true } resolved)
        {
            resolved.Dc.Dispose();
        }
    }
}

/// <summary>
/// EF Core ModelBuilder 擴充 — 註冊 WorkFlow 資料模型
/// </summary>
public static class WorkFlowDbContextExtensions
{
    /// <summary>
    /// Called from the CONSUMER's <c>DataContext.OnModelCreating</c> (NOT from FrameworkContext)
    /// to register all WorkFlow tables, indexes, FK relationships, per-provider RowVer mapping,
    /// AND the <see cref="ITenant"/> (plus, for <c>PersistPoco</c> entities, soft-delete) global
    /// query filter for every WorkFlow entity.
    /// </summary>
    /// <remarks>
    /// <para><b>Issue #899, resolved:</b> the (now obsolete)
    /// <see cref="ApplyWorkFlowModels(ModelBuilder)"/> zero-argument overload registers WorkFlow
    /// entity types via <c>modelBuilder.Entity&lt;T&gt;()</c>, but every documented call site
    /// (<c>docs/workflow.md</c>, <c>demo/WalkingTec.Mvvm.Demo/DataContext.cs</c>) invokes it from
    /// the consumer's own <c>DataContext.OnModelCreating</c> AFTER
    /// <c>base.OnModelCreating(modelBuilder)</c> returns. By the time that base call returns,
    /// <c>FrameworkContext.OnModelCreating</c>'s own Pass 2 loop
    /// (<c>src/WalkingTec.Mvvm.Core/DataContext.cs</c>) has ALREADY finished iterating
    /// <c>modelBuilder.Model.GetEntityTypes()</c> and applying the <see cref="ITenant"/> /
    /// <c>IPersistPoco</c> global query filters for every entity type known to the model AT THAT
    /// POINT — it can never retroactively see an entity type registered afterward. All 10
    /// WorkFlow entities implement <see cref="ITenant"/>, but the filter was never actually
    /// enforced through a standard <c>FrameworkContext</c>-derived app: a context scoped to
    /// tenant A could read tenant B's <c>ProcessDefinition</c>/<c>ApprovalTask</c>/etc. row by id
    /// with no filter applied at all — same root cause as #862's <c>ApplyEtlModels()</c> defect,
    /// one module over (confirmed, not assumed — see the SQLite-fixture behavioural tests in
    /// <c>TenantFilterInvariantTests.cs</c>).</para>
    ///
    /// <para><b>Not a copy of ETL's fix — the filter composition differs.</b> Core's Pass 2
    /// applies a SINGLE combined filter, <c>IsValid == true &amp;&amp; TenantCode ==
    /// this.TenantCode</c>, for any entity that is both <c>IPersistPoco</c> and
    /// <see cref="ITenant"/> (<c>DataContext.cs:238-258</c>, via <c>Expression.AndAlso</c>) — a
    /// tenant-only filter is only correct for a <c>BasePoco</c>-only entity. ETL's
    /// <c>ApplyEtlTenantFilter&lt;T&gt;</c> applies tenant-only because all four ETL entities are
    /// <c>BasePoco</c>. WorkFlow has BOTH kinds among its 10 <see cref="ITenant"/> entities:</para>
    /// <para>
    /// <c>PersistPoco, ITenant</c> (6): <see cref="ProcessDefinition"/>,
    /// <see cref="ProcessInstance"/>, <see cref="ProcessDefinitionVersion"/>,
    /// <see cref="ProcessDefinitionDraft"/>, <see cref="ApprovalTask"/>,
    /// <see cref="DelegationRule"/>.
    /// </para>
    /// <para>
    /// <c>BasePoco, ITenant</c> (4): <see cref="NodeInstance"/>, <see cref="WorkflowTimer"/>,
    /// <see cref="CcRecord"/>, <see cref="WorkflowEventLog"/>.
    /// </para>
    /// <para>Copying ETL's tenant-only helper here would have left the 6 <c>PersistPoco</c>
    /// entities without their soft-delete filter. <c>ApplyWorkFlowTenantFilter&lt;T&gt;</c>
    /// below re-derives the combined-vs-tenant-only choice per entity from its own
    /// <c>IPersistPoco</c>-ness at the call site, exactly like Pass 2 does, instead of assuming
    /// ETL's all-<c>BasePoco</c> shape. <b>This widens the defect's impact beyond multi-tenant
    /// deployments</b>: soft-delete filter absence for the 6 <c>PersistPoco</c> entities affects
    /// SINGLE-tenant deployments too — today every framework-standard query sees soft-deleted
    /// <c>ProcessDefinition</c>/<c>ApprovalTask</c>/<c>DelegationRule</c> rows regardless of
    /// tenancy.</para>
    ///
    /// <para><b>No migration needed.</b> All 10 entities already implement <see cref="ITenant"/>
    /// and their <c>TenantCode</c> columns/indexes are already registered in
    /// <c>ApplyWorkFlowModelsCore</c> below (unchanged by this fix) — this overload only changes
    /// which <c>HasQueryFilter</c> calls run at model-build time, not the schema. A query filter
    /// is EF metadata, never emitted into a migration/DDL diff.</para>
    ///
    /// <para><b>Engine background writes</b> (<c>WorkflowTimerExecutor.*</c>,
    /// <c>NodeKindHandlers</c>, etc.) stamp <c>TenantCode</c> at every real persisted write site
    /// — verified by enumerating every <c>new NodeInstance/ApprovalTask/WorkflowEventLog/
    /// WorkflowTimer/CcRecord/ProcessInstance/ProcessDefinition*</c> construction under
    /// <c>src/WalkingTec.Mvvm.WorkFlow/Engine</c> and <c>Definition</c> that is actually
    /// persisted (<c>db.Set&lt;T&gt;().Add(...)</c> / <c>_dc.AddEntity(...)</c>): every one
    /// assigns <c>TenantCode</c> from an already-tenant-scoped source (the owning
    /// <c>ProcessInstance</c>/<c>NodeInstance</c>/<c>ProcessDefinition</c>, or a snapshot
    /// variable captured from one). The handful of constructions that do NOT set
    /// <c>TenantCode</c> (the "for notifier" shells inside
    /// <c>WorkflowTimerExecutor.*.NotifyXxxAsync</c>) are transient objects passed straight to a
    /// notifier call and never added to a <c>DbSet</c> — confirmed by reading each call site, not
    /// inferred from naming.</para>
    ///
    /// <para><b>Engine query filter activation</b> (30+ <c>IgnoreQueryFilters()</c> call sites in
    /// <c>WorkflowTimerExecutor.*</c>): this fix activates those calls from no-op to real —
    /// today, with no filter registered, <c>IgnoreQueryFilters()</c> is a documented no-op. The
    /// bare (unnamed) form is deliberately kept rather than switching to EF Core 10's named query
    /// filters: every one of those sites is a cross-TENANT system sweep (background reaper/timer
    /// executor with no per-request identity), and TODAY — with no filter active at all — those
    /// sweeps already see <c>IsValid == false</c> rows too. Composing the new combined filter
    /// with a bare <c>IgnoreQueryFilters()</c> therefore PRESERVES that existing soft-delete
    /// visibility for the sweeps (only the tenant dimension changes, from "always ignored because
    /// nothing existed to ignore" to "explicitly ignored because the sweep is cross-tenant by
    /// design") instead of silently narrowing sweep visibility to <c>IsValid == true</c> rows as
    /// an accidental side effect of an unrelated fix. This is a decided choice, not an
    /// oversight — narrowing sweep visibility to non-soft-deleted rows only would be a separate,
    /// deliberately-scoped behaviour change, not a consequence of wiring the tenant filter
    /// correctly.</para>
    ///
    /// <para><b>Why <c>ApplyDashboardModels</c></b> (<c>Dashboard/EfCoreDashboardDbContext.cs</c>)
    /// <b>is not touched by this fix:</b> it is the same zero-argument extension-method shape and
    /// would have the identical defect if its entities implemented <see cref="ITenant"/> — they
    /// use a hand-rolled <c>TenantId</c> string property instead, so there is no <c>ITenant</c>
    /// filter to fail to apply and no HasQueryFilter gap for this issue to close there.
    /// Tracked structurally, not per-component, by #901 (the <c>IModelFinalizingConvention</c>
    /// class-level fix): if Dashboard entities are ever changed to implement <c>ITenant</c>,
    /// #901's model-finalizing convention would close the gap automatically, whereas a third
    /// <c>ApplyDashboardModels(this ModelBuilder, EmptyContext)</c> overload copy-pasted from
    /// this one would not be a good use of #899's "targeted stop-the-bleeding" scope.</para>
    ///
    /// <para><b>EF migrations — consumer owns them.</b>
    /// <c>WalkingTec.Mvvm.WorkFlow</c> ships ZERO migrations (same as Etl).
    /// Generate migrations in your consumer application:
    /// <code>
    /// dotnet ef migrations add WorkFlow_InitialCreate \
    ///   --context DataContext \
    ///   --project &lt;YourApp&gt;/&lt;YourApp&gt;.csproj \
    ///   --startup-project &lt;YourApp&gt;/&lt;YourApp&gt;.csproj
    /// </code>
    /// This creates 10 tables: Wf_ProcessDefinition, Wf_ProcessDefinitionVersion,
    /// Wf_ProcessInstance, Wf_NodeInstance, Wf_ApprovalTask, Wf_WorkflowEventLog,
    /// Wf_CcRecord, Wf_DelegationRule, Wf_WorkflowTimer, Wf_ProcessDefinitionDraft (#899
    /// correction: this comment previously said 9 tables — it undercounted the WF-21.3 draft
    /// table, added after the "9 tables" line was first written).
    /// </para>
    ///
    /// <para><b>RowVer concurrency mapping (WF-3, spec §7.2):</b>
    /// <c>uint RowVer</c> is mapped as a plain property — NOT as an EF concurrency token.
    /// The engine manages it inside the WHERE clause of <c>ExecuteUpdateAsync</c>
    /// (app-incremented, portable CAS pattern from WF-0 / TokenService).
    /// The provider is detected at model-build time via <c>Database.ProviderName</c>.
    /// </para>
    /// </remarks>
    /// <param name="builder">ModelBuilder from your DataContext.OnModelCreating.</param>
    /// <param name="context">
    /// Pass <c>this</c> from your DataContext.OnModelCreating override. Required so the
    /// TenantCode filter binds to the CURRENT context instance's TenantCode at query time
    /// (EF Core's supported "DbContext instance access in query filters" idiom) instead of a
    /// value that would otherwise be impossible to obtain from a ModelBuilder-only extension
    /// method.
    /// </param>
    public static ModelBuilder ApplyWorkFlowModels(this ModelBuilder builder, EmptyContext context)
    {
        ApplyWorkFlowModelsCore(builder);

        // #899: apply the combined (IsValid + TenantCode) filter for every WorkFlow
        // PersistPoco/ITenant entity, and the tenant-only filter for every BasePoco/ITenant
        // entity, right here -- instead of relying on FrameworkContext.OnModelCreating's Pass 2
        // (which never sees these types -- see remarks above). ApplyWorkFlowTenantFilter<T>
        // decides combined-vs-tenant-only per T from IPersistPoco-ness, same as Pass 2 does.
        ApplyWorkFlowTenantFilter<ProcessDefinition>(builder, context);
        ApplyWorkFlowTenantFilter<ProcessDefinitionVersion>(builder, context);
        ApplyWorkFlowTenantFilter<ProcessDefinitionDraft>(builder, context);
        ApplyWorkFlowTenantFilter<ProcessInstance>(builder, context);
        ApplyWorkFlowTenantFilter<ApprovalTask>(builder, context);
        ApplyWorkFlowTenantFilter<DelegationRule>(builder, context);
        ApplyWorkFlowTenantFilter<NodeInstance>(builder, context);
        ApplyWorkFlowTenantFilter<WorkflowTimer>(builder, context);
        ApplyWorkFlowTenantFilter<CcRecord>(builder, context);
        ApplyWorkFlowTenantFilter<WorkflowEventLog>(builder, context);

        return builder;
    }

    /// <summary>
    /// 舊版多載（無 <see cref="EmptyContext"/> 參數）— 僅為向下相容保留，行為與升級前完全一致：
    /// 只註冊資料表結構、索引、FK 關聯與 RowVer 對應，<b>不會</b>套用 <see cref="ITenant"/> 全域
    /// 查詢過濾器（含 6 個 <c>PersistPoco</c> entity 的 soft-delete 過濾器）。這正是 #899 描述的
    /// 問題本身——本多載沒有管道可以取得目前的 context 執行個體，因此無法修正。請改用
    /// <see cref="ApplyWorkFlowModels(ModelBuilder, EmptyContext)"/>。
    /// </summary>
    [Obsolete("ApplyWorkFlowModels() without a DbContext instance can never apply the ITenant " +
              "global query filter (or, for the 6 PersistPoco entities, the IsValid soft-delete " +
              "filter) to any of the 10 WorkFlow entities (issue #899) -- table/column/index " +
              "registration is unchanged and still runs, but tenant isolation and soft-delete " +
              "filtering for these tables do not. Call ApplyWorkFlowModels(this) from your " +
              "DataContext.OnModelCreating instead.")]
    public static ModelBuilder ApplyWorkFlowModels(this ModelBuilder builder)
    {
        return ApplyWorkFlowModelsCore(builder);
    }

    private static ModelBuilder ApplyWorkFlowModelsCore(ModelBuilder builder)
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

            // C16 fix: map ApprovePercent with explicit precision so ratio quorum
            // thresholds are not truncated under decimal(18,2) on SqlServer/MySQL/Oracle.
            // Precision (5,4) stores values like 0.6667 accurately (e.g. ceil(n*0.6667)).
            e.Property(x => x.ApprovePercent).HasPrecision(5, 4);

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

        // ── ProcessDefinitionDraft (WF-21.3) ─────────────────────────────────────
        //
        // Consumer migration note:
        //   One new table added: Wf_ProcessDefinitionDraft.
        //   Generate a migration in your consumer project:
        //     dotnet ef migrations add WorkFlow_AddDraftStore ...
        //
        //   This table stores in-progress (draft) edits of workflow definitions.
        //   The engine never reads this table; it is deleted atomically by
        //   ProcessDefinitionPublisher.PublishRawAsync when a draft is published.
        builder.Entity<ProcessDefinitionDraft>(e =>
        {
            e.ToTable("Wf_ProcessDefinitionDraft");

            // One draft per (TenantCode, DefinitionId) — unique index.
            // Non-filtered (no WHERE clause) to work across all 7 providers.
            e.HasIndex(x => new { x.TenantCode, x.DefinitionId })
             .IsUnique()
             .HasDatabaseName("IX_Wf_ProcessDefinitionDraft_Tenant_Definition");

            e.HasOne(x => x.Definition)
                .WithMany()
                .HasForeignKey(x => x.DefinitionId)
                .OnDelete(DeleteBehavior.Restrict);

            e.Property(x => x.TenantCode).HasMaxLength(50);
            e.Property(x => x.GraphJson).IsRequired();
            e.Property(x => x.BaseContentHash).HasMaxLength(64);
            e.Property(x => x.LastSavedBy).HasMaxLength(50);

            // RowVersion: plain uint column — app-incremented CAS (spec §7.2 / WF-2/3 pattern).
            // NOT an EF concurrency token; managed entirely by the application.
            e.Property(x => x.RowVersion);
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

    /// <summary>
    /// Applies the SAME filter shape <c>DataContext.cs</c>'s Pass 2 loop uses for a
    /// <c>TopBasePoco</c> descendant: the combined <c>IsValid == true &amp;&amp; TenantCode ==
    /// this.TenantCode</c> filter for a type that is ALSO <c>IPersistPoco</c> (6 of the 10
    /// WorkFlow entities), or the tenant-only filter for a <c>BasePoco</c>-only
    /// <see cref="ITenant"/> type (the other 4). See the remarks on
    /// <see cref="ApplyWorkFlowModels(ModelBuilder, EmptyContext)"/> for why this needs to live
    /// here rather than relying on the base context's own filter application, and why it is NOT
    /// simply a copy of ETL's tenant-only <c>EtlDbContextExtensions.ApplyEtlTenantFilter&lt;T&gt;</c>.
    /// </summary>
    private static void ApplyWorkFlowTenantFilter<T>(ModelBuilder builder, EmptyContext context)
        where T : class, ITenant
    {
        var pe = Expression.Parameter(typeof(T));
        var conditions = new List<Expression>();

        // Mirrors DataContext.cs Pass 2's ordering exactly (IsValid first, then TenantCode) --
        // only entities that are ALSO IPersistPoco get the soft-delete half of the filter. This
        // is the one place this method's logic diverges from ETL's ApplyEtlTenantFilter<T>,
        // which always applies tenant-only because every ETL entity is BasePoco (no IPersistPoco
        // entity exists in that module to get wrong).
        if (typeof(IPersistPoco).IsAssignableFrom(typeof(T)))
        {
            conditions.Add(Expression.Equal(Expression.Property(pe, "IsValid"), Expression.Constant(true)));
        }

        conditions.Add(Expression.Equal(
            Expression.Property(pe, nameof(ITenant.TenantCode)),
            Expression.PropertyOrField(Expression.Constant(context), nameof(EmptyContext.TenantCode))));

        Expression finalExp = conditions[0];
        for (int i = 1; i < conditions.Count; i++)
        {
            finalExp = Expression.AndAlso(finalExp, conditions[i]);
        }

        var lambda = Expression.Lambda<Func<T, bool>>(finalExp, pe);
        builder.Entity<T>().HasQueryFilter(lambda);
    }
}
