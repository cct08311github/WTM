using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            var dc = ResolveDataContext(scope.ServiceProvider);
            if (dc == null) return null;
            var dbSet = dc.Set<RefreshTokenEntity>();

            // ── Reuse-attack detection (read-only; no mutation yet) ──────────────
            // Load with AsNoTracking so this read does not conflict with the
            // ExecuteUpdateAsync claim below (which bypasses the change-tracker).
            var existing = await dbSet.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Token == refreshToken);

            if (existing == null)
                return null;

            if (existing.IsRevoked)
            {
                // A revoked token presented again is a reuse-attack signal.
                // If it was previously rotated (ReplacedByToken != null), revoke
                // the entire descendant chain to contain the potential breach.
                if (existing.ReplacedByToken != null)
                {
                    // Re-query with tracking so the descendant revocation can save.
                    var tracked = await dbSet.FirstOrDefaultAsync(x => x.Token == refreshToken);
                    if (tracked != null)
                    {
                        var revLogger = _sp.GetService<ILoggerFactory>()?.CreateLogger("TokenService");
                        await RevokeDescendantsAsync(dbSet, tracked, ipAddress,
                            "Attempted reuse of revoked token", _timeProvider, revLogger);
                        await dc.SaveChangesAsync();
                    }
                }
                return null;
            }

            // #676: route the expiry check through _timeProvider instead of the entity's own
            // RefreshTokenEntity.IsExpired (which reads DateTime.UtcNow directly) — this is an
            // advisory pre-check only (the authoritative enforcement is the ExecuteUpdateAsync
            // `x.ExpiresUtc > now` predicate below), but using the same clock for both avoids a
            // seeded FakeTimeProvider test disagreeing with itself between the two checks.
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (now >= existing.ExpiresUtc)
                return null;

            // ── Atomic claim via ExecuteUpdateAsync ───────────────────────────────
            // Only the first concurrent caller wins; any later caller that presents
            // the same token finds RevokedUtc already set and gets claimed == 0.
            var newTokenString = GenerateRefreshTokenString();

            var claimed = await dbSet
                .Where(x => x.Token == refreshToken
                             && x.RevokedUtc == null          // still active
                             && x.ExpiresUtc > now)           // not expired
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.RevokedUtc, now)
                    .SetProperty(x => x.RevokedByIp, ipAddress)
                    .SetProperty(x => x.ReplacedByToken, newTokenString)
                    .SetProperty(x => x.RevokeReason, "Rotated"));

            if (claimed == 0)
            {
                // Another concurrent request already rotated this token — or it
                // became inactive between our read and this update. Treat as failure.
                return null;
            }

            // ── Issue new token ───────────────────────────────────────────────────
            var newEntity = new RefreshTokenEntity
            {
                Token = newTokenString,
                ITCode = existing.ITCode,
                TenantCode = existing.TenantCode,
                // #676: explicit CreatedUtc stamp — overrides RefreshTokenEntity's own
                // `= DateTime.UtcNow` field initializer so this row's clock matches _timeProvider.
                CreatedUtc = now,
                ExpiresUtc = now.AddDays(RefreshTokenExpiryDays),
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
            var dc = ResolveDataContext(scope.ServiceProvider);
            if (dc == null) return;
            var existing = await dc.Set<RefreshTokenEntity>()
                .FirstOrDefaultAsync(x => x.Token == refreshToken);
            if (existing == null || !existing.IsActive) return;
            existing.RevokedUtc = _timeProvider.GetUtcNow().UtcDateTime;
            existing.RevokedByIp = ipAddress;
            existing.RevokeReason = reason ?? "Explicit revocation";
            await dc.SaveChangesAsync();

            // Immediately deny the current request's access token so it cannot be
            // reused after logout even before its natural expiry (Issue #126).
            DenyCurrentAccessToken();
        }

        /// <summary>
        /// Reads the <c>jti</c> and <c>exp</c> claims from the current HTTP request's
        /// access token and adds the JTI to the <see cref="IAccessTokenDenylist"/>.
        ///
        /// Silently skips when called outside an HTTP request context (e.g. background
        /// jobs), when there is no <c>jti</c> claim, or when the denylist service is
        /// unavailable — the refresh-token revocation above has already occurred.
        /// </summary>
        private void DenyCurrentAccessToken()
        {
            try
            {
                var httpContextAccessor = _sp.GetService<IHttpContextAccessor>();
                var httpContext = httpContextAccessor?.HttpContext;
                if (httpContext == null) return;

                var user = httpContext.User;
                var jti = user.FindFirstValue(JwtRegisteredClaimNames.Jti);
                if (string.IsNullOrEmpty(jti)) return;

                var denylist = httpContext.RequestServices.GetService<IAccessTokenDenylist>();
                if (denylist == null) return;

                // Compute expiry: prefer the token's own exp claim (Unix seconds);
                // fall back to now + configured access-token lifetime.
                DateTimeOffset expiresUtc;
                var expClaim = user.FindFirstValue(JwtRegisteredClaimNames.Exp);
                if (!string.IsNullOrEmpty(expClaim)
                    && long.TryParse(expClaim, out var expUnix))
                {
                    expiresUtc = DateTimeOffset.FromUnixTimeSeconds(expUnix);
                }
                else
                {
                    expiresUtc = _timeProvider.GetUtcNow().AddSeconds(_jwtOptions.Expires);
                }

                denylist.Deny(jti, expiresUtc);
            }
            catch (Exception ex)
            {
                // Failure to populate the denylist must never surface as an exception:
                // the refresh-token has already been revoked, which is the primary
                // security action. A failed denylist write is a best-effort degradation.
                _sp.GetService<ILoggerFactory>()?.CreateLogger("TokenService")
                    ?.LogWarning(ex, "Failed to add current access-token jti to the revocation denylist; the access token may remain valid until expiry.");
            }
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
            var dc = ResolveDataContext(scope.ServiceProvider);
            var createdUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var entity = new RefreshTokenEntity
            {
                Token = GenerateRefreshTokenString(),
                ITCode = itCode, TenantCode = tenantCode,
                // #676: explicit CreatedUtc stamp — overrides RefreshTokenEntity's own
                // `= DateTime.UtcNow` field initializer so this row's clock matches _timeProvider.
                CreatedUtc = createdUtc,
                ExpiresUtc = createdUtc.AddDays(RefreshTokenExpiryDays),
                CreatedByIp = ipAddress
            };
            if (dc != null)
            {
                await dc.Set<RefreshTokenEntity>().AddAsync(entity);
                await dc.SaveChangesAsync();
            }
            else
            {
                // #721 follow-up: if this ever fires it means the returned RefreshToken
                // string was never persisted, so a subsequent RefreshTokenAsync call for
                // it will always (correctly) reject as "not found" — fail-closed, not a
                // security hole, but silently non-functional for legitimate refreshes.
                _sp.GetService<ILoggerFactory>()?.CreateLogger("TokenService")
                    ?.LogWarning("Could not resolve a DataContext to persist the issued refresh token for {ITCode}; the refresh token will not be usable.", itCode);
            }
            return entity;
        }

        /// <summary>
        /// Resolves the app's real, connection-string/tenant-routed DataContext for
        /// refresh-token persistence.
        ///
        /// #721 follow-up: <c>IDataContext</c> is NOT usable via plain DI resolution here.
        /// <c>AddWtmContext</c> only registers
        /// <c>services.TryAddScoped&lt;IDataContext, NullContext&gt;()</c> as a safe
        /// placeholder default — apps obtain their real, connection-string-resolved
        /// DataContext through <see cref="WTMContext.DC"/> (built by
        /// <see cref="WTMContext.CreateDC"/>), never through generic DI. Before this fix,
        /// <c>scope.ServiceProvider.GetService&lt;IDataContext&gt;() as DbContext</c> always
        /// resolved <c>NullContext</c> (cast to <c>DbContext</c> =&gt; null) in every real
        /// deployment, silently making refresh-token persistence/validation a no-op over
        /// HTTP end-to-end — masked only because the unit-test fixtures
        /// (<c>TokenTestFixture</c>) explicitly re-register <c>IDataContext</c> to point at
        /// a real <c>DbContext</c>. Resolving a scoped <see cref="WTMContext"/> instead gives
        /// the same connection-string/tenant-aware <c>DbContext</c> the rest of the
        /// framework uses, isolated in <paramref name="scopedProvider"/>'s own DI scope.
        /// </summary>
        private static DbContext ResolveDataContext(IServiceProvider scopedProvider)
        {
            // Primary path (real deployments): WTMContext.DC is built by
            // WTMContext.CreateDC(), the framework's connection-string/tenant-aware
            // factory — this is what every other part of WTM actually uses.
            var wtm = scopedProvider.GetService<WTMContext>();
            var dc = wtm?.DC as DbContext;
            if (dc != null)
            {
                return dc;
            }

            // Fallback: hosts that explicitly re-register IDataContext against a real
            // DbContext in DI (e.g. test fixtures) without registering WTMContext itself.
            return scopedProvider.GetService<IDataContext>() as DbContext;
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
        /// <remarks>
        /// If the chain is deeper than <c>maxDepth</c>, a warning is logged via
        /// <paramref name="logger"/> so the truncation is observable in production;
        /// any active leaf token beyond that depth remains un-revoked in this edge case.
        /// </remarks>
        private static async Task RevokeDescendantsAsync(
            DbSet<RefreshTokenEntity> dbSet, RefreshTokenEntity token,
            string ipAddress, string reason, TimeProvider timeProvider,
            ILogger? logger = null)
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
            // Loop exhausted maxDepth without finding an active leaf to revoke.
            // Log a warning so the truncation is observable; an active descendant
            // beyond depth 50 may remain un-revoked in an attacker-crafted chain.
            logger?.LogWarning(
                "RevokeDescendantsAsync: revocation chain exceeds maxDepth ({MaxDepth}); " +
                "an active descendant token may remain un-revoked. " +
                "IpAddress: {IpAddress}, Reason: {Reason}",
                maxDepth, ipAddress, reason);
        }
    }
}
