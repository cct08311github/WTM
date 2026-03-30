#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Services
{
    /// <summary>
    /// Default implementation of <see cref="IWtmApiClient"/>.
    /// Logic is an exact copy of the original WTMContext.CallAPI methods
    /// (WTMContext.cs lines 1417-1660) to guarantee identical behaviour.
    /// </summary>
    public class WtmApiClient : IWtmApiClient
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public WtmApiClient(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        }

        #region Core overload (HttpContent)

        public async Task<ApiResult<T>> CallAPI<T>(string? domainName, string? url, HttpMethodEnum method,
            HttpContent? content, int? timeout = null, string? proxy = null,
            Dictionary<string, string>? headers = null, string? authToken = null) where T : class
        {
            ApiResult<T> rv = new ApiResult<T>();
            try
            {
                if (string.IsNullOrEmpty(url))
                {
                    return rv;
                }

                HttpClient client;
                if (string.IsNullOrEmpty(domainName))
                {
                    client = _httpClientFactory.CreateClient();
                    client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.2; SV1; .NET CLR 1.1.4322; .NET CLR 2.0.50727)");
                }
                else
                {
                    client = _httpClientFactory.CreateClient(domainName);
                }

                if (headers != null)
                {
                    foreach (var item in headers)
                    {
                        if (client.DefaultRequestHeaders.Any(x => x.Key == item.Key) == false)
                        {
                            client.DefaultRequestHeaders.Add(item.Key, item.Value);
                        }
                    }
                }

                if (client.DefaultRequestHeaders.Any(x => x.Key == "Authorization") == false
                    && string.IsNullOrEmpty(authToken) == false)
                {
                    client.DefaultRequestHeaders.Add("Authorization", "Bearer " + authToken);
                }

                if (timeout.HasValue)
                {
                    client.Timeout = new TimeSpan(0, 0, 0, timeout.Value, 0);
                }

                HttpResponseMessage? res = null;
                switch (method)
                {
                    case HttpMethodEnum.GET:
                        res = await client.GetAsync(url);
                        break;
                    case HttpMethodEnum.POST:
                        res = await client.PostAsync(url, content);
                        break;
                    case HttpMethodEnum.PUT:
                        res = await client.PutAsync(url, content);
                        break;
                    case HttpMethodEnum.DELETE:
                        res = await client.DeleteAsync(url);
                        break;
                    default:
                        break;
                }

                if (res == null)
                {
                    return rv;
                }

                rv.StatusCode = res.StatusCode;
                if (res.IsSuccessStatusCode == true)
                {
                    Type dt = typeof(T);
                    if (dt == typeof(byte[]))
                    {
                        rv.Data = await res.Content.ReadAsByteArrayAsync() as T;
                    }
                    else
                    {
                        string responseTxt = await res.Content.ReadAsStringAsync();
                        if (dt == typeof(string))
                        {
                            rv.Data = responseTxt as T;
                        }
                        else
                        {
                            rv.Data = JsonSerializer.Deserialize<T>(responseTxt, CoreProgram.DefaultJsonOption);
                        }
                    }
                }
                else
                {
                    string responseTxt = await res.Content.ReadAsStringAsync();
                    if (res.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    {
                        try
                        {
                            rv.Errors = JsonSerializer.Deserialize<ErrorObj>(responseTxt, CoreProgram.DefaultJsonOption);
                        }
                        catch { }
                    }
                    rv.ErrorMsg = responseTxt;
                }

                return rv;
            }
            catch (Exception ex)
            {
                rv.ErrorMsg = ex.ToString();
                return rv;
            }
        }

        #endregion

        #region GET convenience

        public async Task<ApiResult<T>> CallAPI<T>(string? domainName, string? url,
            int? timeout = null, string? proxy = null,
            Dictionary<string, string>? headers = null, string? authToken = null) where T : class
        {
            return await CallAPI<T>(domainName, url, HttpMethodEnum.GET, (HttpContent?)null, timeout, proxy, headers, authToken);
        }

        #endregion

        #region Form-encoded overload

        public async Task<ApiResult<T>> CallAPI<T>(string? domainName, string? url, HttpMethodEnum method,
            IDictionary<string, string>? postdata, int? timeout = null, string? proxy = null,
            Dictionary<string, string>? headers = null, string? authToken = null) where T : class
        {
            HttpContent? content = null;
            if (!(postdata == null || postdata.Count == 0))
            {
                List<KeyValuePair<string, string>> paras = [];
                foreach (string key in postdata.Keys)
                {
                    paras.Add(new KeyValuePair<string, string>(key, postdata[key]));
                }
                content = new FormUrlEncodedContent(paras);
            }
            return await CallAPI<T>(domainName, url, method, content, timeout, proxy, headers, authToken);
        }

        #endregion

        #region JSON object overload

        public async Task<ApiResult<T>> CallAPI<T>(string? domainName, string? url, HttpMethodEnum method,
            object? postdata, int? timeout = null, string? proxy = null,
            Dictionary<string, string>? headers = null, string? authToken = null) where T : class
        {
            HttpContent content = new StringContent(
                JsonSerializer.Serialize(postdata, CoreProgram.DefaultPostJsonOption),
                Encoding.UTF8, "application/json");
            return await CallAPI<T>(domainName, url, method, content, timeout, proxy, headers, authToken);
        }

        #endregion

        #region string-typed convenience overloads

        public async Task<ApiResult<string>> CallAPI(string? domainName, string? url, HttpMethodEnum method,
            HttpContent? content, int? timeout = null, string? proxy = null,
            Dictionary<string, string>? headers = null, string? authToken = null)
        {
            return await CallAPI<string>(domainName, url, method, content, timeout, proxy, headers, authToken);
        }

        public async Task<ApiResult<string>> CallAPI(string? domainName, string? url,
            int? timeout = null, string? proxy = null,
            Dictionary<string, string>? headers = null, string? authToken = null)
        {
            return await CallAPI<string>(domainName, url, timeout, proxy, headers, authToken);
        }

        public async Task<ApiResult<string>> CallAPI(string? domainName, string? url, HttpMethodEnum method,
            IDictionary<string, string>? postdata, int? timeout = null, string? proxy = null,
            Dictionary<string, string>? headers = null, string? authToken = null)
        {
            return await CallAPI<string>(domainName, url, method, postdata, timeout, proxy, headers, authToken);
        }

        public async Task<ApiResult<string>> CallAPI(string? domainName, string? url, HttpMethodEnum method,
            object? postdata, int? timeout = null, string? proxy = null,
            Dictionary<string, string>? headers = null, string? authToken = null)
        {
            return await CallAPI<string>(domainName, url, method, postdata, timeout, proxy, headers, authToken);
        }

        #endregion
    }
}
