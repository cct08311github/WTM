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
    ///
    /// <para><strong>DEPRECATED (Phase-4a, #567):</strong> the <c>"legacy"</c> config value —
    /// and the bundled 2.6.3 <c>/layui</c> asset tree it selects — is deprecated. This is a
    /// non-breaking, advance notice only: <c>Layui:Asset=legacy</c> remains fully functional and
    /// continues to select <c>/layui</c> exactly as documented above. Actual removal (this
    /// <c>legacy</c> branch and the vendored 2.6.3 tree) is planned for the next MAJOR version,
    /// gated on downstream production migration off <c>legacy</c> (tracked as BMS#242).</para>
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
        /// <remarks>
        /// <strong>SECURITY (#776):</strong> the vendored layui 2.6.3 tree selected by
        /// <c>"legacy"</c> historically built each grid cell's <c>&lt;td data-content="..."&gt;</c>
        /// attribute (an internal tooltip/truncation feature, part of the "table" module's cell
        /// renderer) from the raw, un-templeted field value with no double-quote escaping — a
        /// field value containing a literal <c>"</c> followed by markup could break out of the
        /// attribute and execute as real DOM, independent of WTM's own <c>ff.EscapeText</c>
        /// cell-templet guard (#108), which never runs on this code path. The vulnerable
        /// construction physically existed in <strong>two</strong> on-disk locations per vendored
        /// tree — the monolithic <c>layui.js</c> bundle (which several vendored trees ship with
        /// the "table" module inlined, and which production <c>_Layout.cshtml</c> pages actually
        /// load) and the standalone <c>lay/modules/table.js</c> module file (fetched on demand by
        /// trees that ship <c>layui.js</c> as a thin AMD-style loader instead of a bundle) — both
        /// needed the fix; a re-vendor that only patches one is still vulnerable via the other.
        /// The vendored copies in this repo now carry an upstream-parity fix (escapes via
        /// <c>layui.util.escape</c>, matching what the 2.13.8 tree already does) in every such
        /// location, but <c>"legacy"</c> is a real downstream rollback path to that
        /// historically-vulnerable tree — a future re-vendor of 2.6.3 could silently reintroduce
        /// the unpatched construction in either location. This is one more reason <c>"legacy"</c>
        /// is deprecated (see the DEPRECATED note above); the default 2.13.8 tree never exhibited
        /// this issue. <see cref="WalkingTec.Mvvm.Mvc.FrameworkServiceExtension.UseWtmContext"/>
        /// emits a one-time startup <c>LogWarning</c> when <c>Layui:Asset=legacy</c> is configured.
        /// </remarks>
        public static string ResolveLayuiBase(IConfiguration config)
            => string.Equals(config?["Layui:Asset"], "legacy", System.StringComparison.Ordinal) ? "/layui" : "/layui-next";
    }
}
