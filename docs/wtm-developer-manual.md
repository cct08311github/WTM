# WTM 開發與使用手冊

> **版本**：10.17.0 | **目標框架**：.NET 10 (LTS) | **最後更新**：2026-07-21
>
> **10.17.0 重點**（BMS 實戰回饋批次，#756–#762；預設零行為變更，兩個 opt-in 新功能＋兩個框架服務修復）：**新增** `AddWtmRefreshTokenRetention()`——`FrameworkRefreshTokens` 的每日 opt-in 清理（#721 後每次登入真寫一列、無界成長；revoked-but-unexpired 列受硬性不變量保護永不刪除,§10.2）；**新增** rate-limit 顯式註冊 `WtmRateLimitingOptions.RegisterPolicy(p,w,q)` + `RequireWtmRateLimit()` 端點擴充——minimal-API 端點不再隱式依賴「某 controller 恰好掛同 tuple attribute」（刪 action 連帶炸 health 端點的下游實案,§10.9）,並修 `ScanWtmRateLimitAttributes` 逐成員 partial-load 防護。**修復** `LookupCacheWarmupService` 不 honor `ConnectionKey`——非 default 連線型別每 boot warm-fail,且 cache key 無 connection 成分、打錯庫可能毒化 runtime 快取整個 TTL（§12.6）；**修復** 兩個 retention 服務的 `RunAtLocalHour` 超界（如 midnight=24 typo）會 fault `Host.StartAsync` 或觸發 `StopHost` 全站停機——現 clamp [0,23] + warning。另 `FrameworkRefreshTokens` 內建三索引（fresh DB 自動生效;既有 DB 需手動 CREATE INDEX）。**升級自 ≤10.14.5 者必讀** `CHANGELOG.md` `[10.15.0]` Migration 段（CPM pin ≥10.0.9 / 既有 DB 建表 / 刪 action 的 attribute 連帶）。詳見 `CHANGELOG.md` `[10.17.0]`。
>
> **10.16.0/10.16.1 重點**（#470 Slices G–M LayUI islandification,全 opt-in）：新 `WtmUIOptions.UseSelectIslandRender`（**預設 `false`**）讓 ComboBox/Tree/Transfer/Upload/laydate/slider/colorpicker/ueditor/textarea-counter/grid-cell 按鈕改以 eval-free JSON island + `data-wtm-*` delegated dispatch 渲染——表單/對話框 CSP 可推向 `script-src 'self'`（§6.11）。**flag-off＝逐位元組舊輸出**——但此保證在 10.15.0/10.16.0 因 #753 迴歸（G/H/I 七個 emitter 未上 gate）並不成立,**10.16.1 才恢復**：回滾/bisect 應在 10.14.5 ↔ 10.16.1 之間直跳。10.16.0 另修復 `WtmDataContextHealthCheck` 從未真探 DB 的 #727 殘留（#741,§14A——過去恆綠的 `/ready` 從此可能真的轉 Unhealthy,operator 需知）,及數項 XSS/encoding 深度防禦。詳見 `CHANGELOG.md` `[10.16.0]`/`[10.16.1]`。
>
> **10.15.0 重點**（"final optimization" 批次,40+ issues；**升級必讀 Migration**）：**安全** P0 —— #721 refresh-token 身分繞過修復：`api/_account/refreshtoken` 從此**強制驗證**出示的 refresh token（舊行為憑有效 AT 即可無限換發）；client 必須送真 `refresh_token`,抄過 demo `RefreshToken` action 的專案必須刪除（含 attribute 連帶檢查）,**既有 DB 必須先有 `FrameworkRefreshTokens` 表**（缺表＝升級後全站登入失敗,fresh staging 測不出,§10.2）。**正確性** —— 四個框架服務（`IWorkflowEngine` timer、`ActionLogRetentionService`、`LookupCacheWarmupService`、`ITokenService` 持久化）先前在所有真實部署中因 `NullContext` DI gap **靜默失能**,#721/#727 修復後首次真跑（注意各自的「首次真跑副作用」：寫哪張表、走哪條連線、對外語意）。**WorkFlow** —— #667 引擎交易全面相容 `EnableRetryOnFailure`,死鎖重試耗盡統一回 `WorkflowActionCode.DeadlockRetryExhausted`（§18.6）。**ETL** —— 欄名 identifier 驗證、REST 分頁預設收緊（`MaxPages=1000`/跨 host 拒絕）、dead-letter 有界化、ReDoS timeout（§8.20）。另含 evidence-based 效能批次與 .NET 10 現代化。詳見 `CHANGELOG.md` `[10.15.0]`（含 Migration 段全文）。
>
> **10.14.5 重點**（安全 patch，#652 修復；預設零行為變更）：修復 #651 的追蹤殘留 #652 —— `<wt:selector>` 搜尋面板的 `$$script$$`/`$$dialoginit$$` sentinel 除了先前已修的 JSON island/inline script 外,也可被**伺服器端渲染的 plaintext**（`WebUtility.HtmlEncode` 不轉義 `$`）偽造：radio/checkbox 選項標籤與值、tree 節點、taginput 標籤、slider/rate/upload/hidden 值等欄位 TagHelper 的 model 衍生純文字,含字面 `$$script$$…$$#script$$` 時會在 `ff.OpenDialog2` 全域還原時被注入為可執行 `<script>`（stored XSS,預設、kill-switch-off 路徑）。修復於單一 tokenization chokepoint：`SelectorTagHelper` 在 tokenize 真實標籤**之前**先將面板內容中每個字面 `$` 轉義為 Private-Use 佔位字元（U+E000）,`ff.OpenDialog2` 在 un-tokenize **之後**才將佔位字元還原為 `$`——偽造的 `$$script$$…` 序列還原後仍是惰性文字,開發者腳本中的真實 jQuery `$` 往返不受影響；同一修復也堵住姊妹 `$$dialoginit$$` island 偽造向量,並將 `$$SearchPanel$$` 模板插入改為 replacer function,防止 `String.prototype.replace` 的 `$$`/`$&` 特殊處理造成二次咬字（連帶修復一個既有的 `Save $$10$$` 類標籤失真 bug）。**已知殘留**：巢狀 `<wt:selector>`（一個 selector 巢狀在另一 selector 的 `<wt:searchpanel>` 內,無 demo 實際使用此組合)在 #627 kill-switch **關閉**時,外層還原仍可能重新啟動內層 sentinel——字串 sentinel 方案無法對任意巢狀深度做到 composition-safe（這正是 #470/#627 退役要解決的問題）；**#627 kill-switch 開啟時可完全緩解**,追蹤於 #655。無需遷移,預設與 kill-switch 路徑皆行為保留。詳見 §6.1.1、§10.7 及 `CHANGELOG.md` `[10.14.5]`。
>
> **10.14.4 重點**（#470 島化推進 + 修復；預設零行為變更）：`ff.OpenDialog2`（`<wt:selector>` 面板路徑）補上 JSON island 機制（#635，slice 0）；`item-url` 的 combobox/checkbox/radio/transfer 改發 island（#633，且同時是 chain target 者決定性讓步給 chain 結果 #645）；checkbox/radio 預設值改走 `data-wtm-defaults` 屬性供 `ff.ChainChange` 無競態讀取（#632），back-compat global 僅以 parse-time inline script 單一發佈（#646/#649）。**安全**：#651 —— tokenize 的 selector 面板中,model 衍生 JSON（島體/inline 寫入）因 System.Text.Json 不轉義 `$`,含 `$$#dialoginit$$$$script$$…` 的值會被 OpenDialog2 全域替換注入可執行腳本（stored XSS）；已將**所有**流入 tokenized 面板的序列化路由經 `LayuiIslandJson.Serialize`(`$`→`$`,可逆)並加 source-sweep guard 鎖住。**修復**:#636（kill-switch 孤兒 token 吞內容）、#638（鏈式 radio 編輯頁空白）。此 release 於 tag 前經跨廠商 review 攔下 1 個 stored-XSS 類 + 3 個時序回歸（#645/#646/#649）,帶 bug 中間態未進任何 release。殘留追蹤:#652（plaintext label 的同類 sentinel 碰撞,pre-existing）。詳見 §6.1.1、§10.7 及 `CHANGELOG.md` `[10.14.4]`。
>
> **10.14.3 重點**（legacy script rehydration kill-switch，#627 / #470；opt-in，預設零行為變更）：新增**選擇性停用 legacy 動態腳本執行**的開關 —— 佈局加 `<meta name="wtm-disable-legacy-script-rehydration" content="true">`（純標記，最嚴 CSP 下可用）或設 `ff.DisableLegacyScriptRehydration = true`（嚴格 boolean），即停用 `framework_layui.js` 的**四個** legacy 執行點（`IsScript` eval fallback、`ff.OpenDialog`/`ff._replayInitFromHtml` 的 inline script 再注入、`ff.OpenDialog2` 的 selector 搜尋面板 `$$script$$` 還原），全部改為大聲診斷（計數 `console.warn` / `console.error`）。**資格注意**：多個 TagHelper 組態（combobox/tree 的 `xmSelect.render`、transfer、ueditor、upload、checkbox/radio 預設值、AJAX partial 內 grid、selector 面板、datetime callback 與 range 分支、帶 callback 的 slider/colorpicker、非識別字 `BeforeSubmit`——非窮舉）**仍發出可執行 inline `<script>`**，含這些 widget 的 AJAX 對話框開啟開關會（大聲地）壞——對多數 CRUD app 而言此開關目前是 *staging 稽核工具*，正式啟用待 #470 widget 島化完成。分級 CSP 硬化配方見新文件 `docs/csp-hardening.md` 與 §10.7；`CHANGELOG.md` `[10.14.3]`。
>
> **10.14.1–10.14.2 重點**（#567 Phase-3/4a — 工具 views 跟隨資產切換 + legacy 樹 deprecation）：**10.14.1（#614）** 七個框架內嵌工具 UI 殼（`_CodeGen/{Index,Gen,SetField}`、`_DashboardPage/{Index,Designer,Render}`、WorkFlow designer 頁）原先硬編 `/layui`（2.6.3），現一律經新 helper `WalkingTec.Mvvm.Mvc.LayuiAssets.ResolveLayuiBase(IConfiguration)` 跟隨 `Layui:Asset`（兩個固定字面值之間選擇、永不串接 config 進 URL）——升級後未設 `legacy` 的 host 這些工具頁改 serve 2.13.8（七頁皆經雙樹真瀏覽器認證）。**10.14.2（#567 Phase-4a）** 正式將 `Layui:Asset=legacy` 與隨附的 2.6.3（`/layui`）樹標記 **deprecated**（CHANGELOG `### Deprecated` + `LayuiAssets` XML `<remarks>`）：**功能完全不變**（`legacy` 照常解析、兩樹續存），僅為移除窗口預告；實際移除排在**下一個 major**，gated on 下游（BMS）prod 遷移完成。套件端稽核確認零硬編 `/layui/` 殘留。見 §6.1.1 及 `CHANGELOG.md` `[10.14.1]`/`[10.14.2]`。
>
> **10.14.0 重點**（bundled layui 預設翻轉 2.6.3 → 2.13.8，#573 / #567 Phase-2b）：demo 佈局殼（`_Layout.cshtml` / `Login.cshtml`）預設改載 **layui-next（2.13.8）** 資產樹。**這是 opt-out 式行為變更**：設定鍵 `Layui:Asset == "legacy"`（精確比對）釘回舊的 2.6.3 樹（`/layui`），其他任意值（含舊 opt-in 值 `"next"`，維持有效）或未設定則選 2.13.8（`/layui-next`）；config 值永不串接進 URL，僅在兩個固定字面值間選擇。翻轉閘經 BMS staging 真瀏覽器 e2e 620/620 sign-off + #565 雙樹 harness 15/15 把關。`wt:richtextbox` 相容性保留：layui 於 2.8 移除 `layedit` 模組，故 2.6.3 的 `layedit.js` + face 圖 + CSS 已 vendor 至 layui-next 樹旁並在 next 樹條件式 `layui.extend`。**升級指引**：要留在 2.6.3 設 `Layui:Asset=legacy`（appsettings 或 `Layui__Asset` 環境變數）即為完整回退路徑（兩樹皆隨附，無移除）；自帶佈局殼的 App 不受影響（資產選擇在 App 端 view，框架從不挑 layui 資產）。詳見 §6.1 及 `CHANGELOG.md` `[10.14.0]`。
>
> **10.13.x 重點**（LayUI 現代化硬化 + 對抗式稽核修復群，10.13.0 → 10.13.17）：**eval-free 對話框島架構完成（#470 退役）** — 對話框/表單初始化改由 JSON island（`ff.DispatchAction`）驅動，一個標準 WTM 對話框現發出**零** inline `<script>`（`framework_layui.js` 全檔僅存 1 個 `eval(`，即已棄用的 `IsScript` 路徑），嚴格 CSP 成為可選；`ff.SafeHtml`（DOMPurify）淨化所有對話框 partial 與 PostForm 重繪，`ff.ConsumeIslandsIn(rootEl)` 公開 API（#587）供 SPA 片段消費島；ChangeFunc/DoneFunc/BeforeSubmit 等具名回呼經 `window[name]` 白名單/黑名單守衛解析（#558/#601），trust boundary 見 `.claude/rules/architecture.md`。**原生無依賴 TagInput**（#571，chip 輸入，`createTextNode` XSS-safe）。**LayUI 雙樹**：opt-in `layui-next`（2.13.8）平行資產（#566），為 10.14.0 翻轉鋪路。安全：`SQLitePCLRaw` 3.0.x override 清除 GHSA-2m69（#393，10.13.5 起 0 NU1903）、`Microsoft.OpenApi` 2.7.5 P0 修復（#528）、多輪對抗式稽核修復（#528–#549 等）。**皆相容 / opt-in**；預設行為在 10.14.0 前不變。詳見各版 `CHANGELOG.md`。
>
> **10.13.0 重點**（#193 商用化 program 完成 — 18 PRs，全 opt-in / 非破壞）：新增獨立套件 **`WalkingTec.Mvvm.FileHandlers.S3`**（S3/MinIO 物件儲存 `IWtmFileHandler`，`AddWtmS3FileHandler(...)`，Core 不引入 AWSSDK 依賴）；WorkFlow HTTP 面補齊（加签/委托/回退 5 端點）；`BasePagedListVM.DoSearchAsync` / `BaseBatchVM` 非同步批次（原子交易 + 逐列驗證）；opt-in 串流匯出（`UseStreamingExport`，NPOI SXSSF 窗格化）；opt-in 分散式 `LookupCache`（`AddWtmDistributedLookupCache`）。**行為變更**：`UIEnum.VUE`（Vue 2，2023-12 EOL）標記 `[Obsolete]`（僅警告，仍可產碼；改用 `VUE3`/`Blazor`）。詳見 `CHANGELOG.md` `[10.13.0]`。
>
> **10.12.0 重點**（WorkFlow Wave 6 — 低代码设计器 + 安全強化）：`AddWtmWorkFlowDesigner()` + `UseWtmWorkFlowDesigner()`（opt-in）啟用 `/_workflow-designer` 低代碼視覺設計器；eval-free、no-CDN、三個嵌入式 IIFE 模組（表單視圖 / 源碼視圖 / SVG 圖形視圖，9 種 NodeKind 全覆蓋）；原始位元組保真（`WtmJsonRaw` 無損數字 codec；unknown fields 存活；no-op 儲存 ContentHash 不變 → `IdempotentNoOp`）；伺服器端草稿（`ProcessDefinitionDraft` **新表，需 migration**，RowVersion If-Match 並發保護，publish 在同一 transaction 刪草稿）；Publish CAS（`expectedBaseContentHash`，伺服器事務內比對，並發衝突 → HTTP 409 `BaseVersionChanged`）；設計器範圍 Antiforgery（`X-WTM-WF-XSRF`，不觸碰全局設定）；URL-RBAC（`WorkflowPrivileges.DesignerPage/DesignerBase`，嚴於 `[AllRights]`）。#296 安全修復：`WebhookWorkflowNotifier` escape-at-sink（已發布流程圖同樣受保護，無需重發布）+ `InvalidNodeKey`/`DuplicateNodeKey` 驗證器（新 publish 時 fail-close）。#297 修復 `framework_dashboard_designer.js` 未列為 EmbeddedResource → 404。詳見 §18.13（Wave 6 設計器）、§18.14（安全修復）及 `CHANGELOG.md` `[10.12.0]`。
>
> **10.12.1 重點**（patch — WorkFlow 引擎並發修復）：#290 修復回退交易 ABBA 死鎖（`ProcessInstance` 改為最後取得，符合 `WorkflowTimer → ApprovalTask → NodeInstance → ProcessInstance` 標準鎖序）+ 加入 Delegate/AddApprover 死鎖受害者重試兜底（`WorkFlowOptions.DeadlockRetryAttempts`，預設 3）；#299 `WorkflowGraphValidator` 拒絕不支援的 `schemaVersion`（`GraphValidationError.SchemaVersionUnsupported`）；#307 測試穩定性（並發斷言接受 `NodeClosed`）。**零 schema 變更；零新 API 曲面。** 詳見 `CHANGELOG.md` `[10.12.1]`。
>
> **10.11.0 重點**（WorkFlow Wave 4+5 — 加签 / 委托 / 超时）：`AddApproverAsync`（加签）讓活躍審批人可在自己的位置前後注入新審批人，透過 `ApproverSetEpoch` CAS 關閉與並發完成的競態；`DelegateTaskAsync`（委托，中途轉辦）透過單語句 CAS 1-for-1 轉讓任務槽，`TotalRequired` 不變；`DelegationResolvingDecorator` 在節點進入前做可遞移替代（hop cap=3，明確 `visited` 集偵測循環，cycle→fail-closed）；`RevokeDelegationAsync` 管理員批量撤回委托；`AddWtmWorkFlowTimers()`（opt-in）啟用背景排程：Remind 催辦鏈、Escalate 分配升級、`AllowTimerAutoAction=false`（預設 fail-closed）保護自動審批/拒絕。`DelegationWindowMode` 預設 `AtAssignment`（授權在 mint 時凍結）；`AtAction` 需顯式 opt-in，且在 Oracle/DaMeng 啟動時阻擋（#270）。schema additive：`Wf_NodeInstance.ApproverSetEpoch` + `Wf_ApprovalTask.{DelegationRuleId, DelegationExpiresUtc, WindowVerifiedUtc, AddDepth}`，`Wf_WorkflowTimer` 無變動。詳見 §18.10（Wave 4 加签）、§18.11（Wave 5 委托）、§18.12（Wave 5 超时）及 `CHANGELOG.md` `[10.11.0]`。
>
> **10.10.0 重點**（WorkFlow Wave 3 — 回退-to-node + 平行審批閘道）：`ReturnToPrevAsync`/`ReturnToNodeAsync` 讓審批人可退回任意*支配*上游節點（非僅發起人），採 supersede-not-delete 跨度丟棄、per-instance `Generation` epoch bump、`MaxReturnLoops` cap（預設 3）、crash-recovery lease（`ReturningLeaseUtc`）、engine-owned transaction；`WorkflowEventLog.Seq` 改由 `ProcessInstance.NextSeq` 一列式 CAS 分配（移除 `MAX(Seq)+1`/SERIALIZABLE，portable，無隔離層級依賴）。新增 `NodeKind.ParallelGateway`/`InclusiveGateway` AND/OR fork、單語句 Join fire CAS（`FireJoinIfSatisfiedAsync`）、孤兒 fail-closed（`DecrementJoinExpectedAsync`）、可達性兜底確保 Join 絕不掛起；`NodeKind.Ack`（阻塞等待確認，有別於非阻塞 Cc）。schema 為 additive（5 張表新增欄位，皆有 `HasDefaultValue`）；**`NextSeq` 需 per-instance 回填為 `MAX(Seq)+1`**，否則上線後第一次事件 append 即衝突。多 token 行為僅在閘道節點觸發，既有單 token 圖完全不受影響。詳見 §18.9（Wave 3）及 `CHANGELOG.md` `[10.10.0]`。
>
> **10.9.0 重點**（簽核引擎釋出，第 4 個 NuGet 套件）：全新 `WalkingTec.Mvvm.WorkFlow` 模組 — 中文企業級審批/工作流引擎，支援**串签/会签/或签**三種審批模式、版本固定的流程定義（canonical JSON + SHA-256 ContentHash）、`GuardedTransition` CAS 原子轉換、`IApproverResolver`（Role/User/ManagerChain）、沙盒化條件路由（白名單 + fail-closed）、撤回/回退/抄送、三個 RBAC 管控控制器、`IWorkflowNotifier`（複用 `IWtmWebhookSink`，opt-in `AddWtmWorkFlowNotifications`，post-commit best-effort）、雙軌稽核（`[AuditChanges]` + append-only `WorkflowEventLog`）、ProcessDefinition Admin Grid。**安全預設**：`InitiatorAutoApprove = false`、`AutoApproveOnMissingHandler = FailClose`（#250）。消費者需呼叫 `modelBuilder.ApplyWorkFlowModels()` 並執行自己的 migration（零內建 migration，與 Etl 相同）。`DBTypeEnum.Memory` 不支援（啟動即報錯）。詳見 §18（WorkFlow 模組）及 `CHANGELOG.md` `[10.9.0]`。
>
> **10.8.0 重點**（Dashboard BI + ETL 功能釋出，epic #193；全 opt-in，無預設行為變更）：無代碼拖拉式 **Dashboard 設計器**（`_DashboardDesignerController` `/_dashboard-designer`，`[AllRights]` + 伺服器端租戶 + `AllowedWidgetTypes`/`FilterConfig.AllowedOps` allowlist，見 §9.13）；**DB-backed dashboard store**（`AddWtmEfDashboardStore` → `EfCoreDashboardService`，租戶範圍 + `IDashboardService.DeleteAsync(id, tenantId)`）+ 跨 widget drill-down；**KPI 阈值告警**（`AddWtmDashboardAlerts` → `WidgetThreshold`/`ThresholdEvaluator`）+ **排程快照/匯出**（`AddWtmDashboardSnapshots`，`DashboardExcelExporter` Excel + 可插拔 `IDashboardRenderer` PDF/PNG）；**共用 webhook sink**（`AddWtmWebhookSink`/`AddWtmWebhookSinks` → `IWtmWebhookSink`，钉钉/企微/飞书/Slack/Teams，SSRF-hardened，見 §10.10）；**ETL 連接器**（`EtlSourceRegistry` + `CsvEtlSource`/`ExcelEtlSource`/`PostgreSqlSource`/`MySqlEtlSource`/`RestEtlSource`，見 §8.19）+ **ETL governance**（`IEtlGovernanceStore`/`DbEtlGovernanceStore`、dead-letter/血緣/per-tenant 隔離、`AddWtmEtlAlerts`）。新增依賴 `Npgsql` 10.0.2 + `MySqlConnector` 2.4.0。詳見 `CHANGELOG.md` `[10.8.0]`。
>
> **10.7.0 重點**（商用化硬化 + CodeGen v2，epic #193）：一批 security & correctness 修復（建議升級）+ 特性驅動的代碼生成系統。安全：MVC controller 授權漏洞（`UpdateModelProperty` 改走 `DoEdit` + `CanEditProperty` hook、connstring 白名單、`Selector` 改 `[AllRights]`、`IsQuickDebug` startup guard，見 §10）、租戶隔離 + RBAC 稽核（`SetDuplicatedCheck` 租戶範圍、`FileUploadOptions.EnforceTenantFileScope`、RBAC entity `[AuditChanges]`）、Grid/TagHelper XSS 編碼、Dashboard widget 強化（method/header/Op/port allowlist、`RestWidgetDataSourceOptions.AllowedPorts`）。新增：CodeGen 特性 `[ListColumn]`/`[SearchField]`/`[FormField]`/`[ImportConfig]`（codegen + runtime 雙消費）、**regenerate-safe 兩區產生**（`*.Generated.cs` + partial），見 §16.5。**行為變更（需注意）**：`Selector` 改需驗證（`AllowUnauthenticatedSelector=true` 還原）；多租戶 dup-check 改租戶範圍。詳見 `CHANGELOG.md` `[10.7.0]`。
>
> **10.6.0 重點**（ETL + OLAP 深度優化，#179）：效能改善皆為內部、不改變可觀察行為；新增的調校旋鈕一律 opt-in 並預設維持舊行為。新增 ETL bulk-loader 調校（`MssqlBulkLoader` 的 `bulkCopyOptions` / `internalBatchSize`、`OracleBulkLoader` 的 `timeoutSeconds`，見 §8.18）、ETL source/schema 調校（`OracleSource.FetchRowCount`、`EtlPipelineConfig.WatermarkSqlType`、`EtlSchemaServiceFactory.CreateWithCache` 的 `CachingEtlSchemaService` 快取裝飾器，見 §8.18）、OLAP overload（`AnalysisExcelExporter.ExportToStream`、`AnalysisPivotEngine.Pivot(..., fillZero)`，見 §7.17）。修復 ETL MSSQL schema-qualified 欄位查詢（`audit.STG_x` 0 欄位問題，#184）與 scheduler 狀態變更的 `SkipCount` 競態 + `UpdateTime` 稽核（#185）。Analysis 引擎消除一次多餘 DB round-trip 並 per-request 解析策略以維持執行緒安全（#186）；`AnalysisQueryEngine.cs` 拆成 4 個 partial 檔（#190）。詳見 `CHANGELOG.md` `[10.6.0]`。
>
> **10.5.4 重點**（patch，bug-hunt remediation）：對抗式 bug 獵捕（Opus orchestrate → Sonnet 偵查 → 獨立 skeptic 對抗驗證 → Opus review）修復 **CRITICAL(1) + HIGH(20) + MEDIUM(24) + LOW(20) = 65 個對抗驗證確認缺陷**，全數相容（無 breaking change）。新增/變更的使用者可見表面（皆 opt-in 或非破壞多載）：`DataContext.EnableSensitiveQueryLogging`（opt-in，預設 `false`；EF Core 敏感查詢參數日誌現需顯式開啟，避免 debug 模式洩漏 PII，見 §10）；`MssqlBulkLoader` 逾時可設定（建構參數 `timeoutSeconds`，預設 300s，取代原無限 `0`，見 §8）；`IDashboardService.GetWidgetDataAsync` 新增 `tenantId` 非破壞多載（default interface member，租戶隔離，見 §9）；`FilterCondition.Values`（In/NotIn 的 List 形式優先於逗號字串 `Value`，見 §7）；`BaseImportVM` 實作 `IDisposable`（釋放 `XSSFWorkbook`，見 §4.5）；Analysis SaveQuery 加 per-user 數量上限 + `AnalysisSavedQuery.ConfigJson` `[StringLength(65536)]`（見 §7）。詳見 `CHANGELOG.md` `[10.5.4]`。

WalkingTec MVVM Framework (WTM) 是一套 ASP.NET Core 快速開發框架，以四種 ViewModel 類型為核心，搭配內建代碼生成器、LayUI TagHelper、Analysis Mode、ETL 模組（含可視化儀表板）與 Dashboard，提供完整的企業級 CRUD 開發體驗。

10.5.0 一次帶來 31 個新增能力 — 涵蓋 10 個可選 middleware/attribute（維運、可靠度、頻寬、API lifecycle、可觀測性、功能旗標、IP allow-list、cache 控制）、11 個 Analysis Mode 進階 BI 功能（Sort/TopN、相對日期 token、DistinctCount、HavingFilters、GrandTotal、可調 Limits、CompareWith、自動洞察、drill-through、CSV/Excel 總計列）、7 個 ETL 模組強化（Replace 載入模式、欄位對應、Schema 自動探勘、批次重試、表單驗證、3 步驟精靈、可視化儀表板）、3 個安全強化（CSP 三態、frame-ancestors、CSP 違規回報端點）。所有新功能均為**加值式 / 預設關閉**，零行為破壞。

---

## 目錄

1. [快速開始](#1-快速開始)
2. [架構總覽](#2-架構總覽)
3. [Model 層](#3-model-層)
4. [四種 ViewModel](#4-四種-viewmodel)
5. [Controller 層](#5-controller-層)
6. [LayUI TagHelper](#6-layui-taghelper)
7. [Analysis Mode 分析模式](#7-analysis-mode-分析模式)
8. [ETL 模組](#8-etl-模組)
9. [Dashboard 模組](#9-dashboard-模組)
10. [安全機制](#10-安全機制)
11. [多租戶](#11-多租戶)
12. [Lookup Cache](#12-lookup-cache)
13. [測試指南](#13-測試指南)
14. [TimeProvider 時間抽象](#14-timeprovider-時間抽象)
15. [Integration Tests 整合測試](#15-integration-tests-整合測試)
16. [代碼生成器](#16-代碼生成器)
17. [配置參考](#17-配置參考)
18. [WorkFlow 模組 — 審批引擎](#18-workflow-模組--審批引擎)
19. [常見問題](#19-常見問題)

---

## 1. 快速開始

### 1.1 最小可運行應用

**Program.cs**
```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDistributedMemoryCache();
builder.Services.AddWtmSession(3600, builder.Configuration);
builder.Services.AddWtmAuthentication(builder.Configuration);
builder.Services.AddMvc();
builder.Services.AddWtmContext(builder.Configuration);

var app = builder.Build();
app.UseStaticFiles();
app.UseWtmStaticFiles();     // 載入內嵌 JS (/_js/)
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseSession();
app.UseWtm();                // WTM 中介軟體
app.UseWtmContext();         // 每個請求注入 WTMContext
app.Run();
```

**appsettings.json（最小配置）**
```json
{
  "Connections": [
    {
      "Key": "default",
      "Value": "Data Source=app.db",
      "DbType": "SQLite"
    }
  ],
  "CookiePre": "WTM"
}
```

**DataContext.cs**
```csharp
public class DataContext : FrameworkContext
{
    public DbSet<Employee> Employees { get; set; } = null!;

    public DataContext(CS cs) : base(cs) { }
    public DataContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
}
```

### 1.2 建置與測試

```bash
# 建置
dotnet build WalkingTec.Mvvm.sln -c Release

# 執行全部測試
dotnet test WalkingTec.Mvvm.sln -c Release --verbosity normal

# 執行單一測試類別
dotnet test test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj \
  --filter "FullyQualifiedName~AnalysisControllerTests" -c Release

# JS 測試
cd test/WalkingTec.Mvvm.Js.Tests && npm ci && npm test
```

---

## 2. 架構總覽

### 2.1 專案結構

| 專案 | 角色 |
|------|------|
| `WalkingTec.Mvvm.Core` | 核心：ViewModel 基底類、DataContext、Model、Analysis 引擎、Lookup Cache |
| `WalkingTec.Mvvm.Mvc` | MVC 層：Controller 基底、Framework Controller、Filter、TagHelper 支援、內嵌 JS |
| `WalkingTec.Mvvm.TagHelpers.LayUI` | LayUI TagHelper（50+ 個：Grid、Form、Dialog、Selector 等） |
| `WalkingTec.Mvvm.Etl` | ETL 模組：Pipeline、MSSQL/Oracle Loader、Quartz 排程、管理 UI |

### 2.2 請求生命週期

```
HTTP Request
  → WtmMiddleware (request body buffering, upload limit)
  → DataContextFilter (選擇連線字串 + DB 類型)
  → PrivilegeFilter (權限檢查、URL 存取控制)
  → FrameworkFilter (注入 WTMContext、初始化 VM、驗證)
  → Controller Action (業務邏輯)
  → FrameworkFilter.OnActionExecuted (ViewData 注入、頁面標題)
  → FrameworkFilter.OnResultExecuted (寫入 ActionLog 審計日誌)
```

### 2.3 核心關係圖

```
BaseVM ← BaseCRUDVM<T>
       ← BasePagedListVM<TModel, TSearcher>
       ← BaseBatchVM<TModel, TEditModel>
       ← BaseImportVM<TTemplate, TEntity>
       ← BaseTemplateVM

BaseController → WTMContext → IDataContext (EF Core)
BaseApiController ↗              ↓
                          FrameworkContext (多資料庫)
```

---

## 3. Model 層

### 3.1 基底類繼承鏈

```
TopBasePoco          → Guid ID, Checked, BatchError, ExcelIndex (transient)
  ├─ BasePoco        → + CreateTime, CreateBy, UpdateTime, UpdateBy (審計欄位)
  │    ├─ PersistPoco → + bool IsValid (軟刪除，透過 Global Query Filter 排除)
  │    └─ FrameworkUser, FrameworkRole, ... (內建模型)
  └─ TreePoco        → + Guid? ParentId (樹狀結構)
       └─ TreePoco<T> → + T? Parent, List<T>? Children (導航屬性)
```

### 3.2 內建框架模型

| 模型 | 用途 |
|------|------|
| `FrameworkUser` | 使用者（帳號、密碼 PBKDF2、姓名、照片、租戶碼） |
| `FrameworkRole` | 角色（代碼、名稱） |
| `FrameworkUserRole` | 使用者↔角色 M:N 關聯表 |
| `FrameworkGroup` | 群組 |
| `FrameworkUserGroup` | 使用者↔群組 M:N 關聯表 |
| `FrameworkMenu` | 階層式選單（TreePoco<FrameworkMenu>） |
| `FunctionPrivilege` | 角色↔選單 功能權限 |
| `DataPrivilege` | 資料列級權限（Entity + 條件） |
| `FileAttachment` | 檔案附件（metadata + blob） |
| `ActionLog` | 審計日誌（誰、做什麼、何時、IP、結果） |
| `FrameworkTenant` | 多租戶定義 |
| `RefreshTokenEntity` | JWT Refresh Token 儲存 |

### 3.3 關鍵介面

| 介面 | 用途 | 範例 |
|------|------|------|
| `IBasePoco` | 審計欄位 | `CreateTime`, `UpdateBy` |
| `ITenant` | 多租戶 | `string? TenantCode` → Global Query Filter |
| `IPersistPoco` | 軟刪除 | `bool IsValid` → Query Filter 排除 false |
| `ISubFile` | 檔案關聯標記 | 子表附件 |

### 3.4 Model 範例

```csharp
public class Employee : BasePoco, ITenant
{
    [Display(Name = "姓名")]
    [Required(ErrorMessage = "{0}是必填項")]
    [StringLength(50)]
    public string Name { get; set; } = "";

    [Display(Name = "部門")]
    public Guid? DepartmentId { get; set; }

    [Display(Name = "部門")]
    public Department? Department { get; set; }

    [Display(Name = "薪資")]
    [Dimension(DisplayName = "部門", Hierarchy = DateHierarchy.None)]
    public decimal Salary { get; set; }

    [Display(Name = "入職日期")]
    [Dimension(Hierarchy = DateHierarchy.Month)]
    public DateTime HireDate { get; set; }

    public string? TenantCode { get; set; }
}

public class Department : BasePoco
{
    [Display(Name = "部門名稱")]
    [Required]
    public string Name { get; set; } = "";

    [Display(Name = "部門代碼")]
    [RegularExpression(@"^\d{3}$", ErrorMessage = "必須是3位數字")]
    public string Code { get; set; } = "";
}
```

### 3.5 常用 Attribute

| Attribute | 對象 | 用途 |
|-----------|------|------|
| `[Dimension]` | Property | Analysis Mode 維度（GROUP BY 候選） |
| `[Measure]` | Property | Analysis Mode 度量（聚合候選） |
| `[EnableAnalysis]` | Class (ListVM) | 啟用 Analysis Mode |
| `[CacheLookup]` | Class (Model) | 啟用 Lookup Cache |
| `[MiddleTable]` | Class | M:N 關聯表標記（自動處理） |
| `[SoftFK]` | Property | 軟外鍵（名稱對應，非導航屬性） |
| `[CanNotEdit]` | Property | 建立後唯讀 |
| `[ActionDescription]` | Method | 自訂選單/動作標籤 |
| `[Public]` | Method | 不需權限檢查 |
| `[AllRights]` | Method | 僅管理員可用 |
| `[DebugOnly]` | Method | 僅 Debug 模式可用 |
| `[NoLog]` | Method | 不寫審計日誌 |
| `[FixConnection]` | Method | 指定特定連線字串 |

---

## 4. 四種 ViewModel

### 4.1 BaseVM — 所有 VM 的共同基底

**關鍵屬性：**
```csharp
public WTMContext? Wtm { get; set; }     // DI 注入的請求上下文
public IDataContext? DC { get; set; }    // EF Core DataContext
public Dictionary<string, object> FC     // 表單資料集合
public LoginUserInfo? LoginUserInfo      // 當前登入使用者
public IDistributedCache? Cache          // 分散式快取
public IModelStateService? MSD           // 驗證錯誤服務
public IStringLocalizer? Localizer       // 多語系
```

**生命週期方法（可覆寫）：**
```csharp
protected virtual void InitVM() { }      // VM 建立後初始化（載入下拉選單等）
protected virtual void ReInitVM() { }    // 驗證失敗後重新初始化
public virtual void Validate() { }       // 自訂驗證邏輯
```

---

### 4.2 BaseCRUDVM\<T\> — 單筆實體 CRUD

**適用場景：** 新增、編輯、刪除、詳情查看單一實體。

```csharp
public class EmployeeVM : BaseCRUDVM<Employee>
{
    // 下拉選單資料
    public List<ComboSelectListItem>? AllDepartments { get; set; }

    // M:N 關聯的選取 ID
    public List<string>? SelectedSkillIds { get; set; }

    protected override void InitVM()
    {
        // 載入下拉選單
        AllDepartments = DC!.Set<Department>()
            .GetSelectListItems(Wtm!, x => x.Name);

        // 載入已選的 M:N 關聯
        if (Entity.ID != Guid.Empty)
        {
            SelectedSkillIds = DC.Set<EmployeeSkill>()
                .Where(x => x.EmployeeId == Entity.ID)
                .Select(x => x.SkillId.ToString())
                .ToList();
        }
    }

    public override void Validate()
    {
        // 自訂驗證：薪資不能為負數
        if (Entity.Salary < 0)
            MSD!.AddModelError("Entity.Salary", "薪資不能為負數");
    }

    public override void DoAdd()
    {
        base.DoAdd();
        // 新增 M:N 關聯記錄
        if (SelectedSkillIds != null)
        {
            foreach (var skillId in SelectedSkillIds)
            {
                DC!.Set<EmployeeSkill>().Add(new EmployeeSkill
                {
                    EmployeeId = Entity.ID,
                    SkillId = Guid.Parse(skillId)
                });
            }
            DC.SaveChanges();
        }
    }

    public override void DoEdit(bool updateAllFields = false)
    {
        // 重建 M:N 關聯
        DC!.Set<EmployeeSkill>()
            .Where(x => x.EmployeeId == Entity.ID)
            .ToList()
            .ForEach(x => DC.DeleteEntity(x));

        if (SelectedSkillIds != null)
        {
            foreach (var skillId in SelectedSkillIds)
            {
                DC.Set<EmployeeSkill>().Add(new EmployeeSkill
                {
                    EmployeeId = Entity.ID,
                    SkillId = Guid.Parse(skillId)
                });
            }
        }
        base.DoEdit(updateAllFields);
    }
}
```

---

### 4.3 BasePagedListVM\<TModel, TSearcher\> — 分頁列表

**適用場景：** 搜尋、分頁、匯出 Excel、Analysis Mode。

```csharp
// 搜尋條件
public class EmployeeSearcher : BaseSearcher
{
    [Display(Name = "姓名")]
    public string? Name { get; set; }

    [Display(Name = "部門")]
    public Guid? DepartmentId { get; set; }

    [Display(Name = "入職日期起")]
    public DateTime? HireDateBegin { get; set; }

    [Display(Name = "入職日期迄")]
    public DateTime? HireDateEnd { get; set; }
}

// 列表顯示用 DTO
public class Employee_View : Employee
{
    [Display(Name = "部門名稱")]
    public string? DepartmentName_view { get; set; }
}

// ListVM
[EnableAnalysis]  // 啟用 Analysis Mode
public class EmployeeListVM : BasePagedListVM<Employee_View, EmployeeSearcher>
{
    protected override IEnumerable<IGridColumn<Employee_View>> InitGridHeader()
    {
        return new List<GridColumn<Employee_View>>
        {
            this.MakeGridHeader(x => x.Name).SetWidth(150),
            this.MakeGridHeader(x => x.DepartmentName_view).SetWidth(120),
            this.MakeGridHeader(x => x.Salary).SetWidth(100)
                .SetFormat((e, v) => $"{v:N0}"),
            this.MakeGridHeader(x => x.HireDate).SetWidth(120)
                .SetFormat((e, v) => ((DateTime)v).ToString("yyyy-MM-dd")),
            this.MakeGridHeaderAction(width: 200)
        };
    }

    protected override List<GridAction> InitGridAction()
    {
        return new List<GridAction>
        {
            this.MakeStandardAction("Employee", GridActionStandardTypesEnum.Create,  "新增", ""),
            this.MakeStandardAction("Employee", GridActionStandardTypesEnum.Edit,     "編輯", "", GridActionParameterTypesEnum.SingleId),
            this.MakeStandardAction("Employee", GridActionStandardTypesEnum.Delete,   "刪除", "", GridActionParameterTypesEnum.MultiIds),
            this.MakeStandardAction("Employee", GridActionStandardTypesEnum.Details,  "詳情", "", GridActionParameterTypesEnum.SingleId),
            this.MakeStandardAction("Employee", GridActionStandardTypesEnum.BatchEdit,"批量編輯", "", GridActionParameterTypesEnum.MultiIds),
            this.MakeStandardAction("Employee", GridActionStandardTypesEnum.Import,   "匯入", ""),
            this.MakeStandardAction("Employee", GridActionStandardTypesEnum.ExportExcel, "匯出", ""),
        };
    }

    protected override IOrderedQueryable<Employee_View> GetSearchQuery()
    {
        var query = DC!.Set<Employee>()
            .CheckContain(Searcher.Name, x => x.Name)
            .CheckEqual(Searcher.DepartmentId, x => x.DepartmentId)
            .CheckBetween(Searcher.HireDateBegin, Searcher.HireDateEnd, x => x.HireDate)
            .DPWhere(Wtm!, x => x.DepartmentId)  // 資料權限過濾
            .Select(x => new Employee_View
            {
                ID = x.ID,
                Name = x.Name,
                Salary = x.Salary,
                HireDate = x.HireDate,
                DepartmentName_view = x.Department!.Name
            })
            .OrderByDescending(x => x.HireDate);

        return query;
    }
}
```

**搜尋擴展方法：**
| 方法 | 用途 | SQL 對應 |
|------|------|---------|
| `CheckContain(val, expr)` | 模糊搜尋 | `LIKE '%val%'` |
| `CheckEqual(val, expr)` | 精確比對 | `= val` |
| `CheckBetween(begin, end, expr)` | 區間查詢 | `BETWEEN` |
| `CheckWhere(val, predicate)` | 自訂條件 | 任意 WHERE |
| `DPWhere(wtm, expr)` | 資料權限過濾 | 依 DataPrivilege 設定 |

---

### 4.4 BaseBatchVM\<TModel, TEditModel\> — 批量操作

**適用場景：** 勾選多筆後批量修改欄位或批量刪除。

```csharp
// 可編輯欄位定義
public class Employee_BatchEdit : BaseVM
{
    [Display(Name = "部門")]
    public Guid? DepartmentId { get; set; }

    [Display(Name = "薪資調整")]
    public decimal? SalaryAdjustment { get; set; }
}

public class EmployeeBatchVM : BaseBatchVM<Employee, Employee_BatchEdit>
{
    public EmployeeBatchVM()
    {
        ListVM = new EmployeeListVM();
        LinkedVM = new Employee_BatchEdit();
    }
}
```

---

### 4.5 BaseImportVM\<TTemplate, TEntity\> — Excel 匯入

**適用場景：** 從 Excel 批量匯入資料。

```csharp
public class EmployeeTemplateVM : BaseTemplateVM
{
    [Display(Name = "姓名")]
    public ExcelPropety Name_Excel = ExcelPropety.CreateProperty<Employee>(x => x.Name);

    [Display(Name = "薪資")]
    public ExcelPropety Salary_Excel = ExcelPropety.CreateProperty<Employee>(x => x.Salary);

    [Display(Name = "部門")]
    public ExcelPropety Department_Excel = ExcelPropety.CreateProperty<Employee>(x => x.DepartmentId);

    protected override void InitVM()
    {
        // 設定部門下拉選單（Excel 下拉驗證）
        Department_Excel.DataType = ColumnDataType.ComboBox;
        Department_Excel.ListItems = DC!.Set<Department>()
            .GetSelectListItems(Wtm!, x => x.Name);
    }
}

public class EmployeeImportVM : BaseImportVM<EmployeeTemplateVM, Employee>
{
}
```

---

## 5. Controller 層

### 5.1 BaseController vs BaseApiController

| 特性 | BaseController | BaseApiController |
|------|---------------|-------------------|
| 繼承 | `Controller` | `ControllerBase` |
| 回傳 | View/PartialView/FFResult | JSON |
| 驗證失敗 | 重新渲染表單 | 400 + 錯誤 JSON |
| 適用 | LayUI 前端 | SPA / API |

### 5.2 完整 CRUD Controller 範例

```csharp
[ActionDescription("員工管理")]
public class EmployeeController : BaseController
{
    // === 列表 ===
    [ActionDescription("搜尋")]
    public IActionResult Index()
    {
        var vm = Wtm.CreateVM<EmployeeListVM>();
        return PartialView(vm);
    }

    [ActionDescription("搜尋")]
    [HttpPost]
    public string Search(EmployeeSearcher searcher)
    {
        var vm = Wtm.CreateVM<EmployeeListVM>(passInit: true);
        vm.Searcher = searcher;
        return vm.GetJson(enumToString: false);
    }

    // === 新增 ===
    [ActionDescription("新增")]
    public IActionResult Create()
    {
        var vm = Wtm.CreateVM<EmployeeVM>();
        return PartialView(vm);
    }

    [ActionDescription("新增")]
    [HttpPost]
    public IActionResult Create(EmployeeVM vm)
    {
        if (!ModelState.IsValid)
            return PartialView(vm);

        vm.DoAdd();
        if (!ModelState.IsValid)
            return PartialView(vm);

        return FFResult()
            .CloseDialog()
            .RefreshGrid();
    }

    // === 編輯 ===
    [ActionDescription("修改")]
    public IActionResult Edit(string id)
    {
        var vm = Wtm.CreateVM<EmployeeVM>(id);
        return PartialView(vm);
    }

    [ActionDescription("修改")]
    [HttpPost]
    [ValidateFormItemOnly]
    public IActionResult Edit(EmployeeVM vm)
    {
        if (!ModelState.IsValid)
            return PartialView(vm);

        vm.DoEdit();
        if (!ModelState.IsValid)
            return PartialView(vm);

        return FFResult()
            .CloseDialog()
            .RefreshGridRow(vm.Entity.ID);
    }

    // === 刪除 ===
    [ActionDescription("刪除")]
    public IActionResult Delete(string id)
    {
        var vm = Wtm.CreateVM<EmployeeVM>(id);
        return PartialView(vm);
    }

    [ActionDescription("刪除")]
    [HttpPost]
    public IActionResult Delete(string id, IFormCollection noUse)
    {
        var vm = Wtm.CreateVM<EmployeeVM>(id);
        vm.DoDelete();
        if (!ModelState.IsValid)
            return PartialView(vm);

        return FFResult()
            .CloseDialog()
            .RefreshGrid()
            .Alert(Localizer["Sys.DeleteSuccess"]);
    }

    // === 匯入匯出 ===
    [ActionDescription("匯入")]
    [HttpPost]
    public IActionResult Import(EmployeeImportVM vm)
    {
        if (vm.ErrorListVM.EntityList.Count > 0 || !vm.BatchSaveData())
            return PartialView(vm);

        return FFResult().CloseDialog().RefreshGrid();
    }

    [ActionDescription("匯出")]
    [HttpPost]
    public IActionResult ExportExcel(EmployeeListVM vm)
    {
        return vm.GetExportData();
    }
}
```

### 5.3 BaseApiController 完整 CRUD 範例

**適用場景：** SPA 前端（React/Vue/Angular）或手機 App 對接 API。

```csharp
[ActionDescription("員工管理API")]
[ApiController]
[Route("api/[controller]")]
public class EmployeeApiController : BaseApiController
{
    [ActionDescription("搜尋")]
    [HttpPost("search")]
    public IActionResult Search([FromBody] EmployeeSearcher searcher)
    {
        var vm = Wtm.CreateVM<EmployeeListVM>(passInit: true);
        vm.Searcher = searcher;
        return Content(vm.GetJson(), "application/json");
    }

    [ActionDescription("詳情")]
    [HttpGet("{id}")]
    public IActionResult Get(Guid id)
    {
        var vm = Wtm.CreateVM<EmployeeVM>(id.ToString());
        if (vm.Entity == null)
            return NotFound(new { Code = 404, Msg = "員工不存在" });
        return Ok(new { Code = 200, Msg = "success", Data = vm.Entity });
    }

    [ActionDescription("新增")]
    [HttpPost]
    public IActionResult Create([FromBody] EmployeeVM vm)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState.GetErrorJson());

        vm.DoAdd();
        if (!ModelState.IsValid)
            return BadRequest(ModelState.GetErrorJson());

        return Ok(new { Code = 200, Msg = "新增成功", Data = new { vm.Entity.ID } });
    }

    [ActionDescription("修改")]
    [HttpPut("{id}")]
    public IActionResult Edit(Guid id, [FromBody] EmployeeVM vm)
    {
        if (vm.Entity.ID != id)
            return BadRequest(new { Code = 400, Msg = "ID 不一致" });

        if (!ModelState.IsValid)
            return BadRequest(ModelState.GetErrorJson());

        vm.DoEdit();
        if (!ModelState.IsValid)
            return BadRequest(ModelState.GetErrorJson());

        return Ok(new { Code = 200, Msg = "修改成功" });
    }

    [ActionDescription("刪除")]
    [HttpDelete("{id}")]
    public IActionResult Delete(Guid id)
    {
        var vm = Wtm.CreateVM<EmployeeVM>(id.ToString());
        vm.DoDelete();
        if (!ModelState.IsValid)
            return BadRequest(ModelState.GetErrorJson());

        return Ok(new { Code = 200, Msg = "刪除成功" });
    }

    [ActionDescription("批量刪除")]
    [HttpPost("batch-delete")]
    public IActionResult BatchDelete([FromBody] Guid[] ids)
    {
        foreach (var id in ids)
        {
            var vm = Wtm.CreateVM<EmployeeVM>(id.ToString());
            vm.DoDelete();
        }
        return Ok(new { Code = 200, Msg = $"已刪除 {ids.Length} 筆" });
    }

    [ActionDescription("匯出")]
    [HttpPost("export")]
    public IActionResult Export([FromBody] EmployeeSearcher searcher)
    {
        var vm = Wtm.CreateVM<EmployeeListVM>(passInit: true);
        vm.Searcher = searcher;
        vm.SearcherMode = ListVMSearchModeEnum.Export;
        var data = vm.GenerateExcel();
        return File(data, "application/vnd.ms-excel",
            $"Employee_{DateTime.Now:yyyyMMdd}.xlsx");
    }
}
```

**API 與 MVC Controller 的關鍵差異：**

| 差異 | MVC (BaseController) | API (BaseApiController) |
|------|---------------------|------------------------|
| 回傳格式 | View / PartialView / FFResult | JSON (Ok / BadRequest / NotFound) |
| 驗證失敗 | `return PartialView(vm)` 重新渲染表單 | `return BadRequest(ModelState.GetErrorJson())` |
| 路由 | `[ActionDescription]` + 選單 | `[Route("api/[controller]")]` RESTful |
| 認證 | Cookie + Session | JWT Bearer Token |
| 典型前端 | LayUI（server-rendered） | React / Vue / 手機 App |

### 5.4 FFResultJson 流暢 API（推薦，10.3.0+）

FFResultJson 是 WTM 的 **CSP 安全** JSON 動作分派機制，告訴 LayUI 執行動作後該做什麼。Server 回傳 `X-WTM-Action: application/json` 標頭 + JSON body，client `ff.DispatchAction` 走白名單 switch，無任何動態代碼執行。

```csharp
FFResultJson()
    .CloseDialog()                  // 關閉對話框
    .RefreshGrid()                  // 重新載入列表
    .RefreshGridRow(id)             // 等同 RefreshGrid（layui 無單行刷新）
    .Alert("成功")                   // 對話框提示
    .Message("已儲存")               // 輕量 toast 訊息
    .Redirect("/Home/Index")        // 跳轉（僅接受相對路徑，阻擋 open-redirect）
    .Reload()                        // 重新載入當前頁面
    .RefreshPage();                  // 重新渲染 LayUI 主面板
```

**常用組合場景：**
```csharp
// 場景：新增成功後關閉對話框並刷新列表
return FFResultJson().CloseDialog().RefreshGrid();

// 場景：編輯成功後刷新
return FFResultJson().CloseDialog().RefreshGridRow(vm.Entity.ID);

// 場景：刪除成功後刷新並提示
return FFResultJson().CloseDialog().RefreshGrid()
    .Alert(Localizer["Sys.DeleteSuccess"]);
```

**安全契約**：
- `Alert` / `Message` 的 `msg` / `title` 參數由 dispatcher 透過 `ff.EscapeText`（jQuery `.text()/.html()` idiom）自動轉義，**視為純文字**（避免 layui `layer.alert` 的 innerHTML XSS）
- `Redirect(url)` 僅接受 `/`, `~/`, `#`, `?` 開頭的相對路徑；絕對 URL / protocol-relative / `javascript:` 一律 `ArgumentException`
- `Type` 以 `WtmActionType` enum 型別保證（wire 格式為 `"alert"`、`"closeDialog"` 等 camelCase，client 已鎖死）

**Action 清單**：

| 方法 | 客戶端行為 |
|------|-----------|
| `CloseDialog()` | `ff.CloseDialog()` |
| `Alert(msg, title?)` | `layer.alert(escape(msg), { title })` |
| `Message(msg, title?)` | `layer.msg(escape(msg))` |
| `RefreshGrid(winId?, index?)` | `ff.RefreshGrid(winId \|\| 'LAY_app_body', index \|\| 0)` |
| `RefreshGridRow(id, winId?)` | 同 `RefreshGrid`（layui 無單行刷新） |
| `RefreshPage()` | `layui.index.render()` |
| `Reload()` | `location.reload()` |
| `Redirect(url)` | `location.href = url`（僅相對路徑） |

### 5.4.1 FFResult 遺留 API（已 `[Obsolete]`）

舊版 `FFResult()` 透過 `IsScript: true` header + 回傳 JavaScript 字串由 client `eval()` 執行。**10.3.0 起標記 `[Obsolete(DiagnosticId = "WTM789")]`**，編譯期警告 downstream 遷移至 `FFResultJson()`。

```csharp
// 舊寫法（仍可用，會產生 WTM789 編譯警告）
return FFResult().CloseDialog().RefreshGrid();

// 新寫法
return FFResultJson().CloseDialog().RefreshGrid();
```

**抑制警告（暫緩遷移期間）：** 在 `.csproj` 加 `<NoWarn>WTM789</NoWarn>`。

**Client 端 legacy 路徑**：`ff.DispatchAction` 先檢查 `X-WTM-Action` header，若非 JSON 再走 `IsScript` → `ff._legacyScriptEval` → `eval()`（集中於單一 helper，僅剩 1 個 `eval(` 在 `framework_layui.js`）。下次 major release 將移除此 fallback。

**常用組合場景：**
```csharp
// 場景：新增成功後關閉對話框並刷新列表
return FFResult().CloseDialog().RefreshGrid();

// 場景：編輯成功後只更新該行（避免翻回第一頁）
return FFResult().CloseDialog().RefreshGridRow(vm.Entity.ID);

// 場景：刪除成功後刷新並提示
return FFResult().CloseDialog().RefreshGrid()
    .Alert(Localizer["Sys.DeleteSuccess"]);

// 場景：匯入完成後刷新並跳轉
return FFResult().CloseDialog().RefreshGrid()
    .RedirectTo("/Employee/Index");
```

### 5.5 檔案上傳與下載

**上傳（由 _FrameworkController 統一處理）：**
```
POST /_Framework/Upload → 回傳 { Id, Name }
POST /_Framework/UploadImage → 回傳 { Id, Name }（支援自動縮圖 width/height）
```

**在 Controller 中使用已上傳的檔案：**
```csharp
// Model 中定義附件關聯
public class Employee : BasePoco
{
    public Guid? PhotoId { get; set; }
    [Display(Name = "照片")]
    public FileAttachment? Photo { get; set; }
}

// View 中使用 upload TagHelper
// <wt:upload field="Entity.PhotoId" upload-type="ImageFile" />

// 下載（由 _FrameworkController 統一處理）
// GET /_Framework/GetFile?id={fileId}           → 下載
// GET /_Framework/GetFile?id={fileId}&stream=true → 在瀏覽器內顯示
// GET /_Framework/GetFile?id={fileId}&width=200&height=200 → 縮圖
```

### 5.6 常用 Action Attribute

| Attribute | 用途 | 範例 |
|-----------|------|------|
| `[ActionDescription("名稱")]` | 定義選單/功能標籤（權限管理用） | `[ActionDescription("員工管理")]` |
| `[Public]` | 跳過權限檢查（任何人可用） | 登入頁、驗證碼 |
| `[AllRights]` | 任何已登入使用者可用 | 個人設定、首頁 |
| `[DebugOnly]` | 僅 Debug 模式可用 | 代碼生成器 |
| `[NoLog]` | 不寫 ActionLog 審計日誌 | 高頻查詢（避免日誌膨脹） |
| `[FixConnection("cskey")]` | 指定特定連線字串 | 跨資料庫查詢 |
| `[ValidateFormItemOnly]` | 僅驗證表單送出的欄位 | 編輯時忽略未送出的必填欄位 |

### 5.7 Framework Controller 路由表

| 路由 | 方法 | 用途 |
|------|------|------|
| `/_Framework/Selector` | POST | 彈出選擇器 |
| `/_Framework/GetPagingData` | POST | 分頁資料 |
| `/_Framework/Upload` | POST | 檔案上傳 |
| `/_Framework/GetFile?id=` | GET | 檔案下載/串流 |
| `/_Framework/GetExportExcel` | POST | 匯出 Excel |
| `/_Framework/Menu` | GET | 選單 JSON |
| `/_Framework/GetVerifyCode` | GET | 驗證碼圖片 |
| `/_Analysis/meta` | GET | Analysis 欄位中繼資料 |
| `/_Analysis/query` | POST | Analysis 聚合查詢 |
| `/_Analysis/export` | POST | Analysis 匯出 |
| `/_Dashboard/list` | GET | Dashboard 列表 |
| `/_Dashboard/{id}` | GET/PUT/DELETE | Dashboard CRUD |
| `/api/_account/refreshtoken` | POST | JWT Token 刷新 |

---

## 6. LayUI TagHelper

### 6.1 命名空間

```cshtml
@addTagHelper *, WalkingTec.Mvvm.TagHelpers.LayUI
```

#### 6.1.1 隨附的 LayUI 版本與 `Layui:Asset` 切換（10.14.0+）

WTM 的 TagHelper 渲染 LayUI 標記，但**框架本身從不挑選 layui 資產** —— 資產的 `<link>`/`<script>` 由 App 端的佈局殼（demo 的 `_Layout.cshtml` / `Login.cshtml`）提供。demo 隨附兩棵 vendored 資產樹並同時服務，由設定鍵 `Layui:Asset` 選擇：

| `Layui:Asset` 值 | 選中的樹 | LayUI 版本 |
|------------------|----------|-----------|
| `legacy`（精確比對） | `/layui` | 2.6.3 |
| 其他任意值 / 未設定（**10.14.0 起的預設**） | `/layui-next` | 2.13.8 |

> **安全不變量**：config 值**永不串接進 URL**，只在上表兩個固定字面值之間選擇。

**10.14.0 前**預設是 2.6.3（`next` 為 opt-in）；**10.14.0 起翻轉**為 2.13.8 預設，`legacy` 釘回。要留在 2.6.3，在 appsettings 設 `"Layui": { "Asset": "legacy" }` 或環境變數 `Layui__Asset=legacy` —— 這是完整回退路徑（兩樹皆隨附，無移除）。**自帶佈局殼的 App 不受此翻轉影響**；要採用 2.13.8 需比照 demo 的切換模式接線（含 layuiadmin scaffolding 的 laytpl `{{ }}` 轉義與 `router().path` 正規化兩處相容性修補，見 `CHANGELOG.md` `[10.13.17]`）。`wt:richtextbox` 在 2.13.8 樹上需比照 demo vendored 的 `layedit` 三檔 + `layui.extend` 接線（layui 於 2.8 移除 layedit 模組）。

**框架內嵌工具頁也跟隨此開關（10.14.1+，#614）**：`_CodeGen`、`_DashboardPage` 與 WorkFlow designer 頁經 `LayuiAssets.ResolveLayuiBase(IConfiguration)` 解析資產基底（同一安全不變量：兩個固定字面值，永不串接）。

> **Deprecation（10.14.2，#567 Phase-4a）**：`Layui:Asset=legacy` 與隨附的 2.6.3（`/layui`）樹已標記 **deprecated** —— 功能完全不變、兩樹續存，這是移除窗口的預告。實際移除排在**下一個 major**，並 gated on 下游 production 遷移；在那之前 `legacy` 保證可用。

**對話框初始化採 eval-free JSON island 架構**（#470，10.13.x）：**常用表單初始化路徑**（form init/submit/validate/error-highlight、laydate、rate、taginput，及 callback-free 的 slider/colorpicker）透過 `ff.DispatchAction` 島派發、零 inline `<script>`，並經 `ff.SafeHtml`（DOMPurify）淨化；SPA 片段可用 `ff.ConsumeIslandsIn(rootEl)` 消費子樹內的島（#587）。惟**多個 widget 組態仍發出可執行 inline `<script>`**（combobox/tree `xmSelect.render`、transfer、ueditor、upload、checkbox/radio 預設值等——#470 hard-blocker 清單，非窮舉），在 AJAX 對話框內靠 legacy 再注入路徑運作；欲量測/停用該 legacy 面，見 §10.7.1 的 kill-switch（10.14.3+）與 `docs/csp-hardening.md`。

### 6.2 佈局

```cshtml
@* 響應式行列 *@
<wt:row items-per-row="Three" items-per-row-sm="One" space="8">
    <wt:textbox field="Entity.Name" />
    <wt:combobox field="Entity.DepartmentId" items="@Model.AllDepartments" />
    <wt:datetime field="Entity.HireDate" type="Date" />
</wt:row>
```

### 6.3 表單完整範例

```cshtml
@model EmployeeVM

<wt:form vm="@Model">
    <wt:row items-per-row="Two">
        <wt:textbox field="Entity.Name" />
        <wt:textbox field="Entity.Salary" />
    </wt:row>
    <wt:row items-per-row="Two">
        <wt:combobox field="Entity.DepartmentId"
                     items="@Model.AllDepartments"
                     empty-text="請選擇部門" />
        <wt:datetime field="Entity.HireDate"
                     type="Date"
                     max="@DateTime.Today.ToString("yyyy-MM-dd")" />
    </wt:row>
    <wt:row items-per-row="One">
        <wt:checkbox field="SelectedSkillIds"
                     items="@Model.AllSkills" />
    </wt:row>
    <wt:row align="Right">
        <wt:submitbutton text="儲存" theme="Primary" />
        <wt:closebutton />
    </wt:row>
</wt:form>
```

### 6.4 列表 + 搜尋面板

```cshtml
@model EmployeeListVM

<wt:searchpanel vm="@Model">
    <wt:row items-per-row="Three">
        <wt:textbox field="Searcher.Name" />
        <wt:combobox field="Searcher.DepartmentId"
                     items="@ViewBag.AllDepartments" />
        <wt:datetime field="Searcher.HireDateBegin"
                     type="Date" />
    </wt:row>
</wt:searchpanel>

<wt:grid vm="@Model"
         url="/Employee/Search"
         height="-200"
         enable-analysis="true" />
```

### 6.5 主要 TagHelper 速查表

**佈局（6 個）：** `container`, `row`, `card`, `panel`, `tab`, `treecontent`

**表單（2 個）：** `form`, `searchpanel`

**輸入欄位（17 個）：**

| TagHelper | 用途 | 關鍵屬性 |
|-----------|------|---------|
| `wt:textbox` | 文字輸入 | `field`, `empty-text`, `is-password`, `search-url` |
| `wt:textarea` | 多行文字 | `field`, `empty-text` |
| `wt:combobox` | 下拉選單 | `field`, `items`, `multi-select`, `enable-search` |
| `wt:tree` | 樹狀選擇 | `field`, `items`, `show-line` |
| `wt:datetime` | 日期時間 | `field`, `type` (date/time/datetime), `min`, `max` |
| `wt:checkbox` | 多選框 | `field`, `items` |
| `wt:radio` | 單選框 | `field`, `items`, `yes-text`, `no-text` |
| `wt:switch` | 開關 | `field`, `lay-text` (ON\|OFF) |
| `wt:selector` | 彈窗選擇 | `field`, `list-vm`, `text-bind`, `multi-select` |
| `wt:upload` | 檔案上傳 | `field`, `upload-type`, `file-size` |
| `wt:hidden` | 隱藏欄位 | `field` |
| `wt:display` | 唯讀顯示 | `field` |
| `wt:slider` | 滑桿 | `field`, `min`, `max`, `step` |
| `wt:transfer` | 穿梭框 | `field`, `items` |
| `wt:colorpicker` | 色彩選擇 | `field` |
| `wt:richtextbox` | 富文字 | `field` |

**按鈕（6 個）：** `button`, `submitbutton`, `resetbutton`, `closebutton`, `linkbutton`, `downloadtemplatebutton`

**資料顯示（2 個）：** `grid`, `chart`

### 6.6 聯動下拉（Cascade）

**場景：省市區三級聯動**

```cshtml
@* 選擇省份後自動載入城市，選擇城市後載入區域 *@
<wt:row items-per-row="Three">
    <wt:combobox field="Entity.ProvinceId"
                 items="@Model.AllProvinces"
                 empty-text="請選擇省份"
                 link-field="Entity.CityId"
                 trigger-url="/Address/GetCities" />

    <wt:combobox field="Entity.CityId"
                 empty-text="請先選省份"
                 link-field="Entity.DistrictId"
                 trigger-url="/Address/GetDistricts" />

    <wt:combobox field="Entity.DistrictId"
                 empty-text="請先選城市" />
</wt:row>
```

Controller 端：
```csharp
[HttpGet]
public IActionResult GetCities(Guid id)
{
    var cities = DC.Set<City>()
        .Where(x => x.ProvinceId == id)
        .GetSelectListItems(Wtm, x => x.Name);
    return JsonMore(cities);
}

[HttpGet]
public IActionResult GetDistricts(Guid id)
{
    var districts = DC.Set<District>()
        .Where(x => x.CityId == id)
        .GetSelectListItems(Wtm, x => x.Name);
    return JsonMore(districts);
}
```

### 6.7 彈窗選擇器（Selector）

**場景：從員工列表中選擇審批人**

```cshtml
@* 單選 — 選擇一位審批人 *@
<wt:selector field="Entity.ApproverId"
             list-vm="@typeof(EmployeeListVM)"
             text-bind="Name"
             window-title="選擇審批人"
             window-width="800"
             window-height="500" />

@* 多選 — 選擇多位參與者（field 為 List 類型時自動多選） *@
<wt:selector field="SelectedParticipantIds"
             list-vm="@typeof(EmployeeListVM)"
             text-bind="Name"
             multi-select="true"
             window-title="選擇參與者" />
```

**運作機制：**
1. 點擊輸入框右側按鈕 → 開啟 `/_Framework/Selector` 彈窗
2. 彈窗內載入指定 ListVM 的搜尋 + 列表頁面
3. 使用者勾選後點確認 → 選中的 ID 寫回 `field`，顯示名稱寫回 `text-bind`
4. 多選時以逗號分隔

### 6.8 檔案上傳（Upload）

**場景 1：上傳員工大頭照（圖片，限 2MB）**
```cshtml
<wt:upload field="Entity.PhotoId"
           upload-type="ImageFile"
           file-size="2048"
           show-preview="true"
           thumb-width="120"
           thumb-height="120" />
```

**場景 2：上傳合約文件（PDF/Word，限 10MB）**
```cshtml
<wt:upload field="Entity.ContractFileId"
           upload-type="AllFiles"
           file-size="10240" />
```

**場景 3：上傳 Excel 匯入檔**
```cshtml
<wt:upload field="UploadFileId"
           upload-type="ExcelFile"
           file-size="5120" />
```

**upload-type 選項：**

| 類型 | 說明 | 允許的副檔名 |
|------|------|------------|
| `AllFiles` | 所有檔案 | 不限 |
| `ImageFile` | 圖片 | jpg, jpeg, png, bmp, gif |
| `ZipFile` | 壓縮檔 | zip, rar, 7z |
| `ExcelFile` | Excel | xlsx, xls |
| `WordFile` | Word | docx, doc |
| `PDFFile` | PDF | pdf |
| `TextFile` | 文字 | txt, csv |

### 6.9 對話框（Dialog）

**場景：在列表頁彈出編輯對話框**

Dialog 不是 TagHelper，而是透過 Grid Action 自動觸發。當 GridAction 的 `ShowInRow` 為 `true` 時，點擊列表行按鈕會開啟對話框：

```csharp
// ListVM 中定義 Action
protected override List<GridAction> InitGridAction()
{
    return new List<GridAction>
    {
        // 標準 CRUD 按鈕 — 自動開啟對話框
        this.MakeStandardAction("Employee", GridActionStandardTypesEnum.Create, "新增", ""),
        this.MakeStandardAction("Employee", GridActionStandardTypesEnum.Edit, "編輯", "",
            GridActionParameterTypesEnum.SingleId),
        this.MakeStandardAction("Employee", GridActionStandardTypesEnum.Details, "詳情", "",
            GridActionParameterTypesEnum.SingleId),

        // 自訂按鈕 — 指定 URL 和對話框大小
        this.MakeAction("Employee", "Approve", "審批", "審批",
            GridActionParameterTypesEnum.SingleId)
            .SetDialogTitle("員工審批")
            .SetWidth(600).SetHeight(400),

        // 無對話框 — 直接發送 AJAX（例如「啟用/停用」切換）
        this.MakeAction("Employee", "ToggleStatus", "切換狀態", "",
            GridActionParameterTypesEnum.SingleId)
            .SetIsRedirect(false)
            .SetOnClickScript("toggleEmployeeStatus"),
    };
}
```

**手動開啟對話框（JavaScript）：**
```javascript
// 在 LayUI 中手動開啟 iframe 對話框
layui.layer.open({
    type: 2,
    title: '自訂對話框',
    area: ['800px', '600px'],
    content: '/Employee/Edit?id=' + employeeId
});
```

### 6.10 Grid 進階功能

**固定列 + 行內按鈕 + 自訂排序：**
```csharp
protected override IEnumerable<IGridColumn<Employee_View>> InitGridHeader()
{
    return new List<GridColumn<Employee_View>>
    {
        // 固定左側
        this.MakeGridHeader(x => x.Name).SetWidth(150).SetFixed(GridColumnFixedEnum.Left),

        // 自訂格式化
        this.MakeGridHeader(x => x.Salary).SetWidth(120)
            .SetFormat((entity, val) => $"<span style='color:green'>NT${val:N0}</span>"),

        // 可排序
        this.MakeGridHeader(x => x.HireDate).SetWidth(120).SetSort(true)
            .SetFormat((e, v) => ((DateTime)v).ToString("yyyy-MM-dd")),

        // 狀態欄（用顏色標籤）
        this.MakeGridHeader(x => x.Status).SetWidth(80)
            .SetFormat((e, v) => v?.ToString() == "Active"
                ? "<span class='layui-badge layui-bg-green'>在職</span>"
                : "<span class='layui-badge'>離職</span>"),

        // 操作按鈕列（固定右側）
        this.MakeGridHeaderAction(width: 200).SetFixed(GridColumnFixedEnum.Right)
    };
}
```

**Grid 屬性速查：**

| 屬性 | 用途 | 範例值 |
|------|------|--------|
| `url` | 資料來源 URL | `/Employee/Search` |
| `height` | 表格高度（負值=距底部距離） | `-200` |
| `enable-analysis` | 啟用 Analysis Mode 按鈕 | `true` |
| `multi-select` | 多選（出現 checkbox） | `true`（預設） |
| `auto-search` | 頁面載入時自動搜尋 | `true`（預設） |

### 6.11 Island Render（opt-in，10.16.0+/10.16.1，#470 Slices G–M）

`WtmUIOptions.UseSelectIslandRender`（**預設 `false`**）啟用後，LayUI 互動元件（ComboBox/Tree 的 `xmSelect`、Transfer、Upload/MultiUpload、laydate、slider/colorpicker、ueditor/layedit、textarea counter、grid-cell 按鈕與 `SubmitButton`）改以**宣告式 JSON island + `data-wtm-*` delegated handler**（由 `ff.DispatchAction` 消費）渲染——**無 inline `<script>`、無 `eval`、無 per-widget 全域函式**，是把表單/對話框 CSP 推向 `script-src 'self'` 的路徑（搭配 §10.7 CSP 與 #627 kill-switch）。

```csharp
services.Configure<WtmUIOptions>(o => o.UseSelectIslandRender = true);   // 或 appsettings "UIOptions" 節
```

**要點**：
- **預設關閉＝與舊版逐位元組相同輸出**（zero behaviour change）。注意版本邊界：10.15.0/10.16.0 有部分 emitter 未上 gate（#753 迴歸），**10.16.1 才恢復完整保證**——回滾/bisect 不要落在中間兩版。
- 開啟後渲染時機由 parse-time 移到 `DOMContentLoaded`（真實但窄的行為差異，這正是它必須 opt-in 的原因）；開發者自寫 callback（`ChangeFunc` 等）若是純識別字會走 `ff._resolveGuardedWindowFn` 守門，非識別字則保留舊 inline 路徑並 `console.warn`。
- 建議在 staging 全頁面回歸後再開；每 app 一次性決策。

---

## 7. Analysis Mode 分析模式

Analysis Mode 讓任何列表頁面變成即時分析工具：使用者可以自由選擇維度（GROUP BY）和度量（SUM/AVG/COUNT 等），產生圖表和樞紐表，並匯出 Excel/CSV。開發者只需加 3 個 Attribute，零 JavaScript。

### 7.1 啟用方式（三步驟）

**步驟 1：Model 上標記維度和度量**
```csharp
public class SaleRecord : BasePoco
{
    [Dimension(DisplayName = "地區")]
    public string Region { get; set; } = "";

    [Dimension(DisplayName = "業務員")]
    public string SalesRep { get; set; } = "";

    [Dimension(DisplayName = "日期", Hierarchy = DateHierarchy.Month)]
    public DateTime SaleDate { get; set; }

    [Measure]
    public decimal Amount { get; set; }

    [Measure]
    public int Quantity { get; set; }

    [Measure]
    public decimal Discount { get; set; }
}
```

**步驟 2：ListVM 上標記 [EnableAnalysis]**
```csharp
[EnableAnalysis]
public class SaleRecordListVM : BasePagedListVM<SaleRecord, SaleRecordSearcher>
{
    protected override IOrderedQueryable<SaleRecord> GetSearchQuery()
    {
        return DC!.Set<SaleRecord>()
            .CheckContain(Searcher.Region, x => x.Region)
            .CheckBetween(Searcher.DateBegin, Searcher.DateEnd, x => x.SaleDate)
            .DPWhere(Wtm!, x => x.Region)  // 資料權限也在 Analysis 中生效
            .OrderByDescending(x => x.SaleDate);
    }
}
```

**步驟 3：View 的 Grid 啟用**
```cshtml
<wt:grid vm="@Model" enable-analysis="true" />
```

使用者在列表頁面會看到「分析模式」按鈕，點擊後進入互動式分析介面。

### 7.2 核心元件

| 元件 | 用途 |
|------|------|
| `AnalysisVmRegistry` | 啟動時掃描 `[EnableAnalysis]` VM，建立白名單 |
| `AnalysisFieldScanner` | 反射 Model 產生 `AnalysisFieldMeta` 列表 |
| `AnalysisQueryEngine` | 驗證欄位 → Expression Tree 過濾 → `Take(50_000)` → 記憶體內 GroupBy → 截斷至 10,000 行 |
| `AnalysisPivotEngine` | 樞紐分析（交叉表） |
| `AnalysisExcelExporter` | 匯出 XLSX |
| `_AnalysisController` | REST API（meta/query/pivot/export） |
| `framework_analysis.js` | 前端 UI（維度/度量選擇、圖表自動偵測、匯出按鈕） |

### 7.3 Dimension Attribute 詳解

```csharp
[Dimension(
    DisplayName = "訂單日期",        // UI 顯示名稱（預設用 [Display] 的 Name）
    Hierarchy = DateHierarchy.Month  // 日期層級（僅 DateTime 欄位有效）
)]
```

**DateHierarchy 選項：**

| 層級 | 說明 | 分組範例 |
|------|------|---------|
| `None` | 非日期欄位（預設） | 原值 |
| `Year` | 年 | 2026 |
| `Quarter` | 季 | 2026-Q1 |
| `Month` | 月 | 2026-03 |
| `Week` | 週 | 2026-W10 |
| `Day` | 日 | 2026-03-12 |
| `Hour` | 時 | 2026-03-12 14:00 |

使用者可在前端切換層級（例如從「月」切換到「季」），不需重新開發。

### 7.4 AggregateFunc 選項

| 函數 | 說明 | 適用類型 |
|------|------|---------|
| `Sum` | 加總 | 數值 |
| `Avg` | 平均 | 數值 |
| `Min` | 最小值 | 數值、日期 |
| `Max` | 最大值 | 數值、日期 |
| `Count` | 計數 | 任何 |
| `DistinctCount` | 不重複計數 | 任何 |
| `Variance` | 變異數 | 數值 |
| `StdDev` | 標準差 | 數值 |

### 7.5 API 端點

| 端點 | 方法 | 用途 |
|------|------|------|
| `/_analysis/meta?listVmType=` | GET | 取得可用維度/度量欄位清單 |
| `/_analysis/query` | POST | 執行聚合查詢 |
| `/_analysis/pivot` | POST | 樞紐分析（交叉表） |
| `/_analysis/export?format=xlsx\|csv` | POST | 匯出查詢結果 |
| `/_analysis/pivot/export?format=xlsx\|csv` | POST | 匯出樞紐結果 |

### 7.6 查詢請求與回應

**請求：**
```json
{
  "listVmType": "MyApp.ViewModels.SaleRecordListVM, MyApp",
  "dimensions": ["Region", "SaleDate"],
  "measures": [
    { "field": "Amount", "func": "Sum" },
    { "field": "Quantity", "func": "Count" }
  ],
  "dimensionHierarchies": { "SaleDate": "Month" },
  "filters": [
    { "field": "SaleDate", "operator": ">=", "value": "2026-01-01" }
  ],
  "searcherFormData": { "Region": "北區" }
}
```

**回應：**
```json
{
  "columns": ["Region", "SaleDate", "Amount_Sum", "Quantity_Count"],
  "rows": [
    { "Region": "北區", "SaleDate": "2026-01", "Amount_Sum": 150000, "Quantity_Count": 42 },
    { "Region": "北區", "SaleDate": "2026-02", "Amount_Sum": 180000, "Quantity_Count": 55 }
  ],
  "totalCount": 2,
  "truncated": false
}
```

### 7.7 使用場景範例

#### 場景 1：HR 部門分析員工薪資分佈

**Model：**
```csharp
public class Employee : BasePoco
{
    [Dimension(DisplayName = "部門")]
    public string Department { get; set; } = "";

    [Dimension(DisplayName = "職級")]
    public string Level { get; set; } = "";

    [Dimension(DisplayName = "入職日期", Hierarchy = DateHierarchy.Year)]
    public DateTime HireDate { get; set; }

    [Measure]
    public decimal Salary { get; set; }
}
```

**可能的分析操作（使用者在前端自由組合）：**
- 維度: 部門 → 度量: Salary(Avg) → 看各部門平均薪資
- 維度: 部門 + 職級 → 度量: Salary(Min, Max) → 看各部門各職級薪資範圍
- 維度: 入職日期(Year) → 度量: Salary(Avg) → 看薪資隨年資的趨勢

#### 場景 2：電商訂單多維分析

```csharp
public class OrderRecord : BasePoco
{
    [Dimension(DisplayName = "商品類別")]
    public string Category { get; set; } = "";

    [Dimension(DisplayName = "付款方式")]
    public string PaymentMethod { get; set; } = "";

    [Dimension(DisplayName = "訂單日期", Hierarchy = DateHierarchy.Month)]
    public DateTime OrderDate { get; set; }

    [Measure]
    public decimal OrderAmount { get; set; }

    [Measure]
    public decimal ShippingCost { get; set; }

    [Measure]
    public int ItemCount { get; set; }
}
```

**樞紐分析範例：** 行 = 商品類別，列 = 付款方式，值 = OrderAmount(Sum)
→ 產生「類別 × 付款方式」的交叉表。

### 7.8 自訂欄位存取控制

```csharp
// 實作 IAnalysisFieldPolicy 限制不同角色看到的欄位
public class MyFieldPolicy : IAnalysisFieldPolicy
{
    public IEnumerable<AnalysisFieldMeta> Filter(
        IEnumerable<AnalysisFieldMeta> fields,
        LoginUserInfo? user)
    {
        // 非管理員不能看到薪資欄位
        if (user?.Roles?.Any(r => r.RoleCode == "admin") != true)
            return fields.Where(f => f.FieldName != "Salary");
        return fields;
    }
}

// 註冊
services.AddSingleton<IAnalysisFieldPolicy, MyFieldPolicy>();
```

### 7.9 安全限制

| 限制 | 值 | 說明 |
|------|-----|------|
| 維度上限 | 3 個 | 防止過度分組 |
| 度量上限 | 3 個 | 防止過度聚合 |
| 資料取樣 | 50,000 筆 | `Take(50_000)` 後在記憶體內 GroupBy |
| 結果行數 | 10,000 筆 | 超過截斷，回應標記 `truncated: true` |
| CSV 防護 | 前置 tab | `=+@-\t\r` 開頭的儲存格加 tab，防止公式注入 |
| 欄位白名單 | 啟動時掃描 | 未標記 `[Dimension]`/`[Measure]` 的欄位無法查詢 |
| 資料權限 | DPWhere 生效 | `GetSearchQuery()` 中的資料權限過濾在 Analysis 中同樣生效 |

### 7.10 前端自動圖表選擇

`framework_analysis.js` 根據維度和度量組合自動選擇最佳圖表類型：

| 維度數 | 度量數 | 自動選擇 |
|--------|--------|---------|
| 0 | 1+ | KPI 卡片 |
| 1 (非日期) | 1 | 長條圖 |
| 1 (日期) | 1 | 折線圖 |
| 1 | 2+ | 堆疊長條圖 |
| 2+ | 1+ | 分組長條圖 |

使用者可手動切換圖表類型，不受自動偵測限制。

### 7.11 結果排序與 TopN（10.5.0+）

兩個欄位讓 dashboard 直接表達「銷售額 DESC 取前 5 名」的查詢，不必 fetch 10,000 列再前端排序：

```csharp
var req = new AnalysisQueryRequest
{
    VmType   = typeof(SalesAnalysisVM).AssemblyQualifiedName,
    Dimensions = new() { new() { Field = "Region" } },
    Measures   = new() { new() { Field = "Amount", Func = AggregateFunc.Sum } },
    Sort = new()
    {
        new() { Field = "Amount_Sum", Descending = true },     // 多鍵排序：再加一筆即可
    },
    TopN = 5,
};
```

- `Sort.Field` 必須是已請求的維度或度量結果欄（命名格式 `{Field}_{Func}`，例 `Amount_Sum`），白名單外直接 reject
- `TopN` 範圍：`1..AnalysisLimits.MaxResultRows`（預設 10,000）
- `TopN` 只裁 `Rows`，**`TotalCount` 仍報全量 group 數** — 前端可顯示「showing 5 of 47」
- `SortValueComparer` 處理混型：數值統一到 `decimal`、日期直接比、混型 fallback ordinal — 保證可決定論不丟例外
- 預設不排序（保持向後相容）

### 7.12 9 個新增相對日期 token（10.5.0+）

filter UI 預設集合在 10.5.0 從 8 個擴到 17 個：

| 新 token | 涵義 |
|--------|------|
| `@yesterday` | 今日 − 1（單日） |
| `@nextWeek` / `@nextMonth` | 預測窗口（運維 dashboard / 排程 job） |
| `@last7days` / `@last90days` / `@last365days` | 滾動窗口 |
| `@lastQuarter` | 完整上一個自然季 |
| `@thisYear` | 1/1 至 12/31（與 `@ytd` 不同：`@ytd` 截止今天） |
| `@lastYear` | 完整上一個自然年 |

token 大小寫不敏感、展開為 `(Gte, Lte)` filter pair，server side 型別轉換與 expression tree 組裝完全沿用既有路徑。**沒動到** 8 個舊 token 行為。

```csharp
new FilterSpec { Field = "OrderDate", Operator = FilterOperator.RelativeDate, Value = "@last90days" }
```

### 7.13 DistinctCount 聚合函數（10.5.0+）

回答「每區獨立客戶數」/「每分類獨立商品數」這類經典 BI 問題：

```csharp
[Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.DistinctCount)]
public Guid CustomerId { get; set; }

// 請求：
new MeasureSpec { Field = "CustomerId", Func = AggregateFunc.DistinctCount }
```

| 路徑 | 行為 |
|------|------|
| `InProcessGroupByStrategy` | `g.Select(propGet).Where(non-null).Distinct().Count()` — 處理非數值（FK id 字串）也安全 |
| `ServerSideGroupByStrategy` | `g.Select(e => e.Prop).Distinct().Count()` → EF Core 翻譯為 SQL `COUNT(DISTINCT col)` 整批下推 |

NULL 排除（符合 ANSI-SQL `COUNT(DISTINCT)` 語意），結果欄命名 `CustomerId_DistinctCount` 可直接放進 `Sort.Field`。

### 7.14 HavingFilters 後聚合過濾（10.5.0+）

10.4 之前 Analysis 只有 `Filters`（pre-aggregation, SQL `WHERE`）+ Sort/TopN（post-aggregation），**沒有** 後聚合篩選。10.5 補上：

```csharp
var req = new AnalysisQueryRequest
{
    Measures = new() { new() { Field = "Amount", Func = AggregateFunc.Sum } },
    HavingFilters = new()
    {
        new() { Field = "Amount_Sum", Operator = FilterOperator.Gte, Value = "1000000" },  // 月銷售 >= 1M 的區
    },
};
```

- `HavingFilter.Operator` 限數值子集：`Eq`/`NotEq`/`Gt`/`Gte`/`Lt`/`Lte`（`Contains`/`In` 對標量聚合無意義）
- `Value` 用 `InvariantCulture` 解析為 `decimal`；**無法解析則保守過濾掉所有 group**（不靜默放行）
- 多筆 AND
- 執行管線：`GROUP BY → HAVING → ORDER BY → LIMIT`（match SQL 標準）
- `TotalCount` 反映 **post-having** 的 group 基數，所以「showing 3 of 5」是針對使用者過濾後的世界

### 7.15 GrandTotal 總計列（10.5.0+）

dashboard 想要「合計」footer 不必前端再算一遍：

```csharp
var req = new AnalysisQueryRequest
{
    Measures = new() {
        new() { Field = "Amount", Func = AggregateFunc.Sum },
        new() { Field = "OrderId", Func = AggregateFunc.Count },
    },
    IncludeGrandTotal = true,
};
var resp = engine.Execute(req);
// resp.GrandTotalRow != null
```

**聚合規則：**

| 度量 Func | GrandTotalRow 行為 |
|------|------|
| `Sum` / `Count` | 群組值的總和 |
| `Max` / `Min` | 群組的 Max / Min |
| `Avg` / `DistinctCount` | **`null`** — 加權平均需要 per-group sample count（GroupBy 結果丟掉了），distinct 不能直接相加 — 靜默錯誤比 null 更糟 |

- 維度欄統一輸出 `null`（client 自由標 "總計"）
- 範圍：**post-HAVING / pre-TopN**（與 `TotalCount` 同一宇宙，`TopN` 不影響合計）
- 空結果仍輸出 `GrandTotalRow`（度量欄 `null` 而非 `0`，避免「平均為零」誤導）
- Excel exporter 自動加一列底色淡黃 + 粗體底列；CSV exporter 加 footer line（`null` 維度欄輸出 "總計" 標籤、後續 `null` 維度欄留白；數值維持 `.ToString()` + RFC 4180 escape；首字元為 `=` `+` `-` `@` 仍走 CSV-formula-injection escape — 縱深防禦）

### 7.16 AnalysisLimits 可調列數上限（10.5.0+）

把硬編碼的 `10,000`（group result）/ `50,000`（raw materialize）拉成 static class，不必 fork 框架：

```csharp
// Program.cs（一次設定）
WalkingTec.Mvvm.Core.Analysis.AnalysisLimits.MaxResultRows     = 50_000;
WalkingTec.Mvvm.Core.Analysis.AnalysisLimits.MaxMaterializeRows = 200_000;
WalkingTec.Mvvm.Core.Analysis.AnalysisLimits.MaxFilterClauses   = 100;
WalkingTec.Mvvm.Core.Analysis.AnalysisLimits.MaxGroupByFields   = 64;
```

- `MaxResultRows` 同時作為 `TopN` 驗證上限 — 兩者永遠對齊同一維度
- `MaxFilterClauses`（預設 50，#795）：`Filters` / `HavingFilters` / `Sort` /
  `CompareWith.Filters` 各自的條款數上限，enforced 在
  `AnalysisQueryEngine.ValidateFields` 本身 —— 每個呼叫端（含 Dashboard
  analysis widget）都自動繼承，不必個別 controller 各自檢查一次
- `MaxGroupByFields`（預設 32，#795）：`Dimensions` / `Measures` 各自的欄位
  數上限，同樣 enforced 在引擎層；`_AnalysisController` 自己另有更嚴格的
  「最多 3 個維度 / 度量」UX 前置檢查，兩者並存、互不取代
- 預設值（10,000 / 50,000 / 50 / 32）= 沒動程式碼就完全相同的既有行為
- 測試裡用 `try / finally` 在單測逐筆 override

### 7.17 CompareWith 期間對比（10.5.0+）

「本月 vs 上月」/「本年 vs 去年」/「A 通路 vs B 通路」一發請求搞定：

```csharp
var req = new AnalysisQueryRequest
{
    Filters = new()
    {
        new() { Field = "OrderDate", Operator = FilterOperator.RelativeDate, Value = "@thisMonth" },
    },
    Measures = new() { new() { Field = "Amount", Func = AggregateFunc.Sum } },
    CompareWith = new ComparisonRequest
    {
        Label = "上月",
        Filters = new()
        {
            new() { Field = "OrderDate", Operator = FilterOperator.RelativeDate, Value = "@lastMonth" },
        },
    },
    Sort = new() { new() { Field = "Amount_Sum_ChangePct", Descending = true } },  // 漲幅榜
    TopN = 5,
};
```

每個度量自動派生 3 欄：

| 派生欄 | 涵義 |
|------|------|
| `{Field}_{Func}_Compare` | 對比期值 |
| `{Field}_{Func}_Delta` | 當期 − 對比期 |
| `{Field}_{Func}_ChangePct` | 變化百分比（`0.25` = +25%）；對比期為 0 時為 `null`（避免 `Infinity` 序列化） |

- 對比期才有的 row 會以主度量 `null` 加入 — 失去全部營收的區也看得見
- `Sort.Field` 自動白名單派生欄，「漲幅 / 跌幅榜」一行 query 完成
- 自動加上 `ColumnDisplayNames`：`金額 合計 (上期)` / `金額 合計 差值` / `金額 合計 變化%`
- 內部 sub-request **drop** 掉 `Sort` / `TopN` / `IncludeGrandTotal` / `HavingFilters` / `CompareWith` — 不會無窮遞迴、不會 shape 錯位

### 7.18 自動洞察（Insights）BI 敘事（10.5.0+）

把 `Rows` 從「光秃秃的數字」升級成「數字 + 可貼進 callout 的 2~5 行中文短句」：

```csharp
req.IncludeInsights = true;
var resp = engine.Execute(req);
// resp.Insights = [
//   "本期最高: 北 (金額 合計 = 1,000)，為平均的 2.31 倍",
//   "本期最低: 西 (金額 合計 = 200)",
//   "與對比期相比，北 漲幅最大 (+25.0%)，南 跌幅最大 (-20.0%)",
//   "Top 1 群組佔總計的 86.6%（Pareto 集中度高）",
//   "1 個群組為異常離群值（|z| > 2.0）：Outlier (金額 合計, z = 2.66)"
// ]
```

5 條啟發式（heuristic）依序套在第一個 measure：top performer + ratio、bottom performer（單一群組會 skip 避免重複）、period-over-period 漲跌冠軍（需 `CompareWith`）、Pareto 集中度（N≥5 且 top 20% ≥ 80% 總計）、z-score 離群值（N≥4 且 |z|>2）。

- 每條獨立 try/catch — 一條炸不影響其他
- 空結果 / 單列 / 全 null measure → 空 list（UI 隱藏 callout）
- 計算發生在 **Sort+TopN 之後**，敘事反映使用者實際看到的內容
- **輸出字串跨版本不穩定**，prog 取值請用底層數值欄

### 7.19 Drill-through 點選下鑽（10.5.0+）

dashboard 的「點 group → 看背後原始列」終於有官方 helper：

```csharp
var raw = AnalysisDrillThrough.BuildQuery<Order>(
    baseQuery: dc.Set<Order>(),
    originalReq: req,                       // dashboard 當前請求（保留 Filters）
    groupValues: new Dictionary<string, string?>
    {
        ["Region"]      = "北",              // dashboard 點到的 cell
        ["OrderDate"]   = "2026 Q1",         // 日期階層自動反查為 [start, endExclusive)
    },
    whitelist: AnalysisDrillThrough.BuildWhitelistFor<Order>());
// raw 是過濾後的 IQueryable<Order> — 可直接 Take(50).ToList() 成 detail grid
```

- 重用 engine 的 `ApplyFilters`（已 `public`）— 白名單 / 型別轉換 / 相對日期語意一致
- 保留 `originalReq.Filters`（drill 仍在 dashboard 既有 scope 內）
- 日期階層維度（`Year` / `Quarter` / `Month` / `Day`）的人類標籤（"2026 Q1"）由 `DateTruncator.TryParseLabel` 反查為半開區間 `[start, endExclusive)`；Eq 比對毫秒精度永遠 0 列
- 標籤無法 parse → `Take(0)` 優雅退化（dashboard 顯示「無資料」），不丟例外、也不傳回未過濾結果
- 缺維度值 → 該維度不過濾（drill 自動放寬）；多維度 AND

### 7.17 OLAP 匯出 / Pivot opt-in overload（10.6.0+）

兩個 additive overload，既有簽章不變、預設行為完全保留：

- **`AnalysisExcelExporter.ExportToStream(response, destination, ...)`**：把 workbook 直接寫到目標 `Stream`，省去 `byte[] Export(...)` 路徑那次 `ms.ToArray()` 全量複製（大型匯出減半尖峰記憶體）。既有 `byte[] Export(...)` 簽章與回傳型別不變。

  ```csharp
  // 串流到 HTTP Response（不在記憶體中保留整份 byte[]）
  exporter.ExportToStream(response, httpContext.Response.Body, includeMetadata: true);
  ```

- **`AnalysisPivotEngine.Pivot(..., bool fillZero)`**：新增 5 參數 overload；`fillZero: false` 時略過不存在的 pivot 值 / 度量組合的零值儲存格（稀疏輸出）。既有 4 參數 `Pivot(...)` 委派為 `fillZero: true`，所有現有呼叫端的零值填滿行為不變。

---

## 8. ETL 模組

### 8.1 架構

```
管理 UI (_EtlJobController)
  → EtlSchedulerService (啟用/暫停/觸發)
  → Quartz Scheduler
  → EtlQuartzJob [DisallowConcurrentExecution]
  → EtlPipelineExecutor
     1. EnsureStagingTable → 自動建 staging 表
     2. TruncateStaging
     3. Extract (IEtlSource) → Transform (optional) → BulkLoad (IBulkLoader)
     4. Merge staging → target (MERGE INTO)
     5. Commit/Discard Watermark
  → EtlRunLog (寫入執行紀錄)
  → EtlProgressTracker (即時進度)
```

### 8.2 啟用方式

```csharp
// Program.cs / Startup.cs
services.AddWtmEtl();

// DataContext.OnModelCreating
modelBuilder.ApplyEtlModels();
```

### 8.3 Job 定義範例

```csharp
var job = new EtlJobDefinition
{
    Name = "同步訂單",
    Description = "每小時從 MSSQL 來源同步訂單到本地",
    CronExpression = "0 0 * * * ?",       // 每小時
    SourceDbType = DBTypeEnum.SqlServer,
    SourceConnectionKey = "SourceMssql",
    TargetConnectionKey = "default",
    QueryTemplate = "SELECT * FROM Orders WHERE UpdatedAt > @watermark",
    TargetTableName = "Orders",
    MergeKeyColumn = "OrderNo",
    WatermarkType = EtlWatermarkType.Timestamp,
    WatermarkColumn = "UpdatedAt",
    BatchSize = 50000,
    TimeoutMinutes = 30,
    Status = EtlJobStatus.Enabled
};
```

### 8.4 支援的資料庫

| 來源/目標 | Extract | BulkLoad | Merge |
|-----------|---------|----------|-------|
| **SQL Server** | SqlCommand + SequentialAccess | SqlBulkCopy | MERGE...WHEN MATCHED/NOT MATCHED |
| **Oracle** | OracleCommand + SequentialAccess | Array Binding | MERGE INTO...USING |

### 8.5 水印策略（Watermark）

水印是增量同步的核心 — 記錄「上次同步到哪裡」，下次只抓新的資料。

| 類型 | 說明 | WHERE 子句 | 適用場景 |
|------|------|-----------|---------|
| `FullLoad` | 全量載入，每次抓全部 | `1=1` | 小資料表、維度表 |
| `Timestamp` | 時間戳增量 | `column > @watermark` | 有 UpdatedAt 欄位的交易表 |
| `Identity` | 自增 ID 增量 | `column > @watermark` | 僅新增不修改的日誌表 |

**水印生命週期：**
```
Job 開始 → 讀取 LastWatermarkValue
  → 每批次更新 PendingValue（取 batch 中的最大值）
  → 全部完成 → CommitPendingValue() → 存回 DB
  → 若失敗 → DiscardPendingValue() → 不更新，下次重跑
```

**時區處理：** 水印值一律以 UTC 儲存。若來源資料庫使用本地時間，在 `WatermarkTimeZone` 欄位指定時區（如 `"Asia/Taipei"`），系統自動在查詢時轉換。

### 8.6 使用場景範例

#### 場景 1：每小時從 MSSQL ERP 同步訂單（Timestamp 增量）

```csharp
// 1. 定義 Job
var job = new EtlJobDefinition
{
    Name = "sync-orders",
    Description = "每小時從 ERP 同步訂單到報表庫",
    CronExpression = "0 0 * * * ?",           // 每小時整點
    SourceDbType = DBTypeEnum.SqlServer,
    SourceConnectionKey = "ErpSource",         // appsettings.json 中的連線
    TargetConnectionKey = "default",
    QueryTemplate = @"
        SELECT OrderNo, CustomerName, Amount, Status, Region,
               OrderDate, UpdatedAt
        FROM dbo.Orders
        WHERE UpdatedAt > @watermark
        ORDER BY UpdatedAt",
    TargetTableName = "Orders",
    MergeKeyColumn = "OrderNo",               // MERGE ON 的 key
    WatermarkType = EtlWatermarkType.Timestamp,
    WatermarkColumn = "UpdatedAt",
    WatermarkTimeZone = "Asia/Taipei",
    InitialWatermarkValue = "2026-01-01T00:00:00",  // 首次同步起點
    BatchSize = 50000,
    TimeoutMinutes = 30,
    Status = EtlJobStatus.Enabled
};
```

**Pipeline 執行流程：**
```
1. EnsureStagingTable → 自動建立 Orders_Staging（與查詢結果同結構）
2. TruncateStaging → 清空 staging
3. 迴圈：
   a. Extract：SELECT ... WHERE UpdatedAt > '2026-03-12 09:00:00' (上次水印)
   b. Transform：（此例無自訂轉換）
   c. BulkLoad：SqlBulkCopy 寫入 Orders_Staging（每批 50,000 筆）
   d. 更新 PendingWatermark = batch 中最大的 UpdatedAt
4. Merge：
   MERGE INTO Orders AS target
   USING Orders_Staging AS source ON target.OrderNo = source.OrderNo
   WHEN MATCHED THEN UPDATE SET ...
   WHEN NOT MATCHED THEN INSERT ...
5. 成功 → Commit Watermark → 寫入 RunLog(Success)
   失敗 → Discard Watermark → 寫入 RunLog(Failed) + LastError
```

#### 場景 2：每天從 Oracle 同步客戶資料（FullLoad）

```csharp
var job = new EtlJobDefinition
{
    Name = "sync-customers",
    Description = "每天凌晨 2 點全量同步客戶主檔",
    CronExpression = "0 0 2 * * ?",           // 每天 02:00
    SourceDbType = DBTypeEnum.Oracle,
    SourceConnectionKey = "OracleHR",
    TargetConnectionKey = "default",
    QueryTemplate = @"
        SELECT CUSTOMER_ID, CUSTOMER_NAME, PHONE, EMAIL, REGION,
               CREDIT_LIMIT, STATUS
        FROM HR.CUSTOMERS
        WHERE STATUS = 'ACTIVE'",
    TargetTableName = "Customers",
    MergeKeyColumn = "CUSTOMER_ID",
    WatermarkType = EtlWatermarkType.FullLoad,  // 每次全量
    BatchSize = 10000,
    TimeoutMinutes = 60,
    Status = EtlJobStatus.Enabled
};
```

**Oracle 與 MSSQL Loader 差異：**

| 特性 | MssqlBulkLoader | OracleBulkLoader |
|------|----------------|-----------------|
| 寫入方式 | `SqlBulkCopy`（原生 binary copy） | Array Binding（參數化 INSERT） |
| 速度 | 極快（10 萬筆/秒） | 快（5 萬筆/秒） |
| Schema 探索 | `INFORMATION_SCHEMA.COLUMNS` | `USER_TAB_COLUMNS` |
| 表名命名 | 大小寫敏感（加 `[]` 括號） | 強制大寫 |
| MERGE 語法 | `MERGE ... WHEN MATCHED/NOT MATCHED` | `MERGE INTO ... USING` |

#### 場景 3：帶自訂轉換的 Pipeline

```csharp
// 在 EtlPipelineConfig 中設定 TransformFunc
var config = new EtlPipelineConfig
{
    // ... 基本設定 ...
    TransformFunc = batch =>
    {
        // 新增計算欄位
        batch.Columns.Add("FullName", typeof(string));
        batch.Columns.Add("AmountWithTax", typeof(decimal));

        foreach (DataRow row in batch.Rows)
        {
            row["FullName"] = $"{row["LastName"]}, {row["FirstName"]}";
            row["AmountWithTax"] = (decimal)row["Amount"] * 1.05m;
        }
        return batch;
    }
};
```

### 8.7 管理 UI 與操作

ETL 模組內建完整管理介面（`_EtlJobController`），支援 CRUD 和即時操作：

| 操作 | 端點 | 說明 |
|------|------|------|
| 列表/搜尋 | `/_EtlJob/Index`, `Search` | Job 清單（含狀態、下次執行時間） |
| 新增/編輯/刪除 | `/_EtlJob/Create`, `Edit`, `Delete` | 標準 CRUD |
| 手動觸發 | `POST /_EtlJob/TriggerNow?id=` | 立即執行一次（不影響排程） |
| 預覽執行（Dry-Run）| `POST /_EtlJob/DryRun?id=&sampleSize=10` | 只抓第一批 + transform，**不寫目標表**（10.4.0+，#834） |
| 暫停/恢復 | `POST /_EtlJob/Pause`, `Resume` | Quartz 排程暫停/恢復 |
| 中斷執行 | `POST /_EtlJob/Abort?id=` | 中斷正在執行的 Job |
| 跳過下次 | `POST /_EtlJob/SkipNext?id=` | 設定 SkipCount++ |
| 重新排程 | `POST /_EtlJob/Reschedule?id=&newCron=` | 更新 Cron 並重排 |

#### 8.7.1 Dry-Run 預覽模式（10.4.0+，#834）

關鍵 API：`EtlPipelineConfig.IsDryRun` 旗標 + `POST /_EtlJob/DryRun` 管理端點。

新增 Job 或改 `QueryTemplate` / `MergeKeyColumn` 之後，**先 Dry-Run 再 TriggerNow** 可以在不動目標表的前提下驗證：

- SQL 語法、權限、連線字串是否可行（Extract 會真的執行）
- `MergeKeyColumn` 是否存在於 source columns
- Transform mapper 輸出長什麼樣
- 下次真跑時 watermark 會 commit 成什麼值
- source 有沒有資料可抓（空 source 會出警告）

**與 TriggerNow 的差異：**

| | TriggerNow | DryRun |
|-|-----------|--------|
| Extract | ✓ 全部 | ✓ 只第一批 |
| Transform | ✓ | ✓ |
| EnsureStagingTable / Truncate / BulkLoad / Merge | ✓ | ✗ |
| Watermark commit | ✓ | ✗（計算但不寫回） |
| 寫入 RunLog | ✓ | ✗（不污染 audit trail）|

**Response payload：**

```json
{
  "success": true,
  "isDryRun": true,
  "extractedRows": 1000,
  "elapsedMs": 412,
  "newWatermarkValue": "2026-04-18T05:30:00",
  "previewRows": [ { "OrderNo": "ORD-00000001", "Amount": 100, "UpdatedAt": "..." } ],
  "validationWarnings": [ "MergeKeyColumn 'bogus' not found in source columns: [...]" ],
  "errorMessage": null
}
```

`sampleSize` query param 控制 `previewRows` 長度（預設 10，上限 100）。

### 8.8 監控 API

```
GET /_EtlMonitor/Running       → 所有執行中 job 及進度
GET /_EtlMonitor/Progress?jobId=X → 單一 job 即時進度
```

**進度回應格式：**
```json
{
    "jobId": "3fa85f64-...",
    "jobName": "sync-orders",
    "phase": "BulkLoad",
    "processedRows": 125000,
    "totalRows": null,
    "rowsPerSecond": 48000,
    "startedAt": "2026-03-12T10:00:00Z"
}
```

**執行歷史：**
```
GET /_EtlRunLog/Index?jobId=X → 該 Job 的所有執行紀錄
POST /_EtlRunLog/Rerun?runLogId=X → 從某次執行的水印快照重跑
```

**EtlRunLog 記錄的資訊：**

| 欄位 | 說明 |
|------|------|
| `Trigger` | Scheduled / Manual / Retry |
| `Result` | Success / Failed / Aborted / Skipped |
| `ExtractedRows` | 抽取的總筆數 |
| `LoadedRows` | 寫入的總筆數 |
| `ErrorRows` | 錯誤筆數 |
| `ElapsedMs` | 執行耗時（毫秒） |
| `ErrorMessage` | 錯誤訊息（最長 4000 字元） |
| `WatermarkSnapshot` | 執行時的水印值（可用於重跑） |

### 8.9 Cron 表達式速查

| 表達式 | 說明 |
|--------|------|
| `0 0 * * * ?` | 每小時整點 |
| `0 0/30 * * * ?` | 每 30 分鐘 |
| `0 0 2 * * ?` | 每天凌晨 2 點 |
| `0 0 0 ? * MON-FRI` | 週一到週五午夜 |
| `0 0 8,12,18 * * ?` | 每天 8:00、12:00、18:00 |

### 8.10 並發控制與錯誤處理

- **`[DisallowConcurrentExecution]`**：同一 Job 不會同時執行兩個實例
- **失敗處理**：水印不更新（`DiscardPendingValue`），下次自動從上次成功點重跑
- **`OperationCanceledException`**：標記為 `Aborted`（手動中斷或逾時）
- **重跑機制**：每次 RunLog 保存 `WatermarkSnapshot`，可從任何歷史時間點重跑

### 8.11 載入模式：Merge vs Replace（10.5.0+）

10.4 之前 ETL 的 BulkLoad 階段只有 Merge（依主鍵 upsert）。10.5 補上 Replace（先刪後插），用於 dimension table 完整重建、或「按月份覆寫銷售報表分區」這類場景。

```csharp
public enum EtlLoadMode { Merge, Replace }

var config = new EtlPipelineConfig
{
    LoadMode = EtlLoadMode.Replace,
    ReplaceWhereClause = "OrderDate >= '2026-01-01' AND OrderDate < '2026-02-01'",
    // null = 整表清空後重灌；非 null = 只清符合 WHERE 的列
    ColumnMappings = new()
    {
        ["src_cust_id"] = "CustomerID",      // rename：source 欄 → target 欄
        ["raw_blob"]    = null,              // null/empty = drop（不要寫入 target）
    },
};
```

**`IBulkLoader.ReplaceAsync`：**
- `MssqlBulkLoader`：開 transaction → `DELETE FROM target WHERE <clause>` → `SqlBulkCopy.WriteToServer` → commit
- `OracleBulkLoader`：同上但用 `ManagedDataAccess` + array-binding insert
- `IsSafeWhereClause` 公開工具：拒絕含 `;` / `--` / `xp_` / DML keyword 的字串。VM 層在 save 時就跑這個檢查，**不安全的 WHERE 在保存就被擋下，不會等到第一次跑 job**

### 8.12 ColumnMappings 欄位對應 / drop（10.5.0+）

跨資料庫遷移最常見痛點：source 與 target 欄名不一致 / 部分欄不要灌入。`EtlPipelineConfig.ColumnMappings: Dictionary<string, string?>?`：

```csharp
config.ColumnMappings = new()
{
    ["cust_id"]       = "CustomerID",        // rename
    ["order_no"]      = "OrderNumber",
    ["raw_payload"]   = null,                // drop（任何 null/empty 都當 drop）
    ["debug_flag"]    = "",                  // drop
};
```

執行階段：`EtlPipelineExecutor.ApplyColumnMappings(table, mappings)`（已 public 供測）— 改寫 `DataTable.Columns` 名稱、刪掉 drop 欄。`null`/沒設 = 沿用 source 欄名（向後相容）。

ColumnMappings 也支援 JSON 表示，存於 `EtlJobDefinition.ColumnMappingJson`（VM 驗證會解析、檢查 key/value 非空）：

```json
{ "cust_id": "CustomerID", "order_no": "OrderNumber", "raw_payload": "" }
```

### 8.13 IEtlSchemaService DB 自動探勘（10.5.0+）

3 步驟精靈裡「下一步」要列出 source DB 有哪些 table、target DB 有哪些 column — 由 `IEtlSchemaService` + `_EtlSchemaController` 提供：

```csharp
public interface IEtlSchemaService
{
    Task<IReadOnlyList<string>> ListTablesAsync(string csKey, string? schema = null, CancellationToken ct = default);
    Task<IReadOnlyList<EtlColumnInfo>> ListColumnsAsync(string csKey, string table, string? schema = null, CancellationToken ct = default);
}

public sealed record EtlColumnInfo(string Name, string DataType, bool IsNullable, int? MaxLength);
```

實作：
- `MssqlEtlSchemaService` — `INFORMATION_SCHEMA.TABLES` / `.COLUMNS`
- `OracleEtlSchemaService` — `ALL_TABLES` / `ALL_TAB_COLUMNS`
- `EtlSchemaServiceFactory.For(dbType)` 根據 `DBTypeEnum` 取對應實作

**Controller endpoints：**

```
GET /_EtlSchema/Tables?csKey=src&dbType=SqlServer
GET /_EtlSchema/Columns?csKey=src&dbType=SqlServer&table=Orders
```

RBAC 與 `_EtlJobController` 同一把鑰匙（Admin / ETLAdmin / IsQuickDebug bypass）。

### 8.14 批次重試與指數退避（10.5.0+）

任一個 BulkLoad 批次的 transient 失敗（DB lock timeout、network jitter、deadlock）以前會炸掉整個 job。10.5 補上 per-batch retry：

```csharp
var config = new EtlPipelineConfig
{
    MaxBatchRetries        = 5,                   // 預設 0（向後相容）；clamp 到 [0, 50]
    BatchRetryBaseDelayMs  = 200,
    BatchRetryMaxDelayMs   = 30_000,
};
```

退避公式：`delay = random(0, BaseDelay × 2^attempt)`（full-jitter exponential backoff），上限 `BatchRetryMaxDelayMs`。Cancellation 在等待中被尊重 — token 取消會以 `Aborted = true` 結束。

**水印契約不變**：耗盡重試預算 → 整個 job 失敗 + watermark 不 commit；其中一批 recover 成功 → watermark 正常 commit。

`EtlExecutionResult.RetryAttemptsTotal`（10.5.0+）報出本次執行所有批次的累計重試次數，可拉長條圖看「我的 ETL 多 flaky」。

`MockBulkLoader.TransientFailuresBeforeSuccess` 模擬 flaky bulk-load — 用來在自家 pipeline 寫 unit test：

```csharp
var loader = new MockBulkLoader { TransientFailuresBeforeSuccess = 2 };
// 前 2 次 BulkLoad 丟 transient exception，第 3 次成功
```

### 8.15 EtlJobDefinitionVM 表單驗證（10.5.0+）

`EtlJobDefinitionVM.Validate()` 的新增規則（save 時就抓，不等 first run 才炸）：

| 規則 | 條件 | 訊息欄位 |
|------|------|------|
| Merge 模式必填 MergeKeyColumn | `LoadMode == Merge && string.IsNullOrEmpty(MergeKeyColumn)` | `Entity.MergeKeyColumn` |
| Replace 模式 WHERE 安全檢查 | `LoadMode == Replace && !IsSafeWhereClause(ReplaceWhereClause)` | `Entity.ReplaceWhereClause` |
| ColumnMappingJson 必須是合法 JSON object | parse fail / 非 object | `Entity.ColumnMappingJson` |
| ColumnMappingJson key/value 不可為空白 | 任一鍵為空白 / 任一值為空白 | `Entity.ColumnMappingJson` |

```csharp
// Replace + null/empty WHERE = 「整表重灌」；操作員自負其責，validator 接受
vm.Entity.LoadMode           = EtlLoadMode.Replace;
vm.Entity.ReplaceWhereClause = null;
vm.Validate();   // 不 raise error

// Replace + 含 SQL injection 的 WHERE = 拒
vm.Entity.ReplaceWhereClause = "1=1; DROP TABLE Users";
vm.Validate();   // MSD["Entity.ReplaceWhereClause"] 出現

// Merge + 不安全 WHERE = 不檢查（WHERE 在 Merge 模式根本沒用）
vm.Entity.LoadMode = EtlLoadMode.Merge;
vm.Validate();   // 不 raise error
```

### 8.16 三步驟 Job 精靈 UI（10.5.0+）

`Views/_EtlJob/Create.cshtml` + `Edit.cshtml` 提供 wizard：

1. **Step 1 — Source**：填 `SourceCsKey` / `SourceDbType` → 點「探勘」呼叫 `_EtlSchema/Tables` → table 下拉自動填入 → 選表後 `_EtlSchema/Columns` 自動填出來、勾選想抽取的欄位
2. **Step 2 — Target**：填 `TargetCsKey` / `TargetTableName` → 同樣探勘 target schema → UI 用 left-right list 拉欄位對應（rename / drop）→ 自動序列化為 `ColumnMappingJson`
3. **Step 3 — Schedule + Mode**：cron 表達式視覺化解析、`LoadMode` 切換、`ReplaceWhereClause` / `MergeKeyColumn` 條件式顯示對應欄位

JS 命名空間 `EtlWizard`，AJAX 注入文字一律走 `escapeHtml()`（XSS 防禦）。Razor 中的字面 `@watermark_clause` 用 `@@watermark_clause` 雙 `@` 跳脫（不要被當識別字符）。

> **後端必須註冊 schema 服務：** `services.AddSingleton<MssqlEtlSchemaService>(); services.AddSingleton<OracleEtlSchemaService>();`（demo 已預註冊）

### 8.17 ETL 可視化儀表板（10.5.0+）

ETL 模組單頁總覽，view 路徑 `/_EtlDashboard/Index`：

```
┌─────────────── 7 個 KPI 卡 ────────────────┐
│ TotalJobs / ActiveJobs / DisabledJobs       │
│ RunningNow / RunsInWindow / SuccessRate     │
│ TotalRowsLoadedInWindow                     │
└─────────────────────────────────────────────┘
┌── 結果分布 donut ───┬── 每日 stacked-bar ──┐
│ Success/Failed/      │ 過去 N 天 4 種狀態   │
│ Aborted/Skipped      │ 疊圖 + RowsLoaded    │
└─────────────────────┴──────────────────────┘
┌── 進行中（live） ────┬── 最近失敗（topN）──┐
│ JobName/Phase/進度   │ JobName/StartedAt/   │
│ Bar/RowsPerSec       │ ElapsedMs/ErrorMsg   │
└─────────────────────┴──────────────────────┘
        ┌── 最慢 jobs（topN）──┐
        │ JobName/Avg/Max/Run #│
        └──────────────────────┘
```

**Backend：**

```csharp
public class EtlDashboardService
{
    public EtlDashboardSummary BuildSummary(IDataContext dc, int windowDays = 7, int topN = 10);
}
```

```csharp
services.AddSingleton<EtlDashboardService>();   // AddWtmEtl() 已內建
```

- `windowDays` clamp 到 `[1, 90]`、`topN` clamp 到 `[1, 100]`
- `DailyTrend` 永遠輸出 `windowDays` 個資料點（oldest first）— front-end 圖表 x 軸密度恆定，零 run 的天 fall back 到 0 0 0 0
- `SlowestJobs` **只計** `Result == Success` 的 run — 失敗常 abort early 會把均值偏快、結果誤導
- 進行中的 `JobName` 從 `EtlProgressTracker` 取，空字串時從 DB 反查 `EtlJobDefinition.Name` 補上
- UTC 桶（避免 timezone bug）

**Endpoints：**

| Method | Path | 用途 |
|------|------|------|
| GET | `/_EtlDashboard/Index` | 渲染 partial view |
| GET | `/_EtlDashboard/Stats?days=N&topN=M` | 回 JSON snapshot（front-end polling） |

RBAC 與 `_EtlJobController` 同一把鑰匙（Admin / ETLAdmin / IsQuickDebug bypass）。

**Front-end（demo Razor + ECharts 5）：**
- 載 ECharts 5 from CDN（`<script>` 直接加，不必動 `_Layout`）
- 每 10 秒 polling `/Stats`；window-range select 變更觸發即時 refresh
- `window.addEventListener("resize")` → 圖表 resize
- 所有 AJAX 注入文字過 `escapeHtml()` — 連錯誤訊息都不洩 XSS sink
- SuccessRate 顯色：`<80%` 紅 / `80–95%` 琥珀 / `≥95%` 藍

### 8.18 ETL 效能調校 opt-in 選項（10.6.0+）

10.6.0 為 ETL pipeline 加入一組 opt-in 調校旋鈕。**全部 additive、預設維持舊行為**；`IBulkLoader` 介面簽章不變（第三方 loader 實作不受影響）。

| 選項 | 位置 | 預設 | 說明 |
|------|------|------|------|
| `bulkCopyOptions` | `MssqlBulkLoader` ctor | `SqlBulkCopyOptions.Default` | 傳給 `SqlBulkCopy`；私有 staging 表建議用 `TableLock`（20–50% 吞吐）。 |
| `internalBatchSize` | `MssqlBulkLoader` ctor | `0`（= 整批一次） | >0 時設 `SqlBulkCopy.BatchSize`，分批 checkpoint、降 TDS 尖峰記憶體。 |
| `timeoutSeconds` | `OracleBulkLoader` ctor | `0`（= 無限等待，舊行為） | >0 時對 Merge/Replace/Truncate/EnsureStaging/BulkLoad 設 `CommandTimeout`，避免永久卡死。 |
| `FetchRowCount` | `OracleSource` 屬性 | `0`（= ODP.NET 預設） | >0 時設 reader `FetchSize`，寬列 / 高延遲連線可 3–10× 抽取吞吐。 |
| `WatermarkSqlType` | `EtlPipelineConfig` | `null`（= `AddWithValue`） | 設定後 MSSQL watermark 改用明確型別的 `SqlParameter`，避免 plan-cache 污染 / 隱式轉換。 |

**Schema 快取裝飾器**：`EtlSchemaServiceFactory.CreateWithCache(dbType, IMemoryCache, ttl?)` 回傳 `CachingEtlSchemaService`（預設 60 秒 TTL，cache key 以連線字串的 SHA256 摘要區隔，不存明文）；裸 `Create(...)` 維持不快取。`_EtlSchemaController` 在 DI 提供 `IMemoryCache` 時自動採用。

```csharp
// 範例：opt-in MSSQL TableLock + 分批；Oracle 逾時保護
var loader = new MssqlBulkLoader(bulkCopyOptions: SqlBulkCopyOptions.TableLock, internalBatchSize: 5000);
var oracle = new OracleBulkLoader(timeoutSeconds: 300);

// 範例：schema 探勘加 60s 快取
var schemaSvc = EtlSchemaServiceFactory.CreateWithCache(DBTypeEnum.SqlServer, memoryCache);
```

> 修復（10.6.0）：MSSQL `GetColumnsAsync` 先前只用 `TABLE_NAME` 過濾，對 schema-qualified staging 表（如 `audit.STG_x`）回傳 0 欄位；現在一併過濾 `TABLE_SCHEMA`（未限定 schema → `dbo`）。Scheduler 的 `UpdateStatusAsync` / `SkipNextAsync` 改用 `ExecuteUpdateAsync`，`SkipCount` 改伺服器端遞增（消除 read-then-write 競態）並明確補 `UpdateTime` 稽核欄位。

### 8.19 來源連接器 + Governance（10.8.0+，全 opt-in）

10.8.0 把 ETL 從「内建幾種 loader」擴成可插拔的連接器生態，並加上生產級治理。皆 opt-in，預設行為不變。

**來源連接器（`EtlSourceRegistry`，`AddWtmEtl()` 註冊）：**

| 連接器 | 說明 |
|--------|------|
| `CsvEtlSource` | CSV 檔案來源 |
| `ExcelEtlSource` | Excel（NPOI）來源 |
| `PostgreSqlSource` + `PostgreSqlBulkLoader` | PostgreSQL 來源 + `COPY` + `ON CONFLICT` upsert |
| `MySqlEtlSource` + `MySqlBulkLoader` | MySQL 來源 + `ON DUPLICATE KEY` upsert |
| `RestEtlSource`（`RestEtlSourceConfig`）| REST/HTTP 來源，分頁 + 認證 + SSRF 強化 |

```csharp
// 註冊 registry + 内建來源
builder.Services.AddWtmEtl();

// 自訂來源可註冊進 registry
services.AddSingleton<IEtlSource, MyCustomSource>();
```

> **行為變更**：先前 PostgreSQL/MySQL 的 bulk-load 會拋 `NotSupportedException`，現在改用 provider 原生 upsert。新增依賴 `Npgsql` 10.0.2 + `MySqlConnector` 2.4.0。

**Governance（`AddWtmEtlAlerts()` + `IEtlGovernanceStore`）：**

- **run-log retention / composite merge keys / SLA 告警 / dry-run audit**（#229）
- **dead-letter quarantine**（`EtlDeadLetterRow`）：失敗列隔離保存，原始錯誤文字寫入前先 sanitize
- **資料血緣**（`EtlLineageRecord`）：記錄 source→target 的轉換軌跡
- **per-tenant ETL job 隔離**（`EtlJobDefinition : ITenant`，新增 `TenantCode` 欄位）
- 持久化由 `DbEtlGovernanceStore` 提供；預設是 `NullEtlGovernanceStore`（no-op）
- **webhook 告警卡**（`EtlAlertService`，#235）：失敗 / SLA 破壞事件可透過共用 webhook sink 推播；預設關閉（`EtlAlertOptions.EnableWebhookAlerts = false`）

```csharp
// opt-in DB governance + webhook 告警
builder.Services.AddWtmEtlAlerts(opt =>
{
    opt.EnableWebhookAlerts = true;   // 預設 false
});
```

> **Migration**：啟用 `DbEtlGovernanceStore` 或 per-tenant job 隔離會引入新的 EF Core 實體（dead-letter、lineage、`EtlJobDefinition` 含 `TenantCode`）。**啟用前需先產生並套用 EF Core migration。** 不啟用則完全不受影響。

### 8.20 10.15.0 安全/資源 guardrails（#680/#703/#700 等）

10.15.0 對 ETL 加了一批 fail-closed guardrails，多數零設定即生效、少數改了預設值（**升級必讀** `CHANGELOG.md` `[10.15.0]` Migration 段）：

- **來源欄名驗證（identifier-injection 硬化）**：source 欄名必須符合 `^[\p{L}\p{N}_#$]+$`（Unicode 字母/數字、`_`、`#`、`$`——CJK 欄名不受影響），不合格（空白、引號、`;`、`--`、emoji 等）在 extraction 期即失敗。解法：用 `ColumnMappings`（§8.12）改名到合格 target。
- **`RestEtlSourceConfig` 分頁預設收緊**：`NextLink` 跨 host 分頁預設拒絕（需顯式 `AllowCrossHostPagination = true`）；爬頁上限新預設 `MaxPages = 1000`（要無限爬需顯式 `MaxPages = 0`）。
- **Dead-letter 有界化**：`EtlOptions.MaxDeadLetterRowsPerRun`（預設 10000，超出丟棄並標記截斷）；`DeadLetterFlushMode = Periodic`（opt-in，#700）提供 bounded crash-durability，預設 `OncePerRun` 維持原行為。
- **ReDoS 防護（#703）**：使用者供給的 regex 加 timeout 防護。

---

## 9. Dashboard 模組

Dashboard 提供可拖拽、可設定的即時數據看板，支援 6 種 Widget 類型、自訂資料來源、Analysis Mode 整合、多租戶隔離與角色共享。

### 9.1 啟用方式

```csharp
// Program.cs / Startup.cs
services.AddWtmDashboard(opts =>
{
    opts.DashboardDirectory = "App_Data/dashboards";  // JSON 檔案儲存路徑
    opts.DefaultRefreshInterval = 60;                  // 秒
    opts.EnableEditing = true;                         // 允許前端編輯
    opts.AllowIframeSameOrigin = false;                // Embed widget 安全設定
});

// 註冊自訂資料來源
services.AddWidgetDataSource<SalesKpiDataSource>();
services.AddWidgetDataSource<InventoryTableSource>();
// 或批量掃描 assembly
services.AddWidgetDataSourcesFromAssembly(typeof(Startup).Assembly);
```

### 9.2 核心模型

**DashboardDefinition** — 一個看板的完整定義：
```csharp
{
    "id": "sales-overview",
    "title": "銷售總覽",
    "owner": "admin",
    "tenantId": null,
    "sharing": { "mode": "public", "roles": null },
    "refreshInterval": 30,
    "layout": [
        { "id": "w1", "x": 0, "y": 0, "w": 4, "h": 2 },
        { "id": "w2", "x": 4, "y": 0, "w": 8, "h": 4 },
        { "id": "w3", "x": 0, "y": 2, "w": 4, "h": 4 }
    ],
    "widgets": {
        "w1": {
            "type": "kpi",
            "title": "本月營收",
            "source": { "kind": "custom", "name": "sales-kpi" },
            "config": { "format": "currency", "prefix": "NT$" }
        },
        "w2": {
            "type": "chart",
            "title": "月度趨勢",
            "source": {
                "kind": "analysis",
                "listVmType": "MyApp.ViewModels.SaleRecordListVM, MyApp",
                "dimensions": [{ "field": "SaleDate", "hierarchy": "Month" }],
                "measures": [{ "field": "Amount", "func": "Sum" }]
            },
            "config": { "chartType": "line" }
        },
        "w3": {
            "type": "table",
            "title": "待處理訂單",
            "source": { "kind": "custom", "name": "pending-orders" },
            "config": {}
        }
    },
    "filters": [
        { "field": "region", "label": "地區", "type": "select", "options": ["北區","南區","中區"] }
    ],
    "links": [
        { "source": "w1", "target": "w2", "event": "click", "param": "region" }
    ]
}
```

### 9.3 六種 Widget 類型

| 類型 | 用途 | 資料格式 | 使用場景 |
|------|------|---------|---------|
| **kpi** | 單一數值 + 趨勢箭頭 | `Value`, `PreviousValue` | 營收、訂單數、轉換率 |
| **chart** | ECharts 圖表（bar/line/pie 自動偵測） | `Rows[]`, `Columns[]` | 趨勢分析、分佈圖 |
| **table** | 可排序表格 | `Rows[]`, `Columns[]` | 明細清單、排行榜 |
| **progress** | 進度條 + 百分比 | `Value` (0~1 或 0~100) | 目標達成率、專案進度 |
| **list** | 項目列表（含狀態標籤） | `Rows[{label, status, url}]` | 待辦事項、告警清單 |
| **embed** | iframe 嵌入 | `config.url` | 外部報表、Grafana |

### 9.4 自訂資料來源（Custom DataSource）

**介面定義：**
```csharp
public interface IWidgetDataSource
{
    string Name { get; }
    WidgetDataSourceKind Kind { get; }
    Task<WidgetDataResult> GetDataAsync(
        WidgetDataRequest request, CancellationToken ct = default);
}
```

**完整範例 — 銷售 KPI：**
```csharp
public class SalesKpiDataSource : IWidgetDataSource
{
    public string Name => "sales-kpi";
    public WidgetDataSourceKind Kind => WidgetDataSourceKind.Custom;

    private readonly DataContext _db;
    public SalesKpiDataSource(DataContext db) => _db = db;

    public async Task<WidgetDataResult> GetDataAsync(
        WidgetDataRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var thisMonth = await _db.Set<Order>()
            .Where(o => o.OrderDate >= new DateTime(now.Year, now.Month, 1))
            .SumAsync(o => o.Amount, ct);

        var lastMonth = await _db.Set<Order>()
            .Where(o => o.OrderDate >= new DateTime(now.Year, now.Month - 1, 1)
                     && o.OrderDate < new DateTime(now.Year, now.Month, 1))
            .SumAsync(o => o.Amount, ct);

        return new WidgetDataResult
        {
            Value = thisMonth,
            PreviousValue = lastMonth  // 前端自動計算趨勢 ↑↓
        };
    }
}
```

**完整範例 — 待處理訂單表格：**
```csharp
public class PendingOrdersDataSource : IWidgetDataSource
{
    public string Name => "pending-orders";
    public WidgetDataSourceKind Kind => WidgetDataSourceKind.Custom;

    private readonly DataContext _db;
    public PendingOrdersDataSource(DataContext db) => _db = db;

    public async Task<WidgetDataResult> GetDataAsync(
        WidgetDataRequest request, CancellationToken ct = default)
    {
        // 支援 Dashboard 級 filter
        var region = request.Parameters.GetValueOrDefault("region");

        var query = _db.Set<Order>().Where(o => o.Status == "Pending");
        if (!string.IsNullOrEmpty(region))
            query = query.Where(o => o.Region == region);

        var rows = await query
            .OrderByDescending(o => o.OrderDate)
            .Take(20)
            .Select(o => new Dictionary<string, object?>
            {
                ["訂單號"] = o.OrderNo,
                ["客戶"] = o.CustomerName,
                ["金額"] = o.Amount,
                ["日期"] = o.OrderDate.ToString("yyyy-MM-dd")
            })
            .ToListAsync(ct);

        return new WidgetDataResult
        {
            Columns = new List<string> { "訂單號", "客戶", "金額", "日期" },
            Rows = rows,
            Metadata = new Dictionary<string, object?> { ["totalCount"] = rows.Count }
        };
    }
}
```

**完整範例 — 進度條：**
```csharp
public class QuarterTargetDataSource : IWidgetDataSource
{
    public string Name => "quarter-target";
    public WidgetDataSourceKind Kind => WidgetDataSourceKind.Custom;

    private readonly DataContext _db;
    public QuarterTargetDataSource(DataContext db) => _db = db;

    public async Task<WidgetDataResult> GetDataAsync(
        WidgetDataRequest request, CancellationToken ct = default)
    {
        var actual = await _db.Set<Order>()
            .Where(o => o.OrderDate >= GetQuarterStart())
            .SumAsync(o => o.Amount, ct);

        var target = 1_000_000m;  // 季度目標

        return new WidgetDataResult
        {
            Value = actual / target,  // 0~1 的比率
            Metadata = new Dictionary<string, object?>
            {
                ["max"] = target,
                ["label"] = $"NT${actual:N0} / NT${target:N0}"
            }
        };
    }
}
```

### 9.4.1 REST 資料來源（10.4.0+，無需 C#）

關鍵 API：`RestWidgetDataSource` + `WidgetDataSourceKind.Rest`。

**用途**：直接從外部 HTTP endpoint 取 JSON 資料，不需要寫 `IWidgetDataSource` 實作；admin 在 Dashboard JSON config 填 URL + headers + `jsonPath` 即可。

**Dashboard config：**

```json
{
  "id": "weather-taipei",
  "widget": "kpi",
  "dataSource": {
    "kind": "Rest",
    "options": {
      "url": "https://api.weather.gov/points/25.03,121.56",
      "method": "GET",
      "headers": { "Accept": "application/json" },
      "jsonPath": "$.properties.forecast",
      "cacheTtlSeconds": 300,
      "timeoutSeconds": 10,
      "maxResponseBytes": 1048576,
      "allowPrivateNetwork": false,
      "allowHttp": false
    }
  }
}
```

**`jsonPath` 支援**：僅簡單 dot-path（`$`、`$.data`、`$.result.items`）。不支援 wildcard、array filter；進階需求用 `Custom` DataSource。

**回應型別映射：**

| JSON 形狀 | `WidgetDataResult` | 適合 widget |
|-----------|-------------------|-------------|
| scalar (`42`, `"hi"`, `true`) | `Value` | `kpi`, `progress` |
| array of objects | `Rows[]` + `Columns[]` | `table`, `chart` |
| array of primitives | `Rows[{value}]` + `Columns=["value"]` | `chart` |
| single object | `Rows[ { ...obj } ]` + `Columns[]` | `kpi`, `list` |

**SSRF 防護（預設 on）：**

Admin-supplied URL 是 classic SSRF 攻擊面。預設禁以下 IP 範圍（hostname resolve 後檢查）：

| 範圍 | 例子 |
|------|------|
| RFC 1918 私網 | `10.*.*.*`, `172.16-31.*.*`, `192.168.*.*` |
| Loopback | `127.*.*.*`, `::1` |
| Link-local | `169.254.*.*`（含 AWS IMDS `169.254.169.254`） |
| Multicast | `224.0.0.0/4` |
| IPv6 ULA / link-local / multicast | `fc00::/7`, `fe80::/10`, `ff00::/8` |

opt-in 內部 endpoint：設 `allowPrivateNetwork: true`（搭配 `allowHttp: true` 若用 plain HTTP）。

**安全與性能上限：**

- Response body 上限 `maxResponseBytes`（預設 1 MiB）— 防 memory 爆炸
- 請求 timeout `timeoutSeconds`（預設 10s）
- 回應以 `cacheTtlSeconds`（預設 60s）快取於 `IMemoryCache`，key = `method + url + body + jsonPath`；設 0 停用快取
- URL 僅接受 `https://`（預設）；`http://` 需 `allowHttp: true` opt-in

**不支援的範圍**：無 OAuth2 flow、無 retry/backoff、無 streaming、無 outbound webhook 接收（這些請用 `Custom` DataSource）。

### 9.5 Analysis Mode 資料來源

Widget 可直接使用任何 `[EnableAnalysis]` 的 ListVM 作為資料來源，無需寫自訂 DataSource：

```json
{
    "type": "chart",
    "title": "部門薪資分佈",
    "source": {
        "kind": "analysis",
        "listVmType": "MyApp.ViewModels.EmployeeListVM, MyApp",
        "dimensions": [{ "field": "DepartmentName_view" }],
        "measures": [
            { "field": "Salary", "func": "Sum" },
            { "field": "Salary", "func": "Avg" }
        ],
        "filters": [
            { "field": "HireDate", "operator": ">=", "value": "2025-01-01" }
        ]
    }
}
```

安全性：所有欄位透過 `AnalysisVmRegistry` 白名單驗證，與 Analysis Mode API 相同。

### 9.6 API 端點

| 端點 | 方法 | 認證 | 用途 |
|------|------|------|------|
| `/_dashboard/list` | GET | 所有已認證使用者 | 列出可見的 Dashboard（依 owner/role/tenant 過濾） |
| `/_dashboard/{id}` | GET | 檢視權限 | 取得完整 Dashboard 定義（layout + widgets + filters） |
| `/_dashboard` | POST | 所有已認證使用者 | 建立新 Dashboard（server 端設定 owner + tenant） |
| `/_dashboard/{id}` | PUT | 編輯權限 | 更新 Dashboard（owner 不可改、tenant 鎖定） |
| `/_dashboard/{id}` | DELETE | 編輯權限 | 刪除 Dashboard（不可回復） |
| `/_dashboard/{id}/widget/{wid}/data` | GET/POST | 檢視權限 | 取得 Widget 資料（支援 query string 或 POST body 傳入 filter） |
| `/_dashboard/datasources` | GET | 所有已認證使用者 | 列出可用資料來源（custom + Analysis Mode ListVM） |

### 9.7 前端 JavaScript API

```html
<!-- 引入內嵌資源 -->
<script src="/_js/framework_dashboard.js"></script>
<link rel="stylesheet" href="/_js/framework_dashboard.css" />
```

**初始化 Dashboard：**
```javascript
// 載入並渲染 Dashboard
const dashboard = await WtmDashboard.DashboardManager.init('container', 'sales-overview');
// 自動開始定時刷新（依 refreshInterval 設定）

// 手動控制刷新
WtmDashboard.DashboardManager.stopRefresh();
WtmDashboard.DashboardManager.startRefresh(30);  // 30 秒
```

**編輯模式：**
```javascript
// 切換拖拽編輯模式（暫停刷新）
WtmDashboard.DashboardEditor.toggleEditMode();

// 新增 Widget
WtmDashboard.DashboardEditor.addWidget('w-new', {
    type: 'kpi',
    title: '新指標',
    source: { kind: 'custom', name: 'my-source' },
    config: { format: 'percent' }
}, { x: 0, y: 6, w: 4, h: 2 });

// 儲存（PUT /_dashboard/{id}）
await WtmDashboard.DashboardEditor.saveDashboard();
```

**Dashboard Filter 聯動：**
```javascript
// FilterBar 控制 → 所有 Widget 重新載入（帶 filter 參數）
WtmDashboard.FilterBar.onChange(filters => {
    // filters = { region: "北區", dateRange: "2026-Q1" }
    // 自動傳入每個 widget 的 data 請求
});
```

**Widget 間聯動（EventBus）：**
```javascript
// Widget w1 (KPI) 點擊時通知 w2 (Chart) 過濾
WtmDashboard.EventBus.on('click', 'w2', data => {
    // data = { region: "北區" }
    // w2 重新載入，帶入 region 參數
});
```

### 9.8 存取控制

| 身分 | 列表 | 檢視 | 建立 | 編輯 | 刪除 |
|------|:----:|:----:|:----:|:----:|:----:|
| **Owner** | 自己的 | ✅ | ✅ | ✅ | ✅ |
| **Admin** | 全部 | ✅ | ✅ | ✅ | ✅ |
| **角色共享** | 共享的 | ✅ | — | — | — |
| **公開** | 公開的 | ✅ | — | — | — |

**共享設定：**
```json
// 私有（僅 owner + admin）
{ "mode": "private" }

// 公開（所有已認證使用者）
{ "mode": "public" }

// 角色共享（指定角色 + owner + admin）
{ "mode": "private", "roles": ["manager", "analyst"] }
```

### 9.9 使用場景範例

#### 場景 1：主管營運看板

**需求：** 主管需要即時看到營收 KPI、月度趨勢、待處理訂單。

```csharp
// 1. 建立 3 個 DataSource
services.AddWidgetDataSource<SalesKpiDataSource>();       // KPI: 本月 vs 上月
services.AddWidgetDataSource<PendingOrdersDataSource>();  // Table: 待處理訂單
// Chart: 直接用 Analysis Mode，不需自訂 DataSource
```

Dashboard JSON:
```json
{
    "title": "營運看板",
    "sharing": { "mode": "private", "roles": ["manager"] },
    "refreshInterval": 30,
    "layout": [
        { "id": "revenue",  "x": 0, "y": 0, "w": 3, "h": 2 },
        { "id": "orders",   "x": 3, "y": 0, "w": 3, "h": 2 },
        { "id": "target",   "x": 6, "y": 0, "w": 3, "h": 2 },
        { "id": "trend",    "x": 0, "y": 2, "w": 6, "h": 4 },
        { "id": "pending",  "x": 6, "y": 2, "w": 6, "h": 4 }
    ],
    "widgets": {
        "revenue": { "type": "kpi", "title": "本月營收", "source": { "kind": "custom", "name": "sales-kpi" }, "config": { "format": "currency", "prefix": "NT$" } },
        "orders":  { "type": "kpi", "title": "訂單數", "source": { "kind": "custom", "name": "order-count-kpi" }, "config": {} },
        "target":  { "type": "progress", "title": "季度目標", "source": { "kind": "custom", "name": "quarter-target" }, "config": { "color": "#1890ff" } },
        "trend":   { "type": "chart", "title": "月度營收趨勢", "source": { "kind": "analysis", "listVmType": "MyApp.SaleRecordListVM, MyApp", "dimensions": [{"field":"SaleDate","hierarchy":"Month"}], "measures": [{"field":"Amount","func":"Sum"}] }, "config": {"chartType":"line"} },
        "pending": { "type": "table", "title": "待處理訂單 TOP 20", "source": { "kind": "custom", "name": "pending-orders" }, "config": {} }
    },
    "filters": [
        { "field": "region", "label": "地區", "type": "select", "options": ["全部","北區","南區","中區"] }
    ]
}
```

#### 場景 2：IT 監控面板

```json
{
    "title": "系統監控",
    "sharing": { "mode": "private", "roles": ["admin", "devops"] },
    "refreshInterval": 10,
    "widgets": {
        "cpu":     { "type": "progress", "title": "CPU 使用率", "source": { "kind": "custom", "name": "system-cpu" }, "config": { "color": "#52c41a" } },
        "memory":  { "type": "progress", "title": "記憶體", "source": { "kind": "custom", "name": "system-memory" }, "config": { "color": "#faad14" } },
        "alerts":  { "type": "list", "title": "近期告警", "source": { "kind": "custom", "name": "recent-alerts" }, "config": {} },
        "grafana": { "type": "embed", "title": "Grafana", "source": { "kind": "custom", "name": "empty" }, "config": { "url": "/grafana/d/api-latency" } }
    }
}
```

#### 場景 3：多租戶 SaaS — 每個租戶有自己的看板

```csharp
// Dashboard 自動隔離 — 建立時自動帶入 TenantId
// POST /_dashboard → server 端從 LoginUserInfo.TenantCode 注入 tenantId
// 租戶 A 看不到租戶 B 的 Dashboard，即使知道 ID 也返回 403
```

### 9.10 配置選項

```csharp
public class DashboardOptions
{
    public string DashboardDirectory { get; set; } = "App_Data/dashboards";
    public bool EnableEditing { get; set; } = true;
    public int DefaultRefreshInterval { get; set; } = 60;
    public bool AllowIframeSameOrigin { get; set; } = false;  // Embed 安全
}
```

### 9.11 儲存結構

Dashboard 以 JSON 檔案儲存（非資料庫）：
```
App_Data/dashboards/
├── _default/              ← 無租戶
│   ├── sales-overview.json
│   └── system-monitor.json
├── tenant-a/              ← 租戶 A
│   └── custom-board.json
└── tenant-b/              ← 租戶 B
    └── operations.json
```

### 9.12 安全注意事項

- **Embed Widget**：iframe 預設 `sandbox="allow-scripts"`（無 `allow-same-origin`），`javascript:`, `data:`, `vbscript:` URL 被封鎖
- **Analysis 資料來源**：所有欄位經 `AnalysisVmRegistry` 白名單驗證，無 SQL 注入風險
- **多租戶**：Server 端強制 tenant 隔離，非同租戶的 GET/PUT/DELETE 返回 403
- **編輯權限**：僅 owner 和 admin 可修改/刪除

### 9.13 無代碼設計器 + DB store + 告警/快照（10.8.0+，全 opt-in）

10.8.0 把 Dashboard 從「手寫 JSON 定義」升級成完整的低代碼 BI 子系統。所有新能力皆 opt-in，預設仍走 JSON-file store、不啟用告警/快照。

**無代碼設計器（#238）：** 拖拉式設計器頁面 `/_DashboardPage/Designer`，後端 `_DashboardDesignerController`（路由 `/_dashboard-designer`）：

| Method | Path | 用途 |
|--------|------|------|
| GET | `/vm-meta?vmType=…` | 回傳已註冊 Analysis VM 的 dimensions/measures（`AnalysisFieldScanner.ScanModel`） |
| POST | `/preview` | 對 widget 草稿做即時資料預覽（建臨時單 widget dashboard → `GetWidgetDataAsync` → `finally` 必刪） |

設計器讓使用者不寫 JSON 即可綁定 Analysis VM / REST 來源 / 靜態值，設定圖表類型、篩選、DateRange preset、KPI 阈值、跨 widget drill-down，並產出與 runtime 完全相同的 `DashboardDefinition`/`WidgetDefinition` schema。安全：兩個 endpoint 皆 `[AllRights]`；tenant 取自伺服器端 `LoginUserInfo.TenantCode`；widget 型別過 `DashboardOptions.AllowedWidgetTypes`、filter operator 過 `FilterConfig.AllowedOps` 後才打資料；前端零 user-data `innerHTML`（全 DOM-method 建構）。

**DB-backed store（#234）：** `AddWtmEfDashboardStore()` 註冊 `EfCoreDashboardService`，以 EF Core 取代 JSON 檔案持久化 dashboard。所有讀/寫/刪皆租戶範圍；新增 `IDashboardService.DeleteAsync(id, tenantId)` overload 強制刪除時的租戶擁有權。並支援**跨 widget drill-down**：點某 widget 的資料點，會把其 dimension 值推進 dashboard filter bar。

```csharp
// opt-in：改用 DB store（預設仍是 JSON 檔案）
builder.Services.AddWtmEfDashboardStore();
```

**KPI 阈值告警（#237）：** `AddWtmDashboardAlerts()` 註冊背景評估器 `DashboardAlertHostedService`，週期性以 `WidgetThreshold` 規則（`ThresholdComparisonOp` Gt/Ge/Lt/Le/Eq、`ThresholdAlertLevel`、`ThresholdEvaluator`）評估 widget measure，並透過共用 webhook sink 推播告警卡。告警在「跨越阈值」時觸發（含 cooldown 去重）、租戶感知。預設關閉（`DashboardAlertOptions.EvaluationIntervalSeconds = 0`）。

**排程快照 / 匯出（#237）：** `AddWtmDashboardSnapshots()` 註冊 cron 驅動的 `DashboardSnapshotHostedService`，依排程把 dashboard 匯出成 Excel（`DashboardExcelExporter`，NPOI 多 sheet）。PDF/PNG 匯出透過可插拔的 `IDashboardRenderer` seam — framework **不綁** headless 瀏覽器；未註冊 renderer 時 `NotConfiguredDashboardRenderer` 會拋出帶指引的例外。

```csharp
// opt-in：KPI 告警 + 排程快照
builder.Services.AddWtmDashboardAlerts(opt => opt.EvaluationIntervalSeconds = 60);
builder.Services.AddWtmDashboardSnapshots();
```

**Widget 體驗（#228, #231）：** widget 設定前置驗證；`AnalysisWidget` 資料加 per-widget timeout 快取；圖表新增 multi-series、6 種額外圖表類型、DateRange preset、in-flight 請求防重、static-value widget 型別。

> **Migration**：`AddWtmEfDashboardStore` 引入新的 dashboard EF Core 實體。**啟用前需先產生並套用 EF Core migration。** 不啟用則維持 JSON-file store 不受影響。

---

## 10. 安全機制

### 10.1 密碼雜湊

**演算法：** ASP.NET Core Identity 的 PBKDF2 實作（SHA-256, 100,000 iterations, 128-bit salt）

```csharp
// 雜湊密碼（用於註冊、重設密碼）
string hash = PasswordHashHelper.HashPassword("MyP@ssw0rd");
// 輸出格式：Base64 編碼的 Identity V3 格式（包含 salt + iterations + hash）

// 驗證密碼（用於登入）
var result = PasswordHashHelper.VerifyPassword(storedHash, "MyP@ssw0rd");
// result: Success | Failed | SuccessRehashNeeded
```

**三種驗證結果：**

| 結果 | 說明 | 後續動作 |
|------|------|---------|
| `Success` | 密碼正確，hash 格式最新 | 直接放行 |
| `SuccessRehashNeeded` | 密碼正確，但 hash 需要升級 | 放行 + 背景重新 hash |
| `Failed` | 密碼錯誤 | 拒絕登入 |

**Legacy MD5 自動遷移流程：**
```
使用者登入 → IsLegacyMD5Hash(storedHash)?
  → 是：用 MD5 驗證 → 正確 → 自動 rehash 為 PBKDF2 → 更新 DB → 登入成功
  → 否：用 PBKDF2 驗證 → 正常流程
```

`IsLegacyMD5Hash()` 判斷方式：hash 為 32 字元十六進位字串（MD5 格式）。

### 10.2 JWT 認證流程

**完整流程圖：**
```
Client                          Server
  │                                │
  │  POST /api/_account/login      │
  │  { ITCode, Password }         │
  │──────────────────────────────→│ 驗證密碼
  │                                │ 產生 AccessToken (15 min)
  │  { access_token, refresh_token │ 產生 RefreshToken (7 days)
  │    expires_in: 900 }           │ 存入 RefreshTokenEntity 表
  │←──────────────────────────────│
  │                                │
  │  GET /api/data                 │
  │  Authorization: Bearer {AT}    │
  │──────────────────────────────→│ 正常存取
  │←──────────────────────────────│
  │                                │
  │  ... 15 分鐘後 AT 過期 ...      │
  │                                │
  │  POST /api/_account/refreshtoken│
  │  { refreshToken: "old_RT" }    │
  │──────────────────────────────→│ 驗證 old_RT 有效
  │                                │ 標記 old_RT 為 Revoked
  │  { access_token: "new_AT",     │ 產生 new_RT（Token 旋轉）
  │    refresh_token: "new_RT" }   │ old_RT.ReplacedByToken = new_RT
  │←──────────────────────────────│
  │                                │
  │  POST /api/_account/revoketoken│
  │  { refreshToken: "new_RT" }    │
  │──────────────────────────────→│ 登出（撤銷所有 token chain）
  │←──────────────────────────────│
```

**Access Token Claims：**

| Claim | 說明 |
|-------|------|
| `jti` | 唯一 Token ID（`Guid.NewGuid().ToString("N")`） |
| `sub` | 使用者帳號（ITCode） |
| `TenantCode` | 租戶代碼（多租戶場景） |
| `exp` | 過期時間（預設 900 秒 = 15 分鐘） |

**Refresh Token 安全機制：**
- 64 bytes 隨機數（Base64 編碼）
- 存入 `RefreshTokenEntity` 資料表（含 `CreatedByIp`、`RevokedByIp`）
- **Token 旋轉**：每次 refresh 都產生全新的 token pair，舊 token 立即作廢
- **鏈式撤銷**：若被撤銷的 token 有 `ReplacedByToken`，整條 chain 全部撤銷（防止 token 被盜用後的重播攻擊）

**在前端使用：**
```javascript
// 登入
const res = await fetch('/api/_account/login', {
    method: 'POST',
    body: JSON.stringify({ ITCode: 'admin', Password: 'xxx' })
});
const { access_token, refresh_token } = await res.json();

// 帶 Token 請求
fetch('/api/employee', {
    headers: { 'Authorization': `Bearer ${access_token}` }
});

// Token 過期後刷新
const refreshRes = await fetch('/api/_account/refreshtoken', {
    method: 'POST',
    body: JSON.stringify({ refreshToken: refresh_token })
});
const newTokens = await refreshRes.json();
```

**10.15.0 起：上述契約是「強制」的（#721 修復）。** 10.15.0 之前 `WTMContext.RefreshTokenAsync()` 從不驗證 caller 送來的 refresh token（憑仍有效的 Bearer AT 即可無限換發新 pair）；10.15.0 起 refresh 必須出示登入時取得的**真** `refresh_token`，否則 `401`。連帶注意：

- **刪除 app-copy 的 `RefreshToken` action**：若你的專案抄過 demo 的 `_Admin/AccountController.RefreshToken`，必須刪除（與框架端點同路由會 `AmbiguousMatchException`）——**刪除前先確認該 action 上的 attributes**（範本掛著 `[WtmRateLimit(100, 60)]`；若它是你唯一的該 tuple 註冊源，刪除會連帶取消具名 policy 的註冊，引用它的 minimal-API 端點會每請求 500——10.17.0 起可改用 `RegisterPolicy` 顯式註冊，見 §10.9）。
- **既有資料庫必須先有 `FrameworkRefreshTokens` 表**：#721 使 token 持久化首次真正寫入（每次成功登入 INSERT 一列、rotation 再一列，寫入失敗＝登入失敗）。舊版 `EnsureCreated()` 建立的 DB **沒有**這張表，升級後全站登入失敗；fresh DB 自動建表，staging/e2e 測不出來。欄位對照與 idempotent DDL 指引見 `CHANGELOG.md` `[10.15.0]` Migration 段。
- **表無界成長 → opt-in retention（10.17.0，#757）**：`services.AddWtmRefreshTokenRetention()` 啟用每日清理（預設 04:00 本地時間，與 ActionLog 的 03:00 錯開；`ExpiredDays`/`RevokedDays` 預設 30 天、`BatchSize` 5000、任一 knob ≤0 停用該類）。**硬性安全不變量**：revoked-but-unexpired 的列（reuse-attack 鏈式撤銷的 tripwire）無論設定為何都不會被刪。未註冊擴充方法＝完全不啟用（零預設行為變更）。
- **索引（10.17.0，#761）**：`Token` / `ExpiresUtc` / `(RevokedUtc, ExpiresUtc)` 三個索引隨 entity 內建——僅對 fresh DB 自動生效；既有 DB 需手動 `CREATE INDEX`（`EnsureCreated` 不回填索引）。

### 10.3 權限模型

WTM 採用 **RBAC（角色存取控制）** + **列級資料權限** 雙層模型：

```
FrameworkUser
  ├── FrameworkUserRole ──→ FrameworkRole
  │                           ├── FunctionPrivilege ──→ FrameworkMenu (URL)
  │                           └── DataPrivilege (列級過濾)
  └── FrameworkUserGroup ──→ FrameworkGroup
                              └── DataPrivilege (列級過濾)
```

#### 功能權限（FunctionPrivilege）

控制「使用者能存取哪些頁面/功能」：

```
角色「業務經理」 → 可存取 /Employee/Index, /Employee/Create, /Employee/Edit
角色「業務員」   → 可存取 /Employee/Index（只能看，不能改）
```

**PrivilegeFilter 運作流程（每個 HTTP 請求）：**
```
收到請求 → 取得請求 URL
  → 是否標記 [Public]? → 是 → 放行
  → 是否標記 [AllRights]? → 是 → 已登入就放行
  → Wtm.IsAccessable(url) 檢查 → 該角色有此 URL 的 FunctionPrivilege? → 放行/拒絕
```

#### 資料權限（DataPrivilege）

控制「使用者能看到哪些資料列」，在查詢層面自動過濾：

**場景：業務員只能看自己負責區域的客戶**

```csharp
// 1. 在 Admin 後台設定 DataPrivilege：
//    使用者「張三」→ 表「Customer」→ 欄位「Region」→ 值「北區」
//    意思：張三只能看到 Region = "北區" 的客戶

// 2. 在 ListVM 的 GetSearchQuery 中使用 DPWhere
protected override IOrderedQueryable<Customer> GetSearchQuery()
{
    return DC!.Set<Customer>()
        .DPWhere(Wtm!, x => x.Region)   // 自動根據 DataPrivilege 設定過濾
        .OrderBy(x => x.Name);
}
// 張三查詢時，SQL 自動加上 WHERE Region = '北區'
// 管理員則沒有限制（看到全部資料）
```

**DPWhere 內部機制：**
1. 從 `Wtm.LoginUserInfo` 取得當前使用者
2. 查詢 `DataPrivilege` 表：此使用者（或其所屬群組）對此 Table + Column 有哪些限制
3. 動態建構 Expression Tree 加入 WHERE 條件
4. 無 DataPrivilege 記錄 = 無限制（看全部）

### 10.4 Analysis Mode 安全

Analysis Mode 有多層安全防護：

| 層級 | 機制 | 說明 |
|------|------|------|
| VM 白名單 | `AnalysisVmRegistry` | 只有標記 `[EnableAnalysis]` 的 ListVM 才能被查詢 |
| 欄位白名單 | `AnalysisFieldScanner` | 只有標記 `[Dimension]`/`[Measure]` 的欄位才能作為維度/度量 |
| VM 層角色控制 | `[EnableAnalysis(AllowedRoles = "...")]` | 限制可存取此 VM 的角色（值為 **RoleCode**，逗號分隔） |
| 欄位層角色控制 | `[Dimension/Measure(AllowedRoles = "...")]` | 限制可見特定欄位的角色（值同為 **RoleCode**） |
| 欄位存取策略 | `IAnalysisFieldPolicy` | 框架呼叫 `Filter(fields, ClaimsPrincipal)` 過濾可用欄位；`ClaimsPrincipal` 以 `RoleCode` 建構 role claims |
| 資料權限 | `DPWhere` | `GetSearchQuery()` 中的資料權限在 Analysis 中同樣生效 |
| Expression 防注入 | 白名單比對 | 欄位名稱必須完全匹配白名單，無法注入任意表達式 |
| 結果截斷 | `Take(50_000)` + 10,000 行 | 防止大量資料洩露 |
| CSV 防公式注入 | 前置 tab | `=+@-\t\r` 開頭的儲存格加 tab |

**角色識別符規則**：`AllowedRoles` 填入的值必須是 `RoleCode`（資料庫 `FrameworkRole.RoleCode` 欄位），**不是** `RoleName`。`RoleCode` 是穩定的機器識別符；`RoleName` 可能在 UI 中被修改。

```csharp
// ✅ 正確：使用 RoleCode
[EnableAnalysis(AllowedRoles = "analyst,finance_mgr")]
public class SalesListVM : BasePagedListVM<Sales, SalesSearcher>

[Measure(AllowedRoles = "finance_mgr")]
public decimal Salary { get; set; }

// ❌ 錯誤：不要使用 RoleName（顯示名稱）
[EnableAnalysis(AllowedRoles = "財務主管")]  // RoleName 可變，不應依賴
```

`RoleCode = "Admin"` 的使用者永遠繞過欄位層限制（完整存取）。

### 10.5 跨模組 RBAC 統一規範

WTM 三個進階模組（Analysis、Dashboard、ETL）使用相同的角色識別符（`RoleCode`），但各自有不同的設定層次：

| 模組 | RBAC 設定位置 | 角色識別符 | Admin 快速路徑 |
|------|--------------|-----------|--------------|
| **Analysis** | `[EnableAnalysis(AllowedRoles)]`（VM 層） + `[Dimension/Measure(AllowedRoles)]`（欄位層） | `RoleCode` | `RoleCode = "Admin"` 繞過所有限制 |
| **Dashboard** | `DashboardDefinition.Sharing.Roles`（分享設定，陣列） | `RoleCode` | `DashboardOptions.AdminRoles`（預設 `["Admin"]`）可存取全部 Dashboard |
| **ETL** | WTM 標準 `[ActionDescription]` + Admin 後台功能權限授予 | `FunctionPrivilege`（URL 為鍵） | 同一般 Controller 機制 |

#### Analysis RBAC 設定範例

```csharp
// VM 層：限制哪些角色可以查詢這個 VM
[EnableAnalysis(AllowedRoles = "analyst,bi_viewer")]
[ActionDescription("銷售分析")]
public class SalesListVM : BasePagedListVM<Sales, SalesSearcher>
{
    // 欄位層：限制哪些角色可見此欄位
    [Measure(AllowedFuncs = AggregateFunc.Sum, AllowedRoles = "finance_mgr")]
    public decimal GrossProfit { get; set; }

    [Dimension]            // 無 AllowedRoles = 所有已授權使用者都能看到
    public string Region { get; set; }
}
```

#### Dashboard RBAC 設定範例

```json
{
  "id": "sales-overview",
  "title": "Sales Overview",
  "owner": "alice",
  "sharing": {
    "mode": "roles",
    "roles": ["analyst", "sales_mgr"]
  }
}
```

- `mode: "private"` — 僅 owner 可見
- `mode: "public"` — 所有已登入使用者可見
- `mode: "roles"` — `roles` 陣列中列出的 `RoleCode` 可見（加上 owner 和 Admin）

`roles` 陣列的值必須是 `RoleCode`，與 Analysis 的 `AllowedRoles` 規則一致。

#### ETL RBAC

ETL Controller 使用標準 WTM `[AllRights]`（已登入即可）和 `[ActionDescription]`（每個 action 一條 URL 功能權限）。透過 Admin 後台的「角色管理 → 功能授權」頁面為角色授予或撤銷各個 ETL 操作的存取權限，不需要修改程式碼。

```
_EtlController.Index  →  URL: /_etl/index  →  在 Admin 後台為角色 "etl_operator" 授權
_EtlController.Trigger →  URL: /_etl/trigger/{id}
_EtlController.Pause   →  URL: /_etl/pause/{id}
```

#### 不得混用 RoleCode 與 RoleName

所有三個模組的角色設定都必須使用 `RoleCode`，禁止使用 `RoleName`：

| ❌ 錯誤 | ✅ 正確 |
|--------|--------|
| `AllowedRoles = "財務主管"` | `AllowedRoles = "finance_mgr"` |
| `roles: ["業務分析師"]` | `roles: ["analyst"]` |

`RoleName` 是 UI 顯示名稱，由管理員可以在後台修改；`RoleCode` 是不可變的機器識別符，是安全邊界的正確依據。

### 10.6 審計日誌（ActionLog）

所有 Controller Action（除標記 `[NoLog]` 者）自動寫入 `ActionLog` 表：

| 欄位 | 內容 |
|------|------|
| `ITCode` | 操作者帳號 |
| `ActionUrl` | 請求 URL |
| `ActionName` | `[ActionDescription]` 的值 |
| `ActionTime` | 操作時間 |
| `IP` | 客戶端 IP |
| `Duration` | 執行耗時（毫秒） |
| `Remark` | 額外資訊（如錯誤訊息） |
| `LogType` | Normal / Exception / Debug |

**在 `FrameworkFilter.OnResultExecuted` 中寫入**，所以即使 Action 拋出例外也會被記錄。

#### 10.6.1 ActionLog 保留政策（opt-in 背景服務，10.4.0+，#832）

`ActionLog` 會隨流量線性成長（典型每日 1K–10K 筆），半年就會累積百萬筆，造成：

- 後台 ActionLog 列表查詢分頁變慢
- 資料庫儲存 / 備份成本增加
- 金融、醫療等合規場景要求「保留期限」—原生 WTM 無此機制
- 單次 `DELETE FROM ActionLogs WHERE ActionTime < ...` 是地雷—大交易鎖表

`AddWtmActionLogRetention(configure)` 是 opt-in 的 `IHostedService`，每日排定時間跑一次批次刪除：

```csharp
// Program.cs
services.AddWtmActionLogRetention(opt =>
{
    opt.Enabled = true;          // master switch
    opt.RunAtLocalHour = 3;       // 03:00 local
    opt.NormalDays = 90;
    opt.ExceptionDays = 365;      // 例外留久一點，方便事後 debug
    opt.DebugDays = 30;
    opt.JobDays = 90;
    opt.BatchSize = 5000;         // 每次 DELETE 最多 5000 列，避免大交易鎖表
});
```

**關鍵設計決策：**

- **per-`LogType` TTL**：Normal/Exception/Debug/Job 各自獨立天數；設 `0` 或負數等於保留無限（該類別不刪）。
- **批次迴圈**：使用 EF Core 7+ `ExecuteDeleteAsync`（`DELETE ... WHERE ActionTime < @cutoff AND LogType = @t`），搭配 `OrderBy(ActionTime).Take(BatchSize)` 分批刪；每輪迴圈直到該類別當日配額耗盡才收工，避免一次大交易導致鎖表。
- **容錯**：任何單輪失敗都只記 Warning（不 crash hosting），下一個排程還會重試。
- **結構化日誌**：每輪結束寫一筆 `ActionLogRetention: sweep complete. Normal=... Exception=... total=...`。
- **opt-out**：`opt.Enabled = false` 則服務閒置（不刪任何東西），方便某些金融客戶以外部 archival pipeline 管理保留。

**手動觸發**：測試或 admin API 可直接呼叫 `svc.RunRetentionOnceAsync(options, ct)`，不必等到排程時間。

### 10.7 Content Security Policy（opt-in 中介軟體，10.3.0+）

`UseWtmContentSecurityPolicy()` 為可選 CSP 中介軟體，在 response 掛 `Content-Security-Policy` 標頭，封鎖 `'unsafe-eval'`（issue #789 六階段 `framework_layui.js` eval 清除的收益落地）。

**預設策略（不含 `'unsafe-eval'`）：**

```
default-src 'self';
script-src  'self' 'unsafe-inline';
style-src   'self' 'unsafe-inline' https://fonts.googleapis.com;
img-src     'self' data: https:;
font-src    'self' data: https://fonts.gstatic.com;
connect-src 'self';
frame-src   'self';
object-src  'none';
base-uri    'self';
form-action 'self';
```

**啟用：**

```csharp
// Startup.Configure
app.UseWtmContentSecurityPolicy();                         // 預設策略

app.UseWtmContentSecurityPolicy(o => o.ReportOnly = true); // 監控模式（不封鎖、只報告）

app.UseWtmContentSecurityPolicy(o =>
{
    o.ScriptSrc = "'self'";               // 嚴格：禁 inline scripts（需配合 TagHelper 重構）
    o.ReportUri = "/csp-report";
});
```

**關鍵取捨：**

| 指令 | 預設 | 原因 |
|------|------|------|
| `'unsafe-eval'` | **omitted** | #789 六階段清除 eval，框架自身無需此例外 |
| `'unsafe-inline'` | **kept** | LayUI TagHelper 大量輸出 inline `<script>` 與 `style=""`；移除需重構所有 TagHelper（#807 epic 追蹤，本repo pre-Gitea-cutover GitHub tracker 編號，該tracker已停用） |
| `img-src data: https:` | 寬鬆 | 支援上傳圖片 base64 + 外部 CDN |

**行為：**
- **Opt-in**：app 未呼叫 → 不加 header（零 breaking change）
- **First-writer-wins**：若上游中介軟體或反向代理已設過 `Content-Security-Policy`，本中介軟體不覆蓋
- **ReportOnly 模式**：emit `Content-Security-Policy-Report-Only` 而非 enforcement

**遷移須知：** 啟用前先搜尋 app 自有 JS 的 `" + "eval(" + "` 呼叫，全部改用 JSON 或 `Function` 等 CSP 相容寫法。

#### 10.7.1 Legacy script rehydration kill-switch（opt-in，10.14.3+，#627）

CSP 硬化的下一階（拿掉 `script-src` 的 `'unsafe-inline'`）被 `framework_layui.js` 為 AJAX 載入內容保留的 **legacy 動態腳本執行面**擋住。10.14.3 新增 opt-in 開關（預設 OFF，行為 byte-identical），二擇一：

```html
<!-- 佈局標記（建議 — 純 markup，最嚴 CSP 下也可用） -->
<meta name="wtm-disable-legacy-script-rehydration" content="true">
```

```js
ff.DisableLegacyScriptRehydration = true; // 嚴格 boolean —— 字串 'true' 不算
```

開啟後**四個** legacy 執行點全部改為封鎖＋大聲診斷：`ff._legacyScriptEval`（已棄用 `IsScript` 回應的唯一 `eval(`）→ `console.error`；`ff.OpenDialog` / `ff._replayInitFromHtml` 的 inline `<script>` 再注入迴圈與 `ff.OpenDialog2` 的 selector 搜尋面板 `$$script$$` 還原 → 跳過執行＋計數 `console.warn`。腳本**抽取**（DOMPurify 淨化管線的一部分）與 JSON island 派發兩種模式下皆不變；開關每次呼叫即時讀取（SPA/測試可動態切換）。

**資格前提（開啟前必讀）**：多個 TagHelper 組態至今仍發出可執行 inline `<script>`（combobox/tree `xmSelect.render`、transfer、ueditor、upload、checkbox/radio 預設值、AJAX partial 內 grid、selector 面板、datetime callback 與 range 分支、帶 callback 的 slider/colorpicker、非識別字 `BeforeSubmit`——**非窮舉**，#470 hard-blocker 清單）。AJAX 對話框含這些 widget 時開啟開關會（大聲地）壞。權威稽核法是 **staging 實開開關看診斷**，不是比對 widget 清單。完整分級配方（level 0 出廠 eval-free 預設 → level 1 稽核 → level 2 production 開啟 → level 3 `script-src 'self'`）見 `docs/csp-hardening.md`。

### 10.8 Cookie SecurePolicy（10.3.0+）

`CookieOption.SecurePolicy` 控制 session 與認證 cookie 的 `Secure` flag 設定策略，影響 `AddWtmSession` 與 `AddWtmAuthentication` 兩條路徑：

| 值 | 行為 | 場景 |
|---|------|------|
| `SameAsRequest`（預設） | 請求為 HTTPS 時設 `Secure`，HTTP 則不設 | 本地 HTTP dev、direct-HTTPS production |
| `Always` | **永遠**設 `Secure` flag | Production 建議，特別是 TLS-terminating reverse proxy（nginx、Azure App Service、IIS ARR、Cloudflare）後面 |
| `None` | 永不設 `Secure` flag | 僅用於 HTTP-only 特殊場景（非常不建議） |

**反向代理場景的陷阱：**

```
Client ──HTTPS──▶ nginx ──HTTP──▶ WTM app
                                    │
                                    └── 看到 HTTP → SameAsRequest 不加 Secure flag
                                        → cookie 可被瀏覽器送往任意 HTTP endpoint
                                        → session hijack 風險
```

**Production 設定：**

```json
// appsettings.Production.json
{
  "CookieOptions": {
    "SecurePolicy": "Always",
    "LoginPath": "/Login/Login",
    "Expires": 3600
  }
}
```

**預設保持 `SameAsRequest`**：upgrade WTM 不會突然中斷本地 HTTP dev 或現有 direct-HTTPS 部署。只有明確 opt-in `Always` 才獲得強化行為。

### 10.9 Per-endpoint Rate Limit `[WtmRateLimit]`（10.4.0+）

`[WtmRateLimit(permits, windowSeconds)]` 為單一 controller action 或整個 controller 加上**較嚴格的 per-IP quota**，疊加在 `AddWtmRateLimiting` 裝的全域 limiter 之上。專為 brute-force-sensitive 端點設計（登入 / 密碼重設 / 忘記密碼）。

```csharp
[HttpPost("/Login/Login")]
[WtmRateLimit(5, 60)]    // 每 IP 每 60 秒最多 5 次
public IActionResult Login(LoginDTO vm) { ... }

[HttpPost("/Account/ResetPassword")]
[WtmRateLimit(3, 300)]   // 每 IP 每 5 分鐘最多 3 次
public IActionResult ResetPassword(ResetPasswordDTO vm) { ... }

// 也可貼在 Controller 類別上（action 層覆寫優先）
[WtmRateLimit(10, 60)]
public class SensitiveController : ControllerBase { ... }
```

**設計要點**：
- **Per-IP per-endpoint 獨立 bucket** — 攻擊者在 `/Login` 耗光 quota 不影響 `/ResetPassword`
- **Policy dedup**：多個 actions 共用相同 `(permits, window, queue)` → 共用單一 registered policy（避免 policy 爆炸）
- **疊加全域 limiter**：per-endpoint 不會取消全域；兩者皆檢查，先命中的拒絕
- **預設 queue=0**：超過限額立即 429（brute-force 場景中 queue 不幫攻擊者）
- **啟動時 assembly 掃描**：`AddWtmRateLimiting` 掃描所有已載入 assembly，找出所有 `[WtmRateLimit]` 的 `(permits, window, queue)` unique 組合，動態 `AddPolicy` 註冊
- **ASP.NET Core 集成**：`IActionModelConvention` 於 model-build 階段把 `WtmRateLimitAttribute` 映射成 `EnableRateLimitingAttribute` 放進 endpoint metadata；runtime 由內建 rate-limit middleware 接手
- **未啟用則無效**：必須同時 `services.AddWtmRateLimiting()` + `app.UseWtmRateLimiting()` 才生效

**拒絕回應**：HTTP 429 Too Many Requests，Serilog 會記錄一筆 `Rate limit exceeded` warning（`ClientIp` + `Path` + `Method`）— 跟全域 limiter 的 `OnRejected` 同一 logger。

**驗證範例**：

```csharp
// 在測試用 TestServer 裡
var r1 = await client.GetAsync("/Login");  // 200
var r2 = await client.GetAsync("/Login");  // 200 (第 2 次)
var r3 = await client.GetAsync("/Login");  // 200 (第 3 次)
var r4 = await client.GetAsync("/Login");  // 200 (第 4 次)
var r5 = await client.GetAsync("/Login");  // 200 (第 5 次)
var r6 = await client.GetAsync("/Login");  // 429 ⛔ quota 用罄
```

**限制**：
- 只支援 fixed-window（非 sliding / token-bucket）— 簡單、低延遲、足夠擋 brute-force
- Per-IP 分區；未支援 per-user / per-tenant 分區（後者可透過 `WtmRateLimitingOptions.CustomConfig` 自行加 policy）
- Controller 類別與 action 同時貼 `[WtmRateLimit]` 時 **action 層優先**

**10.17.0 起：minimal-API 端點的顯式註冊（#759）。** 10.17.0 之前，具名 policy **只**從 attribute 掃描產生——minimal-API 端點（health check 等）想套 rate limit 只能引用「恰好有某個 controller 掛著同 tuple」的 policy，重構/刪掉那個 action 就會讓不相干的端點**每請求 500**（build 與測試全綠照樣漏抓；下游兩次實證）。新 API 消除這個隱式耦合：

```csharp
services.AddWtmRateLimiting(opt => {
    opt.RegisterPolicy(100, 60);          // 顯式保證 wtm_rl_100_60_0 存在（與 attribute ctor 同一套驗證）
});
...
app.MapGet("/live", ...).RequireWtmRateLimit(100, 60);   // 掛 canonical policy metadata
```

- `RegisterPolicy` 的 tuple 與掃描到的 attribute tuple 以 HashSet 合併去重——顯式＋attribute 同 tuple 不會重複 `AddPolicy`
- `RequireWtmRateLimit` **只掛 metadata、不自動註冊**——tuple 必須由 `RegisterPolicy` 或某個 `[WtmRateLimit]` 提供，否則首個請求即拋 `InvalidOperationException`（大聲失敗，不會靜默）
- 同版並修復 `ScanWtmRateLimitAttributes` 的逐成員 partial-load 防護（MSTest host 內直呼不再因 test-adapter assembly 拋 `TypeLoadException`）

### 10.10 X-Correlation-Id middleware（opt-in，10.4.0+）

`UseWtmCorrelationId()` 中介軟體讓跨服務 trace ID 可從 upstream → WTM → log 連貫穿透，解決 SRE/on-call 查問題要手動比對 timestamp+IP 的痛點。

```csharp
// Startup.Configure
app.UseWtmCorrelationId();                                     // 預設 X-Correlation-Id
app.UseWtmCorrelationId(o => o.HeaderName = "X-Request-Id");   // Heroku/Rails 風格
app.UseWtmCorrelationId(o => o.AdoptInbound = false);          // 不信任 upstream，永遠自產
```

**行為**：
1. **Inbound**：讀請求 header（default `X-Correlation-Id`），通過 sanitization 則採用為此請求的 trace ID
2. **Fallback**：缺少 / 不合格 → 自產 `Guid.NewGuid("N")` 32-hex UUID
3. **Propagate**：覆寫 `HttpContext.TraceIdentifier` → Serilog scope / `Activity.Current.Id` / `WtmProblemDetails.traceId` **自動** 使用同一 ID
4. **Outbound**：回寫 response header（可 opt-out）

**Sanitization 規則（防 log injection）**：
- 空值 / 超過 `MaxLength`（default 128） → 拒絕
- 只允許 `[A-Za-z0-9\-_.]` 字符 — 涵蓋 UUID / W3C Trace Context / slug / 點分命名；拒絕 CR/LF、分號、逗號、空格、control char、非 ASCII
- 不合格 → 轉為 fallback auto-gen（不是錯誤，是 silent replace）

**Options**：

| 欄位 | 預設 | 說明 |
|------|------|------|
| `HeaderName` | `"X-Correlation-Id"` | Request 讀 / Response 寫 的 header 名 |
| `MaxLength` | `128` | 允許 inbound ID 最大長度 |
| `AdoptInbound` | `true` | 是否採用 upstream 傳進的 ID；zero-trust 環境可設 `false` |
| `EmitOutbound` | `true` | 是否在 response 回寫 header；純內部服務可設 `false` |

**驗證範例**：

```
→ GET /api/orders/42
  X-Correlation-Id: caller-abc-123

← 200 OK
  X-Correlation-Id: caller-abc-123            ← 原樣回寫
  ProblemDetails traceId = "caller-abc-123"   ← 錯誤響應自動用新 ID
  Serilog log: TraceIdentifier = "caller-abc-123"  ← scope 自動關聯
```

**與 OpenTelemetry 整合**：WTM 的 `AddWtmOpenTelemetry()` 已設 `Activity.Current` 串接；本 middleware 覆寫 `TraceIdentifier` 後，OpenTelemetry propagator（W3C Trace Context）可自行銜接，或 app 用標準 `propagation.extract` / `inject` pattern。

**不做**：
- 不自動整合 W3C `traceparent` / `tracestate` 解析 — 用標準 `System.Diagnostics.Activity` propagator 即可
- 不支援多 header fallback chain（一個主 header 即可）
- 不加 HMAC / 簽章（correlation ID 非安全 token）

### 10.11 Secure response headers middleware（opt-in，10.4.0+，#838）

`UseWtmSecureHeaders()` 把 OWASP Secure Headers Project 推薦的 hardening header 一次打在每個 response 上，跟 `UseWtmContentSecurityPolicy()` 互補：

```csharp
// Startup.Configure — 用預設值（HSTS 仍然 off）
app.UseWtmSecureHeaders();

// 生產環境：打開 HSTS + DENY frame
app.UseWtmSecureHeaders(opt =>
{
    opt.HstsEnabled = true;
    opt.HstsMaxAgeSeconds = 63_072_000;     // 2 年
    opt.HstsIncludePreload = true;          // 僅在登記 hstspreload.org 後才開
    opt.XFrameOptions = "DENY";
});
```

**預設寫入的 headers**：

| Header | 預設值 | 說明 |
|--------|-------|------|
| `X-Content-Type-Options` | `nosniff` | 防止 MIME sniffing 繞過 |
| `X-Frame-Options` | `SAMEORIGIN` | Clickjacking baseline（舊瀏覽器 fallback；CSP `frame-ancestors` 在新瀏覽器優先）|
| `Referrer-Policy` | `strict-origin-when-cross-origin` | 限制 Referer header 外漏（同步於主流瀏覽器預設）|
| `Permissions-Policy` | `accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()` | 預設拒絕所有敏感 Web API（app 需要時 override）|
| `Strict-Transport-Security` | **opt-in**（`HstsEnabled = false`）| 啟用後 `max-age=31536000; includeSubDomains`；**僅在 HTTPS 請求才寫入** |

**設計要點**：

1. **Per-header 關閉**：把欄位設成 `null` 即不輸出該 header，例如 `opt.ReferrerPolicy = null`
2. **HSTS 預設 off**：因為 HSTS 指令一旦 cache，瀏覽器會拒絕未來的 HTTP fallback；在 TLS 還未確定完美的階段打開是 footgun。我們 force explicit opt-in（`HstsEnabled = true`）並在 middleware 裡再用 `Request.IsHttps` gate，雙保險
3. **First-writer-wins**：上游 reverse proxy（AWS ALB / Cloudflare / nginx）已經寫了某個 header，本 middleware 不覆寫。App 可用自訂 middleware 在前面 pre-set 來 override 本 middleware 的預設值
4. **與 CSP 分開**：CSP 複雜度高（directive 組合、nonce、reporting）值得獨立 middleware；hardening bundle 則是幾個 constant-ish header 打一起即可

**完整推薦配置（production + HTTPS + 主域）**：

```csharp
app.UseWtmSecureHeaders(opt =>
{
    opt.HstsEnabled = true;
    opt.HstsMaxAgeSeconds = 63_072_000;
    opt.HstsIncludeSubDomains = true;
    opt.HstsIncludePreload = false;    // 等穩定再送審 hstspreload.org
    opt.XFrameOptions = "DENY";        // 完全禁止被框架嵌入
});
app.UseWtmContentSecurityPolicy();     // CSP 獨立 middleware，繼續用
```

**不做**：
- 不輸出 `X-XSS-Protection` — 該 header 自 Chrome 78 / Firefox 17 後已 deprecate，新瀏覽器忽略，舊瀏覽器 coverage 不足以值得 framework 層管理
- 不處理 CSP — 已在 #789 Phase 3D 交付（see §10.7）
- 不打 COOP / COEP / CORP — 高級 cross-origin isolation 通常需要 app-level 取捨，framework 預設留白

### 10.12 Slow-request logging middleware（opt-in，10.4.0+，#840）

`UseWtmSlowRequestLogging()` 是輕量 SRE helper：用 `Stopwatch` 包住整個 middleware pipeline，當 request 耗時超過門檻就發一筆 **structured Warning log**。沒超門檻就幾乎零成本（只有 stopwatch + 一次 path 前綴比對）。

```csharp
app.UseWtmSlowRequestLogging();                              // 預設 1000 ms

app.UseWtmSlowRequestLogging(opt =>
{
    opt.ThresholdMs = 500;
    opt.LogLevel = LogLevel.Error;                            // 改打 Error 方便分流
    opt.IncludeQueryString = false;                           // 預設 false 避免 PII 外漏
    opt.IncludeClientIp = true;
    opt.PathExclusions = new[] { "/healthz", "/_framework", "/_js", "/static" };
});
```

**日誌欄位**（Serilog structured template）：

```
WRN SlowRequest Path={Path} Method={Method} Status={StatusCode} ElapsedMs={Elapsed} User={User} ClientIp={ClientIp}
```

log aggregator（Loki / Seq / Elastic）按 `Path` / `Method` / `User` 分組就能直接畫 P95 latency dashboard，**不需要** APM。

**Options**：

| 欄位 | 預設 | 說明 |
|------|------|------|
| `ThresholdMs` | `1000` | 超過此毫秒數才 log |
| `LogLevel` | `Warning` | 超門檻 log 的 level（SRE 可分流到 Error / alerting pipeline）|
| `IncludeClientIp` | `true` | 是否把 request 來源 IP 放進 log（PII 議題：IP 有時也算）|
| `IncludeQueryString` | **`false`** | 預設不記 query string — tokens / IDs / emails 常常從這裡洩漏 |
| `PathExclusions` | `["/healthz", "/_framework", "/_js", "/_content", "/favicon.ico"]` | prefix 比對，大小寫不敏感；靜態資源 / health probe 預設排除避免 log 洪水 |

**安全設計**：
- `LogSanitizer` 處理 path / IP / query string — 防 CR/LF / 控制字元 log injection
- `IncludeQueryString` 預設 false — 因 structured log 通常被 index，query string 裡的 token 容易變成 searchable secret
- `PathExclusions` 有預設覆蓋率：static + health，避免「一開啟就發現 healthz 每 5 秒 spam 一次」的常見 footgun

**不做**：
- 不維護 per-endpoint histogram / percentile — 那是 OpenTelemetry metrics 的領域，非 log middleware 職責
- 不做 per-endpoint 門檻 — v1 只支援一個 global threshold；app 應設為最嚴 SLA 的值
- 不做 sampled full-request tracing — 是另一個 surface

### 10.13 CSP 三態模式 + frame-ancestors（10.5.0+，#843–#846）

10.3.0 引入 `UseWtmContentSecurityPolicy()` 時用了 `bool ReportOnly` 表達兩種狀態。10.5.0 升級為 `WtmCspMode` 三態 enum，並補上 `frame-ancestors`（取代過時的 `X-Frame-Options`）與 server-side 違規回報端點。

**三種模式：**

```csharp
public enum WtmCspMode { Disabled, ReportOnly, Enforce }
```

| 值 | header | 適用情境 |
|------|------|------|
| `Disabled` | 不寫 | 維運切回（不必動程式碼）、本地除錯、灰度回滾 |
| `ReportOnly` | `Content-Security-Policy-Report-Only` | 上線前評估規則對使用者影響 |
| `Enforce` | `Content-Security-Policy` | 正式啟用（預設） |

**完整啟用：**

```csharp
app.UseWtmCspReport();                  // 接收瀏覽器回報，必須在 CSP 之前
app.UseWtmContentSecurityPolicy(opt =>
{
    opt.Mode = WtmCspMode.Enforce;       // 取代舊 ReportOnly bool
    opt.FrameAncestors = "'self'";       // 反 clickjacking — 預設 'none'
    opt.DefaultPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "connect-src 'self'";
    opt.ReportUri = "/_csp/report";      // 與 UseWtmCspReport() 配對
});
```

**舊 `ReportOnly` 仍向下相容**（標 `[Obsolete]`），但不要再在新程式碼裡用 — `Mode` 設了非預設值就以 `Mode` 為準。

**回報端點 `_CspReportMiddleware`：**

```csharp
app.UseWtmCspReport(opt =>
{
    opt.RatePerMinute = 10;              // 每 IP 每分鐘上限（防 amplification）；0 = 關閉
    opt.MaxBodyBytes  = 8 * 1024;
    opt.OnReport      = report =>
    {
        // 自訂回報處理 — 例如轉送至 Sentry
        sentryClient.CaptureMessage($"CSP violation: {report.ViolatedDirective}");
    };
});
```

預設行為：違規以 Serilog Warning 寫成 `CspViolation Document=… Directive=… Blocked=… Source=…:…` — 既有 log pipeline 直接變成 CSP 違規可觀測介面，不必再接 Sentry / Datadog。

| 回應碼 | 條件 |
|------|------|
| 204 | 成功收下 |
| 400 | body 解析失敗 |
| 413 | body > MaxBodyBytes |
| 429 | 同 IP 超過 RatePerMinute |

### 10.14 SecureHeaders Overwrite（10.5.0+，#843）

10.4 的 `UseWtmSecureHeaders()` 採 first-writer-wins（避免覆蓋上游 reverse proxy 設定）；10.5 新增 `Overwrite = true` 給金融/醫療等需要保證 header 出現的場景：

```csharp
app.UseWtmSecureHeaders(opt =>
{
    opt.XFrameOptions = "DENY";         // 想保證一定是 DENY，不接受 proxy 改成 SAMEORIGIN
    opt.Overwrite     = true;
});
```

`Overwrite = true` **不會**繞過 HSTS 的 HTTPS gate（HSTS 仍需 `Request.IsHttps`），也不會啟用設成 `null` 的 header — 它只翻轉「先寫者贏 vs 後寫者贏」的順序。

### 10.15 [WtmIpAllowList] CIDR 白名單（10.5.0+）

把 controller / action 鎖在內網或特定 IP 段：

```csharp
[WtmIpAllowList("10.0.0.0/8", "192.168.0.0/16", "172.16.0.0/12")]
public class AdminApiController : BaseApiController { ... }

[HttpPost]
[WtmIpAllowList("203.0.113.0/24",
                FallbackStatusCode = 404)]   // 404 隱藏端點存在
public ActionResult<int> Webhook([FromBody] Payload p) { ... }
```

- 多個 CIDR OR 起來；單一 host 用 `/32`（IPv4）/ `/128`（IPv6）
- 由 `System.Net.IPNetwork` 解析，IPv4 / IPv6 同樣支援
- `IAsyncAuthorizationFilter` 自動發現，**無需 Program.cs 註冊**
- 拒絕日誌：Warning 級含 client IP + CIDR 列表
- CIDR 字串在 attribute 建構時驗證（typo 在啟動時就 throw，不會等到第一次請求）
- 容忍 `X-Forwarded-For: addr1, addr2` 的 comma list（取第一個）— 但 X-Forwarded-For 容易偽造，**正式公網部署必須結合 nginx/Cloudflare/ALB 邊緣防線**，本 attribute 是「縱深防禦的最後一層」

### 10.16 [WtmNoCache] / [WtmCacheControl] 回應快取控制（10.5.0+）

**`[WtmNoCache]`** — 一行修掉「登出後按上一頁仍看得到 profile」的經典問題：

```csharp
[WtmNoCache]
public class ProfileController : BaseController { ... }
```

自動寫出三段防禦：
- `Cache-Control: no-store, no-cache, must-revalidate, max-age=0, private`
- `Pragma: no-cache`（HTTP/1.0 fallback，企業內網 proxy 仍見得到）
- `Expires: 0`

**`[WtmCacheControl(...)]`** — 顯式宣告快取策略：

```csharp
[WtmCacheControl("public, max-age=300", Vary = "Accept-Encoding, Authorization")]
public ActionResult<List<Region>> Regions() => _regions;
```

- 字串原樣寫入（caller 自負 RFC 7234 正確性）
- `Vary` 寫到對應 header，告訴 CDN/proxy 用哪些 request header 當 cache key
- First-writer-wins：上游 reverse proxy 已寫 `Cache-Control` 就尊重不覆蓋
- ctor 對 null/whitespace 直接 throw，typo 啟動就抓

### 10.17 UseWtmMaintenanceMode 計畫性停機 kill-switch（10.5.0+）

正式運維場景：「半夜 2:00 要做 schema migration，10 分鐘窗口期間擋掉所有外部流量、留下 healthz 給 k8s probe、留下 admin plane 給操作者切回」。

```csharp
app.UseWtmMaintenanceMode(opt =>
{
    opt.Enabled              = false;     // 預設不啟，靠 IsEnabled 動態決定
    opt.IsEnabled            = ctx => featureFlags.IsOn("maintenance-mode");
    opt.AllowedPathPrefixes  = new[] { "/healthz", "/_admin", "/_framework", "/_js", "/_content" };
    opt.AllowedClientIps     = new[] { "10.0.0.0/8" };  // bastion / jump host
    opt.RetryAfterSeconds    = 60;
    opt.ContentType          = "application/problem+json";  // 也支援 text/html + HtmlBodyFactory
});
```

**運作：**
- 不在白名單 → 503 + `Retry-After: 60` + `application/problem+json` body
- `IsEnabled` 例外被吞並 fallback 到 `Enabled` 旗標 — flag 服務當機不會把 production 弄爛
- `Enabled = false` 且沒設 `IsEnabled` → 等同單一 bool 檢查，幾乎零成本

**位置：** 放 `UseRouting` 之後、auth/MVC 之前 — 503 在 expensive middleware 之前 short-circuit。

### 10.18 [WtmDeprecated] API 退役旗標（10.5.0+）

對舊 API 加上 IETF 標準的 `Deprecation: true` / RFC 8594 `Sunset` / RFC 8288 `Link`，告訴 client SDK 該升級了：

```csharp
[WtmDeprecated(
    Message = "Use /api/v2/orders instead",
    Sunset  = "2026-12-31",
    Link    = "</api/v2/orders>; rel=\"successor-version\"")]
public ActionResult<Order> GetOrderV1(Guid id) => _svc.Get(id);
```

每次被呼叫多帶三組 header + 一筆 `Information` 級 Serilog（`DeprecatedEndpointHit Path=… Method=… Message=… Since=…`）— 用日誌 pipeline 直接答得出「誰還在打 v1」。`LogHit = false` 可關。Sunset 字串不合法時不寫該 header，warning 只記一次。

### 10.19 UseWtmServerTiming W3C Server-Timing（10.5.0+）

每個非排除路徑回應掛 `Server-Timing: app;dur=<ms>` — Chrome DevTools → Network → Timing 自動秀後端 latency，**不必接 APM**：

```csharp
app.UseWtmServerTiming(opt =>
{
    opt.MetricName     = "app";
    opt.Description    = "WTM backend";
    opt.MinDurationMs  = 200;            // 0=每筆都送；200=只標慢的
    opt.PathExclusions = new[] { "/healthz", "/_framework", "/_js", "/_content" };
});
```

排除路徑 short-circuit 在 stopwatch 之前，**零成本**。多層 `Server-Timing` 用 W3C 規定的 comma list 共存（CDN/proxy 寫的不會被蓋）。Metric 名做 RFC 7230 token 過濾（含空白/控制字元就靜默不寫）；數字用 invariant culture 格式化（不會在 de-DE locale 變成 `12,34`）。

### 10.20 IWtmFeatureFlags 功能旗標（10.5.0+）

不必引 `Microsoft.FeatureManagement` 或 LaunchDarkly：

```csharp
services.AddWtmFeatureFlags(opt =>
{
    opt.Defaults["new-checkout"] = false;
    opt.Defaults["beta-search"]  = true;

    // 自訂 resolver — 灰度 / 租戶 / user-id rollout / 外部 SDK adapter
    opt.Resolver = (httpCtx, name) =>
    {
        var tenant = httpCtx?.User.FindFirst("tenant")?.Value;
        if (tenant == "early-access") return true;
        return null;                      // null 代表「沒意見、繼續往下找」
    };
});
```

**解析順序**（決定論、可單元測）：
1. `Resolver` delegate（per-request 動態，return null = 沒意見）
2. `IConfiguration` 的 `FeatureFlags:<name>`（每次都 re-read，`appsettings.json` 改了不必重啟）
3. `Options.Defaults`（大小寫不敏感）
4. `false` — 沒宣告就視為關閉（fail-closed，避免忘記註冊就洩漏 pre-release endpoint）

**用法：** 注入 `IWtmFeatureFlags`，或 attribute 守門：

```csharp
[WtmFeatureGate("new-checkout")]                       // 預設 disabled→404（隱藏存在）
public ActionResult<Order> CheckoutV2([FromBody] CartDto c) { ... }

[WtmFeatureGate("beta-search", FallbackStatusCode = 503)]
public IActionResult Search() { ... }
```

`Snapshot()` 列出所有已知旗標 + 當前值 — admin diagnostic 頁直接吃。Resolver 例外被 catch + Warning，**flag 服務 outage 不會把 production 拖垮**。

### 10.21 [WtmIdempotent] + UseWtmIdempotency 重試安全（10.5.0+）

讓 mutating endpoint 有 retry-safe 能力，不必在 action 內手動防重複提交。遵循 Stripe / PayPal / AWS 的 `Idempotency-Key` 慣例（draft-ietf-httpapi-idempotency-key-header） — 多數現代 API SDK 已預設帶這個 header。

```csharp
services.AddMemoryCache();
app.UseWtmIdempotency(opt =>
{
    opt.HeaderName            = "Idempotency-Key";
    opt.DefaultWindowSeconds  = 300;
    opt.MaxKeyLength          = 128;
    opt.MaxCachedBodyBytes    = 1 * 1024 * 1024;
});
```

```csharp
[HttpPost]
[WtmIdempotent(WindowSeconds = 600, RequireKey = true)]
public ActionResult<Order> Place([FromBody] PlaceOrderDto dto) => _svc.Place(dto);
```

- 同一個 `Idempotency-Key` 在 600 秒內重打：原樣回放上次 2xx 回應，並加 `Idempotency-Replay: true` header 讓 client/test 區分 cache hit
- Cache key = `method + path + key`，被偷的 key 不能跨 endpoint 重放
- 只快取 2xx — 4xx/5xx 直通（暫時失敗不會毒化 slot）
- 只回放 `Content-Type`，**不回放 `Set-Cookie`** — 防止前一次的 session 被別人撿到
- Key 只允許 `[A-Za-z0-9\-_.:]`（拒控制字元 / UTF-8 / log injection）；長度 > 128 / `RequireKey = true` 卻沒帶 → 400 + `application/problem+json`
- `GET` / `HEAD` 故意排除（已 RFC-9110 safe，快取會掩蓋 bug）
- backing store 是 `IMemoryCache`（in-process）— 多實例部署需要 sticky LB 或自己包 distributed wrapper

### 10.22 UseWtmETag 條件請求頻寬節省（10.5.0+）

對 GET/HEAD 計算 SHA-256 → base64url 截 22 字元（132 bits 熵）作 ETag，client 帶對的 `If-None-Match` 就回 `304 Not Modified`（無 body）。**省的是頻寬不是 server 計算** — 但 dashboard / reference data / 後台 list 通常變動少，每筆 304 從幾 KB 降到 ~300 bytes header。

```csharp
app.UseWtmETag(opt =>
{
    opt.EligibleMethods    = new[] { HttpMethods.Get, HttpMethods.Head };  // POST/PUT 故意不支援
    opt.MaxBufferedBytes   = 2 * 1024 * 1024;     // 超過直通不算 ETag，避免變記憶體炸彈
    opt.EmitWeakETag       = false;               // 啟動 → 用 W/"..."
    opt.PathExclusions     = new[] { "/healthz", "/_framework", "/_js", "/_content" };
});
```

- RFC 9110 §13.1.2 / §8.8.3.2 比對：通配 `*`、comma list、`W/` 前綴弱比對都支援
- 上游已寫 ETag → first-writer-wins
- 放在 `UseRouting` 之後、compression 之前 — 雜湊算在未壓縮表示上

### 10.23 UseWtmRequestTimeouts per-request 截止期（10.5.0+）

10.4 的 `UseWtmSlowRequestLogging`「**事後**」抓慢，10.5 的 `UseWtmRequestTimeouts`「**進行中**」直接砍。把 `IHttpRequestLifetimeFeature` 換成 deadline-linked `CancellationToken` — EF Core / `HttpClient` / `Task.Delay(_, ct)` 等任何觀察 `HttpContext.RequestAborted` 的 framework 都會合作式取消：

```csharp
app.UseWtmRequestTimeouts(opt =>
{
    opt.DefaultTimeoutMs = 30_000;
    opt.PathOverrides = new Dictionary<string, int>
    {
        ["/api/export"]         = 5  * 60_000,    // 報表慢些
        ["/api/admin/migrate"]  = 10 * 60_000,    // schema 維護更慢
        ["/sse"]                = 0,              // SSE 串流，opt-out
    };
    opt.PathExclusions = new[] { "/healthz", "/_framework", "/_js", "/_content" };
});
```

**Deadline trip 行為：**
- response 還沒開始送 → 寫 `504 Gateway Timeout` + `application/problem+json`（含 `timeoutMs` / `traceId`）+ Warning log（`RequestTimeout Path={Path} Method={Method} TimeoutMs={Timeout}`）
- response 已經開始（chunked / SSE）→ abort connection（無法回送 504 over partial body）
- handler 吞掉 `OperationCanceledException` 還回 200 → middleware 也會把空 200 改成 504（行為不端的 handler 不能藏 deadline）
- 區分「deadline 砍 handler」vs「client 先放棄」（兩者都是 `OperationCanceledException`），只前者產 504

### 10.24 WtmDataSeeder 冪等資料填充（10.5.0+）

```csharp
await WtmDataSeeder.SeedAsync(
    dc,
    new[]
    {
        new Region { Code = "TW", Name = "Taiwan"  },
        new Region { Code = "JP", Name = "Japan"   },
        new Region { Code = "US", Name = "America" },
    },
    x => x.Code);                              // 業務鍵
```

回傳 `WtmSeedResult(Added, Skipped)`。重跑同一份 seed — 不論放在 `Program.cs`、test `[TestInitialize]`、migration job 還是 demo 還原 hook — 第二次起 0 inserts，所以「每次啟動 idempotent seed」變成一行。

**內部設計：**
- 業務鍵存在性檢查 = 一次 `SELECT … WHERE Key IN (…)` 而非 per-item EXISTS — 數百筆 fixture 只一次 round-trip + 一次 `SaveChangesAsync`
- 完全沒新增才不呼叫 `SaveChangesAsync` — pure-skip re-seed 不會誤觸 audit interceptor / SaveChanges filter
- 輸入 array 內重複的鍵自動取第一筆（cut-and-paste fixture 不會炸）
- 純 insert，不更新既有 row（要 upsert 自己包）

### 10.25 安全相關深入閱讀

本章節涵蓋 WTM 框架內建的安全機制。實際 production 部署還需要關注：

- [`docs/production-readiness.md`](./production-readiness.md) — production 場景適用矩陣、補強清單；含「測試覆蓋僅 ~20%」「單人維護」「NPOI 漏洞透過 pin 緩解」等要誠實面對的弱點
- [`docs/dependency-management.md`](./dependency-management.md) — 套件版本政策、**NU1510 雙意義警告**、NPOI → System.Security.Cryptography.Xml security pin 與移除 PackageReference 強制 SOP（誤刪會引入 13+ 專案 high-severity 漏洞）
- [`docs/ci-operations.md`](./ci-operations.md) — CI 工作流與漏洞掃描 gate 配置

### 10.9 10.7.0 安全強化

10.7.0 修補了一批授權/隔離/XSS/SSRF 缺陷（多數 additive，兩項行為變更已標註）：

- **MVC controller 授權**：`UpdateModelProperty` 改走 CRUD VM 的 `DoEdit()`（套用驗證 + duplicate-check），並提供可覆寫的 `CanEditProperty(entity, propertyName)` row-level 授權 hook；`GetPagingData`/`GetExportExcel`/`GetExcelTemplate`/`Upload` 會驗證 client 傳入的 connection-string key 是否屬於 `Configs.Connections`，未知 key 一律拒絕；`Selector` endpoint 由 `[Public]` 改為 `[AllRights]`（**行為變更**：若需未驗證存取，設 `AllowUnauthenticatedSelector = true`）；`IsQuickDebug = true` 在非 Development 環境啟動時直接拋例外（它會繞過所有 RBAC）。
- **租戶隔離 + RBAC 稽核**：`SetDuplicatedCheck` 對 `ITenant` 實體把唯一性檢查限縮到當前租戶（soft-delete 可見性不變；**行為變更**：多租戶 dup-check 不再跨租戶）；`FileUploadOptions.EnforceTenantFileScope`（opt-in，預設 `false`）對以 ID 查檔強制租戶範圍；RBAC 實體（`FrameworkUser`/`FrameworkRole`/`FunctionPrivilege`/`DataPrivilege`/`FrameworkMenu` 等）加 `[AuditChanges]`，權限變更會寫 ChangeLog。
- **Grid/TagHelper XSS**：grid color function（`BackGroundFunc`/`ForeGroundFunc`）值以嚴格 hex/named-color allowlist 驗證並 HTML 編碼；`CheckBox`/`Radio`/`Hidden`/`Form` tag helper 與 `LayuiUIService.Make*` cell renderer 對動態值 `HtmlEncode`。Grid row-action 按鈕（framework 產生的 HTML）正常渲染，user/DB cell 資料維持跳脫（#108 guard 保留）。
- **Dashboard widget 強化**：REST widget HTTP method 限 GET/POST allowlist；header 改用驗證式 `Add`（擋 CRLF）；dashboard filter operator 進 expression tree 前先過 allowlist；`RestWidgetDataSourceOptions.AllowedPorts`（預設 80/443/8080/8443）緩解 SSRF port 探測；widget title 長度上限。
- **VM 工廠型別守衛**：`WtmVmFactory.CreateVM` 在呼叫任何建構式之前就拒絕非 `BaseVM` 型別。

### 10.10 10.8.0 安全強化

10.8.0 的 Dashboard/ETL 新功能皆內建安全防護（全 opt-in，預設不啟用）：

- **Dashboard 設計器授權**：`_DashboardDesignerController` 的 `vm-meta` / `preview` 兩個 endpoint 皆 `[AllRights]`；tenant 取自伺服器端 `LoginUserInfo.TenantCode`（不可由 client 偽造）；`vm-meta` 只透過 `AnalysisVmRegistry.Resolve()` 解析（registry 白名單，非任意型別載入）；`preview` 在打資料前強制 `DashboardOptions.AllowedWidgetTypes` + `FilterConfig.AllowedOps`，臨時預覽 dashboard 蓋伺服器端 `Owner`/`TenantId` 且 `finally` 必刪（走 `DeleteAsync(id, tenantId)` 租戶範圍）。前端零 user-data `innerHTML`（DOM-method 建構）。
- **共用 webhook sink SSRF 強化**：`IWtmWebhookSink`（钉钉/企微/飞书/Slack/Teams）的出站連線 DNS-pinned、僅 HTTPS、封鎖私有 IP / IMDS（169.254.169.254）、停用自動重導向；金鑰絕不寫入 log（钉钉採 HMAC 簽名）。
- **REST ETL 來源 SSRF 強化**：`RestEtlSource` 沿用相同的出站防護（DNS-pin / HTTPS / 私有 IP 封鎖）。
- **ETL governance 錯誤脫敏**：dead-letter / lineage 持久化前，原始錯誤文字經 sanitize（避免連線字串/憑證外洩進 DB）。

---

## 11. 多租戶

WTM 的多租戶透過 EF Core Global Query Filter 在資料庫查詢層面自動隔離，開發者幾乎不需要額外寫程式碼。

### 11.1 啟用方式

**步驟 1：Model 實作 `ITenant`**
```csharp
public class Employee : BasePoco, ITenant
{
    public string? TenantCode { get; set; }

    [Display(Name = "姓名")]
    public string Name { get; set; } = "";
    // ...
}
```

**步驟 2：確認 DataContext 繼承 FrameworkContext**

`FrameworkContext.OnModelCreating` 會自動掃描所有實作 `ITenant` 的 Model，加入 Global Query Filter。不需要手動設定。

**步驟 3：設定租戶資料**

在 Admin 後台的「租戶管理」建立租戶：
```
FrameworkTenant:
  - TCode: "ACME",   TName: "ACME 公司"
  - TCode: "STARK",  TName: "Stark 工業"
```

使用者在 `FrameworkUser.TenantCode` 欄位設定所屬租戶。

### 11.2 運作機制（Global Query Filter）

`FrameworkContext.OnModelCreating` 中的核心邏輯：

```csharp
// 虛擬碼 — 實際實作透過 Expression Tree 動態建構
foreach (var entityType in allModels)
{
    if (typeof(ITenant).IsAssignableFrom(entityType))
    {
        // 自動加入：WHERE TenantCode = DataContext.TenantCode
        builder.Entity(entityType)
            .HasQueryFilter(e => e.TenantCode == this.TenantCode);
    }

    if (typeof(IPersistPoco).IsAssignableFrom(entityType))
    {
        // 自動加入：WHERE IsValid = true（軟刪除過濾）
        builder.Entity(entityType)
            .HasQueryFilter(e => e.IsValid == true);
    }
}
```

**效果：** 所有 LINQ 查詢自動帶上租戶過濾，開發者寫 `DC.Set<Employee>().ToList()` 就只會拿到當前租戶的資料。

### 11.3 使用場景

#### 場景 1：SaaS 多租戶應用

```csharp
// 情境：兩家公司共用同一套系統
// ACME 的使用者登入後，TenantCode = "ACME"
// 以下查詢自動只返回 ACME 的員工：
var employees = DC.Set<Employee>().ToList();
// SQL: SELECT * FROM Employee WHERE TenantCode = 'ACME' AND IsValid = 1

// STARK 的使用者登入後，TenantCode = "STARK"
// 同樣的程式碼，自動只返回 STARK 的員工
```

#### 場景 2：跨租戶查詢（管理後台）

```csharp
// 系統管理員需要看到所有租戶的資料（例如：統計報表）
// 必須明確使用 IgnoreQueryFilters()
var allEmployees = DC.Set<Employee>()
    .IgnoreQueryFilters()  // 移除 TenantCode + IsValid 過濾
    .Where(x => x.IsValid)  // 手動加回軟刪除過濾（需要的話）
    .ToList();

// ⚠️ 注意：IgnoreQueryFilters 會同時移除所有 Global Query Filter
//          包括 ITenant 和 IPersistPoco 的，需要手動補回需要的條件
```

#### 場景 3：租戶切換（超級管理員）

```
POST /_Framework/SetTenant?tenant=ACME
→ 更新 Session + ClaimsPrincipal 的 TenantCode
→ 後續所有查詢自動切換到 ACME 的資料
```

### 11.4 多租戶注意事項

| 事項 | 說明 |
|------|------|
| 新增資料 | `TenantCode` 由 `FrameworkFilter` 在 `DoAdd()` 前自動設定，不需手動填 |
| 唯一索引 | 考慮是否需要加上 `TenantCode`（例如：員工工號在每個租戶內唯一） |
| 資料遷移 | 遷移既有單租戶系統時，需要先為現有資料填入 TenantCode |
| Dashboard | 自動隔離 — 每個租戶只看到自己的 Dashboard |
| Analysis | `GetSearchQuery()` 的租戶過濾自動生效 |
| Lookup Cache | 快取 key 包含 tenantId，每個租戶獨立快取 |

---

## 12. Lookup Cache

Lookup Cache 專為「少量、低頻變動、頻繁讀取」的資料設計（如部門、職稱、狀態碼），一行 Attribute 就能啟用。

### 12.1 啟用方式

```csharp
// 加上 [CacheLookup] 即可
[CacheLookup(WarmOnStartup = true)]  // WarmOnStartup: 應用啟動時預載
public class Department : BasePoco
{
    [Display(Name = "部門名稱")]
    public string Name { get; set; } = "";

    [Display(Name = "部門代碼")]
    public string Code { get; set; } = "";
}
```

### 12.2 使用方式

```csharp
// 方式 1：透過 DI 注入
public class MyService
{
    private readonly ILookupCacheService _cache;
    public MyService(ILookupCacheService cache) => _cache = cache;

    public string GetDepartmentName(Guid id)
    {
        var dept = _cache.Get<Department>(id);
        return dept?.Name ?? "未知部門";
    }

    public IReadOnlyList<Department> GetAllDepartments()
    {
        return _cache.GetAll<Department>();
    }
}

// 方式 2：在 ViewModel 中使用（透過 Wtm.ServiceProvider）
protected override void InitVM()
{
    var cache = Wtm!.ServiceProvider.GetRequiredService<ILookupCacheService>();
    var departments = cache.GetAll<Department>();
    AllDepartments = departments
        .Select(d => new ComboSelectListItem { Value = d.ID.ToString(), Text = d.Name })
        .ToList();
}

// 方式 3：Async 版本（推薦用於 API Controller）
public async Task<IActionResult> GetDepartments()
{
    var cache = HttpContext.RequestServices.GetRequiredService<ILookupCacheService>();
    var departments = await cache.GetAllAsync<Department>(DC as DbContext);
    return Ok(departments);
}
```

### 12.3 手動失效

```csharp
// 單一類型失效
cache.Invalidate<Department>();

// 指定租戶失效
cache.Invalidate<Department>(tenantId: "ACME");

// 類型級失效（所有租戶）
cache.InvalidateType(typeof(Department));
```

### 12.4 Stampede Protection（防快取雪崩）

當快取過期時，如果 100 個請求同時到達，不做保護的話會有 100 次 DB 查詢。WTM 的 Lookup Cache 使用 **Per-Key SemaphoreSlim + Double-Check Locking**：

```
100 個請求同時到達，快取為空：
  → 請求 1：取得 SemaphoreSlim 鎖 → 查 DB → 寫入快取 → 釋放鎖
  → 請求 2~100：等待鎖（最多 10 秒）→ 取得鎖後 double-check → 快取已有 → 直接返回

結果：只有 1 次 DB 查詢，其餘 99 次直接用快取
```

**快取 Key 結構：** `wtm:lookup:{Type.FullName}:{tenantId}`
- 例如：`wtm:lookup:MyApp.Models.Department:ACME`
- 無租戶時 tenantId = `"_"`

### 12.5 自動失效機制

```
DC.Set<Department>().Add(new Department { ... });
DC.SaveChanges();
  → FrameworkContext.SaveChanges() 內部：
    → 掃描 ChangeTracker 中的變更實體
    → 是否有 [CacheLookup] 標記？
    → 是 → 呼叫 InvalidateType(typeof(Department))
    → 下次 GetAll<Department>() 重新從 DB 載入
```

**觸發失效的操作：** Add、Update、Delete（任何透過 `SaveChanges()` 的變更）。

### 12.6 多資料庫支援

每個連線字串（`CS`）獨立維護快取。如果你的應用連接了多個資料庫，同一個 `Department` 類型在不同資料庫中有不同的快取：

```csharp
// 從 default DB 取得
var depts = cache.GetAll<Department>(defaultDc);

// 從 secondary DB 取得（不同快取）
var depts2 = cache.GetAll<Department>(secondaryDc);
```

**`ConnectionKey` 與啟動預熱（10.17.0 修復，#756）**：`[CacheLookup(ConnectionKey = "orss")]` 可將型別綁定到非 default 連線——runtime 的 `GetLookup`/`GetLookupAsync`/`RefreshLookupAsync` 一直都會依此路由到正確 DB，但 10.17.0 之前**啟動預熱不會**：warmup 一律打 default 連線，非 default 型別每次開機 warm-fail（log noise）；更糟的是若 default DB 恰好有同名表，會把**錯誤資料庫**的列寫進 runtime 讀的同一把 cache key，毒化整個 TTL。10.17.0 起 warmup 依 `ConnectionKey` 分組、與 runtime 相同路由；unknown key / 停用連線 / 無 `WTMContext` 的 host 等一律降級為 logged skip（絕不讓例外逃出 BackgroundService）。曾為此 bug 加 `WarmOnStartup = false` opt-out 的下游（如 BMS `Holiday_Orss`）升級後可移除。

### 12.7 使用場景

#### 場景 1：下拉選單加速

```csharp
// 傳統做法：每次開啟表單都查 DB
AllDepartments = DC.Set<Department>().GetSelectListItems(Wtm!, x => x.Name);

// 使用 Lookup Cache：從記憶體取，零 DB 查詢
var cache = Wtm!.ServiceProvider.GetRequiredService<ILookupCacheService>();
AllDepartments = cache.GetAll<Department>()
    .Select(d => new ComboSelectListItem { Value = d.ID.ToString(), Text = d.Name })
    .ToList();
```

#### 場景 2：資料驗證（不查 DB）

```csharp
public override void Validate()
{
    var cache = Wtm!.ServiceProvider.GetRequiredService<ILookupCacheService>();
    var dept = cache.Get<Department>(Entity.DepartmentId);
    if (dept == null)
        MSD!.AddModelError("Entity.DepartmentId", "部門不存在");
}
```

#### 場景 3：適合 vs 不適合快取的資料

| 適合 | 不適合 |
|------|--------|
| 部門（幾十筆，少變動） | 訂單（百萬筆，頻繁變動） |
| 職稱、等級 | 使用者列表（可能很大） |
| 狀態碼、類別 | 任何超過 1000 筆的表 |
| 設定參數 | 含有大型欄位（BLOB）的表 |

**經驗法則：** 資料量 < 1000 筆 且 變動頻率 < 每分鐘 1 次 → 適合用 Lookup Cache。

### 12.8 Stats / Health API（10.4.0+）

`ILookupCacheService.GetStats()` 公開內部統計，讓 admin / SRE 判斷 cache 是否 warm、invalidate 是否生效、hit rate 是否合理。

```csharp
// 取得所有已註冊型別的統計
var all = _lookupCacheService.GetStats();
foreach (var s in all)
{
    Console.WriteLine($"{s.EntityTypeName}: hits={s.Hits} misses={s.Misses} " +
        $"cached-keys={s.CurrentlyCachedTenantKeys} " +
        $"last-warm={s.LastWarmAt:o} last-invalidate={s.LastInvalidatedAt:o}");
}

// 取得單一型別
var cityStats = _lookupCacheService.GetStats(typeof(CityCode));
if (cityStats == null)
{
    // 該型別未標 [CacheLookup]
}
```

**`LookupCacheStats` 欄位：**

| 欄位 | 意義 |
|------|------|
| `EntityTypeName` | 型別 FullName（string） |
| `Hits` | Cache-hit read 次數（fast-path） |
| `Misses` | Cache-miss read 次數（DB load path，含首次載入與 TTL 到期後重載） |
| `InvalidateCount` | `Invalidate<T>` / `InvalidateType` 呼叫次數 |
| `CurrentlyCachedTenantKeys` | 目前記憶體中持有的 tenant key 數；多租戶場景每 tenant 獨立 key |
| `LastAccessAt` | 最後一次 `GetAll`（hit or miss）UTC 時間；從未存取則為 null |
| `LastWarmAt` | 最後一次 SetCache（`WarmOnStartup` 啟動載入 或 `RefreshAsync` 完成）UTC 時間 |
| `LastInvalidatedAt` | 最後一次 invalidate UTC 時間 |
| `TtlMinutesConfigured` | 對應 `CacheLookupAttribute.TtlMinutes` |
| `WarmOnStartup` | 對應 `CacheLookupAttribute.WarmOnStartup` |

**範疇與限制：**
- 計數器為 **process-lifetime** — 重啟歸零，非持久化
- 統計查詢本身（`GetStats` 呼叫）**不**計入 Hits / Misses
- 聚合粒度為 per-type（跨 tenant 合併）；tenant 分層維度刻意不做（記憶體膨脹 vs 效益不符）
- 未暴露 Prometheus / OpenTelemetry exporter — app 可自行在 `/metrics` endpoint 或 background hosted service 取 stats 轉換為想要的監控格式
- 框架不內建 HTTP endpoint；app 可寫 5 行 controller 包：

```csharp
[ApiController, Route("admin/cache"), Authorize(Roles = "Admin")]
public class CacheStatsController : ControllerBase
{
    private readonly ILookupCacheService _cache;
    public CacheStatsController(ILookupCacheService cache) => _cache = cache;

    [HttpGet("stats")]
    public ActionResult<IReadOnlyList<LookupCacheStats>> Stats() => Ok(_cache.GetStats());
}
```

**運維場景範例：**

```
scenario：用戶回報「我改了資料，前端還看到舊值」
  1. admin hit /admin/cache/stats
  2. 看對應 type 的 LastInvalidatedAt：
     - null / 很久以前 → cache 沒失效，確認 Invalidate 呼叫邏輯
     - 剛剛 → cache 已失效，問題在 DB replication / app 層
  3. 看 CurrentlyCachedTenantKeys：失效後應為 0；大於 0 則是其他 tenant 的 key（不影響當前用戶）

scenario：部署後啟動 warmup 是否成功
  1. admin hit /admin/cache/stats
  2. 看 WarmOnStartup=true 的 type：LastWarmAt 應為啟動時間（幾分鐘內）
     - null → warmup service 沒跑 或 DB 查詢拋例外；查 Serilog
     - 遠早於啟動 → warmup 失敗且 fallback 到 lazy load
```

---

## 13. 測試指南

### 13.1 Mock 基礎設施

```csharp
// 建立 WTMContext（不需資料庫）
var wtm = MockWtmContext.CreateWtmContext();

// 建立 WTMContext（帶 DataContext）
var wtm = MockWtmContext.CreateWtmContext(dataContext, "testuser");

// 建立 MVC Controller
var controller = MockController.CreateController<EmployeeController>(dataContext, "user");

// 建立 API Controller
var controller = MockController.CreateApi<EmployeeApiController>(dataContext, "user");
```

### 13.2 Controller 測試範例

```csharp
[TestClass]
public class EmployeeControllerTest
{
    private EmployeeController _controller = null!;
    private string _seed = null!;

    [TestInitialize]
    public void Setup()
    {
        _seed = Guid.NewGuid().ToString();
        _controller = MockController.CreateController<EmployeeController>(
            new DataContext(_seed, DBTypeEnum.Memory), "testuser");
    }

    [TestMethod]
    public void Create_ValidEmployee_SucceedsAndPersists()
    {
        var vm = _controller.Wtm.CreateVM<EmployeeVM>();
        vm.Entity = new Employee { Name = "張三", Salary = 50000 };

        _controller.Create(vm);

        using var ctx = new DataContext(_seed, DBTypeEnum.Memory);
        var employee = ctx.Set<Employee>().FirstOrDefault();
        Assert.IsNotNull(employee);
        Assert.AreEqual("張三", employee.Name);
    }
}
```

### 13.3 Analysis 測試範例（靜態資料注入）

```csharp
[EnableAnalysis]
private class TestListVM : BasePagedListVM<SaleRecord, BaseSearcher>
{
    public override IOrderedQueryable<SaleRecord> GetSearchQuery()
        => _testData.AsQueryable().OrderByDescending(x => x.ID);
}

private static IList<SaleRecord> _testData = new List<SaleRecord>();

[TestInitialize]
public void Setup()
{
    _testData = new List<SaleRecord>
    {
        new() { Region = "北區", Amount = 100 },
        new() { Region = "南區", Amount = 200 },
    };

    var registry = new AnalysisVmRegistry();
    registry.Build(new[] { typeof(MyTest).Assembly });

    _controller = new _AnalysisController(registry);
    _controller.Wtm = MockWtmContext.CreateWtmContext();
}
```

### 13.4 SQLite In-Memory 測試

```csharp
private SqliteConnection _conn = null!;
private DataContext _ctx = null!;

[TestInitialize]
public void Setup()
{
    _conn = new SqliteConnection("DataSource=:memory:");
    _conn.Open();  // 必須保持開啟

    var opts = new DbContextOptionsBuilder<DataContext>()
        .UseSqlite(_conn).Options;
    _ctx = new DataContext(opts);
    _ctx.Database.EnsureCreated();
}

[TestCleanup]
public void Cleanup()
{
    _ctx.Dispose();
    _conn.Dispose();  // 必須在 context 之後 dispose
}
```

### 13.5 JS 測試（Jest）

```javascript
const vm = require('vm');
const fs = require('fs');

function makeEnv() {
    const ctx = vm.createContext({
        window: {},
        document: { getElementById: jest.fn(), querySelectorAll: jest.fn(() => []) },
        fetch: jest.fn(),
    });
    const src = fs.readFileSync('src/.../framework_analysis.js', 'utf8');
    new vm.Script(src).runInContext(ctx);
    return ctx.wtmAnalysis;
}

test('detectChartType returns bar for single dim + single measure', () => {
    const wa = makeEnv();
    expect(wa.detectChartType(['Region'], ['Amount'])).toBe('bar');
});
```

**重要：** 每個 DOM 測試必須用 `makeEnv()` 建立新 context，因為 `_state` 是 IIFE 模組級變數。

---

## 14. TimeProvider 時間抽象

自 10.0.1 起，WTM 全面採用 .NET 8+ 的 `TimeProvider` 抽象取代直接呼叫 `DateTime.Now` / `DateTime.UtcNow`。這使得所有時間相關邏輯皆可在測試中精確控制。

### 14.1 取得當前時間

在 ViewModel 或 Service 中，透過 `WTMContext.TimeProvider` 取得：

```csharp
// 取代 DateTime.Now
var now = Wtm.TimeProvider.GetLocalNow();

// 取代 DateTime.UtcNow
var utcNow = Wtm.TimeProvider.GetUtcNow();
```

### 14.2 受影響範圍

以下區域的 `DateTime.Now` / `DateTime.UtcNow` 已全部遷移至 `TimeProvider`：

- **WTMContext** — `TimeProvider` 屬性，透過 DI 注入
- **TokenService** — JWT access/refresh token 過期計算
- **DataContext** — `CreateTime`、`UpdateTime` 自動填充
- **Dashboard 模組** — 日期範圍計算
- **ETL 模組** — 排程與執行時間
- **DateRange** — `Today()`、`ThisMonth()` 等 factory methods 接受 `TimeProvider` 參數

### 14.3 測試中使用 FakeTimeProvider

```csharp
using Microsoft.Extensions.Time.Testing;

var fakeTime = new FakeTimeProvider(
    new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.FromHours(8)));

// 注入到 WTMContext
var wtm = MockWtmContext.CreateWtmContext(timeProvider: fakeTime);

// 推進時間
fakeTime.Advance(TimeSpan.FromHours(2));
```

`FakeTimeProvider` 來自 `Microsoft.Extensions.TimeProvider.Testing` 套件，測試專案已包含此依賴。

---

## 14A. 健康檢查（10.4.0+）

WTM 提供 ASP.NET Core Health Check 的 wrapper，預設內建 liveness 探針，可擴充至 readiness 群組並用於 Kubernetes / load balancer 健康狀態判斷。

### 14A.1 最小設定

```csharp
// Program.cs
services.AddWtmHealthChecks();
app.UseWtmHealthChecks();  // /healthz（liveness）+ /healthz/ready（readiness）
```

- `/healthz` → 固定 200（只要 process 還活著）— K8s liveness 用
- `/healthz/ready` → 跑所有註冊 check（readiness 判斷）

### 14A.2 Framework-owned checks（#836）

內建 `WtmDataContextHealthCheck`：用 `IDataContext.Database.CanConnectAsync` 檢 DB，預設 2 秒 timeout。

```csharp
services.AddWtmHealthChecks(checks =>
    checks.AddWtmDataContextCheck(
        name: "datacontext",         // default
        timeout: TimeSpan.FromSeconds(2),
        tags: new[] { "ready" })     // default tag
);
```

回應（`/healthz/ready`）：

```json
{
  "status": "Healthy",
  "totalDurationMs": 42,
  "checks": [
    { "name": "self", "status": "Healthy", "durationMs": 0, "tags": ["live"] },
    { "name": "datacontext", "status": "Healthy", "durationMs": 12,
      "description": "DataContext connection OK.", "tags": ["ready"],
      "data": { "dbType": "SQLite" } }
  ]
}
```

Unhealthy 時加 `exception` 欄位（sanitize 過的 message，不含 stack trace）。

> **10.16.0 修復（#741，#727 殘留）**：10.16.0 之前 `WtmDataContextHealthCheck` 在真實部署中解析到的是 `NullContext` DI 佔位符，**從未真的探過 DB**——永遠回報 "Healthy (skipped)"。現在它優先透過 DI 注入的 `WTMContext.CreateDC()`（與框架其他部分同一條 connection-string/tenant-aware 工廠）對真 DB 跑 `CanConnectAsync`。**Operator 注意**：過去恆綠的 `/ready` 從此在 DB 不可達或 `default` 連線停用時會真的轉 **Unhealthy**——這是修復的本意；多租戶且刻意停用 `default` 連線的應用應改 scope 或不掛這個 opt-in check。

### 14A.3 JSON 回應格式

`useJsonResponse` 預設 `false`（保留原本 plain-text 行為避免 silent breaking change）。要換成結構化 JSON 請明確 opt-in：

```csharp
app.UseWtmHealthChecks(useJsonResponse: true);  // application/json + per-check detail
```

打開後 Kubernetes / Prometheus / Datadog 可直接 scrape 每個 check 的 `durationMs` / `description` / `exception`。

關鍵 API：`WtmHealthCheckResponseWriter.WriteJsonResponse` 是寫入 JSON payload 的靜態集回。

---

## 15. Integration Tests 整合測試

WTM 支援對真實資料庫執行整合測試，以驗證 EF Core 查詢、Migration 和 DB-specific 行為。

### 15.1 執行整合測試

**選項 1：Docker（推薦，10.3.0+）**

專案根目錄備有 `docker-compose.yml`，一鍵啟動與 CI 相同版本的 SQL Server 2022：

```bash
# 啟動 SQL Server（同 CI 使用的映像）
docker compose up -d mssql

# 等 ~15s 冷啟動完成，執行整合測試
dotnet test test/WalkingTec.Mvvm.Integration.Test -c Release

# 用完清除
docker compose down -v
```

**選項 2：連線現有 SQL Server**

```bash
export WTM_TEST_MSSQL="Server=localhost;Database=WtmTest;User Id=sa;Password=YourPassword;TrustServerCertificate=True"
dotnet test --filter "TestCategory=Integration" -c Release
```

**選項 3：跳過整合測試**

```bash
dotnet test WalkingTec.Mvvm.sln --filter "TestCategory!=Integration" -c Release
# 或使用 CI 專用的 solution filter
dotnet test ci.slnf -c Release
```

**無 SQL 時的行為（10.3.0+）**：若本機未跑 SQL Server，`IntegrationTestBase.EnsureSqlServerAvailable()` 會透過 `Lazy<T>` 快取的 2 秒 TCP 探測偵測，每個整合測試改報 `AssertInconclusive`（黃色略過）而非 `Failed`（紅色），並附多行設定指引訊息。CI 上的 service container 能連 → 探測回傳 null → 正常執行。

### 15.2 撰寫整合測試

```csharp
[TestClass]
[TestCategory("Integration")]
public class StudentIntegrationTests
{
    [TestMethod]
    public void Create_and_query_student()
    {
        var connStr = Environment.GetEnvironmentVariable("WTM_TEST_MSSQL");
        if (string.IsNullOrEmpty(connStr))
        {
            Assert.Inconclusive("WTM_TEST_MSSQL not set — skipping integration test");
        }

        // 使用真實 DB 執行測試...
    }
}
```

### 15.3 CI 整合

Gitea Actions 中，整合測試需在 `services` 區塊啟動資料庫容器，並將連線字串透過 `env` 傳入。預設 CI 只執行單元測試（不含 `TestCategory=Integration`）。

---

## 16. 代碼生成器

代碼生成器是 WTM 的核心生產力工具 — 選擇 Model，點幾下按鈕，就能生成完整的 CRUD 功能（Controller + ViewModel + View），省去 80% 的重複編碼工作。

### 16.1 存取方式

```
瀏覽器開啟：http://localhost:5000/_CodeGen/Index
```

- 僅在 `Debug` 模式可用（標記 `[DebugOnly]`）
- 生產環境自動隱藏，無安全風險

### 16.2 使用步驟

**步驟 1：準備 Model**

確保 Model 滿足以下條件：
```csharp
// ✅ 繼承 TopBasePoco（或其子類 BasePoco、PersistPoco）
public class Product : BasePoco
{
    [Display(Name = "商品名稱")]
    [Required]
    [StringLength(100)]
    public string Name { get; set; } = "";

    [Display(Name = "價格")]
    public decimal Price { get; set; }

    [Display(Name = "分類")]
    public Guid? CategoryId { get; set; }
    public Category? Category { get; set; }

    [Display(Name = "上架")]
    public bool IsActive { get; set; } = true;
}

// ✅ 在 DataContext 中註冊 DbSet
public class DataContext : FrameworkContext
{
    public DbSet<Product> Products { get; set; } = null!;
    public DbSet<Category> Categories { get; set; } = null!;
}
```

**步驟 2：開啟代碼生成器頁面**

1. 在下拉選單中選擇 Model（如 `Product`）
2. 系統自動掃描 Model 的所有屬性

**步驟 3：設定生成選項**

| 設定項 | 選項 | 說明 |
|--------|------|------|
| Controller 類型 | MVC / API | MVC 生成 View，API 生成 RESTful 端點 |
| 功能模組 | CRUD、列表、匯入、匯出、批量 | 勾選需要的功能 |
| 前端框架 | Razor / React / Vue 3 / Blazor | 僅 MVC 類型可選 |
| 列表欄位 | 勾選要顯示的欄位 | 自動生成 GridHeader |
| 搜尋欄位 | 勾選可搜尋的欄位 | 自動生成 Searcher |
| 匯入欄位 | 勾選可匯入的欄位 | 自動生成 TemplateVM |

**步驟 4：生成代碼**

點擊「生成」後，自動建立以下檔案：

```
Areas/
└── {Module}/
    ├── Controllers/
    │   └── ProductController.cs        ← CRUD Controller（含搜尋/匯入/匯出）
    ├── ViewModels/
    │   └── ProductVMs/
    │       ├── ProductVM.cs            ← CRUD ViewModel
    │       ├── ProductListVM.cs        ← 列表 ViewModel + Searcher
    │       ├── ProductBatchVM.cs       ← 批量操作 ViewModel
    │       └── ProductImportVM.cs      ← 匯入 ViewModel + Template
    └── Views/
        └── Product/
            ├── Index.cshtml            ← 列表頁（搜尋面板 + Grid）
            ├── Create.cshtml           ← 新增表單
            ├── Edit.cshtml             ← 編輯表單
            ├── Delete.cshtml           ← 刪除確認
            ├── Details.cshtml          ← 詳情檢視
            ├── Import.cshtml           ← 匯入頁面
            └── BatchEdit.cshtml        ← 批量編輯頁面
```

### 16.3 生成後的自訂

生成的程式碼是完整可運行的，但通常需要自訂以下部分：

```csharp
// ProductVM.cs — 最常需要自訂的地方
public class ProductVM : BaseCRUDVM<Product>
{
    // 1. 加入下拉選單資料
    public List<ComboSelectListItem>? AllCategories { get; set; }

    protected override void InitVM()
    {
        // 2. 載入下拉選單（生成器會自動為 FK 生成）
        AllCategories = DC!.Set<Category>()
            .GetSelectListItems(Wtm!, x => x.Name);
    }

    public override void Validate()
    {
        // 3. 加入自訂驗證（生成器不會生成，需手動加）
        if (Entity.Price <= 0)
            MSD!.AddModelError("Entity.Price", "價格必須大於 0");
    }
}
```

### 16.4 Analysis Mode 整合

如果 Model 上有 `[Dimension]` 和 `[Measure]` Attribute，生成器會自動：
- 在 ListVM 加上 `[EnableAnalysis]`
- 在 View 的 Grid 加上 `enable-analysis="true"`

### 16.5 使用限制

| 限制 | 說明 |
|------|------|
| 繼承要求 | Model 必須繼承 `TopBasePoco` 或其子類 |
| DbSet 註冊 | Model 必須在 DataContext 中有 `DbSet<T>` |
| Debug only | `[DebugOnly]` 標記，Release 模式不可存取 |
| 覆蓋風險 | 重複生成會覆蓋已有檔案 — 先備份再生成 |

**最佳實踐：** 用代碼生成器建立骨架，然後手動加入業務邏輯。自 10.7.0 起，regenerate-safe 兩區產生（見 §16.5）讓你可以安全地重複生成而不覆蓋手寫邏輯。

### 16.5 特性驅動生成 + regenerate-safe（10.7.0+）

10.7.0 加入一組宣告式特性，放在 Model/VM 屬性上，讓代碼生成器產出更貼近需求的骨架，**而且 runtime 也會讀取**——所以手寫的 Model 也享受同樣的預設，不必只靠生成。四個特性都在 `WalkingTec.Mvvm.Core` 命名空間，**每個預設值都重現舊行為**（不加特性 = 與舊版產出相同），純 additive。

| 特性 | 套用 | 主要屬性（預設） | 消費者 |
|------|------|------------------|--------|
| `[ListColumn]` | 屬性 | `Width(0=auto)` / `Align(Auto)` / `Sort(true)` / `Hide(false)` / `ShowTotal(false)` / `Fixed(None)` | codegen `$headers$` + runtime `MakeGridHeader` |
| `[SearchField]` | 屬性 | `Operator(SearchOperator.Auto: Auto/Contains/Equal/Between)` / `ShowInPanel(true)` / `DateRange(true)` / `Order` | codegen `$where$`（`Auto` = 字串→Contains、日期→Between、其餘→Equal；顯式 Operator 永遠優先；name-heuristic 須 opt-in `UseSmartSearchDefaults`） |
| `[FormField]` | 屬性 | `ControlType(FormControlType.Auto)` / `Colspan(1)` / `Group(null)` / `Order` / `Placeholder(null)` / `ReadonlyOnEdit(false)` | codegen 表單 + runtime（Placeholder / ReadonlyOnEdit） |
| `[ImportConfig]` | 屬性 | `DataType(ColumnDataType.Dynamic)` / `RequiredOnImport(false)` / `ColumnHeader(null)` / `DateFormat(null)` | codegen ImportVM（並自動帶入 Model 的 `[Required]`/`[StringLength]`） |

新增 enum 成員：`GridColumnFixedEnum.None`、`GridColumnAlignEnum.Auto`、`SearchOperator`、`FormControlType`。

```csharp
public class Order : BasePoco
{
    [Display(Name = "訂單編號")]
    [ListColumn(Width = 140, Fixed = GridColumnFixedEnum.Left)]
    [SearchField(Operator = SearchOperator.Equal)]   // 精確比對，不再誤用 LIKE
    public string OrderNo { get; set; }

    [Display(Name = "金額")]
    [ListColumn(Width = 120, Align = GridColumnAlignEnum.Right, ShowTotal = true)]
    public decimal Amount { get; set; }
}
```

**Regenerate-safe 兩區產生**：每個產出檔拆成 `*.Generated.cs`（標 `// <auto-generated/>`，每次重生都覆寫）與同名 partial `*.cs`（只在不存在時建立一次，保留手寫邏輯）。因此可以隨 Model 演進反覆重生，不會蓋掉自訂程式碼。產生的 Controller/VM stub 為 **async**，產生的測試使用 **SQLite shared-memory** fixture（取代不支援 `ExecuteUpdate`/子查詢的 EF InMemory）。

---

## 17. 配置參考

### 17.1 appsettings.json 完整結構

```json
{
  "Connections": [
    { "Key": "default", "Value": "Data Source=app.db", "DbType": "SQLite" },
    { "Key": "secondary", "Value": "Server=...;Database=...;", "DbType": "SqlServer" }
  ],
  "CookiePre": "WTM",
  "IsQuickDebug": true,
  "EnableTenant": false,
  "UIOptions": {
    "DataTable": {
      "RPP": 30,
      "ShowPrint": false,
      "ShowFilter": true
    },
    "ComboBox": { "DefaultEnableSearch": true },
    "DateTime": { "DefaultReadonly": true },
    "SearchPanel": { "DefaultExpand": true }
  },
  "FileUploadOptions": {
    "UploadLimit": 20971520,
    "SaveFileMode": "Local"
  },
  "JwtOptions": {
    "Issuer": "WTM",
    "Audience": "WTM",
    "Expires": 900,
    "RefreshExpires": 604800,
    "SecurityKey": "your-256-bit-secret-key-here-min-32-chars!",
    "LoginPath": "/Login/Login"
  },
  "CorsOptions": {
    "EnableAll": false,
    "Policy": [
      { "Name": "default", "Domain": "http://localhost:3000" }
    ]
  }
}
```

### 17.2 連線字串格式

| DbType | 連線字串範例 |
|--------|------------|
| `SQLite` | `Data Source=app.db` |
| `SqlServer` | `Server=localhost;Database=MyDb;User Id=sa;Password=xxx;TrustServerCertificate=True` |
| `MySql` | `Server=localhost;Port=3306;Database=MyDb;Uid=root;Pwd=xxx;` |
| `PgSql` | `Host=localhost;Port=5432;Database=MyDb;Username=postgres;Password=xxx;` |
| `Oracle` | `Data Source=//localhost:1521/ORCL;User Id=HR;Password=xxx;` |
| `Memory` | （任意字串作為 DB 名）`"testdb"` |

### 17.3 關鍵配置說明

| 配置項 | 預設值 | 說明 |
|--------|--------|------|
| `CookiePre` | `"WTM"` | Cookie 前綴（多站部署時避免衝突） |
| `IsQuickDebug` | `true` | 快速調試模式（跳過部分權限檢查） |
| `Layui:Asset` | （未設定 → `next`） | 佈局殼隨附的 LayUI 資產樹選擇：`legacy` → 2.6.3（`/layui`）；其他值/未設定 → 2.13.8（`/layui-next`，**10.14.0 起的預設**）。App 端 view 消費，框架不挑資產；值不串接進 URL。詳見 §6.1.1 |
| `EnableTenant` | `false` | 啟用多租戶 |
| `UIOptions.DataTable.RPP` | `20` | Grid 每頁預設行數 |
| `FileUploadOptions.UploadLimit` | `20971520` | 上傳限制（bytes，預設 20MB） |
| `FileUploadOptions.SaveFileMode` | `"Local"` | 檔案儲存方式（Local / Database） |
| `JwtOptions.Expires` | `3600` | Access Token 有效期（秒） |
| `JwtOptions.RefreshExpires` | `604800` | Refresh Token 有效期（秒，預設 7 天） |
| `JwtOptions.SecurityKey` | — | JWT 簽名金鑰（至少 32 字元，**必須修改**） |
| `CookieOptions.SecurePolicy` | `"SameAsRequest"` | Cookie `Secure` flag 策略（`SameAsRequest` / `Always` / `None`；production 建議 `Always`，詳見 §10.8） |
| `CookieOptions.Expires` | `3600` | Cookie 驗證有效期（秒） |
| `CookieOptions.LoginPath` | `"/Login/Login"` | 未登入時重導向的登入路徑 |
| `DashboardAlertOptions.EvaluationIntervalSeconds` | `0` | KPI 阈值告警評估間隔（秒；`0` = 關閉，10.8.0+，§9.13） |
| `DashboardSnapshotOptions` | — | 排程快照 cron / 匯出格式（`AddWtmDashboardSnapshots`，10.8.0+） |
| `EtlAlertOptions.EnableWebhookAlerts` | `false` | ETL 失敗/SLA webhook 告警卡開關（10.8.0+，§8.19） |
| `WtmWebhookOptions` | — | 共用 webhook sink（钉钉/企微/飞书/Slack/Teams）provider 設定（`AddWtmWebhookSink`，10.8.0+，§10.10） |
| `UIOptions.UseSelectIslandRender` | `false` | LayUI 互動元件 eval-free island render（10.16.0+，#470 G–M；預設關＝逐位元組舊輸出；10.16.1 起 gate 完整。`appsettings` 綁定與 code-based `Configure<WtmUIOptions>` 皆生效，§6.11） |
| `RefreshTokenRetentionOptions` | —（未註冊＝不啟用） | `AddWtmRefreshTokenRetention()` 的每日清理設定：`Enabled=true`、`RunAtLocalHour=4`（clamp [0,23] + 超界 warning）、`ExpiredDays=30`、`RevokedDays=30`、`BatchSize=5000`（10.17.0，#757，§10.2） |
| `ActionLogRetentionOptions.RunAtLocalHour` | `3` | 10.17.0 起超出 [0,23] 會 clamp 並 log warning（先前的 midnight=24 typo 會讓 host 開機失敗或停機，#762） |

### 17.4 version.props

```xml
<Project>
  <PropertyGroup>
    <VersionPrefix>10.12.1</VersionPrefix>
  </PropertyGroup>
</Project>
```

所有 NuGet 套件共用此版本號。修改此檔案後，所有 `dotnet pack` 產出的套件自動使用新版本。

#### 版本號規則 (X.Y.Z)

| 欄位 | 意義 | 何時遞增 |
|------|------|---------|
| **X** | .NET Core 主版本 | 僅在升至下一個 .NET 主版本（如 .NET 10 → 11）時遞增 |
| **Y** | 主功能升級 | 新增模組或重大新功能時遞增（例：新增 WorkFlow Wave 4+5 → `10.11.0`） |
| **Z** | 次要優化 | Bug 修復、patch、小優化時遞增（例：hotfix → `10.11.1`） |

**範例**：`10.11.0` = .NET 10、第 11 次主功能升級（WorkFlow Wave 4+5 加签/委托/超时）、初始釋出。  
**注意**：`X` 不是 .NET SDK patch 版本，不會因 SDK 10.0.300 vs 10.0.201 而變動，只在主版本升級（10 → 11）時才遞增。

### 17.5 多環境配置

```
appsettings.json              ← 基礎配置（所有環境共用）
appsettings.Development.json  ← 開發環境覆蓋（IsQuickDebug: true）
appsettings.Production.json   ← 生產環境覆蓋（連線字串、JWT Key）
```

**生產環境必改項目：**

| 項目 | 原因 |
|------|------|
| `Connections[].Value` | 使用正式資料庫連線 |
| `JwtOptions.SecurityKey` | 換成高強度隨機金鑰 |
| `IsQuickDebug` | 設為 `false`（啟用完整權限檢查） |
| `CookiePre` | 如有多站部署，確保不同前綴 |

---

## 18. WorkFlow 模組 — 審批引擎

`WalkingTec.Mvvm.WorkFlow` 是一個可選的 NuGet 套件（`dotnet add package WalkingTec.Mvvm.WorkFlow`），為 WTM 應用程式加入中文企業級審批/工作流引擎。架構上是 `WalkingTec.Mvvm.Etl` 的平行姊妹模組，依賴 `WalkingTec.Mvvm.Mvc`。

### 18.1 引擎概覽

| 能力 | 說明 |
|------|------|
| **版本固定流程定義** | canonical JSON + SHA-256 `ContentHash`；執行中審批 FK 不可變版本，定義修改不影響進行中流程 |
| **GuardedTransition** | 所有狀態變更走 `ExecuteUpdateAsync` 的 CAS（guard-in-WHERE），`rowsAffected == 0` → 冪等 no-op；防止雙重完成、遺失完成等競態 |
| **三種審批模式** | 串签/会签/或签，均透過單一 `ApproveMode` enum 在通用 Approval 節點上設定 |
| **IApproverResolver** | Role / User / ManagerChain（含循環偵測、去重、`MaxLevel` cap）；可自行實作 |
| **沙盒條件路由** | 白名單欄位 + 封閉 operator enum，`WhitelistRoutingEvaluator`，無 Roslyn/DynamicLinq，off-whitelist → fail-closed |
| **回退-to-node（Wave 3）** | `ReturnToPrevAsync`/`ReturnToNodeAsync`：退回任意支配上游節點；supersede-not-delete + Generation epoch + NextSeq CAS；見 §18.9 |
| **平行/包容閘道（Wave 3）** | `NodeKind.ParallelGateway`（AND-fork）/`InclusiveGateway`（OR-fork）+ 單語句 Join CAS + 孤兒 fail-closed；見 §18.9 |
| **加签（Wave 4）** | `AddApproverAsync`：活躍審批人注入額外審批人（`Before`/`After`），`ApproverSetEpoch` CAS 關閉並発完成競態；見 §18.10 |
| **委托/转交（Wave 4）** | `DelegateTaskAsync`（中途轉辦，單語句 CAS）、`DelegationResolvingDecorator`（節點進入前替代，遞移 + 循環偵測）、`RevokeDelegationAsync`；見 §18.11 |
| **超时/催辦（Wave 5）** | `AddWtmWorkFlowTimers()`：Remind 催辦、Escalate 升級、AutoApprove/AutoReject（`AllowTimerAutoAction=false` 預設 fail-closed）、`IBusinessCalendar` seam；見 §18.12 |
| **低代码設計器（Wave 6）** | `AddWtmWorkFlowDesigner()` + `UseWtmWorkFlowDesigner()`（opt-in）：`/_workflow-designer`，eval-free IIFE 模組，原始位元組保真，伺服器端草稿，Publish CAS，Antiforgery；見 §18.13 |
| **撤回/回退/抄送** | 撤回(WithdrawPolicy)、回退發起人(ReturnToInitiator)、抄送(CC，非阻塞) |
| **Opt-in 通知** | `AddWtmWorkFlowNotifications()` 複用 `IWtmWebhookSink`；post-commit best-effort，通知失敗不回滾 |
| **雙軌稽核** | `[AuditChanges]`（VM CRUD）+ append-only `WorkflowEventLog`（引擎轉換，`ExecuteUpdateAsync` bypass 了 EF change tracker） |
| **RBAC + 多租戶** | 所有 Entity 直接繼承 `PersistPoco, ITenant`，DataContext query filter 自動套用 |
| **零內建 migration** | 消費者自行 `ApplyWorkFlowModels()` + `dotnet ef migrations add`，與 Etl 模組相同模式 |

### 18.2 三種審批模式

| 模式 | `ApproveMode` | 行為 |
|------|--------------|------|
| **串签** | `Sequential` | 任務逐一啟動，前一位核准後才通知下一位 |
| **会签** | `All` | 所有審批任務同時建立；`approvePercent`（預設 100%）達標時節點完成（CAS）；`RejectGate.Immediate` = 一票否決，`RejectGate.AfterAll` = 不可達才否決 |
| **或签** | `Any` | 所有審批任務同時建立；第一位核准即 CAS 勝出，其餘任務取消；單票否決不觸發失敗，最後一張 Pending 被否決才觸發 |

### 18.3 DI 註冊

```csharp
// Program.cs / Startup.cs

// 必要 — 註冊引擎、審批解析器、路由評估器、完成處理器
services.AddWtmWorkFlow(options =>
{
    options.WithdrawPolicy              = WithdrawPolicy.BeforeFinalApproval; // 預設
    options.InitiatorAutoApprove        = false;                              // 預設 (安全值)
    options.AutoApproveOnMissingHandler = AutoApproveOnMissingHandlerPolicy.FailClose; // 預設 (#250)
    options.MaxLevel                    = 5;
    options.MaxReturnLoops              = 3;
});

// Opt-in — 通知（需搭配 AddWtmWebhookSink）
services.AddWtmWorkFlowNotifications();

// Opt-in — webhook sink（10.8.0 引入）
services.AddWtmWebhookSink(o => {
    o.DingTalk.Enabled    = true;
    o.DingTalk.WebhookUrl = Environment.GetEnvironmentVariable("DINGTALK_WEBHOOK");
});
```

### 18.4 消費者 Migration

WorkFlow 套件**零內建 migration**，需自行在應用程式的 `DataContext.OnModelCreating` 呼叫：

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.ApplyEtlModels();       // 若同時使用 Etl
    modelBuilder.ApplyWorkFlowModels();  // WorkFlow 9 張資料表
}
```

產生 migration：

```bash
dotnet ef migrations add WorkFlowInitialCreate \
  --context DataContext \
  --project YourApp/YourApp.csproj \
  --startup-project YourApp/YourApp.csproj
```

建立的 9 張資料表：`Wf_ProcessDefinition`、`Wf_ProcessDefinitionVersion`、`Wf_ProcessInstance`、`Wf_NodeInstance`、`Wf_ApprovalTask`、`Wf_WorkflowEventLog`、`Wf_CcRecord`、`Wf_DelegationRule`、`Wf_WorkflowTimer`（後兩張供 Wave 4/5 使用，Sprint-1 schema 已到位，零 migration 費用）。

### 18.5 API Endpoints

| Controller | 路徑 | 說明 |
|------------|------|------|
| `ProcessDefinitionController` | `GET /api/_workflow/definitions` | 列出流程定義 |
| | `POST /api/_workflow/definitions/{id}/publish` | 發布（驗證→規範化→hash→新版本或 no-op） |
| | `POST /api/_workflow/definitions/validate` | 僅驗證（dry-run） |
| | `GET /api/_workflow/definitions/{key}/versions` | 版本歷史 |
| `WorkflowInstanceController` | `POST /api/_workflow/instances/start` | 啟動新審批實例 |
| | `POST /api/_workflow/instances/{id}/withdraw` | 撤回 |
| | `GET /api/_workflow/instances/{id}/timeline` | 稽核時間軸 |
| `WorkflowTaskController` | `GET /api/_workflow/tasks/mine` | 我的待辦任務 |
| | `POST /api/_workflow/tasks/{id}/approve` | 核准 |
| | `POST /api/_workflow/tasks/{id}/reject` | 否決 |
| | `POST /api/_workflow/tasks/{id}/return` | 回退發起人 |

所有端點均有 `[ActionDescription]` / `FunctionPrivilege` RBAC 管控，核准資格由 `AssigneeITCode` 在 Action 層驗證。

### 18.6 安全預設與注意事項

- **`InitiatorAutoApprove = false`**（預設）：發起人不會因為是第一位審批人就被自動略過。若需舊行為（explicit opt-in），設 `options.InitiatorAutoApprove = true` 並記錄決策理由。
- **`AutoApproveOnMissingHandler = FailClose`**（預設，#250）：審批人無法解析時節點 fail-closed，需管理員干預。前一隱式預設為 `AutoApprove`（靜默繞過審批，合規風險）。
- **`DBTypeEnum.Memory` 不支援**：EF InMemory 不支援 `ExecuteUpdateAsync`，啟動時立即拋 `InvalidOperationException`。請使用 SQLite、SQL Server、PostgreSQL、MySQL、Oracle 或達夢。
- **沙盒路由**：欄位存取採正向白名單，非白名單欄位在 publish 和 runtime 雙層 fail-closed。
- **通知不阻塞**：所有通知在引擎 transaction commit 後發送，投遞失敗記 Error log，不回滾審批決定。
- **`EnableRetryOnFailure` 相容 + 統一死鎖結果碼（10.15.0，#667）**：引擎所有交易改走 execution-strategy 包裹，EF Core `EnableRetryOnFailure` 開啟時不再拋 `InvalidOperationException`。曾用 try/catch 包 `StartAsync`/`ApproveTaskAsync`/`RejectTaskAsync` 攔死鎖類例外的 caller，改檢查 `result.Code == WorkflowActionCode.DeadlockRetryExhausted`（與其他引擎路徑一致；本來就檢查 result code 的 caller 無需變更）。

### 18.7 稽核機制

- `[AuditChanges]` 套用在 `ProcessDefinition`、`ProcessDefinitionVersion`、`ProcessInstance`、`NodeInstance`、`ApprovalTask`、`DelegationRule`——VM CRUD 操作透過 `ChangeLog` 自動記錄。
- **`WorkflowEventLog`（append-only）** 是引擎狀態轉換的權威稽核來源——引擎使用 `ExecuteUpdateAsync` bypass 了 EF change tracker，`[AuditChanges]` 看不到這些操作。Timeline endpoint：`GET /api/_workflow/instances/{id}/timeline`。

### 18.8 ProcessDefinition Admin Grid

`ProcessDefinitionListVM` 提供租戶範圍的流程定義列表（唯讀）。在區域 Controller 使用：

```csharp
[ActionDescription("流程定義管理")]
public class WfProcessDefinitionController : BaseController
{
    [ActionDescription("列表")]
    public ActionResult Index()
    {
        var vm = CreateVM<ProcessDefinitionListVM>();
        return PartialView(vm);
    }
}
```

### 18.9 Wave 3 — 回退-to-node + 平行/包容閘道 + Join + Ack（10.10.0+）

#### 18.9.1 回退-to-node

Wave 3 在原有「回退發起人」基礎上新增兩個 API：

```csharp
// 退回到最近的已完成上游 Approval 節點（自動計算支配節點）
Task<WorkflowActionResult> ReturnToPrevAsync(
    Guid taskId, string actorITCode, string? reason, CancellationToken ct);

// 退回到指定節點（必須是 trigger 節點的支配節點）
Task<WorkflowActionResult> ReturnToNodeAsync(
    Guid taskId, string targetNodeKey, string actorITCode, string? reason, CancellationToken ct);
```

兩者均需在流程定義的 `rejectPolicy` 設為 `ReturnToPrev` 或 `ReturnToNode`（publish 時驗證），並在執行期驗證目標確為支配節點。回退流程採用引擎自管 transaction，透過六步驟 CAS pipeline 完成 span 超棄（supersede-not-delete）與重新實體化：

| STEP | 操作 | 關鍵 CAS |
|------|------|---------|
| 0 | 驗證 + PIN（read-only） | — |
| 1 | 進入 `Returning` 狀態（互斥 + epoch 遞增 + loop 計數） | `WHERE State==Running AND RowVer==@v AND Generation==@gOld AND ReturnLoops < @max` |
| 2 | 取消跨度計時器 | 各計時器列 per-row CAS |
| 3 | 丟棄 ApprovalTask 跨度 | `WHERE State IN (NotYetActive, Pending, ...) AND RowVer==@v` |
| 4 | 超棄 NodeInstance 跨度（`State→Superseded`） | `WHERE State IN (Activated, Pending) AND RowVer==@v` |
| 5 | 重新實體化目標節點（guarded mint） | `UNIQUE (TenantCode, InstanceId, NodeKey, Generation)` |
| 6 | 追加 Return 事件 + 解鎖 + 路由 | 在同一 txn 內，`Seq` 由 `NextSeq` CAS 分配 |

`MaxReturnLoops`（預設 3）耗盡時，STEP 6-FC 將實例轉為 `Terminated` 並記 `FailClosed` 事件。  
`ReturningLeaseUtc` 用於 crash recovery：Wave-5 reaper 透過一列式 CAS 回收過期租約。

**新增 enum 成員**：

| Enum | 新成員 |
|------|--------|
| `NodeState` | `NodeState.Superseded`（終止：跨度丟棄或合入 Join） |
| `InstanceState` | `InstanceState.Returning`（回退互斥子狀態，租約保護） |
| `NodeKind` | `ParallelGateway`、`InclusiveGateway`（`Join`、`Ack`、`Cc`、`Condition` 已在 MVP） |
| `WorkflowActionCode` | `AlreadyHandled`、`MaxReturnLoopsExceeded`、`JoinUnsatisfiable`、`Returned` |

**新增 `GuardedTransition` 方法**（Wave 3）：`BeginReturnAsync`、`CancelTimersForReturnAsync`、`DiscardTasksForReturnAsync`、`SupersedeNodeAsync`、`MintNodeInstanceGuardedAsync`、`AllocateSeqAsync`、`ReclaimReturningLeaseAsync`（Wave 3 回退相關）；`IncrementJoinArrivedAsync`、`DecrementJoinExpectedAsync`、`FireJoinIfSatisfiedAsync`（Join 相關）。所有既有 `Activate`/`Complete`/`IncrementApproved`/`ClaimTask` predicate 均加入 `AND Generation == @g`。

**出範圍限制（Wave 3）**：return target 必須*支配* trigger 節點；退回到開放平行區域內部（fork 跨越目標邊界）會在 publish 時拒絕或在執行期 `FailClosed`。

#### 18.9.2 NextSeq — 取代 MAX(Seq)+1

10.10.0 移除 `WorkflowEventLogWriter` 中的 `MAX(Seq)+1`/SERIALIZABLE 模式，改由 `ProcessInstance.NextSeq`（`int`, default 1）提供每實例單調序號：

```sql
-- AllocateSeqAsync（GuardedTransition）
UPDATE Wf_ProcessInstance
SET    NextSeq = NextSeq + 1,
       RowVer  = RowVer + 1
WHERE  ID == @id AND RowVer == @v
-- 返回舊的 NextSeq 值作為本次事件的 Seq
```

兩個並行 append 競爭同一 RowVer；勝者取 Seq=k，敗者重試取 Seq=k+1。序號連續、單調、無間隙，且不依賴任何隔離層級。`WorkflowEventLog.Generation`（`int?`，nullable）僅供稽核分組，不參與 Seq 計算。

#### 18.9.3 平行閘道（`NodeKind.ParallelGateway`）與包容閘道（`NodeKind.InclusiveGateway`）

在流程定義 JSON 中使用新節點類型：

```jsonc
// AND-fork（ParallelGateway）— 所有分支同時啟動
{ "nodeKey": "fork1", "kind": "ParallelGateway",
  "outgoing": ["branch_a", "branch_b", "branch_c"],
  "joinNodeKey": "join1"
},

// OR-fork（InclusiveGateway）— 條件為真的分支啟動
{ "nodeKey": "fork2", "kind": "InclusiveGateway",
  "branches": [
    { "rule": { "field": "amount", "operator": "Gt", "value": 100000 }, "target": "cfo" },
    { "rule": { "field": "risk",   "operator": "Eq", "value": "high" }, "target": "risk_team" }
  ],
  "default": "mgr",
  "joinNodeKey": "join1"
},

// Join 節點 — 等待所有（或已到達的）分支
{ "nodeKey": "join1", "kind": "Join" }
```

`InclusiveGateway` 在 fork 時固定 `JoinExpectedArrivals` = 實際啟動分支數，防止 OR-join 死鎖。`ParallelGateway` 的 `JoinExpectedArrivals` = 所有 outgoing 分支數。

所有 fork mint 均透過 `UNIQUE (TenantCode, InstanceId, NodeKey, Generation)` 冪等保護。

#### 18.9.4 Join 節點

Join 完成是一個**單語句條件式 CAS**：

```sql
-- FireJoinIfSatisfiedAsync（GuardedTransition）
UPDATE Wf_NodeInstance
SET    State  = CompletedApproved,
       RowVer = RowVer + 1
WHERE  ID                == @joinId
  AND  State             == Activated
  AND  Generation        == @g
  AND  JoinArrivedCount  >= JoinExpectedArrivals
  AND  RowVer            == @v
```

`rows == 1` → 本 token 觸發 Join，接著 mint 後繼節點；`rows == 0` → 尚未滿足或其他 token 已觸發，冪等 no-op。

孤兒 token fail-closed：一個分支進入終止狀態（`Superseded`、`CompletedRejected`、`FailClosedRouting`）時，於同一 txn 內呼叫 `DecrementJoinExpectedAsync`（防下溢 CAS），再嘗試 `FireJoinIfSatisfiedAsync`。可達性兜底查詢在每次 Join 評估時重新計算「仍能到達 Join 的存活 token」；若集合為空且 Join 尚未滿足，則強制 `CompletedRejected` + `EventAction.FailClosed`，確保 Join 絕不掛起。

#### 18.9.5 Ack 節點（阻塞確認）

`NodeKind.Ack`（已在 enum 中）現在有完整的 `AckHandler` 實作：

- `AckHandler.OnEnterAsync` 建立 `ApprovalTask`（assignee = 確認人）並重用現有任務機制。
- `CanCompleteAsync == false` 直到必要確認數達標（透過現有 `ClaimApprovalTaskAsync` 將任務 `Pending→Approved`）。
- `AckMode ∈ {All, Any, Quorum}`，語意對應 `ApproveMode`。
- **阻塞 token**：直到確認完成前，下游不推進。

對照：`NodeKind.Cc`（已有）`CanCompleteAsync == true`，**永不阻塞 token**。

Ack 任務帶有 `Generation` 戳記，回退時與跨度一起丟棄。

#### 18.9.6 Migration（Wave 3 schema additive 欄位）

```csharp
// 在 DataContext.OnModelCreating 呼叫（已有則自動掃描）
modelBuilder.ApplyWorkFlowModels();
```

```bash
# 產生 Wave 3 migration
dotnet ef migrations add WorkFlowWave3 \
  --context DataContext \
  --project YourApp/YourApp.csproj \
  --startup-project YourApp/YourApp.csproj
```

新增的 additive 欄位（所有既有列以預設值回填，**不遺失資料**）：

| 資料表 | 新欄位 | 預設 / 回填 |
|--------|--------|------------|
| `Wf_ProcessInstance` | `Generation uint` | 0 |
| | `ReturnLoops uint` | 0 |
| | `NextSeq int` | **`MAX(Seq)+1` per instance**（migration SQL 必須回填，見下） |
| | `ReturningLeaseUtc DateTime?` | NULL |
| `Wf_NodeInstance` | `Generation uint` | 0 |
| | `SupersededAtGen uint?` | NULL |
| | `ForkGroupId Guid?` | NULL |
| | `JoinNodeKey string?` | NULL |
| | `JoinExpectedArrivals int` | 0 |
| | `JoinArrivedCount int` | 0 |
| `Wf_ApprovalTask` | `Generation uint` | 0 |
| `Wf_WorkflowTimer` | `Generation uint` | 0 |
| `Wf_WorkflowEventLog` | `Generation int?` | NULL |

> **重要**：`NextSeq` 的回填 SQL（於 migration Up() 中加入）：
> ```sql
> UPDATE Wf_ProcessInstance pi
> SET    NextSeq = COALESCE((SELECT MAX(Seq) + 1 FROM Wf_WorkflowEventLog WHERE InstanceId = pi.ID), 1)
> WHERE  NextSeq = 1;
> ```
> 若跳過此步驟，第一次 `AllocateSeqAsync` 呼叫將回傳 Seq=1，與已存在的事件記錄衝突。

新增唯一索引（非部分索引，跨 MySQL/Oracle/DaMeng 可攜）：  
`UNIQUE (TenantCode, InstanceId, NodeKey, Generation)` on `Wf_NodeInstance`

**相容性**：多 token 行為僅在 `ParallelGateway`/`InclusiveGateway` 節點觸發。既有單 token 流程（`Start`/`Approval`/`Condition`/`End`）行為完全不變。`Generation == 0` 的既有實例透明相容——live-marking 查詢 `WHERE Generation == instance.Generation` 評估為 `0 == 0`，行為與升級前相同。

---

### 18.10 Wave 4 — 加签 (add-approver)（10.11.0+）

**加签**讓一個活躍審批人可以在自己的審批位置前或後注入額外的審批人，無需修改流程定義。

#### 18.10.1 API

```csharp
Task<WorkflowActionResult> AddApproverAsync(
    Guid taskId,                          // 操作者自己的 Pending 任務 PK
    string actorITCode,                   // server-side RBAC 核驗
    IReadOnlyList<string> newApproverITCodes,
    AddPosition position = AddPosition.After,   // Before | After
    string? reason = null,
    CancellationToken ct = default);
```

`AddPosition.Before` 插入操作者序號之前；`AddPosition.After` 插入之後。

#### 18.10.2 各審批模式行為

| 審批模式 | Before | After |
|---------|--------|-------|
| 会签 (All/ratio) | 立即 `Pending`；`TotalRequired += delta` | 同左 |
| 串签 (Sequential) | 插入為 `AddedPending`（指標推進時才 Activate） | 插入為 `AddedPending`，序號為操作者 +1 |
| 或签 (Any) | 拓寬候選集；epoch bump 序列化並發決定 | 同左 |

#### 18.10.3 `ApproverSetEpoch` CAS 保護

`Wf_NodeInstance` 新增 `ApproverSetEpoch (uint, default 0)`。每次加签在同一個 `ExecuteUpdateAsync` 中同時遞增 `TotalRequired` 與 `ApproverSetEpoch`（並更新 `RowVer`）。完成 CAS（`CompleteNodeInstanceAsync`）在 predicate 中加入 `AND ApproverSetEpoch == @e`，確保：

- 若加签先提交 → 完成 CAS epoch 過期 → rows==0 → 引擎重新讀取新 `TotalRequired`，待新審批人也完成才繼續。
- 若完成先提交 → 節點已離開 `Activated` → 加签 CAS `WHERE State==Activated` → rows==0 → `NodeAlreadyDecided`，任務不插入。

此機制與 Wave-3 用 `Generation` 保護的設計完全對稱（GuardedTransition.cs 中的可選參數模式）。

#### 18.10.4 MaxAddDepth 防止無限鏈

`WorkFlowOptions.MaxAddDepth`（預設 3）。每個任務有 `ApprovalTask.AddDepth (int, default 0)`：原始審批人為 0，注入任務為 `sourceTask.AddDepth + 1`。超過上限 → `WorkflowActionCode.MaxAddDepthExceeded`（O(1)，不需遍歷鏈）。

#### 18.10.5 回退時自動清除

注入任務攜帶 `Generation`。回退時 `DiscardTasksForReturnAsync` 按 Generation 批次取消所有 `AddedPending` 和範圍任務（**零新代碼**）。重新進入節點時以新 `Generation` mint 全新的原始審批人集合。

#### 18.10.6 Migration（Wave 4 additive 欄位）

```bash
dotnet ef migrations add WorkFlowWave45 \
  --context DataContext \
  --project YourApp/YourApp.csproj \
  --startup-project YourApp/YourApp.csproj
```

| 資料表 | 新欄位 | 預設 |
|--------|--------|------|
| `Wf_NodeInstance` | `ApproverSetEpoch uint` | 0 |
| `Wf_ApprovalTask` | `AddDepth int` | 0 |

---

### 18.11 Wave 4 — 委托/转交 (delegation)（10.11.0+）

**委托**允許審批人將自己的審批任務轉讓給他人，或設定站立規則使未來任務自動轉派。

#### 18.11.1 兩條路徑

**路徑 A：節點進入前替代（standing delegation，零并发）**

`DelegationResolvingDecorator` 在 `IApproverResolver.ResolveAsync` 後、節點 `Activated` 前，將每個原始審批人替換為活躍 `DelegationRule`（`IsValid==true AND now ∈ [StartUtc,EndUtc]`）的最終受委人。替代是 1-for-1 的：`TotalRequired` 在 mint 事務中一次性寫入，無並發問題。

可遞移鏈（最多 `MaxDelegationHops = 3`，預設）：
```
ResolveTransitive(P):
  visited = { P };  cur = P;  hops = 0
  loop:
    rule = activeRuleFor(cur)  // 活躍規則：IsValid + 時間窗
    if rule == null: return cur
    next = rule.DelegateeITCode
    if visited.Contains(next): log; return AdminFallback(cur)  // 循環 → fail-closed
    if ++hops > MaxDelegationHops: log; return cur             // 達上限 → 停在最後解析處
    visited.Add(next); cur = next
```

若 `AdminFallbackITCode` 為空，引擎 **fail-closes**（從不 fail-open）。

**路徑 B：中途轉辦（mid-flight，`DelegateTaskAsync`）**

```csharp
Task<WorkflowActionResult> DelegateTaskAsync(
    Guid taskId,          // 操作者自己的 Pending 任務 PK
    string actorITCode,   // 必須等於 task.AssigneeITCode
    string delegateeITCode,
    Guid? delegationRuleId = null,
    string? reason = null,
    CancellationToken ct = default);
```

單語句 CAS 將 `AssigneeITCode` 從委托人轉為受委人：

```sql
UPDATE Wf_ApprovalTask
   SET AssigneeITCode = @delegatee, DelegatedFromITCode = @actor,
       DelegationRuleId = @ruleId, DelegationExpiresUtc = @expiry,
       ApproverSetEpoch = @nodeEpochStamp, RowVer = RowVer + 1
 WHERE ID = @taskId AND State = Pending
   AND RowVer = @expectedRowVer AND Generation = @g
```

`TotalRequired` **從不變動**（1-for-1）。若受委人已在同節點持有活躍任務 → `DelegateAlreadyParticipant`（拒絕，維護 vote-count 不變量）。

#### 18.11.2 `DelegationWindowMode`

| 模式 | 行為 | 適用場景 |
|------|------|---------|
| `AtAssignment`（預設）| 規則時間窗在任務 mint 時評估一次，凍結至 `DelegationExpiresUtc` | 合規優先；授權在指定時刻確定 |
| `AtAction`（opt-in）| 每次操作時重新核驗時間窗（含入 CAS predicate） | 嚴格實時控制；**Oracle/DaMeng 啟動時阻擋，等待 #270** |

> **重要**：更改 `DelegationWindowMode` 的預設值需在 `CHANGELOG.md` 記錄，因為這會靜默改變審批授權語義。

#### 18.11.3 RevokeDelegationAsync（管理員撤回）

```csharp
Task<int> RevokeDelegationAsync(
    Guid delegationRuleId,
    string actorITCode,   // 需持有管理員角色
    string? reason = null,
    CancellationToken ct = default);
// 回傳：成功撤回的任務數（rows==1 CAS 次數）
```

批量將所有由 `delegationRuleId` 產生的 `Pending` 任務回歸原始審批人（1-for-1 CAS；`TotalRequired` 不變；冪等）。`DelegationRule.IsValid = false` 只影響未來激活，不影響進行中任務——此方法是進行中任務的撤回路徑。

#### 18.11.4 Migration（Wave 4 delegation 欄位）

| 資料表 | 新欄位 | 預設 |
|--------|--------|------|
| `Wf_ApprovalTask` | `DelegationRuleId Guid?` | NULL |
| | `DelegationExpiresUtc DateTime?` | NULL |
| | `WindowVerifiedUtc DateTime?` | NULL |
| `Wf_NodeInstance` | `DefinitionCode string?` | NULL |

（與 §18.10.6 的 migration 合併在同一個 `WorkFlowWave45` 指令）

---

### 18.12 Wave 5 — 超时/催辦 (timeout + remind)（10.11.0+）

**超时功能**需額外呼叫 `AddWtmWorkFlowTimers()`（不呼叫則行為與 10.10.0 完全相同）。

#### 18.12.1 DI 註冊

```csharp
services.AddWtmWorkFlow(options =>
{
    // AllowTimerAutoAction = false（預設，必須明確 opt-in 才能自動審批/拒絕）
    options.AllowTimerAutoAction = false;  // 顯式確認
    options.TimerBatchSize = 100;
    options.MaxRemindersDefault = 3;
    options.MaxRemindersHardCap = 10;
    options.ReturningLeaseTtl = TimeSpan.FromMinutes(30);
    // options.BusinessCalendarId = "myCalendar";  // 需實作 IBusinessCalendar
});

// Timeout 排程（opt-in）
services.AddWtmWorkFlowTimers();

// 若需自訂業務曆（例：排除週末/節假日）
services.AddSingleton<IBusinessCalendar, MyCompanyCalendar>();
```

#### 18.12.2 流程定義中的 `TimeoutDef`

```json
{
  "nodeKey": "dept-approval",
  "kind": "Approval",
  "approveMode": "All",
  "approvers": [...],
  "timeout": {
    "duration": "PT48H",          // ISO-8601 duration
    "action": "Remind",           // Remind | Escalate | AutoApprove | AutoReject
    "remindEveryHours": 8,        // 催辦間隔（Remind 鏈）
    "maxReminders": 6,            // 最多幾次（受 MaxRemindersHardCap=10 限制）
    "escalateTo": "manager001"    // Escalate 目標 ITCode（可選，否則用 AdminFallbackITCode）
  }
}
```

#### 18.12.3 Action 語義

| `action` | 說明 | 合規預設 |
|----------|------|---------|
| `Remind` | 在事務中原子插入下一個催辦連結，post-commit 通知現有 Pending 審批人 | 預設安全，無需額外 opt-in |
| `Escalate` | 任務型計時器：透過 `EscalateTaskAssigneeAsync` 單語句 CAS 轉派給 `EscalateTo`/`AdminFallbackITCode`；受委人已是參與者 → 降級為 notify-only；節點型計時器：僅通知 | 合理預設 |
| `AutoApprove` | 計時器到期時自動審批 | **需 `AllowTimerAutoAction = true`**（預設 false），否則降級為 Remind + FailClosed 事件 |
| `AutoReject` | 計時器到期時自動拒絕 | 同上 |

> **合規警告**：`AllowTimerAutoAction = true` 允許系統繞過人工審批。此設定屬**合規敏感**選項，必須在 CHANGELOG 中明確記錄並由業務負責人簽核。

#### 18.12.4 多主機安全

`FireTimerAsync` CAS（`WHERE Status==Armed AND RowVer==@v`）是多主機去重的互斥鎖——只有一台主機的 rows==1，其餘 rows==0 後直接跳過，不產生副作用。

#### 18.12.5 `IBusinessCalendar` 整合

```csharp
public interface IBusinessCalendar
{
    DateTime AddBusinessTime(DateTime from, TimeSpan duration, string? calendarId);
}
```

預設 `PassThroughBusinessCalendar` 直接加上 wall-clock duration。若流程定義指定 `businessCalendar: true` 且只有 pass-through 已註冊：
- `Remind`：以 wall-clock 計算 + `LogWarning`（早觸發，但催辦無害）
- `AutoApprove/AutoReject/Escalate`：**在 publish 時拒絕此組合**（fire-time 自動繞過是合規謊言）

#### 18.12.6 Migration（Wave 5 — 零新欄位）

`Wf_WorkflowTimer` 表在 Sprint-1（10.9.0）時 schema 已就位，Wave 5 **不增加任何新欄位**。`EventAction` 新增 3 個成員（`TimeoutRemind`、`TimeoutEscalate`、`DelegationExpiredReverted`）為 enum append-only，不需 schema 變更。

無需額外執行 `dotnet ef migrations add`（與 §18.10.6 合併在同一個 `WorkFlowWave45` migration 即可）。

### 18.13 Wave 6 — 低代码工作流设计器（10.12.0+）

低代碼設計器讓您在瀏覽器中直接創作和發布 `ProcessDefinition` 流程圖，無需手寫 JSON。

#### 18.13.1 DI 註冊

```csharp
// Program.cs（或 Startup.cs）
builder.Services.AddWtmWorkFlow(opts => { /* ... */ });
builder.Services.AddWtmWorkFlowDesigner();   // opt-in；未呼叫則行為完全不變

// Middleware pipeline
app.UseWtmContext();
app.UseWtmWorkFlowDesigner();   // 掛載 /_workflow_designer/assets/* 靜態檔案路徑
```

未呼叫 `AddWtmWorkFlowDesigner()` 時，所有設計器 API 端點返回 HTTP 404（非 500），不影響其他功能。

#### 18.13.2 RBAC 設定

設計器頁面及所有 API 端點均受 URL-RBAC 保護（`PrivilegeFilter`）。在管理員角色設定中註冊兩個 privilege 常數：

```csharp
new FunctionPrivilege { MenuName = "工作流设计器", Url = WorkflowPrivileges.DesignerPage },
new FunctionPrivilege { MenuName = "工作流设计器 API", Url = WorkflowPrivileges.DesignerBase },
```

`[AllRights]` 被刻意排除 — 設計/發布是特權操作。只有同時擁有兩個 privilege 的角色才能開啟設計器。

#### 18.13.3 Migration（必需）

呼叫 `AddWtmWorkFlowDesigner()` 後，`ApplyWorkFlowModels()` 會額外註冊 `ProcessDefinitionDraft` 實體。請執行消費者 migration：

```bash
dotnet ef migrations add AddWorkFlowDesigner \
  --context DataContext \
  --project YourApp/YourApp.csproj \
  --startup-project YourApp/YourApp.csproj
dotnet ef database update
```

新表 `Wf_DefinitionDraft`：`DefinitionId`（FK → `ProcessDefinition`）、`GraphJson`（text）、`BaseContentHash`（nullable）、`RowVersion`、`LastSavedBy`、`LastSavedAt`。每 `(TenantCode, DefinitionId)` 一筆草稿。

#### 18.13.4 開啟設計器

瀏覽 `/_workflow-designer`（可帶 `?code=<definitionCode>` 直接開啟特定流程）。頁面是靜態嵌入式 HTML，零 inline script；所有 JS 模組從 WorkFlow assembly embedded resources 提供，無需 CDN。

#### 18.13.5 三個視圖

| 視圖 | 說明 |
|------|------|
| **表單視圖** | 9 種 NodeKind 屬性面板，Transition 表格含 per-edge 條件編輯器，fieldWhitelist 編輯器。所有內容透過 DOM 元素構建（`document.createElement` + `textContent`），無 `innerHTML`，無 eval。 |
| **源碼視圖** | 原始 JSON textarea，含語法驗證與格式化。複雜 payload 的完整 escape hatch。 |
| **圖形視圖** | 唯讀 SVG 自動佈局（BFS rank-from-Start），`createElementNS` + `textContent` only，切換 tab 時更新。 |

#### 18.13.6 草稿與發布流程

1. **新建流程**：點擊「新建流程」，輸入 Code（`^[A-Za-z0-9_\-\.]{1,64}$`）、名稱、分類。
2. **編輯**：在表單視圖設定各節點屬性，或在源碼視圖直接編輯 JSON。
3. **保存草稿**：「保存草稿」呼叫 `PUT /api/_workflow/designer/definitions/{code}/draft`（If-Match / If-None-Match RowVersion 並發保護）。草稿存儲在伺服器（每個租戶/定義一筆）。
4. **校驗**：「校驗」呼叫 `POST /api/_workflow/designer/validate`，錯誤回應包含可選的 `nodeKey` 定位問題節點。
5. **發布**：「发布」確認後呼叫 `POST /api/_workflow/designer/definitions/{code}/publish`（帶 `X-WTM-WF-Expected-Hash` CAS header）。結果：
   - `IdempotentNoOp`：位元組相同，無新版本。
   - `Published`：創建不可變的新 `ProcessDefinitionVersion`，草稿在同一 transaction 中刪除。
   - HTTP 409 `BaseVersionChanged`：他人在此期間已發布 → 顯示衝突面板，重新載入後在源碼視圖合併。

#### 18.13.7 原始位元組保真合約

- **未修改的儲存**：payload 為原始位元組字串 → 位元組相同 → `ContentHash` 相同 → `IdempotentNoOp`（T-DSN-1）。
- **已修改的儲存**：`WtmJsonRaw.stringify(tree)` — 未觸碰的數字保留原始字面量（`9007199254740993` 不會變成 `9007199254740992`）；未觸碰的 unknown fields 存活；已編輯的已知欄位帶入使用者輸入。
- **Condition branch 更新**：merge-not-regen — 僅更新使用者實際修改的 `(from,to)` TransitionDef，不重建出邊列表，不破壞 unknown fields 或合法的 `condition` payload。
- **`schemaVersion != 1`**：表單視圖鎖定（banner 提示），源碼視圖和 SVG 視圖仍可用；伺服器端 publish 拒絕（HTTP 400 `SchemaVersionUnsupported`）。

#### 18.13.8 `WorkFlowOptions.Designer` 選項

| 選項 | 預設 | 說明 |
|------|------|------|
| `MaxGraphBytes` | 1 MiB | 設計器端點接受的最大 GraphJson 位元組數 |

#### 18.13.9 消費者 wwwroot 要求

設計器頁面從您應用的 `wwwroot` 載入 LayUI 和 jQuery（與 WTM admin shell 相同的路徑要求，框架本身不提供）：

```
wwwroot/
  jquery.min.js
  layui/
    css/layui.css
    layui.js
```

---

### 18.14 Wave 6 — 安全修復（10.12.0+）

#### 18.14.1 #296 — Webhook 通知字串 Escape-at-sink

`WebhookWorkflowNotifier` 現在在插值進 webhook card markdown 前，對所有流程圖創作和使用者創作的字串（node key、node name、審批人顯示名、評論摘要）進行 escape。

**受影響範圍**：钉钉、企微、飞书、Slack、Teams 通知卡片。

**重要**：此修復保護的是**所有卡片**，包含由已發布流程圖產生的卡片 — 無需重新發布流程圖即可受到保護。

```csharp
// 框架內部（無需消費者修改）
// 在 WebhookWorkflowNotifier 的所有插值點：
var escapedNodeName = MarkdownEscape(nodeInstance.NodeName);
var escapedComment = MarkdownEscape(task.Comment?.Substring(0, Math.Min(50, task.Comment.Length)));
```

#### 18.14.2 #296 — nodeKey 字符集白名單與重複檢查

`WorkflowGraphValidator` 新增兩個在 publish 時 fail-close 的驗證規則：

| 規則 | 描述 | 錯誤碼 |
|------|------|--------|
| `InvalidNodeKey` | nodeKey 必須匹配 `^\p{L}\p{N}_\-\.\p{L}\p{N}]{0,63}$`（CJK 友好；阻止注入字符） | `InvalidNodeKey` |
| `DuplicateNodeKey` | 同一圖中不得有兩個節點共享相同 `nodeKey` | `DuplicateNodeKey` |

這些驗證僅影響**新發布**。現有的已發布版本和進行中的流程實例不受影響。若現有 nodeKey 包含違規字符，下次發布時會被拒絕 — 請在重新發布前審查並更新這些 nodeKey。

#### 18.14.3 #297 — Dashboard 設計器 JS 資產 404 修復

`framework_dashboard_designer.js` 已加入 `src/WalkingTec.Mvvm.Mvc` 的 `EmbeddedResource` 清單，修復 Dashboard 設計器頁面（`/_dashboard-designer`）的 404 錯誤。新增迴歸測試在每個設計器資產上斷言 HTTP 200，防止此類問題復發。

---

---

## 19. 常見問題

### Q1: FrameworkContext 測試時出現 SQLite Error 1（重複欄名）
**原因：** `base.OnModelCreating()` 會掃描所有載入的 assembly，造成 `MajorId` 等欄位衝突。
**解法：** 測試用的 `TestFwContext : FrameworkContext` 必須 override `OnModelCreating` **不呼叫** `base`。EF Core 透過 `DbSet<>` 屬性自動探索實體。

### Q2: method.Invoke() 的例外被包裝
**原因：** 反射呼叫（如 Analysis 的 `ExecuteDynamic`）會把內部例外包在 `TargetInvocationException` 裡。
**解法：** catch 時 unwrap：`catch (TargetInvocationException ex) { throw ex.InnerException!; }`

### Q3: Nullable 遷移策略
- 新檔案：完全 nullable-annotated，不加 `#nullable disable`
- catch block：用 `?.` + fallback，不用 `!`
- 現有 156 個 `#nullable disable` 檔案，按依賴順序逐批遷移

### Q4: 多資料庫切換
**方式 1：Attribute 固定指定**
```csharp
[FixConnection("secondary")]  // 此 Action 固定使用 secondary 連線
public IActionResult Report()
{
    var vm = Wtm.CreateVM<ReportListVM>();
    return PartialView(vm);
}
```

**方式 2：動態切換（同一 Action 依條件選擇）**
```csharp
// 在 Controller 中手動切換
Wtm.CurrentCS = "secondary";
var vm = Wtm.CreateVM<EmployeeListVM>();

// 或在 ViewModel 中切換
protected override void InitVM()
{
    DC = Wtm!.CreateDC(cskey: "secondary");
}
```

### Q5: Console 應用使用 WTM
```csharp
var services = new ServiceCollection();
services.AddWtmContextForConsole(new ConfigurationBuilder()
    .AddJsonFile("appsettings.json").Build());

var provider = services.BuildServiceProvider();
var wtm = provider.GetRequiredService<WTMContext>();
var vm = wtm.CreateVM<EmployeeListVM>();
vm.DoSearch();
Console.WriteLine($"共 {vm.Searcher.Count} 筆");
```

### Q6: 如何自訂登入頁面？
```csharp
// 1. 建立自己的 LoginController
[Public]
public class LoginController : BaseController
{
    [ActionDescription("登入")]
    public IActionResult Login() => View();

    [HttpPost]
    [ActionDescription("登入")]
    public async Task<IActionResult> Login(LoginVM vm)
    {
        var user = await vm.DoLoginAsync();
        if (user == null)
            return View(vm);  // 驗證失敗，重新顯示表單

        return Redirect("/Home/Index");
    }
}

// 2. 在 appsettings.json 設定登入路徑
// "JwtOptions": { "LoginPath": "/Login/Login" }
```

### Q7: Grid 列表怎麼加自訂按鈕？
```csharp
protected override List<GridAction> InitGridAction()
{
    return new List<GridAction>
    {
        // 標準按鈕
        this.MakeStandardAction("Employee", GridActionStandardTypesEnum.Create, "新增", ""),

        // 自訂按鈕 — 開啟對話框
        this.MakeAction("Employee", "Approve", "審批", "審批",
            GridActionParameterTypesEnum.SingleId)
            .SetDialogTitle("審批").SetWidth(600),

        // 自訂按鈕 — 直接執行 AJAX（不開對話框）
        this.MakeAction("Employee", "Export", "匯出報表", "",
            GridActionParameterTypesEnum.NoId)
            .SetIsRedirect(false)
            .SetOnClickScript("doExport"),
    };
}
```

### Q8: 怎麼處理 M:N 多對多關聯？
```csharp
// 1. 建立中間表（標記 [MiddleTable]）
[MiddleTable]
public class EmployeeSkill : TopBasePoco
{
    public Guid EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public Guid SkillId { get; set; }
    public Skill? Skill { get; set; }
}

// 2. 在 CRUD VM 中加入 SelectedSkillIds
public List<string>? SelectedSkillIds { get; set; }

// 3. InitVM 中載入已選項
SelectedSkillIds = DC.Set<EmployeeSkill>()
    .Where(x => x.EmployeeId == Entity.ID)
    .Select(x => x.SkillId.ToString()).ToList();

// 4. View 中使用 checkbox
// <wt:checkbox field="SelectedSkillIds" items="@Model.AllSkills" />

// 5. DoAdd/DoEdit 中手動處理關聯（見 Section 4.2 範例）
```

### Q9: 如何實現軟刪除？
```csharp
// Model 繼承 PersistPoco（自帶 IsValid 欄位）
public class Employee : PersistPoco
{
    public string Name { get; set; } = "";
}

// 效果：
// - 刪除時 IsValid 設為 false（不是真刪除）
// - 所有查詢自動加 WHERE IsValid = true（Global Query Filter）
// - 需要看到已刪除資料：.IgnoreQueryFilters()
```

### Q10: 部署到生產環境的檢查清單

| 項目 | 動作 |
|------|------|
| `IsQuickDebug` | 設為 `false` |
| `JwtOptions.SecurityKey` | 替換為高強度隨機金鑰（≥32 字元） |
| 連線字串 | 使用正式 DB，不用 SQLite/Memory |
| HTTPS | 啟用 HTTPS + HSTS |
| 檔案上傳 | 設定合理的 `UploadLimit` |
| 密碼 | 確認所有管理員帳號已改密碼 |
| 代碼生成器 | Release 模式自動隱藏（`[DebugOnly]`） |
| 日誌 | 設定 Serilog/NLog 寫入持久化儲存 |
| 資料庫遷移 | `dotnet ef database update` 確認 schema 最新 |

---

## 附錄

### A. 檔案路徑速查

| 路徑 | 用途 |
|------|------|
| `src/WalkingTec.Mvvm.Core/BaseVM.cs` | ViewModel 基底 |
| `src/WalkingTec.Mvvm.Core/BaseCRUDVM.cs` | CRUD VM |
| `src/WalkingTec.Mvvm.Core/BasePagedListVM.cs` | 列表 VM |
| `src/WalkingTec.Mvvm.Core/WTMContext.cs` | 請求上下文 |
| `src/WalkingTec.Mvvm.Core/DataContext.cs` | EF Core 封裝 |
| `src/WalkingTec.Mvvm.Core/Analysis/` | Analysis 引擎 |
| `src/WalkingTec.Mvvm.Mvc/FrameworkServiceExtension.cs` | 啟動註冊 |
| `src/WalkingTec.Mvvm.Mvc/_AnalysisController.cs` | Analysis API |
| `src/WalkingTec.Mvvm.Etl/` | ETL 模組 |
| `src/WalkingTec.Mvvm.Etl/Pipeline/` | ETL Pipeline 核心（Executor, Loaders, Watermark） |
| `src/WalkingTec.Mvvm.Etl/Scheduling/` | Quartz 排程（SchedulerService, ProgressTracker） |
| `src/WalkingTec.Mvvm.Etl/Controllers/` | ETL 管理 UI（Job CRUD, Monitor, RunLog） |
| `src/WalkingTec.Mvvm.Etl/Models/` | ETL 資料模型（JobDefinition, RunLog, Progress） |
| `src/WalkingTec.Mvvm.Core/Auth/` | JWT Token 服務 |
| `src/WalkingTec.Mvvm.Core/Cache/` | Lookup Cache 服務 |
| `src/WalkingTec.Mvvm.Core/PasswordHashHelper.cs` | 密碼雜湊（PBKDF2） |
| `test/WalkingTec.Mvvm.Test.Mock/` | 測試 Mock |
| `docs/` | 文件 |
| `version.props` | 版本號 |
| `CHANGELOG.md` | 變更日誌 |

### B. 版本歷史

詳見 `CHANGELOG.md`。

**10.5.1（2026-05-13）摘要 — infra-only release，無 src/* 程式碼變更：**

- CI / NuGet publish 完全遷至 Gitea（GitHub Packages / GitHub Actions Marketplace / github-archive remote 全部停用）
- `scripts/publish-to-gitea.sh` 新增 local fallback（`--suffix` / `--dry-run`；dry-run 時 token 已 mask）
- CI publish secret 改為 `PAT_TOKEN`
- `scripts/release-github-package.sh` 重命名為 `release-gitea-package.sh`，內部由 `gh` 改為 `curl`
- `common.props` `RepositoryUrl` / `PackageProjectUrl` 指向 Gitea repo

**10.5.0（2026-04-26）摘要 — 31 個新增能力，零行為破壞：**

| 區塊 | 新增 | 對應章節 |
|------|------|------|
| Middleware / 安全 / 可靠度 / 觀測 | `[WtmIpAllowList]` / `[WtmNoCache]` / `[WtmCacheControl]` / `UseWtmMaintenanceMode` / `[WtmDeprecated]` / `UseWtmServerTiming` / `IWtmFeatureFlags` + `[WtmFeatureGate]` / `[WtmIdempotent]` + `UseWtmIdempotency` / `UseWtmETag` / `UseWtmRequestTimeouts` / `WtmDataSeeder` | §10.13–§10.24 |
| 安全 | `WtmCspMode` 三態 + `FrameAncestors` + `UseWtmCspReport` + `SecureHeadersOptions.Overwrite` | §10.13–§10.14 |
| Analysis Mode | Sort + TopN / 9 個相對日期 token / DistinctCount / HavingFilters / GrandTotal / AnalysisLimits / CompareWith / Insights / Drill-through / CSV+Excel 總計列 | §7.11–§7.19 |
| ETL | `LoadMode.Replace` / ColumnMappings / `IEtlSchemaService` / 批次重試 / VM 驗證 / 三步驟精靈 / **可視化儀表板** | §8.11–§8.17 |
