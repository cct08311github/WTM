#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WalkingTec.Mvvm.Core.Services
{
    /// <summary>
    /// Default implementation of <see cref="IWtmDataContextFactory"/>.
    /// Logic is derived from WTMContext.CreateDC (WTMContext.cs lines 886-962)
    /// and FrameworkTenant.CreateDC (FrameworkTenant.cs lines 69-98)
    /// to guarantee identical behaviour.
    /// </summary>
    public class WtmDataContextFactory : IWtmDataContextFactory
    {
        private readonly IOptionsMonitor<Configs> _configs;
        private readonly GlobalData _globalData;
        private readonly ILoggerFactory? _loggerFactory;
        private readonly TimeProvider _timeProvider;

        public WtmDataContextFactory(
            IOptionsMonitor<Configs> configs,
            GlobalData globalData,
            ILoggerFactory? loggerFactory = null,
            TimeProvider? timeProvider = null)
        {
            _configs = configs ?? throw new ArgumentNullException(nameof(configs));
            _globalData = globalData ?? throw new ArgumentNullException(nameof(globalData));
            _loggerFactory = loggerFactory;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        public IDataContext? CreateDC(
            string? currentCs = null,
            string? currentTenant = null,
            string? refererDomain = null,
            string? userCode = null,
            bool isLog = false,
            string? cskey = null,
            bool logerror = true)
        {
            var configInfo = _configs.CurrentValue;
            string? cs = cskey ?? currentCs;
            string? tenantCode = null;

            var tenants = _globalData.AllTenant ?? [];

            // Resolve tenant code: explicit parameter first, then domain-based resolution
            string? tc = currentTenant;
            if (tc == null && !string.IsNullOrEmpty(refererDomain))
            {
                tc = tenants
                    .Where(x => x.TDomain != null && x.TDomain.Equals(refererDomain, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.TCode)
                    .FirstOrDefault();
            }

            if (tc != null)
            {
                var tenantItem = tenants.Where(x => x.TCode == tc).FirstOrDefault();
                tenantCode = tc;

                // If tenant specifies its own DB and no explicit CS key, create tenant-specific DC
                if (string.IsNullOrEmpty(cs) && tenantItem?.IsUsingDB == true)
                {
                    var tenantDc = CreateTenantDC(tenantItem, configInfo);
                    if (tenantDc != null) tenantDc.CurrentUserCode = userCode;
                    return tenantDc;
                }
            }

            if (isLog)
            {
                if (configInfo?.Connections?.Where(x => x.Key.ToLower() == "defaultlog").FirstOrDefault() != null)
                {
                    cs = "defaultlog";
                }
            }

            if (string.IsNullOrEmpty(cs))
            {
                cs = "default";
            }

            var csConfig = configInfo?.Connections?.Where(x => x.Key.ToLower() == cs.ToLower()).FirstOrDefault();
            if (csConfig != null && !csConfig.Enabled)
            {
                throw new InvalidOperationException(
                    $"Database connection '{csConfig.Key}' ({csConfig.DbType}) is disabled. Enable it in appsettings.json (set Enabled: true).");
            }

            var rv = csConfig?.CreateDC();
            if (rv != null) rv.IsDebug = configInfo?.IsQuickDebug == true;
            rv?.SetTenantCode(tenantCode);
            if (rv != null) rv.CurrentUserCode = userCode;
            if (logerror && _loggerFactory != null)
            {
                rv?.SetLoggerFactory(_loggerFactory);
            }
            TrySetTimeProvider(rv);
            return rv;
        }

        /// <summary>
        /// Create a DataContext for a tenant that has its own database.
        /// Replicates FrameworkTenant.CreateDC logic for the IsUsingDB==true branch.
        /// </summary>
        private IDataContext? CreateTenantDC(FrameworkTenant tenant, Configs? configInfo)
        {
            var context = string.IsNullOrEmpty(tenant.DbContext) ? "DataContext" : tenant.DbContext;
            var dcConstructor = CS.CisFull
                .Where(x => x.DeclaringType?.Name.ToLower() == context.ToLower())
                .FirstOrDefault();
            var tenantDc = (IDataContext?)dcConstructor?.Invoke(new object[] { tenant.TDb!, tenant.TDbType! });
            if (tenantDc == null)
            {
                return null;
            }
            tenantDc.IsDebug = configInfo?.IsQuickDebug == true;
            if (_loggerFactory != null)
            {
                tenantDc.SetLoggerFactory(_loggerFactory);
            }
            tenantDc.SetTenantCode(tenant.TCode);
            TrySetTimeProvider(tenantDc);
            return tenantDc;
        }

        private void TrySetTimeProvider(IDataContext? dc)
        {
            if (dc is EmptyContext ctx)
            {
                ctx.TimeProvider = _timeProvider;
            }
        }
    }
}
