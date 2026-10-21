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
    public partial class GadgetSearcher : BaseSearcher
    {
        [Display(Name = "Gadget Name")]
        public String Name { get; set; }
        [Display(Name = "Gadget Code")]
        public String Code { get; set; }
        [Display(Name = "Category")]
        public GadgetCategory? Category { get; set; }

        protected override void InitVM()
        {
        }

    }
}
