# 安全公告草稿 — 2026-07（WTM 10.21.0）

> [!WARNING]
> **狀態：草稿。未發布。** 追蹤於 #833。
> 發布通道（Gitea issue／GitHub Security Advisory／兩者）尚未裁決。
> 標為「仍未修」的項目就是真的還沒修，不是待補欄位。

---

## 一分鐘速判

在你的應用根目錄跑這四條。**有輸出就往下讀對應章節。**

```bash
# A. 你的組態有沒有把框架的檔案端點開成匿名？（最嚴重）
grep -rn '"IsFilePublic"' --include='appsettings*.json' .

# B. 你有沒有複製過 demo 的 FileApiController？
grep -rn 'Route("api/_file")' --include='*.cs' .

# C. 你有沒有複製過 LayUI demo 的 FrameworkMenuController？
grep -rn -B4 'ActionResult Create(FrameworkMenuVM' --include='*.cs' . | grep -i 'public\]'

# D. 你有沒有明確設定過這些授權旗標？（無輸出 = 全部使用預設）
grep -rniE 'EnforceTenantFileScope|EnforceVmExportAuthorization|EnforceFileAccessAuthorization|EnforceDeletePreviewAuthorization|EnforceVmImportAuthorization' \
  --include='appsettings*.json' --include='*.cs' .
```

---

## 第一類 —— 升級到 10.21.0 就會拿到（不需要你改任何程式碼）

**這一類在 10.21.0 之前是空的。** 先前所有標記為安全修復的變更，要嘛在你複製走的樣板裡，要嘛在預設關閉的旗標後面。

| # | 修好了什麼 | 機制 | Fixed in |
|---|---|---|---|
| 1 | **任何已認證的呼叫者，只要知道 GUID，就能讀取或刪除任何租戶的檔案** | `FileUploadOptions.EnforceTenantFileScope` 預設由 `false` 改為 `true`。`WtmFileProvider` 因此讓 `FileAttachment` 的 `ITenant` global query filter 生效，不再呼叫 `IgnoreQueryFilters()`。**你複製走的 `FileApiController` 有部分一併被修好** —— `GetFile`／`GetUserPhoto`／`DownloadFile` 確實是呼叫套件的 `WtmFileProvider` 解析檔案，因此隨升級一起修好；**但 `GetFileInfo` 不是**（見下方「第 1 項涵蓋不到的地方」） | **10.21.0**（#859） |
| 2 | inline 編輯可寫入任意 `FileAttachment` 外鍵 | `/_Framework/UpdateModelProperty` 依 EF relationship metadata 拒絕任何 principal 為 `FileAttachment` 的 FK。無旗標、預設生效 | **10.21.0**（#824 的一個 sink） |
| 3 | `IsFilePublic=true` 現在會在啟動時告警 | 非 Development 環境下發出 `LogCritical`，指名被開放的路由 | **10.21.0**（#859） |

### 第 1 項的相容性

這是**預設行為變更**。單租戶部署零影響（兩邊 `TenantCode` 都是 `null`）。

**多租戶部署請先確認**：若你的記錄合法引用了多租戶啟用前（或經 main host）上傳的 `TenantCode = NULL` 舊檔，升級後這些檔案將無法解析。這是刻意的 —— 放寬它會重開 #815 關掉的同一個 primitive。需要保留舊行為者可設 `FileUploadOptions.EnforceTenantFileScope = false` 明確 opt out，但那會恢復跨租戶讀取。

### 第 1 項涵蓋不到的地方 —— `GetFileInfo`

升級**不會**修好你複製走的 `GetFileInfo`。它不經過 `WtmFileProvider`：

```csharp
[Public]
public IActionResult GetFileInfo([FromServices] WtmFileProvider fp, string id, string csName = null)
{
    FileAttachment rv = new FileAttachment();
    using (var dc = Wtm.CreateDC(cskey: csName))
    {
        rv = dc.Set<FileAttachment>().CheckID(id).FirstOrDefault();
    }
    return Ok(rv);          // ← 整個 entity
}
```

`EnforceTenantFileScope` 這個旗標只控制 `WtmFileProvider.GetFile` 要不要呼叫 `IgnoreQueryFilters()`。這裡直接查 `dc.Set<FileAttachment>()`，那個旗標碰不到它 —— **升級到 10.21.0 對這個 action 沒有任何作用**。

它的曝險是**未認證**——但「`dc.Set<>()` 仍吃 `ITenant` global filter，所以多租戶部署限縮在呼叫者自己租戶」這個推論本身**不成立**：filter 確實生效，但**匿名呼叫者可以決定 filter 用哪個租戶查**：

- `WTMContext.CreateDC()`（`WTMContext.CreateDC.cs:42-55`）在請求**未認證**、且 `ConfigInfo.DisableRefererTenantResolution` 為 `false`（**預設值**，`Configs.cs:173`）時，會讀取攻擊者可控的 `Referer` header，比對已知租戶的 `TDomain`，把匹配到的租戶設為這次查詢的 tenant context。
- `csName` 直通 `Wtm.CreateDC(cskey: csName)`，可任意指定 `ConfigInfo.Connections` 裡任何已設定、已啟用的連線——同樣零驗證。
- `[Public]` 讓 `PrivilegeFilter` 提前 return，跳過身分檢查。
- `return Ok(rv)` 回傳的是**整個** `FileAttachment` entity，包含 `Path`、`ExtraInfo`、`HandlerInfo`，以及 —— 若你的 `SaveMode` 是 `database` —— **`byte[] FileData`，也就是檔案內容本身**。

因此**不只限於單租戶部署**：任何知道目標 GUID、且能取得或猜到目標租戶 `TDomain` 的匿名呼叫者，都能透過 `Referer` header 把查詢導向該租戶並讀出其完整檔案內容；只有明確設定 `DisableRefererTenantResolution = true`（非預設）才會把匿名請求收斂回 null／main-host 租戶。

→ **這一條屬於第三類（你必須改自己複製走的程式碼），修法見 3a。** 列在這裡是因為第 1 項的敘述容易讓人以為升級就夠了。

### 第 2 項的相容性

inline grid cell 編輯無法上傳檔案、只能手打 GUID，因此合法用途趨近於零 —— 但這是我們的推論，若你有反例請開 issue。無 opt-out。

---

## 第二類 —— 你必須改自己的**組態**

| # | 問題 | 你要做什麼 |
|---|---|---|
| 4 | **`IsFilePublic: true` 讓 `/_Framework/GetFile` 與 `/_Framework/ViewFile` 變成未認證可存取** | 設為 `false`，除非你確實要對外公開所有檔案 |

樣板已於 10.21.0 改為 `false`，但**你自己的 `appsettings.json` 不會被升級改動**。

`PrivilegeFilter` 在 `IsFilePublic == true` 時把這兩個 action 標為 public 並提前 return，**跳過身分檢查**；而 `_FrameworkController` 沒有任何 `[Authorize]` 家族屬性，所以那個 filter 是唯一關卡。

在 10.21.0 之前，這與第一類第 1 項疊加的結果是**未認證的任意跨租戶檔案內容讀取**。10.21.0 修好了租戶那一半（升級即得），但**匿名那一半在你自己的組態裡** —— 若你維持 `IsFilePublic: true`，任何人仍可讀取你自己租戶的所有檔案。

> 三份樣板原本都預設 `true`。若你是從樣板 scaffold 出來的，**你極可能有這個值**，即使你從未主動設定過它。

---

## 第三類 —— 你必須改自己**複製走的程式碼**

樣板是被複製的，不是被引用的。升級套件永遠不會更新這些檔案。

### 3a. 三份 `FileApiController`（速判 B 有輸出時適用）

| 問題 | 修法 |
|---|---|
| `GetFileName` / `GetFile` / `GetFileInfo` / `GetUserPhoto` / `DownloadFile` 標記 `[Public]`（`IAllowAnonymous`）→ **未認證讀取** | 移除這五個 `[Public]` |
| `DeletedFile` 呼叫非 tenant-scoped 的 `DeleteFile`，且是 **HTTP GET** | 改用 `DeleteFileTenantScoped`，並改為 `[HttpPost]` |
| 八個 action 的 `csName` 直通 `Wtm.CreateDC(cskey:)`，**零驗證** | 每一處加上 `WTMContext.IsKnownConnectionKey` 驗證 |
| `GetFileInfo` 直接查 `dc.Set<FileAttachment>()` 並 `Ok(rv)` 回傳**整個 entity** —— 含 `Path`／`ExtraInfo`／`HandlerInfo`，且 `SaveMode=database` 時含 **`byte[] FileData`（檔案內容本身）**。**升級碰不到這一條**：它不經過 `WtmFileProvider`，`EnforceTenantFileScope` 對它無效 | 改走 `WtmFileProvider`，只回傳呼叫端需要的欄位 |

**canonical 修法**：`git diff 50d26c7b5^ 50d26c7b5 -- demo/` 對照你的副本。

**Vue3 特別注意**：移除 `GetFile` 的 `[Public]` 會讓所有 `<img>`／`el-image` 變成 401，因為 Vue3 是純 JWT（沒有 cookie），而瀏覽器發出的圖片請求帶不到 `Authorization` header。10.21.0 的樣板已把圖片載入改走 axios + blob URL；對應的前端變更是 `git diff 50d26c7b5^ 50d26c7b5 -- demo/WalkingTec.Mvvm.Vue3Demo/ClientApp/`。**只套 controller 而不套前端，你的圖片會全部壞掉。**

**GET → POST 的相容性**：10.21.0 起，框架的上傳 widget（`framework_layui.js`、`MultiUploadTagHelper`）先送 POST，**只在收到 405 時**退回 GET。所以在你改自己的 controller 之前，刪除按鈕仍然可用。那個 fallback 會在下一個 major 移除（#853）。

### 3b. LayUI 樣板的 `FrameworkMenuController.Create`（速判 C 有輸出時適用）

該 action 標記 `[HttpPost] [Public]` 並呼叫 `vm.DoAdd()`。`PrivilegeFilter` 對 public 的提前 return 發生在身分檢查與 `[MainTenantOnly]` 使用點之前，兩者一併被繞過。而 `FrameworkMenu.IsPublic` 正是框架判定任意 URL 是否匿名的依據。

→ **未認證者可寫入一筆 `IsPublic=true` 的 menu，把任意端點開成匿名。**

**移除那個 `[Public]` 即可。不需要升級，那個檔案在你自己的 repo 裡。** 可追溯至 2020-12-12，繼承自上游 WTM。僅 LayUI 樣板受影響（Vue3、Blazor、以及同 demo 的 `ApiControllers` 版本皆無）。

驗證：
```bash
curl -i -X POST https://<your-host>/_Admin/FrameworkMenu/Create \
  -d 'Entity.PageName=probe&Entity.Url=/probe&Entity.IsPublic=true'
# 預期 401/403 且資料庫無新列。回 200 或看到 dialog HTML 表示仍受影響。
```

---

## 第四類 —— 仍未修（升級不會改變，也沒有組態可設）

| # | 問題 | 追蹤 |
|---|---|---|
| 5 | 四個 `Enforce*` 授權旗標（export／delete-preview／file-access／import）**預設不強制**，而設為 `true` 的意思是「全部 403，直到你覆寫對應 hook」—— 那個覆寫路徑在生產路由上不生效（`_FrameworkController` 是被路由的具體類別，繼承只會產生第二個 controller） | #827 |
| 6 | `GetPagingData`／`GetEmptyData`／`Selector` **完全沒有** per-VM 閘門。#796 修的是 Excel 匯出，`GetPagingData` 是同一份資料的 JSON 版 | #812 |
| 7 | `[EnableAnalysis]` 的 `AllowedRoles` 無初值 → `CheckAccess` 首行即放行；`_DashboardController`／`_DashboardDesignerController` 的三個端點連 `CheckAccess` 都沒呼叫 | #842 |
| 8 | Dashboard viewer 可覆寫持久化 widget 的 query 結構（`listVmType`／dimensions／measures／filters） | #831 |
| 9 | 套件消費者拿不到 NPOI vulnerable transitive 的 override：`dotnet pack` 時 .NET 10 SDK 的 package-reference pruning（這條 override 本身會觸發 NU1510「framework already provides this package」，正是 pruning 的判準）把 `System.Security.Cryptography.Xml` pin 從封裝出的 nuspec 依賴清單整條移除。本 repo 自己的弱點掃描只跑在 solution 層級（override 在那裡仍生效），對這個落差結構性看不見 | #934 |
| 10 | Dashboard 設計器 `POST /_dashboard-designer/preview` 把呼叫者送來的整個 `WidgetDefinition`（含 `Source.RestOptions`）當「server-side 權威設定」直接餵進資料抓取管線 —— `AllowPrivateNetwork`／`AllowHttp`「只能在伺服器端開啟」的保證在這個端點不成立，呼叫者自己就是那個伺服器端 | #948 |

第 5 項對已認證使用者的影響最廣。**目前沒有可用的緩解措施** —— 把旗標設為 `true` 會讓對應端點對所有人回 403。

第 9 項（#934）：**升級套件不會把這個 override 帶給你**。NuGet 的依賴解析只讀已發布的 nuspec，`dotnet pack` 階段被砍掉的那一行不會出現在你專案的相依圖裡，你會照樣拉到 NPOI 自己宣告的 `System.Security.Cryptography.Xml` 8.0.2（GHSA-37gx-xxp4-5rgx、GHSA-w3x6-4m5h-cxqf，兩個 HIGH）。受影響對象：**每一個從 registry 安裝這個套件、而非直接建置本 repo solution 的下游**——已有下游在數月前獨立發現並自行 workaround（在自己的專案裡重複同一條 override）。本次未修：修法要嘛是下游在自己的 `.csproj` 也釘一次同一個 override（見 `docs/dependency-management.md` Scenario B 的做法），要嘛是本 repo 改變 pack 設定讓 override 不被裁剪，兩者目前都還沒做。

第 10 項（#948）：`_DashboardDesignerController` 同樣是 `[AllRights]`，無逐 widget 授權。**任何已認證呼叫者**可呼叫 `preview`，帶一個 `Source.RestOptions.AllowPrivateNetwork=true`／`AllowHttp=true`、`Url` 指向內網位址（雲端 metadata endpoint、`127.0.0.1`、內部服務）的 widget 定義，讓伺服器代替呼叫者對該位址發出請求並把回應內容原樣回傳——傳統 SSRF。本次未修。

### 本節在 2026-07-29 的更正（原草稿有兩列已過時，另有一列與現行程式碼不符）

| 原第 9 項 | 背景 job 的列級 DataPrivilege fail-open（#843） | **已修**，10.21.0 起預設 fail-closed，逃生口是呼叫端明寫 `declaredSystemQuery: true`。詳見 `docs/production-readiness.md` |
|---|---|---|
| 原第 10 項 | `EtlRunLog` 未實作 `ITenant`；三個 ETL controller 缺角色閘門（#841） | **已修**（`EtlRunLog`／`EtlLineageRecord` 各補 `TenantCode` 欄位＋遷移腳本，三個 controller 補上與既有兩個相同的 Admin／ETLAdmin 守衛）。同批修掉的根因是 `ApplyEtlModels()` 在 `base.OnModelCreating()` **之後**才註冊 ETL 型別，導致 `ITenant` 過濾器**從未生效過** —— 若你的 `DataContext` 仍呼叫舊的無參數多載，你不會拿到這個修復；四處教這個舊寫法的文件與修法見 #893／PR #896 |

原草稿的第 9 項——「多租戶 ETL job 重啟後不會被排程、觸發後靜默 return」（#832）——**與現行程式碼不符，已移出本清單**。實查 `EtlSchedulerService.LoadJobsFromDbAsync`（`:99-116`）與 `EtlQuartzJob.Execute`（`:44-53`）：兩處都已改用 `IgnoreQueryFilters()`（前者於啟動時載入**所有**租戶的 Enabled／Failed job 並逐一排程，後者於觸發時以 `FirstOrDefaultAsync` 而非 `FindAsync` 按 id 找 job，不受 tenant filter 阻擋）。兩處修法都來自同一個提交 `b3dbae4b3`（#841／#862）—— 那個提交讓 `ITenant` 過濾器第一次真正對 `EtlJobDefinition` 生效，同時就補上了這兩處 `IgnoreQueryFilters()` 以避免生效後把排程器擋死，因此原第 9 項描述的兩個症狀（不排程、靜默 return）在合併當下就已一併解決，不是本次比對才修的。**這也不代表 #832 本身該被關閉**：issue 自訂的驗收條件——一支串連「啟動載入 → Quartz 排程 → 觸發 → 執行」全程、斷言租戶 job 真的執行的端到端測試——目前不存在，`test/WalkingTec.Mvvm.Etl.Test/Scheduling/` 底下沒有這樣的測試。已在 #832 留言記錄此發現，範圍要不要收斂成「補這支測試」由 issue owner 決定，本公告不代為裁決。`docs/production-readiness.md:153` 曾把 #832 描述為「仍是獨立 open issue，未受 [#843] 修法影響」，該行的措辭如果被讀成「#832 描述的症狀原封不動」會產生誤導，已一併更正（見該檔案本次變更）。

### 本節在 2026-07-31 的更正（兩列因本次版本已修移出，新增三列仍未修）

| 原第 9 項 | `RedoUpdateModel` 無 allowlist 反射寫入，可經 dotted path 觸及 DI singleton（#867） | **已修**，10.21.0：新增 `RequestBindingPolicy` 正向 allowlist（依 dotted-path 每一 hop **解析到的型別**判斷，不是宣告類別或屬性名稱），`Configs.EnforceRequestBindingScope` 預設 `true`。**已知殘留、不影響此判定**：型別 denylist 結構上無法窮舉未來可能新增的 gateway 型別，追蹤於 #889——已修的是本公告點名的那個具體攻擊面（`ConfigInfo.IsQuickDebug`／`IsFilePublic`／`GlobaInfo.AllAccessUrls` 三個落地點透過 `RedoUpdateModel` 可達），不是「未來永遠不會有新的 gateway 型別」的承諾。 |
|---|---|---|
| 原第 10 項 | `EtlSchedulerService` 多處 `IgnoreQueryFilters()` 在 HTTP 共用路徑上造成跨租戶 IDOR（#883），先前被遮蔽它的 #876（Etl controller 從未接到 `Wtm`）一併列在同一段 | **兩者皆已修**，同一 PR、同一版本 10.21.0：#876 修好 `Wtm` 注入（新增 `WtmControllerActivator`），#883 修好八個 HTTP 入口的租戶所有權檢查（`LoadJobDefinitionForCallerAsync`／`EnsureCallerOwnsJobAsync`）——同一提交同時修，沒有「#876 一修好、#883 立刻可觸發」的視窗。 |
| 原第 10 項（本輪新發現當下的編號，見下段） | `Selector` 的 `Ids`（Batch）路徑：`GetBatchQuery()` 用 `WhereReplaceModifier` 把 `GetSearchQuery()` 產生的整棵 `Where` 表達式樹砍掉、只留 `Ids.Contains(...)` —— 列級 DataPrivilege 的 `Where` 子句是用同一機制掛上去的，一併被砍（#947） | **已修**，同一版本 10.21.0：`BasePagedListVM` 新增 `GetAuthorizedIdsQuery`，`GetBatchQuery()` 的預設分支改為「暫時換上空白 Searcher 呼叫 `GetSearchQuery()`（讓以 Searcher 值為條件才會加上的 `Where` 不會被加入），再把 `Ids` 限制以 AND 疊加」，不再刪除任何既有 `Where` 節點——列級 DataPrivilege（`DPWhere` 加的 `Where`）因此原封不動留在查詢裡。同一次修法連帶關掉 `GetExportExcel`／`GetExportExcelStream`（`CheckExport` 路徑）等其餘同機制呼叫點，詳見 `docs/production-readiness.md` 的 #947 條目（含完整窮舉表與命令）。**已知殘留、不影響此判定範圍**：空白 Searcher 只能壓下透過 `CheckContain`/`CheckEqual`/`CheckWhere` 等 guard-then-add helper 加的 `Where`；一個不經這些 helper、直接在 `.Where(x => Searcher.Field == x.Field)` 這種 lambda 裡讀 `Searcher` 的寫法不受保護——兩種失效形狀皆已於本 repo demo 樹中找到對應範例（`MajorDetailListVM`／`CityChildrenDetailListVM`）並實測確認，兩者皆為 fail-closed（少資料或無資料，不會多洩漏），屬相容性殘留而非本項安全判定的例外，詳見 `docs/production-readiness.md` 同條目。 |

新增仍未修（本輪新發現，非 #836 窮舉表涵蓋範圍——見下方「這份公告不宣稱什麼」）：**#934**（`dotnet pack` 的 nuspec pruning 使 `System.Security.Cryptography.Xml` override 對套件消費者失效）、**#948**（Dashboard 設計器 `preview` 端點 SSRF）。兩者皆為本次版本**未修**，理由見上方各自段落。#947（`Selector` 的 `Ids` 路徑繞過列級 DataPrivilege）已於同一 10.21.0 週期修復，見上表。

---

## 這份公告不宣稱什麼

- **不宣稱清單完整。** 依據是 #836 的 entrypoint→sink 窮舉表，而該表自己指出：以 entrypoint 為 key 的表看不見「sink 實體有沒有實作 `ITenant`」與「路徑有沒有繞過 global query filter」這兩個維度 —— #883／#947（皆已於 10.21.0 修復）當初就是這樣被漏掉的；#934／#948（見第四類第 9–10 項，皆未修）同樣不在該表的維度內，是後續各自獨立審查才找到的。而第一類第 1 項（本公告最嚴重的一條）是在十七輪審查之後才被找到的。
- **不宣稱這些是新缺陷。** 多數可追溯到 2020–2026 的既有設計。2026-07 的工作是**發現**它們，不是造成它們。
- **不宣稱升級就足夠。** 只有第一類是。第二、三類需要你動手，第四類目前無解。

---

## 發布前檢查清單（#833）

- [x] **四條速判命令實跑過** —— 在本 repo（已修）回傳預期的 0／已修值，並用 `git archive` 取出 `679e4358b^` 與 `50d26c7b5^` 的檔案重跑，確認在**受影響的樹**上分別回 1 個 `[Public]`（速判 C）與 Route + 7 個 `[Public]`（速判 B）。命令在兩種狀態下的行為都正確。
- [x] **兩條 `git diff` 命令實跑過** —— `demo/` 得 11 檔／478+／165−，`Vue3Demo/ClientApp/` 得 5 檔／201+／95−，皆可用作對照。
- [ ] `curl` 驗證命令實跑過 —— **尚未**，需要一個實際部署的 host。發布前必須在真實環境確認回傳 401/403 且無新列。
- [x] 與 `docs/production-readiness.md` 逐條比對無矛盾（衝突時以它為準）—— 比對實跑過兩輪（第二輪為跨廠 review 觸發），發現並已就地更正：
  - **一處過度宣稱**：第一類第 1 項原稿聲稱「複製走的 `FileApiController` 也是呼叫套件的 `WtmFileProvider`，所以它一併被修好」。實查 `50d26c7b5^` 的模板：`GetFile`／`GetUserPhoto`／`DownloadFile` 確實經 `fp.GetFile(...)`，但 `GetFileInfo` 直接查 `dc.Set<FileAttachment>()` 並 `Ok(rv)` 整個 entity，`EnforceTenantFileScope` 對它無效——已收斂為「有部分」並新增獨立段落說明 `GetFileInfo` 的曝險（含 `SaveMode=database` 時 `byte[] FileData` 外洩，已對照 `FileAttachment.cs`／`WtmDataBaseFileHandler.cs` 確認屬實）。
  - **同一段落的更正本身第一輪又矯枉過正**：把曝險窄化寫成「與跨租戶無關」，理由是 `dc.Set<>()` 仍吃 `ITenant` filter——這個推論漏看了 filter 用的租戶可以被匿名呼叫者選擇：`WTMContext.CreateDC()` 在未認證、且 `DisableRefererTenantResolution`（**預設 `false`**，`Configs.cs:173`）未開啟時會信任 `Referer` header 解析租戶（`WTMContext.CreateDC.cs:42-59`）。`csName` 是另一件事——它零驗證地直通任一已啟用連線（`:24,80-87`），選的是**連線／資料庫**，不是租戶，也不會繞過過濾器；未帶 `csName` 時（`:61-65`）甚至會自動選用 Referer 選中那個租戶自己的 DB。已重寫為「租戶由匿名呼叫者透過 Referer 選擇，`csName` 則擴大可觸及的已啟用連線範圍」，不再暗示 `csName` 本身能選租戶，也不再宣稱限縮在呼叫者自己租戶。
  - **兩列已過時、一列與現行程式碼不符**：第四類原第 9 項（#843 背景 DataPrivilege fail-open）與原第 10 項（#841 ETL 租戶／角色閘門，與 #832 混列）——查 API 確認 #843／#841 皆已 `closed`，已依 `docs/production-readiness.md:68` 更正、拆分；新增第 10 項（#883，經查證與 #876 的遮蔽關係屬實，PR #882 待合併同時修兩票）。原第 9 項描述的 #832 症狀（重啟後不排程、觸發後靜默 return）經查 `EtlSchedulerService.cs`／`EtlQuartzJob.cs` 已隨 #841／#862 的 `IgnoreQueryFilters()` 一併解決，與現行程式碼不符，已移出「仍未修」清單（#832 issue 本身保留 open，留言記錄發現、範圍留給 issue owner 裁決）；連帶更正了 `docs/production-readiness.md:153` 對 #832 的過時描述，消除該檔案內部的自我矛盾。
  - 比對過程另外發現公告本身教下游寫法的四處文件仍呼叫已棄用的 `ApplyEtlModels()` 零參數多載（不套用 `ITenant` 過濾器），與本文無直接關係但屬同一批交叉檢查的副產品，已獨立立案 #893，不在本 PR 範圍內處理。
  - **2026-07-31（#927 release-prep 觸發的第三輪）**：`version.props` 在標題撰寫後又經 #859／#843／#883 三次 bump 到 10.21.0（10.19.0／10.20.0 皆未曾實際發版、無對應 tag），全文 12 處版本標籤由 10.19.0／10.20.0 改為實際即將發版的 10.21.0，逐列核對其宣稱的修法皆已在樹上（見本次 PR diff，未發現宣稱但未合併的項目）。同一輪順帶發現第四類原第 9／10 項（#867、#883／#876）已在同一 10.21.0 週期修好，卻仍留在「仍未修」清單——已移出並記錄於上方「本節在 2026-07-31 的更正」，同批新增 #934／#947／#948 三項本次確認仍未修的缺陷。
  - **2026-07-31（#947 修復落地，同一 10.21.0 週期，同日第四輪）**：本表新增 #934／#947／#948 之後，#947（`Selector` 的 `Ids` 路徑繞過列級 DataPrivilege）在同一版本週期內修好（`GetBatchQuery()` 改用 `GetAuthorizedIdsQuery` 空白 Searcher + AND，不再刪除既有 `Where` 節點）——與 #843／#841／#867／#883 走的是同一種「先誠實列為未修、修好後移表更正」流程，不是本公告從一開始就宣稱過度。已從第四類移除、移入上方「2026-07-31 的更正」表；第四類第 11 項（#948）改編號為第 10 項，「這份公告不宣稱什麼」一節的交叉引用同步更正。**已知殘留、如實揭露而非隱藏**：修法本身依賴一個「空白 Searcher 能壓下所有以 Searcher 值為條件的 `Where`」的假設，adversarial review（PR #953）證明該假設對兩種形狀不成立（`.Where(x => Searcher.Field == x.Field)` 這種不經 guard-then-add helper、直接在 lambda 裡讀 Searcher 的寫法）——兩者皆為 fail-closed（該列消失或整批清空，不會多洩漏），不影響本項「已修」的判定，但列為 `docs/production-readiness.md` 同條目的已知相容性限制，未來如需徹底關閉需要另一輪設計變更（在 `DPWhere` 掛的 `Where` 節點上加標記，讓 `WhereReplaceModifier` 能選擇性跳過），本次不做。
- [ ] 決定是否為第一類第 1 項的相容性影響（NULL-tenant 舊檔）提供一個一次性稽核指令，讓下游升級前能自查有沒有中招
