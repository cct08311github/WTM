#nullable enable
using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core
{
    public partial class WTMContext
    {
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
                            WtmDiagnosticLogger?.LogWarning(ex, "Failed to reload user info for usercode '{UserCode}'", usercode);
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

                        // Issue #923: this is an independent _remotetoken validation path in
                        // WalkingTec.Mvvm.Core, decoupled from the Mvc-layer startup guard in
                        // FrameworkServiceExtension.AddWtmAuthentication (Core cannot assume a
                        // host even calls that method). Validating a token's signature against
                        // a key that is itself publicly known or too short — see
                        // JwtOption.IsWeakSigningKey — proves nothing: anyone could have forged
                        // a signature that verifies against that same key, so "signature
                        // verified" carries no trust here. Fail closed rather than proceed to
                        // ValidateToken with a key that cannot distinguish a forged token from
                        // a real one.
                        if (jwtOpts.IsWeakSigningKey(out var weakKeyReason))
                        {
                            WtmDiagnosticLogger?.LogWarning(
                                "_remotetoken rejected: JwtOptions.SecurityKey is not usable ({Reason}) " +
                                "so no remote token signature can be trusted.", weakKeyReason);
                            return null!;
                        }

                        var handler = new JwtSecurityTokenHandler();
                        var validationParams = new TokenValidationParameters
                        {
                            ValidateIssuerSigningKey = true,
                            // #931 item 1: read EffectiveSecurityKey (the padded HMAC material), never
                            // SecurityKey directly — SecurityKey is the raw, unpadded, round-trippable
                            // value now; using it here for a key under 32 bytes would throw IDX10720.
                            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOpts.EffectiveSecurityKey)),
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
                            WtmDiagnosticLogger?.LogWarning(ex, "JWT token validation failed unexpectedly (non-signature error)");
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
                            WtmDiagnosticLogger?.LogWarning(ex, "Failed to reload user info via remote token");
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
        /// <remarks>
        /// Steering note: this synchronous method blocks a ThreadPool thread via
        /// <c>DoLoginAsync(...).GetAwaiter().GetResult()</c>. Prefer <see cref="ReloadUserAsync"/>
        /// from any async call site, or pre-resolve via <see cref="EnsureLoginUserInfoAsync"/>
        /// from middleware. The three call sites inside the synchronous <see cref="LoginUserInfo"/>
        /// property getter are an intentional exception — a property getter cannot <c>await</c>,
        /// so routing them through <see cref="ReloadUserAsync"/> would still require
        /// <c>GetAwaiter().GetResult()</c> with no behavioural benefit over calling this method
        /// directly. <see cref="EnsureLoginUserInfoAsync"/> is the async alternative that avoids
        /// the getter's blocking path entirely when called ahead of time from middleware.
        /// </remarks>
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
                        WtmDiagnosticLogger
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

                    // Issue #923: mirrors the guard in the sync LoginUserInfo getter's
                    // Remote-token branch above, exactly — see that copy's comment for the
                    // full rationale. Both paths must stay identical: a signature that
                    // verifies against a publicly known or too-short key proves nothing,
                    // because anyone could have forged one that also verifies.
                    if (jwtOpts.IsWeakSigningKey(out var weakKeyReason))
                    {
                        WtmDiagnosticLogger?.LogWarning(
                            "_remotetoken rejected: JwtOptions.SecurityKey is not usable ({Reason}) " +
                            "so no remote token signature can be trusted.", weakKeyReason);
                        return;
                    }

                    var handler = new JwtSecurityTokenHandler();
                    var validationParams = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        // #931 item 1: EffectiveSecurityKey (padded HMAC material), not SecurityKey
                        // (now the raw, unpadded, round-trippable value) — see the sync getter's
                        // identical comment above.
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOpts.EffectiveSecurityKey)),
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
                        WtmDiagnosticLogger?.LogWarning(ex, "EnsureLoginUserInfoAsync: JWT token validation failed unexpectedly (non-signature error)");
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
                        WtmDiagnosticLogger?.LogWarning(ex, "EnsureLoginUserInfoAsync: failed to reload user info via remote token");
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
    }
}
