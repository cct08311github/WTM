#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// Tests for issue #836 — framework-owned DataContext health check
    /// plus JSON response writer. The check is integration-tested against
    /// a real SQLite in-memory DB (happy path). Unhealthy / malformed
    /// scenarios are unit-tested against the response writer directly by
    /// fabricating <see cref="HealthReport"/> entries — avoids mocking the
    /// EF Core <c>DatabaseFacade</c>, which is not a clean test seam.
    /// </summary>
    [TestClass]
    public class WtmHealthCheckEnhancementsTests
    {
        // ── AddWtmDataContextCheck argument validation ────────────────────

        [TestMethod]
        public void AddWtmDataContextCheck_throws_on_null_builder()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                WtmHealthCheckExtensions.AddWtmDataContextCheck(null!));
        }

        // ── DataContext health check unit ─────────────────────────────────

        [TestMethod]
        public async Task DataContextCheck_returns_healthy_when_CanConnect_true()
        {
            using var dc = InMemoryDataContext.Create();
            var check = new WtmDataContextHealthCheck(dc, null, TimeSpan.FromSeconds(2));
            var result = await check.CheckHealthAsync(new HealthCheckContext());
            Assert.AreEqual(HealthStatus.Healthy, result.Status);
            Assert.IsTrue(result.Data.ContainsKey("dbType"));
        }

        [TestMethod]
        public async Task DataContextCheck_defaults_timeout_on_nonpositive_input()
        {
            using var dc = InMemoryDataContext.Create();
            // TimeSpan.Zero → constructor should coerce to a sane default
            var check = new WtmDataContextHealthCheck(dc, null, TimeSpan.Zero);
            var result = await check.CheckHealthAsync(new HealthCheckContext());
            Assert.AreEqual(HealthStatus.Healthy, result.Status);
        }

        [TestMethod]
        public void DataContextCheck_throws_on_null_dc()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                new WtmDataContextHealthCheck(null!, null, TimeSpan.FromSeconds(1)));
        }

        // ── NullContext guard — issue #441 ────────────────────────────────

        /// <summary>
        /// WTM's DI default registers NullContext as IDataContext.
        /// Accessing NullContext.DBType or .Database throws NotImplementedException.
        /// The health check must detect this and return Healthy (skipped) rather
        /// than letting the exception escape and permanently mark /ready as Unhealthy.
        /// </summary>
        [TestMethod]
        public async Task DataContextCheck_NullContext_returns_healthy_not_throwing()
        {
            // Arrange — NullContext is the WTM DI sentinel; every member throws NotImplementedException.
            // No WTMContext is supplied here, so ResolveDataContext falls back to the DI dc (NullContext).
            var nullDc = new NullContext();
            var check = new WtmDataContextHealthCheck(nullDc, null, TimeSpan.FromSeconds(2));

            // Act — must NOT throw, must NOT return Unhealthy
            var result = await check.CheckHealthAsync(new HealthCheckContext());

            // Assert
            Assert.AreNotEqual(HealthStatus.Unhealthy, result.Status,
                "NullContext must not cause a false Unhealthy (false 503) — fix #441");
            Assert.IsNotNull(result.Description,
                "A description explaining the skip should be present");
        }

        [TestMethod]
        public async Task DataContextCheck_NullContext_description_mentions_no_DataContext()
        {
            var nullDc = new NullContext();
            var check = new WtmDataContextHealthCheck(nullDc, null, TimeSpan.FromSeconds(2));
            var result = await check.CheckHealthAsync(new HealthCheckContext());

            StringAssert.Contains(result.Description, "DataContext",
                "Description should mention DataContext so operators know why it was skipped");
        }

        [TestMethod]
        public async Task DataContextCheck_NullContext_does_not_populate_dbType_data_key()
        {
            // When NullContext is in use, we short-circuit before reading .DBType,
            // so the data dictionary should NOT contain the dbType key (it would throw).
            var nullDc = new NullContext();
            var check = new WtmDataContextHealthCheck(nullDc, null, TimeSpan.FromSeconds(2));
            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.IsFalse(result.Data.ContainsKey("dbType"),
                "No dbType key expected when NullContext short-circuits (would throw if accessed)");
        }

        // ── WTMContext-first resolution — issue #741 (residual of #727) ───

        /// <summary>
        /// #741: <c>AddTypeActivatedCheck</c> constructs this check inside a fresh DI scope on
        /// every probe. Before the fix, constructor-injecting bare <see cref="IDataContext"/>
        /// ALWAYS resolved WTM's <c>NullContext</c> placeholder in a real deployment (WTM never
        /// replaces the DI <see cref="IDataContext"/> registration — apps get their real
        /// DataContext through <see cref="WTMContext.CreateDC"/>), so the check silently reported
        /// "Healthy (skipped)" even when a real, reachable database was configured. This proves
        /// the check now prefers the WTMContext-resolved real DataContext over the DI placeholder.
        /// </summary>
        [TestMethod]
        public async Task DataContextCheck_WtmContextAvailable_ProbesRealDbContext_NotNullContextSkip()
        {
            // Arrange — mirrors AddWtmContext's actual DI shape: IDataContext -> NullContext
            // placeholder, plus a WTMContext whose CreateDC() resolves a real DataContext.
            using var realDc = InMemoryDataContext.Create();
            var wtm = new FakeWtmContext(realDc);
            var nullDc = new NullContext();

            var check = new WtmDataContextHealthCheck(nullDc, wtm, TimeSpan.FromSeconds(2));
            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.AreEqual(HealthStatus.Healthy, result.Status);
            Assert.AreEqual("DataContext connection OK.", result.Description,
                "Before #741 this reported the NullContext 'skipped' description even though a " +
                "real WTMContext-resolved DataContext was available — the DB was never probed.");
            Assert.IsTrue(result.Data.ContainsKey("dbType"),
                "dbType should reflect the real, WTMContext-resolved DataContext.");
        }

        /// <summary>
        /// The DataContext obtained via <see cref="WTMContext.CreateDC"/> is not DI-tracked, so
        /// the health check must dispose it itself after each probe.
        /// </summary>
        [TestMethod]
        public async Task DataContextCheck_WtmContextAvailable_DisposesOwnedDataContext()
        {
            var trackedDc = InMemoryDataContext.Create();
            var wtm = new FakeWtmContext(trackedDc);
            var check = new WtmDataContextHealthCheck(new NullContext(), wtm, TimeSpan.FromSeconds(2));

            await check.CheckHealthAsync(new HealthCheckContext());

            Assert.IsTrue(trackedDc.WasDisposed,
                "A DataContext obtained via WTMContext.CreateDC() must be owned/disposed by the health check.");
        }

        /// <summary>
        /// A DI-fallback DataContext (host registered IDataContext directly, without WTMContext)
        /// is owned by its DI scope, not by the health check — disposing it here would break the
        /// caller's scope-managed lifetime.
        /// </summary>
        [TestMethod]
        public async Task DataContextCheck_NoWtmContext_DoesNotDisposeDiFallbackDataContext()
        {
            using var diDc = InMemoryDataContext.Create();
            var check = new WtmDataContextHealthCheck(diDc, null, TimeSpan.FromSeconds(2));

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.AreEqual(HealthStatus.Healthy, result.Status);
            Assert.IsFalse(diDc.WasDisposed,
                "The DI-fallback DataContext's lifetime belongs to its own DI scope, not the health check.");
        }

        /// <summary>
        /// WTMContext.CreateDC() failing (misconfigured/disabled connection) must surface as
        /// Unhealthy — not escape uncaught, and not be silently swallowed as a NullContext skip.
        /// </summary>
        [TestMethod]
        public async Task DataContextCheck_WtmContextCreateDCThrows_ReturnsUnhealthy()
        {
            var wtm = new ThrowingFakeWtmContext();
            var check = new WtmDataContextHealthCheck(new NullContext(), wtm, TimeSpan.FromSeconds(2));

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.AreEqual(HealthStatus.Unhealthy, result.Status);
            Assert.IsNotNull(result.Exception);
        }

        // ── Registration-path (ActivatorUtilities) — #741 regression guard ─

        /// <summary>
        /// #741 regression: an earlier draft of the fix gave the <c>WTMContext? wtm</c>
        /// constructor parameter no default value. <c>AddWtmDataContextCheck</c> registers this
        /// check via <c>AddTypeActivatedCheck&lt;WtmDataContextHealthCheck&gt;</c>
        /// (<c>ActivatorUtilities.CreateInstance</c>), which resolves constructor parameters not
        /// covered by the explicit <c>args</c> array through <c>IServiceProvider.GetService</c> —
        /// if that returns <c>null</c> AND the parameter has no default value, ActivatorUtilities
        /// throws instead of passing <c>null</c> through. All the unit tests above construct the
        /// check directly (<c>new WtmDataContextHealthCheck(...)</c>), bypassing
        /// ActivatorUtilities entirely, so none of them could catch this. These two tests build a
        /// real <see cref="ServiceProvider"/> and drive the check through its actual
        /// <c>AddWtmDataContextCheck()</c> registration via <see cref="HealthCheckService"/> —
        /// the exact masking gap the #727 post-mortem warned about (constructing test doubles
        /// directly instead of exercising the real DI/activation path).
        /// </summary>
        [TestMethod]
        public async Task DataContextCheck_RegisteredWithWtmContext_ActivatesAndProbesWtmContextDataContext()
        {
            // Arrange — mirrors AddWtmContext's real DI shape: IDataContext -> NullContext
            // placeholder, WTMContext registered separately with a real DataContext behind
            // CreateDC(). This is scenario (a): WTMContext IS present in DI.
            using var realDc = InMemoryDataContext.Create();
            var wtm = new FakeWtmContext(realDc);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IDataContext>(new NullContext());
            services.AddSingleton<WTMContext>(wtm);
            services.AddHealthChecks().AddWtmDataContextCheck();

            using var provider = services.BuildServiceProvider();
            var healthCheckService = provider.GetRequiredService<HealthCheckService>();

            // Act
            var report = await healthCheckService.CheckHealthAsync();

            // Assert
            Assert.AreEqual(HealthStatus.Healthy, report.Status);
            var entry = report.Entries["datacontext"];
            Assert.AreEqual("DataContext connection OK.", entry.Description,
                "WTMContext is registered — the check must resolve WTMContext.CreateDC()'s real " +
                "DataContext, not the DI NullContext placeholder.");
        }

        [TestMethod]
        public async Task DataContextCheck_RegisteredWithoutWtmContext_ActivatesWithoutThrowing_ProbesDiFallback()
        {
            // Arrange — host registers a real IDataContext directly, WITHOUT calling
            // AddWtmContext, so WTMContext is absent from DI entirely. This is scenario (b):
            // the exact shape of the #741 regression. Before the constructor parameter got its
            // default value, ActivatorUtilities.CreateInstance threw InvalidOperationException
            // here because the (then-mandatory) WTMContext parameter could not be resolved from
            // an empty container — the health-check framework surfaced that as an
            // activation-failure Unhealthy/503, contradicting the fix's own documented
            // "graceful DI-fallback" behaviour.
            using var diDc = InMemoryDataContext.Create();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IDataContext>(diDc);
            // Deliberately NOT registering WTMContext.
            services.AddHealthChecks().AddWtmDataContextCheck();

            using var provider = services.BuildServiceProvider();
            var healthCheckService = provider.GetRequiredService<HealthCheckService>();

            // Act — must not throw during activation.
            var report = await healthCheckService.CheckHealthAsync();

            // Assert
            Assert.AreEqual(HealthStatus.Healthy, report.Status);
            var entry = report.Entries["datacontext"];
            Assert.AreEqual("DataContext connection OK.", entry.Description,
                "No WTMContext in DI — the check must fall back to the directly DI-registered " +
                "IDataContext instead of failing to activate.");
        }

        /// <summary>Minimal <see cref="WTMContext"/> double whose <c>CreateDC</c> returns a
        /// pre-built <see cref="IDataContext"/> — mirrors the FakeWtmContext pattern used by
        /// the #727 ProdDi regression tests for ActionLogRetentionService/LookupCacheWarmupService.</summary>
        private sealed class FakeWtmContext : WTMContext
        {
            private readonly IDataContext? _dc;
            public FakeWtmContext(IDataContext? dc) : base(null) => _dc = dc;
            public override IDataContext? CreateDC(bool isLog = false, string? cskey = null, bool logerror = true)
                => _dc;
        }

        private sealed class ThrowingFakeWtmContext : WTMContext
        {
            public ThrowingFakeWtmContext() : base(null) { }
            public override IDataContext? CreateDC(bool isLog = false, string? cskey = null, bool logerror = true)
                => throw new InvalidOperationException("simulated disabled connection");
        }

        // ── JSON response writer — shape ──────────────────────────────────

        [TestMethod]
        public async Task JsonResponseWriter_emits_healthy_shape_for_empty_report()
        {
            var body = await InvokeWriter(HealthStatus.Healthy,
                new Dictionary<string, HealthReportEntry>());

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.AreEqual("Healthy", root.GetProperty("status").GetString());
            Assert.IsTrue(root.TryGetProperty("totalDurationMs", out _));
            Assert.AreEqual(0, root.GetProperty("checks").GetArrayLength());
        }

        [TestMethod]
        public async Task JsonResponseWriter_includes_healthy_check_details()
        {
            var entries = new Dictionary<string, HealthReportEntry>
            {
                ["db"] = new(
                    status: HealthStatus.Healthy,
                    description: "DataContext connection OK.",
                    duration: TimeSpan.FromMilliseconds(42),
                    exception: null,
                    data: new Dictionary<string, object> { ["dbType"] = "SQLite" },
                    tags: new[] { "ready" }),
            };

            var body = await InvokeWriter(HealthStatus.Healthy, entries);

            using var doc = JsonDocument.Parse(body);
            var check = doc.RootElement.GetProperty("checks")[0];
            Assert.AreEqual("db", check.GetProperty("name").GetString());
            Assert.AreEqual("Healthy", check.GetProperty("status").GetString());
            Assert.AreEqual(42L, check.GetProperty("durationMs").GetInt64());
            Assert.AreEqual("DataContext connection OK.", check.GetProperty("description").GetString());
            Assert.AreEqual("SQLite", check.GetProperty("data").GetProperty("dbType").GetString());
            Assert.AreEqual("ready", check.GetProperty("tags")[0].GetString());
            Assert.IsFalse(check.TryGetProperty("exception", out _),
                "healthy entry should omit exception field");
        }

        [TestMethod]
        public async Task JsonResponseWriter_includes_exception_message_on_unhealthy_when_dev()
        {
            var entries = new Dictionary<string, HealthReportEntry>
            {
                ["db"] = new(
                    status: HealthStatus.Unhealthy,
                    description: "probe threw",
                    duration: TimeSpan.FromMilliseconds(12),
                    exception: new InvalidOperationException("simulated DB outage"),
                    data: new Dictionary<string, object>(),
                    tags: null),
            };

            // includeExceptionDetail: true simulates development mode.
            var body = await InvokeWriter(HealthStatus.Unhealthy, entries, includeExceptionDetail: true);

            using var doc = JsonDocument.Parse(body);
            Assert.AreEqual("Unhealthy", doc.RootElement.GetProperty("status").GetString());
            var check = doc.RootElement.GetProperty("checks")[0];
            Assert.AreEqual("Unhealthy", check.GetProperty("status").GetString());
            Assert.AreEqual("simulated DB outage", check.GetProperty("exception").GetString());
        }

        [TestMethod]
        public async Task JsonResponseWriter_redacts_exception_message_in_production()
        {
            // L19: exception message must be omitted (redacted) when includeExceptionDetail is false
            var entries = new Dictionary<string, HealthReportEntry>
            {
                ["db"] = new(
                    status: HealthStatus.Unhealthy,
                    description: "probe threw",
                    duration: TimeSpan.FromMilliseconds(12),
                    exception: new InvalidOperationException("Server=prod-db;Password=secret"),
                    data: new Dictionary<string, object>(),
                    tags: null),
            };

            // includeExceptionDetail: false (default) simulates production mode.
            var body = await InvokeWriter(HealthStatus.Unhealthy, entries, includeExceptionDetail: false);

            using var doc = JsonDocument.Parse(body);
            var check = doc.RootElement.GetProperty("checks")[0];
            // Exception field must not be present in the JSON output.
            Assert.IsFalse(check.TryGetProperty("exception", out _),
                "exception field must be redacted (omitted) in production mode");
        }

        [TestMethod]
        public async Task JsonResponseWriter_content_type_is_json()
        {
            var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
            var report = new HealthReport(new Dictionary<string, HealthReportEntry>(), TimeSpan.Zero);
            await WtmHealthCheckResponseWriter.WriteJsonResponse(ctx, report);
            Assert.AreEqual("application/json; charset=utf-8", ctx.Response.ContentType);
        }

        [TestMethod]
        public async Task JsonResponseWriter_throws_on_null_context_or_report()
        {
            var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
            var report = new HealthReport(new Dictionary<string, HealthReportEntry>(), TimeSpan.Zero);

            await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
                WtmHealthCheckResponseWriter.WriteJsonResponse(null!, report));
            await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
                WtmHealthCheckResponseWriter.WriteJsonResponse(ctx, null!));
        }

        // ── UseWtmHealthChecks opt-out ────────────────────────────────────

        [TestMethod]
        public async Task UseWtmHealthChecks_opt_out_falls_back_to_plain_text()
        {
            using var host = BuildHost(useJsonResponse: false);
            await host.StartAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/healthz");
            var body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("Healthy", body.Trim(),
                "useJsonResponse: false must restore the ASP.NET Core default plain-text writer");
        }

        [TestMethod]
        public async Task UseWtmHealthChecks_default_preserves_plaintext()
        {
            // Default useJsonResponse: false → plain-text body, no silent
            // content-type change for existing callers.
            using var host = BuildHost();
            await host.StartAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/healthz");
            var body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual("Healthy", body.Trim());
        }

        [TestMethod]
        public async Task UseWtmHealthChecks_opt_in_writes_json()
        {
            using var host = BuildHost(useJsonResponse: true);
            await host.StartAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/healthz");
            Assert.AreEqual("application/json",
                response.Content.Headers.ContentType?.MediaType);
        }

        // ── Test scaffolding ──────────────────────────────────────────────

        private static IHost BuildHost(IDataContext? dc = null, bool useJsonResponse = false)
        {
            return new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        if (dc != null) services.AddSingleton(dc);
                        services.AddWtmHealthChecks(checks =>
                        {
                            if (dc != null) checks.AddWtmDataContextCheck();
                        });
                    });
                    webBuilder.Configure(app =>
                        app.UseWtmHealthChecks(useJsonResponse: useJsonResponse));
                })
                .Build();
        }

        private static async Task<string> InvokeWriter(
            HealthStatus overallStatus,
            IReadOnlyDictionary<string, HealthReportEntry> entries,
            bool includeExceptionDetail = false)
        {
            var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
            var report = new HealthReport(entries, overallStatus, TimeSpan.FromMilliseconds(100));
            await WtmHealthCheckResponseWriter.WriteJsonResponse(ctx, report, includeExceptionDetail);
            ctx.Response.Body.Position = 0;
            using var reader = new StreamReader(ctx.Response.Body);
            return await reader.ReadToEndAsync();
        }

        /// <summary>
        /// Tiny <see cref="IDataContext"/> backed by EF Core InMemory
        /// provider. <c>CanConnectAsync</c> returns <c>true</c>; that's all
        /// the health check inspects, and the rest of the contract is stubbed
        /// with <see cref="NotSupportedException"/> to surface accidental use.
        /// </summary>
        private sealed class InMemoryDataContext : DbContext, IDataContext
        {
            public static InMemoryDataContext Create()
                => new(new DbContextOptionsBuilder<InMemoryDataContext>()
                    .UseInMemoryDatabase("healthcheck-inmem-" + Guid.NewGuid().ToString("N"))
                    .Options);

            private InMemoryDataContext(DbContextOptions opts) : base(opts) { }

            /// <summary>Set when Dispose is called — lets ownership/disposal tests
            /// (issue #741) verify the health check disposes only the DataContext
            /// instances it created itself via WTMContext.CreateDC().</summary>
            public bool WasDisposed { get; private set; }

            public override void Dispose()
            {
                WasDisposed = true;
                base.Dispose();
            }

            // ── IDataContext surface ─────────────────────────────────────
            bool IDataContext.IsFake { get; set; } = true;
            bool IDataContext.IsDebug { get; set; }
            bool IDataContext.EnableSensitiveQueryLogging { get; set; }
            string? IDataContext.CurrentUserCode { get; set; }
            string? IDataContext.TenantCode => null;
            DBTypeEnum IDataContext.DBType { get => DBTypeEnum.Memory; set { } }
            string IDataContext.CSName { get => "inmem"; set { } }

            void IDataContext.AddEntity<T>(T e) => Set<T>().Add(e);
            void IDataContext.UpdateEntity<T>(T e) => Set<T>().Update(e);
            void IDataContext.UpdateProperty<T>(T e, System.Linq.Expressions.Expression<Func<T, object>> f) => throw new NotSupportedException();
            void IDataContext.UpdateProperty<T>(T e, string f) => throw new NotSupportedException();
            void IDataContext.DeleteEntity<T>(T e) => Set<T>().Remove(e);
            void IDataContext.CascadeDelete<T>(T e) => throw new NotSupportedException();
            DbSet<T> IDataContext.Set<T>() where T : class => Set<T>();

            Task<bool> IDataContext.DataInit(object? _, bool __) => Task.FromResult(false);
            void IDataContext.EnsureCreate() { }
            IDataContext IDataContext.CreateNew() => this;
            IDataContext IDataContext.ReCreate(Microsoft.Extensions.Logging.ILoggerFactory? _) => this;
            System.Data.DataTable IDataContext.RunSP(string c, params object[] p) => throw new NotSupportedException();
            IEnumerable<TElement> IDataContext.RunSP<TElement>(string c, params object[] p) => throw new NotSupportedException();
            System.Data.DataTable IDataContext.RunSQL(string c, params object[] p) => throw new NotSupportedException();
            IEnumerable<TElement> IDataContext.RunSQL<TElement>(string s, params object[] p) => throw new NotSupportedException();
            System.Data.DataTable IDataContext.Run(string s, System.Data.CommandType t, params object[] p) => throw new NotSupportedException();
            IEnumerable<TElement> IDataContext.Run<TElement>(string s, System.Data.CommandType t, params object[] p) => throw new NotSupportedException();
            object IDataContext.CreateCommandParameter(string n, object v, System.Data.ParameterDirection d) => throw new NotSupportedException();
            void IDataContext.SetLoggerFactory(Microsoft.Extensions.Logging.ILoggerFactory f) { }
            void IDataContext.SetTenantCode(string? t) { }
        }
    }
}
