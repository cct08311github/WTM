# Code Generator Analysis Mode 整合設計

> 日期：2026-03-11
> 狀態：已核准
> 相關：Analysis Mode (`docs/analysis-mode.md`)、Code Generator (`CodeGenVM.cs`)

## 目標

讓 WTM Code Generator 能自動產生 Analysis Mode 所需的 `[EnableAnalysis]`、`[Dimension]`、`[Measure]` attribute，減少開發者手動標記的工作。

## 設計決策

| 決策 | 選擇 | 理由 |
|------|------|------|
| UI 框架範圍 | 僅 LayUI | Analysis 前端目前只有 LayUI 實作 |
| 欄位選擇方式 | 手動勾選 + 智慧預設 | 平衡便利性與控制力 |
| 生成範圍 | 僅 Attribute（不產生額外頁面） | `framework_analysis.js` 已提供完整前端 UI |
| Attribute 位置 | 自動插入 Model .cs 檔案 | Scanner 掃描 Model type，attribute 必須在 Model 上 |

## 架構

### 改動檔案清單

| 檔案 | 改動類型 | 說明 |
|------|----------|------|
| `CodeGenListVM.cs` | 修改 | `InitGridHeader()` 新增 IsDimension/IsMeasure 欄、`GetSearchQuery()` 智慧預設、`CodeGenListView` 新增屬性 |
| `CodeGenVM.cs` | 修改 | 新增 `EnableAnalysis` 屬性、`GenerateVM("ListVM")` 加入 analysis attribute、新增 `InjectAnalysisAttributes()` 方法 |
| `GeneratorFiles/ListVM.txt` | 修改 | 加入 `$analysisusing$` 和 `$analysisattr$` 佔位符 |
| `FieldInfo` (CodeGenVM.cs 底部) | 修改 | 新增 `IsDimensionField`、`IsMeasureField` 屬性 |

### 不改動的部分

- `AnalysisFieldScanner` — 繼續掃描 Model type 屬性上的 attribute
- `AnalysisVmRegistry` — startup 自動掃描 `[EnableAnalysis]`
- `_AnalysisController` — 不需修改
- `framework_analysis.js` — 不需修改
- 其他 UI 框架模板 — 不在此次範圍

## UI 變更

### Code Generator 表格

在現有 checkbox 欄（IsSearcher、IsList、IsForm、IsImport、IsBatch）後面新增：

```
| ... | IsBatch | IsDimension | IsMeasure |
```

這兩欄僅在頂層 `EnableAnalysis` checkbox 勾選時顯示。

### 頂層控制

`CodeGenVM` 新增 `EnableAnalysis` bool 屬性，在 Code Generator 頁面以 checkbox 呈現。

## 智慧預設規則

在 `CodeGenListVM.GetSearchQuery()` 中，根據屬性型別設定初始值：

| 屬性型別 | IsDimension 預設 | IsMeasure 預設 |
|----------|------------------|----------------|
| `string`, `enum` | `true` | `false` |
| `decimal`, `int`, `double`, `float`, `long` | `false` | `true` |
| `DateTime` | `true` | `false` |
| 其他（bool, Guid, 關聯物件等） | `false` | `false` |

開發者可在 UI 上覆寫所有預設值。

## 生成邏輯

### ListVM 生成

當 `EnableAnalysis = true` 時，`GenerateVM("ListVM")` 額外：

1. 替換 `$analysisusing$` → `using WalkingTec.Mvvm.Core.Analysis;`
2. 替換 `$analysisattr$` → `[EnableAnalysis]`

當 `EnableAnalysis = false` 時，兩個佔位符替換為空字串。

### Model 檔案 Attribute 插入

新方法 `InjectAnalysisAttributes()` 流程：

1. 從 `SelectedModel` 取得 Model type 的 full name
2. 根據 model namespace + 專案目錄結構推斷 Model .cs 檔案路徑
3. 讀取檔案內容
4. 對每個勾選了 IsDimension/IsMeasure 的 field：
   - 正則匹配 `public {Type} {FieldName} {`
   - 檢查前面是否已有 `[Dimension]` / `[Measure]`（幂等）
   - 在屬性宣告行前插入 attribute
5. 如果檔案沒有 `using WalkingTec.Mvvm.Core.Analysis`，在 using 區塊末尾加上
6. 寫回檔案

#### 正則策略

```csharp
// 匹配屬性宣告行（含縮排）
var pattern = @"(\s*)(public\s+\S+\??\s+" + Regex.Escape(fieldName) + @"\s*\{)";
// 在前面插入 [Dimension] 或 [Measure]
var replacement = "$1[Dimension]\n$1$2";
```

#### DateTime 特殊處理

DateTime 型別的 Dimension 預設加上 `Hierarchy = DateHierarchy.Month`：

```csharp
[Dimension(Hierarchy = DateHierarchy.Month)]
public DateTime CreateTime { get; set; }
```

#### 邊界處理

- 屬性已有對應 attribute → 跳過（幂等保證）
- 找不到 Model source file → 輸出提示訊息，不中斷生成
- Model 在不同專案/目錄 → 嘗試從 MainDir 往上搜尋

## 資料模型變更

### CodeGenListView 新增屬性

```csharp
[Display(Name = "Codegen.IsDimensionField")]
public bool IsDimensionField { get; set; }

[Display(Name = "Codegen.IsMeasureField")]
public bool IsMeasureField { get; set; }
```

### FieldInfo 新增屬性

```csharp
public bool IsDimensionField { get; set; }
public bool IsMeasureField { get; set; }
```

### CodeGenVM 新增屬性

```csharp
[Display(Name = "Codegen.EnableAnalysis")]
public bool EnableAnalysis { get; set; }
```

## 測試策略

1. **ListVM 生成測試**：驗證 `EnableAnalysis = true/false` 時 ListVM 輸出的差異（有/無 `[EnableAnalysis]` 和 using）
2. **InjectAnalysisAttributes 測試**：
   - 正確插入 `[Dimension]`/`[Measure]` 到屬性前
   - 幂等性（重複執行不重複插入）
   - DateTime → `Hierarchy = DateHierarchy.Month`
   - 自動加入 using 語句
   - 找不到檔案時的優雅降級
3. **智慧預設測試**：驗證各型別的預設 IsDimension/IsMeasure 值

## 實作拆解

### Issue 1: UI 層 — CodeGenListVM + FieldInfo + CodeGenVM 屬性

- `CodeGenListView` 新增 `IsDimensionField`、`IsMeasureField`
- `FieldInfo` 新增對應屬性
- `CodeGenVM` 新增 `EnableAnalysis`
- `CodeGenListVM.InitGridHeader()` 新增兩欄 checkbox
- `CodeGenListVM.GetSearchQuery()` 加入智慧預設邏輯

### Issue 2: ListVM 模板 — EnableAnalysis attribute 生成

- `ListVM.txt` 加入 `$analysisusing$` 和 `$analysisattr$` 佔位符
- `CodeGenVM.GenerateVM("ListVM")` 處理替換邏輯

### Issue 3: Model 檔案插入 — InjectAnalysisAttributes

- 新方法 `InjectAnalysisAttributes()`
- 檔案路徑推斷邏輯
- 正則插入 + 幂等保護
- using 語句注入
- `CodeGenVM.DoGen()` 在生成結束後呼叫

### Issue 4: 測試 + 文件

- MSTest 測試覆蓋上述三個 issue
- 更新 `docs/analysis-mode.md` 加入 Code Generator 整合說明
