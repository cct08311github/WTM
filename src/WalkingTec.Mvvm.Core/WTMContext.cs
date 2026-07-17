#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Services;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core
{
    public partial class WTMContext : IDisposable
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

        #region URL
        public string? BaseUrl { get; set; }
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

        private ILogger? _wtmDiagnosticLogger;

        /// <summary>
        /// Lazily-resolved diagnostic <see cref="ILogger"/> ("WTMContext" category), distinct
        /// from the <see cref="Logger"/> ActionLog logger above. Cached after first successful
        /// resolution to avoid repeating <c>ServiceProvider.GetService&lt;ILoggerFactory&gt;()</c>
        /// + <c>CreateLogger(...)</c> on every warning/error path across the partial class.
        /// Not cached while <see cref="ServiceProvider"/> is still unset, so a later
        /// <see cref="SetServiceProvider"/> call is picked up on the next access.
        /// </summary>
        private ILogger? WtmDiagnosticLogger => _wtmDiagnosticLogger ??= ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger("WTMContext");


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

        /// <summary>
        /// SECURITY (#721): this no-argument overload used to reissue a brand-new token
        /// pair purely from <see cref="LoginUserInfo"/> identity, completely ignoring
        /// whatever refresh token (if any) the caller actually presented — an auth
        /// bypass that let anyone holding a still-valid access token mint fresh tokens
        /// indefinitely, with the entire <see cref="ITokenService"/> rotation/replay-guard
        /// machinery dead on this path. It now always rejects. Use
        /// <see cref="RefreshTokenAsync(string?)"/>, which validates the presented
        /// refresh token (or forwards it to the mainhost for federation frontends)
        /// before issuing anything. Kept only for binary/source compatibility.
        /// </summary>
        [Obsolete("Insecure: performed identity-based reissue that ignored the presented refresh token (#721 auth bypass). Always rejects now. Use RefreshTokenAsync(string refreshToken).")]
        public Task<Token?> RefreshTokenAsync()
        {
            return Task.FromResult<Token?>(null);
        }

        /// <summary>
        /// Refreshes a token pair by validating the caller-presented <paramref name="refreshToken"/>.
        /// This is the sanctioned refresh path (#721 security fix) — a bogus, never-issued,
        /// expired, or already-rotated refresh token is rejected (returns <c>null</c>); it
        /// never reissues purely from <see cref="LoginUserInfo"/> identity.
        /// </summary>
        /// <param name="refreshToken">The refresh token the caller is presenting.</param>
        /// <returns>
        /// A newly issued <see cref="Token"/> pair (with the presented refresh token rotated)
        /// when <paramref name="refreshToken"/> validates successfully; otherwise <c>null</c>.
        /// </returns>
        public async Task<Token?> RefreshTokenAsync(string? refreshToken)
        {
            if (string.IsNullOrEmpty(refreshToken))
            {
                return null;
            }

            if (ConfigInfo?.HasMainHost == true && LoginUserInfo?.CurrentTenant == null)
            {
                // Federation frontend: this host has no RefreshTokenEntity DB of its own,
                // so it cannot validate the token locally. Forward the ACTUAL presented
                // token to the mainhost's hardened endpoint — never an empty body — and
                // let the mainhost validate/rotate it (#721).
                var r = await CallAPI<Token>("mainhost", "/api/_account/refreshtoken", HttpMethodEnum.POST,
                    new { RefreshToken = refreshToken });
                return r?.Data;
            }

            var _authService = ServiceProvider?.GetRequiredService<ITokenService>();
            if (_authService == null)
            {
                return null;
            }
            var ip = HttpContext?.Connection?.RemoteIpAddress?.ToString();
            return await _authService.RefreshTokenAsync(refreshToken, ip);
        }

        [Obsolete("Insecure and blocks threads: performed identity-based reissue that ignored the presented refresh token (#721 auth bypass). Always rejects now. Use RefreshTokenAsync(string refreshToken).")]
        public Token? RefreshToken()
        {
#pragma warning disable CS0618 // intentionally calling the deprecated no-arg async overload
            return RefreshTokenAsync().GetAwaiter().GetResult();
#pragma warning restore CS0618
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

        /// <summary>
        /// Async variant of <see cref="ReadFromCache{T}"/> for factory delegates that need to
        /// perform I/O (e.g. an outbound HTTP call) on cache miss. Avoids blocking a ThreadPool
        /// thread via GetAwaiter().GetResult() in callers such as GetGithubStarts/GetGithubInfo (#538).
        /// </summary>
        public async Task<T> ReadFromCacheAsync<T>(string key, Func<Task<T>> setFunc, int? timeout = null)
        {
            if (Cache.TryGetValue(key, out T rv) == false || rv == null)
            {
                T data = await setFunc().ConfigureAwait(false);
                if (timeout == null)
                {
                    if (Cache != null)
                    {
                        await Cache.AddAsync(key, data).ConfigureAwait(false);
                    }
                }
                else
                {
                    if (Cache != null)
                    {
                        await Cache.AddAsync(key, data, new DistributedCacheEntryOptions()
                        {
                            AbsoluteExpirationRelativeToNow = new TimeSpan(0, 0, timeout.Value)
                        }).ConfigureAwait(false);
                    }
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
                    WtmDiagnosticLogger?.LogWarning(ex, "Failed to load tenant groups for tenant {Tenant}; returning empty list (cached for 6 minutes)", LogSanitizer.Sanitize(tenant));
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
                    WtmDiagnosticLogger?.LogWarning(ex, "Failed to load tenant roles for tenant {Tenant}; returning empty list (cached for 6 minutes)", LogSanitizer.Sanitize(tenant));
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

        /// <summary>
        /// Returns true if <paramref name="csKey"/> is null/empty (→ default connection) or matches a
        /// configured connection-string key (case-insensitive). Use to validate a client-supplied
        /// connection-string key BEFORE CreateDC(cskey:) / setting CurrentCS, to prevent cross-DB access (#503/#506/#517).
        /// </summary>
        public bool IsKnownConnectionKey(string? csKey)
        {
            if (string.IsNullOrEmpty(csKey)) return true;
            return ConfigInfo?.Connections.Any(c =>
                string.Equals(c.Key, csKey, StringComparison.OrdinalIgnoreCase)) == true;
        }

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
                        if (Helper.CoreRegexes.GetUrlPrefixRegex(au).IsMatch(url))
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
                if (au != "/" && Helper.CoreRegexes.GetUrlPrefixRegex(au).IsMatch(url))
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
                WtmDiagnosticLogger?.LogWarning(ex, "Failed to determine if URL '{Url}' is public", LogSanitizer.Sanitize(url));
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



        public void Dispose()
        {
            this._dc?.Dispose();
        }
    }

}
