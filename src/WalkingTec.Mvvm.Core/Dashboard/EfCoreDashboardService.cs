#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Helper;

namespace WalkingTec.Mvvm.Core.Dashboard;

/// <summary>
/// DB-backed <see cref="IDashboardService"/> powered by EF Core.
/// Suitable for multi-node (load-balanced) deployments.
/// Opt-in via <see cref="DashboardServiceCollectionExtensions.AddWtmEfDashboardStore"/>.
/// </summary>
public class EfCoreDashboardService : IDashboardService
{
    private readonly IDbContextFactory<DashboardDbContext> _dbFactory;
    private readonly DashboardOptions _options;
    private readonly IEnumerable<IWidgetDataSource> _dataSources;
    private readonly ILogger<EfCoreDashboardService> _logger;
    private readonly TimeProvider _timeProvider;

    public EfCoreDashboardService(
        IDbContextFactory<DashboardDbContext> dbFactory,
        IOptions<DashboardOptions> options,
        IEnumerable<IWidgetDataSource> dataSources,
        ILogger<EfCoreDashboardService> logger,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _options = options.Value;
        _dataSources = dataSources;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // ── CRUD ─────────────────────────────────────────────────────────────────

    public async Task<DashboardDefinition?> GetAsync(string dashboardId, string? tenantId = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // BUG-FIX (Finding 1): scope lookup by tenantId so cross-tenant reads return null.
        // Mirrors JsonFileDashboardService which resolves a per-tenant file path — a caller
        // supplying the wrong tenantId simply gets "file not found" (null).
        var rec = await db.DashboardRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == dashboardId &&
                (r.TenantId == tenantId || (tenantId == null && r.TenantId == null)));

        if (rec == null) return null;

        var widgets = await db.WidgetRecords
            .AsNoTracking()
            .Where(w => w.DashboardId == dashboardId)
            .ToListAsync();

        return RecordToDefinition(rec, widgets);
    }

    public async Task<IReadOnlyList<DashboardSummary>> ListAsync(
        string userId, string[] userRoles, string? tenantId = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var query = db.DashboardRecords.AsNoTracking();

        if (tenantId != null)
        {
            query = query.Where(r => r.TenantId == null || r.TenantId == tenantId);
        }

        var records = await query.ToListAsync();
        var isAdmin = userRoles != null &&
                      _options.AdminRoles.Any(r => userRoles.Contains(r, StringComparer.OrdinalIgnoreCase));

        var result = new List<DashboardSummary>();
        foreach (var r in records)
        {
            var sharing = BuildSharing(r);
            var isOwner = string.Equals(r.Owner, userId, StringComparison.OrdinalIgnoreCase);
            var isPublic = string.Equals(r.SharingMode, "public", StringComparison.OrdinalIgnoreCase);
            var hasRole = userRoles != null &&
                          sharing.Roles?.Any(role => userRoles.Contains(role, StringComparer.OrdinalIgnoreCase)) == true;

            if (isAdmin || isOwner || isPublic || hasRole)
            {
                result.Add(new DashboardSummary
                {
                    Id = r.Id,
                    Title = r.Title,
                    Owner = r.Owner,
                    TenantId = r.TenantId,
                    Sharing = sharing,
                    UpdatedAt = r.UpdatedAt
                });
            }
        }

        return result;
    }

    public async Task<string> CreateAsync(DashboardDefinition dashboard)
    {
        // Validate widget configs at write time (same rule as JsonFileDashboardService).
        var configError = ValidateWidgetConfigs(dashboard);
        if (configError != null)
            throw new ArgumentException(configError, nameof(dashboard));

        if (string.IsNullOrEmpty(dashboard.Id))
            dashboard.Id = Guid.NewGuid().ToString("N");

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        dashboard.CreatedAt = now;
        dashboard.UpdatedAt = now;

        await using var db = await _dbFactory.CreateDbContextAsync();

        var exists = await db.DashboardRecords.AnyAsync(r => r.Id == dashboard.Id);
        if (exists)
            throw new InvalidOperationException($"Dashboard {dashboard.Id} already exists.");

        var rec = DefinitionToRecord(dashboard);
        db.DashboardRecords.Add(rec);

        foreach (var (wid, wdef) in dashboard.Widgets)
        {
            db.WidgetRecords.Add(WidgetToRecord(dashboard.Id, wid, wdef));
        }

        await db.SaveChangesAsync();
        return dashboard.Id;
    }

    public async Task UpdateAsync(DashboardDefinition dashboard)
    {
        if (string.IsNullOrEmpty(dashboard.Id))
            throw new ArgumentException("Dashboard ID cannot be null or empty.", nameof(dashboard));

        var configError = ValidateWidgetConfigs(dashboard);
        if (configError != null)
            throw new ArgumentException(configError, nameof(dashboard));

        dashboard.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

        await using var db = await _dbFactory.CreateDbContextAsync();

        // BUG-FIX (Finding 2): scope lookup by tenantId to prevent cross-tenant write.
        // Without this a caller can overwrite a different tenant's dashboard simply by knowing
        // its ID. The tenant equality predicate matches JsonFileDashboardService's per-tenant
        // directory check: null tenantId means the "_default" bucket.
        var rec = await db.DashboardRecords.FirstOrDefaultAsync(r =>
            r.Id == dashboard.Id &&
            (r.TenantId == dashboard.TenantId || (dashboard.TenantId == null && r.TenantId == null)));
        if (rec == null)
            throw new KeyNotFoundException($"Dashboard {dashboard.Id} not found.");

        ApplyDefinitionToRecord(dashboard, rec);
        db.DashboardRecords.Update(rec);

        // Replace all widget rows atomically: delete existing + re-insert.
        var existing = await db.WidgetRecords
            .Where(w => w.DashboardId == dashboard.Id)
            .ToListAsync();
        db.WidgetRecords.RemoveRange(existing);

        foreach (var (wid, wdef) in dashboard.Widgets)
        {
            db.WidgetRecords.Add(WidgetToRecord(dashboard.Id, wid, wdef));
        }

        await db.SaveChangesAsync();
    }

    public Task DeleteAsync(string dashboardId)
        // Tenant-unaware overload: delegate to the full implementation with tenantId = null
        // (deletes only from the "default" bucket — callers that need tenant isolation must
        // use the tenant-aware overload).
        => DeleteAsync(dashboardId, tenantId: null);

    // BUG-FIX (Finding 3): implement the tenant-aware DeleteAsync DIM from IDashboardService.
    // The tenant-unaware overload now delegates here. Callers that supply the wrong tenantId
    // get a no-op (idempotent), matching JsonFileDashboardService's "file not found → skip" behaviour.
    public async Task DeleteAsync(string dashboardId, string? tenantId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var rec = await db.DashboardRecords.FirstOrDefaultAsync(r =>
            r.Id == dashboardId &&
            (r.TenantId == tenantId || (tenantId == null && r.TenantId == null)));
        if (rec == null) return; // idempotent — wrong tenant or already deleted

        // Widgets cascade-delete via FK, but we remove explicitly for providers without FK cascade.
        var widgets = await db.WidgetRecords.Where(w => w.DashboardId == dashboardId).ToListAsync();
        db.WidgetRecords.RemoveRange(widgets);
        db.DashboardRecords.Remove(rec);

        await db.SaveChangesAsync();
    }

    // ── Widget data ───────────────────────────────────────────────────────────

    /// <summary>Original (backward-compatible) 4-arg overload.</summary>
    public Task<WidgetDataResult> GetWidgetDataAsync(
        string dashboardId, string widgetId,
        Dictionary<string, string>? filters = null,
        CancellationToken ct = default)
        => GetWidgetDataAsync(dashboardId, widgetId, filters, null, ct);

    /// <summary>Tenant-aware overload — delegates logic here; 4-arg overload forwards with null.</summary>
    public async Task<WidgetDataResult> GetWidgetDataAsync(
        string dashboardId, string widgetId,
        Dictionary<string, string>? filters, string? tenantId,
        CancellationToken ct = default)
    {
        var dashboard = await GetAsync(dashboardId, tenantId);
        if (dashboard == null)
            throw new KeyNotFoundException($"Dashboard {dashboardId} not found.");

        if (dashboard.Widgets == null || !dashboard.Widgets.TryGetValue(widgetId, out var widget))
            throw new KeyNotFoundException($"Widget {widgetId} not found on dashboard {dashboardId}.");

        var sourceName = widget.Source?.Name;
        if (string.IsNullOrEmpty(sourceName))
            sourceName = widget.Source?.Kind;
        if (string.IsNullOrEmpty(sourceName))
            throw new InvalidOperationException($"Widget {widgetId} has no data source name specified.");

        var source = _dataSources.FirstOrDefault(
            ds => string.Equals(ds.Name, sourceName, StringComparison.OrdinalIgnoreCase));
        if (source == null)
            throw new InvalidOperationException($"Data source {sourceName} not found.");

        // Bridge structured WidgetSourceDefinition fields into request parameters
        // (identical to JsonFileDashboardService — kept in sync deliberately).
        var parameters = new Dictionary<string, string>(filters ?? new Dictionary<string, string>());
        var widgetSource = widget.Source;
        if (widgetSource != null)
        {
            if (!string.IsNullOrEmpty(widgetSource.ListVmType) && !parameters.ContainsKey("listVmType"))
                parameters["listVmType"] = widgetSource.ListVmType;
            if (widgetSource.Dimensions != null && !parameters.ContainsKey("dimensions"))
                parameters["dimensions"] = JsonSerializer.Serialize(
                    widgetSource.Dimensions.Select(d => d.Field).ToList());
            if (widgetSource.Measures != null && !parameters.ContainsKey("measures"))
                parameters["measures"] = JsonSerializer.Serialize(widgetSource.Measures);
            if (widgetSource.Filters != null && !parameters.ContainsKey("filters"))
            {
                var filterConditions = new List<FilterCondition>(widgetSource.Filters.Count);
                foreach (var f in widgetSource.Filters)
                {
                    if (!FilterConfig.AllowedOps.Contains(f.Op))
                        throw new InvalidOperationException(
                            $"Widget filter has unsupported operator '{f.Op}'. " +
                            $"Allowed operators: {string.Join(", ", FilterConfig.AllowedOps)}.");

                    var op = f.Op.ToLowerInvariant() switch
                    {
                        "eq"          => FilterOperator.Eq,
                        "ne"          => FilterOperator.NotEq,
                        "gt"          => FilterOperator.Gt,
                        "ge"          => FilterOperator.Gte,
                        "lt"          => FilterOperator.Lt,
                        "le"          => FilterOperator.Lte,
                        "contains"    => FilterOperator.Contains,
                        "notcontains" => FilterOperator.NotContains,
                        "in"          => FilterOperator.In,
                        "notin"       => FilterOperator.NotIn,
                        _ => throw new InvalidOperationException(
                            $"Unexpected operator '{f.Op}' after allowlist check.")
                    };
                    filterConditions.Add(new FilterCondition
                    {
                        Field = f.Field, Operator = op, Value = f.Value
                    });
                }
                parameters["filters"] = JsonSerializer.Serialize(filterConditions);
            }

            // SSRF hardening for REST widgets (mirrors JsonFileDashboardService — see that
            // file's GetWidgetDataAsync for the full comment on what "authoritative" does
            // and does not guarantee here: widgetSource.RestOptions is caller data on all
            // three write paths (#948), not a trusted server-side setting; the real
            // enforcement is ValidateWidgetConfigs above plus RestWidgetDataSource's
            // IDashboardEgressPolicy seam, not this block).
            if (string.Equals(sourceName, "rest", StringComparison.OrdinalIgnoreCase))
            {
                if (widgetSource.RestOptions != null)
                {
                    parameters["options"] = JsonSerializer.Serialize(widgetSource.RestOptions);
                }
                else if (parameters.TryGetValue("options", out var requestOptionsJson)
                         && !string.IsNullOrWhiteSpace(requestOptionsJson))
                {
                    // #955 review finding F6 — mirrors JsonFileDashboardService: a
                    // request-supplied "options" blob for a widget with no persisted
                    // RestOptions is no longer honoured at all (was previously capped to
                    // public HTTPS by stripping AllowPrivateNetwork/AllowHttp; since #948
                    // those flags are no longer read at all, so stripping them here is a
                    // no-op and this channel would otherwise ride along on whatever the
                    // registered IDashboardEgressPolicy approves for other widgets, using
                    // the caller's own Url/Method/Headers/Body). See JsonFileDashboardService's
                    // sibling branch for the full rationale — kept in sync deliberately.
                    throw new InvalidOperationException(
                        $"Widget {widgetId} has no persisted RestOptions; a caller-supplied " +
                        "'options' parameter is no longer accepted for REST widgets without " +
                        "persisted RestOptions (issue #955 finding F6). Configure the widget's " +
                        "Source.RestOptions on the dashboard definition instead.");
                }
            }
        }

        // #843: thread tenantId through so background/no-HttpContext callers (AnalysisWidgetDataSource)
        // can scope the DataContext to this widget's own tenant instead of defaulting to
        // WTMContext.CreateDC()'s LoginUserInfo-derived (and, in the background case, always-null) tenant.
        var request = new WidgetDataRequest { Parameters = parameters, TenantId = tenantId };

        var timeoutSeconds = _options.WidgetDataTimeoutSeconds;
        if (timeoutSeconds > 0)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                return await source.GetDataAsync(request, linkedCts.Token);
            }
            catch (OperationCanceledException) when (
                timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "[Dashboard] Widget data fetch timed out after {TimeoutSeconds}s. " +
                    "DashboardId={DashboardId} WidgetId={WidgetId}",
                    timeoutSeconds,
                    LogSanitizer.Sanitize(dashboardId),
                    LogSanitizer.Sanitize(widgetId));

                return new WidgetDataResult
                {
                    Error = $"Widget data fetch timed out after {timeoutSeconds}s."
                };
            }
        }

        return await source.GetDataAsync(request, ct);
    }

    // ── Access helpers ────────────────────────────────────────────────────────

    public bool CanAccess(DashboardDefinition dashboard, string userId, string[] userRoles)
    {
        if (userRoles != null &&
            _options.AdminRoles.Any(r => userRoles.Contains(r, StringComparer.OrdinalIgnoreCase)))
            return true;
        if (string.Equals(dashboard.Owner, userId, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(dashboard.Sharing?.Mode, "public", StringComparison.OrdinalIgnoreCase)) return true;

        if (dashboard.Sharing?.Roles != null && userRoles != null)
        {
            foreach (var role in dashboard.Sharing.Roles)
            {
                if (userRoles.Contains(role, StringComparer.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    public bool CanEdit(DashboardDefinition dashboard, string userId, string[] userRoles)
    {
        if (userRoles != null &&
            _options.AdminRoles.Any(r => userRoles.Contains(r, StringComparer.OrdinalIgnoreCase)))
            return true;
        return string.Equals(dashboard.Owner, userId, StringComparison.OrdinalIgnoreCase);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private string? ValidateWidgetConfigs(DashboardDefinition dashboard)
    {
        if (dashboard.Widgets == null || dashboard.Widgets.Count == 0)
            return null;

        var knownKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Enum.GetNames<WidgetDataSourceKind>())
            knownKinds.Add(name);
        foreach (var ds in _dataSources)
            knownKinds.Add(ds.Name);

        var allowedTypes = _options.AllowedWidgetTypes;

        foreach (var (widgetId, widget) in dashboard.Widgets)
        {
            if (string.IsNullOrWhiteSpace(widget.Type))
                return $"Widget '{widgetId}': 缺少必填的 Type 屬性。";

            if (allowedTypes is { Length: > 0 })
            {
                if (!Array.Exists(allowedTypes,
                    t => string.Equals(t, widget.Type, StringComparison.OrdinalIgnoreCase)))
                    return $"Widget '{widgetId}': chartType '{widget.Type}' 不在允許清單中。" +
                           $"允許的類型：{string.Join(", ", allowedTypes)}.";
            }

            var src = widget.Source;
            if (src == null) continue;

            if (!string.IsNullOrWhiteSpace(src.Kind) &&
                !string.Equals(src.Kind, "custom", StringComparison.OrdinalIgnoreCase) &&
                !knownKinds.Contains(src.Kind))
            {
                return $"Widget '{widgetId}': 資料源 Kind '{src.Kind}' 不是已知的資料源類型。" +
                       $"已知類型：{string.Join(", ", knownKinds)}.";
            }

            if (string.Equals(src.Kind, "analysis", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(src.Name, "analysis", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(src.ListVmType))
                    return $"Widget '{widgetId}': 資料源 Kind 為 'analysis' 時，必須指定 ListVmType。";
            }

            if (string.Equals(src.Kind, "rest", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(src.Name, "rest", StringComparison.OrdinalIgnoreCase))
            {
                if (src.RestOptions != null && string.IsNullOrWhiteSpace(src.RestOptions.Url))
                    return $"Widget '{widgetId}': 資料源 Kind 為 'rest' 且設有 RestOptions 時，必須指定 Url。";

                // #948: mirrors JsonFileDashboardService.ValidateWidgetConfigs — reject
                // (not silently strip) a caller-supplied RestOptions requesting
                // AllowPrivateNetwork/AllowHttp. See that method's comment for the full
                // reject-vs-strip rationale; kept in sync deliberately, same as the rest
                // of this method.
                if (src.RestOptions != null && (src.RestOptions.AllowPrivateNetwork || src.RestOptions.AllowHttp))
                    return $"Widget '{widgetId}': 資料源 Kind 為 'rest' 時，不可在小工具定義中設定 " +
                           $"AllowPrivateNetwork 或 AllowHttp（這些欄位由伺服器端 IDashboardEgressPolicy 決定，" +
                           $"呼叫端無法自行授予私有網路或明文 HTTP 存取權限）。";

                // #955 review finding F5: mirrors JsonFileDashboardService — AllowedPorts
                // lives on the same caller-controlled RestOptions object; a null/empty
                // value turns off RestWidgetDataSource's port allowlist entirely.
                if (src.RestOptions != null && (src.RestOptions.AllowedPorts == null || src.RestOptions.AllowedPorts.Length == 0))
                    return $"Widget '{widgetId}': 資料源 Kind 為 'rest' 時，AllowedPorts 不可為 null 或空陣列" +
                           $"（這會關閉連接埠允許清單，讓呼叫端能探測任意連接埠）。請指定至少一個允許的連接埠。";
            }
        }

        return null;
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static WidgetRecord WidgetToRecord(string dashboardId, string widgetId, WidgetDefinition wdef)
    {
        return new WidgetRecord
        {
            DashboardId = dashboardId,
            WidgetId = widgetId,
            Type = wdef.Type,
            Title = wdef.Title ?? "",
            SourceJson = JsonSerializer.Serialize(wdef.Source ?? new WidgetSourceDefinition()),
            ConfigJson = JsonSerializer.Serialize(
                wdef.Config ?? new System.Collections.Generic.Dictionary<string, object?>()),
            DrillDownJson = wdef.DrillDown is { Count: > 0 }
                ? JsonSerializer.Serialize(wdef.DrillDown)
                : null
        };
    }

    private static DashboardRecord DefinitionToRecord(DashboardDefinition def)
    {
        var rec = new DashboardRecord
        {
            Id = def.Id,
            SchemaVersion = def.SchemaVersion,
            Title = def.Title,
            Owner = def.Owner,
            TenantId = def.TenantId,
            RefreshInterval = def.RefreshInterval,
            CreatedAt = def.CreatedAt,
            UpdatedAt = def.UpdatedAt
        };
        ApplyDefinitionToRecord(def, rec);
        return rec;
    }

    private static void ApplyDefinitionToRecord(DashboardDefinition def, DashboardRecord rec)
    {
        // BUG-FIX (Finding 2 continued): do NOT allow callers to overwrite TenantId or Owner
        // on an existing record. TenantId is assigned at Create time (controller stamps it from
        // the authenticated session) and must be immutable thereafter to prevent tenant-hijack.
        // Owner is similarly controlled: the controller already strips client-supplied Owner on
        // Update and re-stamps it from the fetched record, but we enforce it here as a
        // defence-in-depth measure so the service layer cannot be called with a spoofed Owner.
        // (rec.TenantId and rec.Owner are intentionally left untouched.)
        rec.Title = def.Title;
        rec.RefreshInterval = def.RefreshInterval;
        rec.UpdatedAt = def.UpdatedAt;
        rec.SchemaVersion = def.SchemaVersion;
        rec.SharingMode = def.Sharing?.Mode ?? "private";
        rec.SharingRolesJson = def.Sharing?.Roles is { Count: > 0 }
            ? JsonSerializer.Serialize(def.Sharing.Roles)
            : null;
        rec.LayoutJson = JsonSerializer.Serialize(def.Layout ?? new System.Collections.Generic.List<LayoutItem>());
        rec.FiltersJson = def.Filters is { Count: > 0 }
            ? JsonSerializer.Serialize(def.Filters)
            : null;
        rec.LinksJson = def.Links is { Count: > 0 }
            ? JsonSerializer.Serialize(def.Links)
            : null;
    }

    private static DashboardDefinition RecordToDefinition(DashboardRecord rec, IEnumerable<WidgetRecord> widgetRows)
    {
        var sharing = BuildSharing(rec);
        var layout = rec.LayoutJson != null
            ? JsonSerializer.Deserialize<System.Collections.Generic.List<LayoutItem>>(rec.LayoutJson)
              ?? new System.Collections.Generic.List<LayoutItem>()
            : new System.Collections.Generic.List<LayoutItem>();
        var filters = rec.FiltersJson != null
            ? JsonSerializer.Deserialize<System.Collections.Generic.List<DashboardFilter>>(rec.FiltersJson)
            : null;
        var links = rec.LinksJson != null
            ? JsonSerializer.Deserialize<System.Collections.Generic.List<WidgetLink>>(rec.LinksJson)
            : null;

        var widgets = new System.Collections.Generic.Dictionary<string, WidgetDefinition>();
        foreach (var w in widgetRows)
        {
            var src = JsonSerializer.Deserialize<WidgetSourceDefinition>(w.SourceJson)
                      ?? new WidgetSourceDefinition();
            var cfg = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object?>>(
                          w.ConfigJson)
                      ?? new System.Collections.Generic.Dictionary<string, object?>();
            List<DrillDownLink>? drillDown = null;
            if (!string.IsNullOrEmpty(w.DrillDownJson))
                drillDown = JsonSerializer.Deserialize<List<DrillDownLink>>(w.DrillDownJson);

            widgets[w.WidgetId] = new WidgetDefinition
            {
                Type = w.Type,
                Title = w.Title,
                Source = src,
                Config = cfg,
                DrillDown = drillDown
            };
        }

        return new DashboardDefinition
        {
            Id = rec.Id,
            SchemaVersion = rec.SchemaVersion,
            Title = rec.Title,
            Owner = rec.Owner,
            TenantId = rec.TenantId,
            Sharing = sharing,
            RefreshInterval = rec.RefreshInterval,
            Layout = layout,
            Filters = filters,
            Links = links,
            Widgets = widgets,
            CreatedAt = rec.CreatedAt,
            UpdatedAt = rec.UpdatedAt
        };
    }

    private static SharingDefinition BuildSharing(DashboardRecord rec)
    {
        List<string>? roles = null;
        if (!string.IsNullOrEmpty(rec.SharingRolesJson))
        {
            roles = JsonSerializer.Deserialize<List<string>>(rec.SharingRolesJson);
        }
        return new SharingDefinition { Mode = rec.SharingMode, Roles = roles };
    }
}
