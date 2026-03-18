# WTM 跨模組整合指南：ETL → Analysis → Dashboard

> WTM 8.x | 繁體中文 | 最後更新：2026-03

本文說明如何將 ETL、Analysis Mode、Dashboard 三個模組串接使用。
各模組詳細 API 請參閱各自的獨立文件：
- ETL：[etl-module.md](etl-module.md)
- Analysis Mode：[analysis-mode.md](analysis-mode.md)
- Dashboard：[dashboard-dev-guide.md](dashboard-dev-guide.md)

---

## 目錄

1. [整合架構概述](#1-整合架構概述)
2. [完整範例情境](#2-完整範例情境)
3. [Step 1 — ETL 載入資料](#3-step-1--etl-載入資料)
4. [Step 2 — Analysis Mode 建立查詢介面](#4-step-2--analysis-mode-建立查詢介面)
5. [Step 3 — Dashboard Widget 串接 Analysis](#5-step-3--dashboard-widget-串接-analysis)
6. [路由入口總覽](#6-路由入口總覽)
7. [常見問題與陷阱](#7-常見問題與陷阱)

---

## 1. 整合架構概述

```
來源系統 (DB / API / CSV)
        │
        ▼  ETL Job（排程或手動觸發）
   WTM 目標資料表（EF Core Model）
        │
        ▼  Analysis Mode（ListVM + [EnableAnalysis]）
   /_analysis/meta、query、export API
        │
        ▼  Dashboard Widget（AnalysisWidgetDataSource）
   /_dashboard/{id}/widget/{wid}/data API
        │
        ▼  瀏覽器 Dashboard 畫面
```

**模組職責**

| 模組 | 職責 | 路由前綴 |
|------|------|----------|
| ETL | 從外部來源周期性同步資料至本地表格 | `/_EtlJob/` |
| Analysis Mode | 提供 BA 可即時查詢的分析介面 | `/_analysis/` |
| Dashboard | 以 Widget 組合展示各項指標 | `/_dashboard/` |

---

## 2. 完整範例情境

> **情境**：每天從 ERP 同步銷售訂單，BA 透過 Analysis 做維度分析，主管在 Dashboard 看即時指標。

本文以 `SaleOrder` 資料表為例。假設結構如下：

```csharp
public class SaleOrder : TopBasePoco
{
    public string Region { get; set; }
    public string ProductCategory { get; set; }
    public decimal Amount { get; set; }
    public DateTime OrderDate { get; set; }
    public string Status { get; set; }
}
```

---

## 3. Step 1 — ETL 載入資料

### 3.1 安裝 ETL 服務

在 `Program.cs`：

```csharp
builder.Services.AddWtmEtl(builder.Configuration);
```

在 `appsettings.json`：

```json
{
  "Connections": [
    {
      "Key": "ErpDB",
      "Value": "Server=erp-db;Database=ERP;...",
      "DBType": "SqlServer"
    }
  ]
}
```

### 3.2 定義 ETL Pipeline

```csharp
// Program.cs
builder.Services.AddEtlPipeline(new EtlPipelineConfig
{
    Name = "SaleOrderSync",
    SourceConnectionKey = "ErpDB",
    TargetConnectionKey = "default",       // WTM 主資料庫
    SourceQuery = @"
        SELECT Region, ProductCategory, Amount, OrderDate, Status
        FROM dbo.Orders
        WHERE UpdatedAt > @watermark",
    WatermarkStrategy = WatermarkStrategy.LastModified,
    WatermarkColumn = "UpdatedAt",
    TargetTable = new StagingTableSpec
    {
        TableName = "SaleOrders",
        Columns = new[]
        {
            new ColumnSpec("Region",          SqlDbType.NVarChar, 50),
            new ColumnSpec("ProductCategory", SqlDbType.NVarChar, 50),
            new ColumnSpec("Amount",          SqlDbType.Decimal,  precision: 18, scale: 2),
            new ColumnSpec("OrderDate",       SqlDbType.DateTime2),
            new ColumnSpec("Status",          SqlDbType.NVarChar, 20),
        }
    },
    Schedule = "0 2 * * *"   // 每天凌晨 2 點
});
```

### 3.3 執行 Migration

ETL 在首次執行時會自動建立目標表。若需手動確認：

```bash
dotnet ef migrations add AddSaleOrders
dotnet ef database update
```

### 3.4 驗證

ETL 執行後，透過管理介面 `/_EtlJob/` 確認 Job 狀態為 `Completed`，並到資料庫驗證 `SaleOrders` 表格有資料。

---

## 4. Step 2 — Analysis Mode 建立查詢介面

資料已在本地表格，現在讓 BA 可以用 Analysis Mode 自由查詢。

### 4.1 在 Model 加上標注

```csharp
using WalkingTec.Mvvm.Core;

public class SaleOrder : TopBasePoco
{
    [Dimension(DisplayName = "地區")]
    public string Region { get; set; }

    [Dimension(DisplayName = "產品類別")]
    public string ProductCategory { get; set; }

    [Dimension(DisplayName = "訂單日期", IsDate = true, Hierarchy = DateHierarchy.YearMonth)]
    public DateTime OrderDate { get; set; }

    [Dimension(DisplayName = "狀態")]
    public string Status { get; set; }

    [Measure(DisplayName = "訂單金額", AllowedFuncs = AggregateFuncs.Sum | AggregateFuncs.Avg)]
    public decimal Amount { get; set; }
}
```

### 4.2 建立 ListVM 並啟用 Analysis

```csharp
[EnableAnalysis]
public class SaleOrderListVM : BasePagedListVM<SaleOrder, BaseSearcher>
{
    protected override IOrderedQueryable<SaleOrder> GetSearchQuery()
    {
        return DC.Set<SaleOrder>().OrderByDescending(x => x.OrderDate);
    }
}
```

### 4.3 Controller 與 View（可選）

若需要在現有 MVC 頁面也看到 Analysis 按鈕：

```csharp
// SaleOrderController.cs
[AllRights]
public class SaleOrderController : BaseController
{
    public ActionResult Index() => BaseList<SaleOrderListVM>();
}
```

在 `Index.cshtml` 加入 Analysis 觸發按鈕（LayUI TagHelper 自動產生）：

```html
<wt:searchform vm="@Model">
    <wt:row items-per-row="ItemsPerRowEnum.Three">
        <wt:datetime field="Searcher.OrderDateRange" />
    </wt:row>
</wt:searchform>
<wt:grid vm="@Model" url="/SaleOrder/Search" />
```

### 4.4 RBAC 保護

若只允許特定角色使用 Analysis：

```csharp
[EnableAnalysis]
[AllowedRoles("Analyst", "FinanceManager")]
public class SaleOrderListVM : BasePagedListVM<SaleOrder, BaseSearcher>
{ ... }
```

`AllowedRoles` 中填寫的是 `RoleCode`（非 `RoleName`）。

---

## 5. Step 3 — Dashboard Widget 串接 Analysis

Analysis Mode 的查詢結果可以直接作為 Dashboard Widget 的資料來源，不需要另外撰寫 `IWidgetDataSource`。

### 5.1 確認 Analysis Widget DataSource 已自動註冊

只要 `AddWtmContext` 在 `AddWtmDashboard` **之前**呼叫，Analysis Widget DataSource 會自動掃描並註冊所有 `[EnableAnalysis]` ListVM：

```csharp
// Program.cs — 順序很重要
builder.Services.AddWtmContext(Configuration, x =>
{
    x.DBType = DBTypeEnum.SqlServer;
    x.ConnectionString = connectionString;
});

builder.Services.AddWtmDashboard();   // 在 AddWtmContext 之後
```

> ⚠️ **常見錯誤**：若 `AddWtmDashboard` 在 `AddWtmContext` 之前呼叫，`AnalysisWidgetDataSource` 會靜默跳過（#591 已追蹤此問題，版本 8.6.1 起會在啟動時拋出警告）。

### 5.2 建立 Dashboard Widget 設定

在 `App_Data/dashboards/_default/sales.json`：

```json
{
  "SchemaVersion": 1,
  "Id": "sales-dashboard",
  "Title": "銷售儀表板",
  "Owner": "admin",
  "RefreshInterval": 300,
  "Layout": [
    { "Id": "w1", "X": 0, "Y": 0, "W": 6, "H": 2 },
    { "Id": "w2", "X": 6, "Y": 0, "W": 6, "H": 2 }
  ],
  "Widgets": {
    "w1": {
      "Type": "chart",
      "Title": "各地區銷售金額（本月）",
      "DataSource": "analysis:YourApp.SaleOrderListVM",
      "Params": {
        "dimensions": ["Region"],
        "measures": [{ "field": "Amount", "func": "Sum" }],
        "filters": [
          { "field": "OrderDate", "operator": "Gte", "value": "{{thisMonth}}" }
        ]
      }
    },
    "w2": {
      "Type": "chart",
      "Title": "各產品類別銷售趨勢",
      "DataSource": "analysis:YourApp.SaleOrderListVM",
      "Params": {
        "dimensions": ["OrderDate", "ProductCategory"],
        "measures": [{ "field": "Amount", "func": "Sum" }]
      }
    }
  }
}
```

**DataSource 格式**：`analysis:{ListVM FullName}`，FullName 為 `命名空間.類別名稱`。

### 5.3 可用的 Params 欄位

| 欄位 | 必填 | 說明 |
|------|------|------|
| `dimensions` | 是 | 維度欄位名稱陣列（對應 `FieldName`），最多 3 個 |
| `measures` | 是 | `[{ "field": "欄位名", "func": "Sum\|Avg\|Count\|Max\|Min" }]`，最多 3 個 |
| `filters` | 否 | `[{ "field", "operator", "value" }]` 靜態篩選條件 |
| `pivotDimension` | 否 | 指定此欄位為交叉軸，使用 Pivot 查詢 |

**Operator 可用值**：`Eq`、`Neq`、`Gt`、`Gte`、`Lt`、`Lte`、`Contains`、`StartsWith`

---

## 6. 路由入口總覽

| 模組 | 入口路由 | 用途 |
|------|----------|------|
| ETL 管理 | `/_EtlJob/` | 建立/執行/監控 ETL Job |
| Analysis 查詢 | `/_analysis/meta`、`/_analysis/query` | BA 查詢介面（前端由 framework_analysis.js 驅動） |
| Analysis 匯出 | `/_analysis/export` | 匯出 XLSX / CSV |
| Dashboard 管理 | `/_dashboard/` | 儀表板列表、CRUD |
| Dashboard Widget 資料 | `/_dashboard/{id}/widget/{wid}/data` | Widget 資料請求 |

所有路由均須通過 WTM RBAC（`[AllRights]` 代表所有登入使用者均可存取）。

---

## 7. 常見問題與陷阱

### Q1：Analysis Widget 在 Dashboard 顯示「找不到資料來源」

**原因**：`AddWtmDashboard` 在 `AddWtmContext` 之前呼叫，`AnalysisWidgetDataSource` 未能掃描到 `[EnableAnalysis]` ListVM。

**解法**：調整 `Program.cs` 的呼叫順序，確保 `AddWtmContext` 先於 `AddWtmDashboard`。

---

### Q2：Analysis 查詢回傳 403 Forbidden

**原因**：ListVM 使用了 `[AllowedRoles("Analyst")]`，但使用者的 `RoleCode` 不是 `Analyst`（可能是 `RoleName` 相符但 `RoleCode` 不同）。

**解法**：確認 RBAC 設定時填入的是 `RoleCode`，可以透過管理後台「角色管理」確認各角色的 Code。

---

### Q3：ETL 執行後 Analysis 看不到新資料

**可能原因 1**：Analysis Mode 使用了查詢快取（`IAnalysisCache`）。預設沒有快取，但若有設定，需等 TTL 過期或手動清快取。

**可能原因 2**：ETL Job 使用 Watermark，但 Watermark 欄位的時區與資料庫時區不一致。確認 `appsettings.json` 中 `TimeZone` 設定。

---

### Q4：大資料量時 Analysis Query 超時

Analysis Mode 預設對來源資料取樣上限為 **50,000 筆**（`DataTruncated = true`）。若資料量極大：

1. 使用 `filters` 縮小查詢範圍（Searcher 條件或 Widget Params.filters）
2. 在 ListVM 的 `GetSearchQuery()` 中加入預篩邏輯（如只查最近 N 個月）
3. 考慮在 ETL 層做預聚合，將每日 / 每月 summary 存入獨立表格

---

### Q5：Dashboard 在多節點部署下資料不同步

見 [dashboard-dev-guide.md 部署注意事項](dashboard-dev-guide.md#部署注意事項) 章節。

---

## 延伸閱讀

- [ETL 模組完整文件](etl-module.md) — Watermark 策略、Staging Table、排程設定
- [Analysis Mode 完整文件](analysis-mode.md) — 自訂欄位、DataPrivilege、快取設定
- [Dashboard 開發指南](dashboard-dev-guide.md) — 自訂 DataSource、Widget 類型、多節點部署
