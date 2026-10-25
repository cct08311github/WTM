// WTM默认页面 Wtm buidin page
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Mvc.Admin.ViewModels.FrameworkMenuVMs
{
    public class FrameworkMenuBatchVM : BaseBatchVM<FrameworkMenu, FrameworkMenu_BatchEdit>
    {
        public FrameworkMenuBatchVM()
        {
        }
        protected override void InitVM()
        {
        }

        public override bool DoBatchDelete()
        {
            string[] roles = null;
            if (Ids != null)
            {
                // the roles are read first: they say whose cached menus are out of date after the delete
                roles = FrameworkMenuCache.RolesOf(DC, FrameworkMenuCache.WithDescendants(DC, Ids.Select(x => Guid.Parse(x))));
                foreach (var item in Ids)
                {
                    FrameworkMenu f = new FrameworkMenu { ID = Guid.Parse(item) };
                    DC.CascadeDelete(f);
                }
            }
            DC.SaveChanges();
            // without this the deleted menus stay in the side menu until the cached menu list expires
            FrameworkMenuCache.Invalidate(Wtm, roles);
            return true;
        }
    }

    public class FrameworkMenu_BatchEdit : BaseVM
    {
        public List<Guid> IDs { get; set; }
        [Display(Name = "_Admin.ShowOnMenu")]
        public bool ShowOnMenu { get; set; }

        [Display(Name = "_Admin.ParentFolder")]
        public Guid? ParentID { get; set; }
        public List<ComboSelectListItem> AllParents { get; set; }
        [Display(Name = "_Admin.Icon")]
        public string Icon { get; set; }
    }
}
