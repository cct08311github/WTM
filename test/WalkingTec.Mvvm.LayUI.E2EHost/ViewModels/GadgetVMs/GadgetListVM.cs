using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.LayUI.E2EHost.Models;


namespace WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.GadgetVMs
{
    public partial class GadgetListVM : BasePagedListVM<Gadget_View, GadgetSearcher>
    {
        protected override List<GridAction> InitGridAction()
        {
            return new List<GridAction>
            {
                this.MakeStandardAction("Gadget", GridActionStandardTypesEnum.Create, Localizer["Sys.Create"],"", dialogWidth: 800),
                this.MakeStandardAction("Gadget", GridActionStandardTypesEnum.Edit, Localizer["Sys.Edit"], "", dialogWidth: 800),
                this.MakeStandardAction("Gadget", GridActionStandardTypesEnum.Delete, Localizer["Sys.Delete"], "", dialogWidth: 800),
                this.MakeStandardAction("Gadget", GridActionStandardTypesEnum.Details, Localizer["Sys.Details"], "", dialogWidth: 800),
                this.MakeStandardAction("Gadget", GridActionStandardTypesEnum.BatchEdit, Localizer["Sys.BatchEdit"], "", dialogWidth: 800),
                this.MakeStandardAction("Gadget", GridActionStandardTypesEnum.BatchDelete, Localizer["Sys.BatchDelete"], "", dialogWidth: 800),
                this.MakeStandardAction("Gadget", GridActionStandardTypesEnum.Import, Localizer["Sys.Import"], "", dialogWidth: 800),
                this.MakeStandardAction("Gadget", GridActionStandardTypesEnum.ExportExcel, Localizer["Sys.Export"], ""),
            };
        }


        protected override IEnumerable<IGridColumn<Gadget_View>> InitGridHeader()
        {
            return new List<GridColumn<Gadget_View>>{
                this.MakeGridHeader(x => x.Name),
                this.MakeGridHeader(x => x.Code),
                this.MakeGridHeader(x => x.Category),
                this.MakeGridHeader(x => x.Price),
                this.MakeGridHeader(x => x.LaunchDate),
                this.MakeGridHeader(x => x.InStock),
                this.MakeGridHeaderAction(width: 200)
            };
        }

        public override IOrderedQueryable<Gadget_View> GetSearchQuery()
        {
            var query = DC.Set<Gadget>()
                .CheckContain(Searcher.Name, x=>x.Name)
                .CheckContain(Searcher.Code, x=>x.Code)
                .CheckEqual(Searcher.Category, x=>x.Category)
                .Select(x => new Gadget_View
                {
				    ID = x.ID,
                    Name = x.Name,
                    Code = x.Code,
                    Category = x.Category,
                    Price = x.Price,
                    LaunchDate = x.LaunchDate,
                    InStock = x.InStock,
                })
                .OrderBy(x => x.ID);
            return query;
        }

    }

    public class Gadget_View : Gadget{

    }
}
