#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Adds ETag-based conditional request support to GET / HEAD
    /// responses, returning <c>304 Not Modified</c> when the client's
    /// <c>If-None-Match</c> matches the hashed response body. Saves
    /// bandwidth on dashboard / reference-data / admin-list endpoints
    /// whose contents change only when someone actually mutates data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementation: the middleware swaps <c>Response.Body</c> with a
    /// bounded <see cref="MemoryStream"/>, runs the pipeline, hashes
    /// the captured bytes with SHA-256 (truncated and base64url-encoded
    /// to a short ETag), compares with <c>If-None-Match</c>, and either
    /// writes the body + ETag or short-circuits with a body-less 304 +
    /// ETag.
    /// </para>
    /// <para>
    /// Pass-throughs:
    /// </para>
    /// <list type="bullet">
    /// <item>Non-eligible methods (<c>POST</c>, <c>PUT</c>, etc.).</item>
    /// <item>Responses with a status outside 2xx.</item>
    /// <item>Responses whose body exceeds <see cref="WtmETagOptions.MaxBufferedBytes"/>.</item>
    /// <item>Responses whose upstream middleware already set an <c>ETag</c> header
    /// (first-writer-wins; the body is still written but neither 304 nor
    /// a second ETag is added).</item>
    /// </list>
    /// <para>
    /// Supports the RFC 9110 §13.1.2 wildcard <c>If-None-Match: *</c>
    /// and comma-separated lists of tags. Weak-ETag comparison ignores
    /// the <c>W/</c> prefix per RFC 9110 §8.8.3.2 "weak comparison".
    /// </para>
    /// </remarks>
    public class WtmETagMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly WtmETagOptions _options;

        public WtmETagMiddleware(RequestDelegate next, WtmETagOptions options)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!_options.EligibleMethods.Contains(context.Request.Method))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            if (IsExcluded(context.Request.Path))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            var originalBody = context.Response.Body;
            using var buffer = new MemoryStream();
            context.Response.Body = buffer;

            try
            {
                await _next(context).ConfigureAwait(false);

                var status = context.Response.StatusCode;
                var len = buffer.Length;
                var upstreamAlreadySet = context.Response.Headers.ContainsKey("ETag");

                if (status >= 200 && status < 300
                    && len > 0 && len <= _options.MaxBufferedBytes
                    && !upstreamAlreadySet)
                {
                    buffer.Position = 0;
                    var hash = ComputeShortHash(buffer);
                    var etag = _options.EmitWeakETag ? $"W/\"{hash}\"" : $"\"{hash}\"";

                    var inm = context.Request.Headers["If-None-Match"].ToString();
                    if (IsMatch(inm, etag))
                    {
                        // 304: status + ETag, no body, Content-Length cleared.
                        context.Response.StatusCode = StatusCodes.Status304NotModified;
                        context.Response.Headers["ETag"] = etag;
                        context.Response.ContentLength = null;
                        context.Response.Body = originalBody;
                        return;
                    }

                    context.Response.Headers["ETag"] = etag;
                }

                buffer.Position = 0;
                context.Response.Body = originalBody;
                await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            }
            finally
            {
                context.Response.Body = originalBody;
            }
        }

        private bool IsExcluded(PathString path)
        {
            if (!path.HasValue) { return false; }
            var value = path.Value!;
            foreach (var prefix in _options.PathExclusions)
            {
                if (string.IsNullOrEmpty(prefix)) { continue; }
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// SHA-256 the buffer, base64url-encode, truncate to 22 chars
        /// (~132 bits of entropy — plenty for collision resistance
        /// across a single endpoint's response space). Public for
        /// unit-test determinism.
        /// </summary>
        public static string ComputeShortHash(Stream content)
        {
            ArgumentNullException.ThrowIfNull(content);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(content);
            var b64 = Convert.ToBase64String(hash);
            // Base64 → base64url, then trim to a compact ETag.
            b64 = b64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
            return b64.Length <= 22 ? b64 : b64.Substring(0, 22);
        }

        /// <summary>
        /// RFC 9110 §13.1.2 <c>If-None-Match</c> matching: wildcard
        /// <c>*</c> matches any current representation; otherwise
        /// compare against a comma-separated list of tags using weak
        /// comparison (ignores the <c>W/</c> prefix). Public for
        /// unit-test determinism.
        /// </summary>
        public static bool IsMatch(string? ifNoneMatch, string etag)
        {
            if (string.IsNullOrWhiteSpace(ifNoneMatch) || string.IsNullOrWhiteSpace(etag))
            {
                return false;
            }

            var trimmed = ifNoneMatch.Trim();
            if (trimmed == "*") { return true; }

            var serverTag = StripWeakPrefix(etag);
            foreach (var candidate in trimmed.Split(','))
            {
                var c = StripWeakPrefix(candidate.Trim());
                if (string.Equals(c, serverTag, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static string StripWeakPrefix(string tag)
        {
            if (tag.StartsWith("W/", StringComparison.Ordinal))
            {
                return tag.Substring(2);
            }
            return tag;
        }
    }
}
