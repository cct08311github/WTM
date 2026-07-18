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
            // #721/#727 PITFALL GUARD: this is a safe PLACEHOLDER default, not a usable
            // DataContext. Real apps never touch it — they get their connection-string/
            // tenant-routed DataContext via WTMContext.CreateDC() or IWtmDataContextFactory.
            // Any framework service that resolves IDataContext directly from DI (constructor
            // injection OR explicit GetService<IDataContext>()/GetRequiredService<IDataContext>())
            // gets THIS NullContext instance in every real deployment — its members throw
            // NotImplementedException, and `as DbContext`/explicit casts throw
            // InvalidCastException, both usually swallowed or surfacing far from the call site.
            // Before adding any new IDataContext-consuming service (especially a hosted
            // service/BackgroundService/Quartz job with no per-request WTMContext), resolve the
            // DataContext via WTMContext.CreateDC() or IWtmDataContextFactory.CreateDC() first,
            // falling back to DI IDataContext only for host/test setups that register a real
            // context directly — see TokenService.ResolveDataContext (#721) and
            // WalkingTec.Mvvm.WorkFlow.ServiceCollectionExtensions.ResolveDataContext (#727) for
            // the canonical pattern, and #727's audit table for the full list of sites checked.
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
