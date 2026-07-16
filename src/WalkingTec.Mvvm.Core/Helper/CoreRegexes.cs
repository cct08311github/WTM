#nullable enable
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace WalkingTec.Mvvm.Core.Helper
{
    /// <summary>
    /// Perf(#677): source-generated / cached regex accessors for hot paths that previously
    /// constructed a new <see cref="Regex"/> instance per call (per request, per row, or per
    /// grid cell). Compile-time-constant patterns use <see cref="GeneratedRegexAttribute"/>
    /// (source-gen, zero per-call allocation). Patterns composed at runtime from a bounded set
    /// of inputs (configured URL prefixes, a VM's DetailGridPrix) use a
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> cache of compiled <see cref="Regex"/>
    /// instances keyed by the composed pattern string, so the same effective pattern is only
    /// ever compiled once. Regex semantics (pattern text, <see cref="RegexOptions"/>, no
    /// explicit timeout) are preserved exactly from the sites they replace.
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
        // ModelState key, e.g. "Details[3]" -> "3". Keyed by DetailGridPrix (a VM-defined
        // constant per grid, bounded cardinality). Was: new Regex($"{DetailGridPrix}\[(.*?)\]")
        // — no RegexOptions on the original, preserved here.
        private static readonly ConcurrentDictionary<string, Regex> _detailGridIndexCache = new();

        internal static Regex GetDetailGridIndexRegex(string detailGridPrix)
        {
            var pattern = $"{detailGridPrix}\\[(.*?)\\]";
            return _detailGridIndexCache.GetOrAdd(pattern, static p => new Regex(p));
        }
    }
}
