# CI Operations

> **適用版本**：10.5.1+
> **最後更新**：2026-06-21
> **CI 平台**：Gitea Actions（self-hosted at `mac-mini.tailde842d.ts.net`）。**至少三個已註冊 runner**（見下方「Runner 拓撲」，2026-07-29／#885 更正——原記錄的兩個之外還有一個先前沒記到的 `azure-overflow-runner`）：WTM 的 `ubuntu-latest` jobs 主要跑在本機 Docker `act_runner`（`local-runner`），但也可能被 Gitea 排到 `azure-overflow-runner`；另有一個 Homebrew runner 服務其他專案。

本文件涵蓋 WTM CI 工作流總覽、Gitea Actions 與 GitHub Actions 的四大已知不相容點，以及排錯 SOP。完整修復脈絡見 [Issue #11](https://mac-mini.tailde842d.ts.net/chiu0831/WTM/issues/11) / [PR #12](https://mac-mini.tailde842d.ts.net/chiu0831/WTM/pulls/12)。

---

## 工作流總覽

| 檔案 | 觸發 | 主要 jobs |
|------|------|-----------|
| `.github/workflows/ci-build.yml` | push + PR | `build-and-test`、`js-test`、`release-tooling-test`、`security-scan` |
| `.github/workflows/e2e-test.yml` | push + PR（path filter：`src/**`、`demo/**`、`test/e2e/**`） | `e2e`（Python + Playwright） |
| `.github/workflows/integration-test.yml` | push + PR（含 SQL Server container） | `integration-test` |
| `.github/workflows/publish-nuget.yml` | `push` tag `v*` + `workflow_dispatch` | NuGet pack→Gitea registry **＋ GitHub mirror sync（清洗 + go-forward push）＋ 建立 GitHub Release＋推 GitHub Packages**（見「Runner 拓撲與發版」） |

Gitea Actions 直接讀 `.github/workflows/*.yml` — 語法與 GitHub Actions 相容、不必搬到 `.gitea/`。但有些 action 版本（特別是 v4+ artifact action）不支援 Gitea 的 GHES API，見下方四大不相容點。

---

## 四大已知不相容點

### 1. `actions/upload-artifact@v4` 在 Gitea 拋 `GHESNotSupportedError`

**症狀**：
```
::error::@actions/artifact v2.0.0+, upload-artifact@v4+ and download-artifact@v4+
are not currently supported on GHES.
```
任何 artifact upload step 在有檔案可上傳時 hard-fail，整個 job conclusion 被標 failure，即便 build/test 全 pass。upload steps 設有 `if: always()`，因此只要 job 產出 artifact，上傳失敗就會污染本來綠燈的 job。

**修法**（已套用，見 [#471](https://mac-mini.tailde842d.ts.net/chiu0831/WTM/issues/471)）：在每個 `upload-artifact@v4` step 加上 `continue-on-error: true`，讓 Gitea 端的 GHES 錯誤靜默失敗，不污染 job conclusion：

```yaml
- name: Upload test results
  uses: actions/upload-artifact@v4
  if: always()
  continue-on-error: true
  with:
    name: test-results
    path: "TestResults/**/*.trx"
```

`continue-on-error: true` 的意義：upload step 失敗不再標記整個 job，CI 信號回歸真實 test 結果。artifact 是診斷用途，不是 build gate。

**三個工作流的 upload-artifact 步驟清單（已全部套用）**：

| 工作流 | Step name | 修法 |
|--------|-----------|------|
| `ci-build.yml` | Upload test results | `continue-on-error: true` ✅ |
| `ci-build.yml` | Upload coverage report | `continue-on-error: true` ✅ |
| `e2e-test.yml` | Upload screenshots | `continue-on-error: true` ✅ |
| `e2e-test.yml` | Upload JUnit XML report | `continue-on-error: true` ✅ |
| `integration-test.yml` | Upload test results | `continue-on-error: true` ✅ |

**追蹤**：未來 Gitea act_runner 原生支援 artifact v4+ protocol 後可移除此 workaround。

### 2. `dotnet tool install -g` 後 `$PATH` 不自動延伸

**症狀**：
```
- name: Install ReportGenerator
  run: dotnet tool install -g dotnet-reportgenerator-globaltool

- name: Generate coverage report
  run: reportgenerator -reports:... -targetdir:...
  # → /var/run/act/workflow/6: line 3: reportgenerator: command not found
  # → exit code 127
```

`dotnet tool install -g` 把 binary 放在 `$HOME/.dotnet/tools/`，但這個目錄**不會**自動加進下一個 step 的 `$PATH`（與 GitHub Actions 行為一致）。

**修法**：install step 同時把 path 寫入 `$GITHUB_PATH`，runner 會在下一個 step 自動加進 `$PATH`：

```yaml
- name: Install ReportGenerator
  run: |
    dotnet tool install -g dotnet-reportgenerator-globaltool
    echo "$HOME/.dotnet/tools" >> "$GITHUB_PATH"
```

注意是寫**檔案** `$GITHUB_PATH`、不是 `export PATH`（後者只在當前 step 有效）。

### 3. `bash -e -o pipefail` + `grep | head -1` SIGPIPE 陷阱

**症狀**：
```bash
LINE_COV=$(grep -oP 'line-rate="\K[^"]+' TestResults/CoverageReport/Cobertura.xml | head -1)
# → exit code 141（SIGPIPE）
```

Runner 的 shell 預設 `bash --noprofile --norc -e -o pipefail`。`head -1` 讀完第一行就關 pipe，`grep` 仍在嘗試寫入 → SIGPIPE → exit 141。`pipefail` 把這個失敗傳給整個 pipeline。

**修法**：用 `grep -m N` 取代 `grep | head -N` — grep 自己讀到 N 個就停、不靠 pipe 截斷：

```bash
# BAD
LINE_COV=$(grep -oP 'line-rate="\K[^"]+' Cobertura.xml | head -1)

# GOOD
LINE_COV=$(grep -oP -m 1 'line-rate="\K[^"]+' Cobertura.xml)
```

這是所有 CI scripts 都該注意的 idiom — 不止 Gitea Actions。

### 4. Job conclusion 全標 `failure` 的誤導

**症狀**：當某個 step 失敗後，Gitea Actions API 把該 job 內**所有** step（含已成功的 `checkout`、`setup-dotnet`）的 `conclusion` 都標成 `failure`。從 API 看像是整個 job 完全壞掉。

```json
{
  "name": "build-and-test",
  "steps": [
    {"name": "Run actions/checkout@v5", "conclusion": "failure"},  // 其實成功
    {"name": "Run actions/setup-dotnet@v5", "conclusion": "failure"},  // 其實成功
    {"name": "Test with coverage", "conclusion": "failure"},  // 其實成功
    ...
  ]
}
```

**正確的判讀方式**：從 raw log 找字串：
```bash
grep -nE "❌  Failure - Main " <job-log>
```
這才是真正失敗的 step。`conclusion` 欄位**不可靠**作為單一信號。

GitHub Actions 的 API 不是這個行為（GitHub 各 step 各自獨立 conclusion）。

---

## 重複觸發：feature-branch push 曾讓整套測試並行跑兩遍（#640，2026-07-10 修正）

`ci-build.yml` 過去同時掛在 `push: [dotnet8, dotnet10, "feature/**", "feat/**"]` 與
`pull_request: [dotnet8, dotnet10]`。專案流程是「推 `feat/**` 分支 → 對 `dotnet10` 開 PR」，
兩個 trigger 因此對**同一個 commit** 各跑一次完整測試套件，**並行**擠在單一 self-hosted runner 上。

**證據**：SHA `ea0e6b9d7` 的 task 清單中 `release-tooling-test` / `build-and-test` / `js-test`
各出現兩次（9308/9311、9309/9312、9310/9313）。而且三次觀察一致 —— **push run 敗、PR run 過**：

| SHA | push-event `build-and-test` | pull_request-event |
|-----|-----------------------------|--------------------|
| `ea0e6b9d7` | ❌ `Test host process crashed`（Core.Test，1644/4218） | ✅ |
| `d77d6a468` | ❌ `cannot start a transaction within a transaction`（#629） | ✅ |
| `c23873c79` | ❌ | ✅ |

這很可能就是長年「SQLite 併發測試很 flaky」（#620 `database is locked`、#629 `tx-in-tx`、
以及新出現的 testhost 原生崩潰）的**共同觸發器**：測試本身不 flaky，是 runner 被自己塞爆。
佐證：這些簽名在本機一律無法重現（#629 曾跑 96 輪蓄意 4-6 路並行負載仍全綠）。

**修法**：從 `push` trigger 移除 `feature/**` / `feat/**`（PR trigger 已完整覆蓋 feature 分支）。
**不可**改用 `concurrency:` group —— push 與 pull_request 事件的 `github.ref` 不同
（`refs/heads/feat/x` vs PR merge ref），彼此不會 dedupe，反而會取消正當的 dotnet10 in-flight run。

**SOP 影響**：看到 commit 的 combined status 是 failure 時，先分辨失敗的是 push-event 還是
pull_request-event 的 job。修正後 feature 分支只會有 pull_request-event 的 job；若舊 commit
仍帶著 push-event 的紅燈，那是歷史雜訊。

---

## e2e 三條 matrix leg 並行導致的時序性失敗（#885，2026-07-29 修正）

`#837` 為 `e2e-test.yml` 加了第三條 matrix leg（`island`）之後，同一天內出現五種不同的
時序敏感失敗（`Page.screenshot` timeout、`Target crashed`、並行競態測試、TOCTOU 測試、
testhost 崩潰），分散在互不相關的 PR 上——形狀都一樣：**一個對時間做了假設的測試，在
資源競爭下假設破裂**，不是測試邏輯壞掉。

**量測**（本票在 `ci/885-runner-capacity` 分支上用 `workflow_dispatch` 對 `e2e-test.yml`
觸發的即時 run 5835，`docker stats` 現場觀察；另對照 PR #881 當天的 e2e run 5833）：

- act_runner 背後的 Docker Desktop VM 硬上限是 **4 CPU / 3.8GiB RAM**（`docker info`），
  與 Mac mini 主機實際的 10 CPU / 16GB **無關**——act_runner 透過 bind-mount 的
  `docker.sock` 用 Docker Desktop VM 自己的 daemon 開 job 容器，不是主機的。
- run 5835 三條 leg 的 job 在 6 秒內全部被 Gitea 排入執行（用 job 的 `runner_id`/
  `runner_name` 查證，不是猜的）。這台 Gitea 實例其實掛了**三個**已註冊 runner，不是
  「Runner 拓撲」小節記錄的兩個：`local-runner`（本機 Docker，`capacity: 2`）、
  `bms-macos-runner`（Homebrew，服務 BMS 等其他專案）、以及先前這份文件沒記到的
  `azure-overflow-runner`（本機看不到它的容器，規格未知）。**哪條 leg 分到哪個
  runner 是 Gitea 排程當下決定，`e2e-test.yml` 本身完全不宣告**——run 5835 是
  baseline+island 分到 `local-runner`、killswitch 分到 `azure-overflow-runner`；但
  下面「修法」驗證用的 run 5837 卻是 baseline+killswitch 同時分到 `local-runner`。
  **不能假設三條 leg 會自然分散到不同機器**，也不能假設固定是哪條 leg 分到哪個
  runner。
- 光是被排到 `local-runner` 的那兩條 leg，各自跑一個 dotnet demo process + 一個
  headless Chromium，`docker stats` 現場量到在啟動後 30 秒內就吃到約 **360% CPU**
  （VM 400% 上限的 9 成）與 **2.85GiB 記憶體**（3.8GiB 上限的 75%）——這是本機直接
  量到的數字，不是推算，也還沒把 `local-runner` 上可能同時排進來的其他 workflow
  job（`ci-build.yml`、`mutation-gate.yml`）算進去。這就是 #885 列的失敗形狀：
  `Page.screenshot` timeout（CPU 被搶到來不及 compositing）與 `Target crashed`
  （Chromium 被 OOM kill）。
- e2e 測試腳本（`test/e2e/wtm_e2e_tests.py`）目前對每個 TC 都無條件拍照（79 處
  `page.screenshot(...)`，其中 13 處 `full_page=True`），不分成功失敗——這會放大每條 leg
  的資源用量，但量測顯示真正的瓶頸是「同時幾個瀏覽器+dotnet process 在跑」而非拍照本身；
  改成只在失敗時拍照是可考慮的後續優化，未在本票處理範圍內（另開 issue 追蹤）。

**修法**：讓三條 leg 依序跑而非搶著並行。第一次嘗試是 `.github/workflows/e2e-test.yml`
單一矩陣 job 加 `strategy.matrix.max-parallel: 1`——**這個設定完全沒有效果**：用
`workflow_dispatch` 重跑後，三條 leg 的 job container 依然在 6 秒內全部啟動、整段並行。
這是已知的 Gitea Actions 上游缺陷（[go-gitea/gitea#35561](https://github.com/go-gitea/gitea/issues/35561)：
"Cannot make steps run sequentially with matrix and max-parallel = 1"），不是設定寫錯。
最終改法：拆掉 matrix，改成三個獨立 job（`e2e-baseline` / `e2e-killswitch` / `e2e-island`），
用 `needs:` 串接——這是本 repo 其他 workflow 已經在用、確定有效的基本功能（`ci-build.yml`
的 `security-scan` needs `build-and-test`；`mutation-gate.yml` 的 `mutants` needs
`changes`），現場重跑驗證確實會依序執行而非並行。下游兩個 job 都帶 `if: always()`，維持
原本 matrix `fail-fast: false` 的語意——某條 leg 失敗不會連帶跳過後面的 leg。代價是
workflow 總時長增加約一條 leg 的時間（2–4 分鐘），換取可預測、不再被資源競爭污染的結果。
**沒有**調大 timeout——那只會延後問題、讓 CI 變慢（issue 本文已排除）；也沒有逐支修測試的
時間假設——除了已經修好的 TC-33（`ccbcbe532`，拿掉 `force=True` 讓 Playwright 自己等
layout 穩定）之外，其餘四種失敗的根因是資源競爭本身，逐支修無法解決同時開太多瀏覽器這
件事。

**SOP 影響**：e2e workflow 現在會比 #837 之前慢；不要因為「怎麼變慢了」重新把三條 leg
改回並行——那正是 #885 五種失敗的共同觸發器。**也不要在這個 repo 的其他 workflow 用
`strategy.matrix.max-parallel` 期待它限制並行度**——目前這個 Gitea 版本會靜默忽略它。
若未來 host 容量提升、`local-runner` `capacity` 調整、或 Gitea 修好 #35561，可重新評估
是否要把這三個 job 併回矩陣。

---

## 排錯 SOP

當 PR 的 CI conclusion 是 failure：

### 1. 取得 workflow run ID

```bash
source $HOME/.gitea-token
curl -s -H "Authorization: token $GITEA_TOKEN" \
  "https://$GITEA_HOST/api/v1/repos/chiu0831/WTM/actions/runs?head_sha=<sha>&limit=5" \
  | python3 -c "import sys,json; d=json.load(sys.stdin); [print(r['id'], r['name']) for r in d['workflow_runs']]"
```

### 2. 找 failed job ID

```bash
curl -s -H "Authorization: token $GITEA_TOKEN" \
  "https://$GITEA_HOST/api/v1/repos/chiu0831/WTM/actions/runs/<run-id>/jobs" \
  | python3 -c "import sys,json; d=json.load(sys.stdin); [print(j['id'], j['name'], j['conclusion']) for j in d['jobs']]"
```

### 3. 抓 log 找真正失敗點（不要看 step conclusion）

```bash
curl -s -H "Authorization: token $GITEA_TOKEN" \
  "https://$GITEA_HOST/api/v1/repos/chiu0831/WTM/actions/jobs/<job-id>/logs" -o /tmp/job.log

# 找真正失敗的 step（不是看 step conclusion）
grep -nE "❌  Failure - Main |::error::" /tmp/job.log

# 找 test pass / fail
grep -nE "Test Run Successful\.|Failed!|Total tests:" /tmp/job.log
```

### 4. 確認是 infrastructure 還是 code regression

對照本文「四大已知不相容點」逐一比對 — 若 failure pattern 是其中之一 → infrastructure 問題，不是你的 PR 引入的 regression。

若不是其中之一 → 看 test output 找真正的 code regression。

---

## 本機 reproduce CI 流程

90% 的 CI 失敗其實本機能先 reproduce、節省一輪 round-trip：

### 本機跑 build-and-test

```bash
# 1. restore + build
$HOME/.dotnet/dotnet restore ci.slnf
$HOME/.dotnet/dotnet build ci.slnf --no-restore -c Release

# 2. test with coverage
$HOME/.dotnet/dotnet test WalkingTec.Mvvm.sln -c Release \
  --no-build \
  --filter "TestCategory!=Integration" \
  --logger "trx;LogFileName=test-results.trx" \
  --collect:"XPlat Code Coverage" \
  --settings coverlet.runsettings \
  --results-directory ./TestResults

# 3. coverage report（本機要先裝）
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator \
  -reports:"TestResults/**/coverage.cobertura.xml" \
  -targetdir:"TestResults/CoverageReport" \
  -reporttypes:"Html;lcov;Cobertura;Badges"
```

### 本機跑 e2e

```bash
# 啟動 demo app
cd demo/WalkingTec.Mvvm.Demo
$HOME/.dotnet/dotnet run --urls http://localhost:52837 &

# 跑 e2e
cd test/e2e
pip install -r requirements.txt
playwright install chromium --with-deps
python wtm_e2e_tests.py --headless --report results/junit.xml
```

### 本機跑 integration-test（需要 SQL Server）

```bash
# 啟 SQL Server container（同 CI image）
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=YourStr0ng!Pass" \
  -p 1433:1433 -d mcr.microsoft.com/mssql/server:2022-latest

sleep 15  # 等 SQL Server 冷啟動

export WTM_TEST_MSSQL="Server=localhost,1433;Database=WtmIntegrationTest;User Id=sa;Password=YourStr0ng!Pass;TrustServerCertificate=True"
$HOME/.dotnet/dotnet test test/WalkingTec.Mvvm.Integration.Test \
  -c Release --filter "TestCategory=Integration"

docker stop $(docker ps -q --filter "ancestor=mcr.microsoft.com/mssql/server:2022-latest")
```

---

## Runner 拓撲與發版（2026-06-20 更新）

### Runner 拓撲（重要：有兩個 runner）

Mac-mini 上全部跑在 **Docker**（`/Volumes/T7/dockerdata-binds/gitea/`）：Gitea server（`gitea/gitea:1.26.1-rootless`）+ `gitea-db`（postgres）+ runner。

| Runner | 形式 | labels | 服務對象 | capacity | config |
|--------|------|--------|----------|----------|--------|
| `local-runner` | Docker `gitea/act_runner`（跑 `catthehacker/ubuntu` 容器） | `ubuntu-latest` / `ubuntu-22.04` / `ubuntu-20.04` | **WTM**（所有 workflow 都用 `runs-on: ubuntu-latest`） | **2** | `/Volumes/T7/dockerdata-binds/gitea/data/runner/config.yaml` |
| `bms-macos-runner` | Homebrew `gitea-runner` | `self-hosted:host` / `macos:host` | 其他專案（BMS 等，host 直跑） | 3 | `/opt/homebrew/etc/gitea-runner/config.yaml` |
| `azure-overflow-runner` | 未知（不在這台 Mac mini 的 `docker ps` 裡；規格、config 位置未查） | `ubuntu-latest` / `ubuntu-24.04` / `ubuntu-22.04` | 未知——名稱暗示遠端/雲端 overflow 容量 | 未知 | 未查 |

> **修正（#885，2026-07-29）**：上面「有兩個 runner」是舊資訊。查 `action_runner` 表
> （`docker exec gitea-db psql -U gitea -d gitea -c "SELECT id,name,agent_labels FROM
> action_runner;"`）才發現第三個已註冊、且 `last_online` 顯示活躍的 runner。因為它的
> label 也含 `ubuntu-latest`，WTM 任何一個 `runs-on: ubuntu-latest` 的 job 都可能被
> Gitea 排到它身上，而不是 `local-runner`——`e2e-test.yml` 的三條 leg 分到哪個 runner
> 因執行而異（見下面 #885 小節的實測）。這裡只記錄「它存在」，規格與用途待補；下次
> 排查 runner 容量問題時先查這張表，不要只看 `docker ps`。

- **WTM CI 的吞吐瓶頸是 Docker `local-runner` 的 `capacity`（目前 2）**——這句話現在只
  精確描述「job 落在 `local-runner` 上時」的情形；`azure-overflow-runner` 的容量未知，
  不能假設它比較寬裕或比較緊繃。大量 PR 連續 merge 時 job 會排隊；`security-scan`
  （`needs: build-and-test`）會排在最後，可能 pending 很久 → 看起來像「卡住」。
- 讀內部狀態：`docker logs gitea`、`docker logs gitea-actions-runner`、`docker ps`。
- **無依賴變更（沒動 `Directory.Packages.props` / `src` 的 `.csproj`）的 PR**：可只等 `build-and-test` 綠就合併 —— `security-scan` 對無依賴變更是確定性綠燈（不可能冒出新 CVE）；runner 真正塞爆時用本機 gate（`dotnet build` + `dotnet test` + `dotnet list --vulnerable`）替代。

### 發版（`publish-nuget.yml`）

由 `push` tag `v*`（或 `workflow_dispatch`）觸發，做四件事：

1. pack + push **6 個套件**到 Gitea NuGet registry（`.../api/packages/chiu0831/nuget`）：`Core`、`Mvc`、`TagHelpers.LayUI`、`WorkFlow`、`Etl`、`FileHandlers.S3`（`--skip-duplicate`，重跑安全）。
2. **GitHub mirror sync**：套 `.sync/` manifest（`github-replace` → `github-excludes` → `github-sanitize.sed`）清洗內網資訊，**go-forward**（不 force-push 改寫 public 歷史）push `dotnet10` 到 `github.com/cct08311github/WTM`。
3. **建立 GitHub Release**（`api.github.com/.../releases`，tag = `${github.ref_name}`）。
4. push NuGet 到 GitHub Packages。

> GitHub Packages / mirror **是活躍的**（每日同步的公開鏡像），不是停用 —— 舊版本文件曾誤記「已停用」。Gitea 仍是 source of truth；GitHub 端不直接 merge。

### ⚠️ 發版已知陷阱（Gitea 1.26.1）

- **Gitea Release 物件「不會」自動建立 —— 要手動補。** `publish-nuget.yml` 只自動建 **GitHub** Release；**Gitea** 的 `/releases` 頁面靠人工建（API：`POST /repos/chiu0831/WTM/releases`，body 用對應版本的 CHANGELOG 段落）。曾經從 v10.12.2 起漏補到 v10.13.0，造成 Gitea releases 頁面看似停滯。**每次 tag 後記得補 Gitea Release。**
- **`workflow_dispatch` 帶 tag ref 會回 HTTP 204 但「不建立 run」** —— Gitea 的 dispatch 對 tag ref 不生效（branch ref 也曾在 stuck 狀態下回 204 無 run）。要重跑 tag 工作流不能靠 dispatch。
- **⚠️ tag-trigger 卡死的真正根因 = tag-object 去重（postgres 層），`docker restart gitea` 也清不掉。** 推 release tag 後 `publish-nuget` 完全沒建 run，是因為 Gitea 以 `(tag-ref, tag-object-sha)` 去重 tag-push 事件，而這筆記錄在 **postgres**，**重啟 gitea 容器不會清除**。所以「刪除 + 重推同一個 tag」（即使重啟過 gitea）仍送出**相同的 annotated-tag-object sha** → 仍被去重 → 不建 run。**過去文件寫「要用新 commit」其實不必要——真正關鍵是新的 tag object，不是新的 commit。**
  - ✅ **可靠解法：推一個全新的 annotated tag object（同一個 release commit 即可）。** `git tag -a` 會用當下時間戳產生**新的 tag-object sha**，繞過去重：
    ```bash
    git tag -d v10.13.5
    git push origin :refs/tags/v10.13.5            # 刪遠端
    git tag -a v10.13.5 <release-commit> -m "..."  # 重建 → 新 timestamp → 新 object sha
    git push origin v10.13.5                        # publish-nuget run 約 30s 內出現
    ```
    （2026-06-21 v10.13.5 實證：原 tag push + workflow_dispatch + restart+同物件重推 = 0 run；全新 `git tag -a` 重建 → 立即觸發 publish run 6218。**不需要重啟 gitea。**）
- 想本機發 Gitea 套件（繞過 runner，**只發 Gitea、不含 GitHub Packages / mirror sync**——那兩者是 CI 專屬，靠 `.sync/` manifest + `GH_MIRROR_PAT`）：`scripts/publish-to-gitea.sh`（或手動 `dotnet pack` 六個專案 + `dotnet nuget push "*.nupkg" --source gitea --skip-duplicate`）。優先修好 CI 觸發，本機發只當最後 fallback。

完整 release 流程見 [`docs/wtm-developer-manual.md`](./wtm-developer-manual.md) 與 [`CHANGELOG.md`](../CHANGELOG.md)。

---

## 相關文件

- [`docs/production-readiness.md`](./production-readiness.md) — production readiness 評估
- [`docs/dependency-management.md`](./dependency-management.md) — 套件版本與 vulnerability gate
- [`docs/getting-started.md`](./getting-started.md) — 環境建置
- [Gitea Actions 官方文件](https://docs.gitea.com/usage/actions/overview) — Gitea CI 平台說明
- [act 專案](https://github.com/nektos/act) — Gitea Actions 內部使用的 runner
