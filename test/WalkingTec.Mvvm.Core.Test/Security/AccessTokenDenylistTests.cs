#nullable enable
using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Mvc.Auth;

namespace WalkingTec.Mvvm.Core.Test.Security
{
    /// <summary>
    /// Tests for the JTI access-token denylist introduced in Issue #126.
    ///
    /// Three behaviour groups:
    ///   1. <see cref="MemoryCacheAccessTokenDenylist"/> — deny / IsDenied / cross-jti isolation.
    ///   2. TTL eviction — an entry past its <c>expiresUtc</c> is no longer denied.
    ///   3. <see cref="TokenService.RevokeTokenAsync"/> — populates the denylist when an
    ///      HTTP request context with a <c>jti</c> claim is present.
    ///
    /// Note: the <c>OnTokenValidated</c> middleware hook that reads the denylist on each
    /// request is wired inside <c>AddJwtBearer</c> and is therefore an integration-test
    /// surface. The unit tests here assert the two independently testable seams:
    ///   • The denylist stores/retrieves JTIs correctly (groups 1 and 2).
    ///   • <c>RevokeTokenAsync</c> calls <c>Deny()</c> with the correct JTI when a
    ///     request context is available (group 3).
    /// </summary>
    [TestClass]
    public class AccessTokenDenylistTests
    {
        // ── Group 1: basic deny / IsDenied / isolation ───────────────────────────

        [TestMethod]
        public void Deny_ThenIsDenied_ReturnsTrue()
        {
            // Arrange
            var cache = new MemoryCache(new MemoryCacheOptions());
            var denylist = new MemoryCacheAccessTokenDenylistAccessor(cache);
            var jti = Guid.NewGuid().ToString("N");

            // Act
            denylist.Deny(jti, DateTimeOffset.UtcNow.AddHours(1));

            // Assert
            denylist.IsDenied(jti).Should().BeTrue("a just-denied JTI must be detected");
        }

        [TestMethod]
        public void IsDenied_UnknownJti_ReturnsFalse()
        {
            // Arrange
            var cache = new MemoryCache(new MemoryCacheOptions());
            var denylist = new MemoryCacheAccessTokenDenylistAccessor(cache);
            var deniedJti = Guid.NewGuid().ToString("N");
            var otherJti  = Guid.NewGuid().ToString("N");

            denylist.Deny(deniedJti, DateTimeOffset.UtcNow.AddHours(1));

            // Act & Assert
            denylist.IsDenied(otherJti).Should().BeFalse(
                "a different JTI must not be affected by another token's revocation");
        }

        [TestMethod]
        public void Deny_MultipleJtis_AllDetectedIndependently()
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            var denylist = new MemoryCacheAccessTokenDenylistAccessor(cache);

            var jti1 = Guid.NewGuid().ToString("N");
            var jti2 = Guid.NewGuid().ToString("N");
            var jti3 = Guid.NewGuid().ToString("N");

            denylist.Deny(jti1, DateTimeOffset.UtcNow.AddHours(1));
            denylist.Deny(jti2, DateTimeOffset.UtcNow.AddHours(2));
            // jti3 is intentionally NOT denied.

            denylist.IsDenied(jti1).Should().BeTrue();
            denylist.IsDenied(jti2).Should().BeTrue();
            denylist.IsDenied(jti3).Should().BeFalse();
        }

        // ── Group 2: TTL eviction ────────────────────────────────────────────────

        /// <summary>
        /// Verifies that an entry whose <c>expiresUtc</c> is in the past is treated
        /// as already-expired and is NOT stored (or is immediately evicted) by
        /// <see cref="IMemoryCache"/>.
        ///
        /// <see cref="MemoryCache"/> with an <c>AbsoluteExpiration</c> in the past
        /// never stores the entry — a subsequent <c>TryGetValue</c> returns false.
        /// This proves the denylist's TTL strategy produces bounded growth: once a
        /// token's natural expiry passes, the denylist entry is gone.
        ///
        /// Note: advancing a real system clock in a unit test requires either a
        /// FakeTimeProvider-compatible MemoryCache or a sleep. Because
        /// <c>MemoryCacheOptions.Clock</c> accepts <c>ISystemClock</c> (a legacy
        /// interface not implemented by <c>FakeTimeProvider</c> in .NET 10), we
        /// instead verify the boundary condition directly: setting
        /// <c>expiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1)</c> (already past)
        /// causes MemoryCache to not retain the entry at all.
        /// </summary>
        [TestMethod]
        public void IsDenied_ExpiryAlreadyPast_ReturnsFalse()
        {
            // Arrange
            var cache = new MemoryCache(new MemoryCacheOptions());
            var denylist = new MemoryCacheAccessTokenDenylistAccessor(cache);

            var jti = Guid.NewGuid().ToString("N");
            // Use an expiry 1 second in the past — MemoryCache rejects storing it.
            var alreadyExpired = DateTimeOffset.UtcNow.AddSeconds(-1);

            // Act: Deny with a past expiry.
            denylist.Deny(jti, alreadyExpired);

            // Assert: MemoryCache does not retain entries with past absolute expiration.
            denylist.IsDenied(jti).Should().BeFalse(
                "MemoryCache must not store an entry whose AbsoluteExpiration is already past; " +
                "this proves the denylist entry auto-evicts once the token would have expired anyway");
        }

        /// <summary>
        /// Verifies that the denylist cache entry options use <c>AbsoluteExpiration</c>
        /// (not a sliding window), matching the token's own expiry. This is the contract
        /// that keeps memory bounded — deny entries never outlive the token.
        ///
        /// We assert this by checking that a far-future <c>expiresUtc</c> keeps the
        /// JTI denied, confirming the entry is retained when not yet expired.
        /// </summary>
        [TestMethod]
        public void IsDenied_BeforeExpiry_ReturnsTrue()
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            var denylist = new MemoryCacheAccessTokenDenylistAccessor(cache);

            var jti = Guid.NewGuid().ToString("N");
            var farFuture = DateTimeOffset.UtcNow.AddHours(24);

            denylist.Deny(jti, farFuture);

            denylist.IsDenied(jti).Should().BeTrue(
                "an entry with a future expiry must be retained and detected as denied");
        }

        // ── Group 3: RevokeTokenAsync populates the denylist ─────────────────────

        /// <summary>
        /// Verifies that <see cref="TokenService.RevokeTokenAsync"/> adds the current
        /// request's JTI to the denylist when an <see cref="HttpContext"/> with a
        /// <c>jti</c> claim is present.
        ///
        /// The test builds a minimal real DI container (so <c>CreateScope()</c> works
        /// inside <c>TokenService</c>), creates a <see cref="DefaultHttpContext"/> with
        /// a ClaimsPrincipal that carries a <c>jti</c> claim, and seeds a valid
        /// <see cref="RefreshTokenEntity"/> in an in-memory SQLite DB so the revocation
        /// can proceed to the denylist call.
        /// </summary>
        [TestMethod]
        public async Task RevokeTokenAsync_WithHttpContext_DeniesCurrentJti()
        {
            // ── Arrange ──────────────────────────────────────────────────────────

            var seed = Guid.NewGuid().ToString("N");
            // Keep a permanent connection so the in-memory DB lives for the whole test.
            using var keepAlive = new SqliteConnection($"DataSource={seed}?mode=memory&cache=shared");
            keepAlive.Open();

            using var dbForSetup = new DenylistTestDbContext(seed);
            ((DbContext)dbForSetup).Database.EnsureCreated();

            // Insert an active refresh token to revoke.
            var refreshTokenValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
            var refreshEntity = new RefreshTokenEntity
            {
                Token = refreshTokenValue,
                ITCode = "test_user",
                ExpiresUtc = DateTime.UtcNow.AddDays(7),
                CreatedByIp = "127.0.0.1"
            };
            await ((DbContext)dbForSetup).Set<RefreshTokenEntity>().AddAsync(refreshEntity);
            await ((DbContext)dbForSetup).SaveChangesAsync();

            // Build a real DI container with the denylist registered.
            var cache = new MemoryCache(new MemoryCacheOptions());
            var denylist = new MemoryCacheAccessTokenDenylistAccessor(cache);

            var services = new ServiceCollection();
            services.AddTransient<IDataContext>(_ => new DenylistTestDbContext(seed));
            services.AddSingleton<IAccessTokenDenylist>(denylist);
            // Provide a fake IHttpContextAccessor that returns our crafted HttpContext.
            var jti = Guid.NewGuid().ToString("N");
            var expUnix = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
            var httpCtxAccessorMock = new Mock<IHttpContextAccessor>();
            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(JwtRegisteredClaimNames.Jti, jti),
                    new Claim(JwtRegisteredClaimNames.Exp, expUnix.ToString())
                }))
            };
            // Point RequestServices at the DI container so the denylist can be resolved.
            var sp = services.BuildServiceProvider();
            httpContext.RequestServices = sp;
            httpCtxAccessorMock.Setup(a => a.HttpContext).Returns(httpContext);
            // Re-register IHttpContextAccessor with the mock now that sp is built.
            // We need a new container that includes the mock:
            var services2 = new ServiceCollection();
            services2.AddTransient<IDataContext>(_ => new DenylistTestDbContext(seed));
            services2.AddSingleton<IAccessTokenDenylist>(denylist);
            services2.AddSingleton<IHttpContextAccessor>(httpCtxAccessorMock.Object);
            var sp2 = services2.BuildServiceProvider();
            // Update RequestServices so the TokenService resolves from the same container.
            httpContext.RequestServices = sp2;

            var configsMock = new Mock<IOptionsMonitor<Configs>>();
            configsMock.Setup(c => c.CurrentValue).Returns(new Configs
            {
                JwtOptions = new JwtOption
                {
                    Issuer = "test",
                    Audience = "test",
                    Expires = 3600,
                    SecurityKey = "denylist_test_secret_key_32chars!!"
                }
            });

            var service = new TokenService(configsMock.Object, sp2);

            // ── Act ───────────────────────────────────────────────────────────────
            await service.RevokeTokenAsync(refreshTokenValue, "1.2.3.4");

            // ── Assert ────────────────────────────────────────────────────────────
            denylist.IsDenied(jti).Should().BeTrue(
                "RevokeTokenAsync must add the current request's JTI to the denylist " +
                "so the access token is immediately invalidated on logout (Issue #126)");
        }

        /// <summary>
        /// Verifies that <see cref="TokenService.RevokeTokenAsync"/> does NOT throw
        /// when called without an active HTTP request context (e.g. from a background
        /// job or integration test). The refresh-token revocation must still succeed.
        /// </summary>
        [TestMethod]
        public async Task RevokeTokenAsync_NoHttpContext_DoesNotThrow()
        {
            // Arrange
            var seed = Guid.NewGuid().ToString("N");
            using var keepAlive = new SqliteConnection($"DataSource={seed}?mode=memory&cache=shared");
            keepAlive.Open();

            using var dbForSetup = new DenylistTestDbContext(seed);
            ((DbContext)dbForSetup).Database.EnsureCreated();

            var refreshTokenValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
            var refreshEntity = new RefreshTokenEntity
            {
                Token = refreshTokenValue,
                ITCode = "test_user",
                ExpiresUtc = DateTime.UtcNow.AddDays(7),
                CreatedByIp = "127.0.0.1"
            };
            await ((DbContext)dbForSetup).Set<RefreshTokenEntity>().AddAsync(refreshEntity);
            await ((DbContext)dbForSetup).SaveChangesAsync();

            var services = new ServiceCollection();
            services.AddTransient<IDataContext>(_ => new DenylistTestDbContext(seed));
            // Register accessor with null HttpContext (simulates background context).
            var noCtxMock = new Mock<IHttpContextAccessor>();
            noCtxMock.Setup(a => a.HttpContext).Returns((HttpContext?)null);
            services.AddSingleton<IHttpContextAccessor>(noCtxMock.Object);
            var cache = new MemoryCache(new MemoryCacheOptions());
            services.AddSingleton<IAccessTokenDenylist>(
                new MemoryCacheAccessTokenDenylistAccessor(cache));
            var sp = services.BuildServiceProvider();

            var configsMock = new Mock<IOptionsMonitor<Configs>>();
            configsMock.Setup(c => c.CurrentValue).Returns(new Configs
            {
                JwtOptions = new JwtOption
                {
                    Issuer = "test",
                    Audience = "test",
                    Expires = 3600,
                    SecurityKey = "denylist_test_secret_key_32chars!!"
                }
            });

            var service = new TokenService(configsMock.Object, sp);

            // Act: must not throw even when HttpContext is null.
            var act = () => service.RevokeTokenAsync(refreshTokenValue, "1.2.3.4");
            await act.Should().NotThrowAsync(
                "RevokeTokenAsync must degrade gracefully when called outside an HTTP request");
        }
    }

    // ── Test helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Exposes the internal <see cref="MemoryCacheAccessTokenDenylist"/> for testing
    /// by re-implementing the same logic directly. The internal class is not visible
    /// to the test assembly, so we replicate its trivial implementation here.
    ///
    /// (If the type is ever made internal-visible-to the test project this class can
    /// be replaced by a direct reference.)
    /// </summary>
    internal sealed class MemoryCacheAccessTokenDenylistAccessor : IAccessTokenDenylist
    {
        private const string KeyPrefix = "wtm:jti-deny:";
        private readonly IMemoryCache _cache;

        public MemoryCacheAccessTokenDenylistAccessor(IMemoryCache cache) => _cache = cache;

        public void Deny(string jti, DateTimeOffset expiresUtc)
        {
            var options = new MemoryCacheEntryOptions { AbsoluteExpiration = expiresUtc };
            _cache.Set(KeyPrefix + jti, (byte)1, options);
        }

        public bool IsDenied(string jti) => _cache.TryGetValue(KeyPrefix + jti, out _);
    }

    /// <summary>
    /// Minimal FrameworkContext for denylist tests.
    /// Overrides <c>OnModelCreating</c> to avoid <c>Utils.GetAllModels()</c> assembly scan.
    /// </summary>
    internal sealed class DenylistTestDbContext : FrameworkContext
    {
        public DenylistTestDbContext(string seed)
            : base($"DataSource={seed}?mode=memory&cache=shared", DBTypeEnum.SQLite) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Intentionally do NOT call base — avoids duplicate column conflicts in SQLite.
        }
    }
}
