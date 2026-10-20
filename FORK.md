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
| `WalkingTec.Mvvm.sln` | 加回 `test/WalkingTec.Mvvm.Admin.Test` | #18 |
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
| `src/WalkingTec.Mvvm.Mvc/Helper/WtmMiddleware.cs` | 非同步讀取 body、`BufferJsonBodyOnly`；workflow 內部呼叫的憑證轉發與錯誤狀態碼；內部呼叫目標改用 `InternalCallBaseAddress`／AllowedHosts 驗證 | #17、#13、#172 |
| `src/WalkingTec.Mvvm.Mvc/Filters/FrameworkFilter.cs` | 依 `ExposeExceptionDetails` 決定是否輸出例外細節 | #26 |
| `src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/{Upload,MultiUpload,UEditor}TagHelper.cs` | 安全取得表單的 model | #88 |
| `src/WalkingTec.Mvvm.TagHelpers.LayUI/**`（Form、Tree、LinkButton、DataTable、Chart、`BaseButton`、`BaseFieldTag`、`BaseElementTag`、`LayuiUIService`…） | 嵌入 JS 字串的值改用 `JsStringEncoder` 轉義；HTML 屬性值改用 `HtmlAttributeEncoder` 編碼；選用的 HTML 內容編碼（`UIOptions:EncodeHtmlContent`） | #24、#25 |
| `src/WalkingTec.Mvvm.Core/ConfigOptions/UIOptions.cs`、`src/WalkingTec.Mvvm.Core/Extensions/ListVMExtension.cs` | `EncodeHtmlContent` 選項、表格儲存格的一般值依設定編碼 | #24 |
| `src/WalkingTec.Mvvm.Mvc/Filters/PrivilegeFilter.cs`、`FrameworkFilter.cs` | 嵌入 JS 字串的值改用 `JsStringEncoder` 轉義 | #24 |
| `src/WalkingTec.Mvvm.Mvc/Helper/FResultExtension.cs` | 訊息轉義；修正 `RedirectUrl` | #25 |
| `src/WalkingTec.Mvvm.Mvc/framework_layui.js` | 全域變數查找改用 `window[...]` | #25 |
| `test/WalkingTec.Mvvm.Test.Mock/MockWtmContext.cs` | 選用參數 `remoteToken` | #1 |
| `test/WalkingTec.Mvvm.Admin.Test/**` | 改為測試 Vue3Demo 的 `_Admin` API | #18 |
| `demo/WalkingTec.Mvvm.Vue3Demo/appsettings.json` | 移除範例密碼，金鑰改為佔位值 | #15 |
| `demo/WalkingTec.Mvvm.Vue3Demo/Areas/_Admin/Controllers/FileApiController.cs` | `UploadImage` 參數順序修正 | #11 |
| `Dockerfile`、`alpine.Dockerfile`、`.dockerignore` | 改為建置 Vue3Demo，含前端建置 stage；刪除 `wtmdoc.Dockerfile` | #16 |
| `demo/WalkingTec.Mvvm.Vue3Demo/ClientApp/wtmbuild.ts`、`public/menu.json` | 路徑改為跨平台；輸出依 key 排序 | #35、#37 |
| `README.md`、`README.zh-CN.md` | 開頭多一段 fork 說明（其餘內容不動，降低同步衝突） | #19 |
| Dependabot 的依賴升級 | 各 csproj／`package-lock.json` 的版本號 | 見各 `dependencies` PR |

fork 自己新增的檔案（上游沒有，不會衝突）：`FORK.md`、`fork.props`、`CHANGELOG.fork.md`、`.github/workflows/*`、`.github/dependabot.yml`、`src/WalkingTec.Mvvm.Mvc/Helper/{WtmEnvironmentHelper（含 Oracle `Version` 檢查）,WtmCorsHelper,WorkflowApiSecurity（含 `WorkflowApiDisabledConvention`、`WorkflowApiStubRemovalConvention`）}.cs`、`src/WalkingTec.Mvvm.Mvc/_WorkflowApiDisabledController.cs`（`EnableWorkflow=false` 時 `/_WorkflowApi/*` 的 404 stub，#210）、`src/WalkingTec.Mvvm.Core/WorkFlow/WorkflowLookup.cs`、`src/WalkingTec.Mvvm.Core/Support/{JsStringEncoder,HtmlAttributeEncoder}.cs`、`src/WalkingTec.Mvvm.Core/Support/FileHandlers/FileNameSanitizer.cs`、`src/WalkingTec.Mvvm.Core/ConfigOptions/WorkflowOption.cs`、`test/WalkingTec.Mvvm.Core.Test/` 下 fork 新增的測試（`TagHelpers/LayUI/**`、`Excel/**`、`*Test.cs`）、`test/WalkingTec.Mvvm.Admin.Test/WorkflowLookupTest.cs`。

## 7. CI、依賴更新與合併

- **CI**：`.github/workflows/ci.yml`（`build-test`）會 build 整個 solution 並跑 Core 測試；`.github/workflows/clientapp.yml`（`clientapp-build`）只在 Vue3Demo 前端有變動時才建置；`.github/workflows/publish.yml` 只在 push `v*` tag（或手動執行）時發佈套件（見第 4 節）。上游留下的 `.azure/pipelines/*`（Azure Pipelines，其中有的釘在舊版 SDK，有的引用已刪除的 Dockerfile）**在本 fork 中不使用**。為了降低同步衝突，這些檔案刻意維持與上游相同，不修改也不刪除（#118）
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

## 11. Workflow 內部呼叫的目標位址（#172）

啟用 `EnableWorkflow` 時，`/v1/activities` 會由 `WtmMiddleware` 轉成一次內部呼叫。原本的目標是 `{scheme}://{Host header}`，在 `RequireAuthentication` 關閉（預設）時 Host header 沒有經過驗證。fork 新增可選設定 `WorkflowOptions:InternalCallBaseAddress`（預設 null），並在沒有設定時用 `AllowedHosts` 驗證：

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

- **已知限制**：`WorkflowOptions:RequireAuthentication=false`（預設）時，未登入者仍可透過 Elsa 的 `/v1/activities` 取得全部角色與群組的 ID 與名稱。這是既有行為，不包含使用者帳號或姓名，另開 Issue 追蹤；建議啟用 workflow 的網站開啟 `WorkflowOptions:RequireAuthentication`
- **租戶範圍變更**：租戶使用者在審批人、角色、群組的解析（設計器下拉選項與審批執行時），現在以自己的租戶為範圍；以前因為是匿名的自我呼叫，查到的是主租戶
- **需要重啟**：`EnableWorkflow` 在啟動時讀取，變更後要重啟才會影響 `_WorkflowApi` 是否存在
- **多主機（`HasMainHost`）**：沿用既有慣例（例如 demo `AccountController.GetFrameworkRoles`）——沒有租戶的使用者呼叫主站，租戶使用者讀本機資料庫。子站使用者必須有 remote token，否則主站會拒絕，解析結果為空，並記一則 Warning（不含 token 與查詢內容），流程不會中斷
- **`WorkflowLookup` 的使用**：給已確認呼叫者身分的程式碼使用；ids／itcode 參數為空時會回傳全部資料（與原 controller 查詢相同），不可直接暴露給未驗證的呼叫者
