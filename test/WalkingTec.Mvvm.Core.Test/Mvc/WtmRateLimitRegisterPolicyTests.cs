#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.Mvc
{
    /// <summary>
    /// Tests for issue #759: explicit rate-limit policy registration
    /// (<see cref="WtmRateLimitingOptions.RegisterPolicy"/> +
    /// <see cref="WtmRateLimitEndpointExtension.RequireWtmRateLimit"/>), and
    /// the <c>ScanWtmRateLimitAttributes</c> partial-load guard
    /// (<c>SafeGetRateLimitAttributes</c>).
    /// </summary>
    [TestClass]
    public class WtmRateLimitRegisterPolicyTests
    {
        // ── RegisterPolicy validation parity with the attribute ctor ──────

        [TestMethod]
        public void RegisterPolicy_rejects_zero_or_negative_permits()
        {
            var opt = new WtmRateLimitingOptions();
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => opt.RegisterPolicy(0, 60));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => opt.RegisterPolicy(-1, 60));
        }

        [TestMethod]
        public void RegisterPolicy_rejects_zero_or_negative_window()
        {
            var opt = new WtmRateLimitingOptions();
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => opt.RegisterPolicy(5, 0));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => opt.RegisterPolicy(5, -1));
        }

        [TestMethod]
        public void RegisterPolicy_rejects_negative_queue_limit()
        {
            var opt = new WtmRateLimitingOptions();
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => opt.RegisterPolicy(5, 60, -1));
        }

        [TestMethod]
        public void RegisterPolicy_returns_same_options_instance_for_fluent_chaining()
        {
            var opt = new WtmRateLimitingOptions();
            var returned = opt.RegisterPolicy(5, 60);
            Assert.AreSame(opt, returned);
        }

        // ── RequireWtmRateLimit policy-name computation ────────────────────

        [TestMethod]
        public async Task RequireWtmRateLimit_computes_canonical_policy_name()
        {
            IEndpointRouteBuilder? endpointBuilder = null;

            using var host = new HostBuilder()
                .ConfigureWebHost(w =>
                {
                    w.UseTestServer();
                    w.ConfigureServices(services => services.AddRouting());
                    w.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(e =>
                        {
                            endpointBuilder = e;
                            e.MapGet("/named", () => Results.Ok()).RequireWtmRateLimit(4, 45, 2);
                        });
                    });
                })
                .Build();
            await host.StartAsync();

            var endpoint = endpointBuilder!.DataSources
                .SelectMany(ds => ds.Endpoints)
                .OfType<RouteEndpoint>()
                .Single(ep => ep.RoutePattern.RawText == "/named");

            var metadata = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>();
            Assert.IsNotNull(metadata, "RequireWtmRateLimit must attach EnableRateLimitingAttribute metadata.");
            Assert.AreEqual(WtmRateLimitAttribute.BuildPolicyName(4, 45, 2), metadata!.PolicyName,
                "RequireWtmRateLimit must compute the same policy name as WtmRateLimitAttribute.BuildPolicyName.");
        }

        // ── Integration: explicit RegisterPolicy, NO controller attribute ────

        [TestMethod]
        public async Task RegisterPolicy_plus_RequireWtmRateLimit_enforces_limit_with_no_controller_attribute()
        {
            // Regression test for the BMS /live /ready incident: a
            // minimal-API endpoint attaches the rate-limit policy name via
            // RequireWtmRateLimit, but nothing in the assembly carries a
            // matching [WtmRateLimit] attribute. Without opt.RegisterPolicy,
            // AddRateLimiter never registers this named policy and the
            // rate-limiting middleware throws InvalidOperationException the
            // first time the endpoint is hit. With RegisterPolicy, the
            // policy is guaranteed to exist independent of any attribute.
            //
            // Tuple choice matters: ScanWtmRateLimitAttributes() scans the
            // WHOLE AppDomain, including this very test assembly, which
            // also carries [WtmRateLimit(2, 30)] and [WtmRateLimit(3, 60)]
            // via WtmRateLimitTestFixtureController (see
            // WtmRateLimitAttributeTests.cs). Using either of those tuples
            // here would let the attribute scan alone satisfy the policy
            // lookup, making this test pass even if RegisterPolicy (and the
            // ExplicitPolicies merge in AddWtmRateLimiting) were completely
            // broken — false assurance. (2, 41, 0) is not used by any
            // [WtmRateLimit] fixture anywhere in the repo; the guard
            // assertion below makes that isolation self-verifying instead
            // of relying on tribal knowledge of a sibling file.
            Assert.IsFalse(
                WtmRateLimitingExtension.ScanWtmRateLimitAttributes().Contains((2, 41, 0)),
                "Guard: (2, 41, 0) must not already be registered via attribute scan, or this test " +
                "would pass even with RegisterPolicy/ExplicitPolicies completely broken. If a new " +
                "[WtmRateLimit(2, 41)] fixture is ever added elsewhere, pick a different unique tuple here.");

            using var host = await BuildMinimalApiHostAsync(opt =>
            {
                opt.PermitLimit = 500;
                opt.WindowSeconds = 60;
                opt.RegisterPolicy(2, 41);
            });
            var client = host.GetTestClient();

            var r1 = await client.GetAsync("/live");
            var r2 = await client.GetAsync("/live");
            var r3 = await client.GetAsync("/live");

            Assert.AreEqual(HttpStatusCode.OK, r1.StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, r2.StatusCode);
            Assert.AreEqual(HttpStatusCode.TooManyRequests, r3.StatusCode,
                "Third request within the window must be rejected — proves the (2, 41) policy " +
                "was registered purely from RegisterPolicy, with no controller attribute involved " +
                "(verified by the guard assertion above).");
        }

        // ── Integration: explicit RegisterPolicy + a matching attribute tuple ─

        [TestMethod]
        public async Task RegisterPolicy_same_tuple_as_existing_attribute_does_not_throw_duplicate_policy()
        {
            // WtmRateLimitTestFixtureController.Medium() (defined in
            // WtmRateLimitAttributeTests.cs) already carries
            // [WtmRateLimit(3, 60)]. Registering the identical tuple
            // explicitly must NOT cause RateLimiterOptions.AddPolicy to
            // throw on a duplicate name — the HashSet.UnionWith dedup in
            // AddWtmRateLimiting must collapse both registrations of
            // (3, 60, 0) into a single AddPolicy call, and the host must
            // start successfully.
            //
            // This test intentionally overlaps with the attribute-scanned
            // tuple (unlike the "no controller attribute" test above, which
            // must NOT overlap). Note what this test does and does not
            // prove: because (3, 60, 0) is already supplied by the attribute
            // scan on its own, the request-level 429 assertions below would
            // still pass even if RegisterPolicy's contribution were silently
            // dropped (e.g. the ExplicitPolicies merge deleted outright) —
            // that regression is what the "no controller attribute" test
            // above pins. What THIS test pins is the dedup itself: if a
            // future refactor iterates the scanned and explicit tuples as
            // two separate AddPolicy loops instead of merging into one
            // HashSet first, host.StartAsync() below throws
            // InvalidOperationException on the duplicate policy name and
            // this test fails loudly.
            Assert.IsTrue(
                WtmRateLimitingExtension.ScanWtmRateLimitAttributes().Contains((3, 60, 0)),
                "Guard: (3, 60, 0) must already be registered via attribute scan (Medium()/AltSameConfig() " +
                "in WtmRateLimitAttributeTests.cs) — this test only exercises the intended scenario if the " +
                "tuple genuinely overlaps between the scan and RegisterPolicy.");

            using var host = await BuildHostWithControllersAsync(opt =>
            {
                opt.PermitLimit = 500;
                opt.WindowSeconds = 60;
                opt.RegisterPolicy(3, 60);
            });
            var client = host.GetTestClient();

            var r1 = await client.GetAsync("/ratelimit/medium");
            var r2 = await client.GetAsync("/ratelimit/medium");
            var r3 = await client.GetAsync("/ratelimit/medium");
            var r4 = await client.GetAsync("/ratelimit/medium");

            Assert.AreEqual(HttpStatusCode.OK, r1.StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, r2.StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, r3.StatusCode);
            Assert.AreEqual(HttpStatusCode.TooManyRequests, r4.StatusCode,
                "Fourth request within the window must still be rejected — the explicit " +
                "registration must not silently override or duplicate the attribute-driven policy.");
        }

        // ── Scan partial-load guard ─────────────────────────────────────────

        [TestMethod]
        public void ScanWtmRateLimitAttributes_direct_call_in_mstest_host_never_throws()
        {
            // Regression for the reported incident: calling
            // ScanWtmRateLimitAttributes() directly inside an MSTest host
            // process previously risked a TypeLoadException bubbling out of
            // an unguarded GetCustomAttributes<T> call when the scan reached
            // a test-adapter assembly whose attribute materialization fails.
            try
            {
                var configs = WtmRateLimitingExtension.ScanWtmRateLimitAttributes();
                Assert.IsNotNull(configs);
            }
            catch (Exception ex)
            {
                Assert.Fail($"ScanWtmRateLimitAttributes() must never throw, even inside an MSTest host; got: {ex}");
            }
        }

        [TestMethod]
        public void SafeGetRateLimitAttributes_swallows_TypeLoadException_from_a_throwing_member()
        {
            // Direct unit test of the private guard helper: a MemberInfo
            // double whose GetCustomAttributes(Type, bool) throws
            // TypeLoadException must yield an empty array, not propagate.
            // MemberTypes.Custom routes System.Attribute's internal
            // dispatch to the generic `_ => element.GetCustomAttributes(...)`
            // branch, which is a virtual call into the double's override —
            // exercising exactly the failure mode the guard exists for.
            var method = typeof(WtmRateLimitingExtension).GetMethod(
                "SafeGetRateLimitAttributes", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "SafeGetRateLimitAttributes helper must exist.");

            var result = (WtmRateLimitAttribute[]?)method!.Invoke(null, new object[] { new ThrowingMemberInfo() });

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result!.Length,
                "A member whose attribute lookup throws TypeLoadException must yield an empty array.");
        }

        // ── Test host / fixtures ─────────────────────────────────────────

        private static async Task<IHost> BuildMinimalApiHostAsync(Action<WtmRateLimitingOptions> configure)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(w =>
                {
                    w.UseTestServer();
                    w.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddWtmRateLimiting(configure);
                    });
                    w.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseWtmRateLimiting();
                        app.UseEndpoints(e =>
                        {
                            e.MapGet("/live", () => Results.Ok("live")).RequireWtmRateLimit(2, 41);
                        });
                    });
                })
                .Build();
            await host.StartAsync();
            return host;
        }

        private static async Task<IHost> BuildHostWithControllersAsync(Action<WtmRateLimitingOptions> configure)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(w =>
                {
                    w.UseTestServer();
                    w.ConfigureServices(services =>
                    {
                        services.AddWtmRateLimiting(configure);
                        services.AddControllers()
                                .AddApplicationPart(typeof(WtmRateLimitTestFixtureController).Assembly);
                    });
                    w.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseWtmRateLimiting();
                        app.UseEndpoints(e => e.MapControllers());
                    });
                })
                .Build();
            await host.StartAsync();
            return host;
        }

        /// <summary>
        /// Minimal <see cref="MemberInfo"/> double whose attribute lookup
        /// always throws <see cref="TypeLoadException"/>, used to exercise
        /// <c>WtmRateLimitingExtension.SafeGetRateLimitAttributes</c>'s
        /// guard directly.
        /// </summary>
        private sealed class ThrowingMemberInfo : MemberInfo
        {
            public override object[] GetCustomAttributes(bool inherit) =>
                throw new TypeLoadException("Simulated partial-load failure.");

            public override object[] GetCustomAttributes(Type attributeType, bool inherit) =>
                throw new TypeLoadException("Simulated partial-load failure.");

            public override bool IsDefined(Type attributeType, bool inherit) =>
                throw new TypeLoadException("Simulated partial-load failure.");

            public override Type? DeclaringType => null;
            public override MemberTypes MemberType => MemberTypes.Custom;
            public override string Name => "ThrowingMember";
            public override Type? ReflectedType => null;
        }
    }
}
