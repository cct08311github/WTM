#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core.ConfigOptions;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Configs
    /// </summary>
    public class Configs
    {
        #region ConnectionStrings

        private List<CS>? _connectStrings;

        /// <summary>
        /// ConnectionStrings
        /// </summary>
        public List<CS> Connections
        {
            get
            {
                if (_connectStrings == null)
                {
                    _connectStrings = [];
                }
                return _connectStrings;
            }
            set
            {
                _connectStrings = value;
            }
        }

        #endregion

        #region Domains

        private Dictionary<string, Domain>? _domains;

        /// <summary>
        /// ConnectionStrings
        /// </summary>
        public Dictionary<string, Domain> Domains
        {
            get
            {
                if (_domains == null)
                {
                    _domains = new Dictionary<string, Domain>();
                }
                return _domains;
            }
            set
            {
                _domains = new Dictionary<string, Domain>();
                foreach (var domain in value)
                {
                    _domains.Add(domain.Key.ToLower(), domain.Value);
                }
                foreach (var item in _domains)
                {
                    if (item.Value != null)
                    {
                        item.Value.Name = item.Key;
                    }
                }
            }
        }

        private bool? _hasMainHost;
        public bool HasMainHost
        {
            get
            {
                if (_hasMainHost == null)
                {
                    _mainHost = Domains?.Where(x => x.Key.ToLower() == "mainhost").Select(x => x.Value.Address).FirstOrDefault();
                    _hasMainHost = !string.IsNullOrEmpty(_mainHost);
                }
                return _hasMainHost == true;
            }
        }

        private string? _mainHost;
        public string? MainHost
        {
            get
            {
                _mainHost = Domains?.Where(x => x.Key.ToLower() == "mainhost").Select(x => x.Value.Address).FirstOrDefault();
                if(_mainHost != null)
                {
                    _mainHost = _mainHost.Trim();
                    if(_mainHost.EndsWith('/') || _mainHost.EndsWith('\\'))
                    {
                        _mainHost = _mainHost[0..^1];
                    }
                }
                return _mainHost;
            }
        }
        #endregion


        #region QuickDebug

        private bool? _isQuickDebug;

        /// <summary>
        /// Is debug mode
        /// </summary>
        public bool IsQuickDebug
        {
            get
            {
                return _isQuickDebug ?? false;
            }
            set
            {
                _isQuickDebug = value;
            }
        }

        #endregion

        #region Tenant

        private bool? _enableTenant;

        /// <summary>
        /// Is debug mode
        /// </summary>
        public bool EnableTenant
        {
            get
            {
                return _enableTenant ?? false;
            }
            set
            {
                _enableTenant = value;
            }
        }

        #endregion

        #region DisableRefererTenantResolution

        private bool? _disableRefererTenantResolution;

        /// <summary>
        /// When true, disables Referer-based tenant resolution entirely — including for
        /// unauthenticated requests.  Set to true in security-strict deployments where
        /// tenants are always identified through identity claims or explicit configuration
        /// rather than the HTTP Referer header.
        /// Default: false (Referer routing applies only to unauthenticated requests).
        /// </summary>
        public bool DisableRefererTenantResolution
        {
            get
            {
                return _disableRefererTenantResolution ?? false;
            }
            set
            {
                _disableRefererTenantResolution = value;
            }
        }

        #endregion


        public string ErrorHandler { get; set; } = "/_Framework/Error";

        #region Cookie prefix

        private string? _cookiePre;

        /// <summary>
        /// Cookie prefix
        /// </summary>
        public string CookiePre
        {
            get
            {
                return _cookiePre ?? string.Empty;
            }
            set
            {
                _cookiePre = value;
            }
        }

        #endregion

        #region PageMode

        private PageModeEnum? _pageMode;

        /// <summary>
        /// PageMode
        /// </summary>
        public PageModeEnum PageMode
        {
            get
            {
                if (_pageMode == null)
                {
                    _pageMode = PageModeEnum.Single;
                }
                return _pageMode.Value;
            }
            set
            {
                _pageMode = value;
            }
        }
        #endregion

        #region TabMode

        private TabModeEnum? _tabMode;

        /// <summary>
        /// TabMode
        /// </summary>
        public TabModeEnum TabMode
        {
            get
            {
                if (_tabMode == null)
                {
                    _tabMode = TabModeEnum.Default;
                }
                return _tabMode.Value;
            }
            set
            {
                _tabMode = value;
            }
        }
        #endregion

        #region BlazorMode

        private BlazorModeEnum? _blazorMode;

        /// <summary>
        /// TabMode
        /// </summary>
        public BlazorModeEnum BlazorMode
        {
            get
            {
                if (_blazorMode == null)
                {
                    _blazorMode = BlazorModeEnum.Server;
                }
                return _blazorMode.Value;
            }
            set
            {
                _blazorMode = value;
            }
        }
        #endregion


        #region Custom settings

        private Dictionary<string, string>? _appSettings;

        /// <summary>
        /// Custom settings
        /// </summary>
        public Dictionary<string, string> AppSettings
        {
            get
            {
                if (_appSettings == null)
                {
                    _appSettings = new Dictionary<string, string>();
                }
                return _appSettings;
            }
            set
            {
                _appSettings = value;
            }
        }

        #endregion

        #region FileOptions

        private FileUploadOptions? _fileUploadOptions;

        /// <summary>
        /// FileOptions
        /// </summary>
        public FileUploadOptions FileUploadOptions
        {
            get
            {
                if (_fileUploadOptions == null)
                {
                    _fileUploadOptions = new FileUploadOptions()
                    {
                        UploadLimit = DefaultConfigConsts.DEFAULT_UPLOAD_LIMIT,
                        SaveFileMode = "database",
                        Settings = new Dictionary<string, List<FileHandlerOptions>>()
                    };
                }
                return _fileUploadOptions;
            }
            set
            {
                _fileUploadOptions = value;
            }
        }

        #endregion

        #region UIOptions

        private UIOptions? _uiOptions;

        /// <summary>
        /// UIOptions
        /// </summary>
        public UIOptions UIOptions
        {
            get
            {
                if (_uiOptions == null)
                {
                    _uiOptions = new UIOptions();
                    if (_uiOptions.DataTable == null)
                        _uiOptions.DataTable = new UIOptions.DataTableOptions
                        {
                            RPP = DefaultConfigConsts.DEFAULT_RPP
                        };

                    if (_uiOptions.ComboBox == null)
                        _uiOptions.ComboBox = new UIOptions.ComboBoxOptions
                        {
                            DefaultEnableSearch = DefaultConfigConsts.DEFAULT_COMBOBOX_DEFAULT_ENABLE_SEARCH
                        };

                    if (_uiOptions.DateTime == null)
                        _uiOptions.DateTime = new UIOptions.DateTimeOptions
                        {
                            DefaultReadonly = DefaultConfigConsts.DEFAULT_DATETIME_DEFAULT_READONLY
                        };

                    if (_uiOptions.SearchPanel == null)
                        _uiOptions.SearchPanel = new UIOptions.SearchPanelOptions
                        {
                            DefaultExpand = DefaultConfigConsts.DEFAULT_SEARCHPANEL_DEFAULT_EXPAND
                        };
                }
                return _uiOptions;
            }
            set
            {
                _uiOptions = value;
            }
        }

        #endregion

        #region Is FileAttachment public

        private bool? _isFilePublic;

        /// <summary>
        /// Is FileAttachment public
        /// </summary>
        public bool IsFilePublic
        {
            get
            {
                return _isFilePublic ?? false;
            }
            set
            {
                _isFilePublic = value;
            }
        }

        #endregion

        #region UEditorOptions

        private UEditorOptions? _ueditorOptions;

        /// <summary>
        /// UEditor配置
        /// </summary>
        /// <value></value>
        public UEditorOptions UEditorOptions
        {
            get
            {
                if (_ueditorOptions == null)
                {
                    _ueditorOptions = new UEditorOptions();
                }
                return _ueditorOptions;
            }
            set
            {
                _ueditorOptions = value;
            }
        }
        #endregion

        #region Cors configs

        private Cors? _cors;

        /// <summary>
        ///  Cors configs
        /// </summary>
        public Cors CorsOptions
        {
            get
            {
                if (_cors == null)
                {
                    _cors = new Cors();
                    _cors.Policy = [];
                }
                return _cors;
            }
            set
            {
                _cors = value;
            }
        }

        #endregion

        #region Support Languages

        private string? _languages;

        /// <summary>
        /// Support Languages
        /// </summary>
        public string Languages
        {
            get
            {
                if (string.IsNullOrEmpty((_languages)))
                {
                    _languages = "zh";
                }
                return _languages;
            }
            set
            {
                _languages = value;
            }
        }

        private List<CultureInfo>? _supportLanguages;
        public List<CultureInfo> SupportLanguages
        {
            get
            {
                if (_supportLanguages == null)
                {
                    _supportLanguages = [];
                    var lans = Languages.Split(",");
                    foreach (var lan in lans)
                    {
                        _supportLanguages.Add(new CultureInfo(lan));
                    }

                }
                return _supportLanguages;
            }
        }

        #endregion

        public string HostRoot { get; set; } = "";


        #region CookieOption configs

        private CookieOption? _cookieOption;

        /// <summary>
        ///  Cors configs
        /// </summary>
        public CookieOption CookieOptions
        {
            get
            {
                if (_cookieOption == null)
                {
                    _cookieOption = new CookieOption();
                }
                return _cookieOption;
            }
            set
            {
                _cookieOption = value;
            }
        }

        #endregion

        #region JwtOption configs

        private JwtOption? _jwtOption;

        /// <summary>
        ///  Cors configs
        /// </summary>
        public JwtOption JwtOptions
        {
            get
            {
                if (_jwtOption == null)
                {
                    _jwtOption = new JwtOption();
                }
                return _jwtOption;
            }
            set
            {
                _jwtOption = value;
            }
        }

        #endregion

        #region Analysis Cache TTL

        private TimeSpan? _analysisCacheTtl;

        /// <summary>
        /// Analysis query cache TTL.  Defaults to 5 minutes.
        /// Set to TimeSpan.Zero to disable caching at the engine level.
        /// </summary>
        public TimeSpan AnalysisCacheTtl
        {
            get => _analysisCacheTtl ?? TimeSpan.FromMinutes(5);
            set => _analysisCacheTtl = value;
        }

        #endregion

        #region TrustForwardedForHeader

        private bool? _trustForwardedForHeader;

        /// <summary>
        /// When <c>true</c>, <c>HttpContextExtention.GetRemoteIpAddress</c>
        /// reads the raw <c>X-Forwarded-For</c> header first (legacy / back-compat behaviour).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Default: <c>false</c> (secure).</b> With the default, client IP is resolved
        /// from <c>HttpContext.Connection.RemoteIpAddress</c> — the verified TCP peer address —
        /// so an attacker cannot spoof IP-based controls such as maintenance-mode allow-lists,
        /// rate-limit partitions, CSP-report buckets, or <c>WtmIpAllowListAttribute</c>.
        /// </para>
        /// <para>
        /// <b>For reverse-proxy deployments</b> (nginx, Kestrel behind a load balancer, etc.)
        /// the <em>recommended</em> migration path is to call
        /// <c>services.AddWtmForwardedHeaders()</c> and <c>app.UseWtmForwardedHeaders()</c>
        /// with your <c>KnownProxies</c>/<c>KnownNetworks</c> configured.  ASP.NET Core's
        /// built-in <c>ForwardedHeadersMiddleware</c> then rewrites
        /// <c>Connection.RemoteIpAddress</c> to the validated client IP before any WTM
        /// middleware runs, so no code change is needed in business logic.
        /// </para>
        /// <para>
        /// Set to <c>true</c> only as a short-term back-compat measure when adopting
        /// <c>UseWtmForwardedHeaders</c> is not immediately feasible.  Issue #114.
        /// </para>
        /// </remarks>
        public bool TrustForwardedForHeader
        {
            get => _trustForwardedForHeader ?? false;
            set => _trustForwardedForHeader = value;
        }

        #endregion

    }
}
