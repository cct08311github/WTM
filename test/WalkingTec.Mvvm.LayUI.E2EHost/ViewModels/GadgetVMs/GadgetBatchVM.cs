using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.LayUI.E2EHost.Models;


namespace WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.GadgetVMs
{
    public partial class GadgetBatchVM : BaseBatchVM<Gadget, Gadget_BatchEdit>
    {
        public GadgetBatchVM()
        {
            ListVM = new GadgetListVM();
            LinkedVM = new Gadget_BatchEdit();
        }

    }

	/// <summary>
    /// Class to define batch edit fields
    /// </summary>
    public class Gadget_BatchEdit : BaseVM
    {
        [Display(Name = "Category")]
        public GadgetCategory? Category { get; set; }
        [Display(Name = "Unit Price")]
        public Decimal? Price { get; set; }
        [Display(Name = "In Stock")]
        public Boolean? InStock { get; set; }

        protected override void InitVM()
        {
        }

    }

}
