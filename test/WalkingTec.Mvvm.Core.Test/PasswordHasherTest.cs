using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class PasswordHasherTest
    {
        private class CapturingLogger : ILogger
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = new List<(LogLevel, string)>();
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }

        // few iterations keep the tests fast; the format and logic do not depend on the count
        private static PasswordHasher Pbkdf2Hasher(int iterations = 1000, int? maxLength = null, ILogger logger = null)
        {
            return new PasswordHasher(new PasswordOption { Algorithm = PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Iterations = iterations }, maxLength, logger);
        }

        [TestMethod]
        public void DefaultOptionsUseMd5()
        {
            var options = new PasswordOption();
            Assert.AreEqual(PasswordAlgorithm.Md5, options.Algorithm);
            Assert.AreEqual(210000, options.Pbkdf2Iterations);
            Assert.AreEqual(PasswordAlgorithm.Md5, new Configs().PasswordOptions.Algorithm);
            Assert.AreEqual(PasswordAlgorithm.Md5, PasswordHasher.Default.Algorithm);
        }

        [DataTestMethod]
        [DataRow("000000")]
        [DataRow("password")]
        [DataRow("")]
        [DataRow(" ")]
        [DataRow("密碼測試123")]
        [DataRow("p@ss$word$with$dollars")]
        [DataRow(null)]
        public void Md5HashEqualsGetMD5String(string password)
        {
            var hasher = new PasswordHasher(new PasswordOption());
            Assert.AreEqual(Utils.GetMD5String(password), hasher.Hash(password));
            Assert.AreEqual(Utils.GetMD5String(password), PasswordHasher.Default.Hash(password));
        }

        [TestMethod]
        public void KnownMd5Value()
        {
            Assert.AreEqual("670B14728AD9902AECBA32E22FA4F6BD", PasswordHasher.Default.Hash("000000"));
        }

        [TestMethod]
        public void VerifyLegacyMd5()
        {
            var hasher = PasswordHasher.Default;
            var stored = Utils.GetMD5String("secret");
            Assert.AreEqual(PasswordVerifyResult.Success, hasher.Verify(stored, "secret"));
            Assert.AreEqual(PasswordVerifyResult.Success, hasher.Verify(stored.ToLowerInvariant(), "secret"));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(stored, "Secret"));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(stored.ToLowerInvariant(), "wrong"));
            Assert.AreEqual(PasswordVerifyResult.Success, hasher.Verify(Utils.GetMD5String("密碼"), "密碼"));
        }

        [TestMethod]
        public void VerifyRejectsMissingOrUnknownValues()
        {
            var hasher = PasswordHasher.Default;
            var stored = Utils.GetMD5String("secret");
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(null, "secret"));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify("", "secret"));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(stored, null));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify("secret", "secret"), "plain text is not a recognised format");
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(stored.Substring(1), "secret"), "31 characters");
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(stored + "0", "secret"), "33 characters");
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify("G" + stored.Substring(1), "secret"), "not hex");
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify("$bcrypt$10$abc$def", "secret"));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify("$pbkdf2-sha256$", "secret"));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify("$pbkdf2-sha256$abc$AAAA$BBBB", "secret"));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify("$pbkdf2-sha256$0$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "secret"));
        }

        [TestMethod]
        public void Pbkdf2RoundTrip()
        {
            var hasher = Pbkdf2Hasher();
            Assert.AreEqual(PasswordAlgorithm.Pbkdf2Sha256, hasher.Algorithm);
            var stored = hasher.Hash("密碼 p@ss");
            StringAssert.StartsWith(stored, "$pbkdf2-sha256$1000$");
            Assert.AreEqual(4, stored.Split('$').Length - 1);
            Assert.AreEqual(PasswordHasher.Pbkdf2OutputLength(1000), stored.Length);
            Assert.AreEqual(PasswordVerifyResult.Success, hasher.Verify(stored, "密碼 p@ss"));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(stored, "密碼 p@s"));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(stored, ""));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(stored, null));
        }

        [TestMethod]
        public void Pbkdf2HashesAreSalted()
        {
            var hasher = Pbkdf2Hasher();
            Assert.AreNotEqual(hasher.Hash("same"), hasher.Hash("same"));
        }

        [TestMethod]
        public void Pbkdf2OutputLengthWithDefaultIterations()
        {
            var hasher = new PasswordHasher(new PasswordOption { Algorithm = PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Iterations = 0 });
            Assert.AreEqual(PasswordOption.DefaultPbkdf2Iterations, hasher.Iterations, "a value below 1 falls back to the default");
            Assert.AreEqual(91, PasswordHasher.Pbkdf2OutputLength(210000));
        }

        [TestMethod]
        public void TamperedPbkdf2IsRejected()
        {
            var hasher = Pbkdf2Hasher();
            var stored = hasher.Hash("secret");
            var parts = stored.Split('$'); // "", "pbkdf2-sha256", iterations, salt, hash
            string Rebuild(Action<string[]> change)
            {
                var p = (string[])parts.Clone();
                change(p);
                return string.Join("$", p);
            }
            // flip a hash character
            var hash = parts[4].ToCharArray();
            hash[0] = hash[0] == 'A' ? 'B' : 'A';
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(Rebuild(p => p[4] = new string(hash)), "secret"));
            // different salt
            var salt = parts[3].ToCharArray();
            salt[0] = salt[0] == 'A' ? 'B' : 'A';
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(Rebuild(p => p[3] = new string(salt)), "secret"));
            // different iteration count
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(Rebuild(p => p[2] = "1001"), "secret"));
            // malformed pieces
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(Rebuild(p => p[3] = "!!!"), "secret"));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(Rebuild(p => p[4] = ""), "secret"));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(stored + "$extra", "secret"));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(Rebuild(p => p[2] = "99999999999"), "secret"));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(Rebuild(p => p[2] = "-5"), "secret"));
            // untouched copy still works
            Assert.AreEqual(PasswordVerifyResult.Success, hasher.Verify(Rebuild(p => { }), "secret"));
        }

        [TestMethod]
        public void RehashNeededWhenMd5StoredAndPbkdf2Configured()
        {
            var hasher = Pbkdf2Hasher();
            Assert.AreEqual(PasswordVerifyResult.SuccessRehashNeeded, hasher.Verify(Utils.GetMD5String("secret"), "secret"));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(Utils.GetMD5String("secret"), "other"));
        }

        [TestMethod]
        public void RehashNeededWhenIterationsAreLowerThanConfigured()
        {
            var old = Pbkdf2Hasher(1000).Hash("secret");
            Assert.AreEqual(PasswordVerifyResult.SuccessRehashNeeded, Pbkdf2Hasher(2000).Verify(old, "secret"));
            Assert.AreEqual(PasswordVerifyResult.Success, Pbkdf2Hasher(1000).Verify(old, "secret"));
            Assert.AreEqual(PasswordVerifyResult.Success, Pbkdf2Hasher(500).Verify(old, "secret"), "more iterations than configured is fine");
            Assert.AreEqual(PasswordVerifyResult.Failed, Pbkdf2Hasher(2000).Verify(old, "wrong"));
        }

        [TestMethod]
        public void Md5ConfiguredNeverAsksForRehash()
        {
            var hasher = PasswordHasher.Default;
            Assert.AreEqual(PasswordVerifyResult.Success, hasher.Verify(Utils.GetMD5String("secret"), "secret"));
            var pbkdf2 = Pbkdf2Hasher().Hash("secret");
            Assert.AreEqual(PasswordVerifyResult.Success, hasher.Verify(pbkdf2, "secret"), "existing pbkdf2 hashes stay valid, no downgrade");
        }

        [TestMethod]
        public void GuardFallsBackToMd5WhenColumnIsTooShort()
        {
            var logger = new CapturingLogger();
            var hasher = Pbkdf2Hasher(210000, 32, logger);
            Assert.AreEqual(PasswordAlgorithm.Pbkdf2Sha256, hasher.ConfiguredAlgorithm);
            Assert.AreEqual(PasswordAlgorithm.Md5, hasher.Algorithm);
            Assert.IsTrue(hasher.FellBackToMd5);
            Assert.AreEqual(Utils.GetMD5String("secret"), hasher.Hash("secret"));
            Assert.AreEqual(Utils.GetMD5String("secret"), hasher.Hash("secret"));
            Assert.AreEqual(PasswordVerifyResult.Success, hasher.Verify(Utils.GetMD5String("secret"), "secret"), "no rehash can be requested");
            Assert.AreEqual(1, logger.Entries.Count(x => x.Level == LogLevel.Warning), "one Warning, logged once");
        }

        [TestMethod]
        public void GuardAllowsPbkdf2WhenColumnIsLongEnough()
        {
            var logger = new CapturingLogger();
            var needed = PasswordHasher.Pbkdf2OutputLength(1000);
            var exact = Pbkdf2Hasher(1000, needed, logger);
            Assert.AreEqual(PasswordAlgorithm.Pbkdf2Sha256, exact.Algorithm);
            Assert.AreEqual(needed, exact.Hash("x").Length);
            Assert.AreEqual(PasswordAlgorithm.Md5, Pbkdf2Hasher(1000, needed - 1).Algorithm);
            Assert.AreEqual(PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Hasher(1000, 255).Algorithm);
            Assert.AreEqual(PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Hasher(1000, null).Algorithm, "unknown or unlimited length");
            Assert.AreEqual(0, logger.Entries.Count);
        }

        [TestMethod]
        public void GuardUsesMoreDigitsOfLargeIterationCounts()
        {
            Assert.IsTrue(PasswordHasher.Pbkdf2OutputLength(1000000) > PasswordHasher.Pbkdf2OutputLength(100000));
            Assert.AreEqual(PasswordAlgorithm.Md5, Pbkdf2Hasher(1000000, PasswordHasher.Pbkdf2OutputLength(100000)).Algorithm);
        }

        [TestMethod]
        public void PasswordMaxLengthIsReadFromTheModel()
        {
            Assert.AreEqual(32, PasswordHasher.GetPasswordMaxLength(typeof(FrameworkUserBase), null));
            Assert.AreEqual(32, PasswordHasher.GetPasswordMaxLength(null, null));
            Assert.AreEqual(32, PasswordHasher.GetPasswordMaxLength(typeof(LoginTestUser), null));
            using (var dc = new LoginTestContext(Guid.NewGuid().ToString("N")))
            {
                Assert.AreEqual(32, PasswordHasher.GetPasswordMaxLength(typeof(LoginTestUser), dc));
            }
        }
    }
}
