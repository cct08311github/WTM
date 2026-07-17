#nullable enable
using System;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Three-state CSP middleware mode (#846). Replaces the boolean
    /// <see cref="WtmCspOptions.ReportOnly"/> with explicit semantics:
    /// off / monitor / enforce — the third state ("Disabled") covers
    /// the operationally-essential "kill the policy without ripping
    /// out the <c>app.UseWtmContentSecurityPolicy()</c> call" scenario
    /// (incident response, dev debugging, canary rollouts).
    /// </summary>
    public enum WtmCspMode
    {
        /// <summary>No CSP header is emitted at all — middleware passes through.</summary>
        Disabled = 0,

        /// <summary>Emit <c>Content-Security-Policy-Report-Only</c>: monitor without blocking.</summary>
        ReportOnly = 1,

        /// <summary>Emit <c>Content-Security-Policy</c>: block violations.</summary>
        Enforce = 2,
    }

    /// <summary>
    /// Configuration for <c>UseWtmContentSecurityPolicy</c>.
    /// Introduced in issue #789 Phase 3D as the capstone of the framework_layui.js
    /// eval removal effort. Defaults block <c>'unsafe-eval'</c> but keep
    /// <c>'unsafe-inline'</c> for script/style so existing Razor TagHelper
    /// inline blocks continue to work without a nonce refactor.
    /// </summary>
    public class WtmCspOptions
    {
        public string DefaultSrc { get; set; } = "'self'";

        /// <summary>
        /// Default: <c>'self' 'unsafe-inline'</c>. Intentionally omits
        /// <c>'unsafe-eval'</c> — the whole point of Phase 1–3C was to reach
        /// an eval-free framework baseline so this restriction is safe.
        /// </summary>
        public string ScriptSrc { get; set; } = "'self' 'unsafe-inline'";

        public string StyleSrc { get; set; } = "'self' 'unsafe-inline' https://fonts.googleapis.com";
        public string ImgSrc { get; set; } = "'self' data: https:";
        public string FontSrc { get; set; } = "'self' data: https://fonts.gstatic.com";
        public string ConnectSrc { get; set; } = "'self'";
        public string FrameSrc { get; set; } = "'self'";
        public string ObjectSrc { get; set; } = "'none'";
        public string BaseUri { get; set; } = "'self'";
        public string FormAction { get; set; } = "'self'";

        /// <summary>
        /// <c>frame-ancestors</c> directive (#844) — the modern,
        /// CSP-native clickjacking defense that supersedes the legacy
        /// <c>X-Frame-Options</c> header. Controls which pages can embed
        /// THIS page in an iframe (inward direction); compare with
        /// <see cref="FrameSrc"/> which controls what pages this page
        /// may embed (outward direction).
        /// Default <c>'none'</c> for maximum safety; set <c>'self'</c>
        /// when an app legitimately requires self-embedding (modal preview,
        /// admin sub-frame, etc.). When <c>null</c> the directive is
        /// omitted from the emitted header.
        /// </summary>
        public string? FrameAncestors { get; set; } = "'none'";

        /// <summary>
        /// Three-state mode (#846): <see cref="WtmCspMode.Enforce"/>,
        /// <see cref="WtmCspMode.ReportOnly"/>, or
        /// <see cref="WtmCspMode.Disabled"/>. Default <see cref="WtmCspMode.Enforce"/>.
        /// When set to <see cref="WtmCspMode.Disabled"/> the middleware is
        /// a zero-cost no-op without removing the
        /// <c>app.UseWtmContentSecurityPolicy()</c> call — useful for
        /// per-environment toggles via <c>appsettings.{Env}.json</c>.
        /// Setting this property overrides
        /// <see cref="ReportOnly"/>; leaving this at the default and
        /// using the legacy <see cref="ReportOnly"/> bool keeps
        /// pre-#846 behaviour exactly.
        /// </summary>
        public WtmCspMode Mode { get; set; } = WtmCspMode.Enforce;

        /// <summary>
        /// Legacy two-state toggle (pre-#846). When <c>true</c> the
        /// middleware emits <c>Content-Security-Policy-Report-Only</c>
        /// instead of <c>Content-Security-Policy</c>. Retained for
        /// backwards compatibility — set <see cref="Mode"/> for the
        /// full three-state picker (including <see cref="WtmCspMode.Disabled"/>).
        /// </summary>
        [Obsolete("Use the Mode property (WtmCspMode enum) instead. ReportOnly = true maps to Mode = WtmCspMode.ReportOnly.")]
        public bool ReportOnly { get; set; } = false;

        /// <summary>
        /// Optional <c>report-uri</c> endpoint appended to the policy string.
        /// When null the directive is omitted. See issue #807 (on this repo's
        /// pre-Gitea-cutover GitHub tracker, now defunct) for tracking
        /// <c>report-to</c> + <c>Reporting-Endpoints</c> support (separate
        /// modern directive with different wire format; not wired here).
        /// </summary>
        public string? ReportUri { get; set; } = null;
    }
}
