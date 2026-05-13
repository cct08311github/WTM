# CLAUDE.md

This file provides guidance to Claude Code when working with this repository.
All detailed rules live in `.claude/rules/` — this file is the entry point.

## Project Mission

Personal fork of WalkingTec MVVM Framework (WTM), taken over 2026-03.
Goal: **stable, modernized, actively-evolved** .NET rapid-development framework.
Current phase: **Feature growth on solid ground** — security audit cleared (10.2.0), Clean Architecture (10.1.0), and 10.4.0/10.5.0 added 20+ opt-in middleware + BI extensions; now iterating on observability, BI, and ETL.
Active branch: `dotnet10`. Origin: Gitea (`mac-mini.tailde842d.ts.net/chiu0831/WTM.git`) — sole authoritative remote. NuGet publishes go to Gitea's NuGet registry (`/api/packages/chiu0831/nuget`). GitHub is no longer used (mirror removed 2026-05-13, Issue #1).

## Decision Priorities (in order)

1. **Compatibility** — avoid breaking existing users; deprecate before removing
2. **Security** — fix vulnerabilities immediately (P0), no shortcuts
3. **Quality** — nullable annotations, structured logging, test coverage
4. **Performance** — optimize only with evidence (profiling, benchmarks)

## Red Lines — Never Violate

- **Never bypass the VM layer** to access DataContext directly from controllers
- **Never silently change default behaviour** — additive (opt-in) over breaking
- **Never use `IgnoreQueryFilters()`** without a comment explaining why
- **Never commit `#nullable disable`** in new code
- **Never use double-`!` null-forgiving chains** — split into separate checks
- Breaking changes require: `CHANGELOG.md` entry, migration path, minor version bump

## Architecture at a Glance

| Project | Role |
|---------|------|
| `WalkingTec.Mvvm.Core` | Core: VMs, DataContext, Models, Analysis engine, WTMContext |
| `WalkingTec.Mvvm.Mvc` | Controllers, startup extensions, JS assets, API |
| `WalkingTec.Mvvm.TagHelpers.LayUI` | LayUI TagHelpers (Grid, Form, Dialog, etc.) |
| `WalkingTec.Mvvm.Etl` | ETL module |

Four VM types: `BaseCRUDVM<T>`, `BasePagedListVM<T,S>`, `BaseImportVM<T>`, `BaseBatchVM<T>` — all extend `BaseVM`.

WTMContext (10.1.0) was refactored from a God Object into focused `IWtm*Service` interfaces
(`IWtmDataContextFactory`, `IWtmLogService`, `ITokenService`, `IWtmAuthService`, `IWtmTenantService`,
`IWtmVmFactory`, `IWtmUserCacheService`, `IWtmFileHandler`, etc.). Register via `services.AddWtmContext(config)`.
Full list and roles → `.claude/rules/architecture.md`.

Startup: `services.AddWtmContext(config)` → `app.UseWtmContext()` → `app.UseWtmStaticFiles()`.

## Security Summary

Core safeguards (full detail in `.claude/rules/architecture.md` § Security):

- Passwords: PBKDF2 with legacy MD5 auto-migration
- JWT: access + refresh rotation, `jti` replay guard, `_remotetoken` signature-validated
- Analysis Mode: field whitelist before Expression Tree
- File upload: path-traversal guard
- `UpdateModelProperty` / `CreateVM(string)`: blocklist + type validation

## Quick Commands

```bash
dotnet build WalkingTec.Mvvm.sln -c Release
dotnet test WalkingTec.Mvvm.sln -c Release --verbosity normal
```

Full command reference → `.claude/rules/tools-commands.md`

## Key Files

| File | Purpose |
|------|---------|
| `version.props` | Single source of framework version |
| `common.props` | Centralized NuGet package versions |
| `CHANGELOG.md` | Version history (update when releasing) |
| `docs/analysis-mode.md` | Analysis Mode manual |
| `docs/lookup-cache.md` | Lookup Cache manual |

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
