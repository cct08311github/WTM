# WTM .NET 10 全面優化計畫

> 建立日期：2026-03-20
> 基於版本：10.0.0（net10.0 / EF Core 10.0.4 / ASP.NET Core 10.0.4）
> 目標：充分利用 .NET 10 LTS 新特性提升框架品質、效能和開發體驗

---

## 總覽

本計畫基於 WTM 實際程式碼分析，將優化項目按風險與收益分為四級：

| 等級 | 定義 | 預期項目數 |
|------|------|-----------|
| **P0** | 必做 — 低風險高收益，不改 API | 8 |
| **P1** | 應做 — 中等改造，顯著品質提升 | 7 |
| **P2** | 可做 — 需要較大重構或實驗 | 5 |
| **P3** | 探索 — 長期方向，待評估 | 3 |

實施分三階段：Phase 1（低風險快贏）→ Phase 2（中等改造）→ Phase 3（大型重構）。

---

## Phase 1：低風險快贏（P0）

### P0-01: `FrozenDictionary` / `FrozenSet` 取代啟動後只讀的 Dictionary

**現況分析**：
- `AnalysisVmRegistry._whitelist`（Dictionary<string, Type>）：Build() 後不再修改，每次 Query 都走 TryGetValue
- `AnalysisQueryEngine._funcDisplayNames`（static readonly Dictionary）：純只讀查表
- `LookupCacheService._registry`（Dictionary<Type, CacheLookupAttribute>）：ScanAssemblies() 後不再修改
- `DateRange.DateTimeFormatDic` / `DateTimeRegexDic`（static readonly Dictionary）：完全不可變
- `IconFontsHelper` 內的多個 static Dictionary
- `Utils` 內的 `systemdll` 字串陣列（用於 StartsWith 比對，可用 `FrozenSet`）

**方案**：
- 用 `System.Collections.Frozen.FrozenDictionary<K,V>` / `FrozenSet<T>` 取代上述欄位
- Build/Scan 方法先組好普通 Dictionary 再 `.ToFrozenDictionary()` 賦值

**影響範圍**：`AnalysisVmRegistry.cs`, `AnalysisQueryEngine.cs`, `LookupCacheService.cs`, `DateRange.cs`, `IconFontsHelper.cs`, `Utils.cs`
**預期收益**：讀取路徑 10-40% 延遲降低（FrozenDictionary 在 TryGetValue 路徑消除 hash collision 開銷）；對 Analysis 高頻查詢路徑有實質幫助
**風險**：極低 — `FrozenDictionary` 自 .NET 8 GA，API 穩定
**相依性**：無
**工作量**：**S**

---

### P0-02: `System.Threading.Lock` 取代 `object` lock

**現況分析**：
- `MemoryAnalysisCache._lock = new object()`
- `LookupCacheService._ctsLock = new object()`
- `Utils` 內有 3 處 `lock(...)` 使用 object

**方案**：
- 將 `private readonly object _lock = new();` 改為 `private readonly Lock _lock = new();`
- `System.Threading.Lock` 自 .NET 9 引入，.NET 10 已穩定，編譯器對 `lock(Lock)` 產生更高效的 IL（避免 Monitor.Enter overhead）

**影響範圍**：`MemoryAnalysisCache.cs`, `LookupCacheService.cs`, `Utils.cs`
**預期收益**：微幅效能提升 + 明確語意（Lock vs object）
**風險**：極低
**相依性**：無
**工作量**：**S**

---

### P0-03: Collection expressions `[..]` 語法現代化

**現況分析**：
- 程式碼中有 362 處 `new List<>()` / `new Dictionary<>()` / `new HashSet<>()`
- 許多可用 collection expressions 簡化，如 `new List<string>()` → `[]`
- `new string[] { ... }` → `[...]`

**方案**：
- 優先處理 Analysis、Cache、Core VM 等高頻觸及的檔案
- 不處理 TagHelper 的 HTML builder 串接（可讀性考量）

**影響範圍**：全 src 目錄，但分批逐模組進行
**預期收益**：程式碼簡潔度提升，減少 40+ 行雜訊；編譯器可能選擇更佳的底層集合
**風險**：極低 — 純語法糖
**相依性**：無
**工作量**：**M**（量大但機械性）

---

### P0-04: `string.Format` → 字串插值 / `CompositeFormat`

**現況分析**：
- 10 處 `string.Format` 散布於 `BasePagedListVM.cs`, `Utils.cs`, `ExcelPropety.cs`, `PagedListExtension.cs`
- 其中 `PagedListExtension.cs` 有 4 處，多為 SQL 分頁拼接

**方案**：
- 對已知 format string 的改為 `$"..."` 字串插值
- 對 format string 為變數/資源的高頻路徑用 `CompositeFormat.Parse()` 預編譯

**影響範圍**：`BasePagedListVM.cs`, `PagedListExtension.cs`, `Utils.cs`, `ExcelPropety.cs`
**預期收益**：消除 boxing、減少 allocation；`CompositeFormat` 可節省重複 parse 開銷
**風險**：低 — 需確認 SQL 拼接處不引入注入風險（已有參數化查詢機制）
**相依性**：無
**工作量**：**S**

---

### P0-05: `Convert.ToHexString` → `Span<T>` 優化 hash 計算

**現況分析**：
- `AnalysisQueryEngine.ComputeHash()` 用 `SHA256.HashData(Encoding.UTF8.GetBytes(raw))` 然後 `Convert.ToHexString(bytes).Substring(0, 16)`
- `PasswordHashHelper.ComputeMD5()` 用 `MD5.HashData(buffer)` 然後逐 byte `sb.Append(b.ToString("X2"))`

**方案**：
- `ComputeHash`：用 `Span<byte>` + `stackalloc` 避免 byte[] 分配（SHA256 hash = 32 bytes，適合 stackalloc）；用 `Convert.ToHexString(span).AsSpan(0, 16)` 避免中間字串
- `ComputeMD5`：同理用 `stackalloc byte[16]` + `Convert.ToHexString`

**影響範圍**：`AnalysisQueryEngine.cs`, `PasswordHashHelper.cs`
**預期收益**：每次 hash 計算減少 2-3 次堆積分配；Analysis 查詢路徑高頻受益
**風險**：極低
**相依性**：無
**工作量**：**S**

---

### P0-06: `TimeProvider` 注入取代直接 `DateTime.Now/Today/UtcNow`

**現況分析**：
- 96 處直接呼叫 `DateTime.Now`/`.Today`/`.UtcNow`，分布於 38 個檔案
- `AnalysisQueryEngine.ResolveRelativeDates()` 用 `DateTime.Today` — 直接耦合系統時鐘導致測試困難
- `TokenService` 用 `DateTime.UtcNow` 計算 token 過期
- `BaseCRUDVM` 8 處 `DateTime.Now` 用於 CreateTime/UpdateTime

**方案**：
- 在 `WTMContext` 注入 `TimeProvider`（DI 註冊 `TimeProvider.System` 作為預設）
- 核心 VM（BaseCRUDVM、BasePagedListVM）及 TokenService、Analysis 引擎改用 `TimeProvider.GetUtcNow()`
- 測試中注入 `FakeTimeProvider` 實現確定性測試

**影響範圍**：`WTMContext.cs`, `BaseCRUDVM.cs`, `BasePagedListVM.cs`, `TokenService.cs`, `AnalysisQueryEngine.cs`, `AnalysisExcelExporter.cs`, ETL scheduling
**預期收益**：可測試性大幅提升；消除 Analysis 日期篩選測試的時區脆弱性
**風險**：低 — `TimeProvider` 自 .NET 8 GA，API 穩定；需確保所有路徑一致切換
**相依性**：無
**工作量**：**M**

---

### P0-07: EF Core 10 `ExecuteUpdateAsync` / `ExecuteDeleteAsync` 批次操作

**現況分析**：
- `DataContext.cs` 有 10 處 `.Remove()` / `.SaveChanges()` / `DeleteRange` 操作
- `BaseCRUDVM.DoDelete()` 執行先 Load 再 Remove 再 SaveChanges 的三步刪除
- `WTMContext` 有 3 處類似模式
- JWT refresh token 清理（`JwtRefreshJob`）逐筆刪過期 token

**方案**：
- 批次刪除場景改用 `ExecuteDeleteAsync` — 單一 SQL DELETE WHERE，不需 Load 實體
- 批次更新場景改用 `ExecuteUpdateAsync` — 例如 soft-delete 更新 IsValid 欄位
- 保留 single-entity CRUD 的現有模式（change tracking 仍有價值）

**影響範圍**：`DataContext.cs`, `BaseCRUDVM.cs`, `JwtRefreshJob.cs`, `WTMContext.cs`
**預期收益**：批次操作效能提升 5-20 倍（省去 SELECT + 逐筆 DELETE 的 round-trip）
**風險**：低 — `ExecuteDeleteAsync` 自 EF Core 7 GA；注意不觸發 change tracker events（已知行為差異）
**相依性**：需確認是否有 SaveChanges 攔截器依賴被刪除實體的 change tracking
**工作量**：**M**

---

### P0-08: `#nullable disable` 殘留清理（持續進行）

**現況分析**：
- 掃描結果只剩 `BaseImportVM.cs` 1 個檔案有 `#nullable disable`（原計畫的 156 個已大幅清理）
- Core 專案已全域 `<Nullable>enable</Nullable>`

**方案**：
- 完成 `BaseImportVM.cs` 的 nullable annotation
- 對 Mvc、TagHelpers.LayUI、Etl 專案啟用 `<Nullable>enable</Nullable>`

**影響範圍**：`BaseImportVM.cs`；後續 Mvc/TagHelper/Etl 各專案
**預期收益**：編譯器靜態分析全面覆蓋，減少 NRE
**風險**：低 — 逐檔處理，不改執行時行為
**相依性**：無
**工作量**：BaseImportVM **S**；全專案啟用 **L**

---

## Phase 2：中等改造（P1）

### P1-01: `System.Text.Json` Source Generator 取代執行時反射序列化

**現況分析**：
- `CoreProgram.cs` 註冊 STJ 選項包含多個自定義 converter（PocoConverter, NullableEnumConverter 等共 14 個）
- `AnalysisQueryEngine.ComputeHash()` 用 `JsonSerializer.Serialize(req)` — 每次查詢都走反射序列化
- `DistributedCacheExtensions` 有 7 處 `JsonSerializer`
- `Dashboard/JsonFileDashboardService` 有 7 處

**方案**：
- 為 Analysis DTOs（`AnalysisQueryRequest`, `AnalysisQueryResponse`, `FilterCondition`）建立 `[JsonSerializable]` context
- 為 Dashboard DTOs 建立獨立的 source-gen context
- 逐步將高頻路徑的 `JsonSerializer.Serialize/Deserialize` 改用 source-gen context

**影響範圍**：新增 `AnalysisJsonContext.cs`, `DashboardJsonContext.cs`；修改 `AnalysisQueryEngine.cs`, `JsonFileDashboardService.cs`, `DistributedCacheExtensions.cs`
**預期收益**：序列化效能提升 20-40%，減少啟動時 JIT 及 trim-safe（為 Native AOT 鋪路）
**風險**：中 — 自定義 converter 的 source-gen 相容性需逐一驗證；PocoConverter 作為 Factory 可能不適用 source-gen
**相依性**：需先確認現有 custom converter 與 source-gen 的交互
**工作量**：**M**

---

### P1-02: Newtonsoft.Json 依賴評估與降級

**現況分析**：
- Mvc 專案仍引用 `Microsoft.AspNetCore.Mvc.NewtonsoftJson`
- 用途集中在 `NewtonsoftJsonFormatterAttribute.cs`（特定 API 的序列化格式）和 `MyNewtonsoftJsonConvention.cs`
- `FrameworkServiceExtension.cs` 註冊 Newtonsoft 作為替代 formatter

**方案**：
- 分析是否有 STJ 無法處理的序列化需求（如循環引用、$type 多型）
- 若可行，將 Newtonsoft-specific endpoints 遷移到 STJ polymorphic serialization（.NET 10 已大幅改善）
- 最終移除 `Microsoft.AspNetCore.Mvc.NewtonsoftJson` 依賴

**影響範圍**：`NewtonsoftJsonFormatterAttribute.cs`, `MyNewtonsoftJsonConvention.cs`, `FrameworkServiceExtension.cs`, `WalkingTec.Mvvm.Mvc.csproj`
**預期收益**：減少一個大型 transitive 依賴（Newtonsoft.Json），降低 NuGet 攻擊面
**風險**：中 — 需確認所有 API 行為不變，特別是 WTM 的 PocoConverter 動態序列化模式
**相依性**：P1-01 完成後評估更準確
**工作量**：**M**

---

### P1-03: `ImmutableDictionary` → `FrozenDictionary` 用於 PropertyCache

**現況分析**：
- `TypeExtension._propertyCache` 用 `ImmutableDictionary<string, List<PropertyInfo>>`
- 這是高頻讀取路徑（每次 model binding、序列化都會碰）
- `ImmutableDictionary` 的讀取效能不如 `FrozenDictionary`

**方案**：
- 改為 `ConcurrentDictionary` 建構期使用，startup 完成後用 `.ToFrozenDictionary()` 凍結
- 或直接用 `ConcurrentDictionary`（如果需要 runtime 新增）

**影響範圍**：`TypeExtension.cs`
**預期收益**：Model binding 路徑讀取加速（ImmutableDictionary TryGetValue = O(log n)，FrozenDictionary = O(1)）
**風險**：低 — 需確認是否有 runtime 動態新增的需求（若有，改用 ConcurrentDictionary）
**相依性**：無
**工作量**：**S**

---

### P1-04: Output Caching 用於 Analysis meta / Dashboard 定義

**現況分析**：
- `_AnalysisController.Meta()` GET 回傳 VM 欄位定義 — 啟動後幾乎不變
- `_DashboardController` 回傳 dashboard 定義 — 低頻變更
- 目前靠 `MemoryAnalysisCache` 自行管理快取

**方案**：
- 對 `GET /_analysis/meta` 加入 ASP.NET Core Output Caching（`[OutputCache(Duration = 300)]`）
- Dashboard 定義端點同理
- 保留 `MemoryAnalysisCache` 用於 query 結果（帶參數查詢不適合 Output Cache）

**影響範圍**：`_AnalysisController.cs`, `_DashboardController.cs`, `FrameworkServiceExtension.cs`（註冊 OutputCache）
**預期收益**：Meta 端點 QPS 可提升 10 倍+，減少反射掃描開銷
**風險**：低 — Output Cache 自 .NET 7 GA；需注意 RBAC 不同角色看到不同 meta 的情況
**相依性**：需確認 meta 是否依角色不同（若是，需加 VaryByRouteValue/VaryByQuery）
**工作量**：**S**

---

### P1-05: Primary Constructors 重構

**現況分析**：
- `AnalysisQueryEngine`、`LookupCacheService`、`TokenService`、`MemoryAnalysisCache` 等類別有標準建構子 + 欄位賦值模式
- 20+ 個服務類別符合此模式

**方案**：
- 對 DI 注入的服務類別改用 primary constructors
- 不改 Model/Entity 類別（它們需要無參建構子給 EF Core）

**影響範圍**：Analysis、Cache、Auth、ETL 的服務類別
**預期收益**：減少 boilerplate 約 3-5 行/類；可讀性提升
**風險**：極低 — 純語法糖
**相依性**：無
**工作量**：**S**

---

### P1-06: `Span<T>` / `ReadOnlySpan<T>` 優化字串處理

**現況分析**：
- `Utils.cs` 有 16 處 `.ToString()` / `.Substring()` 操作
- `WTMContext.ParentWindowId` 用 `.Split(',')` 然後取 `ids[ids.Length - 2]` — 可用 `AsSpan().LastIndexOf` 避免陣列分配
- `PasswordHashHelper.IsLegacyMD5Hash` 逐字元比對可用 `SearchValues<char>`
- `AnalysisQueryEngine.ApplyFilters` 中 `f.Value.Split(new[] { ',' })` 可用 `MemoryExtensions.Split`

**方案**：
- 高頻路徑（WTMContext property getters、Analysis filter parsing）改用 Span API
- 用 `SearchValues<char>` 預編譯字元集（如 hex digits）加速 pattern matching
- 低頻路徑保持現狀（可讀性優先）

**影響範圍**：`WTMContext.cs`, `Utils.cs`, `AnalysisQueryEngine.cs`, `PasswordHashHelper.cs`
**預期收益**：減少中間字串分配（每次 HTTP request 的 WTMContext 存取都受益）
**風險**：低 — Span API 自 .NET Core 2.1 穩定
**相依性**：無
**工作量**：**M**

---

### P1-07: `field` keyword 簡化 property 實作

**現況分析**：
- `WTMContext` 有大量 backing field + property 對：`_httpContext` + `HttpContext`、`_configInfo` + `ConfigInfo` 等
- `GlobalData` 有 `_customUserProperties` backing field 帶 lazy init

**方案**：
- 若 C# 14 `field` keyword 在 .NET 10 GA（目前為 preview feature），可簡化 property 宣告
- `public HttpContext? HttpContext { get => field; }` 取代 `private HttpContext? _httpContext; public HttpContext? HttpContext { get => _httpContext; }`

**影響範圍**：`WTMContext.cs`, `GlobalData.cs`
**預期收益**：程式碼簡潔度提升，減少 10+ 個 backing field
**風險**：中 — 需確認 `field` keyword 在 .NET 10 final 是否為 GA feature（若仍為 preview 則跳過）
**相依性**：C# 14 language version 確認
**工作量**：**S**

---

## Phase 3：大型重構（P2）

### P2-01: EF Core 10 Compiled Models

**現況分析**：
- `FrameworkContext` 有 12 個 DbSet + OnModelCreating 配置
- 應用層 DataContext 通常還有更多 entity
- 每次 DbContext 初始化都要執行 model building

**方案**：
- 使用 `dotnet ef dbcontext optimize` 產生 compiled model
- 在 startup 載入 compiled model 加速首次 DbContext 建立

**影響範圍**：新增 `CompiledModels/` 目錄；修改 `DataContext.cs` OnConfiguring
**預期收益**：DbContext 首次建立加速 50-80%（對 12+ entity 的 context）
**風險**：中 — compiled model 需隨 entity 變更重新產生；CI 流程需整合
**相依性**：EF Core migration 流程不受影響
**工作量**：**M**

---

### P2-02: Native AOT 準備度評估

**現況分析**：
- 大量反射使用：`AnalysisVmRegistry.Build()`、`AnalysisFieldScanner`、`AnalysisVmInvoker`（MakeGenericMethod + Invoke）
- `Utils.GetAllAssembly()` 動態載入 DLL
- `PocoConverter` 是 `JsonConverterFactory`，依賴 runtime reflection
- EF Core provider 配置動態選擇

**方案**：
- 不作為近期目標，但為未來 AOT 做準備：
  - P1-01 的 STJ Source Generator 是第一步
  - 標記已知不支援 AOT 的路徑（`[RequiresUnreferencedCode]`）
  - 移除不必要的 `Assembly.GetTypes()` 呼叫

**影響範圍**：全域評估
**預期收益**：中長期啟動效能和記憶體佔用大幅改善；Docker container cold start 縮短
**風險**：高 — WTM 的核心設計（runtime type scanning, dynamic VM instantiation）與 AOT 衝突嚴重
**相依性**：P1-01
**工作量**：**L**（僅評估 + 標記階段為 M）

---

### P2-03: JSON Column 支持用於 Analysis Saved Queries

**現況分析**：
- `AnalysisSavedQuery` 目前將查詢定義序列化為 JSON string 欄位儲存
- 查詢時需要反序列化才能存取內部結構

**方案**：
- 使用 EF Core 10 的 JSON column mapping，將 `QueryDefinition` 屬性直接映射為 JSON column
- 支援 LINQ 查詢 JSON 內部欄位（如依 measure field 過濾 saved queries）

**影響範圍**：`AnalysisSavedQuery.cs`, `DataContext.cs`
**預期收益**：查詢更靈活，不需客戶端反序列化；DB provider 差異由 EF Core 抽象
**風險**：中 — JSON column 支援程度因 DB provider 而異（SQLite 有限、PostgreSQL/MSSQL 較好）
**相依性**：需確認所有目標 DB provider 的 JSON column 支持狀態
**工作量**：**M**

---

### P2-04: OpenAPI (built-in) 取代 Swashbuckle

**現況分析**：
- Mvc 專案引用 3 個 Swashbuckle 套件（Swagger, SwaggerGen, SwaggerUI）版本 10.1.5
- ASP.NET Core 10 內建 `Microsoft.AspNetCore.OpenApi`

**方案**：
- 用 ASP.NET Core 10 內建 `MapOpenApi()` 取代 Swashbuckle
- 保留 Swagger UI 作為獨立前端（或用 Scalar）

**影響範圍**：`FrameworkServiceExtension.cs`, `WalkingTec.Mvvm.Mvc.csproj`
**預期收益**：減少 3 個 NuGet 依賴；官方 OpenAPI 與框架版本同步更新
**風險**：中 — Swashbuckle 的自定義 filter（如 WTM 可能有的 schema filter）需逐一遷移
**相依性**：無
**工作量**：**M**

---

### P2-05: Rate Limiting 增強用於 Analysis 端點

**現況分析**：
- WTM 8.2.0 已加入 Rate Limiting
- Analysis query 端點（POST /_analysis/query）是計算密集型 — 可受益於更精細的限流

**方案**：
- 使用 ASP.NET Core 10 增強的 Rate Limiting API，針對 Analysis 端點設定獨立的 sliding window 策略
- 按 user identity 限流而非全域限流
- 考慮加入 concurrency limiter（限制同時執行的 Analysis query 數量）

**影響範圍**：`FrameworkServiceExtension.cs`, `_AnalysisController.cs`
**預期收益**：防止 Analysis 大量查詢拖慢整體系統；保護 DB 資源
**風險**：低
**相依性**：無
**工作量**：**S**

---

## 探索項目（P3）

### P3-01: Extension Types（C# 14 preview）

**現況分析**：
- `DCExtension.cs`、`TypeExtension.cs`、`EnumExtension.cs` 等大量 static extension methods
- Extension types 可提供更自然的語法

**評估**：Extension types 在 .NET 10 / C# 14 可能仍為 preview。若 GA，可將重要的 extension method 群組重構為 extension types。暫列為觀察項目。

**工作量**：**L**

---

### P3-02: `IAsyncEnumerable` Streaming 用於大型 Analysis Export

**現況分析**：
- Analysis export（XLSX/CSV）目前先完整查詢再一次寫入
- ETL 管線已使用 `IAsyncEnumerable`

**評估**：大型 export 可改為 streaming 產生 CSV（逐行），減少記憶體峰值。XLSX 因格式限制（需 seek）較難 streaming。需評估實際記憶體壓力是否已是問題。

**工作量**：**M**

---

### P3-03: Minimal API 端點（作為 MVC Controller 的替代選項）

**現況分析**：
- 所有 API 端點（`_AnalysisController`, `_DashboardController` 等）使用 MVC controller
- Minimal API 在 .NET 10 已支持 filter、auth、OpenAPI

**評估**：WTM 的核心價值是 MVC + VM 模式，Minimal API 適合無狀態 API 端點。可考慮為 Analysis/Dashboard 等純 API 端點提供 Minimal API 版本作為可選。不建議替換現有 controller — 會破壞使用者的 override 慣例。

**工作量**：**L**

---

## 不做清單

| 特性 | 不做原因 |
|------|----------|
| **`params` collections** | WTM 幾乎沒有 `params` 方法；改動無實質收益 |
| **Multi-targeting (net8.0 + net10.0)** | 無具體使用者需求；增加 CI 複雜度，不符「除非有 concrete user need」原則 |
| **Blazor SSR / InteractiveServer** | WTM 的前端策略是 LayUI + jQuery + ECharts，不在 Blazor 生態；轉換成本極高且無明確 ROI |
| **`stackalloc` 大範圍使用** | 僅適用於已知小 buffer（hash、格式化）；濫用有 stack overflow 風險 |
| **gRPC endpoint** | WTM 使用場景為後台管理，HTTP JSON 已足夠；gRPC 增加客戶端複雜度 |
| **新的 cryptography API (CryptographicOperations)** | `PasswordHashHelper` 已用 BCrypt.Net-Next + SHA256.HashData，足夠安全；無需切換 |
| **Data Protection API 改進** | WTM 的加密已用 AES-256 獨立實作，且與 Data Protection 體系無交集 |
| **Response Compression middleware 更新** | WTM 主要是後台系統，response body 不大；壓縮收益有限 |

---

## 實施時程建議

| 階段 | 包含項目 | 預估工期 | 里程碑 |
|------|----------|----------|--------|
| **Phase 1a** | P0-01, P0-02, P0-04, P0-05 | 1-2 天 | 零破壞性快速效能提升 |
| **Phase 1b** | P0-03, P0-06, P0-08 | 2-3 天 | 程式碼現代化 + 可測試性 |
| **Phase 1c** | P0-07 | 1-2 天 | EF Core 批次操作 |
| **Phase 2a** | P1-01, P1-03, P1-05 | 2-3 天 | STJ Source Gen + Primary Constructors |
| **Phase 2b** | P1-04, P1-06, P1-07 | 2-3 天 | Output Cache + Span 優化 |
| **Phase 2c** | P1-02 | 1-2 天 | Newtonsoft 評估與移除 |
| **Phase 3** | P2-01 ~ P2-05 | 各 1-3 天 | 依優先順序逐項評估 |

**總預估**：Phase 1 約 5-7 天，Phase 2 約 5-8 天，Phase 3 依需求展開。

---

## 追蹤方式

每個 P0/P1 項目開工前建立 GitHub Issue，格式：
```
feat(core): [P0-01] FrozenDictionary for read-only registries
```

PR 連結對應 issue，合併後更新 CHANGELOG.md。
