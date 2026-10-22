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
| `src/WalkingTec.Mvvm.Core/BasePagedListVM.cs` | 匯出 zip 路徑改用 `Path.Combine`；stylesheet 符合 schema | #89、#87 |
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
| `src/WalkingTec.Mvvm.Mvc/Views/_Framework/Selector.cshtml`、`src/WalkingTec.Mvvm.Mvc/Helper/SelectorScript.cs`（新增） | 寫入內聯腳本的值依所在位置編碼；`SubmitFunc` 只接受函數名或成員路徑 | 第 25 節 |
| `src/WalkingTec.Mvvm.Mvc/_FrameworkController.cs` | `SetTenant` 移除 `[Public]`，檢查 `SetCurrentTenant` 的結果，改為 `async Task<IActionResult>` | 第 26 節 |
| `src/WalkingTec.Mvvm.Core/WTMContext.cs` | `DoLogin` 一律驗證請求的帳號、密碼與租戶；已登入請求的重建流程改為內部方法 `LoadSignedInUser` | 第 27 節 |
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

- **未處理（不屬於選項輸出）**：`combo`、`tree`、`transfer` 的選項以資料物件交給 xm-select、tree、transfer 元件，由元件自行輸出，不在本節範圍；`Download`／`PostForm` 之類組隱藏欄位的程式、頁面片段載入（`LoadPage` 等以 `.html(data)` 放入伺服器回傳的頁面內容，屬設計用途）另案評估
- **影響範圍**：隨 NuGet 發佈的 `WalkingTec.Mvvm.Mvc`（`framework_layui.js`）；使用 LayUI checkbox／radio 連動與 `LoadComboItems` 的頁面。已快取舊版 `framework_layui.js` 的瀏覽器需要重新載入
- **下游提醒**：自行用 JavaScript 串接選項 HTML 的頁面，請對 `value`、`title` 與文字內容使用 `ff.EncodeHtml`
- **測試**：`test/WalkingTec.Mvvm.Core.Test/JsTests/option-encoding.test.js`（node，不需瀏覽器與網路）由 `OptionEncodingTests` 呼叫；找不到 `node` 時該測試標示為 Inconclusive

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

另有 Gadget 模組的批次修改、Excel 匯入與範本下載，以及 `/_Framework/*` 端點（`FrameworkEndpointTests`：沒有按鈕入口的端點用已登入或匿名的 HTTP 請求測）。`wt:ueditor` 只檢查伺服器輸出（UEditor 的 script 不在共用靜態檔內）。`wt:flowinfo` 在沒有 workflow 的 host 只能測到空的時間軸。

**特殊字元**：Lab 的測試資料含需要 HTML／JS 編碼的字元（角括號、`&`、單雙引號、反斜線、`</script>`、樣板分隔符與非 ASCII 文字），每個「看起來像標籤」的片段都帶 `id="e2e-probe…"`。測試檢查：文字原樣顯示、原樣送回伺服器（`/Lab/LastPost` 回傳瀏覽器實際送出的表單），而且沒有任何元素是由這些文字產生的。

**`UIOptions:EncodeHtmlContent`**：測試 host 預設開（`appsettings.json`），Lab 的顯示與編碼測試都以開啟為前提；預設值（關）下，標題、標籤、按鈕文字與表格儲存格會原樣輸出，另有測試固定這個相容行為（`LayUiHtmlEncodingOptionTests`）。測試要暫時切換時用 `SetHtmlContentEncodingAsync(bool)`（呼叫 host 的 `/Lab/HtmlEncoding`，測試結束自動還原）。另外要留意：框架在綁定請求時，預設會把字串先做 HTML 編碼（`StringIgnoreLTGTBinder`），所以經表單存進資料庫的值本來就是編碼過的；`Gadget` 這類資料用預設設定（關）顯示才正確，Lab 的種子資料則是未編碼的原始文字。表格顯示端對這種值只編碼一次的行為見第 23 節（#286）。

**待處理（`[Ignore("#N: …")]`）**：下列測試寫的是「應有的行為」，目前因產品行為不符而略過，修正後移除 `[Ignore]` 即可。清單與原因都在各測試的 `Ignore` 訊息：
- `checkbox`／`radio`／`combobox`／`transfer`／`tree`／`treecontainer` 的選項文字含標記字元時，由元件寫入為 HTML（元素被建立）（#279）
- `wt:panel` 的標題沒有經過其他標題（card、tab、fieldset、button）使用的 HTML 編碼（#279）
- 可編輯表格的儲存格文字含單引號，在表格重繪（新增、刪除列）後被截斷（#279）
- `wt:transfer` 移動任一項目後，只送出自頁面顯示後移動的項目，原本已選的值遺失（#280）
- `wt:slider` 不依綁定欄位的值設定把手位置（只看 `default-value`）（#281）
- `wt:multiupload` 的刪除鈕呼叫的 `/api/_file/DeletedFile/{id}` 沒有任何 controller 提供（#282）
- `ff.clearSelector` 找不到 selector 的隱藏 id 欄位（容器名稱寫成 `id_Container`）（#283）
- `_Framework/UpdateModelProperty` 回傳 Success，但資料沒有存入（#284）
- 匯出 Excel 會移除儲存格中看起來像 HTML 標籤的文字（#285）

**JS 覆蓋率**（`framework_layui.js`，`/_js/framework_layui.js`）：Playwright for .NET 沒有 `page.Coverage`，所以測試經 CDP session 呼叫 `Profiler.startPreciseCoverage`／`takePreciseCoverage`（呼叫次數＋區塊範圍），在每個測試結束時收集，整個 run 彙總。函式只要有任何一個測試呼叫過就算涵蓋。輸出 `TestResults/e2e/js-coverage/framework_layui.report.md|json`（函式、具名函式、字元範圍三種數字，與未被呼叫的函式清單），CI 放進 step summary 與 artifact `e2e-js-coverage`。**只記錄基線，不因數字低而失敗。** 會整頁重新載入的測試（例如切換語言）要在重新載入前呼叫 `FlushJsCoverageAsync()`，否則舊文件的資料會遺失。目前基線：函式 86／95（90.5%），具名函式 65／69（94.2%），字元範圍 72.6%。沒有被呼叫的 9 個：舊瀏覽器用的 `String.startsWith` 補丁、`LoadPage` 開新視窗的分支、`OpenDialog2` 的失敗回呼、對話框內圖表縮放的三個回呼，以及舊的 `changeComboIcon`、`refreshcombobox`（後者依賴不在靜態檔內的 `layui.formSelects`）。

**路由覆蓋**：所有 `_Framework` 端點與 Gadget 動作都有測試命中；排除清單沒有新增項目（沒有「不可達」的端點，需要外部網路、多站台或 `DebugOnly` 的維持原排除）。`SiteProcess.AssertRouteHitAsync` 讓測試可以直接檢查命中紀錄（批次刪除的測試用它確認 `gadget/batchdelete`、`gadget/dobatchdelete` 都被記錄）。基線 16／42 個可測動作，現在 70／70（含 28 個 Lab 測試用動作；原本的 42 個全部命中）。

**執行時間與 nightly**：目前整套（158 個測試，其中 14 個 `[Ignore]`）約 2 分鐘（本機）。若之後變長，在測試加上 `[TestCategory("nightly")]`；`e2e-smoke` 已用 `--filter "TestCategory!=nightly"` 排除它們，要跑時：`dotnet test test/WalkingTec.Mvvm.E2E -c Release --no-build --filter "TestCategory=nightly"`（本 Issue 沒有新增排程 workflow）。

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

排除清單：`test/WalkingTec.Mvvm.E2E/Coverage/vue3demo.route-exclusions.json`（每條都有 `id` 與 `reason`）。排除的是：程式碼產生器（`DebugOnly`）、未啟用的 workflow、呼叫外部網站的 action、需要主站台的 action、LayUI 圖示目錄，以及伺服器端渲染 UI 專用的 `_Framework` 輔助 action（SPA 只呼叫 `/api/*`）。目前基線：共 222 個 action，排除 70，可計入 152，全部命中（100%）。LayUI 目標的數字見其自己的報告。仍只記錄基線，**不因覆蓋率低而失敗**。

#### CI 與執行時間

`e2e-smoke` 在安裝 Node（npm 快取以 `package-lock.json` 為 key）後執行 `npm ci` 與 SPA build，再 build 與執行兩個目標；報告 artifact `e2e-route-coverage` 內含兩個目標的報告。兩個目標合計約 1.5 分鐘（Vue3Demo 有 40 個執行中的測試，另有 8 個 `Ignore`），遠低於 12 分鐘，所以不另設 `nightly` 分類；之後若有長時間測試，用 `[TestCategory("nightly")]` 標記並以 `--filter TestCategory!=nightly` 排除。

#### 已知問題（`[Ignore("#N: ...")]`）

測試發現的產品問題不在這個 PR 修，對應的測試以 `[Ignore("#N: ...")]` 保留預期行為（修好後移除 `Ignore` 即可）。列出方式：`grep -rn 'Ignore("#' test/WalkingTec.Mvvm.E2E/Tests`。

- #288：匯入檔含無效資料列時，回應是「Memory stream is not expandable.」，而不是列出錯誤並提供錯誤檔
- #290：使用者管理頁呼叫 `/api/_Admin/FrameworkUser/*`（與實際路由 `/api/_FrameworkUser/*` 不符）；`City`／`SchoolVue3` 頁的匯出按鈕與資料權限列表的詳細按鈕檢查了不存在的權限鍵而不顯示；學校頁匯出勾選列不會下載；租戶列表的名稱欄為空
- #291：只符合子群組的搜尋回傳空列表（這個 Issue 也包含群組列表的備註欄一律為空，該項沒有對應的測試）
- 沒有對應測試的觀察：缺漏的 i18n 鍵、`FrameworkMenu/GetIcons` 回 400、刪除選單後選單快取未更新 → #292；匯出時以 `SetFormat` 格式化的欄位輸出型別名稱 → #289

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

觀察到的行為與預期不同、但本 Issue 不修改 `src`／`demo` 的項目，用 `knownDeviation` 標記（短而中性的描述加上各身分目前的結果）：測試照常通過並在報告中列出，修好後測試會要求移除標記。目前共 4 類，見追蹤 Issue。

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
| `UpdateModelProperty` | 需要 `Update` 目的的參考，且 `field` 必須在參考列出的可編輯屬性內；在參考綁定連線的基底連線上執行 |
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
