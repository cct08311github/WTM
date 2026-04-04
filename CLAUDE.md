# CLAUDE.md

This file provides guidance to Claude Code when working with this repository.
All detailed rules live in `.claude/rules/` — this file is the entry point.

## Project Mission

Personal fork of WalkingTec MVVM Framework (WTM), taken over 2026-03.
Goal: **stable, modernized, actively-evolved** .NET rapid-development framework.
Current phase: **Takeover & Revival** — stabilize, modernize, improve quality, then add features.

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

WTMContext (10.1.0) was refactored from a God Object into 8 focused services:
`IWtmContextAccessor`, `IWtmContextFactory`, `ILogService`, `IDataContextFactory`,
`IEncryptionService`, `ITokenService`, `IQAContext`. Register via `services.AddWtmContext(config)`.

Startup: `services.AddWtmContext(config)` → `app.UseWtmContext()` → `app.UseWtmStaticFiles()`.

## Security Summary

- Passwords: PBKDF2 via `PasswordHashHelper` (auto-migrates legacy MD5)
- JWT: access + refresh token rotation; `jti` claim prevents replay
- Analysis Mode: all fields whitelist-validated before entering Expression Trees

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
| `architecture.md` | Full architecture, WTMContext services, Analysis Mode, DataContext, compatibility |
| `dotnet-conventions.md` | Code style, nullable policy, EF Core conventions |
| `testing.md` | Test projects, mock patterns, JS test conventions |
| `tools-commands.md` | Build, test, pack, vulnerability scan, pre-PR checklist |
| `dependency-management.md` | Upgrade policy, .NET version strategy, package versioning |
| `known-quirks.md` | TestFrameworkContext, Serilog, EF Core API changes, reflection pitfalls |

Always consult the relevant rule file before making changes in that area.
