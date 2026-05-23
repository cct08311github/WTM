# Job Description: WTM Test Coverage Improvement (v8.1.14+)

> **目的**：此文件供架構師（Opus）進行 Design 細化，後續由實作工程師執行。
>
> **背景**：WTM 8.1.14 已完成 ASYNC-1（全面去除 sync-over-async）、DEPS-1（依賴升級）、QUALITY-1/2（editorconfig + Nullable）。
> 現有測試已能通過 CI，但覆蓋範圍以「快樂路徑」為主，無法驗證安全關鍵路徑（Auth/Token）、async 路徑、錯誤路徑，且 CI 無 coverage gate。

---

## 1. 現狀盤點（Context）

### 1.1 現有測試專案

| 專案 | 框架 | 定位 | 問題 |
|------|------|------|------|
| `test/WalkingTec.Mvvm.Test.Mock` | MSTest/Moq | 共用 Mock 基礎設施 | ✅ 良好，是整個測試體系的基石 |
| `test/WalkingTec.Mvvm.Core.Test` | MSTest | BaseCRUDVM / BaseBatchVM / BasePagedListVM | ✅ 主要路徑已覆蓋，但缺 async 變體與錯誤路徑 |
| `test/WalkingTec.Mvvm.Admin.Test` | MSTest | Admin API Controller（FrameworkUser）| ⚠️ 僅含 FrameworkUser，缺 DataPrivilege / FrameworkGroup / FrameworkRole |
| `src/WalkingTec.Mvvm.Core.Tests` | **xUnit** | PasswordHash / RefreshToken 實體 | ⚠️ **定位與 Core.Test 重疊**，框架不一致，需要架構師決定合併或分工 |

### 1.2 現有 Mock 基礎設施（`MockWtmContext.CreateWtmContext`）

已解決的核心依賴：
- `IDistributedCache` → `MemoryDistributedCache`
- `IHttpContextAccessor` → Moq + `HttpContextAccessor`
- `IStringLocalizerFactory` → `ResourceManagerStringLocalizerFactory`
- `WtmFileProvider` → 實際注入
- `IDataContext` → EF Core InMemory（`DBTypeEnum.Memory`）
- `LoginUserInfo` → 直接設定 ITCode

### 1.3 未被測試的關鍵路徑

#### AUTH-GAP-1：ITokenService（最高優先）
- `DoLoginAsync` — 整個 Login 流程未測試（PR #7 的核心功能）
- `IssueTokenAsync` — JWT 生成路徑
- `RefreshTokenAsync` — Refresh Token rotation（含 race condition 場景）
- `RevokeTokenAsync` — 撤銷路徑

#### AUTH-GAP-2：密碼 Migration Flow
- MD5 legacy hash 自動升級至 PBKDF2 的端對端流程（在 Login 路徑內）
- 驗證 `PasswordVerifyResult.SuccessRehashNeeded` 觸發後，DB 確實被更新

#### VM-GAP-1：Async ViewModel 方法
- `BaseCRUDVM.DoAddAsync` / `DoEditAsync` / `DoDeleteAsync`（8.1.13 新增）
- 現有 `Core.Test` 只測試同步版本，async 版本完全未測

#### VM-GAP-2：ImportVM 管線
- `BaseImportVM.BatchSaveData` 成功路徑
- Import 錯誤累積與 `ErrorListVM` 回報機制

#### ADMIN-GAP-1：Admin API 覆蓋空缺
- `DataPrivilegeController`（API）— 完全未測
- `FrameworkGroupController`（API）— 完全未測
- `FrameworkRoleController`（API）— 完全未測（假設存在）

#### CI-GAP-1：無 coverage enforcement
- CI 目前只執行 test，不計算 coverage，沒有最低門檻
- 沒有 coverage report artifact

---

## 2. 設計要求（Design Requirements）

### 2.1 測試框架統一策略（待架構師決定）

**選項 A：MSTest 統一**
- 刪除 `src/WalkingTec.Mvvm.Core.Tests`（xUnit）
- 將 PasswordHash/RefreshToken 測試移入 `test/WalkingTec.Mvvm.Core.Test`
- 優點：框架一致；缺點：丟棄已寫的 FluentAssertions 語法

**選項 B：xUnit 統一（長期遷移）**
- `src/WalkingTec.Mvvm.Core.Tests` 保留並擴充為主要 Core 測試專案
- `test/WalkingTec.Mvvm.Core.Test` 逐步棄用（加 `[Obsolete]` 標記或移除）
- 優點：xUnit 更適合 async test、FluentAssertions 更可讀；缺點：需遷移工作量

**選項 C：共存（分工明確）**
- `src/WalkingTec.Mvvm.Core.Tests`（xUnit）→ 純粹 Unit Test（不依賴 EF InMemory）
- `test/WalkingTec.Mvvm.Core.Test`（MSTest）→ Integration Test（使用 MockWtmContext + EF InMemory）
- 優點：分工清晰；缺點：兩個框架並存，onboarding 複雜度高

**架構師應選定一個選項並說明理由。**

### 2.2 Auth/Token 測試 Fixture 設計

`ITokenService` 的實作 (`TokenService`) 依賴：
- `IDataContext` — 讀寫 RefreshTokenEntity
- `WTMContext` — 取得 `ConfigInfo`、`LoginUserInfo`
- `IConfiguration` — 讀取 JWT 設定（secret、issuer、audience、expiry）
- `ILogger<T>` — optional

需要架構師設計一個 `TokenServiceFixture`，解決：
1. **JWT 設定注入** — 如何在測試中提供 `IConfiguration`（直接 mock 還是 `ConfigurationBuilder` + in-memory provider）
2. **時間控制** — RefreshToken 過期測試需要控制「現在」的時間（IClock / DateTimeOffset.UtcNow）
3. **Race condition 測試** — Refresh Token rotation 的重用攻擊場景：同一 token 被使用兩次，第二次應返回 401

### 2.3 CI Coverage Gate

需要架構師決定：
1. **Coverage 工具**：`coverlet.collector`（MSTest）還是 `coverlet.msbuild`？
2. **最低門檻**：建議 Line Coverage ≥ 60%，Branch Coverage ≥ 40%（考慮到 168 個 `#nullable disable` 檔案降低了可測性）
3. **Report 格式**：Cobertura（GitHub Actions 可直接顯示）還是 HTML？
4. **Gate 位置**：`dotnet test` 加 `--threshold` 還是 Coverlet MSBuild target？
5. **排除範圍**：生成器模板（`GeneratorFiles/*.txt`）、Views（`.cshtml`）、Migration files

### 2.4 測試命名規格

建議格式（但請架構師確認）：
```
MethodUnderTest_Scenario_ExpectedBehavior
// 例：
DoAddAsync_ValidEntity_PersistsToDbWithAuditFields
RefreshTokenAsync_UsedToken_ThrowsSecurityException
```

---

## 3. 優先覆蓋目標（Priority List）

| Priority | Gap ID | 描述 | 預估 test 數 |
|----------|--------|------|------------|
| P0 | AUTH-GAP-1 | ITokenService 完整流程 | ~15 |
| P0 | AUTH-GAP-2 | MD5 → PBKDF2 migration 端對端 | ~5 |
| P1 | VM-GAP-1 | BaseCRUDVM DoAddAsync/DoEditAsync/DoDeleteAsync | ~10 |
| P1 | CI-GAP-1 | Coverage gate + report artifact | n/a |
| P2 | ADMIN-GAP-1 | DataPrivilege/FrameworkGroup API tests | ~12 |
| P3 | VM-GAP-2 | BaseImportVM BatchSaveData | ~6 |

---

## 4. 技術限制與邊界

- **無本機 dotnet SDK**：所有驗證透過 CI（GitHub Actions）。程式碼必須可在首次 push 時通過。
- **EF InMemory 限制**：不支援 raw SQL、transactions（`BeginTransaction`）、stored procedures。需要繞過或 mock。
- **Elsa Workflow 依賴**：`BaseCRUDVM` 的 `DoAdd/DoEdit/DoDelete` 內部會嘗試啟動 workflow。在測試環境下 Elsa 服務未注入，需確認是否會 throw 或 silently skip。
- **Multi-tenant**：`MockWtmContext` 目前 `LoginUserInfo.CurrentTenant == null`。多租戶路徑需要額外 fixture。
- **RefreshToken race condition**：EF InMemory 不支援真正的並發鎖，需用 `Task.WhenAll` 模擬並驗證「至少一個成功，另一個收到拒絕」的語意。

---

## 5. 驗收條件（Acceptance Criteria）

1. **CI Green**：所有測試通過，無 skip。
2. **Coverage Gate**：`dotnet test` 加 coverage threshold，低於門檻時 CI fail。
3. **Auth Path Tests**：`DoLoginAsync` → `IssueToken` → `RefreshToken` → `RevokeToken` 完整鏈有測試。
4. **Async Tests**：`DoAddAsync`/`DoEditAsync`/`DoDeleteAsync` 各有至少一個 happy path + 一個 error path 測試。
5. **Migration Flow**：MD5 hash 輸入 → VerifyPassword 回傳 `SuccessRehashNeeded` → DB 中 hash 被更新。
6. **Coverage Report**：每次 CI run 產生 Cobertura XML，作為 artifact 上傳。
7. **Framework Consolidation**：xUnit 和 MSTest 並存問題有明確解決方案並實作。

---

## 6. 實作工程師的工作邊界

實作工程師（Sonnet）負責：
- 根據架構師（Opus）的設計細節，撰寫具體的 `.cs` test 檔案
- 更新 `.csproj`、`.sln`、`ci-build.yml`
- **不負責** 架構決策（框架統一、coverage 門檻、Fixture 設計模式）

請架構師（Opus）在設計文件中提供：
1. 選定的框架統一策略（選項 A/B/C）
2. `TokenServiceFixture` 的類別設計（欄位、建構子、setup 方式）
3. 時間控制機制（是否引入 `TimeProvider` 抽象或直接 Mock）
4. Coverage 工具與門檻的精確設定
5. 測試命名格式確認

---

*文件版本：1.0 | 2026-03-04 | 由 Sonnet (8.1.14 實作工程師) 撰寫供 Opus 設計細化*
