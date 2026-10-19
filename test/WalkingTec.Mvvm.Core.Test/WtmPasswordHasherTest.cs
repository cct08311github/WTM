using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// A concrete user type, because <see cref="FrameworkUserBase"/> is abstract.
    /// </summary>
    public class LoginTestUser : FrameworkUserBase
    {
    }

    public class LoginTestContext : FrameworkContext
    {
        public LoginTestContext(string seed) : base(seed, DBTypeEnum.Memory)
        {
        }

        public DbSet<LoginTestUser> LoginTestUsers { get; set; }
    }

    [TestClass]
    public class WtmPasswordHasherTest
    {
        private static WTMContext CreateContext(PasswordOption options)
        {
            var wtm = MockWtmContext.CreateWtmContext(new LoginTestContext(Guid.NewGuid().ToString("N")), "admin");
            wtm.GlobaInfo.CustomUserType = typeof(LoginTestUser);
            if (options != null)
            {
                wtm.ConfigInfo.PasswordOptions = options;
            }
            return wtm;
        }

        [TestMethod]
        public void DefaultContextHashesLikeGetMD5String()
        {
            var wtm = CreateContext(null);
            Assert.AreEqual(PasswordAlgorithm.Md5, wtm.PasswordHasher.Algorithm);
            Assert.IsFalse(wtm.PasswordHasher.FellBackToMd5);
            foreach (var password in new[] { "000000", "", "密碼", null })
            {
                Assert.AreEqual(Utils.GetMD5String(password), wtm.HashPassword(password));
            }
            Assert.AreEqual(PasswordVerifyResult.Success, wtm.VerifyPassword(Utils.GetMD5String("a"), "a"));
            Assert.AreEqual(PasswordVerifyResult.Failed, wtm.VerifyPassword(Utils.GetMD5String("a"), "b"));
        }

        [TestMethod]
        public void Pbkdf2ConfiguredFallsBackToMd5WhileColumnHolds32Characters()
        {
            var wtm = CreateContext(new PasswordOption { Algorithm = PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Iterations = 1000 });
            Assert.AreEqual(PasswordAlgorithm.Pbkdf2Sha256, wtm.PasswordHasher.ConfiguredAlgorithm);
            Assert.AreEqual(PasswordAlgorithm.Md5, wtm.PasswordHasher.Algorithm);
            Assert.IsTrue(wtm.PasswordHasher.FellBackToMd5);
            Assert.AreEqual(Utils.GetMD5String("abc"), wtm.HashPassword("abc"));
            Assert.AreEqual(PasswordVerifyResult.Success, wtm.VerifyPassword(Utils.GetMD5String("abc"), "abc"), "no rehash into a column that is too short");
        }

        [TestMethod]
        public void PasswordHasherCanBeReplaced()
        {
            var wtm = CreateContext(null);
            var hasher = new PasswordHasher(new PasswordOption { Algorithm = PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Iterations = 1000 });
            wtm.PasswordHasher = hasher;
            StringAssert.StartsWith(wtm.HashPassword("abc"), "$pbkdf2-sha256$1000$");
        }
    }
}
