#nullable enable
// Security regression tests for Issue #979.
//
// WTMContext.CallAPI<T> loops over the caller-supplied `headers` dictionary and calls
// `client.DefaultRequestHeaders.Add(item.Key, item.Value)` with NO header-specific catch.
// For a parser-backed header -- Authorization is the notable one -- .NET's own
// FormatException.Message embeds the FULL raw attempted value (confirmed on .NET 10 with a
// standalone probe before writing this fix; see docs/production-readiness.md for the full
// probe transcript). Before the fix, that FormatException falls straight through to this
// method's own broad `catch (Exception ex)`, which hands `ex` whole to
// `WtmDiagnosticLogger.LogError(ex, ...)` -- a logging sink that renders `Exception.ToString()`
// (the .NET default) would put the secret in application logs. Unlike #961 (RestWidgetDataSource
// / RestEtlSource), there was no header-specific catch to fix here at all -- the fix has to
// INSERT a narrow `catch (FormatException)` inside the existing broad catch, which is a
// different shape and a larger blast radius than deleting a chained `, ex`. This file also
// proves that insertion does not change how the pre-existing broad catch handles anything else.

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
public class WtmContextCallApiHeaderLogLeakTests979
{
    /// <summary>
    /// Approximates a real logging sink: <see cref="ILogger"/>'s contract hands both the
    /// templated message AND the exception object to <c>Log(...)</c> separately -- a sink then
    /// typically writes the message, followed by <c>exception.ToString()</c> (which recurses
    /// into <c>InnerException</c> if any). A test that only inspected the formatted message
    /// would miss a leak carried purely in the exception object.
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
    /// the rejection happens before any request leaves the process) and whose ILoggerFactory
    /// is the CapturingLogger above, so we can inspect exactly what WtmDiagnosticLogger emits.
    /// </summary>
    private static (WTMContext wtm, CapturingLogger logger, Func<int> callCount) BuildContext(
        HttpStatusCode? responseStatus = null)
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

        return (wtm, loggerFactory.Logger, () => callCount);
    }

    // Deleting the `try`/`catch (FormatException ex)` around
    // `client.DefaultRequestHeaders.Add(item.Key, item.Value)` in WTMContext.CallApi.cs (letting
    // the original FormatException fall through to the broad `catch (Exception ex)` unmodified)
    // is the single change that turns this test red again -- see
    // test/mutants/entries/979-callapi-header-exception-leak-reintroduce.json.
    [TestMethod]
    public async Task CallAPI_header_value_with_embedded_newline_does_not_leak_value_into_log()
    {
        const string secretFragment = "s3cr3t-A1B2C3-do-not-log-me";
        var (wtm, logger, callCount) = BuildContext();

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {secretFragment}\nX-Injected: evil"
        };

        var result = await wtm.CallAPI<string>(null, "http://test/api", headers: headers);

        callCount().Should().Be(0,
            "the header rejection must happen before any request leaves the process");
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
    public async Task CallAPI_still_logs_the_rejected_header_name()
    {
        var (wtm, logger, _) = BuildContext();

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer irrelevant-value\nX-Injected: evil"
        };

        await wtm.CallAPI<string>(null, "http://test/api", headers: headers);

        logger.FullRenderedOutput.Should().Contain("Authorization",
            "an operator must still be able to tell WHICH header was rejected");
    }

    /// <summary>
    /// Proves the new narrow `catch (FormatException)` around the header-add call does not
    /// change how the pre-existing broad `catch (Exception ex)` handles an exception that has
    /// nothing to do with headers. An HttpRequestException thrown by the HTTP stack while
    /// actually sending the request (no headers involved) must still be caught by the broad
    /// catch, logged in full (this is NOT a header-value leak -- it is the documented,
    /// intentional behaviour from issue #124: WtmDiagnosticLogger.LogError(ex, ...) always
    /// receives the full exception; IsQuickDebug controls only what reaches the CALLER), and
    /// must still produce the same generic ErrorMsg contract.
    /// </summary>
    [TestMethod]
    public async Task CallAPI_non_header_exception_is_still_handled_by_the_broad_catch_unchanged()
    {
        const string sentinel = "connection-refused-sentinel-42";
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException(sentinel));

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

        // No `headers` argument at all -- this exercises a code path the #979 fix never
        // touches, to isolate "did adding the narrow header catch change unrelated behaviour".
        var result = await wtm.CallAPI<string>(null, "http://test/api");

        result.ErrorMsg.Should().Be("An error occurred while processing the request.",
            "the broad catch's generic-message contract for non-header exceptions is unchanged");
        loggerFactory.Logger.FullRenderedOutput.Should().Contain(sentinel,
            "for a NON-header exception the broad catch still logs the full exception exactly as before #979 -- " +
            "only the header-specific FormatException path was narrowed");
    }
}
