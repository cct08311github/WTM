# `_Framework` 通用資料端點的授權設計

> 本文件說明通用資料端點如何檢查所請求的 view model、升級時需要注意的事項與相容設定。措辭依 FORK.md 第 8 節，只描述檢查的內容。

## 1. 檢查的範圍

`src/WalkingTec.Mvvm.Mvc/_FrameworkController.cs` 的類別層級是 `[AllRights]`（任何已登入的使用者）。其中六個 action 依請求帶來的 `_DONOT_USE_VMNAME` 建立 view model：

| action | 用途 | 參考的目的 |
|---|---|---|
| `GetPagingData` | LayUI 表格取得分頁資料（Selector 內的表格走這一條） | `List` |
| `GetEmptyData` | 取得一列空資料 | `List` |
| `GetExportExcel` | 匯出 Excel | `Export` |
| `GetExcelTemplate` | 下載匯入範本 | `Template` |
| `UpdateModelProperty` | 修改單一屬性 | `Update` |
| `Selector` | 選擇器彈窗（含 `Ids` 對應的已選資料），維持 `[Public]` | `Selector` |

這些端點現在會檢查所請求的 view model 對呼叫者是否可用，語意和功能頁相同：`PrivilegeFilter`、`WTMContext.IsAccessable`、管理員角色、`[Public]`／`[AllRights]`、租戶。資料權限（`DPWhere`）照常生效。

## 2. 限制條件

- LayUI 必須維持可用（FORK.md 第 3 節）：表格的分頁／搜尋／排序、Selector、匯出、範本下載，由 TagHelper 產生的頁面和 code generator 範本產生的頁面**不需要修改**。
- 盡量不 breaking：`EncodeHtmlContent` 預設值、匯出 API、`CreateVM`、`BaseImportVM`、`DataPrivilege`／`DPWhere`、多個 `CsName` 的 `FixConnection`、沒有 `HttpContext` 的 `WTMContext` 都要照常運作。
- 預設就要安全；真有特殊的下游用法才提供有文件的相容設定。

## 3. 比較三個方案

### (a) 由頁面綁定：TagHelper 寫入受保護的參考，端點驗證

渲染 view model 的頁面（表格、Selector、匯出按鈕、範本按鈕）由 TagHelper 在 `_DONOT_USE_VMNAME` 旁寫入一個以 Data Protection 保護的不透明字串（`_DONOT_USE_VMREF`）。內容：view model、目的、使用者（ITCode）、租戶、連線 key、頁面的選單權限 url、到期時間。端點收到請求時驗證：簽章與到期、使用者與租戶相同、view model 與目的相同、連線 key 相同，並用 `IsAccessable` 重新檢查頁面的權限。

- 優點
  - 授權的依據是「使用者能打開渲染這個 view model 的頁面」。頁面本身在渲染前已經過 `PrivilegeFilter`（選單權限、`[Public]`／`[AllRights]`、租戶、管理員角色），所以語意和功能頁**完全相同**，不需要猜 view model 屬於哪個功能。
  - 一個 view model 被多個功能頁使用、Selector 在 A 功能的表單裡列出 B 實體的資料，這兩種合法用法自然成立；不需要在 view model 上宣告任何東西。
  - 現有的 LayUI 頁面和範本產生的頁面不用改：參考由 TagHelper 在渲染時加入，並隨 `where`／ffilter／連結一起送出。
  - 綁定使用者、租戶、連線與目的，且會到期，只有被簽發的使用者能使用。
  - 不需要掃描 assembly 或反組譯。
- 缺點
  - 需要 Data Protection 金鑰，且金鑰要持久化、多實例部署要共用（cookie 驗證本來就需要）。金鑰是暫時的（容器重啟、IIS 沒有保存金鑰環）時，重啟後已經打開的頁面需要重新整理。
  - 升級當下已經打開的頁面（沒有參考）要重新整理一次；頁面開著超過有效期限（預設 480 分鐘）也要重新整理。
  - 下游自己用 JavaScript 呼叫這些端點時，要用 `Wtm.CreateGenericEndpointReference(...)` 取得參考（見第 6 節）。

### (b) 推導擁有者：找出使用這個 view model 的 controller action，檢查那個功能的權限

- 優點：不需要頁面配合。
- 缺點（這是不採用的原因）
  - view model 是在 action 方法**內部**以 `Wtm.CreateVM<T>()` 建立，沒有可靠的靜態對應。只能靠命名慣例（`GadgetListVM` ↔ `GadgetController`）或掃 IL；前者對手寫的 controller 不成立，後者脆弱。
  - 一個 view model 可能被多個功能使用，也可能被別的功能的 Selector 引用；推導出的「擁有者」不是唯一的：選最嚴會讓合法的 Selector 壞掉，選最寬會留下缺口。
  - 推導不出來時只能「全擋」（破壞下游）或「全放」（沒有修復）。

### (c) 在 view model 上以 attribute 宣告需要的權限

- 優點：明確。
- 缺點
  - 每一個下游 view model 都要改；code generator 範本和既有的所有 view model 都要補。未標記的預設只能是「放行」（沒有修復）或「拒絕」（破壞所有現有頁面）。
  - 與 (b) 一樣，無法表達「這個 view model 在 A 頁當選擇器資料來源時，只要有 A 頁的權限」。

## 4. 結論：採用 (a)

(a) 是唯一同時滿足「語意與功能頁相同」「現有 LayUI 頁面不用改」「預設安全」的方案。為了涵蓋少數特殊用法，加上兩個有文件的選項（見第 7 節），預設都不啟用。

## 5. 運作方式

### 5.1 參考的內容

`GenericEndpointReference`（以 `IDataProtector` 保護，purpose `WalkingTec.Mvvm.Core.GenericEndpointReference.v1`，不透明字串，長度上限 4096）：

| 欄位 | 內容 |
|---|---|
| `t` | view model，寫法與 `_DONOT_USE_VMNAME` 相同（assembly-qualified name 去掉版本部分） |
| `p` | 目的（可組合）：`List`（GetPagingData、GetEmptyData）、`Selector`、`Export`、`Template`、`Update`。表格的參考包含 `List`；只有表格對該使用者有匯出按鈕（`_Framework/GetExportExcel` 或標記為匯出的動作；含按鈕組 `ActionsGroup` 內的按鈕，且按鈕沒有因權限被隱藏，判斷規則同按鈕的顯示）且不在 Selector 彈窗內時才加上 `Export`。標記為匯出的動作（標準的 `ExportExcel`）雖然送到自己的 controller，仍算作匯出：表格的標準匯出流程依賴它，維持相容。使用本地資料的表格（`use-local-data`）不寫參考Selector 的參考只有 `Selector`，彈窗內的表格只有 `List` |
| `u`／`n` | 使用者 ITCode／租戶（`CurrentTenant`）；匿名為空字串 |
| `c` | 頁面使用的連線 key（由伺服器決定，見 5.7） |
| `o` | 頁面的選單權限 url；頁面是 `[Public]`、`[AllRights]` 或選單公開時為空 |
| `f` | 只用於 `Update`：頁面允許編輯的屬性 |
| `e` | 到期時間 |

### 5.2 頁面的權限 url（`o`）

`PrivilegeFilter` 在需要選單權限的 action（沒有 `[AllRights]`／`[Public]`）通過判斷後，把 `controller.BaseUrl` 記到 `WTMContext.PagePrivilegeUrl`。TagHelper 寫入參考時帶上這個值；端點每次驗證都用 `IsAccessable(o)` 重新檢查，所以使用者失去頁面權限後，已經打開的頁面立刻失效（受使用者快取的更新影響）。`[AllRights]` 的頁面（任何已登入者都能打開）不需要重新檢查。

### 5.3 端點的判斷順序（`GenericEndpointGuard.Authorize`）

1. `GenericEndpoints:Mode = Legacy`：放行（舊行為）。
2. 沒有 view model 名稱：拒絕。
3. 有參考：依序檢查格式與版本、到期、使用者與租戶、view model 名稱（逐字比對，不對來路不明的字串做 `Type.GetType`）、目的、連線 key、頁面權限、`Update` 的欄位清單。全部通過才放行。連線 key 的處理：`GetPagingData`、`GetExportExcel` 與 `Selector` 比對請求帶的 `_DONOT_USE_CS`／`_DONOT_USE_CURRENTCS`（通過後以設定中該連線的拼法執行；`ExemptViewModels` 內的 view model 忽略請求的 key，使用伺服器選的連線，除非 `AllowRequestConnectionKey=true`；`Legacy` 模式維持原行為）；`GetEmptyData`、`UpdateModelProperty`、`GetExcelTemplate` 沒有連線參數，改為在參考綁定的連線上執行（見 5.7）。比較時去掉讀取副本的後綴（`x_1` 與 `x` 是同一個連線）。
4. 沒有有效參考時：view model 在 `ExemptViewModels` 內、呼叫者已登入、不是 `Update` 且型別確實是 `BaseVM` → 放行；否則拒絕。

額外：連線 key 若不在 `Connections` 內，一律拒絕（所有模式）。

### 5.4 Selector

`Selector` 維持 `[Public]`（公開頁面也可能有選擇器），但必須帶有效的參考：匿名者只能使用公開頁面寫出的、綁定匿名的參考。通過後把參考的頁面權限 url 沿用到 `WTMContext.PagePrivilegeUrl`，使 Selector 內的表格寫出的參考仍然綁定原頁面的權限。Selector 的連線 key（`_DONOT_USE_CURRENTCS`）也和參考比對。

### 5.5 拒絕時的回應

一律回 HTTP 200：帶 `layuisearch` 標頭（LayUI 表格）時加 `{"Code":403,...}`；其他加「沒有權限」文字（HTML），包括帶 `Authorization` 標頭的呼叫者（`PrivilegeFilter` 對這類呼叫者回 401/403 狀態碼，這裡不同）。拒絕原因只寫進伺服器 log（Warning，含 action、原因與使用者，不含參考與請求內容）。

### 5.6 沒有 Data Protection 時

TagHelper 取不到 `IDataProtectionProvider` 時不寫參考（頁面仍可正常顯示），端點則一律拒絕沒有有效參考的請求。也就是 fail closed。

### 5.7 連線由伺服器決定

動作的資料庫連線由伺服器決定：`[FixConnection]`、連線函式（`DataContextFilter._csfunc`）、預設連線。查詢字串的 `DONOTUSECSName` 預設**不再**影響連線，因此頁面參考記錄的連線 key 就是伺服器為該頁面選定的連線。需要保留舊行為的應用程式可設定 `GenericEndpoints:AllowRequestConnectionKey = true`（見第 7 節）。`GetEmptyData`、`UpdateModelProperty`、`GetExcelTemplate` 不接受請求指定的連線 key，所以在參考綁定的連線上執行：該 key 是頁面產生時由伺服器決定、並受簽章保護的，因此使用 `[FixConnection]` 或非預設資料庫的頁面（包括匯入頁的範本按鈕）照常運作。規則（`GenericEndpointGuard.TryResolveConnection`）：

- 參考的 key 必須仍是設定中的連線（`Connections`），否則拒絕；
- 參考的 key 為空（頁面使用預設連線）：沿用伺服器為該請求選的連線；
- 讀取（`GetEmptyData`、`GetExcelTemplate`）：基底連線或它的讀取副本，規則同 `[FixConnection]` 的動作；
- 寫入（`UpdateModelProperty`）：一律用基底連線，不用讀取副本；
- 比較請求與參考的連線 key 時，兩邊都去掉讀取副本的後綴（`GenericEndpointGuard.NormalizeConnectionKey`，規則同 `Utils.GetCS`），讀取時副本是隨機挑的，不會因此造成拒絕。

### 5.8 同一個 `_Framework` controller 的管理端點

`RemoveUserCacheByAccount`、`RemoveUserCacheByRole`、`RemoveUserCacheByGroup`（清除其他帳號的快取）現在要求呼叫者具有管理員角色（角色代碼 `001`，與框架其他僅限管理員的功能相同）；其他已登入者得到「沒有權限」的回應（帶 `Authorization` 標頭者得到 403）。框架與 `_Admin` 範本自己清除快取時是在伺服器端直接呼叫 `WTMContext.RemoveUserCache*`，不經過這些 HTTP 端點。

## 6. 下游自行呼叫端點的頁面

沒有任何框架 TagHelper 會呼叫 `GetEmptyData`／`UpdateModelProperty`。自行用 script 呼叫這些端點（或自己呼叫 `GetPagingData` 等）的頁面，在伺服器端取得參考：

```csharp
// 在 action 或 view（頁面本身已經過權限判斷）
var reference = Wtm.CreateGenericEndpointReference(typeof(MyListVM), GenericEndpointPurpose.List);
var editRef = Wtm.CreateGenericEndpointReference(typeof(MyVM), GenericEndpointPurpose.Update, editableFields: new[] { "Name", "Memo" });
```

並把它以 `_DONOT_USE_VMREF` 參數和 `_DONOT_USE_VMNAME` 一起送出。`Update` 的欄位清單是必要的：沒有列出的屬性不能被修改。

## 7. 相容設定（預設安全）

設定節 `GenericEndpoints`：

| 設定 | 預設 | 說明 |
|---|---|---|
| `Mode` | `Enforce` | `Legacy` 恢復舊行為（不檢查），啟動時輸出 Warning。只建議作為升級期間的暫時措施 |
| `ExemptViewModels` | 空 | 在 `Enforce` 下，列出的 view model（完整型別名稱，或以 `*` 結尾的前綴）可由**已登入**的使用者在不帶參考時使用（舊行為，只限這些 view model）。不適用於 `Update`，匿名者不適用。啟動時輸出 Warning |
| `ReferenceLifetimeMinutes` | `480` | 參考的有效分鐘數 |
| `AllowRequestConnectionKey` | `false` | `true`：允許查詢字串 `DONOTUSECSName` 為沒有 `[FixConnection]`、連線函式也沒有回傳的動作指定連線（舊行為），也允許 `ExemptViewModels` 內的 view model 採用請求帶的連線 key。啟用時，已登入的使用者可以為自己能打開的頁面選擇連線；啟動時輸出 Warning |

## 8. 剩餘風險與不在範圍的項目

- `Update` 的參考由頁面作者透過 `CreateGenericEndpointReference(..., editableFields)` 簽發，可編輯屬性的清單只會和作者列的一樣窄。
- `ExemptViewModels` 支援前綴（結尾 `*`），太短的前綴會讓底下所有 view model 都免參考；建議只寫完整型別名稱。
- 參考綁定帳號與租戶，不綁定登入工作階段；在到期（預設 480 分鐘）或頁面權限被移除之前一直有效。
- 檔案端點（`GetFile` 等）的連線參數，以及 `UpdateModelProperty` 的紀錄層級資料權限，不在這次變更內，另行處理。
- `ExemptViewModels` 內的 view model 沒有參考可比對連線 key，所以忽略請求帶的 key（見 5.7 與第 7 節）。
- 資料權限（`DPWhere`）不變；本修正不處理列級權限。`UpdateModelProperty` 依 id 載入的紀錄、以及它不儲存變更的問題（#284）都不在範圍。
- 在 `[AllRights]` 頁面上顯示的 view model，授權就是「任何已登入者」，與該頁相同。
- 本文件未列出的其他 `_Framework` action 的授權不在本次範圍。
- 由範本產生、已複製到下游專案的 `_Admin` controller 不在本次範圍；範本更新後，下游的複本可能需要套用相同的修改。

## 9. 測試

- `test/WalkingTec.Mvvm.Core.Test/GenericEndpointGuardTest.cs`：判斷邏輯（使用者、租戶、view model、目的、連線、到期、頁面權限、欄位清單、豁免、Legacy、竄改）。
- `test/WalkingTec.Mvvm.Core.Test/TagHelpers/LayUI/GenericEndpointReferenceTagHelperTests.cs`：TagHelper 寫入的參考能通過對應請求，且綁定使用者。
- `test/WalkingTec.Mvvm.Core.Test/RequestConnectionKeyTest.cs`、`FrameworkCacheEndpointAdministratorTest.cs`：連線 key 的選項與管理員判斷。
- `test/WalkingTec.Mvvm.E2E/Tests/Security/GenericEndpointAuthorizationTests.cs`：用匿名／無角色／受限角色／管理員的真實請求驗證六個端點、連線由伺服器決定、Selector 彈窗的參考只能分頁，以及一般角色（受限角色、無角色）的表格與 Selector 照常可用；既有的 LayUI E2E（表格、Selector、匯出、範本）維持通過；權限矩陣的期望表已更新（含 `RemoveUserCacheBy*`）。
