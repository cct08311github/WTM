using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class ExposeExceptionDetailsTest
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

        [TestMethod]
        public void ExposeExceptionDetails_DefaultsToNull()
        {
            Assert.IsNull(new Configs().ExposeExceptionDetails);
        }

        [TestMethod]
        public void NotSet_FollowsIsQuickDebug()
        {
            Assert.IsFalse(Configs.ShouldExposeExceptionDetails(new Configs()));
            Assert.IsFalse(Configs.ShouldExposeExceptionDetails(new Configs { IsQuickDebug = false }));
            Assert.IsTrue(Configs.ShouldExposeExceptionDetails(new Configs { IsQuickDebug = true }));
        }

        [TestMethod]
        public void ExplicitFalse_HidesDetailsEvenWhenQuickDebug()
        {
            Assert.IsFalse(Configs.ShouldExposeExceptionDetails(new Configs { IsQuickDebug = true, ExposeExceptionDetails = false }));
            Assert.IsFalse(Configs.ShouldExposeExceptionDetails(new Configs { IsQuickDebug = false, ExposeExceptionDetails = false }));
        }

        [TestMethod]
        public void ExplicitTrue_ExposesDetailsEvenWithoutQuickDebug()
        {
            Assert.IsTrue(Configs.ShouldExposeExceptionDetails(new Configs { IsQuickDebug = false, ExposeExceptionDetails = true }));
            Assert.IsTrue(Configs.ShouldExposeExceptionDetails(new Configs { IsQuickDebug = true, ExposeExceptionDetails = true }));
        }

        [TestMethod]
        public void NullConfigs_DoesNotExpose()
        {
            Assert.IsFalse(Configs.ShouldExposeExceptionDetails(null));
        }

        [TestMethod]
        public void BindsFromConfigurationRoot()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["IsQuickDebug"] = "true",
                    ["ExposeExceptionDetails"] = "false"
                })
                .Build();
            var configs = config.Get<Configs>();
            Assert.IsTrue(configs.IsQuickDebug);
            Assert.AreEqual(false, configs.ExposeExceptionDetails);
            Assert.IsFalse(Configs.ShouldExposeExceptionDetails(configs));

            var unset = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string> { ["IsQuickDebug"] = "true" })
                .Build().Get<Configs>();
            Assert.IsNull(unset.ExposeExceptionDetails);
            Assert.IsTrue(Configs.ShouldExposeExceptionDetails(unset));
        }

        [TestMethod]
        public void QuickDebugWarning_OnlyWhenEnabledOutsideDevelopment()
        {
            Assert.IsTrue(WtmEnvironmentHelper.ShouldWarnQuickDebug(new Configs { IsQuickDebug = true }, false));
            Assert.IsFalse(WtmEnvironmentHelper.ShouldWarnQuickDebug(new Configs { IsQuickDebug = true }, true));
            Assert.IsFalse(WtmEnvironmentHelper.ShouldWarnQuickDebug(new Configs { IsQuickDebug = false }, false));
            Assert.IsFalse(WtmEnvironmentHelper.ShouldWarnQuickDebug(new Configs(), false));
            Assert.IsFalse(WtmEnvironmentHelper.ShouldWarnQuickDebug(null, false));
        }

        [TestMethod]
        public void QuickDebugWarning_LogsCriticalAndDoesNotThrow()
        {
            var logger = new CapturingLogger();
            WtmEnvironmentHelper.LogQuickDebugWarning(new Configs { IsQuickDebug = true }, false, logger);
            Assert.AreEqual(1, logger.Entries.Count);
            Assert.AreEqual(LogLevel.Critical, logger.Entries[0].Level);
            StringAssert.Contains(logger.Entries[0].Message, "IsQuickDebug");

            var quiet = new CapturingLogger();
            WtmEnvironmentHelper.LogQuickDebugWarning(new Configs { IsQuickDebug = true }, true, quiet);
            WtmEnvironmentHelper.LogQuickDebugWarning(new Configs { IsQuickDebug = false }, false, quiet);
            Assert.AreEqual(0, quiet.Entries.Count);

            // a missing logger must not throw
            WtmEnvironmentHelper.LogQuickDebugWarning(new Configs { IsQuickDebug = true }, false, null);
        }
    }
}
