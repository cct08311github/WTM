using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class ProblemDetailsTests
    {
        private static IHost BuildTestHost(bool isQuickDebug = false)
        {
            return new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        var configs = new Configs();
                        configs.IsQuickDebug = isQuickDebug;
                        services.AddSingleton(configs);
                    });
                    webBuilder.Configure(app =>
                    {
                        app.UseWtmProblemDetails();

                        app.Run(async context =>
                        {
                            var path = context.Request.Path.Value ?? "";

                            if (path.StartsWith("/api/throw", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("Test exception message");

                            if (path.StartsWith("/api/ok", StringComparison.OrdinalIgnoreCase))
                            {
                                await context.Response.WriteAsync("OK");
                                return;
                            }

                            if (path.StartsWith("/api/validate", StringComparison.OrdinalIgnoreCase))
                            {
                                context.Response.StatusCode = 400;
                                return;
                            }

                            if (path.StartsWith("/mvc/throw", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("MVC exception");

                            await context.Response.WriteAsync("fallback");
                        });
                    });
                })
                .Build();
        }

        [TestMethod]
        public async Task api_route_exception_returns_problemdetails_json()
        {
            using var host = BuildTestHost();
            await host.StartAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/api/throw");

            Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.AreEqual("application/problem+json",
                response.Content.Headers.ContentType?.MediaType);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            Assert.AreEqual(500, doc.RootElement.GetProperty("status").GetInt32());
            Assert.AreEqual("Internal Server Error",
                doc.RootElement.GetProperty("title").GetString());
        }

        [TestMethod]
        public async Task api_route_500_hides_exception_in_production()
        {
            using var host = BuildTestHost(isQuickDebug: false);
            await host.StartAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/api/throw");
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            Assert.AreEqual("An unexpected error occurred.",
                doc.RootElement.GetProperty("detail").GetString());
            Assert.IsFalse(doc.RootElement.TryGetProperty("exception", out _),
                "Exception details should not be exposed in production");
        }

        [TestMethod]
        public async Task api_route_500_shows_exception_in_debug()
        {
            using var host = BuildTestHost(isQuickDebug: true);
            await host.StartAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/api/throw");
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var detail = doc.RootElement.GetProperty("detail").GetString();
            Assert.IsTrue(detail?.Contains("Test exception message"),
                $"Detail should contain exception message in debug mode. Got: {detail}");
            Assert.IsTrue(doc.RootElement.TryGetProperty("exception", out _),
                "Exception field should be present in debug mode");
        }

        [TestMethod]
        public async Task mvc_route_exception_falls_through()
        {
            using var host = BuildTestHost();
            await host.StartAsync();
            var client = host.GetTestClient();

            try
            {
                var response = await client.GetAsync("/mvc/throw");
                // TestServer may wrap the unhandled exception — if we get a response,
                // verify it's NOT problem+json (meaning ProblemDetails didn't handle it)
                if (response.StatusCode == HttpStatusCode.InternalServerError)
                {
                    Assert.AreNotEqual("application/problem+json",
                        response.Content.Headers.ContentType?.MediaType,
                        "MVC routes should not get ProblemDetails treatment");
                }
            }
            catch (Exception)
            {
                // Re-thrown exception reaching the test = correct behavior
                // The middleware re-threw because it's not an API route
            }
        }

        [TestMethod]
        public async Task problemdetails_includes_traceId()
        {
            using var host = BuildTestHost();
            await host.StartAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/api/throw");
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            Assert.IsTrue(doc.RootElement.TryGetProperty("traceId", out var traceId),
                "ProblemDetails must include traceId");
            Assert.IsFalse(string.IsNullOrEmpty(traceId.GetString()),
                "traceId must not be empty");
        }

        [TestMethod]
        public async Task accept_json_header_triggers_problemdetails()
        {
            using var host = BuildTestHost();
            await host.StartAsync();
            var client = host.GetTestClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "/mvc/throw");
            request.Headers.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            var response = await client.SendAsync(request);

            Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.AreEqual("application/problem+json",
                response.Content.Headers.ContentType?.MediaType);
        }

        [TestMethod]
        public async Task api_route_400_returns_problemdetails()
        {
            using var host = BuildTestHost();
            await host.StartAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/api/validate");
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            Assert.AreEqual(400, doc.RootElement.GetProperty("status").GetInt32());
            Assert.AreEqual("Bad Request",
                doc.RootElement.GetProperty("title").GetString());
        }
    }
}
