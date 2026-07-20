#nullable enable
namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Options for <see cref="RefreshTokenRetentionService"/>.
    /// Introduced by issue #757 to bound unbounded growth of the
    /// <c>FrameworkRefreshTokens</c> table: after #721 every login
    /// <c>INSERT</c>s a row and every refresh rotation inserts another
    /// (the old row is only revoked, never deleted), so growth is
    /// monotonic with no purge anywhere in the framework.
    /// </summary>
    public class RefreshTokenRetentionOptions
    {
        /// <summary>
        /// Master switch. Default <c>true</c> — the service runs. Set to
        /// <c>false</c> for apps that want infinite retention (regulated
        /// domains that manage retention via external archival pipeline).
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Local-time hour of day (0–23) when the daily retention sweep
        /// runs. Default 4 (04:00 local) — deliberately staggered one
        /// hour after <see cref="ActionLogRetentionOptions.RunAtLocalHour"/>'s
        /// default of 3, so the two independent sweeps (which mirror each
        /// other's implementation but never share code) don't contend for
        /// the same window on hosts that enable both. Runs exactly once
        /// per calendar day.
        /// <para>
        /// Not validated at bind time. Values outside 0-23 (including a
        /// live appsettings reload picked up mid-run via
        /// <c>IOptionsMonitor</c>) are clamped to the nearest valid hour by
        /// <see cref="RefreshTokenRetentionService.ComputeNextRun"/> and
        /// logged as a warning — the sweep still runs on a schedule, just
        /// not the (invalid) one configured.
        /// </para>
        /// </summary>
        public int RunAtLocalHour { get; set; } = 4;

        /// <summary>
        /// Retention in days, measured from <see cref="RefreshTokenEntity.ExpiresUtc"/>,
        /// for rows whose token has expired (whether or not it was ever
        /// revoked). Default 30. Rows with
        /// <c>ExpiresUtc &lt; nowUtc - ExpiredDays</c> are purged. Set to
        /// <c>0</c> or negative to disable this sweep (keep expired rows
        /// forever).
        /// </summary>
        public int ExpiredDays { get; set; } = 30;

        /// <summary>
        /// Retention in days, measured from <see cref="RefreshTokenEntity.RevokedUtc"/>,
        /// for rows that were explicitly revoked (rotation or reuse-attack
        /// containment). Default 30. Rows with
        /// <c>RevokedUtc != null &amp;&amp; RevokedUtc &lt; nowUtc - RevokedDays</c>
        /// are purge candidates — but see
        /// <see cref="RefreshTokenRetentionService"/>'s remarks: a revoked
        /// row is only ever actually deleted once it has ALSO expired,
        /// regardless of this setting. Set to <c>0</c> or negative to
        /// disable this sweep (keep revoked rows forever).
        /// </summary>
        public int RevokedDays { get; set; } = 30;

        /// <summary>
        /// Max rows deleted per SQL statement. The service loops
        /// <c>ExecuteDeleteAsync</c> in chunks of this size until no
        /// more rows match. Prevents the lock contention / transaction
        /// log bloat that a single massive <c>DELETE</c> would cause.
        /// Default 5000. Values <c>&lt;= 0</c> are coerced to 5000.
        /// </summary>
        public int BatchSize { get; set; } = 5000;
    }
}
