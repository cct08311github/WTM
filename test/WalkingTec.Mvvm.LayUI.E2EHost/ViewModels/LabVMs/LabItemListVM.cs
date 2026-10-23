using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.LayUI.E2EHost.Models;

namespace WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.LabVMs
{
    public class LabItemSearcher : BaseSearcher
    {
        [Display(Name = "Item Name")]
        public string Name { get; set; }

        [Display(Name = "Category")]
        public GadgetCategory? Category { get; set; }

        [Display(Name = "Active")]
        public bool? Active { get; set; }

        protected override void InitVM()
        {
        }
    }

    /// <summary>Data table fixture: search panel, paging, sortable columns, row and tool bar actions, export.</summary>
    public class LabItemListVM : BasePagedListVM<LabItem_View, LabItemSearcher>
    {
        protected override List<GridAction> InitGridAction()
        {
            return new List<GridAction>
            {
                this.MakeStandardAction("Lab", GridActionStandardTypesEnum.Details, Localizer["Sys.Details"], "", dialogWidth: 600),
                // a tool bar button that posts the selected ids and downloads the answer, after the user confirms
                new GridAction
                {
                    Name = "Download selected",
                    DialogTitle = "Download selected",
                    ControllerName = "Lab",
                    ActionName = "Sample",
                    Area = string.Empty,
                    ParameterType = GridActionParameterTypesEnum.MultiIdWithNull,
                    IsDownload = true,
                    ShowDialog = false,
                    PromptMessage = "Download the selected rows?",
                    IconCls = "layui-icon layui-icon-download-circle"
                },
#pragma warning disable CS0618 // marked obsolete in favour of the standard export action (the Gadget list uses that one), but the LayUI front end must keep working with it
                this.MakeStandardExportAction(),
#pragma warning restore CS0618
            };
        }

        protected override IEnumerable<IGridColumn<LabItem_View>> InitGridHeader()
        {
            return new List<GridColumn<LabItem_View>>
            {
                this.MakeGridHeader(x => x.Name).SetSort(true),
                this.MakeGridHeader(x => x.Category),
                this.MakeGridHeader(x => x.Score).SetSort(true),
                this.MakeGridHeader(x => x.Active),
                this.MakeGridHeader(x => x.Created),
                // the cell holds a link (a list of ColumnFormatInfo); the export writes the link text (#289)
                this.MakeGridHeader(x => x.Note).SetFormat(NoteFormat),
                this.MakeGridHeaderAction(width: 120)
            };
        }

        private List<ColumnFormatInfo> NoteFormat(LabItem_View entity, object val)
        {
            // a dialog link in the cell (the label is data, so it has to be shown as text)
            return new List<ColumnFormatInfo>
            {
                ColumnFormatInfo.MakeDialogButton(ButtonTypesEnum.Link, "/Lab/Details/" + entity.ID, entity.Name, 600, null, "Item")
            };
        }

        public override IOrderedQueryable<LabItem_View> GetSearchQuery()
        {
            var query = DC.Set<LabItem>()
                .CheckContain(Searcher.Name, x => x.Name)
                .CheckEqual(Searcher.Category, x => x.Category)
                .CheckEqual(Searcher.Active, x => x.Active)
                .Select(x => new LabItem_View
                {
                    ID = x.ID,
                    Name = x.Name,
                    Category = x.Category,
                    Score = x.Score,
                    Active = x.Active,
                    Created = x.Created,
                })
                .OrderBy(x => x.Created);
            return query;
        }
    }

    public class LabItem_View : LabItem
    {
        [Display(Name = "Details")]
        public string Note { get; set; }
    }
}
