# ETL Module — 批量資料導入

WTM ETL 模組提供從外部 Oracle / MSSQL 資料庫定期批量匯入資料的完整管線，包含排程、監控、管理介面。

---

## Quick Start（5 分鐘）

### 1. 加入專案參考

```xml
<ProjectReference Include="..\WalkingTec.Mvvm.Etl\WalkingTec.Mvvm.Etl.csproj" />
```

或未來 NuGet 發布後：

```bash
dotnet add package WalkingTec.Mvvm.Etl
```

### 2. 註冊 ETL 服務

```csharp
// Program.cs 或 Startup.cs
services.AddWtmEtl();
```

此方法註冊三個元件：

| 元件 | 生命週期 | 用途 |
|------|---------|------|
| `EtlSchedulerService` | Singleton | 排程管理 API（啟用/暫停/中止等） |
| `EtlProgressTracker` | Singleton | 執行進度追蹤（記憶體內） |
| `EtlHostedService` | HostedService | 應用啟動時初始化 Quartz Scheduler |

### 3. 註冊 EF Models

```csharp
// DataContext.OnModelCreating
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.ApplyEtlModels();
}
```

此方法建立兩張表：

- `EtlJobDefinitions` — Job 定義（含唯一索引 `Name`、索引 `Status`）
- `EtlRunLogs` — 執行記錄（含索引 `JobId`、`StartedAt`，FK → `EtlJobDefinitions`）

### 4. 設定連線字串

```json
// appsettings.json
{
  "Connections": [
    { "Key": "default", "Value": "Server=...;Database=WtmApp;..." },
    { "Key": "UpstreamOracle", "Value": "Data Source=...;User Id=...;Password=...;", "DbType": "Oracle" },
    { "Key": "UpstreamMssql", "Value": "Server=...;Database=...;User Id=...;Password=...;", "DbType": "SqlServer" }
  ]
}
```

> **安全提醒**：連線字串僅存於 `appsettings.json` 或環境變數，**絕不存入資料庫**。

### 5. 執行 Migration

```bash
dotnet ef migrations add AddEtlTables
dotnet ef database update
```

### 6. 透過管理介面建立 Job

啟動應用後：

1. 進入 `/_EtlJob/Index` — 管理所有 ETL Job
2. 點「新增」— 填寫名稱、Cron 表達式、來源連線、Job 類別名稱
3. 設定 Status 為 `Enabled` — Job 即開始按 Cron 排程執行
4. 在 `/_EtlRunLog/Index` 查看執行歷史
5. 在 `/_EtlMonitor/Running` 即時監控進度

---

## 架構概覽

```
                  ┌──────────────────┐
Quartz Trigger ──▶│  EtlQuartzJob    │
                  │  (reads config)  │
                  └────────┬─────────┘
                           ▼
                  ┌──────────────────┐
                  │ EtlPipelineExecutor │
                  └────────┬─────────┘
                           │
          ┌────────────────┼────────────────┐
          ▼                ▼                ▼
   ┌──────────┐    ┌──────────┐    ┌──────────┐
   │ Extract  │    │Transform │    │  Load    │
   │(IEtlSource)│  │(optional)│    │(IBulkLoader)│
   └──────────┘    └──────────┘    └────┬─────┘
                                        ▼
                              ┌──────────────────┐
                              │  Staging Table   │
                              │  (TRUNCATE first)│
                              └────────┬─────────┘
                                       ▼
                              ┌──────────────────┐
                              │  MERGE INTO      │
                              │  Target Table    │
                              └──────────────────┘
```

**流程摘要**：Extract（串流讀取）→ Transform（選用）→ Load（批量寫入 Staging）→ Merge（Upsert 至目標）

---

## API 參考

### EtlPipelineConfig

Pipeline 單次執行的完整配置。

```csharp
public record EtlPipelineConfig
{
    public Guid JobId { get; init; }                   // Job ID（用於記錄/進度）
    public string JobName { get; init; }               // 顯示名稱
    public string SourceConnectionString { get; init; } // 來源資料庫連線
    public string TargetConnectionString { get; init; } // 目標資料庫連線
    public string QueryTemplate { get; init; }         // SQL 查詢（含 @watermark 參數）
    public string TargetTableName { get; init; }       // 目標表名
    public string MergeKeyColumn { get; init; }        // MERGE ON 的 Business Key
    public int BatchSize { get; init; } = 50_000;      // 每批筆數
    public StagingTableSpec StagingTable { get; init; } // Staging 表結構
    public Func<DataTable, DataTable>? TransformFunc { get; init; } // 轉換函式
}
```

### StagingTableSpec

定義 Staging 表的 DDL（Column 名稱 + 原生 SQL 類型）。

```csharp
public class StagingTableSpec
{
    public StagingTableSpec(string tableName, params StagingColumn[] columns);

    public string TableName { get; }
    public IReadOnlyList<StagingColumn> Columns { get; }
}

public record StagingColumn(string Name, string SqlType);
```

**範例**：

```csharp
new StagingTableSpec("_etl_staging_orders",
    new StagingColumn("OrderId", "BIGINT"),          // MSSQL
    new StagingColumn("CustomerName", "NVARCHAR(200)"),
    new StagingColumn("Amount", "DECIMAL(18,2)"),
    new StagingColumn("OrderDate", "DATETIME2")
);
```

### EtlPipelineExecutor

Pipeline 引擎，協調 Extract → Transform → Load → Merge 流程。

```csharp
public class EtlPipelineExecutor
{
    public EtlPipelineExecutor(IEtlSource source, IBulkLoader loader, IProgress<EtlProgress>? progress = null);
    public async Task<EtlExecutionResult> ExecuteAsync(EtlPipelineConfig config, WatermarkStrategy watermark, CancellationToken ct);
}
```

**行為**：

1. `EnsureStagingTable` — 不存在則自動建立
2. `TruncateStaging` — 清空 Staging
3. 逐批 Extract → Transform → BulkLoad，每批後更新 Watermark 暫存值與回報進度
4. `Merge` — 從 Staging Upsert 至目標表
5. 成功 → `CommitPendingValue()`；失敗/中止 → `DiscardPendingValue()`

### EtlExecutionResult

Pipeline 執行結果。

```csharp
public class EtlExecutionResult
{
    public bool Success { get; init; }
    public bool Aborted { get; init; }
    public int ExtractedRows { get; init; }
    public int LoadedRows { get; init; }
    public int ErrorRows { get; init; }
    public long ElapsedMs { get; init; }
    public string? ErrorMessage { get; init; }
    public string? NewWatermarkValue { get; init; }
}
```

### IEtlSource / IBulkLoader

```csharp
public interface IEtlSource : IDisposable
{
    IAsyncEnumerable<DataTable> ExtractBatchesAsync(
        string connectionString, string queryTemplate,
        object? watermarkValue, int batchSize,
        CancellationToken ct);
}

public interface IBulkLoader
{
    Task BulkLoadAsync(string cs, string stagingTable, DataTable batch, CancellationToken ct);
    Task MergeAsync(string cs, string staging, string target, string mergeKey, CancellationToken ct);
    Task TruncateStagingAsync(string cs, string stagingTable, CancellationToken ct);
    Task EnsureStagingTableAsync(string cs, string stagingTable, StagingTableSpec spec, CancellationToken ct);
}
```

**實作**：

| 介面 | MSSQL | Oracle |
|------|-------|--------|
| `IEtlSource` | `MssqlSource`（SqlDataReader） | `OracleSource`（OracleDataReader） |
| `IBulkLoader` | `MssqlBulkLoader`（SqlBulkCopy + MERGE） | `OracleBulkLoader`（Array Binding + MERGE INTO） |

### EtlSourceFactory

根據 `DBTypeEnum` 建立 Source / Loader 實例。

```csharp
public static class EtlSourceFactory
{
    public static IEtlSource CreateSource(DBTypeEnum dbType);    // SqlServer, Oracle
    public static IBulkLoader CreateLoader(DBTypeEnum targetDbType);
}
```

不支援的 `DBTypeEnum` 會拋出 `NotSupportedException`。

---

## Watermark 策略

### 三種模式

| 模式 | 說明 | 適用場景 |
|------|------|---------|
| `FullLoad` | 每次全量載入，不使用 watermark | 來源小（< 10 萬）或無法識別變更 |
| `Timestamp` | 用時間欄位做增量（`WHERE UpdatedAt > @watermark`） | 有可靠 UpdatedAt 的業務表 |
| `Identity` | 用自增 ID 做增量（`WHERE Id > @watermark`） | Append-only 日誌型資料 |

### WatermarkStrategy API

```csharp
public class WatermarkStrategy
{
    public WatermarkStrategy(EtlWatermarkType type, string? column, string? currentValue, string timeZone = "UTC");

    public string BuildWhereClause();           // "(1=1)" 或 "{column} > @watermark"
    public object? GetParameterValue();          // @watermark 的參數值（已轉時區）
    public void UpdateFromBatchMax(object max);  // 暫存批次最大值
    public string? CommitPendingValue();          // 確認新 watermark（成功後呼叫）
    public void DiscardPendingValue();           // 丟棄暫存值（失敗後呼叫）
}
```

### 首次執行行為

| `InitialWatermarkValue` | 行為 |
|------------------------|------|
| `null` | 全量載入（`WHERE 1=1`） |
| `"2026-01-01T00:00:00Z"` | 從該時間點開始 |
| `"0"` | 從 ID = 0 開始 |

### 時區處理

- **儲存**：統一 UTC
- **查詢**：轉回 `WatermarkTimeZone` 指定的時區（IANA 格式，如 `"Asia/Taipei"`）
- **常見坑**：Asia/Taipei (+8) 的日界線問題 — 確保來源 DB 的時間欄位語義明確

---

## Staging Table 策略

### MSSQL

- 普通表 + `TRUNCATE TABLE`
- 首次自動 `CREATE TABLE IF NOT EXISTS`
- Column 定義用 MSSQL 原生類型
- Merge 語法：`MERGE [target] AS t USING [staging] AS s ON t.[key] = s.[key] ...`

### Oracle

- 普通表 + `TRUNCATE TABLE`（不使用 Global Temp Table）
- 首次自動 CREATE（需 `CREATE TABLE` 權限）
- 檢查表存在用 `USER_TABLES`（表名自動 `ToUpperInvariant()`）
- Merge 語法：`MERGE INTO target t USING staging s ON (t.key = s.key) ...`
- **表名建議全大寫**（Oracle 慣例）

### MSSQL / Oracle 類型對照

| 用途 | MSSQL | Oracle |
|------|-------|--------|
| 字串 | `NVARCHAR(N)` | `VARCHAR2(N)` |
| 整數 | `BIGINT` | `NUMBER(19)` |
| 小數 | `DECIMAL(18,2)` | `NUMBER(18,2)` |
| 時間 | `DATETIME2` | `TIMESTAMP` |
| 布林 | `BIT` | `NUMBER(1)` |

---

## 排程系統

### Quartz.NET 整合

ETL 模組使用 Quartz.NET 3.x 做排程。Cron 格式為 **6-7 欄位**（含秒），不同於 Unix 5 欄位格式。

```
秒 分 時 日 月 週 [年]
```

| 範例 | 意義 |
|------|------|
| `0 0 2 * * ?` | 每天凌晨 2:00 |
| `0 0/30 * * * ?` | 每 30 分鐘 |
| `0 0 2 ? * MON-FRI` | 每週一到五凌晨 2:00 |

### EtlSchedulerService API

```csharp
public class EtlSchedulerService
{
    // 啟動時載入所有 Enabled/Failed Job
    public virtual async Task LoadJobsFromDbAsync();

    // 立即執行（Trigger = Manual）
    public virtual async Task TriggerNowAsync(Guid jobId);

    // 暫停/恢復排程觸發
    public virtual async Task PauseAsync(Guid jobId);
    public virtual async Task ResumeAsync(Guid jobId);

    // 更新 Cron 並重新排程
    public virtual async Task RescheduleAsync(Guid jobId, string newCron);

    // 中止執行中的 Job（透過 CancellationToken）
    public virtual async Task AbortAsync(Guid jobId);

    // 跳過下次排程
    public virtual async Task SkipNextAsync(Guid jobId);

    // 啟用/停用 Job
    public virtual async Task EnableAsync(Guid jobId);
    public virtual async Task DisableAsync(Guid jobId);

    // 從歷史記錄重跑（還原 watermark）
    public virtual async Task RerunFromSnapshotAsync(Guid runLogId);

    // 判斷是否可執行
    public static bool ShouldExecute(EtlJobDefinition job);
}
```

### EtlQuartzJob

Quartz 觸發後的橋接器，標記 `[DisallowConcurrentExecution]` 防止同一 Job 並行執行。

**執行流程**：

1. 從 `JobDataMap` 取得 `EtlJobDefinitionId`
2. 檢查 `SkipCount`（> 0 則跳過並記錄）
3. 檢查 `Status`（僅 Enabled/Failed 可執行）
4. 設定 `Status = Running`
5. 從 `Configs.Connections` 取得來源連線字串
6. 建立 Source、Loader、WatermarkStrategy、PipelineConfig
7. 執行 `EtlPipelineExecutor.ExecuteAsync()`
8. 成功：commit watermark → Status = Enabled
9. 失敗：discard watermark → Status = Failed
10. 寫入 `EtlRunLog`，清除進度追蹤

### EtlProgressTracker

Thread-safe 的記憶體內進度字典（`ConcurrentDictionary`）。

```csharp
public class EtlProgressTracker
{
    public void Update(EtlProgress progress);       // 更新/新增進度
    public EtlProgress? Get(Guid jobId);             // 取得單一 Job 進度
    public IReadOnlyList<EtlProgress> GetAll();     // 取得所有執行中的 Job
    public void Remove(Guid jobId);                  // 清除（Job 完成後）
}
```

### EtlProgress

```csharp
public record EtlProgress
{
    public Guid JobId { get; init; }
    public string JobName { get; init; }
    public int ProcessedRows { get; init; }
    public int? TotalRows { get; init; }     // null = 未知
    public string Phase { get; init; }       // "Loading", "Merging", "Done"
    public double? RowsPerSecond { get; init; }
    public DateTime StartedAt { get; init; }
}
```

---

## 資料模型

### EtlJobDefinition

持久化的 Job 配置，繼承 `BasePoco`（Guid ID）。

| 屬性 | 類型 | 說明 |
|------|------|------|
| `Name` | string (required, max 100) | 唯一名稱 |
| `Description` | string? (max 500) | 描述 |
| `CronExpression` | string (required) | Quartz 6/7 欄位格式 |
| `JobClassName` | string (required, max 500) | Job 實作類別全名 |
| `Status` | EtlJobStatus | 狀態（Enabled/Disabled/Running/Paused/Failed） |
| `SourceCsKey` | string (required, max 100) | 對應 appsettings 的連線 Key |
| `SourceDbType` | DBTypeEnum | 來源資料庫類型 |
| `WatermarkType` | EtlWatermarkType | 增量策略 |
| `WatermarkColumn` | string? | Watermark 欄位名 |
| `LastWatermarkValue` | string? | 上次成功的 watermark（JSON） |
| `InitialWatermarkValue` | string? | 首次執行起始值 |
| `WatermarkTimeZone` | string | IANA 時區（預設 "UTC"） |
| `SkipCount` | int | 跳過次數（每跳過一次 -1） |
| `RetryCount` | int | 最大重試次數（預設 3） |
| `TimeoutMinutes` | int | 執行超時分鐘（預設 60） |
| `LastRunAt` | DateTime? | 上次完成時間 |
| `NextFireAt` | DateTime? | 下次觸發時間 |
| `LastError` | string? (max 2000) | 上次錯誤訊息 |

### EtlRunLog

每次執行的審計記錄。

| 屬性 | 類型 | 說明 |
|------|------|------|
| `JobId` | Guid | FK → EtlJobDefinition |
| `Trigger` | EtlRunTrigger | Scheduled / Manual / Retry |
| `Result` | EtlRunResult | Success / Failed / Aborted / Skipped |
| `ExtractedRows` | int | 讀取筆數 |
| `LoadedRows` | int | 寫入 Staging 筆數 |
| `ErrorRows` | int | 失敗筆數 |
| `ElapsedMs` | long | 總耗時（ms） |
| `ErrorMessage` | string? (max 4000) | 例外資訊 |
| `StartedAt` | DateTime | UTC 開始時間 |
| `FinishedAt` | DateTime? | UTC 結束時間 |
| `WatermarkSnapshot` | string? | 執行前的 watermark（用於重跑） |

### 列舉

```csharp
public enum EtlJobStatus     { Enabled, Disabled, Running, Paused, Failed }
public enum EtlRunTrigger     { Scheduled, Manual, Retry }
public enum EtlRunResult      { Success, Failed, Aborted, Skipped }
public enum EtlWatermarkType  { FullLoad, Timestamp, Identity }
```

---

## 管理介面

### 控制器端點

#### `_EtlJobController`（Job 管理）

| 端點 | 方法 | 說明 |
|------|------|------|
| `/_EtlJob/Index` | GET | Job 列表頁面 |
| `/_EtlJob/Search` | POST | 搜尋 Job（Name/Status/SourceDbType） |
| `/_EtlJob/Create` | GET/POST | 新增 Job |
| `/_EtlJob/Edit/{id}` | GET/POST | 編輯 Job |
| `/_EtlJob/Delete/{id}` | GET/POST | 刪除 Job（Running 時禁止刪除） |
| `/_EtlJob/TriggerNow?id=` | POST | 立即執行 |
| `/_EtlJob/Pause?id=` | POST | 暫停排程 |
| `/_EtlJob/Resume?id=` | POST | 恢復排程 |
| `/_EtlJob/Abort?id=` | POST | 中止執行中的 Job |
| `/_EtlJob/SkipNext?id=` | POST | 跳過下次排程 |
| `/_EtlJob/Reschedule?id=` | POST | 更新 Cron 表達式（body: newCron） |

#### `_EtlRunLogController`（執行歷史）

| 端點 | 方法 | 說明 |
|------|------|------|
| `/_EtlRunLog/Index?jobId=` | GET | 執行記錄列表（可依 Job 篩選） |
| `/_EtlRunLog/Search` | POST | 搜尋記錄（JobId/Result/Trigger/日期區間） |
| `/_EtlRunLog/Rerun?runLogId=` | POST | 從歷史快照重跑 |

#### `_EtlMonitorController`（即時監控）

| 端點 | 方法 | 說明 |
|------|------|------|
| `/_EtlMonitor/Running` | GET | 列出所有執行中 Job（JSON） |
| `/_EtlMonitor/Progress?jobId=` | GET | 單一 Job 進度（404 = 未執行） |

### RBAC 權限

所有控制器都透過 WTM 的 `[ActionDescription]` + `PrivilegeFilter` 控制存取。需要在 WTM 系統管理中配置對應的功能權限：

- `ETL Job 管理` — 對應 `_EtlJobController`
- `ETL 執行歷史` — 對應 `_EtlRunLogController`
- `ETL 即時監控` — 對應 `_EtlMonitorController`

---

## 安全模型

### SQL 注入防護

- `QueryTemplate` 是開發者撰寫的 trusted input（寫在程式碼/設定中）
- 所有動態值（watermark）一律用**參數化查詢**（`@watermark`）
- **嚴禁** string interpolation 組合 SQL

### 連線字串管理

- 透過 `Configs.Connections`（`appsettings.json` 或環境變數）
- 不存入資料庫 — `EtlJobDefinition.SourceCsKey` 只存 Key 名稱
- 生產環境建議用環境變數或 Azure Key Vault

---

## 測試

### 單元測試

模組提供兩個 mock 類別，位於 `WalkingTec.Mvvm.Etl.Testing`：

#### MockEtlSource

```csharp
var source = new MockEtlSource();
source.SetData(testDataTable);  // 設定測試資料
```

#### MockBulkLoader

```csharp
var loader = new MockBulkLoader();
loader.FailOnBatch = 3;           // 模擬第 3 批失敗

// 執行後檢查
Assert.AreEqual(5, loader.TotalRows);
Assert.IsTrue(loader.MergeCalled);
Assert.IsTrue(loader.TruncateCalled);
```

### 整合測試

整合測試需要真實資料庫。參見 `test/docker-compose.etl-test.yml` 啟動 MSSQL + Oracle 測試環境。

```bash
# 啟動測試資料庫
docker compose -f test/docker-compose.etl-test.yml up -d

# 等待健康檢查通過
docker compose -f test/docker-compose.etl-test.yml ps

# 執行整合測試（需要 Integration category）
dotnet test test/WalkingTec.Mvvm.Etl.Test \
  --filter "TestCategory=Integration" -c Release

# 清理
docker compose -f test/docker-compose.etl-test.yml down -v
```

---

## 故障排除

### 常見錯誤

| 錯誤 | 原因 | 解法 |
|------|------|------|
| `ETL source not supported` | `SourceDbType` 不在支援範圍 | 確認使用 `SqlServer` 或 `Oracle` |
| `Staging table creation failed` | 無 `CREATE TABLE` 權限 | 請 DBA 預建 staging 表 |
| `MERGE failed: duplicate key` | `MergeKeyColumn` 不唯一 | 確認 merge key 是 business key |
| `Watermark column not found` | `DataTable` 無該欄位 | 確認 SQL 查詢結果包含 watermark 欄位 |
| `Connection key not found` | `SourceCsKey` 不在 Configs | 確認 `appsettings.json` 中有該連線 |
| `Job is already running` | 觸發過快 | `[DisallowConcurrentExecution]` 保護中 |
| `Invalid Cron Expression` | 格式錯誤 | 使用 Quartz 6/7 欄位格式（含秒） |

### 效能調優

- **BatchSize 建議**：MSSQL `50,000`，Oracle `10,000 ~ 30,000`
- **MERGE 大量資料**時建議在 staging 表的 merge key 欄位建 index
- **監控 `ElapsedMs` 趨勢** — 異常增加時檢查來源 DB 效能或網路延遲
- **Oracle Array Binding** 使用 `cmd.ArrayBindCount` 做批量插入，比 `OracleBulkCopy` 更穩定
