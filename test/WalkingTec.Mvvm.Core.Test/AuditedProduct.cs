#nullable enable
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// 測試用 Model：加掛 [AuditChanges]，用於驗證 ChangeLog 審計功能。
    /// </summary>
    [AuditChanges]
    public class AuditedProduct : BasePoco
    {
        [Required]
        [StringLength(50)]
        public string Name { get; set; } = string.Empty;

        public decimal Price { get; set; }
    }
}
