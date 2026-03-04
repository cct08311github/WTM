using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Mvc.Tests.Fixtures;
using Xunit;

namespace WalkingTec.Mvvm.Mvc.Tests.Integration
{
    /// <summary>
    /// Integration tests for TokenService.
    /// Each test class instance gets its own isolated InMemory DB via TokenTestFixture.
    /// </summary>
    public class TokenServiceIntegrationTests : IDisposable
    {
        private readonly TokenTestFixture _fx;

        public TokenServiceIntegrationTests()
        {
            _fx = new TokenTestFixture();
        }

        // ─── IssueTokenAsync ───────────────────────────────────────────────────

        [Fact]
        public async Task IssueTokenAsync_ValidUser_ReturnsAccessAndRefreshToken()
        {
            var user = new LoginUserInfo { ITCode = "alice", TenantCode = null };

            var token = await _fx.TokenService.IssueTokenAsync(user, "127.0.0.1");

            token.Should().NotBeNull();
            token.AccessToken.Should().NotBeNullOrEmpty();
            token.RefreshToken.Should().NotBeNullOrEmpty();
            token.TokenType.Should().Be("Bearer");
            token.ExpiresIn.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task IssueTokenAsync_PersistsRefreshTokenInDb()
        {
            var user = new LoginUserInfo { ITCode = "bob" };

            var token = await _fx.TokenService.IssueTokenAsync(user);

            using var db = _fx.CreateDbContext();
            var stored = await db.Set<RefreshTokenEntity>()
                .FirstOrDefaultAsync(x => x.Token == token.RefreshToken);
            stored.Should().NotBeNull();
            stored!.ITCode.Should().Be("bob");
            stored.IsActive.Should().BeTrue();
        }

        [Fact]
        public async Task IssueTokenAsync_NullUser_ThrowsArgumentNullException()
        {
            var act = () => _fx.TokenService.IssueTokenAsync(null!);

            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [Fact]
        public async Task IssueTokenAsync_TwoIssues_ProduceDistinctRefreshTokens()
        {
            var user = new LoginUserInfo { ITCode = "charlie" };

            var t1 = await _fx.TokenService.IssueTokenAsync(user);
            var t2 = await _fx.TokenService.IssueTokenAsync(user);

            t1.RefreshToken.Should().NotBe(t2.RefreshToken,
                "each issue produces a cryptographically unique token");
        }

        [Fact]
        public async Task IssueTokenAsync_PreservesTenantCode()
        {
            var user = new LoginUserInfo { ITCode = "tenant_user", TenantCode = "tenant_a" };

            var token = await _fx.TokenService.IssueTokenAsync(user);

            using var db = _fx.CreateDbContext();
            var stored = await db.Set<RefreshTokenEntity>()
                .FirstOrDefaultAsync(x => x.Token == token.RefreshToken);
            stored!.TenantCode.Should().Be("tenant_a");
        }

        // ─── RefreshTokenAsync ─────────────────────────────────────────────────

        [Fact]
        public async Task RefreshTokenAsync_ValidToken_ReturnsNewTokenPairAndRotatesOld()
        {
            var user = new LoginUserInfo { ITCode = "dave" };
            var issued = await _fx.TokenService.IssueTokenAsync(user);

            var refreshed = await _fx.TokenService.RefreshTokenAsync(
                issued.RefreshToken, "127.0.0.1");

            refreshed.Should().NotBeNull();
            refreshed!.RefreshToken.Should().NotBe(issued.RefreshToken,
                "old token must be rotated out");
            refreshed.AccessToken.Should().NotBeNullOrEmpty();

            // Old token is now revoked
            using var db = _fx.CreateDbContext();
            var old = await db.Set<RefreshTokenEntity>()
                .FirstOrDefaultAsync(x => x.Token == issued.RefreshToken);
            old!.IsRevoked.Should().BeTrue();
            old.ReplacedByToken.Should().Be(refreshed.RefreshToken);
        }

        [Fact]
        public async Task RefreshTokenAsync_ExpiredToken_ReturnsNull()
        {
            var expired = new RefreshTokenEntity
            {
                Token = "expired_token",
                ITCode = "eve",
                ExpiresUtc = DateTime.UtcNow.AddDays(-1),
                CreatedByIp = "127.0.0.1"
            };
            _fx.SeedToken(expired);

            var result = await _fx.TokenService.RefreshTokenAsync("expired_token");

            result.Should().BeNull();
        }

        [Fact]
        public async Task RefreshTokenAsync_RevokedToken_ReturnsNull()
        {
            var revoked = new RefreshTokenEntity
            {
                Token = "revoked_token",
                ITCode = "frank",
                ExpiresUtc = DateTime.UtcNow.AddDays(7),
                RevokedUtc = DateTime.UtcNow.AddMinutes(-5),
                RevokeReason = "Test"
            };
            _fx.SeedToken(revoked);

            var result = await _fx.TokenService.RefreshTokenAsync("revoked_token");

            result.Should().BeNull();
        }

        [Fact]
        public async Task RefreshTokenAsync_NonexistentToken_ReturnsNull()
        {
            var result = await _fx.TokenService.RefreshTokenAsync("does_not_exist_token");
            result.Should().BeNull();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public async Task RefreshTokenAsync_NullOrEmpty_ReturnsNull(string? token)
        {
            var result = await _fx.TokenService.RefreshTokenAsync(token!);
            result.Should().BeNull();
        }

        // ─── Reuse Detection ───────────────────────────────────────────────────

        [Fact]
        public async Task RefreshTokenAsync_ReusedToken_RevokesDescendantChain()
        {
            // Issue T1
            var user = new LoginUserInfo { ITCode = "grace" };
            var issued = await _fx.TokenService.IssueTokenAsync(user);
            var t1 = issued.RefreshToken;

            // Refresh T1 → get T2
            var secondIssue = await _fx.TokenService.RefreshTokenAsync(t1);
            secondIssue.Should().NotBeNull();
            var t2 = secondIssue!.RefreshToken;

            // Attempt to reuse T1 (already revoked) → should revoke T2 as well
            var reuseResult = await _fx.TokenService.RefreshTokenAsync(t1);
            reuseResult.Should().BeNull("reused token must be rejected");

            // Verify T2 (descendant) is now also revoked
            using var db = _fx.CreateDbContext();
            var t2Entity = await db.Set<RefreshTokenEntity>()
                .FirstOrDefaultAsync(x => x.Token == t2);
            t2Entity.Should().NotBeNull();
            t2Entity!.IsRevoked.Should().BeTrue(
                "descendant token must be revoked when ancestor is reused (token theft detection)");
        }

        // ─── RevokeTokenAsync ──────────────────────────────────────────────────

        [Fact]
        public async Task RevokeTokenAsync_ActiveToken_SetsRevokedUtcAndReason()
        {
            var user = new LoginUserInfo { ITCode = "helen" };
            var issued = await _fx.TokenService.IssueTokenAsync(user);

            await _fx.TokenService.RevokeTokenAsync(
                issued.RefreshToken, "127.0.0.1", "Explicit logout");

            using var db = _fx.CreateDbContext();
            var entity = await db.Set<RefreshTokenEntity>()
                .FirstOrDefaultAsync(x => x.Token == issued.RefreshToken);
            entity!.IsRevoked.Should().BeTrue();
            entity.RevokeReason.Should().Be("Explicit logout");
            entity.RevokedByIp.Should().Be("127.0.0.1");
        }

        [Fact]
        public async Task RevokeTokenAsync_AlreadyRevoked_IsNoOp()
        {
            var revoked = new RefreshTokenEntity
            {
                Token = "already_revoked",
                ITCode = "ivan",
                ExpiresUtc = DateTime.UtcNow.AddDays(7),
                RevokedUtc = DateTime.UtcNow.AddHours(-1),
                RevokeReason = "First revocation"
            };
            _fx.SeedToken(revoked);

            // Should not throw and should not change the existing revocation reason
            var act = () => _fx.TokenService.RevokeTokenAsync(
                "already_revoked", "127.0.0.1", "Second attempt");
            await act.Should().NotThrowAsync();

            using var db = _fx.CreateDbContext();
            var entity = await db.Set<RefreshTokenEntity>()
                .FirstOrDefaultAsync(x => x.Token == "already_revoked");
            entity!.RevokeReason.Should().Be("First revocation",
                "already-revoked token is not modified");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public async Task RevokeTokenAsync_NullOrEmpty_DoesNotThrow(string? token)
        {
            var act = () => _fx.TokenService.RevokeTokenAsync(token!);
            await act.Should().NotThrowAsync();
        }

        // ─── Tenant Isolation ──────────────────────────────────────────────────

        [Fact]
        public async Task IssueTokenAsync_DifferentTenants_TokensHaveDifferentTenantCodes()
        {
            var userA = new LoginUserInfo { ITCode = "judy", TenantCode = "tenant_a" };
            var userB = new LoginUserInfo { ITCode = "judy", TenantCode = "tenant_b" };

            var tokenA = await _fx.TokenService.IssueTokenAsync(userA);
            var tokenB = await _fx.TokenService.IssueTokenAsync(userB);

            using var db = _fx.CreateDbContext();
            var storedA = await db.Set<RefreshTokenEntity>()
                .FirstOrDefaultAsync(x => x.Token == tokenA.RefreshToken);
            var storedB = await db.Set<RefreshTokenEntity>()
                .FirstOrDefaultAsync(x => x.Token == tokenB.RefreshToken);

            storedA!.TenantCode.Should().Be("tenant_a");
            storedB!.TenantCode.Should().Be("tenant_b");
            tokenA.RefreshToken.Should().NotBe(tokenB.RefreshToken);
        }

        public void Dispose() => _fx.Dispose();
    }
}
