#nullable enable
// Security regression tests for Issue #979.
//
// WtmApiClient.CallAPI<T> loops over the caller-supplied `headers` dictionary and calls
// `client.DefaultRequestHeaders.Add(item.Key, item.Value)` with NO header-specific catch --
// the byte-for-byte same shape as WTMContext.CallApi.cs (see
// WtmContextCallApiHeaderLogLeakTests979.cs for the full explanation of the defect and why the
// fix is shaped differently from #961). Before the fix, a parser-backed header's FormatException
// falls straight through to this method's own broad `catch (Exception ex)`, which hands `ex`
// whole to `_logger?.LogError(ex, ...)`.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Moq.Protected;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Services;

namespace WalkingTec.Mvvm.Core.Test.Services;

[TestClass]
public class WtmApiClientHeaderLogLeakTests979
{
    /// <summary>
    /// Same rationale as the CapturingLogger in WtmContextCallApiHeaderLogLeakTests979: an
    /// ILogger sink typically renders both the formatted message AND, separately,
    /// exception.ToString() -- a test that only checked the message template would miss a leak
    /// carried purely in the exception object handed to LogError.
    /// </summary>
    private sealed class CapturingLogger : ILogger<WtmApiClient>
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

    private static (WtmApiClient client, CapturingLogger logger, Func<int> callCount) BuildClient(
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

        CoreProgram.DefaultJsonOption ??= new System.Text.Json.JsonSerializerOptions();

        var logger = new CapturingLogger();
        var client = new WtmApiClient(factoryMock.Object, logger);
        return (client, logger, () => callCount);
    }

    // Deleting the `try`/`catch (FormatException ex)` around
    // `client.DefaultRequestHeaders.Add(item.Key, item.Value)` in WtmApiClient.cs (letting the
    // original FormatException fall through to the broad `catch (Exception ex)` unmodified) is
    // the single change that turns this test red again -- see
    // test/mutants/entries/979-callapi-header-exception-leak-reintroduce.json (the mutant
    // targets WTMContext.CallApi.cs; this file's own coverage is the second line of defense for
    // the byte-for-byte identical defect in WtmApiClient.cs, exercised directly rather than via
    // the shared mutant to keep the mutant single-file per test/mutants/run_mutant.py's scope
    // check).
    [TestMethod]
    public async Task CallAPI_header_value_with_embedded_newline_does_not_leak_value_into_log()
    {
        const string secretFragment = "s3cr3t-A1B2C3-do-not-log-me";
        var (client, logger, callCount) = BuildClient();

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {secretFragment}\nX-Injected: evil"
        };

        var result = await client.CallAPI<string>(null, "http://test/api", HttpMethodEnum.GET,
            (HttpContent?)null, headers: headers);

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
    /// pass for the wrong reason. The header NAME must still reach the log.
    /// </summary>
    [TestMethod]
    public async Task CallAPI_still_logs_the_rejected_header_name()
    {
        var (client, logger, _) = BuildClient();

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer irrelevant-value\nX-Injected: evil"
        };

        await client.CallAPI<string>(null, "http://test/api", HttpMethodEnum.GET,
            (HttpContent?)null, headers: headers);

        logger.FullRenderedOutput.Should().Contain("Authorization",
            "an operator must still be able to tell WHICH header was rejected");
    }

    /// <summary>
    /// Proves the new narrow `catch (FormatException)` around the header-add call does not
    /// change how the pre-existing broad `catch (Exception ex)` handles an exception unrelated
    /// to headers -- mirrors WtmApiClientTests.CallAPI_HttpException_ReturnsGenericErrorMsg but
    /// additionally asserts the LOGGED exception (not just the caller-facing ErrorMsg) still
    /// carries the full original detail, since that is exactly the behaviour #979 must not
    /// disturb for non-header exceptions.
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

        CoreProgram.DefaultJsonOption ??= new System.Text.Json.JsonSerializerOptions();
        var logger = new CapturingLogger();
        var client = new WtmApiClient(factoryMock.Object, logger);

        // No `headers` argument at all -- exercises a code path #979's fix never touches.
        var result = await client.CallAPI<string>(null, "http://test/api");

        result.ErrorMsg.Should().Be("An error occurred while processing the request",
            "the broad catch's generic-message contract for non-header exceptions is unchanged");
        logger.FullRenderedOutput.Should().Contain(sentinel,
            "for a NON-header exception the broad catch still logs the full exception exactly as before #979 -- " +
            "only the header-specific FormatException path was narrowed");
    }
}
