# Fork 維護政策

本 repo（`cct08311github/WTM`）是 [dotnetcore/WTM](https://github.com/dotnetcore/WTM) 的 fork，主分支為 `dotnet10`。本文件說明這個 fork 的定位、維護原則，以及與上游同步的方式。人與 agent 都依照本文件執行。

## 1. 定位

- **貼近上游**：以上游 `dotnet10` 為基準，定期同步，不做大規模分岔
- **安全修補**：修正上游尚未處理的安全問題
- **依下游需要新增功能**：只在下游系統確實需要時才加，而且一律以可選開關（opt-in）的方式提供

## 2. 維護原則

1. **相容性優先，盡量不做 breaking change**：任何改變既有行為的變更（包括「預設開啟的強化」）都視為 breaking
2. **非 breaking 不可時**，動工前要先在 Issue 寫好以下內容，經維護者確認後才能實作：
   - 為什麼必要
   - 考慮過的替代方案
   - 受影響的範圍
   - 遷移步驟
   - 回滾方式（尤其是資料格式這類不可逆的變更）
3. **優先採用**：可選開關、相容層、自動且可回溯的遷移、明確的逃生口設定
4. **流程**：先有 Issue，一個 Issue 對應一個 branch、一個 PR；PR 要用 `Closes #N` 連結 Issue，並把 PR URL 回貼到 Issue。跑完相關測試才能開 PR
5. **commit 格式**：`type(scope): description`，body 說明為什麼要改

## 3. LayUI 前端保護政策

下游系統以 WTM 的 LayUI（伺服器端 Razor TagHelpers）前端開發。上游 v10 已宣告「LayUI 不保證可用」，並移除了 LayUI demo。**本 fork 保證 LayUI 前端持續可用，不會廢棄。**

受保護路徑（同步上游或重構時，不接受刪除、棄用或不相容的修改）：

| 路徑 | 內容 |
|---|---|
| `src/WalkingTec.Mvvm.TagHelpers.LayUI/**` | LayUI TagHelpers（表單、DataTable、Selector、Upload、Tree…） |
| `src/WalkingTec.Mvvm.Mvc/framework_layui.js` | LayUI 前端框架腳本（嵌入式資源，經由 `/_js` 提供） |
| `src/WalkingTec.Mvvm.Mvc/Views/**` | `_Framework`、`_CodeGen`、`_Workflow` 的嵌入式 view |
| `src/WalkingTec.Mvvm.Mvc/GeneratorFiles/Mvc/**` | LayUI MVC 程式碼產生器範本 |
| `src/WalkingTec.Mvvm.Mvc/_FrameworkController.cs` | LayUI 使用的 endpoint（Selector、GetPagingData、匯出、上傳…） |
| `src/WalkingTec.Mvvm.Mvc/Filters/PrivilegeFilter.cs`、`src/WalkingTec.Mvvm.Mvc/Helper/FResultExtension.cs` | `layuisearch` header 處理、`layui.index.render()` 輸出 |
| `src/WalkingTec.Mvvm.Core`（`BasePagedListVM`、`BaseTemplateVM`、`BaseImportVM`、`LayUIMenu`／`MenuExtension`） | LayUI 頁面所依賴的匯出、範本、匯入與選單 |

自動化防線：TagHelper 的呈現與編碼測試（#23），在 CI 中執行。這些測試失敗時，PR（包括同步上游的 PR）不得合併。

## 4. 版號規則

- 套件 ID 與上游相同（`WalkingTec.Mvvm.Core`、`.Mvc`、`.TagHelpers.LayUI`），版號用**四段**：`<上游版本>.<fork 修訂號>`，例如 `10.0.0.3`
  - 上游在 nuget.org 上只發佈三段版號，所以永遠不會撞號；舊 fork 用過的三段版號也不會撞
  - `10.0.0.3` > `10.0.0`，fork 版本一定高於它所依據的上游版本
- `version.props`、`CHANGELOG.md` **與上游保持逐位元組相同，不在 fork 中修改**
- fork 修訂號 `ForkRevision` 放在 `fork.props`：每發佈一個 fork 版本就加 1；合併了上游的版號升級後，重設為 1
- fork 的變更記錄寫在 `CHANGELOG.fork.md`

### 發佈通道：GitHub Packages（#27）

- feed URL：`https://nuget.pkg.github.com/cct08311github/index.json`（不發佈到 nuget.org）
- 套件：`WalkingTec.Mvvm.Core`、`WalkingTec.Mvvm.Mvc`、`WalkingTec.Mvvm.TagHelpers.LayUI`，三個套件版號相同，彼此的相依也指向同一個版號。第一個發佈版本是 `10.0.0.4`

#### 發佈步驟

1. 發佈準備的 PR（`ForkRevision` 加 1、`CHANGELOG.fork.md` 新增該版號的段落）合併到 `dotnet10`
2. 維護者在合併後的 commit 打 tag 並 push：

   ```bash
   git tag v10.0.0.4 <commit>
   git push origin v10.0.0.4
   ```

3. `.github/workflows/publish.yml` 由 tag 觸發，依序執行：
   - 驗證 tag 與 `version.props` + `fork.props` 推算出的版號一致，不一致就失敗
   - build 與 Core 測試
   - `dotnet pack -c Release` 打包三個套件
   - 檢查 nupkg 版號與 nuspec 中彼此相依的版號
   - 用 workflow 的 `GITHUB_TOKEN` push 到 GitHub Packages

   也可以從 Actions 頁面手動執行（`workflow_dispatch`，指定既有的 tag）重跑；已存在的版號會略過，不會覆蓋
4. **不手動 push 套件**。已發佈的版號不可重複使用；有問題就發佈下一個修訂號

#### 下游設定（`nuget.config`）

GitHub Packages 的 NuGet feed 即使是 public repo 也需要驗證。取用端需要一個具有 **`read:packages`** 權限的 token（GitHub classic personal access token）。**token 不可寫進 `nuget.config` 或任何版控檔案**，一律從環境變數讀取：本機放在使用者層級的環境變數，CI 放在該平台的 secret。

用 [Package Source Mapping](https://learn.microsoft.com/nuget/consume-packages/package-source-mapping) 讓 `WalkingTec.Mvvm.*` **只**從 GitHub Packages 取得，其他套件走 nuget.org。上游在 nuget.org 上有同名套件，沒有 mapping 時 NuGet 可能從任一個來源取得，造成混用。

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
    <add key="wtm-fork" value="https://nuget.pkg.github.com/cct08311github/index.json" />
  </packageSources>

  <packageSourceMapping>
    <packageSource key="wtm-fork">
      <package pattern="WalkingTec.Mvvm.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>

  <!-- 憑證從環境變數讀取，不要把實際的值寫進這個檔案 -->
  <packageSourceCredentials>
    <wtm-fork>
      <add key="Username" value="%WTM_PACKAGES_USER%" />
      <add key="ClearTextPassword" value="%WTM_PACKAGES_TOKEN%" />
    </wtm-fork>
  </packageSourceCredentials>
</configuration>
```

- `WTM_PACKAGES_USER`：GitHub 帳號名稱；`WTM_PACKAGES_TOKEN`：具 `read:packages` 權限的 token
- 較具體的 pattern 優先，所以 `WalkingTec.Mvvm.*` 只會從 `wtm-fork` 取得，其餘套件比對到 `*`，從 nuget.org 取得
- 套件版號要寫完整的四段式，例如 `<PackageReference Include="WalkingTec.Mvvm.Mvc" Version="10.0.0.4" />`

#### 第一次 restore 前：清掉 NuGet 快取中的 `walkingtec.*`

舊 fork 或上游的同名套件可能還留在本機快取。NuGet 會優先使用快取中同 ID、同版號的套件，不會再確認來源，所以在每台曾經 restore 過 WTM 套件的開發機與建置機上，第一次改用本 feed 之前先清掉：

```bash
rm -rf ~/.nuget/packages/walkingtec.*
dotnet nuget locals http-cache --clear
```

Windows 的路徑是 `%USERPROFILE%\.nuget\packages\walkingtec.*`；快取位置若有自訂，用 `dotnet nuget locals global-packages --list` 確認。

## 5. 與上游同步

### 自動檢查

`.github/workflows/upstream-check.yml` 每週執行一次（也可以手動觸發），比對上游 `dotnet10`。如果上游有新的 commit，就開一張 `upstream-sync` Issue，列出 commit 清單，並標出有動到第 3 節受保護路徑的部分。這個 workflow **只開 Issue，不會自動合併**。

### 同步步驟（runbook）

```bash
git clone https://github.com/cct08311github/WTM.git /tmp/wtm-sync
cd /tmp/wtm-sync
git remote add upstream https://github.com/dotnetcore/WTM.git
git fetch upstream dotnet10
git switch -c sync/upstream-$(date +%Y%m%d) origin/dotnet10
git merge --no-ff upstream/dotnet10
```

處理衝突的原則：

1. `version.props`、`CHANGELOG.md`：一律採用上游版本。fork 不會改這兩個檔案，正常情況下不會衝突
2. 上游的 `VersionPrefix` 有變動時：把 `fork.props` 的 `ForkRevision` 重設為 1
3. 動到第 3 節受保護路徑時：逐一檢查。上游的刪除或棄用不接受，用 `git checkout origin/dotnet10 -- <path>` 還原；不相容的修改要寫進 PR 說明，交由維護者決定
4. 與第 6 節分岔清單有關的衝突：保留 fork 的修正，同時吸收上游的變更

之後跑 `dotnet build WalkingTec.Mvvm.sln -c Release` 與 `dotnet test`，開 PR 並加上 `upstream-sync` label、連結對應的 Issue。

**同步 PR 必須用「Create a merge commit」合併，不可以 squash。** squash 會切斷與上游的共同祖先，下次同步時同樣的衝突會再出現一次。

同步 PR 一律視為高風險，由維護者確認後才能合併。

## 6. 與上游的分岔清單

被 fork 修改過的上游檔案。每次新增修改都要更新這張表，同步上游時依這張表處理衝突。

| 檔案 | 修改內容 | Issue |
|---|---|---|
| `common.props` | 新增 1 行 `<Import Project=".\fork.props" />` | #30 |
| `WalkingTec.Mvvm.sln` | 加回 `test/WalkingTec.Mvvm.Admin.Test`；新增 `test/WalkingTec.Mvvm.LayUI.E2EHost`、`test/WalkingTec.Mvvm.E2E`、`test/WalkingTec.Mvvm.E2E.Instrumentation` | #18、#270、#272 |
| `src/WalkingTec.Mvvm.Core/WTMContext.cs` | remote token 在使用前完整驗證 | #1 |
| `src/WalkingTec.Mvvm.Core/ConfigOptions/JwtOptions.cs` | `CreateValidationParameters()`／`CreateBearerValidationParameters()`、不安全金鑰檢查、`AllowInsecureKey`、`AllowTokensWithoutExpiry`、熱重載偵測 | #1、#2、#3、#15、#21、#33 |
| `src/WalkingTec.Mvvm.Core/ConfigOptions/Configs.cs` | `ExposeExceptionDetails`、`BufferJsonBodyOnly`、`WorkflowOptions` | #26、#17、#13 |
| `src/WalkingTec.Mvvm.Core/ConfigOptions/Cors.cs` | `AllowCredentialsWithAnyOrigin` | #12 |
| `src/WalkingTec.Mvvm.Core/BaseImportVM.cs` | 依欄位參照讀取儲存格 | #86 |
| `src/WalkingTec.Mvvm.Core/BasePagedListVM.cs` | 匯出 zip 路徑改用 `Path.Combine`；stylesheet 符合 schema；`SetFormat` 回傳 `ColumnFormatInfo`／`List<ColumnFormatInfo>` 的列匯出按鈕文字，不再寫入型別名稱 | #89、#87、#289 |
| `src/WalkingTec.Mvvm.Core/BaseTemplateVM.cs` | 範本的元素順序、ARGB 色碼、保護密碼的 hash | #87 |
| `src/WalkingTec.Mvvm.Core/Support/FileHandlers/WtmLocalFileHandler.cs` | 上傳路徑限制在根目錄內、檔名與副檔名清理、group fallback、根目錄外的已存路徑記 warning | #11 |
| `src/WalkingTec.Mvvm.Core/Support/FileHandlers/{WtmOssFileHandler,WtmDataBaseFileHandler,WtmFileProvider}.cs` | OSS key 驗證、存入 DB 的檔名與副檔名清理（共用 `FileNameSanitizer`） | #128 |
| `src/WalkingTec.Mvvm.Core/OpenXmlHelper.cs` | `GetColumnIndex`、`ToArgb`、`LegacyPasswordHash` | #86、#87 |
| `src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj`、`src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj` | 安全性的依賴版本固定與升級（每個都附 `<!-- security: ... -->` 註解） | #14 |
| `src/WalkingTec.Mvvm.Mvc/Helper/FrameworkServiceExtension.cs` | 啟動時的金鑰、IsQuickDebug、CORS、到期政策檢查、Oracle `Version` 警告；JwtBearer 使用共用驗證參數；CORS policy 改由 `WtmCorsHelper` 建立；熱重載偵測；依 `EnableWorkflow` 註冊 `WorkflowApiDisabledConvention`（false）或 `WorkflowApiStubRemovalConvention`（true） | #2、#3、#12、#21、#26、#158、#13、#210 |
| `src/WalkingTec.Mvvm.Mvc/_WorkflowApiController.cs` | 5 個查詢 action 移除 `[Public]`（需登入）；查詢改呼叫 `WorkflowLookup`（`HasMainHost` 的 `RedirectCall` 分支不變） | #13 |
| `src/WalkingTec.Mvvm.Core/WorkFlow/ApproveActivity.cs` | 設計器下拉選項與審批執行時查使用者／群組／主管／角色，改為直接查資料庫（`HasMainHost` 時改直接呼叫 mainhost），不再以無憑證的 HTTP 自我呼叫 `_workflowapi` | #13 |
| `src/WalkingTec.Mvvm.Mvc/Helper/WtmMiddleware.cs` | 非同步讀取 body、`BufferJsonBodyOnly`；Elsa `/v1/*` 端點一律要求登入；`/v1/activities` 在程式內取得 Elsa 的活動描述（不再 HTTP 自我呼叫；10.0.0.6 以前的內部呼叫目標驗證與憑證轉發已移除） | #17、#13、#172、#208、#216、#225、#256 |
| `src/WalkingTec.Mvvm.Mvc/Helper/WorkflowActivitiesGuard.cs`、`WorkflowActivitiesDescriber.cs` | 新增：Elsa `/v1/*` 端點的登入檢查；複製 Elsa `Activities.List` 的邏輯在程式內產生活動清單 JSON | #208、#216、#225、#256 |
| `src/WalkingTec.Mvvm.Mvc/Filters/FrameworkFilter.cs` | 依 `ExposeExceptionDetails` 決定是否輸出例外細節 | #26 |
| `src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/{Upload,MultiUpload,UEditor}TagHelper.cs` | 安全取得表單的 model | #88 |
| `src/WalkingTec.Mvvm.TagHelpers.LayUI/**`（Form、Tree、LinkButton、DataTable、Chart、`BaseButton`、`BaseFieldTag`、`BaseElementTag`、`LayuiUIService`…） | 嵌入 JS 字串的值改用 `JsStringEncoder` 轉義；HTML 屬性值改用 `HtmlAttributeEncoder` 編碼；選用的 HTML 內容編碼（`UIOptions:EncodeHtmlContent`；`wt:display` 對已編碼的值只編碼一次） | #24、#25、#286 |
| `src/WalkingTec.Mvvm.Core/ConfigOptions/UIOptions.cs`、`src/WalkingTec.Mvvm.Core/Extensions/ListVMExtension.cs`、`src/WalkingTec.Mvvm.Core/Support/HtmlAttributeEncoder.cs` | `EncodeHtmlContent` 選項、表格儲存格的一般值依設定編碼（已編碼的值只編碼一次） | #24、#286 |
| `src/WalkingTec.Mvvm.Mvc/Filters/PrivilegeFilter.cs`、`FrameworkFilter.cs` | 嵌入 JS 字串的值改用 `JsStringEncoder` 轉義 | #24 |
| `src/WalkingTec.Mvvm.Mvc/Filters/PrivilegeFilter.cs` | 需要選單權限的 action 通過後，把頁面 url 記到 `WTMContext.PagePrivilegeUrl`（供通用資料端點的參考使用，3 行） | 第 24 節 |
| `src/WalkingTec.Mvvm.Mvc/_FrameworkController.cs` | `Selector`、`GetPagingData`、`GetEmptyData`、`GetExportExcel`、`GetExcelTemplate`、`UpdateModelProperty` 先檢查請求所指的 view model（`CheckGenericEndpoint`）；`RemoveUserCacheByAccount`／`ByRole`／`ByGroup` 要求管理員角色 | 第 24 節 |
| `src/WalkingTec.Mvvm.Mvc/Filters/DataContextFilter.cs` | 查詢字串 `DONOTUSECSName` 預設不再選擇連線（`GetRequestedConnectionKey`，設定 `GenericEndpoints:AllowRequestConnectionKey`） | 第 24 節 |
| `src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.cs`、`Form/SelectorTagHelper.cs`、`Form/DownloadTemplateButtonTagHelper.cs` | 在表格 `where`／匯出連結、Selector 的 ffilter、範本連結中加入 `_DONOT_USE_VMREF` | 第 24 節 |
| `src/WalkingTec.Mvvm.Core/WTMContext.cs`、`ConfigOptions/Configs.cs` | `PagePrivilegeUrl`、`CreateGenericEndpointReference`、`AuthorizeGenericEndpoint`；設定 `GenericEndpoints` | 第 24 節 |
| `src/WalkingTec.Mvvm.Mvc/Helper/FrameworkServiceExtension.cs` | 啟動時，`GenericEndpoints` 放寬時輸出 Warning（1 行） | 第 24 節 |
| `test/WalkingTec.Mvvm.Test.Mock/MockWtmContext.cs` | 選用參數 `dataProtection` | 第 24 節 |
| `src/WalkingTec.Mvvm.Mvc/Helper/FResultExtension.cs` | 訊息轉義；修正 `RedirectUrl` | #25 |
| `src/WalkingTec.Mvvm.Mvc/framework_layui.js` | 全域變數查找改用 `window[...]` | #25 |
| `test/WalkingTec.Mvvm.Test.Mock/MockWtmContext.cs` | 選用參數 `remoteToken` | #1 |
| `test/WalkingTec.Mvvm.Admin.Test/**` | 改為測試 Vue3Demo 的 `_Admin` API | #18 |
| `demo/WalkingTec.Mvvm.Vue3Demo/appsettings.json` | 移除範例密碼，金鑰改為佔位值 | #15 |
| `demo/WalkingTec.Mvvm.Vue3Demo/Areas/_Admin/Controllers/FileApiController.cs` | `UploadImage` 參數順序修正 | #11 |
| `Dockerfile`、`alpine.Dockerfile`、`.dockerignore` | 改為建置 Vue3Demo，含前端建置 stage；刪除 `wtmdoc.Dockerfile` | #16 |
| `demo/WalkingTec.Mvvm.Vue3Demo/ClientApp/wtmbuild.ts`、`public/menu.json` | 路徑改為跨平台；輸出依 key 排序 | #35、#37 |
| `README.md`、`README.zh-CN.md` | 開頭多一段 fork 說明（其餘內容不動，降低同步衝突） | #19 |
| `src/WalkingTec.Mvvm.Mvc/Views/_Framework/Selector.cshtml`、`src/WalkingTec.Mvvm.Mvc/Helper/SelectorScript.cs`（新增） | 寫入內聯腳本的值依所在位置編碼；`SubmitFunc` 只接受函數名或成員路徑；選取列的值寫入隱藏欄位前以 `ff.EncodeHtml` 編碼 | 第 25 節 |
| `src/WalkingTec.Mvvm.Mvc/framework_layui.js` | 下載／匯出表單的隱藏欄位（名稱、值、`Ids`）以 `ff.EncodeHtml` 編碼 | 第 25 節 |
| `src/WalkingTec.Mvvm.Mvc/_FrameworkController.cs` | `SetTenant` 移除 `[Public]`，檢查 `SetCurrentTenant` 的結果，改為 `async Task<IActionResult>` | 第 26 節 |
| `src/WalkingTec.Mvvm.Core/WTMContext.cs` | `DoLogin` 一律驗證請求的帳號、密碼與租戶；已登入請求的重建流程改為內部方法 `LoadSignedInUser` | 第 27 節 |
| `src/WalkingTec.Mvvm.Core/Support/FileEndpointGuard.cs`、`ConfigOptions/FileEndpointOptions.cs`（新增）、`ConfigOptions/Configs.cs`、`WTMContext.cs`、`Utils.cs`、`IUIService.cs`、`Extensions/ListVMExtension.cs` | 檔案參考與模式（`FileEndpoints`）、`CreateFileReference`／`GetFileUrl`、`IFileAccessPolicy`；`IUIService` 新增有預設實作的 `MakeDownloadButtonWithReference`／`MakeViewButtonWithReference`；`Utils.GetUrlByFileAttachmentId` 新增帶 `WTMContext` 的多載 | 第 28 節 |
| `src/WalkingTec.Mvvm.Mvc/_FrameworkController.cs`、`Filters/PrivilegeFilter.cs`（註解）、`Helper/FrameworkServiceExtension.cs` | `Upload*`、`GetFile`、`ViewFile`、`GetFileName` 依第 28 節檢查並決定連線；`IFileAccessPolicy` 預設註冊與啟動記錄 | 第 28 節 |
| `src/WalkingTec.Mvvm.TagHelpers.LayUI/Common/LayuiUIService.cs`、`ImageTagHelper.cs`、`Abstraction/BaseElementTag.cs`、`Form/{Upload,MultiUpload,Display,RichText}*.cs` | 檔案連結與上傳連結帶參考 | 第 28 節 |
| `test/WalkingTec.Mvvm.Test.Mock/MockWtmContext.cs` | 選用參數 `loggerFactory`、`configs`、`fileAccessPolicy`；預設空的 `Request.Query` | 第 28 節 |
| 圖片解碼限制 | `src/WalkingTec.Mvvm.Mvc/_FrameworkController.cs`（`UploadImage`、`GetFile`）、`Helper/ImageDecodeGuard.cs`（新增）、`demo/WalkingTec.Mvvm.Vue3Demo/Areas/_Admin/ImageDecodeGuard.cs`、`Controllers/FileApiController.cs`（`ResizeImage`） | 縮略圖在解碼前先檢查格式、大小與尺寸，上限由 `ImageUploadLimits` 設定；規則移到框架，demo 保留原有的型別與方法 | 第 30 節 |
| `src/WalkingTec.Mvvm.Core/Json/FileAttachmentOutputExclusion.cs`（新增）、`src/WalkingTec.Mvvm.Mvc/Helper/MvcOptionExtension.cs` | `UseWtmJsonOptions` 在輸出時排除 `FileAttachment` 的儲存細節（`Path`、`ExtraInfo`、`SaveMode`、`HandlerInfo`、`FileData`），輸入與伺服器端程式不受影響 | 第 31 節 |
| `src/WalkingTec.Mvvm.Mvc/CodeGenVM.cs`、`CodeGenListVM.cs` | `MainDir` 在沒有（或不含 `bin/Debug`、`bin/Release` 的）`EntryDir` 時維持未設定，不再丟 `ArgumentOutOfRangeException`；`MainNS` 在沒有 `MainDir` 時回傳空字串，不再丟 `NullReferenceException`（model 驗證會讀取 `MainNS`）；輸出目錄改由伺服器端計算並限制在允許的根目錄內（`UseServerDirectories`、`GetInputErrors`）；`SelectedModel` 對應不到型別時，`InitVM` 與其餘各處不再把 `Type.GetType` 的結果（null）直接用在 `MakeGenericType` 等處而丟 `ArgumentNullException`／`NullReferenceException`，改由 `GetInputErrors` 回報一般的驗證訊息（解析集中在 `CodeGenPathGuard.TryGetType`） | #178、#276、#353 |
| `src/WalkingTec.Mvvm.Mvc/_FrameworkController.cs` | `Error()` 對沒有 `IExceptionHandlerPathFeature`、`TargetSite`（或其 `DeclaringType`）、`RemoteIpAddress` 的情況做空值檢查，不再自己拋例外（有 `TargetSite` 的例外走原來的路徑） | #292 |
| `demo/WalkingTec.Mvvm.Vue3Demo/Startup.cs`、`Areas/_Admin/Controllers/FrameworkMenuController.cs`、`Areas/_Admin/ViewModels/FrameworkMenuVMs/{FrameworkMenuVM2,FrameworkMenuBatchVM}.cs`、`FrameworkMenuCache.cs`（新增）、`Resources/Program.{en,zh}.resx`、`ClientApp/src/i18n/pages/**`、`ClientApp/src/views/_admin/frameworkmenu/index.vue` | 啟動時建立圖示目錄（`GetIcons`／`GetIconItems`）、選單刪除（以及新增、修改）後清除選單與用戶快取、選單管理頁刪除後重建側邊選單、補齊多語系鍵 | #292 |
| `demo/WalkingTec.Mvvm.Vue3Demo/Resources/Program.{en,zh}.resx`、`ClientApp/src/i18n/pages/frameworkuser/*.ts`、`ClientApp/src/views/login/changePassword.vue`（`<script name>` 的 `,` 改 `;`）、`ClientApp/public/menu.json`（隨之重新產生）、`test/WalkingTec.Mvvm.Admin.Test/Vue3DemoI18nKeysTest.cs`（新增） | 補齊其餘缺漏的多語系鍵（`Login.Password`、`_Admin.{Children,FileName,GroupManager,HasMainHost,Job,WorkflowApi}`、`Sys.{Approve,NoWorkflow}`、頁面標題 `message.autotrans._Page_AdminFrameworkUserBatchEdit`），en 的 `CodeGen.Success` 改名為 `Codegen.Success`；測試掃描 demo、Core、Mvc 與 ClientApp 用到的鍵，缺任一語言的文字就失敗 | #355 |
| Dependabot 的依賴升級 | 各 csproj／`package-lock.json` 的版本號 | 見各 `dependencies` PR |

fork 自己新增的檔案（上游沒有，不會衝突）：`FORK.md`、`fork.props`、`CHANGELOG.fork.md`、`.github/workflows/*`、`.github/dependabot.yml`、`src/WalkingTec.Mvvm.Mvc/Helper/{WtmEnvironmentHelper（含 Oracle `Version` 檢查）,WtmCorsHelper,WorkflowApiSecurity（含 `WorkflowApiDisabledConvention`、`WorkflowApiStubRemovalConvention`）}.cs`、`src/WalkingTec.Mvvm.Mvc/_WorkflowApiDisabledController.cs`（`EnableWorkflow=false` 時 `/_WorkflowApi/*` 的 404 stub，#210）、`src/WalkingTec.Mvvm.Core/WorkFlow/WorkflowLookup.cs`、`src/WalkingTec.Mvvm.Core/Support/{JsStringEncoder,HtmlAttributeEncoder,GenericEndpointGuard}.cs`、`src/WalkingTec.Mvvm.Core/ConfigOptions/GenericEndpointOptions.cs`、`docs/security/generic-endpoint-authz.md`、`test/WalkingTec.Mvvm.LayUI.E2EHost/Controllers/ReportController.cs`（授權測試用頁面）、`src/WalkingTec.Mvvm.Core/Support/FileHandlers/FileNameSanitizer.cs`、`src/WalkingTec.Mvvm.Core/ConfigOptions/WorkflowOption.cs`、`test/WalkingTec.Mvvm.Core.Test/` 下 fork 新增的測試（`TagHelpers/LayUI/**`、`Excel/**`、`*Test.cs`）、`test/WalkingTec.Mvvm.Admin.Test/WorkflowLookupTest.cs`、`test/WalkingTec.Mvvm.LayUI.E2EHost/`（E2E 測試用的 LayUI 網站，#270）、`test/WalkingTec.Mvvm.E2E/`（Playwright E2E 測試，#270、#272）、`test/WalkingTec.Mvvm.E2E.Instrumentation/`（E2E 的路由覆蓋與 hosting startup，#272）。

## 7. CI、依賴更新與合併

- **CI**：`.github/workflows/ci.yml`（`build-test`）會 build 整個 solution 並跑 Core 與 Admin 測試；`.github/workflows/clientapp.yml`（`clientapp-build`）只在 Vue3Demo 前端有變動時才建置；`.github/workflows/publish.yml` 只在 push `v*` tag（或手動執行）時發佈套件（見第 4 節）。上游留下的 `.azure/pipelines/*`（Azure Pipelines，其中有的釘在舊版 SDK，有的引用已刪除的 Dockerfile）**在本 fork 中不使用**。為了降低同步衝突，這些檔案刻意維持與上游相同，不修改也不刪除（#118）
- **分支保護**：`dotnet10` 必須透過 PR 合併，`build-test` 與 `CodeQL`（程式碼掃描的總結 check，NEUTRAL 視為通過）都必須通過。注意：在預設的 CodeQL 設定下，`Analyze (*)` 這幾個 job 不會在 Dependabot 的 PR 上執行，所以不設為必要 check，否則 Dependabot 的 PR 會永遠卡住；禁止 force push 與刪除分支，管理員也一樣。`clientapp-build` 只在前端有變動時執行，所以沒有設為必要 check，改由每日排程在合併 npm PR 前確認
- **自動合併**：repo 已開啟 GitHub auto-merge。非高風險的 PR 可以設定成「必要 check 全部通過後自動 squash」
- **Dependabot**：`.github/dependabot.yml`
  - demo 前端的 npm 更新，每週合併成一個 PR
  - NuGet 不提出 major 版本的一般更新（安全更新不受這條限制）
- **每日排程**：維護者機器上的排程任務會每天掃描 Dependabot PR。它會開追蹤 Issue、確認所有 check 通過並評估相容性；patch 或 minor 升級沒有疑慮就合併，major 升級或有疑慮的則加上 `needs-decision`，留給維護者決定
- **合併方式**：一般 PR 用 squash；同步上游的 PR 用 merge commit（見第 5 節）。高風險的合併由維護者確認，包括：
  - breaking change 或預設行為變更
  - 資料格式變更
  - 驗證、授權相關邏輯的大幅修改
  - major 版本的依賴升級
  - 同步上游

## 8. 安全問題的揭露措辭

這個 repo 是 public。安全修補的 Issue、commit、PR 與 CHANGELOG，都用中性措辭描述「強化了什麼」，不寫利用方式或 PoC。詳細分析只留在維護者本機。

## 9. 已知且接受的依賴風險

下列弱點目前**決定維持現狀**，風險記錄在這裡。每次評估依賴時都要重新確認前提是否仍然成立。

### Elsa 2 帶入的 transitive 依賴（#77）

- **套件**：`MailKit`／`MimeKit` 3.6.0（來自 `Elsa.Activities.Email`）、`AutoMapper` 12.0.0（來自 `Elsa.Core`），都有已知弱點，而且要升到新的 major 版本才能修補
- **不升級的原因**：Elsa 2.17 是用上述舊的 major 版本編譯的，強制覆蓋成新版很可能在執行時出現 `MissingMethodException`；升級到 Elsa 3 屬於 breaking change，會影響使用工作流程功能的下游
- **風險有限的前提**：
  - WTM 自己的程式碼沒有直接使用這些套件
  - 工作流程功能要明確啟用（`EnableWorkflow`）才會用到
- **下游建議**：沒有用到工作流程功能的應用，維持不啟用；有用到的應用，不要讓不受信任的輸入進入工作流程的 Email 活動
- **重新評估的時機**（符合任一項就重新開 Issue 評估）：
  - 下游需要依賴 Elsa 的工作流程功能
  - 出現可以直接影響 WTM 用法的新弱點
  - Elsa 2 釋出修正版
  - 決定把 Elsa 相關套件拆成選用套件

## 10. Oracle 最低支援版本（#158）

fork 帶入 `Oracle.EntityFrameworkCore` 10.x（底層 ODP.NET 23），**Oracle Database 最低支援 19c**。WTM 6.x（ODP.NET 3.21）可連 11.2.0.4，因此從 6.x 升級且仍連 11g／12c 的應用將無法連線：請先升級資料庫，或改用其他整合方式（例如經由其他資料庫的連結伺服器、獨立同步程式）。

`Connections[].Version` 對 Oracle 只識別 `19`／`21`／`23`；其他非空值（例如 `11`、`12`）會套用 19 相容模式（行為不變、不丟例外），啟動時每個此類連線輸出一則 Warning。

## 11. Workflow 內部呼叫的目標位址（#172；自 10.0.0.7 起 `/v1/activities` 已不使用，#216）

> **自 10.0.0.7 起（#216）：`/v1/activities` 不再使用 `InternalCallBaseAddress`。** `WtmMiddleware` 改成在程式內取得 Elsa 的 activity 描述（見第 15 節），不再發出內部 HTTP 呼叫，所以不需要設定 `WorkflowOptions:InternalCallBaseAddress`，目標也不再受 Host header 影響。`InternalCallBaseAddress`、`WorkflowApiSecurity.ResolveSelfCall`、`IsAllowedHost` 等 public API 為相容下游而保留，標註 `[Obsolete]`（只產生警告），框架內已沒有任何地方使用，會在未來的主版本移除。以下是 10.0.0.6 以前的行為，僅供舊版本與升級參考。

啟用 `EnableWorkflow` 時，（10.0.0.6 以前）`/v1/activities` 會由 `WtmMiddleware` 轉成一次內部呼叫。原本的目標是 `{scheme}://{Host header}`，在 `RequireAuthentication` 關閉（預設）時 Host header 沒有經過驗證。fork 新增可選設定 `WorkflowOptions:InternalCallBaseAddress`（預設 null），並在沒有設定時用 `AllowedHosts` 驗證：

| 條件 | 內部呼叫目標 |
|---|---|
| 有設定 `InternalCallBaseAddress`（絕對 http/https URI） | 一律用該值，不看 Host header；`RequireAuthentication=true` 時它優先於 `Domains:server` 與 AllowedHosts，作為轉送憑證的可信目標（http 仍只限 loopback） |
| 沒設定，`RequireAuthentication=false`，`AllowedHosts` 為空、`*` 或含任何萬用字元項目 | `{scheme}://{host}`（行為與原本完全相同） |
| 沒設定，`RequireAuthentication=false`，`AllowedHosts` 是不含萬用字元的明確清單 | host 在清單內才用 `{scheme}://{host}`；否則不發出內部呼叫，回應 400，並只記一次 Warning |
| 沒設定，`RequireAuthentication=true` | 與原本相同（`Domains:server`，或 AllowedHosts 命中的 request host） |

- **建議**：正式環境設定 `WorkflowOptions:InternalCallBaseAddress`（例如 `https://localhost:5001` 或內網位址），尤其是反向代理或容器部署。修改後需重新啟動。值不是絕對 http/https URI 時會被忽略並記一次 Warning
- **預設行為不變**：沒有新設定、且 `AllowedHosts` 為空或含 `*` 的部署，行為與以前完全相同
- **何時會開始拒絕**：只有 `AllowedHosts` 是不含萬用字元的明確清單、而且 request host 不在清單內。使用明確 `AllowedHosts` 的部署，ASP.NET Core 的 host filtering 通常已經先擋掉不符合的 host，所以實際上幾乎不會改變行為
- **回滾**：移除設定（本來就沒設定的話不需動作），或讓 `AllowedHosts` 含 `*`，即恢復原行為
- **預設 `AllowedHosts: "*"` 的部署仍有風險**：沒設定 `InternalCallBaseAddress` 時，內部呼叫的目標仍然由 Host header 決定，而且內部呼叫的回應內容會回傳給呼叫端。正式環境應設定 `InternalCallBaseAddress`，且該值必須指向自己的服務
- **ForwardedHeaders**：如果使用 ForwardedHeaders（`X-Forwarded-Host`），檢查的是改寫後的 host，`AllowedHosts` 要列出轉發後的名稱
- **清單項目沒寫 port**：同一主機的任何 port 都會命中，這與 ASP.NET Core host filtering 相同
- **`InternalCallBaseAddress` 的格式**：不可以含帳密（userinfo）、query 或 fragment，否則視為無效並被忽略（記一次 Warning）；可以含 path（例如 `https://h/app`，供虛擬目錄部署），結尾的 `/` 會被去掉

## 12. `_WorkflowApi` 查詢端點需要登入，未啟用 workflow 時一律回 404（#13、#210，Breaking（安全））

**問題（P0）**：`_WorkflowApiController`（路由 `/_WorkflowApi`）有 5 個 action 標了 `[Public]`（`GetWorkflowUsers`、`GetWorkflowGroups`、`GetWorkflowGroupManagers`、`GetWorkflowMyGroupManagers`、`GetWorkflowRoles`），而且這個 controller 不論 `EnableWorkflow` 是否開啟都會被路由到。參數為空時查詢不加條件，所以任何使用 WalkingTec.Mvvm.Mvc 的網站，未登入就能 `GET /_WorkflowApi/GetWorkflowUsers` 列舉所有使用者的帳號（ITCode）與姓名，並取得所有群組、群組主管與角色。

**變更**：

| 情況 | 行為 |
|---|---|
| `EnableWorkflow=false`（預設） | `/_WorkflowApi` 與 `/_WorkflowApi/*` 的所有 HTTP method 一律回 404，登入與否都一樣，不執行任何查詢。`AddWtmContext` 註冊 `WorkflowApiDisabledConvention`：移除 `_WorkflowApiController`，保留 `WorkflowApiDisabledController`（匿名 stub，`[Public]`，路由 `_WorkflowApi` 與 `_WorkflowApi/{**path}`，只回 `NotFound()`，不碰 `WTMContext`／DbContext，`[NoLog]`、不出現在 Swagger）。有 SPA 通配（`MapFallbackToFile`，例如 Vue3Demo）的網站也是 404：attribute route 的優先權高於 fallback（order 為 `int.MaxValue`），所以不會回 index.html（#210） |
| `EnableWorkflow=true` | 5 個 action 與其他 action 一樣需要登入（cookie 或 JWT，`[AllRights]`＝任何已登入使用者）；未登入回 401。`[NoLog]` 不變。`AddWtmContext` 改註冊 `WorkflowApiStubRemovalConvention` 移除 stub，同一時間只有一個 controller 處理 `_WorkflowApi`（否則會 AmbiguousMatch） |

- `WtmApproveActivity`（審批活動）原本從伺服器端無憑證地用 HTTP 呼叫自己的 `GetWorkflowRoles`／`GetWorkflowGroups`（設計器下拉選項）以及審批執行時呼叫這 5 個端點。現在改為直接查資料庫（`WorkflowLookup`，查詢方式與原 controller 相同）。`HasMainHost` 為 true 時這些資料在主站，改為直接以目前使用者的 remote token 呼叫主站的 `/_WorkflowApi/*`（原本是自我呼叫再由 `RedirectCall` 轉送）
- **誰會受影響**：沒有登入、或沒帶 token 就呼叫 `/_WorkflowApi/GetWorkflowUsers|Groups|GroupManagers|MyGroupManagers|Roles` 的外部呼叫者。BMS／BOC_WebAPI 已確認沒有啟用 `EnableWorkflow`，也沒有任何程式碼呼叫這 5 個端點。`EnableWorkflow=false` 的網站，任何人（含已登入者）都不能再使用 `/_WorkflowApi/*`
- **升級方式**：外部整合改為帶登入 cookie 或 `Authorization: Bearer <JWT>` 呼叫；多主機部署（`HasMainHost`）請確認子站的使用者有 remote token（透過主站登入者本來就有）
- **回滾方式**：退回前一個 fork 版本（`WalkingTec.Mvvm.Mvc`／`WalkingTec.Mvvm.Core` 一起退回）。沒有可選開關：匿名列舉使用者是資訊外洩，不提供恢復匿名存取的設定
- **不受影響**：LayUI 頁面與 `WalkingTec.Mvvm.TagHelpers.LayUI`（程式碼中沒有任何地方呼叫 `_WorkflowApi`）；`GetTimeLine`、`GetWorkflow`、`GetMyApprove` 的授權沒有變（本來就需要登入）；`WorkflowOptions:RequireAuthentication` 保護的是 Elsa 自己的管理 API，與本節無關

**補充與已知限制**：

- **已知限制**：Elsa `/v1/*` 的匿名存取已在第 15 節（#208、#256）處理：現在一律要求登入
- **租戶範圍變更**：租戶使用者在審批人、角色、群組的解析（設計器下拉選項與審批執行時），現在以自己的租戶為範圍；以前因為是匿名的自我呼叫，查到的是主租戶
- **需要重啟**：`EnableWorkflow` 在啟動時讀取，變更後要重啟才會影響 `_WorkflowApi` 是否存在
- **多主機（`HasMainHost`）**：沿用既有慣例（例如 demo `AccountController.GetFrameworkRoles`）——沒有租戶的使用者呼叫主站，租戶使用者讀本機資料庫。子站使用者必須有 remote token，否則主站會拒絕，解析結果為空，並記一則 Warning（不含 token 與查詢內容），流程不會中斷
- **`WorkflowLookup` 的使用**：給已確認呼叫者身分的程式碼使用；ids／itcode 參數為空時會回傳全部資料（與原 controller 查詢相同），不可直接暴露給未驗證的呼叫者

## 13. demo 匿名註冊（`Reg`）不再透露帳號是否存在（#209）

demo（`demo/WalkingTec.Mvvm.Vue3Demo`）的 `AccountController.Reg` 是匿名可呼叫的。原本 ITCode 已存在時回傳 400 並帶 `ITCode` 欄位的「帳號重複」訊息，任何人都能藉此試探帳號名稱是否存在。現在：

| 情況 | 行為 |
|---|---|
| ITCode 已存在（不分大小寫），或同時註冊競爭時撞到 | 400，`Message` 為通用訊息（資源鍵 `Sys.SubmitFailed`），`Form` 為空，不含欄位名稱 |
| 必填、長度等格式驗證失敗 | 400，保留原本明確訊息（與帳號是否存在無關，且先於存在性檢查執行） |
| 註冊成功 | 不變（200） |

- 只改 demo 的 `Reg` action；`FrameworkUserVM` 與 `src/` 沒動，後台使用者管理的「帳號重複」提示不受影響。Vue3Demo 前端沒有呼叫 `Reg` 的頁面，所以沒有前端影響
- 回應時間（timing）差異不在本次範圍內
- **下游提醒**：自行實作註冊（或忘記密碼、帳號查詢等匿名流程）的專案，也應避免帳號列舉：帳號已存在與其他失敗要回傳同一個訊息與狀態碼，不要在匿名端點回傳帳號是否存在
## 14. 審批時間軸與流程實例在程式內讀取，不再 HTTP 自我呼叫（#207）

`BaseCRUDVM.GetWorkflowTimeLineAsync`／`GetWorkflowInstanceAsync`（`wt:flowinfo` 與審批頁使用）原本用 `Wtm.CallAPI` 對自己的 `{Wtm.HostAddress}/_workflowapi/GetTimeLine`、`GetWorkflow` 發 HTTP 請求。`CallAPI` 不會轉送登入 cookie，只在 `LoginUserInfo.RemoteToken` 有值時帶 `Authorization: Bearer`；單主機時這個 token 是 `WTMContext.DoLogin` 當場簽發的本機 JWT（預設 3600 秒，`JwtOptions.Expires`），卻跟著使用者快取保存 7 天。因此快取超過 JWT 有效期（或使用者來自沒有填 `RemoteToken` 的 `ReloadUserFunc`）之後，自我呼叫被 `_WorkflowApiController` 的 `[AuthorizeJwtWithCookie]` 以 401 拒絕，`rv.Data` 為 null，時間軸與流程資訊回傳 null（`wt:flowinfo` 會因此拋例外）。另外它還要求網站能以請求的 scheme／Host 連回自己。

現在兩個 controller action 與 `BaseCRUDVM` 共用 Core 的 `WorkflowQuery.GetTimeLineAsync`／`GetWorkflowAsync`，在程式內直接讀本機資料庫與 Elsa store，不再有 HTTP 往返。

- **controller 不變**：路由、授權（`[AuthorizeJwtWithCookie]`、`[AllRights]`）、回傳格式與內容都沒有改
- **多主機（`HasMainHost`）**：workflow 實例與執行紀錄一直在本站的資料庫，這兩個 action 本來就沒有轉送主站，舊的自我呼叫也是打本站，所以行為不變，也不需要轉送；差別只是不再依賴 remote token
- **沒有符合的實例**：時間軸回傳空清單（以前失敗時是 null）；流程實例回傳 null
- **Elsa 相依**：Core 只引用 `Elsa.Abstractions`（經 `Elsa.Server.Core`），不含 `Elsa.Core`，所以 `EntityIdSpecification` 與 `WorkflowInstanceIdSpecification` 以 `WorkflowQuery` 內兩個等價的小型 specification 取代；沒有新增或升級套件
- **影響範圍**：只有啟用 workflow 且使用 `wt:flowinfo` 或呼叫這兩個 VM 方法的網站；覆寫這兩個 virtual 方法的子類別不受影響

## 15. Elsa `/v1/*` 一律要求登入（#208、#216、#225、#256，Breaking（安全））；`/v1/activities` 在程式內取得活動描述

**問題（#208）**：啟用 `EnableWorkflow` 後，流程設計器用 `/v1/activities` 取得活動清單。`WorkflowOptions:RequireAuthentication` 關閉（預設）時，這個路徑沒有任何驗證，未登入者就能取得清單；其中審批活動（`WtmApproveActivity`）的下拉選項包含全部角色與群組的 ID 與名稱。第 12 節（#13）只處理了 `_WorkflowApi`，這個外洩當時列為已知限制。

**變更（#256 擴大範圍）**：`WtmMiddleware` 對 Elsa 的 `/v1/*` 端點一律要求登入，未登入回 401、不到 Elsa，與 `RequireAuthentication`、`RequireAdminRole` 的設定無關；已登入的請求交給 Elsa 處理，`RequireAuthentication=true` 時 Elsa 原有的 `AuthorizeFilter`（含 `RequireAdminRole`）照常生效，未改動。範圍從 `/v1/activities`（#208）、`/v1/designer/*`（#225）擴大到全部 Elsa 端點。**僅在 `EnableWorkflow=true` 時套用（`/v1/activities` 除外）**：`EnableWorkflow=false` 時，除 `activities` 以外的 segment 一律不攔截、交給下一個中介層，避免誤擋未啟用 workflow 的網站自己的 `/v1/...` 路由；`/v1/activities` 維持 10.0.0.6 以來的行為（不論是否啟用 workflow，未登入都回 401）。

**比對範圍**：只比對 Elsa.Server.Api 2.17.0 實際使用的第二段路徑（`WorkflowActivitiesGuard.ElsaApiSegments`）：`activities`、`designer`、`features`、`scripting`、`signals`、`version`、`workflow-channels`、`workflow-definitions`、`workflow-instances`、`workflow-providers`、`workflow-registry`、`workflow-storage-providers`、`workflows`。正規表達式為 `^/+v[^/]*/+(<上述清單>)(/|$)`（不分大小寫，版本段、結尾斜線、子路徑與開頭多餘的 `/` 都算）。選白名單而不是整個 `/v1/*`，是因為應用程式自己的 `/v1/...` 路由（例如 API 版本化）不應被誤擋；此清單由反組譯 Elsa.Server.Api 2.17.0 的路由取得，**升級 Elsa.Server.Api 時需要重新核對**。本 repo 內沒有其他以 `/v1/` 開頭的路由（Swagger 為 `/swagger/v1/swagger.json`，不受影響）。

**`/v1/activities` 的處理**（#208、#216，維持不變）：

| 請求 | 行為 |
|---|---|
| `/v1/activities`，已登入（cookie 或 JWT） | 中介層**在程式內**取得活動描述（`WorkflowActivitiesDescriber`），結果與 Elsa 自己的 `/v1/activities` 逐位元組相同；`zh`／`zh-cn` 的翻譯照舊 |
| `/v1/activities`，未登入 | 直接回 401，不到 Elsa |
| `/v1/activities?inneruse=1` | 與其他形式完全相同（已沒有合法的內部呼叫，也沒有例外）：未登入回 401；已登入走同一條程式內路徑，權限檢查與翻譯都一致 |
| `/v1.0/activities`、`/v01/activities`、`/V1/Activities/`、`/v1/activities/...` | 與上面相同的檢查。Elsa 的路由是 `v{version:apiVersion}/activities`，版本段可以是 `v1`、`v1.0`、`v01` 等，所以比對的是 `^/+v[^/]*/activities(/\|$)`（不分大小寫，結尾斜線、子路徑與開頭多餘的 `/`（`//v1/activities`）都算） |

- **登入判斷**：中介層順序是 `UseAuthentication` → `UseAuthorization` → `UseSession` → `UseWtm`，執行到 `WtmMiddleware` 時 WTM cookie（預設 scheme）已驗證、`HttpContext.User` 已填好。JWT Bearer 不是預設 scheme，`UseAuthentication` 不會驗證它，所以另外呼叫 `AuthenticateAsync`（cookie、JWT 各一次）。任何例外都視為未登入（fail closed）。應用程式必須把 `UseAuthentication` 放在 `UseWtm` 之前（`WTMContext.LoginUserInfo` 本來就需要）
- **在程式內取得活動描述（#216）**：10.0.0.6 的做法是中介層對自己發出 HTTP 呼叫 `/v1/activities?inneruse=1`，用程序內的隨機密鑰 header 放行；在預設的 `AllowedHosts: "*"` 且沒設 `InternalCallBaseAddress` 時，呼叫目標（連同密鑰）由 Host header 決定，已登入者可以偽造 Host 取得密鑰。現在改成 `WorkflowActivitiesDescriber.DescribeAsync` 直接呼叫 Elsa 的服務，**不再有內部 HTTP 呼叫、密鑰 header（`X-Wtm-Internal-Call`）、專用的 no-redirect client（`WtmWorkflowInternal`），也不再用 `ResolveSelfCall`**
- **與 Elsa 一致**：實作複製 Elsa.Server.Api 2.17.0 的 `Elsa.Server.Api.Endpoints.Activities.List`：`IActivityTypeService.GetActivityTypesAsync` 取得所有 activity type，逐一 `DescribeActivityType`，並移除 `IsBrowsable == false` 的輸入與輸出屬性（`GetOptions` 下拉選項在 describe 時產生，維持原樣）；序列化用 Elsa 註冊的 `IEndpointContentSerializerSettingsProvider`（Newtonsoft.Json，camelCase、NodaTime、flag enum、忽略 null）。單元測試把輸出與「Elsa 的 `List` controller 經 MVC Newtonsoft executor 實際寫出的內容」逐字串比對；實測（Vue3Demo）修改前後的回應逐位元組相同
- **`RequireAuthentication` / `RequireAdminRole` 照樣生效（review 補強）**：以前自我呼叫會經過 Elsa controller 上的 `AuthorizeFilter(BuildPolicy(requireAdminRole))`，改成程式內呼叫後這個檢查不會自動發生，所以 `WtmMiddleware` 在 `RequireAuthentication=true` 時先用同一個 policy（cookie 或 JWT，`RequireAdminRole=true` 時再加流程管理員角色）透過 `IPolicyEvaluator` 檢查：未登入 401、已登入但權限不足 403，通過才描述活動。`RequireAuthentication=false` 時維持「只要登入即可」。授權服務缺少或檢查過程發生例外時 fail closed（500，空 body）
- **只處理 GET 與 HEAD**：其他 HTTP 方法交給下一個中介層；但未登入時不論什麼方法都先回 401（判斷順序：路徑符合 → 是否登入 → 方法 → policy → 描述）。HEAD 只回 header，不回 body
- **回應 header**：`Content-Type: application/json; charset=utf-8`、`X-Content-Type-Options: nosniff`、`Cache-Control: no-store`（只在成功時設定）
- **失敗處理**：描述過程發生例外時回 500、body 為空，只以 Error 等級記錄例外型別與訊息（不記 request 內容）；`OperationCanceledException`（請求中止）照原樣往外丟
- **JWT 呼叫者的 `HttpContext.User`**：JWT 不是預設 scheme，`UseAuthentication` 不會填 `HttpContext.User`，而 `WTMContext.LoginUserInfo`（含租戶）正是從 `HttpContext.User` 的 claims 載入；登入判斷以 JWT 驗證成功時，會把驗證後的 principal 設到 `HttpContext.User`，管理員角色檢查才讀得到正確的使用者
- **沒有 workflow 引擎時**：服務沒有註冊（`EnableWorkflow=false`）時，已登入的 `/v1/activities` 交給下一個中介層（一般為 404）；未登入仍是 401
- **誰會受影響**：只有 `EnableWorkflow=true` 的網站，而且只有未登入的呼叫者。從已登入後台開啟的流程設計器不受影響。沒有啟用 workflow 的網站不受影響
- **升級方式**：自行呼叫 Elsa `/v1/*` 端點（含 `/v1/activities`）的整合請改帶登入 cookie 或 `Authorization: Bearer <JWT>`
- **不再需要的設定與限制**：原本「正式環境必須設定 `WorkflowOptions:InternalCallBaseAddress`」與「密鑰只在單一程序內有效，滾動重啟、多實例或負載平衡器會造成短暫 401、審批下拉選項為空」的限制都已解除，因為不再有內部呼叫。`RequireAuthentication=true` 時，以前沒設定 `InternalCallBaseAddress` 又是 `AllowedHosts: "*"` 的部署，已登入使用者也會得到 502（憑證不轉送到不可信的目標）；現在不會
- **保留的 public API**：`WorkflowOptions.InternalCallBaseAddress`、`WorkflowApiSecurity.ResolveSelfCall`／`SelfCallTarget`／`IsAllowedHost`／`IsExplicitHostList`／`NormalizeInternalCallBaseAddress`／`IsSafeTarget`／`FilterAuthCookie` 沒有刪除（下游可能引用，刪除是 breaking）。它們標註 `[Obsolete]`（`error: false`，只有編譯警告），設定檔裡的 `InternalCallBaseAddress` 仍可綁定，但不再有作用。10.0.0.6 新增的 `WorkflowActivitiesGuard.InternalSecretHeader`、`InternalClientName`、`AddInternalHttpClient`、`WithInternalSecret`、`IsValidInternalSecret`、`IsInternalActivitiesRequest` 只存在於 10.0.0.6，已隨密鑰機制移除
- **回滾方式**：退回前一個 fork 版本（`WalkingTec.Mvvm.Mvc`）。沒有可選開關：匿名取得全部角色與群組是資訊外洩，不提供恢復匿名存取的設定
- **不在範圍**：`WorkflowOptions:RequireAuthentication`／`RequireAdminRole` 的行為不變（仍控制已登入請求的 Elsa 授權）；HTTP 活動（由流程定義產生的自訂路由）與 Swagger 不在範圍。啟動時的 Warning 仍會提醒 `RequireAuthentication` 沒有開啟

## 16. 移除 `echarts.common.min.js`；LayUI demo 的 echarts 升到 6.1.0，下游須自行更新（#189）

**移除**：`src/WalkingTec.Mvvm.Mvc/echarts.common.min.js`（echarts 4.0.4 common build）沒有被 csproj 列入、也沒有任何程式碼或 view 引用，`dotnet pack` 的 nupkg 本來就不含它。移除後 nupkg 內容不變，不是 breaking。

**LayUI demo 升級**：`demo/WalkingTec.Mvvm.Vue3Demo/wwwroot/layuiadmin/lib/extend/echarts.js` 由 4.x 升到官方 echarts 6.1.0（`dist/echarts.min.js`，維持 layuiadmin 原本的 `layui.define('echartsTheme', …)` 外殼，`config.js` 的 `extend` 設定不用改）。原因：

- 升級以修補已知漏洞：GHSA-fgmj-fm8m-jvvx（echarts，修補版本 6.1.0）與 GHSA-fhv8-fx5f-7fxf（echarts 內建的 zrender，4.x 線沒有修補版）。細節請見各 advisory
- `ChartTagHelper`／`framework_layui.js` 的圖表程式碼本來就是照 echarts 5 的 API 寫的（`RefreshChart` 的 `setOption(…, {replaceMerge:'series'})`、散點圖的 dataset `filter` transform），4.x 沒有這些 API

**下游須知**：

- `layuiadmin/lib/extend/echarts.js` 是 demo 的範本檔，**不會隨 NuGet 套件發佈**，也沒有任何機制會把它帶到下游。自行複製 layuiadmin 的網站請**自行更新網站上的 echarts**（建議 ≥ 6.1.0，至少 ≥ 5.2.1 才修掉 zrender 那筆）
- `framework_layui.js` 的圖表程式碼**需要 echarts ≥ 5.0**。網站仍是 4.x 時，`wt:chart` 的散點圖不會畫出任何資料（`t.count is not a function`），查詢後刷新也不會替換掉舊的 series（4.x 忽略 `replaceMerge`）。這是升級前就存在的問題，不是這次引入的
- `framework_layui.js` 的 `RefreshChart` 使用的 `JSONfns` 不在本 repo 內，需由宿主網站提供
- **載入時機**：`wt:chart` 輸出的 script 在頁面內容插入時就同步呼叫 `echarts.init(...)`，不會等待 echarts。全域 `echarts` 必須在開啟任何含圖表的頁面之前就已載入，建議在版面配置（`_Layout`）以同步 `<script>` 載入。若改以 layuiadmin extension 等非同步方式（例如 `layui.use('echarts')`）載入，含圖表的頁面在 echarts 載入完成前被開啟時（例如首頁剛載入就點選單，或重新整理時網址直接指向該頁）會出現 `ReferenceError: echarts is not defined`，圖表不會建立，之後也不會自動補建（#338）

**echarts 6 的外觀變化**（升級 demo 後可見，`wt:chart` 的資料與互動行為相同）：

- 預設色盤換新（4.x 的 `#c23531`／`#2f4554`… 改為 `#5470c6`／`#91cc75`…）
- 標題預設置中；圖例（legend）預設移到圖表底部；tooltip 為白底圓角（原本半透明深色）
- 座標軸名稱與標籤預設避免溢出與重疊
- **內建 `'light'` 主題已移除**（改為 `echarts/theme/rainbow.js`），`ChartTagHelper` 的 `Theme=light` 會被當成未註冊主題而用預設主題（不報錯）。6.x 內建的只有 `default` 與 `dark`；`vintage`、`macarons`、`roma` 等要自行載入 `echarts/theme/*.js`（`chalk`、`essos`、`walden`、`westeros`、`wonderland` 連 npm 套件裡都沒有）
- 想維持 5.x 的外觀：載入 npm 套件裡的 `echarts/theme/v5.js`，並用 `echarts.init(dom, 'v5')` 或 `registerTheme` 套用；demo 不預設套用，維持 6 的新外觀

`echartsTheme.js`（layuiadmin 的 4.x 風格主題，只匯出主題物件）不需要修改：用 echarts 6 的 `registerTheme('echartsTheme', layui.echartsTheme)` 註冊並套用，可以正常渲染、沒有錯誤。

## 17. LayUI `wt:chart` 的資料輸出編碼（#221）

`ListExtension.ToChartData`（`wt:chart` 的資料端點使用）原本以字串串接組出 JSON，Scatter 的 tooltip formatter 也把圖表資料直接當 HTML 回傳，圖表資料含特殊字元時缺少適當的編碼。現在：

| 項目 | 行為 |
|---|---|
| `ToChartData` | 放進 JSON 的字串一律經 `System.Text.Json` 轉義；數字固定使用 invariant culture（非有限值以 0 輸出）。非 ASCII 文字（例如中文）維持原樣，不轉成 `\uXXXX` |
| `ToChartData` 的輸出 | 一般資料與修改前**逐字相同**：欄位名稱、型別、順序、空白、數字格式都不變，`ChartData` 與 `wt:chart` 的屬性也不變 |
| Scatter tooltip | `seriesName` 與 `value[0..3]` 先經 formatter 內自帶的小型 HTML 轉義函式再組成字串（不依賴 `echarts.format.encodeHTML`，4.x、5.x、6.x 行為一致）。一般資料的 tooltip 內容不變；`NameX` 等標籤屬性仍由 view 端提供，維持原狀 |
| `Series` 名稱 | 少數特殊開頭的名稱前面補一個空白，避免名稱被宿主網站的 `JSONfns` 解析為函式；其他名稱不變，圖例、dataset 與 series 使用同一個名稱 |

- **`framework_layui.js` 沒有修改**：`RefreshChart` 以宿主網站提供的 `JSONfns.parse` 解析 `series`（全 repo 沒有定義 `JSONfns`），Scatter 的 `symbolSize` 是 `ToChartData` 產生的 function 字串，需要它還原，所以不改成 `JSON.parse`
- **影響範圍**：使用 LayUI `wt:chart` 的網站，包含自行呼叫 `ToChartData` 的資料端點。一般資料的顯示與修改前相同；只有含特殊字元的資料，輸出與 tooltip 被正確編碼
- **下游提醒**：自行實作圖表資料端點或自行組 echarts 設定的網站，請對放進 JSON、HTML tooltip、`formatter` 的字串做同樣的處理

## 18. LayUI 伺服器指令的單一執行入口 `ff.ExecServerScript`（#25，階段 2）

回應標頭帶 `IsScript: true` 時，伺服器回傳的是由 `FFResult()` 產生的指令文字（`ff.CloseDialog()`、`ff.Alert(...)`、`ff.RefreshGrid(...)`、`layui.index.render()`、`window.location.href=...`、`AddCustomScript` 的自訂內容）。原本 `framework_layui.js` 在 `PostForm`、`BgRequest`、`OpenDialog` 三處，以及 `LayuiUIService.MakeDialogButton`（`showDialog=false`）產生的按鈕程式，各自直接 `eval`。現在全部改為呼叫同一個函式 `ff.ExecServerScript(text)`，為之後的資料驅動指令協定先收斂入口。

| 項目 | 行為 |
|---|---|
| `ff.ExecServerScript(text)` | 預設與修改前相同：以 `(0, eval)(text)`（間接 eval）執行指令文字，**預設行為不變** |
| `ff.StrictScriptChannel` | **選用**，預設 `false`。設為 `true` 時 `ExecServerScript` 不執行任何內容，只呼叫 `console.warn`。供之後階段使用；未設定時沒有任何差異 |
| 殘留的 `eval` | `framework_layui.js` 內只剩 `ExecServerScript` 一處；一段已註解掉的舊程式（`LoadPage` 舊實作）已移除，行為不變 |

- **區域變數範圍**：原本的 `eval` 為直接 eval，指令文字看得到呼叫函式的區域變數（例如 `PostForm` 的 `formId`、`index`、`layer`）。改用間接 eval 後只能看到全域範圍。`FResultExtension` 產生的標準指令（`CloseDialog`、`Alert`、`Message`、`RefreshGrid`、`RefreshPage`、`RedirectUrl`）只使用全域的 `ff`、`layui`、`window`，不依賴區域變數，行為不變
- **`AddCustomScript` 的相容性**：自訂指令文字若引用呼叫端的區域變數（例如 `layer.close(index)` 中的 `index`、`formId`），在改版後不再可用；若以 `var` 宣告的名稱，原本只存在於呼叫函式內，現在會成為全域變數。需要這些值的自訂指令，請改為在指令文字中直接帶入值，或改呼叫 `ff.*` 提供的函式
- **`MakeDialogButton`**：只替換執行函式，沒有加上 `IsScript` 標頭檢查（加上會改變非 `IsScript` 回應的既有行為）
- **影響範圍**：隨 NuGet 發佈的 `WalkingTec.Mvvm.Mvc`（`framework_layui.js`）與 `WalkingTec.Mvvm.TagHelpers.LayUI`（`LayuiUIService`）。兩者請同版本升級；已快取舊版 `framework_layui.js` 的瀏覽器需要重新載入才會有 `ff.ExecServerScript`
- **測試**：`test/WalkingTec.Mvvm.Core.Test/JsTests/script-channel.test.js`（node，不需瀏覽器與網路）由 `ServerScriptChannelTests` 呼叫；找不到 `node` 時該測試標示為 Inconclusive

## 19. LayUI `wt:tree`、`wt:combobox` 直接呼叫 `ChangeFunc`（#25，階段 1）

`TreeTagHelper`、`ComboBoxTagHelper` 的 `ChangeFunc` 原本是把設定值放進字串後交給 `eval` 執行。現在改為產生直接呼叫的 script：

| 項目 | 行為 |
|---|---|
| 有連動（`link-id` 或 `LinkField`） | 條件改為 `if ((<函式名稱>(data)) != false)`。函式名稱取 `ChangeFunc` 中 `(` 之前的部分（`FormatFuncName` 沿用）；未設定 `ChangeFunc` 時為 `(1==1)`，連動行為不變 |
| `ChangeFunc` 的名稱 | 只接受一般的 JS 識別字，可用 `.` 連接（例如 `app.handlers.onPick`）；其他內容視為沒有設定，不輸出到 script（修改前原樣交給 `eval`） |
| 沒有連動時 | 輸出與修改前逐字相同（`on:function(data){ <ChangeFunc>(data) }`） |

- **`wt:submitbutton`**：`Click` 仍以既有方式執行，將於後續階段處理
- **影響範圍**：隨 NuGet 發佈的 `WalkingTec.Mvvm.TagHelpers.LayUI`；一般用法（`ChangeFunc="fn"`）行為不變
- 驗證：`DirectCallbackInvocationTests`（輸出不含 `eval(`、無連動時輸出不變、script 以 node 解析）

## 20. LayUI checkbox／radio 選項的 HTML 編碼（#262）

`framework_layui.js` 在 `ff.ChainChange` 與 `ff.LoadComboItems` 中，以字串串接為 checkbox、radio 組出選項 HTML，`value`、`title`（來自伺服器回傳的選項 `Value`、`Text`）與 `name` 沒有做 HTML 編碼。現在串接處一律先經新增的 `ff.EncodeHtml(str)`。

| 項目 | 行為 |
|---|---|
| `ff.EncodeHtml(str)` | 編碼 `& < > " '`；`null`／`undefined` 回傳空字串，其他型別先轉成字串；中文等非 ASCII 字元維持原樣 |
| 修改位置 | `ChainChange` 內 checkbox／radio 共 8 處，`LoadComboItems` 內 checkbox／radio 共 3 處，皆為 `target.append("<input ...>")` 的 `name`、`value`、`title` |
| 輸出 | 一般資料（不含上述五個字元）的 HTML 字串與修改前**逐字相同**：元素、屬性與屬性順序都不變，`form.render()` 看到的 DOM 與修改前一致 |
| 做法 | 維持字串串接並加上編碼，不改用 DOM API，以免改變屬性順序與輸出字串 |

- **未處理**：`Download`／`PostForm` 之類組隱藏欄位的程式、頁面片段載入（`LoadPage` 等以 `.html(data)` 放入伺服器回傳的頁面內容，屬設計用途）另案評估；`combo`、`tree`、`transfer` 的選項文字見下一小節（#279）
- **影響範圍**：隨 NuGet 發佈的 `WalkingTec.Mvvm.Mvc`（`framework_layui.js`）；使用 LayUI checkbox／radio 連動與 `LoadComboItems` 的頁面。已快取舊版 `framework_layui.js` 的瀏覽器需要重新載入
- **下游提醒**：自行用 JavaScript 串接選項 HTML 的頁面，請對 `value`、`title` 與文字內容使用 `ff.EncodeHtml`
- **測試**：`test/WalkingTec.Mvvm.Core.Test/JsTests/option-encoding.test.js`（node，不需瀏覽器與網路）由 `OptionEncodingTests` 呼叫；找不到 `node` 時該測試標示為 Inconclusive

### 顯示文字的編碼（#279）

checkbox／radio 的選項標題、combobox／tree（xm-select）的選項與節點文字、transfer 的項目標題、treecontainer 的節點文字，最後都由 layui／xm-select 以 HTML 寫入頁面，之前沒有編碼；`wt:panel` 的 `Title` 沒有套用 card、tab、fieldset、按鈕標題已有的編碼；可編輯表格把儲存格的值寫回快取 HTML 時沒有編碼。現在文字在**框架把它交給元件的地方**編碼，一般文字（不含 `& < > " '`）的輸出與修改前逐字相同。

| 元件 | 編碼位置 | 做法 | 一般文字的輸出 |
|---|---|---|---|
| checkbox／radio（TagHelper、`ff.ChainChange`、`ff.LoadComboItems`、`LayuiUIService.MakeCheckBox`／`MakeRadio`） | `title` 屬性 | `title` 先被瀏覽器解析成屬性，再被 layui 當 HTML 寫入選項，所以編碼兩層（`HtmlAttributeEncoder.EncodeOnce` 再 `Encode`；JS 為 `ff.EncodeTitle`）；`value` 不變 | 逐字相同 |
| combobox、tree（xm-select） | 元件的 `template` 函式（選項、單選標籤、多選標籤） | 回傳 `ff.EncodeHtmlOnce(item.name)`，圖示 class 用 `ff.EncodeHtml`；交給元件的資料（`name`、`value`）維持原值，搜尋與 `value` 不受影響。`template` 是每個實例的設定，`linkto`／`item-url` 重新載入資料時同樣生效 | 逐字相同 |
| transfer | 交給元件的資料與預選值 | 資料經 `ff.encodeTransferItems`（`value` 以 `ff.EncodeHtml` 編碼，`title` 以 `ff.EncodeTitle`），`linkto`／`item-url` 重新載入處對 `ff.getTransferItems` 的結果同樣處理（`ff.getTransferItems` 本身的輸出不變）；預選值（`value:`）以 `ff.EncodeHtml` 編碼，與資料一致 | 逐字相同 |
| treecontainer | 伺服器輸出的節點資料 | `title` 以 `HtmlAttributeEncoder.EncodeOnce` 編碼（layui tree 把它當 HTML 寫入） | 逐字相同 |
| `wt:panel` | `Title` | 與 card、tab、fieldset 相同：`UIOptions:EncodeHtmlContent=true` 時編碼，預設（false）原樣輸出 | 兩種設定都逐字相同 |
| 可編輯表格 | `ff.gridcellchange` | 新值以 `ff.EncodeHtml` 編碼後寫入快取的單引號 `value='…'`，並用函式取代（值內的 `$&` 之類不會被當成取代樣式）；下拉儲存格比對 `<option value>` 時同樣先編碼 | 逐字相同 |

- **`EncodeOnce` 語意**：選項、節點文字先 HTML 解碼一層再編碼（`HtmlAttributeEncoder.EncodeOnce`、`ff.EncodeHtmlOnce`，語意同第 23 節）。經表單存入的文字在資料庫裡已被綁定器編碼（`A&B` 存成 `A&amp;B`），之前因為被當 HTML 寫入而剛好顯示正確，現在仍顯示為 `A&B`，不會變成 `A&amp;B`；輸出永遠是編碼器的結果，不含未編碼的 `< > " '`。取捨同第 23 節：原始資料若本身含看起來像實體的文字（例如字面的 `&amp;`），會顯示為解碼後的字元；JavaScript 版只解碼 `& < > " '` 的實體與數字參考（綁定器寫出的），伺服器版（treecontainer、checkbox／radio 的 TagHelper）另外解碼其他具名實體，所以同一段文字裡的 `&nbsp;` 之類具名實體，在 checkbox／radio／treecontainer 顯示為對應字元，在 combobox／tree／transfer 顯示為字面文字（用 `&#160;` 或全形空白縮排的選項兩邊都正常）
- **選項文字不受 `UIOptions:EncodeHtmlContent` 影響**：選項與節點文字多半來自資料，一律編碼（與 #262 對 `title`、`value` 屬性的處理一致）；只有 `wt:panel` 的標題與其他標題一樣依該設定。需要在選項裡顯示圖示的，請用選項的 `Icon`
- **transfer 的送出值**：`wt:transfer` 以前用 layui 的 `getData()` 取得右側清單，它不含被右側搜尋框隱藏的項目；現在直接讀右側清單的全部項目（值是還原後的原值），搜尋條件不會讓項目從送出的值中消失。含 `& < > " '` 的 `value` 仍會正確送出；已知限制：layui 對這類項目的內部比對（傳給 `ChangeFunc` 的項目資料、`value` 的選取標記）使用編碼後的值，所以傳給 `ChangeFunc` 的項目可能少了這類項目，移動項目後再重新載入（`linkto`／`item-url`）時這類項目的預選標記也可能遺失；傳給 `ChangeFunc` 的 `title`、`value` 是編碼後的文字（一般文字相同）；transfer 的搜尋比對的是編碼後的標題，搜尋詞含這五個字元時比對不到
- **未修改**：layui 與 xm-select 沒有升級也沒有修改；`wt:grid` 唯讀儲存格的文字依第 23 節
- **影響範圍**：`WalkingTec.Mvvm.Mvc`（`framework_layui.js`，已快取舊版的瀏覽器需要重新載入）與 `WalkingTec.Mvvm.TagHelpers.LayUI`（LayUI 受保護路徑，第 3 節）；自行用 JavaScript 組選項或呼叫 `layui.transfer`／xm-select 的頁面，請對顯示文字使用 `ff.EncodeHtmlOnce`，對 transfer 的資料使用 `ff.encodeTransferItems`
- **測試**：Core.Test 的 `DisplayTextEncodingTests`、`OptionEncodingTests`（`option-encoding.test.js`）；E2E 的 `LayUiChoiceTests`、`LayUiTreeTests`、`LayUiLayoutTests`、`LayUiEditableGridTests`（#279 的 `[Ignore]` 已移除）

### 連動值與樹節點 id 的編碼（#326）

延續 #279、#262：值依**寫入的位置**編碼，一般值（整數、GUID、一般文字）的輸出與修改前逐字相同，表單送出的值不變。

| 位置 | 值 | 編碼 | 一般值的輸出 |
|---|---|---|---|
| `wt:treecontainer` 的 script 字串 | 選取節點的 id（`IdField` 的值）、`AutoLoadUrl` | `JsStringEncoder.Escape` | 逐字相同（網址含 `&` 時寫成 `\u0026`，語意相同） |
| `wt:treecontainer` 的節點資料 | 節點 `id` | `HtmlAttributeEncoder.Encode`：layui 把 id 原樣寫進 `data-id="…"`，瀏覽器解析屬性後讀回原 id | 逐字相同 |
| `wt:treecontainer` 點擊、預選時找節點 | 以前串成 `div[data-id='…']` 選擇器 | 改為比對 `data-id`（`ff.findTreeNode`，不組選擇器）；點擊時 `ff.treeNodeData` 從元素讀回原 id，`ClickFunc` 與頁面腳本收到的 `data.data.id` 仍是原 id | 行為相同 |
| `wt:combobox`、`wt:tree` 連動（含預選值）、`wt:textbox` 自動完成的觸發網址 | 選中的值 | `encodeURIComponent`（`&id=` 參數、路徑段） | 同值（只有空白、非 ASCII 等會被百分號編碼，伺服器照常解碼） |
| `ff.changeComboIcon` | 選項的 `icon` 屬性 | `ff.EncodeHtml`（`<i class="…">`） | 逐字相同 |
| `wt:transfer` | `LeftTitle`、`RightTitle`、無資料與搜尋無結果的提示文字 | layui 以 HTML 寫入，與 card、tab、fieldset、panel 的標題一致：`UIOptions:EncodeHtmlContent=true` 時 HTML 編碼，預設（false）原樣輸出；再依 script 字串編碼 | 兩種設定都逐字相同 |

- **下游提醒**：自行讀取 treecontainer 的 `layui.tree` 配置（`config.data`）中 `id` 的腳本，遇到含 `& < > " '` 的 id 會讀到屬性編碼後的文字；點擊回調的 `data.data.id` 不受影響。自行串接網址的頁面，請對值使用 `encodeURIComponent`
- **未修改**：layui 與 xm-select 沒有升級也沒有修改；`wt:grid` 動作按鈕串接 `&id=` 的位置見下一小節（#344）
- **測試**：Core.Test 的 `LinkedValueEncodingTests`、`LinkageEncodingTests`（`linkage-encoding.test.js`）；E2E 的 `LayUiChoiceTests`、`LayUiTreeTests`、`LayUiFrameworkScriptCoverageTests`（含特殊字元的 id、值與 icon）；`option-encoding.test.js`、`selector-encoding.test.js` 的測試字串改為中性的標記字元組合

### 表格動作按鈕的 id 與行欄位（#344）

延續 #326：`wt:grid` 的動作按鈕在瀏覽器端把資料列的 id、以及動作用 `whereStr` 指定的行欄位接到請求網址。這些值是頁面上的資料（不是伺服器寫進腳本字串的字面值，所以沒有 script 字串編碼的問題），現在依**網址查詢字串**編碼；一般 id（GUID、整數、一般文字）的網址與修改前逐字相同，伺服器收到的值不變。

| 位置 | 值 | 編碼 | 一般值的輸出 |
|---|---|---|---|
| `SingleId`、`SingleIdWithNull` 的 `&id=`（行按鈕、工具列按鈕、`ActionsGroup` 子按鈕；對話框、背景請求、下載、匯出皆同） | 資料列 id、勾選列的 id | `encodeURIComponent` | 同值 |
| `ff.concatWhereStr`（`whereStr` 指定的行欄位） | 行欄位的值 | `encodeURIComponent`；欄位名稱照舊原樣寫入 | 同值 |
| 經 `ff.LoadPage` 的按鈕（`IsRedirect`，沒有 `OnClickFunc`、不是下載與匯出） | 同上 | `ff.LoadPage` 會先對網址 `decodeURIComponent` 一次再寫入頁面 hash，所以**編碼兩次**；`ff.concatWhereStr` 的第 4 個參數 `twice` 為 `true`。頁面 hash 保有一次編碼的值，伺服器收到完整的值 | 同值 |

- **不受影響**：`MultiIds`、`MultiIdWithNull` 以請求主體送出的 `Ids`；`OnClickFunc` 收到的 `ids` 與選取的資料列（原值）；`ff.Download`、`ff.DownloadExcelOrPdf` 以表單送出的 `Ids`（已經是 `ff.EncodeHtml` 編碼的屬性）；`ff.LoadPage` 本身沒有修改
- **下游提醒**：欄位值裡的空格、冒號、逗號等（例如日期時間文字）在網址中變成百分號編碼，伺服器照常解碼，收到的值相同；自行呼叫 `ff.concatWhereStr` 的頁面請傳入原值（函式自己編碼），不要先編碼，否則會編碼兩次
- **未修改**：layui 與 xm-select 沒有升級也沒有修改；`GridAction` 等公開 API 沒有變動，`ff.concatWhereStr` 只新增一個可省略的參數
- **測試**：Core.Test 的 `GridActionIdEncodingTests`（TagHelper 輸出與 `grid-action-encoding.test.js`）；E2E 的 `LayUiGridActionIdTests`（`/Lab/Keys`：資料列 id 為文字的測試專用表格，含一般 id、GUID、含網址分隔字元的 id 與欄位值，涵蓋列按鈕、工具列按鈕、`ActionsGroup` 子按鈕、對話框、背景請求、頁面跳轉、下載）。E2EHost 的實體 id 皆為 GUID，所以這個表格的資料由 `LabController.KeySearch` 以手寫 JSON 提供（只供測試）

## 21. E2E 測試（#270）

fork 承諾 LayUI 持續可用（第 3 節），所以用真的瀏覽器測 LayUI 前端。兩個專案都只供測試，`IsPackable=false`，不會被打包或發佈，也不影響下游。

| 專案 | 內容 |
|---|---|
| `test/WalkingTec.Mvvm.LayUI.E2EHost` | 最小的 LayUI MVC 測試網站：登入頁、layuiadmin 首頁框架與選單、一個 CRUD 模組 `Gadget`（Index／Create／Edit／Details／Delete／BatchEdit／BatchDelete／Import／Export）。SQLite，資料全為合成 |
| `test/WalkingTec.Mvvm.E2E` | MSTest + Playwright for .NET（Chromium）的冒煙測試，加上路由覆蓋報告 |

**`Gadget` 模組是用 `src/WalkingTec.Mvvm.Mvc/GeneratorFiles/Mvc` 的範本產生的**（controller、ViewModels、Views 原樣保留）。所以範本改壞、產生出來的程式碼編譯不過或畫面壞掉時，這組測試會先發現。要重新產生：以 `IsQuickDebug=true` 啟動 E2EHost，對 `/_CodeGen/DoGen` 送出 `Gadget` 的設定；產生器同時會在 `test/WalkingTec.Mvvm.Admin.Test` 寫一個 `GadgetControllerTest.cs`，請刪掉。

**LayUI 靜態檔不複製**：E2EHost 直接服務 `demo/WalkingTec.Mvvm.Vue3Demo/wwwroot` 的 `layui`、`layuiadmin`、`jquery`、`sitecss` 等（啟動時從 repo 目錄往上找；也可用設定 `E2E:StaticAssetsRoot` 指定）。`framework_layui.js` 則是 Mvc 的嵌入式資源，由框架在 `/_js` 提供。UI 字串共用 Vue3Demo 的 `Resources/Program.*.resx`（csproj 連結，不複製）。

### 在本機執行

```bash
dotnet build test/WalkingTec.Mvvm.E2E -c Release          # 同時會建置 E2EHost
# 只需做一次：安裝 Chromium（有 pwsh 時）
pwsh test/WalkingTec.Mvvm.E2E/bin/Release/net10.0/playwright.ps1 install chromium
# 沒有 pwsh 時，用輸出目錄內附的 node 驅動程式
cd test/WalkingTec.Mvvm.E2E/bin/Release/net10.0 && .playwright/node/<平台>/node .playwright/package/cli.js install chromium && cd -

dotnet test test/WalkingTec.Mvvm.E2E -c Release --no-build
HEADED=1 dotnet test test/WalkingTec.Mvvm.E2E -c Release --no-build   # 顯示瀏覽器視窗
```

手動看站台：`cd test/WalkingTec.Mvvm.LayUI.E2EHost && dotnet run`，開 `http://127.0.0.1:8599`，帳號 `e2eadmin`，密碼在 `appsettings.json` 的 `E2E:AdminPassword`（僅限本機手動執行的暫用值）。

### 運作方式與輸出

- 測試啟動時以獨立行程啟動 E2EHost（真的 Kestrel，隨機 port，暫存目錄內的 SQLite）。不使用 in-process 的 `WebApplicationFactory`，因為 WTM 有靜態狀態並在啟動時掃描組件，獨立行程更接近實際執行，也讓之後加入 Vue3Demo 時互不干擾
- 管理員密碼與簽章金鑰每次執行隨機產生，只放在行程的環境變數，不寫入任何檔案
- 輸出在 `TestResults/e2e/`（可用環境變數 `E2E_OUTPUT_DIR` 改）：
  - `route-coverage/<target>.report.md|json`：路由覆蓋報告；`<target>.inventory.json`：所有 action 清單；`<target>.hits.jsonl`：實際執行的 action
  - `server/<target>.log`：伺服器 log
  - `failures/<測試名>/`：失敗測試的 Playwright trace（`trace.zip`）、截圖、瀏覽器 console
- 路由清單由 E2EHost 啟動後以 `IActionDescriptorCollectionProvider` 列出所有 controller action（含 `_Framework` 等框架 controller）；執行紀錄由 E2EHost 內的測試專用 action filter 寫入。兩者只在設定 `E2E:RouteInventoryPath`／`E2E:RouteHitLogPath` 時啟用
- 排除清單：`test/WalkingTec.Mvvm.E2E/Coverage/layui-e2ehost.route-exclusions.json`。每一條都要有 `id` 與 `reason`；比對欄位可用 `key`、`controller`、`area`、`attribute`（如 `DebugOnly`）、`controllerTypePrefix`。沒有任何 action 符合的規則會在報告中列為「未使用」
- 目前只記錄基線，**不因覆蓋率低而失敗**；門檻在後續 Issue 逐步提高

### CI

`.github/workflows/e2e-smoke.yml`（job `e2e-smoke`）在 PR 與 push 到 `dotnet10` 時於 ubuntu 執行，上傳 `e2e-route-coverage`（覆蓋報告）；失敗時另外上傳 `e2e-failure-artifacts`。**目前不是 required check**。

### 擴充

- 新增被測網站：在 `Infrastructure/Targets/SiteTargets.cs` 新增一個 `SiteTarget`、加一份排除清單；行程啟動、命中紀錄與報告的程式共用。沒有 E2E 程式碼的網站（Vue3Demo）用 hosting startup 組件取得同樣的路由覆蓋，不必改網站本身，見下一小節
- 新的 LayUI 頁面測試放在 `Pages/`（page object）與 `Tests/`

### LayUI 元件測試（#271）

在 E2EHost 加上 `Lab` 系列測試頁（只供測試，資料全為合成），用 Playwright 逐一操作 `src/WalkingTec.Mvvm.TagHelpers.LayUI` 的 TagHelper，並量測 `framework_layui.js` 的 JS 覆蓋率。

**Lab 頁面**（`Controllers/LabController.cs`、`Views/Lab/*`；側邊選單的「Lab」資料夾，`[AllRights]`，不需另外授權）：

| 頁面 | 涵蓋的 TagHelper |
|---|---|
| `Inputs` | `wt:form`、`wt:fieldset`、`wt:textbox`（含密碼）、`wt:textarea`、`wt:richtextbox`、`wt:hidden`、`wt:display`、`wt:slider`（單值與範圍）、`wt:colorpicker`、`wt:submitbutton`、`wt:resetbutton`、`wt:linkbutton`（`post-current-form`） |
| `Choices` | `wt:checkbox`、`wt:radio`、`wt:switch`、`wt:combobox`（列舉、清單、連動、`item-url`）、`wt:datetime`（日期、日期時間、區間）、`wt:transfer`；`checkbox`／`radio` 也測 `item-url`（`ff.LoadComboItems`） |
| `Pickers` | `wt:selector`（單選、多選、帶參數）、`wt:upload`（一般檔案、圖片）、`wt:multiupload` |
| `Grid` | `wt:searchpanel`、`wt:grid`（分頁、排序、搜尋、列按鈕、欄內連結、匯出、工具列下載、欄位篩選） |
| `Detail` | 可編輯的 `wt:grid`（`use-local-data`、`AddRow`／`RemoveRow`、文字、下拉、日期儲存格） |
| `Layout` | `wt:container`、`wt:row`、`wt:card`、`wt:panel`、`wt:quote`、`wt:code`、`wt:image`、`wt:tab` 與四個子標籤、`wt:button`、`wt:linkbutton`、`wt:flowinfo` |
| `Charts` | `wt:chart`（長條、折線、圓餅、散點） |
| `Trees` | `wt:tree`（單選、多選、`item-url`）、`wt:treecontainer`（點擊、展開、載入頁面） |
| `Keys` | `wt:grid` 的動作按鈕（資料列 id 為文字的測試專用表格，資料由 `LabController.KeySearch` 提供；#344） |

另有 Gadget 模組的批次修改、Excel 匯入與範本下載，以及 `/_Framework/*` 端點（`FrameworkEndpointTests`：沒有按鈕入口的端點用已登入或匿名的 HTTP 請求測）。`wt:ueditor` 只檢查伺服器輸出（UEditor 的 script 不在共用靜態檔內）。`wt:flowinfo` 在沒有 workflow 的 host 只能測到空的時間軸。

**特殊字元**：Lab 的測試資料含需要 HTML／JS 編碼的字元（角括號、`&`、單雙引號、反斜線、`</script>`、樣板分隔符與非 ASCII 文字），每個「看起來像標籤」的片段都帶 `id="e2e-probe…"`。測試檢查：文字原樣顯示、原樣送回伺服器（`/Lab/LastPost` 回傳瀏覽器實際送出的表單），而且沒有任何元素是由這些文字產生的。

**`UIOptions:EncodeHtmlContent`**：測試 host 預設開（`appsettings.json`），Lab 的顯示與編碼測試都以開啟為前提；預設值（關）下，標題、標籤、按鈕文字與表格儲存格會原樣輸出，另有測試固定這個相容行為（`LayUiHtmlEncodingOptionTests`）。測試要暫時切換時用 `SetHtmlContentEncodingAsync(bool)`（呼叫 host 的 `/Lab/HtmlEncoding`，測試結束自動還原）。另外要留意：框架在綁定請求時，預設會把字串先做 HTML 編碼（`StringIgnoreLTGTBinder`），所以經表單存進資料庫的值本來就是編碼過的；`Gadget` 這類資料用預設設定（關）顯示才正確，Lab 的種子資料則是未編碼的原始文字。表格顯示端對這種值只編碼一次的行為見第 23 節（#286）。

**待處理（`[Ignore("#N: …")]`）**：測試寫的是「應有的行為」、但因產品行為不符而暫時略過時，用 `[Ignore("#N: …")]` 標記，原因寫在訊息裡，修正後移除即可。目前沒有任何被略過的 E2E 測試（先前列在這裡的 #279、#280、#281、#282、#283、#285 皆已修正並關閉）。

**`wt:slider` 與請求 culture（#322、#342、#343）**：

- 隱藏 input 的值是**請求 culture 的文字**（`Languages` 為 `de-DE` 的網站渲染成 `30,5`），表單以同一個 culture 做模型繫結，所以未拖動的滑塊會綁回渲染時的值。腳本內的 `value`／`min`／`max`／`step`／`height` 一律以 invariant culture 輸出（#281、#342）。
- 本專案隨附的 layui 2.5.x slider 只會產生整數（`step` 小於 1 會被當成 1，`setValue` 會四捨五入），所以拖動、點擊與 layui API 都不會寫出小數。唯一的入口是頁面腳本**直接**把小數寫進隱藏 input。
- 契約：頁面腳本直接寫入小數時，必須使用請求 culture 的小數符號（`de-DE` 寫 `42,5`），否則會綁錯。以 `42.5` 為例，千分位為 `.` 的 culture（`de-DE`、`es-ES`、`it-IT`、`nl-NL`、`pt-BR`、`tr-TR`）會**靜默**綁成 425，千分位為其他字元的 culture（例如 `fr-FR`、`sv-SE`）則是繫結失敗（`ModelState` 無效）。這不是 slider 獨有的行為，綁定到 `decimal`／`double`／`float` 的文字欄位（例如 `wt:textbox`）被腳本寫入含 `.` 的小數時也一樣。
- 框架不提供格式化寫值的 helper，也不在送出時自動轉換：框架無法判斷 `1.234` 是 invariant 的 1.234 還是 `de-DE` 的 1234。`Languages` 設多個時，請求 culture 可能因使用者而異，腳本要以頁面實際的 culture 為準。
- 若下游在小數逗號的網站需要小數滑塊，請重新開 Issue 說明需求；那需要先採用支援小數 `step` 的 slider，再決定是否加寫值 helper。
- 固定此契約的測試：`LayUiSliderCultureTests`（`Languages=de-DE` 的 host），其中 `Slider_ScriptValueInTheRequestCulture_BindsTheWrittenValue` 寫入 `42,5` 並斷言綁成 42.5。

**JS 覆蓋率**（`framework_layui.js`，`/_js/framework_layui.js`）：Playwright for .NET 沒有 `page.Coverage`，所以測試經 CDP session 呼叫 `Profiler.startPreciseCoverage`／`takePreciseCoverage`（呼叫次數＋區塊範圍），在每個測試結束時收集，整個 run 彙總。函式只要有任何一個測試呼叫過就算涵蓋。輸出 `TestResults/e2e/js-coverage/framework_layui.report.md|json`（函式、具名函式、字元範圍三種數字，與未被呼叫的函式清單），CI 放進 step summary 與 artifact `e2e-js-coverage`。**只記錄基線，不因數字低而失敗。** 會整頁重新載入的測試（例如切換語言）要在重新載入前呼叫 `FlushJsCoverageAsync()`，否則舊文件的資料會遺失。目前基線：函式 86／95（90.5%），具名函式 65／69（94.2%），字元範圍 72.6%。沒有被呼叫的 9 個：舊瀏覽器用的 `String.startsWith` 補丁、`LoadPage` 開新視窗的分支、`OpenDialog2` 的失敗回呼、對話框內圖表縮放的三個回呼，以及舊的 `changeComboIcon`、`refreshcombobox`（後者依賴不在靜態檔內的 `layui.formSelects`）。

**路由覆蓋**：所有 `_Framework` 端點與 Gadget 動作都有測試命中；排除清單沒有新增項目（沒有「不可達」的端點，需要外部網路、多站台或 `DebugOnly` 的維持原排除）。`SiteProcess.AssertRouteHitAsync` 讓測試可以直接檢查命中紀錄（批次刪除的測試用它確認 `gadget/batchdelete`、`gadget/dobatchdelete` 都被記錄）。基線 16／42 個可測動作，現在 70／70（含 28 個 Lab 測試用動作；原本的 42 個全部命中）。

**執行時間與 nightly**：整套在本機約 3 分鐘（不含需先建置前端的 Vue3Demo 測試）。若之後變長，在測試加上 `[TestCategory("nightly")]`；`e2e-smoke` 已用 `--filter "TestCategory!=nightly"` 排除它們，要跑時：`dotnet test test/WalkingTec.Mvvm.E2E -c Release --no-build --filter "TestCategory=nightly"`（本 Issue 沒有新增排程 workflow）。

**測試 host 的自備項目**：`_Layout.cshtml` 內有測試用的 `JSONfns`（`RefreshChart` 解析 series 時使用，框架腳本與共用靜態檔沒有提供，下游頁面自行引入），`Views/Home/Index.cshtml` 在 `index` 載入後再載入 `echarts` extension（圖表 TagHelper 使用全域 `echarts`）。

### Vue3Demo（#272）

第二個被測網站是 `demo/WalkingTec.Mvvm.Vue3Demo`（Element Plus SPA 加 ASP.NET Core API），這是唯一的 SPA 參考應用。**不修改 `demo/**` 與 `src/**`**，所有設定都來自行程啟動時的環境變數。

| 專案 | 內容 |
|---|---|
| `test/WalkingTec.Mvvm.E2E.Instrumentation` | 測試專用的小型函式庫，只依賴 ASP.NET Core 共用框架。路由清單與命中紀錄的程式從 LayUI E2EHost 原樣搬到這裡（LayUI E2EHost 改為參考它，行為不變）；另含 `IHostingStartup`（`E2EHostingStartup`）與 runtime startup hook。`IsPackable=false` |
| `test/WalkingTec.Mvvm.E2E`（新增） | `Infrastructure/Targets/Vue3DemoSetup.cs`（目標設定）、`Infrastructure/Vue3Seed.cs`（每次執行一次的種子資料）、`Pages/Vue3/`（page object 與 API 用戶端）、`Support/ExcelTemplate.cs`、`Tests/Vue3/` |

#### 如何不改 demo 就能量測與驅動

- **路由覆蓋**：測試以 `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=WalkingTec.Mvvm.E2E.Instrumentation` 啟動 Vue3Demo。demo 並不參考這個組件，所以同時設定 `DOTNET_STARTUP_HOOKS=<組件完整路徑>`，由 runtime 先載入該組件，ASP.NET Core 才找得到它。組件加入的服務與 `E2E:RouteInventoryPath`／`E2E:RouteHitLogPath` 的行為，與 LayUI E2EHost 相同
- **前端**：同一個組件依 `E2E:SpaRoot` 在 demo 的管線最前面加上靜態檔，服務 build 後的 SPA（`ClientApp/dist-e2e`），不複製進 demo 的 `wwwroot`。SPA 用 hash 路由，不需要 SPA fallback
- **資料庫與金鑰**：`Connections__0__*` 指向每次執行獨立的 SQLite 檔（暫存目錄，結束時刪除）、`JwtOptions__SecurityKey` 每次隨機、`IsQuickDebug=false`、`CorsOptions__EnableAll=false`、`ASPNETCORE_ENVIRONMENT=Production`
- **管理員密碼**：demo 在 `DataContext.DataInit` 種入固定密碼的 `admin`。網站啟動後，測試對暫存 SQLite 把該帳號的密碼雜湊（demo 預設的 MD5 格式）換成每次隨機產生的密碼，之後第一個登入的測試即驗證格式正確。密碼只存在行程環境與記憶體
- **種子資料**：demo 只種 `_Admin` 的選單，`SchoolData`／`testvue` 的頁面沒有選單，連管理員呼叫它們的 API 也會得到 403；而 `City` 宣告了資料權限，沒有資料權限的使用者在下拉選單看不到任何城市。`Vue3Seed` 在第一個登入前，用選單管理與資料權限頁面所呼叫的同一組 API 建立這些選單、授予管理員角色（001）所有動作、授予管理員 `City` 的「全部」資料權限，再重新整理選單快取

#### 在本機執行

```bash
cd demo/WalkingTec.Mvvm.Vue3Demo/ClientApp
npm ci
VITE_API_URL= npm run build -- --outDir dist-e2e      # PowerShell：$env:VITE_API_URL=""; npm run build -- --outDir dist-e2e
cd -
dotnet build test/WalkingTec.Mvvm.E2E -c Release
dotnet test test/WalkingTec.Mvvm.E2E -c Release --no-build --filter TestCategory=vue3demo
```

**`VITE_API_URL` 必須是空的**：一般 build 把開發位址 `http://localhost:8598` 編進去，SPA 會呼叫那個位址而不是載入它的網站。輸出放在 `dist-e2e`（已加入 `.gitignore`），不覆蓋 Docker／正式用的 `dist`。測試啟動前會檢查 SPA 是否已 build、是否用了開發位址，不符時直接顯示上面的指令。可用環境變數 `E2E_SPA_ROOT` 指定其他目錄。不加 `--filter` 會同時跑 LayUI 與 Vue3Demo 兩個目標。

#### 測試內容

測試語言為英文（SPA 預設中文；啟動時把 localStorage 的語言設定改為英文並重新載入，語系切換另有測試）。Vue3Demo 的使用者管理、學校與測試頁面有些標籤沒有做在地化，測試用其原有的中文。每個測試用唯一名稱建立資料、結束時刪除。

| 檔案 | 內容 |
|---|---|
| `Vue3AuthTests` | 登入（成功、錯誤密碼、不存在的租戶代碼）、未登入的路由導向登入、登出、語系切換（保留於重新載入後）、由標頭變更密碼 |
| `Vue3AdminTests` | 群組（新增、修改、詳細、刪除、子群組、欄位驗證、批次刪除、匯出、匯入）、角色（含動作權限對話框、匯入）、選單（外部網址選單的新增、修改、詳細、重新整理、刪除）、資料權限（群組與使用者）、租戶（新增、修改、詳細、刪除、由標頭切換）、操作日誌（搜尋與詳細）、使用者列表（角色、群組、照片）、四個列表的匯出 |
| `Vue3SchoolDataTests` | `City`、`School`、`SchoolVue3` 的新增、修改、詳細、刪除、批次刪除、匯入、匯出；`SchoolVue3` 含照片與附件上傳、附件下載 |
| `Vue3ApiTests` | 沒有畫面（或畫面無法到達）的 API：cookie 登入、換發 token、註冊、下拉選單、使用者 API 的完整流程（含密碼）、各模組匯出、檔案 API、`Major`／`Student` 的新增、關聯、搜尋、匯出、匯入、刪除 |

匯入檔以網站自己提供的 Excel 範本加上資料列產生（`ExcelTemplate`，不需要試算表函式庫），所以範本改了、欄位順序變了，測試會先發現。

#### 路由覆蓋

排除清單：`test/WalkingTec.Mvvm.E2E/Coverage/vue3demo.route-exclusions.json`（每條都有 `id` 與 `reason`）。排除的是：程式碼產生器（`DebugOnly`）、未啟用的 workflow、呼叫外部網站的 action、需要主站台的 action，以及伺服器端渲染 UI 專用的 `_Framework` 輔助 action（SPA 只呼叫 `/api/*`）。目前基線：共 223 個 action，排除 68，可計入 155，全部命中（100%）。LayUI 目標的數字見其自己的報告。仍只記錄基線，**不因覆蓋率低而失敗**。

#### CI 與執行時間

`e2e-smoke` 在安裝 Node（npm 快取以 `package-lock.json` 為 key）後執行 `npm ci` 與 SPA build，再 build 與執行兩個目標；報告 artifact `e2e-route-coverage` 內含兩個目標的報告。兩個目標合計約 1.5 分鐘（Vue3Demo 有 40 個執行中的測試，另有 8 個 `Ignore`），遠低於 12 分鐘，所以不另設 `nightly` 分類；之後若有長時間測試，用 `[TestCategory("nightly")]` 標記並以 `--filter TestCategory!=nightly` 排除。

#### 已知問題（`[Ignore("#N: ...")]`）

測試發現的產品問題不在這個 PR 修，對應的測試以 `[Ignore("#N: ...")]` 保留預期行為（修好後移除 `Ignore` 即可）。列出方式：`grep -rn 'Ignore("#' test/WalkingTec.Mvvm.E2E/Tests`。

- #288：匯入檔含無效資料列時，回應是「Memory stream is not expandable.」，而不是列出錯誤並提供錯誤檔
- #290：使用者管理頁呼叫 `/api/_Admin/FrameworkUser/*`（與實際路由 `/api/_FrameworkUser/*` 不符）；`City`／`SchoolVue3` 頁的匯出按鈕與資料權限列表的詳細按鈕檢查了不存在的權限鍵而不顯示；學校頁匯出勾選列不會下載；租戶列表的名稱欄為空
- #291：只符合子群組的搜尋回傳空列表（這個 Issue 也包含群組列表的備註欄一律為空，該項沒有對應的測試）
- #292 已修正：缺漏的 i18n 鍵、`FrameworkMenu/GetIcons` 回 400 與 `GetIconItems` 回 500（含 `_FrameworkController.Error()` 自己拋例外）、刪除選單後側邊選單未即時更新。這些沒有事先標 `Ignore` 的測試，修正時一併新增（`Vue3ApiTests`：圖示目錄、刪除選單後的使用者資訊、租戶範本表頭、直接請求錯誤頁；`Vue3AdminTests`：刪除後的側邊選單、租戶選單與頁面標題），權限矩陣的 `geticonitems` 已移除 `knownDeviation`，路由排除清單移除圖示目錄兩項

### 權限矩陣（#273）

把「誰能呼叫哪個 action」寫成一張進版控的期望表，每次 E2E 都用純 HTTP 實際打一遍，確認框架的授權行為沒有悄悄改變。它不用瀏覽器（`HttpClient`），所以很快：三組矩陣合計約 15 秒。

**身分**（每次執行隨機產生密碼，只存在行程環境與記憶體）：

| 身分 | 說明 |
|---|---|
| `anonymous` | 沒有任何憑證 |
| `norole` | 已登入、有效，但沒有任何角色或群組 |
| `limited` | 角色只授權少數動作：LayUI 為 Gadget 的列表頁、`Search`、`Details`、`ExportExcel`；Vue3Demo 為 City 與角色管理的列表頁、`Search`、`Get`，以及 School 的列表頁與 `Search` |
| `admin` | 系統管理員角色（001）。是第二個管理員帳號，不影響瀏覽器測試用的管理員 session |
| `admin-cookie` | 只有 Vue3Demo：同一個管理員改用 cookie 登入（其他身分用 Bearer token）。MVC controller（`_Framework`）只認 cookie |

**被測的站台**：矩陣各自啟動獨立的站台行程（`layui-e2ehost-matrix`、`vue3demo-matrix`、`vue3demo-matrix-workflow`），不與瀏覽器測試共用。原因是矩陣會用管理員身分送出真的（但是空的）新增、刪除、登出請求，也會觸發一個會改 host 設定的測試 action；獨立站台讓瀏覽器測試的資料、路由覆蓋基線都不受影響。這三個站台不產生路由覆蓋報告。

#### 結果類型與偵測方式

每個請求依狀態碼、轉址目標與（只有框架固定訊息時）回應開頭分類。回應內容只讀來分類，不儲存也不輸出。

| 類型 | 偵測方式 | 出現在 |
|---|---|---|
| `ok` | 狀態 2xx | 通過驗證與授權，action 有回應 |
| `redirect-login` | 轉址（3xx）到登入頁或 cookie scheme 的 access-denied 路徑，或狀態 200 且內容是框架寫的 `window.location.href='<登入頁>'` 導向 script | 未登入的 MVC 請求（200 + script）；已登入但被 `Forbid()` 的 MVC 請求（轉址） |
| `401` | 狀態 401 | 未登入的 API；帶 Bearer token 打只認 cookie 的 MVC controller |
| `403` | 狀態 403 | 已登入但沒有該動作的權限（API） |
| `200-no-privilege-page` | 狀態 200，內容就是「沒有權限」訊息 | 沒有權限的 MVC 頁面與動作 |
| `200-tenant-not-allowed-page` | 狀態 200，內容就是「僅主租戶可用」訊息 | 租戶使用者打標 `MainTenantOnly` 的 MVC 動作（目前沒有租戶身分，保留類型） |
| `200-debug-only-page` | 狀態 200，內容就是「只能在調試模式使用」訊息 | 標 `DebugOnly` 的 MVC 動作 |
| `400-debug-only` | 狀態 400，內容是同一則訊息 | 標 `DebugOnly` 的 API 動作 |
| `404` | 狀態 404 | 功能未啟用的 stub 路由；不存在的紀錄 |
| `200-json-401`／`200-json-403` | 帶標頭 `layuisearch` 的 LayUI 表格查詢：狀態 200，JSON 的 `Code` 為 401／403 | 表格查詢被拒（讓前端能解析） |
| 其他狀態 | 以狀態碼本身為類型（`302`、`400`、`415`、`500`…） | action 自己的回應；`5xx` 永遠不是預期值 |
| `allowed` | 只用在期望表：任何「不是拒絕、也不是伺服器錯誤」的結果 | action 對空請求的回應（400、404、415…）不重要時 |

「拒絕」依框架實際行為分成多種：MVC 頁面沒有權限時回 200 加訊息（`200-no-privilege-page`），API 回 403；未登入的 MVC 頁面回 200 加導向 script，API 回 401。LayUI 按鈕沒有權限時根本不輸出（見下）。

#### 期望表

`test/WalkingTec.Mvvm.E2E/Security/<target>.permission-matrix.json`（`layui-e2ehost`、`vue3demo`）。一個 action 與 HTTP method 一列，key 是路由清單的 key 加 method（`gadget/create GET`；同名 controller 的路由用 `[路由樣板]` 區分）。路由清單的每個 action 都要有一列，每列都要對每個身分寫期望結果。

```json
{ "key": "gadget/create GET", "expect": { "anonymous": "redirect-login", "norole": "200-no-privilege-page", "limited": "200-no-privilege-page", "admin": "ok" } }
```

其他欄位：`exempt`（宣告屬性推導的規則不適用，必須寫原因）、`note`、`skip`（不送出請求，例如會呼叫外部網站）、`knownDeviation`（見下）。`variants` 是帶額外標頭的同一批 action（LayUI 表格查詢）。`configurations` 放其他設定與預設的差異（見下）。

表格記的是**預期的行為**，不是現況快照。測試另外用 action 宣告的屬性檢查表格是否自相矛盾（`DebugOnly`、`Public`／`AllowAnonymous`、`AllRights`、沒有屬性的需要選單權限、`limited` 只能通過它的角色被授權的動作），所以不能只把觀察結果照抄進去。

#### 失敗條件

- 實際結果與表格不同
- 路由清單有 action 但表格沒有（新 endpoint 必須宣告預期行為）
- 表格有列但沒有對應的 action（已移除的 endpoint 不留殘骸）
- 表格與宣告的規則矛盾，卻沒有 `exempt` 或 `knownDeviation`
- 已知偏差已經消失（實際結果等於預期）：請把 `knownDeviation` 移除

#### 新增或修改 endpoint 時怎麼更新

1. 執行 `dotnet test test/WalkingTec.Mvvm.E2E -c Release --no-build --filter TestCategory=permission-matrix`（Vue3Demo 需先 build SPA，見上一節）
2. 失敗訊息會列出缺少的列。`TestResults/e2e/permission-matrix/<site>.observed-entries.txt` 有每個請求的觀察結果，格式與表格相同，可以複製整列
3. **逐項對照這個 action 應該有的行為再貼**：`[Public]`／`[AllRights]`／選單權限。觀察結果與預期不同時，期望欄寫預期，另加 `knownDeviation` 記錄現況
4. 授權規則改變（例如移除 `[AllRights]`）時，改對應的列；`limited` 角色的授權清單在 `Security/MatrixSetups.cs`（`LimitedGrants`）與它的建立處（`DataContext.cs` 的 `E2ESeed.LimitedRoleActions`、`Security/MatrixAccounts.cs` 的 `LimitedRoleUrls`）

#### 已知偏差

觀察到的行為與預期不同、但本 Issue 不修改 `src`／`demo` 的項目，用 `knownDeviation` 標記（短而中性的描述加上各身分目前的結果）：測試照常通過並在報告中列出，修好後測試會要求移除標記。目前共 2 類，見追蹤 Issue（原本的第 4 類，`EnableWorkflow=true` 時名稱以數字結尾的 controller 路由名稱被改變，已在 #294 修好並移除；原本的第 3 類，`_CodeGen` 的 POST action 在 model 驗證階段回 400、蓋過 debug-only 訊息，已在 #276 修好並移除）。

#### 設定切換：`EnableWorkflow`

Vue3Demo 的矩陣跑兩次：預設（`EnableWorkflow=false`）與 `EnableWorkflow=true`（環境變數，demo 會註冊 Elsa）。表格的 `configurations.workflow-enabled` 只記差異：會出現的工作流程 endpoint（`_WorkflowApi` 查詢端點、Elsa REST API 的不同路由）、會消失的 stub（`_WorkflowApi` 在關閉時一律 404）、結果改變的 Elsa 路由（未登入 401；已登入者進入引擎，規則見第 15 節）。LayUI 測試 host 沒有註冊 workflow，所以只跑預設設定。

#### LayUI 按鈕

`ButtonPrivilegeTests`（瀏覽器）：`limited` 登入後 Gadget 列表的工具列只有「Export Excel」、列只有「Details」，沒有 Create／Batch edit／Batch delete／Import、也沒有列上的 Edit／Delete；管理員五個工具列按鈕與三個列按鈕都在；沒有角色的帳號看不到 Inventory 選單。伺服器端對同一批網址的回應由矩陣檢查。

#### 報告

`TestResults/e2e/permission-matrix/`：`summary.md`（各站台總數、各身分的結果分佈、偏差摘要；CI 放進 step summary）、`<site>.matrix.md`（完整矩陣，每個 action 一列）、`<site>.matrix.json`、`<site>.observed-entries.txt`。CI（`e2e-smoke`）把 `summary.md` 放進 step summary，整個目錄上傳為 artifact `e2e-permission-matrix`。

#### 範圍與限制

- 請求是無害的：id 不存在、空 body，且只看結果類型。因此看不到依參數內容才決定的授權（例如以名稱指定資料型別的輔助 action），也看不到資料權限（`DataPrivilege`）
- 沒有租戶身分，`MainTenantOnly` 只驗證主租戶管理員可用
- 路由清單的 key 可能因設定而不同，所以每組設定各用自己的路由清單比對

## 22. 匯入時讀取日期格式的數值儲存格（#295）

Excel 把日期存成「數值（日期序列值）＋日期數字格式」。上游 10.0 改用 OpenXML SDK 後，匯入以純文字讀出儲存格（例如 `46317`），再交給 `DateTime.TryParse`，所以 Date／DateTime 欄位拒絕真正的 Excel 日期儲存格，包括使用者在 WTM 自己的範本（日期欄的數字格式為 `yyyy-MM-dd HH:mm:ss`）裡輸入的日期。

現在 `BaseImportVM.SetTemplateData` 讀儲存格時（含動態欄位），只在下列條件**全部**成立才轉換：

| 條件 | 說明 |
|---|---|
| 欄位型別 | `ExcelPropety.DataType` 為 `Date` 或 `DateTime` |
| 儲存格型別 | 數值型：`t` 屬性省略或為 `n`（不是共用字串、內嵌字串、字串、布林、錯誤） |
| 值 | 可用 `double.TryParse(NumberStyles.Float, InvariantCulture)` 解析，且不小於 0 |
| 數字格式 | `OpenXmlHelper.IsDateFormattedCell` 為 true |

符合時以 `DateTime.FromOADate` 轉換，再輸出成 invariant 字串 `yyyy-MM-dd HH:mm:ss` 交給既有的 `ValueValidity`。工作簿使用 1904 日期系統（`workbookPr date1904`）時，序列值先加 1462 天。轉換失敗（超出日期範圍）時保持原值，由 `ValueValidity` 報格式錯誤。

`IsDateFormattedCell` 的判斷規則：

- 依 `cell.StyleIndex` 找到 `CellFormats` 中的 `NumberFormatId`
- 內建日期格式：14–22、27–36、45、47、50–58（含東亞語系的內建日期格式）；**46（`[h]:mm:ss`）不算**
- 自訂格式（ID 164 以上）：取 `NumberingFormats` 的 `FormatCode`，先去掉 `"..."` 文字與反斜線跳脫字元；`[Red]`、`[$-409]`、`[>100]` 這類方括號內容直接忽略，剩下的部分含 `y`、`d`、`h`、`s`、`m` 之一（不分大小寫）即視為日期或時間；`0.00` 這類數字格式不含這些字母
- **經過時間格式不轉換**：內建 46，以及含 `[h]`／`[hh]`／`[m]`／`[mm]`／`[s]`／`[ss]` 方括號經過時間代碼的自訂格式（去掉引號與跳脫字元後判斷，不分大小寫）。它們代表時間長度而不是時間點，儲存格維持原值交給 `ValueValidity`，行為與 #295 之前相同（#303）
- 找不到樣式表、樣式索引或格式碼時回傳 false（行為與修改前相同）

- **不變的行為**：文字儲存格（例如以字串存放的 `2026-10-22`）、非 Date／DateTime 欄位（數值欄、文字欄等）、沒有日期格式的數值（DateTime 欄位填入純數字仍然是格式錯誤）。`GetCellTextValue` 沒有修改
- **影響範圍**：隨 NuGet 發佈的 `WalkingTec.Mvvm.Core`；LayUI 匯入（`_FrameworkController`）與 SPA／API 匯入共用 `BaseImportVM`，兩者一併生效。`BaseImportVM` 的 public API 沒有變動；新增 public 方法 `OpenXmlHelper.IsDateFormattedCell(Cell, WorkbookPart)`
- **下游提醒**：自行覆寫 `SetTemplateData` 並呼叫 `GetCellTextValue` 的匯入類別不受影響，但不會自動取得這項轉換
- **測試**：`test/WalkingTec.Mvvm.Core.Test/Excel/ExcelTemplateImportTests.cs`（`Import_DateColumn_*`、`Import_NumberColumn_*`、`IsDateFormattedCell_*`）：內建格式 14、東亞格式 31／57、WTM 範本的格式 165、自訂 `yyyy/m/d`、經過時間格式（46、`[h]:mm:ss`、`[mm]:ss`）不轉換、文字欄的日期格式數值維持序列值字串、逐一涵蓋所有內建格式 ID、數值欄的 `0.00`、文字日期、沒有日期格式的純數字、1904 日期系統、超出範圍的序列值，以及各種格式碼的分類

## 23. `UIOptions:EncodeHtmlContent=true` 的語意與表格的重複編碼（#286）

預設值為 **false**，本節的行為只在開啟後才有；預設行為與資料庫的儲存格式都不變。

| 階段 | `EncodeHtmlContent=true` 時 |
|---|---|
| 綁定請求（`StringIgnoreLTGTBinder`） | **與此選項無關**：框架本來就會把表單送來的字串先做 HTML 編碼再存入（除非 action 標註 `StringNeedLTGT`）。所以經表單存入的值在資料庫裡是編碼過的（`A&B` 存成 `A&amp;B`）。本修正沒有改動 |
| 顯示：LayUI TagHelper 的標題、label、按鈕文字等 | 以 HTML 內容輸出的文字一律編碼（第 6 節 #24，不變） |
| 顯示：`wt:display`（欄位值與 `display-text`） | 同表格：先解碼一層再編碼一次（`EncodeOnce`）；`encode-html` 屬性明確指定 `true` 時同樣如此，指定 `false` 時原樣輸出（不變） |
| 顯示：表格儲存格的一般值（`GetSingleDataJson`） | 先 HTML 解碼**一層**，再用 `HtmlAttributeEncoder.Encode` 編碼（`HtmlAttributeEncoder.EncodeOnce`）。修正前直接再編碼一次，表單存入的值在表格上會顯示成 `A&amp;B` |

- **為什麼安全**：輸出永遠是 `HtmlAttributeEncoder.Encode(...)` 的結果，所以無論輸入是已編碼、未編碼（例如種子資料、匯入的資料）或兩者混合，輸出都不含未編碼的 `< > " '`，`&` 之後也只會接編碼器自己寫的實體。不是「含 `&` 就略過編碼」。解碼只做一層，因此使用者輸入的 `&lt;` 字樣（存成 `&amp;lt;`）仍如實顯示為 `&lt;`；數字實體（如 `&#60;`）解碼後同樣會再被編碼，不會變成標記
- **語意上的取捨**：原始值（未經綁定器編碼、直接寫入資料庫的資料）若本身含有看起來像實體的文字（例如字面的 `&amp;`），表格會把它當成已編碼而顯示 `&`。這類資料在預設值（false）下本來就會被瀏覽器解成同樣的字元
- **範圍**：表格儲存格的一般值與 `wt:display`。自訂格式（`SetFormat`）的輸出仍視為 HTML、不編碼（不變）。`wt:display` 的修改位於 LayUI 受保護路徑（`DisplayTagHelper.cs`）內，只有一行，且僅在編碼啟用時改變輸出；關閉（預設）時輸出逐字相同。其他以 HTML 內容輸出文字的 TagHelper（標題、label、按鈕、`wt:selector` 的已選文字等）的編碼方式不變
- **已知且保留的行為**（不在本修正範圍，需要時另案處理）：
  - 搜尋條件含 `& < " '` 時，比對不到以綁定器編碼後存入的值（資料庫存的是編碼後的文字）
  - `StringLength` 等驗證是對**編碼後**的長度計算（一個 `&` 算 5 個字元，一個引號算 6 個）
- **LayUI**：沒有修改 LayUI 受保護路徑（第 3 節）的檔案；表格的儲存格範本與 `framework_layui.js` 都沒有變動，預設值下的輸出逐字相同
- **測試**：`HtmlContentEncodingTests`（儲存格已編碼／混合標記／實體字樣／預設值不變）、`DisplayTagHelper` 的對應案例、`HtmlAttributeEncoderTest`（`EncodeOnce`）；E2E `SpecialCharacterRoundTripTests.Grid_WithEncodeHtmlContentOn_ShowsTheSavedTextAsTyped`（已移除 `[Ignore]`）

## 24. `_Framework` 通用資料端點會檢查所請求的 view model（Breaking（安全，對手寫呼叫的頁面））

**範圍**：`/_Framework/GetPagingData`、`GetEmptyData`、`Selector`、`GetExportExcel`、`GetExcelTemplate`、`UpdateModelProperty`。這幾個 action 依請求中的 `_DONOT_USE_VMNAME` 建立 view model，現在會檢查該 view model 對呼叫者是否可用（`_FrameworkController` 類別層級仍是 `[AllRights]`）。同一節也涵蓋連線由伺服器決定，以及 `RemoveUserCacheBy*` 的管理員檢查。設計比較與取捨見 [docs/security/generic-endpoint-authz.md](docs/security/generic-endpoint-authz.md)。

**變更**：

| 項目 | 行為 |
|---|---|
| 渲染 view model 的頁面 | LayUI TagHelper 在 `_DONOT_USE_VMNAME` 旁寫入 `_DONOT_USE_VMREF`：`wt:grid`（隨 `where` 一起送出，匯出連結也帶上）、`wt:selector`（ffilter）、`wt:downloadTemplateButton`（連結）。範本產生的頁面不需修改 |
| 參考的內容 | 以 ASP.NET Core Data Protection 保護的不透明字串，綁定：view model、目的（列表、選擇器、匯出、範本、更新）、使用者與租戶、連線 key、頁面的選單權限 url、到期時間。表格的參考包含「列表」；只有表格對該使用者有匯出按鈕（`_Framework/GetExportExcel` 或標記為匯出的動作，含按鈕組內的按鈕，且按鈕沒有因權限被隱藏）且不在 Selector 彈窗內時才加上「匯出」；標記為匯出的動作雖然送到自己的 controller 也算，因為表格的標準匯出流程依賴它。使用本地資料的表格（`use-local-data`）不寫參考。Selector 的參考只有「選擇器」，彈窗內的表格只有「列表」 |
| 端點 | 先驗證參考（簽章、到期、使用者與租戶、view model、目的、連線 key、使用者目前仍能開啟該頁面）才建立 view model；沒有有效參考的請求被拒絕（一律回 HTTP 200：帶 `layuisearch` 標頭回 `{"Code":403}`，其他回「沒有權限」文字，包括帶 `Authorization` 標頭的呼叫者；與 `PrivilegeFilter` 對 API 呼叫者回 401/403 狀態碼的做法不同） |
| 語意 | 與功能頁相同：頁面本身已通過 `PrivilegeFilter`（選單權限、`[Public]`、`[AllRights]`、管理員角色、租戶）；`[AllRights]` 頁面上的 view model，任何已登入者可用；選單權限的頁面，使用者失去權限後參考立即失效 |
| `Selector` | 維持 `[Public]`，但必須帶有效參考；匿名者只能使用公開頁面寫出的參考。Selector 內的表格沿用原頁面的權限 |
| 連線 key | `GetPagingData`、`GetExportExcel`、`Selector`：請求的 `_DONOT_USE_CS`／`_DONOT_USE_CURRENTCS` 必須與頁面的連線 key 相同，並以設定中該連線的拼法執行；`ExemptViewModels` 內的 view model 沒有參考可比對，忽略請求帶的 key，使用伺服器為該請求選的連線（設定 `AllowRequestConnectionKey=true` 時才採用請求的 key；`Legacy` 模式維持原行為）（比較時去掉讀取副本的後綴：`x_1` 與 `x` 視為同一個連線，因為讀取時副本是隨機挑的）；`GetEmptyData`、`UpdateModelProperty`、`GetExcelTemplate` 沒有連線參數，改為在參考綁定的連線上執行：該 key 是頁面產生時由伺服器決定、並受簽章保護的，所以使用 `[FixConnection]` 或非預設資料庫的頁面（包括匯入頁的範本按鈕）照常運作。參考綁定的 key 必須仍是設定中的連線，否則拒絕；參考的 key 為空（頁面使用預設連線）時沿用伺服器為該請求選的連線；寫入（`UpdateModelProperty`）一律用基底連線，不用讀取副本；不在 `Connections` 內的請求 key 一律拒絕 |
| 連線由伺服器決定 | 動作的連線由 `[FixConnection]`、連線函式、預設連線決定。查詢字串的 `DONOTUSECSName` 預設不再影響連線（參考記錄的連線 key 因此就是伺服器為頁面選定的連線）。框架內沒有使用這個參數的程式（`PagedListExtension` 中相關的一行早已註解） |
| `RemoveUserCacheByAccount`／`ByRole`／`ByGroup` | 要求呼叫者具有管理員角色（角色代碼 `001`，與 `_WorkflowController`、資料權限範圍使用的判斷相同）；其他已登入者得到「沒有權限」的回應（帶 `Authorization` 標頭者得到 403）。框架與 `_Admin` 範本清除快取是在伺服器端直接呼叫 `WTMContext.RemoveUserCache*`，不經過這些 HTTP 端點 |
| `UpdateModelProperty` | 需要 `Update` 目的的參考，且 `field` 必須在參考列出的可編輯屬性內；在參考綁定連線的基底連線上執行並真的儲存（#284）；失敗回 400。不論模式與屬性清單，`ID`、`TenantCode`（`ITenant`）、`IsValid`（`IPersistPoco`）、`CreateTime`／`CreateBy`／`UpdateTime`／`UpdateBy` 與非純量、`[NotMapped]`、`[CanNotEdit]` 的屬性一律拒絕，文字無法嚴格轉型也拒絕；`Mode=Legacy` 下此端點現在會真的寫入（原本是空操作）。細節見 docs/security/generic-endpoint-authz.md 第 8 節 |
| 沒有 Data Protection | TagHelper 不寫參考，頁面仍可顯示；端點拒絕沒有有效參考的請求 |

**設定 `GenericEndpoints`**（預設安全，不設定就是下列預設值）：

| 設定 | 預設 | 說明 |
|---|---|---|
| `Mode` | `Enforce` | `Legacy`：恢復原本的行為（不檢查），啟動時輸出 Warning。僅供升級期間暫用 |
| `ExemptViewModels` | 空 | 在 `Enforce` 下，列出的 view model（完整型別名稱，結尾 `*` 為前綴）可由已登入者不帶參考使用，只限這些 view model。不含 `Update`、不含匿名者。啟動時輸出 Warning |
| `ReferenceLifetimeMinutes` | `480` | 參考的有效分鐘數 |
| `AllowRequestConnectionKey` | `false` | `true`：允許查詢字串 `DONOTUSECSName` 為沒有 `[FixConnection]`、連線函式也沒有回傳的動作指定連線（舊行為），也允許 `ExemptViewModels` 內的 view model 採用請求帶的連線 key。預設由伺服器決定連線；啟用時啟動輸出 Warning |

- **誰會受影響**：(1) 「自行用 script 呼叫上述端點」的頁面（沒有 TagHelper 會呼叫 `GetEmptyData`、`UpdateModelProperty`）；(2) 自己的頁面在查詢字串帶 `DONOTUSECSName` 選擇連線的網站（需設定 `AllowRequestConnectionKey=true`，或改用 `[FixConnection]`／連線函式）；(3) 非管理員角色以 HTTP 呼叫 `RemoveUserCacheBy*` 的網站（改由管理員帳號呼叫，或在伺服器端直接呼叫 `WTMContext.RemoveUserCache*`）。使用 LayUI TagHelper 與範本產生頁面的網站不需任何修改
- **升級方式**：
  1. 一般 LayUI 網站：不需動作。升級當下已經打開的頁面要重新整理一次；頁面開著超過 `ReferenceLifetimeMinutes` 也要重新整理（表格會顯示沒有權限）
  2. 自行呼叫端點的頁面：在伺服器端用 `Wtm.CreateGenericEndpointReference(typeof(MyListVM), GenericEndpointPurpose.List)`（`Update` 另需 `editableFields`）取得參考，隨 `_DONOT_USE_VMNAME` 以 `_DONOT_USE_VMREF` 送出；或把該 view model 列入 `GenericEndpoints:ExemptViewModels`
  3. Data Protection 金鑰必須持久化，多實例部署還要共用（cookie 驗證本來就需要）。金鑰是暫時的（容器重啟、IIS 沒有保存金鑰環）時，重啟後已經打開的頁面需要重新整理
  4. 由範本產生、已複製到下游專案的 `_Admin` controller 不在這一節的範圍；範本更新後，下游的複本可能需要套用相同的修改
  5. 有用到查詢字串 `DONOTUSECSName` 的頁面：在 `appsettings` 設定 `GenericEndpoints:AllowRequestConnectionKey=true`（或改成 `[FixConnection]`／連線函式）
- **回滾方式**：設定 `GenericEndpoints:Mode` 為 `Legacy`（連線與 `RemoveUserCacheBy*` 的變更另外用 `AllowRequestConnectionKey` 與退回版本處理），或退回前一個 fork 版本（`Core`、`Mvc`、`TagHelpers.LayUI` 一起退回）
- **已知限制**：
  1. `Update` 的參考由頁面作者透過 `CreateGenericEndpointReference(..., editableFields)` 簽發，可編輯屬性的清單只會和作者列的一樣窄
  2. `ExemptViewModels` 支援前綴（結尾 `*`），太短的前綴會讓底下所有 view model 都免參考；建議只寫完整型別名稱
  3. 參考綁定帳號與租戶，不綁定登入工作階段；在到期（預設 480 分鐘）或頁面權限被移除之前一直有效
  4. 檔案端點（`GetFile` 等）的連線參數，以及 `UpdateModelProperty` 的紀錄層級資料權限，不在這次變更內，另行處理
  5. 使用本地資料的表格（`use-local-data`）不寫參考，所以它的 `_Framework/GetExportExcel` 匯出按鈕不會被通過
- **不受影響**：`DataPrivilege`／`DPWhere`、匯出 API、`CreateVM`、`BaseImportVM`、`EncodeHtmlContent`、沒有 `HttpContext` 的 `WTMContext`；各功能頁自己的 `Search`、`ExportExcel` 等 action 不經過這些端點
- **測試**：Core.Test 的 `GenericEndpointGuardTest`、`GenericEndpointReferenceTagHelperTests`、`RequestConnectionKeyTest`、`FrameworkCacheEndpointAdministratorTest`；E2E 的 `GenericEndpointAuthorizationTests`（用 `ReportController` 這個只有管理員角色能開的頁面，以匿名／無角色／受限角色／管理員的真實請求驗證；另有連線由伺服器決定、Selector 彈窗的參考不能匯出、受限角色與無角色的表格與 Selector 照常可用；`OtherDbController` 是帶 `[FixConnection]` 的測試頁，測試主機另設第二個連線 `other` 與它的讀取副本 `other_1`，驗證範本下載、`GetEmptyData`、`UpdateModelProperty` 在固定連線上運作且寫入不進副本，以及未設定的連線或被改動的參考被拒絕）；`RemoveUserCacheBy*` 的權限矩陣列；權限矩陣的六個端點期望值改為「沒有參考即拒絕」（`exempt` 說明見表格）

## 25. 選擇器彈窗視圖寫入腳本的值依位置編碼（安全強化）

**範圍**：`/_Framework/Selector` 回傳的彈窗視圖（`src/WalkingTec.Mvvm.Mvc/Views/_Framework/Selector.cshtml`，LayUI 受保護路徑）。視圖把請求參數（`_DONOT_USE_KFIELD`、`_DONOT_USE_VFIELD`、`_DONOT_USE_FIELD`、`_DONOT_USE_SEL_ID`、`_DONOT_USE_SUBMIT`、`_DONOT_USE_TRIGGER_URL`、`_DONOT_USE_MULTI_SEL`）與已選資料寫進內聯 script。

**變更**：由新的 `SelectorScript` 輔助類依位置處理，沿用既有的 `JsStringEncoder`、`HtmlAttributeEncoder`：

| 位置 | 處理 |
|---|---|
| JavaScript 字串內（欄位名、取值欄位、Selector id、聯動位址、「只能選一筆」訊息） | `JsStringEncoder.Escape` |
| 在腳本字串內組出的 HTML 屬性值（隱藏欄位的 `name`） | 先 `HtmlAttributeEncoder.Encode`，再 `JsStringEncoder.Escape` |
| `multiSel` | 一律輸出 `true` 或 `false` |
| `SubmitFunc` | 只接受函數名或成員路徑（例如 `onPick`、`MyPage.onPick`）；其他內容不輸出呼叫 |
| 已選資料（JSON） | `<` 寫成等價的 `\u003C`，資料內容不變 |

普通值（識別字、欄位名、普通文字）輸出與修改前相同；`wt:selector` 的屬性（`submit-func`、`trigger-url` 等）用法不變。

- **用戶端：選取列的值寫入隱藏欄位（觸及 LayUI 受保護路徑）**：選擇器彈窗的內聯 script（`Selector.cshtml`）與 `framework_layui.js` 的下載／匯出表單（`DownloadExcelOrPdf`、`Download`）原本把選取列的值（`selected[i][ValName]`、列 id、搜尋條件的名稱與值）直接串進隱藏 `<input>` 的 HTML。現在這些值經既有的 `ff.EncodeHtml` 編碼後再寫入；屬性解析後 POST 的仍是原值，顯示用的 `_Display` 欄位本來就以 `.val()` 設定文字。不含 `& < > " '` 的一般資料輸出與修改前逐位元組相同。附帶差異：值為 `null`／`undefined` 時原本寫成字串 `null`，現在寫成空字串。`UIOptions:EncodeHtmlContent=true` 時，表格列的資料本身已是編碼後的文字，POST 的是列所帶的值（以 ID 或數值為取值欄位的一般用法不受影響）。測試：`JsTests/selector-encoding.test.js`（由 `SelectorEncodingTests` 呼叫，找不到 `node` 時標示為 Inconclusive）、E2E 的 `LayUiSelectorTests.Selector_RowWithSpecialCharacters_IsShownAsTextAndPostedUnchanged`
- **補充**：函數名／成員路徑的檢查以 `\z` 結尾（結尾的換行不算）；觸發連結 script 中附加的 `selected[i][ValName]` 以 `encodeURIComponent` 包起來（網址結果對一般值不變）；被丟棄的 `SubmitFunc` 在渲染時記錄一次 Warning（相同的值只記一次，值截短並移除控制字元）；下載／匯出表單 `action` 與外部頁面 iframe 的 `src` 經 `ff.EncodeHtml` 編碼
- **誰會受影響**：`SubmitFunc` 寫成函數名或成員路徑以外內容（例如整段運算式）的頁面，不再被呼叫。使用 TagHelper 並以函數名指定的網站不需修改
- **回滾方式**：退回前一個 fork 版本（`Core`、`Mvc`、`TagHelpers.LayUI` 一起退回）
- **測試**：Core.Test 的 `SelectorScriptTest`；E2E 的 `FrameworkEndpointTests`（`Selector_OrdinaryRequestValues_…`、`Selector_RequestValuesWithSpecialCharacters_…`），以及既有的 `LayUiSelectorTests`

## 26. `_Framework/SetTenant` 需要登入並檢查目標租戶（安全強化）

**範圍**：`/_Framework/SetTenant`（GET，`tenant` 參數）。

**變更**：

| 項目 | 行為 |
|---|---|
| 未登入的請求 | 與其他需要登入的 action 相同，由 `PrivilegeFilter` 處理（MVC 回登入頁導向的 script，API／帶 `Authorization` 的請求回 401）。原先為 `[Public]` |
| 已登入的使用者 | 控制器仍為 `[AllRights]`，不需要選單權限 |
| 目標租戶 | 由 `WTMContext.SetCurrentTenant` 檢查。主租戶使用者（沒有租戶代碼）：目標為空（回到無租戶）或為存在且啟用的租戶；租戶使用者維持原規則（只能選自己的租戶或其子租戶）。未啟用多租戶（`EnableTenant=false`）時沒有租戶，只接受空目標，行為與之前相同。通過：更新登入 cookie 並回 `location.reload();`，與之前相同 |
| 不通過 | 不改變當前租戶、不更新 cookie，回「沒有權限」提示（`FFResult().Alert`），不含 `location.reload();` |
| 請求方式 | 維持 GET（下游版面以 GET 呼叫）；路由與參數不變；action 回傳型別改為 `Task<IActionResult>` |

- **誰會受影響**：切換到不存在或已停用租戶的呼叫（主租戶使用者原本不會被擋，現在回「沒有權限」，不改變狀態；`Vue3Demo` 的 `/api/_account/SetTenant` 同樣回 `false`）；把 `/_Framework/SetTenant` 登記為公開位址（`IsUrlPublic`）的網站，匿名請求現在得到 401 提示（原為例外）。下游版面對已登入使用者呼叫 `SetTenant?tenant=…`（包含 `tenant=` 空值切回主租戶）不需修改
- **回滾方式**：退回前一個 fork 版本
- **測試**：Core.Test 的 `FrameworkSetTenantTest`；E2E 的 `FrameworkEndpointTests`（匿名、已登入）；兩份權限矩陣表 `_framework/settenant GET`（LayUI：匿名 `redirect-login`；Vue3Demo：匿名 `redirect-login`、Bearer 身分 `401`、cookie 管理員 `ok`）

## 27. 登入一律驗證請求的憑證（安全強化）

**範圍**：`WTMContext.DoLogin(username, password, tenant)`，以及呼叫它的登入入口（Vue3Demo `_Admin` 的 `POST /api/_account/login`、`/api/_account/loginjwt`、LayUI E2EHost 的 `/Login/Login`，以及下游同樣寫法的登入頁）。

**變更**：

| 項目 | 行為 |
|---|---|
| `DoLogin` | 無論呼叫者是否已登入，一律驗證請求的帳號、密碼與租戶，通過後才簽發 token。不再沿用目前登入狀態的帳號、租戶或 remote token；已登入的呼叫者以其他帳號的憑證登入，得到的是該帳號 |
| 重建目前使用者 | `LoginUserInfo` 取值遇到快取失效時呼叫 `ReloadUser(itcode)`。已登入的請求（cookie 或 token 已由認證中介軟體驗證）改由內部方法 `LoadSignedInUser` 依登入狀態的 claims 讀取，不需要密碼，仍簽發供伺服器端呼叫使用的 `RemoteToken`，行為與之前相同；未登入的請求仍走 `DoLogin(itcode, null, null)`（只比對密碼為空的帳號），與之前相同 |
| 公開簽章 | 不變（`DoLogin`、`ReloadUser`、`ReloadUserFunc`）。`RefreshToken()`、`ChangePassword`、`SetTenant`／`SetCurrentTenant` 不呼叫 `DoLogin`，不受影響 |

呼叫端清查（`src`、`demo`、`test`、範本、文件）：`DoLogin` 只有兩個登入 action（Vue3Demo `AccountController.Login`／`LoginJwt`）與 LayUI E2EHost 的 `LoginController.Login`，皆為帶憑證的登入；`ReloadUser` 只有 `WTMContext` 內部的三處（`LoginUserInfo` 取值、遠端 token 解析、`HasMainHost` 的 `null` 帳號），皆維持原行為；`LoadUserFromDB` 不存在。

- **誰會受影響**：在「已登入」的請求中呼叫 `Wtm.DoLogin(account, null, null)` 來取得目前使用者的下游程式碼（改用 `Wtm.ReloadUser(itcode)` 或 `Wtm.LoginUserInfo`）；正常的登入頁與登入 API 不需修改。`HasMainHost` 模式下，`DoLogin` 不再使用登入狀態內的 remote token，改為以帳號密碼向主站台驗證（請求自帶的 `_remotetoken` 查詢參數維持原行為）
- **回滾方式**：退回前一個 fork 版本（`Core` 單獨即可）
- **測試**：Admin.Test 的 `DoLoginPasswordTest`（已登入請求仍驗證密碼、不能取得他人帳號、租戶規則與匿名相同、`ReloadUser` 與 `LoginUserInfo` 重建行為不變）；E2E 的 `FrameworkEndpointTests.Login_WhenAlreadySignedIn_…`、`Vue3ApiTests.Account_SignedInCallers_…`；LayUI 權限矩陣的 `login/login POST`，已登入身分的探測請求（不帶有效憑證）原本回 302，現在重新顯示登入頁（`ok`）。`HasMainHost` 分支沒有單元或 E2E 測試（需要主站台）

## 28. `_Framework` 檔案端點的參考、模式與連線（安全強化，預設 `Audit`）

**範圍**：`/_Framework/Upload`、`UploadImage`、`UploadForLayUIRichTextBox`、`UploadForLayUIUEditor`、`GetFileName`、`GetFile`、`ViewFile`。設計與取捨見 [docs/security/file-endpoint-authz.md](docs/security/file-endpoint-authz.md)。**不在範圍內**：demo／範本的 `/api/_file/*`（`FileApiController`，包括它的 `GetFile`、`GetFileName`、`DownloadFile`、`UploadImage`）不受本節的設定影響（其處理見第 29 節）。不變：`WtmFileProvider` 的伺服器端方法（下游在伺服器端直接呼叫，也不處理內嵌標記）、`DeletedFileIds` 與匯入的 `UploadFileId` 的信任方式。本節只涵蓋框架的 `/_Framework` 檔案端點。

**變更**：

| 項目 | 行為 |
|---|---|
| 簽名參考 | 伺服器渲染的檔案連結（LayUI `wt:upload`／`wt:multiupload`／`wt:image`／`wt:display`、表格的下載與檢視欄、`Utils.GetUrlByFileAttachmentId(Wtm, ...)`、`Wtm.GetFileUrl`／`Wtm.CreateFileReference`）帶 `_DONOT_USE_FREF`：以 Data Protection 保護，綁定檔案 id、目的（檢視／下載／檔名）、連線、使用者（匿名頁面為空）、租戶與到期時間（`FileEndpoints:ReferenceLifetimeMinutes`，預設 480）。上傳回應的 `Data` 另外增加 `Url`、`Ref`（`Id`、`Name` 不變） |
| 模式 | `FileEndpoints:Mode`：`Legacy`（舊行為）／`Audit`（**預設**：沒有有效參考的請求照常提供並記錄 Warning，內容為端點、使用者、`Referer`、原因，不含檔案內容）／`Enforce`（拒絕，形式與通用資料端點相同：HTTP 200，「沒有權限」） |
| 連線 | 請求的 `_DONOT_USE_CS` 不再選擇連線（**立即生效，不分模式**）：有效參考的連線優先；沒有參考時，只有列在 `FileEndpoints:RequestConnectionKeys` 的連線，或 `GenericEndpoints:AllowRequestConnectionKey=true`（恢復舊行為）才採用；其餘使用伺服器決定的連線（為空時 `CreateDC()` 不帶 key，租戶資料庫照常路由）。未設定的 key 乾淨地拒絕（原本是 500） |
| `IsFilePublic=true` | 頁面渲染的連結（參考）到期前匿名可用；只知道 id 的匿名請求：`Audit` 提供並記錄，`Enforce` 拒絕 |
| 內嵌檔案 | 富文本框／UEditor 上傳的檔案由伺服器在 `ExtraInfo` 加上保留前綴，在 `Enforce` 下維持用 id 取得（已登入者；`IsFilePublic` 時匿名亦可）。一般上傳會移除用戶端在 `extra` 帶來的這個前綴。富文本框與 UEditor 的連結不帶參考 |
| 升級前的檔案 | `FileEndpoints:LegacyUploadedBefore`（時間）：`UploadTime` 早於它的檔案在 `Enforce` 下維持用 id 取得。`UploadTime` 與它都以**伺服器的本地時間**比較（`FileAttachment.UploadTime` 以 `DateTime.Now` 寫入），時區或夏令時間調整會影響邊界附近的檔案；`UploadTime` 未設定（0001-01-01）的檔案不算「早於」；截止時間在未來時啟動輸出 Warning（到那時為止的所有上傳，包括新上傳，都會以 id 取得） |
| 應用程式的規則 | `IFileAccessPolicy`（預設全部允許，`TryAdd` 註冊，可取代）：通過參考檢查之後呼叫，傳回 false 即拒絕。**自訂的規則在 `Audit` 模式下已經生效**（`Audit` 只放寬「沒有有效參考」這一項，不放寬應用程式自己的規則）；規則丟出例外時請求被拒絕並記錄 Error；`Legacy` 不呼叫 |
| 回應標頭 | `GetFile` 一律加 `X-Content-Type-Options: nosniff`，`stream=true` 時依副檔名設定 `Content-Type`。瀏覽器會當作網站的一部分執行的檔案（`html`、`htm`、`xhtml`、`svg`、`xml`、`xsl`、`js`，以及副檔名不明但內容看起來像 HTML／SVG／script 的檔案）一律以 `attachment` 送出；圖片、PDF、影片、音訊與純文字維持 `inline`，一般預覽不變 |
| 使用者綁定 | 參考除了使用者（`ITCode`）與目前租戶，也綁定使用者所屬的租戶（`LoginUserInfo.TenantCode`），不同所屬租戶的同名使用者不會共用參考 |
| 拒絕的一致性 | `Enforce`：沒有有效參考時，不存在的檔案與無權取得的檔案得到相同的拒絕，回應不透露哪些 id 存在（`Audit` 對不存在的檔案維持空回應） |
| 記錄 | 拒絕與 `Audit` 放行的記錄含端點、使用者、`Referer`（截為 256 字元並移除控制字元）、原因，不含檔案內容。未登入者的請求（`IsFilePublic` 時可能是爬蟲）記在 Information，已登入者的記在 Warning（較簡單的作法，不另做節流） |
| `ViewFile` | 輸出的連結經 HTML 編碼，`width` 只接受正整數並只寫入數字（其他值視同沒有 `width`）；檔案不存在時回空內容（原本是例外；`Enforce` 見上）；內層的連結帶自己的參考 |
| 富文本框上傳 | 回傳的位址使用實際寫入檔案的連線 key（原本寫入的是伺服器目前的 key） |
| 公開 API | `Wtm.CreateFileReference`、`Wtm.GetFileUrl`、`Utils.GetUrlByFileAttachmentId(WTMContext, ...)`、`IUIService.MakeDownloadButtonWithReference`／`MakeViewButtonWithReference`（預設實作呼叫原方法，既有實作不必修改）、`IFileAccessPolicy`。原有方法與簽名不變 |

**設定 `FileEndpoints`**：

| 設定 | 預設 | 說明 |
|---|---|---|
| `Mode` | `Audit` | `Legacy`／`Audit`／`Enforce`。啟動時 `Legacy` 輸出 Warning，`Audit` 輸出 Information |
| `ReferenceLifetimeMinutes` | `480` | 參考有效分鐘數 |
| `LegacyUploadedBefore` | 未設定 | 見上表 |
| `RequestConnectionKeys` | 空 | 沒有參考的請求可以指定的其他連線；非空時啟動輸出 Warning |

- **升級步驟（Runbook）**：
  1. 升級後什麼都不用做：預設 `Audit`，檔案照常提供，LayUI 頁面（TagHelper、表格按鈕）自動帶參考
  2. 觀察 Warning（「File endpoint … served a request without a valid file reference」，含端點、使用者與 `Referer`），找出仍以 id 取得檔案的呼叫者：自己寫的連結改用 `Wtm.GetFileUrl(id, purpose)`，或在 `<a>`／`<img>` 的位址用 `Wtm.CreateFileReference`；儲存在內容中的舊連結（編輯器之外）若無法改，用 `LegacyUploadedBefore` 保留升級前的檔案
  3. 使用第二個資料庫（`[FixConnection]`、`_DONOT_USE_CS` 指向非預設連線）的頁面：TagHelper 與按鈕產生的連結會帶連線；自己寫的連結請列入 `FileEndpoints:RequestConnectionKeys`（立即生效，不分模式）。富文本框與 UEditor 的內嵌連結不帶參考，連線不是預設時同樣需要列入
  4. 警告不再出現後設 `FileEndpoints:Mode=Enforce`
  5. Data Protection 金鑰必須持久化，多實例部署還要共用；金鑰是暫時的時，重啟後已打開的頁面要重新整理
- **回滾方式**：`FileEndpoints:Mode=Legacy`（連線的決定方式另以 `RequestConnectionKeys` 或 `GenericEndpoints:AllowRequestConnectionKey` 處理），或退回前一個 fork 版本
- **誰會受影響**：自己組出 `/_Framework/GetFile`、`ViewFile`、`GetFileName` 連結的頁面（`Enforce` 前需改用參考）；`_DONOT_USE_CS` 指向非預設連線而沒有走 TagHelper 的呼叫（需列入 `RequestConnectionKeys`）；`GetFile` 的顯式 `default` key 原本會略過租戶資料庫路由，現在與不帶 key 相同（走租戶資料庫）
- **UEditor 回應**：`UploadForLayUIUEditor` 成功的回應改由序列化器產生（成員不變），檔名不能改變回應的結構
- **已知限制**：參考綁定使用者與租戶，不綁定登入工作階段；`wt:ueditor` 的上傳位址是全站共用設定，不帶參考；表格欄位一次簽發每列一個參考
- **測試**：Core.Test 的 `FileEndpointGuardTest`、`FileEndpointContextTest`；E2E 的 `FileEndpointTests`（`E2E_FILE_ENDPOINTS_MODE=Enforce` 或 `Legacy` 以其他模式跑整個 LayUI 站台，預設 `Audit`）、`LayUiUploadTests`、`FrameworkEndpointTests`


## 29. 範本檔案 API（`/api/_file/*`）的參考、模式與刪除（安全強化，預設 `Audit`）

**範圍**：demo／範本的 `FileApiController`（`/api/_file/*`，SPA 的檔案 API）改為使用第 28 節的同一套機制：`FileEndpoints:Mode`、`_DONOT_USE_FREF`、連線的決定方式與 `IFileAccessPolicy`。第 28 節的 `/_Framework` 端點不變。不變：`WtmFileProvider` 的伺服器端方法、`BaseCRUDVM.DoEdit` 與 `DeletedFileIds`、LayUI 的行為（`PrivilegeFilter` 只新增標記屬性，原有的名稱規則保留）。設計見 [docs/security/file-endpoint-authz.md](docs/security/file-endpoint-authz.md) 第 7 節；下游副本的最小修改見 [docs/security/vue3-file-api-downstream.md](docs/security/vue3-file-api-downstream.md)。

**變更**：

| 項目 | 行為 |
|---|---|
| 框架輔助類 | `WalkingTec.Mvvm.Mvc.SpaFileApi`：範本 controller 的每個 action 只呼叫它一次（`Upload`、`UploadImage`、`UploadForEditor`、`GetFile`、`DownloadFile`、`GetFileName`、`GetFileInfo`、`GetUserPhoto`、`DeleteFile`）。不使用基底類別。圖片解碼的限制（`ImageDecodeGuard`）仍在應用程式，以 `SpaImageResizer` 傳入 |
| 標記屬性 | `[FileEndpoint(PublicWhenFilesPublic = ...)]`：`PrivilegeFilter` 在 `IsFilePublic=true` 時把標記了 `PublicWhenFilesPublic` 的 action 視為公開（原有的 `FileApiController` 名稱規則保留）。啟動時若有路由在 `api/_file` 的 controller 暴露 `DeletedFile`／`GetFileInfo` 而沒有標記，輸出 Warning（未更新的副本） |
| 讀取的參考 | SPA 的 `<img>` 不能帶憑證，所以讀取用**不綁定使用者的短期參考**（`Wtm.CreateFileCapability`）：綁定檔案、連線、租戶與到期時間（`FileEndpoints:ReferenceLifetimeMinutes`），用途為 `Read`，不含 `Delete`、`Upload`。列表的純文字回應在檔案欄位旁加 `<欄位>__fref`（欄位的格式為下載／檢視按鈕時；HTML 模式與匯出不加）；詳細資料 `BaseCRUDVM.FileRefs`（檔案 id → 參考；僅從資料庫讀出的記錄、僅唯讀屬性、不可由請求繫結）；上傳回應的 `Ref` |
| 讀取的模式 | 連線一律先由 `ResolveFileConnection` 決定（所有模式；未設定的 key 拒絕）。`Legacy` 照舊；`Audit` 沒有有效參考的請求照常提供並記錄；`Enforce` 只接受有效參考。未登入且 `IsFilePublic=false` 時：`Enforce` 只接受有效參考，另外內嵌與 `LegacyUploadedBefore` 之前的檔案可用 id **顯示**（僅 `GetFile`／`GetUserPhoto`），`Audit` 提供並記錄。拒絕為 HTTP 403 |
| 上傳 | 需要登入；移除客戶端在 `extra` 帶來的內嵌標記；連線由 `ResolveFileConnection(csName, null, Upload, 參考)` 決定；回應保留 `Id`、`Name`、`errno`、`data.url`（舊編輯器用的 id 位址），新增 `Url`（含參考的 `GetFile` 位址）、`Ref`、`DeleteRef`（綁定使用者的刪除參考）。`UploadImage` 仍經 `ImageDecodeGuard`；新增 `UploadForEditor`（伺服器加內嵌標記，回應 wangEditor 的格式 `errno`／`data.url`） |
| 刪除 | `DeletedFile` 需要登入，且需要發給**該使用者、該檔案**的 `Delete` 參考（綁定使用者、租戶、到期）；沒有時拒絕並記錄，**除 `Legacy` 外所有模式（含 `Audit`）**。接受 GET（相容）、POST、DELETE |
| `GetFileInfo` | 所有模式都只回傳 `Id`、`FileName`、`FileExt`、`Length`、`UploadTime`，不含檔案內容、路徑、`ExtraInfo`、`HandlerInfo` |
| `GetUserPhoto` | 保留主站台轉址；使用者的頭像（該 id 是租戶內可見、有效使用者的 `PhotoId`，且為圖片類型，不含 SVG）不需參考即可顯示（新的 `Avatar` 通過方式，`IFileAccessPolicy` 仍會被詢問），其他 id 依 `GetFile` 的規則。`AccountController.Reg` 不再採用客戶端帶來的 `PhotoId`；範本的 `FrameworkUserVM` 新增或變更 `PhotoId` 時，只接受存在、為圖片類型（不含 SVG）、且沒有被其他記錄的 `FileAttachment` 外鍵使用的檔案（`Wtm.IsPhotoAssignable`）。`IsUserPhoto` 的結果（含否定）依連線、租戶與檔案快取 30 秒，所以頭像被移除後最多 30 秒仍可顯示。`IFileAccessPolicy` 會收到 `Admission=Avatar`，應用程式可據此限制頭像。導覽列的頭像：`CheckUserInfo` 在 `Attributes.PhotoRef` 帶該頭像的參考，沒有時用 `GetUserPhoto`；主站台轉址保留查詢字串（含 `_DONOT_USE_FREF`）；自己寫的頭像欄位不會得到 `Avatar` 通過方式 |
| 回應標頭 | 所有 GET 加 `X-Content-Type-Options: nosniff`，`Content-Type` 來自 `FileServing`；`GetFile`／`GetUserPhoto` 對會被瀏覽器當作網站一部分執行的內容用 `attachment`，`DownloadFile` 一律 `attachment`；mp4 保留 range；除非 `IsFilePublic=true`，讀取的回應帶 `Cache-Control: private, no-store`；瀏覽器會執行的內容另加 `Content-Security-Policy: sandbox` |
| SPA | `utils/fileRefs.ts` 在記憶體中保存參考（有上限，登出時清除）；回應攔截器收集 `FileRefs`、`<欄位>__fref`；圖片、檔案上傳元件、表格、匯入元件、頭像、`api/file` 用它組位址，找不到時退回原本的位址。移除已儲存的附件只清除欄位（不呼叫刪除）；只有本次上傳的檔案（有 `DeleteRef`）才會刪除。編輯器改用 `UploadForEditor`。已產生的頁面不需修改 |
| 公開 API／設定 | `FilePurpose.Delete`、`Wtm.CreateFileCapability`、`Wtm.GetFileUrl(…, path, …)`、`Wtm.CreateFileDC`、`Wtm.AuthorizeFileDeletion`、`Wtm.IsUserPhoto`、`Wtm.AuthorizeFileRequest(…, isAvatar)`、`FileEndpointAdmission.Avatar`、`FileEndpointGuard.BuildUrl`／`AuthorizeDeletion`、`BaseCRUDVM.FileRefs`、`SpaFileApi`、`FileEndpointAttribute`。皆為新增；沒有新設定 |

**升級步驟**：(1) 升級後預設 `Audit`，SPA 照常運作；(2) 觀察 Warning（含 `GetFile`、`DownloadFile`、`GetFileName`、`GetFileInfo`、`GetUserPhoto` 的端點名稱與 `Referer`）；(3) 自寫的前端程式碼若直接組 `/api/_file/...` 位址，改用參考（見 runbook）；(4) 設 `FileEndpoints:Mode=Enforce`。**刪除不分模式（除 `Legacy`）都需要刪除參考**：呼叫 `DeletedFile` 的自訂前端必須帶上傳回應的 `DeleteRef`。回滾：`FileEndpoints:Mode=Legacy`（刪除仍需登入），或退回前一個 fork 版本。

**行為變更**：(a) `DeletedFile` 沒有刪除參考時一律拒絕（`Legacy` 除外）；(b) 範本的元件不再在使用者按下移除時立即刪除已儲存的附件（取消編輯會保留檔案；被取代的檔案留在原處，本版不清理）；(c) `GetFileInfo` 的回應只有中繼資料；(d) 匿名註冊忽略 `PhotoId`；(e) 拒絕為 403（`/_Framework` 是 200 加提示）。


**測試**：Core.Test 的 `FileEndpointGuardTest`、`FileEndpointContextTest`（刪除、capability、頭像、租戶路由、URL）、`FileRefsTest`、`SpaFileApiTest`（模式 × `IsFilePublic` × 登入與否、`GetFileInfo`、標頭、上傳、標記、連線、刪除、啟動檢查）；Admin.Test 的 `FileApiControllerTest`、`AccountRegTest`；E2E 的 `Vue3FileApiTests`、`Vue3FileUiTests`（`E2E_FILE_ENDPOINTS_MODE=Enforce` 以 `Enforce` 跑整個 Vue3Demo）與權限矩陣的 `vue3demo` `file-enforce` 設定。

## 30. 圖片解碼的限制（安全強化）

**範圍**：`/_Framework/UploadImage`（帶 `width`／`height`）與 `/_Framework/GetFile`（帶 `width`／`height`）產生縮略圖的路徑。demo 的 `/api/_file/*` 本來就有同樣的檢查（`ImageDecodeGuard`），現在規則放在框架（`WalkingTec.Mvvm.Mvc.Imaging.ImageDecodeGuard`、`ImageDecodeLimits`），兩邊用同一套。不變：不帶尺寸的上傳與讀取、`WtmFileProvider` 與各檔案處理器、LayUI 的標籤與腳本。

**變更**：

| 項目 | 行為 |
|---|---|
| 檢查順序 | 先把內容讀進記憶體（上限 `MaxBytes`），只讀檔頭（`Image.Identify`）檢查格式與尺寸，通過才解碼；解析器只註冊 JPEG／PNG／GIF／BMP／WebP，其他格式（TIFF 等）不會被解析；多影格圖片只解第一格 |
| `UploadImage` | 只有帶 `width`／`height` 時才檢查與解碼（不帶時等同 `Upload`）。不通過（超出限制、格式不允許、無法解析、尺寸超過上限或不是正數）時不儲存，回應與原有的「讀不到圖片」相同（`Code` 404、`Data` 為空的 `Id`／`Name`），記錄 Warning（固定原因，不含檔案內容） |
| `GetFile` | 只有帶 `width`／`height` 時才讀取圖片內容；不帶時輸出儲存的位元組。不通過時輸出原檔（記錄 Warning），不再有例外 |
| 設定 | 配置節 `ImageUploadLimits`：`MaxBytes`（10485760）、`MaxWidth`（8192）、`MaxHeight`（8192）、`MaxPixels`（25000000）、`MaxFrames`（1）、`MaxResizeWidth`（4096）、`MaxResizeHeight`（4096）；缺省、0 或負數取預設值。與 demo 的 `/api/_file/*` 共用同一組鍵 |
| 公開 API | 新增 `ImageDecodeLimits`、`ImageDecodeGuard`（`TryLoad`、`Validate`、`TryResolveSize`、`Resize`、`CreateResizer`），在新的命名空間 `WalkingTec.Mvvm.Mvc.Imaging`（避免與下游複製的同名型別衝突）。demo 的 `WalkingTec.Mvvm.Admin.Api.ImageDecodeGuard`／`ImageDecodeLimits` 保留原有成員，轉為呼叫框架；`SpaFileApi` 與 `SpaImageResizer` 不變 |

**升級步驟**：一般應用不需要修改。需要同時上傳超過 2500 萬像素的原圖並要求縮略圖時，調大 `ImageUploadLimits` 的對應值（記憶體用量隨之增加）。下游複製的 `ImageDecodeGuard.cs` 照常運作；要共用框架的規則，把它換成範本的版本。回滾：把限制調到足夠大，或退回前一個 fork 版本。

**行為變更**：(a) 帶尺寸的 `UploadImage` 對超出限制或無法解析的檔案不再儲存；(b) 帶尺寸的 `GetFile` 對這些檔案輸出原檔；(c) `GetFile` 不帶尺寸時不再讀取圖片內容。

**補充**：(a) `GetFile` 退回輸出原檔記錄在 Information（不是失敗），`UploadImage` 的拒絕記錄 Warning；(b) 帶尺寸的 TIFF 等不在允許列表的格式，以及只給高度（寬度）、按比例推算出的另一邊超過 `MaxResizeHeight`（`MaxResizeWidth`）的圖片，現在得到原檔而不是縮略圖；(c) `MaxFrames` 大於 1 時，影格數與每格像素數不會一起受限（`MaxPixels` 只限制單格），是否調大由維運決定；(d) 儲存串流不能 seek 且超過 `MaxBytes` 時，`GetFile` 重新開啟檔案並輸出完整內容；`stream=true` 時儲存串流不能 seek 也能輸出。

**測試**：Core.Test 的 `FrameworkImageEndpointsTest`（`UploadImage`、`GetFile`：一般圖片、超出限制、格式不允許、非圖片、尺寸超出上限、設定、多影格 GIF、漸進式 JPEG、檔頭與資料不符、不可 seek 的串流、SVG 的標頭、記錄層級）、`FrameworkImageDecodeGuardTest`（每個設定鍵的預設值、`Resize`、多影格、不可 seek 的來源）、`ImageDecodeGuardTest`（demo 的型別）。

## 31. 回應不輸出檔案附件的儲存細節（安全強化）

**範圍**：回應（`UseWtmJsonOptions` 的輸出）中的 `FileAttachment`，包含 `BaseCRUDVM.GetById` 填入的導覽屬性（例如 `Student.Photo`）。不變：`GetById` 的載入方式、伺服器端程式讀取這些屬性的行為（`WtmFileProvider`、檔案處理器、下游的匯入 view model）、JSON 輸入的繫結、`DefaultPostJsonOption`（框架發給其他服務的請求）、LayUI 與 Vue3 範本。

**變更**：

| 項目 | 行為 |
|---|---|
| 不輸出的成員 | `Path`、`ExtraInfo`、`SaveMode`、`HandlerInfo`、`FileData`（位元組） |
| 照常輸出 | `ID`、`FileName`、`FileExt`、`Length`、`UploadTime`、`TenantCode` |
| 做法 | `System.Text.Json` 的 type info resolver modifier（`FileAttachmentOutputExclusion`，擴充方法 `ExcludeFileStorageDetailsFromOutput`），做法與 `UserPasswordOutputExclusion` 相同：只影響輸出，模型上沒有 `[JsonIgnore]`；`FileAttachment` 的子類同樣適用，只處理 `FileAttachment` 自己宣告的成員；既有的 resolver 與 modifier 保留；`PocoConverter` 複製選項時保留 resolver |
| 設定 | 無新設定 |

**升級步驟**：一般應用不需要修改。下游的前端若讀取回應中的 `Photo.Path`、`ExtraInfo`、`SaveMode`，改用 `ID` 取得檔案（`/api/_file/GetFile/{id}` 或 `/_Framework/GetFile`）。回滾：退回前一個 fork 版本。

**行為變更**：回應裡 `FileAttachment` 物件不再有上述五個成員。

**補充**：(a) 宣告型別為 `IWtmFile` 的屬性（含 `List<IWtmFile>`）同樣不輸出這些成員與 `DataStream`；(b) 經共用 JSON 設定寫入的物件也一樣：例如以 `Cache.Add`／`Wtm.ReadFromCache<T>` 放進快取的 `FileAttachment`，快取裡不再有這些欄位，讀回來為空（需要這些欄位的快取內容請快取自己的型別，或只快取 id 再從資料庫讀）；讀取（反序列化）與 `CallAPI` 送出的請求內容（`DefaultPostJsonOption`）不受影響。

**測試**：Core.Test 的 `FileAttachmentOutputTest`（輸出、子類、`IWtmFile` 型別、巢狀、輸入、伺服器端值、快取、`DefaultPostJsonOption`、`GetById` 的詳細資料）。
