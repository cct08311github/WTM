using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Services;

namespace WalkingTec.Mvvm.Api.Test;

/// <summary>
/// Issue #827 — <see cref="IWtmFrameworkEndpointAuthorizer"/>, a DI-resolvable authorization
/// seam for <c>_FrameworkController</c>'s five resource hooks (<c>CanExportVm</c>,
/// <c>CanAccessFile</c>, <c>CanPreviewDelete</c>, <c>CanImportVm</c>, <c>CanEditProperty</c>).
///
/// <para>
/// <b>Why every test here goes through real HTTP.</b> #827's own defect was that the previous
/// "override the hook in a derived controller" story was never actually reachable:
/// <c>_FrameworkController</c> is the CONCRETE class MVC routes every <c>/_Framework/*</c>
/// request to, and the existing hook tests (<c>FrameworkControllerRbacHooksTest</c>,
/// <c>FrameworkControllerFileAccessTest</c>, <c>FrameworkControllerImportAuthTest</c> in
/// <c>WalkingTec.Mvvm.Admin.Test</c>) all construct a test SUBCLASS directly and call its action
/// methods in-process — proving the hook mechanism works in isolation, but nothing about whether
/// a real request ever reaches it (a subclass is never routed to). Every test below goes through
/// <see cref="DemoWebApplicationFactory"/> and an actual <see cref="HttpClient"/> request to a
/// production <c>/_Framework/*</c> route, following the strict-factory pattern documented on
/// <c>MvcAuthHolesTests</c>.
/// </para>
///
/// <para>
/// Per-method factories (no class-level <see cref="DemoWebApplicationFactory"/>), mirroring
/// <c>FrameworkControllerFileScopeTests859</c> and <c>DbTestHelpers</c>'s own guidance: a shared
/// eager factory was found to occasionally race the demo.db schema sync during
/// <c>ClassInitialize</c> in earlier work on this project.
/// </para>
/// </summary>
[TestClass]
public class FrameworkAuthorizationSeamTests
{
    private const string StudentListVm =
        "WalkingTec.Mvvm.Demo.ViewModels.StudentVMs.StudentListVM";
    private const string StudentImportVm =
        "WalkingTec.Mvvm.Demo.ViewModels.StudentVMs.StudentImportVM";
    private const string FrameworkUserVm =
        "WalkingTec.Mvvm.Mvc.Admin.ViewModels.FrameworkUserVms.FrameworkUserVM";

    [TestInitialize]
    public void Init() => TestFrameworkEndpointAuthorizer.Reset();

    /// <summary>
    /// Logs in as demo's seeded admin/000000 account. No VerifyCode is supplied — this only
    /// works against a factory left at the demo template's own IsQuickDebug=true default (same
    /// convention as <c>MvcAuthHolesTests._vmExportEnforcedFactory</c>'s login helper); none of
    /// the tests in this file need PrivilegeFilter's RBAC enforcement itself (that is
    /// <c>MvcAuthHolesTests</c>' concern) — they need an authenticated caller so the five hooks
    /// below (all reachable only once a request clears authentication) actually run.
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

    private static FrameworkUser SeedFrameworkUser(WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> factory) =>
        DbTestHelpers.Seed(factory, new FrameworkUser
        {
            ID = Guid.NewGuid(),
            ITCode = $"t827_{Guid.NewGuid():N}",
            Password = "not-a-real-hash",
            Name = "Issue #827 fixture user",
            IsValid = true,
            TenantCode = null,
        });

    /// <summary>
    /// Seeds a <see cref="FileAttachment"/> with inline bytes (<c>SaveMode="database"</c>, same
    /// convention as <c>FrameworkControllerFileScopeTests859</c>) tagged with a marker string, so
    /// a "200 and the response actually contains the file" assertion is possible — a bare status
    /// code cannot distinguish a served file from <c>_FrameworkController</c>'s 200 login-redirect
    /// script response.
    /// </summary>
    private static (Guid Id, string Marker) SeedMarkerFile(
        WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> factory, string? tenantCode, string label)
    {
        var marker = $"WTM-827-SECRET-{label}-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes(marker);
        var file = DbTestHelpers.Seed(factory, new FileAttachment
        {
            ID = Guid.NewGuid(),
            FileName = $"secret827-{label}.txt",
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
    /// Case-insensitive JSON property lookup on a root object — ASP.NET Core's default
    /// <c>System.Text.Json</c> output formatter applies a camelCase naming policy, but this
    /// repo does not pin one explicitly anywhere in the demo's startup, so asserting against a
    /// specific case would make the test depend on an unrelated, unpinned framework default
    /// rather than on what <c>DoImport</c>'s response envelope actually contains.
    /// </summary>
    private static bool TryGetPropertyIgnoreCase(JsonElement root, string name, out JsonElement value)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Core #827 acceptance criterion: a policy registered through DI must be consulted by a
    // REAL request to the CONCRETE, production-routed _FrameworkController — in both directions
    // (Deny overriding a permissive default, Allow overriding a fail-closed flag).
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetExportExcel_CustomDIPolicy_DenyIsConsultedOnRealRoute_OverridesPermissiveFlagDefault()
    {
        TestFrameworkEndpointAuthorizer.ExportDecision = WtmAuthorizationDecision.Deny;
        using var factory = new DemoWebApplicationFactory();
        using var policyFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
                services.AddScoped<IWtmFrameworkEndpointAuthorizer, TestFrameworkEndpointAuthorizer>());
        });

        var client = await LoginAsync(policyFactory);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = StudentListVm,
        });
        var resp = await client.PostAsync("/_Framework/GetExportExcel", form);

        Assert.AreEqual(HttpStatusCode.Redirect, resp.StatusCode,
            "#827: a DI-registered IWtmFrameworkEndpointAuthorizer returning Deny must be " +
            "consulted by the real, routed _FrameworkController and must override the " +
            "permissive default (EnforceVmExportAuthorization is deliberately left at its " +
            "default false here) — proving the seam is reachable on a production route, which " +
            "a subclass override never was.");
        Assert.IsTrue(TestFrameworkEndpointAuthorizer.CanExportVmCalls > 0,
            "#827: the DI-registered authorizer's CanExportVm must actually have been invoked.");
    }

    [TestMethod]
    public async Task GetExportExcel_CustomDIPolicy_AllowIsConsultedOnRealRoute_OverridesEnforceFlagDeny()
    {
        TestFrameworkEndpointAuthorizer.ExportDecision = WtmAuthorizationDecision.Allow;
        using var factory = new DemoWebApplicationFactory();
        using var policyFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["EnforceVmExportAuthorization"] = "true",
                }));
            builder.ConfigureTestServices(services =>
                services.AddScoped<IWtmFrameworkEndpointAuthorizer, TestFrameworkEndpointAuthorizer>());
        });

        var client = await LoginAsync(policyFactory);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = StudentListVm,
        });
        var resp = await client.PostAsync("/_Framework/GetExportExcel", form);

        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            "#827: a DI-registered policy returning Allow must override the fail-closed " +
            "EnforceVmExportAuthorization=true flag — proving DI precedence holds in both " +
            "directions on a real route, not only for Deny.");
        Assert.IsTrue(TestFrameworkEndpointAuthorizer.CanExportVmCalls > 0,
            "#827: the DI-registered authorizer's CanExportVm must actually have been invoked.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Default-behaviour pinning: NO policy registered, NO flag set, one test per hook. Pins
    // "an unregistered seam changes nothing for a default deployment" into CI so it cannot
    // later be claimed without evidence — see CLAUDE.md's production-readiness red line.
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetExportExcel_NoPolicyNoFlag_DefaultAllowsExport()
    {
        using var factory = new DemoWebApplicationFactory();
        var client = await LoginAsync(factory);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = StudentListVm,
        });
        var resp = await client.PostAsync("/_Framework/GetExportExcel", form);
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            "#827: with no IWtmFrameworkEndpointAuthorizer registered and no Enforce flag set, " +
            $"CanExportVm's default must remain permissive — unchanged by this issue's fix. " +
            $"Got {(int)resp.StatusCode}.");
        // #881 review: 200 alone does not prove an actual export happened — GetExportExcel's
        // File() result is what CanExportVm's allow path actually produces; a 200 with an empty
        // or HTML body would not. Assert the real response shape: the Excel content type
        // GetExportExcel's File() call sets (_FrameworkController.cs), and a non-trivial byte
        // count (a genuine .xls workbook, even for zero rows, carries real header structure).
        Assert.AreEqual("application/vnd.ms-excel", resp.Content.Headers.ContentType?.MediaType,
            "#827: the allowed export must actually return the Excel content type GetExportExcel's " +
            $"File() result sets, not merely any 200. Got: {resp.Content.Headers.ContentType}.");
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.IsTrue(bytes.Length > 512,
            $"#827: the allowed export must return a genuine, non-trivial .xls workbook body " +
            $"(got {bytes.Length} bytes) — proving GenerateExcel() actually ran, not just that " +
            $"some 200 response with the right content type came back.");
    }

    [TestMethod]
    public async Task GetFile_NoPolicyNoFlag_DefaultAllowsAccess_ServesSeededContent()
    {
        using var factory = new DemoWebApplicationFactory();
        var (id, marker) = SeedMarkerFile(factory, tenantCode: null, "827default");
        var client = await LoginAsync(factory);
        var resp = await client.GetAsync($"/_Framework/GetFile/{id}");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"#827: with no IWtmFrameworkEndpointAuthorizer registered and no Enforce flag set, " +
            $"CanAccessFile's default must remain permissive. Got {(int)resp.StatusCode}: " +
            $"{body[..Math.Min(200, body.Length)]}");
        Assert.IsTrue(body.Contains(marker, StringComparison.Ordinal),
            "#827: the response must actually contain the seeded file's content, not merely 200 " +
            "— _FrameworkController's non-API pipeline can return 200 for a login-redirect too.");
    }

    [TestMethod]
    public async Task GetDeletePreview_NoPolicyNoFlag_DefaultAllowsPreview()
    {
        using var factory = new DemoWebApplicationFactory();
        var user = SeedFrameworkUser(factory);
        var client = await LoginAsync(factory);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = FrameworkUserVm,
            ["ids"] = user.ID.ToString(),
        });
        var resp = await client.PostAsync("/_Framework/GetDeletePreview", form);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"#827: with no IWtmFrameworkEndpointAuthorizer registered and no Enforce flag set, " +
            $"CanPreviewDelete's default must remain permissive. Got {(int)resp.StatusCode}: {body}");
        // #881 review: 200 alone does not prove the seeded row was actually resolved and
        // labeled -- GetDeletePreview returns JsonMore(results) with a 200 even for an empty
        // results array (e.g. if the per-row CreateVM/lookup silently found nothing), which
        // would look identical to a genuine preview on status code alone. Assert the seeded
        // user's own id — echoed verbatim into each result's "id" field — actually appears in
        // the response body.
        Assert.IsTrue(body.Contains(user.ID.ToString(), StringComparison.OrdinalIgnoreCase),
            $"#827: the response must actually contain the seeded row's id, not merely 200 with " +
            $"an empty/unrelated result set. Body: {body}");
    }

    [TestMethod]
    public async Task DoImport_NoPolicyNoFlag_DefaultAllowsImport_NotDenied()
    {
        using var factory = new DemoWebApplicationFactory();
        var client = await LoginAsync(factory);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = StudentImportVm,
        });
        var resp = await client.PostAsync("/_Framework/DoImport", form);
        var body = await resp.Content.ReadAsStringAsync();
        // No UploadFileId is posted, so BatchSaveData reports its own "please upload a
        // template" validation error (400) — an ALLOWED-then-invalid outcome, not a denial. A
        // CanImportVm denial surfaces as Forbid() -> a redirect under the default cookie auth
        // scheme (see MvcAuthHolesTests' #796/#814 wire-status tests).
        //
        // #881 review: "not a redirect" alone would also pass for a pre-hook 404/500 that has
        // nothing to do with CanImportVm being permissive. Assert the SPECIFIC allowed-then-
        // invalid shape instead: DoImport's own doc comment documents the failure envelope as
        // exactly `{ "ImportedCount": 0, "InlineErrors": [{...}], "ValidateOnly": false }`
        // (_FrameworkController.cs) — reaching that envelope at all requires BatchSaveData to
        // have run, which only happens after CanImportVm allows.
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode,
            $"#827: with no policy and no flag, CanImportVm must allow the request through to " +
            $"BatchSaveData's own validation (missing UploadFileId), which reports its failure " +
            $"as 400 -- not the 302 a CanImportVm denial would produce. Got {(int)resp.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.IsTrue(TryGetPropertyIgnoreCase(doc.RootElement, "ImportedCount", out var importedCount),
            $"#827: the response must be DoImport's own envelope (an ImportedCount field), " +
            $"proving CanImportVm allowed the request through to BatchSaveData. Body: {body}");
        // ImportedCount serializes as a JSON string, not a number, under this app's configured
        // JSON number handling (MvcOptionExtension.cs) — read it via GetRawText/ToString so the
        // assertion does not depend on which representation is in effect.
        var importedCountText = importedCount.ValueKind == JsonValueKind.String
            ? importedCount.GetString()
            : importedCount.GetRawText();
        Assert.AreEqual("0", importedCountText,
            $"#827: no rows should have imported (no template was uploaded). Body: {body}");
        Assert.IsTrue(TryGetPropertyIgnoreCase(doc.RootElement, "InlineErrors", out var inlineErrors),
            $"#827: the envelope must also carry InlineErrors. Body: {body}");
        Assert.IsTrue(inlineErrors.GetArrayLength() >= 1,
            $"#827: InlineErrors must report the missing-template validation failure — an empty " +
            $"array here would mean BatchSaveData never actually ran its own validation. Body: {body}");
    }

    [TestMethod]
    public async Task UpdateModelProperty_NoPolicyNoFlag_DefaultAllowsEdit()
    {
        using var factory = new DemoWebApplicationFactory();
        var user = SeedFrameworkUser(factory);
        var client = await LoginAsync(factory);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_DONOT_USE_VMNAME"] = FrameworkUserVm,
            ["id"] = user.ID.ToString(),
            ["field"] = "Name",
            ["value"] = "Issue #827 default-pinning edit",
        });
        var resp = await client.PostAsync("/_Framework/UpdateModelProperty", form);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"#827: CanEditProperty has no Enforce* flag at all — with no policy registered its " +
            $"default must remain the unconditional allow it always was. Got " +
            $"{(int)resp.StatusCode}: {body}");
        // #881 review: 200 alone does not prove the write actually happened -- read the row back
        // through a FRESH DataContext (not the change-tracked instance the request went
        // through) to confirm the edit was actually persisted, the same pattern
        // MvcAuthHolesTests.UpdateModelProperty_AllowedField_ActuallyPersistsToDatabase uses.
        var persisted = DbTestHelpers.ReadBack<FrameworkUser>(factory, user.ID);
        Assert.IsNotNull(persisted, "#827: the seeded row must still exist after the edit.");
        Assert.AreEqual("Issue #827 default-pinning edit", persisted!.Name,
            "#827: UpdateModelProperty returned 200 but the database read-back shows the old " +
            "value -- the wire-level status code alone does not prove the write actually happened.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // LoginUserInfo == null: a registered policy must still be consulted, without crashing,
    // for an anonymous caller — and a public file must still be servable end to end.
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetFile_Anonymous_IsFilePublicTrue_CustomDIPolicyConsulted_PublicFileStillServed()
    {
        TestFrameworkEndpointAuthorizer.FileDecision = WtmAuthorizationDecision.Allow;
        using var factory = new DemoWebApplicationFactory();
        using var policyFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["IsFilePublic"] = "true",
                }));
            builder.ConfigureTestServices(services =>
                services.AddScoped<IWtmFrameworkEndpointAuthorizer, TestFrameworkEndpointAuthorizer>());
        });

        var (id, marker) = SeedMarkerFile(policyFactory, tenantCode: null, "827anon");

        var client = policyFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        var resp = await client.GetAsync($"/_Framework/GetFile/{id}");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"#827: an anonymous caller (LoginUserInfo == null) against a public file must still " +
            $"be served — a registered IWtmFrameworkEndpointAuthorizer must not crash or " +
            $"misbehave when Wtm.LoginUserInfo is null. Got {(int)resp.StatusCode}: " +
            $"{body[..Math.Min(200, body.Length)]}");
        Assert.IsTrue(body.Contains(marker, StringComparison.Ordinal),
            "#827: the response must contain the seeded file's actual content, not merely 200.");
        Assert.IsTrue(TestFrameworkEndpointAuthorizer.CanAccessFileCalls > 0,
            "#827: the DI-registered authorizer's CanAccessFile must actually have been invoked " +
            "for this anonymous, IsFilePublic=true request — proving the seam is consulted even " +
            "when the caller has no LoginUserInfo, not only for authenticated requests.");
    }
}

/// <summary>
/// Test double for <see cref="IWtmFrameworkEndpointAuthorizer"/>, DI-registered per test via
/// <c>ConfigureTestServices</c>. Every hook's decision and call count is driven by STATIC state
/// that the test method sets up before building the factory and resets in
/// <see cref="FrameworkAuthorizationSeamTests.Init"/> — a new SCOPED instance of this class is
/// constructed by the DI container on every request, but the desired decision has to be steered
/// from outside that container, which <see cref="WebApplicationFactory{TEntryPoint}"/> (not the
/// test method) owns.
/// </summary>
internal sealed class TestFrameworkEndpointAuthorizer : IWtmFrameworkEndpointAuthorizer
{
    public static WtmAuthorizationDecision ExportDecision = WtmAuthorizationDecision.Inherit;
    public static WtmAuthorizationDecision FileDecision = WtmAuthorizationDecision.Inherit;
    public static WtmAuthorizationDecision PreviewDecision = WtmAuthorizationDecision.Inherit;
    public static WtmAuthorizationDecision ImportDecision = WtmAuthorizationDecision.Inherit;
    public static WtmAuthorizationDecision EditPropertyDecision = WtmAuthorizationDecision.Inherit;

    public static int CanExportVmCalls;
    public static int CanAccessFileCalls;
    public static int CanPreviewDeleteCalls;
    public static int CanImportVmCalls;
    public static int CanEditPropertyCalls;

    public static void Reset()
    {
        ExportDecision = WtmAuthorizationDecision.Inherit;
        FileDecision = WtmAuthorizationDecision.Inherit;
        PreviewDecision = WtmAuthorizationDecision.Inherit;
        ImportDecision = WtmAuthorizationDecision.Inherit;
        EditPropertyDecision = WtmAuthorizationDecision.Inherit;
        CanExportVmCalls = 0;
        CanAccessFileCalls = 0;
        CanPreviewDeleteCalls = 0;
        CanImportVmCalls = 0;
        CanEditPropertyCalls = 0;
    }

    public WtmAuthorizationDecision CanExportVm(WTMContext wtm, Type vmType)
    {
        Interlocked.Increment(ref CanExportVmCalls);
        return ExportDecision;
    }

    public WtmAuthorizationDecision CanAccessFile(WTMContext wtm, string fileId)
    {
        Interlocked.Increment(ref CanAccessFileCalls);
        return FileDecision;
    }

    public WtmAuthorizationDecision CanPreviewDelete(WTMContext wtm, Type vmType)
    {
        Interlocked.Increment(ref CanPreviewDeleteCalls);
        return PreviewDecision;
    }

    public WtmAuthorizationDecision CanImportVm(WTMContext wtm, Type vmType)
    {
        Interlocked.Increment(ref CanImportVmCalls);
        return ImportDecision;
    }

    public WtmAuthorizationDecision CanEditProperty(WTMContext wtm, object entity, string propertyName)
    {
        Interlocked.Increment(ref CanEditPropertyCalls);
        return EditPropertyDecision;
    }
}
