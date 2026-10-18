using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class JwtOptionTest
    {
        // Values published in this repository (framework default and demo appsettings samples).
        private static readonly string[] RepoSampleKeys = new[]
        {
            "wtmwtmwtmwtmwtmwtm",
            "superSecretKey@345",
            "CHANGE-ME-development-only-placeholder-key"
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
            var defaultOption = new JwtOption();
            var ex = Assert.ThrowsExactly<InvalidOperationException>(() => defaultOption.EnsureSecureKey(false, null));
            Assert.IsFalse(ex.Message.Contains(defaultOption.SecurityKey));
            Assert.IsFalse(ex.Message.Contains("wtmwtmwtm"));
            Assert.IsTrue(ex.Message.Contains("JwtOptions__SecurityKey"));

            var sample = new JwtOption { SecurityKey = RepoSampleKeys[1] };
            var ex2 = Assert.ThrowsExactly<InvalidOperationException>(() => sample.EnsureSecureKey(false, null));
            Assert.IsFalse(ex2.Message.Contains(sample.SecurityKey));
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

        private static JwtOption OptionFor(bool secure, bool allowInsecure)
        {
            var option = secure ? new JwtOption { SecurityKey = RandomKey() } : new JwtOption { SecurityKey = RepoSampleKeys[1] };
            option.AllowInsecureKey = allowInsecure;
            return option;
        }

        [TestMethod]
        public void AllowInsecureKeyDefaultsToFalse()
        {
            Assert.IsFalse(new JwtOption().AllowInsecureKey);
        }

        [TestMethod]
        public void AllowInsecureKeyOutsideDevelopmentLogsCriticalInsteadOfThrowing()
        {
            var logger = new CapturingLogger();
            var option = OptionFor(secure: false, allowInsecure: true);
            option.EnsureSecureKey(false, logger);

            Assert.AreEqual(1, logger.Entries.Count);
            Assert.AreEqual(LogLevel.Critical, logger.Entries[0].Level);
            var message = logger.Entries[0].Message;
            Assert.IsTrue(message.Contains("JwtOptions:AllowInsecureKey"));
            Assert.IsTrue(message.Contains("JwtOptions__SecurityKey"));
            Assert.IsFalse(message.Contains(option.SecurityKey));
            foreach (var key in RepoSampleKeys)
            {
                Assert.IsFalse(message.Contains(key));
            }
        }

        [TestMethod]
        public void AllowInsecureKeyWithDefaultKeyCriticalMessageDoesNotContainKey()
        {
            var logger = new CapturingLogger();
            var option = new JwtOption { AllowInsecureKey = true };
            option.EnsureSecureKey(false, logger);

            Assert.AreEqual(1, logger.Entries.Count);
            Assert.AreEqual(LogLevel.Critical, logger.Entries[0].Level);
            Assert.IsFalse(logger.Entries[0].Message.Contains(option.SecurityKey));
            Assert.IsFalse(logger.Entries[0].Message.Contains("wtmwtmwtm"));
        }

        [TestMethod]
        public void AllowInsecureKeyDoesNotThrowWithoutLogger()
        {
            // mirrors the early call in AddWtmContext, which has no logger
            OptionFor(secure: false, allowInsecure: true).EnsureSecureKey(false, null);
        }

        [TestMethod]
        public void EnsureSecureKeyBehaviourMatrix()
        {
            // (secure key, AllowInsecureKey, isDevelopment) -> expected outcome
            foreach (var secure in new[] { true, false })
            foreach (var allow in new[] { true, false })
            foreach (var dev in new[] { true, false })
            {
                var logger = new CapturingLogger();
                var option = OptionFor(secure, allow);
                var label = $"secure={secure}, allow={allow}, dev={dev}";

                if (secure)
                {
                    option.EnsureSecureKey(dev, logger);
                    Assert.AreEqual(0, logger.Entries.Count, label);
                }
                else if (dev)
                {
                    option.EnsureSecureKey(true, logger);
                    Assert.AreEqual(1, logger.Entries.Count, label);
                    Assert.AreEqual(LogLevel.Warning, logger.Entries[0].Level, label);
                }
                else if (allow)
                {
                    option.EnsureSecureKey(false, logger);
                    Assert.AreEqual(1, logger.Entries.Count, label);
                    Assert.AreEqual(LogLevel.Critical, logger.Entries[0].Level, label);
                }
                else
                {
                    Assert.ThrowsExactly<InvalidOperationException>(() => option.EnsureSecureKey(false, logger), label);
                    Assert.AreEqual(0, logger.Entries.Count, label);
                }
            }
        }

        [TestMethod]
        public void BoundConfigAllowInsecureKeyTrue()
        {
            var conf = BindConfigs(new Dictionary<string, string> { ["JwtOptions:AllowInsecureKey"] = "true" });
            Assert.IsTrue(conf.JwtOptions.AllowInsecureKey);
            Assert.IsTrue(conf.JwtOptions.IsInsecureKey());
            conf.JwtOptions.EnsureSecureKey(false, null); // must not throw
        }

        [TestMethod]
        public void BoundConfigAllowInsecureKeyDefaultsToFalse()
        {
            var conf = BindConfigs(new Dictionary<string, string> { ["JwtOptions:Issuer"] = "http://example.invalid" });
            Assert.IsFalse(conf.JwtOptions.AllowInsecureKey);
            Assert.ThrowsExactly<InvalidOperationException>(() => conf.JwtOptions.EnsureSecureKey(false, null));

            var explicitFalse = BindConfigs(new Dictionary<string, string> { ["JwtOptions:AllowInsecureKey"] = "false" });
            Assert.IsFalse(explicitFalse.JwtOptions.AllowInsecureKey);
        }

        private static string RandomKeyOfLength(int length)
        {
            var sb = new System.Text.StringBuilder();
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWYZabcdefghijkmnopqrstuvwyz0123456789";
            while (sb.Length < length)
            {
                sb.Append(alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]);
            }
            return sb.ToString();
        }

        [TestMethod]
        public void KeyLengthBoundaries()
        {
            Assert.IsTrue(new JwtOption { SecurityKey = RandomKeyOfLength(31) }.IsInsecureKey(), "31 characters is too short");
            Assert.IsFalse(new JwtOption { SecurityKey = RandomKeyOfLength(32) }.IsInsecureKey(), "32 characters is long enough");
        }

        [TestMethod]
        public void RandomKeyEndingWithXIsSecure()
        {
            var key = RandomKeyOfLength(31) + "x";
            Assert.AreEqual(32, key.Length);
            Assert.IsFalse(new JwtOption { SecurityKey = key }.IsInsecureKey());
        }

        [TestMethod]
        public void PaddedSampleKeyBeyondMinimumLengthIsInsecure()
        {
            foreach (var key in RepoSampleKeys)
            {
                var padded = key.PadRight(key.Length + 6, 'x');
                Assert.AreEqual(key.Length + 6, padded.Length);
                Assert.IsTrue(new JwtOption { SecurityKey = padded }.IsInsecureKey(), "sample key padded beyond 32 characters must be rejected");
                Assert.IsTrue(new JwtOption { SecurityKey = padded.Replace('x', 'X') }.IsInsecureKey(), "upper-case padding must be rejected");
            }
        }

        [TestMethod]
        public void UpperAndMixedCaseSampleKeysAreInsecure()
        {
            foreach (var key in RepoSampleKeys)
            {
                Assert.IsTrue(new JwtOption { SecurityKey = key.ToUpperInvariant() }.IsInsecureKey());
                Assert.IsTrue(new JwtOption { SecurityKey = key.ToUpperInvariant().PadRight(38, 'X') }.IsInsecureKey());
                Assert.IsTrue(new JwtOption { SecurityKey = key.ToLowerInvariant().PadRight(40, 'x') }.IsInsecureKey());
            }
        }

        [TestMethod]
        public void SampleKeyWithNonPaddingSuffixIsNotTreatedAsSample()
        {
            // a sample prefix followed by genuinely different content is not a published value
            var key = RepoSampleKeys[1] + RandomKeyOfLength(20);
            Assert.IsFalse(new JwtOption { SecurityKey = key }.IsInsecureKey());
        }

        [TestMethod]
        public void WhitespaceKeyIsInsecure()
        {
            Assert.IsTrue(new JwtOption { SecurityKey = new string(' ', 32) }.IsInsecureKey());
            Assert.IsTrue(new JwtOption { SecurityKey = new string(' ', 48) }.IsInsecureKey());
            Assert.IsTrue(new JwtOption { SecurityKey = string.Empty }.IsInsecureKey());
        }

        [TestMethod]
        public void LowVarietyKeyIsInsecure()
        {
            Assert.IsTrue(new JwtOption { SecurityKey = new string('a', 40) }.IsInsecureKey());
            Assert.IsTrue(new JwtOption { SecurityKey = "abababababababababababababababab" }.IsInsecureKey());
            Assert.IsTrue(new JwtOption { SecurityKey = "abcdabcdabcdabcdabcdabcdabcdabcdabcd" }.IsInsecureKey(), "only 4 distinct characters");
        }

        [TestMethod]
        public void NullKeyAssignmentIsInsecureAndDoesNotThrow()
        {
            var option = new JwtOption();
            option.SecurityKey = null;
            Assert.IsTrue(option.IsInsecureKey());
            Assert.AreEqual(32, option.SecurityKey.Length);
            Assert.ThrowsExactly<InvalidOperationException>(() => option.EnsureSecureKey(false, null));
        }

        private static Configs BindConfigs(Dictionary<string, string> values)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            return config.Get<Configs>();
        }

        [TestMethod]
        public void BoundConfigWithoutKeyIsInsecure()
        {
            var conf = BindConfigs(new Dictionary<string, string> { ["JwtOptions:Issuer"] = "http://example.invalid" });
            Assert.IsTrue(conf.JwtOptions.IsInsecureKey());
        }

        [TestMethod]
        public void BoundConfigWithEmptyKeyIsInsecure()
        {
            var conf = BindConfigs(new Dictionary<string, string> { ["JwtOptions:SecurityKey"] = "" });
            Assert.IsTrue(conf.JwtOptions.IsInsecureKey());
        }

        [TestMethod]
        public void BoundConfigWithRandomKeyIsSecure()
        {
            var key = RandomKey();
            Assert.AreEqual(48, key.Length);
            var conf = BindConfigs(new Dictionary<string, string> { ["JwtOptions:SecurityKey"] = key });
            Assert.IsFalse(conf.JwtOptions.IsInsecureKey());
            Assert.AreEqual(key, conf.JwtOptions.SecurityKey);
        }

        [TestMethod]
        public void BoundConfigWithSampleKeyIsInsecure()
        {
            foreach (var key in RepoSampleKeys)
            {
                var conf = BindConfigs(new Dictionary<string, string> { ["JwtOptions:SecurityKey"] = key });
                Assert.IsTrue(conf.JwtOptions.IsInsecureKey());
            }
        }

        private class FakeHostEnvironment : IHostEnvironment
        {
            public string EnvironmentName { get; set; }
            public string ApplicationName { get; set; } = "Test";
            public string ContentRootPath { get; set; } = ".";
            public IFileProvider ContentRootFileProvider { get; set; }
        }

        private static IServiceCollection ServicesWithEnvironment(string name)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment { EnvironmentName = name });
            return services;
        }

        private static IConfiguration ConfigWith(params (string Key, string Value)[] values)
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection(values.ToDictionary(x => x.Key, x => x.Value))
                .Build();
        }

        [TestMethod]
        public void EnvironmentInstanceDevelopmentIsDevelopment()
        {
            var config = ConfigWith();
            Assert.IsTrue(WtmEnvironmentHelper.ResolveIsDevelopment(ServicesWithEnvironment("Development"), config));
            Assert.IsTrue(WtmEnvironmentHelper.ResolveIsDevelopment(ServicesWithEnvironment("development"), config));
        }

        [TestMethod]
        public void EnvironmentInstanceOtherThanDevelopmentIsNotDevelopment()
        {
            var config = ConfigWith();
            Assert.IsFalse(WtmEnvironmentHelper.ResolveIsDevelopment(ServicesWithEnvironment("Staging"), config));
            Assert.IsFalse(WtmEnvironmentHelper.ResolveIsDevelopment(ServicesWithEnvironment("Production"), config));
        }

        [TestMethod]
        public void EnvironmentInstanceWinsOverConfiguration()
        {
            var config = ConfigWith(("ASPNETCORE_ENVIRONMENT", "Development"));
            Assert.IsFalse(WtmEnvironmentHelper.ResolveIsDevelopment(ServicesWithEnvironment("Production"), config));
        }

        [TestMethod]
        public void EnvironmentFromConfigurationWithoutInstance()
        {
            var services = new ServiceCollection();
            Assert.IsTrue(WtmEnvironmentHelper.ResolveIsDevelopment(services, ConfigWith(("ASPNETCORE_ENVIRONMENT", "Development"))));
            Assert.IsTrue(WtmEnvironmentHelper.ResolveIsDevelopment(services, ConfigWith(("DOTNET_ENVIRONMENT", "development"))));
            Assert.IsTrue(WtmEnvironmentHelper.ResolveIsDevelopment(services, ConfigWith(("environment", "DEVELOPMENT"))));
            Assert.IsFalse(WtmEnvironmentHelper.ResolveIsDevelopment(services, ConfigWith(("ASPNETCORE_ENVIRONMENT", "Staging"))));
            Assert.IsFalse(WtmEnvironmentHelper.ResolveIsDevelopment(services, ConfigWith(("environment", "Production"), ("ASPNETCORE_ENVIRONMENT", "Development"))), "the 'environment' key takes precedence");
        }

        [TestMethod]
        public void UnknownEnvironmentFailsClosed()
        {
            // only meaningful when the test process itself has no environment name set
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")) == false
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")) == false)
            {
                Assert.Inconclusive("The test process defines an environment name.");
            }
            Assert.IsFalse(WtmEnvironmentHelper.ResolveIsDevelopment(new ServiceCollection(), ConfigWith()));
            Assert.IsFalse(WtmEnvironmentHelper.ResolveIsDevelopment(new ServiceCollection(), null));
        }

        [TestMethod]
        public void ReloadWithUnchangedKeyIsSilent()
        {
            var key = RandomKey();
            var logger = new CapturingLogger();
            var startup = new JwtOption { SecurityKey = key };
            var current = new JwtOption { SecurityKey = key, Expires = 7200 };
            Assert.AreEqual(JwtKeyReloadChange.None, JwtOption.DescribeReloadChange(startup, current));
            JwtOption.LogReloadChange(logger, startup, current, false);
            JwtOption.LogReloadChange(logger, startup, current, true);
            Assert.AreEqual(0, logger.Entries.Count);

            // an insecure key that did not change was already handled by the startup check
            var insecure = new JwtOption { SecurityKey = RepoSampleKeys[1], AllowInsecureKey = true };
            JwtOption.LogReloadChange(logger, insecure, new JwtOption { SecurityKey = RepoSampleKeys[1], AllowInsecureKey = true }, false);
            Assert.AreEqual(0, logger.Entries.Count);
        }

        [TestMethod]
        public void ReloadWithChangedSecureKeyWarnsAboutRestart()
        {
            var startup = new JwtOption { SecurityKey = RandomKey() };
            var current = new JwtOption { SecurityKey = RandomKey() };
            Assert.AreEqual(JwtKeyReloadChange.ChangedKey, JwtOption.DescribeReloadChange(startup, current));
            foreach (var dev in new[] { true, false })
            {
                var logger = new CapturingLogger();
                JwtOption.LogReloadChange(logger, startup, current, dev);
                Assert.AreEqual(1, logger.Entries.Count);
                Assert.AreEqual(LogLevel.Warning, logger.Entries[0].Level);
                StringAssert.Contains(logger.Entries[0].Message, "restart");
                Assert.IsFalse(logger.Entries[0].Message.Contains(startup.SecurityKey));
                Assert.IsFalse(logger.Entries[0].Message.Contains(current.SecurityKey));
            }
        }

        [TestMethod]
        public void ReloadWithChangedInsecureKeyIsCriticalOutsideDevelopmentAndWarningInDevelopment()
        {
            var startup = new JwtOption { SecurityKey = RandomKey() };
            foreach (var sample in RepoSampleKeys)
            {
                var current = new JwtOption { SecurityKey = sample, AllowInsecureKey = true };
                Assert.AreEqual(JwtKeyReloadChange.ChangedInsecureKey, JwtOption.DescribeReloadChange(startup, current));

                var prod = new CapturingLogger();
                JwtOption.LogReloadChange(prod, startup, current, false);
                Assert.AreEqual(1, prod.Entries.Count);
                Assert.AreEqual(LogLevel.Critical, prod.Entries[0].Level);

                var dev = new CapturingLogger();
                JwtOption.LogReloadChange(dev, startup, current, true);
                Assert.AreEqual(1, dev.Entries.Count);
                Assert.AreEqual(LogLevel.Warning, dev.Entries[0].Level);

                foreach (var message in new[] { prod.Entries[0].Message, dev.Entries[0].Message })
                {
                    StringAssert.Contains(message, "restart");
                    StringAssert.Contains(message, "JwtOptions:AllowInsecureKey");
                    Assert.IsFalse(message.Contains(startup.SecurityKey));
                    Assert.IsFalse(message.Contains(current.SecurityKey));
                    foreach (var key in RepoSampleKeys)
                    {
                        Assert.IsFalse(message.Contains(key));
                    }
                }
            }
        }

        [TestMethod]
        public void ReloadChangeDetectionToleratesMissingOptionsAndLogger()
        {
            var option = new JwtOption { SecurityKey = RandomKey() };
            Assert.AreEqual(JwtKeyReloadChange.None, JwtOption.DescribeReloadChange(null, option));
            Assert.AreEqual(JwtKeyReloadChange.None, JwtOption.DescribeReloadChange(option, null));
            JwtOption.LogReloadChange(null, option, new JwtOption { SecurityKey = RandomKey() }, false);
            JwtOption.LogReloadChange(new CapturingLogger(), option, null, false);
        }
    }
}
