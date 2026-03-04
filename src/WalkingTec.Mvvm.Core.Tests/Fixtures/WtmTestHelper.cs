using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Core.Implement;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Tests.Fixtures
{
    /// <summary>
    /// Minimal concrete user for DoLoginAsync tests.
    /// FrameworkUserBase is abstract; each application defines its own subclass.
    /// Core.Tests defines this lightweight version to avoid depending on demo projects.
    /// </summary>
    public class TestLoginUser : FrameworkUserBase { }

    /// <summary>
    /// Common helpers for creating test data and configured WTM contexts.
    /// </summary>
    public static class WtmTestHelper
    {
        // ─── Test Data Factories ───────────────────────────────────────────────

        /// <summary>Create a TestLoginUser with PBKDF2 password.</summary>
        public static TestLoginUser CreateUser(
            string itCode = "testuser",
            string password = "000000",
            string? tenantCode = null,
            bool isValid = true)
        {
            return new TestLoginUser
            {
                ID = Guid.NewGuid(),
                ITCode = itCode,
                Password = PasswordHashHelper.HashPassword(password),
                TenantCode = tenantCode,
                IsValid = isValid,
                Name = $"Test_{itCode}"
            };
        }

        /// <summary>Create a TestLoginUser with legacy MD5 password hash (for migration tests).</summary>
        public static TestLoginUser CreateLegacyMd5User(
            string itCode = "legacy_user",
            string password = "000000",
            string? tenantCode = null)
        {
            return new TestLoginUser
            {
                ID = Guid.NewGuid(),
                ITCode = itCode,
                Password = PasswordHashHelper.ComputeMD5(password),
                TenantCode = tenantCode,
                IsValid = true,
                Name = $"Legacy_{itCode}"
            };
        }

        /// <summary>Create a RefreshTokenEntity with specified state.</summary>
        public static RefreshTokenEntity CreateRefreshToken(
            string itCode = "testuser",
            string? tenantCode = null,
            int expiresInDays = 7,
            bool revoked = false)
        {
            var token = new RefreshTokenEntity
            {
                Token = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) +
                        Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
                ITCode = itCode,
                TenantCode = tenantCode,
                ExpiresUtc = DateTime.UtcNow.AddDays(expiresInDays),
                CreatedByIp = "127.0.0.1"
            };
            if (revoked)
            {
                token.RevokedUtc = DateTime.UtcNow;
                token.RevokeReason = "Test revocation";
            }
            return token;
        }

        // ─── Context Factories ─────────────────────────────────────────────────

        /// <summary>
        /// Create a WTMContext fully configured for DoLoginAsync testing.
        /// Unlike MockWtmContext.CreateWtmContext(), this fixture:
        /// - Sets GlobaInfo.CustomUserType = typeof(TestLoginUser)
        /// - Sets GlobaInfo.TenantGetFunc to return empty list (prevents NPE)
        /// - Registers ITokenService in HttpContext.RequestServices
        /// </summary>
        public static WTMContext CreateLoginTestContext(
            IDataContext dc,
            ITokenService? tokenService = null,
            string userCode = "admin")
        {
            var mockTs = tokenService ?? CreateNoOpTokenService();

            var gd = new GlobalData();
            gd.AllAccessUrls = new List<string>();
            gd.AllAssembly = new List<System.Reflection.Assembly>();
            gd.AllModule = new List<Core.Support.Json.SimpleModule>();
            gd.CustomUserType = typeof(TestLoginUser);
            // AllTenant calls TenantGetFunc?.Invoke() — must not return null to avoid NPE
            gd.SetTenantGetFunc(() => new List<FrameworkTenant>());

            var cache = new MemoryDistributedCache(
                Options.Create(new MemoryDistributedCacheOptions()));
            var res = new ResourceManagerStringLocalizerFactory(
                Options.Create(new LocalizationOptions { ResourcesPath = "Resources" }),
                new Microsoft.Extensions.Logging.LoggerFactory());

            var mockService = new Mock<IServiceProvider>();
            mockService.Setup(x => x.GetService(typeof(IDistributedCache))).Returns(cache);
            mockService.Setup(x => x.GetService(typeof(ITokenService))).Returns(mockTs);

            var mockHttpRequest = new Mock<HttpRequest>();
            mockHttpRequest.Setup(x => x.Cookies).Returns(new MockCookie());
            mockHttpRequest.Setup(x => x.Query).Returns(new Mock<IQueryCollection>().Object);

            var mockHttpContext = new Mock<HttpContext>();
            mockHttpContext.Setup(x => x.Request).Returns(mockHttpRequest.Object);
            mockHttpContext.Setup(x => x.RequestServices).Returns(mockService.Object);
            mockHttpContext.Setup(x => x.User)
                .Returns(new System.Security.Claims.ClaimsPrincipal());
            var mockSession = new MockHttpSession();
            mockHttpContext.Setup(x => x.Session).Returns(mockSession);

            var httpa = new HttpContextAccessor { HttpContext = mockHttpContext.Object };
            var wtm = new WTMContext(null, gd, httpa, new DefaultUIService(), null, dc, res, cache: cache);
            wtm.MSD = new BasicMSD();
            wtm.DC = dc;
            wtm.LoginUserInfo = new LoginUserInfo { ITCode = userCode };

            mockService.Setup(x => x.GetService(typeof(WtmFileProvider)))
                .Returns(new WtmFileProvider(wtm));

            return wtm;
        }

        private static ITokenService CreateNoOpTokenService()
        {
            var mock = new Mock<ITokenService>();
            mock.Setup(x => x.IssueTokenAsync(It.IsAny<LoginUserInfo>(), It.IsAny<string?>()))
                .Returns(Task.FromResult(new Token
                {
                    AccessToken = "test_access_token",
                    RefreshToken = "test_refresh_token",
                    ExpiresIn = 3600,
                    TokenType = "Bearer"
                }));
            mock.Setup(x => x.RefreshTokenAsync(It.IsAny<string>(), It.IsAny<string?>()))
                .Returns(Task.FromResult<Token>(null!));
            mock.Setup(x => x.RevokeTokenAsync(
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .Returns(System.Threading.Tasks.Task.CompletedTask);
            return mock.Object;
        }
    }
}
