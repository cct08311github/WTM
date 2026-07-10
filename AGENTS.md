# AGENTS.md

This file provides guidance to Codex when working with this repository.
All detailed rules live in `.claude/rules/` — this file is the entry point.

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
Full detail → `.claude/rules/architecture.md`.

## Security Summary

Passwords (PBKDF2 + legacy MD5 migration), JWT (access/refresh rotation + `jti` replay guard),
Analysis Mode (field whitelist before Expression Tree), file upload (path-traversal guard),
dynamic ops (blocklist + type validation). Full detail → `.claude/rules/architecture.md` § Security.

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

Full command reference → `.claude/rules/tools-commands.md`

## CI Status Caveat

`actions/upload-artifact@v4` is incompatible with the local internal CI API.
`build-and-test` and `e2e` jobs can finish with `conclusion: failure` even when tests passed.
**Always verify by reading the log:** look for `Test Run Successful` (.NET) and `PASS: 30 | FAIL: 0` (e2e).
Detail + workaround SOP → `docs/ci-operations.md`. Tracking: Issue #11.

## Available Slash Commands

- `/wtm-release-check` — BLOCKING gate before any release (build + test + LOCAL vuln scan)
- `/wtm-manual-update` — Updates `docs/wtm-developer-manual.md` for the version
- `/sync-dependabot` — Validates Dependabot PRs opened on the GitHub mirror and ports them to internal infrastructure (never merged on GitHub directly)
- `/sync-github-security` — Triages GitHub mirror Security tab alerts (Dependabot CVEs / CodeQL / secrets) and ports fixes to internal infrastructure

### Documented workflows (not slash commands)

These are one-liners kept as documented steps rather than command files:

- **Full test suite:** `dotnet test WalkingTec.Mvvm.sln -c Release` then `cd test/WalkingTec.Mvvm.Js.Tests && npm test` (see `.claude/rules/tools-commands.md` § Test)
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

| File | Covers |
|------|--------|
| `workflow.md` | Branch strategy, commit message format, PR checklist, Issue references |
| `architecture.md` | Full architecture, WTMContext services, Analysis Mode, DataContext, compatibility |
| `dotnet-conventions.md` | Code style, nullable policy, EF Core conventions |
| `testing.md` | Test projects, mock patterns, JS test conventions |
| `tools-commands.md` | Build, test, pack, vulnerability scan, pre-PR checklist |
| `dependency-management.md` | Upgrade policy, .NET version strategy, package versioning |
| `known-quirks.md` | TestFrameworkContext, Serilog, EF Core API changes, reflection pitfalls |

Always consult the relevant rule file before making changes in that area.
