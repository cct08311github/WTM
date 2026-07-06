using Microsoft.Extensions.Configuration;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Single source of truth for resolving which vendored LayUI asset tree a
    /// framework-embedded tool-UI page (code generator, dashboard designer/render,
    /// WorkFlow designer) should reference.
    ///
    /// <para><strong>Semantics (mirrors the demo <c>_Layout.cshtml</c> flip, #573/#614):</strong>
    /// <c>Layui:Asset == "legacy"</c> (exact, <see cref="System.StringComparison.Ordinal"/>)
    /// selects the vendored 2.6.3 tree at <c>/layui</c>; any other value (including absent
    /// config) selects the 2.13.8 tree at <c>/layui-next</c>.</para>
    ///
    /// <para><strong>SECURITY INVARIANT:</strong> the raw <c>Layui:Asset</c> config value is
    /// NEVER concatenated into a URL. It is only ever compared for equality against the single
    /// literal <c>"legacy"</c>; the return value is always one of exactly two hardcoded string
    /// literals (<c>"/layui"</c> or <c>"/layui-next"</c>). This method is the only place that
    /// decision is made — callers must never re-derive the base path from config themselves.</para>
    /// </summary>
    public static class LayuiAssets
    {
        /// <summary>
        /// Resolves the LayUI asset base path ("/layui" or "/layui-next") from the
        /// <c>Layui:Asset</c> configuration key, per the invariant documented on
        /// <see cref="LayuiAssets"/>.
        /// </summary>
        /// <param name="config">
        /// The application configuration. Null-safe — a null <paramref name="config"/>
        /// (or a missing/null <c>Layui:Asset</c> key) resolves to the default "/layui-next".
        /// </param>
        /// <returns>Either the fixed literal "/layui" or the fixed literal "/layui-next" — never
        /// a value derived from concatenating <paramref name="config"/> into a path.</returns>
        public static string ResolveLayuiBase(IConfiguration config)
            => string.Equals(config?["Layui:Asset"], "legacy", System.StringComparison.Ordinal) ? "/layui" : "/layui-next";
    }
}
