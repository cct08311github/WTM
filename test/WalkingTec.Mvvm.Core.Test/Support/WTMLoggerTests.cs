#nullable enable
using System;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Support
{
    /// <summary>
    /// Tests for WTMLogger / WTMLoggerProvider / WTMeLoggerExtensions (WTMLogger.cs).
    /// Focuses on IsEnabled logic and the extension-method wiring.
    /// The actual Log() path that writes to DataContext is intentionally not unit-tested
    /// here — it requires a full WTMContext + DB and is covered by integration tests.
    /// </summary>
    [TestClass]
    public class WTMLoggerTests
    {
        // ── Helpers ──────────────────────────────────────────────────────────

        private static LoggerFilterOptions BuildOptions(
            string? providerName,
            string? categoryName,
            LogLevel level)
        {
            var opts = new LoggerFilterOptions();
            opts.Rules.Add(new LoggerFilterRule(providerName, categoryName, level, null));
            return opts;
        }

        private static WTMLogger MakeLogger(string category, LoggerFilterOptions opts)
        {
            // WTMLogger only needs sp for Log(); for IsEnabled tests any sp is fine
            var sp = new ServiceCollection().BuildServiceProvider();
            return new WTMLogger(category, opts, sp);
        }

        // ── IsEnabled ────────────────────────────────────────────────────────

        [TestMethod]
        public void IsEnabled_null_logConfig_returns_false()
        {
            // Simulate null logConfig
            var sp = new ServiceCollection().BuildServiceProvider();
            var logger = new WTMLogger("SomeCategory", null!, sp);
            Assert.IsFalse(logger.IsEnabled(LogLevel.Information));
        }

        [TestMethod]
        public void IsEnabled_VueCliMiddleware_category_always_returns_false()
        {
            var opts = BuildOptions("WTM", "VueCliMiddleware", LogLevel.Trace);
            var logger = MakeLogger("VueCliMiddleware", opts);
            Assert.IsFalse(logger.IsEnabled(LogLevel.Trace));
        }

        [TestMethod]
        public void IsEnabled_matching_WTM_provider_and_category_prefix_returns_true()
        {
            // Rule: WTM provider, categoryName starts with "WalkingTec.Mvvm"
            var opts = BuildOptions("WTM", "WalkingTec.Mvvm", LogLevel.Information);
            var logger = MakeLogger("WalkingTec.Mvvm.Core.SomeService", opts);
            Assert.IsTrue(logger.IsEnabled(LogLevel.Information));
        }

        [TestMethod]
        public void IsEnabled_matching_rule_lower_level_returns_false()
        {
            var opts = BuildOptions("WTM", "WalkingTec.Mvvm", LogLevel.Warning);
            var logger = MakeLogger("WalkingTec.Mvvm.Core.SomeService", opts);
            Assert.IsFalse(logger.IsEnabled(LogLevel.Information));
            Assert.IsTrue(logger.IsEnabled(LogLevel.Warning));
            Assert.IsTrue(logger.IsEnabled(LogLevel.Error));
        }

        [TestMethod]
        public void IsEnabled_ActionLog_category_always_enabled_when_rule_present()
        {
            // "WalkingTec.Mvvm.Core.ActionLog" is a special-cased category
            var opts = BuildOptions("WTM", "SomeOtherCategory", LogLevel.Information);
            var logger = MakeLogger("WalkingTec.Mvvm.Core.ActionLog", opts);
            Assert.IsTrue(logger.IsEnabled(LogLevel.Information));
        }

        [TestMethod]
        public void IsEnabled_no_matching_WTM_rule_returns_false()
        {
            // Rule for a different provider — should not match
            var opts = BuildOptions("NotWTM", "WalkingTec.Mvvm", LogLevel.Trace);
            var logger = MakeLogger("WalkingTec.Mvvm.Core.SomeService", opts);
            Assert.IsFalse(logger.IsEnabled(LogLevel.Information));
        }

        [TestMethod]
        public void IsEnabled_empty_rules_returns_false()
        {
            var opts = new LoggerFilterOptions(); // no rules
            var logger = MakeLogger("WalkingTec.Mvvm.Core.SomeService", opts);
            Assert.IsFalse(logger.IsEnabled(LogLevel.Information));
        }

        [TestMethod]
        public void IsEnabled_LogLevel_None_rule_returns_false_for_any_level()
        {
            var opts = BuildOptions("WTM", "WalkingTec.Mvvm", LogLevel.None);
            var logger = MakeLogger("WalkingTec.Mvvm.Core.X", opts);
            Assert.IsFalse(logger.IsEnabled(LogLevel.Critical));
        }

        // ── BeginScope ───────────────────────────────────────────────────────

        [TestMethod]
        public void BeginScope_returns_null()
        {
            var opts = new LoggerFilterOptions();
            var logger = MakeLogger("Cat", opts);
            var scope = logger.BeginScope("some-scope-state");
            Assert.IsNull(scope);
        }

        // ── AddWTMLogger extension method ─────────────────────────────────────

        [TestMethod]
        public void AddWTMLogger_registers_WTMLoggerProvider()
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddWTMLogger());
            // WTMLoggerProvider needs IOptionsMonitor<LoggerFilterOptions> and IServiceProvider
            // which are available after AddLogging()
            using var sp = services.BuildServiceProvider();
            var providers = sp.GetServices<ILoggerProvider>().ToList();
            Assert.IsTrue(
                providers.Any(p => p is WTMLoggerProvider),
                $"Expected WTMLoggerProvider. Got: {string.Join(", ", providers.Select(p => p.GetType().Name))}");
        }

        [TestMethod]
        public void WTMLoggerProvider_CreateLogger_returns_WTMLogger_instance()
        {
            var services = new ServiceCollection();
            services.AddHttpContextAccessor();
            services.AddLogging(b => b.AddWTMLogger());
            using var sp = services.BuildServiceProvider();

            var provider = sp.GetServices<ILoggerProvider>().OfType<WTMLoggerProvider>().First();
            var logger = provider.CreateLogger("TestCategory");
            Assert.IsInstanceOfType<WTMLogger>(logger);
        }

        [TestMethod]
        public void WTMLoggerProvider_Dispose_does_not_throw()
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddWTMLogger());
            using var sp = services.BuildServiceProvider();
            var provider = sp.GetServices<ILoggerProvider>().OfType<WTMLoggerProvider>().First();
            // Should be a no-op, not throw
            provider.Dispose();
        }
    }
}
