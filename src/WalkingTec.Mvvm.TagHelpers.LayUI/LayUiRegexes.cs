using System.Text.RegularExpressions;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    /// <summary>
    /// Perf(#677, #713): source-generated regex accessors for compile-time-constant patterns
    /// that were previously constructed as a new <see cref="Regex"/> instance per grid
    /// render. See <c>WalkingTec.Mvvm.Core.Helper.CoreRegexes</c> for the equivalent in Core.
    /// </summary>
    internal static partial class LayUiRegexes
    {
        // DataTableTagHelper — strips an embedded <script>...</script> block out of the
        // single-row JSON emitted for an "AddRow" grid action button. Evaluated once per
        // AddRow-type action button per grid render. Was: new Regex("<script>.*?</script>").
        [GeneratedRegex("<script>.*?</script>")]
        internal static partial Regex ScriptBlockRegex();

        // TreeContainerTagHelper — strips a "...Searcher." prefix off a bound field name,
        // e.g. "Searcher.LevelId" -> "LevelId". Was:
        // new Regex(@".*?Searcher\.", RegexOptions.Compiled) (already static-cached).
        [GeneratedRegex(@".*?Searcher\.")]
        internal static partial Regex SearcherPrefixStripRegex();

        // TreeContainerTagHelper — locates the search-button id embedded in the child grid's
        // rendered markup. Was: new Regex(@"id=""(.*?)"" IsSearchButton", RegexOptions.Compiled)
        // (already static-cached).
        [GeneratedRegex(@"id=""(.*?)"" IsSearchButton")]
        internal static partial Regex SearchButtonIdRegex();

        // TreeContainerTagHelper — locates the "{gridid}option = {" variable declaration in
        // the child grid's rendered markup. Was:
        // new Regex(@"(.*?)option = \{", RegexOptions.Compiled) (already static-cached).
        [GeneratedRegex(@"(.*?)option = \{")]
        internal static partial Regex GridOptionVarRegex();
    }
}
