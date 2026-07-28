using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Api.Test;

/// <summary>
/// Issue #194 — MVC controller authorization holes.
/// HTTP-layer integration tests using DemoWebApplicationFactory + SQLite-backed demo app.
///
/// Covers:
///   MVC-006  Selector unauthenticated → 401 (AllowUnauthenticatedSelector=false by default)
///   MVC-010  GetPagingData / GetExportExcel with unknown CS key → 400
///   MVC-004  UpdateModelProperty blocked/validated (sensitive-field blocklist)
///   GAP-01   No-privilege authenticated user → 403; admin → 200 (PrivilegeFilter RBAC)
///   GAP-08   Denylisted JWT → 401 (tested via logout → re-access)
///
/// <para>
/// <b>#837 convention — strict factory is the default for authorization tests.</b> Every
/// authenticated helper in this class (<see cref="NewAuthClientAsync"/>) logs in through
/// <see cref="_strictFactory"/> (<c>IsQuickDebug=false</c>), not the raw <see cref="_factory"/>.
/// demo's own <c>appsettings.json</c> ships <c>IsQuickDebug: true</c>, and
/// <c>PrivilegeFilter.cs</c>'s RBAC denial check
/// (<c>canAccess == false &amp;&amp; controller.ConfigInfo.IsQuickDebug == false</c>) is
/// short-circuited entirely when that flag is true — an authorization test built against the
/// unmodified <see cref="_factory"/> would pass even if the RBAC check it claims to exercise
/// were deleted outright. <see cref="_factory"/> itself should only be reached for tests that
/// are deliberately NOT about authorization enforcement (e.g. a raw CS-key validation check
/// where IsQuickDebug is irrelevant to what's being asserted).
/// </para>
///
/// <para>
/// <b>How to override <c>Configs</c> for a test factory (do it this way, not the other way):</b>
/// every <c>Configs</c> flag override in this class goes through
/// <c>builder.ConfigureAppConfiguration((_, config) =&gt; config.AddInMemoryCollection(...))</c>
/// (see <see cref="ClassInit"/> below and
/// <c>GetFileName_EnforceFlagEnabled_Forbid_Is302RedirectNotLiteral403</c>'s locally-built
/// factory) — <b>never</b> <c>services.Configure&lt;Configs&gt;(o =&gt; ...)</c>. This is not a
/// style preference: <c>AddWtmContext</c> (<c>FrameworkServiceExtension.cs:402</c>) takes an
/// EAGER snapshot — <c>var conf = config.Get&lt;Configs&gt;();</c> — at service-registration
/// time, before the DI container exists, and it is THAT snapshot (not a live
/// <c>IOptions&lt;Configs&gt;</c>) that every piece of startup wiring downstream actually reads
/// (e.g. lines 548/587/604/710/760/925 of the same file — including the tenant-list bootstrap
/// patch 3's fixture depends on). A <c>services.Configure&lt;Configs&gt;</c> delegate registers
/// against the DI-resolved <c>IOptions&lt;Configs&gt;</c>, which nothing in that startup path
/// ever consults, so the override is silently ignored. <c>ConfigureAppConfiguration</c> instead
/// mutates the underlying <see cref="IConfiguration"/> BEFORE <c>config.Get&lt;Configs&gt;()</c>
/// runs, so the eager snapshot itself reflects the override.
/// <c>WalkingTec.Mvvm.Core.ConfigOptions.WtmUIOptions</c> is the opposite case — it
/// genuinely IS read through the live DI <c>IOptions&lt;WtmUIOptions&gt;</c>
/// (<c>services.Configure&lt;WtmUIOptions&gt;(config.GetSection("UIOptions"))</c> at the same
/// call site, resolved lazily in <c>UseWtmContext</c> after the container is built) — a
/// <c>services.Configure&lt;WtmUIOptions&gt;</c> delegate DOES work for that one option class.
/// This asymmetry between the two option types is exactly what <c>UIOptionsSplitBrainTests.cs</c>
/// exists to pin down; mixing the two patterns up is the single easiest way to write an
/// authorization test that silently tests nothing.
/// </para>
/// </summary>
[TestClass]
public class MvcAuthHolesTests
{
    private static DemoWebApplicationFactory _factory = null!;

    // Factory variant with IsQuickDebug=false so auth checks are enforced.
    // Used for tests that validate 401/redirect behaviour for unauthenticated requests, AND
    // (#837) as the default login factory for every authenticated test in this class — see
    // the class-level doc comment above for why _factory itself must not be used for anything
    // that claims to exercise RBAC.
    private static WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> _strictFactory = null!;

    // Factory variant with EnforceVmExportAuthorization=true (#796 fail-closed kill switch) and
    // a distinct AccessDeniedPath (demo's default AccessDeniedPath is the same as LoginPath —
    // "/Login/Login" — so without this override a redirect there is ambiguous between an
    // AccessDenied challenge and an unauthenticated-login challenge). Used to pin the actual
    // wire status of the un-overridden CanExportVm hook's Forbid() denial — see
    // GetExportExcel_EnforceFlagEnabled_Forbid_Is302RedirectToAccessDenied below.
    private static WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> _vmExportEnforcedFactory = null!;

    private const string StudentListVm =
        "WalkingTec.Mvvm.Demo.ViewModels.StudentVMs.StudentListVM";

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        _factory = new DemoWebApplicationFactory();

        // Override IsQuickDebug=false so PrivilegeFilter and auth middleware
        // actually enforce authentication (QuickDebug bypasses all RBAC).
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

        _vmExportEnforcedFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["EnforceVmExportAuthorization"] = "true",
                    ["CookieOptions:AccessDeniedPath"] = "/AccessDenied796Test",
                });
            });
        });

        // #837: under _strictFactory (IsQuickDebug=false), WTMContext.IsAccessable resolves
        // page privilege from the real FrameworkMenus/FunctionPrivileges tables instead of
        // IsQuickDebug=true's "reflect over every controller action, no privilege check at
        // all" shortcut (FrameworkServiceExtension.cs's GetAllMenus). demo's own seed data
        // (DataContext.DataInit) creates the admin FrameworkUser + its role-001
        // FrameworkUserRole, but never a FrameworkMenu or FunctionPrivilege row for ANYTHING —
        // so without this, even admin has zero configured page privileges under strict mode,
        // and every "admin should be able to access X" test in this class would 403 for real
        // (found the hard way: PrivilegeFilter_AdminUser_CanAccessPrivilegedPage went from a
        // false-positive 200 under IsQuickDebug=true to a genuine, correctly-enforced 403 the
        // moment _strictFactory became the login factory). Seed one real menu + privilege pair
        // for the one page this class's tests actually exercise as "admin", exactly the way an
        // admin UI would grant it, rather than reaching for another RBAC-bypass flag.
        //
        // Url is "/Student" — NOT "/Student/Index". PrivilegeFilter.cs computes
        // controller.BaseUrl via LinkGenerator.GetPathByAction(ad.ActionName, ad.ControllerName,
        // ...), and ASP.NET Core's conventional routing ({controller}/{action=Index}/{id?})
        // treats "Index" as the DEFAULT action value — the generator omits a segment that only
        // repeats its own default, so it reverse-routes StudentController.Index() to "/Student",
        // not "/Student/Index", regardless of which of those two equivalent URLs a caller
        // actually requests. A menu row keyed on the literal request path would silently never
        // match (confirmed empirically: instrumenting PrivilegeFilter showed
        // BaseUrl='/Student' for a GET to "/Student/Index").
        var studentIndexMenu = DbTestHelpers.Seed(_strictFactory, new FrameworkMenu
        {
            ID = Guid.NewGuid(),
            PageName = "Student",
            Url = "/Student",
            FolderOnly = false,
            IsInherit = false,
            ShowOnMenu = true,
            IsPublic = false,
            DisplayOrder = 0,
            IsInside = true,
            TenantAllowed = true,
        });
        DbTestHelpers.Seed(_strictFactory, new FunctionPrivilege
        {
            ID = Guid.NewGuid(),
            RoleCode = "001", // matches DataContext.DataInit's admin FrameworkUserRole seed.
            MenuItemId = studentIndexMenu.ID,
            Allowed = true,
        });
        DbTestHelpers.InvalidateMenuCache(_strictFactory);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _vmExportEnforcedFactory.Dispose();
        _strictFactory.Dispose();
        _factory.Dispose();
    }

    // ── helper: unauthenticated client (no cookies, no redirect) ────────────
    // Uses strict factory (IsQuickDebug=false) so auth is enforced.
    private HttpClient NewUnauthClient() =>
        _strictFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

    // ── helper: authenticated client via admin/000000 ───────────────────────
    // #837: uses _strictFactory (IsQuickDebug=false), not _factory — see the class-level
    // doc comment on why every authenticated/authorization test in this class must go
    // through the factory that actually enforces PrivilegeFilter's RBAC check.
    //
    // IsQuickDebug=false ALSO gates a second, orthogonal control: LoginController.cs's POST
    // action requires a VerifyCode matching the value GetVerifyCode stored server-side in
    // session. Under _factory (IsQuickDebug=true) that check never ran, so this helper never
    // needed one; under _strictFactory it does, and CaptchaTestHelper solves it by reading the
    // session store back directly (see that file's doc comment for the full mechanism).
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
        // AllowAutoRedirect=true means a SUCCESSFUL login (302 -> "/") and a FAILED login
        // (200, the same Login view re-rendered with a ModelError) both surface as 200 here —
        // the status code alone cannot distinguish them. LoginController's Login.cshtml marks a
        // failed attempt with a "login-error" span (see MultiTenantSeedFixtureTests' login test
        // for the exact markup); its absence is the actual success signal.
        var loginBody = await loginResp.Content.ReadAsStringAsync();
        if (loginBody.Contains("login-error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"#837: NewAuthClientAsync's admin login did not succeed under _strictFactory " +
                $"(IsQuickDebug=false). Response: {loginBody[..Math.Min(500, loginBody.Length)]}");
        }
        return client;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MVC-006: Selector endpoint requires authentication
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// MVC-006: POST /_Framework/Selector without a session cookie must block access.
    ///
    /// WTM's MVC (non-API) controllers return HTTP 200 with a JavaScript redirect to the
    /// login page when the user is not authenticated — this is the framework's design for
    /// LayUI-based page navigation. The key invariant is that the response must NOT contain
    /// actual Selector data (layui-table rows / option HTML) — it must be the login redirect.
    /// AllowUnauthenticatedSelector=false is the default (secure) setting.
    /// </summary>
    [TestMethod]
    public async Task Selector_UnauthenticatedRequest_RejectsWithoutData()
    {
        var client = NewUnauthClient();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = StudentListVm,
            ["_DONOT_USE_KFIELD"] = "Name",
            ["_DONOT_USE_VFIELD"] = "ID",
            ["_DONOT_USE_FIELD"] = "StudentId",
            ["_DONOT_USE_MULTI_SEL"] = "false",
            ["_DONOT_USE_SEL_ID"] = "sel1",
        });

        var resp = await client.PostAsync("/_Framework/Selector", form);
        var body = await resp.Content.ReadAsStringAsync();

        // WTM MVC pattern: unauthenticated access returns 200 with JS redirect OR 401.
        // A 401 is also acceptable (API-style response). What is NEVER acceptable is a
        // 200 response that contains actual selector data (layui rows, option elements).
        if (resp.StatusCode == HttpStatusCode.OK)
        {
            // Must be a login redirect JS snippet, not real selector data.
            // The JS redirect contains "window.location.href" pointing to /Login/Login.
            var isLoginRedirect = body.Contains("window.location.href", StringComparison.OrdinalIgnoreCase)
                || body.Contains("Login", StringComparison.OrdinalIgnoreCase)
                || body.Contains("ReturnUrl", StringComparison.OrdinalIgnoreCase);

            var hasRealData = body.Contains("layui-table", StringComparison.OrdinalIgnoreCase)
                || body.Contains("<option", StringComparison.OrdinalIgnoreCase);

            Assert.IsTrue(isLoginRedirect && !hasRealData,
                $"MVC-006: Unauthenticated Selector returned 200 with REAL DATA (not login redirect). " +
                $"Body excerpt: {body[..Math.Min(200, body.Length)]}");
        }
        else
        {
            // 401 / 302 / 403 are all acceptable rejections.
            Assert.IsTrue(
                resp.StatusCode == HttpStatusCode.Unauthorized ||
                resp.StatusCode == HttpStatusCode.Redirect ||
                resp.StatusCode == HttpStatusCode.Found ||
                resp.StatusCode == HttpStatusCode.Forbidden ||
                (int)resp.StatusCode >= 300,
                $"MVC-006: Unexpected status {(int)resp.StatusCode} ({resp.StatusCode}). " +
                $"Body: {body[..Math.Min(200, body.Length)]}");
        }
    }

    /// <summary>
    /// MVC-006: Authenticated user should be able to call Selector successfully.
    /// </summary>
    [TestMethod]
    public async Task Selector_AuthenticatedRequest_Succeeds()
    {
        var client = await NewAuthClientAsync();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = StudentListVm,
            ["_DONOT_USE_KFIELD"] = "Name",
            ["_DONOT_USE_VFIELD"] = "ID",
            ["_DONOT_USE_FIELD"] = "StudentId",
            ["_DONOT_USE_MULTI_SEL"] = "false",
            ["_DONOT_USE_SEL_ID"] = "sel1",
        });

        var resp = await client.PostAsync("/_Framework/Selector", form);

        // Authenticated users get 200 (partial view HTML) or an acceptable redirect.
        Assert.IsTrue(
            resp.StatusCode == HttpStatusCode.OK ||
            resp.StatusCode == HttpStatusCode.Redirect,
            $"MVC-006: Authenticated user should reach Selector. Got {(int)resp.StatusCode}.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MVC-010: Unknown connection-string key must be rejected
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// MVC-010: GetPagingData with an unrecognised _DONOT_USE_CS key must return 400.
    /// </summary>
    [TestMethod]
    public async Task GetPagingData_UnknownCsKey_ReturnsBadRequest()
    {
        var client = await NewAuthClientAsync();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = StudentListVm,
            ["_DONOT_USE_CS"] = "EVIL_LATERAL_DB",
        });

        var resp = await client.PostAsync("/_Framework/GetPagingData", form);
        var body = await resp.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode,
            $"MVC-010: GetPagingData should reject unknown CS key. Got {(int)resp.StatusCode}: {body[..Math.Min(500, body.Length)]}");
        // #837: status-code-only was tautological here — "EVIL_LATERAL_DB" is not a real
        // configured connection, so deleting the IsKnownConnectionKey guard
        // (_FrameworkController.cs:404-405) does not make the request succeed; it instead
        // fails downstream (CreateVM/query execution against a nonexistent CS) and a
        // different code path converts THAT exception into an unrelated, generic 400 body
        // ("错误") — confirmed empirically by deleting the guard and observing the test stay
        // green. Asserting the guard's own message text is what actually pins this guard.
        Assert.IsTrue(body.Contains("Unknown connection string key", StringComparison.OrdinalIgnoreCase),
            $"MVC-010: the 400 must come from the IsKnownConnectionKey guard specifically (its " +
            $"message), not from an unrelated downstream failure that would ALSO 400 a request " +
            $"naming a nonexistent connection string even without this guard. Got body: {body}");
    }

    /// <summary>
    /// MVC-010: GetPagingData with a null/empty CS key must succeed (uses the default).
    /// </summary>
    [TestMethod]
    public async Task GetPagingData_NullCsKey_UsesDefault()
    {
        var client = await NewAuthClientAsync();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = StudentListVm,
            // _DONOT_USE_CS not supplied → null → default
        });

        var resp = await client.PostAsync("/_Framework/GetPagingData", form);

        // 200 with JSON paging data (or redirect on stale session)
        Assert.IsTrue(
            resp.StatusCode == HttpStatusCode.OK ||
            resp.StatusCode == HttpStatusCode.Redirect,
            $"MVC-010: GetPagingData with null CS should use default. Got {(int)resp.StatusCode}.");
    }

    /// <summary>
    /// MVC-010: GetExportExcel with an unrecognised _DONOT_USE_CS key must return 400.
    /// </summary>
    [TestMethod]
    public async Task GetExportExcel_UnknownCsKey_ReturnsBadRequest()
    {
        var client = await NewAuthClientAsync();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = StudentListVm,
            ["_DONOT_USE_CS"] = "EVIL_LATERAL_DB",
        });

        var resp = await client.PostAsync("/_Framework/GetExportExcel", form);
        var body = await resp.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode,
            $"MVC-010: GetExportExcel should reject unknown CS key. Got {(int)resp.StatusCode}: {body[..Math.Min(500, body.Length)]}");
        // #837: same tautology as GetPagingData's sibling test above — "EVIL_LATERAL_DB" isn't a
        // real connection, so deleting IsKnownConnectionKey's guard (_FrameworkController.cs
        // GetExportExcel, ~line 682-683) doesn't make the request succeed; it fails downstream
        // and an unrelated code path converts THAT into a generic 400 ("错误") — confirmed
        // empirically. Assert the guard's own message text, not just the status code.
        Assert.IsTrue(body.Contains("Unknown connection string key", StringComparison.OrdinalIgnoreCase),
            $"MVC-010: the 400 must come from the IsKnownConnectionKey guard specifically (its " +
            $"message), not from an unrelated downstream failure that would ALSO 400 a request " +
            $"naming a nonexistent connection string even without this guard. Got body: {body}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MVC-004: UpdateModelProperty blocked fields
    //
    // #837: the three tests below previously targeted StudentListVm (a ListVM) with a
    // Guid.NewGuid() id that resolves to no row. `Wtm.CreateVM(...) as IBaseCRUDVM<TopBasePoco>`
    // is null for a ListVM, so UpdateModelProperty's very first entity guard
    // (_FrameworkController.cs "if (vm?.Entity == null) return BadRequest("Entity not
    // found")", a few lines after the blockedFields check) returned 400 on EVERY request
    // regardless of field name — before the request ever reached the blocklist. Deleting the
    // entire blockedFields HashSet left all three tests green for the wrong reason: the
    // request never got far enough to depend on it either way. Fixed by pointing at a real
    // CRUD VM (FrameworkUserVM) over a real seeded FrameworkUser row, so a would-be 200 is the
    // only other outcome and the blocklist (or, for the dot-notation case, the separate
    // navigation-path guard) is the only thing standing in its way.
    // ═══════════════════════════════════════════════════════════════════════

    private const string FrameworkUserVm =
        "WalkingTec.Mvvm.Mvc.Admin.ViewModels.FrameworkUserVms.FrameworkUserVM";

    /// <summary>
    /// Seeds a real, queryable FrameworkUser row via <see cref="DbTestHelpers"/>.
    /// TenantCode is left null deliberately: demo's default connection has a null
    /// <c>IDataContext.TenantCode</c>, and FrameworkUser is an <c>ITenant</c> entity subject to
    /// the framework's unconditional <c>TenantCode == this.TenantCode</c> global query filter
    /// (see DbTestHelpers's doc comment) — a non-null TenantCode here would make the seeded row
    /// invisible to <c>Wtm.CreateVM</c> inside the running app and silently reintroduce the
    /// exact "Entity not found" masking these tests exist to rule out.
    /// </summary>
    private static FrameworkUser SeedFrameworkUser() =>
        DbTestHelpers.Seed(_strictFactory, new FrameworkUser
        {
            ID = Guid.NewGuid(),
            ITCode = $"t837_{Guid.NewGuid():N}",
            Password = "not-a-real-hash",
            Name = "Issue #837 fixture user",
            IsValid = true,
            TenantCode = null,
        });

    /// <summary>
    /// MVC-004: Attempting to inline-edit the "Password" field must return 400 — depends on
    /// the blockedFields HashSet (_FrameworkController.cs:519-528). Confirmed by temporarily
    /// emptying that HashSet: this test goes RED (200, not 400); restoring it, GREEN again.
    /// </summary>
    [TestMethod]
    public async Task UpdateModelProperty_PasswordField_ReturnsBadRequest()
    {
        var user = SeedFrameworkUser();
        var client = await NewAuthClientAsync();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = FrameworkUserVm,
            ["id"] = user.ID.ToString(),
            ["field"] = "Password",
            ["value"] = "hacked",
        });

        var resp = await client.PostAsync("/_Framework/UpdateModelProperty", form);

        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode,
            $"MVC-004: Password field must be blocked. Got {(int)resp.StatusCode}.");
    }

    /// <summary>
    /// MVC-004: Attempting to inline-edit using dot-notation navigation paths must return 400
    /// FROM THE DOT-NOTATION GUARD SPECIFICALLY (_FrameworkController.cs:513-516,
    /// `if (field.Contains('.')) return BadRequest("Navigation property paths are not allowed
    /// for inline editing")`) — not merely 400 for some other reason. Uses FrameworkUser's real
    /// "Photo" navigation (a FileAttachment) so the payload targets an actual traversable
    /// property, not a name with no meaning on the entity.
    ///
    /// Asserting status code alone here is tautological: "Photo.FileName" is not a real
    /// property name on FrameworkUser, so if the dot-notation guard is deleted, the request
    /// falls through to the property-existence check a few lines later
    /// (`entityType.GetProperty(field, ...)`, :555) — `GetProperty("Photo.FileName")` returns
    /// null (dots aren't legal in a CLR member name), and THAT check independently returns 400
    /// ("Field not found or not writable", :558). A status-code-only assertion cannot tell
    /// those two 400s apart, so it would stay green with the guard it claims to test deleted —
    /// confirmed by deliberately deleting the guard and observing exactly this (see PR
    /// description for the RED/GREEN transcript). Asserting the guard's own message text is
    /// what actually pins this guard, independent of the property-existence fallback.
    ///
    /// This guard is independent of the blockedFields HashSet the Password/ITCode tests above
    /// depend on — it runs earlier and unconditionally, by name, regardless of which field is
    /// targeted, so emptying that HashSet does not and should not turn this test red.
    /// </summary>
    [TestMethod]
    public async Task UpdateModelProperty_DotNotationField_ReturnsBadRequest()
    {
        var user = SeedFrameworkUser();
        var client = await NewAuthClientAsync();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = FrameworkUserVm,
            ["id"] = user.ID.ToString(),
            ["field"] = "Photo.FileName",
            ["value"] = "hacked",
        });

        var resp = await client.PostAsync("/_Framework/UpdateModelProperty", form);
        var body = await resp.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode,
            $"MVC-004: Dot-notation navigation paths must be blocked. Got {(int)resp.StatusCode}: {body}");
        Assert.IsTrue(body.Contains("Navigation property paths are not allowed", StringComparison.OrdinalIgnoreCase),
            $"MVC-004: the 400 must come from the dot-notation guard specifically (its message), " +
            $"not from the unrelated property-existence check that would ALSO 400 a literal " +
            $"\"Photo.FileName\" property lookup if the dot-notation guard were deleted. " +
            $"Got body: {body}");
    }

    /// <summary>
    /// MVC-004: Attempting to inline-edit the "ITCode" field must return 400 — depends on the
    /// blockedFields HashSet (_FrameworkController.cs:519-528). Confirmed by temporarily
    /// emptying that HashSet: this test goes RED (200, not 400); restoring it, GREEN again.
    /// </summary>
    [TestMethod]
    public async Task UpdateModelProperty_ITCodeField_ReturnsBadRequest()
    {
        var user = SeedFrameworkUser();
        var client = await NewAuthClientAsync();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = FrameworkUserVm,
            ["id"] = user.ID.ToString(),
            ["field"] = "ITCode",
            ["value"] = "attacker-controlled-itcode",
        });

        var resp = await client.PostAsync("/_Framework/UpdateModelProperty", form);

        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode,
            $"MVC-004: ITCode field must be blocked. Got {(int)resp.StatusCode}.");
    }

    /// <summary>
    /// #837 patch 2: proves the DB read-back helper by exercising UpdateModelProperty's full
    /// write path end-to-end — seed a real row, edit an ALLOWED field over real HTTP, then
    /// open a fresh scope (not the one that seeded it, and not the one the request itself used)
    /// and read the row back to confirm the database — not just the wire — actually changed.
    /// Every other test in this class before #837 asserted status codes only.
    /// </summary>
    [TestMethod]
    public async Task UpdateModelProperty_AllowedField_ActuallyPersistsToDatabase()
    {
        var user = SeedFrameworkUser();
        var client = await NewAuthClientAsync();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = FrameworkUserVm,
            ["id"] = user.ID.ToString(),
            ["field"] = "Name",
            ["value"] = "Updated By Issue #837 Test",
        });

        var resp = await client.PostAsync("/_Framework/UpdateModelProperty", form);
        var body = await resp.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"#837: an allowed field on a real, seeded entity should succeed. " +
            $"Got {(int)resp.StatusCode}: {body}");

        var persisted = DbTestHelpers.ReadBack<FrameworkUser>(_strictFactory, user.ID);
        Assert.IsNotNull(persisted, "#837: the seeded row must still exist after the edit.");
        Assert.AreEqual("Updated By Issue #837 Test", persisted!.Name,
            "#837: UpdateModelProperty returned 200 but the database read-back shows the old " +
            "value — the wire-level status code alone (what every prior test in this class " +
            "asserted) does not prove the write actually happened.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GAP-01: PrivilegeFilter RBAC — unauthenticated → redirect/401,
    //         authenticated admin → 200
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GAP-01: Unauthenticated request to a privileged MVC page must be blocked.
    ///
    /// WTM's PrivilegeFilter returns HTTP 200 with a JavaScript redirect to the login page
    /// for non-API (BaseController) endpoints — this is the framework's design pattern for
    /// LayUI page navigation. The test verifies that the response either:
    ///   (a) is a non-200 redirect / 401 / 403, OR
    ///   (b) is a 200 response whose body is the JS login redirect, NOT actual page content.
    /// IsQuickDebug=false is enforced via _strictFactory to disable the RBAC bypass.
    ///
    /// #837: this used to check for "StudentId" as the privileged-content marker, copying
    /// PrivilegeFilter_AdminUser_CanAccessPrivilegedPage's original (also wrong) assumption.
    /// "StudentId" never appears anywhere in the real rendered page (the grid's column
    /// definitions are populated client-side via a later GetPagingData AJAX call, not embedded
    /// in the initial HTML) — confirmed by dumping the real authenticated response body, same
    /// as the admin test's fix. That made `hasPrivilegedContent` permanently false, so this
    /// assertion reduced to `isLoginRedirect` alone: a genuine leak that happened to also
    /// contain "window.location.href" somewhere (a string that appears on essentially every
    /// page this framework renders, including the real Student/Index page itself, in unrelated
    /// layui init script) would have passed silently. Switched to the same verified markers as
    /// the admin test (`action="/Student"` + `StudentListVM`). Proven by disabling
    /// PrivilegeFilter.cs's `controller.Wtm.LoginUserInfo == null` unauthenticated-redirect
    /// guard (forcing an unauthenticated request through to the real action) and confirming
    /// this test goes RED — see the PR description for the transcript.
    /// </summary>
    [TestMethod]
    public async Task PrivilegeFilter_UnauthenticatedUser_RedirectsOrReturns401()
    {
        var client = NewUnauthClient();

        var resp = await client.GetAsync("/Student/Index");
        var body = await resp.Content.ReadAsStringAsync();

        if (resp.StatusCode == HttpStatusCode.OK)
        {
            // WTM MVC pattern: returns 200 with embedded JS redirect to the login page.
            // Verify the body is the login-redirect script, not the privileged student list.
            var isLoginRedirect = body.Contains("window.location.href", StringComparison.OrdinalIgnoreCase)
                || body.Contains("/Login/Login", StringComparison.OrdinalIgnoreCase)
                || body.Contains("ReturnUrl", StringComparison.OrdinalIgnoreCase);

            // Markers verified present in the real authenticated Student/Index response and
            // verified absent from the JS-redirect body — see
            // PrivilegeFilter_AdminUser_CanAccessPrivilegedPage's doc comment for how these
            // were confirmed (NOT "StudentId"/"layui-table", which never distinguishes the two).
            var hasPrivilegedContent = body.Contains("action=\"/Student\"", StringComparison.OrdinalIgnoreCase)
                && body.Contains("StudentListVM", StringComparison.OrdinalIgnoreCase);

            Assert.IsTrue(isLoginRedirect && !hasPrivilegedContent,
                $"GAP-01: Unauthenticated request returned 200 with PRIVILEGED CONTENT (not login redirect). " +
                $"Body excerpt: {body[..Math.Min(300, body.Length)]}");
        }
        else
        {
            Assert.IsTrue(
                resp.StatusCode == HttpStatusCode.Unauthorized ||
                resp.StatusCode == HttpStatusCode.Redirect ||
                resp.StatusCode == HttpStatusCode.Found ||
                resp.StatusCode == HttpStatusCode.Forbidden ||
                (int)resp.StatusCode >= 300,
                $"GAP-01: Unexpected status {(int)resp.StatusCode}. Body: {body[..Math.Min(200, body.Length)]}");
        }
    }

    /// <summary>
    /// GAP-01: Admin user must be able to access privileged pages (200) — and the 200 must be
    /// the actual privileged content, not the same "200 with a JS login-redirect" response
    /// PrivilegeFilter_UnauthenticatedUser_RedirectsOrReturns401 (above) proves an
    /// UNauthenticated caller also gets. #837: status-code-only was tautological here — if
    /// NewAuthClientAsync's login silently failed for any reason, this GET would (per that
    /// same GAP-01 pattern) still return 200, just with the login-redirect script instead of
    /// the student list, and this assertion would not have noticed.
    ///
    /// Markers: NOT the "layui-table" + "StudentId" pair the unauthenticated counterpart test
    /// above uses — verified by capturing the real rendered page that "StudentId" never
    /// appears in it at all (the grid's column definitions are populated client-side via a
    /// later GetPagingData AJAX call, not embedded in the initial HTML), so requiring it here
    /// would make this assertion permanently fail regardless of whether access is real. Uses
    /// `action="/Student"` (the search form's real post target) and the `StudentListVM` type
    /// name instead — both confirmed present in a real authenticated response and confirmed
    /// ABSENT from the unauthenticated JS-redirect body (a bare
    /// `&lt;script&gt;...window.location.href='/Login/Login'...&lt;/script&gt;`).
    /// </summary>
    [TestMethod]
    public async Task PrivilegeFilter_AdminUser_CanAccessPrivilegedPage()
    {
        var client = await NewAuthClientAsync();

        var resp = await client.GetAsync("/Student/Index");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"GAP-01: Admin should access Student/Index. Got {(int)resp.StatusCode}.");
        var isLoginRedirect = body.Contains("window.location.href", StringComparison.OrdinalIgnoreCase)
            || body.Contains("/Login/Login", StringComparison.OrdinalIgnoreCase);
        var hasPrivilegedContent = body.Contains("action=\"/Student\"", StringComparison.OrdinalIgnoreCase)
            && body.Contains("StudentListVM", StringComparison.OrdinalIgnoreCase);
        Assert.IsTrue(hasPrivilegedContent && !isLoginRedirect,
            $"GAP-01: 200 status alone is not enough — the body must be the actual privileged " +
            $"Student/Index content, not a login-redirect 200 masquerading as success (which " +
            $"would mean the admin login never actually worked). Body excerpt: " +
            $"{body[..Math.Min(300, body.Length)]}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GAP-08: Denylisted JWT / session invalidation — post-logout access is blocked
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GAP-08: After logout, the session cookie must no longer grant access to
    /// privileged pages (redirect or 401, not 200 with private data).
    /// </summary>
    [TestMethod]
    public async Task AfterLogout_SessionInvalidated_ProtectedPageBlocked()
    {
        // Step 1: Login and verify access. #837: status-code-only was tautological — same
        // "200 could be the login-redirect page instead of real content" gap as
        // PrivilegeFilter_AdminUser_CanAccessPrivilegedPage above; check the body too, so a
        // silently-failed login can't produce a false "before logout: had access" baseline
        // that the rest of this test's "after logout: lost access" comparison would be
        // meaningless against.
        var client = await NewAuthClientAsync();
        var beforeLogout = await client.GetAsync("/Student/Index");
        var beforeLogoutBody = await beforeLogout.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, beforeLogout.StatusCode,
            "Before logout: should have access to Student/Index");
        // Markers: see PrivilegeFilter_AdminUser_CanAccessPrivilegedPage's doc comment — NOT
        // "StudentId", which never appears in the real rendered page at all.
        Assert.IsTrue(
            beforeLogoutBody.Contains("action=\"/Student\"", StringComparison.OrdinalIgnoreCase) &&
            beforeLogoutBody.Contains("StudentListVM", StringComparison.OrdinalIgnoreCase),
            $"Before logout: the 200 must be the actual Student/Index content, not a " +
            $"login-redirect 200 (which would mean the login never worked, making the rest of " +
            $"this test meaningless). Body excerpt: " +
            $"{beforeLogoutBody[..Math.Min(300, beforeLogoutBody.Length)]}");

        // Step 2: Logout
        await client.GetAsync("/Login/Logout");

        // Step 3: Re-attempt — now using a no-redirect client pointing at the same cookie jar.
        // Re-create with no auto-redirect so we can inspect the response code directly.
        var noRedirectClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        // Copy cookies manually is not directly supported in WebApplicationFactory, but the
        // logout handler clears the auth cookie/session regardless of IsQuickDebug (#837:
        // this comment used to say "the demo runs IsQuickDebug=true" — no longer true now that
        // NewAuthClientAsync logs in through _strictFactory; logout invalidation itself was
        // never gated on that flag, only RBAC/menu resolution was, so this assertion's
        // validity is unaffected either way). We verify the body no longer contains
        // student-specific data content.
        var afterLogout = await client.GetAsync("/Student/Index");

        // After logout the client may follow auto-redirect to the login page (200),
        // be returned a 302, or get a 401.  What is NOT acceptable is receiving the
        // original privileged 200 with student data.
        var body = await afterLogout.Content.ReadAsStringAsync();

        // If we get a 200, it must be the login page (not the student list).
        if (afterLogout.StatusCode == HttpStatusCode.OK)
        {
            // The login page contains "ITCode" or a login-button; the student list does not.
            // Accept if it looks like a login page or if it's a redirect target.
            var looksLikeLogin = body.Contains("ITCode", StringComparison.OrdinalIgnoreCase)
                || body.Contains("Login", StringComparison.OrdinalIgnoreCase)
                || body.Contains("login-button", StringComparison.OrdinalIgnoreCase);

            // We accept either a login redirect OR a 200 that looks like login.
            // What we must NOT accept is a 200 that contains sensitive data without login.
            // #837: resp200ContainsStudentData used to check for "StudentId", which never
            // appears in the real page — that made it permanently return false, which made
            // this ENTIRE assertion unconditionally true (`looksLikeLogin || true`) regardless
            // of body content: a real post-logout leak would have passed silently. Fixed
            // resp200ContainsStudentData to use verified markers (see its own doc comment).
            // Proven by disabling LoginController.Logout()'s HttpContext.SignOutAsync call
            // (simulating "logout doesn't actually invalidate the session") and confirming this
            // test goes RED — see the PR description for the transcript. `looksLikeLogin`
            // itself remains an OR of loose markers ("ITCode"/"Login"/"login-button") not
            // reused from a verified-safe source; empirically it did NOT false-positive on the
            // real leaked page during that same RED proof, but it has not been hardened the
            // way the other two markers above were.
            Assert.IsTrue(looksLikeLogin || resp200ContainsStudentData(body) == false,
                "GAP-08: After logout, 200 response must be the login page, not student data.");
        }
        else
        {
            Assert.IsTrue(
                (int)afterLogout.StatusCode is 302 or 301 or 401 or 403,
                $"GAP-08: After logout, expected redirect or 401/403, got {(int)afterLogout.StatusCode}.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // #503: Selector must apply the same IsKnownConnectionKey guard as
    //       GetPagingData / GetExportExcel to prevent cross-DB reads.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// #503: POST /_Framework/Selector with an unrecognised _DONOT_USE_CURRENTCS key
    /// must return 400 BadRequest — not silently point the query at an arbitrary DB.
    /// </summary>
    [TestMethod]
    public async Task Selector_UnknownCurrentCs_ReturnsBadRequest()
    {
        var client = await NewAuthClientAsync();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = StudentListVm,
            ["_DONOT_USE_KFIELD"] = "Name",
            ["_DONOT_USE_VFIELD"] = "ID",
            ["_DONOT_USE_FIELD"] = "StudentId",
            ["_DONOT_USE_MULTI_SEL"] = "false",
            ["_DONOT_USE_SEL_ID"] = "sel1",
            ["_DONOT_USE_CURRENTCS"] = "EVIL_LATERAL_DB",
        });

        var resp = await client.PostAsync("/_Framework/Selector", form);

        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode,
            $"#503: Selector should reject unknown _DONOT_USE_CURRENTCS key. Got {(int)resp.StatusCode}.");
    }

    /// <summary>
    /// #503: POST /_Framework/Selector with a null/empty _DONOT_USE_CURRENTCS key
    /// must proceed normally (uses the default connection — safe path).
    /// </summary>
    [TestMethod]
    public async Task Selector_NullCurrentCs_UsesDefault()
    {
        var client = await NewAuthClientAsync();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = StudentListVm,
            ["_DONOT_USE_KFIELD"] = "Name",
            ["_DONOT_USE_VFIELD"] = "ID",
            ["_DONOT_USE_FIELD"] = "StudentId",
            ["_DONOT_USE_MULTI_SEL"] = "false",
            ["_DONOT_USE_SEL_ID"] = "sel1",
            // _DONOT_USE_CURRENTCS not supplied → null → default connection (always safe)
        });

        var resp = await client.PostAsync("/_Framework/Selector", form);

        // Authenticated admin with null CS should reach the selector (200/redirect).
        Assert.IsTrue(
            resp.StatusCode == HttpStatusCode.OK ||
            resp.StatusCode == HttpStatusCode.Redirect,
            $"#503: Selector with null _DONOT_USE_CURRENTCS should use default. Got {(int)resp.StatusCode}.");
    }

    // #837: "StudentId" never appears anywhere in the real rendered Student/Index page (the
    // grid's column definitions are populated client-side via a later GetPagingData AJAX call,
    // not embedded in the initial HTML — confirmed by dumping the real response body, same as
    // PrivilegeFilter_AdminUser_CanAccessPrivilegedPage's and
    // PrivilegeFilter_UnauthenticatedUser_RedirectsOrReturns401's fixes). That made this
    // function permanently return false, which made every caller's "... || ThisFunc(body) ==
    // false" pattern UNCONDITIONALLY true — not just weak, but a guaranteed pass regardless of
    // what body actually contained. Switched to the same verified markers as those two fixes.
    private static bool resp200ContainsStudentData(string body) =>
        body.Contains("action=\"/Student\"", StringComparison.OrdinalIgnoreCase) &&
        body.Contains("StudentListVM", StringComparison.OrdinalIgnoreCase);

    // ═══════════════════════════════════════════════════════════════════════
    // #796 (review round 2/3, LOW): pin the actual wire status of a CanExportVm denial.
    // Forbid() under the default cookie auth scheme is a 302 redirect to AccessDeniedPath,
    // not a literal 403 — framework_layui.js's AJAX callers see a redirect-to-HTML response.
    // This is kept consistent with every other Forbid() already in _FrameworkController
    // (e.g. BatchAssignRoles' CallerIsAdmin() checks) rather than special-cased; this test
    // exists so a future auth-scheme change (or a switch to StatusCode(403)) trips a test
    // instead of silently changing what callers see. Round 3 strengthens the assertion from
    // status-code-only to also checking the Location header: demo's default AccessDeniedPath
    // equals LoginPath ("/Login/Login"), so a bare 302 status cannot by itself distinguish an
    // AccessDenied challenge from an unauthenticated-login challenge — _vmExportEnforcedFactory
    // configures a distinct AccessDeniedPath so the two are actually distinguishable here.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// #796: GetExportExcel's un-overridden CanExportVm denial (flag enabled, no override)
    /// must surface as the SAME 302 redirect every other Forbid() in this controller produces
    /// under the default cookie auth scheme — not a literal HTTP 403 — and that redirect must
    /// actually point at the configured AccessDeniedPath, not the login-challenge path.
    /// </summary>
    [TestMethod]
    public async Task GetExportExcel_EnforceFlagEnabled_Forbid_Is302RedirectToAccessDenied()
    {
        var client = _vmExportEnforcedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        var loginForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ITCode"] = "admin",
            ["Password"] = "000000",
        });
        await client.PostAsync("/Login/Login", loginForm);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = StudentListVm,
        });
        var resp = await client.PostAsync("/_Framework/GetExportExcel", form);

        Assert.AreEqual(HttpStatusCode.Redirect, resp.StatusCode,
            $"#796: With EnforceVmExportAuthorization=true and no CanExportVm override, " +
            $"GetExportExcel's Forbid() is expected to wire up as a 302 redirect (ASP.NET Core " +
            $"cookie auth's Forbid() behaviour), not a literal 403. " +
            $"Got {(int)resp.StatusCode} ({resp.StatusCode}).");

        var location = resp.Headers.Location?.ToString() ?? string.Empty;
        Assert.IsTrue(location.Contains("/AccessDenied796Test", StringComparison.OrdinalIgnoreCase),
            $"#796: the redirect must target the configured AccessDeniedPath, proving this is an " +
            $"authorization denial and not an unauthenticated-login challenge. Location: {location}");
        Assert.IsFalse(location.Contains("/Login/Login", StringComparison.OrdinalIgnoreCase),
            $"#796: the redirect must NOT be the login-challenge path — that would mean the " +
            $"caller was treated as unauthenticated rather than denied by CanExportVm. Location: {location}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // #814: pin the actual wire status of a CanAccessFile denial. Forbid() under the
    // default cookie auth scheme is a 302 redirect to AccessDeniedPath, not a literal 403 —
    // framework_layui.js's AJAX callers see a redirect-to-HTML response. This is kept
    // consistent with every other Forbid() already in _FrameworkController (e.g.
    // BatchAssignRoles' CallerIsAdmin() checks) rather than special-cased; this test exists
    // so a future auth-scheme change (or a switch to StatusCode(403)) trips a test instead of
    // silently changing what callers see.
    //
    // The EnforceFileAccessAuthorization=true factory variant is built lazily, inside this
    // one test, rather than eagerly in ClassInitialize alongside _factory/_strictFactory: a
    // third WebApplicationFactory booted for every test in this class (most of which never
    // touch it) was found to occasionally race the shared demo.db schema sync during
    // ClassInitialize, an intermittent flake unrelated to what any individual test exercises.
    // Building it on demand, scoped to (and disposed by) the single test that needs it,
    // removes that shared-state window entirely.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// #814: GetFileName's un-overridden CanAccessFile denial (flag enabled, no override)
    /// must surface as the SAME 302 redirect every other Forbid() in this controller produces
    /// under the default cookie auth scheme — not a literal HTTP 403.
    /// </summary>
    [TestMethod]
    public async Task GetFileName_EnforceFlagEnabled_Forbid_Is302RedirectNotLiteral403()
    {
        using var fileAccessEnforcedFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["EnforceFileAccessAuthorization"] = "true",
                });
            });
        });

        var client = fileAccessEnforcedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        var loginForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ITCode"] = "admin",
            ["Password"] = "000000",
        });
        await client.PostAsync("/Login/Login", loginForm);

        var resp = await client.GetAsync($"/_Framework/GetFileName?id={Guid.NewGuid()}&_DONOT_USE_CS=");

        Assert.AreEqual(HttpStatusCode.Redirect, resp.StatusCode,
            $"#814: With EnforceFileAccessAuthorization=true and no CanAccessFile override, " +
            $"GetFileName's Forbid() is expected to wire up as a 302 redirect to AccessDeniedPath " +
            $"(ASP.NET Core cookie auth's Forbid() behaviour), not a literal 403. " +
            $"Got {(int)resp.StatusCode} ({resp.StatusCode}).");
    }
}
