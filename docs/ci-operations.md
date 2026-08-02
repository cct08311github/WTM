# CI Operations

> **適用版本**：10.5.1+
> **最後更新**：2026-07-31
> **CI 平台**：Gitea Actions（self-hosted at `mac-mini.tailde842d.ts.net`）。**至少三個已註冊 runner**（見下方「Runner 拓撲」，2026-07-29／#885 更正——原記錄的兩個之外還有一個先前沒記到的 `azure-overflow-runner`）：WTM 的 `ubuntu-latest` jobs 主要跑在本機 Docker `act_runner`（`local-runner`），但也可能被 Gitea 排到 `azure-overflow-runner`；另有一個 Homebrew runner 服務其他專案。

本文件涵蓋 WTM CI 工作流總覽、Gitea Actions 與 GitHub Actions 的七大已知不相容點，以及排錯 SOP。完整修復脈絡見 [Issue #11](https://mac-mini.tailde842d.ts.net/chiu0831/WTM/issues/11) / [PR #12](https://mac-mini.tailde842d.ts.net/chiu0831/WTM/pulls/12)。

---

## 工作流總覽

| 檔案 | 觸發 | 主要 jobs |
|------|------|-----------|
| `.github/workflows/ci-build.yml` | push + PR | `build-and-test`、`js-test`、`release-tooling-test`、`security-scan` |
| `.github/workflows/e2e-test.yml` | push + PR（path filter：`src/**`、`demo/**`、`test/e2e/**`） | `e2e`（Python + Playwright） |
| `.github/workflows/integration-test.yml` | push + PR（含 SQL Server container） | `integration-test` |
| `.github/workflows/publish-nuget.yml` | `push` tag `v*` + `workflow_dispatch` | NuGet pack→Gitea registry **＋ GitHub mirror sync（清洗 + go-forward push）＋ 建立 GitHub Release＋推 GitHub Packages**（見「Runner 拓撲與發版」） |

Gitea Actions 直接讀 `.github/workflows/*.yml` — 語法與 GitHub Actions 相容、不必搬到 `.gitea/`。但有些 action 版本（特別是 v4+ artifact action）不支援 Gitea 的 GHES API，見下方七大不相容點。

---

## 七大已知不相容點

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

### 5. `actions/checkout@v5` 在 `pull_request` 事件只 checkout PR 自己的 head，不是 base+head 的 merge

**事實**：job log 的 checkout step 印出：
```
[command]/usr/bin/git checkout --progress --force refs/remotes/pull/<N>/head
```
Gitea 對 `pull_request` 事件 checkout 的是 **PR 分支自己的快照**（`refs/remotes/pull/N/head`）。**這跟 GitHub Actions 相反**：GitHub 對同一事件 checkout 的是 base 與 head 的 merge 結果，PR 分支落後 base 多少個 commit 都無所謂，CI 永遠看得到 base 上最新的內容。Gitea 不會——PR 分支比 base 舊多少，CI 就看不到 base 上比它新的東西。

**後果**（反直覺，值得記住）：
- merge 一個修法進 `dotnet10` 之後，**既有** PR 的 CI 不會自動看到它。
- 重新跑 checks、或 close/reopen PR **都沒用**——跑的還是同一個 `refs/remotes/pull/N/head`，Gitea 不會重新產生它。
- 唯一解法：對 PR 自己的分支 push 新 commit（rebase 或 merge base 進去），讓 Gitea 重新產生 `refs/remotes/pull/N/head`。

**排查問句**：PR 紅在一個「base 上明明已經修好」的斷言時，先問「這支 PR 的分支點，在 base 那次修法合併之前還是之後開的？」——之前，就是本項陷阱，不是新 regression。

**案例（#906）**：#882 合併後 `docs/production-readiness.md` 已把 #876 從「未修」節移除，`dotnet10` 上 `ProductionReadinessBaselineDriftTests863` 三支測試全綠；但開在 #882 之前的 PR #881、#904 仍各自紅在同一斷言（`Expected:<0>. Actual:<1>. ... lists #876 as unfixed`），因為它們的 checkout 停在合併前的快照——需要各自 rebase/merge 最新 `dotnet10` 並 push 才會變綠，不是在 `dotnet10` 上再改一次文件能解決的。

### 5b. `workflow_dispatch` 的守衛跟著 ref 走 —— 加守衛**不會**保護舊 ref（#1008，2026-08-03）

**事實**：Gitea Actions 對 `workflow_dispatch` 使用**被 dispatch 的那個 ref 上的 workflow 檔**，不是 default branch 的版本。

**後果，而且非常反直覺**：往 `dotnet10` 合併一道 workflow 守衛之後，**任何還停在守衛落地前的分支，從它 dispatch 仍然會執行沒有守衛的舊版**。

守衛的保護範圍不是「這個 repo」，是「workflow 檔已經更新的那些 ref」。

#### 這實際發生過

`publish-nuget.yml` 的三道分支守衛（`REF != refs/heads/dotnet10` 即中止、dispatch SHA 必須是 `origin/dotnet10` 祖先、tag 必須指向其祖先）由 `3b894df80`（#925，2026-07-31）加入，且已在 `10.21.0-rc.2` 內。

但下游（BMS #350）在本機 NuGet cache 中找到：

| 版本 | nuspec 的建置分支 |
|---|---|
| `10.21.0`（正式版號） | `refs/heads/docs/958-advisory-issue-keyed-corrections` |
| `10.21.0-rc.7` | `refs/heads/ci/925-release-gate` |
| `99.0.0-rc.5` / `99.0.0-smoketest` | `refs/heads/ci/925-release-gate` |

那顆 `10.21.0` **缺少 `dotnet10` 上的三個 security fix**（#824 Part 2、#961、#979）。SemVer 上 `10.21.0-rc.2 < 10.21.0`，所以任何「rc 驗過就升正式版」的下游會**靜默失去**那三個修復。

（`99.0.0-*` 是 #925 自己的煙霧測試，版號刻意選在一切之上以驗證一個從未生效的 pack 版本覆寫缺陷——那部分是預期的。）

#### 為什麼守衛沒擋住

那幾顆是守衛落地**之前**發出的。而更要緊的是：**守衛落地之後，那些舊分支仍然帶著舊的 workflow 檔**。

實測全部 112 條遠端分支：**全部都有 `publish-nuget.yml`，其中 81 條沒有分支守衛**——包括 `release/10.16.0`、`release/10.16.1`、`chore/release-10.15.0` 這種名字看起來就像 release 的。

#### 只有一支 workflow 有這個風險

八支 workflow 全部檢查（有無 `workflow_dispatch` × 有無守衛 × 有無對外副作用）：

**只有 `publish-nuget.yml` 同時具備 `workflow_dispatch` 與對外副作用**（`nuget push` + GitHub mirror push）。其餘七支雖然都能從任意分支 dispatch，但只跑測試與建置——最壞後果是浪費 runner 時間。**它們不需要守衛，加了反而是噪音。**

#### 因此修法不是「加更好的守衛」

守衛已經是對的，且已在主線上。要關閉的是**還存在的舊 ref**——也就是刪除已合併的陳舊分支（#1008），或收斂 Gitea 端的 dispatch 權限。

**這一點值得記住的原因**：面對「某個 workflow 可以被濫用」，直覺反應是去改 workflow。但當保護機制**跟著 ref 走**時，改 workflow 只保護未來的 ref，舊 ref 的攻擊面要靠刪除它們才會消失。

#### 一項方法論註記

第一次掃這 112 條分支時，用的 shell 迴圈**印出零筆**——看起來像「全部安全」。改用 Python 重寫才得到 81。

下游在同一份回報裡寫下的鐵律正好適用：**任何用來證明「沒有」的指令，先確認它在已知有的情況下真的會輸出東西。「查無」與「查壞了」的輸出長得一模一樣。**

### 6. `job.timeout-minutes` 被此 runner 忽略；真正生效的是 `step.timeout-minutes`（issue #926，2026-07-31 查證）

**事實**：這個 Gitea 實例的 act_runner 執行的是 `gitea.com/gitea/act`（`nektos/act` 的 fork）。查證方式是直接讀原始碼，不是猜測：

- **job-level 沒有任何消費端。** `pkg/runner/run_context.go` 與 `pkg/runner/job_executor.go` 全文搜尋 `Timeout`/`TimeoutMinutes` 均為零筆（`job_executor.go` 僅有兩個寫死的 infrastructure timeout：1 分鐘的 container cleanup、5 分鐘的 cancellation post-step，兩者都與 `Job.TimeoutMinutes` 無關）。這與上游 `nektos/act` 自己的文件一致——[nektosact.com/not_supported.html](https://nektosact.com/not_supported.html) 把 `job.timeout-minutes` 列在 "ignored" 清單。
- **step-level 有真正的消費端。** `pkg/runner/step.go`：

  ```go
  func evaluateStepTimeout(ctx context.Context, exprEval ExpressionEvaluator, stepModel *model.Step) (context.Context, context.CancelFunc) {
      timeout := exprEval.Interpolate(ctx, stepModel.TimeoutMinutes)
      if timeout != "" {
          if timeOutMinutes, err := strconv.ParseInt(timeout, 10, 64); err == nil {
              return context.WithTimeout(ctx, time.Duration(timeOutMinutes)*time.Minute)
          }
      }
      return ctx, func() {}
  }
  ```

  由 `runStepExecutor()` 呼叫：`timeoutctx, cancelTimeOut := evaluateStepTimeout(ctx, rc.ExprEval, stepModel)` → `err = executor(timeoutctx)`，這個帶 deadline 的 context 會一路傳進真正的執行層（Docker-backed step 是 `JobContainer.Exec(sr.cmd, ...)(ctx)`；host-backed step 是 `HostEnvironment.ExecWithCmdLine(...)(ctx)`）。`nektos/act` 上游同一段程式碼一致，Gitea 的 fork 沒有動過這段。

**修法**（已套用，issue #926；覆蓋範圍於 2026-07-31 的 cross-vendor review 更正——見下方「timeout-minutes 覆蓋率」小節）：每個 job 保留 `timeout-minutes:`（正確的 GitHub Actions schema、成本為零、未來 runner 若補上支援就自動生效），但每一處都**加註解**明講它在這個 runner 上不生效；真正的邊界是同一個 job 底下**每一個** `run:` step、以及**每一個** `uses:` step（不只 `actions/upload-artifact` / `actions/cache`——`actions/checkout` / `actions/setup-dotnet` / `actions/setup-node` / `actions/setup-python` 同樣是會打網路、可能掛住的呼叫）各自的 `timeout-minutes:`。不要看到 job 有 `timeout-minutes: 45` 就假設這個 job 真的被 45 分鐘框住——要往下找同一個 job 底下的 step-level 設定。

**活體驗證（不必重讀原始碼就能確認）**：[`.github/workflows/timeout-selftest.yml`](../.github/workflows/timeout-selftest.yml)——`workflow_dispatch` 專用、單一 job、單一 step（`timeout-minutes: 1` + `sleep 180`）的 positive control，任何時候都能重跑，runner 升級後尤其該重跑一次。判讀方式（該檔案 header 註解也有記錄；2026-07-31 更正——原文寫「整個 run 總時長」，但 `local-runner` capacity 2 下 dispatch 到真正開始執行之間可能排隊，run 的**總**時長會把排隊時間也算進去，跟這個 step 本身有沒有被 timeout 掐斷是兩件事；要看的是**這個 step 自己**的起訖時間，不是整個 run 的 dispatch-to-finish）：
- **機制生效**：該 step 自己的執行時間在約 1 分鐘處被標記失敗（不是從 run 被 dispatch 那刻起算）。
- **機制被靜默忽略**：該 step 自己跑滿 3 分鐘（完整跑完 `sleep 180`）、job 回報成功/綠燈。

**注意**：這個檔案是本文件「CI red does not mean failed」原則的一個特意反轉——對 `timeout-selftest.yml` 來說，**綠燈才是壞消息**（代表機制沒生效），紅燈在 ~1 分鐘處才是正常、健康的結果。

**追蹤**：這是這份文件第二個「YAML 寫了但這個 Gitea 版本靜默不理」的案例（第一個見下方 #7）。未來升級 act_runner 版本後，重跑一次 `timeout-selftest.yml` 確認行為沒有意外改變。

#### timeout-minutes 覆蓋率（2026-07-31 更正，issue #926 cross-vendor review）

早先的提交訊息／PR 描述宣稱「64 個 real-work step 已檢查、13 個已記錄的例外、0 個未解釋的缺口」。獨立覆核發現這個分母是**挑出來讓宣稱成立**的：只算 `run:` step 加上 `actions/upload-artifact` / `actions/cache` 這兩種 `uses:`，把 27 個 `actions/checkout` / `actions/setup-dotnet` / `actions/setup-node` / `actions/setup-python` 呼叫整個排除在分母之外——即使這些同樣是會打網路、可能掛住的呼叫。以那個窄分母算，64 個裡仍有 13 個沒有自己的 timeout-minutes，其中 6 個是 `actions/cache@v4`；`continue-on-error: true`（cache step 都有）只能吞掉「回傳錯誤」的 step，對「永遠不回傳」的 step 完全沒用——而這正是 issue #926 本身要防的情境。

**誠實分母、已修正**：分母改成「本 repo 每一個 workflow 檔案裡，每一個 `run:` step、每一個 `uses:` step」，只排除**整個檔案**、且排除理由寫明（`EXCLUDED_FILES`，一個模組層級的具名常數，理由字串直接附在旁邊，不是散在邏輯裡的隱性條件）。

**2026-07-31 三次更正——`publish-nuget.yml` 排除已解除**：`EXCLUDED_FILES` 曾經暫時排除 `publish-nuget.yml`，理由是 PR #937 正在重構它的 step。#937 已合併（`3b894df80`）——排除已移除（`EXCLUDED_FILES` 現在是空字典，機制留著沒刪，供未來真的需要暫時排除某檔案時重用），該檔案的最終形狀已加上自己的 step-level timeout-minutes，與其他 6 個 workflow 檔案一視同仁。這一步本身就是 cross-vendor review 抓到的一個 HIGH finding：#937 合併後，整個「不可逆的發版 job」（29 個 real-work step：pack、smoke test、vulnerability scan、GitHub mirror sync、兩次 `nuget push`）曾經完全沒有真正生效的 timeout——只有一個被這個 runner 忽略的 job-level 值，一次 push 中途卡住的網路連線會佔住 capacity-2 runner 兩個 slot 之一，直到 Gitea 實例的 3 小時 `ENDLESS_TASK_TIMEOUT` 硬上限。

**兩個 `nuget push` step 的 timeout 刻意設得寬**（`Push to Gitea Packages` 20 分鐘、`Push NuGet packages to GitHub Packages` 25 分鐘，後者更寬因為跨公網打 github.com 而非 tailnet-local 的 Gitea host）——這是本文件「timeout 該綁窄還是綁寬」少數需要論證取捨的地方，不是照抄慣例：中途砍斷一次多套件批次 push **不是零代價**（可能已經有幾個套件推上去、其他還沒），但這個檔案自己已經有對應的復原機制——`Push to Gitea Packages`/`Push NuGet packages to GitHub Packages` 之前各自的 `Verify version cohort not partially published` step，正是設計來在下一次執行時偵測「上次留下的部分批次」並拒絕在髒狀態上silently繼續。相對地，放著不 bound：確定的代價是佔住 runner 兩個 slot 之一長達 3 小時，讓 repo 其他所有 workflow 塞車；換來的只是「這次網路連線也許最終會自己恢復」的可能性，沒有對應的復原機制。兩相權衡，寬鬆但**有界**的 timeout 全面優於**無界**的等待——這個檔案裡沒有任何一個 step 被判定為「寧可讓它掛著」的例外。

**2026-07-31 二次更正——腳本寫了但沒接進任何 workflow**：上一版只把 [`scripts/audit-workflow-timeouts.py`](../scripts/audit-workflow-timeouts.py) 寫成「隨時可手動重跑」，本身沒有掛進任何 CI 觸發點——會永遠印出綠燈，直到某天真的有一個新 step 漏加 timeout-minutes 才會被人發現，而那正是 issue #926 本來要防的情況：證明了一個性質、卻不在下一支 PR 上重新檢查，等於沒證明。已接進 `mutation-gate.yml` 的 `changes` job，緊接在既有三個 guard（`check-gitea-token-not-sourced.py` / `check-jwt-key-literal-blocklisted.py` / `check-mutant-entries-parse.py`）之後——`changes` 是這裡唯一兩個 trigger 都沒有 path filter 的 job，`ci-build.yml` 則不行：它的 `paths-ignore` 會讓一個只碰 `.github/workflows/**` 的 PR 可能完全不跑它，而 timeout regression 正好最容易從這種 PR 進來。這個新 guard step 自己也帶 `timeout-minutes:`，因此也被腳本自己算進分母——PASS 因此連帶證明了「這個 guard 沒有把自己排除在外」。

```bash
python3 scripts/audit-workflow-timeouts.py
```

2026-07-31 三次更正（`publish-nuget.yml` 排除解除）後的實際輸出——分母現在是**全部 7 個 workflow 檔案、0 個排除**：

```
DENOMINATOR: every `run:` step and every `uses:` step, in every job, across all 7 scanned workflow file(s) (ci-build.yml, e2e-test.yml, integration-test.yml, mutation-gate.yml, publish-nuget.yml, regression.yml, timeout-selftest.yml) -- 0 file excluded (see above).
Total real-work steps in scope: 125
  with timeout-minutes:    125
  documented exemptions:   0
  MISSING timeout-minutes: 0

WORKFLOW_TIMEOUT_AUDIT_RESULT: PASS
```

即：**125 個 step（每個 `run:` + 每個 `uses:`，橫跨本 repo 全部 7 個 workflow 檔案，0 個檔案排除，含 `publish-nuget.yml` 29 個 step 與這個稽核 guard 自己）逐一檢查，125 個都有自己的 `timeout-minutes:`，0 個例外，0 個缺口，且這個檢查現在每次 `changes` job 跑就會重新驗證一次，不是只在寫這份文件的當下算過一次。** 不再需要「文件記錄的例外」清單——早期草稿的 13 個例外（cache step、`if: failure()` 診斷 step、背景啟動 step）現在全部直接補上自己的 timeout-minutes，而不是被記錄成例外後放行；`EXCLUDED_FILES` 目前也是空字典——沒有任何檔案被排除在分母之外。

**追蹤（分母不是凍結值，每次新增/刪除 real-work step 都會變動）**：這是 2026-07-31 當下的快照，不是恆定不變的數字——分母隨後續 PR 自然增減。issue #968（per-entry mutation-gate selection）在 `changes` job 新增一個 guard step（`selftest_relevance_covers_tests_and_workflow.py`/`selftest_kind_validation_and_accounting.py`/`selftest_select_relevant_entries.py` 三支既有與新增的 selftest 首次接進 CI），本次修改後重跑 `python3 scripts/audit-workflow-timeouts.py`：`Total real-work steps in scope: 128`，`with timeout-minutes: 128`，`MISSING timeout-minutes: 0`，`WORKFLOW_TIMEOUT_AUDIT_RESULT: PASS`（125 → 128 的差額中，1 個是本次新增的 guard step，1 個是 #917／PR #972 在同一個 `changes` job 新增的 e2e-integrity guard，另 1 個是本分支起點就已存在、非本次引入的既有差異——rebase 到 #972 之後的起點量測為 127）。不要把上面的 125 當成長期有效的斷言；跑腳本本身才是誠實計數。

**再一次追蹤（rebase 到 #974 之後）**：#974（Vue3Demo build gate）新增整份 `.github/workflows/vue3demo-build.yml`，把掃描的 workflow 檔案數從 7 個變成 8 個。同一時間 #973（本票自己的兩輪 CI 事後修復——`gate` job 沒有 checkout 卻呼叫 repo 內腳本、以及新 selftest 用了這個 runner 沒裝的 PyYAML）並未再新增 `changes` job 的 guard step 數（`selftest_gate_job_reconciliation.py` 掛進既有的 #968 guard step，不是新開一個 step）。rebase 到 `b7dcdac92`（#974 merge commit）之後重跑：`Total real-work steps in scope: 133`，`with timeout-minutes: 133`，`MISSING timeout-minutes: 0`，`WORKFLOW_TIMEOUT_AUDIT_RESULT: PASS`（128 → 133 的差額 5，全部來自 `vue3demo-build.yml` 自己的 real-work step，非本票引入）。同上，不要把任何一個快照數字當成長期有效的斷言。

**這個腳本證明的範圍，以及它證明不了什麼**：`timeout-minutes:` 是 **step** 層級的邊界，只框住「這個 step 自己開始執行之後」的時間。它框不住：
- **job 開始前的 runner 排隊時間**（`local-runner` capacity 2，忙碌時一個 job 可能等很久才輪到自己的 slot）——這不是 step 沒被 bound，是 step 還沒開始執行。
- **`services:` container 的 pull + 啟動 + health-check**：`integration-test.yml` 的 `mssql` service 在**任何 step 執行之前**就由 runner 拉取映像、啟動容器、跑 health-check——這整段時間沒有任何 step 的 `timeout-minutes:` 能框住，因為它發生在第一個 step（`checkout`）開始之前。已在該 workflow 的 `services:` 區塊正上方加註解記錄這個缺口，並說明為什麼選擇「記錄」而非「改成手動 `docker run` 一個可被 timeout 框住的 step」——後者會丟掉 runner 內建的 health-check gating 與 service-container 網路別名，去保護一個已經量到約 6 秒、本來就很快的階段，划不來。真的卡住時，後面 "Wait for MSSQL ready" step 自己的 12 分鐘 timeout 仍會框住「這個 step 開始 poll 之後」的等待，只是框不住 poll 開始之前的 pull/啟動階段。

**追蹤**：`scripts/audit-workflow-timeouts.py` 沒有 per-step 例外清單（`EXEMPT_STEPS` 目前是空字典）——未來如果真的出現一個不能加 timeout-minutes 的 step，把它加進那個字典並寫清楚理由，不要回頭窄化分母讓它從統計裡消失。

### 7. `strategy.matrix.max-parallel` 被靜默忽略（#885，2026-07-29 發現）

**症狀**：`e2e-test.yml` 曾用單一 matrix job + `strategy.matrix.max-parallel: 1` 想讓三條 leg 依序執行而非搶著並行——**這個設定完全沒有效果**：`workflow_dispatch` 重跑後，三條 leg 的 job container 依然在 6 秒內全部啟動、整段並行。

**根因**：已知的 Gitea Actions 上游缺陷（[go-gitea/gitea#35561](https://github.com/go-gitea/gitea/issues/35561)："Cannot make steps run sequentially with matrix and max-parallel = 1"），不是設定寫錯。

**修法**：拆掉 matrix，改成三個獨立 job（`e2e-baseline` / `e2e-killswitch` / `e2e-island`）用 `needs:` 串接——這是本 repo 其他 workflow 已經在用、確定有效的基本功能，現場重跑驗證確實依序執行而非並行。完整量測數據、五種失敗形狀、`needs:` 改法的代價分析，見下方「e2e 三條 matrix leg 並行導致的時序性失敗（#885）」小節。

**追蹤**：不要在這個 repo 的任何 workflow 用 `strategy.matrix.max-parallel` 期待它限制並行度——目前這個 Gitea 版本會靜默忽略它。若未來 host 容量提升、Gitea 修好 #35561，可重新評估。

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

> `strategy.matrix.max-parallel` 被靜默忽略的核心事實與上游 issue 連結已收錄進上方「七大已知不相容點」#7；本節保留完整的量測數據與代價分析（run 5835/5837 的實測結果），供需要細節時查閱。

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
原本 matrix `fail-fast: false` 的語意——某條 leg 失敗不會連帶跳過後面的 leg。實測代價：
修法前 run 5835（並行）總時長約 3 分 20 秒；修法後 run 5837（這個 `needs:` 串接版本，
`workflow_dispatch` 現場跑）總時長約 8 分 58 秒——多了約 5.5 分鐘，因為三條 leg 不再
重疊，時長變成三者相加。換取的是可預測、不再被資源競爭污染的結果。
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

## `integration-test.yml` 的 `mssql` service container 沒有記憶體上限（#1020，2026-08-03）

Run 6509 在 `EnsureCreated()` 死於 `Error 945`（"insufficient system memory in
resource pool 'internal'"）——同一支測試（`BasePagedListVM_Paging`）平常 957ms，那次
跑了 13 秒才死，是先 thrashing 再放棄的樣子，不是硬 crash。**間歇性**，同一天多數 run
是 9/9 全過；`services.mssql` 當時完全沒有任何記憶體邊界（`env` 沒有
`MSSQL_MEMORY_LIMIT_MB`，`options` 沒有 `--memory`），會跟同一個 job 容器（同時在
restore/build/`dotnet test`）搶這台 mac-mini act_runner 背後那顆硬 4 CPU /
**3.813GiB**（`docker info`，本票直接對這個 repo 自己的 Gitea Actions daemon 現場量到）
Docker VM。

**修法前先在本機對同一顆 daemon 做的驗證**（不是查文件就假設能用，三次被「驗證環境成立、
執行環境不成立」的守衛打過之後養成的習慣，見 `#967`/`#973`/`#968`）：

- **`sqlcmd` 在 azure-sql-edge 的 arm64 image 上不存在**——`docker exec` 進容器找
  `/opt/mssql-tools*/bin` 直接 `No such file or directory`，跟 `docker-compose.yml`/
  `test/docker-compose.etl-test.yml` 既有註解一致（本票是第一次真的進容器裡驗證，不是
  沿用註解）。這個 job 本身也沒有 docker socket（`Wait for MSSQL ready` step 既有註解已
  記錄），所以就算 image 有 sqlcmd 也用不到。
- **`sp_configure 'max server memory (MB)'` 在 azure-sql-edge 上不存在**——直接
  `Msg 15123`："The configuration option 'max server memory (MB)' does not exist, or
  it may be an advanced option."（`EXEC sp_configure;` 列出全部選項也搜不到任何
  `memory` 字樣）。這是被文件警告過的「reduced engine」的實例。
- **`sys.dm_os_sys_info` 有支援**，回傳 `physical_memory_kb`／`committed_target_kb`／
  `committed_kb`／`container_type_desc` 等真實欄位——這是最後用在 workflow 裡的 DMV。
- **`MSSQL_MEMORY_LIMIT_MB` 對這台host沒有可觀測的效果**：本機對同一顆 Docker daemon
  分別用 1536／768／400（MB）起容器，`committed_target_kb` 量到
  1480256／1546520／1561152——**隨著要求的上限越調越低，數字反而越高**，跟預期方向相反，
  比較像是跟著容器啟動當下的 ambient 可用記憶體走，不是跟著這個環境變數走。
  `container_type_desc` 五次測試全部是 `NONE`——引擎自己從來不認為它在容器裡跑，這大概
  就是 cgroup-aware 記憶體管理路徑沒被觸發的原因。
- **`--memory`（cgroup 上限）則是實測有效**：容器內 `/sys/fs/cgroup/memory.max`
  每次都精確等於外部給的 `--memory` 值，跟 SQL 引擎自己相不相信這個上限無關——這是
  kernel 層面強制的，不需要引擎配合。

**修法**：`services.mssql.env` 加 `MSSQL_MEMORY_LIMIT_MB: 1536`（照 Microsoft 文件的
建議機制設，如上所述**空跑，沒有效果**）；`services.mssql.options` 加 `--memory=2560m`
（kernel 層面實際生效的邊界）；新增一個 `Report MSSQL effective memory (issue #1020)`
step，用 .NET 10 file-based app（`dotnet run --file`，同一支 `Microsoft.Data.SqlClient`
版本，跟 `Directory.Packages.props` 一致）查 `sys.dm_os_sys_info`，把
`physical_memory_kb`/`committed_target_kb`/`committed_kb`/`container_type_desc` 印進
每次 run 的 log。**三者當中，這個 step 才是有明確站得住腳的價值**：這個缺陷本來就是
間歇性的，單次 green run 什麼都不能證明，能讓「CI 綠了」跟「這個修法真的有作用」脫鉤
的只有它。

**`--memory=2560m` 是不是「修好」這個缺陷，用真正的測試負載覆核之後判斷：不是，是一個
backstop，不是對 run 6509 實際發生情況的修法**——`2560m` 最初是用 idle/startup 狀態量
到的 `committed_target_kb`（約 2.03GiB）加安全邊界推出來的。run 6509 死在第 9 個
create/drop 循環，也就是**持續負載之下**，剛好是 idle 數字最可能低估的情境，所以覆核
了：本機對一個用最終設定（`MSSQL_MEMORY_LIMIT_MB=1536`、`--memory=2560m`）起的
azure-sql-edge 容器，實際跑兩次完整的 9 項整合測試（`dotnet test` 對 Release build 的
`WalkingTec.Mvvm.Integration.Test.dll`，`--filter "TestCategory=Integration"`，兩次都
9/9 全過，各花 6.5s／7.0s），全程用 `docker stats` 取樣。**兩次的 MEM USAGE 峰值約
663MiB，只有 2560m 上限的 26%**，測試跑完後 `sys.dm_os_sys_info.committed_kb` 約
152MiB——**不只遠低於 2560m 上限，也遠低於 mssql 自己 idle 時的 2.03GiB 目標**。一個
在真實負載下只用到上限 26% 的容器，不是那個「正在長大」的東西——`--memory=2560m` 沒有
框住任何實際被觀察到在長大的東西。

**比對照下真正吻合的機制**：`Error 945` 是 SQL Server 自己的記憶體管理員跟系統要記憶體
被拒絕，而不是 mssql 自己用量爆掉；配合上面「`container_type_desc` 五次全部 `NONE`」
——引擎在這台 host 上根本不是照 cgroup 範圍看記憶體，比較像是照整台 VM 的可用記憶體在
判斷。這個形狀更吻合 **mssql 是整台 VM 記憶體被搶光的受害者，而不是加害者**——加害者
的候選是同一個 job 容器同時在跑的 `dotnet build`/`dotnet test`，跟 `#902`（testhost
OOM kill，靠 `-m:1` 序列化解掉）是同一種形狀。逐容器獨立生效的 `--memory` 上限，對
「mssql 自己的 cgroup 沒有超標，但整台 VM 被別的容器榨乾」這件事完全沒有防護力——這正是
上面數字指向的情境。

**原本規劃要用一個更直接的測量把這個問題定案，但沒有做，講清楚為什麼**：讓 mssql 跑
9 項整合測試的同時，另一個容器對同一個 solution 做完整 `dotnet build`，兩邊都取樣記憶
體，直接看 mssql 會不會被旁邊的容器擠壓——這個量測理論上能比上面兩組獨立數字更直接回答
問題。**沒有做**：投入這個工作階段的當下，這台機器上正有一個真實、不相關的 Gitea
Actions job（`mutation-gate.yml` 的 `mutants` job）在跑，CPU 用到 ~260%、記憶體
~800MiB，而這個 job 自己的文件記載的預算上看 ~125 分鐘——刻意在這個時間點疊加一個高
負擔的 build+test 去搶同一顆 4 CPU / 3.813GiB 的 Docker daemon，代價是可能拖慢或搞壞
一個真實、無關的 CI job，換來的量測品質還不見得乾淨。判斷是**留著不做，不是弱弱做一個
拿來充當證據**。

**下一步該往哪查，已經改了方向**：如果之後真實 run 又出現這個缺陷，**不要往上調
`--memory` 這個數字**——上面的證據指向問題在 build/test 那一側的資源競爭，不是 mssql
需要更多空間。該查的是同一個 job 容器同時在做的 restore/build/test，或是這個 runner
的容量本身；`ubuntu-24.04`（Azure overflow runner，15GiB）仍然是最終備案，但那是解法
換一個更大的機器，不是先調這個數字。

**其他 workflow 有沒有一樣的形狀**：用 `yaml.safe_load` 逐一檢查
`ci-build.yml`／`mutation-gate.yml`／`regression.yml`／`e2e-test.yml`／
`publish-nuget.yml`／`timeout-selftest.yml`／`vue3demo-build.yml` 的每個 job，
**沒有其他檔案有 `services:` 區塊**——`integration-test.yml` 是這個 repo唯一起資料庫
service container 的 workflow，不需要另外開票。

---

## 排錯 SOP

當 PR 的 CI conclusion 是 failure：

### 1. 取得 workflow run ID

```bash
GITEA_TOKEN=$(grep -oE '[a-f0-9]{40}' "$HOME/.gitea-token")   # never `source` it -- see docs/gitea-packages.md
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

對照本文「七大已知不相容點」逐一比對 — 若 failure pattern 是其中之一 → infrastructure 問題，不是你的 PR 引入的 regression。

若不是其中之一 → 看 test output 找真正的 code regression。

---

## 本機 reproduce CI 流程

90% 的 CI 失敗其實本機能先 reproduce、節省一輪 round-trip：

### 本機跑 build-and-test

```bash
# 1. restore + build
$HOME/.dotnet/dotnet restore core.slnf
$HOME/.dotnet/dotnet build core.slnf --no-restore -c Release

# 2. test with coverage
$HOME/.dotnet/dotnet test core.slnf \
  -m:1 \
  --no-build -c Release \
  --verbosity normal \
  --filter "TestCategory!=Integration" \
  --logger "trx" \
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

> **為什麼 `-m:1`？**（#902）`core.slnf` 涵蓋 7 個測試專案；MSBuild 預設 `-maxcpucount`（4）會同時起 4 個 testhost，在 CI runner 的 4 CPU / 3.8GiB Docker VM 上把彼此 OOM kill（Api.Test、Core.Test、Etl.Test 都中過）。本機資源通常比這寬裕，拿掉 `-m:1` 未必會在本機重現 OOM，但這代表你測不出 CI 實際會發生的行為——本機 reproduce 要忠實，保留 `-m:1`。
>
> **為什麼 `--logger "trx"` 不指定 `LogFileName`？**（#902）7 個專案若共用同一個固定檔名，只有最後一個的 TRX 會留下（其餘被 `WARNING: Overwriting results file` 蓋掉）；拿掉固定檔名讓 VSTest 自動命名，每個專案的 TRX 才都保留。

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
# 啟 SQL Server container（同 CI image；#767：mcr.microsoft.com/mssql/server 沒有
# linux/arm64 build，在 Apple Silicon 上跑 QEMU x64 模擬會直接 crash，改用有原生
# arm64 image 的 azure-sql-edge——同一顆 TDS-相容引擎，本專案的整合測試只做
# 純 EF Core CRUD，沒用到 full-text search/CLR/temporal tables 等 edge 不支援的功能）
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=YourStr0ng!Pass" \
  -p 1433:1433 -d mcr.microsoft.com/azure-sql-edge:latest

sleep 15  # 等 SQL Server 冷啟動

export WTM_TEST_MSSQL="Server=localhost,1433;Database=WtmIntegrationTest;User Id=sa;Password=YourStr0ng!Pass;TrustServerCertificate=True"
$HOME/.dotnet/dotnet test test/WalkingTec.Mvvm.Integration.Test \
  -c Release --filter "TestCategory=Integration"

docker stop $(docker ps -q --filter "ancestor=mcr.microsoft.com/azure-sql-edge:latest")
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
- **`scripts/publish-to-gitea.sh` 的真實（非 `--dry-run`）路徑已停用（#925 cross-vendor review finding 4）。** 它只 pack 6 個套件中的 3 個（Core/Mvc/TagHelpers.LayUI，永遠不含 WorkFlow/Etl/FileHandlers.S3），也完全不跑 `publish-nuget.yml` 的任何 gate（six-package smoke test、local vulnerability scan、version-cohort check）——runner 掛掉時若真的用它繞過 CI，等於官方文件教人跳過所有這些檢查去發布不完整的一批套件。**修好 CI 觸發永遠優先於本機發** —— runner 卡死多半是 tag-object 去重（見上一條），不是真的不可用：`git tag -d` + `git push origin :refs/tags/vX.Y.Z` + `git tag -a` 重建（新 timestamp → 新 tag-object sha）幾乎都能解決。`--dry-run` 仍可用於預覽版本號/套件清單，但不執行任何 pack/push。

完整 release 流程見 [`docs/wtm-developer-manual.md`](./wtm-developer-manual.md) 與 [`CHANGELOG.md`](../CHANGELOG.md)。

---

## 相關文件

- [`docs/production-readiness.md`](./production-readiness.md) — production readiness 評估
- [`docs/dependency-management.md`](./dependency-management.md) — 套件版本與 vulnerability gate
- [`docs/getting-started.md`](./getting-started.md) — 環境建置
- [Gitea Actions 官方文件](https://docs.gitea.com/usage/actions/overview) — Gitea CI 平台說明
- [act 專案](https://github.com/nektos/act) — Gitea Actions 內部使用的 runner
