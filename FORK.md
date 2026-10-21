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
| `src/WalkingTec.Mvvm.Mvc/Helper/WtmMiddleware.cs` | 非同步讀取 body、`BufferJsonBodyOnly`；`/v1/activities` 要求登入，並在程式內取得 Elsa 的活動描述（不再 HTTP 自我呼叫；10.0.0.6 以前的內部呼叫目標驗證與憑證轉發已移除） | #17、#13、#172、#208、#216 |
| `src/WalkingTec.Mvvm.Mvc/Helper/WorkflowActivitiesGuard.cs`、`WorkflowActivitiesDescriber.cs` | 新增：`/v1/activities` 的登入檢查；複製 Elsa `Activities.List` 的邏輯在程式內產生活動清單 JSON | #208、#216 |
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

- **已知限制**：`/v1/activities` 的匿名存取已在第 15 節（#208）處理：現在一律要求登入。其餘 Elsa `/v1/*` 管理端點仍由 `WorkflowOptions:RequireAuthentication` 控制
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

## 15. `/v1/activities` 一律要求登入，並在程式內取得活動描述（#208、#216，Breaking（安全））

**問題（#208）**：啟用 `EnableWorkflow` 後，流程設計器用 `/v1/activities` 取得活動清單。`WorkflowOptions:RequireAuthentication` 關閉（預設）時，這個路徑沒有任何驗證，未登入者就能取得清單；其中審批活動（`WtmApproveActivity`）的下拉選項包含全部角色與群組的 ID 與名稱。第 12 節（#13）只處理了 `_WorkflowApi`，這個外洩當時列為已知限制。

**變更**：`WtmMiddleware` 對 `/v1/activities` 一律要求登入，與 `RequireAuthentication`、`RequireAdminRole` 的設定無關：

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
- **升級方式**：自行呼叫 `/v1/activities` 的整合請改帶登入 cookie 或 `Authorization: Bearer <JWT>`
- **不再需要的設定與限制**：原本「正式環境必須設定 `WorkflowOptions:InternalCallBaseAddress`」與「密鑰只在單一程序內有效，滾動重啟、多實例或負載平衡器會造成短暫 401、審批下拉選項為空」的限制都已解除，因為不再有內部呼叫。`RequireAuthentication=true` 時，以前沒設定 `InternalCallBaseAddress` 又是 `AllowedHosts: "*"` 的部署，已登入使用者也會得到 502（憑證不轉送到不可信的目標）；現在不會
- **保留的 public API**：`WorkflowOptions.InternalCallBaseAddress`、`WorkflowApiSecurity.ResolveSelfCall`／`SelfCallTarget`／`IsAllowedHost`／`IsExplicitHostList`／`NormalizeInternalCallBaseAddress`／`IsSafeTarget`／`FilterAuthCookie` 沒有刪除（下游可能引用，刪除是 breaking）。它們標註 `[Obsolete]`（`error: false`，只有編譯警告），設定檔裡的 `InternalCallBaseAddress` 仍可綁定，但不再有作用。10.0.0.6 新增的 `WorkflowActivitiesGuard.InternalSecretHeader`、`InternalClientName`、`AddInternalHttpClient`、`WithInternalSecret`、`IsValidInternalSecret`、`IsInternalActivitiesRequest` 只存在於 10.0.0.6，已隨密鑰機制移除
- **回滾方式**：退回前一個 fork 版本（`WalkingTec.Mvvm.Mvc`）。沒有可選開關：匿名取得全部角色與群組是資訊外洩，不提供恢復匿名存取的設定
- **不在範圍**：其餘 Elsa `/v1/*` 管理端點（例如 `/v1/workflow-definitions`）仍由 `WorkflowOptions:RequireAuthentication` 控制，行為不變；HTTP 活動端點、signal 端點、Swagger 也不在範圍。啟動時的 Warning 仍會提醒 `RequireAuthentication` 沒有開啟
