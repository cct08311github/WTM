# Lookup Cache — 靜態/參數表自動快取

**版本**：v8.4.0+

## 適用場景

城市代碼、狀態字典、品類、幣別等低異動頻率的參照表。每次請求都打 DB 查詢此類資料是不必要的開銷；貼一個 Attribute 即可讓框架自動快取。

> **不適用**：資料量超過 10,000 筆、需要即時一致性（金融交易明細等）的表。

---

## 快速開始

### 1. 在 Model 上標記 `[CacheLookup]`

```csharp
using WalkingTec.Mvvm.Core.Cache;

[CacheLookup(TtlMinutes = 60)]
public class CityCode : BasePoco
{
    public string Name { get; set; } = null!;
    public string Province { get; set; } = null!;
    public bool IsActive { get; set; } = true;
}
```

### 2. 在任意 VM 或 Controller 內存取

```csharp
// 取全表（回傳 IReadOnlyList<T>，防止意外修改快取內容）
var cities = Wtm.GetLookup<CityCode>();

// 在記憶體中過濾（Func<T, bool>，不觸發額外 DB 查詢）
var active = Wtm.GetLookup<CityCode>(x => x.IsActive && x.Province == "北部");

// 非同步版本
var cities = await Wtm.GetLookupAsync<CityCode>();
var active = await Wtm.GetLookupAsync<CityCode>(x => x.IsActive);
```

> **零設定**：快取失效已整合進 `FrameworkContext.SaveChanges()` 與 `SaveChangesAsync()`。只要透過 WTM 的 DC 寫入資料，快取即自動失效，無需任何 `Program.cs` 修改。

> **注意**：透過 `EmptyContext` 或原生 EF Core `DbContext` 直接寫入時，**不會**觸發自動失效。

> **IReadOnlyList 回傳型別**：`GetLookup` 和 `GetLookupAsync` 回傳 `IReadOnlyList<T>` 而非 `List<T>`，防止呼叫端意外修改快取中的物件。若需要可變集合，可 `.ToList()` 複製一份。

---

## Attribute 參數

| 參數 | 預設值 | 說明 |
|------|--------|------|
| `TtlMinutes` | `30` | 快取存活時間（分鐘） |
| `TenantIsolation` | 未設定（使用全域預設） | 依 TenantCode 隔離快取鍵，避免租戶資料互串。未設定時使用 `LookupCacheOptions.DefaultTenantIsolation`（預設 `true`） |
| `WarmOnStartup` | `true` | 應用啟動後在後台預熱快取（不阻擋啟動） |
| `ConnectionKey` | `null` | 指定此 Model 所在的資料庫連線鍵（對應 appsettings.json 中 Connections 的 Key）。為 null 時使用預設 DC |

---

## 全域 TenantIsolation 預設值

單一租戶的應用不需要每個 Model 都設定 `TenantIsolation = false`。在 `Program.cs` 註冊全域預設值：

```csharp
// Program.cs
builder.Services.AddSingleton(new LookupCacheOptions
{
    DefaultTenantIsolation = false  // 單租戶應用：關閉租戶隔離
});

// 之後照常呼叫
builder.Services.AddWtmContext(builder.Configuration);
```

個別 Attribute 可覆蓋全域預設：

```csharp
// 即使全域預設 false，此 Model 仍啟用租戶隔離
[CacheLookup(TenantIsolation = true)]
public class TenantSpecificDict : BasePoco { ... }
```

---

## 多資料庫支援（ConnectionKey）

若應用有多個資料庫（例如主庫 + 唯讀 ORSS 庫），可透過 `ConnectionKey` 指定 Model 所在的連線：

```json
// appsettings.json
{
  "Connections": [
    { "Key": "default", "Value": "...", "DbType": "SqlServer" },
    { "Key": "orss", "Value": "...", "DbType": "SqlServer" }
  ]
}
```

```csharp
[CacheLookup(TtlMinutes = 120, ConnectionKey = "orss")]
public class OrssProduct : BasePoco
{
    public string ProductCode { get; set; } = null!;
    public string ProductName { get; set; } = null!;
}

// 使用方式不變，框架會自動使用 orss 連線
var products = Wtm.GetLookup<OrssProduct>();
```

---

## 便利方法

### GetLookupItem — 查找單筆資料

```csharp
// 從快取中查找單筆，回傳 T? （找不到時為 null）
var city = Wtm.GetLookupItem<CityCode>(x => x.Name == "台北");
```

### GetLookupSelectList — 下拉選單整合

```csharp
// 從快取生成 ComboSelectListItem 下拉選項
var options = Wtm.GetLookupSelectList<CityCode>(
    valueField: x => x.ID,
    textField: x => x.Name,
    filter: x => x.IsActive
);
```

---

## 快取行為

### 快取鍵格式

```
wtm:lookup:{type.FullName}:{tenantId}
```

`tenantId` 為空（main tenant）時使用 `_`。

### 失效機制

| 時機 | 觸發 | 範圍 |
|------|------|------|
| 每次 `SaveChanges` / `SaveChangesAsync` | `FrameworkContext` 內建 override | 被寫入實體的所有租戶 |
| TTL 到期 | IMemoryCache 自動 | 當前 key |
| 手動 | `ILookupCacheService.Invalidate<T>()` | 指定型別 + 租戶 |
| 手動（跨租戶） | `ILookupCacheService.InvalidateType(type)` | 指定型別全部租戶 |

> **注意**：`EmptyContext` 不繼承 `FrameworkContext`，因此透過 `EmptyContext` 寫入不會觸發自動失效。

### Startup Warm-up

標記 `WarmOnStartup = true`（預設）的型別會在應用啟動後 3 秒由 `LookupCacheWarmupService` 在後台預熱。

- 失敗只記 warning log，不阻擋啟動
- 僅預熱 main tenant（`TenantCode = null`），其他租戶在首次請求時自動暖機

### Stampede Protection

`IMemoryCache.GetOrCreate` / `GetOrCreateAsync` 並非原子操作 — 在 cache miss 時，多個並發請求可能各自執行一次 DB 查詢。這是安全的，因為每個呼叫者使用自己的 scoped `DbContext`，最壞情況是一次 burst 內多讀一次 DB。

### 過濾語義

`GetLookup<T>(predicate)` 的 `predicate` 參數是 `Func<T, bool>`，在記憶體中執行，不會轉換為 SQL。全表資料先從快取載入，再在記憶體中過濾。

---

## 多副本（多 Pod）部署說明

`IMemoryCache` 是 process 內快取，多個 Pod 之間不共享。在 N 個 Pod 環境下：

- Write 只清發起寫入的那台 Pod 的快取
- 其他 Pod 的快取在 TTL 內仍是舊資料

若需要強一致性，可在 TTL 設定較短（如 `TtlMinutes = 1`）接受短暫的最終一致性，或在未來升級至 `IDistributedCache`（Redis）方案。

---

## 手動操作快取

```csharp
// 注入或取得服務
var cacheSvc = HttpContext.RequestServices.GetRequiredService<ILookupCacheService>();

// 失效特定租戶的 CityCode 快取
cacheSvc.Invalidate<CityCode>(tenantId: "tenant1");

// 失效所有租戶的 CityCode 快取（admin 批次更新後呼叫）
cacheSvc.InvalidateType(typeof(CityCode));
```

---

## 架構說明

```
[CacheLookup] Attribute
        ↓
LookupCacheService（Singleton）
  ├── startup 掃描所有 Assembly，建立型別白名單
  ├── GetAll<T>(): IMemoryCache.GetOrCreate（防 stampede）
  ├── InvalidateType(): 取消 per-type CancellationTokenSource，批次清除所有租戶 key
  ├── GetAttribute(): 查詢型別的 CacheLookupAttribute
  ├── DefaultTenantIsolation: 全域預設值（從 LookupCacheOptions 取得）
  └── GetWarmupTypes(): 供 LookupCacheWarmupService 使用

FrameworkContext（內建，無需設定）
  └── SaveChanges/SaveChangesAsync override:
        ├── CollectDirtyLookupTypes(): 從 ChangeTracker 取出有 [CacheLookup] 的型別
        └── InvalidateLookups(): 對每個型別呼叫 InvalidateType

LookupCacheWarmupService（BackgroundService）
  └── ExecuteAsync: 3s 延遲後預熱 WarmOnStartup=true 的型別

WTMContext.GetLookup<T>()
  ├── ConnectionKey → CreateDC(cskey) 或使用預設 DC
  ├── TenantIsolationOrNull → 全域預設 fallback
  └── ILookupCacheService.GetAll<T> → IReadOnlyList<T>
```

---

## 常見問題

**Q: 貼了 `[CacheLookup]` 但快取沒有失效？**
A: 確認寫入是透過 WTM 的 `DC`（即 `FrameworkContext` 子類別）進行的。若使用原生 EF Core `DbContext` 或 `EmptyContext` 直接寫入，則不會觸發自動失效。

**Q: Warm-up 失敗日誌顯示「DbContext not available」？**
A: `IDataContext` 必須設定正確。確認 `AddWtmContext()` 已在 `services.AddDbContext()` 之後呼叫。

**Q: 不同租戶的 Model 要標記什麼？**
A: 保持 `TenantIsolation = true`（或不設定，使用全域預設 `true`）。若該表的資料在所有租戶間完全相同（如幣別代碼），可設 `TenantIsolation = false` 減少重複快取。

**Q: 可以關掉 Warm-up 嗎？**
A: 在 Attribute 上設 `WarmOnStartup = false` 即可。

**Q: 單租戶應用每個 Model 都要設 `TenantIsolation = false`？**
A: 不用。註冊 `LookupCacheOptions { DefaultTenantIsolation = false }` 即可全域關閉，個別 Model 仍可覆蓋。

**Q: Model 在不同的資料庫（如 ORSS）怎麼辦？**
A: 在 Attribute 上設 `ConnectionKey = "orss"`（對應 appsettings.json 的 Connections Key），框架會自動使用該連線。

**Q: `GetLookup<T>(predicate)` 是 DB 查詢嗎？**
A: 不是。`predicate` 是 `Func<T, bool>`，在記憶體中對已快取的全表資料執行過濾。
