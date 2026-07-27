#nullable enable
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Mvc.Tests.Security
{
    /// <summary>
    /// Regression coverage for issue #794: <see cref="WtmMiddleware"/> used to special-case
    /// <c>/v1/activities</c> by issuing a server-side self-callback built from the
    /// attacker-controlled <c>Host</c> header (<c>wtm.HostAddress</c>), then reflecting the
    /// fetched body back to the caller — an unauthenticated, pre-authorization SSRF (and,
    /// via the attached <c>RemoteToken</c> bearer header, credential exfiltration on top).
    ///
    /// The Elsa workflow engine this callback served was removed in 97b91db8a; nothing else
    /// in the framework registers or handles <c>/v1/activities</c> (see
    /// <c>app.UseHttpActivities()</c>, commented out in every demo Startup). The fix deletes
    /// the dead self-callback branch entirely rather than hardening it, per the issue's
    /// stated preference (dead code beats a denylist).
    ///
    /// These tests assert the request now flows straight through to the next middleware in
    /// the pipeline for that path, regardless of the <c>Host</c> header — i.e. there is no
    /// special-case branch left to exploit. Before the fix, the request never reached
    /// <c>_next</c> for that path; it instead attempted an outbound self-callback.
    /// </summary>
    [TestClass]
    public class WtmMiddlewareActivitiesSsrfTests
    {
        [TestMethod]
        [DataRow("169.254.169.254")]           // cloud metadata endpoint
        [DataRow("127.0.0.1:6379")]             // localhost-bound admin service
        [DataRow("internal-service.local")]      // arbitrary internal host
        [DataRow("example.com")]                 // legitimate-looking host, still must not be special-cased
        public async Task Activities_path_is_not_special_cased_regardless_of_forged_host_header(string forgedHost)
        {
            var nextCalled = false;
            var middleware = new WtmMiddleware(ctx =>
            {
                nextCalled = true;
                return ctx.Response.WriteAsync("next-reached");
            });

            var context = new DefaultHttpContext();
            context.Request.Scheme = "http";
            context.Request.Host = new HostString(forgedHost);
            context.Request.Path = "/v1/activities";
            context.Request.QueryString = QueryString.Empty;
            context.Response.Body = new System.IO.MemoryStream();

            var wtm = new WTMContext(null, _http: new FixedHttpContextAccessor(context));

            await middleware.InvokeAsync(context, wtm);

            Assert.IsTrue(nextCalled,
                "Request to /v1/activities must flow through to the next middleware; " +
                "no self-callback branch should intercept it (issue #794).");

            context.Response.Body.Position = 0;
            using var reader = new System.IO.StreamReader(context.Response.Body);
            var body = await reader.ReadToEndAsync();
            Assert.AreEqual("next-reached", body,
                "The response body must come from downstream middleware, never from a " +
                "reflected self-callback response.");
        }

        [TestMethod]
        public async Task Activities_path_with_inneruse_marker_also_flows_through()
        {
            var nextCalled = false;
            var middleware = new WtmMiddleware(ctx =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

            var context = new DefaultHttpContext();
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("169.254.169.254");
            context.Request.Path = "/v1/activities";
            context.Request.QueryString = new QueryString("?inneruse=1");

            var wtm = new WTMContext(null, _http: new FixedHttpContextAccessor(context));

            await middleware.InvokeAsync(context, wtm);

            Assert.IsTrue(nextCalled, "The second-hop marker path must also reach the next middleware.");
        }

        /// <summary>
        /// Minimal <see cref="IHttpContextAccessor"/> that always returns a fixed context,
        /// mirroring how the framework's scoped accessor would resolve for the current request.
        /// </summary>
        private sealed class FixedHttpContextAccessor : IHttpContextAccessor
        {
            public FixedHttpContextAccessor(HttpContext context) => HttpContext = context;
            public HttpContext? HttpContext { get; set; }
        }
    }
}
