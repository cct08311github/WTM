using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class RateLimitingTests
    {
        [TestMethod]
        public async Task requests_within_limit_succeed()
        {
            using var host = new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddWtmRateLimiting(opt =>
                        {
                            opt.PermitLimit = 5;
                            opt.WindowSeconds = 60;
                        });
                    });
                    webBuilder.Configure(app =>
                    {
                        app.UseWtmRateLimiting();
                        app.Run(async ctx => await ctx.Response.WriteAsync("OK"));
                    });
                })
                .Build();

            await host.StartAsync();
            var client = host.GetTestClient();

            for (int i = 0; i < 5; i++)
            {
                var response = await client.GetAsync("/api/test");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                    $"Request {i + 1} should succeed within rate limit");
            }
        }

        [TestMethod]
        public async Task requests_over_limit_return_429()
        {
            using var host = new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddWtmRateLimiting(opt =>
                        {
                            opt.PermitLimit = 2;
                            opt.WindowSeconds = 60;
                        });
                    });
                    webBuilder.Configure(app =>
                    {
                        app.UseWtmRateLimiting();
                        app.Run(async ctx => await ctx.Response.WriteAsync("OK"));
                    });
                })
                .Build();

            await host.StartAsync();
            var client = host.GetTestClient();

            // First 2 should succeed
            await client.GetAsync("/api/test");
            await client.GetAsync("/api/test");

            // Third should be rate-limited
            var response = await client.GetAsync("/api/test");
            Assert.AreEqual(HttpStatusCode.TooManyRequests, (HttpStatusCode)response.StatusCode,
                "Request exceeding rate limit should return 429");
        }

        [TestMethod]
        public async Task not_calling_AddWtmRateLimiting_has_no_effect()
        {
            using var host = new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.Configure(app =>
                    {
                        // NOT calling UseWtmRateLimiting
                        app.Run(async ctx => await ctx.Response.WriteAsync("OK"));
                    });
                })
                .Build();

            await host.StartAsync();
            var client = host.GetTestClient();

            // 100 requests should all succeed without rate limiting
            for (int i = 0; i < 100; i++)
            {
                var response = await client.GetAsync("/api/test");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }
        }
    }
}
