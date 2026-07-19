# Production Readiness

> **版本適用**：10.14.5 + 2026-07 優化批次（[Unreleased]）以後
> **最後更新**：2026-07-17（重評 — 取代 2026-05-14 的 10.5.1 版評估）

這份文件回答一個問題：**WTM 現在可以上 production 嗎？**

答案不是單純 yes/no — 取決於你的使用場景與風險承受度。本文件提供一個誠實的自評框架，協助你做出決定。

> **本次重評的背景**：2026-05 到 2026-07 之間，本 fork 經歷了 WorkFlow 引擎 tx-safety 戰役（10.9–10.12）、LayUI 2.13.8 islandification 與 CSP 硬化（10.13–10.14）、以及一輪由「最終徹底優化規格書」驅動的 **26-issue 優化批次**（Phase 0–2，見下方 § 2026-07 優化批次）。上一版評估凍結在 10.5.1，已落後約 30 個 release，故整份重寫。核心結論不變：**WTM 是誠實的中型 CRUD 框架，甜蜜點是內網/SMB/中小型多租戶後台；不是高流量 SaaS 或合規敏感場景的首選。** 但「地基」比上一版明顯更穩。

---

## 場景適用矩陣

| 場景類型 | 評估 | 主要考量 |
|----------|------|----------|
| 企業內網 / SMB 後台 / 中小型多租戶 admin app | ✅ **可以**，WTM 的甜蜜點 | 暴露面小、流量可控、認證集中 |
| 公開 SaaS、中等流量、含付費客戶 | ⚠️ **可以但需補強** | 採用前完成下方「production 補強清單」1-4 項；**若使用 JWT refresh flow，先確認 #721 已修**（見 § 安全姿態） |
| 高合規領域（醫療 / 金融 / 政府） | ❌ **不建議** | 單人維護、無第三方合規審計、無正式 SOC2/HIPAA 文件 |
| 高流量 / 高可用 / 多區部署 | ❌ **不建議** | 無公開壓測 baseline、distributed cache/session 未官方文件化 |

WTM 設計為「快速 CRUD 開發框架」，**不**是高流量 SaaS 平台或合規敏感場景的首選。

---

## 客觀現況（截至 2026-07-17，[Unreleased] 分支狀態）

### 已驗證

- `dotnet list package --vulnerable --include-transitive`：**0 個 NU1903 漏洞**（27 個專案，含新納入 CI 的 FileHandlers.S3；release gate 全程守住）
- 單元測試：**~5,780 pass / 0 fail**（Core.Test ~4,313、WorkFlow.Test 564+23 skip、Etl.Test ~683+17 skip、Admin/Api/Mvc.Tests/S3.Test 等）— 較上一版評估的 1,647 增長約 3.5 倍
- JS 測試：**~1,704 pass**（Jest + jsdom，涵蓋 framework_layui.js 的 island dispatch / kill-switch / sentinel-escape 路徑）
- E2E 測試：**35 pass / 1 skip / 0 fail**（Playwright + Python，31→36 檢查；新增 JWT/combobox-cascade/selector/upload 流程 + **#627 kill-switch 專屬 CI matrix leg**）
- CI（internal CI）：build-and-test / js-test / e2e(baseline) / e2e(killswitch) / release-tooling-test / security-scan 全 green
- 安全 audit 歷史：v10.2.0 完整 audit + 其後連續多輪對抗式（cross-vendor + perspective-diverse）審計；本次批次期間對抗式審查在合併前攔下多個真實缺陷（見 § 品質保證機制）

### 架構評估

| 面向 | 狀態 |
|------|------|
| 認證 | PBKDF2 密碼 + legacy MD5 自動 migration、JWT（access + refresh + `jti` replay guard）、`_remotetoken` 完整簽章驗證 ⚠️ **見 #721：HTTP 層 refresh 路由衝突，加固過的 replay-guard 目前經 API 走不到** |
| 授權 | RBAC、`IDataPrivilege` 列級資料權限、ITenant 多租戶 global query filter |
| ORM | EF Core 10，支援 MSSQL / MySQL / PostgreSQL / SQLite / Oracle；**WorkFlow 引擎全 23 個交易站點已可在 `EnableRetryOnFailure`（雲端 SQL Server/PG 常用設定）下運作**（#667 — 先前會拋 `InvalidOperationException`） |
| 中介軟體 | CSP（三態 + frame-ancestors + 違規回報）、correlation ID、health checks、rate limiting、slow query、idempotency、ETag、maintenance mode |
| Observability | 結構化 log（Serilog）、ActionLog/Audit/Exception logging via `IWtmLogService`、CorrelationId + SlowRequest 中介層；**時間源逐步遷移至 `TimeProvider`**（#676，WorkFlow + token 壽命優先，提升可測性） |
| ETL | Pipeline executor、watermark、quality rules、dry-run、匯入精靈、dashboard；**dead-letter 完整性**（捕捉 load/transform/Abort 失敗，run-scoped 去重，保留期 knob，#673）；**REST source SSRF 縱深 + 欄名 allowlist**（#661/#680） |
| BI / Analytics | Analysis Mode：Dimension/Measure、Sort+TopN、DistinctCount、HavingFilters、CompareWith P-o-P、Drill-Through、Insights；widget 資料源改為 async（#662，避免 dashboard fan-out 阻塞 threadpool） |

### 性能與規模

- 本批次做了 **evidence-based** 效能修復（各附 before/after benchmark）：grid header 攤平 ~16×、匯入驗證 ~18×、ETL 欄位映射 1.6–3.4×、多個 per-request 反射熱點消除（與當年 PR #34 同類）。Benchmarks 專案已納入 CI compile gate。
- 但仍**沒有**公開壓測 baseline、p99 SLO、scale-out / K8s 官方部署案例；distributed cache / session（Redis 等）需自行接。
- 如果你的應用會超過單 instance 或需要 SLA，這些都是你要自己驗證的事。

---

## 安全姿態（2026-07 重評）

整體方向是**縱深強化**，但本批次也**誠實揭露了一個真實缺口**——這正是為什麼「測試 pass + 漏洞掃 0」不是 production-ready 的全部證據。

**強化（已合併）**：
- ETL REST source：next-link 跨源憑證轉發封堵 + 有限 MaxPages（#661）；bulk-load 欄名 allowlist（`^[\p{L}\p{N}_#$]+$`，擋 SQL 注入字元、放行 CJK）+ MSSQL bracket 跳脫（#680）
- S3 handler：例外收斂、`../` key traversal 封堵、ContentType 設定（#680）
- GitHub mirror leak-gate 改為無條件執行 + sanitize 全文字檔化（#659）
- Analysis 白名單先於 Expression Tree 的安全邊界持續完好

**新揭露的缺口（未修，已立案）**：
- **#721（P0/P1，安全）**：`AccountController.RefreshToken` 與 `_FrameworkController.RefreshToken` **路由衝突**，ASP.NET Core 把請求路由到較舊的 AccountController，使得加固過的 `TokenService.RefreshTokenAsync`（atomic rotation + reuse-chain 撤銷 + `jti` replay guard，有專屬單元/整合測試）**在 HTTP 層走不到**。實測：帶合法 Bearer + 一個從未發放的 refreshToken 仍回 200 + 新 access_token。**既有單元測試沒抓到，因為它們直接測 TokenService、在路由層之下**——由本批次新增的 live e2e（#681 tc_32）才發現。**若你的部署使用 JWT refresh flow，這是採用前必須確認已修的項目。**
- **#722（layui）**：`ff.OpenDialog2` 剝除 selector 對話框自身的 `<script>`，picker grid 永不載入（功能缺陷，非安全，但影響 selector 使用者）
- **#696（security/sync）**：public mirror 的 test/manual 檔案洩漏 macOS 使用者名稱/內部路徑（僅 mirror，不入 NuGet 包）

**未解的硬化 epic**：
- **#470 / #567**：ff.OpenDialog eval sink 退役與 LayUI CSP 全硬化仍進行中。#627 kill-switch（可 opt-in 停用 legacy 動態腳本執行）+ islandification 已推進，`framework_layui.js` 全檔僅剩 1 個 `eval(`（deprecated IsScript 路徑）。**對一般 CRUD app，kill-switch 已可作為 staging 稽核工具**；生產全面啟用待 widget islandification 完成。**這是走 strict CSP 的採用者最需要追蹤的 roadmap。**

---

## 三個要誠實面對的弱點

### 1. 單人維護（bus factor = 1）

WTM 是個人 fork（chiu0831，2026-03 接手）。整個 fork 的演進、修補、release、安全 audit 都靠一個人（近期以多代理 workflow + 對抗式審查放大單人產能，但決策與責任仍集中一人）。對比 ABP Framework 或 Microsoft 自家框架，社群檢視眼數差兩個數量級。

**意義**：採用 WTM = 採用維護者的判斷力與時間承諾。雖然透過 issue → PR → CI gate + 對抗式審查降低錯判機率（本批次實證有效——多個會進主線的真實缺陷在合併前被攔），仍存在單點風險。

**建議**：若業務關鍵程度高，考慮自己再 fork 一份、本 fork 當 upstream，並準備好「framework 退役」的逃生路徑（見下方）。

### 2. 測試覆蓋率仍偏低（~20% 行 / ~16% 分支），但趨勢正確

測試絕對數已從 1,647 成長到 ~5,780（.NET）+ ~1,704（JS），CI coverage gate 已從卡死的 15%/10% 修正並上調至 **18%/14%**（ratchet 恢復運作，目標 line 60% / branch 40%），且新增了 **#565 LayUI regression Playwright gate** 為 layui 版本升級站崗、**e2e kill-switch matrix leg** 為 CSP 路徑站崗。

但對比業界 production-grade framework 一般 60%+ 行覆蓋率，這仍有相當距離。

**意義**：refactor 與 dep upgrade 的回歸風險比覆蓋好的框架高。~5,780 tests pass 不能視為「保證無回歸」——本批次的 #721 route collision 正是「單元測試全綠卻有整合層缺陷」的活例。你**自己這層**的測試必須補強。

**建議**：對你的業務邏輯（VM、Controller、**整合/HTTP 層**）寫 80%+ 覆蓋，不要假設 framework 的 20% 已經夠。特別是 HTTP 端到端測試——它抓得到單元測試看不到的路由/中介層問題。

### 3. NPOI 漏洞透過 transitive override pin 緩解，非根本解決

NPOI 2.7.6（也包含最新 2.8.0）transitive 拉 vulnerable `System.Security.Cryptography.Xml`（兩個 high CVE：GHSA-37gx-xxp4-5rgx、GHSA-w3x6-4m5h-cxqf）。

當前緩解：`Core.csproj` 顯式 reference 覆蓋（本批次隨 MS 套件群對齊至 10.0.9，pin 隨之提升以維持 >= group）。**這條 reference 絕對不能因為 NU1510 informational warning 而誤刪**（已有 XML 註解警示）。同類 override 也已擴及 Benchmarks 專案（#674：新 ProjectReference 會透過 NPOI 拉回漏洞版，對抗式審查在合併前攔下）。

完整背景見 [`docs/dependency-management.md`](./dependency-management.md)。

**意義**：上游沒修，本 fork 在打補丁。可接受，但你需要在 deployment 與 dep update 流程中明確紀錄這個 pin 不能動。

---

## 2026-07 優化批次（對 readiness 的淨效果）

由「最終徹底優化規格書」驅動的 26-issue 批次（多代理 workflow：實作 → 對抗式驗證 → 修復迴圈執行），對 production-readiness 的實質提升：

| 面向 | 提升 |
|------|------|
| **CI 可靠性** | 反覆咬 CI 的 SQLite TCONC flake 家族**治本**（#709：競爭 fixture 改用 file-based WAL per-actor 連線，獨立 468 次迴圈 0 失敗）；coverage ratchet 修復（#671）；docs-only PR 不再跑全套（#670）；S3/Benchmarks 納入 CI（#658） |
| **交易正確性** | WorkFlow 全交易站點在 `EnableRetryOnFailure` 下合法 + deadlock 重試統一（#667）；strand-reaper 確定性排序（#665）；4 家 DB 的 unique-scope 一致（#664） |
| **安全縱深** | ETL SSRF/欄名/S3 硬化（#661/#680）；mirror leak-gate 補全（#659） |
| **效能** | 多個 evidence-based 熱點修復（各附 benchmark）；graph 快取（#666）；widget async（#662） |
| **可維護性** | 兩個 WorkFlow god-file（5,212+2,143 行）與三個 Core god-file（WTMContext 2,024→856、BasePagedListVM 1,834→998、DCExtension 1,303→527）拆成 partial；死碼清除；backtick 檔名修正 |
| **現代化** | TimeProvider 遷移首批（#676）；GeneratedRegex 熱點（#677）；SourceLink/snupkg/deterministic build/PackageReadme（#678）；Vue2/React demo 退役 + 42MB zip 移除 + 2 個 mirror CVE exception 清零（#679） |

**淨判斷**：地基（CI 穩定度、交易正確性、可維護性、依賴衛生）明顯更穩；同時本批次的 live e2e **誠實揪出**一個先前隱藏的 HTTP 層安全缺口（#721），拉高了「已知風險」的透明度——這是好事，不是退步。

---

## 其他現實考量

| 項目 | 狀態 |
|------|------|
| 文件（開發手冊 + CHANGELOG） | 持續維護中；本批次已修正多處 drift（手冊版本、e2e 標記、交叉引用） |
| 第三方安全審計 | 無（但有連續對抗式 + cross-vendor 自審） |
| 性能 baseline | 缺公開 SLO；有 evidence-based micro-benchmark（Benchmarks 專案） |
| K8s 官方部署案例 | 缺 |
| Distributed cache / session 文件 | 缺 |
| Build 警告 | ~200–460（多為 nullable CS8632/CS8602、XML-doc cref 等風格警告，非錯誤；Mvc nullable 漸進中，#718） |
| Open source 社群 | 小，主要溝通在 internal infrastructure issues |
| 主流商業支援 | 無 SLA、無付費 support 管道 |

---

## Production 補強清單

若你決定上 production，按優先順序執行：

0. **若使用 JWT refresh flow：先確認 #721（refresh 路由衝突）已修** — 否則加固過的 replay-guard 在 HTTP 層未生效。
1. **加強你自己這層的測試覆蓋**：對你的業務邏輯（VM / Controller / **整合 / HTTP 層**）寫 80%+ 覆蓋，不要依賴 framework 的 20%。HTTP 端到端測試尤其重要（抓得到 #721 這類路由層問題）。
2. **CI 加 vulnerability gate**：你的 CI pipeline 加 `dotnet list package --vulnerable --include-transitive`，**任何**新 NU1903 出現就紅、必須處理。同時保留 Core.csproj + Benchmarks 的 Crypto.Xml pin（不要因 NU1510 而誤刪 — 見 [`docs/dependency-management.md`](./dependency-management.md)）。
3. **建立 framework 升級 playbook**：WTM 是小社群框架，每次升級你都要承擔回歸驗證責任。建議流程：升級 → 在你 fork 跑整套 e2e → staging 跑兩週 → production。
4. **接管 observability**：把 WTM 內建的 `IWtmLogService`、CorrelationId、SlowRequest 中介層的 log 接到你的 SIEM。特別注意 WTMContext RBAC fallback warning。
5. **走 strict CSP？追蹤 #470/#567**：#627 kill-switch 可先在 staging 稽核；生產全面啟用待 widget islandification 完成。見 [`docs/csp-hardening.md`](./csp-hardening.md)。
6. **fork 你自己的版本**：如果業務關鍵到不能讓單一上游維護者決定版本進度，建議自己 fork 一份、加自己 CI、本 fork 當 upstream。
7. **準備好退役路徑**：WTM 走的是 plain ASP.NET Core MVC + EF Core + 一些 helpers，遷移到原生 ASP.NET 不算難，但你應該把這個成本算進採用決策。

---

## 個人判斷（維護者立場）

「我自己會不會把 WTM 上 production？」

- 內部專案、SMB、後台系統 → **會**
- 外部 SaaS、有付費客戶 → **會，但會走補強清單 0-4 全做**（0 = 先確認 #721）
- 服務 > 10K DAU 或合規敏感 → **不會，會走 ABP / 原生 ASP.NET**

「WTM 比起 2026-05 的評估」：
- 地基更穩：CI flake 治本、交易正確性、god-file 可維護性、依賴/打包現代化都有實質進展
- 透明度更高：live e2e 揪出 #721 這類先前隱藏的整合層缺口，並誠實立案追蹤
- 但覆蓋率仍 ~20%、bus factor 仍 = 1、NPOI 仍靠 pin — 這三個結構性弱點沒有本質改變

**WTM 是個誠實的中型框架**。它沒有假裝自己是 ABP，沒有 oversell。「測試 pass + 漏洞掃 0」不是 production-ready 的全部證據——#721 正是活例：全綠的單元測試下藏著一個 HTTP 層的 replay-guard 失效。Production-ready 是個和你的 risk profile 對齊的決定，不是框架本身的屬性；本次重評讓這個決定所需的證據更完整、更誠實。

---

## 相關文件

- [`docs/dependency-management.md`](./dependency-management.md) — 套件版本政策、NU1510 雙意義警告、NPOI security pin 詳解
- [`docs/csp-hardening.md`](./csp-hardening.md) — #470/#627 CSP 硬化 roadmap 與 kill-switch 分級啟用
- [`docs/ci-operations.md`](./ci-operations.md) — internal CI 已知不相容與排錯
- [`docs/wtm-developer-manual.md`](./wtm-developer-manual.md) — 完整開發手冊（§ 安全機制）
- [`docs/structured-logging.md`](./structured-logging.md) — 結構化 log 整合方式
- [`CHANGELOG.md`](../CHANGELOG.md) — 版本演進與每版 breaking changes（[Unreleased] 含本批次全部條目）
