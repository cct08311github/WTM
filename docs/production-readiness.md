# Production Readiness

> **版本適用**：10.5.1（2026-05-13）以後
> **最後更新**：2026-05-14

這份文件回答一個問題：**WTM 現在可以上 production 嗎？**

答案不是單純 yes/no — 取決於你的使用場景與風險承受度。本文件提供一個誠實的自評框架，協助你做出決定。

---

## 場景適用矩陣

| 場景類型 | 評估 | 主要考量 |
|----------|------|----------|
| 企業內網 / SMB 後台 / 中小型多租戶 admin app | ✅ **可以**，WTM 的甜蜜點 | 暴露面小、流量可控、認證集中 |
| 公開 SaaS、中等流量、含付費客戶 | ⚠️ **可以但需補強** | 採用前完成下方「production 補強清單」1-4 項 |
| 高合規領域（醫療 / 金融 / 政府） | ❌ **不建議** | 單人維護、無第三方合規審計、無正式 SOC2/HIPAA 文件 |
| 高流量 / 高可用 / 多區部署 | ❌ **不建議** | 無公開壓測 baseline、distributed cache/session 未官方文件化 |

WTM 設計為「快速 CRUD 開發框架」，**不**是高流量 SaaS 平台或合規敏感場景的首選。

---

## 客觀現況（截至 2026-05-14）

### 已驗證

- `dotnet list package --vulnerable --include-transitive`：**22 個專案 0 個 NU1903 漏洞**
- 單元測試：**1647 / 1647 pass**（Core.Test 1640、Mvc.Tests 38、Admin.Test 75、Api.Test 20、Etl.Test 174 等等）
- E2E 測試：**30 / 30 pass**（Playwright + Python 驅動，含 XSS / SQL Injection / CSRF / Session Fixation / RBAC 場景）
- CI（internal CI）：5 / 5 jobs green（build-and-test、js-test、e2e、release-tooling-test、security-scan）
- 安全 audit 歷史：v10.2.0 完整 audit、封掉 password、JWT、Analysis 注入、`UpdateModelProperty` 反射攻擊、檔案路徑跳脫等已知高優先漏洞

### 架構評估

| 面向 | 狀態 |
|------|------|
| 認證 | PBKDF2 密碼 + legacy MD5 自動 migration、JWT（access + refresh + `jti` replay guard）、`_remotetoken` 完整簽章驗證 |
| 授權 | RBAC、`IDataPrivilege` 列級資料權限、ITenant 多租戶 global query filter |
| ORM | EF Core 10、支援 MSSQL / MySQL / PostgreSQL / SQLite / Oracle |
| 中介軟體 | 10.4.0+ 補齊：CSP（三態 + frame-ancestors + 違規回報）、correlation ID、health checks、rate limiting、slow query、idempotency、ETag、maintenance mode |
| Observability | 結構化 log（Serilog）、ActionLog/Audit/Exception logging via `IWtmLogService`、CorrelationId 中介層、SlowRequest 中介層 |
| ETL | Pipeline executor、watermark、quality rules、dry-run、3 步驟匯入精靈、可視化 dashboard |
| BI / Analytics | Analysis Mode：Dimension/Measure attribute、Sort+TopN、DistinctCount、HavingFilters、CompareWith P-o-P、Drill-Through、Insights 自動敘事 |

### 性能與規模

- **沒有**公開壓測 baseline、p99 SLO、scale-out 文件
- **沒有**官方 K8s 部署案例
- distributed cache / session（Redis 等）需自行接

如果你的應用會超過單 instance 或需要 SLA，這些都是你要自己驗證的事。

---

## 三個要誠實面對的弱點

### 1. 單人維護（bus factor = 1）

WTM 是個人 fork（chiu0831，2026-03 接手）。整個 fork 的演進、修補、release、安全 audit 都靠一個人。對比 ABP Framework 或 Microsoft 自家框架，社群檢視眼數差兩個數量級。

**意義**：採用 WTM = 採用維護者的判斷力與時間承諾。雖然透過 issue → PR → CI gate 流程降低錯判機率（v10.5.1 期間實證有效），仍存在單點風險。

**建議**：若業務關鍵程度高，考慮自己再 fork 一份、本 fork 當 upstream，並準備好「framework 退役」的逃生路徑（見下方）。

### 2. 測試覆蓋率僅 ~20% 行 / ~16% 分支

當前 CI gate threshold 為 line 15% / branch 10%（commit 內註解標明「ratchet — 逐步提升、目標 line 60% / branch 40%」）。

對比業界 production-grade framework 一般 60%+ 行覆蓋率，這還有相當距離。

**意義**：refactor 與 dep upgrade 的回歸風險比覆蓋好的框架高。1647 tests pass 不能視為「保證無回歸」。你**自己這層**的測試必須補強。

**建議**：對你的業務邏輯（VM、Controller、整合）寫 80%+ 覆蓋，不要假設 framework 的 20% 已經夠。

### 3. NPOI 漏洞透過 transitive override pin 緩解，非根本解決

NPOI 2.7.6（也包含最新 2.8.0）transitive 拉 vulnerable `System.Security.Cryptography.Xml 8.0.2`（兩個 high CVE：GHSA-37gx-xxp4-5rgx、GHSA-w3x6-4m5h-cxqf）。

當前緩解：`src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj` 顯式 reference `10.0.6` 覆蓋 NPOI transitive。**這條 reference 絕對不能因為 NU1510 informational warning 而誤刪**（已有 XML 註解警示）。

完整背景見 [`docs/dependency-management.md`](./dependency-management.md#nu1510-雙意義警告-net-10-起)。

**意義**：上游沒修，本 fork 在打補丁。可接受，但你需要在 deployment 與 dep update 流程中明確紀錄這個 pin 不能動。

---

## 其他現實考量

| 項目 | 狀態 |
|------|------|
| 文件（開發手冊 + CHANGELOG） | 持續維護中，但隨版本演進偶有 drift |
| 第三方安全審計 | 無 |
| 性能 baseline | 缺 |
| K8s 官方部署案例 | 缺 |
| Distributed cache / session 文件 | 缺 |
| Build 警告 | ~260（多數為 nullable CS8632、CS0108 等代碼風格警告，非錯誤） |
| Open source 社群 | 小，主要溝通在 internal infrastructure issues |
| 主流商業支援 | 無 SLA、無付費 support 管道 |

---

## Production 補強清單

若你決定上 production，按優先順序執行：

1. **加強你自己這層的測試覆蓋**：對你的業務邏輯（VM / Controller / 整合）寫 80%+ 覆蓋，不要依賴 framework 的 20%
2. **CI 加 vulnerability gate**：你的 CI pipeline 加 `dotnet list package --vulnerable --include-transitive`，**任何**新 NU1903 出現就紅、必須處理。同時保留 WTM Core.csproj 的 Crypto.Xml pin（不要因 NU1510 而誤刪 — 見 [`docs/dependency-management.md`](./dependency-management.md)）
3. **建立 framework 升級 playbook**：WTM 是小社群框架，每次升級你都要承擔回歸驗證責任。建議流程：升級 → 在你 fork 跑整套 e2e → staging 跑兩週 → production
4. **接管 observability**：把 WTM 內建的 `IWtmLogService`、CorrelationId、SlowRequest 中介層的 log 接到你的 SIEM（splunk / DataDog / ELK）。特別注意 v10.5.1 加的 WTMContext RBAC fallback warning — 這些 log 是「token 解析非預期 fail」「tenant DB 短暫故障 RBAC 退化」等隱性問題的線索
5. **fork 你自己的版本**：如果業務關鍵到不能讓單一上游維護者決定版本進度，建議自己 fork 一份、加自己 CI、本 fork 當 upstream
6. **準備好退役路徑**：WTM 走的是 plain ASP.NET Core MVC + EF Core + 一些 helpers，遷移到原生 ASP.NET 不算難，但你應該把這個成本算進採用決策

---

## 個人判斷（維護者立場）

「我自己會不會把 WTM 上 production？」

- 內部專案、SMB、後台系統 → **會**
- 外部 SaaS、有付費客戶 → **會，但會走補強清單 1-4 全做**
- 服務 > 10K DAU 或合規敏感 → **不會，會走 ABP / 原生 ASP.NET**

「WTM 比起原版本」：
- 本 fork 比上游原版本（已沒人維護）狀態好很多 — 補完安全漏洞、補完 observability、補完 CI、補完 .NET 10 適配
- 但「比原版好」不等於「達到 production bar」— 兩個不同的問題

**WTM 是個誠實的中型框架**。它沒有假裝自己是 ABP，沒有 oversell。但「測試 pass + 漏洞掃 0」不是 production-ready 的全部證據。Production-ready 是個和你的 risk profile 對齊的決定，不是框架本身的屬性。

---

## 相關文件

- [`docs/dependency-management.md`](./dependency-management.md) — 套件版本政策、NU1510 雙意義警告、NPOI security pin 詳解
- [`docs/ci-operations.md`](./ci-operations.md) — internal CI 已知不相容與排錯
- [`docs/wtm-developer-manual.md`](./wtm-developer-manual.md) — 完整 18 章節開發手冊（§ 10 安全機制）
- [`docs/structured-logging.md`](./structured-logging.md) — 結構化 log 整合方式
- [`CHANGELOG.md`](../CHANGELOG.md) — 版本演進與每版 breaking changes
