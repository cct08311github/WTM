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

**本節的範圍是框架的 `/_Framework` 檔案端點。** demo／範本的 `FileApiController`（`/api/_file/*`）使用同一套機制，另見第 7 節。

沒有變更的部分：`WtmFileProvider.GetFile/Upload/DeleteFile`（下游在伺服器端呼叫，例如自己的匯入 VM；內嵌標記只在 HTTP 端點處理）、`DeletedFileIds` 與匯入的 `UploadFileId` 的信任方式。

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

回應：`GetFile` 一律加 `X-Content-Type-Options: nosniff`；`stream=true` 時依副檔名設定 `Content-Type`，瀏覽器會當作網站的一部分執行的檔案（`html`、`htm`、`xhtml`、`svg`、`xml`、`xsl`、`js`，以及副檔名不明但內容看起來像 HTML／SVG／script 的檔案）以 `attachment` 送出，其他（圖片、PDF、影片、音訊、純文字）維持 `inline`。

`Enforce` 下沒有有效參考時，不存在的檔案與無權取得的檔案得到相同的拒絕（不透露哪些 id 存在）。

`LegacyUploadedBefore` 與 `UploadTime` 都是伺服器的本地時間（`UploadTime` 以 `DateTime.Now` 寫入），時區或夏令時間調整會影響邊界附近的檔案；`UploadTime` 未設定（0001-01-01）的檔案不算早於截止時間；截止時間在未來時，啟動輸出 Warning。

記錄：拒絕與 `Audit` 放行都記錄端點、使用者、`Referer`（截為 256 字元、移除控制字元）與原因，不含檔案內容；未登入者的請求記在 Information（`IsFilePublic` 時可能是爬蟲），已登入者的記在 Warning。

`IsFilePublic=true` 時 `GetFile`／`ViewFile` 對未登入者開放（`PrivilegeFilter`），由端點決定要不要提供：頁面渲染的連結（參考）在到期之前匿名可用；只知道 id 的請求在 `Audit` 下提供並記錄，在 `Enforce` 下拒絕（內嵌與升級前的檔案除外）。

### 3.3 資料庫連線

請求的 `_DONOT_USE_CS` 不再選擇連線：

- 有效參考 → 使用參考中的連線（頁面產生時由伺服器決定、受簽章保護）。
- 沒有有效參考 → 預設連線或伺服器為該請求選的連線（讀取副本後綴不計）；其他已設定的連線只有列在 `FileEndpoints:RequestConnectionKeys` 時才採用，設定 `GenericEndpoints:AllowRequestConnectionKey=true` 則恢復舊行為。
- 不在 `Connections` 內的 key → 一律拒絕（原本是 500）。
- 伺服器決定的連線為空時，呼叫 `CreateDC()` 不帶 key，租戶指定的資料庫照常路由。

### 3.4 應用程式的規則：`IFileAccessPolicy`

參考（或內嵌、升級前）通過之後、提供檔案之前呼叫 `IFileAccessPolicy.IsAllowed(FileAccessRequest)`（檔案 id、目的、通過的方式、使用者、租戶、`WTMContext`）。預設實作 `AllowAllFileAccessPolicy` 允許所有請求；在 `AddFrameworkService` 之前自行註冊會取代它（框架用 `TryAddSingleton`）。`Legacy` 模式不呼叫。**自訂的規則在 `Audit` 模式下已經生效**：`Audit` 只放寬「沒有有效參考」這一項。規則丟出例外時請求被拒絕，並記錄 Error。

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

Core.Test：`FileEndpointGuardTest`（簽發、驗證、竄改、到期、使用者、租戶與所屬租戶、目的、連線、模式、不存在的檔案、內嵌標記、截止日期）、`FileServingTest`（內容類型與 attachment 判斷）、`FileEndpointContextTest`（連結、記錄、政策、`IUIService` 相容、按鈕）。E2E：`FileEndpointTests`（預設 `Audit`；環境變數 `E2E_FILE_ENDPOINTS_MODE=Enforce|Legacy` 以其他模式跑整個 LayUI 測試站台）、`LayUiUploadTests`、`FrameworkEndpointTests`、權限矩陣（含以 `Enforce` 啟動的 `file-enforce` 設定）。

## 7. 單頁應用程式的檔案 API（`/api/_file/*`）

範本的 `FileApiController` 的每個 action 呼叫 `WalkingTec.Mvvm.Mvc.SpaFileApi` 一次，規則與第 3 節相同（參考、`FileEndpoints:Mode`、連線、`IFileAccessPolicy`），差別如下：

- **讀取用 capability**：SPA 的 `<img>` 不能帶憑證，所以伺服器在資料旁寫一個不綁定使用者的短期參考（`Wtm.CreateFileCapability`，用途 `Read`，綁定檔案、連線、租戶與到期時間）。列表的純文字回應是 `<欄位>__fref`，詳細資料是 `FileRefs`（id → 參考），上傳回應是 `Ref`。持有它的人在到期前可以顯示、下載、取得該檔案的名稱；它不含刪除與上傳。已登入且租戶不同的呼叫者不能使用另一個租戶簽發的 capability；未登入的呼叫者依 capability 的租戶讀取資料（租戶有自己的資料庫時用該資料庫）。
- **未登入且 `IsFilePublic=false`**：`Enforce` 只接受有效參考；另外，內嵌的檔案與 `LegacyUploadedBefore` 之前的檔案可以只用 id **顯示**（`GetFile`、`GetUserPhoto`），不能下載或查名稱；`Audit` 提供並記錄；`Legacy` 照舊。`IsFilePublic=true` 時依第 3.2 節。
- **刪除**：需要登入與發給該使用者、該檔案的 `Delete` 參考（上傳回應的 `DeleteRef`，綁定使用者、租戶、到期）。沒有時拒絕並記錄，除 `Legacy` 外的所有模式（含 `Audit`）。
- **頭像**：一個檔案若是租戶內可見、有效使用者的 `PhotoId` 且為圖片類型（不含 SVG），`GetUserPhoto` 不需參考即可顯示（`Avatar`，政策仍會被詢問）。
- **`GetFileInfo`** 在所有模式只回傳 `Id`、`FileName`、`FileExt`、`Length`、`UploadTime`。
- **上傳**：需要登入，移除 `extra` 的內嵌標記，連線依參考決定；`UploadForEditor` 由伺服器加內嵌標記。回應保留 `Id`、`Name`、`errno`、`data.url`，新增 `Url`、`Ref`、`DeleteRef`。
- 拒絕為 HTTP 403。回應標頭見第 3.2 節（`nosniff`，內容類型來自 `FileServing`，會被瀏覽器執行的內容用 `attachment`，`DownloadFile` 一律 `attachment`）。
- 未更新的副本：路由在 `api/_file` 的 controller 暴露 `DeletedFile`／`GetFileInfo` 而沒有 `[FileEndpoint]` 標記，啟動時輸出 Warning。更新方式見 [vue3-file-api-downstream.md](vue3-file-api-downstream.md)。


