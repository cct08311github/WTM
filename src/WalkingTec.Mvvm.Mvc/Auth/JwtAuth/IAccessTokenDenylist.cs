#nullable enable
using System;

namespace WalkingTec.Mvvm.Mvc.Auth
{
    /// <summary>
    /// Tracks access-token JTI (JWT ID) values that have been explicitly revoked
    /// before their natural expiry time (e.g. on logout).
    ///
    /// Entries are stored with an absolute cache expiration equal to the token's own
    /// <c>exp</c> claim, so they auto-evict when the token would have expired anyway —
    /// keeping memory consumption bounded without a background sweep.
    ///
    /// <para>
    /// <b>Single-node note:</b> the default implementation uses
    /// <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/> and is therefore
    /// local to one process. Multi-node deployments (load-balanced, container farms)
    /// should replace this service with a distributed backing store (e.g. Redis via
    /// <c>IDistributedCache</c>) to ensure revocations are honoured across all nodes.
    /// </para>
    /// </summary>
    public interface IAccessTokenDenylist
    {
        /// <summary>
        /// Adds the given <paramref name="jti"/> to the denylist.
        /// The entry will be retained until <paramref name="expiresUtc"/>, after which
        /// it is evicted automatically (the token could not be used anyway once expired).
        /// </summary>
        /// <param name="jti">The JWT ID claim value from the access token.</param>
        /// <param name="expiresUtc">
        /// The UTC instant at which the access token expires.
        /// Cache entries are evicted at this time to bound memory growth.
        /// </param>
        void Deny(string jti, DateTimeOffset expiresUtc);

        /// <summary>
        /// Returns <c>true</c> if the given <paramref name="jti"/> is currently in the
        /// denylist (i.e. the token has been explicitly revoked and has not yet expired).
        /// </summary>
        bool IsDenied(string jti);
    }
}
