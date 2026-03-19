#nullable enable
using System;
using System.ComponentModel.DataAnnotations;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 儲存查詢請求，包含查詢設定與顯示名稱。
    /// </summary>
    public class SaveQueryRequest
    {
        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        public bool IsPublic { get; set; } = false;

        [Required]
        public AnalysisQueryRequest Config { get; set; } = new();
    }

    /// <summary>
    /// 儲存查詢清單項目 DTO（不含 ConfigJson，節省傳輸量）。
    /// </summary>
    public class SavedQuerySummaryDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string ListVmType { get; set; } = string.Empty;
        public string OwnerCode { get; set; } = string.Empty;
        public bool IsPublic { get; set; }
        public bool IsOwner { get; set; }
        public DateTime? CreatedAt { get; set; }
    }
}
