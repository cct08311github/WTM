using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.LayUI.E2EHost.Instrumentation;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.LayUI.E2EHost
{
    public class Startup
    {
        public IConfiguration ConfigRoot { get; }

        public Startup(IWebHostEnvironment env, IConfiguration config)
        {
            ConfigRoot = config;
            E2ESeed.AdminPassword = config["E2E:AdminPassword"];
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddDistributedMemoryCache();
            services.AddWtmSession(3600, ConfigRoot);
            services.AddWtmCrossDomain(ConfigRoot);
            services.AddWtmAuthentication(ConfigRoot);
            services.AddWtmHttpClient(ConfigRoot);
            services.AddWtmMultiLanguages(ConfigRoot);

            services.AddMvc(options =>
            {
                options.UseWtmMvcOptions();
            })
            .AddJsonOptions(options =>
            {
                options.UseWtmJsonOptions();
            })
            .ConfigureApiBehaviorOptions(options =>
            {
                options.UseWtmApiOptions();
            })
            .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
            .AddWtmDataAnnotationsLocalization(typeof(Program));

            services.AddWtmContext(ConfigRoot, (options) =>
            {
                options.DataPrivileges = new List<IDataPrivilege>();
                options.CsSelector = _ => null;
                options.FileSubDirSelector = _ => null;
                options.ReloadUserFunc = (context, account) => null;
            });

            // test-only: route inventory and request recording, see Instrumentation/
            services.AddE2EInstrumentation(ConfigRoot);
        }

        public void Configure(IApplicationBuilder app, IOptionsMonitor<Configs> configs, IWebHostEnvironment env)
        {
            app.UseExceptionHandler(configs.CurrentValue.ErrorHandler);

            // LayUI / layuiadmin / jQuery assets are shared with the Vue3Demo wwwroot instead of being copied into this project.
            var assets = ResolveStaticAssetsRoot(ConfigRoot, env);
            if (assets != null)
            {
                app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(assets) });
            }
            app.UseWtmStaticFiles();
            app.UseRouting();
            app.UseWtmMultiLanguages();
            app.UseWtmCrossDomain();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseSession();
            app.UseWtm();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllerRoute(
                    name: "areaRoute",
                    pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");
                endpoints.MapControllerRoute(
                    name: "default",
                    pattern: "{controller=Home}/{action=Index}/{id?}");
            });

            app.UseWtmContext();
            app.UseE2EInstrumentation();
        }

        /// <summary>
        /// Finds the directory that holds the shared LayUI static files. Order: configuration key E2E:StaticAssetsRoot,
        /// then a walk up from the application and content directories looking for demo/WalkingTec.Mvvm.Vue3Demo/wwwroot.
        /// </summary>
        private static string ResolveStaticAssetsRoot(IConfiguration config, IWebHostEnvironment env)
        {
            var configured = config["E2E:StaticAssetsRoot"];
            if (string.IsNullOrWhiteSpace(configured) == false && Directory.Exists(configured))
            {
                return Path.GetFullPath(configured);
            }
            foreach (var start in new[] { AppContext.BaseDirectory, env.ContentRootPath })
            {
                var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start);
                while (dir != null)
                {
                    var candidate = Path.Combine(dir.FullName, "demo", "WalkingTec.Mvvm.Vue3Demo", "wwwroot");
                    if (Directory.Exists(Path.Combine(candidate, "layui")))
                    {
                        return candidate;
                    }
                    dir = dir.Parent;
                }
            }
            return null;
        }
    }
}
