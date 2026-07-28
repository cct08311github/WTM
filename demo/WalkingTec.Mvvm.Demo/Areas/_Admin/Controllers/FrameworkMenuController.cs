// WTM默认页面 Wtm buidin page
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Mvc.Admin.ViewModels.FrameworkMenuVMs;

namespace WalkingTec.Mvvm.Mvc.Admin.Controllers
{
    [Area("_Admin")]
    [ActionDescription("MenuKey.MenuMangement")]
    [MainTenantOnly]
    public class FrameworkMenuController : BaseController
    {
        [ActionDescription("Sys.Search")]
        public ActionResult Index()
        {
            var vm = Wtm.CreateVM<FrameworkMenuListVM>();
            return PartialView(vm);
        }

        [ActionDescription("Sys.Search")]
        [HttpPost]
        public string Search(FrameworkMenuSearcher searcher)
        {
            var vm = Wtm.CreateVM<FrameworkMenuListVM>(passInit: true);
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

        [ActionDescription("Sys.Create")]
        public ActionResult Create(Guid? id)
        {
            var vm = Wtm.CreateVM<FrameworkMenuVM>();
            if (id != null)
            {
                vm.Entity.ParentId = id;
            }
            vm.Entity.IsInside = true;
            vm.Entity.IsPublic = false;
            vm.Entity.FolderOnly = false;
            vm.Entity.ShowOnMenu = true;
            return PartialView(vm);
        }

        // SECURITY (#840): this action used to carry [Public] (IAllowAnonymous), which
        // PrivilegeFilter.cs treats as an unconditional early return BEFORE both the
        // LoginUserInfo==null check and the class-level [MainTenantOnly] check. An
        // unauthenticated caller could POST a FrameworkMenu row with IsPublic=true pointing at
        // any URL, and WTMContext.IsUrlPublic (consulted on every request) would then treat
        // that URL as anonymous too — an unauthenticated privilege-escalation primitive. This
        // action must require authentication and the class's normal RBAC/tenant checks like
        // every other write action in this controller (Edit/Delete already had none).
        [HttpPost]
        [ActionDescription("Sys.Create")]
        public ActionResult Create(FrameworkMenuVM vm)
        {
            if (!ModelState.IsValid)
            {
                return PartialView(vm);
            }
            else
            {
                vm.DoAdd();
                if (!ModelState.IsValid)
                {
                    vm.DoReInit();
                    return PartialView(vm);
                }
                else
                {
                    return FFResultJson().CloseDialog().RefreshGrid();
                }
            }
        }

        [ActionDescription("Sys.Edit")]
        public ActionResult Edit(Guid id)
        {
            var vm = Wtm.CreateVM<FrameworkMenuVM>(id);
            vm.IconSelectItems = !string.IsNullOrEmpty(vm.IconFont) && IconFontsHelper
                    .IconFontDicItems
                    .ContainsKey(vm.IconFont)
                    ? IconFontsHelper
                        .IconFontDicItems[vm.IconFont]
                        .Select(x => new ComboSelectListItem()
                        {
                            Text = x.Text,
                            Value = x.Value,
                            Icon = x.Icon
                        }).ToList()
                    : new List<ComboSelectListItem>();

            return PartialView(vm);
        }

        [ActionDescription("Sys.Edit")]
        [HttpPost]
        public ActionResult Edit(FrameworkMenuVM vm)
        {
            if (!ModelState.IsValid)
            {
                vm.IconSelectItems = !string.IsNullOrEmpty(vm.IconFont) && IconFontsHelper
                        .IconFontDicItems
                        .ContainsKey(vm.IconFont)
                        ? IconFontsHelper
                            .IconFontDicItems[vm.IconFont]
                            .Select(x => new ComboSelectListItem()
                            {
                                Text = x.Text,
                                Value = x.Value,
                                Icon = x.Icon
                            }).ToList()
                        : new List<ComboSelectListItem>();
                return PartialView(vm);
            }
            else
            {
                vm.DoEdit();
                if (!ModelState.IsValid)
                {
                    vm.DoReInit();
                    return PartialView(vm);
                }
                else
                {
                    return FFResultJson().CloseDialog().RefreshGrid();
                }
            }
        }

        [ActionDescription("Sys.Delete")]
        public ActionResult Delete(Guid id)
        {
            var vm = Wtm.CreateVM<FrameworkMenuVM>(id);
            return PartialView(vm);
        }

        [ActionDescription("Sys.Delete")]
        [HttpPost]
        public ActionResult Delete(Guid id, IFormCollection noUser)
        {
            var vm = Wtm.CreateVM<FrameworkMenuVM>(id);
            vm.DoDelete();
            if (!ModelState.IsValid)
            {
                return PartialView(vm);
            }
            else
            {
                return FFResultJson().CloseDialog().RefreshGrid();
            }
        }

        [ActionDescription("Sys.Details")]
        public PartialViewResult Details(Guid id)
        {
            var v = Wtm.CreateVM<FrameworkMenuVM>(id);
            return PartialView("Details", v);
        }

        [ActionDescription("_Admin.UnsetPages")]
        public ActionResult UnsetPages()
        {
            var vm = Wtm.CreateVM<FrameworkActionListVM>();
            return PartialView(vm);
        }

        [ActionDescription("_Admin.RefreshMenu")]
        public ActionResult RefreshMenu()
        {
            Cache.Delete(nameof(GlobalData.AllMenus));
            return FFResultJson().Alert(Localizer["Sys.OprationSuccess"]);
        }

        [ActionDescription("GetActionsByModelId")]
        [AllRights]
        public ActionResult GetActionsByModelId(string Id)
        {
            if (string.IsNullOrEmpty(Id))
            {
                return JsonMore(new List<ComboSelectListItem>());
            }
            var modules = Wtm.GlobaInfo.AllModule;
            var m =Utils.ResetModule(modules);

            List<ComboSelectListItem> AllActions = new List<ComboSelectListItem>();
            var action = m.Where(x => x.FullName == Id)?.FirstOrDefault().Actions;
            if (action != null)
            {
                var mList = action?.Where(x => x.MethodName != "Index" && x.IgnorePrivillege == false)?.ToList();
                AllActions = mList.ToListItems(y => y.ActionName, y => y.Url);
                AllActions.ForEach(x => x.Selected = true);
            }

            return JsonMore(AllActions);
        }

        [HttpGet]
        [ResponseCache(Duration = 3600)]
        [AllRights]
        public IActionResult GetIconFontItems(string id)
        {
            if (!string.IsNullOrEmpty(id) && IconFontsHelper.IconFontDicItems.ContainsKey(id))
                return JsonMore(IconFontsHelper.IconFontDicItems[id]);
            else
                return JsonMore(null);
        }

    }

}
