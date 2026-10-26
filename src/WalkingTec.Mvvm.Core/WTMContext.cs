using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Logging.Debug;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Json;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core
{
    public class WTMContext : IDisposable
    {
        private HttpContext _httpContext;
        public HttpContext HttpContext { get => _httpContext; }

        private IServiceProvider _serviceProvider;
        public IServiceProvider ServiceProvider { get => _serviceProvider ?? _httpContext?.RequestServices; }


        private List<IDataPrivilege> _dps;
        public List<IDataPrivilege> DataPrivilegeSettings { get => _dps; }

        private Configs _configInfo;
        public Configs ConfigInfo { get => _configInfo; }

        private GlobalData _globaInfo;
        public GlobalData GlobaInfo { get => _globaInfo; }

        private IUIService _uiservice;
        public IUIService UIService { get => _uiservice; }

        private IDistributedCache _cache;
        public IDistributedCache Cache
        {
            get
            {
                return _cache;
            }
        }

        public string CurrentCS
        {
            get;
            set;
        }

        public DBTypeEnum? CurrentDbType { get; set; }

        public string ParentWindowId
        {
            get
            {
                string rv = null;
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
                string rv = null;
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
                    if (HttpContext.Request.Cookies.TryGetValue($"{ConfigInfo?.CookiePre}windowguid", out string windowguid) == true)
                    {

                        if (HttpContext.Request.Cookies.TryGetValue($"{ConfigInfo?.CookiePre}{windowguid}windowids", out string windowid) == true)
                        {
                            rv = windowid;
                        }
                    }
                }
                catch { }
                return rv;
            }
        }

        public ISessionService Session { get; set; }

        public IModelStateService MSD { get; set; }

        public static Func<WTMContext, string, LoginUserInfo> ReloadUserFunc { get; set; }

        #region DataContext

        private IDataContext _dc;
        public IDataContext DC
        {
            get
            {
                if (_dc == null)
                {
                    _dc = this.CreateDC();
                }
                return _dc;
            }
            set
            {
                _dc = value;
            }
        }

        #endregion

        #region Current User

        private LoginUserInfo _loginUserInfo;
        // set once a remote token has been rejected, so it is not re-validated (and re-logged) on every access
        private bool _remoteTokenRejected;
        public LoginUserInfo LoginUserInfo
        {
            get
            {
                if (_loginUserInfo == null && HttpContext?.User?.Identity?.IsAuthenticated == true) // 用户认证通过后，当前上下文不包含用户数据
                {
                    var userIdStr = HttpContext.User.Claims.Where(x => x.Type == AuthConstants.JwtClaimTypes.Subject).Select(x => x.Value).FirstOrDefault();
                    var tenant = HttpContext.User.Claims.Where(x => x.Type == AuthConstants.JwtClaimTypes.TenantCode).Select(x => x.Value).FirstOrDefault();
                    string usercode = userIdStr;
                    var cacheKey = $"{GlobalConstants.CacheKey.UserInfo}:{userIdStr + "$`$" + tenant}";
                    _loginUserInfo = Cache.Get<LoginUserInfo>(cacheKey);
                    if (_loginUserInfo == null)
                    {
                        try
                        {
                            _loginUserInfo = ReloadUser(usercode);
                        }
                        catch { }
                        if (_loginUserInfo != null)
                        {
                            Cache.Add(cacheKey, _loginUserInfo);
                        }
                        else
                        {
                            return null;
                        }
                    }
                }
                if (_loginUserInfo == null && HttpContext?.Request.Query.Any(x => x.Key == "_remotetoken") == true)
                {
                    var remoteToken = HttpContext?.Request.Query["_remotetoken"][0];
                    if (ConfigInfo.HasMainHost == false)
                    {
                        if (_remoteTokenRejected)
                        {
                            return null;
                        }
                        _loginUserInfo = TryResolveRemoteToken(remoteToken);
                        if (_loginUserInfo == null)
                        {
                            _remoteTokenRejected = true;
                            return null;
                        }
                    }
                    else if (string.IsNullOrEmpty(remoteToken) == false)
                    {
                        try
                        {
                            _loginUserInfo = ReloadUser("null");
                        }
                        catch { }
                        if (_loginUserInfo != null)
                        {
                            var cacheKey = $"{GlobalConstants.CacheKey.UserInfo}:{_loginUserInfo.ITCode + "$`$" + _loginUserInfo.TenantCode}";
                            Cache.Add(cacheKey, _loginUserInfo);
                        }
                        else
                        {
                            return null;
                        }
                    }
                }
                return _loginUserInfo;
            }
            set
            {
                if (value == null)
                {
                    Cache.Delete($"{GlobalConstants.CacheKey.UserInfo}:{_loginUserInfo?.ITCode + "$`$" + _loginUserInfo?.TenantCode}");
                    _loginUserInfo = value;
                }
                else
                {
                    _loginUserInfo = value;
                    Cache.Add($"{GlobalConstants.CacheKey.UserInfo}:{_loginUserInfo.ITCode + "$`$" + _loginUserInfo?.TenantCode}", value);
                }
            }
        }

        /// <summary>
        /// Loads <see cref="LoginUserInfo"/> of a signed-in request without blocking the thread, so the synchronous property finds it ready.
        /// It does what the property does when the request is authenticated and the user is not loaded yet: take the user from the cache, else
        /// reload it (<see cref="ReloadUserAsync"/>; an exception is swallowed and gives null, as in the property) and add it to the cache.
        /// Called early in the request by the framework filters; the property stays the fallback for everything else (no <see cref="HttpContext"/>,
        /// a request that did not pass the filters, a user that could not be loaded). A request that is not authenticated falls back to the property,
        /// which also resolves the _remotetoken query parameter.
        /// </summary>
        public async Task<LoginUserInfo> EnsureLoginUserInfoAsync()
        {
            if (_loginUserInfo == null && HttpContext?.User?.Identity?.IsAuthenticated == true)
            {
                var userIdStr = HttpContext.User.Claims.Where(x => x.Type == AuthConstants.JwtClaimTypes.Subject).Select(x => x.Value).FirstOrDefault();
                var tenant = HttpContext.User.Claims.Where(x => x.Type == AuthConstants.JwtClaimTypes.TenantCode).Select(x => x.Value).FirstOrDefault();
                string usercode = userIdStr;
                var cacheKey = $"{GlobalConstants.CacheKey.UserInfo}:{userIdStr + "$`$" + tenant}";
                _loginUserInfo = Cache.Get<LoginUserInfo>(cacheKey);
                if (_loginUserInfo == null)
                {
                    try
                    {
                        _loginUserInfo = await ReloadUserAsync(usercode);
                    }
                    catch { }
                    if (_loginUserInfo != null)
                    {
                        Cache.Add(cacheKey, _loginUserInfo);
                    }
                }
                return _loginUserInfo;
            }
            return LoginUserInfo;
        }

        /// <summary>
        /// Validate the remote token before trusting any of its claims.
        /// Returns null (anonymous) when the token is missing or fails validation.
        /// </summary>
        private LoginUserInfo TryResolveRemoteToken(string remoteToken)
        {
            if (string.IsNullOrEmpty(remoteToken))
            {
                return null;
            }
            ClaimsPrincipal principal;
            try
            {
                var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
                principal = handler.ValidateToken(remoteToken, ConfigInfo.JwtOptions.CreateValidationParameters(), out _);
            }
            catch (Exception ex)
            {
                // any validation failure is treated as anonymous
                _loggerFactory?.CreateLogger<WTMContext>().LogWarning("Remote token validation failed: {ExceptionType}", ex.GetType().Name);
                return null;
            }
            var userIdStr = principal.Claims.Where(x => x.Type == AuthConstants.JwtClaimTypes.Subject).Select(x => x.Value).FirstOrDefault();
            var tenant = principal.Claims.Where(x => x.Type == AuthConstants.JwtClaimTypes.TenantCode).Select(x => x.Value).FirstOrDefault();
            if (string.IsNullOrEmpty(userIdStr))
            {
                return null;
            }
            string usercode = userIdStr;
            var cacheKey = $"{GlobalConstants.CacheKey.UserInfo}:{userIdStr + "$`$" + tenant}";
            var rv = Cache.Get<LoginUserInfo>(cacheKey);
            if (rv == null)
            {
                try
                {
                    rv = ReloadUser(usercode);
                }
                catch { }
                if (rv != null)
                {
                    Cache.Add(cacheKey, rv);
                }
            }
            return rv;
        }

        private Type _localizerType;
        private IStringLocalizerFactory _stringLocalizerFactory;
        private IStringLocalizer _localizer;
        private ILoggerFactory _loggerFactory;
        public ILoggerFactory LoggerFactory { get { return _loggerFactory; } }
        public IStringLocalizer Localizer
        {
            get
            {
                if (_localizer == null && _stringLocalizerFactory != null)
                {
                    if (_localizerType == null)
                    {
                        _localizerType = Assembly.GetEntryAssembly().GetTypes().Where(x => x.Name == "Program").FirstOrDefault();
                    }
                    _localizer = _stringLocalizerFactory.Create(_localizerType);
                }
                return _localizer ?? WalkingTec.Mvvm.Core.CoreProgram._localizer;
            }
        }

        /// <summary>
        /// 从数据库读取用户
        /// </summary>
        /// <param name="itcode">用户名</param>
        /// <returns>用户信息</returns>
        public virtual LoginUserInfo
            ReloadUser(string itcode)
        {
            if (ReloadUserFunc != null)
            {
                var reload = ReloadUserFunc.Invoke(this, itcode);
                if (reload != null)
                {
                    return reload;
                }
            }
            if (DC == null)
            {
                return null;
            }
            // a request that is signed in (cookie or token, already verified by the authentication middleware) is reloaded from that sign-in;
            // any other request goes through the sign-in check, which verifies the (here empty) password
            var user = HttpContext?.User?.Identity?.IsAuthenticated == true ? LoadSignedInUser(itcode) : DoLogin(itcode, null, null);
            return user;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> _reloadUserOverridden = new System.Collections.Concurrent.ConcurrentDictionary<Type, bool>();

        /// <summary>
        /// Whether a derived context replaces <see cref="ReloadUser"/>. The asynchronous reload must not bypass such an override, so it calls it instead.
        /// </summary>
        private bool IsReloadUserOverridden()
        {
            return _reloadUserOverridden.GetOrAdd(GetType(), t => t.GetMethod(nameof(ReloadUser), new[] { typeof(string) }).DeclaringType != typeof(WTMContext));
        }

        /// <summary>
        /// Asynchronous twin of <see cref="ReloadUser"/>: the same rules (<see cref="ReloadUserFunc"/> first, then the signed-in user, or the sign-in
        /// check for any other request), but the database and token work is awaited instead of blocking the thread.
        /// A derived context that overrides <see cref="ReloadUser"/> keeps its override: it is called (synchronously) instead.
        /// </summary>
        /// <param name="itcode">用户名</param>
        /// <returns>用户信息</returns>
        public virtual async Task<LoginUserInfo> ReloadUserAsync(string itcode)
        {
            if (IsReloadUserOverridden())
            {
                return ReloadUser(itcode);
            }
            if (ReloadUserFunc != null)
            {
                var reload = ReloadUserFunc.Invoke(this, itcode);
                if (reload != null)
                {
                    return reload;
                }
            }
            if (DC == null)
            {
                return null;
            }
            // same split as ReloadUser: a signed-in request is reloaded from its sign-in, any other one goes through the sign-in check with an empty password
            return await DoLoginCoreAsync(itcode, null, null, useSignedInIdentity: HttpContext?.User?.Identity?.IsAuthenticated == true);
        }

        #endregion

        #region URL
        public string BaseUrl { get; set; }
        #endregion

        public string HostAddress { get
            {
                if (this.HttpContext?.Request != null)
                {
                    return $"{this.HttpContext.Request.Scheme}://{this.HttpContext.Request.Host}";
                }
                else
                {
                    return "";
                }
            }
        }

        public SimpleLog Log { get; set; }

        protected ILogger<ActionLog> Logger { get; set; }


        private IQueryable<FrameworkUserBase> _baseUserQuery;
        public IQueryable<FrameworkUserBase> BaseUserQuery
        {
            get
            {
                if (_baseUserQuery == null && this.GlobaInfo?.CustomUserType != null && DC != null)
                {
                    var set = DC.GetType().GetMethod("Set", Type.EmptyTypes).MakeGenericMethod(GlobaInfo.CustomUserType);
                    _baseUserQuery = set.Invoke(DC, null) as IQueryable<FrameworkUserBase>;
                }
                return _baseUserQuery;
            }
        }

        public WTMContext(IOptionsMonitor<Configs> _config, GlobalData _gd = null, IHttpContextAccessor _http = null, IUIService _ui = null, List<IDataPrivilege> _dp = null, IDataContext dc = null, IStringLocalizerFactory stringLocalizer = null, ILoggerFactory loggerFactory = null, WtmLocalizationOption lop = null, IDistributedCache cache = null, IServiceProvider sp=null)
        {
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
                _dp = new List<IDataPrivilege>();
            }
            _dps = _dp;
            if (dc is NullContext)
            {
                _dc = null;
            }
            else
            {
                _dc = dc;
            }
            _serviceProvider = sp;
        }

        public void SetServiceProvider(IServiceProvider sp)
        {
            this._serviceProvider = sp;
        }

        /// <summary>
        /// Signs a user in: the account and password of the request are always verified (for the tenant of the request or the given one) before a user
        /// is returned and a token is issued, whether or not the caller is already signed in. A signed-in caller that sends credentials is
        /// treated like any other: it gets the user of the credentials it sent, or null when they do not match.
        /// To reload the signed-in user without credentials see <see cref="ReloadUser"/>.
        /// </summary>
        public LoginUserInfo DoLogin(string username, string password, string tenant)
        {
            return DoLoginCore(username, password, tenant, useSignedInIdentity: false);
        }

        /// <summary>
        /// Loads the user of the current (already verified) sign-in: the account, tenant and remote token come from the claims of the signed-in
        /// request, not from credentials, so no password is involved. Used to rebuild the user after its cache entry is gone. It does not
        /// sign anybody in: the identity is the one the authentication middleware has verified for this request.
        /// </summary>
        private LoginUserInfo LoadSignedInUser(string itcode)
        {
            return DoLoginCore(itcode, null, null, useSignedInIdentity: true);
        }

        /// <summary>
        /// The tenant a sign-in is for: the given one, else the tenant of the data context, else (for the signed-in identity) the tenant claim of the request.
        /// Shared by <see cref="DoLoginCore"/> and <see cref="DoLoginCoreAsync"/>.
        /// </summary>
        private string ResolveLoginTenant(string tenant, bool useSignedInIdentity)
        {
            if(string.IsNullOrEmpty(tenant))
            {
                tenant = DC.TenantCode;               
            }
            if (tenant == null && useSignedInIdentity)
            {
                tenant = HttpContext.User.Claims.Where(x => x.Type == AuthConstants.JwtClaimTypes.TenantCode).Select(x => x.Value).FirstOrDefault() ?? tenant;
            }
            return tenant;
        }

        /// <summary>
        /// The part of the local (non mainhost) sign-in that comes before the roles, groups and privileges are loaded: resolves the tenant data context,
        /// verifies the account (and password) and creates the bare user. Returns null when the tenant does not exist or the account does not verify.
        /// Everything here is synchronous; shared by <see cref="DoLoginCore"/> and <see cref="DoLoginCoreAsync"/>.
        /// </summary>
        private LoginUserInfo PrepareLocalLogin(string username, string password, string tenant, bool useSignedInIdentity)
        {
            bool exist = false;
            if (useSignedInIdentity)
            {
                username = HttpContext.User.Claims.Where(x => x.Type == AuthConstants.JwtClaimTypes.Subject).Select(x => x.Value).FirstOrDefault() ?? username;
            }
            var ct = GlobaInfo.AllTenant.Where(x => x.TCode == tenant).FirstOrDefault();
            //如果找不到指定的tenant，说明租户不存在，直接返回null
            if(ct == null && string.IsNullOrEmpty(tenant) == false)
            {
                return null;
            }
            if (ct != null)
            {
                _dc = ct.CreateDC(this);
            }
            if (useSignedInIdentity)
            {
                exist = BaseUserQuery.IgnoreQueryFilters().Any(x => x.ITCode == username && x.TenantCode == tenant && x.IsValid==true);
            }
            else
            {
                exist = CheckLoginPassword(username, password, tenant);
            }
            if (exist == false)
            {
                return null;
            }

            return new LoginUserInfo
            {
                ITCode = username,
                TenantCode = tenant
            };
        }

        private LoginUserInfo DoLoginCore(string username, string password, string tenant, bool useSignedInIdentity)
        {
            // the mainhost branch below is duplicated in DoLoginCoreAsync (there the calls are awaited): any change to it must be made in both
            tenant = ResolveLoginTenant(tenant, useSignedInIdentity);
            if (ConfigInfo.HasMainHost && string.IsNullOrEmpty(tenant) == true)
            {
                var remoteToken = (useSignedInIdentity ? _loginUserInfo?.RemoteToken : null) ?? HttpContext?.Request.Query?.Where(x => x.Key == "_remotetoken").Select(x => x.Value.First()).FirstOrDefault();
                if (useSignedInIdentity)
                {
                    remoteToken = HttpContext.User.Claims.Where(x => x.Type == AuthConstants.JwtClaimTypes.RToken).Select(x => x.Value).FirstOrDefault();
                }
                    LoginUserInfo rv = null;
                if (string.IsNullOrEmpty(remoteToken) == false)
                {
                    Dictionary<string, string> headers = new Dictionary<string, string>();
                    headers.Add("Authorization", "Bearer " + remoteToken);
                    var user = CallAPI<LoginUserInfo>("mainhost", "/api/_account/checkuserinfo?IsApi=false", HttpMethodEnum.GET, new { }, 10, headers: headers).Result;
                    rv = user.Data;
                    if (rv != null)
                    {
                        rv.RemoteToken = remoteToken;
                    }
                }
                else if(string.IsNullOrEmpty(password)==false)
                {
                    var loginjwt = CallAPI<Token>("mainhost", "/api/_account/loginjwt", HttpMethodEnum.POST, new { Account = username, Password = password }, 10).Result;
                    if (string.IsNullOrEmpty(loginjwt?.Data?.AccessToken) == false)
                    {
                        remoteToken = loginjwt?.Data?.AccessToken;
                        Dictionary<string, string> headers = new Dictionary<string, string>();
                        headers.Add("Authorization", "Bearer " + remoteToken);
                        var user = CallAPI<LoginUserInfo>("mainhost", "/api/_account/checkuserinfo?IsApi=false", HttpMethodEnum.GET, new { }, 10, headers: headers).Result;
                        rv = user.Data;
                        if (rv != null)
                        {
                            rv.RemoteToken = remoteToken;
                        }
                    }
                }
                if (rv != null)
                {
                    //var cacheKey = $"{GlobalConstants.CacheKey.UserInfo}:{rv.ITCode + "$`$" + rv.TenantCode}";
                    //var cacheuser = Cache.Get<LoginUserInfo>(cacheKey);
                    //if (cacheuser != null && cacheuser.TimeTick >= rv.TimeTick)
                    //{
                    //    rv = cacheuser;
                    //}
                    //else
                    //{
                        rv.LoadBasicInfoAsync(this).Wait();
                    //}
                }
                return rv;
            }
            else
            {
                var user = PrepareLocalLogin(username, password, tenant, useSignedInIdentity);
                if (user == null)
                {
                    return null;
                }
                //var cacheKey = $"{GlobalConstants.CacheKey.UserInfo}:{username + "$`$" + tenant}";
                //var cacheuser = Cache.Get<LoginUserInfo>(cacheKey);
                //if (cacheuser != null)
                //{
                //    user = cacheuser;
                //}
                //else
                //{
                    user.LoadBasicInfoAsync(this).Wait();
                //}
                user.RemoteToken = null;
                var authService = HttpContext.RequestServices.GetService(typeof(ITokenService)) as ITokenService;
                var token = authService.IssueTokenAsync(user).Result;
                user.RemoteToken = token.AccessToken;
                return user;
            }
        }

        /// <summary>
        /// Asynchronous twin of <see cref="DoLoginCore"/>: same checks, same tenant handling, same claims, same token, but loading the roles, groups and
        /// privileges, issuing the token and calling the main host are awaited instead of blocking the thread.
        /// </summary>
        private async Task<LoginUserInfo> DoLoginCoreAsync(string username, string password, string tenant, bool useSignedInIdentity)
        {
            // the mainhost branch below is duplicated in DoLoginCore (there the calls block the thread): any change to it must be made in both
            tenant = ResolveLoginTenant(tenant, useSignedInIdentity);
            if (ConfigInfo.HasMainHost && string.IsNullOrEmpty(tenant) == true)
            {
                var remoteToken = (useSignedInIdentity ? _loginUserInfo?.RemoteToken : null) ?? HttpContext?.Request.Query?.Where(x => x.Key == "_remotetoken").Select(x => x.Value.First()).FirstOrDefault();
                if (useSignedInIdentity)
                {
                    remoteToken = HttpContext.User.Claims.Where(x => x.Type == AuthConstants.JwtClaimTypes.RToken).Select(x => x.Value).FirstOrDefault();
                }
                LoginUserInfo rv = null;
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
                var user = PrepareLocalLogin(username, password, tenant, useSignedInIdentity);
                if (user == null)
                {
                    return null;
                }
                await user.LoadBasicInfoAsync(this);
                user.RemoteToken = null;
                var authService = HttpContext.RequestServices.GetService(typeof(ITokenService)) as ITokenService;
                var token = await authService.IssueTokenAsync(user);
                user.RemoteToken = token.AccessToken;
                return user;
            }
        }

        /// <summary>
        /// Checks the password of an unauthenticated login in memory (the stored hash is no longer compared inside the SQL query),
        /// so that every supported hash format can be verified. Same filters as before: account, tenant and IsValid.
        /// A null password keeps its old meaning: it only matches an account whose stored password is empty, and no hashing work is done for it.
        /// A hash that <see cref="Core.PasswordHasher.Verify"/> reports as outdated is replaced after a successful login.
        /// </summary>
        private bool CheckLoginPassword(string username, string password, string tenant)
        {
            var hasher = PasswordHasher;
            var dc = DC;
            var candidates = GetUserQuery(dc).IgnoreQueryFilters()
                .Where(x => x.ITCode == username && x.TenantCode == tenant && x.IsValid == true)
                .Select(x => new { x.ID, x.Password })
                .ToList();
            if (candidates.Count == 0)
            {
                //unknown account: spend about the same time as a real check
                hasher.BurnTime(password);
                return false;
            }
            foreach (var candidate in candidates)
            {
                if (password == null)
                {
                    //previously compared against GetMD5String(null), which is the empty string
                    if (candidate.Password == string.Empty)
                    {
                        return true;
                    }
                    continue;
                }
                var result = hasher.Verify(candidate.Password, password);
                if (result == PasswordVerifyResult.Failed)
                {
                    continue;
                }
                if (result == PasswordVerifyResult.SuccessRehashNeeded)
                {
                    TryRehashPassword(dc, candidate.ID, candidate.Password, password, hasher);
                }
                return true;
            }
            return false;
        }

        private IQueryable<FrameworkUserBase> GetUserQuery(IDataContext dc)
        {
            //resolved from the given context every time: BaseUserQuery caches the set of whichever context was current when it was first read
            var set = dc.GetType().GetMethod("Set", Type.EmptyTypes).MakeGenericMethod(GlobaInfo.CustomUserType);
            return set.Invoke(dc, null) as IQueryable<FrameworkUserBase>;
        }

        /// <summary>
        /// Replaces the stored hash after a successful login, but only while the row still holds the hash that was verified,
        /// so a password changed in the meantime is never reverted. The write touches nothing else: a single-statement update
        /// (ExecuteUpdate) where the provider supports it, otherwise a short-lived separate context, never the change tracker of the request context.
        /// A failure is logged and ignored; it must not block the login.
        /// </summary>
        private void TryRehashPassword(IDataContext dc, Guid userId, string verifiedStoredHash, string password, PasswordHasher hasher)
        {
            try
            {
                var newHash = hasher.Hash(password);
                try
                {
                    GetUserQuery(dc).IgnoreQueryFilters()
                        .Where(x => x.ID == userId && x.Password == verifiedStoredHash)
                        .ExecuteUpdate(x => x.SetProperty(y => y.Password, newHash));
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is NotSupportedException)
                {
                    //providers without set based updates (for example the in memory provider)
                    using (var fresh = dc.CreateNew())
                    {
                        var entity = GetUserQuery(fresh).IgnoreQueryFilters().Where(x => x.ID == userId).FirstOrDefault();
                        if (entity != null && entity.Password == verifiedStoredHash)
                        {
                            entity.Password = newHash;
                            fresh.SaveChanges();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _loggerFactory?.CreateLogger<WTMContext>().LogWarning("Password rehash after login failed: {ExceptionType}", ex.GetType().Name);
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(PasswordAlgorithm, int, int?, string), Lazy<PasswordHasher>> _passwordHashers = new System.Collections.Concurrent.ConcurrentDictionary<(PasswordAlgorithm, int, int?, string), Lazy<PasswordHasher>>();
        private PasswordHasher _passwordHasher;
        private IPasswordColumnInspector _passwordColumnInspector;

        /// <summary>
        /// Looks up the real length of the password column in the database. Only used when PBKDF2 is configured; it never runs with the
        /// default MD5 options. The setter replaces the inspector for this context only (for example in a test).
        /// </summary>
        public IPasswordColumnInspector PasswordColumnInspector
        {
            get
            {
                return _passwordColumnInspector ?? Core.PasswordColumnInspector.Default;
            }
            set
            {
                _passwordColumnInspector = value;
            }
        }

        /// <summary>
        /// The password hasher for the configured <see cref="Configs.PasswordOptions"/>. With the default options it produces and accepts
        /// the same MD5 hashes as <see cref="Utils.GetMD5String"/>.
        /// When PBKDF2 is configured, it is used only if the password column of the real database (not just the EF model) can hold it, see
        /// <see cref="PasswordColumnInspector"/> and docs/migrations/password-hash-upgrade.md.
        /// The setter replaces the hasher for this context only. A replacement must apply the same column length guard as the default
        /// one (see <see cref="Core.PasswordHasher"/>): do not produce PBKDF2 hashes while the password column is too short to hold them.
        /// </summary>
        public PasswordHasher PasswordHasher
        {
            get
            {
                if (_passwordHasher == null)
                {
                    var options = ConfigInfo?.PasswordOptions ?? new PasswordOption();
                    int? maxLength = null;
                    string unverified = null;
                    if (options.Algorithm == PasswordAlgorithm.Pbkdf2Sha256)
                    {
                        //the model (or attributes) say what the application expects; the database says what is really there. Both must allow the longer format.
                        var userType = GlobaInfo?.CustomUserType;
                        maxLength = PasswordHasher.GetPasswordMaxLength(userType, _dc);
                        IDataContext dc = null;
                        string dcError = null;
                        try
                        {
                            dc = DC;
                        }
                        catch (Exception ex)
                        {
                            dcError = ex.GetType().Name;
                        }
                        PasswordColumnInfo column;
                        if (dcError != null)
                        {
                            column = PasswordColumnInfo.Unverified("no data context available: " + dcError);
                        }
                        else
                        {
                            try
                            {
                                column = PasswordColumnInspector.Inspect(dc, userType);
                            }
                            catch (Exception ex)
                            {
                                column = PasswordColumnInfo.Unverified("the column inspector failed: " + ex.GetType().Name);
                            }
                        }
                        if (column.IsVerified == false)
                        {
                            unverified = column.Description;
                        }
                        else if (column.MaxLength.HasValue)
                        {
                            maxLength = maxLength.HasValue ? Math.Min(maxLength.Value, column.MaxLength.Value) : column.MaxLength;
                        }
                    }
                    var key = (options.Algorithm, options.Pbkdf2Iterations, maxLength, unverified);
                    //Lazy: GetOrAdd may run the factory more than once under contention, and the constructor logs the guard Warning
                    _passwordHasher = _passwordHashers.GetOrAdd(key, k => new Lazy<PasswordHasher>(() => new PasswordHasher(options, maxLength, _loggerFactory?.CreateLogger<WTMContext>(), unverified))).Value;
                }
                return _passwordHasher;
            }
            set
            {
                _passwordHasher = value;
            }
        }

        /// <summary>
        /// Hashes a password for storage in the user table (MD5 unless configured otherwise, see <see cref="Configs.PasswordOptions"/>).
        /// </summary>
        public string HashPassword(string password)
        {
            return PasswordHasher.Hash(password);
        }

        /// <summary>
        /// Checks a password against a stored hash of any supported format.
        /// </summary>
        public PasswordVerifyResult VerifyPassword(string storedHash, string password)
        {
            return PasswordHasher.Verify(storedHash, password);
        }

        public Token RefreshToken()
        {
            if(LoginUserInfo == null)
            {
                return null;
            }
            string rt = null;
            if (ConfigInfo.HasMainHost && LoginUserInfo?.CurrentTenant == null)
            {
                var r = CallAPI<Token>("mainhost", $"/api/_account/RefreshToken", HttpMethodEnum.POST, new { }).Result;
                rt = r?.Data?.AccessToken;
            }
            else
            {
                rt = LoginUserInfo.RemoteToken;
           }
            var _authService = ServiceProvider.GetRequiredService<ITokenService>();
            var rv = _authService.IssueTokenAsync(new LoginUserInfo
            {
                ITCode = LoginUserInfo.ITCode,
                TenantCode = LoginUserInfo.TenantCode,
                RemoteToken = rt
            }).Result;
            return rv;
        }

        public T ReadFromCache<T>(string key, Func<T> setFunc, int? timeout = null)
        {
            if (Cache.TryGetValue(key, out T rv) == false || rv == null)
            {
                T data = setFunc();
                if (timeout == null)
                {
                    Cache.Add(key, data);
                }
                else
                {
                    Cache.Add(key, data, new DistributedCacheEntryOptions()
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
            foreach (var userId in userIds)
            {
                var key = $"{GlobalConstants.CacheKey.UserInfo}:{userId + "$`$" + LoginUserInfo?.CurrentTenant}";
                await Cache.DeleteAsync(key);
            }
        }

        public async Task RemoveUserCacheByRole(
    params string[] rolecode)
        {
            List<string> userids = new List<string>();
            if (ConfigInfo.HasMainHost && string.IsNullOrEmpty(LoginUserInfo?.CurrentTenant) == true)
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
                userids = DC.Set<FrameworkUserRole>().Where(x => rolecode.Contains(x.RoleCode)).Select(x => x.UserCode).ToList();
            }
            foreach (var userId in userids)
            {
                var key = $"{GlobalConstants.CacheKey.UserInfo}:{userId + "$`$" + LoginUserInfo?.CurrentTenant}";
                await Cache.DeleteAsync(key);
            }
        }

        public async Task RemoveUserCacheByGroup(
params string[] groupcode)
        {
            List<string> userids = new List<string>();
            if (ConfigInfo.HasMainHost && string.IsNullOrEmpty(LoginUserInfo?.CurrentTenant) == true)
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
                userids = DC.Set<FrameworkUserGroup>().Where(x => groupcode.Contains(x.GroupCode)).Select(x => x.UserCode).ToList();
            }
            foreach (var userId in userids)
            {
                var key = $"{GlobalConstants.CacheKey.UserInfo}:{userId + "$`$" + LoginUserInfo?.CurrentTenant}";
                await Cache.DeleteAsync(key);
            }
        }

        public async Task RemoveGroupCache(string tenant)
        {

                var key = $"{GlobalConstants.CacheKey.TenantGroups}:{tenant}";
                await Cache.DeleteAsync(key);
        }

        public async Task RemoveRoleCache(string tenant)
        {

                var key = $"{GlobalConstants.CacheKey.TenantRoles}:{tenant}";
                await Cache.DeleteAsync(key);
            
        }

        public List<SimpleGroup> GetTenantGroups(string tenant)
        {
            var key = $"{GlobalConstants.CacheKey.TenantGroups}:{tenant}";
            var rv = ReadFromCache<List<SimpleGroup>>(key, () =>
            {
                
                List<SimpleGroup> groups = null;
                try
                {
                    var dbtenant = GlobaInfo.AllTenant.Where(x => x.TCode == tenant && x.IsUsingDB == true).FirstOrDefault();
                    using (var dc = dbtenant == null ? ConfigInfo.Connections.Where(x => x.Key.ToLower() == "default").FirstOrDefault().CreateDC() : dbtenant.CreateDC(this))
                    {
                        groups = dc.Set<FrameworkGroup>().IgnoreQueryFilters().Where(x => x.TenantCode == tenant).Select(x => new SimpleGroup
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
                catch
                {
                    groups = new List<SimpleGroup>();
                }
                return groups;
            }, 360000);
            return rv;
        }

        public List<SimpleRole> GetTenantRoles(string tenant)
        {
            var key = $"{GlobalConstants.CacheKey.TenantRoles}:{tenant}";
            var rv = ReadFromCache<List<SimpleRole>>(key, () =>
            {
                List<SimpleRole> roles = null;
                try
                {
                    var dbtenant = GlobaInfo.AllTenant.Where(x => x.TCode == tenant && x.IsUsingDB == true).FirstOrDefault();
                    using (var dc = dbtenant == null ? ConfigInfo.Connections.Where(x => x.Key.ToLower() == "default").FirstOrDefault().CreateDC() : dbtenant.CreateDC(this))
                    {
                        roles = dc.Set<FrameworkRole>().IgnoreQueryFilters().Where(x => x.TenantCode == tenant).Select(x => new SimpleRole
                        {
                            ID = x.ID,
                            RoleCode = x.RoleCode,
                            RoleName = x.RoleName,
                            Tenant = x.TenantCode
                        }).ToList();
                    }
                }
                catch
                {
                    roles = new List<SimpleRole>();
                }
                return roles;
            }, 360000);
            return rv;
        }


        /// <summary>
        /// Switches the tenant the signed-in user works in. A user of the main tenant (no tenant code) may switch to no tenant (null or empty)
        /// or to an existing, enabled tenant; a user that belongs to a tenant may switch to the own tenant or to one of its sub tenants.
        /// Any other target is refused and nothing changes.
        /// </summary>
        public bool SetCurrentTenant(string tenant)
        {
            if (LoginUserInfo != null)
            {
                var tenants = GlobaInfo.AllTenant ?? new List<FrameworkTenant>();
                bool allowed = LoginUserInfo.TenantCode == null
                    ? string.IsNullOrEmpty(tenant) || tenants.Any(x => x.TCode == tenant && x.Enabled)
                    : LoginUserInfo.TenantCode == tenant || tenants.Any(x => x.TCode == tenant && x.TenantCode == LoginUserInfo.TenantCode);
                if (allowed)
                {
                    LoginUserInfo.CurrentTenant = tenant;
                    LoginUserInfo = LoginUserInfo;
                    return true;
                }
            }
            return false;
        }

        #region CreateDC
        public virtual IDataContext CreateDC(bool isLog = false, string cskey = null, bool logerror = true)
        {
            string cs = cskey ?? CurrentCS;
            string tenantCode = null;

            var tenants = GlobaInfo.AllTenant ?? new List<FrameworkTenant>();
            string tc = _loginUserInfo?.CurrentTenant;
            if (tc == null && HttpContext?.Request.Headers.ContainsKey("Referer")==true)
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
                    return item.CreateDC(this);
                }
            }

            if (isLog == true)
            {
                if (ConfigInfo.Connections?.Where(x => x.Key.ToLower() == "defaultlog").FirstOrDefault() != null)
                {
                    cs = "defaultlog";
                }
            }
            if (string.IsNullOrEmpty(cs))
            {
                cs = "default";
            }
            var rv = ConfigInfo.Connections.Where(x => x.Key.ToLower() == cs.ToLower()).FirstOrDefault().CreateDC();
            rv.IsDebug = ConfigInfo.IsQuickDebug;
            rv.SetTenantCode(tenantCode);
            if (logerror == true)
            {
                rv.SetLoggerFactory(_loggerFactory);
            }
            return rv;
        }

        #endregion

        /// <summary>
        /// 判断某URL是否有权限访问
        /// </summary>
        /// <param name="url">url地址</param>
        /// <returns>true代表可以访问，false代表不能访问</returns>
        public bool IsAccessable(string url)
        {
            // 如果是调试 或者 url 为 null or 空字符串
            if (_configInfo.IsQuickDebug || string.IsNullOrEmpty(url) || IsUrlPublic(url))
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


            url = Regex.Replace(url, "/do(batch.*)", "/$1", RegexOptions.IgnoreCase);

            //如果url以#开头，一般是javascript使用的临时地址，不需要判断，直接返回true
            url = url.Trim();

            if (url.StartsWith("#"))
            {
                return true;
            }
            var menus = _globaInfo.AllMenus;
            var menu = Utils.FindMenu(url, GlobaInfo.AllMenus);
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
        protected bool IsAccessable(SimpleMenu menu, List<SimpleMenu> menus)
        {
            if (LoginUserInfo.CurrentTenant != null && menu.TenantAllowed == false)
            {
                return false;
            }
            //寻找当前菜单的页面权限
            var find = LoginUserInfo?.FunctionPrivileges.Where(x => x.MenuItemId == menu.ID && x.Allowed == true).FirstOrDefault();
            //如果能找到直接对应的页面权限
            if (find != null)
            {
                return true;
            }
            return false;
        }

        #region Generic endpoint references

        /// <summary>
        /// Set by the privilege filter of the request: the url of the current page when opening it needs a menu privilege (it is not public
        /// and not open to every signed-in user), otherwise null. References written into the page record it, so that they stop working when
        /// the user loses the privilege. See <see cref="GenericEndpointGuard"/>.
        /// </summary>
        public string PagePrivilegeUrl { get; set; }

        private IDataProtector GenericEndpointProtector()
        {
            return GenericEndpointGuard.CreateProtector(ServiceProvider?.GetService<IDataProtectionProvider>());
        }

        /// <summary>
        /// Creates the reference that lets the generic data endpoints (<c>/_Framework/GetPagingData</c>, <c>Selector</c>, <c>GetExportExcel</c>,
        /// <c>GetExcelTemplate</c>, <c>GetEmptyData</c>, <c>UpdateModelProperty</c>) serve a view model to the current user. The LayUI
        /// TagHelpers call it for the view models they show; pages that call the endpoints from their own script obtain the reference here
        /// and send it as <see cref="GenericEndpointGuard.ReferenceParameter"/> together with <c>_DONOT_USE_VMNAME</c>.
        /// </summary>
        /// <param name="viewModelType">the view model</param>
        /// <param name="purposes">what the endpoints may do with it</param>
        /// <param name="connectionKey">connection key the page uses (default: <see cref="CurrentCS"/>)</param>
        /// <param name="editableFields">for <see cref="GenericEndpointPurpose.Update"/>: the properties the page lets the user edit</param>
        /// <returns>the reference, or null when none can be created (then the endpoints refuse the request)</returns>
        public string CreateGenericEndpointReference(Type viewModelType, GenericEndpointPurpose purposes, string connectionKey = null, IEnumerable<string> editableFields = null)
        {
            try
            {
                return GenericEndpointGuard.Issue(GenericEndpointProtector(), ConfigInfo?.GenericEndpoints, viewModelType, purposes,
                    connectionKey ?? CurrentCS, LoginUserInfo?.ITCode, LoginUserInfo?.CurrentTenant, PagePrivilegeUrl, DateTimeOffset.UtcNow, editableFields);
            }
            catch (Exception ex)
            {
                _loggerFactory?.CreateLogger<WTMContext>().LogWarning("A generic endpoint reference could not be created: {ExceptionType}", ex.GetType().Name);
                return null;
            }
        }

        /// <summary>
        /// Decides whether a generic data endpoint may serve the view model a request names. See <see cref="GenericEndpointGuard.Authorize"/>.
        /// </summary>
        public GenericEndpointDecision AuthorizeGenericEndpoint(string viewModelName, string reference, GenericEndpointPurpose purpose, bool checkConnection = false, string connectionKey = null, string field = null)
        {
            var protector = GenericEndpointProtector();
            return GenericEndpointGuard.Authorize(new GenericEndpointRequestContext
            {
                Options = ConfigInfo?.GenericEndpoints,
                ViewModelName = viewModelName,
                Purpose = purpose,
                Reference = reference,
                CheckConnection = checkConnection,
                ConnectionKey = connectionKey,
                Field = field,
                User = LoginUserInfo?.ITCode,
                Tenant = LoginUserInfo?.CurrentTenant,
                Now = DateTimeOffset.UtcNow,
                OpenReference = r => GenericEndpointGuard.Unprotect(protector, r),
                CanOpenPage = IsAccessable,
                ResolveType = n =>
                {
                    try { return Type.GetType(n); } catch { return null; }
                }
            });
        }

        #endregion

        #region File references

        private IDataProtector FileEndpointProtector()
        {
            return FileEndpointGuard.CreateProtector(ServiceProvider?.GetService<IDataProtectionProvider>());
        }

        /// <summary>
        /// Creates the reference that lets the file endpoints (<c>/_Framework/GetFile</c>, <c>ViewFile</c>, <c>GetFileName</c>) serve one file to the
        /// current user (see <see cref="FileEndpointGuard"/>). The framework's LayUI TagHelpers and file buttons call it for the links they render;
        /// code that writes its own link to a file calls it too (or uses <see cref="GetFileUrl"/>) and sends the result as
        /// <see cref="FileEndpointGuard.ReferenceParameter"/>.
        /// </summary>
        /// <param name="fileId">id of the file</param>
        /// <param name="purposes">what the reference allows (default: view, download and name)</param>
        /// <param name="connectionKey">connection key of the page (default: <see cref="CurrentCS"/>)</param>
        /// <returns>the reference, or null when none can be created (then the link works only where the endpoints do not require one)</returns>
        public string CreateFileReference(string fileId, FilePurpose purposes = FilePurpose.Read, string connectionKey = null)
        {
            try
            {
                return FileEndpointGuard.Issue(FileEndpointProtector(), ConfigInfo?.FileEndpoints, fileId, purposes,
                    connectionKey ?? CurrentCS, LoginUserInfo?.ITCode, LoginUserInfo?.CurrentTenant, DateTimeOffset.UtcNow, LoginUserInfo?.TenantCode);
            }
            catch (Exception ex)
            {
                _loggerFactory?.CreateLogger<WTMContext>().LogWarning("A file reference could not be created: {ExceptionType}", ex.GetType().Name);
                return null;
            }
        }

        /// <summary>Same as <see cref="CreateFileReference(string, FilePurpose, string)"/> for a guid id.</summary>
        public string CreateFileReference(Guid fileId, FilePurpose purposes = FilePurpose.Read, string connectionKey = null)
        {
            return CreateFileReference(fileId.ToString(), purposes, connectionKey);
        }

        /// <summary>
        /// Creates a short-lived reference that is not bound to a user (a capability): whoever holds it can do what <paramref name="purposes"/> allows
        /// with that one file until it expires (<c>FileEndpoints:ReferenceLifetimeMinutes</c>), signed in or not. For pages that show files in
        /// elements which cannot send credentials, such as the <c>img</c> tag of a single page application. The reference is bound to the file, the
        /// connection and the tenant of the signed-in user; <see cref="FilePurpose.Delete"/> and <see cref="FilePurpose.Upload"/> are never granted.
        /// </summary>
        /// <param name="fileId">id of the file</param>
        /// <param name="purposes">what the reference allows (default: view, download and name)</param>
        /// <param name="connectionKey">connection key of the page (default: <see cref="CurrentCS"/>)</param>
        /// <returns>the reference, or null when none can be created</returns>
        public string CreateFileCapability(string fileId, FilePurpose purposes = FilePurpose.Read, string connectionKey = null)
        {
            try
            {
                purposes &= FilePurpose.Read;
                if (purposes == FilePurpose.None)
                {
                    return null;
                }
                return FileEndpointGuard.Issue(FileEndpointProtector(), ConfigInfo?.FileEndpoints, fileId, purposes,
                    connectionKey ?? CurrentCS, null, LoginUserInfo?.CurrentTenant, DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                _loggerFactory?.CreateLogger<WTMContext>().LogWarning("A file reference could not be created: {ExceptionType}", ex.GetType().Name);
                return null;
            }
        }

        /// <summary>Same as <see cref="CreateFileCapability(string, FilePurpose, string)"/> for a guid id.</summary>
        public string CreateFileCapability(Guid fileId, FilePurpose purposes = FilePurpose.Read, string connectionKey = null)
        {
            return CreateFileCapability(fileId.ToString(), purposes, connectionKey);
        }

        /// <summary>
        /// The url of the file endpoint for one purpose, with a reference for the current user:
        /// <see cref="FilePurpose.View"/> shows the file (<c>GetFile</c> with <c>stream=true</c>), <see cref="FilePurpose.Download"/> downloads it,
        /// <see cref="FilePurpose.Name"/> returns its name (<c>GetFileName</c>).
        /// </summary>
        public string GetFileUrl(string fileId, FilePurpose purpose = FilePurpose.Download, string connectionKey = null)
        {
            var key = connectionKey ?? CurrentCS;
            var url = purpose == FilePurpose.Name ? $"/_Framework/GetFileName/{Uri.EscapeDataString(fileId ?? string.Empty)}?1=1" : $"/_Framework/GetFile/{Uri.EscapeDataString(fileId ?? string.Empty)}?1=1";
            if (purpose == FilePurpose.View)
            {
                url += "&stream=true";
            }
            if (string.IsNullOrEmpty(key) == false)
            {
                url += "&_DONOT_USE_CS=" + Uri.EscapeDataString(key);
            }
            var reference = CreateFileReference(fileId, purpose == FilePurpose.None ? FilePurpose.Read : purpose, key);
            if (reference != null)
            {
                url += "&" + FileEndpointGuard.ReferenceParameter + "=" + Uri.EscapeDataString(reference);
            }
            return url;
        }

        /// <summary>Same as <see cref="GetFileUrl(string, FilePurpose, string)"/> for a guid id.</summary>
        public string GetFileUrl(Guid fileId, FilePurpose purpose = FilePurpose.Download, string connectionKey = null)
        {
            return GetFileUrl(fileId.ToString(), purpose, connectionKey);
        }

        /// <summary>
        /// The address of a file endpoint that is not one of the framework's (for example the file API of a single page application), with a
        /// reference. The address is <c>{path}/{id}?1=1</c> plus the connection key as <paramref name="connectionParameter"/> and the reference as
        /// <see cref="FileEndpointGuard.ReferenceParameter"/>.
        /// </summary>
        /// <param name="path">address of the endpoint without the id, for example <c>/api/_file/GetFile</c></param>
        /// <param name="capability">true: the reference is a capability (<see cref="CreateFileCapability(string, FilePurpose, string)"/>), usable by any caller; false: bound to the signed-in user</param>
        public string GetFileUrl(string fileId, FilePurpose purpose, string connectionKey, string path, string connectionParameter = "_DONOT_USE_CS", bool capability = false)
        {
            var key = connectionKey ?? CurrentCS;
            var purposes = purpose == FilePurpose.None ? FilePurpose.Read : purpose;
            var reference = capability ? CreateFileCapability(fileId, purposes, key) : CreateFileReference(fileId, purposes, key);
            return FileEndpointGuard.BuildUrl(path, fileId, connectionParameter, key, reference);
        }

        private FileEndpointRequestContext CreateFileRequest(string fileId, FilePurpose purpose, string reference, Func<FileEndpointFileInfo> getFileInfo, Func<bool> isAvatar = null)
        {
            var protector = FileEndpointProtector();
            return new FileEndpointRequestContext
            {
                Options = ConfigInfo?.FileEndpoints,
                FileId = fileId,
                Purpose = purpose,
                Reference = reference,
                User = LoginUserInfo?.ITCode,
                Tenant = LoginUserInfo?.CurrentTenant,
                HomeTenant = LoginUserInfo?.TenantCode,
                Now = DateTimeOffset.UtcNow,
                OpenReference = r => FileEndpointGuard.Unprotect(protector, r),
                GetFileInfo = getFileInfo,
                IsAvatar = isAvatar
            };
        }

        /// <summary>
        /// Opens a data context for the connection <see cref="ResolveFileConnection"/> chose. A caller who is not signed in has no tenant of its own:
        /// when it was admitted by a reference that names a tenant (a capability), the data is read in that tenant (its own database when it has
        /// one). Anyone else gets what <see cref="CreateDC"/> gives.
        /// </summary>
        public IDataContext CreateFileDC(FileConnectionDecision connection)
        {
            var tenantCode = connection?.Tenant;
            if (LoginUserInfo == null && string.IsNullOrEmpty(tenantCode) == false)
            {
                if (string.IsNullOrEmpty(connection.Key))
                {
                    var tenant = GlobaInfo?.AllTenant?.FirstOrDefault(x => x.TCode == tenantCode);
                    var tenantDc = tenant?.CreateDC(this);
                    if (tenantDc != null)
                    {
                        return tenantDc;
                    }
                }
                var dc = CreateDC(cskey: connection.Key);
                dc.SetTenantCode(tenantCode);
                return dc;
            }
            return CreateDC(cskey: connection?.Key);
        }

        // the image types a browser shows as a picture of a person; SVG is deliberately not one of them
        private static readonly string[] AvatarExtensions = { "jpg", "jpeg", "png", "gif", "bmp", "webp", "ico" };

        /// <summary>
        /// True when the file is the photo (<c>PhotoId</c>) of a valid user that <paramref name="dc"/> can see (so, in the tenant the context is
        /// scoped to) and the file is a picture type a browser shows (not SVG or any other type that runs as part of a site). For file endpoints that
        /// let anybody see the photo of a user (see <see cref="AuthorizeFileRequest(string, string, FilePurpose, string, Func{FileEndpointFileInfo}, Func{bool})"/>).
        /// </summary>
        public bool IsUserPhoto(IDataContext dc, string fileId)
        {
            try
            {
                if (dc == null || GlobaInfo?.CustomUserType == null || Guid.TryParse(fileId, out var id) == false)
                {
                    return false;
                }
                // answers (positive and negative) are kept for a short time, per connection, tenant and file: the avatar of a page is asked for by every
                // picture on it, and the lookup reads the users table
                var key = (dc.CSName ?? string.Empty) + "|" + (dc.TenantCode ?? string.Empty) + "|" + id.ToString("N");
                var now = DateTime.UtcNow;
                if (_userPhotoCache.TryGetValue(key, out var cached) && now - cached.At < _userPhotoCacheTtl)
                {
                    return cached.Value;
                }
                var rv = GetUserQuery(dc).Any(x => x.PhotoId == id && x.IsValid) && IsPictureFile(dc, id);
                if (_userPhotoCache.Count > UserPhotoCacheLimit)
                {
                    _userPhotoCache.Clear();
                }
                _userPhotoCache[key] = (rv, now);
                return rv;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Value, DateTime At)> _userPhotoCache = new System.Collections.Concurrent.ConcurrentDictionary<string, (bool, DateTime)>();
        private static TimeSpan _userPhotoCacheTtl = TimeSpan.FromSeconds(30);
        private const int UserPhotoCacheLimit = 5000;

        private static bool IsPictureFile(IDataContext dc, Guid id)
        {
            var ext = dc.Set<FileAttachment>().Where(x => x.ID == id).Select(x => x.FileExt).FirstOrDefault();
            return ext != null && AvatarExtensions.Contains(ext.TrimStart('.').ToLowerInvariant());
        }

        /// <summary>
        /// True when the file may be set as the photo of a user: it exists, is a picture a browser shows (not SVG), and no other record refers to it as
        /// an attachment (a foreign key to <see cref="FileAttachment"/>, including <see cref="ISubFile"/> lists). The photo of a user is shown without a
        /// reference, so a file that belongs to something else must not become one. Records of the user type itself are not counted.
        /// </summary>
        public bool IsPhotoAssignable(IDataContext dc, Guid fileId)
        {
            try
            {
                if (dc == null || IsPictureFile(dc, fileId) == false)
                {
                    return false;
                }
                foreach (var entityType in dc.Model.GetEntityTypes())
                {
                    var clr = entityType.ClrType;
                    if (clr == typeof(FileAttachment) || typeof(FrameworkUserBase).IsAssignableFrom(clr))
                    {
                        continue;
                    }
                    foreach (var fk in entityType.GetForeignKeys().Where(x => x.PrincipalEntityType.ClrType == typeof(FileAttachment) && x.Properties.Count == 1))
                    {
                        var method = typeof(WTMContext).GetMethod(nameof(AnyReference), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).MakeGenericMethod(clr);
                        if ((bool)method.Invoke(null, new object[] { dc, fk.Properties[0].Name, fk.Properties[0].ClrType, fileId }))
                        {
                            return false;
                        }
                    }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool AnyReference<T>(IDataContext dc, string property, Type propertyType, Guid id) where T : class
        {
            var set = dc.Set<T>().AsQueryable().IgnoreQueryFilters();
            if (propertyType == typeof(Guid))
            {
                return set.Any(e => Microsoft.EntityFrameworkCore.EF.Property<Guid>(e, property) == id);
            }
            return set.Any(e => Microsoft.EntityFrameworkCore.EF.Property<Guid?>(e, property) == id);
        }

        /// <summary>
        /// Decides which database connection a file endpoint runs on. See <see cref="FileEndpointGuard.ResolveConnection"/>: a valid reference
        /// (for the file and purpose, or an upload reference) decides, otherwise only the keys the application lists in
        /// <c>FileEndpoints:RequestConnectionKeys</c> may be chosen by the request.
        /// </summary>
        public FileConnectionDecision ResolveFileConnection(string requestKey, string fileId, FilePurpose purpose, string reference)
        {
            FileEndpointReference valid = null;
            if (string.IsNullOrEmpty(reference) == false)
            {
                FileEndpointGuard.CheckReference(CreateFileRequest(fileId, purpose, reference, null), out valid);
            }
            return FileEndpointGuard.ResolveConnection(requestKey, valid, ConfigInfo, CurrentCS);
        }

        /// <summary>
        /// Decides whether a file endpoint may serve the file, and asks the application's <see cref="IFileAccessPolicy"/>. In
        /// <see cref="FileEndpointMode.Audit"/> a request that has no valid reference is served and a warning is logged.
        /// </summary>
        /// <param name="endpoint">name of the endpoint (for the warning)</param>
        /// <param name="getFileInfo">reads the file's data on the connection of the request (null result: there is no such file)</param>
        public FileEndpointDecision AuthorizeFileRequest(string endpoint, string fileId, FilePurpose purpose, string reference, Func<FileEndpointFileInfo> getFileInfo)
        {
            return AuthorizeFileRequest(endpoint, fileId, purpose, reference, getFileInfo, null);
        }

        /// <summary>
        /// Same as the overload without <paramref name="isAvatar"/>; the extra callback tells whether the file is the photo of a user the request may
        /// see. It is asked only for a request that carries no valid reference, and only after the embedded and upload time rules; true admits the
        /// request as <see cref="FileEndpointAdmission.Avatar"/> in <see cref="FileEndpointMode.Audit"/> and <see cref="FileEndpointMode.Enforce"/>
        /// (the access policy still decides).
        /// </summary>
        public FileEndpointDecision AuthorizeFileRequest(string endpoint, string fileId, FilePurpose purpose, string reference, Func<FileEndpointFileInfo> getFileInfo, Func<bool> isAvatar)
        {
            var decision = FileEndpointGuard.Authorize(CreateFileRequest(fileId, purpose, reference, getFileInfo, isAvatar));
            return ApplyFilePolicy(endpoint, fileId, purpose, decision);
        }

        /// <summary>
        /// Decides whether the signed-in user may delete the file: a <see cref="FilePurpose.Delete"/> reference that was issued to this user for this
        /// file is required in every mode except <see cref="FileEndpointMode.Legacy"/> (<see cref="FileEndpointMode.Audit"/> does not serve a delete
        /// without one). A refusal is logged. The application's <see cref="IFileAccessPolicy"/> is asked as for a read.
        /// </summary>
        public FileEndpointDecision AuthorizeFileDeletion(string endpoint, string fileId, string reference)
        {
            var decision = FileEndpointGuard.AuthorizeDeletion(CreateFileRequest(fileId, FilePurpose.Delete, reference, null));
            return ApplyFilePolicy(endpoint, fileId, FilePurpose.Delete, decision);
        }

        private FileEndpointDecision ApplyFilePolicy(string endpoint, string fileId, FilePurpose purpose, FileEndpointDecision decision)
        {
            // what a request carries is shortened and stripped of control characters before it is logged
            string Referer()
            {
                try { return LogText.Sanitize(HttpContext?.Request?.Headers?["Referer"].ToString(), 256); } catch { return string.Empty; }
            }
            var user = LogText.Sanitize(LoginUserInfo?.ITCode, 100);
            // a caller who is not signed in (files are open to them with IsFilePublic) may be a crawler: those requests are logged at Information,
            // the ones of signed-in users, which point at a link that was written without a reference, at Warning
            var level = LoginUserInfo == null ? LogLevel.Information : LogLevel.Warning;
            if (decision.Allowed == false)
            {
                _loggerFactory?.CreateLogger<WTMContext>().Log(level, "File endpoint {Endpoint} refused a request: {Reason} (user {User}, referer {Referer})",
                    endpoint, decision.Refusal, user, Referer());
                return decision;
            }
            if (decision.Admission == FileEndpointAdmission.Audited)
            {
                _loggerFactory?.CreateLogger<WTMContext>().Log(level, "File endpoint {Endpoint} served a request without a valid file reference: {Reason} (user {User}, referer {Referer}). It is refused when FileEndpoints:Mode is Enforce.",
                    endpoint, decision.Refusal, user, Referer());
            }
            if (decision.Admission != FileEndpointAdmission.Legacy && decision.Admission != FileEndpointAdmission.FileMissing)
            {
                bool allowed;
                try
                {
                    var policy = ServiceProvider?.GetService<IFileAccessPolicy>();
                    allowed = policy == null || policy.IsAllowed(new FileAccessRequest
                    {
                        FileId = fileId,
                        Purpose = purpose,
                        Admission = decision.Admission,
                        User = LoginUserInfo?.ITCode,
                        Tenant = LoginUserInfo?.CurrentTenant,
                        Wtm = this
                    });
                }
                catch (Exception ex)
                {
                    // a policy that fails does not let the request through
                    _loggerFactory?.CreateLogger<WTMContext>().LogError("The file access policy failed for {Endpoint}: {ExceptionType}; the request is refused", endpoint, ex.GetType().Name);
                    allowed = false;
                }
                if (allowed == false)
                {
                    _loggerFactory?.CreateLogger<WTMContext>().Log(level, "File endpoint {Endpoint} refused a request: the file access policy denied it (user {User})", endpoint, user);
                    return FileEndpointDecision.Refuse(FileEndpointRefusal.PolicyDenied);
                }
            }
            return decision;
        }

        #endregion

        public bool IsUrlPublic(string url)
        {
            var isPublic = false;
            try
            {
                url = Regex.Replace(url, "/do(batch.*)", "/$1", RegexOptions.IgnoreCase);
                url = url.Trim();

                if (url.StartsWith("#"))
                {
                    isPublic = true;
                }
                var menus = GlobaInfo.AllMenus;
                var menu = Utils.FindMenu(url, menus);
                if (menu != null && menu.IsPublic == true)
                {
                    isPublic = true;
                }
            }
            catch { }
            return isPublic;
        }

        public void DoLog(string msg, ActionLogTypesEnum logtype = ActionLogTypesEnum.Normal, string moduleName = "", string actionName = "", string ip = "", string url = "", double duration = 0)
        {
            var log = this.Log?.GetActionLog();
            if (log == null)
            {
                log = new ActionLog();
            }
            log.LogType = logtype;
            log.ActionTime = DateTime.Now;
            log.Remark = msg;
            log.ActionUrl = url;
            log.Duration = duration;
            log.ModuleName = moduleName;
            log.ActionName = actionName;
            log.ITCode = LoginUserInfo?.ITCode;
            log.IP = ip;
            if (string.IsNullOrEmpty(url) && this.HttpContext?.Request != null)
            {
                log.ActionUrl = this.HttpContext.Request.Path.ToString();
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
        private BaseVM CreateVM(Type VMType, object Id = null, object[] Ids = null, Dictionary<string, object> values = null, bool passInit = false)
        {
            //Use reflection to create viewmodel
            var ctor = VMType.GetConstructor(Type.EmptyTypes);
            BaseVM rv = ctor.Invoke(null) as BaseVM;
            rv.Wtm = this;

            rv.FC = new Dictionary<string, object>();
            rv.CreatorAssembly = this.GetType().AssemblyQualifiedName;
            rv.ControllerName = this.HttpContext?.Request?.Path;
            if (HttpContext != null && HttpContext.Request != null)
            {
                try
                {
                    if (HttpContext.Request.QueryString != QueryString.Empty)
                    {
                        foreach (var key in HttpContext.Request.Query.Keys)
                        {
                            if (rv.FC.Keys.Contains(key) == false)
                            {
                                rv.FC.Add(key, HttpContext.Request.Query[key]);
                            }
                        }
                    }
                    if (HttpContext.Request.HasFormContentType)
                    {
                        var f = HttpContext.Request.Form;
                        foreach (var key in f.Keys)
                        {
                            if (rv.FC.Keys.Contains(key) == false)
                            {
                                rv.FC.Add(key, f[key]);
                            }
                        }
                    }
                }
                catch { }
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
                    var tempids = new List<string>();
                    foreach (var iid in Ids)
                    {
                        tempids.Add(iid.ToString());
                    }
                    temp.Ids = tempids.ToArray();
                }
                if (temp.ListVM != null)
                {
                    temp.ListVM.CopyContext(rv);
                    temp.ListVM.Ids = Ids == null ? new List<string>() : temp.Ids.ToList();
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

        private void SetSubVm(BaseVM vm, bool passInit)
        {
            var sub = vm.GetType().GetAllProperties().Where(x => typeof(BaseVM).IsAssignableFrom(x.PropertyType) && x.Name != "ParentVM");
            foreach (var prop in sub)
            {
                var subins = prop.GetValue(vm) as BaseVM;
                bool exist = subins == null ? false : true;
                if (subins == null)
                {
                    subins = prop.PropertyType.GetConstructor(Type.EmptyTypes).Invoke(null) as BaseVM;
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
        public T CreateVM<T>(Expression<Func<T, object>> values = null, bool passInit = false) where T : BaseVM
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
        public T CreateVM<T>(object Id, Expression<Func<T, object>> values = null, bool passInit = false) where T : BaseVM
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
        public T CreateVM<T>(object[] Ids, Expression<Func<T, object>> values = null, bool passInit = false) where T : BaseVM
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
        public T CreateVM<T>(Guid[] Ids, Expression<Func<T, object>> values = null, bool passInit = false) where T : BaseVM
        {
            SetValuesParser p = new SetValuesParser();
            var dir = p.Parse(values);
            return CreateVM(typeof(T), null, Ids.Cast<object>().ToArray(), dir, passInit) as T;
        }

        /// <summary>
        /// Create a ViewModel, and pass Session,cache,dc...etc to the viewmodel
        /// </summary>
        /// <typeparam name="T">The type of the viewmodelThe type of the viewmodel</typeparam>
        /// <param name="Ids">If the viewmodel is a BatchVM, the BatchVM's Ids property will be assigned</param>
        /// <param name="values">use Lambda to set viewmodel's properties,use && for multiply properties, for example Wtm.CreateVM<Test>(values: x=>x.Field1=='a' && x.Field2 == 'b'); will set viewmodel's Field1 to 'a' and Field2 to 'b'</param>
        /// <param name="passInit">if true, the viewmodel will not call InitVM internally</param>
        /// <returns>ViewModel</returns>
        public T CreateVM<T>(int[] Ids, Expression<Func<T, object>> values = null, bool passInit = false) where T : BaseVM
        {
            SetValuesParser p = new SetValuesParser();
            var dir = p.Parse(values);
            return CreateVM(typeof(T), null, Ids.Cast<object>().ToArray(), dir, passInit) as T;
        }

        /// <summary>
        /// Create a ViewModel, and pass Session,cache,dc...etc to the viewmodel
        /// </summary>
        /// <typeparam name="T">The type of the viewmodelThe type of the viewmodel</typeparam>
        /// <param name="Ids">If the viewmodel is a BatchVM, the BatchVM's Ids property will be assigned</param>
        /// <param name="values">use Lambda to set viewmodel's properties,use && for multiply properties, for example Wtm.CreateVM<Test>(values: x=>x.Field1=='a' && x.Field2 == 'b'); will set viewmodel's Field1 to 'a' and Field2 to 'b'</param>
        /// <param name="passInit">if true, the viewmodel will not call InitVM internally</param>
        /// <returns>ViewModel</returns>
        public T CreateVM<T>(long[] Ids, Expression<Func<T, object>> values = null, bool passInit = false) where T : BaseVM
        {
            SetValuesParser p = new SetValuesParser();
            var dir = p.Parse(values);
            return CreateVM(typeof(T), null, Ids.Cast<object>().ToArray(), dir, passInit) as T;
        }
        /// <summary>
        /// Create a ViewModel, and pass Session,cache,dc...etc to the viewmodel
        /// </summary>
        /// <typeparam name="T">The type of the viewmodelThe type of the viewmodel</typeparam>
        /// <param name="Ids">If the viewmodel is a BatchVM, the BatchVM's Ids property will be assigned</param>
        /// <param name="values">use Lambda to set viewmodel's properties,use && for multiply properties, for example Wtm.CreateVM<Test>(values: x=>x.Field1=='a' && x.Field2 == 'b'); will set viewmodel's Field1 to 'a' and Field2 to 'b'</param>
        /// <param name="passInit">if true, the viewmodel will not call InitVM internally</param>
        /// <returns>ViewModel</returns>
        public T CreateVM<T>(string[] Ids, Expression<Func<T, object>> values = null, bool passInit = false) where T : BaseVM
        {
            SetValuesParser p = new SetValuesParser();
            var dir = p.Parse(values);
            return CreateVM(typeof(T), null, Ids.Cast<object>().ToArray(), dir, passInit) as T;
        }

        /// <summary>
        /// Create a ViewModel, and pass Session,cache,dc...etc to the viewmodel
        /// </summary>
        /// <param name="VmFullName">the fullname of the viewmodel's type</param>
        /// <param name="Id">If the viewmodel is a BaseCRUDVM, the data having this id will be fetched</param>
        /// <param name="Ids">If the viewmodel is a BatchVM, the BatchVM's Ids property will be assigned</param>
        /// <param name="passInit">if true, the viewmodel will not call InitVM internally</param>
        /// <returns>ViewModel</returns>
        public BaseVM CreateVM(string VmFullName, object Id = null, object[] Ids = null, bool passInit = false)
        {
            return CreateVM(Type.GetType(VmFullName), Id, Ids, null, passInit);
        }
        #endregion

        #region CallApi
        public async Task<ApiResult<T>> CallAPI<T>(string domainName, string url, HttpMethodEnum method, HttpContent content, int? timeout = null, string proxy = null, Dictionary<string, string> headers = null) where T : class
        {
            ApiResult<T> rv = new ApiResult<T>();
            try
            {
                var factory = this.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                if (string.IsNullOrEmpty(url))
                {
                    return rv;
                }
                //新建http请求
                HttpClient client = null;
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
                    client.DefaultRequestHeaders.Add("Authorization", "Bearer " + LoginUserInfo.RemoteToken);
                }

                //如果配置了代理，则使用代理
                //设置超时
                if (timeout.HasValue)
                {
                    client.Timeout = new TimeSpan(0, 0, 0, timeout.Value, 0);
                }
                //填充表单数据
                HttpResponseMessage res = null;
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
        public async Task<ApiResult<T>> CallAPI<T>(string domainName, string url, int? timeout = null, string proxy = null, Dictionary<string, string> headers = null) where T : class
        {
            HttpContent content = null;
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
        public async Task<ApiResult<T>> CallAPI<T>(string domainName, string url, HttpMethodEnum method, IDictionary<string, string> postdata, int? timeout = null, string proxy = null, Dictionary<string, string> headers = null) where T : class
        {
            HttpContent content = null;
            //填充表单数据
            if (!(postdata == null || postdata.Count == 0))
            {
                List<KeyValuePair<string, string>> paras = new List<KeyValuePair<string, string>>();
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
        public async Task<ApiResult<T>> CallAPI<T>(string domainName, string url, HttpMethodEnum method, object postdata, int? timeout = null, string proxy = null, Dictionary<string, string> headers = null) where T : class
        {
            HttpContent content = new StringContent(JsonSerializer.Serialize(postdata, CoreProgram.DefaultPostJsonOption), System.Text.Encoding.UTF8, "application/json");
            return await CallAPI<T>(domainName, url, method, content, timeout, proxy, headers);
        }

        public async Task<ApiResult<string>> CallAPI(string domainName, string url, HttpMethodEnum method, HttpContent content, int? timeout = null, string proxy = null, Dictionary<string, string> headers = null)
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
        public async Task<ApiResult<string>> CallAPI(string domainName, string url, int? timeout = null, string proxy = null, Dictionary<string, string> headers = null)
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
        public async Task<ApiResult<string>> CallAPI(string domainName, string url, HttpMethodEnum method, IDictionary<string, string> postdata, int? timeout = null, string proxy = null, Dictionary<string, string> headers = null)
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
        public async Task<ApiResult<string>> CallAPI(string domainName, string url, HttpMethodEnum method, object postdata, int? timeout = null, string proxy = null, Dictionary<string, string> headers = null)
        {
            return await CallAPI<string>(domainName, url, method, postdata, timeout, proxy, headers);
        }


        private string GetServerUrl()
        {
            var server = ConfigInfo.Domains.Where(x => x.Key.ToLower() == "serverpub").Select(x => x.Value).FirstOrDefault();
            if (server == null)
            {
                server = ConfigInfo.Domains.Where(x => x.Key.ToLower() == "server").Select(x => x.Value).FirstOrDefault();
            }
            if (server != null && string.IsNullOrEmpty(server.Address) == false)
            {
                return server.Address.TrimEnd('/');
            }
            else
            {
                return this.HttpContext.Request.Scheme + "://" + this.HttpContext.Request.Host.ToString();
            }
        }

        public void Dispose()
        {
            this._dc?.Dispose();
        }

        /// <summary>
        /// 调用 DeepSeek API 进行对话
        /// </summary>
        /// <param name="question">用户问题</param>
        /// <param name="systemMessage">系统提示词，定义 AI 的角色和行为</param>
        /// <returns>DeepSeek 回复内容</returns>
        public async Task<string> DeepSeek(string question, string systemMessage = null)
        {
            var cfg = ConfigInfo?.DeepSeek;
            if (cfg == null || string.IsNullOrEmpty(cfg.ApiKey))
                throw new Exception("DeepSeek is not configured. Set DeepSeek:ApiKey in appsettings.json.");

            using var client = new HttpClient();
            client.BaseAddress = new Uri(cfg.ApiUrl);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {cfg.ApiKey}");

            var messages = new List<object>();
            if (!string.IsNullOrEmpty(systemMessage))
            {
                messages.Add(new { role = "system", content = systemMessage });
            }
            messages.Add(new { role = "user", content = question });

            var body = new
            {
                model = cfg.Model,
                messages = messages,
                temperature = cfg.Temperature,
                max_tokens = cfg.MaxTokens
            };

            var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            var resp = await client.PostAsync("/chat/completions", content);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var reply = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return reply;
        }

        #endregion
    }

}
