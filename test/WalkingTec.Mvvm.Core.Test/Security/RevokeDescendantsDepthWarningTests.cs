#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc.Auth;

namespace WalkingTec.Mvvm.Core.Test.Security
{
    /// <summary>
    /// Tests for L18: <c>TokenService.RevokeDescendantsAsync</c> truncation warning.
    /// <para>
    /// Before the fix, silently returned after exhausting 50 hops; an active leaf token
    /// beyond depth 50 was never revoked and there was no observable signal.
    /// After the fix, a <see cref="LogLevel.Warning"/> is emitted via the injected
    /// <see cref="ILogger"/> when the chain exceeds <c>maxDepth</c>.
    /// </para>
    /// </summary>
    [TestClass]
    public class RevokeDescendantsDepthWarningTests
    {
        // ── helpers ──────────────────────────────────────────────────────────

        private sealed class CapturingLogger : ILogger
        {
            public readonly List<(LogLevel Level, string Message)> Records = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                Records.Add((logLevel, formatter(state, exception)));
            }
        }

        private sealed class RefreshTokenDb : DbContext
        {
            public DbSet<RefreshTokenEntity> Tokens => Set<RefreshTokenEntity>();

            public RefreshTokenDb(DbContextOptions opts) : base(opts) { }

            protected override void OnModelCreating(ModelBuilder mb)
            {
                mb.Entity<RefreshTokenEntity>(e =>
                {
                    e.HasKey(t => t.ID);
                    e.Ignore(t => t.IsActive);
                    e.Ignore(t => t.IsExpired);
                    e.Ignore(t => t.IsRevoked);
                });
            }
        }

        private static RefreshTokenDb CreateDb()
        {
            var opts = new DbContextOptionsBuilder<RefreshTokenDb>()
                .UseInMemoryDatabase("revoketest-" + Guid.NewGuid().ToString("N"))
                .Options;
            return new RefreshTokenDb(opts);
        }

        /// <summary>
        /// Invokes the private static <c>RevokeDescendantsAsync</c> via reflection.
        /// Unwraps <see cref="TargetInvocationException"/> as per WTM test conventions.
        /// </summary>
        private static async Task InvokeRevokeDescendants(
            DbSet<RefreshTokenEntity> dbSet,
            RefreshTokenEntity token,
            string ipAddress,
            string reason,
            TimeProvider timeProvider,
            ILogger? logger)
        {
            var method = typeof(TokenService).GetMethod(
                "RevokeDescendantsAsync",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("RevokeDescendantsAsync not found via reflection.");

            object? taskObj = method.Invoke(null, new object?[]
            {
                dbSet, token, ipAddress, reason, timeProvider, logger
            });

            if (taskObj is Task t)
                await t;
        }

        // ── tests ─────────────────────────────────────────────────────────────

        /// <summary>
        /// L18: When the revocation chain is deeper than 50, a Warning must be logged.
        /// Builds a linear chain of 52 already-revoked tokens followed by one still-active leaf.
        /// The loop exhausts maxDepth at 50 and must emit a warning.
        /// </summary>
        [TestMethod]
        public async Task RevokeDescendants_ChainExceedsMaxDepth_LogsWarning()
        {
            using var db = CreateDb();
            const int chainLength = 52; // > maxDepth (50)

            // Build a linear chain: tokens[0] → tokens[1] → … → tokens[chainLength-1]
            // All are already revoked (simulating a stale chain), so the loop keeps walking.
            var tokens = new RefreshTokenEntity[chainLength];
            for (int i = 0; i < chainLength; i++)
            {
                tokens[i] = new RefreshTokenEntity
                {
                    Token = "tok" + i,
                    ITCode = "user1",
                    ExpiresUtc = DateTime.UtcNow.AddDays(7),
                    // Mark all as revoked so the loop never finds an active node and keeps going.
                    RevokedUtc = DateTime.UtcNow.AddMinutes(-1),
                    ReplacedByToken = i + 1 < chainLength ? "tok" + (i + 1) : null
                };
                db.Tokens.Add(tokens[i]);
            }
            await db.SaveChangesAsync();

            var logger = new CapturingLogger();

            // Act — token[0] is the "compromised root"; the loop should exhaust at 50.
            await InvokeRevokeDescendants(
                db.Tokens, tokens[0],
                "192.0.2.1", "test reason",
                TimeProvider.System, logger);

            // Assert: at least one Warning was logged about truncation.
            var warnings = logger.Records
                .Where(r => r.Level == LogLevel.Warning)
                .ToList();
            Assert.IsTrue(warnings.Count > 0,
                "A LogLevel.Warning must be emitted when the revocation chain exceeds maxDepth.");
            Assert.IsTrue(
                warnings.Any(w => w.Message.Contains("maxDepth") || w.Message.Contains("50") || w.Message.Contains("revocation chain")),
                $"Warning message should reference the depth limit. Got: {string.Join("; ", warnings.Select(w => w.Message))}");
        }

        /// <summary>
        /// Happy path: a short chain (fewer than 50 hops) with one active leaf
        /// terminates normally — no warning is logged.
        /// </summary>
        [TestMethod]
        public async Task RevokeDescendants_ShortChain_DoesNotLogWarning()
        {
            using var db = CreateDb();

            // Root (already revoked) → child (active)
            var root = new RefreshTokenEntity
            {
                Token = "root-tok",
                ITCode = "user1",
                ExpiresUtc = DateTime.UtcNow.AddDays(7),
                RevokedUtc = DateTime.UtcNow.AddMinutes(-1),
                ReplacedByToken = "child-tok"
            };
            var child = new RefreshTokenEntity
            {
                Token = "child-tok",
                ITCode = "user1",
                ExpiresUtc = DateTime.UtcNow.AddDays(7),
                // Not revoked → IsActive = true
            };
            db.Tokens.AddRange(root, child);
            await db.SaveChangesAsync();

            var logger = new CapturingLogger();

            await InvokeRevokeDescendants(
                db.Tokens, root,
                "192.0.2.1", "test reason",
                TimeProvider.System, logger);

            // No warnings expected.
            var warnings = logger.Records.Where(r => r.Level == LogLevel.Warning).ToList();
            Assert.AreEqual(0, warnings.Count,
                "No warning should be logged for a short chain that resolves normally.");

            // The active child should have been revoked.
            var revokedChild = await db.Tokens.FirstAsync(t => t.Token == "child-tok");
            Assert.IsNotNull(revokedChild.RevokedUtc,
                "The active descendant should have been revoked.");
        }
    }
}
