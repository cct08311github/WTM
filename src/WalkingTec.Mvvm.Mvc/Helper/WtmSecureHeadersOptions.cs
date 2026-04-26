#nullable enable
namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Configuration for <see cref="WtmSecureHeadersMiddleware"/> (#838).
    /// Each header can be disabled by setting its property to <c>null</c>;
    /// any non-null value is emitted verbatim. HSTS is
    /// opt-in via <see cref="HstsEnabled"/>.
    /// </summary>
    /// <remarks>
    /// Default values reflect the OWASP Secure Headers Project
    /// recommendations. Apps can override individual headers or disable
    /// them entirely. Order-insensitive — each header is handled independently.
    /// </remarks>
    public class WtmSecureHeadersOptions
    {
        /// <summary>
        /// <c>X-Content-Type-Options</c>. Default <c>nosniff</c>. Set
        /// <c>null</c> to disable.
        /// </summary>
        public string? XContentTypeOptions { get; set; } = "nosniff";

        /// <summary>
        /// <c>X-Frame-Options</c>. Default <c>SAMEORIGIN</c>. Common values:
        /// <c>SAMEORIGIN</c>, <c>DENY</c>. Set <c>null</c> to disable (CSP
        /// <c>frame-ancestors</c> supersedes this header on modern browsers,
        /// but we keep it for older clients).
        /// </summary>
        public string? XFrameOptions { get; set; } = "SAMEORIGIN";

        /// <summary>
        /// <c>Referrer-Policy</c>. Default
        /// <c>strict-origin-when-cross-origin</c> (same as most major
        /// browsers' own default since 2020). Set <c>null</c> to disable.
        /// </summary>
        public string? ReferrerPolicy { get; set; } = "strict-origin-when-cross-origin";

        /// <summary>
        /// <c>Permissions-Policy</c>. Default denies all sensitive features.
        /// Override to selectively allow (e.g.
        /// <c>geolocation=(self)</c>). Set <c>null</c> to disable entirely.
        /// </summary>
        public string? PermissionsPolicy { get; set; } =
            "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";

        /// <summary>
        /// Master switch for HSTS. Default <c>false</c> — HSTS should only
        /// be enabled once an app has committed to HTTPS, because it
        /// instructs browsers to refuse future HTTP fallback. Emitting it
        /// over HTTP is ignored by browsers, but accidentally emitting
        /// while serving mixed-scheme traffic is a classic foot-gun — we
        /// default off to force an informed opt-in.
        /// </summary>
        public bool HstsEnabled { get; set; }

        /// <summary>HSTS <c>max-age</c> in seconds. Default 1 year.</summary>
        public int HstsMaxAgeSeconds { get; set; } = 31_536_000;

        /// <summary>
        /// Adds <c>includeSubDomains</c> directive. Default <c>true</c>.
        /// </summary>
        public bool HstsIncludeSubDomains { get; set; } = true;

        /// <summary>
        /// Adds <c>preload</c> directive (chromium HSTS preload list).
        /// Default <c>false</c> — apps should only opt in after submitting
        /// to <see href="https://hstspreload.org/"/>.
        /// </summary>
        public bool HstsIncludePreload { get; set; }

        /// <summary>
        /// When <c>true</c>, every header configured on this options
        /// instance is written even if a value already exists in the
        /// response (overwriting upstream proxy / sibling-middleware
        /// values). Default <c>false</c> preserves the original
        /// first-writer-wins behaviour, which is the right call for
        /// most apps because reverse proxies usually inject *stricter*
        /// values than the app could compute.
        /// <para>
        /// Flip on for **defense-in-depth** scenarios where the app
        /// owner does not trust upstream proxy configuration to be
        /// correct — banking / healthcare / regulated industries that
        /// must guarantee certain headers regardless of how the request
        /// arrives. Closes #843.
        /// </para>
        /// <para>
        /// HSTS still respects the HTTPS-only gate: <c>Overwrite = true</c>
        /// does NOT cause an HSTS header to appear on plain-HTTP requests.
        /// </para>
        /// </summary>
        public bool Overwrite { get; set; }
    }
}
