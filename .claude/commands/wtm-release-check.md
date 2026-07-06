BLOCKING readiness gate to run before tagging any WTM release. Verifies the source tree is release-ready — version/CHANGELOG consistency, a clean build, a full green test run, and (critically) a **LOCAL** vulnerability scan — and STOPS if any gate fails. This command only *checks*; it does not tag, pack, or publish (that is the release flow — see §5).

**Usage:** `/wtm-release-check`
Run from the repo root on the exact commit you intend to tag.

---

## Why this is a hard gate

A release that ships a fixable `NU1903` (a package with a patched version available) is a P0 defect. CI's `security-scan` job can **false-green** on a stale advisory database — v10.12.4 and v10.13.10 both shipped a live `NU1903` this way. So the vulnerability scan here is run **locally, on the release SHA, and its output is read** — never trusted to a green CI check. Do not skip or shortcut any step below; report the exact result of each.

---

## 1. Preconditions

```bash
# run from the repo root
git fetch origin --quiet
git rev-parse HEAD                 # must equal origin/dotnet10 before a release
git rev-parse origin/dotnet10
git status --porcelain             # must be empty (clean working tree)
```

- **HEAD must equal `origin/dotnet10`** — never tag a local commit that is not on the authoritative remote.
- **Working tree must be clean** — no uncommitted changes leak into a tagged build.

If either fails → STOP and report.

## 2. Version / CHANGELOG consistency

```bash
grep VersionPrefix version.props                       # the version being released
grep -m1 "^## \[" CHANGELOG.md                          # newest CHANGELOG entry
```

- `version.props` `<VersionPrefix>` must match the newest `## [x.y.z]` heading in `CHANGELOG.md`.
- The CHANGELOG entry must have a real date (not a placeholder) and the appropriate `### Security` / `### Changed` / `### Migration` sections for what changed.
- A behaviour change or breaking change **must** have a `### Migration` note (repo red line).
- Developer manual: `docs/wtm-developer-manual.md`'s version header should also be current — if stale, run `/wtm-manual-update` first.

If mismatched → STOP.

## 3. Build (0 errors)

```bash
dotnet build WalkingTec.Mvvm.sln -c Release 2>&1 | tail -3
```

Expect `0 個錯誤` / `0 Error(s)`. `NU1510` warnings on the `System.Security.Cryptography.Xml` / `SQLitePCLRaw.bundle_e_sqlite3` override pins are EXPECTED (they confirm the security overrides are active) — do not "fix" them. Any actual error → STOP.

## 4. Tests (all green)

```bash
dotnet test WalkingTec.Mvvm.sln -c Release --no-build 2>&1 | grep -E "已通過|Passed!|Failed!|失敗:" | tail -12
cd test/WalkingTec.Mvvm.Js.Tests && npm test 2>&1 | tail -5 ; cd -
```

- All .NET test projects must pass (0 failures). Note the CI caveat only applies to the artifact-upload step — the *test result lines* are authoritative: look for `Test Run Successful` / `失敗: 0`.
- JS suite (`test/WalkingTec.Mvvm.Js.Tests`) must be fully green.
- If the LayUI TagHelpers changed, also run the dual-tree harness: `cd test/regression && npx playwright test` (must be 15/15 on both trees).

Any failure → STOP. (A known WorkFlow `TCONC` SQLite flake was fixed in #620 via `busy_timeout`; if a `database is locked` error resurfaces, it is infra contention — rerun once — not a code regression, but investigate if it persists.)

## 5. LOCAL vulnerability scan — the load-bearing gate

```bash
dotnet restore WalkingTec.Mvvm.sln --verbosity quiet
dotnet list WalkingTec.Mvvm.sln package --vulnerable --include-transitive
```

**Read the output.** The gate is **0 `NU1903`**:
- A **fixable** `NU1903` (a patched version exists) = P0 → STOP, pin/bump it, re-run the whole gate.
- An **unfixable** `NU1903` (`first_patched: None`) is a *tracked, documented* accepted exception only if it already has a tracking issue + a CHANGELOG known-issues note (see `.claude/rules/dependency-management.md`). A NEW unfixable one → STOP and document it before proceeding.
- `NU1510` (informational) is acceptable noise — it is the override-active signal, not a vulnerability.

## Result

Report each gate's outcome explicitly (pass/fail with the number). If **all** pass, state that the tree is release-ready and hand off to the release flow:

> **Release flow (NOT part of this gate):** bump handled already; tag `vX.Y.Z` on the release SHA → the `publish-nuget.yml` workflow publishes 6 packages to Gitea + GitHub → **verify the 6 packages exist on both registries** (not the run status) → create the **Gitea Release object** (never auto-created: `POST /api/v1/repos/chiu0831/WTM/releases`). Full mechanics: `docs/ci-operations.md` + the `wtm-release-ops` operator SOP.

If any gate failed, report which, and do NOT proceed to tagging.
