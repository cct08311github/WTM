#nullable enable
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace WalkingTec.Mvvm.Core.Helper
{
    /// <summary>
    /// Perf(#677, #713): source-generated / cached regex accessors for hot paths that
    /// previously constructed a new <see cref="Regex"/> instance per call (per request, per
    /// row, or per grid cell). Compile-time-constant patterns use
    /// <see cref="GeneratedRegexAttribute"/> (source-gen, zero per-call allocation). Patterns
    /// composed at runtime from inputs use a <see cref="ConcurrentDictionary{TKey,TValue}"/>
    /// cache of compiled <see cref="Regex"/> instances keyed by the composed pattern string,
    /// so the same effective pattern is only ever compiled once — EXCEPT where the input is
    /// request-bindable (see <see cref="GetDetailGridIndexRegex"/>), where the cache is also
    /// size-capped as a defense against unbounded growth from a hostile client. Regex
    /// semantics (pattern text, <see cref="RegexOptions"/>, no explicit timeout) are
    /// preserved exactly from the sites they replace.
    /// </summary>
    internal static partial class CoreRegexes
    {
        // PropertyHelper.GetPropertySiblingValues — array/list property path split,
        // e.g. "Foo[0].Bar" -> ("Foo", "Bar"). Was: new Regex("(.*?)\[\-?\d?\]\.(.*?)$")
        [GeneratedRegex(@"(.*?)\[\-?\d?\]\.(.*?)$")]
        internal static partial Regex PropertySiblingPathRegex();

        // BasePagedListVM export paths — strip HTML tags from a cell's rendered text before
        // writing to Excel/CSV. Was: Regex.Replace(text, @"<[^>]*>", ""), three call sites.
        [GeneratedRegex(@"<[^>]*>")]
        internal static partial Regex HtmlTagStripRegex();

        // WTMContext.IsAccessable — url prefix match "^{au}[/\?]?" (RegexOptions.IgnoreCase).
        // `au` is drawn from a bounded, admin-configured set (AllMainTenantOnlyUrls /
        // AllAccessUrls) evaluated per request in a foreach, so per-pattern compiled-Regex
        // caching is safe (small, stable key space) and avoids recompiling the same prefix
        // pattern on every incoming request.
        private static readonly ConcurrentDictionary<string, Regex> _urlPrefixCache = new();

        internal static Regex GetUrlPrefixRegex(string au)
        {
            var pattern = "^" + au + "[/\\?]?";
            return _urlPrefixCache.GetOrAdd(pattern, static p => new Regex(p, RegexOptions.IgnoreCase));
        }

        // BasePagedListVM.ProcessListError — detail-grid row-index extraction from a
        // ModelState key, e.g. "Details[3]" -> "3". Keyed by DetailGridPrix. Was:
        // new Regex($"{DetailGridPrix}\[(.*?)\]") — no RegexOptions on the original,
        // preserved here.
        //
        // SECURITY (#677 review, hardened #713): DetailGridPrix is NOT a fixed developer
        // constant — it round-trips through a hidden form field emitted by
        // DataTableTagHelper (`<input type="hidden" name="{Vm.Name}.DetailGridPrix" .../>`)
        // and is bound from the incoming request on postback. A hostile client can therefore
        // submit an unbounded stream of distinct DetailGridPrix values. The cache below is
        // size-capped: once it holds MaxDetailGridIndexCacheEntries distinct patterns, further
        // distinct values are compiled on demand and NOT added to the cache (still correct,
        // just uncached), so the dictionary itself can never grow past the cap.
        private const int MaxDetailGridIndexCacheEntries = 256;
        private static readonly ConcurrentDictionary<string, Regex> _detailGridIndexCache = new();

        internal static Regex GetDetailGridIndexRegex(string detailGridPrix)
        {
            var pattern = $"{detailGridPrix}\\[(.*?)\\]";
            if (_detailGridIndexCache.TryGetValue(pattern, out var cached))
            {
                return cached;
            }
            if (_detailGridIndexCache.Count >= MaxDetailGridIndexCacheEntries)
            {
                // Cache is at capacity — compile without caching rather than let a hostile
                // stream of distinct DetailGridPrix values grow this dictionary unboundedly.
                return new Regex(pattern);
            }
            return _detailGridIndexCache.GetOrAdd(pattern, static p => new Regex(p));
        }

        // WTMContext.CreateDC (Referer-based tenant-domain resolution, #116) and
        // FrameworkServiceExtension tenant-list bootstrap (Mvc project) — strip an optional
        // http(s):// scheme and trailing slash to normalize a host/URL down to its domain.
        // Was: new Regex("(http://|https://)?(.+?)(/)?$"), constructed per call. Duplicated
        // as WalkingTec.Mvvm.Mvc.Helper.MvcRegexes.BaseUrlDomainRegex() for the Mvc-project
        // call site since this holder is internal to the Core assembly.
        [GeneratedRegex(@"(http://|https://)?(.+?)(/)?$")]
        internal static partial Regex BaseUrlDomainRegex();

        // WtmAuthorizationService — rewrites "/DoBatchXxx"-style action segments back to
        // "/BatchXxx" before URL matching (RegexOptions.IgnoreCase). Was: a static readonly
        // `new Regex("/do(batch.*)", RegexOptions.IgnoreCase | RegexOptions.Compiled)` field;
        // already cached, converted here to source-gen per #713 part 3 (optional).
        [GeneratedRegex(@"/do(batch.*)", RegexOptions.IgnoreCase)]
        internal static partial Regex BatchActionRewriteRegex();
    }
}
