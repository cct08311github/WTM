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

namespace WalkingTec.Mvvm.Core.Dashboard;

public class JsonFileDashboardService : IDashboardService
{
    private readonly DashboardOptions _options;
    private readonly IEnumerable<IWidgetDataSource> _dataSources;
    private readonly ILogger<JsonFileDashboardService> _logger;
    private readonly ConcurrentDictionary<string, DashboardSummary> _index = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly string _baseDir;
    private volatile bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public JsonFileDashboardService(
        IOptions<DashboardOptions> options,
        IEnumerable<IWidgetDataSource> dataSources,
        ILogger<JsonFileDashboardService> logger)
    {
        _options = options.Value;
        _dataSources = dataSources;
        _logger = logger;
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

        var dir = string.IsNullOrEmpty(tenantId)
            ? Path.Combine(_baseDir, "_default")
            : Path.Combine(_baseDir, tenantId);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var fullPath = Path.GetFullPath(Path.Combine(dir, $"{id}.json"));
        var baseFullPath = Path.GetFullPath(_baseDir);
        if (!fullPath.StartsWith(baseFullPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Path traversal detected in dashboard ID or tenant ID.");

        return fullPath;
    }

    private static void ValidatePathSegment(string value, string paramName)
    {
        if (value.Contains("..") || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Invalid characters in {paramName}: '{value}'");
    }

    public async Task<DashboardDefinition?> GetAsync(string dashboardId, string? tenantId = null)
    {
        await EnsureInitializedAsync();
        var path = GetFilePath(dashboardId, tenantId);
        if (!File.Exists(path)) return null;

        var lockObj = GetLock(dashboardId);
        await lockObj.WaitAsync();
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<DashboardDefinition>(fs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[Dashboard] Failed to read dashboard file. DashboardId={DashboardId} Path={FilePath}",
                dashboardId, path);
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

        var result = new List<DashboardSummary>();
        var isAdmin = userRoles != null && userRoles.Contains("Admin", StringComparer.OrdinalIgnoreCase);

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

        dashboard.CreatedAt = DateTime.UtcNow;
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

            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(fs, dashboard);

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

        dashboard.UpdatedAt = DateTime.UtcNow;

        var path = GetFilePath(dashboard.Id, dashboard.TenantId);
        var lockObj = GetLock(dashboard.Id);

        await lockObj.WaitAsync();
        try
        {
            if (!File.Exists(path))
            {
                throw new KeyNotFoundException($"Dashboard {dashboard.Id} not found.");
            }

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(fs, dashboard);

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
            _index.TryRemove(dashboardId, out _);
        }
        finally
        {
            lockObj.Release();
        }
    }

    public async Task<WidgetDataResult> GetWidgetDataAsync(string dashboardId, string widgetId, Dictionary<string, string>? filters = null, CancellationToken ct = default)
    {
        var dashboard = await GetAsync(dashboardId);
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
        }

        var request = new WidgetDataRequest
        {
            Parameters = parameters
        };

        return await source.GetDataAsync(request, ct);
    }

    public bool CanAccess(DashboardDefinition dashboard, string userId, string[] userRoles)
    {
        if (userRoles != null && userRoles.Contains("Admin", StringComparer.OrdinalIgnoreCase)) return true;
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
        if (userRoles != null && userRoles.Contains("Admin", StringComparer.OrdinalIgnoreCase)) return true;
        if (string.Equals(dashboard.Owner, userId, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}