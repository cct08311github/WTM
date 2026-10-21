using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.E2E.Security
{
    /// <summary>A way of calling a site: nothing (anonymous), a session cookie, or a bearer token.</summary>
    public sealed class MatrixIdentity
    {
        public string Name { get; init; }

        /// <summary>Adds the credentials to a request; null for the anonymous identity.</summary>
        public Action<HttpRequestMessage> Authorize { get; init; }

        public static MatrixIdentity Anonymous() => new MatrixIdentity { Name = "anonymous" };

        public static MatrixIdentity Cookie(string name, string cookieHeader) => new MatrixIdentity
        {
            Name = name,
            Authorize = r => r.Headers.TryAddWithoutValidation("Cookie", cookieHeader)
        };

        public static MatrixIdentity Bearer(string name, string token) => new MatrixIdentity
        {
            Name = name,
            Authorize = r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token)
        };
    }

    public sealed class ProbeResult
    {
        public string EntryKey { get; init; }
        public string Identity { get; init; }
        public string Outcome { get; init; }
        public int Status { get; init; }

        /// <summary>Path of the redirect target (no query), for the report.</summary>
        public string Location { get; init; }
    }

    /// <summary>Sends the probes. Plain HTTP; redirects are not followed, cookies are not kept, and response bodies are only classified, never stored.</summary>
    public sealed class MatrixRunner : IDisposable
    {
        private const int BodyLimit = 2048;
        private readonly HttpClient _http;
        private readonly RefusalTexts _texts;
        private readonly IReadOnlyCollection<string> _loginPaths;

        public MatrixRunner(Uri baseUri, RefusalTexts texts, IReadOnlyCollection<string> loginPaths)
        {
            _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All })
            {
                BaseAddress = baseUri,
                Timeout = TimeSpan.FromSeconds(60)
            };
            _texts = texts;
            _loginPaths = loginPaths;
        }

        public async Task<ProbeResult> SendAsync(MatrixIdentity identity, Probe probe, IDictionary<string, string> extraHeaders = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(probe.Method), probe.Url);
            request.Headers.TryAddWithoutValidation("Accept-Language", "en");
            if (extraHeaders != null)
            {
                foreach (var h in extraHeaders)
                {
                    request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }
            identity.Authorize?.Invoke(request);
            if (probe.Method == "POST" || probe.Method == "PUT" || probe.Method == "PATCH" || probe.Method == "DELETE")
            {
                request.Content = probe.IsApi
                    ? new StringContent("{}", Encoding.UTF8, "application/json")
                    : new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());
            }
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            return Classify(identity, probe.EntryKey, response);
        }

        private ProbeResult Classify(MatrixIdentity identity, string entryKey, HttpResponseMessage response)
        {
            string start = null;
            var truncated = false;
            // only short responses can be one of the framework's fixed messages; everything else is not read at all
            var status = (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (status == 200 || status == 400)
            {
                using var stream = response.Content.ReadAsStream();
                var buffer = new byte[BodyLimit * 4];
                var read = 0;
                int n;
                while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0)
                {
                    read += n;
                }
                start = Encoding.UTF8.GetString(buffer, 0, read);
                if (start.Length > BodyLimit)
                {
                    start = start.Substring(0, BodyLimit);
                    truncated = true;
                }
                else if (read == buffer.Length)
                {
                    truncated = true;
                }
                if (read == 0)
                {
                    start = null;
                }
            }
            var result = new ProbeResponse
            {
                Status = status,
                Location = response.Headers.Location?.OriginalString,
                ContentType = contentType,
                BodyStart = start,
                BodyTruncated = truncated
            };
            return new ProbeResult
            {
                EntryKey = entryKey,
                Identity = identity.Name,
                Status = status,
                Location = LocationPath(result.Location),
                Outcome = OutcomeClassifier.Classify(result, _texts, _loginPaths)
            };
        }

        private static string LocationPath(string location)
        {
            if (string.IsNullOrEmpty(location))
            {
                return null;
            }
            var path = Uri.TryCreate(location, UriKind.Absolute, out var abs) ? abs.AbsolutePath : location;
            var q = path.IndexOfAny(new[] { '?', '#' });
            return q >= 0 ? path.Substring(0, q) : path;
        }

        public void Dispose() => _http.Dispose();
    }
}
