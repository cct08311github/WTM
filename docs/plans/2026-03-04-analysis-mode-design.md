# WTM Analysis Mode — 設計文件

**日期**：2026-03-04
**版本**：1.0
**狀態**：已核准，待實施

---

## 目標

讓現有 CRUD 列表頁在同一頁面可切換「列表模式 ↔ 分析模式」，提供動態維度/度量選擇、聚合表格、ECharts 圖表及 Excel 匯出，使工具具備輕量 BI 能力。

## 範圍

### Phase 1（本文件涵蓋）
- Attribute 標記系統（`[Dimension]` / `[Measure]`）
- VM 型別白名單（取代不安全的 `Type.GetType()`）
- Meta API：回傳可用維度/度量清單
- 動態 GroupBy 引擎（EF Core Expression Tree）
- 聚合結果表格（layui table）
- ECharts 基礎圖表（Bar/Line/Pie 自動選型）
- Excel/CSV 匯出
- `DataTableTagHelper` 加 `enable-analysis` 屬性
- `framework_analysis.js`（獨立 JS，不改現有 `framework_layui.js`）

### Phase 2（需求確認後）
- DateHierarchy 鑽取（年/季/月/週/日）
- Pivot 樞紐表
- 圖表 drill-down
- 結果快取（`queryHash` 已預留）

---

## 架構概覽

```
開發者 View                    前端 JS                      後端
──────────────────────────────────────────────────────────────────────
<wt:grid                       framework_analysis.js          _AnalysisController
  vm="@Model"                   ├─ DimensionSelector            ├─ GET /meta
  enable-analysis="true" />     ├─ MeasureSelector              │   → 白名單維度/度量清單
                                ├─ FilterPanel                  ├─ POST /query
DataTableTagHelper               ├─ ResultTable                  │   → 動態 GroupBy → 聚合結果
  └─ 注入切換鍵                 └─ ChartPanel                  └─ GET /export
     + <div #analysis-panel>                                        → Excel/CSV
```

**三層分離原則**：TagHelper 不感知分析邏輯；Controller 不感知 UI；JS 不直接碰 EF。

---

## §1 接入方式

開發者只需一行：

```html
<wt:grid vm="@Model" enable-analysis="true" />
```

`DataTableTagHelper` 改動：
- 新增 `bool EnableAnalysis { get; set; }` 屬性
- 在 toolbar HTML 注入切換鍵
- 在 grid 容器後注入空的 `<div id="analysis-panel-{Id}">` 容器
- 注入對 `framework_analysis.js` 的 script 引用（lazy，僅 `EnableAnalysis=true` 時）

`DataTableTagHelper.cs` 本身只增加約 15 行，主邏輯全在 `_AnalysisPanel.cshtml` partial view 和 JS 中。

---

## §2 Attribute 標記系統

```csharp
/// <summary>
/// 標記此欄位可作為分析維度（GROUP BY 候選）
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class DimensionAttribute : Attribute
{
    public string DisplayName { get; set; }         // 前端顯示名，可為 i18n resource key
    public DateHierarchy Hierarchy { get; set; }    // 僅日期欄位有效（Phase 2）
}

/// <summary>
/// 標記此欄位可作為聚合度量
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class MeasureAttribute : Attribute
{
    public AggregateFunc AllowedFuncs { get; set; } // Flag enum，可多選
    public string DisplayName { get; set; }
}

[Flags]
public enum AggregateFunc
{
    Count = 1,
    Sum   = 2,
    Avg   = 4,
    Max   = 8,
    Min   = 16
}

public enum DateHierarchy { Year, Quarter, Month, Week, Day } // Phase 2
```

**範例**：

```csharp
public class OrderModel : BasePoco
{
    [Dimension(DisplayName = "地區")]
    public string Region { get; set; }

    [Dimension(DisplayName = "業務員")]
    public string SalesRepName { get; set; }

    [Dimension(DisplayName = "下單日期", Hierarchy = DateHierarchy.Month)]
    public DateTime? OrderDate { get; set; }

    [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Avg | AggregateFunc.Count,
             DisplayName = "金額")]
    public decimal Amount { get; set; }

    [Measure(AllowedFuncs = AggregateFunc.Count, DisplayName = "筆數")]
    public int Quantity { get; set; }
}
```

**ListVM override（選用，用於計算欄位）**：

```csharp
// BasePagedListVM 新增虛方法
protected virtual IEnumerable<AnalysisFieldMeta> GetAnalysisFields()
    => AnalysisFieldScanner.ScanModel(typeof(TModel));

// ListVM 可 override 加入計算欄位
public override IEnumerable<AnalysisFieldMeta> GetAnalysisFields()
{
    return base.GetAnalysisFields().Append(new AnalysisFieldMeta
    {
        FieldName = "Profit",
        DisplayName = "毛利",
        Kind = AnalysisFieldKind.Measure,
        AllowedFuncs = AggregateFunc.Sum
    });
}
```

---

## §3 VM 型別白名單

**取代不安全的 `Type.GetType(string)` 直接反射**：

```csharp
// 啟動時（IHostedService 或 middleware）掃描所有 assembly
public static class AnalysisVmRegistry
{
    private static readonly Dictionary<string, Type> _whitelist = new();

    public static void Build(IEnumerable<Assembly> assemblies)
    {
        foreach (var asm in assemblies)
        foreach (var type in asm.GetTypes())
        {
            if (!type.IsAbstract && IsAnalysisEnabledListVm(type))
                _whitelist[type.FullName!] = type;
        }
    }

    // 只接受 FullName（不含 assembly），且必須在白名單內
    public static Type Resolve(string fullName)
        => _whitelist.TryGetValue(fullName, out var t) ? t
           : throw new InvalidOperationException($"VM type not found: {fullName}");

    private static bool IsAnalysisEnabledListVm(Type t)
        => t.BaseType is { IsGenericType: true }
           && t.BaseType.GetGenericTypeDefinition() == typeof(BasePagedListVM<,>)
           && t.GetCustomAttribute<EnableAnalysisAttribute>() != null;
}
```

`ListVmType` 只接受 `FullName`（如 `MyApp.ListVMs.OrderListVM`），不接受 assembly-qualified name。

---

## §4 Query Request / Response

```csharp
public class AnalysisQueryRequest
{
    /// <summary>ListVM 的 FullName，在白名單中查找</summary>
    public string ListVmType { get; set; }

    /// <summary>當前 SearchPanel 的 form data（JSON），傳入後 bind 到 Searcher</summary>
    public string SearcherFormData { get; set; }

    /// <summary>選取的維度欄位名（最多 3 個）</summary>
    public List<string> Dimensions { get; set; }

    /// <summary>選取的度量及聚合函式</summary>
    public List<MeasureRequest> Measures { get; set; }

    /// <summary>額外過濾條件（白名單驗證）</summary>
    public List<FilterCondition> Filters { get; set; }

    /// <summary>預留，Phase 2 快取用</summary>
    public string QueryHash { get; set; }
}

public class MeasureRequest
{
    public string Field { get; set; }
    public AggregateFunc Func { get; set; }
}

public class FilterCondition
{
    public string Field { get; set; }
    public FilterOperator Operator { get; set; }
    public string Value { get; set; }  // 統一 string，server-side 做 type conversion
}

public enum FilterOperator { Eq, Gt, Gte, Lt, Lte, Contains, In }

public class AnalysisQueryResponse
{
    public List<string> Columns { get; set; }           // 欄位名稱（有序）
    public List<Dictionary<string, object>> Rows { get; set; } // 資料列
    public int TotalCount { get; set; }                 // 總筆數（GroupBy 後）
    public bool Truncated { get; set; }                 // 是否因超過 10,000 筆而截斷
    public string QueryHash { get; set; }               // Phase 2 快取識別
}
```

---

## §5 動態 GroupBy 引擎

```csharp
public class AnalysisQueryEngine
{
    // 入口：拿到 IQueryable<TModel>，套上動態 GroupBy + 聚合
    public AnalysisQueryResponse Execute<TModel>(
        IQueryable<TModel> baseQuery,
        AnalysisQueryRequest req,
        IEnumerable<AnalysisFieldMeta> whitelist)
        where TModel : TopBasePoco
    {
        // 1. 驗證所有 Dimensions/Measures 在白名單內
        ValidateFields(req, whitelist);

        // 2. 套上 Filters（白名單驗證後 build Expression）
        var filtered = ApplyFilters(baseQuery, req.Filters, whitelist);

        // 3. 動態 GroupBy（Expression Tree）
        var grouped = ApplyGroupBy(filtered, req.Dimensions);

        // 4. 動態聚合 Projection
        var result = ApplyAggregation(grouped, req.Dimensions, req.Measures);

        // 5. 強制分頁上限
        const int MaxRows = 10_000;
        var rows = result.Take(MaxRows + 1).ToList();
        bool truncated = rows.Count > MaxRows;
        if (truncated) rows = rows.Take(MaxRows).ToList();

        return BuildResponse(req, rows, truncated);
    }
}
```

**EF Core GroupBy 限制因應**：
- 多維度 GroupBy 使用匿名物件（`new { d1, d2, d3 }`），Expression Tree 動態建立
- 若 EF Core 無法翻譯（偵測方式：catch `InvalidOperationException` with "client evaluation"），fallback 記錄 warning log，不 silently fallback 到 client evaluation
- 啟用 `EnableSensitiveDataLogging(false)` + 記錄 `ToQueryString()` 供開發期驗證

---

## §6 `_AnalysisController`

```csharp
[ApiController]
[Route("/_analysis")]
[AllRights]
public class _AnalysisController : BaseController
{
    // 返回可用維度/度量清單
    [HttpGet("meta")]
    public IActionResult GetMeta([FromQuery] string listVmType)

    // 執行分析查詢
    [HttpPost("query")]
    public IActionResult Query([FromBody] AnalysisQueryRequest req)

    // 匯出 Excel/CSV
    [HttpGet("export")]
    public IActionResult Export([FromQuery] string listVmType, [FromQuery] string format = "xlsx")
}
```

**Searcher 狀態傳遞流程**：
1. 前端切換到分析模式時，序列化 SearchPanel form data → JSON string → `SearcherFormData`
2. Controller 收到後：`JsonSerializer.Deserialize(req.SearcherFormData, searcherType)` → 反射 bind 到 Searcher 實例
3. 呼叫 `listVm.GetSearchQuery()` 取得已套用 Searcher 條件的 `IQueryable<TModel>`
4. 此 query 繼承 DC 的 global query filter（多租戶隔離自動生效）

---

## §7 前端設計（`framework_analysis.js`）

**UI 佈局**：

```
┌─ Analysis Panel ─────────────────────────────────────────────────┐
│  維度 □ 地區  □ 業務員  □ 下單月份                               │
│  度量 □ 金額 [SUM▼]  □ 筆數 [COUNT▼]                            │
│  ─────────────────────────────────────────────────────────────── │
│  [查詢]  [匯出 Excel]  [匯出 CSV]                                │
│  ─────────────────────────────────────────────────────────────── │
│  [表格]  [圖表]                  ← tab 切換                       │
│                                                                   │
│  ┌─ 表格 ──────────────────────┐  ┌─ 圖表 ──────────────────┐   │
│  │ 地區 | 業務員 | SUM(金額)   │  │ ECharts                 │   │
│  │ 華東 | 王大明 | 1,234,567   │  │ (Bar/Line/Pie           │   │
│  │ 華南 | 李小花 |   987,654   │  │  自動選型)              │   │
│  └────────────────────────────┘  └────────────────────────┘   │
└───────────────────────────────────────────────────────────────────┘
```

**ECharts 自動選型規則**：

| 維度數量 | 度量數量 | 自動選型 |
|----------|----------|----------|
| 1（非日期） | 1 | Bar（可切 Pie） |
| 1（日期） | 1+ | Line |
| 2 | 1 | Stacked Bar |
| 0 | 多 | 單值卡片 |

**限制規則（前端驗證）**：
- 維度最多選 3 個，超過禁用其他 checkbox
- 度量最多選 3 個
- 至少選 1 個維度或 1 個度量才能執行查詢

---

## §8 安全性清單

| 項目 | 措施 |
|------|------|
| VM 型別注入 | 白名單 `AnalysisVmRegistry`，拒絕未知 type |
| 欄位注入 | 所有 Dimension/Measure/Filter 欄位在白名單驗證後才進 Expression Tree |
| Filter 型別混淆 | `Value` 統一 string，server-side 依欄位型別安全轉換，失敗回 400 |
| 大資料量 DoS | GroupBy 結果強制 `Take(10_001)` 截斷 |
| 多租戶隔離 | 複用同一 DC 實例，global query filter 自動生效 |
| 權限 | `[AllRights]` 基礎登入驗證；Phase 2 評估 Dimension/Measure 欄位級權限 |

---

## §9 測試計畫

| 層次 | 工具 | 涵蓋範圍 |
|------|------|----------|
| 單元測試（xUnit） | SQLite in-memory | AnalysisQueryEngine GroupBy 邏輯（各維度組合）、FilterCondition 型別轉換、VM 白名單掃描 |
| 整合測試 | TestServer | `/meta`、`/query`、`/export` API 端到端 |
| JS 單元測試（Jest） | jsdom | 維度/度量選擇邏輯、ECharts 自動選型函式 |
| 安全測試 | 手動 | 未授權 VM type、超出白名單欄位、超大維度（高基數）查詢 |

---

## 工時估計（Phase 1）

| 模組 | 估計工時 |
|------|----------|
| Attribute + AnalysisVmRegistry + AnalysisFieldScanner | 2-3 天 |
| `_AnalysisController` + Meta API + Searcher 狀態傳遞 | 3-4 天 |
| 動態 GroupBy 引擎（Expression Tree） | 5-8 天 |
| `DataTableTagHelper` 修改 + Partial View | 2-3 天 |
| `framework_analysis.js`（UI + ECharts） | 6-8 天 |
| Excel/CSV 匯出 | 2 天 |
| 安全加固 + 測試 | 4-5 天 |
| **合計** | **24-33 天（約 5-7 週）** |

---

## 不做的事（Phase 1 範圍外）

- Pivot 樞紐表（延後 Phase 2）
- DateHierarchy 鑽取（延後 Phase 2）
- 結果快取（queryHash 已預留欄位）
- 圖表 drill-down
- 欄位級權限控制
- 行動裝置 RWD 優化
