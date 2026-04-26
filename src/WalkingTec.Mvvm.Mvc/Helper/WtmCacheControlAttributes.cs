#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Forces the response to be uncacheable by browsers and proxies —
    /// the "after logout, the back button must not show stale data"
    /// attribute. Emits the full defence-in-depth cache-suppression
    /// bundle:
    /// <list type="bullet">
    /// <item><c>Cache-Control: no-store, no-cache, must-revalidate, max-age=0, private</c>
    /// — covers every modern HTTP cache.</item>
    /// <item><c>Pragma: no-cache</c> — HTTP/1.0 proxy fallback
    /// (still deployed in plenty of enterprise networks).</item>
    /// <item><c>Expires: 0</c> — legacy shared-cache fallback.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Usage:
    /// <code>
    /// [WtmNoCache]
    /// [Route("/profile")]
    /// public class ProfileController : Controller { ... }
    /// </code>
    /// </para>
    /// <para>
    /// Headers are written via <see cref="HttpResponse.OnStarting(Func{Task})"/>
    /// so they land even when a downstream result writer short-circuits
    /// the pipeline. First-writer-wins — any <c>Cache-Control</c>
    /// already set by upstream middleware (reverse proxy, a more
    /// specific action filter) is preserved untouched.
    /// </para>
    /// <para>
    /// For endpoints that want an explicit custom value (e.g.
    /// <c>public, max-age=300</c> on a reference-data API) use
    /// <see cref="WtmCacheControlAttribute"/> instead — the two
    /// attributes are mutually exclusive; apply only one per action.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class WtmNoCacheAttribute : Attribute, IAsyncResultFilter
    {
        internal const string CacheControlValue =
            "no-store, no-cache, must-revalidate, max-age=0, private";

        public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(next);

            context.HttpContext.Response.OnStarting(static state =>
            {
                var http = (HttpContext)state!;
                var headers = http.Response.Headers;
                if (!headers.ContainsKey("Cache-Control"))
                {
                    headers["Cache-Control"] = CacheControlValue;
                }
                if (!headers.ContainsKey("Pragma"))
                {
                    headers["Pragma"] = "no-cache";
                }
                if (!headers.ContainsKey("Expires"))
                {
                    headers["Expires"] = "0";
                }
                return Task.CompletedTask;
            }, context.HttpContext);

            return next();
        }
    }

    /// <summary>
    /// Sets an explicit <c>Cache-Control</c> response header on the
    /// decorated controller / action. The opposite number of
    /// <see cref="WtmNoCacheAttribute"/>: instead of suppressing
    /// caching, tell intermediaries exactly how long to cache.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Usage:
    /// <code>
    /// // 5-minute shared-cache TTL for reference-data API
    /// [WtmCacheControl("public, max-age=300")]
    /// [HttpGet("/api/countries")]
    /// public IActionResult ListCountries() { ... }
    ///
    /// // Long-lived immutable asset (hashed filename in route)
    /// [WtmCacheControl("public, max-age=31536000, immutable")]
    /// [HttpGet("/static/v{hash}/bundle.js")]
    /// public IActionResult Bundle() { ... }
    /// </code>
    /// </para>
    /// <para>
    /// First-writer-wins: an upstream-set <c>Cache-Control</c> is
    /// preserved. The attribute emits verbatim — no parsing or
    /// validation — so callers are responsible for RFC-7234 correctness.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class WtmCacheControlAttribute : Attribute, IAsyncResultFilter
    {
        /// <summary>Raw <c>Cache-Control</c> header value.</summary>
        public string Value { get; }

        /// <summary>
        /// Optional <c>Vary</c> header value (e.g. <c>Accept-Encoding, Authorization</c>).
        /// Emitted only when non-null/whitespace.
        /// </summary>
        public string? Vary { get; set; }

        public WtmCacheControlAttribute(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "[WtmCacheControl] value must not be null or whitespace.",
                    nameof(value));
            }
            Value = value;
        }

        public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(next);

            var http = context.HttpContext;
            var cacheControl = Value;
            var vary = Vary;

            http.Response.OnStarting(() =>
            {
                var headers = http.Response.Headers;
                if (!headers.ContainsKey("Cache-Control"))
                {
                    headers["Cache-Control"] = cacheControl;
                }
                if (!string.IsNullOrWhiteSpace(vary) && !headers.ContainsKey("Vary"))
                {
                    headers["Vary"] = vary;
                }
                return Task.CompletedTask;
            });

            return next();
        }
    }
}
