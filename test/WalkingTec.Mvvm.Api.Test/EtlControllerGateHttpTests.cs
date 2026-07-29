#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Api.Test;

/// <summary>
/// Issue #841 — three ETL controllers (<c>_EtlRunLogController</c>, <c>_EtlMonitorController</c>,
/// <c>_EtlSchemaController</c>) had no <c>OnActionExecuting</c> role gate of their own, unlike
/// <c>_EtlJobController</c>/<c>_EtlDashboardController</c> (<c>grep -c OnActionExecuting</c>
/// returned 0 for the first three, 3 for the other two). This class provides the REAL-HTTP
/// negative control (non-admin authenticated caller is rejected) for all three; the paired
/// positive control (an Admin/ETLAdmin caller passes the gate) lives as a unit test in
/// <c>test/WalkingTec.Mvvm.Etl.Test/Controllers/EtlRbacTests.cs</c> instead of here -- see the
/// "Known limitation" note below for why a real-HTTP positive control is not currently
/// achievable for ANY controller in the WalkingTec.Mvvm.Etl assembly, not just these three.
///
/// <para>
/// <b>Known limitation, found while building this test (filed as a new, separate P0 issue --
/// this defect predates #841/#862 and is not introduced by either): <c>WalkingTec.Mvvm.Etl</c>
/// assembly controllers never receive WTM's three global action filters</b>
/// (<c>DataContextFilter</c>/<c>PrivilegeFilter</c>/<c>FrameworkFilter</c>, registered in
/// <c>MvcOptionExtension.UseWtmMvcOptions</c>) when reached via a real ASP.NET Core MVC request.
/// Confirmed empirically: instrumenting <c>DataContextFilter.OnActionExecuting</c> to
/// short-circuit on any request path containing "Etl" (case-insensitive) never triggers for
/// <c>/_EtlJob/Index</c> (a PRE-EXISTING controller, untouched by this PR) even from a genuinely
/// authenticated client, while the identical instrumentation correctly triggers for
/// <c>/_Framework/GetVerifyCode</c> (a <c>WalkingTec.Mvvm.Mvc</c>-assembly controller). Because
/// <c>DataContextFilter</c> is what populates <c>BaseController.Wtm</c>
/// (<c>ActionExecutingContextExtension.SetWtmContext</c>), <c>Wtm</c> is <c>null</c> inside
/// <em>every</em> ETL controller's <c>OnActionExecuting</c>/action body reached via real HTTP --
/// confirmed directly: a diagnostic response from inside <c>_EtlMonitorController.Running</c>'s
/// gate showed <c>wtmIsNull=true</c>, <c>loginUserInfoIsNull=true</c>,
/// <c>httpContextIsNull=true</c> for a real, successfully-authenticated ETLAdmin-role session.
/// The practical effect: every <c>Wtm.LoginUserInfo.Roles</c>-based role gate on this assembly's
/// controllers (the pre-existing ones on <c>_EtlJobController</c>/<c>_EtlDashboardController</c>,
/// and the three this PR adds) currently fails CLOSED for every caller, including a genuine
/// Admin/ETLAdmin -- not because the gate LOGIC is wrong (it is not; the unit tests in
/// <c>EtlRbacTests.cs</c> prove the logic correctly distinguishes admin from non-admin once
/// <c>Wtm</c>/<c>Roles</c> are populated), but because the surrounding pipeline never gives it
/// the data to decide with. This also means any ETL controller action that reads
/// <c>Wtm.XXX</c> in its own body (e.g. <c>_EtlRunLogController.Index</c>'s
/// <c>Wtm.CreateVM&lt;EtlRunLogListVM&gt;()</c>, <c>_EtlSchemaController.Tables</c>'s
/// <c>Wtm.ConfigInfo.Connections</c>) throws <c>NullReferenceException</c> the moment it is
/// reached via a real request, independent of authorization. This is a defect in the shared
/// <c>WalkingTec.Mvvm.Etl</c> controller-assembly wiring, not in any single controller or in
/// this PR's own added gates -- root-causing WHY the global filters never apply to this specific
/// assembly's controllers is out of scope for #841/#862 and needs its own dedicated
/// investigation; see the issue this finding was filed under for the reproduction steps above.
/// </para>
///
/// <para>
/// <b>What IS still meaningfully tested here despite that limitation:</b> the negative-control
/// tests below still exercise the REAL HTTP pipeline end-to-end and still genuinely kill a
/// mutant that removes the gate -- with the gate present, EVERY caller (admin or not) is
/// rejected (a symptom of the Wtm-population defect above); with the gate deleted entirely, the
/// action runs unguarded and either succeeds (<c>_EtlMonitorController.Running</c>, which never
/// touches <c>Wtm</c>) or throws (the two controllers whose action bodies use <c>Wtm</c>) --
/// either way, a materially different, non-403/redirect response, which is exactly what proves
/// the gate's presence still matters today, independent of the deeper defect.
/// </para>
///
/// <para>
/// <b>Strict factory, not the demo default:</b> demo's own <c>appsettings.json</c> ships
/// <c>IsQuickDebug: true</c>, which bypasses every custom <c>OnActionExecuting</c> role check
/// unconditionally. A test built against the unmodified factory would pass even if the gate
/// itself were deleted outright. <see cref="_strictFactory"/> (<c>IsQuickDebug=false</c>) is
/// therefore the ONLY factory used for the gate-check requests below, matching the convention
/// <c>MvcAuthHolesTests</c>/<c>MultiTenantSeedFixtureTests</c> already established.
/// </para>
/// </summary>
[TestClass]
public class EtlControllerGateHttpTests
{
    private static DemoWebApplicationFactory _factory = null!;
    private static WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> _strictFactory = null!;

    private const string NonAdminItCode = "etlgate-nonadmin-841";
    private const string NonAdminPassword = "Passw0rd!841b";

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
                    // #841: demo's default AccessDeniedPath equals LoginPath ("/Login/Login"),
                    // so a bare 302-to-Login is ambiguous between "never authenticated" and
                    // "authenticated but Forbid()-den by the gate" -- ControllerBase.Forbid()
                    // goes through the cookie-auth ForbidAsync challenge, which redirects to
                    // AccessDeniedPath. A distinct path here disambiguates the two, exactly the
                    // way MvcAuthHolesTests' _vmExportEnforcedFactory does for #796.
                    ["CookieOptions:AccessDeniedPath"] = "/AccessDenied841Test",
                });
            });
        });

        // A real authenticated user with zero FrameworkUserRole rows, so LoginUserInfo.Roles
        // resolves to an empty list once populated -- were Wtm ever non-null here, the gate's
        // roles.Any(...) would be false for this user regardless.
        DbTestHelpers.Seed(_strictFactory, new FrameworkUser
        {
            ID = Guid.NewGuid(),
            ITCode = NonAdminItCode,
            Password = PasswordHashHelper.HashPassword(NonAdminPassword),
            Name = "Issue #841 ETL non-admin",
            IsValid = true,
        });
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _strictFactory.Dispose();
        _factory.Dispose();
    }

    /// <summary>
    /// Logs in via the real <c>/Login/Login</c> POST (captcha solved via
    /// <see cref="CaptchaTestHelper"/>) and returns an authenticated <see cref="HttpClient"/>
    /// carrying the resulting session cookie. <c>AllowAutoRedirect=false</c> throughout
    /// (matching <c>MultiTenantSeedFixtureTests.TenantScopedUser_CanCompleteARealHttpLogin</c>):
    /// a successful <c>DoLoginAsync</c> returns a real 302 to "/", an unambiguous success
    /// signal, and NOT auto-following matters for the gate checks below too --
    /// <c>ControllerBase.Forbid()</c> goes through the cookie-auth <c>ForbidAsync</c> challenge,
    /// which redirects to <c>AccessDeniedPath</c> (302), not a raw 403; following that redirect
    /// would land on another 200 page and mask the gate's actual decision.
    /// </summary>
    private static async Task<HttpClient> NewAuthClientAsync(string itcode, string password)
    {
        var client = _strictFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        var verifyCode = await CaptchaTestHelper.FetchAndSolveAsync(_strictFactory, client);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ITCode"] = itcode,
            ["Password"] = password,
            ["VerifyCode"] = verifyCode,
        });
        var loginResp = await client.PostAsync("/Login/Login", form);
        if (loginResp.StatusCode != HttpStatusCode.Redirect)
        {
            var loginBody = await loginResp.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"#841: login did not succeed for '{itcode}' under _strictFactory " +
                $"(IsQuickDebug=false) -- expected a 302 redirect, got {loginResp.StatusCode}. " +
                $"Response: {loginBody[..Math.Min(500, loginBody.Length)]}");
        }
        return client;
    }

    /// <summary>
    /// Asserts <paramref name="resp"/> is the gate's Forbid() denial: a 302 redirect to the
    /// distinct <c>/AccessDenied841Test</c> path configured on <see cref="_strictFactory"/> --
    /// NOT a raw 403 (ControllerBase.Forbid() goes through the cookie-auth challenge, which
    /// redirects) and NOT a 302 to <c>/Login/Login</c> (which would mean the caller was never
    /// actually authenticated in the first place, proving nothing about the gate itself).
    /// </summary>
    private static void AssertForbidden(HttpResponseMessage resp, string controllerName)
    {
        Assert.AreEqual(HttpStatusCode.Redirect, resp.StatusCode,
            $"#841: a non-Admin/ETLAdmin authenticated caller must be rejected by " +
            $"{controllerName}'s role gate (a 302 to AccessDeniedPath, from " +
            $"ControllerBase.Forbid()'s cookie-auth challenge). Got {(int)resp.StatusCode} " +
            $"{resp.StatusCode}.");
        var location = resp.Headers.Location?.ToString() ?? "";
        StringAssert.Contains(location, "/AccessDenied841Test",
            $"#841: {controllerName} redirected to '{location}' -- expected the distinct " +
            "AccessDeniedPath, not a plain redirect to /Login/Login (which would mean the " +
            "caller was never authenticated at all, not that the gate rejected it).");
    }

    /// <summary>
    /// <b>Mutant target</b>: removing the <c>OnActionExecuting</c> override (or its
    /// <c>context.Result = Forbid();</c> line) from <c>_EtlMonitorController</c>
    /// (<c>src/WalkingTec.Mvvm.Etl/Controllers/_EtlMonitorController.cs</c>) turns this
    /// assertion red -- <c>Running()</c> never touches <c>Wtm</c>, so an unguarded request
    /// succeeds with 200 instead of the AccessDenied redirect.
    /// </summary>
    [TestMethod]
    public async Task EtlMonitorController_Running_NonAdmin_Forbidden()
    {
        var client = await NewAuthClientAsync(NonAdminItCode, NonAdminPassword);
        var resp = await client.GetAsync("/_EtlMonitor/Running");
        AssertForbidden(resp, "_EtlMonitorController");
    }

    /// <summary>
    /// <b>Mutant target</b>: removing the <c>OnActionExecuting</c> override (or its
    /// <c>context.Result = Forbid();</c> line) from <c>_EtlRunLogController</c>
    /// (<c>src/WalkingTec.Mvvm.Etl/Controllers/_EtlRunLogController.cs</c>) turns this
    /// assertion red.
    /// </summary>
    [TestMethod]
    public async Task EtlRunLogController_Index_NonAdmin_Forbidden()
    {
        var client = await NewAuthClientAsync(NonAdminItCode, NonAdminPassword);
        var resp = await client.GetAsync("/_EtlRunLog/Index");
        AssertForbidden(resp, "_EtlRunLogController");
    }

    /// <summary>
    /// <b>Mutant target</b>: removing the <c>OnActionExecuting</c> override (or its
    /// <c>context.Result = Forbid();</c> line) from <c>_EtlSchemaController</c>
    /// (<c>src/WalkingTec.Mvvm.Etl/Controllers/_EtlSchemaController.cs</c>) turns this
    /// assertion red.
    /// </summary>
    [TestMethod]
    public async Task EtlSchemaController_Tables_NonAdmin_Forbidden()
    {
        var client = await NewAuthClientAsync(NonAdminItCode, NonAdminPassword);
        var resp = await client.GetAsync("/_EtlSchema/Tables?csKey=default&dbType=SQLite");
        AssertForbidden(resp, "_EtlSchemaController");
    }
}
