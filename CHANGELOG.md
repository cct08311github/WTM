# 更新日志

## [10.21.0] - 2026-07-31

### Fixed — LookupCache RefreshAsync: DistributedLookupCacheService's timeout bypass, LookupCacheService's missing registry check (#943, #944)

Both are the two follow-up issues #804's own fix explicitly filed rather than silently fixing inline. **Full defect analysis, code comparison against #804, RED-before-fix messages, negative controls, mutation-gate verdicts, and what was deliberately left out of scope are in `docs/production-readiness.md` § "LookupCache RefreshAsync 的兩個姊妹缺陷：#943（DistributedLookupCacheService 逾時繞過）與 #944（LookupCacheService 遺漏 registry 檢查）" — this entry does not repeat or exceed those claims.**

- **#943 — `DistributedLookupCacheService.RefreshAsync<T>` computed `acquired` from the per-key stampede-protection semaphore's `WaitAsync` but never checked it before writing to the cache** — confirmed, by direct code comparison, to be verbatim the same defect #804 fixed in the sibling `LookupCacheService.RefreshAsync`. A caller that timed out did not hold the lock but proceeded to `Invalidate`/`LoadFromDbAsync`/`SetDistributedAsync` anyway, racing the legitimate lock holder's own write and able to overwrite a fresher cached value with a stale one for the entry's full TTL. Fixed the same way #804 fixed it: `if (!acquired)` now logs a warning and throws `TimeoutException` instead of proceeding — a caller-observable behaviour change from silent possibly-stale success (the same disclosure #804 already made for the sibling class). Also wired `StampedeTimeout` to the already-existing `LookupCacheOptions.StampedeTimeout` — it was still hardcoded in this class, unlike the sibling class #804 already wired; default value unchanged at 10s — needed purely so the timeout branch is testable without a real 10-second wait.
- **#944 — `LookupCacheService.RefreshAsync<T>` was missing the "unregistered (non-`[CacheLookup]`) type must never be cached" bypass its own sibling methods `GetAll`/`GetAllAsync` already have.** `SetCache<T>` only sets a TTL when the type is registered, so calling `RefreshAsync` for an unregistered type — reachable via `WTMContext.RefreshLookupAsync<T>()`, which has no upstream guard — inserted a cache entry with no TTL and no working invalidation path: a slow, unbounded memory leak. Fixed by adding the same registry-check bypass as the first statement of the method, before any locking — for an unregistered type the call is now a no-op instead of a write. `DistributedLookupCacheService.RefreshAsync` has the same structural gap but its unregistered-type fallback TTL is a bounded 30 minutes, not immortal — a lesser, out-of-scope variant, deliberately not fixed here; flagged for a possible follow-up issue rather than silently left undocumented.

Tests (TDD — RED captured against the unfixed code before implementing, not simulated after the fact): `test/WalkingTec.Mvvm.Core.Test/Cache/DistributedLookupCacheStampedeRefreshTimeoutTests943.cs` and `LookupCacheRefreshAsyncRegistryCheckTests944.cs`, each with a dedicated negative control (an operation that legitimately completes within the timeout / a registered type, respectively, both confirmed green before and after). Two new mutants (`test/mutants/entries/lookupcache943-distributed-refreshasync-acquired-guard-neutralize.json`, `lookupcache944-refreshasync-registry-guard-neutralize.json`), both `VERDICT: KILLED` / `GATE: PASS` on four independent runs total — two by the implementer, two more by an independent review pass — to rule out a timing-flaky false-deterministic mutant on #943's concurrency-based red test. `kind: security` chosen for both: a forced classification (`run_mutant.py`'s `VALID_KINDS` has no non-security option for a real production mutant, same reasoning already documented for `etl970-cancellation-classification-guard-neutralize.json`), not a claim these are traditional auth/injection vulnerabilities; pushes the security-kind entry count from 61 to 63 (verified by direct parsing of every entry's `kind` field, not assumed). Full suite: `test/WalkingTec.Mvvm.Core.Test` 4997 → 5001 passed, 0 failed (4 new tests). One pre-existing, unrelated `NETSDK1082` browser-wasm build error confirmed present on the unmodified base commit too, not a regression.

**Not verified this session**: neither fix has run on real Gitea Actions CI — this session's hard constraints forbid any Gitea/GitHub API call and forbid opening a PR, so CI verification is deferred to whenever this branch is actually opened as a PR.

### Fixed — publish-nuget.yml release-gate cross-vendor review (#925, #937)

Eight verified findings from a cross-vendor review of #925's initial release-gate implementation, fixed on the same branch, CI-only (no `WalkingTec.Mvvm.*` package code changed — nothing here affects any shipped package's runtime behaviour). **Full accounting of what is proven vs. assumed at publish time, and exactly which checks run before the first push, is in `docs/production-readiness.md` § "Release 供應鏈完整性（#925）" — this entry does not repeat or exceed those claims; that section is the ceiling, not this one.**

- **Local vulnerability scan no longer pipes into `grep -q` under `set -o pipefail`.** That pipeline could SIGPIPE-false-green on large output — `grep -q` exits (closing the pipe) the instant it matches, `echo` can then take SIGPIPE writing the rest, and pipefail's "last non-zero exit wins" rule made the whole pipeline read as success even though the marker matched. Reproduced locally with ~3MB of synthetic vulnerable-package output. Replaced with `dotnet list package --vulnerable --format json` written straight to a file and parsed structurally (`scripts/check-vulnerable-packages.py`), which fails closed (never "clean") on any output shape it does not recognise.
- **GitHub Packages artifacts are now built from a commit SHA pinned before the mirror merge-fallback branch can run**, extracted via `git archive` into a directory with no `.git` at all. The previous ordering packed directly from the live working tree, which — if a non-fast-forward push forced the `-X ours` merge fallback — could silently accept non-conflicting content (in principle including an executable MSBuild targets file) from the public GitHub mirror into what got packed.
- **Every local and sanitized-tree check now runs before the first package push**, not after. Previously Gitea packages were pushed before the GitHub-side sanitize/leak-gate/nuspec checks even ran. Added a job-level `concurrency` group (two publish runs can no longer interleave) and a version-cohort check (`scripts/check-package-cohort.py`, standard NuGet V3 protocol) that refuses to "top up" a partially-published version with artifacts from a different commit rather than treating `--skip-duplicate` as sufficient.
- **`scripts/publish-to-gitea.sh`'s real (non-`--dry-run`) publish path is disabled.** It packed only 3 of the 6 published packages (never WorkFlow/Etl/FileHandlers.S3) and ran none of the gate above. It was previously documented as the runner-outage fallback in `docs/ci-operations.md` and `docs/gitea-packages.md`; both corrected to point at the tag-object-dedup recovery SOP instead.
- **Release ref/SHA integrity is now enforced in-workflow.** A `workflow_dispatch` must target the `dotnet10` branch itself (not an arbitrary dispatch `ref`); a tag's peeled commit must be on `origin/dotnet10`'s history (ancestor-of-tip, chosen deliberately over equal-to-tip so the existing tag-object-dedup recovery SOP in `docs/gitea-packages.md` keeps working). An existing GitHub tag is now only force-moved when its tree is byte-identical to what is about to be published — previously unconditional.
- **The Etl smoke fixture (`test/smoke/publish-nuget-fixture/Program.cs`) was rewritten to use runtime `System.Reflection` assertions** against the 13 members changed by #883, replacing compile-time-only calls that could not actually prove optionality, parameter order, requiredness, or `virtual`-ness despite an earlier version of this fixture's comment claiming all four. The smoke steps now build under `$RUNNER_TEMP` (never inside the git checkout, so they cannot leak into the public mirror) with an isolated `NUGET_PACKAGES` and a candidate-only `NuGet.Config` — closing a same-version-re-run scenario where a stale shared package cache could mask a real signature regression, reproduced locally while building this fix.
- **`scripts/reconcile-release-version.sh`** now rejects a `workflow_dispatch` `version_suffix` containing anything outside `[0-9A-Za-z.-]` (closes a `$GITHUB_OUTPUT` injection vector a multi-line suffix could otherwise exploit) and validates the CHANGELOG heading date is a real calendar date, not merely `YYYY-MM-DD`-shaped. `is_prerelease` (already computed by that script) is now threaded into the GitHub Release payload instead of the payload hardcoding `"prerelease": false` unconditionally, which would have created a stable Release for an RC tag.

### Fixed — check-package-cohort.py probed existence with HEAD, which Gitea always answers 405 (#967, release-blocking)

`scripts/check-package-cohort.py` (introduced by #925/#937, above) probed whether a package version already exists by issuing `HEAD` against the registry's flat-container URL. **Gitea's NuGet endpoint returns `405 Method Not Allowed` for `HEAD` regardless of whether the version exists**; the script only special-cased `404` as "does not exist" and re-raised everything else fail-closed, so a `405` on an *existing* version was indistinguishable from a real error — the gate errored out on every single publish, RC and stable alike, not just partial-cohort ones. Verified live against the real mac-mini Gitea registry: the unfixed script against `WalkingTec.Mvvm.Core 10.18.0` (a version that IS published) returned `ERROR: could not check existence of WalkingTec.Mvvm.Core 10.18.0: HTTP Error 405: Method Not Allowed` (exit 2).

**Fix**: the probe now issues `GET` instead of `HEAD`. Because `GET` returns the full `.nupkg` body, `package_exists()` no longer reuses the `fetch()` helper (which buffers the whole response) — it opens the connection directly and reads at most 1 byte off a 2xx response before closing it, so a multi-MB package is never loaded into memory just to check existence; the existing `404`-vs-everything-else fail-closed branch is otherwise unchanged. Re-verified live against Gitea after the fix: `WalkingTec.Mvvm.Core 10.18.0` (exists) → correctly reported published; `WalkingTec.Mvvm.Core 10.21.0-rc.1` (does not exist) → correctly reported not-yet-published; the full six-package cohort check invoked exactly as `publish-nuget.yml` calls it, with `PKG_VERSION=10.21.0-rc.1`, now passes (`0/6 already published`, clean).

**GitHub Packages status — verified in part, not claimed complete.** Unauthenticated probing of `nuget.pkg.github.com` (both its service index and a plausible flat-container download URL) confirmed it also rejects `HEAD` with `405`, the same failure mode as Gitea, so `GET` is the correct fix for both registries this workflow publishes to, not a fix tuned to one. What was **not** verified: GitHub Packages' authenticated `GET` correctly distinguishing `200` (exists) from `404` (absent) — no `GH_MIRROR_PAT` was available this session. That half remains an assumption resting on standard NuGet V3 protocol compliance until the next real tag publish exercises it. Full accounting in `docs/production-readiness.md` § "check-package-cohort.py 的 HEAD→GET 修復（#967）" — this entry does not claim more than that section verifies.

Tests: `test/check-package-cohort-tests.sh` gained a second fixture HTTP server with a custom handler whose `do_HEAD` always returns `405` (modeling Gitea/GitHub's real behavior — the pre-existing fixture used Python's `http.server` default handler, which answers `HEAD` correctly and is exactly why this defect was never caught before) and whose `do_GET` serves `200`/`404` normally plus a reserved version that returns `500` to pin fail-closed behavior for a genuine non-`404` error distinct from the `405`-masking issue. RED-before-fix reproduced independently against the new test case with the unmodified `HEAD`-based script: `ERROR: could not check existence of WalkingTec.Mvvm.Core 10.21.0: HTTP Error 405: Method Not Allowed`, `FAIL: ... expected exit 0, got 2`. All 7 cases (4 pre-existing + 3 new) pass after the fix.

### Fixed — ETL batch retry-with-backoff misclassified a cancellation that raced a transient failure as a data failure (#970)

`EtlPipelineExecutor.BulkLoadWithRetryAsync`'s retry catch used `catch when (attempt < maxRetries && !cancellationToken.IsCancellationRequested)`, a filter evaluated at the instant the loader throws a transient failure. When cancellation was already requested at that exact instant, the filter evaluated false, so neither that catch nor the `OperationCanceledException`-only catch above it matched — the transient exception's own type propagated untouched to the caller's general `catch (Exception ex)`, reporting an operator-cancelled run as an ordinary data failure (`Aborted=false`, a sanitized data-error `ErrorMessage` rather than `"Job was aborted"`). Full branch table (5 interleavings, before/after) in `docs/production-readiness.md` § "ETL 批次重試 backoff catch 把「cancellation 恰好撞上 transient 例外」誤判成資料失敗（#970）" — this entry does not claim more than that section verifies.

**Fix**: the retry catch no longer filters on cancellation state before matching. It catches unconditionally, checks `cancellationToken.ThrowIfCancellationRequested()` FIRST — converting an already-requested cancellation into `OperationCanceledException` right there, so it converges on the same outer `catch (OperationCanceledException)` the pre-existing Task.Delay-cancellation path already uses — and only then falls back to the unchanged `attempt >= maxRetries` give-up check for a genuine (non-cancelled) data failure. The outer `catch (OperationCanceledException)` itself was not widened to catch more exception types, which would have made genuine data failures misreport as aborted too — the mirror image of this same defect.

Tests: `RetryWithBackoffTests.cs` replaced its only cancellation test's wall-clock race (`cts.CancelAfter(100)` against a 1000ms base delay — itself the actual flake source: full-jitter backoff can let an unlucky low jitter roll fire many retries inside that window, occasionally cancelling exactly at a throw instant instead of during a delay) with a deterministic signal: `MockBulkLoader` (`src/WalkingTec.Mvvm.Etl/Testing/MockBulkLoader.cs`, the framework-shipped test helper) gained an `OnBeforeTransientFailureThrown` event fired synchronously immediately before it throws its simulated failure. Two interleavings are now tested separately: cancellation during the backoff delay (existing test, rewritten to await the throw signal via `TaskCompletionSource` before cancelling), and cancellation already requested at the instant of the throw (new test, cancelling synchronously inside the event handler) — RED-before-fix on the latter: `Assert.IsTrue failed. Cancellation already requested when the transient exception is thrown must still surface as Aborted.` A negative control was added to the existing retry-budget-exhausted test (`Assert.IsFalse(result.Aborted, ...)`) so a fix that set `Aborted` unconditionally would not pass. `RetryWithBackoffTests` run 50 times consecutively: 50/50 green, 0 flakes. Mutant `test/mutants/entries/etl970-cancellation-classification-guard-neutralize.json` (removes the `ThrowIfCancellationRequested()` call): `VERDICT: KILLED`. **`kind` chosen: `security`, and why** — this is a classification/observability correctness defect, not a traditional security vulnerability (no unauthorized access, injection, or tenant-isolation issue); `run_mutant.py`'s `VALID_KINDS` only supports `security`/`selftest`, and `selftest` is reserved for testing the runner itself, not real production mutants — `security` was the only functional option to have this mutant CI-enforced. This pushes the security-kind entry count from 60 to 61, widening the pre-existing #968 timeout-derivation gap (the gate's per-run budget formula was derived for 45 entries); #968 itself is not addressed by this fix.

### Changed — mutation-gate.yml: per-entry relevance selection on PRs, unconditional full set on merge (#968)

CI-only; no `WalkingTec.Mvvm.*` package code changed. `.github/workflows/mutation-gate.yml`'s `mutants` job used to run every `kind: security` entry under `test/mutants/entries/` on every pull_request, sized by a hand-recomputed step-level `timeout-minutes` budget (`entries * 73s * 1.3 / 60 * 1.25`, documented in that step's own comment). That budget has now drifted stale three times as the registry grew — "roughly 30-35 mutant entries" (pre-#926), then 45 (#926, itself already cross-vendor-review-corrected once), then 61 as of #970 above. Full derivation, the two-part fix, and the honest limitation of the new positive control are in `docs/production-readiness.md` § "mutation-gate.yml 預算第三次漂移改為根因修復：per-entry selection 取代「每次 PR 跑全部 entries」（#968）" — this entry does not claim more than that section verifies.

**Part 1 (mechanical)**: the step/job `timeout-minutes` were recomputed for 61 entries using the existing documented formula — step-level 90 → 125 minutes, job-level 100 → 135 minutes — and the stale "currently 45" comment was corrected.

**Part 2 (root cause)**: `test/mutants/gate_lib.py` gained `select`/`select_relevant_entries`/`relevance_self_check`/`resolve_changed_files`/`reconcile`. On `pull_request`, the `mutants` job now runs only the entries whose own `target_file`/`test_project`/test-source declarations (or the always-relevant workflow file / `test/mutants/**` paths) the PR's diff actually touches — `select_relevant_entries()` is built entirely on the pre-existing `is_change_relevant()` (called with a one-entry list), never a second relevance implementation. On `push` to `dotnet10`, the full 61-entry set still runs, **unconditionally** — that code path does not call the diff-resolution function at all, so it cannot be skipped by a diff that happens to compute as irrelevant. **This reduces mutation-gate coverage per individual PR by design**: a single PR's own gate run now only proves the entries selected for that PR's diff, not the whole registry; full-registry coverage is restored on every merge to `dotnet10`, not on every PR — this is a deliberate trade-off (documented in the workflow file itself), not an incidental side effect, and it is not "the same coverage, just faster."

**The one thing that had to not go wrong**: a selection that computes empty must not read the same as "everything ran and passed" (issue #855 defect 4's concern, one level up). `select` distinguishes the two possible causes of an empty result: before returning empty, it runs `relevance_self_check()` against the real, currently-loaded registry (not a synthetic fixture) — confirming the workflow file's own path is still judged relevant (the `ALWAYS_RELEVANT_EXACT` branch) and that the first discovered entry's own `target_file` is still judged relevant (the `target_file` branch). Both hold → the diff genuinely touches nothing gated; `select` exits 0 with an empty id list and an explicit log line explaining why; the `changes` job reports `has_selection=false` (not a failure); `mutants`/`meta-selftest` are skipped; `gate` treats the skip as a genuine pass and says so in its own log. Either check fails → `select` exits 2; the `changes` job step hard-fails (`exit 1`, no fail-open here); `gate`'s existing `is_ok()` check turns a failed `changes` job into `MUTATION_GATE_RESULT: FAIL`. The `gate` job's own reconciliation also moved from "discovered vs executed" (issue #855 defect 4's original check, which only worked because every discovered entry always ran) to **"selected vs executed"**, evaluated as inline bash inside the `gate` job itself.

**Postmortem (#973, found on this branch's own first CI run): that reconciliation briefly shelled out to `python3 test/mutants/gate_lib.py reconcile`, and the `gate` job has no `actions/checkout` step.** `changes`/`mutants`/`meta-selftest` all reported success with a fully reconciled 61-selected/61-executed count, and `gate` still failed — `python3: can't open file '.../test/mutants/gate_lib.py': No such file or directory` — because the repository was never checked out in that job. This is architectural, not incidental: `gate` is the one job that must unconditionally post a status every run (see its own header comment), and every other step in it is pure bash/arithmetic over `needs.*.outputs`; adding a checkout there to fix one call site would work against the reason the job needs nothing else from the tree. **Fix**: the comparison reverted to inline bash (the same `!=` check `#855` defect 4 originally used, just against "selected" instead of "discovered"). `gate_lib.py`'s `reconcile()` function and CLI subcommand are unchanged and still covered by their own selftest — kept as a documented, independently-tested reference spec and for manual diagnostic use — but `gate` does not call them. **Two implementations of the same rule now exist, deliberately**: judged acceptable because the rule itself is a single integer comparison plus a formatted message, unlike `is_change_relevant()` (real matching logic that could genuinely drift if duplicated). A repo-wide sweep (every `run:` step in every job, across all 7 workflow files) found this was the only job anywhere invoking a repo-relative script without a checkout. New `test/mutants/_selftest/selftest_gate_job_reconciliation.py` extracts and runs the `gate` job's actual script — never a hand-copied duplicate — under match/mismatch/skip/failure scenarios, including from a directory with no repository checked out at all, reproducing the #973 failure on demand; wired into the `changes` job's existing selftest guard step alongside the other three.

**Stated limitation, not claimed away**: `relevance_self_check()` proves the relevance machinery is not *completely* broken (two of `is_change_relevant()`'s branches, exercised against real data); it does not prove every individual entry's own relevance judgment is correct. A regression narrow enough to misjudge only some entries (others in the same kind still select correctly) — or one isolated to only the `test_source`/`test_project` branches this inline check does not exercise — is not guaranteed to zero out the whole selection and therefore is not guaranteed to trip this specific check. Those two untested branches are covered instead by `test/mutants/_selftest/selftest_relevance_covers_tests_and_workflow.py`, an issue #855/PR #858 script that existed but was never wired into any CI workflow until this change — closed here alongside `selftest_kind_validation_and_accounting.py` (same gap) and a new `selftest_select_relevant_entries.py`, all three now run as a `changes`-job guard step on every PR, not asserted once in a PR body.

**`kind` classification (considered, deferred)**: #970's `etl970-cancellation-classification-guard-neutralize` (above) had to be labelled `security` — the only enforceable option — despite being a correctness-only defect, because `run_mutant.py`'s `VALID_KINDS` is `{security, selftest}` and `selftest` is reserved for testing the runner itself. Once selection is in place, `kind` becomes classification-only (it no longer decides whether an entry runs), so adding a `correctness` kind would be cheaper now than before — but `mutants` still only selects `kind == 'security'`, so actually adding one would require changing that filter's semantics in the SAME PR that just changed the diff-selection semantics. Deferred to a follow-up rather than compounding two selection-logic changes in one PR.

Verification: `python3 test/mutants/gate_lib.py ids --kind security | wc -l` → 61; `dotnet build WalkingTec.Mvvm.sln` (one pre-existing, unrelated failure confirmed identical on unmodified `origin/dotnet10` — `NETSDK1082`, missing `browser-wasm` runtime pack for `BlazorDemo.Client`, a local workload gap, not a regression); `python3 test/mutants/_selftest/selftest_select_relevant_entries.py` (new, 8/8 cases pass) plus the two now-wired pre-existing selftests, all passing; `python3 test/mutants/run_mutant.py --mutant 953-getbatchquery-wherereplacemodifier-reintroduce` → `VERDICT: KILLED` / `GATE: PASS` and `python3 test/mutants/run_mutant.py --mutant _selftest-empty-red-tests-invalid --expect-verdict INVALID_MUTANT_EMPTY_RED_TESTS` → `PASS`, both run to confirm individual mutant execution (unchanged code in `run_mutant.py`) is unaffected — not all 61 entries were re-run, since neither of those two ever call the changed `gate_lib.py`/workflow code. **Not exercised on real Gitea CI**: this session's hard constraints forbid any Gitea/GitHub API call and forbid opening a PR, so the workflow's actual behaviour on a real pull_request/push event (job scheduling, `GITHUB_OUTPUT` propagation, the `changes`→`mutants`/`meta-selftest`→`gate` job chain end-to-end) is verified by local YAML parsing, `scripts/audit-workflow-timeouts.py`, and direct invocation of the underlying Python — not by a live CI run.

### Security

- **`_FrameworkController.Selector`'s `Ids` branch stripped row-level `DataPrivilege` authorization; `BasePagedListVM.GetBatchQuery()`'s default (no explicit `ReplaceWhere`) path had the same defect (#947).** `Selector` restricts its "already selected" chip display to a caller-supplied `Ids` list by setting `SearcherMode = Batch` and `ReplaceWhere = Ids.GetContainIdExpression(...)`. `DoSearch()`/`DoSearchAsync()` then run the query through `WhereReplaceModifier`, which deletes **every** `Where` node in `GetSearchQuery()` — including row-level `DataPrivilege` applied via `DPWhere` — before rebuilding with only the `Ids` restriction; `WhereReplaceModifier` cannot distinguish a UI search predicate from an authorization predicate, both are ordinary `Queryable.Where` calls in the expression tree. Any authenticated caller (`[AllRights]`; anonymous too when the non-default `ConfigInfo.AllowUnauthenticatedSelector` is set) could supply an arbitrary VM name, an arbitrary `Ids` list, and an arbitrary `_DONOT_USE_VFIELD`, and receive rows their `DataPrivilege` grant does not cover. #867 fixed a sibling call site (`GetPagingData`'s missing `SearcherMode` re-pin) and documented that `Selector`'s own use of `Batch` was intentional (a selector must show already-chosen rows regardless of the current search box) — but never audited what that intentional `ReplaceWhere` assignment itself deleted. **Scope**: evidence points to a same-tenant, row-level `DataPrivilege` bypass. `ITenant`'s EF Core global query filter is a `HasQueryFilter`-based EF-internal rewrite, not a `.Where()` expression node, and is not affected by `WhereReplaceModifier` — cross-tenant reads were not found to be affected, and this fix does not claim to have proven their absence beyond what's testable here.
- **Fix**: `BasePagedListVM` gained a private `GetAuthorizedIdsQuery` that swaps in a blank, unbound `Searcher` for the duration of a `GetSearchQuery()` call, then ANDs the `Ids` restriction on top as an ordinary `Where` — nothing is ever deleted from the expression tree. `GetBatchQuery()`'s own default branch (used whenever `SearcherMode = Batch` is set without an explicit `ReplaceWhere`) now calls this helper; `Selector` no longer sets `ReplaceWhere` at all (`SearcherMode` was already `Batch`), so it reaches the same fixed branch through the ordinary `GetDataJson()` → `DoSearch()` → `GetBatchQuery()` pipeline with **no new public API** — an adversarial review of the original fix found the first version's separate `PopulateSelectedEntities` method (added to `BasePagedListVM` and to the public `IBasePagedListVM` interface) unnecessary, and that its `DoSearch()` bypass silently skipped `GetSearchCommand()`-backed ListVMs (raw SQL/stored-procedure sources) and `Searcher.SortInfo`; removed. `WhereReplaceModifier`/`ExpressionVisitors.cs` itself is untouched — `Export`/`MasterDetail`/any host app's own direct `ReplaceWhere` usage is unaffected (and, precisely because it is untouched, remains a live DataPrivilege-stripping path for any host app that sets `ReplaceWhere` directly — a pre-existing, documented-public-API gap this fix does not close).
- **Known compatibility limitation, found and verified during the same review (not a security regression — both fail closed):** the blank-`Searcher` swap only reliably suppresses a `Where` added through a guard-then-add helper (`CheckContain`/`CheckEqual`/`CheckWhere`, all gated on the Searcher's bound value). A `Where` that reads `Searcher` directly inside a LINQ lambda, not through such a helper, is not reliably suppressed — verified with a runnable reproduction against two shapes present in this repo's own demo tree: a lazily-evaluated closure (`MajorDetailListVM`'s `.Where(x => Searcher.SchoolId == x.SchoolId)`) is read at query-enumeration time, after the real Searcher has already been restored, so the live UI search criteria is applied anyway; an eagerly-evaluated build-time branch (`CityChildrenDetailListVM`'s `if (Searcher.ParentId == null) return emptyList;`) always takes the empty-query branch under the blank Searcher, so the result is always zero rows regardless of the real Searcher or the requested `Ids`. Both are pinned by new regression tests (`BlankSearcherShapeTests953.cs`) against fixture ListVMs mirroring each shape; `BasePagedListVM.GetAuthorizedIdsQuery`'s XML doc states the corrected, narrower invariant.
- **Enumeration corrected and widened to the whole tree** (was `src/`-only, three sites missed): `grep -rn "SearcherMode = \|\.ReplaceWhere =" src demo test --include="*.cs" --include="*.txt" | grep -v "/obj/\|/bin/" | grep -E "Batch|CheckExport|ReplaceWhere"` plus a supplementary `grep -rn "CheckExport" src --include="*.cs" --include="*.txt"` for multi-line ternaries — 13 distinct `src/` sites (was 10): the previously-missed three are `Helper/FileExtension.cs`'s public `GetExportData<T>()` extension (called by 52 files/92 call sites across the demo tree) and the two `GeneratorFiles/Spa/Controller.txt`/`Blazor/Controller.txt` code-generator templates shipped in the NuGet package — every scaffolded downstream controller's `ExportExcelByIds` action is this exact pattern. All non-comment, non-`Selector`/`GetPagingData` `src/` sites resolve through `GetBatchQuery()`'s now-fixed default branch, so this fix's actual blast radius is every generated controller in every downstream app, not seven framework call sites — full table in `docs/production-readiness.md`'s #947 entry.
- Tests: `test/WalkingTec.Mvvm.Api.Test/SelectorDataPrivilegeTests947.cs` — `Selector_...`, a real HTTP integration test through `DemoWebApplicationFactory` asserting a `DataPrivilege`-restricted caller does not receive an unauthorized row named directly in `Ids`, does receive an authorized one (positive control), and that the current UI search criteria is still ignored; `GetExportExcel_...`, added because reverting `GetBatchQuery()`'s default branch alone left the whole suite green (the `GetExportExcel`/`GetExportExcelStream` half of this fix had zero coverage) — parses the returned `.xlsx` with NPOI and asserts the unauthorized row's name does not appear in any cell, the authorized one's does. Two mutants (`test/mutants/entries/`), both `VERDICT: KILLED` / `GATE: PASS`: `947-selector-populateselectedentities-replacewhere-reintroduce` reverts `_FrameworkController.cs`'s `SelectorValueField` assignment back to the pre-fix `ReplaceWhere` assignment (red test: `Selector_...`); `953-getbatchquery-wherereplacemodifier-reintroduce` reverts `BasePagedListVM.GetBatchQuery()`'s default branch back to the pre-fix `WhereReplaceModifier` rebuild (red test: `GetExportExcel_...`, proving that half of the fix independently).
- **`JwtOption.SecurityKey`'s padding let demo-shipped and documented weak keys pass the startup guard — anyone who can read this repository (including its public GitHub mirror) could forge a valid access token for any `ITCode` (#923, P0, BREAKING).** `SecurityKey`'s setter (`JwtOptions.cs`) pads any value shorter than 32 chars with `'x'` — that padding itself is unchanged and still exists so a direct `new JwtOption { SecurityKey = "x" }` construction, or a console/ETL host that never calls `AddWtmAuthentication`, does not crash signing with `IDX10720`. The bug was that the pre-#923 startup guard (`IsDefaultOrWeakKey()`) only compared the **already-padded** value against the literal well-known default, never inspecting what was actually configured — so the demo's `"super"` (`demo/WalkingTec.Mvvm.Demo/appsettings.json`), `"superSecretKey@345"` (Vue3Demo/BlazorDemo), and the developer manual's `"your-256-bit-secret-key-here-min-32-chars!"` placeholder all padded out to 32 characters and sailed through — every one of those values has been publicly committed to `cct08311github/WTM`.

  **Fix**: `JwtOption` gained a `_rawSecurityKey` backing field (the `SecurityKey` setter now also stores the pre-padding value; the padding logic itself was not touched by even one character — `SecurityKey_short_value_is_padded_to_32`/`SecurityKey_already_32_chars_is_not_changed` stay green as the proof) and `IsWeakSigningKey(out string? reason)`, the real security invariant: weak when the raw value is null/whitespace, shorter than 32 UTF-8 bytes (256 bits — the `IDX10720` floor for HMAC-SHA256), or matches `KnownPublicKeys` regardless of length (the manual's 42-byte placeholder is otherwise long enough to pass a length-only check). `IsDefaultOrWeakKey()` is renamed to `IsFactoryDefaultKey()` (behaviour unchanged — still only the literal CLR default) and kept as an `[Obsolete]` alias for source compatibility; `WtmConfigValidationExtension`'s `IsJwtActive` activation check uses `IsFactoryDefaultKey()` while its `SecurityKey` validator uses `IsWeakSigningKey()` — the two predicates are deliberately not merged, or `SecurityKey="super"` with Issuer/Audience still at their localhost defaults would make `IsJwtActive` evaluate false and skip the very validator meant to catch it.

  **Startup gate** (`FrameworkServiceExtension.AddWtmAuthentication`): a weak key throws `InvalidOperationException` in every non-Development environment (unset, the literal default, and every demo/documented key — the demo key case is the breaking change here; the migration is one config line plus `openssl rand -base64 32`). A too-short **custom** key not on the blocklist throws even in Development — an operator who set a short key made a deliberate, mistaken choice, and that belief has to break locally rather than first in production. Development is detected via a registered `IWebHostEnvironment` first (covering `ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT`/`--environment`/launchSettings profiles, since ASP.NET Core's own hosting layer already resolves all of those into one value), falling back to reading the environment variables directly when no `IWebHostEnvironment` is registered; with neither available, the gate fails **closed** (treated as non-Development, not Development). In Development with an unset/blocklisted key, a random 256-bit key is generated once per process and pushed into **both** the local `config.Get<Configs>()` snapshot `AddWtmAuthentication` uses to build the `JwtBearerOptions.IssuerSigningKey` **and** the separately-bound `IOptionsMonitor<Configs>` object graph `TokenService` reads from (via `services.PostConfigure<Configs>`, conditional on the key still being weak at that point — not unconditional, see the design-gate-review correction below) — without that second step the app would sign with one key and validate with another, rejecting every token it issues.

  **Design-gate review correction**: the `PostConfigure<Configs>` assignment above was originally unconditional (`c.JwtOptions.SecurityKey = generatedKey`, no guard). A host that also registers its own `services.Configure<Configs>(o => o.JwtOptions.SecurityKey = "...")` code delegate — reading a real key from a secret manager, with nothing in raw `IConfiguration` — has that value applied to the same `Configs` instance before `PostConfigure` runs (every `Configure` action runs before every `PostConfigure` action), so the unconditional form silently discarded the operator's real key and replaced it with a fresh random one on every restart, with no diagnostic explaining why. Now conditional on `IsWeakSigningKey()` still being true at that point, so the generated key is a fallback rather than an override; `IOptionsMonitor<Configs>` correctly resolves the operator's code-delegate key (`AddWtmAuthentication_DevelopmentCodeDelegateStrongKey_IOptionsMonitorResolvesOperatorKey_NotGeneratedKey`, verified red against the old unconditional form before the fix landed). This does **not** close the whole gap: the local `conf`/`jwtOptions` snapshot `AddWtmAuthentication` uses earlier to build `JwtBearerOptions.IssuerSigningKey` still cannot see that same code delegate (`config.Get<Configs>()` never could), so in this exact scenario `TokenService` now signs with the operator's real key while the handler still validates with the generated one — every login fails, pinned by `AddWtmAuthentication_DevelopmentCodeDelegateStrongKey_JwtBearerHandlerStillUsesGeneratedKey_NotOperatorKey` as the known, deliberate current behaviour. Closing that fully requires changing what `AddWtmAuthentication` reads from — the #753-family split-brain, still not fixed here.

  **`WTMContext.User.cs`'s two independent `_remotetoken` signature-validation paths** (the sync `LoginUserInfo` getter and the async `EnsureLoginUserInfoAsync` — Core cannot assume any host calls the Mvc-layer guard above) both gained the identical `IsWeakSigningKey` fail-closed check before constructing `TokenValidationParameters`: a token that verifies against a weak key proves nothing, since anyone who knows that key could forge the same signature.

  **Cross-vendor review of #923 itself (PR #931) found a laundering bug in the design, not just the implementation, plus seven further hardening items.** (1) **Raw/effective split**: the original `SecurityKey` property's GETTER returned the padded value, so a getter→setter round-trip — or any `System.Text.Json` serialize/deserialize cycle, which only ever sees the public getter — silently turned a weak raw key into a not-weak one (same HMAC bytes, flipped `IsWeakSigningKey` verdict). Fixed by splitting the property: `SecurityKey` now round-trips the raw value unchanged and never pads; a new `EffectiveSecurityKey` computes the padded HMAC material on read. All four production `SymmetricSecurityKey` sinks (`TokenService.cs`, `FrameworkServiceExtension.cs`, both `WTMContext.User.cs` paths) now read `EffectiveSecurityKey`; `IsWeakSigningKey`/`HasLowCharacterDiversity`/`IsFactoryDefaultKey` read `SecurityKey`. (2) **Blocklist gaps**: `test/` is not excluded from the public GitHub mirror sync, so a fixed test-fixture literal is exactly as public as a demo config value — found four more 32+-byte fixed `SecurityKey` literals (three added by #923's own commits) plus three more via a repo-wide sweep for the same pattern. All seven are now permanently in `KnownPublicKeys` (blocklisted forever, even though no test uses them anymore — deleting a literal from the tree does not un-publish it from git history/the mirror) and the tests that used them now generate a random key per run (`JwtTestKeys.StrongCustomKey`, `TokenTestFixture.GeneratedSecurityKey`). New `scripts/check-jwt-key-literal-blocklisted.py` (modeled on `scripts/check-gitea-token-not-sourced.py`, #924/#929) fails CI when a new fixed 32+-byte `SecurityKey` literal appears anywhere in the tree without being blocklisted — wired into `mutation-gate.yml`'s unconditional `changes` job. (3) **PostConfigure swallowed a short custom key**: the conditional PostConfigure from the design-gate review above substituted the ephemeral key whenever `IsWeakSigningKey()` was still true — including for `WeakReasonTooShort`, which design row (d) requires to be rejected in EVERY environment with no fallback. Now excludes `WeakReasonTooShort` explicitly, so a too-short custom key set via a second `Configure<Configs>` delegate surfaces as a `TokenService` construction failure (item 5) instead of silently booting. (4) **`IsFactoryDefaultKey` read the padded field**: a 19-byte custom key like `"wtmwtmwtmwtmwtmwtmx"` padded to the exact same 32-character string as the padded default, so the old check (comparing against the padded backing field) misclassified it as the factory default, making `IsJwtActive` evaluate false and skip the weak-key validator entirely. Now a plain comparison against the raw `SecurityKey` — no padded form exists to collide against since (1) removed padding from that property. (5) **`TokenService` didn't enforce the invariant at the sink**: the only guard lived in `AddWtmAuthentication`; a console/ETL host constructing `TokenService` directly (exactly `TokenTestFixture.cs`'s own shape) never ran it. `TokenService`'s constructor now calls `IsWeakSigningKey` itself and throws `InvalidOperationException` naming the offending config key before any signing can happen — fail-closed at the actual point of use, not just at one optional entry path.

  **No opt-out flag.** Every configuration an escape hatch would preserve is one where the signing key is either below the algorithm's own minimum or already public — there is no legitimate deployment shape that setting survives, so it would only be one extra step between an operator and the vulnerability, not a real accommodation.

  **Upgrading is not the same as rotating.** Installing this fix makes an unset/weak/demo key refuse to boot going forward; it does **not** invalidate any access token already signed with a key that was public before you upgraded. If your `SecurityKey` was ever `"super"`, `"superSecretKey@345"`, the manual's placeholder, or the CLR default, treat it as compromised and rotate to a real random value regardless of whether you were ever forced to by this gate. Rotation itself is cheap: refresh tokens are random 64-byte rows in the `RefreshTokens` table (`TokenService.GenerateRefreshTokenString`), not JWTs — changing `SecurityKey` does not invalidate them. **Rotation requires a restart of every instance that validates tokens, not just a config edit.** `JwtBearerOptions.IssuerSigningKey` is captured once, at `AddWtmAuthentication`'s `ConfigureServices`-time call, from a `config.Get<Configs>()` snapshot that is never re-read afterward — a config change alone (an edited `appsettings.json`, even one with `reloadOnChange: true`, or an updated secret-manager value) does not reach it. A running instance keeps validating tokens signed with the OLD key, and issuing new ones through `TokenService` with whichever key `IOptionsMonitor<Configs>` resolves to at that moment (which MAY be live-updated depending on the configuration source — the two are independently bound, see the design-gate review correction above), until that instance restarts. In a multi-instance deployment, "restart" means every instance; a rolling restart leaves a window where some instances still validate against the old key. If you have reason to believe forgery already happened, rotation alone is not containment: also revoke the affected `RefreshTokens` rows and audit accounts/role grants created while the weak key was in effect.

  (6) **Test-evidence gaps**: the blocklist assertions didn't bind independently of the length guard — deleting a blocklist entry, or breaking `Trim()`/case-insensitivity, would still leave those tests green. New isolation tests use a blocklisted value that is ALSO >= 32 bytes on its own (the manual's 42-byte placeholder, plus whitespace-padded and uppercased variants) so only the blocklist clause (or only `Trim()`, or only `OrdinalIgnoreCase`) can make each one pass. The generated ephemeral key's unpredictability was untested — replacing `RandomNumberGenerator.GetBytes(32)` with `new byte[32]` would have passed every existing test; a new test asserts two independently generated keys differ from each other and from the all-zero constant, backed by a fifth mutant (below). The tampered-signature regression in `EnsureLoginUserInfoAsyncTests.cs` flipped the LAST Base64url character of a token's signature, which can land on unused padding bits and leave the decoded signature bytes unchanged depending on the original character — now decodes the signature, XORs the FIRST byte, asserts the decoded bytes actually differ, then re-encodes, so the test is guaranteed to exercise a genuinely different signature. (7) **Docker/non-Development launch profiles**: `Dockerfile` sets `ASPNETCORE_ENVIRONMENT=Production` with no `SecurityKey` in the demo's `appsettings.json` (per this fix), so `docker build && docker run` now refuses to start until one is supplied — correct behaviour (there is no legitimate reason for a container's own baked-in default to satisfy this), but previously undocumented. The `Dockerfile` now carries a prominent comment with concrete injection examples (plain env var, a Docker secret via an entrypoint wrapper, a Kubernetes `Secret`) rather than silently downgrading the image's own environment to Development, which would have generated a throwaway per-process key and invalidated every access token on every container restart instead of surfacing the requirement. (8) **Documentation corrections** (this entry): the `docs/production-readiness.md`/`CHANGELOG.md` test-count mismatch below is fixed to the real re-run count; the rotation-invalidates-tokens claim above now states its restart precondition; the "complete sink sweep" wording below is scoped to what the grep command actually proves; the `256 bits — the IDX10720 floor` phrasing is not, and was not intended to be, a claim that any 32-byte key has 256 bits of actual entropy — `JwtOptionTests` itself accepts a 32-byte string of one repeated character, or 11 repeated CJK characters, as `IsWeakSigningKey() == false` (`HasLowCharacterDiversity` is the separate, warn-only signal for that case).

  Demo `appsettings.json` (LayUI/Vue3/Blazor) had `JwtOptions.SecurityKey` deleted outright, not replaced with a stronger value — any value committed to this file becomes public the moment it merges. The three files' `CookieOptions.SecurityKey` lines were also deleted; `CookieOption` has no such property, so they were dead configuration that only republished the same leaked string a second time. `docs/wtm-developer-manual.md`'s example `appsettings.json` and its three JWT-related table rows were updated to match. Tests: `JwtOptionTests` (full truth table — unset/default/padded-default/blocklisted keys/whitespace-and-case blocklist bypass/31 vs. exactly-32 vs. 33 vs. 11-CJK-char byte boundaries/low-character-diversity warn-only signal/raw-effective round-trip, including a `System.Text.Json` round-trip proving the weak verdict survives serialization/deserialization), `JwtAuthenticationStartupGateTests923` (population × environment matrix including the fail-closed no-environment-info case, the config-graph wiring check, the two code-delegate PostConfigure tests, the too-short-code-delegate case, and the ephemeral-key unpredictability test), `WtmConfigValidationTests`'s new coherence regression, `RemoteTokenWeakKeyFailClosedTests923` (both `WTMContext.User.cs` paths, using tokens that verify against the padded key to prove the guard fires before signature validation, not because of it), and `TokenServiceTests`' three new constructor-guard tests — full run: `4820 passed, 0 failed` (`test/WalkingTec.Mvvm.Core.Test`); `WalkingTec.Mvvm.Api.Test`: `100 passed, 1 skipped` (the pre-existing mutation-gate baseline selftest fixture), `0 failed`, including `AuthApiTests` exercising a real HTTP login against the demo app under Development with no `SecurityKey` configured. Five mutants under `test/mutants/entries/jwt923-*.json`, each `VERDICT: KILLED` / `GATE: PASS`: `jwt923-rawkey-padding-bypass-reintroduce` (reads `EffectiveSecurityKey`, the always-padded property, where `IsWeakSigningKey` must read the unpadded `SecurityKey` — rebuilt against the #931 raw/effective split; the #923-era patch targeted a field pair that split removed), `jwt923-weak-key-length-guard-neutralize`, `jwt923-blocklist-demo-keys-neutralize`, `jwt923-devgen-environment-guard-neutralize`, and the new `jwt923-devgen-rng-zeroing-neutralize` (replaces `RandomNumberGenerator.GetBytes(32)` with `new byte[32]` — the unpredictability gap from item 6 above).

  **Known residual gaps, not addressed here**: deployments already upgraded with a rotated key but that never rotate again are outside any framework guard's reach; hosts that set `SecurityKey` exclusively via a `services.Configure<Configs>` code delegate (nothing in raw `IConfiguration`) still cannot boot in a non-Development environment (`AddWtmAuthentication` reads `config.Get<Configs>()`, which never sees the delegate, and judges the key weak), and in Development now boot but fail every login instead (see the design-gate review correction above for why) — this is the pre-existing #753-family split-brain config read, not fixed here, tracked separately with the sign/validate mismatch as an added detail; `HasLowCharacterDiversity`'s warn-only signal is not an entropy measurement (a real 40-character sentence passes it too — 8+ distinct characters is not a proxy for actual randomness, it only catches the specific "manually padded a short key with a repeated filler" shape). `TokenService`'s new constructor guard (item 5 above) narrows, but does not eliminate, the split-brain's blast radius: it stops a directly-constructed or DI-resolved `TokenService` from signing with a key `IsWeakSigningKey` would reject, but a caller that reaches `SymmetricSecurityKey` through some OTHER path this fix did not find would not be covered — the sink inventory is bounded by a `grep -rn "SymmetricSecurityKey" --include="*.cs" .` (full tree, re-run for this entry — see `docs/production-readiness.md`'s matching #923/#931 entry for the command's actual output), not by a proof that no other construction path exists. Two further items from the #931 cross-vendor review are explicitly out of scope for this fix and tracked as separate issues: live key rotation does not update the already-running `JwtBearerHandler`'s in-memory `IssuerSigningKey` (see the restart precondition above — this is that same limitation, described precisely instead of glossed over); and Development-environment detection takes the first registered `IWebHostEnvironment` descriptor rather than honoring DI's last-registration-wins resolution semantics, and has no fallback for a generic `IHost` that only registers `IHostEnvironment`.

- **`RedoUpdateModel`'s unrestricted dotted-path reflection write reached process-wide DI singletons — one authenticated low-privilege caller's `ConfigInfo.IsQuickDebug=true` form field disabled authorization for the whole process until restart; `Configs.EnforceRequestBindingScope` default flipped to `true` (#867, P0, BREAKING).** `BaseController.RedoUpdateModel`/`BaseApiController.RedoUpdateModel` (`src/WalkingTec.Mvvm.Mvc/BaseController.cs`/`BaseApiController.cs`) iterate every key in a VM's `FC` dictionary — copied verbatim from `Request.Form`/`Request.Query` by `WTMContext.CreateVM`, with no filtering — and write each one via `PropertyHelper.SetPropertyValue`, which follows dotted paths using only a **getter** to traverse each intermediate hop. A `get`-only property is therefore not protection: the write lands on the live object the getter returns, not on the property itself. `BaseVM.Wtm`/`BaseVM.ConfigInfo` (one hop from any ListVM) resolve to `WTMContext`/`Configs`, and `Configs` is `IOptionsMonitor<Configs>.CurrentValue` — the SAME instance across every request in the process. Runtime-verified landings: `ConfigInfo.IsQuickDebug=true` (`WtmAuthorizationService.cs` then returns `true` for every authorization check, for every user, until restart), `ConfigInfo.IsFilePublic=true` (re-enables the unauthenticated cross-tenant file read #859/#860 just closed), and `Wtm.GlobaInfo.AllAccessUrls=<prefix>` (exempts the whole URL prefix from authorization for everyone). All five call sites (`_FrameworkController.Selector`/`GetPagingData`/`GetExportExcel`/`GetExportExcelStream`/`DoImport`) sit under `[AllRights]` — reachable by any authenticated account, no elevated privilege required.

  **The fix is a positive allowlist keyed on the TYPE each dotted-path segment resolves to, not the property's name or the class that declares it.** New `WalkingTec.Mvvm.Core.RequestBindingPolicy.IsPathAllowed` (`src/WalkingTec.Mvvm.Core/Helper/RequestBindingPolicy.cs`) resolves each segment via reflection (mirroring `PropertyHelper.SetPropertyValue`'s own normalization and per-hop type progression — `member.GetMemberType()`, not `member.DeclaringType` — exactly) and rejects the whole key if:
  - any segment's resolved type IS, or is assignable to (covers a downstream subtype or an interface-typed alias), one of a curated set of gateway types: `WTMContext`, `Configs`, `GlobalData`, `LoginUserInfo`, `IDataContext`, `ISessionService`, `IModelStateService`, `IDistributedCache`, `IStringLocalizer`, `IUIService`, `IServiceProvider`, `IOptionsMonitor<>`, `IOptionsSnapshot<>`, `IOptions<>` (the last three are open generic type DEFINITIONS, matched by a separate check — see the round-3 disclosure below);
  - any segment resolves to a `static` member — `Type.GetMember(name)` defaults to `BindingFlags.Public | Instance | Static`, so `WTMContext.ReloadUserFunc` (`public static`) is reachable through the ordinary instance path `Wtm.ReloadUserFunc` even though it has nothing to do with any one request's context;
  - `Type.GetMember(name)` resolves to more than one member for a segment (fail closed on ambiguity rather than trust `PropertyHelper`'s own unconditional `members[0]` to be the safe candidate — verified this genuinely happens when a base class field is hidden by a derived class property of the same name, not just the more obvious `new`-hiding case);
  - the path exceeds 3 segments — `Searcher.SortInfo.Property` is the deepest verified-legitimate payload this framework's own LayUI DataTable front end sends (regular grids post the unprefixed 2-segment `SortInfo.Property`; only Selector-mode grids add the `Searcher.` prefix — reverified against `framework_layui.js`/`DataTableTagHelper.cs` in cross-vendor review).

  **Cross-vendor review of the first version of this fix (still within the same PR, before merge) found a High-severity bypass and it is worth recording precisely, because the shape of the mistake is instructive.** That version checked `member.DeclaringType` plus a curated NAME set (`Wtm`, `ConfigInfo`, `GlobaInfo`, …) — a denylist wearing an allowlist's clothes. A downstream VM could legally re-expose the exact same `Configs`/`GlobalData` singleton under a name and declaring class the policy had never heard of — `public Configs? Settings => base.ConfigInfo;` — and sail straight through, because `Settings` is declared on the downstream VM, not `BaseVM`. `new`-shadowing, a member declared on an intermediate base class between `BaseVM` and the concrete VM, an interface-typed alias, and a generic type parameter closed over one of these types all defeated that check the same way. The property's name and declaring type are both attacker-influenced (any downstream `BaseVM`/`BaseSearcher` subclass not bounded by this repository can add either); the TYPE the getter actually returns is not. The current version above (checking `member.GetMemberType()`, not `member.DeclaringType`) is what actually ships; see `RequestBindingPolicyTests867`'s five bypass-shape tests (`IsPathAllowed_AliasProperty_ReturnsFalse`, `IsPathAllowed_NewShadowedConfigInfo_ReturnsFalse`, `IsPathAllowed_InterfaceTypedGatewayAlias_ReturnsFalse`, `IsPathAllowed_AliasOnIntermediateBaseClass_ReturnsFalse`, `IsPathAllowed_GenericTypeParameterClosedOverConfigs_ReturnsFalse`), each constructing exactly one of those five and asserting it is now rejected.

  This is deliberately a curated TYPE list, not "every reference type": `BaseSearcher` directly declares the framework's own designed binding surface (`Page`, `Limit`, `SortInfo`, and friends) and none of those types are gateways to a larger, shared/process-wide object graph, so they are not banned. Verified still-working: `Searcher.<CustomFilterField>`, `Searcher.Page`/`Searcher.Limit`, and `Searcher.SortInfo.Property`/`Searcher.SortInfo.Direction` (the depth-3 boundary case).

  **The depth cap is kept — it is not made redundant by the type check.** The type check closes the "reachable alias" bypass; it does not bound how deep a chain of ordinary, non-gateway-typed properties can run before this policy has to give up walking it, and this repository cannot enumerate every type a downstream VM might ever expose.

  **A second cross-vendor review round found a second High-severity bypass, and the comment that hid it is itself the more important finding.** `IsPathAllowed` used to `break` out of its loop and return `true` when an intermediate dotted-path segment failed to resolve, on a comment claiming *"SetPropertyValue's own traversal would stop (middle hop) or no-op (final hop) too."* **That claim is false, and it was never checked against `PropertyHelper.cs` before being written.** `PropertyHelper.SetPropertyValue`'s own intermediate loop (`PropertyHelper.cs:523-551`) also `break`s on a middle-segment miss — but it does NOT reset its traversal type: `tempType`/`temp` stay at whatever they were before the failed hop (the ORIGINAL VM, if it is the very first segment that fails), and execution falls through to resolve and WRITE the FINAL segment against that frozen type (`PropertyHelper.cs:553-559`). A key like `Missing.StaticSecret` therefore still writes `StaticSecret` onto the VM itself even though `Missing` never resolved to anything — bypassing the static-member guard, the ambiguity guard, and the gateway-type guard all in one move, since none of them are ever reached. **Fixed by failing closed on ANY zero-resolution, at any segment position** (first, middle, or last), rather than trying to replicate `SetPropertyValue`'s frozen-type fallback exactly — simpler to keep correct, and provably safe: for a genuinely unresolvable single-segment key, `SetPropertyValue`'s own final-segment lookup would also find nothing and no-op, so rejecting it here too is strictly more conservative, not a functional behavior change. Per the review's explicit instruction, every remaining comment in `RequestBindingPolicy.cs` asserting something about `PropertyHelper`'s behavior was re-verified against `PropertyHelper.cs` line by line (not assumed correct because the others read plausibly) — two more were found imprecise and corrected: the ambiguous-resolution doc comment's "most concretely: `new`-hiding" (plain same-kind `new`-hiding does NOT itself produce `GetMember` ambiguity — only cross-kind hiding does, see above), and `IsPathAllowed`'s own doc comment, which claimed to "mirror `SetPropertyValue`... exactly" — true only on the success path, now stated as such. New regression tests: `MissingIntermediateSegment_ActuallyWritesFinalSegmentOnVm_WhenPolicyIsIgnored` (proves the exploit empirically before asserting the guard blocks it), `IsPathAllowed_MissingIntermediateSegmentThenStaticFinal_ReturnsFalse` (`Missing.StaticSecret`), `IsPathAllowed_MissingIntermediateSegmentThenAmbiguousFinal_ReturnsFalse` (`Missing.<ambiguous-name>`) — the exact two shapes the review asked for. Mutant `wtmsec867-zero-resolution-guard-neutralize` (verbatim `VERDICT: KILLED` / `GATE: PASS`) reverts the fix to the exact pre-fix `break`.

  **A type denylist cannot enumerate every gateway a downstream VM may surface — it fails in two independent ways, and this is disclosed here rather than left implicit.**

  **(a) An innocuous declared type whose setter's BODY writes into shared state anyway (round 2).** `BannedGatewayTypes` judges an alias's DECLARED type; it cannot see what a custom setter's BODY actually does. A downstream VM could add `public List<string> SharedPublicUrls { get => Wtm!.GlobaInfo!.AllAccessUrls; set => Wtm!.GlobaInfo!.AllAccessUrls = value; }` — `SharedPublicUrls`'s resolved type is `List<string>`, not a banned type, so a single-segment key naming it passes this policy, and its setter overwrites `GlobalData.AllAccessUrls` directly — the same severity as the original finding, via a route the type check cannot see. The setter's logic is opaque to `Type.GetMember`/`PropertyInfo.PropertyType` no matter how large `BannedGatewayTypes` grows — extending the list cannot close this failure mode, because the danger lives in code the list was never going to inspect.

  **(b) A declared type that IS itself a gateway but was not yet on the list — needing no custom setter at all (found and its concrete instance closed in round 3).** A third cross-vendor review round found: `public IOptionsMonitor<ActionLogRetentionOptions> Retention => Wtm!.ServiceProvider!.GetRequiredService<IOptionsMonitor<ActionLogRetentionOptions>>();` — a **plain, uncustomized getter**. The 3-segment key `Retention.CurrentValue.NormalDays` walked as `IOptionsMonitor<ActionLogRetentionOptions>` → `ActionLogRetentionOptions` → `int`, none of which were on `BannedGatewayTypes` before this round, landing on `IOptionsMonitor<T>`'s own process-wide cached `CurrentValue` — site-wide ActionLog-retention destruction via one form field. `IOptionsMonitor<>`, `IOptionsSnapshot<>`, `IOptions<>`, and `IServiceProvider` are now on the list. **Verified, not assumed, that this actually works**: `Type.IsAssignableFrom` does NOT relate an open generic type DEFINITION to any of its closed constructions (`typeof(IOptionsMonitor<>).IsAssignableFrom(typeof(IOptionsMonitor<Configs>))` returns `false`) — naively adding the three generic entries to the existing `IsAssignableFrom`-only loop would have silently done nothing, which is exactly the mistake `wtmsec867-open-generic-gateway-guard-neutralize`'s mutant reproduces and kills. `IsBannedGatewayType` now dispatches generic-type-definition entries to a separate `IsOrImplementsOpenGenericDefinition` check (closed construction, implemented interface, or generic base class). One more empirical correction found while writing the per-entry tests: `IOptionsSnapshot<T>` inherits `Value` from `IOptions<T>` rather than redeclaring it, and `Type.GetMember` on an **interface** does not walk up to base interfaces the way it does for classes — `typeof(IOptionsSnapshot<ActionLogRetentionOptions>).GetMember("Value").Length == 0` — so a multi-segment test key through `.Value` would have been rejected by the unrelated zero-resolution guard regardless of whether `IOptionsSnapshot<>` were banned at all; the actual regression test uses a single-segment key to isolate hop 0. **This closes only THIS instance of failure mode (b), not the failure mode itself**: a downstream VM, or a future dependency of this framework, can always introduce a new gateway type this list has not yet been told about. Confirmed by `grep -rn "IOptionsMonitor<\|IOptionsSnapshot<\|IOptions<\|IServiceProvider" src/ demo/ test/` (not by repeating the reviewer's claim) that no `BaseVM`/`BaseSearcher` subclass anywhere in this repository exposes any of the four newly-banned types today, so the legitimate binding-surface cost of adding them is zero.

  **Neither failure mode has a finite fix.** (a) is closed for exactly zero additional `BannedGatewayTypes` entries (setter bodies stay opaque to reflection metadata regardless of list size); (b) is closed only for the specific types on the list at any given time. No such forwarding property or gateway-typed alias of either shape exists anywhere in this repository today (confirmed by `git grep` across all three review rounds) — but nothing stops a downstream `BaseVM`/`BaseSearcher` subclass from adding one. Closing either failure mode structurally needs either a real positive binding-contract (explicit per-VM annotation of which members `RedoUpdateModel` may write — a breaking change of a different magnitude than this PR) or making `Configs`/`GlobalData`/the DI container's own options cache immutable at the DI boundary after startup, which Issue #867's own original analysis already identified as "the real endgame fix" and deliberately deferred to a separate issue so this narrower one could ship first. Issue #889 (widened in round 3 to cover both failure modes), out of scope for this PR. `RequestBindingPolicy`'s own class doc comment carries the same disclosure in code — including a correction to a sentence that, while technically true, read as a completeness claim it did not support ("cannot be defeated by any renaming/hiding/re-declaring trick" now states explicitly that it proves only those specific tricks are closed, not that `BannedGatewayTypes`'s coverage is complete).

  Wired into both `RedoUpdateModel` copies (one policy, two call sites — this exact family has been fixed in one copy and missed in the other before) behind a new kill switch: **`Configs.EnforceRequestBindingScope`, default `true`.** A rejected key is skipped (not written, not thrown) and logged at Warning level with the key sanitized via `LogSanitizer` — the prior total absence of any signal here was itself part of the defect (a 30-key attack with 3 landing sinks left zero trace at default log level). Set to `false` only if a downstream `BaseVM`/`BaseSearcher` subclass genuinely needs a deeper or gateway-crossing binding this policy would otherwise reject; the framework's own binding surface never needs one. **This closes every alias/shadowing/interface/intermediate-base/generic-parameter bypass this review could construct against the reachable gateway types listed above — it is not a claim that no other type anywhere in the framework could ever need adding to that list as new code is written.**

  **Also fixed in the same PR: `GetPagingData` now re-pins `SearcherMode` back to `Search` after `RedoUpdateModel`**, the only one of the five call sites that didn't already (`Selector`/`GetExportExcel`/`GetExportExcelStream` all do). A caller-supplied `SearcherMode=Batch` otherwise routes `GetSearchQuery()` through `GetBatchQuery`, which strips every `Where` the ListVM's own `GetSearchQuery()` applied — including row-level authorization filtering — before adding an `Ids.Contains(...)` clause. This protects downstream `GetSearchQuery` filtering, not the framework itself, and is stated separately here because its blast radius and mechanism are unrelated to the allowlist fix above.

  **Deliberately not ported: `UpdateModelProperty`'s field blocklist** (`ID`/`Password`/`PasswordHash`/`Salt`/`TenantCode`/`CreateTime`/`CreateBy`/`UpdateTime`/`UpdateBy`/`ITCode`). That list protects **entity** columns; none of these five call sites write entities (ListVM/ImportVM have no `Entity` at their root) — porting it would block nothing in the actual reachable target set (`Configs`/`GlobalData`/`WTMContext`) while looking like a fix, which is exactly the kind of overstated claim this repo's Red Line on `docs/production-readiness.md` forbids.

  **Breaking-change note:** any deployment relying on a `RedoUpdateModel` binding deeper than 3 segments, or one that deliberately binds through a property whose type is one of the banned gateway types above (not a documented or supported use of this endpoint family), needs `EnforceRequestBindingScope: false` in `appsettings.json` to keep working after upgrading. No known legitimate deployment does this — the framework's own front end never generates such a payload.

  Version bumped to 10.19.0 (minor) — same still-unreleased `[Unreleased]` cycle #859 already took to 10.19.0 this cycle; no further bump needed per that same precedent.

  Test: `test/WalkingTec.Mvvm.Core.Test/Helper/RequestBindingPolicyTests867.cs` (unit-level, isolates each guard — gateway-TYPE (including the open-generic dispatch), static-member, ambiguous-resolution, zero-resolution, depth cap — against fixture types, including the five bypass-shape tests from round 1, the `Missing.*` regression tests from round 2, and one test per newly-banned type from round 3 (`IOptionsMonitor<>`/`IOptionsSnapshot<>`/`IOptions<>`/`IServiceProvider`)) and `test/WalkingTec.Mvvm.Api.Test/RequestBindingScopeHttpTests867.cs` (real HTTP through `DemoWebApplicationFactory`: `ConfigInfo.IsQuickDebug` flip blocked with a legitimate `Searcher.ZipCode` binding in the SAME request as positive control; the `Wtm.ReloadUserFunc` static-member path; the issue's own `Searcher.Wtm.ConfigInfo.IsQuickDebug` alias plus the depth-cap-isolating `Wtm.ConfigInfo.IsQuickDebug`; and the `SearcherMode` re-pin, proven by a non-matching `Ids` value that would otherwise silently replace the posted `Searcher.ZipCode` filter). Six mutants under `test/mutants/entries/` (`wtmsec867-gateway-type-guard-neutralize`, `-shapes`, `wtmsec867-static-member-guard-neutralize`, `wtmsec867-ambiguous-resolution-guard-neutralize`, `wtmsec867-zero-resolution-guard-neutralize`, `wtmsec867-open-generic-gateway-guard-neutralize`; verbatim `VERDICT: KILLED` / `GATE: PASS` for all six) isolate the gateway-type, five-shapes, static-member, ambiguous-resolution, zero-resolution, and open-generic-dispatch guards independently — the static-member mutant specifically needed a fixture type outside `BaseVM`/`BaseSearcher`/`WTMContext`, because the one real static member on the production graph (`WTMContext.ReloadUserFunc`) is only reachable via `Wtm`, whose resolved type the gateway-type guard already independently rejects; the ambiguous-resolution mutant needed a base FIELD hidden by a derived PROPERTY of the same name (verified empirically — same-kind `new`-hiding does not itself produce an ambiguous `GetMember` result); the zero-resolution mutant reverts the round-2 fix to the exact pre-fix `break`, checked against the two `Missing.*` regression shapes the review named; the open-generic mutant reverts `IsBannedGatewayType`'s generic dispatch to the naive `IsAssignableFrom`-only loop, and its own verification run caught a confounded test (a 3-segment `IOptionsSnapshot<>` key that the independent zero-resolution guard also rejects, for the unrelated reason that `Type.GetMember` on an interface does not surface an inherited member) before the final single-segment version was accepted as the isolating red test.
- **`_FrameworkController`'s five per-resource authorization hooks were unreachable on every production route — the documented remedy ("override the hook in a derived controller") only ever creates a second, never-routed controller (#827, P0).** `_FrameworkController` is the CONCRETE class MVC routes every `/_Framework/*` request to (`src/WalkingTec.Mvvm.Mvc/_FrameworkController.cs:35` — not abstract). `CanExportVm`/`CanAccessFile`/`CanPreviewDelete`/`CanImportVm`/`CanEditProperty` were `protected virtual`, and #796/#814/#818's own documentation told integrators to "override this hook in a controller that inherits from `_FrameworkController`" — that produces a SECOND controller the front end's hard-coded `/_Framework/*` URLs never call. Nine prior test methods across `FrameworkControllerRbacHooksTest`/`FrameworkControllerFileAccessTest`/`FrameworkControllerImportAuthTest` all constructed a test subclass directly and called its action methods in-process, which proved the hook mechanism works in isolation but never that a real request reaches it. On the shipped default (every `Enforce*Authorization` flag `false`), this made the four flags mean nothing more than "deny everyone" (if ever flipped) with no way to selectively re-allow — the same defect #836's cross-vendor review flagged.

  Fix — `IWtmFrameworkEndpointAuthorizer`, a DI-resolvable seam `_FrameworkController` itself consults (`src/WalkingTec.Mvvm.Core/Services/IWtmFrameworkEndpointAuthorizer.cs`): a three-valued `WtmAuthorizationDecision` (`Inherit = 0` / `Allow` / `Deny`, no `DefaultAllow` property so `default` genuinely abstains) lets a registered policy answer per hook; `Inherit` (including no policy registered at all) falls through to exactly the pre-existing flag-driven answer. Resolution is `HttpContext?.RequestServices?.GetService(typeof(IWtmFrameworkEndpointAuthorizer)) as IWtmFrameworkEndpointAuthorizer` — both null-conditional operators are load-bearing (several existing hook tests wire a bare `DefaultHttpContext`/a `Mock<IServiceProvider>` that only stubs the singular `GetService(Type)` overload; `GetService<T>()`/`GetRequiredService` would NRE or throw against that fixture). Registration helper `AddWtmFrameworkEndpointAuthorizer<T>()` (`FrameworkServiceExtension.cs`) registers `AddScoped`, deliberately not `AddSingleton` — a real policy will typically query `WTMContext.LoginUserInfo`/the database, and a singleton would create a captive dependency on that request-scoped state. `CanEditProperty` — the only WRITE endpoint among the five, and the only one with no `Enforce*` flag at all (`=> true`, hardcoded) — gets the same DI-first shape; its Inherit fallback stays the unconditional allow it always was, since there is no flag to fall back to. **This is purely additive: nothing is registered by default, so an unregistered seam changes nothing for a default deployment** — the same compatibility posture the four `Enforce*` flags already had. Coexists with `IWtmAuthorizationService` (URL/menu-level RBAC, a different question) — no `[Obsolete]`, no migration between them.

  **No default policy ships with this seam.** WTM cannot know a downstream deployment's actual per-VM/per-resource rules (which VM types map to which roles, which files a caller may own) — shipping a non-empty default would be a compatibility decision this PR does not make; every existing deployment's behaviour is unchanged until a host explicitly calls `AddWtmFrameworkEndpointAuthorizer<T>()`.

  **Tests**: `test/WalkingTec.Mvvm.Api.Test/FrameworkAuthorizationSeamTests.cs` — HTTP-level, through `DemoWebApplicationFactory`, against the real `/_Framework/*` routes (not a subclass): a DI-registered policy is proven consulted in both directions (`Deny` overriding the permissive flag default, `Allow` overriding a fail-closed `EnforceVmExportAuthorization=true` — each also asserting the test authorizer's own call counter, e.g. `CanExportVmCalls > 0`), one default-behaviour-pinning test per hook with no policy and no flag (pins "this changes nothing by default" into CI — deliberately *not* a claim that the seam was consulted, since none of these five register a policy at all), and an anonymous/`IsFilePublic=true` test proving a registered policy survives `LoginUserInfo == null` and a public file still serves. Mutant `mvc827-di-authorizer-neutralize` (`test/mutants/entries/`, `VERDICT: KILLED`) short-circuits `ResolveEndpointAuthorizer()` to always return `null`; it only kills the `Deny`/`Allow` bidirectional tests above, proving those two genuinely depend on the DI resolution and not merely on the flag `Inherit` falls back to — the five default-behaviour-pinning tests register no policy, so this mutant does not affect their outcome and is not evidence for what they assert.

  **Scope**: this PR covers only `_FrameworkController`'s five hooks. #836's broader recommendation — absorbing `_AnalysisController.CheckAccess` (and its duplicate, `AnalysisWidgetDataSource.CheckAccess`) into the same seam and gating `_AnalysisController`/`_DashboardController`/`_DashboardDesignerController`'s uncovered endpoints — is out of scope here and tracked separately (#812, #823, #847, #867 remain open). Correction from PR #881's review: #836's own comment named this `AnalysisVmRegistry.CheckAccess`; no such method exists — `AnalysisVmRegistry` only has `Build`/`Resolve`/`GetRegisteredTypes`. `CheckAccess` is defined directly on `_AnalysisController` (and separately, near-identically, on `AnalysisWidgetDataSource`).

- **Four VM-name-driven `_FrameworkController` endpoints still constructed the caller-named VM before authorizing it, even after #796/#818's `passInit: true` probes (#829, P0).** `GetExportExcel`/`GetExportExcelStream`/`GetExcelTemplate`/`GetDeletePreview` used to probe the caller-named VM via `Wtm.CreateVM(name, null, null, true)` before calling the matching hook; `passInit: true` only gates `DoInit()`/`InitVM()`/`searcher.DoInit()` — it never gated the constructor call, `WTMContext.CreateVM`'s unconditional `lvm.DoInitListVM()` for a `IBasePagedListVM`, or its equally unconditional `tvm.Template.DoInit()` for a `IBaseImport<BaseTemplateVM>` (`GetExcelTemplate`'s own pre-existing comment admitted this: "there is no passInit-only way to avoid that from this call site"). An authenticated caller could therefore trigger a caller-selected VM's initialization side effects and DB queries even on a request ultimately denied by `CanExportVm`/`CanPreviewDelete`.

  Fix — resolve via `WTMContext.TryResolveVmType` (Type only, no construction) instead of the passInit probe, the same resolution `DoImport` already used for #818, so nothing is constructed until after the authorization decision. `GetDeletePreview`'s response for an unresolvable/unregistered VM name is restored to `BadRequest` — kept as a separate return from the `CanPreviewDelete` denial's `Forbid()`, specifically so the two cases cannot collapse into each other again.

  **That restores only the unresolvable-name subset of base's behaviour, not the full set base's `try`/`catch (ArgumentException)` used to cover.** Base wrapped the *entire* `Wtm.CreateVM(name, null, null, true)` probe in that try, including `WTMContext.CreateVM`'s unconditional `lvm.DoInitListVM()` for an `IBasePagedListVM` (`WTMContext.CreateVM.cs:143`) and `tvm.Template.DoInit()` for an `IBaseImport<BaseTemplateVM>` (`:150`) — so a name that resolved fine but whose VM threw `ArgumentException` during that initialization also produced `BadRequest` in base. #829 deliberately removed that whole probe; a resolvable name's construction (and whatever it can throw) now happens only inside the post-authorization per-row loop, which this method does not wrap in a try/catch. That wider `ArgumentException` set — initialization failures on a name that *does* resolve — no longer routes to `BadRequest`. This is a direct consequence of #829 moving construction after authorization, not a regression this fix reintroduces or leaves unaddressed: catching it again would mean reconstructing the pre-authorization probe #829 exists to remove.

  **Tests**: `test/WalkingTec.Mvvm.Admin.Test/FrameworkControllerVmConstructionOrderingTest.cs` — counting VM/template fixtures proving the deny path never constructs, never calls `InitListVM()`/`GetSearchQuery()`, and never calls the import template's `InitVM()` (`BaseVM.DoInit()`'s own implementation), each paired with a positive control on the allowed path. Mutant `mvc829-getexportexcel-reintroduce-preauthz-construction` (`test/mutants/entries/`, `VERDICT: KILLED`) reintroduces a discarded pre-authorization probe call, proving the construction-ordering test depends on the fix, not on coincidence.

- **ETL entities carrying tenant-scoped data without implementing `ITenant`, plus a root-cause wiring defect that made the ONE ETL entity that already did implement it (`EtlJobDefinition`) non-functional too (#841, #862, P1).** `DataContext.cs:243` only generates a `TenantCode` predicate for entity types implementing `ITenant`; `#836`'s exhaustive-table review found four ETL/Core entities in `class .* : BasePoco` that carry tenant data without it.

  **Root cause, found while fixing the first entity (#862):** `EtlJobDefinition` has implemented `ITenant` since ETL-006, but its filter was **never actually enforced** through a real `FrameworkContext`-derived app. `ApplyEtlModels()` registers ETL entity types via `modelBuilder.Entity<T>()` from the consumer's own `DataContext.OnModelCreating`, called AFTER `base.OnModelCreating(modelBuilder)` returns (the documented convention, `docs/etl-module.md`) — by then `FrameworkContext.OnModelCreating`'s Pass 2 loop (`DataContext.cs`) has already finished iterating `modelBuilder.Model.GetEntityTypes()` and applying `ITenant`/`IPersistPoco` filters for every entity type known to the model AT THAT POINT; it can never retroactively see a type registered afterward. Confirmed empirically, not assumed: seeding two `EtlJobDefinition` rows under different tenants and reading tenant B's row through a context scoped to tenant A returned it, with the generated SQL carrying **no `TenantCode` predicate at all**. Fixed with a new `ApplyEtlModels(ModelBuilder, EmptyContext)` overload that re-applies `DataContext.cs`'s exact filter pattern for each ETL entity implementing `ITenant`, immediately after registering it. The old zero-argument overload is kept (`[Obsolete]`, table/column/index registration unchanged) for callers who have not yet updated their `DataContext.OnModelCreating` to pass `this`.

  **Second-order regression this same fix would have introduced, caught before shipping:** `EtlSchedulerService`/`EtlQuartzJob`/`DbEtlGovernanceStore` resolve their own background `WTMContext` from a fresh DI scope with no HTTP identity — even when `EtlSchedulerService`'s methods are invoked synchronously from a controller action, they `_sp.CreateScope()` a brand-new context, never the calling controller's per-request `Wtm`. That context's `TenantCode` is always null (`WTMContext.CreateDC` only resolves a tenant from `LoginUserInfo.CurrentTenant`, which requires an authenticated request). Once the `ITenant` filter actually started being enforced, every read in this shared, app-wide scheduler — structurally a cron daemon, not a per-tenant service — would only ever match null-tenant rows: a tenant-scoped job stuck in `Running` after a crash would never be reset, Quartz would silently stop executing any tenant-scoped job at all, `TriggerNow`/`Reschedule`/`Rerun` would stop finding jobs by id, and retention pruning would stop deleting old tenant-scoped rows. Fixed by adding `IgnoreQueryFilters()` (with an explanatory comment at every call site, per `.claude/rules/dotnet-conventions.md`) to every `EtlJobDefinition`/`EtlRunLog`/`EtlDeadLetterRow` read in those three files. Deliberately **not** applied to the VM layer (`EtlJobListVM`, `EtlRunLogListVM`, `EtlJobDefinitionVM`) or `EtlDashboardService.BuildSummary` — both correctly use the calling controller's own per-request `Wtm.DC` and stay tenant-scoped, which is the actual security property this fix exists to deliver for the admin UI.

  **Entities changed, in order of confidence (each its own commit):**
  - **`EtlDeadLetterRow`** — already had a populated `TenantCode` column (its own doc comment: "Populated by the pipeline from the executing job's TenantCode context"), but the class did not implement `ITenant` at all, so the value was written and never used for filtering. `RowJson` holds the actual quarantined source rows, so this was a real cross-tenant data-content read. **No migration** — `ITenant` only requires `string? TenantCode { get; set; }`, which already existed.
  - **`EtlLineageRecord`** — no `TenantCode` column at all. Adds a nullable `TenantCode` (StringLength 50, matching the other entities) plus an index, and populates it going forward in `EtlPipelineExecutor` from `EtlPipelineConfig.DeadLetterTenantCode` (despite the dead-letter-specific name, this already carries the executing job's tenant code generally).
  - **`EtlRunLog`** (#841's original subject) — no `TenantCode` column at all; the entity #841's own analysis says makes `_EtlRunLogController`'s authorization gate meaningless no matter how the gate itself is fixed, since the underlying table has no tenant column regardless. Adds the column, populates it at every write site (`EtlSchedulerService.ResetGhostRunningJobsAsync`'s crash-recovery entries, `EtlQuartzJob.Execute`'s skip-path and main finally-block) by inheriting the owning `EtlJobDefinition`'s `TenantCode`.
  - **`ChangeLog`** (`src/WalkingTec.Mvvm.Core/Models/ChangeLog.cs`) — **deliberately left unchanged.** This one is in Core, ships to every downstream unconditionally (unlike the opt-in ETL module), and has no viewer anywhere in `src/`/`demo/` today (`grep -rln "ChangeLog"` only turns up the write path in `BaseCRUDVM.AppendChangeLog` and the model itself) — the exposure is latent, not live. Its `DbSet<ChangeLog> BaseChangeLogs` is declared directly on `FrameworkContext` (not via an `ApplyXModels()` extension), so it does not share the ordering defect above — if this decision is ever revisited, wiring is not the blocker. Turning on tenant filtering for an audit table by default risks silently hiding change history an operator currently relies on being able to see (e.g. a platform-level admin auditing all tenants), which is a materially different trade-off than the ETL entities above (whose data an admin was never meant to see across tenants in the first place). Filed as a finding, not fixed here: the first downstream that builds a viewer over `ChangeLog` must decide this — ideally by adding `ITenant` proactively, before the viewer ships, while the gap is still latent rather than live.

  **Migration (`EtlLineageRecord`, `EtlRunLog`):** this repo has no EF Core Migrations project (`Database.EnsureCreated()` throughout — `grep -rn EnsureCreated src/ demo/`), so `EnsureCreated()` picks up the new column automatically only for a brand-new database; an existing deployment's table needs a manual `ALTER TABLE`. Because both tables carry `JobId`, the correct backfill is derivable — not "null it and hope" — by joining to the already-tenant-scoped `EtlJobDefinitions`:
    ```sql
    ALTER TABLE EtlRunLogs ADD TenantCode NVARCHAR(50) NULL;
    UPDATE r SET r.TenantCode = j.TenantCode FROM EtlRunLogs r JOIN EtlJobDefinitions j ON r.JobId = j.ID;

    ALTER TABLE EtlLineageRecords ADD TenantCode NVARCHAR(50) NULL;
    UPDATE r SET r.TenantCode = j.TenantCode FROM EtlLineageRecords r JOIN EtlJobDefinitions j ON r.JobId = j.ID;
    ```
    Skipping the backfill is not silently wrong, just conservative: EF Core's null-safe `==` translation means an un-backfilled (`NULL`) row resolves only for a caller whose own resolved tenant is ALSO `null` — the same precedent `WtmFileProvider.DeleteFileTenantScoped` established for #815 and #859 extended to file reads — so a single-tenant deployment (`EnableTenant=false`, where every context's own `TenantCode` is also null) sees no change at all, while a multi-tenant deployment that skips the backfill makes its pre-existing run-log/lineage history invisible to every real tenant post-upgrade until backfilled. No version bump beyond what #859 already took this same `[Unreleased]` cycle to 10.19.0 for — these changes land in that same still-unshipped version.

  **Controller gates (#841):** `_EtlRunLogController`, `_EtlMonitorController`, `_EtlSchemaController` had no `OnActionExecuting` role gate of their own (`grep -c OnActionExecuting` returned 0, versus 3 for `_EtlJobController`/`_EtlDashboardController`) — only page-level URL-RBAC. `_EtlRunLogController.Rerun` starts a real pipeline re-execution from a watermark snapshot; `_EtlMonitorController.Running` reads `EtlProgressTracker`'s in-memory, tenant-blind running-job state; `_EtlSchemaController.Tables`/`Columns` do native ADO cross-DB schema introspection, bypassing `CreateDC` entirely. All three now carry the identical Admin/ETLAdmin gate the other two already use (`IsQuickDebug` bypasses it, matching framework convention). Deliberately not fixed here, tracked in #841's own body: `_EtlSchemaController`'s missing `Enabled` check on the resolved connection, and `EtlProgressTracker`'s lack of a tenant dimension.

  **Major finding while building the acceptance tests for the controller gates above, filed as #876 — not introduced by this change and not limited to these three controllers (CORRECTED below, see the #876 entry: the "throws `NullReferenceException`" claim in this paragraph was speculative and turned out to be wrong once actually verified):** the entire `WalkingTec.Mvvm.Etl` assembly's controllers never receive WTM's three global action filters (`DataContextFilter`/`PrivilegeFilter`/`FrameworkFilter`) via the real ASP.NET Core MVC pipeline. Confirmed empirically (see #876 for the full reproduction). Practical effect: `Wtm` is `null` inside every ETL controller's `OnActionExecuting`/action body reached via real HTTP, so every `Wtm.LoginUserInfo.Roles`-based gate on this assembly — the two pre-existing ones and the three added here — fails CLOSED for every caller including a genuine Admin (the gate LOGIC is correct, proven by unit tests that manipulate `Wtm`/`Roles` directly; the surrounding pipeline just never supplies real data), and any ETL action reading `Wtm.XXX` in its own body throws `NullReferenceException` the moment it is reached via a real request. Root-causing why the global filters never apply to this one assembly is out of scope for #841/#862 and is tracked in #876. Test coverage for the three new gates is therefore split: a real HTTP negative control (non-Admin rejected) in `test/WalkingTec.Mvvm.Api.Test/EtlControllerGateHttpTests.cs`, still a genuine mutation-killing test despite the limitation above, and a unit-level positive control (Admin/ETLAdmin passes) in `test/WalkingTec.Mvvm.Etl.Test/Controllers/EtlRbacTests.cs`, the only currently-achievable way to verify that direction.

- **#876 fix (P0): `WalkingTec.Mvvm.Etl` controllers now actually receive `Wtm` before their own role gates run — closes the defect the #841/#862 entry above found and flagged.** Root cause: ASP.NET Core's own `ControllerActionFilter` (the internal wrapper that invokes a controller's own `OnActionExecuting` override, added automatically because every `Controller` subclass implements `IActionFilter`) is hard-coded by the framework to `Order = int.MinValue` — it always runs before ANY custom filter, including WTM's three global ones (`DataContextFilter`/`PrivilegeFilter`/`FrameworkFilter`), no matter what `Order` those are given (verified: setting `DataContextFilter.Order = -1000` made no difference; `int.MinValue` cannot be beaten). `DataContextFilter` is what populates `BaseController.Wtm`. The five `WalkingTec.Mvvm.Etl` controllers (`_EtlJobController`, `_EtlDashboardController`, and the three #841 added) are the only controllers anywhere in the codebase that read `Wtm` directly inside their own `OnActionExecuting` override — every other WTM controller uses `PrivilegeFilter`'s declarative, URL-based authorization instead — so they were the only ones exposed to this ordering gap.

  **Actual observed behaviour, verified before fixing (not assumed): a clean 403/redirect denial for every caller including a genuine Admin — NOT a `NullReferenceException`/500**, correcting the #841/#862 entry's speculation above. The gate's null-conditional operators (`Wtm?.LoginUserInfo?.Roles`) turn "`Wtm` is null" into a silently-empty role list rather than throwing, so `context.Result = Forbid()` fires unconditionally. This was therefore an availability defect (100% lockout of the ETL admin UI for everyone, including real admins), not the framework silently letting unauthorized callers through. Because setting `context.Result` inside `OnActionExecuting` short-circuits the rest of the filter pipeline, `DataContextFilter`/`PrivilegeFilter`/`FrameworkFilter` never got a turn at all for these five actions — no ETL action body was ever actually reachable to throw the speculated `NullReferenceException` in the first place.

  Fixed by `WtmControllerActivator` (`src/WalkingTec.Mvvm.Mvc/Helper/WtmControllerActivator.cs`), which DECORATES the already-registered `IControllerActivator` and populates `Wtm` at controller CONSTRUCTION time — before any filter, including the hard-coded-first `ControllerActionFilter`, ever runs. This is a framework-level fix (registered once in `AddWtmContext`) that applies uniformly to every controller/assembly, not a per-controller patch to the five affected Etl controllers, so a hypothetical third assembly with the same anti-pattern is covered too. **Scope checked**: `WalkingTec.Mvvm.WorkFlow`'s controllers share the same `BaseController` and are therefore equally exposed to the underlying ordering gap, but none of them read `Wtm` from their own `OnActionExecuting`, so none exhibited the symptom — this fix benefits them too, uniformly, without any WorkFlow-specific change. **Blast radius checked**: full regression run across `WalkingTec.Mvvm.Api.Test` (76 tests), `WalkingTec.Mvvm.Mvc.Tests` (52), `WalkingTec.Mvvm.Etl.Test` (713), `WalkingTec.Mvvm.WorkFlow.Test` (591) — all green, no latent failures surfaced by ETL controllers now actually being reachable.

  **A cross-vendor review of this fix (PR #882) found the first version — an unconditional `services.Replace(...WtmControllerActivator)` — was itself a compatibility regression**, confirmed against the ASP.NET Core 10 source: `AddMvcCore()` registers `DefaultControllerActivator` via `TryAddTransient`, and a host additionally calling `.AddControllersAsServices()` replaces that with `ServiceBasedControllerActivator`, which resolves (and DISPOSES) controller instances through the DI container rather than a manual `Dispose()` call. Blindly replacing either one — as the first version did — silently discarded `.AddControllersAsServices()` for any host using it, and a hand-rolled `Release()` checking only `IDisposable` (never `IAsyncDisposable`, never overriding `ReleaseAsync`) risked leaking async-only-disposable controllers or double-disposing ones the container already owned. `WtmControllerActivator` now WRAPS whichever `IControllerActivator` is already registered at the point `AddWtmContext()` runs (preserving its `Create`/`Release`/`ReleaseAsync` and its original `ServiceLifetime`) instead of replacing it, so both known built-in activators' disposal contracts pass through unchanged and `.AddControllersAsServices()` keeps working. This makes `AddWtmContext()` require `AddMvc()`/`AddControllers()` to already be registered — both of this repo's real `Startup.cs` files already call them in that order, so this is a documentation/ordering requirement, not a behaviour change for any app in this tree; `AddWtmContext()` now throws `InvalidOperationException` at startup (not silently no-ops) if that order is violated. The review also raised whether `WTMContext` resolution inside `Create()` could now surface an exception at a pipeline stage that previously would have short-circuited before reaching it; verified against the ASP.NET Core 10 `ControllerActionInvoker` source that controller construction happens in the `State.ActionBegin` case, strictly AFTER the Authorization-filter and Resource-filter stages — an `[Authorize]` or resource-filter short-circuit still wins exactly as before. For these five controllers specifically, that resolution call was previously unreachable at all (the very bug being fixed made their own `OnActionExecuting` short-circuit with `Forbid()` first) — this fix restores the same per-request `WTMContext` resolution every other controller in the app already performs on every request, it does not add a new failure mode application-wide. See `WtmControllerActivator`'s XML doc for the full writeup, including why the review's suggested alternative (implement `IControllerPropertyActivator` instead) is not viable — that interface, like `DefaultControllerActivator`, is `internal` (confirmed by reflection against the installed SDK).

  Test coverage gap from the #841/#862 entry above is now closed: `EtlControllerGateHttpTests.cs` gains three real-HTTP positive controls (`EtlMonitorController_Running_Admin_Succeeds`, `EtlRunLogController_Index_Admin_Succeeds`, `EtlSchemaController_Tables_Admin_Succeeds`) alongside the existing negative controls — a genuine ETLAdmin caller now passes the gate and reaches the action body over a real HTTP call, no longer only at the unit level. Mutant `876-wtmcontrolleractivator-neutralize` (`test/mutants/entries/`) pins the fix: neutralizing `WtmControllerActivator`'s `Wtm`-population guard is verified KILLED.

  **A second round of cross-vendor review of PR #882 found the decorator above still dropped the INNER activator's own disposal, plus a keyed-service gap in how it is selected.** `AddWtmContext` builds the inner activator itself (invoking the captured descriptor's `ImplementationFactory`, or `ActivatorUtilities.CreateInstance` against its `ImplementationType`) — bypassing the DI container's own creation path, which is what normally enrolls a freshly-created disposable instance into the current scope's disposables list. The container only ever sees and tracks the OUTER `WtmControllerActivator`, which implemented neither `IDisposable` nor `IAsyncDisposable` at all — so a disposable third-party `IControllerActivator` that the container used to dispose on its own would silently stop being disposed once wrapped. Neither built-in activator is itself disposable, which is why the existing HTTP test suite never caught this. Fixed: `WtmControllerActivator` now implements both, and a new `ownsInner` constructor flag decides whether disposing the wrapper also disposes `_inner` — `true` when `AddWtmContext` constructed `_inner` itself (the `ImplementationFactory`/`ImplementationType` cases, where nothing else will ever dispose it), `false` when `_inner` is a pre-built, possibly cross-request-shared `ImplementationInstance` (disposing THAT one on a single scope's teardown would tear down a singleton every other resolution still needs — the DI container does not auto-dispose `ImplementationInstance` registrations either, by original .NET design, for the identical reason). **Same review round, same block**: the descriptor-selection `LastOrDefault` had no `ServiceDescriptor.IsKeyedService` check — a keyed `IControllerActivator` registration (.NET 8+ keyed services) sitting last in the collection would have its unkeyed `ImplementationType`/`ImplementationFactory`/`ImplementationInstance` all read as `null` (verified against the real `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.9 assembly — those getters explicitly guard on `IsKeyedService` and return `null`/throw otherwise), throwing at first resolution instead of correctly finding and wrapping the real (unkeyed) activator underneath; an unkeyed `GetRequiredService<IControllerActivator>()` call — what this activator and MVC itself actually perform — never resolves a keyed registration regardless of registration order, so picking one here was always wrong even before it threw. Now filtered to `!d.IsKeyedService`.

  **Also corrected**: the previous version of this doc comment claimed `ServiceBasedControllerActivator` was `internal sealed` like `DefaultControllerActivator` — verified via `ilspycmd` against the real installed 10.0.10 assembly that it is actually `public class`, not sealed, not internal. The design was unaffected either way (this class never needs to name either concrete type), but the claim itself was false and has been corrected in `WtmControllerActivator`'s XML doc.

  Tests: `src/WalkingTec.Mvvm.Mvc.Tests/Security/WtmControllerActivatorDisposalTests.cs` — direct unit tests on `WtmControllerActivator`'s own `Dispose`/`DisposeAsync` (owns-vs-doesn't-own the inner, `IAsyncDisposable` preferred over `IDisposable` when both are available), plus tests exercising the REAL `AddWtmContext` extension end-to-end (not a mirrored copy) — a disposable custom activator registered as a DI `ImplementationInstance` is confirmed NOT disposed on scope teardown (it is shared/caller-owned), a disposable custom activator registered by `ImplementationType` or `ImplementationFactory` (the two cases that leaked before this fix) IS disposed on scope teardown, a real async scope's `DisposeAsync()` is confirmed to prefer the inner's async path over its sync one, and a keyed `IControllerActivator` registration is confirmed skipped in favour of the real unkeyed one.

  **A third round of cross-vendor review of PR #882 found the sync `Dispose()` path from the fix above turned a loud framework failure into a silent success — the same defect class this repo has been chasing all month elsewhere (`BaseCRUDVM`'s error-state-collapsing-into-a-legitimate-value family): an error state a caller could previously detect became indistinguishable from working.** `Dispose()` only checked `_inner is IDisposable` — for an inner that implements ONLY `IAsyncDisposable` (no `IDisposable`), it fell straight through and did nothing, no exception, no disposal. Before this wrapper existed, a DI container's own sync `scope.Dispose()` on a tracked service in that exact shape throws `InvalidOperationException` (verified against the real installed `Microsoft.Extensions.DependencyInjection` 10.0.9 assembly: `ServiceProviderEngineScope.Dispose()`'s message is literally `"'{0}' type only implements IAsyncDisposable. Use DisposeAsync to dispose the container."`) — a loud, actionable signal telling the caller to use async disposal instead. Wrapping silently discarded that signal. Fixed: `Dispose()` now reproduces the same `InvalidOperationException` itself when `_inner` is `IAsyncDisposable`-only, naming the inner's actual type and pointing at `DisposeAsync`. Deliberately NOT completing the disposal by blocking on the async path (`.GetAwaiter().GetResult()`) instead — this repo's own convention (`dotnet-conventions.md`: "Never `.GetAwaiter().GetResult()` on a request path", and scope disposal can happen on a request path) rules that out; throwing the same exception the container would have thrown is both simpler and consistent with that rule. New regression tests (direct unit test and an end-to-end test through the real `AddWtmContext` wiring, both asserting the throw and that the inner's `DisposeAsync` was never actually reached from the sync path) pin this. Mutant `882-wtmcontrolleractivator-syncdispose-asynconly-throw-neutralize` neutralizes the throw back to a silent fall-through — verified KILLED.

  **Also corrected in the same round**: `FrameworkServiceExtension.cs`'s keyed-descriptor comment claimed the unkeyed `ImplementationType`/`ImplementationFactory`/`ImplementationInstance` getters "all throw" for a keyed descriptor — decompiled from the real 10.0.9 assembly, they actually return `null`; what throws is the REVERSE misuse (the `KeyedImplementation*` getters called on an UNKEYED descriptor). The `!d.IsKeyedService` filter itself was already correct — only the comment explaining why was wrong. This is the third comment in this branch's review history asserting something false about framework internals (`ServiceBasedControllerActivator`'s accessibility, previously), and this one was written in the round that claimed to have re-audited every comment for accuracy — re-auditing now explicitly includes comments added in the SAME round they're written, not just carried-over ones.

  **Migration correction**: see the `### Migration` section below — an earlier draft flattened three different signature shapes (`EtlSchedulerService`'s ten optional-both-parameters methods, `EtlProgressTracker`'s two required-`callerTenantCode` methods, and `EtlDashboardService.BuildSummary`'s required-`callerTenantCode`-with-no-`declaredSystemQuery`-at-all) into one four-case list that didn't fit `BuildSummary`, and missed that a derived-class override is a way of CONSUMING a signature, not an identity — orthogonal to which of the three cases above a caller is. Also missed, found by actually compiling it rather than assuming: `Func<Guid, Task> f = scheduler.PauseAsync` no longer compiles (`CS0123`, verified) — optional parameters do not participate in method-group-to-delegate conversion — and a reflection lookup by the old parameter-type set (`GetMethod("PauseAsync", new[] { typeof(Guid) })`) now returns `null` (also verified), the same failure mode a precompiled binary calling the old signature hits.

- **`DCExtension.ApplyDataPrivilegeForAnalysis` failed OPEN — not closed — whenever there was no authenticated identity, silently skipping row-level DataPrivilege for both known background consumers of the Dashboard widget pipeline; default flipped to fail-closed, with an explicit opt-in escape hatch (#843, P1, BREAKING for a narrow, named class of background widgets).** `DCExtension.cs:295-300` returned the caller's query completely unfiltered whenever `WTMContext.LoginUserInfo == null`, instead of denying it. `LoginUserInfo` is null on **every** background execution path: `AnalysisWidgetDataSource.GetDataAsync` (`src/WalkingTec.Mvvm.Core/Dashboard/AnalysisWidgetDataSource.cs`) resolves `WTMContext` from a bare `_serviceProvider.CreateScope()` with no `HttpContext` to read a session/JWT from — and it is the sole data source both `DashboardSnapshotJob` (`Dashboard/Snapshot/DashboardSnapshotJob.cs:97-98`, run by `DashboardSnapshotHostedService`) and `DashboardAlertHostedService` (`Dashboard/Alerting/DashboardAlertHostedService.cs:124-125`) call to execute widget configs that a user persisted through the HTTP dashboard-designer API. Write-time gating (`CanAccess` + DataPrivilege) and execute-time gating disagreed: a user's own row-level restriction, enforced when they view the widget interactively, was silently absent when the SAME widget config re-ran in the background — exporting or alerting on rows that user cannot see through the UI.

  **Identity decision, and the two alternatives rejected.** The question is whose identity a background job should run as. (1) *The widget/job author's identity, re-resolved or snapshotted at run time* — rejected: a job would outlive the author's privileges changing or their account being disabled, and synthesizing a `LoginUserInfo` (roles, DataPrivilege RelateIds) for a human who isn't present is a materially larger, separately-reviewable surface than a P1 fix should carry. (2) *A per-tenant "system" technical identity* — rejected for this release: no such account type exists in this framework today; introducing one is new infrastructure (provisioning, storage, a migration path for every existing tenant) out of proportion to closing an over-visibility hole. (3) **Chosen: `LoginUserInfo == null` now fails CLOSED by default** — reuses `AppendSelfDPWhere`'s own pre-existing `dps == null → 1 != 1` denial (previously reached only for an authenticated caller with zero assigned RelateIds; now also reached for no identity at all), so a background job against any model with a configured DataPrivilege rule sees zero rows instead of everything. A model with **no** DataPrivilege rule configured is unaffected regardless of identity — this is not a blanket "background jobs see nothing" flip. Callers that genuinely need an unfiltered system-wide query get a new, explicit `declaredSystemQuery: true` parameter on `ApplyDataPrivilegeForAnalysis` — a named argument visible in code review, not a config toggle a future deployment could silently re-enable — and it has no effect once there IS an identity (an authenticated caller cannot use it to bypass their own row-level restriction). Neither of the two production call sites of `ApplyDataPrivilegeForAnalysis` (`_AnalysisController.cs:605`, always authenticated; `AnalysisWidgetDataSource.cs:125`, the background path) passes `true` — this ships fail-closed immediately, not as an unflippable opt-in flag. (Four `Enforce*` flags in this codebase are documented as unable to be flipped by their own doc comments, and `UseSelectIslandRender` has been default-off since it shipped with no flip condition — this fix deliberately does not add a fifth: the identity gap is a genuine security bug, not a documented legacy default worth preserving.)

  **Adjacent gap closed in the same PR, without which the fix above would have been untestable against a real background path**: `AnalysisWidgetDataSource`'s `WTMContext.DC` is built via `WTMContext.CreateDC()`, which derives the DataContext's `TenantCode` exclusively from `LoginUserInfo.CurrentTenant` (`WTMContext.CreateDC.cs:28`) — with no identity, that resolves to `TenantCode == null`, and under EF Core's unconditional global `ITenant` query filter that means "rows whose own `TenantCode` is also null", not "all tenants" and not "this widget's own tenant". A background job for a real tenant would therefore see **neither** its own tenant's rows **nor** any other tenant's, independent of the DataPrivilege fix above. This is the Dashboard-shaped twin of #832 (the ETL scheduler's identical `CreateDC` gap — **#832 remains open, untouched, and unaffected by this PR**; it is a different consumer of the same root cause). `WidgetDataRequest.TenantId` existed as a field but neither `IDashboardService` implementation (`EfCoreDashboardService`, `JsonFileDashboardService`) ever populated it; both now do, and `AnalysisWidgetDataSource` uses it — only when `LoginUserInfo == null` and a `TenantId` is present, so interactive HTTP callers are byte-for-byte unaffected — to build the DataContext explicitly via `IWtmDataContextFactory.CreateDC(currentTenant:)`, the same no-HttpContext pattern `WorkflowEngine`/`WorkflowTimerHostedService` already use.

  **Blast radius, enumerated**: the only two production call sites of `ApplyDataPrivilegeForAnalysis` are `_AnalysisController` (interactive, always authenticated — behaviour unchanged) and `AnalysisWidgetDataSource` (background-reachable only via `DashboardSnapshotJob`/`DashboardAlertHostedService` — both enumerated above, no third consumer found: `grep -rn "ApplyDataPrivilegeForAnalysis" src/` returns exactly these two call sites). A deployment upgrading this package will see a background-executed widget's data go from "full, unscoped rows" to "empty" **only if** that widget's underlying model has a `DataPrivilege` rule configured for it — widgets against non-DataPrivilege-gated models are unaffected (positive control: still return their own tenant's rows correctly, and now MORE correctly than before, since the tenant-scoping fix above also applies to them). There is no config toggle to restore the old fail-open behaviour for a DataPrivilege-gated widget; restoring unfiltered access to a specific query is a code-level, reviewable decision (`declaredSystemQuery: true` at a new call site), by design.

  **What an operator will observe, and where to look.** Fail-closed on its own is silent — a background-executed widget going from full data to empty has no signal connecting the two unless something logs it, and "empty chart with no explanation" is exactly the failure shape this fix would otherwise reproduce at a different layer. So: whenever `ApplyDataPrivilegeForAnalysis` actually denies a query because of this default (no identity, and the model has a DataPrivilege rule configured — the no-op case for ungated models never logs), it emits a `LogLevel.Warning` via `CoreProgram.GetLogger("DCExtension")` — the same static-helper logging path `WtmFileProvider` already uses, since `DCExtension` is a static class with no DI-injected `ILogger`. **If a background snapshot/alert widget goes empty after upgrading, search the app's log for `ApplyDataPrivilegeForAnalysis denied all rows for`** — the message names the exact element type and states the remedy (`declaredSystemQuery: true` at the call site, if that specific query is genuinely meant to be unfiltered). **Throttled to once per element type per process** (a `ConcurrentDictionary`-backed guard) — this sits in a query path a scheduled job can re-enter every few minutes, and logging on every call would spam the log for a condition that doesn't change between calls; the throttle resets on process restart, so a redeploy naturally re-surfaces the warning rather than going silent forever. This throttle cadence is a deliberate choice over "per job run": there is no job/run identifier available at this call site (it is shared with the interactive, always-authenticated `_AnalysisController` path), and threading one down here would widen this shared static helper's surface well past what an observability fix warrants.

  Tests: `test/WalkingTec.Mvvm.Core.Test/Extensions/DPWhereInMemoryTests.cs` — fail-closed default, no-op on models without a configured rule, the `declaredSystemQuery` escape hatch (opt-in, proven not the default), proof the escape hatch cannot bypass an authenticated caller's own filtering, and five tests covering the warning: it fires and names the element type + remedy, it throttles to one warning across three repeated denials of the same element type, and it stays silent for the three cases that aren't a real denial (no configured rule, `declaredSystemQuery: true`, an authenticated caller's own pre-existing zero-privilege denial). `test/WalkingTec.Mvvm.Api.Test/DashboardBackgroundTenantIsolationTests843.cs` runs the real, unmocked background path — two tenants seeded via the `MultiTenantSeedFixtureTests`/`DbTestHelpers` pattern, `AnalysisWidgetDataSource` resolved from the demo app's own real DI container with no `HttpContext` in scope (exactly what `DashboardSnapshotJob`/`DashboardAlertHostedService` hand it) — and asserts a job for tenant A sees its own row but not tenant B's; verified this test actually fails (reverting to `WHERE TenantCode IS NULL`, seeing neither tenant) when the tenant-scoping fix is disabled. `JsonFileDashboardServiceTests`/`EfCoreDashboardServiceTests` each gained a `GetWidgetData_bridges_tenantId_into_request` test proving the `WidgetDataRequest.TenantId` wiring. Mutant `dcext843-declaredsystemquery-guard-neutralize` (`test/mutants/entries/`) pins the fix: deleting the `&& declaredSystemQuery` conjunct from the `LoginUserInfo == null` guard is verified KILLED.

  Version bumped to 10.20.0 (minor) per this repo's Red Line — the fail-closed default is a behavioural change for any deployment that (knowingly or not) relied on a background-executed, DataPrivilege-gated widget returning unscoped data.

- **#883 fix (P0): `EtlSchedulerService`'s HTTP-reachable methods are now tenant-scoped — a cross-tenant IDOR that #876's fix would otherwise have exposed, landed in the SAME PR so there is no window where the availability defect is gone but this is not.** A cross-vendor review of `b3dbae4b3` (#841/#862) found that seven of the `IgnoreQueryFilters()` calls added to keep the background Quartz scheduler working under the newly-enforced `ITenant` filter sit on paths ALSO reachable from HTTP controllers (`_EtlJobController`/`_EtlRunLogController`/`EtlJobDefinitionVM`: `TriggerNowAsync`/`RescheduleAsync`/`SkipNextAsync`/`DryRunAsync`/`RerunFromSnapshotAsync`/`ScheduleJobAsync`/`UpdateStatusAsync`), with no check that the caller-supplied `jobId`/`runLogId` belonged to the caller's own tenant. Combined with `EtlProgressTracker` having no tenant dimension (flagged but explicitly NOT fixed by #841's own body, item 4), tenant A's ETLAdmin could read tenant B's running `JobId` from `_EtlMonitorController.Running` and feed it to `DryRun` to read tenant B's actual source-data preview rows, or to `TriggerNow`/`Pause`/`Resume`/`Reschedule`/`SkipNext`/`Rerun` to operate on tenant B's job directly. Several of these also called straight into Quartz (`TriggerJob`/`Interrupt`/`PauseTrigger`/`ResumeTrigger`/`DeleteJob`/`RescheduleJob`) with no DB lookup at all on the common path, since Quartz's own trigger store has no tenant concept — the ownership check had to move before any Quartz call, not just before a DB write. **Sibling sweep found an eighth**: `AbortAsync` has the identical shape (not in the reviewer's own reported list) — found by re-deriving the call-graph from the actual code rather than trusting the reported list was exhaustive, per this repo's "review entire codebase for similar issues" convention, and fixed under the same commit.

  Fixed via `LoadJobDefinitionForCallerAsync` (job lookups) and `EnsureCallerOwnsJobAsync` (Quartz-only calls) — two shared private helpers every HTTP-reachable method now funnels through, requiring the caller's own `Wtm.LoginUserInfo?.CurrentTenant` to match the row's `TenantCode` unless the caller explicitly passes `declaredSystemQuery: true` (the #843 `declaredSystemQuery` contract, reused verbatim rather than inventing a second mechanism for the same "who is allowed to see every tenant's rows" problem — this repo established that exact contract three days earlier for the identical shape of bug). No production HTTP call site passes `true`. `EtlProgressTracker.Get`/`GetAll` gained the same `callerTenantCode`/`declaredSystemQuery` parameters.

  **Two more fixed in the same commit**: (A) the `[Obsolete]` zero-argument `ApplyEtlModels()` overload has no tenant filter at all, but `docs/etl-module.md`, `docs/workflow.md`, and `docs/wtm-developer-manual.md` (two call sites) still taught it — `[Obsolete]` only warns on recompilation against source, so a NuGet-only upgrade following the docs would never see it. All four call sites now show `ApplyEtlModels(this)`, with an inline note explaining why. (B) `EtlQuartzJob`'s background execution wrote dead-letter/lineage `TenantCode` from the background scope's own `dc.TenantCode` (always null there — same root cause as #862's original finding) instead of the already-loaded `jobDef.TenantCode` — meaning every multi-tenant job the built-in Quartz scheduler ever ran produced dead-letter/lineage rows with a permanently null tenant, hidden from every real tenant once #862's filter applies. `EtlRunLog` was unaffected (already correctly used `jobDef.TenantCode`).

  Tests: `EtlSchedulerCrossTenantIdorTests883.cs` — real HTTP negative controls for all eight entry points (tenant A's ETLAdmin rejected against tenant B's job/run-log, indistinguishable from a genuinely nonexistent id), a same-tenant positive control (`SkipNext_OwnTenantJobId_Succeeds`), and direct `EtlProgressTracker` tenant-scoping assertions. Mutant `883-loadjobdefinitionforcaller-tenantcheck-neutralize` pins the shared `LoadJobDefinitionForCallerAsync` guard — verified KILLED (`Abort_CrossTenantJobId_Rejected` is deliberately excluded from this specific mutant's red tests: verified empirically it stays green under this exact mutation, since `AbortAsync`'s own "job not currently running" check produces the identical HTTP status for this test's fixture, making it an unreliable kill signal for this one line even though the test itself remains a valid functional regression check). The pre-existing `etl862-scheduler-rerunsnapshot-ignorequeryfilters-neutralize` mutant's patch was regenerated to match `RerunFromSnapshotAsync`'s restructured lookup (same target line, same intent) and its test updated to pass `declaredSystemQuery: true`, since simulating a background/system caller now requires explicitly declaring that intent rather than getting it by default.

  **A cross-vendor review of this fix (PR #882) found the tenant leak was only half closed: `_EtlDashboardController.Stats` still called the PARAMETERLESS `EtlProgressTracker.GetAll()`.** `EtlDashboardService.BuildSummary` had two call sites (`RunningNow` count and the `Running` list, both projecting `JobId`) that never passed a tenant at all — the tracker's `callerTenantCode` parameter had a `null` default, so both calls fell through to `TenantCode == null`, i.e. HOST-scope jobs (EF Core's null-safe `==` translation: a `null` predicate matches only rows whose OWN `TenantCode` is also `null`). Effect ran in both directions at once: tenant A's dashboard could not see tenant A's own running jobs, and instead saw host-scope job ids/names/phases/rates that belong to no tenant. The new ownership check in `EnsureCallerOwnsJobAsync` already stops a leaked host `JobId` from being used against `DryRun`, so the earlier source-preview IDOR stays closed either way — this is a metadata-boundary leak plus a functional regression, not a re-opening of #883's main finding. Per the review's instruction to sweep rather than patch the two call sites in isolation: `EtlProgressTracker.Get`/`GetAll`'s `callerTenantCode` parameter is now **mandatory** (no default — see the breaking-change section below), so a future caller cannot reach the parameterless overload by omission the way this one did. `BuildSummary` now takes a required `callerTenantCode` and `_EtlDashboardController.Stats` passes `Wtm.LoginUserInfo?.CurrentTenant`. Test: `EtlSchedulerCrossTenantIdorTests883.DashboardStats_ReturnsOnlyCallersOwnTenantsRunningJobs_NotHostScope` seeds one host-scope and one tenant-A tracker entry directly and asserts tenant A's `/_EtlDashboard/Stats` response contains its own job id and not the host-scope one.

- **`FrameworkFilter.OnResultExecuted` interpolated an unescaped `ViewDivId` into an inline `<script>` body — authenticated-victim XSS, no antiforgery gate, default CSP allows `'unsafe-inline'` (#799, MEDIUM).** `FrameworkFilter.cs:394` wrote `$"<script>try{{ff.ResizeChart('{model?.ViewDivId}')}}catch{{}}</script>"` for every `PartialViewResult` whose model is a `BaseVM`, with no encoding. **Reachability, verified end to end (not assumed from the issue text) — `ViewDivId` is request-taintable, not developer-authored Razor content:** `WTMContext.CreateVM` (`WTMContext.CreateVM.cs:36-63`) copies every key in `HttpContext.Request.Form`/`.Query` into the new VM's `FC` dictionary verbatim, including a caller-supplied `"ViewDivId"` field nothing on this path validates; `_FrameworkController.Selector` (`[AllRights]` — authenticated, not per-page-privileged — POST endpoint, and the same `RedoUpdateModel`-then-`PartialView` pairing repeats at `_FrameworkController.cs:417`, `:777`, `:871`) calls `BaseController.RedoUpdateModel(listVM)`, which iterates `FC.Keys` and writes each value onto the matching VM property via raw reflection (`PropertyHelper.SetPropertyValue`) — this bypasses ASP.NET's own model-binder attributes entirely, so `ViewDivId` carrying no `[BindNever]` was never the gap. Neither of the two things that might otherwise have mitigated this apply: `grep -rn 'ValidateAntiForgeryToken|AutoValidateAntiforgeryToken' src/` returns nothing (no antiforgery layer anywhere in this codebase), and `WtmCspOptions.ScriptSrc` defaults to `"'self' 'unsafe-inline'"` (`src/WalkingTec.Mvvm.Mvc/ConfigOptions/WtmCspOptions.cs:42`), so the shipped default CSP does not block an injected inline `<script>` either. Fixed by encoding with `JavaScriptEncoder.Default.Encode` — not `HtmlEncode`, which does not escape `'`/`\` and would leave the JS-string-literal breakout intact — matching the encoder this repo already uses for the same sink shape in `DataTableTagHelper.cs` and in `PrivilegeFilter.cs`'s pre-existing `jsRedirect`/`jsLp` handling. **Sibling sweep**: grepped `FrameworkFilter.cs` and its `Filters/` siblings (`DataContextFilter.cs`, `PrivilegeFilter.cs`, `SwaggerFilter.cs`) for other unescaped interpolations into an inline `<script>` — none found; `PrivilegeFilter.cs:202`'s comparable `<script>var redirect='{jsRedirect}'...` sink was already correctly encoded. **No version bump / not a compatibility break**: `JavaScriptEncoder.Default` only rewrites characters outside its safe set (`<`, `>`, `&`, `'`, `"`, `\`, control chars); the default-generated `ViewDivId` (`BaseVM.ViewDivId`'s own getter: `"ViewDiv" + UniqueId`, alphanumeric) and any ordinary developer-set id contain none of them, so rendered output is byte-identical for every existing app's normal usage — proven by a positive-control test, not just inspection. Test: `test/WalkingTec.Mvvm.Admin.Test/FrameworkFilterViewDivIdXssTests799.cs` drives a hostile `ViewDivId` through the real `Request.Form` → `WTMContext.CreateVM` → `Selector`'s own `RedoUpdateModel` → a real `FrameworkFilter.OnResultExecuted` instance, and asserts on the actually-escaped bytes being present (computed via the same `JavaScriptEncoder.Default.Encode` the fix uses) — not merely on the raw payload's absence. Mutant `mvc799-viewdivid-xss-script-encode` (`test/mutants/entries/`) pins the fix: neutralizing the `JavaScriptEncoder.Default.Encode(...)` call is verified KILLED.

- **`_Framework/GetFile`/`ViewFile` unauthenticated cross-tenant file read; `FileUploadOptions.EnforceTenantFileScope` default flipped to `true` (#859, P0, BREAKING).** `_FrameworkController` (`src/WalkingTec.Mvvm.Mvc/_FrameworkController.cs`) has no `[Authorize]`-family attribute of its own; the only gate is `PrivilegeFilter`. All three demo templates (`demo/WalkingTec.Mvvm.Demo`, `demo/WalkingTec.Mvvm.Vue3Demo`, `demo/WalkingTec.Mvvm.BlazorDemo`) shipped `"IsFilePublic": true`, which makes `PrivilegeFilter.cs:111-122` treat `GetFile`/`ViewFile` as anonymous — a full early return (`:147-151`) that skips the `LoginUserInfo == null` check entirely. Combined with `WtmFileProvider.GetFile`'s `FileUploadOptions.EnforceTenantFileScope` defaulting to `false` (`IgnoreQueryFilters()`), an unauthenticated caller who knew or guessed a `FileAttachment` GUID could read that file's content regardless of which tenant uploaded it — worst on Vue3Demo, the only template shipped `IsQuickDebug:false` + `EnableTenant:true` (a production-shaped default). This is the sibling of #830 (which closed the identical threat model on the demo `FileApiController` copies) on the framework's OWN `_FrameworkController` route, which #830 did not touch.

  Fixed in two halves, both required:
  - **Package (`src/WalkingTec.Mvvm.Core/ConfigOptions/FileUploadOptions.cs:37`): `EnforceTenantFileScope` default flipped `false` → `true`.** `WtmFileProvider.GetFile` now honours the global EF Core `ITenant` query filter by default, so a file resolves only within the caller's own tenant scope regardless of which route reached it — this reaches **existing deployments that upgrade the package and change nothing**, including one running a copied `FileApiController` from before #830, because that controller also resolves files through `WtmFileProvider`. Startup diagnostic added: `FrameworkServiceExtension.UseWtmContext` now emits `LogCritical` (not `throw` — some operators may have `IsFilePublic=true` on deliberately) naming the exposed routes whenever `Configs.IsFilePublic == true` in a non-Development environment, mirroring the existing `IsQuickDebug` startup guard.
  - **Template**: `IsFilePublic` set to `false` in all three demo `appsettings.json`, each with an inline comment stating what enabling it does. Confirmed `git ls-files 'demo/**/publish/**'` returns 0 — the nested `publish/` build-output copies the issue also flagged are untracked local artifacts (already covered by `.gitignore:14,165`), not committed; nothing was deleted on that basis.

  **Breaking-change analysis** (why this is safe to default-flip, not just theoretically desirable — full text in `docs/production-readiness.md`'s #859 entry):
  - **NULL-tenant legacy files**: unchanged behaviour, by deliberate choice. EF Core's null-safe `==` translation means a `TenantCode == null` file resolves only for a caller whose own resolved tenant is ALSO `null` — the same precedent `WtmFileProvider.DeleteFileTenantScoped` already established for #815. Not widened here: doing so would reopen the exact cross-tenant primitive #815 closed.
  - **Every `Upload(` call site stamps `TenantCode`**: audited every call site in `src/` and `demo/` (`_FrameworkController.cs`, `BaseImportVM.cs`, all three demo `FileApiController.cs`, `WtmFileProvider.Upload`, `WtmDataBaseFileHandler.UploadToDB`); all set `TenantCode = _wtm.LoginUserInfo?.CurrentTenant` (or `wtm.LoginUserInfo?.CurrentTenant`) before saving. No unstamped path found.
  - **Background/no-identity paths**: none of the six `IHostedService`/`BackgroundService` classes in this repo (`EtlHostedService`, `DashboardSnapshotHostedService`, `DashboardAlertHostedService`, `ActionLogRetentionService`, `RefreshTokenRetentionService`, `WorkflowTimerHostedService`) call `WtmFileProvider.GetFile` — the flip does not break any existing background file-read path. (#843 is the same "`LoginUserInfo==null`" root-cause family in the OPPOSITE direction — `ApplyDataPrivilegeForAnalysis` fails *open* for background analysis queries; `GetFile`'s tenant filter fails *closed*. Unrelated fix, unaffected by this one, still open.)
  - **`HasMainHost`**: all three demo templates ship `mainhost.Address` commented out, so `Configs.HasMainHost` defaults to `false`; the `GetUserPhoto` redirect that depends on it runs before any `GetFile` call and does not interact with tenant scoping. No conflict found.
  - **Known residual scope change**: a file uploaded with a real (non-null) `TenantCode` that was *intended* to be publicly readable across all tenants no longer resolves for an anonymous caller after this flip (unless the anonymous request's Referer happens to resolve to that same tenant via the #116 mechanism). Re-upload such files through a null-tenant/main-host context, or serve them from a dedicated public file store, to preserve that use case.

  **Adjacent fix found while writing the acceptance tests**: `WtmDataBaseFileHandler.GetFileData` (`src/WalkingTec.Mvvm.Core/Support/FileHandlers/WtmDataBaseFileHandler.cs`) ran its OWN, always-on tenant-scoped query (no `IgnoreQueryFilters()`, independent of `EnforceTenantFileScope`) to fetch a `"database"`-SaveMode file's bytes, one step after `WtmFileProvider.GetFile`'s own query had already resolved (and, per `EnforceTenantFileScope`, already authorized) the same row. Whenever the two disagreed — which, pre-#859, happened on every genuine cross-tenant read, since the outer query bypassed the filter by default while this inner one never did — the mismatch surfaced as an unhandled `NullReferenceException` (observed as HTTP 400 via the framework's error handler) instead of either serving the file or cleanly declining it. `WtmLocalFileHandler`/`WtmOssFileHandler` have no such second filter, so this was a `"database"`-mode-only inconsistency, and it directly contradicted this very fix's own documentation: with `EnforceTenantFileScope` explicitly opted back to `false` for legitimate tenant-agnostic sharing (a case this CHANGELOG entry and the property's doc comment both say remains supported), a `"database"`-mode file would crash instead of being served. Fixed by having `GetFileData` trust the outer query's already-completed authorization (`IgnoreQueryFilters()` — safe because `GetFileData` has exactly one caller in this repo, always downstream of that authorization). Not itself a new hole: `GetFileData` was never reachable with a caller-controlled id that bypassed the outer check.

  Version bumped to 10.19.0 (minor) per this repo's Red Line — no `<PackageReference>` changed, but `EnforceTenantFileScope`'s default is a behavioural change for any consumer relying on the old tenant-agnostic-by-ID lookup.

- **LayUI demo: anonymous POST could create a `FrameworkMenu` row and turn any URL into an anonymous endpoint (#840, P0).** `FrameworkMenuController.Create` (`demo/WalkingTec.Mvvm.Demo/Areas/_Admin/Controllers/FrameworkMenuController.cs`) carried `[Public]`, which `IAllowAnonymous` makes `PrivilegeFilter` return on unconditionally — before both the `LoginUserInfo == null` check and the class-level `[MainTenantOnly]` check. An unauthenticated caller could POST a `FrameworkMenu` row with `IsPublic=true` pointing at any endpoint; `WTMContext.IsUrlPublic`, consulted by `PrivilegeFilter` on every request, then treated that endpoint as anonymous too. Fixed by removing `[Public]`; the action now requires authentication like every other write action in the controller. **Scope: LayUI demo template only** — Vue3Demo, BlazorDemo, and the same demo's API-style `FrameworkMenuController` never had `[Public]` on this action, and the vulnerable code is not part of any published NuGet package. Inherited from upstream WTM since 2020-12-12; not introduced by this fork.

- **Batched `FileAttachment` resolution query failures silently narrowed to "nothing resolved", which could delete every existing child row on an otherwise-legitimate edit while still reporting success (#828, P0, no attacker required).** `BaseCRUDVM.ResolveFileAttachmentIdsForCaller`/`...Async` (`src/WalkingTec.Mvvm.Core/BaseCRUDVM.cs`) caught any exception thrown by the batched `FileAttachment` resolution query and returned an EMPTY resolved set — indistinguishable from "the query succeeded and none of these ids exist". `ApplyFileAttachmentResolution` then applied its per-item narrowing rules (#815 fourth/sixth/seventh round: revert a scalar FK to its prior value, drop-or-restore an `ISubFile` sub-item) to every posted candidate as if each had genuinely failed to resolve. For a posted `ISubFile` collection whose every item happened to be a candidate — the ORDINARY case, since editing an entity normally re-posts its own unchanged children too — every item was dropped, emptying the whole posted collection, which routes into `DoEditPreparePart2`'s empty-collection branch: it physically deletes EVERY existing child row for that parent, and `DoEdit` reports success. A dependency failure that has nothing to do with any candidate id's legitimacy must never be able to reach that branch.

  **Verified, not assumed, that the stated trigger (SQL Server's 2100-parameter-per-query cap) actually applies to this repo**: this repo is pinned to EF Core 10.0.9 (`Directory.Packages.props`). EF Core 8/9 sidestepped the cap entirely — a parameterized `Contains()` collection translated to a single JSON-array parameter unpacked via SQL Server `OPENJSON`. EF Core 10 reverted the DEFAULT translation back to one scalar SQL parameter PER candidate id (see [EF Core 10 breaking changes](https://learn.microsoft.com/ef/core/what-is-new/ef-core-10.0/breaking-changes#parameterized-collections-now-use-multiple-parameters-by-default)) specifically because the OPENJSON form produced bad query plans for a minority of real workloads. So on THIS repo's pinned EF Core version, a large candidate-id batch really can throw purely from its own size, on a request that posted nothing malicious — the claim holds here, but would not have held on EF Core 8 or 9.

  Fixed by adding a `Succeeded` flag to the resolution result: `RejectUnresolvableFileAttachmentReferences`/`...Async` now rejects the WHOLE request when the resolution query itself failed — `MSD.AddModelError`, nothing staged, `SaveChanges` never called — the identical "controlled request-level rejection" contract the pre-existing required-FK-with-no-legitimate-prior-value path already used (#815 sixth/seventh round), instead of ever handing an ambiguous empty set to the per-item narrowing logic. This does not change the established non-error narrowing behaviour (revert/drop/restore/reject) at all — it only changes what happens when the resolution QUERY ITSELF throws.

  **Defense in depth, not the fix itself**: the resolution query is now also split into 500-id batches (`FileAttachmentResolutionBatchSize`), comfortably under SQL Server's hard cap, so that a large but entirely legitimate form (`FormOptions.ValueCountLimit` is 5000, `FrameworkServiceExtension.cs:458`) no longer routinely hits the specific 2100-parameter trigger at all. This is deliberately layered ON TOP of the fix above, not a substitute for it: a batch failure (timeout, connection drop, or any other cause) still goes through the same whole-request rejection, never narrowed.

  No config default changed and no persisted data shape changed — no migration required. The only observable behaviour change is on the previously-buggy path: a resolution failure now surfaces as a request-level validation error instead of a silent partial delete reported as success.
- **Vue3 image-loading regressions introduced by #830's blob-URL fix, all three fixed (#856).** #830 moved Vue3 image loading from raw `<img src>` to `fileApi().getFile()` + blob URLs so removing `[Public]` from `GetFile` would not 401 every image — correct, but it introduced three side effects, all in `ClientApp/src`. **(1) Serial N×M image fetch:** `components/table/index.vue`'s `doSearch()` awaited `fileapi().getFile()` one row/column at a time inside a nested `for` loop; with the grid's 200-row page size, the whole table stalled behind a fully serial chain of network round trips before any row rendered — previously `<img>` tags loaded in parallel and rows rendered immediately. Fixed with a bounded worker pool (`IMAGE_FETCH_CONCURRENCY = 6`) that preserves the original row-major preview-index ordering; an unbounded `Promise.all` over every task was considered and rejected, since it would replace a serial stall with up to `rows × imageColumns` simultaneous requests. **(2) Blob races on overlapping searches and unmount:** the same function had no generation token, no stale-result check, and no unmount guard — a superseded search (page/filter/sort change, or navigating away) could still apply its results and blob URLs after a newer search had already revoked and reset, or after the component unmounted. Fixed with a monotonic `searchToken` plus an `isComponentMounted` flag, checked before the search API call, after each per-image fetch resolves, and again before the batch is applied to `state.data`/`state.picList`; a stale batch revokes every blob URL it produced instead of leaking or racing. **(3) `uploadImage` leaked a stale blob on clear:** `components/uploadImage/index.vue`'s `modelValue`-cleared branch reset `files` but left `imageUrl`/`previewUrlList` (and the outgoing `files` entries) pointing at blob: URLs nothing would ever revoke — a stale preview before blob URLs existed, a leaked capability after. Now revokes and resets all three. **Verification**: this repo has no JS test harness covering Vue3 SFCs (the Jest suite in `test/WalkingTec.Mvvm.Js.Tests` only loads plain-script `framework_layui.js`/`framework_analysis.js` via `vm.Script`, and the e2e suite runs against the LayUI demo, not Vue3), so these three fixes are verified by manual trace of the token/mounted-flag state machine plus a standalone timing script (not committed) comparing the old serial loop against the new bounded pool against a stubbed `getFile()` — roughly 6.6s serial vs 1.1s pooled at concurrency 6 for 400 simulated thumbnail fetches (≈5.9×, matching the concurrency factor), and both changed `.vue` files were confirmed to parse/compile via `@vue/compiler-sfc`'s real `compileScript()`. No automated regression test and no mutant were added for these three files — extracting the fetch logic into a separately-testable module was considered and rejected as its own source of risk (this project's `tsconfig.json` has no `allowJs` and only includes `src/**/*.ts`/`*.vue`, so a plain-JS helper would sit outside the type-checked build). **Also corrects this section's own claim** (a few paragraphs below) that all three blob call sites revoke on component unmount — `stores/userInfo.ts` is a Pinia store with no such lifecycle hook and only ever revoked on replacement; that text has been reworded to say so.

- **#828's fix covered one call site; the same defect was intact in `LoadExistingSubItemFileIds` — and an exhaustive pass turned up a THIRD, undiscovered site (#875, P0, no attacker required).** `BaseCRUDVM.LoadExistingSubItemFileIds` (`src/WalkingTec.Mvvm.Core/BaseCRUDVM.cs`) — the lookup `ApplyFileAttachmentResolution` uses to tell "a rejected sub-item is a brand-new forged item" (safe to drop) from "a rejected sub-item matches an EXISTING child row" (must be restored, not dropped — #815 sixth/seventh round) — caught any exception from its own query and fell through to `return result` with whatever it had accumulated (possibly nothing), indistinguishable from "the query ran fine and confirmed no existing row matches". Every rejected item was then unconditionally dropped instead of restored; when that emptied the whole posted collection, `DoEditPreparePart2`'s empty-collection branch physically deleted every existing child row for the parent while `DoEdit` reported success — the identical #828 data-loss chain, one call site over.

  **The exhaustive pass the issue required before fixing anything** (every `catch` in `BaseCRUDVM.cs`, not just the two already known) found a THIRD site with the same shape: `LoadEntitySnapshot`/`LoadEntitySnapshotAsync` — the sole source of `preSaveSnapshot` for `DoEdit`/`DoEditAsync`/`DoDelete`/`DoDeleteAsync` — also caught its query's exceptions and returned a bare `null`, indistinguishable from the two LEGITIMATE reasons it already returns `null` (`Entity.ID` not set; the row was genuinely deleted concurrently). `ApplyFileAttachmentResolution` reads `preSaveSnapshot == null` as "this is an Add, there is no prior DB state" and, on that reading, skips the `LoadExistingSubItemFileIds` restore-vs-drop check ENTIRELY — so even a snapshot-load hiccup on a genuine Edit, with `LoadExistingSubItemFileIds` itself working fine, reached the same delete-every-child outcome by misclassifying the request as an Add before the sub-item lookup was ever reached. Every other `catch` in the file was reviewed and is either the correct existing fail-closed pattern (the `SaveChanges` catches already gate file-deletion on a `saved` flag; `DoRealDelete`/`DoRealDeleteAsync` wrap their own lookup query and `SaveChanges` in the SAME try, so a query failure there already prevents the delete) or wraps a reflection property getter/setter, never a DB round-trip, and so cannot fabricate a false "row not found" answer.

  Both sites are fixed in this PR, reusing the exact contract #828 already established rather than inventing a second one: `LoadExistingSubItemFileIds` now returns a `Succeeded` flag alongside its result (discarding any partially-accumulated entries on failure, same as `ResolveFileAttachmentIdsForCaller` already does), and `LoadEntitySnapshot`/`Async` now return `EntitySnapshotResult(Succeeded, Snapshot)`. `ApplyFileAttachmentResolution` rejects the whole request when the sub-item lookup fails; `DoEdit`/`DoEditAsync`/`DoDelete`/`DoDeleteAsync` reject the whole request when the snapshot load fails — `MSD.AddModelError`, nothing staged, `SaveChanges` never called, in both cases. `AppendEditChangeLog` (used by `_FrameworkController.UpdateModelProperty`'s narrow single-property save path, which never calls `DoEditPrepare`/`ApplyFileAttachmentResolution` and so cannot reach the deletion branch) deliberately keeps its prior best-effort behaviour on a failed snapshot load — only that path's `ChangeLog.OldValues` audit field degrades, not a data-loss risk.

  **Chunking**: `LoadExistingSubItemFileIds`'s query is `Contains()`-shaped over the rejected items' ids, which can be as large as the whole posted sub-item collection — subject to the identical EF Core 10 / SQL Server 2100-parameter-per-query cap #828 documented for the resolution query. It is now batched into `FileAttachmentResolutionBatchSize`-sized (500) queries for the same reason #828 batched its own resolution query — defense in depth against the routine trigger, not a substitute for the `Succeeded`-flag fix itself.

  **Known, deliberate behaviour narrowing**: a snapshot-load failure now fails the WHOLE Edit/Delete request, even for a `TModel` with no `FileAttachment`/`ISubFile` properties at all — previously that case silently proceeded with a `null` snapshot (only the audit `ChangeLog.OldValues` and `DeletedFileIds` cleanup were degraded). Adding a second, narrower contract keyed on "does this model even have file properties" would duplicate the #828 rule rather than reuse it; this PR keeps the single "a query failure always rejects the whole request" rule instead. No config default changed and no persisted data shape changed — no migration required.

  Tests: `ExistingSubItemLookupFailureGateTests875.cs` (sync/async existing-sub-item-lookup failure with a positive control proving both the normal save AND the restore-in-place behaviour, plus a >batch-size regression) and `EntitySnapshotLoadFailureGateTests875.cs` (sync/async third-site coverage, same positive-control shape) — both on the FK-enforcing SQLite `ProductSubFileContext` fixture with a `DbCommandInterceptor` precisely scoped to each target query (distinct table names so the three #815/#828/#875 interceptors never collide). Mutant `test/mutants/entries/existingsubitem875-lookup-failure-guard-neutralize.json` — `VERDICT: KILLED`.

- **`WalkingTec.Mvvm.WorkFlow`'s 10 `ITenant` entities never received the global tenant (or, for 6 of them, soft-delete) query filter — the same `ApplyXxxModels()` zero-arg wiring-order defect #862 fixed for ETL, one module over; independently confirmed Critical by a cross-vendor architecture audit that reached the same conclusion without knowing this issue existed (#899, P0).** `WorkFlowDbContextExtensions.ApplyWorkFlowModels()` (`src/WalkingTec.Mvvm.WorkFlow/ServiceCollectionExtensions.cs`) registered all 10 WorkFlow entity types via `modelBuilder.Entity<T>()`, but every documented call site (`docs/workflow.md`, `demo/WalkingTec.Mvvm.Demo/DataContext.cs`) invoked it from the consumer's own `DataContext.OnModelCreating` AFTER `base.OnModelCreating()` returns — by which point `FrameworkContext.OnModelCreating`'s own Pass 2 loop (`DataContext.cs`) had already finished iterating `modelBuilder.Model.GetEntityTypes()` and applying the `ITenant`/`IPersistPoco` filters for every entity type known to the model AT THAT POINT. It could never retroactively see a type registered afterward. All 10 entities implement `ITenant`, but the filter was never actually enforced through a standard `FrameworkContext`-derived app — including the framework's own reference demo, which declares zero WorkFlow `DbSet`s and so has no other path to the filter either.

  **Not a copy of ETL's fix — the filter composition had to be wider.** Core's Pass 2 applies a SINGLE combined filter, `IsValid == true && TenantCode == this.TenantCode`, for any entity that is both `IPersistPoco` and `ITenant` (`DataContext.cs:238-258`, via `Expression.AndAlso`) — a tenant-only filter (ETL's `ApplyEtlTenantFilter<T>`) is only correct for a `BasePoco`-only entity, which is all ETL has. WorkFlow has BOTH kinds among its 10 `ITenant` entities: **`PersistPoco, ITenant` (6)** — `ProcessDefinition`, `ProcessInstance`, `ProcessDefinitionVersion`, `ProcessDefinitionDraft`, `ApprovalTask`, `DelegationRule`; **`BasePoco, ITenant` (4)** — `NodeInstance`, `WorkflowTimer`, `CcRecord`, `WorkflowEventLog`. Copying ETL's tenant-only helper as-is would have left the 6 `PersistPoco` entities without their soft-delete filter. **This widens the defect's impact beyond multi-tenant deployments**: soft-delete filter absence for the 6 `PersistPoco` entities affects SINGLE-tenant deployments too — every framework-standard query saw soft-deleted `ProcessDefinition`/`ApprovalTask`/`DelegationRule`/etc. rows regardless of tenancy, before this fix.

  Fixed with a new `ApplyWorkFlowModels(ModelBuilder, EmptyContext)` overload that re-derives the combined-vs-tenant-only choice per entity type from its own `IPersistPoco`-ness at the call site (mirroring `DataContext.cs` Pass 2's own logic, not assuming ETL's all-`BasePoco` shape), and applies it for all 10 entities immediately after registering each one. The old zero-argument overload is kept (`[Obsolete]`, table/column/index registration unchanged) for callers who have not yet updated their `DataContext.OnModelCreating` to pass `this`; `demo/WalkingTec.Mvvm.Demo/DataContext.cs` now passes `this`.

  **Migration: none.** All 10 entities already implemented `ITenant` and their `TenantCode` columns/indexes were already registered by `ApplyWorkFlowModels`. Unlike #862, no column-adding migration is needed — verified, not assumed: a query filter is EF metadata, never emitted into a migration/DDL diff.

  **Engine background writes verified, not assumed.** Every `new NodeInstance/ApprovalTask/WorkflowEventLog/WorkflowTimer/CcRecord/ProcessInstance/ProcessDefinition*` construction under `src/WalkingTec.Mvvm.WorkFlow/Engine` and `Definition` that is actually persisted (`db.Set<T>().Add(...)` / `_dc.AddEntity(...)`) was enumerated: every one stamps `TenantCode` from an already-tenant-scoped source (the owning `ProcessInstance`/`NodeInstance`/`ProcessDefinition`, or a snapshot variable captured from one via an `IgnoreQueryFilters()` cross-tenant read). The handful of constructions that do NOT set `TenantCode` are transient "for notifier" shell objects (`WorkflowTimerExecutor.*.NotifyXxxAsync`) passed straight to a notifier call and never added to a `DbSet` — confirmed by reading each call site, not inferred from naming. No NULL-`TenantCode` write path was found, so no row silently vanishes for a tenant context once the filter activates.

  **Write-PROVENANCE gap found by cross-vendor review of PR #918 (Blocking 1), fixed.** The `TenantCode` an already-tenant-scoped source PROVIDES and the `TenantCode` a caller-supplied PARAMETER claims are not automatically the same thing. `WorkflowEngine.StartAsync`'s `tenantCode` parameter was written onto the new `ProcessInstance` verbatim, with nothing checking it agreed with the `ProcessDefinitionVersion` just loaded (itself already tenant-scoped by the active filter); `WorkflowDefinitionStore.CreateDefinitionAsync`'s `tenantCode` parameter was written onto the new `ProcessDefinition` verbatim, with nothing checking it agreed with the calling `IDataContext`'s own `TenantCode`. A caller passing a mismatched value — including simply `null` by omission, the likeliest un-migrated-caller shape — would silently write a row under a tenant nobody's context can ever query again. Both methods now compare the parameter against the authorized source (`version.TenantCode` for `StartAsync`; `_dc.TenantCode` for `CreateDefinitionAsync`) and throw `InvalidOperationException` BEFORE any write on a mismatch (including the null-vs-non-null case) — fail closed, not silently misfiled. Four new tests prove both the mismatch and the null-vs-non-null shape for both methods, each asserting via `IgnoreQueryFilters()` that NO row was written at all (not merely invisible to the caller's own tenant).

  **Engine query-filter activation, decided not accidental.** This fix activates 30+ previously-no-op `IgnoreQueryFilters()` calls in `WorkflowTimerExecutor.*` from no-op to real (with no filter registered, `IgnoreQueryFilters()` was a documented no-op), and makes every non-ignored engine query go from unfiltered to filtered. The bare (unnamed) `IgnoreQueryFilters()` form is kept deliberately rather than switching to EF Core 10's named query filters: every one of those sites is a cross-TENANT system sweep (background reaper/timer executor, no per-request identity), and TODAY — before this fix, with no filter active at all — those sweeps already saw `IsValid == false` rows too. Composing the new combined filter with a bare `IgnoreQueryFilters()` therefore PRESERVES that existing soft-delete visibility for the sweeps (only the tenant dimension changes, from "always ignored because nothing existed to ignore" to "explicitly ignored because the sweep is cross-tenant by design") instead of silently narrowing sweep visibility to `IsValid == true` rows as an accidental side effect.

  **Per-site classification found incomplete by cross-vendor review of PR #918 (Blocking 2), fixed.** "All 32 sites are cross-tenant sweeps, therefore all should be soft-delete-inclusive" does not follow — the sweep needing to see OTHER tenants' rows does not mean it should also see SOFT-DELETED rows. Re-audited every candidate/notify query site and split them into two real categories: (a) **cross-tenant but must stay `IsValid == true`** — the CANDIDATE selection deciding "is this a live thing to act on" (GATE-0's `ProcessInstance` read in `WorkflowTimerExecutor.Fire.cs`; the task-scoped reads in `.Escalate.cs`/`.Remind.cs`/`.AutoAction.cs`; the sweep candidates in `.Sweep.cs`/`.StrandReaper.cs`) and the post-commit notify re-reads (`freshInstance` in every `Notify*Async`) all gained an explicit `IsValid == true`/`i.IsValid` predicate alongside the bare `IgnoreQueryFilters()`; (b) **cross-tenant AND must include tombstones** — the two WF-19 FIX-2 unique-index collision pre-checks (`Escalate.cs`'s `hasCollision`, `Sweep.cs`'s AtAction-sweep equivalent) are deliberately left bare, with a new comment explaining why: the unique index they probe (`IX_Wf_ApprovalTask_Node_Assignee_Gen`) is not itself filtered on `IsValid`, so a soft-deleted row still physically occupies the slot and a real INSERT there would still throw regardless — the probe must see what the database itself would reject on. Two new end-to-end tests against the real `WorkflowTimerExecutor.RunTickAsync` (not a reimplementation) prove the "zero mutation / zero event / zero notification" property for a soft-deleted `ProcessInstance` (GATE-0) and an individually soft-deleted `ApprovalTask` (task-level fix), each engineered to isolate ONE specific line's contribution from the several OTHER, independently-redundant protections already present (documented in the tests' own comments: `GuardedTransition`'s CAS helpers are not `IgnoreQueryFilters()`'d, so the already-active combined filter blocks a soft-deleted row's mutation on its own in most cases — verified empirically by reverting each fix locally and confirming exactly which assertion, if any, goes red). Full site × ignore × context-source × before/after inventory verified against the `WorkflowTimerExecutor.*`/`NodeKindHandlers.cs` source during this fix; the full WorkFlow test suite (605 tests: 599 original + 4 write-provenance + 2 soft-delete-classification) passes with the filter active.

  **Not fixed here, and why**: WorkFlow's runtime tenant *semantics* are unchanged by design — this is the ETL-precedent wiring fix, not a redesign. The structural class of defect (any zero-argument `ApplyXxxModels(this ModelBuilder)` extension method can never bind a query filter to a context instance) is tracked separately in #901 (an `IModelFinalizingConvention`-based fix that closes the gap at the framework level, for consumers who never recompile against the new overload) — #899 does not replace it, both are needed. `ApplyDashboardModels` (`src/WalkingTec.Mvvm.Core/Dashboard/EfCoreDashboardDbContext.cs`) has the identical zero-argument shape and would have the identical defect if its entities implemented `ITenant` — they use a hand-rolled `TenantId` string property instead, so there is no `ITenant` filter to fail to apply there; #901's convention would close that gap automatically if that ever changes.

  **Corrected a second, independent defect found while auditing this one**: `IWorkflowDefinitionStore.CreateDefinitionAsync`'s duplicate-`Code` check (`WorkflowDefinitionStore.cs`) has no `TenantCode` predicate of its own — it always relied on the (previously nonexistent) global filter. The schema's own unique index is COMPOSITE `(TenantCode, Code)`, so two tenants sharing a Code is by design, but in practice a Code used by ANY tenant was rejected for every other tenant. This fix's filter activation corrects it as a side effect (the duplicate-check query is now automatically tenant-scoped); a dedicated acceptance test (tenant A creates code X, then tenant B can still create code X) exercises the real `WorkflowDefinitionStore.CreateDefinitionAsync` production code path, not a reimplementation.

  **Tests, in two structures because the naive one-test version cannot work**: the fix adds a new overload, so a test using the new wiring does not compile against the pre-fix tree (a compile failure, not a red test run, per this repo's own rule). `TenantFilterInvariantTests.cs` (`test/WalkingTec.Mvvm.WorkFlow.Test/`) was rewritten into two independent fixtures instead of one: (1) `WorkFlowObsoleteOverloadCharacterizationTests` — a demo-shaped context (no `DbSet<T>` declared, matching `demo/WalkingTec.Mvvm.Demo/DataContext.cs`'s actual shape) calling the zero-arg `ApplyWorkFlowModels()`, asserting NO entity gets a filter — green before AND after this fix, pinning the `[Obsolete]` overload's promised unchanged behaviour, plus a runnable reproduction of the duplicate-Code bug under that permanently-unfiltered wiring; (2) `TenantFilterInvariantTests` — the same demo shape calling the NEW `ApplyWorkFlowModels(this)`, asserting the filter both exists AND behaves: two tenant-scoped contexts each see only their own rows (all 10 entity types), the 6 `PersistPoco` types hide `IsValid=false` rows, and `IgnoreQueryFilters()` restores visibility (proving a query filter, not missing data) — plus dedicated single-entity tests and the acceptance test above. The pre-#899 version of this file declared its own nine `DbSet`s and hand-applied the filter by hand, so it could never fail when production wiring broke; confirmed directly (`git stash` the source fix, rebuild the test project) that the rewritten fixture's new-wiring half fails to COMPILE against the pre-fix tree (`CS1501`). **Correction (cross-vendor review of PR #918):** there is no executable pre-fix RED for the acceptance criterion itself — MSTest builds the whole test project as one assembly, so a single compile error anywhere (here, the new-wiring fixture) fails the entire project's build regardless of file boundaries, and no fixture in this project can be *run* against the pre-fix tree, not even the characterization one. What IS runnable both before and after this fix, without needing the new overload, is the permanent characterization fixture's duplicate-Code reproduction — but it is a GREEN characterization (asserts the buggy outcome as expected under the permanently-unfiltered old overload), not a RED test; it documents the defect is real, it does not substitute for a failing pre-fix assertion of the fixed behaviour. The binding, CI-enforced evidence that the fix itself works is the mutant below. SQLite shared-memory fixture throughout (never EF InMemory, per repo rule). Mutant `test/mutants/entries/wf899-applyworkflowmodels-processdefinition-tenantfilter-neutralize.json` targets the `ApplyWorkFlowTenantFilter<ProcessDefinition>` call, mirroring #862's `etl862-applyetlmodels-tenantfilter-neutralize.json` precedent exactly (red + positive-control pair, same entity).

  **The filter above was necessary but not sufficient — every request-path WorkFlow service built its own module DataContext with the tenant unset, so the filter bound to `null` regardless of who was actually asking. Closed by a session-half fix landing in this same PR.** `WorkflowEngine`/`WorkflowTimerExecutor` (via `ScopedWorkflowDataContextHolder.Resolve()` → `ResolveDataContext`), `WorkflowDefinitionStore`, and `ProcessDefinitionPublisher` all create their own module-scoped `IDataContext` through `IWtmDataContextFactory.CreateDC()` called with NO arguments — `WtmDataContextFactory.CreateDC`'s own tenant resolution only runs when a `currentTenant` argument is supplied, so every one of these contexts had `TenantCode == null` unconditionally, in every deployment, regardless of the caller's actual tenant. In a migrated multi-tenant deployment the newly-active filter (`TenantCode == this.TenantCode`) then matched **zero rows** for every module read through these three services — the exact migration this PR's own docs teach would have left the designer's UI listing definitions the engine, reading through a differently-tenant-scoped context, could never find (worse than the pre-#899 no-filter-at-all state for a migrated multi-tenant consumer). In single-tenant deployments the missing stamp was a no-op (`null == null`), so the model-half fix alone was already fully effective there.

  Fixed with a `SetTenantCode` **stamp, not a connection re-route**: `ResolveAmbientTenant(IServiceProvider)` (new, `ServiceCollectionExtensions.cs`) reads `sp.GetService<WTMContext>()?.LoginUserInfo?.CurrentTenant` — the same tenant source `WTMContext.CreateDC()`'s own instance method already resolves for every other framework DataContext (`WTMContext.CreateDC.cs`) — and `ResolveDataContext` now calls `dc.SetTenantCode(ResolveAmbientTenant(sp))` immediately after the factory creates a context (covering `IWorkflowEngine`/`WorkflowTimerExecutor` via the scoped holder), while `WorkflowDefinitionStore`/`ProcessDefinitionPublisher` each gained a new `(IWtmDataContextFactory, string? tenantCode)` constructor overload that does the same, called from their DI factory lambdas — the existing zero/two-argument production constructors are unchanged and simply delegate with `tenantCode: null` (binary compatible). **Deliberately NOT `CreateDC(currentTenant: ...)`**: `WtmDataContextFactory.CreateDC` re-routes a tenant with `IsUsingDB == true` and no explicit connection-string key to a DIFFERENT physical database (`CreateTenantDC`) — this module has always written its `Wf_*` tables to the default connection, and the parameter path would have smuggled a connection-routing change into what is supposed to be a filter fix. The background timer scope has no `HttpContext`, so `WTMContext.LoginUserInfo` short-circuits to `null` there — `ResolveAmbientTenant` returns `null`, identical to the value `ResolveDataContext` always produced before this fix, so the timer/reaper path (already tenant-aware via its own pre-existing per-candidate `SetTenantCode` calls in `WorkflowTimerExecutor.*.cs`) is byte-identical. This specific stamping change does not itself modify `TimerReaperTests.cs`, and the 42 pre-existing tests in that file are unaffected by it — the one addition to that file is a separate, adjacent defect described below, found by cross-vendor review of this same PR.

  **A second, independent defect found by cross-vendor review while auditing this one: a soft-deleted instance in the `Returning` sub-state left its timer Armed forever, with no path to ever being retired.** `WorkflowTimerExecutor.Fire.cs`'s GATE-0 ran its `Returning`-sub-state defer check (`if (instanceSnap.State == InstanceState.Returning) { ...; return; }`) BEFORE the `isOrphan` check that carries this PR's own `|| !instanceSnap.IsValid` disjunct (added earlier in this PR by T-899-1) — an instance that was BOTH `Returning` AND soft-deleted took the defer-and-return branch every tick, forever, never reaching the disjunct that would have retired it. The defer comment's own justification does not hold for this cell: it promises the timer resolves when either the 回退 completes (advancing `Generation` — nothing acts on an invalid instance, so this never happens) or its lease expires and is reclaimed by Phase-2 (`WorkflowTimerExecutor.StrandReaper.cs`'s `ReclaimExpiredLeasesAsync` — but that query was ALSO already updated earlier in this PR to exclude `IsValid == false` rows specifically because "a soft-deleted instance stuck in Returning must not be reclaimed back to Running", so this never happens either). Neither promised resolution applies, verified by reading both code paths, not assumed. **Fixed by adding `&& instanceSnap.IsValid` to the existing defer condition — deliberately NOT by reordering the two blocks**: `isOrphan`'s own `State != InstanceState.Running` disjunct is true for EVERY `Returning` instance, valid or not, so simply moving it ahead of the defer check would retire a live, valid in-flight 回退's timer too, a regression the surrounding FIX-A2 rationale (shrink the fire-vs-return contention window, preserve SLA for surviving nodes) exists to prevent. A `Returning`+valid instance still defers exactly as before — `T-TMO-22c` (`TimerReaperTests.cs`), unmodified, proves this untouched. Grepped for the same `InstanceState.Returning` deferral pattern across all `WorkflowTimerExecutor.*.cs` files: `Fire.cs`'s GATE-0 was the only defer site; `StrandReaper.cs`'s two `Returning` references are the (already-correct) Phase-2 reclaim query, not a deferral. New test `T-899-3` (`TimerReaperTests.cs`) proves the fixed cell: a `Returning`+soft-deleted instance's due timer is retired (flips to `Fired`, zero event, zero notification) instead of staying Armed forever — confirmed red without the `&& instanceSnap.IsValid` conjunct (`Assert.AreEqual failed. Expected:<Fired>. Actual:<Armed>`), green with it restored. WorkFlow suite: 611 → 612. Mutant `test/mutants/entries/wf899-fire-returning-isvalid-ordering-reintroduce.json` reverses the ordering (removes the conjunct, reproducing the exact pre-fix bug) — `VERDICT: KILLED` / `GATE: PASS`.

  **Controller alignment, required for the write-provenance guards to compare same-sourced values.** `WorkflowInstanceController.Start`, `WorkflowTaskController.Inbox`, and `WorkflowDesignerController.CreateDefinition` read `Wtm.LoginUserInfo?.TenantCode` to build the `tenantCode` they pass to the engine/store — a DIFFERENT property than `CurrentTenant` (`LoginUserInfo.CurrentTenant` gets `_currentTenant ?? TenantCode` — identical for ordinary users, differs only under host-admin tenant switching). All three now read `CurrentTenant`, the same property `ResolveAmbientTenant` reads for the DataContext stamp — otherwise `WorkflowEngine.StartAsync`'s and `WorkflowDefinitionStore.CreateDefinitionAsync`'s write-provenance guards (above) would compare two INDEPENDENTLY-derived values that happen to usually agree rather than a genuine same-source invariant, and a host-admin acting under a switched tenant would trip the guard on every write.

  **One-time self-check log for un-migrated consumers.** `WarnIfTenantFilterMissing` (new, `ServiceCollectionExtensions.cs`) checks, on the first factory-created module DataContext resolved in the process, whether `ProcessDefinition` has an active query filter; if not (the consumer is still calling the obsolete zero-arg overload), it logs once — `LogError` when `GlobalData.AllTenant` is non-empty (a real multi-tenant deployment running genuinely unprotected), `LogWarning` otherwise — naming #899 and the one-line migration fix. This does not change the Compatibility-mandated unfiltered behaviour for un-migrated consumers; it only makes that state loud instead of silent.

  **Session-half tests, each naming the production line whose deletion turns it red** (`SessionTenantStampingTests899.cs`, new — 6 tests, bringing the WorkFlow suite from 605 to 611). All 605 pre-existing tests use either a direct-DbContext test constructor or a hand-stamped context, so they structurally cannot observe this defect — precisely why production was broken while CI stayed green; the demo already calls `ApplyWorkFlowModels(this)` so e2e exercises the migrated model shape, but e2e is single-tenant, so `null == null` makes the session-half gap a no-op there too — CI had no path that could catch it. (a) a DI scope with `CurrentTenant == 'A'` yields a holder DataContext whose `TenantCode == 'A'` — red line: the `dc.SetTenantCode(ResolveAmbientTenant(sp))` call in `ResolveDataContext`; (b) two-tenant behaviour through a REAL DI-resolved store/engine (not a hand-stamped context, built the same way `ProdDiReproTests.cs` builds its container): tenant A creates a definition, lists it, and publishes it; tenant B (a fresh scope) cannot see it in its own list, and `StartAsync` against A's versionId reports not-found — same red line as (a); (c) a background scope with no `HttpContext`/`LoginUserInfo` yields a DataContext with `TenantCode == null` — red line: any non-null fallback added to `ResolveAmbientTenant` (this test exists specifically to stop a future regression that "helpfully" stamps the background path); (d) the guard's same-source invariant through DI: `CreateDefinitionAsync` with `'A'` succeeds, then after manually re-stamping the SAME underlying context to `'B'` (via reflection on the private `_dc` field, simulating drift), the same `'A'` throws — red line: the guard's `throw` in `CreateDefinitionAsync`; (e) the self-check log fires exactly once for an un-migrated model and never for a migrated one — red lines: the `LogWarning`/`LogError` call and the `hasFilter` early-return, respectively, inside `WarnIfTenantFilterMissing`. A new compile-preserving mutant, `test/mutants/entries/wf899-resolvedatacontext-tenantstamp-neutralize.json` (replaces the stamped value with a hardcoded `null`; positive control included), targets (a)/(b) — `VERDICT: KILLED` / `GATE: PASS`, mirroring the existing `wf899-applyworkflowmodels-processdefinition-tenantfilter-neutralize.json`.

  **Documentation corrected to teach the complete migration, not just the model half** (`docs/workflow.md`, `docs/wtm-developer-manual.md`, `docs/workflow-engine-spec.md` — the same three sites this PR's model-half already edits; `demo/WalkingTec.Mvvm.Demo/DataContext.cs` needed no change, it was already correct). All three now state plainly: tenant scope for the request path is automatic once `ApplyWorkFlowModels(this)` is called, no further consumer action needed; `ApplyWorkFlowModels(this)` alone produces no EF migration (a query filter is model metadata, not schema); the background timer path is tenant-aware by construction; and a consumer driving `IWorkflowEngine` from their OWN background code (a custom `IHostedService`, a Quartz job, a console tool) must call `SetTenantCode` on the context they build themselves, since WTM has no ambient identity to read in a hand-built scope.

  **Upgrading the package alone does NOT enable tenant isolation.** The query filter activates only for consumers who call `ApplyWorkFlowModels(this)`; un-migrated consumers keep working exactly as before (unfiltered) and now receive the one-time log above instead of silence. No "tenant isolation complete" claim is made here — see `docs/production-readiness.md` for the matching entry and #901's still-open structural closure for never-migrated/late-registering consumers.

  No version bump beyond what this `[Unreleased]` cycle already carries — behaviourally this is the same class of fix as #862 (opt-in code-change: consumers must update `DataContext.OnModelCreating` to pass `this` to get the fix; the old overload's behaviour is unchanged and still shipped).

### Changed

- **`EtlSchedulerService`'s ten `public virtual` scheduling methods, plus `EtlProgressTracker.Get`/`GetAll` and `EtlDashboardService.BuildSummary`, gained a required tenant-ownership parameter — a binary-compatibility break, accepted deliberately (#883, P0, BREAKING).** `TriggerNowAsync`/`PauseAsync`/`ResumeAsync`/`RescheduleAsync`/`AbortAsync`/`SkipNextAsync`/`EnableAsync`/`DisableAsync`/`DryRunAsync`/`RerunFromSnapshotAsync` (`src/WalkingTec.Mvvm.Etl/Scheduling/EtlSchedulerService.cs`) each gained trailing `string? callerTenantCode = null, bool declaredSystemQuery = false` parameters as part of the #883 cross-tenant IDOR fix above. `EtlProgressTracker.Get`/`GetAll` (`src/WalkingTec.Mvvm.Etl/Scheduling/EtlProgressTracker.cs`) and `EtlDashboardService.BuildSummary` (`src/WalkingTec.Mvvm.Etl/Dashboard/EtlDashboardService.cs`) went further: their `callerTenantCode` parameter has **no default** — it is mandatory — because the #882 review found the one-line-above pattern (an omittable tenant parameter defaulting to "see everything scoped to no tenant") is exactly how the dashboard tracker leak in the #883 entry above happened; a mandatory parameter makes the unsafe call impossible to write by omission rather than merely discouraged by convention.

  **Why this is a binary break, not just a source-compatibility one**: C# optional parameters are a call-site/compiler feature, not a CLR one — the method's actual metadata signature changed from e.g. `TriggerNowAsync(Guid)` to `TriggerNowAsync(Guid, string, bool)`. A precompiled caller (a NuGet consumer's DLL built against an older WTM package, or a derived class overriding the old signature) gets a `MissingMethodException` at the old call site, or a compile error on the override, until it is rebuilt against this version.

  **Why no forwarding overload was added to preserve the old signature.** The old, narrower signatures are exactly the shape #883 closes: an `EtlSchedulerService` method that cannot express "which tenant is this call allowed to touch" cannot enforce the ownership check that is the entire point of this fix. A compatibility shim would have to pick a default for the omitted `callerTenantCode` — `null` (today's actual old behaviour) reopens the #883 cross-tenant IDOR for exactly the callers who have not yet recompiled against the new signature, which defeats a P0 security fix by construction; there is no default that is both source-compatible and safe. Per this repo's Red Line (`CLAUDE.md`: "never silently change default behaviour... a breaking change needs a CHANGELOG.md entry, a migration path, and a minor version bump") and the #859 precedent (an accepted breaking default-flip for an unrelated P0 security fix, same release cycle): **the break is accepted, announced here, and versioned**, rather than shipped silently or masked behind an unsafe compatibility shim.

  **Version bumped to 10.21.0 (minor)** — see Migration below for what an existing caller of any of these **thirteen** members needs to do (ten `EtlSchedulerService` methods + `EtlProgressTracker.Get`/`GetAll` + `EtlDashboardService.BuildSummary`; counted by `grep`, not by memory, after an earlier draft of this entry miscounted it as twelve).

### Migration

- **Callers of the thirteen changed members (10.21.0, #883) — recompile is not enough; the call site must change.** An earlier draft of this entry flattened three different signature shapes into one four-case list and got both wrong: it claimed a caller "already resolving its own tenant" needed no source change (false — the OLD signatures had NO tenant parameter at all, so no existing caller could have been passing one), and it told every group to reach for `declaredSystemQuery: true` for a system caller, but `EtlDashboardService.BuildSummary` **has no such parameter at all**. Corrected here (#882 review, third round) into two separate, orthogonal things: which of three signature GROUPS a member belongs to (below), and which of four ways a caller CONSUMES that signature (further below) — a derived-class override, for instance, is a consumption mode, not an identity, and doesn't even apply to two of the three groups.

  **Three signature groups — do not apply one rule to all thirteen:**

  - **Group A — `EtlSchedulerService`'s ten scheduling methods** (`TriggerNowAsync`/`PauseAsync`/`ResumeAsync`/`RescheduleAsync`/`AbortAsync`/`SkipNextAsync`/`EnableAsync`/`DisableAsync`/`DryRunAsync`/`RerunFromSnapshotAsync`, all `public virtual`): `callerTenantCode` and `declaredSystemQuery` are BOTH optional, defaulting to `null`/`false`.
  - **Group B — `EtlProgressTracker.Get`/`GetAll`** (not `virtual`): `callerTenantCode` is REQUIRED (no default); `declaredSystemQuery` remains optional (`false` default).
  - **Group C — `EtlDashboardService.BuildSummary`** (not `virtual`): `callerTenantCode` is REQUIRED (no default); there is **no `declaredSystemQuery` parameter at all** — `BuildSummary` has no cross-tenant escape hatch to begin with, so there is no "system caller" case for it.

  **Authorization semantics — which value to pass, per group, by who the caller is:**

  - *Tenant-scoped caller* (the normal case — what `_EtlJobController`/`_EtlRunLogController`/`EtlJobDefinitionVM`/`_EtlDashboardController` already do): Groups A and B pass `callerTenantCode: Wtm.LoginUserInfo?.CurrentTenant` and leave `declaredSystemQuery` at its default `false`; Group C passes the same `callerTenantCode` (nothing else to set).
  - *Genuine cross-tenant background/system caller* (the ONLY case that may see or operate on another tenant's rows): Group A passes `declaredSystemQuery: true` (`callerTenantCode` is ignored once this is `true`, so it may stay at its default). Group B, being required, must pass BOTH explicitly — `callerTenantCode: null, declaredSystemQuery: true`. Group C has **no** such case: recheck the design before assuming one is needed — there is no production call site in this tree needing it today (verified by `git grep`), and none of the escape-hatch machinery exists for `BuildSummary` to reach for.
  - *Host-scope caller* (intentionally operates only on rows with no tenant, `TenantCode == null` — e.g. a genuinely host-level administrative job): Group A may omit `callerTenantCode` or pass `null`. Groups B and C, being required, must pass `callerTenantCode: null` explicitly — omission is no longer syntactically possible for them.

  **Separately — how a caller CONSUMES a signature, independent of which of the three identities above it is (any member can be reached through any applicable one of these):**

  1. **Direct call** — the ordinary case; update the call site's arguments per the table above.
  2. **Derived-class `override`** — applies ONLY to Group A (the ten `public virtual` `EtlSchedulerService` methods); Groups B and C are not `virtual` and cannot be overridden at all. An un-updated Group-A override's parameter list no longer matches the base method, so it fails to compile.
  3. **Delegate or method-group conversion** — e.g. `Func<Guid, Task> f = scheduler.PauseAsync;` — **verified (not assumed) to no longer compile**: `CS0123: No overload for 'PauseAsync' matches delegate 'Func<Guid, Task>'`. Optional parameters do not participate in method-group-to-delegate conversion; the delegate type must be updated to match the new parameter list (`Func<Guid, string?, bool, Task>`), or converted via an explicit lambda that supplies the new arguments.
  4. **Reflection lookup by parameter-type set, or a precompiled binary calling the old signature** — breaks the same way, **verified**: `typeof(EtlSchedulerService).GetMethod("PauseAsync", new[] { typeof(Guid) })` returns `null` against the new signature (`GetMethod("PauseAsync", new[] { typeof(Guid), typeof(string), typeof(bool) })` finds it); a precompiled caller's IL names the old three-arg-fewer signature and gets `MissingMethodException` at the old call site (see the binary-compatibility analysis above).
  5. **`dynamic` dispatch** — re-binds at runtime, not compile time, so it is neither a normal direct call (mode 1) nor mode 4's static precompiled call — and it breaks differently **per group**, verified by actually running it against all three:
     - **Group A is the one that matters.** `dynamic scheduler = ...; await scheduler.PauseAsync(jobId);` does **not** throw. The C# runtime binder resolves the one-argument call against `PauseAsync`'s optional parameters and silently supplies `callerTenantCode: null, declaredSystemQuery: false` — the exact same silent host-scope narrowing described above for a stale direct call, just reached through `dynamic` instead of a recompiled call site that was never updated. This is the same silent-semantic-change class this migration section exists to warn about, in a different disguise — check any `dynamic`-typed caller of a Group A member by hand; the compiler cannot flag it and nothing throws at runtime.
     - **Groups B and C fail loudly instead**, since their `callerTenantCode` has no default for the runtime binder to fall back to: a `dynamic` call passing only the old argument count throws `Microsoft.CSharp.RuntimeBinder.RuntimeBinderException` (`"No overload for method 'Get' takes 1 arguments"` / the equivalent for `BuildSummary`, exact wording depends on the call shape) — safe by construction, not silent.
     - `git grep` finds no `dynamic`-typed consumer of any of these thirteen members in this tree, so this is not a production regression in this repo — but this is a published package, and an external caller reflecting or scripting against it dynamically would hit exactly this.
- **A pre-existing Dashboard `rest` widget using `AllowPrivateNetwork`/`AllowHttp` (10.21.0, #948) needs one of two things after upgrading, or it starts returning `502` on every refresh.** Nothing about the widget's stored data needs to change — the two flags are still valid JSON, still deserialize, and are simply no longer sufficient by themselves. Pick one:
  - **Config-only (no C#), for a small fixed set of destinations**: register the built-in allowlist policy shipped with this fix —
    ```csharp
    services.AddWtmDashboard();
    services.Configure<DashboardEgressAllowlistOptions>(opt =>
    {
        opt.Entries.Add(new DashboardEgressAllowlistEntry
        {
            Host = "10.1.2.3",      // matched against the RESOLVED IP, or an exact hostname
            Ports = new[] { 8080 }   // null/empty = any port
        });
    });
    services.AddWtmDashboardEgressPolicy<ConfiguredAllowlistDashboardEgressPolicy>();
    ```
    No CIDR/wildcard support by design — see `ConfiguredAllowlistDashboardEgressPolicy`'s own XML doc. An empty `Entries` list behaves exactly like no policy at all (still denies everything) — this does not change the safe-by-default posture on its own.
  - **Custom policy, for anything more expressive** (per-tenant rules, a database-backed allowlist, wildcard/CIDR matching): implement `IDashboardEgressPolicy` and register it the same way —
    ```csharp
    public class MyInternalHostAllowlistPolicy : IDashboardEgressPolicy
    {
        public Task<bool> IsAllowedAsync(DashboardEgressDestination destination, CancellationToken ct = default)
            => Task.FromResult(destination.ResolvedAddress.ToString() == "10.1.2.3" && destination.Port == 8080);
    }
    // services.AddWtmDashboardEgressPolicy<MyInternalHostAllowlistPolicy>();
    ```
    Must be registrable Singleton (`AddWtmDashboardEgressPolicy<T>()` enforces this) — see `IDashboardEgressPolicy`'s XML doc if the policy needs a scoped resource such as a `DbContext`.

  Neither option changes the widget's stored `AllowPrivateNetwork`/`AllowHttp` values — they remain in the JSON as an informational record of the original intent but are not read by `RestWidgetDataSource` as a grant either way; only a registered `IDashboardEgressPolicy` approving the specific resolved destination restores the fetch.
- **`WtmControllerActivator` / `AddWtmContext()` call order (10.21.0, #876/#882).** `AddWtmContext(services, ...)` now requires an `IControllerActivator` to already be registered, i.e. it must run AFTER `services.AddMvc()`/`AddControllers()` (and, if used, `.AddControllersAsServices()`). Both of this repo's real `Startup.cs` files already call them in that order; an app that does not will now get a clear `InvalidOperationException` at startup instead of a silently-discarded #876 fix.
- **`GET /_dashboard/{id}` no longer returns real REST widget header values (10.21.0, #957, BREAKING for any caller reading them from this response).** `Source.RestOptions.Headers` values in the response are now a fixed sentinel string (keys unchanged) — see the matching `### Security` entry above. Any custom code that read a header's real value out of this endpoint's response (the shipped UI never does — `framework_dashboard.js` only renders dashboard/widget content, never a header value) needs another source for that value (e.g. read it directly from wherever the operator originally configured it) after upgrading. **Separately**, a caller that previously relied on *omitting* the `headers` key from a `PUT` body as a way to clear a widget's headers will see different behaviour now: omitting the key now PRESERVES existing headers (the fix for the designer's silent-wipe bug), where it previously cleared them (both bound to the same empty dictionary before this fix). Send an explicit `"headers": {}` to clear headers going forward.
- **`RestWidgetDataSourceOptions.Headers` changed from a non-nullable `Dictionary<string, string>` defaulting to an empty dictionary, to a nullable `Dictionary<string, string>?` with no default (10.21.0, #957, BREAKING for any downstream code constructing or mutating this type directly).** `new RestWidgetDataSourceOptions().Headers` is now `null`, not `new()`. Concretely: `options.Headers.Add("Authorization", "...")` compiled and worked before this release and throws `NullReferenceException` now; for a consumer with nullable reference types enabled, `options.Headers.Count` goes from compiling clean to `CS8602`. Fix at the call site — null-coalesce before writing (`(options.Headers ??= new()).Add(...)`), or null-check before reading. **Necessary, not optional**: see the matching `### Security` entry above — the old non-nullable shape made an absent `headers` key and an explicit `{}` indistinguishable after JSON binding (both bound to the same empty dictionary), which is exactly the distinction the merge-on-update fix depends on to tell "caller didn't touch headers" from "caller wants them cleared"; the designer's silent-wipe bug was not fixable without this change. In-tree callers were already null-safe before this note was written (`RestWidgetDataSource.cs` already guards with `if (options.Headers != null)`; `DashboardCredentialMasking` handles `null` throughout) — this bullet is for downstream consumers of the published package, not because this repo's own code needed a change here.
- **Dashboard `rest` widgets: `RestOptions.AllowPrivateNetwork`/`AllowHttp` stop being honoured at fetch time (10.21.0, #948, BREAKING for any deployment relying on them).** Before this fix these two `RestWidgetDataSourceOptions` fields were a documented, supported opt-in for reaching an internal-network or plain-HTTP destination from a REST widget. As of this release neither field grants anything by itself — see the `### Security` entry above for why (they are caller-reachable data on every widget-definition write path, not a trusted server-side setting) — and reaching such a destination now requires a host-registered `IDashboardEgressPolicy` to approve the specific resolved destination. A dashboard that had a working widget using either flag will get a `502 Widget data fetch failed` on every refresh after upgrading until an operator takes the migration step below. `RestWidgetDataSource`'s constructor also gained a third, optional `IDashboardEgressPolicy?` parameter — source-compatible, binary-breaking for any precompiled caller that `new`s this type directly with a two-argument call (uncommon: the type is normally resolved through DI, not constructed directly).

### Security — demo `FileApiController` hardening, all three copies (#830)

The three demo `FileApiController` copies (LayUI `demo/WalkingTec.Mvvm.Demo`, Vue3
`demo/WalkingTec.Mvvm.Vue3Demo`, Blazor `demo/WalkingTec.Mvvm.BlazorDemo`) had nine holes
across four categories, all fixed in this same PR:

- **Five `[Public]` (unauthenticated) endpoints removed** — `GetFileName`, `GetFile`,
  `GetFileInfo`, `GetUserPhoto`, `DownloadFile`. Combined with
  `FileUploadOptions.EnforceTenantFileScope` defaulting to `false` (`WtmFileProvider`
  uses `IgnoreQueryFilters()` in that mode), any unauthenticated caller who could
  guess/enumerate a `FileAttachment` GUID could read another tenant's file content with
  no login at all. The class-level `[AuthorizeJwtWithCookie]` + `[AllRights]` now
  applies uniformly: any authenticated user can still reach these actions (no per-page
  privilege required), only anonymous access is removed. **Vue3 companion fix**: Vue3
  is JWT-only (`AccountController.LoginJwt` never calls `SignInAsync`, so there is no
  auth cookie; the axios interceptor in `ClientApp/src/utils/request.ts` attaches the
  bearer token to axios calls only). Several places rendered `GetFile` output through a
  raw, unauthenticated URL bound directly to a browser-native `<img>`/`el-image` `src`
  (`stores/userInfo.ts`'s avatar URL, `components/uploadImage/index.vue`'s previews, and
  `components/table/index.vue`'s image-typed grid columns) — a browser's own image fetch
  carries neither the bearer header nor a cookie, so removing `[Public]` would have 401'd
  every one of those images. Reviewed and reproduced by inspection before shipping; all
  three call sites now fetch through the existing `fileApi().getFile()` helper (an
  authenticated axios request returning a blob object URL) instead of binding the raw
  endpoint URL. **Blob URL lifecycle**: `URL.createObjectURL` calls already existed in
  this ClientApp (one pre-existing call site) with no matching `revokeObjectURL`
  anywhere; routing three more call sites through the same helper multiplies that latent
  leak — `table/index.vue` in particular now creates one blob URL per image column per
  row on every search/page/filter change. All three call sites (and the pre-existing
  one in `uploadImage/index.vue`'s upload-success preview) now revoke the URL they are
  about to replace before creating its successor. **Correction (#857):** an earlier
  version of this entry claimed all three call sites also revoke on component unmount —
  that only holds for the two that are actual Vue components,
  `components/uploadImage/index.vue` and `components/table/index.vue` (both wire an
  `onUnmounted` hook). `stores/userInfo.ts` is a Pinia **store**, which has no component
  lifecycle to hook into, so it only ever revokes the previous blob URL at the point of
  replacement inside `setUserInfos()` — a held avatar blob URL is not revoked when the
  session/store itself goes away. `stores/userInfo.ts` additionally re-resolves the
  avatar from a cached `photoId` on a sessionStorage hit rather than trusting the cached
  blob URL, which does not survive a page reload. This store-level gap is a known residual limitation, tracked separately; #856's fixes cover the two real components only.
- **`DeletedFile` now calls `WtmFileProvider.DeleteFileTenantScoped`, not
  `DeleteFile`**, and is now `[HttpPost]`, not `[HttpGet]`. The non-tenant-scoped
  overload let any authenticated caller in tenant A delete tenant B's `FileAttachment`
  row by GUID; a GET performing a delete was a separate CSRF/prefetch hazard.
  `framework_layui.js`'s and `MultiUploadTagHelper.cs`'s delete calls try POST first and
  fall back to GET only on a 405 — see "Compatibility" below for why.
- **`csName` is now validated against `WTMContext.IsKnownConnectionKey` on all eight
  actions** before it reaches `Wtm.CreateDC(cskey:)` — the same guard
  `_FrameworkController` already applies at nine call sites; the demo template had none.
- **`GetFileInfo` now goes through `WtmFileProvider.GetFile(..., withData: false, ...)`**
  instead of querying `dc.Set<FileAttachment>()` directly, and returns a projected
  `{ Id, FileName, FileExt, Length, UploadTime, ExtraInfo }` instead of the whole entity
  (dropping `Path`/`HandlerInfo`/`TenantCode`, which the caller has no need for). This
  also means `GetFileInfo` now goes through the same seam #827's planned
  provider-level authorization check will land on, instead of bypassing it structurally.

**`WtmFileProvider.DeleteFile(string, IDataContext?)` is marked `[Obsolete]`**,
pointing callers at `DeleteFileTenantScoped`. It remains a public framework API and is
non-tenant-scoped by default (`FileUploadOptions.EnforceTenantFileScope = false`), so a
downstream application's own hand-written call to it is equally exposed to
cross-tenant deletion — this fix does nothing for that call site. `[Obsolete]` is a
compiler-warning signal only: it fires solely for downstream code that (a) is C#,
(b) calls `WtmFileProvider.DeleteFile` at compile time, and (c) recompiles against
this package version with warnings surfaced. It does nothing at runtime, for
already-compiled binaries, or for callers who never rebuild.

#### Compatibility

**Corrected (this section originally claimed, incorrectly, that this item does not
change the behaviour of any already-deployed application — that was false and is
retracted; the actual mechanics are below.)**

The demo `FileApiController` copies themselves are templates copied by
`dotnet new wtm`/scaffolding — fixing them in this repo changes nothing for an
application that already copied the old controller into its own source tree, and
`WtmFileProvider.DeleteFile` remaining callable (with an `[Obsolete]` warning) means no
downstream build breaks either. **But `framework_layui.js` and
`MultiUploadTagHelper.cs`'s inline-script fallback are not templates** — they are
shared, shipped assets (`framework_layui.js` is an `<EmbeddedResource>` in the
`WalkingTec.Mvvm.Mvc` package; `MultiUploadTagHelper.cs` ships in
`WalkingTec.Mvvm.TagHelpers.LayUI`) that reach **every** downstream application on a
plain NuGet package upgrade, regardless of whether that application's own scaffolded
`FileApiController` copy is touched. A downstream copies `FileApiController` once, at
scaffold time; a package upgrade does not — and cannot — update that copy for them.

Before this fix, changing those two shared assets to send only `POST` would have meant:
a downstream that upgrades the package and changes nothing gets the new
POST-only delete call shipped straight into their still-`[HttpGet]`-only scaffolded
controller, which returns `405 Method Not Allowed` — the multi-upload delete button
silently stops working, with no error surfaced anywhere a developer would look during
the upgrade itself. That is a real, reproduced compatibility break (confirmed via
review), not a hypothetical one.

**Fix applied**: `framework_layui.js`'s `ff.upload.doDelete` and
`MultiUploadTagHelper.cs`'s inline-script `{Id}DoDelete` now try `POST` first and, only
on a `405` response specifically (never on any other error, so this can never mask an
unrelated failure as a compatibility fallback), retry the identical call with `GET`.
This is a deliberate compromise against this repo's stated priority order —
**Compatibility → Security → Quality → Performance** — for a defect (a GET performing a
delete is itself a CSRF/prefetch hazard) whose severity is lower than the tenant-scoping
fix above it: an un-migrated downstream's delete button keeps working exactly as it did
before this release (same GET-based CSRF exposure it already had, not a new one), and a
downstream that has updated its own copied controller to `[HttpPost]`-only gets the full
hardening immediately, since POST succeeds on the first try and the GET fallback is
never reached.

**This fallback is temporary, not permanent framework behaviour, and is tracked as
such**: #853 has this repo's word that it gets removed (POST-only, no GET retry) at
WTM's next **major** version bump (`version.props`' `VersionPrefix` rolling from `10.x`
to `11.0.0+`) — a deliberately stricter, rarer trigger than this repo's usual
minor-version breaking-change vehicle, chosen precisely so this doesn't join the list of
things gated behind a condition that quietly never gets checked again (see the
"Corrected" section below on `#470`/`#567`'s island-render flag, default-off since
2026-06 with no commit ever flipping it — the same failure shape at a larger scale).

#### Migration notes

- **If your app scaffolded `FileApiController` from an earlier WTM template**, apply
  the same four fixes to your own copy — diff against
  `demo/WalkingTec.Mvvm.Demo/Areas/_Admin/ApiControllers/FileApiController.cs` (or the
  Vue3/Blazor equivalents) in this commit. This is the only step required to pick up the
  actual security fixes ([Public] removal, tenant-scoped delete, csName validation,
  projected GetFileInfo) — your delete button keeps working during and after this step
  either way, because of the compatibility fallback described above.
- **You do not need to change your own front end.** `framework_layui.js` and
  `MultiUploadTagHelper.cs` (the framework's shared upload widgets) already send POST
  first and fall back to GET automatically; there is no `/api/_file/DeletedFile/{id}`
  caller left in framework-owned code that only sends GET. If you wrote your **own**
  custom caller of that route (outside the framework's upload widgets), switch it to
  POST — your copied controller now only exposes `[HttpPost]` once you apply the fix
  above, and a hand-rolled GET caller will start receiving a 405 at that point.
- **If you call `WtmFileProvider.DeleteFile(string, IDataContext?)` directly**, switch
  to `DeleteFileTenantScoped` unless you have deliberately opted into
  `FileUploadOptions.EnforceTenantFileScope = true` and understand the non-tenant-scoped
  overload's blast radius.

### Corrected — retractions of claims made in shipped releases (#835)

A cross-vendor adversarial review of the 2026-07-21…07-28 work found four classes of
claim in merged commit messages, PR bodies and this changelog that do not hold. Nothing
below changes behaviour; the code is as it was. These are corrections to the record,
published because a false claim causes the next reviewer to skip verification — which is
how the underlying defects propagated in the first place.

- **"default-OFF byte-identical" is retracted** (commits `178b5b4b`, `8bb06f82`,
  `46380763`, `26881b2d`, and the 10.18.0 summary below). `DataTableTagHelper.Process()`
  now calls `ListVM.GetGridActions()` at `:444`, *before* the `UseSelectIslandRender`
  check; the pre-island code's first call was at `:699` inside the toolbar build
  (`git show 178b5b4b^` has exactly one call site, at `:650`). `GetGridActions()`
  memoises into `_gridActions`, so `InitGridAction()` does not run twice — but its
  *execution timing* changed for every grid. The observable difference requires
  `UseLocalData == true` (the only ListVM state mutated between those two points is
  `ListVM.NeedPage = false` at `:511`, inside `if (UseLocalData)`) **and** a downstream
  `InitGridAction()` override that reads `NeedPage`. Under those conditions the rendered
  output can differ with the flag OFF.
- **"preventDefault matches legacy `return false`" is retracted** (commit `46380763`).
  The `button:` and `submit:` handlers (`framework_layui.js:6393`, `:6423`) call only
  `preventDefault()`. jQuery's `return false` also calls `stopPropagation()` — the
  code's own comment at `:6374-6378` says so — and the delegated listener is on
  `document` in the bubble phase (`:6480`, no `{capture:true}`), so every ancestor
  handler has already run by the time it fires. Restoring parity needs capture-phase
  interception, not a `stopPropagation()` added at `document`.
- **"DataPrivilege fingerprint enforced at the engine boundary" is retracted**
  (commit `b444c4464`). The clause/field caps *are* enforced unconditionally by the
  engine. The fingerprint is not: **eight** public signatures still accept an optional
  caller-supplied `string? identityKey = null` — `AnalysisQueryEngine.cs:49`, `:193`,
  `:304`, `:374`, and the four `*Dynamic` overloads at `:438`, `:470`, `:502`, `:534`.
  The two in-tree callers do pass it; no future or downstream caller is protected
  automatically. (The `*Dynamic` overloads are the caller-driven dashboard-widget path.)
- **"Override the hook in a derived controller" is retracted** as guidance
  (`_FrameworkController.cs:160`, and the equivalent wording on the other hooks at
  `:480` and on the flags at `Configs.cs:650`). `_FrameworkController` is the concrete
  class MVC routes `/_Framework/*` to (declared at `:35`); subclassing it produces a
  second controller the front end never calls. The repo has no
  `ControllerFeatureProvider`/`IApplicationFeatureProvider` usage, and all three demo
  `Startup` files route `{controller=Home}/{action=Index}/{id?}`, so a derived class only
  ever adds `/MyFramework/*`. A DI-resolved authorization seam is tracked in #827.
- **"Cross-tenant file DELETION has no remaining exit"** (PR #821 body) is retracted
  in place on that PR. The supporting grep covered `src/` only; three demo
  `FileApiController` copies still call the non-tenant-scoped `DeleteFile` (#830).

Standing rule adopted as a result: **a commit message may not claim more than the same
change's entry in `docs/production-readiness.md`.** That file was consistently more
honest than the commit messages describing the same work.

### Security

- **`_FrameworkController.UpdateModelProperty` now refuses to write any `FileAttachment`
  foreign key, unconditionally, even same-tenant (#824 Part 1 / B1.1).** This inline-grid
  cell-edit endpoint is `[AllRights]` and, prior to this fix, had no gate on FK-typed
  fields at all: a request could set e.g. `FrameworkUser.PhotoId` to *any*
  `FileAttachment`'s GUID, including a different tenant's, because #815's FK gate
  (`BaseCRUDVM.RejectUnresolvableFileAttachmentReferences`) only runs from
  `DoAddPrepare`/`DoEditPrepare`, and #797 deliberately routes this endpoint around both.
  Writing the FK alone grants no new *read* capability: `GetFile` is `[AllRights]` and
  ignores the tenant query filter by default (`EnforceTenantFileScope=false`), so any
  caller who already knows a `FileAttachment` GUID could fetch it both before and after
  this fix — the forged FK does not unlock anything `GetFile` did not already allow.
  **It also does not reopen #815's deletion surface, and an earlier draft of this entry
  was wrong to claim it did.** There is no automatic orphan-file cleanup: every deletion
  path is gated on the client having POSTed `DeletedFileIds` (`BaseCRUDVM.cs:440`, `:477`,
  and their two async twins), so a legitimate edit that posts nothing deletes nothing —
  and even when that path does run, it resolves the id through `DeleteFileTenantScoped` →
  `DeleteFileCore(id, dc, enforceTenantScope: true)`, which queries
  `dc.Set<FileAttachment>()` **without** `IgnoreQueryFilters()` (`WtmFileProvider.cs:222`).
  `FileAttachment` implements `ITenant`, so the global `TenantCode` filter applies and a
  cross-tenant id simply fails to resolve — the delete is a no-op regardless of whether
  the FK was forged. What this gate actually buys is **data integrity**: it stops an
  unauthorized cross-tenant reference from being persisted at all — the write is
  unauthorized on its own terms, independent of whether anything downstream ever reads or
  deletes through it — and it closes this one sink's gap in the same-tenant-FK invariant
  #815 already enforces on the Add/Edit VM path
  (`BaseCRUDVM.RejectUnresolvableFileAttachmentReferences`); before this fix,
  `UpdateModelProperty` was the one write path where that invariant did not hold. This is
  hardening, not the closure of a live read- or delete-exploit chain.
  **Behaviour narrowing (opt-out not available):** any inline edit of a FileAttachment FK
  field through this endpoint — including one previously accepted because the id
  belonged to the caller's own tenant — now returns `400 Bad Request` instead of
  succeeding. No config flag gates this; the decision for this PR is that legitimate use
  of this path is effectively nil (inline grid cell edit can only POST a bare GUID
  string, never upload a file), and a tenant-conditional carve-out would require dragging
  tenant-resolution logic into an endpoint that should not touch files at all.
  **Migration:** if any integration relied on setting an attachment FK via this endpoint,
  switch to the normal Add/Edit VM flow (which resolves and validates the attachment
  properly) or the file upload API instead. The predicate driving this gate —
  `WalkingTec.Mvvm.Core.Extensions.DCExtension.IsFileAttachmentForeignKeyProperty`,
  EF relationship-metadata–driven so it also covers downstream-defined attachment FKs —
  is shared infrastructure for the rest of Issue #824's write-path sinks
  (`BasePagedListVM.UpdateEntityList`, `BaseBatchVM.DoBatchEdit`/`Async`,
  `BaseImportVM.BatchSaveData`, grandchild `IEnumerable<ISubFile>`, direct `DbSet`
  writers), which remain open as follow-up work on the same issue. **Issue #824's own
  recommended fix — a single guard at the `EmptyContext.SaveChanges`/`SaveChangesAsync`
  boundary, covering every write path by construction instead of one sink at a time (see
  the issue body and `docs/production-readiness.md`) — remains outstanding.** This entry
  gates one sink as defence in depth, not the architectural fix.

### Fixed

- **`demo/WalkingTec.Mvvm.Vue3Demo/ClientApp/wtmbuild.ts` hardcoded Windows backslash path
  separators, so `vite build` failed immediately on macOS/Linux (#869).** The custom
  `wtmBuildPlugin`'s `buildStart` hook built its scan/output paths by string-concatenating
  literal `"\\src\\views"` / `"\\public\\menu.json"` onto `__dirname` — not a valid path on
  any non-Windows OS, so the plugin threw
  `ENOENT: no such file or directory, scandir '...\src\views'` before a single module
  transformed. Replaced both with `path.join(__dirname, "src", "views")` /
  `path.join(__dirname, "public", "menu.json")` (Node's `path` module was already imported
  in this file for `readDir`'s own path handling). `path.join` resolves with the platform's
  native separator, so this is unchanged behaviour on Windows and a genuine fix on
  macOS/Linux — verified by running `vite build` on macOS before and after: before,
  `0 modules transformed` then the ENOENT above; after, the plugin's `buildStart` hook
  completes and 310 modules transform. This does **not** mean `vite build` fully succeeds
  on macOS at HEAD — a separate, pre-existing dependency-resolution problem unrelated to
  path separators (`src/i18n/index.ts`'s CJS-style deep import
  `element-plus/lib/locale/lang/en` is rejected by `vite@7`'s stricter ESM
  export-conditions resolver, since `element-plus@2.13.5`'s `package.json` only declares a
  `require` condition for that path, not `import`) still blocks the Rollup bundling stage
  on any platform at this dependency combination; tracked separately in #891, not
  introduced or fixed by this change.

- **`LookupCacheService.RefreshAsync<T>` silently proceeded to write the cache after failing to
  acquire its per-key stampede lock — the one semaphore-timeout call site in this file that never
  got the #112(4)/M10 fix; `LookupCacheWarmupService` logged "warm-up completed" even on a run
  that cached nothing (#804, MEDIUM).** Verified directly against the code, not the issue text:
  `RefreshAsync` computed `bool acquired = await semaphore.WaitAsync(StampedeTimeout, ct)` but
  never checked it before calling `Invalidate<T>`/`LoadFromDbAsync`/`SetCache` — unlike
  `GetAll`/`GetAllAsync` (`LookupCacheService.cs:207-234` sync, `:293-313` async), which already
  re-check the cache and skip `SetCache` on a timed-out acquire. A caller that timed out does not
  hold the lock, so its write races the legitimate holder's own `SetCache` and can overwrite a
  fresher value with a stale one for the full TTL — exactly the failure mode the semaphore exists
  to prevent, and the one scenario the #112(4)/#141 hardening (`76b002fa0`) never reached.
  Separately, `LookupCacheWarmupService.WarmTypesAsync` warms every `[CacheLookup(WarmOnStartup =
  true)]` type with `tenantId = null`. For any such type that also implements `ITenant`, with
  `DefaultTenantIsolation` in effect (the default), that call lands on
  `LookupCacheService`'s own existing #112(1)/#168 bypass
  (`_forcedTenantIsolationTypes.Contains(typeof(T)) && tenantId == null &&
  DefaultTenantIsolation`), which returns the DB result directly WITHOUT ever calling `SetCache`.
  The call completing without throwing was indistinguishable, from the warm-up service's own
  point of view, from an actual cache write — so it logged `"Lookup cache warmed: {TypeName}"`
  per type and `"Lookup cache warm-up completed."` overall regardless of whether the cache stayed
  empty. A success log that fires whether or not anything succeeded is a false assurance, not a
  smaller defect than the stampede bug in the same file.

  **Fix.** `RefreshAsync` now checks `acquired` the same way the other two call sites do, but
  responds differently: `GetAll`/`GetAllAsync` are read paths with a safe fallback (return the DB
  result, just don't cache it); `RefreshAsync` is an explicit, caller-invoked "make this happen
  now" operation with no return value the caller can inspect, so silently completing as if the
  refresh had happened would itself be a false assurance. It now logs a warning and throws
  `System.TimeoutException` instead. **Behaviour change a caller can observe:** a `RefreshAsync`
  (or `WTMContext.RefreshLookupAsync`) call that cannot acquire the per-key lock within
  `StampedeTimeout` now throws instead of returning normally having silently not refreshed
  anything (or, before the #112(4) fix, racing a stale write). This is a narrow window — the
  default timeout is 10 seconds and a same-key refresh collision only occurs under concurrent
  `GetAll`/`GetAllAsync`/`RefreshAsync` calls for the same type+tenant — but it is a real,
  disclosed change, not a pure bug fix with identical externally-visible behaviour otherwise.
  `LookupCacheOptions` gained `StampedeTimeout` (default unchanged, `TimeSpan.FromSeconds(10)`),
  so the timeout this file's own log messages already told operators to "consider increasing" is
  now actually configurable — `LookupCacheService`'s `StampedeTimeout` reads
  `_options.StampedeTimeout` instead of a hardcoded `private static readonly` field.
  `LookupCacheWarmupService.WarmTypesAsync` now skips (rather than attempts and mis-reports) any
  `ITenant` type under `DefaultTenantIsolation`, logging that it is not warmable for the null
  tenant instead of a false "warmed"; `ExecuteAsync` now tracks how many types were *actually*
  cached (`SetCache` ran) versus merely attempted, and only logs "warm-up completed" when that
  count is greater than zero — a warm-up run that caches nothing now logs a Warning-level "0 of N
  type(s) were actually cached" instead.

  **Not fixed here, disclosed rather than left as a silent gap:** (1) `RefreshAsync` also does not
  check `_registry.ContainsKey(typeof(T))` before calling `SetCache`, unlike `GetAll`/`GetAllAsync`
  (the #112(2) guard, "non-[CacheLookup] types must never be stored in the cache — without a TTL
  they would be immortal"). `SetCache`'s TTL comes from `_registry.TryGetValue(typeof(T), out var
  attr)`; if that lookup misses, no `AbsoluteExpirationRelativeToNow` is set, so calling
  `RefreshAsync<T>()` for a type that is not `[CacheLookup]`-attributed would insert an immortal
  cache entry the normal `SaveChanges`-triggered invalidation path skips (it gates on
  `IsCacheable`). Real, but out of this PR's authorized scope (the issue names two specific
  claims); needs its own issue. (2) `DistributedLookupCacheService.RefreshAsync` (a separate
  `ILookupCacheService` implementation, `src/WalkingTec.Mvvm.Core/Cache/DistributedLookupCacheService.cs:345-360`)
  has the textually identical defect — `bool acquired = await semaphore.WaitAsync(...)` computed
  and never checked before `Invalidate`/`SetDistributed` — and was not touched by this PR, which
  is scoped to `LookupCacheService.cs`. Tracked as a follow-up, not fixed here.

  **Tests** (`test/WalkingTec.Mvvm.Core.Test/Cache/`): `LookupCacheStampedeRefreshTimeoutTests804.cs`
  — a real `DbCommandInterceptor` counts actual SQL `SELECT` executions (not a weaker "did
  anything crash" proxy) against a `SqliteTestDbMode.FileWal` database with multiple genuinely
  racing `DbContext` instances (shared-cache in-memory is documented elsewhere in this codebase as
  unsafe for that shape of test): one test pins that N=6 concurrent cache-miss `GetAllAsync`
  callers with a slow loader produce EXACTLY ONE real load (not "fewer than N", the weaker bound
  this repo has shipped and regretted before); the other holds the per-key lock with a slow
  loader, calls `RefreshAsync` with a short `StampedeTimeout` from a second, independently-racing
  context, and asserts it throws `TimeoutException`, that the loader count stays at exactly the
  holder's one call (the timed-out `RefreshAsync` must never reach `LoadFromDbAsync`), and that
  the cache ends up holding the holder's result, not a stale write from the timed-out caller.
  `LookupCacheWarmupTenantHonestyTests804.cs` — one test proves a genuinely warmable
  (non-`ITenant`) type is both cached after warm-up and served on a follow-up lookup without a
  further DB hit (same reference back), and that the honest "completed" log fires; the other
  registers only an `ITenant` type, asserts nothing is cached, that the "completed" log text never
  appears in the captured log stream, and that the honest "0 of N" warning does. Both fix commits
  were manually verified red-then-green: temporarily reverting just the new `if (!acquired)`
  guard in `RefreshAsync` and just the new `ITenant`/`DefaultTenantIsolation` skip branch in
  `WarmTypesAsync` (leaving the shared `StampedeTimeout`-configurability and warmed-count
  infrastructure in place) reproduces exactly the two new tests failing and no others, then
  restoring the guards turns both green again — this is a manual, one-time proof for this PR, not
  a `test/mutants/entries/` CI-enforced mutant (none was added for this change).

  Full suite: `test/WalkingTec.Mvvm.Core.Test` — `4824 passed, 0 failed`.

### Security — `System.Security.Cryptography.Xml` override pruned out of the shipped nuspec (#934)

- **The .NET 10 SDK's package-reference pruning silently defeated the NPOI vulnerability override at `dotnet pack` time, not `dotnet restore` time — a consumer installing `WalkingTec.Mvvm.Core` from the registry got NPOI's own vulnerable transitive `System.Security.Cryptography.Xml` pull (8.0.2, GHSA-37gx-xxp4-5rgx / GHSA-w3x6-4m5h-cxqf, HIGH) with nothing overriding it.** `Directory.Packages.props` pins `System.Security.Cryptography.Xml` to 10.0.10 specifically to override that transitive pull (#13, #788), wired via a direct `<PackageReference>` in `WalkingTec.Mvvm.Core.csproj`. That reference's pinned version matches the .NET 10 SDK's "framework already provides this" baseline for `net10.0` — the same match that raises the NU1510 warning already documented as EXPECTED on that line. Starting with the .NET 10 SDK, a direct `PackageReference` the SDK judges prunable this way is additionally marked `PrivateAssets=all`/`IncludeAssets=none` and dropped entirely from the packed `.nuspec`'s `<dependencies>` — reproduced against this exact repo before any fix: `dotnet pack src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj -c Release` produced a nuspec with 13 dependencies, `NPOI 2.7.6` present, `System.Security.Cryptography.Xml` absent. This repo's own release-gate vulnerability scan (`dotnet list WalkingTec.Mvvm.sln package --vulnerable`) never caught it, because it scans the solution — where the override is still a live `PackageReference` — not the packed artifact a consumer actually installs. A downstream consumer independently hit this months ago and worked around it by re-pinning the same override in their own project; this is an observed distribution defect, not a theoretical one.

  **Fix**: `WalkingTec.Mvvm.Core.csproj` now sets `<RestoreEnablePackagePruning>false</RestoreEnablePackagePruning>`, scoped to that project only. Verified: the packed nuspec now carries `System.Security.Cryptography.Xml 10.0.10` again (14 dependencies, no other change to the list). Two narrower fixes were tried first and rejected because they measurably do not work: explicit `IncludeAssets="all" PrivateAssets="none"` metadata on the `PackageReference` itself (pruning overrides user-set asset metadata unconditionally — still pruned) and an MSBuild target removing the package from the SDK-populated `PrunePackageReference` item list before `CollectPrunePackageReferences` runs (the removal is visible mid-build in a diagnostic log, but restore's internal pruning computation applies the SDK's baseline regardless — still pruned).

  **Inventory**: every other `Directory.Packages.props` security override across all six packed packages was re-checked the same way — pack, then inspect the resulting nuspec — and none of the others are affected: `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 (Core, #393), `Microsoft.OpenApi` 2.7.5 (Mvc, #528), and `Common.Logging`/`Common.Logging.Core` 3.4.1 (Etl) all survive pruning unchanged, because none of them match the SDK's "framework-provided" baseline the way `System.Security.Cryptography.Xml` does. No change was needed, or made, to any of those three, nor to `WalkingTec.Mvvm.TagHelpers.LayUI`, `WalkingTec.Mvvm.WorkFlow`, or `WalkingTec.Mvvm.FileHandlers.S3` (none carries a direct security-override `PackageReference` of its own).

  **Consumer-visible package-graph change**: `WalkingTec.Mvvm.Core`'s nuspec gains back one dependency, `System.Security.Cryptography.Xml >= 10.0.10` — a package every affected consumer already pulled in practice (transitively, via NPOI, at the vulnerable 8.0.2, absent the override); this restores the override that was always intended to ship rather than adding a new one. No API surface changed and no CHANGELOG-worthy breaking change for a consumer who was already unaffected (a competing pin of their own, or a resolution path that never triggered NPOI's transitive pull).

- **`publish-nuget.yml`'s release gate now also scans the consumer-installed dependency graph, not only the solution.** New "Consumer-graph vulnerability scan (#934)" step runs `dotnet list package --vulnerable --include-transitive` inside the same scaffolded project the pre-existing smoke-install step already builds (all six packages, restored from the candidate nupkgs under test, before either registry has seen them) and reuses `scripts/check-vulnerable-packages.py` (fails closed on unrecognised output) — the same script the solution-level "Local vulnerability scan" step already uses. Verified both directions locally before this fix landed: run against the six UNFIXED nupkgs (pre-`RestoreEnablePackagePruning`), it reports 8 HIGH findings for `System.Security.Cryptography.Xml` 8.0.2; run against the fixed nupkgs, 0 findings. This closes the structural blind spot #934 exploited — a future override that pruning silently defeats will fail this gate even while the solution-level scan stays clean.
### Security — Dashboard REST widget SSRF via caller-supplied `AllowPrivateNetwork`/`AllowHttp` (#948)

- **All three write paths that accept a Dashboard widget definition from the caller — `_DashboardController.Create`, `.Update`, and `_DashboardDesignerController.Preview` (which calls the same `CreateAsync` on a transient dashboard) — let that definition's `Source.RestOptions.AllowPrivateNetwork`/`AllowHttp` reach `RestWidgetDataSource` as if it were a trusted server-side setting.** `ValidateWidgetConfigs` (in both `JsonFileDashboardService` and `EfCoreDashboardService`) checked `Type`/`Kind`/`ListVmType`/`Url` but never these two security-sensitive fields. **A false cross-component assertion — the class of defect this repo has now shipped four times** — made this look safe: `WidgetDefinition.RestOptions`'s doc comment claimed the two fields "can only be enabled here" and are stripped whenever they originate from the request; that was true only for the separate `GetWidgetData` request-parameter channel, not for `RestOptions` itself, which *is* the caller's data on all three write paths. Both the property's doc comment and the matching claim inside `JsonFileDashboardService.GetWidgetDataAsync` are corrected.
  **Fix, two independent layers.** (1) **Write-time rejection, not silent stripping**: both `ValidateWidgetConfigs` copies now reject (`ArgumentException` → `400`) a `rest` widget whose `RestOptions` requests `AllowPrivateNetwork`/`AllowHttp`. Rejection was chosen over silently clearing the fields because clearing would erase the only signal that an escalation was attempted — a silently-corrected request is indistinguishable from a normal one in the caller's own response, and the controllers' existing `catch (ArgumentException) => LogWarning(...)` already gives rejection a log line for free. (2) **New host-owned, async `IDashboardEgressPolicy` seam**: regardless of (1), `RestWidgetDataSource.ValidateUrlAsync`/`PinnedConnectAsync` (the TOCTOU-safe, connect-time check) no longer treat `AllowPrivateNetwork`/`AllowHttp` as sufficient by themselves — any resolved destination that is private-network or plain-HTTP now requires a registered `IDashboardEgressPolicy.IsAllowedAsync` to approve that *specific resolved destination*; unregistered (the default) means always denied. The two `RestWidgetDataSourceOptions` fields are kept for JSON back-compat but are no longer, by themselves, a grant. **Design rationale**: a global boolean on `DashboardOptions` (the naive "move it up a layer" fix) was rejected — it would let any authenticated editor reach any internal host once flipped on. The real decision needs the resolved destination, not a flag plus caller data. The seam is deliberately async (`.claude/rules/dotnet-conventions.md` forbids `.GetAwaiter().GetResult()` on a request path; a real policy will consult config/a store) and a single method — deliberately not a second sprawling per-hook interface alongside `IWtmFrameworkEndpointAuthorizer` (#827, itself still unreleased), which answers a different question (caller-vs-resource, not network destination).
  **`DashboardOptions.EnableEditing` was declared, defaulted `true`, and read nowhere in `src/` — editing authorization did not exist.** Wired as a coarse, all-or-nothing global kill switch: `Create`/`Update`/`Delete`/`Preview` now return `403` for every caller when `false`, on top of (not replacing) the existing per-resource `CanEdit`/`AdminRoles` checks. Default unchanged (`true`) — no behaviour change for existing deployments.
  **Out of scope, not made harder**: #952 (the REST response cache key omits tenant/user/headers) is a different defect in the same files; this change adds the egress check before the cache lookup and does not touch `BuildCacheKey` or the cache read/write path at all.
  **Initial tests**: covering write-time rejection on both storage backends, real-controller-plus-real-service HTTP-level proof that Create/Update/Preview each reject the malicious payload (Update leaves the prior safe widget intact), a positive control that a legitimate public HTTPS destination still succeeds through all three routes, and `IDashboardEgressPolicy`-seam-level proof (denies by default, allows once an approving policy is registered, call-count-asserted so a present-but-unconsulted policy would not pass).
  **Mutant**: `948-dashboard-restoptions-egress-write-guard-neutralize` (`test/mutants/entries/`) neutralizes the write-time guard with a compile-preserving `false &&` prefix — `VERDICT: KILLED` / `GATE: PASS`.

  **Heterogeneous review round (same day, PR #955) — F1–F5 blocking, F6–F7 fixed in the same pass, F8 deferred.** Reviewer is Fable 5 (Anthropic) — **same vendor as the implementer (Sonnet), a different model family — this is heterogeneous review, not cross-vendor.** The cross-vendor gate (`/codex:adversarial-review`, required by `wtm-release-ops`) was **skipped**: Codex quota is exhausted until 2026-08-06. All seven findings below were independently re-verified against the code (not accepted on the reviewer's assertion alone) before being fixed.

  - **F1 (HIGH, startup-breaking): `AddWtmDashboardEgressPolicy<T>()` registered the policy `Scoped`, but its only consumer chain is `Singleton`.** `IDashboardService` (Singleton) takes `IEnumerable<IWidgetDataSource>` in its constructor; that enumerable — and therefore `RestWidgetDataSource`, despite its own `Transient` registration — is resolved exactly once, from the root container, at the moment the singleton is first built. A `Scoped` `IDashboardEgressPolicy` is a captive-dependency error there: with ASP.NET Core's default DI validation (`ValidateScopes`/`ValidateOnBuild`, on by default under `Host.CreateDefaultBuilder` in Development — reproduced with an executable repro against this exact lifetime shape) the app fails to start outright; with validation off (typically Production) it silently becomes exactly the captive dependency the validator would have caught — one instance held for the process lifetime, shared with the background `DashboardAlertHostedService`/`DashboardSnapshotJob` singletons. None of the 23 tests from the initial round caught this: all of them constructed `RestWidgetDataSource` directly (`new RestWidgetDataSource(factory, cache, policy)`), bypassing the only supported production wiring entirely. **Fixed**: `AddWtmDashboardEgressPolicy<T>()` now registers `Singleton`; `IDashboardEgressPolicy`'s own XML doc states implementations must be thread-safe and must not depend on scoped services directly (use `IDbContextFactory<T>`/`IServiceScopeFactory` internally if a policy needs one). **Tests**: `AddWtmDashboardEgressPolicy_BuildsCleanly_WithScopeAndBuildValidationEnabled` and `_RegistersSingleton_SameInstanceAcrossScopes` (`DashboardServiceCollectionExtensionsExtraTests.cs`) build the container with `ValidateScopes`/`ValidateOnBuild = true` — the exact validation a Development host performs — and resolve `IDashboardService` from a real scope; reverting to `AddScoped` reproduces the reviewer's exact `AggregateException` (`"Cannot consume scoped service 'IDashboardEgressPolicy' from singleton 'IDashboardService'"`), manually confirmed red-then-green.
  - **F2 (HIGH, compatibility): a dashboard with a persisted `rest` widget using `AllowPrivateNetwork`/`AllowHttp` before this fix silently starts failing (`502`) on every refresh after upgrading, with no migration path.** `RestWidgetDataSourceOptions.AllowPrivateNetwork`'s pre-fix doc explicitly documented `true` as "for intentional internal-network widgets" — a supported, documented opt-in, not an oversight. `production-readiness.md` originally said the upgrade "不需要資料遷移" ("no data migration needed") — literally true (no schema/data change) but conflated with "no migration path needed", which does not follow: an operator must take action to restore a previously-working widget. See `### Changed`/`### Migration` below for the accepted breaking change and the two ways to restore function (a config-only built-in allowlist policy, or a custom `IDashboardEgressPolicy`).
  - **F3 (MEDIUM): the connect-time, TOCTOU-safe layer (`PinnedConnectAsync`/`SelectConnectableIpAsync`) had zero direct tests and zero mutant coverage, while `production-readiness.md` claimed it was tested by call-count assertion.** Every `GetDataAsync_*` test used a fake `IHttpClientFactory`/`HttpMessageHandler` that never reaches `SocketsHttpHandler`'s `ConnectCallback` at all — reproduced by deleting `SelectConnectableIpAsync`'s policy-consultation branch entirely and confirming the full suite stayed green. **Fixed**: six new direct unit tests exercise `SelectConnectableIpAsync` itself (all-private/no-policy → null zero calls; all-private/approving policy → first approved IP with destination-field assertions; all-private/denying policy → null, every candidate tried; mixed private+public/no-policy → the public IP via the fast path with zero policy calls — the exact multi-candidate scenario `ValidateUrlAsync` used to get wrong, see below; plain-HTTP+public/no-policy → null; plain-HTTP+public/approving policy → that IP). A second mutant, `948-restwidget-selectconnectableipasync-policy-consultation-neutralize`, neutralizes the policy-consultation loop — `VERDICT: KILLED` / `GATE: PASS`. `ValidateUrlAsync` (the pre-check) was also refactored to call `SelectConnectableIpAsync` directly instead of re-implementing a similar-but-not-identical loop — see F7 below for why that matters on its own. `production-readiness.md`'s claim is corrected to name what is and is not directly tested.
  - **F5 (MEDIUM): `AllowedPorts` — on the same caller-controlled `RestOptions` object as `AllowPrivateNetwork`/`AllowHttp` — was left completely unvalidated; a caller sending `"allowedPorts": null` turned off the Redis/ES/DB port-probing guard entirely, and the just-rewritten guard comment claimed this couldn't happen ("regardless of the egress policy decision below" — true but irrelevant, since the bypass doesn't go through the egress policy at all).** **Fixed**: `ValidateWidgetConfigs` (both storage backends) now rejects a `rest` widget whose `RestOptions.AllowedPorts` is `null` or empty at write time, same reject-not-strip precedent as `AllowPrivateNetwork`/`AllowHttp`. The false comment is corrected. `Headers`/`Method`/`Body` on the same object remain unvalidated and undisclosed as a residual gap — **not fixed here**, tracked as a follow-up (see `docs/production-readiness.md`'s "未涵蓋、刻意不修" list for the explicit disclosure this entry adds). **Tests**: 3 new tests per storage backend (`null`/empty rejected, default accepted) plus the pre-existing positive control; manually confirmed red-then-green.
  - **F6 (MEDIUM-LOW, net regression for hosts that register a policy): the legacy request-supplied-`options` channel — a `rest` widget with no persisted `RestOptions`, `CanAccess` (viewer-level, not `CanEdit`) is enough to hit `GetWidgetData` with a caller-chosen `options` blob — used to be hard-capped to public HTTPS (the two booleans were stripped and, pre-#948, `ValidateUrlAsync` treated the stripped values as authoritative).** Since #948 those two booleans are no longer read at all, so the strip became a no-op: once any host registers an `IDashboardEgressPolicy` approving some private-network/plain-HTTP destination for its own legitimate widgets, this viewer-reachable channel could reach that **same** destination with the **caller's own** `Url`/`Method`/`Headers`/`Body` — the policy has no way to tell a request-supplied blob from a host-approved persisted widget, since `DashboardEgressDestination` carries no widget identifier (see F7's fifth item and F8 below). **Fixed**: this channel is now rejected outright (`InvalidOperationException`) rather than partially stripped — a net narrowing versus pre-#948 (previously public-HTTPS-only; now not honoured at all for a widget with no persisted `RestOptions`), not a widening. The shipped dashboard UI (`framework_dashboard.js`) never exercises this path — it only ever appends `FilterBar` values as query parameters, never an `options` JSON blob — confirmed by reading the file, not assumed. **Tests**: one test per storage backend proves the underlying `IWidgetDataSource.GetDataAsync` is never reached; manually confirmed red-then-green.
  - **F7 (LOW, same defect class as the entry's own headline finding): six more false or stale cross-component assertions found and fixed in files this PR touches, plus one real semantic bug (not just a comment) it uncovered.** (1) The port-allowlist error message still said "Configure AllowedPorts in the **server-side** RestOptions" — the exact framing this entry declares false; reworded. (2)(3) `RestWidgetDataSourceOptions`'s `Url`/`AllowedPorts` doc comments still described the pre-#948 authorization semantics (`AllowHttp=true` "permits http://" on its own; null `AllowedPorts` "not recommended unless AllowPrivateNetwork is also true" as if that combination were still a legitimate escape hatch) — corrected. (4) `DashboardOptions.EnableEditing`'s doc claimed the pre-fix property "had this exact doc comment" — impossible, since the summary describes behaviour this same fix introduces; the property had no doc comment before this fix. Corrected. (5) `RestWidgetDataSourceOptions.AllowPrivateNetwork`'s remark claimed a policy "may choose to read [the intent signal] from the destination's originating widget" — `DashboardEgressDestination` carries no widget/dashboard/tenant identifier at all, so that capability does not exist; claim removed (see F8). (6) **The one that was a real bug, not just wording**: `ValidateUrlAsync`'s pre-check rejected the whole DNS answer set if **any** resolved IP was unapproved, while the connect-time layer only ever needed **one** approved candidate — a legitimate multi-A-record host where the policy approves only one specific IP would fail at the pre-check but would have connected fine downstream, and the removed comment claimed these were "the identical failure mode." Fixed by construction, not by re-wording: `ValidateUrlAsync` now calls `SelectConnectableIpAsync` directly (see F3) instead of maintaining a second, hand-synchronized loop, so there is exactly one "does any candidate qualify" implementation for both layers to share. `docs/production-readiness.md`'s "沒有再找到第三處" ("found no third instance") completeness claim from the initial round is corrected to name the grep actually run and its real result — it was false; the six items above are exactly the "third instance" (and more) it claimed did not exist.
  - **F8 (design, deferred — not implemented this round): `DashboardEgressDestination` carries no tenant/widget/dashboard identifier, which is why F6 could not be closed at the policy layer and why F7's fifth item was false.** The reviewer's argument for adding non-`required` `TenantId`/`WidgetId`/`DashboardId` properties now (source-compatible while the interface is unreleased; `required` members added after release would be binary- and source-breaking) is read and not disputed on the merits. **Not implemented in this PR** — the dispatching instruction for this round was explicit that F8 is a design question to report on, not to resolve unilaterally, and `IDashboardEgressPolicy` is genuinely usable and safe without it today (F6 is closed by rejecting the ambiguous channel outright, not by giving the policy more context to disambiguate it). Whether to add this before the interface ships in a release, and whether the answer changes if #952 (the sibling cache-key defect) ends up needing the same context threaded to `WidgetDataRequest`, is left to the maintainer.

### Security — Dashboard REST widget credentials echoed to every viewer; headers silently wiped on save through the shipped designer (#957)

- **`_DashboardController.Get` returned the whole persisted `DashboardDefinition` — including every widget's `Source.RestOptions.Headers` real values (`Authorization`/`Cookie`/API keys, per that property's own doc comment) — to any caller who merely passed `CanAccess`.** `CanAccess` authorizes viewing dashboard content; it is not credential-view authorization. The shipped viewer (`framework_dashboard.js`'s `DashboardManager._loadDashboard` → `fetch('/_dashboard/' + id)`) loads and holds that response in the browser, so this was reachable through the framework's own normal use, not only a direct API call. **Read-path enumeration** (`git grep -n -E 'Ok\(.*[Dd]ashboard' -- '*.cs'` plus `git grep -n -E 'DashboardDefinition|WidgetDefinition' -- 'src/**/*Controller*.cs'`, full tree): `Get` is the only endpoint that can emit a `DashboardDefinition`/`WidgetDefinition`/anything carrying `RestOptions` into an HTTP response — verified, not assumed, that `List` (`DashboardSummary`) has no `Widgets` field, and that `GetWidgetData`/`PostWidgetData`/`_DashboardDesignerController.Preview` all return `WidgetDataResult`, whose fields `RestWidgetDataSource.MapToWidgetResult` never populates from `RestOptions` (and whose upstream-failure error message is deliberately generic, issue #101).
- **Same root cause, second live defect: `Update` replaced a dashboard's entire widget set wholesale, with no merge logic for headers — any save that didn't explicitly re-send a widget's `Headers` silently wiped it.** The shipped designer (`framework_dashboard_designer.js`) has no headers-editing UI at all; its `_readForm()` rest-widget branch only ever produces `{ kind: 'rest', restOptions: { url: ... } }` — no `headers` key, ever. Saving any REST widget through the shipped designer wiped its headers today, independent of the read-side leak above.
- **Fix**: new `WalkingTec.Mvvm.Core.Dashboard.DashboardCredentialMasking`. `MaskForRead` replaces every header VALUE with a sentinel constant on the way out of `Get` (keys survive, so a future editor UI can still list configured header names) — non-destructive by construction (builds a new widget/options/dictionary only where something actually needs masking) because `JsonFileDashboardService`'s definition cache can hand back a shared instance; masking in place would have permanently corrupted what a subsequent `GetAsync`, or the REST fetch pipeline's own internal read, would see. `ReconcileWidgetHeaders` runs before `Create`/`Update`/`Preview` persist anything, and reconciles the caller's `Headers` against what's already stored: key absent or explicit JSON `null` → preserve every existing header (this is what fixes the designer's silent wipe, **with no JavaScript change** — the designer already omits the key, and this is exactly what the null case now means); present-and-empty `{}` → clear all headers; a real value → stored as given; the sentinel value for a key that already has a stored value → restore the real stored value (a round-tripped masked value from a prior `GET` means "unchanged," not "set my header to this literal string"); the sentinel value for a key with **no** existing value to restore → **rejected** (`400`, nothing persisted) — same reject-over-silently-strip precedent this file already applies to `AllowPrivateNetwork`/`AllowHttp`/`AllowedPorts` (#948): a silently-dropped header looks identical to a normal save and leaves no signal, while storing the literal sentinel as a "credential" would be both useless and would silently discard whatever the caller actually intended. `RestWidgetDataSourceOptions.Headers` changed from `Dictionary<string,string> Headers { get; set; } = new();` to `Dictionary<string,string>? Headers { get; set; }` (no default) — verified empirically that System.Text.Json cannot otherwise distinguish an absent `headers` key from an explicit `{}` (both bound to the same empty, non-null dictionary under the old, non-nullable shape), which is exactly the distinction the absent-vs-empty cases above depend on.
- **Tests (initial round)**: 29 new (`test/WalkingTec.Mvvm.Core.Test/Dashboard/DashboardCredentialMaskingTests.cs`, `DashboardCredentialMaskingEndToEndTests.cs` — the latter through the real `_DashboardController` + real `JsonFileDashboardService`, not a mock). Verified against the pre-fix tree (stashed production changes, `DashboardCredentialMasking` temporarily swapped for a same-signature no-op stand-in): 15 of the 29 genuinely fail pre-fix with messages directly naming the defect (an unmasked real header value where a sentinel was expected, or a rejection that didn't happen); the other 14 stay green even pre-fix — some because System.Text.Json's `{}`/explicit-`null` binding never depended on this fix, some because a case (empty-dict or a plain real value) happens to produce the same outcome under the old naive wholesale-replace as under the new merge logic — full accounting of which is which in `docs/production-readiness.md`'s matching entry, not glossed over here.

**Independent adversarial review (PR #960) — re-derived the sink inventory itself rather than following the initial round's greps, found F1–F8.** F1 fixed as a design change (not a "reject the whole save" patch); F2 filed separately, not touched here; F4/F6/F8 fixed; F5 closed with new test + mutant; F7 disclosed as a residual gap, not fixed (scope decision deferred to the maintainer).

- **F1 (the important one): naive case-(a) "always preserve when `Headers` absent" turned the designer's ACCIDENTAL fail-safe into credential forwarding.** Pre-fix (initial #957 round), the designer's silent header wipe meant an editor could not point a widget's real credential at an attacker-chosen host through the shipped UI — the header just vanished. Once absence-means-preserve shipped, the opposite became true: an editor who merely retypes a widget's URL — the ONLY field the designer's REST-widget UI exposes, with no headers-editing UI to show them what would happen — has the SAME real header value silently carried onto whatever new host they typed, nothing logged, nothing confirmed, and the very next viewer/background fetch sends that credential to the caller-chosen destination. A public HTTPS destination takes `RestWidgetDataSource`'s fast path with no `IDashboardEgressPolicy` consultation, so #948's egress gate does not block this either — that gate answers "may this application reach this network location," not "should this specific credential follow this specific widget to a new one." **Not a privilege escalation** — an editor could already read the plaintext via `Get` before #957 shipped at all — but the read-side mask was one ordinary edit away from being defeated by anyone who already has edit rights, and neither the initial CHANGELOG entry nor its residual-gaps list said so.

  **Fix, not a blanket rejection**: `ReconcileWidgetHeaders` now compares the incoming widget's `RestOptions.Url` against the existing one by destination (scheme+host+port, host case-insensitive, default ports normalized so `https://h`/`https://h:443` compare equal — `Uri` parsing, not string comparison). Same destination → reconciliation proceeds exactly as the initial round shipped it (the common case: path/query edits on the same host). Different destination — or either URL fails to parse, deliberately treated as "not the same destination" rather than falling into the preserve branch on an ambiguous comparison — stored headers are **not** carried over: case (a) leaves `Headers` empty (a silent drop, not a reject, logged via a new `HeaderDriftWarning`/`LogWarnings`, because rejecting the save outright would permanently lock a widget's URL once it had a header — the designer has no way to re-supply one), and case (d)'s sentinel-for-an-existing-key is routed into case (e)'s reject instead of a silent restore (a caller explicitly round-tripping a sentinel against a NEW destination is not a shape the shipped designer produces at all, so the stricter response is warranted). This deliberately mirrors what `HttpClient` itself does on a cross-host redirect — drops `Authorization` rather than forwarding it — and is the same reasoning #948 already used to disable auto-redirect on this client's named `HttpClient`: credentials do not automatically follow a destination change, on principle, not as a one-off heuristic for this endpoint.

  **Known residual, disclosed rather than left implicit**: an editor who changes a widget's host AND, in the same or a later save, explicitly re-supplies the SAME real header value for the new host (case (c) — a real, caller-typed value, not a round-tripped sentinel) succeeds, by design. That is indistinguishable from a legitimate credential rotation; F1 closes the SILENT, no-visible-step forwarding path, not an editor's pre-existing ability to deliberately retype a credential wherever they choose — that capability is unchanged from every editor's existing `CanEdit` scope and is not a new gap this fix introduces.

  **Tests**: 15 new across both files (14 unit + 1 end-to-end; F4 and F5 below have their own, separate test counts) — per-case unit coverage (same-host preserve incl. path-only edits, cross-host drop with warning contents asserted, cross-host sentinel reject, host case-insensitivity, default-port normalization for both `http`/`https`, differing port, differing scheme, and — "decide and test each," not assume — an unparseable/empty URL on the incoming side and on the existing side, each independently), plus one end-to-end reproduction of the exact attack script (create a widget with a real credential on one host, PUT a designer-shaped body retyping only the URL, assert the persisted definition AND the live widget-data-fetch pipeline both show no carried-over credential).
- **F4 — undocumented sixth case with inverse semantics: an incoming widget with `Source` but no `Source.RestOptions` node silently wiped the stored `RestOptions` (URL included, not just headers) — the exact inverse of case (a) one level up.** The original five-case doc never mentioned it, and the initial CHANGELOG entry told integrators "omitting the key now PRESERVES existing headers" — true for `headers`, false for `restOptions` itself. The one existing test for this shape (`..._ignores_widgets_without_RestOptions`) asserted only that no error was returned, never which way the wipe went — the behaviour was pinned in neither direction, and the name claimed something ("ignores") the code did not do. **Fix, case (f)**: when the incoming widget's `RestOptions` is absent but `Source.Kind` is still `"rest"`, the ENTIRE existing `RestOptions` is preserved (cloned, not aliased) — same "absence in a partial update means untouched, not delete" principle as case (a). When `Kind` changed away from `"rest"` (e.g. the designer's own source-kind dropdown), dropping `RestOptions` is the correct, intentional behaviour and case (f) does not apply — pinned by a dedicated boundary test so this fix cannot itself over-apply and make a widget's REST config impossible to remove. Renamed the old test to `..._non_rest_widget_without_RestOptions_is_a_no_op` and gave it a real assertion. **Tests**: 3 new (the case (f) preserve, a clone-not-alias check, and the Kind-changed-away boundary pin).
- **F5 — `_DashboardDesignerController.Preview`'s own reconciliation guard had zero test coverage; deleting that call site turned nothing red.** Both original mutants targeted `_DashboardController` only. **Tests**: 2 new — `Preview_with_masked_sentinel_header_value_is_rejected` (red-before-fix) plus a positive control (`..._with_a_real_header_value_is_accepted_by_the_header_guard`) — and a third mutant, `957-dashboard-preview-header-guard-neutralize`, isolating this exact call site.
- **F6 — `MaskedHeaderValue` changed from `const` to `static readonly`.** WTM ships as NuGet; a `const` is inlined into every downstream assembly's IL at THEIR compile time. If the sentinel literal ever changed in a future release, an already-compiled downstream binary would keep comparing against the OLD value forever and would start persisting the NEW sentinel it receives from a live server as a literal credential, since its own compiled-in comparison would never match. `static readonly` resolves at the consumer's runtime against whichever WTM binary is actually loaded.
- **F7 — masking covers `Headers` only; `Url` and `Body` are copied to the masked `Get` response completely unmasked, and both are at least as common a place for a credential.** `Url` may itself carry one via userinfo (`https://user:pass@host/...`) or a query-string key (`...?api_key=...`); `Body` may carry one in a POST payload. Any `CanAccess` viewer still receives both in full. **Not fixed here — scope decision deferred to the maintainer**; disclosed in `docs/production-readiness.md`'s residual-gaps list with these two concrete shapes so the docs do not read as if credential exposure through this endpoint is closed.
- **F8 — the reflection-driven clone-drift guard (`DashboardCredentialMaskingCloneDriftTests`) had its own blind spot: its `bool` generator always returned `true`, so a future `bool` property whose OWN default is already `true` would be "populated" indistinguishable from its default, and a clone that dropped it would go undetected.** Fixed generally, not by special-casing `bool`: the populator now constructs a fresh default instance of the declaring type per property, reads that property's own actual default from it, and retries generation (`bool` alternates; `enum` cycles every member) until the candidate provably differs — throwing after a bounded number of attempts for a type that structurally cannot vary, per the guard's own established principle that an unpopulated-to-default property can never prove a future clone drops it. Verified, not just claimed: temporarily added a `bool` property defaulting to `true` to `RestWidgetDataSourceOptions` without adding it to the clone — the guard caught it (`expected 'False', actual 'True'`); removed after capturing the failure.

- **Suite**: `test/WalkingTec.Mvvm.Core.Test` `4927 passed, 0 failed` (baseline `4874`; delta 53 = the 29 tests above + 1 reflection-driven clone-drift guard + 3 tests verifying the `Headers` nullability round-trips cleanly through JSON already persisted before this fix + 20 F1/F4/F5 tests from this review round). `src/WalkingTec.Mvvm.Mvc.Tests` `64 passed, 0 failed`; `test/WalkingTec.Mvvm.Api.Test` `102 passed, 0 failed` (1 pre-existing mutation-gate baseline selftest skipped, as usual). Four mutants, all `VERDICT: KILLED` / `GATE: PASS`: `957-dashboard-get-masking-neutralize` (reverts `Get` to the pre-fix `return Ok(dashboard)`), `957-dashboard-update-header-merge-neutralize` (comments out `Update`'s `ReconcileWidgetHeaders` call), `957-dashboard-header-host-scoping-neutralize` (F1 — forces the destination comparison to always report "same"), `957-dashboard-preview-header-guard-neutralize` (F5 — comments out `Preview`'s own reconciliation call).
- **Known residual gaps, not addressed here** (full disclosure in `docs/production-readiness.md`): `Url` and `Body` are unmasked on read (F7, above) — a `CanAccess` viewer can still see a credential carried in either. `Headers`' own content (format/size/charset) is still unvalidated caller data — the same gap #948's F5 already disclosed; this fix narrows what that gap can leak, it does not close it. The masking sentinel is a fixed string constant, not a cryptographically-random value — a real header value that happened to equal it exactly would be misinterpreted by the merge logic; considered acceptably unlikely and not otherwise guarded. The read-path enumeration above proves today's code, not a structural guarantee that no future response field could ever carry `RestOptions` again. F1's host-scoping does not stop an editor willing to deliberately retype a real credential for a new host (case (c)) — see F1's own residual note above.

### Security — Dashboard REST widget cache key omits tenant and headers, letting one cached response leak across tenants/credentials (#952)

- **`RestWidgetDataSource.BuildCacheKey` was `$"{Method}::{Url}::{Body}::{JsonPath}"` — no tenant, no `Headers`.** Two REST widget fetches that differ only in `TenantId`, or only in `Headers` (e.g. one carrying a real `Authorization`, one carrying none), landed on the same `IMemoryCache` entry — an unauthenticated or wrong-tenant widget could read back another widget's already-cached, potentially privileged response for the whole `CacheTtlSeconds` window. Disclosed as a residual gap in #948's own entry above ("`WidgetDataRequest`／REST 回應快取路徑刻意未觸碰") and again in #948's F5 finding; this closes it.
- **The fix is not "add `Headers` to the key" — the key is now structurally incapable of omitting a field.** A design-review draft that took the "just add the missing field" approach itself omitted `AllowedPorts` — proof that hand-enumerating a cache key's inputs is not a safe pattern to repeat. `BuildCacheKey` now reflection-serializes the ENTIRE `RestWidgetDataSourceOptions` object, recursively re-sorts every JSON object's properties by key (`StringComparer.Ordinal`, generic — no `Headers`-specific code, so any future `Dictionary<string,string>` property gets the same treatment automatically), prepends the tenant (or a distinct null sentinel), and SHA256-hashes the result (`WtmRestWidget::v2::<hex>`). **Cache fragmentation is accepted, deliberately** — two widgets differing only in, say, `MaxResponseBytes` no longer share an entry, even though that field cannot affect the response body; this repo's stated priority is Compatibility > Security > Quality > Performance. **No HMAC, no random salt**: an actor that can enumerate `IMemoryCache` keys in-process can equally read the plaintext `Headers` straight out of the widget-definition cache, so an HMAC in the same process buys nothing; a random salt would make "two tenants get different keys" a tautology no test could ever fail.
- **`Headers == null` vs `Headers == {}`** (distinguishable since #957) are **deliberately not** canonicalized to the same key, even though both currently produce a byte-identical outgoing request — doing so would couple this method to `FetchJsonAsync`'s send-time behaviour and reintroduce the per-field special-casing this redesign removes. See `docs/production-readiness.md`'s matching entry for the full null-vs-`{}` reasoning.
- **Tests**: tenant isolation and `Headers` isolation (both RED pre-fix), a byte-identical positive control (so a "fix" that just disables caching cannot pass the two isolation tests for the wrong reason), and an end-to-end attack reproduction — a privileged widget with a real `Authorization` completes a fetch and caches; within the TTL, a second widget with identical `Url`/`Method`/`Body`/`JsonPath` and no headers must get its own response, never the cached authorized one.
- **Mutant**: `952-restwidget-cachekey-tenant-component-neutralize` — `VERDICT: KILLED` / `GATE: PASS`.

### Security — Dashboard REST widget `Headers` had no framework-level content limits (#956)

- **`RestWidgetDataSourceOptions.Headers` had no name blocklist, no count cap, and no size cap.** Disclosed as a residual gap in #948's F5 finding ("`Headers`/`Method`/`Body` 仍未驗證"). `Authorization` and arbitrary custom headers (e.g. `X-Api-Key`) remain fully legal — a REST widget calling an external service that needs an API key is this feature's explicit supported use case and is not affected.
- **Fix**: `RestWidgetDataSource.ValidateHeaders` (one shared implementation) hard-rejects `Host`, `Transfer-Encoding`, `Content-Length`, `Connection`, `Upgrade`, `TE`, `Trailer`, `Expect`, and anything starting with `Proxy-` (case-insensitive), caps header count at 20, and caps total name+value UTF-8 length at 8 KB. This set is not an arbitrary blocklist — it breaks #948's own DNS-pinning/connect-time SSRF guard invariants. `Host` is the sharp case: `PinnedConnectAsync` connects to an approved, resolved IP while deliberately keeping the outgoing request's URI (and its default `Host` header) as the original hostname the policy checked; a caller-supplied `Host` override would let that already-approved connection present as a different virtual host to whatever server answers the socket.
- **Enforced at both write time** (`JsonFileDashboardService`/`EfCoreDashboardService.ValidateWidgetConfigs`, same reject-not-strip precedent as `AllowPrivateNetwork`/`AllowHttp`/`AllowedPorts`) **and send time** (`RestWidgetDataSource.FetchJsonAsync`) — write-time-only would leave an already-persisted or hand-edited widget unprotected forever; send-time-only would give an operator reviewing a definition no signal it is invalid.
- **Tests**: write-time and send-time each cover every hard-rejected name, the count cap, and the size cap, plus a positive control proving `Authorization`/custom headers still work end to end. The send-time DataRow set deliberately excludes `Content-Length`: RED-verification showed that one case stays green even with this guard removed, because .NET's own `HttpRequestHeaders.Add` independently rejects `Content-Length` (a content-only header) and this file's pre-existing S3 CRLF-injection `try`/`catch` happens to wrap that in the same exception type — decoration, not coverage, for this specific guard. The write-time case has no such confound and is fully RED-verified.
- **Mutants**: `956-restwidget-header-write-time-guard-neutralize` and `956-restwidget-header-send-time-guard-neutralize` — both `VERDICT: KILLED` / `GATE: PASS`.

### Security — `DashboardEgressDestination` carried no tenant/widget/dashboard identity (#948-F8)

- **#948's F8 finding — deferred at the time — is implemented.** `DashboardEgressDestination` gains six non-`required` init properties: `TenantId`, `DashboardId`, `WidgetId`, `Method`, `HeaderNames`, `HasBody`. Purely additive (the type is a sealed class the framework constructs; policies only read it), so the original "binary/source breaking" concern for adding members later does not apply here — the real reason to add them now is semantic freezing (a policy that never saw `TenantId` can never retroactively become tenant-aware without a breaking change) and the compounding cost of deferring further.
- **Built by one shared function, `RestWidgetDataSource.BuildDestination`** (fed by `BuildRequestContext`), called from both the pre-check (`ValidateUrlAsync`) and the connect-time check (`PinnedConnectAsync`) through the existing single `SelectConnectableIpAsync` call site — the two checks are guaranteed to see field-identical destinations for the same fetch, not two hand-synchronized copies.
- **`HeaderNames` carries header NAMES only, never values** — its own doc comment states both what it gives a policy (e.g. "is `Authorization` configured") and that a name appearing here does not mean the value was validated (that is #956's independent job).
- **`ConfiguredAllowlistDashboardEgressPolicy`'s `DashboardEgressAllowlistEntry` gains `Methods`/`TenantId` and actually gates on them** — a destination field with no in-repo consumer is speculative generalization; `DashboardId`/`WidgetId`/`HeaderNames`/`HasBody` have no built-in-policy consumer yet (deliberate — they are context for custom `IDashboardEgressPolicy` implementations), only `Method`/`TenantId` were required to get one.
- **Heterogeneous review follow-up (2026-07-31): the `req.Options.Set`/`TryGetValue` round trip that threads the request context into `PinnedConnectAsync` had no test whose deletion turned red.** `PinnedConnectAsync`'s readback is extracted into `RestWidgetDataSource.ReadRequestContext(HttpRequestMessage)`, and a new test drives a real `GetDataAsync` call, inspects the actual request a mock handler receives, and calls that exact method (not a hand-duplicated `TryGetValue`) — RED-verified against both the `Set` call and `ReadRequestContext`'s body independently. The one line that remains genuinely unpinned — `PinnedConnectAsync`'s own one-line delegation to `ReadRequestContext` — is empirically confirmed unreachable by any test (a full `Dashboard`-namespace run stays green with that line neutralized) and is safe by construction: it degrades to a `null` context, which every downstream gate (`ConfiguredAllowlistDashboardEgressPolicy`'s `Methods`/`TenantId` checks) already treats as fail-closed, and the pre-check (`ValidateUrlAsync`) already enforces the full context before this code path is ever reached. See `docs/production-readiness.md`'s #948-F8 entry for the full account.
- **`WidgetDataRequest` gains `DashboardId`/`WidgetId`** (optional, additive) — `JsonFileDashboardService`/`EfCoreDashboardService.GetWidgetDataAsync` already had both as method parameters and now thread them through.
- **Tests**: pre-check vs. simulated-connect-time destinations proven field-equal (`SocketsHttpConnectionContext` has no public constructor a test can use to drive `PinnedConnectAsync` directly — the same boundary #955's F3 test group already accepted for this class); `HeaderNames` proven to carry names and no values for a secret-bearing header; `DashboardEgressAllowlistEntry` proven to gate on both `Method` and `TenantId`.
- **Mutant**: `948f8-configuredallowlist-method-check-neutralize` — `VERDICT: KILLED` / `GATE: PASS`.

### Fixed — e2e suite: swallowed-exception sites and zero-assert security tests could never fail (#898, #905)

`test/e2e/wtm_e2e_tests.py` had two sites where the CI-critical "e2e legs green" evidence this repo's own merge decisions cite (see CLAUDE.md "CI red does not mean failed") was hollow: 12 sites across TC-04/24/25/26/27/28/29 that caught an exception and let the test report PASS regardless (#898), and three security-themed tests (TC-09/10/12) that only printed observations and never asserted (#905). Re-derived both counts independently before touching anything (`grep -c "_screenshot_on_failure(page, [0-9]"` → 12, matching #898's title exactly) rather than trusting the issue text; also found TC-25–29's function bodies had zero `assert` statements anywhere, not only at the flagged catch sites, and fixed a fourth site (TC-30) the issue titles don't name but whose own docstring already framed it as the same bug. **Full accounting of what each rewritten test now proves, what it deliberately still does not, and the two pre-existing defects this work surfaced but did not fix, is in `docs/production-readiness.md` § "E2E 測試可靠度修正（#898/#905）" — this entry does not repeat or exceed those claims.**

- All 12 `except Exception:` sites narrowed to `except PlaywrightTimeoutError:`; every other exception type now surfaces as `ERROR` instead of being swallowed and silently re-interpreted.
- Every touched test gained real assertions bound to the behaviour named in its own function name (panel actually opens, grid actually renders rows, form fields actually exist, security cookie actually rotates), replacing print-only observations.
- TC-09 (Session Fixation) rewritten to simulate the actual attack: plant an attacker-chosen fixed value under the real auth-cookie name before login, assert it is not still present after login.
- TC-10/TC-12 (Security Headers / Rate Limiting): the underlying WTM middlewares (`WtmSecureHeadersMiddleware`, `WtmRateLimitAttribute`) exist but are opt-in and the demo does not enable either — both tests now assert the demo's current, verified-true baseline (headers absent; repeated failed logins never 500) instead of a protection the demo has never turned on.
- Fixed a pre-existing navigation bug in TC-26/27/28/29/30: `page.goto()` to several grid/form URLs hit PartialView-only endpoints that never load `layui.js`, so those tests were structurally incapable of observing real rendering. Switched to the existing `open_grid_via_sidebar()`/`open_toolbar_dialog()` helpers plus a new `open_grid_via_direct_tab()` for pages with no sidebar entry.
- Every rewritten assertion was proven reachable by deliberately breaking the corresponding behaviour (a real, temporary demo-app change for TC-10; targeted test-file mutations reverted immediately after for the rest), observing the exact `FAIL`, then reverting — see `docs/production-readiness.md` for the full per-test list and which specific assertions were, and were not, individually proven this way.
- Found, but did not fix (no authorizing issue for a `src/` change, out of this fix's test-only scope): a genuine JS syntax bug in `/_EtlJob/Index`'s custom toolbar action (`DataTableTagHelper.cs:1284`, a missing-parens IIFE) that breaks that page's grid rendering via any real navigation path. TC-29 now documents this as a KNOWN-GAP and asserts only what the bug does not affect.
- `run_tests()`'s summary-line accounting (`_compute_stats`, the `Total: N | PASS: n | FAIL: n | ERROR: n | SKIP: n` line, the synthetic `SUITE-ABORT` path) was not modified; all 11 deliberate `FAIL`s produced during this work were confirmed to land correctly in that line's `FAIL` column, individually, via `--tc <N>`.

### Added — AST lint makes an unfailable e2e test a CI error (#917)

`#898`/`#905` (above) fixed the individual instances of e2e TCs that could report PASS regardless of the behaviour under test, by hand. This is the same design review's follow-up: a CI-enforced check that turns the same defect class into a merge-blocking error going forward, instead of waiting for the next manual audit to find it. **Binding design decision: this is an absolute rule, not a ratchet** — no baseline file, no exemption list, no frozen violation count (a count-based ratchet has a swap hole: delete one old violation, add one new one, the count stays put and the gate stays green; this repo's own coverage ratchet has already shown a manually-raised threshold just stagnates). **Full accounting of the four checks, what they deliberately do not catch, the re-derived violation inventory, and both gate-fires demonstrations is in `docs/production-readiness.md` § "e2e 測試完整性 AST lint（#917）" — this entry does not repeat or exceed those claims.**

- New `scripts/check-e2e-test-integrity.py` (Python stdlib `ast` only, no third-party dependencies; reads `TC_REGISTRY` as a literal `ast.Dict`, never imports the target module) checks, against `test/e2e/wtm_e2e_tests.py`: every TC registered in `TC_REGISTRY` has its own `assert` (or matches `tc_36`'s structural docstring+`raise TestSkipped(...)` exemption, matched by shape, not by an allowlist of names); no exception handler that can catch `AssertionError` fails to re-raise with a bare `raise` when its `try`'s own body contains a literal assert (this is what would catch `except AssertionError: raise TestSkipped(...)` laundering a FAIL into a SKIP); every top-level `tc_*` function is actually wired into `TC_REGISTRY` (the e2e counterpart of #855 defect 4); and `assert <constant truthy>` is flagged anywhere it appears. Exit codes match this repo's existing `changes`-job guards: 0 clean, 1 violation(s), 2 could not analyse (parse failure, missing file, no `TC_REGISTRY` found).
- Deliberately does not catch: non-constant tautological asserts (undecidable statically); an assert living in a helper a TC calls, with none in the TC's own body (keeps the fix for a flagged TC a one-line addition); multi-level indirect swallowing (every incident found so far has been single-level); an author editing this lint and its own `--selftest` fixtures in the same PR (visible in the diff, not something any lint can prevent).
- `--selftest` runs six embedded fixtures (no external files) and exits 1/1/1/1/0/2 respectively for: a zero-assert TC (message names the offending function), a swallowed-assert handler, the `except AssertionError: raise TestSkipped(...)` laundering shape, an unregistered `tc_*` function, a clean fixture (the positive control — without it a lint that always exits 1 would pass every negative case above), and unparseable input.
- Wired into `.github/workflows/mutation-gate.yml`'s `changes` job — the only job with no path filter on either trigger, already a required check via `gate`, alongside the four existing guards of the same shape (#924, #931 ×2, #926). The step runs `--selftest` first, then the real scan; no new required-check context was added (#838/#844).
- Re-derived the violation inventory independently (full-file AST scan, not a re-read of the existing #898/#905 tally): exactly one violation, `tc_03_csrf_token` — zero-assert, unconditional PASS, printing `[KNOWN-GAP]` for the documented CSRF gap. `tc_03` converted to characterization assertions (`token_count == 0`, no-token POST `response.status == 200`) in the same PR — same honest shape as `TenantFilterInvariantTests.cs`'s `WfDemoShapedObsoleteContext`: pins the observed status quo, not a spec. Both marker values were verified against a real locally-run demo app before being written, not copied from a sibling test.

### Fixed — Vue3Demo ClientApp had no CI build gate; two always-reproducible build failures survived undetected (#941, #939, #940)

`demo/WalkingTec.Mvvm.Vue3Demo/ClientApp` (a self-contained Vite/Vue3 frontend the .NET solution build never touches) had no CI build gate at all — the reason #939's `npm ci` failure and #940's `vite build` failure could both sit in the tree simultaneously with nothing ever tripping over either one. The gate was added first and confirmed to fail against the unfixed tree before either fix landed. **Full accounting — the exact reproduction output for both defects, three additional peer-dependency conflicts this work found blocking `npm ci` even after #939's own fix (not covered by either named issue, flagged rather than silently absorbed), the path-filter proof in both directions, and what this gate does not cover — is in `docs/production-readiness.md` § "Vue3Demo ClientApp CI 建置閘門（#941/#939/#940）" — this entry does not repeat or exceed those claims.**

- New `.github/workflows/vue3demo-build.yml`, path-filtered to `demo/WalkingTec.Mvvm.Vue3Demo/ClientApp/**` on `pull_request`/`push` to `dotnet10` (plus `workflow_dispatch`) so it only runs when the ClientApp actually changes. Runs `npm ci` then `npm run build` — an install-only gate would not have caught #940.
- **#939**: `@vitejs/plugin-vue` bumped `^4.1.0` → `^6.0.8` (peer now accepts `vite ^7.x`, matching the Dependabot-bumped `vite ^7.3.2` already in the tree).
- **#940**: `DashboardView.vue`'s two `@/utils/dashboard/responsive` imports corrected to `/@/utils/dashboard/responsive`, matching the alias every other file in `src/` already uses (`vite.config.ts` / `tsconfig.json` only register `/@/`, never bare `@/`).
- Unused `echarts-gl`/`echarts-wordcloud` dependencies removed (verified zero import sites anywhere in `src/`) and `@types/node` bumped `^18.15.11` → `^20.19.0` — both were additional peer-dependency conflicts this work found blocking `npm ci` after #939's fix alone, not part of either named issue.
- `python3 scripts/audit-workflow-timeouts.py`: 132/132 real-work steps across all 8 workflow files now carry `timeout-minutes` (up from 127/127).

### Migration

- **#956 — a persisted `rest` widget whose `Headers` includes `Host`, `Transfer-Encoding`, `Content-Length`, `Connection`, `Upgrade`, `TE`, `Trailer`, `Expect`, or any `Proxy-*` name, more than 20 headers, or a combined name+value length over 8 KB, will fail to fetch (`502`, from the new send-time rejection) after upgrading, and will fail to save (`400`) if edited again.** No deployment is known to configure any of these on purpose — they cannot appear through the shipped designer UI (no headers-editing UI at all) — but check any REST widget definitions authored through a direct API call or hand-edited JSON store before upgrading. There is no config flag to restore the old behaviour: this set breaks #948's own SSRF-guard invariants and is not safe to make configurable. `Authorization` and any other custom header are unaffected.
- **#952 — REST widget response caching is now scoped per tenant and per exact `Headers` set, where it previously was not.** This is a correctness fix (a shared cache entry across tenants/credential sets was the defect), but it also means widgets that were previously (accidentally) sharing a cache entry — most likely: the same public URL fetched by multiple tenants or by widgets with different header configurations — now fetch independently. A deployment with many tenants/widgets hitting the same rate-limited upstream API should expect a higher outbound request volume post-upgrade than the pre-fix (incorrect) sharing produced. No configuration change is required or available; `CacheTtlSeconds` still controls how long each now-correctly-scoped entry lives.

## [10.18.0] - 2026-07-22

The **LayUI eval-retirement epic (#470) reaches the whole form + grid + dialog family.** Slices G→O plus the docs endgame (Q) complete the opt-in, eval-free island-render migration begun in 10.16.0: every interactive LayUI widget — combobox/tree, transfer, upload, laydate, slider/colorpicker, ueditor/richtext, textarea counters, tree-container, chart, search-panel, and the full data grid (render core, toolbar/row-button dispatch, local-data, and cell editing) — now renders through declarative JSON islands + `data-wtm-*` delegated handlers when `WtmUIOptions.UseSelectIslandRender = true`, instead of inline `<script>`. **Every migration is default-off and byte-identical to before** — this release ships **zero behaviour change** to existing deployments ⚠️ *(the byte-identical and zero-behaviour-change claims in this sentence are **retracted** — see "Corrected" under [Unreleased] and #835)* while making a strict, `unsafe-inline`/`unsafe-eval`-free Content-Security-Policy achievable for the whole form/grid/dialog surface. `framework_layui.js` stays at exactly **one** active-code `eval(` (the deprecated `IsScript` path). Also: a vendored-layui XSS fix (opt-in-legacy only), refresh-token table indexes, and CI/compose ARM64 fixes.

### Security

- **`System.Security.Cryptography.Xml` bumped 10.0.9 → 10.0.10 (#788).** Five high-severity advisories (GHSA-23rf-6693-g89p, GHSA-8q5v-6pqq-x66h, GHSA-cvvh-rhrc-wg4q, GHSA-g8r8-53c2-pm3f, GHSA-mmjf-rqrv-855v) were published against `10.0.9` — the version WTM directly pins to override NPOI's vulnerable transitive pull — after v10.17.0 shipped with a clean scan. `10.0.10` is the patched servicing build; the pre-tag LOCAL vulnerability scan is back to **0 NU1903**. The `NU1510` "override working" note on this pin is unchanged and still expected.
- **Vendored layui 2.6.3 `data-content` attribute XSS — escaped (opt-in-legacy only) (#776).** The bundled/reference layui 2.6.3 tree's `table.js` built each grid cell's `<td data-content="...">` from the raw field value with no double-quote escaping, allowing attribute-breakout XSS on essentially every grid cell — independent of WTM's own `ff.EscapeText` cell guard (#108), which never runs on that layui-internal path. **The default `/layui-next` (2.13.8) tree was never affected** — it already escapes via `util.escape` — and only `Layui:Asset=legacy` serves the vulnerable 2.6.3 tree; the framework NuGet packages ship no layui at all (it is vendored only in demo/scaffold `wwwroot`). The upstream-parity `layui.util.escape` fix is applied to **all** on-disk copies of the vulnerable construction — both the standalone `lay/modules/table.js` and the monolithic `layui.js` bundle that production `_Layout.cshtml` actually loads (patching only one leaves the other exploitable). A one-time startup `LogWarning` now fires when `Layui:Asset=legacy` is configured, and `docs/csp-hardening.md` documents it. Severity: **Medium** (opt-in-legacy-only; not reachable by default config).

### Added

- **`WtmUIOptions.UseSelectIslandRender` (opt-in, default `false`) now covers the entire form + grid + dialog family — eval-free island rendering (#470 Slices G–Q).** Building on 10.16.0's form-widget/button slices, this release islandifies the remaining interactive surface, all behind the same default-off flag and all byte-identical when off:
  - **N1** — `<wt:treecontainer>` (`renderTreeContainer`) and `<wt:chart>` (`renderChart`) islands (grid-linked tree filtering and echarts init without inline `<script>`).
  - **N2** — `<wt:searchpanel>` delegated `click`/`myclick` wiring + `searchPanelInit` island (jQuery-custom-event contract preserved so `ff.RefreshGrid` still drives grid reloads; `OldPost` and selector-hosted panels stay legacy).
  - **O1** — `<wt:grid>` render core: `renderGrid` island + a client-side templet-descriptor registry replacing the `_raw_`-function-string injection. Compat globals (`{id}option`/`defaultfilter`/`filterback`/`url`) are written synchronously for `wtmColVis`/selector `gridCheckedFunc`/user code.
  - **O2** — grid toolbar + row-action buttons: `gridActions[]` descriptors + `ff._gridToolDispatch` (reusing the Slice-M `data-wtm-click` delegated dispatch) + a `tpl:'actionCol'` row-action templet retiring the `laytpl {{# if }}` conditionals. Also fixes a pre-existing case where dialog-hosted grid toolbars were dead because DOMPurify strips `<script type="text/html">` templates.
  - **O3** — grid local-data island (`localData`), delegated `data-wtm-cellchange` cell editing (replacing regex-injected `onchange`, with `layui.table.cache` index-rewrite semantics preserved verbatim), and a `foldPanel` island for `SearcherExpanded`. Lifts the O1 `UseLocalData` legacy-fallback (such grids now islandify); `EnableAnalysis` and `IsInSelector` grids still fall back to legacy.
  - **#784** — the residual inline-`<script>` emitters a per-emitter sweep surfaced (`<wt:button>`/`<wt:submitbutton>` click-wiring, checkbox/radio/switch `ChangeFunc` + textbox `SearchUrl` autocomplete, `<wt:tab>`/`<wt:panel>` wiring) are now gated too, completing `SubmitButton`'s Slice-M treatment.
  - **Q** — `docs/csp-hardening.md` rewritten (grounded in a per-emitter grep sweep) to make **Level 2/3 strict CSP the recommended target**, with an honest residual-blocker table.
  - Developer-authored callbacks are resolved through the `ff._resolveGuardedWindowFn` identifier guard when they are plain identifiers; a non-identifier callback keeps the exact legacy inline path plus a `console.warn`. Combined with the #627 kill-switch, an app can now progress its dialog/form/grid CSP toward `script-src 'self'`. **Default off = today's behaviour, byte-identical.**

### Improved

- **`FrameworkRefreshTokens` is now indexed (#761).** Added `Token`, `ExpiresUtc`, and composite `(RevokedUtc, ExpiresUtc)` indexes — the token-refresh hot path and the 10.17.0 `AddWtmRefreshTokenRetention` sweeps were full table scans on an unbounded-growth table. The `Token` index is deliberately **non-unique** (compat-first — never risk a failed index build on existing data). See Migration for existing databases.

### Fixed

- **CI: `integration-test` MSSQL readiness on the ARM runner (#767).** The x64-only `mcr.microsoft.com/mssql/server` image crashes under QEMU on the Apple-Silicon self-hosted runner (jemalloc VA-space mapping) — every prior run failed at "Wait for MSSQL ready" polling a dead container. Switched CI and the local dev/test compose files to the native-arm64 `mcr.microsoft.com/azure-sql-edge` image (#773).

### Migration

- **`FrameworkRefreshTokens` indexes on an existing database (#761).** `EnsureCreated()` does not add indexes to a pre-existing table, so databases created before this release will not get the new indexes automatically. On a large/long-lived `FrameworkRefreshTokens` table, add them manually (provider-appropriate): a non-unique index on `Token`, an index on `ExpiresUtc`, and a composite index on `(RevokedUtc, ExpiresUtc)`. New/fresh databases get them automatically. No action needed if the table is small or you do not use `AddWtmRefreshTokenRetention`.
- **No breaking changes.** The entire #470 island family is opt-in behind `WtmUIOptions.UseSelectIslandRender` (default `false`) and byte-identical when off. Turn it on per app once you have audited your dialogs against the residual-blocker table in `docs/csp-hardening.md`.

### Known issues / gated

- **`<wt:linkbutton>` / `<wt:closebutton>` still emit a legacy inline-`<script>` wrapper + `console.warn` when the island flag is on**, because their `Click` is a dotted framework call (`ff.OpenDialog(...)`/`ff.CloseDialog()`) that cannot pass the plain-identifier callback guard — so dialog pages using them still carry inline script + per-page console noise under `UseSelectIslandRender=true`. A descriptor-based island for these fixed framework actions is tracked in #787.
- **#655 (selector sentinel retirement / default flip)** remains gated on BMS staging validation (the same gate as #567 LayUI-modernization Phase 2) — the whole G→Q family stays opt-in until that flip.
- CI overflow-runner image-pull instability (`docker.gitea.com` timeouts) tracked in #783.

## [10.17.0] - 2026-07-21

Field-feedback batch: everything in this release traces to the BMS 10.14.5→10.16.1 production upgrade report (BMS #295 → upstream issues #756–#759, follow-ups #761/#762). Two opt-in features close real downstream gaps (refresh-token retention, explicit rate-limit policy registration), two fixes kill host-crash / cache-poisoning hazards in framework background services, and the 10.15.0 Migration section was retroactively completed (#758, shipped ahead of this release).

### Added

- **`AddWtmRefreshTokenRetention()` — opt-in retention/purge for `FrameworkRefreshTokens` (#757).** After #721 made refresh-token persistence live, the table grew without bound (every login INSERTs a row, every rotation adds another; nothing ever deleted — and rows carry `ITCode`/`CreatedByIp` audit data, a retention surface for regulated deployments). The new service mirrors the ActionLogRetention pattern: daily batched `ExecuteDeleteAsync` sweeps at `RunAtLocalHour` (default 4, staggered from ActionLog's 3), `ExpiredDays`/`RevokedDays` (default 30, ≤0 disables the knob), `BatchSize` (default 5000). **Hard safety invariant:** the revoked sweep always ANDs `ExpiresUtc < nowUtc` — revoked-but-unexpired rows are the reuse-attack chain tripwire (`TokenService.RefreshTokenAsync` checks revoked-before-expiry to trigger descendant revocation) and are never deleted regardless of configuration. Purging old revoked rows downgrades chain containment to plain fail-closed rejection — defaults (30d ≫ 7d token lifetime) keep recent forensics; see XML docs. Opt-in only: nothing runs unless the app calls the extension.
- **Explicit rate-limit policy registration — `WtmRateLimitingOptions.RegisterPolicy(permits, windowSeconds, queueLimit = 0)` + `RequireWtmRateLimit(...)` endpoint extension (#759).** Previously a `wtm_rl_*` named policy existed only if some controller carried the matching `[WtmRateLimit]` tuple — minimal-API endpoints (health checks) referencing a policy via `BuildPolicyName` were implicitly coupled to an unrelated controller action, and deleting/refactoring that action made the endpoint throw `InvalidOperationException` per request while build+tests stayed green (twice field-confirmed by BMS). `RegisterPolicy` guarantees the tuple independent of any attribute (validated by the same guards as the attribute ctor — shared `ValidateTuple`, no drift; HashSet-merged with scanned tuples so explicit+attribute duplicates are safe), and `RequireWtmRateLimit` attaches the canonical policy metadata to any `IEndpointConventionBuilder`. The extension attaches metadata only — pair it with `RegisterPolicy` (unregistered policies fail loudly at request time; no silent auto-registration).
- **`ScanWtmRateLimitAttributes` per-member partial-load guard (#759).** Attribute materialization on members whose dependencies fail to load (`TypeLoadException`/`FileNotFoundException`) no longer aborts the whole scan — mirrors the existing `GetTypes()` guard; direct calls inside an MSTest host (downstream test pattern) no longer throw.

### Fixed

- **`LookupCacheWarmupService` now honors `CacheLookupAttribute.ConnectionKey` (#756).** Startup warmup resolved ONE default-connection DbContext and warmed every `[CacheLookup(WarmOnStartup=true)]` type against it — types mapped to non-default connections warm-failed every boot (log noise; BMS `Holiday_Orss` case). Worse, the lookup cache key has no connection component, so a default-DB warm that accidentally succeeded (same-named table in the default DB) cached wrong-database rows under the exact key the runtime `GetLookup` path serves, for the full TTL — a latent cache-poisoning/data-correctness hazard. Warmup now groups types by `ConnectionKey` and routes each group through `WTMContext.CreateDC(cskey:)`, exactly like the runtime `GetLookup`/`GetLookupAsync`/`RefreshLookupAsync` paths. Every failure path (unknown key, disabled connection, DI resolution throw, WTMContext-less host) degrades to a logged skip — nothing escapes `ExecuteAsync` (the .NET `BackgroundServiceExceptionBehavior.StopHost` hazard), including the pre-existing unguarded default-connection resolution. Downstream note: `WarmOnStartup = false` opt-outs added for this bug (e.g. BMS #295) can be removed after upgrading.
- **`ActionLogRetentionOptions.RunAtLocalHour` (and the new `RefreshTokenRetentionOptions`) clamp out-of-range hours instead of crashing the host (#762).** An out-of-range hour (the classic midnight=24 typo) threw `ArgumentOutOfRangeException` from `ComputeNextRun` outside the fault barrier: at startup this faulted `Host.StartAsync` (app fails to boot); after a live appsettings reload it escaped `ExecuteAsync` and stopped the production host (`StopHost`). Hours are now clamped to [0,23] with an operator `LogWarning` on misconfiguration.

### Known issues

- `FrameworkRefreshTokens` has no indexes on `Token`/`ExpiresUtc`/`RevokedUtc` — the #757 retention sweeps and hot-path token lookups table-scan on large backlogs (pattern-parity with ActionLog). Tracked in #761 (schema change; needs existing-DB migration guidance).

### Migration

- No breaking changes; both new features are opt-in and all fixes preserve default behaviour. Deployments that worked around #756 with `WarmOnStartup = false` on non-default-connection lookup types can remove the opt-out. If you enable `AddWtmRefreshTokenRetention()` on an **existing** production DB, confirm the `FrameworkRefreshTokens` table exists first (see the 10.15.0 Migration section, amended in #758) and consider #761's index guidance for large backlogs.

## [10.16.1] - 2026-07-19

Compatibility patch: restores v10.16.0's own "byte-identical when off" guarantee, which an independent aggregate code review (#753) found was violated for part of the #470 island-render work.

> **Rollback/bisect note:** because v10.15.0 and v10.16.0 ship the #753 regression (Slices G/H/I unconditionally islandified — flag-off output *not* byte-identical), downstream rollbacks or bisects should never land on those two versions: step directly between 10.14.5 and 10.16.1.

### Fixed

- **#470 Slices G/H/I now honor `WtmUIOptions.UseSelectIslandRender` (#753).** Seven emitters — `DateTimeTagHelper` (laydate ready/change/done + range), `SliderTagHelper` (ChangeFunc/OnTipsFunc), `ColorPicker` (ChangeFunc), `UEditorTagHelper`, `RichTextBox` (layedit), `TextAreaTagHelper` (counter), **and `TreeTagHelper`'s ItemUrl remote-lazy branch** — were migrated to the eval-free island / `data-wtm-*` path *unconditionally* in v10.16.0 (they predate the opt-in flag introduced in Slice J and were never retrofitted), so the default (flag-off) output was **not** byte-identical to before and silently shifted those widgets' render timing (parse-time → DOMContentLoaded). All seven are now gated behind `UseSelectIslandRender`: **flag-off restores the exact legacy inline `<script>` (byte-identical to pre-#470); flag-on keeps the island.** With the flag at its default `false`, v10.16.1 is genuinely zero-behaviour-change for the whole G–M family.
- **`WtmUIOptions.UseSelectIslandRender` is now read from a single, consistent source (#753).** TagHelpers previously read `WtmUIOptionsHolder` (a static snapshot bound directly from the `"UIOptions"` config section, bypassing the ASP.NET Core Options pipeline), while `LayuiUIService` read `IOptions<WtmUIOptions>` — so a code-based `services.Configure<WtmUIOptions>(...)` (the mechanism `WtmUIOptions`'s own doc recommends) was honored only by the latter, causing a split partial rollout. The holder is now populated from the DI-resolved `IOptions<WtmUIOptions>.Value`, so both `appsettings.json` binding and code-based `Configure` are honored everywhere.
- **Upload progress-bar reset placement (#753).** Under the opt-in island path, `ff._renderUploadAction`'s `done()` handler reset every `.layui-progress-bar` width to 0% on each successful non-preview upload; the legacy inline script only reset it inside the delete handler. Moved back into the delete handler to match legacy (prevents one upload widget's completion from visually zeroing another's bar).

## [10.16.0] - 2026-07-19

LayUI eval-retirement epic (#470) advances: the entire form-widget + button/grid-cell family gains **opt-in, eval-free island rendering** — every migration is **default-off and byte-identical to before** (`WtmUIOptions.UseSelectIslandRender = false` by default), so this release ships **zero behaviour change** to existing deployments while giving security-conscious apps a path toward a strict, `unsafe-inline`/`unsafe-eval`-free Content-Security-Policy for their forms and dialogs. Guiding principle (#567): BMS-stability-first, compat over novelty. `framework_layui.js` stays at exactly **one** active-code `eval(` (the deprecated `IsScript` path). Also fixes a health-check that never probed a real database (#741, a #727 residual) and includes several defense-in-depth XSS/encoding hardenings surfaced along the way.

### Security

- **Grid-cell / upload markup now built with safe DOM APIs instead of raw HTML string concatenation (#470 Slices K/L, #332-class).** When the opt-in island render is enabled, Transfer's hidden-input building and Upload/MultiUpload's existing-file markup + delete handling construct nodes via `createElement`/`textContent`/`setAttribute`/`ff._makeInput` rather than concatenating the ajax-returned **file display name** (genuinely user-influenceable) and ids into an HTML string — closing the same attribute-breakout class #332 already fixed elsewhere, with an XSS-shaped-filename regression test. Flag-off legacy paths are unchanged.
- **`TreeTagHelper` now `JavaScriptEncoder`-encodes `ItemUrl`/`TriggerUrl` in its inline scripts (#470 Slice J, #747).** These developer-authored attribute values were interpolated raw into JS string literals, unlike the sibling `ComboBoxTagHelper` which already encoded them. Defense-in-depth (values are compile-time Razor literals, not request data); brought to parity.
- **Two latent `ff.ChainChange` `TypeError`s hardened (#470 Slice J).** `ChainChange`'s clear + apply steps called `window[comboid].update(...)` with no existence guard (unlike `ff.LoadComboItems`, hardened in #633/#645); a not-yet-rendered widget in a mixed-migration state now degrades with a `console.warn` instead of an uncaught exception.

### Fixed

- **`WtmDataContextHealthCheck` (opt-in `AddWtmDataContextCheck`) previously always returned "Healthy (skipped)" in real deployments — it resolved WTM's `NullContext` DI placeholder instead of the app's real DataContext (#741, follow-up to #727).** It now resolves through the optional, DI-injected `WTMContext` first (`WTMContext.CreateDC()` — the same connection-string/tenant-aware factory every other part of WTM uses) and probes that real database with `Database.CanConnectAsync`, falling back to a directly DI-registered `IDataContext` only for hosts/tests that register one without also registering `WTMContext`. **Operator note:** a previously-always-green `/ready` readiness probe can now go **Unhealthy** when the underlying database is unreachable or the `default` connection is disabled — this is the intended fix, but multi-tenant apps whose `default` connection is intentionally disabled should scope or omit this opt-in check rather than relying on the prior (silently no-op) behaviour.

### Added

- **`WtmUIOptions.UseSelectIslandRender` (opt-in, default `false`) — eval-free island rendering for LayUI interactive widgets.** When enabled, ComboBox/Tree (`renderSelect`), Transfer (`renderTransfer`), Upload/MultiUpload (`upload`/`multiUpload`/`uploadExisting`), and the button/grid-cell wiring (`LayuiUIService.Make*`, `SubmitButton`) emit declarative JSON islands + `data-wtm-*` delegated handlers consumed by `ff.DispatchAction` — **no inline `<script>`, no `eval`, no per-widget global functions** — instead of the legacy inline scripts. Developer-authored callbacks (`ChangeFunc`, submit-click gates) are resolved through the existing `ff._resolveGuardedWindowFn` identifier guard when they are plain identifiers; a non-identifier callback keeps the exact legacy inline path plus a `console.warn` deprecation notice. Combined with the #627 kill-switch, this lets an app progress its dialog/form CSP toward `script-src 'self'`. **Default off = today's behaviour, byte-identical** — turn it on per app when ready to audit. (#470 Slices G–M)
  - **Slice G**: tree `item-url` → `loadComboItems` island; `ueditor`/`layedit` actions; textarea counter → `data-wtm-counter` delegation.
  - **Slice H**: `laydate` `ready`/`change`/`done` guarded-identifier callbacks + two-hidden-input range write-back island.
  - **Slice I**: `slider` (`ChangeFunc`/`OnTipsFunc`) + `colorpicker` (`ChangeFunc`) callback islands.
  - **Slice J** (the hard blocker): ComboBox/Tree `xmSelect.render` → `renderSelect` island (generic remote/template/data reconstruction, `window[id]` + `data-wtm-defaults` + cascading-chain + required-validation preserved). Gated opt-in because the full-page render-timing shift (parse-time → `DOMContentLoaded`) is a real, if narrow, behaviour change that must not be a silent default flip.
  - **Slice K**: `layui.transfer` → `renderTransfer` island.
  - **Slice L**: Upload/MultiUpload two inline scripts → `upload`/`multiUpload`/`uploadExisting` actions + a parameterized `ff.upload` namespace (eliminates the legacy per-widget global-function timing dependency) + delegated existing-file handlers.
  - **Slice M**: `LayuiUIService.Make*` grid-cell buttons + `SubmitButton` handshake → `data-wtm-click`/`ff._submitButtonClick(id)` delegated dispatch of the fixed framework actions (prerequisite for the grid slice).

### Known issues / gated

- **#655 (nested `<wt:selector>` sentinel re-arm)** closes only once combo/tree island rendering is the **default** in selector panels (Slice P sentinel-retirement), which requires flipping `UseSelectIslandRender` on by default — gated on BMS staging validation (the same gate as the #567 LayUI-modernization Phase 2). Tracked; not silently deferred.
- The full-page page-ready render-timing scope + a `wtm:comboRendered` migration event, plus a pre-existing `TreeTagHelper.ShowLine` dead property and a `SubmitButton` `f_Click`/random-id mismatch quirk, are tracked in **#747** for a future "Slice J2" / follow-up.

## [10.15.0] - 2026-07-18

The "final optimization" batch: 40+ issues across security, CI reliability, WorkFlow transaction correctness, performance (evidence-based, benchmarked), .NET 10 modernization, and dependency/packaging hygiene. Highlights below. **Security:** a JWT refresh auth-bypass (#721) and an ETL ReDoS/identifier-injection hardening pass (#680, #703). **Correctness:** four framework services (`IWorkflowEngine`, `WorkflowTimerExecutor`, `ActionLogRetentionService`, `LookupCacheWarmupService`) were silently non-functional in every real deployment due to an `IDataContext`→`NullContext` DI-resolution gap — all fixed (#721, #727); and all engine transactions now work under EF Core `EnableRetryOnFailure` (#667). Read the Migration section before upgrading.

### Security

- **[P0] Refresh-token identity-bypass — presented refresh token was never validated (#721).** `WTMContext.RefreshTokenAsync()` (called by the app-level `AccountController.RefreshToken(string refreshToken)` action shipped in the Demo/Vue3Demo/BlazorDemo `_Admin` area templates — the pattern real apps copy) ignored whatever refresh token the caller actually presented and reissued a brand-new access/refresh token pair purely from the current `LoginUserInfo` identity. Any caller holding a still-valid (even near-expiry) access token could mint fresh tokens indefinitely by POSTing to `api/_account/RefreshToken` with **any or no** refresh token at all — the entire `ITokenService` rotation/revocation/replay-guard machinery (`TokenChainSecurityTests`, `RefreshTokenAtomicRotationTests`, `TokenServiceIntegrationTests`) was dead on this HTTP path; those suites call `ITokenService` directly, below the route, which is why the bypass went unnoticed. Separately, the same route (`api/_account/refreshtoken`, matched case-insensitively) was **also** live on the framework's own hardened `_FrameworkController.RefreshToken` endpoint — two attribute-routed actions on one URL+verb, which throws `AmbiguousMatchException` (HTTP 500) at request time in any app that has both (verified by reproduction; every shipped demo does). Fixed with four changes:
  - `WTMContext.RefreshTokenAsync(string refreshToken)` (new overload) validates the **presented** token via `ITokenService.RefreshTokenAsync`, rejecting bogus/never-issued/expired/already-rotated tokens (returns `null`); for federation frontends (`ConfigInfo.HasMainHost == true` with no `CurrentTenant`, which have no local `RefreshTokenEntity` DB) it forwards the **real** presented token to the mainhost's hardened endpoint instead of an empty body. The old no-argument `RefreshTokenAsync()` is `[Obsolete]` and now unconditionally rejects (returns `null`) rather than reissuing from identity — kept only for source/binary compatibility.
  - `_FrameworkController.RefreshToken` (the single canonical endpoint after this fix) now delegates to `Wtm.RefreshTokenAsync(string)`, making it federation-aware and removing the duplicated forwarding logic.
  - The demo `AccountController.RefreshToken(string refreshToken)` action (Demo / Vue3Demo / BlazorDemo `_Admin` areas) — which both ignored its own parameter and route-collided with the framework endpoint — was removed in favor of the single framework endpoint. `WalkingTec.Mvvm.BlazorDemo`'s `App.razor` refresh call site was updated to POST the refresh token in the JSON body (`{"RefreshToken":"..."}`) instead of a query string, matching the framework endpoint's contract.
  - **Follow-up fix surfaced while adding HTTP-level regression coverage:** `TokenService`'s `RefreshTokenAsync`/`RevokeTokenAsync`/`CreateRefreshTokenAsync` resolved their `DbContext` via `scope.ServiceProvider.GetService<IDataContext>()`, but `AddWtmContext` only registers `services.TryAddScoped<IDataContext, NullContext>()` as a safe placeholder — real apps obtain their connection-string/tenant-routed `DataContext` through `WTMContext.DC` (built by `WTMContext.CreateDC()`), never through generic DI. This meant refresh-token persistence/validation was a **silent no-op over HTTP in every real deployment** (fail-closed, not a security hole, but the hardened mechanism never actually worked end-to-end outside of test fixtures that manually re-register `IDataContext`). `TokenService` now resolves a scoped `WTMContext` first and falls back to the DI-registered `IDataContext` only if that's unavailable (preserving existing test-fixture compatibility).
  - Added `RefreshTokenApiTests` (new, `WalkingTec.Mvvm.Api.Test`) — the first HTTP-level regression coverage for this endpoint: a bogus/never-issued token is rejected, an empty/absent token is rejected, a genuinely-issued token succeeds and rotates, replaying the rotated token is rejected, and the route resolves to exactly one action (no `AmbiguousMatchException`).
  - **Follow-up (review pass): a third, untouched copy of the identical bypass.** `IWtmAuthService.RefreshTokenAsync(LoginUserInfo, ITokenService, IWtmApiClient, bool)` (`WalkingTec.Mvvm.Core.Services.WtmAuthService`, DI-registered as a singleton in both `Core` and `Mvc` startup extensions) never validated any presented refresh token either — it called `tokenService.IssueTokenAsync(...)` purely from caller-supplied `LoginUserInfo` identity, and its federation branch forwarded an **empty body** to the mainhost's old (now-removed) `api/_account/RefreshToken` action, mirroring the exact pre-fix `WTMContext` pattern. Not currently wired to any HTTP endpoint (repo-wide grep found zero resolvers), so not an active exploit, but reachable to any future/third-party code resolving `IWtmAuthService` for exactly the purpose its XML doc advertised ("Refresh the current user's JWT token"), with no compiler warning. Fixed the same way as `WTMContext`: the old 4-arg overload is now `[Obsolete]` and unconditionally returns `null`; a new `RefreshTokenAsync(string? refreshToken, LoginUserInfo?, ITokenService, IWtmApiClient?, bool)` overload validates the **presented** token via `ITokenService.RefreshTokenAsync` (local host) or forwards the **real** token to the mainhost's hardened `api/_account/refreshtoken` endpoint (federation) — never identity-based reissue, never an empty body. `WtmAuthServiceTests` updated: the obsolete overload is now asserted to always reject (and never call `IssueTokenAsync`) even with a fully-populated user; new tests cover the secure overload (null/empty token rejected, local validation delegates to `ITokenService`, federation forwards the real token, bogus tokens rejected).
  - **Follow-up (review pass): `RefreshTokenApiTests`' bogus/empty/no-body assertions didn't actually prove the fix.** The original pre-fix vulnerable code path (the demo's `[AllRights]`-gated `AccountController.RefreshToken` action) was only reachable by a caller who **already holds a valid access token** — the bypass was "reissue from my own live identity while presenting garbage", not "refresh with zero credentials". The three MANDATORY regression tests POSTed with no `Authorization` header at all, so on pre-fix code they were rejected upstream by the auth filter for an unrelated reason (401, empty body, `WWW-Authenticate: Bearer` — an auth *challenge*) before ever reaching the vulnerable reissue logic; they passed on both pre-fix and post-fix code for different reasons and would not have caught a regression. Verified by reproduction against the pre-fix commit: attaching a genuine `Authorization: Bearer <access_token>` header (from a real `LoginJwt` call) and presenting a bogus refresh token to this exact route returned **HTTP 200 with a freshly minted, fully usable token pair** — the actual bypass. All three tests now authenticate first and attach the access token as a Bearer credential, reproducing the real attack shape; they additionally assert the specific rejection message and the absence of a `WWW-Authenticate` header, distinguishing a genuine business-logic rejection from an auth-filter bounce.

- **ETL staging column-name allowlist + MSSQL bracket-escaping + S3 handler hardening (#680).** Defense-in-depth for the ETL bulk loaders and the opt-in S3 file handler:
  - `MssqlBulkLoader` embeds column/table names in MERGE/INSERT/CREATE TABLE SQL using `[Name]`-style bracket quoting, but the quoter did not escape a literal `]` inside a name — an identifier containing `]` could prematurely close the bracket. `OracleBulkLoader` embeds column names **unquoted** by design (#499: consistent Oracle case-folding), which is only safe if the names are already restricted to a safe character set. Both loaders now depend on a new `EtlColumnNameValidator` allowlist (`^[\p{L}\p{N}_#$]+$` — Unicode letters/digits plus `_`, `#`, `$`) enforced once in `EtlPipelineExecutor`, on the actual extracted batch schema, **before** the first `BulkLoadAsync` call — column names originate from CSV/Excel headers or REST JSON keys when `EtlPipelineConfig.ColumnMappings` is not configured, so this is the first point a hostile/malformed name can be observed. The allowlist is Unicode-letter/digit-based rather than ASCII-only so CJK column headers (e.g. `订单编号`, `客户名称`) — routine in this framework's primary (Chinese) audience's CSV/Excel sources — are accepted; the security boundary is the *character category* (no SQL metacharacter, whitespace, or punctuation survives the allowlist), not the *script*. A non-conforming column now fails the run with a clear error instead of reaching either loader's SQL-building code. `MssqlBulkLoader.QuoteIdentifier` also independently escapes `]`→`]]` as belt-and-suspenders (table/schema names are admin-configured and not covered by the allowlist).
  - `MssqlBulkLoader.IsSafeWhereClause`'s `sp_`/`xp_` system-procedure guard used a plain substring match, false-positiving on legitimate column names like `resp_code`. It now matches only at a word boundary (`\b(xp_|sp_)`), preserving the same protection against real system-procedure invocations.
  - `WtmS3FileHandler` (opt-in `WalkingTec.Mvvm.FileHandlers.S3`): `Upload`/`GetFileData`/`DeleteFile` caught only `AmazonS3Exception`, letting the broader `AmazonServiceException` (throttling, HTTP timeouts, other service-level failures) escape unhandled — broadened to the base type. `GetFileData` now pre-sizes its buffer from the S3 response's Content-Length when known, avoiding `MemoryStream`'s internal buffer-doubling reallocations on large objects (still fully in-memory buffering — `IWtmFileHandler.GetFileData` must return a seekable stream per its existing MVC/import-VM callers, so unbounded streaming is out of scope for this fix). `BuildKey`'s subdir sanitizer allowed `.` and `/` characters through, so a `subdir` of `"../../secrets"` survived unchanged and could walk the resulting object key above `KeyPrefix`; whole `.`/`..` path segments are now dropped (a segment merely containing a dot, e.g. `"archive.2024"`, is unaffected). `Upload` now also sets `PutObjectRequest.ContentType` from the file extension instead of leaving it at the SDK's implicit default.

  **Migration:** a source column whose name does not match `^[\p{L}\p{N}_#$]+$` (Unicode letters, Unicode digits, `_`, `#`, `$`) now fails the ETL run at extraction time instead of being loaded. The impact is narrower than a plain ASCII allowlist: CJK and other non-ASCII letters/digits are accepted (no rename needed), and only genuinely hostile or whitespace/punctuation column names — a space, `]`, quote, `;`, `--`, emoji, etc. — are rejected. Rename a rejected column to a conforming target name via `EtlPipelineConfig.ColumnMappings` before it reaches the loader. No action needed for column sets that already conform (the vast majority of real-world schemas, including CJK ones). `WtmS3FileHandler.Upload`'s `subdir` parameter no longer preserves `../`/`..` path segments — callers relying on that (unsupported, undocumented) escape must restructure their key layout; legitimate segments containing a dot are unaffected.

- **ETL quality-rule regex now fails closed on match-timeout (ReDoS robustness, #703).** `EtlQualityRuleEvaluator` ran regex rules with a 1s `RegexMatchTimeout` but never caught `RegexMatchTimeoutException`, so it propagated out of the quality-rule path. A hostile/pathological **stored** regex rule (classic ReDoS, e.g. `^(a+)+$`) could abort an entire ETL run rather than failing to match a single row; under CI parallel load the same uncaught timeout also produced a benign-match flake. The evaluator now catches the timeout and treats it as a rejection (fail-closed — same path as a genuine non-match), so Drop/Continue/Abort semantics apply uniformly.

- **`RestEtlSource` next-link pagination: same-host restriction + finite `MaxPages` default (#661).** `RestEtlSourceConfig.Headers` (bearer tokens / API keys) are attached to **every** page request, including next-link follow-ups — but the next-link cursor was only scheme-validated (#376: rejects `https://`→`http://` downgrade), never host-checked. A hostile or compromised upstream could set the response's `next` field to an attacker-controlled host and have the crawl forward those same credentials there: a *semantic* redirect distinct from (and not covered by) the existing `AllowAutoRedirect=false` HTTP-level protection, since the pagination logic deliberately follows the JSON-embedded link rather than an HTTP redirect response. Compounding this, `MaxPages` defaulted to `0` (unlimited) with no cursor-cycle detection, so a pathological or hostile upstream could also drive an unbounded crawl. Fixed with three changes to `RestEtlSource`/`RestEtlSourceConfig`:
  - A next-link cursor must now match the configured endpoint's scheme+host+port, or the crawl stops immediately with a sanitized error (scheme/host/port only — never the full URL or header values). New opt-out `RestEtlSourceConfig.AllowCrossHostPagination` (default `false`) permits cross-host pagination for upstream APIs that are known and trusted to do this.
  - `RestEtlSourceConfig.MaxPages` now defaults to `1000` (previously `0`/unlimited) as a finite safety bound. The zero-means-unlimited semantics are unchanged — set `MaxPages=0` explicitly to keep unlimited paging.
  - A cycle guard tracks every next-link cursor URL already followed (seeded with the starting URL); a repeat stops the crawl with a counted error instead of looping.

### Fixed

- **WorkFlow engine transactions now work under EF Core `EnableRetryOnFailure`.** Every transactional unit in the engine (start/advance/approve/reject/return/withdraw/delegate/timer-fire/publish/audit-append) previously opened a manual `BeginTransactionAsync`, which EF Core rejects with `InvalidOperationException` when the host configured a retrying execution strategy (`options.EnableRetryOnFailure` — a commonly-recommended cloud SQL Server/PostgreSQL setting). All 23 transactional sites are now routed through `Db.Database.CreateExecutionStrategy().ExecuteAsync(...)`, making them legal under any host strategy. (#667)

- **[P0] WorkFlow engine (`IWorkflowEngine`) and the timeout reaper (`WorkflowTimerExecutor`) never functioned in any real deployment — audit of the #721 IDataContext→NullContext DI-resolution gap (#727).** `AddWtmContext` only ever registers `services.TryAddScoped<IDataContext, NullContext>()` as a safe placeholder default; apps obtain their real, connection-string/tenant-routed DataContext through `IWtmDataContextFactory`/`WTMContext.CreateDC()`, never through generic DI. `WorkflowEngine` and `WorkflowTimerExecutor` were registered with plain `services.AddScoped<T>()`, which let ASP.NET Core's automatic constructor injection resolve their `IDataContext` parameter straight off the DI container — i.e. `NullContext` in every real deployment, every time. Their constructors cast that parameter to `DbContext` (a documented invariant — `NullContext` doesn't extend `DbContext`), so the cast threw `InvalidCastException` (`"Unable to cast object of type 'NullContext' to type 'DbContext'"`) the very first time either type was constructed via a real DI container — meaning `IWorkflowEngine` was **entirely non-functional** (every `StartAsync`/`AdvanceAsync`/etc. call would 500) and `WorkflowTimerHostedService`'s startup-validation loop (which resolved `IDataContext` the same broken way for its `ValidateDbType` check) retried forever on a 5s/15s/60s backoff, logging warnings, never reaching `TickAsync` — the entire WF-20 timeout-reaper feature (Remind/AutoApprove/AutoReject/Escalate) silently never ran. This was masked because no prior test constructed either type through a real ASP.NET Core DI container: engine tests use the internal direct-`DbContext` test constructor, controller tests mock `IWorkflowEngine` entirely, and `MemoryGuardTests` never resolves `IWorkflowEngine` from its provider — the broken production registration itself was never exercised. `AddWtmWorkFlow`/`AddWtmWorkFlowTimers` now register both types via a factory that resolves the DataContext through `IWtmDataContextFactory` first (same mechanism `IProcessDefinitionPublisher`/`IWorkflowDefinitionStore` already used), falling back to DI `IDataContext` for host/test setups that register a real context directly — mirroring exactly how #721 fixed `TokenService`. `WorkflowTimerHostedService.RunStartupValidationAsync` was fixed the same way. Both engine types now implement `IDisposable` to own the DataContext instance created on their behalf. New `ProdDiReproTests` (WorkFlow.Test) build a DI container the way `demo/WalkingTec.Mvvm.Demo/Startup.cs` actually wires it and prove `IWorkflowEngine`/`WorkflowTimerExecutor` resolve without throwing and genuinely round-trip to a real SQLite database.
- **ActionLogRetentionService (`AddWtmActionLogRetention`, opt-in) silently deleted zero rows in every real deployment (#727).** Same root cause as above: the daily sweep resolved `scope.ServiceProvider.GetService<IDataContext>()` directly, which is `NullContext` in every real app — `NullContext.Set<ActionLog>()` throws `NotImplementedException`, swallowed by the per-iteration `catch (Exception ex)` in `ExecuteAsync`, logged only as a warning, retried (and failed identically) every day forever. Now resolves via `WTMContext.CreateDC()` first (the same pattern `WtmJob`/`EtlSchedulerService` already use for hosted-service DB access), falling back to DI `IDataContext`. New `ActionLogRetentionProdDiTests` prove the sweep now genuinely deletes rows via the WTMContext-primary path — the existing `ActionLogRetentionTests` suite could not have caught this because its fixture always registered a real `IDataContext` directly (masking the gap, the same way `TokenTestFixture` masked #721).
- **`LookupCacheWarmupService` silently skipped startup cache warm-up on every boot, for every WTM app (#727).** This hosted service is registered **unconditionally** by `AddWtmContext` (not opt-in). It resolved `scope.ServiceProvider.GetService<IDataContext>() as DbContext`; since `NullContext` does not extend `DbContext`, the cast silently produced `null` in every real deployment, and the service logged `"skipped: EF Core DbContext not available"` and returned — every `[CacheLookup(WarmOnStartup = true)]` type degraded (silently, by design of the surrounding no-op-then-lazy-fallback logic) to on-demand cache-miss population instead of the intended startup pre-warm, on every application boot. Now resolves via `WTMContext.CreateDC()` first, falling back to DI `IDataContext`. New `LookupCacheWarmupServiceProdDiTests` unit-test the resolution helper directly against both the WTMContext-primary and DI-fallback paths, and against the exact pre-#727 production shape (`NullContext` only), proving each path's expected outcome.
- **`<wt:selector>` picker dialog grid now loads (#722).** `ff.OpenDialog2` only rehydrated the calling page's `$$script$$` search-panel tokens, not the AJAX response body's own `<script>` block the way `ff.OpenDialog` does — so `Selector.cshtml`'s `submitSelect`/`gridCheckedFunc`/`table.render` init was unconditionally stripped by DOMPurify with no restoration path, and the picker dialog opened but its grid never fired `GetPagingData` (found live by #681 e2e). `ff.OpenDialog2` now extracts + dispatches the response body's own scripts through the **same** mechanism `ff.OpenDialog` uses — no new `eval(` (the file still carries exactly one, the deprecated `IsScript` path), the #627 kill-switch semantics preserved, DOMPurify allowlist intact. Guarded by a new Jest test.
- **`<wt:upload>` round-trip verified working (#723).** The upload widget's file-chooser→POST→hidden-ID-write-back round-trip was e2e-unreachable only because #681's `tc_35` used a `.txt` fixture the demo upload's `accept` filter rejected; replaced with a valid image fixture and `tc_35`'s soft known-gap is now hard end-to-end assertions (upload POST observed, hidden ID field populated). No framework change — an e2e-coverage fix.

### Removed

- **Retired the `demo/WalkingTec.Mvvm.VueDemo` (Vue 2) and `demo/WalkingTec.Mvvm.ReactDemo` (webpack 4) sample projects, and dropped the 42MB committed `demo/WalkingTec.Mvvm.BlazorDemo.zip` binary from history-going-forward.** (#679) Both demos were built on end-of-life toolchains (Vue 2 EOL Dec 2023; the React demo's webpack 4 build chain) and were the sole source of two long-standing "no upstream fix" Dependabot exceptions on the public GitHub mirror (`vue-template-compiler` CVE-2024-6783, `elliptic` CVE-2025-14505 — tracked in `docs/dependency-management.md` and mirror issue #610); both are now moot and should be un-dismissed/closed on the mirror via `/sync-github-security`. Removed their `Project(...)` entries from `WalkingTec.Mvvm.sln` and `ci.slnf`. The live `WalkingTec.Mvvm.BlazorDemo` project (source, not the stale zip) and `WalkingTec.Mvvm.Vue3Demo` are unaffected and remain in the tree. `UIEnum.VUE` codegen (`[Obsolete]` since 10.13.0) is **unaffected** — its templates live in `src/WalkingTec.Mvvm.Mvc/GeneratorFiles/Spa/Vue/` as embedded resources, not in the retired demo directory, so scaffolding VUE-targeted code continues to work exactly as before (deprecated, but not broken).

### Added

- **NuGet packages now ship SourceLink, symbol packages (snupkg), a package README, and deterministic CI builds (#678).** `common.props` gained `PublishRepositoryUrl`/`EmbedUntrackedSources`/`IncludeSymbols`+`SymbolPackageFormat=snupkg`/CI-gated `ContinuousIntegrationBuild`, `Microsoft.SourceLink.Gitea` (PrivateAssets=all), and `PackageReadmeFile` — step-into-source debugging and symbol-server support for consumers. Also modernized stale package metadata (copyright, release-notes pointer, the NuGet icon moved out of the demo tree so demo retirement can't break `pack`) and removed the blanket `NoWarn NU1608` (restore proven clean without it).

- **Opt-in periodic dead-letter flush restores crash-durability for `EnableDeadLetter` runs (#700, follow-up to #673).** #673 moved dead-letter capture from a per-batch flush to a single buffered flush-once-per-run, to enable run-scoped de-duplication on rerun — but a hard process crash mid-run (kill -9, host reboot, OOM) then lost ALL dead-letter diagnostics buffered for that run, since nothing had been written yet. New `EtlOptions.DeadLetterFlushMode` (default `OncePerRun`, byte-for-byte the existing #673 behaviour) can be set to `Periodic` to have `EtlPipelineExecutor` write the buffer to the governance store every `EtlOptions.DeadLetterFlushThreshold` entries (default 500) in addition to the final flush. This does **not** reintroduce #673's duplicate-on-rerun bug: partial flushes are written with the same `RunId` and `RunSucceeded = false` as the final flush, so the existing start-of-run `ClearDeadLetterFromFailedRunsAsync` cleanup (keyed on `JobId` + `RunSucceeded == false`, not on which flush call wrote a row) removes every partially-flushed row from an interrupted/failed run exactly as it already removed a fully-buffered failed run's rows; on success, `MarkDeadLetterRunSucceededAsync` promotes ALL of that run's rows — not just the final batch — to `RunSucceeded = true`, because it is keyed by `(JobId, RunId)`, not by flush-batch. Bounds the worst-case diagnostics loss on a crash to at most `DeadLetterFlushThreshold` not-yet-flushed entries instead of the whole run.

### Changed

- **ETL dead-letter capture on the opt-in `EnableDeadLetter` path now buffers and flushes once per run** (previously flushed per batch), enabling run-scoped de-duplication so a failed-then-rerun job no longer writes duplicate dead-letter rows. Capture now also covers bulk-load failures, transform exceptions, and the quality-rule Abort path (previously only the Drop path). (#673)
- **Deadlock/transient-failure handling is now uniform across all WorkFlow transactional paths.** Eight paths that previously threw the raw provider exception to the caller on a deadlock/transient failure (Start, advance-completion, sequential mid-chain, the #361 auto-approve loop, and All/Any/Sequential reject-completion) now retry and, on exhaustion, return `WorkflowActionCode.DeadlockRetryExhausted` — matching the six paths that already did so. SQLite `SQLITE_BUSY`/`SQLITE_LOCKED` are now classified as retryable transients. (#667)

### Known issues

- This change does **not** eliminate the SQLite-shared-memory test flake #629 (`SQLITE_ERROR: cannot start a transaction within a transaction`). That signature is a Microsoft.Data.Sqlite connection-wrapper state desync, not a retryable transient, and the SQLite test fixture uses a non-retrying strategy; #629 remains mitigated at the connection layer (busy_timeout). (#629, #667)

### Migration

- **Downstream apps pinning the Microsoft runtime/EF package group via Central Package Management must bump their pins to ≥ 10.0.9 before upgrading (#660).** 10.15.0 aligns the `Microsoft.EntityFrameworkCore.*` / `Microsoft.Extensions.*` package floors from 10.0.4 to 10.0.9. Under `CentralPackageTransitivePinningEnabled`, a downstream `Directory.Packages.props` still pinning any of these packages at 10.0.4–10.0.8 makes `dotnet restore` fail hard with `NU1109` (an error, not a warning) as soon as it resolves the 10.15.0 packages. Bump those pins first — nothing else in the upgrade is reachable until restore passes. *(Added retroactively from downstream field testing — #758.)*
- **Refresh now requires a valid, previously-issued refresh token (#721).** Clients that relied on the old identity-based reissue behavior — calling refresh with a valid Bearer access token but no refresh token, or a stale/bogus one — will now get `401 Unauthorized`. Callers must present the actual `refresh_token` returned at login (`POST api/_account/refreshtoken` with JSON body `{"RefreshToken":"<token>"}`); federation frontends forward it to the mainhost automatically, no client change needed there. Apps that copied the demo's `_Admin/AccountController.RefreshToken(string refreshToken)` action into their own codebase should delete it — it now duplicates (and, prior to this fix, insecurely shadowed) the framework's `_FrameworkController.RefreshToken` endpoint at the same route and would throw `AmbiguousMatchException` if both are present. **Before deleting the copied action, check the attributes it carries.** The template action is decorated with `[WtmRateLimit(100, 60)]`, and `AddWtmRateLimiting` registers named rate-limiter policies only for the `(permit, window, queue)` tuples it discovers on scanned `[WtmRateLimit]` attributes — if the copied action is the only place in your app carrying a given tuple, deleting it silently deregisters the corresponding named policy (`wtm_rl_100_60_0`), and any minimal-API endpoint that references the policy via `.RequireRateLimiting(WtmRateLimitAttribute.BuildPolicyName(...))` (health-check endpoints are the typical case) starts failing **at runtime** with an `InvalidOperationException` on every request — build and unit tests stay green. Re-home the attribute on another endpoint that should keep that limit, or register the tuple explicitly once the explicit-registration API lands (#759). *(Added retroactively from downstream field testing — #758.)*
- **Existing databases must have the `FrameworkRefreshTokens` table before upgrading — a missing table breaks every login (#721).** The DI fix that revived `ITokenService` also makes refresh-token persistence live for the first time: every successful login now INSERTs a row into `FrameworkRefreshTokens` (`IssueTokenAsync` → `CreateRefreshTokenAsync`), and the write is not swallowed — a failed INSERT fails the login. Databases originally created by an older WTM via `EnsureCreated()` never got this table (`EnsureCreated` does not add new tables to an existing database), so after upgrading, cookie logins fail with `Sys.Error` and `Login`/`LoginJwt` API calls return 500 — on every login attempt. Fresh databases create the table automatically, which is exactly why staging/e2e environments rebuilt from scratch will not surface this; verify your long-lived production DB explicitly. Create the table before rolling out — via your normal EF Core migration flow, or with idempotent DDL matching `RefreshTokenEntity` (provider-appropriate types): `ID` GUID **PK**, `Token` string(256) NOT NULL, `ITCode` string(50) NOT NULL, `TenantCode` string(50) NULL, `ExpiresUtc` datetime NOT NULL, `CreatedUtc` datetime NOT NULL, `CreatedByIp` string(50) NULL, `RevokedUtc` datetime NULL, `RevokedByIp` string(50) NULL, `ReplacedByToken` string(256) NULL, `RevokeReason` string(100) NULL. *(Added retroactively from downstream field testing — #758.)*
- **`RestEtlSource` (ETL) configs using `NextLink` pagination across multiple hosts** must now set `AllowCrossHostPagination=true` explicitly in `RestEtlSourceConfig` — cross-host next-links are rejected by default.
- **`RestEtlSource` (ETL) configs relying on crawls longer than 1000 pages** (the previous default was unlimited) must now set `MaxPages=0` explicitly to restore unlimited paging. Configs that already set `MaxPages` to a nonzero value, or that never exceed 1000 pages, are unaffected.
- **If you enabled `EnableDeadLetter`** (available since 10.5.1): dead-letter rows are now persisted once at run completion rather than incrementally per batch, and are bounded by the new `EtlOptions.MaxDeadLetterRowsPerRun` (default 10000; excess rows dropped with a truncation marker). By default, a hard process crash mid-run still loses all dead-letter diagnostics buffered for that run — set `EtlOptions.DeadLetterFlushMode = EtlDeadLetterFlushMode.Periodic` (new in #700, see the Added section above) to opt into bounded crash-durability instead. No action needed for the default `EnableDeadLetter=false`, and no action needed to keep the exact pre-#700 flush-once-per-run behaviour (`DeadLetterFlushMode` defaults to `OncePerRun`).
- Callers that previously wrapped `StartAsync`/`ApproveTaskAsync`/`RejectTaskAsync` etc. in try/catch to handle a deadlock/transient provider exception should instead check `result.Code == WorkflowActionCode.DeadlockRetryExhausted` (the other engine paths already used this pattern; this makes it uniform). No change needed for callers already checking result codes. (#667)

## [10.14.5] - 2026-07-11

Closes the tracked follow-up to #651: the same `<wt:selector>` dialog sentinel-collision could also be forged through **server-rendered plaintext** (not just JSON island / inline-script bodies), because `WebUtility.HtmlEncode` does not escape `$`. Fixed once at the tokenization chokepoint rather than per field-widget. No migration required — behaviour-preserving for all existing content on both the default and #627-kill-switch paths.

### Security

- **Stored XSS via `$`-sentinel collision in server-rendered selector-panel plaintext (#652).** `SelectorTagHelper` tokenizes its search-panel content into `$$script$$`/`$$dialoginit$$` string sentinels that `ff.OpenDialog2` restores with a **global** replace. Every field TagHelper that can render inside a `<wt:selector>` panel — `<wt:radio>`/`<wt:checkbox>` option labels & values, `<wt:tree>` nodes, `<wt:taginput>` tags, slider/rate/upload/hidden values — HtmlEncodes its model-derived plaintext, which escapes `<>&"'` but **not `$`**. So a stored value containing the literal text `$$script$$…$$#script$$` survived into the template unchanged and was rehydrated into a live `<script>` when the selector dialog opened (default, kill-switch-off path). This predated the island work; #651 closed only the JSON-body sub-case. Fixed at the single tokenization chokepoint: `SelectorTagHelper` now escapes **every** literal `$` in the panel content to a Private-Use placeholder (U+E000) **before** tokenizing real tags, so the only `$` sequences reaching the client are the framework-placed sentinels; `ff.OpenDialog2` restores the placeholder to `$` **after** un-tokenizing — and because that restore is the *last* transform at a selector level, a forged `$$script$$…` re-formed by it stays inert text, never executed, while real jQuery `$` in developer scripts round-trips exactly. One chokepoint covers all current and future field widgets. The `$$SearchPanel$$` template insertion was hardened to a replacer function so the restored `$` cannot be re-mangled by `String.prototype.replace`'s `$$`/`$&` special-casing (a pre-existing latent lossy-round-trip bug — e.g. a `Save $$10$$` label). Also neutralizes the sibling `$$dialoginit$$` island-forgery vector by the same escape. Guarded by C# and JS regression tests, including a JS mutation-verify proving the pre-fix shape executes and a lossless-round-trip test for `$`-heavy labels.

### Known issues

- **Nested `<wt:selector>` inside another selector's `<wt:searchpanel>` (#655, kill-switch-mitigated).** The #652 escape/restore is composition-safe for a single selector level (the real-world reachable case). When one selector is nested inside another selector's *search panel* — an unusual composition used by no demo — the outer dialog's global restore reverses the inner selector's escape, re-arming a sentinel the inner selector's own dialog then executes. The string-sentinel scheme cannot be made composition-safe against arbitrary nesting depth (that is what the #470/#627 retirement resolves). The **#627 kill-switch** (`ff.DisableLegacyScriptRehydration` / the `wtm-disable-legacy-script-rehydration` meta) strips those segments at every level and fully neutralizes this today; a kill-switch-ON nested regression test guards the mitigation. Tracked in #655.

### Migration

- None. Model-derived `$` is escaped and restored transparently; existing content renders byte-identically. Applies on both the default and #627-kill-switch paths.

---

## [10.14.4] - 2026-07-11

Continues the #470 CSP-hardening epic: the `<wt:selector>` search-panel dialog path gains the JSON-island machinery it was missing, and two field-widget concerns move toward islands / markup. Also fixes a defect shipped in 10.14.3, a pre-existing chained-control bug, and — caught by the pre-release cross-vendor review before any tag — a stored-XSS class in the selector-panel dialog tokenization and three timing regressions. The buggy intermediate states never reached a release. All changes are behaviour-preserving unless an app has opted into the #627 kill-switch; no migration required.

### Security

- **Stored XSS via sentinel-collision in the `<wt:selector>` dialog tokenization (#651).** `ff.OpenDialog2` restores the tokenized search-panel template with **global** string replaces (`$$dialoginit$$`/`$$#dialoginit$$`/`$$script$$`/`$$#script$$` → `<script>`/`</script>`), and this release (#635) made **every** `wtm-dialog-init` island dispatch on that path. Island JSON bodies and inline `<script>` field writes carry model-derived data serialized with `System.Text.Json`, whose default encoder escapes `<>&` but **not `$`** — so a stored value containing `$$#dialoginit$$$$script$$…$$#script$$` (e.g. a combobox `selectVal`, a `<wt:taginput>` tag, a tree node label) could break out and inject an executable `<script>` when a selector dialog opened (default, kill-switch-off path). Fixed by routing **every** serialization that can land in the tokenized `#Temp{Id}` template through a shared `LayuiIslandJson.Serialize` that escapes `$`→`$` (transparent — `JSON.parse` / the JS string-literal parser decode it back; `framework_layui.js` is unchanged), guarded by a source-sweep test so no future emitter can silently reintroduce it. **Known remaining item (#652, tracked):** the same sentinel scheme can also be forged by literal `$$script$$` in server-rendered *plaintext* labels/values (HtmlEncode does not escape `$`); this predates the island work and will be addressed by reworking the tokenization scheme.

### Fixed

- **`Layui:Asset` kill-switch could corrupt `<wt:selector>` dialogs in 10.14.3 (#636).** With the #627 kill-switch enabled **and** a search panel containing an island-emitting field, the island's `</script>` was blanket-tokenized while its attribute-bearing open tag was not — an orphaned token the kill-switch's pair-matching strip regex could not remove left an **unclosed `<script>` that silently swallowed the rest of the dialog markup**. `SelectorTagHelper` now tokenizes each island's open+close tags as one matched pair (`$$dialoginit$$`), restored unconditionally by `ff.OpenDialog2`. Apps that never enabled the kill-switch were unaffected. Fixed as part of #635.
- **Chained `<wt:radio>`/`<wt:checkbox>` targets rendered blank on edit pages (#638, pre-existing).** `ff.ChainChange`'s checkbox/radio branches called `.indexOf(...)` on `window[id+'defaultvalues']` without the null-guard the combobox/tree branches have; a chained radio *target* never published the global, so on an Edit page with a preselected source the linked group threw and rendered empty. Both branches are now guarded, and `RadioTagHelper` publishes its defaults once, unconditionally.

### Changed

- **`<wt:selector>` search-panel dialogs now dispatch JSON islands (#635, #470 slice 0).** `ff.OpenDialog2` previously had no island machinery, so any islandified widget inside a selector search panel was inert on that path. It now collects and dispatches `wtm-dialog-init` islands from the opened layer (`ff.ConsumeIslandsIn`), preserving the legacy-scripts-before-islands ordering and the #627 kill-switch semantics. Prerequisite for islandifying widgets used in selector panels.
- **`item-url` combobox/checkbox/radio/transfer emit a JSON island instead of an inline `ff.LoadComboItems(...)` script (#633, #470 slice).** Rides the existing `loadComboItems` dispatch action across all four control types. **A combobox that is both `item-url`-populated and a chain target now yields deterministically to its chain result (#645):** `ff.ChainChange` marks the target and `ff.LoadComboItems` skips a stale/racing apply, so on an Edit page the filtered (chain) selection wins regardless of async completion order. Under the kill-switch, a widget whose data-loading half is islandified but whose render half is still a legacy inline script (combobox `xmSelect.render`, transfer `transfer.render`) emits one actionable `console.warn` naming the widget instead of throwing — see `docs/csp-hardening.md`. Also fixes a latent gap where `TransferTagHelper` interpolated `item-url` without `JavaScriptEncoder`.
- **checkbox/radio default-selection is now also exposed as a `data-wtm-defaults` markup attribute (#632, #470 slice).** `ff.ChainChange` reads defaults from this attribute — available the instant the parser reaches the node — removing a dispatch-timing race an island-only approach would have introduced. The legacy `window[id+'defaultvalues']` global remains published **inline at parse time, and only there** (#646, #649), so its synchronous visibility is byte-identical to earlier releases and there is a single publisher (an earlier draft that also re-published via a page-ready island was dropped because it clobbered app mutations to the global). Because the inline write is retained, checkbox/radio are *not* CSP-clean by default; under strict CSP the global is absent and app code should read the `data-wtm-defaults` attribute. Behaviour is byte-identical for apps that do not use chaining.

### Improved

- **The `item-url` data-load no longer requires `'unsafe-inline'` script on the dialog/selector paths.** Remaining inline-script widget configurations — combobox/tree `xmSelect.render`, transfer/ueditor render, datetime callback/range, callback slider/colorpicker, grids, and checkbox/radio `defaultvalues` — are tracked as #470 hard blockers; `docs/csp-hardening.md` documents the current eligibility for the strict-CSP recipe.
- **CI reliability (#640).** `ci-build.yml` fired on both `push: [..., feat/**]` and `pull_request: [dotnet10]`, running the full test suite twice concurrently on one runner per feature-branch push — the likely root cause of the long-standing "flaky SQLite concurrency test" family (#620, #629). The duplicate push trigger was removed. No package impact.

### Migration

- None. Every change is behaviour-preserving for apps on the default configuration. Apps that enabled the #627 kill-switch should re-read `docs/csp-hardening.md` — the selector-panel path now dispatches islands, and half-islandified widgets degrade with a diagnostic warning rather than silently.

---

## [10.14.3] - 2026-07-10

A slice of the #470 eval-retirement epic (#627): an **opt-in kill-switch** that disables the four legacy dynamic-script-execution points in `framework_layui.js`, plus a documented graduated CSP-hardening recipe. Default OFF — zero behaviour change unless an app opts in.

### Added

- **Legacy script-rehydration kill-switch (#627, opt-in).** Disables the framework's legacy dynamic-script-execution surface for AJAX-loaded content — either markup-only via `<meta name="wtm-disable-legacy-script-rehydration" content="true">` (usable under the strictest CSP) or programmatically via `ff.DisableLegacyScriptRehydration = true` (strict boolean check). When enabled, all **four** legacy paths block with loud diagnostics instead of executing: `ff._legacyScriptEval` (the deprecated `IsScript` fallback and the file's single `eval(`) logs a `console.error` migration pointer; the `ff.OpenDialog` (#522) and `ff._replayInitFromHtml` (#587) inline-script re-injection loops and the `ff.OpenDialog2` Selector search-panel `$$script$$` rehydration (#332) skip execution and emit a counted `console.warn` so stragglers are discoverable in staging. Script *extraction* (part of the DOMPurify markup-sanitization pipeline) and JSON-island dispatch are unchanged in both modes. The flag is read live on every call (SPA shells/tests may toggle it).
  **Eligibility caveat (read before enabling):** many framework TagHelper configurations still emit executable inline `<script>` at this release — non-exhaustively: `<wt:combobox>`/`<wt:tree>` (`xmSelect.render`, `item-url`), `<wt:transfer>`, `<wt:ueditor>`, `<wt:upload>`/`<wt:multiupload>`, `<wt:checkbox>`/`<wt:radio>` default-value globals, grids inside AJAX-loaded partials, `<wt:selector>` search panels, `<wt:datetime>` callback **and range** branches, callback `<wt:slider>`/`<wt:colorpicker>`, and `<wt:form>` with a non-identifier `BeforeSubmit` (#470 hard-blocker territory). Dialogs/fragments containing them **will break (loudly) with the switch on** — the precondition is not just "no hand-written inline scripts" but a clean level-1 audit per `docs/csp-hardening.md`, whose authoritative check is the staging flip, not the widget list. For most CRUD apps the switch is currently a *staging audit tool*; production enablement becomes broadly viable once the widget islandification lands under #470.
- **`docs/csp-hardening.md`** — the graduated recipe: level 0 (shipped default, already `unsafe-eval`-free), level 1 (auditing that AJAX-loaded content is script-free — both hand-written scripts and the still-script-based framework widgets), level 2 (flipping the switch in production, one-line rollback), level 3 (tightening `WtmCspOptions.ScriptSrc` toward `'self'`, with honest notes on full-page widget scripts, layout-bootstrap externalization, static-hash limits for per-request Razor values, and why hand-rolled static nonces are not an option — nonce support tracked under #807).

### Migration

- No action required. The switch is opt-in; without it, behaviour is byte-identical (verified by the pre-existing 1570-test Jest suite passing unchanged). To adopt, follow the level 1 audit in `docs/csp-hardening.md` — including the framework-widget eligibility caveat above — before enabling in production.

---

## [10.14.2] - 2026-07-06

Phase-4a of the #567 LayUI roadmap: formally **deprecate** `Layui:Asset=legacy` and the bundled layui 2.6.3 asset tree, opening the removal window. Nothing is removed — this is advance notice only. The conservative path was chosen at the explicit request of the stability-sensitive downstream (BMS), whose production is still on WTM 8.x and which keeps `legacy` as a one-line rollback safety net for its eventual 8→10 cutover.

### Deprecated

- **`Layui:Asset=legacy` and the bundled layui 2.6.3 (`/layui`) tree are deprecated (#567 Phase-4a).** They remain **fully functional** — `Layui:Asset=legacy` still selects `/layui` exactly as before, and both trees stay vendored. Removal (the `legacy` branch in `LayuiAssets.ResolveLayuiBase`, the vendored 2.6.3 tree, and the `layui-263` arm of the #565 regression suite) is planned for the **next major version**, gated on downstream production migration off `legacy` (tracked as BMS#242). A `<remarks>` deprecation notice was added to `LayuiAssets` so package consumers see it in source/IntelliSense.
- **Package-side removal is verified clean:** an audit confirmed the WTM NuGet packages contain **zero** hardcoded `/layui/` paths — `framework_layui.js` and every embedded resource are clean, and the WorkFlow `designer.html` uses the `%%WTM_LAYUI_BASE%%` token (#614). The only package path to the 2.6.3 tree is the config-controlled `ResolveLayuiBase` `legacy` branch, so the eventual removal will strand nothing that a downstream consumes through the package.
- **`wt:richtextbox` is not a removal blocker:** its 2.6.3 `layedit` is already self-contained in the `layui-next` tree (`layui-next/layedit.js` + face images + CSS, wired via `layui.extend`, #573), so it keeps working on 2.13.8 independently of the 2.6.3 tree.

### Migration

- No action required. To use `Layui:Asset=legacy` you already had to opt in; it continues to work. When removal lands (a future major, announced separately), migrate to the default (`/layui-next`, 2.13.8) — for `wt:richtextbox`, vendor the three `layedit` files beside your `layui-next` tree as the demo `_Layout.cshtml` does.

---

## [10.14.1] - 2026-07-06

Completes the #573/#567 layui flip: the seven framework-embedded tool-UI shells that 10.14.0 left hardcoded to `/layui` (2.6.3) now follow the `Layui:Asset` switch like every other page. Phase-3/4 prep (#614) for the eventual 2.6.3 tree removal. Non-breaking — the `Layui:Asset=legacy` pin restores 2.6.3 for these views exactly as for the app pages.

### Changed

- **Framework-embedded tool views now honour `Layui:Asset` (#614):** `_CodeGen/{Index,Gen,SetField}.cshtml`, `_DashboardPage/{Index,Designer,Render}.cshtml`, and the WorkFlow `designer.html` (streamed by `WorkflowDesignerPageController`) previously hardcoded `/layui` (2.6.3), so 10.14.0's default flip did not reach them. They now resolve their layui base through a single new helper, `WalkingTec.Mvvm.Mvc.LayuiAssets.ResolveLayuiBase(IConfiguration)` — the one source of truth for the "select between two fixed literals, never concatenate config into a URL" invariant (`Layui:Asset == "legacy"` → `/layui`, else → `/layui-next`). The 6 Razor views `@inject IConfiguration`; `designer.html` carries a `%%WTM_LAYUI_BASE%%` token substituted server-side with the helper's fixed-literal output. **Behaviour change**: on upgrade, a host that has *not* set `Layui:Asset=legacy` (i.e. anyone on the 10.14.0 default) now serves 2.13.8 assets to these tool pages instead of 2.6.3. All seven were real-browser-certified on **both** trees — CodeGen wizard, Dashboard designer/render, and the WorkFlow designer render cleanly on 2.13.8 with zero console/page errors and correct token substitution (no per-view gating needed). Regression tests added: `LayuiAssetsTests` (23 cases incl. hostile inputs — result is always one of the two literals) and `WorkflowDesignerPageControllerLayuiTokenTests` (4 cases — action-level, no `%%WTM_LAYUI_BASE%%` token leaks in either config branch).

### Migration

- No action required for hosts on 2.13.8. To keep these tool pages (and everything else) on 2.6.3, set `Layui:Asset=legacy` — the same one-line pin documented for 10.14.0; it now covers the tool views too.

---

## [10.14.0] - 2026-07-06

Default bundled layui flipped 2.6.3 → 2.13.8 (#573, Phase-2b of the #567 roadmap). Minor version bump per the default-behaviour-change policy; both asset trees remain vendored and served, and a one-line config pin restores the previous default. Gate history: #565 dual-tree suite green on both trees (now 15/15 including the new richtextbox section), BMS staging sign-off on `Layui:Asset=next` (620/620 real-browser e2e against WTM 10.13.17), and the two real 2.13.8 regressions that gate surfaced (#594) fixed in 10.13.17; a third gate catch (#615, SPA tab-switch race) is fixed in this release.

### Changed

- **Demo shells now select the layui-next (2.13.8) tree by default (#573):** `_Layout.cshtml` and `Login.cshtml` — the only two `Layui:Asset` consumers — flip their default from `/layui` (2.6.3) to `/layui-next` (2.13.8). New semantics: `Layui:Asset` == `legacy` (exact match only) pins back to the vendored 2.6.3 tree; any other value — **including the former opt-in value `next`, which stays valid** — or absent config selects 2.13.8. The raw config value is still never concatenated into a URL; it only selects between two fixed literals. Framework-embedded tool views (`_CodeGen`, `_DashboardPage`, WorkFlow designer) intentionally keep loading `/layui` (2.6.3) in this release — they were untested on 2.13.8 at the time; both trees stay served, so nothing breaks. (Completed in **10.14.1**, #614 — they now follow `Layui:Asset` too, certified on 2.13.8.)
- **`wt:richtextbox` kept working on the new default via layedit vendoring (#573 blocker G):** layui removed the `layedit` module upstream in 2.8, so on the 2.13.8 tree `layui.use('layedit', …)` (emitted by `RichTextBoxTagHelper`) would silently never fire and the editor degraded to a hidden textarea. The 2.6.3 `layedit.js`, its face-panel images, and its extracted CSS rules are now vendored beside the next tree (`layui-next/layedit.js`, `layui-next/images/face/`, `layui-next/css/layedit.css`), and `_Layout.cshtml` registers `layui.extend({ layedit: '{/}/layui-next/layedit' })` when serving the next tree. Harness section 15 (new) proves `layedit.build()` + a functional content round-trip on **both** trees; a pre-existing upstream defect in `layedit.setContent()` (bare `layedit.sync()` instead of `this.sync()`, present in 2.6.3 itself) is documented in the harness — production code never calls it.

### Fixed

- **SPA tab switch no longer clobbered by a stale home-render callback (#615, found by this release's flip gate):** layuiadmin `index.js`'s view-render `.then()`/`.done()` applied `tabChange`/`tabsBodyChange` with the route captured at render start, so a slow earlier render (the home `/` render kicked off at login) resolving after a faster navigation switched the UI back to home — deterministic on layui 2.13.8 (e2e TC-04), and the probable root cause of the #596 CI flakes on 2.6.3 (where the window only opened under load). Both callbacks now bail when the current route no longer matches; the guard deliberately sits **after** the `this.container` reassignment (view.js's `parse()` targets it — an earlier return made the stale render wipe the whole tab body, a worse bug). All five demo variants patched identically; 11 new Jest tests drive the real vendored layui + view.js + admin.js + index.js on both trees with controlled ajax timing. Downstreams that copied the layuiadmin scaffolding should port the same guard (mind the placement note).

### Migration

- **To stay on layui 2.6.3, set `Layui:Asset` to `legacy`** (configuration key, e.g. `"Layui": { "Asset": "legacy" }` in appsettings.json or the `Layui__Asset` environment variable). This is the complete rollback path — both trees ship, nothing is removed.
- Deployments that already set `Layui:Asset=next` (the 10.13.x opt-in) need no change — the value remains valid and selects the same tree as the new default.
- Apps with their **own** layout shells are unaffected by this release: the asset choice lives in app-side views, the framework never picks layui assets. To adopt 2.13.8, port the demo `_Layout.cshtml`/`Login.cshtml` switch pattern — and first port the two #594 template patterns (laytpl `{{ }}` escaping and `router().path` leading-empty-element normalization, see 10.13.17) if you copied the layuiadmin scaffolding.
- Apps using `wt:richtextbox` on the 2.13.8 tree must also copy the three vendored layedit files from the demo tree and register the `layui.extend` line after `layui.config` (see `_Layout.cshtml`); alternatively keep those pages on `legacy`.

---

## [10.13.17] - 2026-07-05

Dialog-attribute restoration + layui-next flip prerequisites. Fixes `ff.SafeHtml` silently stripping WTM's own framework attributes from every sanitized dialog partial, and ports the two layui-2.13.8 demo-scaffolding regressions (found by downstream BMS staging canary, fixed in their fork) to WTM — clearing the WTM-side blockers on the #573 default-flip checklist.

### Fixed

- **`ff.SafeHtml` no longer strips WTM's custom framework attributes (#591):** DOMPurify's internal default allowlist only recognizes standard HTML5 attributes, so every non-standard attribute WTM's own TagHelpers emit — `lay-filter`, `lay-skin`, `lay-verify`/`lay-vertype`/`lay-reqtext`, `wtm-name`/`wtm-ctype`/`wtm-multi`, `div-for`, and five non-prefixed ones (`subpro`, `issearchbutton`, `oldpost`, `ischart`, `chartlink`) caught by the adversarial review round — was silently removed from dialog partials and PostForm redraws (scoped `form.render` filters, switch skins, validation rules all degraded). The audited inventory is now an explicit `ADD_ATTR` allowlist (per-attribute emission/consumption sites documented in the PR); name-resolved event-binding attributes (`lay-on`, `lay-event`) remain deliberately stripped. 47 dedicated Jest tests incl. attribute-value smuggle inertness. Follow-ups that `ADD_ATTR` structurally cannot fix are tracked in #601.
- **Demo/layuiadmin: two real layui 2.6.3→2.13.8 regressions fixed dual-tree-compatibly (#594, #573 flip prerequisites):** (1) laytpl `{{ }}` HTML-escapes since layui 2.8, so menu templates composing whole attributes inside interpolations (`{{ … ? '' : 'lay-href="'+url+'"' }}`) emitted literally-quoted attribute values on 2.13.8 → SPA menu navigation broke; conditional attributes now branch the opening tag via `{{# if }}` blocks with quotes kept static (menu `Layout.cshtml` + `theme.html` across all demo scaffolds; full anti-pattern sweep documented in the PR). (2) 2.13.8's `router().path` carries a leading empty element, turning SPA tab URLs into protocol-relative `//host` forms (`ERR_NAME_NOT_RESOLVED`); every layuiadmin consumption site now normalizes (`""===path[0]&&path.shift()`) — a 2.6.3 no-op, with no global `layui.router` monkey-patch. 43 new Jest tests load the REAL vendored laytpl/router engines from both trees and assert output parity.

### Improved

- **e2e TC-04 de-flaked with diagnosable timing (#596):** the Analysis-Mode test now waits for grid rows before the toolbar-button visibility wait (45s dedicated timeout) and prints grid-render/button-visible timings — the very logs that later proved two red runs were concurrent-e2e resource starvation, not code regressions.
- **AGENTS.md added (#597):** cross-agent repo entry point mirroring CLAUDE.md, pointing at the shared `.claude/rules/`.

### Migration

- No action required. #591 only preserves attributes that WTM's own server-side TagHelpers emit (values still flow through DOMPurify sanitization); #594 changes demo scaffolding templates only — downstream apps that copied the old layuiadmin templates should port the same two patterns before opting into `Layui:Asset=next`. Known follow-up: #601 (TextBox inline-handler island migration, CodeTagHelper dead `encode` attribute, `<hidden>` element).

---

## [10.13.16] - 2026-07-05

Island-pipeline gap closure + native TagInput hardening bundle. Fixes a v10.13.14 rendering regression, gives the #571 native TagInput the same containment gate as its sibling widgets, and closes the two island-consumption gaps that left SPA-fragment and validation-redraw paths dead (found by downstream BMS staging canary against the #573 flip gate). All changes keep the eval-free island architecture (#470); active `eval(` count remains 1.

### Security

- **#578 form-containment gate extended to the native TagInput (#585):** the #571 native tagInput shipped one PR after #578 added the id-spoofing write-back gate to slider/rate/colorpicker — and never received it. `_renderTagInputAction` resolved its hidden input page-wide and cleared an arbitrary `opts.elem` subtree, so a smuggled island (the #462/#552 threat model) could wipe any container and bind writes to any hidden input. `TagInputTagHelper` now emits `FormId` from the ambient `context.Items["formid"]` and every write-back/container operation is gated on `formEl.contains(...)`, mirroring #578 exactly; absent `formId` keeps back-compat behaviour. Jest containment suite mirrors the #578 tests (cross-form blocked, same-form works, back-compat unguarded).

### Added

- **`ff.ConsumeIslandsIn(rootEl)` — public scoped island consumer (#587):** the `wtm-dialog-init` island pipeline had exactly two consumption entry points (main-document `DOMContentLoaded`, `ff.OpenDialog` pre-collection), so islands inserted by any other DOM path stayed inert — reproduced downstream on layuiadmin SPA-tab fragment loads (laydate panels dead since 10.13.12, form init/submit/validate islands dead since 10.13.13). The new API scans a **subtree only** (`script[type="application/json"].wtm-dialog-init:not([data-wtm-dispatched])`), claims each island before dispatch, and routes through `ff._dispatchIslandWhenReady`. Deliberately scoped — a document-wide rescan would double-dispatch islands left unmarked by downstream `SafeHtml` shims (double `bindSubmit` = duplicate form submission). Demo layuiadmin `lib/view.js` (all five scaffolds) now calls it after ajax fragment insertion.

### Fixed

- **Rate island selector double-escape — non-ASCII field ids rendered nothing (#584, v10.13.14 regression):** `RateTagHelper` built the island's `elem` selector from a `JavaScriptEncoder`-escaped copy of the id while the DOM id stayed raw; `System.Text.Json` then re-escaped the `\uXXXX` sequences, so after client `JSON.parse` the selector no longer matched and `layui.rate.render` silently targeted nothing for legal non-BasicLatin C# identifiers (e.g. Chinese property names). The selector now uses the raw id, matching every sibling widget.
- **`ff.PostForm` validation-failure redraw re-arms islands and legacy scripts (#587):** the form-HTML redraw branch inserted sanitized HTML with no island pre-collection and no inline-script re-execution (lost when `SafeHtml`/DOMPurify hardening landed), so a redrawn form lost error highlights, submit binding, and datetime pickers — a validation failure became a dead end. The branch now mirrors `ff.OpenDialog`: detached-DOMParser pre-collection of islands + `_initScripts` before sanitization, dispatch + re-execution after insertion.
- **Native TagInput separator/Max bypass, no-trim, and chip-remove race (#585):** a pasted/typed string containing the separator counted as ONE tag against `Max` then re-split on render (max 5 could become 7); separator was guarded on keydown only. `_tiAddTag` now splits raw input, trims, drops empties, and enforces `Max` against the resulting count; `_tiCurrentTags` trims (matching legacy `BuildTagsJson`). Chip removal moved from `click` to a primary-button-guarded `mousedown` with `preventDefault`, so removing a chip while the entry input holds pending text is atomic (the blur-rebuild race silently swallowed the deletion; right/middle click correctly no-op).

### Improved

- **#565 harness sections 4 & 10 false-greens killed (#586):** Section 4 passed on `layer.open` alone (its mock island targeted a nonexistent filter — a broken dialog-island dispatch still passed) and Section 10 passed on any truthy `upload.render` return. Section 4 now embeds a real island-backed widget in the dialog partial and asserts post-dispatch DOM + `data-wtm-dispatched`; Section 10 asserts the rendered upload button and click-forwarding. Both proven by sabotage (stubbing `_dispatchIslandWhenReady` / neutering `upload.render` makes them fail, on both trees). The #573 flip gate's green light now actually certifies dialog-dispatch and upload.
- **e2e demo-app readiness gate 30s → 90s + startup log capture (#589):** healthy startups took 20–24s of the 30s budget, so runner contention produced false reds (PR #588 cost a triage cycle); the app's stdout/stderr is now captured and tailed on failure so a genuine crash is distinguishable from a slow start.

### Migration

- No action required. The containment gate only changes behaviour for islands that were already forged; `ff.ConsumeIslandsIn` is additive public API (downstream SPA shells that insert WTM partials via ajax should call it after insertion — see the demo `view.js` diff); the PostForm redraw fix restores behaviour lost in 10.13.12/13. Known follow-ups: #591 (`SafeHtml` strips custom `lay-*`/`wtm-*` attributes from dialog partials), #594 (two real layui-2.13.8 demo-scaffolding regressions — **must fix before the #573 default flip**), #596 (flaky e2e TC-04).

---

## [10.13.15] - 2026-07-04

LayUI dialog-init island hardening + native TagInput. Follow-ups from the v10.13.14 #552 adversarial review, plus the native reimplementation of a TagHelper that never worked against any bundled layui. All changes are eval-free-island architecture (#470) refinements; no shipped default behaviour is removed and the `eval`/`IsScript` fallback stays intact (active `eval(` count remains 1).

### Security

- **Form-containment on slider/rate/colorpicker island write-backs (#578):** the #552 write-back handlers resolved their target hidden input via bare `document.getElementById(fieldId)` with no containment, unlike #564's `highlightErrors`. A smuggled `wtm-dialog-init` island (the #462/#552 threat model) could thus spoof-target any hidden input by id. Each `el.value = …` write is now gated on `!formId || (formEl && formEl.contains(el))`, sourcing the owning-form id from the existing ambient `context.Items["formid"]` (published by `FormTagHelper`, already consumed by the button TagHelpers). No-container case is unchanged (writes unguarded, as before) — verified by back-compat tests.

### Fixed

- **Dialog-path island module-load race closed generically (#576):** `ff.OpenDialog`'s dialog-init dispatch loop called `ff.DispatchAction(payload)` directly, bypassing the `layui.use([mods],cb)` deferral (`ff._dispatchIslandWhenReady`, #556) that the page-ready consumer uses. On dialog open, any island type whose layui submodule wasn't loaded yet could silently no-op. The loop now routes through `ff._dispatchIslandWhenReady`, so **all** island types (`initForm`/`bindSubmit`/`bindValidate`/`highlightErrors`/`laydate`/`loadComboItems`/`slider`/`rate`/`colorpicker`) get the module-load guarantee — superseding #552's per-case workarounds (left in place, now redundant). Legacy inline-script rehydration still runs before island dispatch (single-threaded ordering preserved; guarded by new tests).

### Changed

- **`TagInputTagHelper` reimplemented as a native, dependency-free tag input (#571):** it previously bound to a layui `tagInput` module that ships in NEITHER bundled tree (2.6.3 nor the opt-in 2.13.8 `layui-next`), so `layui.use(['tagInput'],cb)` never resolved and the widget **rendered nothing**. It is now a native chip input driven by `framework_layui.js` (`_renderTagInputAction`) via a `wtm-dialog-init` island — no layui module dependency, works on both trees, XSS-safe (chips built with `document.createTextNode`, never `innerHTML`; adversarially tested with an `<img onerror>` payload rendering inert). Attribute surface preserved (`Delimiter`, `EmptyText`) plus additive `Max`/`ReadOnly`; existing `<wt:taginput>` usages keep compiling.

### Improved

- **#565 regression suite now gates TagInput on both layui trees (#581):** section 14 was a `knownGap` probing whether layui ships a `tagInput` module; it now asserts the native widget's actual output (chips from initial value, hidden-field sync, XSS-inert payload) and passes on both `layui-263` and `layui-next` (14/14 each). **The Phase-2 (#566) gate now has zero knownGaps.**

### Migration

- No action required; all changes are security hardening, a race fix, an equivalent-or-better output-shape change, or test-only. `<wt:taginput>` now actually renders (it was inert before) — no API change, but a previously-broken widget becoming functional is worth noting for anyone who had worked around it.

---

## [10.13.14] - 2026-07-04

LayUI modernization roadmap (#567) — Phases 1–2 land, plus a demo-scaffolding security fix and the final eval-free static-widget migration. The bundled LayUI stays **2.6.3 by default** (BMS zero-risk); a vetted 2.13.8 tree ships alongside it as an **opt-in** parallel asset, gated behind a browser regression suite that passes against both trees. A pre-existing stored-DOM-XSS in the ColorPicker/Slider color emission is closed at the source (both the new island path and the legacy inline-script path).

### Security

- **Demo `FileApiController.GetFile` MIME-sniffing hardening ported to all five demo scaffolds (#563):** the #530 fix (v10.13.11) hardened the framework `_FrameworkController.GetFile`, but the app-owned `[Public]` `FileApiController.GetFile` copied into every `dotnet new wtm` app was never updated — and was actually worse than pre-#530 (it sent **no `Content-Type` header at all** on the raw-stream path, letting the browser sniff an uploaded `text/html`/`image/svg+xml` as active content in the app origin → stored XSS). All five demos (`Demo`, `VueDemo`, `Vue3Demo`, `ReactDemo`, `BlazorDemo`) shared a byte-identical unhardened `GetFile`; all now reuse `_FrameworkController.GetSafeStreamContentType` + `X-Content-Type-Options: nosniff` on the non-mp4 stream branch and pin `image/jpeg` on the re-encoded resize branch. Three behavior-asserting regression tests added.
- **Closed a pre-existing stored-DOM-XSS in ColorPicker/Slider color emission (#552):** the persisted ColorPicker `color` (field data), `PredefinedColors` tokens, and Slider `Theme` were spliced unescaped into LayUI 2.6.3's internal `style="…"` HTML string (`$(htmlString)` parse) on **both** the inline-`<script>` and island paths — `JavaScriptEncoder` protected only the JS string literal, not the decoded runtime value that reaches LayUI's HTML sink. Fixed at the C# source with a shared `BaseFieldTag.IsSafeColorToken` allowlist grammar (`^[#0-9A-Za-z(),.%\s-]+$` — accepts hex/rgb/rgba/hsl/named colors, structurally excludes `< > " ' ; { }`); unsafe values are omitted (LayUI falls back to its default), legitimate colors pass through unchanged, and the bound hidden input still stores the HtmlEncoded value (no data loss). Found via perspective-diverse adversarial review of the #552 island migration.

### Added

- **Opt-in LayUI 2.13.8 parallel asset `layui-next` (#566, roadmap #567 Phase 2):** the demo vendors LayUI 2.13.8 (latest stable 2.x) at `wwwroot/layui-next/` alongside the default 2.6.3 tree; a `Layui:Asset=next` config key switches the demo layout/login to it. **The default stays 2.6.3 — no behaviour change without explicit opt-in.** The #565 TagHelper↔LayUI browser regression suite now runs against **both** trees (`?layui=next`); LayUI 2.13.8 passed all 13 non-gap assertions on the first run with zero TagHelper compat fixes. A default flip to 2.13.8 remains gated on BMS staging sign-off (tracked: #573).

### Changed

- **Slider / Rate / ColorPicker emit an eval-free JSON dialog-init island on the callback-free path (#552, #470-E):** completes the static-config-widget slice of the eval-free dialog epic. A widget with no developer callback (`ChangeFunc`/`OnTipsFunc`) now renders a `wtm-dialog-init` island dispatched via `ff.DispatchAction` instead of an inline `<script>`; a widget **with** a callback keeps the inline-`<script>` fallback unchanged. Equivalent rendered behaviour; the dialog-path dispatch now defers through `layui.use([mod], …)` so a first-in-dialog slider/rate/colorpicker can never silently no-op on a not-yet-loaded module. The Slider value write-back is now per-instance id-scoped (was a shared `name` selector; only differs with duplicate `Field.Name` on one page — tracked: #577).

### Migration

- No action required; all three changes are opt-in, an equivalent output-shape change, or a security fix that only alters output for values that were already an XSS payload. Apps stay on LayUI 2.6.3 unless they explicitly set `Layui:Asset=next`. Follow-ups tracked: #571 (tagInput module absent in all 2.x), #573 (default-flip gate), #576/#577/#578 (OpenDialog dispatch deferral, slider write-back note, write-back containment).

---

## [10.13.13] - 2026-07-03

Eval-free dialogs — completion. Building on the v10.13.12 foundations (#470-A/#556/#558), `FormTagHelper` now emits its form init, submit-binding, auto-validate, and ModelState error-highlight as an eval-free `wtm-dialog-init` JSON island instead of inline `<script>`. **A plain WTM-generated dialog now carries zero inline `<script>`**, making a strict, `unsafe-inline`/`unsafe-eval`-free CSP viable (opt-in) for the LayUI dialog flow. Behaviour is unchanged and browser-verified; the `eval`/`IsScript` fallback remains for legacy and edge-case paths.

### Changed

- **`FormTagHelper` emits an eval-free JSON island for form init + submit + validate + error-highlight (#561, #564):** the per-form inline `<script>` (`ff.RenderForm` / `layui.form.on('submit')` + `BeforeSubmit` gate / auto-validate handler / ModelState error-highlight) is replaced by an `{actions:[initForm, bindSubmit, bindValidate, highlightErrors]}` island dispatched via the eval-free `ff.DispatchAction` path. Rendered behaviour is identical (form render, submit, the `BeforeSubmit` gate, validation, and error display/focus) — **browser-verified** (Playwright) on the render, submit-gate (block/allow), and error-highlight flows. A dialog whose fields are all migrated now emits **no inline `<script>`**.

### Security

- **`highlightErrors` inserts server ModelState messages via `textContent` only (#564):** never `innerHTML`/`eval`; field lookup is `getElementById` + `form.contains()` containment-scoped. Adversarially browser-verified with an `<img src=x onerror=alert(1)>` message — rendered as inert text, zero elements created, no script executed. The `bindSubmit` `BeforeSubmit` resolution keeps the v10.13.12 hardened `window[name]` guard (identifier regex + dangerous-global denylist). The file's active `eval(` count remains 1 (the deprecated `IsScript` fallback).

### Migration

- No action required; the change is an equivalent output-shape change (inline `<script>` → eval-free island), opt-in, and backward-compatible. **Compatibility fallbacks preserved:** a non-identifier `BeforeSubmit` expression (e.g. `obj.Check()`) and SearchPanel / `OldPost` forms keep their legacy inline `<script>` so their gates/behaviour are unchanged. Apps wanting a strict CSP can now opt in for dialogs whose forms use only the migrated paths.

---

## [10.13.12] - 2026-07-03

CI reliability + eval-free dialog foundations. Fixes an intermittently-red CI signal and lands the first opt-in, non-breaking building blocks toward retiring the OpenDialog `eval` sink (#470). All items are additive/opt-in or test-only — no shipped default behaviour is removed, and the `eval` fallback stays intact.

### Fixed

- **Flaky CI: a WorkFlow concurrency test rejected a valid race outcome (#554):** `SequentialTests.Sequential_ConcurrentApprove_ExactlyOneWinner_OneAlreadyHandled` asserted the losing racer must return `AlreadyHandled` or `NodeClosed`, but a legitimate concurrent approve can also leave the loser at a third `ApproveTaskAsync` early guard — `TaskNotActive` (the winner advanced the Sequential pointer past the loser's `SequenceOrder`) — so the same commit passed on re-run and failed intermittently on GitHub/Gitea CI. The loser returns *before* the atomic CAS and never double-approves, so the engine was correct; the assertion was too narrow. Widened it to accept all three legitimate CAS-loss codes (sibling race tests were already correct). Verified across 50 race iterations. Test-only; no runtime change.

### Added

- **Eval-free dialog-init foundations (#470-A #551, #470-B slice 1 #556, #470-C #558) — opt-in, non-breaking:** `ff.DispatchAction` gains `loadComboItems`, a `laydate` action, and a security-hardened `bindSubmit` action; `initForm.dates[]` now carries the full static laydate option set. Dialog-init JSON islands (`<script type="application/json" class="wtm-dialog-init">`) are now consumed universally — all islands in a dialog (`querySelectorAll`) **and** on full-page forms via an idempotent page-ready consumer — with dispatch deferred through `layui.use([...])` so a `laydate`/`form` action can never run before its module loads. `bindSubmit` resolves a developer-named `BeforeSubmit` via a guarded `window[name]` lookup (identifier regex + a denylist of dangerous globals like `eval`/`Function`/`setTimeout`/`fetch` + `hasOwnProperty` + `typeof function`); no `eval`/`new Function` is introduced (the file's active `eval(` count stays 1, the deprecated `IsScript` path). These are the mechanism only — no generated markup emits `bindSubmit` yet.

### Changed

- **`DateTimeTagHelper` emits a JSON init island for callback-free date fields (#556):** a date field with no `ready`/`change`/`done` callback now renders a `wtm-dialog-init` island (dispatched via the eval-free `laydate` action) instead of an inline `<script>laydate.render(...)</script>`. The laydate options are identical, so the rendered picker is unchanged (verified in-browser on both dialog and full-page forms). Date fields **with** a callback keep the inline `<script>` fallback. This is an output-shape change with equivalent runtime behaviour, not a functional change.

### Security

- **`bindSubmit` `window[name]` lookup hardened (#558):** the named-callback resolution is defense-in-depth guarded (identifier-only regex, own-property check, `typeof function`, and a denylist of dangerous globals) and was adversarially security-reviewed before merge. Trust boundary (documented in code): `beforeSubmit`/`filter` must always be compile-time developer-authored literals, never request/field/DB/tenant data — the same trust as today's inline `BeforeSubmit()`.

### Migration

- No action required. All changes are additive/opt-in, test-only, or an equivalent output-shape change; no public API removed, no functional default behaviour changed, the `eval`/`IsScript` fallback is intact. Apps needing a `laydate` callback (`ready`/`change`/`done`) continue to get the inline-`<script>` path automatically.

---

## [10.13.11] - 2026-07-03

Adversarial audit batch. A fresh full-framework multi-agent audit of **v10.13.10** (subsystem-scoped finders → perspective-diverse verification: a correctness lens + an exploitability lens per finding, survive only if neither ruled false-positive) confirmed 14 defects, 0 contested. **11 land here** — 1 P0 dependency, 2 HIGH, 6 MEDIUM, 4 LOW. Each fixed on its own worktree-isolated branch, then integration-merged and validated as a set before landing: full-solution build **0 errors**, **0 NU1903**, Core 4089 / Admin 121 / Etl 606 / WorkFlow 546 tests all pass. The delta finder (v10.13.7..v10.13.10) returned zero — the recently-shipped fixes were clean; the P0 below was a newly-published advisory the release gate had not yet caught.

### Security

- **`Microsoft.OpenApi` 2.4.1 (transitive) was vulnerable — GHSA-v5pm-xwqc-g5wc, HIGH (#528):** Swashbuckle.AspNetCore 10.1.5 transitively pulled `Microsoft.OpenApi` 2.4.1 (circular schema references can terminate OpenAPI parsing — a parser DoS), reaching production. Pinned the first patched **2.7.5** (still on the 2.x major, API-compatible with Swashbuckle) as a direct override in `Mvc.csproj` (`NU1510` on that line is expected). `--vulnerable` now reports 0 NU1903 across the solution.
- **`GetFile` served uploads inline with no `Content-Type` → MIME-sniffing stored XSS (#530 — HIGH):** the `stream=true` (non-mp4) branch wrote the body with `Content-Disposition: inline` but never set `Response.ContentType`, so with default allow-all upload validation a browser MIME-sniffed an uploaded `.html`/`.svg` as active content in the app origin. Now sets a safe content type (native type only for whitelisted image extensions, `application/octet-stream` otherwise) and always emits `X-Content-Type-Options: nosniff` on the streamed response.
- **RBAC public-URL regex was unanchored → fail-open privilege bypass (#531):** `WtmAuthorizationService.MatchUrl` built `"^" + p + "[/\\?]?"` with no end anchor, so any request URL that merely *started with* a public URL matched — a privilege-gated action sharing a prefix with a public one (`/Home/Index` → `/Home/IndexAdmin`) was treated as public. Anchored to a path boundary (`"^" + Regex.Escape(p) + "($|[/?])"`) on both the NonBacktracking and compiled-fallback sites. (`AllAccessUrls` are literal generated paths, so `Regex.Escape` is safe.)
- **SSRF guard missed IPv6 transitional ranges (#533):** `RestWidgetDataSource.IsBlockedIp` did not decode NAT64 (`64:ff9b::/96`), 6to4 (`2002::/16`), or IPv4-compatible IPv6, so a host resolving to e.g. `64:ff9b::169.254.169.254` (IMDS) bypassed the guard. It now extracts the embedded IPv4 from those forms and re-applies the IPv4 rules.
- **Open-redirect via backslash in client redirect guards (#534):** the OpenDialog `Location` guard (#332) and the DispatchAction `redirect` guard accepted `/\evil.com` because only forward-slash was checked as the second char; browsers normalize `\`→`/` for special schemes, yielding a protocol-relative external redirect. Both guards now use `/^\/(?:[^/\\]|$)/` (rejects backslash, preserves the bare-`/` case).

### Fixed

- **Sequential workflow stranded on a leading auto-approved step (#529 — HIGH):** with `InitiatorAutoApprove` and the initiator as the first approver of a Sequential node, `OnEnterAsync` only advanced `SequencePointer` when *every* task was auto-approved — so `[initiator, human, human]` left the pointer frozen on a terminal AutoApproved task with zero Pending tasks and no armed timer, deadlocking the instance forever (a fully-supported config). The handler now advances past the leading contiguous run of auto-approved steps and promotes the first non-auto step to Pending. Companion fix: the timer-arm block in `WorkflowEngine` no longer hardcodes `SequenceOrder == 0` — it re-reads the fresh pointer so the timeout arms on the actually-Pending step.
- **`UpdateModelProperty` silently discarded the edited value (#532):** the entity was loaded `AsNoTracking` and the reflected field was never marked modified (the endpoint's form keys aren't `entity.`-prefixed), so `SaveChanges` wrote only `UpdateTime`/`UpdateBy` while returning `Success`. It now marks the edited property modified (via cached reflection over the runtime entity type, since `Entity` is the covariant `TopBasePoco`) after all existing guards — the sensitive-field blocklist, navigation-path guard, writable-property check, and `CanEditProperty` authz hook still run first, unchanged.
- **Oracle bulk loader failed on an all-dropped batch (#536):** `OracleBulkLoader.BulkLoadAsync` lacked the empty-`DataTable` guard the MySQL/PostgreSQL loaders have, so a batch fully dropped by quality rules set `ArrayBindCount=0` and failed the whole job. Added `if (batch.Rows.Count == 0) return;` before any connection is opened.
- **Low-severity audit cleanup (#538):** `[Public] SetTenant` now returns 401 instead of dereferencing a null `LoginUserInfo` (anonymous NRE); `GetGithubStarts`/`GetGithubInfo` are async (no ThreadPool-blocking `GetAwaiter().GetResult()`, new `WTMContext.ReadFromCacheAsync`); `CS.Cis`/`CisFull` publish a fully-populated list atomically (no partial-read race); and the ETL batch-retry path wraps MSSQL/MySQL bulk loads in a transaction so a failed attempt rolls back instead of duplicating already-committed rows.

### Changed

- **MySQL bulk loader now chunks large batches by default (#537):** `MySqlBulkLoader.InternalBatchSize` default changed **`0` → `1000`**. Previously the default emitted a single multi-row `INSERT` for the whole batch (default `BatchSize` 50,000), risking `max_allowed_packet` overflow. The default now chunks into 1,000-row statements (data written is identical; wrapped in a transaction per #538). Other providers are unaffected (SqlBulkCopy / COPY / array-bind use native mechanisms).
- **Captcha session write is now async (#535):** `GetVerifyCode` changed from `ActionResult` to `async Task<ActionResult>` and uses a new additive `SessionExtensions.SetAsync<T>` instead of the sync `Set<T>` (which blocked a ThreadPool thread via `CommitAsync().GetAwaiter().GetResult()` on this unauthenticated path). The sync `Set<T>` is unchanged for backward compatibility.

### Migration

- **#537 (MySQL bulk load):** no action required and the new chunked+transactional default is recommended. If you specifically relied on a single-statement `INSERT` per batch, pass `InternalBatchSize: 0` when constructing `MySqlBulkLoader` to restore the previous behavior.
- **#535 (captcha):** no action required unless you overrode `GetVerifyCode` with a synchronous signature — it is now `async Task<ActionResult>`. The public `ISession.Set<T>` extension is unchanged; `SetAsync<T>` is additive.
- All other entries are security/correctness fixes with no API or default-behavior change.

---

## [10.13.10] - 2026-06-25

### Fixed

- **`EmptyContext.OnModelCreating` threw `InvalidCastException` on EF Core 10 (Oracle) → Oracle apps failed to start (#525):** the Oracle path cast `ModelBuilder` to `IConventionModelBuilder` — `((IConventionModelBuilder)modelBuilder).HasMaxIdentifierLength(30)` — which worked on EF Core 8 but **throws `InvalidCastException` on EF Core 10** (10.0.4): `ModelBuilder` no longer supports that cast. Any application using `EmptyContext` (or a subclass) with `DBType == DBTypeEnum.Oracle` failed to start (the Oracle path is not exercised by SQL Server / InMemory builds, so it surfaced only in Oracle production — the same no-live-Oracle CI blind spot as #485/#499). Fix: use the cast-free `IMutableModel` API `modelBuilder.Model.SetMaxIdentifierLength(30)` (verified against the EF Core 10.0.4 `Microsoft.EntityFrameworkCore.Relational` reference — `RelationalModelExtensions.SetMaxIdentifierLength(IMutableModel, int?)`); works on EF Core 8 and 10. A regression test reproduces the failure under a SQLite (relational) provider by driving the Oracle branch, so it now has CI coverage without a live Oracle.

### Migration

- No action required. Oracle deployments that could not start on EF Core 10 now boot correctly.

---

## [10.13.9] - 2026-06-22

### Fixed

- **Dialog form init scripts re-injected as `<script>` elements — native order/scope restored, DOMPurify stays active (#522):** the #462 dialog rehydration re-ran the partial's extracted inline init scripts via per-script `eval()`, which does not reproduce native inline-`<script>` semantics — a top-level `var X = xmSelect.render(...)` (`ComboBoxTagHelper`) did not become a shared global, so the sibling `window['X'].update(...)` (`BaseFieldTag`) threw `TypeError: …update is not a function` and ordering/scope-dependent controls (xm-select, toggles, FK selects) failed to initialize. Downstream apps were forced to override `ff.SafeHtml` to a no-op so the scripts would run natively — **disabling DOMPurify on dialog markup (losing XSS protection)**. Fix: each extracted init script is now re-injected as a real `<script>` element in document order, so the browser executes them in native global scope + order with cross-script `var` sharing, **while `ff.SafeHtml`/DOMPurify stays active on the markup**. The DOMParser extraction gate (real `<script>` elements only — never attribute/text-node `"<script>"` strings), the `ff.SafeHtml` sanitization order, `ff._legacyScriptEval` (still used by the IsScript response-header branches), and the total `eval(` count (1) are all unchanged — **no wider XSS surface than #462**. Downstream apps (e.g. BMS) can now remove the `SafeHtml` no-op shim and keep DOMPurify. (+14 JS tests incl. jsdom order/scope behavioral + XSS-boundary; full suite 1132/1132.)

### Migration

- No action required. Apps that added an `ff.SafeHtml = h => h` no-op shim to work around the broken dialog init can now **remove it** and keep DOMPurify XSS protection on dialog markup.

---

## [10.13.8] - 2026-06-22

Housekeeping + small additive hardening — all **opt-in / non-breaking**, no shipped default-behaviour change. Closes the remaining P3 backlog after the v10.13.7 self-audit, plus a CI robustness fix and the foundation for eval-free dialogs.

### Added

- **Opt-in eval-free dialog form init — foundation (#470):** `ff.DispatchAction` gains a whitelisted `initForm` action (eval-free `layui.form.render` + declarative `laydate`); `ff.OpenDialog` consumes an opt-in `<script type="application/json" class="wtm-dialog-init">` JSON island (`JSON.parse` → DispatchAction, **zero eval**); and a new `DialogInitTagHelper` (`<wt:dialog-init form-filter="…">`) emits the island, JSON-serialized with `JavaScriptEncoder.Default` so a `</script>` in a value can never break out (the #481/#490 lesson). A dialog form can now initialize with **zero eval** by opting in. Purely additive — existing dialogs, `ff._legacyScriptEval`, and the #462 DOMParser rehydration are unchanged. (Full retirement of the eval sink stays tracked on #470, gated on downstream `FFResultJson` migration.)
- **`WTMContext.IsKnownConnectionKey(string?)` (#517):** the #503/#506 connection-key guard is now a public single-source-of-truth method, callable as `Wtm.IsKnownConnectionKey(csKey)` from any derived/forked/API controller (null/empty → default connection; case-insensitive match against configured connections). Lets downstream/forked controllers reuse one guard instead of re-implementing the cross-DB check. `_FrameworkController`'s guard now delegates to it.

### Fixed

- **`Utils.MD5String` leaked an undisposed `MD5` instance (#487):** the legacy MD5 path created `MD5.Create()` without disposing it; now `using var`, matching the sibling `GetMD5Stream`. Managed MD5 holds no OS handle, so this is a GC-pressure consistency cleanup (no functional change).

### Changed

- **CI: `publish-nuget.yml` `Create GitHub Release` is now idempotent (#514):** GET the release by tag → `PATCH` if it exists, else `POST`. A re-pushed tag (during the recurring Gitea-Actions stuck-state recovery, where the publish workflow can run more than once for the same tag) no longer 422-fails the run. CI-only — not shipped in any package.
- **Removed 705 lines of residual dead code (#502):** commented-out `LogTrace.cs` / `LogDebug.cs`, five commented regions in `PagedListExtension.cs`, and dead methods (`GetServerUrl`, `GetAllAccessUrls`, `getAuthTypes`, `HandleDeferredAction`, `ReadFreshNodeAsync`) plus dead JS (`_makeWidgetHtml` and a commented `ff.LoadPage` block). Each grep-verified zero-reference (including string/reflection lookups) before removal; full suite stayed green.

### Migration

- No action required. Everything is additive/opt-in or internal cleanup — no public API removed, no default behaviour changed.

---

## [10.13.7] - 2026-06-21

Self-audit regression batch. A **round-2** multi-agent adversarial audit of v10.13.6 — explicitly re-auditing the just-shipped fixes — caught **two HIGH Oracle regressions introduced by v10.13.6's own #485** (its bundled "Oracle identifier quoting" change), plus resource/correctness/security issues. The over-scoped quoting is reverted; the rest fixed. Integrated full-solution suite: **5378 passed / 0 failed**, 0 NU1903.

### Fixed

- **Oracle ETL regressions from #485 identifier quoting — ORA-00955 + ORA-00904 (#499 — HIGH ×2):** #485 (v10.13.6) bundled an L5 "Oracle identifier quoting" hardening that applied case-sensitive double-quotes to `CREATE TABLE` but left the halves of the contract misaligned: the staging-table existence probe still uppercased the name (`USER_TABLES`) so a non-uppercase staging name (incl. the framework's own `STG_{target}_dryrun`) never matched → re-CREATE every run → **ORA-00955** on the 2nd+ run; and `INSERT`/`MERGE` referenced columns unquoted (Oracle folds to uppercase) against case-sensitively-created columns → **ORA-00904** for any non-uppercase column. CI has no live Oracle (integration tests `Assert.Inconclusive`) and the one test used all-uppercase names, so it escaped coverage. **Fix:** reverted the #485 Oracle quoting (`QuoteIdentifier`/`QuoteQualified` removed) so all Oracle identifiers are unquoted and fold consistently to uppercase, matching the probe — the known-good pre-#485 behaviour. The #485 watermark coercion fix is unaffected. *(Lesson: a speculative consistency hardening for a provider with no CI coverage, bundled into an unrelated fix, shipped two HIGH regressions.)*
- **Resource leaks: ImageSharp `Image`, S3 `GetObjectResponse`, NPOI `XSSFWorkbook` (#500 — MEDIUM):** `UploadImage` leaked the ImageSharp `Image` (pooled pixel buffer) + `MemoryStream` when `Resize`/encode/`Upload` threw after a successful `Load`; `WtmS3FileHandler.GetFileData` never disposed the `GetObjectResponse` (owns the live HTTP response stream/connection); `DashboardExcelExporter` created an `XSSFWorkbook` with a bare `var` (never disposed) inside a recurring `IHostedService` background job. All three now wrapped in `using` so they are released on every path.
- **Analysis filter values parsed with current-culture `Convert.ChangeType` (#501 — MEDIUM):** `AnalysisQueryEngine.Filters.cs` coerced user filter values with no `IFormatProvider`, so under comma-decimal request cultures (de/fr/ru…, switched by `Accept-Language`) a numeric filter like `1234.56` mis-parsed → silently wrong analytics or `FormatException` → 500. Now pinned to `InvariantCulture`, matching the already-hardened `PropertyHelper` sites.
- **`DoRealDeleteAsync` orphaned physical files; `DoDeleteAsync` was sync-over-async (#504 — MEDIUM/latent):** the async hard-delete omitted the unloaded-sub-file `.Include()` re-fetch the sync path performs → physical attachment files orphaned; and `DoDeleteAsync` called the **sync** `DoRealDelete()`. Fixed: async re-fetch added (with `f.SetValue(null)` now correctly gated), and `DoDeleteAsync`'s `TopBasePoco` hard-delete branch now `await DoRealDeleteAsync()` (the `IPersistPoco` soft-delete branch is unchanged).

### Security

- **Selector + file endpoints set `CurrentCS`/`CreateDC` from client key without `IsKnownConnectionKey` guard (#503, #506 — MEDIUM):** the `Selector` action (and, defense-in-depth, `UploadImage`, `UploadForLayUIRichTextBox`, `UploadForLayUIUEditor`, `GetFileName`, `GetFile`, `ViewFile`) passed the client-supplied connection-string key to the data context without the allowlist guard every other endpoint applies — letting an authenticated user pivot a Selector popup to any *configured* named connection (cross-DB read in multi-datasource deployments; the file endpoints were self-limiting via a null DC). All now reject unknown keys with each endpoint's natural error shape; the null/empty default-connection path is preserved.
- **`CodeGenVM` field-identifier validation (#505 — MEDIUM, dev-only):** the code generator interpolated POST-controlled `FieldName`/`SubField` raw into emitted C#/Razor source. Reachable only with `[DebugOnly]` + `IsQuickDebug` + the Development-only startup guard (not production), so defense-in-depth: a fail-fast `^[A-Za-z_][A-Za-z0-9_]*$` validation pass now runs before any source is emitted.

### Migration

- **No action required for most deployments.** #499 restores the pre-#485 Oracle behaviour (unquoted identifiers that fold to uppercase). If an Oracle ETL deployment ran under v10.13.6 and created staging tables with **case-sensitive (non-uppercase) names**, drop/recreate them (or rename to uppercase) so the restored uppercase-folding probe matches. No API surface change.

---

## [10.13.6] - 2026-06-21

Multi-agent adversarial security & correctness audit batch (2026-06-21): 9 confirmed findings (4 HIGH / 5 MEDIUM) plus 1 review-discovered HIGH (#490), each adversarially verified (triple-skeptic refutation panel) before fixing and re-reviewed after. Integrated full-solution suite: 5309 passed / 0 failed. (#487, an unrelated LOW `MD5` dispose cleanup, remains open and tracked.)

### Security

- **Selector inline-script data `</script>` breakout XSS (#481 — HIGH):** The `/_Framework/Selector` POST action populated `ViewBag.SelectData` with raw `GetDataJson()` output and passed it through a paired-tag `ScriptTagRegex` (`<script>.*?</script>`, dotall). The Selector view emits this value as `var x = @Html.Raw(ViewBag.SelectData);` inside a live `<script>` element. A bare `</script>` in any entity field value (persisted via a legitimate Create/Edit form or direct DB insert) was not matched by the paired-tag regex, so it terminated the page's real script element and any following markup executed as HTML — stored XSS. The dead `ScriptTagRegex` is removed. `GetDataJson()` output is now passed through `SanitizeSelectorJson`, which replaces `&` → `&`, `<` → `<`, `>` → `>` before assignment to `ViewBag.SelectData`. These `\uXXXX` sequences are valid JSON string content and valid JS string literal content — the JS engine decodes them transparently, so Selector grid display is unaffected. Also removes a dead `FrameworkRole.ToList()` query (`tst` local, never read) that ran on every Selector POST with pre-selected Ids. Three regression tests added.

- **Grid `Image`/`Progress` rich-column attribute XSS (#482 — HIGH):** `ff.EscapeText` (text-node serialization) does not encode the double-quote, yet the `Image` (`src="…"`) and `Progress` (`lay-percent="…"`) rich-column templates placed its output inside double-quoted HTML attributes — a row value like `x" onerror="…` broke out of the attribute into an injected event handler (stored XSS). Added `ff.EscapeAttr` (delegates to `EscapeText`, then encodes `"`→`&quot;` and `'`→`&#39;`); both templates now use it (`Progress` is also numeric-coerced). The prior unit tests only asserted `ff.EscapeText` was *present* (false assurance) and were rewritten to run real quote payloads through jsdom and assert no attribute breakout. Introduced by the #432/#437 rich-column feature.
- **`UseLocalData` grid JSON case-variant `</SCRIPT>` XSS (#490 — HIGH):** the `UseLocalData=true` grid embedded `GetDataJson()` in an inline `<script>` after a *case-sensitive* `.Replace("</script>", …)`, so `</SCRIPT>` (or any mixed case) survived and closed the page's script element. Replaced with `EscapeLocalDataJson`, which unicode-escapes every `<`/`>`/`&` (exhaustive and case-independent); the obsolete `$$script$$`/`$$#script$$` placeholder round-trip (server replace + `ff.LoadLocalData` reversal) was removed. Found by adversarial review of #481.
- **ETL legacy per-job webhook SSRF (#484 — MEDIUM):** `EtlAlertService` POSTed the operator-editable `AlertWebhookUrl` via a bare `HttpClient`. The `EtlAlert` client now uses `AllowAutoRedirect=false` + `RestEtlSource.PinnedConnectAsync` (DNS-pinned at connect time, blocking loopback/RFC1918/link-local/IMDS/IPv6-ULA, redirects disabled); the URL is validated (https-only + blocked-IP) both at the `EtlJobDefinitionVM` boundary and before POST; and `runLog.ErrorMessage` is run through `EtlErrorSanitizer.SanitizeRaw` on **both** the `text` and `errorMessage` payload fields and the email body (defence-in-depth parity with the shared sink).
- **Analysis `CompareWith.Filters` bypassed the expression-tree DoS cap (#486 — MEDIUM):** the `MaxFilterClauses` (50) bound that limits `Expression.AndAlso` tree depth was enforced on `Filters`/`HavingFilters`/`Sort` but not on the period-over-period `CompareWith.Filters`, which feeds the same `ApplyFilters` builder. The cap now applies to `CompareWith.Filters` across the Query/Pivot/Export/PivotExport actions.

### Fixed

- **`BaseImportVM.BatchSaveData` silently dropped all new rows when `UseBulkSave=true` and `DbType=SqlServer` (#480 — HIGH):** the SqlServer bulk-save branch contained only a commented-out `ListAdd.Add(item)` statement, so every new (non-duplicate) row was routed into a no-op and neither added to the EF change tracker nor bulk-inserted. `SaveChanges()`+`tx.Commit()` still succeeded, the method still returned `true`, but zero rows were persisted — silent data loss with false success. Duplicate/overwrite rows (which took the `UpdateProperty`+`continue` path) were unaffected, masking the failure in mixed imports. Fix: removed the dead special-case branch; all rows now go through `DC.Set<P>().Add(item)` unconditionally. The `BulkInsert<K>` no-op stub and the `ListAdd` accumulator were removed alongside. The public `UseBulkSave` property is retained for source compatibility but has no effect; its XML doc now notes this. **No default-behaviour change** — `UseBulkSave` defaults `false`, so existing callers are unaffected.
- **Parallel/Inclusive Gateway: non-idempotent branch mint + non-atomic Join pin (#483 — HIGH/MEDIUM):** a concurrent `AdvanceAsync` CAS-loser re-ran the gateway `OnEnterAsync` and threw a raw `DbUpdateException` (HTTP 500) instead of the engine's `AlreadyHandled` because the branch mint was not idempotent. The mint now uses a provider-independent existence pre-check (idempotent even for NULL-`TenantCode` deployments, where the unique index does not fire on PostgreSQL/MySQL/SQLite/Oracle) plus a unique-violation catch backstop. `JoinExpectedArrivals` is now set in-memory and committed atomically with the Join row (closing a crash-between-two-commits window that permanently stranded the Join with expected=0); the separate `ExecuteUpdate` pin is retained only as a crash-recovery fallback. `WorkflowGraphValidator` now rejects Parallel/Inclusive gateways with zero outgoing transitions. New tests cover the concurrent-loser path, atomic pin, null-tenant idempotency, and the validator.
- **ETL Identity watermark never advanced for `decimal`/`short`/`byte` (#485 — MEDIUM):** the Identity watermark coercion only mapped `long`/`int`, so Oracle `NUMBER`→`decimal` (and `SMALLINT`→`short`, `TINYINT`→`byte`) fell through to null → the watermark never advanced → silent unbounded re-extraction of the same window with no warning. Coercion now uses `Convert.ToInt64` across all integral/decimal types and logs a warning on genuine (non-numeric) failure; an explicit `null`/`DBNull` guard prevents an empty/null-max batch from resetting the watermark backward to 0. Also (L5): the Oracle bulk loader now quotes table/column identifiers (`QuoteIdentifier`/`QuoteQualified`), matching the MySQL/PostgreSQL/MSSQL loaders.

---

## [10.13.5] - 2026-06-21

Security + maintenance. Clears the last standing NU1903 (#393) now that an upstream fix exists — the vulnerable bundled SQLite engine is no longer pulled into any package. Also ships four downstream-reported (BMS-integration) regressions against 10.13.1 that had already merged to the branch (#461–#464), plus a CI reliability change (#473).

### Security

- **Eliminated `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 NU1903 (#393 — GHSA-2m69-gcr7-jv3q, HIGH):** `Microsoft.EntityFrameworkCore.Sqlite` 10.0.4 transitively pulled `SQLitePCLRaw.bundle_e_sqlite3` → the native `SQLitePCLRaw.lib.e_sqlite3` 2.1.11, which carries a HIGH advisory for the SQLite engine it bundles (advisory range `<= 2.1.11`, `first_patched: None`). Until now there was **no version to pin to** — 2.1.11 was the latest published — so #393 was tracked as an *accepted, unfixable* exception. SQLitePCLRaw has since shipped its 3.0.x line, which **restructures packaging**: the monolithic `lib.e_sqlite3` is replaced by `config.e_sqlite3` + `SourceGear.sqlite3` (SQLite 3.50.4). A direct `SQLitePCLRaw.bundle_e_sqlite3 3.0.3` override (pinned in `Directory.Packages.props`, wired via a `PackageReference` in `Core.csproj`, mirroring the existing `System.Security.Cryptography.Xml` override) promotes the whole chain to 3.0.x. The GHSA-affected package **`SQLitePCLRaw.lib.e_sqlite3` is now entirely absent from the dependency tree** — not merely bumped past the vulnerable range.
  - **Verified:** `dotnet list … --vulnerable --include-transitive` reports **0 vulnerable packages** across the solution (was: HIGH NU1903 in Core/Mvc/WorkFlow/LayUI/Etl). Full suite **5314 passed / 0 failed**, including the SQLite-shared-memory test fixtures that exercise the new native engine — confirming SQLitePCLRaw 3.0.x is binary-compatible with `Microsoft.Data.Sqlite.Core` 10.0.4.
  - **Opt-in / compatibility:** the override is transparent to consumers; apps using the SQLite provider transparently get the patched engine. No API surface change.

### Fixed

- **Grid bool→checkbox cells rendered as escaped text when the column had no `SetFormat` (#461 — regression from the #331/#332/#805 `ff.EscapeText` hardening):** a boolean column declared as a bare `this.MakeGridHeader(x => x.SomeBool)` receives framework-generated checkbox HTML from `MakeCheckBox`. The `ff.EscapeText` grid-cell hardening was then escaping that framework markup into literal text, so the cell showed raw `<input type="checkbox" …>` HTML instead of a checkbox. `bool`/`bool?` columns are now treated as `hasFormat=true` so their framework-controlled HTML renders verbatim. **No XSS regression:** `MakeCheckBox` HTML-encodes every caller-supplied attribute (name, value, title) via `WebUtility.HtmlEncode`; only the fixed framework markup is exempted from escaping.
- **Dialog forms never initialized — `ff.OpenDialog` lost inline init `<script>` after #789 DOMPurify (#462):** the #789/#801 client-side DOMPurify (`ff.SafeHtml`) strips `<script>` from the dialog partial, so the form's inline initialization scripts (layui form render, date pickers, etc.) never ran and Create/Edit dialogs rendered but stayed inert. `OpenDialog` now extracts trusted inline init scripts from the same-origin partial **before** sanitization and re-runs them after the dialog DOM is inserted. Extraction uses `DOMParser` to take **real `<script>` elements only** — never a regex over the HTML string, which would also match a `"<script>"` sequence sitting inside an attribute value or text node and execute it, widening XSS beyond the pre-#789 baseline. Only inline JS is re-run (external `src` and non-JS blocks such as `application/json` are skipped); `DOMParser` does not itself execute scripts, and the rendered markup is still passed through `ff.SafeHtml`.
- **Field-less `<wt:display display-text="…">` threw at render (#463 — the #333 `Field==null` guard not exempted for `DisplayTagHelper`):** `DisplayTagHelper` legitimately renders a static label with no `field=` binding, but the `BaseFieldTag` `Field==null` guard added in #333 ("11 functional TagHelper defects") rejected it with an `InvalidOperationException`. `DisplayTagHelper` is now exempted from that guard (mirroring the existing `DisplayTagHelper` special-case on the adjacent guard); all other field tags still require `field=`.
- **`IconFontsHelper` getters null-dereferenced before `GenerateIconFont` ran (#464):** reading `IconFontsHelper.IconFontItems` / `IconFontDicItems` before `GenerateIconFont(...)` populated the static caches threw `ArgumentNullException` (`.Where` on a null static list). Both getters now defensively initialize their backing field to an empty list/dictionary, so early access returns empty instead of throwing.

### Changed

- **CI: `actions/upload-artifact@v4` marked `continue-on-error` (#473):** the v4 artifact-upload action is incompatible with the local Gitea Actions (GHES-style) API and was hard-failing the `build-and-test` / `e2e` jobs even when all tests passed (long-standing quirk, Issue #11). The upload steps are now `continue-on-error: true` so a green test run reports green. **CI infrastructure only — not shipped in any `WalkingTec.Mvvm.*` package.**

### Migration

- **Eliminated `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 NU1903 (#393 — GHSA-2m69-gcr7-jv3q, HIGH):** `Microsoft.EntityFrameworkCore.Sqlite` 10.0.4 transitively pulled `SQLitePCLRaw.bundle_e_sqlite3` → the native `SQLitePCLRaw.lib.e_sqlite3` 2.1.11, which carries a HIGH advisory for the SQLite engine it bundles (advisory range `<= 2.1.11`, `first_patched: None`). Until now there was **no version to pin to** — 2.1.11 was the latest published — so #393 was tracked as an *accepted, unfixable* exception. SQLitePCLRaw has since shipped its 3.0.x line, which **restructures packaging**: the monolithic `lib.e_sqlite3` is replaced by `config.e_sqlite3` + `SourceGear.sqlite3` (SQLite 3.50.4). A direct `SQLitePCLRaw.bundle_e_sqlite3 3.0.3` override (pinned in `Directory.Packages.props`, wired via a `PackageReference` in `Core.csproj`, mirroring the existing `System.Security.Cryptography.Xml` override) promotes the whole chain to 3.0.x. The GHSA-affected package **`SQLitePCLRaw.lib.e_sqlite3` is now entirely absent from the dependency tree** — not merely bumped past the vulnerable range.
  - **Verified:** `dotnet list … --vulnerable --include-transitive` reports **0 vulnerable packages** across the solution (was: HIGH NU1903 in Core/Mvc/WorkFlow/LayUI/Etl). Full suite **5314 passed / 0 failed**, including the SQLite-shared-memory test fixtures that exercise the new native engine — confirming SQLitePCLRaw 3.0.x is binary-compatible with `Microsoft.Data.Sqlite.Core` 10.0.4.
  - **Opt-in / compatibility:** the override is transparent to consumers; apps using the SQLite provider transparently get the patched engine. No API surface change.

### Migration

- No action required. Apps that consume `WalkingTec.Mvvm.Core` automatically pick up the patched native SQLite engine on restore. If a downstream app independently pinned `SQLitePCLRaw.*` to a 2.1.x version, bump it to `3.0.3` (or remove the pin) to inherit the fix.

## [10.13.4] - 2026-06-21

Patch — a data-integrity bug fix in `DataContext` model-building, found by a comprehensive regression-validation pass over the 10.13.x `OnModelCreating` changes.

### Fixed

- **`FileAttachment` foreign keys lost `OnDelete(Restrict)` for navigation-only entities (#458 — regression from #452):** 10.13.2 (#452) narrowed the FileAttachment FK-restrict loop in `DataContext.OnModelCreating` from the global model set to this context's declared `DbSet<T>` types only. As a result, a `TopBasePoco` entity that owns a `FileAttachment` property but is reachable only through a navigation property (i.e. not declared as a `DbSet<T>` on the context) no longer had its attachment FK marked non-cascading — it fell back to EF Core's default delete behaviour:
  - **required FK → `Cascade`**: deleting a `FileAttachment` row would **cascade-delete the owning business entity** (data loss);
  - **optional FK → `ClientSetNull`/`SetNull`**;
  - multi-`DbContext` apps also saw a spurious `ALTER` foreign-key migration delta.

  The FK-restrict loop now runs after entity registration and iterates the context's **fully-discovered EF model** (`modelBuilder.Model.GetEntityTypes()`) — the same superset scope the query-filter pass already uses — so navigation-reachable owners regain `OnDelete(DeleteBehavior.Restrict)`. This does **not** reintroduce the #382/#450 over-expansion crash: it only reads entities EF already discovered for *this* context and never force-registers foreign types. Abstract / non-`TopBasePoco` / `ISubFile` types are skipped, and `DeclaredOnly` property lookup prevents double-configuring inherited relationships.

  **Verified unaffected:** tenant and soft-delete query-filter coverage (the Pass-2 filter loop already iterated the full discovered model across #382→#450→#452 — there was never a cross-tenant filter-coverage regression). Adds `DataContextNavOnlyFileAttachFkTests` regression coverage.

## [10.13.3] - 2026-06-20

Packaging / CI-only release — **no framework code changed.** The only commit since 10.13.2 (#456, porting #455) edits `.github/workflows/publish-nuget.yml`, which is repository CI infrastructure and is **not** shipped inside any `WalkingTec.Mvvm.*` NuGet package. The compiled content of all six existing packages is byte-for-byte identical to 10.13.2. The user-facing effect is purely distribution: the unified publish pipeline now packs two additional packages — `WalkingTec.Mvvm.Etl` and `WalkingTec.Mvvm.FileHandlers.S3` — so they reach the public GitHub Packages mirror for the first time, and the corrected end-to-end publish pipeline is exercised/validated.

### Changed

- **Publish pipeline now packs and pushes all six packages, incl. `WalkingTec.Mvvm.Etl` + `WalkingTec.Mvvm.FileHandlers.S3` (#455, #456):** the NuGet publish workflow previously omitted `Etl` and `FileHandlers.S3` from both the local pack stage and the GitHub-Packages re-pack block, so those two packages were never published to the public GitHub Packages mirror despite existing in the repo (they were only on the internal registry via a manual per-release push). Both pack steps are now included. The S3/MinIO file handler (introduced as the new `WalkingTec.Mvvm.FileHandlers.S3` package in the 10.13.0 商用化 program) and the ETL package are therefore installable from GitHub Packages starting with this version. **Distribution/CI change only — no shipped package binary differs from 10.13.2; the existing four packages are unchanged.**

## [10.13.2] - 2026-06-20

Patch — completes the revert of a `DataContext` model-building regression introduced in 10.12.4 (#382). Multi-`DbContext` apps could crash at startup or get spurious migration tables; both code paths are now scoped to each context's own entity set.

### Fixed

- **`DataContext.OnModelCreating` over-registered foreign entities into the wrong context (#450, #452 — regression from #382):** the multi-level-inheritance query-filter fix shipped in 10.12.4 (#382) switched two model-builder loops to the **global** `Utils.GetAllModels()` set, which is populated from **all** `DbContext` subclasses visible in the loaded assemblies. In an application with a secondary context, entity types belonging to that other context were force-registered into **this** context's model. Consequences:
  - **Startup crash** — if another context declared a keyless / no-primary-key view entity, EF's `ValidateNonNullPrimaryKeys` threw *"requires a primary key to be defined"* against a context that never owned that type.
  - **Spurious migration tables** — foreign entities leaked into this context's model, producing tables that don't belong in its migrations.

  Both the **file-attachment FK loop** and the **Pass-1 entity-registration loop** now iterate only `thisContextDbSetTypes` — the `DbSet<T>` entity types declared on this concrete context (and its base types up to, but not including, `DbContext`), which is exactly the set EF discovers naturally. `Utils.GetAllModels()` / `allTypes` is no longer referenced in `OnModelCreating`. The Pass-2 query-filter logic (the actual #382 multi-level-inheritance fix, applied on EF-root entity types) is unchanged, so the original #382 fix is preserved. #450 scoped Pass 1; #452 scoped the FK loop and removed the global set entirely. Single-context apps are unaffected. Regression tests cover both the multi-context isolation case and the file-attach FK scope.

## [10.13.1] - 2026-06-20

Patch — a HIGH-impact readiness-probe bug fix + a log-injection security hardening (plus CI docs and demo/test dependency bumps).

### Fixed

- **`/ready` permanently returned 503 with the default `IDataContext` (#441):** `WtmDataContextHealthCheck` read `_dc.DBType` **outside** its `try/catch`; with WTM's DI-default `NullContext` (whose members throw `NotImplementedException`) the exception escaped the catch, so **every app using `AddWtmDataContextCheck()` got a permanent Unhealthy / HTTP 503** readiness result (`description = "The method or operation is not implemented."`). Now: a `NullContext` sentinel short-circuits to `Healthy` ("no DataContext configured"), and the `DBType` read is moved inside the `try`. Apps with a real `IDataContext` registered are unaffected (still a genuine `CanConnectAsync` probe).

### Security

- **Log-injection hardening — CWE-117 / `cs/log-forging` (#442):** 19 log statements across the WorkFlow engine and the Dashboard / WorkflowDesigner / EtlSchema / WorkflowTask / WorkflowDefinition controllers logged user-controlled values (ITCodes, ids, node keys, connection-string keys, table/search inputs) without CR/LF sanitization, allowing forged/injected log entries on plain-text sinks. They now route user input through `LogSanitizer` (strips CR/LF/control chars, caps length); structured-logging placeholders are preserved.

### Changed

- **CI operations doc (#443):** `docs/ci-operations.md` now documents the runner topology (WTM CI runs on the Docker `act_runner`, `ubuntu-latest`; a separate Homebrew runner serves other projects) and the Gitea release/tag-trigger gotchas (Gitea Release objects are created manually; a tag-ref `workflow_dispatch` returns 204 but creates no run; a same-commit tag re-push de-dupes — use a fresh commit/tag).
- **Demo/test npm dependencies (#447):** bumped npm deps in the Vue/Vue3 demo ClientApps and the JS test project to clear Dependabot critical/high advisories (shell-quote, axios, form-data, tar, etc.). Demo/test-only — **not** part of any shipped `WalkingTec.Mvvm.*` package.

## [10.13.0] - 2026-06-20

**#193 商用化 (commercialization) program — COMPLETE.** 18 PRs (#405–#439) since 10.12.5 across Security & Correctness, the WorkFlow HTTP surface, Perf/async Phase 2, platform services, grid + import richness, CodeGen modernization, and Dashboard delivery. Every change is **opt-in / non-breaking by default**; each was independently adversarially reviewed (real defects caught and fixed before merge). Two cloud-provider areas (OSS COS/Qiniu, SMS) are intentionally shipped as seams (`IWtmFileHandler`, `ISmsSender`) rather than bundled SDKs, for the 中文內網 / single-tenant target.

### Changed

- **CG-03/04: Deprecated EOL Vue 2 codegen target (`UIEnum.VUE`) (#434, Refs #193):**
  Vue 2 reached end-of-life in December 2023.  `UIEnum.VUE` is now decorated with
  `[Obsolete("Vue 2 is end-of-life; use VUE3 or Blazor instead.", false)]` — a **warning**,
  not an error, so existing builds continue to compile.  Code generation for Vue 2 still
  runs to preserve backwards-compatibility; no generated output changes.

  **User-visible changes:**
  - A yellow deprecation banner is shown in the code-generator UI (Index + Gen pages) when
    Vue 2 is the active target.
  - The server logs a `LogWarning` entry at start and at generation time.
  - On the generation-success dialog the success message is appended with the deprecation text.

  **Supported SPA targets (unchanged):** `VUE3` and `Blazor` remain fully functional.
  `UIEnum.VUE` will be removed in a future major version.

  **Migration:** replace `UIEnum.VUE` with `UIEnum.VUE3` and regenerate.

### Added

- **Opt-in S3/MinIO file handler — new `WalkingTec.Mvvm.FileHandlers.S3` package (#425, Refs #193):**
  A new standalone NuGet package adds S3-compatible object storage (AWS S3 and MinIO) as a WTM file handler.
  - `S3FileHandlerOptions`: `ServiceUrl`, `BucketName`, `AccessKey`, `SecretKey`, `Region`, `ForcePathStyle` (required for MinIO), `KeyPrefix`.
  - `WtmS3FileHandler` implements `IWtmFileHandler` (Upload → PutObject; GetFileData → GetObject; DeleteFile → DeleteObject).
  - `AddWtmS3FileHandler(Action<S3FileHandlerOptions>)` registers `IAmazonS3` as singleton and the handler as scoped `IWtmFileHandler`.
  - `Core` gains **no** AWSSDK dependency — AWSSDK.S3 lives only in the new package.
  - AWSSDK.S3 4.0.25.2 introduces zero new vulnerable packages (only the pre-existing SQLitePCLRaw #393 unfixable NU1903).

  **Migration / opt-in:** existing apps are unaffected. Install the `WalkingTec.Mvvm.FileHandlers.S3` package and call `AddWtmS3FileHandler(...)` to activate.

- **WorkFlow HTTP surface completed (#406, Refs #193):** five new `WorkflowTaskController` endpoints expose engine capabilities that were previously engine-only — `add-approver` (加签), `delegate` + `revoke-delegation` (委托, admin-gated), `return-to-prev` + `return-to-node` (回退). Actor ITCode is always server-side; `MapEngineResult` extended to cover the new result codes. +23 HTTP/RBAC tests.
- **`DoSearchAsync` async list-query path on `BasePagedListVM` (#412, Refs #193):** async EF materialization (`CountAsync`/`ToListAsync`, CancellationToken) sharing the exact query-building of sync `DoSearch` (identical results; sync API unchanged). Exposed on `IBasePagedListVM`.
- **Async batch operations on `BaseBatchVM` (#413, Refs #193):** `DoBatchEditAsync`/`DoBatchDeleteAsync`(/`Add`) mirroring the EVM-002 atomic-transaction + per-row-validation semantics (full rollback on any mid-batch failure). Sync versions unchanged.
- **Opt-in streaming export (#414, Refs #193):** `GenerateExcelToStream` (NPOI SXSSF windowed) + `GenerateCsvToStream`, gated by `UseStreamingExport` (default off), via `_FrameworkController.GetExportExcelStream`, to cut peak memory on large exports. Buffered `GenerateExcel()` unchanged; no new NuGet package.
- **Opt-in distributed `LookupCache` backend (#415, Refs #193):** `IDistributedCache`-backed provider for multi-node deployments, opt-in via `AddWtmDistributedLookupCache()`, cross-node invalidation via a distributed sentinel. Default stays in-memory; no Redis/3rd-party dependency (host plugs `IDistributedCache`).
- **Shared email notification service `IWtmEmailService` (#421, Refs #193):** opt-in SMTP sink (`System.Net.Mail`, no new package) via `AddWtmEmail(...)`; default `NullEmailService` no-op. The per-send transport is disposed deterministically.
- **Opt-in config eager validation (#422, Refs #193):** `AddWtmConfigValidation()` uses `ValidateOnStart` to fail-fast at startup on invalid connection-string / JWT options (JWT validated via the bound `IOptions<Configs>`; gated so apps not using JWT are unaffected). Default = no validation (unchanged).
- **SMS notification seam `ISmsSender` (#424, Refs #193):** opt-in seam + `NullSmsSender` default via `AddWtmSms()`; cloud providers (Aliyun/Tencent) intentionally not bundled — hosts plug their own. No new package.
- **Grid server-side aggregate footers (#431, Refs #193):** opt-in per-column Sum/Avg/Count/Min/Max computed over the **full filtered query** (not just the page), surfaced in the LayUI footer; back-compatible grid-data response. Default per-page `ShowTotal` unchanged.
- **Typed grid columns incl. per-row multi-currency (#432, Refs #193):** opt-in Progress / Tag / Image / Currency column types. **Currency is data-driven per row** (`CurrencyCodeField` → each row formats by its own ISO code via `Intl.NumberFormat`, 3-letter-ISO guarded), so a list can mix TWD + foreign currencies (travel-expense case); fixed single-currency fallback retained. XSS-safe (field names JS-encoded, values `ff.EscapeText`/`Number()`-coerced).
- **Import inline-error endpoint (#433, Refs #193):** the framework import path now surfaces `BaseImportVM.InlineErrors` (capped by `InlineErrorLimit`) + a `ValidateOnly` dry-run, back-compatibly (additive response fields).
- **Dashboard scheduled-snapshot delivery sinks (#438, Refs #193):** opt-in `IDashboardSnapshotSink` + built-ins — FileSystem (path-traversal-guarded), Email (attach via `IWtmEmailService`), Webhook (summary card). Wired into `DashboardSnapshotHostedService` with per-sink isolation; no sink registered = log-only (unchanged default).

### Security

- **Opt-in upload validation seam — `IUploadValidator` AV/policy hook (#407, Refs #193):**
  A new opt-in seam blocks unsafe uploads before any file is persisted.
  Three new `FileUploadOptions` fields activate the built-in `ExtensionContentTypeUploadValidator`:
  - `AllowedExtensions` — allowlist of permitted file extensions (e.g. `".png"`, `".pdf"`); empty = allow all (default).
  - `AllowedContentTypes` — allowlist of permitted MIME types (e.g. `"image/png"`); empty = allow all (default).
  - `MaxUploadBytes` — per-upload byte cap; `0` = unlimited (default).

  A new `IUploadValidator` DI seam (in `WalkingTec.Mvvm.Core.Support.FileHandlers`) enables custom validation (AV scanning, magic-byte inspection, etc.).  Register via `services.AddScoped<IUploadValidator, MyScanner>()` after `AddWtmContext`.  The default `NoOpUploadValidator` preserves existing accept-all behaviour.  Validation is enforced in `_FrameworkController.Upload`, `UploadImage`, `UploadForLayUIRichTextBox`, and `UploadForLayUIUEditor` before any file is written to storage.

  **Migration / opt-in:** no configuration change required for existing apps.  Set any of the three new `FileUploadOptions` fields to restrict uploads, or replace the validator via DI.

- **MVC-008 residual — horizontal password-reset in reference demos (#405, #411, Refs #193):** the SPA demos (Vue/Vue3/React/Blazor + the Razor API controller, #405) and the Razor MVC POST handler (#411) resolved password-reset ownership from the **body-supplied** ITCode, so a `UserManagement`-role user could reset any user's (incl. admin's) password by passing a different entity ID. The guard now resolves the target ITCode from the **DB by entity ID** (server-side identity), not the request body — not bypassable. Non-admins may reset only their own password.

### Fixed

- **i18n fallback hardening (#423, Refs #193):** empty/null `Configs.SupportLanguages` no longer yields an empty supported-cultures list (NRE-safe); an explicit fallback culture is set so requests for an unconfigured culture degrade gracefully. Behaviour for configured cultures is unchanged.

## [10.12.5] - 2026-06-19

WorkFlow engine transaction-safety campaign completed (the #320 follow-up cluster) + LayUI audit epic (#330) closed. All fixes adversarially verified; no behaviour change for correct usage.

### Security

- **LayUI grid `SetFormat` columns — opt-in HTML-encoding (stored-XSS hardening) (#387):** custom `SetFormat` cell renderers emitted `d.{field}` unencoded. A new opt-in `EncodeFormat` column flag (`SetFormatEncode<T>()`) wraps the formatted value in `ff.EscapeText(...)`. Default behaviour is unchanged (opt-in); enable per-column where the formatted field carries user-controlled data.

### Fixed

- **WorkFlow `StartAsync` not transaction-wrapped (#357):** the initial claim + first-node activation now share one transaction, so a crash mid-start can no longer strand a `Running` instance with no active node. Idempotent replay re-reads inside the tx; CAS-loser returns `AlreadyHandled`.
- **WorkFlow `WithdrawAsync` lock-order inversion + non-atomic (#358):** withdraw now acquires `{ApprovalTask, NodeInstance}` before `ProcessInstance` under one transaction (canonical Task→Node→Instance order), removing a deadlock vector against the advance path and closing its partial-commit window.
- **WorkFlow mid-chain `AutoApprove` latent hang (#361):** each loop iteration's node-activate + pointer-CAS is wrapped per-iteration (rollback-on-CAS-loss → no orphan-Pending node); the loop bound is clamped to `MaxSteps = 200` so the `int.MaxValue` FailClose sentinel for `TotalRequired` cannot produce an effectively unbounded loop.
- **WorkFlow Any-mode concurrent-approval double instance-completion (#401):** `AdvanceCoreAsync`'s "no active tokens" drain-loop branch returned `InstanceApproved` to a concurrent **loser** (and on a `Rejected` instance) without winning any CAS, so two concurrent approvers in an Any (或签) node could both report `InstanceApproved`. It now returns `AlreadyHandled`; the genuine completer still returns `InstanceApproved` via the token-processing path. Controllers map both codes to `200 OK` and the completion notifier gates on `InstanceApproved`, so the loser's duplicate notification is correctly suppressed. Fixes an intermittent CI flake (`Any_TCONC1`); adds a regression test.

### Added

- **WorkFlow standing strand-reaper (#359):** `WorkflowTimerExecutor.RunTickAsync` gains a 4th, **default-ON** phase that re-drives `Running` Sequential nodes stranded by the timer/system auto-approve path (task at `SequencePointer` is `AutoApproved`/`AutoRejected` while `SequencePointer < TotalRequired`). This is the durable recovery backstop for the residual partial-commit window the batched timer-claim model cannot make per-task-atomic. Re-drives through the audited `SystemContinueTaskAsync` (RowVer + ApproverSetEpoch + SequencePointer CAS), is idempotent, tenant-scoped, deadlock-safe (Task→Node→Instance), and skips healthy/in-flight nodes. **Opt-out** via `WorkFlowOptions.StrandReaperBatchSize = 0` (default `50`). Also hardens the last-step pointer-advance CAS in `ExecuteApproveCompletionAsync` with the `SequencePointer` guard the mid-chain CAS already carried (consistency under concurrent re-drive). Adds SQLite crash + concurrent-tick idempotency tests.

### Changed

- **`WorkFlowOptions.StrandReaperBatchSize` (new, default 50):** controls the per-tick strand-reaper batch size; set to `0` to disable Phase-4. The reaper only acts on already-stranded instances, so normal workflow behaviour is unchanged.

### Refactored

- **LayUI `DataTableTagHelper.Process` god-method decomposed (#348):** split into four focused builders (`BuildWhereFilter` / `BuildColumns` / `BuildToolbarButtons` / `BuildTableOptionsScript`); eliminated two hidden instance-field mutations (`hasButtonGroup` → `ref` parameter; `NeedShowTotal` → returned + OR-combined). Behaviour-preserving (adversarially reviewed); #387's `encodeFormat` parameter preserved.

### Improved

- **LayUI maintainability tail (#353):** `framework_layui.js` implicit-global/teardown cleanup, `Abstraction` dead-code removal, and source-level test-isolation hygiene (no behaviour change).

### Notes

- Closes the #320/#357/#358/#361/#369/#373/#401/#359 WorkFlow transaction-safety campaign and the #330 LayUI audit epic.
- The `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 `NU1903` (#393) remains a tracked, no-upstream-fix exception (see [10.12.4] Known Issues); unchanged by this release.

## [10.12.4] - 2026-06-19

Stability audit (adversarially verified): 17 confirmed defects across Core / Mvc / Etl / Analysis, fixed in 7 PRs (#383–#390, issues #376–#382). No CRITICAL; no behaviour change for correct usage except the two migration notes below.

### Known Issues

- **`SQLitePCLRaw.lib.e_sqlite3` 2.1.11 — `NU1903` (GHSA-2m69-gcr7-jv3q, HIGH), no upstream fix available (#393).** The bundled SQLite native engine carries a HIGH-severity advisory; it is pulled transitively into every project via `Microsoft.EntityFrameworkCore.Sqlite` (the WTM SQLite DB provider). The advisory's affected range is `<= 2.1.11` with `first_patched: None` — 2.1.11 is the latest published and the newest EF Core Sqlite (10.0.9) still resolves it, so **there is no version to pin or bump to yet**. This is **not** introduced by this release (present since the SQLite provider was added; the advisory is newly disclosed) and is distinct from the project's *fixable*-NU1903 release gate. **Exposure** is limited to applications that use the SQLite provider **and** open untrusted `.db` files or execute untrusted SQL; deployments on SQL Server / PostgreSQL / MySQL / Oracle resolve the transitive but never exercise the engine. **Action:** tracked in #393; `SQLitePCLRaw` will be bumped the moment a patched bundle ships. Applications that do not use SQLite can ignore the warning or exclude the transitive.

### Security

- **CORS reflect-any-origin + credentials removed from the `_donotusedefault` fallback (#377):** the fallback policy combined `SetIsOriginAllowed(_ => true)` with `AllowCredentials()` — a configuration browsers reject per the CORS spec and a credential-exposure footgun. `AllowCredentials()` is now only applied on the explicit-policy path (`CorsOptions.Policy` with a `Domain` list).
- **ETL dry-run no longer logs the source connection string / DB password (#376):** `EtlPipelineExecutor.ExecuteDryRunAsync` removed the raw `SourceConnectionString` Serilog parameter; `QueryTemplate` and the catch-block `ex.Message` are now passed through `EtlErrorSanitizer`.
- **REST source next-link scheme re-validation (#376):** `RestEtlSource` NextLink pagination cursors are re-checked for scheme on every page, rejecting HTTPS→HTTP downgrades that would leak `Authorization`/secret headers (complements the existing DNS-pin / private-IP block).
- **Analysis saved-query tenant isolation (#380):** `AnalysisSavedQuery` is now `ITenant`; `ListSavedQueries`/`GetSavedQuery`/`DeleteSavedQuery` enforce tenant scope (cross-tenant returns `Forbid()`/404), closing a shared-DB cross-tenant leak of public saved-query definitions and owner ITCode.

### Changed

- **CORS migration (#377):** callers that relied on wildcard-origin **credentialed** CORS via the fallback (no `CorsOptions.Policy` configured) must migrate to an explicit `CorsOptions.Policy` entry with a `Domain` allowlist (see `AddWtmCrossDomain`). The explicit-policy path retains `AllowCredentials()`. Non-credentialed wildcard CORS is unaffected.
- **Excel import — Text-column formula evaluation removed (#381):** imported Text cells beginning with `=` are no longer evaluated as formulas (e.g. `=1+1` is preserved as the literal `=1+1` instead of being silently stored as `2`). If you relied on import-time formula evaluation, pre-compute the values before import.

### Migration

- **`AnalysisSavedQuery` schema (#380):** a new nullable `TenantCode NVARCHAR(50) NULL` column is added. New deployments are handled automatically by `EnsureCreated`. Existing **multi-tenant** deployments managed by explicit EF migrations must add it: `ALTER TABLE AnalysisSavedQueries ADD TenantCode NVARCHAR(50) NULL`. Nullable → backward-compatible; single-tenant deployments are unaffected.

### Fixed

- **Multi-tenant cache-miss NRE (#377):** `SetTenantGetFunc` no longer throws (HTTP 500 on every tenant-resolving request after cache expiry) when no `default`-keyed connection is configured (null-guarded, OrdinalIgnoreCase).
- **WTMContext `_remotetoken` sync-over-async (#378):** `EnsureLoginUserInfoAsync` now pre-resolves the `_remotetoken` SSO branch asynchronously, eliminating a ThreadPool-starvation window where the synchronous `LoginUserInfo` getter blocked the request thread for remote-token deployments.
- **Orphaned DbContext leak (#378):** `DoLoginAsync` disposes the default `DataContext` created during tenant resolution before replacing it.
- **MSSQL bulk-loader schema-qualified staging (#376):** schema-qualified staging/target names are per-part quoted (`[schema].[table]`) across all DDL/DML + `SqlBulkCopy.DestinationTableName`, fixing a bug where MERGE read an empty staging table.
- **`_FrameworkController` unguarded dereferences (#379):** `Error()` no longer NREs on direct anonymous access (returns 400); `GetExportExcel` returns 400 for unresolvable VM names instead of an unhandled 500.
- **PivotExport unbounded-filter DoS (#380):** `PivotExport` now enforces the `MaxFilterClauses` cap like the other analysis endpoints, preventing a process-fatal `StackOverflowException` from a crafted oversized filter list.
- **Analysis HAVING null-aggregate filter (#381):** a null aggregate matching a `NotEq` HAVING filter no longer skips subsequent HAVING filters (AND semantics restored; `TotalCount`/grand-total no longer inflated).
- **Excel import error-report NRE (#381):** `GetErrorJson` no longer NREs on uploads containing physical row gaps.
- **`PropertyHelper` indexer NRE (#381):** `GetPropertyName` no longer NREs on property-indexed / nested-indexer lambdas on the grid/sort hot path.
- **Query-filter root-type detection (#382):** global tenant / soft-delete query filters are applied on the EF metadata root, so a multi-level concrete inheritance hierarchy (`Child : ConcreteParent : BasePoco`) is correctly filtered (previously the leaf could escape the tenant/soft-delete filter).
- **Sync `DoDelete` robustness (#382):** uses an idempotent form-context assignment and wraps `SaveChanges` in `DbUpdateException` handling, matching `DoDeleteAsync`.

## [10.12.3] - 2026-06-16

### Fixed

- **WorkFlow #373 — residual permanent-strand window on Sequential approve/reject (follow-up to #320).** The v10.12.2 #320 fix wrapped the intra-transaction advance windows, but on the human **Sequential** approve and reject paths the task-claim CAS still committed *standalone, outside* the advance transaction. A crash / cancel / connection-drop between the claim commit and the advance commit left the instance permanently stranded — a terminal-state task at the `SequencePointer`, the `NodeInstance` still `Activated`, the `ProcessInstance` still `Running`, and zero actionable `Pending` tasks — with no recovery path (retried action → `AlreadyHandled`; next assignee → guard-rejected; `AdvanceAsync` → `Blocked`; the timer reaper only reclaims `Returning` instances). The Sequential human approve (mid-chain + last-step) and reject claim CAS now execute as the **first write inside the same transaction** as the next-task activation + pointer-advance (approve) or sibling-cancel + node-completion + instance-flip (reject), so claim and advance share fate: any crash before commit rolls the whole step back to a clean, re-drivable state. Canonical **Task-before-Node** lock order is preserved (a strict `{ApprovalTask, NodeInstance[, ProcessInstance last]}` set, so it cannot reintroduce the #290/#311 ABBA deadlock), the transactions are wrapped in the established `RunWithDeadlockRetryAsync` envelope, and the audit `WorkflowEventLog` append stays outside the transaction (post-commit). No change to happy-path behaviour, the All/Any approval modes, or the timer/system auto-approve path. The remaining timer/system-claim and other-entry-point windows are tracked as follow-ups (#359 / #360). Adds 8 WorkFlow regression tests including fault-injected crash-window rollback proofs and a Task-before-Node lock-order structural assertion.

## [10.12.2] - 2026-06-16

### Security

- LayUI server-side XSS/encoding hardening (#331): HtmlEncode dynamic values in ColorPicker, LayuiUIService makers (MakeCheckBox/MakeRadio/MakeTextBox/MakeCombo/MakeDateTime); JavaScriptEncoder for ItemUrl/RemoteUrl/TriggerUrl in ComboBox/Radio/CheckBox; GridAction DialogTitle+Url JS encoding; Transfer selectVal via JsonSerializer; Slider DefaultValue numeric validation + field name JS encoding.
- **#332 — `framework_layui.js` XSS / open-redirect / code-exec hardening** (PR #341): seven client-side security fixes to the LayUI framework JavaScript:
  - **Reflected XSS (HIGH)**: `layer.alert(request.responseText)` / `layer.alert(xhr.responseText)` in PostForm, BgRequest, OpenDialog, and OpenDialog2 error handlers now wrap server error bodies with `ff.EscapeText(...)`. Raw server HTML is no longer concatenated into layui's innerHTML-based alert dialog.
  - **Stored XSS (HIGH)**: `ChainChange` and `LoadComboItems` checkbox/radio rendering replaced string-concatenated `<input>` markup with `ff._makeInput(type, name, value, title, checked, disabled)` — a new DOM-API helper that sets attributes via property assignment. `item.Value` / `item.Text` cannot break out of attribute context.
  - **Open redirect**: `OpenDialog` error handler now validates the server `Location` response header before `window.location` assignment — rejects absolute URLs, protocol-relative `//`, `javascript:`, and `data:` with `console.warn`, mirroring the `DispatchAction` redirect guard (#804).
  - **Stored XSS in OpenDialog2 (HIGH)**: AJAX response is now passed through `ff.SafeHtml(str)` (DOMPurify) before the `$$SearchPanel$$` replacement. Grid-id extraction replaced brittle full-document regex with `document.createElement` + `querySelector('table[lay-filter]')` on the sanitized subtree. The `$$script$$` / `$$#script$$` rehydration path is unchanged — it operates on the trusted local `tempId` template, not the server response.
  - **HTML injection in `ff.Download`**: form and hidden inputs now built via `document.createElement` / property assignment instead of `$('<form … action="' + url + '">')` string concatenation.
  - **Arbitrary code execution in `RefreshChart` (code-exec)**: `JSONfns.parse(data.series)` (which deserialized function literals) replaced with `JSON.parse` as the safe default. Function-typed series remain possible via an explicit opt-in: set `window[chartId + 'ChartSeriesParser']` to a trusted parser function before calling `RefreshChart`.
  - **Low: `layui.layer` scope bug in error branches** — stray `layer.alert(...)` calls in `ChainChange` and `LoadComboItems` error branches (where `layer` is not in scope) corrected to `layui.layer.alert(...)`.

- **WorkFlow URL-RBAC fail-open (P0, #319, PR #329)** — `PrivilegeFilter` computed an empty `BaseUrl` for attribute routes whose required parameter is not `id` (`{code}`, `{id:guid}`): `GetPathByAction` returned null and `IsAccessable("")` failed OPEN, so any authenticated tenant user could publish workflow graphs, save/delete drafts, and change definition metadata without the workflow privilege. The gate URL is now derived from the action's attribute-route template (route constraints stripped) so it matches the registered `WorkflowPrivileges` menu and fails CLOSED via `FindMenu`; a defense-in-depth guard denies any gated, authenticated action whose URL is unresolvable. **Migration:** the WorkFlow definition/designer write endpoints now correctly require the workflow privilege — register the `WorkflowPrivileges` menus and grant them to the appropriate roles for those endpoints to be accessible.
- **WorkFlow webhook notifier escaping** (#326, PR #339): user/graph-authored strings (ITCodes, node keys, business keys) placed into `WebhookMessage.Fields` values are now escaped before reaching the Markdown card sink — completes the #296 hardening and closes a phishing-link / Markdown-injection vector into Slack/DingTalk/WeCom/Feishu/Teams cards.

### Changed

- **`OpenDialog2` content sanitization behavior** (migration note): server HTML responses to `OpenDialog2` AJAX calls are now sanitized via DOMPurify before display. Content that was previously rendered raw — including `<script>` tags, inline event handlers (`onerror`, `onload`, `onclick`, etc.), and `<style>` blocks — will be stripped. Legitimate grid-init scripts should be delivered via the `$$script$$` / `$$#script$$` token mechanism in the search-panel template (unchanged behavior). If a page previously relied on raw `<script>` tags in the server response to `OpenDialog2`, migrate to `X-WTM-Action: application/json` (FFResultJson) or the `$$script$$` rehydrate path.

- **`RefreshChart` function-series opt-in** (migration note): `JSONfns.parse` is no longer called by default in `RefreshChart`. The default parser is now `JSON.parse`, which does not support function literals in JSON. If your chart series definitions include function-typed properties (e.g., custom `formatter` callbacks serialized as strings), register a trusted parser before rendering: `window['myChartChartSeriesParser'] = JSONfns.parse;` (or your own safe deserializer). Setting this registry key is a deliberate opt-in; apps that do not set it get the safer `JSON.parse` behavior automatically.

### Added

- **#338 — Dark-mode / theme hook** (`wtmTheme` in `framework_layui.js`, `DefaultThemeClass` on `WtmUIOptions`): opt-in theme class applied to `<body>` on load, toggled and persisted to localStorage. Default off.
- **#338 — DataTable client CSV export** (`EnableClientExport`, `ExportFileName` on `DataTableTagHelper`): injects `'exports'` into the layui `defaultToolbar`; exports currently loaded rows to CSV. Default off; server Excel export unchanged.
- **#338 — maxlength + char counter** (`TextBoxTagHelper` emits `maxlength` from `[StringLength]`/`[MaxLength]`; `TextAreaTagHelper` adds opt-in `ShowCounter` for a live N/Max counter span). Default off.
- **#338 — `wt:rate` and `wt:taginput` TagHelpers** (`Form/RateTagHelper.cs`, `Form/TagInputTagHelper.cs`): opt-in Layui `rate.render` and `tagInput.render` wrappers deriving from `BaseFieldTag`; values submitted as hidden inputs. Default off.
- **#270 — env-var-gated live-provider CAS conformance harness** (`ProviderConformanceHelper`): the `T_PROV_*` tests in `ConcurrencyConformanceTests_LiveDb` are no longer unconditional stubs. When `WTM_WF_LIVE_PROVIDERS` is not set the tests remain `Inconclusive` (CI-safe, default behavior unchanged). When `WTM_WF_LIVE_PROVIDERS=1` is set, the harness activates: missing per-provider connection string (`WTM_WF_CONN_SQLSERVER`, `WTM_WF_CONN_POSTGRES`, `WTM_WF_CONN_MYSQL`, `WTM_WF_CONN_ORACLE`, `WTM_WF_CONN_DAMENG`) → `Assert.Fail` with a clear message; missing driver assembly → `Assert.Fail`; unreachable host → `Assert.Fail`; reachable host → real guarded-CAS concurrent-race check (5 rounds, 2 concurrent UPDATEs, asserts exactly 1 winner + 1 loser and final RowVer=1). Nightly CI containers and DaMeng deadlock-code confirmation remain deferred to a follow-up infrastructure issue.
- (opt-in) `EnableAutoVerify`: projects DataAnnotations to LayUI `lay-verify` tokens automatically in `BaseFieldTag` (see `WtmUIOptions.EnableAutoVerify`)
- (opt-in) `EnableAria`: emits ARIA attributes (`aria-label`, `aria-describedby`, `aria-invalid`) for WCAG 2.1 SC 1.3.1 / 4.1.2 compliance in `BaseFieldTag` (see `WtmUIOptions.EnableAria`)

### Improved

- **LayUI TagHelpers perf/allocation** (#334): cache-once optimizations to reduce per-request allocations in the hot TagHelper render path:
  - `DataTableTagHelper`: `JsonSerializerOptions` promoted to `private static readonly` (avoids per-render allocation, item 1)
  - `DataTableTagHelper`: `fieldPre` result memoized in a nullable backing field — computed once per tag, not per access (item 13)
  - `DataTableTagHelper`: `generateColHeader` replaced 3 Where/ToArray passes with a single-pass partition into 3 pre-allocated lists — O(n) instead of O(3n), fewer allocations (item 6)
  - `DataTableTagHelper`: `prop.GetValue(ListVM.Searcher)` second call replaced with the already-computed `listvalue` local (item 9)
  - `TransferTagHelper`: `JsonSerializerOptions` promoted to `private static readonly` (item 2)
  - `BaseFieldTag`: `FormFieldAttribute` reflection lookup cached in a `ConcurrentDictionary<MemberInfo, FormFieldAttribute?>` — hot path on forms with many fields (item 3)
  - `SelectorTagHelper`: 4 per-render `Regex` instantiations replaced with `private static readonly` compiled fields; two identical patterns collapsed to one shared field (item 4)
  - `SelectorTagHelper`: `GetSingleProperty(TextBind.PropertyName)` hoisted outside the entity loop — constant per render (item 11)
  - `TreeContainerTagHelper`: 2 constant `Regex.Replace` calls promoted to compiled static fields; r3/r patterns likewise; runtime-gridid pattern uses `RegexOptions.Compiled` (item 5)
  - `TreeTagHelper` + `TreeContainerTagHelper`: `.Count()>0` replaced with `.Any()` / `.Any(predicate)` (item 12)
  - `LayuiUIService.MakeCombo`: replaced `rv +=` string concatenation in option loop with `StringBuilder`; `.ToLower()` comparisons replaced with `string.Equals(..., OrdinalIgnoreCase)` (item 7)
  - `MultiUploadTagHelper`: manual JSON array build replaced with `JsonSerializer.Serialize(...)` (item 8)
  - `DisplayTagHelper`: LINQ closure replaced with pre-computed local string to reduce closure allocation (item 14)
  - `LayuiUIService`: marked `sealed` — no subclasses exist, enables JIT devirtualization (item 15)
  - Items 10 and 16 skipped: item 10 (td background via script→style) would change visible rendering; item 16 (hasButtonGroup/NeedShowTotal instance state) requires large god-method restructuring, tracked for separate refactor.

### Fixed

- **LayUI TagHelpers** (Issue #333): 11 functional defects
  - `SwitchTagHelper`: local `Checked` variable now seeded from the public property — fixes `checked="true"` being ignored when model is null
  - `SliderTagHelper`: guard on `arr.Length` before indexing — prevents `IndexOutOfRangeException` for single-element (`[5]`) and empty (`[]`) bracket `DefaultValue`
  - `DataTableTagHelper`: caller-supplied `Filter` dictionary is no longer mutated — prevents `ArgumentException` on re-render; internal `where` dict is used instead
  - `DataTableTagHelper`: `ListVM == null` now throws `InvalidOperationException` with descriptive message instead of opaque NRE
  - `DataTableTagHelper`: `removeClass("layui-form-selected")` — removed stray space that made the call a no-op
  - `BaseFieldTag`: early null guard on `Field` — throws `InvalidOperationException` with clear message when `field=` attribute is missing
  - `RadioTagHelper` / `CheckBoxTagHelper`: `item.Value` null guard in `SetSelected()` — prevents NRE when a list item has a null `Value`
  - `UEditorTagHelper`: persisted model value now wins over `DefaultValue` (previously inverted)
  - `SelectorTagHelper`: display mode now falls back to computed enum display name when the entity text list is empty
  - `clearSelector` (JS): container selector now uses `id` parameter variable, not literal `"id"` string — fixes silent no-op for named selectors
  - `GetNonSelections` (JS): removed stray `invalidNum++` reference — prevents `ReferenceError` under strict mode when the table cache contains Array-constructor rows
- **WorkFlow 回退-to-node no longer strands the workflow** (#322, PR #344): the returned-to node is driven through activation so it reaches `Activated` and its `ApprovalTask` rows are materialized (previously it sat `Pending` with no tasks — the workflow hung).
- **WorkFlow 加签 onto 会签/或签** (#324, PR #344): runtime-injected approvers on `All`/`Any` nodes are now created `Pending` (immediately actionable) instead of `AddedPending`, so the node's threshold stays reachable.
- **WorkFlow timer reaper multi-tenant** (#325, PR #343): lease-reclaim (Phase-2) and AtAction delegation sweep (Phase-3) set per-tenant `DataContext` context before their tenant-filtered guarded-CAS writes, so they no longer silently no-op for non-default tenants.
- **WorkFlow routing predicate cache key** (#323, PR #340): now includes the field whitelist `clrType`, preventing byte-identical rule JSON across graphs/tenants from reusing the wrong type coercion.
- **WorkFlow terminal Approve/Reject audit atomicity** (#321, PR #345): `End→Approved` / `Running→Rejected` state change and its authoritative `WorkflowEventLog` row now commit in one transaction (ordering-neutral).
- **WorkFlow #320 — complete transaction-boundary hardening** (P0 data-corruption; PRs #365 #366 #368 #369 #370 #371): the approve/reject/advance happy-paths previously committed node-completion, successor-mint, pointer-advance, task-activation and instance-flip as SEPARATE auto-committed statements (spec §7.3 violation) — a crash/cancel/connection-drop between commits could permanently strand an instance (Running with an Activated node and zero actionable tasks). Every such window is now wrapped in a single canonical-lock-order (Timer→Task→Node→Instance) transaction, each verified deadlock-safe against the #290 ABBA fix and adversarially reviewed:
  - **#365** advance — complete-source+mint-successor (W1) and End-complete+instance-Approved (W2) atomic;
  - **#366** reject (Sequential/会签/或签) — task-cancels+node-Rejected+instance-Rejected atomic; advisory `RejectedCount` kept standalone so non-failing rejects survive;
  - **#368** Sequential mid-chain — pointer-advance+next-task-activation atomic (the canonical #320 stranding case);
  - **#369** ReturnToInitiator — cancel+node-Returned+instance-Draft atomic;
  - **#370** parallel-Join — branch-arrival+fire+successor-mint atomic, exactly-once fire preserved, orphan-decrement loop moved post-commit;
  - **#371** 会签/或签 — claim+advisory-increment atomic (committed before the downstream drain → no nested transaction).
  State-flip CAS runs before the audit `AppendAsync` (avoids a stale-RowVer self-strand); timers/notifications stay post-commit. 23 new crash-window/atomicity regression tests. **Migration:** none — the success-path behaviour is unchanged; only crash-atomicity improves. Remaining entry-point wraps (`StartAsync` #357, `WithdrawAsync` #358) and the timer/system auto-action path (#359) are tracked as follow-ups; the pre-existing Sequential mid-chain AutoApprove latent hang is #361.
- **WorkFlow correctness/quality** (#327, PRs #352 #354): `NodeInstance.ApprovePercent` mapped `HasPrecision(5,4)` (prevents ratio-quorum truncation on SqlServer/MySQL/Oracle); `All`/`Any` approval completion stamps `NodeInstance.DecidedBy`; Sequential reject cancels 加签 `AddedPending` tasks; guarded node-mint catch narrowed to UNIQUE/duplicate-key only (FK/NOT NULL/CHECK now surface); publish rejects duplicate JSON property names; WorkFlow controllers carry `[ActionDescription]` so their privileges register in the catalog (completes #319's RBAC).
- **WorkFlow audit MED batch 3** (#327, PR #356): C13 — AtAction `RevertToPrincipal` now pre-checks the `(NodeInstanceId, AssigneeITCode, Generation)` unique index before reverting, ending an infinite per-tick reaper retry loop when the principal was already 加签'd onto the node; C14 — delegation provenance now makes a direct approver win over a delegated chain regardless of resolution order (audit-accuracy; code now matches its comment).
- **.NET 10 Razor build fixes** (#362 PR #363; #364 PR #367): `Selector.cshtml` and the demo views used capital `<Text>` / unclosed tags that the stricter .NET 10 Razor SDK rejects (RZ1021/RZ1026/CS1525) — this broke the Mvc project + `dotnet pack` release path (#362) and the full-solution build (#364). Rewritten to `@Html.Raw` / lowercase `<text>` / self-closing tags; rendered output unchanged. `dotnet build WalkingTec.Mvvm.sln` is green again.

## [10.12.1] - 2026-06-13

Patch release — correctness and quality fixes to the `WalkingTec.Mvvm.WorkFlow` engine. No new opt-in surface, no schema change, and no behavior change for hosts that do not call the affected code paths.

### Fixed

- **#310 (WF-290.2) — root-fix the residual Delegate-vs-AddApprover (Node,Task) ABBA deadlock cycle**: `AddApproverAsync` now acquires ApprovalTask (SequenceOrder shift + INSERT) before NodeInstance (`AddApproversToNodeAsync`), matching `DelegateTaskAsync`'s Task→Node order. All human multi-row transactions now share one total lock order: `ApprovalTask → NodeInstance → ProcessInstance(Seq)`. The C-backstop retry envelope (from #290) is retained as defense-in-depth. No behavior change on SQLite or any existing non-deadlock path. (#310)
- **#290 — resolve ABBA deadlock between 回退 (return-to-node) and 委托/加签 (delegate/add-approver) on server DB providers** (PR #311): `ExecuteReturnToNodeAsync` now acquires `ProcessInstance` last, matching the canonical lock order (`WorkflowTimer → ApprovalTask → NodeInstance → ProcessInstance(Seq)`). The fix commits the `Running→Returning` linearization point (STEP-1) in its own transaction (txA) before touching timer/task/node rows (txB), closing the Delegate-vs-Return, AddApprover-vs-Return, and reaper-escalate-vs-Return cycles. A compensating roll-forward in the txB catch block makes crash recovery prompt instead of lease-expiry-delayed. Additive opt-in deadlock-victim retry envelope (`WorkFlowOptions.DeadlockRetryAttempts`, default 3; `WorkFlowOptions.DeadlockRetryBaseDelay`, default 20 ms) backstops the residual delegate-vs-add-approver cycle (tracked as WF-290.2) until lock-order unification lands. `Db.ChangeTracker.Clear()` is called between retry attempts to prevent stale EF tracked-entity duplication. No behavior change on SQLite; no isolation-level dependency; Returning-lease crash recovery unchanged. Note: full deadlock-free proof on server providers (SqlServer/PgSql/MySql/Oracle/DaMeng) is deferred to #270 (live-provider conformance, hardware pending); the retry backstop makes any real deadlock harmless via idempotent retry.
- **#299 — `WorkflowGraphValidator` rejects unsupported `schemaVersion` on new publishes** (PR #309): graphs with `schemaVersion < 1` or `> CurrentSchemaVersion` are now rejected at publish time with `GraphValidationError.SchemaVersionUnsupported`. The `CurrentSchemaVersion` constant is introduced for use by designer and validator alike. Existing published versions and in-flight instances are unaffected.
- **#307 — widen concurrency loser-outcome assertions to accept `NodeClosed`** (PR #308): test-only CI stability fix; production behavior unchanged.

## [10.12.0] - 2026-06-13

WorkFlow Wave 6 — **低代码设计器 (WF-21)** for the `WalkingTec.Mvvm.WorkFlow` approval engine, plus security hardening (#296) and an embedded-resource fix (#297). All new authoring surface is fully opt-in — hosts that do not call `AddWtmWorkFlowDesigner()` / `UseWtmWorkFlowDesigner()` are byte-identical to 10.11.0. The security fixes (#296, #297) ship unconditionally and harden already-published graphs as well as new ones.

Compliance defaults are enforced loudly everywhere they touch the new surface: designer endpoints return 404 (not 500) when the designer is not registered; `schemaVersion != 1` graphs are rejected at publish time with a closed error code; antiforgery is required on every mutating designer endpoint; all graph-authored strings are escaped at the notifier sink regardless of publication date.

### Added

- **WF-21 低代码工作流设计器 — `AddWtmWorkFlowDesigner()` + `UseWtmWorkFlowDesigner()`** (#300, #303): opt-in visual designer at `/_workflow-designer` for authoring and publishing `ProcessDefinition` graphs without writing raw JSON. Key properties:
  - **Eval-free, no-CDN, embedded assets**: three hand-written IIFE modules (`framework_workflow_designer_core.js`, `framework_workflow_designer_forms.js`, `framework_workflow_designer_view.js`) served from the WorkFlow assembly's embedded resources; no `eval` / `new Function`, no third-party CDN. `'unsafe-eval'` is not required.
  - **Three views**: **表单视图** (per-NodeKind property panels for all 9 node kinds, built as DOM nodes — no `innerHTML`; transitions table with per-edge condition editor; field-whitelist editor); **源码视图** (raw-JSON textarea with validate / pretty-print); **图形视图** (read-only SVG auto-layout diagram, BFS rank-from-Start, `createElementNS` + `textContent` only).
  - **Raw-bytes fidelity**: graph documents travel on a raw-body path that bypasses typed MVC binding (unknown fields survive; exact number literals survive via `WtmJsonRaw` lossless-number codec). Byte-identical no-op saves produce the same `ContentHash` → `IdempotentNoOp` (T-DSN-1 mandatory).
  - **Server drafts** (`ProcessDefinitionDraft` — **new table**, consumer migration required): one draft per `(TenantCode, DefinitionId)`; If-Match / If-None-Match RowVersion concurrency; draft deleted in-transaction on publish; post-publish resurrection guard (`If-Match` under an already-published head never creates on miss).
  - **Publish CAS** (`expectedBaseContentHash` in `X-WTM-WF-Expected-Hash` header): server validates inside the existing publish transaction — mismatch → HTTP 409 `BaseVersionChanged`; concurrent edits cannot silently supersede each other. Byte-identical content short-circuits to `IdempotentNoOp` regardless.
  - **`IProcessDefinitionPublisher.PublishRawAsync`** (Default Interface Method — third-party implementations compiled against 10.11 load without modification; the DIM throws `NotSupportedException` at call time if not overridden).
  - **RBAC**: URL-RBAC via `PrivilegeFilter` + `WorkflowPrivileges.DesignerPage` / `WorkflowPrivileges.DesignerBase` constants; deliberately stricter than `[AllRights]` — design/publish is a privileged operation.
  - **Antiforgery**: designer-scoped `IAntiforgery` (cookie + `X-WTM-WF-XSRF` double-submit); token issued by `GET bootstrap`; required on all mutating endpoints (`PUT draft`, `DELETE draft`, `POST publish`, `POST definitions`, `PUT definitions/{code}`); global `AntiforgeryOptions.HeaderName` is NOT modified.
  - **`schemaVersion` gate**: designer endpoints reject `schemaVersion != 1` with HTTP 400 `SchemaVersionUnsupported`; client form panel shows a locked banner and falls back to source + SVG views only.
  - **Dead-link repair**: `ProcessDefinitionListVM` grid actions (Details / Versions) now point at the designer page (previously linked to a nonexistent controller endpoint → 404).
  - **Head creation** (`POST /api/_workflow/designer/definitions`): closes the gap where `POST {code}/publish` would 404 on an unknown definition code; code pattern `^[A-Za-z0-9_\-\.]{1,64}$`; duplicate-in-tenant → 409.
  - Designer endpoints 404 (not 500) when `AddWtmWorkFlowDesigner()` has not been called.
  - Structural guard: embedded-asset manifest test + TestServer 200 test on every designer asset (prevents the class of bug fixed by #297).

- **SEC #296 — nodeKey charset whitelist and duplicate-key validator checks** (#296, #302): two additive `WorkflowGraphValidator` checks that fail-close NEW publishes:
  - `InvalidNodeKey`: rejects node keys that do not match `^[\p{L}\p{N}_\-\.]{1,64}$` (CJK-friendly — `\p{L}` matches 中文; blocks markdown-link injection characters such as `[](){}` at publish time).
  - `DuplicateNodeKey`: rejects graphs where two or more nodes share the same `nodeKey`.
  - **Affects new publishes only** — existing published versions and in-flight instances are unaffected (matching the Wave 5 precedent for publish-time-only validation).

### Fixed

- **SEC #296 — escape graph-authored strings at notifier sink** (#296, #302): `WebhookWorkflowNotifier` now escapes all graph-authored and user-authored strings (node keys, node names, approver display names, comment snippets) at the point they are interpolated into webhook card markdown. Previously, a published graph with a crafted `nodeKey` such as `[重新登入](http://evil)` could inject markdown links into 钉钉/企微/飞书/Slack/Teams notification cards. **The escape-at-sink fix hardens all cards, including those generated from already-published graphs — no re-publish required.**
- **#297 — `framework_dashboard_designer.js` missing from embedded resources → shipped 404** (#297, #301): the file was present in `src/WalkingTec.Mvvm.Mvc` but not listed as `EmbeddedResource` in the project file, causing a 404 on the dashboard designer page. Fixed with a one-line project file addition. A loop regression test now asserts HTTP 200 on every declared designer asset to prevent recurrence.
- **#298 — `framework_analysis` export emitted CDN `<script>` tags** (#298, #305): the Analysis panel dependency-hint banner showed `cdn.jsdelivr.net` snippets, conflicting with the no-CDN intranet deployment constraint. The hint now points at local `/_js/` framework asset paths (sortablejs was already vendored; `echarts.common.min.js` was present in the repo and is now declared as an `EmbeddedResource` — no new third-party dependency). Display-only change; no actual script loading was affected.

### Migration

**New table `Wf_ProcessDefinitionDraft`** (owned by `WalkingTec.Mvvm.WorkFlow`): required only when `AddWtmWorkFlowDesigner()` is called. The entity is registered by `ApplyWorkFlowModels()` — run your EF Core migration to create the table:

```bash
dotnet ef migrations add AddWorkFlowDesigner \
  --context DataContext \
  --project YourApp/YourApp.csproj \
  --startup-project YourApp/YourApp.csproj
dotnet ef database update
```

Schema: `DefinitionId` (FK → `ProcessDefinition`), `GraphJson` (text), `BaseContentHash` (nullable string), `RowVersion`, `LastSavedBy`, `LastSavedAt`. One draft per `(TenantCode, DefinitionId)`.

**No other schema delta**: `Wf_WorkflowTimer`, `Wf_NodeInstance`, `Wf_ApprovalTask`, `Wf_ProcessInstance`, `Wf_WorkflowEventLog`, and `ProcessDefinition`/`ProcessDefinitionVersion` are all unchanged.

**Validator tightening** (`InvalidNodeKey` / `DuplicateNodeKey`) affects **new publishes only**. Existing published versions remain readable and in-flight instances continue running without interruption. Graphs with existing node keys that violate the charset rule will be rejected on next publish — review and update those keys before republishing.

## [10.11.0] - 2026-06-12

WorkFlow Wave 4 + 5 — **加签 (add-approver)**, **委托/转交 (delegation)**, and **超时/催办 (timeout + remind)** for the `WalkingTec.Mvvm.WorkFlow` approval engine. All three features are fully opt-in — hosts that do not call the new registration methods or author `TimeoutDef` in their graph are byte-identical to 10.10.0. See Migration for additive nullable columns added to `Wf_ApprovalTask` and `Wf_NodeInstance`; **no new migration delta for `Wf_WorkflowTimer`** (schema was already in place).

Compliance defaults are enforced loudly: `AllowTimerAutoAction` defaults to **`false`** (auto-approve/auto-reject via timer requires explicit opt-in); `DelegationWindowMode` defaults to **`AtAssignment`** (authority frozen at task-mint; `AtAction` re-check is opt-in and is **startup-blocked on Oracle/DaMeng** until #270 live-provider conformance lands). Any change from these defaults must be documented in CHANGELOG.

### Added

- **加签 (add-approver) — `AddApproverAsync`** (#285): an active approver may inject additional approvers `Before` or `After` their own position in the chain. Works across all three approval modes (串签 / 会签 / 或签):
  - **会签 (All/ratio):** injected tasks are immediately `Pending`; `TotalRequired` is bumped atomically in the same `ExecuteUpdateAsync` that increments `ApproverSetEpoch` — a newly injected approver must act before the node can complete.
  - **串签 (Sequential):** `Before` inserts at the actor's sequence position (`AddedPending` state — activates when the pointer reaches it); `After` inserts immediately after.
  - **或签 (Any):** widens the candidate set; epoch bump linearizes concurrent approve+加签 races.
  - **`ApproverSetEpoch`** (new `uint` column on `Wf_NodeInstance`, default 0): incremented by every approver-set mutation; added to the completion CAS predicate — closes the threshold-recompute race window between a 加签 and an in-flight completion.
  - **`MaxAddDepth`** cap (`WorkFlowOptions.MaxAddDepth`, default 3): prevents unbounded injection chains. `ApprovalTask.AddDepth` (new additive column) tracks depth per task; O(1) check, no chain walk.
  - **`AddPosition` enum**: `Before` | `After`.
  - Injected tasks carry `Generation`, are discarded with the span on 回退, and flow through the existing delegation resolution step (standing rules are honoured for injected approvers).
  - New `WorkflowActionCode` members: `MaxAddDepthExceeded`, `NodeAlreadyDecided`.
  - Concurrency test: `ConcurrencyConformanceTests.cs` covering racing 加签 vs. completion in both orderings.

- **委托/转交 (delegation) — `DelegateTaskAsync` / `RevokeDelegationAsync` / `DelegationResolvingDecorator`** (#284, #286):
  - **Standing delegation (resolution-time substitution):** a `DelegationRule` (`PrincipalITCode`, `DelegateeITCode`, `ScopeDefinitionCode`, `StartUtc`, `EndUtc`) is applied by `DelegationResolvingDecorator : IApproverResolver` at node-entry before Activation. The decorator replaces the principal with the terminal delegatee in the resolved set — substitution, never an addition; `TotalRequired` is written once, pre-Activation, with zero concurrency.
  - **Transitive chains** (hop cap + cycle detection): up to `MaxDelegationHops` (default 3) per-chain, with an explicit `HashSet<string>` visited-set. A cycle (A→B→A) or cap breach routes to `AdminFallbackITCode`; if that is empty the engine **fail-closes** (never fail-open).
  - **Mid-flight delegation — `DelegateTaskAsync`**: an approver explicitly transfers their active `Pending` slot to a delegatee via a **single-statement CAS** on the task row (`AssigneeITCode` flip + `RowVer` + `Generation` guard). `TotalRequired` is never touched (1-for-1 slot transfer). A delegatee already holding an active task on the node is refused with `DelegateAlreadyParticipant`.
  - **`DelegationWindowMode`** (default `AtAssignment`, opt-in `AtAction`): `AtAssignment` (default) freezes the delegation window at task-mint (`DelegationExpiresUtc` denormalized on the task row) — authority is frozen at assignment time. `AtAction` re-checks at claim via the CAS predicate; **blocked at startup on Oracle/DaMeng** (ctor guard, pending #270).
  - **`RevokeDelegationAsync`**: admin bulk-reverts all `Pending` tasks produced by a given `DelegationRuleId` back to their original principals (1-for-1 CAS; `TotalRequired` never moves; idempotent).
  - New additive columns on `Wf_ApprovalTask`: `DelegationRuleId (Guid?)`, `DelegationExpiresUtc (DateTime?)`, `WindowVerifiedUtc (DateTime?)`. New additive column on `Wf_NodeInstance`: `ApproverSetEpoch (uint)`. New additive column on `Wf_ApprovalTask`: `AddDepth (int)`.
  - New `WorkflowActionCode` members: `DelegationExpired`, `DelegationHopsExceeded`, `DelegationCycle`, `DelegateAlreadyParticipant`.
  - `DelegationTests.cs`: 60+ cases covering standing rules, transitive chains, cycle detection, mid-flight reassign, collision, revoke, AtAction window, and return-span discard.

- **超时/催办 (timeout + remind + escalate) — `AddWtmWorkFlowTimers()`** (#287, #291): opt-in background service for timeout-driven actions. Call `services.AddWtmWorkFlowTimers()` in addition to `AddWtmWorkFlow()` to enable:
  - **`WorkflowTimerHostedService`**: single-instance background reaper; poll interval `WorkFlowOptions.TimerPollInterval` (default 1 min); per-tick `CreateScope`; per-timer try/catch (poisoned timer never blocks the batch); 3-attempt startup retry; **throws out of `ExecuteAsync` on `DBTypeEnum.Memory`** (fail-fast per spec invariant #8).
  - **Arm sites**: node-scoped timer (`ApprovalTaskId = NULL`) armed after `ActivateNodeInstanceAsync` for All/Any nodes; per-active-step task-scoped timer armed at activation and at each sequential pointer-advance. `NotYetActive`/`AddedPending` tasks never have running timers (structural).
  - **Remind (催办)**: default-safe. In-transaction next-link INSERT with deterministic idempotency key (`tmo:{n|t}:{id}:{gen}:{k}`) and hard cap `min(MaxReminders ?? MaxRemindersDefault(3), MaxRemindersHardCap(10))`.
  - **Escalate**: task-scoped timers only — reassigns the step via `EscalateTaskAssigneeAsync` (same assignee-bound CAS as WF-19). Collision pre-check (delegatee already a participant) downgrades to notify-only + `FailClosed` event. Node-scoped timers (All/Any): notify-only (quorum-reassign deferred).
  - **AutoApprove / AutoReject**: doubly gated — graph must author the action AND `AllowTimerAutoAction` must be `true` (default **`false`**). Fire-time authoritative: gate-off produces Remind downgrade + `FailClosed` event regardless of graph intent. Per-task CAS reuses the existing `ClaimApprovalTaskAsync` predicate; the same post-claim continuation as the human path runs post-commit.
  - **Multi-host deduplication**: `FireTimerAsync` CAS (`Status==Armed AND RowVer==@v`) is the mutex; exactly one host wins per timer per tick.
  - **`IBusinessCalendar` seam** + `PassThroughBusinessCalendar` default: `businessCalendar:true` graphs with auto/escalate actions and only pass-through registered are **rejected at publish time**; Remind arms with wall-clock math + `LogWarning`.
  - **AtAction expired-delegation sweep (reaper phase 3)**: reverts expired AtAction-mode delegated tasks to their principal when `DelegationExpiredSweep == RevertToPrincipal` (default). Doubly opt-in intersection (`AddWtmWorkFlowTimers` + `DelegationWindowMode == AtAction`).
  - **Returning-lease reclaim (reaper phase 2)**: `ReclaimReturningLeaseByRowVerAsync` — RowVer-pinned portable reclaim, no `DateTime` in UPDATE WHERE (Oracle/DaMeng safe). `ReturningLeaseTtl` option (default 30 min).
  - **New `WorkFlowOptions` options**: `AllowTimerAutoAction` (default `false`), `ReturningLeaseTtl` (default 30 min), `TimerBatchSize` (default 100), `MaxRemindersDefault` (default 3), `MaxRemindersHardCap` (default 10), `DelegationExpiredSweep` (default `RevertToPrincipal`).
  - **New `EventAction` enum members** (append-only): `TimeoutRemind`, `TimeoutEscalate`, `DelegationExpiredReverted`.
  - **`IWorkflowNotifier` +3 default interface methods** (DIM, no-op defaults): `NotifyTimeoutRemindAsync`, `NotifyTimeoutEscalatedAsync`, `NotifyTimeoutAutoActionedAsync`. `WebhookWorkflowNotifier` overrides all three; every DIM has a verified call site.
  - **`TimeoutDef.EscalateTo` graph field** (additive-nullable `string?`): target ITCode for escalate-reassign; falls back to `AdminFallbackITCode` if empty.
  - **Zero new migration columns for `Wf_WorkflowTimer`**: the timer table schema was already in place (Sprint-1); `EventAction` +3 append-only members require no schema change.
  - `DefinitionCode` additive column on `Wf_NodeInstance` (delegation scope filtering at resolution time; also read by timer arm sites).
  - Timer test matrix: 22 scenarios (`T-TMO-01…22`) in `TimerArmCancelTests.cs` and `TimerReaperTests.cs` using SQLite shared-in-memory (WF-0 barrier pattern).

### Fixed

- **`WorkflowEngine` AtAction guard on Oracle/DaMeng** (#286): `DelegationWindowMode.AtAction` is now blocked at `AddWtmWorkFlow()` ctor time on `DbType ∈ {Oracle, DaMeng}` with a clear `NotSupportedException` referencing issue #270. Previously the mode could be set silently and would fail at runtime with a translation error.

### Migration

The following columns are **additive** (`HasDefaultValue` in `ApplyWorkFlowModels`; existing rows backfill to safe defaults without data loss):

| Table | New columns | Backfill default |
|-------|-------------|-----------------|
| `Wf_NodeInstance` | `ApproverSetEpoch (uint)` | 0 |
| | `DefinitionCode (string?)` | NULL |
| `Wf_ApprovalTask` | `DelegationRuleId (Guid?)` | NULL |
| | `DelegationExpiresUtc (DateTime?)` | NULL |
| | `WindowVerifiedUtc (DateTime?)` | NULL |
| | `AddDepth (int)` | 0 |

**`Wf_WorkflowTimer` is unchanged** — no new columns in this wave (timer table was already present from Sprint-1).

To generate the migration (append to your existing migration series):
```bash
dotnet ef migrations add WorkFlowWave45 \
  --context DataContext \
  --project YourApp/YourApp.csproj \
  --startup-project YourApp/YourApp.csproj
```

**Behavior compatibility**: existing graphs (without `TimeoutDef`) are unaffected — timers are never armed for them. Standing delegation rules only apply at next node-activation; in-flight instances are untouched until an explicit `DelegateTaskAsync` call. The `ApproverSetEpoch` column defaults to 0 — pre-Wave-4 completion CAS callers that do not pass `expectedApproverSetEpoch` continue to use the pre-existing predicate (backward-compatible nullable parameter discipline, matching how Wave-3 added `Generation`).

## [10.10.0] - 2026-06-10

WorkFlow Wave 3 — 回退-to-node (`ReturnToPrev`/`ReturnToNode`) and parallel/inclusive gateways with Join and Ack. Every feature is opt-in; existing single-token graphs and the serial-approve API are completely unaffected. See Migration for new additive columns.

### Added

- **回退-to-node — `ReturnToPrevAsync` / `ReturnToNodeAsync`** (#278): an approver can now reject back to any *dominating* upstream Approval node rather than only to the initiator. `ReturnToPrevAsync` resolves the nearest completed upstream Approval node that dominates the trigger node on the pinned graph. `ReturnToNodeAsync` accepts an explicit `targetNodeKey` validated at publish-time and runtime against the dominator set. Both funnel into a single `ExecuteReturnToNodeAsync` pipeline with:
  - **Engine-owned transaction envelope** — the return runs inside one explicit transaction opened by the engine (previously no return path opened a transaction). Correctness does not depend on isolation level.
  - **Supersede-not-delete** span discard (`NodeInstance.State → Superseded`, a terminal flip so no `Activated`-keyed predicate re-fires on a dead node). Superseded rows are never deleted — a late approver's CAS always finds a row and resolves cleanly to `AlreadyHandled`.
  - **Per-instance `Generation` epoch** (`ProcessInstance.Generation uint`, default 0): bumped atomically in the STEP-1 `BeginReturnAsync` CAS together with `ReturnLoops++` and the `Returning` mutex — single-row, single-statement, no isolation-level dependency (the same proven `ConcurrencySpikeTests` shape).
  - **`MaxReturnLoops` cap** (`WorkflowOptions.MaxReturnLoops`, default 3): when the cap is reached the instance terminates with `EventAction.FailClosed` and a `MaxReturnLoopsExceeded` result code — never an infinite bounce loop.
  - **Crash-recovery lease** (`ProcessInstance.ReturningLeaseUtc DateTime?`): set in STEP 1 and cleared in STEP 6; Wave-5 reaper reclaims an expired lease via a single-row CAS so a crashed mid-return cannot permanently wedge an instance.
  - **`NextSeq` instance counter** (`ProcessInstance.NextSeq int`, default 1): replaces the previous `MAX(Seq)+1` pattern (which required SERIALIZABLE and broke under concurrency). Seq is now allocated via a single-row guarded CAS on the instance row — portable, contiguous, monotonic, no isolation dependency. Re-entry never resets it; return events take the next counter value.
  - **New `WorkflowActionCode` members**: `AlreadyHandled`, `MaxReturnLoopsExceeded`, `JoinUnsatisfiable`, `Returned`.
  - **`NodeState.Superseded`** and **`InstanceState.Returning`** enum additions.
  - Return target validation: non-dominating targets are rejected at publish time (`WorkflowGraphValidator`) and fail-closed at runtime. Returning into the middle of an open parallel region is out of scope for Wave 3.

- **Parallel and inclusive gateways — `NodeKind.ParallelGateway` / `NodeKind.InclusiveGateway`** (#279): multi-token `AdvanceCoreAsync` with AND-fork and OR-fork semantics.
  - **AND-fork (Parallel)**: all outgoing branch tokens are minted in one in-transaction `SaveChanges`, each stamped with `Generation`, `ForkGroupId`, and `JoinNodeKey`. Existing single-token exclusive (`Condition`) routing is unchanged.
  - **OR-fork (Inclusive)**: mints each branch whose `TransitionDef.Condition` evaluates `true` via the existing `WhitelistRoutingEvaluator`; fail-closed if zero branches match and no default is specified. `JoinExpectedArrivals` is pinned at fork time to the count of branches actually activated — eliminates the BPMN inclusive-join deadlock.
  - All fork mints are idempotent under retry via `UNIQUE (TenantCode, InstanceId, NodeKey, Generation)`.

- **Join node** (#279): completion is a **single-statement conditional CAS** (`FireJoinIfSatisfiedAsync`: `WHERE ArrivedCount >= ExpectedArrivals AND State == Activated AND RowVer == @v`) — read and fire are never two separate statements, preventing over-fire and hang. Each arriving token increments `JoinArrivedCount` via `IncrementJoinArrivedAsync` (also a single-row CAS). Orphan-token fail-closed: a forked token reaching a non-arriving terminal state (`Superseded`, `CompletedRejected`, `FailClosedRouting`) decrements `JoinExpectedArrivals` via `DecrementJoinExpectedAsync` (underflow-protected CAS) in the same transaction, then attempts `FireJoinIfSatisfiedAsync`. A reachability backstop re-derives satisfiability from a live current-gen cohort query on each Join evaluation; if the set is empty and the Join is unsatisfiable the Join fires `CompletedRejected` + `EventAction.FailClosed` — the Join never hangs.

- **Ack node (blocking acknowledgement)** (#279): `NodeKind.Ack` blocks the token until required acknowledgers claim their `ApprovalTask` rows (`AckMode ∈ {All, Any, Quorum}`, mirrors `ApproveMode`). Distinct from `NodeKind.Cc` (non-blocking, token passes immediately). Ack tasks are generation-stamped and discarded with the span on return. Explicit handler tests assert Ack holds and Cc never does.

- **New `GuardedTransition` methods** (#278, #279): `BeginReturnAsync`, `CancelTimersForReturnAsync`, `DiscardTasksForReturnAsync`, `SupersedeNodeAsync`, `MintNodeInstanceGuardedAsync`, `AllocateSeqAsync`, `ReclaimReturningLeaseAsync`, `IncrementJoinArrivedAsync`, `DecrementJoinExpectedAsync`, `FireJoinIfSatisfiedAsync`. All existing `Activate`/`Complete`/`IncrementApproved`/`ClaimTask` predicates gain `AND Generation == @g`. No raw `ExecuteUpdateAsync` outside `GuardedTransition` — the single-CAS-primitive audit surface holds.

- **Concurrency test matrix** (#278, #279): `ReturnToNodeTests.cs` + `ReturnConcurrencyTests.cs` (T-RET-1…8, T-MIG-1) and `ParallelGatewayTests.cs` / `JoinTests.cs` / `AckCcTests.cs` / `JoinConcurrencyTests.cs` (T-JOIN-1…6, T-ACK-1), all using SQLite shared-memory (not EF InMemory) per the repo concurrency-test pattern.

### Changed

- **`WorkflowEventLog.Seq` now allocated from `ProcessInstance.NextSeq`** (#278): the previous `MAX(Seq)+1 / SERIALIZABLE` pattern is removed. Seq is allocated via a single-row guarded CAS on the instance row (`AllocateSeqAsync`). Seq remains contiguous, monotonic, and gap-free per instance; concurrent appends contend on the instance `RowVer`, with the loser retrying and taking the next counter value. A nullable `WorkflowEventLog.Generation` column tags the epoch for audit grouping (never enters Seq math). Cross-instance event throughput is unaffected; within-instance ordering is the intentional serialization point.

### Migration

The following columns are **additive** (all have `HasDefaultValue` in `ApplyWorkFlowModels` — all existing rows backfill to safe defaults without data loss):

| Table | New columns | Backfill default |
|-------|-------------|-----------------|
| `Wf_ProcessInstance` | `Generation (uint)`, `ReturnLoops (uint)`, `NextSeq (int)`, `ReturningLeaseUtc (DateTime?)` | 0, 0, `MAX(Seq)+1` per instance, `NULL` |
| `Wf_NodeInstance` | `Generation (uint)`, `SupersededAtGen (uint?)`, `ForkGroupId (Guid?)`, `JoinNodeKey (string?)`, `JoinExpectedArrivals (int)`, `JoinArrivedCount (int)` | 0, NULL, NULL, NULL, 0, 0 |
| `Wf_ApprovalTask` | `Generation (uint)` | 0 |
| `Wf_WorkflowTimer` | `Generation (uint)` | 0 |
| `Wf_WorkflowEventLog` | `Generation (int?)` | NULL |

**`NextSeq` backfill is critical**: the migration must seed `NextSeq = (current MAX(Seq) per instance) + 1` for each existing `ProcessInstance`. A partial backfill (leaving `NextSeq = 1`) strands every running instance on the first post-upgrade event append. The provided migration template handles this via a SQL `UPDATE` before the column default is applied.

**New unique index**: `UNIQUE (TenantCode, InstanceId, NodeKey, Generation)` on `Wf_NodeInstance`. Non-filtered for portability across MySQL, Oracle, and DaMeng. Superseded rows occupy slots — this is acceptable given the generation discipline.

**Behavior compatibility**: multi-token behavior only triggers on `NodeKind.ParallelGateway` / `InclusiveGateway` nodes. All existing single-token graphs (using `Start`, `Approval`, `Condition`, `End`) are byte-identical in behavior. Existing `Generation == 0` instances are handled transparently — the live-marking query filters `Generation == instance.Generation`, which evaluates to `0 == 0` for legacy rows.

**Return-target scope**: `ReturnToPrev`/`ReturnToNode` only accept targets that *dominate* the trigger node on the pinned graph. Non-dominating targets are rejected at publish time and fail-closed at runtime. Returning into the middle of an open parallel region (where a fork straddles the target boundary) is a Wave-3 out-of-scope case and will produce a `FailClosed` result.

**`ReturnResetMode.Resume` (opt-in)**: when specified, re-materialized target nodes copy surviving decided approvals from the immediately-prior generation only, re-validating each approver still resolves under the current rule. Compliance-sensitive — document the decision if enabled.

To generate the migration:
```bash
dotnet ef migrations add WorkFlowWave3 \
  --context DataContext \
  --project YourApp/YourApp.csproj \
  --startup-project YourApp/YourApp.csproj
```

## [10.9.0] - 2026-06-10

`WalkingTec.Mvvm.WorkFlow` approval engine — a new NuGet package (the 4th shipping package alongside Core / Mvc / TagHelpers.LayUI). Greenfield Chinese-corporate approval/workflow engine built as a peer sibling to `WalkingTec.Mvvm.Etl`. **Every feature is opt-in** — existing applications are completely unaffected until `AddWtmWorkFlow()` is called. This module ships **zero migrations**; consumers run their own `dotnet ef migrations add` (see Migration).

### Added

- **`WalkingTec.Mvvm.WorkFlow` package** — a new 4th NuGet package containing the approval/workflow engine. Install with `dotnet add package WalkingTec.Mvvm.WorkFlow`. Peer sibling to `WalkingTec.Mvvm.Etl`; depends on `WalkingTec.Mvvm.Mvc`.

- **Version-pinned process definitions** (#240): process graphs are stored as canonical JSON (`ProcessDefinitionVersion.GraphJson`) SHA-256 hashed into `ProcessDefinitionVersion.ContentHash`. Running instances FK the immutable version, never the mutable head — definition edits always create a new version and never affect in-flight approvals. Publishing is idempotent (republishing an identical graph is a no-op). Validation-only dry-run available at `POST /api/_workflow/definitions/validate`.

- **Token/marking engine with `GuardedTransition` atomic transitions** (#240): every state-changing operation routes through a `GuardedTransition` conditional `ExecuteUpdateAsync` helper (guard-in-WHERE CAS, branch on `rowsAffected == 0` → idempotent no-op). This is the same verified pattern as `TokenService` (modeled on `TokenService.cs:95-116`). Handles all race classes: 或签 concurrent approvers, 会签 double-completion (threshold-crossing is itself a guarded CAS on `NodeInstance WHERE State == Activated`), 撤回 vs. final-approve race, and (Wave 5) timeout vs. human.

- **三种审批模式 — 串签 / 会签 / 或签** (#240, #251, #252): all three modes are values of a single `ApproveMode` enum on a generic Approval node.
  - **串签 (Sequential)**: tasks activate one-by-one in order; each approver acts before the next is notified.
  - **会签 (All / Joint, #251)**: all approver tasks are minted `Pending` simultaneously. The node completes when the configured `approvePercent` fraction (`null` = 100%) is reached via guarded CAS. `RejectGate.Immediate` fails the node on first reject; `RejectGate.AfterAll` fails only when the threshold is mathematically unreachable.
  - **或签 (Any-one, #252)**: all approver tasks are minted `Pending` simultaneously. The first approver to approve wins via guarded CAS; sibling tasks are cancelled. A single reject does not fail the node — only the last pending approver's reject triggers node failure.

- **`IApproverResolver`** (#240): pluggable approver resolution with three built-in strategies — `Role` (all users in a WTM role), `User` (explicit ITCode list), and `ManagerChain` (recursive manager lookup with cycle detection, human-dedupe, and configurable `MaxLevel`/`MaxReturnLoops` caps). Implement `IApproverResolver` to add custom strategies.

- **Sandboxed conditional routing — `WhitelistRoutingEvaluator`** (#240): Condition nodes use a **closed operator enum** (`Eq`, `Gt`, `Gte`, `Lt`, `Lte`, `Contains`, `In`, `NotEq`, `NotContains`, `NotIn`) evaluated against `FormDataJson`. Fields must be declared in the definition's `fieldWhitelist` (mirrors the Analysis Mode field whitelist). No Roslyn, no DynamicLinq, no string evaluation. Routing evaluators are compiled and cached by `ContentHash`. `In`/`NotIn` value lists are capped at 100 items. Safety enforced in two layers: publish-time validation (`RoutingValidator`) and runtime re-validation (`WhitelistRoutingEvaluator`) — an off-whitelist field access fails closed with `ROUTING_FIELD_NOT_ALLOWED`.

- **撤回 (Withdraw / ReturnToInitiator)** (#240): `WithdrawPolicy` enum controls when the initiator may withdraw a running instance — `BeforeAnyAction` (strictest), `BeforeFinalApproval` (default), or `Disabled`. `ReturnToInitiator` (回退) lets an approver send an instance back to the initiator for revision. Wave 3 will add ReturnToPrev / ReturnToNode.

- **抄送 (CC, non-blocking)** (#240): CC records are created in the same transaction as the approval step. CC recipients are notified via `IWtmWebhookSink` but have no approval authority and do not block the flow.

- **Engine controllers** (#240): three controllers, all extending `BaseController` with `[ActionDescription]` / `FunctionPrivilege` RBAC gates on every action. Approver-eligibility is enforced at the action layer (`AssigneeITCode` match via `WorkflowActionVM`) — controllers never touch `DC` directly.
  - `ProcessDefinitionController` — `GET /api/_workflow/definitions`, `POST /api/_workflow/definitions/{id}/publish`, `POST /api/_workflow/definitions/validate`, `GET /api/_workflow/definitions/{key}/versions`
  - `WorkflowInstanceController` — `POST /api/_workflow/instances/start`, `POST /api/_workflow/instances/{id}/withdraw`, `GET /api/_workflow/instances/{id}/timeline`
  - `WorkflowTaskController` — `GET /api/_workflow/tasks/mine`, `POST /api/_workflow/tasks/{id}/approve`, `POST /api/_workflow/tasks/{id}/reject`, `POST /api/_workflow/tasks/{id}/return`

- **`IWorkflowNotifier` via `IWtmWebhookSink`** (#240): opt-in via `AddWtmWorkFlowNotifications()`. Reuses the shared `IWtmWebhookSink` introduced in 10.8.0 (DingTalk / WeCom / Feishu / Slack / Teams). When no sink is registered the notifier is a silent no-op. **Non-blocking guarantee:** all notifications are sent after the engine transaction commits — a delivery failure is logged at `Error` and never propagates to the engine caller; a webhook error cannot roll back an approval decision. Cards carry only instance/task identifiers, node key, actor ITCode, and decision outcome — `FormDataJson` is never included.

- **Dual-audit `WorkflowEventLog`** (#240): `[AuditChanges]` on `ProcessDefinition`, `ProcessDefinitionVersion`, `ProcessInstance`, `NodeInstance`, `ApprovalTask`, and `DelegationRule` captures VM-driven CRUD writes via `ChangeLog`. The append-only `WorkflowEventLog` is the authoritative engine audit for state transitions (which use `ExecuteUpdateAsync` and bypass the EF change tracker). Timeline endpoint: `GET /api/_workflow/instances/{id}/timeline`.

- **ProcessDefinition admin grid** (#240): `ProcessDefinitionListVM` provides a read-only tenant-scoped grid for browsing process definitions. Searcher fields: `Code`, `Name`, `Category`, `IsEnabled`. Grid actions: read-only detail dialog and version-history dialog. In-grid editing is intentionally absent — definitions are published via API.

- **`AutoApproveOnMissingHandler` safe default — `FailClose` (#250)**: when an approval node's approver cannot be resolved (empty role, unresolvable ManagerChain, or unsupported rule type), the node fails closed by default — the instance stays `Running` and requires admin intervention. The prior implicit default was `AutoApprove` (silent compliance bypass). `EscalateToAdmin` also now fails closed when `AdminFallbackITCode` is not configured.

- **会签 completion-policy dispatcher (#251)**: threshold-crossing node completion is itself a guarded CAS (`WHERE NodeInstance.State == Activated`) — count increments are advisory; exactly one CAS winner completes the node, preventing double-completion and lost-completion races.

- **或签 CAS winner (#252)**: the first-approver-wins transition is a guarded CAS on `NodeInstance WHERE State == Activated`; sibling task cancellation happens only after the CAS succeeds.

### Migration

- **`ApplyWorkFlowModels()`** (required if using WorkFlow): call from your application's `DataContext.OnModelCreating` and run `dotnet ef migrations add WorkFlowInitialCreate` against your own context. This creates 9 tables: `Wf_ProcessDefinition`, `Wf_ProcessDefinitionVersion`, `Wf_ProcessInstance`, `Wf_NodeInstance`, `Wf_ApprovalTask`, `Wf_WorkflowEventLog`, `Wf_CcRecord`, `Wf_DelegationRule`, `Wf_WorkflowTimer`. Mirrors the `ApplyEtlModels()` pattern exactly. **WorkFlow ships zero migrations** — identical to `WalkingTec.Mvvm.Etl`.
  ```csharp
  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
      base.OnModelCreating(modelBuilder);
      modelBuilder.ApplyEtlModels();       // if also using Etl
      modelBuilder.ApplyWorkFlowModels();  // WorkFlow tables
  }
  ```
  > **Correction (2026-07-30):** the snippet above reflects 10.9.0, before #862. `modelBuilder.ApplyEtlModels();` is now the `[Obsolete]` zero-argument overload -- it registers the ETL tables but can never bind the `ITenant` query filter, so ETL entities silently lose tenant isolation. Use `modelBuilder.ApplyEtlModels(this);` instead; see `docs/etl-module.md` for the current example and #862/#893 for why.

- **Safe defaults** (opt-in overrides required to restore prior implicit behavior):
  - `InitiatorAutoApprove = false` (default): the initiator is never auto-skipped as a first approver. Set `options.InitiatorAutoApprove = true` to restore prior draft behavior (explicit opt-in, constitutes a compliance bypass — document it).
  - `AutoApproveOnMissingHandler = FailClose` (default, #250): a node with no resolvable approver fails closed. Set `AutoApproveOnMissingHandlerPolicy.AutoApprove` to restore the prior silent auto-approve — only if you can accept the compliance implications.

- **`DBTypeEnum.Memory` is unsupported**: the engine throws `InvalidOperationException` at startup when EF InMemory is detected. Use SQLite, SQL Server, PostgreSQL, MySQL, Oracle, or DaMeng. (Live-provider concurrency conformance is proven on SQLite; the 7-provider conformance gate is tracked in issue #270.)

## [10.8.0] - 2026-06-08

Dashboard BI and ETL feature release (epic #193, Chinese-intranet/single-tenant focus): a no-code dashboard designer, KPI threshold alerting with scheduled snapshots, a DB-backed dashboard store, five new ETL source connectors, ETL governance (dead-letter / lineage / per-tenant isolation), and a shared webhook notification sink. **Every new subsystem is opt-in** — no default behavior changes. New persistence stores ship with EF Core entity sets that require a migration before use (see Migration).

### Added

- **No-code dashboard designer** (#238): a drag-and-drop designer at `/_DashboardPage/Designer` backed by `_DashboardDesignerController` (`/_dashboard-designer`). `GET /vm-meta?vmType=…` returns the dimensions/measures of a registered Analysis VM; `POST /preview` renders a live data preview for a widget draft. The designer binds widgets to Analysis VMs (dimensions/measures), REST sources, or static values; configures chart type, filters, DateRange presets, KPI thresholds, and cross-widget drill-down — all without editing JSON — and emits exactly the same `DashboardDefinition`/`WidgetDefinition` schema the runtime already consumes. Both endpoints are `[AllRights]`; the tenant is taken server-side from `LoginUserInfo.TenantCode`; widget types are checked against `DashboardOptions.AllowedWidgetTypes` and filter operators against `FilterConfig.AllowedOps` before any data fetch; the transient preview dashboard is always deleted afterward.
- **DB-backed dashboard store** (#234): `AddWtmEfDashboardStore()` registers `EfCoreDashboardService`, an `IDashboardService` implementation that persists dashboards via EF Core instead of JSON files. All reads/writes/deletes are tenant-scoped; a new `IDashboardService.DeleteAsync(id, tenantId)` overload enforces tenant ownership on deletion. Adds cross-widget drill-down: clicking a data point on one widget pushes its dimension value into the dashboard filter bar.
- **KPI threshold alerts** (#237): `AddWtmDashboardAlerts()` registers a background evaluator (`DashboardAlertHostedService`) that periodically evaluates `WidgetThreshold` rules (`ThresholdComparisonOp` Gt/Ge/Lt/Le/Eq, `ThresholdAlertLevel`) against widget measures and pushes alert cards through the shared webhook sink. Alerts fire on transition (with cooldown de-duplication) and are tenant-aware. Off by default (`DashboardAlertOptions.EvaluationIntervalSeconds = 0`).
- **Scheduled dashboard snapshots + export** (#237): `AddWtmDashboardSnapshots()` registers a cron-driven snapshot service (`DashboardSnapshotHostedService`) that exports dashboards to Excel (`DashboardExcelExporter`, multi-sheet NPOI) on a schedule. PDF/PNG export is supported through a pluggable `IDashboardRenderer` seam — the framework does **not** bundle a headless browser; `NotConfiguredDashboardRenderer` throws a helpful message until a host registers a renderer.
- **Dashboard widget quality-of-life** (#228, #231): widget configuration is validated up front; `AnalysisWidget` data is cached with a per-widget timeout; charts gain multi-series support, six additional chart types, DateRange presets, an in-flight request guard, and a static-value widget type.
- **Shared webhook / notification sink** (#230): `AddWtmWebhookSink()` / `AddWtmWebhookSinks()` register `IWtmWebhookSink`, a provider-agnostic sink that delivers `WebhookMessage` cards to DingTalk (with HMAC signing), WeCom, Feishu, Slack, and Microsoft Teams. SSRF-hardened: DNS-pinned connections, HTTPS-only, private-IP/IMDS blocking, redirects disabled; secrets are never logged.
- **ETL source connectors** (#227, #232, #233): a pluggable `EtlSourceRegistry` plus five new sources — `CsvEtlSource`, `ExcelEtlSource`, `PostgreSqlSource` (with `PostgreSqlBulkLoader` using `COPY` + `ON CONFLICT` upsert), `MySqlEtlSource` (with `MySqlBulkLoader` using `ON DUPLICATE KEY`), and `RestEtlSource` (paginated, authenticated, SSRF-hardened). `AddWtmEtl()` registers the registry and built-in sources.
- **ETL governance** (#229, #236): `AddWtmEtlAlerts()` and `IEtlGovernanceStore`/`DbEtlGovernanceStore` add run-log retention, composite merge keys, SLA alerting, dry-run audit, dead-letter quarantine (`EtlDeadLetterRow`), data lineage (`EtlLineageRecord`), and per-tenant ETL job isolation (`EtlJobDefinition : ITenant`). Raw error text is sanitized before persistence. A `NullEtlGovernanceStore` is the default no-op.
- **ETL webhook alert cards** (#235): ETL failure and SLA-breach events can be pushed as webhook cards through the shared sink. Off by default (`EtlAlertOptions.EnableWebhookAlerts = false`).

### Changed

- **PostgreSQL/MySQL ETL bulk load is now supported** (#232): the bulk-load path that previously threw `NotSupportedException` for these providers now performs provider-native upserts. Adds `Npgsql` 10.0.2 and `MySqlConnector` 2.4.0 as ETL dependencies.

### Migration

- The DB-backed dashboard store (`AddWtmEfDashboardStore`), ETL governance store (`DbEtlGovernanceStore`), and per-tenant ETL job isolation introduce new EF Core entity sets (dashboard records, dead-letter rows, lineage records, ETL job definitions with a `TenantCode` column). **If you opt into any of these stores, generate and apply an EF Core migration before first use.** Deployments that do not enable these opt-in services are unaffected — the JSON-file dashboard store and in-memory ETL paths remain the defaults.

## [10.7.0] - 2026-06-08

Commercial-readiness hardening pass (epic #193, Chinese-intranet/single-tenant focus): a complete security-and-correctness batch plus a CodeGen v2 feature-attribute system. Security fixes are high-priority — upgrading is recommended. Two behavior changes are flagged under Changed.

### Security

- **MVC controller authorization holes fixed** (#194): `UpdateModelProperty` now routes the single-field mutation through the entity's CRUD VM `DoEdit()` (so validation + duplicate-check apply) and adds an opt-in `CanEditProperty(entity, propertyName)` hook plus a sensitive-field blocklist; `GetPagingData` / `GetExportExcel` / `GetExcelTemplate` / `Upload` validate the client-supplied connection-string key against `Configs.Connections` and reject unknown keys; the `Selector` endpoint is now `[AllRights]` instead of `[Public]` (see Changed); a startup guard throws when `IsQuickDebug = true` outside the Development environment (it bypasses all RBAC).
- **Tenant isolation + RBAC audit** (#198): `SetDuplicatedCheck` now scopes the uniqueness query to the current tenant for `ITenant` entities (soft-delete visibility preserved); `FileUploadOptions.EnforceTenantFileScope` (opt-in, default `false`) enforces tenant scope on file-by-id lookups; `[AuditChanges]` is applied to the framework RBAC entities (`FrameworkUser`/`FrameworkRole`/`FunctionPrivilege`/`DataPrivilege`/`FrameworkMenu`/`FrameworkUserRole`/`FrameworkUserGroup`/`FrameworkGroup`) so permission changes are recorded.
- **Grid + TagHelper XSS hardening** (#195): grid `BackGroundFunc`/`ForeGroundFunc` color values are validated against a strict hex/named-color allowlist and HTML-encoded; `CheckBox`/`Radio`/`Hidden`/`Form` tag helpers and the `LayuiUIService.Make*` cell renderers now `HtmlEncode` dynamic values.
- **Dashboard widget hardening** (#197): REST widget HTTP method is restricted to an allowlist (GET/POST); request headers use validating `Add` (rejects CRLF injection); dashboard filter operators are validated against an allowlist before the expression tree is built; a `RestWidgetDataSourceOptions.AllowedPorts` allowlist (default 80/443/8080/8443) mitigates SSRF port-probing; widget titles are length-capped.
- **VM factory type guard** (#201): `WtmVmFactory.CreateVM` rejects non-`BaseVM` types before invoking any constructor (previously the constructor ran before the type was rejected).

### Added

- **CodeGen feature-attributes** (#202, #203, #204): four additive attributes in `WalkingTec.Mvvm.Core` — `[ListColumn]` (Width/Align/Sort/Hide/ShowTotal/Fixed), `[SearchField]` (Operator/ShowInPanel/DateRange/Order), `[FormField]` (ControlType/Colspan/Group/Order/Placeholder/ReadonlyOnEdit), `[ImportConfig]` (DataType/RequiredOnImport/ColumnHeader/DateFormat). They are consumed by both the code generator (better-defaulted scaffolds) and at runtime (grid columns and form fields honor them on hand-written models too). Every default reproduces the previous behavior. New enum members `GridColumnFixedEnum.None`, `GridColumnAlignEnum.Auto`, plus `SearchOperator`/`FormControlType`.
- **Regenerate-safe code generation** (#203): each generated artifact is split into a `*.Generated.cs` (always overwritten) and a companion partial `*.cs` (written once) so re-running the generator never overwrites hand-written logic. The generator now emits async controller/VM stubs and SQLite-shared-memory test fixtures (instead of the EF InMemory provider, which cannot translate `ExecuteUpdate`/sub-queries), and carries model `[Required]`/`[StringLength]` onto generated import VMs.
- **Dashboard filters now apply** (#197): changing a dashboard filter re-fetches the widgets with the filter values applied (the filter bar was previously inert).

### Fixed

- **Grid row-action buttons render correctly again** (#195): generated `MakeButton`/`MakeDialogButton` Edit/View/Delete columns were rendering as escaped literal text on list pages; framework-generated column HTML is now rendered as markup while user/DB cell data stays HTML-escaped (the #108 stored-XSS guard is preserved).
- **Batch operations and Excel import are transactional** (#196): `DoBatchDelete`/`DoBatchEdit` and the import save path are wrapped in a transaction (no partial commits on a mid-batch failure); a single invalid import row is reported per-row instead of failing the whole file; `DynamicSelect` no longer throws on an unknown field name.

### Changed

- **`Selector` endpoint now requires authentication** (#194): it changed from `[Public]` to `[AllRights]`. **Migration:** if you relied on an unauthenticated Selector (e.g. a public kiosk), set `AllowUnauthenticatedSelector = true` in configuration.
- **Multi-tenant duplicate-check is now tenant-scoped** (#198): for `ITenant` entities the uniqueness check no longer sees other tenants' rows. Single-tenant deployments are unaffected; soft-delete visibility is unchanged.

## [10.6.0] - 2026-06-07

Deep optimization of the ETL and OLAP (Analysis) modules (#179): 52 profiled opportunities, 40 adversarially confirmed. Performance improvements are internal and behavior-neutral; new tuning knobs are opt-in and default to prior behavior.

### Added

- **ETL bulk-loader tuning (opt-in)** (#184): `MssqlBulkLoader` gains a `bulkCopyOptions` ctor parameter (default `SqlBulkCopyOptions.Default`; `TableLock` recommended for private staging tables) and an `internalBatchSize` parameter (default `0` = single full batch). `OracleBulkLoader` gains a `timeoutSeconds` parameter (default `0` = prior infinite wait); when set, `CommandTimeout` is applied across Merge/Replace/Truncate/EnsureStaging/BulkLoad. All additive — the `IBulkLoader` interface is unchanged.
- **ETL source/quality/schema tuning (opt-in)** (#185): `OracleSource.FetchRowCount` (default `0` = ODP.NET default) sets the reader `FetchSize` when positive; `EtlPipelineConfig.WatermarkSqlType` (default `null`) uses an explicit typed `SqlParameter` for the MSSQL watermark instead of `AddWithValue`; `EtlSchemaServiceFactory.CreateWithCache(dbType, IMemoryCache, ttl?)` returns a `CachingEtlSchemaService` decorator (60s default TTL) — the bare `Create(...)` stays non-caching.
- **OLAP export/pivot overloads (opt-in)** (#186): `AnalysisExcelExporter.ExportToStream(response, stream, ...)` writes the workbook directly to a destination stream (no second full-buffer copy); `AnalysisPivotEngine.Pivot(..., bool fillZero)` enables sparse output. Existing `byte[] Export(...)` and the 4-parameter `Pivot(...)` signatures are unchanged.

### Fixed

- **ETL MSSQL schema-qualified column lookup** (#184): `GetColumnsAsync` filtered on `TABLE_NAME` only, returning 0 columns for schema-qualified staging tables such as `audit.STG_x`; it now filters on `TABLE_SCHEMA` too (unqualified names default to `dbo`).
- **ETL scheduler status mutations** (#185): `UpdateStatusAsync`/`SkipNextAsync` now use `ExecuteUpdateAsync` with a server-side `SkipCount` increment (eliminating a read-then-write race) and stamp `UpdateTime` explicitly to match the EF audit interceptor.

### Improved

- **OLAP Analysis engine performance** (#180): cached reflection on the request hot path (four `ExecuteDynamic*` `MethodInfo` caches, the `ApplyFilters` `Contains` lookups, and the `ServerSideGroupByStrategy` aggregate-method lookups); single-pass in-process GroupBy with a struct accumulator over pre-resolved `PropertyInfo`; `ComputeHash` now rents its UTF-8 buffer from `ArrayPool` (byte-identical hash, so cache keys are unchanged); single-pass forecast regression; pivot row-key fast paths and output-dictionary pre-sizing; Excel total-cell style reuse.
- **OLAP Dashboard performance** (#180): JSON dashboard definitions are cached with a `LastWriteTimeUtc` staleness guard (N widget reads become 1 per refresh); static `JsonSerializerOptions`; REST widget bodies read via `StreamReader` instead of an intermediate byte array.
- **ETL pipeline performance** (#181): single-pass watermark max via `Comparer<object>.Default`; ordinal column access in the Oracle array-binding loop; hoisted reader-schema snapshot in the MSSQL and Oracle sources; `HashSet` membership for `In`-rule quality checks; scheduler `NextFireAt` and the Quartz `Running` flag use targeted `ExecuteUpdateAsync` (with `UpdateTime` stamped for audit parity).
- **OLAP redundant DB round-trip eliminated** (#186): the in-process group-by truncation check reuses the materialized row count instead of issuing a second `COUNT`-with-limit query — roughly halves analysis-engine database load on auto-refreshing dashboards. The in-process strategy is now resolved per request to keep this thread-safe.
- **`AnalysisQueryEngine.cs` split** (#190): the 1847-line file is split into four `partial class` files (core / Filters / PostProcess / Hashing), each under 700 LOC, with no behavior or public-API change.

## [10.5.5] - 2026-06-06

### Fixes

- **DataContext.Run() parameterized raw SQL works on Oracle** (#147): parameters are built via the provider-agnostic `DbCommand.CreateParameter()` factory; removes the Oracle `NotSupportedException` stopgap from #145 in the Run path.
- **LookupCache: ITenant types are cached again in single-tenant mode** (#168): the #112/#113 cross-tenant bypass now only applies when tenant isolation is enabled (DefaultTenantIsolation=true); single-tenant apps (DefaultTenantIsolation=false) cache ITenant lookups under the global key instead of querying the DB on every call.

## [10.5.4] - 2026-06-04

### Fixes

- **ThreadPool starvation on authenticated-request hot path eliminated** (#128):
  `WTMContext.LoginUserInfo` is a synchronous property getter. On a cache miss for an
  authenticated user it called `ReloadUser` → `DoLoginAsync(...).GetAwaiter().GetResult()`,
  blocking a ThreadPool thread. When `HasMainHost` is true, `DoLoginAsync` makes an outbound
  HTTP call, making starvation under load a real risk.
  Fix (conservative, no breaking changes):
  - Added `ReloadUserAsync(string? itcode)` — async twin of `ReloadUser` that awaits
    `DoLoginAsync` instead of blocking.
  - Added `EnsureLoginUserInfoAsync()` — replicates only the authenticated-user branch of
    the `LoginUserInfo` getter, building the identical cache key and populating
    `_loginUserInfo` asynchronously. No-op when unauthenticated or already resolved.
  - `WtmMiddleware.InvokeAsync` now calls `await wtm.EnsureLoginUserInfoAsync()` immediately
    before `await _next(context)`. Because `WtmMiddleware` is registered after
    `UseAuthentication` (confirmed in demo `Startup.cs`), `HttpContext.User` is fully
    populated at that point. The subsequent synchronous `LoginUserInfo` getter in
    `PrivilegeFilter` finds `_loginUserInfo` already set and returns immediately.
  The synchronous `LoginUserInfo` getter is unchanged — it remains the fallback for
  background jobs, `_remotetoken` requests, and non-middleware contexts.
- **Core robustness: invariant culture conversion, thread-safe static caches, JSON error escaping, regex fallback** (#134): `PropertyHelper.ConvertValue` now passes `InvariantCulture` to both `Convert.ChangeType` calls, preventing decimal/date corruption on non-invariant-locale servers; `Utils.GetAllAssembly`, `GetAllModels`, and `GetAllVms` use double-checked locking to eliminate the empty-intermediate-state race; `ListVMExtension.GetError` escapes backslash and double-quote in error text before JSON interpolation, preventing malformed JSON; `WtmAuthorizationService.MatchUrl` catches `NotSupportedException` and `ArgumentException` in addition to `RegexMatchTimeoutException` so patterns unsupported by the `NonBacktracking` engine fall back gracefully instead of surfacing as a 500.

- **DataContext: Oracle parameter NRE, sensitive-logging PII gate, and connection leak fixed** (#132):
  Three medium-severity fixes in `EmptyContext.Run()` and related helpers:
  - `CreateCommandParameter` for Oracle was a commented-out no-op that silently returned `null`,
    causing `Parameters.Add(null)` → NRE in `Run()`. Now throws `NotSupportedException` with a
    clear message naming the provider, so callers get actionable feedback.
  - `EnableSensitiveDataLogging()` was called unconditionally whenever `IsDebug = true`, logging
    query parameter values (PII/credentials) to any configured logger in debug deployments.
    A new `EnableSensitiveQueryLogging` property (default `false`) must now be explicitly set to
    `true` to activate sensitive logging; `EnableDetailedErrors` is unaffected and remains debug-only.
    **Migration:** if you relied on `IsDebug = true` enabling sensitive logging, also set
    `dc.EnableSensitiveQueryLogging = true` in local dev configuration.
  - `Run()` opened the connection before the `using (command)` block; an exception from
    `ExecuteReader` or `DataTable.Load` would skip the `connection.Close()` call, leaking the
    connection. Fixed with a `try/finally` ensuring `Close()` always runs when the connection
    was opened by `Run()`.

- **BaseImportVM / ExcelPropety: workbook memory leak, wrong error row number, and culture-sensitive decimal/date parse fixed** (#150): `BaseImportVM` now implements `IDisposable` and disposes its `XSSFWorkbook` field on dispose; the redundant placeholder `new XSSFWorkbook()` allocation in `SetTemplateData` was removed; `GetErrorJson` uses a local `using var` workbook instead of overwriting the field. `SetEntityData` now derives `rowIndex` from `item.ExcelIndex` so `FormatData`/`FormatSingleData` errors report the correct Excel row instead of always reporting row 2. `ExcelPropety.ValueValidity` passes `CultureInfo.InvariantCulture` to `decimal.TryParse` and `DateTime.TryParse` so decimal values such as `1.5` and ISO dates parse correctly regardless of server locale; `SetColumnFormat` likewise uses `InvariantCulture` for `decimal.MinValue/MaxValue.ToString()`.
- **ProcessCommand divide-by-zero, DCExtension.Sort NRE on unknown property, invalid SortDir crash, and sort-order info-leak via sensitive fields fixed** (#151): `ProcessCommand` now normalises `Searcher.Limit` to the configured default before the `(Count-1)/Limit` page-count division so `Limit=0` no longer causes a swallowed `DivideByZeroException`; `DCExtension.Sort` skips sort fields that don't exist on `T` (prevents NRE via `Expression.Property(pe, null!)`); `OrderReplaceModifier` returns `node` unchanged when `SortDir` is outside `{Asc, Desc}` so an unknown direction value no longer yields a null expression that crashes `CreateQuery`; both `DCExtension.Sort` and `OrderReplaceModifier` now silently skip properties decorated with `[JsonIgnore]`/`[NotMapped]` and name-matched sensitive fields (`Password`, `PasswordHash`, `Salt`, `Token`), preventing an authenticated sort-order oracle attack.
- **`EmptyContext.ReCreate()` now preserves `Version`; `CascadeDelete` guards against cyclic trees** (#152): `ReCreate()` previously used the 2-arg `(string, DBTypeEnum)` constructor in the null-`ConnectionString` branch, silently dropping the configured DB compatibility `Version`; it now prefers the 3-arg constructor so `Version` is propagated (falls back gracefully when the subtype only exposes 2-arg). `CascadeDelete<T>` used unbounded recursion — a cyclic parent-child reference (A.ParentId=B, B.ParentId=A) or an exceptionally deep tree would cause an uncatchable `StackOverflowException`; fixed by introducing an internal overload that carries a `HashSet<Guid> visited` set and short-circuits on already-visited nodes; the public signature is unchanged.


- **REST widget SSRF hardening** (#101): seven security defects in the REST
  widget data source are fixed.
  - **CRITICAL** — Request-supplied `options` can no longer override
    security-sensitive fields (`AllowPrivateNetwork`, `AllowHttp`). These
    fields are now authoritative only from the server-side
    `WidgetSourceDefinition.RestOptions`; for legacy widgets without server
    `RestOptions`, both flags are forced to their safe defaults (`false`)
    before the options reach `RestWidgetDataSource`.
  - **HIGH** — The named `WtmRestWidget` HttpClient is now explicitly
    registered with `AllowAutoRedirect=false`, preventing SSRF bypass via
    HTTP 302 redirects (e.g. redirect to cloud IMDS at 169.254.169.254).
  - **HIGH** — `IsBlockedIp` now unwraps IPv4-mapped IPv6 addresses
    (e.g. `::ffff:169.254.169.254`) before evaluating rules, closing the
    bypass. Also added CGNAT (100.64.0.0/10, RFC 6598) and reserved
    (240.0.0.0/4) to the block list.
  - **HIGH** — DNS pinning via URI rewrite: the IPs validated at
    `ValidateUrlAsync` time are reused for the actual TCP connection (the
    request URI is rewritten to the literal validated IP with the original
    `Host` header preserved for TLS SNI). This eliminates the DNS
    rebinding/TOCTOU window between validation and fetch.
  - **MEDIUM** — `TimeoutSeconds` is now clamped to `[1, 60]` and
    `MaxResponseBytes` to `[1 KB, 10 MiB]`; negative/zero values no longer
    cause `ArgumentOutOfRangeException` or unbounded waits.
  - **LOW** — `ValidateUrl` is now async (`ValidateUrlAsync`) using
    `Dns.GetHostAddressesAsync` to avoid blocking a thread-pool thread on
    DNS resolution.
  - **LOW** — Non-2xx HTTP fetch errors and SSRF rejections surfaced via
    `_DashboardController` now return a generic 502 ("Widget data fetch
    failed.") with no URL, status code, or internal topology in the body;
    full detail is logged server-side at `Warning` level.
  Migration: no action required for most deployments. If you rely on
  `AllowPrivateNetwork=true` or `AllowHttp=true` for internal REST widgets,
  move those flags into the widget's server-side
  `WidgetSourceDefinition.RestOptions` in your dashboard JSON.
- **BaseBatchVM.DoBatchDelete — wrong entity deleted on unordered DB return** (#104):
  Built a dictionary mapping each entity's ID to the entity so that the permission
  check (`CheckIfCanDelete`) and the deletion always operate on the same record,
  regardless of the order in which the DB returns rows. Non-existent IDs continue to
  be silently skipped (backward-compatible).

- **BaseCRUDVM.DoEdit / DoEditAsync — files deleted when SaveChanges failed** (#104):
  Added a `saved` flag that is set to `true` only inside the `try` block after
  `SaveChanges()` succeeds. The `DeletedFileIds` file-deletion block is now gated
  on `saved`, so orphaned-attachment cleanup never runs when the DB update fails
  (e.g. `DbUpdateConcurrencyException`).

- **BaseCRUDVM.DoAdd / DoAddAsync — files deleted before SaveChanges** (#104):
  Moved the `DeletedFileIds` deletion block to after `SaveChanges()` /
  `SaveChangesAsync()`. A failed insert no longer permanently deletes the
  referenced file attachments.

- **BaseImportVM — IndexOutOfRangeException on templates with Dynamic columns** (#104):
  Restored correct `pIndex` / `i` advancement in the header-validation loop.
  For a `ColumnDataType.Dynamic` property the outer column index `i` now skips
  forward by `DynamicColumns.Count - 1` while `pIndex` only advances once,
  matching the expanded column count. Normal (non-dynamic) templates are unaffected.

- **BaseBatchVM.DoBatchEdit — false-positive duplicate for every edited row** (#104):
  Added `vm.SetEntity(entity)` before `vm.Validate()` in the per-row loop so that
  `ValidateDuplicateData` excludes the correct entity ID (the row being edited) from
  its uniqueness query. Previously `vm.Entity.ID` was `Guid.Empty`, causing every
  batch-edit row to be flagged as a duplicate of itself.
- **Unauthenticated DoS via null `PropertyInfo` in `Selector` and `GetBatchQuery`** (#106):
  `_FrameworkController.Selector` is marked `[Public]` (no authentication required).
  When `Ids.Count > 0`, it resolves `_DONOT_USE_VFIELD` via
  `modelType.GetSingleProperty()`, which returns `null` for any property name
  that does not exist on the model. The return value was previously passed
  directly to `Expression.Property`, throwing `ArgumentNullException` and
  producing an unhandled 500 on every request — a crash-on-demand vector for
  unauthenticated callers. A matching null-forgiving `!` in
  `BasePagedListVM.GetBatchQuery` exposed the same crash for a form-bound
  `SelectorValueField`. Fix: resolve the property into a local variable,
  null-check before use, and fall through gracefully (Selector returns
  `PartialView` with empty `SelectData`; `GetBatchQuery` falls back to the
  default id-based `Contains` predicate). No behaviour change on the happy
  path. Regression tests added.
- **XSS encoding in LayUI TagHelpers** (#108): Five encoding defects fixed
  across DataTableTagHelper, TreeTagHelper, SelectorTagHelper,
  ComboBoxTagHelper, DateTimeTagHelper, and UploadTagHelper.
  - `DataTableTagHelper.getTemplate`: row data values (`d.<field>`) now
    routed through `ff.EscapeText` before insertion as innerHTML, closing a
    stored XSS vector where a DB cell containing markup would execute in the
    browser.
  - `DataTableTagHelper` button labels (`item.Name`) now HTML-encoded into
    button/anchor HTML; `PromptMessage` now JS-encoded into `layer.confirm`
    JS string.
  - `TreeTagHelper` hidden `<input value>` now HTML-attribute-encoded
    (`WebUtility.HtmlEncode`) for both multi-select and single-select model
    values.
  - `SelectorTagHelper` hidden `<input value>` for selected IDs now
    HTML-attribute-encoded.
  - `ComboBoxTagHelper` `ItemUrl` now JS-encoded before emission into the
    `ff.LoadComboItems` script call.
  - `DateTimeTagHelper` `Format` and `RangeSplit` now JS-encoded in both
    the single and range `laydate.render` blocks.
  - `UploadTagHelper` `Field.Model` (Guid) now HTML-encoded in the hidden
    `<input value>` attribute.
  - `FrameworkFilter.cs` line 377 (`model?.ViewDivId`): analysed and left
    unmodified — `ViewDivId` is a framework-generated identifier
    (`"ViewDiv" + UniqueId`) that is never user-influenceable; no fix
    required.
- **Cross-user idempotency cache replay (security)** (#110): `WtmIdempotencyMiddleware`
  previously built cache keys from `method + path + Idempotency-Key` only, allowing
  two authenticated users with the same `Idempotency-Key` to share a cache entry.
  An attacker who guessed or observed another user's key could receive that user's
  cached 2xx response body (order, payment, PII). Fixed by incorporating the
  authenticated user identity (`itcode` claim, falling back to `NameIdentifier`) into
  the cache key as a `u:{userId}` prefix segment. Unauthenticated requests now pass
  through the pipeline without caching to prevent anonymous shared-bucket poisoning.
  `BuildCacheKey` signature updated from 3-arg to 4-arg; all existing tests updated
  and three new security-focused tests added (same-user replay still works; different
  users do not share entries; unauthenticated requests are not cached).
- **LookupCache: five concurrency and correctness bugs fixed (#112)**:
  (1) Cross-tenant data leak — `[CacheLookup(TenantIsolation=false)]` on an
  `ITenant` type caused Tenant A's EF-filtered rows to be stored under a
  global cache key and served to all tenants for the full TTL; the service
  now forces per-tenant isolation for any type implementing `ITenant`
  regardless of the attribute setting, and logs a Warning when the setting is
  ignored. (2) Non-`[CacheLookup]` types were silently stored in the cache
  without a TTL (immortal entry) and never invalidated; `GetAll`/`GetAllAsync`
  now bypass the cache entirely for uncacheable types and query the DB
  directly. (3) `RefreshAsync<T>(dc, tenantId)` called `InvalidateType` which
  cancelled the shared CTS and evicted all tenants' entries for that type,
  causing a cross-tenant stampede; it now calls `Invalidate<T>(tenantId)` to
  remove only the single requesting tenant's key. (4) After a per-key
  semaphore timeout, timed-out threads skipped the double-check and hit the DB
  concurrently, defeating the stampede guard; they now re-check the cache
  before falling through, and the semaphore is only released when it was
  actually acquired. (5) A race between `InvalidateType` and
  `AddExpirationToken` could cancel the `CancellationChangeToken` before it
  was registered, evicting the just-stored entry immediately; the service now
  checks `token.IsCancellationRequested` before registering the token and
  falls back to the absolute TTL expiry.
- **CodeGen: InjectAnalysisAttributes write-boundary + ModuleName injection** (#135): `InjectAnalysisAttributes` previously verified the discovered model file via `SafeCombine` anchored to the file's own directory rather than `MainDir`, allowing `FindModelFile`'s 5-level climb to return and overwrite a same-named `.cs` file outside the project tree. Fixed by comparing the canonical resolved path against `MainDir` before any read/write. `ModuleName` lacked input validation and was interpolated raw into generated JS object literals and JSON menu strings; fixed with `[RegularExpression]` (letters, digits, underscores, hyphens, spaces only) and defense-in-depth `EscapeForJson`/`EscapeForJsSingleQuoted` helpers at each interpolation site.

### Security

- **Exception/connection-string information leak fixed in four production paths** (#124):
  - `_EtlSchemaController` (Tables + Columns endpoints): raw `ex.Message` — which may contain
    the full DB connection string from EF/ADO.NET exceptions — was included in the 500 response
    body.  Fix: full detail is now logged server-side via `ILogger`; the client receives only
    `"Schema introspection failed; see server log."` regardless of `IsQuickDebug`.  Connection
    strings must never appear in HTTP responses in any mode.
  - `_EtlJobController` (TriggerNow, DryRun, Pause, Resume, Abort, SkipNext): raw
    `InvalidOperationException.Message` was echoed to the client in all environments.  Fix:
    full detail is now logged via `ILogger`; in production (`IsQuickDebug == false`) a generic
    `"操作失敗，請稍後再試。"` or `"找不到指定的 Job。"` is returned instead.  Dev mode retains
    the original message to aid diagnostics.
  - `_AnalysisController` (Query, Pivot, Export, PivotExport): the `AnalysisQueryEngine` wraps
    unexpected DB/EF exceptions in `InvalidOperationException` (line 1575 of
    `AnalysisQueryEngine.cs`), so `ex.Message` could expose internal detail.  Fix: engine-level
    catches now log via `ILogger` and return a generic title in production.  Registry-resolution
    and input-validation catches (intentionally user-facing messages) are unchanged.
  - `WTMContext.CallAPI` catch block: `ex.ToString()` (including stack trace) was set as
    `ApiResult.ErrorMsg`.  Fix: full exception is logged via `ILoggerFactory`; `ErrorMsg` is
    set to `"An error occurred while processing the request."` in production.  Dev mode
    (`IsQuickDebug == true`) retains the full `ex.ToString()` for diagnostics.
- **Access-token revocation via JTI denylist** (#126):
  A valid JWT access token remained fully usable until its natural `exp` time even after
  the user logged out or the associated refresh token was explicitly revoked. There was no
  server-side mechanism to invalidate issued access tokens before their expiry.
  Fix: introduced `IAccessTokenDenylist` (backed by `IMemoryCache`) that stores revoked
  JTI values with an absolute cache expiration matching the token's own `exp` claim —
  entries auto-evict when the token would have expired anyway, keeping memory bounded.
  `TokenService.RevokeTokenAsync` now calls `IAccessTokenDenylist.Deny()` with the current
  request's `jti` and `exp` immediately after revoking the refresh token.
  The `OnTokenValidated` JWT-bearer event checks the denylist on every authenticated
  request; if the JTI is denied it calls `context.Fail("Token has been revoked.")` so the
  request is rejected as `401 Unauthorized`.
  **Single-node note:** the default `IMemoryCache` implementation is process-local.
  Multi-node / load-balanced deployments should replace `IAccessTokenDenylist` with a
  distributed-cache-backed implementation (e.g. Redis via `IDistributedCache`) by
  registering a custom `IAccessTokenDenylist` before calling `AddWtmAuthentication`.
  New tests in `test/WalkingTec.Mvvm.Core.Test/Security/AccessTokenDenylistTests.cs`.

- **CodeGenVM: `MainDir` HTTP model-binding vector closed; `ShareDir` NRE fixed** (#122):
  Two bugs in `CodeGenVM` (`src/WalkingTec.Mvvm.Mvc/CodeGenVM.cs`):
  - **HIGH** — `MainDir` was missing `[BindNever]` despite being the write root for all
    generated files. A crafted POST could override `MainDir` with an attacker-controlled
    path, making `SafePathHelper.SafeCombine`'s boundary checks anchor to that arbitrary
    root instead of the server-derived `EntryDir` value — effectively an arbitrary file-write
    escalation. Fix: added `[BindNever]` to `MainDir` (mirroring the same protection already
    present on `EntryDir`). The property setter and internal server-side assignment are
    unaffected.
  - **MEDIUM** — `ShareDir` getter called `sharedir.FullName` unconditionally after a
    `.FirstOrDefault()` lookup that can return `null` when no sibling `*.shared` project
    directory exists (e.g. Blazor projects not following the `*.shared` naming convention).
    This caused an unhandled `NullReferenceException` at codegen time. Fix: added a null
    guard mirroring the `VmDir` pattern — when no `*.shared` sibling is found, the getter
    falls back to a `Shared/Pages/<ModelName>` directory under `MainDir`.
  Five regression tests added to `CodeGenAnalysisTests`.

- **Refresh-token double-rotation (TOCTOU race) — atomic claim fix** (#118):
  `TokenService.RefreshTokenAsync` previously used a non-atomic read-check-modify-save
  sequence that allowed two concurrent requests presenting the **same** refresh token
  to both pass the `IsActive` guard, both rotate, and both receive independent
  descendant tokens.  The attacker's token would stay alive permanently because
  the reuse-detection chain only tracked one branch.
  Fix: the revocation that "claims" a token for rotation is now a single
  `ExecuteUpdateAsync` bulk-`UPDATE` with the active conditions embedded in the
  `WHERE` clause (`RevokedUtc IS NULL AND ExpiresUtc > @now`).  Only one
  concurrent caller can match the row; all others get `affected == 0` and are
  immediately rejected — no new token is issued.  The existing sequential
  reuse-detection behaviour (`RevokeDescendantsAsync`) is preserved and still
  fires when a revoked token is replayed.  No DB schema change required;
  compatible with all supported providers (MSSQL / MySQL / PostgreSQL / SQLite /
  Oracle).  A concurrency-focused test suite (`RefreshTokenAtomicRotationTests`)
  with 7 test cases was added to `test/WalkingTec.Mvvm.Core.Test/Security/`.

- **ETL: MssqlBulkLoader schema-qualified existence check; EtlQuartzJob terminal-failure trigger label** (#136): `EnsureStagingTableAsync` now filters `INFORMATION_SCHEMA.TABLES` on both `TABLE_NAME` and `TABLE_SCHEMA` (defaulting to `dbo`) so a same-named staging table in another schema no longer causes `CREATE` to be silently skipped. `EtlQuartzJob.Execute` terminal-failure else branch no longer overwrites the original trigger value (e.g. `Scheduled`) with `Retry`, so `EtlRunLog.Trigger` accurately reflects how the job was initiated.


- **`GetRemoteIpAddress` no longer trusts `X-Forwarded-For` by default** (#114):
  `HttpContextExtention.GetRemoteIpAddress` previously read the raw
  `X-Forwarded-For` header unconditionally, allowing any attacker to spoof
  their client IP and bypass maintenance-mode allow-lists, rate-limit
  partitions, CSP-report per-IP buckets, and `WtmIpAllowListAttribute`.
  The method now returns `Connection.RemoteIpAddress` (the verified TCP peer
  address) by default.

  **Migration** — choose one:

  1. *(Recommended)* **Configure ASP.NET Core's built-in `ForwardedHeaders`
     middleware** so that `Connection.RemoteIpAddress` is already set to the
     real client IP by the time WTM middleware runs.  Call in `Program.cs`:
     ```csharp
     builder.Services.AddWtmForwardedHeaders(opts =>
     {
         opts.KnownNetworks.Add(new IPNetwork(IPAddress.Parse("10.0.0.0"), 8));
         // add all your proxy subnets
     });
     // ... then in app pipeline (before UseRouting):
     app.UseWtmForwardedHeaders();
     ```
     No other code changes are needed — all call sites continue to call
     `GetRemoteIpAddress()` and automatically receive the validated client IP.

  2. *(Temporary back-compat)* Set `TrustForwardedForHeader: true` in
     `appsettings.json` (or via `AddWtmContext`) to restore the old
     raw-XFF-first behaviour:
     ```json
     { "TrustForwardedForHeader": true }
     ```
     This is spoofable when the app is not behind a trusted proxy that strips
     inbound `X-Forwarded-For` values.  Use only as a short-term measure while
     migrating to option 1.
- **Security (#116): Close cross-tenant data-read via forged `Referer` header.**
  `CreateDC()` previously used the HTTP `Referer` header to select a tenant's
  database whenever `LoginUserInfo.CurrentTenant` was `null` — a condition that
  includes authenticated main-host administrators.  An authenticated attacker
  could send a forged `Referer` matching another tenant's domain and read that
  tenant's data through any `[AllRights]` endpoint.  The Referer-based tenant
  routing now applies **only to unauthenticated requests** (`_loginUserInfo ==
  null`).  For authenticated users, the tenant is derived solely from identity
  claims; a `null` tenant means main-host database.

  **Migration note:** Main-host administrators who previously relied on a
  `Referer` header to implicitly browse a tenant's data must instead use the
  explicit, authorised tenant-switch mechanism (e.g. the `SetTenant` action in
  `_FrameworkController`).  No changes are required for tenant users or for
  unauthenticated flows (e.g. per-domain login pages).

  **New opt-in flag:** Set `DisableRefererTenantResolution: true` in
  `appsettings.json` (under the WTM configuration section) to disable
  Referer-based tenant routing entirely — including for unauthenticated
  requests.  Recommended for security-strict deployments where tenant identity
  is always established through claims or explicit configuration.
- **ETL RerunFromSnapshotAsync — data-loss TOCTOU race (HIGH)** (#120):
  `RerunFromSnapshotAsync` had two related bugs that silently caused reruns
  to start from the wrong watermark, skipping the intended replay window.

  **Bug 1 (Running guard):** If the job was executing when a rerun was
  requested, Quartz's `[DisallowConcurrentExecution]` would queue the new
  trigger rather than reject it.  The still-running job's `finally` block
  then wrote `result.NewWatermarkValue` ("W2") back to `LastWatermarkValue`,
  overwriting the snapshot value ("W0") that had just been saved by
  `RerunFromSnapshotAsync`.  When the queued trigger fired it read W2 from
  the DB and reported Success, silently skipping all data in [W0, W2).
  Fix: `RerunFromSnapshotAsync` now throws `InvalidOperationException` if
  `jobDef.Status == EtlJobStatus.Running`, rejecting the rerun immediately
  so the operator knows to retry once the current execution finishes.

  **Bug 2 (JobDataMap override — defense in depth):** Even when the job is
  not Running at the moment the check runs, a residual window exists between
  the DB write and `TriggerJob` where another execution could overwrite the
  watermark.  Fix: `TriggerNowAsync` now accepts an optional
  `watermarkOverride` parameter.  `RerunFromSnapshotAsync` passes the
  snapshot watermark ("W0") through Quartz's per-trigger `JobDataMap`
  (key `EtlWatermarkOverride`).  `EtlQuartzJob.Execute` reads this key
  before falling back to `jobDef.LastWatermarkValue`, so the intended
  replay start point is preserved even if the DB value is modified between
  the write and the trigger firing.  Normal scheduled and manual triggers
  do not pass this key and are completely unaffected.

  **API compatibility:** `TriggerNowAsync(Guid)` callers require no
  changes — the `watermarkOverride` parameter defaults to `null`.
- **Analysis engine: six correctness and security fixes (#130)**:
  (M1) `FilterCondition.Value` null-guard — relative-date token checks and
  all other `f.Value` accesses now use null-safe operators, preventing NullReferenceException
  when STJ deserialises a missing JSON field. (M2) Unbounded filter/sort
  clause lists capped at 50 entries (`MaxFilterClauses`) in `_AnalysisController` — requests
  exceeding the cap are rejected with HTTP 400 before reaching the Expression-tree builder.
  (M3) Pivot row-key collision fixed — `|` and `\` in dimension values are now
  backslash-escaped before joining with `|`, so a value containing the delimiter
  can no longer shadow a different row. (M4) Data-privilege fingerprint folded into the
  cache identity key — stale cached results can no longer be served to users whose
  row-level data-privilege set has changed since the cache was populated. (M5)
  `ServerSideGroupByStrategy` now throws `InvalidOperationException` when more than
  3 measures are requested, surfacing the error explicitly instead of silently
  dropping measure 4+. (M29) `ComputeHash` returns `null` when `identityKey` is
  absent — cache get/set are skipped entirely for identity-less callers, closing
  a cross-user cache read vector.
- **BaseImportVM: three import correctness fixes (#131)**: (M7) blank rows mid-file
  no longer truncate the import — a `return` inside the row loop was replaced with
  `continue` so separator blank rows are skipped and subsequent data rows are
  still processed. (M8) XLSX uploads exceeding `MaxImportFileBytes` (default 10 MiB,
  virtual/overridable) are now rejected before NPOI loads the workbook into memory;
  `OutOfMemoryException` is rethrown instead of being silently swallowed as
  "WrongTemplate". (M9) a sub-table row that appears before any parent row (i.e.
  `EntityList.LastOrDefault()` returns null) now adds a clear diagnostic error
  ("Sub-table row appears before any parent row") and skips the row, instead of
  dereferencing null and producing an unhandled 500.
- **Cache stampede fixes in Core services** (#133): two stampede-protection defects corrected. (M21) `WtmTenantService._keyLocks` promoted to `static` so per-key `SemaphoreSlim` locks are shared across all scoped instances — previously each request got a fresh empty dictionary, letting concurrent cold-key requests each issue an independent DB query. (M10) `LookupCacheService` fall-through callers (semaphore timeout) now skip `SetCache`; they return the DB result directly, preventing unlocked writes that could overwrite a fresher value stored by the lock holder.
- **Dashboard `GetWidgetDataAsync` ignores tenantId — cross-tenant widget data (#137)**:
  `GetWidgetDataAsync` called `GetAsync(dashboardId)` without forwarding the tenantId, so
  all widget-data requests read from the `_default` storage directory. Tenant-scoped dashboards
  (stored under `tenantId/`) were never found, and requests could inadvertently resolve
  `_default` data regardless of the caller's tenant. Fixed by adding `string? tenantId = null`
  to both `IDashboardService.GetWidgetDataAsync` and `JsonFileDashboardService.GetWidgetDataAsync`,
  and updating `_DashboardController.GetWidgetData` / `PostWidgetData` to forward the
  authenticated tenant identity from `LoginUserInfo.TenantCode`. Existing callers that omit
  the parameter continue to resolve `_default` dashboards unchanged.
- **Analysis: four low-severity defects fixed (#149)**: (L1) `FilterCondition.Values` (List&lt;string&gt;) is now honoured before comma-splitting `FilterCondition.Value` in the In/NotIn branch; (L2) `MemoryAnalysisCache.Set` moves `_cache.Set` inside the same `lock(_lock)` block as `AddExpirationToken`, closing a race with `InvalidateAll`; (L20) `AnalysisSavedQuery.ConfigJson` gains `[StringLength(65536)]` and `SaveQuery` rejects inserts exceeding the 100-row per-user cap; (L21) `Export`/`PivotExport` validate the `format` query-string against the allowlist {xlsx, csv} before logging, preventing log injection.
- **ETL: missing WatermarkColumn warns instead of silently freezing; MssqlBulkLoader timeouts default to 300 s** (#153): when the configured `WatermarkColumn` is absent from the batch schema, `EtlPipelineExecutor` now logs a warning and records it in `ValidationWarnings` so operators discover the misconfiguration immediately instead of silently re-processing already-ingested rows; `MssqlBulkLoader` replaces all hardcoded `BulkCopyTimeout = 0` / `CommandTimeout = 0` (infinite) with a constructor-injected `timeoutSeconds` (default 300) so pathological network/SQL hangs abort after 5 minutes rather than hanging the job permanently.
- **Five low-severity bugs fixed (#154)**: `FormatText` OOB on odd `&&` count (L15); `_CodeGenController.SetField` NRE on unknown type string (L22); `RevokeDescendantsAsync` now logs a warning when the revocation chain exceeds depth 50 (L18); `LookupCacheWarmupService` XML-doc clarified to document single-tenant-only warmup semantics for multi-tenant deployments (L6); `WtmHealthCheckResponseWriter` redacts raw exception messages (which may contain connection-string fragments) in non-development environments (L19).

## [10.5.3] - 2026-05-23

Patch release: one significant performance improvement to reflection
hot paths and one bug fix discovered during a coverage audit. No new
features, no breaking changes.

Locally verified: `dotnet build -c Release` → 0 errors;
`dotnet test -c Release` → 3,394 pass / 0 fail; `dotnet list package
--vulnerable --include-transitive` → 0 vulnerable.

Bundled internally (not separately listed below as they have no user-
facing behaviour change): seven coverage test PRs (#37, #39, #41, #43,
#46, #48, #50; Phases 1–7, ~1,270 new tests) that lifted codebase line
coverage from ~40% to ~50% and the `Core` project from ~55% to ~70%.

### Performance

- **Reflection + expression cache for hot paths** (#33, #34):
  `PropertyHelper` and `AnalysisFieldScanner` now memoize per-type
  reflection and compiled-expression results in `ConcurrentDictionary`.
  Independently verified with BenchmarkDotNet (Apple M4, .NET 10.0.5,
  ShortRun): **18× faster** on `AnalysisFieldScanner.ScanModel`, up to
  **135× faster** on `PropertyHelper` getter paths, and per-call
  allocation eliminated in three methods. Localizer behaviour is
  unchanged — only the raw reflection/expression results are cached;
  the localizer is still invoked per call so culture changes take
  effect immediately.

### Fixes

- **`GetCleanCrudVM` computed-property crash** (#44, #51): the inner
  copy loop in `SystemExtension.GetCleanCrudVM` previously called
  `PropertyInfo.SetValue` without checking `CanWrite`, throwing
  `ArgumentException: Property set method not found` when iterating an
  entity with a computed get-only property (e.g. `TopBasePoco.IsBasePoco`).
  The crash aborted the entire copy loop, silently skipping every
  property declared after the computed one. Added a `CanWrite` guard at
  the top of the inner loop, matching the existing guard in the outer
  loop. Regression tests added.

## [10.5.2] - 2026-05-17

Security-focused release driven by a deep bug-hunt audit. Six issues
(#20, #22, #24, #26, #28, #30) fixed across six merged PRs (#21, #23,
#25, #27, #29, #31). Highest-impact change is closing a P0 privilege-
escalation in `_FrameworkController.BatchAssignRoles`.

Locally verified: `dotnet build -c Release` → 0 errors;
`dotnet test -c Release` → 2061 pass / 0 fail (baseline 2035 + 26 new
regression tests across the six PRs);
`dotnet list package --vulnerable --include-transitive` → 0 vulnerable.

### Security

- **P0 RBAC bypass fix** (#30, #31): `_FrameworkController.BatchAssignRoles`
  was marked `[AllRights]`, allowing any authenticated user to grant
  themselves any role (privilege escalation to admin). Added runtime
  admin check that returns 403 for non-admin callers. Same guard added
  to the three `RemoveUserCacheBy*` cache-invalidation endpoints.
- **JWT lifetime hardening** (#24, #25): reject JWTs missing an `exp`
  claim or with a future `nbf` claim in the primary auth pipeline.
  Previously, the custom `LifetimeValidator` silently treated no-`exp`
  tokens as valid forever, undermining `ValidateLifetime`. Tokens
  issued by `TokenService` are unaffected since they always include
  `exp`.
- **JWT query-token scoping** (#24, #25): narrow JWT query-string token
  acceptance to WebSocket upgrade requests only. Prevents
  `?access_token=…` leaking into HTTP logs, browser history, and
  `Referer` headers on regular HTTP requests.
- **Login timing-side-channel** (#26, #27): `DoLoginAsync` now performs
  a discarded BCrypt comparison when the ITCode does not exist, keeping
  response times comparable with the user-exists / wrong-password path.
  Previously, the timing gap (~5 ms vs ~100 ms) let attackers enumerate
  valid ITCodes over the network.
- **File path-traversal defense-in-depth** (#22, #23):
  `WtmLocalFileHandler.GetFileData` and `DeleteFile` now validate the
  resolved path stays inside a configured upload root, matching the
  guard that already applied to `Upload()`. Closes a defense-in-depth
  gap when `FileAttachment.Path` is poisoned via another vector.
- **MD5 legacy compare constant-time** (#22, #23):
  `PasswordHashHelper` MD5 migration branch now uses
  `CryptographicOperations.FixedTimeEquals` instead of `string.Equals`.
- **CSP report bucket eviction** (#26, #27):
  `WtmCspReportMiddleware` evicts empty rate-limit buckets older than
  60 s instead of accumulating one entry per distinct client IP for
  the lifetime of the process. Prevents unbounded `ConcurrentDictionary`
  growth under hostile traffic.

### Fixed

- **Triple-`!` null-forgiving chains in `WTMContext.DoLoginAsync`**
  (#20, #21): replaced 4 chains (`HttpContext!.User!.Identity!`) with
  `?.` null-safe equivalents. Resolves a CLAUDE.md red-line violation
  and removes the NRE risk when `WTMContext` is used from non-HTTP
  contexts (background jobs, MockWtmContext).
- **Silent catches in `WtmFileProvider.GetFile` and
  `BaseCRUDVM.DoRealDelete[Async]`** (#20, #21): now log the swallowed
  exception via `ILogger.LogWarning` / `LogError` instead of discarding
  it. Improves admin triage for delete failures.

### Tests

- **47 `TimeSpan.Seconds` → `TotalSeconds` corrections** (#28, #29):
  test assertions used the seconds component (0-59) when total elapsed
  was intended. Tests fail on slow CI (≥10 s) and silently pass when
  duration is 60-69 s. All occurrences in `BaseCRUDVM*Test`,
  `FrameworkUser*Test`, `FrameworkRoleApiTest` files corrected.
- New test files added by the six PRs (31 new tests total):
  `BatchAssignRolesRbacTests.cs`, `JwtLifetimeValidatorTests.cs`,
  `LoginTimingSideChannelTests.cs`,
  `WtmCspReportRateBucketEvictionTests.cs`,
  `WtmLocalFileHandlerPathTraversalTests.cs`, plus expanded
  `PasswordHashHelperTests.cs`.

### Documentation

- 11 `IgnoreQueryFilters()` call sites now carry rationale comments per
  `.claude/rules/dotnet-conventions.md` (#20, #21).

## [10.5.1] - 2026-05-13

Infra-only release. All NuGet publish and CI now run on Gitea; GitHub
(github-archive mirror, nuget.pkg.github.com, GitHub Actions Marketplace)
is fully retired. No source code changes — same binaries as 10.5.0.

### Changed

- **NuGet publish target** moved from `nuget.pkg.github.com/cct08311github/`
  to Gitea NuGet registry (`/api/packages/chiu0831/nuget`).
- **CI workflows** continue to live in `.github/workflows/` (Gitea Actions
  reads this path natively); the publish secret is now `PAT_TOKEN`.
- **`common.props`** `RepositoryUrl` / `PackageProjectUrl` now point to the
  Gitea repo URL.
- **`scripts/release-github-package.sh`** renamed to
  `scripts/release-gitea-package.sh`; internals rewritten from `gh` CLI to
  `curl` against the Gitea API.
- **`e2e-test.yml`** dropped the `mikepenz/action-junit-report` step
  (GitHub Marketplace-only action). E2E artifacts continue to upload via
  `actions/upload-artifact`.

### Added

- **`scripts/publish-to-gitea.sh`** — local manual publish fallback used
  when the Gitea Actions runner is unavailable. Supports `--suffix
  <pre-release>` and `--dry-run`. Token sourced from the `GITEA_TOKEN`
  environment variable or `~/.gitea-token`.

### Security

- `scripts/publish-to-gitea.sh` masks the Gitea token in `--dry-run`
  output (`<prefix>***`) instead of echoing the full secret. Real
  execution still passes the full token to `dotnet nuget push`.

## [10.5.0] - 2026-04-26

Major feature release. 10 opt-in middleware/attributes (maintenance mode,
request timeouts, ETag, idempotency, server-timing, feature gate, IP
allow-list, cache-control, deprecated, secure-headers overwrite),
11 Analysis Mode BI extensions (Sort/TopN, relative-date tokens,
DistinctCount, HavingFilters, GrandTotal, configurable Limits, CompareWith,
auto Insights, drill-through, Excel/CSV total rows), 7 ETL enhancements
(per-batch retry with backoff, column mapping, schema discovery, dashboard,
Replace load mode, dry-run validation hardening), 3 CSP security
hardenings (3-state Mode enum, frame-ancestors directive, server-side
violation report endpoint with rate-limit). All additive / opt-in —
zero breaking change.

Plus the security release-hardening that gated this version: OpenTelemetry
1.15.0 → 1.15.3 transitive bump (#849, GHSA-g94r/q834/mr8r) and
TC-04 e2e flake fix (#851).

### Added
- **Analysis Mode: drill-through helper (`AnalysisDrillThrough.BuildQuery`)** — Closes the canonical "click a group → see the raw rows behind it" UX that every dashboard wants but none of WTM's public surface previously supported; apps had to manually rebuild filter expressions on the controller side, duplicating the engine's filter-and-validate logic. New `AnalysisDrillThrough.BuildQuery<TModel>(baseQuery, originalReq, groupValues, whitelist)` returns the source `IQueryable` filtered down to the rows that aggregated into the clicked result row. Reuses the engine's `ApplyFilters` (now `public`) so all whitelist + type-conversion + relative-date semantics stay consistent. `originalReq.Filters` are preserved (drill stays within the dashboard's existing scope). For dimensions with `DateHierarchy.Year/Quarter/Month/Day`, the human-readable label ("2026 Q1") is reversed via the new `DateTruncator.TryParseLabel` into a half-open `[start, endExclusive)` range filter — single-point Eq comparison against millisecond-precision DateTime would always match zero rows. Unparseable date labels short-circuit to `Take(0)` so the dashboard renders "no rows" gracefully (vs. throwing or, worse, returning unfiltered). Missing dimension values widen the drill (no filter for that dim). Multi-dimension drills AND together. 13 new tests cover string-dimension drills, original-filter preservation, year/quarter/month/day hierarchy reversals, unparseable-label graceful degradation, multi-dimension AND, unknown-dimension validation, missing-value widening, null-arg guards, and `DateTruncator.TryParseLabel` round-trips. Targeted Analysis regression: 153/153 green.
- **ETL: per-batch retry with exponential backoff (`MaxBatchRetries`)** — Closes the operational gap that any single `BulkLoad` blip — DB lock timeout, network jitter, transient deadlock — failed the entire job, forcing the next run to re-extract from scratch. New `EtlPipelineConfig.MaxBatchRetries` (default `0` — back-compat with 10.4.x), `BatchRetryBaseDelayMs` (default 200), `BatchRetryMaxDelayMs` (default 30,000ms). When > 0, `BulkLoadAsync` is wrapped in a retry loop with full-jitter exponential backoff: `delay = random(0, BaseDelay × 2^attempt)`, clamped to the max. `MaxBatchRetries` is clamped to `[0, 50]` at runtime so a misconfigured value can't loop forever. Cancellation is honoured during the wait — a token cancelled mid-backoff surfaces as `Aborted = true`. Watermark contract unchanged: a recovered run commits, an exhausted-budget run discards. Failed runs preserve the original exception sanitization. New `EtlExecutionResult.RetryAttemptsTotal` reports cumulative retries across all batches (0 = no retries needed); useful for observing "how flaky is my ETL?" over time. `MockBulkLoader.TransientFailuresBeforeSuccess` simulates flaky bulk-load behavior so apps can unit-test their own pipeline. 8 new tests cover default off (back-compat), single + multiple transient recoveries, exhausted-budget abort with retry count surfacing, watermark commit/discard contracts, cancellation during backoff, and clamp behavior on negative values. Targeted ETL Pipeline regression: 80/80 green (5 Oracle integration tests skipped — require live DB).
- **Analysis Mode: auto-generated BI insight narrative (`Insights`)** — Turns the analysis surface from "raw numbers" into "data + storytelling": one request now returns the rows AND 2‒5 short Chinese sentences ready to drop into a dashboard callout box. Until now every WTM dashboard re-implemented the "find top performer / largest delta / outlier" narration in JS; now the engine ships a focused heuristic set out of the box. New `AnalysisQueryRequest.IncludeInsights: bool` (default `false` — back-compat). When `true`, `AnalysisQueryResponse.Insights: List<string>?` carries lines from five heuristics applied to the first measure: (1) top performer + ratio to average ("本期最高: 北 (金額 合計 = 1,000)，為平均的 2.31 倍"), (2) bottom performer (skipped when only one group, to avoid duplicating top), (3) period-over-period leaders when `CompareWith` is on ("與對比期相比，北 漲幅最大 (+25.0%)，南 跌幅最大 (-20.0%)"), (4) Pareto concentration when N ≥ 5 and top 20% carry ≥ 80% of total ("Top 1 群組佔總計的 86.6%（Pareto 集中度高）"), (5) z-score outliers when N ≥ 4 ("1 個群組為異常離群值（|z| > 2.0）：Outlier (金額 合計, z = 2.66)"). Each heuristic runs in its own try/catch so a single failing rule never costs the others; empty/single-row/all-null measures degrade to an empty list (UI hides the callout). Computed AFTER Sort+TopN so the narrative reflects what the user actually sees on screen. Output strings are explicitly unstable across versions — callers must NOT parse them; use the underlying numeric columns for programmatic access. Both sync + async paths wired identically. 12 new tests cover default off, top + bottom + comparison + Pareto + outlier heuristics in isolation, the small-N safe degradations, and the empty-result null safety. Targeted Analysis regression: 255/255 green.
- **Analysis Mode: period-over-period comparison (`CompareWith`)** — Killer-feature for every dashboard that ever needed "本月 vs 上月" / "this year vs last year" / "A 通路 vs B 通路". Currently apps fire two separate queries and stitch the results in JS; the new `AnalysisQueryRequest.CompareWith` (a `ComparisonRequest` with its own `Filters` set + optional `Label`) collapses the whole dance into a single request. The engine runs the second query with the alternate filter set, joins by dimension-tuple, and augments each result row with **three derived columns per measure**: `{Field}_{Func}_Compare` (the comparison-period value), `{Field}_{Func}_Delta` (current − comparison), `{Field}_{Func}_ChangePct` (the percent change as a decimal — `0.25` = +25%). Rows that exist only in the comparison set are appended with the primary measure cells `null` so a region that lost all sales is visible, not invisible. Divide-by-zero guard: `ChangePct` is `null` when comparison value is 0 (avoids serialising `Infinity` into JSON). The Sort whitelist is auto-extended to recognise the derived columns, so `Sort = [{ Field = "Amount_Sum_ChangePct", Descending = true }]` does "top 5 fastest-growing regions" out of the box. `ColumnDisplayNames` map gets human-readable headers — `"金額 合計 (上期)"` / `"金額 合計 差值"` / `"金額 合計 變化%"`. The sub-request used internally drops `Sort` / `TopN` / `IncludeGrandTotal` / `HavingFilters` / `CompareWith` so the engine can't recurse into infinite re-execution and shape mismatch is impossible. Both sync and async paths wired identically. Compatible with every existing analysis feature (Sort / TopN / HavingFilters / GrandTotal / DistinctCount / relative-date tokens). 12 new tests cover derived-column emission, dimension-tuple join with all four cardinality cases (matched / primary-only / compare-only / both null), divide-by-zero guard, Sort by derived columns, validator gate when `CompareWith` is null, display names, async parity, and the sub-request shape. Targeted Analysis regression: 243/243 green.
- **Analysis Mode: `AnalysisLimits` configurable result/raw-row caps** — Lifts the previously-hardcoded `10,000` (group result rows) and `50,000` (raw materialize rows) constants into a single `AnalysisLimits` static class so apps with bigger reporting surfaces (monthly SKU breakdowns, multi-tenant aggregates, year-end pivots) can raise the caps without forking the framework. `AnalysisLimits.MaxResultRows` (default 10,000) is read by both `InProcessGroupByStrategy` and `ServerSideGroupByStrategy` as well as the engine-level truncation guard, AND tracks the upper bound for `AnalysisQueryRequest.TopN` validation — raising the cap automatically widens the legal TopN range so the two settings always describe the same dimension. `AnalysisLimits.MaxMaterializeRows` (default 50,000) is the in-process raw-data load cap. Set-once-at-startup; tests override per case via `try / finally`. Defaults preserve the values WTM shipped through 10.4.x so callers that don't touch the class get identical behaviour. Closes the doc'd "Excel exports limited to 10,000 rows" Phase 1 limitation. 6 new tests cover defaults, lowered/raised result-row caps, TopN range tracking, and the materialize-row wrapper. Targeted Analysis regression: 504/504 green.
- **Analysis Mode: CSV exporter renders `GrandTotalRow` as footer line** — Pairs with the previous Excel-exporter change so `xlsx` and `csv` agree on what a "Total" row looks like (was: dashboard had total → Excel had total → CSV silently dropped it). `_AnalysisController.BuildCsv()` now appends a footer when `result.GrandTotalRow != null`: first `null` dimension cell carries the "總計" label, subsequent `null` dimension cells stay blank, `null` measure cells render as empty strings (not `"0"` — same anti-misleading rule as the Excel exporter), real measure values pass through `ToString()` and the existing CSV escaper. Defense-in-depth: even if a caller somehow lands a string starting with `=` / `+` / `-` / `@` in the grand-total cell, the same CSV-formula-injection escape (RFC 4180 + leading-tab) kicks in. Default behaviour (`GrandTotalRow == null`) is unchanged — every existing csv export keeps the previous bytes. Metadata block + footer coexist correctly.
- **Analysis Mode: Excel exporter renders `GrandTotalRow` as bolded yellow footer** — Closes the loose end from the previous `IncludeGrandTotal` change: the JSON response carried the rollup but `AnalysisExcelExporter.Export()` ignored it, so users got a total in the dashboard but a totals-less spreadsheet — exactly the scenario the feature was meant to eliminate. Now when `GrandTotalRow != null` the exporter appends an extra row after the body data with a light-yellow fill and bold font (eye-catches without overstyling), the first `null`-dimension cell carries the "總計" label, subsequent `null`-dim cells stay blank to mirror how Excel users typically write manual total rows by hand. Numeric measure cells render as `Numeric` cells (so Excel SUM / formatting still applies) and inherit the column's `MeasureFormat` (currency / percent / etc. stay consistent with the body). `null` measure cells (Avg / DistinctCount, by design) render as clean empty strings rather than `0` (which would falsely suggest "average is zero"). The chart anchor is shifted down one row when the footer is present so embedded charts still sit cleanly below the data. Default behaviour (`GrandTotalRow == null`) is unchanged — every existing exporter caller keeps the previous output bytes.
- **Analysis Mode: `IncludeGrandTotal` + `GrandTotalRow` for footer-row reporting** — Closes the dashboard pain point that report consumers always want a "grand total" footer row but the response had no slot for it; every WTM app re-implemented the rollup in JS / Excel post-processing. New `AnalysisQueryRequest.IncludeGrandTotal: bool` (default `false` — back-compat). When `true`, `AnalysisQueryResponse.GrandTotalRow` is populated. Aggregation rules per measure: `Sum` / `Count` → sum of group values; `Max` → max; `Min` → min; `Avg` / `DistinctCount` → `null` (a meaningful weighted Avg requires per-group sample counts the GroupBy result drops; per-group distincts can't be summed without re-querying — silent wrong answers are worse than `null`). Dimension cells emit `null` so the client is free to append "Total" / "總計" anywhere it fits the rendering surface. Total scope is **post-HAVING / pre-TopN** — same universe as `TotalCount`, so "showing 3 of 5 (sum 1,234)" stays consistent regardless of how many rows TopN trims off the visible list. Empty-result case still emits `GrandTotalRow` with `null` measure cells (rather than `0`, which would be misleading) so the footer can render an empty state. Both sync `Execute` and async `ExecuteAsync` paths wired identically.
- **Analysis Mode: `HavingFilters` post-aggregation filter** — Closes the BI gap that the analysis surface had `Filters` (pre-aggregation, equivalent to SQL `WHERE`) and Sort/TopN (post-aggregation), but no way to express the canonical "regions where Sum(Amount) ≥ 1M" / "categories with > 100 distinct customers" question — apps had to fetch all groups and filter client-side, then risk losing the genuine survivors at the 10,000-row truncation. New optional `AnalysisQueryRequest.HavingFilters: List<HavingFilter>?`. Each `HavingFilter` carries `Field` (must be a `{measure.Field}_{measure.Func}` of a requested measure — same naming as `Sort` and `AnalysisQueryResponse.Columns`), `Operator` (numeric subset of `FilterOperator`: `Eq` / `NotEq` / `Gt` / `Gte` / `Lt` / `Lte`), and `Value` (parsed to `decimal` with `InvariantCulture`). Multiple filters AND together. Validation rejects out-of-whitelist fields, empty fields, and unsupported operators (`Contains` / `In` / etc. — they don't apply to scalar aggregate values). Execution pipeline now matches SQL standard: `GROUP BY → HAVING → ORDER BY → LIMIT`. `TotalCount` reflects the **post-having** group cardinality, so the client can render "showing 3 of 5" against the user's filtered universe rather than the pre-filter baseline. Conservative on malformed values: an unparsable `Value` filters out all groups (rather than silently widening). Default request (no `HavingFilters`) is unchanged — every existing caller keeps the previous engine behaviour. Both sync `Execute` and async `ExecuteAsync` paths wired identically.
- **Security: `UseWtmCspReport()` server-side CSP-violation report endpoint** — Closes #845. Pairs with `WtmCspOptions.ReportUri`: when the operator points the directive at `/_csp/report` (default), the middleware now actually has somewhere to land. Reads the browser-posted JSON (canonical `{"csp-report": { ... }}` envelope or the looser flat shape some non-Chromium clients emit), parses it into a strongly-typed `CspViolationReport` (kebab-case property mapping via `[JsonPropertyName]`), and either invokes `WtmCspReportOptions.OnReport` callback or emits a structured `Warning` Serilog line `CspViolation Document=… Directive=… Blocked=… Source=…:…` so existing log pipelines become the policy-violation observability surface — no Sentry / Datadog adapter required out of the box. Defenses against amplification: per-IP sliding-60-second rate limit (default 10/min, returns 429 when exceeded; set 0 to disable), 8 KiB body cap (returns 413 when exceeded), POST-only method gate (other verbs fall through so the path can host other handlers if reused), `OnReport` callback exceptions are caught and logged so a faulty dispatcher cannot 500 the report endpoint and leak retry traffic. Browser receives `204 No Content` on success, `400` on malformed body, `429` when rate-limited, `413` when oversized. `WtmCspReportMiddleware.ParseReport` and `CheckAndRecordRate` are public for unit-test determinism. Opt-in via `app.UseWtmCspReport()` placed before `UseWtmContentSecurityPolicy()`. Zero breaking change.
- **Security: `WtmCspMode` three-state mode for CSP middleware** — Closes #846. Replaces the boolean `WtmCspOptions.ReportOnly` with a `WtmCspMode` enum (`Disabled` / `ReportOnly` / `Enforce`, default `Enforce`). The new `Disabled` state is the operationally-essential third option that lets ops kill the policy via `appsettings.{Env}.json` toggle without ripping out the `app.UseWtmContentSecurityPolicy()` call — incident response, dev debugging, canary rollouts. The legacy `ReportOnly` bool stays in place with `[Obsolete]` so existing callers keep working unchanged; `Mode` wins when set non-default. Middleware short-circuits to a true no-op when `Mode == Disabled` (no header, no `OnStarting` registration). `WtmCspMiddleware.ResolveHeaderName()` is public for unit-test determinism.
- **Security: `WtmCspOptions.FrameAncestors` clickjacking defense** — Closes #844. Adds the modern `frame-ancestors` directive, the CSP-native clickjacking control that supersedes the legacy `X-Frame-Options` header (per OWASP Clickjacking Cheat Sheet + MDN). Default `'none'` for safe-by-default; set `'self'` for legitimate self-embedding (modal preview, admin sub-frames). When `null` the directive is omitted from the emitted header. `frame-src` (outward) and `frame-ancestors` (inward) coexist — they answer opposite questions.
- **Security: `WtmSecureHeadersOptions.Overwrite` defense-in-depth flag** — Closes #843. Default `false` keeps the existing first-writer-wins semantics. Set `true` for banking / healthcare / regulated apps that must guarantee certain headers regardless of upstream proxy configuration — typical scenario: proxy injected `X-Frame-Options: SAMEORIGIN` but the app needs to enforce `DENY`. `Overwrite = true` does NOT bypass the HTTPS-only HSTS gate (HSTS still requires `Request.IsHttps`), and disabled headers (`Options.X = null`) stay disabled — the flag only flips the precedence, not the disable contract. New `WtmSecureHeadersMiddleware.SetHeader()` is public for unit-test determinism.
- **Analysis Mode: `DistinctCount` aggregate function** — Closes the long-standing pain point that the analysis surface had `Count` (rows per group) and `Sum`/`Avg`/`Max`/`Min` (numeric reductions) but no way to ask the canonical BI question "how many *unique* customers in each region?" or "how many distinct products were sold per category?" — apps had to issue two separate queries and stitch the result. New `AggregateFunc.DistinctCount = 32` flag, valid in `[Measure(AllowedFuncs = ...)]` like every other aggregate, with display name "不重複計數". Both groupby strategies implement it: `InProcessGroupByStrategy` runs `g.Select(propGet).Where(non-null).Distinct().Count()` on the materialised group (handles non-numeric values gracefully — important for FK-id columns); `ServerSideGroupByStrategy` emits the canonical EF Core 8+ pattern `g.Select(e => e.Prop).Distinct().Count()` which translates to SQL `COUNT(DISTINCT col)` — pushed down to the database for the entire row set, no in-memory materialisation. NULLs are excluded from both paths (matches ANSI-SQL `COUNT(DISTINCT)` semantics). Result column follows the existing `{Field}_{Func}` naming so the new `Sort` field can target a `CustomerId_DistinctCount` column. `_AnalysisController.GetAllowedFuncNames()` exposes "DistinctCount" to the front-end picker. Existing 5 aggregate functions unchanged. 6 tests cover flag-bit independence, in-process per-group + null-exclusion, EF Core server-side round-trip, AllowedFuncs gating, and Sort integration. Targeted Analysis suite (QueryEngine, both GroupBy strategies, Sort+TopN, RelativeDateTokens, DistinctCount): 150/150 green.
- **Analysis Mode: 9 new relative-date filter tokens** — Closes the dashboard pain point where the filter UI's "this week / last 30 days" presets stopped at 8 tokens; common BI questions like "yesterday's exceptions" or "Q3 vs Q4" required hand-rolled date pairs. New tokens (case-insensitive, like the existing set): `@yesterday` (single day = today − 1), `@nextWeek` / `@nextMonth` (forecast windows for ops dashboards / scheduled jobs), `@last7days` / `@last90days` / `@last365days` (rolling windows alongside the existing `@last30days`), `@lastQuarter` (full previous calendar quarter — distinct from `@thisQuarter` which is to-date semantics), `@thisYear` (Jan 1 through Dec 31 — distinct from `@ytd` which clamps to today), `@lastYear` (full previous calendar year). Same emit semantics as the pre-existing tokens — each expands to a `(Gte, Lte)` pair on the original filter's field, so server-side type conversion + Expression-tree assembly are unchanged. 13 tests cover each new token's bounds, the `@thisYear` ↔ `@ytd` distinction (matters except on Dec 31), the `@lastQuarter` 3-month length invariant, case-insensitivity, backwards compatibility on all 8 pre-existing tokens, and the unknown-token error message. Pre-existing 8 token tests remain green. Zero breaking change — additive switch arms only.
- **Analysis Mode: result `Sort` + `TopN` on `AnalysisQueryRequest`** — Closes the long-standing gap that the analysis surface had no way to express "top 5 regions by sales DESC" — callers had to fetch up to 10,000 rows and sort client-side, then risk having the genuine top-N truncated by the row cap. Two new optional fields on `AnalysisQueryRequest`: `Sort: List<SortSpec>?` (each `SortSpec` carries `Field` + `Descending`; multi-key sort by repeating); `TopN: int?` (1‒10,000). `Sort.Field` must reference either a requested dimension or a measure-result column (`{Field}_{Func}`, e.g. `Amount_Sum`) — same naming as `AnalysisQueryResponse.Columns`. Out-of-whitelist sort fields are rejected at validation with a clear error so callers can't smuggle in fields the engine never selected; out-of-range `TopN` (≤0 or >10,000) is also rejected. `TopN` trims `Rows` but `TotalCount` continues to report the full underlying group cardinality, so the client can render "showing 3 of 5". `SortValueComparer` handles the mixed-type measure values cleanly: numeric types unify via `decimal`, dates compare directly, mixed-type pairs fall back to ordinal string compare so the sort is deterministic instead of throwing. Default request (no `Sort`, no `TopN`) is unchanged — every existing caller keeps the previous engine behaviour. Both sync `Execute` and async `ExecuteAsync` paths wired identically.
- **Reliability: `UseWtmRequestTimeouts()` per-request deadline middleware** — Natural pair with `UseWtmSlowRequestLogging()` (#840): the latter logs slow outliers, this kills runaways. Replaces `IHttpRequestLifetimeFeature` with a deadline-linked `CancellationToken` so EF Core / `HttpClient` / `Task.Delay(_, ct)` and any other framework that observes `HttpContext.RequestAborted` cancel cooperatively. When the deadline trips before the response started, the middleware writes a `504 Gateway Timeout` with `application/problem+json` (`type` / `title` / `status` / `detail` / `timeoutMs` / `traceId`) and emits a structured `Warning` Serilog line `RequestTimeout Path={Path} Method={Method} TimeoutMs={Timeout}`. If the response has already started (chunked stream, SSE), the connection is aborted instead — the only correct option since we can't retroactively send a 504 over a partially-written body. `WtmRequestTimeoutsOptions`: `DefaultTimeoutMs` (default 30 s — generous for ordinary CRUD, tight enough that a runaway query / external call doesn't park a thread for minutes; set to 0 to disable the global default), `PathOverrides` (longest-prefix-wins; e.g. `/api/export` → 5 min, `/api/admin/migrate` → 10 min, `/sse` → 0 to opt out for streaming endpoints), `PathExclusions` (defaults match the rest of the bundle: `/healthz`, `/_framework`, `/_js`, `/_content`, `/favicon.ico`). Even when a handler swallows the cancellation and returns normally, the middleware still converts an empty 200 to a 504 — a misbehaving handler can't accidentally hide a deadline trip from observability. Distinguishes "deadline killed handler" from "client gave up first" (both surface as `OperationCanceledException`) by tracking whether the original `RequestAborted` cancelled separately — only the former produces a 504. Opt-in via `app.UseWtmRequestTimeouts()` immediately after `UseRouting`. Zero breaking change.
- **Caching: `[WtmNoCache]` + `[WtmCacheControl]` response-cache-control attributes** — Two small, developer-facing attributes covering the opposite halves of HTTP cache-control: suppression and explicit declaration. `[WtmNoCache]` emits the full defence-in-depth suppression bundle on the decorated controller/action — `Cache-Control: no-store, no-cache, must-revalidate, max-age=0, private`, `Pragma: no-cache` (HTTP/1.0 fallback still found in enterprise proxies), `Expires: 0` — fixing the classic "after logout the browser back-button still shows the profile page" production bug in one line. `[WtmCacheControl("public, max-age=300")]` sets an explicit `Cache-Control` value verbatim (no parsing — callers own RFC-7234 correctness); optional `Vary = "Accept-Encoding, Authorization"` property adds the paired `Vary` header so intermediaries know what to key on. Both implement `IAsyncResultFilter` so MVC auto-discovers them — no `Program.cs` registration. Headers are written via `Response.OnStarting` so they land even when downstream result writers short-circuit. First-writer-wins: an upstream-set `Cache-Control` (reverse proxy, more-specific sibling filter) is preserved untouched. `[WtmCacheControl]` ctor rejects null/whitespace values with a clear `ArgumentException` so a typo is caught at startup. Zero breaking change.
- **Security: `[WtmIpAllowList]` CIDR-based IP allow-list attribute** — Decorate a controller or action with `[WtmIpAllowList("10.0.0.0/8", "192.168.0.0/16", "172.16.0.0/12")]` and any request whose resolved client IP falls outside every supplied CIDR short-circuits with `403 Forbidden` before the action body runs. Multiple CIDRs are OR'd together; single-host rules use `/32` (IPv4) or `/128` (IPv6). Backed by `System.Net.IPNetwork` so IPv4 and IPv6 work uniformly. Implements `IAsyncAuthorizationFilter` so MVC auto-discovers it — zero Program.cs wiring. `FallbackStatusCode` lets callers switch to `404` (hide endpoint existence) / `451` / custom codes. Rejections are logged at `Warning` with the resolved IP and the CIDR list so the "who got blocked?" question is answerable from the normal log pipeline. Input CIDRs are validated at attribute-construction time (malformed strings throw `ArgumentException` with the offending index + value at the `[WtmIpAllowList(...)]` call site — surfaces the typo at startup, not on first request). Tolerates reverse-proxy quirks: accepts "addr, addr2" comma-lists from `X-Forwarded-For` by using the first entry (matches `GetRemoteIpAddress()` convention). Security caveat documented inline: `X-Forwarded-For` is trivially spoofable when the app is not behind a trusted proxy — combine with network-layer firewalling / auth tokens for public-internet exposure. Value is defense-in-depth: even when nginx / Cloudflare / ALB already enforces the same allow-list at the edge, carrying the rule in code means a proxy misconfiguration during a rushed deploy doesn't silently drop the control.
- **Rapid dev: `WtmDataSeeder` idempotent fixture seeder** — `await WtmDataSeeder.SeedAsync(dc, items, x => x.Code)` inserts every row whose business key (any `Expression<Func<TEntity, TKey>>`: string / int / Guid / …) is not already present in the table and returns a `WtmSeedResult(Added, Skipped)` report. Re-running the same seed call — whether from `Program.cs`, a test `[TestInitialize]`, a migration job, or a demo-restore hook — yields zero inserts, so "idempotent seed on every startup" becomes a one-liner. Existence check is a single `SELECT … WHERE Key IN (…)` round trip instead of per-item EXISTS, so seeding hundreds of fixture rows costs one query plus one `SaveChangesAsync`. `SaveChangesAsync` is called at most once and only when at least one row was actually added, so a pure-skip re-seed never triggers audit interceptors / SaveChanges filters — and never accidentally persists dirty in-memory state that happened to be attached to the DbContext. Duplicate keys *within* the input array collapse to the first occurrence (safe against cut-and-paste mistakes in fixture data). Insert-only semantics: seeder never updates existing rows (call site retains control over upsert if needed). Ships in `WalkingTec.Mvvm.Core.Helper`; zero API surface added to `DataContext` / `IDataContext`.
- **Bandwidth: `UseWtmETag()` conditional-request middleware** — Attaches a SHA-256-based `ETag` header to 2xx `GET` / `HEAD` responses and returns `304 Not Modified` (body-less) when the client sends a matching `If-None-Match`. Saves bandwidth on dashboard / reference-data / admin-list endpoints whose contents change only when someone actually mutates data — the server still computes the response (no compute savings vs. OutputCache), but the wire only carries ~300 bytes of headers instead of the full payload. `WtmETagOptions`: `EligibleMethods` (default `GET`+`HEAD` — `POST`/`PUT` ETag-ing is a footgun per RFC 9110 §15.4 and intentionally unsupported), `PathExclusions` (defaults match the other middleware bundles: `/healthz`, `/_framework`, `/_js`, `/_content`, `/favicon.ico` — static pipelines have their own strong ETags), `MaxBufferedBytes` (default 2 MiB — larger responses pass through unchanged so a rogue payload can't turn the middleware into a memory hog), `EmitWeakETag` (default `false`; switch on when downstream compression / minification may alter bytes without changing semantics). Matching follows RFC 9110 §13.1.2 / §8.8.3.2: wildcard `*` always matches, comma-separated list of tags, `W/` prefix ignored on both sides (weak comparison). Upstream-set `ETag` headers are preserved (first-writer-wins) so a reverse proxy / CDN that already computed a stronger hash wins. Hash is base64url + 22-char truncation → 132 bits of entropy, plenty of collision resistance per endpoint. Opt-in via `app.UseWtmETag()` after `UseRouting` and before compression (so the hash runs on the uncompressed representation). Zero breaking change.
- **Reliability: `[WtmIdempotent]` + `UseWtmIdempotency()` idempotency-key middleware** — Retry-safe mutating endpoints without the action author having to think about duplicate-submit. Decorate an action (or controller) with `[WtmIdempotent(WindowSeconds = 300, RequireKey = false)]`; when the client sends the same `Idempotency-Key` header within the window the middleware replays the cached 2xx response verbatim (status + content-type + body) and adds `Idempotency-Replay: true` so tests / clients can distinguish a cache hit from a fresh invocation. Follows the Stripe / PayPal / AWS convention (draft-ietf-httpapi-idempotency-key-header) — most modern API SDKs already send the header, so wiring this middleware retroactively makes existing endpoints mobile-retry-safe. `WtmIdempotencyOptions`: `HeaderName` (default `Idempotency-Key`), `DefaultWindowSeconds` (300, used when `WindowSeconds <= 0` on the attribute), `MaxKeyLength` (128 — UUID/ULID fit; prevents key-based cache bloat), `EligibleMethods` (`POST`/`PUT`/`PATCH`/`DELETE` — `GET`/`HEAD` intentionally excluded as already RFC-9110 safe+idempotent, caching would mask bugs), `MaxCachedBodyBytes` (1 MiB — larger 2xx pass through but are not cached). Cache key is scoped by `method + path + idempotency key` so a stolen key cannot replay a response captured on a different endpoint. Key characters are allow-listed (letters/digits/`-_.:`) to reject control chars / UTF-8 / log-injection. Only 2xx responses are cached — 4xx / 5xx pass through so a transient failure cannot poison the slot. Response headers other than `Content-Type` (e.g. `Set-Cookie`) are NOT replayed, preventing a previous caller's session from leaking into a retry. Missing key on a non-`RequireKey` endpoint is a no-op (best-effort); `RequireKey = true` + missing returns `400 application/problem+json` with the header name echoed in `detail`. Malformed / oversized keys also return 400. Backing store is `IMemoryCache` (in-process; multi-instance deployments need sticky LB or a distributed wrap). Opt-in via `app.UseWtmIdempotency()` after `UseRouting`. Zero breaking change.
- **Feature flags: `IWtmFeatureFlags` service + `[WtmFeatureGate]` MVC attribute** — First-class feature-flag primitive for WTM apps without pulling in `Microsoft.FeatureManagement` or an external SaaS adapter (LaunchDarkly, Unleash, etc.). Register with `services.AddWtmFeatureFlags()` (optionally `AddWtmFeatureFlags(opt => { opt.Defaults["new-checkout"] = true; opt.Resolver = (ctx, name) => ...; })`). Resolution order is deterministic and testable: (1) `WtmFeatureFlagsOptions.Resolver` delegate — per-request override point for tenant / canary / user-id rollouts or external-SDK adapters; returning `null` means "no opinion, fall through"; (2) `IConfiguration` binding at `FeatureFlags:<name>` — configuration-bound values are re-read on every call so `appsettings.json` watcher / `IConfigurationRoot.Reload()` changes propagate without restart; (3) `Options.Defaults` static dictionary (case-insensitive by default); (4) `false` — fail-closed for undeclared names. Resolver exceptions are caught and logged at `Warning` so a flag-service outage cannot take down production. `Snapshot()` enumerates every known flag with its currently-resolved value for admin diagnostic pages. `IHttpContextAccessor` is optional so background services / hosted jobs can consume flags without a request scope.\
`[WtmFeatureGate("new-checkout")]` decorates an action or controller; when the flag is disabled the action short-circuits with `404 Not Found` (default — gives attackers / curious users zero signal that a gated endpoint exists) and `FallbackStatusCode` lets apps switch to 403 / 503 when the intent is "exists but disabled right now". Fails closed when `AddWtmFeatureFlags` hasn't been called (missing service → treat as disabled), so forgetting the registration never accidentally exposes a pre-release endpoint. Zero breaking change — purely additive.
- **Observability: `UseWtmServerTiming()` W3C Server-Timing middleware** — Attaches a `Server-Timing: app;dur=<ms>` response header on every non-excluded request so browser DevTools (Chrome → Network → Timing → "Server Timing") surfaces per-request backend latency without wiring an APM. Complementary to `UseWtmSlowRequestLogging()` (#840): the former logs to the backend aggregator, the latter surfaces the same number in-band for front-end devs. `WtmServerTimingOptions`: `MetricName` (default `"app"`), `Description` (optional, rendered as quoted `desc="..."` with backslash + double-quote escaped), `MinDurationMs` (default 0 — emit always; raise to 200 to tag only slow outliers), `PathExclusions` (defaults match slow-request logging: `/healthz`, `/_framework`, `/_js`, `/_content`, `/favicon.ico`). Excluded paths pay zero stopwatch cost (short-circuits before `Stopwatch.StartNew`). Header attached via `Response.OnStarting` so it lands even when downstream middleware short-circuits. First-writer-wins at the header level — upstream CDN / reverse-proxy `Server-Timing` values are preserved; the middleware `.Append(...)`s its own metric so multiple layers coexist per the W3C comma-list semantics. Metric-name token is sanitised against RFC 7230 (rejects spaces, separators, control chars) — a defective caller value yields empty header rather than malformed output. Decimal formatting is culture-invariant (avoids `12,34` in de-DE locales). Opt-in via explicit `app.UseWtmServerTiming()` placed early after `UseRouting`. Zero breaking change.
- **API lifecycle: `[WtmDeprecated]` endpoint deprecation attribute** — Decorate a controller class or action method with `[WtmDeprecated(Message = "...", Sunset = "2026-12-31", Link = "</api/v2/orders>; rel=\"successor-version\"")]` and every invocation emits the IETF-standard `Deprecation: true` header (per draft-ietf-httpapi-deprecation-header), an RFC 8594 `Sunset: <HTTP-date>` header when a parsable date is supplied, and an RFC 8288 `Link` header pointing at the successor endpoint. The attribute implements `IAsyncResultFilter` so MVC auto-discovers it — no `Program.cs` registration required. Headers are attached via `Response.OnStarting` and respect first-writer-wins (a reverse-proxy or upstream-middleware-set `Deprecation` is preserved). An `Information`-level Serilog entry (`DeprecatedEndpointHit Path=… Method=… Message=… Since=…`) is emitted per hit so ops can query "who's still calling v1?" from the existing log pipeline; opt out with `LogHit = false`. Malformed `Sunset` values are ignored (header simply not emitted, warning logged once per process via lazy-init) instead of crashing the action. Zero breaking change — affects only endpoints that opt in. Fixture controllers in the test suite cover class-level inheritance, per-action override, `Link` emission, and Sunset-date formatting.
- **Ops: `UseWtmMaintenanceMode()` opt-in maintenance-mode middleware** — Planned-outage / deploy-window kill-switch for non-allow-listed traffic. When `WtmMaintenanceModeOptions.Enabled = true` (or the dynamic `IsEnabled` delegate returns true), every request that doesn't hit an `AllowedPathPrefixes` entry or an `AllowedClientIps` entry short-circuits with `503 Service Unavailable` + `Retry-After: 60` + an `application/problem+json` body (`type`, `title`, `status`, `detail`, `retryAfterSeconds`, `traceId`). Default allow-list keeps `/healthz`, `/_framework`, `/_js`, `/_content`, `/_admin` reachable so (a) k8s readiness probes stay green through the outage and (b) operators can still drive the admin plane to toggle maintenance mode off again. `AllowedClientIps` gives ops a bastion / jump-host bypass. `IsEnabled` is a `Func<HttpContext, bool>` — wire it to `IOptionsMonitor<WtmMaintenanceModeOptions>`, a Redis feature flag, or a LaunchDarkly-style service to flip the switch without restarting the app; a throwing delegate is caught and falls back to the static `Enabled` flag so a flag-service outage does not crash production. `ContentType` defaults to `application/problem+json` (aligns with `UseWtmProblemDetails`) but `text/html` is supported via a minimal inline HTML fallback or a custom `HtmlBodyFactory`. When `Enabled` is `false` and no provider is set, the middleware is a single bool check — effectively free. Opt-in via explicit `app.UseWtmMaintenanceMode()` placed after `UseRouting` but before auth / MVC so the 503 short-circuits before expensive middleware runs. Zero breaking change.

## [10.4.0] - 2026-04-18

### Added
- **Observability: `UseWtmSlowRequestLogging()` slow-request middleware** — Wraps the request pipeline with `Stopwatch` and emits a structured `Warning` (or configurable level) log entry whenever elapsed exceeds `ThresholdMs` (default 1000 ms). Structured template `SlowRequest Path={Path} Method={Method} Status={StatusCode} ElapsedMs={Elapsed} User={User} ClientIp={ClientIp}` lets Loki / Seq / Elastic pivot on path / user / IP for a zero-APM "top slow endpoints" dashboard. PII-safe defaults: `IncludeQueryString = false` (tokens / IDs / emails commonly leak via query strings into indexed logs), `IncludeClientIp = true` (already sanitised). Default `PathExclusions` cover `/healthz`, `/_framework`, `/_js`, `/_content`, `/favicon.ico` so a naive 1 s threshold doesn't spam on static assets + health probes. Excluded paths pay zero measurement cost (short-circuits before starting the stopwatch). `LogSanitizer` neutralises CR/LF / control-char log-injection. Opt-in via explicit `app.UseWtmSlowRequestLogging()` — zero breaking change. Manual §10.12. (#840)
- **Security: `UseWtmSecureHeaders()` opt-in hardening headers middleware** — Writes the OWASP Secure Headers Project bundle on every response, sibling to the existing `UseWtmContentSecurityPolicy()` (#789 Phase 3D). Defaults: `X-Content-Type-Options: nosniff`, `X-Frame-Options: SAMEORIGIN`, `Referrer-Policy: strict-origin-when-cross-origin`, `Permissions-Policy` denying accelerometer/camera/geolocation/gyroscope/magnetometer/microphone/payment/usb. Any header can be disabled with `opt.X = null` or overridden with a custom value. `Strict-Transport-Security` is **opt-in** (`HstsEnabled = false` default) and only emitted when the request is HTTPS — avoiding the classic HSTS-over-HTTP-during-TLS-rollout foot-gun. HSTS configurables: `HstsMaxAgeSeconds` (default 31536000), `HstsIncludeSubDomains` (default true), `HstsIncludePreload` (default false, opt-in after submitting to hstspreload.org). First-writer-wins at every header so upstream reverse-proxy values (ALB, Cloudflare, nginx) are preserved. Zero breaking change — opt-in via explicit `app.UseWtmSecureHeaders()` call. Manual §10.11. (#838)
- **Observability: framework-owned health checks + JSON response writer** — `AddWtmHealthChecks` previously shipped only a "self" liveness probe; every WTM-based app then reinvented the same `IDataContext.CanConnectAsync` check and the same SRE JSON formatter in its own `Program.cs`. Now:
  - `builder.AddWtmDataContextCheck(name = "datacontext", timeout = 2s, tags = ["ready"])` extension method registers the canonical DB probe. Returns `Unhealthy` with the exception message if the underlying provider rejects the connection; returns `Unhealthy` with a timeout diagnostic if the probe exceeds the configured deadline. Attaches `dbType` / `durationMs` to the entry's `data` for observability dashboards.
  - `WtmHealthCheckResponseWriter.WriteJsonResponse` emits `application/json` with `status`, `totalDurationMs`, and a `checks[]` array containing `name` / `status` / `durationMs` / `description` / `exception` / `data` / `tags` per entry — Kubernetes, Prometheus, and Datadog can scrape structured fields instead of parsing plain text.
  - `UseWtmHealthChecks(useJsonResponse: true)` opts into the JSON writer. Default is `false` so existing apps continue to receive the ASP.NET Core plain-text body (`"Healthy"` / `"Unhealthy"`) without any behaviour change.
  Zero breaking change — both the DB check registration and the JSON writer are opt-in. Manual §14A. (#836)
- **ETL: dry-run preview mode** — New opt-in `EtlPipelineConfig.IsDryRun` flag + `POST /_EtlJob/DryRun?id=&sampleSize=` admin endpoint let operators preview an ETL job without touching the target DB. When `IsDryRun = true`, the executor runs Extract (validates SQL syntax / permissions / column presence) + Transform on the first batch only, then returns extracted row count, first N transformed rows as preview (default 10, up to 100), validation warnings (missing `MergeKeyColumn` in source columns, empty source, etc.), and the watermark value that *would* be committed — but skips `EnsureStagingTable`, `TruncateStaging`, `BulkLoad`, `Merge`, and watermark commit. Bypasses Quartz, never writes an `EtlRunLog` row (no audit-trail pollution). Catches the most common ETL misconfigurations (bad query, wrong merge key, wrong column names) before they damage production data. Zero breaking change — `IsDryRun` defaults false. Manual §8.7.1. (#834)
- **Audit: `AddWtmActionLogRetention(opt)` opt-in background retention service** — Bounds unbounded growth of the `ActionLogs` table without requiring DBA-level partitioning. Per-`LogType` TTL (default: Normal 90d, Exception 365d, Debug 30d, Job 90d); zero or negative days disables retention for that type. Daily sweep runs once per calendar day at configurable local hour (default 03:00). Deletion uses EF Core 7+ `ExecuteDeleteAsync` batched at `BatchSize` (default 5000) with `OrderBy(ActionTime).Take(BatchSize)` looping until drained — avoids the transaction-log bloat / lock contention a single massive `DELETE` would cause. Fault-tolerant: per-iteration exceptions logged as Warning, service never crashes the host. Structured Serilog output per sweep: `ActionLogRetention: sweep complete. Normal={N} Exception={E} Debug={D} Job={J} total={T}`. Opt-out: `Enabled = false` for apps managing retention via external archival pipeline. Manual §10.6.1. (#832)
- **Observability: `UseWtmCorrelationId()` opt-in correlation-ID middleware** — Inbound reads an `X-Correlation-Id`-style header (configurable), sanitizes and adopts it as `HttpContext.TraceIdentifier` so Serilog scope, `Activity.Current`, and `WtmProblemDetails.traceId` all emit the caller-propagated ID automatically. Fallback: auto-generate `Guid.NewGuid("N")` UUID when inbound missing / invalid. Outbound: echoes chosen ID in the response header. Sanitization rejects CR/LF / control chars / non-ASCII (log injection defense); rejects non-`[A-Za-z0-9\-_.]` chars; length cap default 128. Opt-outs: `AdoptInbound=false` for zero-trust; `EmitOutbound=false` to suppress echo. Manual §10.10. (#830)
- **Security: `[WtmRateLimit]` per-endpoint rate limit attribute** — Brute-force protection for login / password-reset / forgot-password style endpoints without lowering the global limiter's ceiling. Decorate a controller action or class with `[WtmRateLimit(permits, windowSeconds, queueLimit=0)]` to enforce a **per-IP-per-endpoint** quota on top of `AddWtmRateLimiting`'s global limiter. At startup, assembly scan collects unique `(permits, window, queue)` tuples and registers one named policy per tuple (dedup'd — no policy explosion). An `IActionModelConvention` injects ASP.NET Core's built-in `EnableRateLimitingAttribute` into each decorated action's metadata at model-build time. Exceeded quota returns HTTP 429 with an existing `Rate limit exceeded` Serilog warning. Zero breaking change — existing `AddWtmRateLimiting(opt => { ... })` calls work unchanged. Manual §10.9. (#828)
- **Cache: Lookup Cache stats / health API** — New `ILookupCacheService.GetStats()` / `.GetStats(Type)` methods expose hits, misses, invalidate count, last-access / last-warm / last-invalidate timestamps, and currently-cached tenant key count per type. Closes the "is my cache actually warm?" / "did the invalidate take effect?" observability gap. Process-lifetime counters (reset on restart) via `Interlocked`; tenant-key set guarded by `Lock`. Framework stays SDK-agnostic — apps wire `LookupCacheStats` into their telemetry (Prometheus / OpenTelemetry / custom). Manual §12.8. (#826)
- **Dashboard: `RestWidgetDataSource` (no C# required for external HTTP endpoints)** — New built-in `WidgetDataSourceKind.Rest` lets admins configure REST-backed widgets via Dashboard JSON config: `{"kind":"Rest","options":{"url":...,"jsonPath":"$.data",...}}`. Supports GET/POST with custom headers, simple dot-path JSON extraction, response caching via `IMemoryCache`, response size cap (default 1 MiB), request timeout (default 10 s). SSRF guard rejects private/loopback/link-local/multicast/IPv6-ULA IPs by default (incl. AWS IMDS `169.254.169.254`); opt-in via `allowPrivateNetwork: true`. HTTPS-only by default; opt-in `allowHttp: true` for internal plain-HTTP endpoints. Zero breaking change — existing `Custom` and `Analysis` data sources untouched. Manual §9.4.1. (#824)

### Changed
- **Tests: migrate `WalkingTec.Mvvm.Mvc.Tests` from deprecated xunit 2.9.3 to MSTest** — The only test project still on xunit is now unified with the rest of the WTM test suite (Core/Admin/Api/Etl/Integration all MSTest). Removes the last `dotnet list package --deprecated` hit. 38 tests unchanged; `FluentAssertions` style preserved (framework-agnostic). `[Fact]` → `[TestMethod]`, `[Theory]` + `[InlineData]` → `[TestMethod]` + `[DataRow]`, constructor → `[TestInitialize]`, `IDisposable.Dispose` → `[TestCleanup]`. (#822)
- **Demo: migrate `FFResult()` → `FFResultJson()` across all demo Controllers** — 123 call sites across 21 files in `demo/WalkingTec.Mvvm.Demo/Controllers/` + `demo/.../Areas/_Admin/Controllers/` updated to the CSP-safe JSON dispatcher path introduced in 10.3.0 (#789 Phase 3C). Demo is now the canonical reference implementation for downstream WTM apps — copy-paste starter code uses the non-deprecated API. Zero `WTM789` obsolete warnings from the demo project (was 123). Build warning count drops 340 → 218. No behavior change: `Alert/Message/CloseDialog/RefreshGrid/RefreshGridRow` have identical wire semantics between `FResult` and `WtmActionResult`. (#818)

### Fixed
- **Security: eliminate NU1903 from `System.Security.Cryptography.Xml 8.0.2` transitive** — NPOI 2.7.6 was pulling a vulnerable 8.0.2 version (GHSA-37gx-xxp4-5rgx + GHSA-w3x6-4m5h-cxqf, both HIGH severity) into every test/demo project in the solution. Add a direct `PackageReference` to `WalkingTec.Mvvm.Core.csproj` pinning to 10.0.6 via new `<SystemSecurityCryptographyXmlVersion>` variable in `common.props` (aligns with the rest of the central-version table). All downstream projects now inherit the patched version; `dotnet list package --vulnerable --include-transitive` returns zero NU1903 rows. NPOI unchanged. (#816)

## [10.3.0] - 2026-04-17

Security hardening release. Issue #789 six-phase `framework_layui.js`
refactor plus post-merge follow-ups (#804 / #805 / #806 / #811 / #813).
Active-code `eval(` count in `framework_layui.js`: **18 → 1** (single centralized
legacy fallback behind console deprecation warning). DOM-XSS sinks: **7 → 0**.
All changes are additive / opt-in — no breaking defaults.

### Added
- **Security: `UseWtmContentSecurityPolicy()` opt-in CSP middleware** — Apps can now register a Content-Security-Policy response header via `app.UseWtmContentSecurityPolicy()`. Default policy enforces `script-src 'self' 'unsafe-inline'` — `'unsafe-eval'` is intentionally omitted, which closes the issue #789 eval()-execution attack surface built up through Phases 1/2/3A/3B/3C. Keeps `'unsafe-inline'` so existing Razor TagHelper inline scripts still work without a nonce refactor. Supports `ReportOnly` mode and a custom `report-uri` for staged rollout. First-writer-wins: respects any CSP header already set by upstream middleware or reverse proxy. **Breaking for apps that still call `eval()` in their own JS** — opt-in means the default is unchanged; to enable, add `app.UseWtmContentSecurityPolicy()` after eliminating app-level `eval()`. (#789 Phase 3D)

### Fixed
- **Security: CSP-safe JSON action dispatcher replaces `IsScript` eval()** — `framework_layui.js` now routes AJAX responses through `ff.DispatchAction` when the server returns `X-WTM-Action: application/json` (via new `FFResultJson()` + `WtmActionResult`). The legacy `IsScript: true` path still works but emits a deprecation warning through the single centralized `ff._legacyScriptEval` helper. Active-code `eval(` count in `framework_layui.js` dropped from 18 (pre-#789) to 1. `FFResult()` is marked `[Obsolete(DiagnosticId = "WTM789")]` — suppressible per-project via `<NoWarn>WTM789</NoWarn>`. Framework-owned callers migrated (`_FrameworkController`, `_CodeGenController`, `_EtlJobController`, `Controller.txt` codegen template). Demo/app callers stay on the legacy path for one release. (#789 Phase 3C, #802)
- **Security: sanitize AJAX response HTML via DOMPurify** — The four `.html(data)` / `innerHTML = str` sinks in `framework_layui.js` (`LoadPage1`, `PostForm`, `BgRequest`, `OpenDialog`) now pass raw AJAX bodies through `ff.SafeHtml` which wraps DOMPurify. Vendored `dompurify.js` as an EmbeddedResource served alongside `framework_layui.js`; load order documented in `_Layout.cshtml`. Fail-closed: when DOMPurify did not load, SafeHtml returns empty string rather than rendering attacker HTML. (#789 Phase 3B, #801)
- **Security: DOM XSS via cookie-to-id and iframe URL concatenation** — Cookie-sourced ids (`$.cookie("divid")`) are now passed through jQuery `.attr()` / DOM `setAttribute` rather than concatenated into HTML strings, and iframe URLs are set via the `.src` setter. (#789 Phase 3A, #800)
- **Security: eliminate remaining non-FFResult eval() sites** — combo/tc/selector global-variable access and three TagHelper-templated call sites now use the safe `window[name]` property-lookup pattern. (#789 Phase 2, #799)
- **Security: `WtmActionResult.Redirect` rejects non-relative URLs** — Open-redirect (CWE-601) hardening. `WtmActionResultExtension.Redirect(url)` now throws `ArgumentException` on `null` / empty / absolute (`http://`, `https://`) / protocol-relative (`//evil.com`) / non-http schemes (`javascript:`, `data:`). Only relative shapes (`/...`, `~/...`, `#...`, `?...`) are accepted. Client-side `ff.DispatchAction` adds a defense-in-depth shape check via `charAt(0)` before assigning `location.href`. (#804, PR #808)
- **Security: escape `WtmAction.Message` / `Title` before `layui.layer.alert`** — Research confirmed layui 2.5.7's `layer.alert(msg)` concatenates `msg` into `innerHTML`, so the new Phase 3C dispatcher must escape before handing off. `ff.DispatchAction` now routes `action.message` + `action.title` through a new `ff.EscapeText` helper (jQuery `.text()/.html()` idiom) on the `alert` and `message` branches. `ff.Alert` / `ff.Msg` themselves unchanged — legacy callers that pass intentional HTML still work. (#805, PR #809)
- **Security: `CookieOption.SecurePolicy` is now configurable** — `AddWtmSession` and `AddWtmAuthentication` previously hardcoded `CookieSecurePolicy.SameAsRequest`. Behind a TLS-terminating reverse proxy (nginx, Azure App Service, IIS ARR, Cloudflare) the app sees HTTP from the proxy, so cookies were emitted without the `Secure` flag. New `CookieOption.SecurePolicy` property lets operators set `Always` for production via `appsettings.json` (`CookieOptions:SecurePolicy`). Default preserved as `SameAsRequest` — zero breaking change; explicit opt-in for the hardened behavior. (#813, PR #814)
- **Security: chart-related eval() removal** — 11 chart dispatch sites in `framework_layui.js` no longer call `eval(identifier + suffix)`. (#789 Phase 1, #798)

### Improved
- **DX: Integration.Test SQL reachability probe + docker-compose** — `IntegrationTestBase.EnsureSqlServerAvailable()` now runs a short 2 s TCP probe once per assembly (cached via `Lazy<T>`); when SQL Server is not reachable, each integration test reports `AssertInconclusive` (yellow) with a multi-line setup guide instead of `Failed` (red). Added `docker-compose.yml` at repo root (`docker compose up -d mssql`) and `test/WalkingTec.Mvvm.Integration.Test/README.md` with three run-options runbook. From-fresh-clone `dotnet test` now shows 9 yellow skipped (was 9 red failed / 1m13s). CI unchanged — probe succeeds against the service container. (#811, PR #812)
- **Quality: `#789` tidy-up bundle** — `[Obsolete(DiagnosticId = "WTM789")]` now also on `FResult` class declaration + `FResultExtension` class (previously only on the `FFResult()` factory method). `WtmAction.Type` converted from raw `string` to typed `WtmActionType` enum with `JsonStringEnumConverter(CamelCase)` — wire format unchanged, type system closed. `WtmActionResultExtension.RefreshGrid` emits `Index` only when non-zero (honors `WhenWritingNull`). `WtmCspOptions.ReportUri` XML doc no longer conflates `report-uri` with `report-to`. (#806, PR #810)

## [10.2.0] - 2026-04-11

### Changed
- **BREAKING: Remove deprecated Elsa Workflow integration** — Delete all `[Obsolete]` workflow stubs that were non-functional since v8.3.0. Removed: `IWorkflow`, `FrameworkWorkflow`, `FlowInfoTagHelper`, `AddWtmWorkflow()`, `BaseCRUDVM.StartWorkflowAsync/ContinueWorkflowAsync/GetWorkflowTimeLineAsync/GetWorkflowInstanceAsync`, `BasePagedListVM.GetMyApproves()`, `DataContext.FrameworkWorkflows` DbSet, `TypeExtension.GetParentWorkflowPoco()`, and related WorkFlow DTOs. **Migration**: remove all references to these types and methods — they were already returning null/empty (#762)

### Fixed
- **Build: exclude Blazor WASM Client from Release build** — Prevent NETSDK1082 error when `wasm-tools` workload is not installed. Blazor demo packages upgraded 10.0.3 → 10.0.5 (#764)
- **Security (CRITICAL): validate JWT signature for `_remotetoken`** — Standalone deployments accepted unsigned JWT tokens, allowing identity impersonation. Now validates signature, issuer, and audience (#765)
- **Security (HIGH): block mass assignment in `UpdateModelProperty`** — Inline cell editing accepted arbitrary field names. Now rejects sensitive fields and navigation paths (#766)
- **Security (HIGH): validate VM type in `CreateVM(string)`** — User-supplied type names were passed to `Type.GetType()` without restriction. Now requires `BaseVM` subclass (#767)
- **Security (HIGH): stop leaking exception messages in error page** — Production error handler returned raw `ex.Message` to clients. Now returns generic localized message (#769)
- **Security (MEDIUM): prevent path traversal in file upload** — `subdir` parameter was concatenated without validation. Now enforces upload root boundary (#770)
- **TokenService: convert recursive revocation to iterative** — Prevent StackOverflow on circular refresh-token chains with max depth 50 (#771)
- **GetFile: guard image processing against non-image files** — Only attempt `Image.Load` for known image extensions, safely reset stream on failure (#775)
- **Export: cross-platform paths and temp directory cleanup** — Replace Windows-only path separators with `Path.Combine()`, delete temp directories in `finally` block (#776)

### Improved
- **WtmTenantService: replace blocking `Wait()` with `WaitAsync()`** — Prevent thread pool starvation under concurrent load (#772)
- **ETL: set CommandTimeout to 300s** — Replace infinite timeout (0) with 5-minute safety net in MssqlSource and OracleSource (#773)
- **Regex: static compilation in `_FrameworkController.Selector`** — Avoid per-request Regex allocation (#774)
- **Exception logging: replace 13 bare `catch {}` blocks** — Add `ILogger.LogWarning` in WTMContext (6), BaseCRUDVM (4), FrameworkFilter (3) to surface silent failures (#777)

## [10.1.1] - 2026-04-04

### Fixed
- **Security hardening** — Resolve XSS (reflected user input in error pages), path traversal (file provider), and information disclosure (API client error responses) (#748)
- **CI** — Update xunit.runner.visualstudio to 3.0.0 for .NET 10 compatibility

### Improved
- **C# 12 collection expressions** — Extended `List<T>` → `[]` modernization from Core (10.0.1) to Mvc (11 files), TagHelpers.LayUI (11 files), and Etl (2 files), covering controllers, filters, helpers, data grids, tree views, form tag helpers, and pipeline loaders (52 files total)
- **Code comments** — Added English explanatory comments to non-obvious security logic (TokenService token-reuse detection, PasswordHashHelper legacy hash chain) and complex algorithms (DataContext multi-tenant filter, AnalysisQueryEngine expression trees, ServerSideGroupByStrategy measure projection) (#754, #755)

## [10.1.0] - 2026-03-28

### Added
- **Clean Architecture: 8 extracted services** — Decompose WTMContext God Object (1670 lines) into 8 focused, single-responsibility services using the Strangler Fig pattern (#727):
  - `IWtmApiClient` / `WtmApiClient` — HTTP API calls (CallAPI)
  - `IWtmLogService` / `WtmLogService` — structured action logging (DoLog)
  - `IWtmAuthorizationService` / `WtmAuthorizationService` — URL access control (IsAccessable, IsUrlPublic)
  - `IWtmDataContextFactory` / `WtmDataContextFactory` — DataContext creation with tenant/CS resolution
  - `IWtmAuthService` / `WtmAuthService` — password verification, remote auth, token refresh
  - `IWtmUserCacheService` / `WtmUserCacheService` — user info cache invalidation
  - `IWtmTenantService` / `WtmTenantService` — tenant groups/roles with caching
  - `IWtmVmFactory` / `WtmVmFactory` — ViewModel creation and initialization
- **WTMContext facade delegation** — WTMContext methods now delegate to extracted services when available via DI, with inline fallback for environments without DI (#730)
- **WtmUIOptions** — Centralized, configurable UI styling system with overridable CSS class names, sizing defaults, and required field markers. Defaults match LayUI for zero-config backwards compatibility (#730)
- All 8 services registered in DI for both web (`AddWtmContext`) and console (`AddWtmContextForConsole`) apps

### Improved
- **WtmAuthorizationService** — Compiled regex cache (`ConcurrentDictionary` + `RegexOptions.Compiled`) eliminates per-request allocation in singleton service
- **WtmTenantService** — Cache stampede protection via per-key `SemaphoreSlim` with double-check locking
- **Structured logging** — Replace empty `catch {}` blocks with `ILogger.LogWarning` in WtmAuthorizationService and WtmTenantService
- **Nullable safety** — Fix pre-existing nullable warnings in WTMContext.cs (WindowIds, ReloadUser, BaseUserQuery, DoLoginAsync)

### Fixed
- **WtmAuthService.RefreshTokenAsync** — Pass user's RemoteToken as authToken when calling remote refresh endpoint (QA review finding)
- **9 pre-existing test failures** — Fix FrameworkTenant_CreateDC (missing constructor), DoLoginAsync tests (missing IWtmTenantService mock), SearchTest (missing mock in MockWtmContext) (#730)

### Notes
- WTMContext's original method bodies are preserved as fallback — zero breaking changes for existing consumers
- New code can optionally inject services directly via DI instead of going through WTMContext
- Minor version bump (10.0.1 → 10.1.0) per compatibility policy: new public API surface (8 service interfaces)

## [10.0.1] - 2026-03-21

### Changed
- **TimeProvider 全面遷移** — 將 `DateTime.Now`/`UtcNow` 替換為 `TimeProvider`，覆蓋 Core VMs、Mvc Controllers/Filters、TokenService、DataContext、WTMLogger、FileHandlers、Dashboard、ETL 等 27 個檔案。測試可透過 `FakeTimeProvider` 控制時間（#697, #706, #708, #711, #712）
  - `WTMContext.TimeProvider`（Phase 1）
  - Core VMs: BaseCRUDVM, BaseBatchVM, BaseImportVM, BasePagedListVM, BaseTemplateVM（Phase 2）
  - Mvc: _FrameworkController, FrameworkFilter, _AnalysisController, FileExtension（Phase 2）
  - Auth: TokenService — 建構子注入 `TimeProvider`（Phase 2）
  - Core: DataContext（新增 `EmptyContext.TimeProvider` 屬性）、WTMLogger、FileHandlers（Phase 3）
  - Dashboard: JsonFileDashboardService — 建構子注入 `TimeProvider`（Phase 3）
  - ETL: EtlQuartzJob, EtlSchedulerService（Phase 3）
  - DateRange: 6 個 `CreateUtc*()` factory methods 支援 `TimeProvider?` 可選參數，原 static properties 保持向後相容（Phase 3）
- **Collection expressions 現代化** — Analysis DTOs 和 BasePagedListVM 使用 C# 12 collection expressions（#707）
- **.NET 10 效能優化 Phase 1a** — FrozenDictionary、Lock、Span/stackalloc、string interpolation（#702）

### Fixed
- **Analysis Saved Query 存取控制** — 強化 VM 存取檢查，防止未授權存取（#710）
- **BaseImportVM nullable 修正** — 移除 Core 專案最後一個 `#nullable disable`（#703）
- **Build 健康度** — 解決 NU1603、NU1510 警告，移除未使用的 import（#693）

### Performance
- **ExecuteDeleteAsync** — Analysis Saved Query 刪除改用 `ExecuteDeleteAsync` 提升效能（#704）

### Notes
- `DateTimeOffset.UtcDateTime`（而非 `.DateTime`）用於所有 UTC 語境，避免 `DateTimeKind.Unspecified` 導致時區誤判
- Property initializers（RefreshTokenEntity, ChangeLog 等）、static display timestamps、codegen templates 刻意保留 `DateTime`

## [10.0.0] - 2026-03-20

### Changed
- **BREAKING: .NET 10 升級** — 所有專案目標框架從 `net8.0` 升級至 `net10.0`（.NET 10 LTS）
- **BREAKING: 版本號跟隨 .NET 版本** — `VersionPrefix` 從 `8.6.1` 升至 `10.0.0`
- **BREAKING: MySQL Provider 替換** — 從 `Pomelo.EntityFrameworkCore.MySql` 改為官方 `MySql.EntityFrameworkCore` 10.0.1
  - `UseMySql()` → `UseMySQL()`（大寫 SQL）
  - 不再需要 `ServerVersion` 參數
  - `MySqlSchemaBehavior` 不再可用
  - `MySqlConnector` namespace → `MySql.Data.MySqlClient`
- **BREAKING: API Versioning 套件替換** — `Microsoft.AspNetCore.Mvc.Versioning` → `Asp.Versioning.Mvc` 8.1.1
  - `AddVersionedApiExplorer()` → `AddApiVersioning().AddApiExplorer()`
  - 新增 `using Asp.Versioning;` namespace
- **EF Core 10** — 全部 EF Core 套件升至 10.0.4
- **ASP.NET Core 10** — 全部 ASP.NET Core 套件升至 10.0.4
- **Swashbuckle 10** — 從 6.6.2 升至 10.1.5（依賴 OpenAPI.NET v2）
- **Serilog 10** — `Serilog.AspNetCore` 從 8.0.3 升至 10.0.0
- **Npgsql 10** — `Npgsql.EntityFrameworkCore.PostgreSQL` 從 8.0.11 升至 10.0.1
- **Oracle EF Core 10** — `Oracle.EntityFrameworkCore` 從 8.23.70 升至 10.23.60
- **SDK** — `global.json` 升至 .NET SDK 10.0.0
- **Dockerfile** — 基底映像從 .NET 3.1 更新至 .NET 10
- **CI** — GitHub Actions 的 `dotnet-version` 從 `8.0.x` 更新至 `10.0.x`
- **套件版本集中管理** — `common.props` 新增 12 個 MSBuild 版本變數，統一管理 Microsoft、EF Core、ASP.NET Core 及主要第三方套件版本
- **System.Text.Json** 10.0.0 → 10.0.4
- **Quartz** 3.16.0 → 3.16.1
- 修正遺留 demo 測試專案：`net5.0`/`net6.0` → `net10.0`

### Migration Guide
1. 將您的專案 `TargetFramework` 從 `net8.0` 改為 `net10.0`
2. 安裝 .NET 10 SDK
3. 若使用 MySQL：將 `Pomelo.EntityFrameworkCore.MySql` 替換為 `MySql.EntityFrameworkCore`
   - `using MySqlConnector;` → `using MySql.Data.MySqlClient;`
   - `optionsBuilder.UseMySql(cs, serverVersion, ...)` → `optionsBuilder.UseMySQL(cs)`
4. 若使用 API Versioning：更新套件引用及 namespace
   - `Microsoft.AspNetCore.Mvc.Versioning` → `Asp.Versioning.Mvc`
5. 更新所有 Microsoft.EntityFrameworkCore.* 套件至 10.0.x
6. 更新所有 Microsoft.AspNetCore.* 套件至 10.0.x

### Added
- **BaseCRUDVM — 批量刪除預覽**：新增 `GetDeletePreviewString()` 虛擬方法，回傳實體的人類可讀標籤（依序搜尋 Name/Title/Code/ITCode 等屬性，否則回退至主鍵）；同時新增 `IBaseCRUDVM<T>.GetDeletePreviewString()` 介面成員（#619）。
- **`_FrameworkController` — 批量刪除預覽端點**：POST `/_Framework/GetDeletePreview` — 接受 vmType 名稱與最多 10 個 ID，回傳 `[{id, label}]` 陣列供前端確認對話框使用（#619）。
- **`_FrameworkController` — 批量指派角色端點**：POST `/_Framework/BatchAssignRoles` — 將一個角色指派給多位使用者（upsert `FrameworkUserRole`），完成後清除受影響的快取（#619）。
- **ComboBox 遠端搜尋**：`wt:combobox` 新增 `remote-url` 屬性；設定後啟用 xmSelect `remoteSearch + remoteMethod`，每次輸入觸發 `GET {remote-url}?q=<keyword>` 並即時更新選項（#565）。
- **TreeSelect 懶加載**：`wt:tree` 新增 `lazy-url` 屬性；設定後啟用 xmSelect `lazy + load`，展開節點時觸發 `GET {lazy-url}?id=<nodeValue>` 並動態載入子節點（#565）。
- **ETL 管理 UI 改進**：`EtlJobListVM` 覆寫 `InitGridAction()` 增加 Create/Edit/Delete 標準動作，以及每行操作按鈕：立即執行（確認 POST）、暫停、恢復、中止、執行記錄（開啟篩選後的 RunLog 對話框）；新增 ConsecutiveFailureCount 欄位（#540）。
- **ETL Create/Edit 表單補全**：Demo 的 Create.cshtml 與 Edit.cshtml 補入 QueryTemplate（textarea）、AlertEmail、AlertWebhookUrl、AlertAfterConsecutiveFailures 欄位（#540）。
- **BaseCRUDVM — 樂觀並行衝突處理**：`DoEdit` / `DoEditAsync` 捕獲 `DbUpdateConcurrencyException`，設定 `IsConcurrencyConflict = true` 並新增模型錯誤，而非直接拋出例外；新增介面屬性 `IBaseCRUDVM<T>.IsConcurrencyConflict`（#620）。
- **BaseImportVM — 匯入進度回報**：`BatchSaveData(IProgress<ImportProgress>? progress = null)` 在驗證與儲存階段回報 `ImportProgress { Processed, Total, Phase }`（#607）。
- **BaseImportVM — 行內錯誤清單**：新增 `InlineErrors` 屬性（最多 `InlineErrorLimit` 筆，預設 50）供 API 端點直接回傳驗證錯誤（#615）。
- **BaseTemplateVM — 欄位說明列**：`ShowDescriptionRow = true`（預設）時，於模板第二行插入淺綠色斜體說明列，標示 Required/Optional、資料類型、min/max 限制；匯入時自動識別並跳過說明列（v2 標記）（#615）。

### Fixed
- **Test suite — EF Core 10 API**：`DataContext` 中 `modelBuilder.Model.SetMaxIdentifierLength(30)` 改為 `modelBuilder.HasMaxIdentifierLength(30)`，修正 EF Core 10 將內部 API 設為不可存取的問題（#676）。
- **Test suite — MSTest / Test.Sdk 版本**：`MSTest.TestAdapter` / `MSTest.TestFramework` 從 3.2.2 升至 3.6.4；`Microsoft.NET.Test.Sdk` 從 17.9.0 升至 17.12.0，確保與 .NET 10 測試主機相容（#676）。
- **Test suite — coverlet / xunit runner**：`coverlet.collector` 從 6.0.2 升至 6.0.4；`xunit.runner.visualstudio` 從 2.8.2 升至 2.8.3，修正與 Test.Sdk 17.12.0 的相容性（#676）。
- **Test suite — Serilog Sinks**：`Serilog.Sinks.InMemory` 從 0.11.0 升至 1.0.0，解決與 `Serilog.AspNetCore 10.0.0`（使用 Serilog 4.x）的型別衝突（#676）。

### Deprecated
- **Workflow API**：內建 Elsa workflow 整合（`IWorkflow`、`FrameworkWorkflow`、`ApproveTimeLine`、`ApproveInfo`、`FlowInfoTagHelper`、`IBaseCRUDVM` 工作流程方法、`DataContext.FrameworkWorkflows`）標記為 `[Obsolete]`，將於下一個主版本移除（#586）。
  - **遷移指引**：若仍需工作流程功能，請直接引用 Elsa 或改用其他工作流程引擎；移除 `IWorkflow` 介面實作及相關 TagHelper。

## [8.6.1] - 2026-03-17

### Added
- **ETL — `CompositeEtlSource`**：多來源 Union 模式，支援跨資料庫合併載入（#425）
- **Dashboard — `inferChartType`**：依維度/度量組合自動選擇最佳圖表類型（Bar/Line/Stacked/Card）（#431）
- **Dashboard — KPI 告警閾值著色**：超過風控閾值時 Card Widget 自動標色，支援即時風控場景（#386）
- **Dashboard — 響應式斷點**：Widget 格局依視窗寬度自動調整欄數（#344）
- **Analysis — Excel 匯出格式化**：Bold header、自動欄寬、`#,##0.00` 數字格式（#378）
- **Analysis — 合規匯出 Metadata**：`includeMetadata=true` 參數新增 Metadata 工作表，含查詢條件、欄位清單、QueryHash（#385）
- **Analysis — VM 層級 RBAC**：`[AllowedRoles]` attribute 限制整個 ListVM 的可見度（#339）
- **Analysis — 欄位層級 RBAC**：`[AllowedRoles]` 可套用於個別 Dimension/Measure 欄位（#341）
- **Analysis — 結構化 Logging**：`AnalysisController` 所有端點加入 structured log（#340）
- **Analysis — CancellationToken**：Query/Export 路徑支援請求取消，避免 DB 長跑查詢浪費資源（#326）
- **Analysis — Ad-hoc Filter UI**：BA 可在前端動態新增篩選條件列（欄位 + 運算子 + 值）（#313）
- **Analysis — 雙 Y 軸 + 金額縮放**：2 個度量最大值差距 ≥ 10 倍時自動啟用雙 Y 軸；依最大值自動選擇元/萬元/百萬元/億元縮放單位（#281）
- **ETL — MergeKeyColumn 唯一性驗證**：Job 建立時驗證 MergeKey 欄在目標表為唯一索引（#342）
- **ETL — 5 欄位 Unix Cron 自動轉換**：輸入標準 5 欄位 Cron 運算式自動轉 Quartz 6 欄位格式（#343）
- **Analysis — 快取 Hash 含租戶/使用者 ID**：防止跨租戶快取穿透（#314）

### Fixed
- **ETL**：`EtlJobDefinitionVM.Validate()` catch 範圍縮小，DB 連線錯誤現在正確回報至 UI（#353）
- **ETL**：`Rerun` / `TriggerNow` / `Pause` / `Resume` / `SkipNext` 端點加入 `InvalidOperationException` 處理，回傳 400 而非 500（#359 #367）
- **Analysis**：CSV 匯出加入 UTF-8 BOM，Windows Excel 開啟不再出現亂碼（#379）
- **Analysis**：空資料時 Query 回傳 `{ rows: [], truncated: false }` 並附帶友善提示訊息，不再回傳空 body（#383）
- **Analysis**：GroupBy 維度 key 中的 null bytes（`\0`）改為空字串，防止 ECharts 標籤顯示異常（#372）
- **Analysis**：`collectFilters` 欄位名稱 `op` → `operator`，並新增最少一個維度的前端驗證（#347 #348 #349）
- **Analysis**：`Avg` / `Max` / `Min` 在分組無資料時回傳 `null`，不再誤回傳 `0`（#336）
- **Analysis**：`FilterOperator.In` 過濾運算子補回（#331）
- **Analysis**：GroupBy 分隔符改為 Unicode 私用區字元，防止維度值含逗號時分組錯誤（#332）
- **Analysis**：切換圖表類型時正確 dispose 舊 ECharts 實例，避免記憶體洩漏（#333）
- **Analysis**：`Count` 聚合改為計算非 null 值數量（#337）
- **Analysis**：`DateHierarchy` 模式下自動降級為 InProcess 策略（#338）
- **Analysis**：`AutoSizeColumn` 加 try-catch，修復 CI Linux 無字型環境下的例外（#378）
- **Dashboard**：`Create` / `Update` 端點驗證 `WidgetType` 不得為空字串或空白（#382）

## [8.6.0] - 2026-03-12

### Added
- **ETL Module** (`WalkingTec.Mvvm.Etl`): Batch data import pipeline
  - Support for MSSQL and Oracle source databases
  - `EtlBulkJob` (zero-transform) and `EtlMappedJob<TIn,TOut>` (with transform) base classes
  - Staging Table → MERGE INTO pattern for idempotent upserts
  - Three watermark modes: FullLoad, Timestamp, Identity
  - Quartz.NET-based scheduling with dynamic cron management
  - Management UI: Job CRUD, execution history, real-time progress monitoring
  - Operations: trigger now, pause, resume, abort, skip next, reschedule
  - RBAC integration via existing PrivilegeFilter
  - `MockBulkLoader` and `MockEtlSource` for unit testing
  - Developer documentation (`docs/etl-module.md`)
  - Docker Compose test environment (`test/docker-compose.etl-test.yml`)
  - 65+ unit tests, 15 integration tests
- **Developer Manual** (`docs/wtm-developer-manual.md`): 3000+ line comprehensive manual
  - 16 sections covering all framework features with scene-based examples
  - Full API Controller CRUD example, TagHelper scenarios (Selector, Upload, Dialog, Cascade)
  - ETL end-to-end scenarios (MSSQL/Oracle/Transform), watermark lifecycle
  - Security deep-dive: PBKDF2 migration flow, JWT token rotation diagram, DataPrivilege usage
  - Multi-tenant Global Query Filter explanation with SaaS scenarios
  - Lookup Cache stampede protection walkthrough, invalidation flow
  - Code Generator step-by-step guide with generated file structure
  - 10 FAQ entries including deployment checklist

### Fixed
- **fix(dashboard):** Path traversal vulnerability in dashboard file storage (#220)
- **fix(dashboard):** Tenant isolation bypass — non-owner tenants could access dashboards by ID (#220)
- **fix(etl):** `GetMaxValue` crash on empty result set (#220)

## 8.5.1 (2026-03-11)

### 修復

* **fix(codegen)：** `InjectAnalysisAttributes` 重複 attribute 注入問題 — 原正則要求 attribute 緊鄰屬性宣告，當中間有其他 attribute（如 `[Required]`）或註解時會重複插入導致編譯錯誤。改用字串區塊搜尋取代嚴格正則（#162）

## 8.5.0 (2026-03-11)

### Code Generator — Analysis Mode 整合

Code Generator 新增 Analysis Mode 支援，自動產生 `[EnableAnalysis]`、`[Dimension]`、`[Measure]` attribute。

* **feat(codegen)：** 新增 `EnableAnalysis` checkbox 及 `IsDimension`/`IsMeasure` 欄位選擇（#156）
* **feat(codegen)：** 智慧預設 — string/enum→Dimension、數值→Measure、DateTime→Dimension(Month)（#156）
* **feat(codegen)：** ListVM 模板自動加入 `[EnableAnalysis]` attribute + using（#157）
* **feat(codegen)：** 生成後自動在 Model .cs 檔案插入 `[Dimension]`/`[Measure]` attribute — 正則插入、幂等保護、DateTime 自動 Hierarchy（#158）
* **feat(codegen)：** `FindModelFile` 從 MainDir 往上搜尋 Model source file，排除 bin/obj（#158）

### 測試與文件

* 10 個新 MSTest 測試覆蓋 attribute 注入、幂等性、檔案搜尋（#159）
* 更新 `docs/analysis-mode.md` 加入 Code Generator 整合說明（#159）

## 8.4.1 (2026-03-11)

### 修復

* **fix(cache)：** `RefreshAsync` 快取踩踏（Cache Stampede）漏洞 — 將 `InvalidateType` + `LoadFromDbAsync` + `SetCache` 三步移入 per-key SemaphoreSlim 鎖內，消除 invalidate 與 reload 之間的競爭條件（#153）
* **fix(security)：** `DecryptString` 補上 `FormatException` catch — 傳入非法 Base64 字串時回傳空字串而非拋出例外，與舊版 `DecryptStringLegacy` 行為一致（#153）

## 8.4.0 (2026-03-11)

### Lookup Cache — 靜態表/查找表快取系統

全新的 Cache-Aside 快取機制，為頻繁讀取但極少變更的查找表（如系統代碼、組織結構、角色列表等）提供零配置自動快取。

#### 核心功能

* **feat(cache)：** `[CacheLookup]` Attribute 標記模型即啟用快取，支援 `WarmOnStartup`、`TenantIsolation`、`ConnectionKey` 設定（#139）
* **feat(cache)：** `ILookupCacheService` + `LookupCacheService` — `IMemoryCache` 為底層的 Cache-Aside 實作，per-type `CancellationTokenSource` 批次失效（#139）
* **feat(cache)：** `WTMContext.GetLookup<T>()` / `GetLookupAsync<T>()` — 框架級快取存取 API，自動解析 `ConnectionKey` 與 `TenantIsolation`（#142）
* **feat(cache)：** `GetLookupItem<T>(predicate)` 單筆查詢 + `GetLookupSelectList<T>()` 下拉選單整合（#142）

#### 架構改善

* **refactor(cache)：** 從 `LookupInvalidationInterceptor`（EF SaveChanges 攔截器）重構為 `FrameworkContext.SaveChanges()` override — 消除 Activator.CreateInstance 的 interceptor 注入限制（#139）
* **refactor(cache)：** Warmup 從 `Task.Delay(3s)` 改為 `IHostApplicationLifetime.ApplicationStarted` 事件驅動 — 不再猜測啟動時間（#143）
* **feat(cache)：** `IReadOnlyList<T>` 回傳型態防止快取被意外修改（#142）
* **feat(cache)：** 全域 `LookupCacheOptions.DefaultTenantIsolation` + Attribute 三態覆寫（Unset/true/false sentinel pattern）（#142）

#### 穩定性

* **feat(cache)：** Per-key `SemaphoreSlim(1,1)` stampede 防護 — 10s timeout fallback，防止快取過期時大量並發同時打 DB（#146）
* **feat(cache)：** `RefreshAsync<T>()` 手動刷新 API（#146）

#### 測試

* 36 個 MSTest 涵蓋：快取命中/失效/過期、tenant 隔離、warmup、stampede 競爭、ConnectionKey 多 DB、GetLookupItem/SelectList、RefreshAsync

### 安全修復

* **feat(security)：** 連線字串加密從 DES（56-bit）升級為 AES-256-CBC — SHA-256 key derivation、random IV、`DecryptString` 自動 fallback DES 相容舊資料（#148）

### 品質改善

* **fix(core)：** `DPWhere` / `AppendSelfDPWhere` 支援 in-memory `IQueryable` — 偵測 `EnumerableQuery<T>` 自動切換 `Enumerable.Any`，解決 Lookup Cache 整合及測試場景的 Expression Tree 失敗（#149）
* **fix(ci)：** Tag push 時自動上傳 `.nupkg` 為 Release assets（#137）
* **test：** 統一測試框架 + 覆蓋率門檻、DataPrivilege API / BaseImportVM / async BaseCRUDVM / 密碼遷移等 49 個新測試（#128–#132）

## 8.3.1 (2026-03-10)

### Analysis Mode Phase 2 — 跨 DB 分析引擎優化

本版本完成 Analysis Mode Phase 2 全部 16 項子任務（#91–#106），涵蓋跨 DB 策略、日期鑽取、Pivot 樞紐表、圖表互動等功能。

#### 基礎設施（Wave 1）

* **feat(analysis)：** 新增 `DateHierarchy` enum（Year/Quarter/Month/Day）與 `AnalysisFieldMeta.IsDate` 自動偵測（#91, #92, #93）
* **feat(analysis)：** 提取 `IGroupByStrategy` 策略模式 — `ServerSideGroupByStrategy`（EF Core SQL push）+ `InProcessGroupByStrategy`（記憶體聚合），`GroupByStrategyResolver` 依 DBType 路由（#94）
* **feat(analysis)：** `ServerSideGroupByStrategy` 實作，MSSQL/Oracle 優先 server-side GroupBy（#95）
* **feat(analysis)：** `AnalysisQueryEngine` 整合雙軌策略 + ServerSide → InProcess 自動 fallback（透過 exception filter）（#96）
* **feat(analysis)：** `IAnalysisCache` + `QueryHash` 快取層，SHA256-based 查詢結果快取（#97）

#### 功能擴展（Wave 2）

* **feat(analysis)：** `DateTruncator` 跨 DB 日期截斷，用 `.Year`/`.Month`/`.Day` CLR 屬性 + 整數 key 格式（#98）
* **feat(analysis)：** 前端日期鑽取 UI — hierarchy dropdown（年/季/月/日）、`formatDateKey` 純函式（#99）
* **feat(analysis)：** API DTO 更新 — `DimensionHierarchies` 字典加入 `AnalysisQueryRequest`（#100）
* **feat(analysis)：** `AnalysisPivotEngine` — 純記憶體 row-column 交叉轉置，max 50 pivot values（#101）
* **feat(analysis)：** Pivot 前端 — 樞紐模式 toggle、pivot radio selector、`renderPivotTable`（動態表頭、null→"-"、水平捲動）、`renderPivotChart`（stacked bar）（#102）
* **feat(analysis)：** `POST /_analysis/pivot` API endpoint（#103）

#### 進階互動（Wave 3）

* **feat(analysis)：** 圖表 drill-down 互動 — ECharts click handler、drill history stack（push/pop/reset）、日期自動降階（Year→Quarter→Month→Day）、麵包屑 UI（#104）
* **feat(analysis)：** `IAnalysisFieldPolicy` 欄位級權限控制介面（#105）
* **feat(analysis)：** `FilterOperator.In` 支援多值篩選（#106）

#### 測試

* 新增 Analysis Mode 相關 MSTest 與 Jest 測試，涵蓋 Engine fallback、DateTruncator、PivotEngine（9 tests）、Controller pivot endpoint（4 tests）、前端 drill-down（10 tests）、前端 pivot（9 tests）等

## 8.3.0 (2026-03-09)

### ⚠️ Breaking Changes

* **移除 Elsa 工作流整合：** 完全解耦 Elsa 2.x 依賴（Controller、Model、UI assets、NuGet 引用）。如需工作流功能，請自行整合 Elsa 或其他方案

### 安全修復

* **fix(deps)：** `System.Text.Json` 8.0.0 → 8.0.6 — 修復 2× High CVE（GHSA-hh2w-p6rv-4g7w, GHSA-8g4q-xg66-9fp4）
* **fix(deps)：** `SixLabors.ImageSharp` 3.1.3 → 3.1.12 — 修復 2× High + 4× Moderate CVE
* **fix(deps)：** `DUWENINK.Captcha` 0.7.0 → 0.8.0（最後一個 .NET 8 相容版本）
* **chore(deps)：** ReactDemo 與 Vue3Demo npm audit fix（200→22, 46→3 殘留漏洞）

### 品質改善

* **refactor(nullable)：** 完成 Core 專案全部 168 個 `#nullable disable` 檔案的 nullable 現代化（Models, Support, Grid, Config, Helpers, ViewModels 等），僅餘 `BaseImportVM.cs` 內 5 行局部 scope
* **fix(nullable)：** 修復 7 個高優先檔案中的 null-forgiving 問題（`MSD!` → `MSD?`、double-`!` chain 拆解、catch block null guard）
* **quality：** 減少 Core nullable 警告基線（56 項清理）
* **refactor(nullable)：** `WTMContext.cs`、`DataContext.cs`、`IDataContext.cs`、`DCExtension.cs`、`PropertyHelper.cs`、`ListVMExtension.cs` 等核心檔案 nullable 啟用

### 功能改善

* **feat(analysis)：** `collectSelection()` querySelector 從全域 `document` 改為 scoped 到 panel 元素，避免多 gridId 衝突
* **fix(taghelper)：** 修復 `ipr_xs`/`ipr_sm` 巢狀 container 未清除的 context 洩漏問題
* **test(taghelper)：** 新增 `DateTimeTagHelper` rendering-path 測試（`IsRange`/`RangeStartName`/`RangeEndName`）

### 依賴更新

* Dependabot 批次更新：cross-spawn, nanoid, zrender/echarts, vue, vite, @babel/helpers, @babel/runtime, lodash, axios, qs, express 等

### 文件與工具

* **docs：** 新增 Project Mission 與 Development Principles 至 `CLAUDE.md`
* **feat：** 新增 dry-run 發布工作流 + package smoke QA + 一鍵發布腳本
* **test：** CI 新增 release tooling 驗證

## 8.2.0 (2026-03-06)

* **新增（結構化日誌）：** Opt-in Serilog 整合，透過 `AddWtmSerilog()` / `UseWtmSerilog()` 啟用。支援 Console + JSON 檔案 Sink，每日滾動、30 天保留、HTTP 請求記錄、自訂 Sink 擴充。與現有 WTMLogger / ActionLog 並存不衝突
* **新增（ProblemDetails）：** Opt-in RFC 7807 錯誤回應，透過 `UseWtmProblemDetails()` 啟用。API 路由（`/api/*` 或 `Accept: application/json`）回傳結構化 JSON 錯誤含 traceId 串聯。MVC 頁面行為不受影響
* **新增（審計攔截器）：** `EmptyContext.SaveChanges` 自動填入 `CreateBy`/`CreateTime`/`UpdateBy`/`UpdateTime`。僅填空值不覆寫，與現有 VM 手動設定完全相容。透過 `IDataContext.CurrentUserCode` 傳遞當前用戶，`WTMContext.DC` getter 自動注入
* **新增（Rate Limiting）：** Opt-in IP 限流，透過 `AddWtmRateLimiting()` / `UseWtmRateLimiting()` 啟用。預設 100 次/60 秒固定窗口，超限回 429。支援自訂 `PermitLimit`、`WindowSeconds`、`QueueLimit` 及 `CustomConfig` 完整覆寫
* **清理：** 移除 `SearchPanelTagHelper.cs` 未使用的 `using Newtonsoft.Json.Schema`
* **文件：** 新增 `docs/structured-logging.md` — 完整使用手冊，含設定、生產環境建議、log 查詢技巧、前端整合範例
* **依賴：** 新增 `Serilog.AspNetCore` 8.0.3
* **測試：** Serilog 6 tests + ProblemDetails 7 tests + AuditInterceptor 6 tests + RateLimiting 3 tests，全部 CI ✅

## 8.1.17 (2026-03-05)

* **新增（Analysis Mode）：** `[Dimension]` / `[Measure]` / `[EnableAnalysis]` 屬性標注系統，無需額外程式碼即可在列表頁切換分析模式
* **新增（Analysis Mode）：** `/_analysis/meta`、`/_analysis/query`、`/_analysis/export` 三個 API，支援動態 GroupBy 聚合（Sum / Count / Avg / Max / Min）
* **新增（Analysis Mode）：** `DataTableTagHelper.EnableAnalysis` 屬性，一行 HTML 啟用分析按鈕
* **新增（Analysis Mode）：** `framework_analysis.js` 前端 UI，含 ECharts 自動選型圖表（Bar / Stacked Bar / Line / 數字卡片）
* **新增（Analysis Mode）：** Excel (.xlsx) 與 CSV 匯出；CSV 防 formula injection（`=`, `+`, `-`, `@` 前置 tab）
* **安全：** `AnalysisVmRegistry` 白名單機制；`AnalysisQueryEngine` 全程 Expression Tree，無 SQL 字串拼接
* **修正：** `ExecuteDynamic` 未 unwrap `TargetInvocationException`，導致無效欄位回 500 而非 400
* **修正：** 移除未實作的 `FilterOperator.In`（宣告但無 switch case，原回 `NotSupportedException`）
* **效能：** 資料載入硬上限 `MaxMaterializeRows = 50,000` 防止 OOM；結果超過 10,000 列自動截斷
* **測試：** Engine 30 tests + Exporter 6 tests + Controller 16 tests + JS 42 tests，全部 CI ✅

## 8.1.16 (2026-03-04)

* **修正（BUG-1）：** CodeGenVM `GetRandomValues()` 對 readonly 欄位 NullReferenceException；`GenerateAddFKModel` null guard
* **修正（BUG-2）：** `UploadImage` 上傳非圖片檔回 500，改為 try-catch 回 400 Bad Request
* **修正（BUG-3）：** 多租戶下 `FileAttachment`（實作 `ITenant`）受 EF global query filter 影響不可見；改用 `IgnoreQueryFilters()`
* **修正（BUG-4）：** Elsa + MySQL 不自動建表；MySQL treats schema=database，`WtmElsaContext.Schema` 對 MySQL 回 null
* **修正（BUG-5）：** CodeGen 路徑在非標準環境 `IndexOf("\bin\Debug\")` 回 -1 導致 `Substring(0,-1)` 例外；改用 `Directory.GetCurrentDirectory()` fallback
* **修正（BUG-6）：** ComboBox `selectVal` 空時強制所有 `item.Selected = false`；改為只在 `selectVal.Count > 0` 時覆蓋
* **新增（BUG-7）：** Oracle 連線新增 `Enabled` 屬性（預設 true），`appsettings.json` 可設 `Enabled: false` 停用個別連線
* **效能（BUG-8）：** 新增 `DbConnectionWarmupService`（BackgroundService），啟動 2s 後呼叫 `CanConnectAsync()` 消除 Oracle 冷啟動延遲

## 8.1.15 (2026-03-04)

* **測試：** 50+ MSTest 測試，覆蓋 BaseCRUDVM、BasePagedListVM、BatchVM、RBAC、TokenService、PasswordHelper
* **CI：** 新增覆蓋率流水線（dotnet-coverage + Coverlet）
* **修正：** JWT `GenerateAccessToken` 新增 `jti` claim（`Guid.NewGuid().ToString("N")`），避免同秒產生相同 Token

## 8.1.14 (2026-03-04)

* **非同步（ASYNC-1）：** 消除全部 `.Result` / `.Wait()` 同步等待，解決 ThreadPool 飢餓風險
* **依賴（DEPS-1）：** EF Core → 8.0.22, Quartz → 3.16.0, Elsa → 2.15.2, Swashbuckle → 6.6.2
* **程式碼品質（QUALITY-1）：** 更新 `.editorconfig` 規則
* **程式碼品質（QUALITY-2）：** 啟用 Nullable，168 個遺留檔案加 `#nullable disable`
* **測試（TEST-1）：** 建立 xUnit 測試專案，17 個基礎測試
* **CI：** dotnet test TRX logger + artifact 上傳

## 8.1.13 (2026-03-04)

* **安全(Breaking Change)：** 密碼儲存從 MD5 升級至 PBKDF2（ASP.NET Core Identity PasswordHasher），自動向下相容舊 MD5 帳號；**部署前必須執行 db-migration-8.1.13.sql 以擴展 Password 欄位至 256 字元**
* **安全：** JWT 新增 Refresh Token 機制，支援 Token Rotation 防止重複使用，提供 /api/_account/refreshtoken 與 /api/_account/revoketoken 端點
* **依賴：** DotNetCore.NPOI 1.2.3 → NPOI 2.7.6
* **依賴：** 移除 .NET Core 2.1.x 舊版依賴套件
* **CI：** 新增 GitHub Actions 建置、測試與套件弱點掃描工作流

##8.1.12(2024-10-10)
* **修改：**   修复Blazor和Vue下多租户工作流的Bug

##8.1.10(2024-7-15)
* **修改：**   修复Layui导出查询条件不变的bug
* **修改：**   修复自带代码生成器Blazor生成代码大小写的问题

##8.0.6(2024-3-18)

* **修改：**   更新Elsa工作流到2.14.1，更好的支持.net8
* **修改：**   更新验证码的处理，避免linux缺少字体的问题，感谢DUWENINK提供

##8.0.4(2024-2-21)

* **修改：**   修复工作流的一些bug


##8.0.2(2024-2-1)

* **修改：**   修复EF8.0默认不支持sqlserver 2014一下的问题
* **修改：**   修复Layui模式下TreeTagHelper不选择也会提交空字符串的问题

* ##8.0.1(2024-1-19)
* **修改：**   修复工作流在Oracle中不能正常启动的问题

##8.0.0(2024-1-11)
* **修改：**  全面升级支持dotnet8



## v6.x.x

##6.5.1(2023-11-19)
* **修改：**  修复GetSelectItemList方法的bug
* **修改：**  修复LayUI Tab页中使用按钮组会导致Tab页切换失效的问题

##6.4.9(2023-11-14)
* **新增：**  AddWorkflow函数添加了option参数，可以添加自定义工作流节点

##6.4.8(2023-11-13)
* **新增：**  工作流审批节点增加角色，部门，部门负责人审批等选项
* **新增：**  工作流现在可以读取CRUDVM中的SetInclude指定的关联表
* **修改：**  删除表单数据时会自动删除相关的工作流数据
* **修改：**  修复单点登录时登陆主站可能引发异常的问题

##6.4.3(2023-10-7)
* **新增：**  LayUI下新增FlowInfoTagHelper，用来显示审批记录
* **修改：**  修复工作流在Sqlite中报错的问题

##6.4.1(2023-9-16)
* **新增：**  修复工作流在MySql数据库中报错的问题
* **修改：**  修复多租户用户使用不同域名登陆和切换的问题

##6.4.0(2023-9-3)
* **新增：**  添加工作流功能，老系统需要重新生成数据库，因为增加了一些系统表
* **修改：**  用户组名称更改为部门
* **修改：**  修复LayUI中TreeContainer的Bug

##6.3.30(2023-7-28)
* **修改：**  修复创建默认数据库时，默认菜单Api方法的菜单显示为false
* **修改：**  修复了LayUI模式联动会覆盖初始绑定的数据的问题
* **修改：**  继承BaseApiController的控制器没有权限的时候会返回403错误

##6.3.29(2023-6-29)
* **修改：**  默认的Json序列化，数字类型仍然会被输出为字符串，这是因为很多前端控件都需要对比字符串
* **修改：**  修复了LayUI模式下导出之后搜索的问题
* **修改：**  优化了LayUI模式下TreeContainer控件
* **修改：**  默认的Json序列化，添加了可为空的类型的读写判断


##6.3.25(2023-6-8)
* **新增：**  新增针对WtmPlus中生成的vue3的项目的简易代码生成器
* **修改：**  修复了多租户模式下GetUserDC方法
* **修改：**  修复了多租户模式下Excel导入的问题
* **修改：**  修复了LayUI模式下TreeContainer和Searcher组合查询的问题
* **修改：**  修改了默认的Json序列化，数字类型不再被序列化为字符串
* **修改：**  修改FileHandler，现在FileHandler中可以使用Wtm进行数据库操作

##6.3.22(2023-4-4)
* **修改：**  FileProvider加入异常判断
* **修改：**  修复LayUI模式中TreeTagHelper多选的问题
* **修改：**  修复IPersistPoco数据唯一性验证没有考虑IsValid字段的问题

##6.3.20(2023-2-19)
* **修改：**  修复日志记录的配置问题
* **修改：**  修复LayUI模式中重置搜索表单不能重置多个下拉菜单的问题
* **修改：**  修复LayUI模式中子表控件删除行之后赋值的问题
* **修改：**  修复LayUI模式中设置默认搜索条件后导出的问题


##6.3.19(2023-2-11)
* **修改：**  修复LayUI模式下联动Tree控件的选中问题
* **修改：**  优化BatchVM中批量修改的默认操作


##6.3.18(2023-2-6)
* **修改：**  修复LayUI模式下重置按钮不能重置下拉菜单的问题
* **修改：**  修复LayUI模式下子表控件对于日期类型的保存问题

##6.3.16(2023-2-2)
* **修改：**  修复LayUI模式下Tree控件的选中问题
* **修改：**  修复LayUI模式下DateTime控件绑定int的问题


##6.3.15(2023-1-30)
* **修改：**  更新Blazor控件库到最新版本
* **修改：**  修复Layui模式下的一些js错误
* **修改：**  修复Layui模式下TextBox的Change-Func和Done-Func无效的问题

##6.3.14(2023-1-13)
* **修改：**  修复多租户情况下上传和导入可能出现的异常
* **修改：**  修复Layui模式下Tree和Combobox的bug
* **修改：**  修复OSS上传图片没有自动指定ContentType的问题


##6.3.9(2022-11-17)
* **修改：**  系统自带的部门，角色，租户等表可以通过自定义类继承基类的方式增加字段


##6.3.8(2022-11-15)
* **修改：**  更新版本以适应新版的Bootstrap Blazor 7
* **修改：**  Blazor现在可以通过appsettings中的PageMode来设置默认是单一页还是多Tab页
* **修改：**  修复了LayUI子表控件超过10行删除数据的bug
* **修改：**  LayUI模式子表现在可以通过SetEditType方法设置子表控件是否只读
* **修改：**  LayUI模式子表现在可以通过SetEditType方法设置子表中日期控件的格式
* **修改：**  修复内置Login方法大小写判断的bug

##6.3.7(2022-10-19)
* **修改：**  更新版本以适应新版的Bootstrap Blazor控件库
* **修改：**  修复了SoftKey属性引发的bug
* **修改：**  增加了默认菜单表页面名称字段的长度
* **修改：**  修复了Blazor模式OpdenDialog方法

##6.3.4(2022-8-10)
* **修改：**  修复了主子表数据维护多租户没有正确赋值的问题
* **修改：**  修复了LayUI模式Selector控件显示不正确的问题
* **修改：**  修复了ListVM对特殊字符的过滤

##6.3.1(2022-7-25)
* **修改：**  修复了LayUI模式下Selector联动的问题
* **修改：**  修复了非外键关联的Include字段问题
* **修改：**  修复了多租户根据url判断租户的问题

##6.3.0(2022-7-22)
* **修改：**  优化Blazor的用户信息，提升页面效率
* **修改：**  配合BootStrapBlazor控件的最新版本，更新树形列表
* **修改：**  Blazor文件上传控件增加了文件下载
* **修改：**  修复多数据库导入的bug
* **修改：**  修复Layui模式Selector绑定非主键时显示的问题
* **修改：**  修复Layui模式Display控件的样式

##6.2.6(2022-7-5)
* **修改：**  修复Jwt登录时间验证的问题
* **修改：**  修复Layui模式中Tree控件禁用的问题
* **修改：**  移除过时引用
* **修改：**  修复主键类型为string时Crud的问题
* **修改：**  修复WtmJob Displose时的bug
* **修改：**  优化Blazor菜单，感谢akin的PR

##6.2.4(2022-6-16)
* **修改：**  优化登录
* **修改：**  修复WtmFileProvider直接使用的问题
* **新增：**  为配合WtmPlus的新功能，框架底层增加SoftKey,SoftFK属性，用于标记非主键关联的模型

##6.2.3(2022-6-12)
* **新增：**  QuartzRepeatAttribute增加了DelaySeconds参数，可以控制延迟多少秒启动服务
* **修改：**  恢复LoginUserInfo中的UserId以兼容老系统
* **修改：**  修复Layui模式下Combobox处理默认值的问题
* **修改：**  修复登录时Token会加长的问题
* **修改：**  现在DoEdit方法会自动检查继承TreePoco的模型，其父级ID不能被修改为本身ID
* **修改：**  增加了一些验证，避免中间件报错，提高性能

##6.2.2(2022-6-7)
* **HotFix：**  修复附件上传问题

##6.2.1(2022-6-7)

* **新增：**  新增CanNotEditAttribute，用于标记在模型属性上，指明该字段不应该被修改。
* **新增：**  优化VM内包含其他VM时框架默认的处理方法，现在框架默认会自动给子VM赋必须的值，并和表单提交的值对应。
* **修改：**  修复MainTenantOnly属性会导致权限失效的问题
* **修改：**  修复登录时Token会加长的问题
* **修改：**  修复Blazor模式无法删除租户的问题
* **修改：**  修复Blazor模式添加外部菜单的显示问题
* **修改：**  修复LayUI模式Combobox在Https下无法下载数据的问题

##6.2.0(2022-6-5)

本次为大版本更新，包含中断性更改，老项目升级时需要手动更新旧数据库以及覆盖默认生成的项目文件

* **新增：**  新增多租户支持，支持单数据库，独立数据库以及混合模式，使用方法参见文档 https://wtmdoc.walkingtec.cn/#/Global/MultiTenant
* **新增：**  新增单点登录支持，使用方法参见文档 https://wtmdoc.walkingtec.cn/#/Global/SSO
* **新增：**  新增统一用户，角色，用户组管理支持，WTM现在可以用来架构微服务风格的分布式系统。
* **新增：**  Layui和Blazor新增默认的多租户管理界面，其他UI后续会添加
* **新增：**  新增MainTenantOnlyAttribute，用于标记方法不能被子租户使用
* **修改：**  用户组修改为树形结构，可作为部门组织结构使用，为下一步工作流做好准备
* **修改：**  由于用户组修改为树形结构，用户组的数据权限也可以向下继承
* **修改：**  重构用户登录，重新登陆，权限验证等逻辑，更大程度上使用缓存，大幅提高性能
* **修改：**  用户表的其他字段现在会被自动读取到LoginUserInfo.Attributes中
* **修改：**  Blazor支持最新的BB控件库
* **修改：**  修复了文件上传的一些安全性问题
* **修改：**  优化导出操作
* **修改：**  修复Layui Combobox和Tree控件的一些bug
* **中断性修改：**  移除了系统自带的PersistedGrant表,简化了jwt登录流程，现在不再需要一个单独的RefreshToken来刷新Token，而是登陆后调用RefreshToken接口刷新当前用户的Token
* **中断性修改：**  系统自带的表，除了FrameworkMenu外，都新增了TenantCode字段
* **中断性修改：**  系统自带的FrameworkGroup字段发生了改变，变为树形结构，且增加了Manager字段
* **中断性修改：**  新增新的系统表FrameworkTenant
* **中断性修改：**  Appsettings文件中新增EnableTenant配置



##6.1.1(2022-4-1) 
* **新增：**  集成Quartz作业调度，为后续工作流所需内部定时任务做好准备，使用方法参见文档 https://wtmdoc.walkingtec.cn/#/Global/Quartz
* **修改：**  日志分类中增加了“作业”一项
* **修改：**  Layui模式下登录现在会统一调用/api/_account/login方法，为后续单点登录做好准备
* **修改：**  现在当用户缓存失效时，框架会自动调用/api/_account/login或/api/_account/loginJwt获取用户信息，如无特殊情况，不需要再重写ReloadUser方法了
* **修改：**  修复layui数据权限维护时下拉菜单的bug
老项目更新时，除了升级Nuget包，还应该通过官网或Plus生成新项目，将系统自带的Controller和View覆盖一下

##6.0.7(2022-3-21) 
* **HotFix：**  修复使用用xmselect引发的联动和TreeContainer的bug

##6.0.6(2022-3-20) 
* **修改：**  使用xmselect控件重写了Layui的Combobox和Tree，老用户更新的时候需要手动添加xmselect的js文件：https://gitee.com/maplemei/xm-select/releases/v1.2.4
把这个文件放到wwwroot/layui下面。LayUI项目的用户还需要手动在/Views/Shared/_Layout.cshtml文件中加入对这个js的引用， 其他UI类型的项目不需要，但是因为其他UI类型的项目代码生成器也是用的layui的界面，所以还是需要copy xm-select的js文件到wwwroot/layui下面的

* **修改：**  Layui模式下修复了数据列表指定line-height的bug
* **修改：**  Layui模式下MakeViewButton方法现在可以生成更美观的图片预览
* **修改：**  验证码现在会自动读取系统安装的字体，linux下部署的时候不会出现找不到字体的问题了
* **修改：**  Blazor项目默认使用自带的字体文件
* **修改：**  修复Blazor项目中向localstorage存储大量数据报错的问题
* **修改：**  Blazor的弹出窗口现在可以最大化和拖动
* **修改：**  修复Blazor项目中切换多语言有可能报错的问题

##6.0.5(2022-2-28) 
* **修改：**  默认初始化数据中加入了普通用户角色
* **修改：**  优化了数据权限查询语句
* **修改：**  修复登录用户保存信息是并发的问题
* **修改：**  修复了layui模式radio控件绑定空值的bug


##6.0.2(2021-12-26) 
* **修改：**  修改Blazor默认代码生成，适应BootstrapStrap 6.x新版本
* **修改：**  再次更新数据权限逻辑，新的默认逻辑为，如果设定了数据权限的表本身继承了BasePoco，而且没有给当前用户配置任何数据权限，那么用户默认可以看到自己加的数据。如果给用户配置了数据权限，则根据数据权限的配置显示数据，不管是否是当前用户添加的。
* **修改：**  修改了layui transfer控件绑定数据的bug
* **修改：**  修改了layui多选控件设置必填的bug

## v5.x.x

##5.10.30(2023-7-28)
* **修改：**  修复创建默认数据库时，默认菜单Api方法的菜单显示为false
* **修改：**  修复了LayUI模式联动会覆盖初始绑定的数据的问题
* **修改：**  继承BaseApiController的控制器没有权限的时候会返回403错误


##5.10.29(2023-6-29)
* **修改：**  默认的Json序列化，数字类型仍然会被输出为字符串，这是因为很多前端控件都需要对比字符串
* **修改：**  修复了LayUI模式下导出之后搜索的问题
* **修改：**  优化了LayUI模式下TreeContainer控件
* **修改：**  默认的Json序列化，添加了可为空的类型的读写判断

##5.10.25(2023-6-8)
* **新增：**  新增针对WtmPlus中生成的vue3的项目的简易代码生成器
* **修改：**  修复了多租户模式下GetUserDC方法
* **修改：**  修复了多租户模式下Excel导入的问题
* **修改：**  修复了LayUI模式下TreeContainer和Searcher组合查询的问题
* **修改：**  修改了默认的Json序列化，数字类型不再被序列化为字符串
* **修改：**  修改FileHandler，现在FileHandler中可以使用Wtm进行数据库操作

##5.10.22(2023-4-4)
* **修改：**  FileProvider加入异常判断
* **修改：**  修复LayUI模式中TreeTagHelper多选的问题
* **修改：**  修复IPersistPoco数据唯一性验证没有考虑IsValid字段的问题

##5.10.20(2023-2-19)
* **修改：**  修复日志记录的配置问题
* **修改：**  修复LayUI模式中重置搜索表单不能重置多个下拉菜单的问题
* **修改：**  修复LayUI模式中子表控件删除行之后赋值的问题
* **修改：**  修复LayUI模式中设置默认搜索条件后导出的问题

##5.10.19(2023-2-11)
* **修改：**  修复LayUI模式下联动Tree控件的选中问题
* **修改：**  优化BatchVM中批量修改的默认操作

##5.10.18(2023-2-6)
* **修改：**  修复LayUI模式下重置按钮不能重置下拉菜单的问题
* **修改：**  修复LayUI模式下子表控件对于日期类型的保存问题

##5.10.16(2023-2-2)
* **修改：**  修复LayUI模式下Tree控件的选中问题
* **修改：**  修复LayUI模式下DateTime控件绑定int的问题


##5.10.15(2023-1-30)
* **修改：**  更新Blazor控件库到最新版本
* **修改：**  修复Layui模式下的一些js错误
* **修改：**  修复Layui模式下TextBox的Change-Func和Done-Func无效的问题

##5.10.14(2023-1-13)
* **修改：**  修复多租户情况下上传和导入可能出现的异常
* **修改：**  修复Layui模式下Tree和Combobox的bug
* **修改：**  修复OSS上传图片没有自动指定ContentType的问题

##5.10.9(2022-11-17)
* **修改：**  系统自带的部门，角色，租户等表可以通过自定义类继承基类的方式增加字段

##5.10.8(2022-11-15)
* **修改：**  更新版本以适应新版的Bootstrap Blazor 7
* **修改：**  Blazor现在可以通过appsettings中的PageMode来设置默认是单一页还是多Tab页
* **修改：**  修复了LayUI子表控件超过10行删除数据的bug
* **修改：**  LayUI模式子表现在可以通过SetEditType方法设置子表控件是否只读
* **修改：**  LayUI模式子表现在可以通过SetEditType方法设置子表中日期控件的格式
* **修改：**  修复内置Login方法大小写判断的bug


##5.10.7(2022-10-19)
* **修改：**  更新版本以适应新版的Bootstrap Blazor控件库
* **修改：**  修复了SoftKey属性引发的bug
* **修改：**  增加了默认菜单表页面名称字段的长度
* **修改：**  修复了Blazor模式OpdenDialog方法

##5.10.4(2022-8-10)
* **修改：**  修复了主子表数据维护多租户没有正确赋值的问题
* **修改：**  修复了LayUI模式Selector控件显示不正确的问题
* **修改：**  修复了ListVM对特殊字符的过滤

##5.10.1(2022-7-25)
* **修改：**  修复了LayUI模式下Selector联动的问题
* **修改：**  修复了非外键关联的Include字段问题
* **修改：**  修复了多租户根据url判断租户的问题

##5.10.0(2022-7-22)
* **修改：**  优化Blazor的用户信息，提升页面效率
* **修改：**  配合BootStrapBlazor控件的最新版本，更新树形列表
* **修改：**  Blazor文件上传控件增加了文件下载
* **修改：**  修复多数据库导入的bug
* **修改：**  修复Layui模式Selector绑定非主键时显示的问题
* **修改：**  修复Layui模式Display控件的样式

##5.9.6(2022-7-5)
* **修改：**  修复Jwt登录时间验证的问题
* **修改：**  修复Layui模式中Tree控件禁用的问题
* **修改：**  移除过时引用
* **修改：**  修复主键类型为string时Crud的问题
* **修改：**  修复WtmJob Displose时的bug
* **修改：**  优化Blazor菜单，感谢akin的PR

##5.9.4(2022-6-16)
* **修改：**  优化登录
* **修改：**  修复WtmFileProvider直接使用的问题
* **新增：**  为配合WtmPlus的新功能，框架底层增加SoftKey,SoftFK属性，用于标记非主键关联的模型


##5.9.3(2022-6-12)
* **新增：**  QuartzRepeatAttribute增加了DelaySeconds参数，可以控制延迟多少秒启动服务
* **修改：**  恢复LoginUserInfo中的UserId以兼容老系统
* **修改：**  修复Layui模式下Combobox处理默认值的问题
* **修改：**  修复登录时Token会加长的问题
* **修改：**  现在DoEdit方法会自动检查继承TreePoco的模型，其父级ID不能被修改为本身ID
* **修改：**  增加了一些验证，避免中间件报错，提高性能

##5.9.2(2022-6-7)
* **HotFix：**  修复附件上传问题

##5.9.1(2022-6-7)

* **新增：**  新增CanNotEditAttribute，用于标记在模型属性上，指明该字段不应该被修改。
* **新增：**  优化VM内包含其他VM时框架默认的处理方法，现在框架默认会自动给子VM赋必须的值，并和表单提交的值对应。
* **修改：**  修复MainTenantOnly属性会导致权限失效的问题
* **修改：**  修复登录时Token会加长的问题
* **修改：**  修复Blazor模式无法删除租户的问题
* **修改：**  修复Blazor模式添加外部菜单的显示问题
* **修改：**  修复LayUI模式Combobox在Https下无法下载数据的问题


##5.9.0(2022-6-5)

本次为大版本更新，包含中断性更改，老项目升级时需要手动更新旧数据库

* **新增：**  新增多租户支持，支持单数据库，独立数据库以及混合模式，使用方法参见文档 https://wtmdoc.walkingtec.cn/#/Global/MultiTenant
* **新增：**  新增单点登录支持，使用方法参见文档 https://wtmdoc.walkingtec.cn/#/Global/SSO
* **新增：**  新增统一用户，角色，用户组管理支持，WTM现在可以用来架构微服务风格的分布式系统。
* **新增：**  Layui和Blazor新增默认的多租户管理界面，其他UI后续会添加
* **修改：**  用户组修改为树形结构，可作为部门组织结构使用，为下一步工作流做好准备
* **修改：**  由于用户组修改为树形结构，用户组的数据权限也可以向下继承
* **修改：**  重构用户登录，重新登陆，权限验证等逻辑，更大程度上使用缓存，大幅提高性能
* **修改：**  Blazor支持最新的BB控件库
* **修改：**  修复了文件上传的一些安全性问题
* **修改：**  优化导出操作
* **修改：**  修复Layui Combobox和Tree控件的一些bug
* **中断性修改：**  移除了系统自带的PersistedGrant表,简化了jwt登录流程，现在不再需要一个单独的RefreshToken来刷新Token，而是登陆后调用RefreshToken接口刷新当前用户的Token
* **中断性修改：**  系统自带的表，除了FrameworkMenu外，都新增了TenantCode字段
* **中断性修改：**  系统自带的FrameworkGroup字段发生了改变，变为树形结构，且增加了Manager字段
* **中断性修改：**  新增新的系统表FrameworkTenant
* **中断性修改：**  Appsettings文件中新增EnableTenant配置


##5.8.3(2022-4-1) 
* **新增：**  集成Quartz作业调度，为后续工作流所需内部定时任务做好准备，使用方法参见文档 https://wtmdoc.walkingtec.cn/#/Global/Quartz
* **修改：**  日志分类中增加了“作业”一项
* **修改：**  Layui模式下登录现在会统一调用/api/_account/login方法，为后续单点登录做好准备
* **修改：**  现在当用户缓存失效时，框架会自动调用/api/_account/login或/api/_account/loginJwt获取用户信息，如无特殊情况，不需要再重写ReloadUser方法了
* **修改：**  修复layui数据权限维护时下拉菜单的bug
* **修改：**  图形操作改用ImageSharp
老项目更新时，除了升级Nuget包，还应该通过官网或Plus生成新项目，将系统自带的Controller和View覆盖一下

##5.7.9(2022-3-21) 
* **HotFix：**  修复使用用xmselect引发的联动和TreeContainer的bug

##5.7.7(2022-3-20) 
* **修改：**  使用xmselect控件重写了Layui的Combobox和Tree，老用户更新的时候需要手动添加xmselect的js文件：https://gitee.com/maplemei/xm-select/releases/v1.2.4
把这个文件放到wwwroot/layui下面。LayUI项目的用户还需要手动在/Views/Shared/_Layout.cshtml文件中加入对这个js的引用， 其他UI类型的项目不需要，但是因为其他UI类型的项目代码生成器也是用的layui的界面，所以还是需要copy xm-select的js文件到wwwroot/layui下面的

* **修改：**  Layui模式下修复了数据列表指定line-height的bug
* **修改：**  Layui模式下MakeViewButton方法现在可以生成更美观的图片预览
* **修改：**  验证码现在会自动读取系统安装的字体，linux下部署的时候不会出现找不到字体的问题了
* **修改：**  Blazor项目默认使用自带的字体文件
* **修改：**  修复Blazor项目中向localstorage存储大量数据报错的问题
* **修改：**  Blazor的弹出窗口现在可以最大化和拖动
* **修改：**  修复Blazor项目中切换多语言有可能报错的问题

##5.7.6(2022-2-28) 
* **修改：**  默认初始化数据中加入了普通用户角色
* **修改：**  优化了数据权限查询语句
* **修改：**  修复登录用户保存信息是并发的问题
* **修改：**  修复了layui模式radio控件绑定空值的bug

##5.7.3 (2021-12-26) 
* **修改：**  再次更新数据权限逻辑，新的默认逻辑为，如果设定了数据权限的表本身继承了BasePoco，而且没有给当前用户配置任何数据权限，那么用户默认可以看到自己加的数据。如果给用户配置了数据权限，则根据数据权限的配置显示数据，不管是否是当前用户添加的。
* **修改：**  修改了layui transfer控件绑定数据的bug
* **修改：**  修改了layui多选控件设置必填的bug

##5.7.1 (2021-12-8) 
* **修改：**  还原了数据权限的默认逻辑
* **修改：**  修复了批量修改时将某些字段清空的bug
* **修改：**  修复了TreeContainer联动列表的bug
* **修改：**  修复了更新时因字段大小写引发的验证失败的bug

##5.7.0 (2021-12-2) 
* **修改：**  添加了BoolStringConverter，用于将字符串序列化为布尔值
* **修改：**  修复了导入时唯一性条件的判断
* **修改：**  现在用户缓存里会写入TenantCode，方便扩展多租户
* **修改：**  修复了layui控件中关于默认值的逻辑，现在绑定字段没有值的情况下才会使用默认值
* **修改：**  修复了子表控件在最大化弹出窗口中显示不完全的问题
* **修改：**  修复了全局错误处理会引发其他http错误的问题
* **修改：**  修复了某些情况下文件无法删除的问题
* **修改：**  其他配合WtmPlus的修改

##5.6.3 (2021-10-29) 
* **修改：**  修复了上个版本引发的权限配置搜索的bug
* **修改：**  修复了自带的代码生成器中生成BatchVM中的日期类型字段的错误

##5.6.1 (2021-10-27) 
* **修改：**  修复了上个版本引发的Selector报错的问题
* **修改：**  修复了Blazor模式下删除文件的bug

##5.6.0 (2021-10-26) 
* **修改：**  修复了数据权限读取的问题
* **修改：**  修复了layui模式下英文界面搜索后列表分页栏显示中文的问题
* **修改：**  修复了layui模式下修改了刷新列表会回到第一页的问题
* **修改：**  修复了批量导入时有的字段不识别的问题
* **修改：**  修复了公共页面配置无效的问题
* **修改：**  修复了Blazor模式下js加载的问题

##5.5.5 (2021-10-3) 
* **修改：**  修复了图表显示的一些问题
* **修改：**  修复了WtmContext中引用HttpContext造成非web项目使用报错的问题
* **修改：**  默认的Cors添加了Content-Disposition头
* **修改：**  修复了Blazor文件下载url错误的问题
* **修改：**  修复了Layui模式编辑菜单目录报错的问题
* **修改：**  修复了Layui Transfer控件默认值的bug
* **修改：**  修复了导出Excel时枚举描述为数字时产生的bug

##5.5.0 (2021-9-16) 
* **修改：**  升级自带的BootStrapBlazor到5.10.8版本，修复了wasm模式下刷新页面报错的问题
* **修改：**  修复了Blazor编写公开页面的问题
* **修改：**  修复了layui模式中一些控件的高度问题
* **修改：**  修复了layui模式环状图表会显示坐标的问题
* **修改：**  修改了Blazor模式中导入的默认代码
* **修改：**  修改了layui模式中Selector单选不能清空的问题

##5.4.9 (2021-9-12) 
* **修改：**  修复<wt:radio>控件绑定布尔值的bug
* **修改：**  新增生成测试数据的方法
* **修改：**  修复了Blazor模式公开页面的bug

##5.4.8 (2021-9-10) 
* **修改：**  Layui模式下SearchPanel可以通过设置ChartId来对图表进行数据搜索
* **修改：**  修复了在菜单管理中设置页面公开无效的问题
* **修改：**  修复了Blazor模式中导出有时报错的问题
* **修改：**  修复了默认的字符过滤逻辑，防止xss攻击

##5.4.7 (2021-9-4) 
* **修改：**  修复了Layui下图表在Tab页和弹出窗口中刷新的问题
* **修改：**  修复了Taghelper组合style有可能无效的问题

##5.4.6 (2021-9-3) 
* **新增：**  受益于WtmPlus的需求，Layui新增了图表控件，支持柱状，饼图，环状，折线和散点，以及多种主题。具体请参考文档。
* **修改：**  Layui默认登陆页面统一成React，Vue，Blazor相同的样式
* **修改：**  移除了Layui自带的echartjs，使用echart官方的版本
* **修改：**  修复了Layui模式下Radio和CheckBox绑定布尔值无效的bug
* **修改：**  修改了自带代码生成器生成单元测试的逻辑
* **修改：**  修复了偶发登录后又跳出的问题
* **修改：**  修复了Layui中维护菜单时添加api报错的问题


##5.4.5 (2021-8-27) 
* **修改：**  修复了Layui模式下上传图片控件异步加载的问题
* **修改：**  修复了Layui模式下ListVM中使用SetFixed方法会导致内部按钮不能点击的bug
* **修改：**  修改了框架默认的生成缩略图的逻辑
* **修改：**  Blazor模式下搜索框默认的打开状态现在可以正确读取appsettings里的全局设置
* **修改：**  修复了使用低版本IE无法登录的问题

##5.4.4 (2021-8-25) 
* **修改：**  增强了Layui模式下图片上传控件的预览效果，感谢Rea同学的代码
* **修改：**  修复Layui模式下可能出现的联动的问题
* **修改：**  Layui模式下现在可以在combobox,radio,checkbox 和 transfer控件上使用 item-url 来替代items实现类似前后端分离的获取初始数据的效果
* **修改：**  AddWtmSwagger方法现在可以设置一个参数来指定是否使用全类名
* **修改：**  更新了框架对.netcore的相关类库依赖到5.0.9
* **修改：**  自带的代码生成器会提示使用WtmPlus可以提高工作效率，哈哈。

##5.3.8 (2021-8-11) 
* **修改：**  修复GetTreeSelectListItems方法数据权限过滤的bug
* **修改：**  修复LayUI模式下联动的问题
* **修改：**  修复Blazor模式下代码生成器的一些问题

##5.3.5 (2021-8-3) 
* **HotFix：**  修复Blazor代码生成器

##5.3.4 (2021-8-3) 
* **修改：**  优化了layui模式中联动的功能，多选下拉菜单现在不受联动的影响
* **修改：**  Blazor模式更新到BootStrap Blazor控件库的最新版本
* **修改：**  修复了主子表同时修改的一些bug

##5.3.2 (2021-7-26) 
* **修改：**  修复了主子表导入的问题
* **修改：**  修复了用户组中设置数据权限的bug
* **修改：**  为配合WtmPlus的发布，layui模式中的upload和multiupload现在可以点击预览图看大图，并支持disabled模式
* **修改：**  默认生成的代码中RefreshToken方法不再需要登陆

##5.3.1 (2021-7-18) 
* **hotfix：**  修复了上一个版本引发的Selector不能正确显示的bug

##5.3.0 (2021-7-18) 
* **修改：**  修复了有些地址权限控制无法识别的bug
* **修改：**  默认的用户加载现在会读取用户名和头像
* **修改：**  修复了一些EF生成的语句不支持sqlite的问题
* **修改：**  日志现在可以根据当前的数据库连接字符串记录到不同的数据库里
* **修改：**  Blazor模式中配置外部链接现在可以正常显示
* **修改：**  Blazor模式中在页面上添加 @layout EmptyLayout 和 @attribute [Public] 属性，可以制作不受权限控制的独立的页面

##5.2.8 (2021-7-12) 
* **修改：**  修复了layui模式下在弹出窗口刷新grid的问题
* **修改：**  修复了Blazor模式下关联主表后提交表单失败的问题
* **修改：**  修复了Vue模式下导出错误的问题

##5.2.7 (2021-7-4) 
* **修改：**  修复了layui模式下表单错误的显示位置问题
* **修改：**  修复了初始化默认数据有可能报错的问题
* **修改：**  TokenService现在会默认使用名为tokendefault的连接字符串，便于将token相关表保存在其他数据库或内存中

##5.2.6 (2021-6-27) 
* **修改：**  修复了layui模式下设置组件style和class有时无效的问题
* **修改：**  Layui模式下添加了wt:card组件
* **修改：**  修复了Wtm中的Cache依赖HttpContext的问题

##5.2.5 (2021-6-20) 
* **修改：**  修复了layui模式下slider控件无法提交的bug
* **修改：**  修复了GetSelectListItems and GetTreeSelectListItems 方法在sqlite下有可能引发错误的bug

##5.2.4 (2021-6-9) 
* **修改：**  修复了Layui的Selector读取数据的bug
* **修改：**  修复了Layui的Display没有自动换行的问题
* **修改：**  修改了Blazor项目默认代码，更新了BB版本，并修复了访问不存在的地址会报错的问题

##5.2.3 (2021-6-1) 
* **修改：**  修复了项目默认代码Blazor模式中配置角色页面权限的bug
* **修改：**  修复了项目默认代码删除用户时没有同时删除角色及用户组关联数据的bug
* **修改：**  修复了项目默认代码LoginJwt方法对密码进行了小写操作的bug
* **修改：**  修复了Layui模式中Selector控件只能绑定id值的bug
* **修改：**  默认生成的Blazor项目更改为server模式

##5.2.2 (2021-5-24) 
* **修改：**  修复了代码生成器生成单元测试的一些bug
* **修改：**  优化了DC.RunSql函数，现在可以在事务中间使用DC.RunSql
* **修改：**  更新了默认项目中的FrameworkUserVM，使其在修改和删除用户时刷新用户权限缓存。
* **修改：**  更新了Blazor项目默认代码，修复了修改密码无法找到Api的问题，修复了wasm模式中必须指定后台地址的问题
* **修改：**  修复了Layui模式中Selecor设置disable无效的问题
* **修改：**  Layui模式中，子表Grid单元格编辑时，现在可以设置SetEditType(EditTypeEnum.Datetime)来支持日期选择
* **修改：**  优化了数据唯一性的查询语句，修复了mysql中生成的语句报错的问题

##5.2.1 (2021-5-15) 
* **修改：**  修复了代码生成器生成中间表相关ViewModel和页面错误的bug
* **修改：**  修复了BaseCrudVM中默认修改方法没有过滤掉[NotMapped]字段的错误

##5.2.0 (2021-5-14) 
* **新增：**  新增对Blazor的支持，现在可以在官网生成Blazor模式的项目，代码生成器现在也可以生成Blazor的代码，具体请见文档http://wtmdoc.walkingtec.cn/#/Blazor/Intro
* **新增：**  [ActionDescription]中加入了IsPage属性，设置了这个属性的方法可以在菜单管理中添加，解决了一个Controller下只能配置一个主页面的问题
* **修改：**  修改了代码生成器的一些内部实现
* **修改：**  修复了默认代码中非管理员设置数据权限出现的错误
* **修改：**  修复了DynamicDataConverter读取数据报错的问题
* **修改：**  修复了在配置文件中将jwt的SecurityKey设置的过短会报错的问题
* **修改：**  修复了Layui模式下，搜索框折叠后下方列表错位的问题

##5.1.9 (2021-4-30) 
* **修改：**  移除了默认的 wt:grid onedit 方法重载，用户可以自己写js来实现功能
* **修改：**  修复了OssFileHandler读取文件时没有正确使用groupname的问题
* **修改：**  修复了layui模式下selector没有正确读取[FixedConnection]中指定DC的问题
* **修改：**  修改了BaseCrunVM中的DoEdit方法中主子表同时开启事务修改数据会报错的问题
* **修改：**  修改了api提交多层嵌套的json数据，有时没有正确验证的问题
* **突变：**  修改了读取appsettings文件的问题，老用户可以在线生成新项目，替换默认的program和startup文件

##5.1.7 (2021-4-19) 
* **修改：**  修复了json同时提交主子表数据不能自动更新子表的bug
* **修改：**  修复了更新失败时没有返回错误信息的bug

##5.1.6 (2021-4-18) 
* **修改：**  默认Json序列化加入了JsonNumberHandling.WriteAsString
* **修改：**  默认Json序列化加入了自定义日期，去掉了默认日期和时间之间的那个T
* **修改：**  修复了导入时IsOverWriteExistData=false的时候没有验证重复数据的bug
* **修改：**  修复了导入时已经删除的PersistPoco会被更新的bug

##5.1.4 (2021-4-11) 
* **修改：**  修复了数据权限指向自定义ID表时无法设置的bug
* **修改：**  现在生成的新项目使用微软默认的加载config文件的方式
* **修改：**  appsettings文件中的CookieOptions里面可以指定"Domain"，比如"Domain":".abc.com",可以允许所有abc.com的二级域名设置cookie，解决前后台分别部署在不同域名设置cookie无效的问题。
* **修改：**  修复了重启服务获取不到userid的问题
* **修改：**  ISubFile中的order字段改为Order，纯粹是强迫症，老项目可以批量替换一下，影响不大
* **修改：**  修复了React模式下数据权限报错的问题
* **修改：**  修复了获取中文文件名的一些问题

##5.1.2 (2021-4-6) 
* **修改：**  新生成的项目加入了默认的favicon文件
* **修改：**  新生成的Layui项目更新至layui至2.6.3
* **修改：**  新生成的Vue项目修复了设置默认搜索条件不生效的问题
* **修改：**  修复了预览pdf文件时不显示的问题
* **修改：**  更新了BaseImportVM中的报错逻辑
* **修改：**  TreePoco中加入了HasChildren字段

对于在线新建项目时生成的默认文件，更新时可以生成一个新项目，并将生成的默认文件覆盖老项目

##5.1.1 (2021-3-28) 
* **修改：**  wt:image现在也可以显示label，默认不显示，通过设置hide-label=false来显示label
* **修改：**  ListVM种和wt:display中显示时间时，使用yyyy-MM-dd或者yyyy-MM-dd HH:mm:ss作为默认格式
* **修改：**  修复了wt:tree显示上的一些bug
* **修改：**  修复了代码生成器生成多对多假删除时的逻辑

##5.1.0 (2021-3-22) 
* **修改：**  修复了wtm.CreateDC时，connectionstring名称只能认小写字母的bug
* **修改：**  修复了_framework/getfile方法有时不能显示附件的bug
* **修改：**  代码生成时加入了一些模型字段验证，可以更快的发现模型设计的问题
* **修改：**  修复了代码生成器生成单元测试的一些bug

##5.0.9 (2021-3-14) 
* **修改：**  现在导入的模板会优先从ImportVM中字段的[DisplayName]上读取文字作为模板的列名
* **修改：**  修复在事务中操作附件造成阻塞的bug
* **新增：**  新增DynamicData类，在DC.RunSql<>和wtm.CallApi<>以及其他自定义方法中都可以使用DynamicData作为泛型，框架会自动序列化这种类型的变量。当我们懒得为了返回格式化的json定义新类的时候，可以使用这个类。
* **修改：**  修复了导入时唯一性验证的bug
* **修改：**  修复了导入时必填验证的bug
* **修改：**  修复了layui模式下在空间中指定Required无效的问题
* **修改：**  修复了layui模式下Combobox在禁用状态下指定emptytext无效的问题
* **修改：**  修复了layui模式下多个selector冲突的问题
* **修改：**  修复了layui模式下对于列名相同的不同ListVM设置背景色冲突的问题
* **修改：**  修复了默认项目生成的一些基础代码

##5.0.8 (2021-3-6) 
* **修改：**  现在附件保存在本地时，如果配置的是相对路径，数据库中也会记录相对路径，便于迁移
* **新增：**  修复了导出枚举字段没有正确显示文字的问题
* **修改：**  修复了layui模式中FF.RefreshPage()不能正确刷新页面的问题
* **修改：**  修复了layui模式中登陆过期后，重新登陆跳转到原有页面地址错误的问题。（老项目可以从官网重新生成新项目，然后替换HomeVms中的LoginVM文件）
* **修改：**  修改了默认生成的单元测试项目中的MockUtility文件，可测试ModelState中保存的内容

##5.0.7 (2021-2-27) 
* **修改：**  修复React修改当前用户密码失败的bug
* **新增：**  修复了React登录失败时的文字提示
* **修改：**  修复了代码生成器对于非Guid主键的关联表也会生成new Guid()语句的bug
* **修改：**  修复了有的空菜单目录没有隐藏的bug
* **修改：**  修复了Layui中对enum字段使用SetFormat不生效的bug
* **修改：**  修复了Layui中对指定NeedPage=false的不分页的列表，再次点击搜索还是会分页的bug
* **修改：**  修复了excel导入时没有验证模型字段的bug

##5.0.6 (2021-2-21) 
* **修改：**  修复了ListVM处理枚举列的一个小bug
* **新增：**  AddWtmMultiLanguages函数现在可以指定一个option，用来指定自定义的多语言文件
* **修改：**  现在默认允许集合结尾多写逗号的json格式
* **修改：**  修复了React模式下switch控件不选择无法提交的bug
* **修改：**  oracle终于支持.net5了，更新了对他的引用，现在不会再报警告了
* **突变：**  移除了默认项目中引用的Microsoft.EntityFrameworkCore.Tools和Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation，因为他们已经在wtm的dll中引用了。5.0以上的项目可以手动从依赖中删除这两个包。

##5.0.4 (2021-2-7)
* **HotFix：**  紧急修复5.0.3版本在各层分离的多项目模式下无法登录的bug

##5.0.3 (2021-2-7)
* **突变：**  用了新的方式修复了用户表不能自定义主键的问题。老版本更新后请手动将原始代码中的FrameworkUesrBase的引用统一更新成FrameworkUser，或者下载新项目把公共部分copy过去
* **突变：**  wtm.CallApi方法重构，增加了返回code，错误信息等字段。默认文件中HomeController中的GetGithubInfo方法使用了CallApi，老版本请手动将返回值修改为返回CallApi的结果.Data
* **新增：**  Layui的文件预览现在可以播放mp4的附件
* **新增：**  ImportVM中的GenerateTemplate现在可以重写，通过重写该方法可以指定上传模板的文件名
* **修改：**  修复了api中Json序列化引起的循环引用的错误

##5.0.2 (2021-1-31)
* **新增：**  新增IBasePoco和IPersistPoco两个接口，提升定义模型的灵活性。比如继承了TreePoco的树形模型如果同时实现IBasePoco框架也会自动设置添加人，添加事件，修改人，修改时间。详情请查阅文档中模型定义的部分。
* **新增：**  支持发布为单一文件
* **修改：**  修复了<wt:switch>为false时没有正确提交的bug
* **修改：**  修复了删除主表有时没有正确删除附件子表中的文件的bug
* **修改：**  修复了<wt:selector>中combobox赋值的bug
* **修改：**  现在api默认也会过滤<>
* **修改：**  修复了某些情况下上传大文件会被限制的bug
* **修改：**  修复了VUE使用element控件多次刷新界面的bug

##5.0.1 (2021-1-23)
* **修改：**  修复了<wt:Transfer>控件绑定字符串数组的bug
* **修改：**  修改了<wt:MultiUpload>控件，使其正确的删除文件
* **修改：**  修改了<wt:Combobox>控件默认值的bug
* **修改：**  修复了代码生成器生成单元测试代码的问题
* **修改：**  修复了代码生成器生成修改页面时默认文字的多语言错误
* **修改：**  新生成的项目自带的layui版本升级到2.5.7
* **修改：**  新生成的项目Admin模块中增加了用户批量修改角色的代码

##5.0.0 (2021-1-17)
* **新增：**  全面支持.net 5.0
* **新增：**  全新的WtmContext类
* **新增：**  针对性能做了大幅代码优化，访问速度肉眼可见的提高
* **新增：**  重构了文件上传和下载的功能，内置支持阿里云OSS
* **修改：**  移除了对NewtonJson的引用，使用微软默认的System.Text.Json
* **修改：**  Startup文件回归
* **修改：**  内置管理模块的代码直接包含在项目中
* **修改：**  将FrameworkUser用户表提取出来直接生成在项目中，便于大家扩展
* **修改：**  修改了内置一些数据库表结构，为后续功能扩展做好准备

## v3.x.x （2.x.x同步更新）

##3.8.0 以及 2.8.0 (2020-12-27)
* **新增：**  增加了CallApiStream方法用户调用返回二进制数组的api
* **修改：**  修复了枚举类型在多表头列表导出时不显示的问题
* **修改：**  修复了SubmitButton上添加ComformText不提交的问题
* **修改：**  修复了ImportVM中SetDuplicateCheck方法的bug，框架现在会优先使用ImportVM中的SetDuplicateCheck方法
* **修改：**  PersistPoco中IsValid字段默认值改为true
* **修改：**  修复了按钮无法指定layui-btn-normal样式的问题

##3.7.9 以及 2.7.9 (2020-11-29)
* **修改：**  修复layui模式下搜索没有重置页码的bug
* **修改：**  修复DpWhere方法在某些情况下报错的bug

##3.7.8 以及 2.7.8 (2020-11-24)
* **修改：**  彻底修复GetProperty反射与自定义主键引发的一些冲突


##3.7.7 以及 2.7.7 (2020-11-21)
* **修改：**  修复了DpWhere方法的一些bug
* **修改：**  Layui默认的数据权限管理添加了搜索功能
* **修改：**  <wt:grid>增加了auto-search属性，用来控制是否自动进行搜索，默认是true
* **修改：**  优化了一些框架自动生成的表达式语句
* **修改：**  将内部引用的.netcore mvc和ef相关的包升级到了最新版

##3.7.6 以及 2.7.6 (2020-11-15)
* **修改：**  TreeContainer现在可以设置默认选中的项目，通过设置TreeSelectListItem中的Selected属性
* **修改：**  修复了数据权限在过滤自定义主键的模型时的bug
* **修改：**  修复若干小问题

##3.7.5 以及 2.7.5 (2020-10-19)
* **修改：**  Layui模式下Selector控件也可以使用trigger-url和link-field来联动其他控件了（鸣谢AaronLucas)
* **修改：**  修复了多表头列表导出excel时表头显示错误的问题
* **修改：**  修复VUE左侧菜单收缩后不显示图标的问题

##3.7.4 以及 2.7.4 (2020-9-22)
* **修改：**  修复Layui模式下多选Combobx联动的问题
* **修改：**  优化CheckBetween方法所生成的sql语句
* **修改：**  修复ListVM中以数组作为列生成的json错误的问题
* **修改：**  修复了使用保存的cookie在登出后仍然可以访问系统的问题

##3.7.3 以及 2.7.3 (2020-9-12)
* **修改：**  修复Layui模式下数据权限管理页面的bug
* **修改：**  修复Layui模式下用户管理无法取消角色和用户组的bug
* **修改：**  修复<wt:tree>控件无法取消选择的bug
* **修改：**  修复链接多个数据库时有一个无法连接就会导致系统无法启动的问题
* **修改：**  修复ListVM在对PersistPoco搜索时如果数据源不是PersistPoco报错的问题

##3.7.2 以及 2.7.2 (2020-9-7)
* **修改：**  修改默认跨域逻辑，不配置跨域信息会默认允许所有跨域
* **修改：**  修复RefreshToken方法在pgsql中报错的问题
* **修改：**  修复菜单管理模块显示的bug，以及修改菜单会造成其他角色都有权限的bug
* **修改：**  修复多表头导出时表头显示的问题
* **修改：**  修改导入逻辑，默认不启用sqlserver的bulkcopy，可以通过在ImportVM中设置UseBulkSave属性来启用

##3.7.1 以及 2.7.1 (2020-8-26)
* **新增：**  Layui模式下新增<wt:colorpicker>颜色选择器，参考文档 https://wtmdoc.walkingtec.cn/#/UI/ColorPicker
* **修改：**  修复<wt:checkbox>多选显示的bug
* **修改：**  修复了一些layui模式下自带的系统管理页面
* **修改：**  修复了导出excel时数字格式为null值时报错的问题
* **修改：**  修复了FixConnectionAttribute没有优先检查方法的问题
* **修改：**  修复了IssueTokenAsync方法并发时报错的问题

##3.7.0 以及 2.7.0 (2020-8-18)

* **修改：**  导出excel时，如果ListVM中设置了多表头，导出的excel也会显示多表头
* **修改：**  修复了3.x版本中ListVM调用存储过程报错的问题，并更新了相关文档
* **修改：**  修复了layui下SearchPanel的重置按钮没有正确重置表单的问题
* **修改：**  修复了layui下多表头的列表显示错位的问题
* **修改：**  新生成的3.x的项目添加了RuntimeCompilation，并默认设置为调试模式下Razor页面可以动态编译
* **修改：**  修复VUE和React模式中菜单管理设置为不显示菜单时仍然显示的错误
* **修改：**  修复VUE和React模式中获取和显示文件的接口不受IsFilePublic属性控制的问题

##3.6.9 以及 2.6.9 (2020-7-23)
* **修改：**  修改了PersistPoco的假删除逻辑
* **修改：**  修复了layui下使用TreeContainer搜索，搜索条件会消失的bug
* **修改：**  导出excel时，如果时数字格式，现在会自动把excel的列设置成数值格式，方便再加工
* **修改：**  修复了使用代码生成器生成api时会生成一些无用代码的bug
* **修改：**  layui下<wt:grid>增加了line-height属性，可以指定行高，适合列表显示图片之类的情况使用
* **修改：**  ListVM中DateTime类型的字段默认格式改回yyyy-MM-dd hh:mm:ss
* **修改：**  修复了admin中修改用户密码，输入过长时显示的bug
* **修改：**  修复VUE中代码生成列表搜索框默认控件数量不对的bug
* **修改：**  修复VUE中列表没有充满屏幕的bug

##3.6.8 以及 2.6.8 (2020-7-6)
* **修改：**  增强了对oracle的支持，注：3.x版本的oracle仍然是beta版，可跑起框架，但可能有未知问题
* **修改：**  修复了layui搜索相关的一些bug
* **修改：**  代码生成器现在对于bool类型的变量在layui下默认使用<wt:switch>控件
* **修改：**  ListVM中DateTime类型的字段默认使用yyyy-MM-dd的格式
* **修改：**  appsettings中的UploadLimit修改为long类型，可是指定更大的数字
* **修改：**  修复VUE中没有角色的用户登录时重复刷新的bug
* **修改：**  修复VUE中列表没有充满屏幕的bug

##3.6.7 以及 2.6.7 (2020-6-30)
3.6.6/2.6.6 热更新

##3.6.6 以及 2.6.6 (2020-6-30)
* **修改：**  修复sqlserver bulk导入时枚举类型没有正确赋值的问题
* **修改：**  Layui修复了Combobox，CheckBox，Radio等控件设定default-value无效的问题
* **修改：**  Layui修复了Combobox禁用无效的问题
* **修改：**  Layui修复了取消某个搜索条件仍然按之前条件搜索的问题
* **修改：**  Layui修复了按钮组的显示问题
* **修改：**  VUE修复了编辑后再添加id重复的问题
* **修改：**  VUE修复了数据权限管理页面错误的问题
* **修改：**  VUE和React修复了菜单有目录的情况下排序的问题
* **修改：**  修改了一些多语言英文文本

##3.6.5 以及 2.6.5 (2020-6-15)
* **修改：**  修复操作列和菜单的多语言问题
* **修改：**  修复一对多删除时某些情况下失败的问题
* **修改：**  修复代码生成器生成的api单元测试报错的问题

##3.6.4 以及 2.6.4 (2020-6-3)
* **修改：**  修复了默认初始化数据找不到Action报错的问题
* **修改：**  修复了代码生成器在关联多个外键的同名字段时，生成的列表显示错误的问题
* **修改：**  修复了权限认证时没有正确处理Async方法的问题
* **修改：**  修复了LayUI模式下SetBindVisiableColName失效的问题
* **修改：**  修复了VUE模式下菜单模块的多语言显示错误的问题
* **修改：**  修复了React模式下一些文字错误

##3.6.3 以及 2.6.3 (2020-5-26)
* **修改：**  修复了上一版本引发的搜索报错的问题
* **修改：**  修复了bulk导入的一个小bug
* **修改：**  代码生成器生成页面时加入了多语言，老项目请在_ViewImports.cshtml文件中加入一行 @using Microsoft.Extensions.Localization;

##3.6.2 以及 2.6.2 (2020-5-25)
* **修改：**  修复了包含自定义列名的模型导入失败的bug
* **修改：**  修复了Index页面没有正确判断页面权限的bug
* **修改：**  现在Searcher也可以写Validate方法，查询条件后台返回的错误可以正确显示
* **修改：**  修复了控制台Log没有显示时间的bug
* **修改：**  修复了React和Vue配置页面权限的文字错误

##3.6.1 以及 2.6.1 (2020-5-22)
3.6.0/2.6.0 的热更新，修复代码生成器生成Controller的一个bug

##3.6.0 以及 2.6.0 (2020-5-22)
* **新增：**  导出优化，支持xlsx格式，单个excel文件现在最大可导出100万行，可设置单个文件最大行数，超过最大行数时会自动下载包含多个excel文件的zip包。详情请参见文档https://wtmdoc.walkingtec.cn/#/VM/Export
* **新增：**  导入优化，支持xlsx格式，支持公式，使用sqlserver时自动使用bulk导入，提高大批量数据的导入速度。
* **新增：**  ListVM中的MakeGridHeader方法现在可以正确绑定任何其定义lambda表达式
* **新增：**  修复有关联关系的数据无法正常删除的bug
* **修改：**  Layui模式中所有按钮的TagHelper现在都可以指定confirm-text来弹出一个询问框
* **修改：**  Layui模式修复默认下载按钮失效的bug
* **修改：**  Layui模式修复Display TagHelper绑定附件时显示错误的bug
* **修改：**  React模式修复代码生成器生成一对多控件时的问题
* **修改：**  Vue模式增加多语言支持
* **修改：**  VUE模式修复一些近期反馈的小bug



##3.5.7 以及 2.5.7 (2020-5-6)
* **新增：**  SubmitButton中新增SubmitUrl属性，用于多个提交按钮提交到不同的地址
* **新增：**  BaseController和BaseApiController增加可重写的GetLoginUserInfo方法，用于自定义用户认证
* **修改：**  优化认证逻辑，加快响应速度
* **修改：**  修复jwt无效时返回登录界面的错误，现在可以正确返回401，修复jwt token过期时间不准确的问题
* **修改：**  完善多语言支持
* **修改：**  修复DoDelete和SetInclude冲突的bug
* **修改：**  VUE修复菜单空目录bug
* **修改：**  VUE修复权限配置和搜索的bug
* **修改：**  React完善多语言支持

##3.5.6 以及 2.5.5 (2020-4-13)
* **新增：**  ConnectionString配置中可以设置Version字段，用于控制mysql的具体版本
* **修改：**  移除了动态控制器，因为和动态编译页面产生冲突
* **修改：**  IsFilePublic现在可以正常工作
* **修改：**  更新了默认生成的VUE项目代码，修复了一些bug

##3.5.4 以及 2.5.4 (2020-4-3)
* **新增：**  新增了动态控制器，老项目需要手动在Project文件的 \<PropertyGroup\>中加入\<PreserveCompilationReferences>true</PreserveCompilationReferences\>节点

* **修改：**  修复vue代码生成没有正确生成某些api的bug
* **修改：**  修复vue自带系统管理中的一些bug，整体更稳定
* **修改：**  IsFilePublic配置在3.x下可以正常工作
* **修改：**  修复框架自带GetFile和ViewFile方法无法正常调用的bug

##3.5.2 以及 2.5.2 (2020-3-29)
* **修改：**  修复vue代码生成下拉菜单少了一个逗号的bug
* **修改：**  修复vue发布时的问题
* **修改：**  修复vue列表高度计算的问题
* **修改：**  修复vue数据权限列表的删除bug

* **新增：**  Layui. 现在ListVM中的GridAction可以通过SetButtonClass方法设置按钮颜色
* **新增：**  Layui. UIService中新增MakeButton方法替换之前有问题的MakeRedirectButton方法
* **修改：**  修复GetGridActions会被调用两次的问题（这其实是.netcore的bug...)


##3.5.1 以及 2.5.1 (2020-3-26)
* **修改：**  修复vue菜单相关的一些bug
* **修改：**  修复vue代码生成器对于布尔值的控件生成的bug
* **修改：**  修复vue代码生成器对于下拉菜单生成的bug

##3.5.0 以及 2.5.0 发布，你心心念的Vue来了！！！vue目前还属于预览版，欢迎大家多提宝贵意见
* **新增：**  现在官网可以生成Vue的项目了
* **新增：**  VUE项目可以使用和Layui，React相同的代码生成
* **新增：**  appsettings文件中增加了Domains的配置，用来注册httpclient。在Controller和VM中通过ConfigInfo.Domains["key"].CallAPI来方便高效的调用其他网站的api
* **修改：**  修复代码生成器会将bool的搜索条件啊生成两次的bug
* **修改：**  修复继承自TopBasePoco的Model在DoAdd中没有正确的添加子表数据的bug
* **修改：**  修复用户没有权限时没有正确返回401错误的bug

## v3.1.x

3.1版本正式发布，支持.netcore 3.1，与2.4.x最新版本在功能上同步更新


## v2.4.x

v2.4.9(2020-3-15)
* **修改：**  重构日志，使用.netcore默认的日志记录流程和规则。 在.ConfigureLogging中可以使用AddWTMLogger来添加WTM的日志功能，并可以在appsetting文件中配置Logging来指定需要记录日志的级别，就像你操作其他Console，Debug这些日志一样。
* **修改：**  修复layui下日期控件默认显示当前日期的问题
* **修改：**  修复form和其中的searchpanel同时指定label-width会报错的问题
* **修改：**  代码生成现在会默认为DateTime类型的搜索条件生成时间区间的搜索
* **修改：**  修复了jwt认证失败没有正确返回401的问题

v2.4.7(2020-3-9)

* **新增：**  现在Layui模式下列表可以列筛选和打印
* **新增：**  现在ListVM中的Action按钮可以通过SetPromptMessage设置询问对话框
* **新增：**  现在数据权限可以识别多对多和树形结构

* **修改：**  修改了新生成的项目LoginVM和RegVM错位的问题
* **修改：**  修复了设置不分页不起作用的bug
* **修改：**  修改了view强制要求model继承BaseVM的bug
* **修改：**  修复了Combobox在disable状态下的显示问题
* **修改：**  修复了代码生成器在多个DataContext时候的生成问题
* **修改：**  修复了SearchPanel中Combobox多选时提交数据错误的问题

v2.4.6(2020-2-22)
本次更新加入了在连接字符串上指定数据库类型和DataContext的功能，并修复了近一阶段的bug。
* **新增：**  现在在appsettings中的ConnectionStrings里面可以指定每一个连接字符串的DbType和DbContext
* **新增：**  现在新增了一个EmptyContext基类，FrameworkContext会包含框架自带的表，而EmptyContext不会，这对于我们使用WTM连接其他系统的数据库十分有用
* **注意：**  老版本升级时需要在DataContext文件中加入一个新的构造函数：
        public DataContext(CS cs)
             : base(cs)
        {
        }
* **新增：**  增加了NoLog标记，用来指定某个方法不记录系统日志
* **修改：**  移除了不必要的验证，提升webapi的响应速度
* **新增：**  Layui模式下登陆页面新增了用户注册的演示页面
* **修改：**  修复了layui模式下autocomplete textbox在有初始值时的js错误
* **修改：**  修复了layui模式下可编辑grid表头错位的bug
* **修改：**  修复了PIndex页面的js错误
* **修改：**  移除了React模式下对node-sass的依赖
* **修改：**  移除了React模式下数据权限管理的bug
* **修改：**  修复了WebApi在非调试模式下权限认证的bug
* **修改：**  修复了代码生成器生成React菜单的bug
* **修改：**  修复了代码生成器生成标记了[Range(xxx.Max)]字段的bug






v2.4.5 (2020-1-4)
本次为累积更新，修复了一个月以来issue上提出的主要bug
* **修改：**  修复了获取PersistPoco的下拉选项时，没有过滤IsVaild=false的问题
* **修改：**  修复了弹出窗口在手机上显示不全的问题
* **修改：**  修复了UEditor单图上传错误的问题
* **修改：**  修复当搜索条件只有一个时，在搜索框中按回车键会出现异常页面的问题
* **修改：**  修复了Transfer 穿梭框显示问题的问题
* **修改：**  修复了在form表达外使用ImageTagHelper 会获得一个异常的问题
* **修改：**  修复了textbox加了padding-text之后tab无法切换的问题
* **修改：**  修复了selector display="true"时，显示错误的问题
* **修改：**  修复了使用一些第三方控件导致view无法显示的问题
* **修改：**  修复了代码生成器中点击关闭按钮报错的问题
* **修改：**  修复了连续三级空菜单没有隐藏的问题
* **修改：**  React模式系统自带管理模块加入了中英文多语言

* **新增：**  upload控件增加了进度条，通过设置ShowProgress可以选择是否显示（鸣谢 ‘阿拉斯没有家’同学 https://github.com/buffonlwx）
* **新增：**  ListVM中的GridAction中增加了下载类型的按钮
* **新增：**  BaseController中的CreateDC方法现在可以使用连接字符串的key，而不需要写死整个连接字符串
* **新增：**  BaseController中的CreateDC方法现在可以指定数据库类型
* **新增：**  菜单维护时外部菜单可以使用/aaa/bbb的形式来指定一个内部地址，这样方便大家把一个具体方法配置到左侧菜单上

### v2.4.3 / v3.0.4 (2019-12-8)

* **新增：**  增加多附件上传控件，特别鸣谢‘草监牛寺’同学，参见文档 https://wtmdoc.walkingtec.cn/#/UI/UploadMulti
* **修改：**  Appsettings文件中增加了IsOldSqlServer配置，对于使用sqlserver 2008以前的用户使用
* **修改：**  修复某些模型生成单元测试时的bug
* **修改：**  修复自定义ID的模型attach时可能失败的bug
* **修改：**  修复主子表操作时没有判断PersistPoco的bug


### v2.4.2 (2019-11-22)

* **修改：**  修复Add-Migration报错的问题
* **修改：**  修复刷新菜单无效的问题
* **修改：**  修复无法添加多级菜单的问题
* **修改：**  修复在导出时SearchMode仍然为Search的问题
* **修改：**  修复权限控制无法识别中文url的问题

### v2.4.1 (2019-11-16)

* **修改：**  修复同时使用Cookie和jwt登陆时报错的bug（不建议混合两种模式）

#### 前后端不分离模式
* **修改：**  修复Combobox联动由于没有图表而报错的bug

#### React前后端分离模式
* **新增：**  多语言支持
* **修改：**  修复菜单地址bug
* **修改：**  修复菜单管理，数去权限管理页面问题

### v2.4.0 (2019-11-5)
本次更新为大版本更新，废弃了之前Session的模式，使用Jwt和cookie两种方式进行登陆认证。
框架目前支持Cookie和Jwt两种模式，继承BaseController和BaseApiController的控制器将默认支持Cookie模式。
已有使用session认证的代码不需要修改，用户使用过程中并不会感觉到变化。
用户可以通过[AuthorizeCookie],[AuthorizeJwt],[AuthorizeJwtWithCookie]三种标签来指定Controller的验证方式。
详情请参考https://wtmdoc.walkingtec.cn/#/Global/jwt
系统增加了persistedgrants表来存储jwt持久化信息，另外菜单的默认数据也发生了改变，建议已有系统重新生成数据库或手动同步数据库

* **新增：**  Jwt支持
* **新增：**  Swagger jwt支持
* **修改：**  修复多语言验证信息bug
* **修改：**  菜单管理支持不同Area下同名Controller的配置


## v2.3.x

### v2.3.9 (2019-10-19)

* **新增：**  多语言支持。https://wtmdoc.walkingtec.cn/#/Global/MultiLanguages
老版本升级后会遇到单元测试项目中MockController.cs文件报错，将报错的行替换为
_controller.GlobaInfo.SetModuleGetFunc(() => new List\<FrameworkModule\>());
即可。

* **新增：**  dotnet 3.0支持，线上新建项目时可选择dotnetcore3.0版本的项目

#### 前后端不分离模式
* **新增：**  集成了UEditor。https://wtmdoc.walkingtec.cn/#/UI/UEditor
* **新增：**  列表按钮现在可以设置Max属性，来控制打开窗体时最大化
* **修改：**  现在View页面不再强制要求Model必须继承BaseVM
* **修改：**  修改菜单无法删除的历史遗留bug

### v2.3.6 (2019-9-27)

* **新增：**  Debug模式下，debug窗口会输出ef执行的sql语句
* **修改：**  移除EnableCors属性，集成dotnetcore自带的Cors实现跨域，并可在appsettings文件中进行配置

#### 前后端不分离模式
* **修改：**  代码生成器会为Controller生成独立的搜索和导出方法，方便对搜索和导出进行权限控制，之前公共方法仍然保留
* **修改：**  修复IE11下的显示问题
* **修改：**  修复PersistPoco导入时没有给IsValid赋值的问题
* **修改：**  修复SearchPanel中显示树形列表的问题

### v2.3.5 (2019-9-19)

本次更新增加了自定主键功能，除了默认的guid主键外，框架现在还支持自增整形和string类型的主键。
同时代码生成器也可以准确识别主键类型，生成对应的代码。
具体使用方式参见文档 https://wtmdoc.walkingtec.cn/#/Model/CustomKey

由于主键不一定是guid了，老项目更新的时候需要手动修改之前的文件，主要是两部分：
1. Controller里 BatchEdit，BatchDelete中的ids参数由guid[] 变为 string[]
2. batchvm中的CheckIfCanDelete方法，第一个参数由guid变为object
3. 老数据库中DataPrivileges表RelatedId字段类型由Guid变为Nvarchar
改起来还是比较简单的

#### 前后端不分离模式
* **修改：**  修复layui模式下三级菜单无法显示的bug
* **修改：**  修复selector控件不能搜索，不初始化的bug

#### React前后端分离模式
* **新增：**  增加菜单对字体图标的支持
* **修改：**  修复react模式下三级菜单无法显示的bug

### v2.3.4 (2019-9-5)

#### 前后端不分离模式
* **新增：**  现在Layui模式可以直接用代码生成器生成api
* **新增：**  现在Layui模式的菜单管理也可以配置api的权限，包括框架自带的api
* **新增：**  新建layui项目时自动添加swagger的支持，可以查看api文档
* **修改：**  修复grid排序时搜索条件不起作用的bug

#### React前后端分离模式
* **新增：**  新增Tab页关闭其他，关闭当前，关闭所有的功能

### v2.3.3 (2019-9-3)

* **新增：**  新增对Oracle的支持（鸣谢：hd2y）Oracle database version 18c is required.
* **新增：**  新增对DC使用事务的支持（鸣谢：AaronLucas）
* **修改：**  更新了默认数据的添加逻辑，现在使用EF的migration不会担心没有初始数据了
* **修改：**  新生成的项目会在DataContext.cs中自动加入IDesignTimeDbContextFactory类，方便使用Migration

#### 前后端不分离模式
* **修改：**  现在首页默认不展开菜单
* **修改：**  直接访问具体url可以准确定位左侧菜单
* **修改：**  更新默认生成的Login单元测试
* **修改：**  修复批量修改的一些bug
* **修改：**  修复维护菜单会改变菜单id的bug

#### React前后端分离模式
* **修改：**  修复grid中分组之后排序的bug
* **修改：**  修复grid中有时出现横向滚动条的bug
* **修改：**  新登陆页面


### v2.3.1 (2019-8-31)
修复了2.3.0版本中的一些bug
* **修改：**  修复菜单图标显示的问题
* **修改：**  修复主子表修改报错的问题
* **修改：**  修复Selector在IIS下无法显示的问题

### v2.3.0 (2019-8-30)

本次更新是一个大版本的更新，彻底重构了不分离模式的前端UI，大家可以愉快且免费的使用LayuiAdmin了

老项目更新说明：
本次更新全面支持了图标字体，放弃了使用图片附件作为菜单图标。FrameworkMenu表去掉了IconId和CustomIcon字段，新加入了字符串格式的Icon字段
老用户最快的升级方法是线上生成一个同名的新LayUI的项目，然后把你的model，viewmodel，controller考过去。。。

#### 前后端不分离模式
* **新增：**  LayUI升级为LayUIAdmin
* **新增：**  添加对图标字体的支持
* **新增：**  更新默认登录页
* **新增：**  新增Tree和TreeContainer控件
* **新增：**  新增Combobox和DateTime控件的默认配置（鸣谢：AaronLucas）
* **新增：**  新增对Sqlite数据库支持（鸣谢：xuegaoge)
* **修改：**  修复单元格内控件错位的bug
* **修改：**  修复多表头列表导出的bug
* **修改：**  修复LinkButton无法在当前页显示的问题

#### React前后端分离模式
* **修改：**  更新菜单管理模块，支持图标字体
* **修改：**  更新列表显示的逻辑

#### React前后端分离模式
还在路上，预计下个版本会和大家见面~~~

## v2.2.x

### v2.2.52 (2019-8-7)

本次更新主要修复了近期用户反馈的一些bug

#### 前后端不分离模式

* **新增：** GridAction的按钮现在可以通过IsRedirect属性设置在当前页或者Tab页，或者新窗口中显示
* **修改：** 修复layui普通checkbox样式问题
* **修改：** 修复菜单添加报错的bug
* **修改：** 修复多表头显示问题
* **修改：** 修复GridAction中IconCls属性不起作用的问题

#### React前后端分离模式

* **修改：** 修复Area下的api地址解析问题
* **修改：** 修复下拉菜单显示枚举错误的问题
* **修改：** 修复列表中switch现实问题

### v2.2.50 (2019-7-30)

* **修改：** 代码生成器加入对模型基类的验证
* **修改：** 修复日志过长导致截断的bug
* **修改：** 使用新Logo

#### 前后端不分离模式

* **新增：** 新增slider滑块控件
* **新增：** 新增transfer穿梭框控件
* **新增：** 新增对列表汇总行的支持
* **修改：** 修复了绑定字段为数组引起的bug
* **修改：** 修复菜单管理和数据权限管理中历史遗留的bug
* **修改：** 控件的默认id添加vm名称前缀，防止多tab页时出现id重名的控件
* **修改：** 修复checkbox无法触发change-func函数的bug
* **修改：** 使用layui的template重写列表前景色和背景色的实现

#### React前后端分离模式

* **修改：** 优化页面异步加载机制 路由规则调整
* **新增：** 新增aggrid，替代antd自带的grid

### v2.2.48 (2019-7-12)

* **修改：** 修复导入时，同样唯一性数据没有自动更新的bug
* **修改：** 修复批量修改时的验证bug
* **修改：** 修复前后端分离模式菜单排序的bug
* **修改：** 修复layui模式下，searchpanel中oldpost参数无效的bug

### v2.2.47 (2019-7-5)

* **新增：** 新增ValidateFormItemOnly属性，可以加在controller的方法上，用来指示框架只验证表单提交的字段

#### 前后端不分离模式

* **修改：** 修复Admin中更新用户的bug
* **修改：** 修复grid中form控件id的错误

### v2.2.46 (2019-7-3)

* **优化：** 优化ListVM查询速度
* **优化：** 修改ListVM对存储过程的支持，并配套相关文档

#### 前后端不分离模式

* **修改：** 修复grid中使用表单组件的bug
* **修改：** 修复GetFile方法没有正确输入视频content-type的bug

#### React前后端分离模式

* **优化：** 优化配置文件
* **修改：** 修复添加操作没有正确验证model的bug

### v2.2.45 (2019-6-17)

* **新增：** wt:grid 增加MultiLine属性，用于控制是否允许单元格自动换行
* **修改：** 修改默认首页的样式，增加了对于调试模式的提示，以免新用户迷茫

### v2.2.44 (2019-6-14)

* **新增：** 新增自动化单元测试。在线生成项目时会同时生成单元测试项目，代码生成器也会在生成代码的同时为Controller生成单元测试。同时支持分离和不分离两种模式
* **修改：** 修复主子表导入bug，文档中ImportVM中同时加入了主子表导入的范例
* **修改：** 修复默认多字段排序的bug

#### 前后端不分离模式

* **修改：** 修复用户组数据权限修改时的bug

#### React前后端分离模式

* **新增：** 顶部菜单配置，可以在全局设置里将菜单配置成顶部显示
* **新增：** 富文控件中增加文件上传
* **新增：** 增加时间区间控件
* **新增：** 添加了默认的修改密码页面
* **优化：** 优化表格

#### VUE前后端分离模式

VUE目前进展稍慢，距离和大家见面还需要一段时间

### v2.2.42 (2019-5-28)

* **修改：** FrameworkMenu表中加入string类型的CustumIcon字段，已有项目可以手动修改数据库，添加这个字段

#### React前后端分离模式

* **新增：** 菜单维护加入自定义图标，可以设置antd自带图标
* **优化：** 优化编译速度，优化布局

#### 前后端不分离模式

* **新增：** Appsettings中增加TabMode配置，设置layui模式下Tab页样式 ，可选配置有Default和Simple
* **新增：** Appsettings中增加IsFilePublic配置，可以设置附件是否不需要登陆就可以查看和下载

### v2.2.40 (2019-5-18)

* **修改：** 修复枚举导出时不显示的bug

#### 前后端分离模式

* **修改：** 修复React依赖报错的问题

### v2.2.39 (2019-5-10)

* **修改：** 修复代码生成器对关联字段生成ImportVM时的bug

#### 前后端不分离模式

* **修改：** upload控件增加修改尺寸的选项，当UploadType为ImageFile时，通过设置ThumbWidth和ThumbHeight，可以让服务器保存缩小后的图片
* **修改：** upload控件增加缩略图预览，当UploadType为ImageFile时默认开启缩略图预览，通过ShowPreview，PreviewWidth，PreviewHeight等参数可配置
* **修改：** 更改绑定布尔值时checkbox的样式
* **修改：** 修复layui模式下登录用户头像显示的问题，已经生成的项目可以将以下两个文件覆盖自己的项目对应的文件
    1. [LoginVM.cs](https://github.com/dotnetcore/WTM/blob/develop/demo/WalkingTec.Mvvm.Demo/ViewModels/HomeVMs/LoginVM.cs)
    1. [Header.cshtml](https://github.com/dotnetcore/WTM/blob/develop/demo/WalkingTec.Mvvm.Demo/Views/Home/Header.cshtml)

### v2.2.38 (2019-4-29)

* **修改：** 修改DpWhere逻辑，DpWhere中多个字段参数之间现在是or的关系

#### 前后端不分离模式

* **新增：** 新增layui下根据数据控制行内动作按钮是否显示。现在可以在Action上调用BindVisiableColName方法来指定某个隐藏列的名称，该隐藏列值为字符串'true'的时候，action按钮才显示
* **修改：** 修改了tab页模式下，多层弹出窗口grid不刷新的bug
* **修改：** 修复selector在searchpanel里，清空了不起作用的bug
* **修改：** 修改pindex页面使用tab，导致代码生成器显示错位的bug

### v2.2.36 (2019-4-25)

#### 前后端不分离模式

* **新增：** 新增layui下列表后台排序功能，在ListVM中设置列的时候，对需要排序的列调用SetSort(true)即可

### v2.2.35 (2019-4-22)

* **修改：** 修复代码生成器在Mac系统下无法工作的问题
* **修改：** 修复UpdateTime和UpdateBy字段没有自动写入的问题

#### 前后端不分离模式

* **修改：** 修复checkbox为false，表单字段不提交的bug

#### 前后端分离模式

* **修改：** 修复api在标记了Area属性时框架无法识别的bug

### v2.2.34 (2019-4-20)

#### 前后端不分离模式

* **修改：** 修复菜单管理中，修改菜单的bug

#### 前后端分离模式

* **修改：** 修复IsQuickDebug为false时，api无法正常访问的问题
* **修改：** 修复Excel导入的bug

### v2.2.33 (2019-4-16)

#### Features

* **修改：** 修改前后端分离模式中表单提交的逻辑，现在表单中没有定义的字段将不会被更新
* **修改：** 修改前后端分离模式中框架自带的一些前台页面的bug
* **修改：** 修改BaseCRUDVM中的删除逻辑，现在可以正确删除include了其他关联类的Entity

### v2.2.32 (2019-4-16)

#### Features

* **新增：** React前后端分离模式RTM版本正式发布！欢迎大家使用

### v2.2.28 (2019-4-9)

#### Features

* **修改：** 系统菜单表新增ClassName和MethodName两个字符串字段，已有项目升级时可手动在库中添加这两个字段
* **修改：** React的前后端分离模式接近完成

### v2.2.26 (2019-4-1)

#### Features

* **新增：** 新增对pgsql的支持
* **修改：** 重写菜单管理逻辑，菜单配置更简便
* **修改：** React的前后端分离模式稳步推进

### v2.2.22 (2019-3-20)

#### Features

* **修改：** 代码生成器会在Controller和VM的类上加入partial，这样方便大家另起一个文件写自定义的代码，而不用担心再次生成代码的时候会被覆盖
* **修改：** 修改<wt:combobox>支持多选枚举的绑定
* **修改：** React的前后端分离模式增加了多个控件，修改了若干实际项目中反应的问题，向正式版又前进了一大步

### v2.2.10 (2019-2-27)

#### Features

* **新增：** 新增在线生成项目，[https://wtmdoc.walkingtec.cn/setup](https://wtmdoc.walkingtec.cn/setup) ，生成WTM项目更快捷
* **修改：** 进一步完善React的前后端分离模式，当然目前还是预览

### v2.2.8 (2019-2-24)

#### Features

* **新增：** 框架开始支持前后端分离模式，可以创建React的前后端分离模式的项目，并生成前后端分离模式的代码
* **新增：** 新增MiddleTableAttribute，对于多对多的关系，可以在中间表的模型上标记[MiddleTable]，框架的代码生成器根据这个标记可以正确生成多对多关系的增删改查代码

### v2.2.4 (2019-01-11)

#### Bug Fixes

* 修改默认连接字符串的bug ([318840c](https://github.com/WalkingTec/WalkingTec.Mvvm/commit/318840c))
* 修改密码的bug ([1427fb7](https://github.com/WalkingTec/WalkingTec.Mvvm/commit/1427fb7))

### v2.2.3 (2019-01-08)

#### Bug Fixes

* 修改外部地址菜单刷新的bug ([918560f](https://github.com/WalkingTec/WalkingTec.Mvvm/commit/918560f))

### v2.2.2 (2019-01-04)

#### Bug Fixes

* 修改富文本必填验证的bug ([e9f2cd0](https://github.com/WalkingTec/WalkingTec.Mvvm/commit/e9f2cd0))

### v2.2.1 (2018-12-23)

#### Features

* **修改：** 修改文件上传相关配置，将 `SaveFileMode` 及 `UploadDir` 更改到 `FileUploadOptions` 中

#### Bug Fixes

* 解决 .net core 2.2下 IIS Inprogres s运行的问题 ([90256fe](https://github.com/WalkingTec/WalkingTec.Mvvm/commit/90256fe))

### v2.2.0 (2018-12-20)

#### Features

* **新增：** 添加富文本组件
* **新增：** 添加自定义路由的简便入口
* **修改：** CrossDomainAttribute现在可以指定允许的域名
