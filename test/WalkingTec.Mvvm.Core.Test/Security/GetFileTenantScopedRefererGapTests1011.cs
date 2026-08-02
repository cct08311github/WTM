#nullable enable
using System;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Implement;
using WalkingTec.Mvvm.Core.Support.FileHandlers;

namespace WalkingTec.Mvvm.Core.Test.Security
{
    /// <summary>
    /// A SQLite-backed <see cref="FrameworkContext"/> subclass with the <c>(CS)</c> constructor
    /// <see cref="CS.CreateDC"/> requires for <see cref="CS.DcConstructor"/> wiring — see
    /// <c>FileAttachmentSqliteContext</c> (same folder) for why a minimal
    /// <see cref="FrameworkContext"/> subclass is used here instead of
    /// <c>WalkingTec.Mvvm.Core.Test.DataContext</c>.
    /// </summary>
    internal sealed class RefererGapFileContext : FrameworkContext
    {
        public RefererGapFileContext(string cs, DBTypeEnum dbtype) : base(cs, dbtype) { }

        public RefererGapFileContext(CS cs) : base(cs.Value ?? "default", cs.DbType ?? DBTypeEnum.SQLite, cs.Version) { }
    }

    /// <summary>
    /// Issue #1011 — documents, honestly, what the new
    /// <see cref="WtmFileProvider.GetFileTenantScoped"/> does NOT fix: the anonymous/forged
    /// <c>Referer</c> tenant-resolution route (#116).
    ///
    /// <para>
    /// With <see cref="Configs.DisableRefererTenantResolution"/> at its default <c>false</c>, an
    /// UNAUTHENTICATED caller who knows a tenant's registered domain can send a forged
    /// <c>Referer</c> header and have <see cref="WTMContext.CreateDC"/> (see
    /// <c>WTMContext.CreateDC.cs:42-56</c>) resolve that tenant's scope — the SAME mechanism
    /// <c>RefererTenantIsolationTests.CreateDC_UnauthenticatedRequest_RefererMatchesTenant_RoutesTenant</c>
    /// pins as intentional legacy behaviour (per-domain login pages). <c>GetFileTenantScoped</c>
    /// then faithfully scopes to whatever tenant <c>CreateDC</c> resolved — forged or not — because
    /// it only confines a read to "the tenant this <c>IDataContext</c> already resolved for", not
    /// "the tenant this caller is actually allowed to see". This test proves that literally: it
    /// resolves the FORGED tenant's file for an anonymous caller, not an aspiration that it
    /// wouldn't. <see cref="FrameworkControllerFileScopeTests859"/>'s own class doc comment notes
    /// its cross-tenant coverage is authenticated-caller-only or unauthenticated-with-no-Referer —
    /// it deliberately does not cover the forged-Referer-plus-anonymous shape; this test fills
    /// that specific gap for the new scoped API, not for #859's package-flip fix (which is a
    /// different mechanism — <c>FileUploadOptions.EnforceTenantFileScope</c> — and is equally
    /// unable to see through a forged tenant resolution, for the same underlying reason: both
    /// operate strictly downstream of whatever tenant <c>CreateDC</c> already decided).
    /// </para>
    ///
    /// <para>
    /// Closing this route needs <c>DisableRefererTenantResolution=true</c> or an authorizer that
    /// rejects anonymous callers before <c>WtmFileProvider</c> is ever reached — not a change to
    /// <c>GetFileTenantScoped</c> itself, which is doing exactly what its doc comment says it
    /// does.
    /// </para>
    /// </summary>
    [TestClass]
    public class GetFileTenantScopedRefererGapTests1011
    {
        private const string ForgedTenantCode = "TENANT_FORGED";
        private const string ForgedTenantDomain = "forged-tenant.example.com";

        [TestMethod]
        [Description("#1011 (honest known-gap): anonymous caller + forged Referer -> GetFileTenantScoped resolves the FORGED tenant's file")]
        public void GetFileTenantScoped_Anonymous_ForgedReferer_ResolvesForgedTenantFile()
        {
            var dbId = Guid.NewGuid().ToString("N");
            var sqliteCs = $"DataSource=refgap1011_{dbId};Mode=Memory;Cache=Shared";
            using var keepAlive = new SqliteConnection(sqliteCs);
            keepAlive.Open();

            // ── Seed the forged tenant's file directly (bypasses CreateDC's tenant routing —
            //    this is just data setup, not part of the mechanism under test). ──
            Guid fileId;
            var marker = $"WTM-1011-REFERER-GAP-{Guid.NewGuid():N}";
            using (var seedCtx = new RefererGapFileContext(sqliteCs, DBTypeEnum.SQLite))
            {
                seedCtx.Database.EnsureCreated();
                var fa = new FileAttachment
                {
                    ID = Guid.NewGuid(),
                    FileName = "forged-tenant-file.txt",
                    FileExt = "txt",
                    TenantCode = ForgedTenantCode,
                    SaveMode = "database",
                    UploadTime = DateTime.UtcNow,
                    FileData = Encoding.UTF8.GetBytes(marker),
                    Length = marker.Length,
                };
                fileId = fa.ID;
                seedCtx.Add(fa);
                seedCtx.SaveChanges();
            }

            // ── Config: one "default" connection wired to RefererGapFileContext, Referer
            //    resolution left at its default (false = NOT disabled). ──
            var cs = new CS { Key = "default", Value = sqliteCs, DbType = DBTypeEnum.SQLite, Enabled = true };
            cs.DcConstructor = typeof(RefererGapFileContext).GetConstructor([typeof(CS)])!;

            var configs = new Configs
            {
                DisableRefererTenantResolution = false, // default — the gap this test documents
                EnableTenant = true,
            };
            configs.FileUploadOptions.EnforceTenantFileScope = false; // opted-out deployment
            configs.Connections.Add(cs);

            var configMonitor = new Mock<IOptionsMonitor<Configs>>();
            configMonitor.Setup(x => x.CurrentValue).Returns(configs);

            // ── One registered tenant whose domain matches the forged Referer. ──
            var forgedTenant = new FrameworkTenant
            {
                TCode = ForgedTenantCode,
                TName = "Forged Tenant",
                TDomain = ForgedTenantDomain,
            };
            var gd = new GlobalData();
            gd.SetTenantGetFunc(() => [forgedTenant]);

            // ── Anonymous HttpContext with a forged Referer header. ──
            var httpCtx = new DefaultHttpContext();
            httpCtx.Request.Headers["Referer"] = $"https://{ForgedTenantDomain}/";
            var accessor = new HttpContextAccessor { HttpContext = httpCtx };

            var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
            var localizer = new ResourceManagerStringLocalizerFactory(
                Options.Create(new LocalizationOptions { ResourcesPath = "Resources" }),
                new Microsoft.Extensions.Logging.LoggerFactory());

            var wtm = new WTMContext(
                configMonitor.Object, gd, accessor, new DefaultUIService(),
                null, null, localizer, null, null, cache);
            // Deliberately never touch wtm.LoginUserInfo — this caller is anonymous.

            // ── Act: CreateDC() resolves the FORGED tenant via the Referer, exactly like
            //    RefererTenantIsolationTests.CreateDC_UnauthenticatedRequest_RefererMatchesTenant_RoutesTenant. ──
            using var forgedDc = wtm.CreateDC();

            Assert.IsNotNull(forgedDc,
                "sanity: CreateDC must return a DC for this setup.");
            Assert.AreEqual(ForgedTenantCode, forgedDc!.TenantCode,
                "sanity: the forged Referer must actually have routed CreateDC to the forged " +
                "tenant — otherwise the assertion below would be vacuous (not exercising the " +
                "gap this test documents at all).");

            var fp = new WtmFileProvider(wtm);
            var result = fp.GetFileTenantScoped(fileId.ToString(), withData: true, dc: forgedDc);

            // ── Assert the ACTUAL behaviour, not an aspiration: the scoped API resolves the
            //    forged tenant's file for an anonymous caller. This is the known gap, pinned so
            //    a future reader does not assume it is closed. ──
            Assert.IsNotNull(result,
                "#1011 known gap: GetFileTenantScoped resolves within whichever tenant CreateDC " +
                "already resolved — including a tenant selected via a forged Referer header for " +
                "an anonymous caller. This is NOT a claim that the scoped API is safe against " +
                "this route; it documents that it is not, so the CHANGELOG's #1011 entry cannot " +
                "be read as closing it. Closing this needs DisableRefererTenantResolution=true " +
                "or an authorizer that rejects anonymous callers before this method is reached.");
            Assert.IsNotNull(result!.DataStream);
            using var reader = new System.IO.StreamReader(result.DataStream!);
            Assert.AreEqual(marker, reader.ReadToEnd(),
                "the resolved content must be the FORGED tenant's marker, proving this is a real " +
                "read of that tenant's data and not a coincidental non-null result.");
        }
    }
}
