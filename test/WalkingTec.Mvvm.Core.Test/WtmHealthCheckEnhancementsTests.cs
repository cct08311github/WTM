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
            var check = new WtmDataContextHealthCheck(dc, TimeSpan.FromSeconds(2));
            var result = await check.CheckHealthAsync(new HealthCheckContext());
            Assert.AreEqual(HealthStatus.Healthy, result.Status);
            Assert.IsTrue(result.Data.ContainsKey("dbType"));
        }

        [TestMethod]
        public async Task DataContextCheck_defaults_timeout_on_nonpositive_input()
        {
            using var dc = InMemoryDataContext.Create();
            // TimeSpan.Zero → constructor should coerce to a sane default
            var check = new WtmDataContextHealthCheck(dc, TimeSpan.Zero);
            var result = await check.CheckHealthAsync(new HealthCheckContext());
            Assert.AreEqual(HealthStatus.Healthy, result.Status);
        }

        [TestMethod]
        public void DataContextCheck_throws_on_null_dc()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                new WtmDataContextHealthCheck(null!, TimeSpan.FromSeconds(1)));
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
        public async Task JsonResponseWriter_includes_exception_message_on_unhealthy()
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

            var body = await InvokeWriter(HealthStatus.Unhealthy, entries);

            using var doc = JsonDocument.Parse(body);
            Assert.AreEqual("Unhealthy", doc.RootElement.GetProperty("status").GetString());
            var check = doc.RootElement.GetProperty("checks")[0];
            Assert.AreEqual("Unhealthy", check.GetProperty("status").GetString());
            Assert.AreEqual("simulated DB outage", check.GetProperty("exception").GetString());
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
            IReadOnlyDictionary<string, HealthReportEntry> entries)
        {
            var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
            var report = new HealthReport(entries, overallStatus, TimeSpan.FromMilliseconds(100));
            await WtmHealthCheckResponseWriter.WriteJsonResponse(ctx, report);
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
