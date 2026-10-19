using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Test.Mock;

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
        private static PasswordHasher Pbkdf2Hasher(int iterations = PasswordHasher.MinIterations, int? maxLength = null, ILogger logger = null)
        {
            return new PasswordHasher(new PasswordOption { Algorithm = PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Iterations = iterations }, maxLength, logger);
        }

        [TestMethod]
        public void DefaultOptionsUseMd5()
        {
            var options = new PasswordOption();
            Assert.AreEqual(PasswordAlgorithm.Md5, options.Algorithm);
            Assert.AreEqual(600000, options.Pbkdf2Iterations);
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
            StringAssert.StartsWith(stored, "$pbkdf2-sha256$100000$");
            Assert.AreEqual(4, stored.Split('$').Length - 1);
            Assert.AreEqual(PasswordHasher.Pbkdf2OutputLength(100000), stored.Length);
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
        public void DefaultIterationsRoundTrip()
        {
            var hasher = new PasswordHasher(new PasswordOption { Algorithm = PasswordAlgorithm.Pbkdf2Sha256 });
            Assert.AreEqual(600000, hasher.Iterations);
            var stored = hasher.Hash("secret");
            StringAssert.StartsWith(stored, "$pbkdf2-sha256$600000$");
            Assert.AreEqual(91, stored.Length);
            Assert.AreEqual(91, PasswordHasher.Pbkdf2OutputLength(600000));
            Assert.AreEqual(PasswordVerifyResult.Success, hasher.Verify(stored, "secret"));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(stored, "Secret"));
        }

        [TestMethod]
        public void IterationsBelowTheMinimumAreRaised()
        {
            foreach (var configured in new[] { 1000, 1, 0, -5, PasswordHasher.MinIterations - 1 })
            {
                var logger = new CapturingLogger();
                var hasher = Pbkdf2Hasher(configured, null, logger);
                Assert.AreEqual(PasswordHasher.MinIterations, hasher.Iterations, configured.ToString());
                Assert.AreEqual(1, logger.Entries.Count(x => x.Level == LogLevel.Warning));
                StringAssert.StartsWith(hasher.Hash("x"), "$pbkdf2-sha256$100000$");
            }
        }

        [TestMethod]
        public void IterationsAboveTheMaximumAreLowered()
        {
            foreach (var configured in new[] { PasswordHasher.MaxStoredIterations + 1, int.MaxValue })
            {
                var logger = new CapturingLogger();
                var hasher = Pbkdf2Hasher(configured, null, logger);
                Assert.AreEqual(2000000, hasher.Iterations);
                Assert.AreEqual(1, logger.Entries.Count(x => x.Level == LogLevel.Warning));
            }
        }

        [TestMethod]
        public void IterationsInsideTheRangeAreKeptWithoutWarning()
        {
            var logger = new CapturingLogger();
            foreach (var configured in new[] { PasswordHasher.MinIterations, 600000, PasswordHasher.MaxStoredIterations })
            {
                Assert.AreEqual(configured, Pbkdf2Hasher(configured, null, logger).Iterations);
            }
            Assert.AreEqual(0, logger.Entries.Count);
        }

        [TestMethod]
        public void OutOfRangeIterationsAreSilentWhileMd5IsConfigured()
        {
            var logger = new CapturingLogger();
            var hasher = new PasswordHasher(new PasswordOption { Pbkdf2Iterations = 5 }, null, logger);
            Assert.AreEqual(PasswordHasher.MinIterations, hasher.Iterations);
            Assert.AreEqual(0, logger.Entries.Count);
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
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(Rebuild(p => p[2] = "100001"), "secret"));
            // malformed pieces
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(Rebuild(p => p[3] = "!!!"), "secret"));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(Rebuild(p => p[4] = ""), "secret"));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(stored + "$extra", "secret"));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(Rebuild(p => p[2] = "99999999999"), "secret"));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(Rebuild(p => p[2] = (PasswordHasher.MaxStoredIterations + 1).ToString()), "secret"), "above the supported range");
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

        // builds a stored hash with any iteration count, including ones the hasher itself would no longer produce
        private static string StoredPbkdf2(string password, int iterations)
        {
            var salt = new byte[16];
            System.Security.Cryptography.RandomNumberGenerator.Fill(salt);
            var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
            return $"$pbkdf2-sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        [TestMethod]
        public void RehashNeededWhenIterationsAreLowerThanConfigured()
        {
            var weak = StoredPbkdf2("secret", 1000);
            Assert.AreEqual(PasswordVerifyResult.SuccessRehashNeeded, Pbkdf2Hasher(100000).Verify(weak, "secret"));
            Assert.AreEqual(PasswordVerifyResult.Failed, Pbkdf2Hasher(100000).Verify(weak, "wrong"));
            var current = Pbkdf2Hasher(150000).Hash("secret");
            Assert.AreEqual(PasswordVerifyResult.SuccessRehashNeeded, Pbkdf2Hasher(200000).Verify(current, "secret"));
            Assert.AreEqual(PasswordVerifyResult.Success, Pbkdf2Hasher(150000).Verify(current, "secret"));
            Assert.AreEqual(PasswordVerifyResult.Success, Pbkdf2Hasher(100000).Verify(current, "secret"), "more iterations than configured is fine");
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
            var hasher = Pbkdf2Hasher(600000, 32, logger);
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
            var needed = PasswordHasher.Pbkdf2OutputLength(100000);
            var exact = Pbkdf2Hasher(100000, needed, logger);
            Assert.AreEqual(PasswordAlgorithm.Pbkdf2Sha256, exact.Algorithm);
            Assert.AreEqual(needed, exact.Hash("x").Length);
            Assert.AreEqual(PasswordAlgorithm.Md5, Pbkdf2Hasher(100000, needed - 1).Algorithm);
            Assert.AreEqual(PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Hasher(100000, 255).Algorithm);
            Assert.AreEqual(PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Hasher(100000, null).Algorithm, "unknown or unlimited length");
            Assert.AreEqual(0, logger.Entries.Count);
        }

        [TestMethod]
        public void GuardUsesMoreDigitsOfLargeIterationCounts()
        {
            Assert.IsTrue(PasswordHasher.Pbkdf2OutputLength(1000000) > PasswordHasher.Pbkdf2OutputLength(100000));
            Assert.AreEqual(PasswordAlgorithm.Md5, Pbkdf2Hasher(1000000, PasswordHasher.Pbkdf2OutputLength(100000)).Algorithm);
        }

        private static CountingPasswordHasher Counting(PasswordAlgorithm algorithm)
        {
            return new CountingPasswordHasher(new PasswordOption { Algorithm = algorithm, Pbkdf2Iterations = PasswordHasher.MinIterations });
        }

        [TestMethod]
        public void Pbkdf2HasherSpendsOneDerivationOnEveryNonNullCheck()
        {
            var hasher = Counting(PasswordAlgorithm.Pbkdf2Sha256);
            var pbkdf2 = hasher.Hash("secret");
            Assert.AreEqual(1, hasher.Derivations);

            hasher.Verify(pbkdf2, "secret");
            Assert.AreEqual(2, hasher.Derivations, "pbkdf2 stored: the real derivation");
            hasher.Verify(Utils.GetMD5String("secret"), "secret");
            Assert.AreEqual(3, hasher.Derivations, "legacy MD5 stored: a dummy derivation");
            hasher.Verify(Utils.GetMD5String("secret"), "wrong");
            Assert.AreEqual(4, hasher.Derivations);
            hasher.Verify("not a hash", "secret");
            Assert.AreEqual(5, hasher.Derivations, "unrecognised value");
            hasher.Verify("$pbkdf2-sha256$bad", "secret");
            Assert.AreEqual(6, hasher.Derivations, "malformed pbkdf2 value");
            hasher.Verify("", "secret");
            hasher.Verify(null, "secret");
            Assert.AreEqual(8, hasher.Derivations, "empty or missing stored value");
        }

        [TestMethod]
        public void NullPasswordDoesNoHashingWork()
        {
            var hasher = Counting(PasswordAlgorithm.Pbkdf2Sha256);
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(Utils.GetMD5String("secret"), null));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify(null, null));
            Assert.AreEqual(PasswordVerifyResult.Failed, hasher.Verify("", null));
            hasher.BurnTime(null);
            Assert.AreEqual(0, hasher.Derivations);
            hasher.BurnTime("x");
            Assert.AreEqual(1, hasher.Derivations);
        }

        [TestMethod]
        public void Md5HasherNeverDerivesPbkdf2Keys()
        {
            var hasher = Counting(PasswordAlgorithm.Md5);
            hasher.Verify(Utils.GetMD5String("secret"), "secret");
            hasher.Verify("not a hash", "secret");
            hasher.BurnTime("secret");
            hasher.Hash("secret");
            Assert.AreEqual(0, hasher.Derivations);
        }

        private class HidingUser : LoginTestUser
        {
            [System.ComponentModel.DataAnnotations.StringLength(200)]
            public new string Password { get; set; }
        }

        [TestMethod]
        public void HiddenPasswordPropertyUsesTheMostDerivedDeclaration()
        {
            Assert.AreEqual(200, PasswordHasher.GetPasswordMaxLength(typeof(HidingUser), null));
        }

        [TestMethod]
        public void PasswordMaxLengthIsReadFromTheModel()
        {
            Assert.AreEqual(128, PasswordHasher.GetPasswordMaxLength(typeof(FrameworkUserBase), null));
            Assert.AreEqual(128, PasswordHasher.GetPasswordMaxLength(null, null));
            Assert.AreEqual(128, PasswordHasher.GetPasswordMaxLength(typeof(LoginTestUser), null));
            using (var dc = new LoginTestContext(Guid.NewGuid().ToString("N")))
            {
                Assert.AreEqual(128, PasswordHasher.GetPasswordMaxLength(typeof(LoginTestUser), dc));
            }
        }

        [TestMethod]
        public void PasswordColumnInTheModelHoldsThePbkdf2Format()
        {
            var attribute = typeof(FrameworkUserBase).GetProperty("Password").GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.StringLengthAttribute), true)
                .Cast<System.ComponentModel.DataAnnotations.StringLengthAttribute>().Single();
            Assert.AreEqual(128, attribute.MaximumLength);
            Assert.IsTrue(PasswordHasher.Pbkdf2OutputLength(PasswordHasher.MaxStoredIterations) <= 128, "even the highest iteration count fits");
        }

        [TestMethod]
        public void GuardWarningNamesTheMigrationGuide()
        {
            var tooShort = new CapturingLogger();
            Pbkdf2Hasher(100000, 32, tooShort);
            var unverified = new CapturingLogger();
            var hasher = new PasswordHasher(new PasswordOption { Algorithm = PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Iterations = PasswordHasher.MinIterations }, 128, unverified, "reason text");
            Assert.AreEqual(PasswordAlgorithm.Md5, hasher.Algorithm, "an unverified column is treated like a too short one, whatever the model says");
            Assert.IsTrue(hasher.FellBackToMd5);
            foreach (var logger in new[] { tooShort, unverified })
            {
                var warning = logger.Entries.Single(x => x.Level == LogLevel.Warning);
                StringAssert.Contains(warning.Message, "docs/migrations/password-hash-upgrade.md");
            }
            StringAssert.Contains(unverified.Entries.Single().Message, "reason text");
        }
    }
}
