Triage GitHub Advanced Security tab alerts (CVE dependencies, CodeQL findings, leaked secrets) on the public GitHub mirror (`cct08311github/WTM`) and port fixes to the authoritative Gitea remote. Gitea is always the source of truth — fixes are made there, then synced back to GitHub which auto-closes the alert.

**Usage:** `/sync-github-security [dependabot|codeql|secrets|all]`
Default: `all` — discover and triage all three alert types in sequence.

---

## 1. Purpose

The GitHub mirror surfaces three alert types in its Security tab. This skill handles each one by: (1) discovering open alerts via the GitHub API, (2) triaging per the policy table in §3, (3) applying any fix on Gitea via the standard issue+PR workflow, (4) syncing back to GitHub so the alert auto-closes.

The companion `/sync-dependabot` skill handles auto-generated Dependabot *PRs* (version bump proposals). This skill handles Dependabot *alerts* (unresolved CVE notifications) plus CodeQL and secret-scanning findings that never generate a PR on their own.

**Gitea→GitHub sync note:** when pushing Gitea `dotnet10` to the GitHub mirror, the push workflow applies `.sync/github-sanitize.sed` (strips internal hostnames / brand references), `.sync/github-excludes.txt` (drops internal-only paths), and `.sync/github-replace.txt` (file swaps). The skill does not need to re-apply these rules — the workflow handles them. However: if a fix commit introduces a new doc or config file that should NOT appear on GitHub, add its path to `.sync/github-excludes.txt` in the same commit. And never write internal hostnames or Gitea-brand references in fix commits — the sed rules would scrub them, but it is cleaner not to write them in the first place.

---

## 2. Discovery

Always `source $HOME/.gitea-token` before Gitea API calls. The `gh` CLI targets the GitHub mirror (`-R cct08311github/WTM`).

### 2a. Dependabot alerts

```bash
gh api -X GET /repos/cct08311github/WTM/dependabot/alerts \
  -F state=open --paginate \
  --jq '.[] | [.number, .severity, .dependency.package.name, .dependency.manifest_path, .security_advisory.cve_id] | @tsv'
```

### 2b. Code scanning alerts (CodeQL)

```bash
gh api -X GET /repos/cct08311github/WTM/code-scanning/alerts \
  -F state=open --paginate \
  --jq '.[] | [.number, .rule.severity, .rule.id, .most_recent_instance.location.path, .html_url] | @tsv'
```

### 2c. Secret scanning alerts

```bash
gh api -X GET /repos/cct08311github/WTM/secret-scanning/alerts \
  -F state=open --paginate \
  --jq '.[] | [.number, .secret_type, .secret_type_display_name, .html_url] | @tsv'
```

Print all results, grouped by type. For `all` mode, triage in the order: **secrets first** (P0), then Dependabot, then CodeQL.

---

## 3. Triage policy table

| Type | Scope | Action |
|---|---|---|
| Dependabot | Shipped packages (`src/*/*.csproj`, `common.props`) | Always fix — open Gitea issue + PR |
| Dependabot | Demo apps (`demo/**/package*.json` or `demo/**/*.csproj`) | Fix Critical/High; defer Medium/Low |
| Dependabot | Test projects (`test/*/*.csproj`) | Fix Critical/High; defer otherwise |
| Dependabot | Upstream-blocked transitive (e.g. NPOI pulling a vulnerable dep it doesn't expose) | Document in `docs/dependency-management.md` § GitHub Mirror Dependabot Alerts; dismiss with `tolerable_risk` + comment |
| CodeQL | High / Critical | Always fix |
| CodeQL | Medium / Low — confirmed true positive | Fix if low-risk change; defer if risky (note in MEMORY) |
| CodeQL | False positive | Dismiss with `false_positive` + comment explaining why |
| Secret scanning | Real leaked credential | **P0**: rotate the secret immediately before any other step, then redact from history + document as security incident |
| Secret scanning | False positive (test fixture, example value) | Dismiss with `revoked` or `used_in_tests` + comment |

**Before removing any `PackageReference` for a Dependabot alert:** read `.claude/rules/dependency-management.md` §NU1510, run the before/after vulnerability scan diff described there, and verify no new NU1903 appears after removal. A pin that looks unnecessary may be a transitive security override (canonical example: `System.Security.Cryptography.Xml` in `Core.csproj` — Issue #816). Removing it would re-expose the CVE it suppresses.

---

## 4. Port to Gitea

All steps use the Gitea API (`source $HOME/.gitea-token` first, exports `GITEA_TOKEN`, `GITEA_HOST`, `GITEA_USER`).

**HARD RULE — Sonnet delegation for code edits:** the global agent model rule prohibits the Opus session from directly editing `.cs`, `.ts`, `.js`, or any other source/test file. CodeQL fixes and any fix that modifies source code MUST be delegated to a Sonnet subagent. The Opus session handles git, Gitea API calls, documentation edits (`.md`), and config-only changes (`.csproj` version bumps, `common.props`, `global.json`).

### 4a. Open Gitea issue

```bash
source $HOME/.gitea-token
curl -s -X POST \
  "https://$GITEA_HOST/api/v1/repos/chiu0831/WTM/issues" \
  -H "Authorization: token $GITEA_TOKEN" \
  -H "Content-Type: application/json" \
  -d "{
    \"title\": \"fix(security): <alert-type> #<alert-n> — <brief description>\",
    \"body\": \"## Alert\n\nType: Dependabot / CodeQL / Secret scanning (pick one)\nAlert: https://github.com/cct08311github/WTM/security/<alerts|code-scanning|secret-scanning>/alerts/<alert-n>\nSeverity: Critical/High/Medium/Low\nCVE / Rule: <cve-id or codeql-rule-id>\n\n## Root cause\n\n<description>\n\n## Fix plan\n\n<description>\",
    \"labels\": [2]
  }"
```

Note the returned `number` — this is `<gitea-issue>`.

### 4b. Create branch from latest dotnet10

```bash
git fetch origin dotnet10
git checkout -b fix/security-alert-<alert-n> origin/dotnet10
```

### 4c. Apply the fix

**Config-only fixes** (version bumps in `.csproj` / `common.props`, dismissals with no code change): the Opus session may edit these directly.

**Source code fixes** (`.cs`, `.ts`, `.js`, any logic change): delegate to a Sonnet subagent with the exact file path, description of the required change, and verification command. Wait for Sonnet to complete before continuing.

For Dependabot version bumps in `src/*/*.csproj` or `common.props`:
1. Run the before-scan: `dotnet list WalkingTec.Mvvm.sln package --vulnerable --include-transitive > /tmp/vuln-before.txt`
2. Apply the version bump
3. Run the after-scan: `dotnet list WalkingTec.Mvvm.sln package --vulnerable --include-transitive > /tmp/vuln-after.txt`
4. Diff: `diff /tmp/vuln-before.txt /tmp/vuln-after.txt` — confirm the alert package disappears; confirm no new NU1903 was introduced

### 4d. Commit

```bash
git add <files>
git commit -m "fix(security): <brief description> — Closes #<gitea-issue>

- Alert: <alert-type> #<alert-n> on cct08311github/WTM
- Severity: <severity>
- CVE / Rule: <id>
- Fix: <one-line description>"
```

For dismissals (no code change), use a docs-only commit:

```bash
git commit --allow-empty -m "chore(security): dismiss <alert-type> #<alert-n> as <reason> — Closes #<gitea-issue>

False positive / tolerable_risk / used_in_tests.
<one-line justification>"
```

### 4e. Push

```bash
git push -u origin fix/security-alert-<alert-n>
```

### 4f. Open Gitea PR

```bash
source $HOME/.gitea-token
curl -s -X POST \
  "https://$GITEA_HOST/api/v1/repos/chiu0831/WTM/pulls" \
  -H "Authorization: token $GITEA_TOKEN" \
  -H "Content-Type: application/json" \
  -d "{
    \"title\": \"fix(security): <brief description>\",
    \"head\": \"fix/security-alert-<alert-n>\",
    \"base\": \"dotnet10\",
    \"body\": \"## Alert\n\nType: <alert-type>\nGitHub alert: https://github.com/cct08311github/WTM/security/<path>/alerts/<alert-n>\nSeverity: <severity>\nCVE / Rule: <id>\n\n## Fix\n\n<description>\n\n## Validation\n\n- [x] Before/after vulnerability scan diff clean\n- [x] dotnet build passing\n- [x] Relevant tests passing\n\nCloses #<gitea-issue>\"
  }"
```

Note the returned `number` — this is `<gitea-pr>`.

---

## 5. CI watch + merge

### 5a. Wait for CI

Poll Gitea CI. The canonical check: look for `Test Run Successful` in the build-and-test log and `PASS: N | FAIL: 0` in the e2e log — do NOT rely on job `conclusion` alone (known infra quirk documented in `docs/ci-operations.md`). Gate on the full required-context set, never the combined state field alone — see `/sync-dependabot` §4a (Issue #605) for the reference loop; note e2e only triggers for diffs touching `src/**`, `demo/**`, or `test/e2e/**` (paths filter), so require the e2e context only when the diff touches those paths.

```bash
source $HOME/.gitea-token
curl -s "https://$GITEA_HOST/api/v1/repos/chiu0831/WTM/actions/runs?branch=fix/security-alert-<alert-n>&limit=5" \
  -H "Authorization: token $GITEA_TOKEN" \
  --jq '.workflow_runs[] | [.id, .status, .conclusion] | @tsv'
```

### 5b. Infra flake handling

If CI fails with either known infra flake, do NOT change the code — rebase and retrigger:
- `RWLayer of container unexpectedly nil` (Docker daemon crash)
- `Failed to negotiate protocol` / `Test Run Aborted` (VSTest timeout)

```bash
git fetch origin dotnet10
git rebase origin/dotnet10
git push --force-with-lease origin fix/security-alert-<alert-n>
```

### 5c. Squash-merge when CI green

```bash
source $HOME/.gitea-token
curl -s -X POST \
  "https://$GITEA_HOST/api/v1/repos/chiu0831/WTM/pulls/<gitea-pr>/merge" \
  -H "Authorization: token $GITEA_TOKEN" \
  -H "Content-Type: application/json" \
  -d "{
    \"Do\": \"squash\",
    \"merge_message_field\": \"fix(security): <brief description> (Closes #<gitea-issue>)\",
    \"delete_branch_after_merge\": true
  }"
```

Note the merge commit SHA — this is `<sha>`.

---

## 6. Sync to GitHub + verify alert closed

### 6a. Push dotnet10 to GitHub mirror

```bash
git fetch origin dotnet10
git checkout dotnet10
git pull origin dotnet10

# Try fast-forward first
git push github dotnet10
```

If non-fast-forward (GitHub has divergent history):

```bash
git fetch github dotnet10
git checkout -b tmp/github-sync github/dotnet10
git merge origin/dotnet10 -X theirs \
  -m "chore: sync Gitea dotnet10 → GitHub (security fix)"
git push github HEAD:dotnet10
git checkout dotnet10
git branch -D tmp/github-sync
```

**Never force-push** to `dotnet10` on either remote.

The push triggers the Gitea→GitHub sync workflow which applies the `.sync/` manifest (sanitize + exclude + replace). No manual manifest steps needed here unless the fix added a new file that should be excluded from GitHub — in that case add it to `.sync/github-excludes.txt` before pushing.

### 6b. Verify GitHub alert closed

**Dependabot alert** — GitHub detects the package bump automatically. Wait ~5 minutes after mirror push, then check:

```bash
gh api /repos/cct08311github/WTM/dependabot/alerts/<alert-n> \
  --jq '[.state, .auto_dismissed_at, .fixed_at] | @tsv'
```

Expect `state: fixed`.

**CodeQL alert** — requires a fresh code scanning run (GitHub must re-analyse the codebase):

```bash
gh workflow run codeql.yml -R cct08311github/WTM
# Wait for run to complete (~10 min), then check
gh api /repos/cct08311github/WTM/code-scanning/alerts/<alert-n> \
  --jq '[.state, .fixed_at] | @tsv'
```

Expect `state: fixed`.

**Secret scanning alert** — only resolves when: (1) the leaked secret has been rotated AND (2) the raw value no longer appears in any commit reachable from the default branch. Check:

```bash
gh api /repos/cct08311github/WTM/secret-scanning/alerts/<alert-n> \
  --jq '[.state, .resolved_at, .resolution] | @tsv'
```

Expect `state: resolved`.

### 6c. Manual dismissal if auto-close doesn't happen

If the alert does not auto-close after a reasonable wait (Dependabot: 10 min; CodeQL: 30 min after scan completes; secret scanning: immediate after rotation + push), dismiss manually:

```bash
# Dependabot
gh api -X PATCH /repos/cct08311github/WTM/dependabot/alerts/<n> \
  -F state=dismissed \
  -F dismissed_reason=tolerable_risk \
  -F dismissed_comment="Fixed in Gitea commit <sha>; synced to mirror."

# CodeQL
gh api -X PATCH /repos/cct08311github/WTM/code-scanning/alerts/<n> \
  -F state=dismissed \
  -F dismissed_reason=false_positive \
  -F dismissed_comment="<reason>"

# Secret scanning
gh api -X PATCH /repos/cct08311github/WTM/secret-scanning/alerts/<n> \
  -F state=resolved \
  -F resolution=revoked \
  -F resolution_comment="Secret rotated and removed from codebase. Gitea commit <sha>."
```

---

## 7. Long-term tracking

For alerts that cannot be fixed (upstream-blocked transitive, by-design CodeQL findings, ongoing secret-rotation logistics), add an entry to `docs/dependency-management.md` under `## GitHub Mirror Dependabot Alerts — 已接受的例外` (created for Issue #610; follows the NPOI/SQLitePCLRaw tracking conventions). Each entry records: alert number, package, CVE, severity, scope, and an explicit **unblock condition**. Example row:

```markdown
| #<n> | <package> | <CVE> | <severity> | <manifest scope> | first_patched: None（<why blocked>）→ <unblock condition> |
```

This lives in a tracked repo doc (public-mirror-safe) — NOT in `.claude/rules/` (local-only, gitignored) and NOT in a repo `MEMORY.md` (does not exist).

---

## 8. Done — report to operator

```
✅ GitHub Security alerts processed

  Alert type:   Dependabot / CodeQL / Secret scanning
  Alert #:      <n>
  Gitea issue:  #<gitea-issue>  https://mac-mini.tailde842d.ts.net/chiu0831/WTM/issues/<gitea-issue>
  Gitea PR:     #<gitea-pr>     https://mac-mini.tailde842d.ts.net/chiu0831/WTM/pulls/<gitea-pr>
  Merge commit: <sha>
  GitHub alert: <state: fixed / resolved / dismissed>
  dotnet10 mirror: synced
```

---

## Anti-patterns

- ❌ Dismissing an alert without fixing it — unless it is a confirmed false positive or upstream-blocked transitive (document in MEMORY either way)
- ❌ Removing a `PackageReference` security-override pin because Dependabot flags it — NU1510 is ambiguous; run the before/after vuln diff first
- ❌ Treating a secret-scanning alert as anything but P0 — rotate the credential before any other step
- ❌ Opus session editing `.cs` or other source files directly for CodeQL fixes — delegate to Sonnet
- ❌ Force-pushing to `dotnet10` on either remote
- ❌ Introducing internal hostnames (`mac-mini.tailde842d.ts.net`), Gitea-brand references, or Gitea token names in fix commits — the `.sync/github-sanitize.sed` rules would scrub them on push, but writing them in the first place is cleaner to avoid
- ❌ Triggering a CodeQL re-scan without verifying the fix is actually on the GitHub mirror first
- ❌ Closing a secret-scanning alert as `revoked` without first confirming the credential has actually been rotated at the provider
