#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.Mvc
{
    /// <summary>
    /// Tests for <see cref="WtmDeprecatedAttribute"/>. Exercises header
    /// emission, Sunset date formatting, class-level inheritance, log-opt-out,
    /// and malformed-Sunset resilience. Integration tests confirm MVC
    /// auto-discovers the attribute as an <c>IAsyncResultFilter</c>.
    /// </summary>
    [TestClass]
    public class WtmDeprecatedAttributeTests
    {
        // ── Integration via TestServer + MVC pipeline ────────────────────

        [TestMethod]
        public async Task Decorated_action_emits_deprecation_header()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/deprecated/simple");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.Contains("Deprecation"),
                "Decorated action must emit the Deprecation header.");
            Assert.AreEqual("true", response.Headers.GetValues("Deprecation").First());
        }

        [TestMethod]
        public async Task Undecorated_action_emits_no_deprecation_header()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/deprecated/fresh");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsFalse(response.Headers.Contains("Deprecation"));
            Assert.IsFalse(response.Headers.Contains("Sunset"));
            Assert.IsFalse(response.Headers.Contains("Link"));
        }

        [TestMethod]
        public async Task Sunset_date_is_formatted_as_rfc1123_http_date()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/deprecated/with-sunset");

            Assert.IsTrue(response.Headers.Contains("Sunset"));
            var sunset = response.Headers.GetValues("Sunset").First();
            // 2026-12-31 → "Thu, 31 Dec 2026 00:00:00 GMT"
            StringAssert.Contains(sunset, "31 Dec 2026");
            StringAssert.Contains(sunset, "GMT");
        }

        [TestMethod]
        public async Task Link_header_emits_verbatim_when_configured()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/deprecated/with-link");

            Assert.IsTrue(response.Headers.Contains("Link"));
            var link = response.Headers.GetValues("Link").First();
            StringAssert.Contains(link, "/api/v2/orders");
            StringAssert.Contains(link, "successor-version");
        }

        [TestMethod]
        public async Task Class_level_attribute_applies_to_all_actions_on_controller()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var a = await client.GetAsync("/classdep/first");
            var b = await client.GetAsync("/classdep/second");

            Assert.IsTrue(a.Headers.Contains("Deprecation"));
            Assert.IsTrue(b.Headers.Contains("Deprecation"));
        }

        [TestMethod]
        public async Task Invalid_sunset_string_is_silently_ignored_no_header()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/deprecated/bad-sunset");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.Contains("Deprecation"),
                "Malformed Sunset must not suppress the Deprecation header.");
            Assert.IsFalse(response.Headers.Contains("Sunset"),
                "Malformed Sunset must not emit the Sunset header.");
        }

        // ── Unit-level filter invocation ─────────────────────────────────
        //
        // Header-emission unit tests intentionally omitted: DefaultHttpContext
        // does not fire Response.OnStarting callbacks without a real server
        // pipeline, so asserting headers at the filter boundary would be
        // testing the ASP.NET Core test harness instead of the attribute.
        // The integration tests above (via TestServer + MapControllers)
        // already cover the full header path end-to-end.

        [TestMethod]
        public async Task LogHit_false_suppresses_log_entry()
        {
            var attr = new WtmDeprecatedAttribute { LogHit = false };
            var capture = new CapturingLoggerProvider();
            var (ctx, response) = BuildResultExecutingContext(capture);

            await attr.OnResultExecutionAsync(ctx, () => ResultExecutedNoOp(ctx));
            await response.StartAsync();

            Assert.AreEqual(0, capture.Records.Count(r => r.Category.StartsWith("WalkingTec.Mvvm.Mvc.WtmDeprecated")),
                "LogHit=false must suppress the informational hit log.");
        }

        [TestMethod]
        public async Task LogHit_default_true_emits_information_log_entry()
        {
            var attr = new WtmDeprecatedAttribute
            {
                Message = "Use v2 instead",
                Since = "10.5.0",
            };
            var capture = new CapturingLoggerProvider();
            var (ctx, response) = BuildResultExecutingContext(capture);

            await attr.OnResultExecutionAsync(ctx, () => ResultExecutedNoOp(ctx));
            await response.StartAsync();

            var matches = capture.Records.Where(r =>
                r.Category.StartsWith("WalkingTec.Mvvm.Mvc.WtmDeprecated")
                && r.LogLevel == LogLevel.Information).ToList();
            Assert.AreEqual(1, matches.Count, "LogHit default must emit exactly one Information-level entry.");
            StringAssert.Contains(matches[0].Message, "DeprecatedEndpointHit");
        }

        [TestMethod]
        public async Task OnResultExecutionAsync_rejects_null_context()
        {
            var attr = new WtmDeprecatedAttribute();
            await Assert.ThrowsExceptionAsync<ArgumentNullException>(async () =>
                await attr.OnResultExecutionAsync(null!, () => Task.FromResult<Microsoft.AspNetCore.Mvc.Filters.ResultExecutedContext>(null!)));
        }

        // ── Scaffolding ──────────────────────────────────────────────────

        private static async Task<IHost> BuildHostAsync()
        {
            var host = new HostBuilder()
                .ConfigureWebHost(w =>
                {
                    w.UseTestServer();
                    w.ConfigureServices(services =>
                    {
                        services.AddControllers()
                                .AddApplicationPart(typeof(WtmDeprecatedAttributeTests).Assembly);
                    });
                    w.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(e => e.MapControllers());
                    });
                })
                .Build();
            await host.StartAsync();
            return host;
        }

        private static (Microsoft.AspNetCore.Mvc.Filters.ResultExecutingContext ctx, Microsoft.AspNetCore.Http.HttpResponse response)
            BuildResultExecutingContext(ILoggerProvider? loggerProvider = null)
        {
            var services = new ServiceCollection();
            if (loggerProvider != null)
            {
                services.AddLogging(b => b.AddProvider(loggerProvider).SetMinimumLevel(LogLevel.Trace));
            }
            else
            {
                services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
            }
            var sp = services.BuildServiceProvider();

            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = sp };
            http.Request.Path = "/any";
            http.Request.Method = "GET";

            var actionContext = new Microsoft.AspNetCore.Mvc.ActionContext(
                http,
                new Microsoft.AspNetCore.Routing.RouteData(),
                new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());

            var ctx = new Microsoft.AspNetCore.Mvc.Filters.ResultExecutingContext(
                actionContext,
                Array.Empty<Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata>(),
                new OkResult(),
                new object());

            return (ctx, http.Response);
        }

        private static Task<Microsoft.AspNetCore.Mvc.Filters.ResultExecutedContext> ResultExecutedNoOp(
            Microsoft.AspNetCore.Mvc.Filters.ResultExecutingContext ctx)
        {
            var executed = new Microsoft.AspNetCore.Mvc.Filters.ResultExecutedContext(
                ctx, ctx.Filters, ctx.Result, ctx.Controller);
            return Task.FromResult(executed);
        }
    }

    // ── Fixture controllers used by integration tests ────────────────────

    [ApiController]
    [Route("deprecated")]
    public class WtmDeprecatedFixtureController : ControllerBase
    {
        [HttpGet("simple")]
        [WtmDeprecated(LogHit = false)]
        public IActionResult Simple() => Ok("simple");

        [HttpGet("fresh")]
        public IActionResult Fresh() => Ok("fresh");

        [HttpGet("with-sunset")]
        [WtmDeprecated(Sunset = "2026-12-31", LogHit = false)]
        public IActionResult WithSunset() => Ok("sunset");

        [HttpGet("with-link")]
        [WtmDeprecated(Link = "</api/v2/orders>; rel=\"successor-version\"", LogHit = false)]
        public IActionResult WithLink() => Ok("link");

        [HttpGet("bad-sunset")]
        [WtmDeprecated(Sunset = "not-a-date", LogHit = false)]
        public IActionResult BadSunset() => Ok("bad");
    }

    [ApiController]
    [Route("classdep")]
    [WtmDeprecated(Message = "Entire controller is scheduled for removal.", LogHit = false)]
    public class WtmDeprecatedClassLevelController : ControllerBase
    {
        [HttpGet("first")]
        public IActionResult First() => Ok("first");

        [HttpGet("second")]
        public IActionResult Second() => Ok("second");
    }

    // ── Capturing logger provider ────────────────────────────────────────

    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentBag<LogRecord> Records { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Records);
        public void Dispose() { }

        public readonly struct LogRecord
        {
            public LogRecord(string category, LogLevel level, string message)
            { Category = category; LogLevel = level; Message = message; }
            public string Category { get; }
            public LogLevel LogLevel { get; }
            public string Message { get; }
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly System.Collections.Concurrent.ConcurrentBag<LogRecord> _sink;

            public CapturingLogger(string category, System.Collections.Concurrent.ConcurrentBag<LogRecord> sink)
            { _category = category; _sink = sink; }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _sink.Add(new LogRecord(_category, logLevel, formatter(state, exception)));
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }
}
