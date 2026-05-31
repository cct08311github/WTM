#nullable enable
using System;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Implement;

namespace WalkingTec.Mvvm.Core.Test.Security
{
    /// <summary>
    /// Verifies the security fix for cross-tenant data-read via forged Referer header (#116).
    ///
    /// Threat: An authenticated main-host admin whose CurrentTenant is null could send a
    /// crafted "Referer: http://tenant-a.example.com/foo" header and — before this fix —
    /// have CreateDC() silently route to tenant-a's database.
    ///
    /// The fix restricts Referer-based tenant resolution to UNAUTHENTICATED requests only
    /// (_loginUserInfo == null).  For any authenticated user, the tenant derives from
    /// identity claims only (null → main-host database).
    ///
    /// An opt-in flag Configs.DisableRefererTenantResolution (default false) lets
    /// security-strict deployments disable Referer routing entirely.
    /// </summary>
    [TestClass]
    public class RefererTenantIsolationTests
    {
        private const string TenantACode   = "TENANT_A";
        private const string TenantADomain = "tenant-a.example.com";

        // ─── helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a minimal WTMContext backed by a per-test in-memory SQLite connection.
        /// <paramref name="refererValue"/> is the raw Referer header value (null = no header).
        /// <paramref name="loginUser"/> is set via the public LoginUserInfo setter so the
        ///   backing _loginUserInfo field reflects an authenticated (non-null) or
        ///   unauthenticated (null) context.
        /// <paramref name="disableReferer"/> controls the new opt-in flag.
        /// </summary>
        private static WTMContext CreateContext(
            string? refererValue,
            LoginUserInfo? loginUser,
            bool disableReferer = false)
        {
            // ── Config: one SQLite connection keyed "default" ──────────────────
            var dbId = Guid.NewGuid().ToString("N");
            var sqliteCs = $"Data Source=file:referer_test_{dbId}?mode=memory&cache=shared";
            var cs = new CS
            {
                Key    = "default",
                Value  = sqliteCs,
                DbType = DBTypeEnum.SQLite,
            };
            // CS.Cis scans assemblies for non-EmptyContext subclasses with a (CS) ctor.
            // In the test process there may be no such type, making CreateDC() return null.
            // Wire the EmptyContext(CS) constructor directly so CreateDC() works without
            // a real application DbContext assembly being present.
            cs.DcConstructor = typeof(EmptyContext)
                .GetConstructor([typeof(CS)])!;
            cs.Enabled = true;

            var configs = new Configs
            {
                DisableRefererTenantResolution = disableReferer,
            };
            configs.Connections.Add(cs);

            var configMonitor = new Mock<IOptionsMonitor<Configs>>();
            configMonitor.Setup(x => x.CurrentValue).Returns(configs);

            // ── GlobalData: register a single tenant whose domain is tenant-a ──
            // IsUsingDB == false (no TDb/TDbType) → CreateDC falls through to the
            // shared "default" connection and calls rv.SetTenantCode(tc).
            var tenantA = new FrameworkTenant
            {
                TCode   = TenantACode,
                TName   = "Tenant A",
                TDomain = TenantADomain,
            };
            var gd = new GlobalData();
            gd.SetTenantGetFunc(() => [tenantA]);

            // ── HttpContext: real DefaultHttpContext so Headers dictionary works ─
            var httpCtx = new DefaultHttpContext();
            if (refererValue != null)
            {
                httpCtx.Request.Headers["Referer"] = refererValue;
            }
            var accessor = new HttpContextAccessor { HttpContext = httpCtx };

            // ── Distributed cache (required by WTMContext) ────────────────────
            var cache = new MemoryDistributedCache(
                Options.Create(new MemoryDistributedCacheOptions()));

            // ── Localizer factory ─────────────────────────────────────────────
            var localizer = new ResourceManagerStringLocalizerFactory(
                Options.Create(new LocalizationOptions { ResourcesPath = "Resources" }),
                new Microsoft.Extensions.Logging.LoggerFactory());

            var wtm = new WTMContext(
                configMonitor.Object,
                gd,
                accessor,
                new DefaultUIService(),
                null,       // data privileges
                null,       // pre-created DC
                localizer,
                null,       // logger factory
                null,       // localisation options
                cache);

            // Initialise the backing DC so properties that rely on DC != null work.
            var seedDc = new EmptyContext(sqliteCs, DBTypeEnum.SQLite);
            seedDc.Database.EnsureCreated();
            wtm.DC = seedDc;

            // Set authenticated user state WITHOUT triggering the lazy-load getter.
            // The setter assigns _loginUserInfo directly when the value is non-null.
            wtm.LoginUserInfo = loginUser;

            return wtm;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Test cases
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// SECURITY: Authenticated user + forged Referer → must NOT route to tenant DB.
        /// The tenant code on the returned DC must be null (main-host routing).
        /// </summary>
        [TestMethod]
        public void CreateDC_AuthenticatedUser_ForgedReferer_DoesNotRouteTenantDB()
        {
            // Arrange: authenticated main-host admin (CurrentTenant = null)
            var authenticatedAdmin = new LoginUserInfo
            {
                ITCode     = "admin",
                TenantCode = null,
                // CurrentTenant defaults to null → pre-fix code would have used Referer
            };
            // Use trailing-slash form to match what the production regex extracts as domain.
            // This ensures the forged Referer *would* match if the auth check were absent.
            var referer = $"http://{TenantADomain}/";

            using var wtm = CreateContext(referer, authenticatedAdmin);

            // Act
            using var dc = wtm.CreateDC();

            // Assert: TenantCode must be null — the forged Referer was ignored
            dc.Should().NotBeNull(
                "CreateDC must succeed even when Referer-based routing is suppressed");
            dc!.TenantCode.Should().BeNull(
                "authenticated users must never have their tenant resolved from the " +
                "attacker-controlled Referer header; tenant must come from identity claims only");
        }

        /// <summary>
        /// LEGACY: Unauthenticated request with a Referer matching a registered tenant
        /// domain SHOULD still route to that tenant (pre-login / per-domain login pages).
        /// Default flag (DisableRefererTenantResolution = false).
        ///
        /// Note: the production Referer regex "(http://|https://)?(.+?)(/)?$" captures the
        /// domain only when the Referer is scheme+domain (no path), e.g. "https://domain"
        /// or "https://domain/".  This is a known limitation of the existing regex.
        /// The test uses a trailing-slash URL to match how the regex is designed to work.
        /// </summary>
        [TestMethod]
        public void CreateDC_UnauthenticatedRequest_RefererMatchesTenant_RoutesTenant()
        {
            // Arrange: no authenticated user.
            // Use scheme+domain+trailing-slash to match what the production regex extracts
            // as the domain group (the regex stops at the first '/').
            var referer = $"https://{TenantADomain}/";

            using var wtm = CreateContext(referer, loginUser: null, disableReferer: false);

            // Act
            using var dc = wtm.CreateDC();

            // Assert: tenant code is set — legacy unauthenticated routing preserved
            dc.Should().NotBeNull();
            dc!.TenantCode.Should().Be(TenantACode,
                "unauthenticated requests may legitimately use Referer for per-domain " +
                "tenant routing (e.g. per-domain login pages); this must still work");
        }

        /// <summary>
        /// OPT-IN FLAG: DisableRefererTenantResolution = true disables Referer routing
        /// even for unauthenticated requests.
        /// </summary>
        [TestMethod]
        public void CreateDC_DisableRefererFlag_UnauthenticatedRequest_RefererIgnored()
        {
            // Arrange: no authenticated user, but flag is set to disable Referer routing.
            // Use trailing-slash form so the domain would match if routing were active.
            var referer = $"https://{TenantADomain}/";

            using var wtm = CreateContext(referer, loginUser: null, disableReferer: true);

            // Act
            using var dc = wtm.CreateDC();

            // Assert: Referer is ignored — tenant code stays null
            dc.Should().NotBeNull();
            dc!.TenantCode.Should().BeNull(
                "when DisableRefererTenantResolution is true, the Referer header must " +
                "be ignored even for unauthenticated requests");
        }

        /// <summary>
        /// Boundary: authenticated tenant user with CurrentTenant set via the authorised
        /// login path should still get their tenant — the fix only removes Referer as a
        /// fallback, it does not affect claims-based tenant routing.
        /// </summary>
        [TestMethod]
        public void CreateDC_AuthenticatedUserWithCurrentTenant_RoutesTenant()
        {
            // Arrange: authenticated tenant user (CurrentTenant set by claims/login)
            var tenantUser = new LoginUserInfo
            {
                ITCode        = "user1",
                TenantCode    = TenantACode,
                CurrentTenant = TenantACode,
            };
            // No Referer — tenant comes from identity, not from header
            using var wtm = CreateContext(refererValue: null, loginUser: tenantUser);

            // Act
            using var dc = wtm.CreateDC();

            // Assert: tenant comes from claims, not Referer
            dc.Should().NotBeNull();
            dc!.TenantCode.Should().Be(TenantACode,
                "a tenant user whose CurrentTenant is set via authorised login must " +
                "continue to route to their tenant database");
        }

        /// <summary>
        /// TIMING-ROBUSTNESS: When the request principal IsAuthenticated but the
        /// LoginUserInfo lazy-load getter has NOT yet been called (_loginUserInfo == null),
        /// a forged Referer header must still be ignored.
        ///
        /// This covers the window between the start of a request and the first access of
        /// the LoginUserInfo property.  Before the robustness fix, CreateDC() called early
        /// in a middleware pipeline could see _loginUserInfo == null even for an
        /// authenticated principal and incorrectly honour the Referer header.
        /// </summary>
        [TestMethod]
        public void CreateDC_AuthenticatedPrincipal_LoginUserInfoNeverAccessed_ForgedRefererIgnored()
        {
            // Arrange: build a WTMContext where the HttpContext principal is authenticated
            // but we deliberately do NOT set LoginUserInfo (leaving _loginUserInfo == null).
            var dbId = Guid.NewGuid().ToString("N");
            var sqliteCs = $"Data Source=file:referer_timing_test_{dbId}?mode=memory&cache=shared";
            var cs = new CS
            {
                Key    = "default",
                Value  = sqliteCs,
                DbType = DBTypeEnum.SQLite,
            };
            cs.DcConstructor = typeof(EmptyContext)
                .GetConstructor([typeof(CS)])!;
            cs.Enabled = true;

            var configs = new Configs { DisableRefererTenantResolution = false };
            configs.Connections.Add(cs);

            var configMonitor = new Mock<IOptionsMonitor<Configs>>();
            configMonitor.Setup(x => x.CurrentValue).Returns(configs);

            var tenantA = new FrameworkTenant
            {
                TCode   = TenantACode,
                TName   = "Tenant A",
                TDomain = TenantADomain,
            };
            var gd = new GlobalData();
            gd.SetTenantGetFunc(() => [tenantA]);

            // Build a real DefaultHttpContext with IsAuthenticated == true.
            // GenericIdentity with a non-empty name is authenticated by contract.
            var httpCtx = new DefaultHttpContext();
            httpCtx.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    claims: null,
                    authenticationType: "TestAuth"  // non-null/non-empty → IsAuthenticated == true
                )
            );
            // Forge a Referer that matches tenant-a's domain.
            httpCtx.Request.Headers["Referer"] = $"http://{TenantADomain}/";
            var accessor = new HttpContextAccessor { HttpContext = httpCtx };

            var cache = new MemoryDistributedCache(
                Options.Create(new MemoryDistributedCacheOptions()));
            var localizer = new ResourceManagerStringLocalizerFactory(
                Options.Create(new LocalizationOptions { ResourcesPath = "Resources" }),
                new Microsoft.Extensions.Logging.LoggerFactory());

            using var wtm = new WTMContext(
                configMonitor.Object,
                gd,
                accessor,
                new DefaultUIService(),
                null,   // data privileges
                null,   // pre-created DC
                localizer,
                null,   // logger factory
                null,   // localisation options
                cache);

            // Seed a DC so WTMContext internal properties that require DC work.
            var seedDc = new EmptyContext(sqliteCs, DBTypeEnum.SQLite);
            seedDc.Database.EnsureCreated();
            wtm.DC = seedDc;

            // INTENTIONALLY do NOT access wtm.LoginUserInfo — leaving _loginUserInfo == null.
            // This simulates CreateDC() being called early in the pipeline before the getter
            // is first accessed.

            // Act
            using var dc = wtm.CreateDC();

            // Assert: despite _loginUserInfo being null, the authenticated principal must
            // prevent the forged Referer from routing to tenant-a.
            dc.Should().NotBeNull(
                "CreateDC must return a DC even when Referer-based routing is suppressed");
            dc!.TenantCode.Should().BeNull(
                "an authenticated principal (IsAuthenticated == true) must block Referer-based " +
                "tenant routing even if _loginUserInfo has not yet been lazily resolved; " +
                "the timing-robustness fix adds HttpContext.User.Identity.IsAuthenticated check");
        }
    }
}
