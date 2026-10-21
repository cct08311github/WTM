using System;
using System.Collections.Generic;
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

        private static APIRequestContextOptions Body(object body) => new()
        {
            DataString = JsonSerializer.Serialize(body, BodyOptions),
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" }
        };

        public async Task<JsonElement> PostAsync(string url, object body, int expected = 200)
        {
            return await ReadAsync(await _context.PostAsync(url, Body(body)), url, expected);
        }

        public async Task<JsonElement> PutAsync(string url, object body, int expected = 200)
        {
            return await ReadAsync(await _context.PutAsync(url, Body(body)), url, expected);
        }

        /// <summary>Posts and returns the raw response (for downloads, error bodies and expected failures).</summary>
        public Task<IAPIResponse> PostRawAsync(string url, object body) => _context.PostAsync(url, Body(body));

        public Task<IAPIResponse> PutRawAsync(string url, object body) => _context.PutAsync(url, Body(body));

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
            return doc.RootElement.GetProperty("Id").GetString();
        }

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
