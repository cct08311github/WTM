using System.Text.RegularExpressions;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    /// <summary>
    /// Perf(#677): source-generated regex accessors for compile-time-constant patterns that
    /// were previously constructed as a new <see cref="Regex"/> instance per grid render. See
    /// <c>WalkingTec.Mvvm.Core.Helper.CoreRegexes</c> for the equivalent in Core.
    /// </summary>
    internal static partial class LayUiRegexes
    {
        // DataTableTagHelper — strips an embedded <script>...</script> block out of the
        // single-row JSON emitted for an "AddRow" grid action button. Evaluated once per
        // AddRow-type action button per grid render. Was: new Regex("<script>.*?</script>").
        [GeneratedRegex("<script>.*?</script>")]
        internal static partial Regex ScriptBlockRegex();
    }
}
