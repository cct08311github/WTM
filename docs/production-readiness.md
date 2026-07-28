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
- CI（Gitea Actions）：build-and-test / js-test / e2e(baseline) / e2e(killswitch) / release-tooling-test / security-scan 全 green
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
- **demo `FileApiController`（LayUI/Vue3/Blazor 三份 template 各一份）九個洞、四類，全修（#830）**：五個 `[Public]` 匿名端點移除（`GetFile` 等，搭配預設 `EnforceTenantFileScope=false` 曾讓任何人猜 GUID 讀他租戶檔案）；`DeletedFile` 改走 `DeleteFileTenantScoped`（原本任一已認證使用者可刪除他租戶的檔案列）並改 `[HttpPost]`；`csName` 全八個動作都先過 `IsKnownConnectionKey`；`GetFileInfo` 改走 `WtmFileProvider` 並只回傳投影欄位。**相容性代價誠實記錄，不是零**：`framework_layui.js`／`MultiUploadTagHelper.cs` 是隨 NuGet 套件出貨給每個下游 app 的共用資產（demo controller 本身只是 scaffold-time 複製的 template，不是），若直接全面改成只送 POST，任何「升級套件但沒動自己複製的 controller」的下游會因為對方 controller 仍是 `[HttpGet]` 而 405、刪除按鈕悄悄壞掉——已在審查中重現。修法：這兩份共用資產先送 POST，只在收到 405 時 fallback 成 GET（且只在 405 時，不吞其他錯誤），對已配合改 `[HttpPost]` 的下游立即拿到完整 CSRF 強化、對還沒改的下游維持原本行為（原本就有的 GET-CSRF 曝險，非新增）。**Vue3 template 的伴隨修復**：Vue3 走純 JWT（`LoginJwt` 從不呼叫 `SignInAsync`，無 auth cookie），拿掉 `[Public]` 前若不修前端，`<img>`/`el-image` 這類瀏覽器原生請求（不帶 axios 的 Authorization header，也沒 cookie 可退）會讓每張圖全部 401 —— 已改走既有的 `fileApi().getFile()`（axios 帶 Bearer + blob URL）而非直接綁原始 URL，涵蓋 `stores/userInfo.ts` 大頭貼、`uploadImage/index.vue` 預覽、`table/index.vue` 圖片型欄位三處。`test/mutants/manifest.json` 新增三筆 `fileapi830-*` mutant（僅 `GetFile`／`DeletedFile` 的 tenant-scope／csName guard 三個守門，1/5 個 `[Public]` 移除點有 mutant 釘住，其餘四個靠 `FileApiControllerHardeningTests830.cs` 的 HTTP 測試涵蓋，非 mutant 級證明）。**GET fallback 有明訂退場**：POST-then-GET-on-405 不是永久行為，追蹤於 #853，觸發條件是 WTM 下一次「major」版號（`version.props` 由 `10.x` 跳到 `11.0.0+`）——刻意選比這個 repo 慣用的 breaking-change 載體（minor 版號）更嚴格、更少見的門檻，兩處 fallback 程式碼與 CHANGELOG 都已標註 #853，避免重蹈 `#470`/`#567` island-render 旗標「預設關、沒人記得要翻」的覆轍。**Blob URL 生命週期**：`URL.createObjectURL` 這個 ClientApp 本來就有一處既存呼叫、卻從未 `revokeObjectURL`；本次把三個新讀圖點都接上同一個 helper，等於把既有的潛在洩漏放大（`table/index.vue` 尤其明顯——每次搜尋/翻頁/篩選都會為每列每個圖片欄位各建一個 blob URL）。已補上：每個呼叫點在建立新 URL 前先 revoke 即將被取代的舊值、元件 unmount 時 revoke 剩餘持有值；`stores/userInfo.ts` 另外修正一個關聯正確性問題——blob URL 存進 sessionStorage 後在重新整理頁面時會失效，改為快取 `photoId` 並在 cache hit 時重新解析。

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
| Open source 社群 | 小，主要溝通在 Gitea issues |
| 主流商業支援 | 無 SLA、無付費 support 管道 |
| File cleanup（Add 路徑孤兒檔案） | #815 修 `DeletedFileIds` 任意檔案刪除漏洞——最終在寫入端（`BaseCRUDVM.DoAdd/DoEdit/DoDelete` 系列）kill 掉 primitive 本身：posted 的 `FileAttachment` FK 若在呼叫者自己的 tenant scope（query filter 強制開啟，不受 `EnforceTenantFileScope` 影響）下無法解析，直接在 `SaveChanges` 前被拒絕/還原，讀跟刪都連帶被擋。`DeleteFileTenantScoped`（sink 端 tenant-scoped 解析）與 entity-reference 檢查保留為第二層防禦。代價是 `DoAdd`/`DoAddAsync` 路徑上 `DeletedFileIds` 完全失效（新增中的一列沒有可驗證的既存 FK 參照）——「上傳後、儲存前取消」的檔案會變成永久孤兒 `FileAttachment` 列 + 實體 blob，無框架清理機制。已立案 #822（reaper，非 P0，僅儲存空間浪費）。 |
| #815 tenant-scoping 的已知邊界（單一租戶部署） | tenant-scoped 檢查比對 `FileAttachment.TenantCode == DC.TenantCode`；未啟用多租戶時兩邊通常都是 `null`，比對永遠成立、等於沒有隔離。也就是說 #815 這輪修的是「任意租戶」的洞；在單一租戶部署裡剩下的是「任一使用者可刪除/引用任何其他使用者的檔案」，這條 tenant scoping 從未處理過，仍是 open（`FileAttachment` 本來就沒有 owner 欄位，需要另外的 per-caller 授權機制，如 #814/#811 系列）。 |
| #815 NULL-tenant 檔案的第二個窄化 | tenant-scoped 檢查把 NULL tenant 當成「只有 tenant 也是 NULL 的呼叫者」才能解析——這代表已啟用多租戶的使用者，如果自己的記錄合法引用了一個 NULL-tenant（例如遷移前、main-host 上傳、或多租戶啟用前留下）的舊檔案，現在再也無法透過 `DeletedFileIds` 清掉它了（write-time 的 `RejectUnresolvableFileAttachmentReferences` 與 sink 端的 `DeleteFileTenantScoped` 都會判定「不可解析」而擋下）。**決定**：維持現狀（NULL-tenant 檔案不視為對所有租戶開放），因為把它放寬等於替 #815 想關的同一類 primitive（任意租戶可觸及不屬於自己的檔案）開一個新的、範圍更窄但機制相同的後門——按本專案 Compatibility → Security → Quality 的優先順序，安全的一側勝出。代價是這批舊資料的孤兒清理需要另外的管理員工具或一次性 migration，未來若要放寬，應該用一個顯式的 opt-in 設定而非預設行為改變。同一條規則（unconditional tenant-scoped resolution）也適用於下一列的 `ISubFile` 集合子項。 |
| #815 第三輪——`ISubFile` 集合的寫入端補洞 | `RejectUnresolvableFileAttachmentReferences` 原本只檢查 `TModel` 自身的純量 `FileAttachment` 屬性；`List<T>`（`T : ISubFile`，例如 `Product.Attachments`）集合完全沒有寫入端驗證——攻擊者可用 `Attachments = [ new ProductAttachment { FileId = <victim GUID> } ]` 直接寫入未經檢查的 FK，且這條路徑連 `DeletedFileIds` 的 entity-reference 第二層防禦都沒有（`GetOwnFileIds` 從未涵蓋集合屬性）。修法：在同一個方法裡新增對 `IEnumerable<ISubFile>` 屬性的走訪，套用與純量欄位相同的 tenant-scoped 解析。`DoRealDelete`/`DoRealDeleteAsync`/`DoBatchDelete`/`DoBatchDeleteAsync` 對此集合的刪除早在 #815 second rework 就已經是 tenant-scoped（見上面兩列），只是先前沒有測試透過集合路徑實際驗證過；本輪同時補上這批涵蓋測試。 |
| #815 第四輪——不寫入無效 FK；批次解析 + async | Code review（同 vendor Claude + cross-vendor gpt-5.6-sol）指出第三輪把無法解析的子項 `FileId` 清空為 `Guid.Empty` 這件事本身就是個 bug：`ISubFile` 強制非 nullable `Guid FileId`，且 `DataContext.OnModelCreating` 對 `ISubFile` 型別是 `continue`（維持 EF 預設 convention），所以真正的 FK constraint 一定存在——`Guid.Empty` 在任何有 FK 檢查的資料庫上都不對應任何列。結果第三輪自稱的「controlled rejection」在真實 DB 上其實是 `DoEdit` 整筆（含合法的 scalar 變更）靜默 rollback、`DoAdd` 丟未捕捉的 `DbUpdateException`。修法：改成把無法解析的子項從**已 posted 的集合本身**移除（透過 `IList.Remove`，`Utils.CheckDifference`/`toadd` 迴圈或 cascade-add 因此永遠看不到它），而不是覆寫欄位值；非 `IList`（例如固定長度陣列）的少見情況才退回清空整個屬性（fail closed，優先順序：Compatibility → **Security** → Quality）。同時把 `IsFileAttachmentResolvableForCaller`（每個候選 id 各發一次同步查詢）拆成 `CollectFileAttachmentCandidates`（純記憶體，收集所有候選 id）+ `ResolveFileAttachmentIdsForCaller`/`ResolveFileAttachmentIdsForCallerAsync`（單一批次查詢）+ `ApplyFileAttachmentResolution`（套用結果），並讓 `DoAddPrepareAsync`/`DoEditPrepareAsync`（`DoAddAsync`/`DoEditAsync`/`DoDeleteAsync` 呼叫）改走 await 版本，不再是 async 請求路徑上的 sync-over-async N+1（#128 同款 ThreadPool-starvation 教訓）。三個第三輪寫在 EF InMemory 上的 write-gate 測試移到 `DoEditFkWriteGateSubFileSqliteTests815.cs`（FK-enforcing 的 SQLite `ProductSubFileContext` fixture）——見 `.claude/rules/testing.md`「EF InMemory limits」一節，現在明確記載這次事故。 |
| #823（追蹤中，非本輪範圍） | `_FrameworkController.UpdateModelProperty`（`#797` 刻意讓它跳過 `DoEditPrepare`，因此繞過本頁 `RejectUnresolvableFileAttachmentReferences` 這個寫入端閘門）是另一個可由呼叫者指名任意 VM 型別＋欄位的寫入端點，缺 VM 型別層授權。不在本輪修復範圍，已擴大 #823 追蹤，之後不應該用「繞回 DoEditPrepare」的方式修，因為那會違反 #797 的既有設計決策。 |
| #824（部分已修，其餘追蹤中） | `BasePagedListVM.UpdateEntityList`、`BaseBatchVM.DoBatchEdit`/`Async`、`BaseImportVM.BatchSaveData` 的 Excel 對應、grandchild `IEnumerable<ISubFile>`、直接 `DbSet` 存檔等 FK-WRITE 路徑，仍然可以寫入呼叫者無法解析的 `FileAttachment`/`ISubFile.FileId`（#815 第一輪 code review 用 `DoBatchEdit` 實測證實）。Issue #824 建議的修法是在 `EmptyContext.SaveChanges`/`SaveChangesAsync` 這個所有寫入路徑共同的邊界＋EF relationship metadata 統一擋，而不是逐個 sink 加閘門——**這個架構性修法尚未實作，仍是 open**。PR #849（`security/824-attachment-fk-write-gate`）先在 `UpdateModelProperty` 這一個 sink 補了同樣精神的 EF-metadata-driven 判定（見下一列），屬於單點防禦，不是 #824 建議的邊界方案；其餘五條路徑仍然開放，等同本列描述的現狀。 |
| #824 Part 1 —— `UpdateModelProperty` sink gate（已合併，PR #849） | `_FrameworkController.UpdateModelProperty`（`[AllRights]`，`#797` 讓它跳過 `DoEditPrepare`）原本對 FK 型別欄位沒有任何驗證，任何呼叫者可把欄位設成任一租戶的 `FileAttachment` GUID。已加上與 #815 `RejectUnresolvableFileAttachmentReferences` 同精神的判定——`DCExtension.IsFileAttachmentForeignKeyProperty`（EF relationship metadata 驅動，不硬編欄位名稱），不分租戶一律拒絕寫入。**這只補了資料完整性，不是新關掉一條可利用的讀取或刪除鏈**：寫入本身沒有授予新的讀取能力（`GetFile` 預設 `EnforceTenantFileScope=false` 時走 `IgnoreQueryFilters()`，本來就能用已知 GUID 跨租戶讀取，這件事跟 FK 有沒有被偽造無關）；也沒有重開 #815 的刪除面——`DoAdd`/`DoEdit` 系列的孤兒清理只在 client 實際 POST `DeletedFileIds` 時才跑（不是自動觸發），且經過 `DeleteFileTenantScoped` → `DeleteFileCore(..., enforceTenantScope: true)` 的 tenant-scoped 解析（`FileAttachment` 實作 `ITenant`，全域 query filter 生效），偽造的跨租戶 FK 在該路徑上解析不到、等於 no-op。價值在於：擋掉一筆本來就不該落地的未授權跨租戶參照，並讓這個 sink 補齊 #815 已經在 Add/Edit VM 路徑上維持的同一條 same-tenant-FK 不變式。仍未涵蓋（見上一列）：`BasePagedListVM.UpdateEntityList`、`BaseBatchVM.DoBatchEdit`/`Async`、`BaseImportVM.BatchSaveData`、grandchild `IEnumerable<ISubFile>`、直接 `DbSet` 寫入——Issue #824 建議的 `SaveChanges` 邊界方案仍未實作。 |
| #859 —— `_Framework/GetFile`/`ViewFile` 未認證跨租戶讀取，`EnforceTenantFileScope` 預設翻成 `true` | 上一列（#824 Part 1）記錄的「`GetFile` 預設 `EnforceTenantFileScope=false` 時走 `IgnoreQueryFilters()`，本來就能用已知 GUID 跨租戶讀取」這件事本身，加上三份 demo template 都把 `IsFilePublic` 設成 `true`（讓 `PrivilegeFilter` 連認證都不要求），組成了 #859 的完整鏈：Vue3Demo 是唯一 `IsQuickDebug:false` + `EnableTenant:true` 的 production 形狀樣板，未認證呼叫者可直接讀走任一租戶的檔案內容。修法兩半：套件半把 `FileUploadOptions.EnforceTenantFileScope` 預設翻成 `true`（`WtmFileProvider.GetFile` 改為預設honour 全域 `ITenant` query filter），並在 `FrameworkServiceExtension.UseWtmContext` 加上 `IsFilePublic==true` 非 Development 環境的 `LogCritical`（比照既有 `IsQuickDebug` 守門模式，但只 log 不 throw——`IsFilePublic` 有正當用途，可能是操作者刻意開啟）；template 半把三份 demo appsettings.json 的 `IsFilePublic` 改回 `false` 並加註說明。翻轉前的四項覆核：**(1) NULL-tenant 舊檔案**——沿用 #815 已記錄的決定（見上面「#815 NULL-tenant 檔案的第二個窄化」列）：EF 對 nullable 欄位的 `==` 轉譯是 null-safe，`TenantCode == null` 的檔案只有「呼叫者自己的 tenant 也是 null」才能解析到；這是 #815 已經在 `DeleteFileTenantScoped` 上採用的既定先例，`GetFile` 翻轉後沿用同一條規則，不另開窄化的例外——放寬等於重開 #815 關掉的同一個 primitive。**(2) 每個 `Upload(` call site 是否都蓋了 `TenantCode`**——逐一檢查 `src/`（`_FrameworkController.cs` 四處、`BaseImportVM.cs`、`WtmFileProvider.Upload`）與 `demo/`（三份 `FileApiController.cs`、`ConsoleDemo`）呼叫點：全部經過 `WtmFileProvider.Upload`（非 DB handler 分支自己 `new FileAttachment` 時蓋）或 `WtmDataBaseFileHandler.UploadToDB`（database 模式自己蓋），兩處都執行 `file.TenantCode = _wtm.LoginUserInfo?.CurrentTenant;`——沒有找到漏蓋的路徑。**(3) 背景/無身分路徑**——`GetFile(` 的呼叫點全部落在 `_FrameworkController`/demo `FileApiController`（HTTP request-scoped）與 `BaseCRUDVM`/`BaseImportVM`（同樣掛在 request-scoped 的匯入流程上）；`grep BackgroundService/IHostedService` 找到的六個背景服務（`EtlHostedService`、`DashboardSnapshotHostedService`、`DashboardAlertHostedService`、`ActionLogRetentionService`、`RefreshTokenRetentionService`、`WorkflowTimerHostedService`）沒有一個呼叫 `WtmFileProvider.GetFile`——翻轉不會讓任何現有背景路徑讀不到檔案。註：#843 談的是同一個「`LoginUserInfo==null` 時怎麼辦」根因家族，但方向相反且機制不同——`ApplyDataPrivilegeForAnalysis` 對無身分 fail-*open*（不過濾，過度可見），`GetFile` 的 tenant query filter 對無身分 fail-*closed*（只解析 `TenantCode` 也是 null 的列，看不到其他租戶）；#843 仍是 open issue，但不影響、也不被本修法影響。**(4) `HasMainHost`**——demo appsettings.json 的 `mainhost.Address` 預設整行被註解掉，`Configs.HasMainHost` 因此預設 `false`；`GetUserPhoto` 的 `HasMainHost && CurrentTenant==null` redirect 是路由層邏輯，發生在呼叫 `GetFile`之前，且兩端（上傳與讀取）在同一台 mainhost 節點上都用同一個 `LoginUserInfo?.CurrentTenant`（mainhost 自己的使用者通常也是 null tenant），翻轉後行為一致，未發現互相干擾。**結論：翻轉可以安全進行，四項覆核均未發現阻擋理由**，唯一需要記錄的行為改變是：一個真正打算跨租戶公開、但上傳時蓋了非 null `TenantCode` 的「公開」檔案，翻轉後對匿名呼叫者不再可解析（除非透過 Referer-based tenant 路由巧合命中同一租戶）——這類檔案需改用 null-tenant/main-host 情境上傳，或另建專用公開檔案儲存區。**寫驗收測試時另外發現一個相鄰但獨立的 bug**：`WtmDataBaseFileHandler.GetFileData`（`database` SaveMode 專用）自己又下了一次「不受 `EnforceTenantFileScope` 控制、永遠套用」的 tenant-scoped query，跟外層 `WtmFileProvider.GetFile` 的（依旗標決定要不要套用租戶過濾的）查詢結果不一致時——也就是 `EnforceTenantFileScope=false` 且真的跨租戶讀取時——不是洩漏也不是乾淨拒絕，而是回傳 `null` DataStream 讓 controller 端丟未捕捉的 `NullReferenceException`（表現成 HTTP 400）。`WtmLocalFileHandler`/`WtmOssFileHandler` 沒有這層多餘過濾。這使得本次修法自己文件裡承諾的「`EnforceTenantFileScope=false` 這個顯式 opt-out 仍然有效」對 `database` SaveMode 不成立，因此在本 PR 一併修掉（`GetFileData` 改用 `IgnoreQueryFilters()`，理由：全庫只有 `WtmFileProvider.GetFile` 這一個呼叫點，執行到這裡時外層授權判斷早已完成，不是新開的洞）。 |

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
- [`docs/ci-operations.md`](./ci-operations.md) — Gitea Actions 已知不相容與排錯
- [`docs/wtm-developer-manual.md`](./wtm-developer-manual.md) — 完整開發手冊（§ 安全機制）
- [`docs/structured-logging.md`](./structured-logging.md) — 結構化 log 整合方式
- [`CHANGELOG.md`](../CHANGELOG.md) — 版本演進與每版 breaking changes（[Unreleased] 含本批次全部條目）
