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

        #region SelectorAccess

        /// <summary>
        /// MVC-006 opt-out: when true, the Selector endpoint is accessible without authentication
        /// (legacy / public-kiosk mode).  Default is false — the secure default added in 10.6.x.
        /// Set to true in appsettings.json only if your deployment requires unauthenticated selector access.
        /// </summary>
        public bool AllowUnauthenticatedSelector { get; set; } = false;

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

        private bool? _useLegacyTenantSwitchAuthorization;

        /// <summary>
        /// Issue #1007 kill switch. When true, <see cref="WTMContext.SetCurrentTenant(string?)"/>
        /// reverts, byte-for-byte, to its pre-10.23.0 admission rule: a tenant switch is admitted
        /// whenever the caller is a host (<c>TenantCode == null</c>), the request equals the
        /// caller's own current tenant code, OR <c>GlobalData.AllTenant</c> contains ANY row
        /// (<c>.Any(...)</c>, not a uniqueness-checked single match) whose <c>TCode</c> equals the
        /// request and whose parent <c>TenantCode</c> equals the caller's own tenant code. That
        /// legacy rule does not require the requested code to resolve uniquely, does not consult
        /// <see cref="WalkingTec.Mvvm.Core.Services.IWtmTenantSwitchPolicy"/> at all, and allows a
        /// host caller to switch into ANY code — including one absent from, or duplicated in,
        /// <c>AllTenant</c>.
        /// Default: false (the narrowed #1007 admission rule is enforced). Set to true only as a
        /// migration escape hatch — e.g. a federation front end whose local <c>AllTenant</c> does
        /// not know about a main-host-only tenant code, or while cleaning up duplicate/missing
        /// tenant records the narrowed rule now refuses. Deprecated: scheduled for removal in the
        /// minor version after next.
        /// </summary>
        public bool UseLegacyTenantSwitchAuthorization
        {
            get
            {
                return _useLegacyTenantSwitchAuthorization ?? false;
            }
            set
            {
                _useLegacyTenantSwitchAuthorization = value;
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

        /// <summary>
        /// Default cultures used when <see cref="Languages"/> is empty or produces no valid entries.
        /// </summary>
        internal static readonly string[] DefaultFallbackLanguages = ["zh", "en"];

        private List<CultureInfo>? _supportLanguages;

        /// <summary>
        /// Parsed <see cref="CultureInfo"/> list derived from <see cref="Languages"/>.
        /// Always returns at least one entry: falls back to <c>zh, en</c> when the
        /// configured value is empty, whitespace-only, or produces no non-blank tokens.
        /// </summary>
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
                        var trimmed = lan.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                        {
                            _supportLanguages.Add(new CultureInfo(trimmed));
                        }
                    }

                    // Guard: if no valid culture was parsed, fall back to sensible defaults
                    // so that downstream code that indexes [0] never throws.
                    if (_supportLanguages.Count == 0)
                    {
                        foreach (var fallback in DefaultFallbackLanguages)
                        {
                            _supportLanguages.Add(new CultureInfo(fallback));
                        }
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

        #region RBAC hook enforcement (Issue #796)

        /// <summary>
        /// Opt-in, configuration-only switch for
        /// <c>WalkingTec.Mvvm.Mvc._FrameworkController.CanExportVm</c>.
        /// <para>
        /// <c>GetExportExcel</c>/<c>GetExportExcelStream</c> accept a caller-supplied VM type
        /// name and export ANY registered <c>ListVM</c>; there is no built-in mapping from an
        /// arbitrary VM type back to the menu/<c>FunctionPrivilege</c> that gates the page the
        /// VM normally belongs to (the same VM type can be reused by more than one controller,
        /// or by none), so the framework cannot safely derive that mapping on its own.
        /// </para>
        /// <para>
        /// <b>Default: <c>false</c></b> (unchanged pre-#796 behaviour — the un-overridden hook
        /// allows every export). Set to <c>true</c> to flip the un-overridden hook's default
        /// answer to "deny", turning this into a fail-closed kill switch that can be enabled from
        /// <c>appsettings.json</c> with no code change: every export through the shared endpoint
        /// returns 403 until the hosting application overrides <c>CanExportVm</c> with real
        /// per-VM policy (e.g. <c>WTMContext.IsAccessable</c> against the VM's own page URL).
        /// This flag does not — and cannot, without a VM-to-URL registry the framework does not
        /// have — grant fine-grained per-VM access by itself; it only changes what "no override"
        /// means. See Issue #796.
        /// </para>
        /// </summary>
        public bool EnforceVmExportAuthorization { get; set; } = false;

        /// <summary>
        /// Opt-in, configuration-only switch for
        /// <c>WalkingTec.Mvvm.Mvc._FrameworkController.CanPreviewDelete</c>.
        /// <c>GetDeletePreview</c> accepts a caller-supplied VM type name plus up to 10 ids and
        /// returns a confirmed-existence oracle and a human-readable label for every row that
        /// exists, for any registered VM, regardless of the caller's privilege over it.
        /// <para>
        /// <b>Default: <c>false</c></b> (unchanged pre-#796 behaviour). Set to <c>true</c> to make
        /// the un-overridden hook deny every preview (fail-closed) until the hosting application
        /// overrides <c>CanPreviewDelete</c> with real per-VM policy. Same rationale and the same
        /// "cannot be safely auto-derived" limitation as
        /// <see cref="EnforceVmExportAuthorization"/> — see Issue #796.
        /// </para>
        /// </summary>
        public bool EnforceDeletePreviewAuthorization { get; set; } = false;

        // Note: the file-access half of #796 (CanAccessFile / EnforceFileAccessAuthorization /
        // GetFile-GetFileName-ViewFile guards) was carved out to issue #814 and lives there.

        #endregion

        #region File-access hook enforcement (Issue #796 / #814)

        /// <summary>
        /// Opt-in, configuration-only switch for
        /// <c>WalkingTec.Mvvm.Mvc._FrameworkController.CanAccessFile</c>.
        /// <c>FileAttachment</c> carries no owner/uploader column, so the framework cannot
        /// enforce row-level file ownership by default without a schema migration.
        /// <para>
        /// <b>Default: <c>false</c></b> (unchanged pre-#796 behaviour — any authenticated caller
        /// who knows a file id can fetch it). Set to <c>true</c> to make the un-overridden hook
        /// deny every file access (fail-closed) until the hosting application overrides
        /// <c>CanAccessFile</c> with a real ownership scheme (e.g. a join table, or an uploader id
        /// stashed in <c>FileAttachment.ExtraInfo</c>). Combine with
        /// <see cref="FileUploadOptions.EnforceTenantFileScope"/> for the tenant-boundary half of
        /// this gap.
        /// </para>
        /// <para>
        /// See Issue #814 for the current gated endpoint list (<c>GetFile</c>,
        /// <c>GetFileName</c>, <c>ViewFile</c>, <c>DoImport</c>'s uploaded-template read/delete)
        /// and Issue #811 for the real per-caller ownership fix this flag stands in for.
        /// </para>
        /// </summary>
        public bool EnforceFileAccessAuthorization { get; set; } = false;

        #endregion

        #region VM import hook enforcement (Issue #818)

        /// <summary>
        /// Opt-in, configuration-only switch for
        /// <c>WalkingTec.Mvvm.Mvc._FrameworkController.CanImportVm</c>.
        /// <c>DoImport</c> accepts a caller-supplied VM type name and, if it implements
        /// <c>IWtmImportable</c>, calls <c>BatchSaveData()</c> to bulk-insert into whatever entity
        /// that VM imports; there is no built-in mapping from an arbitrary VM type back to the
        /// menu/<c>FunctionPrivilege</c> that gates the page the VM normally belongs to (the same
        /// VM type can be reused by more than one controller, or by none), so the framework
        /// cannot safely derive that mapping on its own.
        /// <para>
        /// <b>Default: <c>false</c></b> (unchanged pre-#818 behaviour — the un-overridden hook
        /// allows every import). Set to <c>true</c> to flip the un-overridden hook's default
        /// answer to "deny", turning this into a fail-closed kill switch that can be enabled from
        /// <c>appsettings.json</c> with no code change: every import through the shared endpoint
        /// returns 403 until the hosting application overrides <c>CanImportVm</c> with real
        /// per-VM policy. Same rationale and the same "cannot be safely auto-derived" limitation
        /// as <see cref="EnforceVmExportAuthorization"/>. See Issue #818.
        /// </para>
        /// <para>
        /// This flag covers VM-level authorization only. It does not cover the uploaded template
        /// file (<c>UploadFileId</c>, gated separately by <see cref="EnforceFileAccessAuthorization"/>
        /// / <c>CanAccessFile</c>, tracked as Issue #814), nor <c>BaseVM.DeletedFileIds</c> —
        /// processed inside <c>BatchSaveData</c> and not gated by any hook, tracked as Issue #815.
        /// </para>
        /// </summary>
        public bool EnforceVmImportAuthorization { get; set; } = false;

        #endregion

        #region Request-binding scope enforcement (Issue #867)

        /// <summary>
        /// Security default-on kill switch for
        /// <c>WalkingTec.Mvvm.Mvc.BaseController.RedoUpdateModel</c> /
        /// <c>WalkingTec.Mvvm.Mvc.BaseApiController.RedoUpdateModel</c> — the reflection-based
        /// binder that copies every caller-supplied form/query key onto a <c>BaseVM</c> for the
        /// five <c>_FrameworkController</c> endpoints backing LayUI DataTable grids
        /// (<c>Selector</c>, <c>GetPagingData</c>, <c>GetExportExcel</c>,
        /// <c>GetExportExcelStream</c>, <c>DoImport</c>).
        /// <para>
        /// <b>Default: <c>true</c>.</b> When <c>true</c>, each key is checked with
        /// <c>WalkingTec.Mvvm.Core.RequestBindingPolicy.IsPathAllowed</c> before being written:
        /// a dotted path that traverses a member declared on <c>BaseVM</c>/<c>BaseSearcher</c>
        /// that is itself a gateway into a larger object graph (<c>Wtm</c>, <c>ConfigInfo</c>,
        /// <c>LoginUserInfo</c>, <c>DC</c>, …), any member declared on <c>WTMContext</c>, any
        /// <c>static</c> member, or a path deeper than 3 segments is rejected and logged
        /// (Warning level, key sanitized via <c>LogSanitizer</c>) instead of written. Without
        /// this, one authenticated low-privilege caller's form field — e.g.
        /// <c>ConfigInfo.IsQuickDebug=true</c> — reaches the process-wide
        /// <c>IOptionsMonitor&lt;Configs&gt;.CurrentValue</c> singleton and flips a security
        /// setting for every user of the running process until restart. See Issue #867 and the
        /// CHANGELOG's #867 entry for the full exploit chain and the reachable-target survey this
        /// allowlist is built from.
        /// </para>
        /// <para>
        /// <b>Set to <c>false</c> only if a downstream <c>BaseVM</c>/<c>BaseSearcher</c>
        /// subclass genuinely needs a deeper or gateway-crossing dotted-path binding this policy
        /// would otherwise reject</b> — the framework's own designed binding surface
        /// (<c>Searcher.*</c>, <c>Ids</c>, <c>Searcher.SortInfo.Property</c>) never needs one, so
        /// this should stay <c>true</c> for the overwhelming majority of deployments.
        /// </para>
        /// </summary>
        public bool EnforceRequestBindingScope { get; set; } = true;

        #endregion

    }
}
