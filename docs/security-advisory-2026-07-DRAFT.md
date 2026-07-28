# 安全公告草稿 — 2026-07（WTM 10.19.0）

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

## 第一類 —— 升級到 10.19.0 就會拿到（不需要你改任何程式碼）

**這一類在 10.19.0 之前是空的。** 先前所有標記為安全修復的變更，要嘛在你複製走的樣板裡，要嘛在預設關閉的旗標後面。

| # | 修好了什麼 | 機制 | Fixed in |
|---|---|---|---|
| 1 | **任何已認證的呼叫者，只要知道 GUID，就能讀取或刪除任何租戶的檔案** | `FileUploadOptions.EnforceTenantFileScope` 預設由 `false` 改為 `true`。`WtmFileProvider` 因此讓 `FileAttachment` 的 `ITenant` global query filter 生效，不再呼叫 `IgnoreQueryFilters()`。**你複製走的 `FileApiController` 也是呼叫套件的 `WtmFileProvider` 來解析檔案的，所以它一併被修好** | **10.19.0**（#859） |
| 2 | inline 編輯可寫入任意 `FileAttachment` 外鍵 | `/_Framework/UpdateModelProperty` 依 EF relationship metadata 拒絕任何 principal 為 `FileAttachment` 的 FK。無旗標、預設生效 | **10.19.0**（#824 的一個 sink） |
| 3 | `IsFilePublic=true` 現在會在啟動時告警 | 非 Development 環境下發出 `LogCritical`，指名被開放的路由 | **10.19.0**（#859） |

### 第 1 項的相容性

這是**預設行為變更**。單租戶部署零影響（兩邊 `TenantCode` 都是 `null`）。

**多租戶部署請先確認**：若你的記錄合法引用了多租戶啟用前（或經 main host）上傳的 `TenantCode = NULL` 舊檔，升級後這些檔案將無法解析。這是刻意的 —— 放寬它會重開 #815 關掉的同一個 primitive。需要保留舊行為者可設 `FileUploadOptions.EnforceTenantFileScope = false` 明確 opt out，但那會恢復跨租戶讀取。

### 第 2 項的相容性

inline grid cell 編輯無法上傳檔案、只能手打 GUID，因此合法用途趨近於零 —— 但這是我們的推論，若你有反例請開 issue。無 opt-out。

---

## 第二類 —— 你必須改自己的**組態**

| # | 問題 | 你要做什麼 |
|---|---|---|
| 4 | **`IsFilePublic: true` 讓 `/_Framework/GetFile` 與 `/_Framework/ViewFile` 變成未認證可存取** | 設為 `false`，除非你確實要對外公開所有檔案 |

樣板已於 10.19.0 改為 `false`，但**你自己的 `appsettings.json` 不會被升級改動**。

`PrivilegeFilter` 在 `IsFilePublic == true` 時把這兩個 action 標為 public 並提前 return，**跳過身分檢查**；而 `_FrameworkController` 沒有任何 `[Authorize]` 家族屬性，所以那個 filter 是唯一關卡。

在 10.19.0 之前，這與第一類第 1 項疊加的結果是**未認證的任意跨租戶檔案內容讀取**。10.19.0 修好了租戶那一半（升級即得），但**匿名那一半在你自己的組態裡** —— 若你維持 `IsFilePublic: true`，任何人仍可讀取你自己租戶的所有檔案。

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
| `GetFileInfo` 直接查 `dc.Set<FileAttachment>()` 並回傳整個 entity | 改走 `WtmFileProvider`，只回傳呼叫端需要的欄位 |

**canonical 修法**：`git diff 50d26c7b5^ 50d26c7b5 -- demo/` 對照你的副本。

**Vue3 特別注意**：移除 `GetFile` 的 `[Public]` 會讓所有 `<img>`／`el-image` 變成 401，因為 Vue3 是純 JWT（沒有 cookie），而瀏覽器發出的圖片請求帶不到 `Authorization` header。10.19.0 的樣板已把圖片載入改走 axios + blob URL；對應的前端變更是 `git diff 50d26c7b5^ 50d26c7b5 -- demo/WalkingTec.Mvvm.Vue3Demo/ClientApp/`。**只套 controller 而不套前端，你的圖片會全部壞掉。**

**GET → POST 的相容性**：10.19.0 起，框架的上傳 widget（`framework_layui.js`、`MultiUploadTagHelper`）先送 POST，**只在收到 405 時**退回 GET。所以在你改自己的 controller 之前，刪除按鈕仍然可用。那個 fallback 會在下一個 major 移除（#853）。

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
| 9 | 背景 job 的列級 DataPrivilege **fail-open** —— 無 `LoginUserInfo` 時直接回傳未過濾查詢 | #843 |
| 10 | 多租戶 ETL job 重啟後不會被排程；`EtlRunLog` 沒有實作 `ITenant`，因此不受 global filter 保護；三個 ETL controller 缺角色閘門 | #832、#841 |

第 5 項對已認證使用者的影響最廣。**目前沒有可用的緩解措施** —— 把旗標設為 `true` 會讓對應端點對所有人回 403。

---

## 這份公告不宣稱什麼

- **不宣稱清單完整。** 依據是 #836 的 entrypoint→sink 窮舉表，而該表自己指出：以 entrypoint 為 key 的表看不見「sink 實體有沒有實作 `ITenant`」與「路徑有沒有繞過 global query filter」這兩個維度 —— 第四類第 10 項就是這樣被漏掉的。而第一類第 1 項（本公告最嚴重的一條）是在十七輪審查之後才被找到的。
- **不宣稱這些是新缺陷。** 多數可追溯到 2020–2026 的既有設計。2026-07 的工作是**發現**它們，不是造成它們。
- **不宣稱升級就足夠。** 只有第一類是。第二、三類需要你動手，第四類目前無解。

---

## 發布前檢查清單（#833）

- [x] **四條速判命令實跑過** —— 在本 repo（已修）回傳預期的 0／已修值，並用 `git archive` 取出 `679e4358b^` 與 `50d26c7b5^` 的檔案重跑，確認在**受影響的樹**上分別回 1 個 `[Public]`（速判 C）與 Route + 7 個 `[Public]`（速判 B）。命令在兩種狀態下的行為都正確。
- [x] **兩條 `git diff` 命令實跑過** —— `demo/` 得 11 檔／478+／165−，`Vue3Demo/ClientApp/` 得 5 檔／201+／95−，皆可用作對照。
- [ ] `curl` 驗證命令實跑過 —— **尚未**，需要一個實際部署的 host。發布前必須在真實環境確認回傳 401/403 且無新列。
- [ ] 與 `docs/production-readiness.md` 逐條比對無矛盾（衝突時以它為準）
- [ ] 發布通道已裁決（Gitea issue／GitHub Security Advisory／兩者）
- [ ] 決定是否為第一類第 1 項的相容性影響（NULL-tenant 舊檔）提供一個一次性稽核指令，讓下游升級前能自查有沒有中招
