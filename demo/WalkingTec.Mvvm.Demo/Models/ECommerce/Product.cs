using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Demo.Models.ECommerce
{
    public enum ProductCategory
    {
        [Display(Name = "電子產品")] Electronics,
        [Display(Name = "服飾")] Clothing,
        [Display(Name = "食品")] Food,
        [Display(Name = "家居")] Home,
        [Display(Name = "運動")] Sports,
        [Display(Name = "書籍")] Books
    }

    public class Product : BasePoco
    {
        [Display(Name = "商品名稱")]
        [Required]
        [StringLength(100)]
        [Dimension(DisplayName = "商品名稱")]
        public string Name { get; set; } = null!;

        [Display(Name = "品牌")]
        [StringLength(50)]
        [Dimension(DisplayName = "品牌")]
        public string Brand { get; set; } = null!;

        [Display(Name = "類別")]
        [Required]
        [Dimension(DisplayName = "商品類別")]
        public ProductCategory Category { get; set; }

        [Display(Name = "單價")]
        [Required]
        public decimal UnitPrice { get; set; }
    }
}
