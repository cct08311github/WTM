# Production Readiness

> **版本適用**：10.14.5 + 2026-07 優化批次（[Unreleased]）以後
> **最後更新**：2026-07-17（重評 — 取代 2026-05-14 的 10.5.1 版評估）

這份文件回答一個問題：**WTM 現在可以上 production 嗎？**

答案不是單純 yes/no — 取決於你的使用場景與風險承受度。本文件提供一個誠實的自評框架，協助你做出決定。

> **「已修」與「已保護」是兩件事。** 本文件（以及 `CHANGELOG.md`）衡量的是**框架自己修了什麼**；它不回答、也無法回答「你的 production 有沒有真的採用」。repo pin（下游套件版本宣告）、staging sign-off（驗證環境跑過的版本）、production deployment（實際服務流量的版本）是三個可能相差好幾個世代、且沒有任何自動化機制可以互相對帳的層級。已知最完整的下游採用個案（BMS）目前這三層之間有明顯落差，且其 production 版本沒有即時查詢方式可以確認。**目前確切版本數字、逐項修復對該下游的可達性分類與證據、以及所有查證方法與已知的不確定之處，一律以 [`docs/release-adoption-ledger.md`](./release-adoption-ledger.md)（#919）為準——本文件刻意不重複那些數字，避免兩份文件各自過期而互相矛盾。**

> **本次重評的背景**：2026-05 到 2026-07 之間，本 fork 經歷了 WorkFlow 引擎 tx-safety 戰役（10.9–10.12）、LayUI 2.13.8 islandification 與 CSP 硬化（10.13–10.14）、以及一輪由「最終徹底優化規格書」驅動的 **26-issue 優化批次**（Phase 0–2，見下方 § 2026-07 優化批次）。上一版評估凍結在 10.5.1，已落後約 30 個 release，故整份重寫。核心結論不變：**WTM 是誠實的中型 CRUD 框架，甜蜜點是內網/SMB/中小型多租戶後台；不是高流量 SaaS 或合規敏感場景的首選。** 但「地基」比上一版明顯更穩。

---

## 場景適用矩陣

| 場景類型 | 評估 | 主要考量 |
|----------|------|----------|
| 企業內網 / SMB 後台 / 中小型多租戶 admin app | ✅ **可以**，WTM 的甜蜜點 | 暴露面小、流量可控、認證集中 |
| 公開 SaaS、中等流量、含付費客戶 | ⚠️ **可以但需補強** | 採用前完成下方「production 補強清單」1-4 項 |
| 高合規領域（醫療 / 金融 / 政府） | ❌ **不建議** | 單人維護、無第三方合規審計、無正式 SOC2/HIPAA 文件 |
| 高流量 / 高可用 / 多區部署 | ❌ **不建議** | 無公開壓測 baseline、distributed cache/session 未官方文件化 |

WTM 設計為「快速 CRUD 開發框架」，**不**是高流量 SaaS 平台或合規敏感場景的首選。

---

## 客觀現況（截至 2026-07-17，[Unreleased] 分支狀態）

### 已驗證

- `dotnet list package --vulnerable --include-transitive`：**0 個 NU1903 漏洞**（27 個專案，含當時新納入 CI 的 S3 file handler 套件與其測試專案（已於 10.23.0 依 #1054 移除）；release gate 全程守住）
- 單元測試：**~5,780 pass / 0 fail**（Core.Test ~4,313、WorkFlow.Test 564+23 skip、Etl.Test ~683+17 skip、Admin/Api/Mvc.Tests/S3.Test 等）— 較上一版評估的 1,647 增長約 3.5 倍
- JS 測試：**~1,704 pass**（Jest + jsdom，涵蓋 framework_layui.js 的 island dispatch / kill-switch / sentinel-escape 路徑）
- E2E 測試：**35 pass / 1 skip / 0 fail**（Playwright + Python，31→36 檢查；新增 JWT/combobox-cascade/selector/upload 流程 + **#627 kill-switch 專屬 CI matrix leg**）
- CI（Gitea Actions）：build-and-test / js-test / e2e(baseline) / e2e(killswitch) / release-tooling-test / security-scan 全 green
- **CI workflow step-level timeout 覆蓋（#926，2026-07-31 cross-vendor review，三輪更正）**：先前提交訊息宣稱「64 個 real-work step 已檢查、13 個已記錄例外、0 個未解釋缺口」，獨立覆核發現該分母排除了 27 個 `actions/checkout`/`actions/setup-*` 呼叫，是**挑出來讓宣稱成立的分母**，不是誠實計數。第一輪更正：改成誠實分母（每一個 `run:` step、每一個 `uses:` step，不篩選子集），但當時暫時排除 `publish-nuget.yml`（PR #937 正在重構它）。第二輪更正：稽核腳本 `scripts/audit-workflow-timeouts.py` 原本只是「可手動重跑」，未接進任何 CI 觸發點——已接進 `mutation-gate.yml` 的 `changes` job（唯一兩個 trigger 都沒有 path filter 的 job）。**第三輪更正**：#937 已合併（`3b894df80`），`publish-nuget.yml` 排除已解除——該檔案的最終形狀（29 個 real-work step：checkout/setup、6 個 Gitea pack、smoke test、vulnerability scan、GitHub mirror sync 全流程、兩次 `nuget push`）現在跟其他 6 個 workflow 檔案一視同仁，全部有自己的 timeout-minutes。**這一步本身修的是 cross-vendor review 的一個 HIGH finding**：#937 合併後，整個不可逆的發版 job 曾經完全沒有真正生效的 timeout（只有一個被此 runner 忽略的 job-level 值），一次 push 中途卡住會佔住 capacity-2 runner 兩個 slot 之一長達 Gitea 的 3 小時 `ENDLESS_TASK_TIMEOUT` 硬上限。兩個 `nuget push` step 的 timeout 刻意設得寬（Gitea 20 分鐘、GitHub Packages 25 分鐘）而非設成例外或砍緊：中途砍斷一次批次 push 不是零代價，但這兩個 push 之前各自的 `Verify version cohort not partially published` step 本來就是設計來偵測「上次留下的部分批次」並拒絕在髒狀態上繼續——放著不 bound 的確定代價（佔住 runner slot 3 小時）大於 bound 帶來的風險（一個已有復原機制的部分批次狀態）。現況：全部 7 個 workflow 檔案、**0 個檔案排除**，共 125 個 real-work step，125 個都有自己的 `timeout-minutes:`，0 個例外、0 個缺口，且這個檢查現在每次 PR 都由 `changes` job 重新驗證。全樹重新驗證（非只掃 `src/`）：`python3 scripts/audit-workflow-timeouts.py`。**已知、記錄在案、仍未修的缺口**（誠實揭露，不是「全部覆蓋」）：(1) `integration-test.yml` 的 `mssql` service container 由 runner 在任何 step 執行前拉取/啟動/health-check，任何 step-level timeout 都框不到這段——已於該檔案 `services:` 區塊上方註解記錄，判斷為文件化優於重構（見 docs/ci-operations.md 的 timeout-minutes 覆蓋率小節）；(2) ~~mutation-gate.yml 的 45-entry 安全 mutant 預算~~——**已由 #968 取代，見下方獨立條目**：45→61 entry 的第三次時間預算漂移，以及把「每次 PR 跑全部 entries」改成「per-entry relevance selection」的根因修法。這裡不再重複那個過時的 90 分鐘數字。
- **mutation-gate.yml 預算第三次漂移改為根因修復：per-entry selection 取代「每次 PR 跑全部 entries」（#968）**：`.github/workflows/mutation-gate.yml` 自己文件記錄的公式（`entries * 73s * 1.3 / 60 * 1.25`）原本是為 45 個 entry 推導的；`kind: security` entry 數量後來漲到 61（#970 的 `etl970-cancellation-classification-guard-neutralize` 是最後一個推手——一個 correctness-only mutant 因為 `run_mutant.py` 的 `VALID_KINDS` 只接受 `security`/`selftest` 兩種、`selftest` 保留給測 runner 自己，被迫標成 `security` 才能被 CI 強制執行），同一份公式算出來變成 ~119-121 分鐘，早已超過還沒改的 90 分鐘上限——這是「30-35 entries → 45 → 61」同一個漂移第三次發生，即使該 step 自己的註解白紙黑字寫著「Do not let this drift stale the way '30-35 entries' did」。**Part 1（機械式重算，已完成）**：61 entries 代入公式，`61 * 73s * 1.3 = 5788.9s ≈ 96.5 分鐘`，加 25% headroom 後 `≈ 120.6 分鐘`，無條件進位到 125 分鐘（step-level `timeout-minutes`）；job-level 維持「step + 10 分鐘 setup 緩衝」的既有比例，改為 135 分鐘。這個數字仍然是**靜態估計，不是量測到的 p95**——entry 數量或單一 entry 成本明顯變動時仍需重新推導，Part 1 本身不解決「每次都要有人手動重算」這個根因。

  **Part 2（根因修復：per-entry selection）**：`test/mutants/gate_lib.py` 新增 `select`/`select_relevant_entries`/`relevance_self_check`/`resolve_changed_files`/`reconcile`。`pull_request` 事件下，`mutants` job 不再跑全部 61 個 `kind: security` entry，改成只跑**這個 PR 的 diff 實際觸及**的 entry——`select_relevant_entries()` 完全建立在既有的 `is_change_relevant()` 之上（用單一 entry 的 list 呼叫它，只是把 target_file/test_source/test_project 三個分支的檢查範圍縮小到那一個 entry，ALWAYS_RELEVANT_* 分支維持對全部 entry 生效不變），**沒有第二套 relevance 邏輯**。`push` 事件（合併到 dotnet10 後的 post-merge backstop）**無條件**跑全部 61 個，不看 diff、不做任何 relevance 計算——`gate_lib.py select --event-name push` 這條分支完全不呼叫 `resolve_changed_files()`，字面意義上的「不 conditional 在任何東西上」。**這是刻意的取捨，不是「更快且覆蓋不變」**：單一 PR 自己的 mutation-gate 執行結果，現在只證明「這個 PR 的 diff 觸及的 entry 有被跑過」，不再證明「全部 61 個 entry 都被這個 PR 驗證過」——全 registry 覆蓋率移到每次合併到 dotnet10 才恢復，不是每個 PR 都有。這個 trade-off 明講在 `changes` job 自己的註解裡，不只寫在這份文件。

  **「0 selected」的兩種情況必須能分辨，這是 #968 最容易踩雷的部分**：per-entry selection 讓「這個 PR 沒有 entry 被選中」第一次變成一個合法結果，而不只是「computation 出錯」——兩者若無法分辨，relevance 計算本身的一個 bug（例如某個分支被改壞、永遠回傳 False）就會讓整個 mutation gate 靜默停跑、卻仍然回報 PASS，跟 #855 defect 4「entry 存在但沒被執行不能是靜默 pass」是同一種缺陷類別、只是往上移了一層。修法：`gate_lib.py select` 在計算出**空**選擇結果時，先跑 `relevance_self_check()`——用真實載入的 registry（不是合成 fixture）驗證兩件事必為 True：(a) 這份 workflow 檔案自己的路徑一定會被判定相關（`ALWAYS_RELEVANT_EXACT` 分支）；(b) 第一個 discover 到的 entry，對它自己宣告的 `target_file` 一定會被判定相關（`target_file` 分支）。兩個 positive control 都通過 → 這次真的是「diff 沒碰到任何 gated 路徑」，`select` 印出理由後以 exit 0 回傳空清單，`changes` job 設 `has_selection=false`（不是失敗），`mutants`/`meta-selftest` 被跳過，`gate` job 把這個 skip 當成真正的 PASS，並且在 log 裡明講原因。任一個 positive control 沒過 → `select` 以 exit code 2 回傳、`changes` job 這個 step 直接 `exit 1`，整個 `changes` job 變成 `failure`（不是 skipped），`gate` job 既有的 `is_ok()` 判斷會把這個結果算成 `MUTATION_GATE_RESULT: FAIL`——**完全不會跟「合法的 0 selected」讀成同一個結果**，且不像 relevance 判斷本身在舊版對「diff 算不出來」的情況那樣 fail open 選擇跑全部——這裡刻意選擇 hard fail，因為 relevance machinery 若被證實壞掉，也沒有理由相信它退回去「跑全部」的判斷是可靠的。**誠實揭露這個 positive control 證明得了什麼、證明不了什麼**：它只驗證 relevance machinery「沒有完全壞掉」（workflow-file 分支 + target_file 分支兩條路徑仍然可用），**不保證每一個 entry 各自的 relevance 判斷都正確**——一個只影響某個特定 entry（而非整個 `is_change_relevant()` 函式）的窄範圍迴歸、或是只破壞 test_source/test_project 兩個分支的迴歸，不保證會讓整體選擇結果變成 0（其他 entry 仍會正常被選中），因此不保證會觸發這個 positive control。test_source/test_project 兩個分支的覆蓋改由 `test/mutants/_selftest/selftest_relevance_covers_tests_and_workflow.py`（issue #855 為 `is_change_relevant()` 寫的既有腳本）負責，但那支腳本本身在 #968 之前**從未被接進任何 workflow**——寫完之後就沒人跑過，是一個沒人發現的既有缺口，這次一併修：連同另一支既有的 `selftest_kind_validation_and_accounting.py`、以及本次新增的 `selftest_select_relevant_entries.py`（涵蓋上述四個「gate 還會不會開火」的驗證），全部接進 `.github/workflows/mutation-gate.yml` 的 `changes` job 當一個新 guard step，**每次 PR 都會機器重新驗證這些性質**，不是只在這張 PR body 上斷言一次。`gate` job 自己的 reconciliation 也從「discovered vs executed」改為「**selected vs executed**」（#855 defect 4 的邏輯往上移一層，以 `gate` job 自己的 inline bash 判斷——見下方 #973 事後檢討，說明為什麼最後選擇 inline 而不是呼叫 `gate_lib.py`）：只有 `has_selection == 'true'` 才做這個比對，`has_selection == 'false'` 時（合法的 0 selected）刻意不比對，避免「0 selected 對 0 executed」這個恆真比對偽裝成有意義的驗證。

  **事後檢討（#973，在本分支自己第一次真實 CI 跑出來）：上面這個比對曾經短暫呼叫 `python3 test/mutants/gate_lib.py reconcile`，而 `gate` job 從來沒有、架構上也不該有 `actions/checkout` step。** `changes`/`mutants`/`meta-selftest` 三個 job 全部回報 success、selected/executed 完全對得上（61/61），`gate` 卻失敗——`python3: can't open file '.../test/mutants/gate_lib.py': No such file or directory`——因為那個 job 從未 checkout 過 repository。這不是偶然疏漏，是架構性的：`gate` 是這個 workflow 裡**唯一必須無條件回報狀態**的 job（見該 job 自己的 header 註解），裡面每一個 step 都只對 `needs.*.outputs` 做純 bash／算術，不需要 tree 裡的任何東西——為了修一個呼叫點而加 checkout，等於推翻這個 job 不需要 tree 的整個理由。**修法**：改回 inline bash（跟 #855 defect 4 原本的寫法同一種形狀，只是比對對象從「discovered」換成「selected」）。`gate_lib.py` 的 `reconcile()` function／CLI subcommand 維持不動，也仍由它自己的 selftest 覆蓋——保留下來是當作這條規則的文件化、有測試佐證的參考實作，也供人工用 `python3 test/mutants/gate_lib.py reconcile --selected N --executed M` 診斷，但 `gate` job 不再呼叫它。**刻意讓同一條規則存在兩個實作**：判斷為可接受，因為這條規則本身只是一個整數比較加一句訊息——跟 `is_change_relevant()`（真正有比對邏輯、重複實作真的可能長歪）不是同一個風險等級。全庫掃過一輪（每個 workflow 檔案、每個 job 的每一個 `run:` step）確認：這是全部 7 個 workflow 檔案裡唯一一個「呼叫 repo-relative 腳本卻沒有 checkout」的 job，沒有第二個。新增 `test/mutants/_selftest/selftest_gate_job_reconciliation.py`，直接從這份 workflow 檔案本身抽取 `gate` job 的真實 script（不是手抄的複本）跑match／mismatch／skip／failure 四種情境，其中一種刻意在**完全沒有 checkout 任何東西的空目錄**底下執行——就是 #973 實際踩到的條件，可隨時重現——已接進 `changes` job 既有的 selftest guard step，跟另外三支一起每次 PR 重跑。

  **`kind` 的判斷（本票一併考慮，決定延後）**：#970 的 `etl970-cancellation-classification-guard-neutralize` 是一個 correctness-only mutant（重試邏輯把 cancellation 誤判成 data failure），沒有未授權存取、注入或跨租戶問題，卻因為 `VALID_KINDS` 只有 `security`/`selftest` 兩個選項、`selftest` 保留給測 runner 本身，被迫標成 `security` 才能拿到 CI 強制執行——這正是「security」entry 數量持續成長、進而讓時間預算持續漂移的一部分推手。Selection 機制上線後，`kind` 理論上會從「決定要不要跑」降級成純分類（`mutants` job 目前仍只選 `kind == 'security'`），加一個新 kind 因此變得比較便宜——但**本票判斷延後，不在這個 PR 加新 kind**：加新 kind 若要真的有意義，`mutants` job 的 discovery filter 需要從「只選 `security`」改成「選除了 `selftest` 以外的每個 kind」，這是選擇邏輯本身的第二層變動，跟本票已經在做的「per-entry relevance selection」同時動，會讓同一個 PR 疊兩層選擇語意變化，增加審查與出錯的難度——不符合「不要為了 `kind` 讓 selection 這個工作變複雜」的判斷準則。留給下一張 issue 單獨處理。
- **mutation-gate.yml 的 `mutants`／`meta-selftest` checkout 是 shallow clone，讓 #968 的 per-entry selection 在第一個真正命中 partial-selection 的 PR 上就整個失效（#1001）**：CI-only，未動任何 `WalkingTec.Mvvm.*` package 程式碼，**也沒有改變 #968 本身「per-PR coverage 刻意變少」的取捨**——這裡修的是「那個刻意變少的選擇機制，在一個真實 PR 上第一次被完整命中時，反而讓 required check 整個失敗」，不是把 coverage 改回去。

  **根因**：#968 引進 per-entry selection 時，`changes` job 的 checkout 帶了 `fetch-depth: 0`；`mutants`／`meta-selftest` 兩個 job 的 checkout 從一開始就沒有帶，於是沿用 `actions/checkout@v5` 的預設值——shallow clone（`fetch-depth: 1`），PR 的 base commit 不在磁碟上。`changes` 與 `mutants` 兩個 job 各自獨立呼叫 `python3 test/mutants/gate_lib.py select --kind security`，帶的是**同一組** `BASE_SHA`／`HEAD_SHA` 環境變數——但 `select` 底層靠 `git diff --name-only base_sha head_sha`（`gate_lib.py` 的 `resolve_changed_files()`）把這兩個 SHA 解析成改動檔案清單，shallow clone 上這條 `git diff` 會直接失敗（`base_sha` 不可達）。`resolve_changed_files()` 把這個失敗接住、回傳 `None`；`select` 既有的 fail-open 規則（issue #855，#968 沿用至今）於是選出**整個** `kind='security'` 集合——不是因為 diff 算出來是空的，是因為 diff 根本算不出來。兩個 job 帶著一模一樣的 env var，卻踩在不一樣的 repository 狀態上：這份 workflow 檔案自己原本的註解「兩個 job 從 SAME event/SHA inputs 各自算出同一個選擇」只講對了一半——inputs 相同，checkout 深度不同，一樣會分岔。

  **為什麼直到現在才被發現**：#998 之前的每一張 PR，要嘛動到 `test/mutants/**` 本身（`ALWAYS_RELEVANT_PREFIXES` 的一員——兩個 job 不管 checkout 深度都會選到全部，`changes` 的「正確全選」跟 `mutants` 的「fail-open 全選」剛好數字對得上，68 == 68），要嘛是純文件 PR（整個 job 被跳過，不會執行到 `select`）。PR #998（`aa4631179`）是第一張落在 **partial-selection**（#968 這個優化機制本來就是為了服務這種情況）的 PR：`changes`（full history）正確算出 30（`test/WalkingTec.Mvvm.Core.Test.csproj` 是 29 個 entry 的 `test_project`，那張 PR 剛好編輯了這個 `.csproj` 加一個測試專用套件參照，+1 是它改到的 `src/` 檔案）+ 6 個 selftest = 36；`mutants`（shallow，fail-open）執行了全部 68 個 security entry；`meta-selftest` 不受影響（`kind='selftest'` 從不被 relevance 篩選，一直是「無條件全選、無條件全執行」，所以它的 selected/executed 一直是 6/6，即使在 shallow clone 底下也一樣）；`gate` job 的 selected-vs-executed reconciliation 因此在 36 != 74（68 + 6）上失敗。**這裡引用的 #998 診斷數字（30/6/68 這幾個具體值）來自本票起手時已完成的既有診斷，非本次工作階段重新用 API 對過 PR #998 本身查證**——本工作階段的硬性限制禁止呼叫 Gitea/GitHub API，下面「本機驗證」段落列的才是本次獨立跑出來、可重現的證據。

  **修法，兩部分**：(1) `mutants` job 的 checkout 加 `fetch-depth: 0`——這是真正修到的那個 bug。`meta-selftest` job 的 checkout 為了一致性同樣加了 `fetch-depth: 0`，但**誠實揭露：這個 job 本來就沒有被這個缺陷影響**——`kind='selftest'` 的 entry 從不被 relevance 篩選（永遠無條件全選），它自己的 selected/executed 對帳（6/6）在 shallow clone 底下也一直是對的；這一半是防禦性補強，不是修一個觀測到的真實缺陷。(2) `gate` job 的 selected-vs-executed reconciliation，原本是單純的 `!=`，現在改成不對稱：`executed < selected`（該跑的 entry 沒跑到）維持 hard fail——這是危險方向，也是這條檢查原本要擋的事；`executed > selected`（跑得比預期多）現在改成印出 `::warning::`（帶兩邊總數）後放行，不再失敗——這是安全方向，而且即使 fetch-depth 修好了，`select` 的 fail-open 規則仍然可能因為跟這次缺陷無關的其他理由觸發（暫時性的 git 錯誤、這份 workflow 沒完整涵蓋到的 event payload 形狀）——把安全方向也判定成失敗，等於在下一次任何這類分岔發生時，重新製造出 #998 的同一種症狀（一個站得住腳的選擇差異卻擋住合併），只是換一個根因。`test/mutants/gate_lib.py` 的 `reconcile()` 參考實作同步改成同一條不對稱規則，讓這份檔案原本「兩個實作，刻意重複，因為這條規則不可能真的分岔」的宣稱維持成立，而不是讓其中一個實作偷偷過期。

  **本機證明：不只是「required check 不再失敗」，是「現在真的少跑」**：對 `src/WalkingTec.Mvvm.Core/WTMContext.CallApi.cs`（剛好是唯一一個以它為 `target_file` 的 security entry）建構一個只改一個檔案的 diff，跨兩個真實 commit。在 full-history clone 上跑 `gate_lib.py select --kind security --event-name pull_request --base-sha <base> --head-sha <head>`：選出 **1 個（68 個裡的 1 個）**。在同一組 commit 的 `git clone --depth 1`（base commit 確實不在，重現修復前 `mutants`／`meta-selftest` checkout 的真實狀態）上跑同一條指令：選出 **68 個（全部）**，且 stderr 明確印出 `Could not compute a path diff ... SELECT mode=fallback-full`——跟 fail-open 分支的訊息逐字對得上。`actions/checkout@v5` 的 `fetch-depth: 0` 就是上面重現的「full history」條件本身，所以修好之後，`mutants`／`meta-selftest` 兩個 job 在真實 CI 上拿到的會是「1 of 68」那個答案，不是「68 of 68」。

  **測試**：`python3 test/mutants/_selftest/selftest_gate_job_reconciliation.py`（直接從這份 workflow 檔案本身抽取 `gate` job 的真實 script，不是手抄複本；把原本的 match/mismatch/skip/failure 四情境改成 match/shortfall/surplus/skip/failure 五情境，新增 `check_surplus_tolerated_with_warning`——`SECURITY_COUNT` 帶到 62（`SELECTED_SECURITY_COUNT` 維持 61），斷言 `exit=0` 且 stdout 同時含 `MUTATION_GATE_RESULT: PASS` 與 `WARNING` 兩邊總數——7/7 全過）；`python3 test/mutants/_selftest/selftest_select_relevant_entries.py` 的 `check_reconcile()` 同步擴充成 match/shortfall（`reconcile(5,4)` 仍為 `False`）/surplus（`reconcile(4,5)` 為 `True` 且訊息含 `WARNING`）三案例，8/8 全過。`python3 scripts/check-mutant-entries-parse.py`（74 個 entry 檔案全部通過解析與驗證）、`python3 scripts/audit-workflow-timeouts.py`（133/133 real-work step 仍全部帶 `timeout-minutes`，這張 PR 只在既有兩個 checkout step 加 `with:` 區塊、其餘全是註解，沒有新增任何 step，數字不變）皆 PASS。**YAML 剖析**：本機環境剛好有 PyYAML（`python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/mutation-gate.yml'))"` 直接成功），但因為這份 issue 特別點名「CI runner 最近才發現沒有 PyYAML」，這次同時額外用了 `scripts/audit-workflow-timeouts.py`（stdlib-only、為了同一個理由寫的 dependency-free YAML 子集剖析器）當第二層、不依賴 PyYAML 的結構驗證——上面那次 133/133 PASS 的執行本身就是這第二層證明，不是另外重複一次。**未在真實 Gitea CI 上跑過，跟上方 #968/#973 同一個限制**：本工作階段硬性限制禁止呼叫任何 Gitea/GitHub API、禁止開 PR，所以這個 workflow 在真實 pull_request/push 事件下的行為（job 排程、`GITHUB_OUTPUT` 跨 job 傳遞、`changes`→`mutants`/`meta-selftest`→`gate` 整條 chain）沒有被真實觸發過一次——本機驗證只到「YAML 剖析成功＋guard script 全線 PASS＋直接呼叫 `gate_lib.py select` 對著一個真的 shallow clone 重現修復前的行為」這三層，不是「#998 那次失敗被重新跑過一次、這次綠燈」。
- 安全 audit 歷史：v10.2.0 完整 audit + 其後連續多輪對抗式（cross-vendor + perspective-diverse）審計；本次批次期間對抗式審查在合併前攔下多個真實缺陷（見 § 品質保證機制）

### 架構評估

| 面向 | 狀態 |
|------|------|
| 認證 | PBKDF2 密碼 + legacy MD5 自動 migration、JWT（access + refresh + `jti` replay guard，`AccountController`／`_FrameworkController` 的 refresh 路由衝突已於 2026-07-17 修復，#721）、`_remotetoken` 完整簽章驗證 |
| 授權 | RBAC、`IDataPrivilege` 列級資料權限、ITenant 多租戶 global query filter |
| ORM | EF Core 10，支援 MSSQL / MySQL / PostgreSQL / SQLite / Oracle；**WorkFlow 引擎全 23 個交易站點已可在 `EnableRetryOnFailure`（雲端 SQL Server/PG 常用設定）下運作**（#667 — 先前會拋 `InvalidOperationException`） |
| 中介軟體 | CSP（三態 + frame-ancestors + 違規回報）、correlation ID、health checks、rate limiting、slow query、idempotency、ETag、maintenance mode |
| Observability | 結構化 log（Serilog）、ActionLog/Audit/Exception logging via `IWtmLogService`、CorrelationId + SlowRequest 中介層；**時間源逐步遷移至 `TimeProvider`**（#676，WorkFlow + token 壽命優先，提升可測性） |
| ETL | Pipeline executor、watermark、quality rules、dry-run、匯入精靈、dashboard；**dead-letter 完整性**（捕捉 load/transform/Abort 失敗，run-scoped 去重，保留期 knob，#673）；**REST source SSRF 縱深 + 欄名 allowlist**（#661/#680） |
| BI / Analytics | Analysis Mode：Dimension/Measure、Sort+TopN、DistinctCount、HavingFilters、CompareWith P-o-P、Drill-Through、Insights；widget 資料源改為 async（#662，避免 dashboard fan-out 阻塞 threadpool） |

### 性能與規模

- 本批次做了 **evidence-based** 效能修復（各附 before/after benchmark）：grid header 攤平 ~16×、匯入驗證 ~18×、ETL 欄位映射 1.6–3.4×、多個 per-request 反射熱點消除（與當年 PR #34 同類）。Benchmarks 專案已納入 CI compile gate。
- 但仍**沒有**公開壓測 baseline、p99 SLO、scale-out / K8s 官方部署案例；distributed cache / session（Redis 等）需自行接。
- 如果你的應用會超過單 instance 或需要 SLA，這些都是你要自己驗證的事。

---

## Release 供應鏈完整性（#925，2026-07-31）

`publish-nuget.yml` 是把六個套件從 Gitea 推到兩個公開/私有 registry 的唯一自動化路徑。#925 起這個 workflow 本身經過一輪 cross-vendor review，8 項發現全部驗證後修復（#937）。**這裡只寫這次改動實際證明了什麼，不寫「gate every publish」這種本文件無法逐項驗證的整句宣稱**——CHANGELOG 對這批改動的描述不得超出以下清單。

**已驗證、每次 publish 執行**：
- 觸發來源完整性：`workflow_dispatch` 必須打在 `dotnet10` 分支本身；tag push 的 tag 必須指到 `origin/dotnet10` 歷史上的一個 commit（用 `git merge-base --is-ancestor`，不要求等於當下 tip——刻意相容既有的 tag-object 去重重建 SOP）。
- 版本一致性：tag/`version_suffix` 與 `version.props`/`CHANGELOG.md` 的三方協調（`scripts/reconcile-release-version.sh`），穩定版額外要求 CHANGELOG 最新標題有**合法曆法日期**（不只是形狀對）。
- 本機 vulnerability scan 改走 `dotnet list package --vulnerable --format json` 直接寫檔 + 結構化解析（`scripts/check-vulnerable-packages.py`），不再靠 `echo | grep -q` 這種在 `set -o pipefail` 下會被 SIGPIPE 誤判成乾淨的管線。**（#934，2026-07-31 追加）這支掃描跑在 `WalkingTec.Mvvm.sln` 上，不是消費端安裝到的東西**——同一個 job 現在額外對 smoke-install 步驟裝好六個套件的那個 consumer 專案，重跑一次同一支 `scripts/check-vulnerable-packages.py`（見下一條），兩邊分別覆蓋「solution 乾不乾淨」與「出貨物乾不乾淨」，不能互相取代。
- 六個套件的 smoke test 在**任何 push 之前**執行，對 `WalkingTec.Mvvm.Etl` 的 13 個 #883 changed members 做的是**執行期 reflection 斷言**（`ParameterInfo` 逐一比對名稱／型別／順序／`IsOptional`／`DefaultValue`／`IsVirtual`），不是編譯期呼叫——後者曾經誤稱自己證明了 optionality/順序/virtuality，實際上四者都證明不了（見 `test/smoke/publish-nuget-fixture/Program.cs` 的檔頭說明與 PR 描述裡的實測）。**這個 fixture 證明的是編譯期 metadata 與文件相符，不證明任何執行期行為**——`declaredSystemQuery: true` 真的會繞過租戶過濾這件事，由 `test/WalkingTec.Mvvm.Etl.Test` 涵蓋，不是這支 smoke fixture。
- **（#934，2026-07-31 新增）Consumer-graph vulnerability scan**：對 smoke-install 步驟已裝好六個套件（從候選 nupkgs 安裝，兩個 registry 都還沒看過）的同一個 consumer 專案，跑 `dotnet list package --vulnerable --include-transitive` 並沿用同一支 `scripts/check-vulnerable-packages.py`。修復前對六個未修的 nupkg 本機實測：8 個 HIGH finding（`System.Security.Cryptography.Xml` 8.0.2）；對修復後的 nupkg 實測：0 finding——兩個方向都跑過，不是只驗證了「綠」那一半。
- Gitea/GitHub Packages 各自的 version-cohort 檢查（`scripts/check-package-cohort.py`，NuGet V3 標準協定）：同一版本若只有部分套件已存在，直接拒絕，不會用 `--skip-duplicate` 悄悄補齊、混進兩個不同 commit 的產物。
- GitHub mirror 的 sanitize/leak-gate/nuspec 檢查全部在**第一個 push（Gitea）之前**跑完；GitHub Packages 的重新 pack 改成從 `git archive` 對一個在任何 merge-fallback 分支跑之前就先釘住的 commit SHA 抽取到一個沒有 `.git` 的乾淨目錄——merge 產生的內容不可能進到打包輸入。
- 既有 GitHub 上的同名 tag 若要被 force-move，先比對兩邊的 tree hash 是否相同；不同就拒絕，不再無條件 force-push。

**刻意沒做、且這裡明講原因**：
- 不重跑完整 .NET 測試套件或 mutation-gate（跑一次要跨越這台 2-capacity runner 的容量，而且 CI 的 `build-and-test` conclusion 欄位在本 repo 是不可靠訊號，見 CLAUDE.md「CI red does not mean failed」——引用它取代真的重跑，等於用一個已知不可靠的訊號冒充驗證，這個決定本身寫在 workflow 檔案的註解裡）。
- 這次修復撰寫期間**刻意不呼叫任何 Gitea/GitHub API、不 push 任何 tag**（含 PR 本身也未開）。以下邏輯因此只做到 bash 語法檢查 + 邏輯覆查 + 對標準 git plumbing 指令（`git fetch`/`rev-parse`/`ls-remote`）的行為推導，**沒有對 mac-mini Gitea 或 GitHub Packages 的真實 registry / 真實 tag push 端到端跑過**：`scripts/check-package-cohort.py` 的 HTTP 呼叫邏輯（只在本機對 nuget.org 這個公開、非 Gitea/GitHub 的標準 NuGet V3 端點，以及一個假造的本機 fixture server 驗證過協定正確性，見 `test/check-package-cohort-tests.sh`）；「Push release tag to GitHub」步驟的 tree-比對 force-move guard；`is_prerelease` 帶進 GitHub Release payload 那段。下次真實 tag 發版時應視為這幾段邏輯的首次生產驗證。**（#967 更正，2026-08-01）`scripts/check-package-cohort.py` 這一段的「未端到端驗證」已不成立——這支腳本原本用的 `HEAD` 探測法讓這個 gate 對 Gitea **每一次**都失敗（見下方新章節），問題在 #967 修好之後才真正對 mac-mini Gitea 跑過端到端；GitHub Packages 只驗證到一半，同見下方新章節。**
- `scripts/publish-to-gitea.sh` 的真實發佈路徑已停用（見 `docs/gitea-packages.md` §7）——這是「runner 不可用時的本機 fallback」，不是這個 gate 的一部分，過去被文件誤導成等效替代品。

**可重跑的盤點指令**（驗證上面「每次 publish 執行」清單裡各檢查確實排在第一個 push 之前，而不是憑記憶）：
```bash
grep -n '^\s*- name:' .github/workflows/publish-nuget.yml | \
  grep -B999 'Push to Gitea Packages' | tail -20
```
執行時間點：2026-07-31，對應 commit 見同一批次的 git log；上面兩份清單如果與 workflow 檔案實際內容不符，以 `.github/workflows/publish-nuget.yml` 為準，這份文件過期。

---

## E2E 測試可靠度修正（#898/#905，2026-07-31）

`test/e2e/wtm_e2e_tests.py`（36 個 TC，見上方「已驗證」的 35 pass/1 skip 數字）裡有兩類站點在改動前**不論被測行為是否真的成立都會回報 PASS**：12 個吞掉例外後直接繼續、完全沒有下游 assert 保護的 catch 分支（#898），以及 3 個安全主題測試（TC-09/10/12）全程只 print、從未 assert（#905）。CHANGELOG 對這批改動的描述不得超出以下清單——每一條斷言都在真的把對應行為打破後觀察到 FAIL，才算數。

**改動前後的狀態**：
- 用 `grep -c "_screenshot_on_failure(page, [0-9]"` 重新推導 #898 的「12 個站點」，逐一核對後**與 issue 標題吻合**：TC-04(1)、TC-24(3)、TC-25(1)、TC-26(1)、TC-27(1)、TC-28(3)、TC-29(2)。但同時發現 issue 標題沒有涵蓋的第二個問題：TC-25/26/27/28/29 這幾個函式除了那個吞例外的分支之外，**整個函式從頭到尾沒有任何 `assert`**——修好 catch 分支本身不足以讓這些測試真的可能失敗。額外找到 TC-30（零 assert，但沒有 catch 分支，不在 12 個站點清單內，其 docstring 早已引用 #898）也一併修，TC-03（CSRF，零 assert）**維持不動**——那是刻意記錄「WTM 未實作 CSRF」這個已知安全缺口的測試，不是被吞掉的例外。（**#917 後續**：TC-03 這個零 assert 缺口本身，已由下方「e2e 測試完整性 AST lint（#917）」章節改為 characterization assertions 修復。）
- 12 個 catch 分支全數把 `except Exception:` narrow 成 `except PlaywrightTimeoutError:`，其餘例外型別（真正的 JS crash、頁面已死等）現在會如實變成 ERROR 而不是被吞掉再繼續。
- 每個受影響的測試函式都補上綁定該測試自己命名行為的真斷言（面板是否真的開啟、grid 是否真的渲染出資料列、表單欄位是否真的存在、分頁元件是否真的出現……），而不是只補一個「有沒有拋例外」的空殼判定。
- TC-09（Session Fixation）重寫為真正模擬攻擊手法：登入前用探測到的驗證 cookie 名稱植入攻擊者已知的固定值，登入後斷言該值已被輪替；不是原本「印出登入前後 cookie 名稱」的空判定。
- TC-10（Security Headers）與 TC-12（Rate Limiting）調查後發現 WTM **確實有**對應的保護機制（`WtmSecureHeadersMiddleware`/`WtmRateLimitAttribute`），但都是 opt-in、demo 沒有呼叫——因此改為斷言「目前這個已知、刻意的缺席狀態」（六個 header 全數缺席／連續 10 次錯誤登入皆乾淨回應 200 不 500），而不是斷言一個 demo 從未啟用過的保護；drift（任何一個 header 意外出現、任何一次請求變成非 200）現在會被抓到。

**每個受影響測試都用「刻意打破、觀察 FAIL、還原」的方式證明過至少一條斷言真的可達**（本機 dotnet 10 + Playwright + Chromium，對著本機起的 demo app 實測，2026-07-31）：
- TC-04：暫時把 `.analysis-field-pool` 選擇器改成不存在的字串 → `analysis-panel 不存在！`。
- TC-09：暫時在斷言前把登入後 cookie 值強制覆寫回攻擊者植入的固定值 → `[Session Fixation] 驗證 cookie ... 登入後仍是攻擊者登入前植入的固定值`。
- TC-10：**真的**在 demo `Startup.cs` 暫時加一行 `app.UseWtmSecureHeaders()`（真實 middleware，非測試檔本身的 mutation）、重建、重啟 demo → `[KNOWN-GAP] demo 目前未呼叫 UseWtmSecureHeaders()...預期六個安全 header 全數缺席，但實際缺席清單為 [...]`；驗證完立刻還原、重建、重啟，`git diff` 確認 `demo/` 目錄零殘留變更。
- TC-12：暫時把其中一次請求的狀態碼結果竄改成 500 → `連續嘗試第 3 次錯誤登入時收到非預期狀態碼 500`。
- TC-24：暫時把 API payload 的 dimension 改成一個真的會被 `/_analysis/query` 拒絕的欄位名（借用 TC-20 已驗證的 400 語意）→ `Analysis API 查詢失敗：HTTP 400`。
- TC-25/26/27/28/29/30：各自暫時把一個代表性斷言的選擇器改成不存在的字串，逐一觀察到對應的 FAIL 訊息（分頁元件、表單欄位、grid、DataPrivilege 表單、EtlRunLog 篩選欄位、EtlJob 的 Searcher.Name、下載範本按鈕）。
- 沒有逐一 mutation-test 每一條新增的斷言（例如 TC-24 的 dim/msr pill 數量、TC-28 的 FrameworkMenu 選單列數）——這些與已驗證過的斷言同一種形狀（`locator(...).count() > 0`，binding 到同一類已證實可達的 DOM 結構），視為同類已覆蓋，但沒有逐條重複實測，此處明講不誇大。
- run_tests() 的彙總報告邏輯（`_compute_stats`、"Total: N \| PASS: n \| FAIL: n \| ERROR: n \| SKIP: n" 那一行、synthetic SUITE-ABORT 路徑）本次**沒有修改**；上面 11 次刻意製造的 FAIL 全部正確反映在該行的 FAIL 欄位裡（每次都手動核對過 `--tc <N>` 單獨執行的彙總輸出），沒有一次被吞掉或算成 PASS。

**過程中發現、但本 PR 刻意不動的兩個既有缺陷**（皆超出 #898/#905「只改 test/e2e」的授權範圍，需要另外開 issue 才能動 `src/`）：
1. TC-26/27/28/29/30 原本用 `page.goto()` 直接導覽到多個 grid/表單頁面（`/Student/Create`、`/_Admin/FrameworkUser/Index` 等）——實測確認這些是 PartialView-only 端點（伺服器一律回傳不含 `<html>`/`<script>` 的裸片段），繞過 layuiadmin 的 AJAX tab 載入機制會讓 `layui`/`xmSelect` 完全沒有載入（console 可觀察到 `layui is not defined`）。這是本次修復的一部分（改用 `open_grid_via_sidebar()`/`open_toolbar_dialog()`/新增的 `open_grid_via_direct_tab()`），不是遺留缺口。
2. `/_EtlJob/Index` 的「執行記錄」自訂工具列動作有一個真實、可重現的 JS 語法錯誤——根因定位到 `src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.cs` 第 1284 行 `actionScript = $"{item.OnClickFunc}(ids,ff.GetSelectionData('{Id}'));"` 沒有把 `item.OnClickFunc` 包在括號裡，產生不合法的 IIFE（`function(ids,data){...}(ids,...)`），導致整個 `<script>` 區塊解析失敗、grid 從未渲染成功。透過 `page.goto()` 與透過 layuiadmin 真正的 tab 載入路徑都會重現，與導覽方式無關。**沒有修這個缺陷**——需要改 `src/` 下的框架程式碼，沒有對應 issue 授權；TC-29 改為只斷言「頁面路由正確、不受此 bug 影響的純 HTML 欄位存在」，grid 本身與受影響的篩選欄位維持只記錄、不斷言（KNOWN-GAP，見 TC-29 docstring 的完整根因記錄）。

**仍未涵蓋、明講不假裝**：
- TC-10/TC-12 只驗證「demo 目前刻意關閉這兩個 opt-in 保護的既知狀態沒有意外漂移」，**不驗證**這兩個 middleware 真的啟用時的行為是否正確——那需要另一個對已啟用該 middleware 的部署跑的測試，不在本次範圍。
- TC-24 的拖放（drag-and-drop）路徑仍保留環境性容忍：逾時不直接判 FAIL（headless CI 上 Sortable.js 的已知時序脆弱性），但拖放失敗不再讓整個 TC 靜默 PASS——面板開啟、欄位存在、以及查詢結果都改為透過與拖放互相獨立的直接 API 呼叫做無條件斷言。
- TC-03（CSRF）維持原樣：WTM 目前沒有 CSRF token 保護，這是已知、刻意記錄的缺口，不是本次修復範圍。（**#917 後續**：已改為 characterization assertions，見下方新章節。）
- TC-29 的 EtlJob 部分是有明確根因記錄的 KNOWN-GAP（見上方），不是偷懶的軟性檢查，但也確實沒有斷言到。

**可重跑的盤點指令**：
```bash
grep -n "_screenshot_on_failure(page, [0-9]" test/e2e/wtm_e2e_tests.py   # 12 個站點清單
python3 -c "
import re
lines = open('test/e2e/wtm_e2e_tests.py').readlines()
starts = [(i, re.match(r'async def (tc_\d+_\w+)\(page', l).group(1))
          for i, l in enumerate(lines) if re.match(r'async def tc_\d+_\w+\(page', l)]
starts.append((len(lines), 'EOF'))
for idx in range(len(starts) - 1):
    s, name = starts[idx]; e = starts[idx + 1][0]
    n = len(re.findall(r'\n\s*assert ', ''.join(lines[s:e])))
    print(f'{name:40s} asserts={n}')
"
```

---

## check-package-cohort.py 的 HEAD→GET 修復（#967，2026-08-01，release-blocking）

`scripts/check-package-cohort.py`（見上一節）原本用 `method="HEAD"` 探測版本是否已存在。**Gitea 的 NuGet flat-container endpoint 對 HEAD 一律回 405 Method Not Allowed，不論該版本存不存在**——腳本只把 404 特判成「不存在」，其餘一律 re-raise 當成 fail-closed 錯誤，405 落在「其餘」，所以這個 gate **每一次 publish 都會失敗**，無論套件實際狀態如何。這是 release-blocking：`workflow_dispatch` 與 tag push 兩條路徑都會在「Verify version cohort not partially published (Gitea)」這一步卡死。修法：探測方式改成 `GET`，且不重用會把整個回應體讀進記憶體的 `fetch()` helper——改成直接開連線、靠 `urlopen` 對非 2xx 狀態碼丟 `HTTPError`（404 分支邏輯不變)、2xx 時最多讀 1 byte（`resp.read(1)`）就讓 `with` block 關閉連線，不會把整個 `.nupkg`（可能數 MB）緩衝進記憶體。

**已驗證（對真實 mac-mini Gitea registry 端到端跑過，2026-08-01）**：
- `WalkingTec.Mvvm.Core 10.18.0`（已發布的版本）→ 正確回報「1/1 already published」，exit 0。
- `WalkingTec.Mvvm.Core 10.21.0-rc.1`（未發布的版本）→ 正確回報「0/1 already published」（not yet published），exit 0。
- 完全比照 `publish-nuget.yml` 呼叫方式、六個套件、`PKG_VERSION=10.21.0-rc.1` 的完整 cohort check → `0/6 already published`，`Cohort check passed`，exit 0——即這個版本目前乾淨、可以安全發布，gate 不再誤擋。
- 修復前（`method="HEAD"`）對同一台真實 Gitea、同一個已存在版本（`10.18.0`）的實測輸出：`ERROR: could not check existence of WalkingTec.Mvvm.Core 10.18.0: HTTP Error 405: Method Not Allowed`，exit 2——這就是 release-blocking 的實際錯誤訊息，不是推導。

**GitHub Packages（`nuget.pkg.github.com`）—— 只驗證到一半，誠實揭露**：
- 已驗證：未帶 auth 的情況下，HEAD 對 `https://nuget.pkg.github.com/cct08311github/index.json`（service index）與一個合理猜測的 flat-container download URL 都回 405；同樣未帶 auth 的 GET 對同兩個 URL 回 401（正常的「需要認證」回應，代表請求有被路由/認證層處理，不是被方法層擋掉）——重複測試皆一致。這代表 GitHub Packages 對 HEAD 的拒絕方式與 Gitea 相同（方法層直接拒絕，不因路徑或認證而異），所以把探測方式統一改成 GET 對兩邊都是正確、而非只碰運氣對了一邊。
- **未驗證**：GitHub Packages 帶正確 PAT 之後，GET 能否正確區分「該版本存在（200）」與「不存在（404）」——這次工作階段沒有可用的 `GH_MIRROR_PAT`，無法測試。這一段**不宣稱已修好**，留待下一次真正的 tag 發版（`publish-nuget.yml` 的「Verify version cohort not partially published (GitHub Packages)」步驟）作為首次生產驗證。

**測試**：`test/check-package-cohort-tests.sh` 新增一個獨立的 fixture HTTP server，用自訂 handler 讓 `do_HEAD` 一律回 405（模擬 Gitea/GitHub 的真實行為），`do_GET` 對特定版本正確回 200/404，另對保留版本號 `0.0.500` 回 500（模擬非 405-masking 的真正異常）。舊的 fixture 直接用 Python `http.server` 的 `SimpleHTTPRequestHandler`，它對 HEAD 的處理是「正確」的（200/404），這正是舊測試套件從未抓到這個缺陷的原因——它跟真實 Gitea/GitHub 的行為不一樣。RED-before-fix 已獨立重現（把腳本換回 `method="HEAD"`、跑新測試案例）：`ERROR: could not check existence of WalkingTec.Mvvm.Core 10.21.0: HTTP Error 405: Method Not Allowed`，該 test case 判定 `FAIL: ... expected exit 0, got 2`。修復後 7 個 case（4 個既有 + 3 個新增）全線變綠，`check-package-cohort-tests: PASS`。**指出哪一行刪除會讓測試變紅**：把 `scripts/check-package-cohort.py` 的 `package_exists()` 內 `method="GET"` 改回 `method="HEAD"`，會讓 `test/check-package-cohort-tests.sh` 新增的「version exists -- detected correctly against HEAD-405 Gitea-like registry」與「version absent -- ...」兩個 case 從 exit 0 變成 exit 2（RED）——這兩行就是這次修復的證明。

---

## ETL 批次重試 backoff catch 把「cancellation 恰好撞上 transient 例外」誤判成資料失敗（#970，2026-08-01）

`EtlPipelineExecutor.BulkLoadWithRetryAsync`（每個 batch 的重試-with-backoff 邏輯）原本用一個 `catch when (attempt < maxRetries && !cancellationToken.IsCancellationRequested)` 過濾器決定「這次失敗要不要重試」。這個 `when` 過濾器是在 loader 拋出 transient 例外的**當下**被評估——如果 cancellation 在那個瞬間**已經**被要求，過濾器算出 false，這個 catch 就不吃這個例外；由於例外本身不是 `OperationCanceledException`，它上面的 `catch (OperationCanceledException) { throw; }` 也接不住，於是這個 transient 例外原封不動往外傳，一路傳到 `ExecuteAsync` 最外層的一般 `catch (Exception ex)`，回報 `Success=false`、**`Aborted=false`**、`ErrorMessage` 是經過 sanitize 的資料錯誤訊息——呼叫端因此把一次操作者主動取消的 run，誤判成一次普通的資料失敗（無法用字串比對補救，因為 `ErrorMessage` 根本不是 `"Job was aborted"`）。

**五條 cancellation 路徑窮舉（`EtlPipelineExecutor.cs` 行號為修復後的版本）：**

| 情境 | 命中的 catch | 修復前 `Aborted` | 修復後 `Aborted` |
|---|---|---|---|
| 1. 重試 backoff 的 `Task.Delay`（`:778`）中收到 cancellation | `:737 catch (OperationCanceledException) → throw` → 外層 `:479` | **true**（本來就對） | true（未變） |
| **2. loader 拋出 transient 例外的當下，cancellation 已經被要求** | 修復前：`:742` 的 `when` 過濾器算出 false，兩個 catch 都不吃，例外原樣外傳到 `:502`。修復後：`:742 catch (Exception)` 不再有 `when`，進入後先呼叫 `:760 cancellationToken.ThrowIfCancellationRequested()` 轉成 OCE → 外層 `:479` | **false（缺陷）** | **true（本次修復）** |
| 3. 其他 `OperationCanceledException`（`:198` 主迴圈 extract、`:864` dry-run extract、`EnsureStagingTableAsync`/`TruncateStagingAsync`/`ReplaceAsync`/`MergeAsync` 等尊重傳入 token 的呼叫） | 外層 `:479`（主 pipeline）或 dry-run 自己的 `:938` | true | true（未變） |
| 4. Dry-run 路徑（`ExecuteDryRunAsync`，完全不呼叫 `BulkLoadWithRetryAsync`） | `:938 catch (OperationCanceledException)` | true，`"Dry-run aborted."` | true（未變） |
| 5.（負控組）重試預算用盡、**從未**收到 cancellation 的真實資料失敗 | 修復前後都落到 `:502`（`if (attempt >= maxRetries) throw;`） | false | false（未變，負控組通過） |

**修法**：把第 2 條路徑的判斷從「進 catch 前的 `when` 過濾器」搬進 catch 本體——`catch (Exception)` 不再用 `when` 篩選，進入後**先**呼叫 `cancellationToken.ThrowIfCancellationRequested()`，cancellation 一旦已經被要求就在這裡直接轉成 `OperationCanceledException`（收斂到跟第 1 條路徑同一個外層 `catch (OperationCanceledException)`）；沒有 cancellation 時才照舊判斷 `attempt >= maxRetries`（用盡預算就原樣 `throw;`，真實資料失敗的分類完全不變）。**沒有**放寬 `:479` 的外層 catch 去吃更多例外類型——那會讓第 5 條（真實資料失敗）也被誤判成 Aborted，是同一種缺陷的鏡像版本。

**測試**：`test/WalkingTec.Mvvm.Etl.Test/Pipeline/RetryWithBackoffTests.cs` 新增 `MockBulkLoader.OnBeforeTransientFailureThrown`（`src/WalkingTec.Mvvm.Etl/Testing/MockBulkLoader.cs`，隨框架發布的測試輔助類別新增的 event，在模擬的 transient 例外離開該方法前**同步**觸發）取代舊測試唯一依賴的 `cts.CancelAfter(100)` 對抗 1000ms base delay 的 wall-clock 賽跑——**這條賽跑本身就是 flake 的來源，不是單純的計時巧合**：full-jitter backoff 抽 `Random.Shared.NextInt64(0, ceilingMs + 1)`，早期嘗試若抽到很小的 jitter，100ms 內可能已經跑過好幾次重試，剛好在某次 throw 的瞬間被取消，直接撞上第 2 條路徑的缺陷。

- `Cancellation_during_retry_backoff_aborts_immediately`（既有，重寫）——用 `TaskCompletionSource`＋`RunContinuationsAsynchronously` 等待第一次 transient 失敗「已經拋出」的訊號才呼叫 `cts.Cancel()`，並把 base/max delay 拉大到 1,000,000/2,000,000ms，讓 full-jitter 抽到剛好 0（會整段跳過 `Task.Delay`）的機率從原本的約 1/2001 降到約 1/2,000,001。修復前後都綠，驗證第 1 條路徑未受影響（修復前單獨跑過，見下）。
- `Cancellation_already_requested_when_transient_thrown_aborts_immediately`（新增）——在 `OnBeforeTransientFailureThrown` 事件內**同步**呼叫 `cts.Cancel()`，決定性地重現「cancellation 已經被要求、loader 才拋出 transient 例外」這個瞬間，不靠任何計時。`MaxBatchRetries = 0` 是刻意選擇，不是隨手帶的參數：跑 mutation gate 時第一版用 `MaxBatchRetries = 3` 曾經讓移除 `ThrowIfCancellationRequested()` 的 mutant 判定成 `UNEXPECTED_RED`（`Assert.IsFalse failed.`，即整個 run 意外成功）——原因是拿掉檢查後程式碼會落到 `attempt++`/`Task.Delay`，而 `Task.Delay` 對「呼叫當下 token 已經被取消」自己就會短路成已取消的 Task，讓 mutant 有機率仍然意外收斂到 `Aborted=true`（或若 full-jitter 剛好抽到 0、整段跳過 `Task.Delay`，甚至讓重試無聲成功）——兩種情形都不是決定性地證明這個測試真的在測 `ThrowIfCancellationRequested()` 這一行。改成 `MaxBatchRetries = 0` 後，`attempt >= maxRetries` 立刻成立，能讓程式碼走到 `Aborted=true` 的唯一路徑就只剩 `ThrowIfCancellationRequested()` 本身，mutant 才會每次確定性地被抓到。**RED-before-fix**（暫時把 production 修法還原、只保留測試改動後實測）：`Assert.IsTrue failed. Cancellation already requested when the transient exception is thrown must still surface as Aborted.`——同一輪跑其餘 8 個既有測試全綠（含上面的 `Cancellation_during_retry_backoff_aborts_immediately`），證明缺陷只影響第 2 條路徑，不是測試環境或 mock 改動本身的問題。同一個 `MaxBatchRetries = 0` 形狀也補進了既有的 `Default_no_retry_first_failure_aborts_job`（新增 `Assert.IsFalse(result.Aborted, ...)`），跟這條新測試組成一組乾淨的最小對照組：相同設定下，唯一的差異是有沒有 cancellation。
- `Failures_beyond_budget_abort_job_with_retry_count`（既有，新增一行負控組斷言）——`Assert.IsFalse(result.Aborted, ...)`：重試預算用盡、全程沒有 cancellation 的真實資料失敗必須維持 `Aborted=false`。沒有這條斷言，一個把 `Aborted` 無條件設成 true 的錯誤修法會同時通過前兩條測試卻仍然是錯的。

**穩定性**：`RetryWithBackoffTests` 整個測試類別（10 個測試方法，含上述兩個 cancellation 測試）連續執行 **50 次，50/50 全綠**，0 flake——驗證新的訊號式同步機制（而非計時）確實消除了原本的 wall-clock 賽跑。

**Mutation gate**：`test/mutants/entries/etl970-cancellation-classification-guard-neutralize.json`，移除修法核心的 `cancellationToken.ThrowIfCancellationRequested();`（`:760`）呼叫（compile-preserving——`cancellationToken` 在同方法其餘兩處仍被使用，不會產生未使用變數警告）。`VERDICT: KILLED`。**`kind` 選擇與理由**：本缺陷是「cancellation 分類錯誤」的可觀測性／正確性問題，不涉及未授權存取、injection、租戶隔離或憑證——不是傳統意義的安全漏洞。但 `run_mutant.py` 的 `VALID_KINDS` 目前只接受 `security`／`selftest` 兩種，`selftest` 明文保留給測試 runner 自身邏輯（見 `test/mutants/manifest.json` 的 `$comment`），不適用於一個真實的 production mutant。在現有 schema 下 `security` 是唯一能讓這個 mutant 被 CI 的 `mutants` job 實際執行、且非 KILLED 會擋 gate 的功能性選項，因此選了 `security`，但誠實記錄：這會把 `security`-kind entry 數從 60 推到 61，讓 #968（gate 逐項 timeout budget 是照 45 個 entry 的公式推導，在 60 個時已經吃緊）的落差再拉大一點——本次修復沒有動 #968 本身（scope 之外），值得另開一個「幫非安全性 mutant 加一個新 kind」的 issue，但 HARD CONSTRAINT 禁止本次呼叫任何 Gitea API 開票，故僅在此與 CHANGELOG 明講，留待 user 自行決定是否開票。

**已知、本次沒有稽核／沒有動的相關路徑（誠實揭露，不是缺陷清單的延伸）**：`EtlPipelineExecutor.cs` 裡另外三個 dead-letter 清理／flush 呼叫（`:157` 執行前清理、`:344` 週期性 flush、`:448`/`:460` 成功後 flush）全部包在會吞下**所有**例外（含 `OperationCanceledException`）且從不 rethrow 的 best-effort try/catch 裡——cancellation 若剛好撞上這幾個呼叫，不會立刻讓這次 run 中止，但也不會被永久遺失，下一個會檢查 token 的地方（例如下一輪 `:198` 的 `ThrowIfCancellationRequested()`）仍然會抓到；這是修復前就存在、刻意設計的 best-effort 語意，本次修復沒有觸碰。另外，`:412`–`:435` 的 `AddLineageRecordAsync`（僅 `EnableLineage=true` 時執行）沒有包在任何吞例外的 catch 裡——如果 cancellation 剛好在 merge 與 watermark commit 都已經成功之後、寫 lineage 記錄的當下才被要求，整個 run 會回報 `Aborted=true`，即使實際的資料載入已經完全成功；這條路徑機制上正確收斂到 `:479`（跟第 1/3 條路徑同一機制，不是本次修復動過的程式碼），但「run 明明成功了卻回報 Aborted」是不是正確的語意，是本次 issue 沒有要求、也沒有稽核過的獨立問題，這裡只誠實點名，不宣稱已經處理。

---

## e2e 測試完整性 AST lint（#917，2026-08-01）

`#898`/`#905`（見上方章節）用手動盤點修掉了兩類「TC 不論被測行為是否成立都回報 PASS」的既有站點。這一項是同一個設計審查裁定的機制化跟進：**加一個 CI-enforced 的 AST lint，把同一個缺陷類別變成合併前一定會擋下的錯誤，而不是靠下一次手動盤點才發現**。裁定明講這是**絕對規則，不是 ratchet**——沒有 baseline 檔、沒有 exemption 清單、沒有凍結違規數；理由是 count-based ratchet 有 swap hole（刪一個舊違規、加一個新違規，計數不變、gate 照樣綠），而且本 repo 自己的 coverage ratchet 已經證明過人工調高的門檻只會停滯不動。

**新腳本 `scripts/check-e2e-test-integrity.py`**（Python stdlib `ast`，零第三方依賴，只讀 AST、從不 `import` 目標檔案）對 `test/e2e/wtm_e2e_tests.py` 做四項檢查：(1) 每個註冊在 `TC_REGISTRY` 裡的 TC function 自己的 body 裡（含 if/for/while/with/try 內部，但不跨進巢狀 def/lambda/class）至少要有一個 `assert`；(2) 唯一的豁免是**結構**辨識、不是名單——body 恰好是一段 docstring 加一個無條件 `raise TestSkipped(...)`（tc_36 現在的形狀）；(3) 任何 `try` 的 body 裡有 literal assert 時，能接住 `AssertionError` 的 handler（bare except／`except Exception`／`except AssertionError`／上述任一的 tuple 形式）必須以 bare `raise` 重新拋出，否則判定違規——特別會抓 `except AssertionError: raise TestSkipped(...)` 這種把 FAIL 洗成 SKIP 的形狀；(4) 每個頂層 `tc_*` function 必須真的被 `TC_REGISTRY` 引用到（#855 defect 4 的 e2e 版本：寫了但沒接上）。額外獨立一條：`assert <constant truthy>`（例如 `assert True`）本身就是違規，不論出現在哪裡。Exit code 比照本 repo `changes` job 既有四個 guard 的慣例：0 乾淨、1 有違規、2 無法分析（parse 失敗／檔案不存在／找不到 `TC_REGISTRY`）。

**這支 lint 刻意不抓的東西，明講不假裝完整**：非常數的 tautological assert（例如剛設完值就斷言同一個值，或 `assert x == x`）——這需要資料流分析，靜態語法做不到；一個 helper function 裡有 assert、但呼叫它的 TC 自己 body 裡沒有——check (1) 只看 TC 自己的 scope，不追呼叫圖，刻意如此（修法維持「補一個 assert」這種低摩擦動作，不逼人重構 helper）；多層間接吞例外（內層 try 乾淨 re-raise，外層 try 又吞掉）——check (3) 只看每個 `try` node 自己的 body，不追蹤例外跨多層 try 的傳播路徑，本 repo 目前找到的每一起事故（#898、#905）都是單層；以及作者在同一個 PR 裡同時改這支 lint 跟它自己的 `--selftest` fixture——PR diff 裡看得到，沒有任何 lint 機制能防住審查者不看 diff 這件事。

**重新推導的違規盤點（本次工作獨立重新掃描全檔，不沿用先前盤點）**：對 PR 修復前的 `test/e2e/wtm_e2e_tests.py`（36 個 TC，36 個都有註冊）跑這支 lint，checks (3)/(4)/加碼 constant-assert 檢查全數乾淨（0 違規）；check (1)/(2) 找到**恰好一個**違規：`tc_03_csrf_token`（line 412），零 assert、不符合 `tc_36` 的結構豁免。沒有找到清單之外的額外違規。

**驗證這支 lint 真的會擋下違規——兩個獨立證明，皆可重跑**：

1. **`--selftest`，每次 CI 執行都會重新驗證**（embedded fixture，無外部檔案）：zero-assert TC → exit 1，訊息點名 `tc_01_no_assert`；bare-except 吞掉一個真 assert 的 handler → exit 1；`except AssertionError: raise TestSkipped(...)` 洗白形狀 → exit 1，訊息含 `TestSkipped`；一個定義了但沒接進 `TC_REGISTRY` 的 `tc_*` function → exit 1，訊息點名 `tc_02_orphan`；**clean fixture（positive control）→ exit 0**——沒有這條，一支永遠回傳 1 的假 lint 會通過上面每一個負面案例；unparseable input → exit 2，與 0/1 明確有別。六個 case 全部通過（`python3 scripts/check-e2e-test-integrity.py --selftest`，本機實測 exit 0）。
2. **對一個真實歷史 commit 的永久可重跑 replay**：`git show 59444a657:test/e2e/wtm_e2e_tests.py > /tmp/old.py && python3 scripts/check-e2e-test-integrity.py /tmp/old.py` 對 commit `59444a657`（`origin/test/898-905-e2e-cannot-fail` 分支的 tip，該分支仍在 Gitea 上，任何人都能重新 fetch）——這個 commit 的 `tc_03_csrf_token` 仍是零 assert（含 `#898`/`#905` 那次修復也刻意沒動它，見上方章節）——本機實測：exit 1，違規訊息點名 `tc_03_csrf_token`。對本 PR head（含下方 tc_03 修復）跑同一支 lint：`OK: no e2e test integrity violations found`，exit 0。

**CI 接線**：加進 `.github/workflows/mutation-gate.yml` 的 `changes` job——本 repo唯一兩個 trigger 都沒有 path filter、且已透過 `gate` job 掛成 required check 的 job，跟既有四個 guard（#924/#931×2/#926）同一種形狀。這步驟先跑 `--selftest`、`set -e` 確保 selftest 失敗會擋下後面的真掃描，再跑對 `test/e2e/wtm_e2e_tests.py` 的真掃描，兩者的 exit code 直接變成這個 step 的 exit code。**沒有新增 required-check context**（#838/#844 的教訓：workflow trigger 本身沒有 path filter，所以不會出現「永遠 expected、卡住合併鍵」的陷阱）——這一項只是在既有 `changes` job 裡多加一個 step。**這裡沒有、也不能宣稱「已在 Gitea CI 上跑過一次綠燈」**：這次工作階段的硬性限制禁止呼叫任何 Gitea/GitHub API、禁止開 PR，所以 CI 真的觸發、`gate` job 真的把這個 step 的結果算進最終判定，要等這個分支真正開 PR 之後才會是第一次生產驗證；本機驗證只到「`python3 -c "import yaml"` 剖析整份 workflow 檔案成功、新 step 出現在 `changes` job 的正確位置」與「`scripts/audit-workflow-timeouts.py`（本身也是 `changes` job 的既有 guard之一）對修改後的 `mutation-gate.yml` 判定全部 127 個 real-work step（含這個新 step 自己）都有 `timeout-minutes`，PASS」這兩層靜態確認。

**`tc_03_csrf_token` 的修復（唯一違規，P0，本 PR 內修復）**：原本這個函式只 print `[KNOWN-GAP]` 訊息、無條件回傳，記錄「WTM 未實作 CSRF token」這個已知安全缺口但完全沒有 assert 保護——不論 CSRF 有沒有被實作，這個 TC 永遠 PASS。改為 **characterization assertions**（比照 `test/WalkingTec.Mvvm.WorkFlow.Test/TenantFilterInvariantTests.cs` 的 `WfDemoShapedObsoleteContext` 同一種誠實作法：釘住觀察到的現狀，不是宣稱現狀是規格）：`assert token_count == 0`（頁面上沒有 `__RequestVerificationToken`）、`assert response.status == 200`（無 token 的 POST 被無條件接受，不是被拒絕）。**這兩個 marker 字串/數值皆對著本機真的起的 demo app 實測過，不是照抄 sibling 測試假設存在**——本機以 dotnet 10 + Playwright + headless Chromium 起 demo app（`dotnet run -c Release --no-build --urls http://0.0.0.0:52837`），單獨跑 `python3 wtm_e2e_tests.py --tc 3`，實際觀察輸出：`  __RequestVerificationToken 數量: 0` 與 `  無 Token POST 回應: HTTP 200`，兩者與新增的斷言完全一致，TC-03 本身 PASS。一旦 CSRF 保護被實作，這兩個斷言會如預期地變紅——這是刻意設計，紅燈本身就是這個測試存在的意義，需要被重寫成驗證保護生效，而不是驗證保護不存在。

**e2e 全套件本機重跑（before/after，非 CI 執行，明講原因）**：這次工作階段的硬性限制禁止呼叫任何 Gitea/GitHub API，所以無法觸發 Gitea Actions 上的真實 e2e workflow；改為本機起 demo app（同上）與全 36 個 TC，兩次都在同一台機器、同一個本機 demo app 實例上跑：

- **修復前**（`tc_03` 仍是零 assert）：`Total: 36 | PASS: 35 | FAIL: 0 | ERROR: 0 | SKIP: 1`
- **修復後**（`tc_03` 改為 characterization assertions，且加了 lint 但目標檔案本身乾淨）：`Total: 36 | PASS: 35 | FAIL: 0 | ERROR: 0 | SKIP: 1`——與修復前逐位元組相同，`tc_03` 本身也維持 PASS（1.4s~1.9s，兩次執行時間微幅浮動屬正常），因為新斷言釘住的正是本機實測到的現狀，不是改變了任何行為。這證明**這次修復沒有改變任何一個 TC 的最終判定**，只是讓 `tc_03` 從「不可能失敗」變成「現在會如實反映現狀，且現狀改變時會變紅」。

**可重跑的盤點指令**：
```bash
python3 scripts/check-e2e-test-integrity.py --selftest   # 六個 embedded fixture，見上方
python3 scripts/check-e2e-test-integrity.py               # 對 test/e2e/wtm_e2e_tests.py 的真掃描，PR head 上應為 exit 0
git show 59444a657:test/e2e/wtm_e2e_tests.py > /tmp/old_wtm_e2e.py
python3 scripts/check-e2e-test-integrity.py /tmp/old_wtm_e2e.py   # 應 exit 1，點名 tc_03_csrf_token
python3 scripts/audit-workflow-timeouts.py                 # 確認新 step 也有 timeout-minutes（#926 既有 guard）
```

---

## Vue3Demo ClientApp CI 建置閘門（#941/#939/#940，2026-08-01）

`#941` 主張 `demo/WalkingTec.Mvvm.Vue3Demo/ClientApp`（獨立的 Vite/Vue3 前端，.NET 方案本身的建置完全不會碰到它）**在 CI 裡從來沒有任何建置閘門**，標題稱這是「三個必現缺陷同時存活」的唯一解釋。這次工作**先加閘門、對著修復前的樹實測確認會紅、再修**，而不是先修再補閘門（後者無法證明閘門真的會抓到這一類缺陷）。

**重新推導與 issue 標題比對**：
- **`#939`**（`npm ci` 因 Dependabot 把 `vite` 升到 `^7.3.2` 但沒有同步升 `@vitejs/plugin-vue`〔仍 `^4.1.0`〕而直接失敗，peer 衝突）——**本機重現，與標題完全一致**。對修復前的樹跑 `npm ci`，實際輸出：`npm error ERESOLVE could not resolve` / `peer vite@"^4.0.0" from @vitejs/plugin-vue@4.4.0` / `Found: vite@7.3.5`，exit 1。`git log -S vite -- .../package.json` 確認正是 Dependabot commit `a6e9a32ec`（`4.5.14` → `7.3.1`）造成，未動 `@vitejs/plugin-vue`。
- **`#940`**（`DashboardView.vue` 用裸 `@/` 前綴，但 `vite.config.ts`/`tsconfig.json` 只註冊 `/@/`〔前導斜線〕alias，`vite build` 解析失敗）——**本機重現，與標題完全一致**。修好 `#939` 後跑 `npm run build`，實際輸出：`[vite]: Rollup failed to resolve import "@/utils/dashboard/responsive" from ".../DashboardView.vue?vue&type=script&setup=true&lang.ts"`，exit 1。`grep -rlE "from ['\"]@/" src` 全樹搜尋確認：117 個檔案正確使用 `/@/`，只有 `DashboardView.vue` 這一個檔案的兩行（原 53、54 行）用裸 `@/`。
- **`#941`**（三個必現缺陷同時存活）——這次工作階段只拿到 `#939`/`#940` 兩個具名 issue 的**標題**，且硬性限制禁止呼叫任何 Gitea API，**無法讀取任一張 issue 的完整內文**，所以無法逐字確認標題裡「三個」具體所指是否就是下面獨立發現的第三類缺陷。可以確認的是：把 `#939` 單獨修好後，`npm ci` **並未變綠**——連續浮現三個先前完全被 `#939` 擋住、從未被任何人或任何 CI 跑到過的額外 peer-dependency 衝突（見下）。這與標題「三個缺陷」的計數相符，但這是本次工作獨立重新推導出來的，不是對 issue 內文的確認，在此誠實記錄這個落差。

**修 `#939` 之後才浮現、原本被同一個 `#939` 擋住的額外衝突（不在原三個 issue 編號內，誠實揭露，非本次任務原始範圍但阻擋 gate 變綠、因此一併處理）**：

1. **`echarts-gl`**：`package-lock.json` 鎖定的 `2.0.9` 版 peer 只接受 `echarts@^5.1.2`，但 `echarts@^6.1.0`（Dependabot 早於本次工作合併，commit `6108812e9`）已經在同一份 lockfile 裡——`npm ci` 因此在 `#939` 修好後立刻報第二個 ERESOLVE（`Found: echarts@6.1.0` / `peer echarts@"^5.1.2" from echarts-gl@2.0.9`）。`npm view echarts-gl@2.1.0 peerDependencies` 顯示同一個 semver 範圍（package.json 宣告的 `^2.0.9`）內已有相容版本（`{echarts: '^5.1.2 || ^6.0.0'}`），純粹是 lockfile 從未刷新過，不是 package.json 版本範圍的問題。
2. **`echarts-wordcloud`**：最新已發布版本（`2.1.0`，也是目前鎖定的版本）peer 仍只接受 `echarts@^5.0.1`——**沒有任何已發布版本支援 echarts 6**，這不是刷新 lockfile 能解決的，是上游套件尚未跟上。
3. **`@types/node`**：`^18.15.11`（package.json 原值）vs. `vite@7.3.5` 的 `peerOptional @types/node@"^20.19.0 || >=22.12.0"`——`vite@7.3.5` 自己的 `engines.node` 也要求同一個下限，代表 Dependabot 升 `vite` 到 `7` 那次 commit，已經把這個專案「建置所需的最低 Node 版本」從 package.json 宣稱的 `>=16.0.0` 悄悄拉到 `^20.19.0 || >=22.12.0`，只是從未被任何 CI 步驟驗證過，`engines.node` 欄位本身這次沒有一併更正（範圍外，留待日後）。

**修法（全部落在 devDependency／未使用 dependency 層級，沒有動任何 `src/` 執行邏輯，除了 `#940` 那兩行 import 路徑）**：
- `grep -rn "echarts-gl\|echarts-wordcloud"` 全樹搜尋（含 `.vue`/`.ts`），確認這兩個套件除了 `src/utils/build.ts` 裡被整段註解掉（`//` 開頭）的 CDN 設定清單外，**完全沒有任何 import 站點、沒有任何其他套件透過 transitive dependency 需要它們**（`package-lock.json` 的 `packages` 圖確認唯一 consumer 是根專案自己）——直接從 `package.json` 移除，`package-lock.json` 隨 `npm install` 自然刷新（`echarts-gl`、其專屬 transitive dep `claygl`、`echarts-wordcloud` 三個 node_modules 條目一併消失）。`src/utils/build.ts` 裡的註解殘留沒有清理（見下方「沒有涵蓋的部分」）。
- `@types/node` 從 `^18.15.11` 升到 `^20.19.0`——純型別宣告套件，不影響任何 runtime 行為；範圍選在 vite 7 peer 允許的下限，也對齊本次新增 CI gate 用的 `actions/setup-node@v5` 的 `node-version: '20'`。
- `@vitejs/plugin-vue` 從 `^4.1.0` 升到 `^6.0.8`（`#939` 本身的修法）——`npm view @vitejs/plugin-vue@6.0.8 peerDependencies` 確認 `vite: '^5.0.0 || ^6.0.0 || ^7.0.0 || ^8.0.0'`，涵蓋既有的 `vite@^7.3.2`。
- `DashboardView.vue` 兩行 import 的 `@/utils/dashboard/responsive` 改成 `/@/utils/dashboard/responsive`（`#940` 本身的修法）——與 `vite.config.ts` 的 `alias: {'/@': pathResolve('./src/')}`、`tsconfig.json` 的 `"paths": {"/@/*": ["src/*"]}` 對齊，也與樹上其餘 117 個既有正確用法一致。
- `package.json` 是 CRLF 檔案（`git ls-files --eol` 確認）；上述每一處修改都用 binary-safe（`open("rb")`/`open("wb")`）字串替換完成，逐位元組核對過改動前後只有目標那一行的版本號不同、換行符沒被 LF 化。

**Gate 本身（`#941`）**：新增 `.github/workflows/vue3demo-build.yml`。`pull_request`/`push`（限 `dotnet10`）都用 `paths: ['demo/WalkingTec.Mvvm.Vue3Demo/ClientApp/**']` 限定；另保留 `workflow_dispatch` 供手動觸發。單一 job `vue3demo-build`：checkout → setup-node（Node 20）→ 快取 `~/.npm`（`continue-on-error: true`，key 綁 `package-lock.json` hash）→ `npm ci`（`working-directory` 限定在 `ClientApp`）→ `npm run build`。**快取只蓋 npm 自己的下載暫存（`~/.npm`），不快取 `node_modules`、也不會讓 `npm ci` 本身被跳過**——`npm ci` 每次都會對著 committed lockfile 重新做一次完整的 peer-dependency 解析，這正是 `#939` 失敗的那一步；快取命中只省下重新下載已驗證過的 tarball，不會、也不能讓這一步被略過。

**RED-before-fix（gate 的兩個指令分別對 pre-fix 樹實測，不是推導）**：
- `npm ci`：`git stash` 暫時擋住 `#939`/`#940` 的修法後，`npm ci` exit 1，輸出 `Conflicting peer dependency: vite@4.5.14` / `peer vite@"^4.0.0" from @vitejs/plugin-vue@4.4.0`。
- `npm run build`：用 `--legacy-peer-deps` 跳過 peer 檢查把套件裝進 `node_modules`（僅為了越過 `#939` 去獨立驗證 `#940` 這一步本身，gate 本身的 `npm ci` 從不加這個 flag），`npm run build` exit 1，輸出 `[vite]: Rollup failed to resolve import "@/utils/dashboard/responsive" from ".../DashboardView.vue..."`。

**GREEN-after-fix（同樣兩個指令，對修復後的樹實測）**：`rm -rf node_modules dist && npm ci` exit 0（361 個套件裝妥，0 個 ERESOLVE）；`npm run build` exit 0，產出 `dist/index.html` 與完整 `assets/`（`vite v7.3.5 building client environment for production... ✓ 2615 modules transformed. ... built in 7.21s`）。

**Path filter 雙向證明（用 workflow 檔案裡實際的 filter 字串，非改寫）**：filter 為 `demo/WalkingTec.Mvvm.Vue3Demo/ClientApp/**`。用 Python `fnmatch` 模擬 GitHub Actions 的路徑 glob 語意（官方文件：`**` 比對任意字元、含路徑分隔符，等價於把這個 pattern 裡唯一的 `**` 換成單一 `*`）本機驗證：這次 PR 實際修改的三個 ClientApp 內檔案（`package.json`／`package-lock.json`／`DashboardView.vue`）全部 MATCH；`.github/workflows/vue3demo-build.yml` 自己、`CHANGELOG.md`、`docs/production-readiness.md`、以及**同一個 demo 專案內、僅僅在 `ClientApp/` 之外的手足檔案**（`demo/WalkingTec.Mvvm.Vue3Demo/Program.cs`、`.csproj`、`DataContext.cs`）與完全不同的 demo（`WalkingTec.Mvvm.Demo`）全部 NO MATCH。這特別驗證了「filter 差一點就整個不會觸發」這個已知陷阱（本 repo 已有 lint 活在 `paths-ignore` 排除範圍內、從未真的跑過的先例）——最接近的反例（同目錄樹但在 `ClientApp/` 之外的 `.cs`/`.csproj`）也正確地不觸發。

**Timeout 稽核**：`python3 scripts/audit-workflow-timeouts.py` 在新增 `vue3demo-build.yml`（5 個 real-work step：checkout／setup-node／cache／`npm ci`／`npm run build`，全部帶 `timeout-minutes`）後，全庫 8 個 workflow 檔案、132 個 real-work step，132 個都有 `timeout-minutes`，`WORKFLOW_TIMEOUT_AUDIT_RESULT: PASS`（新增前為 127 個 step 全過）。

**新增的每次觸發 wall-clock 成本**：本機（非目標的 4-CPU self-hosted Gitea runner，warm npm cache）量測：`npm ci` 3.67s、`npm run build` 7.05s（vite 自報 `built in 7.21s`）。**這不能直接當作 runner 端實測值**——checkout／setup-node／cache 還原、以及 runner 上冷的 npm registry 下載都會另外加時間，而這次工作階段的硬性限制（禁止呼叫任何 Gitea/GitHub API、禁止開 PR）代表這支 workflow **從未在真實 Gitea Actions 上跑過一次**，無法給出 runner 端實測數字。硬上限是各 step `timeout-minutes` 總和 33 分鐘（5+5+5+8+10），這是超時就會被砍掉的天花板，不是預期耗時；保守推算單次觸發落在 1–3 分鐘量級，但這是推算、不是實測，且只在 PR 的 diff 真的碰到 `demo/WalkingTec.Mvvm.Vue3Demo/ClientApp/**` 時才會觸發（見上方 path filter 證明）。

**這個 gate 沒有涵蓋的部分（誠實揭露）**：ClientApp 目前沒有任何 Vue/JS 測試套件，這次也沒有新增一個——gate 只證明 `npm ci && npm run build` 這兩個指令仍然成功，**不驗證任何執行期行為**（沒有 unit test、沒有 e2e、沒有 lint、不驗證 `dist/` 產物在瀏覽器裡實際能跑）。`src/utils/build.ts` 裡註解掉的 CDN 設定清單仍保留對 `echarts-gl`／`echarts-wordcloud` 的字串引用（純註解，`//` 開頭，不影響任何建置或執行），本次沒有清理，屬於低風險文件殘留，未另開 issue。`npm audit` 回報 4 個 high severity 漏洞——這是既有狀態，本次工作只移除套件、沒有新增任何 runtime dependency，沒有處理也沒有加劇，屬於未經本次工作稽核的既有技術債。

**未驗證/無法驗證**：這支 workflow 從未在真實 Gitea Actions runner 上執行過一次（見上，硬性限制禁止開 PR／呼叫 API）；上述「額外浮現的三個衝突」是否恰好就是 `#941` 標題所稱的「三個必現缺陷」，無法對照 issue 原文確認。

**可重跑的盤點指令**：
```bash
cd demo/WalkingTec.Mvvm.Vue3Demo/ClientApp
rm -rf node_modules dist
npm ci        # 應 exit 0（fix 前 exit 1，見上方 RED-before-fix）
npm run build # 應 exit 0，產出 dist/index.html（fix 前 exit 1，見上方 RED-before-fix）
cd ../../../..
python3 scripts/audit-workflow-timeouts.py   # 應 PASS，132 個 real-work step 全帶 timeout-minutes
```

---

## LookupCache RefreshAsync 的兩個姊妹缺陷：#943（DistributedLookupCacheService 逾時繞過）與 #944（LookupCacheService 遺漏 registry 檢查）（2026-08-01）

兩者都是 #804 修復當時「已發現、當時不修、記錄在該條目、另立 issue」的追蹤項（見上方 #804 條目已更新的收尾句），本次各自獨立修復，同一 PR 提交但分開兩個 commit。**先確認兩者是否真的如 issue 標題所述**，再動手：

**#943 —— 逐字比對後確認：verbatim 同款缺陷。** `DistributedLookupCacheService.RefreshAsync<T>`（修復前 `DistributedLookupCacheService.cs:345-362`）算出 `bool acquired = await semaphore.WaitAsync(StampedeTimeout, ct)` 卻從未在繼續 `Invalidate`/`LoadFromDbAsync`/`SetDistributedAsync` 前檢查它——跟 #804 修復前的 `LookupCacheService.RefreshAsync` 逐行同形狀。此類別自己的 `GetAll`/`GetAllAsync` 早就有正確的 `if (!acquired)` 回退（直接回傳 DB 結果、不寫快取），只有 `RefreshAsync` 沒跟上，跟 #804 是同一種「一個呼叫點漏掉」缺陷。**修法**：在 `try` 區塊內、`Invalidate` 之前加上 `if (!acquired) { 記 warning; throw TimeoutException; }`，訊息與行為對齊 #804 已修復的 `LookupCacheService.RefreshAsync`。

**唯一真正的差異，且屬於本次修復範圍**：跟 #804 不同，這個檔案的 `StampedeTimeout` 一直是寫死的 `private static readonly TimeSpan StampedeTimeout = TimeSpan.FromSeconds(10)`——從未像 #804 把 `LookupCacheService` 的同名欄位接到 `LookupCacheOptions.StampedeTimeout`，即使建構子早就接收並保存了 `LookupCacheOptions? options` 到 `_options`。不修這條，測試就無法在不真的等 10 秒的情況下觸發逾時分支——跟 #804 當初接上這條設定的理由完全一樣。改成 `private TimeSpan StampedeTimeout => _options.StampedeTimeout;`，預設值不變（10 秒），純粹是可測試性/一致性修正，不是缺陷本身。

**#944 —— 從程式碼重新推導後確認：真實但範圍比字面標題更精確。** `LookupCacheService.RefreshAsync<T>`（#804 修復後的現狀，`if (!acquired)` 逾時檢查已存在）沒有它自己的手足方法 `GetAll`/`GetAllAsync` 開頭就有的 Bug #112(2) 防護：`if (!_registry.ContainsKey(typeof(T))) { return LoadFromDb<T>(dc); }`。`RefreshAsync` 完全沒有對應檢查，任何 `T`（不論是否註冊）都會直接跑到 `Invalidate`/`LoadFromDbAsync`/`SetCache`。**為什麼這是真缺陷（從 `SetCache` 自己的程式碼重新推導，非採信 issue 文字）**：`SetCache<T>` 只有在 `_registry.TryGetValue(typeof(T), out var attr)` 成功時才設定 `entry.AbsoluteExpirationRelativeToNow`；對未註冊型別，這個分支被跳過，該筆快取沒有 TTL，也沒有可用的 per-type CTS 失效路徑（`InvalidateType` 從來沒有人會對一個沒被登記成 lookup 型別的東西呼叫）——於是這筆快取項會在 `IMemoryCache` 裡活到 process 重啟或 size-based 逐出壓力發生為止。**「未註冊」的定義直接取自 registry 自己的邏輯**（`IsCacheable(Type) == _registry.ContainsKey(entityType)`，`_registry` 只收錄具體、非抽象、掛 `[CacheLookup]` 的 `TopBasePoco` 子型別），不是猜測。生產路徑可觸及：`WTMContext.RefreshLookupAsync<T>()`（`WTMContext.LookupCache.cs:137`）呼叫 `svc.GetAttribute(typeof(T))`（未註冊型別回傳 `null`，沒有任何 guard），接著無條件呼叫 `svc.RefreshAsync<T>(...)`。**修法**：在方法最前面（`BuildKey`/`_keyLocks.GetOrAdd`/任何 semaphore 動作之前）加上同一個 registry 檢查，未命中就直接 `return`（no-op）——跟 #804/#943 的逾時分支不同，這裡不是操作失敗（沒拿到鎖），而是這個型別本來就從未被要求快取，靜默不做事才是正確語意，不是假象。不影響同方法內既有的 #804 `if (!acquired)` guard（獨立、更早的第二道檢查）。

**曾經 out of scope，現已修復（見下方新章節）**：`DistributedLookupCacheService.RefreshAsync` 結構上有相同的「缺 registry 檢查」缺口，但它的 `SetDistributedAsync` → `BuildCacheEntryOptions(attr)` 對未註冊型別的 fallback 是**有界的 30 分鐘 TTL**（`attr != null ? TimeSpan.FromMinutes(attr.TtlMinutes) : TimeSpan.FromMinutes(30)`），不是不死快取——是同一家族裡程度較輕、範圍不同的變體，不是 #944 標題所述「永不過期」那個症狀。當時（#943/#944 修復當下）刻意不修，因為它不是 #943 或 #944 字面所指的缺陷，擴大修復範圍會偏離兩張 issue 各自的授權範圍；已依此誠實記錄另立 issue #975，本次工作階段一併修復——見下方「DistributedLookupCacheService.RefreshAsync 缺 registry 檢查——#944 留下的第三個實例（#975）」章節，此段落不再是 stale 的「留待使用者自行決定」。

**測試（TDD 順序：先寫測試對著未修的程式碼跑紅，再實作，再確認變綠）**：

- `test/WalkingTec.Mvvm.Core.Test/Cache/DistributedLookupCacheStampedeRefreshTimeoutTests943.cs`——直接沿用 #804 測試檔（`LookupCacheStampedeRefreshTimeoutTests804.cs`，同 namespace）裡的 `StampedeTestHelper804.CreateFileWalContext`、`CountingDelayReaderInterceptor804`、`StampedeCityContext`、`CityCode`，換上 `DistributedLookupCacheService` + `MemoryDistributedCache`。Holder 用 500ms 延遲的 `GetAllAsync` 持鎖，refresher 用 80ms 的 `StampedeTimeout`（與 #804 測試已驗證可靠的相同數值）——這兩個數值經獨立確認在本機連續執行下沒有 flake。**RED-before-fix（實測，逐字）**：`Assert.ThrowsException failed. Expected exception type:<System.TimeoutException> but no exception was thrown. ` 刪掉修法裡的 `if (!acquired) { ...; throw new TimeoutException(...); }` 區塊，這條測試就會變紅。**負控組**：`RefreshAsync_NoContention_CompletesAndCachesNormally_NegativeControl`（本檔新增）與既有的 `DistributedLookupCacheServiceTests.RefreshAsync_repopulates_cache_with_fresh_data`——兩者驗證「沒有鎖競爭時 RefreshAsync 正常完成並快取」，修復前後皆綠、從未變紅（修法只改變 `if (!acquired)` 分支內部行為，鎖立刻拿到時完全不會進入該分支）。
- `test/WalkingTec.Mvvm.Core.Test/Cache/LookupCacheRefreshAsyncRegistryCheckTests944.cs`——沿用 `LookupCacheTests.cs` 裡既有的 `OrderRecord`（未掛 `[CacheLookup]`，未註冊）與 `CityCode`（已註冊）。**RED-before-fix（實測，逐字）**：`Assert.IsFalse failed. Bug #944: RefreshAsync<T> must not write an unregistered (non-[CacheLookup]) type to the cache — such an entry would never expire (SetCache only sets a TTL for registered types) and has no working invalidation path.` 刪掉修法裡的 `if (!_registry.ContainsKey(typeof(T))) { return; }` guard，這條測試就會變紅。**負控組**：`RefreshAsync_RegisteredType_StillCachesNormally_NegativeControl`（本檔新增，自成一體）與既有的 `StampedeAndRefreshTests.RefreshAsync_fills_cache_so_next_call_is_hit`——驗證已註冊型別透過 `RefreshAsync` 仍正常快取，修復前後皆綠。

**Mutation gate**：兩個新 entry，`test/mutants/entries/lookupcache943-distributed-refreshasync-acquired-guard-neutralize.json`（patch 把 `if (!acquired)` 改成 `if (false)`，一行、compile-preserving）與 `test/mutants/entries/lookupcache944-refreshasync-registry-guard-neutralize.json`（patch 把 `if (!_registry.ContainsKey(typeof(T)))` 改成 `if (false)`）。兩者皆 **`VERDICT: KILLED` / `GATE: PASS`**——由實作 agent 各自跑兩次確認決定性後才寫入 JSON（`red_expected_assertion_patterns` 從 `run_mutant.py --trx-dir` 產生的 TRX 檔用 `xml.etree.ElementTree` 直接讀出，不是從終端機輸出手抄），本次 review 再由審查者身分**額外獨立重跑兩次、每個 mutant 各兩次共四次**，全部 `KILLED`/`PASS`，逐字比對訊息一致，確認 943 這個涉及真實計時的 mutant不是碰巧綠燈的假決定性。**`kind` 選擇**：兩者都選 `"security"`——`run_mutant.py` 的 `VALID_KINDS` 只接受 `{security, selftest}`，`selftest` 專屬 runner 自身邏輯測試，這兩個都不是；雖然兩者本質是併發正確性／無界快取成長的完整性缺陷，不是傳統的未授權存取或 injection，但在現有 schema 下 `security` 是唯一能讓 mutant 被 CI 的 `mutants` job 實際執行、非 KILLED 會擋 gate 的功能性選項——跟 `etl970-cancellation-classification-guard-neutralize.json` 記錄的同一種強制分類理由一致。**entry 數量**：`test/mutants/entries/*.json` 總數 67 → 69；`security`-kind（直接對 base commit 每個 entry 檔的 `kind` 欄位解析計數，未依賴 script 本身）61 → 63，讓 #968 追蹤的 timeout-budget 落差再拉大 2（本次未處理，#968 scope 之外）。

**驗證**：`find . -name 'demo.db*' -path '*bin*' -delete && dotnet build WalkingTec.Mvvm.sln`——1 個已知、跟本次修改無關的錯誤：`NETSDK1082`（`BlazorDemo.Client` 缺 `browser-wasm` runtime pack），**直接對 base commit（未修改的 `origin/dotnet10`）單獨重建同一個專案確認過同一個錯誤存在**，不是修法造成的新問題。`dotnet test test/WalkingTec.Mvvm.Core.Test/`：修復前 **4997 passed**（在乾淨 worktree、修改任何檔案之前跑過確認），修復後 **5001 passed, 0 failed**（4997 + 本次新增的 4 個測試方法）。

**未能驗證的部分（誠實列出，不是隱藏）**：本次工作階段的 HARD CONSTRAINT 禁止呼叫任何 Gitea/GitHub API、禁止開 PR，因此這兩個修復尚未在真正的 Gitea Actions CI 上跑過——本機驗證只到 `dotnet build`/`dotnet test`/`run_mutant.py` 這三層，跟 CI 實際排程、job 併發、runner 環境的行為一致與否，要等分支真正開 PR 之後才是第一次生產驗證。`DistributedLookupCacheService.RefreshAsync` 的 registry-check 缺口本身除了「TTL 有界」這一點之外沒有進一步稽核（例如它的失效語意在缺 registry 檢查時是否還有其他非預期副作用），因為明確不在本次修復範圍內。

---

## Dashboard/ETL REST widget 的 header 值透過鏈結例外洩漏進應用程式日誌（#961，P1，2026-08-01）

**先重新推導、不採信 issue 文字**：`RestWidgetDataSource.FetchJsonAsync`（送出 REST widget 請求前把 `options.Headers` 逐一 `req.Headers.Add(h.Key, h.Value)`）確實把 `catch (Exception ex) when (ex is FormatException || ex is InvalidOperationException)` 抓到的原始例外，原封不動當成 `InnerException` 鏈到自己丟出的新 `InvalidOperationException` 上；`_DashboardController.GetWidgetData`/`PostWidgetData` 與 `_DashboardDesignerController.Preview` 三處都 `catch (InvalidOperationException ex)` 後直接 `_logger.LogWarning(ex, ...)`，把整個例外物件（含 `InnerException`）交給 logger。

**用自己的探針、對 .NET 10 實測，而非採信 issue 描述的行為**（`dotnet run -c Release`，見下方指令）：

```
THROW Authorization :: FormatException: The format of value 'Bearer my-real-secret-token
X-Injected: 1' is invalid.
THROW X-Api-Key     :: FormatException: New-line or NUL characters are not allowed in header values.
```

`Authorization`（parser-backed header，用 `AuthenticationHeaderValue` 解析）的 `FormatException.Message` 把**完整原始值**（含 secret）嵌進訊息；未知 header（如 `X-Api-Key`）走的是通用值驗證，訊息是固定文字、不含值。`_logger.LogWarning(ex, ...)` 收到的 `ex` 就是那個鏈了 `InnerException` 的 `InvalidOperationException`——絕大多數 logging sink（含 .NET 內建 console/file provider 的預設格式化器）在收到帶 exception 的 log 呼叫時，會另外印出 `exception.ToString()`，而該方法會遞迴印出整條 `InnerException` 鏈，於是原始 `FormatException.Message` 裡的 secret 就進了應用程式日誌。**觸發不需要攻擊**——任何會讓 .NET 驗證失敗的值都會觸發，例如貼上時夾帶的換行、操作員手滑打錯一個字元。

**與最初懷疑的觸發條件不同，親自驗證後更正（不是照抄 issue 描述）**：單純非 ASCII 文字（中文字、全形空白、Latin-1 補充字元）、單獨的控制字元 `0x01`、`DEL`（`0x7F`）、TAB，在 .NET 10 上**都不會**丟出 `FormatException`——擴充探針測試逐一證實：這些值全部 `NO-THROW`。`HttpRequestHeaders.Add` 的值驗證實際上只拒絕內嵌 CR、LF、NUL 三種字元（與未知 header 那則固定訊息文字「New-line or NUL characters are not allowed in header values.」完全對應）。因此迴歸測試的「非 ASCII」情境改用「非 ASCII 文字 + 內嵌 NUL」的組合值（`"Bearer 機密憑證A1B2C3\0trailer"`）——NUL 才是真正觸發 `FormatException` 的原因，非 ASCII 文字只是確認洩漏內容裡真的帶有非 ASCII 片段，兩者缺一都無法同時滿足「RED-before-fix 必須真的紅」與「值仍含非 ASCII 內容」兩個條件。

**修法**：header-adding 的 `catch` 區塊不再把 `ex` 當作 `InnerException` 鏈上去。保留 header **名稱**（`h.Key`，本來就在外層訊息裡——操作員看不到是哪個 header 被拒絕就無法修正設定）與例外**型別**（`ex.GetType().Name`，純診斷用途），完全捨棄值本身與原始例外物件：

```csharp
throw new InvalidOperationException(
    $"REST widget: header '{h.Key}' was rejected by the HTTP stack " +
    $"(possible invalid characters or CRLF in name/value; underlying error: {ex.GetType().Name}).");
```

`src/WalkingTec.Mvvm.Etl/Pipeline/Sources/RestEtlSource.cs`（`FetchPageAsync`）逐字同形狀，同步套用相同修法——它丟出的 `InvalidOperationException` 被 `EtlPipelineExecutor` 的多處 `catch (Exception ex)` 接住後整個交給 `_logger?.LogError(ex, ...)`，是同一種洩漏、只是多隔了一層。

**全樹掃描同形狀缺陷，指令與結果原文列出，不是複述 issue 已知的兩個檔案**：

```
grep -rn "Headers\.Add(" src --include="*.cs"
grep -rln "FormatException" src --include="*.cs"
```

第一條指令找到的所有 `req.Headers.Add(name, value)`／`client.DefaultRequestHeaders.Add(name, value)` 呼叫點中，**只有兩處**符合「迴圈跑過呼叫端可控的 header 字典、外面包一層專門 catch `FormatException`/`InvalidOperationException` 再 rethrow」這個確切形狀——就是上面已修的 `RestWidgetDataSource.cs`/`RestEtlSource.cs` 兩處。

**掃描另外發現、根因相同但程式碼形狀不同、本次刻意不修的兩處**：`src/WalkingTec.Mvvm.Core/WTMContext.CallApi.cs`（`CallAPI<T>`）與 `src/WalkingTec.Mvvm.Core/Services/WtmApiClient.cs`（`CallAPI<T>`）都在迴圈裡呼叫 `client.DefaultRequestHeaders.Add(item.Key, item.Value)`，`item` 來自呼叫端傳入、可能帶 Authorization 的 `headers` 字典——但這兩處**沒有**針對 header 的專屬 try/catch，`FormatException` 會直接穿透到整個方法外層那個涵蓋「發 HTTP 請求＋讀回應＋反序列化 JSON」全流程的廣義 `catch (Exception ex)`，該 catch 本來就把 `ex` 整個交給 `LogError`/`WtmDiagnosticLogger?.LogError`——沒有任何鏈結需要拆，缺陷是「完全沒有窄化」而非「窄化後又鏈回去」。修這兩處需要在既有的廣義 catch 裡面**新增**一段 header 專屬的窄 catch（而非只刪掉建構子的第三個參數），影響面與風險輪廓跟本次修法不同，且會讓一個 P1 修復的 diff 範圍偏離原始回報的兩個確切檔案。刻意不在本次一併修，留給後續獨立處理——不是漏掉沒發現。

**測試**：`test/WalkingTec.Mvvm.Core.Test/Dashboard/RestWidgetHeaderValueLogLeakTests961.cs` 透過真正的 `RestWidgetDataSource`（mock HTTP handler 證實從未被呼叫——header 拒絕發生在任何位元組送出之前）驅動 `_DashboardController.GetWidgetData`，用一個同時記錄格式化訊息「與」exception 物件本身的 `CapturingLogger`（模擬真實 sink 會另外印 `exception.ToString()`，不是只檢查訊息樣板——訊息樣板本身從未內嵌 `{Exception}`，只查樣板會完全漏掉這個洩漏）。兩條 RED-before-fix 洩漏測試（內嵌換行；非 ASCII + NUL 組合），逐字捕捉的紅燈訊息：

```
Did not expect string "[Dashboard] Widget data fetch failed. ...
System.InvalidOperationException: REST widget: header 'Authorization' was rejected ...
 ---> System.FormatException: The format of value 'Bearer s3cr3t-A1B2C3-do-not-log-me
X-Injected: evil' is invalid. ..." to contain "s3cr3t-A1B2C3-do-not-log-me" because the secret must never reach the log, ...
```

外加一條正控組（`GetWidgetData_still_logs_the_rejected_header_name`）證明修復後 header 名稱仍留在日誌裡——沒有這條，一個「整個例外都吞掉、什麼都不記」的過度修法也會讓前兩條變綠。`test/WalkingTec.Mvvm.Etl.Test/Pipeline/RestEtlSourceHeaderValueLogLeakTests961.cs` 直接對 `RestEtlSource` 丟出的例外斷言同一件事（`ex.ToString()` 不含 secret、`ex.Message` 含 header 名稱），因為 `RestEtlSource` 本身沒有自己的 logger、真正記錄的是它的呼叫方 `EtlPipelineExecutor`。

**Mutation gate**：`test/mutants/entries/961-restwidget-header-exception-chain-reintroduce.json`（patch 把 `throw new InvalidOperationException(...)` 補回 `, ex`，一行、compile-preserving）。**`red_expected_assertion_patterns` 取自一次真的跑過 `run_mutant.py`（`-c Release` build，對應 #959 的 Debug/Release 差異）且結果 PASS 的執行**，而非事先猜測——第一次嘗試用可跨行的 `.*` pattern 因為 Python 預設 `re` 旗標下 `.` 不吃換行，實際跑出 `VERDICT: UNEXPECTED_RED`（pattern 沒對到、不是抓錯原因），改成錨定在 FluentAssertions 固定尾綴文字的較窄 pattern 後重跑變 `VERDICT: KILLED`。**寫入 entry JSON 前確認決定性**：同一個修好的 pattern 連續執行三次，三次皆 `VERDICT: KILLED` / `GATE: PASS`，且每次執行後 `git status` 確認目標檔案都乾淨還原。**`kind` 選擇 `security`**：本案是貨真價實的機密外洩（非強制分類的邊緣案例），符合 `VALID_KINDS` 的字面定義。

**驗證（原始，對 base commit `origin/dotnet10` tip `7a1695f80`）**：`find . -name 'demo.db*' -path '*bin*' -delete && dotnet build WalkingTec.Mvvm.sln`——1 個已知、跟本次修改無關的錯誤：`NETSDK1082`（`BlazorDemo.Client` 缺 `browser-wasm` runtime pack），**直接對 base commit 單獨重建同一個專案確認過同一個錯誤存在**，不是修法造成的新問題。`dotnet test test/WalkingTec.Mvvm.Core.Test/`：base **5001 passed, 0 failed**（在乾淨 worktree、修改任何檔案之前跑過確認），修復後 **5004 passed, 0 failed**（5001 + 本次新增的 3 個測試方法）。`dotnet test test/WalkingTec.Mvvm.Etl.Test/`：**699 passed, 17 skipped（既有 Oracle 相關，非本次修改造成）, 0 failed**（含本次新增的 2 個測試方法）。

**Rebase 重新驗證（2026-08-01，#824/#978 合併後，`dotnet10` 推進到 `3887d7b11`）**：**先確認、非假設**目標檔案在 rebase 範圍內是否被動過——`git log --oneline 7a1695f80..3887d7b11 -- src/WalkingTec.Mvvm.Core/Dashboard/RestWidgetDataSource.cs` 空輸出，`git diff --stat 7a1695f80 3887d7b11 -- <同檔案>` 也空輸出，確認 #978 完全沒碰這個檔案——因此 mutant 的驗證**沒有**過期，不需要重新產生 entry JSON（先跑驗證、只有真的碰到目標檔案才重新產生，順序不能反過來）。`git rebase origin/dotnet10` 全程 **零手動衝突解決**——git 的 3-way merge 自動處理了 `CHANGELOG.md`／`docs/production-readiness.md` 兩處插入點（#978 也改了這兩個檔案，但插入的 context 位置跟本次修法沒有重疊到需要人工介入的程度）；`test/mutants/` 底下 #978 新增／修改的五個檔案跟本次新增的兩個檔案（`961-restwidget-header-exception-chain-reintroduce.json`/`.patch`）互不相同名，目錄層級也不衝突。Rebase 後逐項核對：`grep -c "^<<<<<<<\|^=======$\|^>>>>>>>"` 對 `CHANGELOG.md`/`docs/production-readiness.md`/兩個修改過的原始碼檔案全部 0；`docs/production-readiness.md` 逐一核對「每個 `## ` 標題前恰好一個 `---` 分隔線」（17 個標題對 17 個分隔線，一一對應，含本節自己前後），排除「naive keep-both-sides 少一條分隔線」與「先前 fix 補兩條」這兩種已知失敗模式。

**更正（原始報告誤判工具，實際是本文件自己的缺陷）**：上面那次 `grep` 檢查最初需要加 `-a` 才會有輸出——`docs/production-readiness.md` 不加 `-a` 直接印出空字串、連 exit code 都是「沒找到」而非「執行失敗」。原始報告把這歸因為「macOS 內建 BSD `grep` 對含中日文的 UTF-8 檔案的環境特有假訊號」，這個歸因是錯的，而且沒有實際查證就寫進報告。coordinator 複查後指出：CJK 文字本身不會讓 BSD `grep` 出這個問題（一個含中文與衝突標記的檔案不加 `-a` 照樣正常 grep）；會讓它出問題的是**這份文件自己在 rebase 前的某次編輯裡混進了一個真正的 NUL byte（`0x00`）**——本節上方「非 ASCII + 內嵌 NUL」測試向量的說明文字裡，`"Bearer 機密憑證A1B2C3` 後面原本寫的不是文字轉義，而是一個真的 `\x00` 位元組，直到 `trailer"` 才繼續。用 `python3 -c "print(open(f,'rb').read().count(b'\x00'))"` 逐檔核對：`docs/production-readiness.md` 當時是 **1**（缺陷），`CHANGELOG.md` 與兩個原始碼檔案是 **0**，兩個新增的 C# 測試檔（`RestWidgetHeaderValueLogLeakTests961.cs`/`RestEtlSourceHeaderValueLogLeakTests961.cs`）各是 **1**（兩者皆刻意、正確——測試方法本體真的需要一個位元組級的 NUL 才能觸發 `FormatException`，跟文件散文裡出現 NUL 是完全不同的情況）。**修法**：把文件裡那個真的 `0x00` 位元組換成純文字轉義 `\0`（兩個字元：反斜線、零），使文件用文字**描述**這個向量而不是**包含**它；換掉之後，同一條 `grep` 指令不加 `-a` 也能正常執行、正確印出 `0`——證實根因是這個 NUL byte，不是 BSD `grep` 對 CJK 的通用限制。教訓：工具在剛編輯過的檔案上出現非預期行為時，第一嫌疑對象是那個檔案，不是工具。

Rebase 後重新測量（`origin/dotnet10` 新 tip `3887d7b11`，非原本的 `7a1695f80`）：`dotnet build WalkingTec.Mvvm.sln`——同一個 `NETSDK1082` browser-wasm 錯誤，重新對新 base commit 單獨重建同一個專案再次確認存在，非本次改動造成。`dotnet test test/WalkingTec.Mvvm.Core.Test/`：新 base **5032 passed, 0 failed**（#824/#978 淨增 31 個測試，5001→5032，與本次修法無關）→ 本分支 **5035 passed, 0 failed**（5032 + 本次新增的 3 個測試方法，數量不變）。`dotnet test test/WalkingTec.Mvvm.Etl.Test/`：**699 passed, 17 skipped（既有 Oracle 相關）, 0 failed**（含本次新增的 2 個測試方法，跟 rebase 前一致——#978 沒有動到 Etl.Test）。Mutant `961-restwidget-header-exception-chain-reintroduce`：rebase 後**連續重跑三次**，三次皆 `VERDICT: KILLED` / `GATE: PASS`，每次執行後 `git status` 確認目標檔案乾淨還原——entry JSON 本身在 rebase 前後**完全沒有編輯**，因為驗證顯示不需要。

**未能驗證的部分（誠實列出，不是隱藏）**：本次工作階段的 HARD CONSTRAINT 禁止呼叫任何 Gitea/GitHub API、禁止開 PR，因此這兩個修復尚未在真正的 Gitea Actions CI 上跑過——本機驗證只到 `dotnet build`/`dotnet test`/`run_mutant.py` 這三層。`WTMContext.CallApi.cs`/`Services/WtmApiClient.cs` 兩處同根因缺陷已由使用者另立 **#979** 追蹤，本文件僅記錄「找到了、為何不修」，不宣稱「已修」或「全樹已無殘留同形狀缺陷」——後者需要的是「同根因、不同形狀」的窮舉，本次的 grep 指令只窮舉了「同形狀」那一個維度。

**更新（2026-08-01，#979）**：上面「刻意不修」的兩處已在 #979 修復——不是留待「後續獨立處理」的空話，這次真的關閉了。細節、探針證據（含對 #979 issue 文字本身一處描述錯誤的實測更正）、修法、掃描出的第三個同根因站點（同樣刻意不修）、測試與 mutation gate，見下一節「`WTMContext.CallApi`／`WtmApiClient` 的 header 值透過廣義例外洩漏進應用程式日誌（#979）」。

---

## `WTMContext.CallApi`／`WtmApiClient` 的 header 值透過廣義例外洩漏進應用程式日誌（#979，2026-08-01）

**先重新推導、不採信 issue 文字**：`WTMContext.CallApi.cs`（`CallAPI<T>`）與 `Services/WtmApiClient.cs`（`CallAPI<T>`）都在迴圈裡對呼叫端傳入的 `headers` 字典逐一呼叫 `client.DefaultRequestHeaders.Add(item.Key, item.Value)`，**確實沒有任何 header 專屬的 try/catch**——這與 #961 修的 `RestWidgetDataSource`/`RestEtlSource`（已有專屬 catch，只是把原始例外鏈上去）是不同的形狀：#961 是「窄化後又鏈回去」，#979 是「完全沒有窄化」。`FormatException` 會直接穿透到整個方法外層那個涵蓋「發 HTTP 請求＋讀回應＋反序列化 JSON」全流程的既有廣義 `catch (Exception ex)`，該 catch 把 `ex` 整個交給 `WtmDiagnosticLogger?.LogError(...)`（`WTMContext.CallApi.cs`）或 `_logger?.LogError(...)`（`WtmApiClient.cs`）。

**用自己的探針、對 .NET 10 實測，而非採信 issue 文字給的表格**（`dotnet run -c Release`）：

```
CRLF-embedded (Authorization)       :: THROW FormatException: The format of value 'Bearer secret-token\nX-Injected: 1' is invalid.
CR-only-embedded (Authorization)    :: THROW FormatException: The format of value 'Bearer secret-tokenX-Injected: 1' is invalid.
LF-only-embedded (Authorization)    :: THROW FormatException: ...
NUL-embedded (Authorization)        :: THROW FormatException: The format of value 'Bearer secret-token trailer' is invalid.
Chinese-text (Authorization)        :: NO-THROW
Fullwidth-space (Authorization)     :: THROW FormatException: The format of value 'Bearer　secrettoken' is invalid.
Latin1-supplement (Authorization)   :: NO-THROW
Control-0x01 (Authorization)        :: THROW FormatException: ...
DEL-0x7F (Authorization)            :: THROW FormatException: ...
TAB (Authorization)                 :: NO-THROW
CRLF-embedded (X-Api-Key)           :: THROW FormatException: New-line or NUL characters are not allowed in header values.
NUL-embedded (X-Api-Key)            :: THROW FormatException: New-line or NUL characters are not allowed in header values.
Chinese-text (X-Api-Key)            :: NO-THROW
Fullwidth-space (X-Api-Key)         :: NO-THROW
Control-0x01 (X-Api-Key)            :: NO-THROW
DEL-0x7F (X-Api-Key)                :: NO-THROW
TAB (X-Api-Key)                     :: NO-THROW
```

**與交辦時給的表格不完全相符，親自驗證後如實回報（不是照抄）**：CR/LF、NUL 會 THROW，純非 ASCII 文字（中文字、Latin-1 補充字元）NO-THROW——這兩點與給定表格一致。但表格宣稱「bare 控制字元 `0x01`、`DEL`、全形空白」在 `Authorization` 上 NO-THROW，**實測是 THROW**。追查原因：`Authorization` 是 parser-backed header，`HttpRequestHeaders.Add` 對它會用 `AuthenticationHeaderValue` 的 scheme/token 語法解析，比一般 header 的「只擋 CR/LF/NUL」驗證嚴格得多——同一批值（全形空白、`0x01`、`DEL`）改用一個未知/一般 header（`X-Api-Key`）测试則全部 NO-THROW，與表格相符。也就是說**表格對「一般 header」成立，但對 `Authorization` 本身不成立**——這正是 issue 文字裡「Authorization 是 parser-backed header」這句話沒有講完整的地方。這個差異不影響修法本身（`catch (FormatException)` 不分是哪一種驗證失敗都會抓到），但因為交辦訊息明確要求「不一致就如實回報，不要為了對表格而讓程式碼將就」，故記錄於此。回歸測試沿用「內嵌換行」這個 issue 本身點名的「realistic trigger」，不受這個差異影響。

**修法**：在既有的廣義 `catch (Exception ex)` **裡面**新增一段 header 專屬的窄 `catch (FormatException)`，包住 `client.DefaultRequestHeaders.Add(item.Key, item.Value)` 這一行——不是把窄 catch 疊在廣義 catch 前面（那樣會連 `UriFormatException` 之類其他 `FormatException` 子類別的既有行為都一起改到），而是只包住這一個呼叫點：

```csharp
try
{
    client.DefaultRequestHeaders.Add(item.Key, item.Value);
}
catch (FormatException ex)
{
    throw new InvalidOperationException(
        $"CallAPI: header '{item.Key}' was rejected by the HTTP stack " +
        $"(possible invalid characters or CRLF in name/value; underlying error: {ex.GetType().Name}).");
}
```

保留 header **名稱**（`item.Key`）與例外**型別**（`ex.GetType().Name`），捨棄值本身與原始例外物件——與 #961 相同精神，但套用位置不同：#961 是「刪掉既有窄 catch 裡鏈結原始例外的 `, ex`」，#979 是「在既有廣義 catch 裡面新增一段從未存在過的窄 catch」。合成出的 `InvalidOperationException` 繼續往外傳，落進**完全沒有改動過**的既有廣義 `catch (Exception ex)`——外層 catch 本身的程式碼一個字元都沒動，這是「新窄 catch 不改變廣義 catch 對其他例外的既有行為」這個驗收條件成立的結構性理由，不是靠測試才碰運氣證明。`Services/WtmApiClient.cs` 逐字同形狀，同步套用相同修法（訊息前綴改成 `WtmApiClient.CallAPI:` 以便日誌區分兩個呼叫路徑）。

**全樹掃描同形狀缺陷，指令與結果原文列出，涵蓋整個 repo 而非只有 `src/`**：

```
grep -rn "Headers\.Add(" --include="*.cs" . | grep -v '/bin/\|/obj/'
```

結果：`src/`、`demo/`、`test/` 三處都有命中。`demo/WalkingTec.Mvvm.BlazorDemo/.../ServiceExtension.cs` 兩處與 `src/WalkingTec.Mvvm.Mvc/Helper/FrameworkServiceExtension.cs`／`src/WalkingTec.Mvvm.Core/Helper/IServiceExtension.cs` 各兩處，都是框架啟動時註冊的固定字面值（`Cache-Control: no-cache`、寫死的 User-Agent 字串），不是呼叫端可控輸入，跟本次缺陷的「呼叫端提供的字典/字串」前提不成立，不在風險範圍。`test/` 底下的命中全部是測試程式碼本身在建構請求（`X-Forwarded-For`、`Idempotency-Key` 等），不是生產路徑。生產路徑上，`req.Headers.Add(name, value)`／`client.DefaultRequestHeaders.Add(name, value)` 搭配「呼叫端可控值、無 header 專屬 catch」這個確切形狀，**只有本次修的兩處**（`WTMContext.CallApi.cs:47`、`WtmApiClient.cs:63`）。

**掃描另外發現、根因相同、本次刻意不修的第三個/第四個站點**：同一輪掃描發現 `WTMContext.CallApi.cs:77` 與 `WtmApiClient.cs:93` 各有一行 `client.DefaultRequestHeaders.Add("Authorization", "Bearer " + LoginUserInfo?.RemoteToken)` / `... + authToken)`——不是迴圈跑 `headers` 字典，而是**單一固定 header 名稱**、值來自 `LoginUserInfo.RemoteToken`（遠端 token，可能是格式不正確的憑證）或 `authToken` 參數。這兩處同樣落在**同一個**、本次已修過的方法內、**同一個**既有廣義 catch 之內，理論上如果 `RemoteToken`/`authToken` 帶有 CR/LF/NUL，一樣會讓 `FormatException.Message`（含完整 Bearer token）穿透到 `LogError`。但這兩處**不是 #979 issue 文字點名的形狀**（issue 明確講的是 `item.Key`/`item.Value` 這個迴圈），header 名稱固定為 `"Authorization"` 也代表「記錄 header 名稱」這個診斷手段在這裡沒有意義（永遠都是 Authorization，不需要靠名稱去定位是哪個 header）。刻意不在本次一併修——跟 #961 對 `WTMContext.CallApi.cs`/`WtmApiClient.cs` 的處理方式一致：找到了、記錄下來、不擴大這次 P1 修復的 diff 範圍去涵蓋 issue 沒有點名的程式碼。**這是本文件第二次記錄這個模式的殘留站點；建議下一輪安全掃描直接把「固定 header 名稱、值來自可能不受信任的來源字串、無 header 專屬 catch」列為自己的檢查項，不要等下一個 issue 再重新用 grep 發現一次。**

**測試**：`test/WalkingTec.Mvvm.Core.Test/Security/WtmContextCallApiHeaderLogLeakTests979.cs`（對應 `WTMContext.CallApi.cs`）與 `test/WalkingTec.Mvvm.Core.Test/Services/WtmApiClientHeaderLogLeakTests979.cs`（對應 `WtmApiClient.cs`），各三條測試，結構相同：

1. **洩漏測試**：`Authorization` header 值帶內嵌換行（`"Bearer {secret}\nX-Injected: evil"`），驅動真正的 `CallAPI<T>`（mock `IHttpClientFactory`/`HttpMessageHandler`，並用 callCount 斷言「header 拒絕發生在任何 HTTP 請求送出之前」），用一個同時記錄格式化訊息「與」exception 物件本身的 `CapturingLogger`（模擬真實 sink 另外印 `exception.ToString()` 的行為），斷言 `logger.FullRenderedOutput` 不含 secret 片段。RED-before-fix（對未修的程式碼跑，逐字擷取）：

   ```
   WTMContext.CallApi.cs 版本：
   Did not expect logger.FullRenderedOutput "CallAPI failed to 'http://test/api'
   System.FormatException: The format of value 'Bearer s3cr3t-A1B2C3-do-not-log-me
   X-Injected: evil' is invalid.
      at System.Net.Http.Headers.HttpHeaderParser.ParseValue(...)
      at System.Net.Http.Headers.HttpHeaders.ParseAndAddValue(...)
      at System.Net.Http.Headers.HttpHeaders.Add(...)
      at WalkingTec.Mvvm.Core.WTMContext.CallAPI[T](...) in .../WTMContext.CallApi.cs:line 45"
   to contain "s3cr3t-A1B2C3-do-not-log-me" because the secret must never reach the log, ...

   WtmApiClient.cs 版本：
   Did not expect logger.FullRenderedOutput "API call failed to http://test/api
   System.FormatException: The format of value 'Bearer s3cr3t-A1B2C3-do-not-log-me
   X-Injected: evil' is invalid.
      ...
      at WalkingTec.Mvvm.Core.Services.WtmApiClient.CallAPI[T](...) in .../WtmApiClient.cs:line 61"
   to contain "s3cr3t-A1B2C3-do-not-log-me" because the secret must never reach the log, ...
   ```

   刪掉哪一行會讓它變紅：把新增的 `try { client.DefaultRequestHeaders.Add(item.Key, item.Value); } catch (FormatException ex) { throw new InvalidOperationException(...); }` 整段換回原本的 `client.DefaultRequestHeaders.Add(item.Key, item.Value);` 單行（或如 mutant 所證，只把 `throw new InvalidOperationException(...)` 換成 `throw;` 就夠）。

2. **正控組**：同樣的內嵌換行值，斷言 `logger.FullRenderedOutput` 仍含 `"Authorization"`。RED-before-fix（同一次未修程式碼上的執行）：

   ```
   Expected logger.FullRenderedOutput "...FormatException: The format of value 'Bearer irrelevant-value
   X-Injected: evil' is invalid. ..." to contain "Authorization" because an operator must still be able to tell WHICH header was rejected.
   ```

   這條紅燈本身是個發現：.NET 對 `Authorization` 的 `FormatException.Message` **從來不提header 名稱**（訊息固定是 "The format of value '...' is invalid."），所以未修的程式碼不只洩漏密鑰，連「是哪個 header 被拒絕」這個診斷資訊都給不出來——正控組在 RED 階段就同時證明了這兩件事。刪掉哪一行會讓它變紅：把合成訊息 `$"CallAPI: header '{item.Key}' was rejected..."` 裡的 `{item.Key}` 拿掉（或整段換成不含 header 名稱的通用文字）——這是跟洩漏測試不同的一行/token，證明正控組驗證的是獨立於洩漏本身的另一個性質，不是同一斷言的重複包裝。

3. **廣義 catch 迴歸測試**：不帶 `headers` 參數，改用會丟 `HttpRequestException("connection-refused-sentinel-42")` 的 mock handler（#979 的修法完全不會碰到這條路徑），斷言 (a) `result.ErrorMsg` 仍是修法前就存在的固定通用文字，(b) logger 仍完整收到含 sentinel 字串的例外。這條測試在未修的程式碼上**本來就是綠燈**（不是 RED-before-fix 的一員），刪掉哪一行會讓它變紅：既有的 `WtmDiagnosticLogger?.LogError(ex, ...)`（`WTMContext.CallApi.cs:126`）或 `_logger?.LogError(ex, ...)`（`WtmApiClient.cs:145`）——這兩行跟 #979 的修法完全無關，說明這條測試的角色是「證明新窄 catch 沒有動到廣義 catch 的既有行為」，不是「驗證新程式碼有沒有寫對」，兩者角色不同必須分開報。

**Mutation gate**：`test/mutants/entries/979-callapi-header-exception-leak-reintroduce.json`（patch 把 `WTMContext.CallApi.cs` 裡合成的 `throw new InvalidOperationException(...)` 換成裸的 `throw;`，重新讓原始 `FormatException` 不受影響地穿透到廣義 catch，一行、compile-preserving）。**`kind` 選 `security`**：這是貨真價實的機密外洩，不是邊緣案例，符合 `VALID_KINDS` 的字面定義。

過程中兩個值得記錄的迭代（都是靠實際跑 `run_mutant.py` 才發現，不是先驗猜到）：

- **第一次選的 green_test（positive control）是錯的**：一開始選 2 號測試（「header 名稱仍留在日誌裡」的正控組）當作這個 mutant 的 green_test，實際跑出 `VERDICT: POSITIVE_CONTROL_FAILED`——不是 bug，是真的：這個 mutant 的 `throw;` 會讓原始 `FormatException`（訊息裡從不含 header 名稱，見上方「正控組」小節）取代合成訊息，所以 2 號測試在這個 mutant 底下**本來就該紅**，跟 1 號洩漏測試紅的是同一行程式碼、不是獨立性質，不能當 positive control。改用 3 號「廣義 catch 迴歸測試」（完全不同的程式路徑，這個 mutant 的 patch 根本沒碰到）當 green_test 後，`VERDICT` 變成預期的 `KILLED`。
- **`red_expected_assertion_patterns` 取自一次真的跑過 `run_mutant.py`（`-c Release` build，對應 #959 的 Debug/Release 差異）的執行**，而非先猜測——先用一個佔位字串跑，正確得到 `VERDICT: UNEXPECTED_RED`（pattern 沒對到，不是抓錯原因），改成錨定在 FluentAssertions 固定尾綴文字（`to contain "s3cr3t-A1B2C3-do-not-log-me" because the secret must never reach the log`，跟多行的例外堆疊分開，避免 Python `re` 預設 `.` 不吃換行的老問題——#961 已經記過一次這個教訓，這次直接套用而非重踩）後重跑變 `VERDICT: KILLED`。

**寫入 entry JSON 前確認決定性**：修正 green_test 與 pattern 之後，同一份 entry 連續執行 **三次**，三次皆 `VERDICT: KILLED` / `GATE: PASS`，且每次執行後 `git status --porcelain -- src/WalkingTec.Mvvm.Core/WTMContext.CallApi.cs` 皆為空輸出，確認目標檔案乾淨還原。**`Services/WtmApiClient.cs` 的同形狀缺陷刻意不另開第二個 mutant entry**——`run_mutant.py` 的 scope 檢查限制一個 patch 只能動一個 `target_file`，而 `WtmApiClientHeaderLogLeakTests979.cs` 已經對該檔案的修法做了同樣三條直接測試（洩漏／正控組／廣義 catch 迴歸），第二個 mutant 只會增加 gate 執行時間、不會增加這條直接測試沒有涵蓋到的證據。

**驗證**：`find . -name 'demo.db*' -path '*bin*' -delete && dotnet build WalkingTec.Mvvm.sln`——1 個已知、跟本次修改無關的錯誤：`NETSDK1082`（`BlazorDemo.Client` 缺 `browser-wasm` runtime pack），**直接對本次 base commit（`origin/dotnet10` tip `46d5bc576`）單獨重建同一個專案確認過同一個錯誤存在**，不是修法造成的新問題。`dotnet test test/WalkingTec.Mvvm.Core.Test/`：base **5035 passed, 0 failed**（在乾淨 worktree、修改任何檔案之前跑過確認），修復後 **5041 passed, 0 failed**（5035 + 本次新增的 6 個測試方法，數量吻合）。

**未能驗證的部分（誠實列出，不是隱藏）**：本次工作階段的 HARD CONSTRAINT 禁止呼叫任何 Gitea/GitHub API、禁止開 PR，因此這個修復尚未在真正的 Gitea Actions CI 上跑過——本機驗證只到 `dotnet build`/`dotnet test`/`run_mutant.py` 這三層。`WTMContext.CallApi.cs:77`/`WtmApiClient.cs:93`（Authorization header 透過 `RemoteToken`/`authToken` 組成）兩處同根因缺陷本文件僅記錄「找到了、為何不修」，不宣稱「已修」；全樹掃描指令這次涵蓋了整個 repo（不只 `src/`），但只窮舉了「呼叫 `Headers.Add`」這一個 API 形狀，不證明沒有其他方式（例如 `TryAddWithoutValidation` 之後在別處被驗證/記錄、或非 `HttpRequestHeaders` 的其他 header 表示方式）可能存在結構不同但根因相同的洩漏路徑。

**更新（2026-08-03，#982）**：上面這兩處「找到了、刻意不修」的殘留站點已修——見下一節「`WTMContext.CallApi`／`WtmApiClient` 的 `Authorization` header 值透過廣義例外洩漏進應用程式日誌（#982）」。

---

## `WTMContext.CallApi`／`WtmApiClient` 的 `Authorization` header 值透過廣義例外洩漏進應用程式日誌（#982，2026-08-03）

**這是 #979 的 scope gap，不是新缺陷**：#979 在兩個檔案各自的 `headers` 字典迴圈外面新增了窄 `catch (FormatException)`，但那個窄 catch 只包住迴圈本身；緊接在迴圈**後面**、同一個方法裡的 `Authorization` 組裝行完全沒被包到：

```
WTMContext.CallApi.cs:77   client.DefaultRequestHeaders.Add("Authorization", "Bearer " + LoginUserInfo?.RemoteToken);
Services/WtmApiClient.cs:93  client.DefaultRequestHeaders.Add("Authorization", "Bearer " + authToken);
```

先重讀已合併的程式碼樹確認兩處確實存在、確實在 #979 修過的既有廣義 `catch (Exception ex)` 之內、確實沒有專屬 catch，而不是採信交辦文字——兩處都與描述相符。這兩個站點不是這次新發現的：**#979 自己的 commit 就已經找到並記錄了**（本文件上一節「掃描另外發現、根因相同、本次刻意不修的第三個/第四個站點」），理由是「不是 issue 文字點名的迴圈形狀」，並且**明白建議「下一輪安全掃描直接把這個形狀列為檢查項」**——這正是 #982 存在的原因。`WtmApiClient.cs:77` 的一段既有註解宣稱這個 add 行的修法「byte-for-byte the same fix as WTMContext.CallApi.cs's copy of this loop」——這句話本身沒錯（修法形狀確實逐字相同），但它精確地示範了 CLAUDE.md 記錄過的那個模式：**修法被複製到兩個檔案時，它的邊界（只包住迴圈、不包住迴圈後面那一行）也被原樣複製過去**。#979 自己的 commit 標題也寫「from CallApi and WtmApiClient」，但兩個檔案裡各自只修到一個 add 點——這正是本節標題刻意把「兩處都名字寫出來」的原因，不重複那種只講對一半的說法。

**修法**：套用與 #979 完全相同的形狀——在 `Authorization` add 這一行外面包一層窄 `try`/`catch (FormatException)`，合成一個只帶 header **名稱**（這裡固定是字面值 `"Authorization"`，不是攻擊者可控的字串，但保留它是為了跟 #979 的訊息格式一致，方便從日誌文字辨識是哪一類 add 失敗)與例外**型別**的 `InvalidOperationException`，捨棄值本身與原始例外物件。兩個檔案各自套用一次，訊息前綴沿用各自既有慣例（`"CallAPI: ..."` / `"WtmApiClient.CallAPI: ..."`）。合成出的例外繼續往外傳，落進**完全沒有改動過**的既有廣義 catch——跟 #979 一樣，這是「新窄 catch 不改變廣義 catch 既有行為」這個驗收條件成立的結構性理由。

**`authToken` 全 repo 呼叫端清查（`WtmApiClient.cs`）**：`authToken` 是 `IWtmApiClient.CallAPI<T>` 全部四個 overload 上的公開參數（`IWtmApiClient.cs` 宣告、`WtmApiClient.cs` 實作）。用 `grep -rn "authToken"` 與 `grep -rn "\.CallAPI("` 對 `src/`、`demo/`、`test/` 三處逐一窮舉：
- `WtmAuthService.AuthenticateViaRemoteHostAsync`（`src/WalkingTec.Mvvm.Core/Services/WtmAuthService.cs:39`/`:62`）與 `RefreshTokenAsync`（`:111`）呼叫 `apiClient.CallAPI(...)`，但把 token 放進 `headers` 字典（`{ "Authorization", "Bearer " + remoteToken }`）傳，從未使用 `authToken:` 具名參數——這條路徑已經被 #979 的 `headers` 迴圈修法保護。
- `WtmUserCacheService.RemoveUserCacheByRoleAsync`/`RemoveUserCacheByGroupAsync`（`src/WalkingTec.Mvvm.Core/Services/WtmUserCacheService.cs:44`/`:78`）呼叫 `apiClient.CallAPI<List<string>>("mainhost", url)`，不帶任何 header 或 token 參數。
- `WTMContext.cs:532`/`:561` 從 `ServiceProvider` 解析出 `IWtmApiClient` 只是為了轉呼叫上面兩個 `WtmUserCacheService` 方法，同樣不帶 `authToken`。
- repo 內剩下唯一出現 `authToken:` 具名參數的地方是 `test/WalkingTec.Mvvm.Core.Test/Services/WtmApiClientTests.cs:153`，傳的是字面常數 `"my-token"`，不是可控輸入。

**結論：repo 內沒有任何呼叫端會把可能含 CRLF/NUL 的字串傳進 `authToken`。** 但 `authToken` 是 `IWtmApiClient`（一個 DI 註冊介面）上刻意公開、有文件說明（`/// <param name="authToken">Bearer token to attach (if not already present in headers). Pass null to skip.</param>`）的參數——WTM 是框架，這個介面存在的目的就是給下游宿主應用呼叫，不是只給 repo 自己用。**判定為 MEDIUM**：沒有框架內部觸發路徑，但這是刻意公開、文件化、預期被呼叫端傳入外部字串的參數，跟 #979 修的 `headers` 字典（同樣是「呼叫端可控字串，框架端無驗證」的形狀）風險性質相同，只是目前沒有 repo 內已知呼叫端示範這條路徑。`WTMContext.CallApi.cs:77` 的 `LoginUserInfo?.RemoteToken` 維持 #979 原本的判定：**LOW**——`?_remotetoken=` 查詢參數只會經過已受保護的 `headers` 迴圈到達（`WTMContext.cs:273-275` 把它放進 `headers` 字典傳給 `CallAPI`），CRLF 值會在那裡被攔下、從未指派到 `LoginUserInfo.RemoteToken`；JWT 的 `RToken` claim 需要 mainhost 本身簽發帶 CRLF 的 claim；`IssueTokenAsync` 回傳的是真正的 base64url JWT。要觸發這條路徑需要 mainhost 本身已被攻陷。

**測試**：`test/WalkingTec.Mvvm.Core.Test/Security/WtmContextCallApiAuthorizationHeaderLogLeakTests982.cs`（對應 `WTMContext.CallApi.cs`，透過 `wtm.LoginUserInfo = new LoginUserInfo { RemoteToken = ... }` 直接餵值，繞過已受保護的 `headers` 迴圈）與 `test/WalkingTec.Mvvm.Core.Test/Services/WtmApiClientAuthorizationHeaderLogLeakTests982.cs`（對應 `WtmApiClient.cs`，`authToken` 是方法參數，直接傳）各兩條測試，結構沿用 #979：

1. **洩漏測試**：token 值帶內嵌換行，斷言 `logger.FullRenderedOutput` 不含 secret 片段、且 mock handler 從未被呼叫（header 拒絕發生在任何 HTTP 請求送出之前）。RED-before-fix（對未修的程式碼跑，逐字擷取）：

   ```
   WtmApiClient.cs 版本：
   Did not expect string "API call failed to http://test/api
   System.FormatException: The format of value 'Bearer s3cr3t-authtoken-A1B2C3-do-not-log-me
   X-Injected: evil' is invalid.
      at System.Net.Http.Headers.HttpHeaderParser.ParseValue(String value, Object storeValue, Int32& index)
      at System.Net.Http.Headers.HttpHeaders.ParseAndAddValue(HeaderDescriptor descriptor, HeaderStoreItemInfo info, String value)
      at System.Net.Http.Headers.HttpHeaders.Add(HeaderDescriptor descriptor, String value)
      at WalkingTec.Mvvm.Core.Services.WtmApiClient.CallAPI[T](...) in .../WtmApiClient.cs:line 93" to contain "s3cr3t-authtoken-A1B2C3-do-not-log-me" because the secret must never reach the log, whether via the message template or the logged exception's own text.

   WTMContext.CallApi.cs 版本：
   Did not expect string "CallAPI failed to 'http://test/api'
   System.FormatException: The format of value 'Bearer s3cr3t-remotetoken-A1B2C3-do-not-log-me
   X-Injected: evil' is invalid.
      ...
      at WalkingTec.Mvvm.Core.WTMContext.CallAPI[T](...)" to contain "s3cr3t-remotetoken-A1B2C3-do-not-log-me" because the secret must never reach the log, ...
   ```

2. **正控組**：同樣的內嵌換行值，斷言 `logger.FullRenderedOutput` 仍含 `"Authorization"`。RED-before-fix 同樣重現（例外訊息固定是 "The format of value '...' is invalid."，從不提 header 名稱，跟 #979 對 `Authorization` 的既有發現一致）。

刪掉哪一行會讓兩個檔案的洩漏測試變紅：把新增的 `try { ...Add("Authorization", ...); } catch (FormatException ex) { throw new InvalidOperationException(...); }` 換回原本的 `...Add("Authorization", ...);` 單行（或如下面的 mutant 所證，只把 `throw new InvalidOperationException(...)` 換成 `throw;` 就夠）。GREEN-after-fix：4 條測試全過。

**Mutation gate**：`test/mutants/entries/982-callapi-authorization-header-exception-leak-reintroduce.json`，target file 為 `WTMContext.CallApi.cs`，patch 把本次新增的 `throw new InvalidOperationException(...)` 換成裸的 `throw;`——跟 `979-callapi-header-exception-leak-reintroduce` 同一個 mutant 形狀，只是換了一個 catch 區塊。`kind` 選 `security`。

**正控組解耦論證（`.claude/rules/testing.md`「A mutant's positive control must not touch the mutated decision path」要求寫進 entry）**：這次直接沿用 #979 既有的 `WtmContextCallApiHeaderLogLeakTests979.CallAPI_non_header_exception_is_still_handled_by_the_broad_catch_unchanged` 當 green_test，而不是本節新增的「header 名稱仍留在日誌裡」正控組——後者跟 #979 犯過的錯誤同形狀（斷言的輸出來自被突變的同一個 catch 區塊，`Authorization` 的 `FormatException.Message` 從不含 header 名稱，mutant 套用後這條測試本身就會變紅，不能當控制組）。選用的 green_test 呼叫路徑追蹤：該測試用 `MockWtmContext.CreateWtmContext()` 建構 `wtm`，**從未設定 `wtm.LoginUserInfo`**——`LoginUserInfo` getter 在沒有已驗證 `HttpContext.User`、沒有 `_remotetoken` 查詢參數的情況下維持 `null`，所以 `LoginUserInfo?.RemoteToken` 也是 `null`；外層 guard `string.IsNullOrEmpty(LoginUserInfo?.RemoteToken) == false` 因此恆為 `false`，整個 `if` 區塊（含本次新增、被 mutant 改動的 try/catch）**從未被執行到**。這是解耦論證裡最強的一種（呼叫圖從未到達被突變的那一行），不需要靠短路或不變量論證。

**執行結果**：`python3 test/mutants/run_mutant.py --mutant 982-callapi-authorization-header-exception-leak-reintroduce`——`VERDICT: KILLED` / `GATE: PASS`（實際輸出見 CHANGELOG.md 與本次 session 的執行記錄；每次執行後 `git status --porcelain -- src/WalkingTec.Mvvm.Core/WTMContext.CallApi.cs` 皆為空輸出，確認目標檔案乾淨還原）。**`Services/WtmApiClient.cs` 的同形狀缺陷刻意不另開第二個 mutant entry**——理由與 #979 相同：`run_mutant.py` 的 scope 檢查限制一個 patch 只能動一個 `target_file`，而 `WtmApiClientAuthorizationHeaderLogLeakTests982.cs` 已經對該檔案的修法做了同樣兩條直接測試（洩漏／正控組），第二個 mutant 只會增加 gate 執行時間、不會增加這條直接測試沒有涵蓋到的證據。

**驗證**：`find . -name 'demo.db*' -path '*bin*' -delete`。`dotnet test test/WalkingTec.Mvvm.Core.Test/`：base **5062 passed, 0 failed**（在這個分支自己的修改被 `git stash` 移除後、乾淨測得）→ 修復後 **5066 passed, 0 failed**（5062 + 本次新增的 4 個測試方法，數量吻合）。`dotnet build WalkingTec.Mvvm.sln`：**0 Error(s)**——本次 session 自己重建整個 solution 沒有重現 #961/#979 base commit 上報告過的 `NETSDK1082` browser-wasm 錯誤，跟本次修法無關，不深究。

**未能驗證的部分（誠實列出，不是隱藏）**：本次工作階段的 HARD CONSTRAINT 禁止呼叫任何 Gitea/GitHub API、禁止開 PR，因此這個修復尚未在真正的 Gitea Actions CI 上跑過——本機驗證只到 `dotnet build`/`dotnet test`/`run_mutant.py` 這三層。#979 文件裡提過的「全樹掃描只窮舉了呼叫 `Headers.Add` 這一個 API 形狀」這個限制原樣延續到本次；`authToken`/`RemoteToken` 的呼叫端清查只窮舉了「repo 內目前存在的呼叫端」，不代表「這個公開參數永遠不會被下游宿主應用以不受信任的字串呼叫」——這正是判它 MEDIUM 而非把它跟 `RemoteToken` 一起判 LOW 的理由。

---

## Mutation-gate 正控組（positive control）與被突變行的耦合（#986）

**問題本質**：`run_mutant.py` 的 `green_test`（正控組）存在的理由是證明「除了被突變的行為以外，其他一切照常運作」。如果 `green_test` 自己的通過與否，其實也取決於被突變的那一行，它就不是控制組——它是第二個受影響的東西。這件事在本 repo 發生過兩次，方向相反：

- **#979**（`979-callapi-header-exception-leak-reintroduce`）：一開始選的 `green_test` 斷言「header 名稱仍留在日誌裡」，剛好也是被突變的同一個 `catch` 區塊產生的輸出。`run_mutant.py` 直接回報 `VERDICT: POSITIVE_CONTROL_FAILED`——**大聲**，在這個 entry 被提交前就攔下來了。
- **#986**（`dcext824-derived-principal-neutralize`）：`green_test` 呼叫 `IsFileAttachmentForeignKeyProperty`，用的 fixture 唯一的外鍵原則（principal）恰好是一個 TPH 衍生的 `FileAttachment` 子類別——正是這個 mutant 要讓「衍生類別辨識」失效的那個形狀。production code（`DCExtension.Schema.cs:211` 附近）在逐一走訪 FK 的迴圈裡，**先**呼叫被突變的 `IsFileAttachmentPrincipal`，**之後**才比對屬性名稱；mutant 套用後，這個 gate 提早把該 FK 判定為「不是 FileAttachment 外鍵」而 `continue`，屬性名稱比對根本沒被執行到。測試查詢的屬性名稱（`ID`）本來就不是外鍵屬性，所以不管走哪條路徑最終都回傳 `false`——`Assert.IsFalse` 照樣通過，但是透過一條跟未突變程式碼不同的分支。**`run_mutant.py` 全程回報 `VERDICT: KILLED` / `GATE: PASS`——乾淨，因為 red test 依然正確地失敗了。** gate 沒有辦法看出 green test 自己的證明是空的；沒有任何警訊。

第二種比第一種更糟：耦合的正控組如果剛好還是通過，完全不會發出任何訊號；耦合但失敗（#979）至少會逼著人在合併前修正。

**方法論（可重複、不是憑印象）**：對 `test/mutants/entries/` 底下每一個 `(patch, green_test)` 組合：

1. 讀 patch，找出被改動的**確切**條件或比較式（不只是函式名稱，是哪一個分支）。
2. 讀 green test，把它的呼叫路徑追到那一行。如果呼叫圖從未到達被突變的那一行（不同程式碼路徑，或這次輸入根本不會呼叫到那個函式），就是解耦——結束。
3. 如果真的會到達，判斷這個突變能不能改變**這一次呼叫**的回傳值。兩種可證明「不能」的方式：
   - **短路（short-circuit）**：外層布林運算式較早的項已經讓結果確定，根本不會求值到被突變的那一項——worked example 是 `fileattachmentguard985-principal-key-check-neutralize`（`!IsCanonicalFileAttachmentPrincipalKey(...) && ... && false`）：對該 green test 的 model，`!IsCanonical...` 已經是 `false`，`&&` 從未求值到 mutant 加的 `&& false`。
   - **結果不變（invariant）**：對這個特定輸入，突變前後兩種寫法可證明相等——`dcext824` 的修法（`IsFileAttachmentForeignKeyProperty_UnrelatedProperty_NonDerivedPrincipal_ReturnsFalse`）：對一個 exact-type 的 `FileAttachment` principal，`typeof(FileAttachment) == typeof(FileAttachment)` 與 `typeof(FileAttachment).IsAssignableFrom(typeof(FileAttachment))` 都是 `true`，衍生類別辨識這個突變本身無法改變這一次呼叫的結果。
4. 兩者都證明不了，就是耦合——換一個 fixture/輸入，或換一個既有測試，直到證明得出來為止。
5. `run_mutant.py` 自己的 `VERDICT: KILLED` / `GATE: PASS` 是必要條件，**但不是充分條件**——它只證明 red test 依然失敗、green test 依然通過，不證明「為什麼」green test 通過。第 3 步的論證是 `run_mutant.py` 檢查不到的那一半，必須寫進 entry 的 `description`，下一個讀者才不用重新推導一次。

**這次稽核實際跑了什麼**：對 74 個 entry（68 個 `kind: security` + 6 個 `kind: selftest`）逐一套用上面的方法——讀 patch 找出被改動的確切條件，讀 green test 原始碼追它的呼叫路徑，判定屬於「從未到達」「短路」「結果不變」三類之一，或標記為耦合。**只找到上面兩個耦合案例**；其餘 66 個 `security` entry 的 green test 已經正確解耦（多數 entry 自己的 `description` 就已經寫出解耦論證，這次是逐一對照實際 patch 與測試原始碼驗證過，不是照抄 description 本身）。6 個 `selftest` entry 是 `run_mutant.py` 這支腳本自己的 meta 測試（共用一個 no-op 或刻意編譯失敗的 patch），不屬於這個缺陷類別的管轄範圍。

**#876 的耦合，程度較輕**：`876-wtmcontrolleractivator-neutralize`（`WtmControllerActivator.Create` 提早填入 `Wtm` 的 guard）原本的 `green_test` 是 `EtlMonitorController_Running_NonAdmin_Forbidden`——用的是**同一個** ETL controller 的角色判斷式 `Wtm?.LoginUserInfo?.Roles?.Select(...) ?? Array.Empty<string>()`。mutant 套用後 `Wtm` 從未被提早填入，這行走的是 `?? Array.Empty<string>()` 的 fallback 分支（而非真的求值出一個非管理員使用者的真實角色清單），最終一樣算出 `isAdmin=false` 而拒絕——跟未突變、真的判斷出「這個使用者不是管理員」得到的結論**一樣**，但是走不同的路徑。方向是安全的（fail-closed，兩條路都拒絕存取，不是 #986 命名案例那種 fail-open 方向），但原本的 description 宣稱「不影響仍然正確的 non-admin 拒絕路徑」這句話本身站不住腳。修法：改用 `MvcAuthHolesTests.Selector_AuthenticatedRequest_Succeeds`——`_FrameworkController.Selector` 完全不在自己的 `OnActionExecuting` 裡讀 `Wtm`，走的是 `PrivilegeFilter` 宣告式、以 URL 為準的授權；`DataContextFilter`/`PrivilegeFilter`（兩個都是普通的 `ActionFilterAttribute`，不受這個 mutant 影響）各自都會呼叫 `context.SetWtmContext()`，不管 `WtmControllerActivator` 有沒有提早填過——**直接讀原始碼確認**（`src/WalkingTec.Mvvm.Mvc/Filters/DataContextFilter.cs:29`、`PrivilegeFilter.cs:35`）過這件事，不是憑推測。這個 mutant 對 `Selector` 的行為完全沒有影響，是真正不同的程式碼路徑，不是碰巧結果相同。

**驗證**：

```
find . -name 'demo.db*' -path '*bin*' -delete
python3 scripts/check-mutant-entries-parse.py   # OK: all 74 mutant entry file(s) ... parse and validate.
python3 test/mutants/run_mutant.py --mutant dcext824-derived-principal-neutralize
python3 test/mutants/run_mutant.py --mutant 876-wtmcontrolleractivator-neutralize
```

兩者皆 `VERDICT: KILLED` / `GATE: PASS`；每次執行後 `git status --porcelain` 對兩個目標原始碼檔案皆為空輸出，確認 patch 乾淨還原。**只跑過這兩個修改過的 entry**，其餘 72 個 entry 這次稽核僅讀原始碼與 patch 靜態推導，未逐一重新執行 `run_mutant.py`（那需要對每個 entry 個別建置＋跑測試，落在這次工作階段的時間預算之外）——這代表**這份稽核證明的是「對照 patch 與測試原始碼手推，這 72 個 entry 的 green test 邏輯上解耦」，不是「這 72 個 entry 剛剛被重新執行過一遍都還是 `KILLED`」**；兩者不是同一件事，且靜態推導無法排除程式碼與這次讀到的版本之間的競爭條件、環境相依行為等 `run_mutant.py` 自己才驗得出來的問題（見上面「Verify a guard where it runs, not where you wrote it」一節的教訓）。

**新增規則的位置**：寫進 `.claude/rules/testing.md`（新增一節「A mutant's positive control must not touch the mutated decision path」，緊接在既有「A fixture must not supply what production is supposed to supply」之後——兩者都是「看起來像證明、其實沒證明」這一類問題）與 `test/mutants/run_mutant.py` 檔頭（緊接在 exit code 說明之後，指向 testing.md 的完整規則）。選這兩個位置是因為 `.claude/rules/testing.md` 有 `paths: test/**` 的條件式載入，任何人一碰 `test/mutants/entries/*.json` 就會自動看到這條規則；`run_mutant.py` 則是每個 mutant 作者實際會打開、讀過整段檔頭說明的腳本本身。`test/mutants/` 底下沒有獨立的 README 可以寫。


---

## LayUI TagHelper：9 個「statement 位置原樣內插 `*Func` callback」的 unwrapped-IIFE 修復（#999 part (A)，2026-08-02）

> **本項引入的已知迴歸——見 #1034。** `({expr})({args})` 的包裹**終結 optional chain 的短路傳播**：
> `*Func` 值只要含 `?.`，行為就改變。`handlers?.onChange(data)` 在 `handlers` 為 undefined 時安全短路、
> 什麼都不做；而發射出的 `(handlers?.onChange)(data)` 會先把 `undefined` 求值出來、再把它當函式呼叫，
> 拋 `TypeError` 並中止整個 callback。短路只在**同一條鏈內**傳播，加上括號就結束了那條鏈。
> 這是以**執行**兩種形狀驗證的，不是以閱讀驗證。影響 part (A)、part (B) 與 #965 合計 **20 個**包裹發射站點。
> **本項的測試沒有抓到它**，因為那些測試用真的 JS parser **解析**發射結果並斷言其文字形狀——
> 而 `(handlers?.onChange)(data)` 在語法上完全合法，只有執行才看得出差異。這是「解析通過 ≠ 行為正確」的實例。
> 沒有便宜的正確修法：`(expr)?.(args)` 會把「識別字打錯」從大聲拋錯降級為靜默無事；
> 而「文字含 `?.` 就不包裹」正是 part (B) 存在要消滅的「用字串猜 JS 文法」。修法設計在 #1034 追蹤。
> 嚴重度 **[med]**：需要開發者在 `*Func` 值裡寫 `?.`，合法但不常見——**但失效模式從「靜默無事」
> 變成「拋錯中止 callback」，方向是變差的。**
>
> **更正 2026-08-03（#1034）**：上面這句話對它指名的「做法」仍然正確——用字串猜 JS 文法（`Contains("?.")`）
> 依然被否決，理由不變：`(v)=>a?.b` 這類 arrow body 含 `?.` 卻絕不能 direct-append。**被取代的是**
> 「這個缺陷類別無解」這個讀法——本文件下方新增的「#1034」章節有封閉語言分類器修法（命中集合可證明是合法
> ES2020 `OptionalMemberExpression` 鏈，未命中一律退回今天的 wrap，byte-identical），已隨 `10.22.1` 出貨。

**背景**：#965（PR #998，已合併為 `7c9887d4b`）修了第一個被發現的站點——`DataTableTagHelper.cs` 的 `GridAction.OnClickFunc`，在 statement 起始位置原樣內插一個開發者提供的 callback 字串，若該值是匿名函式字面量（`function(ids,data){...}`）會產生 `function(ids,data){...}(ids,...)`——JS 對「statement 以 `function` 關鍵字起頭」有固定文法：一定被解析成 FunctionDeclaration（要求具名），匿名的在這個位置直接是 SyntaxError，且這個錯誤會讓**整個**外層 `<script>` block 解析失敗，不只是壞掉那一個 handler。#999 一次窮舉了全部 47 個內插站點，依**機制**分成兩類：(a) **RAW 內插**——開發者的值原封不動抵達輸出，`({X})(...)` 是完整修法；(b) **`FormatFuncName` 站點**（`BaseElementTag.cs:225-242`）——在抵達任何語法位置**之前**就先在 `(` 處截斷、補上 `(data)`，`function(v){...}` 變成字串 `function(data)`，對這種站點加括號（`(function(data));`）本身就是 SyntaxError，需要不同修法。本項只處理 (a)；(b) 的 12 個站點在 #999 part (B) 追蹤，待跨廠設計審查，**這裡不宣稱、也不暗示 `*Func` unwrapped-IIFE 這個類別已經修完**。

**本項修復的 9 個站點**（statement 位置，逐一用 JS 文法手動驗證過，非抄 issue 文字）：

| 檔案 | 屬性 | 位置 |
|---|---|---|
| `DataTableTagHelper.cs:809` | `DoneFunc` | `done: function(res,curr,count){ DoneFunc(...) }`——`table.render` 主渲染 script 裡固定會發、不受任何 island/legacy 分流影響 |
| `TransferTagHelper.cs:341` | `ChangeFunc` | `onchange: function(data,index){ defaultFunc(...); ChangeFunc(...); }`——`defaultFunc(...)` 呼叫之後的第二條 statement |
| `SliderTagHelper.cs:469` | `ChangeFunc` | `change: function(value){ defaultFunc(...); ChangeFunc(...) }`——同上形狀 |
| `DateTimeTagHelper.cs:477/478/479` | `ReadyFunc`/`ChangeFunc`/`DoneFunc` | 單欄位（非 `IsRange`）路徑，各自獨立的 `function(...){ X(...) }` callback，內插值是該 function body 唯一（起始）的 statement |
| `DateTimeTagHelper.cs:577/578` | `ReadyFunc`/`ChangeFunc` | 兩隱藏 input 的 `IsRange` 路徑，同上形狀 |
| `DateTimeTagHelper.cs:582` | `DoneFunc` | `IsRange` 路徑共用的 `done: function(value,date,endDate){...}`——`DoneFunc(...)` 是內建 split（`document.getElementById(...).value=...`）兩條 statement**之後**的第三條 statement，一樣是 statement 位置 |

修法與 #965 相同：`({X})(...)`——把值強制推進 expression context，parser 不會再走 FunctionDeclaration 分支。#965 已驗證這個包法對 bare-identifier（`(myFn)(a,b)`）、dotted（`(obj.method)(a,b)`）、call-expression（`(getHandler())(a,b)`）三種既有合法形狀都相容中立（加括號不改變求值結果）；本項的 9 個站點內插值型別與 #965 完全同構，同一條 JS 文法論證直接適用。

**任務指示裡的分類核對，含一處誠實更正**：任務原文列出「exactly these sites」的清單合計是 1（DataTable）+1（Transfer）+1（Slider）+6（DateTime）= **9** 個站點，但同一份任務指示稍後的驗證段落寫「Verify each of the ten in-scope sites」——**這是一處計數誤植，不是漏掉了第 10 個站點**：對這 4 個檔案做過詳盡 `grep -nE '\{[A-Za-z_]*Func\}'` 全站點掃描（見下方可重跑指令），結果與「exactly these sites」清單逐一對應、沒有多、沒有少。同一次掃描也覆核了任務指示要求排除的兩個站點，兩者皆屬 expression 位置，確認**不應該**包括在本次修法範圍：

- `SliderTagHelper.cs:471`——`,setTips: function(value){{return {OnTipsFunc}(value,sliderIns);}}`：`return` 之後的內插值是 return expression 的一部分，parser 一開始就走 expression 文法，不會誤判成 FunctionDeclaration。
- `DataTableTagHelper.cs:838`——`table.on('checkbox({Id})',{CheckedFunc});`：內插值是 `table.on(...)` 呼叫的第二個引數，同樣是 expression 位置。

`DataTableTagHelper.cs:1284`（`GridAction.OnClickFunc`）依任務指示刻意不動——留給仍在同一分支族但獨立的 `fix/965-datatable-unwrapped-iife`（PR #998）處理，避免兩支分支在同一行衝突。

**測試——真的用 JS parser 解析輸出，不是字串比對**：新增 `test/WalkingTec.Mvvm.Core.Test/TagHelpers/RawFuncInterpolationParens999Tests.cs`，11 個測試方法：對 9 個修復站點各一個（渲染真實 TagHelper、抽出實際輸出的 `<script>` block、餵給 Acornima 解析，解析失敗即 Fail），另外 2 個對上方兩個排除站點做**正面驗證**——用同樣函式字面量餵進去，斷言**不加括號**也已經能正確解析（證明這兩個站點過去就沒壞、不該被動）。

**Acornima 相依重複、刻意如此**：本 repo `origin/dotnet10` 目前沒有任何 JS parser 相依。PR #998（#965，已合併為 `7c9887d4b`）以完全相同的方式引入 **Acornima 1.6.2**（BSD-3-Clause、純 .NET、Test262-complete、test-only）——`Directory.Packages.props` 一行 `<PackageVersion Include="Acornima" Version="1.6.2" />`＋`WalkingTec.Mvvm.Core.Test.csproj` 一行 `<PackageReference Include="Acornima" />`，不被任何出貨專案引用。本項在**完全相同的版本、完全相同的位置**重複這兩行——兩支分支之後合併時，這兩個檔案的衝突會是逐字相同、trivial 的重複行衝突，不是語意衝突。

**RED-before-fix（暫時 `git stash push` 還原 4 個 source 檔案、只保留測試與套件改動，重跑）**：

```
Failed DataTable_DoneFunc_FunctionLiteral_ParsesAsValidJavaScript
Failed Transfer_ChangeFunc_FunctionLiteral_ParsesAsValidJavaScript
Failed Slider_ChangeFunc_FunctionLiteral_ParsesAsValidJavaScript
Failed DateTime_SingleField_ReadyFunc_FunctionLiteral_ParsesAsValidJavaScript
Failed DateTime_SingleField_ChangeFunc_FunctionLiteral_ParsesAsValidJavaScript
Failed DateTime_SingleField_DoneFunc_FunctionLiteral_ParsesAsValidJavaScript
Failed DateTime_Range_ReadyFunc_FunctionLiteral_ParsesAsValidJavaScript
Failed DateTime_Range_ChangeFunc_FunctionLiteral_ParsesAsValidJavaScript
Failed DateTime_Range_DoneFunc_FunctionLiteral_ParsesAsValidJavaScript
Passed Slider_OnTipsFunc_FunctionLiteral_AlreadyParsesAsValidJavaScript_NoWrappingNeeded
Passed DataTable_CheckedFunc_FunctionLiteral_AlreadyParsesAsValidJavaScript_NoWrappingNeeded
Total tests: 11 / Passed: 2 / Failed: 9
```

每個 RED 的失敗訊息都是 Acornima 的 `ParseErrorException`（例如 `Unexpected token '(' (17:41)`），指向內插值後面緊跟著的呼叫括號——與診斷完全吻合，不是巧合性的其他失敗。還原修法（`git stash pop`）後同一組測試：**GREEN，11/11 全綠**。

**既有 byte-identity 測試的連帶修正（誠實揭露這不是零成本的加括號）**：全 `test/WalkingTec.Mvvm.Core.Test` 套件（5052 個測試，含新增的 11 個）先跑出 2 個既有失敗——`RenderGridIsland470SliceO1Tests.cs`（`NonIdentifierDoneFunc_FlagOn_FallsBackToLegacy_WithWarn`）與 `RenderTransferIsland470SliceKTests.cs`（`Transfer_FlagOn_NonIdentifierChangeFunc_KeepsInlineRender_EmitsWarn`）各自釘死了修復前**沒有括號**的確切子字串（`myObj.notAnIdentifier(res,curr,count)`／`some.dotted.expr(data, index,transferIns);`）。這是預期中的連帶影響，不是回歸：兩者都改成斷言加括號後的形狀（`(myObj.notAnIdentifier)(res,curr,count)`／`(some.dotted.expr)(data, index,transferIns);`），並在旁加註解說明原因。全庫 `grep` 過一輪這 6 個站點的舊形狀子字串，確認這兩處是**唯一**受影響的既有測試，沒有第三個遺漏。修正後全套件：**5052 passed, 0 failed**（測試數：修復前 5041 個既有 + 本項新增 11 個 = 5052，數量吻合）。

**Mutant 判斷：本項不新增 `test/mutants/entries/*.json`**。理由：(1) 這是 JS 解析正確性／相容性修復，不涉及未授權存取、injection、跨租戶或憑證外洩，不是傳統意義的安全漏洞——與 #970 條目記錄的 `etl970-cancellation-classification-guard-neutralize` 同一種「correctness-only 卻被迫套用 `security` kind」處境；(2) `run_mutant.py` 的 `VALID_KINDS` 目前只接受 `security`／`selftest`，若把本項強塞成 `security`，等於重複 #970 已經記錄在案、且 #968 花了一整張 PR 才吸收掉的 kind 分類漂移，本文件不應該再製造同一種漂移；(3) 更關鍵的差異：這個修復的回歸保護**已經**是 CI 強制的——`build-and-test`（required check）跑的 `dotnet test` 涵蓋新增的 9 個逐站點測試，任何一個站點被意外還原都會讓對應的那一個測試變紅（上方 RED-before-fix 就是這個機制本身的決定性重現，不是推論），`mutants` job 的 `kind: security` 額度不是這個修復唯一的執行保證。若未來 `VALID_KINDS` 新增一個 `correctness`/`compat` kind（#970 條目已提過這個需求，本項不重複開票），屆時回頭補一個 mutant entry 是合理的；本次判斷是不在沒有適配 kind 的情況下勉強塞。

**未能驗證的部分（誠實列出）**：本次工作階段 HARD CONSTRAINT 禁止呼叫任何 Gitea/GitHub API、禁止開 PR，因此這個修復尚未在真正的 Gitea Actions CI 上跑過——本機驗證只到 `dotnet build`/`dotnet test` 這兩層，`js-test`（Jest/jsdom）與 `e2e` 兩個 CI leg 完全沒有覆蓋到 TagHelper 產生的 inline `<script>` 是否真的被瀏覽器執行，本項也沒有另外起 demo app 手動驗證瀏覽器端行為（純 C# 字串輸出＋ JS parser 靜態驗證，沒有執行 JS）。`FormatFuncName` 的 12 個站點（part B）完全沒有動，也沒有重新驗證 #999 的 47 站點總表在其他維度（例如是否還有站點介於「statement 位置」與「`FormatFuncName` 截斷」兩種分類之外）是否窮盡——這次工作只覆核了任務指示明確列出的 9＋2 個站點，不宣稱重新窮舉了全部 47 個。

**可重跑的盤點指令**：
```bash
grep -nE '\{[A-Za-z_]*Func\}' \
  src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.cs \
  src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/TransferTagHelper.cs \
  src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/SliderTagHelper.cs \
  src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/DateTimeTagHelper.cs
find . -name 'demo.db*' -path '*bin*' -delete
dotnet test test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj -c Release \
  --filter "FullyQualifiedName~RawFuncInterpolationParens999Tests"
```

---

## LayUI TagHelper：`FormatFuncName` 截斷造成的 10 個站點修復——新增 `FormatFuncInvocation`（#999 part (B)，2026-08-03）

> **本項引入的已知迴歸——見 #1034。** `({expr})({args})` 的包裹**終結 optional chain 的短路傳播**：
> `*Func` 值只要含 `?.`，行為就改變。`handlers?.onChange(data)` 在 `handlers` 為 undefined 時安全短路、
> 什麼都不做；而發射出的 `(handlers?.onChange)(data)` 會先把 `undefined` 求值出來、再把它當函式呼叫，
> 拋 `TypeError` 並中止整個 callback。短路只在**同一條鏈內**傳播，加上括號就結束了那條鏈。
> 這是以**執行**兩種形狀驗證的，不是以閱讀驗證。影響 part (A)、part (B) 與 #965 合計 **20 個**包裹發射站點。
> **本項的測試沒有抓到它**，因為那些測試用真的 JS parser **解析**發射結果並斷言其文字形狀——
> 而 `(handlers?.onChange)(data)` 在語法上完全合法，只有執行才看得出差異。這是「解析通過 ≠ 行為正確」的實例。
> 沒有便宜的正確修法：`(expr)?.(args)` 會把「識別字打錯」從大聲拋錯降級為靜默無事；
> 而「文字含 `?.` 就不包裹」正是 part (B) 存在要消滅的「用字串猜 JS 文法」。修法設計在 #1034 追蹤。
> 嚴重度 **[med]**：需要開發者在 `*Func` 值裡寫 `?.`，合法但不常見——**但失效模式從「靜默無事」
> 變成「拋錯中止 callback」，方向是變差的。**
>
> **更正 2026-08-03（#1034）**：上面這句話對它指名的「做法」仍然正確——用字串猜 JS 文法（`Contains("?.")`）
> 依然被否決，理由不變：`(v)=>a?.b` 這類 arrow body 含 `?.` 卻絕不能 direct-append。**被取代的是**
> 「這個缺陷類別無解」這個讀法——本文件下方新增的「#1034」章節有封閉語言分類器修法（命中集合可證明是合法
> ES2020 `OptionalMemberExpression` 鏈，未命中一律退回今天的 wrap，byte-identical），已隨 `10.22.1` 出貨。

**背景**：part (A)（#1003）修了 9 個「statement 位置原樣內插」的站點——開發者的 `*Func` 值原封不動抵達輸出，`({X})(...)` 加括號是完整修法。它明確排除了另一類站點：所有經過 `BaseElementTag.FormatFuncName`（`src/WalkingTec.Mvvm.TagHelpers.LayUI/Abstraction/BaseElementTag.cs:225-242`）的呼叫。`FormatFuncName` 在**抵達任何語法位置之前**就先在第一個 `(` 處截斷、補上 `(data)`：

```csharp
var ind = rv.IndexOf("(");
if (ind > 0) { rv = rv.Substring(0, ind); }   // function(v){...}  ->  function
if (appendparameter == true) { rv += "(data)"; }  //                ->  function(data)
```

`function(v){...}` 變成字串 `function(data)`，對這個結果加括號（`(function(data));`）本身就是 SyntaxError——已經實際執行 Acornima 解析確認過。

**Part (B) 的第一版設計被跨廠 review 打回**：原計畫是「當第一個 `(` 前面的文字不是 plain identifier 時，`FormatFuncName` 停止截斷」。對函式字面量 `function(v){...}`，那段文字就是字面上的 `function`——這**通過**本專案到處在用的 identifier regex `^[A-Za-z_$][\w$]*\z`。`function` 是 JS **關鍵字**，不是 identifier，而這個 regex 完全不認識關鍵字。這個修法會上線但什麼都沒改到。

**決定採用的設計：不動 `FormatFuncName`，新增一個獨立的 helper，只負責產生「完整可呼叫的 invocation」。**

### 1. Helper 的位置與簽章，及理由

```csharp
public static string FormatFuncInvocation(string funcExpression, string args = "data")
{
    if (string.IsNullOrEmpty(funcExpression))
    {
        return null;
    }
    return $"({funcExpression})({args})";
}
```

放在 `BaseElementTag`（`FormatFuncName`正下方），**static**（不是像 `FormatFuncName` 那樣的 instance 方法）。理由：

- **不需要任何 instance 狀態**——`FormatFuncInvocation` 是純字串轉換，不像 `FormatFuncName` 目前雖為 instance method 但其實也用不到 `this`；用 static 更準確反映這件事。
- **可測試性**——static 方法能不建構任何 TagHelper 子類別、直接單元測試（雖然本項驗收仍以「渲染真實 TagHelper 輸出」為主，static 只是讓這個選項保持開放）。
- **與 `FormatFuncName` 的「不可 override」問題無關，且刻意如此**：`BaseElementTag` 是 `public abstract`，`FormatFuncName` 是 `public` 但**非 virtual**——下游只能用 `new` 隱藏它、無法 override。這件事對本次新 helper 完全不構成理由去改動 `FormatFuncName` 的簽章或可見度：`FormatFuncInvocation` 是一個全新、獨立的方法，不呼叫 `FormatFuncName`、也不被 `FormatFuncName` 呼叫，兩者的可覆寫性互不相干。
- **考慮過但放棄的替代方案：獨立 static utility class**（例如 `Common/` 底下一個新的 `JsInvocationHelper`）。放棄理由：所有 10 個需要它的呼叫點都在 `BaseElementTag` 的子類別內（`TreeContainerTagHelper : BaseElementTag`；`TreeTagHelper`/`ComboBoxTagHelper`/`ColorPickerTagHelper`/`SelectorTagHelper` : `BaseFieldTag` : `BaseElementTag`），放在 `BaseElementTag` 上讓這些呼叫點沿用現有「呼叫繼承來的方法、不用額外 `using`」的寫法，diff 最小；且緊鄰 `FormatFuncName` 也讓日後讀者在看到 `FormatFuncName` 時容易發現這個姊妹方法。
- **刻意不與 `FormatFuncName` 共用任何程式碼路徑**（互不呼叫）：這是本 issue 的核心教訓——讓同一條字串同時當「拿去分類的依據」與「拿去執行的程式碼」，是缺陷的根源，不是細節。`FormatFuncName` 的呼叫者把輸出當**決策輸入**（是不是 plain identifier？）或 **HTML 屬性值**；`FormatFuncInvocation` 的呼叫者把輸出當**要執行的 JS**。兩條路徑在原始碼層面就分開，任何人都不會不小心把一個方法的輸出接到另一個方法原本設計要接的地方。

### 2. 站點表（10 個切換的站點，逐一重新推導，未信任任何既有清單）

自行重跑 `grep -rn "FormatFuncName(" --include="*.cs" .`（排除 `bin/`/`obj/`），對整個 repo（不只 `src/`）逐行確認是否為註解、是否真的執行到、以及被截斷後的字串最終落在 JS 的什麼語法位置：

| # | 檔案:行 | 分支 | JS 語法位置 | 呼叫參數 |
|---|---|---|---|---|
| 1 | `BaseElementTag.cs:348`（`EmitFormChangeWiring`） | CheckBox/Switch/Radio `ChangeFunc`，`form.on('{kind}({filter})', function(data){{ X; }})` | statement | `data` |
| 2 | `BaseElementTag.cs:408`（`EmitAutocompleteWiring`，有 TriggerUrl） | TextBox `ChangeFunc`，`onselect: function(data){{ ...; X; ff.ChainChange(...); }}` | statement | `data` |
| 3 | `BaseElementTag.cs:429`（`EmitAutocompleteWiring`，無 TriggerUrl） | TextBox `ChangeFunc`，`onselect: function(data){{ ...; X; }}` | statement | `data` |
| 4 | `TreeContainerTagHelper.cs:311` | `ClickFunc`，賦值給 `cusmtomclick`，嵌入 `click: function(data){{ ...; X; }}` | statement | `data` |
| 5 | `SelectorTagHelper.cs:478` | `BeforeOnpenDialogFunc`，`$('#..._Select').on('click',function(){{ var data={{}}; X; ... }})` | statement（`data` 是合成的空物件） | `data` |
| 6 | `TreeTagHelper.cs:368` | `ChangeFunc`，`if (X != false) {{ ...ChainChange... }}`（`LinkField`/`LinkId` 有設） | **expression**（if 條件） | `data` |
| 7 | `TreeTagHelper.cs:377` | `ChangeFunc`，`on:function(data){{ X }}`（`LinkField`/`LinkId` 未設） | statement | `data` |
| 8 | `ComboBoxTagHelper.cs:458` | 同 #6 鏡像 | **expression**（if 條件） | `data` |
| 9 | `ComboBoxTagHelper.cs:467` | 同 #7 鏡像 | statement | `data` |
| 10 | `ColorPicker.cs:291` | `ChangeFunc`，`done: function(data){{ ...; X; }}` | statement | `data` |

**為什麼 #6/#8 是「expression 位置」而不是 statement**：`if (X != false)` 裡的 `X` 是 if 條件的一部分，必須是一個能求值的 expression，不能是裸的 statement。舊行為下 `X = function(data)`（截斷後）——parser 進入 `if(...)` 後已經在 expression 文法裡，看到 `function` 關鍵字會嘗試把它解析成匿名 FunctionExpression，接著預期 `{` 開始函式本體，卻遇到 `!=`——一樣是 SyntaxError（原因與 statement 位置的「匿名 FunctionDeclaration 不合法」不同，但結論相同：解析失敗）。新設計不需要區分 statement/expression 就能一律安全：`(expr)(args)` 這個形狀在任何位置都合法（grouping operator 可以出現在任何 expression 允許出現的地方），這正是「不用猜語法位置」這條設計原則帶來的簡化——helper 完全不需要知道自己被放在哪裡。

**args 全部是字面上的 `"data"`**：10 個站點原本都用 `FormatFuncName` 的預設 `appendparameter=true`（固定補 `"(data)"`），沒有一個站點用其他參數名。

### 3. `FormatFuncName` 其餘 21 個呼叫點的驗證，及 `[Obsolete]` 判斷

`FormatFuncName` 目前在整個 repo（`bin`/`obj` 除外）共有 **21 個非註解的真實呼叫點**（用 `grep -rn "FormatFuncName(" --include="*.cs" . | grep -v '/bin/\|/obj/'` 核對過，只有這 21 行是活的呼叫，另有數行是註解文字提及 `FormatFuncName` 但不是呼叫）。上表 10 個已切換到 `FormatFuncInvocation`；剩下 **11 個**維持呼叫 `FormatFuncName`，逐一核對用途：

| 檔案:行 | 用途 |
|---|---|
| `BaseElementTag.cs:321`（`EmitFormChangeWiring`） | 決策輸入——算出 `changeFuncName` 餵給 `_changeFuncIdentifierRegex.IsMatch(...)`，決定 `useIsland` |
| `BaseElementTag.cs:370`（`EmitAutocompleteWiring`） | 同上 |
| `ComboBoxTagHelper.cs:129` | HTML 屬性——`output.Attributes.Add("wtm-cf", ...)`，寫進 data attribute，不是可執行 JS |
| `ComboBoxTagHelper.cs:321` | 決策輸入——`changeIsIdentifier` 判斷，驅動 `useSelectIsland` |
| `TreeTagHelper.cs:236` | 決策輸入——同上（Tree 版） |
| `TreeContainerTagHelper.cs:130` | 決策輸入——`clickIsIdentifier` 判斷，驅動 `useTreeContainerIsland` |
| `ColorPicker.cs:171` | 決策輸入——`changeIsIdentifier` 判斷，驅動 `useColorIsland`；同一個截斷後的 bare name 之後也被放進 island DTO 的 `ChangeFn` 欄位（JSON 資料，不是內嵌 JS） |
| `TransferTagHelper.cs:262` | 決策輸入——同上（Transfer 版）；Transfer 自己的合法 JS 內插站點（`TransferTagHelper.cs:341`）在 part (A) 已經改成直接包裝**原始** `ChangeFunc`（`({ChangeFunc})(data, index,transferIns);`），根本不經過 `FormatFuncName`，所以不在本次 10 站點清單內 |
| `TextBoxTagHelper.cs:98` | 決策輸入——`changeFuncName`／`changeIsIdentifier`，驅動要不要走 island |
| `TextBoxTagHelper.cs:99` | 決策輸入——`doneFuncName`／`doneIsIdentifier`，同上 |
| `CheckBoxTagHelper.cs:199` | HTML 屬性——`output.Attributes.Add("wtm-cf", ...)`，同 ComboBox |

任務原始清單列的 11 個站點（`BaseElementTag.cs:265`/`:314`、`ComboBoxTagHelper.cs:129`/`:321`、`TreeTagHelper.cs:236`、`TreeContainerTagHelper.cs:130`、`ColorPicker.cs:171`、`TransferTagHelper.cs:262`、`TextBoxTagHelper.cs:98`/`:99`、`CheckBoxTagHelper.cs:199`）是舊行號（`FormatFuncInvocation` 加入後整個檔案位移），用語意重新定位後**逐一核對一致**——同一組 11 個呼叫點，只是行號因為新增了 `FormatFuncInvocation` 方法本體而往下移動（例如 `BaseElementTag.cs:265`→現在的 `:321`）。

**重要發現：`TextBoxTagHelper.cs` 的 `oninput`/`onchange` HTML 屬性寫入（`:105`/`:109`）不在 `FormatFuncName` 的 21 個呼叫點內，但用的是同一個已截斷字串**——`changeFuncName`/`doneFuncName`（`:98`/`:99` 算出）稍後被手動接上 `(this.value)`：`output.Attributes.Add("oninput", $"{changeFuncName}(this.value)")`。這是同一個缺陷類別在第三個地方的變體，但**本次刻意不切換**：只有在 `!changeIsIdentifier` 時才會走到這行，而 `FormatFuncName` 對函式字面量 `"function(v){...}"` 的截斷結果剛好是 `"function"`——通過 identifier regex，所以函式字面量根本走不到這個屬性寫入分支（走的是 island 分支，`ChangeFunc="function"`，client 端解析不到這個名字，靜默跳過，不是 SyntaxError）。唯一會走到這個屬性分支的是「截斷後仍非 identifier」的形狀（例如 dotted `a.b.foo` 或以 `(` 開頭的 arrow function），這些形狀目前產生的屬性值（`a.b.foo(this.value)`、`(v)=>{...}(this.value)`）本身另有語法風險（後者的匿名 arrow function 直接呼叫語法本身可能不合法），但這是一個**與本次 10 個站點不同、需要另開 issue 追蹤的缺陷**——本次判斷任務指示明確列出的清單已經涵蓋且核對過的 11 個屬於「決策輸入或 HTML 屬性」，`oninput`/`onchange` 這兩行不在該清單內，也不在本次修復範圍，不擴大改動面。

**`[Obsolete]` 判斷：不標記。** 上述 11 個呼叫點裡，除了 2 個純 HTML 屬性寫入（`wtm-cf`）外，其餘 9 個全部把 `FormatFuncName` 的截斷輸出當作 identifier 分類的輸入，直接驅動 `useIsland`/`useSelectIsland`/`useTreeContainerIsland`/`useColorIsland` 這些 3-way 決策的走向——這些呼叫點在可預見的未來都還需要「truncate-then-classify」這個行為本身，`FormatFuncInvocation` 完全不能取代它們（`FormatFuncInvocation` 故意不截斷，用在決策輸入上會讓「是不是 identifier」這個問題失去意義）。沒有證據顯示這 11 個呼叫點裡有任何一個是可以刪除或遷移的死碼。

### 4. `changeIsIdentifier` 類決策未變

逐一核對：10 個切換站點全部只動了「已經被某個既有分支選中之後、放在該分支哪個位置的文字」，沒有一個站點的**分支選擇本身**被觸碰——

- `EmitFormChangeWiring`/`EmitAutocompleteWiring` 的 `useIsland` 判斷式（`isIdentifier`）完全不變，只是 `else`（legacy inline script）分支內、被寫進 `<script>` 的那行文字從 `FormatFuncName(changeFunc)` 換成 `FormatFuncInvocation(changeFunc)`。
- `TreeTagHelper`/`ComboBoxTagHelper` 的 `useSelectIsland`/`changeIsIdentifier` 判斷式完全不變；`if (X != false)` 與 `on:function(data){{X}}` 兩處的 `X` 只是文字形狀變了。
- `ColorPicker` 的 `useColorIsland`/`changeIsIdentifier` 判斷式完全不變；只有 `else`（legacy）分支內 `done:` callback 的那行文字變了。
- `TreeContainerTagHelper`/`SelectorTagHelper` 完全沒有走到任何 `IsIdentifier` 判斷（`ClickFunc`/`BeforeOnpenDialogFunc` 不受 island flag 影響，一律走這條路徑）。

**沒有任何一個 island/legacy 選擇、任何一個 `console.warn` 觸發條件、任何一個 `required` 驗證時機被改變**——這正是任務指示要求「若會翻轉就停下回報」的紅線；本次確認未翻轉，全套件（5092 測試，含 5 個既有斷言的 byte-shape 更新）全綠佐證。

### 5. 測試

新增 `test/WalkingTec.Mvvm.Core.Test/TagHelpers/FormatFuncInvocation999BTests.cs`，18 個測試：

- **10 個函式字面量測試**（每個切換站點各一個）——渲染真實 TagHelper、抽出實際輸出的 `<script>` block、餵給 Acornima 解析。
- **3 個 arrow function 測試**（`CheckBoxTagHelper`、`TreeTagHelper`/`ComboBoxTagHelper` 的 `if (X != false)` 分支）。
- **2 個 plain identifier 行為保留測試**（`CheckBoxTagHelper`、`TreeTagHelper` 的 `if` 分支）——斷言 `(myFunc)(data)` 這個新形狀，證明語意與舊的 `myFunc(data)` 完全等價。
- **3 個 dotted member `this`-binding 保留測試**（`CheckBoxTagHelper`、`TreeTagHelper` 的 `if` 分支、`ColorPicker`）——斷言完整 dotted 表達式被整個包在括號內、呼叫參數在括號外（例如 `(ns999.obj.doThing)(data);`），這個文字形狀正是保證 `this` 綁定不變的關鍵（grouping operator 不會剝離 `MemberExpression` 產生的 Reference）。

**RED-before-fix（逐字擷取，18/18 全紅，先跑此檔再改 source）**：

```
Failed CheckBox_ChangeFunc_FunctionLiteral_ParsesAsValidJavaScript
Failed CheckBox_ChangeFunc_ArrowFunction_ParsesAsValidJavaScript
Failed CheckBox_ChangeFunc_PlainIdentifier_StillCallsThroughUnchanged
Failed CheckBox_ChangeFunc_DottedMember_PreservesThisBindingShape
Failed TextBox_ChangeFunc_WithTriggerUrl_FunctionLiteral_ParsesAsValidJavaScript
Failed TextBox_ChangeFunc_NoTriggerUrl_FunctionLiteral_ParsesAsValidJavaScript
Failed TreeContainer_ClickFunc_FunctionLiteral_ParsesAsValidJavaScript
Failed Selector_BeforeOnpenDialogFunc_FunctionLiteral_ParsesAsValidJavaScript
Failed Tree_ChangeFunc_WithLink_FunctionLiteral_ParsesAsValidJavaScript
Failed Tree_ChangeFunc_WithLink_ArrowFunction_ParsesAsValidJavaScript
Failed Tree_ChangeFunc_WithLink_PlainIdentifier_StillCallsThroughUnchanged
Failed Tree_ChangeFunc_WithLink_DottedMember_PreservesThisBindingShape
Failed Tree_ChangeFunc_NoLink_FunctionLiteral_ParsesAsValidJavaScript
Failed ComboBox_ChangeFunc_WithLink_FunctionLiteral_ParsesAsValidJavaScript
Failed ComboBox_ChangeFunc_WithLink_ArrowFunction_ParsesAsValidJavaScript
Failed ComboBox_ChangeFunc_NoLink_FunctionLiteral_ParsesAsValidJavaScript
Failed ColorPicker_ChangeFunc_FunctionLiteral_ParsesAsValidJavaScript
Failed ColorPicker_ChangeFunc_DottedMember_PreservesThisBindingShape

Failed! - Failed: 18, Passed: 0, Skipped: 0, Total: 18
```

三個代表性失敗訊息，逐字擷取，證明截斷確實發生（不是巧合性的其他失敗）：

```
Failed CheckBox_ChangeFunc_FunctionLiteral_ParsesAsValidJavaScript
StringAssert.Contains failed. String '
 cb999_litdefaultvalues = ["System.Collections.Generic.List`1[System.String]"];
' does not contain string 'OpenDialog'.
```

```
Failed Selector_BeforeOnpenDialogFunc_FunctionLiteral_ParsesAsValidJavaScript
StringAssert.Contains failed. String '
var sel999_litfilter = {};
$('#sel999_lit_Select').on('click',function(){
  var data={};function(data);
  ...
' does not contain string 'SelectorBeforeOpen999B'.
```

`Selector` 這個失敗訊息本身就是最直接的證據——`function(data){ff.OpenDialog(...)}` 這整個函式本體（含標記字串 `SelectorBeforeOpen999B`）在截斷後完全消失，只剩下 `function(data);`，與 issue 描述的 `function(v){...}` → `function(data)` 分毫不差。

```
Failed ComboBox_ChangeFunc_WithLink_ArrowFunction_ParsesAsValidJavaScript
Assert.Fail failed. ComboBoxTagHelper's if (ChangeFunc != false) link-chain gate, arrow-function
ChangeFunc emitted a <script> block that is not valid JavaScript ...
Parser error: Unexpected token '(' (43:102)
```

**GREEN-after-fix**：套用 10 個站點的修改後，同一批 18 個測試 **18/18 全綠**。全套件（`dotnet test test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj -c Debug -m:1`）**5092 passed, 0 failed**（`-m:1` 依循 `.claude/rules`/`#902` 序列化 testhost 避免 OOM 的既有慣例）。

**既有測試的連帶修正（誠實揭露這不是零成本的加括號）**：`test/WalkingTec.Mvvm.Core.Test/TagHelpers/ResidualEmitters784BaseElementTests.cs` 有 5 個既有斷言釘死了修復前**沒有括號**的確切子字串——`"myCheckChange(data);"`（CheckBox flag-off）、`"obj.myCheckChange(data);"`（CheckBox flag-on 非-identifier）、`"mySwitchChange(data);"`（Switch flag-off）、`"myRadioChange(data);"`（Radio flag-off）、`"obj.myTextChange(data);"`（TextBox flag-on 非-identifier）。全部改成加括號後的形狀（例如 `"(myCheckChange)(data);"`），並在旁加註解說明原因，同 part (A) 對 `RenderGridIsland470SliceO1Tests.cs`/`RenderTransferIsland470SliceKTests.cs` 的處理方式。用 `grep -rnE '"[A-Za-z_$][A-Za-z0-9_$.]*\(data\)' test/` 對整個 `test/` 目錄掃過一輪這個形狀，確認這 5 行是**唯一**受影響的既有斷言，沒有第 6 個遺漏（`ColorPickerTagHelperTests.cs`/`RenderSelectIsland470SliceJTests.cs`/`RenderTreeContainerIsland470SliceN1Tests.cs` 裡雖然也有斷言 ChangeFunc/ClickFunc 的原始值字串如 `"some.dotted.expr"`，但都只斷言**不含呼叫括號**的裸子字串，加括號後仍然包含該子字串，不受影響）。

### 6. 站點計數核對——訂正 part (A) commit message 裡的「12」

part (A) 的 commit message 寫「FormatFuncName sites (12)」。本次不信任這個數字，重新從目前的樹逐行核對：

```bash
grep -rn "FormatFuncName(" --include="*.cs" . | grep -v '/bin/\|/obj/'
```

整個 repo（含 `test/`，排除 `bin`/`obj`）共 34 行提及 `FormatFuncName`，其中：**21 行是真實、非註解的呼叫**（含方法定義本身之外的呼叫點），其餘是註解或方法簽章本身。21 個呼叫點裡：**10 個是本次切換的 emission 站點，11 個是決策輸入/HTML 屬性站點**。

part (A) 估計的「12」比實際的「10」多 2——多出的 2 個是 `BaseElementTag.cs:166`/`:182`，位於一整段 `//` 註解掉的 `ComboBoxTagHelper` `LinkField`/`TriggerUrl` `form.on(...)` 區塊裡（`BaseElementTag.cs:150-189` 的 `case ComboBoxTagHelper item:` 分支，除了 `if(item.MultiSelect == true){break;}` 這行是活的，其餘全部被註解掉，不編譯、不執行）。這兩行**不是活的缺陷**——它們不會產生任何輸出，也不可能觸發 SyntaxError，因為它們根本不會被執行。part (A) 當時窮舉時很可能是連同註解文字一起數的，這是可以理解的（早期審計常見的過度計數，而非低估），但本次既然被要求「re-derive, don't trust」，就把這個訂正明確寫下來。

**修訂後的 #999 總帳**：#999 原始估計的 22 個站點（10 raw + 12 truncation），扣掉 2 個死碼「站點」，實際活的缺陷母體是 **20 個**（10 raw-interpolation + 10 `FormatFuncName`-truncation）。目前狀態：

| 分類 | 母體 | 已修 | 修復者 |
|---|---|---|---|
| raw interpolation | 10 | 10 | #965（1 個）+ part (A)/#1003（9 個） |
| `FormatFuncName` truncation | 10 | 10 | part (B)（本次，10 個） |
| **合計** | **20** | **20** | |

> **這張表說的是「unwrapped-IIFE 這個缺陷類別的 20 個站點都套用了修法」，不是「這 20 個站點現在都正確」。**
> 修法本身引入了 optional-chain 迴歸（見本節開頭的 #1034 揭露），對含 `?.` 的 `*Func` 值，
> 這 20 個站點的行為比修法前更差。兩件事都成立，不要用這張表推導出「這 20 個站點已無問題」。

**這個「20/20」的完整性宣稱，其驗證邊界要誠實劃清**：raw-interpolation 那一半的「10 個母體、9+1 已修」是 part (A) 自己的重新推導結果，本次工作階段**沒有**重新獨立核對（沒有重跑 part (A) 當時用來窮舉 raw interpolation 站點的方法）；`FormatFuncName`-truncation 那一半的「10 個母體、10/10 已修」則是本次工作階段獨立重新推導、並用上方可重跑的 `grep` 指令驗證過的。換句話說：「`FormatFuncName` 這一半已經 100% 修完」是本次驗證過的宣稱；「#999 整體 20/20 已經 100% 修完」則有一半是信任 part (A) 既有工作，不是本次重新證明。

### 可重跑的盤點指令

```bash
# 21 個真實呼叫點（10 emission + 11 decision-input/attribute）
grep -rn "FormatFuncName(" --include="*.cs" . | grep -v '/bin/\|/obj/' | grep -v "public string FormatFuncName"

# 新 helper 的 10 個呼叫點
grep -rn "FormatFuncInvocation(" --include="*.cs" src/ | grep -v "public static string FormatFuncInvocation"

find . -name 'demo.db*' -path '*bin*' -delete
dotnet test test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj -c Debug \
  --filter "FullyQualifiedName~FormatFuncInvocation999BTests"

# mutant patch 逐檔核對（68/68 apply 乾淨，0 orphan）
python3 scripts/check-mutant-entries-parse.py
```

### Mutant：考慮過，不新增

理由與 #965、part (A) 完全一致：這是 JS 解析正確性修復，不是傳統意義的安全漏洞（無未授權存取、injection、跨租戶、憑證外洩維度）；`run_mutant.py` 的 `VALID_KINDS`（`security`/`selftest`）沒有適合這個類別的 kind，硬塞成 `security` 只會重複 #970/#968 已經吸收過的 kind 分類漂移。回歸保護已經是 CI 強制的：10 個切換站點裡任何一個被意外還原成 `FormatFuncName`，`FormatFuncInvocation999BTests.cs` 裡對應的函式字面量測試就會變紅（上方 RED-before-fix 逐字輸出就是這個機制本身的決定性重現，不是推論）。

### 未能驗證的部分（誠實列出）

本次工作階段 HARD CONSTRAINT 禁止呼叫任何 Gitea/GitHub API、禁止開 PR，因此這個修復尚未在真正的 Gitea Actions CI 上跑過——本機驗證只到 `dotnet build`/`dotnet test` 這兩層，沒有另外起 demo app 手動驗證瀏覽器端行為（純 C# 字串輸出＋ JS parser 靜態驗證，沒有執行 JS，也沒有覆蓋 `js-test`/`e2e` 兩個 CI leg）。`TextBoxTagHelper.cs:105`/`:109` 的 `oninput`/`onchange` HTML 屬性寫入（同一缺陷類別的第三個變體，見上方第 3 節）本次刻意不動、也沒有另開 issue——僅在本文件與 CHANGELOG 記錄觀察到的現象，尚未建立追蹤票。raw-interpolation 那一半（part A 的 9+1 個站點）的母體重新推導本次沒有重跑，完整性宣稱的驗證邊界見上方第 6 節。

---

## `FileAttachmentSaveChangesGuard.BuildMap` 沒驗證 `fk.PrincipalKey`，Guid 替代鍵造成假允許與假拒絕（#985，cross-vendor review of #824 Part 2，2026-08-02）

**缺陷（設計 gate 已裁定，本節只記錄裁定內容與驗證過程，不重新開放討論）**：`BuildMap`（`FileAttachmentSaveChangesGuard.cs:317` 附近）只用關聯**形狀**辨識候選 FK——principal 是 `FileAttachment` 或其衍生型別（`DCExtension.IsFileAttachmentPrincipal`）——通過後就只保留依賴端屬性名稱與 principal 的 CLR 型別，**完全丟棄 `fk.PrincipalKey`**。下游解析查詢（`DCExtension.ResolveFileAttachmentIds`/`-Async`，`DCExtension.FileAttachmentResolution.cs:72`/`:114`）卻寫死 `x.ID`。對一個透過 `HasForeignKey(...).HasPrincipalKey(x => x.SomeGuidAlternateKey)` 設定、principal key 是 Guid 型別替代鍵的關聯，這個落差同時產生兩個方向的錯誤：

- **假允許**：受害者 `ID=V, AlternateGuid=A`；攻擊者 `ID=A, AlternateGuid=B`。攻擊者的依賴端 FK 帶 `A`（真正的關聯，透過 `AlternateGuid`，指向受害者的列——資料庫的 FK constraint 也確實這樣強制）。guard 的解析查詢卻是 `x.ID == A`，在攻擊者自己的租戶範圍內找到**攻擊者自己那一列**（其 `ID` 剛好等於 `A`），誤判為已解析、放行寫入。
- **假拒絕**：一個合法的、真正透過 `AlternateGuid` 指向同租戶檔案的 FK 值，永遠不會被 `x.ID` 查詢找到，於是被拒絕，即使這是完全合法的寫入。

複合鍵的情況會被同一個修法免費涵蓋，不需要獨立分支：EF Core 的 `ForeignKey.AreCompatible`（透過反編譯 `Microsoft.EntityFrameworkCore.dll` 10.0.9 確認，`ArePropertyCountsEqual` 不通過會丟 `ForeignKeyCountMismatch`）強制 FK 屬性數量必須等於 principal key 屬性數量——`FileAttachment` 繼承的主鍵永遠是單一 `[Key] Guid ID`（`TopBasePoco.cs:19`），所以任何複合 FK 都不可能指向這個單欄位 PK，同一條「principal key 不是那個單一 Guid ID」判斷式自然涵蓋它。

**修法（設計 gate 裁定：在 `BuildMap` 時偵測並大聲拒絕，不把解析查詢改成通用查詢）**：`IsFileAttachmentPrincipal` 通過後（`:317` 之後），新增 `IsCanonicalFileAttachmentPrincipalKey` 判斷 `fk.PrincipalKey`：要求 `pk.IsPrimaryKey()` **且** `pk.Properties.Count == 1` **且** 該屬性名稱是 `nameof(TopBasePoco.ID)` **且** 其（去除 nullable 包裝後的）CLR 型別是 `Guid`——**不是**只查 `IsPrimaryKey()`：下游 context 可以在自己的 `OnModelCreating` 裡重新宣告 `FileAttachment` 的主鍵（例如改成複合鍵，或改名的單一欄位），那樣仍然會通過 `IsPrimaryKey()` 卻依然不是解析查詢實際查的那個 `ID` 欄位——這正是「`IsPrimaryKey()` 本身不是這個解析查詢依賴的不變式，『那個單一 Guid `ID`』才是」這句話的具體意思。三種分支：

| principal key 形狀 | 行為 |
|---|---|
| 單一 Guid `ID` 主鍵（canonical） | 完全比照修法前——照舊往下跑 Finding 7 的逐屬性迴圈 |
| 不是那個 canonical PK，且**沒有**任何 Guid 型別的 FK 屬性 | Finding 7 路徑**逐位元組不變**——`LogNonGuidAttachmentFk` + 排除出地圖（`:365`-`:370` 這段迴圈本身完全沒改一個字元，新檢查只是加在它前面、對這個分支永遠是 no-op） |
| 不是那個 canonical PK，且**至少一個** Guid 型別的 FK 屬性（涵蓋單一 Guid 替代鍵，以及任何含 Guid 成分的複合鍵） | **拋 `NotSupportedException`**（自 #1000 起，拋之前先記一行 `LogWarning`——見下面 #1000 該節）——模型設定錯誤，不是請求時的安全判斷，因此刻意不重用 `UnresolvableFileAttachmentReferenceException`（那個型別的語意是「posted 的 id 沒通過租戶範圍解析」，重用會讓設定錯誤看起來像被抓到的攻擊）。訊息點名實體、FK 屬性、principal key 的屬性，以及兩種補救：把 FK 改指回 `FileAttachment.ID`，或用既有的 opt-out `FileAttachmentSaveChangesGuard.Enabled = false`（`:105`，會跳過 `:681`-`:684`/`:769`-`:772` 的地圖建構本身） |

`_fkMapCache.GetOrAdd(model, BuildMap)`（`:279`-`:282`）不會快取一個會拋例外的 factory——`ConcurrentDictionary.GetOrAdd` 對同一個 key 每次呼叫都會重新呼叫 factory，直到某次成功寫入為止，所以每一次 `SaveChanges` 碰到這個模型都會重新拋出，不是「第一次拋、之後靜默通過」——這個行為**是自己動手確認的**，不是採信 issue 文字：讀 `ConcurrentDictionary<TKey,TValue>.GetOrAdd(TKey, Func<TKey,TValue>)` 的官方文件與行為契約（factory 拋例外時不會有任何值被寫入字典），並用下方 Test 1/Test 2 兩支測試各自獨立呼叫 `dc.SaveChanges()` 兩次驗證兩次都拋，確認結論。

**EF Core API 與不變式驗證（10.0.9，本 repo 釘住的版本，`Directory.Packages.props:51`）**：用 `ilspycmd` 反編譯 `~/.nuget/packages/microsoft.entityframeworkcore/10.0.9/lib/net10.0/Microsoft.EntityFrameworkCore.dll` 直接讀介面定義，不採信記憶或 issue 文字宣稱：`IReadOnlyForeignKey.PrincipalKey` 型別是 `IReadOnlyKey`（存在）；`IReadOnlyKey.IsPrimaryKey()` 是一個 default interface method，實作是 `this == DeclaringEntityType.FindPrimaryKey()`（存在）；`ForeignKey.AreCompatible`（`Microsoft.EntityFrameworkCore.Metadata.Internal.ForeignKey` 內部類別）在 `ArePropertyCountsEqual` 失敗時丟 `CoreStrings.ForeignKeyCountMismatch(...)`——確認 FK 屬性數量必須等於 principal key 屬性數量這個不變式在這個版本上成立，composite 分支因此不需要獨立處理。

**測試（TDD，RED 先於實作，訊息全部實際跑出來、不是預期猜測）**：`test/WalkingTec.Mvvm.Core.Test/VM/FileAttachmentSaveChangesGuardPrincipalKeyRejectionTests985.cs`，5 支新測試：

- `SaveChanges_GuidAlternateKeyPrincipal_ThrowsAtFirstUse`（單一 Guid 替代鍵）：修法前 RED——`Assert.ThrowsException failed. Expected exception type:<System.NotSupportedException>. Actual exception type:<WalkingTec.Mvvm.Core.Exceptions.UnresolvableFileAttachmentReferenceException>.`（posted 的隨機 Guid 沒有任何列的 `ID` 對得上，落在假拒絕那個症狀）。**這一支同時取代了 issue 原本要求的假允許與假拒絕兩種重現**——測試自己的註解說明原因：兩種症狀是同一個根因（`BuildMap` 信任任何 Guid 形狀的候選會被 `x.ID` 正確解析）的兩種不同表現；修法不是個別修補任一症狀，而是在 `BuildMap` 時就整個拒絕建圖，讓兩種症狀都**在解析查詢執行之前**就不可能發生——單獨用假允許或假拒絕的資料各自重現一次，只會證明同一個 `NotSupportedException` 從兩個不同呼叫點被拋出，對「這個形狀完全不可達」這件事沒有增加證據。刪除這一行會變紅：`if (!IsCanonicalFileAttachmentPrincipalKey(principalKey) && fk.Properties.Any(p => IsGuidTypedProperty(p.ClrType)))`。
- `SaveChanges_CompositeKeyWithGuidComponent_ThrowsAtFirstUse`（複合鍵、含一個 Guid 成分）：修法前 RED，訊息同上（`Actual exception type:<...UnresolvableFileAttachmentReferenceException>`），驗證「複合鍵免費涵蓋」的宣稱不只是推理、是實測。刪除同一行會變紅。
- `SaveChanges_NonGuidAlternateKeyPrincipal_StillWarnsAndSkips`（Finding 7 迴歸釘子）：重用既有的 `NonGuidFkContext824`/`DocumentByHash824` fixture（`FileAttachmentSaveChangesGuardNonGuidFkTests824.cs`），修法前後皆 PASS（這條路徑本來就沒被改動，不是「先紅後綠」）——這正是用來確認 Finding 7 分支逐位元組不變的**行為證據**：同一個 fixture、同一個斷言（不拋任何例外），在新檢查加入前後結果相同。刪除 `IsCanonicalFileAttachmentPrincipalKey` 判斷式本身、或刪除新檢查前的 `&&` 右側 `fk.Properties.Any(...)` 子句，都不會讓這支測試變紅（它本來就該一直是綠的）——這支測試的「刪哪一行會變紅」問法在此不適用，它的角色是「刪新程式碼後仍必須維持綠」的守門測試，不是偵測新程式碼存在的測試。
- `SaveChanges_InTreeCanonicalGuidIdPrincipal_MapUnchanged_NormalSavesStillWork`（正控組）：重用既有的 `BypassGuardContext824`/`ProductWithOptionalPhoto`（慣例辨識、principal key 就是 `FileAttachment.ID` 的 canonical 形狀），修法前後皆 PASS，同一台語意：同租戶合法寫入必須照常持久化。
- `SaveChanges_GuidAlternateKeyPrincipal_AttackerIdEqualsVictimAlternateKey_ThrowsAtFirstUse`（mutation gate 專用的假允許具體重現）：受害者 `FileAttachment`（`ID=V, AlternateGuid985=A, TENANT_VICTIM`）與攻擊者自己的 `FileAttachment`（`ID=A`——刻意等於受害者的 `AlternateGuid985`——`TENANT_ATTACKER`）都經由**不含這個問題 FK 的獨立 seed context**（`GuidAlternateKeySeedContext985`，避免 seed 本身就先觸發 `BuildMap` 的拒絕）建立；攻擊者的依賴端 FK 帶 `A`。修法前 RED——`Assert.ThrowsException failed. Expected exception type:<System.NotSupportedException> but no exception was thrown.`（不是「Actual exception type」，是「沒有任何例外」——這正是假允許的訊號：`SaveChanges` 靜默成功，代表偽造的參照被放行）。

套件數字：`test/WalkingTec.Mvvm.Core.Test` **5041 → 5046 passed, 0 failed**（base commit 5041 是在乾淨 worktree、任何修改前直接跑出來的，不是沿用舊記錄）。`dotnet build WalkingTec.Mvvm.sln`：1 個已知、跟本次修改無關的 `NETSDK1082`（`BlazorDemo.Client` 缺 `browser-wasm` runtime pack）——**對 base commit（`origin/dotnet10` tip `7c70018ac`）單獨重建同一個 sln 確認過同一個錯誤存在**，不是本次修法造成的新問題。

**Mutation gate**：`test/mutants/entries/fileattachmentguard985-principal-key-check-neutralize.json`，中和新檢查（`&& false`，compile-preserving，不是刪除，比照本文件其他 mutant 的既定慣例）。red_test 就是上面的假允許具體重現——`red_expected_assertion_patterns` 錨定在單行、穩定的片段 `Assert\.ThrowsException failed\. Expected exception type:<System\.NotSupportedException> but no exception was thrown\.`（避開 Python `re` 的 `.` 不吃換行這個本文件已經記過兩次的教訓，只取一行內的文字）。**green_test 與被 mutate 的程式碼可證明地解耦**：green_test 用的是 canonical 形狀（`ProductWithOptionalPhoto.PhotoId`），其唯一的 FileAttachment FK 在 `IsCanonicalFileAttachmentPrincipalKey(principalKey)` 會回傳 `true`；新檢查是 `if (!IsCanonicalFileAttachmentPrincipalKey(principalKey) && fk.Properties.Any(...))`——C# 的 `&&` 短路求值下，`!true` 已經是 `false`，右側（含被 mutate 的 `&& false` 那個子句）**根本不會被求值**，不是「資料剛好沒觸發」，是語言層級保證不可達。這正是 `.claude/rules/testing.md`「green_test 不得補償 production 邏輯本身」那條規則要求的等級——不只是「不同 fixture」，是可以指出程式語意上為什麼碰不到。實際驗證：`run_mutant.py --mutant fileattachmentguard985-principal-key-check-neutralize` 連續跑兩次，皆 `VERDICT: KILLED` / `GATE: PASS`，兩次執行後 `git status --porcelain -- src/WalkingTec.Mvvm.Core/FileAttachmentSaveChangesGuard.cs` 皆空輸出，確認目標檔案乾淨還原；`red_expected_assertion_patterns` 的實際文字先用手動 `git apply`＋`dotnet test -c Release`＋`git apply -R`（本 repo 對「先確認一次、再交給 `run_mutant.py` 正式跑」的既定方法）單獨捕捉過一次，跟正式 `run_mutant.py` 跑出來的結果一致。`python3 scripts/check-mutant-entries-parse.py`：75 個 mutant entry 全部通過（含本次新增這一個）。

**框架定位，誠實陳述（措辭已於 #1000 cross-vendor review 後修正——見下方 #1000 Part 1 該節的缺陷 5）**：這是對一個從未正確過的模型形狀做 fail-closed 硬化，**本 repo 出貨的每一個 in-tree 模型都不受影響**：每一個 `FileAttachment` 衍生型別或關聯都只用慣例辨識（隱含指向 `ID`），沒有任何地方用 `HasPrincipalKey` 指向替代鍵——正控組測試（上面第四支）直接證明這個 canonical 形狀的地圖內容與行為完全不受影響，本次修法**沒有任何 in-tree 模型能碰到新的 throw**。

**但對下游消費者，這是破壞性變更，不是「未支援設定的收斂」**：`HasPrincipalKey` 是合法的 EF Core API，本檔案自己在 #985 之前的類別文件註解就稱它是 "the one legal EF Core shape"；本 repo 從未有任何 analyzer、文件契約、或執行期驗證把它標記成「不支援」（逐項查證：無 Roslyn analyzer、無 `.editorconfig` 規則、CHANGELOG 與本文件在 #985 之前唯一提到它的地方只說它是「本庫目前沒有任何地方這樣用」與「一種合法的 EF Core 用法」，從未說「不支援」）。WTM 以 NuGet 套件發行，下游自行撰寫 EF model；`HasForeignKey(...).HasPrincipalKey(x => x.SomeGuidAlternateKey)` 指向一個 `FileAttachment`-principal 的 Guid 替代鍵，用任何合理判準都算「目前支援的設定」。這樣的下游模型在升級前寫入照常成功（只是可能被本節缺陷描述的假允許/假拒絕問題打到）；升級後第一次對該模型呼叫 `SaveChanges` 就會看到 `NotSupportedException`，此後**每一次** `SaveChanges` 都會重新拋出——從「寫入會成功」變成「整個 context 的每一次存檔都失敗」，是本專案 CLAUDE.md 定義下的破壞性變更。三條路可走：把 FK 改指回 `FileAttachment.ID`、用文件化的 `FileAttachmentSaveChangesGuard.Enabled = false` opt-out（代價見本文件與 CHANGELOG 對這個開關既有的完整說明——會重新打開整個 #824 系列關掉的洞，不是只影響這一個模型），或把這個模型形狀本身回報成獨立 issue、帶回上游討論是否要擴充解析查詢支援任意 principal key（本次修法刻意不做這件事，設計 gate 已裁定範圍）。

---

## `BuildPrincipalKeyRejectionException` 的拋出點沒有 log；opt-out 訊息與#985「框架定位」段落宣稱過寬；本節六個錨點過期（#1000 Part 1，2026-08-02）

**範圍聲明（先講不做什麼）**：#1000 分成兩部分；part (2)（Guid 型 FK 與非 Guid 型 FK 的 fail-closed/fail-open 不對稱）正在跨廠設計審查中，**不在本次變更範圍內**，本節不改動 guard 的任何拒絕/放行條件。本次變更只做兩件事：在既有的拋出點加一行 log（觀測性），以及修正文件的宣稱強度與六個過期的行號錨點（正確性）。**修改前後，這個 guard 接受或拒絕的寫入集合逐位元組相同**——見下方「行為不變如何自證」。

**缺陷 1（Task 1a，觀測性缺口）**：`BuildPrincipalKeyRejectionException`（`FileAttachmentSaveChangesGuard.cs:416`-`453`，#985 新增）與它的拋出點（`:346`）自 #985 上線起完全沒有 log。這個類別已經對其餘三個拒絕/失敗決策點都套用了 log-at-decision-point 的規則（`LogRejection`、`LogResolutionFailure`、`LogNonGuidAttachmentFk`；理由寫在 `_loggedRejections` 上方的 Finding 3 說明，`:126`-`:137` 附近）——#985 新增第四個決策點時沒有沿用同一條規則，於是這個決策點重演了 Finding 3 當年指出的同一個問題：本節上一個小節已指出 `_fkMapCache.GetOrAdd` 不快取會拋例外的 factory，代表**每一次 `SaveChanges` 碰到同一個誤設定的模型都會重新拋出**——沒有 log 意味著一個持續發生的設定錯誤，或攻擊者反覆嘗試觸發同一個模型錯誤，完全沒有伺服器端訊號可查。七個下游呼叫點各自的 catch（`BaseCRUDVM.cs:738`/`:798` 空 catch、`BaseBatchVM.cs:295`/`:470`/`:627`/`:768` 的 `SetExceptionMessage(e, null)` 直接丟棄、`_FrameworkController.cs:823` 空 catch 崩塌成 `Sys.EditFailed`）沒有一個能補救——修在呼叫端只堵住一個、留下六個，這正是這個類別本來就選擇「在決策點記」而不是「在呼叫端記」的原因，本次沿用同一個理由，不重新論證。`_FrameworkController.cs:823` 是七個裡最鋒利的例子：它上面緊接著一個專門給 `UnresolvableFileAttachmentReferenceException` 開的 `catch (UnresolvableFileAttachmentReferenceException)`（`:792`），那個 catch 自己的註解明講「A caught-and-generic 'Sys.EditFailed' here would be indistinguishable from any other edit failure... the same 'error state collapsing into a value the caller cannot tell apart from another' shape this codebase has hit before」——也就是這個檔案自己主張「不可以把這類拒絕壓扁成 `Sys.EditFailed`」。但 `BuildPrincipalKeyRejectionException` 拋的是 `NotSupportedException`，不是 `UnresolvableFileAttachmentReferenceException`（本節上一個小節已解釋原因：一個是模型設定錯誤、一個是請求時的資料判斷，刻意不同型別），所以完全繞過 `:792` 那個專屬 catch，直接落進 `:823` 那個它自己註解明講不該存在的壓扁路徑——這個檔案的另一部分已經有共識「這樣不對」，卻攔不到這個特定的例外型別，是「修呼叫端補不完」最直接的證據。

**修法 1**：新增 `_loggedPrincipalKeyRejections`（`ConcurrentDictionary<string, byte>`，`:152`-`:162`）與 `LogPrincipalKeyRejection`（`:259`-`:277`），呼叫點在 `BuildMap` 拋出之前（`:345`，緊接在 `throw BuildPrincipalKeyRejectionException(...)` 之前一行）。形狀、層級、節流紀律逐項比照既有三個 helper：`CoreProgram.GetLogger("FileAttachmentSaveChangesGuard")?.LogWarning(...)`（同一個 logger 名稱、同一個 `LogWarning` 層級——**沒有拉高成 `LogError`**，理由：這是與另外三個決策一致的分類，不是這個決策點特別更嚴重，一致性優先於逐案判斷）；結構化參數（`{EntityType}`、`{FkProperties}`、`{PrincipalType}`、`{PrincipalKeyProperties}`）而非字串內插；節流 key 是 `{entityType.ClrType.FullName}.{fkPropertyNames}`（比照 `LogNonGuidAttachmentFk` 用實體+屬性當 key，一個 process 一輩子只記一次，之後同一個欄位的重複拒絕不再記，但仍然照常拒絕）。`ClearLoggingThrottleForTests` 一併更新。`?.LogWarning` 的 null-conditional 代表就算沒有掛 logger，log 呼叫本身也不影響下一行的 `throw`——見下方「行為不變」。

**缺陷 2（Task 1b-1，訊息宣稱過寬）**：`BuildPrincipalKeyRejectionException` 的訊息（修法前 `:399`附近）說操作者可以「opt out of this guard entirely **for this context**」——這裡有兩個不準：(a) `Enabled` 是 `:105` 宣告的單一 `static` 欄位，**整個 process 共用**，不是「for this context」；(b) 訊息只講「看這個類別自己的文件」，沒有講清楚關掉之後具體重新打開的是**這個 check 本身要關掉的那個 #985 假允許**（受害者 `ID`＝攻擊者 `AlternateGuid` 那個碰撞），而不只是泛泛的「文件裡寫的東西」。

**修法 2**：訊息改為（`:447`-`:452`）：

> "...or opt out of this guard PROCESS-WIDE via FileAttachmentSaveChangesGuard.Enabled = false — that single static switch is shared by every context in this process, not scoped to this one, and setting it false re-enables the exact Guid-alternate-key false allow this check exists to close (plus the rest of the #824 write-path protection this class's own doc comment describes)."

明講 PROCESS-WIDE、明講不是 scoped to this one、明講重新打開的是「this check 存在的目的」（Guid 替代鍵假允許），同時仍然指向類別文件補齊 #824 全貌。既有測試 `FileAttachmentSaveChangesGuardPrincipalKeyRejectionTests985.cs` 的斷言只查字面子字串 `"FileAttachmentSaveChangesGuard.Enabled"`（不查 `"for this context"`），新訊息仍然包含這個子字串，該測試改前改後皆綠，不需要改測試斷言本身。

**缺陷 3（Task 1b-2，測試缺口）**：本文件（修法前）第 495 行宣稱「每一次 `SaveChanges` 碰到這個模型都會重新拋出」，但既有測試 `SaveChanges_GuidAlternateKeyPrincipal_ThrowsAtFirstUse` 只呼叫一次 `dc.SaveChanges()`，從未實測第二次呼叫的行為——宣稱缺實測支撐。**修法（加測試，不是弱化句子）**：在同一支測試、同一個 `dc` 實例上，緊接第一次 `Assert.ThrowsException<NotSupportedException>` 之後，加第二個 `Assert.ThrowsException<NotSupportedException>(() => dc.SaveChanges())`（該實體仍然是 `Added` 狀態——guard 自己的 throw 發生在 `base.SaveChanges()` 之前，change tracker 沒有被回滾），並斷言第二次的例外訊息一樣點名同一個實體。這就是「同一個 `dc` 實例、緊接著第一次之後」——不是換一個新的 `dc`（換新實例只會證明「每個模型第一次都拋」，證明不了「同一個模型的第二次也拋」，即 `GetOrAdd` 不快取 throwing factory 這個具體不變式）。

**測試（TDD）**：`test/WalkingTec.Mvvm.Core.Test/VM/FileAttachmentSaveChangesGuardPrincipalKeyRejectionTests985.cs` 這次改動：既有的 `SaveChanges_GuidAlternateKeyPrincipal_ThrowsAtFirstUse` 擴充第二次 `SaveChanges` 斷言（Task 1b-2）；新增 `SaveChanges_GuidAlternateKeyPrincipal_LogsWarningAtThrow`（Task 1a 的 log 斷言，CapturingLogger 技術複製自 `FileAttachmentSaveChangesGuardLoggingTests824.cs`，比照本 repo「不跨檔共用這類 test infra」的既定慣例）——斷言恰好一個 `LogLevel.Warning`、訊息點名實體、FK 屬性、principal key 屬性三者。套件數字：`test/WalkingTec.Mvvm.Core.Test` **5046 → 5047 passed, 0 failed**（新增淨 1 支測試方法；擴充既有方法不算新測試）。

**缺陷 4（Task 1b-3，本節自己的六個錨點過期）**：本節（#985，寫於 `a43dc997d`）點名的六個行號，全部是 #985 修法**當下**已經算錯、從未對過的行號，不是後續漂移——逐一驗證，其中兩個原本指到的位置落在完全無關的文字上：

| 錨點（修法前寫的） | 原本聲稱指向 | 實際指到的內容 | 正確錨點 |
|---|---|---|---|
| `:93` | `Enabled` 開關本身 | `Enabled` 屬性 XML doc 的 `/// <summary>` 開頭標籤（doc 內，不是宣告本身） | `:105`（`public static bool Enabled { get; set; } = true;`） |
| `:224`-`:227` | `_fkMapCache.GetOrAdd(model, BuildMap)` 那次呼叫 | **`LogNonGuidAttachmentFk` 的 `LogWarning` 訊息字串內文**（"...principal is {PrincipalType}...but its own CLR type is..."）——完全無關的一段 log 文字，剛好看起來像在講 guard 邏輯，最容易騙過只掃一眼的 reviewer | `:279`-`:282`（`GetOrBuildMap` 整個方法本體） |
| `:262` | `IsFileAttachmentPrincipal` 檢查通過之後 | `foreach (var fk in entityType.GetForeignKeys())`——在 `IsFileAttachmentPrincipal` 檢查**之前**，不是之後 | `:317`（`if (!DCExtension.IsFileAttachmentPrincipal(principalClrType))`） |
| `:281`-`:286` | Finding 7「逐位元組不變」的排除/加入迴圈本身 | **本檔案自己描述#985新檢查設計理由的註解文字**（"A design gate settled this as a model-configuration error to reject LOUDLY here..."）——內容在談新檢查的設計 gate，不是在談 Finding 7 那段舊迴圈；是本節六個錨點裡**最容易誤導**的一個：審稿人如果只是想確認「#985 有沒有動到 Finding 7 那段程式碼」，讀到這幾行提到「design gate」「reject LOUDLY」會覺得語意對得上，容易略過它其實指錯了程式碼位置 | `:365`-`:370`（`if (propertyClrType != typeof(Guid)) { LogNonGuidAttachmentFk(...); continue; } infos.Add(...)`） |
| `:524`-`:527` | `Guard` 方法裡 `if (!Enabled) { return; }` | `CollectCandidates` 的 Modified 分支中段（`continue; } var id = ExtractGuid(property.CurrentValue); if (id is not Guid modifiedId)`） | `:681`-`:684` |
| `:612`-`:615` | `GuardAsync` 方法裡 `if (!Enabled) { return; }` | `CollectCandidatesAsync` 尾端（`candidates.Add(...); } }`） | `:769`-`:772` |

修正方法：不是對舊行號套固定偏移量——六個錨點的偏移量彼此不同（`:93`→`:105` 是 +12，`:281`→`:365` 是 +84，`:524`→`:681` 是 +157），因為每個錨點與新插入程式碼的相對位置不同。每一個都是**重新從目前程式碼逐字比對**：先假設某段程式碼／某個方法簽章是原文想指的東西，用 `grep -n`/`sed -n` 在目前檔案裡找到它實際所在的行號，再回頭核對原文的描述（「Enabled 開關」「GetOrAdd 呼叫」「IsFileAttachmentPrincipal 通過後」……）跟找到的程式碼是否真的對得上——不是「行號±N」的機械平移。修法本身（本節上方三個小節）連帶更新這六處引用，改成上表右欄的新錨點。

**缺陷 5（後續加入，同一個 commit：#985 框架定位段落宣稱過寬）**：#985 該節「框架定位，誠實陳述」段落原文說「不是對任何目前支援的設定做行為變更」——第二半句（「沒有任何 in-tree 模型能碰到新的 throw」，引正控組測試佐證）精準、正確，問題在「不是對任何目前支援的設定做行為變更」這半句：它讀起來是在對「支援的設定」這個大集合做宣稱，不只是這個 repo 自己的 in-tree 模型。**自己動手查證，不採信描述**：(a) `#824` 落地那個版本（`3887d7b11`，#985 之前）的 `FileAttachmentSaveChangesGuard.cs` 類別文件註解明講 `HasPrincipalKey(x => x.SomeAlternateKey)` 是「the one legal EF Core shape」——`git show 3887d7b11:src/WalkingTec.Mvvm.Core/FileAttachmentSaveChangesGuard.cs | grep -n "legal EF Core"` 可重現，命中兩處（類別文件註解與 `LogNonGuidAttachmentFk` 上方的實作註解）——這個框架自己的原始碼把它稱為合法用法，不是不支援的用法；(b) `git ls-tree -r aad773391 --name-only | grep -i "analyzer\|editorconfig\|Roslyn"` 只有 `.editorconfig`，內容沒有任何一條規則提到 `FileAttachment`/`PrincipalKey`；(c) `git show 3887d7b11:CHANGELOG.md` 對 `HasPrincipalKey` 唯一的敘述是「本庫目前沒有任何地方這樣用」（"nothing in this repository uses today"）與「非標準…設定」（"non-standard... configuration"）——用的是「這個 repo 沒這樣用」，從未寫過「不支援」或任何禁止語言。三者合起來：WTM 以 NuGet 套件發行，下游自行撰寫 EF model，`HasPrincipalKey` 是 EF Core 官方支援的合法 API，本框架從未透過 analyzer、文件契約，或執行期驗證宣告它不支援——一個下游 context 用這個形狀設定 `FileAttachment`-principal 的 Guid 替代鍵，升級前 `SaveChanges` 成功（只是可能被本節缺陷描述的假允許/假拒絕問題打到），升級後同一個 context 的**每一次** `SaveChanges` 都會拋 `NotSupportedException`——這是本專案 CLAUDE.md 定義下的破壞性變更，不是「未支援設定的收斂」。

**修法 5**：改寫該段落，保留「沒有任何 in-tree 模型能碰到新的 throw」（已驗證、有價值，不刪），拿掉「不是對任何目前支援的設定做行為變更」這個過寬宣稱，改寫成明講兩件事：對本 repo 自己的 in-tree 模型沒有行為變更；對下游用了 `HasPrincipalKey` 這個合法 EF Core 形狀的消費者，這是破壞性變更（從「寫入成功」變成「每次存檔都失敗」）。**刻意不擴大範圍**：不新增任何行為變更、不加新的 guard、不做任何 migration tooling——只修正這段話宣稱的範圍。

**CHANGELOG 的同一個過寬宣稱 —— 已在 PR #991 修正（本節原記為「供決策，未動手改」，該狀態已不成立）**：`CHANGELOG.md` 現有的 #985 條目（`### Security — FileAttachmentSaveChangesGuard.BuildMap now rejects...`，PR #991 的內容）逐字使用同一句「not a behaviour change to any supported configuration」——`grep -n "not a behaviour change to" CHANGELOG.md` 命中該條目。這一條**沒有**在本次修改——它是 PR #991 的內容，不是這次 stacked 分支自己加的。**後續**：協調者裁定留在原位由 #991 自己修，已於 commit `cf78cd15e` 完成——退回該句、改標 BREAKING、補遷移路徑，且不再把 `Enabled = false` 列為遷移步驟。**兩份文件現在說同一件誠實的話**——這一點很重要，因為「CHANGELOG 不得超過 production-readiness」是**相對**判準，兩份同錯時它會空洞地通過。

**行為不變如何自證（不只是宣稱）**：
1. 新增的 log 呼叫（`LogPrincipalKeyRejection(...)`）在 `throw BuildPrincipalKeyRejectionException(...)` **之前**一行，兩者之間沒有任何條件分支——`throw` 在 log 呼叫之後無條件執行，log 呼叫本身是 `CoreProgram.GetLogger(...)?.LogWarning(...)`，`?.` 保證就算沒有掛 logger（`GetLogger` 回傳 `null`）也不會拋出或跳過下一行；不存在任何路徑讓加了這行 log 之後 `throw` 不執行，或執行了但沒有真的拒絕寫入。
2. 訊息文字改動（Task 1b-1）只動 `NotSupportedException.Message` 的字串內容，不動判斷式、不動任何分支、不動回傳值型別——`BuildPrincipalKeyRejectionException` 的呼叫端仍然拿到同一個型別的例外，仍然在同一個 `if` 條件為真時才被呼叫。
3. `FileAttachmentSaveChangesGuardPrincipalKeyRejectionTests985.cs` 目前 6 支測試（5 支沿用自 #985，其中 1 支本次擴充第二次 `SaveChanges` 斷言；1 支本次新增）與同一目錄下所有其他 #824/#985 測試檔（`FileAttachmentSaveChangesGuardBypassPathTests824.cs`、`FileAttachmentSaveChangesGuardLoggingTests824.cs`、`FileAttachmentSaveChangesGuardNonGuidFkTests824.cs`、`FileAttachmentSaveChangesGuardTpcSameUnitOfWorkTests824.cs`、`FileAttachmentSaveChangesGuardInvocationCountTests824.cs`）本次修法前後跑起來逐支結果相同（全綠，見上方套件數字）——特別是 `FileAttachmentSaveChangesGuardNonGuidFkTests824.cs`（本次刻意未觸碰）與正控組測試（canonical Guid ID 形狀、非 Guid 替代鍵形狀）都是這個 guard 判斷「接受」還是「拒絕」某個寫入的行為證據，不是只看程式碼審閱。
4. `test/WalkingTec.Mvvm.Core.Test` 整體 5046 → 5047（僅淨增本次新增的 1 支測試方法），沒有任何既有測試從 PASS 變 FAIL 或反過來。
5. 缺陷 5／修法 5（框架定位段落改寫）純粹是 `docs/production-readiness.md` 的文字——不動 `.cs`、不動任何測試檔，`git diff --stat` 可證：改動只落在 `docs/production-readiness.md`。

**Mutation gate：刻意不加，理由陳述而非省略**。這個類別另外三個既有 log helper（`LogRejection`、`LogResolutionFailure`、`LogNonGuidAttachmentFk`）本身都沒有專屬 mutant——`test/mutants/entries/` 裡跟這個檔案有關的兩個 mutant（`fileattachmentguard824-reject-condition-neutralize`、`fileattachmentguard985-principal-key-check-neutralize`）都是中和**拒絕條件本身**（`!outcome.ResolvedIds.Contains(...)`/`IsCanonicalFileAttachmentPrincipalKey(...) && ...`），不是中和任何 log 呼叫——這是本檔案既有、一致的分類方式：log 是觀測性，不是安全控制。本次新增的 `LogPrincipalKeyRejection` 呼叫同樣符合這個分類：把它整行刪掉，`throw BuildPrincipalKeyRejectionException(...)` 仍然無條件執行（見上方第 1 點），guard 拒絕的寫入集合不變一個位元——刪掉這行 log 不會讓任何一支既有的行為測試（不是本次新加的 log 斷言測試）從紅變綠或從綠變紅，唯一會變紅的是本次新增的 `SaveChanges_GuidAlternateKeyPrincipal_LogsWarningAtThrow`（一支直接斷言 log 內容的測試，不是 mutation gate 的 `run_mutant.py` 機制）。因此依本文件既有慣例，不另外登記 mutant entry。`python3 scripts/check-mutant-entries-parse.py`：75 個 mutant entry 全部通過（本次未新增任何 mutant entry，數字不變）。

**Stacked 分支破壞了下層自己的 mutation-gate 補丁——已修正，並記下這一類問題本身**：本節在 `LogPrincipalKeyRejection(...)` 呼叫（`:345`）與其後的 `throw BuildPrincipalKeyRejectionException(...)`（`:346`）之間，插進了四行新註解＋一行 log 呼叫；這段插入落在 `fileattachmentguard985-principal-key-check-neutralize.patch`（#985 自己用來釘住其修法的 mutation-gate 補丁）的 fixed context 範圍內，導致該補丁對本分支 tip 的 `git apply --check` 失敗（`error: patch failed: src/WalkingTec.Mvvm.Core/FileAttachmentSaveChangesGuard.cs:292`）——`run_mutant.py` 的 `apply_patch()` 就是跑這行 `git apply --check`，失敗會拋 `GateError(VERDICT_PATCH_DID_NOT_APPLY, ...)`，而 `PASSING_VERDICTS` 不含這個結果，於是 `mutation-gate`（`dotnet10` 的必過 status check）整條紅。**已修正**：補丁重新對本分支目前的程式碼產生（`git diff` 對著已手動套用同一個 `&& false` 中和的檔案跑出來），中和的條件本身逐字不變——`!IsCanonicalFileAttachmentPrincipalKey(principalKey) && fk.Properties.Any(p => IsGuidTypedProperty(p.ClrType)) && false`，`// MUTANT test/mutants ...` 註解也原樣保留——只有補丁的 context 行跟著新插入的 log 呼叫往下移了幾行。條目 JSON 的 `target_symbol` 同時也是過期的（寫著「line ~295-296」，實際條件現在在 `:338`-`:339`），一併訂正；逐項查過這個條目 JSON 其餘欄位，沒有其他行號類的宣稱。重新用 `python3 test/mutants/run_mutant.py --mutant fileattachmentguard985-principal-key-check-neutralize` 驗證，得到 `VERDICT: KILLED` / `GATE: PASS`。**這件事本身值得記一筆**：`test/mutants/patches/` 下每一個補丁都是對某個檔案在某個歷史時間點的固定 context 快照；任何後續分支只要改動同一個檔案裡落在補丁 context 範圍內的程式碼（哪怕只是插進註解與一行 log，不動任何判斷式），就可能讓補丁的 `git apply --check` 失效，而這個失效**沒有被任一邊的 PR 自己的 CI 抓到**——#1000 Part 1 這個分支的 CI 只看得到自己這棵樹（Gitea PR CI checkout 的是 head，不是 merge ref），從未套用過 #985 的補丁；#985 自己的 CI 早在 #1000 存在前就跑過、通過。是這次針對兩者疊加後狀態的 heterogeneous review 才發現的，不是任何自動化擋下來的。**誠實陳述現況，不誇大**：這次是人工在派工前逐一 `git apply --check` 過 `test/mutants/patches/` 下全部 65 個補丁才抓到（結果：64 個原本就過、這 1 個原本失敗、修正後 65 個全過）——目前沒有任何機制強制這件事，下一個 stacked 分支一樣可能重演同一個問題，這一段只是記錄問題與這次的修法，不是宣稱「這類問題以後不會再發生」。

**Not verified this session**：跟本文件上方 #943/#944 條目相同的 hard constraint——本次工作階段禁止任何 Gitea/GitHub API 呼叫、禁止開 PR，因此尚未在真正的 Gitea Actions CI 上跑過；本機驗證只到 `dotnet build`/`dotnet test`/`check-mutant-entries-parse.py` 這三層。

---

## DataTableTagHelper 未包裹 IIFE：GridAction.OnClickFunc 為函式字面值時整個 `<script>` 區塊解析失敗（#965，2026-08-02）

> **本項引入的已知迴歸——見 #1034。** `({expr})({args})` 的包裹**終結 optional chain 的短路傳播**：
> `*Func` 值只要含 `?.`，行為就改變。`handlers?.onChange(data)` 在 `handlers` 為 undefined 時安全短路、
> 什麼都不做；而發射出的 `(handlers?.onChange)(data)` 會先把 `undefined` 求值出來、再把它當函式呼叫，
> 拋 `TypeError` 並中止整個 callback。短路只在**同一條鏈內**傳播，加上括號就結束了那條鏈。
> 這是以**執行**兩種形狀驗證的，不是以閱讀驗證。影響 part (A)、part (B) 與 #965 合計 **20 個**包裹發射站點。
> **本項的測試沒有抓到它**，因為那些測試用真的 JS parser **解析**發射結果並斷言其文字形狀——
> 而 `(handlers?.onChange)(data)` 在語法上完全合法，只有執行才看得出差異。這是「解析通過 ≠ 行為正確」的實例。
> 沒有便宜的正確修法：`(expr)?.(args)` 會把「識別字打錯」從大聲拋錯降級為靜默無事；
> 而「文字含 `?.` 就不包裹」正是 part (B) 存在要消滅的「用字串猜 JS 文法」。修法設計在 #1034 追蹤。
> 嚴重度 **[med]**：需要開發者在 `*Func` 值裡寫 `?.`，合法但不常見——**但失效模式從「靜默無事」
> 變成「拋錯中止 callback」，方向是變差的。**
>
> **更正 2026-08-03（#1034）**：上面這句話對它指名的「做法」仍然正確——用字串猜 JS 文法（`Contains("?.")`）
> 依然被否決，理由不變：`(v)=>a?.b` 這類 arrow body 含 `?.` 卻絕不能 direct-append。**被取代的是**
> 「這個缺陷類別無解」這個讀法——本文件下方新增的「#1034」章節有封閉語言分類器修法（命中集合可證明是合法
> ES2020 `OptionalMemberExpression` 鏈，未命中一律退回今天的 wrap，byte-identical），已隨 `10.22.1` 出貨。

**背景**：這個缺陷本身是 #898/#905（見上方「E2E 測試可靠度修正」條目）改寫 TC-29 時發現、但當時判斷超出「只改 test/e2e」範圍而刻意不修、只記成 KNOWN-GAP 的既有缺陷；#965 是授權修這個缺陷本身的 issue。

### 1. 缺陷與修法

`src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.cs`（修前）第 1284 行，`AddSubButton` 方法內：

```csharp
actionScript = $"{item.OnClickFunc}(ids,ff.GetSelectionData('{Id}'));";
```

這一行把 `GridAction.OnClickFunc` 這個字串原封不動接在呼叫括號前面。`src/WalkingTec.Mvvm.Core/Grid/GridActionExtension.Legacy.cs`（`SetOnClickScript`）的 XML doc 記載的預期形狀是一個具名、頁面全域函式的**識別字**（`function test(ids,datas){}`，呼叫端寫 `test(ids,data)`），但程式碼層級沒有任何東西強制這件事。實際上有兩處呼叫端把 `OnClickFunc`設成一段**行內匿名函式字面值**，而非識別字：

- `src/WalkingTec.Mvvm.Etl/ViewModels/EtlJobListVM.cs:94`（「執行記錄」動作）：
  ```csharp
  OnClickFunc = @"function(ids,data){var id=ids&&ids.length>0?ids[0]:'';ff.OpenDialog('/_EtlRunLog/Index?jobId='+id,null,'執行記錄',900,null,undefined,false);}"
  ```
- `src/WalkingTec.Mvvm.WorkFlow/ViewModels/ProcessDefinitionListVM.cs:73,82`（「设计器」「版本历程」兩個動作，同形狀）

產生的 JS 是 `function(ids,data){...}(ids,ff.GetSelectionData('...'));`——這**不只是「IIFE 少包一層括號」的表面問題**：一個以裸 `function` 關鍵字開頭的敘述式位置永遠會被解析成匿名 `FunctionDeclaration`，而 `FunctionDeclaration` 要求具名，所以光是 `function(ids,data){...}` 這幾個字元本身在敘述式位置就已經是語法錯誤，不需要看後面接了什麼。整個包住它的 `<script>` 區塊因此**整段解析失敗**，殃及同一區塊裡真正的 `table.render()` 呼叫（`BuildTableOptionsScript` 把 `wtToolBarFunc_{Id}` 分派函式與 `layui.use(['table'], function(){...})` 選項區塊寫在**同一個** `<script>` 標籤內）——`/_EtlJob/Index` 的整個 grid 因此永遠不會渲染成功，不限於「執行記錄」這個動作本身。

**修法**（同一行，`src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.cs`）：

```csharp
actionScript = $"({item.OnClickFunc})(ids,ff.GetSelectionData('{Id}'));";
```

把整個 `item.OnClickFunc` 表達式包進一層括號。對函式字面值，這修成合法的 `(function(...){...})(...)` IIFE。對已經合法的其他形狀（裸識別字、`obj.method` 這種成員運算式、`obj.method()` 這種呼叫運算式）是 **no-op**——JavaScript 的分組運算子（括號）不會剝離其內部運算式的 Reference（`this` 綁定）：`(obj.method)(args)` 呼叫時 `this` 仍是 `obj`，跟 `obj.method(args)` 完全等價；只有逗號運算子（`(0, obj.method)(args)`）才會剝離。這點在改動前已用既有測試證實（見下方「測試」小節）。

### 2. 全樹重新推導受影響範圍（不採信 issue 文字或既有 framing）

**指令與原始輸出**（在乾淨的 `fix/965-datatable-unwrapped-iife` worktree、修改任何檔案之前執行）：

```
$ grep -rn "OnClickFunc" --include="*.cs" --include="*.cshtml" --include="*.tt" --include="*.txt" .
```

真正的（非測試檔案）命中只有四處，全部落在同一組型別上：

```
src/WalkingTec.Mvvm.Etl/ViewModels/EtlJobListVM.cs:94:                OnClickFunc = @"function(ids,data){var id=ids&&ids.length>0?ids[0]:'';ff.OpenDialog('/_EtlRunLog/Index?jobId='+id,null,'執行記錄',900,null,undefined,false);}"
src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.cs:1229:                    if (string.IsNullOrEmpty(item.OnClickFunc))
src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.cs:1284:                        actionScript = $"{item.OnClickFunc}(ids,ff.GetSelectionData('{Id}'));";
src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.Island.cs:172,175,180,188,190,192:  （FindNonIdentifierOnClickFunc／island 判斷邏輯，見下方）
src/WalkingTec.Mvvm.WorkFlow/ViewModels/ProcessDefinitionListVM.cs:73,82:               OnClickFunc      = @"function(ids,data){var code=data&&data.Code?data.Code:'';if(code){window.open(...)}}"（兩處，同形狀）
src/WalkingTec.Mvvm.Core/Grid/GridAction.cs:99:        public string? OnClickFunc { get; set; }
src/WalkingTec.Mvvm.Core/Grid/GridActionExtension.Legacy.cs:166:            self.OnClickFunc = onClickScript;
```

即 `GridAction.OnClickFunc` 只有一個型別宣告、一個設值 extension method、一個真正拼接進 JS 字串的產生端（`DataTableTagHelper.cs:1284`），以及**兩個**呼叫端把它設成函式字面值（EtlJobListVM、ProcessDefinitionListVM）。

```
$ find src/WalkingTec.Mvvm.Mvc/GeneratorFiles -type f | sort
```
（列出 CodeGen 範本，共 42 個 `.txt` 檔，`Mvc/` `Spa/Blazor|React|Vue|Vue3/` 各子目錄）——

```
$ grep -rln "OnClickFunc" src/WalkingTec.Mvvm.Mvc/GeneratorFiles/
$ grep -rln 'function(' src/WalkingTec.Mvvm.Mvc/GeneratorFiles/
```
兩個指令**都是空輸出**——沒有任何 CodeGen 範本產生 `OnClickFunc` 或任何 `function(` 字面值。腳手架出來的下游程式碼不會複製這個缺陷形狀。

```
$ grep -rnE 'function[[:space:]]*\([^)]*\)[[:space:]]*\{.*\}\s*\(' --include="*.cs" --include="*.cshtml" --include="*.txt" .
```
排除 `demo/**/wwwroot/*.js`（第三方 vendor JS，如 jquery.min.js，本身就是壓縮過的合法 IIFE，不是本 repo 產生的程式碼）後，命中的都不是本缺陷形狀：`SliderTagHelper.cs`/`DateTimeTagHelper.cs` 的 `function(value){{return {OnTipsFunc}(value,sliderIns);}}` 是物件屬性值（`setTips: function(value){...}`），不是敘述式位置的裸呼叫；`src/WalkingTec.Mvvm.Mvc/Views/_DashboardPage/{Index,Render,Designer}.cshtml` 的 `(function () {...}());` 三處**本來就已經正確包裹**（`(function(){` 開頭），是同一種 IIFE 手法的另一種合法寫法，不是本缺陷。

**識別字保護的架構性理由**：`DataTableTagHelper.Island.cs`（#470 opt-in island render）的 `DetermineGridIslandDecision`/`FindNonIdentifierOnClickFunc` 會把任何**非識別字**的 `GridAction.OnClickFunc`（含函式字面值）強制導向 legacy（非 island）渲染路徑（`GridAction.OnClickFunc '{badOnClick}' is not a plain identifier`），而 island 路徑對 `OnClickFunc` 唯一的處理（`DataTableTagHelper.Island.cs:685` `descriptor.OnClickFn = item.OnClickFunc;`）本身有註解明講「保證是裸識別字——非識別字在到這裡之前就已經被強制導向 legacy 路徑」，所以 island 路徑本身不可能重現這個缺陷。且 `DetermineGridIslandDecision` 第一條判斷就是 `WtmUIOptionsHolder.Options.UseSelectIslandRender` 這個旗標，**預設關閉**（`WtmUIOptions.cs:151` `public bool UseSelectIslandRender { get; set; } = false;`）——代表在預設設定下（絕大多數部署，含 demo 本身），legacy 路徑本來就是唯一路徑，不需要 island 判斷介入就會命中本缺陷。

**再審視時發現、更正原稿的一句過度宣稱**：原稿在這裡宣稱 `DataTableTagHelper.cs` 內其餘結構相似的 `{Func}(...)` 直接串接（`DoneFunc`/`ChangeFunc`/`ReadyFunc`/`CheckedFunc`／`OnTipsFunc`）「全部只被設成識別字或走 `xxxIsIdentifier ? xxxFuncName : null` 這種自我驗證守衛」——重新讀程式碼後這句話**不成立**。那個 `xxxIsIdentifier ? xxxFuncName : null` 守衛（`ComboBoxTagHelper.cs`/`TransferTagHelper.cs`/`TreeTagHelper.cs`/`TextBoxTagHelper.cs`）只保護寫進 opt-in island JSON descriptor 的那個值，從未保護這些屬性同時餵給的、legacy（一律執行、不受旗標控制）直接串接路徑——例如 `TransferTagHelper.cs` 自己的 legacy 分支（約行 341）用未經任何識別字檢查的原始 `ChangeFunc` 值組出 `,onchange: function(data,index){{defaultFunc(data,index,transferIns); {ChangeFunc}(data, index,transferIns); }}`，如果 `ChangeFunc` 是函式字面值，這裡會產生跟 `GridAction.OnClickFunc` 完全同一類的未包裹 IIFE 語法錯誤。`SliderTagHelper.cs`/`DateTimeTagHelper.cs`/`DataTableTagHelper.cs` 自己的 `DoneFunc` 同樣是無守衛的直接串接。這些姊妹站點今天沒有出過同樣的缺陷，**真正的原因是目前沒有任何實際呼叫端傳入非識別字值，不是因為有任何機制擋著**——重新掃描這個 commit 點所有真實（非測試）`.cshtml` 用法：`grep -rnoE '(change|done|ready|checked|ontips)-func="[^"]*"' demo/ src/`，命中全部是裸識別字或簡單呼叫運算式（`MenuTypeChange(data)`、`DbTypeChange`、`gridCheckedFunc`、`abc`、`aaa` 等），沒有一個是函式字面值。這是這些姊妹站點今天沒事的經驗事實，不是結構性保證——標記為本次修復範圍外的潛在風險類別，留給未來一輪掃描/issue 處理，不在 #965 這次一併擴大修。

**結論**：受影響的產生端只有一處（`DataTableTagHelper.cs:1284`），單一修法即可涵蓋兩個呼叫端。`EtlJobListVM` 的「執行記錄」動作**今天透過真實導覽路徑可達**（`/_EtlJob/Index`，見下方 e2e 小節）；`ProcessDefinitionListVM` 的兩個動作在這個 commit 點**未連接到任何 controller/view**——重新、更徹底地驗證（不只是原本那一條 grep）：`grep -rn "ProcessDefinitionListVM" --include="*.cs" --include="*.cshtml" .` 除了 VM 檔本身與 `test/WalkingTec.Mvvm.WorkFlow.Test/NotifierTests.cs` 之外沒有其他命中；`src/WalkingTec.Mvvm.WorkFlow/Controllers/` 下沒有任何檔案引用它；額外檢查是否存在某種泛型/反射式自動路由機制能繞過具名 controller 觸及它（`grep -rln "GetTypes().*ListVM\|Assembly.*ListVM\|typeof(BasePagedListVM" src/`）——命中的三個檔案（`_DashboardDesignerController.cs`／`_DashboardController.cs`／`AnalysisVmRegistry.cs`）皆屬 Dashboard／Analysis 這兩個不相關功能，沒有任何能觸及 WorkFlow ListVM 的通用機制。`docs/workflow.md` §8 確實展示了一段以 `WfProcessDefinitionController`（繼承 `BaseController`）呼叫 `CreateVM<ProcessDefinitionListVM>()` 的程式碼，但那是文件給下游整合者看的**範例**（"In your area controller (inherit BaseController):"），`find . -iname "*WfProcessDefinition*"` 在整棵樹裡找不到這個類別的任何實作檔——與本 repo 自己既有的 `CHANGELOG.md`「Dead-link repair」條目（`ProcessDefinitionListVM` 的 Details/Versions 動作原本連到的正是這個「已驗證不存在」的 `_WfProcessDefinition` controller）互相印證。因此目前無法透過導覽觸發，但原始碼裡確實帶著同一顆未爆彈——同一個生成器修法讓它在未來被接上任何 view 時就已經是修好的狀態，不需要屆時另外記得處理。

### 3. 測試機制：實際解析 emitted script，而非字串比對

**為什麼不是字串比對**：本 repo 自己的 `DataTableByteIdentityTests`（`test/WalkingTec.Mvvm.Core.Test/TagHelpers/DataTableByteIdentityTests.cs` + `.Fixtures.cs`）正是這個確切程式碼路徑（`AddSubButton` 的 `actionScript` 那一行）的 byte-identity 回歸測試——它的 fixture 把 `OnClickFunc` 設成裸識別字 `"myGridOnClickHandler"`（`DataTableByteIdentityTests.Vm.cs:209`），**在這個缺陷存在期間全程綠燈，精確原因是它的 fixture 從未真正觸發過這個缺陷**——即使觸發了，golden 字串一樣會把當下 emit 出來的內容（不論合法與否）原封不動凍結進期望值；字串比對測的是「跟上次一樣」，不是「這串文字是不是合法 JS」，兩者是不同的性質。

**環境限制**：`.github/workflows/ci-build.yml`（本 repo 唯一跑 `dotnet test` 的 job，`build-and-test`）只有 `actions/setup-dotnet@v5`，**沒有 `actions/setup-node`**——`setup-node` 只出現在完全不同、跑 Jest 的 `js-test` job（`cd test/WalkingTec.Mvvm.Js.Tests && npm test`）裡。這個 repo 是本機 Gitea self-hosted act_runner，不是 GitHub-hosted image，不能假設兩個 job 共用同一份工具鏈。因此新測試**不能**依賴 `node` 在 `.NET test job` 的 PATH 上。

**選擇 Acornima 而非其他方案**：
- **不用 `node` 子行程**：上述環境限制直接排除。若堅持要跑 `node`，必須在工具缺席時給出清楚標記的獨立失敗，而不是靜默通過或給出不明所以的例外——這個成本與風險都比選一個純 .NET 套件高。
- **不手寫正規表達式/自製迷你解析器判斷語法**：JS 語法（尤其函式表達式 vs 宣告式的敘述式位置規則）沒有簡單、可靠的正規表達式判準；手寫解析器等於自己重新發明一個不完整、可能有盲點的 parser，且失去「這是一個真正的、被廣泛驗證過的 JS 解析器」這個可信度。
- **選 Acornima（NuGet, 1.6.2, BSD-3-Clause）**：pure .NET、無外部行程依賴、Test262-complete（作者宣稱通過完整 ECMAScript 2026 test262 套件）、`net8.0`/`netstandard2.0/2.1`/`net462` 皆有 target（`net8.0` 組件在 `net10.0` 測試專案下可直接載入，.NET 同系列前向相容，這是標準行為，不需要額外處理）。`new Parser().ParseScript(code)` 對合法輸入回傳 AST，對不合法輸入拋出 `Acornima.SyntaxErrorException`（`ParseErrorException` 的具體子類別之一）。曾經是同類套件的 Esprima.NET 已由同一作者的 Acornima 取代（README 明講兩者關係：acornjs + Esprima.NET 的融合），選現行維護中的套件而非停止維護的舊選項。

**新增依賴，明確標註（依 CLAUDE.md 對依賴新增要嚴格審查的政策）**：`Acornima 1.6.2` 加進 `Directory.Packages.props`（「Test packages」區）與 `test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj`（唯一引用它的專案）。**僅供測試使用，不被任何出貨用的 `WalkingTec.Mvvm.*` 套件引用**——`dotnet restore`/`dotnet build` 確認過只有這一個 `.csproj` 拉它。

**測試本身**（`test/WalkingTec.Mvvm.Core.Test/TagHelpers/DataTableTagHelperUnwrappedIife965Tests.cs`）：用一個 `GridAction`（`OnClickFunc` 與 `EtlJobListVM` 的「執行記錄」逐字相同）驅動真正的 `DataTableTagHelper.Process()`，從 `output.PostElement.GetContent()` 取出**第一個裸 `<script>...</script>` 區塊**（刻意不匹配 `<script type="text/html" ...>`——那是 LayUI 範本，是 HTML 不是 JS；`BuildTableOptionsScript` 為這個 fixture 只會產生一個裸 `<script>` 標籤，就是含 `wtToolBarFunc_*` 分派器＋`table.render()` 的那一個），先用 `StringAssert.Contains` 確認抓到的區塊確實含 `OpenDialog`／`執行記錄`（避免抽取器抓到空區塊而讓測試「無條件通過」），再交給 `new Parser().ParseScript(...)`，`catch (ParseErrorException)` 時 `Assert.Fail` 並把解析錯誤與整段 emitted script 印出來。

### 4. RED-before-fix / GREEN-after，逐字擷取

**RED**（`git checkout -- src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.cs` 把修法還原回原始未修狀態，重新 build 後跑同一支測試）：

```
Failed ToolbarActionScript_WithFunctionLiteralOnClickFunc_ParsesAsValidJavaScript [118 ms]
  Error Message:
   Assert.Fail failed. DataTableTagHelper emitted a <script> block that is not valid JavaScript
   (Issue #965 — likely an unwrapped IIFE from a function-literal GridAction.OnClickFunc).
   Parser error: Unexpected token '(' (10:9)
--- emitted script ---
...
var isPost = false;
var tempUrl = '',whereStr=null;
function(ids,data){var id=ids&&ids.length>0?ids[0]:'';ff.OpenDialog('/_EtlRunLog/Index?jobId='+id,null,'執行記錄',900,null,undefined,false);}(ids,ff.GetSelectionData('wtTable_965'));};break;
default:break;}
...
```

第 10 行第 9 個字元正是 `function(ids,data){...}` 之後緊接的那個呼叫括號——與程式碼分析預期的失敗位置完全吻合。

**GREEN**（把修法（`cp` 備份檔）還原回來，重新 build 後跑同一支測試）：

```
Passed ToolbarActionScript_WithFunctionLiteralOnClickFunc_ParsesAsValidJavaScript [142 ms]

Test Run Successful.
Total tests: 1
     Passed: 1
```

### 5. TC-29（e2e）：KNOWN-GAP 解除

**修前**：`test/e2e/wtm_e2e_tests.py`（改寫自 #898）的 `tc_29_etl_management` 對 `/_EtlRunLog/Index` 做完整斷言（grid `.layui-table-body` 存在、`Searcher.Result`/`Searcher.Trigger` 篩選欄位存在），但對 `/_EtlJob/Index`：因為透過 `open_grid_via_direct_tab()`（等待 `window.layui.table.cache` 填入才返回）導覽會逾時（table.cache 因為本缺陷永遠不會被填入），改用手動 `page.evaluate` 觸發導覽＋固定 `wait_for_timeout(1000)` 繞過那個等待；只斷言純 HTML 的 `Searcher.Name` 欄位存在（不需要 JS 就會出現在 DOM 中），對 grid（`.layui-table-body`）與兩個 xmSelect 篩選欄位（`Searcher.Status`/`Searcher.SourceDbType`）**只印出 `[KNOWN-GAP]` 訊息，完全不斷言**。docstring 明確記載根因指到 `DataTableTagHelper.cs:1284`。

**修後**：`/_EtlJob/Index` 改用與 `/_EtlRunLog/Index` 相同的 `open_grid_via_direct_tab()`（生成器修好後 `table.cache` 應該會正常填入，該 helper 因此不再逾時），並把三項斷言全部轉成正面斷言：grid `.layui-table-body` count > 0、`Searcher.Name`／`Searcher.Status`／`Searcher.SourceDbType` 三個篩選欄位 count > 0，取代原本的「只記錄、不斷言」與 KNOWN-GAP 印出。docstring 更新為記載 #965 的根因與修法、並保留 #898 當時記錄的執行順序（EtlRunLog 先測、EtlJob 後測）理由。

**（第一輪送出時的）誠實揭露驗證深度，已被下面「review round」取代**：第一輪 `#965` 的驗證沒有在真實瀏覽器 / 跑起來的 demo 站台上重新執行 TC-29，只靠 `.NET` 單元測試層級＋程式碼層級推論鏈。**這個推論鏈本身不完整**——見下方，CI 用真實數據直接反證了其中一段。

### 5b. Review round：CI 實際跑出的第二個 bug，本機真實瀏覽器重現、診斷、修好、驗證

**CI 結果**：`e2e (baseline)`／`e2e (island)` 兩個 leg 都在同一支測試 FAIL，數據逐字如下：

```
[TC-29] 開始執行...
  RunLog .layui-table-body: 3
  RunLog 搜尋: Result=2, Trigger=2
  EtlJob .layui-table-body: 3
  ETL Job 搜尋欄位: Name=0, Status=0, SourceDbType=0
[TC-29] FAIL: /_EtlJob/Index 連純 HTML 的 Searcher.Name 欄位都沒有渲染——代表頁面路由本身出了問題
[TC-29] No browser console errors
```

**先讀數據，不要相信斷言訊息自己的診斷**：`EtlJob .layui-table-body: 3` 代表 grid 確實渲染成功，`No browser console errors` 代表沒有 JS 例外——這兩點直接反證斷言訊息裡「代表頁面路由本身出了問題」這句話，那是舊版（route 本身壞掉時期）留下的過時診斷文字，不是這次失敗的真正原因。真正的訊號是：grid 渲染、search 欄位沒渲染，且 RunLog 自己的 `Result=2, Trigger=2` 同一支測試裡是成功的——兩個頁面的 searcher 定義本身先讀過（`EtlJobSearcher.cs`／`EtlJobListVM.cs` 的 `<wt:searchpanel>` vs `EtlRunLogSearcher.cs`／`EtlRunLogListVM.cs`／`_EtlRunLog/Index.cshtml`），兩者的 `<wt:searchpanel vm="@Model" reset-btn="true">` 寫法逐字對稱，都沒有 `SearcherExpanded`；controller 的 `Index()` 也逐字對稱（`Wtm.CreateVM<T>()` + `PartialView(vm)`）——**排除了「EtlJobListVM 的 searcher 定義本身有問題」這個假設**。

**本機重現、找到真正原因**：在本機用 `dotnet run` 啟動一份全新（無殘留 `demo.db`）的 demo process，直接用 `test/e2e/wtm_e2e_tests.py --tc 29` 對著它跑——**第一次執行（冷啟動）就重現了與 CI 逐字相同的失敗**（`Name=0, Status=0, SourceDbType=0`）；同一個 process 重跑則穩定 PASS。這個「冷啟動失敗、熱重跑穩定過」的模式指向時序問題，不是恆定的邏輯錯誤。寫一支獨立診斷腳本（直接呼叫 `wtm_e2e_tests.py` 的 `login()`/`open_grid_via_direct_tab()`），在觸發 EtlJob 導覽後每 100ms 輪詢一次 `window.layui.table.cache` 的 key 與 `Searcher.Name` 的 DOM count，實測到：EtlJob 自己的 table.cache key 平均要再等 ~0.1–0.2s 才出現（且 search 面板欄位在那之前就已經出現，見下段），代表**現有等待條件本身太寬鬆**。

**根因**：`open_grid_via_direct_tab()`（`test/e2e/wtm_e2e_tests.py`）的等待條件是：

```python
await page.wait_for_function(
    """() => {
        const caches = window.layui?.table?.cache || {};
        return Object.keys(caches).length > 0;
    }""",
    timeout=TIMEOUT,
)
```

「全域 `table.cache` 至少有一個 key」——這對*第一次*呼叫（RunLog）是對的（此時 cache 確實是空的）。但 `table.cache` 是整個 page 生命週期共用、從不清空的物件；TC-29 在同一個 page/session 內把這支 helper 呼叫兩次，第二次呼叫（EtlJob）時 RunLog 那個 key 早就讓這個條件在點擊當下瞬間成立——helper 提早回傳，後面的 `page.wait_for_load_state("networkidle")` 給的緩衝在 demo process 剛啟動、EF/Razor 尚未 JIT 過的第一次請求上不夠吸收 EtlJob 自己實際渲染所需的時間，斷言因此在真正渲染完成前就讀到 0。**這正是 #898 當時把 EtlJob 排在 RunLog 之後這個順序，意外（非刻意）替其掩蓋掉的同一個 bug**——RunLog 天生是第一個呼叫，吃不到「第二次呼叫」這個 race；EtlJob 天生是第二個，一定會踩到。

**修法**（`open_grid_via_direct_tab()` 本身，`test/e2e/wtm_e2e_tests.py`）：等待條件改成「出現一個先前不存在的 key」而非「至少有一個 key」——呼叫前先記錄 `Object.keys(...)` 的快照，等待條件改成 `Object.keys(caches).some(k => !preSet.has(k))`。對第一次呼叫（快照是空集合）行為完全不變；對第二次（或未來任何一次）呼叫則正確等到「這次呼叫自己觸發的那個新 table」真正載入，不再被之前任何一次呼叫的殘留 key 騙過。

**驗證（本機真實瀏覽器＋真實 demo process，非模擬）**：
- 未修版（`git stash` 掉 helper 的修法）：`--tc 29` 對冷啟動的 demo process 執行，重現 `Name=0, Status=0, SourceDbType=0`，與 CI 逐字一致。
- 修好後：**兩次獨立的冷啟動**（各自 `pkill` 掉 process、刪除殘留 `demo.db`、重新 `dotnet run`）第一次執行即 PASS；同一個 process 上再連續熱重跑 3 次，全部 PASS——累計 5/5。
- 另外用同一支已修好的 helper 測試「EtlJob 先、RunLog 後」這個反過來的執行順序（獨立診斷腳本，非正式測試檔案），冷啟動照樣 3/3 PASS——見下方「執行順序」小節。
- 用 `python3 scripts/check-e2e-test-integrity.py test/e2e/wtm_e2e_tests.py` 確認這輪修改沒有引入「測試不會失敗」這類違規：clean。
- 用同一份本機 demo process 跑鄰近測試（TC-27/28/30，皆不使用 `open_grid_via_direct_tab()`）與全部 36 支 TC 的完整 e2e 套件，確認這次修法沒有波及其他測試——結果見下方「7. 驗證」。

**執行順序，更正**：#898 當時「RunLog 先測」的理由（避免壞掉的 EtlJob `<script>` 污染同一個 session）已隨 IIFE 缺陷修好而不成立；這輪 review 額外發現，那個順序同時也巧合地讓 `open_grid_via_direct_tab()` 的 table.cache race 從未在 RunLog 自己身上現形過（原因見上方「根因」小節）——不是因為 RunLog 本身不會遇到這個 race，只是它天生不會踩到「第二次呼叫」這個條件。**helper 本身修好後，順序不再是任何已知問題的必要 workaround**——已直接實測反過來的順序（EtlJob 先、RunLog 後）冷啟動一樣穩定 PASS（見上方）。程式碼仍保留原順序（EtlRunLog 在前），純粹因為這是現有、已充分驗證過的設定，這輪修法沒有理由再多改一件沒有必要性的事。

**未能驗證**：這輪修法尚未在真正的 Gitea Actions CI 上跑過（本次工作階段的 hard constraint 禁止任何 Gitea/GitHub API 呼叫、禁止開 PR）——只在本機真實 demo process＋真實瀏覽器上驗證過，不是模擬或程式碼層級推論。

### 6. Mutant：考慮過，判斷不加

這是一個 `core`／correctness 缺陷（生成器輸出的 JS 語法錯誤），不是安全缺陷——沒有未授權存取、注入或跨租戶維度。`test/mutants/run_mutant.py` 的 `VALID_KINDS` 只有 `{security, selftest}`，沒有對應的誠實分類；把它硬標成 `security` 才能讓 CI 強制執行，會重複 #970 那筆條目已經點名、且 #968（本文件上方獨立條目）才剛花一整張 PR 修過的同一種「`kind` 分類漂移」問題（`security`-kind entry 數量因為這類被迫分類而持續成長，直接推高 mutation-gate.yml 的時間預算，#968 為此把「每次 PR 跑全部 entries」改成「per-entry relevance selection」；`kind` 本身該不該加新分類目前刻意延後處理）。加一個必須造假分類才能被強制執行的 entry，不會比本節第 4 點已經完成的手動 RED/GREEN 證明（`git checkout` 還原修法、重新 build、跑同一支測試、逐字擷取失敗訊息，再還原修法重新驗證 GREEN）提供更多證據，卻會讓 `kind` 分類漂移這個已知問題再往前推一步。也不存在 `.claude/rules/testing.md`「fixture 不能自己供應本該由 production 供應的東西」那類風險（#899/#967 的失敗模式）——這支測試直接驅動真正的、未修改的 `DataTableTagHelper.Process()`，把真正 emitted 出來的字串交給真正的 parser，沒有任何地方用測試自己的邏輯取代被測程式碼的行為。

### 7. 驗證

`find . -name 'demo.db*' -path '*bin*' -delete && dotnet build core.slnf --no-restore -c Release`：0 error（101 個既有、與本次修改無關的 warning，含既有 nullable annotation/XML doc 警告）。

`dotnet test test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj -c Release --no-build`（無 filter，量到本次改動前的乾淨基準）：base **5041 passed, 0 failed** → **5042 passed, 0 failed**（1 個新測試，其餘全部沿用既有 golden 斷言後仍然通過）。

`dotnet test core.slnf -m:1 --no-build -c Release --verbosity normal --filter "TestCategory!=Integration"`（`-m:1`／`--filter` 這兩個決定「跑哪些測試」的旗標與 `.github/workflows/ci-build.yml` 的 `build-and-test` job 相同；該 job 另外還有 `--logger`／`--collect`／`--settings`／`--results-directory` 幾個只影響 TRX/coverage 產物、不影響測試是否通過的旗標，這裡略過，不是逐字重現整條指令）：7 個測試專案全部 `Test Run Successful`，0 failed——`WalkingTec.Mvvm.Core.Test` 5035 passed（此指令帶 `TestCategory!=Integration` filter，與上面無 filter 的 5042 不是同一個分母，故數字不同屬預期）、`WalkingTec.Mvvm.Admin.Test` 192 passed、`WalkingTec.Mvvm.Mvc.Tests` 64 passed、`WalkingTec.Mvvm.Etl.Test` 699 passed（含 `EtlJobListVmGridTests.cs` 既有的 `OnClickFunc` 相關測試，無回歸）、`WalkingTec.Mvvm.WorkFlow.Test` 589 passed + 23 skipped（既有；含 `NotifierTests.cs` 的 `ProcessDefinitionListVMTests`，無回歸）、`WalkingTec.Mvvm.Api.Test` 103 passed + 1 skipped（既有的 mutation-gate baseline selftest）、當時 core.slnf 內第 7 個測試專案——S3 file handler 的測試專案（已於 10.23.0 依 #1054 移除）——19 passed。

`python3 -m py_compile test/e2e/wtm_e2e_tests.py`：語法檢查通過。`python3 scripts/check-e2e-test-integrity.py test/e2e/wtm_e2e_tests.py`：clean，無違規。

**Review round 新增：本機真實瀏覽器＋真實 demo process 執行的 e2e 驗證**（見上方「5b」小節根因與修法全文）。`dotnet run -c Release --no-build` 啟動 `demo/WalkingTec.Mvvm.Demo`（`ASPNETCORE_ENVIRONMENT=Development`，`http://localhost:52837`，`IsQuickDebug: true`，SQLite `./demo.db`，每次冷啟動前先刪除殘留的 `demo.db`/`-shm`/`-wal`），`playwright`（1.58.0，chromium 已預裝）對它跑：

- `python3 wtm_e2e_tests.py --tc 29`：未修版（`open_grid_via_direct_tab()` 修法暫時還原）對冷啟動 process 執行，逐字重現 CI 的失敗（`Name=0, Status=0, SourceDbType=0`）。修好後：兩次獨立冷啟動（各自 kill process、清 `demo.db`、重新 `dotnet run`）第一次執行即 PASS，同一個 process 上再連續熱重跑 3 次也全部 PASS，累計 **5/5**。
- `python3 wtm_e2e_tests.py --tc 27,28,30`（鄰近測試，皆不使用 `open_grid_via_direct_tab()`）：3/3 PASS，確認這次修法沒有波及其他測試。
- `python3 wtm_e2e_tests.py`（全部 36 支 TC，同一個熱 process）：**Total: 36 | PASS: 35 | FAIL: 0 | ERROR: 0 | SKIP: 1**（唯一的 SKIP 是既有的 TC-36，demo 未啟用多租戶主機模式，與本次修法無關）——與 CI 那次失敗的「Total: 36 | PASS: 34 | FAIL: 1 | ERROR: 0 | SKIP: 1」相比，唯一變化就是 TC-29 從 FAIL 翻成 PASS，其餘 34 個 PASS + 1 個 SKIP 不變。

**仍未能驗證的部分（誠實列出）**：這輪修法尚未在真正的 Gitea Actions CI 上跑過——本次工作階段的 hard constraint 禁止呼叫任何 Gitea/GitHub API、禁止開 PR，只能本機驗證。本機的 demo process／SQLite／Playwright 版本與 CI 的自架 runner不保證逐一致（例如 CI runner 的 CPU/記憶體資源、Chromium 版本可能不同），因此「本機冷啟動可重現、修好後可穩定通過」不等於「CI runner 上保證不會有更極端的時序」——但 root cause（等待條件本身邏輯錯誤，不是單純的時間不夠長）已經修好，且新等待條件的正確性不依賴任何特定的時間常數。

---

## DistributedLookupCacheService.RefreshAsync 缺 registry 檢查——#944 留下的第三個實例（#975）（2026-08-01）

上方 #944 條目本身已明確記錄：`DistributedLookupCacheService.RefreshAsync` 結構上有相同缺口，但因為 fallback TTL 有界（30 分鐘）而非不死，判斷為「同一家族裡程度較輕、範圍不同的變體」，當時刻意不修、留待另立 issue。本次工作階段的 issue #975 就是那張票，此處收尾。

**先從這個類別自己的 `_registry`/`IsCacheable` 重新推導，不採信 #944 實作會直接搬過來**：`DistributedLookupCacheService.RefreshAsync<T>`（`if (!acquired)` 逾時檢查——#943 的修復——已存在）沒有它自己的手足方法 `GetAll`/`GetAllAsync`（`DistributedLookupCacheService.cs` 開頭就有的檢查，:188 同步／:245 非同步）：`if (!_registry.ContainsKey(typeof(T))) return LoadFromDb<T>(dc);`。`RefreshAsync` 完全沒有對應檢查，任何 `T`（不論是否註冊）都會直接跑到 `Invalidate`/`LoadFromDbAsync`/`SetDistributedAsync`。

**與 #944 的差異（從這個類別自己的 `BuildCacheEntryOptions` 重新推導，非採信 issue 文字）**：`SetDistributedAsync` → `BuildCacheEntryOptions(attr)` 在 `_registry.TryGetValue(entityType, out var a)` 失敗（未註冊）時，走 `attr != null ? TimeSpan.FromMinutes(attr.TtlMinutes) : TimeSpan.FromMinutes(30)` 的 fallback 分支——**有界 30 分鐘**，不是 #944 那種「`SetCache` 對未註冊型別完全跳過 TTL 設定，於是條目在 `IMemoryCache` 裡活到 process 重啟」的不死快取。這就是本條目標題所述、也是 issue 本身指出的差異：後果不同（有界污染 vs. 永久洩漏），修法是否也該不同因此需要重新判斷，不能假設 #944 的實作直接搬過來就對。**結論：實測後，缺陷本身（缺 registry 檢查）與 #944 標題所述完全相符，只是後果嚴重度不同**——即使會自動過期，仍不該讓未註冊型別繞過 `GetAll`/`GetAllAsync` 已經強制的 `IsCacheable` 契約，於是修法本身跟 #944 一樣：在方法最前面（`BuildKey`/`_keyLocks.GetOrAdd`/任何 semaphore 動作之前）加上同一個 registry 檢查，未命中就直接 `return`（no-op），不影響同方法內既有的 #943 `if (!acquired)` guard（獨立、更早的第二道檢查）。生產路徑可觸及性與 #944 相同：`WTMContext.RefreshLookupAsync<T>()` 呼叫 `svc.GetAttribute(typeof(T))`（未註冊型別回傳 `null`，沒有任何 guard），接著無條件呼叫 `svc.RefreshAsync<T>(...)`。

**修第三個實例前，先確認沒有第四個**：全樹重新推導 `ILookupCacheService` 的所有實作與其寫入路徑方法。指令與結果：

```
$ grep -rl "ILookupCacheService" src/ --include='*.cs'
src/WalkingTec.Mvvm.Core/Cache/DistributedLookupCacheService.cs
src/WalkingTec.Mvvm.Core/Cache/ILookupCacheService.cs
src/WalkingTec.Mvvm.Core/Cache/LookupCacheService.cs
src/WalkingTec.Mvvm.Core/Cache/LookupCacheStats.cs
src/WalkingTec.Mvvm.Core/Cache/LookupCacheWarmupService.cs
src/WalkingTec.Mvvm.Core/DataContext.cs
src/WalkingTec.Mvvm.Core/WTMContext.cs
src/WalkingTec.Mvvm.Core/WTMContext.LookupCache.cs
src/WalkingTec.Mvvm.Mvc/Helper/FrameworkServiceExtension.cs

$ grep -n "class.*: ILookupCacheService" src/WalkingTec.Mvvm.Core/Cache/*.cs
DistributedLookupCacheService.cs:59:    public sealed class DistributedLookupCacheService : ILookupCacheService
LookupCacheService.cs:25:    public class LookupCacheService : ILookupCacheService
```

`DataContext.cs`/`WTMContext.cs`/`WTMContext.LookupCache.cs`/`FrameworkServiceExtension.cs` 逐一確認過都只是消費端（DI 解析、屬性型別、註冊委派），沒有第三個 `: ILookupCacheService` 實作——全樹只有這兩個類別。針對兩個類別各自的三個泛型寫入路徑方法（`GetAll<T>`／`GetAllAsync<T>`／`RefreshAsync<T>`——唯三個會呼叫 `SetCache`/`SetDistributedAsync` 寫入快取本體的方法），逐一以行號範圍配 grep 計數 `_registry.ContainsKey(typeof(T))`／`_registry.TryGetValue(typeof(T)` 在方法本文裡的出現次數：

| 類別 | 方法 | 有 registry-check-on-T（寫入前置檢查） |
|---|---|---|
| `LookupCacheService` | `GetAll<T>` | 有 |
| `LookupCacheService` | `GetAllAsync<T>` | 有 |
| `LookupCacheService` | `RefreshAsync<T>` | 有（#944 修復） |
| `DistributedLookupCacheService` | `GetAll<T>` | 有 |
| `DistributedLookupCacheService` | `GetAllAsync<T>` | 有 |
| `DistributedLookupCacheService` | `RefreshAsync<T>` | **原本沒有——這就是 #975** |

另外確認過兩個檔案都沒有用 `IsCacheable(typeof(T))` 這種替代寫法繞過偵測（`grep -n "IsCacheable(typeof(T))"` 兩個檔案皆零筆），排除「檢查存在但寫法不同、grep 抓不到」的假陰性。**結論：找不到第四個實例**——`#975` 修完後，這個家族（未註冊型別繞過 registry 檢查被寫入快取）在全樹已無已知未修版本。

**這個 survey 沒有證明的部分（誠實列出）**：純字面 grep pattern-match，不是語意驗證——三個方法各自的檢查是否真的擋在寫入之前、而非巧合出現在方法本文任意處，是人工讀碼確認的（見上方逐方法程式碼引用），grep 本身無法保證這點。`Invalidate<T>`／`InvalidateType` 是刪除／sentinel-only 路徑，不寫入快取本體，故不列入表格——這是刻意排除，不是遺漏，但也代表這個 survey 沒有涵蓋「刪除路徑是否有其他缺陷家族」這個問題。只搜尋 `src/` 樹下的 `.cs` 檔案；未涵蓋任何動態產生或反射建構的 `ILookupCacheService` 實作（本次沒有找到、也沒有證據存在這種東西，但 grep 本身不能排除）。

**測試（TDD 順序：先寫測試對著未修的程式碼跑紅，再實作，再確認變綠）**：`test/WalkingTec.Mvvm.Core.Test/Cache/DistributedLookupCacheRefreshAsyncRegistryCheckTests975.cs`——沿用 `DistributedLookupCacheTests.cs` 裡既有的 `DistTestHelper`/`DCity`（已註冊，`[CacheLookup(TtlMinutes = 10, WarmOnStartup = true)]`）/`DOrder`（未掛 `[CacheLookup]`，未註冊，該檔自己的 `Uncacheable_type_always_loads_from_db` 測試已把它定調為這個服務的「未註冊」case）。**RED-before-fix（實測，逐字，Release build，經 `run_mutant.py` 的 dry run 再次確認）**：`Assert.IsNull failed. Bug #975: RefreshAsync<T> must not write an unregistered (non-[CacheLookup]) type to the distributed cache -- even though such an entry would self-expire after the bounded 30-minute fallback TTL (unlike #944's immortal IMemoryCache entry), it must never be written in the first place.` 刪掉修法裡的 `if (!_registry.ContainsKey(typeof(T))) { return; }` guard，這條測試就會變紅——單一行刪除即可讓斷言失敗，不需要更動測試本身。**負控組**：`RefreshAsync_RegisteredType_StillCachesNormally_NegativeControl`（本檔新增，自成一體）——驗證已註冊型別（`DCity`）透過 `RefreshAsync` 仍正常寫入分散式快取（`distCache.Get(key)` 非 null）且內容可經 `GetAllAsync` 正確讀回，修復前後皆綠，從未變紅（修法只在方法最前面加一個提早 `return`，已註冊型別永遠不會進入那個分支，因此這條測試本來就不依賴修法是否存在）。

**Mutation gate**：一個新 entry，`test/mutants/entries/lookupcache975-distributed-refreshasync-registry-guard-neutralize.json`（patch 把 `if (!_registry.ContainsKey(typeof(T)))` 改成 `if (false)`，一行、compile-preserving，patch 用「暫改 → `git diff` → 還原」方式對著已提交的修復版本產生，非手寫）。`red_expected_assertion_patterns` 直接從 `run_mutant.py --trx-dir` 產生的 TRX 檔用 `xml.etree.ElementTree` 讀出——第一次 dry run（pattern 還是 placeholder）回報 `UNEXPECTED_RED`，證明 harness 真的有評估這個 mutant 而非短路通過；填入真實 pattern 後，**兩次獨立重跑**皆 **`VERDICT: KILLED` / `GATE: PASS`**。所選 pattern `Bug #975: RefreshAsync<T> must not write an unregistered` 為單行片段（訊息本身不含換行，繞開 Python `.*` 預設不跨行的陷阱），不含任何 regex 特殊字元需要跳脫，且不匹配 `run_mutant.py` 自己的任何 `PROBE_MESSAGES`。**`kind` 選擇**：`"security"`——`run_mutant.py` 的 `VALID_KINDS` 只接受 `{security, selftest}`，`selftest` 專屬 runner 自身邏輯測試，這個不是；本質是快取污染／契約繞過的完整性缺陷，不是傳統的未授權存取或 injection，但在現有 schema 下 `security` 是唯一能讓 mutant 被 CI 的 `mutants` job 實際執行、非 KILLED 會擋 gate 的功能性選項——跟 `lookupcache943-distributed-refreshasync-acquired-guard-neutralize.json`/`lookupcache944-refreshasync-registry-guard-neutralize.json`/`etl970-cancellation-classification-guard-neutralize.json` 記錄的同一種強制分類理由一致。**entry 數量**：直接對本次實際 base commit（`3887d7b11`，非文件裡任何舊數字）解析 `test/mutants/entries/*.json`——總數 72 → 73；`security`-kind（逐檔解析 `"kind"` 欄位，未依賴 script 本身）66 → 67。

**驗證**（在本次實際 base commit `3887d7b11` 上量測，未採信文件裡任何舊數字——當天數字已變動多次）：`find . -name 'demo.db*' -path '*bin*' -delete && dotnet build WalkingTec.Mvvm.sln`——1 個已知、跟本次修改無關的錯誤：`NETSDK1082`（`BlazorDemo.Client` 缺 `browser-wasm` runtime pack），**直接對本次 base commit 單獨重建同一個專案確認過同一個錯誤存在**，不是本次修法造成的新問題。`dotnet test test/WalkingTec.Mvvm.Core.Test/`：修復前（base commit 加上本次新增的兩個測試方法、production 程式碼尚未修改）**5033 passed, 1 failed**（total 5034——唯一失敗即上方 RED-before-fix 那條）；修復後 **5034 passed, 0 failed**（total 不變，只有那條新測試從紅轉綠，其餘全部持平，未見任何連帶回歸）。

**未能驗證的部分（誠實列出，不是隱藏）**：本次工作階段的 HARD CONSTRAINT 禁止呼叫任何 Gitea/GitHub API、禁止開 PR，因此這個修復尚未在真正的 Gitea Actions CI 上跑過——本機驗證只到 `dotnet build`/`dotnet test`/`run_mutant.py` 這三層。上方「class × method × has-check」survey 只是全樹一次性的靜態 grep 快照，不是持續稽核機制——未來若有人新增第三個 `ILookupCacheService` 實作或用替代寫法繞過 `_registry.ContainsKey(typeof(T))` 這個字面模式，這份 survey 不會自動重跑並抓到。

## `WtmFileProvider.GetFileTenantScoped`/`GetFileNameTenantScoped`：`BaseImportVM` 的 `UploadFileId` 讀取點改走不受旗標影響的租戶範圍讀取（#1011，2026-08-03）

**設計史（不重新開議，但完整記錄推翻過程，因為這正是 user 要求的流程）**：本 issue 第一輪判斷是「什麼都不用加」——論點是跨租戶*刪除*沒有正當部署形狀（刪除端理當強制範圍），但跨租戶*讀取*有正當部署形狀（`FileUploadOptions.cs` 對 `EnforceTenantFileScope` 自己的文件註解就記錄了「租戶無關公開檔案庫」這個明確的 opt-out 用途），所以部署層級的旗標才是讀取的正確控制層——修法應該到此為止。跨廠 review 用一條**實際存在、已驗證的呼叫路徑**推翻了這個判斷，不是假設性的：`BaseImportVM.cs:322` 與 `:1571` 兩處都用 flag-driven 的 `GetFile` 解析 `UploadFileId`——而 `UploadFileId` 是 model-bound 輸入，跟 `WtmFileProvider.DeleteFileTenantScoped` 自己的文件註解已經明講的 `BaseVM.DeletedFileIds` 同一類（未經驗證、呼叫端可控）；而且 `UploadFileId` 不是共用範本：`SetTemplateData`（`BaseImportVM.cs:311-315`）在它是 `null` 時回「請上傳範本」，`:317` 讀進來後直接餵給 NPOI——它就是使用者剛上傳的那份工作簿。真正的分界線從來不是讀取 vs 刪除，而是**呼叫端可控的 ID sink 必須不受全域旗標影響**——跟 `DeleteFileTenantScoped` 在刪除端（#815）已經確立的原則完全一樣。

### 修了什麼

**新增（additive，`src/WalkingTec.Mvvm.Core/Support/FileHandlers/WtmFileProvider.cs`）**：`GetFileTenantScoped(string id, bool withData = true, IDataContext? dc = null)` 與 `GetFileNameTenantScoped(string id, IDataContext? dc = null)`。兩者與既有 `GetFile`/`GetFileName` 共用私有 core（`GetFileCore`/`GetFileNameCore`），沿用這個檔案自己已經為 #815/#821 建立的 `DeleteFile`/`DeleteFileTenantScoped`/`DeleteFileCore` 形狀——沒有另開一套模式。Scoped 版本一律傳 `enforceTenantScope: true`；**`GetFile`/`GetFileName` 的既有 flag-driven 行為完全不變**——這次加法不動 `FileUploadOptions.cs:52-53` 記錄的那個 opt-out，既有呼叫端零影響。

**行為變窄（`src/WalkingTec.Mvvm.Core/BaseImportVM.cs`）**：`UploadFileId` 的兩個讀取點——`SetTemplateData`（`:322`）與 `GetErrorJson`（`:1571`）——改呼叫 `GetFileTenantScoped`。**這是真實的行為變更，不是加法，而且範圍很窄**：只有明確把 `FileUploadOptions.EnforceTenantFileScope` 撥回 `false`（opt-out，不是 #859 之後的預設）的部署，之前匯入流程能用 GUID 跨租戶解析 `UploadFileId`；修法後不能，跟這個旗標無關。用 `EnforceTenantFileScope` 預設值（`true`）的部署對這條路徑沒有任何觀察得到的變化——修法前 `GetFile`/`GetFileTenantScoped` 對這種部署本來就給同一個答案。

**刻意沒動：`Upload`**。`Upload` 自己的缺陷——`TenantCode` 蓋自環境身分（`_wtm.LoginUserInfo?.CurrentTenant`，`:135`），不是傳入的 `dc.TenantCode`——是不同性質的問題，追蹤在另一張票 #988。這裡加一個 `UploadTenantScoped` 會搶先決定 #988 還沒定案的設計方向。

### 這個修法沒解決什麼——誠實列出，不能被讀成已關閉

**（2026-08-03 補，v10.22.0 發版前跨廠審查指出）「鎖進那個租戶」這句話還有一個前提沒寫出來：那個 `IDataContext` 得先有租戶過濾器。** scoped 分支做的事只是**不呼叫 `IgnoreQueryFilters()`**（`WtmFileProvider.cs` 的 core 實作），它**不會自己加上 `TenantCode == dc.TenantCode` 這個 predicate`**。對框架自己的 `DataContext` 兩者等價，因為它確實在 `OnModelCreating` 配置了 `ITenant` 的 global query filter（`DataContext.cs:243`）。但 `IDataContext` 是公開介面，契約只要求提供 `DbSet<T>`（`IDataContext.cs`），**沒有**要求那個 filter；框架也會反射載入下游任意的 `DbContext`（`CS.cs`）。所以一個合法但沒有配置 `HasQueryFilter` 的下游 context，用這兩個 overload 得不到任何租戶範圍——查詢不帶 tenant predicate，會照樣回傳別的租戶的列。`BaseImportVM` 的兩個新呼叫點依賴同一個前提。**要把這件事變成無條件保證，得讓 overload 自己加上 predicate（或收窄介面契約並文件化），兩者都不在 #1011 範圍內。**

`GetFileTenantScoped` 只是把讀取範圍鎖進「傳入的 `IDataContext` 已經解析出來的那個租戶」——它不驗證呼叫端身分，也不判斷「這個請求正確的租戶應該是誰」。`Configs.DisableRefererTenantResolution` 預設 `false`（`Configs.cs:162-178`）時，一個知道某租戶網域的匿名呼叫者可以偽造 `Referer` header，讓 `WTMContext.CreateDC`（`WTMContext.CreateDC.cs:42-56`，#116 機制）解析出那個租戶的範圍；scoped API 接著會忠實地把讀取鎖進**那個被偽造出來的租戶**。要擋這條路徑需要 `DisableRefererTenantResolution=true`，或是一個在請求抵達 `WtmFileProvider` 之前就拒絕匿名呼叫者的 authorizer——不是改這個方法本身，它目前的行為就是文件註解寫的那樣。#859 的舊修法同樣不擋得住這條路徑（同一個根因：兩者都嚴格下游於 `CreateDC` 已經決定的租戶）。

### 對下游的可達性

三份 demo `FileApiController.cs` copy 已經呼叫 `DeleteFileTenantScoped`（#830），但它們從來沒有為 `UploadFileId` 呼叫過 `WtmFileProvider.GetFile`/`GetFileTenantScoped`——`BaseImportVM` 是框架自己的基底類別，所有 `*ImportVM` 都繼承它，所以單純升級 NuGet 套件（下游不用改任何自己的程式碼）就已經讓所有建立在 `BaseImportVM` 上的匯入流程套用這次修法。唯一真正沒受益的殘餘案例：某個下游在 #1011 出現之前就複製並自行修改過 `BaseImportVM` 本身（不只是 `FileApiController`）——那份複本要等它自己的維護者重新套用或重新 scaffold 才受益，跟任何框架基底類別的修法一樣。

### 測試方法論：為什麼是 SQLite 不是 EF InMemory

斷言的核心是全域 `ITenant` query filter 的 SQL 轉譯——EF Core 對 nullable 欄位 `==` 的 null-safe 轉譯——InMemory provider 用 LINQ-to-objects 直接跑 .NET 運算式，完全不做這層轉譯，綠燈證明不了真實關聯式資料庫（SQL Server/SQLite/Oracle）的行為。三份新測試檔案：

- `test/WalkingTec.Mvvm.Core.Test/Security/WtmFileProviderTenantScopedReadTests1011.cs`（5 個測試，全部在 `EnforceTenantFileScope=false` 下跑）：釘住 `GetFile` 既有的跨租戶 opt-out（不「修」它）；斷言 `GetFileTenantScoped`/`GetFileNameTenantScoped` 擋下跨租戶；各配一個同租戶的正控組。
- `test/WalkingTec.Mvvm.Core.Test/VM/BaseImportVMTenantScopedReadTests1011.cs`（2 個測試）：驅動**真正、沒有覆寫**的 `BaseImportVM.SetTemplateData()`——不像這個專案其他 `BaseImportVM` fixture 用 bytes-injection 覆寫繞過 `WtmFileProvider`——透過 DI mock 接上 `WtmFileProvider`，`WTMContext` 用 `CreateDC` bypass 直接回傳已經租戶範圍化的 context（跟 `FrameworkControllerFileAccessTest.SingleConnectionFileAccessWtmContext` 同一招；`Wtm.CreateDC('default')` 沒辦法直接 mock，見 `.claude/rules/testing.md`）。斷言跨租戶 `UploadFileId` 被擋（0 筆解析、1 條 `WrongTemplate` 錯誤），同租戶 `UploadFileId` 仍然成功（1 筆解析、0 錯誤）。
- `test/WalkingTec.Mvvm.Core.Test/Security/GetFileTenantScopedRefererGapTests1011.cs`：誠實記錄 Referer 缺口——匿名呼叫者用偽造 `Referer` 讓 `CreateDC` 解析出某個已註冊租戶的網域，斷言 `GetFileTenantScoped` **會**解析（不是拒絕）那個被偽造出來的租戶的檔案。既有的 `FrameworkControllerFileScopeTests859.cs` 自己的類別文件註解就寫明它只涵蓋「已驗證跨租戶」與「未驗證但沒有 Referer」兩種形狀，刻意沒涵蓋「未驗證 + 偽造 Referer」——這條測試補的正是這個缺口，不是重複既有覆蓋。

**RED-before-fix，逐字擷取**（各自獨立暫時 revert 後重跑，之後各自獨立還原）：

```
Assert.IsNull failed. #1011: GetFileTenantScoped must NOT resolve a file belonging to
a different tenant, even though EnforceTenantFileScope=false — the scoped overload
keeps the global ITenant query filter ON unconditionally, so a caller-controlled id
(e.g. BaseImportVM.UploadFileId) cannot read across tenants regardless of the
deployment-wide flag.
```
（`WtmFileProviderTenantScopedReadTests1011.GetFileTenantScoped_EnforceTenantFileScopeFalse_CrossTenantGuid_ReturnsNull`；暫時把 `GetFileTenantScoped` 改回吃旗標後重跑，1 failed / 4 passed。）

```
Assert.AreEqual failed. Expected:<0>. Actual:<1>. #1011: a cross-tenant UploadFileId
must not be parsed into any template rows — got 1. Errors:
```
（`BaseImportVMTenantScopedReadTests1011.SetTemplateData_EnforceTenantFileScopeFalse_CrossTenantUploadFileId_Blocked`；暫時把 `SetTemplateData` 的呼叫改回 `GetFile` 後重跑，1 failed / 1 passed——`Actual:<1>` 證明修法前那份跨租戶種下的工作簿真的被解析、真的被 NPOI 成功剖析出一列，不是斷言本身接錯線。）

兩處還原後重新確認全部回到 GREEN。

### Mutation gate

`test/mutants/entries/baseimportvm1011-settemplatedata-tenant-scope-revert.json`：patch 把 `SetTemplateData` 的 `fp.GetFileTenantScoped(...)` 換回 `fp.GetFile(...)`（compile-preserving 的方法名替換，兩個 overload 在 `WtmFileProvider` 上都存在）。`python3 test/mutants/run_mutant.py --mutant baseimportvm1011-settemplatedata-tenant-scope-revert` 逐字回報 `VERDICT: KILLED` / `GATE: PASS`。

兩個 green（正控組）測試，各自對照 `.claude/rules/testing.md` 那條「正控組不能碰到被突變的決策路徑」硬性規則單獨追過呼叫圖：
1. `WtmFileProviderTenantScopedReadTests1011.GetFileTenantScoped_EnforceTenantFileScopeFalse_SameTenantGuid_ReturnsFile` 直接呼叫 `WtmFileProvider.GetFileTenantScoped`——`BaseImportVM.SetTemplateData`（這個 patch 唯一動到的方法）根本不在它的呼叫圖上，被突變的那行程式碼對這個測試而言不可能執行到。屬於「呼叫圖從未觸及突變行」這一類最單純的解耦論證。
2. `BaseImportVMTenantScopedReadTests1011.SetTemplateData_EnforceTenantFileScopeFalse_SameTenantUploadFileId_Succeeds` 確實會執行到被突變的那行（它也驅動真正的 `SetTemplateData()`），但這個輸入下的結果可證明是不變量：`EnforceTenantFileScope=false` 時，突變版的 `GetFile` 走 `IgnoreQueryFilters()`（完全沒有租戶條件），修法版的 `GetFileTenantScoped` 走 `TenantCode == 呼叫端租戶`；對一個 `TenantCode` 本來就等於呼叫端自己租戶的檔案，不論有沒有套用租戶過濾都會解析到同一列——突變改變的是「跑哪一個查詢」，不是「這一列找不找得到」，所以對這個特定輸入而言修法前後的結果可證明相等。這是比第 1 條更強的第二層檢查，驗證 patch 宣稱的影響範圍（只影響跨租戶案例）確實成立，不是巧合。

完整解耦論證寫進了 entry 的 `description`/`green_test_decoupling` 欄位，不只留在這裡。

### 驗證

`find . -name 'demo.db*' -path '*bin*' -delete && dotnet build WalkingTec.Mvvm.sln -c Release`：0 錯誤。`dotnet test test/WalkingTec.Mvvm.Core.Test/ -c Release --filter "TestCategory!=Integration"`：**5063 passed, 0 failed**（含本次新增 8 個測試）。`dotnet test test/WalkingTec.Mvvm.Admin.Test/ -c Release`：**192 passed, 0 failed**。`dotnet test test/WalkingTec.Mvvm.Api.Test/ -c Release`：**103 passed, 0 failed**，1 個既有 skip（`AlwaysFails_MutationGateBaselineSelftestFixture`，mutation-gate 自我測試用，非本次相關）。

**未能驗證的部分（誠實列出，不是隱藏）**：本次工作階段的 HARD CONSTRAINT 禁止呼叫任何 Gitea/GitHub API、禁止開 PR，因此這個修復尚未在真正的 Gitea Actions CI 上跑過——本機驗證只到 `dotnet build`/`dotnet test`/`run_mutant.py` 這三層。「三份 demo `FileApiController.cs` 都沒呼叫 `UploadFileId` 相關的 `GetFile`」這個結論是逐檔人工確認，不是全樹 grep 掃描的結果，如果未來有 demo 樣板繞過 `BaseImportVM` 自行讀取 `UploadFileId`，這份記錄不會自動抓到。CHANGELOG 的 Red Line 修正（`[10.21.0]` #859 條目原本寫「regardless of which route reached it」）改動的是這份文件已經記錄過的既有 caveat 的**措辭**，不是新增一個之前沒被覆核過的事實。

---

## mutant patch 逐檔 `git apply --check`：CI 補一個「同一個 PR 內就能證明」的 gate，不宣稱防住整個缺陷類別（#1005，2026-08-03）

**這是 CI-only 基礎設施，不動任何 `WalkingTec.Mvvm.*` package 程式碼。**

**事故本身**：mutant patch 的 fixed context 可以被「另一張、完全不碰這個 patch 檔案」的 PR 用一次無關編輯打壞。本次工作階段之前，`#1000`（同一個 release cycle，見上方該條目）在 `FileAttachmentSaveChangesGuard.cs` 的 rejection `if` 與 `throw` 之間插入一行 `LogPrincipalKeyRejection(...)` 呼叫——這一行剛好落在 `test/mutants/patches/fileattachmentguard985-principal-key-check-neutralize.patch`（`#985` 自己出的、用來釘住自己修法的 patch）的 fixed context 裡面。`git apply --check` 對 `#985` 單獨跑會過；對 `#985` + `#1000` 一起跑會失敗，訊息是 `patch does not apply`。**兩張 PR 自己的 CI 都看不到這件事**：見下方「Gitea checkout 機制的查證」。`run_mutant.py` 自己的 `apply_patch()` 跑的就是這一模一樣的 `git apply --check`，失敗時丟出 `GateError(VERDICT_PATCH_DID_NOT_APPLY)`；`PASSING_VERDICTS` 不含這個 verdict，所以下游的 `mutants`/`meta-selftest` job 會正確地讓 gate 失敗——但要等到兩個分支真正共享同一棵樹的那一刻才會發生，在這個 repo 的 checkout 模型下，那是合併後第一次對 `dotnet10` 的 `push`，比任何一張 PR 自己的 CI 晚了一整個 gate 週期。

### Gitea checkout 機制的查證（issue 本身的斷言，逐字核對，不採信記憶）

Issue 主張：這個 repo 的 Gitea PR CI 對 `pull_request` 事件 checkout 的是 `refs/pull/N/head`，不是 merge ref。查 `.github/workflows/*.yml` 與 `docs/ci-operations.md`：**這份記錄完全支持這個斷言，不需要修正 issue 的 framing**。`docs/ci-operations.md` 第 133-144 行（第 5 節）逐字寫著：

> `actions/checkout@v5` 在 `pull_request` 事件只 checkout PR 自己的 head，不是 base+head 的 merge
>
> **事實**：job log 的 checkout step 印出：`[command]/usr/bin/git checkout --progress --force refs/remotes/pull/<N>/head`
>
> Gitea 對 `pull_request` 事件 checkout 的是 **PR 分支自己的快照**（`refs/remotes/pull/N/head`）。**這跟 GitHub Actions 相反**：GitHub 對同一事件 checkout 的是 base 與 head 的 merge 結果……Gitea 不會——PR 分支比 base 舊多少，CI 就看不到 base 上比它新的東西。

同一小節也記錄了這個機制曾造成的實際案例（`#906`：`#882` 合併後 `production-readiness.md` 已更新、`dotnet10` 上測試全綠，但開在 `#882` 之前的 PR `#881`/`#904` 仍各自紅在同一斷言，因為它們的 checkout 停在合併前的快照）——這正是同一個機制的另一個展示，跟本票要修的「patch fixed-context 被跨 PR 打壞」是同一根因的不同症狀。`.github/workflows/*.yml` 逐一確認：8 個 workflow 檔案的 `pull_request` trigger 都用 `actions/checkout@v5` 的預設行為（沒有任何一處自行覆寫成 merge-ref checkout），跟 `docs/ci-operations.md` 記錄的一致。**結論：issue 的 framing 站得住腳，不需要更正。**

### 為什麼修法擴充既有的 `scripts/check-mutant-entries-parse.py`，不是新開一支腳本

讀過該腳本本身後確認：它已經在 `.github/workflows/mutation-gate.yml` 的 `changes` job（本 repo 兩個 trigger 都沒有 path filter、已透過 `gate` job 掛成 required check 的唯一 job）裡對每一次 PR 無條件執行；`git apply --check` 是 dry run（不寫入 working tree 或 index），對 66 個 patch 全部跑完只要毫秒等級，不像 `run_mutant.py` 下游那樣需要真的 build/test、因此完全不共享那些 flaky 失敗模式。這正是原本 `check-mutant-entries-parse.py` 自己 docstring 陳述的設計原則（「cheap job，沒有 flaky failure mode」）——延續原本的判斷準則，沒有找到更好的落點。

### 修法內容

- **規則**：對 `test/mutants/patches/*.patch` 下每一個檔案跑 `git apply --check`，對象是這個 job 當下 checkout 到的樹（跟 `run_mutant.py` 自己的 `apply_patch()` 看到的完全一樣）。檔案清單來自直接對 `test/mutants/patches/` 目錄 glob，不是走每個 entry 的 `patch` 欄位——所以不管有沒有 entry 引用它，每個 patch 都會被檢查（acceptance criterion 4）。
- **Orphan patch（沒有任何 entry 引用的 patch）**：獨立、非阻斷性地回報，不影響 exit code。**這個 repo 現況：0 個 orphan**——用一支獨立腳本重新推導（不沿用 check-mutant-entries-parse.py 自己的邏輯，避免同一個 bug 兩邊都算對）：

```
$ python3 - <<'EOF'
import json
from pathlib import Path
entries_dir = Path("test/mutants/entries")
patches_dir = Path("test/mutants/patches")
referenced = set()
for p in sorted(entries_dir.glob("*.json")):
    entry = json.loads(p.read_text(encoding="utf-8"))
    patch = entry.get("patch")
    if patch and patch.startswith("patches/"):
        referenced.add(Path(patch).name)
on_disk = set(p.name for p in patches_dir.glob("*.patch"))
print("patches on disk:", len(on_disk))
print("referenced from entries:", len(referenced))
print("orphans:", sorted(on_disk - referenced))
print("entries pointing at a missing patch file:", sorted(referenced - on_disk))
EOF
patches on disk: 66
referenced from entries: 66
orphans: []
entries pointing at a missing patch file: []
```

  （查證過程的一個岔路，記錄下來避免下次重踩：第一次跑這支腳本時錯把 `Path(patch).name` 用在所有 entry 上，結果把 `test/mutants/_selftest/*.patch`——meta-selftest 專用、由 `run_mutant.py --expect-verdict` 消費、根本不在 `test/mutants/patches/` 目錄下——的 3 個檔案名算成「entry 指向的、但目錄裡沒有」的假警報。加回 `patches/` 前綴過濾後，這 3 個 selftest fixture 正確被排除，不計入本票的 orphan/missing 統計——它們屬於一個完全不同的機制，見下方 selftest-entry 判斷段落。）

- **Exit code 區分「could not analyse」與「找到違規」，比照本 repo 既有慣例（0/1/2）**：`git` 不在 PATH、目前目錄不在任何 git work tree 裡、或 patch 檔案本身讀不到，這三種都是 exit 2（scanner error），絕不能跟「這個 patch 真的套不上」的 exit 1 混在一起——見下方三個逐字驗證。
- **失敗訊息點名 patch、target file、原因**：target file 透過 `git apply --numstat` 取得（跟 `run_mutant.py` 自己的 `git_apply_touched_paths()` 同一招——用 git 自己的 header parser，不是文字層級解析 `diff --git a/<path> b/<path>` 那一行，後者可以被惡意或壞掉的 patch 弄得跟真正套用的路徑不一致），reason 直接引用 git 自己的 stderr。
- **`--selftest` 模式**：仿照 `scripts/check-e2e-test-integrity.py` 同一種形狀——在一個全新建立的 scratch git repo（tempfile，不是本 repo 任何既有檔案）裡放一個 fixture 檔與兩個合成 patch（一個 context 對得上、一個對不上），加一個刻意不存在的「missing.patch」，逐一斷言：乾淨 patch 套用成功（positive control）、context 對不上的 patch 回報 violation 且訊息同時點名 patch 路徑與 target file、missing patch 回報 scanner problem 而非 violation、`check_git_usable()` 在真實 repo 裡回報乾淨、把 `PATH` 指到一個保證沒有 `git` 的空目錄時正確回報 git 不可用。已接進 `mutation-gate.yml` 的 `changes` job，`--selftest` 先跑、`set -e` 確保它失敗會擋下真掃描——跟 `#917`（上方 e2e-integrity lint）同一種接法。
- **沒有加任何依賴**：只用 `subprocess` + `git`，符合本 repo `changes` job guard 一律 stdlib-only 的既有慣例（這個 runner 曾被發現沒裝 PyYAML）。

### RED-before-green，逐字擷取（不是憑記憶宣稱，兩次都是本次工作階段實際執行）

**手法**：在 `FileAttachmentSaveChangesGuard.cs`（`fileattachmentguard985-principal-key-check-neutralize.patch` 唯一的 target file）裡、該 patch 自己的 fixed context 範圍內（`var principalKey = fk.PrincipalKey;` 與 `if (!IsCanonicalFileAttachmentPrincipalKey(...` 之間）插入一行探針註解，刻意重現「無關編輯落在另一個 patch 的 context 裡」這個形狀本身——不是編出來的假設，是 `#1000` 對 `#985` 真的做過的同一種動作，只是這次是刻意、暫時、且會被還原的。

**RED**（`python3 scripts/check-mutant-entries-parse.py`，樹被探針行擾動後，逐字）：

```
::error::1 of 66 mutant patch file(s) under test/mutants/patches do NOT apply to the current tree (named above, one per line, each with its target file and git's own reason) -- test/mutants/run_mutant.py's apply_patch() will raise GateError(VERDICT_PATCH_DID_NOT_APPLY) for each of these the moment its entry is selected, failing the mutation-gate 'mutants'/'meta-selftest' job. The most common cause (issue #1005): an unrelated commit -- often from another PR whose own CI could not see this patch at all, since Gitea PR CI checks out refs/pull/N/head, never a merge ref (docs/ci-operations.md) -- edited a line INSIDE one of these patches' fixed context. Regenerate the patch(es) named above against the current tree.
INFO: 0 orphan patch file(s) under test/mutants/patches (every patch is referenced by an entry).
test/mutants/patches/fileattachmentguard985-principal-key-check-neutralize.patch does not apply to the current tree (target: src/WalkingTec.Mvvm.Core/FileAttachmentSaveChangesGuard.cs) -- error: patch failed: src/WalkingTec.Mvvm.Core/FileAttachmentSaveChangesGuard.cs:335
error: src/WalkingTec.Mvvm.Core/FileAttachmentSaveChangesGuard.cs: patch does not apply
EXIT CODE: 1
```

（同一時間直接跑 `git apply --check test/mutants/patches/fileattachmentguard985-principal-key-check-neutralize.patch` 本身確認一致：`error: patch failed: ...:335` / `error: ...: patch does not apply`，exit 1——不是這支腳本自己編出來的訊息，是 git 本身的判定，腳本只是原樣帶出來加上 target file 標註。）

**還原**（`git checkout -- src/WalkingTec.Mvvm.Core/FileAttachmentSaveChangesGuard.cs`）後 **GREEN**（同一支腳本，逐字）：

```
INFO: 0 orphan patch file(s) under test/mutants/patches (every patch is referenced by an entry).
OK: all 76 mutant entry file(s) under test/mutants/entries parse and validate; all 66 mutant patch file(s) under test/mutants/patches apply cleanly to the current tree.
EXIT CODE: 0
```

### Exit code 三個 precondition 案例，逐字驗證（criterion 3：不得跟「patch 真的套不上」混在一起）

**Case A：目前目錄不在任何 git work tree 裡**（在 repo 外的一個乾淨 scratch 目錄跑，該目錄底下複製了 `scripts/`、`test/mutants/run_mutant.py`、一份 entry、一份 patch，但沒有 `.git`）：

```
::error::guard scanner: current directory is not inside a git work tree: fatal: not a git repository (or any of the parent directories): .git
EXIT: 2
```

**Case B：`git` 不在 `PATH` 上**（`PATH` 指向一個只放了 `python3` symlink、保證沒有 `git` 的目錄，在一個真實的 scratch git repo 裡跑）：

```
::error::guard scanner: 'git' executable is not available on PATH: [Errno 2] No such file or directory: 'git'
EXIT: 2
```

**Case C：patch 檔案本身讀不到**（對 `fileattachmentguard985-...patch` 執行 `chmod 000`，在本次工作用的 worktree 裡直接跑）：

```
::error::test/mutants/patches/fileattachmentguard985-principal-key-check-neutralize.patch: patch file is not readable (missing or permission denied)
::error::1 of 66 mutant patch file(s) under test/mutants/patches could not even be analysed (shown above) -- whether they apply to the current tree is UNKNOWN, not confirmed clean, and must not be reported as either. Distinct from a patch that genuinely does not apply (exit 1) -- acceptance criterion 3.
EXIT: 2
```

三案例皆 exit 2，逐字確認訊息本身也沒有借用「does not apply」這個 exit-1 專屬措辭；`chmod 644` 還原權限後重跑，回到 exit 0。三個測試都在完成後清理（scratch 目錄整個刪除；worktree 內的權限與檔案內容都還原、`git status`/`git diff --stat` 確認乾淨）。

### `--selftest` 本身不是裝飾——用 mutation 證明過會失敗

比照本 repo「mutation 證據必須實測，不能只憑手動宣稱」的既有原則：暫時把 `check_patch_applies()` 判斷「套不上」的分支改成 `if False:`（永遠不回報 violation），重跑 `--selftest`：

```
SELFTEST FAILED:
  - negative control: a patch whose context no longer matches the tree must be reported as a VIOLATION (exit-1 class), never a scanner problem (got violation=None, scanner_problem=None)
EXIT: 1
```

確認 `--selftest` 真的會抓到邏輯被破壞，不是一支永遠印 OK 的裝飾腳本。還原後 `--selftest` 重跑回到 `SELFTEST OK`（exit 0）。

### `selftest`-kind mutant entry：考慮過，判斷不加

`test/mutants/entries/*.json` 裡 `kind: "selftest"` 的 entry，消費方式是 `run_mutant.py --mutant <id> --expect-verdict <V>`——用來對 `run_mutant.py` 自己的 verdict 邏輯（build failure、baseline-not-green、scope bypass 等）做端對端回歸測試，走的是「真的 apply patch → 真的 build → 真的跑 test → 比對 runner 自己回報的 verdict」這整條路徑。`scripts/check-mutant-entries-parse.py` 是完全獨立的腳本，從不呼叫 `run_mutant.py`，也不透過 entry 的 `kind` 欄位驅動任何行為——一個 `kind: selftest` entry 不會執行到本票新增的任何一行程式碼，加了也測不到東西。正確、且已經實作的自我驗證機制是這支腳本自己的 `--selftest`（見上方）——跟 `check-e2e-test-integrity.py`、`check-jwt-key-literal-blocklisted.py`、`test/mutants/_selftest/*.py` 那幾支既有 `changes`-job guard 使用的同一種形狀，不是 mutant-entry 機制。

### 誠實揭露的範圍（不宣稱防住整個缺陷類別）

這個修法**不**防住 #1005 這個缺陷類別本身。它防住的是：(1) 一張 PR 自己的 commit 打壞自己某個 patch 的 fixed context——在那張 PR 自己的 CI 裡，比 `mutants`/`meta-selftest` job 更早、更便宜地擋下；(2) 合併到 `dotnet10` 之後的每一次 `push`——這時全樹已經是合併後的真實狀態，沒有第二張還沒合併的 PR 需要看不到。它**沒有、也不可能**防住 #1005 本身發生的那種跨分支情況：兩張 PR 各自獨立看都是綠的，只有兩者都合併之後才會衝突——因為 Gitea 的 PR CI checkout 模型下，沒有任何一次 CI 執行會同時看到兩個還沒合併的分支的樹。這是這個 repo checkout 機制本身的結構性限制，不是這支腳本能從單一 PR 的 job 裡解決的東西。**誠實的說法是「比 gate 早一個合併週期擋下，且對單分支情況完全防住」，不是「防住 #1005 這一整類缺陷」。**

### 未能驗證的部分

本次工作階段的硬性限制禁止呼叫任何 Gitea/GitHub API、禁止開 PR，因此這個修法尚未在真正的 Gitea Actions CI 上跑過——本機驗證只到：`python3 scripts/check-mutant-entries-parse.py --selftest`/真掃描、上方逐字擷取的 RED/GREEN 與三個 precondition 案例、`python3 -c "import yaml; yaml.safe_load(...)"` 與 `scripts/audit-workflow-timeouts.py`（133/133 real-work step 仍全部帶 `timeout-minutes`，本票只改了既有一個 step 的 `run:` 內容與周圍註解，沒有新增 step，數字不變）。這個 checkout 機制本身（`refs/pull/N/head` vs. merge ref）的查證，是讀 `docs/ci-operations.md` 既有記錄，不是本次重新在真實 Gitea PR 上觸發驗證——該文件本身的紀錄是本次工作階段之外、既有的既有事實。

---

## `10.21.0` 版號作廢：非 release 分支的驗證建置佔用了發行版號，經由機器層級的 NuGet global-packages 目錄外溢（#1006，2026-08-03）

下游（BMS）回報 `10.21.0` 正式版缺少 `10.21.0-rc.2` 有的三個 security fix。**複驗結論：現象屬實，但成因與原本假設的「後來的發版把修正弄丟」相反，而且傳播通道不是 NuGet feed。**

### 已用指令驗證的事實

- **feed 乾淨**。Gitea packages API（`/api/v1/packages/chiu0831?type=nuget`）列出六個套件的全部版本，最高一律是 `10.21.0-rc.2`。`10.21.0`、`10.21.0-rc.7`、`99.0.0-rc.5`、`99.0.0-smoketest` 都不在上面。repo 內亦無任何 `10.21.0` / `99.0.0` 的 git tag。
- **時序與原假設相反**。`~/.nuget/packages` 內各版本的寫入時間：`99.0.0-rc.5` 07-31 07:33、`10.21.0-rc.7` 07-31 07:44、`99.0.0-smoketest` 07-31 09:06、**`10.21.0` 07-31 20:25**、`10.21.0-rc.1` 08-01 23:56、`10.21.0-rc.2` 08-02 01:12。那顆 `10.21.0` **早於兩個 rc**，是驗證性建置直接沿用了當時分支上 `version.props` 的裸版號，rc 後綴是隔天真正發版流程才加的。
- **內容與來源**（nuspec `repository` 屬性 + `strings` 對組件比對完整型別名）：`10.21.0-rc.2` 來自 `refs/heads/dotnet10` `7e0d99b78`，含 `FileAttachmentSaveChangesGuard`×3、`UnresolvableFileAttachmentReferenceException`×1；`10.21.0` 來自 `refs/heads/docs/958-advisory-issue-keyed-corrections` `b0e4ebc02`，兩者皆 0；`10.21.0-rc.7` 來自 `refs/heads/ci/925-release-gate` `18a359ca0`，兩者皆 0。
- **傳播通道是 global-packages 目錄，不是 feed**。WTM 與下游專案在同一台機器同一使用者下共用 `~/.nuget/packages`；NuGet 解析版本時先看這個目錄，命中就不連任何 source。這解釋了下游觀察到的表面矛盾：`dotnet list package --outdated` 正確回報「沒有更新」（它查 source），而污染產物其實只差把版本約束改成 `10.21.0` 就會被離線吃進去。

### 這個機制是實測的，含 positive control

拿一個「本機 cache 有、feed 沒有」的版本 `10.13.17`，在 `nuget.config` 寫 `<packageSources><clear /></packageSources>`（零 source）下 `dotnet restore`：

```
Restored t.csproj (in 179 ms).
```

兩個對照組確認這個「成功」確實來自 global-packages 命中，而不是 restore 根本沒檢查：

```
# cache 與 feed 都沒有的版本
warning NU1603: ... WalkingTec.Mvvm.Core 10.13.99 was not found. 10.14.0 was resolved instead.
# 隔離之後的 10.21.0
error NU1100: Unable to resolve 'WalkingTec.Mvvm.Core (>= 10.21.0)' for 'net10.0'.
```

**附帶更正**：`PackageReference` / `PackageVersion` 的 `Version="X"` 是**下限**（`>= X`）而非 exact pin（exact 要寫 `[X]`）；上面 NU1603 那行即是證據。因此「下游是 exact pin 所以安全」這個推理不成立——今天安全的真正原因是 NuGet 取「滿足約束的最低版本」，而 `10.21.0-rc.2` 存在。

### 已處理

`~/.nuget/packages` 下 22 個「從未發布卻佔用發行版號」的條目（四個版本 × 涵蓋到的套件）已移到 維護者本機的一個隔離目錄（路徑不記在此，屬營運環境細節）——**move 而非 delete，可逆**。動手前確認全樹沒有任何 `.props` / `.csproj` / `.config` 引用這四個版本。隔離後 live cache 上限與 feed 一致。

> 值得記一筆：那顆污染的 `10.21.0` 只涵蓋 Core / Etl / Mvc / LayUI 四顆，S3 file handler 套件（已於 10.23.0 依 #1054 移除）與 `WorkFlow` 沒有——而下游引用的正好是 Core / Mvc / LayUI 三顆，全中。缺的那兩顆會讓 restore 直接 NU1100 失敗（大聲），有的那三顆才會靜默降級。

### `10.21.0` 作廢而不補發，理由

即使 feed 乾淨、本機已隔離，**至少曾有一台機器在 `(WalkingTec.Mvvm.Core, 10.21.0)` 這個身分底下放過一份不同的組件**，且無法證明沒有其他副本。NuGet 的 `(id, version)` 是不可變身分，cache 命中不會重新驗證；補發一顆「正確的 `10.21.0`」會讓同一版號在已快取舊版的機器上長期對應兩份不同二進位，比現況更糟——現況至少會 NU1100 大聲失敗。`version.props` 已是 `10.22.0`，走這條路不需額外動作。

### 尚未處理，且這裡明講

- **防止再犯沒有做**。非 release 分支的 `dotnet pack` 仍可使用裸的發行版號。#925 的 branch guard 擋的是 publish 這一步，擋不到「pack 到本機 + 被 restore 撿走」這條路；guard 自身還有 #1008 記錄的 ref 涵蓋問題。這需要改 CI，另案。
- **只掃了這一台機器**。沒有掃描任何其他環境，沒有證明 `10.21.0` 不存在於其他副本。上一節的作廢建議正是建立在「無法證明不存在」之上，不是建立在「已證明外流」之上。
- 隔離動作沒有跑一次下游專案的完整 restore 來確認不受影響；依據是「全樹無引用」的靜態掃描與下游目前釘在 `10.21.0-rc.2`（該版本仍在 cache 與 feed 上）。

---

## 下游 production 版本記載錯誤，且錯的值決定了兩件下游工作的範圍（#938，2026-07-31）

`docs/release-adoption-ledger.md` 對某個下游 production 版本的記載是錯的，錯在樂觀方向。**具體版本號、三份互相矛盾的來源、以及可重跑的鑑別方法，全部記在該帳本內，本文件刻意不重複**——理由與本文件開頭那條規則相同：下游的部署具體資訊屬於該帳本，且該帳本不進公開 mirror（`.sync/github-excludes.txt`）。

**這裡只記與框架自身流程有關、且可公開的那一半：**

- 那一欄**從寫下第一天起就標著「沒有任何自動化或即時方式可以確認」**，而它仍然被當成範圍依據使用——升級指南的範圍與一份 EOS 分析的基準都建立在它上面。**hedge 讓錯誤可更正而非靜默，這部分有效；它沒有、也無法阻止一個被正確標記為不可靠的值靜靜決定其他工作的形狀。**
- 由此新增一條 SOP（寫在該帳本的維護章節）：不可驗證的事實不得作為其他工作的範圍依據——要求可重跑的查證方式，且**引用端**要自己標明前提未驗證，不能只依賴來源文件的 hedge（讀者往往只讀結論那一欄）。
- **方法論教訓（比版本號本身重要）**：三條「互相獨立且一致」的證據同時錯，因為作者共用同一個資訊環境與同一個可能的誤解。判斷一致性有沒有價值，要問「這些來源會不會一起錯」，不是「有幾個來源」。找的應該是**機制上不同**的來源（資料庫 schema 不受任何人的信念影響）。
- 一併更正了 `CHANGELOG.md` 一則舊條目裡同一個錯誤數字，原句保留在日期註記內。該條目描述的決策不受影響——它建立在「該下游遠落後」之上，更正後的數字讓這個前提更成立。
## `FileAttachmentSaveChangesGuard` 的同一 unit-of-work 信任集不檢查租戶：裁決為已揭露限制，附具名前置條件（#987，2026-08-03）

`IsTrustedSameUnitOfWork` 只比對兩件事：candidate 的 id 是否出現在本次 `SaveChanges` 的 `Added` `FileAttachment` 集合，以及該 entry 的 runtime type 是否可指派給 FK 宣告的 principal 型別。**`TenantCode` 不在判定內**，#985／#1000／#1011 都沒有收窄它。缺陷描述屬實。

**經設計、跨廠對抗性審查、以及對關鍵事實的自行複驗後，決定不改 guard。** 本節記錄為什麼「不修」是誠實的結論而非延後——以及一個被評估後否決的具體方案，避免下一輪從頭重推。

### 信任例外有三個維度，目前檢查兩個

**PK 存在性**（#824 原始設計：「保證會被 INSERT，因此會過 PK 檢查」）、**型別**（#978 Finding 6 補上）、**租戶**（缺）。這是最精準的框架定位。但補上第三維**關不掉這個缺口**。

### 為什麼補上租戶檢查沒有用：兩步繞過

任何只檢查「當次 dependent FK 寫入」的規則，都可以用兩次 `SaveChanges` 繞過：

1. attachment 戳 null 或攻擊者自己的租戶，dependent 指向它 → 租戶相容、信任成立、兩列合法落地
2. 只把該 attachment 的 `TenantCode` 改成受害者 → 這次沒有任何 FK candidate，guard 在 `candidates.Count == 0` 早退

最終資料庫列與「單次建立受害者租戶 attachment ＋ 攻擊者 dependent」**完全相同**。`ValidateDuplicateData`、`BaseCRUDVM`、import stamping 都只存在於各自 VM pipeline，直接 `DbSet` writer 不經過它們；樹內沒有會擋第二步的 production `SaveChangesInterceptor`。

**這個專案已經踩過同型的兩步攻擊**：`BaseCRUDVM` 的刪檔註解自己寫著「第一請求偽造引用、第二請求再刪除」會擊穿只看既存引用的防線，所以 sink 端的 tenant-scoped resolution 才是主控制。在 `Added` 集合上加租戶檢查，是重複同一個架構錯誤。

**真正缺的是 tenant-transition invariant**：資料庫的 FK 只含 attachment ID；principal 的租戶一旦可變，所有既存 dependent 都可能被重新分類。**只檢查當次的 dependent FK write，架構上不可能維護這個反向不變式。**

### 被評估並否決的方案，及否決理由

提案為「只在兩邊租戶皆非 null 且不同時撤銷信任」（null 一律放行），理由是 `WtmFileProvider` 從 ambient 身分戳租戶（#988 未修），背景工作合法產生 null，嚴格相等會拒絕它們。

**這個相容性論據不成立，已複驗**：兩條 upload 路徑（`WtmFileProvider` 的 local 路徑、`WtmDataBaseFileHandler.UploadToDB`）都在 `AddEntity` 之後**立刻**自己 `SaveChanges()`，所以附件永遠不會以 `Added` 狀態與 dependent FK 寫入處在同一批。而 `UploadToDB` 走 `wtm.DC` 的那個真同批情形裡，附件租戶與 `dc.TenantCode` **同源**（皆為 `LoginUserInfo?.CurrentTenant`），必然相等——不是 null 那一格。

所以「恰有一邊 null 也信任」沒有任何 in-tree 合法流程支撐；現行 persisted path 對「恰一邊 null」本來就拒絕。它不是保留既有規則，是把 same-unit bypass 換成 null 的形式留下來。

其餘否決理由：

- **`OrdinalIgnoreCase` 有不安全方向**：global filter 在 SQL 端依伺服器 collation 求值，而本框架同時支援 SQL Server／MySQL／PostgreSQL／SQLite／Oracle，沒有共同 collation 語義。記憶體判定 `"TENANT_A" == "tenant_a"` 為真、case-sensitive DB 判定為假 → 會信任一筆 persisted path 解析不到的 attachment。
- **`SetTenantCode` 是 `public void`**（`EmptyContext.cs`）：能直接操作 context 的 writer 可以先把 context 租戶設成受害者，讓任何「entity 租戶 vs context 租戶」的比較直接成立。該比較只在「請求能控制 entity、但不能控制 context」這個很窄的前置條件下有意義。
- **同 ID 的多筆 TPC added type 被壓成一個值**：信任集是 `Dictionary<Guid, Type>`，以賦值覆蓋；TPC 的 concrete tables 可有相同 ID，結果取決於 ChangeTracker 列舉的最後一筆。把租戶塞進同一個 dictionary 會延續這個不確定性。
- **`SaveChanges` 不是完整的寫入邊界**：`ExecuteUpdate`／`ExecuteDelete` 完全繞過 ChangeTracker，現有防線只是一支可被 alias 拆解規避的文字掃描測試。

### 具名前置條件（本節的可操作結論）

> **Host 存在一條可讓請求控制 `FileAttachment` 之 persisted fields、但不能控制 context tenant、也不能使用 bulk 或 raw SQL 的自訂寫入路徑。**

**in-tree 沒有證明這個前置條件成立**：inline 編輯明確 block `TenantCode`；`BaseCRUDVM` 的 Add 路徑強制覆寫成當前租戶；`BaseImportVM` 主列與子列同樣 stamp；CodeGen 產生的 MVC／API controller 都走 `BaseCRUDVM.DoAdd`/`DoEdit`——但「走 `BaseCRUDVM`」本身不是理由，真正的機制是 `DoAddPrepareCore` 那段刪重迴圈（自己的註解寫著「将所有TopBasePoco的属性赋空值，防止添加关联的重复内容」）：在 `DC.Set<TModel>().Add(Entity)` 呼叫前，把 `Entity` 上任何 `TopBasePoco` 型別（含 `FileAttachment`）的巢狀導覽屬性設為 `null`，讓呼叫端貼上去的巢狀 `FileAttachment` 物件（連同其 `Path`）從未進入 EF 的 graph tracking，因此連 INSERT 都不會發生——不是靠租戶戳記擋下來的。這條路徑現在有測試釘住並附一個註冊 mutant（`test/WalkingTec.Mvvm.Core.Test/VM/FileAttachmentNavPropertyPersistedFieldInvariantTests1024.cs`；mutant `basecrudvm1024-doadd-fileattachment-nav-nulling-neutralize`，2026-08-03，#1024 Phase 1）。**這個機制目前只對 Add 路徑證實**：同一段迴圈也出現在 `DoEditPreparePart1`，但實測刪掉該行並不會讓對應的 Edit 測試變紅——`DC.UpdateEntity`/`AddEntity` 底層是 `Entry(entity).State = ...`，不像 `DbSet.Add()` 會做遞迴 graph walk，本來就不會把一個從未被追蹤過的巢狀物件掛進 tracking，所以 Edit 路徑此刻不是靠這行擋下攻擊，而是框架的寫入原語本身沒有 cascade 行為（診斷過程與完整推理見同一測試檔案的檔頭註解）。

**但不可升格為「下游不可達」**：`FileAttachment.TenantCode` **沒有 `[CanNotEdit]`**（只有 `[Display]` 與 `[StringLength]`），而 `BaseCRUDVM.DoEdit` 會更新任何出現在 FC、且非 ID／NotMapped／CanNotEdit 的 scalar property。下游只要手寫或生成一個 `FileAttachment` 的 CRUD VM 就可能暴露它。

### 同一前置條件下有一條更嚴重的路徑：#1024

`FileAttachment.Path` 是可偽造的 storage locator。租戶過濾器保護的是 metadata 那一列，**不是它指向的 blob**；`ResolveUnderUploadRoot` 只保證路徑落在某個 upload root 之下（目錄穿越防護），沒有租戶維度。同一個能控制 attachment 欄位的寫入端，只要建一筆**自己租戶**的合法 attachment 並把 `Path` 指向受害者的實體檔，就能經正常 `GetFile` 讀出檔案內容——步驟更少、不需要動 `TenantCode`、guard 架構上碰不到。**投入 #987 之前應先處理 #1024。**

**部署設定不是這裡的邊界，另開 #1032（2026-08-03）**：上面的推導聚焦在 `ResolveUnderUploadRoot`（local 儲存的目錄穿越防護），容易被誤讀成「只有部署方把 `SaveFileMode` 設成 local/oss，`Path` 這個欄位才有意義」。這站不住腳：`sm` 是 `_FrameworkController.Upload`（`src/WalkingTec.Mvvm.Mvc/_FrameworkController.cs`）與 CodeGen 生成的 `FileApiController.Upload`（如 `demo/WalkingTec.Mvvm.Demo/Areas/_Admin/ApiControllers/FileApiController.cs`）的公開參數，兩個 controller 都掛 `[AllRights]`——`PrivilegeFilter.cs` 對有 `[AllRights]` 標記的 action 直接跳過頁面權限判斷（`isAllRights == false` 才會呼叫 `IsAccessable`）。`WtmFileProvider.CreateFileHandler` 只要 `sm` 是非空字串就直接查 `_handlers[sm]`，完全不看部署設定的 `FileUploadOptions.SaveFileMode` 預設值；`WtmLocalFileHandler`／`WtmOssFileHandler`／`WtmDataBaseFileHandler` 三個 handler 類別都定義在 `WalkingTec.Mvvm.Core`（框架本體，任何部署都會載入），`WtmFileProvider.Init` 把掃描到的每個 `IWtmFileHandler` 實作都登記進 `_handlers`，不受 `SaveFileMode` 篩選。也就是說，即使一個部署把 `SaveFileMode` 設成 `database`，呼叫端仍能在同一次 Upload 請求上帶 `sm=local`（或 `sm=oss`）選用會賦予 `Path` 真實檔案系統／物件儲存意義的 handler——**不需要部署方特地把儲存後端設成 local/oss**。已另開 #1032 追蹤這條「`sm` 繞過 `SaveFileMode`」的獨立問題；本節與本次 #1024 Phase 1 都不修。

### 明確沒有做的事

- 沒有改 `FileAttachmentSaveChangesGuard` 任何一行；沒有新增測試或 mutant（沒有行為變更可測）；**沒有 CHANGELOG 條目**（沒有變更可宣稱）。
- **沒有實際建構任何一個攻擊並執行。** 上述判定全部來自靜態追蹤，包含兩步繞過——「兩步繞過成立」是對控制流的推導，不是實測。
- 沒有查證是否有下游真的暴露 `FileAttachment` 的 CRUD 端點。這決定嚴重度，目前**未知**。
- **TPC base-type principal 的 FK 是否會產生真正的 relational 約束，未驗證。** 這一格決定「既有 attachment 能不能被冒充」；repo 現有的 TPC fixture 只涵蓋 derived-principal FK。要驗證需新增一支 SQLite（FK 開啟）fixture，把 dependent navigation 宣告成 base `FileAttachment`，檢查 `GenerateCreateScript()` 再實跑。在那之前，**不宣稱 EF 一定會或一定不會產生 FK**。

### 何時該重新評估

任一條成立即應重開：#1024 修好之後（前置條件的成本結構改變）；普通已驗證請求的可達性被證明；或 `FileAttachment.TenantCode`／`Path` 取得建立後不可變的語意（屆時 transition policy 已存在，反向不變式才有地方掛）。

---

## `WtmFileProvider.GetFileCore`/`DeleteFileCore` 的投影遺漏 `HandlerInfo`：多群組 OSS 部署下讀寫可能落錯 bucket（#1028，2026-08-03）

### 缺陷

`WtmFileProvider.cs` 的 `GetFileCore`（`.CheckID(id).Select(x => new FileAttachment {...})`）與 `DeleteFileCore`（同形狀，另一份手寫欄位表）各自用一段手動維護的 `Select` 投影重建 `FileAttachment`，理由正當——避免載入 `FileData`（`byte[]`）這個大欄位。但兩份欄位表都**遺漏 `FileAttachment.HandlerInfo`**：

- `GetFileCore`（修法前）：`ID, ExtraInfo, FileExt, FileName, Length, Path, SaveMode, UploadTime`
- `DeleteFileCore`（修法前）：`ID, ExtraInfo, FileExt, FileName, Path, SaveMode, Length, UploadTime`

兩份欄位表其實是同一個 8 欄位集合（只有列出順序不同），**沒有互相矛盾**——但這正是 issue 要指出的問題：兩份手寫清單已經各自漂移過一次（都漏了 `HandlerInfo`），彼此卻剛好一致，代表沒有任何機制擋得住下一次漂移，"看起來一致" 不等於 "有東西在保證一致"。

`HandlerInfo` 是 `IWtmFile` 介面的一部分（`Models/IWtmFile.cs`），`WtmOssFileHandler.GetFileData`/`DeleteFile` 用它挑 OSS 群組/bucket（`FileHandlerOptions.GroupName`），找不到值時 fallback 成 `ossSettings?.FirstOrDefault()`。因為投影從未複製這個欄位，`WtmFileProvider.GetFile`/`GetFileTenantScoped`/`DeleteFile`/`DeleteFileTenantScoped` 回傳的物件 `HandlerInfo` **永遠是 null**，不論資料庫裡實際存了什麼——在只有一個 OSS 群組的部署下這條 fallback 剛好命中正確答案，缺陷完全不可見；一旦部署設定第二個群組，讀取會去錯的 bucket 找檔案，刪除會對錯的 bucket 發 `DeleteObject`。對不存在的 object 發 delete 通常回成功，所以「刪錯 bucket」跟「刪對 bucket」在呼叫端看起來一模一樣，該刪的檔案其實還留著。

### 修了什麼

**新增 `HandlerInfo` 到兩處投影，並把欄位表收斂成單一來源**——這是本 issue 真正要處理的點，不只是補一個欄位。`WtmFileProvider.cs` 新增一個 `private static readonly Expression<Func<FileAttachment, FileAttachment>> _fileMetadataProjection`，內容是收斂後的 9 個欄位（原 8 個 + `HandlerInfo`）；`GetFileCore`／`DeleteFileCore` 都改成 `.Select(_fileMetadataProjection)`，不再各自手寫。以後要在這個投影加/拿掉欄位，只有一個地方要改，兩個呼叫點不可能再各自漂移。

**EF Core 轉譯證據**：`_fileMetadataProjection` 的宣告型別是 `Expression<Func<FileAttachment, FileAttachment>>`（不是編譯過的 delegate），`IQueryable<T>.Select` 的多載本來就吃這個型別——把已經是這個型別的欄位直接傳進 `Select(...)`，C# 編譯器不會另外包一層 lambda，EF Core provider 看到的運算式樹跟直接把 lambda 打在呼叫點是同一份。這不是理論推導：`test/WalkingTec.Mvvm.Core.Test/Security/WtmFileProviderHandlerInfoRoundTripTests1028.cs` 用 SQLite shared-memory fixture（`Microsoft.Data.Sqlite`，非 EF InMemory——InMemory 的 LINQ-to-objects 求值會放行任何 C# 投影，包含 EF 真正 provider 轉譯不了的寫法，綠燈證明不了轉譯真的成立）實際跑過 `GetFileCore` 這條路徑，SELECT 確實被 SQLite provider 執行且回傳正確值（見下方 RED/GREEN）；`DeleteFileCore` 用的是同一個 `_fileMetadataProjection` 物件（reflection 可驗證兩個呼叫點指向同一個靜態欄位），沒有另外查證的必要——它不是「形狀相同的另一份運算式」，是同一個物件參照。

### 測試

**釘點測試 v1（`test/WalkingTec.Mvvm.Core.Test/Support/WtmFileProviderProjectionFieldSetTests1028.cs`，前 3 支）**：用 reflection 取出 `WtmFileProvider._fileMetadataProjection` 這個 private static 欄位，把它的 `MemberInitExpression.Bindings` 轉成成員名稱清單，用 `CollectionAssert.AreEquivalent` 跟一份**寫死**的期望集合比對——刻意用集合比對，不是數量比對：數量比對在「拿掉一個欄位、換成另一個不相關欄位」時一樣會過，抓不到 #1028 這種「數量沒變、內容錯了」的漂移。另外兩支測試分別釘住「必須包含 `HandlerInfo`」與「必須不包含 `FileData`」——後者是護住投影存在的理由本身：這個修法不能順便把大欄位載入的問題也修沒了（變成退化成整列 entity load）。

**這三支測試本身有一個結構性缺口，經 review 抓到並已補上**：三支都是拿投影去對一份**人工寫死**的 `ExpectedFields`，而 `ExpectedFields` 本身不會自動追蹤 `FileAttachment` 的真實形狀——這正是產生 #1028 的同一種缺陷（人工維護的清單，沒有機制保證跟著源頭走）。如果明天有人在 `FileAttachment` 上加一個新欄位卻沒決定它該不該進投影，這三支測試會**全部維持綠燈**，因為它們比對的是彼此，不是比對 `FileAttachment` 本身。

**釘點測試 v2 —— 真正防止再犯的那一支（同檔案，第 4 支：`FileAttachmentMappedScalarProperties_EqualsProjectionUnionDeliberateExclusions`）**：不比對寫死清單，改成直接對 `typeof(FileAttachment)` 做 reflection，推導出「目前實際存在的 mapped scalar 屬性集合」，斷言它等於「投影欄位」∪「`DeliberatelyExcludedFields`」（後者是一個具名字典，目前只有兩項：`FileData`——原因是這正是投影存在的理由；`TenantCode`——原因是它驅動 EF Core 的 `ITenant` 全域過濾器，`Select` 執行前就已經套用，不屬於任何呼叫端消費的 `IWtmFile` contract；兩項各自附理由字串）。任何既不在投影裡、也不在 `DeliberatelyExcludedFields` 裡的欄位都判定為「UNDECIDED」，斷言失敗訊息直接告訴下一個人怎麼做：加進投影，或加進 `DeliberatelyExcludedFields` 並寫理由。

Reflection 細節（依 review 要求逐項處理）：
- **繼承鏈**：`Type.GetProperties(BindingFlags.Public | BindingFlags.Instance)` 對**類別**預設就會走完整 base chain（這正是抓到 `TopBasePoco.ID` 的機制）——這裡刻意記錄一個相關但不同的坑：`Type.GetMember` 在**介面**上呼叫時**不會**沿 base interface 往上找（WTM 自己另一個缺陷 `type-getmember-on-interface-skips-inherited` 記過同一個 gotcha），但 `FileAttachment` 是類別不是介面，不適用那個坑，這裡的寫法是對的。
- **`[NotMapped]`**：用 `GetCustomAttribute<NotMappedAttribute>() == null` 過濾，排除 `TopBasePoco.Checked`/`BatchError`/`ExcelIndex`/`IsBasePoco` 與 `FileAttachment.DataStream`。
- **Navigation／collection 屬性**：`FileAttachment` 目前沒有任何一個，但方法本身要能正確分類——`IsNavigationOrCollectionProperty` 對 `string`／`byte[]`（兩者都實作 `IEnumerable` 但都是合法的 EF 純量欄位型別）明確排除在「集合」判定之外，其餘任何實作 `IEnumerable` 的型別視為集合 navigation；非集合的一般 struct/enum/`Nullable<>`（`Guid`/`DateTime`/`decimal`/...）視為純量；任何其餘 class（例如 `Stream`）視為 navigation-shaped，不計入純量集合。

**Mutation 證明，不只是斷言——逐字擷取**：在 `FileAttachment.cs` 暫時加一個丟棄用純量屬性 `public string? Wtm1028ThrowawayProbe { get; set; }`，重跑：

```
Failed FileAttachmentMappedScalarProperties_EqualsProjectionUnionDeliberateExclusions [3 ms]
Error Message:
 Assert.Fail failed. #1028: FileAttachment's mapped scalar properties no longer match (_fileMetadataProjection's fields) UNION (DeliberatelyExcludedFields). UNDECIDED — these FileAttachment properties are mapped/scalar but appear in NEITHER the projection NOR DeliberatelyExcludedFields: [Wtm1028ThrowawayProbe]. For each: either add it to WtmFileProvider._fileMetadataProjection if callers/handlers need it, or add it to DeliberatelyExcludedFields in this test with a reason — this exact 'silently neither' shape is the #1028 regression.
Total tests: 5
     Passed: 4
     Failed: 1
```

其餘 4 支（含舊版 3 支寫死清單測試）維持綠燈——證明新測試量測的是新加欄位本身，不是連帶弄壞了別的東西。移除該屬性後重跑：`Total tests: 5, Passed: 5`，`git diff --stat FileAttachment.cs` 確認檔案回到與上一次 commit 逐位元組相同（該屬性未被提交）。

**釘點測試 v3 —— 投影「身分」測試（同檔案，第 5 支：`GetFileCoreAndDeleteFileCore_BothReferenceTheSameSharedProjectionField`）**：v1/v2 都只驗證投影**內容**，沒有驗證 `GetFileCore` 與 `DeleteFileCore` 是不是真的指向**同一個**運算式物件——理論上有人可以讓兩份測試都維持綠燈，做法是給 `DeleteFileCore` 另外宣告一個欄位列表完全相同、但獨立存在的 `Expression<Func<FileAttachment, FileAttachment>>`，這樣悄悄地把 #1028 要根除的「兩份手寫清單」形狀原樣複製回來（只是兩份現在恰好同步，跟修法前的狀態一樣脆弱）。這支測試對 `GetFileCore`/`DeleteFileCore` 的 IL（`MethodBody.GetILAsByteArray()`）做位元組掃描，找 `ldsfld`（opcode `0x7E`，永遠是單位元組、後接 4 位元組 metadata token，沒有短版本）指令，把 token 解析回 `FieldInfo`（`Module.ResolveField(token)`），確認兩個方法的 IL 裡都有一個 `ldsfld` 解析回同一個 `_fileMetadataProjection` 欄位。這不是完整 IL decoder（沒有追蹤指令邊界，理論上另一條指令的 operand 位元組剛好等於 `0x7E` 會被誤判成 `ldsfld`），但風險是單向的——`ResolveField` 對不是真正欄位 token 的位元組序列通常直接丟例外（被 catch、繼續掃描），巧合解析成剛好等於目標欄位的機率可忽略，所以誤判只可能讓測試「太寬鬆」而非「太嚴格」，下面的 RED 證明排除了目前這兩個方法形狀下真的發生這種情況。

**這支測試的 RED 證明**：把 `DeleteFileCore` 暫時改回它自己獨立的內聯 `Select(x => new FileAttachment {...})`（欄位列表刻意保持完全相同，包含 `HandlerInfo`，只是不再是同一個物件參照），重跑：

```
Failed GetFileCoreAndDeleteFileCore_BothReferenceTheSameSharedProjectionField [3 ms]
Error Message:
 Assert.IsTrue failed. #1028: DeleteFileCore's IL no longer references the shared _fileMetadataProjection field — if it was changed to build its own inline Select(x => new FileAttachment { ... }) projection (even one with an identical field list), that silently reintroduces the two-hand-maintained-lists shape #1028 fixed. Route it back through _fileMetadataProjection.
```

其餘 4 支（含 v2 的欄位集合測試）維持綠燈——因為欄位集合本身沒有變，只有「是不是同一個物件」變了，證明這支測試量測的是 v1/v2 都量測不到的維度。還原 `DeleteFileCore` 後重跑：`Total tests: 5, Passed: 5`；`git diff --stat WtmFileProvider.cs` 確認回到與上一次 commit 逐位元組相同。

**Round-trip 測試**（`test/WalkingTec.Mvvm.Core.Test/Support/WtmFileProviderHandlerInfoRoundTripTests1028.cs`，2 個測試，SQLite shared-memory fixture）：`SaveMode="database"`（不需要真的 OSS endpoint、不碰網路）種一筆帶 `HandlerInfo` 的 `FileAttachment`，呼叫 `WtmFileProvider.GetFile(...)`，斷言回傳物件的 `HandlerInfo` 等於種下去的值；正控組斷言修法前就已經在投影裡的欄位（`FileName`、`ExtraInfo`）沒有被這次改動動到。

**RED-before-fix，逐字擷取**（暫時 `git stash` 掉 `WtmFileProvider.cs` 的修法、只留新測試檔案，重新 build + 跑）：

```
Failed GetFile_PersistedHandlerInfo_SurvivesProjectionRoundTrip [713 ms]
Error Message:
 Assert.AreEqual failed. Expected:<group-get-5681756506834f418301bdef4f49e000>. Actual:<(null)>. #1028: HandlerInfo must survive GetFileCore's Select projection. Before the fix, the hand-maintained projection never copied this column, so HandlerInfo was always null regardless of what was persisted — the exact defect that made WtmOssFileHandler fall back to the first configured OSS group/bucket instead of the one the file actually belongs to.
Failed!  - Failed:     1, Passed:     1, Skipped:     0, Total:     2, Duration: 744 ms
```

（同一次跑，正控組 `GetFile_PersistedMetadata_OtherFieldsStillSurviveProjection` 維持綠燈——證明 fixture 本身沒問題，紅的只有斷言 `HandlerInfo` 的那一支，紅的理由跟預期完全對上。）`git stash pop` 還原修法後，同一組 filter 重跑：`Passed! - Failed: 0, Passed: 5, Skipped: 0, Total: 5`（含當時已存在的釘點測試 3 支；v2/v3 兩支是 review 之後才補上的第二輪）。

**範圍聲明的更新——`DeleteFileCore` 現在有一支「身分」測試，而不只是作者本人的觀察**：初版報告寫「reflection 已證明兩個呼叫點指向同一個靜態欄位」，但那句話當時只是作者互動式驗證過、沒有寫進任何測試——review 正確指出這樣不算數，晚一點有人把其中一個呼叫點改回內聯 lambda 也不會有任何測試變紅。現在已經是上面的釘點測試 v3，帶 RED/GREEN 逐字證明。`DeleteFileCore` 仍然沒有獨立的、透過真正 OSS/database handler 觀察 `HandlerInfo` 傳遞的 round-trip 測試——`DeleteFileCore` 是 `void`，沒有可觀察的回傳值；要做到那個層級的驗證，唯一辦法是透過 `WtmFileProvider` 的 private、process-wide static handler registry（`_handlers`/`_defaultHandler`，由 `WtmFileProvider.Init` 寫入）注入一個會記錄收到值的假 handler——這是跨測試共享的可變靜態狀態，會在同一個測試 process 裡的其他測試之間互相汙染，判斷這個風險大於多驗證到的信心。但「`DeleteFileCore` 用的是跟 `GetFileCore` 同一個運算式物件」這件事本身，現在是被測試強制的，不再只是作者的觀察。

### 其他 `FileAttachment` 投影site全樹核對

`grep -rn "new FileAttachment"` 與 `grep -rn "Set<FileAttachment>()"` 全樹掃過 `src/`、`demo/`、`test/`：沒有其他檔案用 `Select(x => new FileAttachment {...})` 這種部分欄位投影重建 `FileAttachment`。其餘命中都是：完整 entity load（`Utils.cs:695`、`WtmDataBaseFileHandler.cs:41`，沒有 `Select`，載入含 `FileData` 的整列）、`Select(x => x.ID)` 這種單欄位投影（`DCExtension.FileAttachmentResolution.cs`，跟這個缺陷的形狀無關）、或建立全新物件用於寫入（`new FileAttachment()` 接著逐一設 property，不是從查詢投影重建）。這個缺陷的模式（部分欄位 `Select` 投影 + 遺漏欄位）在整個 repo 裡只存在這一份程式碼、兩個呼叫點。

### Mutant：考慮過，判斷不適合，未新增

`run_mutant.py` 的 `VALID_KINDS` 只有 `security`／`selftest` 兩種。這個修法的性質更接近**正確性修復**（投影欄位表遺漏欄位）而不是傳統意義上「安全控制被繞過」的形狀——沒有身分驗證邊界被打開、沒有攻擊者能主動觸發的輸入、`HandlerInfo` 是否正確完全取決於部署設定（幾個 OSS 群組），不是任何一方可控制的輸入。consequence 雖然讀起來像資安事故（跨群組讀錯、刪錯），但成因是「設定欄位沒被複製」而非「檢查被繞過」，跟本文件裡其他 `security` kind 的 mutant（例如 #985 的 principal-key 檢查、#1011 的租戶範圍檢查）不是同一類東西：那些都是**條件判斷**被中和、可以直接寫「拿掉這個 `&&` 分支」的 compile-preserving patch；這裡沒有條件判斷可以中和——唯一能寫的「mutant」就是把 `HandlerInfo = x.HandlerInfo,` 這一行從投影裡刪掉，等於直接把已經修好的程式碼改回修法前的樣子，本質上是重複回歸測試的角色，不是額外驗證一個決策分支沒被繞過。

回歸保護已經由本節上方兩份測試檔案（5 個測試，含集合比對釘點 + round-trip）用 CI 強制的一般測試套件提供，不需要疊加 mutation-gate 的重量級機制。

### 驗證

`find . -name 'demo.db*' -path '*bin*' -delete && dotnet build src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj -c Release`：0 錯誤（既有警告不變，跟本次改動無關的既有 XML doc/nullable 警告）。`dotnet test test/WalkingTec.Mvvm.Core.Test/ -c Release --filter "TestCategory!=Integration" -m:1`：**5092 passed, 0 failed**（含本次新增 7 個測試——round-trip 2 個＋釘點測試 5 個，後者含 review 之後補上的 v2 欄位集合推導測試與 v3 投影身分測試）。`test/mutants/patches/*.patch`（68 個檔案）逐一 `git apply --check`：**68/68 通過**（review 補測試後重新核對一次，結果不變），沒有 patch 動到 `WtmFileProvider.cs`。

**未能驗證的部分（誠實列出，不是隱藏）**：本次工作階段的 HARD CONSTRAINT 禁止呼叫任何 Gitea/GitHub API、禁止開 PR，本機驗證只到 `dotnet build`/`dotnet test` 這兩層，沒有跑過真正的 Gitea Actions CI。**consequence 是追溯出來的，不是重現出來的**：沒有架設任何多群組 OSS 環境（真實 Aliyun OSS endpoint、多個 `FileHandlerOptions.GroupName`）去實際驗證「讀錯 bucket」「刪錯 bucket」——上面的因果鏈是讀 `WtmOssFileHandler.GetFileData`/`DeleteFile` 原始碼、對照 `HandlerInfo` 一路是 null 推出來的，不是對一個真的跑著兩個 bucket 的部署發過請求、看到過真的讀到 A bucket 的東西。`DeleteFileCore` 路徑仍然沒有一支透過真正的 file handler 直接觀察「`HandlerInfo` 被傳進 `fh.DeleteFile(file)` 那一刻」的 round-trip 測試（理由見上方「範圍聲明的更新」段落：唯一做法要動 `WtmFileProvider` 的 process-wide 可變靜態狀態，判斷風險大於效益）；但「`DeleteFileCore` 跟 `GetFileCore` 是不是共用同一個投影物件」這件事，第一版報告只是作者互動驗證過、沒有測試守著，這一輪已經補上（v3 IL 身分測試，RED/GREEN 皆已逐字擷取）——這是本次修正的範圍，不是新的未能驗證項目。

---

## `integration-test.yml` 的 `mssql` service container：讓引擎自報的記憶體資訊進 CI log 有明確價值，`--memory=2560m` 是 backstop 不是修法，證據指向 run 6509 是 mssql 被同容器的 build/test 搶記憶體的受害者（#1020，2026-08-03）

**完整的本機實測方法、逐項數字與其他 workflow 的排查結果在 `docs/ci-operations.md` 的
「#1020」條目——本節不重複，只記讀者判斷 production-readiness 需要的結論。**

**本節標題本身經過一次修正**：第一版標題寫「加記憶體邊界＋讓引擎自報的記憶體資訊進 CI
log」，把兩者並列成同等分量的修法。用真實測試負載覆核 `--memory=2560m` 之後（見下方），
數字顯示這個並列是錯的——記憶體邊界那一半沒有框住任何被觀察到在長大的東西，見下方
「這次改動沒有做、也不宣稱的事」。

### 缺陷與現況

Run 6509（`dotnet10@7cc2b864d`）在 `EnsureCreated()` 死於 `Error 945`
（insufficient system memory in resource pool 'internal'），同一支測試平常 957ms、
那次跑了 13 秒才死——thrashing 後放棄，不是硬 crash。**間歇性**：同一天多數 run
9/9 全過。`services.mssql` 當時完全沒有記憶體邊界，會跟同一個 job 容器（同時在
build/test）共用這台 mac-mini act_runner 背後那顆硬 **4 CPU / 3.813GiB** 的 Docker VM。

### 這次改動實際做了什麼，依實際證據價值排序（誠實邊界）

- **`Report MSSQL effective memory (issue #1020)` step——三項改動中唯一有明確站得住腳
  價值的一項。** 用 .NET 10 file-based app 查 `sys.dm_os_sys_info`，把
  `physical_memory_kb`/`committed_target_kb`/`committed_kb`/`container_type_desc` 印進
  每一次 run 的 log。這一步**不修任何東西**，只讓「引擎自己相信的記憶體狀況」對人類
  讀者可見——因為缺陷本身是間歇性的，光是「這次 CI 綠了」不構成任何證據，能讓兩者脫鉤
  的只有這一步。
- **`MSSQL_MEMORY_LIMIT_MB: 1536`——實測空跑，不宣稱有用。** 本機對同一 image/同一
  host 用 1536/768/400 三種設定量測 `sys.dm_os_sys_info.committed_target_kb`，數字沒有
  跟著往預期方向動（反而是限制越低、數字越高），且 `container_type_desc` 五次全部是
  `NONE`——**這個環境變數在這台 host 上沒有可觀測的效果**。留著只是因為免費且照文件
  建議，不是因為證實有用。
- **`--memory=2560m`——kernel 層面確實生效（容器內 `/sys/fs/cgroup/memory.max` 精確
  等於外部給的值），但是一個 backstop，不是對 run 6509 實際發生情況的修法。** 用真實
  測試負載覆核之後（見下方），這個上限沒有框住任何被觀察到在長大的東西——理由與完整
  推論見下一節。
- `sp_configure 'max server memory (MB)'` 在 azure-sql-edge 上不存在（`Msg 15123`，
  本機直接觸發過）；`docker exec` + `sqlcmd` 也不是選項（image 在 arm64 上不含
  sqlcmd，且這個 job 沒有 docker socket）——兩者都在本機對同一顆 daemon 實測排除，
  不是查文件就假設不行。

### 這次改動**沒有**做、也不宣稱的事

- **不宣稱修好 #1020 描述的 flake。** 缺陷是間歇性的，本次修法能否讓它消失只能靠往後
  多次真實 run 的觀察，這次工作階段本身完全沒有跑過真正的 Gitea Actions CI（環境的
  HARD CONSTRAINT 禁止呼叫 Gitea API、禁止開 PR）。
- **不宣稱 `MSSQL_MEMORY_LIMIT_MB` 對 azure-sql-edge 生效**——上面已經用實測數字說明
  為什麼不宣稱。
- **不宣稱 `--memory=2560m` 框住了 run 6509 實際發生的情況——用真實測試負載覆核，結論
  是它沒有。** `2560m` 最初是用 idle/startup 狀態量到的 `committed_target_kb`（約
  2.03GiB）加安全邊界推出來的；run 6509 死在第 9 個 create/drop 循環，也就是**持續
  負載之下**，剛好是 idle 數字最可能低估的情境，所以覆核了：本機對一個用最終設定
  （`MSSQL_MEMORY_LIMIT_MB=1536`、`--memory=2560m`）起的 azure-sql-edge 容器，跑兩次
  完整的 9 項整合測試（皆 9/9 通過，6.5s／7.0s），`docker stats` 全程取樣。**峰值
  MEM USAGE 約 663MiB，只有 2560m 上限的 26%**，跑完後 `committed_kb` 約 152MiB——
  **不只遠低於 2560m 上限，也遠低於 mssql 自己 idle 時的 2.03GiB 目標**。一個在真實
  負載下只用到上限 26% 的容器，不是 run 6509 裡「正在長大」的東西——這個上限沒有框住
  任何實際被觀察到在長大的東西。
- **不宣稱這代表 `--memory` 這個改動沒有意義，但它的意義是「防一個目前沒觀察到、未來
  可能出現的失控 mssql」，不是「修好 run 6509」。** 兩者是不同的宣稱，本節刻意分開。
- **不宣稱查到了 run 6509 真正的根因，但用手上的數字給出一個比「mssql 需要更多記憶體」
  更吻合的讀法：mssql 更像是受害者，不是加害者。** `Error 945` 是 SQL Server 自己的
  記憶體管理員跟系統要記憶體被拒絕；配合 `container_type_desc` 五次全部 `NONE`——引擎
  在這台 host 上不是照 cgroup 範圍看記憶體，比較像是照整台 VM 的可用記憶體判斷——這個
  形狀更吻合「mssql 是整台 VM 記憶體被搶光的受害者」，加害者候選是同一個 job 容器同時
  在跑的 `dotnet build`/`dotnet test`，跟 `#902`（testhost OOM kill，靠 `-m:1` 序列化
  解掉）是同一種形狀。逐容器獨立生效的 `--memory` 上限，對「mssql 自己沒超標、但整台
  VM 被別的容器榨乾」這件事沒有防護力。這是**從手上數字論證的讀法，不是新測出來的**
  ——見下一條，直接測這件事的量測沒有做。
- **不宣稱驗證過「mssql 會不會被同容器的 build/test 擠壓」這件事——這個更直接的量測
  規劃過，但沒有做。** 方法是讓 mssql 跑 9 項整合測試的同時，另一個容器對同一個
  solution 做完整 `dotnet build`，兩邊都取樣記憶體。沒有做的原因：投入這個工作階段的
  當下，這台機器上正有一個真實、不相關的 Gitea Actions job（`mutation-gate.yml` 的
  `mutants` job）在跑，CPU 用到 ~260%、記憶體 ~800MiB，且該 job 自己的文件記載預算上
  看 ~125 分鐘——刻意疊加一個高負擔的 build+test 去搶同一顆 4 CPU / 3.813GiB 的
  Docker daemon，代價是可能拖慢或搞壞一個真實、無關的 CI job，換來的量測品質還不見得
  乾淨。**判斷是留著不做，不弱弱做一個近似值拿來充當證據。**

### 下一步

如果之後真實 run 又出現這個缺陷：**不要往上調 `--memory` 這個數字**——上面的證據指向
問題在 build/test 那一側的資源競爭，不是 mssql 需要更多空間。該查的是同一個 job 容器
同時在做的 restore/build/test，或是這個 runner 的容量本身；`ubuntu-24.04`（Azure
overflow runner，15GiB）仍然是最終備案，但那是換一台更大的機器，不是先調這個數字。

### 其他 workflow 有沒有一樣的形狀

用 `yaml.safe_load` 逐一檢查這個 repo 全部 7 個其他 workflow 檔案
（`ci-build.yml`／`mutation-gate.yml`／`regression.yml`／`e2e-test.yml`／
`publish-nuget.yml`／`timeout-selftest.yml`／`vue3demo-build.yml`）的每個 job 有沒有
`services:` 區塊：**沒有其他檔案起任何 service container**，`integration-test.yml`
是這個 repo 唯一的一份，不需要另外開票。

### 驗證

`python3 scripts/audit-workflow-timeouts.py`：改動前 133/133（0 missing），改動後
134/134（0 missing，新增的 step 自己帶 `timeout-minutes: 5`）。
`python3 -c "import yaml; yaml.safe_load(open('.github/workflows/integration-test.yml'))"`：
解析成功。改動後的 workflow 步驟腳本（含 heredoc 產生的 `.cs` file-based app）在本機對
一個用相同設定（`MSSQL_MEMORY_LIMIT_MB=1536`、`--memory=2560m`）起的 azure-sql-edge
容器端到端跑過一次，成功印出
`physical_memory_kb=2048000 committed_target_kb=1478632 committed_kb=122352 container_type_desc=NONE`——
證明 YAML 的 heredoc 縮排、`dotnet run --file`、`Microsoft.Data.SqlClient@6.1.1`
restore 這條路徑本身是通的，不是紙上談兵。另外對 `--memory=2560m` 本身做了真實負載
覆核（細節見上方「沒有做、也不宣稱的事」）：本機 build 出
`test/WalkingTec.Mvvm.Integration.Test` 的 Release DLL，對一個用最終設定起的
azure-sql-edge 容器跑了兩次 `dotnet test ... --filter "TestCategory=Integration"`，
**兩次都 9/9 通過**（6.5s／7.0s），`docker stats` 全程取樣，峰值 MEM USAGE
約 663MiB／2560MiB（26%），跑完後 `committed_kb` 約 152MiB。

---

## 20 個 wrapped `*Func` 發射站點恢復 optional-chain 短路傳播：封閉語言分類器，fail-closed（#1034，2026-08-03）

> 本節修復的正是 #999 part (A)/(B) 與 #965 三個條目（見上方對應章節開頭）自己揭露、當時明講「沒有便宜正確修法」的那個迴歸。修法採跨廠複審（Codex `gpt-5.6-sol`：**APPROVED WITH NAMED CHANGES**，六項具名修改已逐一套用，詳見本節「六項具名修改」小節）批准的第二輪設計。

### 缺陷回顧

`#999`/`#965` 把 20 個「開發者提供的 `*Func` callback 字串」發射站點的 `X(args)` 改成 `(X)(args)`，修的是不同缺陷（statement 位置的匿名 function literal 造成 unwrapped-IIFE SyntaxError）。但加上這層括號同時**終結了 optional chain 的短路傳播**：`handlers?.onChange(data)` 在 `handlers` 為 nullish 時安全短路成無事發生；`(handlers?.onChange)(data)` 強迫這條鏈先求值出 `undefined`、再把它當函式呼叫，拋 `TypeError` 並中止整個 callback。這個差異只有**執行** JS 才看得出來——`#999`/`#965` 的測試用真 parser 解析發射結果、斷言其語法合法，而 `(handlers?.onChange)(data)` 語法完全合法，parse-only 測試抓不到這個回歸。

**§2.4 措辭更正（本修法的核心新事實）**：短路情境下的可觀察差異，不只「TypeError vs no-op」。以 `DataTableTagHelper.cs:1300`（`GridAction.OnClickFunc`，args = `ids,ff.GetSelectionData('{Id}')`）為例——`ff.GetSelectionData` 會呼叫 LayUI 的 `checkStatus`，是有副作用的呼叫：

- **wrap 形（10.22.0 現行）**：短路時，`ids,ff.GetSelectionData('{Id}')` 這兩個呼叫參數**仍會先被求值**（JS 呼叫語意：先求值全部引數，再求值 callee、再嘗試呼叫），求值完才發現 callee 是 `undefined` 而拋 `TypeError`。
- **direct 形（本修法）**：短路時，鏈上的呼叫從未發生，`ids,ff.GetSelectionData('{Id}')` 這兩個引數**完全不會被求值**。

所以本文件與 CHANGELOG 的正確措辭是：**「所有可觀察差異均限於今天會在 outer call 拋 TypeError 的短路情境；direct form 同時不會求值呼叫參數。」** 不可只寫「TypeError vs no-op」（漏了引數求值這一格），也不可寫「不存在任何 downstream 依賴現狀」——10.22.0 已釋出，這是無遙測的絕對宣稱，論證只到「不可能正常依賴現狀」，不到「沒有人這樣做」。

### 修法：封閉語言分類器，fail-closed

`BaseElementTag.FormatFuncInvocation`（`src/WalkingTec.Mvvm.TagHelpers.LayUI/Abstraction/BaseElementTag.cs:293`-`405` 一帶）新增 `IsNarrowOptionalChain` 分類器：

```csharp
private static readonly Regex _narrowOptionalChainRegex =
    new(@"^[A-Za-z_$][A-Za-z0-9_$]*(?:\??\.[A-Za-z_$][A-Za-z0-9_$]*)+\z", RegexOptions.Compiled);

private static readonly HashSet<string> _jsReservedHeads = new(StringComparer.Ordinal) { /* 43 個 JS 保留字 */ };

private static bool IsNarrowOptionalChain(string s)
{
    if (!s.Contains("?.")) return false;
    if (!_narrowOptionalChainRegex.IsMatch(s)) return false;
    int cut = s.IndexOfAny(new[] { '.', '?' });
    return !_jsReservedHeads.Contains(s[..cut]);
}
```

命中（整段字串是「ASCII 識別字原子以 `.`／`?.` 串接、至少一個 `?.`、頭原子非 JS 保留字」）→ `FormatFuncInvocation` 回傳 `$"{funcExpression}({args})"`（呼叫留在鏈內，短路傳播）；未命中 → 回傳現行 `$"({funcExpression})({args})"`（**byte-identical**，10.22.0 現行輸出）。

**為什麼不是「文字含 `?.` 就不包裹」**：那正是 `#999 part (A)` 條目自己否決過的「用字串猜 JS 文法」——一個 arrow body `(v)=>a?.b` 含 `?.` 卻絕不能 direct-append（會把整段 arrow body 錯誤地接上呼叫括號）。封閉語言分類器與 naive `Contains` 偵測的差別是**失敗方向**：naive 偵測誤判是 fail-open（把不安全的值直接拼接進可執行 JS）；本分類器誤判只可能是**漏收**（一個真的是安全鏈的值被放過，退回今天的 wrap，代價＝「跟今天一樣壞」），不可能誤收——分類器命中的集合是可證明的封閉語言，其中每個成員都是合法 ES2020 `OptionalMemberExpression`，把呼叫放進鏈內對這整個語言成立短路傳播是 ECMAScript 對這個 AST 形狀的定義性質，不需要逐案驗證。

**`\z` 不是 `$`**：同檔案家族 `src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/TextBoxTagHelper.cs:43`-`55` 的 ANCHOR NOTE 已經記過這條教訓——.NET 的 `$` 會在字串尾端單一 `\n` 之前比中，JS 沒有這個例外；用 `$` 會讓 `"a?.b\n"` 在伺服器端被誤判為安全鏈，實際輸出仍帶著那個換行字元，client 端解析出完全不同的東西。`IsNarrowOptionalChain` 的新 regex 上方直接留了指向這段 ANCHOR NOTE 的註解，不重複整段論證。

**保留字 denylist 是衛生，不是必要**：即使 `function?.call` 溜過 regex，direct 與 wrap 兩形都同樣是 SyntaxError（`function` 是關鍵字，不是合法的鏈頭），不會更壞——denylist 只是讓「分類器命中 ⇒ 發射一定合法」變成無例外的不變式，避免未來看到「命中卻輸出壞碼」的假象。

### 20 個發射站點——全部收斂到同一個 helper

不同於 `#999 part (A)`/`(B)` 各自零散處理，本修法把全部 20 個站點統一經過 `FormatFuncInvocation`：其中 10 個站點在 `#999`/`#965` 已經呼叫這個 helper（`BaseElementTag.cs` ×3、`TreeTagHelper.cs` ×2、`ComboBoxTagHelper.cs` ×2、`TreeContainerTagHelper.cs`、`ColorPicker.cs`、`SelectorTagHelper.cs`），修好 helper 本身即自動修好這 10 個；另外 10 個站點原本是「原地 `({X})(args)`」的 raw 包裹（未經過 helper），本次逐一改為呼叫 `FormatFuncInvocation(X, "<該站 args>")`，args 逐字照抄（含空格、含 `;` 位置）：

| # | 檔案:行號 | 屬性 | args |
|---|---|---|---|
| 1-3 | `BaseElementTag.cs:450,510,531` | ChangeFunc（checkbox/switch/radio 共用 wiring；autocomplete 兩變體） | `data`（既有，經 helper） |
| 4-5 | `TreeTagHelper.cs:368,377` | ChangeFunc（`if(X!=false)`／statement） | `data`（既有，經 helper） |
| 6-7 | `Form/ComboBoxTagHelper.cs:458,467` | 同上 | `data`（既有，經 helper） |
| 8 | `TreeContainerTagHelper.cs:311` | ClickFunc | `data`（既有，經 helper） |
| 9 | `Form/ColorPicker.cs:291` | ChangeFunc | `data`（既有，經 helper） |
| 10 | `Form/SelectorTagHelper.cs:478` | BeforeOnpenDialogFunc | `data`（既有，經 helper） |
| 11 | `DataTableTagHelper.cs:809` | DoneFunc | `res,curr,count`（**本次收斂**） |
| 12 | `DataTableTagHelper.cs:1300` | GridAction.OnClickFunc（#965） | `ids,ff.GetSelectionData('{Id}')`（**本次收斂**） |
| 13 | `Form/SliderTagHelper.cs:469` | ChangeFunc | `value,sliderIns`（**本次收斂**） |
| 14 | `Form/TransferTagHelper.cs:341` | ChangeFunc | `data, index,transferIns`（**本次收斂**，注意 `data,` 後的空格） |
| 15-17 | `Form/DateTimeTagHelper.cs:477,478,479` | Ready/Change/DoneFunc（單欄位） | `value,dateIns` 等（**本次收斂**） |
| 18-20 | `Form/DateTimeTagHelper.cs:577,578,582` | 同上（IsRange 雙 hidden-input 路徑） | 同上（**本次收斂**） |

各收斂站點原有的 `string.IsNullOrEmpty(...) ? string.Empty : ...` 三元結構全部保留不動，空值格 byte-identical。

### 六項具名修改（Codex `gpt-5.6-sol` cross-vendor review）逐一落實

1. **補 using**：`BaseElementTag.cs` 原本沒有 `System`/`System.Collections.Generic`，本檔專案未開 `ImplicitUsings`（已核對 `.csproj` 與 `common.props`，均無此設定）——`HashSet<string>`/`StringComparer.Ordinal` 需要它們，已補上 `using System;`／`using System.Collections.Generic;`，`dotnet build` 通過。
2. **§2.4 措辭修正**：見上方「缺陷回顧」小節——已改為「所有可觀察差異均限於今天會在 outer call 拋 TypeError 的短路情境；direct form 同時不會求值呼叫參數」，並移除「不存在任何 downstream 依賴現狀」的絕對宣稱。
3. **T-cls 是 36 筆不是 37，且不宣稱「.NET/node 等價自證」**：`test/WalkingTec.Mvvm.Core.Test/TagHelpers/OptionalChainInvocation1034Tests.cs` 的 `TCls_TrueSet_*`／`TCls_FalseSet_*` 合計 6 + 30 = **36** 筆（第一輪設計文件的「37」是列舉時的計數誤植——`CLS-F` 那組實際只有 30 筆，不是 31，逐項數過）。T-cls 只在 .NET 上執行，測試檔案自己的文件註解與本節都明講：這是「這個 regex/denylist 的 36 筆分類 regression matrix」，**不是**「.NET 與 node 兩個引擎的正則等價證明」——原始 node 交叉驗證的記錄在設計文件／PR 說明，本次工作階段沒有重跑 node。
4. **每列刪哪一行會變紅，必須是可編譯刪除**：
   - **T-chain**：narrow 分支寫成 `FormatFuncInvocation` 內一個獨立、可整段刪除仍可編譯的 `if` block（刪除後控制流自然落到下方的 wrap `return`），測試檔案自己的註解點名這個刪除動作。
   - **T-byte**：對應的是 compile-preserving mutation（`IsNarrowOptionalChain` 整個方法體改成 `return true;`），不是刪行——測試檔案註解據實這樣寫，也是下方「手動 mutation 驗證」小節實跑驗證的 mutant B。
   - **反空洞正控**：答案是刪 `helper.Process(...)`／`ProcessAsync(...)` 呼叫（仍可編譯，`output` 物件本身還在，只是內容維持空），不是刪 extractor 呼叫（那會編譯失敗）——`AssertNotVacuous` 輔助方法的文件註解明講這個區別。
   - **T-cond**（Tree／ComboBox 的 `if(X != false)` 站點）：兩個站點分別是 `TreeTagHelper.cs:368` 與 `ComboBoxTagHelper.cs:458` 各自的 `FormatFuncInvocation(ChangeFunc)` 呼叫，已折入對應的 T-chain 測試（`TChain_Site04_*`／`TChain_Site06_*`），測試方法註解點名各自的刪除點。
   - **T-ast**：指定的 compile-preserving mutation 是把 narrow 分支的 `return $"{funcExpression}({args})";` 換成已否決的 `return $"{funcExpression}?.({args})";`——T-ast 測試直接斷言 `CallExpression.Optional == false`，這個 mutation 會讓斷言翻成 `true`，變紅。
5. **補 island-ON fallback 測試**：`IslandFallback_Tree_OptionalChainChangeFunc_FlagOn_StaysOnLegacyPath_DirectForm`／`IslandFallback_ComboBox_*` 兩支測試直接測 `TreeTagHelper.cs:236` 與 `ComboBoxTagHelper.cs:321` 的 `useSelectIsland`／`changeIsIdentifier` 決策本身（把 `UseSelectIslandRender` 開 ON，斷言含 `?.` 的 ChangeFunc 仍落在 legacy `xmSelect.render(` 路徑、不觸發 `"type":"renderSelect"` island，且 legacy 路徑內的呼叫仍是本修法的 direct 形）——不是只靠 flag-OFF golden 間接推論。
6. **不加 mutation entry，措辭精確**：見下方「Mutation gate」小節——不寫「等價於 mutant 保護」，寫「相同的兩個 decision direction 有一般 CI regression protection」。

### 測試

`test/WalkingTec.Mvvm.Core.Test/TagHelpers/OptionalChainInvocation1034Tests.cs`，harness 沿用 `FormatFuncInvocation999BTests.cs`（render 真 TagHelper、抽取實際發射的 `<script>` block、test-only Acornima 解析）——**零新出貨相依，零新測試相依**（不引 Jint／`node`）。

- **T-chain ×20**：每個發射站點一支，`*Func` = `window.handlers?.onChange`，斷言逐字含 `window.handlers?.onChange(<該站 args>)` 且 `Assert.IsFalse(script.Contains("(window.handlers?.onChange)"))`。
- **T-byte**：20 個站點各一支 plain-identifier byte-identity（`myHandler1034`），加兩個 exemplar 站點（CheckBox 家族、DataTable DoneFunc 家族）各補 dotted-member／factory-call 兩形，共 24 支——function-literal 的 byte/parse 覆蓋刻意不重複，已存在於未動過的 `FormatFuncInvocation999BTests`／`RawFuncInterpolationParens999Tests`／`DataTableTagHelperUnwrappedIife965Tests`，且那些既有 fixture 同樣會被本節「always true」mutant 抓到（見下方手動驗證）。
- **T-cls**：36 筆分類 regression matrix（`[DataTestMethod]`/`[DataRow]`，6 個 `TCls_TrueSet_*` + 30 個 `TCls_FalseSet_*`），透過 PUBLIC 的 `FormatFuncInvocation` 觀察分類結果，不需要對私有分類器開反射或改成 internal。
- **T-cond ×2**：折入 T-chain 的 `TChain_Site04_*`／`TChain_Site06_*`。
- **T-ast ×3**：`TAst_Site01_*`（statement 位置）／`TAst_Site04_*`（expression 位置 `if(X!=false)`）用 Acornima 走 AST，斷言根是 `ChainExpression`、內含的 `CallExpression.Optional == false`；`TAst_WrapForm_PositiveControl_*` 斷言 wrap 形的根不是 `ChainExpression`。
- **Island-fallback ×2**：見上方具名修改 #5。
- **反空洞正控**：`AssertNotVacuous` 在每支測試前先斷言 emitted script 非空，配合各測試自己既有的 marker `StringAssert.Contains`。

**實測結果**：本檔案 85 個測試方法（`[DataRow]` 展開後）**85/85 全綠**；`test/WalkingTec.Mvvm.Core.Test` 整個套件（含本次新增）**5188 passed, 0 failed**（`dotnet test -c Debug -m:1`，依 `.claude/rules`/`#902` 序列化 testhost 避免 OOM）。

### 手動 mutation 驗證（不註冊 gate entry，見下方理由）——實跑輸出

`IsNarrowOptionalChain` 手動改成兩個 compile-preserving mutant，各自對本檔 85 支測試跑一次，再還原：

**Mutant A（整個方法體改成 `return false;`，narrow 分支永遠不命中）**：

```
Failed:    30, Passed:    55, Skipped:     0, Total:    85
```

紅的 30 支＝T-chain ×20 + T-cls TrueSet ×6 + T-ast（chain 相關）×2 + Island-fallback ×2，逐支比對過測試名稱清單，與預期完全吻合；`git status --porcelain -- src/WalkingTec.Mvvm.TagHelpers.LayUI/Abstraction/BaseElementTag.cs` 在還原後空輸出，確認乾淨還原。

**Mutant B（整個方法體改成 `return true;`，任何值都誤判為 narrow）**：

```
Failed:    54, Passed:    31, Skipped:     0, Total:    85
```

紅的 54 支＝T-byte ×24 + T-cls FalseSet ×30，同樣逐支比對吻合；`git status --porcelain` 同上確認乾淨還原。兩個方向合計覆蓋了 T-chain/T-byte/T-cls/T-ast/Island-fallback 五組測試裡對分類器有依賴的全部子集。

**這不是正式的 mutation-gate RED-before-fix 工作流**（沒有拆成「先加測試再加碼」兩個 commit）——上面兩段是這次工作階段手動跑出來的、可重現的證據，供 PR 描述引用，不是 CI 強制的 `run_mutant.py` 產物。

### Mutation gate：不加 entry

依 repo 既有先例（`CHANGELOG.md` 的 #965、#999 part (A)、#999 part (B) 三個條目皆為「Mutant: considered, not added」）：這是 JS 語意正確性修復，不是傳統意義的安全漏洞（無未授權存取、injection、跨租戶、憑證外洩維度）；`test/mutants/run_mutant.py` 的 `VALID_KINDS`（`security`/`selftest`）沒有適合這類缺陷的 kind，硬塞 `security` 會重複 #970/#968 已吸收過的 kind 分類漂移。

**準確措辭（不可寫成「等價於 mutant 保護」）**：`IsNarrowOptionalChain` 恆 false／恆 true 這兩個方向都不是無保護——上方「手動 mutation 驗證」證明兩個方向都有測試會變紅——但這是**一般 CI regression protection**（本檔案 85 支測試在每次 `dotnet test` 都會跑），不是 `run_mutant.py` 那套 mutation gate 機制本身：一般測試沒有 mutation runner 的 patch apply／clean baseline／expected-red-pattern／positive-control 對帳，也不是 required check 強制的 per-entry 執行。兩者是不同層級的保護，不能互相替代宣稱。

### 站點窮舉——可重跑指令與實際輸出（整棵樹，非只 `src/`）

```bash
grep -rn "FormatFuncInvocation(" --include="*.cs" --include="*.cshtml" . | grep -v "/bin/\|/obj/\|/test/" | grep -v "public static string FormatFuncInvocation"
grep -rnE '\(\{[A-Za-z_][A-Za-z0-9_.]*\}\)\(' --include="*.cs" --include="*.cshtml" . | grep -v "/bin/\|/obj/\|/test/"
```

**第一條實際輸出（22 行）**：

```
test/WalkingTec.Mvvm.Core.Test/TagHelpers/OptionalChainInvocation1034Tests.cs:1036:        var result = BaseElementTag.FormatFuncInvocation(value, "data");
test/WalkingTec.Mvvm.Core.Test/TagHelpers/OptionalChainInvocation1034Tests.cs:1074:        var result = BaseElementTag.FormatFuncInvocation(value, "data");
src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.cs:809:      {(string.IsNullOrEmpty(DoneFunc) ? string.Empty : BaseElementTag.FormatFuncInvocation(DoneFunc, "res,curr,count"))}
src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.cs:1300:                        actionScript = $"{BaseElementTag.FormatFuncInvocation(item.OnClickFunc, $"ids,ff.GetSelectionData('{Id}')")};";
src/WalkingTec.Mvvm.TagHelpers.LayUI/TreeContainerTagHelper.cs:311:                        cusmtomclick = $"{FormatFuncInvocation(ClickFunc)};";
src/WalkingTec.Mvvm.TagHelpers.LayUI/TreeTagHelper.cs:368:            if ({(string.IsNullOrEmpty(ChangeFunc) ? "true" : FormatFuncInvocation(ChangeFunc))} != false) {{
src/WalkingTec.Mvvm.TagHelpers.LayUI/TreeTagHelper.cs:377:        }}" : FormatFuncInvocation(ChangeFunc))}
src/WalkingTec.Mvvm.TagHelpers.LayUI/Abstraction/BaseElementTag.cs:450:    {FormatFuncInvocation(changeFunc)};
src/WalkingTec.Mvvm.TagHelpers.LayUI/Abstraction/BaseElementTag.cs:510:     {FormatFuncInvocation(changeFunc)};
src/WalkingTec.Mvvm.TagHelpers.LayUI/Abstraction/BaseElementTag.cs:531:     {FormatFuncInvocation(changeFunc)};
src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/SelectorTagHelper.cs:478:  {(string.IsNullOrEmpty(BeforeOnpenDialogFunc) == true ? "" : "var data={};" + FormatFuncInvocation(BeforeOnpenDialogFunc) + ";")}
src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/ComboBoxTagHelper.cs:458:            if ({(string.IsNullOrEmpty(ChangeFunc)?"true":FormatFuncInvocation(ChangeFunc))} != false) {{
src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/ComboBoxTagHelper.cs:467:        }}" : FormatFuncInvocation(ChangeFunc))}
src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/DateTimeTagHelper.cs:477:    {(string.IsNullOrEmpty(ReadyFunc) ? string.Empty : $",ready: function(value){{{FormatFuncInvocation(ReadyFunc, "value,dateIns")}}}")}
src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/DateTimeTagHelper.cs:478:    {(string.IsNullOrEmpty(ChangeFunc) ? string.Empty : $",change: function(value,date,endDate){{{FormatFuncInvocation(ChangeFunc, "value,date,endDate,dateIns")}}}")}
src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/DateTimeTagHelper.cs:479:    {(string.IsNullOrEmpty(DoneFunc) ? string.Empty : $",done: function(value,date,endDate){{{FormatFuncInvocation(DoneFunc, "value,date,endDate,dateIns")}}}")}
src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/DateTimeTagHelper.cs:577:        {(string.IsNullOrEmpty(ReadyFunc) ? string.Empty : $",ready: function(value){{{FormatFuncInvocation(ReadyFunc, "value,dateIns")}}}")}
src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/DateTimeTagHelper.cs:578:        {(string.IsNullOrEmpty(ChangeFunc) ? string.Empty : $",change: function(value,date,endDate){{{FormatFuncInvocation(ChangeFunc, "value,date,endDate,dateIns")}}}")}
src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/DateTimeTagHelper.cs:582:            {(string.IsNullOrEmpty(DoneFunc) ? string.Empty : $"{FormatFuncInvocation(DoneFunc, "value,date,endDate,dateIns")};")}
src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/SliderTagHelper.cs:469:    {(string.IsNullOrEmpty(ChangeFunc) ? string.Empty : FormatFuncInvocation(ChangeFunc, "value,sliderIns"))}
src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/ColorPicker.cs:291:        {FormatFuncInvocation(ChangeFunc)};
src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/TransferTagHelper.cs:341:    {(string.IsNullOrEmpty(ChangeFunc) ? string.Empty : $"{FormatFuncInvocation(ChangeFunc, "data, index,transferIns")};")}
```

**第二條實際輸出（2 行）**：

```
test/WalkingTec.Mvvm.Core.Test/TagHelpers/OptionalChainInvocation1034Tests.cs:1075:        Assert.AreEqual($"({value})(data)", result,
src/WalkingTec.Mvvm.TagHelpers.LayUI/Abstraction/BaseElementTag.cs:399:            return $"({funcExpression})({args})";
```

第一條：**20 個 `src/` 呼叫站點**（與上表逐一對應）+ 2 個命中在 `test/WalkingTec.Mvvm.Core.Test/TagHelpers/OptionalChainInvocation1034Tests.cs` 本身（T-cls 直接呼叫 `BaseElementTag.FormatFuncInvocation(value, "data")` 兩處，是測試程式碼呼叫 helper 做分類觀察，不是第 21/22 個發射站點）——**誠實揭露**：這兩條指令沿用任務簡報給定的原樣寫法，`grep -v "/test/"` 因為路徑是相對路徑（`test/...` 沒有前導 `/`）而沒有濾掉 `test/` 目錄，不是漏濾；已逐行核對每個命中的實際內容，20 個 `src/` 命中與上表 20 個站點一一對應，無遺漏、無多餘。

第二條：**只剩 2 個命中**——`src/WalkingTec.Mvvm.TagHelpers.LayUI/Abstraction/BaseElementTag.cs:399`（`FormatFuncInvocation` 自己的 fallback `return` 陳述式本身，不是呼叫站點）與同一個測試檔案裡 T-cls 的斷言字面值 `$"({value})(data)"`（比對用的期望字串，不是發射站點）。**沒有任何一個原本的 raw 包裹發射站點殘留**——10 個此次收斂的站點全部確認已改為呼叫 `FormatFuncInvocation`。

**驗證邊界誠實揭露**：上述兩條指令是樣式比對（`{...}`/`FormatFuncInvocation(` 字面），一個假想的用 `string.Concat`/`StringBuilder` 組裝呼叫的站點會漏掉此掃描——這個邊界沿用 `#999 part (A)` 條目已記錄的相同限制，本次未做 Roslyn taint 分析，也不宣稱窮盡了這種假想站點。

### 範圍外，另案追蹤（不在本修範圍）

以下站點今天已是「未包裹」形態（沒有 #999/#965/#1034 的迴歸——`?.` 鏈本來就已短路），各自的缺陷屬於不同類別，不套用本修法（套用會把今天正常運作的 bytes 改掉，零收益）：

- **`Form/SliderTagHelper.cs:471`（OnTipsFunc，expression 位置）**——追蹤於 #1041。
- **`Form/TextBoxTagHelper.cs:105,109`（oninput/onchange，HTML 屬性 direct append）**——一併記於 #1041。
- **Selector 跨套件 sink（`SelectorTagHelper.cs` → `_FrameworkController.cs` → `Selector.cshtml` 的 request round-trip）**——追蹤於 #1043。

### CHANGELOG 舊條目更正指標

10.22.0 CHANGELOG 的三個 `#1034` 揭露區塊（`#999 part (A)`／`#999 part (B)`／`#965` 條目開頭的 blockquote）各有一句「skipping the wrapper when the text contains `?.` is exactly the guess-JS-grammar approach…」——本修落地後，這句話描述的「被否決的做法」（naive substring 偵測）依然被否決，但整句話容易被誤讀成「optional chain 這個缺陷類別無解」。依本 repo 慣例（`CHANGELOG.md:24` 的「Corrected 2026-08-03 (#1035)」形式），在三處原文下方各加一則更正指標，指向本 10.22.1 條目，說明「被否決的是 naive substring 偵測；10.22.1 的封閉語言分類器 fail-closed，不做字串猜文法」——**不刪改原文**（`two-docs-overclaiming` 教訓：更正要指出差異，不是把舊文字改成看起來一直都對）。

### 未能驗證／不確定之處（誠實列出）

- 本次工作階段禁止呼叫任何 Gitea/GitHub API、禁止開 PR——這個修復尚未在真正的 Gitea Actions CI 上跑過，本機驗證只到 `dotnet build`/`dotnet test`。
- T-cls 的 36 筆矩陣只在 .NET 執行；與 node 引擎的交叉驗證記錄在設計文件（非本次工作階段重跑），本節第 3 項具名修改已明講這個邊界。
- 手動 mutation 驗證是本次工作階段人工跑出來的、非 CI 強制——沒有註冊 `test/mutants/entries/*.json`，理由見上方「Mutation gate」小節。
- 10.22.0 已釋出一段時間：理論上存在「已升級且依賴短路格會 TypeError 中止」的下游使用者；本節與 CHANGELOG 均未宣稱「不存在這樣的依賴」，只論證這類依賴不可能正常運作（見「缺陷回顧」小節）。

---

## `WTMContext.SetCurrentTenant` admission 收窄：narrow 一個判斷點，不重建系統不變式（#1007，2026-08-03）

跨廠複審（Codex gpt-5.6-sol，round 6）**APPROVED WITH NAMED CHANGES**；本節與程式碼已套用全部九項 named change（下方逐項標註）。

### 宣稱邊界（先寫，因為它約束其餘一切；NC1 已收窄）

> **`SetCurrentTenant` 不再接受呼叫端提出的、未經本次呼叫單次讀取之 `AllTenant` 快照唯一解析背書的租戶碼**（`null` 請求只有 host 呼叫者會成功——`user.TenantCode == null` 才放行，非 host 呼叫者的 `null` 請求一律明確回 `false`；`req == TenantCode`＝自己的 home 碼，兩分支皆不經 `AllTenant` 解析——明文 documented limitation，home routing 本身不在本項保護範圍）。
> **本項不使 `CurrentTenant` 不可偽造**——`Wtm.LoginUserInfo.CurrentTenant = "x";` 仍可繞過（`WTMContext.User.cs:146` 的 getter 原樣回傳可變物件）；hydration（快取反序列化整物件安裝 `LoginUserInfo`，`WTMContext.User.cs:285,299`）、`ReloadUserFunc`、federation 的 `CallAPI<LoginUserInfo>`（`WTMContext.cs:275,290`）、公開 setter（`WTMContext.User.cs:148-160`）之重驗與防護，以及 routing sink（`CreateDC`／`GetUserDC`）本身，全部屬 **#1045**，本項未觸碰。
> **升級前已寫入 user cache 的 override 本輪不重驗**，存活至該快取項目**最後一次寫入後七天**，或重新登入（→ #1045）。**「最長七天」是假的上限，不得如此描述（#1049）**：`DistributedCacheExtensions.cs:118` 的 `AbsoluteExpirationRelativeToNow = new TimeSpan(7, 0, 0, 0)` 是相對於**寫入當下**的七天（只在 `typeof(T) == typeof(LoginUserInfo)` 且呼叫端未自帶 `options` 時套用，本例正是這個路徑），而重新寫入不需要重新登入——**一次成功的租戶切換就會重寫**：新 admission `WTMContext.cs:721-722`、legacy `WTMContext.cs:811`，兩者都觸發 setter 的 `Cache?.Add`（`WTMContext.User.cs:157-159`）。因此持有 stale override 的使用者只要每七天內做一次合法切換即可無限續租；正確的上限是「無上限，直到某次寫入沒有發生」。另注意 `AddAsync<T>` **沒有**這個型別特例（`DistributedCacheExtensions.cs:130-141`），走 async 寫入的項目連七天都沒有。
> **四個 stock `SetTenant` HTTP 入口——`_FrameworkController.cs:1817` 與三份 demo `AccountController.cs:97`——接受本次呼叫 caller-proposed override 的唯一顯式 member-assignment path**（NC1：原設計稿寫「唯一寫入通道」過寬，已收窄為「顯式 member-assignment path」）——stock HTTP middleware 從快取反序列化並**整物件安裝** `LoginUserInfo`（`WTMContext.User.cs:285,299`）是另一條 stock HTTP 狀態進入通道，本項不覆蓋、也不宣稱覆蓋。

**禁用措辭**（前五輪 Red Line 事故清單，程式碼註解比照辦理）：「routing sink 只消費已解析 identity」「租戶身分不變式已建立」「CurrentTenant 不可偽造」「完整封閉」。

### 範圍證明：這一個判斷點在 stock HTTP 面上是否真的獨佔（NC2，完整可重跑指令）

**窄口徑**（member-access assignment；**不涵蓋 object initializer**，這是刻意的口徑限定，不是漏測）：

```bash
grep -rnE --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git --include='*.cs' '\.CurrentTenant\s*=[^=]' .
```

`src/` 命中恰 2 處：`WTMContext.cs:721`（`SetCurrentTenant` 本體的 `user.CurrentTenant = tenant;`）與 `WTMContext.cs:811`（`LegacySetCurrentTenant` 內逐字複製的舊本體）；其餘 17 處全在 `test/`。

**寬口徑**（NC2 要求另補跑，涵蓋 object initializer 形態如 `new LoginUserInfo { CurrentTenant = ... }`）：

```bash
grep -rnE --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git --include='*.cs' 'CurrentTenant\s*=[^=]' .
```

`src/` 命中同樣恰 2 處（`WTMContext.cs:721,811`），額外一處命中是 `WorkflowInstanceController.cs:87` 的**註解**（`// (CurrentTenant = _currentTenant ?? TenantCode)`，非賦值）；其餘全在 `test/`（含多個 `new LoginUserInfo { CurrentTenant = ... }` 物件初始化式）。兩條指令皆已在本次工作階段實跑，`src/` 結論一致——**宣稱因此嚴格限定為「member-access assignment」這一種賦值形態**，兩條指令的完整輸出已核對逐行。

`SetCurrentTenant(` 呼叫端：

```bash
grep -rn "SetCurrentTenant(" --include="*.cs" . | grep -v '/bin/\|/obj/'
```

恰 4 處呼叫（`_FrameworkController.cs:1817` + 三份 demo `AccountController.cs:97`）+ 1 處定義（`WTMContext.cs`）。

**三個誠實邊界**（全數落 #1045，不因為上面兩條指令通過而消失）：(a) 上述 grep 只窮舉屬性賦值，**整物件安裝**（快取反序列化、`CallAPI<LoginUserInfo>`、setter 塞整個物件）是另一類通道，完全不在這兩條指令的偵測範圍內；(b) 升級前存量毒 override 不被追溯淨化；(c) 下游程式碼與反射寫入無法被 grep 證明不存在。

### 決策函式

`WTMContext.SetCurrentTenant(string? tenant)` 先檢查 kill switch（見下），否則呼叫新的 `private bool IsTenantSwitchPermitted(LoginUserInfo user, string? req)`，其分支順序（每支皆可獨立刪除仍編譯，見下方測試矩陣）：

1. **L-null**：`req == null` → `return user.TenantCode == null;`——只有 host 才能用 `null` 回 home。
2. **L-home**：`req == user.TenantCode` → `return true;`——自己的 home 碼永遠放行，**不經解析**。
3. 以下才**單次讀取** `GlobaInfo?.AllTenant`（`GlobalData.cs:54` 每次 access 都 invoke provider，此處只呼叫一次存成本地變數）：
   - **L-ambiguous**：`matches.Count > 1` → `return false;`——解析出多列一律拒，不分 host／tenant。
   - **L-notfound**：`matches.FirstOrDefault() == null` → `return false;`——**這裡之前，`IWtmTenantSwitchPolicy` 尚未被諮詢**。
   - 諮詢 `IWtmTenantSwitchPolicy`（若已註冊）：`Deny` → `false`；`Allow` → `true`；`Inherit`（含未註冊）→ 落入下面的結構性預設。
   - **L-host**：`user.TenantCode == null` → `return true;`——host 深度不設限。
   - **L-child**：`descriptor.TenantCode == user.TenantCode` → `return true;`——非 host 只能到直接子租戶。
   - 其餘 `return false;`。

單次讀取意味著唯一性判定（L-ambiguous）與 descriptor 選定出自**同一份**快照；但 admission 與後續 `CreateDC`（`CreateDC.cs:27` 自己再讀一次）之間的跨讀取 TOCTOU **本輪不封**——這也是 §0 宣稱以「admission」而非「CurrentTenant」為主詞的原因。

### `IWtmTenantSwitchPolicy`（`src/WalkingTec.Mvvm.Core/Services/IWtmTenantSwitchPolicy.cs`，新介面，NC3 已補完整 XML docs）

重用既有三值 enum `WtmAuthorizationDecision`（`IWtmFrameworkEndpointAuthorizer.cs:16-21`），未在既有介面加成員。`AddScoped` 註冊（比照 #827）；未註冊＝`GetService` 回 `null`＝恆 `Inherit`。**可覆寫**：entitlement（L-host／L-child 的預設關係判斷，例如放行 sibling／grandchild 工作流）。**不可覆寫**：解析——`NotFound`／`Ambiguous` 在諮詢行之前就 `return`，policy 呼叫次數在這兩種輸入下**結構性為 0**（測試矩陣列 11 用計數 stub 兩種輸入各驗一次）。**`Deny` 結構上碰不到 `req == null` 與 `req == home`**——policy 無法把呼叫者困在別人的租戶裡。**policy 拋例外 → propagate，不 catch**（介面 XML doc 的 `<exception>` 區塊已明文；測試矩陣列 14 pin 死這件事，防未來加 catch-and-deny 讓錯誤塌縮成合法拒絕）。

### 完整行為差異表：非 host 呼叫者的零差異例外（NC4，補回被漏掉的 T6）

「今日」＝10.22.0 的 `D1||D2||D3`（`WTMContext.cs:679`，pre-#1007）；「新」＝上方決策函式（kill switch off、無 policy）。**結構性驗證**：非 host 時 D1 恆假 ⇒ 今日 ≡ D2||D3 ≡ L-home＋L-child（單列），**零差異例外恰為 T2、T6、T8、T9**（NC4 修正：原設計稿 §7 首段與「給複審者的三句話」都只寫了 T2/T8/T9，漏了表內明載為收窄的 T6）：

- **T2**：非 host、`null` 請求、`AllTenant` 內有 `TCode == null` 的畸形列（`TenantCode == home`）——今日經 D3 的 `Any` 誤判為 Allow，新規則 L-null 先擋，Deny。
- **T6**：非 host 直呼 Core 傳 `""`，`AllTenant` 有兩列 `TCode == ""`（任一 parent==home）——今日經 D3 的 `Any` Allow，新規則 L-ambiguous 先擋，Deny。
- **T8**：duplicate 子碼，第一列 parent≠home、第二列 parent==home——今日**憑第二列獲准、卻路由到第一列**（admission/routing 分裂，攻擊面），新規則整體拒絕。
- **T9**：duplicate 子碼，兩列皆 parent==home——今日 Allow（`Any`）＋路由 First，新規則整體拒絕（去重後可恢復，功能損失格但非安全洞）。

Host 呼叫者這一側的核心洞是 H3（不存在碼今日 Allow＋路由蓋 ghost 章到 default）與 H6（duplicate TCode 今日 Allow＋路由 First）——這兩項是本次收窄要堵的主要對象，其餘 host 差異詳見設計文件（本節不重複整份 H1–H9／T1–T10 表）。

### Kill switch 與遷移

`Configs.UseLegacyTenantSwitchAuthorization`（`ConfigOptions/Configs.cs`，`#region Tenant` 內，預設 `false`）開啟時，`SetCurrentTenant` 直接分派到 `LegacySetCurrentTenant`——**逐字複製** 10.22.0 的 `SetCurrentTenant` 本體，包含 null-user 處理都是原文，可用上方兩條 grep 指令自行 diff 驗證「逐字」這個宣稱。未加 `[Obsolete]`（避免框架自讀時的 CS0618 噪音）；XML doc 與本節都明寫 deprecated，預定移除版本為下下個 minor。

遷移路徑（升級後可能需要處理的五種情境，逐項對應到緊急程度）：

1. host 切到「不存在／停用／重複碼」現在得 403（框架端點）或 `false`（demo）：修資料（enable／去重目標租戶——經 provider 快取，最長 1 小時後生效，`FrameworkServiceExtension.cs:1275`，或清 `AllTenant` 快取鍵）。**policy 救不了 NotFound／Ambiguous**——唯一逃生門是 kill switch。
2. `EnableTenant=false`／console 部署（`AllTenant` 恆空）：host 的任何非 null 切換由 Allow→Deny。
3. **Federation 前端（`HasMainHost`）**：前端本地 `AllTenant` 通常為空，host「切到只有 mainhost 知道的碼」由 Allow→Deny——此流程是否真實存在無法由本 repo 證明；受影響者開 kill switch 過渡，正解在 #1045。
4. **存量 override**：升級前寫入 user cache 的 override 本輪不重驗（→ #1045），**必須在升級時清 user cache 或強制重登入**。**不得依賴「等它自然過期」**（#1049）：TTL 是相對於最後一次寫入的七天，而一次成功的租戶切換就會重寫並把到期日往後推，所以活躍使用者的 stale override 沒有自然過期的上限。
5. sibling／孫租戶等正當營運流程：註冊 `IWtmTenantSwitchPolicy` 回 `Allow`。

### 測試矩陣（16 列＋NC7/NC5 的兩項更正）

`test/WalkingTec.Mvvm.Admin.Test/SetCurrentTenantAdmissionTests1007.cs`（14 列，單元層級，`MockWtmContext` + `WTMContext.SetServiceProvider` 塞 policy stub）與 `FrameworkControllerRbacHooksTest.cs`（列 15/16，見下方 NC7）。Deny 列一律雙斷言（回 `false` 且 `CurrentTenant` 未變）；Allow 列斷言 `true`、`CurrentTenant == req`、且快取已更新（`MockWtmContext` 用真 `MemoryDistributedCache`，讀回同一把 cache key 驗證）。四列誠實標「無可刪行」而非硬湊：4（單行刪除只會更嚴，靠 M3 補分支精確度）、6（兩向都不可證，靠列 7 補 L-null 的證明）、14（斷言 catch 的不存在，是 pinning）。

**NC5 更正（原設計稿判斷過於保守）**：列 13（KS on × tenant × 孫）**是可證的**，不是「無可刪行」——刪掉 `LegacySetCurrentTenant` 本體的整條 guard：

```csharp
if (LoginUserInfo?.TenantCode == null || LoginUserInfo?.TenantCode == tenant || GlobaInfo?.AllTenant?.Any(x => x.TCode == tenant && x.TenantCode == LoginUserInfo?.TenantCode) == true)
```

剩下的 `{ ... }` 在 C# 是合法 standalone block、仍可編譯，且會無條件執行——孫租戶請求由 `false` 變 `true`，`Row13_KillSwitchOn_Tenant_Grandchild_ReturnsFalse_PinsLegacyIsNotDisableAllChecks` 這支測試會紅。已在測試檔的 doc comment 裡把這條 guard 行列為列 13 的 deletion proof，不再標 pin。

**NC7 更正（原設計稿誤稱 wire test）**：`FrameworkControllerRbacHooksTest` 裡的列 15（`SetTenant_HostGhostTenant_ReturnsForbidResult`，新增）與列 16（`SetTenant_SwitchToOwnTenant_DoesNotReturnForbid`，強化為斷言精確型別 `WtmActionResult`）是**controller-level** 測試——直接建構 `RbacHookProbeController` 並呼叫其 `SetTenant` action method in-process，不是經 test server 發 HTTP request。本節與測試檔案的 doc comment 都已改稱「controller-level」，不再稱「wire」。真正經 test server／真實 HTTP 的測試在下方 NC9 一節。

### 生產可達性：NC9（named change，最重要的一項；已完整落地，非部分達成）

原設計稿的策略測試只用 `WTMContext.SetServiceProvider` 塞 policy stub——這繞過真正生產路徑 `_serviceProvider ?? _httpContext?.RequestServices`（`WTMContext.cs:35`），而 controller fixture 的 `WTMContext` 與 controller 本身各自持有不同 `HttpContext`（`MockWtmContext.cs:36`、`FrameworkControllerRbacHooksTest.cs:136`），沒有任何一支測試證明「一個真的透過 DI 容器註冊的 policy，在真實、routed 的 HTTP 請求上真的會被諮詢」。

**已新增 `test/WalkingTec.Mvvm.Api.Test/TenantSwitchPolicySeamTests1007.cs`**，比照 #827 的 `FrameworkAuthorizationSeamTests` 同一套機制：

- `DemoWebApplicationFactory` 起一個真正的 in-process ASP.NET Core host。
- `builder.ConfigureTestServices(services => services.AddScoped<IWtmTenantSwitchPolicy, TestTenantSwitchPolicy>())`——**真正的** `IServiceCollection`／`AddScoped`／`BuildServiceProvider`，不是 Moq。
- 用真實 `HttpClient` 對 `/_Framework/SetTenant?tenant=...` 發 **真正的、routed 的 GET 請求**（同一份 `_FrameworkController.SetTenant`，即上方提到的框架端點），並透過 `/Login/Login` 完成真實登入取得 cookie。
- `SetTenant_NoPolicyRegistered_HostSwitchToListedTenant_Succeeds`：baseline，無 policy 註冊，host 切換到一個真實 seed 進 DB、`EnableTenant=true` 後由框架自己的 `SetTenantGetFunc` 讀出的租戶——確認 fixture 本身健全。
- `SetTenant_DIPolicyDeny_HostSwitchToListedTenant_ReturnsForbid_ConsultedOnRealRoute`：同一個 fixture，DI 註冊一個回 `Deny` 的 policy，斷言回應是 403（cookie auth 下 `Forbid()` 呈現為 302 redirect）**且** `TestTenantSwitchPolicy.Calls > 0`。

兩支測試皆已在本次工作階段實跑並綠燈（`dotnet test test/WalkingTec.Mvvm.Api.Test --filter FullyQualifiedName~TenantSwitchPolicySeamTests1007`，2/2 pass）——**這是本輪對 NC9 的誠實回報：已經是真的經過 request-scoped DI／真實 HTTP route，不是只換了說法的 `SetServiceProvider` stub。**

**仍誠實揭露的邊界**（不宣稱多過此範圍）：這支測試只驗證「一個 policy 場景（Deny 蓋掉 host 預設）在真實路由上可達」，沒有把 16 列矩陣全部搬到真實 HTTP（成本 ~50 倍，對「DI/HTTP 可達性」這一個具體缺口沒有額外訊息量）；沒有涵蓋 federation（`HasMainHost`）拓樸——那本來就是遷移路徑第 3 項承認的未證實流程；也沒有驗證 Allow-覆蓋-sibling（P2）方向在真實 HTTP 上的鏡像（單元測試列 10 已覆蓋該方向的邏輯本身，只是不經真實 DI/HTTP）。

### Mutation gate：M1/M2/M3，三個皆已本機 KILLED（NC6 已更正說明）

**NC6 更正**：原設計稿寫「未強化的 green control 會被 baseline-not-green 擋下」——這是錯的。`run_mutant.py` 的 `evaluate_baseline` 只在**乾淨樹上跑 `red_tests`** 並要求它們已綠；green control（positive control）是**套用 mutant之後**才檢查是否仍 pass，機制上**無法**偵測「green_test 斷言太弱、即使拿掉守衛也照樣綠」這種情況。正確的說法：**強化列 16（`SetTenant_SwitchToOwnTenant_DoesNotReturnForbid` 改斷言精確型別）是語意上的必要前置，但 gate 本身不會自動抓到未強化的假綠**——這一步的價值來自人工判斷（見上方 NC7 段落），不是 gate 機制保證的。

三個 entry 皆 `kind: security`，`test_project` 皆 `test/WalkingTec.Mvvm.Admin.Test/WalkingTec.Mvvm.Admin.Test.csproj`，本次工作階段用 `python3 test/mutants/run_mutant.py --mutant <id>`（先清 `find . -name 'demo.db*' -path '*bin*' -delete`）逐一實跑，`red_expected_assertion_patterns` 皆已用實際捕捉到的失敗訊息回填、`_provisional: false`：

- **`1007-setcurrenttenant-hostbypass-reintroduce`**：在 L-home 之後、快照讀取之前插入 `if (user.TenantCode == null) { return true; }`（精確重引入 D1）。red：列 15（host×ghost）。green：列 16（短路 decoupling——`req=="tenantA"==user.TenantCode` 在字面上位於插入行之前的 L-home 就已 `return true`）。**VERDICT: KILLED / GATE: PASS**。
- **`1007-ambiguous-collapse-firstmatch`**：`matches.Count > 1` → `matches.Count > int.MaxValue`。red：列 8。green：列 3（invariant-result decoupling——`Count==1` 時兩式皆 false）。**VERDICT: KILLED / GATE: PASS**。
- **`1007-childscope-widen`**：L-child 條件 `descriptor.TenantCode == user.TenantCode` → `descriptor != null`。red：列 4。green：列 1（短路 decoupling——NotFound 提前 return，此輸入永不到達 L-child）。**VERDICT: KILLED / GATE: PASS**。

### BMS smoke（NC8）

本輪未執行任何下游（BMS）smoke test——**這是明確記錄的未執行相容性風險，不是已排除的疑慮**。`SetCurrentTenant` 是 default-ON 的行為收窄，NuGet 出貨後任何依賴「host 可切換到任意/不存在租戶碼」這個舊行為的下游都會在升級後遇到新的 `false`/403。建議：**下次 release gate 把 BMS smoke（或至少一次針對 SetTenant 端點的手動驗證）列為檢查項**；若該次 release 略過，這裡就是它的明確記錄，不得被讀成「已驗證無影響」。

### #1042 handoff：框架端點 log 尚未 sanitize（NC8）

Core 層的新增 log（`WTMContext.cs` 的 `WtmDiagnosticLogger?.LogWarning`）已對 `user.ITCode`、`user.TenantCode`、`tenant` 三個值套用 `LogSanitizer.Sanitize`。但 `_FrameworkController.cs:1831` 既有的 `SetTenant refused: ...` warning **仍直接記錄 raw `tenant`**（未經 sanitize）——本輪**不修**這一行（不在範圍鐵律內：`_FrameworkController.cs` 一行不動），交給 #1042。**同一次被拒絕的請求會產生兩筆 log：一筆經 sanitize（Core 層新增）、一筆未經 sanitize（`_FrameworkController.cs` 既有）**——不得暗示 endpoint log hygiene 已完成。

### 未能驗證／不確定之處（誠實列出，對應 §10）

- Federation 前端（`HasMainHost`）「host 切到僅 mainhost 知道的碼」流程是否真實存在——不阻擋出貨（kill switch 是逃生門），但遷移路徑第 3 項若缺席會阻擋（缺了＝宣稱過度，本節已列）。
- 下游（含 BMS）對「host 可任意切換租戶碼」這個舊行為的依賴程度——見上方「BMS smoke」小節，明確記錄為未執行的相容性風險。
- 升級前存量 override 殘留——不阻擋（屬 #1045），本節與 CHANGELOG 皆已明寫此限制。
- 反射／IL 寫入的不存在性——上方兩條 grep 只窮舉屬性賦值，不宣稱「已窮盡所有寫入」。
- 快取寫入失敗語意維持今日（`Cache.Add` 拋則例外外洩、in-memory 已變）——既有瑕疵，本輪不修不修飾，列 follow-up。
- 本次工作階段禁止呼叫任何 Gitea/GitHub API、禁止開 PR——這個修復尚未在真正的 Gitea Actions CI 上跑過，本機驗證只到 `dotnet build`/`dotnet test`/`run_mutant.py`。

---

## 移除整個 opt-in S3/MinIO 檔案處理模組 `WalkingTec.Mvvm.FileHandlers.S3`（#1054，BREAKING，取代 #1027，2026-08-03）

**這不是漏洞修復，是專案擁有者裁定的移除**——與本文件其他條目性質不同，這裡不主張任何安全缺陷；CHANGELOG `[10.23.0]` 的 `### Removed`/`### Migration` 兩段是本項的權威敘述，本節補充驗證與方法論，不重複、不超過那兩段的宣稱。

### 移除範圍

整目錄刪除 `src/WalkingTec.Mvvm.FileHandlers.S3/`（`WtmS3FileHandler.cs`、`S3FileHandlerOptions.cs`、`S3FileHandlerServiceCollectionExtensions.cs`、`README.md`、`.csproj`）與 `test/WalkingTec.Mvvm.FileHandlers.S3.Test/`。連帶移除引用：`WalkingTec.Mvvm.sln`（兩個 `Project(...)` 區塊 + 對應的 `ProjectConfigurationPlatforms`/`NestedProjects` GlobalSection 行）、`core.slnf`（兩個專案路徑）、`Directory.Packages.props`（`AWSSDK.S3` 那行連同其 `<!-- Issue #425 -->` 註解——這不是 `.claude/rules/dependency-management.md` 列管的安全 override pin，是單純的「這個套件不再需要」）、`.github/workflows/publish-nuget.yml`（pack 步驟、smoke test 安裝清單、Gitea/GitHub 兩份 cohort-check 套件清單、GitHub mirror re-pack 步驟，以及沿路每一處提到「六個套件」的註解——逐一改寫，不是只改程式碼那幾行）、`.github/workflows/ci-build.yml`（`core.slnf spans 7 test projects` → 6，含 #902 那段解釋 MSBuild `-m:1` 理由的兩處引用）、`scripts/publish-to-gitea.sh`（頭部理由註解與執行期錯誤訊息的套件計數）、四份文件（`docs/gitea-packages.md`、`docs/wtm-developer-manual.md`、`docs/ci-operations.md`、本文件）、`test/smoke/publish-nuget-fixture/Program.cs`（移除 S3 的 `dotnet add package`/型別解析行，「其他五個套件」→「其他四個套件」）、`test/WalkingTec.Mvvm.Core.Test/Security/GetFileDataCallSiteInvariantTests859.cs`（`AllowedCallSiteFiles` 允許清單移除 `src/WalkingTec.Mvvm.FileHandlers.S3/WtmS3FileHandler.cs` 這一項；程式碼註解裡也點名了 `WtmS3FileHandler`，同步移除，不然只改清單、留著註解點名一個不存在的型別）。

### 誠實揭露：兩條呼叫路徑，只有一條真的死了

**不宣稱「沒有人可能在用」。** 刪除前實際讀了兩份原始碼確認：

- `WtmFileProvider.Init`（`src/WalkingTec.Mvvm.Core/Support/FileHandlers/WtmFileProvider.cs:76`）只把「有 `(WTMContext)` 這個確切建構子」的 `IWtmFileHandler` 實作註冊進 `_handlers` 字典：`item.GetConstructor(new Type[] { typeof(WTMContext) })`。`WtmS3FileHandler` 唯一的建構子簽章是 `(IAmazonS3, IOptions<S3FileHandlerOptions>, ILogger<WtmS3FileHandler>?)`，沒有 `(WTMContext)` 版本——所以這行 `GetConstructor` 恆回 `null`，`Init` 直接 `continue` 跳過它。也就是說 `FileUploadOptions.SaveFileMode="s3"` 這條路，在任何已發行版本裡都**從未真的可達**過 `WtmS3FileHandler`——`CreateFileHandler("s3")` 找不到 key，退回 `WtmDataBaseFileHandler`（靜默降級，不是丟例外）。
- 但 `AddWtmS3FileHandler(Action<S3FileHandlerOptions>)`（`S3FileHandlerServiceCollectionExtensions.cs`）是完全獨立的第二條路——它直接 `services.AddScoped<IWtmFileHandler, WtmS3FileHandler>()`，不經過 `WtmFileProvider` 的反射查找。任何下游 app 呼叫過這個擴充方法、並用建構子注入直接消費 `IWtmFileHandler`（而不是透過 `WtmFileProvider.CreateFileHandler(saveMode)`）的話，今天這條路是**真的活著、真的能動**的。這種 app 升級到本版之後 build 會壞（型別找不到）。

### 套件計數一致性核對（實跑，非人工核對）

`publish-nuget.yml` 改完後，三處套件計數各自獨立跑 `grep` 確認皆為 5：pack 步驟（`grep -n "^      - name: Pack WalkingTec"` → 5 行）、smoke test 的 `dotnet add package` 清單（5 行）、GitHub mirror re-pack 的 `dotnet pack "$PACK_DIR/...` 清單（5 行）；Gitea 與 GitHub 兩份 `check-package-cohort.py` 呼叫的套件名稱清單也各自改為 5 個。`NUPKG_COUNT -ne 6` 的兩處人肉判斷式（本地 pack 後、GitHub re-pack 後）都改為 `-ne 5`。

### 遷移建議不指向另一個正在被移除的套件

CHANGELOG `[10.23.0]` 的 Migration 段落列了三條路：釘住舊版、把 `WtmS3FileHandler.cs`／`S3FileHandlerOptions.cs` 複製進自己的專案（`IWtmFileHandler` 是 public 介面）、或改用框架其餘的 `IWtmFileHandler` 實作。**第三條刻意只列 `WtmLocalFileHandler`（本機磁碟）與 `WtmDataBaseFileHandler`（database 模式，`SaveFileMode` 未設定時的預設值）——不列 `WtmOssFileHandler`（Aliyun OSS）**，因為 OSS handler 已被專案擁有者裁定移除、對應票是 #1055（排在本票之後、本次工作階段尚未執行）。把下游從一條已知死路指向另一條即將死路，不是誠實的遷移建議；等 #1055 執行完，`WtmOssFileHandler` 本身也會需要一份和本項同構的 CHANGELOG/production-readiness 說明。

### 版本

`version.props` 維持 `10.23.0`——這個版本本來就還在進行中、尚未發行（含上方 #1007 的安全修復），BREAKING 落在這個版本裡即可，沒有額外 bump。

### 驗證

`find . -name 'demo.db*' -path '*bin*' -delete`（跑測試前必做）→ `dotnet build core.slnf -c Release`：0 error（495 個既有、與本次修改無關的 warning）。`dotnet test core.slnf -c Release --no-build`：exit code 0，六個測試組件（`WalkingTec.Mvvm.Mvc.Tests`/`Admin.Test`/`Api.Test`/`Etl.Test`/`Core.Test`/`WorkFlow.Test`）逐一皆回報 `Failed: 0`（solution-filter 多專案彙總輸出是 VSTest 的逐組件 `Passed!` 摘要，不是單一 csproj 執行時的 `Test Run Successful` 字面字串——這裡明寫實際輸出格式，不套用本文件其他條目慣用的措辭）；被刪除的那個測試專案不在清單內，因為它已隨套件一起刪除，這件事本身就是驗證的一部分（core.slnf 引用已清乾淨，不會出現「找不到專案」的建置錯誤）。全樹 `grep -rn "FileHandlers\.S3\|WtmS3FileHandler\|AWSSDK\.S3" .`（排除 `bin`/`obj`）最終只剩 `CHANGELOG.md` 的歷史條目與本次新增的 `[10.23.0]` 條目，以及本文件（`docs/production-readiness.md`）自己這一節——其餘每一處命中（含程式碼註解、docs 敘述、workflow 註解）都已逐一改寫，不是只改看得到的程式碼行。**本節與 CHANGELOG 用同一組完整套件名/型別名/檔案路徑，不做字串拼接或迂迴指稱來規避這條 grep**——`docs/production-readiness.md` 是本 repo 的基準文件，CHANGELOG 的宣稱不得超過它；基準文件本身寫得比 CHANGELOG 含糊，會讓「CHANGELOG 不得超過 production-readiness」這個比對機制失效（#1049 的教訓）。

### 未能驗證／刻意沒動的部分（誠實列出）

- 本次工作階段禁止呼叫任何 Gitea/GitHub API、禁止開 PR——這個修法尚未在真正的 Gitea Actions CI 上跑過。
- 下游是否真的有專案呼叫 `AddWtmS3FileHandler()` 並直接注入 `IWtmFileHandler`——這件事本 repo 無法證明存在或不存在；上一節的「兩條路徑」分析只證明「哪條路徑在技術上是活的」，不是「有沒有真實下游在用它」。
- CHANGELOG 的歷史條目（`[10.13.0]` 引入、`[10.13.3]` 上 GitHub mirror、`[10.15.0]` 的例外處理修復等）一概未改寫，只在本項新增一則指向它們的條目。
- #1055（`WtmOssFileHandler` 移除）本身完全未觸碰——本次只在遷移建議裡提前註明它也在排定移除之列，不代表 #1055 已經執行或已經驗證。

---

## 安全姿態（2026-07 重評）

整體方向是**縱深強化**。本批次曾**誠實揭露一個真實缺口**（#876：ETL controller 從未接到全域 filter），寫驗收測試當下就地立案並在同一輪修復——過程本身正是為什麼「測試 pass + 漏洞掃 0」不是 production-ready 的全部證據：這個缺口不是掃描器或既有測試找到的，是寫一個新測試、實測觀察真實 HTTP 行為才浮現。

**強化（已合併）**：
- **`FrameworkFilter.OnResultExecuted`未轉義 `ViewDivId` 內插進 inline `<script>`（#799，MEDIUM）**：`FrameworkFilter.cs:394` 對每個 model 是 `BaseVM` 的 `PartialViewResult` 原樣輸出 `<script>try{ff.ResizeChart('{ViewDivId}')}catch{}</script>`，沒有任何編碼。**可觸及性逐段驗證，非照抄 issue 文字**：`WTMContext.CreateVM` 把 `Request.Form`/`.Query` 的每個 key 原樣複製進 VM 的 `FC` dictionary（含攻擊者可控的 `"ViewDivId"` 欄位）；`_FrameworkController.Selector`（`[AllRights]`，只需認證、不需頁面級授權的 POST 端點，`RedoUpdateModel`＋`PartialView` 這個組合在 `_FrameworkController.cs:417/777/871` 重複出現）呼叫 `RedoUpdateModel(listVM)`，用原始反射（`PropertyHelper.SetPropertyValue`）把 `FC` 的每個值寫進 VM 對應屬性——完全繞過 ASP.NET 自己的 model binder attribute，`ViewDivId` 沒有 `[BindNever]`從來就不是真正的缺口所在。兩個原本可能擋下它的機制都不成立：`grep -rn 'ValidateAntiForgeryToken|AutoValidateAntiforgeryToken' src/` 全庫零結果（本專案完全沒有 antiforgery 層）；`WtmCspOptions.ScriptSrc` 預設 `"'self' 'unsafe-inline'"`（`WtmCspOptions.cs:42`），出貨預設 CSP 也擋不住注入的 inline `<script>`。修法：改用 `JavaScriptEncoder.Default.Encode`——`HtmlEncode` 不轉義 `'`/`\`，擋不住 JS 字串字面量的斷句攻擊，`JavaScriptEncoder` 才是這個 sink 該用的工具；比照本庫既有同款 sink 的做法（`DataTableTagHelper.cs`、`PrivilegeFilter.cs` 既有的 `jsRedirect`/`jsLp`）。**同檔案＋同目錄 sibling 掃描**：`Filters/` 下的 `DataContextFilter.cs`/`PrivilegeFilter.cs`/`SwaggerFilter.cs` 均已檢查，未發現其他未轉義的 inline `<script>` 內插——`PrivilegeFilter.cs:202` 的同款 sink 本來就已經正確編碼。**不需版本翻升、非相容性破壞**：`JavaScriptEncoder.Default` 只改寫落在其安全字元集外的字元（`<`/`>`/`&`/`'`/`"`/`\`/控制字元），預設產生的 `ViewDivId`（`BaseVM.ViewDivId` 自己的 getter：`"ViewDiv" + UniqueId`，純英數字）與一般開發者自訂的 id 都不含這些字元，既有 app 的正常渲染輸出逐位元組不變——這點由一個 positive-control 測試證明，不只是靠推論。測試：`test/WalkingTec.Mvvm.Admin.Test/FrameworkFilterViewDivIdXssTests799.cs` 讓惡意 `ViewDivId` 走真實的 `Request.Form` → `WTMContext.CreateVM` → `Selector` 自己的 `RedoUpdateModel` → 真正的 `FrameworkFilter.OnResultExecuted` 實例，斷言的是「已轉義的位元組確實出現」（用同一個 `JavaScriptEncoder.Default.Encode` 算出預期值），不只是「原始 payload 不見了」。已註冊 mutant `mvc799-viewdivid-xss-script-encode`（`test/mutants/entries/`），刪掉 `JavaScriptEncoder.Default.Encode(...)` 呼叫會讓斷言變紅，驗證為 KILLED。
- ETL REST source：next-link 跨源憑證轉發封堵 + 有限 MaxPages（#661）；bulk-load 欄名 allowlist（`^[\p{L}\p{N}_#$]+$`，擋 SQL 注入字元、放行 CJK）+ MSSQL bracket 跳脫（#680）
- S3 handler：例外收斂、`../` key traversal 封堵、ContentType 設定（#680）
- GitHub mirror leak-gate 改為無條件執行 + sanitize 全文字檔化（#659）
- Analysis 白名單先於 Expression Tree 的安全邊界持續完好
- **ETL 實體 ITenant 覆蓋率 + 根因 wiring 缺陷（#841、#862，P1）**：`#836` 窮舉表找出四個 `class .* : BasePoco` 沒實作 `ITenant` 卻帶租戶資料的實體。修復三個（`EtlDeadLetterRow` 無須遷移、`EtlLineageRecord`／`EtlRunLog` 各自新增 `TenantCode` 欄位＋遷移腳本，見 CHANGELOG），第四個（Core 的 `ChangeLog`，稽核表，目前無 viewer）**刻意不修**——立案存證，未動 Core 行為。**根因更深**：`EtlJobDefinition` 自 ETL-006 起就實作 `ITenant`，但 `ApplyEtlModels()` 在消費端 `DataContext.OnModelCreating` 裡是**在** `base.OnModelCreating()` 之後才註冊 ETL 實體型別，而 `FrameworkContext.OnModelCreating` 的 Pass 2 迴圈（套用 `ITenant` 過濾器那段）早就跑完、看不到之後才註冊的型別——這個過濾器**從未真正生效過**，實測確認：兩個不同租戶的 `EtlJobDefinition` 互相可見，產生的 SQL 完全沒有 `TenantCode` 條件。修法是新增一個帶 `EmptyContext` 參數的 `ApplyEtlModels` 多載，在註冊每個型別後立刻補套用過濾器；舊多載保留但標 `[Obsolete]`。**修完這個 wiring 後，另外發現並修掉一個原本會被引入的迴歸**：`EtlSchedulerService`/`EtlQuartzJob`/`DbEtlGovernanceStore` 都是背景排程（自己開 DI scope，`TenantCode` 恆為 null），過濾器一旦真的生效，這個共用排程器就會再也找不到任何租戶所屬的 job/run log——已補上 `IgnoreQueryFilters()`（每處都有註解說明原因）。另外三個 ETL controller（`_EtlRunLogController`/`_EtlMonitorController`/`_EtlSchemaController`）補上與既有兩個 controller 相同的 Admin/ETLAdmin 角色守衛（#841 主體）。**寫驗收測試時意外挖到一個範圍大得多、與本項無關的既有缺陷，已獨立立案並修復（#876，見下一條）**：`WalkingTec.Mvvm.Etl` 整個組件的 controller 透過真實 HTTP 從未接到 WTM 三個全域 filter，導致 `Wtm` 恆為 null——角色守衛的邏輯本身正確（unit test 證實），但因此對任何呼叫者（含真正的 Admin）都會 fail-closed。
- **#876（P0，已修）：`WalkingTec.Mvvm.Etl` controller 角色守衛讀到的 `Wtm` 現在真的會被填值——修掉上一條找到的缺口。** 根因：ASP.NET Core 自己的 `ControllerActionFilter`（呼叫 controller 自己的 `OnActionExecuting` override 的內部包裝，凡是 `Controller` 子類別實作 `IActionFilter` 就會被框架自動加上）被框架寫死 `Order = int.MinValue`——永遠比任何自訂 filter（含 WTM 的三個全域 filter）先跑，不論那些 filter 的 `Order`設多少都贏不了（實測：把 `DataContextFilter.Order` 設成 `-1000` 完全沒變化）。`DataContextFilter` 正是把 `BaseController.Wtm` 設值的地方。全部原始碼裡只有這五個 `WalkingTec.Mvvm.Etl` controller（`_EtlJobController`、`_EtlDashboardController`、加上 #841 新增的三個）會在自己的 `OnActionExecuting` override 裡直接讀 `Wtm`——WTM 其他 controller 一律走 `PrivilegeFilter` 的宣告式、URL-based 授權——所以只有這五個會踩到這個排序缺口。**實際觀察行為（修復前，實測非推測）：乾淨的 403/redirect 拒絕，不是 `NullReferenceException`/500**——更正上一條「必定 NullReferenceException」的推測：守衛裡的 null-conditional（`Wtm?.LoginUserInfo?.Roles`）把「`Wtm` 是 null」變成靜默的空角色清單而非拋例外，`context.Result = Forbid()` 因此無條件觸發。這其實是可用性缺陷（整個 ETL 管理 UI 對所有人、含真正的 Admin，100% 鎖死），不是框架讓未授權呼叫者悄悄通過；而且因為在 `OnActionExecuting` 裡設 `context.Result` 會讓整條 filter pipeline 短路，`DataContextFilter`/`PrivilegeFilter`/`FrameworkFilter` 對這五個 action 從未真正跑過一次——action body 從未真的被呼叫過，原本推測的 NRE 根本沒有機會發生。修法：新增 `WtmControllerActivator`（`src/WalkingTec.Mvvm.Mvc/Helper/WtmControllerActivator.cs`）取代預設 `IControllerActivator`，在 controller **建構時**（早於任何 filter，含寫死排第一的 `ControllerActionFilter`）就把 `Wtm` 設好——這是框架層級的修法（在 `AddWtmContext` 註冊一次），對所有 controller/組件一視同仁，不是只補這五個 controller，未來任何第三個組件踩到同樣 anti-pattern 也一併涵蓋。**範圍確認**：`WalkingTec.Mvvm.WorkFlow` 的 controller 共用同一個 `BaseController`，理論上同樣暴露在這個排序缺口下，但沒有一個會在自己的 `OnActionExecuting` 裡讀 `Wtm`，所以從未出現症狀——這次修法對它們同樣有益，不需要額外改動。**Blast radius 已查**：`WalkingTec.Mvvm.Api.Test`（76）、`WalkingTec.Mvvm.Mvc.Tests`（52）、`WalkingTec.Mvvm.Etl.Test`（713）、`WalkingTec.Mvvm.WorkFlow.Test`（591）全數重跑，全綠，ETL controller 現在真的可以被呼叫到也沒有引出潛在失敗。測試：`EtlControllerGateHttpTests.cs` 新增三個真實 HTTP 的 positive control（真正的 ETLAdmin 現在可以通過守衛、跑到 action body），補齊 #841/#862 那條說明的「只能在 unit test 驗證」缺口。Mutant `876-wtmcontrolleractivator-neutralize`（`test/mutants/entries/`）：中和 `WtmControllerActivator` 的 `Wtm` 填值 guard，驗證為 KILLED。
- **WorkFlow 10 個 `ITenant` entity 從未取得全域 tenant／soft-delete query filter——同一根因家族，ETL 隔壁模組，且被獨立的跨廠架構稽核判定為全庫唯一 Critical（#899，P0）**：與 #862 完全同型的 wiring-order 缺陷——`ApplyWorkFlowModels()` 在消費端 `DataContext.OnModelCreating` 是**在** `base.OnModelCreating()` 之後才註冊 WorkFlow 實體型別，`FrameworkContext.OnModelCreating` 的 Pass 2 過濾器迴圈早就跑完、看不到之後才註冊的型別——這個過濾器**從未真正生效過**，連框架自己的 demo 參考實作都中招（demo 沒宣告任何 WorkFlow `DbSet`）。**修法不是照抄 ETL 的 helper**：Core Pass 2 對 `IPersistPoco` 型別套的是 `IsValid==true` **AND** `TenantCode==this.TenantCode` 的單一合成 filter；ETL 的 `ApplyEtlTenantFilter<T>` 只套 tenant，對 ETL 完整（四個全是 `BasePoco`），但 WorkFlow 10 個 `ITenant` entity 裡有 **6 個是 `PersistPoco`**（`ProcessDefinition`、`ProcessInstance`、`ProcessDefinitionVersion`、`ProcessDefinitionDraft`、`ApprovalTask`、`DelegationRule`）、**4 個是 `BasePoco`**（`NodeInstance`、`WorkflowTimer`、`CcRecord`、`WorkflowEventLog`）——照抄會讓 6 個 `PersistPoco` entity 永遠缺 soft-delete filter。**影響範圍因此比多租戶更廣**：soft-delete filter 缺失連單租戶部署都中——修復前所有框架標準查詢都看得到已軟刪的 `ProcessDefinition`/`ApprovalTask`/`DelegationRule`。新增 `ApplyWorkFlowModels(ModelBuilder, EmptyContext)` 多載，依每個型別是否為 `IPersistPoco` 動態決定合成過濾器或純 tenant 過濾器（與 Pass 2 同邏輯），註冊後立刻套用；舊零參數多載保留 `[Obsolete]`、行為不變。**Migration：無**——10 個 entity 早就實作 `ITenant`，`TenantCode` 欄位/索引早就註冊，只是過濾器沒套上，不是 schema 變更。**引擎背景寫入逐站點驗證，非印象**：窮舉 `src/WalkingTec.Mvvm.WorkFlow/Engine`／`Definition` 下每個實際落地（`db.Set<T>().Add(...)`／`_dc.AddEntity(...)`）的 `new NodeInstance/ApprovalTask/WorkflowEventLog/WorkflowTimer/CcRecord/ProcessInstance/ProcessDefinition*` 建構——全部從已知租戶來源（擁有它的 `ProcessInstance`/`NodeInstance`/`ProcessDefinition`，或透過 `IgnoreQueryFilters()` 跨租戶讀出的快照變數）填 `TenantCode`；唯一不填的是傳給 notifier 的暫時性殼物件，從未落地，逐一讀原始碼確認、非憑命名推斷。**引擎 query filter 活化是裁定過的決策，非意外**：本修法把 `WorkflowTimerExecutor.*` 裡 30+ 處原本無作用的 `IgnoreQueryFilters()` 活化成有效呼叫。刻意保留裸（未具名）`IgnoreQueryFilters()`、不改用 EF Core 10 具名過濾器——那些站點全是無 per-request 身分的跨租戶系統掃描，且修復前（無過濾器時）本來就看得到 `IsValid=false` 的列；合成過濾器＋全裸 ignore 保留了既有 soft-delete 可見度，只有租戶維度從「沒東西可忽略」變成「明確跨租戶忽略」，不是把掃描可見度意外收窄到只剩 `IsValid=true`。全 599 個原有 WorkFlow 測試在過濾器活化後原封不動通過（另見下方 PR #918 review 新增的 6 個測試）。**意外挖到並修正的第二個缺陷**：`IWorkflowDefinitionStore.CreateDefinitionAsync` 的重複 Code 檢查完全依賴（原本不存在的）全域過濾器，schema 自己的唯一索引是複合 `(TenantCode, Code)`（兩租戶本可共用同一 Code），實務上任一租戶用過的 Code 會擋掉其他所有租戶——本修法的過濾器活化順帶修好它，並補上專屬驗收測試（租戶 A 建立 code X 後租戶 B 仍可建立同一個 code）。**測試分兩支，因為單一版本結構上做不到**：修法新增新多載，用新 wiring 的測試在修法前根本編譯不過（非測試變紅，是編譯失敗——依既有裁定不算 RED）。重寫 `TenantFilterInvariantTests.cs` 為兩個獨立 fixture：`WorkFlowObsoleteOverloadCharacterizationTests`（demo 形狀 context、零參數多載，斷言沒有過濾器，修法前後皆綠，釘住 `[Obsolete]` 承諾，並含一支可執行重現重複-Code 缺陷的測試）＋`TenantFilterInvariantTests`（demo 形狀、新 `ApplyWorkFlowModels(this)`，斷言過濾器存在且行為正確：10 個型別的跨租戶隔離、6 個 `PersistPoco` 型別的 soft-delete 隱藏、`IgnoreQueryFilters()` 還原可見度）。舊版測試自己宣告 9 個 `DbSet` 又手動補過濾器，production wiring 壞掉也測不出來；已實測確認：`git stash` 掉本次修法後重編譯新 wiring fixture 得到 `CS1501`（編譯失敗，非紅測試）。**更正（PR #918 跨廠 review）**：驗收準則本身沒有可執行的修法前 RED——MSTest 把整個測試專案編成單一 assembly，任一檔案的編譯錯誤（這裡是新 wiring fixture）會讓整個專案的建置失敗，此專案下沒有任何 fixture 能對修法前的樹「跑起來」，連舊 wiring 重現測試也不例外。真正能在修法前後都跑、且不需要新多載的，是舊 wiring 重現測試——但它是**綠色的特徵化測試**（在永久不過濾的舊多載下斷言「錯誤結果」為預期值），不是紅測試；它證明缺陷真實存在，不能替代「修法前對正確行為的斷言會失敗」這件事，而那件事結構上就是拿不到。修法本身有效的**具約束力、CI 強制證據是下面的 mutant**。SQLite shared-memory fixture（不用 EF InMemory）。新增 mutant `test/mutants/entries/wf899-applyworkflowmodels-processdefinition-tenantfilter-neutralize.json`，完全比照 #862 的 `etl862-applyetlmodels-tenantfilter-neutralize.json` 先例（同一 entity 的紅測試＋positive control 配對）。

  **PR #918 跨廠 review 的兩個 Blocking 發現，均已修復：**

  **Blocking 1——寫入端 provenance 缺口**：filter 本身不保護一個「TenantCode 來自呼叫端參數、從未驗證過」的寫入。`WorkflowEngine.StartAsync` 的 `tenantCode` 參數過去直接寫進新建的 `ProcessInstance`，沒有任何檢查確認它與剛載入（已被 filter 限定租戶）的 `ProcessDefinitionVersion` 一致；`WorkflowDefinitionStore.CreateDefinitionAsync` 的 `tenantCode` 參數過去也直接寫進新建的 `ProcessDefinition`，沒有檢查它與呼叫端 `IDataContext` 自己的 `TenantCode` 一致。呼叫端一旦傳入不一致的值（含最容易踩到的：單純省略、傳 `null`），就會悄悄寫出一列自己的 context 再也查不到的資料。兩個方法現在都在任何寫入前，把參數與已授權來源（`StartAsync` 比對 `version.TenantCode`；`CreateDefinitionAsync` 比對 `_dc.TenantCode`）比對，不一致（含 null-vs-非-null）一律拋 `InvalidOperationException`——fail closed，不是悄悄寫錯地方。新增 4 支測試，各自用 `IgnoreQueryFilters()` 證明「真的沒有任何列被寫入」（不只是「呼叫端自己看不到」）。

  **Blocking 2——32 個站點的分類不完整**：「全部 32 個站點都是跨租戶掃描」不能推出「全部都該連 soft-delete 一起忽略」。重新逐站點稽核，切成兩類：(a) **跨租戶但必須保持 `IsValid == true`**——決定「這是不是活的、該不該處理」的候選查詢（`Fire.cs` 的 GATE-0、`Escalate.cs`/`Remind.cs`/`AutoAction.cs` 的 task-scoped 讀取、`Sweep.cs`/`StrandReaper.cs` 的候選查詢）與所有 `Notify*Async` 的 post-commit 重讀，全部加上明確的 `IsValid == true` 條件；(b) **跨租戶且必須看得到 tombstone**——兩個 WF-19 FIX-2 唯一索引碰撞預檢（`Escalate.cs`/`Sweep.cs` 各一），刻意保持裸 `IgnoreQueryFilters()`，並補上註解說明：它們探測的唯一索引本身不排除 `IsValid=false`，soft-deleted 列依然佔著那個槽位，真的 INSERT 下去照樣會撞——探測必須看到資料庫自己會拒絕的東西。新增兩支對真實 `WorkflowTimerExecutor.RunTickAsync` 跑的端對端測試（非重新實作），證明 soft-deleted `ProcessInstance`（GATE-0）與個別 soft-deleted 的 `ApprovalTask`（task 層修法）都達到「零 mutation、零 event、零 notification」；每支都刻意設計成只綁定「這一行」的貢獻，排除掉其他幾層獨立防護（`GuardedTransition` 的 CAS helper 沒用 `IgnoreQueryFilters()`，本來就會被已生效的合成過濾器擋下大多數情境）——實測還原每個修法逐一確認：哪個斷言真的變紅、哪個沒有。全 WorkFlow 測試套件（605 個：原本 599 ＋ 4 個寫入 provenance ＋ 2 個 soft-delete 分類）在過濾器活化後全數通過。

  **未修範圍，刻意**：引擎 tenant 語意設計不變——本票是 ETL 同款止血修法，不是重新設計；結構性根因（任何零參數 `ApplyXxxModels(this ModelBuilder)` extension method 都無法把過濾器綁定到 context instance）交給另一張 issue #901（`IModelFinalizingConvention`，框架層級關類別，涵蓋未來組件與未重新編譯的既有消費端）；`ApplyDashboardModels` 是同型零參數 pattern，目前僥倖無事只因其 entity 用手工 `TenantId` string、不實作 `ITenant`，#901 若這點改變會自動涵蓋。

  **上面的過濾器必要但不充分——同一 PR 內的 session 半邊補完。** WorkFlow 每個 request-path 服務（`WorkflowEngine`/`WorkflowTimerExecutor` 經 `ScopedWorkflowDataContextHolder.Resolve()` → `ResolveDataContext`、`WorkflowDefinitionStore`、`ProcessDefinitionPublisher`）都是呼叫 `IWtmDataContextFactory.CreateDC()` **不帶任何參數**來自鑄模組專屬的 DataContext——`WtmDataContextFactory.CreateDC` 的租戶解析只在收到 `currentTenant` 參數時才跑，所以這些 context 的 `TenantCode` 一律、無條件是 `null`，不管實際呼叫者是誰。在已遷移的多租戶部署下，新生效的過濾器（`TenantCode == this.TenantCode`）因此對這三個服務的每一次讀取都比對出**零列**——本 PR 自己文件教的遷移動作，反而會讓 designer UI 列得出定義、引擎卻讀取不到（比修復前完全不過濾還糟）。單租戶部署下這個缺口是 no-op（`null == null`），所以 model 半邊單獨看在單租戶已經完整有效。

  修法是 `SetTenantCode` **戳記，不是連線改路由**：新增 `ResolveAmbientTenant(IServiceProvider)`（`ServiceCollectionExtensions.cs`）讀 `sp.GetService<WTMContext>()?.LoginUserInfo?.CurrentTenant`——與 `WTMContext` 自己的 `CreateDC()` instance method（`WTMContext.CreateDC.cs`）替其他每個框架 DataContext 解析租戶用的同一個來源；`ResolveDataContext` 在 factory 建出 context 後立刻呼叫 `dc.SetTenantCode(ResolveAmbientTenant(sp))`（涵蓋經 holder 解析的 engine／timer executor），`WorkflowDefinitionStore`／`ProcessDefinitionPublisher` 各自新增 `(IWtmDataContextFactory, string? tenantCode)` ctor 多載做同樣的事，由各自的 DI factory lambda 呼叫——既有零／兩參數 production ctor 不變，只是委派給新多載並傳 `tenantCode: null`（binary compatible）。**刻意不用 `CreateDC(currentTenant: ...)`**：`WtmDataContextFactory.CreateDC` 對 `IsUsingDB == true` 且無顯式連線字串 key 的租戶會改連到不同的實體資料庫（`CreateTenantDC`）——本模組向來把 `Wf_*` 寫在預設連線，走參數路徑等於在過濾器修法裡夾帶一次連線改路由的相容性破壞。背景 timer scope 沒有 `HttpContext`，`WTMContext.LoginUserInfo` 因此短路回 `null`——`ResolveAmbientTenant` 在那裡回傳 `null`，與修法前 `ResolveDataContext` 一貫產生的值完全相同，所以 timer/reaper 路徑（早已靠 `WorkflowTimerExecutor.*.cs` 既有的逐候選列 `SetTenantCode` 呼叫做到 tenant-aware）逐位元組不變。這個戳記修法本身不動 `TimerReaperTests.cs`，該檔既有 42 支測試不受影響——該檔唯一的新增是下面這個由跨廠 review 找到、與本修法相鄰但獨立的缺陷。

  **跨廠 review 稽核本票時另外抓到的第二個獨立缺陷：`Returning` 子狀態下的軟刪除 instance，其 timer 會永遠卡在 Armed，沒有任何路徑會被 retire。** `WorkflowTimerExecutor.Fire.cs` 的 GATE-0 把 `Returning` 子狀態的 defer 判斷（`if (instanceSnap.State == InstanceState.Returning) { ...; return; }`）放在 `isOrphan` 判斷（含本 PR 稍早 T-899-1 加的 `|| !instanceSnap.IsValid`）**之前**——一個同時是 `Returning` 又軟刪除的 instance，每個 tick 都會走進 defer-and-return 分支，永遠碰不到本該 retire 它的那個 disjunct。Defer 判斷自己的註解承諾的兩條出路，對這個 cell 都不成立：一是 回退 完成、`Generation` 前進（沒有任何程式會對已軟刪除的 instance 動作，這條路不會發生）；二是 lease 過期由 Phase-2 回收（`WorkflowTimerExecutor.StrandReaper.cs` 的 `ReclaimExpiredLeasesAsync`——但這條查詢本 PR 稍早也已經加上排除 `IsValid == false`，理由正是「已軟刪除、卡在 Returning 的 instance 不該被回收回 Running」，所以這條路也不會發生）。兩條路都讀過原始碼逐一確認過，不是假設。**修法是在既有 defer 判斷加上 `&& instanceSnap.IsValid`——刻意不是把兩段判斷順序對調**：`isOrphan` 自己的 `State != InstanceState.Running` disjunct 對任何 `Returning` instance（不管是否軟刪除）恆真，若單純把 `isOrphan` 判斷往前搬，會連正在進行中、合法的 回退 也一併 retire 掉——這正是 FIX-A2 當初的設計理由（縮小 fire-vs-return 競爭窗、保護存活節點 SLA）要防的回歸。`Returning`＋合法 instance 的 defer 行為完全不變——`T-TMO-22c`（`TimerReaperTests.cs`）原封不動，證明這點。已對全部 `WorkflowTimerExecutor.*.cs` 檔案 grep 同一個 `InstanceState.Returning` defer pattern：只有 `Fire.cs` 的 GATE-0 是 defer 站點；`StrandReaper.cs` 的兩處 `Returning` 引用是（早已正確）的 Phase-2 回收查詢，不是 defer。新增測試 `T-899-3`（`TimerReaperTests.cs`）證明修好的這個 cell：`Returning`＋軟刪除 instance 的到期 timer 會被 retire（轉成 `Fired`，零 event、零 notification），不會永遠卡 Armed——已實測確認：拿掉 `&& instanceSnap.IsValid` 這個 conjunct 時變紅（`Assert.AreEqual failed. Expected:<Fired>. Actual:<Armed>`），補回去變綠。WorkFlow 套件：611 → 612。Mutant `test/mutants/entries/wf899-fire-returning-isvalid-ordering-reintroduce.json` 把順序改回去（拿掉那個 conjunct，重現修法前的確切缺陷）——`VERDICT: KILLED` / `GATE: PASS`。

  **Controller 對齊，是 guard 比對同源值的必要條件。** `WorkflowInstanceController.Start`、`WorkflowTaskController.Inbox`、`WorkflowDesignerController.CreateDefinition` 原本讀 `Wtm.LoginUserInfo?.TenantCode` 組出傳給引擎/store 的 `tenantCode`——這是與 `CurrentTenant` **不同**的屬性（`LoginUserInfo.CurrentTenant` = `_currentTenant ?? TenantCode`，一般使用者恆等，僅 host-admin 切換租戶情境下有別）。三處全部改讀 `CurrentTenant`，與 `ResolveAmbientTenant` 替 DataContext 戳記用的同一個屬性——否則 `WorkflowEngine.StartAsync`／`WorkflowDefinitionStore.CreateDefinitionAsync` 的寫入 provenance guard（上文）比對的會是兩個各自獨立推導、只是「通常」剛好一致的值，而非真正同源不變式；host-admin 切換租戶時就會讓 guard 每次寫入都誤炸。

  **未遷移消費端的一次性自檢 log。** 新增 `WarnIfTenantFilterMissing`（`ServiceCollectionExtensions.cs`）：process 內第一次解析出 factory 建立的模組 DC 時，檢查 `ProcessDefinition` 是否有生效中的 query filter；沒有（消費端仍呼叫舊零參數多載）就發一次 log——`GlobalData.AllTenant` 非空時 `LogError`（真實多租戶部署、真正無防護）、否則 `LogWarning`——訊息載明 #899 與一行遷移指令。這不改變相容性要求下未遷移消費端「維持不過濾」的行為，只是讓這個狀態從無聲變有聲。

  **Session 半邊測試，每條斷言都指得出「刪哪一行變紅」**（`SessionTenantStampingTests899.cs`，新增 6 支，WorkFlow 套件從 605 增至 611）。既有 605 支測試全用 direct-DbContext test ctor 或手工戳記 context，結構上碰不到這個缺陷——這正是 production 壞掉而 CI 照樣綠燈的原因；demo 已呼叫 `ApplyWorkFlowModels(this)` 讓 e2e 跑的是遷移後的 model 形狀，但 e2e 是單租戶，null==null 讓 session 半邊的缺口在那裡也是 no-op——CI 結構上沒有任何路徑抓得到。(a) `CurrentTenant=='A'` 的 DI scope → holder DataContext 的 `TenantCode=='A'`——變紅行：`ResolveDataContext` 內的 `dc.SetTenantCode(ResolveAmbientTenant(sp))`；(b) 經**真實 DI 解析**的 store/engine（非手工戳記 context，比照 `ProdDiReproTests.cs` 建容器的方式）跑雙租戶行為：租戶 A 建立定義、列出、發布；租戶 B（全新 scope）在自己的列表裡看不到，`StartAsync` 帶 A 的 versionId 得 not-found——同一條變紅行；(c) 無 `HttpContext`/`LoginUserInfo` 的背景 scope → DataContext 的 `TenantCode == null`——變紅行：`ResolveAmbientTenant` 若被改成任何非 null 回退（本測試專門攔住未來「順手」也給背景 scope 戳記的回歸）；(d) 經 DI 的 guard 同源不變式：`CreateDefinitionAsync` 帶 'A' 成功，接著用反射直接改私有 `_dc` 欄位把底層 context 手動戳成 'B'（模擬漂移）後，同樣帶 'A' 會 throw——變紅行：`CreateDefinitionAsync` guard 的 `throw`；(e) 自檢 log 雙向：未遷移 model 剛好發一次、已遷移 model 完全不發——變紅行分別是 `WarnIfTenantFilterMissing` 內的 `LogWarning`/`LogError` 呼叫、與 `hasFilter` 提前 return。新增 compile-preserving mutant `test/mutants/entries/wf899-resolvedatacontext-tenantstamp-neutralize.json`（把戳記值換成寫死的 `null`，含 positive control），鎖定 (a)/(b)——`VERDICT: KILLED` / `GATE: PASS`，比照既有 `wf899-applyworkflowmodels-processdefinition-tenantfilter-neutralize.json`。

  **文件修正為教完整遷移，不只 model 半邊**（`docs/workflow.md`、`docs/wtm-developer-manual.md`、`docs/workflow-engine-spec.md`——與本 PR model 半邊已編輯的同三個站點；`demo/WalkingTec.Mvvm.Demo/DataContext.cs` 不需改，原本就對）。三份文件現在都講明：只要呼叫了 `ApplyWorkFlowModels(this)`，request path 的租戶範圍就自動生效，不需消費端額外動作；`ApplyWorkFlowModels(this)` 本身不會產生 EF migration（query filter 是 model metadata，非 schema）；背景 timer 路徑天生 tenant-aware；消費端若自己寫背景程式（自訂 `IHostedService`、Quartz job、console 工具）直接驅動 `IWorkflowEngine`，必須自行對建立的 context 呼叫 `SetTenantCode`——WTM 在手動建立的 scope 裡沒有 ambient 身分可讀。

  **僅升級套件不啟用租戶隔離。** Query filter 只對呼叫 `ApplyWorkFlowModels(this)` 的消費端生效；未遷移的消費端行為完全不變（仍不過濾），現在會收到上面那則一次性 log 取代無聲。這裡不做「tenant isolation complete」這類完備性宣稱——對應的 `CHANGELOG.md` 條目與尚未關閉的 #901（涵蓋未遷移／晚註冊消費端的結構性關閉）另見。
- **demo `FileApiController`（LayUI/Vue3/Blazor 三份 template 各一份）九個洞、四類，全修（#830）**：五個 `[Public]` 匿名端點移除（`GetFile` 等，搭配預設 `EnforceTenantFileScope=false` 曾讓任何人猜 GUID 讀他租戶檔案）；`DeletedFile` 改走 `DeleteFileTenantScoped`（原本任一已認證使用者可刪除他租戶的檔案列）並改 `[HttpPost]`；`csName` 全八個動作都先過 `IsKnownConnectionKey`；`GetFileInfo` 改走 `WtmFileProvider` 並只回傳投影欄位。**相容性代價誠實記錄，不是零**：`framework_layui.js`／`MultiUploadTagHelper.cs` 是隨 NuGet 套件出貨給每個下游 app 的共用資產（demo controller 本身只是 scaffold-time 複製的 template，不是），若直接全面改成只送 POST，任何「升級套件但沒動自己複製的 controller」的下游會因為對方 controller 仍是 `[HttpGet]` 而 405、刪除按鈕悄悄壞掉——已在審查中重現。修法：這兩份共用資產先送 POST，只在收到 405 時 fallback 成 GET（且只在 405 時，不吞其他錯誤），對已配合改 `[HttpPost]` 的下游立即拿到完整 CSRF 強化、對還沒改的下游維持原本行為（原本就有的 GET-CSRF 曝險，非新增）。**Vue3 template 的伴隨修復**：Vue3 走純 JWT（`LoginJwt` 從不呼叫 `SignInAsync`，無 auth cookie），拿掉 `[Public]` 前若不修前端，`<img>`/`el-image` 這類瀏覽器原生請求（不帶 axios 的 Authorization header，也沒 cookie 可退）會讓每張圖全部 401 —— 已改走既有的 `fileApi().getFile()`（axios 帶 Bearer + blob URL）而非直接綁原始 URL，涵蓋 `stores/userInfo.ts` 大頭貼、`uploadImage/index.vue` 預覽、`table/index.vue` 圖片型欄位三處。`test/mutants/manifest.json` 新增三筆 `fileapi830-*` mutant（`GetFile`／`DeletedFile` 的 tenant-scope／csName guard 三個守門）。**#857 更正**：這裡曾寫「1/5 個 `[Public]` 移除點有 mutant 釘住，其餘四個靠 HTTP 測試涵蓋、非 mutant 級證明」——查驗當下 `FileApiControllerHardeningTests830.cs` 其實只有 3 個 `[TestMethod]`，`GetFileName`／`GetFileInfo`／`GetUserPhoto`／`DownloadFile` 四個動作連 HTTP 測試都沒有，這句宣稱本身不成立。已補齊：四個動作各自新增 marker-based 的 401/leak HTTP 測試，並各補一筆 `fileapi857-public-*-reintroduce` mutant（重新加回 `[Public]`，紅測試需為對應的新測試），現在五個 `[Public]` 移除點全部有 mutant 級證明，不再是純 HTTP-測試層級的宣稱。**GET fallback 有明訂退場**：POST-then-GET-on-405 不是永久行為，追蹤於 #853，觸發條件是 WTM 下一次「major」版號（`version.props` 由 `10.x` 跳到 `11.0.0+`）——刻意選比這個 repo 慣用的 breaking-change 載體（minor 版號）更嚴格、更少見的門檻，兩處 fallback 程式碼與 CHANGELOG 都已標註 #853，避免重蹈 `#470`/`#567` island-render 旗標「預設關、沒人記得要翻」的覆轍。**Blob URL 生命週期**：`URL.createObjectURL` 這個 ClientApp 本來就有一處既存呼叫、卻從未 `revokeObjectURL`；本次把三個新讀圖點都接上同一個 helper，等於把既有的潛在洩漏放大（`table/index.vue` 尤其明顯——每次搜尋/翻頁/篩選都會為每列每個圖片欄位各建一個 blob URL）。已補上：每個呼叫點在建立新 URL 前先 revoke 即將被取代的舊值。**#857 更正**：這裡曾多寫一句「元件 unmount 時 revoke 剩餘持有值」，對三個呼叫點一概而論——`uploadImage/index.vue`／`table/index.vue` 是 Vue 元件，各自掛了 `onUnmounted` 確實會 revoke；但 `stores/userInfo.ts` 是 Pinia store，沒有元件生命週期可掛，只在 `setUserInfos()` 命中 sessionStorage 快取、要用新值取代舊值那一刻才 revoke，store 本身消失時不會額外 revoke（見 #856）。`stores/userInfo.ts` 另外修正一個關聯正確性問題——blob URL 存進 sessionStorage 後在重新整理頁面時會失效，改為快取 `photoId` 並在 cache hit 時重新解析。

- **#883（P0，已修）：#841/#862 的 `IgnoreQueryFilters()` 有多處位於 HTTP controller 共用的路徑上，形成跨租戶 IDOR——與 #876 一起落地。** `b3dbae4b3` 為了讓背景排程器在新啟用的 tenant filter 下仍能運作加了 17 處 `IgnoreQueryFilters()`，其中 `EtlSchedulerService` 的 `TriggerNowAsync`/`RescheduleAsync`/`SkipNextAsync`/`DryRunAsync`/`RerunFromSnapshotAsync`/`ScheduleJobAsync`/`UpdateStatusAsync` 七處是 HTTP controller（`_EtlJobController`/`_EtlRunLogController`/`EtlJobDefinitionVM`）的共用路徑，不是純背景；複查程式碼另外發現 `AbortAsync` 是同一類缺口的第八處（原始回報清單沒列，依「發現安全問題後全庫掃同一 pattern」慣例一併修）。**鏈路**：`_EtlMonitorController.Running` 對任何通過角色閘門的 ETLAdmin 回傳 `EtlProgressTracker`（無租戶維度的 `ConcurrentDictionary<Guid, EtlProgress>`）裡所有租戶的 `JobId`；拿到別租戶的 id 後餵進 `DryRun`（caller-controlled id，`IgnoreQueryFilters()` 載入後回傳來源資料預覽列）或 `TriggerNow`/`SkipNext`/`Reschedule`，即可讀取或操作其他租戶的 job——角色閘門只檢查 `Admin`/`ETLAdmin`，不比對輸入列與呼叫者的 `TenantCode`。**#876 意外遮住這條路徑**：#876 修復前 `Wtm` 恆為 null，角色閘門對所有人 fail-closed，這個「全員鎖死」的可用性缺陷剛好也擋住了這條 IDOR；#876 一旦修復，閘門開始正常放行合法呼叫者，這條路徑立刻變得可利用——因此兩者在同一 PR（#882）裡一起落地，不留中間視窗。**修法**：`EtlSchedulerService` 新增 `LoadJobDefinitionForCallerAsync`（job 查詢）/`EnsureCallerOwnsJobAsync`（純 Quartz 呼叫）兩個共用私有 helper，每個 HTTP 可達的方法都先過這一關才動 Quartz 或寫 DB；沿用 #843 已建立的 `declaredSystemQuery`（具名、review 時看得到的布林參數，非設定檔旗標）契約區分 HTTP 呼叫端（tenant-scoped，預設）與背景呼叫端（明確宣告 unfiltered）——目前沒有任何 production 呼叫點傳 `true`。`EtlProgressTracker.Get`/`GetAll` 也補上同一組 `callerTenantCode`/`declaredSystemQuery` 參數，補齊 #841 本文就點名「這個閘門沒修到」的那個缺口。**同一 commit 一併處理的兩項**：(A) `[Obsolete]` 的舊 `ApplyEtlModels()` 多載完全沒有 tenant filter，`docs/etl-module.md`／`docs/workflow.md`／`docs/wtm-developer-manual.md`（兩處）已全部改教新的 `ApplyEtlModels(this)` 寫法並加註解說明原因。(B) `EtlQuartzJob` 背景執行時原本把 dead-letter/lineage 的 `TenantCode` 寫成背景 scope 的 `dc.TenantCode`（恆為 null），已改用已載入的 `jobDef.TenantCode`——修復前，內建排程器為任何多租戶 job 產生的 dead-letter/lineage 列，升級前後都是 null，然後被 #862 的新 filter 對所有真實租戶隱藏；`EtlRunLog` 不受影響（本來就正確使用 `jobDef.TenantCode`）。**測試**：`EtlSchedulerCrossTenantIdorTests883.cs` 對七個原始項目＋`AbortAsync` 逐一提供真實 HTTP 負向測試（租戶 A 的 ETLAdmin 對租戶 B 的 job 一律被拒），另加同租戶 positive control（`SkipNext_OwnTenantJobId_Succeeds`）與 `EtlProgressTracker` 直接測試（正確只回傳呼叫者自己租戶的執行進度）。Mutant `883-loadjobdefinitionforcaller-tenantcheck-neutralize`：中和 `LoadJobDefinitionForCallerAsync` 的租戶檢查，驗證為 KILLED（`AbortAsync` 因其自身的「job 未在執行中」邏輯剛好也會在同一測資下回同樣的 400，經實測確認對這條 mutant 不是有效的判定訊號，未列入該 mutant 的紅測試，但功能上仍是有效的迴歸測試）。來源：`/codex:adversarial-review`（gpt-5.6-sol）對 `b3dbae4b3` 的單題驗證，主 session 已複驗 `DryRunAsync` 的 caller-controlled id 與 `EtlProgressTracker` 無租戶維度兩點。

  **PR #882 的第二輪 cross-vendor review（同一 gpt-5.6-sol session）Request changes，抓到三項未完成，均已處理**：(1) **Dashboard tracker 洩漏只補了一半**——`_EtlDashboardController.Stats` 經 `EtlDashboardService.BuildSummary` 呼叫的兩處 `_tracker.GetAll()` 當時仍是無參數版本，預設 `callerTenantCode == null` 落到 `TenantCode == null` 的 host-scope job，租戶 A 因此看不到自己的 running job、卻看得到 host-scope 的 `JobId`/名稱/phase/速率——方向相反的洩漏＋功能迴歸同時發生。review 明確要求「掃全部 `_tracker.Get`/`GetAll` 呼叫端而非只補這兩處」：`EtlProgressTracker.Get`/`GetAll` 的 `callerTenantCode` 改成**不可省略**（無預設值），`BuildSummary` 改收必要的 `callerTenantCode`，`_EtlDashboardController.Stats` 傳入 `Wtm.LoginUserInfo?.CurrentTenant`，新增 `DashboardStats_ReturnsOnlyCallersOwnTenantsRunningJobs_NotHostScope` 真實 HTTP 測試（同時斷言租戶 A 看得到自己的 job、看不到 host-scope 的 job）。(2) **全域 `IControllerActivator` replacement 改壞相容性**——第一版 `services.Replace(...WtmControllerActivator)` 無條件蓋掉既有註冊，對照 ASP.NET Core 10 原始碼證實：`AddMvcCore()` 用 `TryAddTransient` 註冊 `DefaultControllerActivator`，host 若額外呼叫 `.AddControllersAsServices()` 會換成 `ServiceBasedControllerActivator`（controller 的建立與**釋放**都交給 DI container，不是手動 `Dispose()`）——無條件 `Replace` 會悄悄關掉這個機制；第一版手刻的 `Release()` 也只檢查 `IDisposable`，沒覆寫 `ReleaseAsync`，async-only-disposable controller 不會被正確釋放。修法改成**裝飾（decorate）現有的 registration** 而非取代：`WtmControllerActivator` 建構時接收既有 activator 當作 `_inner`，`Create` 呼叫 `_inner.Create` 後才補 `Wtm`，`Release`/`ReleaseAsync` 原封不動轉呼叫 `_inner`——不論既有 activator 是哪一種，其建立與釋放語意都完整保留；同時把 `AddWtmContext()` 改成要求 `IControllerActivator` 已註冊（否則丟 `InvalidOperationException`），即必須排在 `AddMvc()`/`AddControllers()` 之後——本 repo 兩個真實 `Startup.cs` 本來就是這個順序，不是新增的限制。review 建議改用 `IControllerPropertyActivator`，經反射對照真實安裝的 SDK 證實該介面（與 `DefaultControllerActivator`）都是 `internal`，此組件外部無法實作或存取，該建議不可行——這點回覆 review 是「不成立，附證據」而非採納。至於「controller 建構時 `WTMContext` resolution 若丟例外，現在會不會把原本可 short-circuit 的回應變成 500」，對照 ASP.NET Core 10 `ControllerActionInvoker` 原始碼證實 controller 建構落在 `State.ActionBegin`，嚴格晚於 Authorization filter 與 Resource filter 階段——`[Authorize]`／resource filter 的 short-circuit 順序完全不受影響；對這五個 controller 而言，這個 resolution 呼叫在修復前根本永遠不會執行到（正是要修的那個 bug讓它們的 `OnActionExecuting` 先用 `Forbid()` 短路），修復後只是讓它們跟 app 裡所有其他 controller 一樣，在每個 request 都會真的解析一次 `WTMContext`——不是新增一種失敗模式；因此沒有加 try/catch（加了反而會把「DI 設定壞掉」這種本該 fail-fast 的啟動期錯誤，靜默降級成難以診斷的 `Wtm` 為 null 狀態）。(3) **十個 `EtlSchedulerService` public virtual 方法簽章是二進位 breaking change**——`callerTenantCode`/`declaredSystemQuery` 是 C# 編譯期 optional parameter，CLR 層級的方法簽章確實變了，既有 NuGet consumer 或既有 override 需要 `MissingMethodException`/重新編譯。審慎評估後**選擇接受 breaking change 並照 Red Line 走完整流程**，而非提供相容 shim：相容 shim 勢必要為省略的 `callerTenantCode` 選一個預設值，選 `null`（等同今天的舊行為）等於讓還沒重新編譯的呼叫端繼續暴露在 #883 要關的那個跨租戶 IDOR 下，沒有一個預設值能同時做到「向前相容」與「安全」；`EtlProgressTracker`/`EtlDashboardService.BuildSummary` 的 `callerTenantCode` 更進一步做成必填（無預設值），直接對應上面 (1) 的教訓——省略即不安全的參數，不該讓呼叫端能靠省略就取得舊行為。版本號 10.20.0 → **10.21.0**（minor），`CHANGELOG.md` 新增 `### Changed`／`### Migration` 章節，說明哪些成員簽章變了、既有呼叫端要怎麼改。三項均已在同一分支處理，rebase 到目前 `dotnet10` 後重跑全部 mutant 與回歸測試（見 CHANGELOG 對應條目與 PR #882 本身的紀錄）。

  **第三輪 cross-vendor review（同一 gpt-5.6-sol session，改用 `ilspycmd` 對照本機真實 10.0.10 組件而非只讀原始碼）Request changes，兩項阻擋 + 一項文案錯誤，均已處理**：(1) **Decorator 沒有完整保留 inner activator 本身的 disposal contract**——`AddWtmContext` 自己建立 inner instance（呼叫既有 descriptor 的 `ImplementationFactory`，或對 `ImplementationType` 跑 `ActivatorUtilities.CreateInstance`），這繞過了 DI container 自己的建立路徑——container 只認得、只追蹤外層 `WtmControllerActivator`，而它先前沒實作 `IDisposable`／`IAsyncDisposable`，所以一個原本會被 container 釋放的第三方 disposable `IControllerActivator`，包裝後會悄悄洩漏（內建兩個 activator 都不是 disposable，既有 HTTP 測試因此抓不到）。同一段還漏了 `ServiceDescriptor.IsKeyedService` 檢查——若最後一筆 `IControllerActivator` registration 是 keyed 的（.NET 8+ keyed services），其 unkeyed 的 `ImplementationType`/`Factory`/`Instance` 全部是 null（對照真實 `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.9 組件驗證：三個 getter 都先檢查 `IsKeyedService` 才決定回傳值或丟例外），會在解析時丟例外——而且就算不丟例外，unkeyed 的 `GetRequiredService<IControllerActivator>()`（這個 activator 與 MVC 本身實際呼叫的方式）本來就不可能解析到 keyed registration，所以先前的邏輯連「選到」都選錯了。**修法**：`WtmControllerActivator` 新增 `IDisposable`／`IAsyncDisposable` 與建構子的 `ownsInner` 旗標——`AddWtmContext` 自己建出 inner 時（`ImplementationFactory`/`ImplementationType` 兩種情形）傳 `true`（沒有其他人會釋放它，wrapper 必須負責），inner 是既有的、可能跨 request 共用的 `ImplementationInstance` 時傳 `false`（container 本來就不會自動釋放 `ImplementationInstance` registration，理由相同：那是呼叫端自己擁有生命週期的共用實例，單一 scope 結束就釋放會拖垮下一個 request）。`LastOrDefault` 加上 `!d.IsKeyedService` 過濾。**同時修正一個文案錯誤**：上一版 doc comment 誤稱 `ServiceBasedControllerActivator` 是 `internal sealed`（跟 `DefaultControllerActivator` 一樣）——用 `ilspycmd` 對照本機真實 10.0.10 組件證實它其實是 `public class`，不是 sealed 也不是 internal；設計本身不受影響（這個類別本來就不需要點名任一個具體型別），但文案本身確實錯了，已在 `WtmControllerActivator.cs` 的 doc comment 訂正。**測試**：`src/WalkingTec.Mvvm.Mvc.Tests/Security/WtmControllerActivatorDisposalTests.cs`——六支直接針對 `WtmControllerActivator.Dispose`/`DisposeAsync` 的單元測試，加三支呼叫「真正的」`AddWtmContext`（不是抄一份 mirror）端到端驗證：`ImplementationInstance` 型 inner 在 scope 結束時確認不會被釋放（共用/呼叫端擁有），`ImplementationType` 型 inner（修復前會洩漏的那種）確認會被釋放，keyed `IControllerActivator` registration 確認被正確跳過、改包裝真正的 unkeyed 那個。

  (2) **Migration 文字本身有錯，而且公開文件仍教舊 API**——上一版 CHANGELOG Migration 寫「已經自己解析 tenant 的呼叫端不需要改 source」，這句話本身邏輯不通：舊簽章根本沒有 tenant 參數可傳，不可能有呼叫端「已經」在傳。重新編譯後若呼叫端不改、仍寫 `PauseAsync(jobId)`，新參數會靜默綁定成 `callerTenantCode: null`，只會命中 host-scope（`TenantCode == null`）的 job——一個原本操作自己租戶 job 的呼叫端，升級後會開始對自己的 job 收到 `InvalidOperationException: Job {id} not found.`，這是真正的 data-plane 行為改變，不是無痛升級。**已重寫成四種情形**（tenant-scoped 呼叫端必須傳 `callerTenantCode: Wtm.LoginUserInfo?.CurrentTenant`；真正跨租戶的 background/system 呼叫端才傳 `declaredSystemQuery: true`；derived override 必須同步更新完整簽章；只有真正的 host-scope 呼叫端才可以省略或明確傳 `null`）。另外 `docs/etl-module.md`（`EtlSchedulerService API` 區塊）仍列舊的、無 tenant 參數的簽章，`docs/wtm-developer-manual.md` 的 `BuildSummary(IDataContext, int, int)` 已經編譯不過——都已更新成真實簽章並補上 breaking-change 提示；順手複查同一份 `docs/etl-module.md` 還發現 `EtlProgressTracker` 的 code block 也是舊簽章（review 沒點名，屬於同一類問題，一併修正）。**member 數量**：CHANGELOG 先前寫「12 個」，實際用 `grep` 逐一數過是 **13 個**（`EtlSchedulerService` 10 個 + `EtlProgressTracker.Get`/`GetAll` 2 個 + `EtlDashboardService.BuildSummary` 1 個）——已訂正。

  (3) **CI 紅燈確認非本分支迴歸**：`build-and-test` 的 1/4727 失敗（`BaseCRUDVMAsyncTest.SingleTableDoAddAsync`，`Assert.IsTrue(DateTime.Now.Subtract(rv[0].CreateTime!.Value).TotalSeconds < 10)`，22 秒才跑完 10 秒預算）追蹤呼叫鏈：`_schoolvm.Wtm = MockWtmContext.CreateWtmContext(...)` 直接對一個 `new BaseCRUDVM<School>()` 手動賦值，`MockWtmContext.CreateWtmContext`（`test/WalkingTec.Mvvm.Test.Mock/MockWtmContext.cs`）整個用 Moq 手刻 `HttpContext`/`IServiceProvider`，完全不呼叫 `AddWtmContext`、不建立真實 `IServiceCollection`/`ServiceProvider`、不解析 `IControllerActivator`——本分支唯一改動的兩個檔案（`FrameworkServiceExtension.cs`、`WtmControllerActivator.cs`）在這條呼叫鏈上完全不可達。`CreateTime` 來自 `Wtm.TimeProvider.GetLocalNow()`，本分支也沒動過 `TimeProvider` 註冊或 `BaseCRUDVM.cs`。確認是 #885 描述的 runner-contention flake，不是這個分支的迴歸。

  **第四輪 cross-vendor review（同一 gpt-5.6-sol session）Request changes，兩項阻擋 + 一項非阻擋文案錯誤，均已處理**：(1) **sync scope 會靜默漏掉 async-only 的 inner activator**——上一輪修的 `Dispose()` 只檢查 `_inner is IDisposable`，如果 `_inner` 只實作 `IAsyncDisposable`（沒有 `IDisposable`），這個檢查會直接落空、什麼都不做、也不丟例外。修這個 wrapper 之前，DI container 自己的 sync `scope.Dispose()` 對一個同樣形狀、container 自己追蹤的 service，會丟 `InvalidOperationException`（對照真實安裝的 `Microsoft.Extensions.DependencyInjection` 10.0.9 組件驗證：`ServiceProviderEngineScope.Dispose()` 的訊息字面是 `"'{0}' type only implements IAsyncDisposable. Use DisposeAsync to dispose the container."`）——這是loud、可行動的訊號，告訴呼叫端要改用 async disposal。包裝之後這個訊號被靜默吞掉，變成「看起來成功」——這正是這個 repo 一整個月在 `BaseCRUDVM` 家族追的同一種缺陷：一個呼叫端原本分辨得出來的錯誤狀態，變成跟正常運作分不出來。**修法**：`Dispose()` 在 `_inner` 只有 `IAsyncDisposable` 時，自己重現同一個 `InvalidOperationException`（點名 inner 的實際型別，並指向 `DisposeAsync`）。刻意不用 `.GetAwaiter().GetResult()` 去阻塞完成那個 async disposal——本 repo 自己的慣例（`dotnet-conventions.md`：「Never `.GetAwaiter().GetResult()` on a request path」，而 scope disposal 確實可能發生在 request path 上）已經排除這條路；丟出同一個例外既更簡單，也符合這條慣例。新增的迴歸測試（一支直接單元測試、一支透過真正的 `AddWtmContext` wiring 的端到端測試）都斷言會丟例外、而且 inner 的 `DisposeAsync` 從未真的被 sync 路徑呼叫到。新增 mutant `882-wtmcontrolleractivator-syncdispose-asynconly-throw-neutralize`——中和這個 throw、還原成靜默 fall-through，驗證為 KILLED。

  (2) **Migration 文字仍套不進所有 13 個成員**——上一輪的四種情形表把三種不同的簽章形狀壓平成一張表，`EtlDashboardService.BuildSummary` 根本沒有 `declaredSystemQuery` 參數，情形 2（傳 `declaredSystemQuery: true`）套不上去；`EtlProgressTracker` 的 system case 因為 `callerTenantCode` 是必填，必須明講要同時傳 `callerTenantCode: null, declaredSystemQuery: true` 兩個，不能只講後半；「每個呼叫端都恰好落在一種情形」也不成立——「derived override」是消費簽章的「方式」，跟租戶／系統／host-scope 這個「身分」維度是正交的，而且只適用於十個 `EtlSchedulerService` 方法（`EtlProgressTracker`/`BuildSummary` 都不是 `virtual`，無法被 override，已逐一查證）。另外查到一個先前完全沒寫進文件、靠實際編譯才發現的相容性坑：`Func<Guid, Task> f = scheduler.PauseAsync` 現在編譯不過（實測 `CS0123: No overload for 'PauseAsync' matches delegate 'Func<Guid, Task>'`）——optional parameter 不參與 method-group-to-delegate 轉換；用參數型別集合做的 reflection 查找也是同樣道理壞掉（實測 `GetMethod("PauseAsync", new[] { typeof(Guid) })` 回傳 `null`，換成新的三參數集合才找得到）。**已依 review 建議重寫**：先分「三個簽章群組」（A：`EtlSchedulerService` 十個方法，兩參數皆 optional；B：`EtlProgressTracker.Get`/`GetAll`，`callerTenantCode` 必填；C：`EtlDashboardService.BuildSummary`，`callerTenantCode` 必填且沒有 `declaredSystemQuery`），每組各自列身分對應的傳值方式；再獨立列「消費簽章的方式」（直接呼叫／derived override，只適用群組 A／delegate 或 method group 轉換，附實測的 `CS0123` 證據／reflection 查找或既有編譯好的 binary，附實測證據）——兩個維度正交，不再混在同一張表裡。

  **第五輪 review（同一 gpt-5.6-sol session）Request changes，一項阻擋，純文件、不動程式碼**：消費簽章的方式漏了第五種——**`dynamic` dispatch**。這既不是一般的直接呼叫（重編譯後看得到參數不對），也不是 mode 4 那種靜態、編譯期就固定簽章的 precompiled 呼叫——它在**執行期**才重新綁定，三個群組的結果因此不一樣，已實際跑過驗證（不是憑推論）：群組 A（`callerTenantCode`/`declaredSystemQuery` 皆為 optional）的 `dynamic scheduler; await scheduler.PauseAsync(jobId);` **不會丟例外**——runtime binder 用 1 個引數成功對到 `PauseAsync`，`callerTenantCode` 靜默預設 `null`、`declaredSystemQuery` 靜默預設 `false`，等同呼叫端悄悄降級成 host-scope——這正是這個 migration 章節一開始要警告的那種「靜默行為改變」，只是換了一件 `dynamic` 的外衣重新出現，編譯器抓不到、執行期也不丟例外。群組 B／C（`callerTenantCode` 必填）則會在執行期丟 `Microsoft.CSharp.RuntimeBinder.RuntimeBinderException`（實測訊息：`"No overload for method 'Get' takes 1 arguments"`，`BuildSummary` 同理但確切文字依呼叫形狀而定）——安全，不是靜默。`git grep` 全庫沒有任何 `dynamic` 型別呼叫這 13 個成員中的任何一個，所以這不是本 repo 目前的 production regression，但這是一個對外發佈的套件，外部呼叫端用 `dynamic`／reflection／script 呼叫時會真的踩到——已在 `CHANGELOG.md` 的消費方式清單補上第五種，兩份文件現在一致，CHANGELOG 沒有多宣稱任何 production-readiness.md 沒說的事。

  (3) **非阻擋但仍是同一輪修正**：`FrameworkServiceExtension.cs` 的 keyed-descriptor 註解說 unkeyed 的 `ImplementationType`/`ImplementationFactory`/`ImplementationInstance` getter 對 keyed descriptor「全部會丟例外」——對照真實 10.0.9 組件反編譯，三個 getter 實際上是回傳 `null`，不是丟例外；真正會丟例外的是相反方向的誤用（對一個 unkeyed descriptor 呼叫 `KeyedImplementationType`/`-Factory`/`-Instance`）。`!d.IsKeyedService` 這個過濾條件本身沒錯，只有解釋它的註解寫錯。這是本分支 review 歷程裡第三個斷言框架內部行為卻寫錯的註解（前兩個分別是 `SetPropertyValue` 的走訪方式、`ServiceBasedControllerActivator` 的存取層級），而且是在「上一輪聲稱已重新查核兩個改動檔案裡所有註解、沒找到其他錯誤」的那一輪裡新加的——這次重新查核已把「這一輪自己新寫的註解」也納入查核範圍，不只查沿用下來的舊註解。

- **#923（P0，已修）：`JwtOption.SecurityKey` 的 padding 讓 demo-shipped 弱金鑰通過起手式檢查，任何讀得到本 repo 的人可偽造任意使用者的 JWT。** `SecurityKey` 的 setter（`JwtOptions.cs`）會把短於 32 字元的值補 `'x'` 到 32 字元——這段補齊邏輯本身沒有問題（它讓不經 `AddWtmAuthentication` 的直接建構 `new JwtOption { SecurityKey = "x" }`、或不呼叫該方法的 console/ETL host 不會在簽章時炸 `IDX10720`），問題是修復前的 `IsDefaultOrWeakKey()` 只比對**補齊後**的值是否等於字面 well-known default，從未檢查過原始值——`demo/WalkingTec.Mvvm.Demo/appsettings.json` 的 `"super"`、Vue3/BlazorDemo 的 `"superSecretKey@345"`、開發手冊 §17.1 的 `"your-256-bit-secret-key-here-min-32-chars!"` 補齊後都通過檢查，而這些值全部公開存在於 `cct08311github/WTM` 這個公開 mirror 上。**修法**：新增 `_rawSecurityKey` backing field（`SecurityKey` setter 多存一份補齊前的原始值，補齊邏輯本身一個字元都沒動——`SecurityKey_short_value_is_padded_to_32`／`SecurityKey_already_32_chars_is_not_changed` 兩支既有測試維持綠燈，作為沒動到 padding 的 positive control）；新增 `IsWeakSigningKey(out string? reason)`，判斷 `_rawSecurityKey`（不是補齊後的值）是否 null/空白、短於 32 UTF-8 bytes、或在 `KnownPublicKeys` 黑名單裡（黑名單獨立於長度判斷——手冊那個 42-byte 佔位金鑰唯一會被擋下的路徑）。原本的 `IsDefaultOrWeakKey()` 改名為 `IsFactoryDefaultKey()`（語意完全不變，只認字面 default），標 `[Obsolete]` 但保留別名以維持原始碼相容；`WtmConfigValidationExtension.IsJwtActive`（判斷「JWT 有沒有在用」）改用 `IsFactoryDefaultKey()`，SecurityKey 驗證本身改用 `IsWeakSigningKey()`——兩個 predicate 刻意不合併，否則 `SecurityKey="super"` + Issuer/Audience 仍是 localhost 的組態會讓 `IsJwtActive` 誤判為 false，連驗證器都被跳過（迴歸測試：`WtmConfigValidationTests.AddWtmConfigValidation_demo_security_key_super_with_localhost_issuer_audience_throws_on_start`）。

  **起手式行為（`FrameworkServiceExtension.AddWtmAuthentication`）**：`IsWeakSigningKey()` 為真時，非 Development 環境一律 `throw InvalidOperationException`（含未設定、demo 金鑰、字面 well-known default 三種——demo 金鑰過去「開得起來」是本次修復刻意破壞相容性的部分，遷移成本是設一個環境變數加跑一次 `openssl rand -base64 32`）；**唯一的例外是短於 32 bytes、但不在黑名單裡的自訂金鑰——這種金鑰在 Development 也照樣 throw**，理由是操作者已經做了一個帶著錯誤信念的選擇（「我設了金鑰、以為它可以用」），這個信念必須在自己桌機上就撞牆，不能只在 production 才發現。Development 環境偵測：優先讀已註冊的 `IWebHostEnvironment`（涵蓋 `ASPNETCORE_ENVIRONMENT`／`DOTNET_ENVIRONMENT`／`--environment` command-line switch／launchSettings profile，因為 ASP.NET Core 自己的 hosting 層已經把這些來源解析成同一個值），找不到才退回直接讀環境變數；兩者都沒有時**fail closed 視為非 Development**（`AddWtmAuthentication_UnsetKey_NoEnvironmentInfoAtAll_FailsClosed_Throws`）。Development 且金鑰未設定/為黑名單值時，改為每個 process 產生一次 256-bit 隨機金鑰（`RandomNumberGenerator.GetBytes(32)`），寫入 `Console.Error`（本檔案既有的 ConfigureServices 階段警告慣例，見 `AddWtmContext`（`FrameworkServiceExtension.cs`）既有的 `Console.Error.WriteLine` 寫法——`ILoggerFactory` 在這個階段還沒 build 好，無法用真正的 `ILogger`）；沒有 escape-hatch 旗標——設計裁決見下方連結，此處不重複論證。**wiring 陷阱（已避開）**：`AddWtmAuthentication` 用 `config.Get<Configs>()` 建 `JwtBearerOptions.IssuerSigningKey`，`TokenService` 建構子用 `IOptionsMonitor<Configs>.CurrentValue`——是兩個獨立綁定的物件圖；產生的臨時金鑰只塞進其中一個，就會變成「簽章用 A、驗證用 B」，簽出去的 token 全部驗證失敗。修法是 `services.PostConfigure<Configs>(c => { if (c.JwtOptions.IsWeakSigningKey(out _)) c.JwtOptions.SecurityKey = generatedKey; })`——`Configure`/`PostConfigure` 的執行順序不受 `IServiceCollection` 註冊順序影響（所有 `Configure` 動作先跑完，才跑所有 `PostConfigure`），所以即使 `AddWtmContext`（真正呼叫 `services.Configure<Configs>(config)` 的地方）在 demo `Startup.cs` 裡排在 `AddWtmAuthentication` **之後**，這個賦值依然生效。測試：`JwtAuthenticationStartupGateTests923.AddWtmAuthentication_DevelopmentEphemeralKey_IsWiredIntoBothConfigsObjectGraphs` 直接比對 `IOptionsMonitor<Configs>.CurrentValue.JwtOptions.SecurityKey` 與 `IOptionsMonitor<JwtBearerOptions>` 解析出的 `IssuerSigningKey` 位元組是否相同；刪掉 `PostConfigure` 這行，只有這支測試會紅。**design gate review 更正（2026-07-31）**：這個賦值原本是**無條件**覆寫，被抓出一個真實缺陷——如果 host 另外用 `services.Configure<Configs>(o => o.JwtOptions.SecurityKey = "...")` code delegate 設定了真正的金鑰（例如從 secret manager 讀出來，appsettings.json 完全沒有這個 key），因為所有 `Configure` 動作都在所有 `PostConfigure` 動作之前跑完，操作者的真金鑰會先被設進同一個 `Configs` 實例，再被這行無條件覆寫成臨時金鑰，且沒有任何診斷訊息說明為什麼。改成有條件（先重查 `IsWeakSigningKey()`）之後，`IOptionsMonitor<Configs>` 正確保留操作者的真金鑰——`AddWtmAuthentication_DevelopmentCodeDelegateStrongKey_IOptionsMonitorResolvesOperatorKey_NotGeneratedKey` 已針對舊的無條件版本實測過會紅（`Assert.AreEqual failed. Expected:<OperatorSuppliedSecretManagerKey_32Bytes!!>. Actual:<`隨機值`>`），改完才綠。**但這個修正沒有把整個場景修好**：`AddWtmAuthentication` 一開始建 `JwtBearerOptions.IssuerSigningKey` 用的是本地 `conf`/`jwtOptions`（來自 `config.Get<Configs>()`），這個物件從頭到尾看不到任何 code delegate；即使 `IOptionsMonitor<Configs>` 現在正確拿到操作者的真金鑰，`JwtBearerOptions.IssuerSigningKey` 仍然是臨時金鑰——變成「`TokenService` 簽章用真金鑰、`JwtBearer` handler 驗證用臨時金鑰」，這個組合下**每次登入都會失敗**。`AddWtmAuthentication_DevelopmentCodeDelegateStrongKey_JwtBearerHandlerStillUsesGeneratedKey_NotOperatorKey` 把這個殘留缺口實測釘住（斷言兩把金鑰**不相等**，而非誤植成相等）。這仍然是下面殘留風險 (2) 描述的 #753 家族 split-brain 讀取問題本身，只是原本以為只在非 Development 環境表現成拒絕啟動，實測後發現在 Development 環境會表現成更隱蔽的「啟動成功但驗證必敗」——已更新殘留風險 (2) 的描述以符合實測結果。

  **`WTMContext.User.cs` 的兩條獨立 `_remotetoken` 驗證路徑**（同步 `LoginUserInfo` getter 與非同步 `EnsureLoginUserInfoAsync`，各自獨立實作、非共用同一段程式碼——Core 不能假設任何 host 有呼叫 Mvc 層的 `AddWtmAuthentication`）：兩處都在建構 `TokenValidationParameters` 之前新增 `jwtOpts.IsWeakSigningKey(out reason)` fail-closed 檢查，弱金鑰時直接拒絕、不嘗試驗證簽章。理由：用弱金鑰簽出的 token，簽章驗證「通過」證明不了任何事——任何知道那把金鑰的人都能偽造出同樣通過驗證的簽章。測試（`RemoteTokenWeakKeyFailClosedTests923`，5 支）刻意用**能通過補齊後金鑰驗證的合法簽章**去測（不是隨便一個壞 token）：用短金鑰／`"super"` 簽出的 JWT，即使簽章本身技術上有效，兩條路徑仍必須拒絕；另有一支強金鑰 positive control 證明修復沒有連正常登入都擋掉。**副作用修正**：`test/WalkingTec.Mvvm.Core.Test/Integration/EnsureLoginUserInfoAsyncTests.cs` 原本兩支既有測試（`_ValidJwt_...`／`_TamperedJwt_...`）用的測試金鑰只有 24 raw bytes——在新的 fail-closed 檢查下會被直接擋在簽章驗證之前，讓「合法 JWT 應該通過」「被竄改的 JWT 應該被拒絕」這兩支測試對錯誤的原因給出正確的斷言（前者會假失敗、後者會假通過）；已把測試金鑰換成 32-byte 版本，兩支測試恢復驗證原本要驗證的行為。

  **跨廠 review（PR #931，Codex gpt-5.6-sol ultra，MERGE AFTER FIXING）發現這是設計本身有個 laundering 漏洞，不只是實作細節，另加七項 hardening。** (1) **raw/effective 分離**：原設計的 `SecurityKey` getter 回傳的是**補齊後**的值，所以 getter→setter round-trip、或任何只看得到 public getter 的 `System.Text.Json` 序列化/反序列化，都會把一個弱的原始金鑰靜默洗成「不弱」——HMAC 位元組沒變，`IsWeakSigningKey` 的判定卻翻盤了。修法：把屬性拆開——`SecurityKey` 現在原封不動 round-trip 原始值，永不補齊；新增 `EffectiveSecurityKey`，在讀取當下計算補齊後的 HMAC 材料，不寫回。四個 production 的 `SymmetricSecurityKey` sink（`TokenService.cs`、`FrameworkServiceExtension.cs`、`WTMContext.User.cs` 兩處）全部改讀 `EffectiveSecurityKey`；`IsWeakSigningKey`／`HasLowCharacterDiversity`／`IsFactoryDefaultKey` 則讀 `SecurityKey`。(2) **黑名單漏洞**：`test/` 不在 GitHub 公開 mirror 的排除清單裡（`.sync/github-excludes.txt`），所以測試 fixture 裡的固定字面量跟 demo config 一樣公開——review 另外點名 4 個 32+ bytes 的固定 `SecurityKey` 字面量（3 個是 #923 自己的 commit 加進去的），本 repo 依「發現安全問題就全庫掃同一個 pattern」的既有慣例再掃一輪，多找到 3 個。七個全部永久列進 `KnownPublicKeys`（即使現在已經沒有測試在用也不從黑名單移除——從樹上刪掉字面量，不等於從 git 歷史／mirror 上撤回），改用的測試則換成每次執行隨機產生一把金鑰（`JwtTestKeys.StrongCustomKey`、`TokenTestFixture.GeneratedSecurityKey`）。新增 `scripts/check-jwt-key-literal-blocklisted.py`（仿照 `scripts/check-gitea-token-not-sourced.py`，#924/#929 的寫法），CI 偵測到樹上出現新的、未列入黑名單的 32+ bytes 固定 `SecurityKey` 字面量就擋下——掛進 `mutation-gate.yml` 沒有 path filter 的 `changes` job。(3) **PostConfigure 吞掉太短的自訂金鑰**：design gate review 加的有條件 PostConfigure，只要 `IsWeakSigningKey()` 在那個時點仍為真就換成臨時金鑰——這包含 `WeakReasonTooShort`，但設計表格的 row (d) 要求太短的自訂金鑰**在每個環境都要拒絕，不能有 fallback**。修法：排除 `WeakReasonTooShort`，讓透過第二個 `Configure<Configs>` delegate 設定的太短金鑰改成在 `TokenService` 建構時失敗（見 (5)），而不是被靜默放行。(4) **`IsFactoryDefaultKey` 讀到補齊後的欄位**：19-byte 的自訂金鑰如 `"wtmwtmwtmwtmwtmwtmx"` 補齊後跟補齊後的 default 是同一個 32 字元字串，舊版比對（比對補齊後的 backing field）因此誤判成 factory default，讓 `IsJwtActive` 算出 false、整個弱金鑰驗證器被跳過。修法：改成單純比對未補齊的 `SecurityKey`——(1) 已經讓這個屬性不再補齊，不會再有補齊後的形式可以碰撞。(5) **`TokenService` 沒有在自己的 sink 上強制這條不變式**：唯一的守門只在 `AddWtmAuthentication`；不經過它、直接建構 `TokenService` 的 console/ETL host（`TokenTestFixture.cs` 本身就是這個形狀）完全繞過。修法：`TokenService` 建構子自己呼叫 `IsWeakSigningKey`，弱金鑰就 throw `InvalidOperationException`（訊息點名是哪個 config key）——在真正能簽章之前 fail closed，不是只靠一條可選的進入路徑。

  **Demo 專案**：三份 `appsettings.json`（LayUI／Vue3／Blazor）的 `JwtOptions.SecurityKey` 與 `CookieOptions.SecurityKey`（`CookieOption` 類別本來就沒有這個屬性——`grep -n "class CookieOption" -A20 src/WalkingTec.Mvvm.Core/ConfigOptions/CookieOptions.cs` 可查證，這幾行原本就是綁不到任何東西、只是把外洩字串再公布一次的死配置）全部刪除，不是換成更強的值——任何寫進這個檔案的值都會出現在某人的 production。`git grep -n "superSecretKey@345\|\"SecurityKey\": \"super\"" -- demo/ docs/` 現在是零結果。三個 demo app 的 `launchSettings.json` 主要 profile 皆為 `ASPNETCORE_ENVIRONMENT=Development`（Vue3Demo／BlazorDemo 另有名為非 Development 的次要 profile，未受影響——那兩個 profile 本來就會走 throw 分支，這也是刻意行為）；刪掉 `SecurityKey` 後主要 profile 改用臨時金鑰、開發體驗不變——`AuthApiTests`（透過 `DemoWebApplicationFactory`，真實 HTTP，真實 `/api/_Account/Login` 走 `TokenService.IssueTokenAsync` 簽章、`JwtBearer` handler 驗證）與整個 `WalkingTec.Mvvm.Api.Test`（100 個測試，1 個既有的 mutation-gate baseline selftest fixture跳過）全數重跑皆綠，證明拿掉金鑰後 JWT 登入流程與其餘 HTTP 整合測試都沒有壞掉，且確認 demo app 在 Development 環境下沒有設定 `SecurityKey` 仍能正常啟動並完成登入；`test/WalkingTec.Mvvm.Core.Test` 全專案重跑（含本節列出的所有新測試，以及 #931 review 加的測試）為 `4820 passed, 0 failed`（此為本輪重新執行的真實數字——先前版本此處與 `CHANGELOG.md` 分別寫 4806／4808，兩個數字都不是重跑得到的實際值，#931 review 點名須修正）。**#931 item 2 黑名單燒毀清單**：`JwtOption.KnownPublicKeys` 目前列的值——CLR default、三個 demo/manual 字面量、以及 `WTM_Test_Key_AtLeast_32_Characters!!`／`MyR@ndomStr0ngK3y_NotTheDefault!!`／`myTestSecretKey1234567890abcdefg`／`OperatorSuppliedSecretManagerKey_32Bytes!!`／`denylist_test_secret_key_32chars!!`／`atomic_rotation_secret_key_32chars!!`／`wtm_very_secret_key_1234567890123`——全部視為**永久燒毀**：即使樹上已經沒有測試在用它們，也不會、也不應該從黑名單移除，因為這些字串一旦進過 git 歷史就透過 `cct08311github/WTM` 公開 mirror 永久可查；任何人都不應該把這些值當成「還能用的範例」複製進自己的組態。

  (6) **測試證據本身的缺口**：黑名單斷言原本沒有跟長度守門獨立分開——刪掉一條黑名單項目、或弄壞 `Trim()`／大小寫不敏感，這些測試照樣綠燈。新增的隔離測試改用**本身就 >= 32 bytes 的黑名單值**（手冊那個 42-byte 佔位金鑰，另加上加了空白／轉大寫的變體），確保每一條斷言只有對應那一個 clause 才能讓它通過。臨時金鑰的不可預測性完全沒有測試綁定——把 `RandomNumberGenerator.GetBytes(32)` 換成 `new byte[32]` 一樣能通過所有既有測試；新增一支測試斷言兩次獨立產生的臨時金鑰彼此不同、也不等於全零常數，並有第五個 mutant 佐證（見下）。`EnsureLoginUserInfoAsyncTests.cs` 竄改簽章的測試原本是翻轉 Base64url 字串的**最後一個字元**，這個位置視原字元而定，可能剛好落在沒用到的 padding bit 上、解碼後位元組其實沒變；改成解碼後對簽章**第一個位元組**做 XOR、斷言解碼後的位元組確實不同，再重新編碼，確保這支測試每次都真的在測一個不同的簽章。(7) **Docker image 與非 Development launch profile**：`Dockerfile` 設 `ASPNETCORE_ENVIRONMENT=Production`，demo 的 `appsettings.json` 依本次修法不再帶 `SecurityKey`，所以 `docker build && docker run` 現在會直接拒絕啟動——這個行為本身是對的（image 自帶的預設值沒有理由能滿足這個檢查），但先前完全沒有文件。修法：在 `Dockerfile` 加上顯眼的註解＋具體注入範例（純環境變數、Docker secret 搭配 entrypoint wrapper、Kubernetes `Secret`），而不是把 image 自己的環境悄悄降成 Development——那樣會產生一次性臨時金鑰、每次容器重啟就讓所有 access token 作廢，只是把問題藏起來而非攤開。(8) **文件更正**（本次連動修正）：下方 test count 改成重新跑過的真實數字；上一段的「換金鑰即讓現有 token 失效」補上重啟前提；下面「完整性宣稱」的措辭收斂到 grep 指令實際證明的範圍；`256 bits — 為 HMAC-SHA256 的 IDX10720 下限` 這句話從來就不是、也不該被讀成「任何 32-byte 金鑰都有 256 bits 的實際亂度」——`JwtOptionTests` 自己就接受 32 個重複字元、或 11 個重複中文字元組成的金鑰判定為 `IsWeakSigningKey() == false`（`HasLowCharacterDiversity` 才是抓這種情況的、warn-only 訊號）。

  **五個 mutant**（`test/mutants/entries/jwt923-*.json`，皆 `VERDICT: KILLED` / `GATE: PASS`）：`jwt923-rawkey-padding-bypass-reintroduce`（讓 `IsWeakSigningKey` 改讀 `EffectiveSecurityKey`（永遠補齊的屬性）而非 `SecurityKey`（未補齊的屬性）——依 (1) 的 raw/effective 拆分重建；#923 時期的 patch 目標欄位已被那次拆分移除，套不上去了）、`jwt923-weak-key-length-guard-neutralize`（把 32-byte 長度門檻改成恆假）、`jwt923-blocklist-demo-keys-neutralize`（把黑名單比對短路成恆假，紅測試選手冊 42-byte 佔位金鑰——唯一只靠黑名單、不靠長度就會被擋下的案例）、`jwt923-devgen-environment-guard-neutralize`（把 `eligibleForDevelopmentCarveOut` 的環境判斷硬編成 `true`，讓 Production 誤判成 Development、weak key 不再 throw 而是靜默換成臨時金鑰——這是這幾個裡唯一直接命中起手式 throw/no-throw 決策的）、新增的 `jwt923-devgen-rng-zeroing-neutralize`（把 `RandomNumberGenerator.GetBytes(32)` 換成 `new byte[32]`——對應 (6) 提到的不可預測性缺口）。

  **完整性宣稱與其依據（本輪重跑）**：`grep -rn "SymmetricSecurityKey" --include="*.cs" .`（全樹執行，非僅 `src/`）確認 production sink 仍是四個（`TokenService.cs:249`、`FrameworkServiceExtension.cs:857`、`WTMContext.User.cs:85`／`:353`），全部已改讀 `EffectiveSecurityKey`；另外命中的幾處全部在 `test/`／`src/WalkingTec.Mvvm.Mvc.Tests/`，用的是測試自己的獨立字串常數或已知強金鑰，不經過 `JwtOption` 的弱金鑰判定路徑。**這個宣稱的範圍就是這個 grep 指令本身所能證明的範圍**：它確認了「建構 `SymmetricSecurityKey` 的位置只有這幾處」，不是「不存在任何其他方式讓弱金鑰材料流向簽章/驗證邏輯」的證明——(5) 新增的 `TokenService` 建構子守門把這個不變式往 sink 本身推近了一步（不再只靠 `AddWtmAuthentication` 這一個可選的進入點），但如果未來出現一條這次 grep 沒找到的建構路徑，這句宣稱就需要重新跑一次同樣的指令才能維持有效，不會自動繼續成立。**未涵蓋、刻意不修的殘留風險**（下一版 CHANGELOG 與此處一致，不多宣稱）：(1) 已經升級到含此修復版本、但金鑰仍是舊的公開值且從未輪替的既有部署——升級本身不會讓已外洩的金鑰失效，需要操作者主動輪替，且**輪替本身需要重啟每一個驗證 token 的 instance**：`JwtBearerOptions.IssuerSigningKey` 是 `AddWtmAuthentication` 呼叫當下從 `config.Get<Configs>()` 拍的一張快照，之後不會再讀，光改設定值（就算來源支援 `reloadOnChange`）碰不到它，正在跑的 instance 會繼續用舊金鑰驗證，直到那個 instance 重啟；(2) 用 code delegate（`services.Configure<Configs>(o => o.JwtOptions.SecurityKey = "...")`）設定金鑰的 app：`AddWtmAuthentication` 讀的是 `config.Get<Configs>()`，不是完整合併過 code delegate 的 `IOptionsMonitor<Configs>`，這個限制本身不因本次修法而改變——這是既有的 #753 家族 split-brain 讀取問題，本次不修，另立追蹤票。**實測後修正影響範圍描述**：非 Development 環境下這樣的 app 仍會被拒絕啟動（`AddWtmAuthentication` 判定金鑰弱，且不符合 Development 例外）；Development 環境下，PostConfigure 修成有條件之後 `IOptionsMonitor<Configs>`（`TokenService` 簽章用）會正確拿到操作者的真金鑰，但 `JwtBearerOptions.IssuerSigningKey`（驗證用）仍然看不到那個 code delegate、維持臨時金鑰——不是「開不起來」，而是「開得起來但每次登入都驗證失敗」，且沒有任何啟動期診斷指出原因，兩者都在 `JwtAuthenticationStartupGateTests923.cs` 的兩支對照測試裡實測釘住；(3) `HasLowCharacterDiversity` 這層 warn-only 訊號不是 entropy 量測，一個 40 字元的英文句子照樣會通過（`HasLowCharacterDiversity_strong_random_key_returns_false` 只證明它對真正隨機金鑰不會誤報，不證明它能抓到低 entropy 但字元多樣的金鑰）；(4) production 若被誤判成 Development（例如 `IWebHostEnvironment` 未正確註冊、環境變數設錯），會靜默拿到一次性隨機金鑰，後果是每次重啟 access token 全部作廢（可用性問題），不是安全破口——但操作者不會立刻發現組態判斷錯了。此點與 #931 review 另立追蹤的「環境偵測取第一個註冊的 `IWebHostEnvironment` 描述元而非依 DI 後註冊者優先的語意、且對只註冊 `IHostEnvironment` 的 generic host 沒有 fallback」是同一個偵測機制的兩種呈現，本次不修。

- **#948（P0，已修）：Dashboard REST widget 讓呼叫端透過小工具定義本身夾帶 `AllowPrivateNetwork`／`AllowHttp`，形成 SSRF——三條寫入路徑（不只 issue 原本點名的那一條）都中招。** `_DashboardController.Create`（`[HttpPost("")]`）／`.Update`（`[HttpPut("{id}")]`）都直接 `[FromBody]` 綁定整個 `DashboardDefinition`；`_DashboardDesignerController.Preview` 綁定 `WidgetDefinition` 後呼叫**同一個** `IDashboardService.CreateAsync`（建立一個 transient dashboard）。三者共用的 `JsonFileDashboardService`/`EfCoreDashboardService.ValidateWidgetConfigs`（寫入時的驗證關卡）只檢查 `Type`／`Kind`／`analysis` 的 `ListVmType`／`rest` 的 `Url` 是否非空，從未檢查 `RestWidgetDataSourceOptions.AllowPrivateNetwork`／`AllowHttp`。**假的跨元件斷言，本 repo 已出貨過三次的同一類缺陷再度出現**：`WidgetDefinition.RestOptions` 的 doc comment 原文宣稱「這兩個安全欄位只能在這裡設定——從 request 來源的 options 一律被強制清成安全預設值」，這句話對「`GetWidgetData` 抓資料時的 request `options` 參數」這個管道是真的（該管道確有 strip 邏輯），但對 `RestOptions` 這個屬性本身完全是假的：三條寫入路徑的請求 body 直接綁定到含 `Source.RestOptions` 的 `WidgetDefinition`，所以 `widgetSource.RestOptions` **就是呼叫端自己的資料**，不是「伺服器端」設定。`JsonFileDashboardService.GetWidgetDataAsync` 內把它序列化進 `parameters["options"]` 並標註「authoritative」的註解也承接了同一個錯誤前提。兩處錯誤斷言均已改寫，並逐一複查 `RestWidgetDataSource`／`EfCoreDashboardService` 同款跨元件斷言，沒有再找到第三處。**「三條寫入路徑」這句完整性宣稱，補上證明指令與這次（#957 修復同時）重跑的實際輸出**（先前只描述結論、沒有附證明指令，本身正是 CLAUDE.md Red Line 要求的那種缺口）：`git grep -n -E 'FromBody\][^)]*(DashboardDefinition|WidgetDefinition)' -- 'src/**/*.cs'` → 命中恰好三處，與宣稱相符：`_DashboardController.cs:94`（`Create`）、`:134`（`Update`）、`_DashboardDesignerController.cs:114`（`Preview`）。**這個宣稱的範圍就是這個 grep 指令本身能證明的範圍**：它確認「直接 `[FromBody]` 綁定 `DashboardDefinition`／`WidgetDefinition` 的 controller action 只有這三個」，不是「不存在任何其他方式讓呼叫端資料流進 `RestOptions`」的證明——例如一個間接綁定（先綁定成別的型別、程式內再轉型/複製欄位）不會被這個 pattern 命中，如果未來出現這種寫法，這句宣稱需要重新跑一次同樣的指令才能維持有效。
  **修法分兩層，理由各自獨立**：(1) **寫入時拒絕（不是靜默清除）**——兩份 `ValidateWidgetConfigs` 現在對 `rest` 種類的小工具，若 `RestOptions.AllowPrivateNetwork || RestOptions.AllowHttp` 為真就回傳驗證錯誤（`ArgumentException` → 三條路徑各自的 catch 都回 `400`）。**拒絕優於靜默清除的理由**：清除對正常呼叫端比較友善（合法情境本來就不該設這兩個欄位），但清除會抹去唯一能證明「有人嘗試升級權限」的訊號——被靜默改正的請求，在呼叫端自己看到的回應裡與正常請求毫無分別，也不會留下任何 operator 找得到的 log 行；`_DashboardController`／`_DashboardDesignerController` 既有的 `catch (ArgumentException ex) { _logger.LogWarning(ex, ...) }` 剛好提供了現成的、已經在用的 log 出口，選拒絕幾乎零額外成本。(2) **執行期不再信任這兩個布林值本身，一律問 host 註冊的 `IDashboardEgressPolicy`**——即使 (1) 的驗證有漏網之魚（未來新寫入路徑、既有資料庫裡的舊資料），`RestWidgetDataSource.ValidateUrlAsync`（fast pre-check）與 `PinnedConnectAsync`（TOCTOU-safe 的實際 connect-time 檢查，透過 `SocketsHttpHandler.ConnectCallback`）都改成：任何解析出的目的地只要落在私有網段或走明文 `http://`，就必須經過已註冊的 `IDashboardEgressPolicy.IsAllowedAsync(destination)` 對**這個具體、已解析的目的地**核准；沒有註冊 policy（預設）等於一律拒絕。`RestOptions.AllowPrivateNetwork`／`AllowHttp` 兩個屬性保留（相容既有已序列化的 widget JSON、也讓 policy 實作可讀取呼叫端原始意圖作為參考），但不再是任何形式的授權依據——文件已同步更正。**為什麼是這個介面形狀**：「把兩個布林搬到 `DashboardOptions` 全域設定」被 issue 本身否決——一個全域「允許私網」開關一旦打開，任何已認證的編輯者都能把任意小工具指向任意內網位址；真正需要決策的是目的地本身（host/IP/port/scheme），這件事只有 host 自己知道。介面刻意做成 async（`.claude/rules/dotnet-conventions.md` 明文禁止 request path 上 `.GetAwaiter().GetResult()`，且真實的 egress policy 通常要查設定檔或資料庫）、單一方法（`Task<bool> IsAllowedAsync(DashboardEgressDestination, CancellationToken)`），刻意不比照 `IWtmFrameworkEndpointAuthorizer`（#827，本身也還沒發過版）做成多 hook 的授權介面——兩者回答的問題不同（那個介面問「這個呼叫者能不能碰這個資源」，這個問「這個網路目的地能不能被連到」），本票也刻意不在同一批新增第二個大型授權介面。
  **`DashboardOptions.EnableEditing`：宣告但從未被讀取，順手一併修。** `grep -rn "EnableEditing" src/` 顯示修復前只有 `DashboardOptions.cs` 自己宣告這個屬性（預設 `true`），沒有任何 controller 讀取它——編輯授權事實上不存在，且這個看起來像防護的旗標比完全沒有旗標更糟（會被誤讀成「已經有控制」）。裁定：接成一個粗粒度全域開關而非移除——`_DashboardController.Create`／`.Update`／`.Delete` 與 `_DashboardDesignerController.Preview`（本身也會 transient 寫入）在方法一開頭檢查 `_options.EnableEditing`，為 `false` 時對任何呼叫者一律 `Forbid()`，不看擁有者或角色（`IDashboardService.CanEdit`／`AdminRoles` 的既有per-resource 授權完全不受影響、疊加在這個開關之上）。預設 `true`，行為與修復前完全相同——這是加法，非既有部署的行為變更。
  **`WidgetDataRequest`／REST 回應快取路徑刻意未觸碰**：`RestWidgetDataSource.BuildCacheKey` 目前只用 `Method::Url::Body::JsonPath`，不含 tenant/user/header——這是另一張票（#952，一個使用者的已驗證回應可能被快取後端給另一個使用者），與本票是「同檔案、不同缺陷」，且 #952 需要對 `WidgetDataRequest` 的形狀單獨做設計決策。本次改動只加了 `ValidateUrlAsync`／`PinnedConnectAsync`（在快取查詢**之前**執行）的 egress 檢查，完全沒有動到 `BuildCacheKey` 或快取寫入/讀取邏輯，也沒有讓 #952 更難修。
  **驗收（初輪）**：涵蓋：(a) 寫入時驗證——`JsonFileDashboardService`／`EfCoreDashboardService` 的 Create／Update 各自拒絕 `AllowPrivateNetwork=true`／`AllowHttp=true`；(b) 端對端 HTTP 層——真實 `_DashboardController`／`_DashboardDesignerController`（非 mock service）背後接真實 `JsonFileDashboardService`，證明 Create／Update／Preview 三條路徑各自對惡意小工具定義回 `400`，且 Update 失敗後原本安全的小工具維持不變；(c) 正面對照組——一個合法的公開 HTTPS 目的地、不帶任何安全欄位，Create／Update／Preview 三者皆須成功（防止「擋掉一切」的修法通過前三項假陽性斷言）；(d) `RestWidgetDataSource` 層——`ValidateUrlAsync` 在沒有註冊 policy 時一律拒絕私網／明文 HTTP（即使 `AllowPrivateNetwork`／`AllowHttp` 為 `true`），註冊一個核准的 stub policy 後才放行，且用呼叫計數斷言 policy 真的被諮詢過而非僅僅存在；(e) `EnableEditing=false` 時 Create／Update／Delete／Preview 四端點皆回 `403`、且服務層方法從未被呼叫，`EnableEditing=true`（預設）行為 pin 測試維持通過。**因二層防護（寫入時拒絕＋執行期 policy 閘門）同時存在，(b)/(c) 組的 red-then-green 只能單獨鎖定寫入時那一層**——中和寫入層 guard 後，Create／Update／Preview 六支相關測試全數轉紅（`no exception was thrown` / `Expected result not to be <null>`），正面對照組維持綠燈，還原 guard 後全部恢復綠燈——手動驗證後，同一組斷言＋patch 已註冊為 CI 強制的 compile-preserving mutant `948-dashboard-restoptions-egress-write-guard-neutralize`，`VERDICT: KILLED` / `GATE: PASS`（詳見 CHANGELOG）。**(d) 這條當時只驗到 `ValidateUrlAsync`（fast pre-check），沒有驗到 `PinnedConnectAsync`（connect-time 那一層）——這句宣稱本身在下面 F3 被指出過度，已更正**。

  **同日跨模型（非跨廠）審查（PR #955，Fable 5／Anthropic，與實作者 Sonnet 同廠不同模型），F1–F5 阻擋合併、F6–F7 同批修復、F8 裁定暫緩：**

  - **F1（HIGH，會讓 app 起不來）：`AddWtmDashboardEgressPolicy<T>()` 註冊成 `Scoped`，但唯一消費鏈是 `Singleton`。** `IDashboardService`（Singleton）建構子吃 `IEnumerable<IWidgetDataSource>`；這個 enumerable（因此連帶 `RestWidgetDataSource`，即使它自己註冊成 `Transient`）在該 singleton 第一次被建構時、從 root container 解析且只解析一次。`Scoped` 的 `IDashboardEgressPolicy`在這裡是 captive-dependency 錯誤：ASP.NET Core 預設 DI 驗證（`ValidateScopes`/`ValidateOnBuild`，`Host.CreateDefaultBuilder` 在 Development 環境下預設開啟）下 app 直接啟動失敗（已用同款 lifetime 形狀寫 repro 實測重現）；驗證關閉時（通常是 Production）則靜默變成驗證器原本該攔的那種 captive dependency——一個實例被整個 process 生命週期共用，包括背景的 `DashboardAlertHostedService`/`DashboardSnapshotJob` singleton。初輪 23 支測試沒有一支抓到：全部直接 `new RestWidgetDataSource(factory, cache, policy)`，完全繞過唯一支援的 production wiring。**修法**：`AddWtmDashboardEgressPolicy<T>()` 改註冊 `Singleton`；`IDashboardEgressPolicy` 的 XML doc 明寫實作必須 thread-safe、不得直接依賴 scoped 服務（需要的話用 `IDbContextFactory<T>`/`IServiceScopeFactory` 自行管理）。測試：`AddWtmDashboardEgressPolicy_BuildsCleanly_WithScopeAndBuildValidationEnabled`／`_RegistersSingleton_SameInstanceAcrossScopes`（`DashboardServiceCollectionExtensionsExtraTests.cs`）用 `ValidateScopes`/`ValidateOnBuild = true` 建 container 並從真實 scope 解析 `IDashboardService`；還原成 `AddScoped` 時實測重現審查者描述的確切 `AggregateException`，手動確認 red-then-green。

  - **F2（HIGH，相容性）：升級前已持久化、帶 `AllowPrivateNetwork`/`AllowHttp` 的 `rest` widget，升級後每次 refresh 靜默變成 `502`，且無 migration path。** `RestWidgetDataSourceOptions.AllowPrivateNetwork` 修復前的 doc 明文寫「for intentional internal-network widgets」——這是有文件、受支援的 opt-in，不是疏漏。本文件先前在此寫「不需要資料遷移」——字面為真（資料本身不用改），但被誤讀成「不需要 migration path」並不成立：operator 必須採取行動才能恢復一個原本正常運作的 widget。**裁定（依 CLAUDE.md 「Compatibility → Security」排序，「比較安全」本身不足以當唯一理由）**：不選擇「對舊資料沿用舊行為直到 host 主動 opt-in」——因為這些欄位本來就是攻擊者可經由 Create/Update/Preview 寫入的呼叫端資料，對「已持久化」的資料沿用舊信任等於對已經可能被利用過的資料重新開洞，是本票要關的洞本身；改為（a）在 `CHANGELOG.md` 新增 `### Changed`／`### Migration` 條目、標記 BREAKING，並提供兩條復原路徑：(b) **新增內建、設定驅動的 `ConfiguredAllowlistDashboardEgressPolicy`**（`src/WalkingTec.Mvvm.Core/Dashboard/ConfiguredAllowlistDashboardEgressPolicy.cs`）——不寫 C# 也能用組態把特定內網目的地加回白名單，比對邏輯刻意簡單（精確 IP／hostname 比對，不支援 CIDR/wildcard，理由是這是「不想寫授權程式碼」的部署專用的選項，可稽核性比表達力重要）；`Entries` 為空時行為與完全沒有 policy 相同（一律拒絕），本身不改變預設安全姿態。(c) 自訂 `IDashboardEgressPolicy` 的 sample code。**兩者都不會改動已持久化的 `AllowPrivateNetwork`/`AllowHttp` 值**——它們繼續留在 JSON 裡作為原始意圖的紀錄，但無論如何都不再被 `RestWidgetDataSource` 當成授權依據；只有已註冊、核准該具體已解析目的地的 `IDashboardEgressPolicy` 能恢復抓取。**測試**：`ConfiguredAllowlistDashboardEgressPolicyTests.cs`（8 支：空清單拒絕、IP+port 相符放行、port 不符拒絕、`Ports=null` 放行任意 port、hostname 比對（含大小寫不敏感）、不相符拒絕、多筆 entry 命中第二筆）＋一支 DI 驗證測試（`AddWtmDashboardEgressPolicy_WithBuiltInAllowlistPolicy_BuildsCleanly_WithScopeAndBuildValidationEnabled`，同 F1 的 `ValidateScopes`/`ValidateOnBuild` 形狀）。

  - **F3（MEDIUM）：connect-time、TOCTOU-safe 的那一層（`PinnedConnectAsync`/`SelectConnectableIpAsync`）零測試、零 mutant，而本文件（上方「驗收」段）宣稱它有靠呼叫計數驗證。** 全部既有 `GetDataAsync_*` 測試用的假 `IHttpClientFactory`/`HttpMessageHandler` 完全不會走到 `SocketsHttpHandler` 的 `ConnectCallback`——已實測：整段刪掉 `SelectConnectableIpAsync` 的 policy 諮詢分支，全套件仍維持綠燈。**修法**：新增 6 支直接測試 `SelectConnectableIpAsync`（全私有＋無 policy→null 零次呼叫；全私有＋核准 policy→回傳第一個核准 IP 並斷言 destination 各欄位；全私有＋拒絕 policy→null、每個候選都問過；私有+公開混合＋無 policy→經 fast path 直接回傳公開 IP、零次 policy 呼叫——這正是下面 F7 指出 `ValidateUrlAsync` 曾經算錯的多候選情境；明文 HTTP+公開 IP＋無 policy→null；明文 HTTP+公開 IP＋核准 policy→回傳該 IP）；新增第二個 mutant `948-restwidget-selectconnectableipasync-policy-consultation-neutralize`，中和 policy 諮詢迴圈，`VERDICT: KILLED` / `GATE: PASS`。同時把 `ValidateUrlAsync`（pre-check）改為直接呼叫 `SelectConnectableIpAsync`，不再維護第二份、語意會跟著漂移的迴圈——原因見下方 F7 第 6 點。本文件上方「驗收」段的宣稱已更正為只涵蓋 `ValidateUrlAsync`。

  - **F5（MEDIUM）：`AllowedPorts` 跟 `AllowPrivateNetwork`/`AllowHttp` 活在同一個呼叫端可控的 `RestOptions` 物件上，卻完全沒被驗證——呼叫端只要送 `"allowedPorts": null` 就能整個關掉 Redis/ES/DB 連接埠探測防護，而剛改寫的 guard 註解宣稱「regardless of the egress policy decision below」（這句本身沒錯，但問題根本不是繞過 egress policy，是繞過驗證本身）。** **修法**：兩份 `ValidateWidgetConfigs` 現在對 `rest` 小工具的 `RestOptions.AllowedPorts` 為 `null` 或空陣列時拒絕（`ArgumentException`），沿用 `AllowPrivateNetwork`/`AllowHttp` 同款拒絕而非靜默重設的先例；已修正該假註解。**同一物件上的 `Headers`/`Method`/`Body` 仍未驗證、本次刻意不修**——已列入下方「未涵蓋、刻意不修」清單，需要另外立案追蹤。**測試**：兩份 dashboard service 各 3 支（`null`／空陣列被拒絕、預設值仍被接受），加上既有正面對照組；手動確認 red-then-green。**2026-07-31 更正：此點已被 #956 部分關閉**——見下方 #956 條目。範圍是部分而非完整：`Headers` 現在對一組固定的 hop-by-hop/framing 欄位名稱（`Host`/`Transfer-Encoding`/`Content-Length`/`Connection`/`Upgrade`/`TE`/`Trailer`/`Expect`/`Proxy-*`）、header 數量上限（20）、以及 name+value 總長度上限（8 KB）做寫入時與送出時雙層驗證；`Method`（仍只認 GET/POST，見 S1）與 `Body`（未加大小上限之外的格式驗證）本身未在 #956 範圍內變動。

  - **F6（MEDIUM-LOW，對「已註冊 policy」的部署是淨退步）：legacy request-supplied `options` 管道——`rest` widget 沒有持久化 `RestOptions` 時，只需要 `CanAccess`（viewer 等級，非 `CanEdit`）就能對 `GetWidgetData` 帶自己的 `options` blob——修復前是硬上限（兩個布林被 strip，且 `#948` 修復前 `ValidateUrlAsync` 把 strip 後的值當權威，這條路只打得到 public HTTPS）。** `#948` 之後那兩個布林已完全不被讀取，strip 因此變成無效動作：一旦任何 host 註冊了核准某個私網/明文 HTTP 目的地（給自己合法 widget 用）的 `IDashboardEgressPolicy`，這條 viewer 可達的管道就能用**呼叫端自己的** `Url`/`Method`/`Headers`/`Body` 打到**同一個**目的地——policy 沒有任何方式分辨這個請求來自持久化、host 核准過的 widget，還是來自呼叫端臨時塞的 `options` blob（`DashboardEgressDestination` 不帶 widget 識別，見下方 F7 第 5 點與 F8）。**修法**：整條管道直接拒絕（`InvalidOperationException`），不再部分 strip——相對 `#948` 前是窄化（原本可打 public HTTPS；現在完全不接受沒有持久化 `RestOptions` 的 widget 之 request-supplied options），不是放寬。已讀過 `framework_dashboard.js` 確認正式出貨的 UI 從未走這條路（widget-data fetch 只會把 `FilterBar` 值當 query string 附加，從不送 `options` JSON blob）。**測試**：兩份 dashboard service 各一支，斷言底層 `IWidgetDataSource.GetDataAsync` 從未被呼叫到；手動確認 red-then-green。

  - **F7（LOW，與本條目標題同一類缺陷）：本 PR 自己改寫或新增的註解裡，又找到六處假或過時的跨元件斷言，外加一個不只是文字問題的真實語意錯誤。** (1) 連接埠允許清單的錯誤訊息仍寫「Configure AllowedPorts in the **server-side** RestOptions」——正是本條目宣告為假的那個框架，已改寫。(2)(3) `RestWidgetDataSourceOptions` 的 `Url`／`AllowedPorts` doc 仍描述 `#948` 前的授權語意（`AllowHttp=true` 單獨就「允許 http://」；`AllowedPorts` 為 null「不建議，除非 AllowPrivateNetwork 也是 true」，把這個組合寫成還算合法的逃生口）——已更正。(4) `DashboardOptions.EnableEditing` 的 remark 宣稱修復前的屬性「had this exact doc comment」——不可能為真，因為 summary 描述的正是本次修法才新增的 403 行為；修復前該屬性完全沒有 doc comment。已更正。(5) `RestWidgetDataSourceOptions.AllowPrivateNetwork` 的 remark 宣稱 policy「may choose to read [intent signal] from the destination's originating widget」——`DashboardEgressDestination` 不帶任何 widget／dashboard／tenant 識別，這個能力根本不存在；已移除此宣稱（另見 F8）。(6) **唯一不只是文字問題、是真實 bug 的一項**：`ValidateUrlAsync` 的 pre-check 原本是「只要任一解析出的 IP 未獲核准就整批拒絕」，但 connect-time 層只需要**其中一個**候選被核准即可——一個合法的多 A 記錄 host，若 policy 只核准其中一個特定 IP，會在 pre-check 就被錯誤擋下，即使照樣能在下游連線成功；被刪掉的舊註解宣稱兩層是「identical failure mode」。**修法是靠架構對齊，不是改字**：`ValidateUrlAsync` 現在直接呼叫 `SelectConnectableIpAsync`（見 F3），不再維護第二份手動同步的迴圈，兩層因此共用同一份「是否有任一候選合格」的實作。本文件上方「驗收」段先前宣稱「逐一複查……沒有再找到第三處」——這句本身是假的、且沒有附上實際跑過的指令；已更正為指名這次真的執行過的 `grep` 與其結果：`git grep -n "server-side" -- src/` 命中上面第 (1) 點；上面六項本身就是「沒有找到的第三處」（以及更多）。

  - **F8（設計，本輪裁定暫緩、不實作）：`DashboardEgressDestination` 不帶 tenant／widget／dashboard 識別，這正是 F6 沒辦法在 policy 層徹底解決、F7 第 5 點宣稱為假的根因。** 審查者的論點——趁介面還沒發版，現在加 non-`required` 的 `TenantId`/`WidgetId`/`DashboardId` 屬性是 source-compatible 的；發版後才加 `required` 成員會是 binary/source 雙重 breaking——讀過、不否定其論證本身。**本輪不實作**：本輪指示明確要求 F8 是設計題、回報裁定而非單方面重新設計；且 `IDashboardEgressPolicy` 沒有這個欄位today 也是真正可用、安全的（F6 是靠直接拒絕曖昧管道解決，不是靠讓 policy 拿到更多上下文去消歧義）。是否要在這個介面真正發版前補上，以及答案會不會因為 #952（同檔案的快取鍵缺陷，也需要類似上下文餵進 `WidgetDataRequest`）需要同一批上下文而改變，留給維護者裁定。**2026-07-31 更正：維護者裁定補上，已與 #952/#956 一起實作**——見下方 #948-F8 條目。

  **驗收（累計）**：全套件重跑：`test/WalkingTec.Mvvm.Core.Test` `4872 passed, 0 failed`；`src/WalkingTec.Mvvm.Mvc.Tests` `64 passed, 0 failed`；`test/WalkingTec.Mvvm.Api.Test` `100 passed, 0 failed`（1 個既有的 mutation-gate baseline selftest fixture 依慣例 skip）。兩個 mutant（`948-dashboard-restoptions-egress-write-guard-neutralize`、`948-restwidget-selectconnectableipasync-policy-consultation-neutralize`）皆 `VERDICT: KILLED` / `GATE: PASS`。
  **未涵蓋、刻意不修**：(1) 執行期 `IDashboardEgressPolicy` 沒有內建「萬用」實作（`ConfiguredAllowlistDashboardEgressPolicy` 是精確比對，不含 CIDR/wildcard）——這是刻意的：WTM 不知道下游部署實際上該允許哪些內網目的地，出一個過度寬鬆的預設值會是本 PR 沒有做的相容性/安全決策；未註冊任何 policy 時的行為是「一律拒絕」，不是「一律允許」，這個留白是安全方向的留白。(2) 既有、已經被寫入資料庫或 JSON 檔案的、帶 `AllowPrivateNetwork=true` 的舊資料，本次修法後在讀取/顯示時不受影響（讀取路徑不驗證），但 `RestWidgetDataSource` 執行期已不再信任該欄位，實際抓資料時一樣會被擋——不需要資料遷移，但需要 operator 動作（見上方 F2）。(3) 同一個 `RestOptions` 物件上的 `Headers`／`Method`／`Body` 仍是完全未驗證的呼叫端資料（見上方 F5）——`Headers` 尤其可能夾帶憑證，且與 #952（快取鍵不含 header）是相鄰但獨立的風險；本次未修，需要獨立追蹤。**2026-07-31：(3) 已被 #952（快取鍵）與 #956（Headers 部分驗證）關閉，範圍見各自條目——`Method`/`Body` 本身仍未加格式驗證，殘留。** (4) `DashboardEgressDestination` 不帶 widget／tenant 識別（見上方 F8）——本輪裁定暫緩，非遺漏。**2026-07-31：(4) 已被 #948-F8 關閉**，見下方條目。

- **#957（P1，已修，承接上方 F5「`Headers` 尤其可能夾帶憑證」的殘留揭露）：`_DashboardController.Get` 把整個 `DashboardDefinition`（含每個 widget `Source.RestOptions.Headers` 的原始憑證值——`Authorization`／`Cookie`／API key，見該屬性自己的 doc comment）回傳給任何通過 `CanAccess` 的檢視者；`CanAccess` 是「檢視授權」，不是「憑證檢視授權」——一個部門級 dashboard 讓全部門都能看，不代表全部門都該拿到它背後的 API key。** 出貨的檢視器 UI（`framework_dashboard.js`：`DashboardManager._loadDashboard` → `fetch('/_dashboard/' + id)`）會把這個回應整個載進瀏覽器，不是理論風險。同一份修法也解決了一個獨立但相關的既活躍資料遺失缺陷：`Update` 對整個 widget 集合做 wholesale replace，先前完全沒有 headers 的 merge 邏輯——任何存回的定義如果沒帶到某個 widget 既有的 `Headers`，就會被靜默清空；出貨中的 designer（`framework_dashboard_designer.js`）恰好每次都符合這個條件——它整個沒有 headers 編輯 UI，`_readForm()` 的 rest 分支只組出 `{ kind: 'rest', restOptions: { url: _val('dsd-rest-url') } }`，從不送出 `headers` 鍵，所以透過出貨中的 designer 儲存任何 rest widget，都會今天就把它的 headers 洗掉。

  **讀取路徑窮舉**（`git grep -n -E 'Ok\(.*[Dd]ashboard' -- '*.cs'` 與 `git grep -n -E 'DashboardDefinition|WidgetDefinition' -- 'src/**/*Controller*.cs'`，全樹執行）：能把 `DashboardDefinition`／`WidgetDefinition`／任何含 `RestOptions` 的物件送進 HTTP 回應的端點只有 `_DashboardController.Get`（`return Ok(dashboard)`）一個。逐一驗證過其餘端點，確認不需要遮罩，不是假設：`List`（`[HttpGet("list")]`）回傳 `DashboardSummary`——讀過 `DashboardDefinition.cs` 的模型定義，該型別只有 `Id`／`Title`／`Owner`／`TenantId`／`Sharing`／`UpdatedAt` 六個欄位，完全不含 `Widgets`；`GetWidgetData`／`PostWidgetData`（widget 資料抓取端點）與 `_DashboardDesignerController.Preview` 三者最終都回傳 `WidgetDataResult`（`Value`／`PreviousValue`／`Rows`／`Columns`／`Metadata`／`Error`），讀過 `RestWidgetDataSource.MapToWidgetResult` 確認它只把上游 JSON 回應內容映射進這些欄位，從不回寫 `RestOptions` 本身；連錯誤路徑都刻意不洩漏——`FetchJsonAsync`對非 2xx 上游回應丟出的是不含目標 URL／狀態碼的通用訊息（issue #101 的既有慣例，本次沿用未動）。`_EtlDashboardController`（`src/WalkingTec.Mvvm.Etl`）用完全不同的 `EtlDashboardSummary` 模型，與本票的 `DashboardDefinition`／`WidgetDefinition` 無關，確認過不在範圍內。

  **修法，兩部分共用同一個新的 `WalkingTec.Mvvm.Core.Dashboard.DashboardCredentialMasking` 靜態類別**：(1) **讀取遮罩**——`MaskForRead` 把每個 widget 的 `Source.RestOptions.Headers` VALUES 換成常數 sentinel（KEYS 不動，讓未來的編輯器 UI 還能列出設定過哪些 header 名稱）。**刻意非破壞性（non-destructive）**：`JsonFileDashboardService.GetAsync` 的 `_defCache` 在快取命中時會回傳同一個共享實例（`return cached.Def`）——若原地修改該實例，遮罩會永久污染這個快取，讓後續任何 `GetAsync`（包括 `GetWidgetDataAsync` 內部真正呼叫上游 REST API 要用到的呼叫）都看到被遮罩過的假 header，直接打壞每一個 rest widget 的實際抓取。`MaskForRead` 因此只在真的有 header 需要遮罩時才建立新物件（widget/`WidgetSourceDefinition`/`RestWidgetDataSourceOptions`/`Headers` 字典各自淺複製一份），完全沒有 header 需要遮罩的 dashboard 直接回傳原始參考（零額外配置、也讓既有靠參考相等斷言的測試不受影響）。(2) **寫入時 merge（`ReconcileWidgetHeaders`）**——`Create`／`Update` 呼叫前先比對呼叫端送來的 `Headers` 跟目前已持久化的 `Headers`（`Create` 沒有既存值，等同永遠比對到「不存在」）：Headers 鍵缺席或明確 `null` → 保留既有全部 headers；Headers 存在但為空 `{}` → 清空全部 headers；某個 key 的值不是遮罩 sentinel → 直接存那個新值；某個 key 的值等於遮罩 sentinel 且該 key 已有既存值 → 還原既存的真實值（呼叫端把 GET 讀回的遮罩字串原封不動送回，代表沒有要改這個欄位，不是要把字面上的圓點字串存成憑證）；某個 key 的值等於遮罩 sentinel 但該 key 沒有既存值可還原 → **拒絕**（`400`，不寫入任何東西）——這裡選拒絕而非靜默丟棄該 header，理由與 #948 對 `AllowPrivateNetwork`／`AllowHttp`／`AllowedPorts` 的既有先例相同：靜默丟棄看起來對正常呼叫端比較友善，但會抹去唯一能證明「有東西把遮罩字面值當真實憑證送進來」的訊號（打錯 header 名稱、或客戶端程式碼誤用），而拒絕幾乎零額外成本（三條寫入路徑既有的 `catch (ArgumentException) => LogWarning` 已經是現成的 log 出口）。`Preview` 也接了同一條 reconcile 防線（它跟 `Create`／`Update` 一樣直接 `[FromBody]` 綁定呼叫端資料），但因為它永遠建立全新的 transient widget，「既有值」永遠不存在，任何 sentinel 值在這裡都必定落入拒絕分支。

  **`Headers` 屬性本身也改了型別，理由是這個區分本身在舊模型下做不到**：實測（`RestWidgetDataSourceOptionsHeadersBindingTests`）System.Text.Json 對 `Dictionary<string,string> Headers { get; set; } = new();`（修復前）反序列化的行為是——JSON 完全沒有 `headers` 鍵時，該屬性從不被觸碰，停在建構式的預設值 `new()`（空字典，非 `null`）；JSON 帶 `"headers": {}` 時，同樣得到空字典。兩者在綁定後**完全無法區分**，讓「沒送 headers 鍵（該保留）」跟「明確送空物件（該清空）」这兩個 case 在型別層面就先天混在一起，這正是上方案例表 (a)/(b) 需要分開處理的前提。修法：改成 `Dictionary<string,string>? Headers { get; set; }`（可為 null、無預設值）——JSON 缺席鍵維持 `null`，明確 `null` 也是 `null`，`{}` 才是空字典，三種情形在型別層面就能分開偵測，四支 binding 測試（缺席鍵、明確 `null`、空物件、有值物件）逐一實測釘住這個行為。因為出貨中的 designer 本來就完全沒送 `headers` 鍵，這個型別修正加上 `ReconcileWidgetHeaders` 的 case (a) 邏輯，兩者合起來就是資料遺失 bug 的完整修法——**完全不需要改任何 JavaScript**。

  **這個型別改動同時是下游可見的 breaking change，不只是實作細節**：`new RestWidgetDataSourceOptions().Headers` 修復前一律非 null（拿到空字典），修復後一律是 `null`。下游若直接 `.Headers.Add(...)` 或（啟用 nullable reference types 時）讀 `.Headers.Count`，前者會從編譯通過、執行正常變成執行期 `NullReferenceException`，後者會從編譯乾淨變成 `CS8602` 警告。本 repo 自己的呼叫端已經是 null-safe（`RestWidgetDataSource.cs` 已用 `if (options.Headers != null)` 保護；`DashboardCredentialMasking` 全程處理 null），所以這不是本票需要改的程式碼缺陷，但對下游套件使用者是真實的相容性斷裂，已補上 `CHANGELOG.md`「### Migration」條目（具體失敗形狀、一行修法、以及為什麼這個改動是必要而非可選——同上一段）。

  **協調者回饋追問：既有（修復前寫入的）持久化 JSON 在這次修復後的 round trip 有沒有問題？** 已用真正落地在磁碟、模擬修復前 code 產出的 JSON（而非測試自己在記憶體建構的物件）驗證過，不是假設：`Update_with_headers_absent_preserves_headers_from_a_dashboard_file_written_before_this_fix_existed`（`DashboardCredentialMaskingEndToEndTests.cs`）直接把一份手寫的 dashboard JSON 檔案寫進 `_default/` 目錄（`Headers` 帶著真實值，這是修復前後**唯一**序列化形狀相同的情況——nullable 與否只影響「缺席／空物件」的綁定結果，對已經有值的字典沒有差別），繞過這次測試自己的 `Create()`，模擬「升級前就存在的資料」；透過 designer 形狀（無 `headers` 鍵）的 `Update` 之後，用一個全新、從未碰過這個檔案的 `JsonFileDashboardService` 實例重新從磁碟讀回，確認 headers 正確保留——乾淨，沒有發現缺陷。另外兩支測試直接驗證協調者點名的第二個疑慮——「`Headers = null` 序列化後究竟是 `"headers":null` 還是被省略，重讀回來是不是又變成 preserve 語意」：`Serializing_a_null_Headers_value_produces_an_explicit_JSON_null_not_omission` 確認序列化輸出含明確的 `"Headers":null`（不是省略欄位——`JsonFileDashboardService` 用的是預設 `JsonSerializerOptions`，`DefaultIgnoreCondition` 預設 `Never`，不會跳過 null 屬性）；`A_serialized_null_Headers_round_trips_back_to_null_not_an_empty_dictionary` 確認重新反序列化後仍是 `null`，不是 `{}`——也是乾淨的，沒有發現「preserve 一次存檔後悄悄變成 clear」這個協調者擔心的缺陷。

  **測試與驗收，誠實揭露哪些測試真的證明了什麼**：29 支新測試分兩個檔案——`DashboardCredentialMaskingTests.cs`（純單元，直接呼叫 `DashboardCredentialMasking` 的兩個方法，不經 controller）與 `DashboardCredentialMaskingEndToEndTests.cs`（比照 `DashboardSsrfWriteValidationEndToEndTests.cs` 的模式，真實 `_DashboardController` 接真實 `JsonFileDashboardService`，非 mock）。對 pre-fix tree 做過真正的 red 驗證：`git stash` 掉四個修改過的 production 檔案，並把新的 `DashboardCredentialMasking.cs` 暫時換成一個保留同樣公開介面、但方法本體完全 no-op 的替身（`MaskForRead` 直接回傳原物件、`ReconcileWidgetHeaders` 永遠回傳 `null`），確保這 29 支測試在「這個修復完全不存在」的情境下真的能編譯並執行。結果：**15 支真的轉紅**（`Get` 直接回傳未遮罩的原始 header 值、`Update` 的 case (a)/(d)/(e) 三種情境、以及依賴這些行為的 binding/end-to-end 測試），失敗訊息逐一比對過確實是「看到真實憑證字串而非遮罩字元」或「預期被拒絕卻沒被拒絕」這類直接對應本缺陷的訊息，非無關的例外或 fixture 錯誤。**另外 14 支在 no-op 替身下仍然維持綠燈**——誠實揭露而非隱藏：其中 3 支是「不需要遮罩就沒東西可髒」型的非破壞性測試（沒有遮罩發生，自然也不會被污染，這條性質要靠下面的 mutant 而非單純還原修復前程式碼才能證明有牙齒）；4 支是 STJ 對 `{}`／明確 `null` 的 binding 行為本來就與模型是否可為 null 無關；其餘幾支的 case (b)/(c) 情境剛好跟修復前的「整包直接覆蓋」天真行為算出同一個結果（呼叫端明確送空字典或明確送真實值時，不管有沒有 merge 邏輯，結果本來就一樣）——這些測試仍然是正確行為的有效 pin，只是不構成「這個特定缺陷已修復」的證據，其中「讀取遮罩非破壞性」那一支改用 mutation testing（見下）補上真正的牙齒。

  **全套件重跑（初輪）**：`test/WalkingTec.Mvvm.Core.Test` `4907 passed, 0 failed`（對照本文件重評基準 `4874 passed`，差額 33 = 29 支上述測試 + 1 支下方的 clone-drift guard + 3 支下方的 pre-fix JSON round-trip 驗證測試）；`src/WalkingTec.Mvvm.Mvc.Tests` `64 passed, 0 failed`；`test/WalkingTec.Mvvm.Api.Test` `102 passed, 0 failed`（1 個既有 mutation-gate baseline selftest fixture 依慣例 skip）。

  **`MaskForRead`／`MaskWidgetHeaders` 的複製邏輯是手寫的（`DashboardCredentialMasking.cs` 裡逐欄位的 object initializer），初輪驗收時完整——獨立覆查過 `DashboardDefinition`／`WidgetDefinition`／`WidgetSourceDefinition`／`RestWidgetDataSourceOptions` 的公開可讀寫屬性數（13／6／7／11），全部都有對應賦值——但沒有任何機制保證它「維持」完整：往這四個型別任何一個加一個屬性、忘記同步加進複製邏輯，`Get` 會靜默把那個欄位回傳成 CLR 預設值（遮罩變成資料清空），且沒有任何既有測試會因此轉紅。`DashboardCredentialMaskingCloneDriftTests.cs`（新增 1 支）就是為了堵住這個機制性缺口而寫：用反射走訪整個物件圖，把每一層每一個公開可寫屬性都設成一個明顯非預設值（遞迴進巢狀 POCO／collection／dictionary，不留任何欄位在預設值），呼叫 `MaskForRead` 之後再用反射逐屬性比對兩份圖，除了 `RestWidgetDataSourceOptions.Headers` 的值（該被遮罩成 sentinel）之外全部斷言相等。因為 populate／compare 兩邊都是靠反射走型別本身，不是靠一份手寫的屬性清單，未來加在這四個型別上的新屬性會自動被涵蓋進去——**這是一個「偵測」機制，不是「保證」**：初輪已實測驗證它真的有牙齒，不是裝飾——暫時在 `RestWidgetDataSourceOptions` 加一個不存在的屬性（`ScratchDriftProbe957`）、刻意不加進複製邏輯，測試立刻轉紅並精確指出 `RestOptions.ScratchDriftProbe957: expected 'drift-84' (String), actual '' (String)` 這樣的路徑；移除該屬性後測試恢復綠燈。（此機制自己的一個盲點——F8——見下方獨立審查段落。）

  **獨立對抗性審查（PR #960）——審查者自己重新推導了 sink 清單，沒有照抄初輪的 grep，找到 F1–F8。** F1 以設計變更修復（不是「整包拒絕」的補丁）；F2 另立案，本票不動；F4／F6／F8 已修；F5 補測試＋mutant 關閉；F7 揭露但不修（範圍決定留給維護者）。

  - **F1（最重要、審查抓到的真缺陷）：naive case (a)「Headers 缺席就一律保留」把 designer 原本意外的 fail-safe 變成憑證轉發。** 初輪修復前，designer 的靜默清空是意外的安全網——編輯者透過出貨中的 UI 沒辦法把真實憑證指向攻擊者選的 host，因為 header 本來就會消失。「缺席即保留」上線後反而相反：編輯者只是透過 designer 改了 widget 的 URL 欄位（designer 的 REST widget UI 唯一暴露的欄位，且完全沒有 headers 編輯 UI 讓編輯者看到會發生什麼事），就會把**同一個真實 header 值**靜默帶到新輸入的任何 host，沒有任何提示、沒有任何 log，下一次真正的抓取（頁面載入、`RefreshInterval` 自動刷新、或 `DashboardAlertHostedService`）就把憑證送到新 host。公開 HTTPS 目的地會走 `RestWidgetDataSource` 的 fast path、完全不諮詢 `IDashboardEgressPolicy`，所以 #948 的 egress 閘門也擋不到這個——那道閘門回答的是「這個應用程式能不能連到這個網路位置」，不是「這個特定憑證該不該跟著這個 widget 換到新位置」。**這不是權限提升**——編輯者本來（#957 修復前）就能透過 `Get` 讀到明文——但代表讀取端的遮罩，只差編輯者一次普通的網址編輯就會被繞過，而初輪的 CHANGELOG 與殘留缺口清單都沒有講到這點。

    **修法，不是整包拒絕**：`ReconcileWidgetHeaders` 現在會比較傳入 widget 的 `RestOptions.Url` 跟既有的 URL 是否落在同一個「目的地」（scheme+host+port，host 大小寫不敏感，預設 port 正規化讓 `https://h`／`https://h:443` 視為相同——用 `Uri` 解析，不是字串比對）。目的地相同 → reconcile 邏輯照初輪原樣進行（最常見的情況：同一個 host 上改路徑／查詢字串）。目的地不同——或任一邊 URL 根本解析不出來，這種模糊情況刻意判定為「不是同一個目的地」，絕不落入保留分支——既有 headers 就不會被帶過去：case (a) 讓 `Headers` 變空（靜默清空，不是拒絕，透過新的 `HeaderDriftWarning`／`LogWarnings` 記 log；選清空而非整包拒絕，是因為擋掉整次存檔會讓 widget 的 URL 一旦設過 header 就永久鎖死——designer 沒有任何方式重新提供 headers），case (d) 的「既有 key 收到 sentinel」則被導進 case (e) 的拒絕分支，而不是靜默還原（呼叫端明確針對新目的地送出 round-trip 的 sentinel，這不是出貨中的 designer 會產生的形狀，用更嚴格的回應是合理的）。這個設計刻意模仿 `HttpClient` 自己在跨 host redirect 時的行為——不轉發 `Authorization`，而是拿掉——也是 #948 讓這個 client 停用自動 redirect 的同一個理由：憑證不會自動跟著目的地變更走，這是原則，不是這個端點的一次性 heuristic。

    **已知殘留，明講不隱瞞**：編輯者若把 widget 的 host 換掉，並在同一次或之後的存檔明確重新輸入**同一個**真實 header 值（case (c)——呼叫端自己打的真實值，不是 round-trip 回來的 sentinel），這次存檔會成功，是刻意的設計——這跟合法的憑證輪替無法區分。F1 關掉的是「靜默、無感知步驟」的轉發路徑，不是編輯者本來就有的、刻意把憑證打到任何地方的能力——那個能力沒有改變，是每個編輯者既有 `CanEdit` 範圍內的事，不是本票新開的洞。

    **測試**：兩個檔案合計新增 15 支（14 支單元 + 1 支端對端；F4／F5 各自的測試數量分開列在下方各自的段落）——逐案例單元測試（同 host 保留，含只改路徑的情境；跨 host 清空並斷言 warning 內容；跨 host 的 sentinel 被拒絕；host 大小寫不敏感；`http`／`https` 各自的預設 port 正規化；port 不同；scheme 不同；以及「決定並逐一測試」而非假設——傳入端 URL 解析不出來、既有端 URL 解析不出來，兩個方向各自獨立測試），加一支端對端重現完整攻擊腳本（先用真實憑證在某個 host 建立 widget，PUT 一個只改了 URL 的 designer 形狀 payload，斷言持久化的定義和實際 widget-data 抓取管線都沒有帶著憑證跟過去）。
  - **F4——未文件化的第六個情境，語意相反：傳入 widget 有 `Source` 但沒有 `Source.RestOptions` 節點，會靜默清空既有的整個 `RestOptions`（含 URL，不只 headers）——恰好是上面 case (a) 的相反。** 原本的五案例文件從未提到這個情境，初輪的 CHANGELOG 告訴整合者「省略這個鍵現在會保留既有 headers」——這句話對 `headers` 是真的，對 `restOptions` 本身是假的。原本唯一涵蓋這個情境的測試（`..._ignores_widgets_without_RestOptions`）只斷言沒有回傳錯誤，從未斷言清空的方向究竟是哪一種——行為兩個方向都沒被釘住，測試名稱裡的「ignores」本身也不是程式碼實際做的事。**修法，case (f)**：當傳入 widget 沒有 `RestOptions` 但 `Source.Kind` 仍是 `"rest"` 時，整個既有 `RestOptions`（複製，不是別名）會被保留——跟 case (a) 同一個原則往上一層：partial update 裡的缺席代表「呼叫端沒碰這裡」，不是「刪除它」。當 `Kind` 已經換成別的（例如 designer 自己的 source-kind 下拉選單），拿掉 `RestOptions` 是正確、刻意的行為，case (f) 不適用——用一支專門的邊界測試釘住，避免這個修法自己過度套用、讓 widget 的 REST 設定永遠沒辦法真正移除。舊測試改名為 `..._non_rest_widget_without_RestOptions_is_a_no_op` 並補上真正的斷言。**測試**：新增 3 支（case (f) 保留、clone 而非別名的驗證、Kind 換走的邊界 pin）。
  - **F5——`_DashboardDesignerController.Preview` 自己的 reconcile 防線完全沒有測試；刪掉那個呼叫點不會讓任何測試轉紅。** 原本兩個 mutant 都只針對 `_DashboardController`。**測試**：新增 2 支——`Preview_with_masked_sentinel_header_value_is_rejected`（red-before-fix）與正面對照組 `..._with_a_real_header_value_is_accepted_by_the_header_guard`——以及第三個 mutant `957-dashboard-preview-header-guard-neutralize`，專門鎖定這個呼叫點。**同時更正下方（4）的舊揭露**：「防線本身是純防禦性」跟「防線沒有測試」是兩件不同的事——後者現在已經不成立（有測試、有 mutant），前者依然成立（見下方修正後的（4））。
  - **F6——`MaskedHeaderValue` 從 `const` 改成 `static readonly`。** WTM 以 NuGet 套件形式出貨；`const` 會在下游組件「自己編譯的當下」被內嵌進 IL。如果這個 sentinel 字面值未來哪個版本改了，已經編譯好的下游二進位檔會永遠拿舊值比對，而且會把從正式伺服器收到的**新** sentinel 當成字面憑證存起來，因為它自己內嵌的比對永遠不會命中。`static readonly` 在消費端執行期才解析，比對的是實際載入的 WTM 組件，不會有這個飄移窗口。
  - **F7——遮罩只涵蓋 `Headers`；`Url` 與 `Body` 在 `Get` 的遮罩回應裡完全原文照傳，而這兩個欄位夾帶憑證的機率不亞於 `Headers`。** `Url` 本身可能透過 userinfo（`https://user:pass@host/...`）或查詢字串裡的 key（`...?api_key=...`）夾帶憑證；`Body` 可能在 POST payload 裡夾帶。任何通過 `CanAccess` 的檢視者現在還是會拿到這兩者的完整內容。**本票刻意不修——範圍決定留給維護者**；已列進下方「未涵蓋、刻意不修」清單，並附上這兩個具體形狀，避免文件讀起來像是這個端點的憑證外洩問題已經全部關閉。
  - **F8——反射驅動的 clone-drift guard（`DashboardCredentialMaskingCloneDriftTests`）自己也有一個盲點：它的 `bool`產生器永遠回傳 `true`，所以未來如果哪個 `bool` 屬性自己的預設值就是 `true`，「填入」它會跟預設值無法區分，clone 把它漏掉也偵測不到。** 修法是通用性的，不是針對 `bool` 特判：populator 現在對每個屬性都先建構一個該宣告型別的全新預設實例，讀出那個屬性自己真正的預設值，然後重試產生（`bool` 交替 true/false；`enum` 依序輪過每個成員）直到候選值確實跟預設值不同——如果某個型別結構上就是生不出第二個不同的值，就丟例外，呼應這個 guard 自己已經建立的原則：一個跟預設值無法區分的填入值，永遠沒辦法證明未來的 clone 把它漏掉了。**已實測驗證，不只是宣稱**：暫時在 `RestWidgetDataSourceOptions` 加一個預設值就是 `true` 的 `bool` 屬性、刻意不加進複製邏輯，guard 立刻抓到（`expected 'False', actual 'True'`）；擷取失敗訊息後移除。

  **全套件重跑（本輪，累計）**：`test/WalkingTec.Mvvm.Core.Test` `4927 passed, 0 failed`（對照重評基準 `4874 passed`，差額 53 = 初輪 29 支 + clone-drift guard 1 支 + pre-fix JSON round-trip 3 支 + 本輪 F1／F4／F5 新增 20 支）；`src/WalkingTec.Mvvm.Mvc.Tests` `64 passed, 0 failed`；`test/WalkingTec.Mvvm.Api.Test` `102 passed, 0 failed`（1 個既有 mutation-gate baseline selftest fixture 依慣例 skip）。四個 mutant 皆 `VERDICT: KILLED` / `GATE: PASS`：`957-dashboard-get-masking-neutralize`（還原 `Get` 為修復前的 `return Ok(dashboard)`）、`957-dashboard-update-header-merge-neutralize`（註解掉 `Update` 的 `ReconcileWidgetHeaders` 呼叫）、`957-dashboard-header-host-scoping-neutralize`（F1——強制目的地比對永遠判定為「相同」）、`957-dashboard-preview-header-guard-neutralize`（F5——註解掉 `Preview` 自己的 reconcile 呼叫）。

  **未涵蓋、刻意不修**：(1) `Headers` 內容本身（格式、大小、字元集）仍未做輸入驗證——這是 #948 F5 就已經揭露、本次刻意不擴大範圍的既有殘留缺口；本票只解決「憑證外洩給無權限檢視者」與「更新時被靜默清空/覆寫成 sentinel 字面值」兩點，不等於「`Headers` 已經過完整輸入驗證」。**2026-07-31 更正：(1) 已被 #956 部分關閉**——固定的 hop-by-hop/framing 欄位名稱、header 數量上限、name+value 總長度上限，見下方 #956 條目；一般字元集/格式仍未驗證，`Authorization`/自訂 header 的實際內容本身依然不受限制（這是刻意的，見該條目）。(2) 遮罩用的 sentinel 是一個固定字串常數，不是密碼學安全的隨機值——如果某個 header 的真實值恰好等於這個字面字串，會被誤判成 case (d)/(e)（拿既有值蓋過去，或在沒有既有值時被拒絕）；機率極低（需要呼叫端主動把這個特定字串設成真實憑證），但不是不可能，屬已知邊界，未特別加防護。(3) 上方「讀取路徑窮舉」的結論是**驗證現在的程式碼確實如此**，不是「未來永遠如此」的結構性保證——如果之後有人把 debug 用的 `RestOptions` dump 塞進 `WidgetDataResult.Metadata`，這次的遮罩機制不會自動涵蓋到那個新欄位，需要重新跑一次同樣的 grep 並人工確認。(4) **修正（F5 審查）**：`_DashboardDesignerController.Preview` 的 reconcile 防線本身，現在有直接測試與專屬 mutant 覆蓋（見上方 F5）——「防線沒有測試」這句舊揭露已不成立。仍然成立、且是不同的一句話：出貨中的 designer 從未在 Preview 的 request body 帶 `headers`，這條分支目前沒有被真實 UI 流量走過，跟 #948 F6 揭露「未被 UI 走過的管道」是同一種揭露方式——「有測試涵蓋」與「被真實流量走過」是兩個獨立的事實，不能互相替代。(5) **F7（見上方）**：`Url`／`Body` 在遮罩回應裡完全未遮罩，`https://user:pass@host/...` 與 `...?api_key=...` 是兩個具體的憑證外洩形狀，本票刻意不修。(6) F1 的 host-scoping 沒有、也沒辦法擋住編輯者刻意把真實憑證重新輸入到新 host（case (c)）——見上方 F1 自己的殘留揭露。

- **#952（P1，已修，承接上方 F5「與 #952（快取鍵不含 header）是相鄰但獨立的風險」的殘留揭露）：`RestWidgetDataSource.BuildCacheKey` 是 `$"...{Method}::{Url}::{Body}::{JsonPath}"` 這種手動列舉字串，完全不含 tenant 也不含 `Headers`——兩個不同租戶、或同一租戶下兩個只差在 `Headers`（例如一個帶真實 `Authorization`、一個完全沒帶）的請求會落到同一個 `IMemoryCache` 條目，後者在 TTL 內會直接讀到前者的已授權回應。** 這不是新洞——是 #948/#955 審查時就已經點名、本輪才動的相鄰缺陷（見上方 #948 條目「`WidgetDataRequest`／REST 回應快取路徑刻意未觸碰」）。**設計裁定的重點不是「把 Headers 加進 key」，是「讓 key 結構上不可能漏欄位」**：本票的設計審查階段，一版提案打算沿用手動列舉的形狀（在既有四欄位上再加 `Headers`），但那版提案自己就漏掉了 `AllowedPorts`——這正是「再列舉一次」這條路本身不安全的實證，不是假設。**修法**：`BuildCacheKey` 改成對**整個** `RestWidgetDataSourceOptions` 物件做反射序列化（`JsonSerializer.SerializeToNode`），再遞迴把每一層 `JsonObject` 的屬性依 `StringComparer.Ordinal` 重新排序（`CanonicalizeNode`——不含任何 `Headers` 專屬邏輯，未來任何新的 `Dictionary<string,string>` 屬性都會被同一套邏輯排序），序列化成不含空白的 JSON 字串，前面接上 `(tenantId ?? " null")`（沒有分隔符——JSON 字串以 `{` 開頭，與任何合理的 tenant code 都不會混淆），整段做 SHA256、hex 編碼、前綴 `WtmRestWidget::v2::`。**代價是刻意接受的**：兩個只差在 `MaxResponseBytes`（不影響實際回應內容）的小工具現在不會共用快取條目——本 repo 的既定優先序是 Compatibility > Security > Quality > **Performance**，結構完整性排在效能之前。**不用 HMAC、不用隨機 salt，兩者都是刻意決定**：(a) 任何能在同一個 process 裡列舉 `IMemoryCache` keys 的行為者，也同樣能直接從小工具定義快取讀到明文 `Headers`——同一個 process 裡的 HMAC 金鑰不會拉高這個威脅模型的門檻，只會增加成本；(b) 隨機 salt 會讓「兩個租戶拿到不同 key」變成一句永遠不會失敗的斷言——本 repo 已有紀錄在案的失敗模式（見 memory：`random-salt-tautology`），設計審查階段一版提案採用隨機 salt，因這個理由被否決。**null vs `{}` 的裁定（設計審查沒涵蓋、本次刻意決定並記錄）**：#957 之後 `Headers` 是 `Dictionary<string,string>?`，`null`（未設定）與 `{}`（明確清空）在型別層面可分辨，但在**送出的 HTTP 請求**上完全相同（`FetchJsonAsync` 的 `if (options.Headers != null) foreach` 對兩者都送出零個自訂 header）。裁定：**兩者刻意不正規化成同一個 key**——正規化雖然在今天是安全的最佳化（兩者送出的請求位元組相同），但要讓 `BuildCacheKey` 知道並依賴 `FetchJsonAsync` 這個送出期的行為，等於重新引入這次重新設計整個要移除的「逐欄位特判」；代價是一次可避免的 cache miss（一個設 `Headers: {}` 的小工具、與另一個完全沒設定 headers 的小工具，原本可能命中同一個 key），跟 `MaxResponseBytes` 的破碎化代價同一個優先序下接受。**測試**：`RestWidgetDataSourceTests.cs` 新增 4 支——tenant 隔離（RED pre-fix：`Assert.AreEqual failed. Expected:<2>. Actual:<1>.`）、同租戶下 `Headers` 隔離（RED pre-fix，同一個失敗訊息形狀）、正面對照組（位元組完全相同的兩次請求仍共用快取，`Assert.AreEqual(1, callCount)`——沒有這支，一個乾脆整個關掉快取的「修法」也會通過前兩支）、以及端到端攻擊重現（一個帶真實 `Authorization` 的特權小工具先完成一次抓取並快取；TTL 內第二個沒帶任何 header 的小工具用完全相同的 `Url`/`Method`/`Body`/`JsonPath` 呼叫 `GetDataAsync`，斷言拿到的是自己的回應 `"public-unauthorized-data"`，不是快取住的 `"vip-authorized-data"`）。**竊取是決定性的，不是競速**：pre-fix 情境下，未授權探測若打到非 2xx 上游會在 `FetchJsonAsync` 丟例外、發生在 `_cache.Set` 之前，所以失敗的探測不會污染 key——攻擊者可以免費重試到受害者的回應真的落地為止，不需要贏過任何時序窗口；本票的端到端測試不需要刻意模擬這個輪詢過程，兩次呼叫的順序本身就是竊取會被觀察到的那個時刻。**驗收**：`test/WalkingTec.Mvvm.Core.Test` `4996 passed, 0 failed`（對照 #957 收尾時的 `4927 passed`，差額 69 = #952/#956/#948-F8 三票合計新增 70 支測試 − 1 支因下方 #956 揭露的既有機制混淆而移除的 DataRow case）；`src/WalkingTec.Mvvm.Mvc.Tests` `64 passed, 0 failed`；`test/WalkingTec.Mvvm.Api.Test` `102 passed, 0 failed`（1 個既有 mutation-gate baseline selftest fixture 依慣例 skip）。Mutant `952-restwidget-cachekey-tenant-component-neutralize`（拿掉 canonical 輸入的 tenant 分量）`VERDICT: KILLED` / `GATE: PASS`。**未涵蓋、刻意不修**：(1) `IMemoryCache` 本身仍是單一 process 記憶體內快取，不是分散式快取——本票沒有改變這個既有架構，多執行個體部署下每個 process 各自維護一份快取，不在本票範圍。(2) 快取碎片化（fragmentation）本身是刻意接受的效能代價，見上方設計說明，不是缺陷。

- **#956（P1，已修，承接上方 F5「同一物件上的 `Headers`/`Method`/`Body` 仍未驗證」的殘留揭露）：`RestWidgetDataSourceOptions.Headers` 對呼叫端送出去的 HTTP 請求完全沒有框架層級的內容限制——沒有欄位名稱黑名單、沒有數量上限、沒有大小上限。** 設計審查階段把 issue 原文「`Headers`/`Method`/`Body` are unvalidated」的框架修正為更精確的形狀：真正的問題不是「什麼格式都能塞」，是**框架本身該無條件擋掉哪些欄位名稱**——`Authorization`、自訂 header（`X-Api-Key` 等）是這個功能存在的理由（一個 REST widget 呼叫需要 API key 的外部服務），不能被這次修法波及。**修法**：新增 `RestWidgetDataSource.ValidateHeaders`（單一共用實作，寫入時與送出時都呼叫同一份）——(a) 硬性拒絕以下欄位名稱（大小寫不敏感）：`Host`、`Transfer-Encoding`、`Content-Length`、`Connection`、`Upgrade`、`TE`、`Trailer`、`Expect`、以及任何以 `Proxy-` 開頭的名稱；(b) header 數量上限 20；(c) 所有 name+value 的 UTF-8 位元組總長度上限 8 KB。**為什麼是這組名單，不是任意黑名單**：這組欄位會打破 #948 自己的 DNS-pinning／connect-time SSRF guard 的不變式——`Host` 是最尖銳的例子：`PinnedConnectAsync` 已核准並連線到一個特定解析出的 IP，但刻意保留原始 hostname 當作外送請求的 URI（因此預設 `Host` header 也是那個 hostname），這樣 TLS SNI 與伺服器憑證驗證才會用對值；如果呼叫端能覆寫 `Host` header，同一個已核准的 IP/port 連線就能對伺服器呈現成完全不同的 virtual host——policy 檢查過的 URL 沒變，但伺服器實際把這個請求當成「給誰」的語意變了。其餘欄位是標準的 hop-by-hop/framing header：`Transfer-Encoding`/`Content-Length` 可用於 request smuggling，`Connection`/`Upgrade`/`TE`/`Trailer` 可用於把底層 socket 卡在非預期狀態，`Proxy-*` 是這個 client 從未打算暴露的代理專屬語意。**強制在寫入時與送出時各自獨立驗證**：`JsonFileDashboardService`/`EfCoreDashboardService.ValidateWidgetConfigs`（Create/Update，Preview 走同一條 `CreateAsync`）在寫入前拒絕（`ArgumentException`，沿用 `AllowPrivateNetwork`/`AllowHttp`/`AllowedPorts` 既有的「拒絕不靜默清除」先例）；`RestWidgetDataSource.FetchJsonAsync` 在每次送出前再驗證一次（`InvalidOperationException`）。**只做其中一層都不夠**：只做寫入時，會讓修法上線前就已持久化、或被直接改 JSON 檔繞過驗證的舊小工具永遠不受保護——這正是本 repo 一再出現的「guard 沒蓋到它原本該蓋的路徑」形狀；只做送出時，operator 檢視/儲存一個小工具定義時完全看不到任何訊號，直到真的有人去看那個 widget 才會發現定義本身是壞的。**測試**：寫入時（`DashboardWidgetConfigValidationTests`／`EfCoreDashboardServiceTests` 各一組，鏡射既有 `AllowedPorts` 測試群的形狀）涵蓋 9 個硬拒絕名稱（含大小寫不敏感、`Proxy-` 前綴）、數量上限、大小上限，加正面對照組（`Authorization` + 自訂 header 仍被接受）；送出時（新增 `RestWidgetHeaderHardeningTests.cs`）鏡射同一組情境，另外直接單元測試 `ValidateHeaders` 本身（含邊界值：剛好等於上限、剛好超過上限）。**送出時的 DataRow 測試集刻意不含 `Content-Length`**：RED-verify 時發現拿掉本票的 guard 之後，`Content-Length` 這個案例仍然維持綠燈——不是因為本票的 guard 還有效，是因為 .NET 自己的 `HttpRequestHeaders.Add` 本來就會拒絕 `Content-Length`（它屬於 `HttpContent.Headers`，從不屬於 `HttpRequestMessage.Headers`），而這個檔案既有的 S3 CRLF-injection guard（`catch (Exception ex) when (ex is FormatException || ex is InvalidOperationException)`）剛好把 BCL 的拒絕包裝成跟本票 guard 一樣的例外型別——對「送出時」這個 guard 本身而言，`Content-Length` 這一格是裝飾，不是證據；寫入時沒有這個混淆（`ValidateWidgetConfigs` 從不碰 `HttpRequestHeaders`），該案例維持在寫入時測試群裡並已 RED-verify。**驗收**：見上方 #952 條目的全套件重跑數字（同一輪重跑涵蓋三票）。Mutant `956-restwidget-header-write-time-guard-neutralize`（中和 `JsonFileDashboardService` 的 guard 呼叫）與 `956-restwidget-header-send-time-guard-neutralize`（中和 `FetchJsonAsync` 的 guard 呼叫）皆 `VERDICT: KILLED` / `GATE: PASS`。**未涵蓋、刻意不修**：(1) `Headers` 內容本身（字元集、一般格式）超出硬拒絕名單之外的部分仍未驗證——`Authorization`/自訂 header 的實際值可以是任何字串，這是刻意的（見上方「為什麼是這組名單」）。(2) `Method`（仍只認 GET/POST，S1 既有機制未變動）與 `Body`（除了 #948 既有的 `MaxResponseBytes` 上限外，未加輸入驗證）本身不在本票範圍——issue 原文點名的三個欄位裡，本票只處理 `Headers`。

- **#948-F8（P2，已修，承接 #955 review 對 #948 的第 8 項發現，先前裁定暫緩）：`DashboardEgressDestination` 不帶任何 tenant／dashboard／widget／method／header-name／has-body 資訊，`IDashboardEgressPolicy` 實作因此永遠沒辦法依這些維度做決策，且沒有任何管道能把「這個目的地是哪個 widget 產生的」餵回去。** 先前裁定暫緩的理由是「介面還沒真正發版，之後要加也還來得及」——本次維護者重新裁定：**理由本身有一半是假的**。`DashboardEgressDestination` 是 sealed class，只由框架建構、只由 policy 讀取——加 non-`required` 屬性對它是 100% 相容的加法，不是「binary/source 雙重 breaking」（那個顧慮描述的是 interface 的情境，不是這個 sealed class）。真正該現在做的理由是**語意凍結成本，不是相容性期限**：一個從沒見過 `TenantId` 的 v1 policy 實作，往後永遠沒辦法察覺租戶維度的存在——每多發一個版本，這個凍結的成本就更貴一次。**修法**：新增 6 個 non-`required` init 屬性——`TenantId`、`DashboardId`、`WidgetId`、`Method`、`HeaderNames`、`HasBody`。**全部一次加齊，不是留一個「以後再加」的名額**：原始裁定的敘事把這個決定框成「先佔一個相容性名額」，但沿用上面的理由（加法本身零代價），沒有名額可省——分兩批加只會讓語意凍結的視窗多開一輪。**單一函式建構，寫入時與連線時共用**：新增 `RestWidgetDataSource.BuildDestination`（`SelectConnectableIpAsync` 的 policy 諮詢迴圈裡唯一的建構點，`ValidateUrlAsync`／`PinnedConnectAsync` 都只透過這個既有的共用呼叫路徑抵達）與 `BuildRequestContext`（把 `options`／tenantId／dashboardId／widgetId 轉成 `RestWidgetRequestContext`，`ValidateUrlAsync` 與 `FetchJsonAsync` 各自呼叫一次、相同輸入產生欄位相等的結果；`FetchJsonAsync` 把結果透過 `HttpRequestMessage.Options` 帶給 `PinnedConnectAsync`）——保證兩層看到的 destination 逐欄位相同，不是「兩份手動同步的複本」那個本 repo 已經出過事的形狀（見 #955 F7）。`WidgetDataRequest` 新增 `DashboardId`/`WidgetId`（optional，純加法）；`JsonFileDashboardService`/`EfCoreDashboardService.GetWidgetDataAsync` 兩者本來就有這兩個 id 當方法參數，順手接上。**`HeaderNames` 只帶名稱，永遠不帶值**：doc comment 同時寫明兩面——policy 可以用它做「有沒有設定 Authorization」這類不碰憑證內容的判斷，但看到一個名稱出現在這裡，不代表框架已經驗證過那個值本身（#956 的硬拒絕檢查是獨立跑的，不會反映在這個集合上）。**`DashboardEgressAllowlistEntry` 真的讀 `Method` 跟 `TenantId`**：只加欄位、沒有任何地方消費，是投機性泛化——本票同時給 `Method`／`TenantId` 一個真正的消費者：`ConfiguredAllowlistDashboardEgressPolicy.IsAllowedAsync` 新增 `Methods`（null/空 = 任何方法，向後相容既有設定）與 `TenantId`（null/空 = 任何租戶，同樣向後相容）兩個閘門；`DashboardId`／`WidgetId`／`HeaderNames`／`HasBody` 目前沒有內建 policy 消費，是刻意的——它們是給**自訂** `IDashboardEgressPolicy` 實作用的上下文（本票的消費者要求只針對 `Method`/`TenantId`，因為那兩個是 `DashboardEgressAllowlistEntry` 已有的、只是還沒接線的維度），不是每個新欄位都需要內建 policy 立刻消費才算數。**測試**：`RestWidgetRequestContextTests.cs`——(a) 直接驗證 pre-check（`ValidateUrlAsync`，真的執行到）跟「直接呼叫 `SelectConnectableIpAsync`」（餵進用同一個 `BuildRequestContext` 建出的 context——`SocketsHttpConnectionContext` 沒有 public 建構子，測試沒辦法直接驅動 `PinnedConnectAsync` 本身，這點誠實揭露，見下方未涵蓋）逐欄位比對相等；(b) `HeaderNames` 對一個帶密碼型 header 的請求只含名稱、不含任何值片段。`ConfiguredAllowlistDashboardEgressPolicyTests.cs` 新增 8 支涵蓋 `Methods`/`TenantId` 的核准／拒絕／大小寫不敏感／null 代表任意。**驗收**：見上方 #952 條目的全套件重跑數字。Mutant `948f8-configuredallowlist-method-check-neutralize`（中和 `Methods` 閘門）`VERDICT: KILLED` / `GATE: PASS`。

**同日異質審查（2026-07-31）——兩項後續，皆非缺陷**：(A) 審查者要求對「送出時的 8 個硬拒絕 header 名稱 DataRow」逐一實測 `HttpRequestHeaders.Add(name, "x")` 是否真的成功（不能只靠分類規則推理），因為 `Content-Length` 那一格已經證明過「以為會成功、實測才發現被 BCL 自己攔下」這個坑真實存在。**實測結果（拋棄式探測程式，非假設）**：`Host`、`Transfer-Encoding`、`Connection`、`Upgrade`、`TE`、`Trailer`、`Expect`、`Proxy-Authorization` 這 8 個名稱在本機 .NET 10 build 上全部 `Add` 成功——沒有第二個 `Content-Length` 式的混淆。**沒有動任何程式碼或測試**——8 個 DataRow 全部維持既有形狀，因為全部都是本票 guard 自己的真實負載。(B) 審查者指出 `req.Options.Set(RequestContextOptionKey, ...)`（`FetchJsonAsync`）與其讀回（`PinnedConnectAsync`）這兩行完全沒有「刪掉會變紅」的測試——所有既有測試要嘛用 `MockHttpHandler`（完全繞過 `SocketsHttpHandler`，永遠不會走到 `PinnedConnectAsync`），要嘛（(a) 那支）用手動建構的 context 直接呼叫 `SelectConnectableIpAsync`，模擬而非驅動連線時那一側。**修法**：把 `PinnedConnectAsync` 的讀回邏輯抽成 `RestWidgetDataSource.ReadRequestContext(HttpRequestMessage)`——`PinnedConnectAsync` 本身改成呼叫這個方法，不再內嵌 `TryGetValue`。新增 `FetchJsonAsync_sets_the_request_context_option_matching_BuildRequestContext`：真的驅動一次 `GetDataAsync`（`req.Options.Set(...)` 那行因此**真的執行**，不是模擬），從 mock handler 攔截到的真實 `HttpRequestMessage` 上呼叫 `ReadRequestContext`——**呼叫的是 `PinnedConnectAsync` 自己也呼叫的同一個方法，不是手抄一份 `TryGetValue`**。RED-verify（拿掉 `req.Options.Set(...)`那行、和拿掉 `ReadRequestContext` 方法體，各自單獨測試）都讓這支新測試轉紅，其餘測試不受影響。**縮小後精確揭露剩餘缺口**：唯一真正沒有任何測試覆蓋的是 `PinnedConnectAsync` 內部那一行單純的委派呼叫本身——`var requestContext = ReadRequestContext(context.InitialRequestMessage);`——**已實測驗證這個缺口的邊界，不是宣稱**：把這一行改成 `RestWidgetRequestContext? requestContext = null;`（只中和委派呼叫本身，不動 `ReadRequestContext` 的方法體），整個 `Dashboard` 命名空間 629 支測試全數維持綠燈，證實這一行今天確實是零測試覆蓋。**為什麼這樣是安全的（不是「不重要所以不修」）**：`ReadRequestContext` 找不到值時回傳 `null`（`TryGetValue` 的 `out` 參數在查無此鍵時维持型別預設值），`BuildDestination` 對 `context: null` 的處理方式與所有其他呼叫路徑相同——`TenantId`/`DashboardId`/`WidgetId`/`Method`/`HeaderNames` 全部退回 `null`、`HasBody` 退回 `false`；`ConfiguredAllowlistDashboardEgressPolicy` 的 `Methods`/`TenantId` 閘門對 `null` 一律**fail closed**（見上方修法段落），不會有任何 pre-F8 就存在的 policy 因此意外核准更多目的地；而且 `ValidateUrlAsync`（pre-check）已經在 `FetchJsonAsync` 執行前用完整 context 檢查過一次，`PinnedConnectAsync` 這行即使被刪除，connect-time 這層只會退化成 pre-F8 的 host/port/IP 比對，不會出現「policy 看到的目的地」與「pre-check 核准的目的地」不一致的情況。**重新命名，不擴大既有斷言範圍**：原本叫 `PreCheck_and_simulated_ConnectTime_produce_field_equal_destinations_for_the_same_fetch` 的測試改名為 `PreCheck_and_a_direct_SelectConnectableIpAsync_call_produce_field_equal_destinations_for_the_same_fetch`——舊名讀起來像「模擬過連線時」，容易被誤讀成 `PinnedConnectAsync` 本身被驅動過；新測試是獨立新增的一支，不是把斷言塞進舊測試裡讓它「順便」碰到那一行。**未涵蓋、誠實揭露**：(1) `PinnedConnectAsync` 內部那一行委派呼叫本身仍是零測試覆蓋，理由與安全性見上方——這是本票能觸及的邊界（`SocketsHttpConnectionContext` 無 public 建構子），已知、已記錄，不是遺漏。(2) `DashboardId`／`WidgetId`／`HeaderNames`／`HasBody` 目前只有 `RestWidgetDataSource` 這一個框架端消費者把它們填進 `DashboardEgressDestination`，沒有任何內建 `IDashboardEgressPolicy` 讀取它們——`ConfiguredAllowlistDashboardEgressPolicy` 只讀 `Method`/`TenantId`，這是刻意的範圍（見上方），不是遺漏，但如果之後要讓內建 policy 也能依 widget/dashboard id 決策，需要另外擴充 `ConfiguredAllowlistDashboardEgressPolicy`。

**新揭露的缺口（未修，已立案）**：目前無新揭露、未修的缺口——本節維持存在做為標準結構（`ProductionReadinessBaselineDriftTests863` 的結構性防呆會抓這個標題被靜默改名/移除），#948-F8 是本節最近一次的內容，已於上方移至「強化（已合併）」。

**#863 更正**：這裡曾在下列三個都已關閉後仍列為未修，其中 #721 那條還進一步指示採用者「先確認已修」——等於叫人去查一個已經不存在的問題，而這份文件正是 CLAUDE.md Red Line 用來比對每個 commit/PR/CHANGELOG 宣稱強度的基準，基準本身漂移就讓整個機制失效。三者現況：#721（HTTP 層 refresh 路由衝突）已於 2026-07-17 修復；#722（`ff.OpenDialog2` 剝除 selector 對話框自身 `<script>`）已於 2026-07-18 修復；#696（public mirror 洩漏 macOS 使用者名稱/內部路徑）已於 2026-07-18 修復。維護機制：`ProductionReadinessBaselineDriftTests863`（`test/WalkingTec.Mvvm.Core.Test/Security/`）會抓出本節每個以 `- **#N` 起始的條目、查 Gitea API 狀態，若已關閉卻還列在這裡就會讓測試紅——需要網路與（非必要的）token，環境不可用時明確 Inconclusive，不會誤判成綠燈。

**未解的硬化 epic**：
- **#470 / #567**：ff.OpenDialog eval sink 退役與 LayUI CSP 全硬化仍進行中。#627 kill-switch（可 opt-in 停用 legacy 動態腳本執行）+ islandification 已推進，`framework_layui.js` 全檔僅剩 1 個 `eval(`（deprecated IsScript 路徑）。**對一般 CRUD app，kill-switch 已可作為 staging 稽核工具**；生產全面啟用待 widget islandification 完成。**這是走 strict CSP 的採用者最需要追蹤的 roadmap。**

---

## 三個要誠實面對的弱點

### 1. 單人維護（bus factor = 1）

WTM 是個人 fork（chiu0831，2026-03 接手）。整個 fork 的演進、修補、release、安全 audit 都靠一個人（近期以多代理 workflow + 對抗式審查放大單人產能，但決策與責任仍集中一人）。對比 ABP Framework 或 Microsoft 自家框架，社群檢視眼數差兩個數量級。

**意義**：採用 WTM = 採用維護者的判斷力與時間承諾。雖然透過 issue → PR → CI gate + 對抗式審查降低錯判機率（本批次實證有效——多個會進主線的真實缺陷在合併前被攔），仍存在單點風險。

**建議**：若業務關鍵程度高，考慮自己再 fork 一份、本 fork 當 upstream，並準備好「framework 退役」的逃生路徑（見下方）。

### 2. 測試覆蓋率仍偏低（~20% 行 / ~16% 分支），但趨勢正確

測試絕對數已從 1,647 成長到 ~5,780（.NET）+ ~1,704（JS），CI coverage gate 已從卡死的 15%/10% 修正並上調至 **18%/14%**（ratchet 恢復運作，目標 line 60% / branch 40%），且新增了 **#565 LayUI regression Playwright gate** 為 layui 版本升級站崗、**e2e kill-switch matrix leg** 為 CSP 路徑站崗。

但對比業界 production-grade framework 一般 60%+ 行覆蓋率，這仍有相當距離。

**意義**：refactor 與 dep upgrade 的回歸風險比覆蓋好的框架高。~5,780 tests pass 不能視為「保證無回歸」——本批次的 #721 route collision 正是「單元測試全綠卻有整合層缺陷」的活例。你**自己這層**的測試必須補強。

**建議**：對你的業務邏輯（VM、Controller、**整合/HTTP 層**）寫 80%+ 覆蓋，不要假設 framework 的 20% 已經夠。特別是 HTTP 端到端測試——它抓得到單元測試看不到的路由/中介層問題。

### 3. NPOI 漏洞透過 transitive override pin 緩解，非根本解決

NPOI 2.7.6（也包含最新 2.8.0）transitive 拉 vulnerable `System.Security.Cryptography.Xml`（兩個 high CVE：GHSA-37gx-xxp4-5rgx、GHSA-w3x6-4m5h-cxqf）。

當前緩解：`Core.csproj` 顯式 reference 覆蓋（本批次隨 MS 套件群對齊至 10.0.9，pin 隨之提升以維持 >= group）。**這條 reference 絕對不能因為 NU1510 informational warning 而誤刪**（已有 XML 註解警示）。同類 override 也已擴及 Benchmarks 專案（#674：新 ProjectReference 會透過 NPOI 拉回漏洞版，對抗式審查在合併前攔下）。

**2026-07-31 追加發現與修復（#934）——上面這條 override 本身有效，但下游收不到。** `dotnet restore`/`dotnet list package --vulnerable` 這類對 solution 的檢查裡，override 一直有效；問題出在 `dotnet pack`。.NET 10 SDK 的 package-reference pruning 判準與觸發 NU1510 的判準相同（pin 版本落在 SDK 認定「framework 已提供」的基準內），一旦命中就把該 `PackageReference` 標成 `PrivateAssets=all`/`IncludeAssets=none`，從打包出的 nuspec `<dependencies>` 整條砍掉。實測：`dotnet pack src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj -c Release` 產出的 nuspec 有 13 個依賴，`NPOI 2.7.6` 在，`System.Security.Cryptography.Xml` 不在——從 registry 安裝的下游因此拿到的是 NPOI 自己宣告的 8.0.2（GHSA-37gx-xxp4-5rgx、GHSA-w3x6-4m5h-cxqf，兩個 HIGH），本 repo 自己的 solution-scoped 掃描結構上看不到這個落差（掃的是 solution，那裡 override 還是活的 `PackageReference`；掃描目標不是出貨物）。已有下游數月前獨立踩到這個問題，自行在自己的專案裡重複同一條 override 繞過——是已觀察到的分發缺陷，不是理論風險。

修法：`WalkingTec.Mvvm.Core.csproj` 加 `<RestoreEnablePackagePruning>false</RestoreEnablePackagePruning>`，只作用於該專案。驗證後 nuspec 恢復 14 個依賴，`System.Security.Cryptography.Xml 10.0.10` 回來，其餘依賴不變。曾先試兩個更窄的做法、實測後放棄：在 `PackageReference` 上顯式標 `IncludeAssets="all" PrivateAssets="none"`（pruning 無條件覆蓋使用者自設的 asset metadata，實測仍被裁）；寫一個 MSBuild target 在 `CollectPrunePackageReferences` 執行前把該套件從 SDK 產生的 `PrunePackageReference` item 清單移除（diagnostic log 可見該 target 確實跑在正確時機，但 restore 內部的 pruning 計算仍套用 SDK 基準，實測仍被裁）。逐一 pack 六個套件重新盤點其餘的 override：`SQLitePCLRaw.bundle_e_sqlite3` 3.0.3（Core，#393）、`Microsoft.OpenApi` 2.7.5（Mvc，#528）、`Common.Logging`／`Common.Logging.Core` 3.4.1（Etl）皆未被裁剪，未變動——只有 `System.Security.Cryptography.Xml` 命中 SDK 的 framework-provided 基準。

`publish-nuget.yml` 新增「Consumer-graph vulnerability scan」步驟，對 smoke-install 步驟已經裝好六個套件的同一個 consumer 專案跑 `dotnet list package --vulnerable --include-transitive`，沿用既有的 `scripts/check-vulnerable-packages.py`（對無法辨識的輸出 fail-closed）。修復前對六個未修的 nupkg 實測：8 個 HIGH finding（`System.Security.Cryptography.Xml` 8.0.2）；對修復後的 nupkg 實測：0 finding。這關掉的是結構性盲點本身——之後任何一條 override 被 pruning 裁掉，這個 gate 會紅，不必等下一次人工複查才發現；solution-scoped 掃描維持乾淨不再等於「消費端拿到的東西也乾淨」。

完整背景見 [`docs/dependency-management.md`](./dependency-management.md)。

**意義**：上游沒修，本 fork 在打補丁；打包管線本身也曾經悄悄漏掉這個補丁，現已加上專屬的 consumer-graph gate 監控。可接受，但你需要在 deployment 與 dep update 流程中明確紀錄這個 pin 不能動，也不能只信任 solution-scoped 的漏洞掃描結果。

---

## 2026-07 優化批次（對 readiness 的淨效果）

由「最終徹底優化規格書」驅動的 26-issue 批次（多代理 workflow：實作 → 對抗式驗證 → 修復迴圈執行），對 production-readiness 的實質提升：

| 面向 | 提升 |
|------|------|
| **CI 可靠性** | 反覆咬 CI 的 SQLite TCONC flake 家族**治本**（#709：競爭 fixture 改用 file-based WAL per-actor 連線，獨立 468 次迴圈 0 失敗）；coverage ratchet 修復（#671）；docs-only PR 不再跑全套（#670）；S3/Benchmarks 納入 CI（#658） |
| **交易正確性** | WorkFlow 全交易站點在 `EnableRetryOnFailure` 下合法 + deadlock 重試統一（#667）；strand-reaper 確定性排序（#665）；4 家 DB 的 unique-scope 一致（#664） |
| **安全縱深** | ETL SSRF/欄名/S3 硬化（#661/#680）；mirror leak-gate 補全（#659） |
| **效能** | 多個 evidence-based 熱點修復（各附 benchmark）；graph 快取（#666）；widget async（#662） |
| **可維護性** | 兩個 WorkFlow god-file（5,212+2,143 行）與三個 Core god-file（WTMContext 2,024→856、BasePagedListVM 1,834→998、DCExtension 1,303→527）拆成 partial；死碼清除；backtick 檔名修正 |
| **現代化** | TimeProvider 遷移首批（#676）；GeneratedRegex 熱點（#677）；SourceLink/snupkg/deterministic build/PackageReadme（#678）；Vue2/React demo 退役 + 42MB zip 移除 + 2 個 mirror CVE exception 清零（#679） |

**淨判斷**：地基（CI 穩定度、交易正確性、可維護性、依賴衛生）明顯更穩；同時本批次的 live e2e **誠實揪出**一個先前隱藏的 HTTP 層安全缺口（#721），拉高了「已知風險」的透明度——這是好事，不是退步。

---

## 其他現實考量

| 項目 | 狀態 |
|------|------|
| 文件（開發手冊 + CHANGELOG） | 持續維護中；本批次已修正多處 drift（手冊版本、e2e 標記、交叉引用） |
| 第三方安全審計 | 無（但有連續對抗式 + cross-vendor 自審） |
| 性能 baseline | 缺公開 SLO；有 evidence-based micro-benchmark（Benchmarks 專案） |
| K8s 官方部署案例 | 缺 |
| Distributed cache / session 文件 | 缺 |
| Build 警告 | ~200–460（多為 nullable CS8632/CS8602、XML-doc cref 等風格警告，非錯誤；Mvc nullable 漸進中，#718） |
| Open source 社群 | 小，主要溝通在 Gitea issues |
| 主流商業支援 | 無 SLA、無付費 support 管道 |
| File cleanup（Add 路徑孤兒檔案） | #815 修 `DeletedFileIds` 任意檔案刪除漏洞——最終在寫入端（`BaseCRUDVM.DoAdd/DoEdit/DoDelete` 系列）kill 掉 primitive 本身：posted 的 `FileAttachment` FK 若在呼叫者自己的 tenant scope（query filter 強制開啟，不受 `EnforceTenantFileScope` 影響）下無法解析，直接在 `SaveChanges` 前被拒絕/還原，讀跟刪都連帶被擋。`DeleteFileTenantScoped`（sink 端 tenant-scoped 解析）與 entity-reference 檢查保留為第二層防禦。代價是 `DoAdd`/`DoAddAsync` 路徑上 `DeletedFileIds` 完全失效（新增中的一列沒有可驗證的既存 FK 參照）——「上傳後、儲存前取消」的檔案會變成永久孤兒 `FileAttachment` 列 + 實體 blob，無框架清理機制。已立案 #822（reaper，非 P0，僅儲存空間浪費）。 |
| #815 tenant-scoping 的已知邊界（單一租戶部署） | tenant-scoped 檢查比對 `FileAttachment.TenantCode == DC.TenantCode`；未啟用多租戶時兩邊通常都是 `null`，比對永遠成立、等於沒有隔離。也就是說 #815 這輪修的是「任意租戶」的洞；在單一租戶部署裡剩下的是「任一使用者可刪除/引用任何其他使用者的檔案」，這條 tenant scoping 從未處理過，仍是 open（`FileAttachment` 本來就沒有 owner 欄位，需要另外的 per-caller 授權機制，如 #814/#811 系列）。 |
| #815 NULL-tenant 檔案的第二個窄化 | tenant-scoped 檢查把 NULL tenant 當成「只有 tenant 也是 NULL 的呼叫者」才能解析——這代表已啟用多租戶的使用者，如果自己的記錄合法引用了一個 NULL-tenant（例如遷移前、main-host 上傳、或多租戶啟用前留下）的舊檔案，現在再也無法透過 `DeletedFileIds` 清掉它了（write-time 的 `RejectUnresolvableFileAttachmentReferences` 與 sink 端的 `DeleteFileTenantScoped` 都會判定「不可解析」而擋下）。**決定**：維持現狀（NULL-tenant 檔案不視為對所有租戶開放），因為把它放寬等於替 #815 想關的同一類 primitive（任意租戶可觸及不屬於自己的檔案）開一個新的、範圍更窄但機制相同的後門——按本專案 Compatibility → Security → Quality 的優先順序，安全的一側勝出。代價是這批舊資料的孤兒清理需要另外的管理員工具或一次性 migration，未來若要放寬，應該用一個顯式的 opt-in 設定而非預設行為改變。同一條規則（unconditional tenant-scoped resolution）也適用於下一列的 `ISubFile` 集合子項。 |
| #815 第三輪——`ISubFile` 集合的寫入端補洞 | `RejectUnresolvableFileAttachmentReferences` 原本只檢查 `TModel` 自身的純量 `FileAttachment` 屬性；`List<T>`（`T : ISubFile`，例如 `Product.Attachments`）集合完全沒有寫入端驗證——攻擊者可用 `Attachments = [ new ProductAttachment { FileId = <victim GUID> } ]` 直接寫入未經檢查的 FK，且這條路徑連 `DeletedFileIds` 的 entity-reference 第二層防禦都沒有（`GetOwnFileIds` 從未涵蓋集合屬性）。修法：在同一個方法裡新增對 `IEnumerable<ISubFile>` 屬性的走訪，套用與純量欄位相同的 tenant-scoped 解析。`DoRealDelete`/`DoRealDeleteAsync`/`DoBatchDelete`/`DoBatchDeleteAsync` 對此集合的刪除早在 #815 second rework 就已經是 tenant-scoped（見上面兩列），只是先前沒有測試透過集合路徑實際驗證過；本輪同時補上這批涵蓋測試。 |
| #815 第四輪——不寫入無效 FK；批次解析 + async | Code review（同 vendor Claude + cross-vendor gpt-5.6-sol）指出第三輪把無法解析的子項 `FileId` 清空為 `Guid.Empty` 這件事本身就是個 bug：`ISubFile` 強制非 nullable `Guid FileId`，且 `DataContext.OnModelCreating` 對 `ISubFile` 型別是 `continue`（維持 EF 預設 convention），所以真正的 FK constraint 一定存在——`Guid.Empty` 在任何有 FK 檢查的資料庫上都不對應任何列。結果第三輪自稱的「controlled rejection」在真實 DB 上其實是 `DoEdit` 整筆（含合法的 scalar 變更）靜默 rollback、`DoAdd` 丟未捕捉的 `DbUpdateException`。修法：改成把無法解析的子項從**已 posted 的集合本身**移除（透過 `IList.Remove`，`Utils.CheckDifference`/`toadd` 迴圈或 cascade-add 因此永遠看不到它），而不是覆寫欄位值；非 `IList`（例如固定長度陣列）的少見情況才退回清空整個屬性（fail closed，優先順序：Compatibility → **Security** → Quality）。同時把 `IsFileAttachmentResolvableForCaller`（每個候選 id 各發一次同步查詢）拆成 `CollectFileAttachmentCandidates`（純記憶體，收集所有候選 id）+ `ResolveFileAttachmentIdsForCaller`/`ResolveFileAttachmentIdsForCallerAsync`（單一批次查詢）+ `ApplyFileAttachmentResolution`（套用結果），並讓 `DoAddPrepareAsync`/`DoEditPrepareAsync`（`DoAddAsync`/`DoEditAsync`/`DoDeleteAsync` 呼叫）改走 await 版本，不再是 async 請求路徑上的 sync-over-async N+1（#128 同款 ThreadPool-starvation 教訓）。三個第三輪寫在 EF InMemory 上的 write-gate 測試移到 `DoEditFkWriteGateSubFileSqliteTests815.cs`（FK-enforcing 的 SQLite `ProductSubFileContext` fixture）——見 `.claude/rules/testing.md`「EF InMemory limits」一節，現在明確記載這次事故。 |
| #823（追蹤中，非本輪範圍） | `_FrameworkController.UpdateModelProperty`（`#797` 刻意讓它跳過 `DoEditPrepare`，因此繞過本頁 `RejectUnresolvableFileAttachmentReferences` 這個寫入端閘門）是另一個可由呼叫者指名任意 VM 型別＋欄位的寫入端點，缺 VM 型別層授權。不在本輪修復範圍，已擴大 #823 追蹤，之後不應該用「繞回 DoEditPrepare」的方式修，因為那會違反 #797 的既有設計決策。 |
| #824（原始缺口，已由 Part 2 的 `SaveChanges` 邊界 guard 關閉——見下方兩列） | `BasePagedListVM.UpdateEntityList`、`BaseBatchVM.DoBatchEdit`/`Async`、`BaseImportVM.BatchSaveData` 的 Excel 對應、grandchild `IEnumerable<ISubFile>`、直接 `DbSet` 存檔等 FK-WRITE 路徑，曾經可以寫入呼叫者無法解析的 `FileAttachment`/`ISubFile.FileId`（#815 第一輪 code review 用 `DoBatchEdit` 實測證實）。Issue #824 建議的修法是在 `EmptyContext.SaveChanges`/`SaveChangesAsync` 這個所有寫入路徑共同的邊界＋EF relationship metadata 統一擋，而不是逐個 sink 加閘門。PR #849 先在 `UpdateModelProperty` 這一個 sink 補了同精神的單點防禦（見下一列，Part 1）；**架構性的邊界方案本身已在 Part 2（見下方）實作並合併**，涵蓋本列列出的全部五條剩餘路徑。 |
| #824 Part 1 —— `UpdateModelProperty` sink gate（已合併，PR #849） | `_FrameworkController.UpdateModelProperty`（`[AllRights]`，`#797` 讓它跳過 `DoEditPrepare`）原本對 FK 型別欄位沒有任何驗證，任何呼叫者可把欄位設成任一租戶的 `FileAttachment` GUID。已加上與 #815 `RejectUnresolvableFileAttachmentReferences` 同精神的判定——`DCExtension.IsFileAttachmentForeignKeyProperty`（EF relationship metadata 驅動，不硬編欄位名稱），不分租戶一律拒絕寫入。**這只補了資料完整性，不是新關掉一條可利用的讀取或刪除鏈**：寫入本身沒有授予新的讀取能力（`GetFile` 預設 `EnforceTenantFileScope=false` 時走 `IgnoreQueryFilters()`，本來就能用已知 GUID 跨租戶讀取，這件事跟 FK 有沒有被偽造無關）；也沒有重開 #815 的刪除面——`DoAdd`/`DoEdit` 系列的孤兒清理只在 client 實際 POST `DeletedFileIds` 時才跑（不是自動觸發），且經過 `DeleteFileTenantScoped` → `DeleteFileCore(..., enforceTenantScope: true)` 的 tenant-scoped 解析（`FileAttachment` 實作 `ITenant`，全域 query filter 生效），偽造的跨租戶 FK 在該路徑上解析不到、等於 no-op。價值在於：擋掉一筆本來就不該落地的未授權跨租戶參照，並讓這個 sink 補齊 #815 已經在 Add/Edit VM 路徑上維持的同一條 same-tenant-FK 不變式。**Part 1 用到的共用判定 `DCExtension.IsFileAttachmentForeignKeyProperty` 當時有一個未發現的洞（見下方 Part 2 Finding 1）：exact-type 比對漏掉「principal 是 `FileAttachment` 衍生子類別」這個形狀，導致這個已合併的 gate 本身，從它上線那天起，就沒擋住指向衍生子類別的偽造 FK；已在 Part 2 一併修正，同一個判定式，兩個消費端同時受益。** |
| #824 Part 2 —— `EmptyContext.SaveChanges`/`SaveChangesAsync` 邊界 guard，涵蓋剩餘五條 sink＋補 Part 1 判定式的洞（已合併） | **設計**：`FileAttachmentSaveChangesGuard.Guard`/`GuardAsync`，掛在 `EmptyContext` 四個 `SaveChanges`/`SaveChangesAsync` override 的最前面（`ApplyAuditFields` 之前），而不是 `SaveChangesInterceptor`——WTM 常見的 `EmptyContext` 衍生類別是靠反射建構（`CS.CreateDC()` → `ConstructorInfo.Invoke`，`CS.cs:105-121`），完全沒有 DI container，一個靠 DI 註冊的 interceptor 對這些 context 會靜默地 fail open（沒有任何錯誤訊息）。`OnConfiguring` 是唯一會被下游 override 的掛鉤，但下游常見寫法是整個覆寫、不呼叫 `base`——把 guard 放在那裡等於讓它可以被一行 override 悄悄拿掉，app 表面上照樣正常運作。改成覆寫 `SaveChanges` 卻不呼叫 `base` 會讓整個持久化壞掉，是明顯、立刻會被發現的失敗，不是靜默繞過。**機制**：兩層 cheap-exit（第一層：per-model 的 FK map 為空就不掃 `ChangeTracker.Entries()`；第二層：map 非空但沒有任何候選 `Added`/`Modified`-`IsModified` 的 FK 就不發查詢）；`entry.Property(fk).IsModified` 而非 `OriginalValue == CurrentValue` 比對（WTM 的 Edit 是 detached-attach，`EmptyContext.UpdateEntity` 把整個 entity 設成 `Modified`，EF 會把 CurrentValues 複製進 OriginalValues，「沒變」這個判定在攻擊路徑上剛好恆真，會 fail open）；同一個 unit of work 裡狀態是 `Added` 的 `FileAttachment` id 直接信任、不查資料庫（保證會被 INSERT、因此會過 PK 檢查；`Unchanged`/`Modified` 的 attachment entry 不算——手動 `Attach` 不會 INSERT、不會過 PK 檢查，是唯一真正的冒充路徑）；解析查詢批次化（500 一批，`DCExtension.ResolveFileAttachmentIds`/`Async`——現在與 `BaseCRUDVM.ResolveFileAttachmentIdsForCaller`/`Async` 共用同一份實作，不再各自維護一份會漂移的版本），保留 `ITenant` query filter（不下 `IgnoreQueryFilters()`）；解析查詢本身失敗＝整筆拒絕、絕不縮小範圍。**拒絕形狀**：`UnresolvableFileAttachmentReferenceException`（`InvalidOperationException`，刻意不繼承 `DbUpdateException`——既有的 catch 邏輯會把 `DbUpdateException` 當成暫時性 store 故障處理，不能讓一個安全決策被誤判成那種東西），訊息只說「在目前 scope 下無法解析」，不透露該 id 是否存在於別的租戶。**Kill switch**：`FileAttachmentSaveChangesGuard.Enabled`，純 static bool（不是 `IOptions<T>`——反射建構的 context 沒有 DI 可以餵）、預設 `true`。**不加「已驗證過，跳過」旗標**——那樣會把已經關掉五輪的繞過面重新打開。 |
| #824 Part 2——cross-vendor review 的四項發現與處置（範圍窄化、測試證據） | **Finding 1（HIGH，已修，且不是 Part 2 引入的洞）**：`DCExtension.IsFileAttachmentForeignKeyProperty`（Part 1、Part 2 共用的那一個判定式）比對 `fk.PrincipalEntityType.ClrType` 用的是 exact-type equality。EF Core 支援 principal 是 `FileAttachment` **衍生子類別**的關聯（TPH 或 TPT，例如 `SignedFile : FileAttachment`，`Invoice.SignedFileId -> SignedFile`）；exact-type 比對下，這個 FK 對判定式來說「不是」attachment FK，於是整條路徑（Part 1 的 `UpdateModelProperty` gate、Part 2 的 SaveChanges guard）都不會檢查它，資料庫本身的 FK constraint 又剛好被跨租戶 `SignedFile` 的 base-table `FileAttachment.ID` 滿足，寫入照樣落地。改成 `Type.IsAssignableFrom`；新增 TPH 與 TPT 兩種 mapping strategy 的迴歸測試（`FileAttachmentForeignKeyPredicateDerivedPrincipalTests824.cs`），對舊判定式 bisect（git stash 該檔案改動、重跑）確認兩支測試都先紅後綠。判定式的文件註解原本宣稱「自動涵蓋所有 downstream-defined attachment FK，不需任何改動」——對指向 `FileAttachment` 本身為真，對指向衍生子類別為假（在此修正之前），已一併改寫。**Finding 2（MEDIUM-HIGH，已記錄不變式＋已建構可重現的 mutation test，非 in-tree 生產漏洞）**：guard 在 `base.SaveChanges()` 進入前就讀完 `ChangeTracker` 狀態；EF Core 自己的 `SavingChanges`/`SavingChangesAsync` interceptor pipeline 在那之後才跑，且被設計成可以再改一次 change tracker——一個把「同一個 unit of work 裡 `Added` 的 FileAttachment」翻回 `Unchanged` 的 hook，會讓 guard 剛剛給出的信任判斷落空（該筆 INSERT 被跳過，連帶跳過 PK 檢查，但被引用的 id 其實早已存在，依賴它的 FK 照樣寫入成功）。本庫目前沒有任何生產用的 interceptor 做這件事；`FileAttachmentSaveChangesGuardInterceptorMutationTests824.cs` 用一個測試用的 `SaveChangesInterceptor` 建構出這個形狀並證明它可達（EF Core 官方支援的擴充點，不需要改 guard 本身），該測試的檔頭註解與斷言訊息都明確寫出「這是可達性證明，不是本庫既有漏洞的宣稱」。不變式（「掛在 `EmptyContext` 的 SaveChanges hook 不得在 guard 跑完後修改 attachment entry 的狀態或 FK 純量值」）記在 `FileAttachmentSaveChangesGuard` 類別本身的文件註解裡。**Finding 3（MEDIUM，範圍窄化已寫入程式碼與文件）**：guard 涵蓋的是「每一筆經過 `EmptyContext` 衍生 context 自己的 `SaveChanges`/`SaveChangesAsync` 的 EF-tracked 寫入」，不是「每一條寫入路徑」——`IDataContext.cs`（`:18`/`:31`）本身就記載外部可以自行實作這個介面；`CS.Cis`/`CS.CisFull` 掃描的是任何 `DbContext` 建構子，不限 `EmptyContext` 子類別；`WalkingTec.Mvvm.WorkFlow.ServiceCollectionExtensions.ResolveDataContext` 的 fallback 分支會回傳 DI 註冊的任意 `IDataContext`，同樣不保證是 `EmptyContext` 衍生。這條窄化陳述同時寫在 `FileAttachmentSaveChangesGuard` 類別文件與 CHANGELOG 的 Migration 段落。**Finding 4（LOW，已修正 PASS 理由＋已補測試）**：本文件與 commit 曾經誤稱 demo seeding 不會產生非 null `PhotoId`（`TypeExtension.cs:479/484` 其實會把 `$fk$` 值寫進去、`DataContext.cs`(demo)`:148/155` 的 `SetTestData` 會遞迴建立 principal 再填 ID，`School.cs:65` 的 `Photo` 導覽屬性就是這個形狀）——demo seeding 確實會跑到 guard 的解析查詢分支，只是**結果仍然 PASS**（seeding 用的是同一個 unit of work、同一個 tenant，不是跨租戶偽造），先前「不會產生非 null PhotoId」這個 PASS 理由本身是錯的，已改寫。13-pattern 列舉當時也漏掉「直接 `DbSet.Add`/`Update` 寫入」這個 sink（判定式自己的文件註解 `:127` 早就把它列為已知的 #824 sink）；已補上 `DirectDbSetAdd_SameTenantLegitimateFileFK_Persists`（合法同租戶直接寫入必須 PASS 的正控組）。 |
| #824 Part 2——測試證據與涵蓋範圍 | **迴歸測試**：issue 點名的六條 sink 各一支，全部在有真實 FK 檢查的 SQLite fixture 上（`FileAttachmentSaveChangesGuardBypassPathTests824.cs`）——`BasePagedListVM.UpdateEntityList`、`BaseBatchVM.DoBatchEdit`/`DoBatchEditAsync`、`BaseImportVM.BatchSaveData` 的 Excel 欄位對應、grandchild `IEnumerable<ISubFile>`（`GrandchildOrder824.Batches[].Attachments[]`，比 `CollectFileAttachmentCandidates` 只掃 `TModel` 自身一層的既有邊界深兩層）、`_FrameworkController.UpdateModelProperty` 內部實際呼叫的 `IDataContext.UpdateProperty` 原語本身（獨立於該 controller 自己的欄位層前置檢查，證明防禦縱深）、直接 `DbSet.Add` 寫入。對 guard 尚未接進 `EmptyContext.SaveChanges` 的樹（`git stash` 該段改動）重跑這 8 支「必須拒絕」測試，全部先紅（偽造的跨租戶 FK 落地、「不應該落地」的斷言失敗）後綠。**必過（must-not-reject）控制組**：同租戶合法直接寫入必須正常持久化；同一個 unit of work 建立的 attachment 不查資料庫直接信任；kill switch（`Enabled = false`）會重新打開被關掉的洞，用來文件化「關掉這個開關等於失去什麼」。**套件數字**：`test/WalkingTec.Mvvm.Core.Test` 5001 → 5015（Finding 1 三支 + guard 十一支），0 failed；`test/WalkingTec.Mvvm.Api.Test` 102 passed + 1 skipped，與既有 baseline 完全一致，未受影響。九個既有的 #815/#828/#875 測試檔中，**七個**只改了一行左右——它們的 seed helper 原本用沒設定租戶（或租戶設錯）的 context 去插入引用特定租戶 `FileAttachment` 的資料列，這在 guard 出現之前無關緊要，現在會在 seed 階段本身被同一個 guard 正確擋下；修法是把 seed context 的租戶對齊被引用檔案的租戶（任何真正的呼叫者做同一件合法插入本來就需要這樣做），不是放寬 guard。**其餘兩個不是一行**：`DoAddEditFkWriteGateScalarSqliteTests815.cs` 的兩支測試除了 seed 端的租戶修正之外，還額外把編輯時的租戶從 `"TENANT_ATTACKER"` 改成 `"TENANT_EDITOR"`（對齊既有種子資料自己的命名）——當時（PR #978 合併前）的理由是：#815 的 restore-in-place 機制會把子項目寫回**真實的**舊 `FileId`，而 guard 那時對任何 `IsModified` 候選一律重新驗證，不論值有沒有變，用「編輯者自己的租戶」重新驗證這個被還原的值會讓 `TENANT_ATTACKER` 版本失敗。**這條理由後來被同一個 issue 自己的後續修法推翻，而非本列描述有誤**：adversarial review Finding 4（見下方新增列）把「posted 值等於目前已持久化的值」的候選整個排除在重新驗證之外，不分租戶——這兩支測試改用 `TENANT_EDITOR` 這件事本身不需要撤銷（沒有壞處，仍然是合法情境），但「不改成 `TENANT_ATTACKER` 就會被擋」這個舊理由，在 Finding 4 之後已經不成立，並已用一支新測試（`DoEdit_MixedRepost_ExistingChildSameIdForgedFileId_DifferentTenantEditor_RestoredValueNotRevalidated`）實測驗證：`TENANT_ATTACKER` 版本現在同樣會成功，不再被拒絕。 |
| #824 Part 2——PR #978 CI 紅燈與 `Sys.EditFailed` 決策（已合併） | Part 2 上線後 PR #978 的 CI 轉紅：兩個**既有**的 mutant（`mvc824-fileattachment-fk-guard-http`、`mvc824-fileattachment-fk-guard-direct-crud-kill`，兩者都針對 Part 1 `UpdateModelProperty` 的欄位層閘門）從 `KILLED` 變成 `UNEXPECTED_RED`——不是 SURVIVED（mutant 沒被抓到），而是連**沒被 mutate 的 baseline** 都紅：中和 Part 1 閘門後，Part 2 的 `FileAttachmentSaveChangesGuard` 邊界照樣擋下同一筆偽造寫入，只是拒絕訊息從 `UpdateModelProperty` 欄位層的專屬文字，退化成 `_FrameworkController.UpdateModelProperty` 舊有的通用 `catch { ... Sys.EditFailed }`。**這證明防禦縱深確實存在**（Part 1 被中和後 Part 2 接住），但也代表舊 mutant 的斷言（比對 Part-1 專屬文字）不再能單獨隔離 Part 1 的貢獻。**決策點（先決定行為，再處理 mutant）**：`Sys.EditFailed` 這個通用訊息是否可接受地代表這個邊界拒絕？裁定**不可接受**——`Sys.EditFailed` 跟任何其他編輯失敗（真的 DB 錯誤、逾時……）無法區分，是這個 codebase 已經踩過的「錯誤狀態塌縮成呼叫端分不出來的值」同一種形狀，會讓一個安全性拒絕在維運上跟雜訊沒兩樣。修法：`UpdateModelProperty` 新增一個**專屬** `catch (UnresolvableFileAttachmentReferenceException)` 子句（夾在既有的 `DbUpdateConcurrencyException` catch 與通用 catch 之間），訊息文字刻意跟欄位層閘門的既有文字（"FileAttachment foreign key"）相近，讓呼叫端**分不出**這次拒絕是來自欄位層的無條件封鎖、還是來自 SaveChanges 層的 backstop——區分這兩者本身就是一種洩漏訊號；且刻意寫死字串、不透過 `Wtm.Localizer`（見下方 Finding 8 的 localizer bug 說明）。**Mutant 重新對焦，而非放寬斷言**：教練組明確要求「不要把斷言放寬到會連帶命中一個無關的 400」——因此沒有改鬆比對文字，而是新增 `UpdateModelProperty_FileAttachmentForeignKey_Part1GateAlone_RejectedWithBoundaryGuardDisabled`（`FileAttachmentSaveChangesGuardTests824.cs`／`FrameworkControllerUpdateModelPropertyPersistTests.cs` 各一支），用 `FileAttachmentSaveChangesGuard.Enabled = false` 在測試範圍內關掉 Part 2，重新把 Part 1 單獨隔離出來；兩個既有 mutant 的 `red_test`/`red_expected_assertion_patterns` 改指向這支新測試，並用 **Release** build 重新跑兩次確認 `VERDICT: KILLED` 穩定（非跑一次就信）。**其他繞過路徑是否有同樣的塌縮**：逐一檢查 `DoBatchEdit`/`DoBatchEditAsync`（`BaseBatchVM.cs`）與 `BaseImportVM.BatchSaveData`——兩者都 catch 住 `UnresolvableFileAttachmentReferenceException` 後呼叫 `SetExceptionMessage(e, id: null)`，其實作是 `if (id != null) {...}`，`id` 為 `null` 時整個訊息被**直接丟棄**，連 `Sys.EditFailed` 等級的通用文字都沒有——比 `UpdateModelProperty` 更差：不是「看起來像其他失敗」，是「呼叫端與伺服器端都看不到任何訊號」。這個發現直接併入下方 Finding 3（MUST FIX）的範圍，用一支處理程序層級的節流 log（而非訊息文字）補上訊號，因為訊息層本身的架構限制（`id: null` 就丟棄）不值得為這一個安全事件另開一條特例訊息路徑。 |
| #824 Part 2——adversarial review of PR #978 的 8 項發現與處置（已合併） | 獨立於前面 cross-vendor review 四項發現之外的**第二輪**審查，針對 Part 2 合併後的完整實作。**MUST FIX 1（guard 自建 FK 判定式的第二份拷貝）**：`FileAttachmentSaveChangesGuard.BuildMap` 原本自己內嵌 `!typeof(FileAttachment).IsAssignableFrom(...)`，跟 `DCExtension.IsFileAttachmentForeignKeyProperty` 用的是**兩份獨立**但語意上該永遠一致的比對——Finding 1（Part 1/Part 2 共用判定式那次）修的是後者，從未真正傳到 guard 自己這份地圖建構邏輯，衍生 principal 的迴歸測試因此只驗過判定式本身、沒驗過 guard 實際使用的路徑。修法：抽出共用 `DCExtension.IsFileAttachmentPrincipal(Type?)`，`BuildMap` 與 `IsFileAttachmentForeignKeyProperty` 都呼叫它；新增 `FileAttachmentSaveChangesGuardDerivedPrincipalTests824`（TPH/TPT 各一支端對端 guard 測試，直接呼叫 `SaveChanges()`，而非只測判定式）；`dcext824-derived-principal-neutralize` 的 mutant patch 重新對準這個共用方法本身，新增 `dcext824-derived-principal-neutralize-guard-kill` 從 guard 這一側再驗一次同一個突變會被抓到。**MUST FIX 2（guard 每次 SaveChanges 跑兩次）**：`DbContext.SaveChanges()`（無參數）原始碼本身是 `=> SaveChanges(true)`——一次**沒有 base 限定**的一般虛擬呼叫，因為 `EmptyContext` 覆寫了 `SaveChanges(bool)`，這次呼叫會走虛擬分派**繞回** `EmptyContext` 自己的覆寫，不是直接落到 `DbContext` 的 bool-arg 實作。原本 guard 同時掛在無參數與 bool-arg 兩個 override 上，等於每次無參數呼叫的 `SaveChanges` 都跑了兩次 guard（含批次解析查詢）。修法：guard 呼叫從兩個無參數 override（`SaveChanges()`/`SaveChangesAsync(CancellationToken)`）移除，只留在 `SaveChanges(bool)`/`SaveChangesAsync(bool, CancellationToken)`——所有呼叫路徑（不論有沒有參數）最終都恰好經過這兩者一次。`FileAttachmentSaveChangesGuardInvocationCountTests824.cs` 用 `DbCommandInterceptor` 直接數解析查詢的 SELECT 次數，證明無參數呼叫下降到恰好一次，且仍然正確拒絕偽造寫入（防止「查詢次數對了但保護消失了」這種偽陽性）。**MUST FIX 3（拒絕沒有任何 log）**：兩個 throw 站點原本都沒有留下任何伺服器端訊號；`BaseBatchVM.DoBatchEdit`/`DoBatchDelete` 的 `SetExceptionMessage(e, id: null)` 更是直接把訊息整個丟棄（見上一列）。修法比照 `DCExtension.ApplyDataPrivilegeForAnalysis`（#843）的既有先例：`CoreProgram.GetLogger(...).LogWarning`，用 `ConcurrentDictionary<string, byte>` 依 (entity type, property) 節流成每個欄位每個 process 只記一次（避免持續攻擊或解析查詢中斷把 log 灌爆），但第一次出現（triage 最需要的那一次）一定會被記下——一個記拒絕本身，一個記解析查詢失敗（用不同文字區分，"resolution query" 字樣），`FileAttachmentSaveChangesGuardLoggingTests824.cs` 三支測試涵蓋兩種 log 各記一次、重複拒絕只記一次。**MUST FIX OR HONESTLY NARROW 4（相容性影響比文件宣稱的廣，SQL 沒涵蓋）**：guard 原本對任何 `IsModified` 候選一律重新驗證，不論 posted 值跟目前已持久化的值是否相同——WTM 的 Edit 是 detached-attach（`EmptyContext.UpdateEntity` 把整個 entity 設成 `Modified`，EF 因此把每個純量屬性都標成 `IsModified = true`，不論這次請求有沒有真的碰過它），這個「沒變也重新驗」的規則因而對兩種情境**過度拒絕**：(a) 非 `ITenant` 的列，其 FK 本來就合法指向別的租戶的檔案（列本身就是跨租戶共用設計，#824 的威脅模型是「新寫入」引入跨租戶參照，不是「既有列」跨租戶）；(b) FK 指向 null-tenant 檔案（#859 記載的、mainhost/pre-multi-tenancy 上傳的既有合法情境），被非 null 租戶的真實呼叫者原樣重貼。**推理，不只是結論**：這正是 #815 自己已經確立的先例（`BaseCRUDVM.ApplyFileAttachmentResolution` 早就把無法解析的 posted 值還原成 entity 自己編輯前的既有值，而非整請求拒絕，只要存在合法的既有值）——只是那個先例先前只套用在單一 VM 層的子項目集合，guard 自己在 SaveChanges 邊界的比對邏輯沒有沿用同一條規則，變成「同一個安全模型在兩個層各自對『沒變』有不同答案」的不一致，而非單純的相容性收斂。修法：把同一條先例**泛化**到這個邊界層——`entry.GetDatabaseValues()`（EF Core 自己讀取目前已持久化欄位值的機制，獨立於、且比 change tracker 自己不可信的 `OriginalValue` 更可信）取得該筆 row 目前實際存的值，posted 值等於它就整個跳過（連解析查詢都不發），值不同（真正的偽造、也包含任何合法異動）照舊完整驗證；`GetDatabaseValues()` 回傳 `null`（列已不存在）時**不**套用這個優化、直接落到正常驗證，fail-closed 而非靜默跳過。**代價老實記錄**：每個「至少有一個候選 FK 真的 IsModified」的 Modified entity 多一次 `GetDatabaseValues`(`Async`) 往返，不跨 entity 批次；`FileAttachmentSaveChangesGuardBypassPathTests824.cs` 新增三支：兩支正控組（非租戶列未變 FK／null-tenant 未變 FK 均須成功）、一支非回歸控制組（用既有合法值改成偽造值，必須仍被拒絕，且刻意繞過 #815 自己 `DoEdit` 前置閘門走 `UpdateProperty` 原語，因為 `DoEdit` 對 optional FK 無合法既有值時會把偽造值靜默清成 `null`，不會讓真正變動的偽造值到達這一層——用這條路徑才能證明 Finding 4 的窄化本身不會反過來放行真正的偽造）；**這個修法也讓上方 Part 2 測試證據列描述的 `TENANT_ATTACKER`→`TENANT_EDITOR` 那條理由過時**（見該列的更正說明與新測試）。**NARROWER 5（cancellation 被洗成安全性例外，已修）**：`DCExtension.FileAttachmentResolution.cs` 的 `ResolveFileAttachmentIds`/`Async` 原本用裸 `catch (Exception ex)` 包住解析查詢——呼叫端取消（或逾時驅動）的 `OperationCanceledException` 因此被轉換成 `FileAttachmentResolutionOutcome.Succeeded == false`，guard 再把它拋成 `UnresolvableFileAttachmentReferenceException`，讓一次普通的取消看起來像一次被拒絕的偽造 FK。修法：兩個方法都在通用 catch 之前先 `catch (OperationCanceledException) { throw; }`，讓取消原樣傳播，其餘所有解析失敗（逾時、斷線……）仍照 #828 先例整筆拒絕。**相關但選擇揭露、不修**：若下游 context 只映射了某個衍生 SignedFile 子類別、卻沒映射 base 的 `FileAttachment` 本身，`Set<FileAttachment>()` 在 LINQ 轉譯階段就會拋例外，剩下的裸 catch 一樣會把它轉成同一種安全性拒絕，且會對該 model 的**每一筆**後續存檔永久生效——這需要一種本庫目前完全沒在用的非標準 EF 設定，範圍窄化再處理這個特例被判斷為與本次修法規模不成比例，因此揭露而非修正。**NARROWER 6（TPC defeats same-unit-of-work 信任例外，已修）**：「同一個 unit of work 裡 `Added` 的 FileAttachment id 直接信任、不查資料庫」這個例外，原本只用一個扁平 `HashSet<Guid>` 判斷「id 有沒有出現在本次 Added 的 attachment 集合裡」，沒有比對**型別**。TPH／TPT 下這個信任沒問題——整個繼承階層共用（或以共享 PK 連結）同一張實體表，一個帶著真實既存衍生列 id 的 `FileAttachment` 殼物件 Added 進去會在 INSERT 時撞 PK 違例。TPC（Table-Per-Concrete-Type）下每個具象型別各自獨立的表與 PK 空間，讓這個「會被 INSERT、因此會過 PK 檢查」的論證失效——base 型別的殼物件插進去的是完全不同、無關的表，不會跟衍生型別自己的表發生任何碰撞，而依賴該衍生型別的 FK 卻早已被受害者既存的真實列滿足。修法：新增 `FkAttachmentInfo.PrincipalClrType`，「同一個 unit of work」的信任例外現在額外要求 `candidate.PrincipalClrType.IsAssignableFrom(addedType)`——base 型別殼物件不再滿足衍生 principal 的要求，落回正常的租戶範圍解析查詢。`FileAttachmentSaveChangesGuardTpcSameUnitOfWorkTests824.cs` 兩支（攻擊須被拒絕／合法同型別 Added 須被信任）。**NARROWER 7（非 Guid FK 被辨識卻靜默跳過，已修——用 log 揭露而非完整支援）**：`BuildMap` 純粹用關聯**形狀**（principal 是 `FileAttachment` 或其衍生型別）辨識候選 FK，不管 FK 屬性自己的 CLR 型別；本庫目前出貨的每個 `FileAttachment` 衍生型別都繼承 `TopBasePoco.ID`（`Guid`，非 virtual），慣例辨識出的 FK 因此實務上永遠是 Guid 型別——唯一的例外是透過 `HasForeignKey(...).HasPrincipalKey(x => x.某個非 Guid 替代鍵)` 明確設定的關聯，本庫目前沒有任何地方這樣用，但這是合法的 EF Core 用法。修法前，這種欄位會被加進地圖，然後被只認 Guid 的 `ExtractGuid` 每次都靜默回傳 `null`——不會產生任何候選、guard 完全不會查詢、也不會拒絕，卻沒有任何訊號說明這個欄位存在且不受保護。完整支援任意替代鍵型別，對一個整個設計就是「一次批次化 Guid 鍵解析查詢」的邊界 guard 而言不成比例；改為把非 Guid 匹配排除在地圖之外（行為不變，仍然不受保護），但用跟 Finding 3 相同的節流 pattern 記一次 `LogWarning`（`LogNonGuidAttachmentFk`），一次性揭露「這個欄位存在、辨識得到、但無法強制」。三支新測試（`FileAttachmentSaveChangesGuardNonGuidFkTests824.cs`）：log 恰好一次、跨兩次 SaveChanges 節流成一次、以及具體的安全性重點——呼叫者可以貼別的租戶一個真實存在的 FileName 進這個欄位，照樣落地不受阻擋（資料庫層的真實 FK constraint 仍會擋掉完全不存在的值，referential integrity 沒丟，丟的是租戶範圍檢查）。**NARROWER 8（文件/註解準確性，部分修正）**：(a) `DoAddEditFkWriteGateScalarSqliteTests815.cs` 一段舊註解宣稱「跨租戶編輯者無法走到這個路徑，會被一個新的、更廣的 #824 拒絕擋下，另外驗證」——這條claim 描述的是 Finding 4 之前的行為，Finding 4 之後已經不成立；已改寫該註解並新增 `DoEdit_MixedRepost_ExistingChildSameIdForgedFileId_DifferentTenantEditor_RestoredValueNotRevalidated` 實測（先用暫時停用 Finding 4 的比對重跑一次確認先紅，再驗證修好後真的綠）。(b) CHANGELOG 原本宣稱「九個既有測試檔各改一行左右」——與本文件同一列一直以來的說法（七個一行、兩個不只一行）不一致，已改成與本文件一致的措辭。(c) `patches/dcext824-derived-principal-neutralize.patch` 這一份 patch 檔被兩個 entry 共用（`dcext824-derived-principal-neutralize` 與新增的 `dcext824-derived-principal-neutralize-guard-kill`），但 patch 檔內嵌的程式碼註解只寫了前者的 id（`// MUTANT test/mutants dcext824-derived-principal-neutralize: ...`），對從後者這個 entry 看過去的人是一個會誤導的命名殘留；純粹是 patch 檔內部的說明文字，不影響它實際套用的位置或 `run_mutant.py` 的比對邏輯（比對用的是 entry JSON 的 `patch` 欄位路徑，不是 patch 內的註解文字），記錄但判斷不值得為此另開修法。 |
| #824 Part 2——mutant patch 被自己後續的編輯打壞（已修，且是一次流程教訓） | 上一列的 8 項發現全部處理完、推上 PR #978 後，CI 又轉紅一次：`git apply --check` 對 `fileattachmentguard824-reject-condition-neutralize.patch` 失敗在 `FileAttachmentSaveChangesGuard.cs:264`，回報 `INVALID_MUTANT_PATCH_DID_NOT_APPLY`。根因：這是**本 PR 自己**在更早的 commit 新增的 mutant，錨定在 `Guard(EmptyContext)` 同步路徑「拒絕」判斷式那一行；同一個 PR 後續的 Finding 1 修法（抽出共用 `IsFileAttachmentPrincipal`）與 Finding 3 修法（拒絕前插入 `LogRejection(candidate)`）都動了同一個檔案，把這行的行號與周圍 context 全部位移——**mutant 在驗證當下是真的通過的，但驗證完之後檔案又被同一個 PR 改過，驗證結果沒有隨之重新確認就當成定論**，等同沒驗證。修法：對照原 patch 內嵌的說明文字，先確認原本中和的是哪一個行為——`if (!outcome.ResolvedIds.Contains(candidate.Id))` 這個**同步路徑的最終拒絕判斷式**本身，用 `&& false`（compile-preserving）讓它恆假，使 guard 對任何無法在呼叫者租戶範圍解析到的候選 FK 永遠不拋例外、寫入直接放行——用temp-edit（在最終已提交的原始碼上手動加回同一個 `&& false` 修改）＋`git diff`＋還原這個既有的標準流程重新產生 patch，而不是憑空重寫；新舊 patch 中和的是**同一行、同一個判斷式**，差別只是行號位移（264→563）跟多了一行不相關的後續 context（`LogRejection(candidate);` 現在也在同一個 if 區塊內）。重新產生後對照 `git apply --check` 通過，`run_mutant.py` 對最終已提交的樹跑兩次，皆為 `VERDICT: KILLED` / `GATE: PASS`，斷言訊息與原本記錄的完全一致（`Assert.ThrowsException failed. Expected exception type:<...UnresolvableFileAttachmentReferenceException> but no exception was thrown`），確認中和的仍是同一個安全行為，不是巧合套用到別的東西上就通過。**流程教訓，已內化**：mutant 驗證與「這個檔案不會再被改」不是同一件事——只要同一個 PR 之後還會再碰同一個檔案，先前的驗證就必須視為過期，動工順序改成「先做完這個檔案上所有其餘的修改，再重新產生／驗證這個檔案相關的每一個 mutant，一次跑完，不要跑完又繼續改」。 |
| #824 —— `ExecuteUpdate`/`ExecuteDelete` 繞過 `SaveChanges` 的迴歸守衛（`ExecuteUpdateDeleteFileAttachmentInvariantTests`，範圍誠實限定，#857） | `SaveChanges`/`SaveChangesAsync` 不是「所有寫入的唯一必經點」——EF Core 的 `ExecuteUpdate(Async)`/`ExecuteDelete(Async)` 直接編譯成 SQL、繞過 change tracker（也就繞過 `SaveChanges`）。2026-07-28 全庫稽核當下 `src/` 底下沒有任何 `ExecuteUpdate`/`ExecuteDelete` 呼叫點碰 `FileAttachment`，`ExecuteUpdateDeleteFileAttachmentInvariantTests.cs` 把這個事實寫成一支迴歸守衛測試——同一個原始碼**陳述式**裡若同時出現 `FileAttachment` 與 `.ExecuteUpdate`/`.ExecuteDelete`，測試就會失敗。**這是刻意選擇的字面 grep 式掃描，不是語意/別名分析，範圍有一個已知、記錄在案的洞**：把查詢先指派給區域變數、在下一個陳述式才呼叫，就不在同一句陳述式內，掃不到——`var files = DC.Set<FileAttachment>(); files.ExecuteDeleteAsync();` 這種寫法會被放過。測試本身不是恆真（`DetectionLogic_FindsADeliberateViolation_InFixtureText` 這支 sanity check 證明抓得到直接寫法的違規），但也不是完整的 sink 證明；同一份限定文字也寫在該測試檔的類別註解裡。 |
| #859 —— `_Framework/GetFile`/`ViewFile` 未認證跨租戶讀取，`EnforceTenantFileScope` 預設翻成 `true` | 上一列（#824 Part 1）記錄的「`GetFile` 預設 `EnforceTenantFileScope=false` 時走 `IgnoreQueryFilters()`，本來就能用已知 GUID 跨租戶讀取」這件事本身，加上三份 demo template 都把 `IsFilePublic` 設成 `true`（讓 `PrivilegeFilter` 連認證都不要求），組成了 #859 的完整鏈：Vue3Demo 是唯一 `IsQuickDebug:false` + `EnableTenant:true` 的 production 形狀樣板，未認證呼叫者可直接讀走任一租戶的檔案內容。修法兩半：套件半把 `FileUploadOptions.EnforceTenantFileScope` 預設翻成 `true`（`WtmFileProvider.GetFile` 改為預設honour 全域 `ITenant` query filter），並在 `FrameworkServiceExtension.UseWtmContext` 加上 `IsFilePublic==true` 非 Development 環境的 `LogCritical`（比照既有 `IsQuickDebug` 守門模式，但只 log 不 throw——`IsFilePublic` 有正當用途，可能是操作者刻意開啟）；template 半把三份 demo appsettings.json 的 `IsFilePublic` 改回 `false` 並加註說明。翻轉前的四項覆核：**(1) NULL-tenant 舊檔案**——沿用 #815 已記錄的決定（見上面「#815 NULL-tenant 檔案的第二個窄化」列）：EF 對 nullable 欄位的 `==` 轉譯是 null-safe，`TenantCode == null` 的檔案只有「呼叫者自己的 tenant 也是 null」才能解析到；這是 #815 已經在 `DeleteFileTenantScoped` 上採用的既定先例，`GetFile` 翻轉後沿用同一條規則，不另開窄化的例外——放寬等於重開 #815 關掉的同一個 primitive。**(2) 每個 `Upload(` call site 是否都蓋了 `TenantCode`**——逐一檢查 `src/`（`_FrameworkController.cs` 四處、`BaseImportVM.cs`、`WtmFileProvider.Upload`）與 `demo/`（三份 `FileApiController.cs`、`ConsoleDemo`）呼叫點：全部經過 `WtmFileProvider.Upload`（非 DB handler 分支自己 `new FileAttachment` 時蓋）或 `WtmDataBaseFileHandler.UploadToDB`（database 模式自己蓋），兩處都執行 `file.TenantCode = _wtm.LoginUserInfo?.CurrentTenant;`——沒有找到漏蓋的路徑。**(3) 背景/無身分路徑**——`GetFile(` 的呼叫點全部落在 `_FrameworkController`/demo `FileApiController`（HTTP request-scoped）與 `BaseCRUDVM`/`BaseImportVM`（同樣掛在 request-scoped 的匯入流程上）；`grep BackgroundService/IHostedService` 找到的六個背景服務（`EtlHostedService`、`DashboardSnapshotHostedService`、`DashboardAlertHostedService`、`ActionLogRetentionService`、`RefreshTokenRetentionService`、`WorkflowTimerHostedService`）沒有一個呼叫 `WtmFileProvider.GetFile`——翻轉不會讓任何現有背景路徑讀不到檔案。註：#843 談的是同一個「`LoginUserInfo==null` 時怎麼辦」根因家族，但方向相反且機制不同——`ApplyDataPrivilegeForAnalysis` 對無身分 fail-*open*（不過濾，過度可見），`GetFile` 的 tenant query filter 對無身分 fail-*closed*（只解析 `TenantCode` 也是 null 的列，看不到其他租戶）；#843 於本表下一列修復（fail-closed by default），不影響、也不被本修法影響。**(4) `HasMainHost`**——demo appsettings.json 的 `mainhost.Address` 預設整行被註解掉，`Configs.HasMainHost` 因此預設 `false`；`GetUserPhoto` 的 `HasMainHost && CurrentTenant==null` redirect 是路由層邏輯，發生在呼叫 `GetFile`之前，且兩端（上傳與讀取）在同一台 mainhost 節點上都用同一個 `LoginUserInfo?.CurrentTenant`（mainhost 自己的使用者通常也是 null tenant），翻轉後行為一致，未發現互相干擾。**結論：翻轉可以安全進行，四項覆核均未發現阻擋理由**，唯一需要記錄的行為改變是：一個真正打算跨租戶公開、但上傳時蓋了非 null `TenantCode` 的「公開」檔案，翻轉後對匿名呼叫者不再可解析（除非透過 Referer-based tenant 路由巧合命中同一租戶）——這類檔案需改用 null-tenant/main-host 情境上傳，或另建專用公開檔案儲存區。**寫驗收測試時另外發現一個相鄰但獨立的 bug**：`WtmDataBaseFileHandler.GetFileData`（`database` SaveMode 專用）自己又下了一次「不受 `EnforceTenantFileScope` 控制、永遠套用」的 tenant-scoped query，跟外層 `WtmFileProvider.GetFile` 的（依旗標決定要不要套用租戶過濾的）查詢結果不一致時——也就是 `EnforceTenantFileScope=false` 且真的跨租戶讀取時——不是洩漏也不是乾淨拒絕，而是回傳 `null` DataStream 讓 controller 端丟未捕捉的 `NullReferenceException`（表現成 HTTP 400）。`WtmLocalFileHandler`/`WtmOssFileHandler` 沒有這層多餘過濾。這使得本次修法自己文件裡承諾的「`EnforceTenantFileScope=false` 這個顯式 opt-out 仍然有效」對 `database` SaveMode 不成立，因此在本 PR 一併修掉（`GetFileData` 改用 `IgnoreQueryFilters()`，理由：全庫只有 `WtmFileProvider.GetFile` 這一個呼叫點，執行到這裡時外層授權判斷早已完成，不是新開的洞）。 |
| #843 —— `ApplyDataPrivilegeForAnalysis` 對背景執行 fail-open，改為預設 fail-closed（P1，BREAKING for 一個窄範圍） | `DCExtension.ApplyDataPrivilegeForAnalysis`（`DCExtension.cs:295-300`）在 `WTMContext.LoginUserInfo == null` 時直接跳過列級 DataPrivilege 過濾、回傳未過濾查詢。背景執行（`DashboardSnapshotJob`、`DashboardAlertHostedService`，都透過 `AnalysisWidgetDataSource.GetDataAsync` 的 `_serviceProvider.CreateScope()` 拿到沒有 `HttpContext` 的 `WTMContext`）因此永遠 `LoginUserInfo == null`——寫入時（互動式 dashboard-designer）受 `CanAccess` 與 DataPrivilege 約束的 widget 設定，背景重跑時完全不受限。**身分裁決**：評估三個選項——job 建立者身分（否決：帳號停用/權限變動後仍沿用舊權限，且需要重建一份不存在的 `LoginUserInfo` 快照，風險/工作量超出 P1 修復比例）、租戶系統身分（否決：本框架目前沒有這種帳號類型，需要新基礎設施）、**顯式宣告的系統查詢（採用）**：`LoginUserInfo == null` 預設改為 fail-closed（沿用 `AppendSelfDPWhere` 既有的 `dps==null → 1!=1` 邏輯，範圍限定在「該 model 有設定 DataPrivilege 規則」），新增 `declaredSystemQuery: true` 具名參數作為唯一、review 可見的例外通道（非設定檔旗標，對已認證呼叫者無效）。兩個生產呼叫點（`_AnalysisController`、`AnalysisWidgetDataSource`）都不傳 `true`——**立即以 fail-closed 出貨，不是預設關的 opt-in 旗標**（不重蹈 `UseSelectIslandRender`/四個 `Enforce*` 旗標的覆轍）。**隨手修的相鄰缺口**：`AnalysisWidgetDataSource` 的 `WTMContext.DC` 透過 `WTMContext.CreateDC()` 建立，其 `TenantCode` 只認 `LoginUserInfo.CurrentTenant`（背景執行永遠 null）——EF 全域 `ITenant` filter 因此把背景查詢限定在「`TenantCode` 也是 null 的列」，不是「所有租戶」也不是「這個 widget 自己的租戶」，是 #832 描述之機制的 Dashboard 同構體——ETL 排程器同款 `CreateDC` 缺口本身已由 #841／#862 那次讓 `ITenant` 過濾器首次對 `EtlJobDefinition` 真正生效的提交（`b3dbae4b3`）一併補上的 `IgnoreQueryFilters()`（`EtlSchedulerService.LoadJobsFromDbAsync`／`EtlQuartzJob.Execute`）處理，時間點早於本項（#843）修法，機制上不是本項修法帶來的；#832 保留 open 是因為缺一支端到端 regression test（seed 租戶 job → 啟動載入 → 排程 → 觸發 → 執行全程），不是機制仍未處理——**2026-07-29 更正**：本行原寫「仍是獨立 open issue，未受本修法影響」，容易被讀成「ETL 排程器的 `CreateDC` 缺口本身也還沒修」，與程式碼不符，一併更正。修法：`WidgetDataRequest.TenantId`（原本存在卻從未被填的欄位）現在由 `EfCoreDashboardService`/`JsonFileDashboardService` 填入，`AnalysisWidgetDataSource` 僅在 `LoginUserInfo == null` 時透過 `IWtmDataContextFactory.CreateDC(currentTenant:)`（`WorkflowEngine`/`WorkflowTimerHostedService` 已在用的同款無 HttpContext 模式）明確建立租戶範圍的 DataContext——互動式 HTTP 路徑（一律有 `LoginUserInfo`）不受影響。**blast radius**：全庫僅兩個 `ApplyDataPrivilegeForAnalysis` 呼叫點（`grep -rn "ApplyDataPrivilegeForAnalysis" src/` 驗證），只有背景執行且底層 model 有設定 DataPrivilege 規則的 widget 會從「回傳全部資料」變成「回傳空結果」；沒有設定 DataPrivilege 規則的 widget 不受影響（且因租戶範圍修復而變得**更正確**，不是新增風險）。沒有設定檔可以恢復舊的 fail-open 行為——要重新開放特定查詢，需要在呼叫端加一行看得到的 `declaredSystemQuery: true`。**可觀測性**：fail-closed 預設本身是靜默的——背景 widget 變空、operator 卻看不到任何連到這個原因的訊號。修法時一併補上：每當這個方法因「無身分＋該 model 有設定 DataPrivilege 規則」而實際拒絕時，透過 `CoreProgram.GetLogger("DCExtension")`（`DCExtension` 是 static class 沒有 DI，沿用 `WtmFileProvider` 既有的 static-helper logging 模式）記一筆 `LogWarning`，訊息含 element type 名稱與補救方式（`declaredSystemQuery: true`）——**搜尋 log 關鍵字 `ApplyDataPrivilegeForAnalysis denied all rows for`** 即可定位。**節流**：每個 element type 在單一 process 生命週期只記一次（`ConcurrentDictionary` 守衛）——這是排程 job 可能每幾分鐘重跑一次的查詢路徑，逐次呼叫都記會洗版；沒有現成的「job run」邊界可用（那個邊界在呼叫端好幾層之上，且此 helper 與一律已認證的 `_AnalysisController` 共用，往下傳 job id 會擴大這個共用 static helper 的介面），process 重啟後節流自然重置，不會永久沉默。驗收：`DPWhereInMemoryTests` 新增 5 支測試涵蓋 warning 觸發、節流（3 次呼叫只記 1 次）、以及 3 種不該記的情況（無規則、`declaredSystemQuery: true`、已認證使用者自己既有的零權限拒絕）。驗收：`DPWhereInMemoryTests.ApplyDataPrivilegeForAnalysisTests`（fail-closed 預設、無規則 model 不受影響、escape hatch opt-in 且對已認證呼叫者無效）＋`DashboardBackgroundTenantIsolationTests843.cs`（真實背景路徑：兩租戶經 `MultiTenantSeedFixtureTests`/`DbTestHelpers` 模式 seed，`AnalysisWidgetDataSource` 從 demo app 真實 DI container 解析、無 `HttpContext`，斷言租戶 A 的 job 看得到自己的列但看不到租戶 B 的；停用租戶範圍修法後手動驗證此測試真的會變紅，SQL log 顯示回退成 `WHERE TenantCode IS NULL`）＋`JsonFileDashboardServiceTests`/`EfCoreDashboardServiceTests` 各補一支 `TenantId` 橋接測試＋`test/mutants/entries/dcext843-declaredsystemquery-guard-neutralize.json`（`VERDICT: KILLED`）。版本翻升至 10.20.0（minor）。 |
| #867 —— `RedoUpdateModel` 無限制 dotted-path 反射寫入觸及 process-wide DI singleton，`Configs.EnforceRequestBindingScope` 預設翻成 `true` | `BaseController.RedoUpdateModel`/`BaseApiController.RedoUpdateModel` 把 `WTMContext.CreateVM` 從 `Request.Form`／`Request.Query` 逐字複製進 `FC` 的每一個 key，原封不動用 `PropertyHelper.SetPropertyValue` 寫回 VM——該方法支援 dotted path，中間層只需要 getter 就能穿越（get-only 不構成保護，寫入落在 getter 回傳的活物件上）。`BaseVM.Wtm`／`BaseVM.ConfigInfo`（距 VM 根一跳）解析到 `WTMContext`／`Configs`，而 `Configs` 就是 `IOptionsMonitor<Configs>.CurrentValue`——**跨所有 request 共用同一個 instance**。Runtime 實測落地：`ConfigInfo.IsQuickDebug=true`（`WtmAuthorizationService` 之後對所有人回 true，直到重啟）、`ConfigInfo.IsFilePublic=true`（在執行期還原 #859/#860 剛關掉的未認證跨租戶讀檔）、`Wtm.GlobaInfo.AllAccessUrls=<prefix>`（該 URL 前綴對所有人免權限）。五個呼叫點（`Selector`／`GetPagingData`／`GetExportExcel`／`GetExportExcelStream`／`DoImport`）全部在 `[AllRights]` 之下，任何已登入帳號即可觸發，不需要任何額外授權。<br><br>**第一版修法（同一個 PR 內、合併前）被 cross-vendor review 抓到 High severity 繞過，這件事本身值得記錄**：那一版判準是「精確 `member.DeclaringType` ＋ curated 名字集合」——形狀是 denylist，不是真正的 positive allowlist。下游 VM 只要合法寫一個 alias property（`public Configs? Settings => base.ConfigInfo;`）就能繞過，因為 `Settings` 宣告在下游 VM 上，不在 `BaseVM`；`new` shadowing、alias 宣告在 `BaseVM` 與具體 VM 之間的中間 base class、interface-typed 別名、generic type parameter 關閉成 `Configs` 這四種變形，用的是同一個破口。**最終修法把判準整個反過來：依每一段 dotted path 實際解析到的「型別」擋，不依名字或宣告類別擋**——`RequestBindingPolicy.IsPathAllowed` 用 `member.GetMemberType()`（與 `PropertyHelper.SetPropertyValue` 自己往下一跳的邏輯完全相同，不是 `member.DeclaringType`）算出每一跳的型別，只要該型別「是、或可指派給（`IsAssignableFrom`，涵蓋下游子型別與 interface-typed 別名）」`WTMContext`／`Configs`／`GlobalData`／`LoginUserInfo`／`IDataContext`／`ISessionService`／`IModelStateService`／`IDistributedCache`／`IStringLocalizer`／`IUIService`／`IServiceProvider`／`IOptionsMonitor<>`／`IOptionsSnapshot<>`／`IOptions<>`（後三者是 open generic type definition，用獨立判準比對——見下方第三輪揭露）其中之一，整個 key 就拒絕——這條規則對 alias／shadowing／interface／中間 base class／generic parameter 五種變形全部有效，因為沒有一種變形能改變 getter 實際回傳的型別。額外兩層防禦：`Type.GetMember` 對同一名字解析出**超過一個 member 就直接拒絕**（fail closed，不信任 `PropertyHelper` 自己也在用的 `members[0]`——實測驗證這個情境真的會發生，但只出現在「base class 的欄位被 derived class 的同名屬性 `new` 遮蔽」這種跨 member kind 的 hiding，同 kind 的 property-hides-property 不會讓 `GetMember` 回傳多個 member，.NET reflection 會先收斂成唯一的 most-derived member）；以及深度上限 3 段（`Searcher.SortInfo.Property` 是已驗證最深的合法 payload，重新對照 `framework_layui.js`／`DataTableTagHelper.cs` 確認一般 grid 送的是不加 `Searcher.` 前綴的 2 段 `SortInfo.Property`，只有 Selector 模式才加前綴變成 3 段）——**型別判準沒有讓深度上限變得多餘：型別檢查擋的是「可達到已知危險型別」，擋不住「一串全是安全型別、但深到超乎預期」的鏈，兩者是互補、不是重疊的防線**。<br><br>**刻意不是「`BaseVM`／`BaseSearcher` 宣告的成員全擋」，也不是「所有 reference type 全擋」**：`Page`／`Limit`／`SortInfo` 等框架自己設計的綁定介面就直接宣告在 `BaseSearcher` 上，且型別是純量／POCO，不是危險閘道型別，全擋會連正常分頁排序都打壞——已驗證 `Searcher.<自訂欄位>`、`Searcher.Page`／`Searcher.Limit`、`Searcher.SortInfo.Property`／`Direction`（深度 3 邊界）仍正常運作，`RequestBindingPolicyTests867` 五個新增的 shape 測試（alias property／new shadowing／interface-typed member／intermediate base class／generic type parameter）每個各自建構一種繞過並斷言現在會被拒絕。同一份判準同時掛在兩份 `RedoUpdateModel`（`BaseController`／`BaseApiController`）之下，透過新 kill switch **`Configs.EnforceRequestBindingScope`（預設 `true`）** 控制；拒絕時記 `LogWarning`（key 經 `LogSanitizer.Sanitize`）——先前這條路徑完全沒有任何偵測訊號，本身就是缺陷的一部分。**同一個 PR 另外補上 `GetPagingData` 沒有在 `RedoUpdateModel` 之後釘回 `SearcherMode`**（`Selector`／`GetExportExcel`／`GetExportExcelStream` 都有做，只有它漏了）：caller 指定 `SearcherMode=Batch` 會讓查詢改走 `GetBatchQuery`，把 ListVM 自己 `GetSearchQuery()` 下的所有 `Where`（含列級授權過濾）整個剝掉、換成 `Ids.Contains(...)`；這一列保護的是下游 `GetSearchQuery` 的過濾邏輯，不是框架本身，CHANGELOG 也分開陳述。**刻意不搬 `UpdateModelProperty` 的欄位黑名單過來**：那份清單保護的是 entity 欄位，這五個呼叫點的根物件（ListVM／ImportVM）上沒有 `Entity`，搬過來會是「看起來修好、實際上表二一列都擋不到」的假修法，正好撞上本文件自己的紅線。**這條防線關住了 review 能構造出的每一種 alias/shadowing/interface/中間 base class/generic parameter 繞過，但不是「framework 裡永遠不會再需要在 banned type 清單加新型別」的宣稱**——清單本身仍是有限、可維護、需要隨新程式碼檢視的清單，不是自動涵蓋未來一切的證明。測試：`RequestBindingPolicyTests867`（unit 層，逐一隔離 gateway-type／static-member／ambiguous-resolution／depth cap 四種 guard，含五個 shape 測試）＋`RequestBindingScopeHttpTests867`（走 `DemoWebApplicationFactory` 真實 HTTP pipeline，斷言 `IOptionsMonitor<Configs>.CurrentValue.IsQuickDebug`，不是 status code；同一個 request 內同時驗證合法 `Searcher.ZipCode` 綁定仍然成立）。四個 mutant（`wtmsec867-gateway-type-guard-neutralize`、`-shapes`、`wtmsec867-static-member-guard-neutralize`、`wtmsec867-ambiguous-resolution-guard-neutralize`，`test/mutants/entries/`）皆為 `VERDICT: KILLED` / `GATE: PASS`——static-member mutant 特別需要一個型別不在 banned 清單、宣告類別也不在 `BaseVM`/`BaseSearcher`/`WTMContext` 上的 fixture，因為 production graph 上唯一真實存在的 static 成員（`WTMContext.ReloadUserFunc`）本來就要先經過 `Wtm`，而 `Wtm` 的型別（`WTMContext`）早就被 gateway-type guard 擋下，兩者在正式碼上天生糾纏，必須用 fixture 型別才拆得開；ambiguous-resolution mutant 需要「base 欄位被 derived 屬性同名 `new` 遮蔽」這個實測驗證過的具體構造。<br><br>**第二輪 cross-vendor review 找到第二個 High severity 繞過，而且藏住它的那句 comment 本身才是更重要的發現。** `IsPathAllowed` 原本在中間段解析失敗（`members.Length == 0`）時 `break` 後直接 `return true`，comment 寫「`SetPropertyValue` 自己的 traversal 也會 stop（middle hop）或 no-op（final hop）」——**這句話是錯的，而且從未真的對照過 `PropertyHelper.cs` 求證過**。`PropertyHelper.SetPropertyValue` 的中間層迴圈（`PropertyHelper.cs:523-551`）遇到 middle segment 解析失敗也是 `break`，但**沒有重置 `tempType`/`temp`**——它們停在失敗前的狀態（若是第一段就失敗，就是最原始的 VM 本身），然後往下解析並**寫入最後一段到那個被凍結的型別上**（`PropertyHelper.cs:553-559`）。所以 `Missing.StaticSecret` 這個 key：`Missing` 在 `FixtureVM` 上解析不到，但 `StaticSecret` 解析得到且是 static——一次繞過 static guard、ambiguity guard、gateway-type guard 三層，因為三層都沒被走到。修法：**任何一段解析為零 member，不分第一段／中間段／最後段，一律 fail closed（`return false`）**，不嘗試精確重現 `SetPropertyValue` 的「凍結型別再退回最後一段」邏輯——更簡單、更不容易再錯，而且可證明安全：對一個真的整段都解析不到的 single-segment key，`SetPropertyValue` 自己的最後一段查找本來就會找不到而 no-op，這裡多擋一次只是更保守，不改變任何實際行為。依審查的明確要求，逐條核對 `RequestBindingPolicy.cs` 裡每一句提到 `PropertyHelper` 行為的 comment（不因為其他句子讀起來合理就假設它們也對）——另外抓到兩句不夠精確並修正：ambiguous-resolution comment 原本說「most concretely: new-hiding」（同 kind 的 `new` 遮蔽其實不會造成 `GetMember` 回傳多個 member，只有跨 member kind 的遮蔽才會，上面已經講清楚）；`IsPathAllowed` 自己的 doc comment 原本聲稱「mirrors SetPropertyValue... exactly」（只在成功路徑上為真，失敗路徑刻意分岔且更保守，現在如實陳述）。新增 regression 測試：`MissingIntermediateSegment_ActuallyWritesFinalSegmentOnVm_WhenPolicyIsIgnored`（先實測證明繞過真的存在，再斷言 policy 擋下）、`IsPathAllowed_MissingIntermediateSegmentThenStaticFinal_ReturnsFalse`（`Missing.StaticSecret`）、`IsPathAllowed_MissingIntermediateSegmentThenAmbiguousFinal_ReturnsFalse`（`Missing.<ambiguous-name>`）——正是審查點名要求的兩個 regression 案例。第五個 mutant `wtmsec867-zero-resolution-guard-neutralize`（`test/mutants/entries/`）把修法還原成修復前的 `break`，`VERDICT: KILLED` / `GATE: PASS`。<br><br>**type denylist 天生無法窮舉下游 VM 可能暴露的每一個閘道——分兩種獨立失效模式，在此如實揭露，不留在暗處。**<br><br>**(a) 宣告型別本身無害，但 setter body 把寫入轉寫進共用狀態（第二輪）。** `BannedGatewayTypes` 判斷的是 alias 的**宣告型別**，看不到 setter **body** 實際做了什麼。下游 VM 可以寫 `public List<string> SharedPublicUrls { get => Wtm!.GlobaInfo!.AllAccessUrls; set => Wtm!.GlobaInfo!.AllAccessUrls = value; }`——`SharedPublicUrls` 的 resolved type 是 `List<string>`，不在 banned 清單內，單段 key 就能通過檢查，而它的 setter 直接覆寫 `GlobalData.AllAccessUrls`，嚴重度與最初發現的洞相同，只是走了型別檢查看不到的路徑。setter 的邏輯對 `Type.GetMember`/`PropertyInfo.PropertyType` 永遠不透明，不管 `BannedGatewayTypes` 加到多長都一樣——擴充清單解決不了這種失效模式，因為危險邏輯本來就不在清單會去檢查的地方。<br><br>**(b) 宣告型別本身就是閘道，只是還沒被列進清單——連自訂 setter 都不需要（第三輪找到、其具體案例已關閉）。** 第三輪 cross-vendor review 找到：`public IOptionsMonitor<ActionLogRetentionOptions> Retention => Wtm!.ServiceProvider!.GetRequiredService<IOptionsMonitor<ActionLogRetentionOptions>>();`——一個**單純、未客製化的 getter**。3 段 key `Retention.CurrentValue.NormalDays` 依序解析成 `IOptionsMonitor<ActionLogRetentionOptions>` → `ActionLogRetentionOptions` → `int`，這一輪之前沒有一個在 `BannedGatewayTypes` 上，最終落在 `IOptionsMonitor<T>` 自己的 process-wide cached `CurrentValue`——一個 form 欄位就能對全站 ActionLog 保留期做破壞性竄改。`IOptionsMonitor<>`、`IOptionsSnapshot<>`、`IOptions<>`、`IServiceProvider` 現在都在清單上。**這裡刻意求證、不是假設它會動**：`Type.IsAssignableFrom` 不會把 open generic type DEFINITION 跟它任何一個 closed construction 關聯起來（`typeof(IOptionsMonitor<>).IsAssignableFrom(typeof(IOptionsMonitor<Configs>))` 回傳 `false`）——如果只是把三個 generic entry 塞進既有的、只用 `IsAssignableFrom` 的迴圈，會靜默地什麼都擋不住，這正是第六個 mutant `wtmsec867-open-generic-gateway-guard-neutralize`（`test/mutants/entries/`）重現並 kill 掉的錯誤，`VERDICT: KILLED` / `GATE: PASS`。`IsBannedGatewayType` 現在把「型別是 generic type definition」的 entry 分流到獨立的 `IsOrImplementsOpenGenericDefinition` 判斷（closed construction 本身、透過 interface 實作、或透過 generic base class 繼承三種關係都涵蓋）。寫逐一 entry 的測試時另外實測校正了一件事：`IOptionsSnapshot<T>` 是**繼承** `IOptions<T>` 的 `Value`，不是自己重新宣告，而 `Type.GetMember` 對**interface**不會像對 class 一樣往上找 base interface 的成員——`typeof(IOptionsSnapshot<ActionLogRetentionOptions>).GetMember("Value").Length == 0`——所以一個穿過 `.Value` 的多段 test key，不管 `IOptionsSnapshot<>` 有沒有被擋，都會被另一個獨立的 zero-resolution guard 擋下；真正用來隔離這顆 guard 的 regression test 改成單段 key，只測 hop 0。**這只關掉了失效模式 (b) 的這一個具體案例，不是關掉這個失效模式本身**：下游 VM，或這個框架未來的某個相依套件，永遠可以引入一個清單還沒被告知的新閘道型別。用 `grep -rn "IOptionsMonitor<\|IOptionsSnapshot<\|IOptions<\|IServiceProvider" src/ demo/ test/` 確認（不是照抄 reviewer 的說法）本 repo 目前沒有任何 `BaseVM`/`BaseSearcher` 子類別暴露這四個新列入的型別，所以新增它們的合法綁定面成本是零。<br><br>**兩種失效模式都沒有有限的修法。** (a) 不管 `BannedGatewayTypes` 加幾條都關不掉（setter body 對 reflection metadata 永遠不透明）；(b) 只關掉清單當下列出的那些型別。目前這個 repo 裡沒有任何一種形狀的 forwarding property 或閘道型別別名（三輪 review 都用 `git grep` 確認過）——但沒有任何機制阻止下游 `BaseVM`/`BaseSearcher` 子類加一個。真正結構性關掉任一種失效模式，需要明確的 positive binding contract（逐 VM 標記哪些 member 允許被 `RedoUpdateModel` 寫入，breaking change 的規模遠大於本次修法）或讓 `Configs`／`GlobalData`／DI 容器自己的 options cache 在 DI boundary 啟動後不可變（#867 原始分析已經指出這是「真正的終局」，刻意留給另一個 issue，好讓這個範圍較窄的修法先出）。Issue #889（第三輪已擴大涵蓋兩種失效模式），不在本次 PR 範圍內。`RequestBindingPolicy` 的 class doc comment 也帶同一份揭露——包含修正一句本身沒說錯、但擺放位置讓人讀成「完整性宣稱」的句子（「cannot be defeated by any renaming/hiding/re-declaring trick」現在明確寫成：只證明這些特定手法被擋住，不是宣稱 `BannedGatewayTypes` 涵蓋完整）。版本沿用 #859 這輪已經衝到的 10.19.0，不再另外加碼（同一個 `[Unreleased]` 循環）。 |
| #828 —— 批次解析例外被當成「都不存在」，依賴失效可能靜默刪光既有子列 | `ResolveFileAttachmentIdsForCaller`/`Async`（`src/WalkingTec.Mvvm.Core/BaseCRUDVM.cs`）原本把批次解析查詢的任何例外都吞掉，回傳空集合——跟「查詢成功、但這些 id 真的都不存在」在型別上無法區分。`ApplyFileAttachmentResolution` 對兩者套用同一套逐項窄化規則（#815 第四／六／七輪：純量 FK 還原、`ISubFile` 子項 drop-or-restore）；當一個 posted 的 `ISubFile` 集合裡每一項恰好都是候選 id（一般情況——編輯一個實體時通常會把沒動過的既有子項也一起 re-post），例外就會讓整個 posted 集合被清空，直接落入 `DoEditPreparePart2` 的「空集合」分支：**該 parent 的所有既有 child row 被物理刪除，`DoEdit` 仍回報成功**。觸發不需要攻擊者：SQL Server 每查詢 2100 個參數的上限、逾時、連線瞬斷都能讓這條查詢丟例外。**先查證再修**：issue 原文主張「大量 `Contains` 是最直接路徑」，但 EF Core 8/9 早就把這類查詢改成單一 JSON 參數＋`OPENJSON`，理論上不會撞到參數上限——查證後發現 **EF Core 10（本 repo 釘住的 10.0.9，見 `Directory.Packages.props`）把預設翻譯策略改回「每個候選 id 各自一個 SQL 純量參數」**（EF 官方認定 OPENJSON 形式在部分真實 workload 上查詢計畫變差，10.0 才走回頭路），所以 2100 上限在**這個 repo 的這個版本**上是真實會撞到的，但在 EF Core 8/9 上就不會——claim 依版本而定，不能照抄。修法：`ResolveFileAttachmentIdsForCaller` 回傳結果新增 `Succeeded` 旗標；解析查詢本身失敗時，`RejectUnresolvableFileAttachmentReferences`/`Async` 直接拒絕整個請求（`MSD.AddModelError`、不 staging、不呼叫 `SaveChanges`）——跟既有的「必要 FK 無合法舊值」路徑（#815 第六／七輪）同一套「整請求拒絕」contract，不再把模糊不清的空集合交給逐項窄化邏輯處理；非例外路徑（純量還原／子項 drop-or-restore／必要 FK 拒絕）完全不變。**加上、但不是取代**：解析查詢額外拆成 500 筆一批（`FileAttachmentResolutionBatchSize`），讓一個夠大但完全合法的表單（`FormOptions.ValueCountLimit` 是 5000）不會日常性撞到參數上限——分批失敗一樣走上面的整請求拒絕，不會被窄化。沒有動任何 config 預設值、沒有動任何已存資料的形狀，不需要 migration；唯一的可觀察行為改變只發生在原本就有 bug 的那條路徑上：解析失敗現在會變成請求層級的驗證錯誤，而不是靜默的部分刪除＋回報成功。驗收：`FileAttachmentResolutionFailureGateTests828.cs`（FK-enforcing SQLite `ProductSubFileContext` fixture，`DbCommandInterceptor` 模擬解析查詢失敗，斷言既有子列存活＋caller 收到失敗，正控組同方法內證明正常路徑仍可存檔）＋`test/mutants/entries/fileattach828-resolution-failure-guard-neutralize.json`（`VERDICT: KILLED`）。 |
| #875 —— #828 只補了「隔壁一個函式」，同一缺陷在 `LoadExistingSubItemFileIds`（以及第三個窮舉才挖出的 `LoadEntitySnapshot`/`Async`）原封不動 | issue 明確要求動工前先交窮舉表：對 `BaseCRUDVM.cs` 每一個 catch-and-continue 的 DB 查詢，列出「catch 回傳什麼／呼叫端怎麼解讀／是否可達刪除分支」。窮舉（`grep -n 'catch'` 全部 20 個 catch block 逐一分類，非重新用先前 scoping）結果：(1) `ResolveFileAttachmentIdsForCaller`/`Async`——#828 已修，`Succeeded` 旗標擋住。(2) `LoadExistingSubItemFileIds`（`:2396` 附近，issue 點名的那個）——catch 只 log，落到 `return result`（空或部分累積的 dict），呼叫端 `ApplyFileAttachmentResolution` 把這個「查詢失敗」跟「查詢成功、確認沒有既有列」當同一件事，於是該 drop 的邏輯照跑，可達 `DoEditPreparePart2` 的空集合刪除分支——**本 issue 的主要修復目標**。(3) **窮舉意外挖出的第三個站點**：`LoadEntitySnapshot`/`LoadEntitySnapshotAsync`（`DoEdit`/`DoEditAsync`/`DoDelete`/`DoDeleteAsync` 唯一的 `preSaveSnapshot` 來源）——catch 回傳裸 `null`，跟「`Entity.ID` 未設定」「列真的被同時刪除」這兩個**合法**的 null 案例在型別上完全無法區分。`ApplyFileAttachmentResolution` 把 `preSaveSnapshot == null` 讀成「這是 Add，沒有既存狀態」，因而**整段跳過** `LoadExistingSubItemFileIds` 的 restore-vs-drop 判斷（`preSaveSnapshot != null ? ... : []` 這行本身）——即使 `LoadExistingSubItemFileIds` 自己這次沒有出錯也一樣，因為根本沒被呼叫。也就是說：即使只修了 (2)，一個真正的 Edit 只要在讀 snapshot 這一步撞到暫時性 DB 故障，就會被錯當成 Add，同樣的既有子項照樣被 drop、照樣可能清空整個 posted 集合、照樣觸發刪光。(4) `DoEdit`/`DoEditAsync`/`DoDelete`/`DoDeleteAsync` 的 `SaveChanges`(`Async`) catch——用 `saved`/例外本身直接短路，不呼叫任何後續刪除邏輯，是既有的正確 fail-closed 模式，非缺陷。(5) `DoRealDelete`/`DoRealDeleteAsync`——查詢（`Include`+`FirstOrDefault`）跟 `SaveChanges`/`DeleteEntity` 包在**同一個** try 裡，查詢丟例外時 `SaveChanges` 根本不會被呼叫到，同樣 fail-closed，非缺陷。(6) `DoEditPreparePart2`/`DoAddPrepareCore`/`SerializeScalarProps` 裡其餘的 catch 全部包的是 reflection `SetValue`/`GetValue`/JSON 序列化，不是 DB 往返，排除在表外並在 PR 說明逐一點名理由。**沒有分類到的列**：`AppendEditChangeLog`（`_FrameworkController.UpdateModelProperty` 專用，`#797` 讓它刻意跳過 `DoEditPrepare`）也呼叫 `LoadEntitySnapshot`，但這條路徑從不觸及 `ApplyFileAttachmentResolution`，failure 只會讓該次 ChangeLog 的 `OldValues` 缺失——維持 best-effort，不整請求拒絕。async 雙生法（`LoadEntitySnapshotAsync`/`ResolveFileAttachmentIdsForCallerAsync`）、grandchild 巢狀集合（本類別本來就只處理 TModel 自身一層 `List<T>`，無遞迴）、reflection 間接呼叫（`BasePagedListVM.UpdateEntityList`、`BaseBatchVM.DoBatchEdit(Async)`、`BaseImportVM.BatchSaveData` 從未呼叫 `DoAddPrepare`/`DoEditPrepare`，屬於既有、範圍外的 #824 追蹤項，不在本檔案內）均已確認涵蓋或明確排除，非本輪遺漏。修法：沿用 #828 已建立的 contract，不另發明——`LoadExistingSubItemFileIds` 回傳新增 `Succeeded` 旗標（`ExistingSubItemLookupResult`），例外時捨棄任何部分累積的結果；`LoadEntitySnapshot`/`Async` 同樣回傳 `EntitySnapshotResult(Succeeded, Snapshot)`，`DoEdit`/`DoEditAsync`/`DoDelete`/`DoDeleteAsync` 在 `Succeeded == false` 時直接 `MSD.AddModelError` 並 return，不呼叫 `DoEditPrepare`／不 `SaveChanges`。**分批**：`LoadExistingSubItemFileIds` 的查詢跟 #828 的解析查詢一樣是 `Contains()`-shaped、id 數量最壞情況可達整個 posted 子集合，同樣會撞 EF Core 10 的 2100 參數上限，因此套用相同的 `FileAttachmentResolutionBatchSize`（500）分批，理由與 #828 完全相同。**已知代價（誠實記錄，非隱藏）**：`LoadEntitySnapshot` 失敗現在會讓 Edit/Delete 整請求失敗，即使該 TModel 完全沒有 `FileAttachment`/`ISubFile` 屬性——先前這種情況會靜默用 null snapshot 繼續（只是 ChangeLog `OldValues` 跟 `DeletedFileIds` 清理失效），現在會直接回報失敗。這是刻意選擇：與其為「這個 TModel 有沒有 file 屬性」另開一條分支邏輯（等於重新發明第二套 contract），不如比照 #828 的「查詢失敗一律整請求拒絕」統一規則，範圍不變。沒有動任何 config 預設值、沒有動任何已存資料的形狀，不需要 migration。驗收：`ExistingSubItemLookupFailureGateTests875.cs`（sync/async 既有子項 lookup 失敗 + 正控組 + 500 上限分批迴歸）與 `EntitySnapshotLoadFailureGateTests875.cs`（sync/async 第三站點 + 正控組），均為 FK-enforcing SQLite `ProductSubFileContext` fixture＋`DbCommandInterceptor` 精準鎖定各自的目標查詢（不同資料表名稱互不干擾）；`test/mutants/entries/existingsubitem875-lookup-failure-guard-neutralize.json`（`VERDICT: KILLED`）。 |
| #869 —— Vue3 demo 樣板 `wtmbuild.ts` 寫死 Windows 路徑分隔符已修；`vite build` 在目前釘住的版本組合下仍過不了關（#891，未修，非本項範圍） | 自訂 `wtmBuildPlugin` 的 `buildStart` 用字串拼接 `"\\src\\views"`／`"\\public\\menu.json"` 建路徑，在非 Windows 平台上 `readDir` 一開始就 `ENOENT`。已改用 `path.join(__dirname, "src", "views")` 等呼叫——`path.join` 在 Windows 上解析結果逐位元組不變，非相容性破壞。**修完後 `vite build` 在這個 demo 樣板釘住的依賴組合上仍無法完整跑完**，原因與路徑分隔符無關、修復前就存在：`src/i18n/index.ts` 用 CJS 風格深層匯入 `element-plus/lib/locale/lang/en`／`zh-cn`；已核對 `node_modules/element-plus@2.13.5` 的 `package.json`，其 `exports` map 對 `./lib/*` 只宣告 `require`（`types` 另計），沒有 `import` 條件，`vite@7.3.5` 的 ESM export-conditions 解析不會 fallback 到 `require`，直接丟 `[commonjs--resolver] No known conditions for "./lib/locale/lang/en" specifier`（已本機重現此錯誤逐字比對）。**非 macOS 專屬**：同一組釘住版本在 Windows／Linux 上會踩到一模一樣的 export-conditions 錯誤，跟作業系統無關，純粹是 `vite`/`element-plus` 版本組合沒對齊——已獨立立案 #891 追蹤，未修，故意不在本項範圍內處理。 |
| #804 —— `LookupCacheService.RefreshAsync` 在 semaphore 逾時後仍照樣寫快取（繞過 stampede 保護）；warmup 對 tenant-isolated 型別照樣宣稱成功（MEDIUM，已修） | `RefreshAsync` 算出 `acquired` 卻從未檢查就繼續 `Invalidate`/`LoadFromDbAsync`/`SetCache`——是這個檔案裡唯一漏掉 #112(4)/M10 逾時處理（`GetAll`/`GetAllAsync` 已有，:207-234／:293-313）的呼叫點；逾時代表沒拿到鎖，寫入會跟真正鎖住的那個執行緒的 `SetCache`互相競爭，可能用較舊快照蓋掉較新資料，撐滿整個 TTL。修法：比照另兩處檢查 `acquired`，但因為 `RefreshAsync` 是呼叫端明確要求「現在就要生效」、沒有回傳值可供判斷成功與否，逾時改為記 warning 後拋 `TimeoutException`（**呼叫端可觀察的行為變化**：以前逾時會靜默完成，現在會拋例外，預設 10 秒逾時視窗不變）。`LookupCacheOptions` 新增 `StampedeTimeout`（預設值不變，10 秒，現在可設定，呼應這個檔案 log 訊息一直叫 operator「調大 StampedeTimeout」卻其實無法調的落差）。另一半：`LookupCacheWarmupService` 對每個 `WarmOnStartup=true` 型別一律用 `tenantId=null` 暖機；若該型別同時實作 `ITenant` 且 `DefaultTenantIsolation` 生效（預設如此），會命中 `LookupCacheService` 既有的 #112(1)/#168 bypass（直接回傳 DB 結果、從不呼叫 `SetCache`）——呼叫沒丟例外，warmup 端無從分辨「真的暖了」與「查完就丟掉」，於是照樣印 `"Lookup cache warmed: {TypeName}"` 與整體的 `"warm-up completed."`。修法：`WarmTypesAsync` 對這類型別直接跳過並誠實記 log（省下一次白做的 DB 查詢），`ExecuteAsync` 改成只在「至少一個型別真的被快取」時才印 completed，否則印 Warning 等級的「0 of N 個型別真的被快取」。**已發現、當時未修、已於 #943/#944 個別修復（見下方新條目）**：`RefreshAsync` 也沒有在 `SetCache` 前檢查 `_registry.ContainsKey`（`GetAll`/`GetAllAsync` 的 #112(2) 防護沒有對應版本），對非 `[CacheLookup]` 型別呼叫會產生沒有 TTL、SaveChanges 自動失效機制不會清的不死快取項——這是 #944，已修。`DistributedLookupCacheService.RefreshAsync`（`DistributedLookupCacheService.cs:345-360`）逐字同款的 semaphore-timeout 繞過缺陷——這是 #943，已修。測試：`test/WalkingTec.Mvvm.Core.Test/Cache/LookupCacheStampedeRefreshTimeoutTests804.cs`（`DbCommandInterceptor` 實測 SQL SELECT 次數、FileWal 真實併發，斷言載入次數「恰好一次」而非「小於 N 次」；RefreshAsync 逾時斷言拋例外＋載入次數不變＋快取保有鎖持有者的結果）與 `LookupCacheWarmupTenantHonestyTests804.cs`（正向：真的被暖到的型別後續查詢不再打 DB；反向：只註冊 tenant-isolated 型別時 completed log 永不出現、改印誠實的 0/N warning）；手動 revert 兩處修法各自的關鍵區塊重跑過一次，確認只有對應新測試變紅、其餘不受影響，非 CI 強制的 mutant。全套 `test/WalkingTec.Mvvm.Core.Test`：4824 passed, 0 failed。 |
| #827 —— `_FrameworkController` 五個授權 hook 在生產路由上不可達（改為 DI 可解析的 `IWtmFrameworkEndpointAuthorizer`） | `_FrameworkController` 是 MVC 掃描到、`/_Framework/*` 實際路由到的**具體類別**（`_FrameworkController.cs:35`，非 abstract）。`CanExportVm`/`CanAccessFile`/`CanPreviewDelete`/`CanImportVm`/`CanEditProperty` 原本是 `protected virtual`，#796/#814/#818 自己的文件教整合者「繼承 `_FrameworkController` 並 override 這個 hook」——那條路只會建立第二個、前端硬編 URL 永遠打不到的 controller。九個既有測試方法（`FrameworkControllerRbacHooksTest`/`FrameworkControllerFileAccessTest`/`FrameworkControllerImportAuthTest`）全部直接建構測試子類別呼叫 action method，證明的是 hook 機制在隔離環境下可用，不是它在生產環境可達。修法：新增 `IWtmFrameworkEndpointAuthorizer`（`src/WalkingTec.Mvvm.Core/Services/`），三值決策 `WtmAuthorizationDecision`（`Inherit = 0`／`Allow`／`Deny`，刻意不設 `DefaultAllow` 屬性讓 `default` 真的是棄權），由 `_FrameworkController` 自己（不是子類別）透過 `HttpContext?.RequestServices?.GetService(typeof(IWtmFrameworkEndpointAuthorizer)) as IWtmFrameworkEndpointAuthorizer` 解析並呼叫；兩個 `?.` 都是必要的（既有 hook 測試用裸 `DefaultHttpContext` 或只 stub 單數 `GetService(Type)` 的 `Mock<IServiceProvider>`，`GetService<T>()`/`GetRequiredService` 對這些 fixture 會 NRE 或丟例外）。`Inherit`（含未註冊任何 policy）落回原本的旗標判定；`CanEditProperty`（五個 hook中唯一的寫入端點、唯一沒有 `Enforce*` 旗標的一個）落回原本硬編的 `true`。註冊 helper `AddWtmFrameworkEndpointAuthorizer<T>()`（`FrameworkServiceExtension.cs`）用 `AddScoped`，不用 `AddSingleton`——真實 policy 通常要查 `LoginUserInfo`/資料庫，`AddSingleton` 會造成 captive dependency。**未註冊任何 policy 時行為與修復前完全相同**——這個 seam 純粹是加法，不改變任何既有部署的預設行為。與既有 `IWtmAuthorizationService`（URL/選單層 RBAC，回答不同的問題）並存，沒有 `[Obsolete]`，兩者間無 migration。**刻意不出貨預設 policy**：WTM 不知道下游部署實際的 per-VM/per-resource 規則（哪個 VM 型別該對應哪個角色、哪個檔案屬於哪個呼叫者），出一個非空預設值是本 PR 沒有做的相容性決策——在整合者明確呼叫 `AddWtmFrameworkEndpointAuthorizer<T>()` 之前，每個既有部署的行為都不變。驗收：`test/WalkingTec.Mvvm.Api.Test/FrameworkAuthorizationSeamTests.cs` 全部透過 `DemoWebApplicationFactory` 打真實 `/_Framework/*` HTTP 路由（不是子類別）——證明 DI 註冊的 policy 雙方向都被真正採用（`Deny` 蓋掉預設寬鬆、`Allow` 蓋掉 `EnforceVmExportAuthorization=true` 的 fail-closed）、五個 hook 各一支「無 policy、無旗標」的預設行為 pinning 測試——這五支的工作只是 pin「未註冊 seam 時預設行為不變」，刻意不斷言 seam 本身有沒有被諮詢；以及一支匿名＋`IsFilePublic=true` 測試證明 policy 在 `LoginUserInfo == null` 時仍被正確諮詢且公開檔案仍可服務。Mutant `mvc827-di-authorizer-neutralize`（`test/mutants/entries/`，`VERDICT: KILLED`）把 DI 解析短路成永遠 `null`——這隻殺得死的只有前面雙方向的 `Deny`/`Allow` 測試（兩支各自也斷言測試用 authorizer 自己的呼叫計數，如 `CanExportVmCalls > 0`），證明**這兩支**真的依賴 DI 解析本身、不是只依賴它退回的旗標判定；五支「無 policy、無旗標」的預設行為 pinning 測試從未註冊任何 policy，這個 mutant 對它們的結果沒有影響，不能拿來當作它們證明了 seam 被諮詢。**範圍**：只涵蓋這五個 hook；#836 建議的「把 `_AnalysisController.CheckAccess`（及其重複實作 `AnalysisWidgetDataSource.CheckAccess`）也吸收進同一個介面、順帶補上 `_AnalysisController`/`_DashboardController`/`_DashboardDesignerController` 未覆蓋的端點」不在本 PR 範圍，另立 #812/#823/#847/#867 追蹤（皆維持 open）。**命名更正（PR #881 review 發現）**：#836 自己的留言把這個方法稱為 `AnalysisVmRegistry.CheckAccess`，但樹裡沒有這個方法——`AnalysisVmRegistry` 只有 `Build`/`Resolve`/`GetRegisteredTypes`；`CheckAccess` 是直接定義在 `_AnalysisController` 上的方法（`AnalysisWidgetDataSource` 另有一份幾乎相同的重複實作）。 |
| #829 —— 四個 VM-name 端點在授權**之前**仍會建構呼叫者指定的 VM（改用 `TryResolveVmType`） | `GetExportExcel`/`GetExportExcelStream`/`GetExcelTemplate`/`GetDeletePreview` 原本都用 `Wtm.CreateVM(name, null, null, true)` 探測呼叫者指定的 VM 型別後才呼叫對應 hook；`passInit: true` 只擋 `DoInit()`/`InitVM()`/`searcher.DoInit()`，**從未擋過建構式本身**、`WTMContext.CreateVM` 對 `IBasePagedListVM` 無條件呼叫的 `lvm.DoInitListVM()`，或對 `IBaseImport<BaseTemplateVM>` 同樣無條件呼叫的 `tvm.Template.DoInit()`（`GetExcelTemplate` 自己原本的註解就承認「there is no passInit-only way to avoid that from this call site」）。即使 enforcement flag 已開啟、最終回傳拒絕，已認證使用者仍可指定任意 VM 型別觸發其自訂初始化與資料庫查詢。修法：改用 `WTMContext.TryResolveVmType`（只解析 `Type`，不建構任何實例）——與 `DoImport` 為 #818 已採用的解法相同——四個端點在授權通過之前完全不建構任何東西。`GetDeletePreview` 對「無法解析名稱」這個子集的回應**恢復**為 `BadRequest()`——與 `CanPreviewDelete` 拒絕的 `Forbid()` 分開兩個 return，避免兩種情況合流。**但這只恢復了 base tree 原本 `try { Wtm.CreateVM(name, null, null, true) } catch (ArgumentException)` 捕捉到的其中一個子集，不是全部**：base 的 `try` 包住整個探測式建構呼叫，包含 `WTMContext.CreateVM` 對 `IBasePagedListVM` 無條件呼叫的 `lvm.DoInitListVM()`（`WTMContext.CreateVM.cs:143`）與對 `IBaseImport<BaseTemplateVM>` 無條件呼叫的 `tvm.Template.DoInit()`（`:150`）——所以一個名稱本身可以解析、但該 VM 初始化時丟出 `ArgumentException` 的情況，在 base tree 上也會落到同一個 `BadRequest()`。#829 的修法刻意把整個探測呼叫移除；名稱可解析之後的建構（以及它可能丟出的例外）現在只發生在授權通過後的逐列迴圈裡，這個方法本身沒有包 try/catch。也就是說，這一批更寬的 `ArgumentException`（名稱可解析、但 VM 初始化失敗）不再回傳 `BadRequest`——這是 #829 把建構移到授權判定之後的直接後果，不是本次修正引入或遺留未修的迴歸，也是刻意接受的取捨：要重新攔下它就等於重建 #829 本來要移除的那個授權前探測呼叫。**PR #881 review 抓到的過程性錯誤**：中間一版把兩種情況合併成同一個 `Forbid()`（無法解析名稱從 400 變成 302），且同段註解還聲稱「unchanged」——review 用 `git show origin/dotnet10:...` 對照 base tree 抓到這個矛盾；已拆開修正，並補上 `GetDeletePreview_UnknownVmName_ReturnsBadRequestNotForbid` regression test。驗收：`test/WalkingTec.Mvvm.Admin.Test/FrameworkControllerVmConstructionOrderingTest.cs` 用計數器 fixture（建構式、`InitListVM()`、`GetSearchQuery()`、匯入樣板的 `InitVM()`——即 `BaseVM.DoInit()` 的實作本身）逐一證明拒絕路徑上全部為零次呼叫，每條負向斷言在同一支測試檔中配一個允許路徑的正控組。Mutant `mvc829-getexportexcel-reintroduce-preauthz-construction`（`VERDICT: KILLED`）在授權判定前重新插入一次（結果被丟棄的）探測式建構呼叫，證明驗收測試真的依賴這個修法而非巧合。 |
| #947 —— `Selector` 的 `Ids` 分支繞過列級 `DataPrivilege`（`SearcherMode.Batch` + `ReplaceWhere` 刪光所有 `Where`，含授權過濾）；`GetBatchQuery()` 改走「空白 Searcher + AND」，連帶關掉更多同機制的兄弟站點（PR #953 adversarial review 後修正版） | `_FrameworkController.Selector`（`_FrameworkController.cs:413`，`[AllRights]`，任何已認證帳號可觸發——但見下方「#953 F9」對「任何」的窄化）在 `Ids?.Count > 0`（widget 顯示「已選取」的 chip 列表）時原本設 `SearcherMode = Batch` 並把 `listVM.ReplaceWhere` 設成 `Ids.GetContainIdExpression(...)`。`DoSearch()`／`DoSearchAsync()` 對任何 `SearcherMode` 都會在 `ReplaceWhere != null` 時跑 `WhereReplaceModifier`（`ExpressionVisitors.cs:298-403`，自己的中文註解就寫「先調用一次 Visit，刪除所有的 where 表達式」）——這是逐 Where 節點刪除，不分辨哪個是 UI 搜尋條件、哪個是 `DPWhere`（`DCExtension.Query.cs:27-227`）加的列級授權過濾，兩者在 expression tree 上都只是普通 `Queryable.Where` 呼叫，結構上無法區分。淨效果：任何已認證呼叫者指定任意 VM 名稱＋任意 `Ids`＋任意 `_DONOT_USE_VFIELD`，即可取得列級授權原本會擋下的資料列。**#867 修的是同一個機制的另一個呼叫點（`GetPagingData` 漏了在 `RedoUpdateModel` 後釘回 `SearcherMode`），該修法的文件明確記錄「`Selector` 有做（釘回 `SearcherMode`）」——但 `Selector` 用 `Batch` 本身是刻意設計（selector 要顯示「已選取」的列，不管目前搜尋框打了什麼），從未檢查過這個刻意設計底下的 `ReplaceWhere` 到底刪了什麼；#867 的文件本身沒有宣稱涵蓋這個分支，本項不是對前次文件的更正。**範圍先講清楚**：`ITenant` 的 EF global query filter 是 `HasQueryFilter`（`DataContext.cs:257`）掛的 EF-internal 查詢重寫機制，不是 expression tree 上的 `.Where()` 節點，不在 `WhereReplaceModifier` 刪除的範圍內——目前證據指向**同租戶、列級 `DataPrivilege`** 繞過，**未證實**跨租戶讀取；本項修法與測試都只涵蓋前者。<br><br>**選定的修法（兩個候選之一，經 cross-vendor review 提出後評估選定，非直接照抄）**：不修 `WhereReplaceModifier` 本身（該類別被 `Export`／`MasterDetail`／任何 host app 自己設的 `ReplaceWhere` 共用，改它的刪除邏輯風險面遠大於本項範圍——見下方「#953 F8」，這個決定的代價是 `ReplaceWhere` 本身仍是一條活的繞過路徑）；改在 `BasePagedListVM` 新增 `GetAuthorizedIdsQuery`（private）：暫時把 `Searcher` 換成一個全新、未綁定的 `TSearcher` 實例（`CopyContext` 只帶 `Wtm`／`FC`／`ViewDivId`，不帶任何篩選欄位值）再呼叫 `GetSearchQuery()`，把 `Ids` 限制用一個普通 `.Where()` AND 上去。全程沒有刪除任何既有 `Where` 節點——這正是與 `WhereReplaceModifier`「先刪光再重建」相反的形狀。`GetBatchQuery()` 自己在 `ReplaceWhere == null` 時的預設分支改呼叫這個方法；`Selector` 本身**不再呼叫任何新方法**——`SearcherMode`已經是 `Batch`、`ReplaceWhere` 單純不設，`GetDataJson()` → `DoSearch()` → `GetBatchQuery()` 走到同一個已修分支即可，見下方「#953 F2」。<br><br>**#953 F2（adversarial review 修正，已採用）——原版多寫了一個不需要的公開 API，且繞過了 `DoSearch()` 本身**：初版讓 `Selector` 呼叫新方法 `PopulateSelectedEntities(Ids, peid)`，直接把 `EntityList`／`IsSearched=true` 填好、跳過 `GetDataJson()` 自己的 `DoSearch()` 分派——這個捷徑有兩個代價：(a) `DoSearch()` 開頭的 `GetSearchCommand()`（原生 SQL／預存程序來源的 ListVM 走這條，demo 樹的 `ActionLogListVM` 即為一例，`CustomView` 不是 mapped entity）被整個跳過，這類 ListVM 改成查 `DC.Set<TModel>()` 或直接丟例外；`Searcher.SortInfo` 的 `OrderReplaceModifier` 同樣被跳過。(b) 為了讓 `Selector` 能呼叫它，`PopulateSelectedEntities` 被加進 `IBasePagedListVM<out T, out S>` 介面——對外部實作者是 source／binary breaking change。Review 證明兩個代價都不必要：`GetBatchQuery()` 的預設分支已經修好之後，`Selector` 什麼都不用額外呼叫，把 `ReplaceWhere` 賦值那行直接刪掉、`SearcherMode = Batch`（本來就有）保留，`GetDataJson()` 自然流到 `DoSearch()` → `GetBatchQuery()` → 修好的預設分支——`GetSearchCommand()`／`SortInfo` 全部照舊生效，也不需要任何新公開 API。已採用：`PopulateSelectedEntities` 已從 `BasePagedListVM`／`IBasePagedListVM` 移除；`_FrameworkController.Selector` 現在只多一行 `listVM.SelectorValueField = _DONOT_USE_VFIELD;`（`GetBatchQuery()` 讀這個屬性決定 `Ids` 比對哪個欄位，取代原本手動組 `Expression.Property` 的寫法）。<br><br>**#953 F1（adversarial review 找到、已用可執行的重現程式驗證，非僅推論）——空白 Searcher 假設對第三類 `Where` 形狀不成立**：原版文件宣稱「換成空白 Searcher 就代表目前 UI 搜尋條件被忽略」，這句話只對「透過 `CheckContain`/`CheckEqual`/`CheckWhere` 等 guard-then-add helper 加的 `Where`」成立（helper 本身檢查 Searcher 欄位值為 null/空才跳過 `.Where()`，換成空白 Searcher 讓這個檢查失敗、`Where` 從一開始就不會被加入）。真正的不變式分三類：(1) guard-then-add helper 加的 `Where`——確實被壓下；(2) 完全不讀 Searcher 的 `Where`（`DPWhere` 加的授權 `Where`、寫死的業務規則 `Where`）——不管 Searcher 是不是空白都不受影響，這是本項修法依賴的性質；(3) **不經 guard helper、直接在 LINQ lambda 裡讀 Searcher 的 `Where`——不可靠地被壓下，取決於 Searcher 何時被讀取**，本 repo demo 樹本身就有兩種對應形狀，皆已用可執行的重現程式驗證（見驗收段落）：`demo/.../MajorDetailListVM.GetSearchQuery()`：`.Where(x=>Searcher.SchoolId==x.SchoolId)` —— `Searcher` 是對 VM 實例（`this`）的成員存取，包在 lambda closure 裡，在**查詢執行（enumerate）當下**才被讀取；`GetAuthorizedIdsQuery` 的 `finally` 在呼叫端真正列舉查詢**之前**就已經把 `Searcher` 還原回真實、request-bound 的值，所以這個 `Where` 讀到的是還原後的真實值，等於完全沒被壓下——即使該列的 id 明確寫在 `Ids` 裡，只要不符合目前搜尋框內容，照樣消失。`demo/.../CityChildrenDetailListVM.GetSearchQuery()`：`var id = (Guid?)Searcher.ParentId...; if (id == null) return new List<City>().AsQueryable()...;` —— 這段是 `GetSearchQuery()` 內的一般 C# 陳述式，在**空白 Searcher 生效期間同步執行**，`id` 永遠是 `null`，永遠走空清單分支，`Ids` 限制疊加在一個空的 `EnumerableQuery` 上——不管真實 `ParentId` 或請求的 `Ids` 是什麼，永遠回傳零筆、不查資料庫。**兩者皆為 fail-closed（缺資料或無資料，不會多洩漏），不是安全回歸，但都是先前未被記錄的相容性改變**——原本這些列會顯示（`WhereReplaceModifier` 舊行為會把整段 `Where` 砍掉），修法之後可能悄悄消失。已更正 `BasePagedListVM.cs` 的 `GetAuthorizedIdsQuery`／`GetBatchQuery()` XML 文件與行內註解為上述精確的三分類不變式；`test/WalkingTec.Mvvm.Core.Test/VM/BlankSearcherShapeTests953.cs` 用兩個對映上述形狀的 fixture ListVM 各釘住一支 regression test（斷言目前這個「不可靠」行為，若未來要讓它變成 2 筆／1 筆，必須走下方提到的 marker-tag 重新設計，不能悄悄改 `GetAuthorizedIdsQuery`）。結構性關掉這個殘留（在 `DPWhere` 掛的 `Where` 節點上加標記，讓 `WhereReplaceModifier` 能選擇性跳過而不是換空白 Searcher）是設計層級變更，留給另一輪處理，不折進本次修法。<br><br>**窮舉（本輪重新對整棵樹跑，不只 `src/`——`grep -rn "SearcherMode = \|\.ReplaceWhere =" src demo test --include="*.cs" --include="*.txt" \| grep -v "/obj/\|/bin/" \| grep -E "Batch\|CheckExport\|ReplaceWhere"`，`src/` 部分另以 `grep -rn "CheckExport" src --include="*.cs" --include="*.txt"` 補抓多行 ternary）**：`src/` 共 **13** 個獨立站點（原文件的窮舉只涵蓋 `src/` 且遺漏 3 個，已更正，見下表）；`demo/` 額外命中 **37** 行，`test/` 額外命中 **14** 行，兩者皆非獨立 sink——全部核對過，`demo/` 的 37 行清一色是 `vm.SearcherMode = ... ? CheckExport : Export` 這個模式（generated controller 呼叫框架既有機制），`test/` 的 14 行是既有測試直接操作 VM 屬性做隔離測試。**`src/` 13 個站點**：`_FrameworkController.cs:413`（`Selector`，本項直接修）／`:864`＋`:945`（`GetExportExcel`／`GetExportExcelStream`，`Ids.Count>0` 時設 `CheckExport`，未設 `ReplaceWhere`）／`Helper/FileExtension.cs:21`（`GetExportData<T>()`，公開 extension method，同款邏輯，demo 樹裡 52 個檔案、92 處呼叫，全部經 `GenerateExcel()`→`GetCheckedExportQuery()`→`GetBatchQuery()`）／`GeneratorFiles/Spa/Controller.txt:133`＋`GeneratorFiles/Spa/Blazor/Controller.txt:133`（**code generator 樣板，隨 NuGet 套件出貨**——每個用這個框架 scaffold 出來的下游 controller 都內建這個 `ExportExcelByIds(string[] ids)` action，直接收呼叫者傳入的 `ids`）／`BaseApiController.cs:143`＋`BaseController.cs:169`（皆為註解、非活動程式碼）／`Filters/FrameworkFilter.cs:148`（全域 `ActionFilter`，任何 action 參數是 `IBaseBatchVM<BaseVM>` 就觸發，`Ids` 可由呼叫者透過該筆業務端點自己的表單控制，但 VM 型別由該端點自己的參數型別決定、不是像 `Selector` 那樣呼叫者可自由指定字串）／`WTMContext.CreateVM.cs:101`與`Services/WtmVmFactory.cs:201`（`Wtm.CreateVM<T>(ids:...)`／`IWtmVmFactory.CreateVM(...)`，後者註冊進 DI 但全庫沒有任何建構子注入或呼叫，框架自己的 request path 用不到，只有 host app 自己注入才碰得到）／`TagHelpers.LayUI/Form/SelectorTagHelper.cs:199`（Razor 端渲染既有選取值，`Ids` 來自實體已存的欄位值、不是原始 HTTP 參數；`:203` 的 `ReplaceWhere` 賦值本身已註解掉）。這 13 個站點裡，除 `Selector` 自己（直接修）與 `GetPagingData`（`:486`，#867 已修，`SearcherMode` 釘回 `Search`，`Batch` 從未進入）外，其餘全部落入 `GetBatchQuery()` 自己的預設分支，修 `GetBatchQuery()` 一次就連帶關掉全部（含 `demo/` 那 37 個消費端與樣板產生的每一個下游 controller），不需要逐一修改呼叫端，也不需另立 issue。**這一點是雙面刃（#953 review 提醒）**：能一次關掉的範圍比原文件宣稱的 7 個框架呼叫點大得多，但「改動預設分支」造成的靜默行為變更（見上方「#953 F1」）波及的範圍也同樣是每一個下游 scaffold 出來的 controller，不是本 repo 內的 7 個站點。<br><br>**測試**：`test/WalkingTec.Mvvm.Api.Test/SelectorDataPrivilegeTests947.cs` 現有兩支測試。(1) `Selector_...`：`DemoWebApplicationFactory` 的隔離 `WithWebHostBuilder` 實例（改註冊 `List<IDataPrivilege>` 加入 `DataPrivilegeInfo<School>`——demo app 自己的 `Startup.DataPrivilegeSettings()` 對 `School`/`Major`/`City` 全部註解掉，不改這個隔離實例的話 `DPWhere` 會直接跳過檢查），seed 兩間學校各一個 `Major`、把 admin 的 `DataPrivilege` 只授權其中一間，一次 request 把兩個 `Major` 的 id 都塞進 `Ids`、同時附上一個兩邊 `Remark` 都不match 的 `Searcher.Remark`，斷言未授權的 `Major` 不在回傳的 `SelectData` 裡、已授權的 `Major` 有回傳（positive control，同時因為 `Searcher.Remark` 刻意不 match，也證明目前搜尋條件被正確忽略）。`Selector` 回傳的是渲染過的 Razor partial（`Selector.cshtml` 把 `ViewBag.SelectData` 原樣塞進 `<script>` 內的 `var var_XXXX = [...]`），測試用 regex 抓出這段 JSON 再解析，不是斷言 HTTP 狀態碼。**#953 F4（adversarial review 找到、已補）——`GetExportExcel`／`GetExportExcelStream` 這條額外關掉的路徑原本零測試覆蓋**：把 `GetBatchQuery()` 的預設分支還原成舊的 `WhereReplaceModifier` 重建，整個測試套件（含上面那支 `Selector` 測試，因為 F2 之後它也走 `GetBatchQuery()`）當時仍全綠，等於「額外關掉的那一半」完全沒被驗收證明過。新增 (2) `GetExportExcel_...`：獨立 seed 一組學校／`Major`／DataPrivilege 授權（不與 (1) 共用，證明不依賴測試執行順序），POST `/_Framework/GetExportExcel` 帶兩個 `Major` 的 `Ids`，用 NPOI `XSSFWorkbook` 解析回傳的 xlsx bytes，斷言未授權 `Major` 的 `MajorName` 不出現在任何 sheet／row／cell 裡、已授權的有出現（positive control）。<br><br>**Mutant（兩個，皆 `VERDICT: KILLED` / `GATE: PASS`）**：`947-selector-populateselectedentities-replacewhere-reintroduce`（`test/mutants/entries/`）把 `_FrameworkController.cs` 的 `listVM.SelectorValueField = _DONOT_USE_VFIELD;` 還原成修復前的 `listVM.ReplaceWhere = listVM.Ids.GetContainIdExpression(...)` 賦值（`SearcherMode = Batch` 本身兩行前就有、修復前後皆同，未動）——手動驗證：mutant 套用後兩個 `Major` 的 id 都出現在 `SelectData`，未修復前的繞過原樣重現；`red_test_filter` 為上面的 `Selector_...` 測試。`953-getbatchquery-wherereplacemodifier-reintroduce`（`test/mutants/entries/`）把 `BasePagedListVM.cs` 的 `GetBatchQuery()` 預設分支還原成舊的 `WhereReplaceModifier` 重建——`red_test_filter` 為上面的 `GetExportExcel_...` 測試，獨立證明「額外關掉的那一半」確實依賴這次修法、不是巧合綠燈。<br><br>**#953 F8（揭露，非本項修法範圍）——`ReplaceWhere` 本身仍是活的繞過路徑**：本項刻意不修 `WhereReplaceModifier`（見上方「選定的修法」），所以任何 host app 自己對某個 ListVM 設定 `IBasePagedListVM.ReplaceWhere`（一個文件記載的公開屬性）仍然會刪光包含 `DataPrivilege` 在內的所有 `Where`——`grep -rn "\.ReplaceWhere =" src` 確認框架自己的 request path 已經沒有任何一處還會這樣設（唯一殘留賦值在 `SelectorTagHelper.cs:203`，已註解），所以框架本身不可觸發，但這是 host app 若自己使用這個公開 API 就會落入的既有陷阱，不是本項修法製造的新洞。<br><br>**#953 F9（揭露，措辭窄化）——「任何已認證呼叫者」不含 `AllowUnauthenticatedSelector=true` 時的匿名呼叫者**：`_FrameworkController.cs:376-379`，`ConfigInfo.AllowUnauthenticatedSelector`（非預設）開啟時 `Selector` 連認證都不需要——安全公告草稿本身已正確記錄這點，本文件先前的「任何已認證呼叫者」措辭在該旗標開啟時其實是低估（under-claim）攻擊面而非高估，仍一併更正措辭以求精確一致。 |
| #994 —— 下游可見文件的四處錯述（純文件修正，無程式碼變更） | 由下游（BMS）驗證 10.21.0-rc.1 時照我們自己的文件行動而暴露，見 #989。**(1) 遷移指引會叫人建一個已經存在的索引。** `CHANGELOG.md` `[10.18.0]` 的 Migration 段與 `docs/wtm-developer-manual.md` 都只寫「既有 DB 需手動 `CREATE INDEX`」，未提及本 repo 自己的 `db-migration-8.1.13.sql:23` 早已在同一欄位上建過索引。**已驗證的事實**：`RefreshTokenEntity.cs:38` 宣告 `[Index(nameof(Token), Name = "IX_FrameworkRefreshTokens_Token")]`；`db-migration-8.1.13.sql:23`（MSSQL）／`:46`（MySQL）／`:69`／`:90`／`:113` 建立 `IX_RefreshToken_Token` on `(Token)`——同欄位、不同名，兩者皆由讀取原始碼確認。`grep -cE "CREATE INDEX.*ExpiresUtc\|CREATE INDEX.*RevokedUtc" db-migration-8.1.13.sql` 回 **0**，故「腳本建的 DB 只缺 `ExpiresUtc` 與 `(RevokedUtc, ExpiresUtc)` 兩個索引」這句話有實據。**未驗證、明確標示為推論的部分**：「名稱不衝突所以 `CREATE INDEX` 會成功並靜默留下兩個重複索引」是依各 provider 以索引**名稱**（而非欄位集合）作為唯一識別的標準行為推導，**沒有實際在任何一個 provider 上執行過**；新增的四組查詢 SQL 與兩組冪等 DDL 同樣**未對任何真實資料庫執行過**，是照各 provider 的 catalog／DDL 語法撰寫。CI 不會執行它們，本 repo 也沒有可執行它們的多 provider fixture——這是本條目已知且刻意接受的證明力上限。一併補記反向分歧：`db-migration-8.1.13.sql:24` 建 `IX_RefreshToken_ITCode`，而 `RefreshTokenEntity.cs` 未宣告對應 `[Index]`，故腳本建的 DB 有此索引、EF 建的新 DB 沒有——由同兩份檔案的原始碼確認。**刻意不做的事**：不改 `db-migration-8.1.13.sql` 的索引命名以與 entity 對齊——改名會讓已照它建表的下游對不上，相容性優先於一致性（本 repo Red Line 的優先序）。**(2) 對全新選項寫「default flipped」。** `CHANGELOG.md` 原文為「`Configs.EnforceRequestBindingScope` default flipped to `true` (#867, P0, BREAKING)」。`git show 076cfbea7 -- src/WalkingTec.Mvvm.Core/ConfigOptions/Configs.cs` 的 diff 對該符號**只有 `+` 行、沒有 `-` 行**（`+ public bool EnforceRequestBindingScope { get; set; } = true;`），故該選項是被建立而非被翻轉，升級前狀態為「無此政策」。已改為「新增，預設 `true`」並補一段說明升級語意與回退方式的差別。下游據原措辭推論出一個從未存在的升級前值。`docs/release-adoption-ledger.md:162` 對此描述正確（「不存在（版本更早）」）。**(3) 版本歸屬錯誤。** `docs/wtm-developer-manual.md` 原文標「索引（10.17.0，#761）」；`git tag --contains 8117f9213` 只回 **`v10.18.0`**，故已更正為 10.18.0。同段上一行的 #757 標 10.17.0 經同法確認正確、未動。`CHANGELOG.md` 內的兩處 #761 索引敘述位於 `[10.18.0]` 段內，歸屬本就正確。**(4) 正確版本被 mirror 排除——已評估，結論為維持排除。** `docs/release-adoption-ledger.md` 在 `.sync/github-excludes.txt:35`，不進公開 mirror；它對 (2) 的描述正確而 CHANGELOG 錯誤，形成「錯的下游看得到、對的下游看不到」。**評估結果：排除本身正確且維持不變**——該檔記載下游（BMS）的部署版本、模組引用與可達性分類，屬於下游特定資訊，本就不該進公開 mirror。真正的缺陷是 CHANGELOG 寫錯，而非 ledger 被排除；(2) 修好之後下游已無看不到的事實，此項自行消解。**未做、且明確不主張已做**：沒有建立任何自動化機制來偵測「ledger 與 CHANGELOG 對同一版本存在性事實的陳述不一致」——本輪是人工比對發現的，下一次同類分歧不會被自動攔下。**整體範圍**：本項只改 `CHANGELOG.md`、`docs/wtm-developer-manual.md` 與本檔，無任何 `src/` 變更、無測試、無 mutant——沒有可 mutate 的行為，這個事實本身就是本條目的證明力邊界。 |

---

## Production 補強清單

若你決定上 production，按優先順序執行：

1. **加強你自己這層的測試覆蓋**：對你的業務邏輯（VM / Controller / **整合 / HTTP 層**）寫 80%+ 覆蓋，不要依賴 framework 的 20%。HTTP 端到端測試尤其重要（抓得到 #721 這類路由層問題）。
2. **CI 加 vulnerability gate**：你的 CI pipeline 加 `dotnet list package --vulnerable --include-transitive`，**任何**新 NU1903 出現就紅、必須處理。同時保留 Core.csproj + Benchmarks 的 Crypto.Xml pin（不要因 NU1510 而誤刪 — 見 [`docs/dependency-management.md`](./dependency-management.md)）。
3. **建立 framework 升級 playbook**：WTM 是小社群框架，每次升級你都要承擔回歸驗證責任。建議流程：升級 → 在你 fork 跑整套 e2e → staging 跑兩週 → production。
4. **接管 observability**：把 WTM 內建的 `IWtmLogService`、CorrelationId、SlowRequest 中介層的 log 接到你的 SIEM。特別注意 WTMContext RBAC fallback warning。
5. **走 strict CSP？追蹤 #470/#567**：#627 kill-switch 可先在 staging 稽核；生產全面啟用待 widget islandification 完成。見 [`docs/csp-hardening.md`](./csp-hardening.md)。
6. **fork 你自己的版本**：如果業務關鍵到不能讓單一上游維護者決定版本進度，建議自己 fork 一份、加自己 CI、本 fork 當 upstream。
7. **準備好退役路徑**：WTM 走的是 plain ASP.NET Core MVC + EF Core + 一些 helpers，遷移到原生 ASP.NET 不算難，但你應該把這個成本算進採用決策。

---

## 個人判斷（維護者立場）

「我自己會不會把 WTM 上 production？」

- 內部專案、SMB、後台系統 → **會**
- 外部 SaaS、有付費客戶 → **會，但會走補強清單 1-4 全做**
- 服務 > 10K DAU 或合規敏感 → **不會，會走 ABP / 原生 ASP.NET**

「WTM 比起 2026-05 的評估」：
- 地基更穩：CI flake 治本、交易正確性、god-file 可維護性、依賴/打包現代化都有實質進展
- 透明度更高：live e2e 揪出 #721 這類先前隱藏的整合層缺口，並誠實立案追蹤
- 但覆蓋率仍 ~20%、bus factor 仍 = 1、NPOI 仍靠 pin — 這三個結構性弱點沒有本質改變

**WTM 是個誠實的中型框架**。它沒有假裝自己是 ABP，沒有 oversell。「測試 pass + 漏洞掃 0」不是 production-ready 的全部證據——#721 正是活例：全綠的單元測試下藏著一個 HTTP 層的 replay-guard 失效。Production-ready 是個和你的 risk profile 對齊的決定，不是框架本身的屬性；本次重評讓這個決定所需的證據更完整、更誠實。

---

## 相關文件

- [`docs/release-adoption-ledger.md`](./release-adoption-ledger.md) — **「已修」與「已保護」的落差追蹤**：已知下游（BMS）的 repo pin／staging／production 三層版本現況、每項近期安全修復對該下游的可達性分類與證據、production 採用延遲／可達風險下降／rollback 次數這組新 KPI
- [`docs/dependency-management.md`](./dependency-management.md) — 套件版本政策、NU1510 雙意義警告、NPOI security pin 詳解
- [`docs/csp-hardening.md`](./csp-hardening.md) — #470/#627 CSP 硬化 roadmap 與 kill-switch 分級啟用
- [`docs/ci-operations.md`](./ci-operations.md) — Gitea Actions 已知不相容與排錯
- [`docs/wtm-developer-manual.md`](./wtm-developer-manual.md) — 完整開發手冊（§ 安全機制）
- [`docs/structured-logging.md`](./structured-logging.md) — 結構化 log 整合方式
- [`CHANGELOG.md`](../CHANGELOG.md) — 版本演進與每版 breaking changes（[Unreleased] 含本批次全部條目）
