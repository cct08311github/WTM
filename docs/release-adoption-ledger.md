# Release Adoption Ledger

> **狀態**：第一版，手動維護。**最後全面驗證**：2026-07-30。
> **追蹤**：#919。
> **目的**：這份文件回答一個 `CHANGELOG.md`／`production-readiness.md` 都沒有回答的問題——**「修好了」的東西，有沒有任何一個正在服務流量的系統真的擋得住？**

## 為什麼需要這份文件

這個專案一直用 PR 數、release 數、issue 數衡量進展，而這三個數字量的是**「修好了」**，不是**「保護到了」**。`repo pin`（下游 `Directory.Packages.props` 寫的版本）、`staging sign-off`（下游驗證環境跑過的版本）、`production deployment`（真正服務流量的版本）是三件不同的事，且三者可以同時存在巨大落差——本文件記錄目前已知最完整的下游採用個案（BMS）證實了這一點：**BMS production 目前的最佳證據指向 WTM 8.x 時代，而 WTM 未發布 HEAD 已經是 10.21.0。** 2026-07-29/30 合併的一整批安全修復（含一個 P0），沒有一項被證明保護到任何正在服務流量的系統。

這不是說這些修復沒有價值——它們對「將來會升級的 BMS」以及對「其他任何已經在 10.17.0+ 的下游」都有價值。但把「修好了」和「保護到了」混為一談，會讓人誤判目前的實際風險暴露面。

## 這份文件不是什麼

- 不是自動化儀表板。**目前沒有任何自動化方式可以查詢 BMS production 實際跑的版本／SHA**——這個事實本身就是本文件要記錄的發現之一（見 §7）。
- 不是 BMS 的安全稽核。本文件只讀 BMS repo 取得可達性分類所需的事實（套件 pin、模組引用、middleware 行為），**不修改 BMS 任何檔案**，也不對 BMS 的整體安全姿態下結論。BMS 側若需要配合動作（例如提供 production 版本查詢方式），應在 BMS repo 另開 issue。
- 不宣稱分類完整。凡是查不到來源或無法在合理時間內驗證的項目，一律標記「不確定」，不用猜測湊滿表格——猜測比空白更危險。

---

## 1. BMS 版本分層（repo pin ≠ staging ≠ production）

| 層級 | 版本 | 來源 | 最後確認日期 | 這是不是即時查詢？ |
|---|---|---|---|---|
| **BMS repo pin**（`Directory.Packages.props`） | `WalkingTec.Mvvm.Core`/`Mvc`/`TagHelpers.LayUI` = **10.16.1** | `BMS/Directory.Packages.props:66-68`（`grep -n WalkingTec BMS/Directory.Packages.props`） | 2026-07-30 | 是——這是原始碼裡的靜態宣告，讀了就準 |
| **BMS staging（其內部文件稱「VM」／「verify VM」）** | 已驗證跑過 **10.16.1**（Phase 26） | `BMS/doc/MIGRATION_LOG.md`「Phase 26：WTM 10.14.5 → 10.16.1 升級（2026-07-20）」——「VM 部署 10.16.1 後全套真機 e2e 8 批 644 passed」 | 2026-07-20（該 Phase 記錄日期） | 否——是升級時的人工記錄，非持續查詢 |
| **BMS production** | **WTM 8.x（很可能是 8.6.1）**；**沒有比這更精確的已知證據** | 見下方 §2 的完整證據鏈 | 2026-07-30（今天的間接證據；沒有直接查詢） | **否——目前沒有任何自動化或即時方式可以確認** |

**這三層目前差兩個大版本世代（8.x → 10.16.1 pin → 10.21.0 未發布 HEAD）。** repo pin 落後 HEAD 5 個 minor（10.17.0–10.21.0 全部未拉取），而 production 落後 repo pin 本身一整個 major 世代的升級都還沒部署。

### BMS staging 這個詞的重要澄清

BMS 內部文件用「VM」／「verify VM」指稱**驗證環境**，和「prod」是明確分開的兩個詞——這不是本文件的推論，是 BMS 自己文件的用字：

> `BMS/doc/MIGRATION_LOG.md`（10.14.0→10.14.1 phase）：「**運維**：staging 已跑 10.14.1；VM 備份保留。**prod 部署行為與 10.14.0 相同**（...；**prod 仍 8.x、整個 8→10 尚未部署**）。」

同一份文件在後續每一個 Phase（10.14.2 直到 Phase 26 的 10.16.1）都重複這個模式：**verify VM 上跑到最新，prod 仍停在 8.x**。沒有一個 Phase 記錄「production 已切換」。

---

## 2. BMS production 版本——證據鏈與其邊界

### 證據 1：BMS commit `4b1c2ba`（2026-07-30，今天）

```
refactor(finance)!: 金融數值欄位 decimal → double 回退對齊 prod 8.x（含對抗性驗證） (#316)

生產原始碼一直是 double（初始 commit e3ba15f 起全樹零個 decimal），是 aa47cbb 後
才遷成 decimal 並附帶一份從未執行的 76 條 ALTER TABLE。回退後 model 型別與
prod DB 的 float 一致，消掉部署 10.x 前的 DDL 前置步驟。
```

驗證命令：`cd BMS && git log -1 4b1c2ba --format='%H%n%an%n%ad%n%s%n%n%b'`

**這條證據建立的事實**：commit 作者（BMS 維護者本人）在今天的時間點，仍以「production 是 double、10.x 部署尚未發生」為前提做架構決策。「消掉部署 10.x 前的 DDL 前置步驟」這個措辭本身預設了 10.x 部署**還沒發生**（否則不會有「部署前」這個步驟需要消除）。

**這條證據不能建立的事實**：這是一則 commit message，不是部署記錄。它證明了「commit 作者相信 production 是 8.x」，不證明「production 現在確實是 8.x」——commit 作者可能記錄過時、可能簡化了實際情況。**一則 commit message 不是權威來源。**

### 證據 2：BMS `doc/plans/wtm-8to10-prod-readiness-audit-2026-06-22.md`（獨立於證據 1，早 5 週）

標題與範圍聲明：

```
# WTM 8.6.1 → 10.13.7 生產部署前全面對抗驗證報告
> 日期：2026-06-22｜範圍：prod（WTM 8.6.1）→ master（WTM 10.13.7）整個 8→10 升級的部署前回歸稽核
```

這份文件明確給出了比 commit message 更精確的版本號——**8.6.1**，而非泛稱的「8.x」——並且明確記錄了一個**部署前必修的 CRITICAL blocker**（`ChangeLogs` 表缺失，需要 DBA 先套用 DDL），以及兩項仍列為 backlog 的 finding（#133、#139）。

**這條證據建立的事實**：截至 2026-06-22，BMS 自己的稽核文件把 production 標記為 WTM 8.6.1，並把 10.13.7（當時的 master pin）列為**尚未部署**的目標，附帶一個明確的部署阻斷項。

**這條證據不能建立的事實**：這是稽核**計畫**文件，不是部署後確認文件。它證明了 2026-06-22 當時的狀態評估，不證明之後是否已經部署（雖然證據 1 的日期更新的措辭與此一致）。

### 證據 3：`MIGRATION_LOG.md`——一個資料點，不是逐 Phase 的持續重申

驗證命令：`grep -n "prod 仍 8.x\|prod 部署" BMS/doc/MIGRATION_LOG.md`

這個命令回傳 **3 筆**，不是「每個 Phase 都有」：
- Phase 16（10.13.7→10.13.8，2026-06-22）——「prod 部署前的標準 8→10 真機 e2e 涵蓋」，早於下面說的範圍起點，且沒有直接斷言 prod 版本。
- Phase 22（10.14.0，2026-07-06）——「prod 部署（8→10）到時」（未來式），是範圍起點（10.14.0→10.14.1）**之前**那一個 Phase。
- Phase 23（10.14.0→10.14.1，2026-07-06）——「prod 部署行為與 10.14.0 相同…prod 仍 8.x、整個 8→10 尚未部署」，是三筆裡**唯一**落在「10.14.0→10.14.1 到 Phase 26」這個範圍內的一筆，已在 §1（line 35）直接引用。

**這條證據建立的事實**：`MIGRATION_LOG.md` 對「prod 仍 8.x」最新一筆明確重申，日期是 **2026-07-06（Phase 23）**——不是 2026-07-20（Phase 26）。

**這條證據不能建立的事實**：不能說這份文件「逐 Phase 一路重申到 Phase 26」。Phase 24（10.14.1→10.14.2，2026-07-06）、Phase 25（2026-07-11）、Phase 26（2026-07-20）三個較新的 Phase **都沒有**再出現這兩個詞組——不代表狀態變了，只是這幾個 Phase 的運維段落沒有重複寫這句話（純 bump/patch，內容著重在別的驗證項目）。§2 對「production 目前仍是 8.x」的整體結論主要靠證據 1（今天的 commit）與證據 2（獨立於此、早 5 週的稽核文件）撐住；這條證據只補上「截至 2026-07-06，`MIGRATION_LOG.md` 本身也還在說同一件事」這一個資料點，不是持續到今天的逐期佐證。

### 三條證據合起來，以及仍然不知道的事

三條證據互相獨立（不同文件類型、相隔 5 週以上、不同作者情境）且結論一致：**BMS production 尚未完成 8→10 切換，最後一個已知精確版本號是 8.6.1（2026-06-22）。** 這是目前最好的可得證據。

**但這三條證據沒有一條是即時查詢或部署記錄**——沒有任何一條等同於「打一次 API 拿到現在 production 回應的版本號」。三條證據存在盲區：介於 2026-06-22 與今天之間，理論上不無可能發生一次未被寫進這兩份文件的緊急部署（無論方向）。**這正是本文件要記錄的核心發現：這個專案沒有任何機制可以排除這種可能性。**

**Unknown（誠實標記，不是待補欄位）**：
- BMS production 現在的確切 WTM 版本／commit SHA——不知道，也沒有方法查。
- BMS production 是否仍是 8.6.1，或是 8.x 系列的其他 patch——不知道。
- 若 8→10 切換已在 2026-06-22 之後、今天之前完成而未被記錄——不知道，無法排除。

---

## 3. 可達性分類——WTM 10.17.0 → 10.21.0（未發布）的每一項安全修復

**方法**：清單取自 `CHANGELOG.md` 的 `### Security` 段落（10.17.0 沒有 `### Security` 段落——該版是純可靠性／欄位回饋修復，非標記安全修復，故不計入本表）。分類依 BMS 實際程式碼／設定驗證，不是猜測；每列附驗證命令。三個分類桶取自 issue 原文，另外標出兩個不完全落入這三桶的案例（不硬塞）。

**路徑基準**：本文件所有 `BMS/...` 路徑皆相對於 BMS repo 的唯讀 sibling checkout 根目錄。該 solution 的 ASP.NET Core web 專案子目錄與 repo 本身同名（`BMS`），所以落在該專案內的檔案會呈現雙層前綴（例如 `BMS/BMS/Areas/...`、`BMS/BMS/Infrastructure/...`），而落在 repo 根目錄的檔案（例如 `Directory.Packages.props`、`doc/MIGRATION_LOG.md`）只有單層 `BMS/...` 前綴。

**分類桶**：
- **NuGet-reachable**：升級套件即得，不需下游改任何檔案
- **需要 copied-template migration**：下游要改自己複製走的檔案（scaffold-copy 問題，#833）
- **對 BMS 零可達**：模組未引用，或端點被 BMS 自己的 middleware／設定擋下
- **（額外，不硬塞進三桶）Opt-in 但未接線**：套件出貨了新的擴充點，但預設不生效，下游要主動接線才有效果——這不是 template 複製問題，是全新功能的採用問題
- **（額外）不適用**：BMS 的架構形狀（UI 技術棧、既有 fork 修復）讓這項修復的問題本來就不存在

| # | 修了什麼 | Fixed in | 分類 | BMS 證據 | 驗證日期 |
|---|---|---|---|---|---|
| #788 | `System.Security.Cryptography.Xml` 10.0.9→10.0.10（5 個 HIGH CVE） | 10.18.0 | **NuGet-reachable** | 純傳遞相依版本 bump，隨套件升級自動取得 | 2026-07-30 |
| #776 | 隨附 layui 2.6.3 `data-content` 屬性 XSS（僅 `Layui:Asset=legacy` 時可達） | 10.18.0 | **需要 template migration（不確定是否適用）** | layui 是 vendor 進 `wwwroot` 的資產，非套件出貨；BMS 用**自 fork** `_Layout.cshtml`/`Login.cshtml` 切換資產樹（`Directory.Packages.props` #243 註解），未呼叫 WTM 的 `LayuiAssets` helper——**BMS 自己 vendor 的 layui.js 副本是否含相同的未跳脫寫法，未實際 diff 確認** | 2026-07-30（分類本身未完全驗證，如實標記） |
| #867 | `RedoUpdateModel` 任意 dotted-path 反射寫入可達 DI singleton（`ConfigInfo.IsQuickDebug=true` 等）；`EnforceRequestBindingScope` 預設 `true` | Unreleased（10.19.0 週期內） | **NuGet-reachable** | 修復點在 `BaseController`/`BaseApiController`（Core/Mvc 套件本體），受影響的五個呼叫點（`Selector`/`GetPagingData`/`GetExportExcel`/`GetExportExcelStream`/`DoImport`）**沒有一個**在 BMS 的 `BlockedFrameworkEndpointsMiddleware` 阻擋清單裡（該清單只擋 `/_Framework/UpdateModelProperty`） | 2026-07-30 |
| #827 | `_FrameworkController` 五個授權 hook 在正式路由上不可達；新增 `IWtmFrameworkEndpointAuthorizer` DI seam | Unreleased | **Opt-in 但未接線** | `grep -rn IWtmFrameworkEndpointAuthorizer BMS` 回傳 0——BMS 未註冊任何 policy。套件升級會把這個擴充點帶進來，但**預設不生效**（`Inherit` 語意），下游要自己寫一個實作並呼叫 `AddWtmFrameworkEndpointAuthorizer<T>()` 才會改變任何行為 | 2026-07-30 |
| #829 | 四個 VM-name 驅動的 `_FrameworkController` 端點在授權**之前**就建構呼叫者指定的 VM | Unreleased | **Opt-in 但未接線** | 修復點確實直接改在 `_FrameworkController` 本體（`GetExportExcel`/`GetExportExcelStream`/`GetExcelTemplate`/`GetDeletePreview`），無獨立 opt-in 旗標——但它保護的是「`CanExportVm`/`CanPreviewDelete` 判定 Deny 時，VM 不會在判定前就被建構」；這個判定本身在 BMS 現況下**恆為 Allow**：四個 `Enforce*Authorization` 旗標在 BMS 這個套件版本裡不存在（見 §4），且 `grep -rn IWtmFrameworkEndpointAuthorizer BMS` 回傳 0（同 #827 的證據）。換言之 #829 修的是「deny 路徑的副作用時序」，但 BMS 目前完全沒有會觸發 deny 的路徑——要讓 #829 對 BMS 產生實際保護，下游得先做 #827 那個相同的 opt-in（註冊 authorizer 並回傳 Deny，或升到旗標存在的版本並手動打開），不是單純升級套件就會變的行為 | 2026-07-30 |
| #841 / #862 | ETL 實體缺 `ITenant`＋根因 wiring 缺陷（`EtlJobDefinition` 過濾器從未生效過） | Unreleased | **對 BMS 零可達** | `BMS/Directory.Packages.props` 只 pin `Core`/`Mvc`/`TagHelpers.LayUI`；`grep -n '<PackageVersion Include="WalkingTec.Mvvm.Etl' BMS/Directory.Packages.props` 零結果（**注意**：`grep -n WalkingTec.Mvvm.Etl BMS/Directory.Packages.props` 這個較寬鬆的寫法不是零結果——它會命中 #79 註解本身那句「BMS 不引用 WalkingTec.Mvvm.Etl package」，因為註解文字包含這個套件名稱；上面限定 `<PackageVersion Include=` 的寫法才是實際確認零 pin 的命令） | 2026-07-30 |
| #876 | `WtmControllerActivator`：所有 controller 建構時就填 `Wtm`，修正 ETL controller 的 filter-order 缺陷 | Unreleased | **對 BMS 目前零實際效果**（機制隨 NuGet 出貨，但無對應症狀可修） | 該 bug 的前提是「某 controller 在自己的 `OnActionExecuting` 覆寫裡直接讀 `Wtm`」——`grep -rln OnActionExecuting BMS/BMS --include=*.cs` 找不到任何一個 BMS 自己的 controller 這樣做；且 BMS 不引用 Etl 組件（唯一實際踩到這個 bug 的組件）。修復機制（`WtmControllerActivator`，註冊在 `AddWtmContext`）會隨套件升級自動套用，屬防禦縱深，但目前沒有已知的 BMS 症狀可以被它修好 | 2026-07-30 |
| #883 | `EtlSchedulerService` HTTP 可達方法的跨租戶 IDOR | Unreleased | **對 BMS 零可達** | 同 #841/#862——Etl 組件未被引用 | 2026-07-30 |
| #843 | `DCExtension.ApplyDataPrivilegeForAnalysis` 背景執行 fail-open→fail-closed（Dashboard widget 背景執行的資料列權限） | Unreleased（10.20.0） | **對 BMS 零可達** | 直接對 BMS 重跑驗證命令（非引用 #79 那則 2026-06-07、談的是另一次 10.5.5→10.6.0 升級的舊註解）：`grep -rEn 'AnalysisWidget\|_AnalysisController\|ApplyDataPrivilegeForAnalysis' --include=*.cs --include=*.cshtml BMS` 與 `grep -rEn 'DashboardWidget\|framework_dashboard' --include=*.cs --include=*.cshtml BMS` 皆零結果——兩個生產呼叫點（`_AnalysisController`、`AnalysisWidgetDataSource`）與 Dashboard widget 機制 BMS 都不會執行到 | 2026-07-30（本列命令今天重新對 BMS 實跑，非沿用舊註解日期） |
| #799 | `FrameworkFilter.OnResultExecuted` 的 `ViewDivId` XSS | Unreleased | **NuGet-reachable** | 修復點在 `FrameworkFilter`（Mvc 套件全域 filter），對每個 `BaseVM` 模型的 `PartialViewResult` 都跑，無 opt-in 旗標 | 2026-07-30 |
| #859 | `_Framework/GetFile`/`ViewFile` 未認證跨租戶讀取；`EnforceTenantFileScope` 預設 `true` | Unreleased（10.19.0） | **不適用（兩半皆是）** | 樣板半：demo `appsettings.json` 的 `IsFilePublic` 改回 `false` 只影響**新** scaffold，對 BMS 既有設定無回溯效果——但 BMS 自己的 `appsettings.json:90` 本來就是 `"IsFilePublic": false`，故這半對 BMS 是 moot。套件半：`EnforceTenantFileScope` 預設轉 `true` 讓 `WtmFileProvider` 套用 `ITenant` filter，但 §4 已記錄 BMS `EnableTenant=false`（`appsettings.json:59`，單租戶部署）——BMS 全程式碼庫沒有任何地方把 `FileAttachment` 寫入不同的 `TenantCode`，所以這個過濾器套上去等於 `TenantCode == null` 比對到全部既有列，不排除任何東西。原本標「NuGet-reachable（套件半）」誤把「套件會自動套用這個過濾器」等同於「這個過濾器對 BMS 有實際隔離效果」——單租戶部署下沒有「另一個租戶」可以被隔開，此洞的兩個前提（未認證＋跨租戶）對 BMS 從架構上就都不成立，不是升級後才被保護 | 2026-07-30 |
| #840 | LayUI demo `FrameworkMenuController.Create` 的 `[Public]`（匿名建 menu row，可把任意 URL 開成匿名端點） | Unreleased | **不適用（BMS 已獨立修復）** | `BMS/BMS/Areas/_Admin/Controllers/FrameworkMenuController.cs:84` 有 BMS 自己的修復註解：「`#271 (HIGH)：移除 [Public]`（匿名 bypass）。`FrameworkMenu.Create` 應繼承 class-level 授權（需登入）」——BMS 在 #840 於 WTM 上游修復**之前**就已經在自己的 fork 裡獨立修過同一個洞（不同 issue 編號，同一個缺陷） | 2026-07-30 |
| #828 | `FileAttachment` 批次解析查詢失敗被誤讀成「都不存在」，可能刪光子列 | Unreleased | **NuGet-reachable** | 修復點在 `BaseCRUDVM`（Core 套件），任何使用 `ISubFile` 集合的 VM 都適用，無 opt-in 旗標 | 2026-07-30 |
| #856 | Vue3 image-loading 三處回歸（序列化擷取、blob race、洩漏） | Unreleased | **不適用** | `find BMS -iname ClientApp` 零結果——BMS 是 LayUI 應用，沒有 Vue3 `ClientApp`，此修復的整個前提（Vue3 前端）不存在 | 2026-07-30 |
| #875 | `LoadExistingSubItemFileIds`/`LoadEntitySnapshot` 同款查詢失敗誤讀 | Unreleased | **NuGet-reachable** | 同 #828，`BaseCRUDVM` 內的延伸修復 | 2026-07-30 |
| #830 | demo `FileApiController` 三份 copy 九個洞（`[Public]` 移除、tenant-scoped delete、csName 驗證、`GetFileInfo` 投影）＋共用資產（`framework_layui.js`/`MultiUploadTagHelper.cs`）POST-then-GET fallback | Unreleased | **需要 template migration（controller 半，已確認尚未套用）／NuGet-reachable（共用資產半）** | **BMS 有自己的 `FileApiController.cs`**（`BMS/BMS/Areas/_Admin/ApiControllers/FileApiController.cs`）。實際比對：`GetFileName`/`GetFile`/`GetUserPhoto`/`DownloadFile` 四個動作**目前仍是 `[Public]`**（:146,166,250,276）；`DeletedFile` **仍是 `[HttpGet]`** 且呼叫非 tenant-scoped 的 `fp.DeleteFile(...)`（:303-310）——與 #830 描述的洞完全一致，**尚未套用上游修法**。BMS 已自己補了 `csName` 的 `IsKnownConnectionKey` 驗證（自家 #517），但沒有 `GetFileInfo` 動作（原本就沒複製這個）。共用資產半（`framework_layui.js`/`MultiUploadTagHelper.cs` 的 POST-then-GET fallback）隨套件升級自動取得 | 2026-07-30 |
| #849（#824 Part 1） | `_Framework/UpdateModelProperty` 拒絕寫入任何 `FileAttachment` FK | Unreleased | **對 BMS 零可達** | 這個端點的**整條路由**在 routing 完成、進入 auth/controller **之前**就被 BMS 自己的 `BlockedFrameworkEndpointsMiddleware`（`BMS/BMS/Infrastructure/BlockedFrameworkEndpointsMiddleware.cs:32-37,46-56`）短路回 404——不論 WTM 這個 sink 修不修，BMS 都碰不到它，因為請求根本到不了 | 2026-07-30 |

### 分類統計

| 分類 | 項目數 |
|---|---|
| NuGet-reachable（含部分半） | 6（#788、#867、#799、#828、#875、#830-共用資產半） |
| 需要 copied-template migration，**已確認尚未套用** | 1（#830 的 controller 半） |
| 需要 copied-template migration，**不確定是否適用**（未 diff BMS 自有副本） | 1（#776） |
| 對 BMS 零可達（模組未引用／端點被自家 middleware 擋） | 5（#841/#862、#883、#843、#876、#849） |
| Opt-in 但未接線（不完全落入三桶，額外標出） | 2（#827、#829） |
| 不適用（BMS 已獨立修復，或架構前提不存在） | 3（#840、#856、#859） |
| **合計相異修復項目** | **17**（10.18.0 兩項 + Unreleased 十五項；10.17.0 無 `### Security` 段落，不計入） |

上面六個分類的項目數加總是 18、不是 17：**#830** 一項的兩半（controller 半／共用資產半）分屬不同分類，各自計了一次，其餘 16 項每項只落在一個分類。

**無法乾淨分類、如實標記不確定的項目：2 項**（#776 — 未 diff BMS 自有 vendor 副本；#827 — 不落入三桶原始定義，需要新增第四種語意）。#829 雖然也不落入原始三桶，但它的歸類本身沒有不確定性——它需要的 opt-in 動作與 #827 完全相同（同一個 `IWtmFrameworkEndpointAuthorizer` 種子），故直接併入 #827 所在的「Opt-in 但未接線」分類，不算入這裡的「無法乾淨分類」清單。這兩項（#776、#827）**沒有猜測湊數**，理由已在表格逐項寫明。

**重要澄清，避免誤讀 #919 原始「12 支 PR 沒有一支被證明保護到任何系統」的說法**：這句話在「BMS production 目前仍是 8.x」這個前提下成立——因為 production 連 10.16.1 這個 repo pin 都還沒吃到，遑論 10.17.0+。但不代表這些修復對 BMS **未來**升級之後同樣沒意義：上表 17 項裡，6 項是 **NuGet-reachable**（BMS 只要跑一次套件升級就自動獲得保護，不需要動任何自己的程式碼），5 項對 BMS 架構形狀確實**零可達**（不是修復本身沒價值，是 BMS 沒有對應的攻擊面），2 項需要下游**主動接線**才會生效（套件出貨了新擴充點，但預設不動作——#827、#829 依賴同一個 opt-in 動作），2 項需要下游**改自己複製走的檔案**才會套用（#776、#830 的 controller 半；其中 #830 已確認 BMS 尚未套用），3 項對 BMS **不適用**（架構前提不存在，或 BMS 已自行修過同一個洞）。「修好 ≠ 保護到」這個題目本身，答案取決於「保護到哪個版本層」——這正是本文件要固定下來的區分。

---

## 4. 與本波修復相關的旗標——WTM 預設 vs. BMS 現況

| 旗標 | WTM 預設 | 引入版本 | BMS 是否存在此設定 | BMS 實際值 |
|---|---|---|---|---|
| `Configs.EnforceVmExportAuthorization` | `false` | #810 週期（10.16.1 之後） | 不存在——BMS 套件版本（10.16.1）比這個旗標引入的版本舊，設定項本身在 BMS 這個版本的 `Configs` 類別裡不存在 | N/A |
| `Configs.EnforceDeletePreviewAuthorization` | `false` | 同上 | 不存在 | N/A |
| `Configs.EnforceFileAccessAuthorization` | `false` | 同上 | 不存在 | N/A |
| `Configs.EnforceVmImportAuthorization` | `false` | 同上 | 不存在 | N/A |
| `FileUploadOptions.EnforceTenantFileScope` | **`true`**（#859 起；先前 `false`） | Unreleased（10.19.0） | 不存在（版本更早） | N/A（BMS 目前跑的是舊版 `false` 語意，但 BMS `EnableTenant=false`，此旗標對單租戶部署本來就不生效） |
| `Configs.EnforceRequestBindingScope` | **`true`**（#867 起） | Unreleased（10.19.0） | 不存在（版本更早） | N/A |
| `Configs.IsFilePublic` | `false`（demo 樣板；#859 前三份樣板皆 `true`） | 既有旗標 | 存在 | `appsettings.json:90` = **`false`**（BMS 自己設定，非樣板帶來） |
| `Configs.EnableTenant` | `false` | 既有旗標 | 存在 | `appsettings.json:59` = **`false`**（BMS 是單租戶部署——這一點讓多個以「跨租戶」為前提的修復對 BMS 天生不適用，不是因為修好了） |
| `Configs.IsQuickDebug` | — | 既有旗標 | 存在 | `appsettings.json:58` = `false` |
| `Layui:Asset` | — | #573/#614 起 | BMS 用自 fork `_Layout.cshtml`/`Login.cshtml` 直接切換，不透過 WTM 的 `LayuiAssets.ResolveLayuiBase` | 未知（未讀 BMS 實際 `_Layout.cshtml` 內容以確認目前選的是哪一棵資產樹） |

**要點**：四個 `Enforce*Authorization` 旗標、`EnforceTenantFileScope` 的新預設、`EnforceRequestBindingScope`，**在 BMS 目前的套件版本裡根本不存在**——這不是「BMS 選擇不啟用」，是這些設定項本身要到 10.19.0+ 才被定義。這進一步印證了 §1 的分層問題：討論「BMS 有沒有開某個安全旗標」在 repo pin 落後這麼多版的情況下沒有意義，因為那個旗標的程式碼還沒被拉進來。

---

## 5. BMS 近期升級歷史與 rollback target（repo pin／staging 軌）

來源：`BMS/doc/MIGRATION_LOG.md`。**這是 repo pin／staging 軌的歷史，不是 production 部署歷史**（見 §1、§2）。

| Phase | 日期 | WTM 版本變化 | 驗證 | Rollback target |
|---|---|---|---|---|
| Phase 20 | 2026-07-05 | 10.13.12 → 10.13.15 | staging e2e 557/557（修復 #219/#221 後） | `git revert` #220/#222 合併 commit；刪 `BMS_Layui__Asset` env var + recycle（不需回版） |
| （10.14.0→10.14.1） | 2026-07-06 | 10.14.0 → 10.14.1 | staging e2e 620/620，dual review APPROVE | 回 10.14.0 |
| （10.14.1→10.14.2） | 2026-07-06 | 10.14.1 → 10.14.2（deprecation-only） | build+test，無需真機（doc-only diff） | 回 10.14.1 |
| Phase 25 | 2026-07-11 | 10.14.2 → 10.14.5 | staging e2e 636/636，dual review APPROVE | VM 舊版備份 `C:\bms-publish-old-20260711-051331` |
| Phase 26 | 2026-07-20 | 10.14.5 → 10.16.1 | staging e2e 644/644，dual review APPROVE | VM 舊版備份 `C:\bms-publish-old-20260719-225014` |
| （尚無記錄） | — | 10.16.1 → 10.17.0+ | 尚未開始 | — |

**Phase 20 隔天緊接的 Phase 21**（2026-07-06，10.13.15 → 10.13.17，#228）未在上表獨立成行：staging e2e 620/620（7 批，`e2e-runs/20260706-082401/`），並在此 Phase 完成上游 #573 硬性閘門的 staging sign-off（貼回 WTM #573 解除 flip BLOCKED）。驗證命令：`grep -n "557/557\|620/620 綠" BMS/doc/MIGRATION_LOG.md`——557/557 落在 `## Phase 20`、620/620 綠 落在 `## Phase 21`（`## Phase 22` 也有一筆同數字，屬 flip 採用後的回歸驗證，非本表列項）。

**自 Phase 26（2026-07-20）起，`MIGRATION_LOG.md` 沒有新條目**——距今（2026-07-30）10 天，跨越 WTM 10.17.0、10.18.0 兩個已發布 release 加上 Unreleased 的整個安全批次都還沒被排進 BMS 的升級計畫。

---

## 6. Merge → staging → production 時間——已知與未知

| 階段 | 已知 | 未知 |
|---|---|---|
| WTM merge | 精確到 commit（見 `CHANGELOG.md`／`git log`） | — |
| WTM release（tag） | v10.18.0 是最新 tag；10.19.0–10.21.0 尚未 tag（見 `version.props`） | 何時會 tag——沒有排定的 release 節奏文件 |
| BMS repo pin 更新 | Phase 20-26 逐筆有時間戳（§5） | 下一次 pin 更新（10.16.1→10.17.0+）沒有排定日期 |
| BMS staging 驗證 | Phase 20-26 逐筆有 e2e 結果 | 同上，取決於 pin 更新 |
| **BMS production 部署** | **沒有任何一筆記錄** | **完全未知——見 §2** |

**這一列（production 部署）目前是空的，不是「資料遺失」，是「這件事還沒發生」的最佳可得證據（§2）。**

---

## 7. KPI 變更

舊 KPI（PR 數、release 數、issue 數）衡量的是**產出**，不是**風險是否真的降低**。本專案往後追蹤：

1. **Production adoption latency**——從 WTM 修復 merge 到已知下游 production 實際跑到含該修復的版本，中間經過的時間。目前對 BMS 而言：**無法計算**（§2 已說明沒有 production 版本查詢方式，latency 的終點未知）。
2. **Reachable-risk reduction**——不是「修了幾個安全問題」，是「修了幾個**下游實際可達**的安全問題」。§3 的可達性分類就是這個指標的輸入：17 項裡對 BMS 而言只有 6 項 NuGet-reachable，其餘要嘛零可達、要嘛對 BMS 不適用、要嘛需要下游動作（migration 或接線）。
3. **Rollback count**——下游升級後回滾的次數。目前 BMS 側 0 次（§5 六個 Phase 全部前進、沒有一次 revert 是因為線上事故；`git revert` 出現在文件裡是「rollback 方案」而非「已執行的 rollback」）。

這三個指標的**目的不是取代** PR/release/issue 計數，而是回答「這些產出有沒有真的降低任何人的風險」——舊指標繼續用於追蹤開發速度，但不再單獨作為「安全姿態改善了多少」的證據。

---

## 8. 如何維護這份文件（避免第五起文件漂移事件）

這個月已經發生四起文件漂移（#863、#893、#899、#909）。本文件的維護規則刻意設計得比敘述性文件更容易保持正確：

1. **每次 BMS `Directory.Packages.props` 的 WTM pin 變動時**，更新 §1 的「BMS repo pin」列與 §5 的升級歷史表——這是機械性的，`grep -n WalkingTec BMS/Directory.Packages.props` 就能拿到新值。
2. **每次 WTM 發一個新 tag，或 `CHANGELOG.md` 新增一個 `### Security` 段落時**，把新項目加進 §3 的可達性表——**不要用記憶或猜測分類，重新對照 BMS 當時的實際程式碼／設定跑一次驗證命令**（每列都附了命令，直接照跑）。
3. **每次 BMS 有新的 `doc/MIGRATION_LOG.md` Phase 或 `doc/plans/*prod*` 文件時**，重新檢查 §2 的 production 版本證據鏈是否需要更新——特別留意任何明確寫出「production 已切換」或給出新版本號的字句。
4. **「最後驗證日期」欄位是本文件唯一的新鮮度保證**——沒有自動化能驗證這份文件本身是否過期（正是 §2 想凸顯的問題的縮影）。任何人在讀本文件時，若「最後驗證日期」超過約 2-3 週，應視為**可能已過期**，重新核對 §1/§2/§5 再引用。
5. **如果查到與本文件矛盾的新事實**（例如 BMS production 版本有了新的直接證據），**就地更正並保留更正記錄**（比照 `CHANGELOG.md` 的 `### Corrected` 慣例），不要靜默覆寫——這份文件本身的可信度取決於它示範了它要求下游做到的同一件事：誠實記錄「不知道」與「曾經錯過」。
6. **不要把本文件變成第二份 `docs/security-advisory-2026-07-DRAFT.md`**——那份文件（追蹤於 #833）已經對「下游要怎麼分辨自己屬於哪一類」做了通用、逐項的技術判定，本文件只需要引用其結論、疊加 BMS 的具體版本層與驗證結果，不需要重新推導同樣的技術細節。**已知落差**（`grep -n 829 docs/security-advisory-2026-07-DRAFT.md` 零結果——#829 完全沒有出現在該草稿裡，不構成落差；下面兩項才是實際落差）：截至本文件最後驗證日期，該草稿第 135 行起的「第四類——仍未修」表格第 5 項（#827）與第 9 項（#867）仍把兩者列為「仍未修」，且第 10 項也仍把 **#883** 列為「仍未修」；但 `CHANGELOG.md`（`[Unreleased]`）已記錄 #827、#867、#883 三者均已修復並出貨。那份草稿本身需要一次刷新（未在本票範圍內處理，建議另開 issue）。

---

## 相關文件

- [`docs/production-readiness.md`](./production-readiness.md) — WTM 框架本身的 production-readiness 自評；本文件補的是「即使框架讀起來準備好了，某個具體下游是否真的採用了」這一層
- [`docs/security-advisory-2026-07-DRAFT.md`](./security-advisory-2026-07-DRAFT.md) — 通用的下游可達性技術判定（草稿，追蹤於 #833）；本文件的 §3 疊加 BMS 的具體驗證結果
- [`CHANGELOG.md`](../CHANGELOG.md) — 每項修復的完整技術細節與可達性論證來源
- `BMS/doc/MIGRATION_LOG.md`、`BMS/doc/plans/wtm-8to10-prod-readiness-audit-2026-06-22.md`、`BMS/Directory.Packages.props` — 本文件 BMS 相關陳述的原始來源（BMS repo，唯讀引用）
