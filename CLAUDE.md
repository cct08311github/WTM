# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

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

Version is defined in `version.props` (`VersionPrefix`). All packages share `common.props`.

## Architecture Overview

WTM is an ASP.NET Core 8 rapid-development framework with four core ViewModel types and a built-in code generator.

### Source projects

| Project | Role |
|---------|------|
| `src/WalkingTec.Mvvm.Core` | Core framework: VMs, DataContext, Models, Analysis engine |
| `src/WalkingTec.Mvvm.Mvc` | Controllers, TagHelpers, startup extensions, JS assets |
| `src/WalkingTec.Mvvm.TagHelpers.LayUI` | LayUI-specific TagHelpers (Grid, Form, Dialog, etc.) |

### Four ViewModel types (all in Core)

- **`BaseCRUDVM<T>`** — Add/Edit/Delete for a single entity
- **`BasePagedListVM<TModel, TSearcher>`** — Paginated list with search, export, Analysis Mode
- **`BaseImportVM<T>`** / `BaseTemplateVM<T>` — Excel import
- **`BaseBatchVM<T>`** — Batch operations across multiple records

All VMs extend `BaseVM`, which holds a `WTMContext Wtm` reference (the framework's DI-aware context wrapper).

### Startup wiring

`FrameworkServiceExtension.cs` in Mvc provides the extension methods apps call:
- `services.AddWtmContext(config)` — registers `WTMContext`, `AnalysisVmRegistry`, RBAC, DB connections
- `app.UseWtmContext()` — middleware that injects `WTMContext` into each request
- `app.UseWtmStaticFiles()` — serves embedded resources under `/_js/` (includes `framework_analysis.js`, `framework_layui.js`)

### Analysis Mode (`src/WalkingTec.Mvvm.Core/Analysis/`)

Attribute-driven ad-hoc analytics added to any ListVM:

1. **Attributes**: `[Dimension]`, `[Measure]`, `[EnableAnalysis]` on model properties / ListVM class
2. **`AnalysisVmRegistry`** — built at startup by scanning all assemblies for `[EnableAnalysis]` ListVMs; provides the whitelist for `_AnalysisController`
3. **`AnalysisFieldScanner`** — reflects a model type to produce `AnalysisFieldMeta` list
4. **`AnalysisQueryEngine`** — validates fields against whitelist, applies `FilterCondition` Expression Trees, materialises `Take(50_000)`, groups in-process, truncates at 10,000 result rows
5. **`_AnalysisController`** (`src/WalkingTec.Mvvm.Mvc/`) — three endpoints: `GET /_analysis/meta`, `POST /_analysis/query`, `POST /_analysis/export?format=xlsx|csv`
6. **`framework_analysis.js`** — EmbeddedResource; frontend toggle/query/export UI with ECharts auto-chart selection

Key reflection pitfall: `ExecuteDynamic` calls `Execute<TModel>` via `method.Invoke`, so inner exceptions arrive as `TargetInvocationException` and must be unwrapped before the controller's `catch(InvalidOperationException)` can convert them to 400.

### Controllers

All app controllers extend `BaseController` (MVC) or `BaseApiController` (API), both of which expose `WTMContext Wtm`. Framework controllers (`_FrameworkController`, `_AnalysisController`, `_CodeGenController`, etc.) are prefixed with `_` and live in `WalkingTec.Mvvm.Mvc`.

### DataContext

`DataContext` is WTM's EF Core wrapper supporting MSSQL, MySQL, PostgreSQL, SQLite, Oracle. Multi-tenancy is handled via EF global query filters on entities implementing `ITenant` — use `IgnoreQueryFilters()` where cross-tenant reads are intentional.

### Security

- Passwords: PBKDF2 (`PasswordHashHelper`), auto-migrates legacy MD5 on first login
- JWT: access + refresh token rotation; `jti` claim prevents identical tokens in the same second
- Analysis Mode field access: all dimension/measure/filter fields are whitelist-validated before entering Expression Trees

## Test Projects

| Project | Framework | What it covers |
|---------|-----------|---------------|
| `test/WalkingTec.Mvvm.Core.Test` | MSTest | Core VM behaviour, Analysis engine, Analysis controller |
| `test/WalkingTec.Mvvm.Admin.Test` | MSTest | Admin-layer controllers |
| `test/WalkingTec.Mvvm.Js.Tests` | Jest (Node) | `framework_analysis.js` pure functions + DOM behaviour |
| `test/WalkingTec.Mvvm.Test.Mock` | (helper) | `MockWtmContext.CreateWtmContext()`, `MockController.CreateController<T>()` |

### Testing patterns

**Controller tests** — `_AnalysisController` has no parameterless constructor, so use direct instantiation:
```csharp
var controller = new _AnalysisController(registry);
controller.Wtm = MockWtmContext.CreateWtmContext();
```

**Data injection without DB** — override `GetSearchQuery()` in a test ListVM to return a static in-memory collection; reset the static field in `[TestInitialize]`.

**VM tests** — `MockWtmContext.CreateWtmContext(dataContext)` creates a full `WTMContext`. For tests requiring relational queries, use SQLite shared in-memory (`DataSource=name?mode=memory&cache=shared`) and keep a `SqliteConnection` open for the test lifetime.

**JS tests** — each DOM test must use `makeEnv()` to create a fresh `vm.createContext`, because `_state` is module-level in the IIFE. Tests that share a context will have state leakage.

## Nullable Strategy

`WalkingTec.Mvvm.Core` has `<Nullable>enable</Nullable>`. Legacy files (168 of them) carry `#nullable disable` at the top. New files should be fully nullable-annotated; do not add `#nullable disable` to new code.

## Docs

- `docs/analysis-mode.md` — developer manual for Analysis Mode (attributes, API spec, security, limits)
- `CHANGELOG.md` — version history (update when releasing)
- `version.props` — single source of version number; bump `VersionPrefix` here
