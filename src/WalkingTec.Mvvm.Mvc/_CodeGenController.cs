// CG-03/04: UIEnum.VUE is [Obsolete] for consumers; this controller must still
// compare against it for back-compat generation. Suppress CS0618 file-wide.
#pragma warning disable CS0618 // UIEnum.VUE is [Obsolete] — internal back-compat
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Mvc
{
    [DebugOnly]
    public class _CodeGenController : BaseController
    {
        public IActionResult Inner()
        {
            return View();

        }



        [ActionDescription("代码生成器")]
        public IActionResult Index(UIEnum ui)
        {
            var vm = Wtm.CreateVM<CodeGenVM>();
            vm.UI = ui;
            vm.EntryDir = AppDomain.CurrentDomain.BaseDirectory;
            vm.AllModels = GetAllModels().ToListItems(x => x.Name, x => x.AssemblyQualifiedName);

            // CG-03/04: Log a server-side deprecation warning when a user navigates to
            // the code generator with a deprecated UI target selected.
            if (vm.DeprecationWarning is not null)
            {
                Wtm?.ServiceProvider
                    ?.GetService<ILoggerFactory>()
                    ?.CreateLogger("CodeGen")
                    ?.LogWarning("[CodeGen] Deprecated UI target selected: {UI}. {Warning}", ui, vm.DeprecationWarning);
            }

            return View(vm);
        }

        [HttpPost]
        [ActionDescription("配置字段")]
        public IActionResult SetField(CodeGenVM vm)
        {
            if (vm.SelectedModel != null)
            {
                Type? modeltype = Type.GetType(vm.SelectedModel);
                if (modeltype == null || modeltype.IsSubclassOf(typeof(TopBasePoco)) == false)
                {
                    ModelState.AddModelError("SelectedModel", MvcProgram._localizer["Codegen.SelectedModelMustBeBasePoco"]);
                }
            }
            if (!ModelState.IsValid)
            {
                vm.AllModels = GetAllModels().ToListItems(x => x.Name, x => x.AssemblyQualifiedName);
                return View("Index", vm);

            }
            else
            {
                vm.FieldList.ModelFullName = vm.SelectedModel;
                return View(vm);
            }
        }

        [HttpPost]
        [ActionDescription("生成确认")]
        public IActionResult Gen(CodeGenVM vm)
        {
            
            return View(vm);
        }

        [HttpPost]
        public IActionResult DoGen(CodeGenVM vm)
        {
            // CG-03/04: Log + surface deprecation warning when generating for a deprecated
            // UI target. Generation still completes (back-compat); the warning is advisory.
            if (vm.DeprecationWarning is not null)
            {
                Wtm?.ServiceProvider
                    ?.GetService<ILoggerFactory>()
                    ?.CreateLogger("CodeGen")
                    ?.LogWarning("[CodeGen] Generating deprecated UI target {UI}: {Warning}", vm.UI, vm.DeprecationWarning);
                vm.DoGen();
                var successMsg = MvcProgram._localizer["Codegen.Success"].Value
                    + "\n\n⚠ " + vm.DeprecationWarning;
                return FFResultJson().Alert(successMsg);
            }

            vm.DoGen();
            return FFResultJson().Alert(MvcProgram._localizer["Codegen.Success"]);
        }

        [ActionDescription("预览")]
        [HttpPost]
        public IActionResult Preview(CodeGenVM vm)
        {
            if (vm.PreviewFile == "Controller")
            {
                ViewData["filename"] = $"{vm.ModelName}{(vm.IsApi == true ? "Api" : "")}Controller.cs";
                ViewData["code"] = vm.GenerateController();
            }
            else if(vm.PreviewFile == "Searcher" || vm.PreviewFile.EndsWith("VM"))
            {
                ViewData["filename"] = vm.ModelName + $"{(vm.IsApi == true ? "Api" : "")}" + vm.PreviewFile.Replace("CrudVM","VM") + ".cs";
                ViewData["code"] = vm.GenerateVM(vm.PreviewFile);
            }
            else if(vm.UI == UIEnum.React)
            {
                if (vm.PreviewFile == "storeindex")
                {
                    ViewData["code"] = vm.GetResource("index.txt", "Spa.React.store").Replace("$modelname$", vm.ModelName.ToLower());
                }
                else if (vm.PreviewFile == "index")
                {
                    ViewData["code"] = vm.GetResource("index.txt", "Spa.React").Replace("$modelname$", vm.ModelName.ToLower());
                }
                else if (vm.PreviewFile == "style")
                {
                    ViewData["code"] = vm.GetResource("style.txt", "Spa.React").Replace("$modelname$", vm.ModelName.ToLower());
                }
                else
                {
                    ViewData["code"] = vm.GenerateReactView(vm.PreviewFile);
                }

            }
            else if(vm.UI == UIEnum.VUE)
            {
                List<string> apineeded = [];
                ViewData["code"] = vm.GenerateVUEView(vm.PreviewFile,apineeded);
            }
            else if (vm.UI == UIEnum.VUE3)
            {
                List<string> apineeded = [];
                ViewData["code"] = vm.GenerateVue3View(vm.PreviewFile);
            }
            else if (vm.UI == UIEnum.Blazor)
            {
                ViewData["code"] = vm.GenerateBlazorView(vm.PreviewFile);
            }
            else if (vm.PreviewFile.EndsWith("View"))
            {
                ViewData["filename"] = vm.PreviewFile.Replace("ListView","Index").Replace("View","") + "cshtml";
                ViewData["code"] = vm.GenerateView(vm.PreviewFile);
            }
            return PartialView(vm);
        }

        private  List<Type> GetAllModels()
        {
            List<Type> models = [];
            
            //获取所有模型
            var pros = Wtm.ConfigInfo.Connections
                .Where(x => x.Enabled && x.DcConstructor != null)
                .SelectMany(x => x.DcConstructor.DeclaringType.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance));
            if (pros != null)
            {
                foreach (var pro in pros)
                {
                    if (pro.PropertyType.IsGeneric(typeof(DbSet<>)))
                    {
                        models.Add(pro.PropertyType.GetGenericArguments()[0]);
                    }
                }
            }
            return models;
        }

    }
}
