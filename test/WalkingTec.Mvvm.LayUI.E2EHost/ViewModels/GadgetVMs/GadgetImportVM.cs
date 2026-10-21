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
    public partial class GadgetTemplateVM : BaseTemplateVM
    {
        [Display(Name = "Gadget Name")]
        public ExcelPropety Name_Excel = ExcelPropety.CreateProperty<Gadget>(x => x.Name);
        [Display(Name = "Gadget Code")]
        public ExcelPropety Code_Excel = ExcelPropety.CreateProperty<Gadget>(x => x.Code);
        [Display(Name = "Category")]
        public ExcelPropety Category_Excel = ExcelPropety.CreateProperty<Gadget>(x => x.Category);
        [Display(Name = "Unit Price")]
        public ExcelPropety Price_Excel = ExcelPropety.CreateProperty<Gadget>(x => x.Price);
        [Display(Name = "Launch Date")]
        public ExcelPropety LaunchDate_Excel = ExcelPropety.CreateProperty<Gadget>(x => x.LaunchDate);
        [Display(Name = "In Stock")]
        public ExcelPropety InStock_Excel = ExcelPropety.CreateProperty<Gadget>(x => x.InStock);

	    protected override void InitVM()
        {
        }

    }

    public class GadgetImportVM : BaseImportVM<GadgetTemplateVM, Gadget>
    {

    }

}
