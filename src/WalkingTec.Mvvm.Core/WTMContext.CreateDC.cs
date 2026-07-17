#nullable enable
using System;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace WalkingTec.Mvvm.Core
{
    public partial class WTMContext
    {
        private bool _isCreatingDC;

        #region CreateDC
        public virtual IDataContext? CreateDC(bool isLog = false, string? cskey = null, bool logerror = true)
        {
            bool isReentrant = _isCreatingDC;
            if (!isReentrant)
            {
                _isCreatingDC = true;
            }

            try
            {
                string? cs = cskey ?? CurrentCS;
                string? tenantCode = null;

                var tenants = GlobaInfo?.AllTenant ?? [];
                string? tc = _loginUserInfo?.CurrentTenant;
                // Security fix (#116): Referer-based tenant resolution is only safe for
                // unauthenticated requests. An authenticated user's tenant must come solely
                // from their identity/claims; consulting the attacker-controlled Referer
                // header for authenticated requests allows cross-tenant data access.
                // When DisableRefererTenantResolution is true, skip Referer routing entirely.
                //
                // Robustness note: _loginUserInfo is lazily populated by the LoginUserInfo
                // getter only after that getter is first accessed. To prevent a timing
                // window where CreateDC() runs before the getter is called on an authenticated
                // principal, we ALSO check the request principal directly. Any authenticated
                // principal (HttpContext.User.Identity.IsAuthenticated == true) is excluded
                // from Referer-based routing regardless of whether _loginUserInfo has been
                // resolved yet.
                if (_loginUserInfo == null
                    && HttpContext?.User?.Identity?.IsAuthenticated != true
                    && ConfigInfo?.DisableRefererTenantResolution != true
                    && HttpContext?.Request.Headers.ContainsKey("Referer") == true)
                {
                    Regex r = new Regex("(http://|https://)?(.+?)(/)?$");
                    var m = r.Match(HttpContext?.Request.Headers["Referer"]);
                    string dom = "";
                    if (m.Success)
                    {
                        dom = m.Groups[2].Value;
                    }
                    tc = tenants.Where(x => x.TDomain != null && x.TDomain.ToLower() == dom.ToLower()).Select(x => x.TCode).FirstOrDefault();
                }
                if (tc != null)
                {
                    var item = tenants.Where(x => x.TCode == tc).FirstOrDefault();
                    tenantCode = tc;
                    //如果租户指定了数据库，则返回
                    if (string.IsNullOrEmpty(cs) && item?.IsUsingDB == true)
                    {
                        var tenantDc = item.CreateDC(this);
                        if(tenantDc != null) tenantDc.CurrentUserCode = isReentrant ? _loginUserInfo?.ITCode : LoginUserInfo?.ITCode;
                        return tenantDc;
                    }
                }

                if (isLog == true)
                {
                    if (ConfigInfo?.Connections?.Where(x => x.Key.ToLower() == "defaultlog").FirstOrDefault() != null)
                    {
                        cs = "defaultlog";
                    }
                }
                if (string.IsNullOrEmpty(cs))
                {
                    cs = "default";
                }
                var csConfig = ConfigInfo?.Connections.Where(x => x.Key.ToLower() == cs.ToLower()).FirstOrDefault();
                if (csConfig != null && !csConfig.Enabled)
                {
                    throw new InvalidOperationException($"Database connection '{csConfig.Key}' ({csConfig.DbType}) is disabled. Enable it in appsettings.json (set Enabled: true).");
                }
                var rv = csConfig?.CreateDC();
                if(rv!=null) rv.IsDebug = ConfigInfo?.IsQuickDebug == true;
                rv?.SetTenantCode(tenantCode);
                if(rv != null) rv.CurrentUserCode = isReentrant ? _loginUserInfo?.ITCode : LoginUserInfo?.ITCode;
                if (logerror == true)
                {
                    rv?.SetLoggerFactory(_loggerFactory);
                }
                TrySetDataContextTimeProvider(rv);
                return rv;
            }
            finally
            {
                if (!isReentrant)
                {
                    _isCreatingDC = false;
                }
            }
        }

        #endregion
    }
}
