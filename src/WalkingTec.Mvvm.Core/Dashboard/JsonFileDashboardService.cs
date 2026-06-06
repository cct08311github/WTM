using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Helper;

namespace WalkingTec.Mvvm.Core.Dashboard;

public class JsonFileDashboardService : IDashboardService
{
    private readonly DashboardOptions _options;
    private readonly IEnumerable<IWidgetDataSource> _dataSources;
    private readonly ILogger<JsonFileDashboardService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, DashboardSummary> _index = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly string _baseDir;
    private volatile bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // ── Definition cache: file path → (DashboardDefinition, LastWriteTimeUtc) ──
    // Avoids repeated file reads for the same dashboard when the file has not changed.
    // Keyed by resolved file path; staleness guard uses LastWriteTimeUtc.
    // Invalidated on Create, Update, and Delete so readers never see stale data.
    private readonly ConcurrentDictionary<string, (DashboardDefinition Def, DateTimeOffset Stamp)>
        _defCache = new();

    // Shared options for the legacy REST-options JSON strip path (case-insensitive field matching).
    private static readonly JsonSerializerOptions _caseInsensitiveOptions =
        new() { PropertyNameCaseInsensitive = true };

    public JsonFileDashboardService(
        IOptions<DashboardOptions> options,
        IEnumerable<IWidgetDataSource> dataSources,
        ILogger<JsonFileDashboardService> logger,
        TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _dataSources = dataSources;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _options.DashboardDirectory);

        logger.LogWarning(
            "[WTM Dashboard] Using JsonFile storage backend. " +
            "This is NOT suitable for multi-node (load-balanced) deployments. " +
            "Configure a database backend for production use.");
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            if (!Directory.Exists(_baseDir))
            {
                Directory.CreateDirectory(_baseDir);
            }

            // Scan all subdirectories (tenant dirs + _default)
            var files = Directory.GetFiles(_baseDir, "*.json", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var def = JsonSerializer.Deserialize<DashboardDefinition>(json);
                    if (def != null)
                    {
                        _index[def.Id] = new DashboardSummary
                        {
                            Id = def.Id,
                            Title = def.Title,
                            Owner = def.Owner,
                            TenantId = def.TenantId,
                            Sharing = def.Sharing,
                            UpdatedAt = def.UpdatedAt
                        };
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[Dashboard] Skipping malformed dashboard file during init. Path={FilePath}",
                        file);
                }
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private SemaphoreSlim GetLock(string id)
    {
        return _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
    }

    private string GetFilePath(string id, string? tenantId = null)
    {
        ValidatePathSegment(id, nameof(id));
        if (!string.IsNullOrEmpty(tenantId))
            ValidatePathSegment(tenantId, nameof(tenantId));

        // Build the per-tenant subdirectory using SafeCombine so the canonical
        // path check (including separator-awareness) is applied to the tenant
        // segment, then construct the final file path inside that directory.
        var dir = string.IsNullOrEmpty(tenantId)
            ? SafePathHelper.SafeCombine(_baseDir, "_default")
            : SafePathHelper.SafeCombine(_baseDir, tenantId);

        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        // The file name is id + ".json"; SafeCombine validates that the
        // resolved path stays within the per-tenant directory.
        return SafePathHelper.SafeCombine(dir, $"{id}.json");
    }

    private static void ValidatePathSegment(string value, string paramName)
    {
        if (value.Contains("..") || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Invalid characters in {paramName}: '{value}'");
    }

    public async Task<DashboardDefinition?> GetAsync(string dashboardId, string? tenantId = null)
    {
        ValidatePathSegment(dashboardId, nameof(dashboardId));
        if (!string.IsNullOrEmpty(tenantId))
            ValidatePathSegment(tenantId, nameof(tenantId));

        await EnsureInitializedAsync();
        var path = GetFilePath(dashboardId, tenantId);
        if (!File.Exists(path)) return null;

        // Fast-path: return cached definition when the file has not changed.
        var lastWrite = new DateTimeOffset(new FileInfo(path).LastWriteTimeUtc, TimeSpan.Zero);
        if (_defCache.TryGetValue(path, out var cached) && cached.Stamp == lastWrite)
            return cached.Def;

        var lockObj = GetLock(dashboardId);
        await lockObj.WaitAsync();
        try
        {
            if (!File.Exists(path)) return null;

            // Re-check stamp under lock in case another thread just refreshed the cache.
            lastWrite = new DateTimeOffset(new FileInfo(path).LastWriteTimeUtc, TimeSpan.Zero);
            if (_defCache.TryGetValue(path, out cached) && cached.Stamp == lastWrite)
                return cached.Def;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var def = await JsonSerializer.DeserializeAsync<DashboardDefinition>(fs);
            if (def != null)
                _defCache[path] = (def, lastWrite);
            return def;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[Dashboard] Failed to read dashboard file. DashboardId={DashboardId} Path={FilePath}",
                LogSanitizer.Sanitize(dashboardId), LogSanitizer.Sanitize(path));
            return null;
        }
        finally
        {
            lockObj.Release();
        }
    }

    public async Task<IReadOnlyList<DashboardSummary>> ListAsync(string userId, string[] userRoles, string? tenantId = null)
    {
        await EnsureInitializedAsync();

        List<DashboardSummary> result = [];
        var isAdmin = userRoles != null && _options.AdminRoles.Any(r => userRoles.Contains(r, StringComparer.OrdinalIgnoreCase));

        foreach (var summary in _index.Values)
        {
            // Tenant isolation: only show dashboards from same tenant (or _default)
            if (tenantId != null && summary.TenantId != null &&
                !string.Equals(summary.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var isOwner = string.Equals(summary.Owner, userId, StringComparison.OrdinalIgnoreCase);
            var isPublic = string.Equals(summary.Sharing?.Mode, "public", StringComparison.OrdinalIgnoreCase);
            var hasRole = userRoles != null && summary.Sharing?.Roles?.Any(r => userRoles.Contains(r, StringComparer.OrdinalIgnoreCase)) == true;

            if (isAdmin || isOwner || isPublic || hasRole)
            {
                result.Add(summary);
            }
        }

        return result;
    }

    public async Task<string> CreateAsync(DashboardDefinition dashboard)
    {
        await EnsureInitializedAsync();

        if (string.IsNullOrEmpty(dashboard.Id))
        {
            dashboard.Id = Guid.NewGuid().ToString("N");
        }

        dashboard.CreatedAt = _timeProvider.GetUtcNow().UtcDateTime;
        dashboard.UpdatedAt = dashboard.CreatedAt;

        var path = GetFilePath(dashboard.Id, dashboard.TenantId);
        var lockObj = GetLock(dashboard.Id);

        await lockObj.WaitAsync();
        try
        {
            if (File.Exists(path))
            {
                throw new InvalidOperationException($"Dashboard {dashboard.Id} already exists.");
            }

            // Write via tmp then rename to avoid leaving a partial file on crash.
            var tmpPath = path + ".tmp";
            try
            {
                await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(fs, dashboard);
                }
                File.Move(tmpPath, path);
            }
            catch
            {
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
                throw;
            }

            // Invalidate definition cache so the next GetAsync reads the new file.
            _defCache.TryRemove(path, out _);

            _index[dashboard.Id] = new DashboardSummary
            {
                Id = dashboard.Id,
                Title = dashboard.Title,
                Owner = dashboard.Owner,
                TenantId = dashboard.TenantId,
                Sharing = dashboard.Sharing ?? new SharingDefinition(),
                UpdatedAt = dashboard.UpdatedAt
            };

            return dashboard.Id;
        }
        finally
        {
            lockObj.Release();
        }
    }

    public async Task UpdateAsync(DashboardDefinition dashboard)
    {
        await EnsureInitializedAsync();

        if (string.IsNullOrEmpty(dashboard.Id))
        {
            throw new ArgumentException("Dashboard ID cannot be null or empty.", nameof(dashboard));
        }

        dashboard.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

        var path = GetFilePath(dashboard.Id, dashboard.TenantId);
        var lockObj = GetLock(dashboard.Id);

        await lockObj.WaitAsync();
        try
        {
            if (!File.Exists(path))
            {
                throw new KeyNotFoundException($"Dashboard {dashboard.Id} not found.");
            }

            // Atomic write: serialize to a sibling .tmp file, then atomically replace the target.
            // This prevents readers from ever observing a truncated/partial JSON file if a crash
            // occurs mid-write (File.Replace is backed by a rename(2) on POSIX; on Windows it is
            // an atomic swap at the filesystem level when the source and destination are on the
            // same volume).
            var tmpPath = path + ".tmp";
            try
            {
                await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(fs, dashboard);
                }
                File.Replace(tmpPath, path, null);
            }
            catch
            {
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
                throw;
            }

            // Invalidate definition cache so the next GetAsync reads the updated file.
            _defCache.TryRemove(path, out _);

            _index[dashboard.Id] = new DashboardSummary
            {
                Id = dashboard.Id,
                Title = dashboard.Title,
                Owner = dashboard.Owner,
                TenantId = dashboard.TenantId,
                Sharing = dashboard.Sharing ?? new SharingDefinition(),
                UpdatedAt = dashboard.UpdatedAt
            };
        }
        finally
        {
            lockObj.Release();
        }
    }

    public async Task DeleteAsync(string dashboardId)
    {
        ValidatePathSegment(dashboardId, nameof(dashboardId));

        await EnsureInitializedAsync();

        string? tenantId = null;
        if (_index.TryGetValue(dashboardId, out var summary))
        {
            tenantId = summary.TenantId;
        }
        var path = GetFilePath(dashboardId, tenantId);
        var lockObj = GetLock(dashboardId);

        await lockObj.WaitAsync();
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            // Invalidate definition cache for deleted dashboard.
            _defCache.TryRemove(path, out _);
            _index.TryRemove(dashboardId, out _);
        }
        finally
        {
            lockObj.Release();
        }
    }

    /// <summary>
    /// Original (backward-compatible) 4-arg overload. Delegates to the tenant-aware
    /// overload with <c>tenantId = null</c> so all logic lives in one place.
    /// </summary>
    public Task<WidgetDataResult> GetWidgetDataAsync(string dashboardId, string widgetId, Dictionary<string, string>? filters = null, CancellationToken ct = default)
        => GetWidgetDataAsync(dashboardId, widgetId, filters, null, ct);

    /// <summary>
    /// Tenant-aware overload. Contains the real widget-data logic; the 4-arg overload
    /// forwards here with <c>tenantId = null</c>.
    /// </summary>
    public async Task<WidgetDataResult> GetWidgetDataAsync(string dashboardId, string widgetId, Dictionary<string, string>? filters, string? tenantId, CancellationToken ct = default)
    {
        // Pass tenantId so GetAsync resolves the correct per-tenant storage directory.
        // Without this, tenant-owned dashboards are not found (they live under tenantId/,
        // not _default/) and _default dashboards may be returned to wrong-tenant callers.
        var dashboard = await GetAsync(dashboardId, tenantId);
        if (dashboard == null)
        {
            throw new KeyNotFoundException($"Dashboard {dashboardId} not found.");
        }

        if (dashboard.Widgets == null || !dashboard.Widgets.TryGetValue(widgetId, out var widget))
        {
            throw new KeyNotFoundException($"Widget {widgetId} not found on dashboard {dashboardId}.");
        }

        // Resolve data source name: explicit Name, or fall back to Kind for analysis sources
        var sourceName = widget.Source?.Name;
        if (string.IsNullOrEmpty(sourceName))
        {
            sourceName = widget.Source?.Kind;
        }
        if (string.IsNullOrEmpty(sourceName))
        {
            throw new InvalidOperationException($"Widget {widgetId} has no data source name specified.");
        }

        var source = _dataSources.FirstOrDefault(ds => string.Equals(ds.Name, sourceName, StringComparison.OrdinalIgnoreCase));
        if (source == null)
        {
            throw new InvalidOperationException($"Data source {sourceName} not found.");
        }

        // Bridge structured WidgetSourceDefinition fields into request parameters
        var parameters = new Dictionary<string, string>(filters ?? new Dictionary<string, string>());
        var widgetSource = widget.Source;
        if (widgetSource != null)
        {
            if (!string.IsNullOrEmpty(widgetSource.ListVmType) && !parameters.ContainsKey("listVmType"))
                parameters["listVmType"] = widgetSource.ListVmType;
            if (widgetSource.Dimensions != null && !parameters.ContainsKey("dimensions"))
                parameters["dimensions"] = JsonSerializer.Serialize(widgetSource.Dimensions.Select(d => d.Field).ToList());
            if (widgetSource.Measures != null && !parameters.ContainsKey("measures"))
                parameters["measures"] = JsonSerializer.Serialize(widgetSource.Measures);
            if (widgetSource.Filters != null && !parameters.ContainsKey("filters"))
                parameters["filters"] = JsonSerializer.Serialize(widgetSource.Filters.Select(f => new { f.Field, f.Op, f.Value }).ToList());

            // SSRF hardening (issue #101): for REST widgets, the server-side RestOptions are
            // authoritative. When present, they completely replace any request-supplied "options"
            // parameter so that a caller cannot override security-sensitive fields such as
            // AllowPrivateNetwork or AllowHttp. When RestOptions is absent (legacy widget), fall
            // through but strip AllowPrivateNetwork/AllowHttp from any request-supplied JSON.
            if (string.Equals(sourceName, "rest", StringComparison.OrdinalIgnoreCase))
            {
                if (widgetSource.RestOptions != null)
                {
                    // Authoritative server-side options — overwrite whatever the request sent.
                    parameters["options"] = JsonSerializer.Serialize(widgetSource.RestOptions);
                }
                else if (parameters.TryGetValue("options", out var requestOptionsJson)
                         && !string.IsNullOrWhiteSpace(requestOptionsJson))
                {
                    // Legacy widget: caller-supplied options. Strip the two security-sensitive flags
                    // so that a request can never enable private-network access or plain HTTP.
                    try
                    {
                        var requestOpts = JsonSerializer.Deserialize<RestWidgetDataSourceOptions>(
                            requestOptionsJson,
                            _caseInsensitiveOptions);
                        if (requestOpts != null)
                        {
                            requestOpts.AllowPrivateNetwork = false;
                            requestOpts.AllowHttp = false;
                            parameters["options"] = JsonSerializer.Serialize(requestOpts);
                        }
                    }
                    catch (JsonException)
                    {
                        // Malformed JSON — leave as-is; RestWidgetDataSource.ParseOptions will reject it.
                    }
                }
            }
        }

        var request = new WidgetDataRequest
        {
            Parameters = parameters
        };

        return await source.GetDataAsync(request, ct);
    }

    public bool CanAccess(DashboardDefinition dashboard, string userId, string[] userRoles)
    {
        if (userRoles != null && _options.AdminRoles.Any(r => userRoles.Contains(r, StringComparer.OrdinalIgnoreCase))) return true;
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
        if (userRoles != null && _options.AdminRoles.Any(r => userRoles.Contains(r, StringComparer.OrdinalIgnoreCase))) return true;
        if (string.Equals(dashboard.Owner, userId, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}