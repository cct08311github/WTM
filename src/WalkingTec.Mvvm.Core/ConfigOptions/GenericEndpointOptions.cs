using System;
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// How the generic data endpoints of the framework (<c>/_Framework/GetPagingData</c>, <c>GetEmptyData</c>, <c>Selector</c>,
    /// <c>GetExportExcel</c>, <c>GetExcelTemplate</c>, <c>UpdateModelProperty</c>) decide whether a request for a view model may be served.
    /// </summary>
    public enum GenericEndpointMode
    {
        /// <summary>
        /// Default. The endpoint serves a view model only for a request that carries the reference the LayUI TagHelpers write into the page
        /// that shows the view model (see <see cref="GenericEndpointGuard"/>). The reference is bound to the signed-in user, the tenant,
        /// the view model, the database connection and the purpose, expires, and is valid only while the user may still open the page.
        /// </summary>
        Enforce = 0,

        /// <summary>
        /// Compatibility mode: the endpoints behave as before this check existed (no reference is required). Intended only as a
        /// temporary measure for applications whose own scripts call the endpoints with view model names; prefer
        /// <see cref="GenericEndpointOptions.ExemptViewModels"/> for a few view models. A warning is logged at start-up.
        /// </summary>
        Legacy = 1
    }

    /// <summary>
    /// Settings of the check of the generic data endpoints. Configuration section <c>GenericEndpoints</c>. Secure by default: with no
    /// configuration the reference is required.
    /// </summary>
    public class GenericEndpointOptions
    {
        /// <summary>See <see cref="GenericEndpointMode"/>. Default <see cref="GenericEndpointMode.Enforce"/>.</summary>
        public GenericEndpointMode Mode { get; set; } = GenericEndpointMode.Enforce;

        /// <summary>
        /// View models that may be requested without a reference by any signed-in user while <see cref="Mode"/> is
        /// <see cref="GenericEndpointMode.Enforce"/> (the behavior before the check existed, for these view models only). Full type names
        /// (<c>MyApp.ViewModels.ReportVMs.ReportListVM</c>); a trailing <c>*</c> matches a prefix (<c>MyApp.ViewModels.ReportVMs.*</c>).
        /// Default: empty. Anonymous callers are never served this way.
        /// </summary>
        public List<string> ExemptViewModels { get; set; } = new List<string>();

        /// <summary>
        /// How long a reference written into a page stays valid, in minutes. Default 480. A page that stays open longer must be reloaded
        /// (the grid reports that it has no privilege). Values below 1 are treated as the default.
        /// </summary>
        public int ReferenceLifetimeMinutes { get; set; } = DefaultReferenceLifetimeMinutes;

        /// <summary>
        /// Whether a connection key sent with the request (query string <c>DONOTUSECSName</c>) may choose the database connection of an
        /// action that has no <c>[FixConnection]</c> and for which the connection function returns nothing. Default <c>false</c>: the
        /// connection is decided only by the server (<c>[FixConnection]</c>, the connection function, the default connection), so that the
        /// connection key recorded in a page reference is the one the server chose for the page. Set it only for an application whose own
        /// pages still pass the key in the query string.
        /// </summary>
        public bool AllowRequestConnectionKey { get; set; }

        /// <summary>The default of <see cref="ReferenceLifetimeMinutes"/>.</summary>
        public const int DefaultReferenceLifetimeMinutes = 480;

        /// <summary>The lifetime in effect (the default when the configured value is below 1).</summary>
        public TimeSpan EffectiveLifetime => TimeSpan.FromMinutes(ReferenceLifetimeMinutes < 1 ? DefaultReferenceLifetimeMinutes : ReferenceLifetimeMinutes);

        /// <summary>True when <paramref name="typeFullName"/> is listed in <see cref="ExemptViewModels"/> (exact name, or prefix with a trailing '*').</summary>
        public bool IsExempt(string typeFullName)
        {
            if (string.IsNullOrEmpty(typeFullName) || ExemptViewModels == null)
            {
                return false;
            }
            foreach (var item in ExemptViewModels)
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    continue;
                }
                var pattern = item.Trim();
                if (pattern.EndsWith("*", StringComparison.Ordinal))
                {
                    var prefix = pattern.Substring(0, pattern.Length - 1);
                    if (prefix.Length > 0 && typeFullName.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                else if (string.Equals(pattern, typeFullName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
