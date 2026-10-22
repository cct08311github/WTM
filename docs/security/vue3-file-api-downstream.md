# 下游副本的最小修改：範本檔案 API（`/api/_file/*`）

> 適用於從 Vue3 範本複製了 `FileApiController` 與 ClientApp 的專案。措辭依 FORK.md 第 8 節，只描述要做的修改。設計見 [file-endpoint-authz.md](file-endpoint-authz.md) 第 7 節與 FORK.md 第 29 節。

## 0. 前提與部署順序

- 前提：已升級到 10.0.0.11，再加上本版（框架與範本需同一版本）。
- **伺服器與用戶端一起部署**。只更新伺服器、用戶端仍是舊的：移除本次新上傳的檔案會得到 403（舊用戶端沒有帶 `DeleteRef`）；舊用戶端在 `Enforce` 下以 id 取檔會得到 403。
- 在自己的用戶端搜尋 `GetFileInfo` 的使用處：回應不再含 `Path`、`FileData` 等欄位，只有 `Id`、`FileName`、`FileExt`、`Length`、`UploadTime`。
- `[FileEndpoint]` 標記只在 action 同時呼叫 `SpaFileApi` 時才有意義；只加標記不會套用任何規則。
- 新的列舉成員 `FileEndpointAdmission.Avatar`：自己的 `IFileAccessPolicy` 若對 `Admission` 做 switch，需要處理它（`GetUserPhoto` 對使用者頭像會以此通過方式呼叫政策，可據此限制頭像）。
- 自己寫的頭像欄位（不經 `GetUserPhoto` 與 `PhotoId` 的規則）不會得到頭像的通過方式。
- 副本若有自己的個人資料編輯或註冊流程，新增或變更 `PhotoId` 時要做同樣的檢查：`Wtm.IsPhotoAssignable(DC, fileId)`（範本的 `FrameworkUserVM.PhotoIsAcceptable`），註冊流程不要採用客戶端的 `PhotoId`。
- 頭像的結果快取 30 秒：移除頭像後最多 30 秒仍可顯示。導覽列的頭像用 `CheckUserInfo` 回應的 `Attributes.PhotoRef`（範本的 `AccountController.CheckUserInfo`），沒有時退回 `GetUserPhoto`。

升級框架後，副本**照常運作**（預設 `FileEndpoints:Mode=Audit`），但副本的 controller 沒有套用新的規則：啟動時會有 Warning（`DeletedFile`／`GetFileInfo` 沒有 `[FileEndpoint]`）。要套用，做下列修改。

## 1. 伺服器

1. 確認 `ImageDecodeGuard.cs` 存在（範本的 `Areas/_Admin/ImageDecodeGuard.cs`）；沒有就複製。
2. 把 `Areas/_Admin/Controllers/FileApiController.cs` 換成範本的版本。每個 action 是 `SpaFileApi` 的一次呼叫：

```csharp
[HttpGet("[action]/{id}")] [ActionDescription("GetFile")] [Public] [FileEndpoint(PublicWhenFilesPublic = true)]
public IActionResult GetFile([FromServices] WtmFileProvider fp, string id, string csName = null, int? width = null, int? height = null, string _DONOT_USE_FREF = null)
    => SpaFileApi.GetFile(this, fp, id, csName, width, height, _DONOT_USE_FREF, ResizeImage);
```

   所有 action（`Upload`、`UploadImage`、`UploadForEditor`、`GetFileName`、`GetFile`、`GetFileInfo`、`GetUserPhoto`、`DownloadFile`、`DeletedFile`）都要有 `_DONOT_USE_FREF` 與 `csName` 參數和 `[FileEndpoint]` 標記；讀取類保留 `[Public]`；`DeletedFile` 接受 `[HttpGet]`、`[HttpPost]`、`[HttpDelete]`。`ResizeImage` 是範本 controller 裡的私有方法（用 `ImageDecodeGuard`），一併複製。
3. `AccountController.Reg`：不要把客戶端的 `PhotoId` 寫入新使用者（範本寫 `PhotoId = null`）；`CheckUserInfo` 在 `forapi.Attributes["PhotoRef"]` 放 `Wtm.CreateFileCapability(PhotoId)`；`FrameworkUserVM` 的 `DoAddAsync`／`DoEditAsync` 先呼叫 `PhotoIsAcceptable`。
4. 自己寫的 view model 列表若有下載／檢視欄位，不需修改：純文字列表自動加 `<欄位>__fref`，詳細資料自動有 `FileRefs`。

## 2. 用戶端（ClientApp/src）

1. 新增 `utils/fileRefs.ts`（範本的檔案）。
2. `utils/request.ts` 的回應攔截器：在 `return response.data` 前呼叫 `collect(response.data)`（非 Blob 時）。
3. `utils/storage.ts` 的 `Session.clear()`：呼叫 `clearFileRefs()`。
4. `api/file/index.ts`：`getFile` 改呼叫 `/api/_file/GetFile/{id}`，`getName`／`downloadFile` 帶 `params: refParams(id)`；`deleteFile` 只在 `deleteRefFor(id)` 有值時呼叫 `DELETE /api/_file/DeletedFile/{id}?_DONOT_USE_FREF=…`，沒有就不呼叫。
5. 元件改用 `fileUrl(id, { width, height })`／`remember(res.Id, res.Ref)`／`rememberDelete(res.Id, res.DeleteRef)`：`components/uploadImage`、`components/uploadFile`、`components/table`（圖片欄）、`components/importor`、`stores/userInfo.ts`（頭像改用 `/api/_file/GetUserPhoto/{PhotoId}?width=25&height=25`）。
6. `components/editor`：`uploadImage.server` 改為 `/api/_file/UploadForEditor`。
7. 自己組 `/api/_file/...` 位址的頁面：用 `fileUrl(id)`／`downloadUrl(id)`；刪除只對本次上傳的檔案（有 `DeleteRef`）呼叫。

已用範本產生器產生的頁面（`wtm-upload-image`、`wtm-upload-file`、`WtmImportor`、`WtmEditor`、表格的 `type: 'image'`）不需修改。

## 3. 驗證與切換

1. 啟動後不應再有「`DeletedFile` … does not use the file API helper」的 Warning。
2. 在 `Audit` 下操作各頁面，觀察 Warning（沒有有效參考的請求：端點、使用者、`Referer`）。
3. 無 Warning 後設 `FileEndpoints:Mode=Enforce`（Data Protection 金鑰必須持久化，多實例要共用）。
4. 回滾：`FileEndpoints:Mode=Legacy`。刪除仍需要登入；不分模式（除 `Legacy`）都需要 `DeleteRef`。

## 4. 行為變更

- 移除已儲存的附件只清除欄位，取消編輯會保留檔案；被取代的檔案留在原處（本版不清理，`DoEdit` 與 `DeletedFileIds` 不變）。
- `GetFileInfo` 只回傳 `Id`、`FileName`、`FileExt`、`Length`、`UploadTime`。
- 拒絕為 HTTP 403。
