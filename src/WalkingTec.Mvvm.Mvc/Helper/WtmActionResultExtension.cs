#nullable enable
namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Fluent extension methods mirroring <see cref="FResultExtension"/> but
    /// producing declarative <see cref="WtmAction"/>s for the
    /// <see cref="WtmActionResult"/> JSON response contract. Introduced in
    /// issue #789 Phase 3C.
    /// </summary>
    public static class WtmActionResultExtension
    {
        public static WtmActionResult CloseDialog(this WtmActionResult self)
        {
            self.Actions.Add(new WtmAction { Type = "closeDialog" });
            return self;
        }

        public static WtmActionResult Alert(this WtmActionResult self, string msg, string? title = null)
        {
            var resolvedTitle = title ?? MvcProgram._localizer?["Sys.Info"];
            self.Actions.Add(new WtmAction { Type = "alert", Message = msg, Title = resolvedTitle });
            return self;
        }

        public static WtmActionResult Message(this WtmActionResult self, string msg, string? title = null)
        {
            var resolvedTitle = title ?? MvcProgram._localizer?["Sys.Info"];
            self.Actions.Add(new WtmAction { Type = "message", Message = msg, Title = resolvedTitle });
            return self;
        }

        public static WtmActionResult RefreshGrid(this WtmActionResult self, string winId = "", int index = 0)
        {
            var effectiveWinId = string.IsNullOrEmpty(winId) ? null : winId;
            self.Actions.Add(new WtmAction { Type = "refreshGrid", WinId = effectiveWinId, Index = index });
            return self;
        }

        // Layui does not support single-row refresh, so RefreshGridRow maps to a
        // full-grid refresh. Matches the behavior of FResultExtension.RefreshGridRow.
        public static WtmActionResult RefreshGridRow(this WtmActionResult self, object? id, string winId = "")
        {
            return self.RefreshGrid(winId);
        }

        public static WtmActionResult RefreshPage(this WtmActionResult self)
        {
            self.Actions.Add(new WtmAction { Type = "refreshPage" });
            return self;
        }

        public static WtmActionResult Reload(this WtmActionResult self)
        {
            self.Actions.Add(new WtmAction { Type = "reload" });
            return self;
        }

        public static WtmActionResult Redirect(this WtmActionResult self, string url)
        {
            self.Actions.Add(new WtmAction { Type = "redirect", Url = url });
            return self;
        }
    }
}
