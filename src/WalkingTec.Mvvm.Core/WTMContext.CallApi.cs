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
                            client.DefaultRequestHeaders.Add(item.Key, item.Value);
                        }
                    }
                }
                if (client.DefaultRequestHeaders.Any(x => x.Key == "Authorization") == false && string.IsNullOrEmpty(LoginUserInfo?.RemoteToken) == false)
                {
                    client.DefaultRequestHeaders.Add("Authorization", "Bearer " + LoginUserInfo?.RemoteToken);
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
