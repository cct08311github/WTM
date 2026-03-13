# WTM Architecture

## Source Projects

| Project | Role |
|---------|------|
| `src/WalkingTec.Mvvm.Core` | Core framework: VMs, DataContext, Models, Analysis engine |
| `src/WalkingTec.Mvvm.Mvc` | Controllers, TagHelpers, startup extensions, JS assets |
| `src/WalkingTec.Mvvm.TagHelpers.LayUI` | LayUI-specific TagHelpers (Grid, Form, Dialog, etc.) |

## ViewModel Rules

- All VMs extend `BaseVM` which holds `WTMContext Wtm`
- Four VM types: `BaseCRUDVM<T>`, `BasePagedListVM<T,S>`, `BaseImportVM<T>`, `BaseBatchVM<T>`
- Never bypass the VM layer to access DataContext directly from controllers

## Startup Wiring

`FrameworkServiceExtension.cs` in Mvc:
- `services.AddWtmContext(config)` — registers `WTMContext`, `AnalysisVmRegistry`, RBAC, DB connections
- `app.UseWtmContext()` — middleware that injects `WTMContext` into each request
- `app.UseWtmStaticFiles()` — serves embedded resources under `/_js/`

## Controllers

- App controllers extend `BaseController` (MVC) or `BaseApiController` (API), both expose `WTMContext Wtm`
- Framework controllers prefixed with `_` (e.g., `_AnalysisController`, `_FrameworkController`)

## DataContext

`DataContext` is WTM's EF Core wrapper supporting MSSQL, MySQL, PostgreSQL, SQLite, Oracle. Multi-tenancy via global query filters on `ITenant` — `IgnoreQueryFilters()` requires a comment explaining why.

## Analysis Mode (`src/WalkingTec.Mvvm.Core/Analysis/`)

Attribute-driven ad-hoc analytics added to any ListVM:

1. **Attributes**: `[Dimension]`, `[Measure]`, `[EnableAnalysis]` on model properties / ListVM class
2. **`AnalysisVmRegistry`** — startup scan for `[EnableAnalysis]` ListVMs; whitelist for `_AnalysisController`
3. **`AnalysisFieldScanner`** — reflects model type to produce `AnalysisFieldMeta` list
4. **`AnalysisQueryEngine`** — validates fields, applies `FilterCondition` Expression Trees, `Take(50_000)`, groups in-process, truncates at 10,000 rows
5. **`_AnalysisController`** — `GET /_analysis/meta`, `POST /_analysis/query`, `POST /_analysis/export?format=xlsx|csv`
6. **`framework_analysis.js`** — EmbeddedResource; frontend toggle/query/export UI with ECharts

Key pitfall: `ExecuteDynamic` calls `Execute<TModel>` via `method.Invoke` — inner exceptions arrive as `TargetInvocationException` and must be unwrapped.

## Security

- Passwords: PBKDF2 via `PasswordHashHelper` (auto-migrates legacy MD5)
- JWT: access + refresh token rotation; `jti` claim prevents replay
- Analysis Mode: all fields whitelist-validated before entering Expression Trees

## Compatibility

- **Default stance: avoid breaking changes**
- Prefer additive (opt-in) over breaking
- If unavoidable: document in `CHANGELOG.md`, provide migration path, bump minor version
- Deprecate before removing
- Never silently change default behaviour
