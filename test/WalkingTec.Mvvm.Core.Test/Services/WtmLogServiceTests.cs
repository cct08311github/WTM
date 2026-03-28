#nullable enable
using System;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Services;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core.Test.Services
{
    [TestClass]
    public class WtmLogServiceTests
    {
        private FakeTimeProvider _timeProvider = null!;
        private Mock<ILoggerFactory> _loggerFactory = null!;
        private Mock<ILogger<ActionLog>> _logger = null!;
        private WtmLogService _service = null!;

        [TestInitialize]
        public void Setup()
        {
            _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 3, 28, 10, 0, 0, TimeSpan.FromHours(8)));
            _logger = new Mock<ILogger<ActionLog>>();
            _loggerFactory = new Mock<ILoggerFactory>();
            _loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(_logger.Object);
            _service = new WtmLogService(_timeProvider, _loggerFactory.Object);
        }

        #region Constructor

        [TestMethod]
        public void Ctor_NullTimeProvider_ThrowsArgumentNullException()
        {
            Action act = () => new WtmLogService(null!);
            act.Should().Throw<ArgumentNullException>().WithParameterName("timeProvider");
        }

        [TestMethod]
        public void Ctor_NullLoggerFactory_DoesNotThrow()
        {
            Action act = () => new WtmLogService(TimeProvider.System, null);
            act.Should().NotThrow();
        }

        #endregion

        #region Log level mapping

        [TestMethod]
        public void DoLog_Normal_LogsInformation()
        {
            _service.DoLog("test message", ActionLogTypesEnum.Normal);

            _logger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.IsAny<ActionLog>(),
                    null,
                    It.IsAny<Func<ActionLog, Exception?, string>>()),
                Times.Once);
        }

        [TestMethod]
        public void DoLog_Exception_LogsError()
        {
            _service.DoLog("error occurred", ActionLogTypesEnum.Exception);

            _logger.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.IsAny<ActionLog>(),
                    null,
                    It.IsAny<Func<ActionLog, Exception?, string>>()),
                Times.Once);
        }

        [TestMethod]
        public void DoLog_Debug_LogsDebug()
        {
            _service.DoLog("debug info", ActionLogTypesEnum.Debug);

            _logger.Verify(
                x => x.Log(
                    LogLevel.Debug,
                    It.IsAny<EventId>(),
                    It.IsAny<ActionLog>(),
                    null,
                    It.IsAny<Func<ActionLog, Exception?, string>>()),
                Times.Once);
        }

        #endregion

        #region TimeProvider

        [TestMethod]
        public void DoLog_UsesTimeProvider_ForActionTime()
        {
            ActionLog? capturedLog = null;
            _logger.Setup(x => x.Log(
                    It.IsAny<LogLevel>(),
                    It.IsAny<EventId>(),
                    It.IsAny<ActionLog>(),
                    null,
                    It.IsAny<Func<ActionLog, Exception?, string>>()))
                .Callback<LogLevel, EventId, ActionLog, Exception?, Func<ActionLog, Exception?, string>>(
                    (_, _, log, _, _) => capturedLog = log);

            _service.DoLog("time test");

            capturedLog.Should().NotBeNull();
            capturedLog!.ActionTime.Should().Be(_timeProvider.GetLocalNow().DateTime);
        }

        #endregion

        #region Field population

        [TestMethod]
        public void DoLog_PopulatesAllFields()
        {
            ActionLog? capturedLog = null;
            _logger.Setup(x => x.Log(
                    It.IsAny<LogLevel>(),
                    It.IsAny<EventId>(),
                    It.IsAny<ActionLog>(),
                    null,
                    It.IsAny<Func<ActionLog, Exception?, string>>()))
                .Callback<LogLevel, EventId, ActionLog, Exception?, Func<ActionLog, Exception?, string>>(
                    (_, _, log, _, _) => capturedLog = log);

            _service.DoLog("my msg",
                logtype: ActionLogTypesEnum.Normal,
                moduleName: "TestModule",
                actionName: "TestAction",
                ip: "127.0.0.1",
                url: "/api/test",
                duration: 123.45,
                userCode: "admin");

            capturedLog.Should().NotBeNull();
            capturedLog!.Remark.Should().Be("my msg");
            capturedLog.ModuleName.Should().Be("TestModule");
            capturedLog.ActionName.Should().Be("TestAction");
            capturedLog.IP.Should().Be("127.0.0.1");
            capturedLog.ActionUrl.Should().Be("/api/test");
            capturedLog.Duration.Should().Be(123.45);
            capturedLog.ITCode.Should().Be("admin");
        }

        #endregion

        #region Null logger safety

        [TestMethod]
        public void DoLog_NullLoggerFactory_DoesNotThrow()
        {
            var service = new WtmLogService(TimeProvider.System, null);

            Action act = () => service.DoLog("no logger");

            act.Should().NotThrow();
        }

        #endregion

        #region ExistingLog (SimpleLog) merge

        [TestMethod]
        public void DoLog_WithExistingLog_UsesAsBase()
        {
            ActionLog? capturedLog = null;
            _logger.Setup(x => x.Log(
                    It.IsAny<LogLevel>(),
                    It.IsAny<EventId>(),
                    It.IsAny<ActionLog>(),
                    null,
                    It.IsAny<Func<ActionLog, Exception?, string>>()))
                .Callback<LogLevel, EventId, ActionLog, Exception?, Func<ActionLog, Exception?, string>>(
                    (_, _, log, _, _) => capturedLog = log);

            var existingLog = new SimpleLog
            {
                ModuleName = "PreExisting",
                ActionName = "PreAction"
            };

            // Explicit parameters should override the existing log's values
            _service.DoLog("override msg", moduleName: "NewModule", existingLog: existingLog);

            capturedLog.Should().NotBeNull();
            capturedLog!.ModuleName.Should().Be("NewModule");
            capturedLog.Remark.Should().Be("override msg");
        }

        #endregion
    }
}
