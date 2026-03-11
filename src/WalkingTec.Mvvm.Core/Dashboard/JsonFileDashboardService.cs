using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace WalkingTec.Mvvm.Core.Dashboard;

public class JsonFileDashboardService : IDashboardService
{
    private readonly DashboardOptions _options;
    private readonly IEnumerable<IWidgetDataSource> _dataSources;
    private readonly ConcurrentDictionary<string, DashboardSummary> _index = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly string _baseDir;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public JsonFileDashboardService(IOptions<DashboardOptions> options, IEnumerable<IWidgetDataSource> dataSources)
    {
        _options = options.Value;
        _dataSources = dataSources;
        // Spec: scan _baseDir
        // Path resolution: Use base directory
        _baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _options.DashboardDirectory);
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
                catch
                {
                    // Ignore malformed files during init
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
        var dir = string.IsNullOrEmpty(tenantId)
            ? Path.Combine(_baseDir, "_default")
            : Path.Combine(_baseDir, tenantId);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{id}.json");
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
        catch
        {
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

        var sourceName = widget.Source?.Name;
        if (string.IsNullOrEmpty(sourceName))
        {
            throw new InvalidOperationException($"Widget {widgetId} has no data source name specified.");
        }

        var source = _dataSources.FirstOrDefault(ds => string.Equals(ds.Name, sourceName, StringComparison.OrdinalIgnoreCase));
        if (source == null)
        {
            throw new InvalidOperationException($"Data source {sourceName} not found.");
        }

        var request = new WidgetDataRequest
        {
            Parameters = filters ?? new Dictionary<string, string>()
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