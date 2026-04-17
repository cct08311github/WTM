#nullable enable
using Microsoft.AspNetCore.Http;

namespace WalkingTec.Mvvm.Core
{
    public class CookieOption
    {
        public string Issuer { get; set; } = "http://localhost";
        public string Audience { get; set; } = "http://localhost";
        public int Expires { get; set; } = 3600;
        public bool SlidingExpiration { get; set; } = true;
        public string LoginPath { get; set; } = "/Login/Login";
        public string LogoutPath { get; set; } = "/Login/Logout";
        public string AccessDeniedPath { get; set; } = "/Login/Login";
        public string Domain { get; set; } = "";
        public string ReturnUrlParameter { get; set; } = "ReturnUrl";

        /// <summary>
        /// Controls the <c>Secure</c> flag on session and authentication cookies.
        /// Default is <see cref="CookieSecurePolicy.SameAsRequest"/> for
        /// backwards compatibility (the flag is set when the incoming request
        /// was HTTPS, not set otherwise).
        /// <para>
        /// <b>Production recommendation</b>: set to
        /// <see cref="CookieSecurePolicy.Always"/> to force the <c>Secure</c>
        /// flag regardless of request scheme. This is especially important
        /// behind a TLS-terminating reverse proxy (nginx, Azure App Service,
        /// IIS ARR, Cloudflare) where the app server sees HTTP from the proxy
        /// — with <c>SameAsRequest</c>, cookies would be emitted without
        /// <c>Secure</c> in that topology, and a browser would re-send them
        /// to any accidentally-exposed HTTP endpoint on the same origin.
        /// </para>
        /// <para>
        /// Issue #813 / #789 security hardening.
        /// </para>
        /// </summary>
        public CookieSecurePolicy SecurePolicy { get; set; } = CookieSecurePolicy.SameAsRequest;
    }
}
