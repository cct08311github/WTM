using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Demo.Models.ECommerce
{
    public class OrderItem : BasePoco
    {
        [Display(Name = "訂單")]
        [Required]
        public Guid OrderId { get; set; }

        [Display(Name = "訂單")]
        public Order? Order { get; set; }

        [Display(Name = "商品")]
        [Required]
        public Guid ProductId { get; set; }

        [Display(Name = "商品")]
        public Product? Product { get; set; }

        [Display(Name = "數量")]
        [Required]
        public int Quantity { get; set; }

        [Display(Name = "單價")]
        [Required]
        public decimal UnitPrice { get; set; }

        [Display(Name = "小計")]
        [Required]
        public decimal Subtotal { get; set; }
    }
}
