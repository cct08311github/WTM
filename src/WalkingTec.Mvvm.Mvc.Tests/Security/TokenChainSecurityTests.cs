using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Mvc.Tests.Fixtures;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Mvc.Tests.Security
{
    /// <summary>
    /// Security-focused tests for the JWT refresh token chain.
    /// Verifies theft-detection, chain revocation, and token uniqueness properties
    /// that go beyond basic happy-path coverage in TokenServiceIntegrationTests.
    /// </summary>
    [TestClass]
    public class TokenChainSecurityTests
    {
        private TokenTestFixture _fx = null!;

        [TestInitialize]
        public void Setup()
        {
            _fx = new TokenTestFixture();
        }

        // ─── Reuse Detection — Multi-Hop Chain ──────────────────────────────

        [TestMethod]
        public async Task RefreshChain_ThreeHops_EachPriorTokenRevoked()
        {
            // Issue T1, refresh → T2, refresh → T3
            // Each step must invalidate the previous token
            var user = new LoginUserInfo { ITCode = "chain_user" };
            var issued = await _fx.TokenService.IssueTokenAsync(user);
            var t1 = issued.RefreshToken;

            var r2 = await _fx.TokenService.RefreshTokenAsync(t1);
            r2.Should().NotBeNull();
            var t2 = r2!.RefreshToken;

            var r3 = await _fx.TokenService.RefreshTokenAsync(t2);
            r3.Should().NotBeNull();
            var t3 = r3!.RefreshToken;

            using var db = _fx.CreateDbContext();

            // T1 and T2 must be revoked; T3 is the live token
            var e1 = await db.Set<RefreshTokenEntity>().FirstAsync(x => x.Token == t1);
            var e2 = await db.Set<RefreshTokenEntity>().FirstAsync(x => x.Token == t2);
            var e3 = await db.Set<RefreshTokenEntity>().FirstAsync(x => x.Token == t3);

            e1.IsRevoked.Should().BeTrue("T1 was rotated out when T2 was issued");
            e2.IsRevoked.Should().BeTrue("T2 was rotated out when T3 was issued");
            e3.IsActive.Should().BeTrue("T3 is the live token at the end of the chain");
        }

        [TestMethod]
        public async Task RefreshChain_ReuseAtMidpoint_RevokesAllDescendants()
        {
            // Issue T1 → T2 → T3
            // Reusing T2 (already spent) should revoke T3 (the descendant) as theft detection
            var user = new LoginUserInfo { ITCode = "midchain_user" };
            var issued = await _fx.TokenService.IssueTokenAsync(user);
            var t1 = issued.RefreshToken;

            var r2 = await _fx.TokenService.RefreshTokenAsync(t1);
            var t2 = r2!.RefreshToken;

            var r3 = await _fx.TokenService.RefreshTokenAsync(t2);
            var t3 = r3!.RefreshToken;

            // Reuse T2 — already revoked when T3 was issued
            var reuseResult = await _fx.TokenService.RefreshTokenAsync(t2);
            reuseResult.Should().BeNull("reusing a spent token must be rejected");

            using var db = _fx.CreateDbContext();
            var e3 = await db.Set<RefreshTokenEntity>().FirstAsync(x => x.Token == t3);
            e3.IsRevoked.Should().BeTrue(
                "T3 (descendant of reused T2) must be revoked by theft detection");
        }

        // ─── Token Uniqueness ────────────────────────────────────────────────

        [TestMethod]
        public async Task IssueTokenAsync_RepeatedCalls_AlwaysProduceUniqueTokens()
        {
            const int count = 20;
            var user = new LoginUserInfo { ITCode = "uniqueness_user" };
            var tokens = new System.Collections.Generic.HashSet<string>();

            for (int i = 0; i < count; i++)
            {
                var issued = await _fx.TokenService.IssueTokenAsync(user);
                tokens.Add(issued.RefreshToken);
            }

            tokens.Should().HaveCount(count,
                $"each of {count} issue calls must produce a cryptographically unique refresh token");
        }

        [TestMethod]
        public async Task RefreshTokenAsync_Consecutive_NewTokenDiffersFromOld()
        {
            var user = new LoginUserInfo { ITCode = "rotate_user" };
            var issued = await _fx.TokenService.IssueTokenAsync(user);

            var refreshed = await _fx.TokenService.RefreshTokenAsync(issued.RefreshToken);

            refreshed.Should().NotBeNull();
            refreshed!.RefreshToken.Should().NotBe(issued.RefreshToken,
                "rotation must produce a different token, not reissue the same value");
            refreshed.AccessToken.Should().NotBe(issued.AccessToken,
                "access token must also be regenerated on rotation");
        }

        // ─── Revoke Propagation ──────────────────────────────────────────────

        [TestMethod]
        public async Task RevokedToken_ImmediatelyInactive_CannotRefresh()
        {
            var user = new LoginUserInfo { ITCode = "revoke_user" };
            var issued = await _fx.TokenService.IssueTokenAsync(user);

            await _fx.TokenService.RevokeTokenAsync(
                issued.RefreshToken, "127.0.0.1", "Security test revocation");

            var result = await _fx.TokenService.RefreshTokenAsync(issued.RefreshToken);
            result.Should().BeNull("explicitly revoked token must be rejected on refresh");
        }

        [TestMethod]
        public async Task ManualRevoke_DoesNotAffectOtherUsersTokens()
        {
            var userA = new LoginUserInfo { ITCode = "user_a" };
            var userB = new LoginUserInfo { ITCode = "user_b" };

            var tokenA = await _fx.TokenService.IssueTokenAsync(userA);
            var tokenB = await _fx.TokenService.IssueTokenAsync(userB);

            // Revoke only user_a's token
            await _fx.TokenService.RevokeTokenAsync(tokenA.RefreshToken, null, "Selective revoke");

            // user_b's token must still be usable
            var refreshedB = await _fx.TokenService.RefreshTokenAsync(tokenB.RefreshToken);
            refreshedB.Should().NotBeNull(
                "revoking one user's token must not affect other users' tokens");
        }

        // ─── Expired Token Boundary ──────────────────────────────────────────

        [TestMethod]
        public async Task ExpiredToken_CannotBeRefreshed_EvenIfNotExplicitlyRevoked()
        {
            var expired = new RefreshTokenEntity
            {
                Token = "expired_chain_token",
                ITCode = "expired_user",
                ExpiresUtc = DateTime.UtcNow.AddSeconds(-1),
                CreatedByIp = "127.0.0.1"
            };
            _fx.SeedToken(expired);

            var result = await _fx.TokenService.RefreshTokenAsync("expired_chain_token");
            result.Should().BeNull("expired tokens must be rejected regardless of revocation state");
        }

        [TestCleanup]
        public void Cleanup() => _fx?.Dispose();
    }
}
