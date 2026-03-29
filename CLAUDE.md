# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Mission

Personal fork of WalkingTec MVVM Framework (WTM), taken over 2026-03. Goal: **stable, modernized, actively-evolved** .NET rapid-development framework.

Current phase: **Takeover & Revival** — stabilize, modernize, improve quality, then add features.

## Decision Priorities (in order)

1. **Compatibility** — avoid breaking existing users; deprecate before removing
2. **Security** — fix vulnerabilities immediately (P0), no shortcuts
3. **Quality** — nullable annotations, structured logging, test coverage
4. **Performance** — optimize only with evidence (profiling, benchmarks)

## Build & Test Commands

```bash
# Build entire solution
dotnet build WalkingTec.Mvvm.sln -c Release

# Run all .NET tests
dotnet test WalkingTec.Mvvm.sln -c Release --verbosity normal

# Run a single .NET test class
dotnet test test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj \
  --filter "FullyQualifiedName~AnalysisControllerTests" -c Release

# Run a single .NET test method
dotnet test test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj \
  --filter "Name=Query_happy_path_returns_200_with_aggregated_rows" -c Release

# Run JS tests
cd test/WalkingTec.Mvvm.Js.Tests && npm ci && npm test

# Pack NuGet packages (CI uses /p:Version=$PKG_VERSION)
dotnet pack src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj -c Release -o nupkgs
dotnet pack src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj -c Release -o nupkgs
dotnet pack src/WalkingTec.Mvvm.TagHelpers.LayUI/WalkingTec.Mvvm.TagHelpers.LayUI.csproj -c Release -o nupkgs

# Vulnerability scan
dotnet list WalkingTec.Mvvm.sln package --vulnerable --include-transitive
```

## Architecture

### Source Projects (src/)

| Project | Role |
|---------|------|
| `WalkingTec.Mvvm.Core` | Core: VMs, DataContext, Models, Analysis engine, WTMContext |
| `WalkingTec.Mvvm.Mvc` | Controllers, startup extensions, JS assets, API |
| `WalkingTec.Mvvm.TagHelpers.LayUI` | LayUI TagHelpers (Grid, Form, Dialog, etc.) |
| `WalkingTec.Mvvm.Etl` | ETL module |

### Test Projects (test/)

| Project | Framework | Covers |
|---------|-----------|--------|
| `WalkingTec.Mvvm.Core.Test` | MSTest | Core VM, Analysis engine, Analysis controller |
| `WalkingTec.Mvvm.Admin.Test` | MSTest | Admin-layer controllers |
| `WalkingTec.Mvvm.Integration.Test` | MSTest | DB-agnostic integration tests |
| `WalkingTec.Mvvm.Js.Tests` | Jest | `framework_analysis.js` pure functions + DOM |
| `WalkingTec.Mvvm.Test.Mock` | (helper) | `MockWtmContext.CreateWtmContext()` |

### ViewModel System

Four VM types — all extend `BaseVM` with `WTMContext Wtm`:

- **`BaseCRUDVM<T>`** — Create/Read/Update/Delete
- **`BasePagedListVM<T,S>`** — List with pagination, sorting, export
- **`BaseImportVM<T>`** — Excel import with validation
- **`BaseBatchVM<T>`** — Bulk operations

### WTMContext (10.1.0 Clean Architecture)

WTMContext was a "God Object" — in 10.1.0 it was refactored into 8 focused services:

- `IWtmContextAccessor` / `WtmContextAccessor` — request-scoped context access
- `IWtmContextFactory` / `WtmContextFactory` — creates contexts for background jobs
- `ILogService` — structured logging
- `IDataContextFactory` — creates EF Core DbContexts per tenant
- `IEncryptionService` / `EncryptionService` — AES-256 encryption
- `ITokenService` / `TokenService` — JWT + refresh token with rotation
- `IQAContext` / `QAContext` — quality assurance context (test support)

Register via `services.AddWtmContext(config)` in `FrameworkServiceExtension`.

### Startup Wiring

- `services.AddWtmContext(config)` — registers WTMContext, AnalysisVmRegistry, RBAC, DB connections
- `app.UseWtmContext()` — middleware that injects WTMContext into each request
- `app.UseWtmStaticFiles()` — serves embedded JS under `/_js/`

### Controllers

- App controllers extend `BaseController` (MVC) or `BaseApiController` (API)
- Framework controllers prefixed with `_` (e.g., `_AnalysisController`, `_FrameworkController`)

### Analysis Mode (`src/WalkingTec.Mvvm.Core/Analysis/`)

Attribute-driven ad-hoc analytics:

1. `[Dimension]`, `[Measure]`, `[EnableAnalysis]` attributes on model properties / ListVM class
2. `AnalysisVmRegistry` — startup scan for `[EnableAnalysis]` ListVMs; whitelist for `_AnalysisController`
3. `AnalysisFieldScanner` — reflects model type to produce `AnalysisFieldMeta` list
4. `AnalysisQueryEngine` — validates fields, applies `FilterCondition` Expression Trees, groups in-process
5. `_AnalysisController` endpoints: `GET /_analysis/meta`, `POST /_analysis/query`, `POST /_analysis/export?format=xlsx|csv`
6. `framework_analysis.js` — EmbeddedResource; frontend toggle/query/export UI with ECharts

**Key pitfall**: `ExecuteDynamic` calls `Execute<TModel>` via `method.Invoke` — inner exceptions arrive as `TargetInvocationException` and must be unwrapped.

### DataContext

EF Core wrapper supporting MSSQL, MySQL, PostgreSQL, SQLite, Oracle. Multi-tenancy via global query filters on `ITenant`. `IgnoreQueryFilters()` requires a comment explaining why.

## Security

- Passwords: PBKDF2 via `PasswordHashHelper` (auto-migrates legacy MD5)
- JWT: access + refresh token rotation; `jti` claim prevents replay
- Analysis Mode: all fields whitelist-validated before entering Expression Trees

## Key Files

| File | Purpose |
|------|---------|
| `version.props` | Single source of framework version |
| `common.props` | Centralized NuGet package versions (12 MSBuild variables) |
| `CHANGELOG.md` | Version history (update when releasing) |
| `docs/analysis-mode.md` | Analysis Mode manual |
| `docs/lookup-cache.md` | Lookup Cache manual |

## Detailed Rules

All detailed conventions, architecture, testing patterns, and dependency policies are in `.claude/rules/`:

- `architecture.md` — full architecture, WTMContext refactor, Analysis Mode, security, compatibility
- `dotnet-conventions.md` — code style, nullable, EF Core, testing patterns, build & release
- `dependency-management.md` — upgrade policy, .NET version strategy, package versioning

## Known Quirks

- **TestFrameworkContext / TenantFrameworkContext**: must override `OnModelCreating` and NOT call `base.OnModelCreating()`, otherwise `Utils.GetAllModels()` assembly scanning causes `MajorId` column name conflicts in SQLite tests
- **Serilog.Sinks.InMemory**: v1.0.0 only supports `netstandard2.0` — use custom minimal sink in tests
- **EF Core 10.0.4 API**: `HasMaxIdentifierLength(30)` requires cast to `IConventionModelBuilder`; `Database.IsMySql()` removed → use `DC!.DBType == DBTypeEnum.MySql`
- **Integration test DbContext**: `EmptyContext` doesn't apply `HasQueryFilter`; use `TenantFrameworkContext` for multi-tenant tests
