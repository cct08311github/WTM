#nullable enable
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.IdentityModel.Tokens;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Logging.Debug;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Json;
using WalkingTec.Mvvm.Core.Services;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core
{
    public class WTMContext : IDisposable
    {
        /// <summary>
        /// Pre-computed BCrypt hash used to keep authentication response times
        /// comparable when an ITCode does not exist. Prevents username
        /// enumeration via timing-side-channel. The plaintext does not matter
        /// — VerifyPassword will return Failed against any user-supplied password.
        /// </summary>
        private static readonly string _loginDecoyHash =
            BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"));

        private HttpContext? _httpContext;
        public HttpContext? HttpContext { get => _httpContext; }

        private IServiceProvider? _serviceProvider;
        public IServiceProvider? ServiceProvider { get => _serviceProvider ?? _httpContext?.RequestServices; }


        private List<IDataPrivilege>? _dps;
        public List<IDataPrivilege>? DataPrivilegeSettings { get => _dps; }

        private Configs? _configInfo;
        public Configs? ConfigInfo { get => _configInfo; }

        private GlobalData? _globaInfo;
        public GlobalData? GlobaInfo { get => _globaInfo; }

        private IUIService? _uiservice;
        public IUIService? UIService { get => _uiservice; }

        private readonly TimeProvider _timeProvider;
        /// <summary>
        /// 提供可測試的時間來源，取代直接使用 DateTime.Now/UtcNow。
        /// 預設為 TimeProvider.System；測試可注入 FakeTimeProvider。
        /// </summary>
        public TimeProvider TimeProvider => _timeProvider;

        private IDistributedCache? _cache;
        public IDistributedCache? Cache { get { return _cache; } }

        public string? CurrentCS {
            get;
            set;
        }

        public DBTypeEnum? CurrentDbType { get; set; }

        public string ParentWindowId
        {
            get
            {
                string? rv = null;
                if (WindowIds != null)
                {
                    var ids = WindowIds.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (ids.Length > 1)
                    {
                        rv = ids[ids.Length - 2];
                    }
                }

                return rv ?? string.Empty;
            }
        }

        public string CurrentWindowId
        {
            get
            {
                string? rv = null;
                if (WindowIds != null)
                {
                    var ids = WindowIds.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (ids.Length > 0)
                    {
                        rv = ids[ids.Length - 1];
                    }
                }

                return rv ?? string.Empty;
            }
        }

        public string WindowIds
        {
            get
            {
                string rv = string.Empty;
                try
                {
                    if (HttpContext?.Request.Cookies.TryGetValue($"{ConfigInfo?.CookiePre}windowguid", out string? windowguid) == true)
                    {

                        if (HttpContext?.Request.Cookies.TryGetValue($"{ConfigInfo?.CookiePre}{windowguid}windowids", out string? windowid) == true)
                        {
                            rv = windowid;
                        }
                    }
                }
                catch (Exception) { /* Intentionally ignored: cookie read may fail if cookies are malformed or unavailable */ }
                return rv;
            }
        }

        public ISessionService? Session { get; set; }

        public IModelStateService? MSD { get; set; }

        public static Func<WTMContext, string, LoginUserInfo>? ReloadUserFunc { get; set; }

        #region DataContext

        private IDataContext? _dc;
        public IDataContext? DC {
            get
            {
                if (_dc == null)
                {
                    _dc = this.CreateDC();
                    if (_dc != null)
                    {
                        _dc.CurrentUserCode = LoginUserInfo?.ITCode;
                        // Property injection：讓 FrameworkContext.SaveChanges 能自動失效 [CacheLookup] 快取
                        if (_dc is WalkingTec.Mvvm.Core.FrameworkContext fc)
                            fc.LookupCacheService = ServiceProvider?.GetService(typeof(WalkingTec.Mvvm.Core.Cache.ILookupCacheService))
                                                    as WalkingTec.Mvvm.Core.Cache.ILookupCacheService;
                    }
                }
                return _dc;
            }
            set
            {
                _dc = value;
                TrySetDataContextTimeProvider(_dc);
            }
        }

        #endregion

        #region Current User

        private LoginUserInfo? _loginUserInfo;
        public LoginUserInfo? LoginUserInfo {
            get
            {
                if (_loginUserInfo == null && HttpContext?.User?.Identity?.IsAuthenticated == true) // 用户认证通过后，当前上下文不包含用户数据
                {
                    var userIdStr = HttpContext.User.Claims.Where(x => x.Type == AuthConstants.JwtClaimTypes.Subject).Select(x => x.Value).FirstOrDefault();
                    var tenant = HttpContext.User.Claims.Where(x => x.Type == AuthConstants.JwtClaimTypes.TenantCode).Select(x => x.Value).FirstOrDefault();
                    string? usercode = userIdStr;
                    var cacheKey = $"{GlobalConstants.CacheKey.UserInfo}:{userIdStr + "$`$" + tenant}";
                    _loginUserInfo = Cache?.Get<LoginUserInfo>(cacheKey);
                    if (_loginUserInfo == null)
                    {
                        try
                        {
                            _loginUserInfo = ReloadUser(usercode);
                        }
                        catch (Exception ex)
                        {
                            ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("WTMContext")?.LogWarning(ex, "Failed to reload user info for usercode '{UserCode}'", usercode);
                        }
                        if (_loginUserInfo != null)
                        {
                            Cache?.Add(cacheKey, _loginUserInfo);
                        }
                        else
                        {
                            return null!;
                        }
                    }
                }
                if (_loginUserInfo == null && HttpContext?.Request.Query.Any(x => x.Key == "_remotetoken") == true)
                {
                    var remoteToken = HttpContext?.Request.Query["_remotetoken"][0];
                    if (ConfigInfo?.HasMainHost == false)
                    {
                        // Validate JWT signature — never trust an unverified token (#765)
                        var jwtOpts = ConfigInfo.JwtOptions;
                        var handler = new JwtSecurityTokenHandler();
                        var validationParams = new TokenValidationParameters
                        {
                            ValidateIssuerSigningKey = true,
                            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOpts.SecurityKey)),
                            ValidateIssuer = true,
                            ValidIssuer = jwtOpts.Issuer,
                            ValidateAudience = true,
                            ValidAudience = jwtOpts.Audience,
                            ValidateLifetime = true,
                        };

                        try
                        {
                            var principal = handler.ValidateToken(remoteToken, validationParams, out _);
                            var userIdStr = principal.Claims.Where(x => x.Type == AuthConstants.JwtClaimTypes.Subject).Select(x => x.Value).FirstOrDefault();
                            var tenant = principal.Claims.Where(x => x.Type == AuthConstants.JwtClaimTypes.TenantCode).Select(x => x.Value).FirstOrDefault();
                            string? usercode = userIdStr;
                            var cacheKey = $"{GlobalConstants.CacheKey.UserInfo}:{userIdStr + "$`$" + tenant}";
                            _loginUserInfo = Cache?.Get<LoginUserInfo>(cacheKey);
                            if (_loginUserInfo == null)
                            {
                                _loginUserInfo = ReloadUser(usercode);
                                if (_loginUserInfo != null)
                                {
                                    Cache?.Add(cacheKey, _loginUserInfo);
                                }
                                else
                                {
                                    return null!;
                                }
                            }
                        }
                        catch (SecurityTokenException)
                        {
                            // Signature validation failed — reject the token
                            return null!;
                        }
                        catch (Exception ex)
                        {
                            ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("WTMContext")?.LogWarning(ex, "JWT token validation failed unexpectedly (non-signature error)");
                            return null!;
                        }
                    }
                    else if (string.IsNullOrEmpty(remoteToken) == false)
                    {
                        try
                        {
                            _loginUserInfo = ReloadUser("null");
                        }
                        catch (Exception ex)
                        {
                            ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("WTMContext")?.LogWarning(ex, "Failed to reload user info via remote token");
                        }
                        if (_loginUserInfo != null)
                        {
                            var cacheKey = $"{GlobalConstants.CacheKey.UserInfo}:{_loginUserInfo.ITCode + "$`$" + _loginUserInfo.TenantCode}";
                            Cache?.Add(cacheKey, _loginUserInfo);
                        }
                        else
                        {
                            return null!;
                        }
                    }
                }
                return _loginUserInfo;
            }
            set
            {
                if (value == null)
                {
                    Cache?.Delete($"{GlobalConstants.CacheKey.UserInfo}:{_loginUserInfo?.ITCode + "$`$" + _loginUserInfo?.TenantCode}");
                    _loginUserInfo = value;
                }
                else
                {
                    _loginUserInfo = value;
                    Cache?.Add($"{GlobalConstants.CacheKey.UserInfo}:{_loginUserInfo.ITCode + "$`$" + _loginUserInfo?.TenantCode}", value);
                }
            }
        }

        private Type? _localizerType;
        private IStringLocalizerFactory? _stringLocalizerFactory;
        private IStringLocalizer? _localizer;
        private ILoggerFactory? _loggerFactory;
        public ILoggerFactory? LoggerFactory { get { return _loggerFactory; } }
        public IStringLocalizer? Localizer {
            get
            {
                if (_localizer == null && _stringLocalizerFactory != null)
                {
                    if (_localizerType == null)
                    {
                        _localizerType = Assembly.GetEntryAssembly()?.GetTypes().Where(x => x.Name == "Program").FirstOrDefault();
                    }
                    _localizer = _stringLocalizerFactory.Create(_localizerType!);
                }
                return _localizer ?? WalkingTec.Mvvm.Core.CoreProgram._localizer;
            }
        }

        /// <summary>
        /// 从数据库读取用户
        /// </summary>
        /// <param name="itcode">用户名</param>
        /// <returns>用户信息</returns>
        public virtual LoginUserInfo?
            ReloadUser(string? itcode)
        {
            if (ReloadUserFunc != null)
            {
                var reload = ReloadUserFunc?.Invoke(this, itcode ?? string.Empty);
                if (reload != null)
                {
                    return reload;
                }
            }
            if (DC == null)
            {
                return null!;
            }
            var user = DoLoginAsync(itcode, null, null).GetAwaiter().GetResult();
            return user;
        }

        /// <summary>
        /// Async twin of <see cref="ReloadUser"/>. Resolves the user from
        /// <see cref="ReloadUserFunc"/> or <see cref="DoLoginAsync"/> without
        /// blocking a ThreadPool thread.
        /// </summary>
        /// <param name="itcode">User code (ITCode) to load.</param>
        /// <returns>The resolved <see cref="LoginUserInfo"/>, or <c>null</c> if not found.</returns>
        public virtual async Task<LoginUserInfo?> ReloadUserAsync(string? itcode)
        {
            if (ReloadUserFunc != null)
            {
                var reload = ReloadUserFunc.Invoke(this, itcode ?? string.Empty);
                if (reload != null)
                {
                    return reload;
                }
            }
            if (DC == null)
            {
                return null;
            }
            return await DoLoginAsync(itcode, null, null).ConfigureAwait(false);
        }

        /// <summary>
        /// Pre-resolves <see cref="LoginUserInfo"/> asynchronously for the current
        /// HTTP request. Call this from middleware (after <c>UseAuthentication</c>)
        /// so that the subsequent synchronous <see cref="LoginUserInfo"/> getter on
        /// the hot path finds <c>_loginUserInfo</c> already populated and does not
        /// need to block a ThreadPool thread via <c>GetAwaiter().GetResult()</c>.
        ///
        /// This method mirrors both identity-bearing branches of the sync
        /// <see cref="LoginUserInfo"/> getter:
        /// <list type="number">
        ///   <item>
        ///     <term>Authenticated-user branch</term>
        ///     <description>
        ///       Guarded by <c>HttpContext?.User?.Identity?.IsAuthenticated == true</c>.
        ///       Extracts Subject/TenantCode claims, checks cache, and on miss calls
        ///       <c>await ReloadUserAsync</c> instead of the blocking <c>ReloadUser</c>.
        ///     </description>
        ///   </item>
        ///   <item>
        ///     <term>Remote-token branch</term>
        ///     <description>
        ///       Guarded by a <c>_remotetoken</c> query parameter being present.
        ///       Eliminates the <c>ReloadUser(...).GetAwaiter().GetResult()</c> call
        ///       that previously caused ThreadPool starvation when the
        ///       <c>HasMainHost == true</c> sub-case performed a blocking HTTP
        ///       <c>CallAPI("mainhost", ...)</c>. Both sub-cases now use
        ///       <c>await ReloadUserAsync</c> instead.
        ///     </description>
        ///   </item>
        /// </list>
        ///
        /// The method is a no-op when <c>_loginUserInfo</c> is already set.
        /// </summary>
        public async Task EnsureLoginUserInfoAsync()
        {
            // Short-circuit: already resolved (covers both branches below).
            if (_loginUserInfo != null)
            {
                return;
            }

            // ── Branch 1: standard JWT-authenticated request ─────────────────────
            // Mirrors the first if-block in the sync LoginUserInfo getter exactly.
            if (HttpContext?.User?.Identity?.IsAuthenticated == true)
            {
                var userIdStr = HttpContext.User.Claims
                    .Where(x => x.Type == AuthConstants.JwtClaimTypes.Subject)
                    .Select(x => x.Value)
                    .FirstOrDefault();
                var tenant = HttpContext.User.Claims
                    .Where(x => x.Type == AuthConstants.JwtClaimTypes.TenantCode)
                    .Select(x => x.Value)
                    .FirstOrDefault();
                string? usercode = userIdStr;

                // Cache key must be identical to the one built in the sync getter
                // so that the getter hits the cache on its first access.
                var cacheKey = $"{GlobalConstants.CacheKey.UserInfo}:{userIdStr + "$`$" + tenant}";
                _loginUserInfo = Cache?.Get<LoginUserInfo>(cacheKey);

                if (_loginUserInfo == null)
                {
                    try
                    {
                        _loginUserInfo = await ReloadUserAsync(usercode).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("WTMContext")
                            ?.LogWarning(ex, "EnsureLoginUserInfoAsync: failed to reload user info for usercode '{UserCode}'", usercode);
                    }

                    if (_loginUserInfo != null)
                    {
                        Cache?.Add(cacheKey, _loginUserInfo);
                    }
                }
            }

            // ── Branch 2: _remotetoken query-parameter request ───────────────────
            // Mirrors the second if-block in the sync LoginUserInfo getter exactly,
            // but replaces the blocking ReloadUser() calls with async equivalents
            // to eliminate ThreadPool starvation under load.
            if (_loginUserInfo == null && HttpContext?.Request.Query.Any(x => x.Key == "_remotetoken") == true)
            {
                var remoteToken = HttpContext?.Request.Query["_remotetoken"][0];
                if (ConfigInfo?.HasMainHost == false)
                {
                    // Validate JWT signature — never trust an unverified token (#765)
                    var jwtOpts = ConfigInfo.JwtOptions;
                    var handler = new JwtSecurityTokenHandler();
                    var validationParams = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOpts.SecurityKey)),
                        ValidateIssuer = true,
                        ValidIssuer = jwtOpts.Issuer,
                        ValidateAudience = true,
                        ValidAudience = jwtOpts.Audience,
                        ValidateLifetime = true,
                    };

                    try
                    {
                        var principal = handler.ValidateToken(remoteToken, validationParams, out _);
                        var userIdStr = principal.Claims.Where(x => x.Type == AuthConstants.JwtClaimTypes.Subject).Select(x => x.Value).FirstOrDefault();
                        var tenant = principal.Claims.Where(x => x.Type == AuthConstants.JwtClaimTypes.TenantCode).Select(x => x.Value).FirstOrDefault();
                        string? usercode = userIdStr;
                        var cacheKey = $"{GlobalConstants.CacheKey.UserInfo}:{userIdStr + "$`$" + tenant}";
                        _loginUserInfo = Cache?.Get<LoginUserInfo>(cacheKey);
                        if (_loginUserInfo == null)
                        {
                            _loginUserInfo = await ReloadUserAsync(usercode).ConfigureAwait(false);
                            if (_loginUserInfo != null)
                            {
                                Cache?.Add(cacheKey, _loginUserInfo);
                            }
                            else
                            {
                                return;
                            }
                        }
                    }
                    catch (SecurityTokenException)
                    {
                        // Signature validation failed — reject the token
                        return;
                    }
                    catch (Exception ex)
                    {
                        ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("WTMContext")?.LogWarning(ex, "EnsureLoginUserInfoAsync: JWT token validation failed unexpectedly (non-signature error)");
                        return;
                    }
                }
                else if (string.IsNullOrEmpty(remoteToken) == false)
                {
                    try
                    {
                        _loginUserInfo = await ReloadUserAsync("null").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("WTMContext")?.LogWarning(ex, "EnsureLoginUserInfoAsync: failed to reload user info via remote token");
                    }
                    if (_loginUserInfo != null)
                    {
                        var cacheKey = $"{GlobalConstants.CacheKey.UserInfo}:{_loginUserInfo.ITCode + "$`$" + _loginUserInfo.TenantCode}";
                        Cache?.Add(cacheKey, _loginUserInfo);
                    }
                    else
                    {
                        return;
                    }
                }
            }
        }

        #endregion

        #region URL
        public string? BaseUrl { get; set; }
        #endregion

        #region LookupCache

        /// <summary>
        /// 取得靜態/參數表的快取資料（cache miss 時自動從 DB 補充）。
        /// Model 必須標記 <see cref="WalkingTec.Mvvm.Core.Cache.CacheLookupAttribute"/>。
        /// </summary>
        /// <param name="predicate">可選的記憶體過濾條件（Func，在記憶體中執行），不觸發額外 DB 查詢。</param>
        /// <returns>IReadOnlyList 防止呼叫端意外修改快取內容。</returns>
        public System.Collections.Generic.IReadOnlyList<T> GetLookup<T>(
            System.Func<T, bool>? predicate = null) where T : TopBasePoco
        {
            var svc = ServiceProvider?.GetService(typeof(WalkingTec.Mvvm.Core.Cache.ILookupCacheService))
                      as WalkingTec.Mvvm.Core.Cache.ILookupCacheService;
            if (svc == null)
                throw new InvalidOperationException(
                    "ILookupCacheService is not registered. Call services.AddWtmContext() first.");

            var attr = svc.GetAttribute(typeof(T));
            Microsoft.EntityFrameworkCore.DbContext dbCtx;
            IDataContext? altDc = null;
            try
            {
                if (!string.IsNullOrEmpty(attr?.ConnectionKey))
                {
                    altDc = CreateDC(cskey: attr.ConnectionKey);
                    dbCtx = altDc as Microsoft.EntityFrameworkCore.DbContext
                        ?? throw new InvalidOperationException(
                            $"ConnectionKey '{attr.ConnectionKey}' did not produce an EF Core DbContext.");
                }
                else
                {
                    dbCtx = DC as Microsoft.EntityFrameworkCore.DbContext
                        ?? throw new InvalidOperationException(
                            "GetLookup requires an EF Core DbContext. Ensure IDataContext is configured.");
                }

                // 解析 tenant isolation：Attribute 明確設定優先，否則使用全域預設
                bool useTenant = attr?.TenantIsolationOrNull ?? svc.DefaultTenantIsolation;
                var tenantId = useTenant ? LoginUserInfo?.TenantCode : null;

                var all = svc.GetAll<T>(dbCtx, tenantId);
                return predicate == null ? all : (System.Collections.Generic.IReadOnlyList<T>)[.. all.Where(predicate)];
            }
            finally
            {
                (altDc as IDisposable)?.Dispose();
            }
        }

        /// <summary>
        /// 非同步取得靜態/參數表的快取資料（cache miss 時自動從 DB 補充）。
        /// Model 必須標記 <see cref="WalkingTec.Mvvm.Core.Cache.CacheLookupAttribute"/>。
        /// </summary>
        /// <param name="predicate">可選的記憶體過濾條件（Func，在記憶體中執行），不觸發額外 DB 查詢。</param>
        /// <param name="ct">取消 token。</param>
        /// <returns>IReadOnlyList 防止呼叫端意外修改快取內容。</returns>
        public async System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<T>> GetLookupAsync<T>(
            System.Func<T, bool>? predicate = null,
            System.Threading.CancellationToken ct = default) where T : TopBasePoco
        {
            var svc = ServiceProvider?.GetService(typeof(WalkingTec.Mvvm.Core.Cache.ILookupCacheService))
                      as WalkingTec.Mvvm.Core.Cache.ILookupCacheService;
            if (svc == null)
                throw new InvalidOperationException(
                    "ILookupCacheService is not registered. Call services.AddWtmContext() first.");

            var attr = svc.GetAttribute(typeof(T));
            Microsoft.EntityFrameworkCore.DbContext dbCtx;
            IDataContext? altDc = null;
            try
            {
                if (!string.IsNullOrEmpty(attr?.ConnectionKey))
                {
                    altDc = CreateDC(cskey: attr.ConnectionKey);
                    dbCtx = altDc as Microsoft.EntityFrameworkCore.DbContext
                        ?? throw new InvalidOperationException(
                            $"ConnectionKey '{attr.ConnectionKey}' did not produce an EF Core DbContext.");
                }
                else
                {
                    dbCtx = DC as Microsoft.EntityFrameworkCore.DbContext
                        ?? throw new InvalidOperationException(
                            "GetLookupAsync requires an EF Core DbContext. Ensure IDataContext is configured.");
                }

                bool useTenant = attr?.TenantIsolationOrNull ?? svc.DefaultTenantIsolation;
                var tenantId = useTenant ? LoginUserInfo?.TenantCode : null;

                var all = await svc.GetAllAsync<T>(dbCtx, tenantId, ct).ConfigureAwait(false);
                return predicate == null ? all : (System.Collections.Generic.IReadOnlyList<T>)[.. all.Where(predicate)];
            }
            finally
            {
                (altDc as IDisposable)?.Dispose();
            }
        }

        /// <summary>
        /// 從快取中查找單筆資料。
        /// </summary>
        public T? GetLookupItem<T>(System.Func<T, bool> predicate) where T : TopBasePoco
        {
            return GetLookup<T>().FirstOrDefault(predicate);
        }

        /// <summary>
        /// 從快取生成下拉選單項目（ComboSelectListItem）。
        /// </summary>
        public System.Collections.Generic.List<ComboSelectListItem> GetLookupSelectList<T>(
            System.Func<T, object> valueField,
            System.Func<T, string> textField,
            System.Func<T, bool>? filter = null) where T : TopBasePoco
        {
            var items = filter == null
                ? (System.Collections.Generic.IEnumerable<T>)GetLookup<T>()
                : GetLookup<T>().Where(filter);
            return [.. items.Select(x => new ComboSelectListItem
            {
                Value = valueField(x)?.ToString(),
                Text = textField(x)
            })];
        }

        /// <summary>
        /// 強制重新載入快取：先失效所有租戶的快取，再立即從 DB 重新查詢填入。
        /// 適用於 admin 批次匯入後，避免失效後首次請求的冷查詢。
        /// </summary>
        public async System.Threading.Tasks.Task RefreshLookupAsync<T>(
            System.Threading.CancellationToken ct = default) where T : TopBasePoco
        {
            var svc = ServiceProvider?.GetService(typeof(WalkingTec.Mvvm.Core.Cache.ILookupCacheService))
                      as WalkingTec.Mvvm.Core.Cache.ILookupCacheService;
            if (svc == null)
                throw new InvalidOperationException(
                    "ILookupCacheService is not registered. Call services.AddWtmContext() first.");

            var attr = svc.GetAttribute(typeof(T));
            Microsoft.EntityFrameworkCore.DbContext dbCtx;
            IDataContext? altDc = null;
            try
            {
                if (!string.IsNullOrEmpty(attr?.ConnectionKey))
                {
                    altDc = CreateDC(cskey: attr.ConnectionKey);
                    dbCtx = altDc as Microsoft.EntityFrameworkCore.DbContext
                        ?? throw new InvalidOperationException(
                            $"ConnectionKey '{attr.ConnectionKey}' did not produce an EF Core DbContext.");
                }
                else
                {
                    dbCtx = DC as Microsoft.EntityFrameworkCore.DbContext
                        ?? throw new InvalidOperationException(
                            "RefreshLookupAsync requires an EF Core DbContext.");
                }

                bool useTenant = attr?.TenantIsolationOrNull ?? svc.DefaultTenantIsolation;
                var tenantId = useTenant ? LoginUserInfo?.TenantCode : null;
                await svc.RefreshAsync<T>(dbCtx, tenantId, ct).ConfigureAwait(false);
            }
            finally
            {
                (altDc as IDisposable)?.Dispose();
            }
        }

        #endregion

        public string HostAddress { get
            {
                if (this.HttpContext?.Request != null)
                {
                    return $"{this.HttpContext?.Request.Scheme}://{this.HttpContext?.Request.Host}";
                }
                else
                {
                    return "";
                }
            }
        }

        public SimpleLog? Log { get; set; }

        protected ILogger<ActionLog>? Logger { get; set; }


        private IQueryable<FrameworkUserBase>? _baseUserQuery;
        public IQueryable<FrameworkUserBase>? BaseUserQuery {
            get
            {
                if (_baseUserQuery == null && this.GlobaInfo?.CustomUserType != null && DC != null)
                {
                    var set = DC?.GetType()?.GetMethod("Set", Type.EmptyTypes)?.MakeGenericMethod(GlobaInfo!.CustomUserType!);
                    _baseUserQuery = set?.Invoke(DC, null) as IQueryable<FrameworkUserBase>;
                }
                return _baseUserQuery;
            }
        }

        public WTMContext(IOptionsMonitor<Configs>? _config, GlobalData? _gd = null, IHttpContextAccessor? _http = null, IUIService? _ui = null, List<IDataPrivilege>? _dp = null, IDataContext? dc = null, IStringLocalizerFactory? stringLocalizer = null, ILoggerFactory? loggerFactory = null, WtmLocalizationOption? lop = null, IDistributedCache? cache = null, IServiceProvider? sp=null, TimeProvider? timeProvider = null)
        {
            _timeProvider = timeProvider ?? TimeProvider.System;
            _configInfo = _config?.CurrentValue ?? new Configs();
            _globaInfo = _gd ?? new GlobalData();
            _httpContext = _http?.HttpContext;
            _cache = cache;
            _stringLocalizerFactory = stringLocalizer;
            _loggerFactory = loggerFactory;
            _localizerType = lop?.LocalizationType;
            this.Logger = loggerFactory?.CreateLogger<ActionLog>();
            if (_httpContext == null)
            {
                MSD = new BasicMSD();
            }
            _uiservice = _ui;
            if (_dp == null)
            {
                _dp = [];
            }
            _dps = _dp;
            if (dc is NullContext)
            {
                _dc = null;
            }
            else
            {
                _dc = dc;
                TrySetDataContextTimeProvider(_dc);
            }
            _serviceProvider = sp;
        }

        private void TrySetDataContextTimeProvider(IDataContext? dc)
        {
            if (dc is EmptyContext ctx)
            {
                ctx.TimeProvider = _timeProvider;
            }
        }

        public void SetServiceProvider(IServiceProvider? sp)
        {
            this._serviceProvider = sp;
        }

        public async Task<LoginUserInfo?> DoLoginAsync(string? username, string? password, string? tenant)
        {
            if(string.IsNullOrEmpty(tenant))
            {
                tenant = DC!.TenantCode;
            }
            if (tenant == null && HttpContext?.User?.Identity?.IsAuthenticated == true)
            {
                tenant = HttpContext.User.Claims.Where(x => x.Type == AuthConstants.JwtClaimTypes.TenantCode).Select(x => x.Value).FirstOrDefault() ?? tenant;
            }
            if (ConfigInfo?.HasMainHost == true && string.IsNullOrEmpty(tenant) == true)
            {
                var remoteToken = _loginUserInfo?.RemoteToken ?? HttpContext?.Request.Query?.Where(x => x.Key == "_remotetoken").Select(x => x.Value.First()).FirstOrDefault();
                if (HttpContext?.User?.Identity?.IsAuthenticated == true)
                {
                    remoteToken = HttpContext.User.Claims.Where(x => x.Type == AuthConstants.JwtClaimTypes.RToken).Select(x => x.Value).FirstOrDefault();
                }
                LoginUserInfo? rv = null;
                if (string.IsNullOrEmpty(remoteToken) == false)
                {
                    Dictionary<string, string> headers = new Dictionary<string, string>();
                    headers.Add("Authorization", "Bearer " + remoteToken);
                    var user = await CallAPI<LoginUserInfo>("mainhost", "/api/_account/checkuserinfo?IsApi=false", HttpMethodEnum.GET, new { }, 10, headers: headers);
                    rv = user.Data;
                    if (rv != null)
                    {
                        rv.RemoteToken = remoteToken;
                    }
                }
                else if(string.IsNullOrEmpty(password)==false)
                {
                    var loginjwt = await CallAPI<Token>("mainhost", "/api/_account/loginjwt", HttpMethodEnum.POST, new { Account = username, Password = password }, 10);
                    if (string.IsNullOrEmpty(loginjwt?.Data?.AccessToken) == false)
                    {
                        remoteToken = loginjwt?.Data?.AccessToken;
                        Dictionary<string, string> headers = new Dictionary<string, string>();
                        headers.Add("Authorization", "Bearer " + remoteToken);
                        var user = await CallAPI<LoginUserInfo>("mainhost", "/api/_account/checkuserinfo?IsApi=false", HttpMethodEnum.GET, new { }, 10, headers: headers);
                        rv = user.Data;
                        if (rv != null)
                        {
                            rv.RemoteToken = remoteToken;
                        }
                    }
                }
                if (rv != null)
                {
                    await rv.LoadBasicInfoAsync(this);
                }
                return rv;
            }
            else
            {
                bool exist = false;
                username = HttpContext?.User?.Claims?.Where(x => x.Type == AuthConstants.JwtClaimTypes.Subject).Select(x => x.Value).FirstOrDefault() ?? username;
                var ct = GlobaInfo?.AllTenant.Where(x => x.TCode == tenant).FirstOrDefault();
                if(ct == null && string.IsNullOrEmpty(tenant) == false)
                {
                    return null!;
                }
                if (ct != null)
                {
                    // Dispose any DataContext that was lazily created by the DC property
                    // getter above (e.g. via DC!.TenantCode) before overwriting _dc to
                    // prevent a resource leak (#9 / Issue #378).
                    (_dc as IDisposable)?.Dispose();
                    _dc = ct.CreateDC(this);
                }
                if (HttpContext?.User?.Identity?.IsAuthenticated == true)
                {
                    // IgnoreQueryFilters: cross-tenant user existence probe during login
                    exist = BaseUserQuery!.IgnoreQueryFilters().Any(x => x.ITCode == username && x.TenantCode == tenant && x.IsValid==true);
                }
                else
                {
                    // IgnoreQueryFilters: cross-tenant password lookup during anonymous login
                    var userRecord = BaseUserQuery!.IgnoreQueryFilters()
                        .Where(x => x.ITCode == username &&
                                    x.TenantCode == tenant &&
                                    x.IsValid == true)
                        .Select(x => new { x.ITCode, x.Password })
                        .FirstOrDefault();
                    if (userRecord != null)
                    {
                        var verifyResult = PasswordHashHelper.VerifyPassword(
                            userRecord.Password, password);
                        if (verifyResult == PasswordVerifyResult.Failed)
                        {
                            exist = false;
                        }
                        else
                        {
                            exist = true;
                            if (verifyResult == PasswordVerifyResult.SuccessRehashNeeded)
                            {
                                // IgnoreQueryFilters: load full user record across tenants after verification
                                var fullUser = BaseUserQuery.IgnoreQueryFilters()
                                    .FirstOrDefault(x => x.ITCode == username &&
                                                         x.TenantCode == tenant);
                                if (fullUser != null)
                                {
                                    fullUser.Password = PasswordHashHelper.HashPassword(password);
                                    await DC.SaveChangesAsync();
                                }
                            }
                        }
                    }
                    else
                    {
                        // Discarded BCrypt to keep response timing comparable with the
                        // user-exists / wrong-password path. Prevents username enumeration
                        // via timing-side-channel.
                        _ = PasswordHashHelper.VerifyPassword(_loginDecoyHash, password);
                        exist = false;
                    }
                }
                if (exist == false)
                {
                    return null!;
                }

                LoginUserInfo? user = new LoginUserInfo
                {
                    ITCode = username,
                    TenantCode = tenant
                };
                await user.LoadBasicInfoAsync(this);
                user.RemoteToken = null;
                var authService = HttpContext?.RequestServices.GetService(typeof(ITokenService)) as ITokenService;
                var token = await authService!.IssueTokenAsync(user);
                user.RemoteToken = token.AccessToken;
                return user;
            }
        }

        [Obsolete("Use DoLoginAsync to avoid ThreadPool starvation. DoLogin blocks threads on every auth request.")]
        public LoginUserInfo? DoLogin(string? username, string? password, string? tenant)
        {
            return DoLoginAsync(username, password, tenant).GetAwaiter().GetResult();
        }

        public async Task<Token?> RefreshTokenAsync()
        {
            if (LoginUserInfo == null)
            {
                return null!;
            }
            string rt = null;
            if (ConfigInfo?.HasMainHost == true && LoginUserInfo?.CurrentTenant == null)
            {
                var r = await CallAPI<Token>("mainhost", $"/api/_account/RefreshToken", HttpMethodEnum.POST, new { });
                rt = r?.Data?.AccessToken;
            }
            else
            {
                rt = LoginUserInfo?.RemoteToken;
            }
            var _authService = ServiceProvider?.GetRequiredService<ITokenService>();
            var rv = await _authService.IssueTokenAsync(new LoginUserInfo
            {
                ITCode = LoginUserInfo?.ITCode,
                TenantCode = LoginUserInfo?.TenantCode,
                RemoteToken = rt
            });
            return rv;
        }

        [Obsolete("Use RefreshTokenAsync to avoid ThreadPool starvation. RefreshToken blocks threads on every token refresh.")]
        public Token? RefreshToken()
        {
            return RefreshTokenAsync().GetAwaiter().GetResult();
        }

        public T ReadFromCache<T>(string key, Func<T> setFunc, int? timeout = null)
        {
            if (Cache.TryGetValue(key, out T rv) == false || rv == null)
            {
                T data = setFunc();
                if (timeout == null)
                {
                    Cache?.Add(key, data);
                }
                else
                {
                    Cache?.Add(key, data, new DistributedCacheEntryOptions()
                    {
                        AbsoluteExpirationRelativeToNow = new TimeSpan(0, 0, timeout.Value)
                    });
                }
                return data;
            }
            else
            {
                return rv;
            }
        }

        public async Task RemoveUserCache(
            params string[] userIds)
        {
            var svc = ServiceProvider?.GetService(typeof(IWtmUserCacheService)) as IWtmUserCacheService;
            if (svc != null) { await svc.RemoveUserCacheAsync(LoginUserInfo?.CurrentTenant, userIds); return; }
            foreach (var userId in userIds)
            {
                var key = $"{GlobalConstants.CacheKey.UserInfo}:{userId + "$`$" + LoginUserInfo?.CurrentTenant}";
                await Cache?.DeleteAsync(key);
            }
        }

        public async Task RemoveUserCacheByRole(
    params string[] rolecode)
        {
            var svc = ServiceProvider?.GetService(typeof(IWtmUserCacheService)) as IWtmUserCacheService;
            var apiClient = ServiceProvider?.GetService(typeof(IWtmApiClient)) as IWtmApiClient;
            if (svc != null) { await svc.RemoveUserCacheByRoleAsync(LoginUserInfo?.CurrentTenant, ConfigInfo?.HasMainHost == true, DC, apiClient, rolecode); return; }
            List<string> userids = [];
            if (ConfigInfo?.HasMainHost == true && string.IsNullOrEmpty(LoginUserInfo?.CurrentTenant) == true)
            {
                foreach (var item in rolecode)
                {
                    var rv = await CallAPI<List<string>>("mainhost", $"/api/_frameworkuser/GetUserByRole?keywords={item}");
                    if (rv != null && rv.Data != null)
                    {
                        userids.AddRange(rv.Data);
                    }
                }
            }
            else
            {
                userids = [.. DC.Set<FrameworkUserRole>().Where(x => rolecode.Contains(x.RoleCode)).Select(x => x.UserCode)];
            }
            foreach (var userId in userids)
            {
                var key = $"{GlobalConstants.CacheKey.UserInfo}:{userId + "$`$" + LoginUserInfo?.CurrentTenant}";
                await Cache?.DeleteAsync(key);
            }
        }

        public async Task RemoveUserCacheByGroup(
params string[] groupcode)
        {
            var svc = ServiceProvider?.GetService(typeof(IWtmUserCacheService)) as IWtmUserCacheService;
            var apiClient = ServiceProvider?.GetService(typeof(IWtmApiClient)) as IWtmApiClient;
            if (svc != null) { await svc.RemoveUserCacheByGroupAsync(LoginUserInfo?.CurrentTenant, ConfigInfo?.HasMainHost == true, DC, apiClient, groupcode); return; }
            List<string> userids = [];
            if (ConfigInfo?.HasMainHost == true && string.IsNullOrEmpty(LoginUserInfo?.CurrentTenant) == true)
            {
                foreach (var item in groupcode)
                {
                    var rv = await CallAPI<List<string>>("mainhost", $"/api/_frameworkuser/GetUserByGroup?keywords={item}");
                    if (rv != null && rv.Data != null)
                    {
                        userids.AddRange(rv.Data);
                    }
                }
            }
            else
            {
                userids = [.. DC.Set<FrameworkUserGroup>().Where(x => groupcode.Contains(x.GroupCode)).Select(x => x.UserCode)];
            }
            foreach (var userId in userids)
            {
                var key = $"{GlobalConstants.CacheKey.UserInfo}:{userId + "$`$" + LoginUserInfo?.CurrentTenant}";
                await Cache?.DeleteAsync(key);
            }
        }

        public async Task RemoveGroupCache(string tenant)
        {
            var svc = ServiceProvider?.GetService(typeof(IWtmTenantService)) as IWtmTenantService;
            if (svc != null) { await svc.RemoveGroupCacheAsync(tenant); return; }
            var key = $"{GlobalConstants.CacheKey.TenantGroups}:{tenant}";
            await Cache?.DeleteAsync(key);
        }

        public async Task RemoveRoleCache(string tenant)
        {
            var svc = ServiceProvider?.GetService(typeof(IWtmTenantService)) as IWtmTenantService;
            if (svc != null) { await svc.RemoveRoleCacheAsync(tenant); return; }
            var key = $"{GlobalConstants.CacheKey.TenantRoles}:{tenant}";
            await Cache?.DeleteAsync(key);
        }

        public List<SimpleGroup>? GetTenantGroups(string? tenant)
        {
            var svc = ServiceProvider?.GetService(typeof(IWtmTenantService)) as IWtmTenantService;
            if (svc != null) return svc.GetTenantGroups(tenant);

            // Fallback: inline logic for environments without DI
            var key = $"{GlobalConstants.CacheKey.TenantGroups}:{tenant}";
            var rv = ReadFromCache<List<SimpleGroup>>(key, () =>
            {
                List<SimpleGroup>? groups = null;
                try
                {
                    var dbtenant = GlobaInfo?.AllTenant?.Where(x => x.TCode == tenant && x.IsUsingDB == true).FirstOrDefault();
                    using (var dc = dbtenant == null ? ConfigInfo?.Connections?.Where(x => x.Key.ToLower() == "default").FirstOrDefault()?.CreateDC() : dbtenant.CreateDC(this))
                    {
                        // IgnoreQueryFilters: global tenant-group cache populated cross-filter
                        groups = dc?.Set<FrameworkGroup>().IgnoreQueryFilters().Where(x => x.TenantCode == tenant).Select(x => new SimpleGroup
                        {
                            ID = x.ID,
                            GroupCode = x.GroupCode,
                            GroupName = x.GroupName,
                            Manager = x.Manager,
                            ParentId = x.ParentId,
                            Tenant = x.TenantCode
                        }).ToList();
                    }
                }
                catch (Exception ex)
                {
                    ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("WTMContext")?.LogWarning(ex, "Failed to load tenant groups for tenant {Tenant}; returning empty list (cached for 6 minutes)", LogSanitizer.Sanitize(tenant));
                    groups = [];
                }
                return groups;
            }, 360000);
            return rv;
        }

        public List<SimpleRole>? GetTenantRoles(string? tenant)
        {
            var svc = ServiceProvider?.GetService(typeof(IWtmTenantService)) as IWtmTenantService;
            if (svc != null) return svc.GetTenantRoles(tenant);

            // Fallback: inline logic for environments without DI
            var key = $"{GlobalConstants.CacheKey.TenantRoles}:{tenant}";
            var rv = ReadFromCache<List<SimpleRole>>(key, () =>
            {
                List<SimpleRole>? roles = null;
                try
                {
                    var dbtenant = GlobaInfo?.AllTenant?.Where(x => x.TCode == tenant && x.IsUsingDB == true).FirstOrDefault();
                    using (var dc = dbtenant == null ? ConfigInfo?.Connections?.Where(x => x.Key.ToLower() == "default").FirstOrDefault()?.CreateDC() : dbtenant.CreateDC(this))
                    {
                        // IgnoreQueryFilters: global tenant-role cache populated cross-filter
                        roles = dc?.Set<FrameworkRole>().IgnoreQueryFilters().Where(x => x.TenantCode == tenant).Select(x => new SimpleRole
                        {
                            ID = x.ID,
                            RoleCode = x.RoleCode,
                            RoleName = x.RoleName,
                            Tenant = x.TenantCode
                        }).ToList();
                    }
                }
                catch (Exception ex)
                {
                    ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("WTMContext")?.LogWarning(ex, "Failed to load tenant roles for tenant {Tenant}; returning empty list (cached for 6 minutes)", LogSanitizer.Sanitize(tenant));
                    roles = [];
                }
                return roles;
            }, 360000);
            return rv;
        }


        public bool SetCurrentTenant(string? tenant)
        {
            if (LoginUserInfo != null)
            {
                if (LoginUserInfo?.TenantCode == null || LoginUserInfo?.TenantCode == tenant || GlobaInfo?.AllTenant?.Any(x => x.TCode == tenant && x.TenantCode == LoginUserInfo?.TenantCode) == true)
                {
                    if(LoginUserInfo != null) LoginUserInfo.CurrentTenant = tenant;
                    LoginUserInfo = LoginUserInfo;
                    return true;
                }
            }
            return false;
        }

        private bool _isCreatingDC;

        #region CreateDC
        public virtual IDataContext? CreateDC(bool isLog = false, string? cskey = null, bool logerror = true)
        {
            bool isReentrant = _isCreatingDC;
            if (!isReentrant)
            {
                _isCreatingDC = true;
            }

            try
            {
                string? cs = cskey ?? CurrentCS;
                string? tenantCode = null;

                var tenants = GlobaInfo?.AllTenant ?? [];
                string? tc = _loginUserInfo?.CurrentTenant;
                // Security fix (#116): Referer-based tenant resolution is only safe for
                // unauthenticated requests. An authenticated user's tenant must come solely
                // from their identity/claims; consulting the attacker-controlled Referer
                // header for authenticated requests allows cross-tenant data access.
                // When DisableRefererTenantResolution is true, skip Referer routing entirely.
                //
                // Robustness note: _loginUserInfo is lazily populated by the LoginUserInfo
                // getter only after that getter is first accessed. To prevent a timing
                // window where CreateDC() runs before the getter is called on an authenticated
                // principal, we ALSO check the request principal directly. Any authenticated
                // principal (HttpContext.User.Identity.IsAuthenticated == true) is excluded
                // from Referer-based routing regardless of whether _loginUserInfo has been
                // resolved yet.
                if (_loginUserInfo == null
                    && HttpContext?.User?.Identity?.IsAuthenticated != true
                    && ConfigInfo?.DisableRefererTenantResolution != true
                    && HttpContext?.Request.Headers.ContainsKey("Referer") == true)
                {
                    Regex r = new Regex("(http://|https://)?(.+?)(/)?$");
                    var m = r.Match(HttpContext?.Request.Headers["Referer"]);
                    string dom = "";
                    if (m.Success)
                    {
                        dom = m.Groups[2].Value;
                    }
                    tc = tenants.Where(x => x.TDomain != null && x.TDomain.ToLower() == dom.ToLower()).Select(x => x.TCode).FirstOrDefault();
                }
                if (tc != null)
                {
                    var item = tenants.Where(x => x.TCode == tc).FirstOrDefault();
                    tenantCode = tc;
                    //如果租户指定了数据库，则返回
                    if (string.IsNullOrEmpty(cs) && item?.IsUsingDB == true)
                    {
                        var tenantDc = item.CreateDC(this);
                        if(tenantDc != null) tenantDc.CurrentUserCode = isReentrant ? _loginUserInfo?.ITCode : LoginUserInfo?.ITCode;
                        return tenantDc;
                    }
                }

                if (isLog == true)
                {
                    if (ConfigInfo?.Connections?.Where(x => x.Key.ToLower() == "defaultlog").FirstOrDefault() != null)
                    {
                        cs = "defaultlog";
                    }
                }
                if (string.IsNullOrEmpty(cs))
                {
                    cs = "default";
                }
                var csConfig = ConfigInfo?.Connections.Where(x => x.Key.ToLower() == cs.ToLower()).FirstOrDefault();
                if (csConfig != null && !csConfig.Enabled)
                {
                    throw new InvalidOperationException($"Database connection '{csConfig.Key}' ({csConfig.DbType}) is disabled. Enable it in appsettings.json (set Enabled: true).");
                }
                var rv = csConfig?.CreateDC();
                if(rv!=null) rv.IsDebug = ConfigInfo?.IsQuickDebug == true;
                rv?.SetTenantCode(tenantCode);
                if(rv != null) rv.CurrentUserCode = isReentrant ? _loginUserInfo?.ITCode : LoginUserInfo?.ITCode;
                if (logerror == true)
                {
                    rv?.SetLoggerFactory(_loggerFactory);
                }
                TrySetDataContextTimeProvider(rv);
                return rv;
            }
            finally
            {
                if (!isReentrant)
                {
                    _isCreatingDC = false;
                }
            }
        }

        #endregion

        /// <summary>
        /// 判断某URL是否有权限访问
        /// </summary>
        /// <param name="url">url地址</param>
        /// <returns>true代表可以访问，false代表不能访问</returns>
        public bool IsAccessable(string? url)
        {
            var svc = ServiceProvider?.GetService(typeof(IWtmAuthorizationService)) as IWtmAuthorizationService;
            if (svc != null) return svc.IsAccessable(url, LoginUserInfo, _configInfo, _globaInfo);

            // Fallback: inline logic for environments without DI
            if (_configInfo?.IsQuickDebug == true || string.IsNullOrEmpty(url) || IsUrlPublic(url))
            {
                return true;
            }
            //租户用户不能访问标记[HostOnly]的方法
            if (_configInfo.EnableTenant == true)
            {
                if (LoginUserInfo?.TenantCode != null)
                {
                    var hostonly = _globaInfo.AllMainTenantOnlyUrls;
                    foreach (var au in hostonly)
                    {
                        if (new Regex("^" + au + "[/\\?]?", RegexOptions.IgnoreCase).IsMatch(url))
                        {
                            return false;
                        }
                    }
                }
            }
            //循环所有不限制访问的url，如果含有当前判断的url，则认为可以访问
            var publicActions = _globaInfo.AllAccessUrls;
            foreach (var au in publicActions)
            {                
                if (au != "/" && new Regex("^" + au + "[/\\?]?", RegexOptions.IgnoreCase).IsMatch(url))
                {
                    return true;
                }
            }
            //如果没有任何页面权限，则直接返回false
            if (LoginUserInfo?.FunctionPrivileges == null)
            {
                return false;
            }


            url = Regex.Replace(url ?? "", "/do(batch.*)", "/$1", RegexOptions.IgnoreCase);

            //如果url以#开头，一般是javascript使用的临时地址，不需要判断，直接返回true
            url = url.Trim();

            if (url.StartsWith("#"))
            {
                return true;
            }
            var menus = _globaInfo.AllMenus;
            var menu = Utils.FindMenu(url, GlobaInfo?.AllMenus);
            //如果最终没有找到，说明系统菜单中并没有配置这个url，返回false
            if (menu == null)
            {
                return false;
            }
            //如果找到了，则继续验证其他权限
            else
            {
                return IsAccessable(menu, menus);
            }
        }

        /// <summary>
        /// 判断某菜单是否有权限访问
        /// </summary>
        /// <param name="menu">菜单项</param>
        /// <param name="menus">所有系统菜单</param>
        /// <returns>true代表可以访问，false代表不能访问</returns>
        protected bool IsAccessable(SimpleMenu? menu, List<SimpleMenu>? menus)
        {
            if (LoginUserInfo?.CurrentTenant != null && menu?.TenantAllowed == false)
            {
                return false;
            }
            //寻找当前菜单的页面权限
            var find = LoginUserInfo?.FunctionPrivileges.Where(x => x.MenuItemId == menu?.ID && x.Allowed == true).FirstOrDefault();
            //如果能找到直接对应的页面权限
            if (find != null)
            {
                return true;
            }
            return false;
        }

        public bool IsUrlPublic(string? url)
        {
            var svc = ServiceProvider?.GetService(typeof(IWtmAuthorizationService)) as IWtmAuthorizationService;
            if (svc != null) return svc.IsUrlPublic(url, _globaInfo);

            var isPublic = false;
            try
            {
                url = Regex.Replace(url ?? "", "/do(batch.*)", "/$1", RegexOptions.IgnoreCase);
                url = url.Trim();

                if (url.StartsWith("#"))
                {
                    isPublic = true;
                }
                var menus = GlobaInfo?.AllMenus;
                var menu = Utils.FindMenu(url, menus);
                if (menu != null && menu.IsPublic == true)
                {
                    isPublic = true;
                }
            }
            catch (Exception ex)
            {
                ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("WTMContext")?.LogWarning(ex, "Failed to determine if URL '{Url}' is public", LogSanitizer.Sanitize(url));
            }
            return isPublic;
        }

        public void DoLog(string? msg, ActionLogTypesEnum logtype = ActionLogTypesEnum.Normal, string? moduleName = "", string? actionName = "", string? ip = "", string? url = "", double duration = 0)
        {
            var svc = ServiceProvider?.GetService(typeof(IWtmLogService)) as IWtmLogService;
            if (svc != null)
            {
                var effectiveUrl = string.IsNullOrEmpty(url) ? this.HttpContext?.Request?.Path.ToString() : url;
                svc.DoLog(msg, logtype, moduleName, actionName, ip, effectiveUrl, duration,
                    LoginUserInfo?.ITCode, this.Log);
                return;
            }

            // Fallback: inline logic
            var log = this.Log?.GetActionLog();
            if (log == null)
            {
                log = new ActionLog();
            }
            log.LogType = logtype;
            log.ActionTime = _timeProvider.GetLocalNow().DateTime;
            log.Remark = msg;
            log.ActionUrl = url;
            log.Duration = duration;
            log.ModuleName = moduleName;
            log.ActionName = actionName;
            log.ITCode = LoginUserInfo?.ITCode;
            log.IP = ip;
            if (string.IsNullOrEmpty(url) && this.HttpContext?.Request != null)
            {
                log.ActionUrl = this.HttpContext?.Request.Path.ToString();
            }
            LogLevel ll = LogLevel.Information;
            switch (logtype)
            {
                case ActionLogTypesEnum.Normal:
                    ll = LogLevel.Information;
                    break;
                case ActionLogTypesEnum.Exception:
                    ll = LogLevel.Error;
                    break;
                case ActionLogTypesEnum.Debug:
                    ll = LogLevel.Debug;
                    break;
                default:
                    break;
            }

            Logger?.Log<ActionLog>(ll, new EventId(), log, null, (a, b) =>
            {
                return $@"
===WTM Log===
内容:{a.Remark}
地址:{a.ActionUrl}
时间:{a.ActionTime}
===WTM Log===
";
            });
        }



        #region CreateVM
        /// <summary>
        /// Create a ViewModel, and pass Session,cache,dc...etc to the viewmodel
        /// </summary>
        /// <param name="VMType">The type of the viewmodel</param>
        /// <param name="Id">If the viewmodel is a BaseCRUDVM, the data having this id will be fetched</param>
        /// <param name="Ids">If the viewmodel is a BatchVM, the BatchVM's Ids property will be assigned</param>
        /// <param name="values">properties of the viewmodel that you want to assign values</param>
        /// <param name="passInit">if true, the viewmodel will not call InitVM internally</param>
        /// <returns>ViewModel</returns>
        private BaseVM CreateVM(Type? VMType, object? Id = null, object[]? Ids = null, Dictionary<string, object>? values = null, bool passInit = false)
        {
            //Use reflection to create viewmodel
            var ctor = VMType?.GetConstructor(Type.EmptyTypes);
            BaseVM? rv = ctor?.Invoke(null) as BaseVM;
            if (rv == null)
            {
                throw new InvalidOperationException($"Cannot create ViewModel of type '{VMType?.FullName}'. Type must derive from BaseVM and have a parameterless constructor.");
            }
            rv.Wtm = this;

            rv.FC = new Dictionary<string, object>();
            rv.CreatorAssembly = this.GetType().AssemblyQualifiedName;
            rv.ControllerName = this.HttpContext?.Request?.Path;
            if (HttpContext != null && HttpContext?.Request != null)
            {
                try
                {
                    if (HttpContext?.Request.QueryString != QueryString.Empty)
                    {
                        foreach (var key in HttpContext?.Request.Query.Keys)
                        {
                            if (rv.FC.Keys.Contains(key) == false)
                            {
                                rv.FC.Add(key, HttpContext?.Request.Query[key]);
                            }
                        }
                    }
                    if (HttpContext?.Request?.HasFormContentType == true)
                    {
                        var f = HttpContext?.Request.Form;
                        foreach (var key in f.Keys)
                        {
                            if (rv.FC.Keys.Contains(key) == false)
                            {
                                rv.FC.Add(key, f[key]);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("WTMContext")?.LogWarning(ex, "Failed to populate FC dictionary from request form/query for ViewModel '{VmType}'", rv.GetType().Name);
                }
            }
            //try to set values to the viewmodel's matching properties
            if (values != null)
            {
                foreach (var v in values)
                {
                    PropertyHelper.SetPropertyValue(rv, v.Key, v.Value, null, false);
                }
            }
            //if viewmodel is derrived from BaseCRUDVM<> and Id has value, call ViewModel's GetById method
            if (Id != null && rv is IBaseCRUDVM<TopBasePoco> cvm)
            {
                cvm.SetEntityById(Id);
            }
            SetSubVm(rv, passInit);
            //if viewmodel is derrived from IBaseBatchVM<>，set ViewMode's Ids property,and init it's ListVM and EditModel properties
            if (rv is IBaseBatchVM<BaseVM> temp)
            {
                temp.Ids = new string[] { };
                if (Ids != null)
                {
                    List<string> tempids = [];
                    foreach (var iid in Ids)
                    {
                        tempids.Add(iid?.ToString() ?? "");
                    }
                    temp.Ids = [.. tempids];
                }
                if (temp.ListVM != null)
                {
                    temp.ListVM.CopyContext(rv);
                    temp.ListVM.Ids = Ids == null ? [] : [.. temp.Ids!];
                    temp.ListVM.SearcherMode = ListVMSearchModeEnum.Batch;
                    temp.ListVM.NeedPage = false;
                }
                if (temp.LinkedVM != null)
                {
                    temp.LinkedVM.CopyContext(rv);
                }
                if (temp.ListVM != null)
                {
                    //Remove the action columns from list
                    temp.ListVM.OnAfterInitList += (self) =>
                    {
                        self.RemoveActionColumn();
                        self.RemoveAction();
                        if (temp.ErrorMessage.Count > 0)
                        {
                            self.AddErrorColumn();
                        }
                    };
                    temp.ListVM.DoInitListVM();
                    if (temp.ListVM.Searcher != null)
                    {
                        var searcher = temp.ListVM.Searcher;
                        searcher.CopyContext(rv);
                        if (passInit == false)
                        {
                            searcher.DoInit();
                        }
                    }
                }
                temp.LinkedVM?.DoInit();
                //temp.ListVM.DoSearch();
            }
            //if the viewmodel is a ListVM, Init it's searcher
            if (rv is IBasePagedListVM<TopBasePoco, ISearcher> lvm)
            {
                var searcher = lvm.Searcher;
                searcher.CopyContext(rv);
                if (passInit == false)
                {
                    searcher.DoInit();
                }
                lvm.DoInitListVM();

            }
            if (rv is IBaseImport<BaseTemplateVM> tvm)
            {
                var template = tvm.Template;
                template.CopyContext(rv);
                template.DoInit();
                var errorlist = tvm.ErrorListVM;
                errorlist.CopyContext(rv);
            }

            //if passinit is not set, call the viewmodel's DoInit method
            if (passInit == false)
            {
                rv.DoInit();
            }
            return rv;
        }

        private void SetSubVm(BaseVM? vm, bool passInit)
        {
            var sub = vm?.GetType()?.GetAllProperties().Where(x => typeof(BaseVM).IsAssignableFrom(x.PropertyType) && x.Name != "ParentVM");
            foreach (var prop in sub)
            {
                var subins = prop.GetValue(vm) as BaseVM;
                bool exist = subins == null ? false : true;
                if (subins == null)
                {
                    subins = prop?.PropertyType?.GetConstructor(Type.EmptyTypes).Invoke(null) as BaseVM;
                }
                if (subins != null)
                {
                    subins.CopyContext(vm);
                    subins.ParentVM = vm;
                    subins.PropertyNameInParent = prop.Name;
                   if (passInit == false)
                    {
                        subins.DoInit();
                    }
                    if (exist == false)
                    {
                        vm.SetPropertyValue(prop.Name, subins);
                    }
                    SetSubVm(subins,passInit);
                }
            }

        }


        /// <summary>
        /// Create a ViewModel, and pass Session,cache,dc...etc to the viewmodel
        /// </summary>
        /// <typeparam name="T">The type of the viewmodelThe type of the viewmodel</typeparam>
        /// <param name="values">use Lambda to set viewmodel's properties,use && for multiply properties, for example Wtm.CreateVM<Test>(values: x=>x.Field1=='a' && x.Field2 == 'b'); will set viewmodel's Field1 to 'a' and Field2 to 'b'</param>
        /// <param name="passInit">if true, the viewmodel will not call InitVM internally</param>
        /// <returns>ViewModel</returns>
        public T CreateVM<T>(Expression<Func<T, object>>? values = null, bool passInit = false) where T : BaseVM
        {
            SetValuesParser p = new SetValuesParser();
            var dir = p.Parse(values);
            return CreateVM(typeof(T), null, new object[] { }, dir, passInit) as T;
        }

        /// <summary>
        /// Create a ViewModel, and pass Session,cache,dc...etc to the viewmodel
        /// </summary>
        /// <typeparam name="T">The type of the viewmodelThe type of the viewmodel</typeparam>
        /// <param name="Id">If the viewmodel is a BaseCRUDVM, the data having this id will be fetched</param>
        /// <param name="values">properties of the viewmodel that you want to assign values</param>
        /// <param name="passInit">if true, the viewmodel will not call InitVM internally</param>
        /// <returns>ViewModel</returns>
        public T CreateVM<T>(object Id, Expression<Func<T, object>>? values = null, bool passInit = false) where T : BaseVM
        {
            SetValuesParser p = new SetValuesParser();
            var dir = p.Parse(values);
            return CreateVM(typeof(T), Id, new object[] { }, dir, passInit) as T;
        }

        /// <summary>
        /// Create a ViewModel, and pass Session,cache,dc...etc to the viewmodel
        /// </summary>
        /// <typeparam name="T">The type of the viewmodelThe type of the viewmodel</typeparam>
        /// <param name="Ids">If the viewmodel is a BatchVM, the BatchVM's Ids property will be assigned</param>
        /// <param name="values">use Lambda to set viewmodel's properties,use && for multiply properties, for example Wtm.CreateVM<Test>(values: x=>x.Field1=='a' && x.Field2 == 'b'); will set viewmodel's Field1 to 'a' and Field2 to 'b'</param>
        /// <param name="passInit">if true, the viewmodel will not call InitVM internally</param>
        /// <returns>ViewModel</returns>
        public T CreateVM<T>(object[] Ids, Expression<Func<T, object>>? values = null, bool passInit = false) where T : BaseVM
        {
            SetValuesParser p = new SetValuesParser();
            var dir = p.Parse(values);
            return CreateVM(typeof(T), null, Ids, dir, passInit) as T;
        }


        /// <summary>
        /// Create a ViewModel, and pass Session,cache,dc...etc to the viewmodel
        /// </summary>
        /// <typeparam name="T">The type of the viewmodelThe type of the viewmodel</typeparam>
        /// <param name="Ids">If the viewmodel is a BatchVM, the BatchVM's Ids property will be assigned</param>
        /// <param name="values">use Lambda to set viewmodel's properties,use && for multiply properties, for example Wtm.CreateVM<Test>(values: x=>x.Field1=='a' && x.Field2 == 'b'); will set viewmodel's Field1 to 'a' and Field2 to 'b'</param>
        /// <param name="passInit">if true, the viewmodel will not call InitVM internally</param>
        /// <returns>ViewModel</returns>
        public T CreateVM<T>(Guid[] Ids, Expression<Func<T, object>>? values = null, bool passInit = false) where T : BaseVM
        {
            SetValuesParser p = new SetValuesParser();
            var dir = p.Parse(values);
            return CreateVM(typeof(T), null, [.. Ids.Cast<object>()], dir, passInit) as T;
        }

        /// <summary>
        /// Create a ViewModel, and pass Session,cache,dc...etc to the viewmodel
        /// </summary>
        /// <typeparam name="T">The type of the viewmodelThe type of the viewmodel</typeparam>
        /// <param name="Ids">If the viewmodel is a BatchVM, the BatchVM's Ids property will be assigned</param>
        /// <param name="values">use Lambda to set viewmodel's properties,use && for multiply properties, for example Wtm.CreateVM<Test>(values: x=>x.Field1=='a' && x.Field2 == 'b'); will set viewmodel's Field1 to 'a' and Field2 to 'b'</param>
        /// <param name="passInit">if true, the viewmodel will not call InitVM internally</param>
        /// <returns>ViewModel</returns>
        public T CreateVM<T>(int[] Ids, Expression<Func<T, object>>? values = null, bool passInit = false) where T : BaseVM
        {
            SetValuesParser p = new SetValuesParser();
            var dir = p.Parse(values);
            return CreateVM(typeof(T), null, [.. Ids.Cast<object>()], dir, passInit) as T;
        }

        /// <summary>
        /// Create a ViewModel, and pass Session,cache,dc...etc to the viewmodel
        /// </summary>
        /// <typeparam name="T">The type of the viewmodelThe type of the viewmodel</typeparam>
        /// <param name="Ids">If the viewmodel is a BatchVM, the BatchVM's Ids property will be assigned</param>
        /// <param name="values">use Lambda to set viewmodel's properties,use && for multiply properties, for example Wtm.CreateVM<Test>(values: x=>x.Field1=='a' && x.Field2 == 'b'); will set viewmodel's Field1 to 'a' and Field2 to 'b'</param>
        /// <param name="passInit">if true, the viewmodel will not call InitVM internally</param>
        /// <returns>ViewModel</returns>
        public T CreateVM<T>(long[] Ids, Expression<Func<T, object>>? values = null, bool passInit = false) where T : BaseVM
        {
            SetValuesParser p = new SetValuesParser();
            var dir = p.Parse(values);
            return CreateVM(typeof(T), null, [.. Ids.Cast<object>()], dir, passInit) as T;
        }
        /// <summary>
        /// Create a ViewModel, and pass Session,cache,dc...etc to the viewmodel
        /// </summary>
        /// <typeparam name="T">The type of the viewmodelThe type of the viewmodel</typeparam>
        /// <param name="Ids">If the viewmodel is a BatchVM, the BatchVM's Ids property will be assigned</param>
        /// <param name="values">use Lambda to set viewmodel's properties,use && for multiply properties, for example Wtm.CreateVM<Test>(values: x=>x.Field1=='a' && x.Field2 == 'b'); will set viewmodel's Field1 to 'a' and Field2 to 'b'</param>
        /// <param name="passInit">if true, the viewmodel will not call InitVM internally</param>
        /// <returns>ViewModel</returns>
        public T CreateVM<T>(string[] Ids, Expression<Func<T, object>>? values = null, bool passInit = false) where T : BaseVM
        {
            SetValuesParser p = new SetValuesParser();
            var dir = p.Parse(values);
            return CreateVM(typeof(T), null, [.. Ids.Cast<object>()], dir, passInit) as T;
        }

        /// <summary>
        /// Create a ViewModel, and pass Session,cache,dc...etc to the viewmodel
        /// </summary>
        /// <param name="VmFullName">the fullname of the viewmodel's type</param>
        /// <param name="Id">If the viewmodel is a BaseCRUDVM, the data having this id will be fetched</param>
        /// <param name="Ids">If the viewmodel is a BatchVM, the BatchVM's Ids property will be assigned</param>
        /// <param name="passInit">if true, the viewmodel will not call InitVM internally</param>
        /// <returns>ViewModel</returns>
        public BaseVM CreateVM(string? VmFullName, object? Id = null, object[]? Ids = null, bool passInit = false)
        {
            // First try Type.GetType (handles assembly-qualified names and same-assembly types).
            // Then fall back to scanning GlobaInfo.AllAssembly (covers types in the host app
            // and other loaded assemblies that Type.GetType cannot resolve by short name).
            // Guard: reject unresolvable or non-BaseVM types (#767)
            var vmType = Type.GetType(VmFullName ?? "");
            if (vmType == null && GlobaInfo?.AllAssembly != null)
            {
                foreach (var asm in GlobaInfo.AllAssembly)
                {
                    vmType = asm.GetType(VmFullName ?? "");
                    if (vmType != null) break;
                }
            }

            if (vmType == null || !typeof(BaseVM).IsAssignableFrom(vmType))
            {
                throw new ArgumentException($"Invalid or unregistered ViewModel type: {VmFullName}");
            }

            return CreateVM(vmType, Id, Ids, null, passInit);
        }
        #endregion

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
                ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("WTMContext")?.LogError(ex, "CallAPI failed to '{Url}'", LogSanitizer.Sanitize(url));
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


        private string GetServerUrl()
        {
            var server = ConfigInfo?.Domains.Where(x => x.Key.ToLower() == "serverpub").Select(x => x.Value).FirstOrDefault();
            if (server == null)
            {
                server = ConfigInfo?.Domains.Where(x => x.Key.ToLower() == "server").Select(x => x.Value).FirstOrDefault();
            }
            if (server != null && string.IsNullOrEmpty(server.Address) == false)
            {
                return server.Address.TrimEnd('/');
            }
            else
            {
                return this.HttpContext?.Request.Scheme + "://" + this.HttpContext?.Request.Host.ToString();
            }
        }

        public void Dispose()
        {
            this._dc?.Dispose();
        }

        #endregion
    }

}
