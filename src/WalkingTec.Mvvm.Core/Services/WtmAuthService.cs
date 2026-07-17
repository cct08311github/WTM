#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core.Services
{
    /// <summary>
    /// Default implementation of <see cref="IWtmAuthService"/>.
    /// Logic extracted from WTMContext.DoLoginAsync and RefreshTokenAsync.
    /// </summary>
    public class WtmAuthService : IWtmAuthService
    {
        public PasswordVerifyResult VerifyPassword(string? storedHash, string? providedPassword)
        {
            return PasswordHashHelper.VerifyPassword(storedHash, providedPassword);
        }

        public string HashPassword(string? password)
        {
            return PasswordHashHelper.HashPassword(password);
        }

        public async Task<LoginUserInfo?> AuthenticateViaRemoteHostAsync(
            IWtmApiClient apiClient,
            string? remoteToken,
            string? username,
            string? password)
        {
            LoginUserInfo? rv = null;

            if (!string.IsNullOrEmpty(remoteToken))
            {
                var headers = new Dictionary<string, string>
                {
                    { "Authorization", "Bearer " + remoteToken }
                };
                var user = await apiClient.CallAPI<LoginUserInfo>(
                    "mainhost", "/api/_account/checkuserinfo?IsApi=false",
                    HttpMethodEnum.GET, (object?)new { }, timeout: 10, headers: headers);
                rv = user.Data;
                if (rv != null)
                {
                    rv.RemoteToken = remoteToken;
                }
            }
            else if (!string.IsNullOrEmpty(password))
            {
                var loginjwt = await apiClient.CallAPI<Token>(
                    "mainhost", "/api/_account/loginjwt",
                    HttpMethodEnum.POST,
                    new { Account = username, Password = password },
                    timeout: 10);
                if (!string.IsNullOrEmpty(loginjwt?.Data?.AccessToken))
                {
                    remoteToken = loginjwt.Data.AccessToken;
                    var headers = new Dictionary<string, string>
                    {
                        { "Authorization", "Bearer " + remoteToken }
                    };
                    var user = await apiClient.CallAPI<LoginUserInfo>(
                        "mainhost", "/api/_account/checkuserinfo?IsApi=false",
                        HttpMethodEnum.GET, (object?)new { }, timeout: 10, headers: headers);
                    rv = user.Data;
                    if (rv != null)
                    {
                        rv.RemoteToken = remoteToken;
                    }
                }
            }

            return rv;
        }

        [System.Obsolete("Insecure: performed identity-based reissue that ignored the presented refresh token (#721 auth bypass). Always rejects now. Use RefreshTokenAsync(string, LoginUserInfo, ITokenService, IWtmApiClient, bool).")]
        public Task<Token?> RefreshTokenAsync(
            LoginUserInfo? loginUser,
            ITokenService tokenService,
            IWtmApiClient? apiClient,
            bool hasMainHost)
        {
            return Task.FromResult<Token?>(null);
        }

        public async Task<Token?> RefreshTokenAsync(
            string? refreshToken,
            LoginUserInfo? loginUser,
            ITokenService tokenService,
            IWtmApiClient? apiClient,
            bool hasMainHost)
        {
            if (string.IsNullOrEmpty(refreshToken))
            {
                return null;
            }

            if (hasMainHost && loginUser?.CurrentTenant == null)
            {
                // Federation frontend: this host has no RefreshTokenEntity DB of its own,
                // so it cannot validate the token locally. Forward the ACTUAL presented
                // token to the mainhost's hardened endpoint — never an empty body — and
                // let the mainhost validate/rotate it (#721). Route to the framework's
                // canonical _FrameworkController.RefreshToken endpoint (lowercase path),
                // not the old shadowed/removed demo AccountController action.
                if (apiClient == null)
                {
                    return null;
                }

                var r = await apiClient.CallAPI<Token>(
                    "mainhost", "/api/_account/refreshtoken",
                    HttpMethodEnum.POST, new { RefreshToken = refreshToken });
                return r?.Data;
            }

            return await tokenService.RefreshTokenAsync(refreshToken);
        }
    }
}
