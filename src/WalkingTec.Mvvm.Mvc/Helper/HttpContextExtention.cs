#nullable enable
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    public static class HttpContextExtention
    {
        /// <summary>
        /// The <c>X-Forwarded-For</c> header name.  Read only when
        /// <see cref="Configs.TrustForwardedForHeader"/> is <c>true</c>
        /// (back-compat opt-in) or after
        /// <see cref="WtmForwardedHeadersExtension.UseWtmForwardedHeaders"/> has
        /// already rewritten <c>Connection.RemoteIpAddress</c> via the built-in
        /// <c>ForwardedHeadersMiddleware</c>.
        /// </summary>
        public const string REMOTE_IP_HEADER = "X-Forwarded-For";

        /// <summary>
        /// Resolves the client IP address for the current request.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Default (secure) behaviour:</b> returns
        /// <c>Connection.RemoteIpAddress</c> — the verified TCP peer address.
        /// This is not spoofable by the client.
        /// </para>
        /// <para>
        /// <b>Recommended proxy setup:</b> call
        /// <c>services.AddWtmForwardedHeaders()</c> and
        /// <c>app.UseWtmForwardedHeaders()</c> with your
        /// <c>KnownProxies</c>/<c>KnownNetworks</c>.  ASP.NET Core's
        /// <c>ForwardedHeadersMiddleware</c> then rewrites
        /// <c>Connection.RemoteIpAddress</c> to the validated client IP
        /// <em>before</em> any WTM middleware runs, so this method
        /// automatically returns the real client IP without any extra config.
        /// </para>
        /// <para>
        /// <b>Back-compat opt-in:</b> set <c>TrustForwardedForHeader = true</c>
        /// in <c>appsettings.json</c> (or via <c>AddWtmContext</c>) to restore
        /// the legacy behaviour of reading <c>X-Forwarded-For</c> first.
        /// Use this only as a temporary measure — it is spoofable when the app
        /// is not behind a trusted proxy that strips inbound XFF values.
        /// See Issue #114.
        /// </para>
        /// </remarks>
        public static string GetRemoteIpAddress(this HttpContext self)
        {
            // Check the back-compat flag.  We use GetService (not
            // GetRequiredService) so a misconfigured or partially-built
            // pipeline never throws here — we simply default to the secure path.
            var trustXff = false;
            try
            {
                var opts = self.RequestServices?.GetService<IOptions<Configs>>();
                if (opts != null)
                {
                    trustXff = opts.Value.TrustForwardedForHeader;
                }
            }
            catch
            {
                // If DI is not available (e.g. unit tests without full DI),
                // default to the secure path.
            }

            if (trustXff)
            {
                // Legacy: read raw X-Forwarded-For first (back-compat opt-in).
                var proxyIp = self.Request?.Headers?[REMOTE_IP_HEADER].FirstOrDefault();
                if (!string.IsNullOrEmpty(proxyIp))
                {
                    return proxyIp;
                }
            }

            // Secure default: return the verified TCP peer address.
            return self.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }
    }
}
