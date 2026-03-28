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

        public async Task<Token?> RefreshTokenAsync(
            LoginUserInfo? loginUser,
            ITokenService tokenService,
            IWtmApiClient? apiClient,
            bool hasMainHost)
        {
            if (loginUser == null)
            {
                return null;
            }

            string? rt = null;
            if (hasMainHost && loginUser.CurrentTenant == null)
            {
                if (apiClient != null)
                {
                    var r = await apiClient.CallAPI<Token>(
                        "mainhost", "/api/_account/RefreshToken",
                        HttpMethodEnum.POST, (object?)new { });
                    rt = r?.Data?.AccessToken;
                }
            }
            else
            {
                rt = loginUser.RemoteToken;
            }

            var rv = await tokenService.IssueTokenAsync(new LoginUserInfo
            {
                ITCode = loginUser.ITCode,
                TenantCode = loginUser.TenantCode,
                RemoteToken = rt
            });
            return rv;
        }
    }
}
