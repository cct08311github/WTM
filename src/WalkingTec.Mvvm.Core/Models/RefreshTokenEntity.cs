using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// A rotating refresh token issued by <c>TokenService.IssueTokenAsync</c>/<c>RefreshTokenAsync</c>
    /// and swept by the opt-in <c>RefreshTokenRetentionService</c> (#757).
    /// </summary>
    /// <remarks>
    /// Issue #761: <c>FrameworkRefreshTokens</c> is an unbounded-growth table with three hot
    /// query paths that were previously full table scans:
    /// <list type="bullet">
    /// <item><description><c>TokenService.RefreshTokenAsync</c> looks up by <see cref="Token"/> on
    /// every refresh request — <c>IX_FrameworkRefreshTokens_Token</c>. Intentionally
    /// <b>non-unique</b>: a unique index risks a failed index build against any pre-existing
    /// duplicate data on upgrade, and the lookup only needs an index to serve it, not a
    /// uniqueness constraint (compatibility-first per the framework's Never-silently-break rule).</description></item>
    /// <item><description><c>RefreshTokenRetentionService</c>'s expired-token sweep filters/orders by
    /// <see cref="ExpiresUtc"/> — <c>IX_FrameworkRefreshTokens_ExpiresUtc</c>.</description></item>
    /// <item><description><c>RefreshTokenRetentionService</c>'s revoked-token sweep predicate is
    /// <c>RevokedUtc != null &amp;&amp; RevokedUtc &lt; cutoff &amp;&amp; ExpiresUtc &lt; now</c> —
    /// <c>IX_FrameworkRefreshTokens_RevokedUtc_ExpiresUtc</c> is a composite covering both
    /// predicate columns.</description></item>
    /// </list>
    /// <para>
    /// <b>Existing databases:</b> <c>Database.EnsureCreatedAsync()</c> (used by
    /// <c>FrameworkContext.DataInit</c>) only creates indexes on a brand-new schema — it does
    /// <b>not</b> retrofit indexes onto an already-existing <c>FrameworkRefreshTokens</c> table.
    /// Operators upgrading an existing production database must add these three indexes manually
    /// (<c>CREATE INDEX</c>); migration guidance for that is shipped with the release notes, not
    /// this source change.
    /// </para>
    /// </remarks>
    [Table("FrameworkRefreshTokens")]
    [Index(nameof(Token), Name = "IX_FrameworkRefreshTokens_Token")]
    [Index(nameof(ExpiresUtc), Name = "IX_FrameworkRefreshTokens_ExpiresUtc")]
    [Index(nameof(RevokedUtc), nameof(ExpiresUtc), Name = "IX_FrameworkRefreshTokens_RevokedUtc_ExpiresUtc")]
    public class RefreshTokenEntity
    {
        [Key]
        public Guid ID { get; set; } = Guid.NewGuid();

        [Required, StringLength(256)]
        public string Token { get; set; } = null!;

        [Required, StringLength(50)]
        public string ITCode { get; set; } = null!;

        [StringLength(50)]
        public string? TenantCode { get; set; }

        public DateTime ExpiresUtc { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        [StringLength(50)]
        public string? CreatedByIp { get; set; }

        public DateTime? RevokedUtc { get; set; }

        [StringLength(50)]
        public string? RevokedByIp { get; set; }

        [StringLength(256)]
        public string? ReplacedByToken { get; set; }

        [StringLength(100)]
        public string? RevokeReason { get; set; }

        [NotMapped]
        public bool IsExpired => DateTime.UtcNow >= ExpiresUtc;
        [NotMapped]
        public bool IsRevoked => RevokedUtc != null;
        [NotMapped]
        public bool IsActive => !IsRevoked && !IsExpired;
    }
}
