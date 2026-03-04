using System;
using FluentAssertions;
using WalkingTec.Mvvm.Core;
using Xunit;

namespace WalkingTec.Mvvm.Core.Tests.Security
{
    /// <summary>
    /// Security property tests for the MD5 → PBKDF2 migration path.
    /// Verifies guarantees that prevent stored-hash downgrade and
    /// rainbow-table/precomputation attacks.
    /// </summary>
    public class PasswordMigrationFlowTests
    {
        // ─── Post-Migration Hash Properties ────────────────────────────────────

        [Fact]
        public void Migrate_MD5ToNewHash_StoredHashIsNolongerMD5()
        {
            // Simulate migration: detect legacy hash, then replace with PBKDF2
            var legacyHash = PasswordHashHelper.ComputeMD5("secret");
            PasswordHashHelper.IsLegacyMD5Hash(legacyHash).Should().BeTrue();

            var newHash = PasswordHashHelper.HashPassword("secret");
            PasswordHashHelper.IsLegacyMD5Hash(newHash).Should().BeFalse(
                "after migration the stored value is PBKDF2, not MD5");
        }

        [Fact]
        public void Migrate_MD5ToNewHash_NewHashVerifiesSuccessWithoutRehash()
        {
            var newHash = PasswordHashHelper.HashPassword("pass@word1");
            PasswordHashHelper.VerifyPassword(newHash, "pass@word1")
                .Should().Be(PasswordVerifyResult.Success,
                    "migrated PBKDF2 hash must not require another rehash");
        }

        [Fact]
        public void Migrate_MD5ToNewHash_LegacyHashIsReplacedNotAppended()
        {
            // The new hash must be longer than the 32-char MD5
            var legacyHash = PasswordHashHelper.ComputeMD5("password");
            var newHash = PasswordHashHelper.HashPassword("password");

            newHash.Length.Should().BeGreaterThan(legacyHash.Length,
                "PBKDF2 Base64 encoding is longer than 32-char MD5 hex");
            newHash.Should().NotContain(legacyHash,
                "new hash must not embed the old MD5 value");
        }

        // ─── Salt Uniqueness (Rainbow-Table Resistance) ─────────────────────

        [Fact]
        public void TwoUsers_SamePassword_GetDistinctHashes()
        {
            var hash1 = PasswordHashHelper.HashPassword("commonpass");
            var hash2 = PasswordHashHelper.HashPassword("commonpass");

            hash1.Should().NotBe(hash2,
                "each hash has a unique salt, so identical passwords produce different stored values");
        }

        [Fact]
        public void MD5_IsNotSalted_SamePasswordProducesSameHash()
        {
            // Demonstrates WHY migration is necessary: MD5 is deterministic
            var h1 = PasswordHashHelper.ComputeMD5("000000");
            var h2 = PasswordHashHelper.ComputeMD5("000000");
            h1.Should().Be(h2,
                "MD5 has no salt — same input always produces same hash (rainbow-table risk)");
        }

        [Fact]
        public void VerifyPassword_MD5Hash_WrongPassword_NeverSucceeds()
        {
            var md5Hash = PasswordHashHelper.ComputeMD5("correct");
            PasswordHashHelper.VerifyPassword(md5Hash, "incorrect")
                .Should().Be(PasswordVerifyResult.Failed,
                    "migrating the hash of wrong credentials must never succeed");
        }

        // ─── Cross-User Hash Isolation ──────────────────────────────────────

        [Fact]
        public void UserA_Hash_CannotVerify_UserB_Password()
        {
            var hashA = PasswordHashHelper.HashPassword("passwordA");

            PasswordHashHelper.VerifyPassword(hashA, "passwordB")
                .Should().Be(PasswordVerifyResult.Failed,
                    "hash computed from one password must not verify a different password");
        }

        [Fact]
        public void LegacyMD5_CannotVerify_AsDifferentUser()
        {
            // Even if attacker knows Alice's MD5 hash, it cannot be used to authenticate as Bob
            var aliceMd5 = PasswordHashHelper.ComputeMD5("alice_password");

            PasswordHashHelper.VerifyPassword(aliceMd5, "bob_password")
                .Should().Be(PasswordVerifyResult.Failed);
        }
    }
}
