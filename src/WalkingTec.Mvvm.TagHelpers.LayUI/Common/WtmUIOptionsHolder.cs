#nullable enable
using WalkingTec.Mvvm.Core.ConfigOptions;

namespace WalkingTec.Mvvm.TagHelpers.LayUI.Common
{
    /// <summary>
    /// Issue #470 Slice M: shared process-wide holder for <see cref="WtmUIOptions"/>,
    /// extracted from <c>BaseFieldTag</c>'s private static field so <c>BaseButtonTag</c>
    /// (and its <c>SubmitButtonTagHelper</c> subclass) can read the SAME
    /// <see cref="WtmUIOptions.UseSelectIslandRender"/> flag Slices J/K/L already use,
    /// without a second startup wiring call. <c>BaseFieldTag.SetUIOptions</c> /
    /// <c>BaseFieldTag.UIConfig</c> delegate here — their public signature, call
    /// site (<c>FrameworkServiceExtension.AddWtmContext</c>), and default
    /// (flag OFF) behavior are all unchanged.
    /// </summary>
    internal static class WtmUIOptionsHolder
    {
        private static WtmUIOptions _options = new WtmUIOptions();

        /// <summary>Resolved UI options (always non-null).</summary>
        internal static WtmUIOptions Options => _options;

        /// <summary>Set the global UI options. Called once during app startup.</summary>
        internal static void Set(WtmUIOptions options) => _options = options ?? new WtmUIOptions();
    }
}
