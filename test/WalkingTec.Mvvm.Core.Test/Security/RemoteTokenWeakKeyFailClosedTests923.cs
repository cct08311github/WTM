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
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Core.Implement;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Security
{
    /// <summary>
    /// Issue #923: <see cref="WTMContext"/> has two independent <c>_remotetoken</c>
    /// signature-validation paths — the sync <see cref="WTMContext.LoginUserInfo"/> getter
    /// (<c>WTMContext.User.cs</c>'s Remote-token branch) and the async
    /// <see cref="WTMContext.EnsureLoginUserInfoAsync"/> (its own, separately-written mirror
    /// of the same branch). Neither can assume a host even calls
    /// <c>FrameworkServiceExtension.AddWtmAuthentication</c> (that guard lives in
    /// <c>WalkingTec.Mvvm.Mvc</c>, a different assembly Core does not depend on), so each
    /// independently fails closed when <see cref="JwtOption.IsWeakSigningKey"/> is true —
    /// BEFORE attempting <c>JwtSecurityTokenHandler.ValidateToken</c> at all.
    ///
    /// <para>
    /// The critical property proven here is NOT "an invalid token is rejected" (that was
    /// already covered pre-#923 by the tampered-signature tests in
    /// <c>EnsureLoginUserInfoAsyncTests</c>). It is that a token whose signature WOULD
    /// verify — because it was signed with the very same (weak) key
    /// <c>JwtOption.SecurityKey</c>'s padding produces — is still rejected, because the key
    /// itself is untrustworthy: anyone who knows it (it is either a publicly shipped demo
    /// value, or short enough to brute-force offline) could have produced that same
    /// "valid" signature. Each weak-key test below builds a JWT that DOES verify against the
    /// padded key, to prove the guard fires before signature validation is even attempted —
    /// not that validation happens to fail for an unrelated reason.
    /// </para>
    /// </summary>
    [TestClass]
    public class RemoteTokenWeakKeyFailClosedTests923
    {
        // ─── Helpers (mirrors EnsureLoginUserInfoAsyncTests' private helpers) ──────────

        private static HttpContext MakeRemoteTokenHttpContext(string remoteToken)
        {
            var identity = new ClaimsIdentity(); // IsAuthenticated = false
            var principal = new ClaimsPrincipal(identity);

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

        private static (WTMContext wtm, IDistributedCache cache) MakeWtmContextWithConfig(
            HttpContext httpContext,
            Configs configs)
        {
            IDistributedCache cache = new MemoryDistributedCache(
                Options.Create(new MemoryDistributedCacheOptions()));
            var res = new ResourceManagerStringLocalizerFactory(
                Options.Create(new LocalizationOptions { ResourcesPath = "Resources" }),
                new LoggerFactory());

            var gd = new GlobalData();
            gd.AllAccessUrls = new List<string>();
            gd.AllAssembly = new List<System.Reflection.Assembly>();
            gd.AllModule = new List<SimpleModule>();
            gd.SetTenantGetFunc(() => new List<FrameworkTenant>());

            var mockMonitor = new Mock<IOptionsMonitor<Configs>>();
            mockMonitor.Setup(m => m.CurrentValue).Returns(configs);

            var httpa = new HttpContextAccessor { HttpContext = httpContext };

            var wtm = new WTMContext(mockMonitor.Object, gd, httpa, new DefaultUIService(), null,
                new NullContext(), res, cache: cache);
            wtm.MSD = new BasicMSD();

            return (wtm, cache);
        }

        /// <summary>
        /// Signs a JWT with <paramref name="securityKey"/> PADDED to at least 32 chars with
        /// 'x' if shorter — exactly what <see cref="JwtOption.EffectiveSecurityKey"/> does
        /// (#931 item 1 moved that padding off <see cref="JwtOption.SecurityKey"/> itself).
        /// Deliberately does NOT truncate a longer key to exactly 32 chars — EffectiveSecurityKey
        /// never truncates either, only pads when short, so a 44-char shared test key (see
        /// JwtTestKeys.StrongCustomKey) must sign with its FULL length here or this helper
        /// would sign with different bytes than WTMContext.User.cs validates with.
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

        private static Configs MakeSubHostConfigs(string rawSecurityKey, string issuer, string audience)
        {
            var configs = new Configs
            {
                // "mainhost" key intentionally absent -> HasMainHost == false, which is the
                // branch that reaches JWT signature validation at all.
                Domains = new Dictionary<string, WalkingTec.Mvvm.Core.ConfigOptions.Domain>
                {
                    { "subhost", new WalkingTec.Mvvm.Core.ConfigOptions.Domain { Address = "https://sub.example.com" } }
                }
            };
            configs.JwtOptions.SecurityKey = rawSecurityKey;
            configs.JwtOptions.Issuer = issuer;
            configs.JwtOptions.Audience = audience;
            return configs;
        }

        // ─── Sync LoginUserInfo getter (WTMContext.User.cs's first _remotetoken branch) ──

        [TestMethod]
        public void LoginUserInfoGetter_RemoteToken_ShortRawKey_ValidPaddedSignature_StillReturnsNull()
        {
            const string issuer = "https://test.issuer";
            const string audience = "https://test.audience";
            const string rawKey = "shortkey123"; // 11 UTF-8 bytes, well under the 32-byte floor
            const string userCode = "sync_weak_short";

            var configs = MakeSubHostConfigs(rawKey, issuer, audience);
            // Sign with the PADDED key — the same value WTMContext.User.cs would use if it
            // ever reached ValidateToken — so a signature check ALONE would succeed here.
            var token = BuildJwt(configs.JwtOptions.SecurityKey, issuer, audience, userCode);

            var httpCtx = MakeRemoteTokenHttpContext(token);
            var (wtm, _) = MakeWtmContextWithConfig(httpCtx, configs);

            bool reloadCalled = false;
            WTMContext.ReloadUserFunc = (_, _) =>
            {
                reloadCalled = true;
                return new LoginUserInfo { ITCode = "should_not_reach" };
            };

            try
            {
                var info = wtm.LoginUserInfo;

                reloadCalled.Should().BeFalse(
                    "#923: a raw SecurityKey under 32 bytes must be rejected before ReloadUser is ever called, even though the token's signature verifies against the padded key");
                info.Should().BeNull();
            }
            finally
            {
                WTMContext.ReloadUserFunc = null;
            }
        }

        [TestMethod]
        public void LoginUserInfoGetter_RemoteToken_DemoKeySuper_ValidPaddedSignature_StillReturnsNull()
        {
            const string issuer = "https://test.issuer";
            const string audience = "https://test.audience";
            const string rawKey = "super"; // publicly known demo key (JwtOption.KnownPublicKeys)
            const string userCode = "sync_weak_demo";

            var configs = MakeSubHostConfigs(rawKey, issuer, audience);
            var token = BuildJwt(configs.JwtOptions.SecurityKey, issuer, audience, userCode);

            var httpCtx = MakeRemoteTokenHttpContext(token);
            var (wtm, _) = MakeWtmContextWithConfig(httpCtx, configs);

            bool reloadCalled = false;
            WTMContext.ReloadUserFunc = (_, _) =>
            {
                reloadCalled = true;
                return new LoginUserInfo { ITCode = "should_not_reach" };
            };

            try
            {
                var info = wtm.LoginUserInfo;

                reloadCalled.Should().BeFalse(
                    "#923: the publicly known demo key 'super' must be rejected regardless of a technically-valid signature");
                info.Should().BeNull();
            }
            finally
            {
                WTMContext.ReloadUserFunc = null;
            }
        }

        // ─── Async EnsureLoginUserInfoAsync (WTMContext.User.cs's second _remotetoken branch) ──

        [TestMethod]
        public async Task EnsureLoginUserInfoAsync_RemoteToken_ShortRawKey_ValidPaddedSignature_LeavesLoginUserInfoNull()
        {
            const string issuer = "https://test.issuer";
            const string audience = "https://test.audience";
            const string rawKey = "shortkey123";
            const string userCode = "async_weak_short";

            var configs = MakeSubHostConfigs(rawKey, issuer, audience);
            var token = BuildJwt(configs.JwtOptions.SecurityKey, issuer, audience, userCode);

            var httpCtx = MakeRemoteTokenHttpContext(token);
            var (wtm, _) = MakeWtmContextWithConfig(httpCtx, configs);

            bool reloadCalled = false;
            WTMContext.ReloadUserFunc = (_, _) =>
            {
                reloadCalled = true;
                return new LoginUserInfo { ITCode = "should_not_reach" };
            };

            try
            {
                await wtm.EnsureLoginUserInfoAsync();

                reloadCalled.Should().BeFalse(
                    "#923: EnsureLoginUserInfoAsync's independent copy of this guard must also reject a raw key under 32 bytes before ReloadUserAsync is called");
                wtm.LoginUserInfo.Should().BeNull();
            }
            finally
            {
                WTMContext.ReloadUserFunc = null;
            }
        }

        [TestMethod]
        public async Task EnsureLoginUserInfoAsync_RemoteToken_DemoKeySuper_ValidPaddedSignature_LeavesLoginUserInfoNull()
        {
            const string issuer = "https://test.issuer";
            const string audience = "https://test.audience";
            const string rawKey = "super";
            const string userCode = "async_weak_demo";

            var configs = MakeSubHostConfigs(rawKey, issuer, audience);
            var token = BuildJwt(configs.JwtOptions.SecurityKey, issuer, audience, userCode);

            var httpCtx = MakeRemoteTokenHttpContext(token);
            var (wtm, _) = MakeWtmContextWithConfig(httpCtx, configs);

            bool reloadCalled = false;
            WTMContext.ReloadUserFunc = (_, _) =>
            {
                reloadCalled = true;
                return new LoginUserInfo { ITCode = "should_not_reach" };
            };

            try
            {
                await wtm.EnsureLoginUserInfoAsync();

                reloadCalled.Should().BeFalse(
                    "#923: EnsureLoginUserInfoAsync's independent copy of this guard must also reject the publicly known 'super' key before ReloadUserAsync is called");
                wtm.LoginUserInfo.Should().BeNull();
            }
            finally
            {
                WTMContext.ReloadUserFunc = null;
            }
        }

        // ─── Strong-key positive controls (both paths must still WORK for a real key) ──

        [TestMethod]
        public void LoginUserInfoGetter_RemoteToken_StrongKey_ValidSignature_PopulatesLoginUserInfo()
        {
            const string issuer = "https://test.issuer";
            const string audience = "https://test.audience";
            // #931 item 2: was a fixed literal, publicly readable via test/'s mirror sync.
            var rawKey = JwtTestKeys.StrongCustomKey;
            const string userCode = "sync_strong";

            var configs = MakeSubHostConfigs(rawKey, issuer, audience);
            var token = BuildJwt(configs.JwtOptions.SecurityKey, issuer, audience, userCode);

            var httpCtx = MakeRemoteTokenHttpContext(token);
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
                var info = wtm.LoginUserInfo;

                reloadCount.Should().Be(1,
                    "a strong key must still validate a genuinely valid token and reach ReloadUser exactly once");
                info.Should().NotBeNull();
                info!.ITCode.Should().Be(userCode);
            }
            finally
            {
                WTMContext.ReloadUserFunc = null;
            }
        }
    }
}
