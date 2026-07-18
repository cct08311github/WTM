using System.Text.RegularExpressions;

namespace WalkingTec.Mvvm.Mvc.Helper
{
    /// <summary>
    /// Perf(#677, #713): source-generated regex accessors for compile-time-constant patterns
    /// that were previously constructed as a new <see cref="Regex"/> instance on the request
    /// hot path. See <c>WalkingTec.Mvvm.Core.Helper.CoreRegexes</c> for the equivalent in Core.
    /// </summary>
    internal static partial class MvcRegexes
    {
        // FrameworkFilter — child-collection ModelState key split, e.g.
        // "Entity.Majors[0].SchoolId" -> ("Entity.Majors", ".SchoolId"), used to re-derive the
        // canonical "[0]" key form when checking whether a validation error should be removed.
        // Was: new Regex("(.*?)\[.*?\](.*?$)"), constructed inside a per-request foreach.
        [GeneratedRegex(@"(.*?)\[.*?\](.*?$)")]
        internal static partial Regex ChildCollectionIndexRegex();

        // FrameworkFilter — child-collection foreign-key validation-error suppression:
        // matches ModelState keys like "Entity.Majors[0].SchoolId" (case-insensitive) so the
        // framework can ignore FK validation errors it will auto-populate on save. Was:
        // Regex.IsMatch(x, ".*?\[.*?\]\..*?id", RegexOptions.IgnoreCase), evaluated per
        // ModelState key via a per-request Select().Where().
        [GeneratedRegex(@".*?\[.*?\]\..*?id", RegexOptions.IgnoreCase)]
        internal static partial Regex ChildCollectionForeignKeyErrorRegex();

        // FrameworkServiceExtension tenant-list bootstrap — strip an optional http(s)://
        // scheme and trailing slash to normalize a tenant domain / Referer host. Was:
        // new Regex("(http://|https://)?(.+?)(/)?$"). Duplicate of
        // WalkingTec.Mvvm.Core.Helper.CoreRegexes.BaseUrlDomainRegex() (Core project) since
        // that holder is internal to the Core assembly.
        [GeneratedRegex(@"(http://|https://)?(.+?)(/)?$")]
        internal static partial Regex BaseUrlDomainRegex();
    }
}
