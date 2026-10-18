using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class JwtOptionTest
    {
        // Values published in this repository (framework default and demo appsettings samples).
        private static readonly string[] RepoSampleKeys = new[]
        {
            "wtmwtmwtmwtmwtmwtm",
            "superSecretKey@345"
        };

        private static string RandomKey()
        {
            return Convert.ToBase64String(RandomNumberGenerator.GetBytes(36)); // 48 chars
        }

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

        [TestMethod]
        public void DefaultKeyIsInsecure()
        {
            Assert.IsTrue(new JwtOption().IsInsecureKey());
        }

        [TestMethod]
        public void ShortKeyIsInsecure()
        {
            var option = new JwtOption { SecurityKey = "short-key" };
            Assert.AreEqual(32, option.SecurityKey.Length);
            Assert.IsTrue(option.IsInsecureKey());
        }

        [TestMethod]
        public void RepositorySampleKeysAreInsecure()
        {
            foreach (var key in RepoSampleKeys)
            {
                Assert.IsTrue(new JwtOption { SecurityKey = key }.IsInsecureKey(), "raw sample key must be rejected");
                Assert.IsTrue(new JwtOption { SecurityKey = key.PadRight(32, 'x') }.IsInsecureKey(), "padded sample key must be rejected");
            }
        }

        [TestMethod]
        public void RandomLongKeyIsSecure()
        {
            var key = RandomKey();
            Assert.AreEqual(48, key.Length);
            var option = new JwtOption { SecurityKey = key };
            Assert.IsFalse(option.IsInsecureKey());
            Assert.AreEqual(key, option.SecurityKey);
        }

        [TestMethod]
        public void ReassigningSecureKeyClearsPaddedFlag()
        {
            var option = new JwtOption { SecurityKey = "short" };
            Assert.IsTrue(option.IsInsecureKey());
            option.SecurityKey = RandomKey();
            Assert.IsFalse(option.IsInsecureKey());
        }

        [TestMethod]
        public void EnsureSecureKeyThrowsOutsideDevelopmentForInsecureKey()
        {
            var ex = Assert.ThrowsExactly<InvalidOperationException>(() => new JwtOption().EnsureSecureKey(false, null));
            Assert.IsFalse(ex.Message.Contains("wtmwtmwtm"));
            Assert.IsTrue(ex.Message.Contains("JwtOptions__SecurityKey"));

            var sample = new JwtOption { SecurityKey = RepoSampleKeys[1] };
            var ex2 = Assert.ThrowsExactly<InvalidOperationException>(() => sample.EnsureSecureKey(false, null));
            Assert.IsFalse(ex2.Message.Contains(RepoSampleKeys[1]));
        }

        [TestMethod]
        public void EnsureSecureKeyDoesNotThrowOutsideDevelopmentForSecureKey()
        {
            var option = new JwtOption { SecurityKey = RandomKey() };
            option.EnsureSecureKey(false, null);
        }

        [TestMethod]
        public void EnsureSecureKeyOnlyWarnsInDevelopment()
        {
            var logger = new CapturingLogger();
            var option = new JwtOption { SecurityKey = RepoSampleKeys[1] };
            option.EnsureSecureKey(true, logger);

            Assert.AreEqual(1, logger.Entries.Count);
            Assert.AreEqual(LogLevel.Warning, logger.Entries[0].Level);
            foreach (var key in RepoSampleKeys)
            {
                Assert.IsFalse(logger.Entries[0].Message.Contains(key));
            }
            Assert.IsFalse(logger.Entries[0].Message.Contains(option.SecurityKey));

            // a null logger must not break Development startup either
            new JwtOption().EnsureSecureKey(true, null);
        }

        [TestMethod]
        public void EnsureSecureKeyIsSilentForSecureKeyInDevelopment()
        {
            var logger = new CapturingLogger();
            new JwtOption { SecurityKey = RandomKey() }.EnsureSecureKey(true, logger);
            Assert.AreEqual(0, logger.Entries.Count);
        }
    }
}
