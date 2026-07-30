#nullable enable
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Implement;
using WalkingTec.Mvvm.Core.Services;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Core.Test.Security;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Integration
{
    /// <summary>
    /// Tests for <see cref="WTMContext.EnsureLoginUserInfoAsync"/> and
    /// <see cref="WTMContext.ReloadUserAsync"/>.
    ///
    /// Verifies:
    /// - Cache hit: _loginUserInfo resolved from cache without calling ReloadUser.
    /// - Cache miss + ReloadUserFunc: async path resolves via stub (no sync-over-async).
    /// - Sync getter returns pre-resolved value after EnsureLoginUserInfoAsync runs.
    /// - No-op when not authenticated.
    /// - No-op when _loginUserInfo is already set.
    /// - Sync LoginUserInfo getter itself is unmodified (still returns pre-resolved value).
    /// </summary>
    [TestClass]
    public class EnsureLoginUserInfoAsyncTests
    {
        // ─── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Build an HttpContext mock whose User has IsAuthenticated=true and
        /// carries the given <paramref name="userCode"/> and optional
        /// <paramref name="tenantCode"/> as JWT claims that match
        /// <see cref="AuthConstants.JwtClaimTypes.Subject"/> and
        /// <see cref="AuthConstants.JwtClaimTypes.TenantCode"/>.
        /// </summary>
        private static HttpContext MakeAuthenticatedHttpContext(
            string userCode,
            string? tenantCode = null)
        {
            var claims = new List<Claim>
            {
                new(AuthConstants.JwtClaimTypes.Subject, userCode)
            };
            if (tenantCode != null)
            {
                claims.Add(new(AuthConstants.JwtClaimTypes.TenantCode, tenantCode));
            }

            var identity = new ClaimsIdentity(claims, AuthConstants.AuthenticationType);
            var principal = new ClaimsPrincipal(identity);

            var mockRequest = new Mock<HttpRequest>();
            mockRequest.Setup(r => r.Cookies).Returns(new MockCookie());
            mockRequest.Setup(r => r.Query).Returns(new Mock<IQueryCollection>().Object);

            var mockCtx = new Mock<HttpContext>();
            mockCtx.Setup(c => c.Request).Returns(mockRequest.Object);
            mockCtx.Setup(c => c.User).Returns(principal);

            var mockSp = new Mock<IServiceProvider>();
            mockSp.Setup(s => s.GetService(typeof(ILoggerFactory)))
                  .Returns(new LoggerFactory());
            mockCtx.Setup(c => c.RequestServices).Returns(mockSp.Object);

            return mockCtx.Object;
        }

        /// <summary>
        /// Build an unauthenticated HttpContext mock.
        /// </summary>
        private static HttpContext MakeUnauthenticatedHttpContext()
        {
            var identity = new ClaimsIdentity(); // IsAuthenticated=false
            var principal = new ClaimsPrincipal(identity);

            // Must return an enumerable empty collection so that Linq .Any() works
            // when the sync LoginUserInfo getter checks for "_remotetoken" query param.
            var mockQuery = new Mock<IQueryCollection>();
            mockQuery.Setup(q => q.GetEnumerator())
                     .Returns(System.Linq.Enumerable.Empty<System.Collections.Generic.KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>>().GetEnumerator());

            var mockRequest = new Mock<HttpRequest>();
            mockRequest.Setup(r => r.Cookies).Returns(new MockCookie());
            mockRequest.Setup(r => r.Query).Returns(mockQuery.Object);

            var mockCtx = new Mock<HttpContext>();
            mockCtx.Setup(c => c.Request).Returns(mockRequest.Object);
            mockCtx.Setup(c => c.User).Returns(principal);
            mockCtx.Setup(c => c.RequestServices).Returns(new Mock<IServiceProvider>().Object);

            return mockCtx.Object;
        }

        /// <summary>
        /// Build a minimal <see cref="WTMContext"/> that uses the given HttpContext
        /// and cache, with no DataContext (avoids real DB calls).
        /// </summary>
        private static (WTMContext wtm, IDistributedCache cache) MakeWtmContext(
            HttpContext httpContext)
        {
            IDistributedCache cache = new MemoryDistributedCache(
                Options.Create(new MemoryDistributedCacheOptions()));
            var res = new ResourceManagerStringLocalizerFactory(
                Options.Create(new LocalizationOptions { ResourcesPath = "Resources" }),
                new Microsoft.Extensions.Logging.LoggerFactory());

            var gd = new GlobalData();
            gd.AllAccessUrls = new List<string>();
            gd.AllAssembly = new List<System.Reflection.Assembly>();
            gd.AllModule = new List<SimpleModule>();
            gd.SetTenantGetFunc(() => new List<FrameworkTenant>());

            var mockSp = new Mock<IServiceProvider>();
            mockSp.Setup(s => s.GetService(typeof(IDistributedCache))).Returns(cache);
            mockSp.Setup(s => s.GetService(typeof(ILoggerFactory))).Returns(new LoggerFactory());

            var httpa = new HttpContextAccessor { HttpContext = httpContext };

            // Pass cache explicitly; NullContext → _dc = null (no DB access)
            var wtm = new WTMContext(null, gd, httpa, new DefaultUIService(), null,
                new NullContext(), res, cache: cache);
            wtm.MSD = new BasicMSD();

            return (wtm, cache);
        }

        // ─── Cache-hit path ────────────────────────────────────────────────────

        [TestMethod]
        public async Task EnsureLoginUserInfoAsync_CacheHit_SetsLoginUserInfoWithoutCallingReloadUser()
        {
            // Arrange
            const string userCode = "alice";
            const string tenant = "t1";
            var httpCtx = MakeAuthenticatedHttpContext(userCode, tenant);
            var (wtm, cache) = MakeWtmContext(httpCtx);

            // Prime the cache with the same key the getter and EnsureLoginUserInfoAsync build.
            var cacheKey = $"{GlobalConstants.CacheKey.UserInfo}:{userCode + "$`$" + tenant}";
            var expected = new LoginUserInfo { ITCode = userCode, TenantCode = tenant };
            cache.Add(cacheKey, expected);

            // Track whether ReloadUserFunc is called.
            bool reloadCalled = false;
            WTMContext.ReloadUserFunc = (_, _) =>
            {
                reloadCalled = true;
                return new LoginUserInfo { ITCode = "should_not_reach" };
            };

            try
            {
                // Act
                await wtm.EnsureLoginUserInfoAsync();

                // Assert
                reloadCalled.Should().BeFalse("cache hit must not invoke ReloadUser");
                wtm.LoginUserInfo.Should().NotBeNull();
                wtm.LoginUserInfo!.ITCode.Should().Be(userCode);
            }
            finally
            {
                WTMContext.ReloadUserFunc = null;
            }
        }

        // ─── Cache-miss + ReloadUserFunc path ──────────────────────────────────

        [TestMethod]
        public async Task EnsureLoginUserInfoAsync_CacheMiss_UsesReloadUserFuncAndSetsLoginUserInfo()
        {
            // Arrange
            const string userCode = "bob";
            var httpCtx = MakeAuthenticatedHttpContext(userCode);
            var (wtm, _) = MakeWtmContext(httpCtx);

            var stubbedUser = new LoginUserInfo { ITCode = userCode, TenantCode = null };
            bool reloadCalled = false;

            // ReloadUserFunc is the sync hook; ReloadUserAsync honours it first.
            WTMContext.ReloadUserFunc = (_, code) =>
            {
                reloadCalled = true;
                return code == userCode ? stubbedUser : null!;
            };

            try
            {
                // Act
                await wtm.EnsureLoginUserInfoAsync();

                // Assert — resolved via async path (ReloadUserFunc), no sync-over-async.
                reloadCalled.Should().BeTrue("EnsureLoginUserInfoAsync must call ReloadUserAsync which honours ReloadUserFunc");
                wtm.LoginUserInfo.Should().NotBeNull();
                wtm.LoginUserInfo!.ITCode.Should().Be(userCode,
                    "EnsureLoginUserInfoAsync must store the user returned by ReloadUserFunc");
            }
            finally
            {
                WTMContext.ReloadUserFunc = null;
            }
        }

        // ─── Sync getter returns pre-resolved value ────────────────────────────

        [TestMethod]
        public async Task LoginUserInfoGetter_ReturnPreResolvedValue_WithoutReInvokingReloadUser()
        {
            // Arrange
            const string userCode = "carol";
            var httpCtx = MakeAuthenticatedHttpContext(userCode);
            var (wtm, _) = MakeWtmContext(httpCtx);

            var stubbedUser = new LoginUserInfo { ITCode = userCode };
            int reloadCount = 0;

            WTMContext.ReloadUserFunc = (_, _) =>
            {
                reloadCount++;
                return stubbedUser;
            };

            try
            {
                // Act: pre-resolve asynchronously
                await wtm.EnsureLoginUserInfoAsync();
                int countAfterEnsure = reloadCount;

                // Act: access the sync getter multiple times
                var first = wtm.LoginUserInfo;
                var second = wtm.LoginUserInfo;

                // Assert: getter returns pre-resolved instance; no additional reloads.
                first.Should().NotBeNull();
                first!.ITCode.Should().Be(userCode);
                second.Should().BeSameAs(first,
                    "the cached _loginUserInfo instance must be returned without re-resolution");
                reloadCount.Should().Be(countAfterEnsure,
                    "sync getter must not call ReloadUser after EnsureLoginUserInfoAsync has run");
            }
            finally
            {
                WTMContext.ReloadUserFunc = null;
            }
        }

        // ─── No-op when not authenticated ─────────────────────────────────────

        [TestMethod]
        public async Task EnsureLoginUserInfoAsync_Unauthenticated_IsNoOp()
        {
            // Arrange
            var httpCtx = MakeUnauthenticatedHttpContext();
            var (wtm, _) = MakeWtmContext(httpCtx);

            bool reloadCalled = false;
            WTMContext.ReloadUserFunc = (_, _) =>
            {
                reloadCalled = true;
                return new LoginUserInfo { ITCode = "should_not_reach" };
            };

            try
            {
                // Act
                await wtm.EnsureLoginUserInfoAsync();

                // Assert
                reloadCalled.Should().BeFalse("unauthenticated request must not trigger ReloadUser");
                // Sync getter should also return null (not authenticated).
                var info = wtm.LoginUserInfo;
                info.Should().BeNull("unauthenticated requests yield null LoginUserInfo");
            }
            finally
            {
                WTMContext.ReloadUserFunc = null;
            }
        }

        // ─── No-op when _loginUserInfo already set ────────────────────────────

        [TestMethod]
        public async Task EnsureLoginUserInfoAsync_AlreadySet_IsNoOp()
        {
            // Arrange
            const string userCode = "dave";
            var httpCtx = MakeAuthenticatedHttpContext(userCode);
            var (wtm, _) = MakeWtmContext(httpCtx);

            var alreadySet = new LoginUserInfo { ITCode = userCode };
            // Pre-set via the property setter (bypasses the getter's lazy-resolve logic).
            wtm.LoginUserInfo = alreadySet;

            bool reloadCalled = false;
            WTMContext.ReloadUserFunc = (_, _) =>
            {
                reloadCalled = true;
                return new LoginUserInfo { ITCode = "should_not_reach" };
            };

            try
            {
                // Act
                await wtm.EnsureLoginUserInfoAsync();

                // Assert
                reloadCalled.Should().BeFalse("should be a no-op when _loginUserInfo is already set");
                wtm.LoginUserInfo.Should().BeSameAs(alreadySet,
                    "the existing LoginUserInfo must not be replaced");
            }
            finally
            {
                WTMContext.ReloadUserFunc = null;
            }
        }

        // ─── ReloadUserAsync honours ReloadUserFunc ────────────────────────────

        [TestMethod]
        public async Task ReloadUserAsync_ReloadUserFuncSet_ReturnsFuncResult()
        {
            // Arrange
            const string userCode = "eve";
            var httpCtx = MakeAuthenticatedHttpContext(userCode);
            var (wtm, _) = MakeWtmContext(httpCtx);

            var expected = new LoginUserInfo { ITCode = userCode };
            WTMContext.ReloadUserFunc = (_, _) => expected;

            try
            {
                // Act
                var result = await wtm.ReloadUserAsync(userCode);

                // Assert
                result.Should().BeSameAs(expected,
                    "ReloadUserAsync must return the result from ReloadUserFunc when set");
            }
            finally
            {
                WTMContext.ReloadUserFunc = null;
            }
        }

        // ─── ReloadUserAsync with null DC returns null ─────────────────────────

        [TestMethod]
        public async Task ReloadUserAsync_NullDc_ReturnsNull()
        {
            // Arrange — context created with NullContext so DC == null
            const string userCode = "frank";
            var httpCtx = MakeAuthenticatedHttpContext(userCode);
            var (wtm, _) = MakeWtmContext(httpCtx);

            // ReloadUserFunc is null → will fall through to DC check
            WTMContext.ReloadUserFunc = null;

            // Act
            var result = await wtm.ReloadUserAsync(userCode);

            // Assert
            result.Should().BeNull("when DC is null and ReloadUserFunc is null, ReloadUserAsync returns null");
        }

        // ─── Remote-token helper (unauthenticated ctx with _remotetoken param) ──

        /// <summary>
        /// Build an unauthenticated HttpContext mock that carries a <c>_remotetoken</c>
        /// query parameter. Used to exercise Branch 2 of
        /// <see cref="WTMContext.EnsureLoginUserInfoAsync"/>.
        /// </summary>
        private static HttpContext MakeRemoteTokenHttpContext(string remoteToken)
        {
            var identity = new ClaimsIdentity(); // IsAuthenticated = false
            var principal = new ClaimsPrincipal(identity);

            // Build a query collection that contains the _remotetoken key.
            var queryValues = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                { "_remotetoken", new Microsoft.Extensions.Primitives.StringValues(remoteToken) }
            };
            var mockQuery = new Mock<IQueryCollection>();
            mockQuery.Setup(q => q.GetEnumerator())
                     .Returns(queryValues.GetEnumerator());
            mockQuery.Setup(q => q[It.IsAny<string>()])
                     .Returns((string key) => queryValues.ContainsKey(key) ? queryValues[key] : Microsoft.Extensions.Primitives.StringValues.Empty);

            var mockRequest = new Mock<HttpRequest>();
            mockRequest.Setup(r => r.Cookies).Returns(new MockCookie());
            mockRequest.Setup(r => r.Query).Returns(mockQuery.Object);

            var mockCtx = new Mock<HttpContext>();
            mockCtx.Setup(c => c.Request).Returns(mockRequest.Object);
            mockCtx.Setup(c => c.User).Returns(principal);

            var mockSp = new Mock<IServiceProvider>();
            mockSp.Setup(s => s.GetService(typeof(ILoggerFactory)))
                  .Returns(new LoggerFactory());
            mockCtx.Setup(c => c.RequestServices).Returns(mockSp.Object);

            return mockCtx.Object;
        }

        /// <summary>
        /// Build a <see cref="WTMContext"/> whose <see cref="WTMContext.ConfigInfo"/>
        /// is populated from the supplied <see cref="Configs"/> instance.
        /// All other setup is identical to <see cref="MakeWtmContext"/>.
        /// </summary>
        private static (WTMContext wtm, IDistributedCache cache) MakeWtmContextWithConfig(
            HttpContext httpContext,
            Configs configs)
        {
            IDistributedCache cache = new MemoryDistributedCache(
                Options.Create(new MemoryDistributedCacheOptions()));
            var res = new ResourceManagerStringLocalizerFactory(
                Options.Create(new LocalizationOptions { ResourcesPath = "Resources" }),
                new Microsoft.Extensions.Logging.LoggerFactory());

            var gd = new GlobalData();
            gd.AllAccessUrls = new List<string>();
            gd.AllAssembly = new List<System.Reflection.Assembly>();
            gd.AllModule = new List<SimpleModule>();
            gd.SetTenantGetFunc(() => new List<FrameworkTenant>());

            var mockSp = new Mock<IServiceProvider>();
            mockSp.Setup(s => s.GetService(typeof(IDistributedCache))).Returns(cache);
            mockSp.Setup(s => s.GetService(typeof(ILoggerFactory))).Returns(new LoggerFactory());

            // Wire up an IOptionsMonitor<Configs> that returns our custom Configs.
            var mockMonitor = new Mock<IOptionsMonitor<Configs>>();
            mockMonitor.Setup(m => m.CurrentValue).Returns(configs);

            var httpa = new HttpContextAccessor { HttpContext = httpContext };

            var wtm = new WTMContext(mockMonitor.Object, gd, httpa, new DefaultUIService(), null,
                new NullContext(), res, cache: cache);
            wtm.MSD = new BasicMSD();

            return (wtm, cache);
        }

        // ─── Branch 2: _remotetoken + HasMainHost==true ────────────────────────

        [TestMethod]
        public async Task EnsureLoginUserInfoAsync_RemoteToken_HasMainHostTrue_PopulatesLoginUserInfo()
        {
            // Arrange: unauthenticated request that carries a _remotetoken query param.
            // ConfigInfo.HasMainHost == true → the sub-host path runs, which calls
            // ReloadUserAsync("null"). We stub that via ReloadUserFunc.
            const string remoteToken = "sometoken";
            var httpCtx = MakeRemoteTokenHttpContext(remoteToken);

            // Create a Configs with a "mainhost" domain so that HasMainHost == true.
            var configs = new Configs
            {
                Domains = new Dictionary<string, WalkingTec.Mvvm.Core.ConfigOptions.Domain>
                {
                    {
                        "mainhost",
                        new WalkingTec.Mvvm.Core.ConfigOptions.Domain { Address = "https://main.example.com" }
                    }
                }
            };

            var (wtm, _) = MakeWtmContextWithConfig(httpCtx, configs);

            var stubbedUser = new LoginUserInfo { ITCode = "remoteuser", TenantCode = "rt1" };
            int reloadCount = 0;

            WTMContext.ReloadUserFunc = (_, code) =>
            {
                reloadCount++;
                // The HasMainHost==true branch always calls ReloadUser("null")
                return code == "null" ? stubbedUser : null!;
            };

            try
            {
                // Act
                await wtm.EnsureLoginUserInfoAsync();

                // Assert: _loginUserInfo is populated via the async remote-token path.
                reloadCount.Should().Be(1, "EnsureLoginUserInfoAsync must call ReloadUserAsync(\"null\") exactly once");
                wtm.LoginUserInfo.Should().NotBeNull();
                wtm.LoginUserInfo!.ITCode.Should().Be("remoteuser",
                    "the user returned by ReloadUserFunc must be stored in _loginUserInfo");

                // Verify the sync getter returns the pre-resolved instance WITHOUT
                // triggering another reload.
                int countAfterEnsure = reloadCount;
                var viaGetter = wtm.LoginUserInfo;
                reloadCount.Should().Be(countAfterEnsure,
                    "sync getter must not re-invoke ReloadUser after EnsureLoginUserInfoAsync has run");
                viaGetter.Should().BeSameAs(wtm.LoginUserInfo);
            }
            finally
            {
                WTMContext.ReloadUserFunc = null;
            }
        }

        // ─── Branch 2: _remotetoken + HasMainHost==false (JWT validation path) ──

        /// <summary>
        /// Helper: build a signed JWT using the given key/issuer/audience/subject/tenant.
        /// Pads <paramref name="securityKey"/> to at least 32 chars with 'x' if shorter — like
        /// <see cref="JwtOption.EffectiveSecurityKey"/> (#931 item 1) — but deliberately does
        /// NOT truncate a longer key, since EffectiveSecurityKey never truncates either; a
        /// 44-char shared test key (JwtTestKeys.StrongCustomKey) must sign with its FULL
        /// length here or this would sign with different bytes than WTMContext.User.cs
        /// validates with.
        /// </summary>
        private static string BuildJwt(
            string securityKey,
            string issuer,
            string audience,
            string userCode,
            string? tenantCode = null)
        {
            var effectiveKey = securityKey.Length < 32 ? securityKey.PadRight(32, 'x') : securityKey;
            var keyBytes = Encoding.UTF8.GetBytes(effectiveKey);
            var signingKey = new SymmetricSecurityKey(keyBytes);
            var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new(AuthConstants.JwtClaimTypes.Subject, userCode)
            };
            if (tenantCode != null)
                claims.Add(new(AuthConstants.JwtClaimTypes.TenantCode, tenantCode));

            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims,
                expires: DateTime.UtcNow.AddHours(1),
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>
        /// #931 item 6: decode a JWT segment's Base64url text into its real bytes, so a
        /// tampering test can flip a bit in the DECODED signature rather than risk flipping
        /// only an unused padding bit in the Base64 STRING (see the tampered-signature test's
        /// own comment for why that distinction matters).
        /// </summary>
        private static byte[] Base64UrlDecode(string input)
        {
            var s = input.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }

        private static string Base64UrlEncode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        [TestMethod]
        [Description(
            "Branch 2 / HasMainHost==false: a valid signed JWT in _remotetoken must " +
            "be validated, the subject claim extracted, and _loginUserInfo populated " +
            "via ReloadUserAsync (Issue deferred test coverage).")]
        public async Task EnsureLoginUserInfoAsync_RemoteToken_HasMainHostFalse_ValidJwt_PopulatesLoginUserInfo()
        {
            // Arrange — build a Configs with NO "mainhost" domain so HasMainHost==false.
            const string issuer = "https://test.issuer";
            const string audience = "https://test.audience";
            // #923: must be >= 32 UTF-8 bytes on its own (raw, pre-padding) — WTMContext.User.cs's
            // _remotetoken JWT validation path now fails closed on JwtOption.IsWeakSigningKey()
            // BEFORE ever attempting signature validation, so a short "will be padded to 32" key
            // like the pre-#923 24-byte version here would make this test pass for the wrong
            // reason (rejected as a weak key, not because the JWT is genuinely valid/invalid).
            // #931 item 2: also must not be a fixed literal — the original 32-byte replacement
            // was itself publicly readable via test/'s mirror sync, so this now generates fresh.
            var rawKey = JwtTestKeys.StrongCustomKey;
            const string userCode = "jwt_user";

            var configs = new Configs
            {
                Domains = new Dictionary<string, WalkingTec.Mvvm.Core.ConfigOptions.Domain>
                {
                    // "mainhost" key is intentionally absent → HasMainHost == false
                    {
                        "subhost",
                        new WalkingTec.Mvvm.Core.ConfigOptions.Domain { Address = "https://sub.example.com" }
                    }
                }
            };
            configs.JwtOptions.SecurityKey = rawKey;
            configs.JwtOptions.Issuer = issuer;
            configs.JwtOptions.Audience = audience;

            // Build a valid JWT signed with the same key the WTMContext will validate.
            var validToken = BuildJwt(configs.JwtOptions.SecurityKey, issuer, audience, userCode);

            var httpCtx = MakeRemoteTokenHttpContext(validToken);
            var (wtm, _) = MakeWtmContextWithConfig(httpCtx, configs);

            var stubbedUser = new LoginUserInfo { ITCode = userCode };
            int reloadCount = 0;
            WTMContext.ReloadUserFunc = (_, code) =>
            {
                reloadCount++;
                return code == userCode ? stubbedUser : null!;
            };

            try
            {
                // Act
                await wtm.EnsureLoginUserInfoAsync();

                // Assert
                reloadCount.Should().Be(1,
                    "HasMainHost==false valid JWT path must call ReloadUserAsync exactly once");
                wtm.LoginUserInfo.Should().NotBeNull(
                    "a valid JWT must result in _loginUserInfo being populated");
                wtm.LoginUserInfo!.ITCode.Should().Be(userCode,
                    "ITCode must match the sub claim in the validated JWT");
            }
            finally
            {
                WTMContext.ReloadUserFunc = null;
            }
        }

        [TestMethod]
        [Description(
            "Branch 2 / HasMainHost==false: a JWT with a tampered signature must be " +
            "rejected (SecurityTokenException) and _loginUserInfo must remain null " +
            "(Issue deferred test coverage).")]
        public async Task EnsureLoginUserInfoAsync_RemoteToken_HasMainHostFalse_TamperedJwt_LeavesLoginUserInfoNull()
        {
            // Arrange — same config as the valid-JWT test
            const string issuer = "https://test.issuer";
            const string audience = "https://test.audience";
            // #923: must be >= 32 UTF-8 bytes — see the identical comment on the valid-JWT test
            // above. Without this, WTMContext.User.cs's weak-key fail-closed guard would reject
            // the token before signature validation ever ran, and this test would pass for the
            // wrong reason (masking whether tamper detection itself still works).
            // #931 item 2: also must not be a fixed literal — see the identical comment on the
            // valid-JWT test above.
            var rawKey = JwtTestKeys.StrongCustomKey;
            const string userCode = "tamper_victim";

            var configs = new Configs
            {
                Domains = new Dictionary<string, WalkingTec.Mvvm.Core.ConfigOptions.Domain>
                {
                    {
                        "subhost",
                        new WalkingTec.Mvvm.Core.ConfigOptions.Domain { Address = "https://sub.example.com" }
                    }
                }
            };
            configs.JwtOptions.SecurityKey = rawKey;
            configs.JwtOptions.Issuer = issuer;
            configs.JwtOptions.Audience = audience;

            // Build a valid token, then tamper with the signature by decoding it, flipping one
            // bit in its FIRST byte, and re-encoding.
            //
            // #931 item 6: the original approach flipped the last Base64url CHARACTER of the
            // signature string ('A' <-> 'B'). Base64 encodes 3 bytes into 4 characters, so the
            // last character of a run can cover only the low, sometimes-unused bits of the
            // final byte depending on alignment — when that character started as 'A' (all
            // zero bits in its covered range), flipping it to 'B' can land entirely within
            // padding bits that decode back to the SAME byte value, leaving the decoded
            // signature bytes UNCHANGED even though the STRING changed. A test built that way
            // risks proving "the decoder rejects a non-canonical Base64 string" rather than
            // "the signature check rejects a different signature". Decoding first and flipping
            // a bit in the FIRST byte (always fully "real", never a padding artifact) avoids
            // that, and the assertion below proves the decoded bytes actually differ before
            // the tampered token is ever used.
            var validToken = BuildJwt(configs.JwtOptions.SecurityKey, issuer, audience, userCode);
            var parts = validToken.Split('.');
            var signatureBytes = Base64UrlDecode(parts[2]);
            var tamperedBytes = (byte[])signatureBytes.Clone();
            tamperedBytes[0] ^= 0x01;
            CollectionAssert.AreNotEqual(signatureBytes, tamperedBytes,
                "test precondition: tampering must actually change the decoded signature bytes");
            var tamperedSignature = Base64UrlEncode(tamperedBytes);
            var tamperedToken = string.Join('.', parts[0], parts[1], tamperedSignature);

            var httpCtx = MakeRemoteTokenHttpContext(tamperedToken);
            var (wtm, _) = MakeWtmContextWithConfig(httpCtx, configs);

            bool reloadCalled = false;
            WTMContext.ReloadUserFunc = (_, _) =>
            {
                reloadCalled = true;
                return new LoginUserInfo { ITCode = "should_not_reach" };
            };

            try
            {
                // Act — must NOT throw; SecurityTokenException is caught internally.
                await wtm.EnsureLoginUserInfoAsync();

                // Assert
                reloadCalled.Should().BeFalse(
                    "a tampered JWT signature must be rejected before ReloadUserAsync is called");
                wtm.LoginUserInfo.Should().BeNull(
                    "_loginUserInfo must remain null when JWT signature validation fails");
            }
            finally
            {
                WTMContext.ReloadUserFunc = null;
            }
        }

        // ─── Branch 2: already resolved → no-op ───────────────────────────────

        [TestMethod]
        public async Task EnsureLoginUserInfoAsync_RemoteToken_AlreadyResolved_IsNoOp()
        {
            // Arrange: first call populates _loginUserInfo via ReloadUserFunc.
            const string remoteToken = "sometoken";
            var httpCtx = MakeRemoteTokenHttpContext(remoteToken);

            var configs = new Configs
            {
                Domains = new Dictionary<string, WalkingTec.Mvvm.Core.ConfigOptions.Domain>
                {
                    {
                        "mainhost",
                        new WalkingTec.Mvvm.Core.ConfigOptions.Domain { Address = "https://main.example.com" }
                    }
                }
            };

            var (wtm, _) = MakeWtmContextWithConfig(httpCtx, configs);

            var stubbedUser = new LoginUserInfo { ITCode = "remoteuser2" };
            int reloadCount = 0;

            WTMContext.ReloadUserFunc = (_, _) =>
            {
                reloadCount++;
                return stubbedUser;
            };

            try
            {
                // First call — should resolve via ReloadUserFunc.
                await wtm.EnsureLoginUserInfoAsync();
                reloadCount.Should().Be(1, "first call must invoke ReloadUserFunc once");

                // Second call — must be a no-op.
                await wtm.EnsureLoginUserInfoAsync();
                reloadCount.Should().Be(1,
                    "second call must be a no-op when _loginUserInfo is already set");

                wtm.LoginUserInfo.Should().BeSameAs(stubbedUser,
                    "the resolved instance must remain unchanged after the no-op second call");
            }
            finally
            {
                WTMContext.ReloadUserFunc = null;
            }
        }
    }
}
