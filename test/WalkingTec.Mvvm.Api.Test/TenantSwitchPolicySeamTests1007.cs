using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Services;
using WalkingTec.Mvvm.Demo.Models._Admin;

namespace WalkingTec.Mvvm.Api.Test;

/// <summary>
/// Issue #1007 — named change NC9 from the cross-vendor design review (Codex gpt-5.6-sol,
/// round 6): the rest of #1007's coverage
/// (<c>WalkingTec.Mvvm.Admin.Test.SetCurrentTenantAdmissionTests1007</c> and
/// <c>FrameworkControllerRbacHooksTest</c>'s rows 15/16) resolves
/// <see cref="IWtmTenantSwitchPolicy"/> through <c>WTMContext.SetServiceProvider</c>, which sets
/// the private <c>_serviceProvider</c> field directly — bypassing the real production fallback
/// <c>_serviceProvider ?? _httpContext?.RequestServices</c> (<c>WTMContext.cs:35</c>) entirely.
/// Those fixtures also give the test's <c>WTMContext</c> and the test's controller two SEPARATE
/// <see cref="Microsoft.AspNetCore.Http.HttpContext"/> instances
/// (<c>WalkingTec.Mvvm.Test.Mock.MockWtmContext.cs:36</c>,
/// <c>FrameworkControllerRbacHooksTest.cs:136</c>) — neither proves a policy registered through
/// a real DI container is actually reachable from a real, routed request the way it would be in
/// production.
/// <para>
/// This file closes that gap the same way <c>FrameworkAuthorizationSeamTests</c> (issue #827)
/// did for <c>IWtmFrameworkEndpointAuthorizer</c>: every test here goes through
/// <see cref="DemoWebApplicationFactory"/>, a real ASP.NET Core <c>ServiceCollection</c>-backed
/// DI container (<c>ConfigureTestServices</c>, real <c>AddScoped</c>), and an actual
/// <see cref="HttpClient"/> request to the production, routed <c>/_Framework/SetTenant</c>
/// endpoint — exercising <c>WTMContext.ServiceProvider</c>'s real
/// <c>_httpContext?.RequestServices</c> fallback for real, not a stand-in for it.
/// </para>
/// <para>
/// <b>What this file still does NOT prove</b> (documented honestly, not implied): it does not
/// prove every deployment topology reaches the same seam (e.g. a federation front end with its
/// own <c>AllTenant</c> — see design round 6 §6 migration item 3). It also does not multiply out
/// every row of the admission matrix over real HTTP — that would duplicate
/// <c>SetCurrentTenantAdmissionTests1007</c> at ~50x the cost per test for no additional
/// confidence about the ONE gap this file exists to close (real DI/HTTP reachability).
/// </para>
/// </summary>
[TestClass]
public class TenantSwitchPolicySeamTests1007
{
    [TestInitialize]
    public void Init() => TestTenantSwitchPolicy.Reset();

    /// <summary>
    /// EnableTenant must be set via <c>ConfigureAppConfiguration</c> + <c>AddInMemoryCollection</c>,
    /// never <c>services.Configure&lt;Configs&gt;</c> — see <c>MultiTenantSeedFixtureTests</c>'s
    /// class-level doc comment for the full mechanism (the tenant-list bootstrap reads the eager
    /// <c>Configs</c> snapshot taken at <c>AddWtmContext</c>, which <c>services.Configure</c>
    /// can never reach in time).
    /// </summary>
    private static WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> NewFactory(
        DemoWebApplicationFactory factory, Action<IServiceCollection>? configureServices = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["EnableTenant"] = "true",
                });
            });
            if (configureServices != null)
            {
                builder.ConfigureTestServices(services => configureServices(services));
            }
        });

    /// <summary>
    /// Logs in as demo's seeded admin/000000 account — a HOST caller (<c>TenantCode == null</c>,
    /// see <c>demo/WalkingTec.Mvvm.Demo/DataContext.cs</c>'s <c>DataInit</c>). Same convention as
    /// <c>FrameworkAuthorizationSeamTests.LoginAsync</c>: no VerifyCode is supplied, which only
    /// works against the demo template's own <c>IsQuickDebug=true</c> default.
    /// </summary>
    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ITCode"] = "admin",
            ["Password"] = "000000",
        });
        await client.PostAsync("/Login/Login", form);
        return client;
    }

    /// <summary>
    /// Seeds a single, uniquely-coded <see cref="MyTenant"/> row and evicts
    /// <c>GlobalData.AllTenant</c>'s cache (<c>DbTestHelpers.InvalidateTenantCache</c>'s doc
    /// comment explains why this is required: <c>IWtmDataContextFactory.CreateDC</c> reads
    /// <c>AllTenant</c> unconditionally, including during the seed write itself, and would
    /// otherwise cache an empty list for an hour). Seeded via <see cref="MyTenant"/> (not the
    /// base <see cref="FrameworkTenant"/>), matching demo's own <c>DbSet&lt;MyTenant&gt;</c> —
    /// same convention as <c>MultiTenantSeedFixtureTests.SeedTenant</c>.
    /// </summary>
    private static MyTenant SeedListedTenant(WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> factory, string label)
    {
        var tenant = DbTestHelpers.Seed(factory, new MyTenant
        {
            ID = Guid.NewGuid(),
            TCode = $"t1007{label}{Guid.NewGuid():N}"[..24],
            TName = $"Issue #1007 seam tenant {label}",
            Enabled = true,
            TenantCode = null,
        });
        DbTestHelpers.InvalidateTenantCache(factory);
        return tenant;
    }

    /// <summary>
    /// Baseline (no policy registered): a host caller switching into a uniquely-resolved,
    /// listed tenant is admitted by <c>IsTenantSwitchPermitted</c>'s structural default
    /// (L-host, design round 6 §7 row H2) — proving the fixture/seed itself is sound BEFORE the
    /// policy-reachability test below relies on the same setup to prove a Deny override.
    /// </summary>
    [TestMethod]
    public async Task SetTenant_NoPolicyRegistered_HostSwitchToListedTenant_Succeeds()
    {
        using var factory = new DemoWebApplicationFactory();
        using var tenantFactory = NewFactory(factory);
        var tenant = SeedListedTenant(tenantFactory, "baseline");
        var client = await LoginAsync(tenantFactory);

        var resp = await client.GetAsync($"/_Framework/SetTenant?tenant={tenant.TCode}");

        Assert.AreNotEqual(HttpStatusCode.Redirect, resp.StatusCode,
            $"#1007: with no IWtmTenantSwitchPolicy registered, a host switching into a " +
            $"uniquely-resolved listed tenant must succeed (L-host default), not be refused. " +
            $"Got {(int)resp.StatusCode}.");
    }

    /// <summary>
    /// Core #1007/NC9 proof: a <see cref="IWtmTenantSwitchPolicy"/> registered through REAL
    /// request-scoped DI (<c>ConfigureTestServices</c> -&gt; real <c>AddScoped</c>) and returning
    /// <c>Deny</c> must be consulted by the real, routed <c>/_Framework/SetTenant</c> endpoint
    /// and override the host caller's otherwise-unrestricted default (L-host) for a tenant that
    /// resolves uniquely — proving the seam is reachable on a production route through
    /// <c>WTMContext.ServiceProvider</c>'s genuine <c>_httpContext?.RequestServices</c> fallback,
    /// which <c>WTMContext.SetServiceProvider</c>-based unit tests elsewhere in this issue's
    /// coverage do not exercise.
    /// </summary>
    [TestMethod]
    public async Task SetTenant_DIPolicyDeny_HostSwitchToListedTenant_ReturnsForbid_ConsultedOnRealRoute()
    {
        TestTenantSwitchPolicy.Decision = WtmAuthorizationDecision.Deny;
        using var factory = new DemoWebApplicationFactory();
        using var tenantFactory = NewFactory(factory, services =>
            services.AddScoped<IWtmTenantSwitchPolicy, TestTenantSwitchPolicy>());
        var tenant = SeedListedTenant(tenantFactory, "deny");
        var client = await LoginAsync(tenantFactory);

        var resp = await client.GetAsync($"/_Framework/SetTenant?tenant={tenant.TCode}");

        // _FrameworkController.SetTenant returns Forbid() on refusal; under the default cookie
        // auth scheme that surfaces as a 302 redirect to the login challenge, not a literal 403
        // (same convention as FrameworkAuthorizationSeamTests' Deny assertions and
        // MvcAuthHolesTests' wire-status tests).
        Assert.AreEqual(HttpStatusCode.Redirect, resp.StatusCode,
            $"#1007: a DI-registered IWtmTenantSwitchPolicy returning Deny must be consulted by " +
            $"the real, routed SetTenant endpoint and override the host's otherwise-unrestricted " +
            $"default for a uniquely-resolved listed tenant — proving the policy seam is reachable " +
            $"through WTMContext.ServiceProvider's real request-scoped DI fallback on an actual " +
            $"HTTP route, not merely through WTMContext.SetServiceProvider's stub-injection " +
            $"shortcut. Got {(int)resp.StatusCode}.");
        Assert.IsTrue(TestTenantSwitchPolicy.Calls > 0,
            "#1007: the DI-registered policy's CanSwitchTenant must actually have been invoked.");
    }
}

/// <summary>
/// Test double for <see cref="IWtmTenantSwitchPolicy"/>, DI-registered per test via
/// <c>ConfigureTestServices</c>. Decision and call count are driven by STATIC state the test
/// method sets up before building the factory and resets in
/// <see cref="TenantSwitchPolicySeamTests1007.Init"/> — a new SCOPED instance is constructed by
/// the real DI container on every request, but the desired decision has to be steered from
/// outside that container (same pattern as <c>TestFrameworkEndpointAuthorizer</c>, issue #827).
/// </summary>
internal sealed class TestTenantSwitchPolicy : IWtmTenantSwitchPolicy
{
    public static WtmAuthorizationDecision Decision = WtmAuthorizationDecision.Inherit;
    public static int Calls;

    public static void Reset()
    {
        Decision = WtmAuthorizationDecision.Inherit;
        Calls = 0;
    }

    public WtmAuthorizationDecision CanSwitchTenant(
        WTMContext wtm, LoginUserInfo user, FrameworkTenant resolvedDescriptor, string requestedCode)
    {
        Interlocked.Increment(ref Calls);
        return Decision;
    }
}
