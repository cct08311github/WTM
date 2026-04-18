#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.Mvc
{
    /// <summary>
    /// Tests for issue #840 — slow-request logging middleware. Verifies
    /// threshold-gated logging, path exclusions, opt-in query string, and
    /// log-level configurability.
    /// </summary>
    [TestClass]
    public class WtmSlowRequestMiddlewareTests
    {
        [TestMethod]
        public async Task Under_threshold_does_not_log()
        {
            var sink = new ListLoggerProvider();
            using var host = await BuildHostAsync(sink, opt => opt.ThresholdMs = 500);

            var client = host.GetTestClient();
            var response = await client.GetAsync("/");

            Assert.IsTrue(response.IsSuccessStatusCode);
            var slowLogs = sink.Entries.Where(e => e.Message.Contains("SlowRequest")).ToList();
            Assert.AreEqual(0, slowLogs.Count,
                "fast request must not emit slow-request log");
        }

        [TestMethod]
        public async Task Over_threshold_emits_structured_log()
        {
            var sink = new ListLoggerProvider();
            using var host = await BuildHostAsync(
                sink,
                opt => opt.ThresholdMs = 50,
                handlerDelayMs: 120);

            var client = host.GetTestClient();
            var response = await client.GetAsync("/api/slow");

            Assert.IsTrue(response.IsSuccessStatusCode);
            var slow = sink.Entries.FirstOrDefault(e => e.Message.Contains("SlowRequest"));
            Assert.IsNotNull(slow, "over-threshold request must emit slow-request log. Entries=" +
                string.Join("|", sink.Entries.Select(e => e.Message)));
            Assert.AreEqual(LogLevel.Warning, slow!.Level);

            // Structured properties must land with stable names for log
            // aggregator pivots.
            var state = slow.StateProperties;
            Assert.IsTrue(state.ContainsKey("Path"), "Path property missing");
            Assert.IsTrue(state.ContainsKey("Method"));
            Assert.IsTrue(state.ContainsKey("StatusCode"));
            Assert.IsTrue(state.ContainsKey("Elapsed"));
            Assert.AreEqual("GET", state["Method"]);
            Assert.AreEqual("/api/slow", state["Path"]);
        }

        [TestMethod]
        public async Task Path_exclusion_skips_logging_even_when_slow()
        {
            var sink = new ListLoggerProvider();
            using var host = await BuildHostAsync(
                sink,
                opt =>
                {
                    opt.ThresholdMs = 50;
                    opt.PathExclusions = new List<string> { "/healthz" };
                },
                handlerDelayMs: 120);

            var client = host.GetTestClient();
            await client.GetAsync("/healthz/ready");

            Assert.AreEqual(0, sink.Entries.Count(e => e.Message.Contains("SlowRequest")),
                "excluded path must not emit log");
        }

        [TestMethod]
        public async Task QueryString_excluded_by_default()
        {
            var sink = new ListLoggerProvider();
            using var host = await BuildHostAsync(
                sink,
                opt => opt.ThresholdMs = 50,
                handlerDelayMs: 120);

            var client = host.GetTestClient();
            await client.GetAsync("/api/secret?token=supersecret");

            var slow = sink.Entries.First(e => e.Message.Contains("SlowRequest"));
            var path = (string)slow.StateProperties["Path"];
            Assert.IsFalse(path.Contains("token=supersecret"),
                "query string must NOT appear in structured log by default (PII safety)");
            Assert.IsFalse(path.Contains("?"),
                "query string marker must be stripped");
        }

        [TestMethod]
        public async Task QueryString_included_when_opted_in()
        {
            var sink = new ListLoggerProvider();
            using var host = await BuildHostAsync(
                sink,
                opt =>
                {
                    opt.ThresholdMs = 50;
                    opt.IncludeQueryString = true;
                },
                handlerDelayMs: 120);

            var client = host.GetTestClient();
            await client.GetAsync("/api/search?q=apple");

            var slow = sink.Entries.First(e => e.Message.Contains("SlowRequest"));
            var path = (string)slow.StateProperties["Path"];
            StringAssert.Contains(path, "q=apple");
        }

        [TestMethod]
        public async Task Log_level_is_configurable()
        {
            var sink = new ListLoggerProvider();
            using var host = await BuildHostAsync(
                sink,
                opt =>
                {
                    opt.ThresholdMs = 50;
                    opt.LogLevel = LogLevel.Error;
                },
                handlerDelayMs: 120);

            var client = host.GetTestClient();
            await client.GetAsync("/api/slow");

            var slow = sink.Entries.First(e => e.Message.Contains("SlowRequest"));
            Assert.AreEqual(LogLevel.Error, slow.Level);
        }

        [TestMethod]
        public void Middleware_throws_on_null_options()
        {
            var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<WtmSlowRequestMiddleware>.Instance;
            Assert.ThrowsException<ArgumentNullException>(() =>
                new WtmSlowRequestMiddleware(
                    _ => Task.CompletedTask,
                    null!,
                    logger));
        }

        [TestMethod]
        public void UseWtmSlowRequestLogging_throws_on_null_configure()
        {
            var builder = new Microsoft.AspNetCore.Builder.ApplicationBuilder(
                new ServiceCollection().BuildServiceProvider());
            Assert.ThrowsException<ArgumentNullException>(() =>
                WtmSlowRequestExtension.UseWtmSlowRequestLogging(builder, null!));
        }

        // ── Test scaffolding ──────────────────────────────────────────────

        private static async Task<IHost> BuildHostAsync(
            ListLoggerProvider sink,
            Action<WtmSlowRequestOptions>? configure = null,
            int handlerDelayMs = 0)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddLogging(lb =>
                        {
                            lb.ClearProviders();
                            lb.AddProvider(sink);
                            lb.SetMinimumLevel(LogLevel.Trace);
                        });
                    });
                    webBuilder.Configure(app =>
                    {
                        if (configure != null) app.UseWtmSlowRequestLogging(configure);
                        else app.UseWtmSlowRequestLogging();

                        app.Run(async ctx =>
                        {
                            if (handlerDelayMs > 0)
                                await Task.Delay(handlerDelayMs);
                            await ctx.Response.WriteAsync("ok");
                        });
                    });
                })
                .Build();
            await host.StartAsync();
            return host;
        }

        /// <summary>
        /// In-memory ILoggerProvider that keeps every log entry for
        /// assertion. Structured state keys are exposed as a dictionary
        /// so tests can pivot on {Path} / {Method} / etc.
        /// </summary>
        public sealed class ListLoggerProvider : ILoggerProvider
        {
            public List<Entry> Entries { get; } = new();

            public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, this);
            public void Dispose() { }

            public sealed class Entry
            {
                public string Category { get; init; } = "";
                public LogLevel Level { get; init; }
                public string Message { get; init; } = "";
                public IReadOnlyDictionary<string, object?> StateProperties { get; init; } =
                    new Dictionary<string, object?>();
            }

            public sealed class ListLogger : ILogger
            {
                private readonly string _category;
                private readonly ListLoggerProvider? _parent;

                public ListLogger(string category) { _category = category; _parent = null; }
                public ListLogger(string category, ListLoggerProvider parent)
                {
                    _category = category; _parent = parent;
                }

                public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
                    NullDisposable.Instance;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                    Exception? exception, Func<TState, Exception?, string> formatter)
                {
                    if (_parent == null) return;
                    var props = new Dictionary<string, object?>();
                    if (state is IReadOnlyList<KeyValuePair<string, object?>> kvps)
                    {
                        foreach (var kv in kvps) props[kv.Key] = kv.Value;
                    }
                    _parent.Entries.Add(new Entry
                    {
                        Category = _category,
                        Level = logLevel,
                        Message = formatter(state, exception),
                        StateProperties = props,
                    });
                }

                private sealed class NullDisposable : IDisposable
                {
                    public static readonly NullDisposable Instance = new();
                    public void Dispose() { }
                }
            }
        }
    }
}
