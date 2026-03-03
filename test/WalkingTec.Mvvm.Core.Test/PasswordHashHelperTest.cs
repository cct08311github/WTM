using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class PasswordHashHelperTest
    {
        [TestMethod]
        public void HashPassword_ReturnsNonMd5Format()
        {
            var hash = PasswordHashHelper.HashPassword("test123");
            Assert.IsFalse(PasswordHashHelper.IsLegacyMD5Hash(hash));
            Assert.IsTrue(hash.Length > 32);
        }

        [TestMethod]
        public void VerifyPassword_WithPbkdf2_ReturnsSuccess()
        {
            var hash = PasswordHashHelper.HashPassword("mypassword");
            var result = PasswordHashHelper.VerifyPassword(hash, "mypassword");
            Assert.AreEqual(PasswordVerifyResult.Success, result);
        }

        [TestMethod]
        public void VerifyPassword_WithWrongPassword_ReturnsFailed()
        {
            var hash = PasswordHashHelper.HashPassword("mypassword");
            var result = PasswordHashHelper.VerifyPassword(hash, "wrongpassword");
            Assert.AreEqual(PasswordVerifyResult.Failed, result);
        }

        [TestMethod]
        public void VerifyPassword_WithLegacyMD5_ReturnsSuccessRehashNeeded()
        {
            // Use Utils.GetMD5String to simulate a legacy stored hash
            var md5Hash = Utils.GetMD5String("legacypassword");
            var result = PasswordHashHelper.VerifyPassword(md5Hash, "legacypassword");
            Assert.AreEqual(PasswordVerifyResult.SuccessRehashNeeded, result);
        }

        [TestMethod]
        public void VerifyPassword_WithLegacyMD5_WrongPassword_ReturnsFailed()
        {
            var md5Hash = Utils.GetMD5String("legacypassword");
            var result = PasswordHashHelper.VerifyPassword(md5Hash, "wrong");
            Assert.AreEqual(PasswordVerifyResult.Failed, result);
        }

        [TestMethod]
        public void IsLegacyMD5Hash_ValidMd5_ReturnsTrue()
        {
            var md5 = Utils.GetMD5String("test");
            Assert.IsTrue(PasswordHashHelper.IsLegacyMD5Hash(md5));
        }

        [TestMethod]
        public void IsLegacyMD5Hash_Pbkdf2Hash_ReturnsFalse()
        {
            var pbkdf2 = PasswordHashHelper.HashPassword("test");
            Assert.IsFalse(PasswordHashHelper.IsLegacyMD5Hash(pbkdf2));
        }

        [TestMethod]
        public void HashPassword_EmptyString_ReturnsEmpty()
        {
            var hash = PasswordHashHelper.HashPassword(string.Empty);
            Assert.AreEqual(string.Empty, hash);
        }
    }
}
