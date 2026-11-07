using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;
using WalkingTec.Mvvm.E2E.Infrastructure;

namespace WalkingTec.Mvvm.E2E.Pages.Vue3
{
    /// <summary>
    /// Thin client for the JSON API of the Vue3Demo with a bearer token, used to set up and verify data and to reach actions
    /// that have no screen. Requests that do not return the expected status fail with the response body.
    /// </summary>
    public sealed class Vue3Api : IAsyncDisposable
    {
        private readonly IAPIRequestContext _context;
        private readonly IPlaywright _playwright;

        private Vue3Api(IPlaywright playwright, IAPIRequestContext context, string siteName)
        {
            _playwright = playwright;
            _context = context;
            SiteName = siteName;
        }

        /// <summary>Name of the site this client talks to (see SiteTarget.Name); the run wide seed data is kept per site.</summary>
        public string SiteName { get; }

        public IAPIRequestContext Context => _context;

        public static async Task<Vue3Api> CreateAsync(IPlaywright playwright, SiteProcess site, string account = null, string password = null)
        {
            var anonymous = await playwright.APIRequest.NewContextAsync(new() { BaseURL = site.BaseUri.ToString() });
            var token = await LoginAsync(anonymous, account ?? site.AdminAccount, password ?? site.AdminPassword);
            await anonymous.DisposeAsync();
            var context = await playwright.APIRequest.NewContextAsync(new()
            {
                BaseURL = site.BaseUri.ToString(),
                // the SPA sends its language with every request; the tests run in English
                ExtraHTTPHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token, ["Accept-Language"] = "en" }
            });
            return new Vue3Api(playwright, context, site.Target.Name);
        }

        public static async Task<string> LoginAsync(IAPIRequestContext anonymous, string account, string password)
        {
            var res = await anonymous.PostAsync("/api/_account/loginjwt", new() { DataObject = new { Account = account, Password = password } });
            if (res.Ok == false)
            {
                throw new InvalidOperationException($"Login of '{account}' failed: {res.Status} {await res.TextAsync()}");
            }
            using var doc = JsonDocument.Parse(await res.TextAsync());
            return doc.RootElement.GetProperty("access_token").GetString();
        }

        public async Task<JsonElement> GetAsync(string url, int expected = 200)
        {
            return await ReadAsync(await _context.GetAsync(url), url, expected);
        }

        // the demo binds JSON property names as written (Pascal case), so the body is serialized without a naming policy
        private static readonly JsonSerializerOptions BodyOptions = new() { PropertyNamingPolicy = null };

        private static readonly System.Text.RegularExpressions.Regex Guid36 = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

        // the LinkRef of every upload this client made, by file id: like the single page application, a save that names an uploaded file
        // carries the proof of the upload (UploadedFileRefs); pass withLinkRefs: false to post without it
        private readonly Dictionary<string, string> _linkRefs = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The LinkRef this client received when it uploaded the file; null for any other file.</summary>
        public string LinkRefOf(string fileId) => fileId != null && _linkRefs.TryGetValue(fileId, out var reference) ? reference : null;

        private APIRequestContextOptions Body(object body, bool withLinkRefs = true)
        {
            var json = JsonSerializer.Serialize(body, BodyOptions);
            if (withLinkRefs && _linkRefs.Count > 0)
            {
                var found = Guid36.Matches(json).Select(m => LinkRefOf(m.Value)).Where(x => x != null).Distinct().ToList();
                if (found.Count > 0 && System.Text.Json.Nodes.JsonNode.Parse(json) is System.Text.Json.Nodes.JsonObject record)
                {
                    record["UploadedFileRefs"] = new System.Text.Json.Nodes.JsonArray(found.Select(x => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(x)).ToArray());
                    json = record.ToJsonString();
                }
            }
            return new()
            {
                DataString = json,
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" }
            };
        }

        public async Task<JsonElement> PostAsync(string url, object body, int expected = 200)
        {
            return await ReadAsync(await _context.PostAsync(url, Body(body)), url, expected);
        }

        public async Task<JsonElement> PutAsync(string url, object body, int expected = 200)
        {
            return await ReadAsync(await _context.PutAsync(url, Body(body)), url, expected);
        }

        /// <summary>Posts and returns the raw response (for downloads, error bodies and expected failures).</summary>
        public Task<IAPIResponse> PostRawAsync(string url, object body, bool withLinkRefs = true) => _context.PostAsync(url, Body(body, withLinkRefs));

        public Task<IAPIResponse> PutRawAsync(string url, object body, bool withLinkRefs = true) => _context.PutAsync(url, Body(body, withLinkRefs));

        public Task<IAPIResponse> GetRawAsync(string url) => _context.GetAsync(url);

        /// <summary>Uploads a file through the file API (the call the upload components of the SPA make) and returns its id.</summary>
        public async Task<string> UploadAsync(string fileName, string contentType, byte[] content)
        {
            var form = _context.CreateFormData();
            form.Set("file", new FilePayload { Name = fileName, MimeType = contentType, Buffer = content });
            var res = await _context.PostAsync("/api/_file/Upload", new() { Multipart = form });
            var text = await res.TextAsync();
            if (res.Ok == false)
            {
                throw new InvalidOperationException($"Upload of {fileName} failed: {res.Status} {text}");
            }
            using var doc = JsonDocument.Parse(text);
            Remember(doc.RootElement);
            return doc.RootElement.GetProperty("Id").GetString();
        }

        /// <summary>Uploads through <paramref name="endpoint"/> and returns the whole answer (<c>Id</c>, <c>Url</c>, <c>Ref</c>, <c>DeleteRef</c>, ...).</summary>
        public async Task<JsonElement> UploadFullAsync(string fileName, string contentType, byte[] content, string endpoint = "/api/_file/Upload")
        {
            var form = _context.CreateFormData();
            form.Set("file", new FilePayload { Name = fileName, MimeType = contentType, Buffer = content });
            var res = await _context.PostAsync(endpoint, new() { Multipart = form });
            var text = await res.TextAsync();
            if (res.Ok == false)
            {
                throw new InvalidOperationException($"Upload of {fileName} failed: {res.Status} {text}");
            }
            using var doc = JsonDocument.Parse(text);
            Remember(doc.RootElement);
            return doc.RootElement.Clone();
        }

        private void Remember(JsonElement answer)
        {
            if (answer.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String
                && answer.TryGetProperty("LinkRef", out var reference) && reference.ValueKind == JsonValueKind.String)
            {
                _linkRefs[id.GetString()] = reference.GetString();
            }
        }

        /// <summary>A client without credentials, like the <c>img</c> tags of the SPA.</summary>
        public static Task<IAPIRequestContext> CreateAnonymousAsync(IPlaywright playwright, SiteProcess site) =>
            playwright.APIRequest.NewContextAsync(new() { BaseURL = site.BaseUri.ToString() });

        private static async Task<JsonElement> ReadAsync(IAPIResponse res, string url, int expected)
        {
            var text = await res.TextAsync();
            if (res.Status != expected)
            {
                throw new InvalidOperationException($"{url}: expected {expected}, got {res.Status}: {text}");
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                return default;
            }
            try
            {
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return JsonDocument.Parse(JsonSerializer.Serialize(text)).RootElement.Clone();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _context.DisposeAsync();
        }
    }
}
