#nullable enable
using System;
using Microsoft.Extensions.Caching.Memory;

namespace WalkingTec.Mvvm.Mvc.Auth
{
    /// <summary>
    /// <see cref="IMemoryCache"/>-backed implementation of <see cref="IAccessTokenDenylist"/>.
    ///
    /// Each denied JTI is stored as a cache key with an absolute expiration set to the
    /// token's own <c>exp</c> time, so entries are automatically evicted when the token
    /// would have expired anyway — keeping memory bounded without a background sweep.
    ///
    /// Registered as a singleton via <c>AddWtmAuthentication</c>.
    /// </summary>
    internal sealed class MemoryCacheAccessTokenDenylist : IAccessTokenDenylist
    {
        // Prefix to avoid accidental key collisions with other IMemoryCache users.
        private const string KeyPrefix = "wtm:jti-deny:";

        private readonly IMemoryCache _cache;

        public MemoryCacheAccessTokenDenylist(IMemoryCache cache)
        {
            _cache = cache;
        }

        /// <inheritdoc/>
        public void Deny(string jti, DateTimeOffset expiresUtc)
        {
            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = expiresUtc
            };
            // Value is a dummy byte; only the key's presence matters.
            _cache.Set(KeyPrefix + jti, (byte)1, options);
        }

        /// <inheritdoc/>
        public bool IsDenied(string jti) =>
            _cache.TryGetValue(KeyPrefix + jti, out _);
    }
}
