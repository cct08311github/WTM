using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Asp.Versioning;
using Microsoft.AspNetCore.SpaServices.ReactDevelopmentServer;
using Microsoft.AspNetCore.SpaServices.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.DependencyModel.Resolution;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Mvc.Auth;
using WalkingTec.Mvvm.Mvc.Filters;
using WalkingTec.Mvvm.Mvc.Helper;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using Microsoft.AspNetCore.SpaServices.Extensions;
using Microsoft.Extensions.FileProviders;
using WalkingTec.Mvvm.Core.Support.Quartz;
using WalkingTec.Mvvm.Core.Services;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using System.Threading.Tasks;
using DUWENINK.Captcha.DI;
using Microsoft.IdentityModel.JsonWebTokens;

namespace WalkingTec.Mvvm.Mvc
{
    public static class FrameworkServiceExtension
    {

        //private static Configs _wtmconfigs;
        //private static Configs WtmConfigs
        //{
        //    get
        //    {
        //        if(_wtmconfigs == null)
        //        {
        //            ConfigurationBuilder cb = new ConfigurationBuilder();
        //            _wtmconfigs = cb.WTMConfig(null).Build().Get<Configs>();
        //        }
        //        return _wtmconfigs;
        //    }
        //}




        private static GlobalData GetGlobalData()
        {
            var gd = new GlobalData();
            gd.AllAssembly = Utils.GetAllAssembly();
            return gd;
        }

        private static List<SimpleMenu> GetAllMenus(List<SimpleModule> allModule, bool isQuickdebug, List<CS> connections)
        {
            var localizer = new ResourceManagerStringLocalizerFactory(Options.Create<LocalizationOptions>(new LocalizationOptions { ResourcesPath = "Resources" }), new Microsoft.Extensions.Logging.LoggerFactory()).Create(typeof(WalkingTec.Mvvm.Core.CoreProgram));
            List<SimpleMenu> menus = [];

            if (isQuickdebug)
            {
                var areas = allModule.Where(x => x.NameSpace != "WalkingTec.Mvvm.Admin.Api").Select(x => x.Area?.AreaName).Distinct().ToList();
                foreach (var area in areas)
                {
                    var modelmenu = new SimpleMenu
                    {
                        ID = Guid.NewGuid(),
                        PageName = area ?? localizer["Sys.DefaultArea"],
                        TenantAllowed = true,
                    };
                    menus.Add(modelmenu);
                    var cModules = allModule.Where(x => x.NameSpace != "WalkingTec.Mvvm.Admin.Api" && x.Area?.AreaName == area).ToList();
                    foreach (var item in cModules)
                    {
                        var pages = item.Actions.Where(x => x.MethodName.ToLower() == "index" || x.ActionDes?.IsPage == true).ToList();
                        foreach (var page in pages)
                        {
                            var url = page.Url;
                            menus.Add(new SimpleMenu
                            {
                                ID = Guid.NewGuid(),
                                ParentId = modelmenu.ID,
                                PageName = item.ActionDes == null ? item.ModuleName : item.ActionDes.Description,
                                Url = url,
                                TenantAllowed = true
                            });
                        }
                    }
                }
            }
            else
            {
                try
                {
                    using (var dc = connections.Where(x => x.Key.ToLower() == "default").FirstOrDefault().CreateDC())
                    {
                        menus.AddRange(dc?.Set<FrameworkMenu>()
                                .OrderBy(x => x.DisplayOrder)
                                .Select(x => new SimpleMenu
                                {
                                    ID = x.ID,
                                    ParentId = x.ParentId,
                                    PageName = x.PageName,
                                    Url = x.Url,
                                    DisplayOrder = x.DisplayOrder,
                                    ShowOnMenu = x.ShowOnMenu,
                                    Icon = x.Icon,
                                    IsPublic = x.IsPublic,
                                    FolderOnly = x.FolderOnly,
                                    MethodName = x.MethodName,
                                    IsInside = x.IsInside,
                                    TenantAllowed = x.TenantAllowed
                                })
                                .ToList());
                    }
                }
                catch (Exception ex)
                {
                    Core.CoreProgram.GetLogger("FrameworkServiceExtension")?.LogWarning(ex, "GetAllMenus: failed to load FrameworkMenu from default connection; returning empty list");
                }
            }
            return menus;
        }

        /// <summary>
        /// 获取所有模块
        /// </summary>
        /// <param name="controllers"></param>
        /// <returns></returns>
        private static List<SimpleModule> GetAllModules(List<Type> controllers)
        {
            List<SimpleModule> modules = [];

            foreach (var ctrl in controllers)
            {
                var pubattr1 = ctrl.GetCustomAttributes(typeof(PublicAttribute), false);
                var pubattr12 = ctrl.GetCustomAttributes(typeof(AllowAnonymousAttribute), false);
                var rightattr = ctrl.GetCustomAttributes(typeof(AllRightsAttribute), false);
                var debugattr = ctrl.GetCustomAttributes(typeof(DebugOnlyAttribute), false);
                var areaattr = ctrl.GetCustomAttributes(typeof(AreaAttribute), false);
                var mainhostonlyattr = ctrl.GetCustomAttributes(typeof(MainTenantOnlyAttribute), false);
                var model = new SimpleModule
                {
                    ClassName = ctrl.Name.Replace("Controller", string.Empty)
                };
                if (ctrl.Namespace == "WalkingTec.Mvvm.Mvc")
                {
                    continue;
                }
                if (areaattr.Length == 0 && model.ClassName == "Home")
                {
                    continue;
                }
                if (pubattr1.Length > 0 || pubattr12.Length > 0 || rightattr.Length > 0 || debugattr.Length > 0)
                {
                    model.IgnorePrivillege = true;
                }
                if (mainhostonlyattr.Length > 0)
                {
                    model.MainHostOnly = true;
                }
                if (typeof(BaseApiController).IsAssignableFrom(ctrl))
                {
                    model.IsApi = true;
                }
                model.NameSpace = ctrl.Namespace;
                //获取controller上标记的ActionDescription属性的值
                var attrs = ctrl.GetCustomAttributes(typeof(ActionDescriptionAttribute), false);
                if (attrs.Length > 0)
                {
                    var ada = attrs[0] as ActionDescriptionAttribute;
                    var nameKey = ada.GetDescription(ctrl);
                    model.ModuleName = nameKey;
                    ada.SetLoccalizer(ctrl);
                    model.ActionDes = ada;
                }
                else
                {
                    model.ModuleName = model.ClassName;
                }
                //获取该controller下所有的方法
                var methods = ctrl.GetMethods(BindingFlags.Public | BindingFlags.DeclaredOnly | BindingFlags.Instance);
                //过滤掉/Login/Login方法和特殊方法
                if (model.ClassName.ToLower() == "login")
                {
                    methods = methods.Where(x => x.IsSpecialName == false && x.Name.ToLower() != "login").ToArray();
                }
                else
                {
                    methods = methods.Where(x => x.IsSpecialName == false).ToArray();
                }
                model.Actions = [];
                //循环所有方法
                foreach (var method in methods)
                {
                    var pubattr2 = method.GetCustomAttributes(typeof(PublicAttribute), false);
                    var pubattr22 = method.GetCustomAttributes(typeof(AllowAnonymousAttribute), false);
                    var arattr2 = method.GetCustomAttributes(typeof(AllRightsAttribute), false);
                    var debugattr2 = method.GetCustomAttributes(typeof(DebugOnlyAttribute), false);
                    var postAttr = method.GetCustomAttributes(typeof(HttpPostAttribute), false);
                    var mainhostonlyattr2 = method.GetCustomAttributes(typeof(MainTenantOnlyAttribute), false);
                    //如果不是post的方法，则添加到controller的action列表里
                    if (postAttr.Length == 0)
                    {
                        var action = new SimpleAction
                        {
                            Module = model,
                            MethodName = method.Name,
                            IgnorePrivillege = model.IgnorePrivillege,
                            MainHostOnly = model.MainHostOnly
                        };
                        if (pubattr2.Length > 0 || pubattr22.Length > 0 || arattr2.Length > 0 || debugattr2.Length > 0)
                        {
                            action.IgnorePrivillege = true;
                        }
                        if (mainhostonlyattr2.Length > 0)
                        {
                            action.MainHostOnly = true;
                        }
                        var attrs2 = method.GetCustomAttributes(typeof(ActionDescriptionAttribute), false);
                        if (attrs2.Length > 0)
                        {
                            var ada = attrs2[0] as ActionDescriptionAttribute;
                            ada.SetLoccalizer(ctrl);
                            action.ActionDes = ada;
                        }
                        else
                        {
                            action.ActionName = action.MethodName;
                        }
                        var pars = method.GetParameters();
                        if (pars != null && pars.Length > 0)
                        {
                            action.ParasToRunTest = [];
                            foreach (var par in pars)
                            {
                                action.ParasToRunTest.Add(par.Name);
                            }
                        }
                        model.Actions.Add(action);
                    }
                }
                //再次循环所有方法
                foreach (var method in methods)
                {
                    var pubattr2 = method.GetCustomAttributes(typeof(PublicAttribute), false);
                    var pubattr22 = method.GetCustomAttributes(typeof(AllowAnonymousAttribute), false);
                    var arattr2 = method.GetCustomAttributes(typeof(AllRightsAttribute), false);
                    var debugattr2 = method.GetCustomAttributes(typeof(DebugOnlyAttribute), false);
                    var mainhostonlyattr2 = method.GetCustomAttributes(typeof(MainTenantOnlyAttribute), false);

                    var postAttr = method.GetCustomAttributes(typeof(HttpPostAttribute), false);
                    //找到post的方法且没有同名的非post的方法，添加到controller的action列表里
                    if (postAttr.Length > 0 && model.Actions.Where(x => x.MethodName.ToLower() == method.Name.ToLower()).FirstOrDefault() == null)
                    {
                        if (method.Name.ToLower().StartsWith("dobatch"))
                        {
                            if (model.Actions.Where(x => "do" + x.MethodName.ToLower() == method.Name.ToLower()).FirstOrDefault() != null)
                            {
                                continue;
                            }
                        }
                        var action = new SimpleAction
                        {
                            Module = model,
                            MethodName = method.Name,
                            IgnorePrivillege = model.IgnorePrivillege,
                            MainHostOnly = model.MainHostOnly
                        };
                        if (pubattr2.Length > 0 || pubattr22.Length > 0 || arattr2.Length > 0 || debugattr2.Length > 0)
                        {
                            action.IgnorePrivillege = true;
                        }
                        if (mainhostonlyattr2.Length > 0)
                        {
                            action.MainHostOnly = true;
                        }

                        var attrs2 = method.GetCustomAttributes(typeof(ActionDescriptionAttribute), false);
                        if (attrs2.Length > 0)
                        {
                            var ada = attrs2[0] as ActionDescriptionAttribute;
                            ada.SetLoccalizer(ctrl);
                            action.ActionDes = ada;
                        }
                        else
                        {
                            action.ActionName = action.MethodName;
                        }
                        var pars = method.GetParameters();
                        if (pars != null && pars.Length > 0)
                        {
                            action.ParasToRunTest = [];
                            foreach (var par in pars)
                            {
                                action.ParasToRunTest.Add(par.Name);
                            }
                        }
                        model.Actions.Add(action);
                    }
                }
                if (model.Actions != null && model.Actions.Count() > 0)
                {
                    if (areaattr.Length > 0)
                    {
                        string areaName = (areaattr[0] as AreaAttribute).RouteValue;
                        var existArea = modules.Where(x => x.Area?.AreaName == areaName).Select(x => x.Area).FirstOrDefault();
                        if (existArea == null)
                        {
                            model.Area = new SimpleArea
                            {
                                AreaName = (areaattr[0] as AreaAttribute).RouteValue,
                                Prefix = (areaattr[0] as AreaAttribute).RouteValue,
                            };
                        }
                        else
                        {
                            model.Area = existArea;
                        }
                    }
                    modules.Add(model);
                }
            }

            return modules;
        }

        private static Assembly GetRuntimeAssembly(string name)
        {
            var path = Assembly.GetEntryAssembly().Location;
            var library = DependencyContext.Default.RuntimeLibraries.Where(x => x.Name.ToLower() == name.ToLower()).FirstOrDefault();
            if (library == null)
            {
                return null;
            }
            var r = new CompositeCompilationAssemblyResolver(new ICompilationAssemblyResolver[]
        {
            new AppBaseCompilationAssemblyResolver(Path.GetDirectoryName(path)),
            new ReferenceAssemblyPathResolver(),
            new PackageCompilationAssemblyResolver()
        });

            var wrapper = new CompilationLibrary(
                library.Type,
                library.Name,
                library.Version,
                library.Hash,
                library.RuntimeAssemblyGroups.SelectMany(g => g.AssetPaths),
                library.Dependencies,
                library.Serviceable);

            List<string> assemblies = [];
            r.TryResolveAssemblyPaths(wrapper, assemblies);
            if (assemblies.Count > 0)
            {
                return AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblies[0]);
            }
            else
            {
                return null;
            }
        }

#nullable enable
        public static IServiceCollection AddWtmContext(this IServiceCollection services, IConfiguration config, Action<WtmContextOption>? options = null)
#nullable restore
        {
            services.AddDUWENINKCaptcha();//使用验证码
            var conf = config.Get<Configs>();
            WtmContextOption op = new WtmContextOption();
            options?.Invoke(op);
            services.Configure<Configs>(config);
            // Issue #753 (HIGH, split-brain flag read): register the appsettings
            // "UIOptions" binding source here (unchanged) — but do NOT hand-parse a
            // config-only snapshot and push it into WtmUIOptionsHolder from this
            // service-registration phase. That snapshot bypasses the ASP.NET Core
            // Options pipeline entirely, so a code-based
            // `services.Configure<WtmUIOptions>(o => o.UseSelectIslandRender = true)`
            // delegate (the documented mechanism per WtmUIOptions' XML doc) was
            // silently ignored by every TagHelper (which reads
            // WtmUIOptionsHolder.Options), while LayuiUIService — which reads the
            // real IOptions<WtmUIOptions> — DID see it. UseWtmContext (below, at the
            // app-build phase where the DI container already exists) now resolves
            // the fully-merged IOptions<WtmUIOptions>.Value (appsettings binding +
            // any code Configure delegates) and populates the SAME holder, so both
            // consumers agree.
            services.Configure<WalkingTec.Mvvm.Core.ConfigOptions.WtmUIOptions>(config.GetSection("UIOptions"));
            var gd = GetGlobalData();
            services.AddHttpContextAccessor();
            services.AddSingleton(gd);
            services.AddLayui();
            services.AddSingleton(op.DataPrivileges ?? new List<IDataPrivilege>());
            DataContextFilter._csfunc = op.CsSelector;
            WtmFileProvider._subDirFunc = op.FileSubDirSelector;
            WTMContext.ReloadUserFunc = op.ReloadUserFunc;
            // #721/#727 PITFALL GUARD: safe PLACEHOLDER only — never resolve IDataContext
            // directly from DI (constructor injection or GetService<IDataContext>()) in any new
            // framework service. See IServiceExtension.AddWtmContextForConsole's identical
            // registration for the full rationale and the canonical WTMContext-first/
            // IWtmDataContextFactory-first resolution pattern to copy.
            services.TryAddScoped<IDataContext, NullContext>();
            services.TryAddSingleton(TimeProvider.System);
            services.AddScoped<WTMContext>();
            services.AddScoped<WtmFileProvider>();
            // Issue #876: decorate the already-registered IControllerActivator so WTMContext.Wtm
            // is populated at controller-CONSTRUCTION time, before any filter runs -- ASP.NET
            // Core's own controller-owned ControllerActionFilter is hard-coded to
            // Order = int.MinValue and always runs before DataContextFilter/PrivilegeFilter/
            // FrameworkFilter (global MvcOptions.Filters) regardless of any Order given to
            // them, so those filters can never be "first" for a controller that reads Wtm from
            // its own OnActionExecuting override. See WtmControllerActivator's doc comment for
            // the full root-cause writeup.
            //
            // #882 review: this used to be an unconditional services.Replace(...Singleton...)
            // that discarded whatever IControllerActivator was already registered -- silently
            // undoing a host's .AddControllersAsServices() (which replaces it with
            // ServiceBasedControllerActivator, whose disposal is owned by the DI container, not
            // a manual Dispose() call) and reimplementing Create/Release/ReleaseAsync from
            // scratch instead of preserving whatever disposal contract was already in place. It
            // now WRAPS whatever is currently registered (falling back to nothing -- see the
            // exception below -- only if nothing is), preserving that activator's Create AND its
            // disposal contract exactly, and keeping the original registration's ServiceLifetime
            // (both DefaultControllerActivator and ServiceBasedControllerActivator are
            // TryAddTransient by ASP.NET Core itself) so a captive-dependency mistake isn't
            // introduced for a hypothetical third-party activator with scoped constructor deps.
            //
            // This requires AddMvc()/AddControllers() (and, if used,
            // .AddControllersAsServices()) to run BEFORE AddWtmContext() -- both of this repo's
            // real Startup.cs files already call them in that order. Failing fast here instead
            // of silently installing some fallback means a future call-order regression breaks
            // at startup, not as a silently-undone security fix in production.
            //
            // #882 review, second round (comment corrected in the third round -- see below):
            // only consider UNKEYED descriptors here. .NET 8+ keyed services
            // (ServiceDescriptor.IsKeyedService) can register an IControllerActivator under a
            // key -- its ImplementationType/ImplementationFactory/ImplementationInstance
            // getters all return NULL for a keyed descriptor, not throw (verified against the
            // real Microsoft.Extensions.DependencyInjection.Abstractions 10.0.9 assembly: each
            // getter's body is `if (!IsKeyedService) { return the real value; } return null;`).
            // What DOES throw is the reverse misuse: calling the KeyedImplementationType/
            // -Factory/-Instance getters (the ones that apply to a KEYED descriptor) on an
            // UNKEYED one. Without this filter, the null ImplementationType/Factory/Instance
            // would fall through to the `_ => throw ...` arm below when the descriptor picked
            // out by LastOrDefault happened to be keyed -- an unrelated exception with a
            // confusing message, not a graceful "not found". Separately, and independent of
            // which getter throws or returns null: an unkeyed
            // sp.GetRequiredService<IControllerActivator>() call -- what this activator, and
            // MVC itself, actually performs -- never resolves a keyed registration regardless
            // of where it sits in registration order, so picking the last KEYED one here (as
            // the version before this filter did) would always have wrapped the wrong thing
            // even before any exception was in play.
            var existingActivatorDescriptor = services
                .Where(d => d.ServiceType == typeof(IControllerActivator) && !d.IsKeyedService)
                .LastOrDefault();
            if (existingActivatorDescriptor == null)
            {
                throw new InvalidOperationException(
                    "AddWtmContext() requires an IControllerActivator to already be registered. " +
                    "Call services.AddMvc() or services.AddControllers() (and, if used, " +
                    ".AddControllersAsServices()) BEFORE services.AddWtmContext().");
            }

            services.Replace(ServiceDescriptor.Describe(
                typeof(IControllerActivator),
                sp =>
                {
                    // #882 review, second round: AddWtmContext builds `inner` itself here,
                    // bypassing the DI container's own creation path -- which is what normally
                    // enrolls a freshly-created disposable instance into the current scope's
                    // disposables list. `ownsInner` tracks whether THIS code is the one that
                    // "created" inner (ImplementationFactory/ImplementationType: yes, nothing
                    // else will ever dispose it, so WtmControllerActivator must) or whether
                    // inner is a pre-built, possibly cross-request-shared ImplementationInstance
                    // (no -- the container does not auto-dispose ImplementationInstance
                    // registrations either, by original design, and disposing a shared instance
                    // from a single per-resolution wrapper's teardown would break every other
                    // resolution still using it). See WtmControllerActivator's doc comment.
                    (IControllerActivator inner, bool ownsInner) = existingActivatorDescriptor switch
                    {
                        { ImplementationInstance: IControllerActivator instance } => (instance, false),
                        { ImplementationFactory: not null } => ((IControllerActivator)existingActivatorDescriptor.ImplementationFactory!(sp), true),
                        { ImplementationType: not null } => ((IControllerActivator)ActivatorUtilities.CreateInstance(sp, existingActivatorDescriptor.ImplementationType!), true),
                        _ => throw new InvalidOperationException("Unable to resolve the existing IControllerActivator registration to wrap it."),
                    };
                    return new WalkingTec.Mvvm.Mvc.Helper.WtmControllerActivator(inner, ownsInner);
                },
                existingActivatorDescriptor.Lifetime));

            // Issue #407: register the default no-op upload validator.
            // Hosts can override by calling services.AddScoped<IUploadValidator, MyValidator>()
            // *after* AddWtmContext.  TryAddScoped ensures the first registration wins so the
            // host replacement takes precedence.
            services.TryAddScoped<WalkingTec.Mvvm.Core.Support.FileHandlers.IUploadValidator>(sp =>
            {
                var fileOpts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Configs>>().Value.FileUploadOptions;
                // Activate built-in validator when any allowlist or size cap is configured.
                if ((fileOpts.AllowedExtensions != null && fileOpts.AllowedExtensions.Count > 0) ||
                    (fileOpts.AllowedContentTypes != null && fileOpts.AllowedContentTypes.Count > 0) ||
                    fileOpts.MaxUploadBytes > 0)
                {
                    return new WalkingTec.Mvvm.Core.Support.FileHandlers.ExtensionContentTypeUploadValidator(fileOpts);
                }
                return new WalkingTec.Mvvm.Core.Support.FileHandlers.NoOpUploadValidator();
            });

            services.Configure<FormOptions>(y =>
            {
                y.ValueCountLimit = 5000;
                y.ValueLengthLimit = int.MaxValue - 20480;
                y.MultipartBodyLengthLimit = conf.FileUploadOptions.UploadLimit;
            });
            services.AddHostedService<QuartzHostService>();
            services.AddHostedService<DbConnectionWarmupService>();
            var analysisRegistry = new WalkingTec.Mvvm.Core.Analysis.AnalysisVmRegistry();
            analysisRegistry.Build(AppDomain.CurrentDomain.GetAssemblies());
            services.AddSingleton(analysisRegistry);
            services.AddMemoryCache();
            services.AddSingleton<WalkingTec.Mvvm.Core.Analysis.IAnalysisCache>(sp =>
                new WalkingTec.Mvvm.Core.Analysis.MemoryAnalysisCache(
                    sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                    conf.AnalysisCacheTtl,
                    // #676: explicit resolve — this is a factory delegate, not auto constructor-
                    // injection, so TimeProvider must be pulled from the container by hand.
                    sp.GetService<TimeProvider>()));
            services.AddSingleton<WalkingTec.Mvvm.Core.Analysis.IAnalysisFieldPolicy, WalkingTec.Mvvm.Core.Analysis.DefaultAnalysisFieldPolicy>();
            services.AddSingleton<WalkingTec.Mvvm.Core.Analysis.AnalysisQueryEngine>(sp =>
            {
                var cache = sp.GetService<WalkingTec.Mvvm.Core.Analysis.IAnalysisCache>();
                var configs = sp.GetService<Microsoft.Extensions.Options.IOptions<Configs>>();
                return new WalkingTec.Mvvm.Core.Analysis.AnalysisQueryEngine(
                    WalkingTec.Mvvm.Core.Analysis.GroupByStrategyResolver.Default,
                    cache,
                    configs?.Value.AnalysisCacheTtl);
            });

            // Dashboard module (opt-in via AddWtmDashboard in app startup)
            // No auto-registration — apps call services.AddWtmDashboard() explicitly
            services.AddSingleton<WalkingTec.Mvvm.Core.Cache.ILookupCacheService>(sp =>
                new WalkingTec.Mvvm.Core.Cache.LookupCacheService(
                    sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                    AppDomain.CurrentDomain.GetAssemblies(),
                    sp.GetService<WalkingTec.Mvvm.Core.Cache.LookupCacheOptions>()));
            services.AddHostedService<WalkingTec.Mvvm.Core.Cache.LookupCacheWarmupService>();
            var cs = conf.Connections.Where(x => x.Enabled).ToList();
            foreach (var item in cs)
            {
                try
                {
                    var dc = item.CreateDC();
                    dc.EnsureCreate();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WTM] Warning: could not initialize database connection '{item.Key}' ({item.DbType}): {ex.Message}");
                }
            }
            services.AddApiVersioning(options =>
             {
                 options.ReportApiVersions = true;
                 options.DefaultApiVersion = new ApiVersion(1, 0);
                 options.AssumeDefaultVersionWhenUnspecified = true;
             })
            .AddApiExplorer(o =>
            {
                o.GroupNameFormat = "'v'VVV";
                o.SubstituteApiVersionInUrl = true;
            });

            // Phase 1: Extracted services (parallel to WTMContext, no breaking changes)
            services.AddScoped<IWtmApiClient, WtmApiClient>();
            services.AddScoped<IWtmLogService>(sp => new WtmLogService(
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()));
            services.AddSingleton<IWtmAuthorizationService>(sp => new WtmAuthorizationService(
                sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>() is { } lf1 ? Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<WtmAuthorizationService>(lf1) : null));
            services.AddScoped<IWtmDataContextFactory>(sp => new WtmDataContextFactory(
                sp.GetRequiredService<IOptionsMonitor<Configs>>(),
                sp.GetRequiredService<GlobalData>(),
                sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>(),
                sp.GetService<TimeProvider>()));

            // Phase 2-3: Auth, UserCache, Tenant, VmFactory services
            services.AddSingleton<IWtmAuthService, WtmAuthService>();
            services.AddScoped<IWtmUserCacheService>(sp => new WtmUserCacheService(
                sp.GetRequiredService<IDistributedCache>()));
            services.AddScoped<IWtmTenantService>(sp => new WtmTenantService(
                sp.GetRequiredService<IDistributedCache>(),
                sp.GetRequiredService<IOptionsMonitor<Configs>>(),
                sp.GetRequiredService<GlobalData>(),
                sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>() is { } lf2 ? Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<WtmTenantService>(lf2) : null));
            services.AddSingleton<IWtmVmFactory, WtmVmFactory>();

            return services;
        }

        public static IServiceCollection AddWtmCrossDomain(this IServiceCollection services, IConfiguration config)
        {
            var conf = config.Get<Configs>();
            services.AddCors(options =>
            {
                if (conf.CorsOptions?.Policy?.Count > 0)
                {
                    foreach (var item in conf.CorsOptions.Policy)
                    {
                        string[] domains = item.Domain?.Split(',');
                        options.AddPolicy(item.Name,
                           builder =>
                           {
                               builder.WithOrigins(domains)
                                                   .AllowAnyHeader()
                                                   .AllowAnyMethod()
                                                   .AllowCredentials()
                                                   .WithExposedHeaders("Content-Disposition");
                           });
                    }
                }
                else
                {
                    // Security: reflect-any-origin must NOT be combined with AllowCredentials
                    // (browsers block credentialed cross-origin requests unless an explicit origin
                    // is listed). Use CorsOptions.Policy with an explicit Domain list to enable
                    // credentialed CORS.
                    options.AddPolicy("_donotusedefault",
                        builder =>
                        {
                            builder.SetIsOriginAllowed((a) => true)
                                                .AllowAnyHeader()
                                                .AllowAnyMethod()
                                                .WithExposedHeaders("Content-Disposition");
                        });
                }
            });
            return services;
        }
        public static IServiceCollection AddWtmSession(this IServiceCollection services, int timeout, IConfiguration config)
        {
            var conf = config.Get<Configs>();
            // Issue #813: honor CookieOptions.SecurePolicy so operators can
            // force 'Secure' in production (e.g., behind a TLS-terminating
            // reverse proxy). Default preserved as SameAsRequest.
            var securePolicy = conf.CookieOptions?.SecurePolicy ?? CookieSecurePolicy.SameAsRequest;
            services.AddSession(options =>
            {
                options.Cookie.Name = conf.CookiePre + ".Session";
                options.IdleTimeout = TimeSpan.FromSeconds(timeout);
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = securePolicy;
            });
            return services;
        }
        public static IServiceCollection AddWtmAuthentication(this IServiceCollection services, IConfiguration config)
        {
            var conf = config.Get<Configs>();
            services.AddScoped<ITokenService, TokenService>();
            // Singleton denylist backed by IMemoryCache (already registered via AddMemoryCache()
            // in AddWtmContext). Entries auto-evict at token expiry time — bounded memory growth.
            // See IAccessTokenDenylist for multi-node deployment guidance.
            services.TryAddSingleton<IAccessTokenDenylist>(sp =>
                new MemoryCacheAccessTokenDenylist(
                    sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));

            var jwtOptions = conf.JwtOptions;

            // Issue #923 (P0): JwtOption.SecurityKey's setter silently pads any value
            // shorter than 32 chars with 'x' (JwtOptions.cs — unchanged by this fix), so
            // demo-shipped keys like "super" and the developer manual's 42-byte placeholder
            // both passed the OLD guard here, which only recognized the literal well-known
            // default. Anyone who can read this repository (including its public GitHub
            // mirror, cct08311github/WTM) could therefore forge an HMAC-SHA256 access token
            // for any ITCode. IsWeakSigningKey() is the real invariant: too short (< 32
            // UTF-8 bytes = 256 bits, the IDX10720 minimum SymmetricSignatureProvider
            // enforces for HmacSha256) OR one of JwtOption.KnownPublicKeys — evaluated
            // against the RAW, pre-padding value, independent of length.
            if (jwtOptions.IsWeakSigningKey(out var weakKeyReason))
            {
                // #923 design table row (d): a too-short-but-not-publicly-known key is
                // rejected in EVERY environment, including Development — unlike an unset or
                // publicly known key, which gets the ephemeral-key carve-out below. Setting a
                // short key is a deliberate (if mistaken) choice ("I picked this and believed
                // it was supported"); that false belief must surface on the developer's own
                // machine, not first in production. Exact comparison against the fixed
                // WeakReasonTooShort constant (not a substring match) is intentional and
                // relies on IsWeakSigningKey() never interpolating per-call text into reason.
                bool isTooShortButNotPubliclyKnown =
                    weakKeyReason == JwtOption.WeakReasonTooShort;
                bool eligibleForDevelopmentCarveOut =
                    !isTooShortButNotPubliclyKnown && IsDevelopmentEnvironment(services);

                if (!eligibleForDevelopmentCarveOut)
                {
                    throw new InvalidOperationException(
                        $"[WTM Security] JwtOptions.SecurityKey is not usable: {weakKeyReason} " +
                        "Anyone who can read the WTM repository (including its public GitHub mirror) " +
                        "can forge access tokens for any user with a known, unset, or too-short key. " +
                        "Set a strong, unique key (>= 32 bytes) in your configuration " +
                        "(JwtOptions:SecurityKey). Generate one with: openssl rand -base64 32 " +
                        (isTooShortButNotPubliclyKnown
                            ? "(A too-short custom key is rejected in every environment, including " +
                              "Development — there is no supported short-key configuration.)"
                            : "(This check is skipped only in the Development environment, detected via " +
                              "IWebHostEnvironment when one is registered, or the ASPNETCORE_ENVIRONMENT / " +
                              "DOTNET_ENVIRONMENT variables otherwise; if neither is available this fails " +
                              "closed as non-Development.)"));
                }

                // Development, and NOT the too-short case handled above: never sign with a
                // publicly known key, but do not block local development either. The random
                // 256-bit key below is generated ONCE, here — deliberately OUTSIDE the
                // PostConfigure delegate a few lines down. PostConfigure can run again on
                // every IOptionsMonitor<Configs> reload (e.g. an appsettings.json file-watch
                // triggering a rebind); computing a fresh key INSIDE that delegate would
                // silently invalidate every access token already issued each time it re-ran.
                var generatedKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                jwtOptions.SecurityKey = generatedKey;
                // Console.Error, not ILogger: this runs during ConfigureServices, before the
                // DI container is built, so no ILogger can be resolved here yet — matches the
                // existing Console.Error.WriteLine precedent a few lines above in
                // AddWtmContext's own connection-init warning. Do not "fix" this into an
                // ILogger call; there is nothing to resolve it from at this point.
                Console.Error.WriteLine(
                    $"[WTM Security] Warning: JwtOptions.SecurityKey is unset or a publicly known " +
                    $"value ({weakKeyReason}) — generated a random, process-lifetime-only signing " +
                    "key because the Development environment was detected. Every previously issued " +
                    "access token is now invalid, and every token issued this run becomes invalid on " +
                    "the next restart. Set a real JwtOptions:SecurityKey before deploying outside " +
                    "Development.");

                // TokenService (and anything else that resolves JwtOption via
                // IOptionsMonitor<Configs>) reads a SEPARATE object graph from the `conf`
                // snapshot above: AddWtmAuthentication uses config.Get<Configs>(), while
                // TokenService's constructor uses IOptionsMonitor<Configs>.CurrentValue —
                // two independently-bound Configs instances. Without this PostConfigure, the
                // JwtBearer handler configured below would validate incoming tokens with
                // `generatedKey` while TokenService signed new ones with whatever
                // IOptionsMonitor<Configs> resolves on its own (the unset/weak raw default),
                // producing a "signed with A, validated with B" app that rejects every token
                // it issues. Configure/PostConfigure ordering is independent of REGISTRATION
                // order in the DI container (all Configure actions run before all
                // PostConfigure actions, regardless of which was added to IServiceCollection
                // first), so this is correct even though AddWtmContext's own
                // services.Configure<Configs>(config) is typically called after
                // AddWtmAuthentication in Startup.ConfigureServices.
                //
                // The overwrite is conditional, not unconditional: a host that also registers
                // its own services.Configure<Configs>(o => o.JwtOptions.SecurityKey = "...")
                // code delegate (e.g. reading a real value from a secret manager) has that
                // value applied to THIS SAME instance before PostConfigure runs, since ALL
                // Configure actions run before ANY PostConfigure action — re-checking
                // IsWeakSigningKey() here lets that real key stand instead of being silently
                // clobbered by the ephemeral one on every restart. See the #753-family
                // follow-up for the residual gap this narrows but does not close: the LOCAL
                // `jwtOptions` captured above (used for the JwtBearer handler's own
                // IssuerSigningKey a few lines down) still cannot see that same code delegate
                // — config.Get<Configs>() cannot observe it — so a Development host relying
                // entirely on a code delegate (no key in raw IConfiguration at all) ends up
                // signing new tokens through TokenService with its real key while the JwtBearer
                // handler still validates against the generated one, and every login fails.
                // Fixing that fully means changing what AddWtmAuthentication reads from, which
                // is the #753-family split-brain itself and out of scope here.
                //
                // #931 item 3: the substitution must NOT fire for WeakReasonTooShort. Scenario:
                // this branch already ran (raw config's key was unset, so the ephemeral path
                // started), then a host ALSO registers services.Configure<Configs>(o =>
                // o.JwtOptions.SecurityKey = "short") — a too-short custom key, via a code
                // delegate evaluated after this one. By the time PostConfigure runs, the merged
                // Configs holds that short key, and IsWeakSigningKey() is still true — but for
                // reason WeakReasonTooShort, not "unset/known-public". The design's row (d) says
                // a too-short custom key is rejected in EVERY environment, Development included,
                // with NO ephemeral fallback; substituting the generated key here would silently
                // launder exactly that case into a booting app, defeating row (d) via a second
                // Configure source. Leaving the short key in place instead means IOptionsMonitor
                // <Configs> resolves to it unchanged — TokenService's own constructor guard
                // (#931 item 5) then rejects it at first use, so the rule still surfaces as a
                // failure, just at first sign/validate rather than at this exact statement.
                services.PostConfigure<Configs>(c =>
                {
                    if (c.JwtOptions.IsWeakSigningKey(out var postConfigureReason)
                        && postConfigureReason != JwtOption.WeakReasonTooShort)
                    {
                        c.JwtOptions.SecurityKey = generatedKey;
                    }
                });
            }
            else if (jwtOptions.HasLowCharacterDiversity)
            {
                // Warn-only (#923): the key is long enough and not a known public value, but
                // has fewer than 8 distinct characters — the fingerprint of an operator
                // manually padding a short key to satisfy the length check rather than using
                // a genuinely random one. Never gates startup: this is a heuristic (a real
                // 40-character English sentence also triggers it), and per this repo's
                // Compatibility > Security priority ordering, a heuristic false positive must
                // never stop production from booting.
                // Console.Error, not ILogger, for the same reason as the weak-key warning above.
                Console.Error.WriteLine(
                    "[WTM Security] Warning: JwtOptions.SecurityKey has fewer than 8 distinct " +
                    "characters. This often indicates a short key manually padded to meet the " +
                    "length requirement rather than a genuinely random key. Consider generating " +
                    "one with: openssl rand -base64 32");
            }

            var cookieOptions = conf.CookieOptions;

            JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                     .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
                     {
                         options.TokenValidationParameters = new TokenValidationParameters
                         {
                             NameClaimType = AuthConstants.JwtClaimTypes.Name,
                             RoleClaimType = AuthConstants.JwtClaimTypes.Role,

                             ValidateIssuer = true,
                             ValidIssuer = jwtOptions.Issuer,

                             ValidateAudience = true,
                             ValidAudience = jwtOptions.Audience,

                             ValidateIssuerSigningKey = true,
                             // #931 item 1: EffectiveSecurityKey (padded HMAC material), not
                             // SecurityKey (the raw, unpadded, round-trippable value).
                             IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.EffectiveSecurityKey)),
                             LifetimeValidator = ValidateJwtLifetime,
                             ValidateLifetime = true
                         };
                         options.Events = new JwtBearerEvents
                         {
                             OnMessageReceived = context =>
                             {
                                 // Only honor query-string tokens on WebSocket upgrade requests, where
                                 // clients cannot set Authorization headers. Normal HTTP requests with
                                 // ?access_token=… are ignored to prevent URL/log/Referer leakage.
                                 var accessToken = context.Request.Query["access_token"];
                                 if (!string.IsNullOrEmpty(accessToken)
                                     && context.HttpContext.WebSockets.IsWebSocketRequest)
                                 {
                                     context.Token = accessToken;
                                 }
                                 return Task.CompletedTask;
                             },
                             OnTokenValidated = (context) =>
                             {
                                 // Guard against revoked access tokens (Issue #126).
                                 // After logout / explicit revocation, the jti is added to the
                                 // in-process denylist so the token is rejected even before its
                                 // natural expiry time.
                                 if (context.SecurityToken is JsonWebToken jwt)
                                 {
                                     // Use the literal "jti" to avoid ambiguity between
                                     // System.IdentityModel.Tokens.Jwt and Microsoft.IdentityModel.JsonWebTokens.
                                     var jti = jwt.GetClaim("jti")?.Value;
                                     if (!string.IsNullOrEmpty(jti))
                                     {
                                         var denylist = context.HttpContext.RequestServices
                                             .GetRequiredService<IAccessTokenDenylist>();
                                         if (denylist.IsDenied(jti))
                                         {
                                             context.Fail("Token has been revoked.");
                                             return Task.CompletedTask;
                                         }
                                     }
                                 }
                                 return Task.CompletedTask;
                             }
                            };
                     })
                   .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
                    {
                        options.Cookie.Name = CookieAuthenticationDefaults.CookiePrefix + conf.CookiePre + "." + AuthConstants.CookieAuthName;
                        options.Cookie.HttpOnly = true;
                        options.Cookie.SameSite = SameSiteMode.Strict;
                        // Issue #813: honor CookieOptions.SecurePolicy. Default preserved as SameAsRequest.
                        options.Cookie.SecurePolicy = cookieOptions.SecurePolicy;
                        options.Cookie.Domain = string.IsNullOrEmpty(cookieOptions.Domain) ? null : cookieOptions.Domain;
                        options.ClaimsIssuer = cookieOptions.Issuer;
                        options.SlidingExpiration = cookieOptions.SlidingExpiration;
                        options.ExpireTimeSpan = TimeSpan.FromSeconds(cookieOptions.Expires);
                        // options.SessionStore = new MemoryTicketStore();

                        options.LoginPath = cookieOptions.LoginPath;
                        options.LogoutPath = cookieOptions.LogoutPath;
                        options.ReturnUrlParameter = cookieOptions.ReturnUrlParameter;
                        options.AccessDeniedPath = cookieOptions.AccessDeniedPath;
                    });
            return services;
        }

        /// <summary>
        /// Issue #923: determines whether the host is running in the Development
        /// environment, for the JWT weak-key startup gate's Development-only ephemeral-key
        /// carve-out in <see cref="AddWtmAuthentication"/>.
        /// </summary>
        /// <remarks>
        /// Checked in this order:
        /// <list type="number">
        /// <item>An <see cref="IWebHostEnvironment"/> already registered in
        /// <paramref name="services"/>. The ASP.NET Core generic host registers this as a
        /// singleton INSTANCE before <c>Startup.ConfigureServices</c> runs — regardless of
        /// whether the environment came from <c>ASPNETCORE_ENVIRONMENT</c>,
        /// <c>DOTNET_ENVIRONMENT</c>, a <c>--environment</c> command-line switch, or a
        /// launchSettings.json profile, they all funnel into this ONE resolved value — so it
        /// is found here via <c>ImplementationInstance</c> without building a temporary
        /// <see cref="IServiceProvider"/>.</item>
        /// <item>The <c>ASPNETCORE_ENVIRONMENT</c> / <c>DOTNET_ENVIRONMENT</c> environment
        /// variables directly, for hosts that never register <see cref="IWebHostEnvironment"/>
        /// at all (a bare <see cref="IServiceCollection"/> console/worker host calling
        /// <c>AddWtmAuthentication</c> without going through <c>ConfigureWebHostDefaults</c>).</item>
        /// </list>
        /// If NEITHER is available, this fails CLOSED — returns <c>false</c> (treat as
        /// non-Development) — because the two misdetection directions are not symmetric: a
        /// Production host misdetected as non-Development just gets a normal "set your key"
        /// startup failure (annoying, loud, safe); a Production host misdetected as
        /// Development would silently sign with a random ephemeral key that changes on every
        /// restart (an availability problem, not a breach, per the design's own carve-out —
        /// but still never the right default when the environment genuinely cannot be
        /// determined).
        /// </remarks>
        private static bool IsDevelopmentEnvironment(IServiceCollection services)
        {
            var hostEnv = services
                .FirstOrDefault(d => d.ServiceType == typeof(IWebHostEnvironment))
                ?.ImplementationInstance as IWebHostEnvironment;
            if (hostEnv != null)
            {
                return hostEnv.IsDevelopment();
            }

            var envName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
            return string.Equals(envName, Environments.Development, StringComparison.OrdinalIgnoreCase);
        }

        public static IServiceCollection AddWtmHttpClient(this IServiceCollection services, IConfiguration config)
        {
            var conf = config.Get<Configs>();
            services.AddHttpClient();
            if (conf.Domains != null)
            {
                foreach (var item in conf.Domains)
                {
                    services.AddHttpClient(item.Key, x =>
                    {
                        x.BaseAddress = new Uri(item.Value.Url);
                        x.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
                        x.DefaultRequestHeaders.Add("User-Agent", "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.2; SV1; .NET CLR 1.1.4322; .NET CLR 2.0.50727)");
                    });
                }
            }
            return services;
        }

        public static IServiceCollection AddWtmSwagger(this IServiceCollection services, bool useFullName = false)
        {
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "My API", Version = "v1" });
                var bearer = new OpenApiSecurityScheme()
                {
                    Description = "JWT Bearer",
                    Name = "Authorization",
                    In = ParameterLocation.Header,
                    Type = SecuritySchemeType.ApiKey

                };
                c.AddSecurityDefinition("Bearer", bearer);
                c.AddSecurityRequirement(_ =>
                {
                    var sr = new OpenApiSecurityRequirement();
                    sr.Add(new OpenApiSecuritySchemeReference("Bearer"), new List<string>());
                    return sr;
                });
                c.SchemaFilter<SwaggerFilter>();
                if (useFullName == true)
                {
                    c.CustomSchemaIds(i => i.FullName);
                }
            });
            return services;
        }

#nullable enable
        public static IServiceCollection AddWtmMultiLanguages(this IServiceCollection services, IConfiguration config, Action<WtmLocalizationOption>? op = null)
#nullable restore
        {
            var conf = config.Get<Configs>();
            services.AddLocalization(options => options.ResourcesPath = "Resources");

            services.Configure<RequestLocalizationOptions>(options =>
            {
                // SupportLanguages always returns at least one entry (falls back to zh/en when
                // Languages is empty/whitespace/null — see Configs.SupportLanguages).
                // Setting DefaultRequestCulture explicitly ensures an unconfigured-culture
                // request degrades to the first supported culture rather than failing.
                var cultures = conf.SupportLanguages;
                options.DefaultRequestCulture = new RequestCulture(cultures[0]);
                options.SupportedCultures = cultures;
                options.SupportedUICultures = cultures;
            });
            WtmLocalizationOption loc = new WtmLocalizationOption();
            op?.Invoke(loc);
            if (loc.LocalizationType != null)
            {
                services.AddSingleton<WtmLocalizationOption>(loc);
            }
            return services;
        }

        public static IMvcBuilder AddWtmDataAnnotationsLocalization(this IMvcBuilder builder, Type programType)
        {
            builder.AddDataAnnotationsLocalization(options =>
             {
                 options.DataAnnotationLocalizerProvider = (type, factory) =>
                 {
                     //if (Core.CoreProgram.Buildindll.Any(x => type.FullName.StartsWith(x)))
                     //{
                     //    return factory.Create(typeof(WalkingTec.Mvvm.Core.CoreProgram));
                     //}
                     //else
                     //{
                     return factory.Create(programType);
                     //}
                 };
             });
            return builder;
        }

        public static IApplicationBuilder UseWtmContext(this IApplicationBuilder app, bool isspa = false)
        {
            var configs = app.ApplicationServices.GetRequiredService<IOptionsMonitor<Configs>>().CurrentValue;
            // Issue #753 (HIGH, split-brain flag read): resolve WtmUIOptions through
            // the real ASP.NET Core Options pipeline (IOptions<T>) — this reflects
            // BOTH the appsettings "UIOptions" binding registered in AddWtmContext AND
            // any services.Configure<WtmUIOptions>(o => ...) code delegate the host
            // app registered — then push it into the SAME static holder every
            // TagHelper (BaseFieldTag.UIConfig / BaseButton.UIConfig) reads, so
            // TagHelpers and LayuiUIService (which already reads IOptions<WtmUIOptions>
            // directly) see the identical, fully-merged value.
            var uiOptions = app.ApplicationServices.GetRequiredService<IOptions<WalkingTec.Mvvm.Core.ConfigOptions.WtmUIOptions>>().Value;
            WalkingTec.Mvvm.TagHelpers.LayUI.BaseFieldTag.SetUIOptions(uiOptions);
            var lg = app.ApplicationServices.GetRequiredService<LinkGenerator>();
            var gd = app.ApplicationServices.GetRequiredService<GlobalData>();
            var localfactory = app.ApplicationServices.GetRequiredService<IStringLocalizerFactory>();
            var lop = app.ApplicationServices.GetService<WtmLocalizationOption>();

            // WTM-SEC-006: IsQuickDebug bypasses ALL RBAC checks (WtmAuthorizationService:30,
            // PrivilegeFilter:99). Treat it as a P0 misconfiguration in any non-Development
            // environment — fail fast at startup so operators cannot accidentally deploy with
            // it enabled, mirroring the JWT weak-key guard in AddWtmAuthentication.
            var env = app.ApplicationServices.GetService<IWebHostEnvironment>();
            if (configs.IsQuickDebug == true && env != null && !env.IsDevelopment())
            {
                var logger = app.ApplicationServices.GetService<ILoggerFactory>()
                    ?.CreateLogger("WTM.Security");
                logger?.LogCritical(
                    "[WTM Security] IsQuickDebug=true in a non-Development environment. " +
                    "This bypasses ALL RBAC checks and must not be used in production. " +
                    "Set IsQuickDebug=false or run with ASPNETCORE_ENVIRONMENT=Development.");
                throw new InvalidOperationException(
                    "[WTM Security] IsQuickDebug=true is not allowed outside of the Development environment. " +
                    "It bypasses all RBAC checks (WtmAuthorizationService, PrivilegeFilter). " +
                    "Set IsQuickDebug=false in your production configuration.");
            }

            // WTM-SEC-0xx (#859): IsFilePublic=true makes PrivilegeFilter.cs treat
            // /_Framework/GetFile and /_Framework/ViewFile as anonymous (isPublic short-circuit,
            // before the LoginUserInfo==null check). Unlike IsQuickDebug above, this does NOT
            // throw: IsFilePublic has a legitimate use (a genuinely public file store) and some
            // operators may have turned it on deliberately — an upgrade that crashes on boot is a
            // worse outcome than one that shouts. LogCritical only, so the operator can make an
            // informed call. Combined with FileUploadOptions.EnforceTenantFileScope=false (opt-out
            // as of #859; it was the pre-#859 default), this also exposes every OTHER tenant's file
            // content to the same unauthenticated caller — the log message says so explicitly.
            if (configs.IsFilePublic == true && env != null && !env.IsDevelopment())
            {
                var logger = app.ApplicationServices.GetService<ILoggerFactory>()
                    ?.CreateLogger("WTM.Security");
                logger?.LogCritical(
                    "[WTM Security] IsFilePublic=true in a non-Development environment. " +
                    "This makes /_Framework/GetFile and /_Framework/ViewFile accessible without " +
                    "authentication — any caller who knows or guesses a FileAttachment GUID can " +
                    "read its content. If FileUploadOptions.EnforceTenantFileScope is also set to " +
                    "false, this includes files belonging to OTHER tenants, not just your own. " +
                    "Verify this is intentional (a genuinely public file store) before deploying; " +
                    "otherwise set IsFilePublic=false.");
            }

            //获取所有程序集
            //var mvc = GetRuntimeAssembly("WalkingTec.Mvvm.Mvc");
            //if (mvc != null && gd.AllAssembly.Contains(mvc) == false)
            //{
            //    gd.AllAssembly.Add(mvc);
            //}
            //var core = GetRuntimeAssembly("WalkingTec.Mvvm.Core");
            //if (core != null && gd.AllAssembly.Contains(core) == false)
            //{
            //    gd.AllAssembly.Add(core);
            //}
            //var layui = GetRuntimeAssembly("WalkingTec.Mvvm.TagHelpers.LayUI");
            //if (layui != null && gd.AllAssembly.Contains(layui) == false)
            //{
            //    gd.AllAssembly.Add(layui);
            //}

            //set Core's _Callerlocalizer to use localizer point to the EntryAssembly's Program class
            Type programType = null;
            if (lop?.LocalizationType == null)
            {
                programType = Assembly.GetCallingAssembly()?.GetTypes()?.Where(x => x.Name == "Program").FirstOrDefault();
            }
            else
            {
                programType = lop.LocalizationType;
            }
            var programLocalizer = localfactory.Create(programType);
            Core.CoreProgram._localizer = programLocalizer;

            // Issue #791: give static utility classes (PropertyHelper, Utils, TypeExtension,
            // CS, JSON converters, QuartzHostService, ...) a way to emit diagnostic logs
            // instead of silently swallowing exceptions in bare catch blocks.
            Core.CoreProgram._loggerFactory = app.ApplicationServices.GetService<ILoggerFactory>();

            // Issue #776: Layui:Asset=legacy selects the deprecated vendored layui 2.6.3
            // asset tree, whose table.js historically built <td data-content="..."> from
            // the raw cell value with no double-quote escaping (attribute-breakout XSS).
            // The vendored copy in this repo now carries the upstream-parity fix, but
            // "legacy" remains a real downstream rollback path — a future re-vendor of the
            // 2.6.3 tree could silently reintroduce the unpatched construction. Warn once
            // at startup (not per page render — LayuiAssets.ResolveLayuiBase is called on
            // every render and must stay warning-free) so operators see the recommendation
            // to use the default /layui-next (2.13.8) tree, which does not have this issue.
            if (LayuiAssets.ResolveLayuiBase(app.ApplicationServices.GetService<IConfiguration>()) == "/layui")
            {
                Core.CoreProgram.GetLogger("FrameworkServiceExtension")?.LogWarning(
                    "[WTM Security] Layui:Asset=legacy is configured, selecting the deprecated " +
                    "vendored layui 2.6.3 asset tree. See Issue #776 (data-content attribute XSS " +
                    "in that tree's table.js, patched upstream-parity in this build). Consider " +
                    "migrating off Layui:Asset=legacy to the default /layui-next (2.13.8) tree.");
            }

            var controllers = gd.GetTypesAssignableFrom<IBaseController>();
            var test = app.ApplicationServices.GetService<ISpaStaticFileProvider>();
            gd.IsSpa = isspa == true || test != null;
            gd.AllModule = GetAllModules(controllers);
            var modules = Utils.ResetModule(gd.AllModule, false);
            gd.CustomUserType = gd.GetPocoTypesAssignableFrom<FrameworkUserBase>().Where(x => x.Name.ToLower() == "frameworkuser").FirstOrDefault();
            gd.SetMenuGetFunc(() =>
            {
                List<SimpleMenu> menus = [];
                var cache = app.ApplicationServices.GetRequiredService<IDistributedCache>();
                var menuCacheKey = nameof(GlobalData.AllMenus);
                if (cache.TryGetValue(menuCacheKey, out List<SimpleMenu> rv) == false)
                {

                    var data = GetAllMenus(modules, configs.IsQuickDebug, configs.Connections);
                    cache.Add(menuCacheKey, data, new DistributedCacheEntryOptions() { AbsoluteExpirationRelativeToNow = new TimeSpan(1, 0, 0) });
                    menus = data;
                }
                else
                {
                    menus = rv;
                }

                return menus;
            });
            gd.SetTenantGetFunc(() =>
            {
                List<FrameworkTenant> tenants = [];
                var cache = app.ApplicationServices.GetRequiredService<IDistributedCache>();
                var tenantsCacheKey = nameof(GlobalData.AllTenant);
                if (cache.TryGetValue(tenantsCacheKey, out tenants) == false)
                {
                    tenants = [];
                    if (configs?.EnableTenant == true)
                    {
                        var csDefault = configs.Connections.FirstOrDefault(x => string.Equals(x.Key, "default", StringComparison.OrdinalIgnoreCase));
                        if (csDefault == null)
                        {
                            Core.CoreProgram.GetLogger("FrameworkServiceExtension")?.LogWarning("EnableTenant is true but no 'default' connection is configured; tenant list will be empty.");
                        }
                        else
                        using (var dc = csDefault.CreateDC())
                        {
                            var cusTenantType = gd.GetPocoTypesAssignableFrom<FrameworkTenant>().FirstOrDefault();
                            if (cusTenantType != null)
                            {
                                var set = dc.GetType().GetMethod("Set", Type.EmptyTypes).MakeGenericMethod(cusTenantType);
                                var q = set.Invoke(dc, null) as IQueryable<FrameworkTenant>;
                                // IgnoreQueryFilters: bootstrap global tenant list across filter scopes
                                tenants = q.IgnoreQueryFilters().Where(x => x.Enabled).ToList();
                            }
                            // IgnoreQueryFilters: bootstrap global tenant list across filter scopes
                            var _all = dc.Set<FrameworkTenant>().IgnoreQueryFilters().Where(x => x.Enabled).ToList();
                            foreach (var item in _all)
                            {
                                if(tenants.Any(x=>x.ID == item.ID) == false)
                                {
                                    tenants.Add(item);
                                }
                            }
                            tenants = tenants.OrderBy(x => x.CreateTime).ToList();
                            foreach (var item in tenants)
                            {
                                if (string.IsNullOrEmpty(item.TDomain) == false)
                                {
                                    Regex r = MvcRegexes.BaseUrlDomainRegex();
                                    var m = r.Match(item.TDomain);
                                    if (m.Success)
                                    {
                                        item.TDomain = m.Groups[2].Value;
                                    }
                                }
                                item.Attributes = new Dictionary<string, object>();
                                if (cusTenantType != null)
                                {
                                    var cuspros = cusTenantType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(x => x.PropertyType.IsListOf<TopBasePoco>() == false && typeof(TopBasePoco).IsAssignableFrom(x.PropertyType) == false).ToList();
                                    foreach (var pro in cuspros)
                                    {
                                        if (item.Attributes.ContainsKey(pro.Name) == false)
                                        {
                                            try
                                            {
                                                item.Attributes.Add(pro.Name, pro.GetValue(item));
                                            }
                                            catch (Exception ex)
                                            {
                                                Core.CoreProgram.GetLogger("FrameworkServiceExtension")?.LogDebug(ex, "Custom tenant attribute getter threw for property '{Property}'", pro.Name);
                                            }
                                        }
                                    }

                                }

                            }
                        }
                    }
                    if (tenants == null)
                    {
                        tenants = [];
                    }
                    cache.Add(tenantsCacheKey, tenants, new DistributedCacheEntryOptions() { AbsoluteExpirationRelativeToNow = new TimeSpan(1, 0, 0) });
                }
                return tenants;
            });
            foreach (var m in gd.AllModule)
            {
                if (isspa == false && m.IsApi == true)
                {
                    if (m.ModuleName.ToLower().EndsWith("api") == false)
                    {
                        m.ModuleName += "Api";
                    }
                }
                foreach (var a in m.Actions)
                {
                    string u = null;
                    if (a.ParasToRunTest != null && a.ParasToRunTest.Any(x => x.ToLower() == "id"))
                    {
                        u = lg.GetPathByAction(a.MethodName, m.ClassName, new { id = 0, area = m.Area?.AreaName });
                    }
                    else
                    {
                        u = lg.GetPathByAction(a.MethodName, m.ClassName, new { area = m.Area?.AreaName });
                    }
                    if (u != null && (u.EndsWith("/0")))
                    {
                        u = u.Substring(0, u.Length - 2);
                        if (m.IsApi == true)
                        {
                            u = u + "/{id}";
                        }
                    }
                    if (u != null && (u.ToLower().EndsWith("?id=0")))
                    {
                        u = u[0..^5];
                        if (m.IsApi == true)
                        {
                            u = u + "/{id}";
                        }
                    }

                    a.Url = u;
                }
            }

            gd.AllAccessUrls = gd.AllModule.SelectMany(x => x.Actions).Where(x => x.IgnorePrivillege == true || x.Module.IgnorePrivillege == true).Select(x => x.Url).ToList();
            gd.AllMainTenantOnlyUrls = gd.AllModule.SelectMany(x => x.Actions).Where(x => x.MainHostOnly == true || x.Module.MainHostOnly == true).Select(x => x.Url).ToList();
            WtmFileProvider.Init(configs, gd);
            using (var scope = app.ApplicationServices.CreateScope())
            {
                var fixdc = scope.ServiceProvider.GetRequiredService<IDataContext>();
                if (fixdc is NullContext)
                {
                    var cs = configs.Connections;
                    foreach (var item in cs)
                    {
                        var dc = item.CreateDC();
                        // TODO: UseWtmContext is a sync IApplicationBuilder extension; GetAwaiter().GetResult() used at startup
                        dc.DataInit(gd.AllModule, isspa == true || test != null).GetAwaiter().GetResult();
                    }
                }
                else
                {
                    // TODO: UseWtmContext is a sync IApplicationBuilder extension; GetAwaiter().GetResult() used at startup
                    fixdc.DataInit(gd.AllModule, isspa == true || test != null).GetAwaiter().GetResult();
                }

            }
            return app;
        }
        public static IApplicationBuilder UseWtmMultiLanguages(this IApplicationBuilder app)
        {
            var configs = app.ApplicationServices.GetRequiredService<IOptionsMonitor<Configs>>().CurrentValue;
            // SupportLanguages always returns at least one entry even when Languages is empty/whitespace.
            // We still gate on IsNullOrWhiteSpace so that callers who skip multi-language entirely
            // (i.e. do not call UseWtmMultiLanguages) are not affected.
            if (!string.IsNullOrWhiteSpace(configs.Languages))
            {
                // SupportLanguages always returns at least one entry (falls back to zh/en when unset).
                // Setting DefaultRequestCulture explicitly ensures an unconfigured-culture
                // request degrades to the first supported culture rather than failing.
                var cultures = configs.SupportLanguages;
                app.UseRequestLocalization(new RequestLocalizationOptions
                {
                    DefaultRequestCulture = new RequestCulture(cultures[0]),
                    SupportedCultures = cultures,
                    SupportedUICultures = cultures,
                });
                System.Threading.Thread.CurrentThread.CurrentCulture = cultures[0];
                System.Threading.Thread.CurrentThread.CurrentUICulture = cultures[0];
            }
            return app;
        }
        public static IApplicationBuilder UseWtmCrossDomain(this IApplicationBuilder app)
        {
            var configs = app.ApplicationServices.GetRequiredService<IOptionsMonitor<Configs>>().CurrentValue;
            if (configs.CorsOptions.EnableAll == true)
            {
                if (configs.CorsOptions?.Policy?.Count > 0)
                {
                    app.UseCors(configs.CorsOptions.Policy[0].Name);
                }
                else
                {
                    app.UseCors("_donotusedefault");
                }
            }
            return app;
        }

        public static IApplicationBuilder UseWtmStaticFiles(this IApplicationBuilder app)
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                RequestPath = new PathString("/_js"),
                FileProvider = new EmbeddedFileProvider(
                    typeof(_CodeGenController).GetTypeInfo().Assembly,
                    "WalkingTec.Mvvm.Mvc")
            });
            return app;
        }


        public static IApplicationBuilder UseWtmSwagger(this IApplicationBuilder app, bool showInDebugOnly = true)
        {
            var configs = app.ApplicationServices.GetRequiredService<IOptions<Configs>>().Value;
            if (configs.IsQuickDebug == true || showInDebugOnly == false)
            {
                app.UseSwagger();
                app.UseSwaggerUI(c =>
                {
                    c.SwaggerEndpoint("/swagger/v1/swagger.json", "My API V1");
                });
            }
            return app;
        }

        public static IApplicationBuilder UseReact(this IApplicationBuilder app)
        {
            var env = app.ApplicationServices.GetService<IWebHostEnvironment>();
            app.UseSpaStaticFiles();
            app.UseSpa(spa =>
            {
                spa.Options.SourcePath = "ClientApp";
                if (env.IsDevelopment())
                {
                    spa.UseReactDevelopmentServer(npmScript: "start");
                }
            });

            return app;
        }

        /// <summary>
        /// Registers WTM health check services. Call this in <c>ConfigureServices</c> / <c>builder.Services</c>.
        /// <para>
        /// A built-in "self" liveness check is registered automatically.
        /// Use the <paramref name="configure"/> callback to add database, cache, or custom checks:
        /// </para>
        /// <code>
        /// services.AddWtmHealthChecks(checks =>
        ///     checks.AddDbContextCheck&lt;MyDataContext&gt;("database", tags: new[] { "ready" }));
        /// </code>
        /// </summary>
        public static IServiceCollection AddWtmHealthChecks(
            this IServiceCollection services,
            Action<IHealthChecksBuilder>? configure = null)
        {
            var builder = services
                .AddHealthChecks()
                .AddCheck("self",
                    () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(),
                    tags: new[] { "live" });

            configure?.Invoke(builder);
            return services;
        }

        /// <summary>
        /// Maps WTM health check endpoints. Call this in <c>Configure</c> / after <c>app.UseRouting()</c>.
        /// <list type="bullet">
        ///   <item><term><paramref name="livePath"/></term><description>Liveness probe — returns 200 if the process is alive (default: <c>/healthz</c>).</description></item>
        ///   <item><term><paramref name="readyPath"/></term><description>Readiness probe — runs all registered checks (default: <c>/healthz/ready</c>).</description></item>
        ///   <item><term><paramref name="useJsonResponse"/></term><description>When <c>true</c>, responses include per-check duration / description / exception as JSON (<see cref="WtmHealthCheckResponseWriter.WriteJsonResponse"/>). Default <c>false</c> to preserve the ASP.NET Core plain-text writer — opt in via <c>app.UseWtmHealthChecks(useJsonResponse: true)</c> (#836).</description></item>
        /// </list>
        /// </summary>
        public static IApplicationBuilder UseWtmHealthChecks(
            this IApplicationBuilder app,
            string livePath = "/healthz",
            string readyPath = "/healthz/ready",
            bool useJsonResponse = false)
        {
            var liveOptions = new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            };
            var readyOptions = new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
            {
                Predicate = _ => true
            };

            if (useJsonResponse)
            {
                // Resolve the host environment to decide whether to include raw exception
                // messages in the health-check JSON. In development environments the full
                // exception message is useful for diagnostics; in production it is redacted
                // to prevent leaking connection strings or hostnames to unauthenticated callers.
                var env = app.ApplicationServices.GetService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
                var includeExceptionDetail = env?.IsDevelopment() ?? false;
                var writer = WtmHealthCheckResponseWriter.CreateWriter(includeExceptionDetail);
                liveOptions.ResponseWriter = writer;
                readyOptions.ResponseWriter = writer;
            }

            app.UseHealthChecks(livePath, liveOptions);
            app.UseHealthChecks(readyPath, readyOptions);

            return app;
        }

        /// <summary>
        /// JWT lifetime validator used in the primary auth pipeline.
        /// Rejects tokens that have no <c>exp</c> claim or whose <c>nbf</c> claim
        /// is in the future (with a small clock-skew tolerance). Exposed as
        /// <c>internal static</c> so unit tests can verify the logic directly.
        /// </summary>
        internal static bool ValidateJwtLifetime(
            DateTime? notBefore,
            DateTime? expires,
            SecurityToken securityToken,
            TokenValidationParameters validationParameters)
        {
            // A token with no `exp` claim is rejected (matches Microsoft default when
            // RequireExpirationTime=true). Pre-`nbf` tokens are also rejected.
            var now = DateTime.UtcNow;
            var skew = validationParameters?.ClockSkew ?? TimeSpan.FromSeconds(5);
            if (expires == null) return false;
            if (notBefore.HasValue && notBefore.Value > now + skew) return false;
            return expires.Value > now - skew;
        }

        /// <summary>
        /// OPT-IN: 替換 Lookup Cache 後端為 <see cref="IDistributedCache"/>（例如 Redis）。
        /// <para>
        /// 呼叫本方法後，框架會以 <see cref="WalkingTec.Mvvm.Core.Cache.DistributedLookupCacheService"/>
        /// 取代預設的 in-memory <see cref="WalkingTec.Mvvm.Core.Cache.LookupCacheService"/>。
        /// 主機必須先注冊 <see cref="IDistributedCache"/> 實作（如 <c>services.AddStackExchangeRedisCache(...)</c>），
        /// 且本方法須在 <c>AddWtmContext()</c> 之後呼叫，否則 override 無效。
        /// </para>
        /// <para>
        /// 未呼叫本方法時行為與原先完全相同（仍使用 in-memory backend）。
        /// </para>
        /// <example>
        /// <code>
        /// // Program.cs
        /// builder.Services.AddWtmContext(config);
        /// builder.Services.AddStackExchangeRedisCache(opts =>
        ///     opts.Configuration = builder.Configuration["Redis:ConnectionString"]);
        /// builder.Services.AddWtmDistributedLookupCache();
        /// </code>
        /// </example>
        /// </summary>
        public static IServiceCollection AddWtmDistributedLookupCache(
            this IServiceCollection services)
        {
            // Guard: AddWtmContext must be called first to register ILookupCacheService.
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(WalkingTec.Mvvm.Core.Cache.ILookupCacheService));
            if (descriptor == null)
                throw new InvalidOperationException("AddWtmDistributedLookupCache() must be called after AddWtmContext().");

            // Override the singleton registered by AddWtmContext with the distributed implementation.
            // We add a new singleton descriptor; ASP.NET Core DI resolves the last registered
            // descriptor for a given service type when GetService / GetRequiredService is called,
            // so this effectively replaces the in-memory service.
            services.AddSingleton<WalkingTec.Mvvm.Core.Cache.ILookupCacheService>(sp =>
                new WalkingTec.Mvvm.Core.Cache.DistributedLookupCacheService(
                    sp.GetRequiredService<IDistributedCache>(),
                    AppDomain.CurrentDomain.GetAssemblies(),
                    sp.GetService<WalkingTec.Mvvm.Core.Cache.LookupCacheOptions>(),
                    sp.GetService<Microsoft.Extensions.Logging.ILogger<
                        WalkingTec.Mvvm.Core.Cache.DistributedLookupCacheService>>()));
            return services;
        }

        /// <summary>
        /// Issue #827: registers a host-supplied per-caller authorization policy for
        /// <c>_FrameworkController</c>'s five resource hooks (<c>CanExportVm</c>,
        /// <c>CanAccessFile</c>, <c>CanPreviewDelete</c>, <c>CanImportVm</c>,
        /// <c>CanEditProperty</c>) — see <see cref="IWtmFrameworkEndpointAuthorizer"/>'s own
        /// doc comment for why a DI-resolved policy, rather than a subclass override, is the
        /// only way to reach a production <c>/_Framework/*</c> request.
        /// <para>
        /// Registered <c>AddScoped</c>, deliberately not <c>AddSingleton</c>: a real policy will
        /// typically query <see cref="WalkingTec.Mvvm.Core.WTMContext.LoginUserInfo"/> and/or the
        /// database, and a singleton registration would create a captive dependency on that
        /// request-scoped state (the same shape <see cref="IWtmAuthorizationService"/>
        /// deliberately does NOT need, since it takes all of its inputs as explicit parameters).
        /// </para>
        /// <para>
        /// Calling this method is entirely OPT-IN. Not calling it (the default) leaves every
        /// hook's decision exactly where it was before Issue #827: driven solely by the matching
        /// <c>Enforce*Authorization</c> config flag (or, for <c>CanEditProperty</c>, an
        /// unconditional allow — it has no flag). Must be called after <c>AddWtmContext()</c>.
        /// </para>
        /// <example>
        /// <code>
        /// // Program.cs
        /// builder.Services.AddWtmContext(config);
        /// builder.Services.AddWtmFrameworkEndpointAuthorizer&lt;MyFrameworkPolicy&gt;();
        /// </code>
        /// </example>
        /// </summary>
        public static IServiceCollection AddWtmFrameworkEndpointAuthorizer<T>(this IServiceCollection services)
            where T : class, IWtmFrameworkEndpointAuthorizer
        {
            services.AddScoped<IWtmFrameworkEndpointAuthorizer, T>();
            return services;
        }

    }


}
