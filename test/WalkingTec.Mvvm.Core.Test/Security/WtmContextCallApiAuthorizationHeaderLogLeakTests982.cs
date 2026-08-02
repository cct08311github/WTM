#nullable enable
// Security regression tests for Issue #982.
//
// #979 added a narrow `catch (FormatException)` around WTMContext.CallApi.cs's
// caller-supplied `headers` loop (see WtmContextCallApiHeaderLogLeakTests979.cs), so a
// CRLF/NUL-bearing header value in that dictionary produces a sanitized message naming only
// the header NAME and the exception TYPE, never the value. #982 is the scope gap in that fix:
// the narrow catch covers the LOOP, but not the `Authorization` add-point immediately after
// the loop closes (WTMContext.CallApi.cs, currently around line 77):
//
//     client.DefaultRequestHeaders.Add("Authorization", "Bearer " + LoginUserInfo?.RemoteToken);
//
// This is a DIFFERENT source than the #979 headers-loop leak -- LoginUserInfo.RemoteToken is
// populated from the LoginUserInfo property (see WTMContext.User.cs), not from the caller's
// `headers` dictionary -- so it needs its own regression test exercising this second,
// previously-unprotected add-point. Before the #982 fix, a RemoteToken containing CR/LF makes
// HttpHeaders.Add throw a FormatException whose .Message embeds the FULL raw attempted
// "Bearer <token>" value; that exception falls through to the method's pre-existing broad
// `catch (Exception ex)`, which hands `ex` whole to WtmDiagnosticLogger.LogError(...) -- a
// logging sink that renders Exception.ToString() (the .NET default) would put the token in
// application logs, exactly the #979 defect shape, just at the sibling add-point #979 missed.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Moq.Protected;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Security;

[TestClass]
public class WtmContextCallApiAuthorizationHeaderLogLeakTests982
{
    /// <summary>
    /// Same rationale as WtmContextCallApiHeaderLogLeakTests979.CapturingLogger: an ILogger
    /// sink typically renders both the formatted message AND, separately,
    /// exception.ToString() -- a test that only checked the message template would miss a leak
    /// carried purely in the exception object handed to LogError.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _records = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _records.Add((logLevel, formatter(state, exception), exception));
        }

        public string FullRenderedOutput =>
            string.Join(
                "\n",
                _records.Select(r => r.Message + (r.Exception != null ? "\n" + r.Exception : "")));
    }

    private sealed class FakeLoggerFactory : ILoggerFactory
    {
        public CapturingLogger Logger { get; } = new();
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => Logger;
        public void Dispose() { }
    }

    /// <summary>
    /// Builds a WTMContext whose IHttpClientFactory returns an HttpClient backed by a mock
    /// handler that counts invocations (never expected to fire when the header is rejected --
    /// the rejection happens before any request leaves the process), whose ILoggerFactory is
    /// the CapturingLogger above, and whose LoginUserInfo.RemoteToken carries the
    /// attacker-controlled value that reaches the unprotected Authorization add-point.
    /// </summary>
    private static (WTMContext wtm, CapturingLogger logger, Func<int> callCount) BuildContext(
        string remoteToken, HttpStatusCode? responseStatus = null)
    {
        int callCount = 0;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback(() => callCount++)
            .ReturnsAsync(new HttpResponseMessage(responseStatus ?? HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });

        var httpClient = new HttpClient(handler.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
        factoryMock.Setup(f => f.CreateClient(string.Empty)).Returns(httpClient);

        var loggerFactory = new FakeLoggerFactory();
        var spMock = new Mock<IServiceProvider>();
        spMock.Setup(sp => sp.GetService(typeof(IHttpClientFactory))).Returns(factoryMock.Object);
        spMock.Setup(sp => sp.GetService(typeof(ILoggerFactory))).Returns(loggerFactory);

        CoreProgram.DefaultJsonOption ??= new System.Text.Json.JsonSerializerOptions();

        var wtm = MockWtmContext.CreateWtmContext();
        wtm.ConfigInfo!.IsQuickDebug = false;
        wtm.SetServiceProvider(spMock.Object);
        // Drives WTMContext.CallApi.cs's `LoginUserInfo?.RemoteToken` read directly, bypassing
        // the already-protected `headers` loop entirely -- this is the second, independent
        // source #979 did not cover.
        wtm.LoginUserInfo = new LoginUserInfo { RemoteToken = remoteToken };

        return (wtm, loggerFactory.Logger, () => callCount);
    }

    // Deleting the `try`/`catch (FormatException ex)` wrapping
    // `client.DefaultRequestHeaders.Add("Authorization", "Bearer " + LoginUserInfo?.RemoteToken)`
    // in WTMContext.CallApi.cs (letting the original FormatException fall through to the broad
    // `catch (Exception ex)` unmodified) is the single change that turns this test red again --
    // see test/mutants/entries/982-callapi-authorization-header-log-leak-reintroduce.json.
    [TestMethod]
    public async Task CallAPI_RemoteToken_with_embedded_newline_does_not_leak_value_into_log()
    {
        const string secretFragment = "s3cr3t-remotetoken-A1B2C3-do-not-log-me";
        var (wtm, logger, callCount) = BuildContext($"{secretFragment}\nX-Injected: evil");

        var result = await wtm.CallAPI<string>(null, "http://test/api");

        callCount().Should().Be(0,
            "the Authorization header rejection must happen before any request leaves the process");
        logger.FullRenderedOutput.Should().NotContain(secretFragment,
            "the secret must never reach the log, whether via the message template or the logged exception's own text");
        result.ErrorMsg.Should().NotContain(secretFragment,
            "the ApiResult returned to the caller must not carry the secret either");
    }

    /// <summary>
    /// Positive control: without this, a "fix" that silently swallowed the whole exception (or
    /// the message template) -- logging nothing useful at all -- would make the leak test above
    /// pass for the wrong reason. The header NAME must still reach the log: an operator who
    /// cannot see which header was rejected cannot fix their configuration.
    /// </summary>
    [TestMethod]
    public async Task CallAPI_still_logs_the_rejected_Authorization_header_name()
    {
        var (wtm, logger, _) = BuildContext("irrelevant-token\nX-Injected: evil");

        await wtm.CallAPI<string>(null, "http://test/api");

        logger.FullRenderedOutput.Should().Contain("Authorization",
            "an operator must still be able to tell WHICH header was rejected");
    }
}
