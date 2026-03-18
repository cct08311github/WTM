using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class HealthCheckTests
    {
        private static IHost BuildHost(
            string livePath = "/healthz",
            string readyPath = "/healthz/ready",
            System.Action<IHealthChecksBuilder>? configure = null)
        {
            return new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                        services.AddWtmHealthChecks(configure));
                    webBuilder.Configure(app =>
                        app.UseWtmHealthChecks(livePath, readyPath));
                })
                .Build();
        }

        [TestMethod]
        public async Task LivePath_returns_200_when_service_is_healthy()
        {
            using var host = BuildHost();
            await host.StartAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/healthz");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "/healthz (liveness) 應在服務存活時回傳 200");
        }

        [TestMethod]
        public async Task ReadyPath_returns_200_when_all_checks_pass()
        {
            using var host = BuildHost();
            await host.StartAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/healthz/ready");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "/healthz/ready (readiness) 預設只有 self 檢查，應回傳 200");
        }

        [TestMethod]
        public async Task Custom_path_is_honoured()
        {
            using var host = BuildHost(livePath: "/health", readyPath: "/health/ready");
            await host.StartAsync();
            var client = host.GetTestClient();

            var live  = await client.GetAsync("/health");
            var ready = await client.GetAsync("/health/ready");

            Assert.AreEqual(HttpStatusCode.OK, live.StatusCode,  "自訂 livePath /health 應回傳 200");
            Assert.AreEqual(HttpStatusCode.OK, ready.StatusCode, "自訂 readyPath /health/ready 應回傳 200");
        }

        [TestMethod]
        public async Task Default_paths_return_404_for_non_health_requests()
        {
            using var host = BuildHost();
            await host.StartAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/some-other-path");

            Assert.AreNotEqual(HttpStatusCode.OK, response.StatusCode,
                "/some-other-path 不應被健康檢查 middleware 攔截並回傳 200");
        }

        [TestMethod]
        public async Task Configure_callback_allows_adding_custom_checks()
        {
            var callbackInvoked = false;
            using var host = BuildHost(configure: checks =>
            {
                callbackInvoked = true;
                checks.AddCheck("custom",
                    () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy());
            });
            await host.StartAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/healthz/ready");

            Assert.IsTrue(callbackInvoked, "configure 回呼應被呼叫");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "加入自訂 healthy check 後 /healthz/ready 仍應回傳 200");
        }

        [TestMethod]
        public async Task ReadyPath_returns_503_when_custom_check_is_unhealthy()
        {
            using var host = BuildHost(configure: checks =>
                checks.AddCheck("always-fail",
                    () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("intentional")));
            await host.StartAsync();
            var client = host.GetTestClient();

            var ready = await client.GetAsync("/healthz/ready");
            var live  = await client.GetAsync("/healthz");

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ready.StatusCode,
                "有 unhealthy check 時 /healthz/ready 應回傳 503");
            Assert.AreEqual(HttpStatusCode.OK, live.StatusCode,
                "liveness 端點只跑 'live' tagged check，custom unhealthy check 不影響它");
        }
    }
}
