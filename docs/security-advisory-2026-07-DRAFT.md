# 安全公告草稿（尚未發布）— 2026-07

> [!WARNING]
> **狀態：草稿。未發布。** 追蹤於 #833。
> 「Fixed in」欄位目前多數為 TBD——修復尚未完成。**在所有 TBD 填實之前不得發布**，否則下游會拿到一份無法據以行動的公告。
> 發布通道（Gitea issue／GitHub Security Advisory／兩者）尚未裁決。

---

## 為什麼需要這份公告，而不只是修 framework

WTM 以 NuGet package 出貨（Gitea 私有 registry 與 `nuget.pkg.github.com/cct08311github`，見 `.github/workflows/publish-nuget.yml:21` 與 `:526`），**同時**以 demo 專案作為樣板供下游**複製**。

分階段修 framework 只能觸及：未來發佈的 package、未來由樣板複製出的程式、以及願意升級且仍走相同 framework seam 的應用。它**碰不到**：已 pin 舊版 package 的部署、已複製並改過 controller 的應用、以及不會自己去註冊 opt-in policy 的既有部署。

因此下面每一條都標了**「你要自己檢查什麼」**，而不只是「升級到某版」。

---

## 適用範圍速判

先跑這三條命令。**任何一條有輸出，就往下讀對應章節。**

```bash
# A. 你的應用有沒有複製 demo 的 FileApiController？
grep -rn 'Route("api/_file")' --include='*.cs' . 

# B. 你的應用有沒有複製 LayUI demo 的 FrameworkMenuController？
grep -rn -B3 'ActionResult Create(FrameworkMenuVM' --include='*.cs' . | grep -i 'public\]'

# C. 你的組態有沒有明確開啟這五個旗標？（沒有輸出＝全部使用不安全的預設值）
grep -rniE 'EnforceTenantFileScope|EnforceVmExportAuthorization|EnforceFileAccessAuthorization|EnforceDeletePreviewAuthorization|EnforceVmImportAuthorization' \
  --include='appsettings*.json' --include='*.cs' .
```

---

## 第一類：預設組態即生效（升級 package 不會自動改變，必須改組態）

這些不是 bug，是**預設值選擇**。它們在所有版本都是這個行為，包含最新版。

| # | 行為 | 預設值出處 | 你要做什麼 |
|---|---|---|---|
| 1 | **任何已認證的呼叫者，只要知道 GUID，就能讀取或刪除任何租戶的檔案** | `FileUploadOptions.cs:37` `EnforceTenantFileScope = false` → `WtmFileProvider.cs:159-161`／`:222-224`／`:265-267` 走 `IgnoreQueryFilters()` | 多租戶部署**必須**設為 `true`。設定前先確認自己沒有合法的跨租戶檔案引用（見下方「NULL-tenant 舊檔」） |
| 2 | 任何已認證的呼叫者可對**任何**已註冊 VM 匯出 Excel／取得範本 | `Configs.cs:657` `EnforceVmExportAuthorization = false` | 見「關於這四個旗標的重要警告」 |
| 3 | 同上，delete-preview | `Configs.cs:673` | 同上 |
| 4 | 同上，檔案存取 | `Configs.cs:702` | 同上 |
| 5 | 同上，匯入 | `Configs.cs:733` | 同上 |
| 6 | 任何已認證的呼叫者可對**任何** analysis-enabled ListVM 下查詢並匯出 | `EnableAnalysisAttribute.cs:14` 的 `AllowedRoles` 無初值 → `null` → `_AnalysisController.cs:627` 首行 `return true` | 在每個 `[EnableAnalysis]` 上明確填 `AllowedRoles` |
| 7 | `GetPagingData`／`GetEmptyData`／`Selector` **完全沒有** per-VM 閘門 | 設計如此，無旗標 | 目前無 framework 層解法（追蹤於 #827／#836）。若你的 ListVM 含敏感資料，需自行在 VM 層加控制 |

### 關於旗標 2–5 的重要警告

**把它們設為 `true` 不等於「變安全」，而是「全部拒絕」。** 它們自己的文件就這麼寫（`Configs.cs:645-655`）：

> every export through the shared endpoint returns 403 **until the hosting application overrides `CanExportVm` with real per-VM policy**

而**覆寫 hook 的官方做法在生產環境不生效** —— `_FrameworkController` 是 MVC 路由到的具體類別（`:35`），繼承它只會產生第二個 controller，前端硬編的 `/_Framework/*` 永遠打不到。可注入的授權接縫追蹤於 **#827**，尚未提供。

**所以現階段這四個旗標的實際選項只有：維持 `false`（不強制），或設 `true`（該端點全面停用）。** 這是誠實的現況，不是建議。

---

## 第二類：你若複製過 demo 樣板

樣板不是被引用的，是被**複製**的。修 upstream 樣板**不會**改變你已部署的程式。

### 2a. 三份 `FileApiController`（`Route("api/_file")`）

若「適用範圍速判 A」有輸出，你的應用含有以下全部或部分：

| 端點 | 問題 |
|---|---|
| `GetFileName` / `GetFile` / `GetFileInfo` / `GetUserPhoto` / `DownloadFile` | 標記 `[Public]`（實作 `IAllowAnonymous`）→ **未認證即可依 GUID 讀取任意檔案內容**；配合第一類第 1 項的預設值，跨租戶 |
| `GetFileInfo` | 直接 `dc.Set<FileAttachment>().CheckID(id).FirstOrDefault()` 並**回傳整個 entity** |
| `DeletedFile` | 呼叫非 tenant-scoped 的 `DeleteFile`（而非 `DeleteFileTenantScoped`）；且是 **HTTP GET** |
| 全部 8 個 action | `csName` 直接進 `Wtm.CreateDC(cskey: csName)`，**零驗證** —— `WTMContext.IsKnownConnectionKey`（`:694`）存在但樣板未使用 |

**你要做什麼**：移除五個 `[Public]`；`DeletedFile` 改用 `DeleteFileTenantScoped` 並改為 `[HttpPost]`；每個吃 `csName` 的 action 加上 `IsKnownConnectionKey` 驗證；`GetFileInfo` 改走 `WtmFileProvider` 而非直接查 `DbSet`。

> 具體 patch（三種樣板各一份）：**TBD** —— 待 #830／#833 的修復落地後補上。

### 2b. LayUI 樣板的 `FrameworkMenuController.Create`（#840）

若「適用範圍速判 B」有輸出：

```csharp
[HttpPost]
[Public]                                    // ← 問題所在
[ActionDescription("Sys.Create")]
public ActionResult Create(FrameworkMenuVM vm)   // → vm.DoAdd()
```

`PrivilegeFilter.cs:147-151` 對 `isPublic == true` 是完整 early return，發生在身分檢查（`:153`）與 `isHostOnly`（`:215`）**之前** —— 類別上的 `[MainTenantOnly]` 也一併被繞過。而 `FrameworkMenu.IsPublic` 正是 `WTMContext.cs:809` 判定任意 URL 是否匿名的依據。

→ **未認證者可寫入一筆 `IsPublic=true` 的 menu，把任意端點開成匿名。**

**你要做什麼**：立即移除該 `[Public]`。**這一條不需要等 upstream 修復，也不需要升級 package** —— 那個檔案在你自己的 repo 裡。

**適用範圍**：僅 LayUI 樣板。Vue3Demo、BlazorDemo、以及同 demo 的 `ApiControllers/FrameworkMenuController.cs` 皆無此問題（`[Public]` 計數為 0）。此缺陷可追溯至 2020-12-12（commit `d5a3e7535`），從上游 WTM 繼承。

**部署後驗證**：
```bash
curl -i -X POST https://<your-host>/_Admin/FrameworkMenu/Create \
  -d 'Entity.PageName=probe&Entity.Url=/probe&Entity.IsPublic=true'
# 預期：401 或 403，且資料庫無新列。若回 200 或看到 dialog HTML，你仍受影響。
```

---

## 第三類：背景執行路徑（無論組態如何）

| # | 行為 | 追蹤 |
|---|---|---|
| 8 | Dashboard snapshot／alert 背景 job 執行使用者持久化的 widget 設定時，**列級 DataPrivilege 完全被跳過** —— `DCExtension.cs:298` 對 `LoginUserInfo == null` 是 `return baseQuery`（fail-open） | #843 |
| 9 | 多租戶 ETL job 在重啟後**不會被排程**（`JobDataMap` 只帶 job ID，背景 scope 無 tenant） | #832 |
| 10 | `EtlRunLog` **沒有實作 `ITenant`** → global query filter 不對它生成述詞 → `_EtlRunLogController` 的所有查詢跨租戶，且該 controller 缺角色閘門 | #841 |

第 8 項的關鍵在於：**寫入時的閘門 ≠ 執行時的閘門。** 使用者建立 widget 時受約束；同一個 widget 在背景重跑時不受。

---

## 版本矩陣

| 項目 | Affected | Fixed in |
|---|---|---|
| 第一類 1–7（預設組態） | 所有版本含 10.18.0 | **不適用** —— 這些是預設值選擇，需組態變更或等 #827 提供可注入接縫 |
| 2a 三份 `FileApiController` | 所有版本的樣板 | TBD（#830） |
| 2b `FrameworkMenuController` | LayUI 樣板，2020-12-12 起 | TBD（#840）—— **但你可以自己先修，不必等** |
| 第三類 8 | ≤ 10.18.0 | TBD（#843） |
| 第三類 9 | ≤ 10.18.0 | TBD（#832） |
| 第三類 10 | ≤ 10.18.0 | TBD（#841） |

---

## 這份公告不宣稱什麼

- **不宣稱清單完整。** 依據是 #836 的 entrypoint→sink 窮舉表，該表自己標註了未驗證的列，且明確指出**以 entrypoint 為 key 的表看不見「實體型別有沒有 `ITenant`」與「路徑有沒有繞過 global query filter」這兩個維度** —— 第三類第 10 項就是這樣被漏掉的。
- **不宣稱升級 package 就能解決。** 第一類完全靠組態；第二類完全靠你自己的 repo。
- **不宣稱這些是新缺陷。** 多數可追溯到 2020–2026 的既有設計；2026-07 的工作是**發現**它們，不是**造成**它們。唯一的例外是：2026-07 有數則 commit message 宣稱了程式碼不支援的保護，那些宣稱已於 #835 撤回。

---

## 發布前檢查清單（#833）

- [ ] 所有 TBD 的 Fixed-in 已填實際版本
- [ ] 三份樣板的具體 patch 已附上（不是「參考 upstream」）
- [ ] 每一條的「部署後驗證」命令都已實跑過
- [ ] `docs/production-readiness.md` 與本文件無矛盾（該文件是本 repo 最誠實的紀錄，衝突時以它為準）
- [ ] 發布通道已裁決
