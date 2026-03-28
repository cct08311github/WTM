#nullable enable
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Services;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Core.Support.Quartz;

namespace WalkingTec.Mvvm.Core
{
    public static class IServiceExtension
    {
        public static IServiceCollection AddWtmContextForConsole(this IServiceCollection services, string? jsonFileDir = null, string? jsonFileName = null, Func<IWtmFileHandler, string>? fileSubDirSelector = null)
        {
            var configBuilder = new ConfigurationBuilder();
            IConfigurationRoot ConfigRoot = configBuilder.WTMConfig(null,jsonFileDir,jsonFileName).Build();
            var WtmConfigs = ConfigRoot.Get<Configs>()!;
            services.Configure<Configs>(ConfigRoot);
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddConfiguration(ConfigRoot.GetSection("Logging"))
                       .AddConsole()
                       .AddDebug()
                       .AddWTMLogger();
            });
            var gd = GetGlobalData();
            services.AddHttpContextAccessor();
            services.AddSingleton(gd);
            WtmFileProvider._subDirFunc = fileSubDirSelector;
            services.TryAddScoped<IDataContext, NullContext>();
            services.AddScoped<WTMContext>();
            services.AddScoped<WtmFileProvider>();
            services.AddHttpClient();
            if (WtmConfigs.Domains != null)
            {
                foreach (var item in WtmConfigs.Domains)
                {
                    services.AddHttpClient(item.Key, x =>
                    {
                        x.BaseAddress = new Uri(item.Value.Url!);
                        x.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
                        x.DefaultRequestHeaders.Add("User-Agent", "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.2; SV1; .NET CLR 1.1.4322; .NET CLR 2.0.50727)");
                    });
                }
            }
            services.AddDistributedMemoryCache();
            var cs = WtmConfigs.Connections;
            foreach (var item in cs)
            {
                var dc = item.CreateDC()!;
                dc.Database.EnsureCreated();
            }
            WtmFileProvider.Init(WtmConfigs, gd);
            services.TryAddSingleton<QuartzHostService>();

            // Phase 1: Extracted services (parallel to WTMContext, no breaking changes)
            services.AddScoped<IWtmApiClient, WtmApiClient>();
            services.AddScoped<IWtmLogService>(sp => new WtmLogService(
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()));
            services.AddSingleton<IWtmAuthorizationService>(sp => new WtmAuthorizationService(
                sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>() is { } lf1 ? Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<WtmAuthorizationService>(lf1) : null));
            services.AddScoped<IWtmDataContextFactory>(sp => new WtmDataContextFactory(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Configs>>(),
                sp.GetRequiredService<GlobalData>(),
                sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>(),
                sp.GetService<TimeProvider>()));

            // Phase 2-3: Auth, UserCache, Tenant, VmFactory services
            services.AddSingleton<IWtmAuthService, WtmAuthService>();
            services.AddScoped<IWtmUserCacheService>(sp => new WtmUserCacheService(
                sp.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>()));
            services.AddScoped<IWtmTenantService>(sp => new WtmTenantService(
                sp.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Configs>>(),
                sp.GetRequiredService<GlobalData>(),
                sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>() is { } lf2 ? Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<WtmTenantService>(lf2) : null));
            services.AddSingleton<IWtmVmFactory, WtmVmFactory>();

            return services;
        }

        private static GlobalData GetGlobalData()
        {
            GlobalData gd = new GlobalData();
            gd.AllAssembly = Utils.GetAllAssembly();
            return gd;
        }
    }
}
