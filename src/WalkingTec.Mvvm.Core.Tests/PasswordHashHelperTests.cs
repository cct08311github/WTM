using FluentAssertions;
using WalkingTec.Mvvm.Core;
using Xunit;

namespace WalkingTec.Mvvm.Core.Tests
{
    public class PasswordHashHelperTests
    {
        [Fact]
        public void HashPassword_ReturnsNonEmptyString()
        {
            var hash = PasswordHashHelper.HashPassword("test123");
            hash.Should().NotBeNullOrEmpty();
            hash.Should().NotBe("test123");
        }

        [Fact]
        public void HashPassword_EmptyInput_ReturnsEmpty()
        {
            PasswordHashHelper.HashPassword("").Should().BeEmpty();
            PasswordHashHelper.HashPassword(null).Should().BeEmpty();
        }

        [Fact]
        public void HashPassword_DifferentCalls_ProduceDifferentHashes()
        {
            var hash1 = PasswordHashHelper.HashPassword("test123");
            var hash2 = PasswordHashHelper.HashPassword("test123");
            hash1.Should().NotBe(hash2, "PBKDF2 uses random salt");
        }

        [Fact]
        public void VerifyPassword_CorrectPassword_ReturnsSuccess()
        {
            var hash = PasswordHashHelper.HashPassword("myPassword");
            var result = PasswordHashHelper.VerifyPassword(hash, "myPassword");
            result.Should().Be(PasswordVerifyResult.Success);
        }

        [Fact]
        public void VerifyPassword_WrongPassword_ReturnsFailed()
        {
            var hash = PasswordHashHelper.HashPassword("myPassword");
            var result = PasswordHashHelper.VerifyPassword(hash, "wrongPassword");
            result.Should().Be(PasswordVerifyResult.Failed);
        }

        [Fact]
        public void VerifyPassword_NullInputs_ReturnsFailed()
        {
            PasswordHashHelper.VerifyPassword(null, "pw")
                .Should().Be(PasswordVerifyResult.Failed);
            PasswordHashHelper.VerifyPassword("hash", null)
                .Should().Be(PasswordVerifyResult.Failed);
            PasswordHashHelper.VerifyPassword(null, null)
                .Should().Be(PasswordVerifyResult.Failed);
        }

        [Fact]
        public void VerifyPassword_LegacyMD5_ReturnsSuccessRehashNeeded()
        {
            var md5Hash = PasswordHashHelper.ComputeMD5("000000");
            var result = PasswordHashHelper.VerifyPassword(md5Hash, "000000");
            result.Should().Be(PasswordVerifyResult.SuccessRehashNeeded);
        }

        [Fact]
        public void VerifyPassword_LegacyMD5_WrongPassword_ReturnsFailed()
        {
            var md5Hash = PasswordHashHelper.ComputeMD5("000000");
            var result = PasswordHashHelper.VerifyPassword(md5Hash, "111111");
            result.Should().Be(PasswordVerifyResult.Failed);
        }

        [Fact]
        public void IsLegacyMD5Hash_ValidMD5_ReturnsTrue()
        {
            PasswordHashHelper.IsLegacyMD5Hash("670B14728AD9902AECBA32E22FA4F6BD")
                .Should().BeTrue();
        }

        [Fact]
        public void IsLegacyMD5Hash_PBKDF2Hash_ReturnsFalse()
        {
            var pbkdf2 = PasswordHashHelper.HashPassword("test");
            PasswordHashHelper.IsLegacyMD5Hash(pbkdf2).Should().BeFalse();
        }

        [Fact]
        public void IsLegacyMD5Hash_NullOrEmpty_ReturnsFalse()
        {
            PasswordHashHelper.IsLegacyMD5Hash(null).Should().BeFalse();
            PasswordHashHelper.IsLegacyMD5Hash("").Should().BeFalse();
        }

        [Fact]
        public void IsLegacyMD5Hash_WrongLength_ReturnsFalse()
        {
            PasswordHashHelper.IsLegacyMD5Hash("ABC123").Should().BeFalse();
        }

        [Fact]
        public void MigrationFlow_MD5ToNewHash_Verify_Success()
        {
            // Simulate: user had MD5 password, logs in, system upgrades
            var legacyMD5 = PasswordHashHelper.ComputeMD5("secretPassword");

            // Step 1: Verify against legacy hash
            var verifyResult = PasswordHashHelper.VerifyPassword(
                legacyMD5, "secretPassword");
            verifyResult.Should().Be(PasswordVerifyResult.SuccessRehashNeeded);

            // Step 2: Generate new PBKDF2 hash
            var newHash = PasswordHashHelper.HashPassword("secretPassword");

            // Step 3: Verify against new hash
            var verifyNew = PasswordHashHelper.VerifyPassword(
                newHash, "secretPassword");
            verifyNew.Should().Be(PasswordVerifyResult.Success);
        }
    }
}
