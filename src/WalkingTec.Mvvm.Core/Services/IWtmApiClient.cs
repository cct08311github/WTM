#nullable enable
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Services
{
    /// <summary>
    /// Encapsulates HTTP API call logic previously embedded in WTMContext.
    /// Consumers can inject this service directly instead of relying on
    /// WTMContext.CallAPI methods.
    /// </summary>
    public interface IWtmApiClient
    {
        /// <summary>
        /// Core HTTP call with explicit HttpContent.
        /// </summary>
        /// <param name="authToken">Bearer token to attach (if not already present in headers). Pass null to skip.</param>
        Task<ApiResult<T>> CallAPI<T>(string? domainName, string? url, HttpMethodEnum method,
            HttpContent? content, int? timeout = null, string? proxy = null,
            Dictionary<string, string>? headers = null, string? authToken = null) where T : class;

        /// <summary>GET convenience overload.</summary>
        Task<ApiResult<T>> CallAPI<T>(string? domainName, string? url,
            int? timeout = null, string? proxy = null,
            Dictionary<string, string>? headers = null, string? authToken = null) where T : class;

        /// <summary>POST/PUT/DELETE with form-encoded key-value pairs.</summary>
        Task<ApiResult<T>> CallAPI<T>(string? domainName, string? url, HttpMethodEnum method,
            IDictionary<string, string>? postdata, int? timeout = null, string? proxy = null,
            Dictionary<string, string>? headers = null, string? authToken = null) where T : class;

        /// <summary>POST/PUT/DELETE with a JSON-serialized object body.</summary>
        Task<ApiResult<T>> CallAPI<T>(string? domainName, string? url, HttpMethodEnum method,
            object? postdata, int? timeout = null, string? proxy = null,
            Dictionary<string, string>? headers = null, string? authToken = null) where T : class;

        // ---- string-typed convenience overloads ----

        Task<ApiResult<string>> CallAPI(string? domainName, string? url, HttpMethodEnum method,
            HttpContent? content, int? timeout = null, string? proxy = null,
            Dictionary<string, string>? headers = null, string? authToken = null);

        Task<ApiResult<string>> CallAPI(string? domainName, string? url,
            int? timeout = null, string? proxy = null,
            Dictionary<string, string>? headers = null, string? authToken = null);

        Task<ApiResult<string>> CallAPI(string? domainName, string? url, HttpMethodEnum method,
            IDictionary<string, string>? postdata, int? timeout = null, string? proxy = null,
            Dictionary<string, string>? headers = null, string? authToken = null);

        Task<ApiResult<string>> CallAPI(string? domainName, string? url, HttpMethodEnum method,
            object? postdata, int? timeout = null, string? proxy = null,
            Dictionary<string, string>? headers = null, string? authToken = null);
    }
}
