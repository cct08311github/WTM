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
        private const int RefreshTokenExpiryDays = 7;

        public TokenService(IOptionsMonitor<Configs> configs, IServiceProvider sp)
        {
            _jwtOptions = configs.CurrentValue.JwtOptions;
            _sp = sp;
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
                if (existing is { IsRevoked: true, ReplacedByToken: not null })
                {
                    await RevokeDescendantsAsync(dbSet, existing, ipAddress,
                        "Attempted reuse of revoked token");
                    await dc.SaveChangesAsync();
                }
                return null;
            }
            var newTokenString = GenerateRefreshTokenString();
            existing.RevokedUtc = DateTime.UtcNow;
            existing.RevokedByIp = ipAddress;
            existing.ReplacedByToken = newTokenString;
            existing.RevokeReason = "Rotated";
            var newEntity = new RefreshTokenEntity
            {
                Token = newTokenString,
                ITCode = existing.ITCode,
                TenantCode = existing.TenantCode,
                ExpiresUtc = DateTime.UtcNow.AddDays(RefreshTokenExpiryDays),
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
            existing.RevokedUtc = DateTime.UtcNow;
            existing.RevokedByIp = ipAddress;
            existing.RevokeReason = reason ?? "Explicit revocation";
            await dc.SaveChangesAsync();
        }

        private string GenerateAccessToken(LoginUserInfo info)
        {
            var creds = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.SecurityKey)),
                SecurityAlgorithms.HmacSha256);
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                new(AuthConstants.JwtClaimTypes.Subject, info.ITCode)
            };
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
                expires: DateTime.UtcNow.AddSeconds(_jwtOptions.Expires),
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
                ExpiresUtc = DateTime.UtcNow.AddDays(RefreshTokenExpiryDays),
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

        private static async Task RevokeDescendantsAsync(
            DbSet<RefreshTokenEntity> dbSet, RefreshTokenEntity token,
            string ipAddress, string reason)
        {
            if (string.IsNullOrEmpty(token.ReplacedByToken)) return;
            var child = await dbSet.FirstOrDefaultAsync(
                x => x.Token == token.ReplacedByToken);
            if (child == null) return;
            if (child.IsActive)
            {
                child.RevokedUtc = DateTime.UtcNow;
                child.RevokedByIp = ipAddress;
                child.RevokeReason = reason;
            }
            else
            {
                await RevokeDescendantsAsync(dbSet, child, ipAddress, reason);
            }
        }
    }
}
