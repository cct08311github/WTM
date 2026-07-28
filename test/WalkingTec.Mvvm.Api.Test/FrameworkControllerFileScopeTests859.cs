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
/// Issue #859 — the sibling of #830 on <c>_FrameworkController</c>'s OWN
/// <c>GetFile</c>/<c>ViewFile</c> routes, which #830 did not touch. <c>_FrameworkController</c>
/// carries no <c>[Authorize]</c>-family attribute; the only gate is <c>PrivilegeFilter</c>, which
/// (<c>PrivilegeFilter.cs:111-122</c>) treats <c>GetFile</c>/<c>ViewFile</c> as fully anonymous —
/// a complete early return, before the <c>LoginUserInfo == null</c> check — whenever
/// <c>Configs.IsFilePublic == true</c>. All three demo templates shipped that flag <c>true</c>;
/// combined with <see cref="WalkingTec.Mvvm.Core.ConfigOptions.FileUploadOptions.EnforceTenantFileScope"/>
/// defaulting to <c>false</c> (<c>WtmFileProvider.GetFile</c>'s <c>IgnoreQueryFilters()</c>), an
/// unauthenticated caller who knew or guessed a <see cref="FileAttachment"/> GUID could read that
/// file's content regardless of which tenant uploaded it.
///
/// <para>
/// Fixed in two halves, both exercised below:
/// <list type="number">
/// <item><b>Package</b>: <c>FileUploadOptions.EnforceTenantFileScope</c> default flipped to
/// <c>true</c> — <c>WtmFileProvider.GetFile</c> now honours the global EF Core <c>ITenant</c>
/// query filter by default, so this protects an EXISTING deployment that upgrades the package and
/// changes nothing, independent of what its own <c>IsFilePublic</c> setting is.</item>
/// <item><b>Template</b>: <c>IsFilePublic</c> set to <c>false</c> in all three demo
/// <c>appsettings.json</c> — closes the route itself for future scaffolds.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Deliberately NOT overridden via <c>ConfigureAppConfiguration</c> in any factory below:</b>
/// <c>IsFilePublic</c> (except in <see cref="NewTenantEnabledIsFilePublicFactory"/>, where setting
/// it <c>true</c> IS the point — simulating an existing deployment that has not touched its
/// config) and <c>FileUploadOptions:EnforceTenantFileScope</c> (never overridden anywhere in this
/// file). Both must be read from the actual compiled/committed defaults so that reverting either
/// one — the demo template's <c>IsFilePublic</c> back to <c>true</c>, or
/// <c>FileUploadOptions.EnforceTenantFileScope</c>'s default back to <c>false</c> — turns the
/// relevant test(s) below red. Follows the #837/#830 conventions elsewhere in this project:
/// <c>EnableTenant</c>/<c>IsQuickDebug</c> overrides go through
/// <c>ConfigureAppConfiguration</c> + <c>AddInMemoryCollection</c>, never
/// <c>services.Configure&lt;Configs&gt;</c> (see <c>MvcAuthHolesTests</c>' class doc comment for
/// why); no class-level factory (a shared eager factory was found to race demo.db's schema sync);
/// every FileAttachment is seeded with a random marker string as its content
/// (<see cref="SeedMarkerFile"/>) so a negative assertion checks for the marker's ABSENCE in the
/// response body, never a status code alone — <c>_FrameworkController</c>'s non-API MVC pipeline
/// returns HTTP 200 for both a login-redirect script AND a legitimate file response, so a bare
/// status-code check cannot tell them apart (see <c>PrivilegeFilter.cs:185-201</c>'s
/// <c>ContentResult</c> redirect script).
/// </para>
/// </summary>
[TestClass]
public class FrameworkControllerFileScopeTests859
{
    private static WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> NewTenantEnabledStrictFactory(
        DemoWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["EnableTenant"] = "true",
                    ["IsQuickDebug"] = "false",
                });
            });
        });

    /// <summary>
    /// Simulates an EXISTING deployment that upgrades the package but has not changed its own
    /// <c>IsFilePublic: true</c> config — the exact shape #859's package-half fix (the
    /// <c>EnforceTenantFileScope</c> default flip) exists to protect, independent of the
    /// template-half fix.
    /// </summary>
    private static WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> NewTenantEnabledIsFilePublicFactory(
        DemoWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["EnableTenant"] = "true",
                    ["IsQuickDebug"] = "false",
                    ["IsFilePublic"] = "true",
                });
            });
        });

    private sealed record TenantFixture(MyTenant Tenant, FrameworkUser User, string PlainPassword);

    private static TenantFixture SeedTenantAndUser(
        WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> factory, string label)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        const string plainPassword = "Passw0rd!859";

        var tenant = DbTestHelpers.Seed(factory, new MyTenant
        {
            ID = Guid.NewGuid(),
            TCode = $"t859{label}{suffix}",
            TName = $"Issue #859 tenant {label}",
            Enabled = true,
            TenantCode = null,
        });

        var user = DbTestHelpers.Seed(factory, new FrameworkUser
        {
            ID = Guid.NewGuid(),
            ITCode = $"t859user{label}{suffix}",
            Password = PasswordHashHelper.HashPassword(plainPassword),
            Name = $"Issue #859 user {label}",
            IsValid = true,
            TenantCode = tenant.TCode,
        });

        // See DbTestHelpers.InvalidateTenantCache's doc comment: seeding the tenant row above
        // runs BEFORE that row exists and would otherwise cache an empty tenant list for an hour.
        DbTestHelpers.InvalidateTenantCache(factory);

        return new TenantFixture(tenant, user, plainPassword);
    }

    /// <summary>
    /// Seeds a <see cref="FileAttachment"/> whose bytes are stored inline
    /// (<c>SaveMode="database"</c>, same convention as <c>FileApiControllerHardeningTests830</c>)
    /// so <c>WtmDataBaseFileHandler.GetFileData</c> reads them back with no filesystem dependency,
    /// tagged with a marker string that distinguishes "the response contains the actual file
    /// bytes" from "the response is merely non-empty / non-200".
    /// </summary>
    private static (Guid Id, string Marker) SeedMarkerFile(
        WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> factory, string? tenantCode, string label)
    {
        var marker = $"WTM-859-SECRET-{label}-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes(marker);
        var file = DbTestHelpers.Seed(factory, new FileAttachment
        {
            ID = Guid.NewGuid(),
            FileName = $"secret859-{label}.txt",
            FileExt = "txt",
            Length = bytes.Length,
            UploadTime = DateTime.UtcNow,
            SaveMode = "database",
            FileData = bytes,
            TenantCode = tenantCode,
        });
        return (file.ID, marker);
    }

    /// <summary>
    /// Logs in a tenant-scoped user through <paramref name="factory"/> (IsQuickDebug=false, so a
    /// VerifyCode/captcha is required — see <see cref="CaptchaTestHelper"/>'s doc comment) and
    /// returns an authenticated, cookie-carrying client. Mirrors the login flow in
    /// <c>FileApiControllerHardeningTests830.DeletedFile_CrossTenant_...</c>.
    /// </summary>
    private static async Task<HttpClient> TenantLoginAsync(
        WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> factory, FrameworkUser user, string plainPassword, string tenantCode)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = true,
            HandleCookies = true,
        });
        var verifyCode = await CaptchaTestHelper.FetchAndSolveAsync(factory, client);
        var loginForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ITCode"] = user.ITCode,
            ["Password"] = plainPassword,
            ["Tenant"] = tenantCode,
            ["VerifyCode"] = verifyCode,
        });
        var loginResp = await client.PostAsync("/Login/Login", loginForm);
        var loginBody = await loginResp.Content.ReadAsStringAsync();
        if (loginBody.Contains("login-error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"#859: tenant-scoped user login did not succeed under the strict factory. " +
                $"Response: {loginBody[..Math.Min(500, loginBody.Length)]}");
        }
        return client;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Template half (#859 item 3): IsFilePublic=false (the demo appsettings.json default as of
    // this fix) keeps _Framework/GetFile behind PrivilegeFilter's authentication gate.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// #859: an unauthenticated caller under the demo template's own (post-fix) config must be
    /// rejected by <c>_Framework/GetFile</c> — and, decisively, the response body must not
    /// contain the seeded file's content. Deleting <c>demo/WalkingTec.Mvvm.Demo/appsettings.json</c>'s
    /// <c>IsFilePublic: false</c> (i.e. reverting it to <c>true</c>) turns this test red: with
    /// <c>IsFilePublic</c> unset here, the factory reads whatever the committed appsettings.json
    /// says.
    /// </summary>
    [TestMethod]
    public async Task GetFile_Unauthenticated_TemplateDefaultConfig_RejectedAndDoesNotLeakContent()
    {
        using var factory = new DemoWebApplicationFactory();
        using var strictFactory = NewTenantEnabledStrictFactory(factory);

        var (id, marker) = SeedMarkerFile(strictFactory, tenantCode: null, "route");

        var client = strictFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        var resp = await client.GetAsync($"/_Framework/GetFile/{id}");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.IsFalse(body.Contains(marker, StringComparison.Ordinal),
            $"#859: an unauthenticated caller's response body must NOT contain the seeded file's " +
            $"content, regardless of status code — _FrameworkController's non-API MVC pipeline " +
            $"returns 200 for both a login-redirect script and a legitimate file response, so the " +
            $"status code alone proves nothing. Status {(int)resp.StatusCode}, body excerpt: " +
            $"{body[..Math.Min(200, body.Length)]}");

        // Sanity: confirm this really is PrivilegeFilter's login-redirect branch
        // (PrivilegeFilter.cs:185-201's <script>window.location...</script> ContentResult) rather
        // than some unrelated 404/error — proving the negative assertion above is not vacuous
        // (e.g. because the route itself doesn't exist).
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"#859: an unauthenticated, non-public GetFile request is expected to come back as " +
            $"the framework's 200 login-redirect script response, not any other status. Got " +
            $"{(int)resp.StatusCode}.");
        Assert.IsTrue(body.Contains("window.location", StringComparison.OrdinalIgnoreCase),
            $"#859: the 200 response body must be the login-redirect script — proving " +
            $"PrivilegeFilter's LoginUserInfo==null branch is what handled this request, not a " +
            $"fixture that happens to 200 for an unrelated reason. Body: {body}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Package half (#859 item 1): EnforceTenantFileScope=true (compiled default as of this fix)
    // blocks cross-tenant DB-level resolution EVEN when IsFilePublic=true opens the route — the
    // exact deployment shape that upgrades the package and changes nothing.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// #859: simulates an existing deployment that has NOT changed its own config
    /// (<c>IsFilePublic=true</c>, opened explicitly here) after upgrading the package. Even so,
    /// an anonymous caller must not be able to read a tenant-owned file by GUID — proving the
    /// package-half fix alone (independent of the template-half fix) closes the hole. The
    /// positive control in the same test — a genuinely public (NULL-tenant) file — must still be
    /// servable anonymously, proving the tenant-scope flip does not also break
    /// <c>IsFilePublic</c>'s legitimate use.
    /// </summary>
    [TestMethod]
    public async Task GetFile_Unauthenticated_IsFilePublicTrue_TenantOwnedFileBlocked_NullTenantFileServed()
    {
        using var factory = new DemoWebApplicationFactory();
        using var publicFactory = NewTenantEnabledIsFilePublicFactory(factory);

        var tenantA = SeedTenantAndUser(publicFactory, "A");
        var (tenantFileId, tenantMarker) = SeedMarkerFile(publicFactory, tenantA.Tenant.TCode, "tenant");
        var (publicFileId, publicMarker) = SeedMarkerFile(publicFactory, tenantCode: null, "public");

        var client = publicFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        // Negative: IsFilePublic=true opens the ROUTE to anonymous callers (PrivilegeFilter's
        // isPublic short-circuit), but FileUploadOptions.EnforceTenantFileScope's #859 default
        // (true, not overridden by this factory) must still block DB-level resolution of a file
        // that belongs to a real tenant — an unauthenticated caller with no Referer resolves to
        // dc.TenantCode == null, which cannot match tenantA.Tenant.TCode.
        var tenantResp = await client.GetAsync($"/_Framework/GetFile/{tenantFileId}");
        var tenantBody = await tenantResp.Content.ReadAsStringAsync();
        Assert.IsFalse(tenantBody.Contains(tenantMarker, StringComparison.Ordinal),
            $"#859: even with IsFilePublic=true (an existing deployment that has not changed its " +
            $"config), an anonymous caller must NOT be able to read a tenant-owned file by GUID — " +
            $"FileUploadOptions.EnforceTenantFileScope's new default (true) must still block it " +
            $"at the DB layer, independent of the route-level IsFilePublic gate. Status " +
            $"{(int)tenantResp.StatusCode}, body excerpt: " +
            $"{tenantBody[..Math.Min(200, tenantBody.Length)]}");

        // Positive control: IsFilePublic's legitimate use — a genuinely cross-tenant / NULL-tenant
        // public file — must still be servable to an anonymous caller. An anonymous caller's
        // dc.TenantCode is also null, and EF Core's null-safe equality resolves NULL == NULL.
        var publicResp = await client.GetAsync($"/_Framework/GetFile/{publicFileId}");
        var publicBody = await publicResp.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, publicResp.StatusCode,
            $"#859: IsFilePublic=true must still serve a genuinely public (NULL-tenant) file to " +
            $"an anonymous caller — proving the tenant-scope flip does not break IsFilePublic " +
            $"beyond its intended (tenant-owned-file) scope. Got {(int)publicResp.StatusCode}.");
        Assert.IsTrue(publicBody.Contains(publicMarker, StringComparison.Ordinal),
            $"#859: the anonymous positive control must actually contain the public file's " +
            $"marker — otherwise the negative assertion above would be meaningless (the marker " +
            $"could simply be unreachable through any path, not specifically blocked for " +
            $"cross-tenant access).");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Package half, authenticated variant: an authenticated caller from tenant A cannot fetch
    // tenant B's file by GUID; the same-tenant positive control still succeeds.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// #859: the acceptance criterion's core cross-tenant case — an AUTHENTICATED caller from
    /// tenant A must not be able to fetch tenant B's file by GUID, and the same-tenant positive
    /// control (tenant A fetching its own file) must still succeed in the same test.
    /// </summary>
    [TestMethod]
    public async Task GetFile_AuthenticatedTenantA_CannotFetchTenantBFile_SameTenantFileSucceeds()
    {
        using var factory = new DemoWebApplicationFactory();
        using var tenantFactory = NewTenantEnabledStrictFactory(factory);

        var tenantA = SeedTenantAndUser(tenantFactory, "A");
        var tenantB = SeedTenantAndUser(tenantFactory, "B");

        var (fileAId, markerA) = SeedMarkerFile(tenantFactory, tenantA.Tenant.TCode, "a");
        var (fileBId, markerB) = SeedMarkerFile(tenantFactory, tenantB.Tenant.TCode, "b");

        var client = await TenantLoginAsync(tenantFactory, tenantA.User, tenantA.PlainPassword, tenantA.Tenant.TCode);

        // Negative: tenant A's authenticated caller must not read tenant B's file by GUID.
        var crossResp = await client.GetAsync($"/_Framework/GetFile/{fileBId}");
        var crossBody = await crossResp.Content.ReadAsStringAsync();
        Assert.IsFalse(crossBody.Contains(markerB, StringComparison.Ordinal),
            $"#859: an authenticated caller from tenant A must NOT be able to fetch tenant B's " +
            $"file by GUID — FileUploadOptions.EnforceTenantFileScope's new default (true) must " +
            $"scope WtmFileProvider.GetFile's query to the caller's own tenant. Status " +
            $"{(int)crossResp.StatusCode}, body excerpt: {crossBody[..Math.Min(200, crossBody.Length)]}");

        // Positive control: tenant A must still be able to read its OWN file — proving the
        // negative assertion above is not vacuously true (i.e. GetFile isn't simply broken for
        // every id once EnforceTenantFileScope is on).
        var ownResp = await client.GetAsync($"/_Framework/GetFile/{fileAId}");
        var ownBody = await ownResp.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, ownResp.StatusCode,
            $"#859: tenant A must still be able to read its OWN file after the tenant-scope flip. " +
            $"Got {(int)ownResp.StatusCode}: {ownBody[..Math.Min(200, ownBody.Length)]}");
        Assert.IsTrue(ownBody.Contains(markerA, StringComparison.Ordinal),
            "#859: the same-tenant positive control must actually contain the marker — otherwise " +
            "the cross-tenant negative assertion above would be meaningless (GetFile could simply " +
            "be broken for every id, not specifically blocked for cross-tenant access).");
    }
}
