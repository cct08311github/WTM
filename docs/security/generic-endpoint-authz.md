# `_Framework` 通用資料端點的授權設計

> 狀態：實作於 `security/ghsa-mwxh-generic-endpoint-authz`（私有 fork，GHSA-mwxh-vh26-ph3h）。本文件在公開前須先經維護者審閱，並依 FORK.md 第 8 節改為中性措辭。

## 1. 要強化的是什麼

`src/WalkingTec.Mvvm.Mvc/_FrameworkController.cs` 的類別層級是 `[AllRights]`（任何已登入的使用者）。其中六個 action 依請求帶來的 `_DONOT_USE_VMNAME` 建立 view model：

| action | 用途 | 原本的授權 |
|---|---|---|
| `GetPagingData` | LayUI 表格取得分頁資料（Selector 內的表格走這一條） | 已登入即可 |
| `GetEmptyData` | 取得一列空資料 | 已登入即可 |
| `GetExportExcel` | 匯出 Excel | 已登入即可 |
| `GetExcelTemplate` | 下載匯入範本 | 已登入即可 |
| `UpdateModelProperty` | 修改單一屬性 | 已登入即可 |
| `Selector` | 選擇器彈窗（含 `Ids` 對應的已選資料） | `[Public]` |

這些端點沒有檢查「呼叫者是否有擁有該 view model 的功能（選單／action）的權限」。資料權限（`DPWhere`）仍然生效，但選單權限是下游最主要的保護，所以這些端點必須提供和功能頁相同的語意：`PrivilegeFilter`、`WTMContext.IsAccessable`、管理員角色、`[Public]`／`[AllRights]`、租戶。

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
  - 綁定使用者、租戶、連線與目的，且會到期，取得別人的參考也不能使用。
  - 不需要掃描 assembly 或反組譯。
- 缺點
  - 需要 Data Protection 金鑰（cookie 驗證本來就需要；多實例部署本來就要共用金鑰）。
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
| `p` | 目的（可組合）：`List`（GetPagingData、GetEmptyData）、`Selector`、`Export`、`Template`、`Update` |
| `u`／`n` | 使用者 ITCode／租戶（`CurrentTenant`）；匿名為空字串 |
| `c` | 頁面使用的連線 key |
| `o` | 頁面的選單權限 url；頁面是 `[Public]`、`[AllRights]` 或選單公開時為空 |
| `f` | 只用於 `Update`：頁面允許編輯的屬性 |
| `e` | 到期時間 |

### 5.2 頁面的權限 url（`o`）

`PrivilegeFilter` 在需要選單權限的 action（沒有 `[AllRights]`／`[Public]`）通過判斷後，把 `controller.BaseUrl` 記到 `WTMContext.PagePrivilegeUrl`。TagHelper 寫入參考時帶上這個值；端點每次驗證都用 `IsAccessable(o)` 重新檢查，所以使用者失去頁面權限後，已經打開的頁面立刻失效（受使用者快取的更新影響）。`[AllRights]` 的頁面（任何已登入者都能打開）不需要重新檢查。

### 5.3 端點的判斷順序（`GenericEndpointGuard.Authorize`）

1. `GenericEndpoints:Mode = Legacy`：放行（舊行為）。
2. 沒有 view model 名稱：拒絕。
3. 有參考：依序檢查格式與版本、到期、使用者與租戶、view model 名稱（逐字比對，不對來路不明的字串做 `Type.GetType`）、目的、連線 key（只有帶 `_DONOT_USE_CS`／`_DONOT_USE_CURRENTCS` 的端點）、頁面權限、`Update` 的欄位清單。全部通過才放行。
4. 沒有有效參考時：view model 在 `ExemptViewModels` 內、呼叫者已登入、不是 `Update` 且型別確實是 `BaseVM` → 放行；否則拒絕。

額外：連線 key 若不在 `Connections` 內，一律拒絕（所有模式）。

### 5.4 Selector

`Selector` 維持 `[Public]`（公開頁面也可能有選擇器），但必須帶有效的參考：匿名者只能使用公開頁面寫出的、綁定匿名的參考。通過後把參考的頁面權限 url 沿用到 `WTMContext.PagePrivilegeUrl`，使 Selector 內的表格寫出的參考仍然綁定原頁面的權限。Selector 的連線 key（`_DONOT_USE_CURRENTCS`）也和參考比對。

### 5.5 拒絕時的回應

沿用 `PrivilegeFilter` 的形式：帶 `layuisearch` 標頭（LayUI 表格）時回 200 加 `{"Code":403,...}`；其他回 200 加「沒有權限」文字（HTML）。拒絕原因只寫進伺服器 log（Warning，含 action、原因與使用者，不含參考與請求內容）。

### 5.6 沒有 Data Protection 時

TagHelper 取不到 `IDataProtectionProvider` 時不寫參考（頁面仍可正常顯示），端點則一律拒絕沒有有效參考的請求。也就是 fail closed。

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

## 8. 剩餘風險與不在範圍的項目

- 資料權限（`DPWhere`）不變；本修正不處理列級權限。`UpdateModelProperty` 依 id 載入的紀錄、以及它不儲存變更的問題（#284）都不在範圍。
- 在 `[AllRights]` 頁面上顯示的 view model，授權就是「任何已登入者」，與該頁相同。
- 其他 `_Framework` action 的授權不在本次範圍（已另行記錄在維護者的追蹤項目中）。
- 範本產生的 `_Admin` controller 的 `[AllRights]` 輔助 action 另有追蹤的公告；範本修正後，下游已複製的 `_Admin` 需要套用同樣的修改。

## 9. 測試

- `test/WalkingTec.Mvvm.Core.Test/GenericEndpointGuardTest.cs`：判斷邏輯（使用者、租戶、view model、目的、連線、到期、頁面權限、欄位清單、豁免、Legacy、竄改）。
- `test/WalkingTec.Mvvm.Core.Test/TagHelpers/LayUI/GenericEndpointReferenceTagHelperTests.cs`：TagHelper 寫入的參考能通過對應請求，且綁定使用者。
- `test/WalkingTec.Mvvm.E2E/Tests/Security/GenericEndpointAuthorizationTests.cs`：用匿名／無角色／受限角色／管理員的真實請求驗證六個端點；既有的 LayUI E2E（表格、Selector、匯出、範本）維持通過；權限矩陣的期望表已更新。
