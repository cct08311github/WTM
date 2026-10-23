using System.Collections.Generic;
using System.Linq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.LabVMs
{
    /// <summary>Row of the key fixture grid (/Lab/Keys): the ids are text, the data comes from LabController.KeySearch (test only).</summary>
    public class LabKeyRow : TopBasePoco
    {
        public string Name { get; set; }

        public string Code { get; set; }
    }

    /// <summary>
    /// Data table fixture whose rows have text ids (the host's entities have GUID ids): every kind of grid action button that puts the id
    /// of a row, or a field of it, into the address of the request. All actions point to Lab actions that record what the server received.
    /// </summary>
    public class LabKeyListVM : BasePagedListVM<LabKeyRow, BaseSearcher>
    {
        public const string DialogAction = "KeyDialog";
        public const string RequestAction = "KeyRequest";
        public const string RedirectAction = "KeyGo";
        public const string GroupAction = "KeyGroup";
        public const string DownloadAction = "KeyDownload";
        public const string FileAction = "KeyFile";

        private static GridAction Make(string name, string action, GridActionParameterTypesEnum type, bool showInRow = true)
        {
            return new GridAction
            {
                Name = name,
                DialogTitle = name,
                ControllerName = "Lab",
                ActionName = action,
                Area = string.Empty,
                ParameterType = type,
                ShowInRow = showInRow,
                ShowDialog = false,
                // the value of the Code column is added to the address too (a row field named by the action)
                whereStr = new[] { "Code" },
            };
        }

        protected override List<GridAction> InitGridAction()
        {
            var dialog = Make("Key dialog", DialogAction, GridActionParameterTypesEnum.SingleId);
            dialog.ShowDialog = true;
            dialog.DialogWidth = 500;

            var request = Make("Key request", RequestAction, GridActionParameterTypesEnum.SingleIdWithNull);

            var redirect = Make("Key page", RedirectAction, GridActionParameterTypesEnum.SingleId);
            redirect.IsRedirect = true;

            var sub = Make("Key sub", GroupAction, GridActionParameterTypesEnum.SingleId);
            sub.ShowDialog = true;
            sub.DialogWidth = 500;
            sub.ShowInRow = false;

            // posts the selected ids in the body of the request and answers a file
            var download = Make("Key download", DownloadAction, GridActionParameterTypesEnum.MultiIds, showInRow: false);
            download.IsDownload = true;
            download.whereStr = null;

            // posts to an address that holds the id of the row and answers a file
            var file = Make("Key file", FileAction, GridActionParameterTypesEnum.SingleId);
            file.IsDownload = true;

            return new List<GridAction>
            {
                dialog,
                file,
                request,
                redirect,
                new GridAction
                {
                    Name = "Key more",
                    ActionName = "ActionsGroup",
                    Area = string.Empty,
                    ParameterType = GridActionParameterTypesEnum.NoId,
                    ShowInRow = false,
                    SubActions = new List<GridAction> { sub },
                },
                download,
            };
        }

        protected override IEnumerable<IGridColumn<LabKeyRow>> InitGridHeader()
        {
            return new List<GridColumn<LabKeyRow>>
            {
                this.MakeGridHeader(x => x.Name),
                // not shown: the cells hold the text of the row as markup, and the Code of a row is an address value in this fixture
                this.MakeGridHeader(x => x.Code).SetHide(),
                this.MakeGridHeaderAction(width: 360)
            };
        }

        // the rows come from the controller, not from a query
        public override IOrderedQueryable<LabKeyRow> GetSearchQuery()
        {
            return new List<LabKeyRow>().AsQueryable().OrderBy(x => x.Name);
        }
    }
}
