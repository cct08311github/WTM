# CI Operations

> **適用版本**：10.5.1+
> **最後更新**：2026-05-14
> **CI 平台**：Gitea Actions（act_runner 0.6.1，self-hosted at `mac-mini.tailde842d.ts.net`）

本文件涵蓋 WTM CI 工作流總覽、Gitea Actions 與 GitHub Actions 的四大已知不相容點，以及排錯 SOP。完整修復脈絡見 [Issue #11](https://mac-mini.tailde842d.ts.net/chiu0831/WTM/issues/11) / [PR #12](https://mac-mini.tailde842d.ts.net/chiu0831/WTM/pulls/12)。

---

## 工作流總覽

| 檔案 | 觸發 | 主要 jobs |
|------|------|-----------|
| `.github/workflows/ci-build.yml` | push + PR | `build-and-test`、`js-test`、`release-tooling-test`、`security-scan` |
| `.github/workflows/e2e-test.yml` | push + PR（path filter：`src/**`、`demo/**`、`test/e2e/**`） | `e2e`（Python + Playwright） |
| `.github/workflows/integration-test.yml` | push + PR（含 SQL Server container） | `integration-test` |
| `.github/workflows/publish-nuget.yml` | release tag | NuGet pack + push 到 Gitea registry |

Gitea Actions 直接讀 `.github/workflows/*.yml` — 語法與 GitHub Actions 相容、不必搬到 `.gitea/`。但有些 action 版本（特別是 v4+ artifact action）不支援 Gitea 的 GHES API，見下方四大不相容點。

---

## 四大已知不相容點

### 1. `actions/upload-artifact@v4+` 不相容 Gitea Actions GHES API

**症狀**：
```
::error::@actions/artifact v2.0.0+, upload-artifact@v4+ and download-artifact@v4+
are not currently supported on GHES.
```
任何 artifact upload step hard-fail，整個 job conclusion 被標 failure，即便 build/test 全 pass。

**修法**：降回 `@v3`（最後支援 GHES 的版本），並加上 `continue-on-error: true` 雙保險：

```yaml
- name: Upload test results
  uses: actions/upload-artifact@v3
  if: always()
  continue-on-error: true
  with:
    name: test-results
    path: "TestResults/**/*.trx"
```

`continue-on-error: true` 的意義：即使 v3 未來也壞掉，這個 step 失敗也不再污染 job conclusion，CI 信號回歸真實 test 結果。

**追蹤**：未來 Gitea act_runner 升 artifact protocol 後可再評估升回 v4+。

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

## 與 NuGet 發佈相關

`publish-nuget.yml` 由 release tag 觸發。當前狀態：

- 三個 publish 套件：`WalkingTec.Mvvm.Core`、`WalkingTec.Mvvm.Mvc`、`WalkingTec.Mvvm.TagHelpers.LayUI`（Etl 模組不在 publish 清單）
- Target registry：**Gitea NuGet registry**（`https://mac-mini.tailde842d.ts.net/api/packages/chiu0831/nuget/index.json`）
- 自 2026-05-13 起 GitHub Packages 已停用、所有發佈走 Gitea
- 若 Gitea Actions runner 無法觸發或想本機跑：用 `scripts/publish-to-gitea.sh`

完整 release 流程見 [`docs/wtm-developer-manual.md`](./wtm-developer-manual.md) 與 [`CHANGELOG.md`](../CHANGELOG.md)（維護者另有本機 MEMORY.md，但不入 git）。

---

## 相關文件

- [`docs/production-readiness.md`](./production-readiness.md) — production readiness 評估
- [`docs/dependency-management.md`](./dependency-management.md) — 套件版本與 vulnerability gate
- [`docs/getting-started.md`](./getting-started.md) — 環境建置
- [Gitea Actions 官方文件](https://docs.gitea.com/usage/actions/overview) — Gitea CI 平台說明
- [act 專案](https://github.com/nektos/act) — Gitea Actions 內部使用的 runner
