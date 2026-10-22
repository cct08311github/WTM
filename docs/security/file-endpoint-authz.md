# `_Framework` 檔案端點的授權設計

> 本文件說明檔案端點如何檢查請求、連線如何決定、升級步驟與相容設定。措辭依 FORK.md 第 8 節，只描述檢查的內容。

## 1. 範圍

`src/WalkingTec.Mvvm.Mvc/_FrameworkController.cs`（類別層級 `[AllRights]`）中處理檔案的 action：

| action | 用途 | 參考的目的 |
|---|---|---|
| `GetFile`（`stream=true`） | 在頁面中顯示檔案 | `View` |
| `GetFile`（沒有 `stream`） | 下載檔案 | `Download` |
| `ViewFile` | 依副檔名輸出 `img`／`video`／`embed` | `View` |
| `GetFileName` | 檔名 | `Name` |
| `Upload`、`UploadImage`、`UploadForLayUIRichTextBox`、`UploadForLayUIUEditor` | 上傳 | 不要求參考；參考（目的 `Upload`）只用來帶頁面的連線 |

沒有變更的部分：`WtmFileProvider.GetFile/Upload/DeleteFile`（下游在伺服器端呼叫，例如自己的匯入 VM）、demo 範本的 `FileApiController`（`/api/_file/*`）、`DeletedFileIds` 與匯入的 `UploadFileId` 的信任方式。

## 2. 限制條件

- LayUI 必須維持可用（FORK.md 第 3 節）：`wt:upload`、`wt:multiupload`、`wt:image`、`wt:display`、`wt:richtextbox`、`wt:ueditor`、表格的下載／檢視按鈕。TagHelper 產生的頁面不需修改。
- 盡量不 breaking：本版預設模式是 `Audit`（照常提供檔案，只記錄沒有有效參考的請求）；`IUIService` 是公開介面，只新增有預設實作的方法。
- 預設安全地決定連線，同時保留帶 `[FixConnection]`、第二個資料庫的頁面可運作的明確辦法。

## 3. 檢查的內容

### 3.1 檔案參考

伺服器渲染的每個檔案連結都帶一個以 ASP.NET Core Data Protection 保護的不透明字串（`_DONOT_USE_FREF`），內容為：檔案 id、目的（可組合）、連線 key、使用者（匿名頁面為空）、租戶、到期時間。端點驗證簽章、到期、使用者與租戶（使用者為空的參考對該頁面的所有對象有效）、檔案 id（不分大小寫）與目的。

簽發處：LayUI TagHelper（`wt:upload`、`wt:multiupload`、`wt:image`、`wt:display`）、`LayuiUIService.MakeDownloadButtonWithReference／MakeViewButtonWithReference`（由表格的下載／檢視欄 `ListVMExtension` 呼叫）、`Utils.GetUrlByFileAttachmentId(WTMContext, ...)`、公開方法 `Wtm.CreateFileReference(id, purposes, connectionKey)` 與 `Wtm.GetFileUrl(id, purpose, connectionKey)`。上傳的回應在 `Data` 增加 `Url`、`Ref`（原有的 `Id`、`Name` 不變），TagHelper 的 script 使用 `res.Data.Url ||` 原本的位址。

### 3.2 模式（`FileEndpoints:Mode`）

| 模式 | 行為 |
|---|---|
| `Legacy` | 與檢查出現之前相同。連線的決定方式（3.3）仍然適用，除非設定 `GenericEndpoints:AllowRequestConnectionKey=true` |
| `Audit`（**預設**） | 簽發並驗證參考，沒有有效參考的請求照常提供，並記錄 Warning：端點、使用者、`Referer`、原因；不記錄檔案內容 |
| `Enforce` | 沒有有效參考的請求被拒絕，使用框架的一般拒絕形式（HTTP 200，內容為「沒有權限」，與通用資料端點相同） |

在 `Enforce` 下，下列情況不需要參考：

1. **內嵌檔案**：`UploadForLayUIRichTextBox`、`UploadForLayUIUEditor` 上傳的檔案，伺服器在 `ExtraInfo` 加上保留前綴（`wtm:embedded;`）。一般的 `Upload`／`UploadImage` 會移除用戶端在 `extra` 帶來的這個前綴，所以用戶端不能自己標記。內嵌檔案維持原樣：已登入者可用 id 取得；`IsFilePublic=true` 時匿名者也可以（編輯器的內容會比任何參考活得久）。
2. **升級前上傳的檔案**：`FileEndpoints:LegacyUploadedBefore` 設定一個時間（伺服器本地時間，與 `FileAttachment.UploadTime` 相同），`UploadTime` 早於它的檔案維持用 id 取得。未設定時不保留。

`IsFilePublic=true` 時 `GetFile`／`ViewFile` 對未登入者開放（`PrivilegeFilter`），由端點決定要不要提供：頁面渲染的連結（參考）在到期之前匿名可用；只知道 id 的請求在 `Audit` 下提供並記錄，在 `Enforce` 下拒絕（內嵌與升級前的檔案除外）。

### 3.3 資料庫連線

請求的 `_DONOT_USE_CS` 不再選擇連線：

- 有效參考 → 使用參考中的連線（頁面產生時由伺服器決定、受簽章保護）。
- 沒有有效參考 → 預設連線或伺服器為該請求選的連線（讀取副本後綴不計）；其他已設定的連線只有列在 `FileEndpoints:RequestConnectionKeys` 時才採用，設定 `GenericEndpoints:AllowRequestConnectionKey=true` 則恢復舊行為。
- 不在 `Connections` 內的 key → 一律拒絕（原本是 500）。
- 伺服器決定的連線為空時，呼叫 `CreateDC()` 不帶 key，租戶指定的資料庫照常路由。

### 3.4 應用程式的規則：`IFileAccessPolicy`

參考（或內嵌、升級前）通過之後、提供檔案之前呼叫 `IFileAccessPolicy.IsAllowed(FileAccessRequest)`（檔案 id、目的、通過的方式、使用者、租戶、`WTMContext`）。預設實作 `AllowAllFileAccessPolicy` 允許所有請求；在 `AddFrameworkService` 之前自行註冊會取代它（框架用 `TryAddSingleton`）。`Legacy` 模式不呼叫。

## 4. 為什麼是「頁面簽發的參考」

與通用資料端點相同的取捨（見 generic-endpoint-authz.md）：授權的依據是「使用者看得到渲染這個連結的頁面」，不需要在每個檔案上宣告擁有者，也不改資料表；需要 Data Protection 金鑰持久化，頁面開著超過有效期限（預設 480 分鐘）要重新整理。需要更細的規則（例如依記錄擁有者）用 `IFileAccessPolicy`。

## 5. 設定

| 設定 | 預設 | 說明 |
|---|---|---|
| `FileEndpoints:Mode` | `Audit` | `Legacy`／`Audit`／`Enforce` |
| `FileEndpoints:ReferenceLifetimeMinutes` | `480` | 參考的有效分鐘數（小於 1 視為預設） |
| `FileEndpoints:LegacyUploadedBefore` | 未設定 | 早於此時間上傳的檔案，在 `Enforce` 下維持用 id 取得 |
| `FileEndpoints:RequestConnectionKeys` | 空 | 沒有參考的請求可以指定的其他連線 |

## 6. 測試

Core.Test：`FileEndpointGuardTest`（簽發、驗證、竄改、到期、使用者、租戶、目的、連線、模式、內嵌標記、截止日期）、`FileEndpointContextTest`（連結、記錄、政策、`IUIService` 相容、按鈕）。E2E：`FileEndpointTests`（預設 `Audit`；環境變數 `E2E_FILE_ENDPOINTS_MODE=Enforce|Legacy` 以其他模式跑整個 LayUI 測試站台）、`LayUiUploadTests`、`FrameworkEndpointTests`、兩份權限矩陣表。
