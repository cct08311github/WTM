# Analysis Mode v2 — 拖拉式 BI 面板設計

**日期**: 2026-03-12
**狀態**: 設計已確認，待實作
**關聯**: 現有 Analysis Mode 文件 `docs/analysis-mode.md`

## 目標

將 Analysis Mode 從 checkbox 勾選介面改為拖拉式 BI 面板體驗，同時解決兩個問題：
1. 分析圖表與列表不再二擇一，三者（搜尋面板、分析面板、列表）同時可見且可摺疊
2. 欄位操作從靜態 checkbox 改為拖拉式，更接近 Pivot Table / BI 工具體驗

## 設計決策記錄

| 決策 | 選項 | 選定 | 理由 |
|------|------|------|------|
| 佈局 | A.全寬水平 / B.左側欄 / C.緊湊水平 | **C** | 佔垂直空間最少，與搜尋面板風格一致，不擠壓列表寬度 |
| 圖表區塊 | A.在分析面板內 / B.獨立區塊 | **B** | 收合欄位選擇器後圖表仍可見，更靈活 |
| 拖拉實作 | A.原生HTML5 DnD / B.SortableJS | **B** | 跨區域拖拉 edge case 少，支援觸控，10KB gzip |

## 佈局架構

頁面垂直堆疊四個區塊，各自獨立可摺疊：

```
┌─────────────────────────────────────────────┐
│ 🔍 搜尋面板（LayUI collapse，已有功能）      │ ← 可摺疊
├─────────────────────────────────────────────┤
│ 📊 分析欄位選擇器（新）                      │ ← 可摺疊
│  維度：[地區 ✕] [日期(月) ✕] [拖入...]       │
│  度量：[金額 Sum ✕] [拖入...]                │
│  欄位：○客戶名稱 ○等級 ○狀態 ○付款 ○數量     │
│  [查詢] [Excel] [CSV] ☐樞紐 ☐含圖表匯出     │
├─────────────────────────────────────────────┤
│ 分析結果（新獨立區塊）                       │ ← 可摺疊
│  [bar] [line] [pie] [card]  drill: 地區>月   │
│  ┌──────────────────────────────────┐       │
│  │         ECharts 圖表              │       │
│  └──────────────────────────────────┘       │
│  地區    | 金額 Sum                          │
│  北部    | $523,400                          │
│  南部    | $612,800                          │
├─────────────────────────────────────────────┤
│ 📋 資料列表（LayUI table，已有功能）          │ ← 始終可見
└─────────────────────────────────────────────┘
```

### 收合狀態

欄位選擇器收合時顯示摘要列：
```
📊  維度：[地區] [訂單日期(月)]  度量：[金額 Sum]  [重新查詢] [▼ 展開]
```

## 元件設計

### 1. 欄位 Pill

每個分析欄位（維度/度量）以 pill 形式呈現：

- **可用欄位 pill**（白底）：可拖拉到維度或度量 drop zone
- **維度 pill**（綠色）：在 drop zone 內，可拖拉排序、可移除（✕）
  - 日期維度額外顯示階層下拉（年/季/月/日）
- **度量 pill**（藍色）：在 drop zone 內，可移除
  - 顯示聚合函式下拉（Sum/Avg/Count/Max/Min，根據 `AllowedFuncs`）
- **已用欄位 pill**（灰底 + 刪除線）：表示此欄位已在某個 drop zone 中

### 2. Drop Zone

兩個 drop zone：維度（綠色虛線框）和度量（藍色虛線框）。

- 空狀態顯示「拖入...」placeholder
- SortableJS `group` 設定允許：
  - 從欄位清單拖入 drop zone（clone 模式，原始 pill 變灰）
  - drop zone 內排序（維度順序影響 GroupBy 層次）
  - 從 drop zone 拖回欄位清單（移除）
  - 不允許維度和度量互拖（型別不同）
- 最多 3 個維度、3 個度量（現有驗證規則不變）

### 3. 查詢流程

1. 使用者拖拉欄位到 drop zone
2. 點擊「查詢」按鈕
3. JS 收集 drop zone 中的 pill 資訊 → 組成 `AnalysisQueryRequest`
4. 同時收集搜尋面板表單資料 → `searcherFormData`（已於 `collectSearcherFormData()` 支援）
5. POST `/_analysis/query` → 後端不需任何修改
6. 渲染結果到獨立的圖表結果區塊
7. 欄位選擇器自動收合為摘要列

### 4. Pivot 模式

樞紐模式的 UI 整合到新面板中：

- 欄位選擇器的按鈕列有「☐ 樞紐」checkbox（與現有行為一致）
- 勾選後，維度 drop zone 中每個維度 pill 旁出現 radio button，供選擇 pivot 維度
- 查詢時自動切換端點為 `/_analysis/pivot`，request 帶 `pivotDimension`
- 結果區塊的聚合表格改用 pivot 交叉表格式渲染（現有 `renderPivotTable` 邏輯保留）
- 取消勾選時恢復普通查詢模式

### 5. Drill-down 行為

保留現有 drill-down 機制，適配新的獨立結果區塊：

- 圖表中點擊某個維度值（如「北部」）觸發 drill-down
- drill stack 記錄在 state 中，結果區塊標題列顯示麵包屑（如 `drill: 地區 > 北部 > 月份`）
- 麵包屑支援點擊返回任一層級
- 「重置」按鈕清空 drill stack 回到頂層
- drill 查詢同樣帶入 `searcherFormData`

### 6. 摺疊行為

| 區塊 | 觸發 | 收合效果 |
|------|------|----------|
| 搜尋面板 | LayUI collapse（已有） | 隱藏表單欄位 |
| 欄位選擇器 | 點擊收合按鈕 / 查詢完成自動收合 | 只顯示摘要列（目前選取的 pills） |
| 圖表結果 | 點擊收合按鈕 | 隱藏圖表和聚合表格 |
| 資料列表 | 不可收合 | 始終可見 |

## 狀態管理

每個 grid 獨立一組面板實例，state 結構：

```javascript
_state[gridId] = {
    visible: boolean,           // 分析面板是否可見
    collapsed: boolean,         // 欄位選擇器是否收合
    resultCollapsed: boolean,   // 結果區塊是否收合
    listVmType: string,         // ListVM 全名
    fields: AnalysisFieldMeta[],// 從 /_analysis/meta 載入的欄位清單
    dims: string[],             // drop zone 中的維度欄位名（有序）
    msrs: MeasureSelection[],   // drop zone 中的度量 [{field, func}]
    dimHierarchies: object,     // 日期維度的階層選擇 {fieldName: 'Month'}
    pivotEnabled: boolean,      // 是否啟用樞紐模式
    pivotDim: string|null,      // 選定的 pivot 維度
    drillStack: DrillFrame[],   // drill-down 堆疊 [{filters, hierarchies}]
    drillFilters: Filter[],     // 目前 drill 篩選條件
    lastReq: object,            // 最後一次查詢 request（供 drill/chart toggle 重用）
    lastResult: object,         // 最後一次查詢結果
    lastDimFields: object[],    // 最後一次查詢的維度欄位 meta
    sortableInstances: object   // SortableJS 實例引用（供銷毀用）
};
```

同頁多個 `enable-analysis="true"` grid 時，各自擁有獨立的 state 和 DOM 面板，互不影響。

## 技術方案

### 檔案變更清單

| 檔案 | 變更類型 | 說明 |
|------|----------|------|
| `src/WalkingTec.Mvvm.Mvc/framework_analysis.js` | **重寫** | 新的拖拉面板、獨立結果區塊、收合邏輯 |
| `src/WalkingTec.Mvvm.Mvc/framework_analysis.css` | **新建** | 分析面板樣式（pill、drop zone、收合動畫） |
| `src/WalkingTec.Mvvm.Mvc/sortable.min.js` | **新建** | SortableJS library (~10KB gzip)，作為 EmbeddedResource |
| `src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj` | 修改 | 加入新的 EmbeddedResource |
| `src/WalkingTec.Mvvm.Mvc/FrameworkServiceExtension.cs` | 修改 | 註冊新的 embedded static files |
| `src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.cs` | 修改 | 分析面板 DOM 結構改為新佈局，引入 CSS/SortableJS |
| `test/WalkingTec.Mvvm.Js.Tests/__tests__/analysis/` | **重寫** | 測試更新配合新 API |

### 後端

**不需要任何後端修改。** 所有 API 端點（`/_analysis/meta`、`/_analysis/query`、`/_analysis/export`、`/_analysis/pivot`）和資料模型保持不變。前端只是改變了收集和呈現資料的方式。

### SortableJS 整合

- 下載 `Sortable.min.js` (v1.15.6, MIT license, SHA-256 校驗後) 放入 `src/WalkingTec.Mvvm.Mvc/`
- 作為 EmbeddedResource 打包，由 `UseWtmStaticFiles()` 提供服務
- 前端透過 `<script src="/_js/sortable.min.js">` 載入
- 只在 `enable-analysis="true"` 的頁面載入

### CSS 策略

新建 `framework_analysis.css` 作為 EmbeddedResource：
- `.analysis-panel` — 欄位選擇器容器
- `.analysis-pill` — 基礎 pill 樣式
- `.analysis-pill--dim` — 維度 pill（綠色）
- `.analysis-pill--msr` — 度量 pill（藍色）
- `.analysis-pill--used` — 已使用欄位（灰色 + 刪除線）
- `.analysis-dropzone` — drop zone 虛線框
- `.analysis-dropzone--dim` — 維度 drop zone（綠色）
- `.analysis-dropzone--msr` — 度量 drop zone（藍色）
- `.analysis-dropzone--highlight` — 拖拉 hover 高亮
- `.analysis-result` — 圖表結果獨立區塊
- `.analysis-summary-bar` — 收合狀態摘要列

所有 class 以 `analysis-` 為前綴，避免與 LayUI 或使用者 CSS 衝突。

收合/展開動畫：CSS transition, `max-height` + `opacity`, 250ms ease-out。

### 向後相容性

- `enable-analysis="true"` 屬性不變
- `wtmAnalysis.toggle(gridId, vmFullName)` 公開 API 不變
- 後端 API 不變
- 現有的 `[Dimension]`、`[Measure]`、`[EnableAnalysis]` attribute 不變
- 唯一破壞性變更：`framework_analysis.js` 的內部結構重寫，但這是 framework 內部實作，不影響使用者程式碼

## 安全考量

- **XSS**: 所有欄位名稱和值繼續使用 `textContent` / DOM 方法設值，不拼接 HTML 字串
- **白名單驗證**: 後端 `AnalysisQueryEngine` 的欄位白名單驗證不變
- **SortableJS**: MIT license，無已知安全漏洞，從官方 CDN/npm 下載後校驗 integrity

## 測試計畫

### JS 測試（Jest）
- pill 拖拉事件：從欄位清單到 drop zone、drop zone 內排序、移除
- 收合/展開狀態切換
- 摘要列正確顯示目前選取
- 已用欄位灰顯邏輯
- 日期階層下拉、聚合函式下拉
- 最大維度/度量數量限制
- `collectSearcherFormData` 整合（已有測試）
- 圖表渲染（已有測試，需適配新 DOM 結構）

### 手動測試
- 拖拉操作在 Chrome/Firefox/Safari 正常
- 觸控裝置（iPad）基本可用
- 搜尋面板篩選 → 分析查詢正確帶入條件
- 三個區塊獨立摺疊/展開不互相影響
- 匯出 Excel/CSV 帶入正確的維度/度量/搜尋條件

## 不在範圍內

- Dashboard 前端 UI（獨立 issue）
- 欄位拖拉排序影響 GroupBy 優先順序的視覺提示（可後續優化）
- 自動查詢（拖拉後自動觸發，不需按按鈕）— 可能造成過多請求，暫不實作
- 常用組合/模板儲存 — 未來 feature
- 鍵盤無障礙（keyboard-only drag-drop fallback）— SortableJS 不原生支援，成本高
