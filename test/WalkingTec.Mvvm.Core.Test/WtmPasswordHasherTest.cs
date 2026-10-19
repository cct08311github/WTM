using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
            //an existing database that was not migrated: the model says 128, the real column 32
            var wtm = CreateContext(new PasswordOption { Algorithm = PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Iterations = 100000 });
            wtm.PasswordColumnInspector = new FakePasswordColumnInspector(PasswordColumnInfo.Limited(32));
            Assert.AreEqual(PasswordAlgorithm.Pbkdf2Sha256, wtm.PasswordHasher.ConfiguredAlgorithm);
            Assert.AreEqual(PasswordAlgorithm.Md5, wtm.PasswordHasher.Algorithm);
            Assert.IsTrue(wtm.PasswordHasher.FellBackToMd5);
            Assert.AreEqual(Utils.GetMD5String("abc"), wtm.HashPassword("abc"));
            Assert.AreEqual(PasswordVerifyResult.Success, wtm.VerifyPassword(Utils.GetMD5String("abc"), "abc"), "no rehash into a column that is too short");
        }

        private static WTMContext CreateInspectedContext(IPasswordColumnInspector inspector, int iterations, PasswordAlgorithm algorithm = PasswordAlgorithm.Pbkdf2Sha256, CapturingLoggerFactory logs = null)
        {
            var wtm = CreateContext(new PasswordOption { Algorithm = algorithm, Pbkdf2Iterations = iterations });
            wtm.PasswordColumnInspector = inspector;
            if (logs != null)
            {
                typeof(WTMContext).GetField("_loggerFactory", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(wtm, logs);
            }
            return wtm;
        }

        [TestMethod]
        public void RealColumnLengthDecidesWhetherPbkdf2TakesEffect()
        {
            //91 characters are needed at 100000 iterations
            Assert.AreEqual(91, PasswordHasher.Pbkdf2OutputLength(100000));
            var cases = new (PasswordColumnInfo Info, PasswordAlgorithm Expected)[]
            {
                (PasswordColumnInfo.Limited(32), PasswordAlgorithm.Md5),
                (PasswordColumnInfo.Limited(90), PasswordAlgorithm.Md5),
                (PasswordColumnInfo.Limited(91), PasswordAlgorithm.Pbkdf2Sha256),
                (PasswordColumnInfo.Limited(128), PasswordAlgorithm.Pbkdf2Sha256),
                (PasswordColumnInfo.Unlimited, PasswordAlgorithm.Pbkdf2Sha256),
                (PasswordColumnInfo.Unverified("no idea"), PasswordAlgorithm.Md5),
            };
            foreach (var c in cases)
            {
                var wtm = CreateInspectedContext(new FakePasswordColumnInspector(c.Info), 100000);
                Assert.AreEqual(c.Expected, wtm.PasswordHasher.Algorithm, c.Info.Description);
                if (c.Expected == PasswordAlgorithm.Pbkdf2Sha256)
                {
                    StringAssert.StartsWith(wtm.HashPassword("abc"), "$pbkdf2-sha256$100000$");
                }
                else
                {
                    Assert.AreEqual(Utils.GetMD5String("abc"), wtm.HashPassword("abc"));
                }
            }
        }

        [TestMethod]
        public void FallbackLogsOneWarningThatNamesTheGuide()
        {
            //an iteration count no other test uses, so that this test creates (and logs for) its own hasher
            var logs = new CapturingLoggerFactory();
            var inspector = new FakePasswordColumnInspector(PasswordColumnInfo.Limited(32));
            for (var i = 0; i < 3; i++)
            {
                var wtm = CreateInspectedContext(inspector, 123457, logs: logs);
                Assert.AreEqual(PasswordAlgorithm.Md5, wtm.PasswordHasher.Algorithm);
            }
            var warning = logs.Entries.Single(x => x.Level == LogLevel.Warning);
            StringAssert.Contains(warning.Message, "docs/migrations/password-hash-upgrade.md");
            StringAssert.Contains(warning.Message, "32");
        }

        [TestMethod]
        public void InspectorFailureFallsBackToMd5WithOneWarning()
        {
            var logs = new CapturingLoggerFactory();
            var inspector = new FakePasswordColumnInspector(new InvalidOperationException("secret detail"));
            var wtm = CreateInspectedContext(inspector, 123458, logs: logs);
            Assert.AreEqual(PasswordAlgorithm.Md5, wtm.PasswordHasher.Algorithm);
            Assert.IsTrue(wtm.PasswordHasher.FellBackToMd5);
            Assert.AreEqual(Utils.GetMD5String("abc"), wtm.HashPassword("abc"));
            var warning = logs.Entries.Single(x => x.Level == LogLevel.Warning);
            StringAssert.Contains(warning.Message, "InvalidOperationException");
            Assert.IsFalse(warning.Message.Contains("secret detail"), "exception messages can carry connection details");

            var unverified = new FakePasswordColumnInspector(PasswordColumnInfo.Unverified("the catalog could not be read"));
            logs = new CapturingLoggerFactory();
            wtm = CreateInspectedContext(unverified, 123459, logs: logs);
            Assert.AreEqual(PasswordAlgorithm.Md5, wtm.PasswordHasher.Algorithm);
            StringAssert.Contains(logs.Entries.Single(x => x.Level == LogLevel.Warning).Message, "the catalog could not be read");
        }

        [TestMethod]
        public void InspectorIsNeverCalledWhileMd5IsConfigured()
        {
            var inspector = new FakePasswordColumnInspector(PasswordColumnInfo.Limited(32));
            var wtm = CreateInspectedContext(inspector, 100000, PasswordAlgorithm.Md5);
            Assert.AreEqual(PasswordAlgorithm.Md5, wtm.PasswordHasher.Algorithm);
            Assert.AreEqual(Utils.GetMD5String("abc"), wtm.HashPassword("abc"));
            Assert.AreEqual(PasswordVerifyResult.Success, wtm.VerifyPassword(Utils.GetMD5String("abc"), "abc"));
            //options that were never set at all
            var untouched = CreateContext(null);
            untouched.PasswordColumnInspector = inspector;
            Assert.AreEqual(PasswordAlgorithm.Md5, untouched.PasswordHasher.Algorithm);
            Assert.AreEqual(0, inspector.Calls);
        }

        [TestMethod]
        public void InspectorIsAskedOncePerContextWhenPbkdf2IsConfigured()
        {
            var inspector = new FakePasswordColumnInspector(PasswordColumnInfo.Limited(128));
            var wtm = CreateInspectedContext(inspector, 100000);
            wtm.HashPassword("a");
            wtm.HashPassword("b");
            wtm.VerifyPassword(Utils.GetMD5String("a"), "a");
            Assert.AreEqual(1, inspector.Calls);
        }

        [TestMethod]
        public void PasswordHasherCanBeReplaced()
        {
            var wtm = CreateContext(null);
            var hasher = new PasswordHasher(new PasswordOption { Algorithm = PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Iterations = 100000 });
            wtm.PasswordHasher = hasher;
            StringAssert.StartsWith(wtm.HashPassword("abc"), "$pbkdf2-sha256$100000$");
        }
    }
}
