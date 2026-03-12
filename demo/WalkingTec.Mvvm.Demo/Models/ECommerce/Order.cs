using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Demo.Models.ECommerce
{
    public enum OrderStatus
    {
        [Display(Name = "待處理")] Pending,
        [Display(Name = "已出貨")] Shipped,
        [Display(Name = "已完成")] Completed,
        [Display(Name = "已取消")] Cancelled,
        [Display(Name = "已退貨")] Returned
    }

    public enum PaymentMethod
    {
        [Display(Name = "信用卡")] CreditCard,
        [Display(Name = "ATM轉帳")] ATM,
        [Display(Name = "貨到付款")] COD,
        [Display(Name = "行動支付")] MobilePay
    }

    public class Order : BasePoco
    {
        [Display(Name = "訂單編號")]
        [Required]
        [StringLength(20)]
        public string OrderNo { get; set; } = null!;

        [Display(Name = "客戶")]
        [Required]
        public Guid CustomerId { get; set; }

        [Display(Name = "客戶")]
        public Customer? Customer { get; set; }

        [Display(Name = "訂單日期")]
        [Required]
        [Dimension(DisplayName = "訂單日期", Hierarchy = DateHierarchy.Month)]
        public DateTime OrderDate { get; set; }

        [Display(Name = "訂單狀態")]
        [Required]
        [Dimension(DisplayName = "訂單狀態")]
        public OrderStatus Status { get; set; }

        [Display(Name = "付款方式")]
        [Required]
        [Dimension(DisplayName = "付款方式")]
        public PaymentMethod PaymentMethod { get; set; }

        [Display(Name = "訂單金額")]
        [Required]
        public decimal TotalAmount { get; set; }

        [Display(Name = "明細")]
        public List<OrderItem>? Items { get; set; }
    }
}
