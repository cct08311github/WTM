using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Demo.Models.ECommerce;

namespace WalkingTec.Mvvm.Demo.ViewModels.ECommerceVMs
{
    [EnableAnalysis]
    public class OrderItemListVM : BasePagedListVM<OrderItem_View, OrderItemSearcher>
    {
        protected override List<GridAction> InitGridAction()
        {
            return new List<GridAction>
            {
                this.MakeStandardAction("OrderItem", GridActionStandardTypesEnum.ExportExcel, Localizer["Sys.Export"], "ECommerce"),
            };
        }

        protected override IEnumerable<IGridColumn<OrderItem_View>> InitGridHeader()
        {
            return new List<GridColumn<OrderItem_View>>
            {
                this.MakeGridHeader(x => x.OrderNo),
                this.MakeGridHeader(x => x.ProductName),
                this.MakeGridHeader(x => x.ProductCategory),
                this.MakeGridHeader(x => x.Brand),
                this.MakeGridHeader(x => x.CustomerRegion),
                this.MakeGridHeader(x => x.OrderDate),
                this.MakeGridHeader(x => x.Quantity),
                this.MakeGridHeader(x => x.UnitPrice),
                this.MakeGridHeader(x => x.Subtotal),
            };
        }

        public override IOrderedQueryable<OrderItem_View> GetSearchQuery()
        {
            var query = DC.Set<OrderItem>()
                .Include(x => x.Order).ThenInclude(o => o!.Customer)
                .Include(x => x.Product)
                .CheckEqual(Searcher.Category, x => x.Product!.Category)
                .Select(x => new OrderItem_View
                {
                    ID = x.ID,
                    OrderNo = x.Order!.OrderNo,
                    OrderDate = x.Order.OrderDate,
                    CustomerRegion = x.Order.Customer!.Region,
                    CustomerTier = x.Order.Customer.Tier,
                    OrderStatus = x.Order.Status,
                    PaymentMethod = x.Order.PaymentMethod,
                    ProductName = x.Product!.Name,
                    ProductCategory = x.Product.Category,
                    Brand = x.Product.Brand,
                    Quantity = x.Quantity,
                    UnitPrice = x.UnitPrice,
                    Subtotal = x.Subtotal,
                    ItemCount = 1,
                })
                .OrderByDescending(x => x.OrderDate);
            return query;
        }
    }

    public class OrderItem_View : OrderItem
    {
        [Display(Name = "訂單編號")]
        [Dimension(DisplayName = "訂單編號")]
        public string OrderNo { get; set; } = null!;

        [Display(Name = "訂單日期")]
        [Dimension(DisplayName = "訂單日期", Hierarchy = DateHierarchy.Month)]
        public System.DateTime OrderDate { get; set; }

        [Display(Name = "地區")]
        [Dimension(DisplayName = "地區")]
        public Region CustomerRegion { get; set; }

        [Display(Name = "客戶等級")]
        [Dimension(DisplayName = "客戶等級")]
        public CustomerTier CustomerTier { get; set; }

        [Display(Name = "訂單狀態")]
        [Dimension(DisplayName = "訂單狀態")]
        public OrderStatus OrderStatus { get; set; }

        [Display(Name = "付款方式")]
        [Dimension(DisplayName = "付款方式")]
        public PaymentMethod PaymentMethod { get; set; }

        [Display(Name = "商品名稱")]
        [Dimension(DisplayName = "商品名稱")]
        public string ProductName { get; set; } = null!;

        [Display(Name = "商品類別")]
        [Dimension(DisplayName = "商品類別")]
        public ProductCategory ProductCategory { get; set; }

        [Display(Name = "品牌")]
        [Dimension(DisplayName = "品牌")]
        public string Brand { get; set; } = null!;

        [Display(Name = "銷售數量")]
        [Measure(DisplayName = "銷售數量", AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Avg | AggregateFunc.Count)]
        public new int Quantity { get; set; }

        [Display(Name = "銷售金額")]
        [Measure(DisplayName = "銷售金額", AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Avg | AggregateFunc.Max | AggregateFunc.Min)]
        public new decimal Subtotal { get; set; }

        [Display(Name = "項目數")]
        [Measure(DisplayName = "項目數", AllowedFuncs = AggregateFunc.Count | AggregateFunc.Sum)]
        public int ItemCount { get; set; }
    }

    public class OrderItemSearcher : BaseSearcher
    {
        [Display(Name = "商品類別")]
        public ProductCategory? Category { get; set; }
    }
}
