#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core.Json;

namespace WalkingTec.Mvvm.Core
{
    public partial class WTMContext
    {
        #region CallApi
        public async Task<ApiResult<T>> CallAPI<T>(string? domainName, string? url, HttpMethodEnum method, HttpContent content, int? timeout = null, string? proxy = null, Dictionary<string, string>? headers = null) where T : class
        {
            ApiResult<T> rv = new ApiResult<T>();
            try
            {
                var factory = this.ServiceProvider?.GetRequiredService<IHttpClientFactory>();
                if (string.IsNullOrEmpty(url))
                {
                    return rv;
                }
                //新建http请求
                HttpClient? client = null;
                if (string.IsNullOrEmpty(domainName))
                {
                    client = factory.CreateClient();
                    client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.2; SV1; .NET CLR 1.1.4322; .NET CLR 2.0.50727)");
                }
                else
                {
                    client = factory.CreateClient(domainName);
                }
                if (headers != null)
                {
                    foreach (var item in headers)
                    {
                        if (client.DefaultRequestHeaders.Any(x => x.Key == item.Key) == false)
                        {
                            try
                            {
                                client.DefaultRequestHeaders.Add(item.Key, item.Value);
                            }
                            catch (FormatException ex)
                            {
                                // #979: do NOT let the original FormatException reach the broad
                                // `catch (Exception ex)` below. For a parser-backed header
                                // (Authorization is the notable one) .NET's own
                                // FormatException.Message embeds the FULL raw attempted value,
                                // and that broad catch hands `ex` whole to
                                // WtmDiagnosticLogger.LogError(...), which a logging sink renders
                                // via Exception.ToString() (the .NET default), leaking the secret
                                // into application logs from nothing more than an operator
                                // mistyping a header value (e.g. an embedded newline from a
                                // paste). Same root cause and same fix shape as #961 (which
                                // fixed RestWidgetDataSource/RestEtlSource, two sites that
                                // already had a header-specific catch to chain-fix); here there
                                // was no header-specific catch at all, so we add a narrow one
                                // INSIDE the existing broad catch rather than widen the broad
                                // catch itself. Keep the header NAME (needed to find the
                                // misconfigured entry) and the exception TYPE (diagnostic value);
                                // drop the value and the original exception object entirely.
                                throw new InvalidOperationException(
                                    $"CallAPI: header '{item.Key}' was rejected by the HTTP stack " +
                                    $"(possible invalid characters or CRLF in name/value; underlying error: {ex.GetType().Name}).");
                            }
                        }
                    }
                }
                if (client.DefaultRequestHeaders.Any(x => x.Key == "Authorization") == false && string.IsNullOrEmpty(LoginUserInfo?.RemoteToken) == false)
                {
                    try
                    {
                        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + LoginUserInfo?.RemoteToken);
                    }
                    catch (FormatException ex)
                    {
                        // #982: scope gap in #979, not a new defect. #979 added a narrow
                        // `catch (FormatException)` around the caller-supplied `headers` loop
                        // above (see that loop's own comment for the full leak mechanics), but
                        // this Authorization add-point -- fed from LoginUserInfo?.RemoteToken,
                        // a DIFFERENT source than the `headers` dictionary -- sits immediately
                        // after that loop's closing brace and was left unprotected. A
                        // CRLF/NUL-bearing RemoteToken throws a FormatException whose .Message
                        // embeds the full attempted "Bearer <token>" value; left unguarded, that
                        // exception falls through to the broad `catch (Exception ex)` below,
                        // which hands it whole to WtmDiagnosticLogger.LogError(...) -- the exact
                        // #979 leak shape, just at the sibling add-point #979 missed. Apply the
                        // identical fix shape: keep the header NAME (Authorization is fixed here,
                        // not attacker-controlled) and the exception TYPE; drop the value and the
                        // original exception object entirely.
                        throw new InvalidOperationException(
                            $"CallAPI: header 'Authorization' was rejected by the HTTP stack " +
                            $"(possible invalid characters or CRLF in name/value; underlying error: {ex.GetType().Name}).");
                    }
                }

                //如果配置了代理，则使用代理
                //设置超时
                if (timeout.HasValue)
                {
                    client.Timeout = new TimeSpan(0, 0, 0, timeout.Value, 0);
                }
                //填充表单数据
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
                    Type? dt = typeof(T);
                    if (dt == typeof(byte[]))
                    {
                        rv.Data = await res.Content.ReadAsByteArrayAsync() as T;
                    }
                    else
                    {
                        string? responseTxt = await res.Content.ReadAsStringAsync();
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
                    string? responseTxt = await res.Content.ReadAsStringAsync();
                    if (res.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    {

                        try
                        {
                            rv.Errors = JsonSerializer.Deserialize<ErrorObj>(responseTxt, CoreProgram.DefaultJsonOption);
                        }
                        catch (Exception) { /* Intentionally ignored: response body may not be JSON-formatted ErrorObj; ErrorMsg is set from raw text below */ }
                    }
                    rv.ErrorMsg = responseTxt;
                }

                return rv;
            }
            catch (Exception ex)
            {
                // Never expose exception detail (which may include connection strings or
                // stack traces) in the ApiResult returned to callers.  Log full detail
                // server-side; surface only a generic message.
                WtmDiagnosticLogger?.LogError(ex, "CallAPI failed to '{Url}'", LogSanitizer.Sanitize(url));
                if (_configInfo?.IsQuickDebug == true)
                {
                    rv.ErrorMsg = ex.ToString();
                }
                else
                {
                    rv.ErrorMsg = "An error occurred while processing the request.";
                }
                return rv;
            }
        }

        /// <summary>
        /// 使用Get方法调用api
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="domainName">Appsettings中配置的Domain key</param>
        /// <param name="url">调用地址</param>
        /// <param name="timeout">超时时间，单位秒</param>
        /// <param name="proxy">代理地址</param>
        /// <param name="headers">http headers</param>
        /// <returns></returns>
        public async Task<ApiResult<T>> CallAPI<T>(string? domainName, string? url, int? timeout = null, string? proxy = null, Dictionary<string, string>? headers = null) where T : class
        {
            HttpContent? content = null;
            //填充表单数据
            return await CallAPI<T>(domainName, url, HttpMethodEnum.GET, content, timeout, proxy, headers);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="domainName">Appsettings中配置的Domain key</param>
        /// <param name="url">调用地址</param>
        /// <param name="method">调用方式</param>
        /// <param name="postdata">提交字段</param>
        /// <param name="timeout">超时时间，单位秒</param>
        /// <param name="proxy">代理地址</param>
        /// <param name="headers">http headers</param>
        /// <returns></returns>
        public async Task<ApiResult<T>> CallAPI<T>(string? domainName, string? url, HttpMethodEnum method, IDictionary<string, string>? postdata, int? timeout = null, string? proxy = null, Dictionary<string, string>? headers = null) where T : class
        {
            HttpContent? content = null;
            //填充表单数据
            if (!(postdata == null || postdata.Count == 0))
            {
                List<KeyValuePair<string, string>> paras = [];
                foreach (string key in postdata.Keys)
                {
                    paras.Add(new KeyValuePair<string, string>(key, postdata[key]));
                }
                content = new FormUrlEncodedContent(paras);
            }
            return await CallAPI<T>(domainName, url, method, content, timeout, proxy, headers);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="domainName">Appsettings中配置的Domain key</param>
        /// <param name="url">调用地址</param>
        /// <param name="method">调用方式</param>
        /// <param name="postdata">提交的object，会被转成json提交</param>
        /// <param name="timeout">超时时间，单位秒</param>
        /// <param name="proxy">代理地址</param>
        /// <param name="headers">http headers</param>
        /// <returns></returns>
        public async Task<ApiResult<T>> CallAPI<T>(string? domainName, string? url, HttpMethodEnum method, object? postdata, int? timeout = null, string? proxy = null, Dictionary<string, string>? headers = null) where T : class
        {
            HttpContent content = new StringContent(JsonSerializer.Serialize(postdata, CoreProgram.DefaultPostJsonOption), System.Text.Encoding.UTF8, "application/json");
            return await CallAPI<T>(domainName, url, method, content, timeout, proxy, headers);
        }

        public async Task<ApiResult<string>> CallAPI(string? domainName, string? url, HttpMethodEnum method, HttpContent content, int? timeout = null, string? proxy = null, Dictionary<string, string>? headers = null)
        {
            return await CallAPI<string>(domainName, url, method, content, timeout, proxy, headers);
        }

        /// <summary>
        /// 使用Get方法调用api
        /// </summary>
        /// <param name="domainName">Appsettings中配置的Domain key</param>
        /// <param name="url">调用地址</param>
        /// <param name="timeout">超时时间，单位秒</param>
        /// <param name="proxy">代理地址</param>
        /// <param name="headers">http headers</param>
        /// <returns></returns>
        public async Task<ApiResult<string>> CallAPI(string? domainName, string? url, int? timeout = null, string? proxy = null, Dictionary<string, string>? headers = null)
        {
            return await CallAPI<string>(domainName, url, timeout, proxy, headers);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="domainName">Appsettings中配置的Domain key</param>
        /// <param name="url">调用地址</param>
        /// <param name="method">调用方式</param>
        /// <param name="postdata">提交字段</param>
        /// <param name="timeout">超时时间，单位秒</param>
        /// <param name="proxy">代理地址</param>
        /// <param name="headers">自定义header</param>
        /// <returns></returns>
        public async Task<ApiResult<string>> CallAPI(string? domainName, string? url, HttpMethodEnum method, IDictionary<string, string>? postdata, int? timeout = null, string? proxy = null, Dictionary<string, string>? headers = null)
        {
            return await CallAPI<string>(domainName, url, method, postdata, timeout, proxy, headers);

        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="domainName">Appsettings中配置的Domain key</param>
        /// <param name="url">调用地址</param>
        /// <param name="method">调用方式</param>
        /// <param name="postdata">提交的object，会被转成json提交</param>
        /// <param name="timeout">超时时间，单位秒</param>
        /// <param name="proxy">代理地址</param>
        /// <param name="headers">http headers</param>
        /// <returns></returns>
        public async Task<ApiResult<string>> CallAPI(string? domainName, string? url, HttpMethodEnum method, object? postdata, int? timeout = null, string? proxy = null, Dictionary<string, string>? headers = null)
        {
            return await CallAPI<string>(domainName, url, method, postdata, timeout, proxy, headers);
        }
        #endregion
    }
}
