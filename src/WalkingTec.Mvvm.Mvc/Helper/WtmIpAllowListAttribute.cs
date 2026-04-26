#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Restricts a controller or action to clients whose IP falls
    /// inside one of the supplied CIDR blocks. Requests from outside
    /// the allow-list short-circuit with
    /// <see cref="FallbackStatusCode"/> (default <c>403 Forbidden</c>)
    /// before the action body runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Usage:
    /// <code>
    /// // Admin surface only reachable from corporate VPN subnets:
    /// [WtmIpAllowList("10.0.0.0/8", "192.168.0.0/16", "172.16.0.0/12")]
    /// [Route("/_admin/danger")]
    /// public class AdminDangerController : Controller { ... }
    /// </code>
    /// </para>
    /// <para>
    /// Accepts both IPv4 and IPv6 CIDR notation. <c>/32</c> (IPv4) and
    /// <c>/128</c> (IPv6) let the attribute accept a single fixed host.
    /// Multiple CIDRs are OR'd together.
    /// </para>
    /// <para>
    /// Client IP is resolved via
    /// <see cref="HttpContextExtention.GetRemoteIpAddress"/> — the same
    /// convention as the rest of the framework, which reads
    /// <c>X-Forwarded-For</c> before falling back to the socket
    /// address. <b>IMPORTANT</b>: this header is trivially spoofable by
    /// any client when the app is not behind a trusted reverse proxy
    /// that strips/overwrites inbound values. If you expose the app
    /// directly to the public internet, do not rely on this attribute
    /// alone — combine with network-layer firewalling or require a
    /// genuine auth token. This is the standard caveat for every
    /// IP-based control in the .NET ecosystem.
    /// </para>
    /// <para>
    /// Defense-in-depth value: even when nginx / Cloudflare / ALB
    /// already enforces the same allow-list at the edge, carrying the
    /// rule in code means a proxy misconfiguration during a rushed
    /// deploy doesn't silently drop the control. Co-locating the
    /// restriction with the endpoint also makes reviews easier.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class WtmIpAllowListAttribute : Attribute, IAsyncAuthorizationFilter
    {
        private readonly IPNetwork[] _networks;
        private readonly string[] _sourceCidrs;

        /// <summary>Status returned when the caller is not in any allow-listed network. Default <c>403</c>.</summary>
        public int FallbackStatusCode { get; set; } = StatusCodes.Status403Forbidden;

        /// <summary>CIDRs the attribute was built with, in the original caller order (useful for diagnostics / tests).</summary>
        public IReadOnlyList<string> Cidrs => _sourceCidrs;

        public WtmIpAllowListAttribute(params string[] cidrs)
        {
            if (cidrs == null || cidrs.Length == 0)
            {
                throw new ArgumentException(
                    "[WtmIpAllowList] requires at least one CIDR.", nameof(cidrs));
            }

            _sourceCidrs = cidrs;
            _networks = new IPNetwork[cidrs.Length];
            for (var i = 0; i < cidrs.Length; i++)
            {
                var raw = cidrs[i];
                if (string.IsNullOrWhiteSpace(raw))
                {
                    throw new ArgumentException(
                        $"[WtmIpAllowList] CIDR at index {i} is null or whitespace.",
                        nameof(cidrs));
                }

                if (!IPNetwork.TryParse(raw, out var parsed))
                {
                    throw new ArgumentException(
                        $"[WtmIpAllowList] CIDR '{raw}' at index {i} is not a valid CIDR notation (e.g. '10.0.0.0/8' or '2001:db8::/32').",
                        nameof(cidrs));
                }
                _networks[i] = parsed;
            }
        }

        public Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var http = context.HttpContext;
            var ipString = http.GetRemoteIpAddress();
            if (!IsAllowed(ipString, out var parsed))
            {
                var logger = http.RequestServices
                    .GetService<ILoggerFactory>()
                    ?.CreateLogger("WalkingTec.Mvvm.Mvc.WtmIpAllowList");
                logger?.LogWarning(
                    "WtmIpAllowList rejected Path={Path} Ip={Ip} Cidrs={Cidrs}",
                    LogSanitizer.Sanitize(http.Request.Path.Value ?? ""),
                    LogSanitizer.Sanitize(parsed?.ToString() ?? ipString ?? ""),
                    LogSanitizer.Sanitize(string.Join(",", _sourceCidrs)));

                context.Result = new StatusCodeResult(FallbackStatusCode);
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Whether <paramref name="ipString"/> is inside any of the
        /// attribute's networks. Public for unit-test determinism.
        /// </summary>
        public bool IsAllowed(string? ipString)
            => IsAllowed(ipString, out _);

        private bool IsAllowed(string? ipString, out IPAddress? parsed)
        {
            parsed = null;
            if (string.IsNullOrEmpty(ipString) || ipString == "unknown")
            {
                return false;
            }

            // A reverse-proxy may hand us 'addr:port' or 'addr,addr2'
            // (comma-list). Trim to the first entry before parsing.
            var trimmed = ipString;
            var commaIdx = trimmed.IndexOf(',');
            if (commaIdx >= 0) { trimmed = trimmed.Substring(0, commaIdx).Trim(); }

            if (!IPAddress.TryParse(trimmed, out var ip))
            {
                return false;
            }
            parsed = ip;

            foreach (var net in _networks)
            {
                if (net.Contains(ip))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
