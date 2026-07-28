using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Demo.Models._Admin;

namespace WalkingTec.Mvvm.Api.Test;

/// <summary>
/// Issue #837 patch 3 — the highest-risk item in the issue. demo/appsettings.json ships
/// <c>EnableTenant: false</c>, so before this fixture there was no way for any test in this
/// harness to construct two tenants, each with its own user and its own
/// <see cref="FileAttachment"/> row — the phrase "tenant A uses tenant B's GUID", which is the
/// entire threat model behind #815/#824/#827, had NO representable state to assert against at
/// all. This file builds that state and proves it is real, not merely constructed.
///
/// <para>
/// <b>EnableTenant must be set via <c>ConfigureAppConfiguration</c> + <c>AddInMemoryCollection</c>,
/// never <c>services.Configure&lt;Configs&gt;</c></b> — see the class-level doc comment on
/// <c>MvcAuthHolesTests</c> for the full mechanism. The short version: the tenant-list
/// bootstrap that <see cref="WTMContext.DoLoginAsync"/> depends on
/// (<c>FrameworkServiceExtension.cs:925</c>, <c>if (configs?.EnableTenant == true)</c>) reads
/// the SAME eager <c>Configs</c> snapshot taken at <c>AddWtmContext</c> (<c>:402</c>) that
/// <c>services.Configure&lt;Configs&gt;</c> can never reach — using the wrong mechanism here
/// wouldn't fail loudly, it would just silently leave the tenant list empty and every login in
/// this file would fail for a reason that looks unrelated to the actual mistake.
/// </para>
///
/// <para>
/// Two separate things are proven below, deliberately kept apart:
/// </para>
/// <list type="number">
/// <item>
/// <see cref="TwoTenantsTwoUsersTwoFiles_AreIsolatedByTheGlobalTenantQueryFilter"/> — the state
/// is representable, and <c>DataContext.OnModelCreating</c>'s global <c>ITenant</c> EF query
/// filter (<c>TenantCode == this.TenantCode</c>, applied UNCONDITIONALLY — independent of
/// <c>Configs.EnableTenant</c>, see <c>DataContext.cs</c>) actually isolates two tenants'
/// <see cref="FileAttachment"/> rows from each other at the DB layer: a context scoped to
/// tenant A cannot resolve tenant B's file by its GUID, even though both rows physically exist
/// in the same demo.db.
/// </item>
/// <item>
/// <see cref="TenantScopedUser_CanCompleteARealHttpLogin"/> — the concrete feasibility risk:
/// whether a freshly-seeded tenant is actually picked up by <c>GlobaInfo.AllTenant</c>'s cache
/// in time for a real HTTP <c>/Login/Login</c> POST to authenticate as that tenant's user.
/// This is the part that could plausibly not have worked (cache population order, tenant-type
/// discovery via <c>MyTenant : FrameworkTenant</c>, TPH query merging) — it is proven here by
/// actually doing it over HTTP, not by reading the source and assuming.
/// </item>
/// </list>
///
/// <para>
/// <b>What this file deliberately does NOT do:</b> exercise
/// <c>_FrameworkController.GetFile</c>/<c>GetFileName</c>'s cross-tenant read. Both call sites
/// intentionally bypass the tenant filter (<c>IgnoreQueryFilters()</c>) unless
/// <c>FileUploadOptions.EnforceTenantFileScope</c> is opted in — a known, already-documented gap
/// (see the "WTM-SEC-003" comments in <c>WtmFileProvider.cs</c>), not something #837 introduces
/// or is meant to fix. Asserting THAT endpoint's behaviour under this fixture is exactly the
/// job #815/#824/#827 exist to do — this issue's job is only to make sure they have a fixture
/// to do it with.
/// </para>
/// </summary>
[TestClass]
public class MultiTenantSeedFixtureTests
{
    // #837 patch 2 convention: no class-level factory. Every test below builds (and disposes)
    // its own DemoWebApplicationFactory + EnableTenant=true variant locally — see the note on
    // GetFileName_EnforceFlagEnabled_Forbid_Is302RedirectNotLiteral403 in MvcAuthHolesTests.cs
    // for why a class-level factory that most tests never touch was found to race demo.db's
    // shared schema sync.
    private static WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> NewTenantEnabledFactory(
        DemoWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["EnableTenant"] = "true",
                });
            });
        });

    private sealed record TenantFixture(MyTenant Tenant, FrameworkUser User, FileAttachment File, string PlainPassword);

    /// <summary>
    /// Seeds one tenant + one user (belonging to that tenant) + one FileAttachment (belonging
    /// to that tenant) via <see cref="DbTestHelpers"/>. Called twice (once per label) by the
    /// isolation test below to produce the "two tenants, two users, two FileAttachment rows"
    /// state #837 patch 3 asks for.
    ///
    /// Seeds <see cref="MyTenant"/> (not the base <see cref="FrameworkTenant"/> directly) —
    /// demo's DataContext only declares <c>DbSet&lt;MyTenant&gt;</c>, and
    /// <c>FrameworkServiceExtension.cs</c>'s tenant-list bootstrap specifically looks for a
    /// custom type assignable from <see cref="FrameworkTenant"/> via
    /// <c>GetPocoTypesAssignableFrom&lt;FrameworkTenant&gt;()</c> (the same pattern as
    /// <c>CustomUserType</c> for <see cref="FrameworkUser"/>) before falling back to the base
    /// type — seeding through the type the app actually uses avoids depending on that fallback.
    /// </summary>
    private static TenantFixture SeedTenant(WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> factory, string label)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        const string plainPassword = "Passw0rd!837";

        var tenant = DbTestHelpers.Seed(factory, new MyTenant
        {
            ID = Guid.NewGuid(),
            TCode = $"t837{label}{suffix}",
            TName = $"Issue #837 tenant {label}",
            Enabled = true,
            // Host-owned record — matches the admin/FrameworkUser seed pattern elsewhere in
            // this repo (DataContext.DataInit's `TenantCode = TenantCode`, null for the
            // default connection). The tenant list bootstrap reads this table with
            // IgnoreQueryFilters() regardless, so this has no bearing on whether the tenant
            // itself is discovered.
            TenantCode = null,
        });

        var user = DbTestHelpers.Seed(factory, new FrameworkUser
        {
            ID = Guid.NewGuid(),
            ITCode = $"t837user{label}{suffix}",
            Password = PasswordHashHelper.HashPassword(plainPassword),
            Name = $"Issue #837 user {label}",
            IsValid = true,
            TenantCode = tenant.TCode,
        });

        var file = DbTestHelpers.Seed(factory, new FileAttachment
        {
            ID = Guid.NewGuid(),
            FileName = $"t837-{label}.txt",
            FileExt = "txt",
            Length = 0,
            UploadTime = DateTime.UtcNow,
            SaveMode = "local",
            TenantCode = tenant.TCode,
        });

        // See DbTestHelpers.InvalidateTenantCache's doc comment: seeding the tenant row above
        // (via IWtmDataContextFactory.CreateDC, which unconditionally reads
        // GlobalData.AllTenant to resolve routing) runs BEFORE that row exists, which caches
        // an empty tenant list for an hour unless evicted here.
        DbTestHelpers.InvalidateTenantCache(factory);

        return new TenantFixture(tenant, user, file, plainPassword);
    }

    /// <summary>
    /// Proves the multi-tenant fixture's core claim: with two tenants, two users, and two
    /// FileAttachment rows seeded (EnableTenant=true), a DataContext correctly scoped to
    /// tenant A can resolve its own file by GUID but NOT tenant B's — even though both rows
    /// physically exist in the same demo.db (confirmed via IgnoreQueryFilters reads). This is
    /// the "tenant A uses tenant B's GUID" state #815/#824/#827 need representable; the
    /// isolation itself is the framework's existing global ITenant query filter
    /// (DataContext.OnModelCreating), not anything new introduced here.
    /// </summary>
    [TestMethod]
    public void TwoTenantsTwoUsersTwoFiles_AreIsolatedByTheGlobalTenantQueryFilter()
    {
        using var factory = new DemoWebApplicationFactory();
        using var tenantFactory = NewTenantEnabledFactory(factory);

        var a = SeedTenant(tenantFactory, "A");
        var b = SeedTenant(tenantFactory, "B");

        // Sanity: both rows physically exist, independent of any tenant scoping.
        Assert.IsNotNull(
            DbTestHelpers.ReadBack<FileAttachment>(tenantFactory, a.File.ID, ignoreQueryFilters: true),
            "#837: tenant A's FileAttachment must physically exist in the database.");
        Assert.IsNotNull(
            DbTestHelpers.ReadBack<FileAttachment>(tenantFactory, b.File.ID, ignoreQueryFilters: true),
            "#837: tenant B's FileAttachment must physically exist in the database.");

        var (scopeA, dcA) = DbTestHelpers.OpenScopedContext(tenantFactory, a.Tenant.TCode);
        using (scopeA)
        {
            var ownFile = dcA.Set<FileAttachment>().FirstOrDefault(x => x.ID == a.File.ID);
            var crossTenantFile = dcA.Set<FileAttachment>().FirstOrDefault(x => x.ID == b.File.ID);

            Assert.IsNotNull(ownFile,
                "#837: a context scoped to tenant A must see tenant A's own FileAttachment.");
            Assert.IsNull(crossTenantFile,
                "#837: a context scoped to tenant A must NOT resolve tenant B's FileAttachment " +
                "by GUID — this is the exact cross-tenant-GUID state #815/#824/#827 need to be " +
                "able to assert against, and it is now representable.");
        }

        var (scopeB, dcB) = DbTestHelpers.OpenScopedContext(tenantFactory, b.Tenant.TCode);
        using (scopeB)
        {
            var ownFile = dcB.Set<FileAttachment>().FirstOrDefault(x => x.ID == b.File.ID);
            var crossTenantFile = dcB.Set<FileAttachment>().FirstOrDefault(x => x.ID == a.File.ID);

            Assert.IsNotNull(ownFile,
                "#837: a context scoped to tenant B must see tenant B's own FileAttachment.");
            Assert.IsNull(crossTenantFile,
                "#837: a context scoped to tenant B must NOT resolve tenant A's FileAttachment by GUID.");
        }
    }

    /// <summary>
    /// The concrete feasibility risk this issue called out: a freshly-seeded tenant + user must
    /// actually be usable for a real HTTP login, not just present in the database. Proves
    /// GlobaInfo.AllTenant's cache picks up the seeded MyTenant row and WTMContext.DoLoginAsync
    /// matches ITCode + TenantCode + password against it, over the real /Login/Login endpoint —
    /// exactly the request path #815/#824/#827's future tests will need to authenticate as a
    /// specific tenant before attempting a cross-tenant read.
    /// </summary>
    [TestMethod]
    public async Task TenantScopedUser_CanCompleteARealHttpLogin()
    {
        using var factory = new DemoWebApplicationFactory();
        using var tenantFactory = NewTenantEnabledFactory(factory);

        var a = SeedTenant(tenantFactory, "Login");

        var client = tenantFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ITCode"] = a.User.ITCode,
            ["Password"] = a.PlainPassword,
            ["Tenant"] = a.Tenant.TCode,
        });

        var resp = await client.PostAsync("/Login/Login", form);
        var body = await resp.Content.ReadAsStringAsync();

        // LoginController.Login(POST) returns Redirect(...) (302) only on a successful
        // DoLoginAsync; a failed login (wrong password, unmatched tenant, tenant not found in
        // GlobaInfo.AllTenant) re-renders the SAME Login view with a ModelError — 200 OK, not a
        // redirect. This is an unambiguous, HTTP-status-level signal for "the fixture's
        // tenant-scoped user actually works end-to-end", not just "rows exist in demo.db".
        Assert.AreEqual(HttpStatusCode.Redirect, resp.StatusCode,
            $"#837: a tenant-scoped user seeded by this fixture must be able to complete a " +
            $"real HTTP login. A non-redirect response means the multi-tenant fixture's state " +
            $"is not actually usable end-to-end. Got {(int)resp.StatusCode}: " +
            $"{body[..Math.Min(300, body.Length)]}");
    }
}
