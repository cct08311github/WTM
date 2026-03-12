using Microsoft.AspNetCore.Mvc;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Demo.ViewModels.ECommerceVMs;

namespace WalkingTec.Mvvm.Demo.Controllers
{
    [ActionDescription("電商訂單")]
    public class OrderController : BaseController
    {
        [ActionDescription("訂單列表")]
        public IActionResult Index()
        {
            var vm = Wtm.CreateVM<OrderListVM>();
            return PartialView(vm);
        }

        [ActionDescription("搜尋")]
        [HttpPost]
        public string Search(OrderSearcher searcher)
        {
            var vm = Wtm.CreateVM<OrderListVM>(passInit: true);
            if (ModelState.IsValid)
            {
                vm.Searcher = searcher;
                return vm.GetJson(false);
            }
            else
            {
                return vm.GetError();
            }
        }
    }

    [ActionDescription("商品銷售明細")]
    public class OrderItemController : BaseController
    {
        [ActionDescription("銷售明細")]
        public IActionResult Index()
        {
            var vm = Wtm.CreateVM<OrderItemListVM>();
            return PartialView(vm);
        }

        [ActionDescription("搜尋")]
        [HttpPost]
        public string Search(OrderItemSearcher searcher)
        {
            var vm = Wtm.CreateVM<OrderItemListVM>(passInit: true);
            if (ModelState.IsValid)
            {
                vm.Searcher = searcher;
                return vm.GetJson(false);
            }
            else
            {
                return vm.GetError();
            }
        }
    }
}
