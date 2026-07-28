using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Demo.Models._Admin;

namespace WalkingTec.Mvvm.Api.Test;

/// <summary>
/// Issue #830 — the demo <c>FileApiController</c> (LayUI copy; Vue3/Blazor copies are
/// byte-for-byte the same four fixes applied by construction, and are covered by reasoning
/// here, not by a second HTTP harness — this project only boots the LayUI demo, see
/// <see cref="DemoWebApplicationFactory"/>) had nine holes across four categories:
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Five <c>[Public]</c> (unauthenticated) endpoints, combined with
/// <c>FileUploadOptions.EnforceTenantFileScope</c> defaulting to false at the time (<c>IgnoreQueryFilters()</c>
/// in <see cref="WalkingTec.Mvvm.Core.Support.FileHandlers.WtmFileProvider.GetFile"/>) —
/// unauthenticated arbitrary cross-tenant file content read. <b>#859 update:</b> that default
/// flipped to <c>true</c> — see the class-level <c>EnforceTenantFileScope</c> remarks and
/// <see cref="NewTenantEnabledStrictFactory"/>'s doc comment below for what that changed about
/// this file's own tests.</item>
/// <item><c>DeletedFile</c> called the non-tenant-scoped
/// <see cref="WalkingTec.Mvvm.Core.Support.FileHandlers.WtmFileProvider.DeleteFile"/> instead of
/// <see cref="WalkingTec.Mvvm.Core.Support.FileHandlers.WtmFileProvider.DeleteFileTenantScoped"/>,
/// and was an HTTP GET performing a delete. <b>#859 update:</b> <c>DeleteFile</c> is
/// flag-dependent (<c>WtmFileProvider.cs:208</c>) while <c>DeleteFileTenantScoped</c> is
/// unconditional (<c>:227</c>) — see <see cref="NewTenantEnabledStrictFactory"/> for why that
/// distinction now requires an explicit <c>EnforceTenantFileScope=false</c> to stay
/// observable/testable.</item>
/// <item><c>csName</c> flowed into <c>Wtm.CreateDC(cskey:)</c> on all eight actions with zero
/// validation against <see cref="WTMContext.IsKnownConnectionKey"/>.</item>
/// <item><c>GetFileInfo</c> queried <c>dc.Set&lt;FileAttachment&gt;()</c> directly, bypassing
/// <see cref="WalkingTec.Mvvm.Core.Support.FileHandlers.WtmFileProvider"/> entirely and returning
/// the whole entity.</item>
/// </list>
///
/// <para>
/// Every test below follows the #837 conventions established in this project:
/// <c>IsQuickDebug=false</c> via <c>ConfigureAppConfiguration</c> +
/// <c>AddInMemoryCollection</c> (never <c>services.Configure&lt;Configs&gt;</c> — see
/// <c>MvcAuthHolesTests</c>' class doc comment for why), a fresh
/// <see cref="DemoWebApplicationFactory"/> built locally per test method (not shared at class
/// level — a shared eager factory was found to race the demo.db schema sync, see
/// <c>MvcAuthHolesTests.GetFileName_EnforceFlagEnabled_Forbid_Is302RedirectNotLiteral403</c>), and
/// a positive control in every test method that also carries a negative assertion — the
/// legitimate path must succeed in the SAME test that proves the illegitimate path is rejected.
/// </para>
/// </remarks>
[TestClass]
public class FileApiControllerHardeningTests830
{
    private static WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> NewStrictFactory(DemoWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["IsQuickDebug"] = "false",
                });
            });
        });

    // #859 CI follow-up: this factory's one consumer (DeletedFile_CrossTenant_...) exists to pin
    // WtmFileProvider.DeleteFileTenantScoped's UNCONDITIONAL tenant scoping — the property that
    // makes it strictly better than the flag-dependent WtmFileProvider.DeleteFile
    // (WtmFileProvider.cs:208 vs :227). #859 flipped FileUploadOptions.EnforceTenantFileScope's
    // default from false to true, which means DeleteFile is now ALSO tenant-scoped by default —
    // the two call paths became observably identical under the default config, and the #830
    // mutant that reverts DeletedFile from DeleteFileTenantScoped back to DeleteFile stopped
    // being detectable (it changed nothing observable, so the test stayed green: SURVIVED, caught
    // by the #834 mutation gate on this exact PR). EnforceTenantFileScope=false is set here
    // EXPLICITLY, not left at the default, so the test keeps exercising the one configuration
    // where DeleteFileTenantScoped's unconditional property is actually distinguishable from
    // DeleteFile's opt-out-able one — the same correction already applied to the #815 tests in
    // TenantIsolationFixTests.cs/DeletedFileIdsAuthorizationTests815.cs for the identical reason.
    private static WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> NewTenantEnabledStrictFactory(DemoWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["EnableTenant"] = "true",
                    ["IsQuickDebug"] = "false",
                    ["FileUploadOptions:EnforceTenantFileScope"] = "false",
                });
            });
        });

    /// <summary>
    /// Logs in as admin/000000 through <paramref name="strictFactory"/> (IsQuickDebug=false, so
    /// LoginController's VerifyCode check is enforced — see CaptchaTestHelper's doc comment for
    /// the mechanism) and returns an authenticated, cookie-carrying client.
    /// </summary>
    private static async Task<HttpClient> AdminLoginAsync(WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> strictFactory)
    {
        var client = strictFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = true,
            HandleCookies = true,
        });
        var verifyCode = await CaptchaTestHelper.FetchAndSolveAsync(strictFactory, client);
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
                $"#830: admin login did not succeed under the strict factory. Response: " +
                $"{loginBody[..Math.Min(500, loginBody.Length)]}");
        }
        return client;
    }

    /// <summary>
    /// Seeds a <see cref="FileAttachment"/> whose bytes are stored inline (SaveMode="database")
    /// so <see cref="WalkingTec.Mvvm.Core.Support.FileHandlers.WtmDataBaseFileHandler.GetFileData"/>
    /// can read them back with no local-filesystem dependency, and returns the id plus a marker
    /// string embedded in the file content — distinguishing "the response contains the actual
    /// file bytes" from "the response is merely non-empty".
    /// </summary>
    private static (Guid Id, string Marker) SeedMarkerFile(WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> factory, string? tenantCode)
    {
        var marker = $"WTM-830-SECRET-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes(marker);
        var file = DbTestHelpers.Seed(factory, new FileAttachment
        {
            ID = Guid.NewGuid(),
            FileName = "secret830.txt",
            FileExt = "txt",
            Length = bytes.Length,
            UploadTime = DateTime.UtcNow,
            SaveMode = "database",
            FileData = bytes,
            TenantCode = tenantCode,
        });
        return (file.ID, marker);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Category 1 — [Public] removal: GetFile must reject an unauthenticated caller and must
    // never leak the underlying file bytes while doing so.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// #830: an unauthenticated GET to GetFile for a real, seeded FileAttachment id must be
    /// rejected — and, decisively, the response body must not contain the actual file content.
    /// A bare non-200 status code is not by itself proof of rejection (a 200 whose body somehow
    /// omitted the bytes would also "pass" a status-only check the wrong way, and conversely a
    /// non-200 error page that happened to echo the bytes back would "pass" a status-only check
    /// for the wrong reason) — asserting the marker's absence is what actually pins this guard,
    /// same principle as MVC-006's hasRealData check in MvcAuthHolesTests. The positive control
    /// in the same test proves the marker WOULD be found if access were legitimately granted, so
    /// the negative assertion cannot be vacuously true because the marker never appears anywhere.
    /// </summary>
    [TestMethod]
    public async Task GetFile_Unauthenticated_RejectedAndDoesNotLeakContent_AuthenticatedSucceeds()
    {
        using var factory = new DemoWebApplicationFactory();
        using var strictFactory = NewStrictFactory(factory);

        var (id, marker) = SeedMarkerFile(strictFactory, tenantCode: null);

        // Negative: no cookies, no auth header.
        var unauthClient = strictFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        var unauthResp = await unauthClient.GetAsync($"/api/_file/GetFile/{id}");
        var unauthBody = await unauthResp.Content.ReadAsStringAsync();

        // Confirmed empirically (both Cookie and Bearer schemes are challenged by
        // [AuthorizeJwtWithCookie] and, for an [ApiController] with no Accept:text/html, both
        // resolve to a clean 401 — no login-page redirect ambiguity the way MVC (non-API)
        // controllers have, per Microsoft.AspNetCore.Authorization.DefaultAuthorizationService's
        // "DenyAnonymousAuthorizationRequirement: Requires an authenticated user" log line).
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthResp.StatusCode,
            $"#830: an unauthenticated caller must get 401 from GetFile (the endpoint was " +
            $"[Public] before this fix). Got {(int)unauthResp.StatusCode}.");
        Assert.IsFalse(unauthBody.Contains(marker, StringComparison.Ordinal),
            $"#830: regardless of status code, the unauthenticated response body must NOT contain " +
            $"the seeded file's content — this is the decisive check, not the status code alone. " +
            $"Body excerpt: {unauthBody[..Math.Min(200, unauthBody.Length)]}");

        // Positive control: the SAME id, authenticated, must actually succeed and return the
        // real bytes — proving the negative assertion above is not vacuous (i.e. the marker is
        // not simply unreachable through any path).
        var authClient = await AdminLoginAsync(strictFactory);
        var authResp = await authClient.GetAsync($"/api/_file/GetFile/{id}");
        var authBody = await authResp.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, authResp.StatusCode,
            $"#830: an authenticated caller must still be able to read the file it has legitimate " +
            $"access to (no per-page privilege is required — GetFile is [AllRights]). " +
            $"Got {(int)authResp.StatusCode}: {authBody[..Math.Min(200, authBody.Length)]}");
        Assert.IsTrue(authBody.Contains(marker, StringComparison.Ordinal),
            "#830: the authenticated positive control must actually contain the marker — " +
            "otherwise the unauthenticated test's absence check above would be meaningless " +
            "(the marker could simply be unreachable through any path, not specifically blocked " +
            "for unauthenticated callers).");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Category 2 — DeletedFile → DeleteFileTenantScoped: a caller authenticated as tenant A must
    // not be able to delete tenant B's FileAttachment row.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// #830: uses the same two-tenant/two-user/two-FileAttachment shape as
    /// <see cref="MultiTenantSeedFixtureTests"/> (EnableTenant=true). Tenant A's authenticated
    /// caller POSTs DeletedFile for tenant B's file id; the response is 200 either way (the
    /// controller always returns <c>Ok(true)</c> — see <c>WtmFileProvider.DeleteFileCore</c>: a
    /// tenant-scoped miss is a silent no-op, not an error), so the status code proves nothing.
    /// The decisive assertion is a real database read-back
    /// (<see cref="DbTestHelpers.ReadBack{T}"/> with <c>ignoreQueryFilters: true</c>, cutting
    /// across the same tenant filter the delete itself is scoped by) confirming tenant B's row
    /// still physically exists. The positive control in the same test — tenant A deleting its
    /// OWN file — proves DeleteFileTenantScoped's happy path genuinely deletes (i.e. the negative
    /// assertion above is not vacuously true because delete never works at all through this
    /// endpoint).
    /// </summary>
    [TestMethod]
    public async Task DeletedFile_CrossTenant_DoesNotDeleteOtherTenantsFile_SameTenantDeleteSucceeds()
    {
        using var factory = new DemoWebApplicationFactory();
        using var tenantFactory = NewTenantEnabledStrictFactory(factory);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        const string plainPassword = "Passw0rd!830";

        var tenantA = DbTestHelpers.Seed(tenantFactory, new MyTenant
        {
            ID = Guid.NewGuid(),
            TCode = $"t830A{suffix}",
            TName = "Issue #830 tenant A",
            Enabled = true,
            TenantCode = null,
        });
        var tenantB = DbTestHelpers.Seed(tenantFactory, new MyTenant
        {
            ID = Guid.NewGuid(),
            TCode = $"t830B{suffix}",
            TName = "Issue #830 tenant B",
            Enabled = true,
            TenantCode = null,
        });
        var userA = DbTestHelpers.Seed(tenantFactory, new FrameworkUser
        {
            ID = Guid.NewGuid(),
            ITCode = $"t830userA{suffix}",
            Password = PasswordHashHelper.HashPassword(plainPassword),
            Name = "Issue #830 user A",
            IsValid = true,
            TenantCode = tenantA.TCode,
        });
        var fileA = DbTestHelpers.Seed(tenantFactory, new FileAttachment
        {
            ID = Guid.NewGuid(),
            FileName = "a830.txt",
            FileExt = "txt",
            Length = 1,
            UploadTime = DateTime.UtcNow,
            SaveMode = "database",
            FileData = Encoding.UTF8.GetBytes("a"),
            TenantCode = tenantA.TCode,
        });
        var fileB = DbTestHelpers.Seed(tenantFactory, new FileAttachment
        {
            ID = Guid.NewGuid(),
            FileName = "b830.txt",
            FileExt = "txt",
            Length = 1,
            UploadTime = DateTime.UtcNow,
            SaveMode = "database",
            FileData = Encoding.UTF8.GetBytes("b"),
            TenantCode = tenantB.TCode,
        });
        // See DbTestHelpers.InvalidateTenantCache's doc comment — seeding a tenant row (via
        // IWtmDataContextFactory.CreateDC, called internally by DbTestHelpers.Seed) runs BEFORE
        // that row exists and caches an empty tenant list for an hour unless evicted.
        DbTestHelpers.InvalidateTenantCache(tenantFactory);

        // Login as tenant A's user.
        var client = tenantFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = true,
            HandleCookies = true,
        });
        var verifyCode = await CaptchaTestHelper.FetchAndSolveAsync(tenantFactory, client);
        var loginForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ITCode"] = userA.ITCode,
            ["Password"] = plainPassword,
            ["Tenant"] = tenantA.TCode,
            ["VerifyCode"] = verifyCode,
        });
        var loginResp = await client.PostAsync("/Login/Login", loginForm);
        var loginBody = await loginResp.Content.ReadAsStringAsync();
        Assert.IsFalse(loginBody.Contains("login-error", StringComparison.OrdinalIgnoreCase),
            $"#830: tenant A's user must be able to log in for this test to mean anything. " +
            $"Response excerpt: {loginBody[..Math.Min(300, loginBody.Length)]}");

        // Negative: tenant A's authenticated caller attempts to delete tenant B's file.
        var crossTenantResp = await client.PostAsync($"/api/_file/DeletedFile/{fileB.ID}", content: null);
        Assert.AreEqual(HttpStatusCode.OK, crossTenantResp.StatusCode,
            "#830: DeletedFile always returns 200 (a tenant-scoped miss is a silent no-op, not " +
            "an error) — the status code proves nothing either way; this assertion only confirms " +
            "the request itself was well-formed.");

        var survivingFileB = DbTestHelpers.ReadBack<FileAttachment>(tenantFactory, fileB.ID, ignoreQueryFilters: true);
        Assert.IsNotNull(survivingFileB,
            "#830: tenant B's FileAttachment must still physically exist after tenant A's caller " +
            "attempted to delete it by GUID — DeleteFileTenantScoped must have resolved it via a " +
            "tenant-A-scoped query (which cannot see tenant B's row) and found nothing to delete.");

        // Positive control: tenant A deletes its OWN file — must actually succeed, proving the
        // negative assertion above is not vacuously true (i.e. delete isn't simply broken).
        var ownTenantResp = await client.PostAsync($"/api/_file/DeletedFile/{fileA.ID}", content: null);
        Assert.AreEqual(HttpStatusCode.OK, ownTenantResp.StatusCode,
            $"#830: tenant A must be able to delete its OWN file. Got {(int)ownTenantResp.StatusCode}.");

        var deletedFileA = DbTestHelpers.ReadBack<FileAttachment>(tenantFactory, fileA.ID, ignoreQueryFilters: true);
        Assert.IsNull(deletedFileA,
            "#830: tenant A's own file must actually be gone after the delete — proving " +
            "DeleteFileTenantScoped's happy path genuinely deletes (not merely 'silently does " +
            "nothing for every id', which would make the cross-tenant negative assertion above " +
            "meaningless).");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Category 3 — csName validated via IsKnownConnectionKey before Wtm.CreateDC(cskey:).
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// #830: an unrecognised csName must be rejected by the SAME guard
    /// <c>_FrameworkController</c> uses (<see cref="WTMContext.IsKnownConnectionKey"/>), and the
    /// rejection must be traceable to that guard's own message — not merely any 400. #837 already
    /// found two tests in this exact file that were tautological in precisely this way: an
    /// invented connection key isn't a real configured connection either way, so deleting the
    /// guard does not make the request succeed — it fails downstream for an unrelated reason and
    /// a different code path converts THAT into a generic 400. Asserting the guard's own message
    /// text ("Unknown connection string key" — the literal string
    /// <c>_FrameworkController.IsKnownConnectionKey</c>'s callers use) is what actually pins this
    /// guard specifically, confirmed against the identical pattern in
    /// <c>MvcAuthHolesTests.GetPagingData_UnknownCsKey_ReturnsBadRequest</c>. The positive control
    /// in the same test — a null csName (the default connection) — must succeed AND actually
    /// delete the row, proving the guard does not also block the legitimate path.
    /// </summary>
    [TestMethod]
    public async Task DeletedFile_UnknownCsName_RejectedWithGuardMessage_KnownCsNameSucceeds()
    {
        using var factory = new DemoWebApplicationFactory();
        using var strictFactory = NewStrictFactory(factory);

        var (id, _) = SeedMarkerFile(strictFactory, tenantCode: null);
        var client = await AdminLoginAsync(strictFactory);

        // Negative: an invented csName.
        var badResp = await client.PostAsync($"/api/_file/DeletedFile/{id}?csName=EVIL_LATERAL_DB", content: null);
        var badBody = await badResp.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.BadRequest, badResp.StatusCode,
            $"#830: DeletedFile must reject an unrecognised csName. Got {(int)badResp.StatusCode}: " +
            $"{badBody[..Math.Min(300, badBody.Length)]}");
        Assert.IsTrue(badBody.Contains("Unknown connection string key", StringComparison.OrdinalIgnoreCase),
            $"#830: the 400 must come from the IsKnownConnectionKey guard specifically (its own " +
            $"message), not from an unrelated downstream failure that would ALSO 400 a request " +
            $"naming a nonexistent connection string even without this guard (the exact tautology " +
            $"#837 found twice already in MvcAuthHolesTests). Got body: {badBody}");

        var survivingFile = DbTestHelpers.ReadBack<FileAttachment>(strictFactory, id, ignoreQueryFilters: true);
        Assert.IsNotNull(survivingFile,
            "#830: a request rejected by the csName guard must never reach the delete itself — " +
            "the seeded file must still exist.");

        // Positive control: no csName (null -> default connection) must succeed and actually
        // delete the row.
        var goodResp = await client.PostAsync($"/api/_file/DeletedFile/{id}", content: null);
        Assert.AreEqual(HttpStatusCode.OK, goodResp.StatusCode,
            $"#830: DeletedFile with no csName (the default connection) must succeed. " +
            $"Got {(int)goodResp.StatusCode}.");

        var deletedFile = DbTestHelpers.ReadBack<FileAttachment>(strictFactory, id, ignoreQueryFilters: true);
        Assert.IsNull(deletedFile,
            "#830: the legitimate (default-connection) delete must actually remove the row — " +
            "proving the csName guard does not also block the legitimate path.");
    }
}
