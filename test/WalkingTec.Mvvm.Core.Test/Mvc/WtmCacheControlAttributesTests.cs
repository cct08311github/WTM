#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.Mvc
{
    /// <summary>
    /// Tests for <see cref="WtmNoCacheAttribute"/> and
    /// <see cref="WtmCacheControlAttribute"/>: default bundle emission,
    /// custom value emission, Vary handling, upstream-header preservation,
    /// and constructor validation.
    /// </summary>
    [TestClass]
    public class WtmCacheControlAttributesTests
    {
        // ── [WtmNoCache] ────────────────────────────────────────────────

        [TestMethod]
        public async Task NoCache_emits_full_suppression_bundle()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/cc/sensitive");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var cc = response.Headers.CacheControl!.ToString();
            StringAssert.Contains(cc, "no-store");
            StringAssert.Contains(cc, "no-cache");
            StringAssert.Contains(cc, "must-revalidate");
            StringAssert.Contains(cc, "max-age=0");
            StringAssert.Contains(cc, "private");
            Assert.AreEqual("no-cache", response.Headers.Pragma.First().Name);
            Assert.IsTrue(response.Content.Headers.Expires.HasValue
                || response.Content.Headers.Contains("Expires"),
                "Expires header must be present even if the HttpClient parser chokes on '0'.");
        }

        [TestMethod]
        public async Task NoCache_class_level_attribute_applies_to_every_action()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var a = await client.GetAsync("/classnc/one");
            var b = await client.GetAsync("/classnc/two");

            Assert.IsNotNull(a.Headers.CacheControl);
            Assert.IsNotNull(b.Headers.CacheControl);
        }

        [TestMethod]
        public async Task NoCache_preserves_upstream_cache_control()
        {
            // Upstream middleware wins (e.g. a reverse proxy that
            // already marked the response 'private, no-store, immutable').
            using var host = await BuildHostAsync(
                preSetter: ctx => ctx.Response.Headers["Cache-Control"] = "private, immutable");
            var client = host.GetTestClient();

            var response = await client.GetAsync("/cc/sensitive");

            var cc = response.Headers.CacheControl!.ToString();
            StringAssert.Contains(cc, "immutable",
                "first-writer-wins: upstream Cache-Control must be preserved");
        }

        [TestMethod]
        public async Task NoCache_undecorated_action_receives_no_extra_headers()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/cc/open");

            Assert.IsNull(response.Headers.CacheControl,
                "Undecorated action must not receive Cache-Control from the attribute.");
        }

        // ── [WtmCacheControl] ───────────────────────────────────────────

        [TestMethod]
        public async Task CacheControl_emits_verbatim_value()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/cc/countries");

            var cc = response.Headers.CacheControl!.ToString();
            StringAssert.Contains(cc, "public");
            StringAssert.Contains(cc, "max-age=300");
        }

        [TestMethod]
        public async Task CacheControl_vary_property_emits_vary_header()
        {
            using var host = await BuildHostAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/cc/varied");

            var vary = response.Headers.Vary.Select(v => v.ToString()).FirstOrDefault()
                       ?? response.Headers.GetValues("Vary").FirstOrDefault();
            Assert.IsNotNull(vary);
            StringAssert.Contains(vary!, "Accept-Encoding");
        }

        [TestMethod]
        public async Task CacheControl_preserves_upstream_value()
        {
            using var host = await BuildHostAsync(
                preSetter: ctx => ctx.Response.Headers["Cache-Control"] = "no-store, max-age=0");
            var client = host.GetTestClient();

            var response = await client.GetAsync("/cc/countries");

            var cc = response.Headers.CacheControl!.ToString();
            StringAssert.Contains(cc, "no-store",
                "first-writer-wins: upstream Cache-Control must be preserved");
            Assert.IsFalse(cc.Contains("max-age=300"),
                "Attribute must not append/overwrite when header is already set.");
        }

        // ── Ctor validation ─────────────────────────────────────────────

        [TestMethod]
        public void CacheControl_ctor_rejects_null_and_empty_value()
        {
            Assert.ThrowsException<ArgumentException>(() => new WtmCacheControlAttribute(null!));
            Assert.ThrowsException<ArgumentException>(() => new WtmCacheControlAttribute(""));
            Assert.ThrowsException<ArgumentException>(() => new WtmCacheControlAttribute("   "));
        }

        // ── Scaffolding ─────────────────────────────────────────────────

        private static async Task<IHost> BuildHostAsync(Action<HttpContext>? preSetter = null)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(w =>
                {
                    w.UseTestServer();
                    w.ConfigureServices(services =>
                    {
                        services.AddLogging();
                        services.AddControllers()
                                .AddApplicationPart(typeof(WtmCacheControlAttributesTests).Assembly);
                    });
                    w.Configure(app =>
                    {
                        app.UseRouting();
                        if (preSetter != null)
                        {
                            app.Use(async (ctx, next) =>
                            {
                                ctx.Response.OnStarting(() =>
                                {
                                    preSetter(ctx);
                                    return Task.CompletedTask;
                                });
                                await next();
                            });
                        }
                        app.UseEndpoints(e => e.MapControllers());
                    });
                })
                .Build();
            await host.StartAsync();
            return host;
        }
    }

    [ApiController]
    [Route("cc")]
    public class WtmCacheControlFixtureController : ControllerBase
    {
        [HttpGet("sensitive")]
        [WtmNoCache]
        public IActionResult Sensitive() => Ok("sensitive");

        [HttpGet("open")]
        public IActionResult Open() => Ok("open");

        [HttpGet("countries")]
        [WtmCacheControl("public, max-age=300")]
        public IActionResult Countries() => Ok("countries");

        [HttpGet("varied")]
        [WtmCacheControl("public, max-age=60", Vary = "Accept-Encoding, Authorization")]
        public IActionResult Varied() => Ok("varied");
    }

    [ApiController]
    [Route("classnc")]
    [WtmNoCache]
    public class WtmClassLevelNoCacheFixtureController : ControllerBase
    {
        [HttpGet("one")]
        public IActionResult One() => Ok("one");

        [HttpGet("two")]
        public IActionResult Two() => Ok("two");
    }
}
