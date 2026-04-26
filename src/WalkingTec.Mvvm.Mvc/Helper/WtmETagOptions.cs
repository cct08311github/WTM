#nullable enable
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Configuration for <see cref="WtmETagMiddleware"/>. Controls which
    /// requests receive an <c>ETag</c> header, the maximum response size
    /// the middleware is willing to buffer, and whether the emitted
    /// ETag is strong or weak.
    /// </summary>
    public class WtmETagOptions
    {
        /// <summary>
        /// HTTP methods whose responses get an ETag. Default <c>GET</c>
        /// + <c>HEAD</c> — the only methods where conditional-request
        /// semantics (<c>If-None-Match</c> → <c>304</c>) apply per
        /// RFC 9110 §15.4. Adding other methods here is a footgun and
        /// intentionally unsupported.
        /// </summary>
        public ISet<string> EligibleMethods { get; set; } = new HashSet<string>(
            System.StringComparer.OrdinalIgnoreCase)
        {
            "GET", "HEAD",
        };

        /// <summary>
        /// Path prefixes that bypass the middleware entirely. Defaults
        /// cover static-asset / health-probe paths — hashing static
        /// files is wasted work because the web server already serves
        /// them with a strong ETag and static-asset pipelines handle
        /// 304s independently. Matching is case-insensitive prefix
        /// check against <c>HttpContext.Request.Path</c>.
        /// </summary>
        public IList<string> PathExclusions { get; set; } = new List<string>
        {
            "/healthz",
            "/_framework",
            "/_js",
            "/_content",
            "/favicon.ico",
        };

        /// <summary>
        /// Max buffered response size in bytes. Responses larger than
        /// this fall through unchanged (no ETag emitted). Default
        /// 2 MiB — large enough for any realistic JSON dashboard /
        /// report payload, small enough that a pathological large
        /// response doesn't turn the middleware into a memory hog.
        /// </summary>
        public int MaxBufferedBytes { get; set; } = 2 * 1024 * 1024;

        /// <summary>
        /// Emit a weak ETag (<c>W/"hash"</c>) instead of a strong one
        /// (<c>"hash"</c>). Default <c>false</c> (strong). Switch to
        /// weak when downstream transformations (gzip, minify, CDN
        /// normalisation) may alter bytes without changing semantics
        /// — strong ETag comparison would then miss valid 304s.
        /// </summary>
        public bool EmitWeakETag { get; set; }
    }
}
