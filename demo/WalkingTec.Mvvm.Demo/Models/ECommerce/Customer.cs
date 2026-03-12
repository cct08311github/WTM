using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Demo.Models.ECommerce
{
    public enum Region
    {
        [Display(Name = "北部")] North,
        [Display(Name = "中部")] Central,
        [Display(Name = "南部")] South,
        [Display(Name = "東部")] East
    }

    public enum CustomerTier
    {
        [Display(Name = "一般")] Normal,
        [Display(Name = "VIP")] VIP,
        [Display(Name = "VVIP")] VVIP
    }

    public class Customer : BasePoco
    {
        [Display(Name = "客戶名稱")]
        [Required]
        [StringLength(50)]
        [Dimension(DisplayName = "客戶名稱")]
        public string Name { get; set; } = null!;

        [Display(Name = "地區")]
        [Required]
        [Dimension(DisplayName = "地區")]
        public Region Region { get; set; }

        [Display(Name = "客戶等級")]
        [Required]
        [Dimension(DisplayName = "客戶等級")]
        public CustomerTier Tier { get; set; }
    }
}
