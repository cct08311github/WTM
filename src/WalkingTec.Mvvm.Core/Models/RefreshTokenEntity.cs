using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WalkingTec.Mvvm.Core
{
    [Table("FrameworkRefreshTokens")]
    public class RefreshTokenEntity
    {
        [Key]
        public Guid ID { get; set; } = Guid.NewGuid();

        [Required, StringLength(256)]
        public string Token { get; set; }

        [Required, StringLength(50)]
        public string ITCode { get; set; }

        [StringLength(50)]
        public string TenantCode { get; set; }

        public DateTime ExpiresUtc { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        [StringLength(50)]
        public string CreatedByIp { get; set; }

        public DateTime? RevokedUtc { get; set; }

        [StringLength(50)]
        public string RevokedByIp { get; set; }

        [StringLength(256)]
        public string ReplacedByToken { get; set; }

        [StringLength(100)]
        public string RevokeReason { get; set; }

        [NotMapped]
        public bool IsExpired => DateTime.UtcNow >= ExpiresUtc;
        [NotMapped]
        public bool IsRevoked => RevokedUtc != null;
        [NotMapped]
        public bool IsActive => !IsRevoked && !IsExpired;
    }
}
