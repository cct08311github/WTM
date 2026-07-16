using System.Text.RegularExpressions;

namespace WalkingTec.Mvvm.Mvc.Helper
{
    /// <summary>
    /// Perf(#677): source-generated regex accessors for compile-time-constant patterns that
    /// were previously constructed as a new <see cref="Regex"/> instance on the request hot
    /// path. See <c>WalkingTec.Mvvm.Core.Helper.CoreRegexes</c> for the equivalent in Core.
    /// </summary>
    internal static partial class MvcRegexes
    {
        // FrameworkFilter — child-collection ModelState key split, e.g.
        // "Entity.Majors[0].SchoolId" -> ("Entity.Majors", ".SchoolId"), used to re-derive the
        // canonical "[0]" key form when checking whether a validation error should be removed.
        // Was: new Regex("(.*?)\[.*?\](.*?$)"), constructed inside a per-request foreach.
        [GeneratedRegex(@"(.*?)\[.*?\](.*?$)")]
        internal static partial Regex ChildCollectionIndexRegex();
    }
}
