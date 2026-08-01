#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Dashboard;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    /// <summary>
    /// Issue #961: <see cref="RestWidgetDataSource"/>'s header-adding try/catch (in
    /// <c>FetchJsonAsync</c>, reached via <c>GetDataAsync</c>) chained the ORIGINAL
    /// <see cref="FormatException"/>/<see cref="InvalidOperationException"/> thrown by
    /// <c>HttpRequestMessage.Headers.Add</c> as the <c>InnerException</c> of the
    /// <see cref="InvalidOperationException"/> it re-threw.
    /// <para>
    /// For a parser-backed header — <c>Authorization</c> is the notable one — .NET's own
    /// <see cref="FormatException"/>.Message embeds the FULL raw attempted value. Confirmed on
    /// .NET 10 with a standalone probe before writing this fix:
    /// </para>
    /// <code>
    /// THROW Authorization :: FormatException: The format of value 'Bearer my-real-secret-token
    /// X-Injected: 1' is invalid.
    /// THROW X-Api-Key     :: FormatException: New-line or NUL characters are not allowed in header values.
    /// </code>
    /// <para>
    /// <c>_DashboardController.GetWidgetData</c>/<c>PostWidgetData</c> and
    /// <c>_DashboardDesignerController.Preview</c> catch that <see cref="InvalidOperationException"/>
    /// and pass it WHOLE to <c>ILogger.LogWarning(ex, ...)</c>. A logging sink that renders
    /// <c>Exception.ToString()</c> (the .NET default — what console/file sinks most deployments
    /// actually use) walks the InnerException chain, so chaining the original exception put the
    /// secret in application logs from nothing more hostile than an operator mistyping a
    /// credential (an embedded newline from a paste, a stray control character) — no attack
    /// required.
    /// </para>
    /// <para>
    /// <b>Deviation from the reported non-ASCII vector, verified empirically:</b> plain
    /// non-ASCII text alone (Chinese characters, a full-width space, Latin-1 supplement
    /// characters) does NOT throw <see cref="FormatException"/> on .NET 10 —
    /// <c>HttpRequestHeaders.Add</c>'s value validation only rejects embedded CR, LF, and NUL
    /// (matching the unknown-header generic message text itself: "New-line or NUL characters
    /// are not allowed in header values."). The non-ASCII RED test below therefore combines
    /// non-ASCII text with an embedded NUL character (a raw 0x00 byte) so it both (a) actually reaches the
    /// FormatException path being fixed, and (b) still exercises a raw value carrying
    /// non-ASCII content end-to-end. Also verified via the same probe:
    /// </para>
    /// <code>
    /// THROW Authorization [Chinese + NUL] :: FormatException: The format of value 'Bearer
    /// 機密憑證ABCtoken' is invalid.  (the NUL renders invisibly between "ABC" and "token")
    /// </code>
    /// </summary>
    [TestClass]
    public class RestWidgetHeaderValueLogLeakTests961
    {
        /// <summary>
        /// Approximates a real logging sink: <see cref="ILogger{TCategoryName}"/>'s contract
        /// hands both the templated message AND the exception object to <c>Log(...)</c>
        /// separately — a sink then typically writes the message, followed by
        /// <c>exception.ToString()</c> (which recurses into <c>InnerException</c>). A test that
        /// only inspected <c>formatter(state, exception)</c> would miss the leak entirely,
        /// since none of the message templates here ever embed <c>{Exception}</c> — the
        /// exception's OWN text is what a sink prints separately.
        /// </summary>
        private sealed class CapturingLogger : ILogger<_DashboardController>
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

        private static (_DashboardController controller, CapturingLogger logger) BuildController(
            Mock<IDashboardService> service)
        {
            var opts = Options.Create(new DashboardOptions());
            var logger = new CapturingLogger();
            var controller = new _DashboardController(
                service.Object, opts, Enumerable.Empty<IWidgetDataSource>(),
                registry: null!, logger: logger)
            {
                Wtm = MockWtmContext.CreateWtmContext()
            };
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
            controller.Wtm.LoginUserInfo = new LoginUserInfo { ITCode = "bob" };
            controller.Wtm.LoginUserInfo.Roles = new List<SimpleRole>();
            return (controller, logger);
        }

        /// <summary>
        /// Drives the REAL <see cref="RestWidgetDataSource"/> (not a hand-rolled stand-in) so
        /// the exception under test is exactly what production code throws today. The mock
        /// HTTP handler must never be invoked — the header rejection has to happen before any
        /// bytes leave the process, same invariant as <c>RestWidgetHeaderHardeningTests</c>.
        /// </summary>
        private static async Task<InvalidOperationException> CaptureRealRestWidgetExceptionAsync(
            string headerValue, string headerName = "Authorization")
        {
            int callCount = 0;
            var handler = new MockHttpHandler(_ =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });
            var factory = new SingleClientFactory(handler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var source = new RestWidgetDataSource(factory, cache);

            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions
                    {
                        Url = "https://8.8.8.8/data",
                        Headers = new Dictionary<string, string> { [headerName] = headerValue },
                        CacheTtlSeconds = 0
                    })
                }
            };

            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => source.GetDataAsync(req, CancellationToken.None));
            Assert.AreEqual(0, callCount,
                "the HTTP handler must never be invoked — rejection happens before any request leaves the process");
            return ex;
        }

        private static async Task<CapturingLogger> RunGetWidgetDataAsync(InvalidOperationException realEx)
        {
            var service = new Mock<IDashboardService>();
            var def = new DashboardDefinition
            {
                Id = "d1",
                Owner = "bob",
                Widgets = new Dictionary<string, WidgetDefinition> { ["w1"] = new WidgetDefinition() }
            };
            service.Setup(x => x.GetAsync("d1", It.IsAny<string?>())).ReturnsAsync(def);
            service.Setup(x => x.CanAccess(def, "bob", It.IsAny<string[]>())).Returns(true);
            service
                .Setup(x => x.GetWidgetDataAsync(
                    "d1", "w1", It.IsAny<Dictionary<string, string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(realEx);

            var (controller, logger) = BuildController(service);

            var result = await controller.GetWidgetData("d1", "w1", CancellationToken.None);
            result.Should().BeOfType<ObjectResult>()
                .Which.StatusCode.Should().Be(StatusCodes.Status502BadGateway);

            return logger;
        }

        // Deleting the "throw new InvalidOperationException(...)" in RestWidgetDataSource's
        // header catch block and putting back ", ex" (chaining the original exception) is the
        // single line whose reintroduction turns this test red again — see
        // test/mutants/entries/961-restwidget-header-exception-chain-reintroduce.json.
        [TestMethod]
        public async Task GetWidgetData_header_value_with_embedded_newline_does_not_leak_value_into_log()
        {
            const string secretFragment = "s3cr3t-A1B2C3-do-not-log-me";
            var realEx = await CaptureRealRestWidgetExceptionAsync($"Bearer {secretFragment}\nX-Injected: evil");

            var logger = await RunGetWidgetDataAsync(realEx);

            logger.FullRenderedOutput.Should().NotContain(secretFragment,
                "the secret must never reach the log, whether via the message template or the logged exception's own text");
        }

        [TestMethod]
        public async Task GetWidgetData_header_value_with_nonascii_and_nul_does_not_leak_value_into_log()
        {
            const string secretFragment = "機密憑證A1B2C3";
            var realEx = await CaptureRealRestWidgetExceptionAsync($"Bearer {secretFragment} trailer");

            var logger = await RunGetWidgetDataAsync(realEx);

            logger.FullRenderedOutput.Should().NotContain(secretFragment,
                "the secret must never reach the log, whether via the message template or the logged exception's own text");
        }

        /// <summary>
        /// Positive control: without this, a "fix" that silently swallowed the whole
        /// exception (or the message template) — logging nothing useful at all — would make
        /// both leak tests above pass for the wrong reason. The header NAME must still reach
        /// the log: an operator who cannot see which header was rejected cannot fix their
        /// configuration.
        /// </summary>
        [TestMethod]
        public async Task GetWidgetData_still_logs_the_rejected_header_name()
        {
            var realEx = await CaptureRealRestWidgetExceptionAsync("Bearer irrelevant-value\nX-Injected: evil");

            var logger = await RunGetWidgetDataAsync(realEx);

            logger.FullRenderedOutput.Should().Contain("Authorization",
                "an operator must still be able to tell WHICH header was rejected");
        }
    }
}
