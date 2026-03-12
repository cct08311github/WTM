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
    public class OrderListVM : BasePagedListVM<Order_View, OrderSearcher>
    {
        protected override List<GridAction> InitGridAction()
        {
            return new List<GridAction>
            {
                this.MakeStandardAction("Order", GridActionStandardTypesEnum.Details, Localizer["Sys.Details"], "ECommerce", dialogWidth: 800),
                this.MakeStandardAction("Order", GridActionStandardTypesEnum.ExportExcel, Localizer["Sys.Export"], "ECommerce"),
            };
        }

        protected override IEnumerable<IGridColumn<Order_View>> InitGridHeader()
        {
            return new List<GridColumn<Order_View>>
            {
                this.MakeGridHeader(x => x.OrderNo),
                this.MakeGridHeader(x => x.CustomerName),
                this.MakeGridHeader(x => x.CustomerRegion),
                this.MakeGridHeader(x => x.CustomerTier),
                this.MakeGridHeader(x => x.OrderDate),
                this.MakeGridHeader(x => x.Status),
                this.MakeGridHeader(x => x.PaymentMethod),
                this.MakeGridHeader(x => x.TotalAmount),
                this.MakeGridHeader(x => x.OrderCount),
                this.MakeGridHeaderAction(width: 160)
            };
        }

        public override IOrderedQueryable<Order_View> GetSearchQuery()
        {
            var query = DC.Set<Order>()
                .Include(x => x.Customer)
                .CheckEqual(Searcher.Status, x => x.Status)
                .CheckEqual(Searcher.PaymentMethod, x => x.PaymentMethod)
                .CheckBetween(Searcher.OrderDateFrom, Searcher.OrderDateTo, x => x.OrderDate)
                .Select(x => new Order_View
                {
                    ID = x.ID,
                    OrderNo = x.OrderNo,
                    CustomerName = x.Customer!.Name,
                    CustomerRegion = x.Customer.Region,
                    CustomerTier = x.Customer.Tier,
                    OrderDate = x.OrderDate,
                    Status = x.Status,
                    PaymentMethod = x.PaymentMethod,
                    TotalAmount = x.TotalAmount,
                    OrderCount = 1,
                })
                .OrderByDescending(x => x.OrderDate);
            return query;
        }
    }

    public class Order_View : Order
    {
        [Display(Name = "客戶名稱")]
        [Dimension(DisplayName = "客戶名稱")]
        public string CustomerName { get; set; } = null!;

        [Display(Name = "地區")]
        [Dimension(DisplayName = "地區")]
        public Region CustomerRegion { get; set; }

        [Display(Name = "客戶等級")]
        [Dimension(DisplayName = "客戶等級")]
        public CustomerTier CustomerTier { get; set; }

        [Display(Name = "訂單數")]
        [Measure(DisplayName = "訂單數", AllowedFuncs = AggregateFunc.Count | AggregateFunc.Sum)]
        public int OrderCount { get; set; }

        [Display(Name = "訂單金額")]
        [Measure(DisplayName = "訂單金額", AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Avg | AggregateFunc.Max | AggregateFunc.Min)]
        public new decimal TotalAmount { get; set; }
    }

    public class OrderSearcher : BaseSearcher
    {
        [Display(Name = "訂單狀態")]
        public OrderStatus? Status { get; set; }

        [Display(Name = "付款方式")]
        public PaymentMethod? PaymentMethod { get; set; }

        [Display(Name = "開始日期")]
        public System.DateTime? OrderDateFrom { get; set; }

        [Display(Name = "結束日期")]
        public System.DateTime? OrderDateTo { get; set; }
    }
}
