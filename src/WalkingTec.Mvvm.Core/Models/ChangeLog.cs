#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// 記錄 BaseCRUDVM DoAdd / DoEdit / DoDelete 操作的欄位變更快照。
    /// 僅對標注了 <see cref="AuditChangesAttribute"/> 的 Model 類別生效（opt-in）。
    /// OldValues / NewValues 以 JSON 格式儲存變更前後的欄位鍵值對（#569）。
    /// </summary>
    [Table("ChangeLogs")]
    public class ChangeLog : BasePoco
    {
        /// <summary>操作類型：Add / Edit / Delete</summary>
        [Required]
        [StringLength(10)]
        public string Action { get; set; } = string.Empty;

        /// <summary>被操作的 Model 類別全名（FullName）</summary>
        [Required]
        [StringLength(255)]
        public string EntityType { get; set; } = string.Empty;

        /// <summary>被操作的實體主鍵（以 ToString() 轉字串）</summary>
        [StringLength(255)]
        public string? EntityId { get; set; }

        /// <summary>執行操作的使用者帳號</summary>
        [StringLength(50)]
        public string? ChangedBy { get; set; }

        /// <summary>操作時間（UTC）</summary>
        public DateTime ChangedAt { get; set; } = DateTime.UtcNow;

        /// <summary>變更前的欄位鍵值對（JSON），DoAdd 時為 null</summary>
        public string? OldValues { get; set; }

        /// <summary>變更後的欄位鍵值對（JSON），DoDelete 時為 null</summary>
        public string? NewValues { get; set; }
    }
}
