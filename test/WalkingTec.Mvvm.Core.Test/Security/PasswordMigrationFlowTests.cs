using System;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Security
{
    /// <summary>
    /// Security property tests for the MD5 → BCrypt migration path.
    /// Verifies guarantees that prevent stored-hash downgrade and
    /// rainbow-table/precomputation attacks.
    /// </summary>
    [TestClass]
    public class PasswordMigrationFlowTests
    {
        // ─── Post-Migration Hash Properties ────────────────────────────────────

        [TestMethod]
        public void Migrate_MD5ToNewHash_StoredHashIsNolongerMD5()
        {
            // Simulate migration: detect legacy hash, then replace with BCrypt
            var legacyHash = PasswordHashHelper.ComputeMD5("secret");
            PasswordHashHelper.IsLegacyMD5Hash(legacyHash).Should().BeTrue();

            var newHash = PasswordHashHelper.HashPassword("secret");
            PasswordHashHelper.IsLegacyMD5Hash(newHash).Should().BeFalse(
                "after migration the stored value is BCrypt, not MD5");
        }

        [TestMethod]
        public void Migrate_MD5ToNewHash_NewHashVerifiesSuccessWithoutRehash()
        {
            var newHash = PasswordHashHelper.HashPassword("pass@word1");
            PasswordHashHelper.VerifyPassword(newHash, "pass@word1")
                .Should().Be(PasswordVerifyResult.Success,
                    "migrated BCrypt hash must not require another rehash");
        }

        [TestMethod]
        public void Migrate_MD5ToNewHash_LegacyHashIsReplacedNotAppended()
        {
            // The new hash must be longer than the 32-char MD5
            var legacyHash = PasswordHashHelper.ComputeMD5("password");
            var newHash = PasswordHashHelper.HashPassword("password");

            newHash.Length.Should().BeGreaterThan(legacyHash.Length,
                "BCrypt Base64 encoding is longer than 32-char MD5 hex");
            newHash.Should().NotContain(legacyHash,
                "new hash must not embed the old MD5 value");
        }

        // ─── Salt Uniqueness (Rainbow-Table Resistance) ─────────────────────

        [TestMethod]
        public void TwoUsers_SamePassword_GetDistinctHashes()
        {
            var hash1 = PasswordHashHelper.HashPassword("commonpass");
            var hash2 = PasswordHashHelper.HashPassword("commonpass");

            hash1.Should().NotBe(hash2,
                "each hash has a unique salt, so identical passwords produce different stored values");
        }

        [TestMethod]
        public void MD5_IsNotSalted_SamePasswordProducesSameHash()
        {
            // Demonstrates WHY migration is necessary: MD5 is deterministic
            var h1 = PasswordHashHelper.ComputeMD5("000000");
            var h2 = PasswordHashHelper.ComputeMD5("000000");
            h1.Should().Be(h2,
                "MD5 has no salt — same input always produces same hash (rainbow-table risk)");
        }

        [TestMethod]
        public void VerifyPassword_MD5Hash_WrongPassword_NeverSucceeds()
        {
            var md5Hash = PasswordHashHelper.ComputeMD5("correct");
            PasswordHashHelper.VerifyPassword(md5Hash, "incorrect")
                .Should().Be(PasswordVerifyResult.Failed,
                    "migrating the hash of wrong credentials must never succeed");
        }

        // ─── Cross-User Hash Isolation ──────────────────────────────────────

        [TestMethod]
        public void UserA_Hash_CannotVerify_UserB_Password()
        {
            var hashA = PasswordHashHelper.HashPassword("passwordA");

            PasswordHashHelper.VerifyPassword(hashA, "passwordB")
                .Should().Be(PasswordVerifyResult.Failed,
                    "hash computed from one password must not verify a different password");
        }

        [TestMethod]
        public void LegacyMD5_CannotVerify_AsDifferentUser()
        {
            // Even if attacker knows Alice's MD5 hash, it cannot be used to authenticate as Bob
            var aliceMd5 = PasswordHashHelper.ComputeMD5("alice_password");

            PasswordHashHelper.VerifyPassword(aliceMd5, "bob_password")
                .Should().Be(PasswordVerifyResult.Failed);
        }
    }
}
