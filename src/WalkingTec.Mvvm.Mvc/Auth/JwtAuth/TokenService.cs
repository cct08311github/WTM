using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Mvc.Auth
{
    public class TokenService : ITokenService
    {
        private readonly JwtOption _jwtOptions;
        private readonly IServiceProvider _sp;
        private readonly TimeProvider _timeProvider;
        private const int RefreshTokenExpiryDays = 7;

        public TokenService(IOptionsMonitor<Configs> configs, IServiceProvider sp, TimeProvider? timeProvider = null)
        {
            _jwtOptions = configs.CurrentValue.JwtOptions;
            _sp = sp;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        public async Task<Token> IssueTokenAsync(
            LoginUserInfo loginUserInfo, string ipAddress = null)
        {
            if (loginUserInfo == null)
                throw new ArgumentNullException(nameof(loginUserInfo));
            if (string.IsNullOrEmpty(loginUserInfo.ITCode))
                throw new ArgumentException("ITCode cannot be null or empty.", nameof(loginUserInfo.ITCode));
            var accessToken = GenerateAccessToken(loginUserInfo);
            var refreshEntity = await CreateRefreshTokenAsync(
                loginUserInfo.ITCode, loginUserInfo.TenantCode, ipAddress);
            return new Token
            {
                AccessToken = accessToken,
                ExpiresIn = _jwtOptions.Expires,
                TokenType = AuthConstants.JwtTokenType,
                RefreshToken = refreshEntity.Token
            };
        }

        public async Task<Token> RefreshTokenAsync(
            string refreshToken, string ipAddress = null)
        {
            if (string.IsNullOrEmpty(refreshToken)) return null;
            using var scope = _sp.CreateScope();
            var dc = scope.ServiceProvider.GetService<IDataContext>() as DbContext;
            if (dc == null) return null;
            var dbSet = dc.Set<RefreshTokenEntity>();
            var existing = await dbSet.FirstOrDefaultAsync(x => x.Token == refreshToken);
            if (existing == null || !existing.IsActive)
            {
                // Token-reuse attack detection: a revoked token that was already replaced
                // should never be presented again. If it is, an attacker may have stolen
                // the old token. Revoke the entire descendant chain to contain the breach.
                if (existing is { IsRevoked: true, ReplacedByToken: not null })
                {
                    await RevokeDescendantsAsync(dbSet, existing, ipAddress,
                        "Attempted reuse of revoked token", _timeProvider);
                    await dc.SaveChangesAsync();
                }
                return null;
            }
            var newTokenString = GenerateRefreshTokenString();
            existing.RevokedUtc = _timeProvider.GetUtcNow().UtcDateTime;
            existing.RevokedByIp = ipAddress;
            existing.ReplacedByToken = newTokenString;
            existing.RevokeReason = "Rotated";
            var newEntity = new RefreshTokenEntity
            {
                Token = newTokenString,
                ITCode = existing.ITCode,
                TenantCode = existing.TenantCode,
                ExpiresUtc = _timeProvider.GetUtcNow().UtcDateTime.AddDays(RefreshTokenExpiryDays),
                CreatedByIp = ipAddress
            };
            await dbSet.AddAsync(newEntity);
            await dc.SaveChangesAsync();
            var userInfo = new LoginUserInfo
            { ITCode = existing.ITCode, TenantCode = existing.TenantCode };
            return new Token
            {
                AccessToken = GenerateAccessToken(userInfo),
                ExpiresIn = _jwtOptions.Expires,
                TokenType = AuthConstants.JwtTokenType,
                RefreshToken = newTokenString
            };
        }

        public async Task RevokeTokenAsync(
            string refreshToken, string ipAddress = null, string reason = null)
        {
            if (string.IsNullOrEmpty(refreshToken)) return;
            using var scope = _sp.CreateScope();
            var dc = scope.ServiceProvider.GetService<IDataContext>() as DbContext;
            if (dc == null) return;
            var existing = await dc.Set<RefreshTokenEntity>()
                .FirstOrDefaultAsync(x => x.Token == refreshToken);
            if (existing == null || !existing.IsActive) return;
            existing.RevokedUtc = _timeProvider.GetUtcNow().UtcDateTime;
            existing.RevokedByIp = ipAddress;
            existing.RevokeReason = reason ?? "Explicit revocation";
            await dc.SaveChangesAsync();
        }

        private string GenerateAccessToken(LoginUserInfo info)
        {
            var creds = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.SecurityKey)),
                SecurityAlgorithms.HmacSha256);
            List<Claim> claims = [new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")), new(AuthConstants.JwtClaimTypes.Subject, info.ITCode)];
            if (!string.IsNullOrEmpty(info.Name))
                claims.Add(new Claim(AuthConstants.JwtClaimTypes.Name, info.Name));
            if (!string.IsNullOrEmpty(info.TenantCode))
                claims.Add(new Claim(AuthConstants.JwtClaimTypes.TenantCode, info.TenantCode));
            if (!string.IsNullOrEmpty(info.RemoteToken))
                claims.Add(new Claim(AuthConstants.JwtClaimTypes.RToken, info.RemoteToken));
            var jwt = new JwtSecurityToken(
                issuer: _jwtOptions.Issuer,
                audience: _jwtOptions.Audience,
                claims: claims,
                expires: _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(_jwtOptions.Expires),
                signingCredentials: creds);
            return new JwtSecurityTokenHandler().WriteToken(jwt);
        }

        private async Task<RefreshTokenEntity> CreateRefreshTokenAsync(
            string itCode, string tenantCode, string ipAddress)
        {
            using var scope = _sp.CreateScope();
            var dc = scope.ServiceProvider.GetService<IDataContext>() as DbContext;
            var entity = new RefreshTokenEntity
            {
                Token = GenerateRefreshTokenString(),
                ITCode = itCode, TenantCode = tenantCode,
                ExpiresUtc = _timeProvider.GetUtcNow().UtcDateTime.AddDays(RefreshTokenExpiryDays),
                CreatedByIp = ipAddress
            };
            if (dc != null)
            {
                await dc.Set<RefreshTokenEntity>().AddAsync(entity);
                await dc.SaveChangesAsync();
            }
            return entity;
        }

        private static string GenerateRefreshTokenString()
        {
            var bytes = RandomNumberGenerator.GetBytes(64);
            return Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// Walks the refresh-token replacement chain from a compromised token and
        /// revokes any still-active descendant. Iterates up to <c>maxDepth</c> hops
        /// to avoid unbounded recursion on attacker-crafted chains.
        /// </summary>
        private static async Task RevokeDescendantsAsync(
            DbSet<RefreshTokenEntity> dbSet, RefreshTokenEntity token,
            string ipAddress, string reason, TimeProvider timeProvider)
        {
            const int maxDepth = 50;
            var current = token;
            for (int i = 0; i < maxDepth; i++)
            {
                if (string.IsNullOrEmpty(current.ReplacedByToken)) return;
                var child = await dbSet.FirstOrDefaultAsync(
                    x => x.Token == current.ReplacedByToken);
                if (child == null) return;
                if (child.IsActive)  // Revoke the active descendant and stop.
                {
                    child.RevokedUtc = timeProvider.GetUtcNow().UtcDateTime;
                    child.RevokedByIp = ipAddress;
                    child.RevokeReason = reason;
                    return;
                }
                current = child; // Already revoked — iterate deeper.
            }
        }
    }
}
