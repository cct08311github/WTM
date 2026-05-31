using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
    /// Security tests for the atomic refresh-token rotation fix (Issue #118).
    ///
    /// Verifies that concurrent <c>RefreshTokenAsync</c> calls for the SAME token
    /// cannot both succeed — only one caller wins the <c>ExecuteUpdateAsync</c> claim,
    /// the other returns <c>null</c>, and no second active descendant token is created.
    ///
    /// Also validates existing sequential reuse-detection behaviour.
    ///
    /// Uses SQLite shared in-memory to keep the test self-contained and
    /// cross-provider (SQLite supports <c>ExecuteUpdateAsync</c> via EF Core 7+).
    /// </summary>
    [TestClass]
    public class RefreshTokenAtomicRotationTests
    {
        private string _seed = null!;
        private SqliteConnection _keepAlive = null!;
        private IServiceProvider _sp = null!;
        private TokenService _service = null!;

        [TestInitialize]
        public void Initialize()
        {
            _seed = Guid.NewGuid().ToString("N");
            // Keep a permanent connection so the in-memory DB lives for the whole test.
            _keepAlive = new SqliteConnection($"DataSource={_seed}?mode=memory&cache=shared");
            _keepAlive.Open();

            using var db = CreateDb();
            ((DbContext)db).Database.EnsureCreated();

            _sp = BuildServiceProvider();
            var configs = new Mock<IOptionsMonitor<Configs>>();
            configs.Setup(c => c.CurrentValue).Returns(new Configs
            {
                JwtOptions = new JwtOption
                {
                    Issuer = "test",
                    Audience = "test",
                    Expires = 3600,
                    SecurityKey = "atomic_rotation_secret_key_32chars!!"
                }
            });
            _service = new TokenService(configs.Object, _sp);
        }

        [TestCleanup]
        public void Cleanup() => _keepAlive.Dispose();

        // ── Helpers ──────────────────────────────────────────────────────────────

        private IDataContext CreateDb() => new TokenRotationTestDbContext(_seed);

        private IServiceProvider BuildServiceProvider()
        {
            // Build a real DI container so TokenService's CreateScope() works.
            // IServiceScopeFactory is registered automatically by BuildServiceProvider().
            // IDataContext is transient so each new scope opens its own DbContext
            // pointed at the same named in-memory DB via the seed.
            var services = new ServiceCollection();
            var seed = _seed;
            services.AddTransient<IDataContext>(_ => new TokenRotationTestDbContext(seed));
            return services.BuildServiceProvider();
        }

        private async Task<string> SeedActiveTokenAsync(string itCode = "user1")
        {
            using var db = CreateDb();
            var entity = new RefreshTokenEntity
            {
                Token = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
                ITCode = itCode,
                ExpiresUtc = DateTime.UtcNow.AddDays(7),
                CreatedByIp = "127.0.0.1"
            };
            await ((DbContext)db).Set<RefreshTokenEntity>().AddAsync(entity);
            await ((DbContext)db).SaveChangesAsync();
            return entity.Token;
        }

        // ── Sequential: basic rotation works ─────────────────────────────────────

        [TestMethod]
        public async Task RefreshToken_ActiveToken_ReturnsNewToken()
        {
            var tokenStr = await SeedActiveTokenAsync();
            var result = await _service.RefreshTokenAsync(tokenStr, "1.2.3.4");
            result.Should().NotBeNull("an active token should rotate successfully");
            result!.RefreshToken.Should().NotBeNullOrEmpty();
            result.RefreshToken.Should().NotBe(tokenStr, "the new token must differ from the original");
        }

        [TestMethod]
        public async Task RefreshToken_AlreadyRotated_ReturnsNull()
        {
            var tokenStr = await SeedActiveTokenAsync();
            // First rotation succeeds.
            var first = await _service.RefreshTokenAsync(tokenStr, "1.2.3.4");
            first.Should().NotBeNull();
            // Second attempt on the same (now-revoked) token must fail.
            var second = await _service.RefreshTokenAsync(tokenStr, "1.2.3.4");
            second.Should().BeNull("presenting a rotated token must be rejected");
        }

        [TestMethod]
        public async Task RefreshToken_ExpiredToken_ReturnsNull()
        {
            using var db = CreateDb();
            var entity = new RefreshTokenEntity
            {
                Token = "expired-token-value-" + Guid.NewGuid().ToString("N"),
                ITCode = "user1",
                ExpiresUtc = DateTime.UtcNow.AddMinutes(-5), // already expired
                CreatedByIp = "127.0.0.1"
            };
            await ((DbContext)db).Set<RefreshTokenEntity>().AddAsync(entity);
            await ((DbContext)db).SaveChangesAsync();

            var result = await _service.RefreshTokenAsync(entity.Token, "1.2.3.4");
            result.Should().BeNull("expired tokens must not be rotated");
        }

        [TestMethod]
        public async Task RefreshToken_NullOrEmpty_ReturnsNull()
        {
            (await _service.RefreshTokenAsync(null, null)).Should().BeNull();
            (await _service.RefreshTokenAsync("", null)).Should().BeNull();
        }

        // ── Reuse-attack detection ────────────────────────────────────────────────

        [TestMethod]
        public async Task RefreshToken_ReuseAttack_RevokeDescendantChain()
        {
            var originalToken = await SeedActiveTokenAsync("attacker_target");

            // Legitimate rotation: original → child.
            var legitimate = await _service.RefreshTokenAsync(originalToken, "legit-ip");
            legitimate.Should().NotBeNull();
            var childToken = legitimate!.RefreshToken;

            // Attacker replays the original (already-revoked) token.
            // This must: return null AND revoke the active child.
            var attack = await _service.RefreshTokenAsync(originalToken, "attacker-ip");
            attack.Should().BeNull("reused revoked token must be rejected");

            // Verify child is now revoked (descendant chain revoked).
            using var db = CreateDb();
            var child = await ((DbContext)db).Set<RefreshTokenEntity>()
                .FirstOrDefaultAsync(x => x.Token == childToken);
            child.Should().NotBeNull();
            child!.IsActive.Should().BeFalse("the child token must be revoked after reuse attack");
            child.RevokeReason.Should().NotBeNullOrEmpty("the reuse-attack revocation reason must be set");
        }

        // ── Concurrency / atomic-claim test ──────────────────────────────────────

        /// <summary>
        /// Simulates two concurrent <c>RefreshTokenAsync</c> calls with the SAME token.
        /// The atomic <c>ExecuteUpdateAsync</c> claim guarantees exactly ONE caller
        /// gets a new token; the second gets <c>null</c>.
        ///
        /// DB invariant verified: the original token is rotated at most once —
        /// no second active descendant can exist with the SAME parent token unless
        /// two separate <c>ExecuteUpdateAsync</c> claims both returned 1, which is
        /// the bug being fixed.  The winning caller's child token (if not revoked
        /// by a subsequent reuse-detection sweep) will be the only rotation product.
        ///
        /// Note: because SQLite in-memory is effectively single-threaded, one of the
        /// two tasks will always complete its read before the other, meaning the
        /// "losing" task may see the token already revoked and trigger reuse-detection
        /// (which also revokes the child).  The essential invariant is therefore:
        ///   1. Exactly ONE caller receives a non-null Token.
        ///   2. The original token appears exactly ONCE in the DB (no phantom duplicate).
        ///   3. At most ONE descendant token exists (≤1, not exactly 1).
        /// </summary>
        [TestMethod]
        public async Task RefreshToken_ConcurrentDuplicateCalls_OnlyOneSucceeds()
        {
            var tokenStr = await SeedActiveTokenAsync("concurrent_user");

            // Fire both calls simultaneously (best-effort concurrency for SQLite).
            var task1 = _service.RefreshTokenAsync(tokenStr, "ip-1");
            var task2 = _service.RefreshTokenAsync(tokenStr, "ip-2");
            var results = await Task.WhenAll(task1, task2);

            var successes = results.Where(r => r != null).ToList();
            var failures  = results.Where(r => r == null).ToList();

            // Core guarantee: exactly one caller wins the atomic claim.
            successes.Should().HaveCount(1,
                "exactly one concurrent caller must win the atomic ExecuteUpdateAsync claim (issue #118); " +
                "if both return non-null, the race condition is still present");
            failures.Should().HaveCount(1,
                "the other concurrent caller must get null, not a second rotated token");

            // DB invariant: the original token is in the DB exactly once (no phantom row).
            using var db = CreateDb();
            var allTokens = await ((DbContext)db).Set<RefreshTokenEntity>().ToListAsync();
            var originalRows = allTokens.Where(t => t.Token == tokenStr).ToList();
            originalRows.Should().HaveCount(1,
                "the original token must appear exactly once; a second row would indicate a phantom write");

            // The original token must be revoked (one winner claimed it).
            originalRows[0].IsActive.Should().BeFalse(
                "the original token must be revoked after a successful rotation");

            // At most one descendant token should exist (≤1 because the losing task
            // may trigger reuse-detection and revoke the child on SQLite's serial execution).
            var descendantCount = allTokens.Count(t => t.Token != tokenStr);
            descendantCount.Should().BeLessOrEqualTo(1,
                "at most one new refresh token must be created; two would mean both concurrent callers " +
                "bypassed the atomic claim and each issued a token (the original issue #118 bug)");
        }

        /// <summary>
        /// Sequential double-rotation: after the first call the original token is revoked;
        /// a second call presenting the same (now-revoked) token MUST return null.
        ///
        /// This is the deterministic equivalent of the concurrency test.  It asserts the
        /// core atomic-claim guarantee: an already-claimed (revoked+replaced) token cannot
        /// be claimed a second time.  The reuse-detection logic also kicks in and revokes
        /// the child chain, which is the correct security response.
        /// </summary>
        [TestMethod]
        public async Task RefreshToken_SequentialDoubleRotation_SecondCallReturnsNull()
        {
            var tokenStr = await SeedActiveTokenAsync("sequential_user");

            var first = await _service.RefreshTokenAsync(tokenStr, "ip-first");
            first.Should().NotBeNull("first rotation must succeed");

            // Simulate attacker or network-retry presenting the same old token.
            var second = await _service.RefreshTokenAsync(tokenStr, "ip-second");
            second.Should().BeNull("second rotation of the same token must fail (atomic claim prevents double-rotation)");

            // DB invariant: the original token appears exactly once (not duplicated).
            using var db = CreateDb();
            var allTokens = await ((DbContext)db).Set<RefreshTokenEntity>().ToListAsync();
            allTokens.Count(t => t.Token == tokenStr).Should().Be(1,
                "the original token row must not be duplicated");

            // The second call presenting a revoked token triggers reuse-detection and
            // revokes the child chain — no extra active token should remain.
            // (This is the correct security response, not a bug.)
            var activeDescendants = allTokens.Where(t => t.Token != tokenStr && t.IsActive).ToList();
            activeDescendants.Should().BeEmpty(
                "after reuse-detection on the second call, the child chain must also be revoked; " +
                "0 active descendants is the expected secure outcome");
        }
    }

    /// <summary>
    /// Minimal FrameworkContext for RefreshToken rotation tests.
    /// Overrides OnModelCreating to skip the global Utils.GetAllModels() scan
    /// (avoids column-name conflicts from other test entities).
    /// </summary>
    internal class TokenRotationTestDbContext : FrameworkContext
    {
        public TokenRotationTestDbContext(string seed)
            : base($"DataSource={seed}?mode=memory&cache=shared", DBTypeEnum.SQLite) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Intentionally do NOT call base.OnModelCreating() to avoid
            // Utils.GetAllModels() scanning the entire test assembly and causing
            // 'duplicate column name' conflicts in SQLite.
            // FrameworkContext's DbSet<RefreshTokenEntity> is sufficient for EnsureCreated().
        }
    }
}
