using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Unit
{
    [TestClass]
    public class PasswordHashHelperTests
    {
        // ─── HashPassword ──────────────────────────────────────────────────────

        [TestMethod]
        public void HashPassword_ReturnsNonEmptyString()
        {
            var hash = PasswordHashHelper.HashPassword("test123");
            hash.Should().NotBeNullOrEmpty();
            hash.Should().NotBe("test123");
        }

        [TestMethod]
        public void HashPassword_EmptyInput_ReturnsEmpty()
        {
            PasswordHashHelper.HashPassword("").Should().BeEmpty();
            PasswordHashHelper.HashPassword(null).Should().BeEmpty();
        }

        [TestMethod]
        public void HashPassword_DifferentCalls_ProduceDifferentHashes()
        {
            var hash1 = PasswordHashHelper.HashPassword("test123");
            var hash2 = PasswordHashHelper.HashPassword("test123");
            hash1.Should().NotBe(hash2, "PBKDF2 uses random salt");
        }

        [TestMethod]
        public void HashPassword_VeryLongPassword_RoundTrips()
        {
            var longPw = new string('A', 10000);
            var hash = PasswordHashHelper.HashPassword(longPw);
            PasswordHashHelper.VerifyPassword(hash, longPw)
                .Should().Be(PasswordVerifyResult.Success);
        }

        [TestMethod]
        public void HashPassword_UnicodePassword_RoundTrips()
        {
            var pw = "密碼テスト🔐Ñoño";
            var hash = PasswordHashHelper.HashPassword(pw);
            PasswordHashHelper.VerifyPassword(hash, pw)
                .Should().Be(PasswordVerifyResult.Success);
        }

        // ─── VerifyPassword ────────────────────────────────────────────────────

        [TestMethod]
        public void VerifyPassword_CorrectPassword_ReturnsSuccess()
        {
            var hash = PasswordHashHelper.HashPassword("myPassword");
            PasswordHashHelper.VerifyPassword(hash, "myPassword")
                .Should().Be(PasswordVerifyResult.Success);
        }

        [TestMethod]
        public void VerifyPassword_WrongPassword_ReturnsFailed()
        {
            var hash = PasswordHashHelper.HashPassword("myPassword");
            PasswordHashHelper.VerifyPassword(hash, "wrongPassword")
                .Should().Be(PasswordVerifyResult.Failed);
        }

        [TestMethod]
        public void VerifyPassword_NullInputs_ReturnsFailed()
        {
            PasswordHashHelper.VerifyPassword(null, "pw")
                .Should().Be(PasswordVerifyResult.Failed);
            PasswordHashHelper.VerifyPassword("hash", null)
                .Should().Be(PasswordVerifyResult.Failed);
            PasswordHashHelper.VerifyPassword(null, null)
                .Should().Be(PasswordVerifyResult.Failed);
        }

        [DataTestMethod]
        [DataRow("password123")]
        [DataRow("P@$w0rd!")]
        [DataRow("a")]
        [DataRow("   ")]
        public void VerifyPassword_VariousPasswords_RoundTrips(string password)
        {
            var hash = PasswordHashHelper.HashPassword(password);
            PasswordHashHelper.VerifyPassword(hash, password)
                .Should().Be(PasswordVerifyResult.Success);
        }

        [TestMethod]
        public void VerifyPassword_CaseSensitive()
        {
            var hash = PasswordHashHelper.HashPassword("MyPassword");
            PasswordHashHelper.VerifyPassword(hash, "mypassword")
                .Should().Be(PasswordVerifyResult.Failed);
        }

        [TestMethod]
        public void VerifyPassword_LegacyMD5_ReturnsSuccessRehashNeeded()
        {
            var md5Hash = PasswordHashHelper.ComputeMD5("000000");
            PasswordHashHelper.VerifyPassword(md5Hash, "000000")
                .Should().Be(PasswordVerifyResult.SuccessRehashNeeded);
        }

        [TestMethod]
        public void VerifyPassword_LegacyMD5_WrongPassword_ReturnsFailed()
        {
            var md5Hash = PasswordHashHelper.ComputeMD5("000000");
            PasswordHashHelper.VerifyPassword(md5Hash, "111111")
                .Should().Be(PasswordVerifyResult.Failed);
        }

        // ─── IsLegacyMD5Hash ───────────────────────────────────────────────────

        [TestMethod]
        public void IsLegacyMD5Hash_ValidMD5_ReturnsTrue()
        {
            PasswordHashHelper.IsLegacyMD5Hash("670B14728AD9902AECBA32E22FA4F6BD")
                .Should().BeTrue();
        }

        [TestMethod]
        public void IsLegacyMD5Hash_PBKDF2Hash_ReturnsFalse()
        {
            var pbkdf2 = PasswordHashHelper.HashPassword("test");
            PasswordHashHelper.IsLegacyMD5Hash(pbkdf2).Should().BeFalse();
        }

        [TestMethod]
        public void IsLegacyMD5Hash_NullOrEmpty_ReturnsFalse()
        {
            PasswordHashHelper.IsLegacyMD5Hash(null).Should().BeFalse();
            PasswordHashHelper.IsLegacyMD5Hash("").Should().BeFalse();
        }

        [TestMethod]
        public void IsLegacyMD5Hash_WrongLength_ReturnsFalse()
        {
            PasswordHashHelper.IsLegacyMD5Hash("ABC123").Should().BeFalse();
        }

        [TestMethod]
        public void IsLegacyMD5Hash_LowercaseHex_ReturnsFalse()
        {
            // WTM ComputeMD5 produces uppercase; lowercase should not match
            PasswordHashHelper.IsLegacyMD5Hash("670b14728ad9902aecba32e22fa4f6bd")
                .Should().BeFalse();
        }

        // ─── ComputeMD5 ────────────────────────────────────────────────────────

        [TestMethod]
        public void ComputeMD5_KnownValue_MatchesExpected()
        {
            PasswordHashHelper.ComputeMD5("000000")
                .Should().Be("670B14728AD9902AECBA32E22FA4F6BD");
        }

        [TestMethod]
        public void ComputeMD5_SameInput_Deterministic()
        {
            PasswordHashHelper.ComputeMD5("abc")
                .Should().Be(PasswordHashHelper.ComputeMD5("abc"));
        }

        // ─── Migration Flow ────────────────────────────────────────────────────

        [TestMethod]
        public void MigrationFlow_MD5ToNewHash_Verify_Success()
        {
            var legacyMD5 = PasswordHashHelper.ComputeMD5("secretPassword");
            PasswordHashHelper.VerifyPassword(legacyMD5, "secretPassword")
                .Should().Be(PasswordVerifyResult.SuccessRehashNeeded);

            var newHash = PasswordHashHelper.HashPassword("secretPassword");
            PasswordHashHelper.VerifyPassword(newHash, "secretPassword")
                .Should().Be(PasswordVerifyResult.Success);
        }

        [TestMethod]
        public void Migration_WrongPassword_NeverTriggered()
        {
            var md5Hash = PasswordHashHelper.ComputeMD5("correct");
            PasswordHashHelper.VerifyPassword(md5Hash, "wrong")
                .Should().Be(PasswordVerifyResult.Failed);
        }
    }
}
