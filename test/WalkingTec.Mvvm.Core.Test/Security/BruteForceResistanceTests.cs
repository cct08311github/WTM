using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Security
{
    /// <summary>
    /// Verifies BCrypt work-factor properties that make offline brute-force attacks expensive.
    /// These tests assert measurable computational cost and salt diversity —
    /// properties that MD5 lacks and which motivated the v8.1.13 security upgrade.
    /// </summary>
    [TestClass]
    public class BruteForceResistanceTests
    {
        // ─── Work-Factor Timing ─────────────────────────────────────────────

        [TestMethod]
        public void HashPassword_BCrypt_TakesNonTrivialTime()
        {
            // Hash 5 passwords and assert total wall-clock time > 5ms (conservative).
            // BCrypt-SHA256 with 100 000 iterations typically takes 20–200ms per hash.
            // This guards against accidentally switching to a fast (insecure) algorithm.
            const int iterations = 5;
            const int minExpectedMs = 5;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
                PasswordHashHelper.HashPassword($"password{i}");
            sw.Stop();

            sw.ElapsedMilliseconds.Should().BeGreaterThan(minExpectedMs,
                $"BCrypt hashing {iterations} passwords must take at least {minExpectedMs}ms total; " +
                $"a trivially fast result indicates a weak algorithm");
        }

        [TestMethod]
        public void VerifyPassword_CorrectBCrypt_TakesNonTrivialTime()
        {
            var hash = PasswordHashHelper.HashPassword("benchmark_password");
            const int iterations = 5;
            const int minExpectedMs = 5;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
                PasswordHashHelper.VerifyPassword(hash, "benchmark_password");
            sw.Stop();

            sw.ElapsedMilliseconds.Should().BeGreaterThan(minExpectedMs,
                "verification must re-derive the key and is equally expensive to hashing");
        }

        // ─── Salt Diversity ─────────────────────────────────────────────────

        [TestMethod]
        public void HashPassword_100Calls_AllDistinct()
        {
            const int count = 100;
            var hashes = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < count; i++)
                hashes.Add(PasswordHashHelper.HashPassword("same_password"));

            hashes.Should().HaveCount(count,
                "every hash call must use a unique random salt, producing 100 unique hashes");
        }

        [TestMethod]
        public void HashPassword_SaltedOutput_MeansNoTwoHashesAreEqual()
        {
            var h1 = PasswordHashHelper.HashPassword("p@ssw0rd");
            var h2 = PasswordHashHelper.HashPassword("p@ssw0rd");

            h1.Should().NotBe(h2, "unique salt per hash prevents precomputed table attacks");
        }

        // ─── MD5 vs BCrypt Security Gap ─────────────────────────────────────

        [TestMethod]
        public void MD5Hash_IsDeterministic_DemonstrationOfWeakness()
        {
            // This test documents the security gap that BCrypt migration closes.
            // A deterministic hash means identical passwords share identical hashes —
            // enabling precomputed (rainbow-table) attacks against the full user table.
            var hash1 = PasswordHashHelper.ComputeMD5("admin");
            var hash2 = PasswordHashHelper.ComputeMD5("admin");

            hash1.Should().Be(hash2,
                "MD5 is deterministic — this is a known weakness that BCrypt migration addresses");
        }

        [TestMethod]
        public void BCryptHash_IsNonDeterministic_SaltPreventsRainbowTables()
        {
            var hash1 = PasswordHashHelper.HashPassword("admin");
            var hash2 = PasswordHashHelper.HashPassword("admin");

            hash1.Should().NotBe(hash2,
                "BCrypt with random salt is non-deterministic — same password, different stored value");
        }

        // ─── Verification Still Works After Distinct Salts ──────────────────

        [TestMethod]
        public void VerifyPassword_AllHashes_FromSamePassword_VerifyCorrectly()
        {
            const string password = "consistent_pass";
            var hashes = Enumerable.Range(0, 10)
                .Select(_ => PasswordHashHelper.HashPassword(password))
                .ToList();

            foreach (var hash in hashes)
            {
                PasswordHashHelper.VerifyPassword(hash, password)
                    .Should().Be(PasswordVerifyResult.Success,
                        "every salted hash must still verify against the original password");
            }
        }
    }
}
