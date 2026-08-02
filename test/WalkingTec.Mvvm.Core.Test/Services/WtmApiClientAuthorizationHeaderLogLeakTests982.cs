#nullable enable
// Security regression tests for Issue #982.
//
// #979 added a narrow `catch (FormatException)` around WtmApiClient.cs's caller-supplied
// `headers` loop (see WtmApiClientHeaderLogLeakTests979.cs). #982 is the scope gap in that
// fix: the narrow catch covers the LOOP, but not the `Authorization` add-point immediately
// after the loop closes (WtmApiClient.cs, currently around line 93):
//
//     client.DefaultRequestHeaders.Add("Authorization", "Bearer " + authToken);
//
// Unlike the sibling WTMContext.CallApi.cs gap (whose value comes from
// LoginUserInfo.RemoteToken), `authToken` here is a plain public method parameter on
// WtmApiClient.CallAPI<T> -- directly caller-controlled, no fixture indirection needed. Before
// the #982 fix, an authToken containing CR/LF makes HttpHeaders.Add throw a FormatException
// whose .Message embeds the FULL raw attempted "Bearer <token>" value; that exception falls
// through to the method's pre-existing broad `catch (Exception ex)`, which hands `ex` whole to
// _logger?.LogError(...) -- a logging sink that renders Exception.ToString() (the .NET default)
// would put the token in application logs, exactly the #979 defect shape, just at the sibling
// add-point #979 missed.

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
public class WtmApiClientAuthorizationHeaderLogLeakTests982
{
    /// <summary>
    /// Same rationale as WtmApiClientHeaderLogLeakTests979.CapturingLogger: an ILogger sink
    /// typically renders both the formatted message AND, separately, exception.ToString() -- a
    /// test that only checked the message template would miss a leak carried purely in the
    /// exception object handed to LogError.
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

    // Deleting the `try`/`catch (FormatException ex)` wrapping
    // `client.DefaultRequestHeaders.Add("Authorization", "Bearer " + authToken)` in
    // WtmApiClient.cs (letting the original FormatException fall through to the broad
    // `catch (Exception ex)` unmodified) is the single change that turns this test red again --
    // see test/mutants/entries/982-callapi-authorization-header-log-leak-reintroduce.json.
    [TestMethod]
    public async Task CallAPI_authToken_with_embedded_newline_does_not_leak_value_into_log()
    {
        const string secretFragment = "s3cr3t-authtoken-A1B2C3-do-not-log-me";
        var (client, logger, callCount) = BuildClient();

        var result = await client.CallAPI<string>(null, "http://test/api", HttpMethodEnum.GET,
            (HttpContent?)null, authToken: $"{secretFragment}\nX-Injected: evil");

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
    /// pass for the wrong reason. The header NAME must still reach the log.
    /// </summary>
    [TestMethod]
    public async Task CallAPI_still_logs_the_rejected_Authorization_header_name()
    {
        var (client, logger, _) = BuildClient();

        await client.CallAPI<string>(null, "http://test/api", HttpMethodEnum.GET,
            (HttpContent?)null, authToken: "irrelevant-token\nX-Injected: evil");

        logger.FullRenderedOutput.Should().Contain("Authorization",
            "an operator must still be able to tell WHICH header was rejected");
    }
}
