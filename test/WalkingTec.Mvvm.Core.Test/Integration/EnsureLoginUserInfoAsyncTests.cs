#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Implement;
using WalkingTec.Mvvm.Core.Services;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Core.Support.Json;
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
    }
}
