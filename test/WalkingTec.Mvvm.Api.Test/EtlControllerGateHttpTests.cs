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
/// negative control (non-admin authenticated caller is rejected) AND, since issue #876 fixed
/// the defect described below, the REAL-HTTP positive control (an Admin/ETLAdmin caller passes
/// the gate and reaches the action) for all three.
///
/// <para>
/// <b>Issue #876 (fixed): <c>WalkingTec.Mvvm.Etl</c> assembly controllers never received WTM's
/// three global action filters' effect in time</b> (<c>DataContextFilter</c>/
/// <c>PrivilegeFilter</c>/<c>FrameworkFilter</c>, registered in
/// <c>MvcOptionExtension.UseWtmMvcOptions</c>) — found while building this test, filed as a new,
/// separate P0 issue (predates #841/#862, not introduced by either). Root cause: ASP.NET Core's
/// own <c>ControllerActionFilter</c> (the internal wrapper that invokes a controller's own
/// <c>OnActionExecuting</c> override, added automatically because every <c>Controller</c>
/// subclass implements <see cref="Microsoft.AspNetCore.Mvc.Filters.IActionFilter"/>) is
/// hard-coded by the framework to <c>Order = int.MinValue</c> — it always runs before ANY
/// custom filter, including WTM's three global ones, no matter what <c>Order</c> those are
/// given (confirmed empirically: giving <c>DataContextFilter</c> <c>Order = -1000</c> made no
/// difference). <c>DataContextFilter</c> is what populates <c>BaseController.Wtm</c>
/// (<c>ActionExecutingContextExtension.SetWtmContext</c>) — so for the five
/// <c>WalkingTec.Mvvm.Etl</c> controllers, which are the ONLY controllers anywhere in the
/// codebase that read <c>Wtm</c> directly inside their own <c>OnActionExecuting</c> override
/// (every other WTM controller uses <c>PrivilegeFilter</c>'s declarative, URL-based
/// authorization instead), <c>Wtm</c> was always <c>null</c> at the point the gate read it. The
/// null-conditional operators throughout the gate (<c>Wtm?.LoginUserInfo?.Roles</c>) turned that
/// into a silently-empty role list rather than a <see cref="NullReferenceException"/>, so
/// <c>context.Result = Forbid()</c> fired unconditionally — denying EVERY caller, including a
/// genuine Admin, with a clean 403/redirect. Because setting <c>context.Result</c> inside
/// <c>OnActionExecuting</c> short-circuits the rest of the filter pipeline, <c>DataContextFilter</c>/
/// <c>PrivilegeFilter</c>/<c>FrameworkFilter</c> never got a turn at all for these five actions —
/// so this was an availability defect (100% lockout of the ETL admin UI for everyone), not the
/// framework silently letting unauthorized callers through.
/// </para>
///
/// <para>
/// <b>Fix</b>: <c>WtmControllerActivator</c>
/// (<c>src/WalkingTec.Mvvm.Mvc/Helper/WtmControllerActivator.cs</c>) replaces the default
/// <c>IControllerActivator</c> and populates <c>Wtm</c> at controller CONSTRUCTION time —
/// before any filter, including the hard-coded-first <c>ControllerActionFilter</c>, ever runs.
/// This is a framework-level fix (registered once in <c>AddWtmContext</c>) that applies
/// uniformly to every controller/assembly, not a per-controller patch to the five affected Etl
/// controllers.
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

    // #876: real ETLAdmin caller, used for the positive-control (admin succeeds) tests below.
    // RoleCode must be exactly "ETLAdmin" (case-insensitive) -- the gate compares with
    // string.Equals, not Contains.
    private const string AdminItCode = "etlgate-admin-876";
    private const string AdminPassword = "Passw0rd!876a";
    private const string AdminRoleCode = "ETLAdmin";

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

        // #876: a real ETLAdmin-role user, wired up exactly the way a real deployment would --
        // FrameworkUser + FrameworkRole + FrameworkUserRole linking them by RoleCode, matching
        // the reproduction the issue itself verified with (see class doc comment). The gate
        // checks Wtm.LoginUserInfo.Roles; the surrounding PrivilegeFilter separately checks
        // Wtm.IsAccessable(BaseUrl) via FrameworkMenu/FunctionPrivilege, so a menu grant per URL
        // is also seeded below -- both layers must agree for the action to actually be reached,
        // exactly as a real admin's browser session would need.
        DbTestHelpers.Seed(_strictFactory, new FrameworkUser
        {
            ID = Guid.NewGuid(),
            ITCode = AdminItCode,
            Password = PasswordHashHelper.HashPassword(AdminPassword),
            Name = "Issue #876 ETL admin",
            IsValid = true,
        });
        DbTestHelpers.Seed(_strictFactory, new FrameworkRole
        {
            ID = Guid.NewGuid(),
            RoleCode = AdminRoleCode,
            RoleName = "ETL Admin (#876 test)",
        });
        DbTestHelpers.Seed(_strictFactory, new FrameworkUserRole
        {
            ID = Guid.NewGuid(),
            UserCode = AdminItCode,
            RoleCode = AdminRoleCode,
        });

        // Page-level URL-RBAC grants (PrivilegeFilter), one FrameworkMenu + FunctionPrivilege
        // per action under test -- matching MvcAuthHolesTests' precedent (see that class's doc
        // comment on why Url must be the LinkGenerator-reverse-routed BaseUrl, not the literal
        // request path: default action "Index" is omitted from the generated URL).
        GrantMenuAccess(_strictFactory, "EtlMonitorRunning876", "/_EtlMonitor/Running", AdminRoleCode);
        GrantMenuAccess(_strictFactory, "EtlRunLogIndex876", "/_EtlRunLog", AdminRoleCode);
        GrantMenuAccess(_strictFactory, "EtlSchemaTables876", "/_EtlSchema/Tables", AdminRoleCode);
        DbTestHelpers.InvalidateMenuCache(_strictFactory);
    }

    private static void GrantMenuAccess(
        WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> factory, string pageName, string url, string roleCode)
    {
        var menu = DbTestHelpers.Seed(factory, new FrameworkMenu
        {
            ID = Guid.NewGuid(),
            PageName = pageName,
            Url = url,
            FolderOnly = false,
            IsInherit = false,
            ShowOnMenu = true,
            IsPublic = false,
            DisplayOrder = 0,
            IsInside = true,
            TenantAllowed = true,
        });
        DbTestHelpers.Seed(factory, new FunctionPrivilege
        {
            ID = Guid.NewGuid(),
            RoleCode = roleCode,
            MenuItemId = menu.ID,
            Allowed = true,
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

    // ─── #876 positive controls: a genuine ETLAdmin caller must SUCCEED ─────────────────
    //
    // Before #876, EVERY caller was rejected here, including a real Admin/ETLAdmin -- Wtm was
    // always null when the gate read it, so the "no roles" branch fired unconditionally. These
    // tests are the strongest evidence #876 is fixed: they can only pass if (1) Wtm is actually
    // non-null and correctly populated with this specific user's real roles by the time the
    // gate runs (proving DataContextFilter's effect now reaches these controllers), and (2) the
    // rest of the real HTTP pipeline -- PrivilegeFilter's separate page-level URL-RBAC check --
    // also runs and is satisfied by the FrameworkMenu/FunctionPrivilege grants seeded above.
    // #841 could only verify this half at the unit level (EtlRbacTests.cs) because of #876;
    // this is that gap closed with a real HTTP call.

    /// <summary>
    /// <b>Mutant target</b>: this is the positive control for
    /// <c>etl841-monitorcontroller-gate-neutralize</c>'s green_tests slot is a different,
    /// unrelated test (see that mutant's rationale for why); this test instead directly proves
    /// the #876 fix by succeeding where, before the fix, every caller including this real
    /// ETLAdmin was unconditionally forbidden.
    /// </summary>
    [TestMethod]
    public async Task EtlMonitorController_Running_Admin_Succeeds()
    {
        var client = await NewAuthClientAsync(AdminItCode, AdminPassword);
        var resp = await client.GetAsync("/_EtlMonitor/Running");
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"#876: a genuine ETLAdmin caller must pass _EtlMonitorController's role gate and " +
            $"reach the action -- this can only succeed if Wtm was populated with this user's " +
            $"real roles before the gate ran. Got {(int)resp.StatusCode} {resp.StatusCode}.");
    }

    [TestMethod]
    public async Task EtlRunLogController_Index_Admin_Succeeds()
    {
        var client = await NewAuthClientAsync(AdminItCode, AdminPassword);
        var resp = await client.GetAsync("/_EtlRunLog/Index");
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"#876: a genuine ETLAdmin caller must pass _EtlRunLogController's role gate, reach " +
            $"the action, and have Wtm.CreateVM<EtlRunLogListVM>() succeed (it dereferences Wtm " +
            $"directly, so this also proves Wtm is populated, not just the gate's null-safe " +
            $"read). Got {(int)resp.StatusCode} {resp.StatusCode}.");
    }

    [TestMethod]
    public async Task EtlSchemaController_Tables_Admin_Succeeds()
    {
        var client = await NewAuthClientAsync(AdminItCode, AdminPassword);
        // dbType=SQLite deliberately: EtlSchemaServiceFactory.Create only implements
        // SqlServer/Oracle schema introspection today (demo's only real connection is SQLite,
        // and this test environment has no SqlServer/Oracle available) -- so a genuinely
        // reached action body returns 501 NotSupportedException, not 200. That 501 is itself
        // the proof: Tables() first does `Wtm.ConfigInfo.Connections?.FirstOrDefault(...)`
        // (a direct Wtm dereference -- NRE if Wtm were still null) and only reaches
        // CreateSchemaService/the 501 branch once that lookup and the role gate both succeed.
        // Before #876 this request never got past the gate at all (302/403), so seeing the
        // *specific* NotSupportedException message -- not a gate denial -- is what proves the
        // fix, not incidentally a fragile assertion on unimplemented functionality.
        var resp = await client.GetAsync("/_EtlSchema/Tables?csKey=default&dbType=SQLite");
        Assert.AreEqual(HttpStatusCode.NotImplemented, resp.StatusCode,
            $"#876: a genuine ETLAdmin caller must pass _EtlSchemaController's role gate and " +
            $"reach the action body (Wtm.ConfigInfo.Connections lookup succeeds, then " +
            $"CreateSchemaService(SQLite) throws NotSupportedException, mapped to 501) -- not " +
            $"be rejected by the gate (302/403). Got {(int)resp.StatusCode} {resp.StatusCode}.");
        var body = await resp.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "not yet implemented",
            $"#876: expected EtlSchemaServiceFactory's NotSupportedException message for an " +
            $"unsupported dbType, proving the action body (which reads Wtm.ConfigInfo.Connections) " +
            $"actually ran. Got: {body[..Math.Min(300, body.Length)]}");
    }
}
