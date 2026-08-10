# 整合測試遷移到 `bms-verify-vm` — 設計文件（#1068）

> 狀態：**設計中，尚未實作。** 本文件是 #1068 的方案書，供 review 後才進入實作。
> 授權者 Jun（2026-08-09 23:33 選定方案、23:47 批准開工），執行者 ops session。

## 1. 背景與已生效的約束

Jun 2026-08-09 裁示三條，都已生效：

1. **本機不啟動 SQL Server，本機資源不足。需要 SQL Server 都去 `bms-verify-vm`。**
2. `bms-verify-vm` **按需開啟**，不常態開啟。
3. `vm-gitea-ci-runner` 由 Gitea CI 控制是否開啟，**不常態開啟**。

過渡處置已完成（#1070 / PR #1071，已 merge）：`integration-test.yml` 的 `push` 觸發已移除，`services:` 與全部測試 step **刻意保留未刪**，供本專案直接複用。目前 SQL Server 相關整合行為**沒有任何自動驗證**，該空窗已記載於 `docs/production-readiness.md`。

## 2. 已查證的事實

以下皆為實查，非推論。查證時間 2026-08-09 23:45–23:50。

### 2.1 `bms-verify-vm`

| 項目 | 值 |
|---|---|
| OS | **Windows Server 2022 Datacenter (g2)** |
| Size | `Standard_B4ms` |
| Region | eastasia |
| Resource group | `BMS-VERIFY-RG` |
| 目前狀態 | `VM deallocated` |
| Tailscale | 離線約 5 天（IP 曾為 `100.106.29.50`，**回來後是否照舊未複驗**） |

### 2.4 P1a 探測結果（2026-08-10 18:40，已執行）

腳本 `claude-session/scripts/probe-bms-verify-vm.ps1`（純唯讀），由 BMS session 在其租約窗口內代跑。8 項全數完成，**PASS=2 FAIL=4 INFO=2**。

| # | 項目 | 判定 | 發現 |
|---|---|---|---|
| 1 | .NET SDK | **FAIL** | `dotnet --list-sdks` 空，**未安裝任何 SDK** |
| 2 | WTM clone / NuGet 快取 | INFO | 無 clone；**NuGet 快取已在**（1263 MB / 5844 檔） |
| 3 | 磁碟餘量 | **PASS** | C: 81.66/126.45 GB、D: 29.54/32 GB |
| 4 | git | **FAIL** | 未安裝或不在 PATH |
| 5 | SQL Server | **FAIL** | 無 `MSSQLSERVER`/`MSSQL$*` 服務、1433 未監聽、**只有 LocalDB** |
| 6 | Oracle | **PASS** | 21c XE，listener + 1521 正常，**XEPDB1 status READY** |
| 7 | NuGet sources | **FAIL** | 只有 nuget.org，**缺 Gitea source** |
| 8 | SqlClient 加密 | INFO | 未實測；Windows 原生 driver 預設 `Encrypt=True` 且驗憑證 |

**結果翻轉了兩個假設：**

**（a）Oracle 從「未複驗宣稱」升級為「已證實」。** Oracle 21c XE、`OracleServiceXE` 與 `OracleOraDB21Home1TNSListener` 皆 Running、`:::1521` 監聽、`lsnrctl status` 回 21.0.0.0.0、services 含 `XEPDB1` 且 **status READY**。`ORACLE_HOME=C:\app\azureuser\product\21c\dbhomeXE`。§7.2 的未複驗註記可以撤銷。

**（b）這台沒有可用的 SQL Server —— 只有 LocalDB。** BMS session 於同一輪的 D3 驗證中獨立證實：app pool 身分 `.\azureuser`，`BMS_Connections__0__Value` = `Server=(localdb)\MSSQLLocalDB;Database=BMS_Demo`。**LocalDB 不接受遠端連線，也不是 Windows 服務**，因此不能作為 act_runner host 模式的 DB backing。

> ### ⚠️ 一個倒置，影響排序
>
> **Oracle 的環境已就緒但沒有測試**（#1069：零 harness）；**SQL Server 的測試已存在**（9 個 `[TestMethod]`）**但環境不存在**。
>
> 兩邊各缺一半，而且缺的是不同的一半：
> - 走 SQL Server 路線 = **裝環境**（SQL Server instance + SDK + git + NuGet source），測試現成
> - 走 Oracle 路線 = **環境現成**，但要從零寫 harness
>
> 這台 VM 原本是為「SQL Server 驗證」指定的，實際上它現成可用的是 Oracle。排序時不要假設「先做 SQL Server 比較快」。

### 2.5 執行身分：`az vm run-command` 看不到 per-user 的 LocalDB

BMS session 在同一輪租約中發現（2026-08-10）：這台的 BMS app 跑在 **`azureuser` 的 LocalDB**，IIS app pool 身分是 `SpecificUser` = `.\azureuser`。

**`az vm run-command invoke` 以 SYSTEM 身分執行，看不到 `azureuser` 的 LocalDB 實例。** reseed 必須 SSH 進去用 `azureuser` 身分跑。

這對 P2 provisioning 有兩個直接影響，**在動工前就要決定，不能等裝完才發現**：

1. **自動化管道的身分問題。** 既有 overflow runner 的 provisioning 走 cloud-init（開機時 root），這台若要走 `az vm run-command` 自動化，**所有動作都是 SYSTEM**。安裝 SQL Server instance 本身沒問題（服務層級），但任何「以某個使用者身分驗證/連線」的步驟都會與現況的 per-user 模型衝突。
2. **act_runner 的服務身分需要對應的 DB login。** 即使裝了真正的 1433 instance，act_runner 以服務執行時的身分（SYSTEM 或專用服務帳號）**必須在 SQL Server 上有 login 且具 `CREATE`/`DROP DATABASE` 權限** —— `WalkingTec.Mvvm.Integration.Test` 對每個測試類別 `EnsureDeleted()`/`EnsureCreated()` 自己的資料庫（§7.1）。這不會自動成立，是 P2 要明確配置的一項。

> **換句話說：「裝一個 SQL Server」不等於「act_runner 連得上那個 SQL Server」。** 現況的 per-user LocalDB 模型正好示範了身分不匹配會長什麼樣 —— 一個能用的資料庫，配上一個看不到它的執行身分。

### 2.2 既有的 overflow runner（可複用的先例）

`vm-gitea-ci-runner` 已經在做「CI 按需喚醒 Azure VM」這件事：

- **Linux** VM，`Standard_E2s_v5`，`RG-GITEA-CI-RUNNER`，eastasia
- 喚醒者是 mac mini 上的 **autoscale controller**：`~/.config/ci-runner-controller/controller.py`，launchd label `com.openclaw.ci-runner-controller`
- 邏輯：輪詢 Gitea，若**本機 runner busy 且有 waiting jobs 且 Azure VM 為 deallocated** → `az vm start`（含 Spot 容量退避重試）；連續 N 個獨立週期偵測到 idle → `az vm deallocate --no-wait`
- 雙重收尾：Azure 側 auto-shutdown 排程（`shutdown-computevm-vm-gitea-ci-runner`，每日 **1800 UTC**，狀態 Enabled）＋本機 launchd 每 2 小時補一次
- **實測驗證**：本日 23:45:24 CST，PR #1071 merge 觸發 post-merge CI、本機 act_runner 有 2 個 in-flight task 時，該 VM 自動由 deallocated 轉為 running。機制確實在運作

> 這是本專案最重要的一項發現：**前置條件 2（CI 如何喚醒 deallocated 的 VM）與 3（用完自動 deallocate）已經有一份在生產中運作的實作**，不需要從零設計。

### 2.3 Branch protection

`dotnet10` 的 required contexts 只有 `Mutation Gate / gate (pull_request)`（ops 與 WTM session 各自複驗，數字一致）。`integration-test` **不是** required check，因此調整其 trigger 或 runner label 不會造成 required context 永久停在 `expected, waiting` 而鎖死 merge button（`mutation-gate.yml` 才有該約束，#838/#844 踩過）。

## 3. 核心設計問題：Windows vs Linux 容器

**這是本專案真正的難點，四個前置條件裡最難的一項，而它不在原本列出的四項裡。**

現行 `integration-test.yml` 用 Gitea Actions 的 `services:` 起 `mcr.microsoft.com/azure-sql-edge:latest`。`azure-sql-edge` 是 **Linux 容器**。而 `bms-verify-vm` 是 **Windows Server 2022**。

在 Windows 上跑 Linux 容器需要 Docker Desktop / WSL2 後端。這帶來三個問題：

1. **Docker Desktop 在 Windows Server 上的授權與自動化安裝**比 Linux 的 `apt install docker.io` 麻煩得多
2. **act_runner 在 Windows 上的容器 job 支援**與 Linux 不對等；`services:` 的實作路徑不同
3. **既有的 overflow runner 先例完全用不上** —— 那台是 Linux + docker + squid + iptables 的成熟組合（見 `azure-runner/cloud-init.yml`），照搬不到 Windows

因此**不建議在 `bms-verify-vm` 上重現 `services:` 容器模型**。

## 4. 方案

### 方案 A — Windows 原生 SQL Server + 非容器 job（建議）

在 `bms-verify-vm` 上**原生安裝 SQL Server**（Developer edition），act_runner 以 host 模式（非容器）執行測試，連本機 SQL Server 實例。

- ✅ 繞開 Windows/Linux 容器問題
- ✅ 與 BMS session 現行的手動驗證用法一致（那台本來就是為 BMS 的真機驗證而生）
- ✅ **可容納 Oracle**（見 #1069）—— Oracle 也可原生安裝，或用 Oracle XE；不受容器平台限制
- ⚠️ 需要新的 runner label（**不能用 `ubuntu-latest`**，那會落到本機 act_runner）
- ⚠️ 測試專案需確認在 Windows 上可執行（.NET 跨平台，但路徑/大小寫/連線字串需驗）

### 方案 B — 另建一台 Linux verify VM，沿用既有 overflow runner 模式

- ✅ 完全複用 `azure-runner/cloud-init.yml` 的成熟組合，`services:` 模型不用改
- ❌ **多一台 VM = 多一份成本**，與「不常態開啟」的節約意圖相反
- ❌ 與 Jun「需要 SQL Server 都去 `bms-verify-vm`」的指定不符（他指名了那一台）

### 方案 C — 在 `bms-verify-vm` 裝 Docker Desktop / WSL2 跑 Linux 容器

- ✅ `integration-test.yml` 的 `services:` 區塊幾乎不用改
- ❌ Windows Server 上的 Docker Desktop 授權與無人值守安裝複雜
- ❌ 巢狀虛擬化（Azure VM 內跑 WSL2）在 `Standard_B4ms` 上效能與支援度都是未知數，**未驗證**

**建議採方案 A。** 理由：唯一同時滿足「用 Jun 指名的那台」「不增加成本」「可容納 Oracle」三項的方案。

## 5. 與 VM 租約協定的整合（最容易被忽略的失敗模式）

`bms-verify-vm` 目前已有**兩個人工消費者**（BMS session、WTM session），共用鎖協定：
`/Volumes/T7/openclaw/shared/locks/README-bms-verify-vm.md`

**若 CI 能自行喚醒該 VM，CI 就是第三個消費者，必須也走同一把鎖。** 否則會出現：

> CI 喚醒 VM 開始跑整合測試 → 某個 session 依協定判定「我沒開這台、但它開著」→ 依租約規則 `az vm deallocate` → CI job 中途死亡，且死法看起來像基礎設施故障

controller 取鎖時的 `owner` 應寫為 `ci-controller`、`purpose` 寫觸發它的 run id，讓人一眼看出這台是被 CI 佔用而非某個 session 忘了關。

**取鎖與開機的時序必須是：取鎖成功 → `az vm start` → start 失敗立刻釋放鎖。** 若先 start 再取鎖，中間的窗口內 VM 已開著但無鎖，正好落進「開著且無鎖 = 被遺忘的開機」這個判定。此順序與租約協定的釋放順序（先 `az vm deallocate` 成功、才 `rm` 鎖檔）是對稱的：**兩邊都讓「鎖存在」嚴格涵蓋「VM 可能開著」的時段。**

> 此風險由 BMS session 於 2026-08-09 指出：租約協定目前隱含假設只有兩個人類代理消費者，且「VM 開著且無鎖」被當成異常。CI 加入後該假設不再成立，而這種誤判的排查成本很高 —— 現場會被誤判者自己的 `deallocate` 毀掉。

## 6. 成本控制

喚醒機制若做成「CI 觸發就開機」，在頻繁 push 下等同常態開啟，違反裁示。需要至少一項：

- 沿用 overflow controller 的「連續 N 個 idle 週期才 deallocate」＋ Azure auto-shutdown 雙重收尾
- 整合測試**不綁 push 觸發**，改為 nightly 批次或 release-gate 前一次跑完（與 #927 的發版就緒閘門天然契合）

## 7. 未決問題（實作前必須有答案）

1. **Windows 上的 act_runner 註冊方式與 label 命名** —— label 需能同時表達「這台」與「有哪些 DB provider」，供 #1069 的 Oracle 進來時擴充
2. **SQL Server 在 Windows VM 上的安裝與初始化如何自動化**（重建 VM 時要可重現，比照 `azure-runner/cloud-init.yml` 的角色）
3. **Tailscale 重連行為未複驗** —— VM 已離線 5 天，開機後是否自動回來、IP 是否仍為 `100.106.29.50`、從開機到可連的實際等待時間。BMS session 已同意在它下一次租約中測掉並回報
4. **`test/WalkingTec.Mvvm.Integration.Test` 是否能在 Windows 上執行** —— 未驗證。**其成本先前被 ops 低估為「順手 `dotnet test` 一次」，該估計不成立**（見 §7.1）

### 7.1 驗證這件事本身的陷阱 —— 成本與假綠燈

**成本更正**（BMS session 依 WTM session 的實查資料指出，2026-08-09）：該測試專案 `TargetFramework` 為 `net10.0`，且 `ProjectReference` 指向 `src/WalkingTec.Mvvm.Mvc`，因此需要 **.NET 10 SDK ＋整個 WTM repo ＋一次 NuGet restore**，不是拷一個 dll 過去就能跑；測試對每個測試類別 `EnsureDeleted()` / `EnsureCreated()` 自己的資料庫（`WtmIntTest_<類別名>`），因此需要 **CREATE / DROP DATABASE 權限**，不是既有 DB 的讀寫權就夠。資料庫只透過環境變數 `WTM_TEST_MSSQL` 注入。在一台 5 天沒開機、可能沒有 .NET 10 SDK 的 Windows VM 上，這是**獨立的一輪工作，不是順手**（量級估計 15–60 分鐘，看走哪條路）。

> ⚠️ **驗收標準必須是「9 Passed 且 0 Inconclusive/Skipped」，不接受 `Test Run Successful`。**
>
> `IntegrationTestBase.EnsureSqlServerAvailable()` 在資料庫探測失敗時呼叫 `Assert.Inconclusive`，而 **MSTest 的 Inconclusive 不算失敗** —— `dotnet test` 會 exit 0 並印出 `Test Run Successful`。**把連線字串指到不存在的主機，那 9 個測試會全部 Inconclusive，然後判定為通過。**
>
> CI 至今沒被這個騙到，是因為 `integration-test.yml` 有獨立的 `Wait for MSSQL ready` step 當 gate —— **保護來自那個 gate，不在測試裡**。手動在 VM 上跑沒有那個 gate，因此**手動驗證比 CI 更容易產生假綠燈**。
>
> 這與本 repo `CLAUDE.md` 已載明的「CI red does not mean failed」是同一類問題的鏡像：那裡是**真綠被顯示成紅**，這裡是**沒跑被顯示成綠**。後者危險得多。方案 A 若採 host 模式執行，**必須自帶一個等價於 `Wait for MSSQL ready` 的前置 gate**，不能只靠 `dotnet test` 的退出碼。
5. ~~provider 集合是否納入 Oracle~~ —— **已裁決，見 §7.2。不再是未決問題。**

### 7.2 provider 集合：Oracle 是硬需求（已裁決）

Jun 2026-08-09 23:38 裁示：**「WTM 必須完整支援 Oracle，BMS 有在使用。`bms-verify-vm` 上也有 oracle 環境。」**

三個直接後果，影響本專案的設計而非只是排程：

1. **runner label 必須帶 provider 維度，且要一次設計對。** `DBTypeEnum` 是 `{ SqlServer, MySql, PgSql, Memory, SQLite, Oracle, DaMeng }`；目前有實際下游需求的是 SqlServer 與 Oracle，但**設計時應假設集合會成長**，不要假設它是 `{SqlServer}` 或 `{SqlServer, Oracle}`。
2. **Oracle 在 WTM 目前是零 harness，不是「有測試但缺機器」。** `test/` 下搜 Oracle 只命中建置產物與一處附帶提及（#1069）。所以 **Oracle 那條 label 落地時還沒有測試專案可以綁** —— label 設計不能假設每個 provider 都對應一個既存的 test project。
3. ~~VM 上已有 Oracle 環境，但版本／edition／service name／listener 狀態／1521 是否可從 tailnet 連都未查~~ —— **P1a 已查證，見 §2.4**：Oracle 21c XE，listener 與 1521 正常，`XEPDB1` status READY。owner 的宣稱成立。

## 7.3 Windows 可執行性：已知的與仍未知的

WTM session 靜態掃過 `test/WalkingTec.Mvvm.Integration.Test`（2026-08-10 00:05），搜路徑分隔、OS 判斷、shell 呼叫、Linux-only 假設：**零實際路徑處理、零 OS 判斷、零 shell 呼叫**，命中全在 XML 註解與 `Assert.Inconclusive` 的說明文字裡。技術棧全跨平台（`net10.0`、MSTest、EF Core SqlServer、`Microsoft.Data.SqlClient`），資料庫只經環境變數 `WTM_TEST_MSSQL` 注入。

> **但這只證明「該專案沒有 Linux-only 假設」，不證明「它在 Windows 上跑得起來」。**
> 真正的未知在它的依賴鏈：`ProjectReference` 指向 `src/WalkingTec.Mvvm.Mvc`，所以要在 Windows 上 **restore + build 整條依賴鏈**（EF Core、Quartz、NPOI…）。那條不在測試專案裡，也還沒有人驗過。

兩個已知會咬人的具體點，列入 P1a 以免多花一輪：

- **restore 需要兩個 package source**（nuget.org + Gitea）。未做 source mapping 會出 `NU1507`；Windows 上 `NuGet.Config` 若缺 Gitea source，restore 會以一個**看起來與 Gitea 無關的 NuGet 錯誤**失敗（此誤導性症狀 `CLAUDE.md` 已載明）。
- **`Microsoft.Data.SqlClient` 在 Windows 的預設加密行為與 Linux 不同**。若連本機非 LocalDB instance 而憑證為自簽，可能需要 `TrustServerCertificate=True`。CI 現行連線字串已帶該參數，但那是連容器，不能直接沿用結論。

## 8. 分期建議

| 期別 | 內容 | 前置 |
|---|---|---|
| **P1a** | 開機一次，驗 tailscale 重連／IP／`az vm start` 到可連的等待時間；**環境探測**：`dotnet --list-sdks`（有無 .NET 10）、有無既存 WTM clone／NuGet 快取、磁碟餘量、`git` 是否存在、除 LocalDB 外有無監聽 1433 且接受遠端連線的 SQL Server instance、Oracle 的版本／edition／service name／`lsnrctl status`／1521 是否可從 tailnet 連（#1069 的 discovery 第一步） | 無（併入 BMS 下一次租約，成本接近零） |
| **P1a** ✅ | **已完成 2026-08-10**，結果見 §2.4 | — |
| **P2（新增，已成為第一順位）** | **VM provisioning**：裝 `git`、.NET 10 SDK、SQL Server instance（Developer edition）、設定 Gitea NuGet source。**這在 P1a 之前只是設計文件裡的一句話，現在是確定要做的工項** | P1a（已完成） |
| **P1b** | 真的跑一次 `dotnet test`，驗收採 §7.1 的「9 Passed 且 0 Inconclusive/Skipped」；**同時確認 `TrustServerCertificate` 設定**（見下方警告） | **P2** —— P1a 證實 SDK 與 git 皆缺，所以 P1b 不再可能「順手」搭任何租約 |
| P3 | 決定 provider 集合與 label 命名 | #1069；P1a 已證實 Oracle 環境就緒，決策依據比原本充分 |
| P4 | 註冊 Windows act_runner | P2、P3 |
| P5 | controller 擴充為同時管理兩台 VM，並接上租約鎖 | P4 |
| P6 | `integration-test.yml` 改 `runs-on` 與觸發策略；恢復自動覆蓋並更新 `production-readiness.md` | P5 |

> ⚠️ **P1b 的一條新前置，來自 P1a 探測 8：** Windows 原生 `Microsoft.Data.SqlClient` 預設 `Encrypt=True` 且驗證憑證，與 Linux 容器不同。若連的是自簽憑證的本機 instance，**連線會失敗**。
>
> 而連線失敗**正是觸發 `Assert.Inconclusive` 假綠燈的那條路徑**（§7.1）—— `dotnet test` 會 exit 0 並印 `Test Run Successful`，9 個測試全部 Inconclusive。**所以憑證設定錯誤在 P1b 會偽裝成成功。** 這是 §7.1 的驗收標準「9 Passed 且 0 Inconclusive/Skipped」必須逐字遵守、不可用退出碼替代的具體理由。

**排序變更說明**：原本 P1a 之後直接接 P1b（跑測試），前提是「SDK 可能已在、clone 成本可能低」。P1a 證實**SDK 完全沒裝、git 也沒裝**，所以 provisioning 從「P3 的一部分」提前成獨立的 P2，且 P1b 必須排在它之後。**P1a 的價值正在於此：它把一個原本排在後面、被假設為小工項的東西，證實為第一順位的阻塞點。**

## 9. 相關

- #1068 本專案 · #1070 / PR #1071 過渡處置（已完成）
- #1020 本機 mssql OOM（真失敗非假紅燈）；本機路徑移除後應一併處置
- #1069 Oracle 零測試覆蓋 —— 決定 provider 集合
- #927 發版就緒閘門 —— 整合測試改批次執行時的天然掛載點
- `azure-runner/README.md`（claude-session workspace）—— overflow runner 的 provisioning source
- `docs/azure-ci-runner-runbook.md`（claude-session workspace）—— controller 架構與已知失敗模式
