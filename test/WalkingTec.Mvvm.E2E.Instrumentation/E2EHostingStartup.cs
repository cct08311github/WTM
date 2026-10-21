using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

[assembly: HostingStartup(typeof(WalkingTec.Mvvm.E2E.Instrumentation.E2EHostingStartup))]

namespace WalkingTec.Mvvm.E2E.Instrumentation
{
    /// <summary>
    /// Adds the E2E test instrumentation to a site without changing the site. A process activates it with
    ///   ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=WalkingTec.Mvvm.E2E.Instrumentation
    /// (the assembly must be resolvable by the site, see SiteTarget.HostingStartupDeps in the E2E project).
    /// Everything is opt-in by configuration (environment variables work as well), so the assembly does nothing when it is
    /// loaded without these keys:
    ///   E2E:RouteInventoryPath, E2E:RouteHitLogPath   see <see cref="E2EInstrumentationExtensions"/>
    ///   E2E:SpaRoot                                   directory with a prebuilt single page app (index.html, assets/**) to serve at "/"
    /// </summary>
    public sealed class E2EHostingStartup : IHostingStartup
    {
        public const string SpaRootKey = "E2E:SpaRoot";

        public void Configure(IWebHostBuilder builder)
        {
            builder.ConfigureServices((context, services) =>
            {
                services.AddE2EInstrumentation(context.Configuration);
                services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, E2EStartupFilter>();
            });
        }
    }

    internal sealed class E2EStartupFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        private readonly IConfiguration _config;

        public E2EStartupFilter(IConfiguration config)
        {
            _config = config;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                var spaRoot = _config[E2EHostingStartup.SpaRootKey];
                if (string.IsNullOrWhiteSpace(spaRoot) == false)
                {
                    if (Directory.Exists(spaRoot) == false)
                    {
                        throw new DirectoryNotFoundException(E2EHostingStartup.SpaRootKey + " does not exist: " + spaRoot);
                    }
                    // before everything the site registers: the built SPA is served from its own directory instead of the site's wwwroot
                    var provider = new PhysicalFileProvider(Path.GetFullPath(spaRoot));
                    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
                    app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
                }
                next(app);
                app.UseE2EInstrumentation();
            };
        }
    }
}
