using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.TagHelpers.LayUI.Common;

namespace WalkingTec.Mvvm.Api.Test;

/// <summary>
/// Issue #753 (HIGH, split-brain flag read): before this fix, TagHelpers read
/// <c>WtmUIOptionsHolder.Options</c> (populated in
/// <c>FrameworkServiceExtension.AddWtmContext</c> from a hand-parsed
/// <c>config.GetSection("UIOptions").Get&lt;WtmUIOptions&gt;()</c> snapshot — taken
/// at service-registration time, BEFORE the DI container exists, so it can
/// only ever see the appsettings binding), while <c>LayuiUIService</c> reads
/// the real <c>IOptions&lt;WtmUIOptions&gt;</c> (which reflects BOTH the
/// appsettings binding AND any <c>services.Configure&lt;WtmUIOptions&gt;(o => ...)</c>
/// code delegate a host app registers — the documented mechanism per
/// <see cref="WtmUIOptions"/>'s XML doc). An app that configured the flag in
/// code was silently ignored by every TagHelper while being honored by
/// LayuiUIService.
///
/// The fix moves the holder population to <c>UseWtmContext</c> (after the DI
/// container is built) and resolves the real <c>IOptions&lt;WtmUIOptions&gt;</c>
/// there — so both consumers now agree. This test proves it end-to-end
/// through the full Demo app host (AddWtmContext -> UseWtmContext), which is
/// the only way to exercise the actual startup code path this bug lived in
/// (a plain unit test of the Options pipeline in isolation would prove
/// nothing about THIS bug — the bug was entirely about which snapshot
/// FrameworkServiceExtension pushed into the holder and when).
/// </summary>
[TestClass]
public class UIOptionsSplitBrainTests
{
    [TestMethod]
    public void CodeBasedConfigure_IsSeenByWtmUIOptionsHolder_AfterHostStartup()
    {
        // demo/appsettings.json's "UIOptions" section does not set
        // UseSelectIslandRender at all, so the appsettings-binding path alone
        // leaves it at the compiled-in default (false). Only a code-based
        // services.Configure<WtmUIOptions>(...) delegate — registered here via
        // WithWebHostBuilder, mirroring MvcAuthHolesTests' ConfigureServices
        // override pattern — can flip it to true for this test run.
        using var factory = new DemoWebApplicationFactory();
        using var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<WtmUIOptions>(o => o.UseSelectIslandRender = true);
            });
        });

        // Force the host to actually start (WebApplicationFactory boots
        // lazily) — this runs Program.cs's AddWtmContext/UseWtmContext
        // pipeline for real, including the fixed UseWtmContext line that
        // resolves IOptions<WtmUIOptions> and calls
        // BaseFieldTag.SetUIOptions(...).
        using var client = configuredFactory.CreateClient();

        // WtmUIOptionsHolder is internal to WalkingTec.Mvvm.TagHelpers.LayUI;
        // visible here via the InternalsVisibleTo added for this test (#753).
        Assert.IsTrue(WtmUIOptionsHolder.Options.UseSelectIslandRender,
            "A code-based services.Configure<WtmUIOptions>(o => o.UseSelectIslandRender = true) " +
            "delegate must be visible through WtmUIOptionsHolder (the same static state every " +
            "TagHelper's UIConfig property reads) after host startup — not just through " +
            "LayuiUIService's IOptions<WtmUIOptions> injection.");
    }

    [TestMethod]
    public void NoCodeBasedConfigure_HolderReflectsAppsettingsDefault_False()
    {
        // Control case: without the code-based override, the appsettings
        // binding alone (which never sets UseSelectIslandRender) must leave
        // the holder at the default OFF state — proving the fix doesn't
        // silently turn the flag on for apps that never opted in.
        using var factory = new DemoWebApplicationFactory();
        using var client = factory.CreateClient();

        Assert.IsFalse(WtmUIOptionsHolder.Options.UseSelectIslandRender,
            "Without any code-based Configure override, UseSelectIslandRender must stay at its " +
            "documented default (OFF) — the appsettings-only path must be unaffected by this fix.");
    }

    /// <summary>
    /// Issue #837 patch 4 — proves the exact mechanism <c>.github/workflows/e2e-test.yml</c>'s
    /// new "island" matrix leg relies on: an environment variable named
    /// <c>UIOptions__UseSelectIslandRender</c> (ASP.NET Core's standard double-underscore
    /// convention for the nested config key <c>"UIOptions:UseSelectIslandRender"</c>) flips the
    /// REAL flag every LayUI TagHelper reads — not just <c>IOptions&lt;WtmUIOptions&gt;</c>,
    /// but <see cref="WtmUIOptionsHolder"/> too, via the same <c>UseWtmContext</c> path
    /// <see cref="CodeBasedConfigure_IsSeenByWtmUIOptionsHolder_AfterHostStartup"/> above
    /// proves for a code-based <c>Configure</c> delegate. This test uses
    /// <c>AddInMemoryCollection</c> with the colon-separated key rather than a real OS
    /// environment variable (there is no supported way to set one scoped to a single
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> instance's in-process host), but that is
    /// exactly what ASP.NET Core's environment-variable configuration provider itself does
    /// internally — translate <c>UIOptions__UseSelectIslandRender</c> into the
    /// <c>IConfiguration</c> key <c>"UIOptions:UseSelectIslandRender"</c> — so this is a faithful
    /// proxy for what the real env var does when the e2e workflow sets it on the demo app's
    /// actual OS process.
    /// </summary>
    [TestMethod]
    public void EnvironmentVariableStyleOverride_IsSeenByWtmUIOptionsHolder_AfterHostStartup()
    {
        using var factory = new DemoWebApplicationFactory();
        using var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["UIOptions:UseSelectIslandRender"] = "true",
                });
            });
        });

        using var client = configuredFactory.CreateClient();

        Assert.IsTrue(WtmUIOptionsHolder.Options.UseSelectIslandRender,
            "#837: an env-var-style 'UIOptions:UseSelectIslandRender' config override must be " +
            "visible through WtmUIOptionsHolder after host startup — this is the exact " +
            "mechanism the e2e-test.yml 'island' matrix leg's UIOptions__UseSelectIslandRender " +
            "environment variable relies on to actually exercise island-render TagHelper paths.");
    }
}
