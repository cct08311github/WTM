using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class OracleVersionWarningTest
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

        private static Configs Make(DBTypeEnum? type, string version)
        {
            return new Configs { Connections = new List<CS> { new CS { Key = "default", DbType = type, Version = version } } };
        }

        [DataTestMethod]
        [DataRow("19", 19)]
        [DataRow("21", 21)]
        [DataRow("23", 23)]
        public void SupportedVersions_MapToThemselves(string version, int expected)
        {
            var r = WtmEnvironmentHelper.ResolveOracleCompatibility(version);
            Assert.AreEqual(expected, r.Compatibility);
            Assert.IsTrue(r.IsSupported);
        }

        [DataTestMethod]
        [DataRow("11")]
        [DataRow("12")]
        [DataRow("18")]
        [DataRow("abc")]
        public void UnsupportedVersions_Use19AndAreFlagged(string version)
        {
            var r = WtmEnvironmentHelper.ResolveOracleCompatibility(version);
            Assert.AreEqual(19, r.Compatibility);
            Assert.IsFalse(r.IsSupported);
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        public void UnspecifiedVersion_Uses19WithoutFlag(string version)
        {
            var r = WtmEnvironmentHelper.ResolveOracleCompatibility(version);
            Assert.AreEqual(19, r.Compatibility);
            Assert.IsTrue(r.IsSupported);
        }

        [DataTestMethod]
        [DataRow("11")]
        [DataRow("12")]
        public void UnsupportedOracleVersion_LogsOneWarning(string version)
        {
            var logger = new CapturingLogger();
            WtmEnvironmentHelper.LogOracleVersionWarnings(Make(DBTypeEnum.Oracle, version), logger);
            Assert.AreEqual(1, logger.Entries.Count);
            Assert.AreEqual(LogLevel.Warning, logger.Entries[0].Level);
            StringAssert.Contains(logger.Entries[0].Message, "19c");
            StringAssert.Contains(logger.Entries[0].Message, version);
        }

        [DataTestMethod]
        [DataRow("19")]
        [DataRow("21")]
        [DataRow("23")]
        [DataRow(null)]
        public void SupportedOrUnspecified_LogsNothing(string version)
        {
            var logger = new CapturingLogger();
            WtmEnvironmentHelper.LogOracleVersionWarnings(Make(DBTypeEnum.Oracle, version), logger);
            Assert.AreEqual(0, logger.Entries.Count);
        }

        [TestMethod]
        public void NonOracleConnections_NeverWarn()
        {
            var logger = new CapturingLogger();
            WtmEnvironmentHelper.LogOracleVersionWarnings(Make(DBTypeEnum.SqlServer, "11"), logger);
            WtmEnvironmentHelper.LogOracleVersionWarnings(Make(null, "11"), logger);
            Assert.AreEqual(0, logger.Entries.Count);
        }

        [TestMethod]
        public void NullInputs_DoNotThrow()
        {
            WtmEnvironmentHelper.LogOracleVersionWarnings(null, new CapturingLogger());
            WtmEnvironmentHelper.LogOracleVersionWarnings(Make(DBTypeEnum.Oracle, "11"), null);
        }
    }
}
