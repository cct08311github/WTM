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
| 公開 SaaS、中等流量、含付費客戶 | ⚠️ **可以但需補強** | 採用前完成下方「production 補強清單」1-4 項 |
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
| 認證 | PBKDF2 密碼 + legacy MD5 自動 migration、JWT（access + refresh + `jti` replay guard，`AccountController`／`_FrameworkController` 的 refresh 路由衝突已於 2026-07-17 修復，#721）、`_remotetoken` 完整簽章驗證 |
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

整體方向是**縱深強化**。本批次曾**誠實揭露一個真實缺口**（#876：ETL controller 從未接到全域 filter），寫驗收測試當下就地立案並在同一輪修復——過程本身正是為什麼「測試 pass + 漏洞掃 0」不是 production-ready 的全部證據：這個缺口不是掃描器或既有測試找到的，是寫一個新測試、實測觀察真實 HTTP 行為才浮現。

**強化（已合併）**：
- **`FrameworkFilter.OnResultExecuted`未轉義 `ViewDivId` 內插進 inline `<script>`（#799，MEDIUM）**：`FrameworkFilter.cs:394` 對每個 model 是 `BaseVM` 的 `PartialViewResult` 原樣輸出 `<script>try{ff.ResizeChart('{ViewDivId}')}catch{}</script>`，沒有任何編碼。**可觸及性逐段驗證，非照抄 issue 文字**：`WTMContext.CreateVM` 把 `Request.Form`/`.Query` 的每個 key 原樣複製進 VM 的 `FC` dictionary（含攻擊者可控的 `"ViewDivId"` 欄位）；`_FrameworkController.Selector`（`[AllRights]`，只需認證、不需頁面級授權的 POST 端點，`RedoUpdateModel`＋`PartialView` 這個組合在 `_FrameworkController.cs:417/777/871` 重複出現）呼叫 `RedoUpdateModel(listVM)`，用原始反射（`PropertyHelper.SetPropertyValue`）把 `FC` 的每個值寫進 VM 對應屬性——完全繞過 ASP.NET 自己的 model binder attribute，`ViewDivId` 沒有 `[BindNever]`從來就不是真正的缺口所在。兩個原本可能擋下它的機制都不成立：`grep -rn 'ValidateAntiForgeryToken|AutoValidateAntiforgeryToken' src/` 全庫零結果（本專案完全沒有 antiforgery 層）；`WtmCspOptions.ScriptSrc` 預設 `"'self' 'unsafe-inline'"`（`WtmCspOptions.cs:42`），出貨預設 CSP 也擋不住注入的 inline `<script>`。修法：改用 `JavaScriptEncoder.Default.Encode`——`HtmlEncode` 不轉義 `'`/`\`，擋不住 JS 字串字面量的斷句攻擊，`JavaScriptEncoder` 才是這個 sink 該用的工具；比照本庫既有同款 sink 的做法（`DataTableTagHelper.cs`、`PrivilegeFilter.cs` 既有的 `jsRedirect`/`jsLp`）。**同檔案＋同目錄 sibling 掃描**：`Filters/` 下的 `DataContextFilter.cs`/`PrivilegeFilter.cs`/`SwaggerFilter.cs` 均已檢查，未發現其他未轉義的 inline `<script>` 內插——`PrivilegeFilter.cs:202` 的同款 sink 本來就已經正確編碼。**不需版本翻升、非相容性破壞**：`JavaScriptEncoder.Default` 只改寫落在其安全字元集外的字元（`<`/`>`/`&`/`'`/`"`/`\`/控制字元），預設產生的 `ViewDivId`（`BaseVM.ViewDivId` 自己的 getter：`"ViewDiv" + UniqueId`，純英數字）與一般開發者自訂的 id 都不含這些字元，既有 app 的正常渲染輸出逐位元組不變——這點由一個 positive-control 測試證明，不只是靠推論。測試：`test/WalkingTec.Mvvm.Admin.Test/FrameworkFilterViewDivIdXssTests799.cs` 讓惡意 `ViewDivId` 走真實的 `Request.Form` → `WTMContext.CreateVM` → `Selector` 自己的 `RedoUpdateModel` → 真正的 `FrameworkFilter.OnResultExecuted` 實例，斷言的是「已轉義的位元組確實出現」（用同一個 `JavaScriptEncoder.Default.Encode` 算出預期值），不只是「原始 payload 不見了」。已註冊 mutant `mvc799-viewdivid-xss-script-encode`（`test/mutants/entries/`），刪掉 `JavaScriptEncoder.Default.Encode(...)` 呼叫會讓斷言變紅，驗證為 KILLED。
- ETL REST source：next-link 跨源憑證轉發封堵 + 有限 MaxPages（#661）；bulk-load 欄名 allowlist（`^[\p{L}\p{N}_#$]+$`，擋 SQL 注入字元、放行 CJK）+ MSSQL bracket 跳脫（#680）
- S3 handler：例外收斂、`../` key traversal 封堵、ContentType 設定（#680）
- GitHub mirror leak-gate 改為無條件執行 + sanitize 全文字檔化（#659）
- Analysis 白名單先於 Expression Tree 的安全邊界持續完好
- **ETL 實體 ITenant 覆蓋率 + 根因 wiring 缺陷（#841、#862，P1）**：`#836` 窮舉表找出四個 `class .* : BasePoco` 沒實作 `ITenant` 卻帶租戶資料的實體。修復三個（`EtlDeadLetterRow` 無須遷移、`EtlLineageRecord`／`EtlRunLog` 各自新增 `TenantCode` 欄位＋遷移腳本，見 CHANGELOG），第四個（Core 的 `ChangeLog`，稽核表，目前無 viewer）**刻意不修**——立案存證，未動 Core 行為。**根因更深**：`EtlJobDefinition` 自 ETL-006 起就實作 `ITenant`，但 `ApplyEtlModels()` 在消費端 `DataContext.OnModelCreating` 裡是**在** `base.OnModelCreating()` 之後才註冊 ETL 實體型別，而 `FrameworkContext.OnModelCreating` 的 Pass 2 迴圈（套用 `ITenant` 過濾器那段）早就跑完、看不到之後才註冊的型別——這個過濾器**從未真正生效過**，實測確認：兩個不同租戶的 `EtlJobDefinition` 互相可見，產生的 SQL 完全沒有 `TenantCode` 條件。修法是新增一個帶 `EmptyContext` 參數的 `ApplyEtlModels` 多載，在註冊每個型別後立刻補套用過濾器；舊多載保留但標 `[Obsolete]`。**修完這個 wiring 後，另外發現並修掉一個原本會被引入的迴歸**：`EtlSchedulerService`/`EtlQuartzJob`/`DbEtlGovernanceStore` 都是背景排程（自己開 DI scope，`TenantCode` 恆為 null），過濾器一旦真的生效，這個共用排程器就會再也找不到任何租戶所屬的 job/run log——已補上 `IgnoreQueryFilters()`（每處都有註解說明原因）。另外三個 ETL controller（`_EtlRunLogController`/`_EtlMonitorController`/`_EtlSchemaController`）補上與既有兩個 controller 相同的 Admin/ETLAdmin 角色守衛（#841 主體）。**寫驗收測試時意外挖到一個範圍大得多、與本項無關的既有缺陷，已獨立立案並修復（#876，見下一條）**：`WalkingTec.Mvvm.Etl` 整個組件的 controller 透過真實 HTTP 從未接到 WTM 三個全域 filter，導致 `Wtm` 恆為 null——角色守衛的邏輯本身正確（unit test 證實），但因此對任何呼叫者（含真正的 Admin）都會 fail-closed。
- **#876（P0，已修）：`WalkingTec.Mvvm.Etl` controller 角色守衛讀到的 `Wtm` 現在真的會被填值——修掉上一條找到的缺口。** 根因：ASP.NET Core 自己的 `ControllerActionFilter`（呼叫 controller 自己的 `OnActionExecuting` override 的內部包裝，凡是 `Controller` 子類別實作 `IActionFilter` 就會被框架自動加上）被框架寫死 `Order = int.MinValue`——永遠比任何自訂 filter（含 WTM 的三個全域 filter）先跑，不論那些 filter 的 `Order`設多少都贏不了（實測：把 `DataContextFilter.Order` 設成 `-1000` 完全沒變化）。`DataContextFilter` 正是把 `BaseController.Wtm` 設值的地方。全部原始碼裡只有這五個 `WalkingTec.Mvvm.Etl` controller（`_EtlJobController`、`_EtlDashboardController`、加上 #841 新增的三個）會在自己的 `OnActionExecuting` override 裡直接讀 `Wtm`——WTM 其他 controller 一律走 `PrivilegeFilter` 的宣告式、URL-based 授權——所以只有這五個會踩到這個排序缺口。**實際觀察行為（修復前，實測非推測）：乾淨的 403/redirect 拒絕，不是 `NullReferenceException`/500**——更正上一條「必定 NullReferenceException」的推測：守衛裡的 null-conditional（`Wtm?.LoginUserInfo?.Roles`）把「`Wtm` 是 null」變成靜默的空角色清單而非拋例外，`context.Result = Forbid()` 因此無條件觸發。這其實是可用性缺陷（整個 ETL 管理 UI 對所有人、含真正的 Admin，100% 鎖死），不是框架讓未授權呼叫者悄悄通過；而且因為在 `OnActionExecuting` 裡設 `context.Result` 會讓整條 filter pipeline 短路，`DataContextFilter`/`PrivilegeFilter`/`FrameworkFilter` 對這五個 action 從未真正跑過一次——action body 從未真的被呼叫過，原本推測的 NRE 根本沒有機會發生。修法：新增 `WtmControllerActivator`（`src/WalkingTec.Mvvm.Mvc/Helper/WtmControllerActivator.cs`）取代預設 `IControllerActivator`，在 controller **建構時**（早於任何 filter，含寫死排第一的 `ControllerActionFilter`）就把 `Wtm` 設好——這是框架層級的修法（在 `AddWtmContext` 註冊一次），對所有 controller/組件一視同仁，不是只補這五個 controller，未來任何第三個組件踩到同樣 anti-pattern 也一併涵蓋。**範圍確認**：`WalkingTec.Mvvm.WorkFlow` 的 controller 共用同一個 `BaseController`，理論上同樣暴露在這個排序缺口下，但沒有一個會在自己的 `OnActionExecuting` 裡讀 `Wtm`，所以從未出現症狀——這次修法對它們同樣有益，不需要額外改動。**Blast radius 已查**：`WalkingTec.Mvvm.Api.Test`（76）、`WalkingTec.Mvvm.Mvc.Tests`（52）、`WalkingTec.Mvvm.Etl.Test`（713）、`WalkingTec.Mvvm.WorkFlow.Test`（591）全數重跑，全綠，ETL controller 現在真的可以被呼叫到也沒有引出潛在失敗。測試：`EtlControllerGateHttpTests.cs` 新增三個真實 HTTP 的 positive control（真正的 ETLAdmin 現在可以通過守衛、跑到 action body），補齊 #841/#862 那條說明的「只能在 unit test 驗證」缺口。Mutant `876-wtmcontrolleractivator-neutralize`（`test/mutants/entries/`）：中和 `WtmControllerActivator` 的 `Wtm` 填值 guard，驗證為 KILLED。
- **demo `FileApiController`（LayUI/Vue3/Blazor 三份 template 各一份）九個洞、四類，全修（#830）**：五個 `[Public]` 匿名端點移除（`GetFile` 等，搭配預設 `EnforceTenantFileScope=false` 曾讓任何人猜 GUID 讀他租戶檔案）；`DeletedFile` 改走 `DeleteFileTenantScoped`（原本任一已認證使用者可刪除他租戶的檔案列）並改 `[HttpPost]`；`csName` 全八個動作都先過 `IsKnownConnectionKey`；`GetFileInfo` 改走 `WtmFileProvider` 並只回傳投影欄位。**相容性代價誠實記錄，不是零**：`framework_layui.js`／`MultiUploadTagHelper.cs` 是隨 NuGet 套件出貨給每個下游 app 的共用資產（demo controller 本身只是 scaffold-time 複製的 template，不是），若直接全面改成只送 POST，任何「升級套件但沒動自己複製的 controller」的下游會因為對方 controller 仍是 `[HttpGet]` 而 405、刪除按鈕悄悄壞掉——已在審查中重現。修法：這兩份共用資產先送 POST，只在收到 405 時 fallback 成 GET（且只在 405 時，不吞其他錯誤），對已配合改 `[HttpPost]` 的下游立即拿到完整 CSRF 強化、對還沒改的下游維持原本行為（原本就有的 GET-CSRF 曝險，非新增）。**Vue3 template 的伴隨修復**：Vue3 走純 JWT（`LoginJwt` 從不呼叫 `SignInAsync`，無 auth cookie），拿掉 `[Public]` 前若不修前端，`<img>`/`el-image` 這類瀏覽器原生請求（不帶 axios 的 Authorization header，也沒 cookie 可退）會讓每張圖全部 401 —— 已改走既有的 `fileApi().getFile()`（axios 帶 Bearer + blob URL）而非直接綁原始 URL，涵蓋 `stores/userInfo.ts` 大頭貼、`uploadImage/index.vue` 預覽、`table/index.vue` 圖片型欄位三處。`test/mutants/manifest.json` 新增三筆 `fileapi830-*` mutant（`GetFile`／`DeletedFile` 的 tenant-scope／csName guard 三個守門）。**#857 更正**：這裡曾寫「1/5 個 `[Public]` 移除點有 mutant 釘住，其餘四個靠 HTTP 測試涵蓋、非 mutant 級證明」——查驗當下 `FileApiControllerHardeningTests830.cs` 其實只有 3 個 `[TestMethod]`，`GetFileName`／`GetFileInfo`／`GetUserPhoto`／`DownloadFile` 四個動作連 HTTP 測試都沒有，這句宣稱本身不成立。已補齊：四個動作各自新增 marker-based 的 401/leak HTTP 測試，並各補一筆 `fileapi857-public-*-reintroduce` mutant（重新加回 `[Public]`，紅測試需為對應的新測試），現在五個 `[Public]` 移除點全部有 mutant 級證明，不再是純 HTTP-測試層級的宣稱。**GET fallback 有明訂退場**：POST-then-GET-on-405 不是永久行為，追蹤於 #853，觸發條件是 WTM 下一次「major」版號（`version.props` 由 `10.x` 跳到 `11.0.0+`）——刻意選比這個 repo 慣用的 breaking-change 載體（minor 版號）更嚴格、更少見的門檻，兩處 fallback 程式碼與 CHANGELOG 都已標註 #853，避免重蹈 `#470`/`#567` island-render 旗標「預設關、沒人記得要翻」的覆轍。**Blob URL 生命週期**：`URL.createObjectURL` 這個 ClientApp 本來就有一處既存呼叫、卻從未 `revokeObjectURL`；本次把三個新讀圖點都接上同一個 helper，等於把既有的潛在洩漏放大（`table/index.vue` 尤其明顯——每次搜尋/翻頁/篩選都會為每列每個圖片欄位各建一個 blob URL）。已補上：每個呼叫點在建立新 URL 前先 revoke 即將被取代的舊值。**#857 更正**：這裡曾多寫一句「元件 unmount 時 revoke 剩餘持有值」，對三個呼叫點一概而論——`uploadImage/index.vue`／`table/index.vue` 是 Vue 元件，各自掛了 `onUnmounted` 確實會 revoke；但 `stores/userInfo.ts` 是 Pinia store，沒有元件生命週期可掛，只在 `setUserInfos()` 命中 sessionStorage 快取、要用新值取代舊值那一刻才 revoke，store 本身消失時不會額外 revoke（見 #856）。`stores/userInfo.ts` 另外修正一個關聯正確性問題——blob URL 存進 sessionStorage 後在重新整理頁面時會失效，改為快取 `photoId` 並在 cache hit 時重新解析。

**新揭露的缺口（未修，已立案）**：

- **#883（P0）：#841/#862 的 `IgnoreQueryFilters()` 有多處位於 HTTP controller 共用的路徑上，形成跨租戶 IDOR；且與 #876 有合併順序約束。** `b3dbae4b3` 為了讓背景排程器在新啟用的 tenant filter 下仍能運作加了 17 處 `IgnoreQueryFilters()`，其中 `EtlSchedulerService` 的 `TriggerNowAsync`/`RescheduleAsync`/`SkipNextAsync`/`DryRunAsync`/`RerunFromSnapshotAsync`/`ScheduleJobAsync`/`UpdateStatusAsync` 七處是 HTTP controller（`_EtlJobController`/`_EtlRunLogController`）的共用路徑，不是純背景。**鏈路**：`_EtlMonitorController.Running` 對任何通過角色閘門的 ETLAdmin 回傳 `EtlProgressTracker`（無租戶維度的 `ConcurrentDictionary<Guid, EtlProgress>`）裡所有租戶的 `JobId`；拿到別租戶的 id 後餵進 `DryRun`（caller-controlled id，`IgnoreQueryFilters()` 載入後回傳來源資料預覽列）或 `TriggerNow`/`SkipNext`/`Reschedule`，即可讀取或操作其他租戶的 job——角色閘門只檢查 `Admin`/`ETLAdmin`，不比對輸入列與呼叫者的 `TenantCode`。**#876 意外遮住這條路徑**：#876 修復前 `Wtm` 恆為 null，角色閘門對所有人 fail-closed，這個「全員鎖死」的可用性缺陷剛好也擋住了這條 IDOR；#876 一旦修復，閘門開始正常放行合法呼叫者，這條路徑立刻變得可利用。**因此 PR #882（#876）不得在本票修復前合併**，兩者需一起或依序落地。另有兩項需同時處理：(A) `[Obsolete]` 的舊 `ApplyEtlModels()` 多載完全沒有 tenant filter，但 `docs/etl-module.md`／`docs/workflow.md`／`docs/wtm-developer-manual.md`（兩處）仍在教這個寫法——`[Obsolete]` 只在重新編譯且有人看 warning 時才有效，NuGet-only 升級的下游完全拿不到修復。(B) 內建 `EtlQuartzJob` 在背景執行時，把 dead-letter/lineage 的 `TenantCode` 寫成背景 scope 的 `dc.TenantCode`（恆為 null），而非已載入的 `jobDef.TenantCode`——多租戶 job 產生的這兩種列升級前後都是 null，然後被新 filter 對所有真實租戶隱藏；`EtlRunLog` 不受影響（已正確使用 `jobDef.TenantCode`）。修法方向：`IgnoreQueryFilters()` 不該由「這個方法是否可能被背景呼叫」決定，改用 #843 已建立的 `declaredSystemQuery` 式明確呼叫端身分契約，區分 HTTP 入口（tenant-scoped）與背景入口（明確宣告 unfiltered）。來源：`/codex:adversarial-review`（gpt-5.6-sol）對 `b3dbae4b3` 的單題驗證，主 session 已複驗 `DryRunAsync` 的 caller-controlled id 與 `EtlProgressTracker` 無租戶維度兩點。

**#863 更正**：這裡曾在下列三個都已關閉後仍列為未修，其中 #721 那條還進一步指示採用者「先確認已修」——等於叫人去查一個已經不存在的問題，而這份文件正是 CLAUDE.md Red Line 用來比對每個 commit/PR/CHANGELOG 宣稱強度的基準，基準本身漂移就讓整個機制失效。三者現況：#721（HTTP 層 refresh 路由衝突）已於 2026-07-17 修復；#722（`ff.OpenDialog2` 剝除 selector 對話框自身 `<script>`）已於 2026-07-18 修復；#696（public mirror 洩漏 macOS 使用者名稱/內部路徑）已於 2026-07-18 修復。維護機制：`ProductionReadinessBaselineDriftTests863`（`test/WalkingTec.Mvvm.Core.Test/Security/`）會抓出本節每個以 `- **#N` 起始的條目、查 Gitea API 狀態，若已關閉卻還列在這裡就會讓測試紅——需要網路與（非必要的）token，環境不可用時明確 Inconclusive，不會誤判成綠燈。

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
| #824 —— `ExecuteUpdate`/`ExecuteDelete` 繞過 `SaveChanges` 的迴歸守衛（`ExecuteUpdateDeleteFileAttachmentInvariantTests`，範圍誠實限定，#857） | `SaveChanges`/`SaveChangesAsync` 不是「所有寫入的唯一必經點」——EF Core 的 `ExecuteUpdate(Async)`/`ExecuteDelete(Async)` 直接編譯成 SQL、繞過 change tracker（也就繞過 `SaveChanges`）。2026-07-28 全庫稽核當下 `src/` 底下沒有任何 `ExecuteUpdate`/`ExecuteDelete` 呼叫點碰 `FileAttachment`，`ExecuteUpdateDeleteFileAttachmentInvariantTests.cs` 把這個事實寫成一支迴歸守衛測試——同一個原始碼**陳述式**裡若同時出現 `FileAttachment` 與 `.ExecuteUpdate`/`.ExecuteDelete`，測試就會失敗。**這是刻意選擇的字面 grep 式掃描，不是語意/別名分析，範圍有一個已知、記錄在案的洞**：把查詢先指派給區域變數、在下一個陳述式才呼叫，就不在同一句陳述式內，掃不到——`var files = DC.Set<FileAttachment>(); files.ExecuteDeleteAsync();` 這種寫法會被放過。測試本身不是恆真（`DetectionLogic_FindsADeliberateViolation_InFixtureText` 這支 sanity check 證明抓得到直接寫法的違規），但也不是完整的 sink 證明；同一份限定文字也寫在該測試檔的類別註解裡。 |
| #859 —— `_Framework/GetFile`/`ViewFile` 未認證跨租戶讀取，`EnforceTenantFileScope` 預設翻成 `true` | 上一列（#824 Part 1）記錄的「`GetFile` 預設 `EnforceTenantFileScope=false` 時走 `IgnoreQueryFilters()`，本來就能用已知 GUID 跨租戶讀取」這件事本身，加上三份 demo template 都把 `IsFilePublic` 設成 `true`（讓 `PrivilegeFilter` 連認證都不要求），組成了 #859 的完整鏈：Vue3Demo 是唯一 `IsQuickDebug:false` + `EnableTenant:true` 的 production 形狀樣板，未認證呼叫者可直接讀走任一租戶的檔案內容。修法兩半：套件半把 `FileUploadOptions.EnforceTenantFileScope` 預設翻成 `true`（`WtmFileProvider.GetFile` 改為預設honour 全域 `ITenant` query filter），並在 `FrameworkServiceExtension.UseWtmContext` 加上 `IsFilePublic==true` 非 Development 環境的 `LogCritical`（比照既有 `IsQuickDebug` 守門模式，但只 log 不 throw——`IsFilePublic` 有正當用途，可能是操作者刻意開啟）；template 半把三份 demo appsettings.json 的 `IsFilePublic` 改回 `false` 並加註說明。翻轉前的四項覆核：**(1) NULL-tenant 舊檔案**——沿用 #815 已記錄的決定（見上面「#815 NULL-tenant 檔案的第二個窄化」列）：EF 對 nullable 欄位的 `==` 轉譯是 null-safe，`TenantCode == null` 的檔案只有「呼叫者自己的 tenant 也是 null」才能解析到；這是 #815 已經在 `DeleteFileTenantScoped` 上採用的既定先例，`GetFile` 翻轉後沿用同一條規則，不另開窄化的例外——放寬等於重開 #815 關掉的同一個 primitive。**(2) 每個 `Upload(` call site 是否都蓋了 `TenantCode`**——逐一檢查 `src/`（`_FrameworkController.cs` 四處、`BaseImportVM.cs`、`WtmFileProvider.Upload`）與 `demo/`（三份 `FileApiController.cs`、`ConsoleDemo`）呼叫點：全部經過 `WtmFileProvider.Upload`（非 DB handler 分支自己 `new FileAttachment` 時蓋）或 `WtmDataBaseFileHandler.UploadToDB`（database 模式自己蓋），兩處都執行 `file.TenantCode = _wtm.LoginUserInfo?.CurrentTenant;`——沒有找到漏蓋的路徑。**(3) 背景/無身分路徑**——`GetFile(` 的呼叫點全部落在 `_FrameworkController`/demo `FileApiController`（HTTP request-scoped）與 `BaseCRUDVM`/`BaseImportVM`（同樣掛在 request-scoped 的匯入流程上）；`grep BackgroundService/IHostedService` 找到的六個背景服務（`EtlHostedService`、`DashboardSnapshotHostedService`、`DashboardAlertHostedService`、`ActionLogRetentionService`、`RefreshTokenRetentionService`、`WorkflowTimerHostedService`）沒有一個呼叫 `WtmFileProvider.GetFile`——翻轉不會讓任何現有背景路徑讀不到檔案。註：#843 談的是同一個「`LoginUserInfo==null` 時怎麼辦」根因家族，但方向相反且機制不同——`ApplyDataPrivilegeForAnalysis` 對無身分 fail-*open*（不過濾，過度可見），`GetFile` 的 tenant query filter 對無身分 fail-*closed*（只解析 `TenantCode` 也是 null 的列，看不到其他租戶）；#843 於本表下一列修復（fail-closed by default），不影響、也不被本修法影響。**(4) `HasMainHost`**——demo appsettings.json 的 `mainhost.Address` 預設整行被註解掉，`Configs.HasMainHost` 因此預設 `false`；`GetUserPhoto` 的 `HasMainHost && CurrentTenant==null` redirect 是路由層邏輯，發生在呼叫 `GetFile`之前，且兩端（上傳與讀取）在同一台 mainhost 節點上都用同一個 `LoginUserInfo?.CurrentTenant`（mainhost 自己的使用者通常也是 null tenant），翻轉後行為一致，未發現互相干擾。**結論：翻轉可以安全進行，四項覆核均未發現阻擋理由**，唯一需要記錄的行為改變是：一個真正打算跨租戶公開、但上傳時蓋了非 null `TenantCode` 的「公開」檔案，翻轉後對匿名呼叫者不再可解析（除非透過 Referer-based tenant 路由巧合命中同一租戶）——這類檔案需改用 null-tenant/main-host 情境上傳，或另建專用公開檔案儲存區。**寫驗收測試時另外發現一個相鄰但獨立的 bug**：`WtmDataBaseFileHandler.GetFileData`（`database` SaveMode 專用）自己又下了一次「不受 `EnforceTenantFileScope` 控制、永遠套用」的 tenant-scoped query，跟外層 `WtmFileProvider.GetFile` 的（依旗標決定要不要套用租戶過濾的）查詢結果不一致時——也就是 `EnforceTenantFileScope=false` 且真的跨租戶讀取時——不是洩漏也不是乾淨拒絕，而是回傳 `null` DataStream 讓 controller 端丟未捕捉的 `NullReferenceException`（表現成 HTTP 400）。`WtmLocalFileHandler`/`WtmOssFileHandler` 沒有這層多餘過濾。這使得本次修法自己文件裡承諾的「`EnforceTenantFileScope=false` 這個顯式 opt-out 仍然有效」對 `database` SaveMode 不成立，因此在本 PR 一併修掉（`GetFileData` 改用 `IgnoreQueryFilters()`，理由：全庫只有 `WtmFileProvider.GetFile` 這一個呼叫點，執行到這裡時外層授權判斷早已完成，不是新開的洞）。 |
| #843 —— `ApplyDataPrivilegeForAnalysis` 對背景執行 fail-open，改為預設 fail-closed（P1，BREAKING for 一個窄範圍） | `DCExtension.ApplyDataPrivilegeForAnalysis`（`DCExtension.cs:295-300`）在 `WTMContext.LoginUserInfo == null` 時直接跳過列級 DataPrivilege 過濾、回傳未過濾查詢。背景執行（`DashboardSnapshotJob`、`DashboardAlertHostedService`，都透過 `AnalysisWidgetDataSource.GetDataAsync` 的 `_serviceProvider.CreateScope()` 拿到沒有 `HttpContext` 的 `WTMContext`）因此永遠 `LoginUserInfo == null`——寫入時（互動式 dashboard-designer）受 `CanAccess` 與 DataPrivilege 約束的 widget 設定，背景重跑時完全不受限。**身分裁決**：評估三個選項——job 建立者身分（否決：帳號停用/權限變動後仍沿用舊權限，且需要重建一份不存在的 `LoginUserInfo` 快照，風險/工作量超出 P1 修復比例）、租戶系統身分（否決：本框架目前沒有這種帳號類型，需要新基礎設施）、**顯式宣告的系統查詢（採用）**：`LoginUserInfo == null` 預設改為 fail-closed（沿用 `AppendSelfDPWhere` 既有的 `dps==null → 1!=1` 邏輯，範圍限定在「該 model 有設定 DataPrivilege 規則」），新增 `declaredSystemQuery: true` 具名參數作為唯一、review 可見的例外通道（非設定檔旗標，對已認證呼叫者無效）。兩個生產呼叫點（`_AnalysisController`、`AnalysisWidgetDataSource`）都不傳 `true`——**立即以 fail-closed 出貨，不是預設關的 opt-in 旗標**（不重蹈 `UseSelectIslandRender`/四個 `Enforce*` 旗標的覆轍）。**隨手修的相鄰缺口**：`AnalysisWidgetDataSource` 的 `WTMContext.DC` 透過 `WTMContext.CreateDC()` 建立，其 `TenantCode` 只認 `LoginUserInfo.CurrentTenant`（背景執行永遠 null）——EF 全域 `ITenant` filter 因此把背景查詢限定在「`TenantCode` 也是 null 的列」，不是「所有租戶」也不是「這個 widget 自己的租戶」，是 #832（ETL 排程器同款 `CreateDC` 缺口，仍是獨立 open issue，未受本修法影響）的 Dashboard 同構體。修法：`WidgetDataRequest.TenantId`（原本存在卻從未被填的欄位）現在由 `EfCoreDashboardService`/`JsonFileDashboardService` 填入，`AnalysisWidgetDataSource` 僅在 `LoginUserInfo == null` 時透過 `IWtmDataContextFactory.CreateDC(currentTenant:)`（`WorkflowEngine`/`WorkflowTimerHostedService` 已在用的同款無 HttpContext 模式）明確建立租戶範圍的 DataContext——互動式 HTTP 路徑（一律有 `LoginUserInfo`）不受影響。**blast radius**：全庫僅兩個 `ApplyDataPrivilegeForAnalysis` 呼叫點（`grep -rn "ApplyDataPrivilegeForAnalysis" src/` 驗證），只有背景執行且底層 model 有設定 DataPrivilege 規則的 widget 會從「回傳全部資料」變成「回傳空結果」；沒有設定 DataPrivilege 規則的 widget 不受影響（且因租戶範圍修復而變得**更正確**，不是新增風險）。沒有設定檔可以恢復舊的 fail-open 行為——要重新開放特定查詢，需要在呼叫端加一行看得到的 `declaredSystemQuery: true`。**可觀測性**：fail-closed 預設本身是靜默的——背景 widget 變空、operator 卻看不到任何連到這個原因的訊號。修法時一併補上：每當這個方法因「無身分＋該 model 有設定 DataPrivilege 規則」而實際拒絕時，透過 `CoreProgram.GetLogger("DCExtension")`（`DCExtension` 是 static class 沒有 DI，沿用 `WtmFileProvider` 既有的 static-helper logging 模式）記一筆 `LogWarning`，訊息含 element type 名稱與補救方式（`declaredSystemQuery: true`）——**搜尋 log 關鍵字 `ApplyDataPrivilegeForAnalysis denied all rows for`** 即可定位。**節流**：每個 element type 在單一 process 生命週期只記一次（`ConcurrentDictionary` 守衛）——這是排程 job 可能每幾分鐘重跑一次的查詢路徑，逐次呼叫都記會洗版；沒有現成的「job run」邊界可用（那個邊界在呼叫端好幾層之上，且此 helper 與一律已認證的 `_AnalysisController` 共用，往下傳 job id 會擴大這個共用 static helper 的介面），process 重啟後節流自然重置，不會永久沉默。驗收：`DPWhereInMemoryTests` 新增 5 支測試涵蓋 warning 觸發、節流（3 次呼叫只記 1 次）、以及 3 種不該記的情況（無規則、`declaredSystemQuery: true`、已認證使用者自己既有的零權限拒絕）。驗收：`DPWhereInMemoryTests.ApplyDataPrivilegeForAnalysisTests`（fail-closed 預設、無規則 model 不受影響、escape hatch opt-in 且對已認證呼叫者無效）＋`DashboardBackgroundTenantIsolationTests843.cs`（真實背景路徑：兩租戶經 `MultiTenantSeedFixtureTests`/`DbTestHelpers` 模式 seed，`AnalysisWidgetDataSource` 從 demo app 真實 DI container 解析、無 `HttpContext`，斷言租戶 A 的 job 看得到自己的列但看不到租戶 B 的；停用租戶範圍修法後手動驗證此測試真的會變紅，SQL log 顯示回退成 `WHERE TenantCode IS NULL`）＋`JsonFileDashboardServiceTests`/`EfCoreDashboardServiceTests` 各補一支 `TenantId` 橋接測試＋`test/mutants/entries/dcext843-declaredsystemquery-guard-neutralize.json`（`VERDICT: KILLED`）。版本翻升至 10.20.0（minor）。 |
| #828 —— 批次解析例外被當成「都不存在」，依賴失效可能靜默刪光既有子列 | `ResolveFileAttachmentIdsForCaller`/`Async`（`src/WalkingTec.Mvvm.Core/BaseCRUDVM.cs`）原本把批次解析查詢的任何例外都吞掉，回傳空集合——跟「查詢成功、但這些 id 真的都不存在」在型別上無法區分。`ApplyFileAttachmentResolution` 對兩者套用同一套逐項窄化規則（#815 第四／六／七輪：純量 FK 還原、`ISubFile` 子項 drop-or-restore）；當一個 posted 的 `ISubFile` 集合裡每一項恰好都是候選 id（一般情況——編輯一個實體時通常會把沒動過的既有子項也一起 re-post），例外就會讓整個 posted 集合被清空，直接落入 `DoEditPreparePart2` 的「空集合」分支：**該 parent 的所有既有 child row 被物理刪除，`DoEdit` 仍回報成功**。觸發不需要攻擊者：SQL Server 每查詢 2100 個參數的上限、逾時、連線瞬斷都能讓這條查詢丟例外。**先查證再修**：issue 原文主張「大量 `Contains` 是最直接路徑」，但 EF Core 8/9 早就把這類查詢改成單一 JSON 參數＋`OPENJSON`，理論上不會撞到參數上限——查證後發現 **EF Core 10（本 repo 釘住的 10.0.9，見 `Directory.Packages.props`）把預設翻譯策略改回「每個候選 id 各自一個 SQL 純量參數」**（EF 官方認定 OPENJSON 形式在部分真實 workload 上查詢計畫變差，10.0 才走回頭路），所以 2100 上限在**這個 repo 的這個版本**上是真實會撞到的，但在 EF Core 8/9 上就不會——claim 依版本而定，不能照抄。修法：`ResolveFileAttachmentIdsForCaller` 回傳結果新增 `Succeeded` 旗標；解析查詢本身失敗時，`RejectUnresolvableFileAttachmentReferences`/`Async` 直接拒絕整個請求（`MSD.AddModelError`、不 staging、不呼叫 `SaveChanges`）——跟既有的「必要 FK 無合法舊值」路徑（#815 第六／七輪）同一套「整請求拒絕」contract，不再把模糊不清的空集合交給逐項窄化邏輯處理；非例外路徑（純量還原／子項 drop-or-restore／必要 FK 拒絕）完全不變。**加上、但不是取代**：解析查詢額外拆成 500 筆一批（`FileAttachmentResolutionBatchSize`），讓一個夠大但完全合法的表單（`FormOptions.ValueCountLimit` 是 5000）不會日常性撞到參數上限——分批失敗一樣走上面的整請求拒絕，不會被窄化。沒有動任何 config 預設值、沒有動任何已存資料的形狀，不需要 migration；唯一的可觀察行為改變只發生在原本就有 bug 的那條路徑上：解析失敗現在會變成請求層級的驗證錯誤，而不是靜默的部分刪除＋回報成功。驗收：`FileAttachmentResolutionFailureGateTests828.cs`（FK-enforcing SQLite `ProductSubFileContext` fixture，`DbCommandInterceptor` 模擬解析查詢失敗，斷言既有子列存活＋caller 收到失敗，正控組同方法內證明正常路徑仍可存檔）＋`test/mutants/entries/fileattach828-resolution-failure-guard-neutralize.json`（`VERDICT: KILLED`）。 |
| #875 —— #828 只補了「隔壁一個函式」，同一缺陷在 `LoadExistingSubItemFileIds`（以及第三個窮舉才挖出的 `LoadEntitySnapshot`/`Async`）原封不動 | issue 明確要求動工前先交窮舉表：對 `BaseCRUDVM.cs` 每一個 catch-and-continue 的 DB 查詢，列出「catch 回傳什麼／呼叫端怎麼解讀／是否可達刪除分支」。窮舉（`grep -n 'catch'` 全部 20 個 catch block 逐一分類，非重新用先前 scoping）結果：(1) `ResolveFileAttachmentIdsForCaller`/`Async`——#828 已修，`Succeeded` 旗標擋住。(2) `LoadExistingSubItemFileIds`（`:2396` 附近，issue 點名的那個）——catch 只 log，落到 `return result`（空或部分累積的 dict），呼叫端 `ApplyFileAttachmentResolution` 把這個「查詢失敗」跟「查詢成功、確認沒有既有列」當同一件事，於是該 drop 的邏輯照跑，可達 `DoEditPreparePart2` 的空集合刪除分支——**本 issue 的主要修復目標**。(3) **窮舉意外挖出的第三個站點**：`LoadEntitySnapshot`/`LoadEntitySnapshotAsync`（`DoEdit`/`DoEditAsync`/`DoDelete`/`DoDeleteAsync` 唯一的 `preSaveSnapshot` 來源）——catch 回傳裸 `null`，跟「`Entity.ID` 未設定」「列真的被同時刪除」這兩個**合法**的 null 案例在型別上完全無法區分。`ApplyFileAttachmentResolution` 把 `preSaveSnapshot == null` 讀成「這是 Add，沒有既存狀態」，因而**整段跳過** `LoadExistingSubItemFileIds` 的 restore-vs-drop 判斷（`preSaveSnapshot != null ? ... : []` 這行本身）——即使 `LoadExistingSubItemFileIds` 自己這次沒有出錯也一樣，因為根本沒被呼叫。也就是說：即使只修了 (2)，一個真正的 Edit 只要在讀 snapshot 這一步撞到暫時性 DB 故障，就會被錯當成 Add，同樣的既有子項照樣被 drop、照樣可能清空整個 posted 集合、照樣觸發刪光。(4) `DoEdit`/`DoEditAsync`/`DoDelete`/`DoDeleteAsync` 的 `SaveChanges`(`Async`) catch——用 `saved`/例外本身直接短路，不呼叫任何後續刪除邏輯，是既有的正確 fail-closed 模式，非缺陷。(5) `DoRealDelete`/`DoRealDeleteAsync`——查詢（`Include`+`FirstOrDefault`）跟 `SaveChanges`/`DeleteEntity` 包在**同一個** try 裡，查詢丟例外時 `SaveChanges` 根本不會被呼叫到，同樣 fail-closed，非缺陷。(6) `DoEditPreparePart2`/`DoAddPrepareCore`/`SerializeScalarProps` 裡其餘的 catch 全部包的是 reflection `SetValue`/`GetValue`/JSON 序列化，不是 DB 往返，排除在表外並在 PR 說明逐一點名理由。**沒有分類到的列**：`AppendEditChangeLog`（`_FrameworkController.UpdateModelProperty` 專用，`#797` 讓它刻意跳過 `DoEditPrepare`）也呼叫 `LoadEntitySnapshot`，但這條路徑從不觸及 `ApplyFileAttachmentResolution`，failure 只會讓該次 ChangeLog 的 `OldValues` 缺失——維持 best-effort，不整請求拒絕。async 雙生法（`LoadEntitySnapshotAsync`/`ResolveFileAttachmentIdsForCallerAsync`）、grandchild 巢狀集合（本類別本來就只處理 TModel 自身一層 `List<T>`，無遞迴）、reflection 間接呼叫（`BasePagedListVM.UpdateEntityList`、`BaseBatchVM.DoBatchEdit(Async)`、`BaseImportVM.BatchSaveData` 從未呼叫 `DoAddPrepare`/`DoEditPrepare`，屬於既有、範圍外的 #824 追蹤項，不在本檔案內）均已確認涵蓋或明確排除，非本輪遺漏。修法：沿用 #828 已建立的 contract，不另發明——`LoadExistingSubItemFileIds` 回傳新增 `Succeeded` 旗標（`ExistingSubItemLookupResult`），例外時捨棄任何部分累積的結果；`LoadEntitySnapshot`/`Async` 同樣回傳 `EntitySnapshotResult(Succeeded, Snapshot)`，`DoEdit`/`DoEditAsync`/`DoDelete`/`DoDeleteAsync` 在 `Succeeded == false` 時直接 `MSD.AddModelError` 並 return，不呼叫 `DoEditPrepare`／不 `SaveChanges`。**分批**：`LoadExistingSubItemFileIds` 的查詢跟 #828 的解析查詢一樣是 `Contains()`-shaped、id 數量最壞情況可達整個 posted 子集合，同樣會撞 EF Core 10 的 2100 參數上限，因此套用相同的 `FileAttachmentResolutionBatchSize`（500）分批，理由與 #828 完全相同。**已知代價（誠實記錄，非隱藏）**：`LoadEntitySnapshot` 失敗現在會讓 Edit/Delete 整請求失敗，即使該 TModel 完全沒有 `FileAttachment`/`ISubFile` 屬性——先前這種情況會靜默用 null snapshot 繼續（只是 ChangeLog `OldValues` 跟 `DeletedFileIds` 清理失效），現在會直接回報失敗。這是刻意選擇：與其為「這個 TModel 有沒有 file 屬性」另開一條分支邏輯（等於重新發明第二套 contract），不如比照 #828 的「查詢失敗一律整請求拒絕」統一規則，範圍不變。沒有動任何 config 預設值、沒有動任何已存資料的形狀，不需要 migration。驗收：`ExistingSubItemLookupFailureGateTests875.cs`（sync/async 既有子項 lookup 失敗 + 正控組 + 500 上限分批迴歸）與 `EntitySnapshotLoadFailureGateTests875.cs`（sync/async 第三站點 + 正控組），均為 FK-enforcing SQLite `ProductSubFileContext` fixture＋`DbCommandInterceptor` 精準鎖定各自的目標查詢（不同資料表名稱互不干擾）；`test/mutants/entries/existingsubitem875-lookup-failure-guard-neutralize.json`（`VERDICT: KILLED`）。 |

---

## Production 補強清單

若你決定上 production，按優先順序執行：

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
- 外部 SaaS、有付費客戶 → **會，但會走補強清單 1-4 全做**
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
