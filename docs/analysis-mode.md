# WTM Analysis Mode — 新組件開發使用手冊

**適用版本**：WTM 8.1.17+（v2 拖曳面板自 8.7.0+）
**最後更新**：2026-03-13

---

## 目錄

1. [功能概述](#1-功能概述)
2. [快速開始（5 步驟）](#2-快速開始5-步驟)
3. [Model 標注詳解](#3-model-標注詳解)
4. [ListVM 設定](#4-listvm-設定)
5. [View 啟用](#5-view-啟用)
6. [完整範例](#6-完整範例)
7. [API 規格參考](#7-api-規格參考)
8. [安全性設計](#8-安全性設計)
9. [目前限制與 Phase 2 預告](#9-目前限制與-phase-2-預告)

---

## 1. 功能概述

Analysis Mode 讓已有的 CRUD 列表頁**無需另寫程式碼**，即可在同一頁切換至「分析模式」，提供：

- **動態維度 / 度量選擇**（拖曳欄位到維度/度量區域即分組聚合）
- **聚合表格**（GroupBy + Sum / Count / Avg / Max / Min）
- **自動選型圖表**（Bar / Stacked Bar / Line / 數字卡片，由維度組合自動決定）
- **Excel 匯出**（.xlsx，NPOI 生成）

```
列表頁                         分析模式（同一頁切換）
┌─────────────────────┐        ┌──────────────────────────────────┐
│ [新增] [刪除] [分析] │  ──►  │ 可用欄位：[地區] [業務員] [金額]  │
│ 訂單號 | 地區 | 金額 │        │                                  │
│ ...                 │        │ 維度區 ┤ 拖入 → [地區🗑]          │
└─────────────────────┘        │ 度量區 ┤ 拖入 → [金額 SUM▼ 🗑]   │
                                │ [查詢]  [匯出 Excel]              │
                                ├──────────────────────────────────┤
                                │ ▼ 分析結果（可收合）              │
                                │ 地區 | SUM(金額)                 │
                                │ 華東 | 1,234,567                 │
                                └──────────────────────────────────┘
```

---

## 2. 快速開始（5 步驟）

### Step 1 — 在 Model 標注維度和度量

```csharp
// Models/OrderModel.cs
public class OrderModel : BasePoco
{
    [Dimension(DisplayName = "地區")]
    public string Region { get; set; }

    [Dimension(DisplayName = "業務員")]
    public string SalesRepName { get; set; }

    [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count | AggregateFunc.Avg,
             DisplayName = "金額")]
    public decimal Amount { get; set; }
}
```

### Step 2 — 在 ListVM 加上 `[EnableAnalysis]`

```csharp
// ViewModels/OrderListVM.cs
using WalkingTec.Mvvm.Core.Analysis;

[EnableAnalysis]               // ← 加這一行，白名單註冊
public class OrderListVM : BasePagedListVM<OrderModel, OrderSearcher>
{
    protected override IEnumerable<IGridColumn<OrderModel>> InitGridHeader()
    {
        return new List<IGridColumn<OrderModel>>
        {
            this.MakeGridHeader(x => x.Region),
            this.MakeGridHeader(x => x.SalesRepName),
            this.MakeGridHeader(x => x.Amount),
        };
    }

    public override IOrderedQueryable<OrderModel> GetSearchQuery()
    {
        return DC.Set<OrderModel>()
            .CheckContain(Searcher.Region, x => x.Region)
            .OrderBy(x => x.Region);
    }
}
```

### Step 3 — 在 View 啟用 Analysis 按鈕

```html
<!-- Views/Order/Index.cshtml -->
<wt:searchpanel vm="@Model" reset-btn="true">
    <wt:row>
        <wt:combobox field="Searcher.Region" />
    </wt:row>
</wt:searchpanel>

<wt:grid vm="@Model" url="/Order/Search"
         enable-analysis="true" />    <!-- ← 加這個屬性 -->
```

### Step 4 — 啟動，工具列自動出現「分析」按鈕

框架自動完成：
- `DataTableTagHelper` 注入「切換分析」按鈕
- 懶載入 `/_js/framework_analysis.js`
- 呼叫 `/_analysis/meta` 取得維度/度量清單

### Step 5 — 完成

點擊「分析」按鈕，選維度/度量，點「查詢」即可看到聚合結果與圖表。

---

## 3. Model 標注詳解

### `[Dimension]` — 維度欄位（GROUP BY 候選）

```csharp
[AttributeUsage(AttributeTargets.Property)]
public class DimensionAttribute : Attribute
{
    /// <summary>前端顯示名稱（必填，建議使用中文）</summary>
    public string DisplayName { get; set; }

    /// <summary>日期階層（Phase 2，目前保留）</summary>
    public DateHierarchy Hierarchy { get; set; }
}
```

**適用型別**：`string`、`int`/`long`（ID 類）、`DateTime?`（Phase 2 日期鑽取）。

**範例**：

```csharp
[Dimension(DisplayName = "地區")]
public string Region { get; set; }

[Dimension(DisplayName = "產品類別")]
public string Category { get; set; }

// 日期維度（Phase 2 啟用後支援鑽取）
[Dimension(DisplayName = "下單日期", Hierarchy = DateHierarchy.Month)]
public DateTime? OrderDate { get; set; }
```

### `[Measure]` — 度量欄位（聚合候選）

```csharp
[AttributeUsage(AttributeTargets.Property)]
public class MeasureAttribute : Attribute
{
    /// <summary>允許的聚合函式（Flag enum，可 OR 多選）</summary>
    public AggregateFunc AllowedFuncs { get; set; }

    /// <summary>前端顯示名稱</summary>
    public string DisplayName { get; set; }
}
```

**`AggregateFunc` 枚舉**：

| 值 | 整數 | 說明 |
|----|------|------|
| `Count` | 1 | 計數（筆數） |
| `Sum`   | 2 | 加總 |
| `Avg`   | 4 | 平均值 |
| `Max`   | 8 | 最大值 |
| `Min`   | 16 | 最小值 |

**範例**：

```csharp
// 允許所有聚合
[Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count |
                         AggregateFunc.Avg | AggregateFunc.Max | AggregateFunc.Min,
         DisplayName = "金額")]
public decimal Amount { get; set; }

// 僅允許計數（適合整數 ID 欄位）
[Measure(AllowedFuncs = AggregateFunc.Count, DisplayName = "訂單筆數")]
public int OrderCount { get; set; }

// 適合只需 Sum 的欄位
[Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Avg,
         DisplayName = "重量（公斤）")]
public decimal WeightKg { get; set; }
```

**適用型別**：`decimal`、`double`、`float`、`int`、`long`（任何可轉 `decimal` 的型別）。

### 同一欄位不能同時標注 `[Dimension]` 和 `[Measure]`

框架在查詢時會驗證欄位種類，同時標注會拋 `InvalidOperationException`。

---

## 4. ListVM 設定

### 4.1 必要：`[EnableAnalysis]` 白名單屬性

```csharp
using WalkingTec.Mvvm.Core.Analysis;

[EnableAnalysis]
public class OrderListVM : BasePagedListVM<OrderModel, OrderSearcher> { ... }
```

`[EnableAnalysis]` 是安全白名單機制。啟動時框架掃描所有帶此屬性的 ListVM 並建立登記表；`_AnalysisController` 只允許白名單內的 VM 型別，拒絕任意 type 注入。

**沒有 `[EnableAnalysis]` 的 ListVM 無法使用 Analysis Mode**，`/meta` API 會回傳 400。

### 4.2 選用：覆寫 `GetAnalysisFields()` 加入計算欄位

當需要在分析模式中提供「不存在於 Model 屬性上」的計算欄位時，可覆寫此虛方法：

```csharp
[EnableAnalysis]
public class OrderListVM : BasePagedListVM<OrderModel, OrderSearcher>
{
    // 覆寫後可追加計算欄位
    public override IEnumerable<AnalysisFieldMeta> GetAnalysisFields()
    {
        // 先取 Model 上的 [Dimension]/[Measure] 標注
        var fields = base.GetAnalysisFields().ToList();

        // 追加自訂計算欄位（注意：需自行在 GetSearchQuery 回傳的查詢中提供此欄位）
        fields.Add(new AnalysisFieldMeta
        {
            FieldName   = "GrossProfit",
            DisplayName = "毛利",
            Kind        = AnalysisFieldKind.Measure,
            AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Avg,
            ClrType     = typeof(decimal)
        });

        return fields;
    }
}
```

### 4.3 Searcher 狀態傳遞

切換到分析模式時，SearchPanel 的當前條件（地區篩選、日期範圍等）會**自動傳入**分析查詢，與列表頁篩選保持一致。這透過序列化 form data → `SearcherFormData` → Controller 還原 Searcher 實例 → 呼叫 `GetSearchQuery()` 實現，開發者無需額外處理。

---

## 5. View 啟用

只需在 `<wt:grid>` 加一個屬性：

```html
<wt:grid vm="@Model" url="/Order/Search"
         enable-analysis="true" />
```

框架自動在工具列注入：
```html
<!-- 自動生成（無需手動撰寫）-->
<button class="layui-btn layui-btn-sm" onclick="wtmAnalysis.toggle('gridId')">
    分析
</button>
<div id="analysis-panel-gridId" class="analysis-panel" style="display:none"></div>
<div id="analysis-result-block-gridId" class="analysis-result" style="display:none"></div>
<link rel="stylesheet" href="/_js/framework_analysis.css" />
<script src="/_js/sortable.min.js"></script>
<script src="/_js/framework_analysis.js"></script>
```

**注意**：
- CSS/JS 使用懶載入，只在 `enable-analysis="true"` 時引入，不影響未啟用的頁面
- 多個 Grid 共用同一份 CSS/JS（自動去重）
- 分析面板和結果區塊是獨立的 `<div>`，各自可收合

---

## 6. 完整範例

以下是一個完整的訂單分析功能，從 Model 到 View。

### 6.1 Model

```csharp
// Models/SalesOrder.cs
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

public class SalesOrder : BasePoco
{
    [Dimension(DisplayName = "銷售地區")]
    public string Region { get; set; }

    [Dimension(DisplayName = "業務員")]
    public string SalesRep { get; set; }

    [Dimension(DisplayName = "產品類別")]
    public string ProductCategory { get; set; }

    [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count | AggregateFunc.Avg,
             DisplayName = "銷售金額")]
    public decimal SalesAmount { get; set; }

    [Measure(AllowedFuncs = AggregateFunc.Sum, DisplayName = "數量")]
    public int Quantity { get; set; }

    public string CustomerName { get; set; }   // 未標注 = 不出現在分析選項中
    public DateTime OrderDate { get; set; }
}
```

### 6.2 Searcher

```csharp
// ViewModels/SalesOrderSearcher.cs
public class SalesOrderSearcher : BaseSearcher
{
    [Display(Name = "地區")]
    public string Region { get; set; }

    [Display(Name = "產品類別")]
    public string ProductCategory { get; set; }
}
```

### 6.3 ListVM

```csharp
// ViewModels/SalesOrderListVM.cs
using WalkingTec.Mvvm.Core.Analysis;

[EnableAnalysis]
public class SalesOrderListVM : BasePagedListVM<SalesOrder, SalesOrderSearcher>
{
    protected override IEnumerable<IGridColumn<SalesOrder>> InitGridHeader()
    {
        return new List<IGridColumn<SalesOrder>>
        {
            this.MakeGridHeader(x => x.Region),
            this.MakeGridHeader(x => x.SalesRep),
            this.MakeGridHeader(x => x.ProductCategory),
            this.MakeGridHeader(x => x.SalesAmount),
            this.MakeGridHeader(x => x.Quantity),
            this.MakeGridHeader(x => x.CustomerName),
            this.MakeGridHeader(x => x.OrderDate),
            this.MakeGridHeaderAction(width: 200)
        };
    }

    public override IOrderedQueryable<SalesOrder> GetSearchQuery()
    {
        return DC.Set<SalesOrder>()
            .CheckContain(Searcher.Region, x => x.Region)
            .CheckContain(Searcher.ProductCategory, x => x.ProductCategory)
            .OrderByDescending(x => x.OrderDate);
    }
}
```

### 6.4 Controller

```csharp
// Controllers/SalesOrderController.cs
public class SalesOrderController : BaseController
{
    [ActionDescription("訂單列表")]
    public ActionResult Index()
    {
        var vm = Wtm.CreateVM<SalesOrderListVM>();
        return View(vm);
    }

    [ActionDescription("查詢")]
    public ActionResult Search(SalesOrderListVM vm)
    {
        return PartialView(vm);
    }

    // CRUD actions...
}
```

### 6.5 View

```html
<!-- Views/SalesOrder/Index.cshtml -->
@model SalesOrderListVM

<wt:searchpanel vm="@Model" reset-btn="true" submit-btn="true">
    <wt:row items-per-row="ItemsPerRowEnum.Three">
        <wt:combobox field="Searcher.Region"
                     items="@(Wtm.GetSelectListByType<string>())" />
        <wt:combobox field="Searcher.ProductCategory"
                     items="@(Wtm.GetSelectListByType<string>())" />
    </wt:row>
</wt:searchpanel>

<wt:grid vm="@Model" url="/SalesOrder/Search"
         enable-analysis="true"
         add-dialog="true"
         edit-dialog="true" />
```

### 6.6 分析結果

啟動後點擊「分析」按鈕，從可用欄位區拖曳欄位到維度/度量區：

```
┌─ 可用欄位 ──────────────────────────────────────┐
│ [銷售地區] [業務員] [產品類別] [銷售金額] [數量]  │
└─────────────────────────────────────────────────┘

┌─ 維度（拖入） ──────────────────────────────────┐
│ [銷售地區 🗑]  [產品類別 🗑]                      │
└─────────────────────────────────────────────────┘

┌─ 度量（拖入） ──────────────────────────────────┐
│ [銷售金額 SUM▼ 🗑]  [銷售金額 COUNT▼ 🗑]         │
└─────────────────────────────────────────────────┘

[查詢]  [匯出 Excel ▼]

▼ 分析結果（查詢後面板自動收合，點擊可展開）
銷售地區   │ 產品類別 │ SUM(銷售金額) │ COUNT(銷售金額)
──────────────────────────────────────────────────
華東        │ 家電      │  2,345,678   │  156
華東        │ 3C        │  1,876,543   │  234
華南        │ 家電      │  1,234,567   │   98
```

---

## 7. API 規格參考

Analysis Mode 透過 `/_analysis` 路由提供三個 API，供前端 `framework_analysis.js` 呼叫（開發者通常不需直接呼叫，但可用於自訂前端整合）。

### GET `/_analysis/meta`

取得指定 ListVM 的可用維度和度量清單。

**Query 參數**：

| 參數 | 型別 | 說明 |
|------|------|------|
| `listVmType` | `string` | ListVM 的 FullName（如 `MyApp.ViewModels.SalesOrderListVM`） |

**回應**（`200 OK`）：

```json
{
  "dimensions": [
    { "fieldName": "Region",      "displayName": "銷售地區", "isDate": false },
    { "fieldName": "ProductCategory", "displayName": "產品類別", "isDate": false }
  ],
  "measures": [
    {
      "fieldName": "SalesAmount",
      "displayName": "銷售金額",
      "allowedFuncs": ["Sum", "Count", "Avg"]
    }
  ]
}
```

**錯誤**：
- `400 Bad Request` — `listVmType` 不在白名單中

---

### POST `/_analysis/query`

執行動態 GroupBy 聚合查詢。

**Request Body**（`application/json`）：

```json
{
  "listVmType": "MyApp.ViewModels.SalesOrderListVM",
  "searcherFormData": "{\"Region\":\"華東\"}",
  "dimensions": ["Region", "ProductCategory"],
  "measures": [
    { "field": "SalesAmount", "func": "Sum" },
    { "field": "SalesAmount", "func": "Count" }
  ],
  "filters": [
    { "field": "SalesAmount", "operator": "Gte", "value": "10000" }
  ]
}
```

**`FilterOperator` 可用值**：

| 值 | 說明 | 適用型別 |
|----|------|---------|
| `Eq` | 等於 | 所有型別 |
| `Gt` | 大於 | 數值、日期 |
| `Gte` | 大於等於 | 數值、日期 |
| `Lt` | 小於 | 數值、日期 |
| `Lte` | 小於等於 | 數值、日期 |
| `Contains` | 包含子字串 | string |

**回應**（`200 OK`）：

```json
{
  "columns": ["Region", "ProductCategory", "SalesAmount_Sum", "SalesAmount_Count"],
  "rows": [
    { "Region": "華東", "ProductCategory": "家電",
      "SalesAmount_Sum": 2345678.00, "SalesAmount_Count": 156 },
    { "Region": "華南", "ProductCategory": "3C",
      "SalesAmount_Sum": 987654.00,  "SalesAmount_Count":  87 }
  ],
  "totalCount": 2,
  "truncated": false,
  "queryHash": "a3f2b1c4d5e6f7a8"
}
```

**注意**：當 GroupBy 結果超過 10,000 列時，`truncated: true`，並只回傳前 10,000 列。

**錯誤**：
- `400 Bad Request` — 欄位不在白名單、函式不允許、值轉換失敗

---

### POST `/_analysis/export`

匯出分析結果為 Excel 或 CSV。

**Query 參數**：

| 參數 | 型別 | 預設 | 說明 |
|------|------|------|------|
| `format` | `string` | `xlsx` | 匯出格式：`xlsx` 或 `csv` |

**Request Body**：與 `/query` 端點相同的 `AnalysisQueryRequest` JSON。

**回應**：
- `xlsx`：`application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`，檔名 `analysis.xlsx`
- `csv`：`text/csv`，檔名 `analysis.csv`，UTF-8 編碼，RFC 4180 格式

**安全**：CSV 值以公式字元（`=`, `+`, `-`, `@`）開頭時自動前置 tab，防止 CSV formula injection。

**錯誤**：
- `400 Bad Request` — 維度/度量超過 3 個、欄位不在白名單、`listVmType` 不在白名單

---

## 8. 安全性設計

| 威脅 | 防護措施 |
|------|---------|
| **VM 型別注入** | `AnalysisVmRegistry` 白名單，啟動時掃描 `[EnableAnalysis]` ListVM，未知型別回 400 |
| **欄位名注入** | 所有 Dimension / Measure / Filter 欄位在 `ValidateFields()` 中對白名單驗證後才進 Expression Tree |
| **SQL Injection** | 全程 Expression Tree（非字串拼接），由 EF Core 生成參數化查詢 |
| **Filter 型別混淆** | `Value` 統一傳 `string`，server-side 依欄位的 `ClrType` 做型別安全轉換，失敗回 400 |
| **CSV Formula Injection** | CSV 匯出時，值以 `=` / `+` / `-` / `@` 開頭者自動前置 tab，防止惡意公式在 Excel 中執行 |
| **大量資料 DoS** | GroupBy 結果強制 `Take(10,001)` 截斷並標記 `Truncated: true`；載入上限 50,000 筆防 OOM |
| **多租戶隔離** | 複用同一 `DC` 實例，EF Core global query filter 自動生效，租戶資料隔離無需額外處理 |
| **未授權存取** | `_AnalysisController` 標注 `[AllRights]`（需登入）；VM 層 `[EnableAnalysis(AllowedRoles)]` 限制哪些角色可存取特定 VM；欄位層 `IAnalysisFieldPolicy` 過濾欄位可見性 |

### 角色型存取控制（RBAC）

Analysis Mode 提供兩層角色控制，角色識別符統一使用 **`RoleCode`**（非 `RoleName`）。

#### VM 層限制

```csharp
// 只有 RoleCode 為 "analyst" 或 "finance_mgr" 的使用者才能查詢此 VM
[EnableAnalysis(AllowedRoles = "analyst,finance_mgr")]
public class SalesListVM : BasePagedListVM<Sales, SalesSearcher> { }
```

`AllowedRoles` 為空 = 不限角色（所有已登入使用者都可存取）。

#### 欄位層限制

```csharp
[Measure(AllowedFuncs = AggregateFunc.Sum, AllowedRoles = "finance_mgr")]
public decimal GrossProfit { get; set; }   // 僅 finance_mgr 可見

[Dimension]   // 無 AllowedRoles = 所有已授權使用者均可見
public string Region { get; set; }
```

#### 自訂欄位策略

實作 `IAnalysisFieldPolicy` 可取代預設的 `AllowedRoles` 比對，實作更複雜的欄位過見邏輯（如依租戶動態決定）：

```csharp
public class MyFieldPolicy : IAnalysisFieldPolicy
{
    public IEnumerable<AnalysisFieldMeta> Filter(
        IEnumerable<AnalysisFieldMeta> fields, ClaimsPrincipal user)
    {
        // ClaimsPrincipal 的 role claims 是以 RoleCode 建構
        var isAdmin = user.IsInRole("Admin");
        return fields.Where(f =>
            string.IsNullOrEmpty(f.AllowedRoles) || isAdmin ||
            f.AllowedRoles.Split(',').Any(r => user.IsInRole(r.Trim())));
    }
}

// Program.cs
builder.Services.AddSingleton<IAnalysisFieldPolicy, MyFieldPolicy>();
```

> **重要**：`RoleCode = "Admin"` 的使用者永遠繞過欄位限制。`AllowedRoles` 填寫的值必須是 `FrameworkRole.RoleCode`，**不是** `RoleName`（顯示名稱）。

---

## 9. 目前限制與 Phase 2 預告

### Phase 1 已實作（8.1.17+）

- [x] `[Dimension]` / `[Measure]` 屬性標注系統
- [x] `[EnableAnalysis]` 白名單機制
- [x] `/_analysis/meta` — 維度/度量清單 API
- [x] `/_analysis/query` — 動態 GroupBy 聚合 API（Sum / Count / Avg / Max / Min）
- [x] `/_analysis/export` — Excel (.xlsx) 匯出
- [x] `DataTableTagHelper.EnableAnalysis` — 一鍵啟用
- [x] ECharts 自動選型（Bar / Stacked Bar / Line / 數字卡片）

### v2 拖曳面板已實作（8.7.0+）

- [x] SortableJS 拖曳式 BI 面板（取代 checkbox UI）
- [x] 可用欄位 → 維度區 / 度量區拖曳操作
- [x] 欄位 pill（含刪除按鈕、聚合函式選擇）
- [x] 分析面板與結果區塊獨立收合
- [x] 查詢後面板自動收合並顯示摘要列
- [x] `framework_analysis.css` — 專用樣式（pill、dropzone、拖曳動畫）
- [x] 雙層 XSS 防護（`escapeHtml` + `encodeSafeHtml`）
- [x] SortableJS 實例生命週期管理（防記憶體洩漏）
- [x] 多 Grid 隔離（同一頁面多個分析面板互不干擾）
- [x] 測試覆蓋（Engine 30 + Exporter 6 + Controller 16 + JS 196）

### Phase 1 已知限制

| 限制 | 說明 |
|------|------|
| **In-process GroupBy** | 先 `Take(50,000).ToList()` 再 in-process GroupBy，高基數欄位或大資料量時效能較低。Phase 2 改為 EF Core server-side GroupBy。 |
| **無日期鑽取** | `DateHierarchy` 已預留，Phase 2 實作年/季/月/週/日鑽取 |
| **無 Pivot 表** | Phase 2 功能 |
| **無圖表 drill-down** | Phase 2 功能 |
| **無結果快取** | `QueryHash` 已生成，Phase 2 接 Redis/MemoryCache |
| **無欄位級權限** | Phase 2 評估，目前只有登入驗證 |
| **雙 Y 軸僅支援恰好 2 個度量** | `detectDualAxis` 在度量數 ≠ 2 時不觸發。選取 1 個或 3 個度量時使用單 Y 軸；`bar-stacked` 圖表類型也不啟用雙 Y 軸。觸發條件：2 個度量且最大值 ratio ≥ 10 倍。 |
| **Excel 匯出最多 10,000 行** | 超過上限時回傳截斷結果，response 包含 `X-Analysis-Truncated: true` header。CSV 匯出維持 50,000 行上限。 |

### Phase 2 路線圖

```
Phase 2（規劃中）
├── DateHierarchy 日期鑽取（年/季/月/週/日）
├── EF Core server-side GroupBy（避免全表載入）
├── Pivot 樞紐表
├── QueryHash 結果快取
├── 圖表 drill-down 互動
└── 欄位級權限控制
```

---

## 附錄：常見問題

**Q：點擊「分析」沒有反應，或按鈕不出現？**

確認：
1. View 的 `<wt:grid>` 有 `enable-analysis="true"`
2. ListVM 有 `[EnableAnalysis]` 屬性
3. 瀏覽器 Console 沒有 JS 錯誤
4. `/_js/framework_analysis.js` 能正常載入（開發模式）

**Q：`/meta` API 回傳 400？**

ListVM 的 FullName 沒有在 `AnalysisVmRegistry` 中登記。確認：
1. ListVM 標注了 `[EnableAnalysis]`
2. ListVM 所在的 Assembly 在啟動時已被載入（若 Assembly 是 plugin，需在 startup 手動 `AnalysisVmRegistry.Build(new[] { pluginAssembly })`）

**Q：分析結果被截斷為 10,000 列？**

`AnalysisQueryResponse.Truncated = true` 時表示 GroupBy 結果超過上限。通常代表維度基數過高（如用戶 ID 作為維度）。建議：
- 加更多 Filter 條件縮小範圍
- 選更高層級的維度（地區而非門市）

**Q：如何讓不在 Model 屬性上的計算欄位也能用於分析？**

覆寫 `GetAnalysisFields()` 追加 `AnalysisFieldMeta`，但注意：計算欄位的值需由 `GetSearchQuery()` 回傳的查詢提供（例如透過 `.Select()` 投影）。若欄位完全不在 EF 查詢中，GroupBy 時會拋 `NullReferenceException`。

**Q：`FilterOperator.In` 支援嗎？**

支援。可以使用 `In` 運算子進行多值過濾。

---

## Code Generator 整合

WTM Code Generator 支援自動產生 Analysis Mode 所需的 attribute。

### 使用方式

1. 在 Code Generator 頁面勾選 **Enable Analysis**
2. 表格會顯示 **IsDimension** 和 **IsMeasure** 兩欄
3. 系統根據屬性型別自動推薦（string/enum→Dimension、數值→Measure、DateTime→Dimension）
4. 開發者可覆寫預設值
5. 點擊生成後，`[EnableAnalysis]` 自動加到 ListVM，`[Dimension]`/`[Measure]` 自動插入 Model 檔案

### 智慧預設規則

| 屬性型別 | 預設 |
|----------|------|
| `string`, `enum` | Dimension |
| `decimal`, `int`, `double`, `float`, `long`, `short` | Measure |
| `DateTime` | Dimension (Hierarchy = DateHierarchy.Month) |
| 其他 | 無 |
