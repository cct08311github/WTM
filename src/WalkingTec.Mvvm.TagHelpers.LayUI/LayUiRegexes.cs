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

        // Issue #470 Slice O1 (N-prereq): TreeContainerTagHelper — island-aware probe.
        // Locates the `data-wtm-grid-id="wtTable_..."` attribute DataTableTagHelper emits
        // on its <table> element ONLY when it actually rendered via the opt-in renderGrid
        // island (see DataTableTagHelper.Island.cs) — never on the legacy path, so this can
        // never match legacy-rendered nested-grid markup. Tried BEFORE GridOptionVarRegex
        // (island-then-legacy probe order): an island-rendered nested grid never emits the
        // "{gridid}option = {" text at all, so without this probe running first, a nested
        // grid that islandifies would silently break TreeContainer's tree-click filtering
        // (#470 design brief comment-18118, risk register #3).
        [GeneratedRegex(@"data-wtm-grid-id=""([A-Za-z0-9_]+)""")]
        internal static partial Regex IslandGridIdRegex();
    }
}
