Handle GitHub Dependabot PRs by validating then porting to Gitea. Gitea (`mac-mini.tailde842d.ts.net/chiu0831/WTM.git`) is the authoritative remote. Dependabot runs on the GitHub mirror (`cct08311github/WTM`) and opens PRs there, but we never merge on GitHub directly — we port the change to Gitea, merge there, then sync back.

**Usage:** `/sync-dependabot [<github-pr-number>]`
If no PR number is given, run Discovery first and ask which PR to sync.

---

## 1. Discovery

List all open Dependabot PRs on the GitHub mirror:

```bash
gh pr list -R cct08311github/WTM \
  --author "app/dependabot" \
  --json number,title,createdAt,statusCheckRollup,headRefName \
  --jq '.[] | [.number, .headRefName, .title] | @tsv'
```

Print the list and ask the operator which PR number(s) to sync if not already specified.

---

## 2. Validation gates (ALL BLOCKING — stop at first failure)

Run all checks for the target `<n>`:

### 2a. GitHub CI must be green

```bash
gh pr view <n> -R cct08311github/WTM \
  --json statusCheckRollup \
  --jq '.statusCheckRollup[] | [.name, .conclusion] | @tsv'
```

**PASS**: every check shows `SUCCESS` or `NEUTRAL`.
**FAIL**: any check is `FAILURE` or `PENDING`. Stop — do not port a broken bump.

### 2b. Diff scope: dependency files only

```bash
gh pr diff <n> -R cct08311github/WTM --name-only
```

**Allowed file patterns** (all others = FAIL):
- `package.json` / `package-lock.json` / `yarn.lock`
- `*.csproj` (version ref bumps only — see 2c)
- `common.props`
- `Directory.Packages.props`
- `global.json`
- `*.lock` / `*.lockfile`

**Reject immediately if any changed file matches:**
- `*.cs` — source code
- `*.ts` / `*.js` / `*.vue` — frontend source
- `.github/workflows/*.yml` / `.gitea/workflows/*.yml` — CI config
- `Dockerfile` / `docker-compose*.yml` — infra
- `*.md` — docs (Dependabot does not touch these)

If rejected: **STOP. Do not port.**

### 2c. Scope classification (determines whether human confirmation is required)

Inspect the file list from 2b:

| Scope | Condition | Action |
|-------|-----------|--------|
| **Demo-only npm** | All changes under `demo/*/ClientApp/` | Proceed automatically |
| **Demo csproj** | Changes in `demo/**/*.csproj` only | Proceed automatically |
| **Shipped package dep** | Any change touches `src/**/*.csproj` or `common.props` | **Pause and ask operator to confirm** — these affect NuGet packages shipped to users |

For shipped-package bumps: show the operator the exact diff lines and wait for explicit "proceed" before continuing.

### 2d. Clean apply check

Fetch the patch and verify it applies to current Gitea `dotnet10`:

```bash
git fetch origin dotnet10
gh pr diff <n> -R cct08311github/WTM > /tmp/dependabot-<n>.patch
git apply --check --index /tmp/dependabot-<n>.patch
```

**PASS**: exits 0.
**FAIL**: conflicts reported. Do not proceed — inspect manually and report to operator.

---

## 3. Port to Gitea

All steps use the Gitea API. Read the token and set the host first:

```bash
GITEA_TOKEN=$(grep -oE '[a-f0-9]{40}' "$HOME/.gitea-token")
GITEA_HOST="mac-mini.tailde842d.ts.net"
```

**Never `source $HOME/.gitea-token`.** It is a bare token file (one 40-hex line, no
`export`, no `=`), so `source` makes the shell execute the token as a command and the
resulting `command not found: <token>` writes the credential in clear text into the
session transcript. That fired for real twice on 2026-07-11. `grep -oE` works whatever
the file format is, so it cannot break if an external process rewrites the file.
Never `echo` or `cat` the value.

### 3a. Open Gitea issue

Extract the bump summary from the GitHub PR body:

```bash
gh pr view <n> -R cct08311github/WTM --json title,body \
  --jq '"# " + .title + "\n\n" + .body'
```

Open the issue:

```bash
GITEA_TOKEN=$(grep -oE '[a-f0-9]{40}' "$HOME/.gitea-token")   # never `source` it — see below
GITEA_HOST="mac-mini.tailde842d.ts.net"
curl -s -X POST \
  "https://$GITEA_HOST/api/v1/repos/chiu0831/WTM/issues" \
  -H "Authorization: token $GITEA_TOKEN" \
  -H "Content-Type: application/json" \
  -d "{
    \"title\": \"chore(deps): sync GitHub Dependabot PR #<n>\",
    \"body\": \"Port dependency bump from GitHub mirror Dependabot PR #<n>.\n\n<paste bump bullet list here>\n\nScope: demo-only npm / shipped-package dep (pick one).\nGitHub PR: https://github.com/cct08311github/WTM/pull/<n>\",
    \"labels\": [2]
  }"
```

Note the returned `number` — this is `<gitea-issue>`.

### 3b. Create branch from latest dotnet10

```bash
git fetch origin dotnet10
git checkout -b deps/sync-github-pr-<n> origin/dotnet10
```

### 3c. Apply the patch

```bash
git apply /tmp/dependabot-<n>.patch
```

### 3d. Commit

```bash
git add -A
git commit -m "chore(deps): bump demo npm dependencies (sync from GitHub Dependabot #<n>)

- <package>: <old-version> → <new-version>  (repeat for each bump)
- Scope: demo-only (ClientApp under demo/; not shipped in NuGet packages)
  OR: shipped-package dep in src/*.csproj (operator confirmed)

Closes #<gitea-issue>"
```

Adjust the subject line for shipped-package bumps:
`chore(deps): bump <package> in src/ (sync from GitHub Dependabot #<n>)`

### 3e. Push

```bash
git push -u origin deps/sync-github-pr-<n>
```

### 3f. Open Gitea PR

```bash
GITEA_TOKEN=$(grep -oE '[a-f0-9]{40}' "$HOME/.gitea-token")   # never `source` it — see below
GITEA_HOST="mac-mini.tailde842d.ts.net"
curl -s -X POST \
  "https://$GITEA_HOST/api/v1/repos/chiu0831/WTM/pulls" \
  -H "Authorization: token $GITEA_TOKEN" \
  -H "Content-Type: application/json" \
  -d "{
    \"title\": \"chore(deps): sync GitHub Dependabot PR #<n>\",
    \"head\": \"deps/sync-github-pr-<n>\",
    \"base\": \"dotnet10\",
    \"body\": \"## Dependency bumps\n\n| Package | From | To | Scope |\n|---------|------|----|-------|\n| <package> | <old> | <new> | demo/shipped |\n\nSynced from GitHub Dependabot PR: https://github.com/cct08311github/WTM/pull/<n>\n\nValidation:\n- [x] GitHub CI green\n- [x] Diff scope: dependency files only\n- [x] git apply --check passed\n\nCloses #<gitea-issue>\"
  }"
```

Note the returned `number` — this is `<gitea-pr>`.

---

## 4. CI watch + merge

### 4a. Wait for CI (BOUNDED polling — never silent infinite loop)

Poll Gitea PR-level commit status with **explicit max-iterations AND hard timeout**. Real CI runs are 15–25 min; cap at 25 min so a stuck/non-existent target surfaces fast.

> **Never gate on the combined `state` field alone (Issue #605).** Gitea's combined status
> only aggregates contexts that have *already reported*. Early in a run only the fast jobs
> (release-tooling-test, js-test) have status rows, so combined = `success` while
> build-and-test/e2e are still running — merging then is premature and additionally starts
> push-CI in parallel with the still-running PR-CI on the same runner (known concurrency-test
> flake trigger, #596/#554). The loop below requires every REQUIRED context to be present
> AND `success` before declaring green.
>
> **Path-aware required set:** `e2e-test.yml` only triggers for diffs touching `src/**`,
> `demo/**`, or `test/e2e/**`. A bump confined to other paths (e.g. only
> `test/WalkingTec.Mvvm.Js.Tests/package-lock.json`) never produces an e2e context — drop
> `E2E Tests / e2e (pull_request)` from `required` for such diffs or the loop can never go
> green. And regardless of what any watcher reports: **re-read job-level status
> (`/actions/runs/<id>/jobs`) directly in the same command that performs the merge** —
> job-level status is ground truth; run-level `conclusion`, combined status, and stale
> monitor notifications are all unreliable.

```bash
GITEA_TOKEN=$(grep -oE '[a-f0-9]{40}' "$HOME/.gitea-token")   # never `source` it — see below
GITEA_HOST="mac-mini.tailde842d.ts.net"

# Bounds (do NOT remove — silent infinite polling burned 17 min on a non-existent PR once)
MAX_ATTEMPTS=50          # 50 × 30 s = 25 min ceiling
TIMEOUT_SECONDS=1500     # belt-and-suspenders hard cap
INTERVAL=30
attempt=0
start=$(date +%s)

# Sanity: confirm the PR exists BEFORE entering the loop
if ! curl -sf -H "Authorization: token $GITEA_TOKEN" \
   "https://$GITEA_HOST/api/v1/repos/chiu0831/WTM/pulls/<gitea-pr>" >/dev/null; then
  echo "FATAL: Gitea PR #<gitea-pr> does not exist — aborting"; exit 2
fi

while :; do
  attempt=$((attempt+1))
  elapsed=$(($(date +%s) - start))
  if [ "$attempt" -gt "$MAX_ATTEMPTS" ] || [ "$elapsed" -ge "$TIMEOUT_SECONDS" ]; then
    echo "TIMEOUT after ${attempt} attempts / ${elapsed}s — investigate manually"
    exit 1
  fi

  sha=$(curl -s -H "Authorization: token $GITEA_TOKEN" \
    "https://$GITEA_HOST/api/v1/repos/chiu0831/WTM/pulls/<gitea-pr>" \
    | python3 -c "import sys,json;print(json.load(sys.stdin)['head']['sha'])")
  result=$(curl -s -H "Authorization: token $GITEA_TOKEN" \
    "https://$GITEA_HOST/api/v1/repos/chiu0831/WTM/commits/$sha/status" \
    | python3 -c "
import sys, json
d = json.load(sys.stdin)
sts = {s['context']: s['status'] for s in d.get('statuses', [])}
required = [
    'WTM Build and Test / build-and-test (pull_request)',
    'WTM Build and Test / js-test (pull_request)',
    'WTM Build and Test / release-tooling-test (pull_request)',
    'E2E Tests / e2e (pull_request)',
]
bad = [c for c, v in sts.items() if v in ('failure', 'error')]
if bad:
    print('FAILURE:' + ','.join(bad))
elif all(sts.get(c) == 'success' for c in required):
    print('ALLGREEN')
else:
    done = sum(1 for c in required if sts.get(c) == 'success')
    print(f'WAITING {done}/{len(required)}')
")

  echo "[$attempt/$MAX_ATTEMPTS @ ${elapsed}s] head=${sha:0:8} $result"

  case "$result" in
    ALLGREEN)   echo "ALL GREEN — all required contexts present and successful"; break ;;
    FAILURE:*)  echo "FAILURE — read job log, decide rebase (4b) vs fix"; exit 1 ;;
    *)          sleep $INTERVAL ;;
  esac
done
```

The canonical secondary check (only when commit-status is unreliable due to the known infra quirk): look for `Test Run Successful` in the build-and-test log and `PASS: N | FAIL: 0` in the e2e log — do NOT rely on job `conclusion` alone (see `MEMORY.md`).

### 4b. Infra flake handling

If CI fails with either of these known infra flakes, **do not change the code** — rebase and retrigger:
- `RWLayer of container unexpectedly nil` (Docker daemon crash)
- `Failed to negotiate protocol` / `Test Run Aborted` (VSTest timeout)

```bash
git fetch origin dotnet10
git rebase origin/dotnet10
git push --force-with-lease origin deps/sync-github-pr-<n>
```

### 4c. Squash-merge when CI green

```bash
GITEA_TOKEN=$(grep -oE '[a-f0-9]{40}' "$HOME/.gitea-token")   # never `source` it — see below
GITEA_HOST="mac-mini.tailde842d.ts.net"
curl -s -X POST \
  "https://$GITEA_HOST/api/v1/repos/chiu0831/WTM/pulls/<gitea-pr>/merge" \
  -H "Authorization: token $GITEA_TOKEN" \
  -H "Content-Type: application/json" \
  -d "{
    \"Do\": \"squash\",
    \"merge_message_field\": \"chore(deps): sync GitHub Dependabot PR #<n> (Closes #<gitea-issue>)\",
    \"delete_branch_after_merge\": true
  }"
```

Note the merge commit SHA from the response — this is `<sha>`.

---

## 5. Sync back to GitHub

> **Critical: respect the `.sync/` divergence manifest before pushing.** The public GitHub mirror must never expose internal hostname / token / brand-name references. The manifest at `.sync/{github-excludes.txt,github-replace.txt,github-sanitize.sed}` defines what to drop / swap / scrub. The release-tag publish workflow applies this automatically; for a manual sync (this skill's flow), apply it inline as shown below.

### 5a. Build the filtered push tree, then push

Work on a throwaway local branch so the manifest mods never touch `dotnet10`:

```bash
git fetch origin dotnet10
git checkout -B tmp/github-sync origin/dotnet10

# 5a.0 — preserve the sanitize rules BEFORE excludes delete .sync/ (Issue #605).
# .sync/ itself is in github-excludes.txt, so step 5a.ii removes it from the
# working tree; without this copy, step 5a.iii has no rules file and silently
# skips sanitization. (The publish workflow does the same via $RUNNER_TEMP.)
SANITIZE_SED=$(mktemp -t wtm-sanitize)
cp .sync/github-sanitize.sed "$SANITIZE_SED"

# 5a.i — apply github-replace.txt (file swaps)
while IFS=$'\t' read -r src dst; do
  case "$src" in ''|\#*) continue;; esac
  [ -n "$dst" ] && [ -f "$src" ] && cp -f "$src" "$dst" && git rm -f "$src" || true
done < .sync/github-replace.txt

# 5a.ii — apply github-excludes.txt (delete excluded paths)
while IFS= read -r line; do
  case "$line" in ''|\#*) continue;; esac
  case "$line" in
    */) git rm -rf "${line%/}" 2>/dev/null || true ;;
    *)  git rm -f "$line"      2>/dev/null || true ;;
  esac
done < .sync/github-excludes.txt

# 5a.iii — apply github-sanitize.sed to TRACKED text files (Issue #605: use perl,
# NOT sed. macOS BSD sed treats \b as a literal, so every word-boundary rule in
# github-sanitize.sed is a silent no-op — GITEA_TOKEN/Gitea-brand references would
# leak to the public mirror. Perl's regex engine matches GNU sed semantics for
# these rules. git ls-files (not find) keeps node_modules/untracked files out.)
# (sed s|pat|repl|g rules are valid perl statements once ';'-terminated)
git ls-files -z -- '*.md' '*.yml' '*.props' '*.csproj' '*.json' '*.cs' \
  | xargs -0 perl -pi -e "$(grep -vE '^\s*(#|$)' "$SANITIZE_SED" | sed 's/$/;/')"

# 5a.iv — LEAK GATE (Issue #605): hard-verify no internal marker survived.
# The one expected survivor is publish-nuget.yml's deliberately-split
# _G="GITEA""_" line and its \b-prefixed comment (no word boundary between
# the literal backslash-b text and GITEA, so GNU sed/perl skip it by design).
if git grep -n -E 'mac-mini\.tailde842d|tailde842d|\bGITEA_TOKEN\b|\bGITEA_HOST\b|\bGITEA_USER\b|\bGitea\b|\bgitea-wtm\b' \
     -- '*.md' '*.yml' '*.props' '*.csproj' '*.json' '*.cs' \
     | grep -v 'bGITEA_TOKEN/HOST/USER'; then
  echo "FATAL: internal references survived sanitize — DO NOT PUSH"; exit 1
fi

# 5a.v — commit the manifest application as a single chore commit
git add -A
git commit -m "chore(sync): apply .sync/ manifest for GitHub mirror push (Dependabot #<n>)"

# 5a.vi — push to GitHub. Fast-forward first; fallback merge with the same
# deterministic conflict resolution the publish workflow uses (Gitea authoritative).
if ! git push github HEAD:refs/heads/dotnet10; then
  git fetch github dotnet10
  # -X ours resolves content conflicts but leaves modify/delete conflicts
  # unresolved — resolve them explicitly, exactly like publish-nuget.yml does.
  git merge github/dotnet10 -X ours --allow-unrelated-histories --no-edit \
    -m "merge: reconcile GitHub divergence (Dependabot #<n>)" || true
  # DU = deleted by us (excluded from mirror) → keep deletion.
  git status --porcelain | awk '/^DU /{print $2}' | while IFS= read -r f; do git rm -f -- "$f"; done
  # UD = deleted on mirror, present here → keep HEAD version.
  git status --porcelain | awk '/^UD /{print $2}' | while IFS= read -r f; do git checkout --ours -- "$f"; git add -- "$f"; done
  # Remaining content conflicts → keep HEAD (the sanitized tree).
  git diff --name-only --diff-filter=U | while IFS= read -r f; do git checkout --ours -- "$f"; git add -- "$f"; done
  if [ "$(git ls-files -u | wc -l | tr -d ' ')" -gt 0 ]; then
    echo "FATAL: unresolved conflicts remain"; git ls-files -u | head -20; exit 1
  fi
  # Commit only if the merge is still open (it completed cleanly otherwise)
  [ -f "$(git rev-parse --git-dir)/MERGE_HEAD" ] && git commit --no-edit
  # Final sanity before push: the tree diff vs the mirror must contain ONLY the
  # intended dependency files (mirror-side drift like docs/github-packages.md and
  # the ci-build.yml internal-name rename is preserved by the merge — expected).
  git diff --stat github/dotnet10..HEAD
  git push github HEAD:refs/heads/dotnet10
fi

# 5a.vii — clean up the throwaway branch
git checkout dotnet10
git branch -D tmp/github-sync
```

**Never force-push** to `dotnet10` on either remote. If steps above still fail, stop and investigate (do not retry with `--force`).

### 5b. Close the GitHub Dependabot PR

```bash
# Get Dependabot branch name
BRANCH=$(gh pr view <n> -R cct08311github/WTM --json headRefName --jq '.headRefName')

# Leave a closing comment
gh pr comment <n> -R cct08311github/WTM \
  --body "Superseded by Gitea PR #<gitea-pr> (commit \`<sha>\`). Closing per Gitea-primary policy — dependency bump was ported to Gitea, merged there, and synced back to this mirror."

# Close the PR
gh pr close <n> -R cct08311github/WTM

# Delete the Dependabot branch (Dependabot will re-open if still outdated)
gh api -X DELETE \
  "/repos/cct08311github/WTM/git/refs/heads/$BRANCH"
```

---

## 6. Done — report to operator

```
✅ GitHub Dependabot PR #<n> synced

  Gitea issue:  #<gitea-issue>  https://mac-mini.tailde842d.ts.net/chiu0831/WTM/issues/<gitea-issue>
  Gitea PR:     #<gitea-pr>    https://mac-mini.tailde842d.ts.net/chiu0831/WTM/pulls/<gitea-pr>
  Merge commit: <sha>
  GitHub PR #<n>: closed + branch deleted
  dotnet10 mirror: fast-forward pushed / merge synced (pick one)
```

---

## Anti-patterns

- ❌ Merging the Dependabot PR directly on GitHub — violates Gitea-primary policy
- ❌ Force-pushing to `dotnet10` on either remote
- ❌ Auto-merging without verifying both CI green AND diff scope
- ❌ Treating a shipped-package dep bump (`src/*.csproj`) the same as a demo-only npm bump — shipped bumps require operator confirmation
- ❌ Cancelling or skipping the Gitea CI wait — even a one-line dep bump must pass CI before merge
- ❌ Treating Gitea's combined commit status `success` as ALL GREEN — contexts that haven't reported yet are invisible; require the full required-context set (Issue #605)
- ❌ Running the sanitize step with macOS BSD `sed` — `\b` rules silently no-op; use the perl pipeline in 5a.iii and always run the 5a.iv leak gate before pushing (Issue #605)
- ❌ Applying excludes before copying `github-sanitize.sed` out of `.sync/` — the rules file deletes itself (Issue #605)
- ❌ Leaving the GitHub Dependabot branch alive after sync — delete it so Dependabot doesn't re-open the same PR unnecessarily
