#nullable enable
namespace WalkingTec.Mvvm.Mvc
{
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
        /// When true the middleware emits <c>Content-Security-Policy-Report-Only</c>
        /// instead of <c>Content-Security-Policy</c>. Useful for rolling out a
        /// strict policy in monitor mode before enforcing.
        /// </summary>
        public bool ReportOnly { get; set; } = false;

        /// <summary>
        /// Optional <c>report-uri</c> / <c>report-to</c> endpoint appended to
        /// the policy string. When null the directive is omitted.
        /// </summary>
        public string? ReportUri { get; set; } = null;
    }
}
