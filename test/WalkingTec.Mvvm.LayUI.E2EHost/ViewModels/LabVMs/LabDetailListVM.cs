using System;
using System.Collections.Generic;
using System.Linq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.LayUI.E2EHost.Models;

namespace WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.LabVMs
{
    /// <summary>Editable grid of a master / detail form: rows are added and removed in the browser, cells are edited in place.</summary>
    public class LabDetailListVM : BasePagedListVM<LabDetailRow, BaseSearcher>
    {
        protected override List<GridAction> InitGridAction()
        {
            return new List<GridAction>
            {
                this.MakeStandardAction("Lab", GridActionStandardTypesEnum.AddRow, "Add row", ""),
                this.MakeStandardAction("Lab", GridActionStandardTypesEnum.RemoveRow, "Remove row", ""),
            };
        }

        protected override IEnumerable<IGridColumn<LabDetailRow>> InitGridHeader()
        {
            var kinds = new List<ComboSelectListItem>
            {
                new ComboSelectListItem { Value = "Tool", Text = "Tool" },
                new ComboSelectListItem { Value = "Toy", Text = "Toy" },
                new ComboSelectListItem { Value = "Appliance", Text = "Appliance" }
            };
            return new List<GridColumn<LabDetailRow>>
            {
                this.MakeGridHeader(x => x.Name).SetEditType(EditTypeEnum.TextBox),
                this.MakeGridHeader(x => x.Kind).SetEditType(EditTypeEnum.ComboBox, kinds),
                this.MakeGridHeader(x => x.Day).SetEditType(EditTypeEnum.Datetime, null, DateTimeTypeEnum.Date),
                this.MakeGridHeaderAction(width: 120)
            };
        }

        // the grid starts empty: the rows of a new master record are added by the user
        public override IOrderedQueryable<LabDetailRow> GetSearchQuery()
        {
            return new List<LabDetailRow>().AsQueryable().OrderBy(x => x.ID);
        }
    }
}
