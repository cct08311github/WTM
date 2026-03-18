# Dashboard Module — Developer Guide

> WTM 8.x | Last updated: 2026-03-12

The Dashboard module provides drag-and-drop widget dashboards with RBAC-based sharing, multi-tenant isolation, and pluggable data sources (including Analysis Mode integration).

## Quick Start

### 1. Register the dashboard service

In `Program.cs` (or `Startup.cs`):

```csharp
using WalkingTec.Mvvm.Core.Dashboard;

builder.Services.AddWtmDashboard(opts =>
{
    opts.DashboardDirectory = "App_Data/dashboards"; // default
    opts.DefaultRefreshInterval = 60;                // seconds
    opts.EnableEditing = true;
});
```

### 2. Register data sources

```csharp
// Register a specific custom data source
builder.Services.AddWidgetDataSource<SalesKpiDataSource>();

// Or scan an assembly for all IWidgetDataSource implementations
builder.Services.AddWidgetDataSourcesFromAssembly(typeof(Program).Assembly);
```

### 3. Create a dashboard JSON file

Place it under `App_Data/dashboards/_default/{id}.json`:

```json
{
  "SchemaVersion": 1,
  "Id": "sales-overview",
  "Title": "Sales Overview",
  "Owner": "admin",
  "RefreshInterval": 30,
  "Layout": [
    { "Id": "w1", "X": 0, "Y": 0, "W": 4, "H": 1 },
    { "Id": "w2", "X": 4, "Y": 0, "W": 8, "H": 2 }
  ],
  "Widgets": {
    "w1": {
      "Type": "kpi",
      "Title": "Total Revenue",
      "Source": { "Kind": "custom", "Name": "sales-kpi" }
    },
    "w2": {
      "Type": "chart",
      "Title": "Monthly Trend",
      "Source": {
        "Kind": "analysis",
        "ListVmType": "MyApp.ViewModels.OrderListVM",
        "Dimensions": [{ "Field": "OrderDate", "Hierarchy": "month" }],
        "Measures": [{ "Field": "Amount", "Func": "Sum" }]
      }
    }
  }
}
```

The dashboard is now accessible at `/_dashboard/sales-overview`.

---

## Creating Custom Data Sources

Implement `IWidgetDataSource`:

```csharp
public class SalesKpiDataSource : IWidgetDataSource
{
    public string Name => "sales-kpi";
    public WidgetDataSourceKind Kind => WidgetDataSourceKind.Custom;

    private readonly MyDbContext _db;
    public SalesKpiDataSource(MyDbContext db) => _db = db;

    public async Task<WidgetDataResult> GetDataAsync(
        WidgetDataRequest request, CancellationToken ct = default)
    {
        var total = await _db.Orders.SumAsync(o => o.Amount, ct);
        var lastMonth = await _db.Orders
            .Where(o => o.Date >= DateTime.UtcNow.AddMonths(-1))
            .SumAsync(o => o.Amount, ct);

        return new WidgetDataResult
        {
            Value = total,
            PreviousValue = lastMonth
        };
    }
}
```

### WidgetDataResult patterns

**KPI** — single value with optional comparison:
```csharp
new WidgetDataResult { Value = 1234, PreviousValue = 1100 }
```

**Table** — rows with column names:
```csharp
new WidgetDataResult
{
    Columns = new List<string> { "Name", "Revenue", "Region" },
    Rows = new List<Dictionary<string, object?>>
    {
        new() { ["Name"] = "Product A", ["Revenue"] = 5000, ["Region"] = "North" }
    }
}
```

**Progress** — value between 0 and a max:
```csharp
new WidgetDataResult { Value = 75, Metadata = new() { ["max"] = 100 } }
```

**List** — simple item list:
```csharp
new WidgetDataResult
{
    Rows = new List<Dictionary<string, object?>>
    {
        new() { ["label"] = "Task 1", ["status"] = "done" },
        new() { ["label"] = "Task 2", ["status"] = "pending" }
    }
}
```

### Filters

Filters from the dashboard filter bar (and from widget linkage clicks) arrive via `request.Parameters`:

```csharp
public async Task<WidgetDataResult> GetDataAsync(
    WidgetDataRequest request, CancellationToken ct = default)
{
    var query = _db.Orders.AsQueryable();

    if (request.Parameters.TryGetValue("region", out var region))
    {
        query = query.Where(o => o.Region == region);
    }

    // ... build result
}
```

---

## Using Analysis Mode as a Data Source

Any `[EnableAnalysis]` ListVM can power a dashboard widget without writing a custom data source. Configure the widget's `Source` in JSON:

```json
{
  "Type": "chart",
  "Title": "Order Analysis",
  "Source": {
    "Kind": "analysis",
    "ListVmType": "MyApp.ViewModels.OrderListVM",
    "Dimensions": [
      { "Field": "Category" },
      { "Field": "OrderDate", "Hierarchy": "month" }
    ],
    "Measures": [
      { "Field": "Amount", "Func": "Sum" },
      { "Field": "Quantity", "Func": "Count" }
    ],
    "Filters": [
      { "Field": "Status", "Op": "eq", "Value": "Completed" }
    ]
  }
}
```

The `AnalysisWidgetDataSource` (registered automatically when Analysis Mode is available) bridges the request to `AnalysisQueryEngine`. All fields are validated against the Analysis Mode whitelist — no raw SQL injection is possible.

---

## Dashboard-Level Filters

Define filters at the dashboard level. They apply to all widgets that consume matching parameters:

```json
{
  "Filters": [
    {
      "Id": "f1",
      "Field": "region",
      "Label": "Region",
      "Type": "select",
      "Options": ["North", "South", "East", "West"],
      "Default": "North"
    },
    {
      "Id": "f2",
      "Field": "dateRange",
      "Label": "Date Range",
      "Type": "select",
      "Options": ["7d", "30d", "90d"],
      "Default": "30d"
    }
  ]
}
```

When a user changes a filter, the frontend re-fetches data for all widgets, passing the filter values as query parameters.

---

## Widget Linkage

Widgets can trigger actions on other widgets. For example, clicking a KPI can filter a chart:

```json
{
  "Links": [
    {
      "SourceWidget": "w1",
      "Event": "click",
      "TargetWidget": "w2",
      "Action": "filter",
      "Param": "category"
    }
  ]
}
```

When widget `w1` emits a `click` event, widget `w2` re-fetches its data with the `category` parameter set to the clicked value.

---

## Security Model

### Authorization levels

| Role | List | View | Create | Edit | Delete |
|------|------|------|--------|------|--------|
| **Owner** | Own dashboards | Yes | Yes | Yes | Yes |
| **Admin** | All dashboards | Yes | Yes | Yes | Yes |
| **Role-shared** | Dashboards shared to role | Yes | No | No | No |
| **Public** | Public dashboards | Yes | No | No | No |
| **Other** | None | No | No | No | No |

### Sharing modes

Set via `DashboardDefinition.Sharing`:

```json
// Private (default — only owner and admins)
{ "Mode": "private" }

// Public — visible to all authenticated users
{ "Mode": "public" }

// Role-based — visible to specific roles (values must be RoleCode, not RoleName)
{ "Mode": "roles", "Roles": ["analyst", "finance_mgr"] }
```

> **Important — RoleCode vs RoleName**: The `Roles` array must contain `RoleCode` values (the stable machine identifier stored in `FrameworkRole.RoleCode`), **not** `RoleName` (the human-readable display name that can be changed in the admin UI). Using `RoleName` would break access control whenever an admin renames a role.
>
> Example: if the role is displayed as "財務主管" in the admin UI but has `RoleCode = "finance_mgr"`, the JSON must use `"finance_mgr"`.

#### Admin roles

Admin role codes are configured via `DashboardOptions.AdminRoles` (defaults to `["Admin"]`):

```csharp
builder.Services.Configure<DashboardOptions>(opt =>
{
    opt.AdminRoles = new[] { "Admin", "super_admin" }; // add additional admin RoleCodes
});
```

Admin role users bypass all sharing restrictions and can view, edit, and delete any dashboard.

### Multi-tenant isolation

Dashboards are stored in tenant-specific subdirectories:
```
App_Data/dashboards/
  _default/          ← dashboards with no tenant
  tenantA/           ← tenant A's dashboards
  tenantB/           ← tenant B's dashboards
```

The `TenantId` is automatically read from `LoginUserInfo.TenantCode`. Dashboards in one tenant are invisible to other tenants, even for admin users listing dashboards.

---

## API Reference

Base URL: `/_dashboard`

### List dashboards

```
GET /_dashboard/list
```

Returns dashboards visible to the current user (filtered by owner, roles, sharing, and tenant).

**Response:** `200 OK`
```json
[
  {
    "Id": "abc123",
    "Title": "Sales Overview",
    "Owner": "admin",
    "TenantId": null,
    "Sharing": { "Mode": "public", "Roles": null },
    "UpdatedAt": "2026-03-12T10:00:00Z"
  }
]
```

### Get dashboard

```
GET /_dashboard/{id}
```

Returns full dashboard definition including widgets and layout.

**Response:** `200 OK` | `404 Not Found` | `403 Forbidden`

### Create dashboard

```
POST /_dashboard/
Content-Type: application/json

{
  "Title": "My Dashboard",
  "Layout": [...],
  "Widgets": {...},
  "Sharing": { "Mode": "private" }
}
```

`Owner` and `TenantId` are set automatically from the current user.

**Response:** `200 OK` with the new dashboard ID as string

### Update dashboard

```
PUT /_dashboard/{id}
Content-Type: application/json

{ "Id": "abc123", "Title": "Updated Title", ... }
```

Only the owner and admins can update. Owner is preserved from the original.

**Response:** `200 OK` | `404 Not Found` | `403 Forbidden`

### Delete dashboard

```
DELETE /_dashboard/{id}
```

Only the owner and admins can delete.

**Response:** `200 OK` | `404 Not Found` | `403 Forbidden`

### Get widget data

```
GET /_dashboard/{id}/widget/{wid}/data?region=North&dateRange=30d
```

or with POST for complex filters:

```
POST /_dashboard/{id}/widget/{wid}/data
Content-Type: application/json

{ "region": "North", "dateRange": "30d" }
```

**Response:** `200 OK`
```json
{
  "Value": 12345,
  "PreviousValue": 11000,
  "Rows": null,
  "Columns": null,
  "Metadata": null
}
```

### List data sources

```
GET /_dashboard/datasources
```

Returns available widget data sources (custom + analysis).

**Response:** `200 OK`
```json
[
  { "Name": "sales-kpi", "Kind": "Custom" },
  { "Name": "OrderListVM", "Kind": "Analysis", "Fields": [...] }
]
```

---

## Configuration Reference

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `DashboardDirectory` | `string` | `"App_Data/dashboards"` | Directory for dashboard JSON files (relative to app base) |
| `EnableEditing` | `bool` | `true` | Allow dashboard editing via API |
| `DefaultRefreshInterval` | `int` | `60` | Default auto-refresh interval in seconds |
| `AllowIframeSameOrigin` | `bool` | `false` | Allow embed widgets with same-origin iframes |

---

## Frontend Integration

The dashboard frontend is served as an embedded resource at `/_js/framework_dashboard.js` and `/_js/framework_dashboard.css`. It uses [gridstack.js](https://gridstackjs.com/) for drag-and-drop layout.

### Widget types

| Type | Description | Result fields used |
|------|-------------|--------------------|
| `kpi` | Single number with optional trend arrow | `Value`, `PreviousValue` |
| `chart` | ECharts-powered chart (auto-selects bar/line/pie) | `Rows`, `Columns` |
| `table` | Data table with sortable columns | `Rows`, `Columns` |
| `progress` | Progress bar with percentage | `Value`, `Metadata.max` |
| `list` | Simple item list | `Rows` (with `label`, `status` keys) |
| `embed` | Iframe embed (same-origin only unless configured) | `Metadata.url` |

### JavaScript API

```javascript
// Initialize a dashboard
WtmDashboard.DashboardManager.init('sales-overview', document.getElementById('container'));

// Destroy and clean up
WtmDashboard.DashboardManager.destroy();

// Access the event bus
WtmDashboard.EventBus.on('widget:click', 'w1', function(data) { ... });
WtmDashboard.EventBus.off('widget:click', 'w1');

// Filter bar
WtmDashboard.FilterBar.init(container, filters);
var values = WtmDashboard.FilterBar.getValues();
```

---

## 部署注意事項

### JsonFileDashboardService 的限制

預設的 `JsonFileDashboardService` 將儀表板定義存為本機 JSON 檔案（路徑由 `DashboardDirectory` 決定，預設為 `App_Data/dashboards`）。此方式簡單易用，但有以下限制：

#### 多節點 / 負載均衡

**不支援多節點部署**。各節點的 JSON 檔案相互獨立，透過一個節點建立或修改的儀表板，其他節點無法即時看到，最終導致資料不一致。

> 若應用部署在兩台以上的伺服器或使用 Kubernetes 水平擴展，**必須改用資料庫後端**（見下方「遷移至多節點儲存」）。

#### 容器化部署（Docker / Kubernetes）

容器重啟後容器本地檔案系統會被清除。若未掛載 volume，儀表板定義在每次重啟後都會消失。

**必須掛載持久化 volume**，例如：

```yaml
# docker-compose.yml
volumes:
  - ./data/dashboards:/app/App_Data/dashboards
```

```yaml
# Kubernetes Deployment
volumeMounts:
  - name: dashboard-data
    mountPath: /app/App_Data/dashboards
volumes:
  - name: dashboard-data
    persistentVolumeClaim:
      claimName: dashboard-pvc
```

同時確認 `DashboardDirectory` 設定為容器內的絕對路徑或相對於 `ContentRoot` 的路徑：

```csharp
builder.Services.AddWtmDashboard(opts =>
{
    // 容器環境建議使用絕對路徑或 ContentRoot-relative
    opts.DashboardDirectory = Path.Combine(
        builder.Environment.ContentRootPath,
        "App_Data", "dashboards");
});
```

---

### 遷移至多節點儲存

當需要支援多節點或高可用部署時，實作自訂的 `IDashboardService` 將儀表板定義存入關聯式資料庫。

#### 步驟 1：新增 EF Core 實體

```csharp
public class DashboardEntity
{
    public string Id { get; set; }
    public string Owner { get; set; }
    public string? TenantId { get; set; }
    public string DefinitionJson { get; set; }  // 序列化的 DashboardDefinition
    public DateTime UpdatedAt { get; set; }
}
```

#### 步驟 2：實作 IDashboardService

```csharp
public class EfDashboardService : IDashboardService
{
    private readonly DataContext _db;
    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public EfDashboardService(DataContext db) => _db = db;

    public async Task<DashboardDefinition?> GetAsync(string id, string? tenantId = null)
    {
        var entity = await _db.Set<DashboardEntity>()
            .FirstOrDefaultAsync(e => e.Id == id &&
                (tenantId == null || e.TenantId == tenantId));
        return entity == null
            ? null
            : JsonSerializer.Deserialize<DashboardDefinition>(entity.DefinitionJson, _opts);
    }

    public async Task<IReadOnlyList<DashboardSummary>> ListAsync(
        string userId, string[] userRoles, string? tenantId = null)
    {
        // 回傳使用者可存取的儀表板摘要列表
        var entities = await _db.Set<DashboardEntity>()
            .Where(e => tenantId == null || e.TenantId == tenantId)
            .ToListAsync();
        return entities
            .Select(e => JsonSerializer.Deserialize<DashboardDefinition>(e.DefinitionJson, _opts)!)
            .Where(d => CanAccess(d, userId, userRoles))
            .Select(d => new DashboardSummary { Id = d.Id, Title = d.Title, Owner = d.Owner })
            .ToList();
    }

    public async Task<string> CreateAsync(DashboardDefinition dashboard)
    {
        dashboard.Id ??= Guid.NewGuid().ToString("N");
        _db.Set<DashboardEntity>().Add(new DashboardEntity
        {
            Id = dashboard.Id,
            Owner = dashboard.Owner,
            TenantId = dashboard.TenantId,
            DefinitionJson = JsonSerializer.Serialize(dashboard, _opts),
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return dashboard.Id;
    }

    public async Task UpdateAsync(DashboardDefinition dashboard)
    {
        var entity = await _db.Set<DashboardEntity>().FindAsync(dashboard.Id)
            ?? throw new KeyNotFoundException(dashboard.Id);
        entity.DefinitionJson = JsonSerializer.Serialize(dashboard, _opts);
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(string id)
    {
        var entity = await _db.Set<DashboardEntity>().FindAsync(id);
        if (entity != null)
        {
            _db.Set<DashboardEntity>().Remove(entity);
            await _db.SaveChangesAsync();
        }
    }

    public bool CanAccess(DashboardDefinition d, string userId, string[] roles) =>
        d.Owner == userId || d.SharedWith?.Any(s => roles.Contains(s)) == true ||
        d.Visibility == DashboardVisibility.Public;

    public bool CanEdit(DashboardDefinition d, string userId, string[] roles) =>
        d.Owner == userId || roles.Any(r => d.AdminRoles?.Contains(r) == true);
}
```

#### 步驟 3：替換服務註冊

```csharp
// Program.cs — 以自訂實作取代 JsonFileDashboardService
builder.Services.AddWtmDashboard(); // 先呼叫以取得 DashboardOptions
builder.Services.AddScoped<IDashboardService, EfDashboardService>();
```

---

### 儲存方案對比

| 情境 | 建議方案 | 說明 |
|------|----------|------|
| 單節點、開發環境 | `JsonFileDashboardService`（預設） | 零配置，立即可用 |
| 單節點、生產容器 | `JsonFileDashboardService` + volume 掛載 | 掛載持久化 volume 即可 |
| 多節點 / 高可用 | 自訂 `EfDashboardService`（見上方） | 資料存於共用資料庫 |
| 企業級 / 大流量 | 自訂 `IDashboardService` + Redis 快取 | 依需求自行實作 |
