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

        /// <summary>
        /// Appends a redirect action. Only relative URLs are accepted to
        /// prevent open-redirect (CWE-601). Callers that legitimately need
        /// an external redirect should use <c>location.href</c> from app JS
        /// or build the response JSON manually.
        /// </summary>
        /// <exception cref="System.ArgumentException">
        /// Thrown when <paramref name="url"/> is null, empty, or an absolute
        /// URL (scheme://host/... or protocol-relative //host/...).
        /// </exception>
        public static WtmActionResult Redirect(this WtmActionResult self, string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new System.ArgumentException(
                    "Redirect URL must not be null or empty.", nameof(url));
            }

            // Reject absolute URLs (http://, https://, //evil.com, javascript:, etc.).
            // Only same-origin relative paths (starting with '/' or '~/') or
            // same-page fragments ('#...') and query-only updates ('?...') are
            // allowed. This is intentionally strict — apps that need external
            // redirects must do so explicitly outside the WtmAction contract.
            if (!url.StartsWith('/') && !url.StartsWith('~') &&
                !url.StartsWith('#') && !url.StartsWith('?'))
            {
                throw new System.ArgumentException(
                    "Redirect URL must be relative (start with '/', '~/', '#', or '?'). " +
                    "Absolute URLs are rejected to prevent open redirects. Got: " + url,
                    nameof(url));
            }

            // Double-slash prefix would be interpreted as protocol-relative
            // by the browser (e.g., '//evil.com/path' navigates to evil.com).
            if (url.StartsWith("//", System.StringComparison.Ordinal))
            {
                throw new System.ArgumentException(
                    "Redirect URL must not start with '//' (protocol-relative). " +
                    "Got: " + url, nameof(url));
            }

            self.Actions.Add(new WtmAction { Type = "redirect", Url = url });
            return self;
        }
    }
}
