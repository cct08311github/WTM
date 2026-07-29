#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Demo.Models;

namespace WalkingTec.Mvvm.Api.Test;

/// <summary>
/// Issue #867 — real end-to-end HTTP proof that <c>_FrameworkController.GetPagingData</c>'s
/// <c>RedoUpdateModel(listVM)</c> call can no longer reach process-wide DI singletons or
/// <c>static</c> members through a caller-supplied dotted-path form field, and that the
/// endpoint's own <c>SearcherMode</c> is re-pinned after binding the same way
/// <c>Selector</c>/<c>GetExportExcel</c>/<c>GetExportExcelStream</c> already do.
///
/// <para>
/// Follows the same <see cref="DemoWebApplicationFactory"/> + strict-factory (<c>IsQuickDebug:
/// false</c>) + <see cref="CaptchaTestHelper"/> login pattern <c>MvcAuthHolesTests</c> established
/// for #837 — a separate class (not an addition to <c>MvcAuthHolesTests</c>) because these tests
/// need their own strict factory instance and this file's own baseline-then-attack assertions
/// don't share any fixture state with that class's.
/// </para>
/// </summary>
[TestClass]
public class RequestBindingScopeHttpTests867
{
    private static DemoWebApplicationFactory _factory = null!;

    // IsQuickDebug=false so the flag genuinely starts false and a flip would be observable —
    // under the default demo appsettings.json (IsQuickDebug: true) the "still false" assertion
    // below would be meaningless (it would already be true before any attack).
    private static WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> _strictFactory = null!;

    private const string StudentListVm =
        "WalkingTec.Mvvm.Demo.ViewModels.StudentVMs.StudentListVM";

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        _factory = new DemoWebApplicationFactory();
        _strictFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["IsQuickDebug"] = "false",
                });
            });
        });
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _strictFactory.Dispose();
        _factory.Dispose();
    }

    // Mirrors MvcAuthHolesTests.NewAuthClientAsync exactly (see that class's doc comment for
    // why _strictFactory + CaptchaTestHelper is required once IsQuickDebug=false).
    private async Task<HttpClient> NewAuthClientAsync()
    {
        var client = _strictFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = true,
            HandleCookies = true,
        });
        var verifyCode = await CaptchaTestHelper.FetchAndSolveAsync(_strictFactory, client);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ITCode"] = "admin",
            ["Password"] = "000000",
            ["VerifyCode"] = verifyCode,
        });
        var loginResp = await client.PostAsync("/Login/Login", form);
        var loginBody = await loginResp.Content.ReadAsStringAsync();
        if (loginBody.Contains("login-error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"#867: NewAuthClientAsync's admin login did not succeed under _strictFactory " +
                $"(IsQuickDebug=false). Response: {loginBody[..Math.Min(500, loginBody.Length)]}");
        }
        return client;
    }

    private static Student SeedStudentWithUniqueZip(string uniqueZip) =>
        DbTestHelpers.Seed(_strictFactory, new Student
        {
            ID = $"wtm867-{Guid.NewGuid():N}",
            Password = "p",
            Name = "WTM867 Test Student",
            ZipCode = uniqueZip,
            IsValid = true,
        });

    // ═══════════════════════════════════════════════════════════════════════
    // #867 core: ConfigInfo.IsQuickDebug must not reach the process-wide Configs
    // singleton, and a legitimate Searcher.* binding in the SAME request must still work.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The P0 finding from the issue: one authenticated low-privilege caller's
    /// <c>ConfigInfo.IsQuickDebug=true</c> form field, POSTed to <c>[AllRights]</c>
    /// <c>/_Framework/GetPagingData</c>, used to reach <c>WTMContext.ConfigInfo</c>
    /// (<c>IOptionsMonitor&lt;Configs&gt;.CurrentValue</c> — a process-wide DI singleton) via
    /// <c>RedoUpdateModel</c>'s unrestricted reflection write, disabling <c>IsQuickDebug</c>-gated
    /// authorization checks for every user of the running process until restart.
    ///
    /// <para>
    /// Asserts on <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> resolved directly from the
    /// factory's own <see cref="WebApplicationFactory{TEntryPoint}.Services"/> — the SAME instance
    /// <c>WTMContext</c>'s constructor captures into <c>ConfigInfo</c> (<c>_configInfo =
    /// _config?.CurrentValue</c>) — not on a response status code, per the issue's own acceptance
    /// criteria ("assert on the config value, not on a status code").
    /// </para>
    ///
    /// <para>
    /// Positive control, in the SAME request/method as required: a legitimate
    /// <c>Searcher.ZipCode</c> binding (the framework's designed request-binding surface) is
    /// POSTed alongside the hostile field and must still filter the result set correctly — without
    /// this, a fix that simply rejected every key would make the hostile assertion pass for the
    /// wrong reason.
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task GetPagingData_HostileConfigInfoIsQuickDebugField_DoesNotFlipConfig_LegitimateSearcherBindingStillWorks()
    {
        var configMonitor = _strictFactory.Services.GetRequiredService<IOptionsMonitor<Configs>>();
        Assert.IsFalse(configMonitor.CurrentValue.IsQuickDebug,
            "Sanity: baseline IsQuickDebug must be false under _strictFactory before the hostile request.");

        var uniqueZip = $"867{Guid.NewGuid():N}"[..12];
        SeedStudentWithUniqueZip(uniqueZip);

        var client = await NewAuthClientAsync();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = StudentListVm,
            ["ConfigInfo.IsQuickDebug"] = "true",
            ["Searcher.ZipCode"] = uniqueZip,
        });

        var resp = await client.PostAsync("/_Framework/GetPagingData", form);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"#867: GetPagingData should succeed (rejecting the hostile key is a skip, not a " +
            $"request failure). Got {(int)resp.StatusCode}: {body[..Math.Min(500, body.Length)]}");

        // ── the critical assertion: the CONFIG VALUE, not a status code ──
        Assert.IsFalse(configMonitor.CurrentValue.IsQuickDebug,
            "#867: a caller-supplied 'ConfigInfo.IsQuickDebug' form field must never reach the " +
            "process-wide Configs singleton through RedoUpdateModel's raw reflection write.");

        // ── positive control, same request/method: legitimate Searcher.ZipCode still works ──
        using var doc = JsonDocument.Parse(body);
        var count = doc.RootElement.GetProperty("Count").GetInt64();
        Assert.AreEqual(1, count,
            $"#867: positive control failed — Searcher.ZipCode='{uniqueZip}' should have found " +
            $"exactly the one seeded student (proving the fix does not simply reject every key). " +
            $"Body: {body}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Static-member path
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The issue's own runtime-verified static-member finding: <c>Type.GetMember(name)</c>
    /// defaults to <c>BindingFlags.Public | Instance | Static</c>, so <c>WTMContext</c>'s
    /// <c>public static Func&lt;WTMContext, string, LoginUserInfo&gt;? ReloadUserFunc</c> is
    /// reachable through the ordinary instance path <c>Wtm.ReloadUserFunc</c> even though it has
    /// nothing to do with any one request's <c>WTMContext</c> instance.
    /// </summary>
    [TestMethod]
    public async Task GetPagingData_HostileWtmReloadUserFuncField_DoesNotOverwriteStaticMember()
    {
        var baseline = WTMContext.ReloadUserFunc;
        try
        {
            // A non-null sentinel: distinguishes "still what we set it to" from "coincidentally
            // still null" (the field's own default, which a failed conversion would ALSO leave it
            // as — see PropertyHelper.cs:938/719-726 — making a null-to-null comparison a weak
            // assertion). Captured into a local so the AreSame comparison below is against the
            // exact same delegate instance, not a fresh method-group conversion.
            LoginUserInfo Sentinel(WTMContext ctx, string itcode) => new LoginUserInfo();
            Func<WTMContext, string, LoginUserInfo> sentinelDelegate = Sentinel;
            WTMContext.ReloadUserFunc = sentinelDelegate;

            var client = await NewAuthClientAsync();
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_DONOT_USE_VMNAME"] = StudentListVm,
                ["Wtm.ReloadUserFunc"] = "hostile",
            });

            var resp = await client.PostAsync("/_Framework/GetPagingData", form);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
                $"#867: GetPagingData should succeed. Got {(int)resp.StatusCode}: {body[..Math.Min(500, body.Length)]}");

            Assert.AreSame(sentinelDelegate, WTMContext.ReloadUserFunc,
                "#867: a caller-supplied 'Wtm.ReloadUserFunc' form field must never overwrite the " +
                "process-wide static WTMContext.ReloadUserFunc delegate.");
        }
        finally
        {
            WTMContext.ReloadUserFunc = baseline;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Aliased path — proves the declaring-type check, not a literal-first-segment name blocklist
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>Searcher.Wtm.ConfigInfo.IsQuickDebug</c> (the issue's own alias example) reaches the
    /// exact same target as <c>ConfigInfo.IsQuickDebug</c>, but its key's literal FIRST segment is
    /// <c>"Searcher"</c> — not <c>"Wtm"</c> or <c>"ConfigInfo"</c> — so a blocklist keyed on the
    /// first segment's literal text would miss it entirely. Also includes the shorter
    /// <c>Wtm.ConfigInfo.IsQuickDebug</c> alias (3 segments, within the depth cap) in the SAME
    /// request, isolating the declaring-type guard from the depth cap:
    /// <c>test/WalkingTec.Mvvm.Core.Test/Helper/RequestBindingPolicyTests867.cs</c>'s
    /// <c>IsPathAllowed_WtmDotConfigInfoDotIsQuickDebug_ReturnsFalse</c> unit test pins that one
    /// directly against the policy function; this test proves the same thing through the real
    /// HTTP pipeline.
    /// </summary>
    [TestMethod]
    public async Task GetPagingData_HostileAliasedConfigInfoFields_DoNotFlipConfig()
    {
        var configMonitor = _strictFactory.Services.GetRequiredService<IOptionsMonitor<Configs>>();
        Assert.IsFalse(configMonitor.CurrentValue.IsQuickDebug,
            "Sanity: baseline IsQuickDebug must be false under _strictFactory before the hostile request.");

        var client = await NewAuthClientAsync();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = StudentListVm,
            ["Wtm.ConfigInfo.IsQuickDebug"] = "true",
            ["Searcher.Wtm.ConfigInfo.IsQuickDebug"] = "true",
        });

        var resp = await client.PostAsync("/_Framework/GetPagingData", form);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"#867: GetPagingData should succeed. Got {(int)resp.StatusCode}: {body[..Math.Min(500, body.Length)]}");

        Assert.IsFalse(configMonitor.CurrentValue.IsQuickDebug,
            "#867: neither the 'Wtm.ConfigInfo.IsQuickDebug' nor the aliased " +
            "'Searcher.Wtm.ConfigInfo.IsQuickDebug' form field may reach the process-wide " +
            "Configs singleton — the declaring-type check must catch 'Wtm' wherever in the " +
            "dotted path it appears, not just as the key's literal first segment.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GetPagingData must re-pin SearcherMode after binding (the other half of #867)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>GetPagingData</c> was the only one of the five <c>RedoUpdateModel</c> call sites that
    /// did not re-pin <c>SearcherMode</c> after binding — <c>Selector</c>/<c>GetExportExcel</c>/
    /// <c>GetExportExcelStream</c> all do. A caller-supplied <c>SearcherMode=Batch</c> routes
    /// <c>GetSearchQuery()</c> through <c>GetBatchQuery</c>, which strips every <c>Where</c> the
    /// ListVM's own <c>GetSearchQuery()</c> applied (including row-level filtering such as the
    /// <c>Searcher.ZipCode</c> filter this test also posts) before adding an
    /// <c>Ids.Contains(...)</c> clause. Proven with an <c>Ids</c> value that matches nothing: if
    /// <c>SearcherMode</c> is correctly re-pinned to <c>Search</c>, the <c>ZipCode</c> filter
    /// still finds the seeded student (Count == 1) regardless of the posted <c>SearcherMode</c>/
    /// <c>Ids</c>; if not, <c>Ids.Contains(...)</c> replaces the <c>ZipCode</c> filter entirely and
    /// the non-matching <c>Ids</c> value finds nothing (Count == 0).
    /// </summary>
    [TestMethod]
    public async Task GetPagingData_HostileSearcherModeBatchField_DoesNotBypassRowFiltering()
    {
        var uniqueZip = $"867{Guid.NewGuid():N}"[..12];
        SeedStudentWithUniqueZip(uniqueZip);

        var client = await NewAuthClientAsync();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = StudentListVm,
            ["Searcher.ZipCode"] = uniqueZip,
            ["SearcherMode"] = "Batch",
            ["Ids"] = "id-that-matches-no-seeded-student",
        });

        var resp = await client.PostAsync("/_Framework/GetPagingData", form);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"#867: GetPagingData should succeed. Got {(int)resp.StatusCode}: {body[..Math.Min(500, body.Length)]}");

        using var doc = JsonDocument.Parse(body);
        var count = doc.RootElement.GetProperty("Count").GetInt64();
        Assert.AreEqual(1, count,
            $"#867: a caller-supplied SearcherMode=Batch must not survive past RedoUpdateModel — " +
            $"GetPagingData must re-pin SearcherMode=Search so the posted Searcher.ZipCode filter " +
            $"(not the posted, non-matching Ids list) determines the result. Body: {body}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Regression guard: Configs.EnforceRequestBindingScope really is default-on
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Confirms the kill switch's own default — the demo app under test does not set
    /// <c>EnforceRequestBindingScope</c> anywhere in its appsettings.json, so this reads the
    /// framework's own <see cref="Configs"/> default, not a test-specific override.
    /// </summary>
    [TestMethod]
    public void Configs_EnforceRequestBindingScope_DefaultsToTrue()
    {
        var configMonitor = _strictFactory.Services.GetRequiredService<IOptionsMonitor<Configs>>();
        Assert.IsTrue(configMonitor.CurrentValue.EnforceRequestBindingScope,
            "#867: Configs.EnforceRequestBindingScope must default to true (secure by default, " +
            "matching the #859 precedent) unless a deployment explicitly opts out.");
    }
}
