# AGENTS.md

This file provides guidance to Codex when working with this repository.
Detailed engineering rules live in `.claude/rules/`; broader design and ops
docs live in `docs/` — this file is the entry point.

## Project Mission

Personal fork of WalkingTec MVVM Framework (WTM), taken over 2026-03.
Goal: **stable, modernized, actively-evolved** .NET rapid-development framework.
Current phase: **Feature growth on solid ground** — security audit cleared (10.2.0), Clean Architecture (10.1.0), and 10.4.0/10.5.0 added 20+ opt-in middleware + BI extensions; now iterating on observability, BI, and ETL.
Active branch: `dotnet10`. Origin: internal infrastructure (`internal.registry.invalid/chiu0831/WTM.git`) — sole authoritative remote. NuGet publishes go to internal infrastructure's NuGet registry (`/api/packages/chiu0831/nuget`). The same `dotnet10` branch is also published to a sanitized public GitHub mirror (`github.com/cct08311github/WTM`), synced daily from internal infrastructure since 2026-05-23 via the `.sync/` mechanism (see `.sync/README.md`, the authoritative description), so external users can install via GitHub Packages. The `/sync-dependabot` and `/sync-github-security` slash commands serve that mirror's maintenance — internal infrastructure is always the source of truth and we never merge on GitHub directly.

## Decision Priorities (in order)

1. **Compatibility** — avoid breaking existing users; deprecate before removing
2. **Security** — fix vulnerabilities immediately (P0), no shortcuts
3. **Quality** — nullable annotations, structured logging, test coverage
4. **Performance** — optimize only with evidence (profiling, benchmarks)

## Red Lines — Never Violate

- **Never bypass the VM layer** to access DataContext directly from controllers
- **Never silently change default behaviour** — new functionality must be opt-in; behaviour changes need a `CHANGELOG.md` entry with migration notes
- **Never use `IgnoreQueryFilters()`** without a comment explaining why
- **Never commit `#nullable disable`** in new code
- **Never use double-`!` null-forgiving chains** — split into separate checks
- Breaking changes require: `CHANGELOG.md` entry, migration path, minor version bump

## Architecture at a Glance

Four projects: `Core` (VMs, DataContext, Models, Analysis, WTMContext), `Mvc` (Controllers, startup, API),
`TagHelpers.LayUI` (UI), `Etl` (data pipeline). Four VM types all extend `BaseVM`.
WTMContext uses focused `IWtm*Service` interfaces. Startup: `AddWtmContext` → `UseWtmContext` → `UseWtmStaticFiles`.
Full detail → `docs/system-architecture.md`.

## Security Summary

Passwords (PBKDF2 + legacy MD5 migration), JWT (access/refresh rotation + `jti` replay guard),
Analysis Mode (field whitelist before Expression Tree), file upload (path-traversal guard),
dynamic ops (blocklist + type validation). Related invariants → `.claude/rules/security-invariants.md`;
broader security posture → `docs/production-readiness.md` § 安全姿態.

## Quick Commands

```bash
dotnet build WalkingTec.Mvvm.sln -c Release
dotnet test WalkingTec.Mvvm.sln -c Release --verbosity normal

# Single test class (most-used dev loop)
dotnet test test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj \
  --filter "FullyQualifiedName~AnalysisControllerTests" -c Release
```

> First-time setup: `dotnet restore` requires the internal NuGet source configured —
> see `README.md` § Quick Start step 1, or `docs/gitea-packages.md`.

No single consolidated command reference exists. The daily-loop commands are above;
full local build/test/e2e/vulnerability-scan reproduction is in `docs/ci-operations.md`
§ 本機 reproduce CI 流程; the package-upgrade/vulnerability-scan loop is in
`docs/dependency-management.md`.

## CI Status Caveat

`actions/upload-artifact@v4` is incompatible with the local internal CI API.
`build-and-test` and `e2e` jobs can finish with `conclusion: failure` even when tests passed.
**Always verify by reading the log:** look for `Test Run Successful` (.NET) and, for e2e,
`FAIL: 0 | ERROR: 0` in the `Total: N | PASS: n | FAIL: 0 | ERROR: 0 | SKIP: n` summary line —
**match on `FAIL: 0`, not a hardcoded `PASS: N`**: `N` legitimately differs between the
`e2e-test.yml` matrix's `baseline` leg (full suite) and `killswitch` leg (focused subset,
issue #681), and changes again whenever `TC_REGISTRY` in `test/e2e/wtm_e2e_tests.py` grows.
Detail + workaround SOP → `docs/ci-operations.md`. Tracking: Issue #11.

## Available Slash Commands

- `/wtm-release-check` — BLOCKING gate before any release (build + test + LOCAL vuln scan)
- `/wtm-manual-update` — Updates `docs/wtm-developer-manual.md` for the version
- `/sync-dependabot` — Validates Dependabot PRs opened on the GitHub mirror and ports them to internal infrastructure (never merged on GitHub directly)
- `/sync-github-security` — Triages GitHub mirror Security tab alerts (Dependabot CVEs / CodeQL / secrets) and ports fixes to internal infrastructure

### Documented workflows (not slash commands)

These are one-liners kept as documented steps rather than command files:

- **Full test suite:** `dotnet test WalkingTec.Mvvm.sln -c Release` then `cd test/WalkingTec.Mvvm.Js.Tests && npm test` (see `.claude/rules/testing.md` § "JS and e2e")
- **Nullable-annotation scan:** `grep -rln "#nullable disable" src/` and review `<Nullable>` project settings (new code must never commit `#nullable disable`)

## Key Files

| File | Purpose |
|------|---------|
| `version.props` | Single source of framework version |
| `Directory.Packages.props` | Central Package Management — all NuGet versions |
| `common.props` | Package metadata (authors, license, repo URL) |
| `global.json` | Pins .NET SDK 10.0.0+ (`rollForward: latestFeature`) |
| `CHANGELOG.md` | Version history (update when releasing) |
| `docs/analysis-mode.md` | Analysis Mode manual |
| `docs/lookup-cache.md` | Lookup Cache manual |
| `docs/production-readiness.md` | Prod-readiness scorecard (PR #17) |
| `docs/ci-operations.md` | internal CI CI quirks + SOP (PR #17) |

## Detailed Rules (`.claude/rules/`)

Each file below is auto-loaded by Claude Code via its own `paths:` frontmatter
when you touch a matching file; Codex and other agents should read the whole
directory up front since it has no equivalent auto-load mechanism.

| File | Covers |
|------|--------|
| `dotnet-conventions.md` | Nullable policy, async, EF Core conventions, hot-path reflection caching, logging |
| `testing.md` | Test projects, mock patterns, EF InMemory limits, JS/e2e test conventions |
| `dependency-management.md` | Central Package Management, NU1510 vs NU1903, upgrade policy |
| `release-changelog.md` | Commit scopes, CHANGELOG.md sections, release flow |
| `security-invariants.md` | `src/`-wide invariants: dialog trust boundary, ordering-is-the-boundary, tenant scope, SSRF hardening |

Always consult the relevant rule file before making changes in that area.

### Known gaps (#921)

These four filenames were cited from this document for a long time before
anyone noticed no file existed at the path — do not assume the content
below lives somewhere else in full; in most cases it simply doesn't exist
yet:

- **`architecture.md`** — never created. The closest existing coverage is
  `docs/system-architecture.md` (layering, `WTMContext`, Analysis Mode,
  `DataContext`); it does not attempt the "compatibility" angle the old
  citation implied.
- **`known-quirks.md`** — never created. No file anywhere documents
  `TestFrameworkContext`, Serilog specifics, or EF Core API-change
  pitfalls as a dedicated collection; that knowledge is undocumented.
- **`workflow.md`** — never created. Branch strategy / commit message
  format / PR checklist / Issue-reference conventions are the operator's
  own internal infrastructure workflow, not repo content, so there is nothing to redirect
  to here; `release-changelog.md`'s commit-scope list is the only
  in-repo overlap.
- **`tools-commands.md`** — never created. Commands are scattered across
  the Quick Commands section above, `docs/ci-operations.md`, and
  `docs/dependency-management.md`; no single reference file exists.
